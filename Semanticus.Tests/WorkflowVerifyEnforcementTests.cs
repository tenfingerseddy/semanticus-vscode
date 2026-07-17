using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.Entitlement;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// The E1+E2+E3 workflow-verify enforcement package (docs v5-engine-contract):
    ///   E2 — three-way-plus outcomes: not_applicable (conditional, advances) vs unavailable (applicable but no
    ///        evidence, BLOCKS a hard step like failed); zero coverage blocks.
    ///   E1 — the dax_equivalence SHAPE LEDGER: pinned mismatches FAIL, open mismatches are RECORDED (via the pure
    ///        EquivalenceGate.Evaluate, unit-tested offline with a hand-built comparison).
    ///   E3 — receipts: the witness lock + revision receipt, and candidate-drift detection.
    /// </summary>
    public sealed class WorkflowVerifyEnforcementTests
    {
        private sealed class Pro : IEntitlement { public bool IsPro => true; public EntitlementInfo Info => new EntitlementInfo { Tier = "pro" }; }

        private static AnswerValue Answer(string v) => new AnswerValue { Value = v };
        private static AnswerValue Decline(string r) => new AnswerValue { Declined = true, DeclineReason = r };

        private static string NewWorkspace()
        {
            var ws = Path.Combine(Path.GetTempPath(), "smx-e123-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(Path.Combine(ws, ".semanticus", "workflows"));
            return ws;
        }
        private static void WriteUserWorkflow(string ws, string file, string md) =>
            File.WriteAllText(Path.Combine(ws, ".semanticus", "workflows", file), md);

        // ============================ E2 — runner outcome semantics ============================

        private const string OneVerifyMd = @"---
name: one-verify
title: One verify
strictness: {STRICT}
---
## Step 1: Prove
Prove a thing.
```yaml gate
verify:
  - kind: dax_probe
```
";

        [Fact]
        public async Task Unavailable_blocks_a_hard_step_like_failed()
        {
            var run = new WorkflowRunStore().Start(WorkflowParser.Parse(OneVerifyMd.Replace("{STRICT}", "hard")), null);
            WorkflowVerifyExecutor unavail = (s, st, r, a) =>
                Task.FromResult(new VerifyResult { Kind = s.Kind, Status = "unavailable", Missing = "a live connection", Detail = "offline" });

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => WorkflowRunner.SubmitStepAsync(run, "step-1", null, unavail));
            Assert.Contains("unavailable", ex.Message);
            Assert.Contains("live connection", ex.Message);          // the payload names what was missing
            Assert.Equal("failed", run.Results[0].Status);           // hard gate: stays on the blocked step
            Assert.Equal("unavailable", run.Results[0].VerifyResults[0].Status);
        }

        [Fact]
        public async Task Unavailable_on_a_warn_step_records_but_advances()
        {
            var run = new WorkflowRunStore().Start(WorkflowParser.Parse(OneVerifyMd.Replace("{STRICT}", "warn")), null);
            WorkflowVerifyExecutor unavail = (s, st, r, a) =>
                Task.FromResult(new VerifyResult { Kind = s.Kind, Status = "unavailable", Missing = "a live connection", Detail = "offline" });

            await WorkflowRunner.SubmitStepAsync(run, "step-1", null, unavail);   // does NOT throw at warn
            Assert.Equal("passed", run.Results[0].Status);
            Assert.Equal("completed", run.Status);
            Assert.Contains("warn gate", run.Results[0].Note);
            Assert.Contains("unavailable", run.Results[0].Note);
        }

        private const string ConditionalHardMd = @"---
name: cond-hard
title: Conditional hard
strictness: hard
---
## Step 1: Maybe prove
Maybe prove a thing.
```yaml gate
inputs:
  - name: witness
    question: ""A witness?""
    required: answer-or-decline
verify:
  - kind: dax_probe
    when: inputs.witness.answered
    probe: witness
```
";

        [Fact]
        public async Task Conditional_when_false_is_not_applicable_and_advances_a_hard_step()
        {
            var run = new WorkflowRunStore().Start(WorkflowParser.Parse(ConditionalHardMd), null);
            // Decline the witness → the verify's when: does not hold → not_applicable → the hard step advances.
            await WorkflowRunner.SubmitStepAsync(run, "step-1",
                new Dictionary<string, AnswerValue> { ["witness"] = Decline("no reference figure") }, null);

            Assert.Equal("passed", run.Results[0].Status);
            Assert.Equal("completed", run.Status);
            Assert.Equal("not_applicable", run.Results[0].VerifyResults[0].Status);
        }

        // ============================ E1 — shape ledger (pure gate over the shared ladder) ============================

        private static ShapeComparison Shape(string id, int rows, params (string ctx, string a, string b)[] mm) => new ShapeComparison
        {
            ShapeId = id, RowsCompared = rows, MismatchCount = mm.Length,
            Mismatches = mm.Select(x => new EquivalenceMismatch { Context = x.ctx, ValueA = x.a, ValueB = x.b }).ToArray(),
        };
        private static EquivalenceResult Eq(int leafRows, bool truncated, params ShapeComparison[] shapes) => new EquivalenceResult
        {
            AllMatch = shapes.All(s => s.MismatchCount == 0), RowsCompared = leafRows, Truncated = truncated,
            MismatchCount = shapes.Sum(s => s.MismatchCount), Shapes = shapes,
            Mismatches = shapes.SelectMany(s => s.Mismatches).ToArray(),
        };
        private static readonly string[] None = Array.Empty<string>();

        [Fact]
        public void Default_all_pinned_fails_on_any_shape_mismatch()
        {
            var eq = Eq(2, false, Shape("grand_total", 1), Shape("axis:Cat", 2), Shape("cross", 2, ("Cat=Bikes", "10", "9")));
            var o = EquivalenceGate.Evaluate(eq, None, None, 2);
            Assert.Equal("failed", o.Status);
            Assert.Contains(o.Shapes, s => s.ShapeId == "cross" && s.Pinned && s.MismatchCount == 1);
            Assert.Contains(o.MismatchCells, c => c.Context == "Cat=Bikes" && c.ValueA == "10" && c.ValueB == "9");
        }

        [Fact]
        public void Open_shape_mismatch_is_recorded_and_still_passes()
        {
            // Grand total diverges but is declared OPEN; every PINNED shape agrees → pass, with the divergence recorded.
            var eq = Eq(2, false, Shape("grand_total", 1, ("(grand total)", "100", "90")), Shape("axis:Cat", 2), Shape("cross", 2));
            var o = EquivalenceGate.Evaluate(eq, None, new[] { "grand_total" }, 2);
            Assert.Equal("passed", o.Status);
            var gt = o.Shapes.Single(s => s.ShapeId == "grand_total");
            Assert.False(gt.Pinned);
            Assert.Equal(1, gt.MismatchCount);
            Assert.Contains("OPEN", o.Detail);                         // observed, not gated
        }

        [Fact]
        public void Pinned_mismatch_fails_even_when_an_open_shape_also_diverges()
        {
            // grand_total OPEN (diverges), but the PINNED axis subtotal diverges too → the gate FAILS on the pinned cell.
            var eq = Eq(2, false,
                Shape("grand_total", 1, ("(grand total)", "100", "90")),
                Shape("axis:Cat", 2, ("Cat=Bikes", "5", "4")),
                Shape("cross", 2));
            var o = EquivalenceGate.Evaluate(eq, None, new[] { "grand_total" }, 2);
            Assert.Equal("failed", o.Status);
            Assert.Equal("Cat=Bikes", o.MismatchCells[0].Context);     // pinned cell listed first for adjudication
        }

        [Fact]
        public void Zero_coverage_is_unavailable_not_a_pass()
        {
            var eq = Eq(0, false, Shape("grand_total", 1), Shape("axis:Cat", 0), Shape("cross", 0));
            var o = EquivalenceGate.Evaluate(eq, None, None, 2);
            Assert.Equal("unavailable", o.Status);
            Assert.Contains("0 rows", o.Detail);                       // the ladder's zero-coverage rung, verbatim
            Assert.Contains("rows to compare", o.Missing);
        }

        [Fact]
        public void Zero_coverage_blocks_even_when_the_only_mismatches_are_open()
        {
            // Open-only mismatch + zero grid coverage: setting the open mismatch aside must NOT promote the residual
            // to a pass — the ladder re-grades it as zero-coverage → unavailable.
            var eq = Eq(0, false, Shape("grand_total", 1, ("(grand total)", "1", "2")), Shape("axis:Cat", 0), Shape("cross", 0));
            var o = EquivalenceGate.Evaluate(eq, None, new[] { "grand_total" }, 2);
            Assert.Equal("unavailable", o.Status);
            Assert.Contains("0 rows", o.Detail);
            Assert.Contains("OPEN", o.Detail);                         // the observation still rides the payload
        }

        [Fact]
        public void Truncated_coverage_is_unavailable()
        {
            var eq = Eq(100000, true, Shape("cross", 100000));
            var o = EquivalenceGate.Evaluate(eq, None, None, 2);
            Assert.Equal("unavailable", o.Status);
            Assert.Contains("row cap", o.Detail);
        }

        [Fact]
        public void A_query_error_is_unavailable()
        {
            var o = EquivalenceGate.Evaluate(new EquivalenceResult { Error = "boom" }, None, None, 2);
            Assert.Equal("unavailable", o.Status);
            Assert.Contains("boom", o.Detail);
        }

        // ---- the E5 crossings: the shared evidence ladder feeding the workflow gate ----

        [Fact]
        public void Degraded_mismatch_is_unavailable_not_failed_with_the_fidelity_in_missing()
        {
            // A mismatch under a DEGRADED comparison is an observation, not a conviction (the surrogate itself can
            // cause the divergence) — even on a PINNED shape it must not hard-fail as a proven behavior change.
            var eq = Eq(2, false, Shape("grand_total", 1), Shape("cross", 2, ("Cat=Bikes", "10", "9")));
            eq.Fidelity = "candidates ran under generated names; calc-group identity semantics may differ";
            var o = EquivalenceGate.Evaluate(eq, None, None, 2);
            Assert.Equal("unavailable", o.Status);
            Assert.Contains("calc-group identity", o.Missing);          // the fidelity text names what's missing
            Assert.Contains("DEGRADED", o.Detail);                      // the ladder's degraded_mismatch note, verbatim
        }

        [Fact]
        public void Degraded_match_is_unavailable_with_the_fidelity_in_missing()
        {
            var eq = Eq(2, false, Shape("grand_total", 1), Shape("cross", 2));
            eq.Fidelity = "evaluated INLINE: no DEFINE MEASURE host table is derivable";
            var o = EquivalenceGate.Evaluate(eq, None, None, 2);
            Assert.Equal("unavailable", o.Status);
            Assert.Contains("INLINE", o.Missing);
            Assert.Contains("REDUCED fidelity", o.Detail);
        }

        [Fact]
        public async Task Whitespace_only_grid_is_unavailable_zero_effective_pinned_coverage()
        {
            // A whitespace grid entry normalizes to an EMPTY effective grid (DaxBench.NormalizeGroupBy): the only
            // evaluated shape is the grand total, the ladder grades it 'thin', and the workflow gate blocks it —
            // a raw .Length would have classified this as a per-context proof (the whitespace bypass).
            var grid = new[] { "   " };
            Func<string, int, Task<ResultSet>> fake = (q, mr) => Task.FromResult(new ResultSet
            {
                Columns = new[] { new ColumnDef { Name = "__A" }, new ColumnDef { Name = "__B" } },
                Rows = new[] { new object[] { 42.0, 42.0 } },
                RowCount = 1,
            });
            // Table-qualified expressions: PlanEval derives the DEFINE host from the expression itself, so the
            // comparison is FULL fidelity and the thin rung (not a degraded rung) is what blocks.
            var eq = await DaxBench.VerifyEquivalenceCoreAsync(fake, "SUM('Sales'[Amount])", "SUMX('Sales', 'Sales'[Amount])", grid, null, 100000);
            Assert.Null(eq.Error);
            Assert.True(eq.AllMatch);
            Assert.Null(eq.Fidelity);

            var o = EquivalenceGate.Evaluate(eq, None, None, DaxBench.NormalizeGroupBy(grid).Length);
            Assert.Equal("unavailable", o.Status);
            Assert.Contains("per-context equivalence grid", o.Missing);
            Assert.Contains("grand-total match only", o.Detail);        // the ladder's thin rung, verbatim
        }

        [Fact]
        public async Task Untrusted_spec_degrades_the_proof_to_unavailable_with_the_fidelity_in_missing()
        {
            // Trusted=false (engine context that could not be matched to the connected model): PlanEval must ignore
            // the facts and raise the fidelity note; an all-match comparison then grades 'degraded' → unavailable.
            var spec = new DaxQuerySpec { Trusted = false, HomeTable = "Sales", TargetMeasureName = "X", ModelHasCalcGroups = false };
            var grid = new[] { "'Product'[Category]" };
            Func<string, int, Task<ResultSet>> fake = (q, mr) =>
            {
                var keyed = q.Contains("'Product'[Category]");
                return Task.FromResult(keyed
                    ? new ResultSet
                    {
                        Columns = new[] { new ColumnDef { Name = "'Product'[Category]" }, new ColumnDef { Name = "__A" }, new ColumnDef { Name = "__B" } },
                        Rows = new[] { new object[] { "Bikes", 10.0, 10.0 } },
                        RowCount = 1,
                    }
                    : new ResultSet
                    {
                        Columns = new[] { new ColumnDef { Name = "__A" }, new ColumnDef { Name = "__B" } },
                        Rows = new[] { new object[] { 10.0, 10.0 } },
                        RowCount = 1,
                    });
            };
            var eq = await DaxBench.VerifyEquivalenceCoreAsync(fake, "SUM('Sales'[Amount])", "SUMX('Sales', 'Sales'[Amount])", grid, null, 100000, spec);
            Assert.Null(eq.Error);
            Assert.NotNull(eq.Fidelity);                                 // untrusted facts → degraded evaluation, noted
            Assert.All(eq.Shapes, s => Assert.Equal(eq.Fidelity, s.Fidelity));   // fidelity preserved per shape row

            var o = EquivalenceGate.Evaluate(eq, None, None, DaxBench.NormalizeGroupBy(grid).Length);
            Assert.Equal("unavailable", o.Status);
            Assert.Contains("full-fidelity comparison", o.Missing);
            Assert.Contains(eq.Fidelity, o.Missing);                     // the fidelity gap IS what's missing
        }

        // ============================ E1 ADDENDUM — run-decided partition (openShapesFrom) ============================

        private static readonly string[] EvaluatedIds = { "grand_total", "axis:'Product'[Category]", "cross" };

        private static VerifySpec FromSpec(params string[] staticOpen) => new VerifySpec
        {
            Kind = "dax_equivalence", Probe = "witnessDax", OpenShapesFrom = "openShapes",
            OpenShapes = staticOpen,
        };
        private static Dictionary<string, AnswerValue> With(string name, AnswerValue a) =>
            new Dictionary<string, AnswerValue> { [name] = a };

        [Fact]
        public void Partition_from_a_run_input_opens_the_listed_shape_and_everything_else_stays_pinned()
        {
            var open = EquivalenceGate.ResolveOpenShapes(FromSpec(), With("openShapes", Answer("grand_total")), EvaluatedIds, out var err);
            Assert.Null(err);
            Assert.Equal(new[] { "grand_total" }, open);

            // Open grand_total mismatch records but PASSES; pinned axis mismatch FAILS — the run-decided partition
            // drives the same ledger enforcement as the static keys.
            var openOnly = Eq(2, false, Shape("grand_total", 1, ("(grand total)", "100", "90")), Shape("axis:'Product'[Category]", 2), Shape("cross", 2));
            var o1 = EquivalenceGate.Evaluate(openOnly, None, open, 2);
            Assert.Equal("passed", o1.Status);
            Assert.Contains("OPEN", o1.Detail);

            var pinnedBad = Eq(2, false, Shape("grand_total", 1, ("(grand total)", "100", "90")), Shape("axis:'Product'[Category]", 2, ("Cat=Bikes", "5", "4")), Shape("cross", 2));
            var o2 = EquivalenceGate.Evaluate(pinnedBad, None, open, 2);
            Assert.Equal("failed", o2.Status);
        }

        [Fact]
        public void Declined_partition_input_means_all_pinned()
        {
            var open = EquivalenceGate.ResolveOpenShapes(FromSpec(), With("openShapes", Decline("every evaluated shape is pinned")), EvaluatedIds, out var err);
            Assert.Null(err);
            Assert.Empty(open);                                         // empty set = all pinned (fail-closed default)

            var eq = Eq(2, false, Shape("grand_total", 1, ("(grand total)", "100", "90")), Shape("cross", 2));
            Assert.Equal("failed", EquivalenceGate.Evaluate(eq, None, open, 2).Status);   // the grand total is enforced again
        }

        [Fact]
        public void Invalid_shape_id_in_the_partition_answer_is_refused_naming_the_bad_id()
        {
            var open = EquivalenceGate.ResolveOpenShapes(FromSpec(), With("openShapes", Answer("grand_totals")), EvaluatedIds, out var err);
            Assert.Null(open);
            Assert.Contains("grand_totals", err);                       // the bad id, named
            Assert.Contains("grand_total", err);                        // and the grammar it must match
        }

        [Fact]
        public void Partition_id_not_among_the_evaluated_shapes_is_refused_fail_closed()
        {
            var open = EquivalenceGate.ResolveOpenShapes(FromSpec(), With("openShapes", Answer("axis:'Date'[Year]")), EvaluatedIds, out var err);
            Assert.Null(open);
            Assert.Contains("axis:'Date'[Year]", err);
            Assert.Contains("not among the evaluated shapes", err);
        }

        // ---- sol blocker 1: pinned evidence is classified from PER-SHAPE data, never the open cross ----

        private static ShapeComparison TruncShape(string id, int rows) => new ShapeComparison { ShapeId = id, RowsCompared = rows, Truncated = true };

        [Fact]
        public void All_non_grand_total_shapes_open_and_matching_is_never_a_plain_pass()
        {
            // Only grand_total pinned; the axes + cross (all per-context coverage) are OPEN. Even a clean run is a
            // grand-total-only pinned proof — thin — and must NOT pass off the open cross's RowsCompared.
            var eq = Eq(500, false, Shape("grand_total", 1), Shape("axis:Cat", 3), Shape("cross", 500));
            var o = EquivalenceGate.Evaluate(eq, None, new[] { "axis:Cat", "cross" }, 1);
            Assert.Equal("unavailable", o.Status);
            Assert.Contains("per-context equivalence grid", o.Missing);
            Assert.Contains("grand-total match only", o.Detail);
        }

        [Fact]
        public void All_non_grand_total_shapes_open_with_an_open_cross_mismatch_is_never_a_plain_pass()
        {
            var eq = Eq(500, false, Shape("grand_total", 1), Shape("axis:Cat", 3), Shape("cross", 500, ("Cat=Bikes", "10", "9")));
            var o = EquivalenceGate.Evaluate(eq, None, new[] { "axis:Cat", "cross" }, 1);
            Assert.Equal("unavailable", o.Status);                       // recorded observation, zero pinned coverage — not passed
            Assert.Contains("OPEN", o.Detail);
        }

        [Fact]
        public void All_shapes_open_is_unavailable_nothing_to_prove()
        {
            var eq = Eq(2, false, Shape("grand_total", 1), Shape("cross", 2));
            var o = EquivalenceGate.Evaluate(eq, None, new[] { "grand_total", "cross" }, 1);
            Assert.Equal("unavailable", o.Status);
            Assert.Contains("at least one pinned shape", o.Missing);
        }

        [Fact]
        public void Open_truncation_does_not_contaminate_complete_pinned_evidence()
        {
            // The open cross hit the row cap (top-level Truncated=true), but every PINNED shape is complete — the
            // open shape's truncation is an observation, not contamination.
            var eq = Eq(100000, true, Shape("grand_total", 1), Shape("axis:Cat", 3), TruncShape("cross", 100000));
            var o = EquivalenceGate.Evaluate(eq, None, new[] { "cross" }, 1);
            Assert.Equal("passed", o.Status);
            Assert.Contains("truncated", o.Detail);                      // ... but named in the open observation
        }

        [Fact]
        public void Pinned_truncation_blocks_naming_the_shape()
        {
            var eq = Eq(100000, true, Shape("grand_total", 1), TruncShape("cross", 100000));
            var o = EquivalenceGate.Evaluate(eq, None, None, 1);
            Assert.Equal("unavailable", o.Status);
            Assert.Contains("cross", o.Missing);
            Assert.Contains("row cap", o.Missing);
        }

        [Fact]
        public void Pass_message_counts_only_pinned_contexts_and_names_open_observations()
        {
            // 500-row open cross must not inflate the pass: pinned = grand_total(1) + axis(3) = 4 pinned contexts.
            var eq = Eq(500, false, Shape("grand_total", 1), Shape("axis:Cat", 3), Shape("cross", 500));
            var o = EquivalenceGate.Evaluate(eq, None, new[] { "cross" }, 1);
            Assert.Equal("passed", o.Status);
            Assert.Contains("4 pinned context(s)", o.Detail);
            Assert.DoesNotContain("500 pinned", o.Detail);
            Assert.Contains("cross (500 row(s)", o.Detail);              // the open shape, named as an observation
        }

        [Fact]
        public void Zero_coverage_on_any_pinned_shape_blocks_even_when_the_open_cross_has_rows()
        {
            var eq = Eq(500, false, Shape("grand_total", 1), Shape("axis:Cat", 0), Shape("cross", 500));
            var o = EquivalenceGate.Evaluate(eq, None, new[] { "cross" }, 1);
            Assert.Equal("unavailable", o.Status);
            Assert.Contains("axis:Cat", o.Missing);                      // the zero-coverage pinned shape, named
        }

        // ---- sol blocker 3: the partition lock + revision receipt (the laundering path) ----

        private const string LaunderMd = @"---
name: launder
title: Laundering guard
strictness: hard
---
## Step 1: Prove
Prove equivalence.
```yaml gate
inputs:
  - name: witnessDax
    question: ""Witness?""
    required: required
  - name: openShapes
    question: ""Open shapes?""
    required: answer-or-decline
verify:
  - kind: dax_equivalence
    probe: witnessDax
    openShapesFrom: openShapes
```
";

        // The engine executor's exact wiring, reproduced at the kernel seam (the real live path needs an endpoint):
        // resolve the partition → RegisterPartition (lock/receipt/block, keyed on the verify's step ordinal) →
        // Evaluate the supplied comparison.
        private static WorkflowVerifyExecutor GateExecutor(Func<EquivalenceResult> makeEq) => (sp, st, r, a) =>
        {
            var evaluated = new[] { "grand_total", "axis:Cat", "cross" };
            var open = EquivalenceGate.ResolveOpenShapes(sp, a, evaluated, out var err);
            if (err != null) return Task.FromResult(new VerifyResult { Kind = sp.Kind, Status = "unavailable", Missing = "a valid open-shape partition", Detail = err });
            var block = EquivalenceGate.RegisterPartition(r, st.Id, Array.IndexOf(st.Gate.Verify, sp), sp.Probe, open);
            if (block != null) return Task.FromResult(new VerifyResult
            {
                Kind = sp.Kind, Status = "unavailable",
                Missing = "shape partition changed after evidence was seen — re-run the verify with the new partition on a fresh evaluation",
                Detail = block,
            });
            var o = EquivalenceGate.Evaluate(makeEq(), sp.PinnedShapes, open, 2);
            return Task.FromResult(new VerifyResult { Kind = sp.Kind, Status = o.Status, Missing = o.Missing, Detail = o.Detail, Shapes = o.Shapes, MismatchCells = o.MismatchCells });
        };

        private static WorkflowVerifyExecutor LaunderingExecutor() => GateExecutor(
            () => Eq(2, false, Shape("grand_total", 1), Shape("axis:Cat", 2), Shape("cross", 2, ("Cat=Bikes", "10", "9"))));

        [Fact]
        public async Task Laundering_sequence_second_submission_blocked_with_receipt_third_evaluates_fresh()
        {
            var def = WorkflowParser.Parse(LaunderMd);
            Assert.Null(def.Error);
            var run = new WorkflowRunStore().Start(def, null);
            var exec = LaunderingExecutor();

            // 1) Submit with everything pinned (openShapes declined): the cross mismatch FAILS the hard gate,
            //    and the all-pinned partition is now locked — the evidence has been seen.
            await Assert.ThrowsAsync<InvalidOperationException>(() => WorkflowRunner.SubmitStepAsync(run, "step-1",
                new Dictionary<string, AnswerValue> { ["witnessDax"] = Answer("w"), ["openShapes"] = Decline("all pinned") }, exec));
            Assert.Equal("failed", run.Results[0].VerifyResults.Single().Status);
            var attempt1Detail = run.Results[0].VerifyResults.Single().Detail;   // the exact evidence string that must survive
            Assert.Empty(run.PartitionRevisions);

            // 2) THE LAUNDERING ATTEMPT: re-submit with the diverging shape flipped OPEN. Blocked (unavailable),
            //    a partition-revision receipt is appended, and the prior failed evidence is not erased from history.
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => WorkflowRunner.SubmitStepAsync(run, "step-1",
                new Dictionary<string, AnswerValue> { ["witnessDax"] = Answer("w"), ["openShapes"] = Answer("cross") }, exec));
            Assert.Contains("partition changed", ex.Message);
            Assert.Equal("unavailable", run.Results[0].VerifyResults.Single().Status);
            var receipt = Assert.Single(run.PartitionRevisions);
            Assert.Equal("", receipt.Before);
            Assert.Equal("cross", receipt.After);
            Assert.Equal("step-1", receipt.StepId);
            Assert.NotNull(receipt.TimestampUtc);

            // 3) The NEXT submission evaluates FRESH under the new locked partition: cross is open (its mismatch
            //    recorded as an observation), the pinned shapes agree → passed. The receipt stays on the view.
            await WorkflowRunner.SubmitStepAsync(run, "step-1",
                new Dictionary<string, AnswerValue> { ["witnessDax"] = Answer("w"), ["openShapes"] = Answer("cross") }, exec);
            Assert.Equal("passed", run.Results[0].Status);
            Assert.Equal("completed", run.Status);
            Assert.Contains("OPEN", run.Results[0].VerifyResults.Single().Detail);

            var view = WorkflowRunner.BuildView(run);
            var viewReceipt = Assert.Single(view.PartitionRevisions);     // get_workflow_run shows the receipt
            Assert.Equal("cross", viewReceipt.After);

            // Blocker 1 — IMMUTABLE EVIDENCE HISTORY: all THREE attempts stay visible. The original pinned-mismatch
            // evidence that motivated the partition change is never erased by the later pass.
            var history = view.Steps[0].VerifyHistory;
            Assert.Equal(3, history.Count);
            Assert.Equal(new[] { 1, 2, 3 }, history.Select(h => h.Ordinal).ToArray());
            Assert.Equal(new[] { "failed", "unavailable", "passed" }, history.Select(h => h.Results.Single().Status).ToArray());
            Assert.Equal(attempt1Detail, history[0].Results.Single().Detail);   // the mismatch evidence, byte-for-byte
            Assert.All(history, h => Assert.NotNull(h.TimestampUtc));

            // ... and in the TERMINAL run record (the experience-log certificate).
            var record = System.Text.Json.JsonSerializer.SerializeToElement(WorkflowRunner.BuildRunRecord(run));
            var recHistory = record.GetProperty("steps")[0].GetProperty("verifyHistory");
            Assert.Equal(3, recHistory.GetArrayLength());
            Assert.Equal("failed", recHistory[0].GetProperty("Results")[0].GetProperty("Status").GetString());
            Assert.Equal(attempt1Detail, recHistory[0].GetProperty("Results")[0].GetProperty("Detail").GetString());
            Assert.Equal("unavailable", recHistory[1].GetProperty("Results")[0].GetProperty("Status").GetString());
            Assert.Equal("passed", recHistory[2].GetProperty("Results")[0].GetProperty("Status").GetString());
        }

        [Fact]
        public async Task Terminal_record_keeps_the_only_archived_attempt_after_a_rejected_resubmission_cleared_current_results()
        {
            // Attempt 1 fails (archived). Attempt 2 clears the CURRENT results, then input enforcement rejects it
            // BEFORE anything is archived. Abort. The one-item history over an empty current field is now the ONLY
            // copy of the evidence — the terminal record must carry it (a count-based suppression would drop it).
            var run = new WorkflowRunStore().Start(WorkflowParser.Parse(LaunderMd), null);
            var exec = LaunderingExecutor();

            await Assert.ThrowsAsync<InvalidOperationException>(() => WorkflowRunner.SubmitStepAsync(run, "step-1",
                new Dictionary<string, AnswerValue> { ["witnessDax"] = Answer("w"), ["openShapes"] = Decline("all pinned") }, exec));
            var attempt1Detail = run.Results[0].VerifyHistory.Single().Results.Single().Detail;

            // The invalid resubmission: witnessDax (required) unanswered → rejected at the input gate, pre-archive.
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => WorkflowRunner.SubmitStepAsync(run, "step-1", new Dictionary<string, AnswerValue>(), exec));
            Assert.Contains("unanswered", ex.Message);
            Assert.Empty(run.Results[0].VerifyResults);                   // current evidence really was cleared
            Assert.Single(run.Results[0].VerifyHistory);                  // ... but the archive was not

            WorkflowRunner.Abort(run, "giving up");
            var record = System.Text.Json.JsonSerializer.SerializeToElement(WorkflowRunner.BuildRunRecord(run));
            var step0 = record.GetProperty("steps")[0];
            Assert.Equal(0, step0.GetProperty("verify").GetArrayLength());
            var recHistory = step0.GetProperty("verifyHistory");
            Assert.Equal(1, recHistory.GetArrayLength());                 // attempt 1's evidence survives the wipe
            Assert.Equal("failed", recHistory[0].GetProperty("Results")[0].GetProperty("Status").GetString());
            Assert.Equal(attempt1Detail, recHistory[0].GetProperty("Results")[0].GetProperty("Detail").GetString());
        }

        [Fact]
        public async Task Submission_ceiling_refuses_further_submissions_never_truncates_history()
        {
            var run = new WorkflowRunStore().Start(WorkflowParser.Parse(LaunderMd), null);
            var exec = LaunderingExecutor();
            var answers = new Dictionary<string, AnswerValue> { ["witnessDax"] = Answer("w"), ["openShapes"] = Decline("all pinned") };

            for (var i = 0; i < WorkflowRunner.MaxSubmissionsPerStep; i++)
                await Assert.ThrowsAsync<InvalidOperationException>(() => WorkflowRunner.SubmitStepAsync(run, "step-1", answers, exec));
            Assert.Equal(WorkflowRunner.MaxSubmissionsPerStep, run.Results[0].VerifyHistory.Count);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => WorkflowRunner.SubmitStepAsync(run, "step-1", answers, exec));
            Assert.Contains($"submitted {WorkflowRunner.MaxSubmissionsPerStep} times", ex.Message);
            Assert.Contains("skip_workflow_step", ex.Message);            // the audited ways forward, named
            Assert.Equal(WorkflowRunner.MaxSubmissionsPerStep, run.Results[0].VerifyHistory.Count);   // refused, not truncated
        }

        [Fact]
        public void Register_partition_locks_first_set_and_receipts_every_change()
        {
            var run = new WorkflowRunStore().Start(WorkflowParser.Parse(LaunderMd), null);
            Assert.Null(EquivalenceGate.RegisterPartition(run, "step-1", 0, "witnessDax", new[] { "cross" }));   // first = lock
            Assert.Null(EquivalenceGate.RegisterPartition(run, "step-1", 0, "witnessDax", new[] { "cross" }));   // unchanged = fine
            var block = EquivalenceGate.RegisterPartition(run, "step-1", 0, "witnessDax", Array.Empty<string>());
            Assert.Contains("partition changed", block);
            Assert.Single(run.PartitionRevisions);
            Assert.Null(EquivalenceGate.RegisterPartition(run, "step-1", 0, "witnessDax", Array.Empty<string>()));   // re-locked — fresh eval OK
        }

        // ---- sol blocker 2 (round 2): the lock key carries the verify's ordinal within the step ----

        private const string TwoVerifyMd = @"---
