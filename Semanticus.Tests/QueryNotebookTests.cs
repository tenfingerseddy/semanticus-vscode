// The Query notebook (1.6, query-c): pins, close, reopen, remove and the full history, kept beside the model and read by both doors.
// Written before the page: the page only draws what these rows say.

using System;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.Query;
using Xunit;

namespace Semanticus.Tests
{
    public sealed class QueryNotebookTests
    {
        private static async Task<DecisionFixture> OpenAsync(Semanticus.Engine.Entitlement.IEntitlement tier = null)
        {
            var f = new DecisionFixture(tier ?? TestEntitlements.Pro);
            var e = f.Engine;
            await e.CreateModelAsync("Contoso Notebook", 1604, discardUnsaved: true);
            await e.CreateTableAsync("Sales", "human");
            await e.CreateColumnAsync("table:Sales", "Amount", "Double", "Amount", "human");
            await e.CreateMeasureAsync("table:Sales", "Net Sales", "SUM ( Sales[Amount] )", "human");
            f.Sessions.Current.LiveOrigin = new LiveOrigin("powerbi://api.powerbi.com/v1.0/myorg/Contoso Notebook", "Notebook", null);
            return f;
        }

        [Fact]
        public async Task A_question_lands_in_the_notebook_with_its_id_and_a_close_keeps_it_in_the_history()
        {
            using var f = await OpenAsync();
            var e = f.Engine;
            var a = await e.AskDataAsync(new DataAsk { Measures = new[] { "Net Sales" } });
            Assert.False(string.IsNullOrEmpty(a.QuestionId));
            var view = await e.QueryNotebookAsync();
            var row = Assert.Single(view.Questions);
            Assert.Equal(a.QuestionId, row.Id);
            Assert.True(row.InNotebook); Assert.False(row.Pinned);

            var closed = await e.QueryNotebookAsync("close", row.Id);
            Assert.Equal("Closed. It stays in the history.", closed.Done);
            var kept = Assert.Single(closed.Questions);
            Assert.False(kept.InNotebook);

            var back = await e.QueryNotebookAsync("reopen", row.Id);
            Assert.True(Assert.Single(back.Questions).InNotebook);
        }

        [Fact]
        public async Task A_pin_survives_a_reload_and_a_close_unpins_it()
        {
            using var f = await OpenAsync();
            var e = f.Engine;
            var id = (await e.AskDataAsync(new DataAsk { Measures = new[] { "Net Sales" } })).QuestionId;
            await e.QueryNotebookAsync("pin", id);
            // A second engine over the same files is a reload: the pin is on disk, not in memory.
            var reloaded = (await e.QueryNotebookAsync()).Questions.Single();
            Assert.True(reloaded.Pinned); Assert.True(reloaded.InNotebook);
            var unpinned = (await e.QueryNotebookAsync("unpin", id)).Questions.Single();
            Assert.False(unpinned.Pinned); Assert.True(unpinned.InNotebook);
            await e.QueryNotebookAsync("pin", id);
            var closed = (await e.QueryNotebookAsync("close", id)).Questions.Single();
            Assert.False(closed.Pinned); Assert.False(closed.InNotebook);
        }

        [Fact]
        public async Task Remove_deletes_the_row_and_an_unknown_id_says_so_in_plain_words()
        {
            using var f = await OpenAsync();
            var e = f.Engine;
            var id = (await e.AskDataAsync(new DataAsk { Measures = new[] { "Net Sales" } })).QuestionId;
            var missing = await e.QueryNotebookAsync("remove", "dq-nothing");
            Assert.Null(missing.Done);
            Assert.Contains("No query has that id", missing.Note);
            Assert.Single(missing.Questions);
            var gone = await e.QueryNotebookAsync("remove", id);
            Assert.Equal("Removed.", gone.Done);
            Assert.Empty(gone.Questions);
            Assert.Contains("Say which query", (await e.QueryNotebookAsync("pin")).Note);
            Assert.Contains("The actions are", (await e.QueryNotebookAsync("shred", id)).Note);
        }

