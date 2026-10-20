// Verifier v-fix-hero26 (N1): the assistant's run_dax END TO END the way the product runs. The owner engine serves the pipe (RpcServer);
// the Studio is a Human pipe connection that hears model/activity and reads queryNotebook; the person's Claude Code is the MCP door, which
// holds AssistantEngine.ForAssistant(RemoteEngine) (Program.cs) and calls McpTools.RunDax. The live model is a fake LiveConnection, so
// no live call is made. When VFH26_OUT is set, the wire shapes the Studio receives are written there for the webview drive.

using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Semanticus.Engine;
using Semanticus.Engine.Query;
using Xunit;

namespace Semanticus.Tests
{
    public sealed class VFixHero26PipeTests
    {
        private const string Challenge = "verify-hero26-studio-challenge-0123456789abcdef";
        private const string Units = "EVALUATE ROW(\"Units 2024\", CALCULATE([Net Sales], 'Sales'[Amount] > 0))";
        private const string PersonDax = "EVALUATE ROW(\"Net Sales\", [Net Sales])";

        private static ResultSet Fake(string q) => q.Contains("Units 2024", StringComparison.Ordinal)
            ? new ResultSet { Columns = new[] { new ColumnDef { Name = "[Units 2024]", Type = "Int64" } }, Rows = new[] { new object[] { 291894L } }, RowCount = 1, ElapsedMs = 31 }
            : new ResultSet { Columns = new[] { new ColumnDef { Name = "[Net Sales]", Type = "Decimal" } }, Rows = new[] { new object[] { 39137964.25m } }, RowCount = 1, ElapsedMs = 29 };

        private static async Task<DecisionFixture> OpenAsync(bool history = true)
        {
            var f = new DecisionFixture(TestEntitlements.Pro);
            var e = f.Engine;
            await e.CreateModelAsync("Contoso Notebook", 1604, discardUnsaved: true);
            await e.CreateTableAsync("Sales", "human");
            await e.CreateColumnAsync("table:Sales", "Amount", "Double", "Amount", "human");
            await e.CreateMeasureAsync("table:Sales", "Net Sales", "SUM ( Sales[Amount] )", "human");
            e.SetLiveConnectionForTest(LiveConnection.ForTest("xmla", "powerbi://api.powerbi.com/v1.0/myorg/Contoso Notebook", "Notebook", Fake));
            if (history) f.Sessions.Current.LiveOrigin = new LiveOrigin("powerbi://api.powerbi.com/v1.0/myorg/Contoso Notebook", "Notebook", null);
            return f;
        }

        private static async Task<(System.IO.Pipes.NamedPipeClientStream Pipe, StreamJsonRpc.JsonRpc Rpc)> ConnectAsync(string pipeName, RpcConnectionRole role, ConcurrentQueue<JObject> heard = null)
        {
            var pipe = new System.IO.Pipes.NamedPipeClientStream(".", pipeName, System.IO.Pipes.PipeDirection.InOut, System.IO.Pipes.PipeOptions.Asynchronous);
            await pipe.ConnectAsync(5000);
            await RpcHandshake.WriteAsync(pipe, role, role == RpcConnectionRole.Human ? Challenge : null);
            await RpcHandshake.ReadAcceptedAsync(pipe);
            var rpc = new StreamJsonRpc.JsonRpc(RpcServer.CreateHandler(pipe));
            if (heard != null) rpc.AddLocalRpcMethod("model/activity", new Action<JObject>(n => heard.Enqueue(n)));
            rpc.StartListening();
            return (pipe, rpc);
        }

        // The pipe's own serializer, so a captured shape is the shape the Studio receives.
        private static Newtonsoft.Json.JsonSerializer Wire() { var s = new Newtonsoft.Json.JsonSerializer(); RpcServer.ConfigureSerializer(s); return s; }

        private static async Task Eventually(Func<bool> done)
        {
            for (var i = 0; i < 100 && !done(); i++) await Task.Delay(50);
        }

