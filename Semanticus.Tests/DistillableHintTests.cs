using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.Entitlement;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// Learning Loop L3 (docs/learning-loop-plan.md §3.2): the engine offers the /distill-workflow
    /// moment as a deterministic, engine-set hint — never self-graded. Two surfaces:
    ///   • WorkflowRunView.Distillable — a run that COMPLETED with every step passed AND at least one
    ///     REAL (passed, not skipped/offline) verify. Built offline via the kernel, like WorkflowKernelTests.
    ///   • ApplyPlanReport.Distillable — a clean multi-item apply (FailedCount==0, AppliedCount>=2) that
    ///     did not regress the grade (OverallAfter >= OverallBefore). Driven through the real engine offline.
    /// The hint must never fire on a partial/skipped/offline run — a repeatable recipe needs real evidence.
    /// </summary>
    public sealed class DistillableHintTests
    {
        private sealed class Fake : IEntitlement
        {
            public bool IsPro { get; }
            public EntitlementInfo Info { get; }
            public Fake(bool pro) { IsPro = pro; Info = new EntitlementInfo { Tier = pro ? "pro" : "free" }; }
        }

        // ---- WorkflowRunView surface (kernel, offline) --------------------------------------------

        // A minimal two-step hard workflow: step-1 collects an objectRef, step-2 carries one verify.
        private const string Md = @"---
name: distill-test
title: Distill test
version: 1
strictness: hard
---

## Step 1: Capture the target

```yaml gate
inputs:
  - name: target
    question: ""The object being acted on.""
    type: objectRef
    required: required
```

## Step 2: Verify

```yaml gate
verify:
  - kind: bpa_clean
    scope: model
```
";

        // The distillable run-view hint fires only when EVERY result reads "passed" (WorkflowRunner.BuildView
        // around line 2466), and under the badge vocabulary (WorkflowRunner.ConcludeSubmittedStep) a step that
        // ran no check reads "done", never "passed". The shape that can fire is therefore one where every step
        // carries a check that passed, which is what this definition gives step-1 as well as step-2. `Md` above
        // keeps its input-only step-1 for the two negative cases below, which depend on exactly that shape.
        private const string MdAllChecked = @"---
name: distill-test-checked
title: Distill test checked
version: 1
strictness: hard
---

## Step 1: Capture the target

```yaml gate
inputs:
  - name: target
    question: ""The object being acted on.""
    type: objectRef
    required: required
verify:
  - kind: bpa_clean
    scope: model
```

## Step 2: Verify

```yaml gate
verify:
  - kind: bpa_clean
    scope: model
```
";

        private static WorkflowDef Def() { var d = WorkflowParser.Parse(Md); Assert.Null(d.Error); return d; }
        private static AnswerValue Answer(string v) => new AnswerValue { Value = v };
        private static readonly WorkflowVerifyExecutor Passing =
            (spec, step, r, a) => Task.FromResult(new VerifyResult { Kind = spec.Kind, Status = "passed", Detail = "no new violations" });
        private static readonly WorkflowVerifyExecutor OfflineSkip =
            (spec, step, r, a) => Task.FromResult(new VerifyResult { Kind = spec.Kind, Status = "skipped", Detail = "offline — not verified" });

        [Fact]
        public async Task Run_view_distillable_on_completed_run_with_all_passed_and_real_verify()
        {
            var def = WorkflowParser.Parse(MdAllChecked);
            Assert.Null(def.Error);
            var run = new WorkflowRunStore().Start(def, null);
            await WorkflowRunner.SubmitStepAsync(run, "step-1",
                new Dictionary<string, AnswerValue> { ["target"] = Answer("measure:Facts/Sales") }, Passing);
            await WorkflowRunner.SubmitStepAsync(run, "step-2", null, Passing);

            Assert.Equal("completed", run.Status);
            Assert.All(run.Results, r => Assert.Equal("passed", r.Status));   // every row really did pass a check
            var view = WorkflowRunner.BuildView(run);
            Assert.True(view.Distillable);
            Assert.False(string.IsNullOrEmpty(view.DistillableWhy));
            Assert.Contains("/distill-workflow", view.DistillableWhy);
        }

        [Fact]
        public async Task Run_view_not_distillable_when_a_step_was_skipped()
        {
            var run = new WorkflowRunStore().Start(Def(), null);
            WorkflowRunner.SkipStep(run, "step-1", "no target to hand — drafting first");   // a skip is not a pass
            await WorkflowRunner.SubmitStepAsync(run, "step-2", null, Passing);

            Assert.Equal("completed", run.Status);
            var view = WorkflowRunner.BuildView(run);
            Assert.False(view.Distillable);                 // a skipped step means the recipe wasn't followed end-to-end
            Assert.Null(view.DistillableWhy);
        }

        [Fact]
        public async Task Run_view_not_distillable_when_completed_but_verify_only_skipped_offline()
        {
            var run = new WorkflowRunStore().Start(Def(), null);
            await WorkflowRunner.SubmitStepAsync(run, "step-1",
                new Dictionary<string, AnswerValue> { ["target"] = Answer("measure:Facts/Sales") }, null);
            await WorkflowRunner.SubmitStepAsync(run, "step-2", null, OfflineSkip);   // hard gate passes on skip, but no evidence

            Assert.Equal("completed", run.Status);
            Assert.All(run.Results, r => Assert.Equal("done", r.Status));   // no passing check anywhere: proof-free rows
            var view = WorkflowRunner.BuildView(run);
            Assert.False(view.Distillable);                 // all-skipped verifies are not real evidence
            Assert.Null(view.DistillableWhy);
        }

        // ---- ApplyPlanReport surface (real engine, offline) ---------------------------------------

        private static async Task<(LocalEngine engine, string t, string m1, string m2)> OpenTwoMeasureModelAsync()
        {
            var engine = new LocalEngine(new SessionManager(), new Fake(true));
            await engine.CreateModelAsync("DistillTest", 1567);
            var t = await engine.CreateTableAsync("Facts", "human");
            var m1 = await engine.CreateMeasureAsync(t, "Sales Amount", "1", "human");   // no description ⇒ set_description improves the grade
            var m2 = await engine.CreateMeasureAsync(t, "Total Cost", "1", "human");
            return (engine, t, m1, m2);
        }

        [Fact]
        public async Task Apply_report_distillable_on_clean_multi_item_apply_that_holds_the_grade()
        {
            var (engine, _, m1, m2) = await OpenTwoMeasureModelAsync();
            using (engine)
            {
                await engine.AddPlanItemAsync(m1, "set_description", "Total sales in reporting currency.", "describe m1", null, null, "human");
                await engine.AddPlanItemAsync(m2, "set_description", "Total cost of goods sold.", "describe m2", null, null, "human");
                var rep = await engine.ApplyPlanAsync(null, "human");

                Assert.Equal(2, rep.AppliedCount);
                Assert.Equal(0, rep.FailedCount);
                Assert.True(rep.OverallAfter >= rep.OverallBefore);
                Assert.True(rep.Distillable);
                Assert.False(string.IsNullOrEmpty(rep.DistillableWhy));
                Assert.Contains("/distill-workflow", rep.DistillableWhy);
            }
        }

        [Fact]
        public async Task Apply_report_not_distillable_on_single_item_apply()
        {
            var (engine, _, m1, _) = await OpenTwoMeasureModelAsync();
            using (engine)
            {
                await engine.AddPlanItemAsync(m1, "set_description", "Total sales in reporting currency.", "describe m1", null, null, "human");
                var rep = await engine.ApplyPlanAsync(null, "human");

                Assert.Equal(1, rep.AppliedCount);
                Assert.False(rep.Distillable);              // one item is not a multi-step recipe
                Assert.Null(rep.DistillableWhy);
            }
        }
    }
}
