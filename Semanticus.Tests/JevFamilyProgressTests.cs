// The measures and column format families look at EVERY eligible object, a run at a time (m11/sf-progress, design-spec-first-start-v2
// section 1, finding S11). Before this, each family cut its list at 60 inside Prepare, so on a model with 75 measures the last 15 were
// never asked about, and a first batch whose answers could not be used stayed first in line on every run and starved the rest.
//
// WHAT IS PROVED HERE, AND HOW. Counted off an oracle that records the object behind every question it is sent (a batched question
// carries its object's ref in its name), never argued from the code.
//   75 measures that need decisions: all 75 are asked over the runs, each once.
//   70 unformatted columns: the same.
//   A first batch whose answers are all unusable does not block the objects behind it, and is not asked a second time.
//   A service that answers nothing is not asked batch after batch for ever (the guard), and the rows say so.
//   The Plan's progress words say what is true: how far Jev got, and how many answers it could not use.
//   Free sends nothing and starts no run.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Decisions;
using Semanticus.Engine;
using Semanticus.Engine.Decisions;
using Xunit;

namespace Semanticus.Tests
{
    public sealed class JevFamilyProgressTests : IClassFixture<ModelTemplates>
    {
        // Lean suite (8 Oct): the wide models (75 to 150 objects, a call each) are built once per class and each test opens its own copy.
        private readonly ModelTemplates _templates;
        public JevFamilyProgressTests(ModelTemplates templates) => _templates = templates;

        private const string MeasurePoint = "measure.folder_and_format";
        private const string ColumnPoint = "column.format";

        /// <summary>How the oracle treats one object.</summary>
        private enum Reply { Usable, Unusable, Silent }

        /// <summary>Answers a batched request question by question, and remembers the object behind each question. A usable answer
        /// picks a real option; an unusable one names an option that was never offered (the reader refuses it); a silent one makes the
        /// whole request come back with the service's own words (nothing was heard).</summary>
        private sealed class ScriptedOracle : IDecisionOracle
        {
            private readonly Func<string, Reply> _reply;
            private readonly List<string> _asked = new List<string>();
            private readonly object _gate = new object();
            private TaskCompletionSource<bool> _hold;
            private Func<string, bool> _holdFor;
            public ScriptedOracle(Func<string, Reply> reply) { _reply = reply; }
            public bool Available => true;

            /// <summary>Every object a question was sent for, in the order they were sent.</summary>
            public IReadOnlyList<string> Asked { get { lock (_gate) return _asked.ToList(); } }

            /// <summary>Hold every request that asks about a matching object until <see cref="Release"/>.</summary>
            public void HoldFor(Func<string, bool> match)
            {
                _holdFor = match;
                _hold = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            public void Release() => _hold?.TrySetResult(true);

            public static string ObjectOf(DecisionQuestion q)
            {
                var at = q.Name.IndexOf(':');
                return at < 0 ? q.Name : q.Name.Substring(at + 1);
            }

            public async Task<DecisionAnswers> AskAsync(DecisionRequest request, CancellationToken ct)
            {
                if (request.PointName != MeasurePoint && request.PointName != ColumnPoint)
                    return DecisionAnswers.NotDecided(request, "not a point this oracle answers");
                var refs = request.Questions.Select(ObjectOf).Distinct(StringComparer.Ordinal).ToList();
                lock (_gate) _asked.AddRange(refs);
                if (_holdFor != null && refs.Any(_holdFor))
                    await _hold.Task.WaitAsync(ct);

                if (refs.Any(r => _reply(r) == Reply.Silent))
                    return DecisionAnswers.NotDecided(request, "the advice service did not answer");

                var body = new StringBuilder("{");
                var first = true;
                foreach (var q in request.Questions)
                {
                    var plain = q.Name.Substring(0, Math.Max(0, q.Name.IndexOf(':')));
                    string choice;
                    if (_reply(ObjectOf(q)) == Reply.Unusable) choice = "not_an_option_that_was_sent";
                    else if (plain == "format_family") choice = "whole_number";
                    else choice = q.Options.Any(o => o.Id == DecisionRegistry.NoFolder) ? DecisionRegistry.NoFolder : q.Options[0].Id;
                    if (!first) body.Append(',');
                    first = false;
                    body.Append(JsonSerializer.Serialize(q.Name)).Append(":{\"type\":\"choice\",\"choice\":")
                        .Append(JsonSerializer.Serialize(choice)).Append(",\"confidence\":0.95}");
                }
                body.Append('}');
                return DecisionAnswerReader.Read(request, JsonDocument.Parse(body.ToString()).RootElement, "jev-test-1", 100, 10);
            }
        }

