using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;

namespace Semanticus.Engine
{
    public sealed class EngineInfo
    {
        public string PipeName { get; set; }
        public int Pid { get; set; }
        public string StartedUtc { get; set; }
        public string Workspace { get; set; }
        // Identity fields (additive; PascalCase to match this file's existing Newtonsoft-default keys). The
        // (pid, process-start-time) pair is the standard PID-reuse killer — a recycled pid never reproduces the
        // original start instant. Stamped by WriteInfo; a record WITHOUT them is a legacy engine's (see IsAlive).
        public string ProcessStartUtc { get; set; }   // Process.StartTime as UTC ISO round-trip
        public string ExePath { get; set; }           // the owner's executable (dotnet.exe in dev, the apphost when self-contained)
        // Absolute address of the owner's pipe. On Windows this is the named-pipe path for PipeName. On Unix, .NET
        // places the socket at Path.Combine(GetTempPath(), "CoreFxPipe_" + PipeName), and GetTempPath follows THIS
        // process's TMPDIR. The agent process often has a different TMPDIR than the UI owner, so the short PipeName
        // alone is not enough to join; clients must open this stamped path (D-118).
        public string PipePath { get; set; }
        // The process this engine dies with: the extension host that spawned it, or whoever last adopted it
        // (ParentLifetime). Additive and NULLABLE on purpose, and the null case is load-bearing: a record
        // without it came from a build that predates the parent watch, and "no recorded parent" must NEVER
        // be read as "no owner". A reader that stops an engine on that guess kills a live window's unsaved
        // session, which is exactly what the sweep in Semanticus.VSCode/src/extension.ts is now careful not
        // to do when VS Code updates while an older window is still open.
        public int? ParentPid { get; set; }
        /// <summary>"assistant" when the assistant's connection started this owner itself (VS Code was closed).
        /// Null for an owner a VS Code window started. An updated window stops an assistant-started owner of
        /// another version instead of reading its live parent (the connection) as another open window.</summary>
        public string StartedBy { get; set; }
        public const string StartedByAssistant = "assistant";
        /// <summary>Which BUILD this owner is (<see cref="BuildIdentity"/>), F-241. The executable path cannot tell
        /// an in-place reinstall from the build it replaced, so a window compares this instead before it adopts.
        /// Null on a record from an engine older than this field, which a window reads as "not my build".</summary>
        public string BuildId { get; set; }
        /// <summary>Where this owner keeps per-user state (<see cref="SemanticusHome.Root"/>) and its sign-ins
        /// (<see cref="SemanticusHome.SignInDir"/>), stamped by WriteInfo. An engine given its own state folder joins
        /// only an owner whose folders are the same ones (27 Sep): joining any other would write that run's
        /// connections and sign-ins into someone else's home. Null on a record older than these fields.</summary>
        public string Home { get; set; }
        public string SignInDir { get; set; }
    }

    /// <summary>What the assistant's door may do with a live owner it found for its folder (F-241).</summary>
    public enum McpJoin
    {
        /// <summary>This install and this build: join it.</summary>
        Join,
        /// <summary>A record older than executable provenance. Joined with a warning, as before.</summary>
        Legacy,
        /// <summary>Another install folder: the extension updated. The "was updated" reconnect.</summary>
        DifferentInstall,
        /// <summary>This path, another build (an in-place reinstall), or an owner too old to say which build it is.
        /// Not joined and never stopped from this side: Studio replaces it, or offers Save when it holds work.</summary>
        DifferentBuild,
    }

    /// <summary>
    /// Single-owner-with-rendezvous broker. The owner takes an exclusive lock on
    /// <c>.semanticus/engine.lock</c> and publishes <c>.semanticus/engine.json</c> (pipe name + pid)
    /// so any other process (a second VS Code window, or the MCP proxy) can discover and attach to
    /// the live session. Stale info (dead pid) is ignored.
    /// </summary>
    public static class EngineBroker
    {
        public static string DirFor(string workspace) => Path.Combine(workspace, ".semanticus");

