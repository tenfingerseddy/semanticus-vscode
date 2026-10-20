// m16/jev-batch (Kane, 15 Oct 2026: "Yes": make Jev batching the first 1.5.1 job): ONE request for the whole model.
//
// Jev answers as many questions as fit its context in one call, in parallel. The Plan used to ask a table at a time (a 30-table model
// made 75 calls to build and 514 to run, 20 s and 128 s at a realistic 1.3 s a call). These tests pin the new shape: the requests are
// cut by Jev's context (JevTableBatch.MaxBodyBytes), never by table; every shared fact goes once; an answer is mapped back by id; and
// a bad answer for one question loses only that question.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Semanticus.Decisions;
using Semanticus.Engine;
using Semanticus.Engine.Decisions;
using Xunit;

namespace Semanticus.Tests
{
    public sealed class JevBatchAcrossTablesTests
    {
        private static async Task<(DecisionFixture F, CountingRelay Relay)> OpenAsync(int tables, CountingRelay relay = null)
        {
            var dir = Path.Combine(Path.GetTempPath(), "jev-across-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var bim = Path.Combine(dir, "many.bim");
            ManyTablesModel.Write(bim, tables);
            var f = new DecisionFixture(TestEntitlements.Pro);
            await f.Engine.OpenAsync(bim);
            await f.SaveAsAsync("many.bim");
            await f.ConsentAsync();
            relay ??= new CountingRelay();
            f.Engine.UseDecisionsForTest(new DecisionService(relay, new DecisionBudget(), answers: new DecisionAnswerStore()));
            f.Engine.JevBatchedForTest = _ => true;
            return (f, relay);
        }

        private static async Task ReadUntilQuietAsync(DecisionFixture f, CountingRelay relay)
        {
            var seen = -1;
            for (var read = 0; read < 12; read++)
            {
                await f.Engine.GetModelPlanAsync();
                await f.Engine.JevSettledForTest();
                if (relay.Calls.Count == seen) break;
                seen = relay.Calls.Count;
            }
        }

        // ---- the packing -----------------------------------------------------------------------------------------------

        private static TableBatch<string> Build(IReadOnlyList<string> chunk, int bytesEach, string point = "column.summarize")
        {
            // an ask is "Table/column"; every ask carries a text of bytesEach bytes in the one list the snapshot sends
            var b = new TableBatch<string> { TableRef = "table:" + chunk[0].Split('/')[0], Asks = chunk.ToList() };
            var pt = DecisionRegistry.Require(point);
            var questions = new List<DecisionQuestion>();
            for (var i = 0; i < chunk.Count; i++)
            {
                questions.Add(new DecisionQuestion("summarize_by:" + chunk[i], DecisionQuestionKind.Choice, "For the column `columns[" + i + "]`: how?",
                    new[] { new DecisionOption("sum", "totals mean something"), new DecisionOption("none", "totals mean nothing") }));
                b.Names[chunk[i]] = new List<KeyValuePair<string, string>> { new KeyValuePair<string, string>("summarize_by:" + chunk[i], "summarize_by") };
            }
            b.Questions = questions;
            b.Snapshot = DecisionSnapshot.Build(pt, new[]
            {
                new KeyValuePair<string, object>("columns", chunk.Select(a => (object)new Dictionary<string, object> { { "name", a }, { "text", new string('x', bytesEach) } }).ToList()),
            });
            return b;
        }

        [Fact]
        public void Objects_of_many_tables_go_in_one_request_until_the_context_is_full_and_never_per_table()
        {
            var asks = Enumerable.Range(0, 120).Select(i => "T" + (i / 4) + "/c" + i).ToList();   // 30 tables of 4

            var batches = JevTableBatch.Pack(asks, a => a.Split('/')[0], c => Build(c, 100), DecisionRegistry.Require(DecisionRegistry.ColumnSummarize));

            Assert.Single(batches);   // 120 small asks are far under Jev's context
            Assert.Equal(asks, batches[0].Asks);   // order kept: a table's objects stay together, the first appearance fixes a table's place
        }

        [Fact]
        public void A_request_is_cut_only_where_the_context_budget_says_and_every_ask_lands_in_exactly_one_request()
        {
            var asks = Enumerable.Range(0, 400).Select(i => "T" + (i / 10) + "/c" + i).ToList();

            var batches = JevTableBatch.Pack(asks, a => a.Split('/')[0], c => Build(c, 1500), DecisionRegistry.Require(DecisionRegistry.ColumnSummarize));

            Assert.True(batches.Count > 1 && batches.Count < 40, "400 asks of about 1.5 KB do not fit one request and do not need one each table: " + batches.Count);
            Assert.Equal(asks, batches.SelectMany(b => b.Asks).ToList());
            var point = DecisionRegistry.Require(DecisionRegistry.ColumnSummarize);
            foreach (var b in batches)
            {
                var json = DecisionDigest.CanonicalSnapshotJson(b.Snapshot);
                var body = JevDecisionOracle.BuildBody(b.RequestFor(point), json);
                Assert.True(System.Text.Encoding.UTF8.GetByteCount(body) <= JevTableBatch.MaxBodyBytes, "a request passed the context budget");
                Assert.True(System.Text.Encoding.UTF8.GetByteCount(json) <= JevTableBatch.MaxSnapshotBytes, "a snapshot passed the limit");
            }
            // each request is as full as it can be: adding the next ask would pass the budget (so no request is cut early)
            for (var i = 0; i + 1 < batches.Count; i++)
            {
                var next = Build(batches[i].Asks.Concat(new[] { batches[i + 1].Asks[0] }).ToList(), 1500);
                var json = DecisionDigest.CanonicalSnapshotJson(next.Snapshot);
                var body = JevDecisionOracle.BuildBody(next.RequestFor(point), json);
                Assert.True(System.Text.Encoding.UTF8.GetByteCount(body) > JevTableBatch.MaxBodyBytes
                            || System.Text.Encoding.UTF8.GetByteCount(json) > JevTableBatch.MaxSnapshotBytes
                            || next.Questions.Count > JevTableBatch.MaxQuestions, "request " + i + " was cut before it was full");
            }
        }

        [Fact]
        public void One_ask_over_the_limit_is_sent_alone_and_left_to_the_client_to_refuse_never_cut_short()
        {
            var asks = new[] { "A/1", "A/2", "B/1" };

            var batches = JevTableBatch.Pack(asks, a => a.Split('/')[0], c => Build(c, c.Contains("A/2") ? 70 * 1024 : 100), DecisionRegistry.Require(DecisionRegistry.ColumnSummarize));

            Assert.Equal(new[] { "A/1", "A/2", "B/1" }, batches.SelectMany(b => b.Asks));
            Assert.Contains(batches, b => b.Asks.Count == 1 && b.Asks[0] == "A/2");
        }

        [Fact]
        public void The_old_per_table_shape_is_what_a_request_narrows_to_when_the_relay_is_older()
        {
            var asks = Enumerable.Range(0, 90).Select(i => "T" + (i / 30) + "/c" + i).ToList();
            var batches = JevTableBatch.Pack(asks, a => a.Split('/')[0], c => Build(c, 100), DecisionRegistry.Require(DecisionRegistry.ColumnSummarize));
            Assert.Single(batches);

            var narrower = batches[0].Narrow();

            Assert.Equal(3, narrower.Count);   // one request a table, each at most JevTableBatch.LegacyChunk asks
            Assert.All(narrower, b => Assert.True(b.Asks.Count <= JevTableBatch.LegacyChunk));
            Assert.Equal(asks, narrower.SelectMany(b => b.Asks).ToList());
        }


        // ---- the engine: how many calls ------------------------------------------------------------------------------

        [Fact]
        public async Task A_Plan_read_of_a_many_table_model_asks_each_point_in_requests_of_its_measured_size()
        {
            // m16/jev-size (live sweep): each point has its own bound on objects a request, and requests are as full as that bound allows
            // (several tables share one), not one a table and not one for the whole model.
            var (f, relay) = await OpenAsync(12);
            using (f)
            {
                await ReadUntilQuietAsync(f, relay);

                var calls = relay.Calls;
                Assert.NotEmpty(calls);
                var bounds = new Dictionary<string, (int Objects, int QuestionsEach)>
                {
                    ["ai.schema"] = (JevTableBatch.AiSchemaObjectsPerRequest, 1),
                    ["measure.folder_and_format"] = (JevTableBatch.MeasureObjectsPerRequest, 2),
                    ["column.summarize"] = (JevTableBatch.ColumnObjectsPerRequest, 1),
                    ["column.format"] = (JevTableBatch.ColumnObjectsPerRequest, 1),
                };
                Assert.Equal(15, JevTableBatch.AiSchemaObjectsPerRequest);
                Assert.Equal(30, JevTableBatch.MeasureObjectsPerRequest);
                Assert.Equal(60, JevTableBatch.ColumnObjectsPerRequest);
                foreach (var g in calls.Where(c => bounds.ContainsKey(c.Point)).GroupBy(c => c.Point))
                {
                    var (objects, each) = bounds[g.Key];
                    Assert.All(g, c => Assert.True(c.Questions <= objects * each, g.Key + " sent " + c.Questions + " questions, over its bound of " + objects + " objects"));
                    if (each == 1)
                    {
                        var fewest = (int)Math.Ceiling(g.Sum(c => (double)c.Questions) / objects);
                        Assert.True(g.Count() <= fewest + 1, g.Key + " was asked " + g.Count() + " times where " + fewest + " requests of " + objects + " hold it ("
                            + string.Join(",", g.Select(c => c.Questions)) + " questions)");
                    }
                }
                Assert.Contains(calls, c => c.Point == "ai.schema" && c.Questions == JevTableBatch.AiSchemaObjectsPerRequest);   // a full request, across tables
                Assert.Equal(1, calls.Count(c => c.Point == "column.summarize"));
                Assert.All(calls, c => Assert.True(c.BodyBytes <= JevTableBatch.MaxBodyBytes));
                // The whole model is looked at in the first run, not a part of it per run.
                Assert.True(calls.Where(c => c.Point == "ai.schema").Sum(c => c.Questions) > 80, "ai.schema was still held to a small cap per run");
            }
        }

        [Fact]
        public async Task The_shared_facts_of_a_request_go_once_not_once_for_each_table()
        {
            var (f, relay) = await OpenAsync(12);
            using (f)
            {
                var seen = new List<DecisionRequest>();
                f.Engine.UseDecisionsForTest(new DecisionService(new SnapshotTap(relay, seen), new DecisionBudget(), answers: new DecisionAnswerStore()));
                await ReadUntilQuietAsync(f, relay);

                var summarize = seen.Single(r => r.PointName == "column.summarize");
                // model_tables is the model's whole table list, shared by every table: one copy in the one request.
                Assert.True(summarize.Snapshot.ContainsKey("model_tables"));
                var json = DecisionDigest.CanonicalSnapshotJson(summarize.Snapshot);
                Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(json, "\"model_tables\"").Count);
                // Every entry says which table it is in, and each table's own facts are keyed by the table name.
                using var doc = JsonDocument.Parse(json);
                var columns = doc.RootElement.GetProperty("columns").EnumerateArray().ToList();
                var byTable = doc.RootElement.GetProperty("table_columns");
                Assert.All(columns, c => Assert.True(byTable.TryGetProperty(c.GetProperty("table").GetString(), out _), "an entry names a table that has no shared facts"));
                Assert.True(columns.Select(c => c.GetProperty("table").GetString()).Distinct().Count() > 3, "the one request carries more than one table");
                // Every question names its entry by path AND by the entry's own name and table: Jev finds an entry by name and does not count
                // into a list (m16/jev-size: by path alone, sure answers fell away after about the tenth entry).
                foreach (var r in seen.Where(r => r.PointName is "ai.schema" or "column.summarize" or "column.format" or "measure.folder_and_format"))
                {
                    using var d = JsonDocument.Parse(DecisionDigest.CanonicalSnapshotJson(r.Snapshot));
                    var (list, noun) = r.PointName switch { "ai.schema" => ("objects", "object"), "measure.folder_and_format" => ("measures", "measure"), _ => ("columns", "column") };
                    var entries = d.RootElement.GetProperty(list);
                    foreach (var q in r.Questions)
                    {
                        var i = int.Parse(System.Text.RegularExpressions.Regex.Match(q.Instructions, "^For the " + noun + " `" + list + "\\[(\\d+)\\]`").Groups[1].Value);
                        var e = entries[i];
                        Assert.StartsWith("For the " + noun + " `" + list + "[" + i + "]`, named \"" + e.GetProperty("name").GetString() + "\" in the table \""
                                          + e.GetProperty("table").GetString() + "\": ", q.Instructions, StringComparison.Ordinal);
                    }
                }
            }
        }

