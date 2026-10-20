// The AI data schema family (T268, M4 point 5: what Copilot sees). For every visible table, column and measure,
// whether Copilot and data agents should see it. Jev picks include or exclude; the engine checks each pick against the
// schema's own rule (nothing a measure Copilot still sees uses is left out) and writes it with set_ai_data_schema.
//
// WHAT IS PROVED HERE, AND HOW.
//   Below the replay bar (AiSchemaFamily.JevDecides forced false): nothing is asked and there is no item, for Pro as
//   for free. The shipped value is true: run 2 met Kane's switch-on bar of 26 September.
//   Free: nothing asked, no item, no run started, even with the bar forced met.
//   Pro, bar met: one item per table, "Choose what Copilot sees: N things in T", owned by Jev, in Trust, every line a
//   decision on ai.schema with an engine-built reason and check. Unsure arrives dropped.
//   The checks: a column a kept measure uses, a measure another kept measure uses and a table a measure elsewhere
//   reads from are refused with a plain reason, never lines. A measure is not put back while what it uses stays out.
//   Approve writes the kept lines as one undo step, and the readiness rule that guards the schema stays quiet.
//   What goes to Jev: the five declared fields, names, types and structure facts only; never a formula.
//
// THE FAMILY ASKS ONE TABLE AT A TIME (m11/jev-batch): a request holds a table's objects and one question for each, so a
// recorded answer is keyed by the object's ref, not by a request digest (ByObjectRecordedOracle). The refs are read from the
// family's own Prepare, the call the run makes. Requests per run are the table chunks (here one for each of 3 tables).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Analysis;
using Semanticus.Decisions;
using Semanticus.Engine;
using Semanticus.Engine.Decisions;
using Xunit;

namespace Semanticus.Tests
{
    public sealed class AiSchemaFamilyTests : IClassFixture<ModelTemplates>
    {
        private readonly ModelTemplates _templates;
        public AiSchemaFamilyTests(ModelTemplates templates) => _templates = templates;

        private const string Point = "ai.schema";
        private const string Prefix = "jev.ai-schema.";

        /// <summary>Customer and Returns are joined from Sales. Sales has an amount, a line key and a hidden column;
        /// its measures are Total Sales (uses Amount), Share Base (uses Amount), Share (uses both) and Returned (uses
        /// Returns[Qty]).</summary>
        private async Task<DecisionFixture> ModelAsync(string file, bool pro = true)
        {
            var f = new DecisionFixture(pro ? TestEntitlements.Pro : TestEntitlements.Free);
            await _templates.OpenCopyAsync(f, "contoso", b => BuildModelAsync(b), file);
            f.UseNull();
            return f;
        }

        private static async Task BuildModelAsync(DecisionFixture f)
        {
            var e = f.Engine;
            await e.CreateModelAsync("Contoso", 1604);
            await e.CreateTableAsync("Customer", "human");
            await e.CreateColumnAsync("table:Customer", "CustomerKey", "Int64", null, "human");
            await e.CreateColumnAsync("table:Customer", "Customer Name", "String", null, "human");
            await e.CreateColumnAsync("table:Customer", "Region", "String", null, "human");
            await e.CreateTableAsync("Sales", "human");
            await e.CreateColumnAsync("table:Sales", "CustomerKey", "Int64", null, "human");
            await e.CreateColumnAsync("table:Sales", "Amount", "Double", null, "human");
            await e.CreateColumnAsync("table:Sales", "LineKey", "Int64", null, "human");
            await e.CreateColumnAsync("table:Sales", "Internal", "Double", null, "human");
            await e.SetColumnMetadataAsync("column:Sales/Internal", true, null, null, null, "human");
            await e.CreateTableAsync("Returns", "human");
            await e.CreateColumnAsync("table:Returns", "Qty", "Int64", null, "human");
            await e.CreateMeasureAsync("table:Sales", "Total Sales", "SUM ( Sales[Amount] )", "human");
            await e.CreateMeasureAsync("table:Sales", "Share Base", "SUM ( Sales[Amount] )", "human");
            await e.CreateMeasureAsync("table:Sales", "Share", "DIVIDE ( [Total Sales], [Share Base] )", "human");
            await e.CreateMeasureAsync("table:Sales", "Returned", "SUM ( Returns[Qty] )", "human");
            await e.CreateRelationshipAsync("column:Sales/CustomerKey", "column:Customer/CustomerKey", null, null, "human");
        }

        private sealed class CapturingOracle : IDecisionOracle
        {
            private readonly IDecisionOracle _inner;
            private readonly List<DecisionRequest> _seen = new List<DecisionRequest>();
            public CapturingOracle(IDecisionOracle inner) { _inner = inner; }
            public bool Available => true;
            public IReadOnlyList<DecisionRequest> Seen { get { lock (_seen) return _seen.ToList(); } }
            public Task<DecisionAnswers> AskAsync(DecisionRequest request, CancellationToken ct)
            {
                lock (_seen) _seen.Add(request);
                return _inner.AskAsync(request, ct);
            }
        }

        private static async Task<Dictionary<string, string>> DigestsAsync(DecisionFixture f)
        {
            var prepared = await f.Sessions.Current.ReadAsync(m => AiSchemaFamily.Prepare(m));
            // The family asks a table's objects at once (m11/jev-batch): an entry is keyed by the object's ref.
            return prepared.Asks.ToDictionary(a => a.Ref, a => a.Ref, StringComparer.Ordinal);
        }

        private static string Fixture(IEnumerable<(string Digest, string Choice, double Confidence)> rows)
        {
            var sb = new StringBuilder("{\"servedModelVersion\":\"jev-test-1\",\"entries\":[");
            var first = true;
            foreach (var (digest, choice, confidence) in rows)
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append("{\"digest\":").Append(JsonSerializer.Serialize(digest))
                  .Append(",\"point\":\"").Append(Point).Append('"')
                  .Append(",\"usage\":{\"input_tokens\":500,\"output_tokens\":20}")
                  .Append(",\"answers\":{\"ai_schema\":{\"type\":\"choice\",\"choice\":").Append(JsonSerializer.Serialize(choice))
                  .Append(",\"confidence\":").Append(confidence.ToString(CultureInfo.InvariantCulture)).Append("}}}");
            }
            return sb.Append("]}").ToString();
        }