        [Fact]
        public async Task Verify_run_dax_over_the_agent_pipe_keeps_the_assistants_own_row_and_never_a_persons()
        {
            using var f = await OpenAsync();
            var e = f.Engine;
            var pipeName = TestPipe.Mint("semanticus-vfh26-");
            using var server = new RpcServer(f.Sessions, e, pipeName, Challenge);
            using var stop = new System.Threading.CancellationTokenSource();
            var serving = server.RunAsync(stop.Token);
            var heard = new ConcurrentQueue<JObject>();
            (System.IO.Pipes.NamedPipeClientStream Pipe, StreamJsonRpc.JsonRpc Rpc) studio = default, raw = default;
            RemoteEngine agent = null;
            try
            {
                studio = await ConnectAsync(pipeName, RpcConnectionRole.Human, heard);
                // The person: one asked question, one hand query with its kept number, both written through the Studio's own door.
                var asked = await studio.Rpc.InvokeAsync<JObject>("askData", new DataAsk { Measures = new[] { "Net Sales" } }, "human");
                var askedId = (string)asked["questionId"];
                Assert.False(string.IsNullOrEmpty(askedId));
                var person = await studio.Rpc.InvokeAsync<string>("recordQueryRun", PersonDax, 1, 40.0, null, null);
                Assert.True(await studio.Rpc.InvokeAsync<bool>("noteQueryRow", person, new QueryRowNote { Headline = "$39,137,964.25" }));
                var before = await studio.Rpc.InvokeAsync<JObject>("queryNotebook", "list", null);

                // The person's Claude Code: the MCP door's engine is ForAssistant(RemoteEngine), and run_dax is the real tool method.
                agent = await RemoteEngine.ConnectAsync(pipeName, timeoutMs: 5000);
                var door = AssistantEngine.ForAssistant(agent);
                var r = await McpTools.RunDax(door, Units);
                Assert.Null(r.Error);
                Assert.Null(r.Note);
                Assert.Equal(291894L, Convert.ToInt64(r.Rows[0][0]));

                await Eventually(() => heard.Any(a => (string)a["kind"] == "run_dax"));
                var ran = heard.Single(a => (string)a["kind"] == "run_dax");
                Assert.Equal("agent", (string)ran["origin"]);

                var after = await studio.Rpc.InvokeAsync<JObject>("queryNotebook", "list", null);
                var rows = (JArray)after["questions"];
                var mine = rows.Where(x => (string)x["origin"] == "agent").ToList();
                Assert.Single(mine);
                Assert.Equal("dax", (string)mine[0]["kind"]);
                Assert.Equal(Units, (string)mine[0]["dax"]);
                Assert.True(string.IsNullOrEmpty((string)mine[0]["question"]), "the assistant's row carries no question words: " + mine[0]);
                // Every person's row is byte-for-byte what it was before the assistant ran.
                foreach (var p in (JArray)before["questions"])
                    Assert.True(JToken.DeepEquals(p, rows.Single(x => (string)x["id"] == (string)p["id"])), "a person's row changed: " + p["id"]);

                // Run again by the assistant: the same assistant row, brought up to date; still never the person's.
                var again = await McpTools.RunDax(door, Units);
                Assert.Null(again.Note);
                var after2 = (JArray)(await studio.Rpc.InvokeAsync<JObject>("queryNotebook", "list", null))["questions"];
                Assert.Single(after2, x => (string)x["origin"] == "agent");

                // The agent connection, spoken to raw: it has no person writers at all, and its own writer cannot reach a person's row.
                raw = await ConnectAsync(pipeName, RpcConnectionRole.Agent);
                await Assert.ThrowsAnyAsync<Exception>(() => raw.Rpc.InvokeAsync<string>("recordQueryRun", Units, 1, 1.0, "999", person));
                await Assert.ThrowsAnyAsync<Exception>(() => raw.Rpc.InvokeAsync<bool>("noteQueryRow", person, new QueryRowNote { Headline = "291,894" }));
                await Assert.ThrowsAnyAsync<Exception>(() => raw.Rpc.InvokeAsync<bool>("noteQueryRow", (string)mine[0]["id"], new QueryRowNote { Headline = "291,894" }));
                await Assert.ThrowsAnyAsync<Exception>(() => agent.NoteQueryRowAsync(person, new QueryRowNote { Headline = "291,894" }));
                // The person's own DAX, run by the assistant: an assistant row of its own; the person's row keeps its number and origin.
                var twin = await raw.Rpc.InvokeAsync<string>("recordAgentQueryRun", PersonDax, 9, 1.0, "291,894");
                Assert.NotEqual(person, twin);
                var end = (JArray)(await studio.Rpc.InvokeAsync<JObject>("queryNotebook", "list", null))["questions"];
                var theirs = end.Single(x => (string)x["id"] == person);
                Assert.Equal("$39,137,964.25", (string)theirs["headline"]);
                Assert.Equal("human", (string)theirs["origin"]);
                Assert.Equal(1, (int)theirs["rowCount"]);
                Assert.Equal("agent", (string)end.Single(x => (string)x["id"] == twin)["origin"]);
                Assert.True(JToken.DeepEquals(((JArray)before["questions"]).Single(x => (string)x["id"] == askedId), end.Single(x => (string)x["id"] == askedId)));

                var outDir = Environment.GetEnvironmentVariable("VFH26_OUT");
                if (!string.IsNullOrEmpty(outDir))
                {
                    Directory.CreateDirectory(outDir);
                    File.WriteAllText(Path.Combine(outDir, "wire-n1.json"), new JObject
                    {
                        ["before"] = before, ["after"] = after, ["activity"] = ran, ["units"] = Units, ["personDax"] = PersonDax, ["person"] = person, ["asked"] = askedId,
                    }.ToString());
                }
            }
            finally
            {
                agent?.Dispose();
                try { raw.Rpc?.Dispose(); raw.Pipe?.Dispose(); } catch { }
                try { studio.Rpc?.Dispose(); studio.Pipe?.Dispose(); } catch { }
                stop.Cancel();
                try { await serving; } catch { }
            }
        }

