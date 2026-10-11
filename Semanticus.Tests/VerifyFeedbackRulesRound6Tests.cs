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
    /// Verifier findings on m11/feedback-rules at 9a4ebcae (the targeted check of V1b). Every STATIC date and time
    /// format tried is now left alone on measures and columns by the two rules the fix changed, on both doors. Three
    /// holes remain in the same principle ("a format rule only touches a format string it can prove is its own"), and
    /// those two rules' new Descriptions promise each one away; a fourth case is the flag rule, which still raises a
    /// real date format on an oddly named column. These cases fail at that head and name what a fix must make true
    /// without choosing the fix.
    /// </summary>
    public sealed class VerifyFeedbackRulesRound6Tests : IClassFixture<ModelTemplates>
    {
        private readonly ModelTemplates _templates;
        public VerifyFeedbackRulesRound6Tests(ModelTemplates templates) => _templates = templates;

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
            await e.CreateMeasureAsync("table:Sales", "Net Sales", "SUM ( Sales[Amount] )", "human");
            await e.CreateMeasureAsync("table:Sales", "Last Order Date", "MAX ( Sales[Amount] )", "human");
            await e.CreateTableAsync("Date", "human");
            await e.CreateColumnAsync("table:Date", "Order Date", "DateTime", "OrderDate", "human");
        }

        private static Task<long> Mutate(DecisionFixture f, Action<Model> edit) =>
            f.Sessions.Require().MutateAsync("human", "test setup", edit);

        /// <summary>Every shipped rule whose one-click fix writes FormatString, whatever it is scoped at.</summary>
        private static string[] FormatWritingRules() => BpaRuleSet.Standard()
            .Where(r => (r.FixExpression ?? "").Replace(" ", "").StartsWith("FormatString=", StringComparison.OrdinalIgnoreCase))
            .Select(r => r.ID).ToArray();

        private const string Ref = "measure:Sales/Last Order Date";

        /// <summary>Press every format-writing fix on the measure (a refusal is an acceptable answer), then Fix all.</summary>
        private static async Task PressEverythingAsync(DecisionFixture f)
        {
            foreach (var rule in FormatWritingRules())
            {
                try { await f.Engine.BpaFixAsync(rule, Ref, "agent"); }
                catch (InvalidOperationException) { /* refusing the press is one acceptable answer */ }
            }
            await f.Engine.BpaFixAllAsync("human");
        }

        private static async Task<string[]> WritersFlaggingAsync(DecisionFixture f) =>
            (await f.Engine.BpaScanAsync()).Violations
                .Where(v => v.ObjectRef == Ref && FormatWritingRules().Contains(v.RuleId))
                .Select(v => v.RuleId).OrderBy(x => x, StringComparer.Ordinal).ToArray();

        // 1. A DYNAMIC date or time format. A measure whose format is a format string expression has an empty static
        // FormatString (the wrapper clears it, and Power BI keeps the two exclusive). INTEGER_FORMATTING's new empty
        // branch reads that as "no format string at all", so it raises a measure that shows as a date, tells the
        // person it is an unformatted whole number, and one press (or Fix all) writes "#,0" BESIDE the dynamic date
        // format. The same file's PROVIDE_FORMAT_STRING_FOR_MEASURES already counts FormatStringExpression as a format,
        // and so does the readiness rule FMT-MEASURE. Either answer satisfies this: stop raising a measure whose format
        // is a format string expression, or refuse to write a static format onto one.

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("\"dd/mm/yyyy\"")]
        [InlineData("\"Long Date\"")]
        [InlineData("IF ( ISINSCOPE ( 'Date'[Order Date] ), \"Long Date\", \"hh:nn\" )")]
        public async Task Verify_no_format_rule_flags_or_writes_beside_a_dynamic_date_format_on_a_measure(string dynamicFormat)
        {
            using var f = await NorthstarAsync();
            await Mutate(f, m => m.Tables["Sales"].Measures["Last Order Date"].FormatStringExpression = dynamicFormat);

            var hits = await WritersFlaggingAsync(f);
            Assert.True(hits.Length == 0,
                $"a measure whose format is the dynamic date format {dynamicFormat} is flagged by a rule whose fix writes a static format string: {string.Join(", ", hits)}");

            await PressEverythingAsync(f);
            var (fs, fse) = await f.Sessions.Require().ReadAsync(m =>
                (m.Tables["Sales"].Measures["Last Order Date"].FormatString, m.Tables["Sales"].Measures["Last Order Date"].FormatStringExpression));
            Assert.True(string.IsNullOrEmpty(fs), $"a static format string \"{fs}\" was written beside the dynamic date format {dynamicFormat}");
            Assert.Equal(dynamicFormat, fse);
        }

        // 2. A CURRENCY format that is not written with a dollar sign. The new Description says "Currency formats are
        // left alone", but the expression only excludes "$". Every other currency symbol passes the "no letter"
        // proof, so a euro, pound, yen or rupee format is raised and one press rewrites it to "#,0": the symbol and
        // the decimals are gone. "€ #,0.00", "£ #,0" and "¥ #,0" are the forms Microsoft's own dynamic format string
        // article uses. Answer: leave a currency format alone whatever its symbol, as the Description promises.

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("€#,0.00")]
        [InlineData("€ #,0.00")]
        [InlineData("#,0.00 \"€\"")]
        [InlineData("\\€#,0.00;-\\€#,0.00;\\€#,0.00")]
        [InlineData("£ #,0")]
        [InlineData("¥ #,0")]
        [InlineData("₹#,0.00")]
        public async Task Verify_no_format_rule_rewrites_a_currency_format_whatever_its_symbol(string currency)
        {
            using var f = await NorthstarAsync();
            await Mutate(f, m => m.Tables["Sales"].Measures["Last Order Date"].FormatString = currency);

            var hits = await WritersFlaggingAsync(f);
            Assert.True(hits.Length == 0,
                $"the currency format \"{currency}\" is flagged by a rule whose fix overwrites the format string: {string.Join(", ", hits)}");

            await PressEverythingAsync(f);
            Assert.Equal(currency, await f.Sessions.Require().ReadAsync(m => m.Tables["Sales"].Measures["Last Order Date"].FormatString));
        }

        // 3. A unit written in a script other than Latin. The new Description says "Any format string carrying a
        // letter is left alone", and the cost note says a number with a unit written into it, such as #,0 "days", is
        // left alone. The proof is [A-Za-z], so it only knows Latin letters: a count in pieces, #,0 "шт", or in items,
        // #,0"件", is raised and one press drops the unit. Answer: a letter in any script counts as a letter.

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("#,0 \"шт\"")]
        [InlineData("#,0\"件\"")]
        [InlineData("#,0 \"개\"")]
        public async Task Verify_a_number_format_with_a_unit_in_any_script_is_left_alone(string withUnit)
        {
            using var f = await NorthstarAsync();
            await Mutate(f, m => m.Tables["Sales"].Measures["Last Order Date"].FormatString = withUnit);

            var hits = await WritersFlaggingAsync(f);
            Assert.True(hits.Length == 0,
                $"the format \"{withUnit}\", which carries letters, is flagged by a rule whose fix overwrites the format string: {string.Join(", ", hits)}");

            await PressEverythingAsync(f);
            Assert.Equal(withUnit, await f.Sessions.Require().ReadAsync(m => m.Tables["Sales"].Measures["Last Order Date"].FormatString));
        }

        // 3b. The same Latin-only proof in PERCENTAGE_FORMATTING. Its new Description says "a percentage a person has
        // written words into ... is never flagged", and 0.0% "of plan" is indeed left alone, but the same words in
        // Russian or Japanese are raised and one press rewrites the format to the house style with the words gone.

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("0.0% \"от плана\"")]
        [InlineData("0.0%\"の計画\"")]
        public async Task Verify_a_percentage_with_words_in_any_script_is_left_alone(string withWords)
        {
            using var f = await NorthstarAsync();
            await Mutate(f, m => m.Tables["Sales"].Measures["Last Order Date"].FormatString = withWords);

            var hits = await WritersFlaggingAsync(f);
            Assert.True(hits.Length == 0,
                $"the percentage format \"{withWords}\", which carries words, is flagged by a rule whose fix overwrites the format string: {string.Join(", ", hits)}");

            await PressEverythingAsync(f);
            Assert.Equal(withWords, await f.Sessions.Require().ReadAsync(m => m.Tables["Sales"].Measures["Last Order Date"].FormatString));
        }

        // 4. The brief says "try every format rule". FORMAT_FLAG_COLUMNS_AS_YES/NO_VALUE_STRINGS reads FormatString and
        // its " Flag" branch accepts any DataType but String, so a DATE column whose name ends in " Flag" and which
        // carries a real date format is raised and told to use the "Yes";"Yes";"No" format, which cannot describe a
        // date. It has no fix, so nothing is overwritten; it is a false finding on a real date format, on a column.

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("Long Date")]
        [InlineData("dd/mm/yyyy")]
        [InlineData("hh:nn")]
        public async Task Verify_no_format_rule_flags_a_date_column_named_like_a_flag(string format)
        {
            using var f = await NorthstarAsync();
            await f.Engine.CreateColumnAsync("table:Date", "Renewal Flag", "DateTime", "RenewalFlag", "human");
            await Mutate(f, m => m.Tables["Date"].Columns["Renewal Flag"].FormatString = format);

            var formatRules = BpaRuleSet.Standard()
                .Where(r => (r.Expression ?? "").IndexOf("FormatString", StringComparison.OrdinalIgnoreCase) >= 0)
                .Select(r => r.ID).ToArray();
            var hits = (await f.Engine.BpaScanAsync()).Violations
                .Where(v => v.ObjectRef == "column:Date/Renewal Flag" && formatRules.Contains(v.RuleId))
                .Select(v => v.RuleId).OrderBy(x => x, StringComparer.Ordinal).ToArray();
            Assert.True(hits.Length == 0,
                $"a date column formatted \"{format}\" is flagged by a format rule: {string.Join(", ", hits)}");
        }

        // Default tier: the witness row of the sweep form, through the same body.
        [Fact]
        public Task Verify_no_format_rule_flags_a_date_column_named_like_a_flag_sample() =>
            Verify_no_format_rule_flags_a_date_column_named_like_a_flag("Long Date");
    }
}
