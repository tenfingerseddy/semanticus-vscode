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
    /// Verifier findings on m11/feedback-rules at eb996ca6 (the full re-verify of the format rules, V1c). The round-7
    /// grammar keeps every date, time, currency, script, escape, locale code and dynamic format tried away from both
    /// measure format rules. Two holes remain in the ruling it follows ("prove what a format IS, leave everything
    /// else alone and never write over it"). The grammar is a list of allowed CHARACTERS, not a grammar of a plain
    /// number, so a format made only of those characters that means something else is still raised and one press
    /// or Fix all writes the house style over it: a number divided by a thousand, a section that hides values, a
    /// digit mask. And the change-plan door applies a fix it seeded earlier without asking the rule again, so a plan
    /// built before the person gave a measure a dynamic format writes "#,0" beside that format. These cases fail at
    /// that head and name what a fix must make true without choosing the fix.
    /// </summary>
    public sealed class VerifyFeedbackRulesRound8Tests : IClassFixture<ModelTemplates>
    {
        private readonly ModelTemplates _templates;
        public VerifyFeedbackRulesRound8Tests(ModelTemplates templates) => _templates = templates;

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
            await e.CreateMeasureAsync("table:Sales", "Share", "DIVIDE ( SUM ( Sales[Amount] ), 100 )", "human");
            await e.CreateMeasureAsync("table:Sales", "Last Order Date", "MAX ( Sales[Amount] )", "human");
        }

        private static Task<long> Mutate(DecisionFixture f, Action<Model> edit) =>
            f.Sessions.Require().MutateAsync("human", "test setup", edit);

        /// <summary>Every shipped rule whose one-click fix writes FormatString, whatever it is scoped at.</summary>
        private static string[] FormatWritingRules() => BpaRuleSet.Standard()
            .Where(r => (r.FixExpression ?? "").Replace(" ", "").StartsWith("FormatString=", StringComparison.OrdinalIgnoreCase))
            .Select(r => r.ID).ToArray();

        private const string Ref = "measure:Sales/Net Sales";

        /// <summary>Press every format-writing fix on the measure (a refusal is an acceptable answer), then Fix all.</summary>
        private static async Task PressEverythingAsync(DecisionFixture f)
        {
            foreach (var rule in FormatWritingRules())
            {
                try { await f.Engine.BpaFixAsync(rule, Ref, "agent"); }
                catch (InvalidOperationException) { /* refusing the press is one acceptable answer */ }
            }
            await f.Engine.BpaFixAllAsync("human");
            // The third writer, the change-plan door: seed a plan now and apply every fix it offers.
            var plan = await McpTools.ProposePlan(f.Engine, null, false, 0);
            var fixes = plan.Items.Where(i => i.Kind == "bpa_fix").Select(i => i.Id).ToArray();
            if (fixes.Length > 0) await McpTools.ApplyPlan(f.Engine, fixes);
        }

        // The coordinator's ruling after this round (28 Sep, V1d): a fix never writes over a format string that is
        // already there, on any door, and INTEGER_FORMATTING and PERCENTAGE_FORMATTING may still RAISE a format like
        // these as advice with no one-click fix. So the failure is a finding that OFFERS a fix here, not a finding.
        // Pressing every fix, then Fix all, then applying every fix the change plan seeds, must still leave the format
        // exactly as the person wrote it. The "Answer:" lines below name a grammar fix; the ruling SUPERSEDES them
        // (no grammar, a fill-only fix instead), and they are kept as the verifier wrote them.
        private static async Task<string[]> WritersFlaggingAsync(DecisionFixture f) =>
            (await f.Engine.BpaScanAsync()).Violations
                .Where(v => v.ObjectRef == Ref && v.CanAutoFix && FormatWritingRules().Contains(v.RuleId))
                .Select(v => v.RuleId).OrderBy(x => x, StringComparer.Ordinal).ToArray();

        private async Task AssertLeftAloneAsync(string format, string what)
        {
            using var f = await NorthstarAsync();
            await Mutate(f, m => m.Tables["Sales"].Measures["Net Sales"].FormatString = format);

            var hits = await WritersFlaggingAsync(f);
            Assert.True(hits.Length == 0,
                $"\"{format}\" is {what}, not a plain number, yet a rule offers a one-click fix that overwrites the format string: {string.Join(", ", hits)}");

            await PressEverythingAsync(f);
            Assert.Equal(format, await f.Sessions.Require().ReadAsync(m => m.Tables["Sales"].Measures["Net Sales"].FormatString));
        }

        // 1. A number SCALED by a thousand or a million. Microsoft's Power BI format string reference: "Two adjacent
        // thousands separators or a thousands separator immediately to the left of the decimal separator (whether or
        // not a decimal is specified) means scale the number by dividing it by 1,000", with its own example: "##0,,"
        // shows 100 million as 100. Every character is in the grammar, so INTEGER_FORMATTING raises it as a plain
        // number and one press writes "#,0": the value a person reads becomes a thousand or a million times larger,
        // under a title that still says thousands or millions. PERCENTAGE_FORMATTING has the same hole, so one case is
        // a percentage. Answer: a comma is a thousands separator only between digit placeholders; a trailing comma, or
        // one just left of the decimal point, is not a plain number.

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("##0,,")]
        [InlineData("#,##0,,")]
        [InlineData("#,##0,")]
        [InlineData("#,##0,.0")]
        [InlineData("#,##0,.0%")]
        public async Task Verify_no_format_rule_rewrites_a_number_format_that_divides_by_a_thousand(string scaled) =>
            await AssertLeftAloneAsync(scaled, "a number divided by a thousand or a million");

        // 2. A section that HIDES values. The same reference: "If you include semicolons with nothing between them,
        // the missing section doesn't display", with its own examples "0.00;;" (only positive values show),
        // "0.00;-0.0;" (a zero shows nothing) and "0.00;" (a negative shows nothing). A section that is an empty
        // quoted piece or only a space does the same. Each is raised, and one press writes a house style that shows
        // every value, so the numbers a person chose to hide appear. Answer: a section that shows nothing (empty,
        // only spaces, or only an empty quoted piece) is not part of a plain number the house style can replace. A
        // zero section that shows a dash, such as "-", still shows something, so these cases do not touch it.

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("0.00;;")]
        [InlineData("0.00;-0.0;")]
        [InlineData("0.00;")]
        [InlineData("#,0;-#,0;\"\"")]
        [InlineData("#,0;-#,0;\" \"")]
        [InlineData("0.00; ")]
        [InlineData("0.0%;-0.0%;")]
        public async Task Verify_no_format_rule_writes_over_a_format_that_hides_some_values(string hiding) =>
            await AssertLeftAloneAsync(hiding, "a format that hides some values");

        // 3. A DIGIT MASK. "0000-00-00" shows the date key 20260928 as 2026-09-28, and "(000) 000-0000" lays out a
        // phone number. The minus sign and the brackets the grammar allows are meant for a negative number, but here
        // they are literal separators between digits. One press writes "#,0" and the date key reads 20,260,928.
        // Answer: a minus sign or a bracket between digit placeholders is text, not the sign of a number.

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("0000-00-00")]
        [InlineData("(000) 000-0000")]
        public async Task Verify_no_format_rule_rewrites_a_digit_mask(string mask) =>
            await AssertLeftAloneAsync(mask, "a digit mask");

        // 4. The CHANGE-PLAN door, on both doors. propose_plan seeds each auto-fixable finding as an approved bpa_fix
        // item, and apply_plan applies it later. Its stale-value guard compares only the property the fix writes, so
        // a measure that had no format when the plan was built and was then given a dynamic date format by the person
        // still gets "#,0" written beside that format: V1b's D1, by another road. The saved model then carries both a
        // static and a dynamic format string. apply_fix does not have this hole because it asks the rule again at the
        // moment it writes. Answer: at apply time, a bpa_fix item the rule no longer raises is skipped, or the guard
        // also sees the format string expression.

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("mcp")]
        [InlineData("studio")]
        public async Task Verify_a_plan_built_before_a_dynamic_format_was_set_never_writes_beside_it(string door)
        {
            using var f = await NorthstarAsync();
            // Since THE FORMAT FILL RULE (28 Sep) the plan seeds a fill only for a measure proved to be a whole number,
            // so the scenario's blank measure is a count (it was a MAX of a date, which is no longer filled at all).
            await f.Engine.CreateMeasureAsync("table:Sales", "Order Rows", "COUNTROWS ( Sales )", "human");
            const string Target = "measure:Sales/Order Rows";
            var rpc = new EngineRpcTarget(f.Engine);
            var plan = door == "mcp" ? await McpTools.ProposePlan(f.Engine, null, false, 0) : await rpc.proposePlan(null, false, 0);
            Assert.Contains(plan.Items, i => i.RuleId == "INTEGER_FORMATTING" && i.ObjectRef == Target);   // the scenario: a measure with no format

            await f.Sessions.Require().MutateAsync("human", "the person gives the measure a dynamic date format",
                m => m.Tables["Sales"].Measures["Order Rows"].FormatStringExpression = "\"dd/mm/yyyy\"");

            var ids = plan.Items.Select(i => i.Id).ToArray();
            if (door == "mcp") await McpTools.ApplyPlan(f.Engine, ids); else await rpc.applyPlan(ids);

            var (fs, fse) = await f.Sessions.Require().ReadAsync(m =>
                (m.Tables["Sales"].Measures["Order Rows"].FormatString, m.Tables["Sales"].Measures["Order Rows"].FormatStringExpression));
            Assert.True(string.IsNullOrEmpty(fs),
                $"the {door} plan door wrote the static format \"{fs}\" beside the dynamic date format the person set after the plan was built");
            Assert.Equal("\"dd/mm/yyyy\"", fse);
        }

        // 5. The copy. Both measure format rules' Descriptions say the format string may carry "at most a quoted piece
        // of that punctuation". The expression removes EVERY quoted piece, so a format with two, such as "("#,0")",
        // is raised and rewritten. Either the rule takes at most one, or its Description says how many it takes.

        [Fact]
        public async Task Verify_each_measure_format_rule_description_says_how_many_quoted_pieces_it_takes()
        {
            using var f = await NorthstarAsync();
            await Mutate(f, m =>
            {
                m.Tables["Sales"].Measures["Net Sales"].FormatString = "\"(\"#,0\")\"";
                m.Tables["Sales"].Measures["Share"].FormatString = "\"(\"0.0%\")\"";
            });
            var card = await f.Engine.BpaScanAsync();
            var rules = BpaRuleSet.Standard().ToDictionary(r => r.ID);
            foreach (var (id, objRef) in new[] { ("INTEGER_FORMATTING", Ref), ("PERCENTAGE_FORMATTING", "measure:Sales/Share") })
            {
                var raises = card.Violations.Any(v => v.RuleId == id && v.ObjectRef == objRef);
                Assert.False(raises && rules[id].Description.Contains("at most a quoted piece", StringComparison.Ordinal),
                    $"{id} raises a format with two quoted pieces, while its Description says it takes at most a quoted piece");
            }
        }
    }
}
