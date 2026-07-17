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
            }
            catch { /* identity stamping is best-effort — a record without it is handled as legacy by IsAlive */ }
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
                    var expected = string.IsNullOrEmpty(info.ExePath) ? null : Path.GetFileNameWithoutExtension(info.ExePath);
                    return expected == null || string.Equals(p.ProcessName, expected, StringComparison.OrdinalIgnoreCase);
                }
                // LEGACY record (pre identity fields): fall back to the old pid + name check — an upgraded engine
                // must not orphan a RUNNING old-version owner just because its engine.json predates the new fields.
                return string.Equals(p.ProcessName, Process.GetCurrentProcess().ProcessName, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }   // pid gone / access denied / raced exit ⇒ not alive
        }

        /// <summary>The executable identity of this launch shape: dotnet for a development DLL, or the
        /// self-contained apphost for a packaged engine.</summary>
        public static string CurrentExecutablePath()
        {
            try { return Process.GetCurrentProcess().MainModule?.FileName ?? Environment.ProcessPath; }
            catch { return Environment.ProcessPath; }
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