        public static string PipeNameFor(string workspace)
        {
            using var sha = SHA1.Create();
            // Case-fold the path ONLY on Windows (its filesystem is case-insensitive, so /work/A and /work/a are the
            // SAME workspace and must share a pipe). On a case-sensitive filesystem they are DIFFERENT workspaces —
            // lowercasing before the hash would collide them onto one pipe. Windows pipe names are unchanged by this.
            var key = OperatingSystem.IsWindows() ? workspace.ToLowerInvariant() : workspace;
            var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(key));
            var hex = BitConverter.ToString(hash, 0, 6).Replace("-", string.Empty).ToLowerInvariant();
            return "semanticus-" + hex;
        }

        /// <summary>The absolute pipe address .NET would use in THIS process. Stamp the owner's value at
        /// <see cref="WriteInfo"/> so an attaching agent does not recompute it under a different temp directory.</summary>
        public static string PipePathFor(string pipeName)
        {
            if (string.IsNullOrEmpty(pipeName)) return null;
            if (OperatingSystem.IsWindows()) return @"\\.\pipe\" + pipeName;
            return Path.Combine(Path.GetTempPath(), "CoreFxPipe_" + pipeName);
        }

        /// <summary>Stderr when the MCP door could not join or become owner. Never tells the reader to delete
        /// the session lock: that lock is what keeps the open VS Code window on this folder.</summary>
        public static string McpAttachFailureMessage(TimeSpan deadline, bool ownerAlive)
        {
            var seconds = deadline.TotalSeconds.ToString("0");
            if (ownerAlive)
                return "[mcp] FATAL: Semanticus is already open for this folder, but the AI Assistant could not join that session within "
                    + seconds
                    + "s. Keep the VS Code window open, run Semanticus: Connect AI Assistant in VS Code, then reconnect with /mcp.";
            return "[mcp] FATAL: Semanticus could not start for this folder within "
                + seconds
                + "s. Wait a moment, then reconnect with /mcp. If it keeps happening, close other VS Code windows for this folder, run Semanticus: Connect AI Assistant in VS Code, then reconnect with /mcp.";
        }

        /// <summary>Stderr when the owner for this folder is a different installed build (usually: the extension
        /// updated while an older window, or an older connection, is still in use).</summary>
        public const string McpDifferentBuildMessage =
            "[mcp] FATAL: Semanticus for this folder is running a different version than this connection. Run Semanticus: Connect AI Assistant in VS Code, then reconnect with /mcp.";

        /// <summary>Stderr when this engine was given its own state folder and the owner already running for the folder
        /// keeps its state somewhere else. There is one owner per folder, so this run cannot start its own here.</summary>
        public const string McpDifferentStateFolderMessage =
            "[mcp] FATAL: Semanticus is already running for this folder, and it keeps its saved connections and sign-ins in a different state folder than the one this engine was given (--state-dir or SEMANTICUS_HOME). Not joining it, so nothing from this run is written there. Start this run on a folder of its own with --workspace, or stop the other engine first.";

        /// <summary>Stderr when this engine chose no state folder (the person's own assistant) and the owner already
        /// running for the folder keeps its state in another one: an isolated run started it with a folder of its own.
        /// Names both folders, and the one way to join that folder on purpose.</summary>
        public static string McpOwnerUsesAnotherStateFolderMessage(string ownerHome, string mine) =>
            $"[mcp] FATAL: Semanticus is already running for this folder, but another run started it with a state folder of its own ({ownerHome}), not your usual one ({mine}). "
            + "Not joining it, so none of your saved connections, sign-ins or settings are written there, and your own agent policy stays the one in force. "
            + $"Stop that engine, then reconnect with /mcp. If you meant to use that folder, start this engine with {SemanticusHome.Option} {ownerHome}.";