name: two-verify
title: Two same-probe verifies
strictness: hard
---
## Step 1: Prove twice
Prove equivalence under two different partitions.
```yaml gate
inputs:
  - name: witnessDax
    question: ""Witness?""
    required: required
  - name: openA
    question: ""Open shapes for the first proof?""
    required: answer-or-decline
  - name: openB
    question: ""Open shapes for the second proof?""
    required: answer-or-decline
verify:
  - kind: dax_equivalence
    probe: witnessDax
    openShapesFrom: openA
  - kind: dax_equivalence
    probe: witnessDax
    openShapesFrom: openB
```
";

        [Fact]
        public async Task Two_same_step_same_probe_verifies_with_different_partitions_do_not_share_a_lock()
        {
            // Pre-fix, the stepId|probe key made these two verifies share ONE lock: the second register ("cross")
            // would collide with the first ("grand_total"), receipt + block, and every re-submission would flip the
            // shared lock back and forth — both blocked forever. With the ordinal in the key, both evaluate cleanly.
            var def = WorkflowParser.Parse(TwoVerifyMd);
            Assert.Null(def.Error);
            var run = new WorkflowRunStore().Start(def, null);
            var exec = GateExecutor(() => Eq(2, false, Shape("grand_total", 1), Shape("axis:Cat", 2), Shape("cross", 2)));

            await WorkflowRunner.SubmitStepAsync(run, "step-1", new Dictionary<string, AnswerValue>
            {
                ["witnessDax"] = Answer("w"),
                ["openA"] = Answer("grand_total"),
                ["openB"] = Answer("cross"),
            }, exec);

            Assert.Equal("completed", run.Status);
            Assert.All(run.Results[0].VerifyResults, v => Assert.Equal("passed", v.Status));
            Assert.Equal(2, run.Results[0].VerifyResults.Length);
            Assert.Empty(run.PartitionRevisions);                         // two locks, no collision, no receipts
            Assert.Equal(2, run.PartitionLocks.Count);
        }

        // ---- sol should-fixes 4 + 5 ----

        [Fact]
        public void Parser_refuses_openShapesFrom_bound_to_a_non_text_input()
        {
            var def = WorkflowParser.Parse(@"---
name: bad-type
title: Bad type
---
## Step 1: Prove
Prove.
```yaml gate
inputs:
  - name: witnessDax
    question: ""Witness?""
    required: required
  - name: openShapes
    question: ""Open shapes?""
    type: objectRef
    required: answer-or-decline
verify:
  - kind: dax_equivalence
    probe: witnessDax
    openShapesFrom: openShapes
```
");
            Assert.NotNull(def.Error);
            Assert.Contains("must bind a 'text' input", def.Error);
            Assert.Contains("objectRef", def.Error);
        }

        [Fact]
        public void Static_open_shape_not_among_the_evaluated_set_is_refused_not_silently_ignored()
        {
            var spec = new VerifySpec { Kind = "dax_equivalence", Probe = "witnessDax", OpenShapes = new[] { "axis:'Date'[Year]" } };
            var open = EquivalenceGate.ResolveOpenShapes(spec, new Dictionary<string, AnswerValue>(), EvaluatedIds, out var err);
            Assert.Null(open);
            Assert.Contains("axis:'Date'[Year]", err);
            Assert.Contains("static openShapes", err);                    // same named refusal as a bound id
        }

        [Fact]
        public void Static_open_shapes_union_with_the_run_decided_ones()
        {
            var open = EquivalenceGate.ResolveOpenShapes(FromSpec("grand_total"), With("openShapes", Answer("axis:'Product'[Category]")), EvaluatedIds, out var err);
            Assert.Null(err);
            Assert.Equal(new[] { "grand_total", "axis:'Product'[Category]" }, open.OrderBy(x => x == "grand_total" ? 0 : 1).ToArray());
        }

        [Fact]
        public void Parser_refuses_openShapesFrom_that_names_no_input_on_the_step()
        {
            var def = WorkflowParser.Parse(@"---
name: bad-from
title: Bad from
---
## Step 1: Prove
Prove.
```yaml gate
inputs:
  - name: witnessDax
    question: ""Witness?""
    required: required
verify:
  - kind: dax_equivalence
    probe: witnessDax
    openShapesFrom: notAnInput
```
");
            Assert.NotNull(def.Error);
            Assert.Contains("notAnInput", def.Error);
            Assert.Contains("does not name an input", def.Error);
        }

        [Fact]
        public void Parser_accepts_openShapesFrom_bound_to_a_same_step_input()
        {
            var def = WorkflowParser.Parse(@"---
name: good-from
title: Good from
---
## Step 1: Prove
Prove.
```yaml gate
inputs:
  - name: witnessDax
    question: ""Witness?""
    required: required
  - name: openShapes
    question: ""Which evaluated shapes are OPEN?""
    required: answer-or-decline
verify:
  - kind: dax_equivalence
    probe: witnessDax
    openShapesFrom: openShapes
    pinnedShapes: [cross]
```
");
            Assert.Null(def.Error);
            var v = def.Steps[0].Gate.Verify.Single();
            Assert.Equal("openShapes", v.OpenShapesFrom);
            Assert.Equal(new[] { "cross" }, v.PinnedShapes);
        }

        // ==================== sol round 3: openShapesFrom is inheritance-capable and inheritance-ONLY ====================
        // The v6 seed's Step-7 performance re-proof must run under the exact partition Step 6 locked. Three legs:
        // the parser accepts a PRIOR-step binding (resolution is run-wide, like probe:); a completed step can
        // never be re-answered (RequireCurrentStep); and the runner DROPS undeclared submitted answers, so the
        // binding step has no input path to restate the partition.

        private const string InheritMd = @"---
name: inherit-partition
title: Inherited partition
strictness: hard
---
## Step 1: Declare the partition
Declare it.
```yaml gate
inputs:
  - name: witnessDax
    question: ""Witness?""
    required: required
  - name: openShapes
    question: ""Open shapes?""
    required: answer-or-decline
```

## Step 2: Prove under the inherited partition
Prove.
```yaml gate
verify:
  - kind: dax_equivalence
    probe: witnessDax
    openShapesFrom: openShapes
```
";

        [Fact]
        public void Parser_accepts_openShapesFrom_bound_to_a_prior_step_input()
        {
            var def = WorkflowParser.Parse(InheritMd);
            Assert.Null(def.Error);
            Assert.Empty(def.Steps[1].Gate.Inputs);                       // the binding step declares NO inputs
            Assert.Equal("openShapes", def.Steps[1].Gate.Verify.Single().OpenShapesFrom);
        }

        [Fact]
        public void Parser_refuses_openShapesFrom_bound_only_to_a_later_step_input()
        {
            // "prior" means prior: an input declared only on a LATER step cannot serve — at the gated step it
            // could not have been answered yet, so the binding would silently mean "all pinned" on first run.
            var def = WorkflowParser.Parse(@"---
name: later-from
title: Later from
---
## Step 1: Prove
Prove.
```yaml gate
inputs:
  - name: witnessDax
    question: ""Witness?""
    required: required
verify:
  - kind: dax_equivalence
    probe: witnessDax
    openShapesFrom: openShapes
```

## Step 2: Declare too late
Declare.
```yaml gate
inputs:
  - name: openShapes
    question: ""Open shapes?""
    required: answer-or-decline
```
");
            Assert.NotNull(def.Error);
            Assert.Contains("openShapesFrom 'openShapes' does not name an input", def.Error);
        }

        [Fact]
        public void Parser_refuses_a_non_text_prior_step_binding()
        {
            var def = WorkflowParser.Parse(@"---
name: bad-prior-type
title: Bad prior type
---
## Step 1: Declare
Declare.
```yaml gate
inputs:
  - name: openShapes
    question: ""Open shapes?""
    type: objectRef
    required: required
```

## Step 2: Prove
Prove.
```yaml gate
inputs:
  - name: witnessDax
    question: ""Witness?""
    required: required
verify:
  - kind: dax_equivalence
    probe: witnessDax
    openShapesFrom: openShapes
```
");
            Assert.NotNull(def.Error);
            Assert.Contains("must bind a 'text' input", def.Error);
            Assert.Contains("objectRef", def.Error);
        }

        // The engine executor's wiring reproduced at the kernel seam, CAPTURING the partition each verify
        // actually resolved — the assertion target for the inheritance proofs below.
        private static WorkflowVerifyExecutor CapturingExecutor(List<string[]> resolved) => (sp, st, r, a) =>
        {
            var evaluated = new[] { "grand_total", "axis:Cat", "cross" };
            var open = EquivalenceGate.ResolveOpenShapes(sp, a, evaluated, out var err);
            if (err != null) return Task.FromResult(new VerifyResult { Kind = sp.Kind, Status = "unavailable", Missing = "a valid open-shape partition", Detail = err });
            resolved.Add(open);
            var block = EquivalenceGate.RegisterPartition(r, st.Id, Array.IndexOf(st.Gate.Verify, sp), sp.Probe, open);
            if (block != null) return Task.FromResult(new VerifyResult { Kind = sp.Kind, Status = "unavailable", Missing = "a stable partition", Detail = block });
            var o = EquivalenceGate.Evaluate(Eq(2, false, Shape("grand_total", 1), Shape("axis:Cat", 2), Shape("cross", 2)), sp.PinnedShapes, open, 2);
            return Task.FromResult(new VerifyResult { Kind = sp.Kind, Status = o.Status, Missing = o.Missing, Detail = o.Detail });
        };

        [Fact]
        public async Task Later_step_verify_resolves_the_prior_steps_partition_by_inheritance()
        {
            var run = new WorkflowRunStore().Start(WorkflowParser.Parse(InheritMd), null);
            var resolved = new List<string[]>();

            await WorkflowRunner.SubmitStepAsync(run, "step-1",
                new Dictionary<string, AnswerValue> { ["witnessDax"] = Answer("w"), ["openShapes"] = Answer("cross") },
                CapturingExecutor(resolved));
            await WorkflowRunner.SubmitStepAsync(run, "step-2", new Dictionary<string, AnswerValue>(), CapturingExecutor(resolved));

            Assert.Equal("completed", run.Status);
            Assert.Equal(new[] { "cross" }, Assert.Single(resolved));     // step 2 proved under EXACTLY step 1's partition
            Assert.Equal("cross", WorkflowRunner.AllAnswers(run)["openShapes"].Value);
        }

        [Fact]
        public async Task Undeclared_answer_smuggled_into_the_binding_step_is_dropped_not_a_repartition()
        {
            // THE closed hole: pre-fix, SubmitStepAsync stored the submitted dictionary wholesale, so a stray
            // 'openShapes' answered at the binding step entered the run-wide last-answered-wins map and silently
            // re-partitioned the locked verify with no receipt. The runner now drops undeclared names: assert the
            // smuggle is IGNORED — the resolved partition, the run-wide answer, and the step record all keep the
            // prior step's value, and no partition-revision receipt fires.
            var run = new WorkflowRunStore().Start(WorkflowParser.Parse(InheritMd), null);
            var resolved = new List<string[]>();

            await WorkflowRunner.SubmitStepAsync(run, "step-1",
                new Dictionary<string, AnswerValue> { ["witnessDax"] = Answer("w"), ["openShapes"] = Answer("cross") },
                CapturingExecutor(resolved));
            await WorkflowRunner.SubmitStepAsync(run, "step-2",
                new Dictionary<string, AnswerValue> { ["openShapes"] = Answer("grand_total") },   // undeclared on step-2
                CapturingExecutor(resolved));

            Assert.Equal("completed", run.Status);
            Assert.Equal(new[] { "cross" }, Assert.Single(resolved));     // the smuggled partition never reached the verify
            Assert.Equal("cross", WorkflowRunner.AllAnswers(run)["openShapes"].Value);
            Assert.False(run.Results[1].Answers.ContainsKey("openShapes"));   // ... and never entered the run record
            Assert.Empty(run.PartitionRevisions);
        }

        [Fact]
        public async Task Completed_step_cannot_be_resubmitted_so_the_inherited_partition_is_immutable()
        {
            // The other half of inheritance-only: once step 1 passed, its openShapes answer is frozen — a
            // re-submission targeting it is refused outright (steps advance in order, never backwards).
            var run = new WorkflowRunStore().Start(WorkflowParser.Parse(InheritMd), null);
            await WorkflowRunner.SubmitStepAsync(run, "step-1",
                new Dictionary<string, AnswerValue> { ["witnessDax"] = Answer("w"), ["openShapes"] = Answer("cross") },
                CapturingExecutor(new List<string[]>()));

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => WorkflowRunner.SubmitStepAsync(run, "step-1",
                new Dictionary<string, AnswerValue> { ["witnessDax"] = Answer("w"), ["openShapes"] = Answer("grand_total") },
                CapturingExecutor(new List<string[]>())));
            Assert.Contains("not the current step", ex.Message);
            Assert.Equal("cross", WorkflowRunner.AllAnswers(run)["openShapes"].Value);
        }

        private const string WitnessRepairWindowMd = @"---
name: witness-repair-window
title: Witness repair window
strictness: hard
---
## Step 1: Complete the battery
Lock the current witness.
```yaml gate
inputs:
  - name: witnessDax
    question: ""The current witness.""
    type: text
    required: required
```
## Step 2: Run the hard equality gate
Restate the witness when unchanged, or omit it to inherit the prior answer.
```yaml gate
inputs:
  - name: witnessDax
    question: ""The current witness, which may not be declined.""
    type: text
    required: required
  - name: certificate
    question: ""The certificate.""
    type: text
    required: required
verify:
  - kind: dax_equivalence
    probe: witnessDax
```
";

        [Fact]
        public async Task Hard_gate_rejects_a_decline_then_omission_inherits_the_prior_witness_for_evaluation()
        {
            var def = WorkflowParser.Parse(WitnessRepairWindowMd);
            Assert.Null(def.Error);
            Assert.Equal("required", def.Steps[1].Gate.Inputs.Single(i => i.Name == "witnessDax").Required);

            var run = new WorkflowRunStore().Start(def, null);
            await WorkflowRunner.SubmitStepAsync(run, "step-1",
                new Dictionary<string, AnswerValue> { ["witnessDax"] = Answer("EVALUATE ROW(\"v\", 42)") }, null);

            var calls = 0;
            string resolvedWitness = null;
            WorkflowVerifyExecutor capture = (spec, step, state, all) =>
            {
                if (!all.TryGetValue(spec.Probe, out var probe) || !probe.Answered)
                    return Task.FromResult(new VerifyResult { Kind = spec.Kind, Status = "unavailable", Missing = "an answered witness", Detail = "probe unanswered" });
                calls++;
                resolvedWitness = probe.Value;
                return Task.FromResult(new VerifyResult { Kind = spec.Kind, Status = "passed", Detail = "witness evaluated" });
            };

            var declined = await Assert.ThrowsAsync<InvalidOperationException>(() => WorkflowRunner.SubmitStepAsync(run, "step-2",
                new Dictionary<string, AnswerValue>
                {
                    ["witnessDax"] = Decline("unchanged"),
                    ["certificate"] = Answer("FULL"),
                }, capture));
            Assert.Contains("witnessDax", declined.Message);
            Assert.Contains("may not be declined", declined.Message);
            Assert.Equal(0, calls);

            await WorkflowRunner.SubmitStepAsync(run, "step-2",
                new Dictionary<string, AnswerValue> { ["certificate"] = Answer("FULL") }, capture);

            Assert.Equal("completed", run.Status);
            Assert.Equal(1, calls);
            Assert.Equal("EVALUATE ROW(\"v\", 42)", resolvedWitness);
            Assert.False(run.Results[1].Answers.ContainsKey("witnessDax"));
            Assert.Equal("EVALUATE ROW(\"v\", 42)", WorkflowRunner.AllAnswers(run)["witnessDax"].Value);
        }

        // ============================ E3(a) — witness lock + revision ============================

        private const string WitnessMd = @"---
name: witness-flow
title: Witness lock
strictness: hard
---
## Step 1: Prove equivalence
Prove candidate equals the witness.
```yaml gate
inputs:
  - name: witnessDax
    question: ""The raw-row witness, verbatim.""
    type: text
    required: required
  - name: target
    question: ""The measure under proof.""
    type: objectRef
    required: required
verify:
  - kind: dax_equivalence
    probe: witnessDax
```
";

        [Fact]
        public async Task Witness_is_locked_on_first_submission_and_a_change_records_a_revision_receipt()
        {
            var ws = NewWorkspace();
            WriteUserWorkflow(ws, "witness-flow.md", WitnessMd);
            var sessions = new SessionManager();
            try
            {
                // No session/live: the equivalence verify comes back unavailable and the HARD gate throws, keeping the
                // step current and re-submittable — but the witness lock is recorded on EVERY submission regardless.
                var e = new LocalEngine(sessions, new Pro(), ws);
                var run = await e.StartWorkflowAsync("witness-flow", "human");

                await Assert.ThrowsAsync<InvalidOperationException>(() => e.SubmitWorkflowStepAsync(run.RunId, "step-1",
                    "{\"witnessDax\": \"EVALUATE ROW(\\\"v\\\", 1)\", \"target\": \"measure:Sales/Total\"}", "human"));
                var afterFirst = await e.GetWorkflowRunAsync(run.RunId);
                Assert.NotNull(afterFirst.WitnessLocks);
                Assert.Equal("witnessDax", afterFirst.WitnessLocks.Single().Probe);
                Assert.Null(afterFirst.WitnessRevisions);                 // no revision yet

                // Re-submit the SAME step with a DIFFERENT witness → a revision receipt is appended (never silently replaced).
                await Assert.ThrowsAsync<InvalidOperationException>(() => e.SubmitWorkflowStepAsync(run.RunId, "step-1",
                    "{\"witnessDax\": \"EVALUATE ROW(\\\"v\\\", 2)\", \"target\": \"measure:Sales/Total\"}", "human"));
                var afterSecond = await e.GetWorkflowRunAsync(run.RunId);
                var rev = Assert.Single(afterSecond.WitnessRevisions);
                Assert.Equal("witnessDax", rev.Probe);
                Assert.NotEqual(rev.BeforeHash, rev.AfterHash);
                Assert.Equal("step-1", rev.StepId);
            }
            finally { sessions.Dispose(); Directory.Delete(ws, true); }
        }

        // ============================ E3(b) — candidate drift ============================

        private const string DriftMd = @"---
name: drift-flow
title: Drift detection
strictness: hard
---
## Step 1: Author the candidate
Create the candidate measure.
```yaml gate
ops: [create_measure]
inputs:
  - name: target
    question: ""The created measure.""
    type: objectRef
    required: required
  - name: witnessDax
    question: ""The witness.""
    type: text
    required: required
```
## Step 2: Prove equivalence
Prove candidate equals the witness.
```yaml gate
verify:
  - kind: dax_equivalence
    probe: witnessDax
```
";

        [Fact]
        public async Task Candidate_changed_outside_the_workflow_is_detected_as_drift()
        {
            var ws = NewWorkspace();
            WriteUserWorkflow(ws, "drift-flow.md", DriftMd);
            var sessions = new SessionManager();
            try
            {
                var e = new LocalEngine(sessions, new Pro(), ws);
                await e.CreateModelAsync("Drift", 1604);
                await e.CreateTableAsync("Sales", "human");
                var run = await e.StartWorkflowAsync("drift-flow", "human");

                // Author the candidate AT the declaring step (records the workflow-authored hash for this measure).
                var mref = await e.CreateMeasureAsync("table:Sales", "Candidate", "1", "human");
                await e.SubmitWorkflowStepAsync(run.RunId, "step-1",
                    "{\"target\": \"" + mref + "\", \"witnessDax\": \"EVALUATE ROW(\\\"v\\\", 1)\"}", "human");

                // Change the measure OUTSIDE the workflow (step-2 does not declare update_measure) → drift.
                await e.SetDaxAsync(mref, "2", "human");

                var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => e.SubmitWorkflowStepAsync(run.RunId, "step-2", "{}", "human"));
                Assert.Contains("drift", ex.Message);
                var after = await e.GetWorkflowRunAsync(run.RunId);
                var v = after.Steps[1].VerifyResults.Single(x => x.Kind == "dax_equivalence");
                Assert.Equal("unavailable", v.Status);
                Assert.Contains("drift", v.Detail);
            }
            finally { sessions.Dispose(); Directory.Delete(ws, true); }
        }

        [Fact]
        public async Task No_drift_when_the_candidate_is_untouched_falls_through_to_the_offline_reason()
        {
            var ws = NewWorkspace();
            WriteUserWorkflow(ws, "drift-flow.md", DriftMd);
            var sessions = new SessionManager();
            try
            {
                var e = new LocalEngine(sessions, new Pro(), ws);
                await e.CreateModelAsync("Drift", 1604);
                await e.CreateTableAsync("Sales", "human");
                var run = await e.StartWorkflowAsync("drift-flow", "human");

                var mref = await e.CreateMeasureAsync("table:Sales", "Candidate", "1", "human");
                await e.SubmitWorkflowStepAsync(run.RunId, "step-1",
                    "{\"target\": \"" + mref + "\", \"witnessDax\": \"EVALUATE ROW(\\\"v\\\", 1)\"}", "human");

                // No outside edit → no drift. The equivalence is instead UNAVAILABLE because there is no live endpoint
                // (a local created model) — proving drift is not a false positive.
                var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => e.SubmitWorkflowStepAsync(run.RunId, "step-2", "{}", "human"));
                Assert.DoesNotContain("drift", ex.Message);
                var after = await e.GetWorkflowRunAsync(run.RunId);
                var v = after.Steps[1].VerifyResults.Single(x => x.Kind == "dax_equivalence");
                Assert.Equal("unavailable", v.Status);
                Assert.Contains("live connection", v.Detail);
            }
            finally { sessions.Dispose(); Directory.Delete(ws, true); }
        }
    }
}
