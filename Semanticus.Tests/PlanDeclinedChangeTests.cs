using System;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using TabularEditor.TOMWrapper;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>m11/rule-clashes round 5. A waiver declines a CHANGE, not a rule id. The change a finding asks for is
    /// (object, property); when the person accepts a finding, every seeded plan line that writes that same property on
    /// that same object is left out, whichever rule seeded it (a readiness rule and its Model quality twin), and so is
    /// the same change on every other bulk path: seeding, Run the plan, apply_safe_fixes and BPA Fix all. The pairing
    /// is read off what each fix writes, so a pair nobody listed is covered too. A line added by hand still applies,
    /// and unwaive and undo bring the lines back. The round 4 verifier's own cases (VerifyRuleClashesRound4Tests) prove
    /// the foreign-key and SummarizeBy pairs on both doors; these cover the other pairs and the other paths.</summary>
    public sealed class PlanDeclinedChangeTests
    {
        private const string Left = "Left out because you accepted this finding after it was planned.";
        private const string Declined = "Left out because you accepted this change under another rule.";
        private const string Why = "Declined on purpose.";

        /// <summary>Sales with two visible foreign keys (ProductKey sums), and an unformatted whole-number measure.</summary>
        private static async Task<DecisionFixture> StarAsync()
        {
            var f = new DecisionFixture(TestEntitlements.Pro);
            var e = f.Engine;
            await e.CreateModelAsync("Declined", 1604);
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
            await e.CreateMeasureAsync("table:Sales", "Order Count", "COUNTROWS ( Sales )", "human");
            return f;
        }

        private static Task<SetResult> WaiveAsync(DecisionFixture f, string door, string system, string ruleId, string objRef) => door == "studio"
            ? new EngineRpcTarget(f.Engine).waiveFinding(system, ruleId, objRef, Why)
            : McpTools.WaiveFinding(f.Engine, system, ruleId, objRef, Why);

        private static Task<ApplyPlanReport> ApplyAsync(DecisionFixture f, string door) => door == "studio"
            ? new EngineRpcTarget(f.Engine).applyPlan()
            : McpTools.ApplyPlan(f.Engine);

        private static bool Writes(ChangeItem i, string objRef, string property) =>
            i.ObjectRef == objRef && string.Equals(i.Target, property, StringComparison.OrdinalIgnoreCase);

        private static bool Seeded(ChangeItem i, string objRef, string property) => Writes(i, objRef, property) && !string.IsNullOrEmpty(i.RuleId);

        private static Task<T> Read<T>(DecisionFixture f, Func<Model, T> read) => f.Sessions.Require().ReadAsync(read);

        private static Task<(bool Hidden, AggregateFunction Sum, string Format)> StateAsync(DecisionFixture f) =>
            Read(f, m => (m.Tables["Sales"].Columns["CustomerKey"].IsHidden, m.Tables["Sales"].Columns["ProductKey"].SummarizeBy, m.Tables["Sales"].Measures["Order Count"].FormatString));

        /// <summary>Each pair is accepted AFTER the plan, under EITHER rule of the pair, and no line that writes that
        /// property on that object stays ticked, whichever rule seeded it. Nothing is written. FMT-MEASURE (an AI-content
        /// finding) and INTEGER_FORMATTING (a Model quality one) are a pair nobody listed as twins.</summary>
        [Theory]
        [InlineData("studio", "bpa", "NUMERIC_COLUMN_SUMMARIZE_BY", "column:Sales/ProductKey", "summarizeBy")]
        [InlineData("mcp", "air", "FMT-SUMMARIZE", "column:Sales/ProductKey", "summarizeBy")]
        [InlineData("studio", "air", "VIS-FK", "column:Sales/CustomerKey", "isHidden")]
        [InlineData("mcp", "bpa", "HIDE_FOREIGN_KEYS", "column:Sales/CustomerKey", "isHidden")]
        [InlineData("studio", "air", "FMT-MEASURE", "measure:Sales/Order Count", "formatString")]
        [InlineData("mcp", "bpa", "INTEGER_FORMATTING", "measure:Sales/Order Count", "formatString")]
        public async Task A_finding_accepted_after_the_plan_leaves_out_every_line_that_writes_the_same_property(
            string door, string system, string ruleId, string objRef, string property)
        {
            using var f = await StarAsync();
            var plan = await f.Engine.ProposePlanAsync(null, true, 40, "human");
            Assert.NotEmpty(plan.Items.Where(i => Seeded(i, objRef, property)));   // not vacuous: the plan does make this change
            var before = await StateAsync(f);

            await WaiveAsync(f, door, system, ruleId, objRef);

            // The note names the acceptance that left the line out (round 6, L1): this finding's own, or another rule's.
            var lines = (await f.Engine.GetPlanAsync()).Items.Where(i => Seeded(i, objRef, property)).ToArray();
            // A line that carries this finding's ask (a merged line: Model quality's format and the readiness FMT-MEASURE ask the same change
            // of one measure, and the plan keeps both asks on the one line) says "you accepted this finding".
            Assert.All(lines, i => Assert.Equal(("skipped", i.RuleId == ruleId || (i.Asks?.Any(a => a.RuleId == ruleId) ?? false) ? Left : Declined), (i.Status, i.Note)));

            await ApplyAsync(f, door);
            var now = await StateAsync(f);
            if (property == "isHidden") Assert.Equal(before.Hidden, now.Hidden);
            if (property == "summarizeBy") Assert.Equal(before.Sum, now.Sum);
            if (property == "formatString") Assert.Equal(before.Format, now.Format);
        }

        /// <summary>Before the plan: the line that stands for a change another rule's accepted finding declined is seeded
        /// left out, not ticked, in either direction, for a pair the verifier did not list. It is a line the person can see
        /// and can bring back: unwaiving the finding puts it back as it would have been seeded.</summary>
        [Theory]
        [InlineData("air", "FMT-MEASURE")]
        [InlineData("bpa", "INTEGER_FORMATTING")]
        public async Task A_finding_accepted_before_the_plan_leaves_its_twin_seeded_left_out(string system, string ruleId)
        {
            using var f = await StarAsync();
            await WaiveAsync(f, "studio", system, ruleId, "measure:Sales/Order Count");

            var plan = await f.Engine.ProposePlanAsync(null, true, 40, "human");

            var twin = plan.Items.Where(i => Seeded(i, "measure:Sales/Order Count", "formatString")).ToArray();
            Assert.NotEmpty(twin);   // not vacuous: the other rule's line is there to see
            Assert.All(twin, i => Assert.Equal(("skipped", Declined), (i.Status, i.Note)));
            Assert.DoesNotContain(twin, i => i.RuleId == ruleId);   // the waived finding itself is not seeded

            var before = (await StateAsync(f)).Format;
            await McpTools.ApplyPlan(f.Engine);
            Assert.Equal(before, (await StateAsync(f)).Format);

            await new EngineRpcTarget(f.Engine).unwaiveFinding(system, ruleId, "measure:Sales/Order Count");
            Assert.All((await f.Engine.GetPlanAsync()).Items.Where(i => Seeded(i, "measure:Sales/Order Count", "formatString")),
                i => Assert.Equal(("approved", (string)null), (i.Status == "needs_content" ? "approved" : i.Status, i.Note)));
        }

        /// <summary>The left-out state is a mark, not a deletion: unwaive brings the line back as it was, and so does
        /// undoing the waiver; redo leaves it out again. The twin's line is the one that comes back.</summary>
        [Fact]
        public async Task Unwaive_and_undo_bring_a_twins_line_back()
        {
            using var f = await StarAsync();
            await f.Engine.ProposePlanAsync(null, true, 0, "human");
            async Task<ChangeItem> Line() => (await f.Engine.GetPlanAsync()).Items.Single(i => Writes(i, "column:Sales/CustomerKey", "isHidden"));
            Assert.Equal("approved", (await Line()).Status);

            await f.Engine.WaiveFindingAsync("bpa", "HIDE_FOREIGN_KEYS", "column:Sales/CustomerKey", Why, "human");
            var line = await Line();
            Assert.Equal(("skipped", Declined), (line.Status, line.Note));   // the line is VIS-FK's; the acceptance is another rule's

            await new EngineRpcTarget(f.Engine).unwaiveFinding("bpa", "HIDE_FOREIGN_KEYS", "column:Sales/CustomerKey");
            line = await Line();
            Assert.Equal(("approved", (string)null), (line.Status, line.Note));

            await f.Engine.WaiveFindingAsync("bpa", "HIDE_FOREIGN_KEYS", "column:Sales/CustomerKey", Why, "human");
            await f.Engine.UndoAsync("human");
            line = await Line();
            Assert.Equal(("approved", (string)null), (line.Status, line.Note));
            await f.Engine.RedoAsync("human");
            line = await Line();
            Assert.Equal(("skipped", Declined), (line.Status, line.Note));
        }

        /// <summary>A line added by hand carries no rule id and is an explicit request: it still applies although the
        /// change is one a finding declined.</summary>
        [Fact]
        public async Task A_line_added_by_hand_for_a_declined_change_still_applies()
        {
            using var f = await StarAsync();
            await f.Engine.WaiveFindingAsync("bpa", "HIDE_FOREIGN_KEYS", "column:Sales/CustomerKey", Why, "human");
            await f.Engine.WaiveFindingAsync("air", "VIS-FK", "column:Sales/CustomerKey", Why, "human");
            var added = await McpTools.AddPlanItem(f.Engine, "column:Sales/CustomerKey", "set_column_hidden", "true");
            var line = added.Items.Single(i => i.ObjectRef == "column:Sales/CustomerKey" && i.Kind == "set_column_hidden");
            Assert.Equal("approved", line.Status);

            var report = await McpTools.ApplyPlan(f.Engine);

            Assert.Equal("applied", report.Items.Single(i => i.Id == line.Id).Status);
            Assert.True(await Read(f, m => m.Tables["Sales"].Columns["CustomerKey"].IsHidden));
        }

        /// <summary>apply_safe_fixes (both doors reach the one method) hides and unsums only what no accepted finding
        /// declined, whichever system the acceptance was made in.</summary>
        [Fact]
        public async Task Apply_safe_fixes_leaves_a_key_whose_model_quality_finding_was_accepted()
        {
            using var f = await StarAsync();
            await f.Engine.WaiveFindingAsync("bpa", "HIDE_FOREIGN_KEYS", "column:Sales/CustomerKey", Why, "human");
            await f.Engine.WaiveFindingAsync("bpa", "NUMERIC_COLUMN_SUMMARIZE_BY", "column:Sales/ProductKey", Why, "human");

            await f.Engine.ApplySafeFixesAsync("human");

            var (hidden, sum) = await Read(f, m => (m.Tables["Sales"].Columns["CustomerKey"].IsHidden, m.Tables["Sales"].Columns["ProductKey"].SummarizeBy));
            Assert.False(hidden);
            Assert.Equal(AggregateFunction.Sum, sum);
            Assert.True(await Read(f, m => m.Tables["Sales"].Columns["ProductKey"].IsHidden));   // what nobody declined is still fixed
        }

        [Fact]
        public async Task Bpa_fix_all_leaves_a_key_whose_readiness_finding_was_accepted()
        {
            using var f = await StarAsync();
            await f.Engine.WaiveFindingAsync("air", "VIS-FK", "column:Sales/CustomerKey", Why, "human");
            await f.Engine.WaiveFindingAsync("air", "FMT-SUMMARIZE", "column:Sales/ProductKey", Why, "human");
            var scan = await f.Engine.BpaScanAsync();
            // not vacuous: the Model quality finding is still auto-fixable, and reads as accepted because of the readiness acceptance
            Assert.Contains(scan.Violations, v => v.RuleId == "HIDE_FOREIGN_KEYS" && v.ObjectRef == "column:Sales/CustomerKey" && v.CanAutoFix && v.Waived && v.WaivedAsTwin);

            var result = await f.Engine.BpaFixAllAsync("human");

            var (hidden, sum) = await Read(f, m => (m.Tables["Sales"].Columns["CustomerKey"].IsHidden, m.Tables["Sales"].Columns["ProductKey"].SummarizeBy));
            Assert.False(hidden);
            Assert.Equal(AggregateFunction.Sum, sum);
            Assert.True(await Read(f, m => m.Tables["Sales"].Columns["ProductKey"].IsHidden));   // what nobody declined is still fixed
            // A finding left alone because its change was declined is still open and auto-fixable, so Fix all lists it with the reason
            // (round 6, L2) and never claims that nothing remains.
            Assert.Contains(result.Unfixed, u => u.ObjectRef == "column:Sales/CustomerKey" && u.RuleId == "HIDE_FOREIGN_KEYS" && u.Reason.Contains("related finding"));
            Assert.DoesNotContain(result.Unfixed, u => u.ObjectRef == "column:Sales/ProductKey");   // hidden by the press, so the visible-only finding stopped firing: nothing to list
            Assert.True(result.Remaining >= 1);
        }

        /// <summary>Run the plan's safe-fix row is worked out from the readiness findings. With every foreign key's
        /// Model quality finding accepted, that row has no line left to run, so it is not offered at all (it would
        /// otherwise stay ready and do nothing on every press).</summary>
        [Fact]
        public async Task The_plans_safe_fix_row_is_not_offered_for_a_change_every_finding_declined()
        {
            using var f = await StarAsync();
            Assert.Contains((await f.Engine.GetModelPlanAsync()).Items, i => i.Id == "safefix/Visibility");   // not vacuous

            await f.Engine.WaiveFindingAsync("bpa", "HIDE_FOREIGN_KEYS", "column:Sales/CustomerKey", Why, "human");
            await f.Engine.WaiveFindingAsync("bpa", "HIDE_FOREIGN_KEYS", "column:Sales/ProductKey", Why, "human");

            Assert.DoesNotContain((await f.Engine.GetModelPlanAsync()).Items, i => i.Id == "safefix/Visibility");
        }
    }
}
