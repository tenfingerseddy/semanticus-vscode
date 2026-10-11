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
    /// Verifier findings on m11/feedback-rules at e8430e99 (the full re-verify of the format rules after the fill-only
    /// ruling, V1d). The ruling: a fix never writes over a format string that is already there, on any door, and only
    /// fills an empty one on an object with no format string expression; advice may still be raised, but its fix is not
    /// automatic. Across 1987 format shapes on a measure and on five kinds of column, both doors, Pro and Free, the
    /// shipped rules now keep that ruling. These cases are the places it does not reach yet: a custom rule's fix on a
    /// format property the analyzer does not know by name, the advice text on a measure whose format is set by an
    /// expression, and the Model Plan's own format line on such a measure. They fail at that head and name what must be
    /// true without choosing the fix.
    /// </summary>
    public sealed class VerifyFeedbackRulesRound9Tests : IClassFixture<ModelTemplates>
    {
        private readonly ModelTemplates _templates;
        public VerifyFeedbackRulesRound9Tests(ModelTemplates templates) => _templates = templates;

        private static Task<long> Mutate(DecisionFixture f, Action<Model> edit) =>
            f.Sessions.Require().MutateAsync("human", "test setup", edit);

        private static string Rule(string id, string scope, string fix) =>
            "{\"ID\":\"" + id + "\",\"Name\":\"" + id + "\",\"Category\":\"Formatting\",\"Severity\":3,\"Scope\":\"" + scope + "\","
            + "\"Expression\":\"true\",\"FixExpression\":" + System.Text.Json.JsonSerializer.Serialize(fix) + "}";

        /// <summary>Press the rule's fix on both doors (a refusal is an acceptable answer), then Fix all on the Studio door,
        /// apply_safe_fixes on the MCP door, and every fix the change plan seeds, on both doors.</summary>
        private static async Task PressEverythingAsync(DecisionFixture f, string ruleId, string objRef)
        {
            var rpc = new EngineRpcTarget(f.Engine);
            try { await McpTools.ApplyFix(f.Engine, "bpa", ruleId, objRef); } catch (InvalidOperationException) { }
            try { await rpc.bpaFix(ruleId, objRef); } catch (InvalidOperationException) { }
            await rpc.bpaFixAll();
            await McpTools.ApplySafeFixes(f.Engine, "bpa");
            var plan = await McpTools.ProposePlan(f.Engine, null, false, 0);
            var fixes = plan.Items.Where(i => i.Kind == "bpa_fix").Select(i => i.Id).ToArray();
            if (fixes.Length > 0) await McpTools.ApplyPlan(f.Engine, fixes);
            var studioPlan = await rpc.proposePlan(null, false, 0);
            var studioFixes = studioPlan.Items.Where(i => i.Kind == "bpa_fix").Select(i => i.Id).ToArray();
            if (studioFixes.Length > 0) await rpc.applyPlan(studioFixes);
        }

        // 1. A CALCULATION GROUP'S OWN DYNAMIC FORMATS. A calculation group carries two format string expressions of its
        // own, one for when no item is selected and one for when several are. They are formats as much as a measure's
        // FormatStringExpression is, and the wrapper exposes both as plain writable string properties. The fill-only rule
        // is keyed on two property NAMES (BpaAnalyzer.IsFormatProperty: FormatString and FormatStringExpression), so a
        // model's own rule whose fix targets either of these writes over the dynamic format the person set: one press, Fix
        // all, apply_safe_fixes and the plan all offer it. The lane's claim is that the rule holds for a custom rule too
        // and never writes over a format, on any door (BPARules-PowerBI.PROVENANCE.md, round 8).

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("NoSelectionFormatStringExpression")]
        [InlineData("MultipleOrEmptySelectionFormatStringExpression")]
        public async Task Verify_no_custom_fix_writes_over_a_calculation_group_format_expression(string property)
        {
            using var f = new DecisionFixture(TestEntitlements.Pro);
            var e = f.Engine;
            await e.CreateModelAsync("Northstar", 1702);
            await e.CreateTableAsync("Sales", "human");
            await e.CreateColumnAsync("table:Sales", "Amount", "Decimal", "Amount", "human");
            await e.CreateMeasureAsync("table:Sales", "Net Sales", "SUM ( Sales[Amount] )", "human");
            var cg = await e.CreateCalculationGroupAsync("Scale", "human");
            await e.CreateCalculationItemAsync(cg, "Millions", "SELECTEDMEASURE () / 1000000", "human");
            const string Existing = "\"##0,,\"";
            await Mutate(f, m =>
            {
                var t = (CalculationGroupTable)m.Tables["Scale"];
                t.NoSelectionExpression = "SELECTEDMEASURE ()";
                t.MultipleOrEmptySelectionExpression = "SELECTEDMEASURE ()";
                typeof(CalculationGroupTable).GetProperty(property).SetValue(t, Existing);
            });
            await e.LoadBpaRulesAsync("[" + Rule("TEST_GROUP_FORMAT", "CalculationGroup", property + " = \"\\\"#,0\\\"\"") + "]", replace: true, "human");

            var card = await e.BpaScanAsync();
            var finding = Assert.Single(card.Violations, v => v.RuleId == "TEST_GROUP_FORMAT");   // the rule does raise the group
            Assert.False(finding.CanAutoFix,
                $"a fix is offered that writes over the calculation group's {property} (\"##0,,\" shows a number in millions)");

            await PressEverythingAsync(f, "TEST_GROUP_FORMAT", finding.ObjectRef);
            var now = await f.Sessions.Require().ReadAsync(m => (string)typeof(CalculationGroupTable).GetProperty(property).GetValue(m.Tables["Scale"]));
            Assert.Equal(Existing, now);
        }

        // 2. A KPI'S TARGET FORMAT. A KPI has its own format string for its target value, TargetFormatString. The same
        // name-keyed rule does not know it either, so a model's own rule scoped to KPI is offered as a one-click fix that
        // writes over a target shown as a percentage: bpa_scan marks it CanAutoFix, Model quality shows a Fix button and
        // counts it in "this app can fix", and the deploy gate counts it as fixable. Today only an unrelated gap stops
        // the write (a KPI's ref, obj:KPI/..., does not resolve, so the press answers "Object not found"), so this case
        // is latent; it fails on the offer, and on the write if the ref is ever made to resolve.

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Verify_no_custom_fix_writes_over_a_kpi_target_format_string()
        {
            using var f = new DecisionFixture(TestEntitlements.Pro);
            var e = f.Engine;
            await e.CreateModelAsync("Northstar", 1604);
            await e.CreateTableAsync("Sales", "human");
            await e.CreateColumnAsync("table:Sales", "Amount", "Decimal", "Amount", "human");
            await e.CreateMeasureAsync("table:Sales", "Margin", "SUM ( Sales[Amount] )", "human");
            await Mutate(f, m =>
            {
                var k = m.Tables["Sales"].Measures["Margin"].AddKPI();
                k.TargetExpression = "0.3";
                k.StatusExpression = "1";
                k.TargetFormatString = "0.0%";
            });
            await e.LoadBpaRulesAsync("[" + Rule("TEST_KPI_FORMAT", "KPI", "TargetFormatString = \"#,0\"") + "]", replace: true, "human");

            var card = await e.BpaScanAsync();
            var finding = Assert.Single(card.Violations, v => v.RuleId == "TEST_KPI_FORMAT");   // the rule does raise the KPI
            Assert.False(finding.CanAutoFix, "a fix is offered that writes over the KPI's target format \"0.0%\"");

            await PressEverythingAsync(f, "TEST_KPI_FORMAT", finding.ObjectRef);
            Assert.Equal("0.0%", await f.Sessions.Require().ReadAsync(m => m.Tables["Sales"].Measures["Margin"].KPI.TargetFormatString));
        }

        // 3. THE ADVICE ON A MEASURE WHOSE FORMAT IS SET BY AN EXPRESSION. Tabular Editor's own INTEGER_FORMATTING, the
        // rule people load from its public rule file, has no format string expression guard, so as the model's own rule it
        // raises a measure shown through a dynamic format. The fill-only rule rightly withholds its fix, and get_fix_prompt
        // returns advice. But the advice is written for a static format: it says the format is "already set" to the empty
        // static string, and tells the assistant to change it "with set_property on FormatString". That write puts a
        // static format beside the dynamic one, which Analysis Services does not allow (TOMWrapper Measure.cs, the
        // FormatStringExpression setter) and which is the very harm round 8 closed on the plan door (F4). The lane's own
        // words: get_fix_prompt "tells the assistant what the current format is". Here it does not.

        private const string UpstreamInteger =
            "[{\"ID\":\"INTEGER_FORMATTING\",\"Name\":\"[Formatting] Whole numbers should be formatted with thousands separators and no decimals\","
            + "\"Category\":\"Formatting\",\"Severity\":2,\"Scope\":\"Measure\","
            + "\"Expression\":\"not FormatString.Contains(\\\"$\\\") and not FormatString.Contains(\\\"%\\\") and not (FormatString = \\\"#,0\\\" or FormatString = \\\"#,0.0\\\")\","
            + "\"FixExpression\":\"FormatString = \\\"#,0\\\"\",\"CompatibilityLevel\":1200}]";

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("mcp")]
        [InlineData("studio")]
        public async Task Verify_the_advice_on_a_dynamic_format_says_the_format_is_an_expression(string door)
        {
            using var f = new DecisionFixture(TestEntitlements.Pro);
            var e = f.Engine;
            await e.CreateModelAsync("Northstar", 1604);
            await e.CreateTableAsync("Sales", "human");
            await e.CreateColumnAsync("table:Sales", "Amount", "Decimal", "Amount", "human");
            await e.CreateMeasureAsync("table:Sales", "Due", "MAX ( Sales[Amount] )", "human");
            const string Target = "measure:Sales/Due";
            await Mutate(f, m => m.Tables["Sales"].Measures["Due"].FormatStringExpression = "\"dd/mm/yyyy\"");
            await e.LoadBpaRulesAsync(UpstreamInteger, replace: true, "human");

            var card = await e.BpaScanAsync();
            Assert.Contains(card.Violations, v => v.RuleId == "INTEGER_FORMATTING" && v.ObjectRef == Target && !v.CanAutoFix);

            var prompt = door == "mcp"
                ? await McpTools.GetFixPrompt(e, "bpa", "INTEGER_FORMATTING", Target)
                : await new EngineRpcTarget(e).bpaGetFixPrompt("INTEGER_FORMATTING", Target);
            Assert.Equal("(advice)", prompt.Tool);
            Assert.True(prompt.Prompt.Contains("format string expression", StringComparison.Ordinal) && prompt.Prompt.Contains("dd/mm/yyyy", StringComparison.Ordinal),
                "the advice on a measure shown through a dynamic format does not say its format is an expression, or what the "
                + "expression is, and sends the assistant to a static write beside it: " + prompt.Prompt);
        }

        // 4. The refusal a press gets on that same measure, on both doors. It does say the format is set by an expression,
        // then tells the person to "set it yourself with set_measure_format or set_property": both set a static format
        // string and leave the expression in place, so following the refusal's own words gives the measure both formats.

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("mcp")]
        [InlineData("studio")]
        public async Task Verify_the_refusal_on_a_dynamic_format_does_not_send_the_person_to_a_static_write_beside_it(string door)
        {
            using var f = new DecisionFixture(TestEntitlements.Pro);
            var e = f.Engine;
            await e.CreateModelAsync("Northstar", 1604);
            await e.CreateTableAsync("Sales", "human");
            await e.CreateColumnAsync("table:Sales", "Amount", "Decimal", "Amount", "human");
            await e.CreateMeasureAsync("table:Sales", "Due", "MAX ( Sales[Amount] )", "human");
            const string Target = "measure:Sales/Due";
            await Mutate(f, m => m.Tables["Sales"].Measures["Due"].FormatStringExpression = "\"dd/mm/yyyy\"");
            await e.LoadBpaRulesAsync(UpstreamInteger, replace: true, "human");

            var refusal = door == "mcp"
                ? await Assert.ThrowsAsync<InvalidOperationException>(() => McpTools.ApplyFix(e, "bpa", "INTEGER_FORMATTING", Target))
                : await Assert.ThrowsAsync<InvalidOperationException>(() => new EngineRpcTarget(e).bpaFix("INTEGER_FORMATTING", Target));
            Assert.Contains("format string expression", refusal.Message, StringComparison.Ordinal);
            Assert.False(refusal.Message.Contains("set it yourself with set_measure_format or set_property", StringComparison.Ordinal),
                "the refusal on a measure whose format is set by an expression tells the person to set a static format string, "
                + "which leaves the expression in place and gives the measure both formats: " + refusal.Message);
        }

        // 5. THE PLAN'S OWN FORMAT LINE ON A DYNAMIC FORMAT. The Jev measures family asks about every measure whose static
        // format is blank (MeasureDefaultsPoint.WantsProposal reads FormatString only), so a measure shown through a format
        // string expression gets a "Format as ..." line on the Model Plan. Round 8's apply-time guard now refuses that
        // line, which is right, but the refusal comes after the person approved: the whole set is put back, the blank
        // measure beside it is not filled either, the note says the expression was set "now" when it was there before the
        // plan was worked out, and the same line is offered again, so every approve of that item fails until the person
        // finds and drops the line. The ruling allows a fill only on an object with no format string expression, so the
        // line should never be offered. Answers are recorded (no network), the way JevFamilyTests drive the family.

        // m11/jev-batch: an entry is keyed by the object ref (played with ByObjectRecordedOracle), not by a request digest, since a table is one request now.
        private static string MeasuresFixture(params (string objectRef, string family)[] rows)
        {
            var entries = rows.Where(r => r.objectRef != null).Select(r =>
                "{\"digest\":" + System.Text.Json.JsonSerializer.Serialize(r.objectRef)
                + ",\"point\":\"" + Semanticus.Decisions.DecisionRegistry.MeasureFolderAndFormat + "\""
                + ",\"usage\":{\"input_tokens\":900,\"output_tokens\":40}"
                + ",\"answers\":{\"display_folder\":{\"type\":\"choice\",\"choice\":\"" + Semanticus.Decisions.DecisionRegistry.NoFolder + "\",\"confidence\":0.95}"
                + ",\"format_family\":{\"type\":\"choice\",\"choice\":\"" + r.family + "\",\"confidence\":0.97}}}");
            return "{\"servedModelVersion\":\"jev-test-1\",\"entries\":[" + string.Join(",", entries) + "]}";
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Verify_the_plan_never_offers_a_static_format_for_a_measure_whose_format_is_an_expression()
        {
            using var f = new DecisionFixture(TestEntitlements.Pro);
            var e = f.Engine;
            await e.CreateModelAsync("Northstar", 1604);
            await e.CreateTableAsync("Sales", "human");
            await e.CreateColumnAsync("table:Sales", "Amount", "Double", null, "human");
            await e.CreateMeasureAsync("table:Sales", "Order Count", "COUNTROWS ( Sales )", "human", "Volume");
            await e.CreateMeasureAsync("table:Sales", "Due", "MAX ( Sales[Amount] )", "human");
            // The control's blank measure is a count (verifier V1e): under THE FORMAT FILL RULE a Jev line may fill only a
            // measure the engine proves is a whole number, and a SUM of the Double column Amount is not one.
            await e.CreateMeasureAsync("table:Sales", "Units", "COUNTROWS ( Sales )", "human");
            await Mutate(f, m =>
            {
                m.Tables["Sales"].Measures["Order Count"].FormatString = "#,##0";
                m.Tables["Sales"].Measures["Due"].FormatStringExpression = "\"dd/mm/yyyy\"";
            });
            await f.SaveAsAsync("Northstar.bim");
            f.UseNull();
            await f.ConsentAsync();
            // Due is recorded as a date: if the family asks about it at all it is answered, and the rule must still refuse the line.
            f.UseRecordedByObject(MeasuresFixture(("measure:Sales/Due", "date_or_time"), ("measure:Sales/Units", "whole_number")));

            await e.GetModelPlanAsync();
            await e.JevSettledForTest();
            var item = Assert.Single((await e.GetModelPlanAsync()).Items, i => i.Id == "jev.measures.table:Sales");
            Assert.Contains(item.ChangeSet.Lines, l => l.Target == "measure:Sales/Units" && l.Op == "set_measure_format");   // the family runs
            var line = item.ChangeSet.Lines.FirstOrDefault(l => l.Target == "measure:Sales/Due" && l.Op == "set_measure_format");
            Assert.True(line == null,
                $"the Model Plan offers \"Format as {line?.After}\" on a measure whose format is set by an expression, and approving the item then fails whole");

            var done = await e.ModelPlanActionAsync(item.Id, "approve", "human", item.ChangeSet.Digest);
            Assert.True(done.Performed, done.Sentence);
            var (dueFs, dueFse, unitsFs) = await f.Sessions.Require().ReadAsync(m =>
                (m.Tables["Sales"].Measures["Due"].FormatString, m.Tables["Sales"].Measures["Due"].FormatStringExpression, m.Tables["Sales"].Measures["Units"].FormatString));
            Assert.True(string.IsNullOrEmpty(dueFs));
            Assert.Equal("\"dd/mm/yyyy\"", dueFse);
            Assert.False(string.IsNullOrWhiteSpace(unitsFs), "the blank measure beside it is filled");
        }

        // 6. THE ADVICE ON A FIX THAT TARGETS THE EXPRESSION. The lane holds a fix that targets FormatStringExpression to
        // the fill-only rule too, and get_fix_prompt then returns advice. But the advice text is written for a static
        // format string only: it quotes the value of the property the fix targets as "the format" and always sends the
        // assistant to "set_property on FormatString" (LocalEngine.BpaGetFixPromptAsync). On a measure with the static
        // format 0.00 it says the format is "already set" to "(none)"; on a calculation item, which has no FormatString at
        // all, it names a write that cannot be made.

        private const string ExpressionRule =
            "[{\"ID\":\"TEST_DYNAMIC_FORMAT\",\"Name\":\"Dynamic format\",\"Category\":\"Formatting\",\"Severity\":3,\"Scope\":\"Measure, CalculationItem\","
            + "\"Expression\":\"true\",\"FixExpression\":\"FormatStringExpression = \\\"SELECTEDMEASUREFORMATSTRING ()\\\"\"}]";

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("mcp")]
        [InlineData("studio")]
        public async Task Verify_the_advice_on_a_fix_that_targets_the_expression_names_the_format_that_is_there(string door)
        {
            using var f = new DecisionFixture(TestEntitlements.Pro);
            var e = f.Engine;
            await e.CreateModelAsync("Northstar", 1604);
            await e.CreateTableAsync("Sales", "human");
            await e.CreateColumnAsync("table:Sales", "Amount", "Decimal", "Amount", "human");
            await e.CreateMeasureAsync("table:Sales", "Decimals", "SUM ( Sales[Amount] )", "human");
            var cg = await e.CreateCalculationGroupAsync("Share", "human");
            await e.CreateCalculationItemAsync(cg, "Of Total", "SELECTEDMEASURE ()", "human");
            await Mutate(f, m =>
            {
                m.Tables["Sales"].Measures["Decimals"].FormatString = "0.00";
                ((CalculationGroupTable)m.Tables["Share"]).CalculationItems["Of Total"].FormatStringExpression = "\"0.0%\"";
            });
            await e.LoadBpaRulesAsync(ExpressionRule, replace: true, "human");
            var card = await e.BpaScanAsync();
            var onMeasure = Assert.Single(card.Violations, v => v.RuleId == "TEST_DYNAMIC_FORMAT" && v.ObjectRef == "measure:Sales/Decimals");
            var onItem = Assert.Single(card.Violations, v => v.RuleId == "TEST_DYNAMIC_FORMAT" && v.ObjectRef.StartsWith("calcitem:", StringComparison.Ordinal));
            Assert.False(onMeasure.CanAutoFix);
            Assert.False(onItem.CanAutoFix);

            async Task<FixPrompt> Prompt(string objRef) => door == "mcp"
                ? await McpTools.GetFixPrompt(e, "bpa", "TEST_DYNAMIC_FORMAT", objRef)
                : await new EngineRpcTarget(e).bpaGetFixPrompt("TEST_DYNAMIC_FORMAT", objRef);
            var measurePrompt = await Prompt(onMeasure.ObjectRef);
            Assert.Equal("(advice)", measurePrompt.Tool);
            Assert.True(measurePrompt.Prompt.Contains("\"0.00\"", StringComparison.Ordinal),
                "the advice on a measure formatted 0.00 does not say that is its format: " + measurePrompt.Prompt);
            var itemPrompt = await Prompt(onItem.ObjectRef);
            Assert.Equal("(advice)", itemPrompt.Tool);
            Assert.False(itemPrompt.Prompt.Contains("on FormatString", StringComparison.Ordinal),
                "the advice on a calculation item, which has no FormatString, tells the assistant to set FormatString: " + itemPrompt.Prompt);
        }

        // 7. THE DEPLOY GATE AND THE MODEL'S OWN FORMAT RULES. The gate blocks on a finding of severity 2 or more that has
        // no automatic fix (LocalEngine.LivePublish.cs, the blocking set). The lane moved the two shipped measure rules to
        // severity 1 so their advice cannot block. A model that carries Tabular Editor's own rule file keeps both rules at
        // severity 2, and model rules replace the shipped ones by id, so the same findings, now advice with their fix
        // withheld, block the deploy. The engine calls each one "advice, not a fault to fix" while the gate refuses to ship
        // on it. Before the lane they never blocked (their fix was offered; Fix all then wrote over the format). The brief's
        // check: the deploy gate is never blocked by format advice. Measured the builder's way: the blocking count must be
        // the same with advice formats as with the house styles that clear them.

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Verify_format_advice_from_the_models_own_rules_never_blocks_the_deploy_gate()
        {
            using var f = new DecisionFixture(TestEntitlements.Pro);
            var e = f.Engine;
            await e.CreateModelAsync("Northstar", 1604);
            await e.CreateTableAsync("Sales", "human");
            await e.CreateColumnAsync("table:Sales", "Amount", "Decimal", "Amount", "human");
            await e.CreateMeasureAsync("table:Sales", "Decimals", "SUM ( Sales[Amount] ) * 2", "human");
            await e.CreateMeasureAsync("table:Sales", "Rate", "DIVIDE ( SUM ( Sales[Amount] ), 3 )", "human");
            await e.CreateMeasureAsync("table:Sales", "Dated", "MAX ( Sales[Amount] ) + 4", "human");
            await e.LoadBpaRulesAsync(UpstreamRules, replace: true, "human");
            await Mutate(f, m =>
            {
                m.Tables["Sales"].Measures["Decimals"].FormatString = "0.00";
                m.Tables["Sales"].Measures["Rate"].FormatString = "0%";
                m.Tables["Sales"].Measures["Dated"].FormatString = "Long Date";
            });
            var card = await e.BpaScanAsync();
            Assert.Equal(3, card.Violations.Count(v => (v.RuleId == "INTEGER_FORMATTING" || v.RuleId == "PERCENTAGE_FORMATTING") && !v.CanAutoFix && v.Severity == 2));
            var advised = await e.DeployGateAsync(null, "human");

            await Mutate(f, m =>
            {
                m.Tables["Sales"].Measures["Decimals"].FormatString = "#,0";
                m.Tables["Sales"].Measures["Rate"].FormatString = "#,0.0%;-#,0.0%;#,0.0%";
                m.Tables["Sales"].Measures["Dated"].FormatString = "#,0";
            });
            Assert.DoesNotContain((await e.BpaScanAsync()).Violations, v => v.RuleId is "INTEGER_FORMATTING" or "PERCENTAGE_FORMATTING");
            var cleared = await e.DeployGateAsync(null, "human");

            Assert.True(cleared.BpaBlocking == advised.BpaBlocking,
                $"format advice from the model's own rules blocks the deploy gate: {advised.BpaBlocking} blocking with the advice formats, "
                + $"{cleared.BpaBlocking} with the house styles. {string.Join(" | ", advised.Blockers)}");
        }

        // 8. THE FORMAT FILL RULE (coordinator, 28 Sep 22:25), which narrows the ruling this lane implements. An automatic
        // fill is allowed only where the engine can prove what kind of number the object holds: a whole-number count, or a
        // SUM, MIN or MAX of an integer column, may get #,0, and so may an Int64 column that is not a key, ID, date key, code
        // or flag. Nothing else: not a DIVIDE or any ratio (a 73% utilisation must never read 1), not a SUM of a decimal or
        // currency column (cents and decimals must never be lost), not a measure the engine cannot classify. The lane fills
        // EVERY blank measure with #,0 through INTEGER_FORMATTING, and every blank date column through DATECOLUMN_ and
        // MONTHCOLUMN_FORMATSTRING, on one press, Fix all, apply_safe_fixes and the change plan, on both doors and both tiers
        // (the verifier's probe on a model shaped like the check's raw CSV: 14 of 19 blank measures filled that the rule
        // forbids, and three date columns). Each case is one blank measure beside base measures that already have formats.

        // Lean suite: build once for the class, open a copy per test.
        private async Task<DecisionFixture> TimesheetsAsync()
        {
            var f = new DecisionFixture(TestEntitlements.Pro);
            await _templates.OpenCopyAsync(f, "timesheets", BuildTimesheetsAsync, "Timesheets.bim");
            return f;
        }

        private static async Task BuildTimesheetsAsync(DecisionFixture f)
        {
            var e = f.Engine;
            await e.CreateModelAsync("Timesheets", 1604);
            await e.CreateTableAsync("Timesheets", "human");
            await e.CreateColumnAsync("table:Timesheets", "EMP_ID", "Int64", "EMP_ID", "human");
            await e.CreateColumnAsync("table:Timesheets", "HOURS_BILLED", "Decimal", "HOURS_BILLED", "human");
            await e.CreateColumnAsync("table:Timesheets", "HOURS_TOTAL", "Decimal", "HOURS_TOTAL", "human");
            await e.CreateColumnAsync("table:Timesheets", "AMT", "Decimal", "AMT", "human");
            await e.CreateColumnAsync("table:Timesheets", "RATE", "Double", "RATE", "human");
            await e.CreateColumnAsync("table:Timesheets", "WORK_DATE", "DateTime", "WORK_DATE", "human");
            await e.CreateMeasureAsync("table:Timesheets", "HRS_BILL", "SUM ( Timesheets[HOURS_BILLED] )", "human");
            await e.CreateMeasureAsync("table:Timesheets", "HRS_TOT", "SUM ( Timesheets[HOURS_TOTAL] )", "human");
            await e.CreateMeasureAsync("table:Timesheets", "REVENUE", "SUM ( Timesheets[AMT] )", "human");
            await Mutate(f, m =>
            {
                var t = m.Tables["Timesheets"];
                t.Measures["HRS_BILL"].FormatString = "#,0.00";
                t.Measures["HRS_TOT"].FormatString = "#,0.00";
                t.Measures["REVENUE"].FormatString = "$#,0.00";
                foreach (var c in t.Columns.Where(c => c.Name != "WORK_DATE")) c.FormatString = "0";
                t.Columns["WORK_DATE"].FormatString = "General Date";
            });
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("UTIL_PCT", "DIVIDE ( [HRS_BILL], [HRS_TOT] )")]
        [InlineData("UTIL_RATIO", "[HRS_BILL] / [HRS_TOT]")]
        [InlineData("HOURS", "SUM ( Timesheets[HOURS_BILLED] )")]
        [InlineData("AMOUNT", "SUM ( Timesheets[AMT] )")]
        [InlineData("AVG_RATE", "AVERAGE ( Timesheets[RATE] )")]
        [InlineData("LAST_WORK", "MAX ( Timesheets[WORK_DATE] )")]
        [InlineData("MARGIN_EST", "[REVENUE] * 0.3")]
        [InlineData("TARGET", "0.73")]
        public async Task Verify_no_automatic_fill_changes_what_a_measure_shows(string name, string dax)
        {
            using var f = await TimesheetsAsync();
            await f.Engine.CreateMeasureAsync("table:Timesheets", name, dax, "human");
            var target = "measure:Timesheets/" + name;

            var offered = (await f.Engine.BpaScanAsync()).Violations.Where(v => v.ObjectRef == target && v.CanAutoFix && v.RuleId.EndsWith("_FORMATTING", StringComparison.Ordinal)).Select(v => v.RuleId).ToArray();
            Assert.True(offered.Length == 0, $"{name} = {dax} is offered an automatic format fill by {string.Join(", ", offered)}");

            await PressEverythingAsync(f, "INTEGER_FORMATTING", target);
            var written = await f.Sessions.Require().ReadAsync(m => m.Tables["Timesheets"].Measures[name].FormatString);
            Assert.True(string.IsNullOrEmpty(written),
                $"{name} = {dax} was given the format \"{written}\" automatically, which changes the value a person reads");
        }

        // Default tier: the witness row of the sweep form, through the same body.
        [Fact]
        public Task Verify_no_automatic_fill_changes_what_a_measure_shows_sample() =>
            Verify_no_automatic_fill_changes_what_a_measure_shows("UTIL_PCT", "DIVIDE ( [HRS_BILL], [HRS_TOT] )");

        // Default tier, the rule itself: for every shape of the sweep theory the fill rule refuses the measure (the engine paths are
        // the sweep form; one row of them runs in the sample above).
        [Fact]
        public async Task Verify_no_automatic_fill_changes_what_a_measure_shows_rule_only()
        {
            using var f = await TimesheetsAsync();
            var shapes = new (string Name, string Dax)[]
            {
                ("UTIL_PCT", "DIVIDE ( [HRS_BILL], [HRS_TOT] )"), ("UTIL_RATIO", "[HRS_BILL] / [HRS_TOT]"),
                ("HOURS", "SUM ( Timesheets[HOURS_BILLED] )"), ("AMOUNT", "SUM ( Timesheets[AMT] )"),
                ("AVG_RATE", "AVERAGE ( Timesheets[RATE] )"), ("LAST_WORK", "MAX ( Timesheets[WORK_DATE] )"),
                ("MARGIN_EST", "[REVENUE] * 0.3"), ("TARGET", "0.73"),
            };
            await Mutate(f, m => { foreach (var s in shapes) m.Tables["Timesheets"].AddMeasure(s.Name, s.Dax); });
            var filled = await f.Sessions.Require().ReadAsync(m =>
                shapes.Where(s => FormatFill.MayFill(m.Tables["Timesheets"].Measures[s.Name], "#,0")).Select(s => s.Name).ToArray());
            Assert.True(filled.Length == 0, "the fill rule allows an automatic #,0 on: " + string.Join(", ", filled));
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("Order Date", "DATECOLUMN_FORMATSTRING")]
        [InlineData("Order Month", "MONTHCOLUMN_FORMATSTRING")]
        public async Task Verify_no_automatic_fill_on_a_date_column(string column, string rule)
        {
            // The rule's own list names the only column that may be filled: an Int64 column that is not a key, ID, date
            // key, code or flag. A date column holding times shows them until "yyyy-mm-dd" is written over its blank format.
            using var f = await TimesheetsAsync();
            await f.Engine.CreateColumnAsync("table:Timesheets", column, "DateTime", column.Replace(" ", ""), "human");
            var target = "column:Timesheets/" + column;
            var finding = (await f.Engine.BpaScanAsync()).Violations.FirstOrDefault(v => v.ObjectRef == target && v.RuleId == rule);
            Assert.True(finding == null || !finding.CanAutoFix, $"{rule} offers an automatic format fill on the date column {column}");

            await PressEverythingAsync(f, rule, target);
            var written = await f.Sessions.Require().ReadAsync(m => m.Tables["Timesheets"].Columns[column].FormatString);
            Assert.True(string.IsNullOrEmpty(written), $"the date column {column} was given the format \"{written}\" automatically");
        }

        private const string UpstreamRules =
            "[{\"ID\":\"PERCENTAGE_FORMATTING\",\"Name\":\"[Formatting] Percentages should be formatted with thousands separators and 1 decimal\","
            + "\"Category\":\"Formatting\",\"Severity\":2,\"Scope\":\"Measure\","
            + "\"Expression\":\"FormatString.Contains(\\\"%\\\") and FormatString <> \\\"#,0.0%;-#,0.0%;#,0.0%\\\"\","
            + "\"FixExpression\":\"FormatString = \\\"#,0.0%;-#,0.0%;#,0.0%\\\"\",\"CompatibilityLevel\":1200},"
            + "{\"ID\":\"INTEGER_FORMATTING\",\"Name\":\"[Formatting] Whole numbers should be formatted with thousands separators and no decimals\","
            + "\"Category\":\"Formatting\",\"Severity\":2,\"Scope\":\"Measure\","
            + "\"Expression\":\"not FormatString.Contains(\\\"$\\\") and not FormatString.Contains(\\\"%\\\") and not (FormatString = \\\"#,0\\\" or FormatString = \\\"#,0.0\\\")\","
            + "\"FixExpression\":\"FormatString = \\\"#,0\\\"\",\"CompatibilityLevel\":1200}]";
    }
}
