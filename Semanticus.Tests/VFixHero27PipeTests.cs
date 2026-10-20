// Verifier v-fix-hero27 (P1, P6): END TO END over the pipe the product runs on. The owner engine serves the pipe (RpcServer); the Studio
// is a Human pipe connection; the person's Claude Code is the MCP door, AssistantEngine.ForAssistant(RemoteEngine), calling the real tool
// methods. The live model is a fake LiveConnection, so no live call is made. When VFH27_OUT is set, the notebook rows exactly as the
// Studio reads them are written there, and the webview drive (tools/uishot/v-fix-hero27-drive.mjs) seeds the real Studio with them.
//
// P1, engine half: the page now puts a folded row's OWN DAX in the DAX tab, and Run there sends runDax(thatDax) then
// recordQueryRun(thatDax, rows, ms, null, rowId). For every kind of row (the person's words row, the person's DAX row, the assistant's
// ask_data row, the assistant's run_dax row) that run must be filed under THAT row: the same id comes back, no row is added, and no
// row changes owner. The same again after a restart (a new engine process on the same workspace).
// P6: show_in_studio on the Query page tells the assistant its run is already a row there, in the MCP tool result as serialized.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Semanticus.Engine;
using Semanticus.Engine.Query;
using Xunit;

namespace Semanticus.Tests
{
    public sealed class VFixHero27PipeTests
    {
        private const string Challenge = "verify-hero27-studio-challenge-0123456789abcdef";
        private const string Units = "EVALUATE ROW(\"Units 2024\", CALCULATE([Units]))";
        private const string PersonDax = "EVALUATE ROW(\"Total Cost 2025\", [Total Cost])";
        private const string Endpoint = "powerbi://api.powerbi.com/v1.0/myorg/Contoso Notebook";

        private static ResultSet Fake(string q)
        {
            var v = q.Contains("Units 2024", StringComparison.Ordinal) ? 291894m
                : q.Contains("Total Cost", StringComparison.Ordinal) ? 29871303.48m
                : q.Contains("[Units]", StringComparison.Ordinal) ? 13301m : 39137964.25m;
            return new ResultSet { Columns = new[] { new ColumnDef { Name = "[v]", Type = "Decimal" } }, Rows = new[] { new object[] { v } }, RowCount = 1, ElapsedMs = 30, Query = q };
        }

        private static async Task Model(LocalEngine e, SessionManager sessions)
        {
            await e.CreateModelAsync("Contoso Notebook", 1604, discardUnsaved: true);
            await e.CreateTableAsync("Sales", "human");
            await e.CreateColumnAsync("table:Sales", "Amount", "Double", "Amount", "human");
            await e.CreateMeasureAsync("table:Sales", "Net Sales", "SUM ( Sales[Amount] )", "human");
            await e.CreateMeasureAsync("table:Sales", "Units", "COUNTROWS ( Sales )", "human");
            await e.CreateMeasureAsync("table:Sales", "Total Cost", "SUM ( Sales[Amount] ) * 0.7", "human");
            e.SetLiveConnectionForTest(LiveConnection.ForTest("xmla", Endpoint, "Notebook", Fake));
            sessions.Current.LiveOrigin = new LiveOrigin(Endpoint, "Notebook", null);
        }

        private static async Task<(System.IO.Pipes.NamedPipeClientStream Pipe, StreamJsonRpc.JsonRpc Rpc)> Studio(string pipeName)
        {
            var pipe = new System.IO.Pipes.NamedPipeClientStream(".", pipeName, System.IO.Pipes.PipeDirection.InOut, System.IO.Pipes.PipeOptions.Asynchronous);
            await pipe.ConnectAsync(5000);
            await RpcHandshake.WriteAsync(pipe, RpcConnectionRole.Human, Challenge);
            await RpcHandshake.ReadAcceptedAsync(pipe);
            var rpc = new StreamJsonRpc.JsonRpc(RpcServer.CreateHandler(pipe));
            rpc.StartListening();
            return (pipe, rpc);
        }