        [Fact]
        public async Task A_reply_that_leaves_out_one_question_loses_only_that_object_and_the_rest_are_used()
        {
            // "none" for every column asked: the lines that stop a column adding up. The reply leaves out the answer about one column.
            const string dropped = "column:Activity Event/Duration Ms";
            var relay = new CountingRelay
            {
                Choose = q => q.Options.Any(o => o.Id == "none") ? "none" : q.Options[0].Id,
                Omit = q => q.Name.EndsWith(":" + dropped, StringComparison.Ordinal),
            };
            var (f, _) = await OpenAsync(8, relay);
            using (f)
            {
                await ReadUntilQuietAsync(f, relay);
                var plan = await f.Engine.GetModelPlanAsync();

                var rows = plan.Items.Where(i => i.Id.StartsWith("jev.column-summarize.", StringComparison.Ordinal)).ToList();
                var refs = rows.SelectMany(r => r.ChangeSet?.Lines ?? Array.Empty<ModelPlanChangeLine>()).Select(l => l.Target).ToList();
                Assert.DoesNotContain("table:" + dropped, refs);
                Assert.DoesNotContain(dropped, refs);
                Assert.Contains(refs, r => r.StartsWith("column:Activity Event/", StringComparison.Ordinal) && r != dropped);
                Assert.Contains(refs, r => r.StartsWith("column:Capacity Operation/", StringComparison.Ordinal));   // another table, same request
                Assert.Equal(1, relay.Calls.Count(c => c.Point == "column.summarize"));
            }
        }

