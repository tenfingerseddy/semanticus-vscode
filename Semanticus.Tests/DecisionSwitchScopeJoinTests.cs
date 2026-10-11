// join part 2, item 5 (the coordinator's brief, 30 Sep): Power BI Desktop's command-line path is NEVER a model's identity.
//
// Desktop keeps the file it was started with on its command line after the person opens another file in the same window
// (round 5's premise), so a path read from there can name a file the window no longer shows (the switch-scope verifier,
// round 6, F2b: a same-named copy in another folder, or a file whose title Semanticus cannot read). A Desktop model is
// known by its window title or by nothing. So an on reaches every model Semanticus cannot tell apart from the one clicked
// (every Desktop file with that name, or every Desktop model it cannot name), the row says so, and an off still reaches
// every model it could be.

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
    public sealed class DecisionSwitchScopeJoinTests
    {
        private const string Point = DecisionRegistry.MeasureFolderAndFormat;

        private static Task<DecisionPointInfo[]> SetAsync(DecisionFixture f, string door, bool enabled)
            => door == "studio" ? new EngineRpcTarget(f.Engine).setDecisionPointEnabled(Point, enabled)
             : McpTools.SetDecisionPointEnabled(f.Engine, Point, enabled);

        private static async Task<DecisionPointInfo> RowAsync(DecisionFixture f, string door)
        {
            var points = door == "studio" ? await new EngineRpcTarget(f.Engine).getDecisionPoints() : await McpTools.GetDecisionPoints(f.Engine);
            var row = points.Single(p => p.Name == Point);
            Assert.Equal(row.Enabled, f.Engine.DecisionPointEnabled(Point));
            return row;
        }

        private sealed class Machine
        {
            public string Profile, LocalAppData;
            public int Port = 54000;
            public string InstallerRoot => Path.Combine(LocalAppData, "Microsoft", "Power BI Desktop", "AnalysisServicesWorkspaces");
        }

        private static Machine NewMachine(DecisionFixture f)
        {
            var m = new Machine { Profile = Path.Combine(f.Home, "profile") };
            m.LocalAppData = Path.Combine(m.Profile, "AppData", "Local");
            return m;
        }

        private static string DesktopStarts(string root, int port)
        {
            var ws = Path.Combine(root, "AnalysisServicesWorkspace_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(ws, "Data"));
            File.WriteAllText(Path.Combine(ws, "Data", "msmdsrv.port.txt"), port.ToString(), Encoding.Unicode);
            return ws;
        }

        /// <summary>A Desktop run started with <paramref name="launched"/> (null: from Desktop's own Open dialog) whose window title
        /// is <paramref name="title"/>, stamped the way open_local stamps it: LocalDesktop.Resolve over the process list.</summary>
        private static LiveOrigin StampWindow(Machine m, string launched, string title, out string dataSource)
        {
            var port = m.Port++;
            var ws = DesktopStarts(m.InstallerRoot, port);
            var exe = "\"C:\\Program Files\\Microsoft Power BI Desktop\\bin\\PBIDesktop.exe\"";
            var id = LocalDesktop.Resolve(ws, new List<LocalDesktop.ProcInfo>
            {
                new LocalDesktop.ProcInfo { Pid = 30, ParentPid = 1, Name = "PBIDesktop.exe", Title = title,
                    CommandLine = launched == null ? exe : exe + " \"" + launched + "\"" },
                new LocalDesktop.ProcInfo { Pid = 31, ParentPid = 30, Name = "msmdsrv.exe", CommandLine = "\"msmdsrv.exe\" -c -s \"" + ws + "\\Data\"" },
            });
            dataSource = "localhost:" + port;
            return LocalEngine.LocalOriginFor(dataSource, Guid.NewGuid().ToString(), id, null,
                LocalDesktop.WorkspaceRoots(m.LocalAppData, m.Profile), () => new List<LocalDesktop.ProcInfo>());
        }

        private static async Task OpenWindowAsync(DecisionFixture f, Machine m, string launched, string title, string modelName)
        {
            var origin = StampWindow(m, launched, title, out _);
            await f.Engine.CreateModelAsync(modelName, 1604, discardUnsaved: true);
            f.Sessions.Current.LiveOrigin = origin;
            Assert.True(origin.LocalDesktop);
        }

        private const string East = "C:\\east\\Sales.pbix";
        private const string West = "C:\\west\\Sales.pbix";
        private const string Stock = "C:\\north\\Stock.pbix";
        private const string SalesTitle = "Sales - Power BI Desktop";

        // ---- the identity itself ------------------------------------------------------------------------------------------

        [Fact]
        public void A_Desktop_identity_has_no_path_to_carry()
        {
            // The type itself: nothing a later change can fill in by mistake.
            Assert.Null(typeof(LocalDesktop.DesktopIdentity).GetField("PbixPath"));
            Assert.Null(typeof(LocalDesktop.DesktopIdentity).GetProperty("PbixPath"));
        }

        [Fact]
        public void A_window_started_by_a_file_is_known_by_its_title_not_by_that_files_path()
        {
            var tmp = Path.Combine(Path.GetTempPath(), "sem-join-sw-" + Guid.NewGuid().ToString("N"));
            try
            {
                var m = new Machine { Profile = Path.Combine(tmp, "profile") };
                m.LocalAppData = Path.Combine(m.Profile, "AppData", "Local");
                // Double-clicked east\Sales.pbix, and a window east\Sales.pbix started now showing west\Sales.pbix: the same stamp.
                var east = StampWindow(m, East, SalesTitle, out _);
                Assert.True(east.LocalDesktop);
                Assert.Null(east.LocalPath);
                Assert.Equal("Sales", east.LocalName);
                // A window started by east\Sales.pbix now showing Stock is Stock, by its title.
                var stock = StampWindow(m, East, "Stock - Power BI Desktop", out _);
                Assert.Null(stock.LocalPath);
                Assert.Equal("Stock", stock.LocalName);
            }
            finally { try { Directory.Delete(tmp, true); } catch { } }
        }

        [Theory]
        [InlineData(null)]                                            // no title at all (minimized, access denied)
        [InlineData("   ")]                                           // a blank one
        [InlineData("Sales - Power BI Desktop (Vorschau)")]           // a build whose suffix Semanticus cannot read
        [InlineData("Sales - EU - Power BI Desktop (Vorschau)")]      // another file, under that suffix
        public void A_window_whose_title_cannot_be_read_is_known_by_nothing_whatever_its_command_line_says(string title)
        {
            var tmp = Path.Combine(Path.GetTempPath(), "sem-join-sw-" + Guid.NewGuid().ToString("N"));
            try
            {
                var m = new Machine { Profile = Path.Combine(tmp, "profile") };
                m.LocalAppData = Path.Combine(m.Profile, "AppData", "Local");
                var origin = StampWindow(m, "C:\\Reports\\Sales.pbix", title, out _);
                Assert.True(origin.LocalDesktop);
                Assert.Null(origin.LocalPath);
                Assert.Null(origin.LocalName);
            }
            finally { try { Directory.Delete(tmp, true); } catch { } }
        }

        // ---- an on reaches the models Semanticus cannot tell apart, and says so ---------------------------------------------

        [Theory]
        [InlineData("studio")]
        [InlineData("agent")]
        public async Task A_same_named_copy_in_another_folder_shares_one_identity_and_the_on_says_it_reaches_it(string door)
        {
            using var f = new DecisionFixture();
            var m = NewMachine(f);
            await OpenWindowAsync(f, m, East, SalesTitle, "Sales");                 // day 1: east\Sales.pbix double-clicked
            await McpTools.SetDecisionPointEnabled(f.Engine, Point, false);        // switched off
            await OpenWindowAsync(f, m, West, SalesTitle, "Sales");                 // day 2: west\Sales.pbix double-clicked
            var westBefore = await RowAsync(f, door);
            Assert.False(westBefore.Enabled, "west\\Sales.pbix cannot be told from east\\Sales.pbix, so east's off must reach it");
            var row = (await SetAsync(f, door, true)).Single(p => p.Name == Point);
            Assert.True(row.Enabled);
            Assert.Equal(LocalEngine.OnForFilesWithThisNameNote, row.Note);
            Assert.Contains("every Power BI Desktop file with this name", row.Note);
            Assert.Contains("another folder", row.Note);
            Assert.DoesNotContain("path", row.Note);

            await OpenWindowAsync(f, m, East, SalesTitle, "Sales");                 // day 3: east\Sales.pbix double-clicked
            Assert.True((await RowAsync(f, door)).Enabled, "the on for Sales is for every Desktop file named Sales; its row said so");
            await OpenWindowAsync(f, m, Stock, "Stock - Power BI Desktop", "Stock");
            Assert.True((await RowAsync(f, door)).Enabled);                         // Stock was never off
        }

        [Theory]
        [InlineData("studio")]
        [InlineData("agent")]
        public async Task An_on_for_a_model_known_by_nothing_says_it_reaches_every_model_Semanticus_cannot_name(string door)
        {
            using var f = new DecisionFixture();
            var m = NewMachine(f);
            await OpenWindowAsync(f, m, East, SalesTitle, "Sales");                 // a named Sales switched off
            await McpTools.SetDecisionPointEnabled(f.Engine, Point, false);
            await OpenWindowAsync(f, m, East, null, "Sales");                       // a window whose title cannot be read
            Assert.False((await RowAsync(f, door)).Enabled);
            var row = (await SetAsync(f, door, true)).Single(p => p.Name == Point);
            Assert.True(row.Enabled);
            Assert.Equal(LocalEngine.OnForModelsWithNoNameNote, row.Note);
            Assert.Contains("every Power BI Desktop model whose name Semanticus cannot read", row.Note);

            await OpenWindowAsync(f, m, West, "Sales - Power BI Desktop (Vorschau)", "Sales");   // another nameless model: on
            Assert.True((await RowAsync(f, door)).Enabled);
            await OpenWindowAsync(f, m, East, SalesTitle, "Sales");                              // the named Sales: still off
            var named = await RowAsync(f, door);
            Assert.False(named.Enabled);
        }

        [Theory]
        [InlineData("studio")]
        [InlineData("agent")]
        public async Task The_notes_of_an_on_after_its_own_off_name_the_views_that_may_still_be_off(string door)
        {
            // Named: switched off and back on in one run. Its own off stays next door for nameless models.
            using (var f = new DecisionFixture())
            {
                var m = NewMachine(f);
                await OpenWindowAsync(f, m, East, SalesTitle, "Sales");
                await SetAsync(f, door, false);
                var row = (await SetAsync(f, door, true)).Single(p => p.Name == Point);
                Assert.Equal(LocalEngine.OnForThisNameAfterItsOwnOffNote, row.Note);
                Assert.Contains("every Power BI Desktop file with this name", row.Note);
                Assert.DoesNotContain("path", row.Note);
                await OpenWindowAsync(f, m, West, null, "Sales");
                Assert.False((await RowAsync(f, door)).Enabled, "the note says a model whose name cannot be read may still be off");
                await OpenWindowAsync(f, m, West, SalesTitle, "Sales");
                Assert.True((await RowAsync(f, door)).Enabled, "the note says the on is for every file with this name");
            }
            // Nameless: switched off and back on in one run. Its own off stays next door for named files.
            using (var f = new DecisionFixture())
            {
                var m = NewMachine(f);
                await OpenWindowAsync(f, m, East, null, "Sales");
                await SetAsync(f, door, false);
                var row = (await SetAsync(f, door, true)).Single(p => p.Name == Point);
                Assert.Equal(LocalEngine.OnForNoNameAfterItsOwnOffNote, row.Note);
                Assert.Contains("every Power BI Desktop model whose name Semanticus cannot read", row.Note);
                await OpenWindowAsync(f, m, East, SalesTitle, "Sales");
                Assert.False((await RowAsync(f, door)).Enabled, "the note says a file whose name can be read may still be off");
                await OpenWindowAsync(f, m, West, "   ", "Sales");
                Assert.True((await RowAsync(f, door)).Enabled, "the note says the on is for every model whose name cannot be read");
            }
        }

        // ---- an off keeps failing safe ------------------------------------------------------------------------------------

        [Theory]
        [InlineData("studio")]
        [InlineData("agent")]
        public async Task An_off_for_a_model_known_by_nothing_reaches_every_Desktop_model(string door)
        {
            using var f = new DecisionFixture();
            var m = NewMachine(f);
            await OpenWindowAsync(f, m, East, "Sales - EU - Power BI Desktop (Vorschau)", "Sales - EU");
            await SetAsync(f, door, false);
            foreach (var (launched, title) in new[] { (East, SalesTitle), (West, SalesTitle), ((string)null, "Sales - EU - Power BI Desktop"),
                                                      (Stock, "Stock - Power BI Desktop"), (West, (string)null) })
            {
                await OpenWindowAsync(f, m, launched, title, "Any");
                Assert.False((await RowAsync(f, door)).Enabled, "a model known by nothing may be any Desktop file, so its off reaches " + (title ?? "a nameless model"));
            }
        }

        [Theory]
        [InlineData("studio")]
        [InlineData("agent")]
        public async Task An_off_for_a_named_file_reaches_every_file_with_that_name_and_every_nameless_model_and_no_other_name(string door)
        {
            using var f = new DecisionFixture();
            var m = NewMachine(f);
            await OpenWindowAsync(f, m, West, SalesTitle, "Sales");                 // west\Sales.pbix in a window east started, or west itself
            await SetAsync(f, door, false);
            await OpenWindowAsync(f, m, West, SalesTitle, "Sales");
            Assert.False((await RowAsync(f, door)).Enabled);
            await OpenWindowAsync(f, m, East, SalesTitle, "Sales");
            Assert.False((await RowAsync(f, door)).Enabled);
            await OpenWindowAsync(f, m, East, null, "Sales");
            Assert.False((await RowAsync(f, door)).Enabled);
            await OpenWindowAsync(f, m, West, "Stock - Power BI Desktop", "Stock");  // Semanticus can tell Stock apart by its title
            Assert.True((await RowAsync(f, door)).Enabled);
        }
    }
}
