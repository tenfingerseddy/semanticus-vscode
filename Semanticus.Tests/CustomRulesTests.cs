using System;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Analysis;
using Semanticus.Engine;
using Semanticus.Engine.Entitlement;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// Custom rule authoring — the readiness custom-rule engine (load/merge/reset + undo, the
    /// Applicable/AppliesTo scoring conventions incl. the dormant-when-empty / no-inflation pins, built-in id
    /// collision refusal, finding provenance, waivers on custom findings) and the validate_rule authoring
    /// preview for BOTH rule kinds (teaching errors from the real parser, honest test-run counts).
    /// </summary>
    public sealed class CustomRulesTests
    {
        private sealed class Fake : IEntitlement
        {
            public bool IsPro { get; }
            public EntitlementInfo Info { get; }
            public Fake(bool pro) { IsPro = pro; Info = new EntitlementInfo { Tier = pro ? "pro" : "free" }; }
        }

        // A KPI-folder rule: population = the two measures in the "KPI" folder; violation = the undescribed one.
        private const string KpiRule =
            "{\"ID\":\"ORG-KPI-DESC\",\"Name\":\"KPI measures need descriptions\",\"Category\":\"Descriptions\"," +
            "\"Severity\":\"Medium\",\"Scope\":\"Measure\",\"AppliesTo\":\"DisplayFolder = \\\"KPI\\\"\"," +
            "\"Expression\":\"string.IsNullOrEmpty(Description)\",\"Message\":\"%object% needs a description\"," +
            "\"FixKind\":\"AiContent\"}";

        private static async Task<(LocalEngine engine, SessionManager sessions)> FreshAsync(bool pro = true)
        {
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, new Fake(pro));
            await engine.CreateModelAsync("CustomRules", 1601);
            var t = await engine.CreateTableAsync("Facts", "human");
            await engine.CreateColumnAsync(t, "Amount", "Decimal", "Amount", "human");
            await engine.CreateMeasureAsync(t, "KPI Alpha", "SUM(Facts[Amount])", "human");
            await engine.CreateMeasureAsync(t, "KPI Beta", "SUM(Facts[Amount])", "human");
            await engine.CreateMeasureAsync(t, "Other Gamma", "SUM(Facts[Amount])", "human");
            await engine.SetObjectPropertyAsync("measure:Facts/KPI Alpha", "DisplayFolder", "KPI", "human");
            await engine.SetObjectPropertyAsync("measure:Facts/KPI Beta", "DisplayFolder", "KPI", "human");
            await engine.SetDescriptionAsync("measure:Facts/KPI Beta", "Beta is described.", "human");
            return (engine, sessions);
        }

        private static async Task<int> CustomCountAsync(LocalEngine engine) =>
            (await engine.GetCustomRulesAsync()).Readiness.Length;

        // ---- load / merge / replace / reset round-trip + undo -------------------------------------

        [Fact]
        public async Task Load_merge_replace_reset_round_trip_with_undo()
        {
            var (engine, _) = await FreshAsync();

            var info = await engine.LoadReadinessRulesAsync(KpiRule, replace: false, "human");
            Assert.Equal(1, info.ModelRules);
            Assert.Equal(info.BuiltinRules + 1, info.ActiveRules);      // additive: custom never overrides a built-in
            Assert.Equal("inline", info.Source);

            var second = KpiRule.Replace("ORG-KPI-DESC", "ORG-KPI-DESC-2");
            await engine.LoadReadinessRulesAsync(second, replace: false, "human");
            Assert.Equal(2, await CustomCountAsync(engine));

            await engine.LoadReadinessRulesAsync(KpiRule, replace: true, "human");
            Assert.Equal(1, await CustomCountAsync(engine));

            await engine.UndoAsync("human");                             // undo the replace → both rules back
            Assert.Equal(2, await CustomCountAsync(engine));
            await engine.RedoAsync("human");                             // redo → the replaced single-rule set
            Assert.Equal(1, await CustomCountAsync(engine));

            var reset = await engine.ResetReadinessRulesAsync("human");
            Assert.Equal(0, reset.ModelRules);
            Assert.Equal(0, await CustomCountAsync(engine));
            Assert.Empty((await engine.AiReadinessScanAsync()).Findings.Where(f => f.RuleId == "ORG-KPI-DESC"));

            await engine.UndoAsync("human");                             // undo the reset → the rule returns
            Assert.Equal(1, await CustomCountAsync(engine));
        }

        // ---- scoring: AppliesTo = the Applicable population; violations inside it; provenance ------

        [Fact]
        public async Task AppliesTo_scopes_the_applicable_population_and_findings_carry_provenance()
        {
            var (engine, _) = await FreshAsync();
            var before = await engine.AiReadinessScanAsync();
            var descBefore = before.Categories.Single(c => c.Category == "Descriptions");

            await engine.LoadReadinessRulesAsync(KpiRule, replace: false, "human");
            var after = await engine.AiReadinessScanAsync();

            var finding = Assert.Single(after.Findings.Where(f => f.RuleId == "ORG-KPI-DESC"));
            Assert.True(finding.Custom, "a custom rule's finding must carry custom provenance");
            Assert.Contains("KPI Alpha", finding.ObjectName);            // the undescribed KPI measure, not Gamma
            Assert.Contains("needs a description", finding.Message);
            Assert.All(after.Findings.Where(f => f.RuleId != "ORG-KPI-DESC"),
                f => Assert.False(f.Custom, "built-in findings must not be tagged custom"));

            // Applicable = the AppliesTo population (the 2 KPI-folder measures), NOT all measures.
            var descAfter = after.Categories.Single(c => c.Category == "Descriptions");
            Assert.Equal(descBefore.Applicable + 2, descAfter.Applicable);
            Assert.Equal(descBefore.Violations + 1, descAfter.Violations);
            Assert.Empty(after.RuleErrors);
        }

        // ---- custom-rule copy safety: arbitrary user text is never scanned or mutated (copy review round 3) --------

        [Fact]
        public async Task Custom_rule_message_with_op_looking_parentheticals_flows_through_untouched()
        {
            var (engine, _) = await FreshAsync();
            // A custom message whose literal text contains BOTH an op-looking parenthetical "(enable_qna)" and real
            // evidence "(order_date, ship_date)". Custom rules pass no out-of-band op, so Compose must leave the whole
            // string byte-identical in Message and set DisplayMessage null (the UI then renders Message verbatim). An
            // earlier in-band/allowlist design would have eaten "(enable_qna)"; this must not.
            var rule =
                "{\"ID\":\"ORG-PARENS\",\"Name\":\"parens\",\"Category\":\"Descriptions\"," +
                "\"Severity\":\"Medium\",\"Scope\":\"Measure\",\"AppliesTo\":\"DisplayFolder = \\\"KPI\\\"\"," +
                "\"Expression\":\"string.IsNullOrEmpty(Description)\"," +
                "\"Message\":\"%object% is disabled (enable_qna); evidence (order_date, ship_date)\"," +
                "\"FixKind\":\"AiContent\"}";
            await engine.LoadReadinessRulesAsync(rule, replace: false, "human");

            var f = Assert.Single((await engine.AiReadinessScanAsync()).Findings.Where(x => x.RuleId == "ORG-PARENS"));
            Assert.Contains("(enable_qna)", f.Message);                 // op-looking custom text is NOT stripped
            Assert.Contains("(order_date, ship_date)", f.Message);      // real evidence survives
            Assert.Null(f.DisplayMessage);                              // no out-of-band op ⇒ null, never a mangled string
        }

        // ---- the dormant / no-inflation pins -------------------------------------------------------

        [Fact]
        public async Task Empty_population_is_dormant_and_never_inflates_any_category()
        {
            var (engine, _) = await FreshAsync();
            var before = await engine.AiReadinessScanAsync();

            // Population filter matches nothing → Applicable=0 → dormant. Target a category (Synonyms) that has
            // no active rules on this bare model, so any inflation would be UNMISTAKABLE (HasRules would flip).
            var dormant = KpiRule
                .Replace("ORG-KPI-DESC", "ORG-DORMANT")
                .Replace("Descriptions", "Synonyms")
                .Replace("DisplayFolder = \\\"KPI\\\"", "DisplayFolder = \\\"NO-SUCH-FOLDER\\\"");
            await engine.LoadReadinessRulesAsync(dormant, replace: false, "human");
            var after = await engine.AiReadinessScanAsync();

            Assert.Empty(after.Findings.Where(f => f.RuleId == "ORG-DORMANT"));
            Assert.Equal(before.Overall, after.Overall);
            Assert.Equal(before.Grade, after.Grade);
            foreach (var b in before.Categories)
            {
                var a = after.Categories.Single(c => c.Category == b.Category);
                Assert.Equal(b.HasRules, a.HasRules);    // a dormant rule never activates a category…
                Assert.Equal(b.Score, a.Score);          // …and never moves its score
                Assert.Equal(b.Applicable, a.Applicable);
                Assert.Equal(b.Violations, a.Violations);
            }
        }

        // A rule can COMPILE clean yet throw on real objects (here: Substring past the end of every name).
        // The load path must run the same test-run evaluation validate_rule does — parse-only validation would
        // persist a rule every subsequent scan chokes on.
        [Fact]
        public async Task Load_refuses_a_rule_that_compiles_but_throws_at_evaluation()
        {
            var (engine, _) = await FreshAsync();
            var throwing = KpiRule.Replace("string.IsNullOrEmpty(Description)", "Name.Substring(100) = \\\"x\\\"");
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => engine.LoadReadinessRulesAsync(throwing, replace: false, "human"));
            Assert.Contains("ORG-KPI-DESC", ex.Message);
            Assert.Contains("failed to evaluate", ex.Message);           // the same teaching detail validate_rule gives
            Assert.Contains("validate_rule", ex.Message);                // …and points at the preview op for iteration
            Assert.Equal(0, await CustomCountAsync(engine));             // nothing landed
            Assert.Empty((await engine.AiReadinessScanAsync()).RuleErrors);
        }

        // Scoring integrity: an evaluation error mid-scan (a hand-edited/stale rule the strict load never saw)
        // must leave the rule DORMANT — the population counted before the throw would otherwise read as a ~100%
        // pass and could activate its category. The error surfaces via RuleErrors only.
        [Fact]
        public async Task Rule_that_throws_mid_evaluation_goes_dormant_and_never_moves_the_score()
        {
            var (engine, sessions) = await FreshAsync();
            var before = await engine.AiReadinessScanAsync();

            // Compiles, then throws while evaluating. Hand-set on the annotation (the load op refuses it now).
            // Target a category (Synonyms) with no active rules on this bare model, so any phantom activation
            // would be UNMISTAKABLE (HasRules would flip).
            await sessions.Current.MutateAsync("human", "hand-edit", m =>
                m.SetAnnotation(CustomReadinessRuleSet.AnnotationName,
                    "[{\"ID\":\"ORG-THROWS\",\"Category\":\"Synonyms\",\"Scope\":\"Measure\",\"Expression\":\"Name.Substring(100) = \\\"x\\\"\"}]"));
            var after = await engine.AiReadinessScanAsync();

            Assert.Contains(after.RuleErrors, e => e.Contains("ORG-THROWS") && e.Contains("failed to evaluate"));
            Assert.Empty(after.Findings.Where(f => f.RuleId == "ORG-THROWS"));
            Assert.Equal(before.Overall, after.Overall);
            Assert.Equal(before.Grade, after.Grade);
            foreach (var b in before.Categories)
            {
                var a = after.Categories.Single(c => c.Category == b.Category);
                Assert.Equal(b.HasRules, a.HasRules);    // a broken rule never activates a category…
                Assert.Equal(b.Score, a.Score);          // …never moves its score…
                Assert.Equal(b.Applicable, a.Applicable); // …and leaves no phantom pass population behind
                Assert.Equal(b.Violations, a.Violations);
            }
        }

        // ---- built-in id collision = refusal with teaching ------------------------------------------

        [Fact]
        public async Task Builtin_id_collision_is_refused_with_teaching()
        {
            var (engine, _) = await FreshAsync();
            var colliding = KpiRule.Replace("ORG-KPI-DESC", "DESC-MEASURE");
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => engine.LoadReadinessRulesAsync(colliding, replace: false, "human"));
            Assert.Contains("DESC-MEASURE", ex.Message);
            Assert.Contains("built-in", ex.Message);
            Assert.Contains("validate_rule", ex.Message);                // the failure teaches the preview op
            Assert.Equal(0, await CustomCountAsync(engine));             // nothing landed
        }

        // A hand-edited annotation (bypassing the load op's strict validation) fails LOUD on the scorecard:
        // the colliding/unparseable rule is skipped and RuleErrors says so — never a silent pass, never a hijack.
        [Fact]
        public async Task Hand_edited_annotation_surfaces_rule_errors_instead_of_silently_passing()
        {
            var (engine, sessions) = await FreshAsync();
            await sessions.Current.MutateAsync("human", "hand-edit", m =>
                m.SetAnnotation(CustomReadinessRuleSet.AnnotationName,
                    "[{\"ID\":\"DESC-MEASURE\",\"Category\":\"Descriptions\",\"Scope\":\"Measure\",\"Expression\":\"true\"}]"));
            var card = await engine.AiReadinessScanAsync();
            Assert.Contains(card.RuleErrors, e => e.Contains("DESC-MEASURE") && e.Contains("built-in"));

            await sessions.Current.MutateAsync("human", "hand-edit", m =>
                m.SetAnnotation(CustomReadinessRuleSet.AnnotationName, "{not json"));
            card = await engine.AiReadinessScanAsync();
            Assert.Contains(card.RuleErrors, e => e.Contains("unparseable") && e.Contains("reset_readiness_rules"));
        }

        // ---- waivers work on custom findings like any finding ---------------------------------------

        [Fact]
        public async Task Waiver_applies_to_a_custom_finding()
        {
            var (engine, _) = await FreshAsync();
            await engine.LoadReadinessRulesAsync(KpiRule, replace: false, "human");
            var card = await engine.AiReadinessScanAsync();
            var f = card.Findings.Single(x => x.RuleId == "ORG-KPI-DESC");

            await engine.WaiveFindingAsync("air", "ORG-KPI-DESC", f.ObjectRef, "KPI descriptions arrive next sprint", "human");
            var after = await engine.AiReadinessScanAsync();
            var waived = after.Findings.Single(x => x.RuleId == "ORG-KPI-DESC");
            Assert.True(waived.Waived, "the custom finding must be waivable");
            Assert.Equal("KPI descriptions arrive next sprint", waived.WaiverReason);

            var desc = after.Categories.Single(c => c.Category == "Descriptions");
            var descWith = card.Categories.Single(c => c.Category == "Descriptions");
            Assert.Equal(descWith.Violations - 1, desc.Violations);      // out of the score…
            Assert.Equal(descWith.Waived + 1, desc.Waived);              // …but surfaced and counted as waived
        }

        // ---- validate_rule: teaching errors + the honest test-run preview ---------------------------

        [Fact]
        public async Task Validate_rule_readiness_teaches_and_previews()
        {
            var (engine, _) = await FreshAsync();

            // A valid rule: real compile + a test run with counts and sample names, clearly a preview.
            var ok = await engine.ValidateRuleAsync("readiness", KpiRule);
            Assert.True(ok.AllValid);
            var check = Assert.Single(ok.Rules);
            Assert.Equal(2, check.Applicable);
            Assert.Equal(1, check.Violations);
            Assert.Contains(check.Sample, s => s.Contains("KPI Alpha"));
            Assert.Contains("nothing saved", check.Note, StringComparison.OrdinalIgnoreCase);

            // A bad expression: the real parser's error, pointing at the position.
            var bad = await engine.ValidateRuleAsync("readiness", KpiRule.Replace("string.IsNullOrEmpty(Description)", "Frobnicate(("));
            Assert.False(bad.AllValid);
            Assert.Contains(bad.Rules[0].Errors, e => e.Contains("position"));

            // An invalid category lists the valid ones.
            var cat = await engine.ValidateRuleAsync("readiness", KpiRule.Replace("Descriptions", "Sparkles"));
            Assert.Contains(cat.Rules[0].Errors, e => e.Contains("Sparkles") && e.Contains("Naming") && e.Contains("DataAgentConfig"));

            // SafeFix is refused for custom rules — the error names the advisory kinds.
            var fix = await engine.ValidateRuleAsync("readiness", KpiRule.Replace("AiContent", "SafeFix"));
            Assert.Contains(fix.Rules[0].Errors, e => e.Contains("SafeFix") && e.Contains("AiContent"));

            // A dormant rule says so, honestly.
            var dormant = await engine.ValidateRuleAsync("readiness",
                KpiRule.Replace("DisplayFolder = \\\"KPI\\\"", "DisplayFolder = \\\"NO-SUCH-FOLDER\\\""));
            Assert.True(dormant.Rules[0].Valid);
            Assert.True(dormant.Rules[0].Dormant);
            Assert.Contains("dormant", dormant.Rules[0].Note);

            // An unknown kind teaches the two valid ones.
            var ex = await Assert.ThrowsAsync<ArgumentException>(() => engine.ValidateRuleAsync("sparkly", KpiRule));
            Assert.Contains("'bpa'", ex.Message);
            Assert.Contains("'readiness'", ex.Message);
        }

        [Fact]
        public async Task Validate_rule_bpa_previews_and_flags_standard_overrides()
        {
            var (engine, _) = await FreshAsync();

            var rule = "{\"ID\":\"ORG_MEASURE_DESC\",\"Name\":\"Measures need descriptions\",\"Category\":\"Metadata\"," +
                       "\"Severity\":2,\"Scope\":\"Measure\",\"Expression\":\"string.IsNullOrEmpty(Description)\"}";
            var ok = await engine.ValidateRuleAsync("bpa", rule);
            Assert.True(ok.AllValid);
            Assert.Equal(3, ok.Rules[0].Applicable);                     // all 3 measures (BPA has no AppliesTo)
            Assert.Equal(2, ok.Rules[0].Violations);                     // Alpha + Gamma undescribed
            Assert.Contains("load_bpa_rules", ok.Note);

            var bad = await engine.ValidateRuleAsync("bpa", rule.Replace("string.IsNullOrEmpty(Description)", "NoSuchProp == 1"));
            Assert.False(bad.AllValid);
            Assert.NotEmpty(bad.Rules[0].Errors);

            // Re-using a bundled standard rule id is legal for BPA (model rules win) — but the preview SAYS so.
            var stdId = BpaRuleSet.Standard()[0].ID;
            var over = await engine.ValidateRuleAsync("bpa", rule.Replace("ORG_MEASURE_DESC", stdId));
            Assert.True(over.Rules[0].Valid);
            Assert.Contains("OVERRIDE", over.Rules[0].Note);
        }

        // ---- BPA provenance: violations from model-embedded rules are tagged custom -----------------

        [Fact]
        public async Task Bpa_violations_from_model_rules_carry_provenance()
        {
            var (engine, _) = await FreshAsync();
            await engine.LoadBpaRulesAsync(
                "{\"ID\":\"ORG_TEST_CUSTOM\",\"Name\":\"No Gamma measures\",\"Category\":\"Naming\",\"Severity\":2," +
                "\"Scope\":\"Measure\",\"Expression\":\"Name.Contains(\\\"Gamma\\\")\"}", false, "human");
            var card = await engine.BpaScanAsync();
            var custom = card.Violations.Where(v => v.RuleId == "ORG_TEST_CUSTOM").ToArray();
            Assert.NotEmpty(custom);
            Assert.All(custom, v => Assert.True(v.Custom));
            Assert.All(card.Violations.Where(v => v.RuleId != "ORG_TEST_CUSTOM"), v => Assert.False(v.Custom));
        }

        // ---- dry_run denies the rule-annotation writers (the load_bpa_rules family) -----------------

        [Fact]
        public async Task Dry_run_denies_readiness_rule_loads_with_teaching()
        {
            var (engine, _) = await FreshAsync();
            var load = await Assert.ThrowsAsync<InvalidOperationException>(
                () => engine.DryRunOpAsync("load_readiness_rules", "{\"source\":\"[]\"}"));
            Assert.Contains("cannot rehearse", load.Message);
            var reset = await Assert.ThrowsAsync<InvalidOperationException>(
                () => engine.DryRunOpAsync("reset_readiness_rules", null));
            Assert.Contains("cannot rehearse", reset.Message);
        }
    }
}