        [Fact]
        public async Task A_relay_older_than_the_client_refuses_the_big_request_and_the_Plan_asks_a_table_at_a_time_instead()
        {
            var relay = new CountingRelay { RefuseOver = JevTableBatch.LegacyChunk };
            var (f, _) = await OpenAsync(10, relay);
            using (f)
            {
                // Requests bigger than the old relay takes (m16/jev-size bounds most points under it, so the seam asks as a whole model would).
                f.Engine.JevRequestObjectsForTest = JevTableBatch.MaxQuestions;
                await ReadUntilQuietAsync(f, relay);
                var plan = await f.Engine.GetModelPlanAsync();

                Assert.True(relay.Refused > 0, "the relay never refused a request: the fallback was not exercised");
                Assert.True(f.Engine.JevRelayNeedsTablesForTest, "the engine did not learn to ask a table at a time");
                // The model was still looked at: every point got answers through the smaller requests, so the rows have Jev's lines.
                Assert.All(relay.Calls, c => Assert.True(c.Questions <= JevTableBatch.LegacyChunk, c.Point + " sent " + c.Questions + " questions to a relay that takes " + JevTableBatch.LegacyChunk));
                Assert.Contains(plan.Items, i => i.Id.StartsWith("jev.", StringComparison.Ordinal) && (i.ChangeSet?.Lines?.Length ?? 0) > 0);
                // After the first refusal nothing more is refused: the engine cut its requests that way from then on.
                var refusedBefore = relay.Refused;
                await f.Engine.CreateMeasureAsync("table:Activity Event", "Added After Refusal", "COUNTROWS ( 'Activity Event' )", "human");
                await f.Engine.GetModelPlanAsync();
                await f.Engine.JevSettledForTest();
                Assert.Equal(refusedBefore, relay.Refused);
            }
        }

