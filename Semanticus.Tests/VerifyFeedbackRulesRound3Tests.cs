using System;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Analysis;
using Semanticus.Engine;
using TabularEditor.TOMWrapper;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// Verifier findings on m11/feedback-rules at e3d6f061 (round 3). Each test fails at that head and names what a
    /// fix must make true, without choosing the fix. Same small Northstar model the lane's own tests use.
    /// </summary>
    public sealed class VerifyFeedbackRulesRound3Tests : IClassFixture<ModelTemplates>
    {
        private readonly ModelTemplates _templates;
        public VerifyFeedbackRulesRound3Tests(ModelTemplates templates) => _templates = templates;

        // Lean suite: build once for the class, open a copy per test.
        private async Task<DecisionFixture> NorthstarAsync()
        {
            var f = new DecisionFixture(TestEntitlements.Pro);
            await _templates.OpenCopyAsync(f, "northstar", BuildNorthstarAsync, "Northstar.bim");
            return f;
        }

        private static async Task BuildNorthstarAsync(DecisionFixture f)
        {
            var e = f.Engine;
            await e.CreateModelAsync("Northstar", 1604);
            await e.CreateTableAsync("Sales", "human");
            await e.CreateColumnAsync("table:Sales", "Amount", "Decimal", "Amount", "human");
            await e.CreateColumnAsync("table:Sales", "CustomerKey", "Int64", "CustomerKey", "human");
            await e.CreateMeasureAsync("table:Sales", "Net Sales", "SUM ( Sales[Amount] )", "human");
            await e.CreateTableAsync("Customer", "human");
            await e.CreateColumnAsync("table:Customer", "CustomerKey", "Int64", "CustomerKey", "human");
            await e.CreateColumnAsync("table:Customer", "City", "String", "City", "human");
            await e.CreateRelationshipAsync("column:Sales/CustomerKey", "column:Customer/CustomerKey", null, true, "human");
            await e.CreateTableAsync("Date", "human");
            await e.CreateColumnAsync("table:Date", "Year", "Int64", "Year", "human");
        }

        private static Task<long> Mutate(DecisionFixture f, Action<Model> edit) =>
            f.Sessions.Require().MutateAsync("human", "test setup", edit);

        private const string MonthRule = "MONTHCOLUMN_FORMATSTRING";
        private const string FlagRule = "FORMAT_FLAG_COLUMNS_AS_YES/NO_VALUE_STRINGS";

        // V1. The date rule was corrected so that any real date format passes and its one-click fix never replaces a
        // format that is already there. Its sibling, the MONTH rule, still flags every format that is not exactly
        // "MMMM yyyy" and its fix overwrites. Seen on the lane's own dogfood model in a real lane: Date/MonthStart
        // carries "yyyy-mm-dd" (the very ISO form the date fix writes) and apply_fix replaced it with "MMMM yyyy".

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("yyyy-mm-dd")]
        [InlineData("Long Date")]
        [InlineData("Short Date")]
        [InlineData("mmm yyyy")]
        public async Task Verify_the_month_rule_does_not_flag_a_column_that_already_has_a_date_format(string format)
        {
            using var f = await NorthstarAsync();
            await f.Engine.CreateColumnAsync("table:Date", "MonthStart", "DateTime", "MonthStart", "human");
            await Mutate(f, m => m.Tables["Date"].Columns["MonthStart"].FormatString = format);

            var hit = (await f.Engine.BpaScanAsync()).Violations
                .FirstOrDefault(v => v.RuleId == MonthRule && v.ObjectRef == "column:Date/MonthStart");

            Assert.True(hit == null, $"the month rule flags a column formatted \"{format}\": {hit?.Message}");
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Verify_the_month_rules_one_click_fix_never_replaces_a_format_that_is_already_there()
        {
            using var f = await NorthstarAsync();
            await f.Engine.CreateColumnAsync("table:Date", "MonthStart", "DateTime", "MonthStart", "human");
            await Mutate(f, m => m.Tables["Date"].Columns["MonthStart"].FormatString = "yyyy-mm-dd");

            try { await f.Engine.BpaFixAsync(MonthRule, "column:Date/MonthStart", "agent"); }
            catch (InvalidOperationException) { /* refusing is one acceptable answer */ }

            Assert.Equal("yyyy-mm-dd",
                await f.Sessions.Require().ReadAsync(m => m.Tables["Date"].Columns["MonthStart"].FormatString));
        }

        // V2. The flag rule's description was rewritten this round. It tells the person a three-part format string
        // clears the rule and that "any other flag passes as a text column". A Boolean flag column is flagged and the
        // remedy the message names cannot clear it, because the clearing clause requires DataType = "Int64".

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Verify_a_flag_column_the_rule_flags_can_be_cleared_by_the_remedy_its_message_names()
        {
            using var f = await NorthstarAsync();
            await f.Engine.CreateColumnAsync("table:Sales", "Return Flag", "Boolean", "ReturnFlag", "human");

            var hit = (await f.Engine.BpaScanAsync()).Violations
                .FirstOrDefault(v => v.RuleId == FlagRule && v.ObjectRef == "column:Sales/Return Flag");
            if (hit == null) return;   // not flagged: nothing to promise

            await Mutate(f, m => m.Tables["Sales"].Columns["Return Flag"].FormatString = "\"Yes\";\"Yes\";\"No\"");
            var still = (await f.Engine.BpaScanAsync()).Violations
                .FirstOrDefault(v => v.RuleId == FlagRule && v.ObjectRef == "column:Sales/Return Flag");

            Assert.True(still == null,
                "the rule flags this column and the format string its own message names does not clear it: " + hit.Message);
        }

        // V3. The sweep's skip reason for a visible attribute now glues the verdict sentence (which already ends with
        // "propose its removal and approve that change") to a second paragraph that says the same thing again.

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Verify_the_sweep_skip_reason_for_a_visible_attribute_gives_the_same_advice_only_once()
        {
            using var f = await NorthstarAsync();

            var named = await f.Engine.RemoveSafeObjectsAsync(new[] { "column:Customer/City" }, null, "agent");
            var reason = named.Skipped.Single(k => k.Ref == "column:Customer/City").Reason;

            var proposals = System.Text.RegularExpressions.Regex.Matches(
                reason, "propose", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Count;
            Assert.True(proposals <= 1, $"the skip reason tells the person to propose the removal {proposals} times: {reason}");
        }

        // V4. The cleanup machinery no longer offers or removes a calculation group's own column on either door. The
        // per-object door still does: delete_object removes the picker, and the group is left with nothing people can
        // filter by. That is the harm the verdict change was made for.

        [Fact]
        public async Task Verify_a_calculation_group_is_never_left_without_a_column_people_can_pick_with()
        {
            using var f = await NorthstarAsync();
            var cg = await f.Engine.CreateCalculationGroupAsync("Time Intelligence", "human");
            await f.Engine.CreateCalculationItemAsync(cg, "Current", "SELECTEDMEASURE ()", "human");
            var pickerRef = await f.Sessions.Require().ReadAsync(m =>
                ObjectRefs.For(((CalculationGroupTable)m.Tables["Time Intelligence"]).Columns.First(c => !c.IsHidden)));

            try { await f.Engine.DeleteObjectAsync(pickerRef, "human"); }
            catch (InvalidOperationException) { /* refusing is one acceptable answer */ }

            var pickable = await f.Sessions.Require().ReadAsync(m =>
                ((CalculationGroupTable)m.Tables["Time Intelligence"]).Columns.Count(c => !c.IsHidden));
            Assert.True(pickable > 0,
                "delete_object removed the calculation group's own column, so the group exists and nothing can filter by it");
        }
    }
}
