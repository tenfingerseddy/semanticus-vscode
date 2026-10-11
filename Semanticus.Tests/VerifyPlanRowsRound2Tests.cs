// Verifier's failing tests for lane m11/plan-rows, round 2, at 96e0ca74 (29 Sep). Each one fails on the lane head and
// names a defect in the verifier's report (lane-reports/v-plan-rows-r2.md). They are left failing on purpose: the fix
// makes them pass.
//
//   V7  Run the plan writes a format expression a model's own rule asks for. The best-practice row sends only a
//       FormatString fill through FormatFill.MayFill; a fix on FormatStringExpression is not a format to it, so a rule
//       that travels with the model (load_rules, the BestPracticeAnalyzer annotation) replaces the person's own
//       format with a dynamic one nobody proved.
//   V8  One default page of get_words_queue does not fit an assistant's reading limit when the model's measures carry
//       long DAX. Every item repeats its object's whole grounding, so 40 items on measures of about 2,000 characters
//       of DAX are about 160,000 characters, where Claude Code reads at most 25,000 tokens from one tool result.

using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    public sealed class VerifyPlanRowsRound2Tests
    {
        private static async Task<string> PropertyOf(IEngine engine, string objRef, string name) =>
            (await engine.GetObjectPropertiesAsync(objRef)).FirstOrDefault(p => p.Name == name)?.Value?.ToString() ?? "";

        private static async Task PressUntilQuietAsync(IEngine engine)
        {
            for (var press = 0; press < 5; press++)
            {
                var plan = await engine.GetModelPlanAsync();
                if (!plan.Items.Any(i => i.StateWord == PlanStateWords.Ready && i.Doer == PlanDoers.Semanticus)) return;
                var ran = await engine.ApplyPlanItemsAsync(null, "human", plan.ReadyDigest);
                if (!ran.Performed) return;
            }
        }

        // A model rule whose fix writes a format string expression on every measure that has none.
        private const string DynamicFormatRule = """
            [ { "ID": "VERIFY_DYNAMIC_FORMAT", "Name": "Measures should carry a dynamic format", "Category": "Formatting",
                "Description": "Verifier probe: a rule that travels with the model.", "Severity": 2, "Scope": "Measure",
                "Expression": "string.IsNullOrEmpty(FormatStringExpression)",
                "FixExpression": "FormatStringExpression = \"[Total Sales]\"", "CompatibilityLevel": 1200 } ]
            """;

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task V7_run_the_plan_never_writes_a_format_expression_a_model_rule_asks_for()
        {
            using var f = await ChangeCauseTests.PlanModelAsync("VerifyRuleFormatExpression.bim");
            var money = await f.Engine.CreateMeasureAsync("table:Sales", "Money", "SUM ( Sales[Amount] )", "human");
            await f.Engine.SetMeasureFormatAsync(money, "$#,##0.00", "human");
            await f.Engine.LoadBpaRulesAsync(DynamicFormatRule, false, "human");
            await PressUntilQuietAsync(f.Engine);
            var format = await PropertyOf(f.Engine, money, "FormatString");
            var expression = await PropertyOf(f.Engine, money, "FormatStringExpression");
            Assert.True(format == "$#,##0.00" && expression.Length == 0,
                "Run the plan changed the person's format '$#,##0.00' to static '" + format + "', dynamic '" + expression
                + "': a format Semanticus cannot prove, written by the best-practice row.");
        }

        [Fact]
        public async Task V8_a_default_page_of_the_words_queue_fits_an_assistants_reading_limit_when_the_dax_is_long()
        {
            // 20 columns and 60 measures of about 2,000 characters of DAX each, written as a file and opened in one call (81 engine writes before).
            using var f = await PlanRowsBim.OpenAsync("LongDax", "LongDax.bim", new object[]
            {
                PlanRowsBim.Table("Fact", Enumerable.Range(0, 20).Select(c => $"AMT_{c:00}:Decimal").ToArray(),
                    Enumerable.Range(0, 60).Select(k =>
                    {
                        // About 2,000 characters of DAX: a sum of column sums, as long business measures often are.
                        var terms = Enumerable.Range(0, 90).Select(i => $"SUM ( 'Fact'[AMT_{(k + i * 7) % 20:00}] )");
                        return PlanRowsBim.M($"M_{k:00}", string.Join(" + ", terms));
                    }).ToArray()),
            });
            var page = await McpTools.GetWordsQueue(f.Engine);
            var chars = JsonSerializer.Serialize(page, new JsonSerializerOptions(JsonSerializerDefaults.Web)).Length;
            // 80,000 characters is about 20,000 tokens: inside Claude Code's default 25,000-token limit for one result.
            Assert.True(chars <= 80_000, "the default page (" + page.Items.Length + " of " + page.Total + " items) is " + chars
                + " characters, about " + chars / 4 + " tokens: the person's assistant cannot read it in one result.");
        }
    }
}