        // N2 + N3 end to end: the assistant saves two year checks with save_test (filterColumn + filterValue, the hero's shape), runs
        // run_tests over the agent pipe, keeps the report with save_evidence; every place a person or the assistant reads the count
        // says the same number of checks. The model has eight more measures with no saved check (the hero's "2 of 11").
        [Fact]
        public async Task Verify_save_test_year_checks_run_and_every_count_says_the_same_checks()
        {
            var ws = Path.Combine(Path.GetTempPath(), "smx-vfh26-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(ws);
            var sessions = new SessionManager();
            var e = new LocalEngine(sessions, TestEntitlements.Pro, ws);
            var pipeName = TestPipe.Mint("semanticus-vfh26t-");
            using var stop = new System.Threading.CancellationTokenSource();
            RpcServer server = null; Task serving = null; RemoteEngine agent = null;
            var heard = new ConcurrentQueue<ActivityEvent>();
            sessions.Bus.Activity += a => heard.Enqueue(a);
            var asked = new ConcurrentQueue<string>();
            try
            {
                await e.CreateModelAsync("Northwind Checks", 1604);
                sessions.Current.SourcePath = Path.Combine(ws, "Northwind Checks.bim");
                await e.CreateTableAsync("Sales", "human");
                await e.CreateColumnAsync("table:Sales", "Amount", "Double", "Amount", "human");
                await e.CreateTableAsync("Date", "human");
                await e.CreateColumnAsync("table:Date", "Year", "Int64", "Year", "human");
                await e.CreateMeasureAsync("table:Sales", "Net Sales", "SUM ( Sales[Amount] )", "human");
                await e.CreateMeasureAsync("table:Sales", "Units", "COUNTROWS ( Sales )", "human");
                for (var i = 1; i <= 8; i++) await e.CreateMeasureAsync("table:Sales", "Extra " + i, "1", "human");
                e.SetLiveConnectionForTest(LiveConnection.ForTest("xmla", "powerbi://api.powerbi.com/v1.0/myorg/Northwind", "Northwind Checks", q =>
                {
                    asked.Enqueue(q);
                    if (q.Contains("\"2025\"", StringComparison.Ordinal) || q.Contains("\"2024\"", StringComparison.Ordinal))
                        return new ResultSet { Error = "Cannot compare text values with whole numbers. Technical Details: RootActivityId: 4f1c0a" };
                    var v = q.Contains("Units", StringComparison.Ordinal) ? 291894m : q.Contains("2025", StringComparison.Ordinal) ? 39137964.25m : 1m;
                    return new ResultSet { Columns = new[] { new ColumnDef { Name = "v", Type = "Decimal" } }, Rows = new[] { new object[] { v } }, RowCount = 1 };
                }));
                server = new RpcServer(sessions, e, pipeName, Challenge);
                serving = server.RunAsync(stop.Token);
                agent = await RemoteEngine.ConnectAsync(pipeName, timeoutMs: 5000);
                var door = AssistantEngine.ForAssistant(agent);

                TestDefinition Check(string title, string measure, string expected, string year) => new TestDefinition
                {
                    Kind = TestKinds.MeasureValue, Title = title, TargetRef = "measure:Sales/" + measure,
                    ParamsJson = "{\"measureRef\":\"measure:Sales/" + measure + "\",\"expectedValue\":\"" + expected + "\",\"filterColumn\":\"'Date'[Year]\",\"filterValue\":\"" + year + "\"}",
                };
                await McpToolsTesting.SaveTest(door, Check("Net Sales 2025", "Net Sales", "39137964.25", "2025"));
                await McpToolsTesting.SaveTest(door, Check("Units 2024", "Units", "1000", "2024"));

                var run = await McpToolsTesting.RunTests(door, persist: true);
                // N2: the year went in as a number and both checks reached a verdict.
                Assert.DoesNotContain(asked, q => q.Contains("\"2025\"", StringComparison.Ordinal) || q.Contains("\"2024\"", StringComparison.Ordinal));
                var saved = run.Reconciles.Where(o => !o.DefId.StartsWith("unmapped:", StringComparison.Ordinal)).ToList();
                Assert.Equal(2, saved.Count);
                Assert.Contains(saved, o => o.Title == "Net Sales 2025" && o.Verdict == Verdict.Pass);
                Assert.Contains(saved, o => o.Title == "Units 2024" && o.Verdict == Verdict.Fail);
                Assert.True(run.Reconciles.Length > saved.Count, "the run lists coverage rows for the measures with no saved check");

                // N3: the tool's headline, the assistant's note on the page, the report and the kept report say the same checks.
                var headline = run.Headline;
                await Eventually(() => heard.Any(a => a.Kind == "run_tests"));
                var note = heard.Last(a => a.Kind == "run_tests").Label;
                var report = (await agent.ExportTestReportAsync()).Markdown ?? "";
                var kept = await McpToolsEvidence.SaveEvidence(door, "tests");
                var library = await agent.ListEvidenceAsync();
                var item = library.Items.Single(x => x.Kind == "test-suite");
                var outDir = Environment.GetEnvironmentVariable("VFH26_OUT");
                if (!string.IsNullOrEmpty(outDir))
                {
                    Directory.CreateDirectory(outDir);
                    File.WriteAllText(Path.Combine(outDir, "wire-n3.json"), new JObject
                    {
                        ["headline"] = headline, ["note"] = note, ["reportCheckLine"] = report.Split('\n').FirstOrDefault(l => l.StartsWith("- Checks:", StringComparison.Ordinal)),
                        ["kept"] = $"{item.Verified} of {item.Total}", ["run"] = JToken.FromObject(run, Wire()),
                        ["tests"] = JToken.FromObject(await agent.ListTestDefinitionsAsync(), Wire()),
                    }.ToString());
                }
                Assert.Equal("1 of 2 checks pass: 1 differs.", headline);
                Assert.Contains("1 of 2 checks pass", note);
                Assert.Contains("- Checks: 1 of 2 checks pass.", report);
                Assert.Equal(1, item.Verified); Assert.Equal(2, item.Total);
                Assert.DoesNotContain("run_tests 1.0", report);
            }
            finally
            {
                agent?.Dispose(); stop.Cancel(); try { if (serving != null) await serving; } catch { }
                server?.Dispose(); e.Dispose(); sessions.Dispose();
                try { Directory.Delete(ws, true); } catch { }
            }
        }

        [Fact]
        public async Task Verify_run_dax_over_the_agent_pipe_says_in_the_tool_result_when_the_row_is_not_kept()
        {
            using var f = await OpenAsync(history: false);
            var pipeName = TestPipe.Mint("semanticus-vfh26n-");
            using var server = new RpcServer(f.Sessions, f.Engine, pipeName, Challenge);
            using var stop = new System.Threading.CancellationTokenSource();
            var serving = server.RunAsync(stop.Token);
            RemoteEngine agent = null;
            try
            {
                agent = await RemoteEngine.ConnectAsync(pipeName, timeoutMs: 5000);
                var r = await McpTools.RunDax(AssistantEngine.ForAssistant(agent), Units);
                Assert.Null(r.Error);
                Assert.Equal(291894L, Convert.ToInt64(r.Rows[0][0]));
                Assert.False(string.IsNullOrWhiteSpace(r.Note), "a run that was not kept must say so in the tool result");
                Assert.Contains("not kept", r.Note);
                // The MCP tool result is the ResultSet serialized: the note travels in it.
                var wire = System.Text.Json.JsonSerializer.Serialize(r);
                Assert.Contains("not kept", wire);
            }
            finally { agent?.Dispose(); stop.Cancel(); try { await serving; } catch { } }
        }
    }
}
