// The switch-scope matrix: every kind of model as the one clicked and as a reader, through both doors, after a restart too.
// Built by the switch-scope verifier (round 6, VerifyR6MatrixTests.cs, a scratch file), kept at the join (part 2, item 5)
// under the join's ruling (the coordinator's brief, 30 Sep): Power BI Desktop's command-line path is NEVER a model's identity.
// A Desktop model is known by its window title or by nothing, so the identities are: a title (every Desktop file with that
// name, whatever folder it is in and whichever file started the window), or nothing (every Desktop model whose name
// Semanticus cannot read); a real server, a cloud, a saved or an unsaved model is its own.
//
// Rulings it holds (each harm or note flag fails the cell): an off reaches every model the clicker cannot be told apart
// from (OFFMISS); an on is kept only under the clicker's identity, changes no shared off and no other identity (ONREACH,
// ONOFF) and reaches every model of that identity (ONMISS); what a run reads survives a restart (RESTART); a newer off takes
// away only the ons of models that read it (REVOKE-WRONG); and the on's row says what really happened (OVERCLAIM, OWN-EMPTY,
// OWN-UNSAID, SILENT). An off that reaches more than it must (OFFOVER) is fail-safe and only printed.
//
// Kinds (each is a clicker and a reader): N installer Desktop known by nothing; T:Sales, T:Stock title only; P:east,
// P:west (two Sales.pbix, double-clicked), P:stock; S:P a Store-app Desktop (store\Budget.pbix); S:N a Store-app Desktop
// known by nothing; R:T a Report Server Desktop, title Budget; U a local instance no folder places and no process list
// proves (WMI unavailable); AS a real Analysis Services (its msmdsrv a service, no Desktop running); AS+D the same server
// database opened while a Power BI Desktop also runs; Cloud; Saved-root (saved in the workspace folder); Saved-away;
// Unsaved; AS+DS a real server beside a Store-app Desktop on another port; U+S a Desktop in a folder no build knows,
// placed by its own port while a server also runs; X:Stock a window east\Sales.pbix started now showing Stock; X:SalesEU
// that window showing "Sales - EU" under a title Semanticus cannot read; Xw:west that window showing west\Sales.pbix.
// Readers only: Pn:east (east\Sales.pbix with no title), AS2 (another database on the same server), Cloud2 (another dataset
// in the same workspace), Xs:east and Xl:east (the window east started, showing Sales, a readable title and one it
// cannot read). Every P, S:P and X kind is stamped through LocalDesktop.Resolve with the command line Desktop would show.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Semanticus.Decisions;
using Semanticus.Engine;
using Xunit;
using Xunit.Abstractions;

namespace Semanticus.Tests
{
    public abstract class DecisionSwitchScopeMatrixBase
    {
        protected readonly ITestOutputHelper Out;
        protected DecisionSwitchScopeMatrixBase(ITestOutputHelper output) { Out = output; }

        protected const string Point = DecisionRegistry.MeasureFolderAndFormat;
        protected static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> StampLog = new System.Collections.Concurrent.ConcurrentDictionary<string, string>();
        private const string CloudEndpoint = "powerbi://api.powerbi.com/v1.0/myorg/Contoso Live";

        public static readonly string[] Kinds =
            { "N", "T:Sales", "T:Stock", "P:east", "P:west", "P:stock", "S:P", "S:N", "R:T", "U", "AS", "AS+D", "Cloud", "Saved-root", "Saved-away", "Unsaved",
              "AS+DS", "U+S", "X:Stock", "X:SalesEU", "Xw:west" };
        protected static readonly string[] Readers = Kinds.Concat(new[] { "Pn:east", "AS2", "Cloud2", "Xs:east", "Xl:east" }).ToArray();

        protected sealed class World
        {
            internal DecisionFixture F;
            public string East, West, Stock, StoreBudget, Root, Away, Profile, LocalAppData, DesktopWs, StoreDesktopWs;
            public IReadOnlyList<string> Roots;
            public int Port = 53000;
            public string InstallerRoot => Path.Combine(LocalAppData, "Microsoft", "Power BI Desktop", "AnalysisServicesWorkspaces");
            public string StoreRoot => Path.Combine(Profile, "Microsoft", "Power BI Desktop Store App", "AnalysisServicesWorkspaces");
            public string RsRoot => Path.Combine(LocalAppData, "Microsoft", "Power BI Desktop SSRS", "AnalysisServicesWorkspaces");
        }

