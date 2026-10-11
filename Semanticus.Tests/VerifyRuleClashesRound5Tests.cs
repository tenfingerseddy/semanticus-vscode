using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using TabularEditor.TOMWrapper;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// The verifier's failing cases for m11/rule-clashes round 5 (949bc486). The round's promise: a waiver declines a CHANGE
    /// (object, property), whichever rule seeded it, so an accepted finding is never carried out by its twin rule. It holds
    /// for the pairs whose fixes are both mapped (MapSafeFix, MapAiContent, MapBpaFix) while the accepted finding still fires.
    /// It does not hold in three places, each a declined change that is still made:
    ///   1. a readiness finding whose fix kind is Proposal declines nothing. "Key column is visible" (VIS-KEY, which the
    ///      Model Plan offers as "hide it"), "has no format string" (FMT-COLUMN, "set its format") and "relies on implicit
    ///      aggregation" (DAC-IMPLICIT-MEASURE, "set SummarizeBy=None and add one") are accepted, and the plan still seeds
    ///      their Model quality twins pre-approved (HIDE_FOREIGN_KEYS, DATECOLUMN_FORMATSTRING, NUMERIC_COLUMN_SUMMARIZE_BY),
    ///      and Fix all still runs them;
    ///   2. the declined set is read off the accepted findings that FIRE NOW. NUMERIC_COLUMN_SUMMARIZE_BY only fires on a
    ///      visible column and its twin FMT-SUMMARIZE fires on hidden keys too, so once the key is hidden (by the plan's own
    ///      VIS-FK line, nobody declined that) the acceptance declines nothing and the key is unsummed: by Run the plan, by
    ///      a second apply_safe_fixes press and by the next plan;
    ///   3. make_model_ai_ready's queue still asks the assistant for a change another finding declined (a format after
    ///      INTEGER_FORMATTING was accepted, a rename to the table name after NAME-COLUMN was accepted), while the plan
    ///      leaves the same change out.
    /// Each case first shows the change really would be made, so none can pass by accident. They are left failing on purpose.
    /// </summary>
    public sealed class VerifyRuleClashesRound5Tests : IClassFixture<ModelTemplates>
    {
        private readonly ModelTemplates _templates;
        public VerifyRuleClashesRound5Tests(ModelTemplates templates) => _templates = templates;

        private const string Declined = "Left out because you accepted this change under another rule.";

        /// <summary>A small star. Customer[CustomerKey] is the one-side key, marked IsKey, and shares its name with the Sales
        /// foreign key (HIDE_FOREIGN_KEYS matches by name); Sales[Order Date] has no format; Sales[Amount] sums by default;
        /// Sales[ProductKey] sums; Product[ProductName] is a cryptic label column; Order Count has no format.</summary>
        private async Task<DecisionFixture> StarAsync(string? saveAs = null)
        {
            // The build runs once per class; every test opens a copy of the saved model (ModelTemplates, lean suite).
            var f = new DecisionFixture(TestEntitlements.Pro);
            await _templates.OpenCopyAsync(f, "star6", BuildStarAsync, (saveAs ?? "star6") + ((saveAs ?? "star6").EndsWith(".bim", StringComparison.Ordinal) ? "" : ".bim"));
            if (saveAs != null) f.UseNull();
            return f;
        }

        private static async Task BuildStarAsync(DecisionFixture f)
        {
            var e = f.Engine;
            await e.CreateModelAsync("Round5", 1604);
            await e.CreateTableAsync("Sales", "human");
            await e.CreateColumnAsync("table:Sales", "Amount", "Double", null, "human");
            await e.CreateColumnAsync("table:Sales", "CustomerKey", "Int64", null, "human");
            await e.CreateColumnAsync("table:Sales", "ProductKey", "Int64", null, "human");
            await e.CreateColumnAsync("table:Sales", "Order Date", "DateTime", null, "human");
            await e.CreateTableAsync("Customer", "human");
            await e.CreateColumnAsync("table:Customer", "CustomerKey", "Int64", null, "human");
            await e.CreateColumnAsync("table:Customer", "Name", "String", null, "human");
            await e.CreateTableAsync("Product", "human");
            await e.CreateColumnAsync("table:Product", "ProductKey", "Int64", null, "human");
            await e.CreateColumnAsync("table:Product", "ProductName", "String", null, "human");
            await e.CreateRelationshipAsync("column:Sales/CustomerKey", "column:Customer/CustomerKey", null, true, "human");
            await e.CreateRelationshipAsync("column:Sales/ProductKey", "column:Product/ProductKey", null, true, "human");
            await e.SetColumnMetadataAsync("column:Sales/ProductKey", null, "Sum", null, null, "human");
            await e.CreateMeasureAsync("table:Sales", "Total Sales", "SUM ( Sales[Amount] )", "human");
            await e.CreateMeasureAsync("table:Sales", "Order Count", "COUNTROWS ( Sales )", "human");
            await f.Sessions.Require().MutateAsync("human", "setup: the customer key is the table's key, the date has no format", m =>
            {
                m.Tables["Customer"].Columns["CustomerKey"].IsKey = true;
                m.Tables["Sales"].Columns["Order Date"].FormatString = "";
            });
        }

        private static Task<SetResult> WaiveAsync(DecisionFixture f, string door, string system, string ruleId, string objRef, string reason) => door == "studio"
            ? new EngineRpcTarget(f.Engine).waiveFinding(system, ruleId, objRef, reason)
            : McpTools.WaiveFinding(f.Engine, system, ruleId, objRef, reason);

        private static Task<ChangePlanView> ProposeAsync(DecisionFixture f, string door) => door == "studio"
            ? new EngineRpcTarget(f.Engine).proposePlan()
            : McpTools.ProposePlan(f.Engine);

        private static Task<ApplyPlanReport> ApplyAsync(DecisionFixture f, string door) => door == "studio"
            ? new EngineRpcTarget(f.Engine).applyPlan()
            : McpTools.ApplyPlan(f.Engine);

        private static Task<T> Read<T>(DecisionFixture f, Func<Model, T> read) => f.Sessions.Require().ReadAsync(read);

        private static bool Writes(ChangeItem i, string objRef, string property) =>
            i.ObjectRef == objRef && string.Equals(i.Target, property, StringComparison.OrdinalIgnoreCase);

        private static Column Col(Model m, string objRef)
        {
            var body = objRef.Substring("column:".Length);
            var slash = body.IndexOf('/');
            return m.Tables[body.Substring(0, slash)].Columns[body.Substring(slash + 1)];
        }

        /// <summary>The value of the property the declined change writes, read the same way before and after.</summary>
        private static string Value(Column c, string property) => property switch
        {
            "isHidden" => c.IsHidden.ToString(),
            "formatString" => c.FormatString ?? "",
            "summarizeBy" => c.SummarizeBy == AggregateFunction.None ? "None" : "sums",
            _ => throw new ArgumentException(property),
        };

        /// <summary>The person accepts a readiness finding whose fix is a Proposal, before the plan is made: "Customer number is
        /// a business code people search by" (VIS-KEY, the Model Plan's "hide it"), "Order Date keeps the report's regional date
        /// format" (FMT-COLUMN, "set its format"), "reports rely on the implicit sum of Amount" (DAC-IMPLICIT-MEASURE, whose
        /// message asks for SummarizeBy=None). Each asks for the same property on the same object as a Model quality rule with
        /// an auto-fix, and the plan seeds that twin pre-approved, with no note, because the declined set only reads mapped
        /// fixes. Applying the plan makes the change the person declined.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("studio", "VIS-KEY", "column:Customer/CustomerKey", "isHidden")]
        [InlineData("studio", "DAC-IMPLICIT-MEASURE", "column:Sales/Amount", "summarizeBy")]
        public async Task Verify_an_accepted_readiness_proposal_finding_is_not_carried_out_by_its_model_quality_twin(
            string door, string ruleId, string objRef, string property)
        {
            using var f = await StarAsync();
            var card = await f.Engine.AiReadinessScanAsync();
            Assert.Contains(card.Findings, x => x.RuleId == ruleId && x.ObjectRef == objRef && x.Fix == "Proposal");   // not vacuous: it fires
            var before = await Read(f, m => Value(Col(m, objRef), property));

            await WaiveAsync(f, door, "air", ruleId, objRef, "Declined on purpose; the reports depend on it as it is.");
            var plan = await ProposeAsync(f, door);

            var twin = plan.Items.Where(i => Writes(i, objRef, property) && !string.IsNullOrEmpty(i.RuleId)).ToArray();
            Assert.NotEmpty(twin);   // not vacuous: a seeded line makes this change
            await ApplyAsync(f, door);
            Assert.Equal(before, await Read(f, m => Value(Col(m, objRef), property)));
        }

        /// <summary>The same acceptance, then Model quality's Fix all (apply_safe_fixes with system bpa). Fix all skips a change
        /// a mapped finding declined, and runs HIDE_FOREIGN_KEYS and DATECOLUMN_FORMATSTRING here anyway.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("studio", "VIS-KEY", "column:Customer/CustomerKey", "isHidden")]
        public async Task Verify_fix_all_leaves_a_change_a_readiness_proposal_finding_declined(string door, string ruleId, string objRef, string property)
        {
            using var f = await StarAsync();
            var scan = await f.Engine.BpaScanAsync();
            Assert.Contains(scan.Violations, v => v.ObjectRef == objRef && v.CanAutoFix && !v.Waived
                                                  && (v.RuleId == "HIDE_FOREIGN_KEYS" || v.RuleId == "DATECOLUMN_FORMATSTRING"));   // not vacuous
            var before = await Read(f, m => Value(Col(m, objRef), property));
            await WaiveAsync(f, door, "air", ruleId, objRef, "Declined on purpose; the reports depend on it as it is.");

            if (door == "studio") await new EngineRpcTarget(f.Engine).bpaFixAll();
            else await McpTools.ApplySafeFixes(f.Engine, "bpa");

            Assert.Equal(before, await Read(f, m => Value(Col(m, objRef), property)));
        }

        /// <summary>The person accepts "Do not summarize numeric columns" on Sales[ProductKey] ("summed on purpose in the stock
        /// report"), the round 4 case. While the key is visible the readiness twin's line is left out. Run the plan first runs
        /// the Visibility row, which hides the key (nobody declined that); the accepted finding then no longer fires (the rule
        /// only looks at visible columns), so it declines nothing, the Formatting row grows by one, and running it stops the key
        /// summing. One Run the plan, on Pro.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Verify_run_the_plan_does_not_unsum_a_key_whose_do_not_summarize_finding_was_accepted()
        {
            using var f = await StarAsync("Round5Run.bim");
            await WaiveAsync(f, "studio", "bpa", "NUMERIC_COLUMN_SUMMARIZE_BY", "column:Sales/ProductKey", "Product key is summed on purpose in the stock report.");
            var card = await f.Engine.AiReadinessScanAsync();
            Assert.Contains(card.Findings, x => x.RuleId == "FMT-SUMMARIZE" && x.ObjectRef == "column:Sales/ProductKey");   // not vacuous: the twin fires

            var ran = new List<string>();
            for (var press = 0; press < 6; press++)
            {
                var plan = await f.Engine.GetModelPlanAsync();
                foreach (var r in plan.Items.Where(i => i.Doer == PlanDoers.Semanticus && i.ChangeSet?.Stale == true))
                    await f.Engine.ModelPlanActionAsync(r.Id, "propose", "human");
                plan = await f.Engine.GetModelPlanAsync();
                if (!plan.Items.Any(r => r.Doer == PlanDoers.Semanticus && r.StateWord == PlanStateWords.Ready && !r.Held)) break;
                var result = await McpTools.ApplyPlanItems(f.Engine, null, plan.ReadyDigest);
                if (!result.Performed) break;
                ran.AddRange(result.ItemIds ?? Array.Empty<string>());
            }
            Assert.Contains("safefix/Formatting", ran);   // not vacuous: the row that owns the key's SummarizeBy did run
            Assert.Equal(AggregateFunction.Sum, await Read(f, m => m.Tables["Sales"].Columns["ProductKey"].SummarizeBy));
        }

        /// <summary>The same acceptance and apply_safe_fixes pressed twice. The first press hides the key and leaves it summing;
        /// the second press finds the acceptance no longer fires and sets SummarizeBy to None.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("studio")]
        [InlineData("mcp")]
        public async Task Verify_a_second_safe_fix_press_does_not_unsum_a_key_whose_do_not_summarize_finding_was_accepted(string door)
        {
            using var f = await StarAsync();
            await WaiveAsync(f, door, "bpa", "NUMERIC_COLUMN_SUMMARIZE_BY", "column:Sales/ProductKey", "Product key is summed on purpose in the stock report.");
            async Task PressAsync() { if (door == "studio") await new EngineRpcTarget(f.Engine).applySafeFixes(); else await McpTools.ApplySafeFixes(f.Engine, "air"); }

            await PressAsync();
            Assert.Equal((true, AggregateFunction.Sum), await Read(f, m => (m.Tables["Sales"].Columns["ProductKey"].IsHidden, m.Tables["Sales"].Columns["ProductKey"].SummarizeBy)));   // the first press keeps the promise
            await PressAsync();
            Assert.Equal(AggregateFunction.Sum, await Read(f, m => m.Tables["Sales"].Columns["ProductKey"].SummarizeBy));
        }

        /// <summary>The default-tier witness of the theory above (lean suite): one row, the hardest door. The full form runs in the sweep.</summary>
        [Fact]
        public Task Verify_a_second_safe_fix_press_does_not_unsum_a_key_whose_do_not_summarize_finding_was_accepted_sample() => Verify_a_second_safe_fix_press_does_not_unsum_a_key_whose_do_not_summarize_finding_was_accepted("mcp");

        /// <summary>The same acceptance through the change plan: the first plan leaves the readiness twin out and its apply hides
        /// the key; the next plan seeds FMT-SUMMARIZE pre-approved with no note, and applying it unsums the key.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("studio")]
        [InlineData("mcp")]
        public async Task Verify_the_next_plan_after_an_apply_does_not_unsum_a_key_whose_do_not_summarize_finding_was_accepted(string door)
        {
            using var f = await StarAsync();
            await WaiveAsync(f, door, "bpa", "NUMERIC_COLUMN_SUMMARIZE_BY", "column:Sales/ProductKey", "Product key is summed on purpose in the stock report.");
            var first = await ProposeAsync(f, door);
            Assert.Contains(first.Items, i => Writes(i, "column:Sales/ProductKey", "summarizeBy") && i.Status == "skipped" && i.Note == Declined);   // the first plan keeps the promise
            await ApplyAsync(f, door);
            Assert.True(await Read(f, m => m.Tables["Sales"].Columns["ProductKey"].IsHidden));

            await ProposeAsync(f, door);
            await ApplyAsync(f, door);

            Assert.Equal(AggregateFunction.Sum, await Read(f, m => m.Tables["Sales"].Columns["ProductKey"].SummarizeBy));
        }

        /// <summary>make_model_ai_ready's queue is the assistant's work list ("waived ones left out"). After the person accepts
        /// INTEGER_FORMATTING on Order Count ("left unformatted on purpose") it still queues FMT-MEASURE, "choose a format", on
        /// that measure; after NAME-COLUMN on Product[ProductName] ("reports bind to this name") it still queues DIM-PRIMARY-NAME,
        /// "rename the label column to 'Product'". The change plan leaves both changes out (born left out with the note).</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("studio", "bpa", "INTEGER_FORMATTING", "measure:Sales/Order Count", "formatString", "FMT-MEASURE")]
        public async Task Verify_make_ai_ready_does_not_queue_a_change_another_finding_declined(
            string door, string system, string ruleId, string objRef, string property, string twinRule)
        {
            using var f = await StarAsync();
            await WaiveAsync(f, door, system, ruleId, objRef, "Declined on purpose; reports depend on it as it is.");
            var plan = await ProposeAsync(f, door);
            Assert.Contains(plan.Items, i => Writes(i, objRef, property) && i.RuleId == twinRule && i.Status == "skipped" && i.Note == Declined);   // the plan honours it

            var ready = door == "studio" ? await new EngineRpcTarget(f.Engine).makeAiReady() : await McpTools.MakeModelAiReady(f.Engine);

            Assert.DoesNotContain(ready.AiQueue, w => w.Finding.ObjectRef == objRef && w.Finding.RuleId == twinRule);
        }
    }
}
