using System;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Analysis;
using Semanticus.Engine;
using Semanticus.Engine.Entitlement;
using TabularEditor.TOMWrapper;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// ORPHANED waivers: a stored accepted-finding record whose rule id is absent from the LOADED rule set. This is the
    /// normal outcome of replacing a rule corpus (Microsoft's BPA set renames nearly every id), so the store will hold
    /// dead records in the field. The defect shape this guards against is the project's worst one: a thing that LOOKS
    /// like protection and silently does nothing. An orphan must be SURFACED and COUNTED as its own state — it may not
    /// suppress a finding, may not be counted as an active waiver, and may not be silently dropped or migrated.
    /// </summary>
    public sealed class OrphanedWaiverTests
    {
        private sealed class Fake : IEntitlement
        {
            public bool IsPro { get; }
            public EntitlementInfo Info { get; }
            public Fake(bool pro) { IsPro = pro; Info = new EntitlementInfo { Tier = pro ? "pro" : "free" }; }
        }

        // Deliberately unclaimable ids. Real retired ids (PERF_UNUSED_COLUMNS, LAYOUT_MEASURES_DF) would work TODAY,
        // but a future corpus could reintroduce the name and the test would silently stop testing anything — so the
        // ids are reserved, AND their absence is asserted below rather than assumed.
        private const string DeadBpaRule = "SEMANTICUS_TEST_BPA_RULE_THAT_DOES_NOT_EXIST";
        private const string DeadAirRule = "SEMANTICUS-TEST-AIR-RULE-THAT-DOES-NOT-EXIST";

        private static async Task<(LocalEngine engine, SessionManager sessions)> OpenAsync(bool pro = false)
        {
            Assert.DoesNotContain(BpaRuleSet.Standard(), r => string.Equals(r.ID, DeadBpaRule, StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(ReadinessRuleSet.Default(), r => string.Equals(r.Id, DeadAirRule, StringComparison.OrdinalIgnoreCase));
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, new Fake(pro));
            await engine.OpenAsync(TestModels.FindBim());
            return (engine, sessions);
        }

        // ---- BPA: a waiver pointing at a rule that no longer exists is surfaced + counted ---------------------------
        [Fact]
        public async Task Bpa_waiver_for_a_rule_id_absent_from_the_loaded_set_is_surfaced_and_counted()
        {
            var (engine, _) = await OpenAsync();
            using (engine)
            {
                var before = await engine.BpaScanAsync();
                Assert.DoesNotContain(before.Violations, v => v.RuleId == DeadBpaRule);   // the rule genuinely isn't loaded
                Assert.Equal(0, before.OrphanedWaiverCount);                              // …and nothing is orphaned yet
                var activeBefore = before.ViolationCount;
                var waivedBefore = before.WaivedCount;

                // A real object ref, a rule id the loaded corpus no longer contains — exactly what a corpus swap leaves behind.
                var objRef = before.Violations.First(v => v.ObjectRef.StartsWith("measure:") || v.ObjectRef.StartsWith("column:") || v.ObjectRef.StartsWith("table:")).ObjectRef;
                var r = await engine.WaiveFindingAsync("bpa", DeadBpaRule, objRef, "carried over from the old rule set", "human");
                Assert.True(r.Changed);

                var after = await engine.BpaScanAsync();

                // SURFACED: the dead record is reported, with enough detail to act on it (rule id + ref + reason).
                Assert.Equal(1, after.OrphanedWaiverCount);
                var orphan = Assert.Single(after.OrphanedWaivers);
                Assert.Equal(DeadBpaRule, orphan.RuleId);
                Assert.Equal(objRef, orphan.ObjectRef);
                Assert.Equal("carried over from the old rule set", orphan.Reason);

                // NOT protection: it suppressed nothing and is not counted among the active (honoured) waivers.
                Assert.Equal(activeBefore, after.ViolationCount);
                Assert.Equal(waivedBefore, after.WaivedCount);
                Assert.DoesNotContain(after.Violations, v => v.RuleId == DeadBpaRule);

                // NOT destroyed: the user's intent stays in the store, recoverable + un-waivable.
                Assert.Contains(await engine.ListWaiversAsync(), w => w.System == "bpa" && w.RuleId == DeadBpaRule);
            }
        }

        // ---- AI-readiness: same contract on the other rule system ---------------------------------------------------
        [Fact]
        public async Task Air_waiver_for_a_rule_id_absent_from_the_loaded_set_is_surfaced_and_counted()
        {
            var (engine, _) = await OpenAsync();
            using (engine)
            {
                var before = await engine.AiReadinessScanAsync();
                Assert.Equal(0, before.OrphanedWaiverCount);
                var waivedBefore = before.WaivedCount;
                var overallBefore = before.Overall;

                var objRef = before.Findings.First().ObjectRef;
                await engine.WaiveFindingAsync("air", DeadAirRule, objRef, "kept from the old catalog", "human");

                var after = await engine.AiReadinessScanAsync();
                Assert.Equal(1, after.OrphanedWaiverCount);
                Assert.Equal(DeadAirRule, Assert.Single(after.OrphanedWaivers).RuleId);
                Assert.Equal(waivedBefore, after.WaivedCount);   // never counted as an honoured waiver…
                Assert.Equal(overallBefore, after.Overall);       // …and it cannot move the grade
            }
        }

        // F-032: a known rule needing live data is not a deleted rule. Exercise the ordinary offline door first,
        // then supply invented cardinalities directly to the same analyzer core; no XMLA connection is involved.
        [Theory]
        [InlineData("SCALE-HICARD-COLUMN", "column:Sales/Customer Label", 1)]
        [InlineData("scale-hicard-column", null, 2)]
        [InlineData("SCALE-QNA-INDEX", "model", 1)]
        [InlineData("scale-qna-index", "*", 1)]
        public async Task Offline_live_rule_waiver_survives_and_applies_when_live_data_arrives(string ruleId, string? objectRef, int expectedWaived)
        {
            var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, new Fake(true));
            await engine.CreateModelAsync("OfflineWaiverFixture", 1604);
            var table = await engine.CreateTableAsync("Sales", "human");
            await engine.CreateColumnAsync(table, "Customer Label", "String", "Customer Label", "human");
            await engine.CreateColumnAsync(table, "Order Label", "String", "Order Label", "human");
            var stats = new ReadinessLiveStats();
            stats.Set("Sales", "Customer Label", 3_000_000);
            stats.Set("Sales", "Order Label", 3_000_000);
            var analyzer = new ReadinessAnalyzer();
            var liveBefore = await sessions.Require().ReadAsync(m => analyzer.Analyze(m, stats));
            if (objectRef == "model")
                objectRef = Assert.Single(liveBefore.Findings, f => f.RuleId == "SCALE-QNA-INDEX").ObjectRef;
            var before = await engine.AiReadinessScanAsync();

            await engine.WaiveFindingAsync("air", ruleId, objectRef, "Invented fixture accepts this index cost.", "human");
            var stored = await sessions.Require().ReadAsync(m => m.GetAnnotation(WaiverStore.Annotation));
            var offline = await engine.AiReadinessScanAsync();

            Assert.Empty(offline.OrphanedWaivers);
            Assert.Equal(0, offline.OrphanedWaiverCount);
            Assert.Equal(0, offline.WaivedCount);
            Assert.DoesNotContain(offline.Findings, f => ReadinessRuleSet.LiveRules(stats).Any(r => r.Id == f.RuleId));
            Assert.Equal(System.Text.Json.JsonSerializer.Serialize(before), System.Text.Json.JsonSerializer.Serialize(offline));
            Assert.Equal(stored, await sessions.Require().ReadAsync(m => m.GetAnnotation(WaiverStore.Annotation)));

            var liveAfter = await sessions.Require().ReadAsync(m => analyzer.Analyze(m, stats));
            Assert.Empty(liveAfter.OrphanedWaivers);
            Assert.Equal(expectedWaived, liveAfter.WaivedCount);
            Assert.Equal(liveBefore.Findings.Length, liveAfter.Findings.Length);
            foreach (var finding in liveAfter.Findings)
            {
                var matches = string.Equals(finding.RuleId, ruleId, StringComparison.OrdinalIgnoreCase)
                    && (WaiverStore.IsRuleLevel(objectRef) || finding.ObjectRef == objectRef);
                Assert.Equal(matches, finding.Waived);
                if (matches)
                {
                    Assert.Equal("Invented fixture accepts this index cost.", finding.WaiverReason);
                    Assert.Equal(WaiverStore.IsRuleLevel(objectRef), finding.WaiverRuleLevel);
                }
            }
            var limitsBefore = liveBefore.Categories.Single(c => c.Category == "CopilotLimits");
            var limitsAfter = liveAfter.Categories.Single(c => c.Category == "CopilotLimits");
            Assert.Equal(limitsBefore.Applicable, limitsAfter.Applicable);
            Assert.Equal(limitsBefore.Violations - expectedWaived, limitsAfter.Violations);
            Assert.Equal(expectedWaived, limitsAfter.Waived);
            Assert.True(limitsAfter.Score > limitsBefore.Score);
            Assert.Equal(stored, await sessions.Require().ReadAsync(m => m.GetAnnotation(WaiverStore.Annotation)));
        }

        [Fact]
        public async Task Offline_reconciliation_keeps_known_ids_but_reports_unknown_and_deleted_custom_rules()
        {
            var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, new Fake(true));
            await engine.CreateModelAsync("OfflineCatalogFixture", 1604);
            const string customId = "FIXTURE-CUSTOM-DESCRIPTION";
            await sessions.Require().MutateAsync("human", "store fixture rules and waivers", m =>
            {
                m.SetAnnotation(CustomReadinessRuleSet.AnnotationName, CustomReadinessRuleSet.Serialize(new[]
                {
                    new CustomReadinessRuleDef { ID = customId, Category = "Descriptions", Scope = "Measure", Expression = "true" }
                }));
                var ids = ReadinessRuleSet.LiveRules(new ReadinessLiveStats()).Select(r => r.Id)
                    .Concat(new[] { "DESC-MEASURE", customId, DeadAirRule }).ToArray();
                Assert.DoesNotContain(ReadinessRuleSet.Default(), r => r.Id == DeadAirRule);
                Assert.DoesNotContain(ReadinessRuleSet.LiveRules(new ReadinessLiveStats()), r => r.Id == DeadAirRule);
                WaiverStore.Save(m, ids.Select(id => new WaiverRecord
                {
                    System = "AIR", RuleId = id.ToLowerInvariant(), Reason = "Invented dormant-rule waiver.", By = "human"
                }).Concat(new[] { new WaiverRecord { System = "bpa", RuleId = DeadBpaRule, Reason = "Other system." } }).ToList());
            });
            var analyzer = new ReadinessAnalyzer();
            var state = new ReadinessScanState();
            var baseline = await sessions.Require().ReadAsync(m => analyzer.Baseline(m, state));
            var scoped = await sessions.Require().ReadAsync(m => analyzer.Reanalyze(m, state, null));
            var offline = await engine.AiReadinessScanAsync();
            foreach (var card in new[] { baseline, scoped, offline })
            {
                Assert.Empty(card.RuleErrors);
                Assert.Equal(1, card.OrphanedWaiverCount);
                Assert.Equal(DeadAirRule.ToLowerInvariant(), Assert.Single(card.OrphanedWaivers).RuleId);
                Assert.Equal(0, card.WaivedCount);
                Assert.DoesNotContain(card.Findings, f => ReadinessRuleSet.LiveRules(new ReadinessLiveStats()).Any(r => r.Id == f.RuleId));
            }

            await sessions.Require().MutateAsync("human", "delete custom fixture rule", m =>
                m.RemoveAnnotation(CustomReadinessRuleSet.AnnotationName));
            var after = await engine.AiReadinessScanAsync();
            Assert.Equal(2, after.OrphanedWaiverCount);
            Assert.Equal(new[] { DeadAirRule.ToLowerInvariant(), customId.ToLowerInvariant() }.OrderBy(x => x),
                after.OrphanedWaivers.Select(w => w.RuleId).OrderBy(x => x));
            Assert.Contains(await engine.ListWaiversAsync(), w => w.RuleId == customId.ToLowerInvariant());
        }

        // ---- the healthy case must stay quiet: a LIVE waiver is never mislabelled as orphaned -----------------------
        [Fact]
        public async Task A_waiver_matching_a_loaded_rule_is_not_reported_as_orphaned()
        {
            var (engine, _) = await OpenAsync();
            using (engine)
            {
                var before = await engine.BpaScanAsync();
                var v = before.Violations.First(x => !x.Waived &&
                    (x.ObjectRef.StartsWith("measure:") || x.ObjectRef.StartsWith("column:") || x.ObjectRef.StartsWith("table:")));

                await engine.WaiveFindingAsync("bpa", v.RuleId, v.ObjectRef, "house standard, accepted", "human");

                var after = await engine.BpaScanAsync();
                Assert.Equal(0, after.OrphanedWaiverCount);            // the rule is loaded ⇒ the waiver is live, not dead
                Assert.Empty(after.OrphanedWaivers);
                Assert.True(after.WaivedCount >= 1);                   // and it IS honoured
                Assert.Equal(before.ViolationCount - 1, after.ViolationCount);
            }
        }

        // ---- a rule-level (model-wide) waiver orphans the same way, and is reported with a null ref -----------------
        [Fact]
        public async Task Rule_level_waiver_for_a_dead_rule_is_surfaced_as_orphaned()
        {
            var (engine, _) = await OpenAsync(pro: true);   // rule-level waiving is the Pro bulk lever
            using (engine)
            {
                await engine.WaiveFindingAsync("bpa", DeadBpaRule, null, "we never removed unused columns", "human");

                var after = await engine.BpaScanAsync();
                var orphan = Assert.Single(after.OrphanedWaivers);
                Assert.Equal(DeadBpaRule, orphan.RuleId);
                Assert.True(WaiverStore.IsRuleLevel(orphan.ObjectRef));
                Assert.Equal(1, after.OrphanedWaiverCount);
            }
        }

        // ---- the cross-system guard: an "air" waiver is not judged against the BPA rule set (or vice versa) ---------
        [Fact]
        public async Task An_air_waiver_is_not_reported_as_an_orphan_on_the_bpa_card()
        {
            var (engine, _) = await OpenAsync();
            using (engine)
            {
                var findings = await engine.AiReadinessScanAsync();
                var f = findings.Findings.First(x => !x.Waived);
                await engine.WaiveFindingAsync("air", f.RuleId, f.ObjectRef, "accepted", "human");

                // The BPA card judges only "bpa" waivers — an AIR rule id is not "missing from the BPA rule set".
                var bpa = await engine.BpaScanAsync();
                Assert.Equal(0, bpa.OrphanedWaiverCount);
                var air = await engine.AiReadinessScanAsync();
                Assert.Equal(0, air.OrphanedWaiverCount);
            }
        }
    }
}