        private static ScriptedOracle Use(DecisionFixture f, Func<string, Reply> reply)
        {
            var oracle = new ScriptedOracle(reply);
            f.Engine.UseDecisionsForTest(new DecisionService(oracle, new DecisionBudget()));
            return oracle;
        }

        // ---- models ------------------------------------------------------------------------------------------

        /// <summary>T01..T05, each with perTable measures (named "T01 M01": a measure name is unique across the model) that have no folder and no format. Refs sort the way the family asks.</summary>
        private async Task<DecisionFixture> MeasuresModelAsync(string file, int tables = 5, int perTable = 15, bool pro = true)
        {
            var f = new DecisionFixture(pro ? TestEntitlements.Pro : TestEntitlements.Free);
            await _templates.OpenCopyAsync(f, "measures-" + tables + "x" + perTable, b => BuildMeasuresAsync(b, tables, perTable), file);
            f.UseNull();
            return f;
        }

        private static Task BuildMeasuresAsync(DecisionFixture f, int tables, int perTable) =>
            PlanRowsBim.LoadAsync(f, "Wide", "Build.bim", Enumerable.Range(1, tables).Select(t =>
            {
                var table = "T" + t.ToString("00");
                // Label is text: the column format family leaves it alone.
                return (object)PlanRowsBim.Table(table, new[] { "Label:String" },
                    Enumerable.Range(1, perTable).Select(m => PlanRowsBim.M(table + " M" + m.ToString("00"), "COUNTROWS ( " + table + " )")).ToArray());
            }).ToArray());

        /// <summary>C01..C05, each with perTable visible whole-number columns that have no format and are neither keys nor link ends.</summary>
        private async Task<DecisionFixture> ColumnsModelAsync(string file, int tables = 5, int perTable = 14, bool pro = true)
        {
            var f = new DecisionFixture(pro ? TestEntitlements.Pro : TestEntitlements.Free);
            await _templates.OpenCopyAsync(f, "columns-" + tables + "x" + perTable, b => BuildColumnsAsync(b, tables, perTable), file);
            f.UseNull();
            return f;
        }

        private static Task BuildColumnsAsync(DecisionFixture f, int tables, int perTable) =>
            PlanRowsBim.LoadAsync(f, "WideColumns", "Build.bim", Enumerable.Range(1, tables).Select(t =>
                (object)PlanRowsBim.Table("C" + t.ToString("00"),
                    Enumerable.Range(1, perTable).Select(c => "Units " + c.ToString("00") + ":Int64").ToArray())).ToArray());

        private static IEnumerable<string> MeasureRefs(int tables, int perTable) =>
            from t in Enumerable.Range(1, tables) from m in Enumerable.Range(1, perTable)
            select "measure:T" + t.ToString("00") + "/T" + t.ToString("00") + " M" + m.ToString("00");

        private static IEnumerable<string> ColumnRefs(int tables, int perTable) =>
            from t in Enumerable.Range(1, tables) from c in Enumerable.Range(1, perTable)
            select "column:C" + t.ToString("00") + "/Units " + c.ToString("00");

        /// <summary>Read the plan the way the page does: each read lets the run that is out land, and a read after a partial run
        /// starts the next one. Stops when nothing is pending.</summary>
        private static async Task<ModelPlanView> UntilSettledAsync(DecisionFixture f, int maxReads = 30)
        {
            ModelPlanView plan = null;
            for (var i = 0; i < maxReads; i++)
            {
                plan = await f.Engine.GetModelPlanAsync();
                await f.Engine.JevSettledForTest();
                plan = await f.Engine.GetModelPlanAsync();
                if (!plan.JevPending) return plan;
            }
            return plan;
        }

