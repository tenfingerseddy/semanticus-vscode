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
    /// The verifier's failing cases for m11/rule-clashes round 4 (bd2a5184). The round's promise is that a finding the
    /// person accepts is never carried out by the change plan, whether it was accepted before or after the plan was made.
    /// It holds when the plan line carries the accepted finding's own rule. It does not hold for a change two rules ask
    /// for: a readiness rule and its Model quality (BPA) twin. VIS-FK and HIDE_FOREIGN_KEYS both hide a foreign key;
    /// FMT-SUMMARIZE and NUMERIC_COLUMN_SUMMARIZE_BY both stop a key column summing. The plan keeps one line per object
    /// and property (BuildSeeds' dedup), carrying one rule id, and a waiver is matched on that one rule only
    /// (LocalEngine.PlanWaivers.cs SeedIsWaived, BuildSeeds' !Waived filters, CollectLinesForItem). So accepting the
    /// twin that the line does not carry changes nothing: the plan still makes the change the person declined. The
    /// last case is a low one: a line the assistant fills while its finding is accepted loses the "left out" state.
    /// They are left failing on purpose.
    /// </summary>
    public sealed class VerifyRuleClashesRound4Tests : IClassFixture<ModelTemplates>
    {
        private readonly ModelTemplates _templates;
        public VerifyRuleClashesRound4Tests(ModelTemplates templates) => _templates = templates;

        private const string Left = "Left out because you accepted this finding after it was planned.";
        private const string Shown = "Customer key is shown on purpose; the order report lists it.";
        private const string Summed = "Product key is summed on purpose in the stock report.";

        /// <summary>A small star: both foreign keys on Sales are visible, and Sales[ProductKey] sums by default.</summary>
        private async Task<DecisionFixture> TwinsAsync(string saveAs = null)
        {
            // The build runs once per class; every test opens a copy of the saved model (ModelTemplates, lean suite).
            var f = new DecisionFixture(TestEntitlements.Pro);
            await _templates.OpenCopyAsync(f, "twins", BuildTwinsAsync, (saveAs ?? "twins") + ((saveAs ?? "twins").EndsWith(".bim", StringComparison.Ordinal) ? "" : ".bim"));
            if (saveAs != null) f.UseNull();
            return f;
        }

        private static async Task BuildTwinsAsync(DecisionFixture f)
        {
            var e = f.Engine;
            await e.CreateModelAsync("Twins", 1604);
            await e.CreateTableAsync("Sales", "human");
            await e.CreateColumnAsync("table:Sales", "Amount", "Double", null, "human");
            await e.CreateColumnAsync("table:Sales", "CustomerKey", "Int64", null, "human");
            await e.CreateColumnAsync("table:Sales", "ProductKey", "Int64", null, "human");
            await e.CreateTableAsync("Customer", "human");
            await e.CreateColumnAsync("table:Customer", "CustomerKey", "Int64", null, "human");
            await e.CreateColumnAsync("table:Customer", "Name", "String", null, "human");
            await e.CreateTableAsync("Product", "human");
            await e.CreateColumnAsync("table:Product", "ProductKey", "Int64", null, "human");
            await e.CreateColumnAsync("table:Product", "Name", "String", null, "human");
            await e.CreateRelationshipAsync("column:Sales/CustomerKey", "column:Customer/CustomerKey", null, true, "human");
            await e.CreateRelationshipAsync("column:Sales/ProductKey", "column:Product/ProductKey", null, true, "human");
            await e.SetColumnMetadataAsync("column:Sales/ProductKey", null, "Sum", null, null, "human");
            await e.CreateMeasureAsync("table:Sales", "Total Sales", "SUM ( Sales[Amount] )", "human");
        }

        private static Task<ChangePlanView> ProposeAsync(DecisionFixture f, string door) => door == "studio"
            ? new EngineRpcTarget(f.Engine).proposePlan()
            : McpTools.ProposePlan(f.Engine);

        private static Task<SetResult> WaiveAsync(DecisionFixture f, string door, string system, string ruleId, string objRef, string reason) => door == "studio"
            ? new EngineRpcTarget(f.Engine).waiveFinding(system, ruleId, objRef, reason)
            : McpTools.WaiveFinding(f.Engine, system, ruleId, objRef, reason);

        private static Task<ApplyPlanReport> ApplyAsync(DecisionFixture f, string door) => door == "studio"
            ? new EngineRpcTarget(f.Engine).applyPlan()
            : McpTools.ApplyPlan(f.Engine);

        private static Task<(bool Hidden, AggregateFunction Summarize)> DeclinedAsync(DecisionFixture f) =>
            f.Sessions.Require().ReadAsync(m => (m.Tables["Sales"].Columns["CustomerKey"].IsHidden, m.Tables["Sales"].Columns["ProductKey"].SummarizeBy));

        private static bool Hides(ChangeItem i) => i.ObjectRef == "column:Sales/CustomerKey" && string.Equals(i.Target, "isHidden", StringComparison.OrdinalIgnoreCase);
        private static bool Unsums(ChangeItem i) => i.ObjectRef == "column:Sales/ProductKey" && string.Equals(i.Target, "summarizeBy", StringComparison.OrdinalIgnoreCase);

        /// <summary>The person accepts two Model quality findings AFTER the plan was made: "Hide foreign keys" on
        /// Sales[CustomerKey] and "Do not summarize numeric columns" on Sales[ProductKey], each with a reason. The plan
        /// holds one line for each change, filed under the readiness twin (VIS-FK, FMT-SUMMARIZE), so neither is left out,
        /// the Studio keeps both ticked, and applying the plan hides the key and stops it summing. Measured on both doors,
        /// and on one session across them over the wire (the assistant proposes and applies, the person accepts in Studio).</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("studio")]
        [InlineData("mcp")]
        public async Task Verify_a_model_quality_finding_accepted_after_the_plan_is_not_carried_out_under_its_readiness_twin(string door)
        {
            using var f = await TwinsAsync();
            var plan = await ProposeAsync(f, door);
            Assert.Contains(plan.Items, i => Hides(i) && i.Status == "approved");     // not vacuous: the plan makes both changes
            Assert.Contains(plan.Items, i => Unsums(i) && i.Status == "approved");
            var bpa = await f.Engine.BpaScanAsync();                                   // and they are real Model quality findings
            Assert.Contains(bpa.Violations, v => v.RuleId == "HIDE_FOREIGN_KEYS" && v.ObjectRef == "column:Sales/CustomerKey" && v.CanAutoFix);
            Assert.Contains(bpa.Violations, v => v.RuleId == "NUMERIC_COLUMN_SUMMARIZE_BY" && v.ObjectRef == "column:Sales/ProductKey" && v.CanAutoFix);

            await WaiveAsync(f, door, "bpa", "HIDE_FOREIGN_KEYS", "column:Sales/CustomerKey", Shown);
            await WaiveAsync(f, door, "bpa", "NUMERIC_COLUMN_SUMMARIZE_BY", "column:Sales/ProductKey", Summed);
            await ApplyAsync(f, door);

            Assert.Equal((false, AggregateFunction.Sum), await DeclinedAsync(f));
        }

        /// <summary>The other way round, and before the plan: the person accepts the READINESS findings (VIS-FK on
        /// Sales[CustomerKey], FMT-SUMMARIZE on Sales[ProductKey]) on the AI readiness page, then presses "Review these
        /// together". BuildSeeds skips the waived readiness findings, and the Model quality twins it skipped them for are
        /// seeded instead, pre-approved, so applying the plan makes the same two declined changes.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("studio")]
        [InlineData("mcp")]
        public async Task Verify_a_readiness_finding_accepted_first_is_not_carried_out_by_its_model_quality_twin(string door)
        {
            using var f = await TwinsAsync();
            await WaiveAsync(f, door, "air", "VIS-FK", "column:Sales/CustomerKey", Shown);
            await WaiveAsync(f, door, "air", "FMT-SUMMARIZE", "column:Sales/ProductKey", Summed);

            var plan = await ProposeAsync(f, door);
            Assert.DoesNotContain(plan.Items, i => i.RuleId == "VIS-FK" && Hides(i) || i.RuleId == "FMT-SUMMARIZE" && Unsums(i));   // the waived findings are not seeded
            Assert.Contains(plan.Items, Hides);                                                              // not vacuous: the changes still are
            Assert.Contains(plan.Items, Unsums);
            await ApplyAsync(f, door);

            Assert.Equal((false, AggregateFunction.Sum), await DeclinedAsync(f));
        }

        /// <summary>Run the plan (the guarded path) on a saved model: the person accepted "Hide foreign keys" on
        /// Sales[CustomerKey] in Model quality. The Plan's "Fix 2 safe things in visibility" row works its lines out from
        /// the readiness findings (CollectLinesForItem skips air waivers only) and hides the key anyway.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Verify_run_the_plan_does_not_hide_a_foreign_key_whose_model_quality_finding_was_accepted()
        {
            using var f = await TwinsAsync("Twins.bim");
            await WaiveAsync(f, "studio", "bpa", "HIDE_FOREIGN_KEYS", "column:Sales/CustomerKey", Shown);
            var ran = new List<string>();
            for (var press = 0; press < 4; press++)
            {
                var plan = await f.Engine.GetModelPlanAsync();
                if (!plan.Items.Any(r => r.Doer == PlanDoers.Semanticus && r.StateWord == PlanStateWords.Ready && !r.Held)) break;
                var result = await f.Engine.ApplyPlanItemsAsync(null, "human", plan.ReadyDigest);
                ran.AddRange(result.ItemIds ?? Array.Empty<string>());
            }
            Assert.Contains("safefix/Visibility", ran);   // not vacuous: the visibility row ran

            Assert.False(await f.Sessions.Require().ReadAsync(m => m.Tables["Sales"].Columns["CustomerKey"].IsHidden));
        }

        /// <summary>LOW. The assistant fills a line from the ids it read before the person accepted its finding
        /// (set_plan_item on an id from an earlier get_plan). SetPlanItemAsync clears the note, and for an opt-in kind moves
        /// the line to "proposed", so while the waiver still holds the line no longer reads as left out (the rename shows
        /// as a normal row to review), and once the waiver goes the description line stays "skipped" with no reason, because
        /// MarkWaivedSeeds only restores a line whose note is the left-out note. Apply still leaves both out (checked
        /// inside the write), so nothing declined is written.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("set_description")]
        [InlineData("rename")]
        public async Task Verify_a_line_filled_while_its_finding_is_accepted_stays_left_out_and_comes_back_when_the_waiver_goes(string kind)
        {
            using var f = await TwinsAsync();
            await f.Engine.ProposePlanAsync(null, true, 40, "human");
            var rule = kind == "rename" ? "NAME-COLUMN" : "DESC-TABLE";
            var obj = kind == "rename" ? "column:Customer/CustomerKey" : "table:Customer";
            var id = (await f.Engine.GetPlanAsync()).Items.Single(i => i.RuleId == rule && i.ObjectRef == obj).Id;
            await WaiveAsync(f, "studio", "air", rule, obj, "Declined on purpose.");

            await McpTools.SetPlanItem(f.Engine, id, kind == "rename" ? "Customer ID" : "Every customer we invoice.");
            var whileWaived = (await f.Engine.GetPlanAsync()).Items.Single(i => i.Id == id);
            Assert.Equal(("skipped", Left), (whileWaived.Status, whileWaived.Note));

            await new EngineRpcTarget(f.Engine).unwaiveFinding("air", rule, obj);
            var back = (await f.Engine.GetPlanAsync()).Items.Single(i => i.Id == id);
            Assert.Contains(back.Status, new[] { "approved", "proposed" });
        }

        /// <summary>The default-tier witness of the theory above (lean suite): one row, the hardest door. The full form runs in the sweep.</summary>
        [Fact]
        public Task Verify_a_line_filled_while_its_finding_is_accepted_stays_left_out_and_comes_back_when_the_waiver_goes_sample() => Verify_a_line_filled_while_its_finding_is_accepted_stays_left_out_and_comes_back_when_the_waiver_goes("rename");
    }
}