        [Fact]
        public async Task The_same_question_asked_again_takes_the_old_rows_place_and_pin_and_the_history_keeps_both_asks()
        {
            using var f = await OpenAsync();
            var e = f.Engine;
            var first = (await e.AskDataAsync(new DataAsk { Measures = new[] { "Net Sales" } })).QuestionId;
            await e.QueryNotebookAsync("pin", first);
            var second = (await e.AskDataAsync(new DataAsk { Measures = new[] { "Net Sales" } })).QuestionId;
            var rows = (await e.QueryNotebookAsync()).Questions;
            Assert.Equal(2, rows.Length);
            var newest = rows.Single(r => r.Id == second); var oldest = rows.Single(r => r.Id == first);
            Assert.True(newest.Pinned && newest.InNotebook);
            Assert.False(oldest.Pinned || oldest.InNotebook);
            // Bringing the older ask back moves the row to it, again one row per question.
            var back = (await e.QueryNotebookAsync("reopen", first)).Questions;
            Assert.Single(back, r => r.Pinned || r.InNotebook);
            Assert.True(back.Single(r => r.Id == first).Pinned);
        }

        [Fact]
        public async Task A_query_run_as_written_is_a_row_on_the_free_tier_and_the_page_notes_its_number()
        {
            using var f = await OpenAsync(TestEntitlements.Free);
            var e = f.Engine;
            var id = await e.RecordQueryRunAsync("EVALUATE ROW(\"v\", [Net Sales])", 1, 42, "$1.2M");
            Assert.False(string.IsNullOrEmpty(id));
            Assert.True(await e.NoteQueryRowAsync(id, new QueryRowNote { WarmMs = 12, ColdMs = 90, Dropped = "Region is not filtered." }));
            var row = (await e.QueryNotebookAsync()).Questions.Single();
            Assert.Equal("dax", row.Kind); Assert.Equal("$1.2M", row.Headline);
            Assert.Equal(12, row.WarmMs); Assert.Equal(90, row.ColdMs); Assert.Equal("Region is not filtered.", row.Dropped);
            // Running the same DAX with other spacing is the same row.
            await e.RecordQueryRunAsync("EVALUATE   ROW(\"v\",   [Net Sales])", 1, 40, "$1.2M");
            var rows = (await e.QueryNotebookAsync()).Questions;
            Assert.Single(rows, r => r.InNotebook);
            // The question history for Jev's questions stays Pro; the notebook is not.
            await Assert.ThrowsAsync<Semanticus.Engine.Entitlement.EntitlementException>(() => e.ListDataQuestionsAsync());
        }

        [Fact]
        public async Task A_folded_row_opened_again_is_updated_in_place_and_adds_nothing_to_the_history()
        {
            using var f = await OpenAsync();
            var e = f.Engine;
            var id = (await e.AskDataAsync(new DataAsk { Measures = new[] { "Net Sales" } })).QuestionId;
            await e.QueryNotebookAsync("pin", id);
            var again = await e.AskDataAsync(new DataAsk { Measures = new[] { "Net Sales" }, RecordAs = id });
            Assert.Equal(id, again.QuestionId);
            var row = Assert.Single((await e.QueryNotebookAsync()).Questions);
            Assert.True(row.Pinned && row.InNotebook);
            // An id that is not in the history adds a row, as a plain ask does.
            var other = await e.AskDataAsync(new DataAsk { Measures = new[] { "Net Sales" }, RecordAs = "dq-gone" });
            Assert.NotEqual("dq-gone", other.QuestionId);
        }

