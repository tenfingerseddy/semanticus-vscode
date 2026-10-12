// A decision point's switch belongs to ONE model, whatever kind of model it is.
//
// The switch is stored beside the model, and a model saved to disk has a folder of its own for that. A model
// opened live, or not saved yet, has none: its switches used to fall back to the workspace's one settings file,
// so every such model shared one switch per point. Switching Jev off for live model A switched it off for live
// model B, and switching it back on for B turned it on again for A, whose owner had switched it off, while the
// engine's own line said "for this model" (the query-split verifier, 28 Sep, section 7). These tests pin the
// per-model key for each kind of model, from both doors.

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Decisions;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    public sealed class DecisionSwitchScopeTests
    {
        private const string Cloud = "powerbi://api.powerbi.com/v1.0/myorg/Contoso Live";

        private static async Task OpenLiveAsync(DecisionFixture f, string database, string endpoint = Cloud,
                                                string localName = null, string localPath = null)
        {
            await f.Engine.CreateModelAsync(database, 1604, discardUnsaved: true);
            f.Sessions.Current.LiveOrigin = new LiveOrigin(endpoint, database, null, localName: localName, localPath: localPath);
        }

        // The two doors, each driving the same engine: Studio over the RPC target, the assistant over MCP.
        private static Task<DecisionPointInfo[]> Set(DecisionFixture f, string door, string point, bool enabled)
            => door == "studio" ? new EngineRpcTarget(f.Engine).setDecisionPointEnabled(point, enabled)
             : McpTools.SetDecisionPointEnabled(f.Engine, point, enabled);

        private static async Task<bool> Enabled(DecisionFixture f, string door, string point)
        {
            var points = door == "studio" ? await new EngineRpcTarget(f.Engine).getDecisionPoints()
                       : await McpTools.GetDecisionPoints(f.Engine);
            var enabled = points.Single(p => p.Name == point).Enabled;
            // The asking paths read the same switch the list reports.
            Assert.Equal(enabled, f.Engine.DecisionPointEnabled(point));
            return enabled;
        }

        [Theory]
        [InlineData(DecisionRegistry.QuerySummarize, "studio")]
        [InlineData(DecisionRegistry.QuerySummarize, "agent")]
        [InlineData(DecisionRegistry.MeasureFolderAndFormat, "agent")]
        [InlineData(DecisionRegistry.ColumnFormat, "studio")]
        public async Task Two_models_opened_live_keep_their_own_switch(string point, string door)
        {
            using var f = new DecisionFixture();
            await OpenLiveAsync(f, "Model A");
            await Set(f, door, point, false);
            Assert.False(await Enabled(f, door, point));

            await OpenLiveAsync(f, "Model B");
            Assert.True(await Enabled(f, door, point));   // A's owner switched it off for A, not for B

            // B's owner switches it off and on again. That is B's switch, and A's stays where A's owner left it.
            await Set(f, door, point, false);
            await Set(f, door, point, true);
            Assert.True(await Enabled(f, door, point));

            await OpenLiveAsync(f, "Model A");
            Assert.False(await Enabled(f, door, point));
            // The other points of A were never touched.
            Assert.All(DecisionRegistry.Points.Where(p => p.Name != point), p => Assert.True(f.Engine.DecisionPointEnabled(p.Name)));
        }

        [Fact]
        public async Task The_same_live_model_on_another_endpoint_spelling_is_still_the_same_model()
        {
            using var f = new DecisionFixture();
            await OpenLiveAsync(f, "Model A");
            await Set(f, "studio", DecisionRegistry.QuerySummarize, false);
            await OpenLiveAsync(f, "MODEL A", endpoint: Cloud.ToUpperInvariant() + "/");
            Assert.False(await Enabled(f, "agent", DecisionRegistry.QuerySummarize));
        }

        [Fact]
        public async Task A_Desktop_model_keeps_its_switch_when_Desktop_opens_it_on_a_new_port()
        {
            // Power BI Desktop picks a new port and a new database name every time it starts, so neither can say which
            // model this is. The file it has open can.
            using var f = new DecisionFixture();
            var sales = Path.Combine(f.Home, "reports", "Sales.pbix");
            await OpenLiveAsync(f, Guid.NewGuid().ToString(), endpoint: "localhost:52001", localName: "Sales", localPath: sales);
            await Set(f, "studio", DecisionRegistry.ColumnFormat, false);

            await OpenLiveAsync(f, Guid.NewGuid().ToString(), endpoint: "localhost:61234", localName: "Sales", localPath: sales);
            Assert.False(await Enabled(f, "agent", DecisionRegistry.ColumnFormat));

            // A different report on the port the first one used is a different model.
            await OpenLiveAsync(f, Guid.NewGuid().ToString(), endpoint: "localhost:52001", localName: "Stock",
                                localPath: Path.Combine(f.Home, "reports", "Stock.pbix"));
            Assert.True(await Enabled(f, "agent", DecisionRegistry.ColumnFormat));
        }

        [Fact]
        public async Task Two_unsaved_models_keep_their_own_switch_and_the_first_save_carries_it()
        {
            using var f = new DecisionFixture();
            await f.Engine.CreateModelAsync("Draft A", 1604);
            await Set(f, "agent", DecisionRegistry.MeasureFolderAndFormat, false);
            Assert.False(await Enabled(f, "studio", DecisionRegistry.MeasureFolderAndFormat));

            await f.Engine.CreateModelAsync("Draft B", 1604, discardUnsaved: true);
            Assert.True(await Enabled(f, "studio", DecisionRegistry.MeasureFolderAndFormat));
            await Set(f, "studio", DecisionRegistry.ColumnFormat, false);

            // Saving B for the first time keeps what B's owner chose, and only that.
            var folder = Path.Combine(f.Workspace, "draft-b");
            Directory.CreateDirectory(folder);
            await f.Engine.SaveAsync(Path.Combine(folder, "DraftB.bim"), "bim", false, "human");
            Assert.False(await Enabled(f, "agent", DecisionRegistry.ColumnFormat));
            Assert.True(await Enabled(f, "agent", DecisionRegistry.MeasureFolderAndFormat));

            // And the workspace was never written as if it were one of these models.
            Assert.False(File.Exists(Path.Combine(f.Workspace, LayoutStore.DirName, "decision-settings.json")));
        }

        [Fact]
        public async Task A_live_model_saved_to_disk_keeps_its_switch()
        {
            using var f = new DecisionFixture();
            await OpenLiveAsync(f, "Model A");
            await Set(f, "agent", DecisionRegistry.QuerySummarize, false);
            var folder = Path.Combine(f.Workspace, "model-a");
            Directory.CreateDirectory(folder);
            await f.Engine.SaveAsync(Path.Combine(folder, "ModelA.bim"), "bim", false, "human");
            Assert.False(await Enabled(f, "studio", DecisionRegistry.QuerySummarize));
        }

        [Fact]
        public async Task A_switch_kept_in_the_shared_file_before_this_fix_is_not_turned_back_on()
        {
            // Before the fix every live and unsaved model in a workspace read the workspace's one file. A point
            // someone switched off there stays off for a live model until that model's own switch says otherwise,
            // because reading it as on would send what a person asked not to send.
            using var f = new DecisionFixture();
            var shared = Path.Combine(f.Workspace, LayoutStore.DirName, "decision-settings.json");
            Directory.CreateDirectory(Path.GetDirectoryName(shared));
            File.WriteAllText(shared, "{\"points\":{\"" + DecisionRegistry.QuerySummarize + "\":{\"enabled\":false}}}");

            await OpenLiveAsync(f, "Model A");
            Assert.False(await Enabled(f, "studio", DecisionRegistry.QuerySummarize));
            await Set(f, "studio", DecisionRegistry.QuerySummarize, true);
            Assert.True(await Enabled(f, "studio", DecisionRegistry.QuerySummarize));

            // A's owner turning it on is A's switch: B still reads what the shared file said.
            await OpenLiveAsync(f, "Model B");
            Assert.False(await Enabled(f, "agent", DecisionRegistry.QuerySummarize));

            // A model saved in the workspace folder itself keeps using that file as its own, and what it does there
            // from now on reaches no live model.
            await f.Engine.CreateModelAsync("On disk", 1604, discardUnsaved: true);
            await f.SaveAsAsync("OnDisk.bim");
            Assert.False(await Enabled(f, "agent", DecisionRegistry.QuerySummarize));
            await Set(f, "agent", DecisionRegistry.ColumnFormat, false);
            await OpenLiveAsync(f, "Model C");
            Assert.True(await Enabled(f, "agent", DecisionRegistry.ColumnFormat));
            Assert.False(await Enabled(f, "agent", DecisionRegistry.QuerySummarize));
        }

        [Fact]
        public async Task With_no_model_open_the_switch_says_to_open_one()
        {
            using var f = new DecisionFixture();
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Set(f, "agent", DecisionRegistry.QuerySummarize, false));
            Assert.Equal(LocalEngine.NoModelForSwitchNote, ex.Message);
            Assert.True(await Enabled(f, "studio", DecisionRegistry.QuerySummarize));
        }

        // ---- round 2 (the switch-scope verifier, 28 Sep): Desktop files, models side by side, and the small items ----

        private const string Point = DecisionRegistry.MeasureFolderAndFormat;

        private static async Task<DecisionPointInfo> Row(DecisionFixture f, string door, string point = Point)
        {
            var points = door == "studio" ? await new EngineRpcTarget(f.Engine).getDecisionPoints()
                       : await McpTools.GetDecisionPoints(f.Engine);
            var row = points.Single(p => p.Name == point);
            Assert.Equal(row.Enabled, f.Engine.DecisionPointEnabled(point));
            return row;
        }

        private static Task OpenDesktopAsync(DecisionFixture f, string port, string title = null, string path = null,
                                             string database = null, bool unnamed = false)
            => OpenDesktopCoreAsync(f, port, database ?? Guid.NewGuid().ToString(), title, path, unnamed || (title == null && path == null));

        private static async Task OpenDesktopCoreAsync(DecisionFixture f, string port, string database, string title, string path, bool desktop)
        {
            await f.Engine.CreateModelAsync(title ?? "Model", 1604, discardUnsaved: true);
            f.Sessions.Current.LiveOrigin = new LiveOrigin("localhost:" + port, database, null, localName: title, localPath: path, localDesktop: desktop);
        }

        [Fact]
        public async Task A_title_only_Desktop_file_switched_off_and_on_again_stays_on_after_Desktop_restarts()
        {
            using var f = new DecisionFixture();
            await OpenDesktopAsync(f, "52001", "Sales");
            await Set(f, "studio", Point, false);
            await Set(f, "agent", Point, true);
            await OpenDesktopAsync(f, "61234", "Sales");
            var row = await Row(f, "agent");
            Assert.True(row.Enabled);
            Assert.Null(row.Note);
        }

        [Theory]
        [InlineData("studio")]
        [InlineData("agent")]
        public async Task A_Desktop_on_lasts_for_its_own_title_leaves_other_files_off_and_says_so(string door)
        {
            // K1, ruled 29 Sep, and round 5, ruled 30 Sep. Two Sales.pbix files, both opened from Desktop's Open dialog,
            // cannot be told apart once Desktop restarts. B's on is kept for files with that name, so it survives a
            // restart, says on the door it was set from that the other file's off stays, and clears no off: A keeps its own.
            using var f = new DecisionFixture();
            var a = Guid.NewGuid().ToString();
            await OpenDesktopAsync(f, "52001", "Sales", database: a);
            await Set(f, "agent", Point, false);
            await OpenDesktopAsync(f, "52002", "Sales");
            Assert.False((await Row(f, door)).Enabled);
            var set = (await Set(f, door, Point, true)).Single(p => p.Name == Point);
            Assert.True(set.Enabled);
            Assert.Equal(LocalEngine.OnForFilesWithThisNameNote, set.Note);
            Assert.DoesNotContain("\u2014", set.Note);
            // The other points of B say nothing: nothing was switched for them.
            Assert.All((await new EngineRpcTarget(f.Engine).getDecisionPoints()).Where(p => p.Name != Point), p => Assert.Null(p.Note));
            var row = await Row(f, door);
            Assert.True(row.Enabled);
            Assert.Null(row.Note);

            await OpenDesktopAsync(f, "61234", "Sales");   // Desktop started again
            Assert.True((await Row(f, "studio")).Enabled, "an on for a Desktop file did not survive a Desktop restart");
            await OpenDesktopAsync(f, "52001", "Sales", database: a);   // A, in the run that switched it off
            Assert.False((await Row(f, "agent")).Enabled, "A's own off in its own run was switched back on");
        }

        [Fact]
        public async Task An_off_another_Sales_pbix_switched_off_after_this_ones_on_wins_and_a_later_on_still_works()
        {
            // Re-labelled at M11 sol1-plan (Sol 6.1, finding 2.2). This used to say the older on beat the newer off ("On until Power BI
            // Desktop closes"), which let a window resume uploads the person had switched off for the title it shares. Now the newer
            // off wins for the window that switched on first, and an on switched after it works again.
            using var f = new DecisionFixture();
            var b = Guid.NewGuid().ToString();
            await OpenDesktopAsync(f, "52002", "Sales", database: b);
            await Set(f, "studio", Point, true);
            await OpenDesktopAsync(f, "52001", "Sales");
            await Set(f, "agent", Point, false);
            await OpenDesktopAsync(f, "52002", "Sales", database: b);
            var row = await Row(f, "studio");
            Assert.False(row.Enabled, "an older on beat a newer off for the same title");
            Assert.StartsWith("Off because it was switched off for a Power BI Desktop file with the same name.", row.Note);
            Assert.DoesNotContain("\u2014", row.Note);

            await Set(f, "studio", Point, true);   // a later explicit on, in this window
            Assert.True((await Row(f, "studio")).Enabled);
            Assert.All((await new EngineRpcTarget(f.Engine).getDecisionPoints()).Where(p => p.Name != Point), p => Assert.Null(p.Note));
        }

        [Theory]
        [InlineData(false)]   // known by its path first, by its title only later
        [InlineData(true)]    // known by its title first, by its path only later
        public async Task A_Desktop_file_known_by_its_path_alone_shares_its_off_with_the_same_file_known_by_title(bool titleFirst)
        {
            // The rarer half of E1 (R2): a path with no title one day, a title with no path the next. The path names the
            // file, and Desktop's title is the file's name, so the two meet in the title's file.
            using var f = new DecisionFixture();
            var sales = Path.Combine(f.Home, "reports", "Sales.pbix");
            if (titleFirst) await OpenDesktopAsync(f, "52001", "Sales"); else await OpenDesktopAsync(f, "52001", path: sales);
            await Set(f, "agent", Point, false);
            if (titleFirst) await OpenDesktopAsync(f, "61234", path: sales); else await OpenDesktopAsync(f, "61234", "Sales");
            Assert.False((await Row(f, "studio")).Enabled, "one Sales.pbix read on when opened the other way");
        }

        [Fact]
        public async Task Studio_hears_the_assistant_open_another_model_and_a_click_on_the_last_models_rows_switches_nothing()
        {
            // E2 over the real named pipe, with Studio's own handshake: the assistant opens another model, Studio hears it
            // under the new model's id (what the panel re-reads on), and a click that still carries the last model's id
            // is refused with nothing written, before or after the news arrives.
            using var f = new DecisionFixture();
            var pipeName = TestPipe.Mint("semanticus-sw3-");
            const string challenge = "switch-scope-round-3-studio-challenge-0123456789";
            using var server = new RpcServer(f.Sessions, f.Engine, pipeName, challenge);
            using var stop = new System.Threading.CancellationTokenSource();
            var serving = server.RunAsync(stop.Token);
            var heard = new System.Collections.Concurrent.ConcurrentQueue<(string Session, string Label)>();
            System.IO.Pipes.NamedPipeClientStream pipe = null;
            StreamJsonRpc.JsonRpc studio = null;
            RemoteEngine agent = null;
            try
            {
                pipe = new System.IO.Pipes.NamedPipeClientStream(".", pipeName, System.IO.Pipes.PipeDirection.InOut, System.IO.Pipes.PipeOptions.Asynchronous);
                await pipe.ConnectAsync(5000);
                await RpcHandshake.WriteAsync(pipe, RpcConnectionRole.Human, challenge);
                await RpcHandshake.ReadAcceptedAsync(pipe);
                studio = new StreamJsonRpc.JsonRpc(RpcServer.CreateHandler(pipe));
                studio.AddLocalRpcMethod("model/didChange", new Action<Newtonsoft.Json.Linq.JObject>(n => heard.Enqueue(((string)n["sessionId"], (string)n["label"]))));
                studio.StartListening();
                agent = await RemoteEngine.ConnectAsync(pipeName);

                await agent.CreateModelAsync("Sales", 1604);
                await McpTools.SaveModel(agent, Path.Combine(f.Workspace, "Sales.bim"), "bim");
                await McpTools.SetDecisionPointEnabled(agent, Point, false);
                var sales = (await studio.InvokeAsync<SessionInfo>("sessionInfo")).SessionId;

                await McpTools.CreateModel(agent, "Stock", 1604, true);
                var stock = f.Sessions.Current.Id;
                Assert.NotEqual(sales, stock);
                // The click on Sales's rows, whether or not the news has arrived yet.
                var refused = await Assert.ThrowsAnyAsync<Exception>(() => studio.InvokeAsync<DecisionPointInfo[]>("setDecisionPointEnabled", Point, true, "human", sales));
                Assert.Contains("no longer open, so nothing was changed", refused.Message);
                for (var i = 0; i < 40 && !heard.Any(h => h.Session == stock); i++) await Task.Delay(50);
                Assert.Contains(heard, h => h.Session == stock && h.Label == "Created Stock");
                Assert.True((await Row(f, "studio")).Enabled);   // Stock was never switched

                await McpTools.OpenModel(agent, Path.Combine(f.Workspace, "Sales.bim"), true);
                var reopened = f.Sessions.Current.Id;
                for (var i = 0; i < 40 && !heard.Any(h => h.Session == reopened); i++) await Task.Delay(50);
                Assert.Contains(heard, h => h.Session == reopened && h.Label == "Opened Sales");
                Assert.False((await Row(f, "agent")).Enabled);
                // Rows read again for the model open now carry its id, and that click is taken.
                var on = await studio.InvokeAsync<DecisionPointInfo[]>("setDecisionPointEnabled", Point, true, "human", reopened);
                Assert.True(on.Single(p => p.Name == Point).Enabled);
            }
            finally
            {
                agent?.Dispose();
                try { studio?.Dispose(); } catch { }
                try { pipe?.Dispose(); } catch { }
                stop.Cancel();
                try { await serving; } catch { }
            }
        }

        [Fact]
        public async Task A_switch_with_nowhere_to_keep_it_says_so_in_plain_words()
        {
            // LOW 9: "open the engine with a workspace folder" was engine jargon a person could reach.
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, TestEntitlements.Pro, null);
            await engine.CreateModelAsync("Live", 1604);
            sessions.Current.LiveOrigin = new LiveOrigin(Cloud, "Live", null);
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => McpTools.SetDecisionPointEnabled(engine, Point, false));
            Assert.Equal("There is nowhere to keep this setting yet. Save the model to a file first, then try again.", ex.Message);
            Assert.DoesNotContain("engine", ex.Message);
        }

        [Fact]
        public async Task A_Desktop_model_Desktop_names_nothing_about_keeps_its_off_across_a_Desktop_restart()
        {
            // D3: no path, no title. Its port and database change when Desktop restarts, so the off is also kept where
            // every such model reads it, and a cloud model never does.
            using var f = new DecisionFixture();
            await OpenDesktopAsync(f, "52001");
            await Set(f, "agent", Point, false);
            await OpenDesktopAsync(f, "61234");
            var row = await Row(f, "studio");
            Assert.False(row.Enabled, "after Desktop restarts the switched-off point reads on");
            Assert.NotNull(row.Note);

            await OpenLiveAsync(f, "Model A");
            Assert.True((await Row(f, "agent")).Enabled);

            // Switched on in a later run it is on for models known by nothing, says what it left alone, and stays on after
            // the next restart (K1, ruled 29 Sep; round 5, 30 Sep: an on clears no off).
            await OpenDesktopAsync(f, "61234", database: "run-3");
            var set = (await Set(f, "studio", Point, true)).Single(p => p.Name == Point);
            Assert.Equal(LocalEngine.OnForModelsWithNoNameNote, set.Note);
            Assert.True((await Row(f, "agent")).Enabled);
            await OpenDesktopAsync(f, "63000");
            Assert.True((await Row(f, "agent")).Enabled);
            await OpenLiveAsync(f, "Model A");
            Assert.True((await Row(f, "agent")).Enabled);
        }

        [Fact]
        public async Task A_Desktop_model_Desktop_names_nothing_about_switched_off_and_on_again_stays_on_after_a_restart()
        {
            using var f = new DecisionFixture();
            await OpenDesktopAsync(f, "52001");
            await Set(f, "agent", Point, false);
            await Set(f, "studio", Point, true);
            await OpenDesktopAsync(f, "61234");
            var row = await Row(f, "agent");
            Assert.True(row.Enabled);
            Assert.Null(row.Note);
        }

        [Fact]
        public async Task Two_saved_models_side_by_side_in_one_folder_keep_their_own_switch()
        {
            // D4.
            using var f = new DecisionFixture();
            var folder = Path.Combine(f.Workspace, "models");
            Directory.CreateDirectory(folder);
            await f.Engine.CreateModelAsync("Sales", 1604);
            await f.Engine.SaveAsync(Path.Combine(folder, "Sales.bim"), "bim", false, "human");
            await Set(f, "agent", Point, false);

            await f.Engine.CreateModelAsync("Stock", 1604, discardUnsaved: true);
            await f.Engine.SaveAsync(Path.Combine(folder, "Stock.bim"), "bim", false, "human");
            Assert.True((await Row(f, "studio")).Enabled, "Stock.bim read Sales.bim's switch");
            await Set(f, "studio", Point, false);
            await Set(f, "studio", Point, true);

            await f.Engine.OpenAsync(Path.Combine(folder, "Sales.bim"));
            Assert.False((await Row(f, "agent")).Enabled, "Stock's owner turning the point on turned it on for Sales.bim too");
            await f.Engine.OpenAsync(Path.Combine(folder, "Stock.bim"));
            Assert.True((await Row(f, "agent")).Enabled);
        }

        [Theory]
        [InlineData("live")]
        [InlineData("unsaved")]
        [InlineData("saved")]
        public async Task A_save_beside_another_model_never_switches_that_model(string kind)
        {
            // D4: saving a model into a folder that already holds another model carries its own switches and no one else's.
            using var f = new DecisionFixture();
            var folder = Path.Combine(f.Workspace, "shared-folder");
            Directory.CreateDirectory(folder);
            await f.Engine.CreateModelAsync("Other", 1604);
            await f.Engine.SaveAsync(Path.Combine(folder, "Other.bim"), "bim", false, "human");
            await Set(f, "studio", DecisionRegistry.ColumnFormat, false);

            if (kind == "live") await OpenLiveAsync(f, "Model A");
            else await f.Engine.CreateModelAsync("Model A", 1604, discardUnsaved: true);
            if (kind == "saved") await f.Engine.SaveAsync(Path.Combine(f.Workspace, "elsewhere", "ModelA.bim"), "bim", false, "human");
            await Set(f, "agent", Point, false);
            Assert.True((await Row(f, "agent", DecisionRegistry.ColumnFormat)).Enabled);
            await f.Engine.SaveAsync(Path.Combine(folder, "ModelA.bim"), "bim", false, "human");
            Assert.False((await Row(f, "studio")).Enabled);
            Assert.True((await Row(f, "studio", DecisionRegistry.ColumnFormat)).Enabled);

            await f.Engine.OpenAsync(Path.Combine(folder, "Other.bim"));
            Assert.True((await Row(f, "agent")).Enabled, "saving model A beside Other.bim switched the point off for Other.bim");
            Assert.False((await Row(f, "agent", DecisionRegistry.ColumnFormat)).Enabled);
        }

        [Fact]
        public async Task A_saved_model_keeps_its_switch_through_save_as_and_a_rename()
        {
            // Keying a saved model by its own file must not lose its off when the file moves: before per-model keys the
            // folder's one file kept it, and permission is asked again at the new address, so the next yes would send it.
            using var f = new DecisionFixture();
            var folder = Path.Combine(f.Workspace, "models");
            Directory.CreateDirectory(folder);
            await f.Engine.CreateModelAsync("Sales", 1604);
            await f.Engine.SaveAsync(Path.Combine(folder, "Sales.bim"), "bim", false, "human");
            await Set(f, "agent", Point, false);

            await f.Engine.SaveAsync(Path.Combine(folder, "Sales copy.bim"), "bim", false, "human");
            Assert.False((await Row(f, "studio")).Enabled, "save as, same folder");
            await f.Engine.SaveAsync(Path.Combine(f.Workspace, "other", "Sales.bim"), "bim", false, "human");
            Assert.False((await Row(f, "studio")).Enabled, "save as, another folder");

            // Renamed on disk, outside Semanticus: the switch errs toward off, and the row says why.
            File.Move(Path.Combine(folder, "Sales.bim"), Path.Combine(folder, "Sales 2024.bim"));
            await f.Engine.OpenAsync(Path.Combine(folder, "Sales 2024.bim"));
            var row = await Row(f, "agent");
            Assert.False(row.Enabled, "a renamed model reads on again");
            Assert.Contains("Sales.bim", row.Note);
        }

        [Fact]
        public async Task Reading_the_switches_writes_nothing()
        {
            // LOW 1: get_decision_points says "Reads only".
            using var f = new DecisionFixture();
            var shared = Path.Combine(f.Workspace, LayoutStore.DirName, "decision-settings.json");
            await OpenLiveAsync(f, "Model A");
            await Row(f, "agent");
            await Row(f, "studio");
            await OpenDesktopAsync(f, "52001", "Sales");
            await Row(f, "agent");
            await f.Engine.CreateModelAsync("Draft", 1604, discardUnsaved: true);
            await Row(f, "agent");
            Assert.False(Directory.Exists(Path.Combine(f.Workspace, LayoutStore.DirName, "decision-settings")));
            Assert.False(File.Exists(shared));
        }

        [Fact]
        public async Task An_unsaved_model_honours_an_off_the_whole_workspace_kept_before()
        {
            // LOW 2.
            using var f = new DecisionFixture();
            var shared = Path.Combine(f.Workspace, LayoutStore.DirName, "decision-settings.json");
            Directory.CreateDirectory(Path.GetDirectoryName(shared));
            File.WriteAllText(shared, "{\"points\":{\"" + Point + "\":{\"enabled\":false}}}");
            await f.Engine.CreateModelAsync("Draft", 1604);
            var row = await Row(f, "agent");
            Assert.False(row.Enabled, "an unsaved model reads on a point the workspace's file switched off");
            Assert.NotNull(row.Note);
            await Set(f, "studio", Point, true);
            Assert.True((await Row(f, "agent")).Enabled);
        }

        [Fact]
        public async Task An_off_an_older_engine_writes_later_still_reaches_a_live_model()
        {
            // LOW 3: an engine from before per-model switches, still running on this workspace, keeps writing the
            // workspace's one file. Its offs are still honoured; only a model's own switch can say otherwise.
            using var f = new DecisionFixture();
            await OpenLiveAsync(f, "Model A");
            Assert.True((await Row(f, "agent")).Enabled);
            var shared = Path.Combine(f.Workspace, LayoutStore.DirName, "decision-settings.json");
            Directory.CreateDirectory(Path.GetDirectoryName(shared));
            File.WriteAllText(shared, "{\"points\":{\"" + Point + "\":{\"enabled\":false}}}");
            await OpenLiveAsync(f, "Model B");
            Assert.False((await Row(f, "agent")).Enabled);
        }

        [Fact]
        public async Task A_percent_encoded_endpoint_is_the_same_model_and_two_workspaces_are_two()
        {
            // LOW 4 and LOW 7: the spelling fact that bites.
            using var f = new DecisionFixture();
            await OpenLiveAsync(f, "Model A");
            await Set(f, "studio", Point, false);
            await OpenLiveAsync(f, "Model A", endpoint: "powerbi://api.powerbi.com/v1.0/myorg/Contoso%20Live");
            Assert.False((await Row(f, "agent")).Enabled);

            await OpenLiveAsync(f, "Sales", endpoint: "powerbi://api.powerbi.com/v1.0/myorg/Team East");
            await Set(f, "agent", Point, false);
            await OpenLiveAsync(f, "Sales", endpoint: "powerbi://api.powerbi.com/v1.0/myorg/Team West");
            Assert.True((await Row(f, "agent")).Enabled);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task A_first_save_that_cannot_keep_the_switches_says_so_in_plain_words(bool pro)
        {
            // LOW 5: no operating system error and no file path, and Free is not sent to a panel that shows it nothing.
            using var f = new DecisionFixture(pro ? TestEntitlements.Pro : TestEntitlements.Free);
            await f.Engine.CreateModelAsync("Draft", 1604);
            await McpTools.SetDecisionPointEnabled(f.Engine, Point, false);
            var folder = Path.Combine(f.Workspace, "draft");
            Directory.CreateDirectory(Path.Combine(folder, LayoutStore.DirName, "decision-settings.json"));
            var target = Path.Combine(folder, "Draft.bim");
            var ex = await Assert.ThrowsAnyAsync<Exception>(() => McpTools.SaveModel(f.Engine, target, "bim"));
            Assert.True(File.Exists(target));
            Assert.DoesNotContain(folder, ex.Message);
            Assert.DoesNotContain(LayoutStore.DirName, ex.Message);
            Assert.DoesNotContain("(", ex.Message);
            if (pro) Assert.Equal("The model was saved, but the Pro suggestions switched off for it could not be kept beside it. Switch them off again under What Pro decides.", ex.Message);
            else Assert.Equal("The model was saved. The Pro suggestions switched off for it could not be kept beside it, so switch them off again if you move to Pro.", ex.Message);
        }

        [Fact]
        public void The_switch_tool_says_where_each_kind_of_model_keeps_its_switches()
        {
            // LOW 8: "travels with the project" is true of a saved model only.
            var method = typeof(McpTools).GetMethod(nameof(McpTools.SetDecisionPointEnabled));
            var text = ((System.ComponentModel.DescriptionAttribute)Attribute.GetCustomAttribute(method, typeof(System.ComponentModel.DescriptionAttribute))).Description;
            Assert.Contains("Off means it is never asked, whatever the permission says.", text);
            Assert.Contains("A saved model keeps them in its folder", text);
            Assert.Contains("a model open live keeps them in this workspace", text);
            Assert.Contains("a model not saved yet keeps them until its first save", text);
            Assert.DoesNotContain("\u2014", text);
        }

        [Fact]
        public async Task A_switch_set_while_a_live_open_is_finishing_waits_for_it()
        {
            // LOW 10: the live open publishes its session a moment before it says which live model it is. A switch set
            // in that moment used to be kept as an unsaved model's and lost when the open finished.
            using var f = new DecisionFixture();
            await f.Engine.CreateModelAsync("Opening", 1604);
            System.Threading.Interlocked.Increment(ref f.Engine._liveOriginStampPending);
            try
            {
                var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Set(f, "agent", Point, false));
                Assert.Equal(LocalEngine.ModelStillOpeningNote, ex.Message);
            }
            finally { System.Threading.Interlocked.Decrement(ref f.Engine._liveOriginStampPending); }
            f.Sessions.Current.LiveOrigin = new LiveOrigin(Cloud, "Opening", null);
            Assert.True((await Row(f, "studio")).Enabled);
            await Set(f, "studio", Point, false);
            Assert.False((await Row(f, "agent")).Enabled);
        }
    }
}
