using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// Verified Edits — the measure-faithful evaluation FIDELITY contract (sol round 2). Three legs:
    ///   • PlanEval decision order: target identity FIRST (a bare-ref candidate runs AS the target), the inline
    ///     fast path only for classifiable measure refs, and honest notes on every degraded branch.
    ///   • The spec TRUST KEY: database-name equality is not identity — the session spec is trusted only when the
    ///     session's live origin matches the executing connection on canonical endpoint + database.
    ///   • Fidelity GATES, not annotates: every proof consumer (optimize/apply_plan via the shared evidence
    ///     ladder, workflow verify via 'unavailable', interview scoring, MCP activity labels) must refuse to
    ///     call degraded evidence a proof.
    /// </summary>
    public sealed class MeasureFaithfulFidelityTests
    {
        // ---- B2: PlanEval decision order ------------------------------------------------------------

        [Fact]
        public void Bare_ref_candidate_with_known_target_is_shadowed_as_the_target()
        {
            // A candidate body that is just [Base] must run AS the target M (calc items keyed on M apply),
            // not under [Base]'s own identity — the pre-fix bare-bracket fast path silently did the latter.
            var spec = new DaxQuerySpec { HomeTable = "Sales", TargetMeasureName = "M", ModelMeasureNames = new[] { "Base", "M" } };
            var q = DaxBench.BuildProbeQuery("[Base]", new[] { "'Date'[Year]" }, Array.Empty<string>(), spec, out var note);
            Assert.Contains("MEASURE 'Sales'[M] = (\n[Base]\n    )", q);
            Assert.Contains("\"v\", [M]", q);
            Assert.Null(note);
        }

        [Fact]
        public void Self_referential_candidate_ships_the_circular_define_to_fail_loudly()
        {
            // [M] offered as a rewrite OF M is a circular definition. The DEFINE is emitted as-is so the engine
            // rejects it loudly — the correct verdict on a circular rewrite, never a silent identity switch.
            var spec = new DaxQuerySpec { HomeTable = "Sales", TargetMeasureName = "M", ModelMeasureNames = new[] { "M" } };
            var q = DaxBench.BuildProbeQuery("[M]", Array.Empty<string>(), Array.Empty<string>(), spec, out _);
            Assert.Contains("MEASURE 'Sales'[M] = (\n[M]\n    )", q);
        }

        [Fact]
        public void Unclassifiable_exact_bare_ref_is_inline_WITH_the_note()
        {
            // Pre-fix, the bare-bracket fast path returned silently; an exact [Amount] that cannot be classified
            // as a measure may be an unqualified column ref — inline is right, silence is not.
            var spec = new DaxQuerySpec { ModelMeasureNames = new[] { "Total Sales" } };   // [Amount] is NOT a measure
            var q = DaxBench.BuildProbeQuery("[Amount]", Array.Empty<string>(), Array.Empty<string>(), spec, out var note);
            Assert.DoesNotContain("DEFINE", q);
            Assert.NotNull(note);
            Assert.Contains("[Amount]", note);

            // No inventory at all (no spec) — unclassifiable too.
            DaxBench.BuildProbeQuery("[Amount]", Array.Empty<string>(), Array.Empty<string>(), null, out var note2);
            Assert.NotNull(note2);
        }

        [Fact]
        public void Classified_bare_ref_keeps_the_note_free_inline_fast_path()
        {
            var spec = new DaxQuerySpec { ModelMeasureNames = new[] { "Total Sales" } };
            var q = DaxBench.BuildProbeQuery("[Total Sales]", Array.Empty<string>(), Array.Empty<string>(), spec, out var note);
            Assert.DoesNotContain("DEFINE", q);        // identity-exact for itself
            Assert.Null(note);
        }

        [Fact]
        public void No_derivable_host_notes_when_measures_are_referenced_or_calc_groups_exist()
        {
            // Measure-only composite, no table ref, no fallback home: inline is unavoidable, and the application
            // point of calc items/identity differs from a deployed measure — the note must say so.
            var spec = new DaxQuerySpec { ModelMeasureNames = new[] { "A", "B" } };   // HomeTable deliberately null
            DaxBench.BuildProbeQuery("[A] + [B]", Array.Empty<string>(), Array.Empty<string>(), spec, out var note);
            Assert.NotNull(note);
            Assert.Contains("no DEFINE MEASURE host table is derivable", note);

            // Calc groups alone also warrant it, even for a constant.
            var cg = new DaxQuerySpec { ModelMeasureNames = Array.Empty<string>(), ModelHasCalcGroups = true };
            DaxBench.BuildProbeQuery("1 + 1", Array.Empty<string>(), Array.Empty<string>(), cg, out var cgNote);
            Assert.NotNull(cgNote);

            // A pure constant on a calc-group-free model stays silent (nothing is lost vs a deployed measure).
            var plain = new DaxQuerySpec { ModelMeasureNames = Array.Empty<string>() };
            DaxBench.BuildProbeQuery("1 + 1", Array.Empty<string>(), Array.Empty<string>(), plain, out var quiet);
            Assert.Null(quiet);
        }

        [Fact]
        public void Comparison_with_known_target_uses_the_real_home_over_the_expression_table()
        {
            // The target's REAL home binds unqualified refs correctly — it must beat expression derivation.
            var spec = new DaxQuerySpec { HomeTable = "TargetHome", TargetMeasureName = "M", ModelMeasureNames = new[] { "M" } };
            var q = DaxBench.BuildComparisonQuery("SUM ( 'Sales'[Amt] )", "SUM ( 'Sales'[Amt] ) + 0", new[] { "'Date'[Year]" }, null, spec);
            Assert.Matches(@"MEASURE 'TargetHome'\[__smx_[0-9a-f]{8}_A\]", q);   // real home + generated ids (two bodies, one identity)
            Assert.Matches(@"MEASURE 'TargetHome'\[__smx_[0-9a-f]{8}_B\]", q);
        }

        // ---- B3: the spec trust key -----------------------------------------------------------------

        [Fact]
        public void Same_database_name_on_a_different_endpoint_stays_untrusted()
        {
            Assert.False(LocalEngine.TrustSessionSpec(
                "powerbi://api.powerbi.com/v1.0/myorg/Workspace-A", "Contoso Sales",
                "powerbi://api.powerbi.com/v1.0/myorg/Workspace-B", "Contoso Sales"));
        }

        [Fact]
        public void Canonical_endpoint_variance_still_trusts()
        {
            Assert.True(LocalEngine.TrustSessionSpec(
                "powerbi://api.powerbi.com/v1.0/myorg/Team", "Contoso Sales",
                "POWERBI://api.powerbi.com/v1.0/myorg/Team/", "contoso sales"));
            Assert.True(LocalEngine.TrustSessionSpec("localhost:56789", "guid-db", "LOCALHOST:56789", "guid-db"));
        }

        [Fact]
        public void Missing_origin_fields_fail_closed()
        {
            Assert.False(LocalEngine.TrustSessionSpec(null, "Db", "endpoint", "Db"));
            Assert.False(LocalEngine.TrustSessionSpec("endpoint", null, "endpoint", "Db"));
            Assert.False(LocalEngine.TrustSessionSpec("", "", "endpoint", "Db"));
            Assert.False(LocalEngine.TrustSessionSpec("endpoint", "Db", null, null));
        }

        // ---- SF4: one lexer — bracketed identifiers are names, not comment/string carriers -----------

        [Fact]
        public void Lexer_preserves_comment_and_string_markers_inside_bracketed_identifiers()
        {
            var s = DaxBench.StripCommentsAndStrings("[Gross -- Net] + [A // B] + [Caption \"Q1\"] -- real comment");
            Assert.Contains("[Gross -- Net]", s);
            Assert.Contains("[A // B]", s);
            Assert.Contains("[Caption \"Q1\"]", s);
            Assert.DoesNotContain("real comment", s);      // a REAL trailing comment is still stripped
        }

        [Fact]
        public void Bracketed_identifier_with_comment_marker_still_classifies_as_a_measure()
        {
            // Pre-fix the lexer damaged [Gross -- Net] before ScanRefs, so it could never match the inventory.
            var spec = new DaxQuerySpec { ModelMeasureNames = new[] { "Gross -- Net" } };
            var q = DaxBench.BuildProbeQuery("[Gross -- Net]", Array.Empty<string>(), Array.Empty<string>(), spec, out var note);
            Assert.DoesNotContain("DEFINE", q);            // classified measure ref → inline fast path
            Assert.Null(note);
        }

        [Fact]
        public void Qualified_ref_with_marker_in_the_column_name_keeps_its_host()
        {
            var q = DaxBench.BuildProbeQuery("SUM ( 'Sales'[Gross -- Net] )", Array.Empty<string>(), Array.Empty<string>());
            Assert.Contains("MEASURE 'Sales'[", q);        // host survives the marker inside the column name
            Assert.Contains("SUM ( 'Sales'[Gross -- Net] )", q);   // the body is spliced UNdamaged
        }

        // ---- B1: fidelity gates every proof consumer --------------------------------------------------

        private static EquivalenceResult Eq(bool allMatch = true, int rows = 3, bool truncated = false,
            string fidelity = null, string error = null, int mismatches = 0) => new EquivalenceResult
        { AllMatch = allMatch, RowsCompared = rows, Truncated = truncated, Fidelity = fidelity, Error = error, MismatchCount = mismatches };

        [Fact]
        public void Evidence_ladder_grades_each_rung_in_order()
        {
            Assert.Equal("unverified", DaxBench.ClassifyEquivalenceEvidence(Eq(error: "boom"), 1).State);
            Assert.Equal("degraded_mismatch", DaxBench.ClassifyEquivalenceEvidence(Eq(allMatch: false, mismatches: 1, fidelity: "calc groups"), 1).State);
            Assert.Equal("failed", DaxBench.ClassifyEquivalenceEvidence(Eq(allMatch: false, mismatches: 2), 1).State);
            Assert.Equal("unverified", DaxBench.ClassifyEquivalenceEvidence(Eq(rows: 0), 1).State);
            Assert.Equal("unverified", DaxBench.ClassifyEquivalenceEvidence(Eq(truncated: true), 1).State);
            Assert.Equal("degraded", DaxBench.ClassifyEquivalenceEvidence(Eq(fidelity: "calc groups"), 1).State);
            Assert.Equal("thin", DaxBench.ClassifyEquivalenceEvidence(Eq(), 0).State);
            Assert.Equal("proven", DaxBench.ClassifyEquivalenceEvidence(Eq(), 1).State);
        }

        [Fact]
        public void Degraded_fidelity_outranks_the_thin_grid_rung()
        {
            // apply_plan ships a thin-grid match with an honest label — a DEGRADED thin-grid match must not ride
            // that path, so fidelity must dominate.
            var (state, note) = DaxBench.ClassifyEquivalenceEvidence(Eq(fidelity: "inline fallback"), 0);
            Assert.Equal("degraded", state);
            Assert.Contains("REDUCED fidelity", note);
        }

        [Fact]
        public void A_mismatch_under_degraded_fidelity_is_an_observation_not_a_conviction()
        {
            // The reduced-fidelity surrogate itself can cause the divergence (calc-group identity on generated
            // names) — so fidelity sits BEFORE mismatch: blocks like failed, but is never reported as a proven
            // behavior change.
            var (state, note) = DaxBench.ClassifyEquivalenceEvidence(Eq(allMatch: false, mismatches: 1, fidelity: "x"), 1);
            Assert.Equal("degraded_mismatch", state);
            Assert.Contains("not authoritative", note);
        }

        [Fact]
        public void Interview_never_grades_Correct_on_degraded_agreement()
        {
            var grid = new[] { "'Date'[Year]" };
            var q = new InterviewQuestion { Question = "total?", Tier = "paraphrase", ScalarExpr = "[A]", ParaphraseExpr = "[B]", GroupBy = grid };
            var (outcome, detail) = InterviewScoring.ScoreParaphrase(q, Eq(fidelity: "reduced fidelity reason"));
            Assert.Equal(InterviewScoring.Unverified, outcome);
            Assert.Contains("reduced fidelity", detail);

            // …a degraded MISMATCH is an observation, not the confidently-wrong conviction (round 3).
            var (wrong, wrongDetail) = InterviewScoring.ScoreParaphrase(q, Eq(allMatch: false, mismatches: 1, fidelity: "reason"));
            Assert.Equal(InterviewScoring.Unverified, wrong);
            Assert.Contains("not authoritative", wrongDetail);

            // …a PROVEN mismatch is still SilentlyWrong (no fidelity caveat, real divergence).
            var (conv, _) = InterviewScoring.ScoreParaphrase(q, Eq(allMatch: false, mismatches: 1));
            Assert.Equal(InterviewScoring.SilentlyWrong, conv);

            // …and a clean per-context agreement still grades Correct (the gate adds no false negatives).
            var (ok, _) = InterviewScoring.ScoreParaphrase(q, Eq());
            Assert.Equal(InterviewScoring.Correct, ok);
        }

        [Fact]
        public void Interview_thin_agreement_never_reaches_Correct()
        {
            // Grand-total-only agreement used to grade Correct with a parenthetical — now the ONE ladder gates it.
            var q = new InterviewQuestion { Question = "total?", Tier = "paraphrase", ScalarExpr = "[A]", ParaphraseExpr = "[B]" };   // no GroupBy
            var (outcome, detail) = InterviewScoring.ScoreParaphrase(q, Eq(rows: 1));
            Assert.Equal(InterviewScoring.Unverified, outcome);
            Assert.Contains("grand total", detail);
        }

        // A minimal hard-gated one-verify workflow for the kernel test.
        private const string HardMd = @"---
name: fidelity-gate-test
title: Fidelity gate test
version: 1
strictness: hard
---

## Step 1: Verify

```yaml gate
verify:
  - kind: dax_equivalence
    probe: original
```
";

        [Fact]
        public async Task Workflow_hard_gate_blocks_an_unavailable_verify()
        {
            var def = WorkflowParser.Parse(HardMd);
            Assert.Null(def.Error);
            var run = new WorkflowRunStore().Start(def, null);
            WorkflowVerifyExecutor degraded = (spec, step, r, a) => Task.FromResult(new VerifyResult
            { Kind = spec.Kind, Status = "unavailable", Detail = "equivalence compared (degraded: calc groups) — not authoritative." });

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => WorkflowRunner.SubmitStepAsync(run, "step-1", null, degraded));
            Assert.Contains("hard gate", ex.Message);
            Assert.Contains("degraded", ex.Message);
            Assert.Equal("active", run.Status);            // the run did NOT advance past the gate
            Assert.Equal("failed", run.Results[0].Status);

            // skip_workflow_step remains the audited override.
            WorkflowRunner.SkipStep(run, "step-1", "accepting degraded evidence for this rehearsal");
            Assert.Equal("completed", run.Status);
        }

        [Fact]
        public async Task Workflow_hard_gate_still_passes_a_clean_verify()
        {
            var def = WorkflowParser.Parse(HardMd);
            var run = new WorkflowRunStore().Start(def, null);
            WorkflowVerifyExecutor passing = (spec, step, r, a) => Task.FromResult(new VerifyResult
            { Kind = spec.Kind, Status = "passed", Detail = "equivalent across 12 contexts" });
            await WorkflowRunner.SubmitStepAsync(run, "step-1", null, passing);
            Assert.Equal("completed", run.Status);
        }

        [Fact]
        public void Mcp_label_follows_the_evidence_ladder_never_claiming_verified_over_caveats()
        {
            var grid = new[] { "'Date'[Year]" };

            // Degraded rungs say "Compared", never "Verified".
            var degradedMatch = McpTools.EquivalenceLabel(Eq(fidelity: "the model contains calculation groups"), grid);
            Assert.StartsWith("Compared a rewrite (degraded:", degradedMatch);
            Assert.DoesNotContain("Verified", degradedMatch);
            Assert.Contains("NOT verified", degradedMatch);
            Assert.Equal("Compared a rewrite (degraded) — difference observed, not authoritative",
                McpTools.EquivalenceLabel(Eq(allMatch: false, mismatches: 1, fidelity: "x"), grid));

            // The rungs the pre-fix label lied about: zero rows / truncated / thin / error are NOT "Verified".
            Assert.StartsWith("Compared a rewrite — could not verify", McpTools.EquivalenceLabel(Eq(rows: 0), grid));
            Assert.StartsWith("Compared a rewrite — could not verify", McpTools.EquivalenceLabel(Eq(truncated: true), grid));
            Assert.StartsWith("Compared a rewrite — could not verify", McpTools.EquivalenceLabel(Eq(error: "boom"), grid));
            Assert.Equal("Compared a rewrite — matched at the grand total only — NOT verified",
                McpTools.EquivalenceLabel(Eq(), null));

            // Only the authoritative rungs earn "Verified".
            Assert.Equal("Verified a rewrite — equivalent", McpTools.EquivalenceLabel(Eq(), grid));
            Assert.Equal("Verified a rewrite — NOT equivalent", McpTools.EquivalenceLabel(Eq(allMatch: false, mismatches: 1), grid));
        }

        // ---- round 3: untrusted spec degrades, never vanishes -----------------------------------------

        [Fact]
        public void Untrusted_spec_ignores_identity_and_notes_unknowable_calc_groups()
        {
            // The untrusted marker carries session facts about a possibly DIFFERENT model: identity/inventory/home
            // must be ignored (no shadow, no spec host), and the DEFINE path must carry the honest note because
            // calc-group presence on the CONNECTED model is unknowable. Ambiguity degrades; it never proves.
            var spec = new DaxQuerySpec { Trusted = false, HomeTable = "SessionOnly", TargetMeasureName = "M", ModelMeasureNames = new[] { "M", "Base" } };
            var q = DaxBench.BuildProbeQuery("SUM ( 'Sales'[Amt] )", new[] { "'Date'[Year]" }, Array.Empty<string>(), spec, out var note);
            Assert.DoesNotContain("SessionOnly", q);                       // untrusted home table never used
            Assert.DoesNotContain("MEASURE 'Sales'[M]", q);                // untrusted identity never shadowed
            Assert.Matches(@"MEASURE 'Sales'\[__smx_[0-9a-f]{8}_probe\]", q);   // expression-derived host + generated id
            Assert.NotNull(note);                                          // fidelity is degraded and SAYS so
            Assert.Contains("could not be matched", note);
        }

        [Fact]
        public void Untrusted_spec_cannot_classify_bare_refs_so_they_note_and_inline()
        {
            var spec = new DaxQuerySpec { Trusted = false, ModelMeasureNames = new[] { "Total Sales" } };   // inventory unusable
            var q = DaxBench.BuildProbeQuery("[Total Sales]", Array.Empty<string>(), Array.Empty<string>(), spec, out var note);
            Assert.DoesNotContain("DEFINE", q);
            Assert.NotNull(note);                                          // pre-fix: silently proven-capable
        }

        [Fact]
        public async Task Untrusted_comparison_carries_fidelity_end_to_end()
        {
            // Through the verify core: an untrusted spec must surface Fidelity on the RESULT, which the evidence
            // ladder then grades degraded — the silent-proven hole this round closes.
            var rs = new ResultSet
            {
                Columns = new[] { new ColumnDef { Name = "'Date'[Year]" }, new ColumnDef { Name = "__A" }, new ColumnDef { Name = "__B" } },
                Rows = new[] { new object[] { 2024, 1.0, 1.0 } },
                RowCount = 1,
            };
            Func<string, int, Task<ResultSet>> exec = (query, mr) => Task.FromResult(rs);
            var eq = await DaxBench.VerifyEquivalenceCoreAsync(exec, "SUM ( 'Sales'[Amt] )", "SUM ( 'Sales'[Amt] ) + 0",
                new[] { "'Date'[Year]" }, null, 100000, new DaxQuerySpec { Trusted = false });
            Assert.Null(eq.Error);
            Assert.True(eq.AllMatch);
            Assert.NotNull(eq.Fidelity);
            Assert.Equal("degraded", DaxBench.ClassifyEquivalenceEvidence(eq, 1).State);
        }

        // ---- round 3: circular self-reference is validated BEFORE comparison/apply --------------------

        [Fact]
        public void ReferencesTarget_is_token_aware_bare_and_home_qualified()
        {
            Assert.True(DaxBench.ReferencesTarget("[M] + 0", "M"));
            Assert.True(DaxBench.ReferencesTarget("CALCULATE ( [m] )", "M"));            // names are case-insensitive
            Assert.True(DaxBench.ReferencesTarget("[Total ]] Sales]", "Total ] Sales")); // ]] unescaped by the scanner
            Assert.False(DaxBench.ReferencesTarget("-- [M]\n1 + 1", "M"));               // comments don't count
            Assert.False(DaxBench.ReferencesTarget("LEN ( \"[M]\" )", "M"));             // strings don't count
            Assert.False(DaxBench.ReferencesTarget("[Other]", "M"));

            // QUALIFIED self-reference: 'Home'[M] resolves to the measure — equally circular WHEN the table is the
            // target's home. A same-named ref on a DIFFERENT table is a column (measure names are model-unique).
            Assert.True(DaxBench.ReferencesTarget("SUM ( 'Sales'[M] )", "M", "Sales"));
            Assert.True(DaxBench.ReferencesTarget("SUM ( sales[m] )", "M", "Sales"));            // both parts case-insensitive
            Assert.True(DaxBench.ReferencesTarget("'O''Brien Sales'[M] + 0", "M", "O'Brien Sales"));   // '' unescaped
            Assert.False(DaxBench.ReferencesTarget("SUM ( 'Other'[M] )", "M", "Sales"));         // different table = a column
            Assert.False(DaxBench.ReferencesTarget("SUM ( 'Sales'[Amount] )", "M", "Sales"));    // different name
            Assert.False(DaxBench.ReferencesTarget("SUM ( 'Sales'[M] )", "M"));                  // no home known = bare-only contract
        }

        [Fact]
        public async Task Optimize_rejects_a_circular_candidate_before_any_comparison()
        {
            using var engine = new LocalEngine(new SessionManager());
            await engine.OpenAsync(TestModels.FindBim());
            var target = (await engine.ListMeasuresAsync()).First();

            // Candidate 0 bare-references the target itself; two clean constants keep the race alive so the
            // rejection is visibly per-candidate (not the <2-valid refusal).
            var r = await engine.OptimizeMeasureAsync(target.Ref,
                new[] { $"[{target.Name}] + 0", "1 + 1", "2 + 2" }, new[] { "'Date'[Year]" }, null, apply: false, "human");

            var circular = r.Candidates.First(c => c.Index == 0);
            Assert.Equal("invalid", circular.VerifyState);                 // never reaches proven / the race
            Assert.Contains("circular rewrite", circular.Note);
            Assert.Equal("unproven-offline", r.Verdict);                   // offline harness: the others degrade honestly
        }

        [Fact]
        public async Task Apply_plan_rejects_a_circular_set_dax_item_pre_mutation()
        {
            using var engine = new LocalEngine(new SessionManager());
            await engine.OpenAsync(TestModels.FindBim());
            var target = (await engine.ListMeasuresAsync()).First();
            var before = await engine.GetDaxAsync(target.Ref);

            var plan = await engine.AddPlanItemAsync(target.Ref, "set_dax", $"[{target.Name}] * 2", "circular rewrite", null, null, "human");
            var item = Assert.Single(plan.Items);
            await engine.SetPlanItemAsync(item.Id, null, approved: true, "human");
            var report = await engine.ApplyPlanAsync(new[] { item.Id }, "human");

            var applied = report.Items.Single(i => i.Id == item.Id);
            Assert.Equal("rejected", applied.Status);                      // structurally rejected, not shipped
            Assert.Equal("failed", applied.VerifyState);
            Assert.Contains("circular rewrite", applied.Note);
            Assert.Equal(before, await engine.GetDaxAsync(target.Ref));    // PRE-mutation: the body is untouched

            // Round 5: the rejection is VISIBLE in the report telemetry — never "0 applied · 0 skipped · 0 failed"
            // reading like a clean no-op.
            Assert.Equal(1, report.RejectedCount);
            Assert.Contains("1 rejected", report.Note);
            Assert.False(report.Distillable);                              // a plan with an impossible edit is no recipe
        }

        [Fact]
        public async Task Apply_plan_circular_rejection_is_immune_to_overrideIds()
        {
            // overrideIds is the accountable path past a FAILED/unprovable equivalence VERDICT — a judgment call.
            // Circularity is a structural impossibility, so the override must not resurrect the item: pre-fix,
            // FailVerify kept an overridden item in toApply and a later equivalence pass could flip it verified.
            using var engine = new LocalEngine(new SessionManager());
            await engine.OpenAsync(TestModels.FindBim());
            var target = (await engine.ListMeasuresAsync()).First();
            var before = await engine.GetDaxAsync(target.Ref);

            // QUALIFIED self-reference this time ('Home'[M]) — the round-4 circular shape.
            var plan = await engine.AddPlanItemAsync(target.Ref, "set_dax", $"'{target.Table}'[{target.Name}] * 2", "circular rewrite", null, null, "human");
            var item = Assert.Single(plan.Items);
            await engine.SetPlanItemAsync(item.Id, null, approved: true, "human");
            var report = await engine.ApplyPlanAsync(new[] { item.Id }, "human",
                overrideIds: new[] { item.Id }, overrideReason: "trying to force the circular rewrite through");

            var final = report.Items.Single(i => i.Id == item.Id);
            Assert.Equal("rejected", final.Status);                        // the override did NOT resurrect it
            Assert.NotEqual("applied", final.Status);
            Assert.Contains("circular rewrite", final.Note);
            Assert.Equal(0, report.AppliedCount);                          // nothing mutated
            Assert.Equal(1, report.RejectedCount);                         // and the telemetry says why it was a no-op
            Assert.Equal(before, await engine.GetDaxAsync(target.Ref));    // the body is untouched
        }

        // ---- round 4: whitespace group-by cannot masquerade as a matrix --------------------------------

        [Fact]
        public void NormalizeGroupBy_trims_and_drops_blanks()
        {
            Assert.Equal(new[] { "'Date'[Year]" }, DaxBench.NormalizeGroupBy(new[] { " ", "'Date'[Year] ", null, "" }));
            Assert.Empty(DaxBench.NormalizeGroupBy(new[] { " ", "\t" }));
            Assert.Empty(DaxBench.NormalizeGroupBy(null));
        }

        [Fact]
        public void Interview_whitespace_grid_grades_thin_not_proven()
        {
            // GroupBy [" "] runs grand-total-only (query construction drops blanks) — the raw Length must not
            // let it classify as a per-context proof.
            var q = new InterviewQuestion { Question = "q", Tier = "paraphrase", ScalarExpr = "[A]", ParaphraseExpr = "[B]", GroupBy = new[] { " " } };
            var (outcome, detail) = InterviewScoring.ScoreParaphrase(q, Eq(rows: 1));
            Assert.Equal(InterviewScoring.Unverified, outcome);
            Assert.Contains("grand total", detail);
        }

        [Fact]
        public void Mcp_label_whitespace_grid_is_thin_not_verified()
        {
            Assert.Equal("Compared a rewrite — matched at the grand total only — NOT verified",
                McpTools.EquivalenceLabel(Eq(), new[] { "  " }));
        }

        [Fact]
        public async Task Add_plan_item_whitespace_matrix_is_opt_in_like_an_empty_one()
        {
            // A set_dax with verifyGroupBy [" "] proves only the grand total — it must be OPT-IN (proposed), the
            // same as []; pre-fix the raw Length==1 made it land pre-approved with auto-apply reachable.
            using var engine = new LocalEngine(new SessionManager());
            await engine.OpenAsync(TestModels.FindBim());
            var target = (await engine.ListMeasuresAsync()).First();
            var plan = await engine.AddPlanItemAsync(target.Ref, "set_dax", "1 + 1", "whitespace matrix", new[] { " " }, null, "human");
            Assert.Equal("proposed", Assert.Single(plan.Items).Status);
        }

        // ---- round 4: fidelity survives an execution error ---------------------------------------------

        [Fact]
        public void Error_rung_keeps_the_degraded_evaluation_caveat()
        {
            var (state, note) = DaxBench.ClassifyEquivalenceEvidence(Eq(error: "boom", fidelity: "untrusted spec"), 1);
            Assert.Equal("unverified", state);                             // error still ranks first
            Assert.Contains("boom", note);
            Assert.Contains("degraded evaluation", note);                  // …but the caveat survives into the detail
            Assert.Contains("untrusted spec", note);
        }

        [Fact]
        public async Task Verify_core_error_results_carry_the_fidelity_note()
        {
            Func<string, int, Task<ResultSet>> exec = (query, mr) => Task.FromResult(new ResultSet { Error = "query failed" });
            var eq = await DaxBench.VerifyEquivalenceCoreAsync(exec, "SUM ( 'Sales'[Amt] )", "SUM ( 'Sales'[Amt] ) + 0",
                new[] { "'Date'[Year]" }, null, 100000, new DaxQuerySpec { Trusted = false });
            Assert.Equal("query failed", eq.Error);
            Assert.NotNull(eq.Fidelity);                                   // the degraded-evaluation context is not lost
        }

        // ---- round 3: endpoint path case-sensitivity ---------------------------------------------------

        [Fact]
        public void Endpoint_path_case_difference_is_ambiguity_and_stays_untrusted()
        {
            // Host (and scheme) fold; the PATH does not — a case-differing workspace path could be a different
            // workspace on a case-sensitive route, and ambiguity always fails closed.
            Assert.False(LocalEngine.TrustSessionSpec(
                "powerbi://api.powerbi.com/v1.0/myorg/Team", "Db",
                "powerbi://api.powerbi.com/v1.0/myorg/team", "Db"));
            Assert.True(LocalEngine.TrustSessionSpec(
                "POWERBI://API.POWERBI.COM/v1.0/myorg/Team", "Db",
                "powerbi://api.powerbi.com/v1.0/myorg/Team", "Db"));
        }
    }
}