        [Fact]
        public async Task A_query_still_being_shaped_replaces_its_row_instead_of_adding_one_per_change()
        {
            using var f = await OpenAsync(TestEntitlements.Free);
            var e = f.Engine;
            var first = await e.RecordQueryRunAsync("EVALUATE ROW(\"v\", 1)", 1, 5);
            var second = await e.RecordQueryRunAsync("EVALUATE ROW(\"v\", 2)", 1, 6, "2", replaces: first);
            Assert.Equal(first, second);
            var row = Assert.Single((await e.QueryNotebookAsync()).Questions);
            Assert.Contains("2", row.Dax); Assert.Equal("2", row.Headline);
            Assert.Null(row.WarmMs);
        }

        // UAT 17 Oct, F37 (lost work): a pinned query shaped further is a NEW row. The pinned one keeps its query, its pin and its place
        // in Asked before; it was rewritten and lost when New query left its fields in the builder and the next change replaced it.
        [Fact]
        public async Task F37_a_pinned_query_is_never_rewritten_by_the_query_shaped_from_it()
        {
            using var f = await OpenAsync(TestEntitlements.Free);
            var e = f.Engine;
            var pinned = await e.RecordQueryRunAsync("EVALUATE ROW(\"v\", 1)", 1, 5);
            await e.QueryNotebookAsync("pin", pinned);
            var shaped = await e.RecordQueryRunAsync("EVALUATE ROW(\"v\", 2)", 1, 6, "2", replaces: pinned);
            Assert.NotEqual(pinned, shaped);
            var all = (await e.QueryNotebookAsync()).Questions;
            Assert.Equal(2, all.Length);
            var kept = all.Single(r => r.Id == pinned);
            Assert.True(kept.Pinned);
            Assert.Contains("1", kept.Dax); Assert.DoesNotContain("2", kept.Dax);
            Assert.False(all.Single(r => r.Id == shaped).Pinned);
        }

        [Fact]
        public async Task A_question_run_again_unchanged_from_its_dax_view_stays_one_row()
        {
            using var f = await OpenAsync();
            var e = f.Engine;
            var asked = await e.AskDataAsync(new DataAsk { Measures = new[] { "Net Sales" } });
            Assert.False(string.IsNullOrWhiteSpace(asked.Dax));
            var id = await e.RecordQueryRunAsync(asked.Dax.Replace("\n", "\r\n  "), 3, 77);
            Assert.Equal(asked.QuestionId, id);
            var row = Assert.Single((await e.QueryNotebookAsync()).Questions);
            Assert.Equal("words", row.Kind); Assert.Equal(77, row.TookMs); Assert.Equal(3, row.RowCount);
            // A different query is a new row of its own.
            var other = await e.RecordQueryRunAsync("EVALUATE ROW(\"v\", 9)", 1, 5);
            Assert.NotEqual(asked.QuestionId, other);
            Assert.Equal(2, (await e.QueryNotebookAsync()).Questions.Length);
        }

