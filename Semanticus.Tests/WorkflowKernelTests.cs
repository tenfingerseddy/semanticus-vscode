using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// The workflow-engine KERNEL (docs/pro-mode-spec.md §2/§4) — parser, state machine, gate
    /// evaluator — proven pure/offline before any plumbing builds on it. The dual-drive ops, real
    /// verify executors, seed library, and smoke coverage land in follow-up slices with their own
    /// tests; what's pinned here is the contract those slices assume: rejection text names every
    /// unanswered question verbatim (the steering mechanism), declines are recorded never dropped,
    /// offline verify comes back skipped never passed, and views are torn-proof clones.
    /// </summary>
    public sealed class WorkflowKernelTests
    {
        // The normative §2 example, condensed but structurally faithful (comments, quotes, gates).
        private const string NewMeasureMd = @"---
name: new-measure                      # kebab-case id
title: Author a verified measure
description: Create a measure with declared intent.
version: 2
strictness: warn                       # hard | warn | off
triggers: [create_measure, update_measure]   # advisory
---

Preamble before the first step is not part of any step.

## Step 1: Capture intent

Ask the user for the business definition.

```yaml gate
inputs:
  - name: verificationValue
    question: ""A known-good number to verify against — from the user, not derived.""
    type: verification
    required: answer-or-decline
  - name: expectedGrain
    question: ""At what grain is this measure meaningful?""
    type: text
    required: answer-or-decline
```

## Step 2: Author the DAX

Use `get_grounding` for naming/format/sibling context.

## Step 3: Create and verify

Create via `create_measure`.

```yaml gate
strictness: hard                 # per-gate override
verify:
  - kind: dax_probe
    when: inputs.verificationValue.answered
    probe: verificationValue
  - kind: bpa_clean
    scope: object
```
";

        private static WorkflowDef Def() { var d = WorkflowParser.Parse(NewMeasureMd); Assert.Null(d.Error); return d; }
        private static AnswerValue Answer(string v) => new AnswerValue { Value = v };
        private static AnswerValue Decline(string why) => new AnswerValue { Declined = true, DeclineReason = why };

        // ---- parser ---------------------------------------------------------------------------

        [Fact]
        public void Parser_round_trips_frontmatter_steps_and_gates()
        {
            var d = Def();
            Assert.Equal("new-measure", d.Name);
            Assert.Equal("Author a verified measure", d.Title);
            Assert.Equal(2, d.Version);
            Assert.Equal("warn", d.Strictness);                       // inline comment stripped
            Assert.Equal(new[] { "create_measure", "update_measure" }, d.Triggers);
            Assert.Equal(3, d.Steps.Length);

            var s1 = d.Steps[0];
            Assert.Equal("step-1", s1.Id);
            Assert.Equal("Capture intent", s1.Title);
            Assert.Contains("business definition", s1.Instructions);
            Assert.DoesNotContain("yaml gate", s1.Instructions);      // the gate block is NOT instruction text
            Assert.Equal(2, s1.Gate.Inputs.Length);
            Assert.Equal("verificationValue", s1.Gate.Inputs[0].Name);
            Assert.Equal("A known-good number to verify against — from the user, not derived.", s1.Gate.Inputs[0].Question);
            Assert.Equal("verification", s1.Gate.Inputs[0].Type);

            Assert.Null(d.Steps[1].Gate);                             // a gateless step parses clean

            var s3 = d.Steps[2];
            Assert.Equal("hard", s3.Gate.Strictness);
            Assert.Equal(2, s3.Gate.Verify.Length);
            Assert.Equal("dax_probe", s3.Gate.Verify[0].Kind);
            Assert.Equal("inputs.verificationValue.answered", s3.Gate.Verify[0].When);
            Assert.Equal("verificationValue", s3.Gate.Verify[0].Probe);
            Assert.Equal("object", s3.Gate.Verify[1].Scope);
        }

        [Fact]
        public void Parser_is_line_ending_agnostic()
        {
            // a git autocrlf checkout hands the parser CRLF files — same result either way
            var crlf = WorkflowParser.Parse(NewMeasureMd.Replace("\r\n", "\n").Replace("\n", "\r\n"));
            Assert.Null(crlf.Error);
            Assert.Equal(3, crlf.Steps.Length);
            Assert.Equal("hard", crlf.Steps[2].Gate.Strictness);
        }

        [Theory]
        [InlineData("no frontmatter at all", "frontmatter")]
        [InlineData("---\nname: x\n---\nno steps here", "Step")]
        [InlineData("---\nname: x\nstrictness: brutal\n---\n## Step 1: A", "strictness 'brutal'")]
        [InlineData("---\nname: x\n---\n## Step 1: A\n## Step 3: C", "numbering")]
        [InlineData("---\nname: x\n---\n## Step 1: A\n```yaml gate\nverify:\n  - kind: vibes_check\n```", "verify kind")]
        [InlineData("---\nname: x\n---\n## Step 1: A\n```yaml gate\nverify:\n  - kind: dax_probe\n    when: user.is.happy\n```", "inputs.<name>.answered")]
        [InlineData("---\nname: x\n---\n## Step 1: A\n```yaml gate\ninputs:\n  - name: a\n```\n```yaml gate\ninputs:\n  - name: b\n```", "more than one")]
        public void Parser_surfaces_malformed_files_instead_of_skipping(string text, string expectInError)
        {
            var d = WorkflowParser.Parse(text);
            Assert.NotNull(d.Error);
            Assert.Contains(expectInError, d.Error);
        }

        [Fact]
        public void Parser_preserves_and_ignores_unknown_keys_for_forward_compat()
        {
            var d = WorkflowParser.Parse("---\nname: x\nfutureKey: whatever\n---\n## Step 1: A\n```yaml gate\nfutureSection:\n  - thing: 1\ninputs:\n  - name: a\n    futureAttr: z\n```\nBody.");
            Assert.Null(d.Error);
            Assert.Equal("a", d.Steps[0].Gate.Inputs.Single().Name);
        }

        [Fact]
        public void LoadDirectory_hot_reloads_and_enforces_filename_match()
        {
            var dir = Path.Combine(Path.GetTempPath(), "smx-wf-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
            try
            {
                File.WriteAllText(Path.Combine(dir, "new-measure.md"), NewMeasureMd);
                var defs = WorkflowParser.LoadDirectory(dir, "user");
                Assert.Null(defs.Single().Error);

                // definitions are re-read from disk at call time — an edit is seen on the NEXT load
                File.WriteAllText(Path.Combine(dir, "new-measure.md"), NewMeasureMd.Replace("version: 2", "version: 3"));
                Assert.Equal(3, WorkflowParser.LoadDirectory(dir, "user").Single().Version);

                // name/filename mismatch is surfaced, not skipped
                File.WriteAllText(Path.Combine(dir, "wrong-name.md"), NewMeasureMd);
                var bad = WorkflowParser.LoadDirectory(dir, "user").Single(x => x.FilePath.EndsWith("wrong-name.md"));
                Assert.Contains("must match the filename", bad.Error);
            }
            finally { Directory.Delete(dir, true); }
        }

        // ---- input gate -----------------------------------------------------------------------

        [Fact]
        public async Task Rejection_names_every_unanswered_question_verbatim()
        {
            var run = new WorkflowRunStore().Start(Def(), null);
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => WorkflowRunner.SubmitStepAsync(run, "step-1", new Dictionary<string, AnswerValue>(), null));
            Assert.Contains("2 gate input(s) unanswered", ex.Message);
            Assert.Contains("A known-good number to verify against — from the user, not derived.", ex.Message);
            Assert.Contains("At what grain is this measure meaningful?", ex.Message);
            Assert.Equal(0, run.StepIndex);                            // the run stays on the step
            Assert.Equal("active", run.Status);
        }

        [Fact]
        public async Task Decline_is_recorded_and_auditable_never_silently_dropped()
        {
            var run = new WorkflowRunStore().Start(Def(), null);

            // a decline WITHOUT a reason is rejected — a decline is a recorded act, not a shrug
            var bare = new Dictionary<string, AnswerValue> { ["verificationValue"] = Decline(""), ["expectedGrain"] = Answer("day") };
            await Assert.ThrowsAsync<InvalidOperationException>(() => WorkflowRunner.SubmitStepAsync(run, "step-1", bare, null));

            var ok = new Dictionary<string, AnswerValue> { ["verificationValue"] = Decline("user has no known-good figure yet"), ["expectedGrain"] = Answer("day") };
            await WorkflowRunner.SubmitStepAsync(run, "step-1", ok, null);
            var rec = run.Results[0];
            Assert.Equal("passed", rec.Status);
            Assert.True(rec.Answers["verificationValue"].Declined);
            Assert.Equal("user has no known-good figure yet", rec.Answers["verificationValue"].DeclineReason);
            Assert.Equal(1, run.StepIndex);
        }

        // ---- verify gate + strictness tiers -----------------------------------------------------

        private static async Task<WorkflowRunState> RunToStep3(WorkflowDef def, string settings = null)
        {
            var run = new WorkflowRunStore().Start(def, settings);
            await WorkflowRunner.SubmitStepAsync(run, "step-1", new Dictionary<string, AnswerValue>
            { ["verificationValue"] = Answer("42"), ["expectedGrain"] = Answer("day") }, null);
            await WorkflowRunner.SubmitStepAsync(run, "step-2", null, null);
            return run;
        }

        [Fact]
        public async Task Hard_gate_blocks_on_failed_verify_and_the_step_is_resubmittable()
        {
            var run = await RunToStep3(Def());
            WorkflowVerifyExecutor failing = (spec, step, r, a) => Task.FromResult(
                spec.Kind == "dax_probe"
                    ? new VerifyResult { Kind = spec.Kind, Status = "failed", Detail = "probe expected 42, got 41" }
                    : new VerifyResult { Kind = spec.Kind, Status = "passed", Detail = "no new violations" });

            // step-3 submits NO answers: the answer namespace is run-wide, so the probe's when-clause
            // holds via step-1's recorded verificationValue and the executor can read it there.
            IReadOnlyDictionary<string, AnswerValue> seen = null;
            WorkflowVerifyExecutor failingCapture = (spec, step, r, a) => { seen = a; return failing(spec, step, r, a); };
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => WorkflowRunner.SubmitStepAsync(run, "step-3", null, failingCapture));
            Assert.Contains("hard gate", ex.Message);
            Assert.Contains("probe expected 42, got 41", ex.Message);
            Assert.Equal("42", seen["verificationValue"].Value);       // step-1's answer reached the step-3 executor
            Assert.Equal("failed", run.Results[2].Status);
            Assert.Equal(2, run.StepIndex);                            // still the current step

            WorkflowVerifyExecutor passing = (spec, step, r, a) => Task.FromResult(new VerifyResult { Kind = spec.Kind, Status = "passed", Detail = "ok" });
            await WorkflowRunner.SubmitStepAsync(run, "step-3", null, passing);
            Assert.Equal("passed", run.Results[2].Status);
            Assert.Equal("completed", run.Status);
        }

        [Fact]
        public async Task Warn_tier_records_failures_but_passes_and_off_skips_the_gate_entirely()
        {
            // step-3's per-gate override is hard; relax it via the def to exercise warn on step-1's tier
            var warnDef = WorkflowParser.Parse(NewMeasureMd.Replace("strictness: hard                 # per-gate override", "futureKey: relaxed-for-test"));
            Assert.Null(warnDef.Error);
            var run = await RunToStep3(warnDef);                       // frontmatter default: warn
            WorkflowVerifyExecutor failing = (spec, step, r, a) => Task.FromResult(new VerifyResult { Kind = spec.Kind, Status = "failed", Detail = "boom" });
            await WorkflowRunner.SubmitStepAsync(run, "step-3", null, failing);
            Assert.Equal("passed", run.Results[2].Status);             // warn: recorded, not blocking
            Assert.Contains("warn gate", run.Results[2].Note);
            Assert.Contains("boom", run.Results[2].Note);

            // settings override "off": the gate is skipped AND recorded as such — and inputs are not demanded
            var offRun = new WorkflowRunStore().Start(warnDef, "off");
            await WorkflowRunner.SubmitStepAsync(offRun, "step-1", null, failing);
            Assert.Equal("passed", offRun.Results[0].Status);
            Assert.Contains("strictness off", offRun.Results[0].Note);
            Assert.Empty(offRun.Results[0].VerifyResults);
        }

        [Fact]
        public async Task Offline_verify_comes_back_skipped_never_silently_passed()
        {
            var run = await RunToStep3(Def());
            WorkflowVerifyExecutor offline = (spec, step, r, a) => Task.FromResult(new VerifyResult { Kind = spec.Kind, Status = "skipped", Detail = "offline — no live connection" });
            await WorkflowRunner.SubmitStepAsync(run, "step-3", null, offline);
            Assert.All(run.Results[2].VerifyResults, v => Assert.Equal("skipped", v.Status));
            Assert.Contains("offline", run.Results[2].VerifyResults[0].Detail);
        }

        [Fact]
        public async Task When_clause_skips_the_probe_when_its_input_was_declined()
        {
            var run = new WorkflowRunStore().Start(Def(), null);
            await WorkflowRunner.SubmitStepAsync(run, "step-1", new Dictionary<string, AnswerValue>
            { ["verificationValue"] = Decline("no reference figure"), ["expectedGrain"] = Answer("day") }, null);
            await WorkflowRunner.SubmitStepAsync(run, "step-2", null, null);

            var executed = new List<string>();
            WorkflowVerifyExecutor exec = (spec, step, r, a) => { executed.Add(spec.Kind); return Task.FromResult(new VerifyResult { Kind = spec.Kind, Status = "passed", Detail = "ok" }); };
            await WorkflowRunner.SubmitStepAsync(run, "step-3", null, exec);

            Assert.Equal(new[] { "bpa_clean" }, executed);             // dax_probe's when: did not hold
            var probe = run.Results[2].VerifyResults.Single(v => v.Kind == "dax_probe");
            Assert.Equal("not_applicable", probe.Status);              // E2: a conditional that did not hold is not_applicable (advances)
        }

        // ---- state machine + accountability ------------------------------------------------------

        [Fact]
        public async Task Steps_advance_in_order_and_skip_requires_a_reason()
        {
            var run = new WorkflowRunStore().Start(Def(), null);
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => WorkflowRunner.SubmitStepAsync(run, "step-2", null, null));
            Assert.Contains("not the current step", ex.Message);

            Assert.Throws<InvalidOperationException>(() => WorkflowRunner.SkipStep(run, "step-1", "  "));
            WorkflowRunner.SkipStep(run, "step-1", "user wants to draft DAX first, will backfill intent");
            Assert.Equal("skipped", run.Results[0].Status);
            Assert.Contains("backfill intent", run.Results[0].Note);
            Assert.Equal(1, run.StepIndex);

            WorkflowRunner.Abort(run, "changed course");
            Assert.Equal("aborted", run.Status);
            await Assert.ThrowsAsync<InvalidOperationException>(() => WorkflowRunner.SubmitStepAsync(run, null, null, null));
        }

        [Fact]
        public void Views_are_clones_and_carry_the_full_instruction_text()
        {
            var run = new WorkflowRunStore().Start(Def(), null);
            var view = WorkflowRunner.BuildView(run);
            Assert.Equal("Ask the user for the business definition.", view.CurrentStep.Instructions);
            Assert.Equal(2, view.CurrentStep.Questions.Length);

            // mutate the live state AFTER the view was built — the snapshot must not tear
            run.Results[0].Status = "passed";
            run.Results[0].Answers["x"] = new AnswerValue { Value = "later" };
            Assert.Equal("in_progress", view.Steps[0].Status);
            Assert.Empty(view.Steps[0].Answers);
        }

        [Fact]
        public void Enforcement_is_what_is_gated_not_reading_the_playbook()
        {
            var d = Def();
            Assert.True(d.HasEnforcedGate());
            // step-3 carries a per-gate `hard` override, and per-gate beats the settings override
            // ("this one never relaxes") — so a settings-wide off does NOT free this workflow
            Assert.True(d.HasEnforcedGate("off"));

            // without per-gate overrides, settings-off relaxes every gate → runs free
            var relaxable = WorkflowParser.Parse(NewMeasureMd.Replace("strictness: hard                 # per-gate override", "futureKey: relaxed-for-test"));
            Assert.Null(relaxable.Error);
            Assert.True(relaxable.HasEnforcedGate());
            Assert.False(relaxable.HasEnforcedGate("off"));
            Assert.True(relaxable.HasEnforcedGate("warn"));

            var gateless = WorkflowParser.Parse("---\nname: x\n---\n## Step 1: A\nJust advice.");
            Assert.Null(gateless.Error);
            Assert.False(gateless.HasEnforcedGate());
        }

        [Fact]
        public void Run_store_keeps_live_runs_and_evicts_terminal_ones_first()
        {
            var store = new WorkflowRunStore();
            var live = store.Start(Def(), null);
            for (int i = 0; i < 9; i++)
            {
                var r = store.Start(Def(), null);
                WorkflowRunner.Abort(r, "noise");
            }
            Assert.Same(live, store.Get(live.RunId));                  // the active run survived the churn
            Assert.NotNull(store.Get(null));                           // null id = latest
            Assert.Throws<InvalidOperationException>(() => store.Require("wfr-nope"));

            // the cap refuses new starts rather than silently dropping an ACTIVE run
            var full = new WorkflowRunStore();
            for (int i = 0; i < 8; i++) full.Start(Def(), null);
            Assert.Contains("already active", Assert.Throws<InvalidOperationException>(() => full.Start(Def(), null)).Message);
        }

        [Fact]
        public void Terminal_run_record_carries_answers_declines_and_evidence()
        {
            var run = new WorkflowRunStore().Start(Def(), null);
            WorkflowRunner.SkipStep(run, "step-1", "capturing later");
            WorkflowRunner.Abort(run, "demo");
            var json = System.Text.Json.JsonSerializer.Serialize(WorkflowRunner.BuildRunRecord(run));
            Assert.Contains("\"workflow\":\"new-measure\"", json);
            Assert.Contains("capturing later", json);
            Assert.Contains("\"status\":\"aborted\"", json);
        }
    }
}
