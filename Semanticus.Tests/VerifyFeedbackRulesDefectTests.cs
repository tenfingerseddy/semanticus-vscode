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
    /// Verifier findings on m11/feedback-rules (864a4454). Each test fails at that head and names what a fix must make
    /// true, without choosing the fix. Built on the same small Northstar model as DogfoodFeedbackRulesTests.
    /// </summary>
    public sealed class VerifyFeedbackRulesDefectTests : IClassFixture<ModelTemplates>
    {
        private readonly ModelTemplates _templates;
        public VerifyFeedbackRulesDefectTests(ModelTemplates templates) => _templates = templates;

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

        private const string FlagRule = "FORMAT_FLAG_COLUMNS_AS_YES/NO_VALUE_STRINGS";

        // With two sections the first applies to positive values AND zeros (DAX FORMAT, MDX FORMAT_STRING, VBA Format),
        // so "Yes";"No" shows a 0 flag as Yes. The inverted three-section form shows 1 as No and 0 as Yes. Neither is
        // a Yes/No flag format, yet the new clause (the string merely contains "Yes" and "No") clears the rule for both.
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("\"Yes\";\"No\"")]
        [InlineData("\"No\";\"No\";\"Yes\"")]
        public async Task Verify_a_yes_no_format_that_shows_a_zero_flag_as_yes_does_not_clear_the_flag_rule(string format)
        {
            using var f = await NorthstarAsync();
            await f.Engine.CreateColumnAsync("table:Date", "Is Weekend", "Int64", "IsWeekend", "human");
            await Mutate(f, m => m.Tables["Date"].Columns["Is Weekend"].FormatString = format);

            var v = (await f.Engine.BpaScanAsync()).Violations;

            Assert.Contains(v, x => x.RuleId == FlagRule && x.ObjectRef == "column:Date/Is Weekend");
        }

        // Default tier: the witness row of the sweep form, through the same body.
        [Fact]
        public Task Verify_a_yes_no_format_that_shows_a_zero_flag_as_yes_does_not_clear_the_flag_rule_sample() =>
            Verify_a_yes_no_format_that_shows_a_zero_flag_as_yes_does_not_clear_the_flag_rule("\"No\";\"No\";\"Yes\"");

        // The rewritten description says a date column "should have a date format string". A column that has one (a
        // regional Long Date, which Microsoft recommends for reports read in several countries) is still flagged, and
        // the rule's one-click fix replaces it with yyyy-mm-dd. The finding tells a person something false about their
        // column. Either the rule accepts the formats its description promises, or the description names what passes.
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("Long Date")]
        [InlineData("Short Date")]
        [InlineData("dd/mm/yyyy")]
        public async Task Verify_the_date_rule_only_promises_what_it_checks(string format)
        {
            using var f = await NorthstarAsync();
            await f.Engine.CreateColumnAsync("table:Sales", "Order Date", "DateTime", "OrderDate", "human");
            await Mutate(f, m => m.Tables["Sales"].Columns["Order Date"].FormatString = format);

            var hit = (await f.Engine.BpaScanAsync()).Violations
                .FirstOrDefault(x => x.RuleId == "DATECOLUMN_FORMATSTRING" && x.ObjectRef == "column:Sales/Order Date");

            Assert.False(hit != null && hit.Message.Contains("should have a date format string", StringComparison.Ordinal),
                $"a column formatted '{format}' has a date format string, yet it is flagged with: {hit?.Message}");
        }

        // A person can still ask for a visible attribute to go: the Impact card's Propose removal (a delete_if_unused
        // plan item) and remove_safe_objects with its ref. Both refuse it now, which is the new rule, but the reason
        // they give is false: "the unused verdict no longer holds at apply time" (it never held) and "its safety could
        // not be fully verified" (nothing failed to verify; the engine keeps visible attributes on purpose).
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Verify_a_visible_attribute_a_person_asks_to_remove_is_refused_with_its_real_reason()
        {
            using var f = await NorthstarAsync();
            var e = f.Engine;

            var direct = await e.RemoveSafeObjectsAsync(new[] { "column:Customer/City" }, null, "agent");
            var directReason = direct.Skipped.FirstOrDefault(s => s.Ref == "column:Customer/City")?.Reason ?? "";

            // The Impact card adds the item and the person approves it (the tick in Changes), then applies it.
            var plan = await e.AddPlanItemAsync("column:Customer/City", "delete_if_unused", null, "Remove City", null, null, "human");
            var id = plan.Items.Last(i => i.ObjectRef == "column:Customer/City").Id;
            await e.SetPlanItemAsync(id, null, approved: true, "human");
            var report = await e.ApplyPlanAsync(new[] { id }, "human");
            var item = report.Items.SingleOrDefault(i => i.Id == id);
            Assert.True(item != null, "the approved removal was not part of the apply report");
            var planNote = item.Status == "applied" ? "" : item.Note ?? "";

            foreach (var (door, text) in new[] { ("remove_safe_objects", directReason), ("apply_plan", planNote) })
            {
                if (text.Length == 0) continue;   // removed on request: no reason to judge
                Assert.DoesNotContain("could not be fully verified", text, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("no longer holds", text, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("Visible attribute", text, StringComparison.Ordinal);
            }
        }
    }
}