        /// <summary>True when joining the owner <paramref name="info"/> describes would put this process's writes in a
        /// home it did not choose. Such an owner is never joined: every write the join makes (saved connections, sign-ins)
        /// lands in the owner's home, the owner's agent policy and licence govern the calls, and nothing tells the caller.
        /// <para>A process that chose its home (SEMANTICUS_HOME, --state-dir, or SEMANTICUS_SIGNIN_DIR) joins only an owner
        /// whose home AND sign-in store are the ones it was given; a record too old to say where it keeps state cannot
        /// prove that, so it is not joined either.</para>
        /// <para>A process that chose nothing is the person's own engine, and its home is the person's usual one. It
        /// joins only an owner whose stamped home is that same folder (27 Sep, round 3: an isolated run's owner took the
        /// person's saved connections into a scratch folder and swapped the person's locked policy for the default). An
        /// unstamped record predates --state-dir, so it can only be the person's own and is joined as before. The sign-in
        /// store is not compared here: its default follows XDG_DATA_HOME, which two of the person's own processes can
        /// see differently, while an owner started with a state folder of its own always has a different home.</para></summary>
        public static bool StateFolderMismatch(EngineInfo info) =>
            info != null && StateFolderMismatch(info, ChoseStateFolder(), SemanticusHome.Root(), SemanticusHome.SignInDir);

        /// <summary>The rule itself, for this process's choice, home and sign-in store (a test cannot unset the test
        /// run's own home without moving every test running beside it).</summary>
        internal static bool StateFolderMismatch(EngineInfo info, bool chose, string home, Func<string> signInDir)
        {
            if (info == null) return false;
            if (chose)
                return !SemanticusHome.SameFolder(info.Home, home) || !SemanticusHome.SameFolder(info.SignInDir, signInDir());
            return !string.IsNullOrWhiteSpace(info.Home) && !SemanticusHome.SameFolder(info.Home, home);
        }

        /// <summary>The line the MCP door prints, and why it stops, when <see cref="StateFolderMismatch"/> refused
        /// <paramref name="info"/>'s owner.</summary>
        public static string StateFolderMismatchMessage(EngineInfo info) =>
            ChoseStateFolder()
                ? McpDifferentStateFolderMessage
                : McpOwnerUsesAnotherStateFolderMessage(info?.Home, Path.GetFullPath(SemanticusHome.Root()));

