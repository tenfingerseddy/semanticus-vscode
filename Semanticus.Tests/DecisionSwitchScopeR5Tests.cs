// review-fix round 5 for m11/switch-scope: written first, red first (see the fix report for the run).
//
// Ruling, round 5: ONS NEVER CLEAR. An off may be shared (it is written for every model the clicker cannot be told apart
// from). An on is kept only under the clicker's own identity (its path when Desktop names one, else its title, else
// "known by nothing"), touches no shared off and no other identity's off, and beats every shared off when that same
// identity reads again. The row says exactly that. A Microsoft Store or Report Server Power BI Desktop is Desktop, and an
// instance that cannot be placed with certainty is a Desktop model known by nothing, never Analysis Services.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Semanticus.Decisions;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    public sealed class DecisionSwitchScopeR5Tests
    {
        /// <summary>The engine's old note for a file known by its path, retired at the join (item 5, 30 Sep): Semanticus no
        /// longer knows a Desktop file by its path. Kept here only so the skipped fact that read it still says what it read.</summary>
        private const string RetiredOnForThisFileAfterItsOwnOffNote = "On for this file. The same file opened another way, such as from Desktop's recent list, may still be off from when this file was switched off, until you switch it on there.";

        private const string Point = DecisionRegistry.MeasureFolderAndFormat;
        private const string Cloud = "powerbi://api.powerbi.com/v1.0/myorg/Contoso Live";
        private const string PathOn = "On for this file. Other Power BI Desktop files that were switched off together with it stay off until you switch them on there.";
        // The join (item 5, 30 Sep) reworded both notes to say which models an on reaches; these read the engine's own words now.
        private const string TitleOn = LocalEngine.OnForFilesWithThisNameNote;
        private const string NothingOn = LocalEngine.OnForModelsWithNoNameNote;
        private const string FileOff = "Off because it was switched off for this file.";

        private static int _ports = 53000;

        private static async Task OpenDesktopAsync(DecisionFixture f, string title = null, string path = null)
        {
            await f.Engine.CreateModelAsync(title ?? "Model", 1604, discardUnsaved: true);
            f.Sessions.Current.LiveOrigin = new LiveOrigin("localhost:" + (++_ports), Guid.NewGuid().ToString(), null,
                localName: title, localPath: path, localDesktop: true);
        }

        private static Task<DecisionPointInfo[]> Set(DecisionFixture f, string door, bool enabled)
            => door == "studio" ? new EngineRpcTarget(f.Engine).setDecisionPointEnabled(Point, enabled)
             : McpTools.SetDecisionPointEnabled(f.Engine, Point, enabled);

        private static async Task<DecisionPointInfo> Row(DecisionFixture f, string door = "agent")
        {
            var points = door == "studio" ? await new EngineRpcTarget(f.Engine).getDecisionPoints()
                                          : await McpTools.GetDecisionPoints(f.Engine);
            var row = points.Single(p => p.Name == Point);
            Assert.Equal(row.Enabled, f.Engine.DecisionPointEnabled(Point));
            return row;
        }

        private static string Folder(DecisionFixture f) => Path.Combine(f.Workspace, LayoutStore.DirName, LayoutStore.DecisionSettingsFolder);
        private static string East(DecisionFixture f) => Path.Combine(f.Home, "east", "Sales.pbix");
        private static string West(DecisionFixture f) => Path.Combine(f.Home, "west", "Sales.pbix");
        private static string StockPath(DecisionFixture f) => Path.Combine(f.Home, "north", "Stock.pbix");

        private static void WriteOldOff(DecisionFixture f)
        {
            var shared = Path.Combine(f.Workspace, LayoutStore.DirName, LayoutStore.DecisionSettingsFile);
            Directory.CreateDirectory(Path.GetDirectoryName(shared));
            File.WriteAllText(shared, "{\"points\":{\"" + Point + "\":{\"enabled\":false}}}");
        }

        // ---- the matrix: an on reaches no other identity ---------------------------------------------------------------

        // Every kind of view a model can be opened as. Key = identity: a path file, a title, nothing, or a cloud model.
        private static readonly string[] Views = { "N", "T:Sales", "T:Stock", "P:east", "Pn:east", "P:west", "P:stock", "Cloud" };

        private static async Task OpenViewAsync(DecisionFixture f, string view)
        {
            switch (view)
            {
                case "N": await OpenDesktopAsync(f); break;
                case "T:Sales": await OpenDesktopAsync(f, "Sales"); break;
                case "T:Stock": await OpenDesktopAsync(f, "Stock"); break;
                case "P:east": await OpenDesktopAsync(f, "Sales", East(f)); break;
                case "Pn:east": await OpenDesktopAsync(f, null, East(f)); break;
                case "P:west": await OpenDesktopAsync(f, "Sales", West(f)); break;
                case "P:stock": await OpenDesktopAsync(f, "Stock", StockPath(f)); break;
                case "Cloud":
                    await f.Engine.CreateModelAsync("Live", 1604, discardUnsaved: true);
                    f.Sessions.Current.LiveOrigin = new LiveOrigin(Cloud, "Live", null);
                    break;
                default: throw new ArgumentException(view);
            }
        }

        // Rewritten at the join (item 5, the coordinator's brief, 30 Sep): a Desktop model is known by its window title or by
        // nothing, never by the path on Desktop's command line, so a view's identity is its title, or nothing.
        private static string IdentityOf(string view) => view switch
        {
            "N" or "Pn:east" => "nothing",
            "T:Sales" or "P:east" or "P:west" => "title:sales",
            "T:Stock" or "P:stock" => "title:stock",
            _ => view,
        };

        private static async Task<Dictionary<string, bool>> ReadAllAsync(DecisionFixture f)
        {
            var seen = new Dictionary<string, bool>();
            foreach (var view in Views)
            {
                await OpenViewAsync(f, view);
                seen[view] = (await Row(f)).Enabled;
            }
            await f.Engine.CreateModelAsync("Disk", 1604, discardUnsaved: true);
            await f.SaveAsAsync("Disk" + Guid.NewGuid().ToString("N") + ".bim");
            seen["Saved"] = (await Row(f)).Enabled;
            return seen;
        }

        public static IEnumerable<object[]> Cells()
        {
            var offs = new[] { "none", "old", "N", "T:Sales", "P:east", "P:west", "P:stock", "Cloud" };
            var i = 0;
            foreach (var off in offs)
                foreach (var clearer in Views)
                    yield return new object[] { off, clearer, i++ % 2 == 0 ? "studio" : "agent" };
        }

        [Theory]
        [MemberData(nameof(Cells))]
        public async Task An_on_for_one_identity_reaches_no_other_identity(string off, string clearer, string door)
        {
            using var f = new DecisionFixture();
            if (off == "old") WriteOldOff(f);
            else if (off != "none")
            {
                await OpenViewAsync(f, off);
                await Set(f, "agent", false);
            }
            await OpenViewAsync(f, clearer);
            var before = await ReadAllAsync(f);

            await OpenViewAsync(f, clearer);
            var on = (await Set(f, door, true)).Single(p => p.Name == Point);
            Assert.True(on.Enabled);

            var after = await ReadAllAsync(f);
            foreach (var view in before.Keys)
            {
                if (view != "Saved" && IdentityOf(view) == IdentityOf(clearer)) continue;
                if (before[view]) continue;
                Assert.False(after[view], "an on for " + clearer + " (off left by " + off + ") switched the point back on for " + view + "; the row said: " + on.Note);
            }
            await OpenViewAsync(f, clearer);
            Assert.True((await Row(f, door)).Enabled, "the identity that switched it on reads off again in a later run (" + clearer + ", off " + off + ")");
        }

        // ---- the same identity reads its own on, in a later run, past every shared off ----------------------------------

        [Theory]
        [InlineData("N", "studio")]
        [InlineData("T:Sales", "agent")]
        [InlineData("P:east", "studio")]
        public async Task An_on_for_an_identity_beats_every_shared_off_when_that_identity_reads_again(string view, string door)
        {
            using var f = new DecisionFixture();
            WriteOldOff(f);
            await OpenViewAsync(f, "N");                       // an off a model known by nothing left
            await Set(f, "agent", false);
            await OpenViewAsync(f, "T:Sales");                 // an off from the recent list
            await Set(f, "agent", false);
            await OpenViewAsync(f, "P:west");                  // another file's off
            await Set(f, "agent", false);

            await OpenViewAsync(f, view);
            Assert.False((await Row(f, door)).Enabled);
            await Set(f, door, true);
            await OpenViewAsync(f, view);
            var row = await Row(f, door);
            Assert.True(row.Enabled);
            Assert.Null(row.Note);
        }

        // ---- a newer off wins over an older on ---------------------------------------------------------------------------

        [Fact]
        public async Task An_off_switched_after_an_on_wins_for_the_models_that_might_be_that_file()
        {
            using var f = new DecisionFixture();
            await OpenViewAsync(f, "N");
            await Set(f, "agent", false);
            await Set(f, "agent", true);                       // on for models known by nothing
            await OpenViewAsync(f, "P:stock");
            await Set(f, "agent", false);                      // Stock switched off afterwards
            await OpenViewAsync(f, "N");                       // this model might be Stock
            Assert.False((await Row(f)).Enabled);

            await OpenViewAsync(f, "T:Sales");
            await Set(f, "agent", false);
            await Set(f, "agent", true);                       // on for files opened by the name Sales
            await OpenViewAsync(f, "P:west");
            await Set(f, "agent", false);                      // a Sales.pbix by its path switched off afterwards
            await OpenViewAsync(f, "T:Sales");
            Assert.False((await Row(f)).Enabled);
        }

        // ---- what the row says -------------------------------------------------------------------------------------------

        [Theory]
        [InlineData("P:east", PathOn, "studio", Skip = "Void under the join's ruling (item 5, the coordinator's brief, 30 Sep): Power BI Desktop's command-line path is never a model's identity, so P:east is the Sales title and its row says what T:Sales says.")]
        [InlineData("T:Sales", TitleOn, "agent")]
        [InlineData("N", NothingOn, "studio")]
        public async Task The_row_of_an_on_says_only_what_the_on_did(string view, string say, string door)
        {
            using var f = new DecisionFixture();
            WriteOldOff(f);
            await OpenViewAsync(f, view);
            Assert.False((await Row(f, door)).Enabled);
            var row = (await Set(f, door, true)).Single(p => p.Name == Point);
            Assert.Equal(say, row.Note);
            Assert.DoesNotContain("\u2014", row.Note);
            Assert.DoesNotContain("\u2013", row.Note);
        }

        [Fact(Skip = "Void under the join's ruling (item 5, the coordinator's brief, 30 Sep): Power BI Desktop's command-line path is never a model's identity, so there is no file view; the title view's note is pinned by DecisionSwitchScopeR6Tests and DecisionSwitchScopeJoinTests.")]
        public async Task An_on_after_the_files_own_off_names_the_views_that_still_read_that_off()
        {
            using var f = new DecisionFixture();
            await OpenViewAsync(f, "P:east");
            await Set(f, "agent", false);
            var row = (await Set(f, "agent", true)).Single(p => p.Name == Point);
            Assert.True(row.Enabled);
            Assert.Equal(RetiredOnForThisFileAfterItsOwnOffNote, row.Note);
        }

        [Fact]
        public async Task An_on_writes_no_shared_file_and_removes_no_off_that_is_not_its_own()
        {
            using var f = new DecisionFixture();
            WriteOldOff(f);
            await OpenViewAsync(f, "N");
            await Set(f, "agent", false);
            await OpenViewAsync(f, "T:Sales");
            await Set(f, "agent", false);
            await OpenViewAsync(f, "P:west");
            await Set(f, "agent", false);
            string Snapshot() => string.Join("\n", Directory.GetFiles(Path.Combine(f.Workspace, LayoutStore.DirName), "*.json", SearchOption.AllDirectories)
                .Where(p => !Path.GetFileName(p).StartsWith("live-") && !Path.GetFileName(p).StartsWith("local-") && !Path.GetFileName(p).StartsWith("model-")
                            && !Path.GetFileName(p).Contains("-on"))
                .OrderBy(p => p, StringComparer.Ordinal).Select(p => p + "=" + File.ReadAllText(p)));
            var before = Snapshot();
            await OpenViewAsync(f, "P:east");
            await Set(f, "agent", true);
            await OpenViewAsync(f, "T:Stock");
            await Set(f, "agent", true);
            await OpenViewAsync(f, "N");
            await Set(f, "agent", true);
            Assert.Equal(before, Snapshot());
        }

        // ---- files written by rounds 3 and 4 --------------------------------------------------------------------------

        [Fact]
        public async Task An_old_lift_or_clearedFor_entry_turns_nothing_on_for_another_model()
        {
            using var f = new DecisionFixture();
            WriteOldOff(f);
            Directory.CreateDirectory(Folder(f));
            File.WriteAllText(Path.Combine(Folder(f), "desktop-earlier-lifted.json"),
                "{\"points\":{\"" + Point + "\":{\"enabled\":true}}}");
            await OpenViewAsync(f, "P:stock");
            Assert.False((await Row(f)).Enabled);
            await OpenViewAsync(f, "N");
            Assert.False((await Row(f)).Enabled);
            File.WriteAllText(Path.Combine(Folder(f), "desktop-earlier-lifted.json"),
                "{\"points\":{\"" + Point + "\":{\"enabled\":true,\"scopes\":[\"any\",\"title:zz\"]}}}");
            await OpenViewAsync(f, "T:Stock");
            Assert.False((await Row(f)).Enabled);

            File.Delete(Path.Combine(Folder(f), "desktop-earlier-lifted.json"));
            File.Delete(Path.Combine(f.Workspace, LayoutStore.DirName, LayoutStore.DecisionSettingsFile));
            File.WriteAllText(Path.Combine(Folder(f), "desktop-unnamed.json"),
                "{\"points\":{\"" + Point + "\":{\"enabled\":false,\"offFor\":[\"run:abc\"],\"clearedFor\":[\"any\",\"title:zz\",\"pathtitle:zz\"]}}}");
            await OpenViewAsync(f, "P:stock");
            Assert.False((await Row(f)).Enabled);
            await OpenViewAsync(f, "T:Stock");
            Assert.False((await Row(f)).Enabled);
            await OpenViewAsync(f, "N");
            Assert.False((await Row(f)).Enabled);
        }

        // ---- F3: Microsoft Store and Report Server Power BI Desktop -------------------------------------------------------

        private static string Profile(DecisionFixture f) => Path.Combine(f.Home, "profile");
        private static string LocalAppData(DecisionFixture f) => Path.Combine(Profile(f), "AppData", "Local");
        private static string Root(DecisionFixture f, string layout) => layout switch
        {
            "installer" => Path.Combine(LocalAppData(f), "Microsoft", "Power BI Desktop", "AnalysisServicesWorkspaces"),
            "store" => Path.Combine(Profile(f), "Microsoft", "Power BI Desktop Store App", "AnalysisServicesWorkspaces"),
            "reportserver" => Path.Combine(LocalAppData(f), "Microsoft", "Power BI Desktop SSRS", "AnalysisServicesWorkspaces"),
            _ => throw new ArgumentException(layout),
        };

        private static string DesktopStarts(string root, int port)
        {
            var ws = Path.Combine(root, "AnalysisServicesWorkspace_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(ws, "Data"));
            File.WriteAllText(Path.Combine(ws, "Data", "msmdsrv.port.txt"), port.ToString(), Encoding.Unicode);
            return ws;
        }

        private static LiveOrigin Stamp(DecisionFixture f, int port, Func<IReadOnlyList<LocalDesktop.ProcInfo>> processes = null)
            => LocalEngine.LocalOriginFor("localhost:" + port, Guid.NewGuid().ToString(), null, null,
                LocalDesktop.WorkspaceRoots(LocalAppData(f), Profile(f)), processes ?? (() => new List<LocalDesktop.ProcInfo>()));

        [Fact]
        public void The_documented_Desktop_workspace_folders_are_the_installer_the_Store_app_and_Report_Server()
        {
            var roots = LocalDesktop.WorkspaceRoots(Path.Combine("C:", "Users", "kane", "AppData", "Local"), Path.Combine("C:", "Users", "kane"));
            Assert.Equal(new[]
            {
                Path.Combine("C:", "Users", "kane", "AppData", "Local", "Microsoft", "Power BI Desktop", "AnalysisServicesWorkspaces"),
                Path.Combine("C:", "Users", "kane", "Microsoft", "Power BI Desktop Store App", "AnalysisServicesWorkspaces"),
                Path.Combine("C:", "Users", "kane", "AppData", "Local", "Microsoft", "Power BI Desktop Store App", "AnalysisServicesWorkspaces"),
                Path.Combine("C:", "Users", "kane", "AppData", "Local", "Microsoft", "Power BI Desktop SSRS", "AnalysisServicesWorkspaces"),
            }, roots.ToArray());
        }

        [Theory]
        [InlineData("installer", "studio")]
        [InlineData("store", "studio")]
        [InlineData("store", "agent")]
        [InlineData("reportserver", "studio")]
        [InlineData("reportserver", "agent")]
        public async Task An_off_set_in_any_edition_of_Power_BI_Desktop_holds_after_Desktop_restarts(string layout, string door)
        {
            using var f = new DecisionFixture();
            var first = DesktopStarts(Root(f, layout), 52555);
            await f.Engine.CreateModelAsync("Sales", 1604, discardUnsaved: true);
            var day1 = Stamp(f, 52555);
            Assert.True(day1.LocalDesktop, layout + " was stamped as Analysis Services");
            f.Sessions.Current.LiveOrigin = day1;
            Assert.False((await Set(f, door, false)).Single(p => p.Name == Point).Enabled);
            Directory.Delete(first, true);

            DesktopStarts(Root(f, layout), 52556);
            await f.Engine.CreateModelAsync("Sales", 1604, discardUnsaved: true);
            f.Sessions.Current.LiveOrigin = Stamp(f, 52556);
            var day2 = await Row(f, door);
            Assert.False(day2.Enabled);
            Assert.NotNull(day2.Note);
        }

        private static List<LocalDesktop.ProcInfo> Processes(bool desktopRunning, bool serviceRunning)
        {
            var list = new List<LocalDesktop.ProcInfo>();
            if (desktopRunning)
            {
                list.Add(new LocalDesktop.ProcInfo { Pid = 10, ParentPid = 1, Name = "PBIDesktop.exe", Title = "Sales - Power BI Desktop" });
                list.Add(new LocalDesktop.ProcInfo { Pid = 11, ParentPid = 10, Name = "msmdsrv.exe", CommandLine = "\"msmdsrv.exe\" -c -s \"C:\\Users\\k\\Odd Place\\AnalysisServicesWorkspace_1\\Data\"" });
            }
            if (serviceRunning)
                list.Add(new LocalDesktop.ProcInfo { Pid = 20, ParentPid = 4, Name = "msmdsrv.exe", CommandLine = "\"C:\\Program Files\\Microsoft SQL Server\\MSAS16.MSSQLSERVER\\OLAP\\bin\\msmdsrv.exe\" -s \"C:\\Program Files\\Microsoft SQL Server\\MSAS16.MSSQLSERVER\\OLAP\\Config\"" });
            return list;
        }

        [Fact]
        public async Task An_instance_no_known_folder_places_is_a_Desktop_model_known_by_nothing_while_a_Desktop_runs()
        {
            using var f = new DecisionFixture();
            var origin = Stamp(f, 52777, () => Processes(desktopRunning: true, serviceRunning: true));
            Assert.True(origin.LocalDesktop);
            Assert.Null(origin.LocalName);
            await f.Engine.CreateModelAsync("Odd", 1604, discardUnsaved: true);
            f.Sessions.Current.LiveOrigin = origin;
            await Set(f, "agent", false);
            await OpenDesktopAsync(f);                          // any Desktop model known by nothing reads that off
            Assert.False((await Row(f)).Enabled);
        }

        [Fact]
        public async Task An_instance_is_Analysis_Services_only_when_no_Desktop_owns_a_running_server()
        {
            using var f = new DecisionFixture();
            Assert.False(Stamp(f, 2383, () => Processes(desktopRunning: false, serviceRunning: true)).LocalDesktop);
            Assert.True(Stamp(f, 2383, () => Processes(desktopRunning: true, serviceRunning: true)).LocalDesktop);
            Assert.True(Stamp(f, 2383, () => new List<LocalDesktop.ProcInfo>()).LocalDesktop);   // nothing can be proved: never Analysis Services
            Assert.False(LocalEngine.LocalOriginFor("localhost", "db", null, null, new string[0], () => Processes(true, true)).LocalDesktop);   // no port: a named instance
        }

        [Fact]
        public void The_identity_ladder_reads_a_Report_Server_Desktop_as_a_Desktop()
        {
            var ws = "C:\\Users\\k\\AppData\\Local\\Microsoft\\Power BI Desktop SSRS\\AnalysisServicesWorkspaces\\AnalysisServicesWorkspace_9";
            var id = LocalDesktop.Resolve(ws, new List<LocalDesktop.ProcInfo>
            {
                new LocalDesktop.ProcInfo { Pid = 1, ParentPid = 0, Name = "PBIDesktopRS.exe", Title = "Sales - Power BI Desktop", CommandLine = "\"C:\\Program Files\\Microsoft Power BI Desktop RS\\bin\\PBIDesktopRS.exe\" \"C:\\r\\Sales.pbix\"" },
                new LocalDesktop.ProcInfo { Pid = 2, ParentPid = 1, Name = "msmdsrv.exe", CommandLine = "msmdsrv.exe -c -s \"" + ws + "\\Data\"" },
            });
            Assert.NotNull(id);
            // Rewritten at the join (item 5, 30 Sep): the identity is the window title, never the command line's path.
            Assert.Equal("Sales", id.Stem);
        }
    }
}
