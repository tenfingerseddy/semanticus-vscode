// verify: m11/switch-scope round 2. Left failing on purpose: each fact is a defect the verifier found, not a fix.
//
// Round 2 keys a Power BI Desktop model by its .pbix path, else by that run of Desktop, and shares an off with the
// models that might be the same file: a title file for models Desktop named a title for, one file for the models
// Desktop named nothing about. But which of those a model is depends on what Semanticus could read from Desktop that
// day, and the two groups never read each other. LocalDesktop.CleanTitle reads a title it does not recognise, such as
// "Ventes - Power BI Desktop (Preview)", as no title at all (PrimerTests.cs, LocalDesktopTests). On such a Desktop a
// file opened by double-click is known by its path alone, and the same file opened from Desktop's recent list is known
// by nothing. So a point switched off one way reads on the other way, and the next yes to the permission card sends
// it. That is round 1's D2, still open for these two states, and the promise "Off means it is never asked, whatever
// the permission says" (set_decision_point_enabled) does not hold there. Before this lane (98091203) it held: every
// live model read the workspace's one file.
//
// Two more. An off kept in the workspace's one file from before per-model switches can no longer be switched on for
// good for a Desktop model opened from Desktop's dialog: its on lives in a file for that run of Desktop, and nothing
// ever clears the old off. At 98091203 the on cleared it, and at round 1 (22769031) a title-only on stuck.
// And the Studio panel's new re-read on a model swap listens for a model/didChange with a new session id, which the
// engine never sends when the assistant opens or creates a model (nor anything else Studio hears), so the panel keeps
// the last model's rows and a click sets the new model's switch from them (round 1's LOW 9, not fixed; the headless
// scene settings-model-swap dispatches that didChange by hand).

