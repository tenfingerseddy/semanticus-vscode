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
    /// The map-lies defect behind the Microsoft-corpus switch: <c>BpaAnalyzer.Analyze</c> evaluated a rule with
    /// <c>collection.Where(rule.Expression)</c>, which is LAZY. A user expression that throws part way through a scope
    /// aborted that scope's enumeration, so the scan kept whatever prefix of violations it had already collected and
    /// returned it as if the scope had been evaluated to completion. The only hint was the <c>RuleErrors</c> side
    /// channel, which every count, every summary and the deploy gate ignored — a scan quietly under-reporting and
    /// reading clean, which is the one failure mode this product may never have.
    ///
    /// WHAT CHANGED, STATED PRECISELY, because the paragraph above reads as though the prefix is now thrown away
    /// and IT IS NOT. Measured 2026-07-30: after a mid-scope throw the scan still reports the violations it had
    /// already matched (on this fixture, one, against [Total Sales]). What changed is that the unknown now travels
    /// WITH them, in the scorecard, the token-light summary and the deploy gate, instead of only in a side channel
    /// nothing read. Keeping a violation we genuinely found is defensible; presenting it as a finished scope was
    /// not. If discarding the prefix is ever decided to be the right behaviour, that is an ENGINE change and this
    /// docstring plus <c>An_unknown_never_claims_how_many_objects_were_evaluated_before_the_throw</c> must move
    /// together with it. Do not let this comment drift back into claiming a discard the code does not perform.
    ///
    /// The contract these tests pin: a rule that throws part way through a scope is UNKNOWN for that scope,
    /// explicitly, in the scorecard AND in the token-light summary AND in the deploy gate's verdict. We do NOT claim
    /// "evaluated N of M": the throw happens inside a lazy predicate, so the number of objects visited before it is
    /// not observable from here, and inventing a count would be its own lie. Objects-in-scope (M) IS countable
    /// without running the expression, so that is all we report.
    /// </summary>
    public sealed class BpaUnknownScopeTests
    {
        private sealed class Fake : IEntitlement
        {
            public bool IsPro => true;
            public EntitlementInfo Info => new EntitlementInfo { Tier = "pro" };
        }

        // A rule whose expression throws for SOME objects and not others — the mid-scope case. Name.Substring(3)
        // throws IndexOutOfRangeException on any name shorter than 3 characters. Deliberately NOT the compatibility
        // -level trigger (ObjectLevelSecurity below CL 1400), because that one throws on the first object and would
        // not distinguish "aborted part way" from "never started".
        private const string ThrowsMidScope =
            "[{\"ID\":\"TEST_THROWS_MID_SCOPE\",\"Name\":\"throws part way through the scope\",\"Category\":\"Test\"," +
            "\"Severity\":3,\"Scope\":\"Measure\",\"Expression\":\"Name.Substring(3).Length >= 0\"}]";

        // The same clean fixture the deploy-gate tests use: nothing in the Microsoft corpus blocks on it.
        private static async Task<LocalEngine> CleanModelAsync()
        {
            var engine = new LocalEngine(new SessionManager(), new Fake());
            await engine.CreateModelAsync("Unknowns", 1604);
            var t = await engine.CreateTableAsync("Sales", "human");
            var amt = await engine.CreateColumnAsync(t, "Sales Amount", "Decimal", "Sales Amount", "human");
            await engine.CreateColumnAsync(t, "Customer Name", "String", "Customer Name", "human");
            await engine.SetColumnMetadataAsync(amt, true, null, null, null, "human");
            var mref = await engine.CreateMeasureAsync(t, "Total Sales", "SUM ( Sales[Sales Amount] )", "human");
            await engine.SetMeasureFormatAsync(mref, "#,0", "human");
            await engine.SetDescriptionAsync(mref, "The sum of all sales amounts across the model.", "human");
            var dt = await engine.CreateTableAsync("Date", "human");
            var dkey = await engine.CreateColumnAsync(dt, "Date", "DateTime", "Date", "human");
            await engine.SetObjectPropertyAsync(dkey, "IsKey", "true", "human");
            await engine.SetObjectPropertyAsync(dkey, "FormatString", "yyyy-mm-dd", "human");
            await engine.MarkDateTableAsync(dt, "Date", "human");
            return engine;
        }

        // Add the object the expression throws on: a measure whose name is shorter than 3 characters. Kept separate
        // from the fixture so a test can choose whether the rule is broken BEFORE or AFTER it was loaded.
        private static async Task AddShortNamedMeasureAsync(LocalEngine engine)
        {
            var m = await engine.CreateMeasureAsync("table:Sales", "AB", "1", "human");
            await engine.SetMeasureFormatAsync(m, "#,0", "human");
            await engine.SetDescriptionAsync(m, "A deliberately short measure name for the throw case.", "human");
        }

        // ---- 1) the scorecard reports UNKNOWN, not a quietly-truncated scope ----------------------------------------

        [Fact]
        public async Task A_rule_that_throws_part_way_through_a_scope_is_reported_as_unknown()
        {
            using var engine = await CleanModelAsync();
            await AddShortNamedMeasureAsync(engine);
            await engine.LoadBpaRulesAsync(ThrowsMidScope, replace: true, "human");
            var card = await engine.BpaScanAsync();

            // The side channel still carries the message (existing behaviour, kept for the authoring/validate lane)...
            Assert.Contains(card.RuleErrors, e => e.Contains("TEST_THROWS_MID_SCOPE"));
            // ...but the answer is now first-class: the rule/scope pair is UNKNOWN, and it says so where counts live.
            var u = Assert.Single(card.Unknowns, x => x.RuleId == "TEST_THROWS_MID_SCOPE");
            Assert.Equal("Measure", u.Scope);
            Assert.Equal(3, u.Severity);
            Assert.False(u.CanAutoFix);
            Assert.True(u.BlocksGate, "a severity-3 rule with no auto-fix would block the gate, so its unknown must too");
            Assert.True(u.ObjectsInScope >= 2, "objects-in-scope is countable without running the expression");
            Assert.False(string.IsNullOrWhiteSpace(u.Reason));
            Assert.Equal(card.Unknowns.Length, card.UnknownCount);
            Assert.True(card.UnknownCount > 0);
        }

        // We must never present a visited count we cannot substantiate.
        [Fact]
        public async Task An_unknown_never_claims_how_many_objects_were_evaluated_before_the_throw()
        {
            using var engine = await CleanModelAsync();
            await AddShortNamedMeasureAsync(engine);
            await engine.LoadBpaRulesAsync(ThrowsMidScope, replace: true, "human");
            var card = await engine.BpaScanAsync();
            var u = Assert.Single(card.Unknowns, x => x.RuleId == "TEST_THROWS_MID_SCOPE");
            // The whole rule/scope is unknown. There is no "evaluated N of M" field to over-claim with, and the
            // reason states the coverage honestly rather than implying the scope was finished.
            Assert.Contains("could not be evaluated", u.Reason, StringComparison.OrdinalIgnoreCase);

            // The prefix IS still retained, and that is the point of the next two lines. `Where(expr)` is lazy, so
            // the throw on the measure named "AB" aborted enumeration after the rule had already matched
            // "Total Sales", and that one violation is still reported. Keeping it is defensible (a violation we
            // did find is real) but it is only honest while the unknown travels WITH it. A partial violation list
            // presented next to a zero unknown count is the map-lies defect returning.
            //
            // WHAT THIS LINE USED TO BE, and why it is written this way now. It read:
            //     Assert.DoesNotContain(card.Violations,
            //         v => v.RuleId == "TEST_THROWS_MID_SCOPE" && !v.Waived && card.UnknownCount == 0);
            // The `card.UnknownCount == 0` conjunct does not vary per element and is constant-FALSE on this
            // fixture, because the test above asserts `UnknownCount > 0` on the identical setup. The predicate
            // therefore matched nothing, and `DoesNotContain` passed whatever `Violations` actually held: an
            // assertion that could not fail. Measured 2026-07-30 by dropping the conjunct, which turned the line
            // red and revealed the retained prefix that had been invisible behind it.
            //
            // The premise is asserted first, so the implication can never go vacuous the way it just did: if the
            // engine ever starts discarding the prefix, THIS line fails and the invariant gets re-decided
            // deliberately rather than silently passing over an empty list.
            var partial = card.Violations
                .Where(v => v.RuleId == "TEST_THROWS_MID_SCOPE" && !v.Waived)
                .ToArray();
            Assert.NotEmpty(partial);
            Assert.True(card.UnknownCount > 0,
                "the scan reported " + partial.Length + " violation(s) from a rule whose scope threw, while "
                + "claiming zero unknowns; a partial list presented as a finished scope is the defect this "
                + "whole file exists to prevent");
        }

        // ---- 2) the deploy gate may not read GREEN over an unknown that would have blocked -------------------------

        [Fact]
        public async Task The_deploy_gate_does_not_read_green_when_a_blocking_rule_could_not_be_evaluated()
        {
            using var engine = await CleanModelAsync();
            await AddShortNamedMeasureAsync(engine);

            // Baseline: the fixture really is clean under the shipped corpus, so a later red verdict can only come
            // from the unknown (this is what makes the assertion below mean something).
            var before = await engine.DeployGateAsync(null, "human");
            Assert.True(before.Pass, "fixture must start clean; blockers were: " + string.Join(" | ", before.Blockers));

            await engine.LoadBpaRulesAsync(ThrowsMidScope, replace: true, "human");
            var after = await engine.DeployGateAsync(null, "human");

            Assert.False(after.Pass);
            Assert.Equal(1, after.BpaUnknownBlocking);
            // The GATE's wording moved from "could not be evaluated" to "could not run" when the blocker copy was
            // made legible (see DeployGateBlockerCopyTests). What this test is here to pin is unchanged and is
            // asserted on the substance, not the phrasing: the gate blocks, the unknown is counted, and the copy
            // still refuses to read the unknown as clean. BpaRuleUnknown.Reason still says "could not be evaluated"
            // and the assertion at line 114 on that field is untouched.
            Assert.Contains(after.Blockers, b => b.Contains("could not run", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(after.Blockers, b => b.Contains("not the same as clean", StringComparison.OrdinalIgnoreCase));
            Assert.Contains("Gate blocked", after.Note);
        }

        // ---- 3) the token-light summary carries it too (an agent that only calls bpa_summary must still see it) ----

        [Fact]
        public async Task The_bpa_summary_carries_the_unknown_as_a_first_class_count()
        {
            using var engine = await CleanModelAsync();
            await AddShortNamedMeasureAsync(engine);
            await engine.LoadBpaRulesAsync(ThrowsMidScope, replace: true, "human");
            var sum = await McpTools.BpaScanSummary(engine);

            Assert.Equal(1, sum.UnknownCount);
            Assert.Contains("TEST_THROWS_MID_SCOPE", sum.UnknownRules);
            // The Note is the line an agent actually reads: "no violations" must never be the whole story when a
            // rule could not be evaluated.
            Assert.Contains("could not be evaluated", sum.Note, StringComparison.OrdinalIgnoreCase);
        }

        // ---- 4) the readiness side: a throwing rule must not be able to move the score UPWARD ---------------------

        private const string ThrowsOnShortNames =
            "[{\"ID\":\"TEST_READINESS_THROWS\",\"Title\":\"throws part way through the scope\"," +
            "\"Category\":\"Descriptions\",\"Severity\":\"High\",\"Scope\":\"Measure\"," +
            "\"Expression\":\"Name.Substring(3).Length >= 0\"}]";

        // First line of defence, and it is stronger than the BPA lane's: load_readiness_rules TEST-RUNS the rule on the
        // open model and REFUSES to load one that cannot evaluate, so it never enters the corpus and never scores.
        // (This is precisely what BPA rule loading does not do, which is why BPA needs the scorecard-level unknown.)
        [Fact]
        public async Task Loading_a_readiness_rule_that_cannot_evaluate_is_refused_at_the_door()
        {
            using var engine = await CleanModelAsync();
            await AddShortNamedMeasureAsync(engine);   // the rule cannot evaluate on THIS model
            var before = await engine.AiReadinessScanAsync();

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => engine.LoadReadinessRulesAsync(ThrowsOnShortNames, replace: true, "human"));
            Assert.Contains("TEST_READINESS_THROWS", ex.Message);
            Assert.Contains("failed to evaluate", ex.Message);

            // Nothing loaded ⇒ nothing changed. The refusal is the honest outcome, not a silent dormant rule.
            var after = await engine.AiReadinessScanAsync();
            Assert.Equal(before.RawOverall, after.RawOverall, 6);
            Assert.Empty(after.UnevaluatedRules);
        }

        // Second line of defence, for the rule that loads clean and only LATER starts throwing because the model
        // changed under it. A rule error silently shrinking Applicable is the worst version of this defect: a broken
        // rule would make the model look BETTER. The evaluator forces such a rule dormant (Applicable = 0, findings
        // cleared) for exactly that reason; this locks it in so the defence cannot be refactored away.
        [Fact]
        public async Task A_rule_that_starts_throwing_after_it_loaded_goes_dormant_and_cannot_raise_the_score()
        {
            // Two identical models, built and scanned SEQUENTIALLY — the TOM wrapper keeps ambient per-process state,
            // so two live sessions at once is not a supported shape. Only the second one carries the rule.
            //
            // The control gets the identical model edit, which is what isolates the rule's contribution from the edit's
            // own (legitimate) effect on every other rule's population: a measure with a description and a format
            // string really does improve the score, so a plain before/after on one model would credit that to the defect.
            Scorecard baseline;
            using (var control = await CleanModelAsync())
            {
                await AddShortNamedMeasureAsync(control);
                baseline = await control.AiReadinessScanAsync();
            }

            Scorecard after;
            using (var broken = await CleanModelAsync())
            {
                // On this model every measure name is still longer than 3 characters, so the rule loads cleanly...
                await broken.LoadReadinessRulesAsync(ThrowsOnShortNames, replace: true, "human");
                Assert.Empty((await broken.AiReadinessScanAsync()).UnevaluatedRules);
                // ...and only now does the model break it: a 2-character name makes Name.Substring(3) throw mid-scope.
                await AddShortNamedMeasureAsync(broken);
                after = await broken.AiReadinessScanAsync();
            }

            // Dormant means it contributed NOTHING: identical score to the model that never had the rule. Equality in
            // both directions is the point — a broken rule must not be able to move the number either way, and upward
            // is the dangerous direction because it makes a model look better for being less well checked.
            Assert.DoesNotContain(after.Findings, f => f.RuleId == "TEST_READINESS_THROWS");
            Assert.Equal(baseline.RawOverall, after.RawOverall, 6);
            Assert.Equal(baseline.Grade, after.Grade);
            // And the dormancy is visible rather than silent, in the scorecard and in the token-light summary.
            Assert.Contains("TEST_READINESS_THROWS", after.UnevaluatedRules);
            Assert.Equal(1, ReadinessSummary.From(after).UnevaluatedRuleCount);
            Assert.Empty(baseline.UnevaluatedRules);
        }

        // ---- 5) an unsupported scope is unknown, and severity 2/3 block under the same predicate as a throw ----
        // A community rule targeting TablePermission used to record coverage as unknown while hard-coding
        // BlocksGate = false. That treated "we do not map this scope" as "the population is zero", so a model
        // that actually has permissions could still get a green gate. An absent evaluator mapping does not prove
        // the population is zero. The same Severity >= 2 && !CanAutoFix predicate the throw path already uses
        // is the one that must apply here.

        private static string UnsupportedScopeRule(int severity, string fixExpression = null)
        {
            var fix = string.IsNullOrEmpty(fixExpression)
                ? ""
                : ",\"FixExpression\":" + System.Text.Json.JsonSerializer.Serialize(fixExpression);
            return "[{\"ID\":\"TEST_UNSUPPORTED_SCOPE\",\"Name\":\"permissions check\",\"Category\":\"Security\","
                 + "\"Severity\":" + severity.ToString() + ",\"Scope\":\"TablePermission\","
                 + "\"Expression\":\"true\"" + fix + "}]";
        }

        [Theory]
        [InlineData(2)]
        [InlineData(3)]
        public async Task An_unsupported_blocking_scope_stays_unknown_and_blocks(int severity)
        {
            using var engine = await CleanModelAsync();
            await engine.LoadBpaRulesAsync(UnsupportedScopeRule(severity), replace: true, "human");
            var card = await engine.BpaScanAsync();

            var u = Assert.Single(card.Unknowns, x => x.RuleId == "TEST_UNSUPPORTED_SCOPE");
            Assert.Equal("TablePermission", u.Scope);
            Assert.Equal(severity, u.Severity);
            Assert.False(u.CanAutoFix);
            Assert.True(u.BlocksGate,
                "severity " + severity + " with no auto-fix would block after an evaluation failure, so an unsupported scope must too");
            Assert.Equal(0, u.ObjectsInScope);
            Assert.Contains(card.RuleErrors, e => e.Contains("TEST_UNSUPPORTED_SCOPE") && e.Contains("TablePermission") && e.Contains("unsupported scope"));
            Assert.Equal(card.Unknowns.Length, card.UnknownCount);
        }

        [Fact]
        public async Task An_unsupported_info_scope_stays_unknown_visible_and_does_not_block()
        {
            using var engine = await CleanModelAsync();
            await engine.LoadBpaRulesAsync(UnsupportedScopeRule(1, "IsHidden = true"), replace: true, "human");
            var card = await engine.BpaScanAsync();

            var u = Assert.Single(card.Unknowns, x => x.RuleId == "TEST_UNSUPPORTED_SCOPE");
            Assert.Equal(1, u.Severity);
            Assert.False(u.CanAutoFix, "no unknown is auto-fixable, whatever its severity or its FixExpression");
            Assert.False(u.BlocksGate, "severity 1 does not block on the throw path, so it must not block here either");

            // Nonblocking is not invisible. Refusing to auto-fix an unknown must not also demote it out of the
            // counts: a severity-1 unknown is still coverage nobody has, and the reader has to be able to see it.
            Assert.Equal(1, card.UnknownCount);
            Assert.Contains(card.RuleErrors, e => e.Contains("TEST_UNSUPPORTED_SCOPE"));
            var gate = await engine.DeployGateAsync(null, "human");
            Assert.True(gate.Pass, "a severity-1 unknown must not block; blockers were: " + string.Join(" | ", gate.Blockers));
            Assert.Equal(0, gate.BpaUnknownBlocking);
        }

        [Fact]
        public async Task The_deploy_gate_does_not_read_green_when_an_unsupported_blocking_scope_was_never_evaluated()
        {
            using var engine = await CleanModelAsync();

            var before = await engine.DeployGateAsync(null, "human");
            Assert.True(before.Pass, "fixture must start clean; blockers were: " + string.Join(" | ", before.Blockers));

            await engine.LoadBpaRulesAsync(UnsupportedScopeRule(3), replace: true, "human");
            var after = await engine.DeployGateAsync(null, "human");

            Assert.False(after.Pass);
            Assert.Equal(1, after.BpaUnknownBlocking);
            Assert.Contains(after.Blockers, b => b.Contains("could not run", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(after.Blockers, b => b.Contains("not the same as clean", StringComparison.OrdinalIgnoreCase));
            Assert.Contains("Gate blocked", after.Note);
        }

        // ---- 6) a parseable FixExpression can never clear an unknown, on EITHER unknown path ------------------
        //
        // The other half of the same defect, left standing by the fix for the first half. Both unknown paths asked
        // <c>CanAutoFix(rule.FixExpression)</c>, a SYNTAX question about a string with no object in it, and let a
        // "true" answer switch BlocksGate off. That answer cannot be true for an unknown, whichever path produced it:
        // an unsupported scope yielded no object for a fix to be applied to, and an aborted evaluation never
        // identified the objects in the part of the scope it did not reach. Nor is there anything that would apply
        // one: the bulk auto-fix pass iterates card.Violations, and so does plan seeding, so NO code path has ever
        // read BpaRuleUnknown and repaired unknown coverage. (Named in prose rather than by method here on purpose:
        // the coverage oracle harvests evidence by symbol, and this file exercises no bulk-fix operation.)
        // A parseable assignment was clearing a deployment block that
        // no fix pass could have earned, which is the switched-off-check family again, one field along from where it
        // was found the first time.
        //
        // The test this section replaced, An_unsupported_auto_fixable_scope_does_not_block, asserted exactly the
        // unsafe behaviour: it pinned CanAutoFix = true and BlocksGate = false for a severity-3 rule on TablePermission
        // whose fix nothing would ever run. It was consistent with the throw path, which is why it read as correct;
        // both paths were wrong in the same way.

        // Throws part way through a MAPPED scope AND carries a fix that really does apply to the objects in it.
        // IsHidden is a writable Boolean on a Measure, so the violations collected before the abort are genuinely
        // fixable, which is the point: the prefix keeps its ordinary fix behaviour while the unknown blocks.
        private const string ThrowsMidScopeWithValidFix =
            "[{\"ID\":\"TEST_THROWS_WITH_FIX\",\"Name\":\"throws part way through a fixable scope\",\"Category\":\"Test\"," +
            "\"Severity\":3,\"Scope\":\"Measure\",\"Expression\":\"Name.Substring(3).Length >= 0\"," +
            "\"FixExpression\":\"IsHidden = true\"}]";

        [Theory]
        [InlineData(2)]
        [InlineData(3)]
        public async Task An_unsupported_blocking_scope_with_a_valid_fix_is_not_auto_fixable_and_still_blocks(int severity)
        {
            using var engine = await CleanModelAsync();

            var before = await engine.DeployGateAsync(null, "human");
            Assert.True(before.Pass, "fixture must start clean; blockers were: " + string.Join(" | ", before.Blockers));

            // "IsHidden = true" is the strongest version of the case: a syntactically valid assignment naming a real
            // writable Boolean, so the old syntax-only answer said "fixable" with full confidence and switched the
            // block off. There is still no object here to hide.
            await engine.LoadBpaRulesAsync(UnsupportedScopeRule(severity, "IsHidden = true"), replace: true, "human");
            var card = await engine.BpaScanAsync();

            var u = Assert.Single(card.Unknowns, x => x.RuleId == "TEST_UNSUPPORTED_SCOPE");
            Assert.False(u.CanAutoFix,
                "an unsupported scope produced no object, so there is nothing the fix could be applied to");
            Assert.True(u.BlocksGate,
                "severity " + severity + " coverage that was never evaluated must block, and no fix pass can clear it");

            // Through the REAL gate, not just the scorecard field: this is the deployment decision the defect moved.
            var after = await engine.DeployGateAsync(null, "human");
            Assert.False(after.Pass, "a blocking rule that never ran must not produce a green gate");
            Assert.Equal(1, after.BpaUnknownBlocking);
            Assert.Equal(0, after.BpaBlocking);   // nothing fired: the block is the unknown itself, not a violation
            Assert.Contains(after.Blockers, b => b.Contains("could not run", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(after.Blockers, b => b.Contains("not the same as clean", StringComparison.OrdinalIgnoreCase));
            Assert.Contains("Gate blocked", after.Note);
        }

        [Fact]
        public async Task A_throwing_scope_with_a_valid_fix_blocks_while_the_violations_it_collected_stay_fixable()
        {
            using var engine = await CleanModelAsync();
            await AddShortNamedMeasureAsync(engine);

            var before = await engine.DeployGateAsync(null, "human");
            Assert.True(before.Pass, "fixture must start clean; blockers were: " + string.Join(" | ", before.Blockers));

            await engine.LoadBpaRulesAsync(ThrowsMidScopeWithValidFix, replace: true, "human");
            var card = await engine.BpaScanAsync();

            // TRUE violations keep their ordinary object-aware fixability. This is the half of the contract that the
            // correction must not break: the objects the predicate really returned before the abort are real
            // findings on real objects, and IsHidden really can be written on them.
            var prefix = card.Violations.Where(v => v.RuleId == "TEST_THROWS_WITH_FIX" && !v.Waived).ToArray();
            Assert.NotEmpty(prefix);
            Assert.All(prefix, v => Assert.True(v.CanAutoFix,
                "a violation the predicate genuinely returned keeps the per-object fix answer it earned"));
            Assert.True(card.AutoFixable >= prefix.Length, "the fixable prefix still counts as fixable");

            // The unevaluated remainder is the unknown, and a fix cannot reach objects nobody identified.
            var u = Assert.Single(card.Unknowns, x => x.RuleId == "TEST_THROWS_WITH_FIX");
            Assert.False(u.CanAutoFix,
                "the aborted enumeration never named the objects in the rest of the scope, so nothing here is fixable");
            Assert.True(u.BlocksGate, "severity 3 coverage that was cut short must block");
            Assert.Equal(prefix.Length, u.PartialViolations);

            var after = await engine.DeployGateAsync(null, "human");
            Assert.False(after.Pass, "the fixable prefix does not make the unevaluated remainder clean");
            Assert.Equal(1, after.BpaUnknownBlocking);
            // The prefix is auto-fixable, so it is NOT in the violation-blocking population: the whole block comes
            // from the unknown. Before the correction this gate was green.
            Assert.Equal(0, after.BpaBlocking);
            Assert.Contains(after.Blockers, b => b.Contains("could not run", StringComparison.OrdinalIgnoreCase));
            Assert.Contains("Gate blocked", after.Note);
        }

        // The control in the other direction, so "block when in doubt" cannot quietly become "block always". A scope
        // we DO map that happens to hold no objects was evaluated to completion over an empty population. That is a
        // real, finished answer, not an unknown, and treating it as one would block every model that simply has no
        // perspectives.
        [Fact]
        public async Task A_mapped_scope_with_no_objects_stays_clean_and_never_becomes_an_unknown()
        {
            using var engine = await CleanModelAsync();
            const string emptyMappedScopeRule =
                "[{\"ID\":\"TEST_EMPTY_MAPPED_SCOPE\",\"Name\":\"perspectives check\",\"Category\":\"Test\"," +
                "\"Severity\":3,\"Scope\":\"Perspective\",\"Expression\":\"true\",\"FixExpression\":\"IsHidden = true\"}]";
            await engine.LoadBpaRulesAsync(emptyMappedScopeRule, replace: true, "human");
            var card = await engine.BpaScanAsync();

            Assert.DoesNotContain(card.Unknowns, x => x.RuleId == "TEST_EMPTY_MAPPED_SCOPE");
            Assert.Equal(0, card.UnknownCount);
            Assert.DoesNotContain(card.Violations, v => v.RuleId == "TEST_EMPTY_MAPPED_SCOPE");
            Assert.DoesNotContain(card.RuleErrors, e => e.Contains("TEST_EMPTY_MAPPED_SCOPE"));

            var gate = await engine.DeployGateAsync(null, "human");
            Assert.True(gate.Pass, "an empty mapped scope is clean, not unknown; blockers were: " + string.Join(" | ", gate.Blockers));
            Assert.Equal(0, gate.BpaUnknownBlocking);
        }
    }
}