        private static async Task<JArray> List(StreamJsonRpc.JsonRpc studio) => (JArray)(await studio.InvokeAsync<JObject>("queryNotebook", "list", null))["questions"];

        // What the Studio sends when the person presses Run in the open row's DAX tab (daxlab.tsx runQuery): the DAX the tab shows, then the
        // run filed under the row the page has open (activeRow.current).
        private static async Task<string> RunInDaxTab(StreamJsonRpc.JsonRpc studio, JToken row)
        {
            var dax = (string)row["dax"];
            var rs = await studio.InvokeAsync<ResultSet>("runDax", dax, 50000);
            Assert.Null(rs.Error);
            return await studio.InvokeAsync<string>("recordQueryRun", rs.Query ?? dax, rs.RowCount, rs.ElapsedMs, null, (string)row["id"]);
        }

        private static async Task EveryRowRunsUnderItself(StreamJsonRpc.JsonRpc studio, string[] ids, string when)
        {
            var before = await List(studio);
            foreach (var id in ids)
            {
                var row = before.Single(x => (string)x["id"] == id);
                Assert.False(string.IsNullOrWhiteSpace((string)row["dax"]), $"{when}: row {id} has its own DAX to show");
                var filed = await RunInDaxTab(studio, row);
                Assert.True(filed == id, $"{when}: Run in the DAX tab of a {(string)row["origin"]} {(string)row["kind"]} row was filed under {filed}, not under the row ({id})");
                var now = await List(studio);
                Assert.Equal(before.Count, now.Count);
                var after = now.Single(x => (string)x["id"] == id);
                Assert.Equal((string)row["origin"], (string)after["origin"]);
                Assert.Equal((string)row["kind"], (string)after["kind"]);
                Assert.Equal((string)row["dax"], (string)after["dax"]);
                // Every other row is untouched by this row's run.
                foreach (var other in before.Where(x => (string)x["id"] != id && ids.Contains((string)x["id"])))
                    Assert.True(JToken.DeepEquals(other, now.Single(x => (string)x["id"] == (string)other["id"])), $"{when}: running {id} changed row {other["id"]}");
                before = now;
            }
        }