        [Theory]
        [InlineData("cleanup.suggest", "cleanup:", 40, 100)]
        [InlineData("query.summarize", "q:", 24, 60)]
        [InlineData("compose.draft", "step:", 64, 100)]
        [InlineData("cleanup.suggest", "cleanup:", 7, 100)]   // a relay that refuses even the old bound: halved again, nothing dropped
        public async Task A_single_request_point_the_relay_refuses_as_too_big_is_asked_again_in_smaller_ones_and_every_answer_comes_back(string pointName, string prefix, int relayTakes, int questions)
        {
            var relay = new CountingRelay { RefuseOver = relayTakes };
            var (f, _) = await OpenAsync(2, relay);
            using (f)
            {
                var point = DecisionRegistry.Require(pointName);
                var snapshot = DecisionSnapshot.Build(point, point.SnapshotFields.Select(n => new KeyValuePair<string, object>(n, (object)new List<object>())));
                var option = new[] { new DecisionOption("suggest", "the column is a leftover"), new DecisionOption("leave_out", "anything else") };
                var qs = Enumerable.Range(0, questions).Select(i => new DecisionQuestion(prefix + i, DecisionQuestionKind.Choice, "Is item " + i + " a leftover?", option)).ToList();

                var outcome = await f.Engine.Splitter.AskAsync(f.Engine.Decisions, pointName, snapshot, qs, true, true, null, System.Threading.CancellationToken.None);

                Assert.True(outcome.Asked && outcome.Answers.Decided, "the merged reply was not decided: " + outcome.Answers?.NotDecidedReason + outcome.SkippedReason);
                Assert.All(qs, q => Assert.NotNull(outcome.Answers.For(q.Name)));
                Assert.All(relay.Calls, c => Assert.True(c.Questions <= relayTakes, c.Questions + " questions to a relay that takes " + relayTakes));
                var refused = relay.Refused;
                Assert.True(refused > 0);
                // The next ask starts at the bound the relay taught: it is not refused again.
                var again = await f.Engine.Splitter.AskAsync(f.Engine.Decisions, pointName, snapshot, qs.Select(q => new DecisionQuestion(q.Name, q.Kind, q.Instructions + " ", q.Options)).ToList(), true, true, null, System.Threading.CancellationToken.None);
                Assert.True(again.Asked && again.Answers.Decided);
                if (relayTakes >= JevTableBatch.LegacyQuestionsFor(pointName)) Assert.Equal(refused, relay.Refused);
            }
        }

