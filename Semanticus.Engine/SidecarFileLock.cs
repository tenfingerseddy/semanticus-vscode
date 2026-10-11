// A lock on one sidecar file that every engine process on this machine sees (Sol 6, slice 7, finding 4).
//
// WHY IT EXISTS. The owner lock is per workspace (EngineBroker), but the plan memory and the History live beside the
// MODEL, so two workspaces that open the same model run two engine processes writing the same two files. Both stores
// already read the whole file, change their part and replace it, under a C# lock. That lock is one process's own, so
// the second process read the file before the first wrote it and then wrote over it, losing the first one's records.
//
// HOW. An exclusive open (FileShare.None) of a lock file held for the whole read, change and write. Windows enforces
// that share mode across processes; .NET on Linux and macOS takes an flock on it, which every .NET process honours.
//
// WHERE THE LOCK FILE LIVES, AND WHY NOT BESIDE THE MODEL. Beside the model it would be one more file in the person's
// model folder and repository (plan-memory.json travels with the model; a lock file would show up as untracked). So it
// lives in the per-user lock folder (SemanticusHome.LockRoot), named by a hash of the sidecar's full path. That folder
// is NOT the Semanticus home, because a run given its own state folder (--state-dir) can open the person's real model
// and must still wait for the person's engine (27 Sep, round 3). The cost is that two processes only share the lock
// when they share that folder and spell the path the same way; two engines of one user on one machine do both. It is
// never deleted: deleting a lock file while another process waits on the old one would give the next process a new
// file and two holders at once.
//
// ENGINES OLDER THAN THE LOCK FOLDER took the same locks under <home>/locks (~/.semanticus/locks for the person). While
// one of them may still be running (an update with an older window open on another workspace of the same model), every
// engine takes that legacy lock under its own home as well, after the new one: one order everywhere, so two current
// engines cannot deadlock, and an older engine on the same home still waits for a current one. It is always this run's
// own home, so a run given a state folder never writes in the person's.
//
// A run given a state folder may open the person's real model while a released engine of the person's is running on
// ~/.semanticus/locks (Sol, 28 Sep). It takes that engine's lock READ-ONLY, last: it opens the lock file only if it
// already exists (a released engine creates it on its first write and never deletes it), and never creates, writes or
// deletes anything there. The limit: a released engine cannot see this run's lock or its live Plan claims, because
// showing them would mean writing in the person's home.