        /// <summary>Jev's recorded answers. Everything not named here is "include", sure.</summary>
        private static readonly Dictionary<string, (string Choice, double Confidence)> Picks = new Dictionary<string, (string, double)>(StringComparer.Ordinal)
        {
            ["column:Sales/CustomerKey"] = ("include", 0.5),      // Jev not sure: the rule decides, and VIS-FK leaves it out
            ["column:Customer/CustomerKey"] = ("exclude", 0.9),   // Jev sure: a Jev line
            ["column:Sales/LineKey"] = ("exclude", 0.7),          // likely is not sure: the rule keeps it in, no line
            ["column:Customer/Region"] = ("exclude", 0.4),        // unsure: the rule keeps it in, no line
            ["column:Sales/Amount"] = ("exclude", 0.95),          // refused: Total Sales and Share Base use it
            ["measure:Sales/Share Base"] = ("exclude", 0.95),     // refused: Share uses it and stays in
            ["table:Returns"] = ("exclude", 0.95),                // refused: Returned reads Returns[Qty]
        };

        private static async Task<CapturingOracle> AnswerAsync(DecisionFixture f, bool decides = true,
            IReadOnlyDictionary<string, (string Choice, double Confidence)> picks = null)
        {
            await f.ConsentAsync();
            f.Engine.AiSchemaJevDecidesForTest = decides;
            // The other families are off, so every ask counted here is this family's.
            await f.Engine.SetDecisionPointEnabledAsync(DecisionRegistry.MeasureFolderAndFormat, false, "human");
            await f.Engine.SetDecisionPointEnabledAsync(DecisionRegistry.ColumnFormat, false, "human");
            await f.Engine.SetDecisionPointEnabledAsync(DecisionRegistry.ColumnCategory, false, "human");
            await f.Engine.SetDecisionPointEnabledAsync(DecisionRegistry.ColumnSummarize, false, "human");
            var d = await DigestsAsync(f);
            picks ??= Picks;
            var recorded = new ByObjectRecordedOracle(Fixture(d.Select(kv =>
                picks.TryGetValue(kv.Key, out var p) ? (kv.Value, p.Choice, p.Confidence) : (kv.Value, "include", 0.95))), "jev-test-1");
            var capturing = new CapturingOracle(recorded);
            f.Engine.UseDecisionsForTest(new DecisionService(capturing, new DecisionBudget()));
            return capturing;
        }

        private static async Task<ModelPlanView> SettledPlanAsync(DecisionFixture f)
        {
            await f.Engine.GetModelPlanAsync();
            await f.Engine.JevSettledForTest();
            return await f.Engine.GetModelPlanAsync();
        }

        private static Task<string[]> ExcludedAsync(DecisionFixture f) =>
            f.Sessions.Current.ReadAsync(m => ReadinessRuleSet.AiSchemaExcluded(m)
                .Select(ObjectRefs.For).OrderBy(r => r, StringComparer.Ordinal).ToArray());

        // ---- the replay bar, and free ----------------------------------------------------------------------

        [Fact]
        public async Task Below_the_replay_bar_nothing_is_asked_and_there_is_no_item()
        {
            using var f = await ModelAsync("AiSchemaBar.bim");
            var oracle = await AnswerAsync(f, decides: false);

            var plan = await SettledPlanAsync(f);

            Assert.DoesNotContain(plan.Items, i => i.Id.StartsWith(Prefix, StringComparison.Ordinal));
            Assert.DoesNotContain(oracle.Seen, r => r.PointName == Point);
            // The shipped value is the replay's verdict under Kane's switch-on bar of 26 September
            // (docs/redesign/jev-ai-schema-replay.md): run 2 beat the rule, p 0.008, no wrong exclusion. Jev decides.
            Assert.True(AiSchemaFamily.JevDecides);
        }

        [Fact]
        public async Task A_free_session_asks_nothing_and_has_no_item_even_with_the_bar_met()
        {
            using var f = await ModelAsync("AiSchemaFree.bim", pro: false);
            await f.ConsentAsync();
            f.Engine.AiSchemaJevDecidesForTest = true;
            var oracle = f.UseRecorded(Fixture(new[] { (new string('0', 64), "exclude", 0.99) }));

            var plan = await f.Engine.GetModelPlanAsync();
            await f.Engine.JevSettledForTest();

            Assert.DoesNotContain(plan.Items, i => i.Id.StartsWith(Prefix, StringComparison.Ordinal));
            Assert.Equal(0, oracle.AskCount);
            Assert.Equal(0, f.Engine.Decisions.RequestsStarted);
            Assert.Equal(0, f.Engine.JevRunsStartedForTest);
        }

        // ---- Pro --------------------------------------------------------------------------------------------

        [Fact]
        public async Task Pro_is_Jev_when_sure_else_the_rule_one_row_per_table_with_engine_built_reasons()
        {
            using var f = await ModelAsync("AiSchemaPro.bim");
            await AnswerAsync(f);

            var plan = await SettledPlanAsync(f);
            var rows = plan.Items.Where(i => i.Id.StartsWith(Prefix, StringComparison.Ordinal)).OrderBy(i => i.Id, StringComparer.Ordinal).ToArray();

            Assert.Equal(new[] { Prefix + "table:Customer", Prefix + "table:Sales" }, rows.Select(i => i.Id).ToArray());
            var sales = rows.Single(i => i.Id == Prefix + "table:Sales");
            var customer = rows.Single(i => i.Id == Prefix + "table:Customer");
            Assert.Equal("Choose which 1 field Copilot can see in Sales", sales.Title);
            Assert.Equal("Choose which 1 field Copilot can see in Customer", customer.Title);
            Assert.Equal(ModelPlanSections.Trust, sales.Section);
            Assert.Equal(ModelPlanOwners.Jev, sales.Who);
            Assert.Equal(ModelPlanChangeStates.Proposed, sales.ChangeSet.State);

            foreach (var line in rows.SelectMany(i => i.ChangeSet.Lines))
            {
                Assert.Equal("set_ai_data_schema", line.Op);
                Assert.Equal("Excluded", line.After);
                Assert.True(line.Kept, line.Id);
                Assert.False(string.IsNullOrWhiteSpace(line.Reason), line.Id);
                Assert.True(line.Check.Exists, line.Id);
                Assert.DoesNotContain("\u2014", line.Reason, StringComparison.Ordinal);
                Assert.DoesNotContain("\u2014", line.Check.Text, StringComparison.Ordinal);
            }

            // Jev was sure: a Jev decision.
            var sure = Assert.Single(customer.ChangeSet.Lines);
            Assert.Equal("column:Customer/CustomerKey", sure.Target);
            Assert.Equal(ModelPlanChangeKinds.Decision, sure.Provenance.Kind);
            Assert.Equal(Point, sure.Provenance.Point);
            Assert.Equal("sure", sure.Provenance.ConfidenceWord);

            // Jev was not sure: the rule decided, and the line says it is the rule's.
            var ruled = Assert.Single(sales.ChangeSet.Lines);
            Assert.Equal("column:Sales/CustomerKey", ruled.Target);
            Assert.Equal(ModelPlanChangeKinds.Rule, ruled.Provenance.Kind);
            Assert.Equal("AI-SCHEMA-RULE", ruled.Provenance.Rule);
            Assert.Equal("CustomerKey joins Sales to Customer. Copilot still follows that join without seeing the key.", ruled.Reason);
            Assert.Contains(sales.Why, w => w.Contains("rules", StringComparison.Ordinal) && w.Contains("not sure", StringComparison.Ordinal));

            // A likely or unsure pick the rule disagrees with is no line at all.
            Assert.DoesNotContain(rows.SelectMany(i => i.ChangeSet.Lines), l => l.Target == "column:Sales/LineKey" || l.Target == "column:Customer/Region");
        }