        private static IEnumerable<string> LineTargets(ModelPlanView plan, string op) =>
            plan.Items.Where(i => i.ChangeSet != null).SelectMany(i => i.ChangeSet.Lines).Where(l => l.Op == op).Select(l => l.Target);

        private static string[] WhyOf(ModelPlanView plan, string family) =>
            plan.Items.Where(i => i.Id.StartsWith("jev." + family + ".", StringComparison.Ordinal))
                .SelectMany(i => i.Why ?? Array.Empty<string>()).ToArray();

        // ---- the three that failed first ---------------------------------------------------------------------

        [Fact]
        public async Task All_75_measures_that_need_decisions_are_asked_over_the_runs_and_none_twice()
        {
            using var f = await MeasuresModelAsync("Measures75.bim");
            await f.ConsentAsync();
            var oracle = Use(f, _ => Reply.Usable);

            var plan = await UntilSettledAsync(f);

            Assert.False(plan.JevPending);
            var asked = oracle.Asked;
            Assert.Equal(MeasureRefs(5, 15).OrderBy(r => r, StringComparer.Ordinal), asked.Distinct().OrderBy(r => r, StringComparer.Ordinal));
            Assert.Equal(asked.Count, asked.Distinct().Count());   // none asked twice
            // Every one of them has its format line on the plan, the last fifteen included.
            Assert.Equal(75, LineTargets(plan, "set_measure_format").Distinct().Count());
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task All_70_unformatted_columns_are_asked_over_the_runs_and_none_twice()
        {
            using var f = await ColumnsModelAsync("Columns70.bim");
            await f.ConsentAsync();
            var oracle = Use(f, _ => Reply.Usable);

            var plan = await UntilSettledAsync(f);

            Assert.False(plan.JevPending);
            var asked = oracle.Asked;
            Assert.Equal(ColumnRefs(5, 14).OrderBy(r => r, StringComparer.Ordinal), asked.Distinct().OrderBy(r => r, StringComparer.Ordinal));
            Assert.Equal(asked.Count, asked.Distinct().Count());
            Assert.Equal(70, LineTargets(plan, "set_format_string").Distinct().Count());
        }

        [Fact]
        public async Task A_first_batch_of_unusable_measure_answers_does_not_block_the_measures_behind_it()
        {
            using var f = await MeasuresModelAsync("MeasuresUnusable.bim");
            await f.ConsentAsync();
            var first60 = new HashSet<string>(MeasureRefs(5, 15).Take(60), StringComparer.Ordinal);
            var oracle = Use(f, r => first60.Contains(r) ? Reply.Unusable : Reply.Usable);

            var plan = await UntilSettledAsync(f);

            Assert.False(plan.JevPending);
            var asked = oracle.Asked;
            Assert.Equal(75, asked.Distinct().Count());
            Assert.Equal(asked.Count, asked.Distinct().Count());   // the unusable ones are not asked a second time in this chain
            // The fifteen behind the first batch were decided, and the sixty it could not use have no line.
            var formatted = LineTargets(plan, "set_measure_format").ToHashSet(StringComparer.Ordinal);
            Assert.Equal(MeasureRefs(5, 15).Skip(60).OrderBy(r => r, StringComparer.Ordinal), formatted.OrderBy(r => r, StringComparer.Ordinal));
        }

        [Fact]
        public async Task A_first_batch_of_unusable_column_answers_does_not_block_the_columns_behind_it()
        {
            using var f = await ColumnsModelAsync("ColumnsUnusable.bim");
            await f.ConsentAsync();
            var first60 = new HashSet<string>(ColumnRefs(5, 14).Take(60), StringComparer.Ordinal);
            var oracle = Use(f, r => first60.Contains(r) ? Reply.Unusable : Reply.Usable);

            var plan = await UntilSettledAsync(f);

            Assert.False(plan.JevPending);
            var asked = oracle.Asked;
            Assert.Equal(70, asked.Distinct().Count());
            Assert.Equal(asked.Count, asked.Distinct().Count());
            var formatted = LineTargets(plan, "set_format_string").ToHashSet(StringComparer.Ordinal);
            Assert.Equal(ColumnRefs(5, 14).Skip(60).OrderBy(r => r, StringComparer.Ordinal), formatted.OrderBy(r => r, StringComparer.Ordinal));
        }

        // ---- the guard ---------------------------------------------------------------------------------------

        [Fact]
        public async Task A_service_that_answers_nothing_is_not_asked_batch_after_batch()
        {
            using var f = await MeasuresModelAsync("MeasuresSilent.bim", tables: 5, perTable: 30);   // 150 measures
            await f.ConsentAsync();
            var oracle = Use(f, _ => Reply.Silent);

            var plan = await UntilSettledAsync(f);

            Assert.False(plan.JevPending);
            // One run's worth went out, and no more: nothing came back, so the next batch was not started.
            Assert.Equal(60, oracle.Asked.Distinct().Count());
            var why = WhyOf(plan, "measures");
            Assert.DoesNotContain(why, w => w.Contains("The rest follow"));
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_service_that_keeps_giving_unusable_answers_is_asked_for_two_runs_and_then_left()
        {
            using var f = await MeasuresModelAsync("MeasuresAlwaysUnusable.bim", tables: 5, perTable: 30);   // 150 measures
            await f.ConsentAsync();
            var oracle = Use(f, _ => Reply.Unusable);

            var plan = await UntilSettledAsync(f);

            Assert.False(plan.JevPending);
            var asked = oracle.Asked;
            Assert.Equal(120, asked.Count);
            Assert.Equal(120, asked.Distinct().Count());
        }

        // ---- the words ---------------------------------------------------------------------------------------

        [Fact]
        public async Task The_progress_words_say_how_far_Jev_got_while_it_is_part_way_and_what_it_could_not_use_when_done()
        {
            using var f = await MeasuresModelAsync("MeasuresWords.bim");
            await f.ConsentAsync();
            var tenUnusable = new HashSet<string>(MeasureRefs(5, 15).Take(10), StringComparer.Ordinal);
            var last15 = new HashSet<string>(MeasureRefs(5, 15).Skip(60), StringComparer.Ordinal);
            var oracle = Use(f, r => tenUnusable.Contains(r) ? Reply.Unusable : Reply.Usable);
            oracle.HoldFor(r => last15.Contains(r));

            // The first run lands. The next read starts the second run, which is held on its fifteen, so the plan after it still
            // shows what the first run found.
            await f.Engine.GetModelPlanAsync();
            await f.Engine.JevSettledForTest();
            Assert.Equal(60, oracle.Asked.Count);
            await f.Engine.GetModelPlanAsync();
            await WaitUntil(() => oracle.Asked.Count >= 75, "the second run never asked");
            var midway = await f.Engine.GetModelPlanAsync();
            Assert.True(midway.JevPending);
            var why = WhyOf(midway, "measures");
            Assert.Contains(why, w => w.Contains("looked at 60 of 75 measures") && w.Contains("The rest follow"));
            Assert.Contains(why, w => w.Contains("10 of them"));

            oracle.Release();
            var done = await UntilSettledAsync(f);

            var after = WhyOf(done, "measures");
            Assert.DoesNotContain(after, w => w.Contains("The rest follow"));
            Assert.Contains(after, w => w.Contains("no usable answer for 10 of 75 measures"));
            Assert.DoesNotContain("—", string.Concat(after), StringComparison.Ordinal);
        }

        // ---- free ---------------------------------------------------------------------------------------------

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Free_sends_no_ask_and_starts_no_run_however_many_objects_there_are()
        {
            using var f = await MeasuresModelAsync("MeasuresFree.bim", pro: false);
            await f.ConsentAsync();
            var oracle = Use(f, _ => Reply.Usable);

            for (var i = 0; i < 3; i++)
            {
                await f.Engine.GetModelPlanAsync();
                await f.Engine.JevSettledForTest();
            }

            Assert.Empty(oracle.Asked);
            Assert.Equal(0, f.Engine.JevRunsStartedForTest);
            Assert.Equal(0, f.Engine.Decisions.RequestsStarted);
        }

        private static async Task WaitUntil(Func<bool> condition, string what)
        {
            var until = DateTime.UtcNow.AddSeconds(30);
            while (!condition())
            {
                Assert.True(DateTime.UtcNow < until, what);
                await Task.Delay(20);
            }
        }
    }
}
