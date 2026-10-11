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
    /// Verifier finding on m11/feedback-rules at 7c258c85 (round 5, the targeted check of V1 to V4). V2, V3 and V4
    /// hold. V1 does not: the brief for this round says no rule may flag or replace a real date format "on columns
    /// or measures, including INTEGER_FORMATTING", and the measure half was left. These cases fail at that head and
    /// name what a fix must make true without choosing the fix: either stop raising a measure that already carries a
    /// real date format, or refuse to write over one.
    /// </summary>
    public sealed class VerifyFeedbackRulesRound5Tests : IClassFixture<ModelTemplates>
    {
        private readonly ModelTemplates _templates;
        public VerifyFeedbackRulesRound5Tests(ModelTemplates templates) => _templates = templates;

        // Lean suite: the model is built once for the class and each test opens a copy.
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
            await e.CreateMeasureAsync("table:Sales", "Net Sales", "SUM ( Sales[Amount] )", "human");
            await e.CreateMeasureAsync("table:Sales", "Last Order Date", "MAX ( Sales[Amount] )", "human");
        }

        private static Task<long> Mutate(DecisionFixture f, Action<Model> edit) =>
            f.Sessions.Require().MutateAsync("human", "test setup", edit);

        /// <summary>Every shipped rule whose one-click fix writes FormatString, whatever it is scoped at.</summary>
        private static string[] FormatWritingRules() => BpaRuleSet.Standard()
            .Where(r => (r.FixExpression ?? "").Replace(" ", "").StartsWith("FormatString=", StringComparison.OrdinalIgnoreCase))
            .Select(r => r.ID).ToArray();

        // V1, the measure half. MONTHCOLUMN_FORMATSTRING was corrected this round so a column carrying a real date
        // format is left alone. INTEGER_FORMATTING is the same shape one object kind across: it is scoped at Measure,
        // its expression has no data-type guard at all, and its fix writes "#,0". A measure such as Last Order Date
        // formatted "Long Date" is raised by it, and one press replaces the date format with a number format.

        [Theory]
        [Trait(TestCost.Key, TestCost.Sweep)]
        [InlineData("Long Date")]
        [InlineData("Short Date")]
        [InlineData("yyyy-mm-dd")]
        [InlineData("mmm yyyy")]
        [InlineData("dd/mm/yyyy")]
        public async Task Verify_no_rule_flags_a_measure_that_already_has_a_real_date_format(string format)
        {
            using var f = await NorthstarAsync();
            await Mutate(f, m => m.Tables["Sales"].Measures["Last Order Date"].FormatString = format);

            var writers = FormatWritingRules();
            var hits = (await f.Engine.BpaScanAsync()).Violations
                .Where(v => v.ObjectRef == "measure:Sales/Last Order Date" && writers.Contains(v.RuleId))
                .Select(v => v.RuleId).OrderBy(x => x, StringComparer.Ordinal).ToArray();

            Assert.True(hits.Length == 0,
                $"a measure formatted \"{format}\" is flagged by a rule whose fix overwrites the format string: {string.Join(", ", hits)}");
        }

        // Default tier: one row of the sweep form (the witness), through the same body.
        [Fact]
        public Task Verify_a_one_click_fix_never_replaces_a_real_date_format_on_a_measure_sample() =>
            Verify_a_one_click_fix_never_replaces_a_real_date_format_on_a_measure("Long Date");

        [Theory]
        [Trait(TestCost.Key, TestCost.Sweep)]
        [InlineData("Long Date")]
        [InlineData("yyyy-mm-dd")]
        public async Task Verify_a_one_click_fix_never_replaces_a_real_date_format_on_a_measure(string format)
        {
            using var f = await NorthstarAsync();
            await Mutate(f, m => m.Tables["Sales"].Measures["Last Order Date"].FormatString = format);

            foreach (var rule in FormatWritingRules())
            {
                try { await f.Engine.BpaFixAsync(rule, "measure:Sales/Last Order Date", "agent"); }
                catch (InvalidOperationException) { /* refusing the press is one acceptable answer */ }
            }

            Assert.Equal(format,
                await f.Sessions.Require().ReadAsync(m => m.Tables["Sales"].Measures["Last Order Date"].FormatString));
        }
    }
}
