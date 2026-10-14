using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;

namespace Semanticus.Engine
{
    public sealed class FileRecoveryInfo
    {
        public string Source { get; set; }
        public string CapturedUtc { get; set; }
    }

    internal sealed class FileRecoveryRecord
    {
        public string Source { get; set; }
        public string CapturedUtc { get; set; }
        public string BimJson { get; set; }
    }

    public static partial class RestorePointStore
    {
        // One current copy in the existing restore store. Published history never enumerates this filename.
        // While an offer is open, the session's new edits go to a second file so neither set is lost. On the
        // next open the newer set becomes the offer and the older one is parked, kept until a save or discard.
        private static string FileKey(string source)
        {
            var path = PathWitness.Real(Path.GetFullPath(source));
            return OperatingSystem.IsWindows() ? path.ToUpperInvariant() : path;
        }
        internal static string UnsavedPath(string source) => Path.Combine(TargetDir("file", FileKey(source)), "unsaved.json");

        private static string LatestPath(string source) => Path.Combine(TargetDir("file", FileKey(source)), "unsaved-latest.json");

        internal static FileRecoveryRecord ReadUnsaved(string source)
        {
            if (string.IsNullOrEmpty(source)) return null;
            lock (Gate)
            {
                var path = UnsavedPath(source);
                var latest = LatestPath(source);
                if (File.Exists(latest) && !IsReparsePoint(latest))
                {
                    if (File.Exists(path)) WithRecoveryRetry(path, () => File.Move(path, Path.Combine(Path.GetDirectoryName(path), "unsaved-older-" + Guid.NewGuid().ToString("N") + ".json")));
                    WithRecoveryRetry(latest, () => File.Move(latest, path));
                }
                return ReadRecord(path, source);
            }
        }

        private static FileRecoveryRecord ReadRecord(string path, string source)
        {
            if (!File.Exists(path)) return null;
            if (IsReparsePoint(Root()) || IsReparsePoint(Path.GetDirectoryName(path)) || IsReparsePoint(path))
                throw new IOException("The recovery copy is a link and could not be read.");
            var copy = JsonSerializer.Deserialize<FileRecoveryRecord>(WithRecoveryRetry(path, () => ReadShared(path)), JsonOpts);
            if (copy == null || string.IsNullOrEmpty(copy.BimJson) || FileKey(copy.Source) != FileKey(source))
                throw new IOException("The recovery copy does not name this model.");
            return copy;
        }

        // The newest parked copy, or null. Parked copies are the older sets a newer offer displaced; the person is shown
        // one once the set in front of it is settled, so it is never lost to a file hunt.
        private static string NewestOlderPath(string source)
        {
            var dir = Path.GetDirectoryName(UnsavedPath(source));
            if (!Directory.Exists(dir)) return null;
            return Directory.GetFiles(dir, "unsaved-older-*.json").OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
        }

        /// <summary>The copy <see cref="PromoteOlder"/> would offer next, without moving anything.</summary>
        internal static FileRecoveryRecord PeekOlder(string source)
        {
            if (string.IsNullOrEmpty(source)) return null;
            lock (Gate) { var older = NewestOlderPath(source); return older == null ? null : ReadRecord(older, source); }
        }

        /// <summary>Makes the newest parked copy the current offer. The caller has settled the set that held that place.</summary>
        internal static FileRecoveryRecord PromoteOlder(string source)
        {
            if (string.IsNullOrEmpty(source)) return null;
            lock (Gate)
            {
                var older = NewestOlderPath(source);
                if (older == null) return null;
                var copy = ReadRecord(older, source);
                var current = UnsavedPath(source);
                WithRecoveryRetry(current, () => File.Move(older, current, overwrite: true));
                return copy;
            }
        }

