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
    /// Finding WAIVERS (accepted findings) for BOTH AI-readiness and BPA. A waiver is an audited decision to accept a
    /// finding: it drops out of the SCORE (counts as a pass) but is always surfaced (tagged + reasoned + counted), so
    /// the grade can't be silently inflated. Proves the contract: a waiver raises the score; a HARD GATE is NOT lifted
    /// by waivers (gates evaluate the raw count — you can't accept past a physical ceiling); a reason is required;
    /// rule-level (model-wide) waiving is the Pro bulk lever while per-instance is free; round-trips persist; and BPA
    /// waivers interoperate with Tabular Editor's BestPracticeAnalyzer_IgnoreRules (honour inbound + mirror outbound).
    /// </summary>
    public sealed class WaiverTests
    {
        private sealed class Fake : IEntitlement
        {
            public bool IsPro { get; }
            public EntitlementInfo Info { get; }
            public Fake(bool pro) { IsPro = pro; Info = new EntitlementInfo { Tier = pro ? "pro" : "free" }; }
        }

        private static async Task<(LocalEngine engine, SessionManager sessions)> OpenAwAsync(bool pro = false)
        {
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, new Fake(pro));
            await engine.OpenAsync(TestModels.FindBim());
            return (engine, sessions);
        }

        // ---- AI-readiness: a waiver raises the score and tags (never hides) the finding ----------------------------

        [Fact]
        public async Task Air_waiver_excludes_a_finding_from_the_score_but_keeps_it_surfaced()
        {
            var (engine, _) = await OpenAwAsync();
            using (engine)
            {
                var table = (await engine.ListMeasuresAsync()).First().Table;
                var msRef = await engine.CreateMeasureAsync("table:" + table, "Wv_tmp_code1", "1", "agent");   // cryptic ⇒ NAME-MEASURE

                var before = await engine.AiReadinessScanAsync();
                Assert.Contains(before.Findings, f => f.RuleId == "NAME-MEASURE" && f.ObjectRef == msRef && !f.Waived);
                var nbefore = before.Categories.First(c => c.Category == "Naming");

                var r = await engine.WaiveFindingAsync("air", "NAME-MEASURE", msRef, "Intentional house code, defined in the glossary.", "human");
                Assert.True(r.Changed);

                var after = await engine.AiReadinessScanAsync();
                var wf = after.Findings.First(f => f.RuleId == "NAME-MEASURE" && f.ObjectRef == msRef);
                Assert.True(wf.Waived);                                            // still surfaced…
                Assert.Equal("Intentional house code, defined in the glossary.", wf.WaiverReason);
                Assert.True(after.WaivedCount >= 1);                              // …and counted as waived
                var nafter = after.Categories.First(c => c.Category == "Naming");
                Assert.True(nafter.Score >= nbefore.Score);                      // a waiver never lowers the score
                Assert.True(nafter.Violations < nbefore.Violations);             // the active violation count dropped
                Assert.True(nafter.Waived >= 1);
            }
        }

        // ---- the honesty guarantee: a waiver does NOT lift a hard gate ---------------------------------------------

        [Fact]
        public async Task Air_waiver_does_not_lift_the_undescribed_measures_hard_gate()
        {
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, new Fake(true));   // Pro, so even rule-level can't lift the gate
            using (engine)
            {
                await engine.CreateModelAsync("GateTest", 1567);
                var t = await engine.CreateTableAsync("Facts", "human");
                var m1 = await engine.CreateMeasureAsync(t, "Sales Amount", "1", "human");   // human-readable, NO description
                var m2 = await engine.CreateMeasureAsync(t, "Total Cost", "1", "human");

                var before = await engine.AiReadinessScanAsync();
                Assert.Contains(before.GatedBy, g => g.Contains("undescribed"));   // 2/2 undescribed > 50% ⇒ gate fires
                Assert.True(before.Overall <= 69);

                await engine.WaiveFindingAsync("air", "DESC-MEASURE", m1, "accepted", "human");
                await engine.WaiveFindingAsync("air", "DESC-MEASURE", m2, "accepted", "human");

                var after = await engine.AiReadinessScanAsync();
                Assert.Contains(after.GatedBy, g => g.Contains("undescribed"));    // gate still fires on the RAW ratio
                Assert.True(after.Overall <= 69);                                  // can't accept past a physical ceiling
                Assert.True(after.WaivedCount >= 2);
            }
        }

        // ---- governance: a reason is required ----------------------------------------------------------------------

        [Fact]
        public async Task Waiving_without_a_reason_is_refused()
        {
            var (engine, _) = await OpenAwAsync();
            using (engine)
            {
                var table = (await engine.ListMeasuresAsync()).First().Table;
                var msRef = await engine.CreateMeasureAsync("table:" + table, "Wv_tmp_code2", "1", "agent");
                await Assert.ThrowsAsync<ArgumentException>(() => engine.WaiveFindingAsync("air", "NAME-MEASURE", msRef, "", "human"));
                await Assert.ThrowsAsync<ArgumentException>(() => engine.WaiveFindingAsync("air", "NAME-MEASURE", msRef, "   ", "human"));
            }
        }

        // ---- monetization: rule-level (bulk) is Pro; per-instance is free ------------------------------------------

        [Fact]
        public async Task Rule_level_waiver_is_pro_gated_while_per_instance_is_free()
        {
            var (free, _) = await OpenAwAsync(pro: false);
            using (free)
            {
                // rule-level (null ref OR '*') = "waive every instance, model-wide" — the bulk lever, refused on free
                await Assert.ThrowsAsync<EntitlementException>(() => free.WaiveFindingAsync("air", "NAME-MEASURE", null, "blanket", "human"));
                await Assert.ThrowsAsync<EntitlementException>(() => free.WaiveFindingAsync("bpa", "ANYRULE", "*", "blanket", "human"));
                // per-instance stays free
                var table = (await free.ListMeasuresAsync()).First().Table;
                var msRef = await free.CreateMeasureAsync("table:" + table, "Wv_tmp_code3", "1", "agent");
                var r = await free.WaiveFindingAsync("air", "NAME-MEASURE", msRef, "one off", "human");
                Assert.True(r.Changed);
            }

            var (pro, _) = await OpenAwAsync(pro: true);
            using (pro)
            {
                var r = await pro.WaiveFindingAsync("air", "NAME-MEASURE", null, "we never expand standard finance acronyms", "human");
                Assert.True(r.Changed);   // Pro may waive the whole rule
            }
        }

        // ---- list + un-waive round-trip persists on the model ------------------------------------------------------

        [Fact]
        public async Task List_and_unwaive_round_trip()
        {
            var (engine, _) = await OpenAwAsync();
            using (engine)
            {
                var table = (await engine.ListMeasuresAsync()).First().Table;
                var msRef = await engine.CreateMeasureAsync("table:" + table, "Wv_tmp_code4", "1", "agent");
                await engine.WaiveFindingAsync("air", "NAME-MEASURE", msRef, "keeps the code", "human");

                var list = await engine.ListWaiversAsync();
                var w = list.FirstOrDefault(x => x.System == "air" && x.RuleId == "NAME-MEASURE" && x.ObjectRef == msRef);
                Assert.NotNull(w);
                Assert.Equal("keeps the code", w.Reason);
                Assert.False(string.IsNullOrEmpty(w.When));   // audited: carries a timestamp

                await engine.UnwaiveFindingAsync("air", "NAME-MEASURE", msRef, "human");
                Assert.DoesNotContain(await engine.ListWaiversAsync(), x => x.ObjectRef == msRef);
                var after = await engine.AiReadinessScanAsync();
                Assert.Contains(after.Findings, f => f.RuleId == "NAME-MEASURE" && f.ObjectRef == msRef && !f.Waived);   // counts again
            }
        }

        // ---- BPA: waiving drops the active count + mirrors to Tabular Editor's ignore annotation -------------------

        [Fact]
        public async Task Bpa_waiver_drops_the_active_count_and_mirrors_to_tabular_editor()
        {
            var (engine, sessions) = await OpenAwAsync();
            using (engine)
            {
                var card = await engine.BpaScanAsync();
                var v = card.Violations.FirstOrDefault(x => !x.Waived &&
                    (x.ObjectRef.StartsWith("measure:") || x.ObjectRef.StartsWith("column:") || x.ObjectRef.StartsWith("table:")));
                Assert.NotNull(v);   // AdventureWorks trips several standard rules on resolvable objects
                var beforeCount = card.ViolationCount;

                await engine.WaiveFindingAsync("bpa", v.RuleId, v.ObjectRef, "house standard, accepted", "human");

                var after = await engine.BpaScanAsync();
                var wv = after.Violations.First(x => x.RuleId == v.RuleId && x.ObjectRef == v.ObjectRef);
                Assert.True(wv.Waived);
                Assert.Equal("house standard, accepted", wv.WaiverReason);
                Assert.Equal(beforeCount - 1, after.ViolationCount);   // dropped from the active count
                Assert.True(after.WaivedCount >= 1);

                // …and Tabular Editor's per-object ignore annotation was written, so TE3 honours it too.
                var teMirrored = await sessions.Require().ReadAsync(m =>
                    WaiverStore.IsTeIgnored(ObjectRefs.Resolve(m, v.ObjectRef), v.RuleId));
                Assert.True(teMirrored);
            }
        }

        // ---- the bulk AIR safe-fixer must NOT auto-fix a finding the user accepted --------------------------------

        [Fact]
        public async Task Apply_safe_fixes_does_not_auto_fix_a_waived_finding()
        {
            var (engine, _) = await OpenAwAsync(pro: true);   // apply_safe_fixes is Pro-gated; AdventureWorks is rich in SafeFix findings
            using (engine)
            {
                var before = await engine.AiReadinessScanAsync();
                var sf = before.Findings.First(f => f.Fix == "SafeFix" && !f.Waived);   // a deterministic AIR fix (VIS-FK / FMT-SUMMARIZE / …)

                await engine.WaiveFindingAsync("air", sf.RuleId, sf.ObjectRef, "accepted by design", "human");
                await engine.ApplySafeFixesAsync("human");   // fixes the OTHER safe findings, but must skip the accepted one

                var after = await engine.AiReadinessScanAsync();
                var still = after.Findings.FirstOrDefault(f => f.RuleId == sf.RuleId && f.ObjectRef == sf.ObjectRef);
                Assert.NotNull(still);          // the underlying condition was NOT auto-fixed away…
                Assert.True(still!.Waived);     // …because the finding is waived (the audited decision stands)
            }
        }

        [Fact]
        public async Task Bpa_honors_a_tabular_editor_ignore_set_outside_semanticus()
        {
            var (engine, sessions) = await OpenAwAsync();
            using (engine)
            {
                var card = await engine.BpaScanAsync();
                var v = card.Violations.FirstOrDefault(x => !x.Waived &&
                    (x.ObjectRef.StartsWith("measure:") || x.ObjectRef.StartsWith("column:") || x.ObjectRef.StartsWith("table:")));
                Assert.NotNull(v);

                // Simulate a rule ignored in TE3 (the native BestPracticeAnalyzer_IgnoreRules annotation), set OUTSIDE our op.
                await sessions.Require().MutateAsync("human", "te ignore", m =>
                    WaiverStore.WriteTeIgnore(ObjectRefs.Resolve(m, v.ObjectRef), v.RuleId, true));

                var after = await engine.BpaScanAsync();
                var wv = after.Violations.First(x => x.RuleId == v.RuleId && x.ObjectRef == v.ObjectRef);
                Assert.True(wv.Waived);                              // honoured inbound
                Assert.Equal(WaiverStore.TeReason, wv.WaiverReason);
            }
        }

        // ---- data-preservation: waiving over CORRUPT waiver data preserves it aside + warns (never destroys) -------
        // Audit finding: a corrupt Semanticus_Waivers annotation degraded to an empty list, and the next add/remove
        // SAVED that empty list over the original — silently destroying every accepted-findings record. Now the corrupt
        // bytes are copied to a `<name>.corrupt-<hex>` sibling annotation BEFORE Save, and a warning is surfaced (if the
        // aside write fails, the mutation is refused). Neuter: swap LoadForWrite back to Load and the corrupt bytes are
        // gone after the waive — this test fails.
        [Fact]
        public async Task Waive_over_corrupt_waiver_data_preserves_it_aside_and_warns()
        {
            var (engine, sessions) = await OpenAwAsync();
            using (engine)
            {
                // A hand-mangled (truncated) waiver annotation — parses to nothing but must not be lost.
                await sessions.Require().MutateAsync("human", "corrupt waivers",
                    m => ((IAnnotationObject)m).SetAnnotation(WaiverStore.Annotation, "[ {\"System\":\"air\" TRUNCATED"));

                var table = (await engine.ListMeasuresAsync()).First().Table;
                var msRef = await engine.CreateMeasureAsync("table:" + table, "Wv_corrupt_code", "1", "agent");

                var r = await engine.WaiveFindingAsync("air", "NAME-MEASURE", msRef, "accepted", "human");
                Assert.True(r.Changed);
                Assert.False(string.IsNullOrEmpty(r.Warning));      // the loss risk was surfaced, not silent
                Assert.Contains("corrupt", r.Warning);

                // The unreadable bytes were preserved to a `.corrupt-<hex>` sibling annotation, recoverable…
                var (name, val) = await sessions.Require().ReadAsync(m =>
                {
                    var ao = (IAnnotationObject)m;
                    var n = ao.GetAnnotations().FirstOrDefault(a => a.StartsWith(WaiverStore.Annotation + ".corrupt-", StringComparison.Ordinal));
                    return (n, n == null ? null : ao.GetAnnotation(n));
                });
                Assert.NotNull(name);
                Assert.Contains("TRUNCATED", val);

                // …and the new waiver still landed (the store is valid again).
                Assert.Contains(await engine.ListWaiversAsync(), w => w.System == "air" && w.RuleId == "NAME-MEASURE" && w.ObjectRef == msRef);
            }
        }

        // ---- TE mirror: a PARSEABLE-but-structurally-invalid ignore annotation is preserved, never overwritten -----
        // Review follow-up (sol): `{"RuleIDs":"NAME"}` or `[]` parse as JSON but aren't TE's shape — the old lenient
        // read treated them as an empty set and the rewrite destroyed them with no preservation. Structural validity
        // is now the FULL expected schema (one RuleIDs property, an array of strings). Neuter: accept any parseable
        // JSON as valid again and the aside annotation disappears — this test fails.
        [Fact]
        public async Task Structurally_invalid_te_ignore_is_preserved_aside_before_rewrite()
        {
            var (engine, sessions) = await OpenAwAsync();
            using (engine)
            {
                var card = await engine.BpaScanAsync();
                var v = card.Violations.First(x => x.ObjectRef.StartsWith("measure:") || x.ObjectRef.StartsWith("column:") || x.ObjectRef.StartsWith("table:"));

                // Parseable JSON, wrong shape: RuleIDs is a STRING — a rewrite would silently drop whatever it meant.
                await sessions.Require().MutateAsync("human", "plant invalid te ignore", m =>
                    ((IAnnotationObject)ObjectRefs.Resolve(m, v.ObjectRef)).SetAnnotation("BestPracticeAnalyzer_IgnoreRules", "{\"RuleIDs\":\"SOME_RULE\"}"));

                await engine.WaiveFindingAsync("bpa", v.RuleId, v.ObjectRef, "accepted", "human");   // mirrors WriteTeIgnore

                var (backup, rewritten) = await sessions.Require().ReadAsync(m =>
                {
                    var ao = (IAnnotationObject)ObjectRefs.Resolve(m, v.ObjectRef);
                    var n = ao.GetAnnotations().FirstOrDefault(a => a.StartsWith("BestPracticeAnalyzer_IgnoreRules.corrupt-", StringComparison.Ordinal));
                    return (n == null ? null : ao.GetAnnotation(n), ao.GetAnnotation("BestPracticeAnalyzer_IgnoreRules"));
                });
                Assert.Equal("{\"RuleIDs\":\"SOME_RULE\"}", backup);          // the invalid original, preserved verbatim
                Assert.Contains(v.RuleId, rewritten);                          // the fresh, valid mirror landed
            }
        }

        // ---- TE mirror: BOTH keys are inspected — a corrupt LEGACY-typo annotation is preserved, not just dropped ---
        // Review follow-up (sol): the legacy "BestPractizeAnalyzer_IgnoreRules" key was removed unconditionally while
        // only the preferred key was inspected — a corrupt legacy value was silently destroyed by the consolidation.
        // Neuter: skip the legacy preserve and the backup annotation disappears — this test fails.
        [Fact]
        public async Task Corrupt_legacy_te_ignore_is_preserved_before_consolidation()
        {
            var (engine, sessions) = await OpenAwAsync();
            using (engine)
            {
                var card = await engine.BpaScanAsync();
                var v = card.Violations.First(x => x.ObjectRef.StartsWith("measure:") || x.ObjectRef.StartsWith("column:") || x.ObjectRef.StartsWith("table:"));

                await sessions.Require().MutateAsync("human", "plant legacy + preferred", m =>
                {
                    var ao = (IAnnotationObject)ObjectRefs.Resolve(m, v.ObjectRef);
                    ao.SetAnnotation("BestPracticeAnalyzer_IgnoreRules", "{\"RuleIDs\":[\"KEEP_ME\"]}");   // preferred: valid
                    ao.SetAnnotation("BestPractizeAnalyzer_IgnoreRules", "{ mangled legacy ]");            // legacy: corrupt
                });

                await engine.WaiveFindingAsync("bpa", v.RuleId, v.ObjectRef, "accepted", "human");

                var (legacyBackup, legacyGone, rewritten) = await sessions.Require().ReadAsync(m =>
                {
                    var ao = (IAnnotationObject)ObjectRefs.Resolve(m, v.ObjectRef);
                    var n = ao.GetAnnotations().FirstOrDefault(a => a.StartsWith("BestPractizeAnalyzer_IgnoreRules.corrupt-", StringComparison.Ordinal));
                    return (n == null ? null : ao.GetAnnotation(n),
                            !ao.HasAnnotation("BestPractizeAnalyzer_IgnoreRules"),
                            ao.GetAnnotation("BestPracticeAnalyzer_IgnoreRules"));
                });
                Assert.Equal("{ mangled legacy ]", legacyBackup);   // the corrupt legacy value, preserved before removal
                Assert.True(legacyGone);                            // the consolidation still retires the typo key
                Assert.Contains("KEEP_ME", rewritten);              // the valid preferred set survived the merge
                Assert.Contains(v.RuleId, rewritten);
            }
        }

        // ---- display never throws on a mangled store: null/blank entries are filtered on load ----------------------
        // Review follow-up (sol): `[null]` deserializes to a list holding null, which downstream display consumers
        // dereference. Load now normalizes; the WRITE path treats the mangled store as corrupt (preserve-aside+warn).
        [Fact]
        public async Task Mangled_waiver_entries_are_filtered_on_load_and_preserved_on_write()
        {
            var (engine, sessions) = await OpenAwAsync();
            using (engine)
            {
                await sessions.Require().MutateAsync("human", "plant mangled waivers", m =>
                    ((IAnnotationObject)m).SetAnnotation(WaiverStore.Annotation,
                        "[ null, {\"system\":\"air\",\"ruleId\":\"NAME-MEASURE\",\"objectRef\":\"x\",\"reason\":\"kept\"} ]"));

                // Display: no throw, the null entry filtered, the readable record kept.
                var list = await engine.ListWaiversAsync();
                Assert.DoesNotContain(list, w => w == null);
                Assert.Contains(list, w => w.RuleId == "NAME-MEASURE" && w.Reason == "kept");

                // Write over it: the partially-unreadable original is preserved aside + warned, never silently dropped.
                var table = (await engine.ListMeasuresAsync()).First().Table;
                var msRef = await engine.CreateMeasureAsync("table:" + table, "Wv_mangled_code", "1", "agent");
                var r = await engine.WaiveFindingAsync("air", "NAME-MEASURE", msRef, "accepted", "human");
                Assert.False(string.IsNullOrEmpty(r.Warning));
                var backup = await sessions.Require().ReadAsync(m =>
                {
                    var ao = (IAnnotationObject)m;
                    var n = ao.GetAnnotations().FirstOrDefault(a => a.StartsWith(WaiverStore.Annotation + ".corrupt-", StringComparison.Ordinal));
                    return n == null ? null : ao.GetAnnotation(n);
                });
                Assert.NotNull(backup);
                Assert.Contains("null", backup);   // the original, null entry and all
            }
        }

        // ---- TE mirror: a WHITESPACE preferred annotation must not shadow + destroy a VALID legacy set --------------
        // Review follow-up (sol): TryParseTeIgnore reports whitespace as valid-nothing (set=null), so a present-but-
        // whitespace preferred key took precedence over a valid LEGACY value — the legacy set was ignored and then
        // consolidated away, destroying real ignore ids. A whitespace preferred is now treated as ABSENT so the valid
        // legacy seeds the set. Neuter: restore `prefRaw != null && prefOk` and the legacy KEEP is lost — this fails.
        [Fact]
        public async Task Whitespace_preferred_te_ignore_does_not_destroy_a_valid_legacy_set()
        {
            var (engine, sessions) = await OpenAwAsync();
            using (engine)
            {
                var card = await engine.BpaScanAsync();
                var v = card.Violations.First(x => x.ObjectRef.StartsWith("measure:") || x.ObjectRef.StartsWith("column:") || x.ObjectRef.StartsWith("table:"));

                // Preferred present-but-WHITESPACE (validly nothing); legacy carries a real ignore id.
                await sessions.Require().MutateAsync("human", "plant whitespace preferred + valid legacy", m =>
                {
                    var ao = (IAnnotationObject)ObjectRefs.Resolve(m, v.ObjectRef);
                    ao.SetAnnotation("BestPracticeAnalyzer_IgnoreRules", "   ");
                    ao.SetAnnotation("BestPractizeAnalyzer_IgnoreRules", "{\"RuleIDs\":[\"KEEP\"]}");
                });

                // Add a NEW id via the mirror — the legacy KEEP must survive, not be shadowed by the empty preferred.
                await sessions.Require().MutateAsync("human", "add NEW via WriteTeIgnore",
                    m => WaiverStore.WriteTeIgnore(ObjectRefs.Resolve(m, v.ObjectRef), "NEW", true));

                var pref = await sessions.Require().ReadAsync(m =>
                    ((IAnnotationObject)ObjectRefs.Resolve(m, v.ObjectRef)).GetAnnotation("BestPracticeAnalyzer_IgnoreRules"));
                Assert.Contains("KEEP", pref);   // the valid legacy set survived (no data lost)…
                Assert.Contains("NEW", pref);    // …alongside the newly added id
            }
        }

        // ---- LoadForWrite: a literal JSON `null` annotation is corrupt, not a silently-empty store -----------------
        // Review follow-up (sol): a raw value of literal `null` deserialized to null, coalesced to an empty list, left
        // corruptRaw null, and the next Save overwrote it with no preservation. Now treated as corrupt: preserved
        // aside + warned. Neuter: coalesce the deserializer's null to an empty list again and no aside appears — fails.
        [Fact]
        public async Task Waive_over_a_literal_null_waiver_annotation_preserves_it_aside()
        {
            var (engine, sessions) = await OpenAwAsync();
            using (engine)
            {
                await sessions.Require().MutateAsync("human", "null waivers",
                    m => ((IAnnotationObject)m).SetAnnotation(WaiverStore.Annotation, "null"));

                var table = (await engine.ListMeasuresAsync()).First().Table;
                var msRef = await engine.CreateMeasureAsync("table:" + table, "Wv_null_code", "1", "agent");

                var r = await engine.WaiveFindingAsync("air", "NAME-MEASURE", msRef, "accepted", "human");
                Assert.True(r.Changed);
                Assert.False(string.IsNullOrEmpty(r.Warning));   // the loss risk was surfaced, not silent
                Assert.Contains("corrupt", r.Warning);

                var backup = await sessions.Require().ReadAsync(m =>
                {
                    var ao = (IAnnotationObject)m;
                    var n = ao.GetAnnotations().FirstOrDefault(a => a.StartsWith(WaiverStore.Annotation + ".corrupt-", StringComparison.Ordinal));
                    return n == null ? null : ao.GetAnnotation(n);
                });
                Assert.NotNull(backup);
                Assert.Equal("null", backup);   // the literal `null`, preserved verbatim

                // …and the new waiver still landed (the store is valid again).
                Assert.Contains(await engine.ListWaiversAsync(), w => w.System == "air" && w.RuleId == "NAME-MEASURE" && w.ObjectRef == msRef);
            }
        }
    }
}