        private static bool ChoseStateFolder() =>
            SemanticusHome.IsMoved || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(SemanticusHome.SignInVariable));

        /// <summary>Returns the held lock stream (keep open for the engine's lifetime) or null if another owner exists.</summary>
        public static FileStream TryAcquireOwnerLock(string workspace)
        {
            var dir = DirFor(workspace);
            Directory.CreateDirectory(dir);
            var lockPath = Path.Combine(dir, "engine.lock");
            try { return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) { return null; }
        }

        public static void WriteInfo(string workspace, EngineInfo info)
        {
            var dir = DirFor(workspace);
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "engine.json");
            // Stamp the OWNER's process identity at the write chokepoint (both call sites publish self: Pid =
            // Environment.ProcessId), so IsAlive can verify (pid, start-time) instead of pid-existence alone.
            try
            {
                // IsNullOrEmpty, not `??=`: an EMPTY-string field is not null, so `??=` would leave it empty and
                // IsAlive's empty-string check would route the record to the LEGACY pid+name fallback — losing the
                // PID-reuse killer. Stamp an empty field too. Deliberately NOT fatal and NO version/format marker
                // (coordinator's call): reading our own process's StartTime does not realistically fail, so refusing
                // to publish here would brick startup over a hardening nicety; a record that somehow lacks identity
                // is handled as legacy by IsAlive.
                if (string.IsNullOrEmpty(info.ProcessStartUtc))
                    info.ProcessStartUtc = Process.GetCurrentProcess().StartTime.ToUniversalTime().ToString("o");
                if (string.IsNullOrEmpty(info.ExePath))
                    info.ExePath = CurrentExecutablePath();
                if (string.IsNullOrEmpty(info.BuildId))
                    info.BuildId = BuildIdentity.Current;
                if (string.IsNullOrEmpty(info.Home))
                    info.Home = Path.GetFullPath(SemanticusHome.Root());
                if (string.IsNullOrEmpty(info.SignInDir))
                    info.SignInDir = Path.GetFullPath(SemanticusHome.SignInDir());
            }
            catch { /* identity stamping is best-effort — a record without it is handled as legacy by IsAlive */ }
            // Stamp the owner's absolute pipe address under THIS process's temp directory. An attaching agent must
            // not recompute it: its temp directory is often not the owner's (D-118).
            if (string.IsNullOrEmpty(info.PipePath) && !string.IsNullOrEmpty(info.PipeName))
                info.PipePath = PipePathFor(info.PipeName);
            // Atomic publish: write a temp sibling then move-replace, so a concurrent reader (a second VS Code
            // window or the MCP proxy) never sees a HALF-written engine.json as "no engine" and races us for
            // ownership. Same temp+move discipline every sidecar owes.
            var tmp = path + ".tmp-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            File.WriteAllText(tmp, JsonConvert.SerializeObject(info, Formatting.Indented));
            try { File.Move(tmp, path, overwrite: true); }
            catch { try { File.Delete(tmp); } catch { } throw; }
        }

        public static EngineInfo ReadInfo(string workspace)
        {
            var p = Path.Combine(DirFor(workspace), "engine.json");
            if (!File.Exists(p)) return null;
            try { return JsonConvert.DeserializeObject<EngineInfo>(File.ReadAllText(p)); }
            catch { return null; }
        }

        /// <summary>Remove this workspace's published record, but ONLY when it names <paramref name="pid"/>.
        /// An engine that is leaving takes its own advertisement with it; a record naming anyone else belongs
        /// to another owner, and deleting that would hide a live session from the next window. Returns whether
        /// a record was actually removed.</summary>
        /// <summary>Re-stamp the watched parent on this workspace's record, but ONLY when it names
        /// <paramref name="pid"/>. Called after an adoption, so a reader can tell "this engine belongs to a
        /// window that is still open" from "this engine's window is gone". Returns whether it was written.
        /// </summary>
        public static bool UpdateParentPid(string workspace, int pid, int? parentPid)
        {
            try
            {
                var info = ReadInfo(workspace);
                if (info == null || info.Pid != pid) return false;
                info.ParentPid = parentPid;
                WriteInfo(workspace, info);
                return true;
            }
            catch { return false; }   // a record we could not re-stamp reads as an older one, which is safe
        }

        public static bool ClearInfo(string workspace, int pid)
        {
            try
            {
                var info = ReadInfo(workspace);
                if (info == null || info.Pid != pid) return false;
                File.Delete(Path.Combine(DirFor(workspace), "engine.json"));
                return true;
            }
            catch { return false; }   // a record we could not remove is stale, not fatal: IsAlive still rejects it
        }

        public static bool IsAlive(EngineInfo info)
        {
            if (info == null) return false;
            try
            {
                var p = Process.GetProcessById(info.Pid);
                if (p.HasExited) return false;
                // PID EXISTENCE is not liveness: the OS recycles PIDs, so a recycled pid can belong to an UNRELATED
                // process — and a NAME check alone is unreliable in both launch shapes (dev is dotnet-hosted, so
                // ProcessName is "dotnet" for every .NET process; self-contained copies can drift). The recorded
                // (pid, process-START-TIME) pair is the standard PID-reuse killer: a recycled pid never reproduces
                // the original start instant.
                if (!string.IsNullOrEmpty(info.ProcessStartUtc))
                {
                    if (!DateTime.TryParse(info.ProcessStartUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out var recorded))
                        return false;   // a new-format record with an unparseable identity is untrustworthy
                    var actual = p.StartTime.ToUniversalTime();
                    // The 3-second tolerance direction is DELIBERATE — do not tighten. Too-tight risks a false
                    // NOT-alive, which lets a second engine seize ownership from a LIVE owner (split-brain, data
                    // corruption); a false-alive merely delays a fresh start. The window absorbs clock-representation
                    // drift between the recorded ISO string and the OS start instant.
                    if (Math.Abs((actual - recorded.ToUniversalTime()).TotalSeconds) > 3) return false;
                    // Supplementary (never the sole signal): the recorded executable's name, when known.
                    return ProcessNameMatches(info.ExePath, p.ProcessName);
                }
                // LEGACY record (pre identity fields): fall back to the old pid + name check — an upgraded engine
                // must not orphan a RUNNING old-version owner just because its engine.json predates the new fields.
                return string.Equals(p.ProcessName, Process.GetCurrentProcess().ProcessName, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }   // pid gone / access denied / raced exit ⇒ not alive
        }

        /// <summary>True when <paramref name="processName"/> (what <see cref="Process.ProcessName"/> reported)
        /// names the executable at <paramref name="exePath"/>. This check is SUPPLEMENTARY evidence, so it is
        /// permissive exactly where the platform mangles names. Windows reports the file name without its
        /// extension, so the extension is dropped there. Unix reports the file name as-is, and the Unix apphost
        /// has NO extension, so <c>Path.GetFileNameWithoutExtension</c> would cut a dotted name like
        /// "Semanticus.Engine" down to "Semanticus" and never match — which is what made a LIVE owner read as dead
        /// and stopped the agent door joining it on Linux (D-118). The Linux kernel also records a process name
        /// truncated to 15 characters, which .NET reports when the command line is unreadable; a name explained by
        /// that truncation is the same process, not a mismatch.</summary>
        public static bool ProcessNameMatches(string exePath, string processName)
        {
            if (string.IsNullOrEmpty(exePath) || string.IsNullOrEmpty(processName)) return true;   // no evidence to contradict
            string fileName;
            try { fileName = Path.GetFileName(exePath); } catch { return true; }   // an unreadable path proves nothing
            if (string.IsNullOrEmpty(fileName)) return true;

            var expected = OperatingSystem.IsWindows() ? Path.GetFileNameWithoutExtension(fileName) : fileName;
            if (string.Equals(expected, processName, StringComparison.OrdinalIgnoreCase)) return true;

            // Accept ONLY a difference that the kernel's 15-character record explains. Anything else is a real
            // mismatch — a recycled pid carrying an unrelated process is exactly what this guard exists to catch.
            const int LinuxNameMax = 15;
            var shorter = expected.Length <= processName.Length ? expected : processName;
            var longer = expected.Length <= processName.Length ? processName : expected;
            return shorter.Length == LinuxNameMax && longer.StartsWith(shorter, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>The executable identity of this launch shape: dotnet for a development DLL, or the
        /// self-contained apphost for a packaged engine.</summary>
        public static string CurrentExecutablePath()
        {
            try { return Process.GetCurrentProcess().MainModule?.FileName ?? Environment.ProcessPath; }
            catch { return Environment.ProcessPath; }
        }

        /// <summary>The one join rule for the assistant's door, shared by the first join (Program.Mcp) and every
        /// rejoin (RemoteEngine). The path decides the install; the build identity decides the build, because an
        /// in-place reinstall keeps the path (F-241). A process that cannot read its own build cannot judge anyone
        /// else's, so it joins as every build before this one did.</summary>
        public static McpJoin McpJoinVerdict(EngineInfo info) => McpJoinVerdict(info, BuildIdentity.Current);

        public static McpJoin McpJoinVerdict(EngineInfo info, string expectedBuild)
        {
            if (!HasExecutableProvenance(info)) return McpJoin.Legacy;
            if (!ExecutableMatches(info)) return McpJoin.DifferentInstall;
            if (string.IsNullOrEmpty(expectedBuild)) return McpJoin.Join;
            return string.Equals(info.BuildId, expectedBuild, StringComparison.OrdinalIgnoreCase) ? McpJoin.Join : McpJoin.DifferentBuild;
        }

        public static bool HasExecutableProvenance(EngineInfo info)
            => !string.IsNullOrWhiteSpace(info?.ExePath);

        /// <summary>Compares a provenance-bearing workspace owner with the expected launch executable.
        /// Callers must distinguish a legacy record with no executable provenance from a genuine mismatch.</summary>
        public static bool ExecutableMatches(EngineInfo info, string expectedPath = null)
        {
            if (info == null || string.IsNullOrWhiteSpace(info.ExePath)) return false;
            try
            {
                var expected = expectedPath ?? CurrentExecutablePath();
                if (string.IsNullOrWhiteSpace(expected)) return false;
                var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                return string.Equals(Path.GetFullPath(info.ExePath), Path.GetFullPath(expected), comparison);
            }
            catch { return false; }
        }
    }
}
