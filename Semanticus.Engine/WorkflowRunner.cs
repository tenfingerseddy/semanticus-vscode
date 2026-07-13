using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Semanticus.Engine
{
    // ============================================================================================
    // The workflow run state machine + gate evaluator (docs/pro-mode-spec.md §4 — the hard kernel).
    //
    // Deliberately PURE over the run state: verify execution is an injected delegate so the kernel
    // is testable offline and the LocalEngine wires the real executors (DaxBench probe, BPA/
    // readiness rescan) on top. No locking here — the engine's _workflowGate serializes every run
    // mutation across both doors (same single-owner discipline as _planGate). The REJECTION TEXT is
    // the steering mechanism: errors name each unanswered question verbatim so the agent knows
    // exactly what to go ask the user.
    // ============================================================================================

    /// <summary>Executes one verify spec against the live model. Contract: offline / not-wired MUST
    /// come back as status "skipped" with the reason — never silently passed (the change-plan
    /// honesty rule).</summary>
    public delegate Task<VerifyResult> WorkflowVerifyExecutor(
        VerifySpec spec, WorkflowStep step, WorkflowRunState run, IReadOnlyDictionary<string, AnswerValue> answers);

    public static class WorkflowRunner
    {
        /// <summary>Effective gate strictness. Resolution: GLOBAL enforcement override ?? per-gate
        /// override ?? per-workflow settings override ?? frontmatter default ?? hard. The global mode
        /// (set_workflow_enforcement) sits at the very TOP on purpose: per-gate hardness exists to stop
        /// an agent sidestepping a step, but the toggle is the accountable owner opting out of (or
        /// forcing) enforcement wholesale — a kill-switch below anything would be dead weight against
        /// the stock seeds' per-gate 'hard'. (The per-workflow settings file sits above the frontmatter
        /// so a user relaxes a stock workflow without editing the shipped file.)</summary>
        public static string EffectiveStrictness(WorkflowDef def, GateSpec gate, string settingsStrictness, string globalStrictness = null) =>
            (string.IsNullOrWhiteSpace(globalStrictness) ? null : globalStrictness)
            ?? gate?.Strictness ?? (string.IsNullOrWhiteSpace(settingsStrictness) ? null : settingsStrictness) ?? def.Strictness ?? "hard";

        // ---- transitions (all called under _workflowGate) ------------------------------------

        /// <summary>Submit the current step: input gate → verify gate → strictness → advance.
        /// Throws with instructive text on rejection (missing inputs / hard verify failure); the
        /// step stays current and can be re-submitted with the gaps filled.</summary>
        public static async Task SubmitStepAsync(
            WorkflowRunState run, string stepId, Dictionary<string, AnswerValue> answers, WorkflowVerifyExecutor executor)
        {
            var step = RequireCurrentStep(run, stepId);
            var result = run.Results[run.StepIndex];
            answers ??= new Dictionary<string, AnswerValue>();

            // A re-submit after a failure starts fresh — stale answers/evidence never linger.
            result.Status = "in_progress";
            result.Answers = new Dictionary<string, AnswerValue>(answers);
            result.VerifyResults = Array.Empty<VerifyResult>();
            result.Note = null;

            var strictness = EffectiveStrictness(run.Def, step.Gate, run.SettingsStrictness, run.GlobalStrictness);
            result.EffectiveStrictness = step.Gate == null ? null : strictness;

            // The answer namespace is RUN-WIDE: a verify at step 3 may gate on / probe an input
            // recorded at step 1 (the §2 example does exactly this). Later answers win on a name clash.
            var all = AccumulatedAnswers(run);

            if (step.Gate != null && strictness != "off")
            {
                EnforceInputs(step, all);

                var outcomes = new List<VerifyResult>();
                foreach (var spec in step.Gate.Verify)
                {
                    if (!WhenHolds(spec.When, all))
                    {
                        outcomes.Add(new VerifyResult { Kind = spec.Kind, Status = "skipped", Detail = $"when '{spec.When}' did not hold (input declined or absent)." });
                        continue;
                    }
                    if (executor == null)
                    {
                        outcomes.Add(new VerifyResult { Kind = spec.Kind, Status = "skipped", Detail = "no verify executor wired." });
                        continue;
                    }
                    try { outcomes.Add(await executor(spec, step, run, all) ?? new VerifyResult { Kind = spec.Kind, Status = "skipped", Detail = "executor returned nothing." }); }
                    catch (Exception ex) { outcomes.Add(new VerifyResult { Kind = spec.Kind, Status = "failed", Detail = ex.Message }); }
                }
                result.VerifyResults = outcomes.ToArray();

                var failures = outcomes.Where(o => o.Status == "failed").ToArray();
                if (failures.Length > 0)
                {
                    if (strictness == "hard")
                    {
                        result.Status = "failed";
                        result.Note = "hard gate failed: " + string.Join(" | ", failures.Select(f => $"{f.Kind}: {f.Detail}"));
                        throw new InvalidOperationException(
                            $"Step '{step.Id}' failed its hard gate. " +
                            string.Join(" ", failures.Select(f => $"[{f.Kind}] {f.Detail}")) +
                            " Fix the issue and re-submit this step (or skip_workflow_step with a reason — the skip is recorded).");
                    }
                    result.Note = "warn gate: " + string.Join(" | ", failures.Select(f => $"{f.Kind}: {f.Detail}"));
                }
            }
            else if (step.Gate != null)
            {
                result.Note = "gate skipped (strictness off).";
            }

            result.Status = "passed";
            Advance(run);
        }

        /// <summary>Skip the current step. A reason is REQUIRED — the accountable-override shape:
        /// never a hard wall, never a silent bypass.</summary>
        public static void SkipStep(WorkflowRunState run, string stepId, string reason)
        {
            var step = RequireCurrentStep(run, stepId);
            if (string.IsNullOrWhiteSpace(reason))
                throw new InvalidOperationException($"Skipping step '{step.Id}' requires a reason — it is recorded on the run, not a formality.");
            var result = run.Results[run.StepIndex];
            result.Status = "skipped";
            result.Note = "skipped: " + reason.Trim();
            Advance(run);
        }

        public static void Abort(WorkflowRunState run, string reason)
        {
            if (run.Status != "active") throw new InvalidOperationException($"Run '{run.RunId}' is already {run.Status}.");
            run.Status = "aborted";
            run.AbortReason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
            run.FinishedUtc = DateTime.UtcNow.ToString("o");
            var cur = run.StepIndex < run.Results.Length ? run.Results[run.StepIndex] : null;
            if (cur != null && cur.Status == "in_progress") { cur.Status = "pending"; }
        }

        // ---- gate internals ------------------------------------------------------------------

        /// <summary>All answers recorded so far, steps in order, the current (in-progress) step last —
        /// so a step's fresh submission overrides an earlier same-name answer.</summary>
        private static Dictionary<string, AnswerValue> AccumulatedAnswers(WorkflowRunState run)
        {
            var all = new Dictionary<string, AnswerValue>(StringComparer.Ordinal);
            for (int i = 0; i <= run.StepIndex && i < run.Results.Length; i++)
                foreach (var kv in run.Results[i].Answers) all[kv.Key] = kv.Value;
            return all;
        }

        /// <summary>The input gate. Every non-optional input must be answered or explicitly declined
        /// ({"declined": true, "reason": ...}); `required: required` may not be declined. The
        /// rejection lists EVERY gap with its question verbatim (mirror the cicd_publish refusal tone).</summary>
        private static void EnforceInputs(WorkflowStep step, Dictionary<string, AnswerValue> answers)
        {
            var problems = new List<string>();
            foreach (var input in step.Gate.Inputs)
            {
                answers.TryGetValue(input.Name, out var a);
                var missing = a == null || (!a.Declined && string.IsNullOrWhiteSpace(a.Value));
                if (input.Required == "optional") continue;
                if (missing)
                    problems.Add($"'{input.Name}' is unanswered — ask the user: \"{input.Question}\" " +
                                 (input.Required == "required"
                                     ? "(required: it cannot be declined)"
                                     : "(answer it, or decline explicitly with {\"declined\": true, \"reason\": \"...\"})"));
                else if (a.Declined && input.Required == "required")
                    problems.Add($"'{input.Name}' may not be declined (required) — ask the user: \"{input.Question}\"");
                else if (a.Declined && string.IsNullOrWhiteSpace(a.DeclineReason))
                    problems.Add($"'{input.Name}' was declined without a reason — a decline is recorded and needs one.");
            }
            if (problems.Count > 0)
                throw new InvalidOperationException(
                    $"Step '{step.Id}' rejected: {problems.Count} gate input(s) unanswered. " + string.Join(" ", problems));
        }

        private static bool WhenHolds(string when, Dictionary<string, AnswerValue> answers)
        {
            if (string.IsNullOrWhiteSpace(when)) return true;
            // parser guarantees the form inputs.<name>.answered
            var name = when.Substring("inputs.".Length, when.Length - "inputs.".Length - ".answered".Length);
            return answers.TryGetValue(name, out var a) && a != null && a.Answered;
        }

        private static WorkflowStep RequireCurrentStep(WorkflowRunState run, string stepId)
        {
            if (run.Status != "active")
                throw new InvalidOperationException($"Run '{run.RunId}' is {run.Status} — no step accepts submissions.");
            var step = run.CurrentStep;
            if (!string.IsNullOrWhiteSpace(stepId) && !string.Equals(stepId, step.Id, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"Step '{stepId}' is not the current step — the run is on '{step.Id}' ({step.Title}). Steps advance in order.");
            return step;
        }

        private static void Advance(WorkflowRunState run)
        {
            run.StepIndex++;
            if (run.StepIndex >= run.Def.Steps.Length)
            {
                run.Status = "completed";
                run.FinishedUtc = DateTime.UtcNow.ToString("o");
            }
            else run.Results[run.StepIndex].Status = "in_progress";
        }

        // ---- views ------------------------------------------------------------------------------

        /// <summary>A cloned point-in-time snapshot (Plan.cs BuildView discipline) — a later in-place
        /// mutation can't tear a serialized view. Carries the current step's FULL instruction text
        /// verbatim: the agent is taught at the point of need, never a summary.</summary>
        public static WorkflowRunView BuildView(WorkflowRunState run)
        {
            var view = new WorkflowRunView
            {
                RunId = run.RunId,
                Workflow = run.Def.Name,
                Title = run.Def.Title,
                WorkflowVersion = run.Def.Version,
                Status = run.Status,
                AbortReason = run.AbortReason,
                StartedUtc = run.StartedUtc,
                FinishedUtc = run.FinishedUtc,
                ModelName = run.ModelName,
                ModelFingerprint = run.ModelFingerprint,
                StepIndex = run.StepIndex,
                TotalSteps = run.Def.Steps.Length,
                Steps = run.Results.Select(r => r.Clone()).ToArray(),
            };
            // Learning Loop L3 distillable hint: only a completed run whose every step PASSED (no skip,
            // no fail) AND that produced at least one PASSED verify (real evidence, not an all-skipped/
            // offline run) is a repeatable recipe worth /distill-workflow.
            var allPassed = run.Status == "completed" && run.Results.All(r => r.Status == "passed");
            var realVerify = run.Results.SelectMany(r => r.VerifyResults).Any(v => v.Status == "passed");
            view.Distillable = allPassed && realVerify;
            view.DistillableWhy = view.Distillable
                ? $"'{run.Def.Name}' completed with all {run.Results.Length} steps passed and verified evidence — a repeatable recipe; run /distill-workflow to capture it."
                : null;
            var cur = run.CurrentStep;
            if (cur != null)
            {
                view.CurrentStep = new CurrentStepView
                {
                    StepId = cur.Id,
                    Title = cur.Title,
                    Instructions = cur.Instructions,
                    Questions = cur.Gate?.Inputs.ToArray() ?? Array.Empty<GateInput>(),
                    VerifyKinds = cur.Gate?.Verify.Select(v => v.Kind).ToArray() ?? Array.Empty<string>(),
                    EffectiveStrictness = cur.Gate == null ? null : EffectiveStrictness(run.Def, cur.Gate, run.SettingsStrictness, run.GlobalStrictness),
                    Ops = cur.Ops,
                };
            }
            return view;
        }

        public static WorkflowInfo BuildInfo(WorkflowDef def, string settingsStrictness = null, string globalStrictness = null,
            bool enabled = true, bool active = true, string activeReason = null) => new WorkflowInfo
        {
            Name = def.Name,
            Title = def.Title,
            Description = def.Description,
            WhenToUse = def.WhenToUse,
            Version = def.Version,
            Source = def.Source,
            StepCount = def.Steps.Length,
            Enabled = enabled,
            Active = active,
            ActiveReason = activeReason,
            Gated = def.Error == null && def.HasEnforcedGate(settingsStrictness, globalStrictness),
            Triggers = def.Triggers,
            Error = def.Error,
        };

        /// <summary>The terminal-run record for the experience log (learning-loop §3.1: the audit of
        /// a run must not die with the session). Answers/declines/evidence/outcomes, compact.</summary>
        public static object BuildRunRecord(WorkflowRunState run) => new
        {
            runId = run.RunId,
            workflow = run.Def.Name,
            version = run.Def.Version,
            status = run.Status,
            abortReason = run.AbortReason,
            startedUtc = run.StartedUtc,
            finishedUtc = run.FinishedUtc,
            modelName = run.ModelName,
            modelFingerprint = run.ModelFingerprint,
            steps = run.Results.Select(r => new
            {
                r.StepId,
                r.Status,
                r.Note,
                r.EffectiveStrictness,
                answers = r.Answers.Select(kv => new
                {
                    name = kv.Key,
                    kv.Value.Value,
                    declined = kv.Value.Declined ? (bool?)true : null,
                    reason = kv.Value.DeclineReason,
                }).ToArray(),
                verify = r.VerifyResults,
            }).ToArray(),
        };
    }
}