        [Fact]
        public async Task Verify_run_in_the_dax_tab_of_every_kind_of_row_is_filed_under_that_row_also_after_a_restart()
        {
            using var f = new DecisionFixture(TestEntitlements.Pro);
            await Model(f.Engine, f.Sessions);
            var pipeName = TestPipe.Mint("semanticus-vfh27-");
            var stop = new System.Threading.CancellationTokenSource();
            var server = new RpcServer(f.Sessions, f.Engine, pipeName, Challenge);
            var serving = server.RunAsync(stop.Token);
            (System.IO.Pipes.NamedPipeClientStream Pipe, StreamJsonRpc.JsonRpc Rpc) studio = default;
            RemoteEngine agent = null;
            string[] ids;
            JArray rows;
            try
            {
                studio = await Studio(pipeName);
                agent = await RemoteEngine.ConnectAsync(pipeName, timeoutMs: 5000);
                var door = AssistantEngine.ForAssistant(agent);

                // The person asks in the page (a words row) and the page notes its number; the person runs a hand query (a DAX row).
                var asked = await studio.Rpc.InvokeAsync<JObject>("askData", new DataAsk { Measures = new[] { "Net Sales" } }, "human");
                var personWords = (string)asked["questionId"];
                Assert.True(await studio.Rpc.InvokeAsync<bool>("noteQueryRow", personWords, new QueryRowNote { Headline = "$39,137,964.25" }));
                var personDax = await studio.Rpc.InvokeAsync<string>("recordQueryRun", PersonDax, 1, 283.0, null, null);
                Assert.True(await studio.Rpc.InvokeAsync<bool>("noteQueryRow", personDax, new QueryRowNote { Headline = "Total Cost 2025: 29,871,303.48" }));
                // The person's Claude Code: ask_data (a words row of its own) and run_dax (a DAX row of its own).
                var a = await McpTools.AskData(door, measures: new[] { "Units" });
                Assert.Equal(DataAnswer.Answered, a.Outcome);
                var r = await McpTools.RunDax(door, Units);
                Assert.Null(r.Error);
                Assert.Null(r.Note);

                rows = await List(studio.Rpc);
                var agentWords = (string)rows.Single(x => (string)x["origin"] == "agent" && (string)x["kind"] == "words")["id"];
                var agentDax = (string)rows.Single(x => (string)x["origin"] == "agent" && (string)x["kind"] == "dax")["id"];
                Assert.Equal(a.QuestionId, agentWords);
                ids = new[] { personWords, personDax, agentWords, agentDax };
                Assert.Equal(4, ids.Distinct().Count());
                Assert.Equal(4, rows.Count(x => ids.Contains((string)x["id"])));
                // Four different queries, so each row's DAX tab has its own text.
                Assert.Equal(4, ids.Select(id => (string)rows.Single(x => (string)x["id"] == id)["dax"]).Distinct().Count());

                var outDir = Environment.GetEnvironmentVariable("VFH27_OUT");
                if (!string.IsNullOrEmpty(outDir))
                {
                    Directory.CreateDirectory(outDir);
                    File.WriteAllText(Path.Combine(outDir, "wire-rows.json"), new JObject
                    {
                        ["rows"] = rows, ["personWords"] = personWords, ["personDax"] = personDax, ["agentWords"] = agentWords, ["agentDax"] = agentDax,
                    }.ToString());
                }

                await EveryRowRunsUnderItself(studio.Rpc, ids, "same session");
                // The order does not matter: the assistant's rows first, then the person's.
                await EveryRowRunsUnderItself(studio.Rpc, Enumerable.Reverse(ids).ToArray(), "same session, reversed");
            }
            finally
            {
                agent?.Dispose();
                try { studio.Rpc?.Dispose(); studio.Pipe?.Dispose(); } catch { }
                stop.Cancel();
                try { await serving; } catch { }
                server.Dispose();
            }

            // A full restart: a new engine process (new SessionManager, new LocalEngine) on the same workspace, the same live model.
            using var sessions2 = new SessionManager();
            using var e2 = new LocalEngine(sessions2, TestEntitlements.Pro, f.Workspace);
            await Model(e2, sessions2);
            var pipe2 = TestPipe.Mint("semanticus-vfh27r-");
            using var stop2 = new System.Threading.CancellationTokenSource();
            using var server2 = new RpcServer(sessions2, e2, pipe2, Challenge);
            var serving2 = server2.RunAsync(stop2.Token);
            (System.IO.Pipes.NamedPipeClientStream Pipe, StreamJsonRpc.JsonRpc Rpc) studio2 = default;
            try
            {
                studio2 = await Studio(pipe2);
                var back = await List(studio2.Rpc);
                foreach (var id in ids)
                {
                    var was = rows.Single(x => (string)x["id"] == id);
                    var now = back.Single(x => (string)x["id"] == id);
                    Assert.Equal((string)was["dax"], (string)now["dax"]);
                    Assert.Equal((string)was["origin"], (string)now["origin"]);
                }
                await EveryRowRunsUnderItself(studio2.Rpc, ids, "after a restart");
            }
            finally
            {
                try { studio2.Rpc?.Dispose(); studio2.Pipe?.Dispose(); } catch { }
                stop2.Cancel();
                try { await serving2; } catch { }
            }
        }