        internal static void WriteUnsaved(string source, string bimJson, bool latest = false)
        {
            lock (Gate)
            {
                var root = Path.GetFullPath(Root());
                Directory.CreateDirectory(root);
                if (IsReparsePoint(root)) throw new IOException("The recovery store is a link.");
                var path = latest ? LatestPath(source) : UnsavedPath(source);
                var dir = Path.GetDirectoryName(path);
                if (!IsWithin(root, dir) || (Directory.Exists(dir) && IsReparsePoint(dir)))
                    throw new IOException("The recovery folder could not be used.");
                Directory.CreateDirectory(dir);
                var temp = Path.Combine(dir, ".unsaved-" + Guid.NewGuid().ToString("N") + ".tmp");
                try
                {
                    var copy = new FileRecoveryRecord { Source = Path.GetFullPath(source), CapturedUtc = DateTimeOffset.UtcNow.ToString("O"), BimJson = bimJson };
                    WithRecoveryRetry(path, () =>
                    {
                        using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                        {
                            JsonSerializer.Serialize(file, copy, JsonOpts);
                            file.Flush(flushToDisk: true);
                        }
                        File.Move(temp, path, overwrite: true);
                    });
                    // A current copy written over an applied offer supersedes the second file.
                    if (!latest) WithRecoveryRetry(path, () => File.Delete(LatestPath(source)));
                }
                finally
                {
                    // The temp never outlives the call: a failed write must not leave a stray .unsaved-*.tmp behind.
                    try { WithRecoveryRetry(temp, () => { if (File.Exists(temp)) File.Delete(temp); }); } catch { }
                }
            }
        }

        /// <summary>Test seam: runs before every try at a recovery file and may throw, to stand in for the sharing violation another process
        /// (a scanner, an indexer) causes on Windows. It can also block, to hold a write in flight with its temp file open.</summary>
        internal static Action<string> RecoveryIoHookForTest;

        /// <summary>Test seam: the waits between tries. Null keeps the real ones.</summary>
        internal static int[] RecoveryRetryDelaysForTest;

        private static readonly int[] RecoveryRetryDelaysMs = { 20, 50, 100, 200, 400, 600 };

        /// <summary>Waits until every recovery write in this process that is under way has finished. Writes take the same gate, so after this
        /// returns nothing is writing into a folder the caller has already pointed the store away from.</summary>
        internal static void WaitForWriters() { lock (Gate) { } }

        private static string ReadShared(string file)
        {
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(fs);
            return reader.ReadToEnd();
        }

        private static T WithRecoveryRetry<T>(string file, Func<T> act)
        {
            var delays = RecoveryRetryDelaysForTest ?? RecoveryRetryDelaysMs;
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    RecoveryIoHookForTest?.Invoke(file);
                    return act();
                }
                catch (Exception ex) when (attempt < delays.Length
                    && (ex is IOException || ex is UnauthorizedAccessException)
                    && !(ex is DirectoryNotFoundException || ex is FileNotFoundException || ex is PathTooLongException))
                {
                    Thread.Sleep(delays[attempt]);
                }
            }
        }

        private static void WithRecoveryRetry(string file, Action act) => WithRecoveryRetry<bool>(file, () => { act(); return true; });

        // latestOnly removes just the second file (the session's new edits are gone). Otherwise every kept copy goes,
        // except that keepOlder leaves the parked ones for PromoteOlder.
        internal static void DeleteUnsaved(string source, bool latestOnly = false, bool keepOlder = false)
        {
            if (string.IsNullOrEmpty(source)) return;
            lock (Gate)
            {
                try
                {
                    var latest = LatestPath(source);
                    WithRecoveryRetry(latest, () => File.Delete(latest));
                    if (latestOnly) return;
                    var current = UnsavedPath(source);
                    WithRecoveryRetry(current, () => File.Delete(current));
                    if (!keepOlder) foreach (var older in Directory.GetFiles(Path.GetDirectoryName(current), "unsaved-older-*.json")) WithRecoveryRetry(older, () => File.Delete(older));
                }
                catch (DirectoryNotFoundException) { }   // a save or discard can precede the first debounced copy
            }
        }
    }
}