        // ---- one wave, not one wave a table -----------------------------------------------------------------------------

        [Fact]
        public async Task The_summarize_rows_of_different_tables_are_ready_together_and_one_press_runs_them_in_one_wave()
        {
            // Every column asked "stops adding up" (none): each table's row writes its own table's columns and its own new measures, and
            // nothing in one table's row can change what another table's row should say, so no row waits for another.
            var relay = new CountingRelay { Choose = q => q.Options.Any(o => o.Id == "none") ? "none" : q.Options[0].Id };
            var (f, _) = await OpenAsync(12, relay);
            using (f)
            {
                await ReadUntilQuietAsync(f, relay);
                var plan = await f.Engine.GetModelPlanAsync();

                var rows = plan.Items.Where(i => i.Id.StartsWith("jev.column-summarize.", StringComparison.Ordinal) && (i.ChangeSet?.Lines?.Length ?? 0) > 0).ToList();
                Assert.True(rows.Count >= 3, "the model gave fewer than three summarize rows: " + string.Join(", ", rows.Select(r => r.Id)));
                var ready = rows.Where(r => r.StateWord == PlanStateWords.Ready).ToList();
                Assert.True(ready.Count == rows.Count, "only " + ready.Count + " of " + rows.Count + " summarize rows are Ready; the rest wait for another row: "
                    + string.Join("; ", rows.Where(r => r.StateWord != PlanStateWords.Ready).Select(r => r.Id + " " + r.StateWord + " " + r.StateNote)));
            }
        }

        // ---- progress stays honest ---------------------------------------------------------------------------------------