using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Decisions;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    public sealed class DecisionSwitchScopeVerifyR2Tests
    {
        private const string Point = DecisionRegistry.MeasureFolderAndFormat;

        /// <summary>A Desktop model the way open_local stamps it (LocalEngine.cs:3239): localDesktop is always true there,
        /// and the title and path are whatever LocalDesktop.TryGetIdentity could read.</summary>
        private static async Task OpenDesktopAsync(DecisionFixture f, string port, string title = null, string path = null,
                                                   string database = null)
        {
            await f.Engine.CreateModelAsync(title ?? "Model", 1604, discardUnsaved: true);
            f.Sessions.Current.LiveOrigin = new LiveOrigin("localhost:" + port, database ?? Guid.NewGuid().ToString(), null,
                localName: title, localPath: path, localDesktop: true);
        }

        private static async Task<string> AddMeasureAsync(DecisionFixture f)
        {
            await f.Engine.CreateTableAsync("Sales", "human");
            await f.Engine.CreateColumnAsync("table:Sales", "Amount", "Double", null, "human");
            await f.Engine.CreateMeasureAsync("table:Sales", "Order Count", "COUNTROWS ( Sales )", "human", "Volume");
            return await f.Engine.CreateMeasureAsync("table:Sales", "Total Sales", "SUM ( Sales[Amount] )", "human");
        }

        [Fact]
        public async Task Verify_a_Desktop_file_switched_off_by_path_stays_off_when_Desktop_names_nothing_the_next_time()
        {
            using var f = new DecisionFixture();
            await OpenDesktopAsync(f, "52001", path: Path.Combine(f.Home, "reports", "Sales.pbix"));   // double-clicked
            await McpTools.SetDecisionPointEnabled(f.Engine, Point, false);

            await OpenDesktopAsync(f, "61234");   // the same file, another day, from Desktop's recent list
            Assert.False(f.Engine.DecisionPointEnabled(Point),
                "A point switched off for Sales.pbix reads on when Desktop names neither its path nor its title.");
        }

        [Fact]
        public async Task Verify_a_Desktop_file_switched_off_with_nothing_named_stays_off_when_opened_by_path()
        {
            using var f = new DecisionFixture();
            await OpenDesktopAsync(f, "52001");
            await new EngineRpcTarget(f.Engine).setDecisionPointEnabled(Point, false);

            await OpenDesktopAsync(f, "61234", path: Path.Combine(f.Home, "reports", "Sales.pbix"));
            Assert.False(f.Engine.DecisionPointEnabled(Point),
                "A point switched off for a Desktop model Desktop named nothing about reads on when that file is opened by path.");
        }

        [Fact]
        public async Task Verify_the_next_yes_sends_what_was_switched_off_for_a_Desktop_file_opened_the_other_way()
        {
            using var f = new DecisionFixture();
            await OpenDesktopAsync(f, "52001", path: Path.Combine(f.Home, "reports", "Sales.pbix"));
            var measure = await AddMeasureAsync(f);
            await f.ConsentAsync();
            var oracle = f.UseEmptyRecorded();
            await McpTools.SetDecisionPointEnabled(f.Engine, Point, false);
            Assert.False((await f.Engine.ProposeMeasureDefaultsAsync(measure)).Asked);

            // The same file from Desktop's recent list. A re-opened live model asks for permission again, and the
            // person says yes again.
            await OpenDesktopAsync(f, "61234");
            measure = await AddMeasureAsync(f);
            Assert.False((await f.Engine.GetConsentAsync()).Current);
            await f.ConsentAsync();
            var asked = oracle.AskedDigests.Count;
            var proposal = await McpTools.ProposeMeasureDefaults(f.Engine, measure);
            Assert.False(proposal.Asked, "The measure was sent for a suggestion although the point was switched off for this file.");
            Assert.Equal(asked, oracle.AskedDigests.Count);
        }

        [Theory]
        [InlineData(false)]   // Desktop named the title (opened from its Open dialog)
        [InlineData(true)]    // Desktop named nothing
        public async Task Verify_an_off_from_before_per_model_switches_can_be_switched_on_for_good_for_a_Desktop_model(bool unnamed)
        {
            // Every person who switched a point off before this lane has such an off in the workspace's one file.
            using var f = new DecisionFixture();
            var shared = Path.Combine(f.Workspace, LayoutStore.DirName, "decision-settings.json");
            Directory.CreateDirectory(Path.GetDirectoryName(shared));
            File.WriteAllText(shared, "{\"points\":{\"" + Point + "\":{\"enabled\":false}}}");

            await OpenDesktopAsync(f, "52001", title: unnamed ? null : "Sales");
            Assert.False(f.Engine.DecisionPointEnabled(Point));
            await new EngineRpcTarget(f.Engine).setDecisionPointEnabled(Point, true);
            Assert.True(f.Engine.DecisionPointEnabled(Point));

            await OpenDesktopAsync(f, "61234", title: unnamed ? null : "Sales");   // Desktop started again
            Assert.True(f.Engine.DecisionPointEnabled(Point),
                "Switched on for this model, the point is off again after Desktop restarts, and nothing can clear the old off.");
        }

        [Fact]
        public async Task Verify_Studio_is_told_when_the_assistant_opens_another_model_so_What_Pro_decides_can_read_its_switches_again()
        {
            // decisions-settings.tsx reads the switches again only on a model/didChange whose sessionId is new. This listens
            // for both notifications the extension already relays to Studio (model/didChange as 'didChange', model/activity
            // as 'activity', extension.ts:1676 and :1733), so either can carry the news; today neither does.
            using var f = new DecisionFixture();
            var pipeName = TestPipe.Mint("semanticus-vsw2v-");
            const string challenge = "verify-switch-scope-r2-studio-challenge-0123456789";
            using var server = new RpcServer(f.Sessions, f.Engine, pipeName, challenge);
            using var stop = new System.Threading.CancellationTokenSource();
            var serving = server.RunAsync(stop.Token);
            var heard = new ConcurrentQueue<string>();
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
                studio.AddLocalRpcMethod("model/didChange", new Action<Newtonsoft.Json.Linq.JObject>(n => heard.Enqueue((string)n["sessionId"])));
                studio.AddLocalRpcMethod("model/activity", new Action<Newtonsoft.Json.Linq.JObject>(n => heard.Enqueue((string)n["sessionId"])));
                studio.StartListening();
                agent = await RemoteEngine.ConnectAsync(pipeName);

                await agent.CreateModelAsync("Sales", 1604);
                await McpTools.SaveModel(agent, Path.Combine(f.Workspace, "Sales.bim"), "bim");
                await McpTools.CreateModel(agent, "Stock", 1604, true);
                var stock = f.Sessions.Current.Id;
                await Eventually(() => heard.Contains(stock));
                Assert.Contains(stock, heard);

                await McpTools.OpenModel(agent, Path.Combine(f.Workspace, "Sales.bim"), true);
                var sales = f.Sessions.Current.Id;
                await Eventually(() => heard.Contains(sales));
                Assert.Contains(sales, heard);
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

        private static async Task Eventually(Func<bool> done)
        {
            for (var i = 0; i < 40 && !done(); i++) await Task.Delay(50);
        }
    }
}