        // P2 attack (FAILS on aaad4765): a hand query whose last column has a long name folds to "<name>: <number>" (noteFor). The engine
        // keeps at most 80 characters of it and cuts from the END, so the number is cut, not the label: "$39,137,964.25" is kept as "$39,137,"
        // and the folded row shows that as the number, whole and on one line. The page lets the label shorten; the store must too.
        [Fact]
        public async Task Verify_a_long_label_never_cuts_the_number_the_row_keeps()
        {
            using var f = new DecisionFixture(TestEntitlements.Pro);
            await Model(f.Engine, f.Sessions);
            var pipeName = TestPipe.Mint("semanticus-vfh27c-");
            using var stop = new System.Threading.CancellationTokenSource();
            using var server = new RpcServer(f.Sessions, f.Engine, pipeName, Challenge);
            var serving = server.RunAsync(stop.Token);
            (System.IO.Pipes.NamedPipeClientStream Pipe, StreamJsonRpc.JsonRpc Rpc) studio = default;
            try
            {
                studio = await Studio(pipeName);
                const string said = "Net Sales For Active Customers In The North Region In Fiscal Year 2025: $39,137,964.25";
                var id = await studio.Rpc.InvokeAsync<string>("recordQueryRun", "EVALUATE ROW(\"Year\", 2025, \"Net Sales For Active Customers In The North Region In Fiscal Year 2025\", [Net Sales])", 1, 40.0, null, null);
                Assert.True(await studio.Rpc.InvokeAsync<bool>("noteQueryRow", id, new QueryRowNote { Headline = said }));
                var kept = (string)(await List(studio.Rpc)).Single(x => (string)x["id"] == id)["headline"];
                var number = kept.Substring(kept.LastIndexOf(": ", StringComparison.Ordinal) + 2);
                Assert.True(number == "$39,137,964.25", $"the row keeps \"{kept}\": its number reads \"{number}\", not $39,137,964.25");
            }
            finally
            {
                try { studio.Rpc?.Dispose(); studio.Pipe?.Dispose(); } catch { }
                stop.Cancel();
                try { await serving; } catch { }
            }
        }

        [Fact]
        public async Task Verify_show_in_studio_on_the_query_page_after_run_dax_says_the_row_is_there_in_the_mcp_result()
        {
            using var f = new DecisionFixture(TestEntitlements.Pro);
            await Model(f.Engine, f.Sessions);
            var pipeName = TestPipe.Mint("semanticus-vfh27s-");
            using var stop = new System.Threading.CancellationTokenSource();
            using var server = new RpcServer(f.Sessions, f.Engine, pipeName, Challenge);
            var serving = server.RunAsync(stop.Token);
            (System.IO.Pipes.NamedPipeClientStream Pipe, StreamJsonRpc.JsonRpc Rpc) studio = default;
            RemoteEngine agent = null;
            try
            {
                studio = await Studio(pipeName);
                agent = await RemoteEngine.ConnectAsync(pipeName, timeoutMs: 5000);
                var door = AssistantEngine.ForAssistant(agent);
                var r = await McpTools.RunDax(door, Units);
                Assert.Null(r.Error);
                Assert.Contains(await List(studio.Rpc), x => (string)x["origin"] == "agent" && (string)x["dax"] == Units);

                var outDir = Environment.GetEnvironmentVariable("VFH27_OUT");
                var said = new JObject();
                foreach (var place in new[] { "edit/queries", "explore/ask" })
                {
                    var shown = await McpTools.ShowInStudio(door, place, "Here is the query I ran.");
                    // The MCP tool result is the result serialized with the SDK's own options: the line travels in it.
                    var wire = System.Text.Json.JsonSerializer.Serialize(shown, ModelContextProtocol.McpJsonUtilities.DefaultOptions);
                    said[place] = wire;
                    Assert.False(string.IsNullOrWhiteSpace(shown.Shown), place + ": no 'shown' line");
                    Assert.Contains("\"shown\"", wire);
                    Assert.Contains("already a row on that page", wire);
                    Assert.Contains("Do not tell the person to paste", wire);
                }
                var plan = await McpTools.ShowInStudio(door, "plan", "This is your Plan.");
                Assert.Null(plan.Shown);
                said["plan"] = System.Text.Json.JsonSerializer.Serialize(plan, ModelContextProtocol.McpJsonUtilities.DefaultOptions);
                if (!string.IsNullOrEmpty(outDir)) { Directory.CreateDirectory(outDir); File.WriteAllText(Path.Combine(outDir, "wire-p6.json"), said.ToString()); }
            }
            finally
            {
                agent?.Dispose();
                try { studio.Rpc?.Dispose(); studio.Pipe?.Dispose(); } catch { }
                stop.Cancel();
                try { await serving; } catch { }
            }
        }
    }
}