        protected static string DesktopStarts(string root, int port)
        {
            var ws = Path.Combine(root, "AnalysisServicesWorkspace_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(ws, "Data"));
            File.WriteAllText(Path.Combine(ws, "Data", "msmdsrv.port.txt"), port.ToString(), Encoding.Unicode);
            return ws;
        }

        private protected static async Task<World> NewWorldAsync(DecisionFixture f)
        {
            var w = new World
            {
                F = f,
                East = Path.Combine(f.Home, "east", "Sales.pbix"),
                West = Path.Combine(f.Home, "west", "Sales.pbix"),
                Stock = Path.Combine(f.Home, "north", "Stock.pbix"),
                StoreBudget = Path.Combine(f.Home, "store", "Budget.pbix"),
                Root = Path.Combine(f.Workspace, "Root.bim"),
                Away = Path.Combine(f.Home, "away", "Away.bim"),
                Profile = Path.Combine(f.Home, "profile"),
            };
            w.LocalAppData = Path.Combine(w.Profile, "AppData", "Local");
            w.Roots = LocalDesktop.WorkspaceRoots(w.LocalAppData, w.Profile);
            w.DesktopWs = DesktopStarts(w.InstallerRoot, 52999);   // a Power BI Desktop that runs the whole time (read only by AS+D)
            w.StoreDesktopWs = DesktopStarts(w.StoreRoot, 52998);  // a Store-app Desktop that runs the whole time (read only by AS+DS)
            Directory.CreateDirectory(Path.GetDirectoryName(w.Away));
            await f.Engine.CreateModelAsync("Root", 1604, discardUnsaved: true);
            await f.Engine.SaveAsync(w.Root, "bim", false, "human");
            await f.Engine.CreateModelAsync("Away", 1604, discardUnsaved: true);
            await f.Engine.SaveAsync(w.Away, "bim", false, "human");
            return w;
        }

        private static LocalDesktop.ProcInfo Service() => new LocalDesktop.ProcInfo
        {
            Pid = 20, ParentPid = 4, Name = "msmdsrv.exe",
            CommandLine = "\"C:\\Program Files\\Microsoft SQL Server\\MSAS16.MSSQLSERVER\\OLAP\\bin\\msmdsrv.exe\" -s \"C:\\Program Files\\Microsoft SQL Server\\MSAS16.MSSQLSERVER\\OLAP\\Config\"",
        };

        protected static async Task OpenAsync(World w, LocalEngine e, SessionManager s, string kind)
        {
            async Task Desktop(string root, string name, string path, string stem)
            {
                // Stamped the way open_local stamps it: the command line names the file Desktop was started with (or none),
                // and the title is the window's (none when Semanticus cannot read one).
                var port = w.Port++;
                var ws = DesktopStarts(root, port);
                var exe = root == w.StoreRoot ? "PBIDesktopStore.exe" : root == w.RsRoot ? "PBIDesktopRS.exe" : "PBIDesktop.exe";
                var id = LocalDesktop.Resolve(ws, new List<LocalDesktop.ProcInfo>
                {
                    new LocalDesktop.ProcInfo { Pid = 30, ParentPid = 1, Name = exe, Title = stem == null ? null : stem + " - Power BI Desktop",
                        CommandLine = "\"C:\\Program Files\\Microsoft Power BI Desktop\\bin\\" + exe + "\"" + (path == null ? "" : " \"" + path + "\"") },
                    new LocalDesktop.ProcInfo { Pid = 31, ParentPid = 30, Name = "msmdsrv.exe", CommandLine = "\"msmdsrv.exe\" -c -s \"" + ws + "\\Data\"" },
                });
                StampLog[kind] = $"{kind} stamped stem={id?.Stem ?? "-"}";
                await e.CreateModelAsync(name ?? "Model", 1604, discardUnsaved: true);
                s.Current.LiveOrigin = LocalEngine.LocalOriginFor("localhost:" + port, Guid.NewGuid().ToString(), id, null, w.Roots, () => new List<LocalDesktop.ProcInfo>());
                Assert.True(s.Current.LiveOrigin.LocalDesktop, kind + " was not stamped as Power BI Desktop");
                Assert.Null(s.Current.LiveOrigin.LocalPath);
            }
            async Task Local(string dataSource, string db, List<LocalDesktop.ProcInfo> processes, bool? desktop)
            {
                await e.CreateModelAsync(db, 1604, discardUnsaved: true);
                s.Current.LiveOrigin = LocalEngine.LocalOriginFor(dataSource, db, null, null, w.Roots, () => processes);
                if (desktop != null) Assert.Equal(desktop.Value, s.Current.LiveOrigin.LocalDesktop);
            }
            // A Desktop window stamped the way open_local stamps it: LocalDesktop.Resolve over the process list, whose command line
            // names the Windows path Desktop was started with. The path it returns is mapped to this machine's copy of that file.
            async Task Window(string name, string launchedWin, string title)
            {
                var port = w.Port++;
                var ws = DesktopStarts(w.InstallerRoot, port);
                var id = LocalDesktop.Resolve(ws, new List<LocalDesktop.ProcInfo>
                {
                    new LocalDesktop.ProcInfo { Pid = 30, ParentPid = 1, Name = "PBIDesktop.exe", Title = title,
                        CommandLine = "\"C:\\Program Files\\Microsoft Power BI Desktop\\bin\\PBIDesktop.exe\" \"" + launchedWin + "\"" },
                    new LocalDesktop.ProcInfo { Pid = 31, ParentPid = 30, Name = "msmdsrv.exe", CommandLine = "\"msmdsrv.exe\" -c -s \"" + ws + "\\Data\"" },
                });
                StampLog[kind] = $"{kind} stamped stem={id?.Stem ?? "-"}";
                await e.CreateModelAsync(name ?? "Model", 1604, discardUnsaved: true);
                s.Current.LiveOrigin = LocalEngine.LocalOriginFor("localhost:" + port, Guid.NewGuid().ToString(), id, null, w.Roots, () => new List<LocalDesktop.ProcInfo>());
                Assert.True(s.Current.LiveOrigin.LocalDesktop, kind + " was not stamped as Power BI Desktop");
                Assert.Null(s.Current.LiveOrigin.LocalPath);
            }
            switch (kind)
            {
                case "X:Stock": await Window("Stock", "C:\\east\\Sales.pbix", "Stock - Power BI Desktop"); break;
                case "X:SalesEU": await Window("Sales - EU", "C:\\east\\Sales.pbix", "Sales - EU - Power BI Desktop (Vorschau)"); break;
                case "Xs:east": await Window("Sales", "C:\\east\\Sales.pbix", "Sales - Power BI Desktop"); break;
                case "Xw:west": await Window("Sales", "C:\\east\\Sales.pbix", "Sales - Power BI Desktop"); break;
                case "Xl:east": await Window("Sales", "C:\\east\\Sales.pbix", "Sales - Power BI Desktop (Vorschau)"); break;
                case "U+S":
                {
                    // A Desktop whose workspace folder no build knows: its msmdsrv names the folder, whose port file is this port.
                    var port = w.Port++;
                    var ws = DesktopStarts(Path.Combine(w.Profile, "Elsewhere", "AnalysisServicesWorkspaces"), port);
                    await Local("localhost:" + port, "Odd", new List<LocalDesktop.ProcInfo>
                    {
                        new LocalDesktop.ProcInfo { Pid = 40, ParentPid = 1, Name = "PBIDesktop.exe", Title = "Odd - Power BI Desktop" },
                        new LocalDesktop.ProcInfo { Pid = 41, ParentPid = 40, Name = "msmdsrv.exe", CommandLine = "\"msmdsrv.exe\" -c -s \"" + ws + "\\Data\"" },
                        Service(),
                    }, true);
                    break;
                }
                case "AS+DS":
                    await Local("localhost:2383", "Server", new List<LocalDesktop.ProcInfo>
                    {
                        new LocalDesktop.ProcInfo { Pid = 50, ParentPid = 1, Name = "PBIDesktopStore.exe", Title = "Budget - Power BI Desktop" },
                        new LocalDesktop.ProcInfo { Pid = 51, ParentPid = 50, Name = "msmdsrv.exe", CommandLine = "\"msmdsrv.exe\" -c -s \"" + w.StoreDesktopWs + "\\Data\"" },
                        Service(),
                    }, false);
                    break;
                case "N": await Desktop(w.InstallerRoot, null, null, null); break;
                case "T:Sales": await Desktop(w.InstallerRoot, "Sales", null, "Sales"); break;
                case "T:Stock": await Desktop(w.InstallerRoot, "Stock", null, "Stock"); break;
                case "P:east": await Desktop(w.InstallerRoot, "Sales", w.East, "Sales"); break;
                case "Pn:east": await Desktop(w.InstallerRoot, "Sales", w.East, null); break;
                case "P:west": await Desktop(w.InstallerRoot, "Sales", w.West, "Sales"); break;
                case "P:stock": await Desktop(w.InstallerRoot, "Stock", w.Stock, "Stock"); break;
                case "S:P": await Desktop(w.StoreRoot, "Budget", w.StoreBudget, "Budget"); break;
                case "S:N": await Desktop(w.StoreRoot, null, null, null); break;
                case "R:T": await Desktop(w.RsRoot, "Budget", null, "Budget"); break;
                case "U": await Local("localhost:" + (w.Port++), "Odd", new List<LocalDesktop.ProcInfo>(), true); break;
                case "AS": await Local("localhost:2383", "Server", new List<LocalDesktop.ProcInfo> { Service() }, false); break;
                case "AS2": await Local("localhost:2383", "Server2", new List<LocalDesktop.ProcInfo> { Service() }, false); break;
                case "AS+D":
                    await Local("localhost:2383", "Server", new List<LocalDesktop.ProcInfo>
                    {
                        new LocalDesktop.ProcInfo { Pid = 10, ParentPid = 1, Name = "PBIDesktop.exe", Title = "Sales - Power BI Desktop" },
                        new LocalDesktop.ProcInfo { Pid = 11, ParentPid = 10, Name = "msmdsrv.exe", CommandLine = "\"msmdsrv.exe\" -c -s \"" + w.DesktopWs + "\\Data\"" },
                        Service(),
                    }, false);
                    break;
                case "Cloud":
                    await e.CreateModelAsync("Sales", 1604, discardUnsaved: true);
                    s.Current.LiveOrigin = new LiveOrigin(CloudEndpoint, "Sales", null);
                    break;
                case "Cloud2":
                    await e.CreateModelAsync("Budget", 1604, discardUnsaved: true);
                    s.Current.LiveOrigin = new LiveOrigin(CloudEndpoint, "Budget", null);
                    break;
                case "Saved-root": await e.OpenAsync(w.Root, discardUnsaved: true); break;
                case "Saved-away": await e.OpenAsync(w.Away, discardUnsaved: true); break;
                case "Unsaved": await e.CreateModelAsync("Fresh", 1604, discardUnsaved: true); break;
                default: throw new ArgumentException(kind);
            }
        }

        // ---- who is who ------------------------------------------------------------------------------------------------

        /// <summary>The identity the ruling keys an on on: a Desktop model's window title, or nothing (one identity, the ruling
        /// says so), never the file on Desktop's command line. AS+D is the server database AS is.</summary>
        protected static string IdentityOf(string k) => k switch
        {
            "N" or "S:N" or "U" or "U+S" or "X:SalesEU" or "Pn:east" or "Xl:east" => "nothing",
            "T:Sales" or "P:east" or "P:west" or "Xs:east" or "Xw:west" => "title:sales",
            "T:Stock" or "X:Stock" or "P:stock" => "title:stock",
            "R:T" or "S:P" => "title:budget",
            "AS" or "AS+D" or "AS+DS" => "server:Server", "AS2" => "server:Server2",
            "Cloud" => "cloud:Sales", "Cloud2" => "cloud:Budget",
            "Saved-root" => "saved:root", "Saved-away" => "saved:away",
            "Unsaved" => "unsaved:" + Guid.NewGuid().ToString("N"),   // every unsaved model is its own
            _ => throw new ArgumentException(k),
        };

        /// <summary>Really a Power BI Desktop model (U is ruled one). AS+D is a real server whatever the product stamps.</summary>
        protected static bool TrueDesktop(string k) => k is "N" or "T:Sales" or "T:Stock" or "P:east" or "Pn:east" or "P:west" or "P:stock" or "S:P" or "S:N" or "R:T" or "U"
            or "U+S" or "X:Stock" or "X:SalesEU" or "Xs:east" or "Xl:east" or "Xw:west";
        protected static bool IsNothing(string k) => IdentityOf(k) == "nothing";
        protected static bool IsNamedDesktop(string k) => TrueDesktop(k) && IdentityOf(k).StartsWith("title:", StringComparison.Ordinal);
        protected static string TitleOf(string k) => k switch
        {
            "T:Sales" or "P:east" or "Pn:east" or "P:west" or "Xs:east" or "Xl:east" or "Xw:west" => "sales",
            "T:Stock" or "P:stock" or "X:Stock" => "stock",
            "S:P" or "R:T" => "budget",
            _ => null,
        };

        /// <summary>Ruling 1: an off by c is written for every model c cannot be told apart from.</summary>
        protected static bool Reach(string c, string r)
        {
            if (c == "Unsaved" || r == "Unsaved") return false;
            if (IdentityOf(c) == IdentityOf(r) && IdentityOf(c) != "nothing") return true;
            if (!TrueDesktop(c) || !TrueDesktop(r)) return false;
            if (IsNothing(c) || IsNothing(r)) return true;
            return TitleOf(c) == TitleOf(r);
        }

        /// <summary>What the "old" workspace off (written before per-model switches) reaches: every live and unsaved model and
        /// a model saved in the workspace folder itself.</summary>
        protected static bool OldReaches(string r) => r != "Saved-away";

        // ---- reading and switching ------------------------------------------------------------------------------------

        protected static async Task<DecisionPointInfo> ReadAsync(LocalEngine e)
        {
            var viaAgent = (await McpTools.GetDecisionPoints(e)).Single(p => p.Name == Point);
            var viaStudio = (await new EngineRpcTarget(e).getDecisionPoints()).Single(p => p.Name == Point);
            Assert.Equal(viaAgent.Enabled, viaStudio.Enabled);
            Assert.Equal(viaAgent.Note, viaStudio.Note);
            Assert.Equal(viaAgent.Enabled, e.DecisionPointEnabled(Point));
            return viaAgent;
        }

        protected static async Task<DecisionPointInfo> SetAsync(LocalEngine e, string door, bool enabled)
        {
            var rows = door == "studio" ? await new EngineRpcTarget(e).setDecisionPointEnabled(Point, enabled)
                                        : await McpTools.SetDecisionPointEnabled(e, Point, enabled);
            return rows.Single(p => p.Name == Point);
        }

        protected static async Task<Dictionary<string, DecisionPointInfo>> ReadAllAsync(World w, LocalEngine e, SessionManager s)
        {
            var seen = new Dictionary<string, DecisionPointInfo>();
            foreach (var r in Readers) { await OpenAsync(w, e, s, r); seen[r] = await ReadAsync(e); }
            return seen;
        }

        protected static Task<Dictionary<string, DecisionPointInfo>> ReadAllAfterRestartAsync(World w, Semanticus.Engine.Entitlement.IEntitlement tier = null)
        {
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, tier ?? TestEntitlements.Pro, w.F.Workspace);
            return ReadAllAsync(w, engine, sessions);
        }

