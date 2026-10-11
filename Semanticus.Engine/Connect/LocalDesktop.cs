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
    /// The identity (the join's ruling, 30 Sep: a Desktop model is known by its window title or by nothing):
    ///   1. The window-title STEM — same-stem files collide (a copy of Sales.pbix in another folder is the same
    ///      identity), so the primer layer stamps provenance beside the artifact and fails closed on a proven mismatch.
    ///   2. No readable title → null, and the caller keys on the endpoint|database coordinates (works, not
    ///      restart-stable); the decision switches treat it as a Desktop model known by nothing.
    /// NEVER the .pbix path on PBIDesktop's command line. A process's command line never changes, so it names the file
    /// Desktop was STARTED with, not the one the window shows after the person opens another file in it; trusting it
    /// let a window showing west\Sales.pbix (or "Sales - EU" under a title Semanticus cannot read) be kept under the
    /// file that started it (the switch-scope verifier, round 6, F2b). No test on the title can tell two same-named
    /// files apart, so there is no safe way to keep the path.
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

        /// <summary>The captured identity: the window title's stem. There is deliberately no path here (see the class comment).
        /// Never returned with a null stem (null instead).</summary>
        internal sealed class DesktopIdentity
        {
            public string Stem;
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
                var workspace = FindWorkspaceDir(port, DefaultWorkspaceRoots());
                if (workspace == null) return null;   // no Desktop workspace owns this port ⇒ real SSAS / unknown
                return Resolve(workspace, SnapshotProcesses());
            }
            catch { return null; }
        }

        /// <summary>Whether some Desktop workspace owns the data source's port. A real Analysis Services on this machine has
        /// no such workspace, but a workspace that cannot be found does NOT prove a real server: see <see cref="PlaceOf"/>.
        /// <paramref name="workspacesRoot"/> is a test seam; null reads every Desktop folder on this machine.</summary>
        internal static bool IsDesktopInstance(string dataSource, string workspacesRoot = null)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(dataSource)) return false;
                var port = ParsePort(dataSource);
                return port > 0 && FindWorkspaceDir(port, workspacesRoot != null ? new[] { workspacesRoot } : DefaultWorkspaceRoots()) != null;
            }
            catch { return false; }
        }

        internal enum Placement
        {
            /// <summary>A Desktop workspace owns the port: Power BI Desktop, whichever edition.</summary>
            Desktop,
            /// <summary>Proved a real Analysis Services: the process list is readable, holds a server, and no Desktop owns one.</summary>
            Server,
            /// <summary>No workspace names the port and nothing proves a real server (an edition or folder this build does not
            /// know, or no process list). Treated as a Desktop model known by nothing, so it reads every Desktop off and can
            /// never be taken for Analysis Services.</summary>
            Unplaced,
        }

        /// <summary>Where a local data source lives. Windows, WMI: <paramref name="processes"/> is the seam for the process
        /// list, <paramref name="roots"/> the seam for the workspace folders (null reads this machine's).</summary>
        internal static Placement PlaceOf(string dataSource, IReadOnlyList<string> roots = null, Func<IReadOnlyList<ProcInfo>> processes = null)
        {
            try
            {
                var port = string.IsNullOrWhiteSpace(dataSource) ? 0 : ParsePort(dataSource);
                // Desktop always listens on a port, so a data source without one (a named instance) is a server.
                if (port <= 0) return Placement.Server;
                if (FindWorkspaceDir(port, roots ?? DefaultWorkspaceRoots()) != null) return Placement.Desktop;
                var list = (processes ?? (() => SnapshotProcesses()))();
                var servers = list?.Where(p => IsName(p, "msmdsrv")).ToList();
                if (servers == null || servers.Count == 0) return Placement.Unplaced;
                // Only the msmdsrv listening on THIS port says what this port is. A Desktop msmdsrv names its workspace, whose
                // port file names its port, so a Desktop running on another port says nothing about this one. A Desktop
                // msmdsrv whose port cannot be read might be this one: unplaced.
                var desktopOwned = servers.Where(p => list.Any(q => q.Pid == p.ParentPid && IsDesktopProcess(q))).ToList();
                foreach (var d in desktopOwned)
                {
                    var theirs = PortOfDesktopServer(d.CommandLine);
                    if (theirs <= 0 || theirs == port) return Placement.Unplaced;
                }
                return servers.Count > desktopOwned.Count ? Placement.Server : Placement.Unplaced;
            }
            catch { return Placement.Unplaced; }
        }

        /// <summary>The port a Desktop msmdsrv listens on: its command line names the workspace Data folder (-s), which holds
        /// msmdsrv.port.txt. -1 when the command line or the port file cannot be read.</summary>
        private static int PortOfDesktopServer(string commandLine)
        {
            try
            {
                if (string.IsNullOrEmpty(commandLine)) return -1;
                var m = System.Text.RegularExpressions.Regex.Match(commandLine, "-s\\s+(?:\"([^\"]+)\"|(\\S+))");
                if (!m.Success) return -1;
                var dir = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
                if (!OperatingSystem.IsWindows()) dir = dir.Replace('\\', Path.DirectorySeparatorChar);
                var file = Path.Combine(dir, "msmdsrv.port.txt");
                if (!File.Exists(file)) return -1;
                var digits = new string(File.ReadAllText(file, Encoding.Unicode).Where(char.IsDigit).ToArray());
                return int.TryParse(digits, out var port) ? port : -1;
            }
            catch { return -1; }
        }

        /// <summary>The folders Power BI Desktop keeps its Analysis Services workspaces in, one per edition. Each workspace
        /// holds Data\msmdsrv.port.txt with the port it listens on. Sources, checked 30 Sep 2026 against two public
        /// detectors (cmettler/fabricator-extension PowerBiDesktop.cs, muxiaoqi007/pbichat PowerBiInstanceDetector.cs):
        /// the installer's %LOCALAPPDATA%\Microsoft\Power BI Desktop; the Microsoft Store app's "Power BI Desktop Store App"
        /// under %USERPROFILE% and under %LOCALAPPDATA%; Report Server's "Power BI Desktop SSRS" under %LOCALAPPDATA%; and the
        /// Store package's own folders under %LOCALAPPDATA%\Packages\Microsoft.MicrosoftPowerBIDesktop*. Microsoft does not
        /// publish these folders, so a build that meets another layout falls to <see cref="Placement.Unplaced"/>.</summary>
        internal static IReadOnlyList<string> WorkspaceRoots(string localAppData, string userProfile)
        {
            const string leaf = "AnalysisServicesWorkspaces";
            var roots = new List<string>();
            if (!string.IsNullOrEmpty(localAppData))
                roots.Add(Path.Combine(localAppData, "Microsoft", "Power BI Desktop", leaf));
            if (!string.IsNullOrEmpty(userProfile))
                roots.Add(Path.Combine(userProfile, "Microsoft", "Power BI Desktop Store App", leaf));
            if (!string.IsNullOrEmpty(localAppData))
            {
                roots.Add(Path.Combine(localAppData, "Microsoft", "Power BI Desktop Store App", leaf));
                roots.Add(Path.Combine(localAppData, "Microsoft", "Power BI Desktop SSRS", leaf));
                try
                {
                    var packages = Path.Combine(localAppData, "Packages");
                    if (Directory.Exists(packages))
                        foreach (var package in Directory.GetDirectories(packages, "Microsoft.MicrosoftPowerBIDesktop*"))
                        {
                            roots.Add(Path.Combine(package, "LocalCache", "Local", "Microsoft", "Power BI Desktop", leaf));
                            roots.Add(Path.Combine(package, "LocalState", leaf));
                        }
                }
                catch { /* an unreadable Packages folder only means fewer roots */ }
            }
            return roots;
        }

        private static IReadOnlyList<string> DefaultWorkspaceRoots() => WorkspaceRoots(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

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
            return parent != null && IsDesktopProcess(parent) ? IdentityOf(parent) : null;
        }

        /// <summary>The Desktop executables: the installer's PBIDesktop, the Store app's PBIDesktopStore and Report Server's PBIDesktopRS.</summary>
        private static bool IsDesktopProcess(ProcInfo p) => IsName(p, "PBIDesktop") || IsName(p, "PBIDesktopStore") || IsName(p, "PBIDesktopRS");

        private static bool IsName(ProcInfo p, string name)
            => p.Name != null && (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)
                               || string.Equals(p.Name, name + ".exe", StringComparison.OrdinalIgnoreCase));

        /// <summary>The window title alone, never the command line (see the class comment). A title Semanticus cannot read, or
        /// none, is known by nothing.</summary>
        private static DesktopIdentity IdentityOf(ProcInfo desktop)
        {
            var stem = CleanTitle(desktop.Title);
            return stem == null ? null : new DesktopIdentity { Stem = stem };
        }

        /// <summary>The .pbix path from a PBIDesktop command line: a quoted "…\name.pbix" argument (paths with
        /// spaces — the shell always quotes), else a lone unquoted token ending in .pbix. Null when absent
        /// (Desktop-opened-via-dialog has a bare exe command line). FULLY QUALIFIED paths only: a relative
        /// argument would resolve against OUR working directory, not the model's location.
        /// NOT AN IDENTITY and nothing in the engine calls it (the join's ruling, 30 Sep): it names the file Desktop was
        /// started with, which the window may no longer show. Kept with its test only as a parser.</summary>
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
        private static string FindWorkspaceDir(int port, IEnumerable<string> roots)
        {
            foreach (var baseDir in roots)
            {
                if (!Directory.Exists(baseDir)) continue;
                foreach (var ws in Directory.GetDirectories(baseDir))
                {
                    var portFile = Path.Combine(ws, "Data", "msmdsrv.port.txt");
                    if (!File.Exists(portFile)) continue;
                    try
                    {
                        var digits = new string(File.ReadAllText(portFile, Encoding.Unicode).Where(char.IsDigit).ToArray());
                        if (int.TryParse(digits, out var p) && p == port) return ws;
                    }
                    catch { /* unreadable workspace: skip */ }
                }
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
                    "SELECT ProcessId, ParentProcessId, Name, CommandLine FROM Win32_Process WHERE Name='msmdsrv.exe' OR Name='PBIDesktop.exe' OR Name='PBIDesktopStore.exe' OR Name='PBIDesktopRS.exe'");
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