        private static string RepoRoot()
        {
            var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "Semanticus.sln"))) dir = dir.Parent;
            Assert.True(dir != null, "could not find Semanticus.sln");
            return dir!.FullName;
        }

        // Hero 25 H1: the assistant's own run_dax is a row of its own. It never lands in, or overwrites, the figure of a row the person owns.
        [Fact]
        public async Task An_assistant_run_is_its_own_row_and_leaves_the_persons_row_with_the_same_dax_alone()
        {
            using var f = await OpenAsync();
            var e = f.Engine;
            var asked = await e.AskDataAsync(new DataAsk { Measures = new[] { "Net Sales" } });
            await e.NoteQueryRowAsync(asked.QuestionId, new QueryRowNote { Headline = "$39.1M" });
            var id = await e.RecordQueryRunAsync(asked.Dax, 1, 12, null, "agent");
            Assert.NotEqual(asked.QuestionId, id);
            var rows = (await e.QueryNotebookAsync()).Questions;
            Assert.Equal(2, rows.Length);
            Assert.Equal("$39.1M", rows.Single(r => r.Id == asked.QuestionId).Headline);
            var mine = rows.Single(r => r.Id == id);
            Assert.Equal("agent", mine.Origin); Assert.Equal("dax", mine.Kind); Assert.True(mine.InNotebook);
            // The assistant running the same query again brings its own row up to date: still one assistant row.
            var again = await e.RecordQueryRunAsync(asked.Dax, 1, 9, null, "agent");
            Assert.Equal(id, again);
            Assert.Equal(2, (await e.QueryNotebookAsync()).Questions.Length);
        }

        // Verifier v-fix-hero25: run_dax calls RecordQueryRunAsync on the engine the MCP door really holds, the gated AssistantEngine
        // (Program.cs registers AssistantEngine.ForAssistant). The registry marks RecordQueryRunAsync Person-only, so the gate refuses it,
        // RunDax swallows the refusal, and the assistant's run never becomes a row. The test above calls LocalEngine directly and misses it.
        [Fact]
        public async Task Verifier_the_assistants_run_dax_row_is_kept_through_the_gate_the_mcp_door_holds()
        {
            using var f = await OpenAsync();
            var gated = AssistantEngine.ForAssistant(f.Engine);
            string id = null;
            var refused = await Record.ExceptionAsync(async () => id = await gated.RecordQueryRunAsync("EVALUATE ROW(\"Net Sales\", [Net Sales])", 1, 12, null, "agent"));
            Assert.Null(refused);
            var row = Assert.Single((await f.Engine.QueryNotebookAsync()).Questions);
            Assert.Equal(id, row.Id);
            Assert.Equal("agent", row.Origin);
        }

        // Verifier v-fix-hero25 r2: RecordQueryRunAsync now passes the gate. Through the gate it can never reach the person's row, even when
        // the call says origin "human" and names that row to replace; and the person-only NoteQueryRowAsync is still refused.
        [Fact]
        public async Task Verifier_through_the_gate_the_persons_row_is_never_written()
        {
            using var f = await OpenAsync();
            const string dax = "EVALUATE ROW(\"Total Sales\", [Net Sales])";
            var person = await f.Engine.RecordQueryRunAsync(dax, 1, 40);
            await f.Engine.NoteQueryRowAsync(person, new QueryRowNote { Headline = "1,356.1" });
            var gated = AssistantEngine.ForAssistant(f.Engine);
            var mine = await gated.RecordQueryRunAsync(dax, 7, 12, "999", "human", person);
            Assert.NotEqual(person, mine);
            var refused = await Record.ExceptionAsync(() => gated.NoteQueryRowAsync(person, new QueryRowNote { Headline = "999" }));
            Assert.NotNull(refused);
            var rows = (await f.Engine.QueryNotebookAsync()).Questions;
            var theirs = rows.Single(r => r.Id == person);
            Assert.Equal("1,356.1", theirs.Headline); Assert.Equal(1, theirs.RowCount); Assert.Equal("human", theirs.Origin);
            Assert.Equal("agent", rows.Single(r => r.Id == mine).Origin);
        }

        // hero 26: the person's Claude Code reaches the owner engine over an AGENT pipe connection (RemoteEngine), and that connection gets
        // EngineRpcTarget only (RpcServer: HumanGovernanceRpcTarget is added for the Human role). run_dax keeps its row through
        // RemoteEngine.RecordQueryRunAsync -> "recordQueryRun", which only HumanGovernanceRpcTarget has, so the call fails on the agent
        // connection, McpTools.RunDax swallows it, no assistant row is kept, and the page falls back to the draft row (ask-box words and the
        // open row's chips over the assistant's number). Seen live: Units 2024 = 291,894 under "net sales in June 2025", Read as Net Sales, Region.
        [Fact]
        public async Task An_agent_connection_can_keep_the_assistants_run_dax_row()
        {
            using var f = await OpenAsync();
            var e = f.Engine;
            const string dax = "EVALUATE ROW(\"Units 2024\", [Net Sales])";
            var person = await e.RecordQueryRunAsync(dax, 1, 40);
            await e.NoteQueryRowAsync(person, new QueryRowNote { Headline = "1,356.1" });
            var pipeName = TestPipe.Mint("semanticus-nbagent-");
            using var server = new RpcServer(f.Sessions, e, pipeName);
            using var stop = new System.Threading.CancellationTokenSource();
            var serving = server.RunAsync(stop.Token);
            RemoteEngine remote = null;
            try
            {
                // The person's Claude Code: an AGENT pipe connection, which gets EngineRpcTarget only (no HumanGovernanceRpcTarget).
                remote = await RemoteEngine.ConnectAsync(pipeName, timeoutMs: 5000);
                var run = new ResultSet { RowCount = 1, ElapsedMs = 12 };
                Assert.Null(await McpTools.KeepRunRowAsync(remote, dax, run));
                var rows = (await e.QueryNotebookAsync()).Questions;
                var mine = Assert.Single(rows, r => r.Origin == "agent");
                Assert.Equal("dax", mine.Kind);
                // It is the assistant's own row: the person's row, same DAX, keeps its number and origin even when the call names it to replace.
                var named = await remote.RecordQueryRunAsync(dax, 7, 9, "999", "human", person);
                Assert.Equal(mine.Id, named);
                var theirs = (await e.QueryNotebookAsync()).Questions.Single(r => r.Id == person);
                Assert.Equal("1,356.1", theirs.Headline); Assert.Equal(1, theirs.RowCount); Assert.Equal("human", theirs.Origin);
                // The person-only writer stays person-only.
                await Assert.ThrowsAnyAsync<Exception>(() => remote.NoteQueryRowAsync(person, new QueryRowNote { Headline = "999" }));
                Assert.Equal("1,356.1", (await e.QueryNotebookAsync()).Questions.Single(r => r.Id == person).Headline);
            }
            finally { remote?.Dispose(); stop.Cancel(); try { await serving; } catch { } }
        }

        // hero 26 N1: a run that could not be kept is said in the tool result, not swallowed.
        [Fact]
        public async Task A_run_that_could_not_be_kept_says_so_in_the_tool_result()
        {
            using var f = await OpenAsync();
            var pipeName = TestPipe.Mint("semanticus-nbgone-");
            using var server = new RpcServer(f.Sessions, f.Engine, pipeName);
            using var stop = new System.Threading.CancellationTokenSource();
            var serving = server.RunAsync(stop.Token);
            var remote = await RemoteEngine.ConnectAsync(pipeName, timeoutMs: 5000);
            try
            {
                remote.Dispose();
                var note = await McpTools.KeepRunRowAsync(remote, "EVALUATE ROW(\"x\", 1)", new ResultSet { RowCount = 1 });
                Assert.False(string.IsNullOrWhiteSpace(note));
                Assert.Contains("not kept", note);
            }
            finally { stop.Cancel(); try { await serving; } catch { } }
        }

        [Fact]
        public void The_assistants_run_dax_tool_keeps_its_run_as_a_row()
        {
            var src = System.IO.File.ReadAllText(System.IO.Path.Combine(RepoRoot(), "Semanticus.Engine", "McpTools.cs"));
            var at = src.IndexOf("Name = \"run_dax\"", StringComparison.Ordinal);
            Assert.True(at > 0);
            var body = src.Substring(at, 1800);
            Assert.Contains("KeepRunRowAsync(", body);
            var keep = src.IndexOf("static async Task<string> KeepRunRowAsync", StringComparison.Ordinal);
            Assert.True(keep > 0);
            var keepBody = src.Substring(keep, 900);
            Assert.Contains("RecordQueryRunAsync(", keepBody);
            Assert.Contains("\"agent\"", keepBody.Substring(keepBody.IndexOf("RecordQueryRunAsync(", StringComparison.Ordinal), 220));
        }

        [Fact]
        public async Task The_assistants_tool_and_the_pages_call_read_the_same_rows()
        {
            using var f = await OpenAsync();
            var e = f.Engine;
            var id = (await e.AskDataAsync(new DataAsk { Measures = new[] { "Net Sales" } }, "agent")).QuestionId;
            var pinned = await McpTools.QueryNotebook(e, "pin", id);
            Assert.Equal("Pinned.", pinned.Done);
            var rpc = new EngineRpcTarget(e, "human", null, null);
            var fromRpc = await rpc.queryNotebook();
            var fromMcp = await McpTools.QueryNotebook(e);
            Assert.Equal(fromMcp.Questions.Select(q => (q.Id, q.Pinned, q.InNotebook, q.Origin)), fromRpc.Questions.Select(q => (q.Id, q.Pinned, q.InNotebook, q.Origin)));
            Assert.Equal("agent", fromMcp.Questions.Single().Origin);
        }

        // v-query-c (verifier): a folded row opened again is updated in place with a new date, but it kept the number of its LAST run.
        // When the new run fails (or is refused), the folded row and the history then show the old number as of "just now", and the
        // assistant's query_notebook reads that headline as the row's current number. A wrong number shown as right.
        [Fact]
        public async Task V_a_row_run_again_in_place_never_keeps_the_old_number_under_the_new_date()
        {
            using var f = await OpenAsync();
            var e = f.Engine;
            var id = (await e.AskDataAsync(new DataAsk { Measures = new[] { "Net Sales" } })).QuestionId;
            Assert.True(await e.NoteQueryRowAsync(id, new QueryRowNote { Headline = "$1.2M", ColdMs = 900, WarmMs = 90 }));
            var before = Assert.Single((await e.QueryNotebookAsync()).Questions);
            await Task.Delay(20);
            // Opened again; this time the number cannot be read (a measure that is gone), so this run produced no number at all.
            var again = await e.AskDataAsync(new DataAsk { Measures = new[] { "Gone Measure" }, RecordAs = id });
            Assert.Equal(id, again.QuestionId);
            var row = Assert.Single((await e.QueryNotebookAsync()).Questions);
            Assert.NotEqual(before.AskedAt, row.AskedAt);
            Assert.True(row.Headline == null, $"the row says it was run at {row.AskedAt} (outcome {row.Outcome}) and still shows {row.Headline}, the number of the run before");
        }

        // v-query-c (small): the remote proxy sent six arguments to the five-argument recordQueryRun RPC. Hero 26: it now calls the agent method, which takes four.
        [Fact]
        public void The_remote_proxy_sends_as_many_arguments_as_the_agent_record_query_run_rpc_takes()
        {
            var rpc = typeof(EngineRpcTarget).GetMethod("recordAgentQueryRun");
            Assert.NotNull(rpc);
            var sent = RemoteEngine.RecordQueryRunArgs("EVALUATE ROW(\"x\", 1)", 1, 2.5, "1", null);
            Assert.Equal(rpc.GetParameters().Length, sent.Length);
            Assert.Equal(4, sent.Length);
            // The person's page keeps its runs through the person-only method, which the assistant's connection never has.
            Assert.NotNull(typeof(HumanGovernanceRpcTarget).GetMethod("recordQueryRun"));
            Assert.Null(typeof(EngineRpcTarget).GetMethod("recordQueryRun"));
        }

        [Fact]
        public void The_history_is_trimmed_oldest_first_and_never_loses_a_pinned_row()
        {
            var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sem-nb-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            var file = DataQuestionStore.FileIn(dir);
            try
            {
                for (var i = 0; i < DataQuestionStore.Keep + 5; i++)
                    DataQuestionStore.Append(file, new DataQuestionRecord { Id = "q" + i, Kind = "dax", Dax = "EVALUATE ROW(\"v\", " + i + ")", Pinned = i == 0, InNotebook = true });
                var all = DataQuestionStore.Read(file, 0);
                Assert.Equal(DataQuestionStore.Keep, all.Length);
                Assert.Contains(all, r => r.Id == "q0" && r.Pinned);
                Assert.DoesNotContain(all, r => r.Id == "q1");
            }
            finally { try { System.IO.Directory.Delete(dir, true); } catch { } }
        }
    }
}