using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace Semanticus.Engine
{
    internal static class SidecarFileLock
    {
        /// <summary>How long a writer waits for another process's write before giving up. A write is milliseconds; this
        /// is only ever reached if the other process hangs while holding the file.</summary>
        internal static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);

        /// <summary>Test seam: a shorter wait for the calling thread only, so a test of the give-up message does not wait
        /// 15 seconds or shorten anyone else's wait. Null in production.</summary>
        [ThreadStatic] internal static TimeSpan? WaitForTests;

        // The lock files this process holds right now, by where they really are on disk (every link followed), with a
        // count. So one Acquire never takes the same file twice under two names, and a wait that runs out can say
        // truthfully whether this engine or another one is writing (round 4: an engine waited 15 s on itself and blamed
        // "another Semanticus window" that did not exist).
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> HeldHere =
            new System.Collections.Concurrent.ConcurrentDictionary<string, int>(StringComparer.Ordinal);

        /// <summary>Take the lock for <paramref name="file"/>, waiting for another process that holds it. Dispose to
        /// release. Throws an IOException with a sentence when the wait runs out.</summary>
        public static IDisposable Acquire(string file)
        {
            var until = DateTime.UtcNow + (WaitForTests ?? Wait);
            var held = new System.Collections.Generic.List<IDisposable>();
            var taken = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
            try
            {
                Take(held, taken, PathFor(file), file, until, readOnly: false);
                var legacy = LegacyPathFor(file);
                if (legacy != null) Take(held, taken, legacy, file, until, readOnly: false);
                var person = PersonLegacyPathFor(file);
                if (person != null && File.Exists(person)) Take(held, taken, person, file, until, readOnly: true);
            }
            catch { Release(held); throw; }
            return held.Count == 1 ? held[0] : new All(held);
        }

        // One lock file, unless this Acquire already holds the same file under another name.
        private static void Take(System.Collections.Generic.List<IDisposable> held, System.Collections.Generic.HashSet<string> taken,
                                 string path, string file, DateTime until, bool readOnly)
        {
            if (!readOnly) Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var key = SemanticusHome.ResolvedPath(path);
            if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()) key = key.ToUpperInvariant();   // one file, two spellings
            if (!taken.Add(key)) return;
            var stream = AcquireOne(path, key, file, until, readOnly);
            if (stream == null) return;
            HeldHere.AddOrUpdate(key, 1, (_, n) => n + 1);
            held.Add(new Held(stream, key));
        }

        // readOnly: the person's legacy lock, which this run may only open when it exists, never create or write.
        private static FileStream AcquireOne(string path, string key, string file, DateTime until, bool readOnly)
        {
            while (true)
            {
                try
                {
                    return readOnly
                        ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None, 1)
                        : new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1);
                }
                catch (FileNotFoundException) when (readOnly) { return null; }
                catch (DirectoryNotFoundException) when (readOnly) { return null; }
                catch (IOException) when (DateTime.UtcNow < until) { Thread.Sleep(10); }
                catch (UnauthorizedAccessException) when (DateTime.UtcNow < until) { Thread.Sleep(10); }
                catch (IOException ex)
                {
                    throw new IOException(HeldHere.ContainsKey(key)
                        ? "This Semanticus engine is still busy writing " + Path.GetFileName(file) + " for this model, so it could not start another write. Try again in a moment."
                        : "Another Semanticus engine on this computer (a VS Code window or an assistant) is still writing " + Path.GetFileName(file)
                            + " for this model. Try again in a moment.", ex);
                }
            }
        }

        private sealed class Held : IDisposable
        {
            private readonly FileStream _stream;
            private readonly string _key;
            private int _disposed;
            public Held(FileStream stream, string key) { _stream = stream; _key = key; }
            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
                try { _stream.Dispose(); }
                finally
                {
                    if (HeldHere.AddOrUpdate(_key, 0, (_, n) => n - 1) <= 0) HeldHere.TryRemove(new System.Collections.Generic.KeyValuePair<string, int>(_key, 0));
                }
            }
        }

        /// <summary>The lock file for one sidecar: the per-user lock folder and a hash of the full path. Case is folded
        /// on Windows, where two spellings name one file.</summary>
        internal static string PathFor(string file)
        {
            var full = Path.GetFullPath(file);
            if (OperatingSystem.IsWindows()) full = full.ToUpperInvariant();
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(full))).ToLowerInvariant().Substring(0, 32);
            return Path.Combine(SemanticusHome.LockRoot(), "sidecar-" + hash + ".lock");
        }

        /// <summary>The lock an engine older than the lock folder takes for the same sidecar, when this engine must take
        /// it too (<see cref="SemanticusHome.LegacyLockRoot"/>); null otherwise.</summary>
        internal static string LegacyPathFor(string file)
        {
            var root = SemanticusHome.LegacyLockRoot();
            if (root == null || SemanticusHome.SamePlace(root, SemanticusHome.LockRoot())) return null;
            return Path.Combine(root, Path.GetFileName(PathFor(file)));
        }

        /// <summary>The released engine's lock under the person's usual home, which a run given a state folder takes
        /// read-only (<see cref="SemanticusHome.PersonLegacyLockRoot"/>); null otherwise.</summary>
        internal static string PersonLegacyPathFor(string file)
        {
            var root = SemanticusHome.PersonLegacyLockRoot();
            return root == null ? null : Path.Combine(root, Path.GetFileName(PathFor(file)));
        }

        private static void Release(System.Collections.Generic.List<IDisposable> held)
        {
            for (var i = held.Count - 1; i >= 0; i--) try { held[i].Dispose(); } catch { }
        }

        private sealed class All : IDisposable
        {
            private readonly System.Collections.Generic.List<IDisposable> _held;
            public All(System.Collections.Generic.List<IDisposable> held) { _held = held; }
            public void Dispose() => Release(_held);
        }
    }

    /// <summary>
    /// Whether an engine run is still alive, told by the same kind of lock (Codex on pull request 393, finding 4).
    ///
    /// WHY IT EXISTS. An assistant's claim on a Plan task names the engine run that made it. A claim from a run that has
    /// ended must be released, because the assistant that held it is attached to nothing that can hear its reply. But a
    /// different run id alone does not prove that: two workspaces on one model run two live engines, and releasing the
    /// other one's claim would hand its task to a second assistant while the first is still working on it.
    ///
    /// HOW. A run that makes a claim holds an exclusive open of its own lock file, "run-" and its id, for as long as it
    /// lives, and never lets go on its own. The operating system drops that hold when the process ends, however it
    /// ends, so there is no heartbeat to miss and no clock to trust. Another engine asks by trying the same exclusive
    /// open: refused means the run is alive; granted, or no file, means it is gone. A granted probe deletes the file
    /// on close, and a run that ends cleanly deletes its own.
    ///
    /// THE HONEST LIMITS. It lives in the per-user lock folder (<see cref="SemanticusHome.LockRoot"/>, which a state
    /// folder never moves), so it answers for engines of one user on one machine, the same reach as
    /// <see cref="SidecarFileLock"/>. An engine on another machine sharing the model folder
    /// is seen as gone. A claim written before this record existed has no file and is seen as gone, which is the old
    /// behaviour for it. A probe that meets another probe mid-check is told "alive" and releases nothing that read:
    /// a wrong "alive" only keeps a task claimed until the next read, a wrong "gone" hands it out twice.
    /// </summary>
    internal static class EngineRunLiveness
    {
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, FileStream[]> Held =
            new System.Collections.Concurrent.ConcurrentDictionary<string, FileStream[]>(StringComparer.Ordinal);
        private static readonly object HoldGate = new object();

        /// <summary>Start holding this run's record, once. Best effort: a run whose record cannot be written is seen as
        /// gone by other engines, which is exactly how every run was seen before the record existed.</summary>
        public static void Hold(string runId)
        {
            if (string.IsNullOrEmpty(runId) || Held.ContainsKey(runId)) return;
            lock (HoldGate)
            {
                if (Held.ContainsKey(runId)) return;
                var streams = new System.Collections.Generic.List<FileStream>();
                try
                {
                    // The legacy record too, under this run's own home, so an older engine still reads this run as alive.
                    foreach (var path in PathsFor(runId))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                        streams.Add(new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose));
                    }
                    Held[runId] = streams.ToArray();
                }
                catch (Exception ex)
                {
                    foreach (var fs in streams) try { fs.Dispose(); } catch { }
                    try { Console.Error.WriteLine("[plan] could not record that this engine is running: " + ex.Message); } catch { }
                }
            }
        }

        /// <summary>Stop holding it: the run is ending. Its file goes with the handle.</summary>
        public static void Release(string runId)
        {
            if (!string.IsNullOrEmpty(runId) && Held.TryRemove(runId, out var streams))
                foreach (var fs in streams) try { fs.Dispose(); } catch { }
        }

        /// <summary>True while some engine process holds the run's record.</summary>
        public static bool IsAlive(string runId)
        {
            if (string.IsNullOrEmpty(runId)) return false;
            if (Held.ContainsKey(runId)) return true;   // this process's own run: no probe of its own lock
            if (PathsFor(runId).Any(Probe)) return true;    // an older engine holds only the legacy record
            // A released engine on the person's usual home, asked read-only: never created, never deleted.
            var person = SemanticusHome.PersonLegacyLockRoot();
            return person != null && ProbeReadOnly(Path.Combine(person, Path.GetFileName(PathFor(runId))));
        }

        private static bool ProbeReadOnly(string path)
        {
            try { using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None, 1)) { } return false; }
            catch (FileNotFoundException) { return false; }
            catch (DirectoryNotFoundException) { return false; }
            catch (IOException) { return true; }
            catch (UnauthorizedAccessException) { return true; }
        }

        private static bool Probe(string path)
        {
            try
            {
                using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose)) { }
                return false;                               // nobody held it: the run ended without deleting its file
            }
            catch (FileNotFoundException) { return false; }
            catch (DirectoryNotFoundException) { return false; }
            catch (IOException) { return true; }            // held: the run is alive
            catch (UnauthorizedAccessException) { return true; }
        }

        internal static string PathFor(string runId)
        {
            var safe = new string(runId.Where(ch => char.IsLetterOrDigit(ch) || ch == '-').ToArray());
            return Path.Combine(SemanticusHome.LockRoot(), "run-" + safe + ".lock");
        }

        /// <summary>This run's record, then the one an engine older than the lock folder reads, when this engine must
        /// keep that one too (see <see cref="SidecarFileLock"/>).</summary>
        internal static string[] PathsFor(string runId)
        {
            var path = PathFor(runId);
            var root = SemanticusHome.LegacyLockRoot();
            if (root == null || SemanticusHome.SamePlace(root, SemanticusHome.LockRoot())) return new[] { path };
            return new[] { path, Path.Combine(root, Path.GetFileName(path)) };
        }
    }
}