        [Fact]
        public void The_tally_says_what_Jev_is_choosing_with_real_counts_and_goes_quiet_when_nothing_is_out()
        {
            var tally = new JevChoosingTally();
            Assert.Null(tally.Words());

            tally.Add(DecisionRegistry.AiSchema, 396);
            Assert.Equal("Jev is choosing what Copilot sees in 396 fields", tally.Words());

            // several points out at once: one short line with the one real count (the objects, all points together)
            tally.Add(DecisionRegistry.ColumnSummarize, 48);
            Assert.Equal("Jev is choosing 444 settings", tally.Words());
            tally.Add(DecisionRegistry.MeasureFolderAndFormat, 1);
            tally.Add(DecisionRegistry.ColumnFormat, 24);
            Assert.Equal("Jev is choosing 469 settings", tally.Words());

            // each request that comes back takes its own objects off, so the count falls and is never a number that stopped being true
            tally.Add(DecisionRegistry.AiSchema, -148);
            Assert.Equal("Jev is choosing 321 settings", tally.Words());
            tally.Add(DecisionRegistry.AiSchema, -248);
            tally.Add(DecisionRegistry.ColumnFormat, -24);
            tally.Add(DecisionRegistry.MeasureFolderAndFormat, -1);
            Assert.Equal("Jev is choosing how 48 columns add up", tally.Words());
            tally.Add(DecisionRegistry.ColumnSummarize, -47);
            Assert.Equal("Jev is choosing how 1 column adds up", tally.Words());
            tally.Add(DecisionRegistry.ColumnSummarize, -1);
            Assert.Null(tally.Words());
            Assert.Equal("Jev is choosing folders and formats for 1 measure", Phrase(DecisionRegistry.MeasureFolderAndFormat));
            Assert.All(new[] { "how 1 column adds up", "what Copilot sees in 1 field" }, w => Assert.DoesNotContain("\u2014", w));

            static string Phrase(string point) { var t = new JevChoosingTally(); t.Add(point, 1); return t.Words(); }
        }

        [Fact]
        public async Task A_press_that_waits_for_Jev_says_what_it_waits_for_with_the_real_count()
        {
            var relay = new CountingRelay { BaseLatencyMs = 400, PerQuestionMs = 1 };
            var (f, _) = await OpenAsync(10, relay);
            using (f)
            {
                var events = new List<PlanRunProgress>();
                void Tap(PlanRunProgress v) { lock (events) events.Add(v); }
                f.Sessions.Bus.PlanRunProgressed += Tap;
                await ReadUntilQuietAsync(f, relay);
                var plan = await f.Engine.GetModelPlanAsync();
                foreach (var asst in plan.Items.Where(i => i.Doer != PlanDoers.Semanticus && i.StateWord != PlanStateWords.Done && i.Status != ModelPlanStatuses.Skipped))
                    await f.Engine.SkipModelPlanItemAsync(asst.Id, "No assistant here.", "human");
                plan = await f.Engine.GetModelPlanAsync();
                var ran = await f.Engine.ApplyPlanItemsAsync(null, "human", plan.ReadyDigest);
                f.Sessions.Bus.PlanRunProgressed -= Tap;

                Assert.True(ran.Performed, ran.Sentence);
                List<PlanRunProgress> waits;
                lock (events) waits = events.Where(e => e.Phase == PlanRunPhases.WaitJev).ToList();
                Assert.NotEmpty(waits);
                Assert.Contains(waits, e => e.Choosing != null && e.Choosing.StartsWith("Jev is choosing ", StringComparison.Ordinal)
                                            && System.Text.RegularExpressions.Regex.IsMatch(e.Choosing, "[0-9]+ (columns?|fields?|measures?|settings?)"));
            }
        }

        private sealed class SnapshotTap : IDecisionOracle
        {
            private readonly IDecisionOracle _inner;
            private readonly List<DecisionRequest> _seen;
            public SnapshotTap(IDecisionOracle inner, List<DecisionRequest> seen) { _inner = inner; _seen = seen; }
            public bool Available => true;
            public Task<DecisionAnswers> AskAsync(DecisionRequest request, System.Threading.CancellationToken ct)
            {
                lock (_seen) _seen.Add(request);
                return _inner.AskAsync(request, ct);
            }
        }
    }
}