        [Fact]
        public async Task What_a_kept_measure_uses_is_refused_with_a_reason_never_a_line()
        {
            using var f = await ModelAsync("AiSchemaChecks.bim");
            await AnswerAsync(f);

            var plan = await SettledPlanAsync(f);
            var sales = plan.Items.Single(i => i.Id == Prefix + "table:Sales");
            var lines = plan.Items.Where(i => i.Id.StartsWith(Prefix, StringComparison.Ordinal)).SelectMany(i => i.ChangeSet.Lines).ToArray();

            Assert.DoesNotContain(lines, l => l.Target == "column:Sales/Amount");
            Assert.DoesNotContain(lines, l => l.Target == "measure:Sales/Share Base");
            Assert.DoesNotContain(plan.Items, i => i.Id == Prefix + "table:Returns");
            Assert.Contains(sales.ChangeSet.Refused, r => r.Target == "column:Sales/Amount" && r.Reason.Contains("Copilot still sees", StringComparison.Ordinal));
            Assert.Contains(sales.ChangeSet.Refused, r => r.Target == "measure:Sales/Share Base"
                && r.Reason == "Share uses Share Base, and Copilot still sees Share, so Share Base stays in. Leave Share out too to leave Share Base out.");
            Assert.All(sales.ChangeSet.Refused, r => Assert.DoesNotContain("\u2014", r.Reason, StringComparison.Ordinal));
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Approve_leaves_the_kept_things_out_as_one_undo_step_and_the_schema_rule_stays_quiet()
        {
            using var f = await ModelAsync("AiSchemaApprove.bim");
            await AnswerAsync(f);
            var sales = (await SettledPlanAsync(f)).Items.Single(i => i.Id == Prefix + "table:Sales");

            var result = await f.Engine.ModelPlanActionAsync(sales.Id, "approve", "human", sales.ChangeSet.Digest);

            Assert.True(result.Performed, result.Sentence);
            Assert.Equal(new[] { "column:Sales/CustomerKey" }, await ExcludedAsync(f));
            var scan = await f.Engine.AiReadinessScanAsync();
            Assert.DoesNotContain(scan.Findings, x => x.RuleId == "AISCHEMA-DEP-MISSING");

            await f.Engine.UndoAsync("human");
            Assert.Empty(await ExcludedAsync(f));
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_second_read_changes_nothing_so_a_digest_stays_approvable()
        {
            using var f = await ModelAsync("AiSchemaStable.bim");
            await AnswerAsync(f);

            var first = (await SettledPlanAsync(f)).Items.Single(i => i.Id == Prefix + "table:Sales");
            var second = (await f.Engine.GetModelPlanAsync()).Items.Single(i => i.Id == Prefix + "table:Sales");

            Assert.Equal(first.ChangeSet.Digest, second.ChangeSet.Digest);
        }

        [Fact]
        public async Task A_thing_already_left_out_that_Jev_is_sure_of_arrives_dropped_and_waits_for_the_person()
        {
            // Coordinator's decision (26 Sep, D1): what a person already left out is their intent. Jev may still say
            // people ask about it, but the line arrives dropped, says so in plain words, and applies only if kept.
            using var f = await ModelAsync("AiSchemaBack.bim");
            await f.Engine.SetAiDataSchemaAsync("column:Customer/Region", false, null, "human");
            await AnswerAsync(f, picks: new Dictionary<string, (string, double)>());

            var customer = (await SettledPlanAsync(f)).Items.Single(i => i.Id == Prefix + "table:Customer");
            var back = Assert.Single(customer.ChangeSet.Lines);
            Assert.Equal("column:Customer/Region", back.Target);
            Assert.Equal("Excluded", back.Before);
            Assert.Equal("Included", back.After);
            Assert.False(back.Kept, "a put-back of the person's own exclusion arrived kept");
            Assert.Equal(ModelPlanChangeKinds.Decision, back.Provenance.Kind);
            Assert.Equal("sure", back.Provenance.ConfidenceWord);
            Assert.Equal("Jev thinks people ask about Region; you left it out, so this waits for you.", back.Reason);
            Assert.Contains(customer.Why, w => w.Contains("you left out", StringComparison.Ordinal));
            Assert.DoesNotContain(customer.Why, w => w.Contains("because Jev was unsure", StringComparison.Ordinal));

            // Approving the row as it arrives changes nothing: the person has not kept the put-back.
            var r = await f.Engine.ModelPlanActionAsync(customer.Id, "approve", "human", customer.ChangeSet.Digest);
            Assert.False(r.Performed, r.Sentence);
            Assert.Equal(new[] { "column:Customer/Region" }, await ExcludedAsync(f));
        }

        [Fact]
        public async Task A_row_says_who_chose_its_lines_Jev_the_rule_or_both_with_the_split()
        {
            using var f = await ModelAsync("AiSchemaWho.bim");
            await AnswerAsync(f, picks: new Dictionary<string, (string, double)>
            {
                ["column:Sales/CustomerKey"] = ("include", 0.5),    // the rule: VIS-FK leaves it out
                ["column:Sales/LineKey"] = ("exclude", 0.95),       // Jev, sure
            });

            var sales = (await SettledPlanAsync(f)).Items.Single(i => i.Id == Prefix + "table:Sales");
            Assert.Equal(2, sales.ChangeSet.Lines.Length);
            Assert.Equal(PlanSources.Jev, sales.Source);
            Assert.Contains(sales.Why, w => w.StartsWith("Jev chose 1 of these", StringComparison.Ordinal)
                && w.Contains("Semanticus's own rules chose the other 1", StringComparison.Ordinal));
            Assert.DoesNotContain(sales.Why, w => w.StartsWith("Jev chose each setting", StringComparison.Ordinal));
        }

        [Fact]
        public async Task A_row_whose_lines_are_all_the_rules_is_not_from_Jev()
        {
            using var f = await ModelAsync("AiSchemaRuleOnly.bim");
            await AnswerAsync(f, picks: new Dictionary<string, (string, double)> { ["column:Sales/CustomerKey"] = ("include", 0.5) });

            var sales = (await SettledPlanAsync(f)).Items.Single(i => i.Id == Prefix + "table:Sales");
            Assert.Equal(PlanSources.Found, sales.Source);
            Assert.Contains(sales.Why, w => w.StartsWith("Semanticus's own rules chose each setting", StringComparison.Ordinal));
            Assert.DoesNotContain(sales.Why, w => w.Contains("Jev chose", StringComparison.Ordinal));
        }

        /// <summary>The model the two across-table tests share: Product[List Price], used by Sales[Average List Price].</summary>
        private async Task<DecisionFixture> AcrossAsync(string file)
        {
            var f = new DecisionFixture(TestEntitlements.Pro);
            await _templates.OpenCopyAsync(f, "across", async b =>
            {
                await BuildModelAsync(b);
                await b.Engine.CreateTableAsync("Product", "human");
                await b.Engine.CreateColumnAsync("table:Product", "List Price", "Double", null, "human");
                await b.Engine.CreateColumnAsync("table:Product", "Product Name", "String", null, "human");
                await b.Engine.CreateMeasureAsync("table:Sales", "Average List Price", "AVERAGE ( Product[List Price] )", "human");
            }, file);
            f.UseNull();
            return f;
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Dependent_put_backs_across_two_tables_kept_by_the_person_apply_in_the_order_the_plan_offers()
        {
            using var f = await AcrossAsync("AiSchemaAcross.bim");
            await f.Engine.SetAiDataSchemaAsync("measure:Sales/Average List Price", false, null, "human");
            await f.Engine.SetAiDataSchemaAsync("column:Product/List Price", false, null, "human");
            await AnswerAsync(f, picks: new Dictionary<string, (string, double)>());

            var plan = await SettledPlanAsync(f);
            var sales = plan.Items.Single(i => i.Id == Prefix + "table:Sales");
            var product = plan.Items.Single(i => i.Id == Prefix + "table:Product");
            var measureLine = sales.ChangeSet.Lines.Single(l => l.Target == "measure:Sales/Average List Price");
            var columnLine = product.ChangeSet.Lines.Single(l => l.Target == "column:Product/List Price");
            Assert.False(measureLine.Kept);
            Assert.False(columnLine.Kept);
            Assert.Contains("List Price", measureLine.Check.Text, StringComparison.Ordinal);

            // The person keeps both put-backs. The row that puts the measure back now waits for the row that puts
            // back the field it uses, whatever the plan's order says.
            await f.Engine.SetPlanItemAsync(measureLine.PlanItemId, null, true, "human");
            await f.Engine.SetPlanItemAsync(columnLine.PlanItemId, null, true, "human");
            plan = await SettledPlanAsync(f);
            sales = plan.Items.Single(i => i.Id == Prefix + "table:Sales");
            product = plan.Items.Single(i => i.Id == Prefix + "table:Product");
            Assert.Contains(sales.WaitsFor ?? Array.Empty<PlanWaitRef>(), w => w.Id == product.Id);
            Assert.DoesNotContain(product.WaitsFor ?? Array.Empty<PlanWaitRef>(), w => w.Id == sales.Id);

            var first = await f.Engine.ModelPlanActionAsync(product.Id, "approve", "human", product.ChangeSet.Digest);
            Assert.True(first.Performed, first.Sentence);
            sales = (await SettledPlanAsync(f)).Items.Single(i => i.Id == Prefix + "table:Sales");
            var line = sales.ChangeSet.Lines.Single(l => l.Target == "measure:Sales/Average List Price");
            if (!line.Kept)
            {
                await f.Engine.SetPlanItemAsync(line.PlanItemId, null, true, "human");
                sales = (await SettledPlanAsync(f)).Items.Single(i => i.Id == Prefix + "table:Sales");
            }
            var second = await f.Engine.ModelPlanActionAsync(sales.Id, "approve", "human", sales.ChangeSet.Digest);
            Assert.True(second.Performed, second.Sentence);

            Assert.Empty(await ExcludedAsync(f));
            var scan = await f.Engine.AiReadinessScanAsync();
            Assert.DoesNotContain(scan.Findings, x => x.RuleId == "AISCHEMA-DEP-MISSING");
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Leaving_things_out_across_two_tables_leaves_the_measure_out_before_the_field_it_uses()
        {
            using var f = await AcrossAsync("AiSchemaAcrossOut.bim");
            await AnswerAsync(f, picks: new Dictionary<string, (string, double)>
            {
                ["measure:Sales/Average List Price"] = ("exclude", 0.95),
                ["column:Product/List Price"] = ("exclude", 0.95),
            });

            var plan = await SettledPlanAsync(f);
            var sales = plan.Items.Single(i => i.Id == Prefix + "table:Sales");
            var product = plan.Items.Single(i => i.Id == Prefix + "table:Product");
            Assert.Contains(product.WaitsFor ?? Array.Empty<PlanWaitRef>(), w => w.Id == sales.Id);
            Assert.DoesNotContain(sales.WaitsFor ?? Array.Empty<PlanWaitRef>(), w => w.Id == product.Id);
        }

        // ---- what a run costs ------------------------------------------------------------------------------------

        private static async Task<string> RecordedAnswersAsync(DecisionFixture f, IReadOnlyDictionary<string, (string Choice, double Confidence)> picks)
        {
            var d = await DigestsAsync(f);
            return Fixture(d.Select(kv => picks.TryGetValue(kv.Key, out var p) ? (kv.Value, p.Choice, p.Confidence) : (kv.Value, "include", 0.95)));
        }

        /// <summary>What the development client says when it sent an ask once, heard nothing back, and this session's
        /// budget could not fit the retry. Every ai.schema ask ends that way here.</summary>
        private sealed class AskedThenBudgetOracle : IDecisionOracle
        {
            public bool Available => true;
            public Task<DecisionAnswers> AskAsync(DecisionRequest request, CancellationToken ct) =>
                Task.FromResult(DecisionAnswers.NotDecided(request, request.PointName == Point
                    ? JevDecisionOracle.AskedThenStoppedReason : "the advice service did not answer"));
        }

        /// <summary>A batch whose only ask was sent once and then stopped by this session's budget is a budget stop, and
        /// the rows say what the budget stop says. It used to become the batch's stop REASON instead, so the row read
        /// "... so it was not asked again." and then promised the rest would come when Jev could answer, which is a budget
        /// stop told as a wait for Jev (ai.schema verify round 5, L1). The development route is the only one that retries,
        /// so it is the only one that can reach this.</summary>
        [Fact]
        public async Task An_ask_the_budget_stopped_after_one_try_reads_as_the_budget_on_every_row()
        {
            using var f = await ModelAsync("AiSchemaAskedThenBudget.bim");
            await AnswerAsync(f);   // permission, the bar, and the other families off
            f.Engine.UseDecisionsForTest(new DecisionService(new AskedThenBudgetOracle(), new DecisionBudget()));
            f.Engine.AiSchemaCapForTest = 1;   // one ask in the batch, so no other ask's budget refusal can decide it

            var plan = await SettledPlanAsync(f);

            var rows = plan.Items.Where(i => i.Id.StartsWith(Prefix, StringComparison.Ordinal)).ToArray();
            Assert.NotEmpty(rows);
            foreach (var row in rows)
            {
                var line = Assert.Single((row.Why ?? Array.Empty<string>())
                    .Where(w => w.StartsWith("Jev has looked at 0 of ", StringComparison.Ordinal)));
                Assert.EndsWith("This session has used its suggestion budget, so the rest wait for the next session.", line, StringComparison.Ordinal);
            }
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_restart_with_no_change_asks_Jev_nothing_again()
        {
            using var f = await ModelAsync("AiSchemaRestart.bim");
            var picks = new Dictionary<string, (string, double)> { ["column:Sales/LineKey"] = ("exclude", 0.95) };
            await AnswerAsync(f, picks: picks);
            var inner = new ByObjectRecordedOracle(await RecordedAnswersAsync(f, picks), "jev-test-1");
            var first = new CapturingOracle(inner);
            // The service the engine builds for a real session carries the durable answer store; this one does too.
            f.Engine.UseDecisionsForTest(new DecisionService(first, new DecisionBudget(), answers: new DecisionAnswerStore()));
            var before = await SettledPlanAsync(f);
            Assert.Equal(1, first.Seen.Count(r => r.PointName == Point));   // one request for the model (Customer, Sales, Returns), not per object (14)
            // Permission is bound to a marker inside the model, so the restart opens the file as saved.
            var path = System.IO.Path.Combine(f.Workspace, "AiSchemaRestart.bim");
            await f.Engine.SaveAsync(path, "bim", false, "human");

            // A new engine on the same machine and the same file, with nothing changed.
            var sessions = new SessionManager();
            var again = new LocalEngine(sessions, TestEntitlements.Pro, f.Workspace);
            try
            {
                await again.OpenAsync(path);
                again.AiSchemaJevDecidesForTest = true;
                again.JevBatchedForTest = _ => true;   // the tests' mode (DecisionFixture sets it on the first engine)
                var second = new CapturingOracle(inner);
                again.UseDecisionsForTest(new DecisionService(second, new DecisionBudget(), answers: new DecisionAnswerStore()));
                await again.GetModelPlanAsync();
                await again.JevSettledForTest();
                var after = await again.GetModelPlanAsync();

                Assert.Equal(0, second.Seen.Count(r => r.PointName == Point));
                string[] Rows(ModelPlanView v) => v.Items.Where(i => i.Id.StartsWith(Prefix, StringComparison.Ordinal))
                    .Select(i => i.Id + ":" + string.Join(",", i.ChangeSet.Lines.Select(l => l.Target + "=" + l.After)))
                    .OrderBy(x => x, StringComparer.Ordinal).ToArray();
                Assert.NotEmpty(Rows(after));
                Assert.Equal(Rows(before), Rows(after));

                // A change to one table's facts asks again about that table's objects, and nothing else.
                await again.CreateColumnAsync("table:Customer", "Segment", "String", null, "human");
                await again.GetModelPlanAsync();
                await again.JevSettledForTest();
                var asked = second.Seen.Where(r => r.PointName == Point).ToArray();
                Assert.NotEmpty(asked);
                Assert.All(asked, r =>
                {
                    using var objects = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(r.Snapshot["objects"]));
                    Assert.All(objects.RootElement.EnumerateArray(), o => Assert.Equal("Customer", o.GetProperty("table").GetString()));
                });
            }
            finally
            {
                again.Dispose();
                sessions.Dispose();
            }
        }

        [Fact]
        public async Task Withdrawing_permission_forgets_the_stored_answers()
        {
            using var f = await ModelAsync("AiSchemaForget.bim");
            var picks = new Dictionary<string, (string, double)>();
            await AnswerAsync(f, picks: picks);
            var inner = new ByObjectRecordedOracle(await RecordedAnswersAsync(f, picks), "jev-test-1");
            var store = new DecisionAnswerStore();
            var first = new CapturingOracle(inner);
            f.Engine.UseDecisionsForTest(new DecisionService(first, new DecisionBudget(), answers: store));
            await SettledPlanAsync(f);
            Assert.Equal(1, first.Seen.Count(r => r.PointName == Point));   // one request for the model (Customer, Sales, Returns), not per object (14)
            var folder = System.IO.Path.Combine(ConnectionRegistry.Root(), "decision-answers");
            string[] Kept() => System.IO.Directory.Exists(folder)
                ? System.IO.Directory.GetFiles(folder, "*.jsonl").Where(p => new System.IO.FileInfo(p).Length > 0).ToArray()
                : Array.Empty<string>();
            Assert.NotEmpty(Kept());   // precondition: the answers were kept on this computer

            await f.Engine.WithdrawConsentAsync("human");
            // Forgotten on disk at the withdrawal itself, not only unserved later: the generation check below would
            // hide a forget that never happened, so this is the assertion that has teeth for the forget (R2-1).
            Assert.True(Kept().Length == 0, "Permission was withdrawn, but answers are still kept: "
                + string.Join(", ", Kept().Select(System.IO.Path.GetFileName)));
            await f.ConsentAsync();
            var second = new CapturingOracle(inner);
            f.Engine.UseDecisionsForTest(new DecisionService(second, new DecisionBudget(), answers: store));
            await SettledPlanAsync(f);

            // Answers bought under the old permission are not served under the new one.
            Assert.Equal(1, second.Seen.Count(r => r.PointName == Point));   // one request for the model, not per object (14)
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_restart_is_answered_from_the_store_the_engine_itself_builds()
        {
            // The test above hands in a service it assembled with a store. This one puts a recorded client inside the
            // service the ENGINE builds for a real session (LocalEngine.BuildDecisions), so a production service that
            // stopped carrying the store fails here (verify round 2: with the store taken out of the engine's own
            // service, no test failed).
            using var f = await ModelAsync("AiSchemaRestartProduct.bim");
            var picks = new Dictionary<string, (string, double)> { ["column:Sales/LineKey"] = ("exclude", 0.95) };
            await AnswerAsync(f, picks: picks);
            var inner = new ByObjectRecordedOracle(await RecordedAnswersAsync(f, picks), "jev-test-1");
            var first = new CapturingOracle(inner);
            f.Engine.UseDecisionsForTest(f.Engine.BuildDecisions(first, new DecisionBudget()));
            await SettledPlanAsync(f);
            Assert.Equal(1, first.Seen.Count(r => r.PointName == Point));   // one request for the model (Customer, Sales, Returns), not per object (14)
            var path = System.IO.Path.Combine(f.Workspace, "AiSchemaRestartProduct.bim");
            await f.Engine.SaveAsync(path, "bim", false, "human");

            var sessions = new SessionManager();
            var again = new LocalEngine(sessions, TestEntitlements.Pro, f.Workspace);
            try
            {
                await again.OpenAsync(path);
                again.AiSchemaJevDecidesForTest = true;
                again.JevBatchedForTest = _ => true;   // the tests' mode (DecisionFixture sets it on the first engine)
                var second = new CapturingOracle(inner);
                again.UseDecisionsForTest(again.BuildDecisions(second, new DecisionBudget()));
                await again.GetModelPlanAsync();
                await again.JevSettledForTest();
                var after = await again.GetModelPlanAsync();

                Assert.Equal(0, second.Seen.Count(r => r.PointName == Point));
                Assert.Contains(after.Items, i => i.Id == Prefix + "table:Sales"
                    && i.ChangeSet.Lines.Any(l => l.Target == "column:Sales/LineKey"));
            }
            finally
            {
                again.Dispose();
                sessions.Dispose();
            }
        }

        [Fact]
        public async Task An_answer_kept_by_an_older_build_with_its_ids_as_they_were_is_dropped_from_disk_and_asked_again()
        {
            // Before the ids were hashed an entry held them as they were, and a measure's folder id is the model's own
            // folder name. Such an entry is never served, and reading the file rewrites it without the entry, so the
            // name does not stay on this computer. Discarding it only means the question is asked again.
            using var f = new DecisionFixture();
            var request = DecisionFixture.SampleRequest();   // its folder option is the model's own folder, "Volume"
            var answers = await new RecordedDecisionOracle(
                DecisionFixture.ProofFixture(request.Digest, "Volume", 0.93, "currency", 0.97), "jev-test-1")
                .AskAsync(request, CancellationToken.None);
            var scope = "disk:contoso#older";
            var store = new DecisionAnswerStore();
            store.Put(scope, 1, request, answers);
            var folder = System.IO.Path.Combine(ConnectionRegistry.Root(), "decision-answers");
            var file = System.IO.Directory.GetFiles(folder, "*.jsonl").Single();
            Assert.True(new DecisionAnswerStore().TryGet(scope, 1, request, null, out var served));   // today's entry serves
            Assert.Equal(MeasureDefaultsPoint.FolderOptionId("Volume"), served.For(MeasureDefaultsPoint.FolderQuestion).Choice);

            // The same answer as an older build wrote it: no format number, and the option ids as they were.
            var older = "{\"d\":" + JsonSerializer.Serialize(request.Digest) + ",\"p\":" + JsonSerializer.Serialize(request.PointName)
                + ",\"v\":\"jev-test-1\",\"g\":1,\"t\":" + JsonSerializer.Serialize(DateTimeOffset.UtcNow)
                + ",\"a\":{\"display_folder\":{\"type\":\"choice\",\"choice\":\"folder:Volume\",\"confidence\":0.93},"
                + "\"format_family\":{\"type\":\"choice\",\"choice\":\"currency\",\"confidence\":0.97}}}";
            System.IO.File.WriteAllText(file, older + "\n");

            Assert.False(new DecisionAnswerStore().TryGet(scope, 1, request, null, out _));
            Assert.DoesNotContain("Volume", System.IO.File.ReadAllText(file), StringComparison.Ordinal);
        }

        /// <summary>The install key is owner only WHILE it is being written, not only once the belt-and-braces chmod after
        /// it has run. The verifier's test for this reads the source between two markers, so a create mode applied on
        /// Windows only (Linux back to the default) passed it (ai.schema verify round 5, L2). This one looks at the file
        /// itself from inside the write, through the store's seam, before anything else can touch its mode.</summary>
        [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "umask")]
        private static extern uint Umask(uint mask);

        [Fact]
        public async Task This_installs_key_is_already_owner_only_while_its_bytes_are_written()
        {
            if (OperatingSystem.IsWindows()) return;   // Windows has no Unix mode; the folder's ACL is what holds it there
            using var f = new DecisionFixture();
            var request = DecisionFixture.SampleRequest();
            var answers = await new RecordedDecisionOracle(
                DecisionFixture.ProofFixture(request.Digest, "Volume", 0.93, "currency", 0.97), "jev-test-1")
                .AskAsync(request, CancellationToken.None);
            System.IO.UnixFileMode? whileWritten = null;
            var store = new DecisionAnswerStore { KeyWrittenForTest = path => whileWritten = System.IO.File.GetUnixFileMode(path) };

            // A strict umask (077) would make any file owner only, so the check could not tell the create mode from the
            // default: this Put runs under the usual 022, which leaves group and others able to read a file made
            // without the mode (Sol on the round 5 fix). The umask is the process's, so it is put back at once.
            var previous = Umask(Convert.ToUInt32("022", 8));
            try { store.Put("disk:contoso#keymode", 1, request, answers); }
            finally { Umask(previous); }

            Assert.True(whileWritten.HasValue, "precondition: the store made a key and the seam saw it being written");
            Assert.Equal(System.IO.UnixFileMode.UserRead | System.IO.UnixFileMode.UserWrite, whileWritten.Value);
        }

        [Fact]
        public async Task An_answer_kept_with_its_question_names_as_they_were_is_dropped_from_disk_and_asked_again()
        {
            // Format 2 hashed the option ids but kept each question's name as it was, and a question can be named after
            // the model (plan.rank's "value:starter/table:Sales", verify round 3, R3-2). Today's entry holds no question
            // name, and a format 2 entry is never served: reading the file rewrites it without the entry.
            using var f = new DecisionFixture();
            var request = DecisionFixture.SampleRequest();
            var answers = await new RecordedDecisionOracle(
                DecisionFixture.ProofFixture(request.Digest, "Volume", 0.93, "currency", 0.97), "jev-test-1")
                .AskAsync(request, CancellationToken.None);
            var scope = "disk:contoso#format2";
            new DecisionAnswerStore().Put(scope, 1, request, answers);
            var folder = System.IO.Path.Combine(ConnectionRegistry.Root(), "decision-answers");
            var file = System.IO.Directory.GetFiles(folder, "*.jsonl").Single();
            var today = System.IO.File.ReadAllText(file).Trim();
            foreach (var q in request.Questions)
                Assert.DoesNotContain("\"" + q.Name + "\"", today, StringComparison.Ordinal);
            Assert.True(new DecisionAnswerStore().TryGet(scope, 1, request, null, out var served));   // today's entry serves
            Assert.Equal(MeasureDefaultsPoint.FolderOptionId("Volume"), served.For(MeasureDefaultsPoint.FolderQuestion).Choice);

            // The same entry as format 2 wrote it: the option ids still hashed, the question names as they were.
            var entry = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(today);
            var kept = entry["a"].EnumerateObject().Select(p => p.Value).ToArray();
            var plain = request.Questions.Select(q => q.Name).Where(n => answers.For(n) != null).ToArray();
            Assert.Equal(plain.Length, kept.Length);
            var older = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (var kv in entry) older[kv.Key] = kv.Value;
            older["f"] = 2;
            older["a"] = plain.Zip(kept).ToDictionary(x => x.First, x => (object)x.Second, StringComparer.Ordinal);
            System.IO.File.WriteAllText(file, JsonSerializer.Serialize(older) + "\n");

            Assert.False(new DecisionAnswerStore().TryGet(scope, 1, request, null, out _));
            var after = System.IO.File.ReadAllText(file);
            foreach (var n in plain)
                Assert.DoesNotContain("\"" + n + "\"", after, StringComparison.Ordinal);
        }

        [Fact]
        public void Only_a_line_that_lets_Copilot_see_what_the_person_left_out_is_called_a_put_back_on_any_family()
        {
            // The sentence is shared by every Jev family (JevFamily.Items). A dropped line is a put-back only when it
            // restores something the person left out, which only "what Copilot sees" has: an include over an exclusion.
            ChangeItem L(string family, string objectRef, string kind, string before, string after, string point, string status, string word) =>
                new ChangeItem
                {
                    ObjectRef = objectRef, ObjectName = objectRef.Substring(objectRef.LastIndexOf('/') + 1), Kind = kind,
                    Source = JevFamily.Source, JevItem = JevFamily.ItemId(family, "table:Sales"), Before = before, After = after,
                    DecisionPoint = point, DecisionConfidence = 0.95, DecisionWord = word, Status = status,
                };
            const string PutBack = "back something you left out";
            string[] Why(params ChangeItem[] lines) => JevFamily.Items(lines).Single().Why;

            // A person dropped a sure line on each of the other families: no put-back, and no "unsure" either.
            foreach (var (family, point, kind) in new[]
            {
                (MeasuresFamily.Name, DecisionRegistry.MeasureFolderAndFormat, "set_display_folder"),
                (ColumnFormatFamily.Name, DecisionRegistry.ColumnFormat, "set_format_string"),
                (StarterMeasuresFamily.Name, DecisionRegistry.MeasureStarter, "create_measure"),
            })
            {
                var why = Why(L(family, "measure:Sales/Total", kind, "(none)", "Sales", point, "rejected", JevFamily.Sure));
                Assert.DoesNotContain(why, w => w.Contains(PutBack, StringComparison.Ordinal));
                Assert.DoesNotContain(why, w => w.Contains("because Jev was unsure", StringComparison.Ordinal));
            }

            // What Copilot sees: a dropped exclusion is the person's choice; a dropped include over an exclusion is the put-back.
            var dropped = Why(L(AiSchemaFamily.Name, "column:Sales/LineKey", "set_ai_data_schema", "Included", "Excluded", Point, "rejected", JevFamily.Sure));
            Assert.DoesNotContain(dropped, w => w.Contains(PutBack, StringComparison.Ordinal));
            var back = Why(L(AiSchemaFamily.Name, "column:Sales/LineKey", "set_ai_data_schema", "Excluded", "Included", Point, "rejected", JevFamily.Sure));
            Assert.Contains(back, w => w.StartsWith("1 change puts back something you left out", StringComparison.Ordinal));
            var unsure = Why(L(MeasuresFamily.Name, "measure:Sales/Total", "set_display_folder", "(none)", "Sales",
                DecisionRegistry.MeasureFolderAndFormat, "rejected", JevFamily.Unsure));
            Assert.Contains(unsure, w => w.StartsWith("1 change is unticked because Jev was not sure", StringComparison.Ordinal));
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Past_the_cap_Jev_asks_the_rest_in_batches_and_the_row_says_so_until_it_has()
        {
            using var f = await ModelAsync("AiSchemaCap.bim");
            var oracle = await AnswerAsync(f, picks: new Dictionary<string, (string, double)> { ["column:Customer/CustomerKey"] = ("exclude", 0.95) });
            f.Engine.AiSchemaCapForTest = 5;
            f.Engine.JevPerTableForTest = true;   // the cap is asked in whole requests: one a table here, so the first batch is Customer's

            await f.Engine.GetModelPlanAsync();
            await f.Engine.JevSettledForTest();
            // The cap counts OBJECTS and asks whole table chunks (m11/jev-batch): Customer's four objects fit under 5, Sales' eight
            // after them would not, so the first batch is ONE request (Customer, which holds Customer[CustomerKey]).
            Assert.Equal(1, oracle.Seen.Count(r => r.PointName == Point));
            Assert.Equal(new[] { "Customer" }, oracle.Seen.Where(r => r.PointName == Point).SelectMany(r =>
            {
                using var objects = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(r.Snapshot["objects"]));
                return objects.RootElement.EnumerateArray().Select(o => o.GetProperty("table").GetString()).Distinct().ToArray();
            }).ToArray());
            // Reading the plan shows the first batch and starts the next one, so the count on the row is 4 or more.
            var plan = await f.Engine.GetModelPlanAsync();
            var row = plan.Items.Single(i => i.Id == Prefix + "table:Customer");
            Assert.Contains(row.Why, w => w.StartsWith("Jev has looked at ", StringComparison.Ordinal)
                && w.EndsWith(" of 14 things in this model so far. The rest follow.", StringComparison.Ordinal));

            // Each read after a batch lands asks the next one, until everything has been looked at once.
            for (var i = 0; i < 6; i++) plan = await SettledPlanAsync(f);
            // Three table chunks in all (Customer 4 objects, Sales 8, Returns 2), each asked once: a table is never asked twice.
            Assert.Equal(3, oracle.Seen.Count(r => r.PointName == Point));
            Assert.Equal(3, oracle.Seen.Where(r => r.PointName == Point).Select(r => r.Digest).Distinct().Count());
            row = plan.Items.Single(i => i.Id == Prefix + "table:Customer");
            Assert.DoesNotContain(row.Why, w => w.StartsWith("Jev has looked at", StringComparison.Ordinal));
            Assert.Contains(row.ChangeSet.Lines, l => l.Target == "column:Customer/CustomerKey");
        }

        // ---- two families, one column --------------------------------------------------------------------------

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_column_this_family_got_no_answer_for_keeps_the_other_familys_line_once()
        {
            // Column format and what Copilot sees both ask about Sales/Amount. Column format answers; this family gets
            // nothing back for it. "Nothing new for Amount" is this family's to say, not column format's: before, the
            // unanswered set was keyed by object alone, so column format's waiting line was kept AND put back, twice.
            using var f = await ModelAsync("AiSchemaTwoFamilies.bim");
            await f.ConsentAsync();
            f.Engine.AiSchemaJevDecidesForTest = true;
            await f.Engine.SetDecisionPointEnabledAsync(DecisionRegistry.MeasureFolderAndFormat, false, "human");
            var asks = await f.Sessions.Current.ReadAsync(m => ColumnFormatFamily.Prepare(m));
            var amount = asks.Asks.Single(a => a.Ref == "column:Sales/Amount");
            // Column format asks a table's columns at once (m11/jev-batch): the answer is recorded under the column's ref.
            f.UseRecordedByObject("{\"servedModelVersion\":\"jev-test-1\",\"entries\":[{\"digest\":" + JsonSerializer.Serialize(amount.Ref)
                + ",\"point\":\"column.format\",\"usage\":{\"input_tokens\":500,\"output_tokens\":20}"
                + ",\"answers\":{\"format_family\":{\"type\":\"choice\",\"choice\":\"decimal\",\"confidence\":0.95}}}]}");

            await SettledPlanAsync(f);
            var row = (await SettledPlanAsync(f)).Items.Single(i => i.Id == "jev.column-format.table:Sales");

            Assert.Equal(new[] { "column:Sales/Amount" }, row.ChangeSet.Lines.Select(l => l.Target).ToArray());
        }

        // ---- the checks, pure ---------------------------------------------------------------------------------

        [Fact]
        public void A_measure_stays_out_while_what_it_uses_stays_out_and_a_table_carries_its_fields()
        {
            AiSchemaFamily.Ask A(string r, string kind, string table, bool own = false) => new AiSchemaFamily.Ask
            {
                Ref = r, Kind = kind, ObjectName = r.Substring(r.LastIndexOfAny(new[] { ':', '/' }) + 1),
                TableRef = "table:" + table, TableName = table, OwnExcluded = own,
            };
            var prepared = new AiSchemaFamily.Prepared();
            void Known(AiSchemaFamily.Ask a) { prepared.OwnExcluded[a.Ref] = a.OwnExcluded; prepared.TableOf[a.Ref] = a.TableRef; prepared.NameOf[a.Ref] = a.ObjectName; }
            var sales = A("table:Sales", AiSchemaFamily.KindTable, "Sales");
            var amount = A("column:Sales/Amount", AiSchemaFamily.KindColumn, "Sales", own: true);
            var total = A("measure:Sales/Total", AiSchemaFamily.KindMeasure, "Sales", own: true);
            var ret = A("table:Returns", AiSchemaFamily.KindTable, "Returns", own: true);
            var qty = A("column:Returns/Qty", AiSchemaFamily.KindColumn, "Returns");
            foreach (var a in new[] { sales, amount, total, ret, qty }) Known(a);
            prepared.Measures["measure:Sales/Total"] = ("table:Sales", new HashSet<string>(new[] { "column:Sales/Amount" }, StringComparer.OrdinalIgnoreCase));

            var lines = new List<ChangeItem>();
            var refused = new List<ModelPlanChangeRefusal>();
            AiSchemaFamily.Resolve(prepared, new[]
            {
                new AiSchemaFamily.Pick(total, "include", 0.95),    // Amount stays out, so Total cannot come back
                new AiSchemaFamily.Pick(qty, "include", 0.95),      // Returns stays out, so Qty cannot come back alone
                new AiSchemaFamily.Pick(sales, "maybe", 0.99),      // never offered
            }, 0.85, lines, refused);

            Assert.Empty(lines);
            Assert.Equal(new[] { "column:Returns/Qty", "measure:Sales/Total" }, refused.Select(r => r.Target).OrderBy(t => t, StringComparer.Ordinal).ToArray());

            // Put Amount back in the same breath, and Total may come back with it.
            lines.Clear(); refused.Clear();
            AiSchemaFamily.Resolve(prepared, new[]
            {
                new AiSchemaFamily.Pick(total, "include", 0.95),
                new AiSchemaFamily.Pick(amount, "include", 0.95),
            }, 0.85, lines, refused);
            Assert.Equal(new[] { "column:Sales/Amount", "measure:Sales/Total" }, lines.Select(l => l.ObjectRef).OrderBy(t => t, StringComparer.Ordinal).ToArray());
            Assert.Empty(refused);
        }

        // ---- what goes --------------------------------------------------------------------------------------------

        [Fact]
        public async Task Only_names_types_and_structure_facts_go_to_Jev()
        {
            using var f = await ModelAsync("AiSchemaSent.bim");
            var oracle = await AnswerAsync(f);
            await SettledPlanAsync(f);

            var sent = oracle.Seen.Where(r => r.PointName == Point).ToArray();
            // One request for the whole model (m16/jev-batch): 3 tables, holding 3 tables + 7 visible columns (the hidden one is not asked) + 4 measures.
            Assert.Single(sent);
            var declared = DecisionRegistry.Require(Point).SnapshotFields;
            var allowed = new HashSet<string>(StringComparer.Ordinal)
            {
                "table_kind", "visible_columns", "visible_measures", "relationships",
                "data_type", "is_calculated", "is_key", "relationship_end", "sets_order_of_another_column", "used_by_measures",
                "uses",
            };
            foreach (var r in sent)
            {
                // A batched request sends each table's fields once (a map keyed by the table's name) and its objects as a list; nothing undeclared.
                Assert.Equal(new[] { "objects", "table_fields" }, r.Snapshot.Keys.OrderBy(x => x, StringComparer.Ordinal));
                Assert.All(r.Snapshot.Keys, k => Assert.Contains(k, declared));
                using var entries = JsonDocument.Parse(JsonSerializer.Serialize(r.Snapshot["objects"]));
                Assert.Equal(entries.RootElement.GetArrayLength(), r.Questions.Count);
                foreach (var entry in entries.RootElement.EnumerateArray())
                {
                    Assert.Equal(new[] { "facts", "kind", "name", "table" }, entry.EnumerateObject().Select(p => p.Name).OrderBy(x => x, StringComparer.Ordinal));
                    Assert.All(entry.GetProperty("facts").EnumerateObject(), p => Assert.Contains(p.Name, allowed));
                }
                var json = JsonSerializer.Serialize(r.Snapshot);
                Assert.DoesNotContain("SUM", json, StringComparison.Ordinal);      // never a formula
                Assert.DoesNotContain("DIVIDE", json, StringComparison.Ordinal);
                Assert.DoesNotContain("\"Internal\"", json, StringComparison.Ordinal);   // hidden: not asked, not named
            }
        }
    }
}