        protected static string Sha8(string s)
        {
            using var sha = SHA256.Create();
            return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(s ?? "")), 0, 8).ToLowerInvariant();
        }

        protected static string Folder(World w) => Path.Combine(w.F.Workspace, LayoutStore.DirName, LayoutStore.DecisionSettingsFolder);

        /// <summary>A background off: the old workspace file, a round-4 lift, a round-4 clearedFor, or an off by a kind.</summary>
        protected static async Task BackgroundAsync(World w, string bg)
        {
            var old = Path.Combine(w.F.Workspace, LayoutStore.DirName, LayoutStore.DecisionSettingsFile);
            void WriteOld()
            {
                Directory.CreateDirectory(Path.GetDirectoryName(old));
                File.WriteAllText(old, "{\"points\":{\"" + Point + "\":{\"enabled\":false}}}");
            }
            switch (bg)
            {
                case "none": return;
                case "old": WriteOld(); return;
                case "lift4":
                    // What a round-4 build wrote when a title view and a path view of Sales lifted the old off.
                    WriteOld();
                    Directory.CreateDirectory(Folder(w));
                    File.WriteAllText(Path.Combine(Folder(w), "desktop-earlier-lifted.json"),
                        "{\"points\":{\"" + Point + "\":{\"enabled\":true,\"scopes\":[\"title:" + Sha8("sales") + "\",\"pathtitle:" + Sha8("sales") + "\",\"any\"]}}}");
                    return;
                case "cleared4":
                    // What a round-4 build left in the unnamed off after title and path views of Sales and Stock switched on.
                    Directory.CreateDirectory(Folder(w));
                    File.WriteAllText(Path.Combine(Folder(w), "desktop-unnamed.json"),
                        "{\"points\":{\"" + Point + "\":{\"enabled\":false,\"offFor\":[\"run:0123456789abcdef\"],\"clearedFor\":[\"title:" + Sha8("sales")
                        + "\",\"pathtitle:" + Sha8("sales") + "\",\"title:" + Sha8("stock") + "\",\"any\"]}}}");
                    return;
                default:
                    await OpenAsync(w, w.F.Engine, w.F.Sessions, bg);
                    Assert.False((await SetAsync(w.F.Engine, "agent", false)).Enabled);
                    return;
            }
        }

        /// <summary>Which readers a background leaves off, by the ruling.</summary>
        protected static bool BackgroundReaches(string bg, string r) => bg switch
        {
            "none" => false,
            "old" or "lift4" => OldReaches(r),
            "cleared4" => TrueDesktop(r),
            _ => Reach(bg, r),
        };

        // ---- the notes, short ---------------------------------------------------------------------------------------------

        protected static string Short(string note) => note switch
        {
            null => "-",
            LocalEngine.OnForFilesWithThisNameNote => "ON-NAME",
            LocalEngine.OnForModelsWithNoNameNote => "ON-NONAME",
            LocalEngine.OnForThisNameAfterItsOwnOffNote => "ON-NAME-OWN",
            LocalEngine.OnForNoNameAfterItsOwnOffNote => "ON-NONAME-OWN",
            "Off because it was switched off for every model here before each model kept its own switches." => "OLD",
            "Off because it was switched off for this model." => "SELF",
            "Off because it was switched off for a Power BI Desktop file with the same name. Desktop does not say whether that was this file." => "TITLE",
            "Off because it was switched off for a Power BI Desktop file whose name could not be read. Desktop does not say whether that was this file." => "UNNAMED",
            "On until Power BI Desktop closes. Desktop does not say which file this is, so after that it is off again." => "UNTILCLOSE",
            "Off because it was switched off for another Power BI Desktop file. Desktop does not say which file this is, so it may be that one." => "ANY",
            "Off because it was switched off for this file." => "FILEOFF",
            "Off because it was switched off for a model Semanticus took to be this one before it was saved." => "CARRIED",
            _ when note.StartsWith("Off because it was switched off for ") && note.EndsWith(", which is no longer in this folder.") => "MOVED",
            _ => "?" + note,
        };

        protected static string State(DecisionPointInfo p) => (p.Enabled ? "on " : "off") + " " + Short(p.Note);

        protected void Table(string head, Dictionary<string, DecisionPointInfo> before, Dictionary<string, DecisionPointInfo> after,
            Dictionary<string, DecisionPointInfo> restart, IEnumerable<string> flags)
        {
            Out.WriteLine("CELL " + head);
            foreach (var r in Readers)
                Out.WriteLine($"   {r,-11} {State(before[r]),-16} -> {State(after[r]),-16} -> {State(restart[r])}");
            foreach (var flag in flags) Out.WriteLine("   " + flag);
            foreach (var kv in StampLog.OrderBy(k => k.Key)) Out.WriteLine("   STAMP " + kv.Value);
        }

        protected static bool Harm(string flag) =>
            flag.StartsWith("ONREACH") || flag.StartsWith("OFFMISS") || flag.StartsWith("ONMISS") || flag.StartsWith("RESTART") || flag.StartsWith("ONOFF") || flag.StartsWith("REVOKE-WRONG")
            // The notes are held too: a row that says something that did not happen is a defect (the join, item 5).
            || flag.StartsWith("OVERCLAIM") || flag.StartsWith("OWN-EMPTY") || flag.StartsWith("OWN-UNSAID") || flag.StartsWith("SILENT");

        protected void AssertNoHarm(List<string> flags)
        {
            var harms = flags.Where(Harm).ToList();
            Assert.True(harms.Count == 0, string.Join(" | ", harms));
        }

        protected static string DoorFor(string a, string b) => ((a + "|" + b).Sum(ch => (int)ch) & 1) == 0 ? "studio" : "agent";

        /// <summary>The two big blocks of the matrix (an on after every background, 525 cells, and a newer off after every on, 441).
        /// They took 22 m 53 s on omarchy-yoga at the join (30 Sep), so they carry the sweep trait (lean suite, 10 Oct): the nightly
        /// sweep runs them, the per-PR run does not. They used to be skipped unless SEMANTICUS_SWITCH_MATRIX=full; a skip that
        /// reports "not a pass" is gone because the sweep tier runs the same cells every night. The other 78 cells run every time.</summary>
        protected sealed class FullMatrixTheoryAttribute : TheoryAttribute
        {
        }

        public static IEnumerable<object[]> ClickerCells() => Kinds.Select(k => new object[] { k });
        // Lean suite (10 Oct): the sample every pull request runs. One kind a person can tell apart by its path, one by its title, one in the cloud,
        // so each reach rule is crossed; the full list of kinds is the sweep tier's.
        public static IEnumerable<object[]> ClickerSample() => new[] { "P:east", "T:Sales", "Cloud" }.Select(k => new object[] { k });
        public static IEnumerable<object[]> BgClickerCells()
        {
            foreach (var bg in new[] { "none", "old", "lift4", "cleared4" }.Concat(Kinds))
                foreach (var c in Kinds)
                    yield return new object[] { bg, c };
        }
        public static IEnumerable<object[]> PairCells()
        {
            foreach (var x in Kinds)
                foreach (var c in Kinds)
                    yield return new object[] { x, c };
        }

        /// <summary>The on-row note checks: each sentence must hold for the readers it names.</summary>
        protected static void NoteChecks(string c, DecisionPointInfo cBefore, DecisionPointInfo row, Dictionary<string, DecisionPointInfo> after, List<string> flags)
        {
            var othersOff = Readers.Where(r => TrueDesktopProduct(r) && IdentityOf(r) != IdentityOf(c) && !after[r].Enabled).ToList();
            var others = row.Note == LocalEngine.OnForFilesWithThisNameNote || row.Note == LocalEngine.OnForModelsWithNoNameNote;
            if (others && othersOff.Count == 0)
                flags.Add("OVERCLAIM-OTHERS: the row says other files stay off and no other Desktop reader does");
            var saysName = row.Note == LocalEngine.OnForFilesWithThisNameNote || row.Note == LocalEngine.OnForThisNameAfterItsOwnOffNote;
            var saysNoName = row.Note == LocalEngine.OnForModelsWithNoNameNote || row.Note == LocalEngine.OnForNoNameAfterItsOwnOffNote;
            if (saysName && !IsNamedDesktop(c)) flags.Add("OVERCLAIM-NAME: the row says on for every file with this name, and " + c + " has no name Semanticus reads");
            if (saysNoName && !IsNothing(c)) flags.Add("OVERCLAIM-NONAME: the row says on for every model with no name, and " + c + " has one");
            if (saysName || saysNoName)
                foreach (var r in Readers.Where(r => IdentityOf(r) == IdentityOf(c) && !after[r].Enabled))
                    flags.Add($"OVERCLAIM-REACH {r}: the row says the on reaches it, and it reads off ({Short(after[r].Note)})");
            if (row.Note == LocalEngine.OnForThisNameAfterItsOwnOffNote)
            {
                var named = Readers.Where(r => IsNothing(r) && TrueDesktop(r) && !after[r].Enabled).ToList();
                if (named.Count == 0) flags.Add("OWN-EMPTY: the row says a model whose name cannot be read may still be off, and none reads off");
                var stray = othersOff.Where(r => !IsNothing(r)).ToList();
                if (stray.Count > 0) flags.Add("OWN-UNSAID: also off, not named by the row: " + string.Join(", ", stray.Select(r => r + " " + Short(after[r].Note))));
            }
            if (row.Note == LocalEngine.OnForNoNameAfterItsOwnOffNote)
            {
                var named = Readers.Where(r => IsNamedDesktop(r) && !after[r].Enabled).ToList();
                if (named.Count == 0) flags.Add("OWN-EMPTY: the row says a Desktop file whose name can be read may still be off, and none reads off");
                var stray = othersOff.Where(r => !IsNamedDesktop(r)).ToList();
                if (stray.Count > 0) flags.Add("OWN-UNSAID: also off, not named by the row: " + string.Join(", ", stray.Select(r => r + " " + Short(after[r].Note))));
            }
            if (row.Note == null && cBefore != null && !cBefore.Enabled && row.Enabled && TrueDesktopProduct(c) && othersOff.Count > 0)
                flags.Add("SILENT: " + c + " was off, is on, others stay off (" + string.Join(", ", othersOff) + "), and the row says nothing");
        }

        /// <summary>Stamped a Desktop model by this build (AS+D is, by the builder's decision 1).</summary>
        protected static bool TrueDesktopProduct(string k) => TrueDesktop(k);
    }

    // ---- a1: an off from a clean workspace ----------------------------------------------------------------------------------

    public sealed class DecisionSwitchScopeMatrix_Off : DecisionSwitchScopeMatrixBase
    {
        public DecisionSwitchScopeMatrix_Off(ITestOutputHelper o) : base(o) { }

        [Theory]
        [MemberData(nameof(ClickerSample))]
        public Task Off_sample(string c) => Off(c);

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [MemberData(nameof(ClickerCells))]
        public async Task Off(string c)
        {
            using var f = new DecisionFixture();
            var w = await NewWorldAsync(f);
            var door = DoorFor("off", c);
            var before = await ReadAllAsync(w, f.Engine, f.Sessions);
            await OpenAsync(w, f.Engine, f.Sessions, c);
            var row = await SetAsync(f.Engine, door, false);
            var after = await ReadAllAsync(w, f.Engine, f.Sessions);
            var restart = await ReadAllAfterRestartAsync(w);
            var flags = new List<string>();
            if (row.Enabled) flags.Add("OFFMISS the clicker's own row reads on");
            foreach (var r in Readers)
            {
                var want = Reach(c, r);
                if (want && after[r].Enabled) flags.Add($"OFFMISS {r}: {c}'s off should reach it (cannot be told apart) and it reads on");
                if (!want && !after[r].Enabled && before[r].Enabled) flags.Add($"OFFOVER {r}: {c}'s off reached a model it can be told apart from ({Short(after[r].Note)})");
                if (after[r].Enabled != restart[r].Enabled || after[r].Note != restart[r].Note) flags.Add($"RESTART {r}: {State(after[r])} before, {State(restart[r])} after");
            }
            Table($"action=off clicker={c} door={door} row={State(row)}", before, after, restart, flags);
            AssertNoHarm(flags);
        }
    }

    // ---- a2: an on after a background off (none, old, a round-4 lift or clearedFor, or any kind's off) --------------------

    public sealed class DecisionSwitchScopeMatrix_On : DecisionSwitchScopeMatrixBase
    {
        public DecisionSwitchScopeMatrix_On(ITestOutputHelper o) : base(o) { }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [FullMatrixTheory]
        [MemberData(nameof(BgClickerCells))]
        public async Task On(string bg, string c)
        {
            using var f = new DecisionFixture();
            var w = await NewWorldAsync(f);
            var door = DoorFor(bg, c);
            await BackgroundAsync(w, bg);
            var before = await ReadAllAsync(w, f.Engine, f.Sessions);
            await OpenAsync(w, f.Engine, f.Sessions, c);
            var cBefore = await ReadAsync(f.Engine);
            var row = await SetAsync(f.Engine, door, true);
            var cAfter = await ReadAsync(f.Engine);
            var after = await ReadAllAsync(w, f.Engine, f.Sessions);
            var restart = await ReadAllAfterRestartAsync(w);
            var flags = new List<string>();
            if (!row.Enabled || !cAfter.Enabled) flags.Add("ONMISS the clicker does not read on after its own on");
            var cid = IdentityOf(c);
            foreach (var r in Readers)
            {
                if (!before[r].Enabled && after[r].Enabled && IdentityOf(r) != cid)
                    flags.Add($"ONREACH {r}: off -> on after the on for {c}, another identity (row {Short(row.Note)})");
                if (before[r].Enabled && !after[r].Enabled) flags.Add($"ONOFF {r}: the on for {c} switched it off");
                if (IdentityOf(r) == cid && c != "Unsaved" && !after[r].Enabled) flags.Add($"ONMISS {r}: the same identity as {c} reads off in a later run ({Short(after[r].Note)})");
                if (!IdentityOf(r).StartsWith("unsaved") && (after[r].Enabled != restart[r].Enabled || after[r].Note != restart[r].Note))
                    flags.Add($"RESTART {r}: {State(after[r])} before, {State(restart[r])} after");
                var bgWant = BackgroundReaches(bg, r);
                if (bgWant && before[r].Enabled) flags.Add($"BG-OFFMISS {r}: the background off {bg} should reach it and it reads on");
                if (!bgWant && !before[r].Enabled) flags.Add($"BG-OFFOVER {r}: the background off {bg} reached it ({Short(before[r].Note)})");
            }
            NoteChecks(c, cBefore, row, after, flags);
            Table($"action=on bg={bg} clicker={c} door={door} clicker-before={State(cBefore)} row={State(row)}", before, after, restart, flags);
            AssertNoHarm(flags);
        }
    }

    // ---- a3 and a4: the clicker's own off then on, and own on then off ---------------------------------------------------

    public sealed class DecisionSwitchScopeMatrix_Own : DecisionSwitchScopeMatrixBase
    {
        public DecisionSwitchScopeMatrix_Own(ITestOutputHelper o) : base(o) { }

        [Theory]
        [MemberData(nameof(ClickerSample))]
        public Task OnAfterOwnOff_sample(string c) => OnAfterOwnOff(c);

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [MemberData(nameof(ClickerCells))]
        public async Task OnAfterOwnOff(string c)
        {
            // (a) the same run: off, then on, nothing in between.
            using var f1 = new DecisionFixture();
            var w1 = await NewWorldAsync(f1);
            await OpenAsync(w1, f1.Engine, f1.Sessions, c);
            await SetAsync(f1.Engine, "agent", false);
            var offOnly = await ReadAllAsync(w1, f1.Engine, f1.Sessions);           // what the off alone leaves

            using var f2 = new DecisionFixture();
            var w2 = await NewWorldAsync(f2);
            var door = DoorFor("own", c);
            await OpenAsync(w2, f2.Engine, f2.Sessions, c);
            var off = await SetAsync(f2.Engine, door, false);
            var row = await SetAsync(f2.Engine, door, true);
            var after = await ReadAllAsync(w2, f2.Engine, f2.Sessions);
            var restart = await ReadAllAfterRestartAsync(w2);
            var flags = new List<string>();
            Check(c, off, row, offOnly, after, restart, flags);
            Table($"action=on-after-own-off(same run) clicker={c} door={door} row={State(row)}", offOnly, after, restart, flags);

            // (b) a later run: off, then the same identity opened again another day and switched on there.
            using var f3 = new DecisionFixture();
            var w3 = await NewWorldAsync(f3);
            await OpenAsync(w3, f3.Engine, f3.Sessions, c);
            await SetAsync(f3.Engine, "agent", false);
            var before3 = await ReadAllAsync(w3, f3.Engine, f3.Sessions);
            await OpenAsync(w3, f3.Engine, f3.Sessions, c);
            var cBefore3 = await ReadAsync(f3.Engine);
            var row3 = await SetAsync(f3.Engine, door, true);
            var after3 = await ReadAllAsync(w3, f3.Engine, f3.Sessions);
            var restart3 = await ReadAllAfterRestartAsync(w3);
            var flags3 = new List<string>();
            Check(c, cBefore3, row3, before3, after3, restart3, flags3);
            Table($"action=on-after-own-off(later run) clicker={c} door={door} clicker-before={State(cBefore3)} row={State(row3)}", before3, after3, restart3, flags3);
            AssertNoHarm(flags.Concat(flags3).ToList());
        }

        private static void Check(string c, DecisionPointInfo cBefore, DecisionPointInfo row, Dictionary<string, DecisionPointInfo> before,
            Dictionary<string, DecisionPointInfo> after, Dictionary<string, DecisionPointInfo> restart, List<string> flags)
        {
            var cid = IdentityOf(c);
            if (!row.Enabled) flags.Add("ONMISS the clicker's row reads off after its own on");
            foreach (var r in Readers)
            {
                if (!before[r].Enabled && after[r].Enabled && IdentityOf(r) != cid)
                    flags.Add($"ONREACH {r}: off -> on after {c}'s own on, another identity (row {Short(row.Note)})");
                if (IdentityOf(r) == cid && c != "Unsaved" && !after[r].Enabled) flags.Add($"ONMISS {r}: the same identity as {c} reads off ({Short(after[r].Note)})");
                if (!IdentityOf(r).StartsWith("unsaved") && (after[r].Enabled != restart[r].Enabled || after[r].Note != restart[r].Note))
                    flags.Add($"RESTART {r}: {State(after[r])} before, {State(restart[r])} after");
            }
            NoteChecks(c, cBefore, row, after, flags);
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [MemberData(nameof(ClickerCells))]
        public async Task OffAfterOwnOn(string c)
        {
            foreach (var bg in new[] { "none", "old" })
            {
                using var f = new DecisionFixture();
                var w = await NewWorldAsync(f);
                var door = DoorFor("offafteron" + bg, c);
                await BackgroundAsync(w, bg);
                await OpenAsync(w, f.Engine, f.Sessions, c);
                var on = await SetAsync(f.Engine, door, true);
                var before = await ReadAllAsync(w, f.Engine, f.Sessions);
                await OpenAsync(w, f.Engine, f.Sessions, c);
                var row = await SetAsync(f.Engine, door, false);
                var after = await ReadAllAsync(w, f.Engine, f.Sessions);
                var restart = await ReadAllAfterRestartAsync(w);
                var flags = new List<string>();
                if (row.Enabled) flags.Add("OFFMISS the clicker's own row reads on after its off");
                foreach (var r in Readers)
                {
                    var want = Reach(c, r) || BackgroundReaches(bg, r) && IdentityOf(r) != IdentityOf(c);
                    if (Reach(c, r) && after[r].Enabled) flags.Add($"OFFMISS {r}: {c}'s off after its own on should reach it and it reads on ({Short(after[r].Note)})");
                    if (!want && !after[r].Enabled && before[r].Enabled) flags.Add($"OFFOVER {r}: {c}'s off reached a model it can be told apart from ({Short(after[r].Note)})");
                    if (!IdentityOf(r).StartsWith("unsaved") && (after[r].Enabled != restart[r].Enabled || after[r].Note != restart[r].Note))
                        flags.Add($"RESTART {r}: {State(after[r])} before, {State(restart[r])} after");
                }
                Table($"action=off-after-own-on bg={bg} clicker={c} door={door} on-row={State(on)} row={State(row)}", before, after, restart, flags);
                AssertNoHarm(flags);
            }
        }
    }

    // ---- a5: decision 2, a newer off by c after an on by x ---------------------------------------------------------------

    public sealed class DecisionSwitchScopeMatrix_Revoke : DecisionSwitchScopeMatrixBase
    {
        public DecisionSwitchScopeMatrix_Revoke(ITestOutputHelper o) : base(o) { }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [FullMatrixTheory]
        [MemberData(nameof(PairCells))]
        public async Task NewerOff(string x, string c)
        {
            using var f = new DecisionFixture();
            var w = await NewWorldAsync(f);
            var door = DoorFor(x, c);
            await BackgroundAsync(w, "old");                                  // every live model reads off; x needs its on
            await OpenAsync(w, f.Engine, f.Sessions, x);
            var xOn = await SetAsync(f.Engine, door, true);
            var before = await ReadAllAsync(w, f.Engine, f.Sessions);
            await OpenAsync(w, f.Engine, f.Sessions, c);
            var cOff = await SetAsync(f.Engine, door == "studio" ? "agent" : "studio", false);
            var after = await ReadAllAsync(w, f.Engine, f.Sessions);
            var restart = await ReadAllAfterRestartAsync(w);

            // then c switches itself back on in a later run: does x's on come back?
            await OpenAsync(w, f.Engine, f.Sessions, c);
            var cOn = await SetAsync(f.Engine, door, true);
            var undo = await ReadAllAsync(w, f.Engine, f.Sessions);

            var flags = new List<string>();
            var xid = IdentityOf(x);
            var cid = IdentityOf(c);
            foreach (var r in Readers)
            {
                if (!IdentityOf(r).StartsWith("unsaved") && (after[r].Enabled != restart[r].Enabled || after[r].Note != restart[r].Note))
                    flags.Add($"RESTART {r}: {State(after[r])} before, {State(restart[r])} after");
                if (!before[r].Enabled && after[r].Enabled) flags.Add($"ONREACH {r}: an off by {c} switched it on");
                if (!after[r].Enabled && undo[r].Enabled && IdentityOf(r) != cid) flags.Add($"ONREACH {r}: {c}'s on after its off switched it on (row {Short(cOn.Note)})");
                if (IdentityOf(r) != xid || x == "Unsaved" || !before[r].Enabled) continue;
                var reach = Reach(c, r);
                if (!after[r].Enabled && !reach) flags.Add($"REVOKE-WRONG {r}: {c}'s off silenced {x}'s on, and {r} does not read {c}'s off ({Short(after[r].Note)})");
                if (!after[r].Enabled && reach && cid != xid) flags.Add($"REVOKED {r}: {c}'s off took {x}'s on away ({Short(after[r].Note)}); undo -> {State(undo[r])}");
                if (after[r].Enabled && reach && cid != xid) flags.Add($"KEPT {r}: {x}'s on beats {c}'s newer off, which may be this model's ({Short(after[r].Note)})");
                if (after[r].Enabled && cid == xid) flags.Add($"OFFMISS {r}: the same identity switched off and it reads on");
            }
            Table($"action=revoke x={x} c={c} door={door} x-on={State(xOn)} c-off={State(cOff)} c-on={State(cOn)}", before, after, restart, flags);
            Out.WriteLine("   undo: " + string.Join(" ", Readers.Select(r => r + "=" + (undo[r].Enabled ? "on" : "off"))));
            AssertNoHarm(flags);
        }
    }

    // ---- Free says the same as Pro ------------------------------------------------------------------------------------

    public sealed class DecisionSwitchScopeMatrix_Free : DecisionSwitchScopeMatrixBase
    {
        public DecisionSwitchScopeMatrix_Free(ITestOutputHelper o) : base(o) { }

        private async Task<(DecisionPointInfo Row, Dictionary<string, DecisionPointInfo> After, Dictionary<string, DecisionPointInfo> Restart)> RunAsync(
            Semanticus.Engine.Entitlement.IEntitlement tier, string bg, string c, string door)
        {
            using var f = new DecisionFixture(tier);
            var w = await NewWorldAsync(f);
            await BackgroundAsync(w, bg);
            await OpenAsync(w, f.Engine, f.Sessions, c);
            var row = await SetAsync(f.Engine, door, true);
            var after = await ReadAllAsync(w, f.Engine, f.Sessions);
            var restart = await ReadAllAfterRestartAsync(w, tier);
            return (row, after, restart);
        }

        [Theory]
        [InlineData("P:west", "T:Sales")]
        [InlineData("old", "Cloud")]
        public Task Free_reads_and_switches_as_Pro_does_sample(string bg, string c) => Free_reads_and_switches_as_Pro_does(bg, c);

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("P:west", "T:Sales")]
        [InlineData("T:Sales", "P:east")]
        [InlineData("old", "N")]
        [InlineData("P:stock", "N")]
        [InlineData("N", "P:east")]
        [InlineData("old", "Cloud")]
        [InlineData("Saved-root", "Saved-root")]
        [InlineData("old", "AS")]
        [InlineData("S:P", "R:T")]
        [InlineData("U", "S:N")]
        [InlineData("P:east", "X:Stock")]
        [InlineData("old", "AS+D")]
        [InlineData("N", "U+S")]
        public async Task Free_reads_and_switches_as_Pro_does(string bg, string c)
        {
            var door = DoorFor(bg, c);
            var pro = await RunAsync(TestEntitlements.Pro, bg, c, door);
            var free = await RunAsync(TestEntitlements.Free, bg, c, door);
            var diffs = new List<string>();
            if (pro.Row.Enabled != free.Row.Enabled || pro.Row.Note != free.Row.Note) diffs.Add($"row: Pro {State(pro.Row)}, Free {State(free.Row)}");
            foreach (var r in Readers)
            {
                if (pro.After[r].Enabled != free.After[r].Enabled || pro.After[r].Note != free.After[r].Note) diffs.Add($"{r}: Pro {State(pro.After[r])}, Free {State(free.After[r])}");
                if (pro.Restart[r].Enabled != free.Restart[r].Enabled || pro.Restart[r].Note != free.Restart[r].Note) diffs.Add($"{r} after restart: Pro {State(pro.Restart[r])}, Free {State(free.Restart[r])}");
            }
            Out.WriteLine($"CELL free bg={bg} clicker={c} door={door} row={State(free.Row)} diffs={diffs.Count}");
            foreach (var d in diffs) Out.WriteLine("   " + d);
            Assert.Empty(diffs);
        }
    }

    // ---- outside the matrix: a saved model switched off and back on, then renamed or gone --------------------------------

    public sealed class DecisionSwitchScopeMatrix_SavedRename : DecisionSwitchScopeMatrixBase
    {
        public DecisionSwitchScopeMatrix_SavedRename(ITestOutputHelper o) : base(o) { }

        [Theory]
        [InlineData("studio")]
        [InlineData("agent")]
        public async Task A_saved_model_switched_off_and_on_then_renamed(string door)
        {
            using var f = new DecisionFixture();
            var folder = Path.Combine(f.Home, "models");
            Directory.CreateDirectory(folder);
            var root = Path.Combine(folder, "Root.bim");
            await f.Engine.CreateModelAsync("Root", 1604, discardUnsaved: true);
            await f.Engine.SaveAsync(root, "bim", false, "human");
            await SetAsync(f.Engine, door, false);
            var on = await SetAsync(f.Engine, door, true);
            Out.WriteLine("Root.bim after off then on: " + State(on));
            // The ruled half (ONMISS): a saved model reads on right after its own on. What the rename and the new model below then
            // read is not ruled yet (v-switch-scope-r5 item A, it errs toward off), so those two are printed, not asserted.
            Assert.True(on.Enabled, "Root.bim reads " + State(on) + " right after its own on");
            await f.Engine.CreateModelAsync("Other", 1604, discardUnsaved: true);
            File.Move(root, Path.Combine(folder, "Root2.bim"));
            await f.Engine.OpenAsync(Path.Combine(folder, "Root2.bim"), discardUnsaved: true);
            var renamed = await ReadAsync(f.Engine);
            Out.WriteLine("Root2.bim (renamed outside Semanticus): " + State(renamed) + " | " + renamed.Note);
            await f.Engine.CreateModelAsync("Fresh", 1604, discardUnsaved: true);
            File.Move(Path.Combine(folder, "Root2.bim"), Path.Combine(f.Home, "Root2.bim"));
            await f.Engine.SaveAsync(Path.Combine(folder, "New.bim"), "bim", false, "human");
            var fresh = await ReadAsync(f.Engine);
            Out.WriteLine("New.bim (a new model saved into the folder): " + State(fresh) + " | " + fresh.Note);
        }
    }
}
