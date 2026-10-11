// review-fix round 4 for m11/switch-scope: written first, red first (see the fix report for the run).
//
// Round 4 said an on clears the offs of the models the clearer cannot be told apart from. Round 5 (ruled 30 Sep)
// replaced that: an on never clears anything. It is kept for the identity that clicked and reaches no other; the row
// says what it left alone; a file Desktop opens by its path keeps its own off with a reason on the row. The tests below
// keep round 4's scenarios and pin what they mean now.

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Decisions;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    public sealed class DecisionSwitchScopeR4Tests
    {
        /// <summary>The engine's old note for a file known by its path, retired at the join (item 5, 30 Sep): Semanticus no
        /// longer knows a Desktop file by its path. Kept here only so the skipped fact that read it still says what it read.</summary>
        private const string RetiredOnForThisFileAfterItsOwnOffNote = "On for this file. The same file opened another way, such as from Desktop's recent list, may still be off from when this file was switched off, until you switch it on there.";

        private const string Point = DecisionRegistry.MeasureFolderAndFormat;
        private const string Cloud = "powerbi://api.powerbi.com/v1.0/myorg/Contoso Live";
        private const string ForThisFile = "On for this file. Other Power BI Desktop files that were switched off together with it stay off until you switch them on there.";
        // The join (item 5, 30 Sep) reworded both notes to say which models an on reaches; these read the engine's own words now.
        private const string ForThisName = LocalEngine.OnForFilesWithThisNameNote;
        private const string ForNoName = LocalEngine.OnForModelsWithNoNameNote;
        private const string FileOff = "Off because it was switched off for this file.";
        private const string CarriedOff = "Off because it was switched off for a model Semanticus took to be this one before it was saved.";

        private static async Task OpenDesktopAsync(DecisionFixture f, string port, string title = null, string path = null)
        {
            await f.Engine.CreateModelAsync(title ?? "Model", 1604, discardUnsaved: true);
            f.Sessions.Current.LiveOrigin = new LiveOrigin("localhost:" + port, Guid.NewGuid().ToString(), null,
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

        private static void WriteOldOff(DecisionFixture f)
        {
            var shared = Path.Combine(f.Workspace, LayoutStore.DirName, LayoutStore.DecisionSettingsFile);
            Directory.CreateDirectory(Path.GetDirectoryName(shared));
            File.WriteAllText(shared, "{\"points\":{\"" + Point + "\":{\"enabled\":false}}}");
        }

        private static string Sales(DecisionFixture f) => Path.Combine(f.Home, "east", "Sales.pbix");
        private static string Stock(DecisionFixture f) => Path.Combine(f.Home, "west", "Stock.pbix");

        // Every kind of Desktop view that could read the shared off: it reports what each one reads now.
        private static async Task<string> Reads(DecisionFixture f)
        {
            var seen = "";
            async Task Look(string label, string title, string path)
            {
                await OpenDesktopAsync(f, (52100 + seen.Length).ToString(), title, path);
                seen += label + "=" + ((await Row(f)).Enabled ? "on" : "off") + ";";
            }
            await Look("nothing", null, null);
            await Look("SalesTitle", "Sales", null);
            await Look("SalesPath", "Sales", Sales(f));
            await Look("StockTitle", "Stock", null);
            await Look("StockPath", "Stock", Stock(f));
            await Look("LedgerTitle", "Ledger", null);
            return seen;
        }

        // ---- F1: the workspace's old off ----------------------------------------------------------------------------

        [Theory]
        [InlineData("title", "studio")]
        [InlineData("path", "agent", Skip = "Void under the join's ruling (item 5, the coordinator's brief, 30 Sep): Power BI Desktop's command-line path is never a model's identity, so a file is not known by its path and the path view is the title view.")]
        public async Task An_on_for_a_Desktop_file_lifts_the_old_off_for_no_one_else(string by, string door)
        {
            using var f = new DecisionFixture();
            WriteOldOff(f);
            await OpenDesktopAsync(f, "52001", "Sales", by == "path" ? Sales(f) : null);
            Assert.False((await Row(f, door)).Enabled);
            var row = (await Set(f, door, true)).Single(p => p.Name == Point);
            Assert.Equal(by == "path" ? ForThisFile : ForThisName, row.Note);

            var seen = await Reads(f);
            // Only the identity that switched it on reads on: a title, or the file's path. The old off stays for every other.
            Assert.Equal(by == "path"
                ? "nothing=off;SalesTitle=off;SalesPath=on;StockTitle=off;StockPath=off;LedgerTitle=off;"
                : "nothing=off;SalesTitle=on;SalesPath=on;StockTitle=off;StockPath=off;LedgerTitle=off;", seen);   // the join: SalesPath is the Sales title
        }

        [Fact]
        public async Task An_on_for_a_model_known_by_nothing_is_for_models_known_by_nothing_and_says_so()
        {
            using var f = new DecisionFixture();
            WriteOldOff(f);
            await OpenDesktopAsync(f, "52001");
            var row = (await Set(f, "agent", true)).Single(p => p.Name == Point);
            Assert.Equal(ForNoName, row.Note);
            Assert.Equal("nothing=on;SalesTitle=off;SalesPath=off;StockTitle=off;StockPath=off;LedgerTitle=off;", await Reads(f));

            // A cloud model and a saved model still read the workspace's old off.
            await f.Engine.CreateModelAsync("Live", 1604, discardUnsaved: true);
            f.Sessions.Current.LiveOrigin = new LiveOrigin(Cloud, "Live", null);
            Assert.False((await Row(f)).Enabled);
            await f.Engine.CreateModelAsync("Disk", 1604, discardUnsaved: true);
            await f.SaveAsAsync("Disk.bim");
            Assert.False((await Row(f)).Enabled);
        }

        [Fact(Skip = "Void under the join's ruling (item 5, the coordinator's brief, 30 Sep): Power BI Desktop's command-line path is never a model's identity, so its path-known Sales and title-only Sales are one identity; restart and the untouched old off are held by the matrix (RESTART flags) and An_on_for_a_model_known_by_nothing_is_for_models_known_by_nothing_and_says_so.")]
        public async Task An_on_survives_a_restart_for_its_own_identity_only_and_the_old_off_is_never_written()
        {
            using var f = new DecisionFixture();
            WriteOldOff(f);
            var shared = Path.Combine(f.Workspace, LayoutStore.DirName, LayoutStore.DecisionSettingsFile);
            var before = File.ReadAllText(shared);
            await OpenDesktopAsync(f, "52001", "Sales", Sales(f));
            await Set(f, "studio", true);
            Assert.Equal(before, File.ReadAllText(shared));

            // A restart is a second engine over the same workspace folder.
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, TestEntitlements.Pro, f.Workspace);
            await engine.CreateModelAsync("Stock", 1604, discardUnsaved: true);
            sessions.Current.LiveOrigin = new LiveOrigin("localhost:53000", Guid.NewGuid().ToString(), null, localName: "Stock", localPath: Stock(f), localDesktop: true);
            Assert.False(engine.DecisionPointEnabled(Point));
            await engine.CreateModelAsync("Sales", 1604, discardUnsaved: true);
            sessions.Current.LiveOrigin = new LiveOrigin("localhost:53001", Guid.NewGuid().ToString(), null, localName: "Sales", localDesktop: true);
            Assert.False(engine.DecisionPointEnabled(Point), "an on for the file by its path switched on a file Desktop named only by title");
            await engine.CreateModelAsync("Sales", 1604, discardUnsaved: true);
            sessions.Current.LiveOrigin = new LiveOrigin("localhost:53002", Guid.NewGuid().ToString(), null, localName: "Sales", localPath: Sales(f), localDesktop: true);
            Assert.True(engine.DecisionPointEnabled(Point));
            Assert.False(File.Exists(Path.Combine(Folder(f), "desktop-earlier-lifted.json")));
        }

        // ---- F1: an off in desktop-unnamed.json ----------------------------------------------------------------------

        [Theory]
        [InlineData("path", "studio", Skip = "Void under the join's ruling (item 5, the coordinator's brief, 30 Sep): Power BI Desktop's command-line path is never a model's identity, so a file is not known by its path and the path view is the title view.")]
        [InlineData("title", "agent")]
        public async Task An_on_for_a_Desktop_file_leaves_an_off_a_model_known_by_nothing_left_exactly_as_it_was(string by, string door)
        {
            using var f = new DecisionFixture();
            await OpenDesktopAsync(f, "52000");
            await Set(f, "agent", false);
            var unnamedFile = Path.Combine(Folder(f), "desktop-unnamed.json");
            var before = File.ReadAllText(unnamedFile);
            await OpenDesktopAsync(f, "52001", "Sales", by == "path" ? Sales(f) : null);
            Assert.False((await Row(f, door)).Enabled);
            var row = (await Set(f, door, true)).Single(p => p.Name == Point);
            Assert.Equal(by == "path" ? ForThisFile : ForThisName, row.Note);

            Assert.Equal(by == "path"
                ? "nothing=off;SalesTitle=off;SalesPath=on;StockTitle=off;StockPath=off;LedgerTitle=off;"
                : "nothing=off;SalesTitle=on;SalesPath=on;StockTitle=off;StockPath=off;LedgerTitle=off;", await Reads(f));   // the join: SalesPath is the Sales title
            Assert.Equal(before, File.ReadAllText(unnamedFile));
        }

        [Fact]
        public async Task A_new_off_from_a_model_known_by_nothing_takes_a_title_on_away()
        {
            using var f = new DecisionFixture();
            await OpenDesktopAsync(f, "52000");
            await Set(f, "agent", false);
            await OpenDesktopAsync(f, "52001", "Sales");
            Assert.False((await Row(f)).Enabled);
            await Set(f, "studio", true);
            await OpenDesktopAsync(f, "52002", "Sales");
            Assert.True((await Row(f)).Enabled);

            await OpenDesktopAsync(f, "52003");   // some file known by nothing, switched off once more
            Assert.False((await Row(f)).Enabled);
            await Set(f, "agent", false);
            await OpenDesktopAsync(f, "52004", "Sales");
            Assert.False((await Row(f)).Enabled, "the newer off did not win over the older on for the title");
        }

        [Fact]
        public async Task An_on_for_a_model_known_by_nothing_leaves_the_unnamed_off_for_every_other_file()
        {
            using var f = new DecisionFixture();
            await OpenDesktopAsync(f, "52000");
            await Set(f, "agent", false);
            await OpenDesktopAsync(f, "52050");   // Desktop started again: this run did not write that off
            var row = (await Set(f, "studio", true)).Single(p => p.Name == Point);
            Assert.Equal(ForNoName, row.Note);
            Assert.Equal("nothing=on;SalesTitle=off;SalesPath=off;StockTitle=off;StockPath=off;LedgerTitle=off;", await Reads(f));
            Assert.Contains(Point, File.ReadAllText(Path.Combine(Folder(f), "desktop-unnamed.json")));
        }

        // ---- M1: a file known by its path keeps its own off, and says so -----------------------------------------------

        [Theory(Skip = "Void under the join's ruling (item 5, the coordinator's brief, 30 Sep): Power BI Desktop's command-line path is never a model's identity, so the double-clicked Sales and the recent-list Sales share the Sales title and its on.")]
        [InlineData("studio")]
        [InlineData("agent")]
        public async Task An_on_made_from_the_recent_list_leaves_the_double_clicked_file_off_and_the_path_view_says_why(string door)
        {
            using var f = new DecisionFixture();
            await OpenDesktopAsync(f, "52001", "Sales", Sales(f));
            await Set(f, door, false);
            await OpenDesktopAsync(f, "52002", "Sales");
            Assert.False((await Row(f, door)).Enabled);
            var on = (await Set(f, door, true)).Single(p => p.Name == Point);
            Assert.Equal(ForThisName, on.Note);

            await OpenDesktopAsync(f, "52003", "Sales");
            Assert.True((await Row(f, door)).Enabled);
            await OpenDesktopAsync(f, "52004", "Sales", Sales(f));
            var path = await Row(f, door);
            Assert.False(path.Enabled);
            Assert.Equal(FileOff, path.Note);

            // The title view's on did not touch the file's own off; switching it on there does.
            var back = (await Set(f, door, true)).Single(p => p.Name == Point);
            Assert.True(back.Enabled);
            Assert.Equal(RetiredOnForThisFileAfterItsOwnOffNote, back.Note);
            await OpenDesktopAsync(f, "52005", "Sales", Sales(f));
            Assert.True((await Row(f, door)).Enabled);
        }

        [Fact(Skip = "Void under the join's ruling (item 5, the coordinator's brief, 30 Sep): Power BI Desktop's command-line path is never a model's identity, so no file reads off \"for this file\" by its path any more.")]
        public async Task A_file_switched_off_by_its_path_reads_off_with_its_reason()
        {
            using var f = new DecisionFixture();
            await OpenDesktopAsync(f, "52001", "Sales", Sales(f));
            var off = (await Set(f, "studio", false)).Single(p => p.Name == Point);
            Assert.False(off.Enabled);
            Assert.Equal(FileOff, off.Note);
            await OpenDesktopAsync(f, "52002", "Sales", Sales(f));
            var row = await Row(f, "agent");
            Assert.False(row.Enabled);
            Assert.Equal(FileOff, row.Note);
        }

        [Fact(Skip = "Void under the join's ruling (item 5, the coordinator's brief, 30 Sep): Power BI Desktop's command-line path is never a model's identity, so a file known only by its path (no title) is known by nothing and shares that on.")]
        public async Task An_on_made_when_Desktop_names_nothing_leaves_the_double_clicked_file_off()
        {
            using var f = new DecisionFixture();
            await OpenDesktopAsync(f, "52001", path: Sales(f));
            await Set(f, "agent", false);
            await OpenDesktopAsync(f, "61234");
            Assert.False((await Row(f)).Enabled);
            var on = (await Set(f, "agent", true)).Single(p => p.Name == Point);
            Assert.Equal(ForNoName, on.Note);
            await OpenDesktopAsync(f, "63000", path: Sales(f));
            var row = await Row(f);
            Assert.False(row.Enabled);
            Assert.Equal(FileOff, row.Note);
        }

        // ---- LOW A: an on also clears its twin record in desktop-any.json ----------------------------------------------

        [Fact(Skip = "Void under the join's ruling (item 5, the coordinator's brief, 30 Sep): Power BI Desktop's command-line path is never a model's identity, so an on for a Sales window reaches every Sales window (DecisionSwitchScopeJoinTests, the matrix).")]
        public async Task An_on_made_for_the_file_by_path_reaches_neither_its_title_view_nor_a_model_known_by_nothing()
        {
            using var f = new DecisionFixture();
            await OpenDesktopAsync(f, "52001", "Sales");
            await Set(f, "studio", false);
            await OpenDesktopAsync(f, "61234", "Sales", Sales(f));
            Assert.False((await Row(f, "studio")).Enabled);
            await Set(f, "studio", true);
            await OpenDesktopAsync(f, "63000", "Sales");
            Assert.False((await Row(f, "studio")).Enabled, "an on for the file by its path switched on a title-only view");
            await OpenDesktopAsync(f, "64000");
            Assert.False((await Row(f, "studio")).Enabled, "an on for the file by its path switched on a model known by nothing");
            await OpenDesktopAsync(f, "65000", "Sales", Sales(f));
            Assert.True((await Row(f, "studio")).Enabled);
        }

        [Fact]
        public async Task An_on_made_for_a_title_only_file_lasts_for_that_title_and_does_not_reach_a_model_known_by_nothing()
        {
            using var f = new DecisionFixture();
            await OpenDesktopAsync(f, "52001", "Sales");
            await Set(f, "agent", false);
            await OpenDesktopAsync(f, "52002", "Sales");
            await Set(f, "agent", true);
            await OpenDesktopAsync(f, "52003", "Sales");
            Assert.True((await Row(f)).Enabled);
            await OpenDesktopAsync(f, "52004");
            var nothing = await Row(f);
            Assert.False(nothing.Enabled, "an on for a title switched on a model known by nothing");
        }

        // ---- LOW B: a local Analysis Services model is not a Power BI Desktop model -------------------------------------

        [Fact]
        public void A_local_data_source_is_a_Desktop_instance_only_when_a_Desktop_workspace_owns_its_port()
        {
            var root = Path.Combine(Path.GetTempPath(), "sem-ws-" + Guid.NewGuid().ToString("N"));
            try
            {
                var data = Path.Combine(root, "AnalysisServicesWorkspace_1", "Data");
                Directory.CreateDirectory(data);
                File.WriteAllText(Path.Combine(data, "msmdsrv.port.txt"), "52123", System.Text.Encoding.Unicode);
                Assert.True(LocalDesktop.IsDesktopInstance("localhost:52123", root));
                Assert.False(LocalDesktop.IsDesktopInstance("localhost:2383", root));
                Assert.False(LocalDesktop.IsDesktopInstance("localhost:52123", Path.Combine(root, "nowhere")));
                Assert.False(LocalDesktop.IsDesktopInstance(null, root));
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }

        [Fact]
        public void A_local_open_is_stamped_a_Desktop_model_only_for_a_Desktop_instance()
        {
            var root = Path.Combine(Path.GetTempPath(), "sem-ws-" + Guid.NewGuid().ToString("N"));
            try
            {
                var data = Path.Combine(root, "AnalysisServicesWorkspace_1", "Data");
                Directory.CreateDirectory(data);
                File.WriteAllText(Path.Combine(data, "msmdsrv.port.txt"), "52123", System.Text.Encoding.Unicode);
                var desktop = LocalEngine.LocalOriginFor("localhost:52123", "db", null, root);
                Assert.True(desktop.LocalDesktop);
                var server = new System.Collections.Generic.List<LocalDesktop.ProcInfo> { new LocalDesktop.ProcInfo { Pid = 5, ParentPid = 1, Name = "msmdsrv.exe" } };
                var analysisServices = LocalEngine.LocalOriginFor("localhost:2383", "db", null, root, null, () => server);
                Assert.False(analysisServices.LocalDesktop);
                Assert.Null(analysisServices.LocalName);
                Assert.Null(analysisServices.LocalPath);
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }

        [Fact]
        public async Task A_local_Analysis_Services_model_never_reads_or_clears_a_Desktop_off()
        {
            using var f = new DecisionFixture();
            await OpenDesktopAsync(f, "52000");
            await Set(f, "agent", false);                               // an off a Desktop model known by nothing left
            await f.Engine.CreateModelAsync("Server", 1604, discardUnsaved: true);
            f.Sessions.Current.LiveOrigin = new LiveOrigin("localhost:2383", "Server", null);
            Assert.True((await Row(f)).Enabled);
            await Set(f, "agent", false);
            var on = (await Set(f, "agent", true)).Single(p => p.Name == Point);
            Assert.Null(on.Note);
            await OpenDesktopAsync(f, "52001");
            Assert.False((await Row(f)).Enabled);                       // the Desktop off is still there
        }

        // ---- LOW F: a first save that turns a neighbour's off into the model's own says so ------------------------------

        [Fact]
        public async Task A_first_save_that_makes_an_off_read_next_door_the_models_own_says_so_in_a_note()
        {
            using var f = new DecisionFixture();
            WriteOldOff(f);
            await f.Engine.CreateModelAsync("Fresh", 1604, discardUnsaved: true);
            var before = await Row(f);
            Assert.False(before.Enabled);
            Assert.NotNull(before.Note);
            await f.SaveAsAsync("Fresh.bim");
            var after = await Row(f);
            Assert.False(after.Enabled);
            Assert.Equal(CarriedOff, after.Note);

            // The person's own switch replaces the note.
            var on = (await Set(f, "studio", true)).Single(p => p.Name == Point);
            Assert.True(on.Enabled);
            var off = (await Set(f, "studio", false)).Single(p => p.Name == Point);
            Assert.False(off.Enabled);
            Assert.Null(off.Note);
        }

        [Fact]
        public async Task A_first_save_of_a_model_with_its_own_off_adds_no_note()
        {
            using var f = new DecisionFixture();
            await f.Engine.CreateModelAsync("Fresh", 1604, discardUnsaved: true);
            await Set(f, "agent", false);
            await f.SaveAsAsync("Fresh.bim");
            var after = await Row(f);
            Assert.False(after.Enabled);
            Assert.Null(after.Note);
        }

        // ---- LOW F: the files that mean nothing after Desktop closes are not committed ----------------------------------

        [Fact]
        public async Task The_per_run_and_shared_Desktop_files_are_ignored_by_git_in_the_workspace_folder()
        {
            using var f = new DecisionFixture();
            await OpenDesktopAsync(f, "52001", "Sales");
            await Set(f, "agent", false);
            var ignore = Path.Combine(f.Workspace, LayoutStore.DirName, ".gitignore");
            var lines = File.ReadAllText(ignore).Split('\n').Select(l => l.Trim()).ToList();
            Assert.Contains("decision-settings/live-*.json", lines);
            Assert.Contains("decision-settings/desktop-*.json", lines);
            var once = File.ReadAllText(ignore);
            await Set(f, "agent", true);
            await Set(f, "agent", false);
            Assert.Equal(once, File.ReadAllText(ignore));
        }

        [Fact]
        public async Task An_existing_ignore_file_gets_the_switch_lines_added_once_and_keeps_its_own()
        {
            using var f = new DecisionFixture();
            var sidecar = Path.Combine(f.Workspace, LayoutStore.DirName);
            Directory.CreateDirectory(sidecar);
            File.WriteAllText(Path.Combine(sidecar, ".gitignore"), "engine.lock\nmine.txt");
            await OpenDesktopAsync(f, "52001");
            await Set(f, "agent", false);
            var lines = File.ReadAllText(Path.Combine(sidecar, ".gitignore")).Split('\n').Select(l => l.Trim()).ToList();
            Assert.Contains("mine.txt", lines);
            Assert.Equal(1, lines.Count(l => l == "decision-settings/live-*.json"));
        }
    }
}
