// Verifier's failing tests for lane m11/plan-rows, round 3, at 37f2cb95 (29 Sep). Each one fails on the lane head and
// names a defect in the verifier's report (lane-reports/v-plan-rows-r3.md). They are left failing on purpose: the fix
// makes them pass.
//
//   V9  Run the plan writes the formats the change plan proposed, past the fill check. propose_plan (the assistant's
//       "pull request for your model", and the Studio's "Review these together") seeds every best-practice fix the scan
//       can make, formats included, and this lane files every bpa_fix line under the best-practice row, a Semanticus
//       row. Run the plan takes a row that already has a proposal as it stands (it works lines out only for a row with
//       none), so the fill check in BpaPlanLines never sees them: while the best-practice row is the one ready row, the
//       next press writes INTEGER_FORMATTING's #,0 over a person's money format and on a ratio, PERCENTAGE_FORMATTING's
//       pattern over a person's percent, a rule that travels with the model writes its dynamic format, and the shipped
//       AVOID_FLOATING_POINT_DATA_TYPES fix turns a Double column into Decimal (a data type change the row refuses too).

using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    public sealed class VerifyPlanRowsRound3Tests
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

        // A model rule whose fix writes a format string expression on every measure that has none (as in round 2's V7).
        private const string DynamicFormatRule = """
            [ { "ID": "VERIFY_DYNAMIC_FORMAT", "Name": "Measures should carry a dynamic format", "Category": "Formatting",
                "Description": "Verifier probe: a rule that travels with the model.", "Severity": 2, "Scope": "Measure",
                "Expression": "string.IsNullOrEmpty(FormatStringExpression)",
                "FixExpression": "FormatStringExpression = \"[Rows]\"", "CompatibilityLevel": 1200 } ]
            """;

        // One fact table and four measures a person has partly formatted: a ratio (none), a count (none), money with
        // cents (#,##0.00, no $ sign, so INTEGER_FORMATTING flags it) and a percent (0.0%, which PERCENTAGE_FORMATTING
        // flags). With no relationship there is no safe-fix row, so the best-practice row is ready from the start, as it
        // is on any model once the safe fixes are in.
        private static Task<DecisionFixture> FormattedModelAsync(string file) => PlanRowsBim.OpenAsync("Formats", file,
            new object[]
            {
                PlanRowsBim.Table("Sales", new[] { "Amount:Decimal", "Qty:Int64" },
                    PlanRowsBim.M("Share", "DIVIDE ( SUM ( Sales[Amount] ), SUM ( Sales[Amount] ) + 1 )"),
                    PlanRowsBim.M("Rows", "COUNTROWS ( Sales )"),
                    PlanRowsBim.M("Money", "SUM ( Sales[Amount] )", "#,##0.00"),
                    PlanRowsBim.M("Margin Pct", "DIVIDE ( SUM ( Sales[Amount] ), 3 )", "0.0%")),
            });

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task V9_run_the_plan_never_writes_a_format_the_change_plan_proposed_that_the_fill_check_refuses()
        {
            using var f = await FormattedModelAsync("VerifyProposedFormats.bim");
            var row = (await f.Engine.GetModelPlanAsync()).Items.Single(i => i.Id == "bpafix/model");
            Assert.Equal(PlanStateWords.Ready, row.StateWord);
            // The assistant asks for the change plan, with the tool's own defaults (the ai-ready and model-pr skills do).
            await f.Engine.ProposePlanAsync(null, true, 40, "agent");
            await PressUntilQuietAsync(f.Engine);
            var money = await PropertyOf(f.Engine, "measure:Sales/Money", "FormatString");
            var pct = await PropertyOf(f.Engine, "measure:Sales/Margin Pct", "FormatString");
            var share = await PropertyOf(f.Engine, "measure:Sales/Share", "FormatString");
            Assert.True(money == "#,##0.00" && pct == "0.0%" && share.Length == 0,
                "after propose_plan, Run the plan wrote formats the best-practice row's own fill check refuses: Money "
                + "'#,##0.00' -> '" + money + "', Margin Pct '0.0%' -> '" + pct + "', the ratio Share '' -> '" + share + "'.");
        }

        // Not only formats: the row refuses a data type change on its own lines, and the shipped AVOID_FLOATING_POINT_DATA_TYPES
        // fix (DataType = DataType.Decimal) goes through the same way. The rules lane's format withholding would not stop it.
        [Fact]
        public async Task V9_run_the_plan_never_changes_a_data_type_the_change_plan_proposed()
        {
            using var f = await FormattedModelAsync("VerifyProposedType.bim");
            await f.Engine.CreateColumnAsync("table:Sales", "Rate", "Double", null, "human");
            await f.Engine.ProposePlanAsync(null, true, 40, "agent");
            await PressUntilQuietAsync(f.Engine);
            var type = await PropertyOf(f.Engine, "column:Sales/Rate", "DataType");
            Assert.True(type == "Double", "after propose_plan, Run the plan changed the person's column Sales[Rate] from Double to "
                + type + ": a data type change, which the best-practice row refuses on its own lines.");
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task V9_run_the_plan_never_writes_a_format_expression_the_change_plan_proposed_from_a_model_rule()
        {
            using var f = await FormattedModelAsync("VerifyProposedExpression.bim");
            await f.Engine.LoadBpaRulesAsync(DynamicFormatRule, false, "human");
            await f.Engine.ProposePlanAsync(null, true, 40, "agent");
            await PressUntilQuietAsync(f.Engine);
            var format = await PropertyOf(f.Engine, "measure:Sales/Money", "FormatString");
            var expression = await PropertyOf(f.Engine, "measure:Sales/Money", "FormatStringExpression");
            Assert.True(format == "#,##0.00" && expression.Length == 0,
                "after propose_plan, Run the plan changed the person's format '#,##0.00' to static '" + format + "', dynamic '"
                + expression + "': the model rule's dynamic format, which the best-practice row refuses on its own lines.");
        }
    }
}
