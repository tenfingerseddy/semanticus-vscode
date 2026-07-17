using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace Semanticus.Engine
{
    /// <summary>
    /// Resolves the restart-STABLE identity of the Power BI Desktop file behind a local AS instance — for a local
    /// Desktop model the endpoint port and the database GUID both rotate every restart, so the primer keys on this
    /// instead. Captured ONCE at open_local/connect_local and stamped on the session/connection; never re-derived.
    ///
    /// The identity ladder (strongest first):
    ///   1. The FULL .pbix PATH from the owning PBIDesktop process command line — present when the file was opened
    ///      by double-click / shell, ABSENT when opened via Desktop's own dialog or recent list (verified
    ///      empirically 2026-07-17: two live Desktops, one of each shape). Distinct across same-named files.
    ///   2. The window-title STEM — same-stem files collide, so the primer layer stamps provenance beside the
    ///      artifact and fails closed on a proven mismatch.
    ///   3. Neither → null, and the caller keys on the endpoint|database coordinates (works, not restart-stable).
    ///
    /// How ownership is PROVEN: port → the Desktop workspace dir whose msmdsrv.port.txt matches (a Desktop-only
    /// artifact, so a real SSAS on localhost never resolves an identity and keeps its stable coordinate key) →
    /// the msmdsrv process whose command line names that workspace (WMI) → its PARENT PBIDesktop process. The
    /// TE2/DAX Studio parity technique, written managed (System.Management, no P/Invoke). PROOF ONLY — there is
    /// deliberately NO "sole Desktop on the machine" fallback (a stale port file can alias a reused port onto an
    /// unrelated Desktop) and no titles-only degraded snapshot: WMI unavailable, owner missing, or any ambiguity
    /// returns null and the caller keys on the coordinates — degraded, never a guessed name.
    /// </summary>
    internal static class LocalDesktop
    {
        /// <summary>One process, as the decision core sees it — the injectable seam (the WMI/Process plumbing is
        /// integration-only; the DECISIONS are unit-tested through this).</summary>
        internal sealed class ProcInfo
        {
            public int Pid;
            public int ParentPid;
            public string Name;          // "PBIDesktop" / "msmdsrv" (with or without .exe — normalized in matching)
            public string Title;         // main window title; null/blank when unreadable (minimized, access denied)
            public string CommandLine;   // full command line; null when unreadable (no WMI)
        }

        /// <summary>The captured identity: either part may be null; both-null is never returned (null instead).</summary>
        internal sealed class DesktopIdentity
        {
            public string PbixPath;      // ladder rung 1 — the strongest identity
            public string Stem;          // ladder rung 2 — the window-title stem
        }

        /// <summary>The Desktop identity for a local data source ("localhost:51234"), or null when it is not a
        /// Power BI Desktop instance / nothing capturable. Windows-only (Desktop is).</summary>
        public static DesktopIdentity TryGetIdentity(string dataSource)
        {
            try
            {
                if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(dataSource)) return null;
                var port = ParsePort(dataSource);
                if (port <= 0) return null;
                var workspace = FindWorkspaceDir(port);
                if (workspace == null) return null;   // no Desktop workspace owns this port ⇒ real SSAS / unknown
                return Resolve(workspace, SnapshotProcesses());
            }
            catch { return null; }
        }

        // ---- the PURE decision core (unit-tested through the ProcInfo seam) --------------------------------

        /// <summary>Choose the owning Desktop and extract its identity — PROOF ONLY, no fallback: the msmdsrv
        /// whose command line names the workspace, then its parent, which must be PBIDesktop (devenv/services is
        /// NOT the Desktop lane and yields null). No proven owner ⇒ null, always: a "sole Desktop on the machine"
        /// heuristic is not ownership proof (a stale port file can alias a reused port onto an unrelated Desktop),
        /// and the degraded coordinate key is the honest answer when proof is unavailable — including when WMI
        /// itself is (the snapshot is then empty).</summary>
        internal static DesktopIdentity Resolve(string workspaceDir, IReadOnlyList<ProcInfo> processes)
        {
            if (processes == null || processes.Count == 0 || string.IsNullOrEmpty(workspaceDir)) return null;
            // Windows path semantics made EXPLICIT (the inputs are always Windows paths, whatever OS runs this
            // code): fold separators to backslash on both sides and compare ordinal-ignore-case — never rely on
            // the host OS's own path comparison rules.
            var ws = workspaceDir.Replace('/', '\\');
            var owner = processes.FirstOrDefault(p => IsName(p, "msmdsrv")
                && p.CommandLine != null && p.CommandLine.Replace('/', '\\').IndexOf(ws, StringComparison.OrdinalIgnoreCase) >= 0);
            if (owner == null) return null;
            var parent = processes.FirstOrDefault(p => p.Pid == owner.ParentPid);
            return parent != null && IsName(parent, "PBIDesktop") ? IdentityOf(parent) : null;
        }

        private static bool IsName(ProcInfo p, string name)
            => p.Name != null && (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)
                               || string.Equals(p.Name, name + ".exe", StringComparison.OrdinalIgnoreCase));

        private static DesktopIdentity IdentityOf(ProcInfo desktop)
        {
            var path = ExtractPbixPath(desktop.CommandLine);
            var stem = CleanTitle(desktop.Title);
            return path == null && stem == null ? null : new DesktopIdentity { PbixPath = path, Stem = stem };
        }

        /// <summary>The .pbix path from a PBIDesktop command line: a quoted "…\name.pbix" argument (paths with
        /// spaces — the shell always quotes), else a lone unquoted token ending in .pbix. Null when absent
        /// (Desktop-opened-via-dialog has a bare exe command line). FULLY QUALIFIED paths only: a relative
        /// argument would resolve against OUR working directory, not the model's location — never an identity.</summary>
        internal static string ExtractPbixPath(string commandLine)
        {
            if (string.IsNullOrWhiteSpace(commandLine)) return null;
            var m = System.Text.RegularExpressions.Regex.Match(commandLine, "\"([^\"]+\\.pbix)\"",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (m.Success) return QualifiedOrNull(m.Groups[1].Value);
            var token = commandLine.Split(' ').FirstOrDefault(t => t.EndsWith(".pbix", StringComparison.OrdinalIgnoreCase));
            return QualifiedOrNull(token?.Trim('"'));
        }

        // The input is ALWAYS a Windows command line (Power BI Desktop only exists on Windows), so "fully
        // qualified" is checked against the WINDOWS shape explicitly — drive-rooted (C:\ or C:/) or UNC
        // (\\server\share) — never via Path.IsPathFullyQualified, whose answer changes with the OS running the
        // parser ("C:\x\y.pbix" is not qualified on Linux) while the input never does.
        private static string QualifiedOrNull(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            var p = path.Trim();
            bool driveRooted = p.Length >= 3 && char.IsAsciiLetter(p[0]) && p[1] == ':' && (p[2] == '\\' || p[2] == '/');
            bool unc = p.Length >= 5 && p[0] == '\\' && p[1] == '\\' && p[2] != '\\';   // \\server\share...
            return driveRooted || unc ? p : null;
        }

        /// <summary>Title → stem policy (fail-closed): no " - " → the title IS the stem; the exact known English
        /// suffixes (" - Power BI Desktop" / " - Power BI Designer") are stripped once and the remainder kept
        /// AS-IS (a file named "Sales - EU" keeps its dash); any OTHER " - &lt;tail&gt;" is treated as UNCAPTURABLE
        /// (null) — a localized or future edition suffix must never leak into identity as if it were part of the
        /// file name. The cost: a dash-named file under a titles-only capture is uncapturable (degraded key);
        /// the path rung of the ladder usually supersedes.</summary>
        internal static string CleanTitle(string title)
        {
            if (string.IsNullOrWhiteSpace(title)) return null;
            var t = title.TrimEnd();   // suffix check BEFORE a full trim: " - Power BI Desktop" must read as suffix-only (empty stem)
            foreach (var suffix in new[] { " - Power BI Desktop", " - Power BI Designer" })
                if (t.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    var stem = t.Substring(0, t.Length - suffix.Length).Trim();
                    return stem.Length == 0 ? null : stem;
                }
            t = t.Trim();
            if (t.Contains(" - ")) return null;   // unknown suffix shape: fail closed, never guess
            return t;
        }

        internal static int ParsePort(string dataSource)
        {
            var i = dataSource.LastIndexOf(':');
            return i > 0 && int.TryParse(dataSource.Substring(i + 1).Trim(), out var port) ? port : 0;
        }

        // ---- Windows plumbing (integration-only; not unit-tested) ------------------------------------------

        // The workspace dir whose msmdsrv.port.txt equals the port — the same enumeration LocalDiscovery.List
        // does, in reverse (port → workspace). Only Power BI Desktop creates these: the Desktop-vs-SSAS
        // discriminator is HOW the instance exists, never loopback string-matching.
        private static string FindWorkspaceDir(int port)
        {
            var baseDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft", "Power BI Desktop", "AnalysisServicesWorkspaces");
            if (!Directory.Exists(baseDir)) return null;
            foreach (var ws in Directory.GetDirectories(baseDir))
            {
                var portFile = Path.Combine(ws, "Data", "msmdsrv.port.txt");
                if (!File.Exists(portFile)) continue;
                try
                {
                    var digits = new string(File.ReadAllText(portFile, Encoding.Unicode).Where(char.IsDigit).ToArray());
                    if (int.TryParse(digits, out var p) && p == port) return ws;
                }
                catch { /* unreadable workspace — skip */ }
            }
            return null;
        }

        // WMI gives the two things Process alone cannot: another process's COMMAND LINE and PARENT pid — both
        // required for ownership PROOF. No WMI ⇒ an empty snapshot ⇒ Resolve returns null ⇒ the degraded
        // coordinate key. Deliberately NO Process-API fallback: a titles-only snapshot cannot prove ownership,
        // and a marginal convenience is not worth a wrong-name stamping path.
        private static List<ProcInfo> SnapshotProcesses()
        {
            var list = new List<ProcInfo>();
            try
            {
                using var query = new System.Management.ManagementObjectSearcher(
                    "SELECT ProcessId, ParentProcessId, Name, CommandLine FROM Win32_Process WHERE Name='msmdsrv.exe' OR Name='PBIDesktop.exe'");
                foreach (System.Management.ManagementObject mo in query.Get())
                {
                    var pid = (int)(uint)mo["ProcessId"];
                    list.Add(new ProcInfo
                    {
                        Pid = pid,
                        ParentPid = (int)(uint)mo["ParentProcessId"],
                        Name = mo["Name"] as string,
                        CommandLine = mo["CommandLine"] as string,
                        Title = TitleOf(pid),
                    });
                }
            }
            catch { list.Clear(); }
            return list;
        }

        private static string TitleOf(int pid)
        {
            try { using var p = Process.GetProcessById(pid); var t = p.MainWindowTitle; return string.IsNullOrWhiteSpace(t) ? null : t; }
            catch { return null; }
        }
    }
}
