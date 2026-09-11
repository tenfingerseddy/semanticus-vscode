using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
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
        public const string OverriddenCertificateConsequence = "This run's certificate becomes OVERRIDDEN.";

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
        /// <summary>Ceiling on ARCHIVED submissions per step (polish e): further submissions are REFUSED — history
        /// is an audit surface and is never truncated. Far above any honest retry loop; only a runaway agent hits it.</summary>
        public const int MaxSubmissionsPerStep = 50;

        public static async Task SubmitStepAsync(
            WorkflowRunState run, string stepId, Dictionary<string, AnswerValue> answers, WorkflowVerifyExecutor executor,
            IReadOnlyCollection<string> modelMeasureNames = null,
            IReadOnlyCollection<(string Table, string Name, bool IsCalculated)> modelColumns = null,
            IReadOnlyCollection<string> modelFunctionNames = null,
            IReadOnlyCollection<(string Name, bool IsCalculated)> modelTables = null)
        {
            var current = ValidateSubmissionCore(run, stepId, answers);
            // THREE unexpanded-template shapes take a setup submission, and each is expansion-only.
            // An unexpanded input-backed template whose list source is its OWN input takes exactly one
            // PREPARATION submission (§1.6.1a): it answers that source and splices the plan.
            // An unexpanded inline InLiteral template takes exactly one expansion-only submission
            // (§1.6.1b): it answers nothing and splices the authored list.
            // An unexpanded template whose source was declared on an EARLIER row and that reaches the run
            // still unexpanded takes exactly one expansion-only submission (§1.6.1d): it answers nothing
            // and resolves the list from its own loop-entry projection. None of the three is the authored
            // loop body, so none runs verify, completes its submitted top-level frame, or executes #0.
            // The contrast that stays: a deferred source declared on this ordinary row for the immediately
            // following loop is NOT expansion-only. That call runs the DECLARING step's gate and verify,
            // and the recorded decision splices in the tail (§1.6.1c). The declaring row and the loop row
            // are different rows and only the second of them is a setup call.
            if (IsForEachPreparation(current.Planned))
            {
                PrepareCurrentForEach(run, current, answers);
                return;
            }
            if (IsInlineForEachExpansion(current.Planned))
            {
                ExpandCurrentInlineForEach(run, current, answers);
                return;
            }
            if (IsUnexpandedDeferredForEachRow(current.Planned))
            {
                ExpandCurrentDeferredForEach(run, current, answers);
                return;
            }
            var step = current.Planned.Step;
            var result = run.Results[current.PlanIndex];
            if (result.VerifyHistory.Count >= MaxSubmissionsPerStep)
                throw new InvalidOperationException(
                    $"Step '{step.Id}' has been submitted {MaxSubmissionsPerStep} times: the evidence archive is capped by refusing further submissions, never by truncating history. skip_workflow_step with a reason (recorded) or abort_workflow."
                    + SkipConsequence(run, current.Planned));

            // Pairing block: no await, no branch, and no throw between these five statements. The source
            // answer and the expansion decision become durable together; a later await cannot split them.
            result.Status = "in_progress";
            result.Answers = current.CommittedAnswers;
            result.VerifyResults = Array.Empty<VerifyResult>();
            result.Note = null;
            result.PendingForEach = current.DeferredForEach;

            var strictness = EffectiveStrictness(run, current.Planned);
            result.EffectiveStrictness = step.Gate == null ? null : strictness;

            // A later verify in this frame may use an earlier answer. The submission capture keeps this lookup
            // fixed while execution advances; newly-current step conditions deliberately resolve their own frame.
            var all = AccumulatedAnswers(run);

            if (step.Gate != null && strictness != "off")
            {
                EnforceInputs(step, all);
                EnforceInputPolicies(step, all, modelMeasureNames, modelColumns, modelFunctionNames, modelTables);

                var outcomes = new List<VerifyResult>();
                foreach (var spec in step.Gate.Verify)
                {
                    // E2: a conditional `when:` that does not hold is normally NOT_APPLICABLE. Coverage-backed
                    // equivalence is the v7 exception: after a prior pass, its executor must compare the current
                    // target identity and hash before it may call the verify not applicable.
                    var conditionalCandidateCheck = run.CoverageSurface != null
                        && spec.Kind == "dax_equivalence"
                        && run.LastPassedEquivalenceCandidate != null;
                    if (!WhenHolds(spec.When, all) && !conditionalCandidateCheck)
                    {
                        outcomes.Add(new VerifyResult { Kind = spec.Kind, Status = "not_applicable", Detail = $"when '{spec.When}' did not hold (input declined or absent): not applicable." });
                        continue;
                    }
                    if (executor == null)
                    {
                        // Kernel test seam only. Production always wires ExecuteWorkflowVerifyAsync. This skip is
                        // advisory so plan and authoring tests can submit a gated step without a fake executor.
                        // Live probes return unavailable, which does block a hard gate.
                        outcomes.Add(new VerifyResult { Kind = spec.Kind, Status = "skipped", Detail = "no verify executor wired." });
                        continue;
                    }
                    try { outcomes.Add(await executor(spec, step, run, all) ?? new VerifyResult { Kind = spec.Kind, Status = "unavailable", Missing = "a verify result", Detail = "executor returned nothing." }); }
                    catch (Exception ex) { outcomes.Add(new VerifyResult { Kind = spec.Kind, Status = "failed", Detail = ex.Message }); }
                }
                result.VerifyResults = outcomes.ToArray();
                // Blocker 1 — IMMUTABLE EVIDENCE HISTORY: archive this submission's outcomes append-only (cloned, so
                // the archive is a snapshot). The current results above still drive advancement; the archive is what
                // guarantees a re-submission can never erase the mismatch that motivated a partition/witness change.
                if (outcomes.Count > 0)
                    result.VerifyHistory.Add(new VerifyAttempt
                    {
                        Ordinal = result.VerifyHistory.Count + 1,
                        TimestampUtc = DateTime.UtcNow.ToString("o"),
                        Results = outcomes.Select(o => o.Clone()).ToArray(),
                    });

                // E2 — FAIL CLOSED: `failed` (evidence, gate unmet) and `unavailable` (applicable, no
                // authoritative evidence: offline / missing probe / zero coverage / drift / degraded fidelity)
                // BLOCK a hard step. A gate that waves through unprovable evidence is not a gate.
                // `not_applicable` still advances. The kernel test-seam `skipped` (no executor wired) is
                // advisory. skip_workflow_step remains the only audited way past a blocked hard gate.
                var blockers = outcomes.Where(o => o.Status == "failed" || o.Status == "unavailable").ToArray();
                if (blockers.Length > 0)
                {
                    if (strictness == "hard")
                    {
                        result.Status = "failed";
                        result.Note = "hard gate blocked: " + string.Join(" | ", blockers.Select(f => $"{f.Kind} [{f.Status}]: {Describe(f)}"));
                        throw new InvalidOperationException(
                            $"Step '{step.Id}' did not clear its hard gate. " +
                            string.Join(" ", blockers.Select(f => $"[{f.Kind}:{f.Status}] {Describe(f)}")) +
                            " Fix the issue and re-submit this step (or skip_workflow_step with a reason: the skip is recorded)."
                            + SkipConsequence(run, current.Planned));
                    }
                    result.Note = "warn gate: " + string.Join(" | ", blockers.Select(f => $"{f.Kind} [{f.Status}]: {Describe(f)}"));
                }
            }
            else if (step.Gate != null)
            {
                result.Note = "gate skipped (strictness off).";
            }

            ApplyPendingForEach(run, current, result);
            if (current.CallInputs != null)
                FrameById(run, CallFrameIdFor(current.Planned)).CommitCallInputs(current.CallInputs);
            result.Status = ConcludeSubmittedStep(result, step, strictness);
            // The iteration frame completes because this row was accepted and the run advances. The step's own
            // status may be done/failed/skipped; the frame still finished (warn failures do not retry).
            CompleteIterationFrame(current, "passed");
            Advance(run);
        }

        /// <summary>The badge follows the record. Passed means a check passed. Done means the step finished
        /// without proof (instructional click-through, answers or declines with no verify, or only not-applicable
        /// checks). Skipped means the gate was off. Failed means a verify failed or could not produce evidence,
        /// even when warn strictness still advances the run.</summary>
        internal static string ConcludeSubmittedStep(StepResult result, WorkflowStep step, string strictness)
        {
            var vrs = result.VerifyResults ?? Array.Empty<VerifyResult>();
            if (vrs.Any(v => v.Status == "failed" || v.Status == "unavailable"))
                return "failed";
            if (step?.Gate != null && string.Equals(strictness, "off", StringComparison.Ordinal))
                return "skipped";
            if (vrs.Any(v => v.Status == "passed"))
                return "passed";
            return "done";
        }

        /// <summary>§1.6 — the two separators an input-backed list may use. Comma or line break, and nothing
        /// else: a tab or a semicolon is part of an item's name, not a split, because the spec names these two.</summary>
        private static readonly char[] ForEachValueSeparators = { ',', '\r', '\n' };

        /// <summary>Resolve the current forEach template's item list. PURE by contract: every path, refusal or
        /// success, leaves plan, results, frames, statuses, StepIndex and the authored definition untouched, and
        /// the returned list is always freshly allocated. Splicing a resolved list into the plan is
        /// <see cref="ExpandCurrentForEach"/>, kept a separate transition on purpose so a resolution failure can
        /// never half-expand a loop. Inline expansion calls both in one transition. Preparation uses the same
        /// input-list parser directly, then calls expansion in its own atomic transition.</summary>
        internal static IReadOnlyList<string> ResolveCurrentForEachValues(WorkflowRunState run)
        {
            if (run == null) throw new ArgumentNullException(nameof(run));
            if (run.Status != "active")
                throw new InvalidOperationException($"Run '{run.RunId}' is {run.Status}: its forEach values cannot resolve.");
            if (run.StepIndex < 0 || run.StepIndex >= run.Plan.Count)
                throw new InvalidOperationException($"Run '{run.RunId}' has no current plan entry whose forEach values could resolve.");
            if (run.Plan.Count != run.Results.Count)
                throw new InvalidOperationException($"Run '{run.RunId}' cannot resolve forEach values because its plan and results are not aligned.");

            var index = run.StepIndex;
            var source = run.Plan[index];
            if (!string.Equals(run.Results[index].StepId, source.InstanceId, StringComparison.Ordinal))
                throw new InvalidOperationException($"Step '{source.InstanceId}' cannot resolve its forEach values because its result row is not aligned.");
            var step = source.Step;
            if (step.ForEach == null)
                throw new InvalidOperationException($"Step '{source.InstanceId}' is not a forEach template.");
            if (source.IterationIndex.HasValue)
                throw new InvalidOperationException($"Step '{source.InstanceId}' is an iteration row from an already expanded forEach template.");
            if (source.ForEachExpanded)
                throw new InvalidOperationException($"Step '{source.InstanceId}' was already expanded.");

            // An inline literal already IS the item list: the author wrote each entry as one item, so it is
            // copied out verbatim. Splitting, trimming or dropping blanks here would silently rewrite an
            // authored name -- §1.6 defines those rules for the INPUT spelling only, where one answer string
            // has to become many items. The copy is defensive: a caller may not reach back into `ForEach`.
            if (step.ForEach.InLiteral != null) return FreshForEachValueCopy(step.ForEach.InLiteral);

            var name = step.ForEach.InInput;
            if (string.IsNullOrWhiteSpace(name))
                throw new InvalidOperationException(
                    $"Step '{source.InstanceId}' names no forEach list source: `in:` is either an inline literal list or an inputs.<name> reference.");

            // Resolution reads THIS planned step's own frame through a cutoff that INCLUDES its own row. The list
            // answer may come from an earlier step or from this loop step's own submission (decision 1.6.1), so
            // the current row must remain visible without admitting any later row.
            var answers = AccumulatedAnswers(run, run.FrameAtPlanIndex(index), index);
            answers.TryGetValue(name, out var answer);
            if (answer != null && answer.Declined)
                throw new InvalidOperationException(
                    $"Step '{source.InstanceId}' cannot resolve its forEach list: input '{name}' was declined "
                    + $"({(string.IsNullOrWhiteSpace(answer.DeclineReason) ? "no reason recorded" : answer.DeclineReason.Trim())}). "
                    + "A decline names nothing to repeat over, which is not the same claim as an empty list. Answer it with the "
                    + "list, or skip_workflow_step with a reason (the skip is recorded)."
                    + SkipConsequence(run, source));
            if (answer == null || answer.Value == null)
            {
                // THE GAP PATH, and the scope distinction has to be made HERE. An answer excluded by frame
                // visibility is exactly an answer this projection has no entry for, so a check placed after
                // this method returns would never run: every cross-frame case would already have been
                // reported as missing. The ONE backwards resolution below decides which sentence to emit,
                // and it is the same walk that decides blank ownership, so the two can never disagree about
                // which answer is the winner.
                var visible = ResolveVisibleForEachSource(run, index, name);
                if (visible.Excluded == ForEachSourceExclusion.ScopeOnly)
                    throw new InvalidOperationException(
                        $"Step '{source.InstanceId}' cannot resolve its forEach list: input '{name}' is answered on step "
                        + $"'{run.Results[visible.AnsweringPlanIndex].StepId}', in a different frame, and its declaration there is not "
                        + "`scope: run`. An answer stays inside the frame that recorded it unless its declaration says otherwise. "
                        + $"Declare '{name}' with `scope: run` so it survives out to this loop, or answer it in this frame, "
                        + "or skip_workflow_step with a reason (the skip is recorded)."
                        + SkipConsequence(run, source));
                // CallBoundary lands here on purpose. OverlayAnswers drops a call-frame row BEFORE it ever
                // consults `scope: run`, so adding scope to that declaration would change nothing and the
                // scope sentence would be advice that cannot be followed.
                var declarer = DeclaringStepIdFor(run, source, name);
                throw new InvalidOperationException(
                    $"Step '{source.InstanceId}' cannot resolve its forEach list: input '{name}' is unanswered in this frame. "
                    + "Answer it on the step that declares it before this loop expands. An answered blank list is an empty loop; "
                    + "an unanswered one is a gap, and guessing at it would fabricate the coverage the loop then certifies."
                    + (declarer == null ? "" : $" The input is declared on step '{declarer}'.")
                    + " skip_workflow_step with a reason (the skip is recorded)."
                    + SkipConsequence(run, source));
            }

            return FreshForEachValueCopy(SplitInputBackedForEachValues(answer.Value));
        }

        /// <summary>§1.6 — an input-backed list is comma-or-newline separated. Items are trimmed and blanks dropped,
        /// so a trailing comma or a CRLF pair adds no phantom iteration, and an OPTIONAL input answered blank is an
        /// empty loop (acceptance check 14b) rather than one pass over the empty string. The ONE spelling: resolution
        /// and the preparation submission must never disagree about what a given answer means.</summary>
        private static List<string> SplitInputBackedForEachValues(string value) => value
            .Split(ForEachValueSeparators)
            .Select(item => item.Trim())
            .Where(item => item.Length > 0)
            .ToList();

        private static string[] FreshForEachValueCopy(ICollection<string> values)
        {
            var copy = new string[values.Count];
            values.CopyTo(copy, 0);
            return copy;
        }

        /// <summary>True when this planned row is the one shape a PREPARATION submission answers: an unexpanded
        /// input-backed forEach template whose list source is declared on the template's own gate. Do not widen
        /// this to inline literals: those take a no-answer expansion. A source declared on an earlier step, an
        /// iteration row and an ordinary row stay false.</summary>
        private static bool IsForEachPreparation(PlannedStep planned) =>
            planned.Step.ForEach != null
            && !planned.IterationIndex.HasValue
            && !planned.ForEachExpanded
            && SelfDeclaredForEachSource(planned.Step) != null;

        /// <summary>True when this planned row is an unexpanded inline InLiteral loop, including <c>in: []</c>.
        /// The array is present and InInput is empty, so the list is already authored and this call answers nothing.</summary>
        private static bool IsInlineForEachExpansion(PlannedStep planned) =>
            planned.Step.ForEach?.InLiteral != null
            && string.IsNullOrWhiteSpace(planned.Step.ForEach.InInput)
            && !planned.IterationIndex.HasValue
            && !planned.ForEachExpanded;

        /// <summary>True when this planned row is an unexpanded input-backed loop whose source is NOT
        /// declared on its own gate. Unit 15's preparation shape is therefore disjoint by construction.</summary>
        private static bool IsUnexpandedDeferredForEachRow(PlannedStep planned) =>
            planned.Step.ForEach != null
            && planned.Step.ForEach.InLiteral == null
            && !string.IsNullOrWhiteSpace(planned.Step.ForEach.InInput)
            && !planned.IterationIndex.HasValue
            && !planned.ForEachExpanded
            && SelfDeclaredForEachInput(planned.Step) == null;

        /// <summary>The three current-row shapes that take a setup submission. They are mutually exclusive by
        /// construction: preparation requires a self-declared source and the deferred predicate requires none;
        /// inline requires <c>InLiteral != null</c> and the deferred predicate requires it null. The deferred row
        /// joins this union in the same landing that gives it a submission to take: it now accepts exactly one
        /// no-answer call that runs no gate, so advertising the authored gate on it would offer questions that
        /// call refuses.</summary>
        private static bool IsExpansionOnlyRow(PlannedStep planned) =>
            IsForEachPreparation(planned) || IsInlineForEachExpansion(planned)
            || IsUnexpandedDeferredForEachRow(planned);

        /// <summary>The same classification against the run's CURRENT row, so the engine can route a submission
        /// away from the submission-frame and witness-receipt scope before installing either. PURE: it reads the
        /// plan and answers nothing.</summary>
        internal static bool IsForEachPreparation(WorkflowRunState run) =>
            run != null && run.Status == "active" && run.StepIndex >= 0 && run.StepIndex < run.Plan.Count
            && run.Plan.Count == run.Results.Count && IsForEachPreparation(run.Plan[run.StepIndex]);

        /// <summary>Union of the three expansion-only current-row shapes. LocalEngine uses this one capture-skip
        /// for all three; the runner still distinguishes payload rules internally.</summary>
        internal static bool IsExpansionOnlySubmission(WorkflowRunState run) =>
            run != null && run.Status == "active" && run.StepIndex >= 0 && run.StepIndex < run.Plan.Count
            && run.Plan.Count == run.Results.Count && IsExpansionOnlyRow(run.Plan[run.StepIndex]);

        // The three setup phrases. Each promises the FIRST APPLICABLE iteration, never #0: a loop-dependent
        // `when:` is judged per iteration after the splice, so the walk may exclude #0 and any run of leading
        // iterations. None of the three may assert the state of a source this call has not validated yet:
        // every one of these strings is rendered on the current row BEFORE the setup call runs, and the same
        // row is reached when the source is missing, declined, blank, over-bound or out of scope.
        private const string PreparationSetupInstructions =
            "This call only fixes the loop list. Answer exactly the visible source question. Do not do iteration work yet. The first applicable iteration will return the first real instructions.";
        private const string InlineSetupInstructions =
            "The authored list is ready. Submit the current base id with no answers. Do not do iteration work yet. The first applicable iteration will return the first real instructions.";
        private const string DeferredSetupInstructions =
            "This loop does not ask you for its list. Submit the current base id with no answers. Do not do iteration work yet. The first applicable iteration will return the first real instructions.";

        /// <summary>The bounded inline expansion: refuse any named field, resolve the authored list, splice.
        /// Null and an empty dictionary are the same no-names payload. Extra names belong on #0.</summary>
        private static void ExpandCurrentInlineForEach(
            WorkflowRunState run, CoherentCurrentStep current, IReadOnlyDictionary<string, AnswerValue> answers)
        {
            var extra = (answers ?? (IReadOnlyDictionary<string, AnswerValue>)new Dictionary<string, AnswerValue>())
                .Keys.OrderBy(name => name, StringComparer.Ordinal).ToArray();
            if (extra.Length > 0)
                throw new InvalidOperationException(
                    $"Step '{current.Planned.InstanceId}' is an unexpanded inline forEach template: this submission expands the authored list and answers nothing. "
                    + $"Named: {string.Join(", ", extra)}. Submit {(extra.Length == 1 ? "it" : "them")} against the iteration this expansion makes current.");

            CompleteCurrentForEachSetup(run, current, ResolveCurrentForEachValues(run), null);
        }

        /// <summary>The bounded deferred loop-entry transition (§1.6.1d): refuse any named field, resolve the
        /// loop's own list from its loop-entry projection, validate blank against the winning visible answer's
        /// owning declaration, then hand to the shared setup helper. Every step before the splice is PURE, so a
        /// refusal at any of them leaves the run byte-identical. This call answers nothing and records no
        /// decision: there is no answer for a decision to pair with.</summary>
        private static void ExpandCurrentDeferredForEach(
            WorkflowRunState run, CoherentCurrentStep current, IReadOnlyDictionary<string, AnswerValue> answers)
        {
            var planned = current.Planned;
            var step = planned.Step;
            var sourceName = step.ForEach.InInput;

            var extra = (answers ?? (IReadOnlyDictionary<string, AnswerValue>)new Dictionary<string, AnswerValue>())
                .Keys.OrderBy(name => name, StringComparer.Ordinal).ToArray();
            if (extra.Length > 0)
            {
                // The mistake an agent actually makes is re-answering the loop's own source here, so that one
                // name earns an extra sentence naming where it belongs. It may NOT say the list is answered or
                // resolved: this refusal fires before any source validation, so at this moment the list may be
                // missing, declined, blank, over-bound or out of scope.
                var namesSource = extra.Contains(sourceName, StringComparer.Ordinal);
                var declarer = namesSource ? DeclaringStepIdFor(run, planned, sourceName) : null;
                throw new InvalidOperationException(
                    $"Step '{planned.InstanceId}' is an unexpanded forEach template whose list source is declared on an earlier step: "
                    + "this submission expands that list and answers nothing. "
                    + $"Named: {string.Join(", ", extra)}. Submit {(extra.Length == 1 ? "it" : "them")} against the iteration this expansion makes current."
                    + (!namesSource ? ""
                        : $" '{sourceName}' is this loop's own list source: the loop reads it from "
                          + (declarer == null ? "the step that declares it" : $"step '{declarer}', which declares it")
                          + ", not from this call."));
            }

            // ONE backwards resolution decides missing, declined and cross-frame scope, in the order the
            // resolver already has: decline first, then the gap path, where the scope branch lives.
            var values = ResolveCurrentForEachValues(run);

            // Blank ownership follows the EXACT latest visible answer selected into the loop-entry projection.
            // AccumulatedAnswers is latest-visible-wins, so a later optional blank overrides an earlier required
            // value and means empty, and a later required blank overrides an earlier optional value and refuses.
            // An unrelated earlier declaration cannot veto the winner and a definition-wide first match cannot
            // bless it, which is why this reads the same walk the gap path used rather than a second one.
            if (values.Count == 0)
            {
                var visible = ResolveVisibleForEachSource(run, current.PlanIndex, sourceName);
                var owner = visible.Owner;
                if (owner == null)
                    throw new InvalidOperationException(
                        $"Step '{planned.InstanceId}' cannot expand: its forEach list source '{sourceName}' is answered blank, and the "
                        + "declaration that owns that answer cannot be identified, so nothing here permits a blank to spell an empty loop. "
                        + "Only a source declared `required: optional` may spell an empty loop as a blank answer. Answer it with the list, "
                        + "or skip_workflow_step with a reason (the skip is recorded)."
                        + SkipConsequence(run, planned));
                if (owner.Required != "optional")
                    throw new InvalidOperationException(
                        $"Step '{planned.InstanceId}' cannot expand: its forEach list source '{sourceName}' is answered blank, "
                        + "and a blank answer to a non-optional input is the same gap the input gate refuses, not an empty list. "
                        + $"Ask the user: \"{owner.Question}\" "
                        + (owner.Required == "required"
                            ? "(required: it cannot be declined)"
                            : "(answer it, or decline explicitly with {\"declined\": true, \"reason\": \"...\"})")
                        + " Only a source declared `required: optional` may spell an empty loop as a blank answer. "
                        + "skip_workflow_step with a reason (the skip is recorded)."
                        + SkipConsequence(run, planned));
            }

            CompleteCurrentForEachSetup(run, current, values, null);
        }

        /// <summary>The bounded preparation transition. It validates the WHOLE replacement — payload shape, the
        /// source answer, the resolved list, the bound, and every synthetic frame identity — before any run object
        /// changes, then commits plan, results, frames, frozen answer clones, statuses and index together. The
        /// submitted answer is never written to the template row and rolled back: a refused preparation leaves a
        /// run that never saw it.</summary>
        private static void PrepareCurrentForEach(
            WorkflowRunState run, CoherentCurrentStep current, IReadOnlyDictionary<string, AnswerValue> answers)
        {
            var planned = current.Planned;
            var step = planned.Step;
            var declaration = SelfDeclaredForEachInput(step);
            var source = declaration.Name;

            // Undeclared names are DROPPED by an ordinary submission, but a preparation may not drop OR keep a
            // second name: it does not run the gate that would enforce it, and the iteration it creates is where
            // that answer belongs. Refusing is the only reading that neither loses an answer nor shares it.
            var extra = (answers ?? (IReadOnlyDictionary<string, AnswerValue>)new Dictionary<string, AnswerValue>())
                .Keys.Where(name => !string.Equals(name, source, StringComparison.Ordinal))
                .OrderBy(name => name, StringComparer.Ordinal).ToArray();
            if (extra.Length > 0)
                throw new InvalidOperationException(
                    $"Step '{planned.InstanceId}' is an unexpanded forEach template: this submission fixes the iteration count from its list source '{source}' and answers nothing else. "
                    + $"Named besides it: {string.Join(", ", extra)}. Submit {(extra.Length == 1 ? "it" : "them")} against the iteration this expansion makes current.");

            // Every unusable-source refusal on an explicit current-row setup call names the SAME recorded way
            // past and states what that skip costs. The inline and deferred setup paths already did; preparation
            // is the shape that reaches this row holding the only answer that could make it usable, so leaving
            // it without a route offered the agent a wall on the one call it is allowed to make here.
            AnswerValue submitted = null;
            answers?.TryGetValue(source, out submitted);
            if (submitted == null && NearestCallFrame(run, current.Frame) != null)
                AccumulatedAnswers(run, current.Frame, current.PlanIndex).TryGetValue(source, out submitted);
            if (submitted == null)
                throw new InvalidOperationException(
                    $"Step '{planned.InstanceId}' cannot expand: its forEach list source '{source}' is unanswered in this submission. "
                    + "Answer it with the list. An answered blank list is an empty loop; an unanswered one is a gap, and guessing at it would fabricate the coverage the loop then certifies. "
                    + "skip_workflow_step with a reason (the skip is recorded)."
                    + SkipConsequence(run, planned));
            if (submitted.Declined)
                throw new InvalidOperationException(
                    $"Step '{planned.InstanceId}' cannot expand: its forEach list source '{source}' was declined "
                    + $"({(string.IsNullOrWhiteSpace(submitted.DeclineReason) ? "no reason recorded" : submitted.DeclineReason.Trim())}). "
                    + "A decline names nothing to repeat over, which is not the same claim as an empty list. Answer it with the "
                    + "list, or skip_workflow_step with a reason (the skip is recorded)."
                    + SkipConsequence(run, planned));
            if (submitted.Value == null)
                throw new InvalidOperationException(
                    $"Step '{planned.InstanceId}' cannot expand: its forEach list source '{source}' is unanswered in this submission. "
                    + "A null value is the same gap as no answer at all, and is not an empty list. "
                    + "Answer it with the list, or skip_workflow_step with a reason (the skip is recorded)."
                    + SkipConsequence(run, planned));

            // A preparation deliberately does NOT run EnforceInputs: the iteration questions it refuses to accept
            // must not be enforced against it. But the list source is a gate input like any other and its own
            // `required:` rule still binds, so it is enforced HERE or nowhere. `EnforceInputs` treats a blank
            // answer to a non-optional input as missing, which is exactly why acceptance check 14b declares the
            // source `required: optional` to reach an empty loop at all; routing around the gate must not quietly
            // widen that. Blank and separators-only are the same case: both resolve to no items.
            var values = SplitInputBackedForEachValues(submitted.Value);
            if (values.Count == 0 && declaration.Required != "optional")
                throw new InvalidOperationException(
                    $"Step '{planned.InstanceId}' cannot expand: its forEach list source '{source}' is answered blank, "
                    + "and a blank answer to a non-optional input is the same gap the input gate refuses, not an empty list. "
                    + $"Ask the user: \"{declaration.Question}\" "
                    + (declaration.Required == "required"
                        ? "(required: it cannot be declined)"
                        : "(answer it, or decline explicitly with {\"declined\": true, \"reason\": \"...\"})")
                    + " Only a source declared `required: optional` may spell an empty loop as a blank answer. "
                    + "skip_workflow_step with a reason (the skip is recorded)."
                    + SkipConsequence(run, planned));

            CompleteCurrentForEachSetup(run, current, values, submitted);
        }

        /// <summary>The ONE ordered spelling of what every explicit current-row setup call does once its own
        /// shape-specific source has been validated: enforce the bound, classify the condition, splice, then walk
        /// to the first applicable iteration. Preparation, inline and deferred setup all pass through here, so
        /// "the bound is enforced before the condition is parsed" is a property of the unit rather than of one
        /// branch of it. The one data difference between the shapes is <paramref name="preparedSource"/>, the
        /// transient facts overlay self-sourced preparation needs before it persists anything.</summary>
        private static void CompleteCurrentForEachSetup(
            WorkflowRunState run, CoherentCurrentStep current, IReadOnlyList<string> values, AnswerValue preparedSource)
        {
            // FIRST, and before any condition is read. The bound is decided from the source alone, so a source
            // that cannot produce a legal loop refuses on its own sentence rather than being absorbed into a
            // not_applicable record by a condition that has not been parsed yet.
            ValidateForEachBound(run, current, values);

            var when = current.Planned.Step.When;
            if (!string.IsNullOrWhiteSpace(when))
            {
                // Setup parses once for classification, through the shipped evaluator, only after source
                // validation accepted. Expanded rows use the existing per-iteration parse and evaluation walk.
                var expr = WorkflowPredicate.Parse(when, out var parseError);
                var loopDependent = expr != null
                    && WorkflowPredicate.ReferencedRoots(expr).Contains("loop", StringComparer.Ordinal);
                if (!loopDependent)
                {
                    var facts = SetupConditionFacts(run, current, preparedSource);
                    if (expr == null || !WorkflowPredicate.Evaluate(expr, facts))
                    {
                        // Loop-free false or unreadable: mark the UNEXPANDED template not_applicable, create zero
                        // iterations, no frame and no #n id, and advance exactly once. ForEachExpanded stays
                        // false, so TotalStepsProvisional stays conservatively true on this row.
                        RecordConditionNotApplicableAndAdvance(run, current, when, expr, parseError, facts);
                        AdvancePastInapplicableSteps(run);
                        return;
                    }
                }
                // A predicate that references any loop fact is left WHOLE for per-iteration judgement below.
                // Never text-matched, and never partly folded: the grammar has no partial form.
            }

            ExpandCurrentForEach(run, values, preparedSource);
            // The empty path already advanced once inside the splice. A nonempty splice leaves #0 current with
            // real loop bindings, and this walk is what judges them: it marks each excluded iteration
            // not_applicable, completes that iteration's frame, and lands on the FIRST APPLICABLE iteration,
            // which is #0 only when #0 applies.
            if (values.Count > 0) AdvancePastInapplicableSteps(run);
        }

        /// <summary>The pure bound check every setup shape shares. <see cref="ExpandForEachAt"/> keeps its own
        /// defensive copy of this rule; this one runs earlier, before the condition is parsed, and carries the
        /// recorded skip route. The splice must never be the thing that discovers the bound.</summary>
        private static void ValidateForEachBound(
            WorkflowRunState run, CoherentCurrentStep current, IReadOnlyList<string> values)
        {
            var step = current.Planned.Step;
            if (values.Count <= step.ForEach.MaxIterations) return;
            throw new InvalidOperationException(
                $"Step '{current.Planned.InstanceId}' resolved {values.Count} forEach values, above maxIterations {step.ForEach.MaxIterations}. "
                + "The list was not truncated and the step remains current. skip_workflow_step with a reason (the skip is recorded)."
                + SkipConsequence(run, current.Planned));
        }

        /// <summary>The one facts seam the three setup shapes share. Inline and deferred setup carry no prepared
        /// answer and use ordinary <see cref="ConditionFacts"/>. Self-sourced preparation has deliberately
        /// persisted NOTHING at this point. A later refusal must leave a run that never saw the payload, so its
        /// freshly validated source is overlaid transiently, making both <c>inputs.&lt;source&gt;.answered</c> and
        /// <c>inputs.&lt;source&gt;.value</c> describe the payload this same transition is preparing. The overlay
        /// is facts-only, writes no result row, seed or plan, and is discarded after classification.</summary>
        private static PredicateFacts SetupConditionFacts(
            WorkflowRunState run, CoherentCurrentStep current, AnswerValue preparedSource)
        {
            var facts = ConditionFacts(run, current);
            var source = SelfDeclaredForEachSource(current.Planned.Step);
            if (preparedSource == null || source == null) return facts;
            var inputs = new Dictionary<string, AnswerValue>(StringComparer.Ordinal);
            foreach (var kv in facts.Inputs) inputs[kv.Key] = kv.Value;
            inputs[source] = RunFrame.CloneAnswer(preparedSource);
            facts.Inputs = inputs;
            return facts;
        }

        /// <summary>Replace the current, already-resolved forEach template with aligned iteration rows.
        /// This is an internal plan transition only; resolving the authored list is a separate transition.</summary>
        internal static void ExpandCurrentForEach(WorkflowRunState run, IReadOnlyList<string> values) =>
            ExpandForEachAt(run, run.StepIndex, values, null);

        /// <summary><paramref name="preparedSource"/> is the answer a preparation submission carries for a
        /// self-declared list source. It is the ONE value that may be frozen without already sitting in the
        /// loop-entry seed; every other caller passes null and the seed must already hold it.</summary>
        private static void ExpandCurrentForEach(
            WorkflowRunState run, IReadOnlyList<string> values, AnswerValue preparedSource) =>
            ExpandForEachAt(run, run.StepIndex, values, preparedSource);

        /// <summary>The one splice. A deferred caller passes a target strictly greater than the current index,
        /// so this rewrite cannot move the declaring step's captured submission index.</summary>
        private static void ExpandForEachAt(
            WorkflowRunState run, int targetIndex, IReadOnlyList<string> values, AnswerValue preparedSource,
            string expectedTargetStepId = null)
        {
            if (run == null) throw new ArgumentNullException(nameof(run));
            if (values == null) throw new ArgumentNullException(nameof(values));
            if (run.Status != "active")
                throw new InvalidOperationException($"Run '{run.RunId}' is {run.Status}: its plan cannot expand.");
            if (targetIndex < 0 || targetIndex >= run.Plan.Count)
                throw new InvalidOperationException($"Run '{run.RunId}' has no plan entry to expand at index {targetIndex}.");
            if (run.Plan.Count != run.Results.Count)
                throw new InvalidOperationException($"Run '{run.RunId}' cannot expand because its plan and results are not aligned.");

            var index = targetIndex;
            var source = run.Plan[index];
            var step = source.Step;
            // [T220] Resolved once, from the TEMPLATE row, and copied by reference onto every row this
            // expansion creates. An expansion cannot change which workflow authored the row, so no loop path
            // looks an owner up or invents one.
            var owner = run.RequireCoherentRow(source);
            if (step.ForEach == null)
                throw new InvalidOperationException($"Step '{source.InstanceId}' is not a forEach template.");
            if (source.IterationIndex.HasValue)
                throw new InvalidOperationException($"Step '{source.InstanceId}' is an iteration row from an already expanded forEach template.");
            if (source.ForEachExpanded)
                throw new InvalidOperationException($"Step '{source.InstanceId}' was already expanded.");
            if (!string.Equals(run.Results[index].StepId, source.InstanceId, StringComparison.Ordinal))
                throw new InvalidOperationException($"Step '{source.InstanceId}' cannot expand because its result row is not aligned.");
            if (expectedTargetStepId != null
                && !string.Equals(expectedTargetStepId, source.InstanceId, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"Step '{expectedTargetStepId}' cannot expand because plan index {index} is '{source.InstanceId}'.");
            if (values.Count > step.ForEach.MaxIterations)
                throw new InvalidOperationException(
                    $"Step '{step.Id}' resolved {values.Count} forEach values, above maxIterations {step.ForEach.MaxIterations}. The list was not truncated and the step remains current.");

            // Build and validate the whole replacement before touching run state. CreateIteration clones the
            // seed independently for every frame, so one iteration can never alter another's loop-entry answers.
            var seedSources = ResolveAnswerSources(run, run.FrameAtPlanIndex(index), index);
            var seed = seedSources.ToDictionary(p => p.Key, p => RunFrame.CloneAnswer(p.Value.Answer), StringComparer.Ordinal);
            var frozenSource = SelfDeclaredForEachSource(step);
            AnswerValue frozenAnswer = null;
            if (frozenSource != null)
            {
                if (preparedSource != null)
                {
                    // The preparation's own answer joins the loop-entry namespace here, not on the template row:
                    // a refusal below must leave a run that never recorded it.
                    frozenAnswer = preparedSource;
                    seed[frozenSource] = RunFrame.CloneAnswer(preparedSource);
                    seedSources[frozenSource] = new WorkflowAnswerSource(RunFrame.CloneAnswer(preparedSource),
                        SelfDeclaredForEachInput(step), index);
                }
                else if (!seed.TryGetValue(frozenSource, out var candidate) || candidate?.Answered != true)
                {
                    throw new InvalidOperationException(
                        $"Step '{source.InstanceId}' cannot expand because its self-declared forEach source input '{frozenSource}' "
                        + "is not answered in the loop-entry seed. Capture the source answer before expansion; a new iteration hides that frozen input and cannot repair a missing value.");
                }
                else
                {
                    frozenAnswer = candidate;
                }
            }
            if (values.Count == 0)
            {
                var expandedSource = new PlannedStep(step, source.InstanceId, source.FrameId, null, owner, forEachExpanded: true);
                run.Plan[index] = expandedSource;
                var emptyResult = run.Results[index];
                // An empty loop keeps the source row, so a self-declared source answer stays a VISIBLE audit
                // answer on that row rather than surviving only in a hidden seed. A deferred empty target
                // records no answers here: the source answer already lives on the declaring row.
                if (frozenSource != null && preparedSource != null)
                    emptyResult.Answers[frozenSource] = RunFrame.CloneAnswer(preparedSource);
                emptyResult.Status = "not_applicable";
                emptyResult.Note = "did not apply: the forEach value list was empty.";
                emptyResult.EffectiveStrictness = step.Gate == null ? null
                    : EffectiveStrictness(run, expandedSource);
                if (targetIndex == run.StepIndex) Advance(run);
                return;
            }

            var planned = new List<PlannedStep>(values.Count);
            var results = new List<StepResult>(values.Count);
            var frames = new List<RunFrame>(values.Count);
            var frameIds = new HashSet<string>(run.Frames.Select(f => f.FrameId), StringComparer.Ordinal);
            for (var iteration = 0; iteration < values.Count; iteration++)
            {
                var instanceId = $"{source.InstanceId}#{iteration}";
                var frameId = $"{source.FrameId}:{source.InstanceId}:{iteration}";
                if (!frameIds.Add(frameId))
                    throw new InvalidOperationException($"Step '{step.Id}' cannot expand because frame id '{frameId}' already exists.");
                planned.Add(new PlannedStep(step, instanceId, frameId, iteration, owner, forEachExpanded: true));
                var result = new StepResult
                {
                    StepId = instanceId,
                    Title = step.Title,
                    Status = iteration == 0 && targetIndex == run.StepIndex ? "in_progress" : "pending",
                };
                if (frozenSource != null && frozenAnswer != null)
                    result.Answers[frozenSource] = RunFrame.CloneAnswer(frozenAnswer);
                results.Add(result);
                frames.Add(RunFrame.CreateIteration(frameId, step.Id, iteration, step.ForEach.As,
                    values[iteration], "in_progress", seed, parentFrameId: source.FrameId, seedSources: seedSources));
            }

            run.Plan.RemoveAt(index);
            run.Plan.InsertRange(index, planned);
            run.Results.RemoveAt(index);
            run.Results.InsertRange(index, results);
            run.Frames.AddRange(frames);
            ExpandAvailableCalls(run, index);
        }

        // ==================== [T220] the call plan splice ====================
        // ONE bounded transition pair. ResolveCallSplice is PURE: every refusal it owns is decided before any
        // mutation, so a refused hand-off leaves plan, results, frames, statuses, owners and index
        // byte-identical. SpliceCallAt builds every object first and commits last, which is ExpandForEachAt's
        // order and for the same reason: an invariant that fires halfway through leaves a torn plan nobody can
        // address. ResolveCallSplice is also the ONE place an admissibility rule is written: SpliceCallAt
        // re-runs it against the live run instead of keeping a second, drifting copy, so a blueprint that went
        // stale between the two calls is refused rather than committed. Initialization uses this transition
        // before registration; a loop uses it after creating each iteration's header.

        /// <summary>The frame one id names, or null. Kept separate from
        /// <see cref="WorkflowRunState.FrameAtPlanIndex"/>, which answers for a ROW and throws on a miss: a
        /// parent walk legitimately ends at a frame whose parent is null.</summary>
        private static RunFrame FrameById(WorkflowRunState run, string frameId) => frameId == null ? null
            : run.Frames.FirstOrDefault(f => string.Equals(f.FrameId, frameId, StringComparison.Ordinal));

        /// <summary>How deep a hand-off from this frame would be, counting the top-level run as 1
        /// (<see cref="CallSpec.MaxDepth"/>). Decided by WALKING <see cref="RunFrame.ParentFrameId"/> and never
        /// by parsing a frame id: ids are built by concatenation and are not a grammar. An iteration frame
        /// returns its parent's depth, because repeating a step ten times is not ten hand-offs. The walk is
        /// bounded by the frame count and THROWS on an overrun rather than spinning on a malformed chain.</summary>
        internal static int DepthOf(WorkflowRunState run, RunFrame frame)
        {
            if (run == null) throw new ArgumentNullException(nameof(run));
            if (frame == null) throw new ArgumentNullException(nameof(frame));
            var current = frame;
            for (var hops = 0; hops <= run.Frames.Count; hops++)
            {
                if (string.Equals(current.ProjectionKind, "call", StringComparison.Ordinal))
                    return current.Depth ?? 1;
                if (current.ParentFrameId == null) return 1;
                var parent = FrameById(run, current.ParentFrameId);
                if (parent == null)
                    throw new InvalidOperationException(
                        $"Frame '{current.FrameId}' names parent frame '{current.ParentFrameId}', which this run does not hold.");
                current = parent;
            }
            throw new InvalidOperationException(
                $"Frame '{frame.FrameId}' has a parent chain longer than this run's {run.Frames.Count} frames, so it does not terminate.");
        }

        /// <summary>The workflow names on one frame's chain, innermost last, rooted at the run's own definition
        /// name. R6 and R7 share this ONE traversal: two questions, one walk, no second dialect.</summary>
        private static List<string> FrameChainNames(WorkflowRunState run, RunFrame frame)
        {
            var names = new List<string>();
            var current = frame;
            for (var hops = 0; hops <= run.Frames.Count; hops++)
            {
                if (string.Equals(current.ProjectionKind, "call", StringComparison.Ordinal))
                    names.Add(current.Workflow);
                if (current.ParentFrameId == null)
                {
                    names.Add(run.Def.Name);
                    names.Reverse();
                    return names;
                }
                var parent = FrameById(run, current.ParentFrameId);
                if (parent == null)
                    throw new InvalidOperationException(
                        $"Frame '{current.FrameId}' names parent frame '{current.ParentFrameId}', which this run does not hold.");
                current = parent;
            }
            throw new InvalidOperationException(
                $"Frame '{frame.FrameId}' has a parent chain longer than this run's {run.Frames.Count} frames, so it does not terminate.");
        }

        /// <summary>Whether <paramref name="frame"/> IS the ancestor or descends from it through
        /// <see cref="RunFrame.ParentFrameId"/>. Bounded by the frame count so a malformed chain terminates.</summary>
        private static bool FrameIsOrDescendsFrom(WorkflowRunState run, RunFrame frame, string ancestorFrameId)
        {
            var current = frame;
            for (var hops = 0; hops <= run.Frames.Count; hops++)
            {
                if (current == null) return false;
                if (string.Equals(current.FrameId, ancestorFrameId, StringComparison.Ordinal)) return true;
                if (current.ParentFrameId == null) return false;
                current = FrameById(run, current.ParentFrameId);
            }
            throw new InvalidOperationException(
                $"Frame '{frame.FrameId}' has a parent chain longer than this run's {run.Frames.Count} frames, so it does not terminate.");
        }

        /// <summary>Plan indexes belonging to one call frame OR to any frame descended from it. The descendant
        /// half is load-bearing, not tidiness: <see cref="ExpandForEachAt"/> REMOVES a loop template row and
        /// inserts iteration rows carrying a NEW frame id, so a callee row that is a `forEach:` template leaves
        /// the call frame's own row set the moment it expands. Judged over frame-id equality alone that set
        /// becomes empty and the frame either never completes or completes while the run is still inside the
        /// loop it contains.</summary>
        private static List<int> CallFrameRowIndexes(WorkflowRunState run, string callFrameId)
        {
            var rows = new List<int>();
            for (var i = 0; i < run.Plan.Count && i < run.Results.Count; i++)
                if (FrameIsOrDescendsFrom(run, run.FrameAtPlanIndex(i), callFrameId)) rows.Add(i);
            return rows;
        }

        /// <summary>The call frame a spliced header owns, by the ratified identity
        /// <c>{header.FrameId}:{header.InstanceId}</c>.</summary>
        private static string CallFrameIdFor(PlannedStep header) => $"{header.FrameId}:{header.InstanceId}";

        /// <summary>Part one, PURE. Refuses; mutates nothing. It takes no <see cref="WorkflowDef"/> and no
        /// settings string: both are already frozen on the run, so the runner READS them rather than being
        /// handed them, and a caller handing in the wrong definition stops being a runtime possibility and
        /// becomes a refusal.</summary>
        internal static CallSplice ResolveCallSplice(WorkflowRunState run, int headerIndex) =>
            ResolveCallSplice(run, headerIndex, candidateOwner: null);

        /// <summary>The candidate-accepting overload. Only the EXACT frozen closure member may pass, and the
        /// four bad candidates are diagnosed apart because they have four different causes and four different
        /// fixes. Passing null derives the owner, which is what production does.</summary>
        internal static CallSplice ResolveCallSplice(WorkflowRunState run, int headerIndex, WorkflowDef candidateOwner)
        {
            if (run == null) throw new ArgumentNullException(nameof(run));
            if (headerIndex < 0 || headerIndex >= run.Plan.Count)
                throw new InvalidOperationException($"Run '{run.RunId}' has no plan entry to splice at index {headerIndex}.");
            if (run.Plan.Count != run.Results.Count)
                throw new InvalidOperationException($"Run '{run.RunId}' cannot splice because its plan and results are not aligned.");

            var header = run.Plan[headerIndex];
            var step = header.Step;
            var call = step.Call;

            // R1
            if (call == null)
                throw new InvalidOperationException($"Step '{header.InstanceId}' is not a hand-off row: it has no 'call:' block.");

            // A loop template never hands off itself; its expanded iteration rows each get one call.
            if (step.ForEach != null && !header.IterationIndex.HasValue)
                throw new InvalidOperationException(
                    $"Step '{header.InstanceId}' must expand its forEach list before a call can be spliced for each iteration.");

            // R3 — the mirror of ExpandForEachAt's already-expanded guard.
            if (header.CallExpanded)
                throw new InvalidOperationException($"Step '{header.InstanceId}' was already spliced into a call frame.");

            // R4 — structure is fixed before values, and an insertion below the current index would move a row
            // the run is already standing on.
            var headerResult = run.Results[headerIndex];
            if (!string.Equals(headerResult.StepId, header.InstanceId, StringComparison.Ordinal))
                throw new InvalidOperationException($"Step '{header.InstanceId}' cannot splice because its result row is not aligned.");
            if (run.StepIndex > headerIndex)
                throw new InvalidOperationException(
                    $"Step '{header.InstanceId}' cannot splice because the run is already past it (the current plan index is {run.StepIndex}). A hand-off is spliced before its header runs, never under a moving cursor.");
            if (headerResult.Status != "pending" && headerResult.Status != "in_progress")
                throw new InvalidOperationException(
                    $"Step '{header.InstanceId}' cannot splice because it has already run: its result is '{headerResult.Status}'.");
            if (headerResult.Answers.Keys.Any(name => name != FrozenForEachSource(header)))
                throw new InvalidOperationException(
                    $"Step '{header.InstanceId}' cannot splice because it already holds answers.");
            if (headerResult.VerifyHistory.Count > 0)
                throw new InvalidOperationException(
                    $"Step '{header.InstanceId}' cannot splice because it already holds verify history.");

            // The header's own owner is READ, not assumed: at depth 2 and 3 the header is itself a callee row,
            // so an implementation that hardcodes run.Def here passes every one-level test and breaks silently.
            var headerOwner = run.RequireCoherentRow(header);
            if (!run.OwnerClosure.Contains(headerOwner))
                throw new InvalidOperationException(
                    $"Step '{header.InstanceId}' names workflow '{headerOwner.Name}' as its owner, which is not a member of this run's frozen owner closure, so no hand-off can be spliced under it.");

            // ---- O1 to O4, decided here, BEFORE R5, because every later refusal reads the owner's contents.
            var target = call.Workflow;
            // A Require miss is not a refusal: it means the plan holds a `call:` row for a name this run's own
            // frozen closure does not carry, which cannot happen after closure resolution and cannot be
            // repaired by refusing politely. It throws as the structural assert it is.
            var member = run.OwnerClosure.Require(target);
            var candidate = candidateOwner ?? member;

            // O1 — swapped target. The candidate is a legitimate frozen member, so a membership-only guard
            // admits it and the plan then runs a different workflow's steps under this header's frame.
            if (run.OwnerClosure.Contains(candidate)
                && !string.Equals(candidate.Name, target, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"Step '{header.InstanceId}' asks to hand off to '{target}', but it was handed the frozen closure member '{candidate.Name}'. That is a swapped target: the frame would name '{target}' while running '{candidate.Name}'s steps.");

            // O2 — duplicate name. Require answers from a name-keyed dictionary and can only return one member,
            // so a duplicate would be resolved silently by whichever entry won.
            var sameName = run.OwnerClosure.Definitions
                .Count(d => string.Equals(d.Name, target, StringComparison.OrdinalIgnoreCase));
            if (sameName > 1)
                throw new InvalidOperationException(
                    $"Step '{header.InstanceId}' cannot hand off to '{target}': this run's frozen owner closure holds that name {sameName} times, so no single definition owns the callee's rows.");

            // O3 — the freeze bypass. Every public value matches, which is exactly why a value comparison
            // cannot catch it and reference identity must.
            if (string.Equals(candidate.Name, target, StringComparison.OrdinalIgnoreCase)
                && !ReferenceEquals(candidate, member))
                throw new InvalidOperationException(
                    $"Step '{header.InstanceId}' was handed a definition named '{target}' that is not this run's frozen closure member for that name. The frozen member is the only admissible object: a same-named definition the caller still holds can be edited mid-run, and every public value would still match.");

            // O4 — foreign owner. It belongs to no closure, or to another run's: never validated, never
            // entitled, never snapshotted, and its gates never entered this run's entitlement decision.
            if (!run.OwnerClosure.Contains(candidate))
                throw new InvalidOperationException(
                    $"Step '{header.InstanceId}' was handed a definition for '{target}' that belongs to no frozen owner closure of this run (this run holds: {string.Join(", ", run.OwnerClosure.Definitions.Select(d => d.Name))}).");

            // R5 — the one case closure resolution does not cover. A missing, unreadable, template or disabled
            // callee is already refused at start, before the run object exists.
            var calleeSteps = member.Steps ?? Array.Empty<WorkflowStep>();
            if (calleeSteps.Length == 0)
                throw new InvalidOperationException(
                    $"Step '{header.InstanceId}' cannot hand off to '{target}': that workflow has no steps, so the hand-off would insert nothing and leave a frame that can never complete.");

            // ---- R6 and R7 share ONE walk of the live frame chain.
            var headerFrame = run.FrameAtPlanIndex(headerIndex);
            var chain = FrameChainNames(run, headerFrame);
            var chainWithTarget = string.Join(" -> ", chain.Concat(new[] { target }));
            if (chain.Any(n => string.Equals(n, target, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException(
                    $"Step '{header.InstanceId}' cannot hand off to '{target}': the hand-offs loop back on themselves: {chainWithTarget}. Remove one of them.");

            var depth = DepthOf(run, headerFrame) + 1;
            if (depth > CallSpec.MaxDepth)
                throw new InvalidOperationException(
                    $"Step '{header.InstanceId}' cannot hand off to '{target}': the hand-offs run {depth} deep: {chainWithTarget}. The limit is {CallSpec.MaxDepth}, counting this run as 1. Remove a hand-off, or fold one of them into the workflow above it.");

            ValidateCallFields(run, header.Step, member);

            // ---- R9, the structural asserts.
            var calleeIds = calleeSteps.Select(x => x.Id).ToArray();
            if (calleeIds.Distinct(StringComparer.Ordinal).Count() != calleeIds.Length)
                throw new InvalidOperationException(
                    $"Step '{header.InstanceId}' cannot hand off to '{target}': that frozen definition's step ids are not distinct, so its rows could not be addressed apart.");

            var frameId = CallFrameIdFor(header);
            if (run.Frames.Any(f => string.Equals(f.FrameId, frameId, StringComparison.Ordinal)))
                throw new InvalidOperationException(
                    $"Step '{header.InstanceId}' cannot hand off to '{target}': frame id '{frameId}' already exists.");

            var existingIds = new HashSet<string>(run.Plan.Select(p => p.InstanceId), StringComparer.Ordinal);
            var rows = new List<(string InstanceId, WorkflowStep Step)>(calleeSteps.Length);
            foreach (var calleeStep in calleeSteps)
            {
                var instanceId = $"{header.InstanceId}/{calleeStep.Id}";
                if (!existingIds.Add(instanceId))
                    throw new InvalidOperationException(
                        $"Step '{header.InstanceId}' cannot hand off to '{target}': planned instance id '{instanceId}' already exists.");
                // T-1588's rule applied at MINT time. Deriving the right owner and then minting rows from a
                // differently-sourced step array reproduces O3 one level down, and reproduces it invisibly,
                // because the owner would be right and only the steps would be stale.
                if (!member.Steps.Any(x => ReferenceEquals(x, calleeStep)))
                    throw new InvalidOperationException(
                        $"Step '{header.InstanceId}' cannot hand off to '{target}': a minted row's step is not one of that frozen owner's own steps by reference.");
                rows.Add((instanceId, calleeStep));
            }

            return new CallSplice(member, depth, frameId, header.InstanceId, rows);
        }

        /// <summary>Part two, the ONE mutating transition, and the authority boundary for a hand-off. The
        /// resolve that produced <paramref name="resolved"/> happened EARLIER and is only a snapshot: between
        /// the two calls the cursor can move, the header can run or collect answers and verify history, a frame
        /// can appear, and the blueprint itself is a value the caller holds. Spot-checking a few of the
        /// invariants the commit relies on is what let a stale or edited blueprint mutate a run that R4 requires
        /// it to refuse.
        ///
        /// So the commit does not carry its own copy of the rule list. It RE-RESOLVES against the live run and
        /// commits only what that resolution says, refusing when the blueprint it was handed is no longer that
        /// answer. <see cref="ResolveCallSplice"/> is pure and decides every refusal before any mutation, so the
        /// refusal-before-mutation contract is unchanged and there is exactly ONE place a rule can be written.
        ///
        /// Build every object first, then commit. The order is <see cref="ExpandForEachAt"/>'s and for the same
        /// reason. Insertion is strictly AFTER the header, so no row at or below it moves and no captured
        /// submission index is invalidated.</summary>
        internal static void SpliceCallAt(WorkflowRunState run, int headerIndex, CallSplice resolved)
        {
            if (run == null) throw new ArgumentNullException(nameof(run));
            if (resolved == null) throw new ArgumentNullException(nameof(resolved));
            // The one precondition that is the COMMIT's own rather than the resolve's. Asking a finished run
            // what it WOULD splice is a legitimate pure question; mutating it is not.
            if (run.Status != "active")
                throw new InvalidOperationException($"Run '{run.RunId}' is {run.Status}: its plan cannot splice.");
            if (headerIndex < 0 || headerIndex >= run.Plan.Count)
                throw new InvalidOperationException($"Run '{run.RunId}' has no plan entry to splice at index {headerIndex}.");

            // Row identity FIRST, before re-resolution, so a plan that moved is diagnosed as the row this index
            // actually holds rather than as whatever refusal that unrelated row happens to earn. This is the
            // expectedTargetStepId discipline the loop splice already carries at :412-415.
            var header = run.Plan[headerIndex];
            if (!string.Equals(resolved.HeaderInstanceId, header.InstanceId, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"Step '{resolved.HeaderInstanceId}' cannot splice because plan index {headerIndex} is '{header.InstanceId}'.");

            // Every R1-R9 and O1-O4 invariant, re-decided against the LIVE run: R4's cursor, header status,
            // answers and verify history included, and R9's frozen owner-step reference check included. The
            // blueprint's own owner goes in as the candidate, so an owner swapped for another closure member,
            // an unfrozen same-named twin or a foreign definition is refused by the same four diagnoses that
            // guard the resolve, and a null owner is refused as a mismatch below.
            var current = ResolveCallSplice(run, headerIndex, resolved.Owner);

            // ...and the blueprint must still BE that answer. Committing the fresh resolution over a blueprint
            // that disagrees with it would silently splice a different hand-off from the one the caller decided
            // on, which is the same defect wearing the opposite sign.
            if (!ReferenceEquals(current.Owner, resolved.Owner)
                || current.Depth != resolved.Depth
                || !string.Equals(current.FrameId, resolved.FrameId, StringComparison.Ordinal)
                || current.Rows.Count != resolved.Rows.Count
                || Enumerable.Range(0, current.Rows.Count).Any(i =>
                    !string.Equals(current.Rows[i].InstanceId, resolved.Rows[i].InstanceId, StringComparison.Ordinal)
                    || !ReferenceEquals(current.Rows[i].Step, resolved.Rows[i].Step)))
                throw new InvalidOperationException(StaleCallSpliceRefusal(header, resolved, current));

            // Structure exists before any answers. With-values commit through their own set-once carrier
            // only after the header passes, so the immutable frame seed never needs to be rewritten.
            var frame = RunFrame.CreateCall(current.FrameId, header.InstanceId, current.Owner.Name, current.Depth,
                Array.Empty<string>(), Array.Empty<string>(), "in_progress",
                answerSeed: null, parentFrameId: header.FrameId, requestedReturns: header.Step.Call.Returns);

            var planned = new List<PlannedStep>(current.Rows.Count);
            var results = new List<StepResult>(current.Rows.Count);
            foreach (var row in current.Rows)
            {
                planned.Add(new PlannedStep(row.Step, row.InstanceId, current.FrameId, null, current.Owner));
                results.Add(new StepResult { StepId = row.InstanceId, Title = row.Step.Title, Status = "pending" });
            }

            // Commit. No status change on any existing row, no Advance, no answers written anywhere, and no
            // owner rewritten on a row this splice did not mint: the header keeps its own.
            run.Plan.InsertRange(headerIndex + 1, planned);
            run.Results.InsertRange(headerIndex + 1, results);
            run.Frames.Add(frame);
            run.Plan[headerIndex] = new PlannedStep(header.Step, header.InstanceId, header.FrameId,
                header.IterationIndex, header.Owner, header.ForEachExpanded, callExpanded: true);
        }

        /// <summary>Expand answer-independent call structure before a run becomes visible to either door.</summary>
        internal static void InitializeCalls(WorkflowRunState run)
        {
            if (run.StepIndex != 0 || run.Results.Any(r => r.Answers.Count != 0 || r.VerifyHistory.Count != 0))
                throw new InvalidOperationException("Call initialization must happen before a workflow collects answers.");
            ValidateClosure(run.Def, new List<string> { run.Def.Name });
            ExpandAvailableCalls(run, 0);

            void ValidateClosure(WorkflowDef owner, List<string> path)
            {
                foreach (var step in owner.Steps.Where(s => s.Call != null))
                {
                    var callee = run.OwnerClosure.Require(step.Call.Workflow);
                    if (path.Contains(callee.Name, StringComparer.OrdinalIgnoreCase) || path.Count >= CallSpec.MaxDepth)
                        throw new InvalidOperationException($"Call chain {string.Join(" -> ", path.Append(callee.Name))} cycles or exceeds the depth limit {CallSpec.MaxDepth}.");
                    if (callee.Steps.Length == 0)
                        throw new InvalidOperationException($"Call target '{callee.Name}' has no steps.");
                    ValidateCallFields(run, step, callee);
                    ValidateClosure(callee, path.Append(callee.Name).ToList());
                }
            }
        }

        private static void ValidateCallFields(WorkflowRunState run, WorkflowStep header, WorkflowDef callee)
        {
            var inputs = callee.Steps.SelectMany(s => s.Gate?.Inputs ?? Array.Empty<GateInput>())
                .Select(i => i.Name).ToHashSet(StringComparer.Ordinal);
            foreach (var name in header.Call.With.Keys)
                if (!inputs.Contains(name))
                    throw new InvalidOperationException($"Step '{header.Id}' with: names '{name}', which is not a declared input of '{callee.Name}'.");
            foreach (var value in header.Call.With.Values)
            {
                var loop = LoopInstructionRef.Match(value ?? "");
                if (loop.Success && loop.Index == 0 && loop.Length == value.Length
                    && (header.ForEach == null || (loop.Groups[1].Value != header.ForEach.As && loop.Groups[1].Value != "index")))
                    throw new InvalidOperationException($"Step '{header.Id}' with: reads loop variable '{loop.Groups[1].Value}', which this step does not bind.");
            }
            var available = WorkflowParser.CallerInputNames(callee, run.OwnerClosure.Definitions.ToArray());
            foreach (var name in header.Call.Returns)
                if (!available.Contains(name))
                    throw new InvalidOperationException($"Step '{header.Id}' returns: names '{name}', which '{callee.Name}' cannot return.");
        }

        private static void ExpandAvailableCalls(WorkflowRunState run, int startIndex)
        {
            for (var i = startIndex; i < run.Plan.Count; i++)
            {
                var row = run.Plan[i];
                if (row.Step.Call == null || row.CallExpanded
                    || (row.Step.ForEach != null && !row.IterationIndex.HasValue)) continue;
                SpliceCallAt(run, i, ResolveCallSplice(run, i));
            }
        }

        /// <summary>One diagnosis for a blueprint the live run no longer resolves to. It names the FIRST
        /// difference rather than every field: a stale or edited blueprint almost always differs in one way, and
        /// a list of five comparisons buries the one that matters.</summary>
        private static string StaleCallSpliceRefusal(PlannedStep header, CallSplice resolved, CallSplice current)
        {
            string difference;
            if (!ReferenceEquals(current.Owner, resolved.Owner))
                difference = $"it carries owner '{resolved.Owner?.Name ?? "(none)"}' and this run resolves the frozen member '{current.Owner.Name}'";
            else if (current.Depth != resolved.Depth)
                difference = $"it was resolved at depth {resolved.Depth} and this run now resolves depth {current.Depth}";
            else if (!string.Equals(current.FrameId, resolved.FrameId, StringComparison.Ordinal))
                difference = $"it names frame id '{resolved.FrameId}' and this run now resolves '{current.FrameId}'";
            else if (current.Rows.Count != resolved.Rows.Count)
                difference = $"it carries {resolved.Rows.Count} callee row(s) and this run now resolves {current.Rows.Count}";
            else
            {
                var i = 0;
                while (i < current.Rows.Count
                    && string.Equals(current.Rows[i].InstanceId, resolved.Rows[i].InstanceId, StringComparison.Ordinal)
                    && ReferenceEquals(current.Rows[i].Step, resolved.Rows[i].Step)) i++;
                difference = $"its callee row {i} is '{resolved.Rows[i].InstanceId}' over step '{resolved.Rows[i].Step?.Id ?? "(none)"}', and this run resolves '{current.Rows[i].InstanceId}' over that frozen owner's own step '{current.Rows[i].Step.Id}'";
            }
            return $"Step '{header.InstanceId}' cannot splice because the resolved hand-off is no longer what this run resolves: {difference}. A hand-off is committed only as the live run resolves it, so resolve it again.";
        }

        /// <summary>Mark a hand-off that did NOT happen, and say where the cursor should resume. One helper,
        /// two callers, one difference: the skip path ASSIGNS the returned index, the not_applicable path
        /// discards it and lets the existing walk-past clause carry the cursor one row at a time.</summary>
        private static int DisposeCallFrame(WorkflowRunState run, int headerIndex, string status, string note)
        {
            var header = run.Plan[headerIndex];
            var callFrameId = CallFrameIdFor(header);
            var rows = CallFrameRowIndexes(run, callFrameId).Where(i => i > headerIndex).ToList();

            foreach (var i in rows)
            {
                var result = run.Results[i];
                result.Status = status;
                // The consequence is the DISPOSED row's own, not the header's: a hard-gated callee row is what
                // drives the run to OVERRIDDEN, and a note taken from the header would report the caller's
                // gate strength for a decision made about the callee's.
                result.Note = note + (status == "skipped" ? SkipConsequence(run, run.Plan[i]) : null);
                // The certificate's skipped projection reads this field, and a null there prints as an absent
                // grade rather than the grade that was set aside. The resolution is the DISPOSED row's own.
                result.EffectiveStrictness = run.Plan[i].Step.Gate == null ? null
                    : EffectiveStrictness(run, run.Plan[i]);
            }

            var verdict = status == "skipped" ? "failed" : "passed";
            foreach (var frame in run.Frames)
            {
                if (frame.ProjectionKind == null) continue;
                if (!FrameIsOrDescendsFrom(run, frame, callFrameId)) continue;
                if (!string.Equals(frame.State, "in_progress", StringComparison.Ordinal)) continue;
                frame.Complete(verdict);
            }

            return rows.Count == 0 ? headerIndex + 1 : rows.Max() + 1;
        }

        /// <summary>A call frame completes when the cursor LEAVES it, once, from one site. It is judged over
        /// the frame AND every frame descended from it, so a loop expanding inside a hand-off cannot complete
        /// the enclosing frame early. `failed` if any of those rows is skipped, otherwise `passed`:
        /// a failed row keeps the cursor on itself and stays retryable, so the cursor never leaves a frame
        /// holding one, and `not_applicable` rows are outside the applicable population.</summary>
        private static void CompleteExitedCallFrames(WorkflowRunState run)
        {
            var pending = new Dictionary<string, (int LastIndex, bool Skipped)>(StringComparer.Ordinal);
            foreach (var frame in run.Frames)
                if (frame.ProjectionKind != null && frame.State == "in_progress")
                    pending.TryAdd(frame.FrameId, (-1, false));
            if (pending.Count == 0) return;

            // One snapshot per advance. Future iterations start in_progress too; searching the entire plan
            // separately for each one multiplied full frame-list searches by both row and frame counts.
            var framesById = new Dictionary<string, RunFrame>(StringComparer.Ordinal);
            foreach (var frame in run.Frames)
                if (!framesById.TryAdd(frame.FrameId, frame))
                    throw new InvalidOperationException($"Run '{run.RunId}' names duplicate frame '{frame.FrameId}'.");
            for (var i = 0; i < run.Plan.Count && i < run.Results.Count; i++)
            {
                var planned = run.Plan[i];
                if (!framesById.TryGetValue(planned.FrameId, out var frame))
                    throw new InvalidOperationException($"Planned step '{planned.InstanceId}' names unknown frame '{planned.FrameId}'.");
                var skipped = run.Results[i].Status == "skipped";
                for (var hops = 0; frame != null; hops++)
                {
                    if (hops > run.Frames.Count)
                        throw new InvalidOperationException(
                            $"Frame '{planned.FrameId}' has a parent chain longer than this run's {run.Frames.Count} frames, so it does not terminate.");
                    if (pending.TryGetValue(frame.FrameId, out var exit))
                        pending[frame.FrameId] = (i, exit.Skipped || skipped);
                    frame = frame.ParentFrameId == null ? null : framesById.GetValueOrDefault(frame.ParentFrameId);
                }
            }

            // Children return before their parent resolves its own declared returns.
            foreach (var frame in run.Frames.AsEnumerable().Reverse())
            {
                if (!pending.TryGetValue(frame.FrameId, out var exit) || exit.LastIndex < 0) continue;
                // When the run finishes StepIndex == Plan.Count, which is greater than every row index, so this
                // ONE predicate covers both an ordinary advance out of the frame and the end of the run.
                if (run.StepIndex <= exit.LastIndex) continue;
                if (frame.ProjectionKind == "call" && frame.CallInputsCommitted)
                {
                    var returned = new Dictionary<string, WorkflowAnswerSource>(StringComparer.Ordinal);
                    if (frame.ReturnNames.Length > 0)
                    {
                        var answers = ResolveAnswerSources(run, frame, exit.LastIndex);
                        foreach (var (name, ordinal) in frame.ReturnNames.Select((name, ordinal) => (name, ordinal)))
                            if (answers.TryGetValue(name, out var answer) && answer.Answer != null
                                && (answer.Answer.Value != null || answer.Answer.Declined))
                                returned[name] = answer with { PlanIndex = exit.LastIndex, DeclarationOrder = ordinal };
                    }
                    frame.PublishReturns(returned, exit.LastIndex);
                    if (frame.ReturnNames.Length > 0 && returned.Count == 0)
                    {
                        var callee = run.OwnerClosure.Definitions.FirstOrDefault(d =>
                            string.Equals(d.Name, frame.Workflow, StringComparison.OrdinalIgnoreCase));
                        var strict = callee != null && run.CallStrictnessOverride.TryGetValue(callee.Name, out var over) && !string.IsNullOrWhiteSpace(over)
                            ? over
                            : callee == null ? null
                            : EffectiveStrictness(callee, null, run.OwnerClosure.SettingsStrictness(callee), run.GlobalStrictness);
                        frame.ReturnNote = string.Equals(strict, "off", StringComparison.Ordinal)
                            ? "Handed back: nothing. Its gate was off."
                            : "Handed back: nothing.";
                    }
                }
                frame.Complete(exit.Skipped ? "failed" : "passed");
            }
        }

        /// <summary>Skip the current step. A reason is REQUIRED — the accountable-override shape:
        /// never a hard wall, never a silent bypass.</summary>
        public static void SkipStep(WorkflowRunState run, string stepId, string reason)
        {
            var current = RequireCurrentStep(run, stepId, "skip");
            var step = current.Planned.Step;
            if (string.IsNullOrWhiteSpace(reason))
                throw new InvalidOperationException($"Skipping step '{step.Id}' requires a reason: it is recorded on the run, not a formality.");
            var result = run.Results[current.PlanIndex];
            result.Status = "skipped";
            result.Note = "skipped: " + reason.Trim() + SkipConsequence(run, current.Planned);
            if (result.PendingForEach != null) result.PendingForEach.State = "abandoned";
            CompleteIterationFrame(current, "failed");
            // [T220] A skipped hand-off takes its whole call frame with it, or the run executes a hand-off it
            // decided not to make. The cursor must be ASSIGNED, not advanced: Advance moves exactly one row and
            // AdvancePastInapplicableSteps has no clause for `skipped`, so a plain advance would leave the run
            // standing on a row it has already recorded as skipped, inside a frame it decided not to enter, and
            // the condition walk would then overwrite the disposal note with a condition note.
            if (current.Planned.CallExpanded)
            {
                var following = DisposeCallFrame(run, current.PlanIndex,
                    "skipped", $"skipped: the hand-off on '{current.Planned.InstanceId}' was skipped.");
                run.StepIndex = following;
                if (run.StepIndex < run.Plan.Count && run.Results[run.StepIndex].Status == "pending")
                    run.Results[run.StepIndex].Status = "in_progress";
                AdvancePastInapplicableSteps(run);
                return;
            }
            Advance(run);
        }

        public static void Abort(WorkflowRunState run, string reason)
        {
            if (run.Status != "active") throw new InvalidOperationException($"Run '{run.RunId}' is already {run.Status}.");
            run.Status = "aborted";
            run.AbortReason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
            run.FinishedUtc = DateTime.UtcNow.ToString("o");
            // Leave the in-progress step as it was. Resetting it to pending made an aborted run look
            // as if that step had never started.
        }

        // ---- gate internals ------------------------------------------------------------------

        /// <summary>Answers visible in one frame through one plan cutoff. Resolution starts from the frozen seed,
        /// then overlays same-frame rows latest-wins. Run-scoped answers may cross iteration boundaries, but never
        /// cross a call boundary only through declared bindings and returns.</summary>
        private static Dictionary<string, AnswerValue> AccumulatedAnswers(WorkflowRunState run)
        {
            var (frame, cutoff) = run.AnswerResolutionContext();
            return AccumulatedAnswers(run, frame, cutoff);
        }

        private static Dictionary<string, AnswerValue> AccumulatedAnswers(
            WorkflowRunState run, RunFrame frame, int cutoff) =>
            OverlayAnswers(run, frame, cutoff, overrideIndex: -1, overrideAnswers: null);

        /// <summary>What AccumulatedAnswers would return at <paramref name="targetIndex"/> if the row at
        /// <paramref name="overrideIndex"/> already held <paramref name="overrideAnswers"/>. Substitution,
        /// not a union: a stale answer on that row does not survive.</summary>
        private static Dictionary<string, AnswerValue> ProjectedAnswersAt(
            WorkflowRunState run, int targetIndex, int overrideIndex,
            IReadOnlyDictionary<string, AnswerValue> overrideAnswers) =>
            OverlayAnswers(run, run.FrameAtPlanIndex(targetIndex), targetIndex, overrideIndex, overrideAnswers);

        private static Dictionary<string, AnswerValue> OverlayAnswers(
            WorkflowRunState run, RunFrame frame, int cutoff, int overrideIndex,
            IReadOnlyDictionary<string, AnswerValue> overrideAnswers)
            => ResolveAnswerSources(run, frame, cutoff, overrideIndex, overrideAnswers)
                .ToDictionary(p => p.Key, p => RunFrame.CloneAnswer(p.Value.Answer), StringComparer.Ordinal);

        internal static IReadOnlyDictionary<string, WorkflowAnswerSource> VisibleAnswerSources(WorkflowRunState run)
        {
            var (frame, index) = run.AnswerResolutionContext();
            return ResolveAnswerSources(run, frame, index);
        }

        private static Dictionary<string, WorkflowAnswerSource> ResolveAnswerSources(
            WorkflowRunState run, RunFrame frame, int cutoff, int overrideIndex = -1,
            IReadOnlyDictionary<string, AnswerValue> overrideAnswers = null)
        {
            var all = new Dictionary<string, WorkflowAnswerSource>(StringComparer.Ordinal);
            frame.CopyInitialSourcesTo(all);
            var returns = run.Frames.Where(f => f.ReturnSources?.Count > 0 && f.ParentFrameId != null
                    && !CrossesCallBoundary(run, frame, FrameById(run, f.ParentFrameId)))
                .Select(f => (Frame: f, Index: f.ReturnPlanIndex
                    ?? throw new InvalidOperationException($"Call frame '{f.FrameId}' returned answers without an exit position.")))
                .GroupBy(x => x.Index).ToDictionary(g => g.Key, g => g.Select(x => x.Frame).ToArray());
            for (var i = 0; i <= cutoff && i < run.Results.Count && i < run.Plan.Count; i++)
            {
                var sameFrame = string.Equals(run.Plan[i].FrameId, frame.FrameId, StringComparison.Ordinal);
                if (sameFrame || !CrossesCallBoundary(run, frame, run.FrameAtPlanIndex(i)))
                {
                    var rowAnswers = i == overrideIndex ? overrideAnswers : run.Results[i].Answers;
                    if (rowAnswers?.Count > 0) run.RequireCoherentRow(run.Plan[i]);
                    foreach (var (input, ordinal) in (run.Plan[i].Step.Gate?.Inputs ?? Array.Empty<GateInput>())
                        .Select((input, ordinal) => (input, ordinal)))
                    {
                        if (rowAnswers == null || !rowAnswers.TryGetValue(input.Name, out var answer)
                            || (!sameFrame && input.Scope != "run")) continue;
                        all.Remove(input.Name);
                        all[input.Name] = new WorkflowAnswerSource(RunFrame.CloneAnswer(answer), input, i, ordinal);
                    }
                    // Old fixtures and records may carry an answer without a gate declaration. Keep its
                    // value visible within its frame, but never invent a type or cross-frame permission.
                    if (sameFrame && rowAnswers != null)
                        foreach (var answer in rowAnswers.Where(p => DeclaredGateInput(run.Plan[i].Step, p.Key) == null))
                        {
                            all.Remove(answer.Key);
                            all[answer.Key] = new WorkflowAnswerSource(RunFrame.CloneAnswer(answer.Value), null, i);
                        }
                }
                if (returns.TryGetValue(i, out var completed))
                    foreach (var call in completed)
                        foreach (var answer in call.ReturnSources)
                        {
                            // A declared return enters the invocation's parent namespace. Only a run-scoped
                            // declaration can then escape that parent's iteration, never its call boundary.
                            if (call.ParentFrameId != frame.FrameId && answer.Value.Declaration?.Scope != "run") continue;
                            all.Remove(answer.Key);
                            all[answer.Key] = answer.Value.DeepCopy();
                        }
            }
            return all;
        }

        // Iterations inherit the enclosing call's closed namespace. Run-scoped answers can cross
        // sibling iterations inside that call, but cannot escape through a nested iteration frame.
        internal static bool CrossesCallBoundary(WorkflowRunState run, RunFrame left, RunFrame right)
        {
            if (ReferenceEquals(left, right)) return false;
            return !ReferenceEquals(NearestCallFrame(run, left), NearestCallFrame(run, right));
        }

        private static RunFrame NearestCallFrame(WorkflowRunState run, RunFrame frame)
        {
            var current = frame;
            for (var hops = 0; hops <= run.Frames.Count; hops++)
            {
                if (current.ProjectionKind == "call") return current;
                if (current.ParentFrameId == null) return null;
                current = FrameById(run, current.ParentFrameId)
                    ?? throw new InvalidOperationException($"Frame '{current.FrameId}' names a parent this run does not hold.");
            }
            throw new InvalidOperationException($"Frame '{frame.FrameId}' has a parent chain that does not terminate.");
        }

        private static bool IsRunScoped(WorkflowStep step, string name) =>
            (step.Gate?.Inputs ?? Array.Empty<GateInput>()).Any(input =>
                string.Equals(input.Name, name, StringComparison.Ordinal)
                && string.Equals(input.Scope, "run", StringComparison.Ordinal));

        private static string FrozenForEachSource(PlannedStep planned) =>
            planned.IterationIndex.HasValue ? SelfDeclaredForEachSource(planned.Step) : null;

        private static string SelfDeclaredForEachSource(WorkflowStep step) => SelfDeclaredForEachInput(step)?.Name;

        /// <summary>The template's own gate declaration of its forEach list source, or null when the source is
        /// inline, named on an earlier step, or absent. The declaration, not just the name, because the
        /// preparation submission has to honour that input's own <c>required:</c> rule.</summary>
        private static GateInput SelfDeclaredForEachInput(WorkflowStep step)
        {
            if (string.IsNullOrWhiteSpace(step.ForEach?.InInput)) return null;
            var source = step.ForEach.InInput;
            return (step.Gate?.Inputs ?? Array.Empty<GateInput>()).FirstOrDefault(input =>
                string.Equals(input.Name, source, StringComparison.Ordinal));
        }

        private static HashSet<string> DeclaredGateInputNames(WorkflowStep step) =>
            new HashSet<string>((step.Gate?.Inputs ?? Array.Empty<GateInput>()).Select(i => i.Name), StringComparer.Ordinal);

        private static GateInput DeclaredGateInput(WorkflowStep step, string name) =>
            string.IsNullOrWhiteSpace(name) ? null
            : (step.Gate?.Inputs ?? Array.Empty<GateInput>()).FirstOrDefault(input =>
                string.Equals(input.Name, name, StringComparison.Ordinal));

        /// <summary>Why a loop source that exists somewhere on the run is nevertheless not in the loop's own
        /// projection. <see cref="CallBoundary"/> exists so this case has to be decided on purpose rather than
        /// folded into <see cref="ScopeOnly"/> by writing the test as "a different frame".</summary>
        private enum ForEachSourceExclusion { None, ScopeOnly, CallBoundary, NotAnswered }

        private readonly struct VisibleForEachSource
        {
            public VisibleForEachSource(
                AnswerValue answer, GateInput owner, int answeringPlanIndex, ForEachSourceExclusion excluded)
            {
                Answer = answer;
                Owner = owner;
                AnsweringPlanIndex = answeringPlanIndex;
                Excluded = excluded;
            }

            public AnswerValue Answer { get; }
            public GateInput Owner { get; }
            public int AnsweringPlanIndex { get; }
            public ForEachSourceExclusion Excluded { get; }
        }

        /// <summary>The ONE backwards read this unit adds, serving BOTH blank ownership and the gap path's scope
        /// branch so the two can never disagree about which answer is the winner. It mirrors
        /// <see cref="OverlayAnswers"/>'s reverse visibility walk exactly, applying the call-frame test and the
        /// `scope: run` test in the same order, and reads only the window that projection already walks: the
        /// loop's own index and backwards. It can refuse no row but the loop's own.
        /// <para>When an answer is visible it returns the nearest VISIBLE answering row that supplied the winning
        /// value plus that row's own declaration. When none is, it reports WHY, which is what lets the gap path
        /// pick its sentence: <c>ScopeOnly</c> is the one case that earns the scope refusal; <c>CallBoundary</c>
        /// is reported as missing, because `scope: run` cannot reach across one and suggesting it would be advice
        /// that does not work; <c>NotAnswered</c> is the plain gap. A value that reaches the projection only
        /// through the frozen seed has no answering row, so it reports <c>NotAnswered</c> with a null owner and
        /// blank validation refuses rather than guessing at a declaration.</para></summary>
        private static VisibleForEachSource ResolveVisibleForEachSource(
            WorkflowRunState run, int loopIndex, string source)
        {
            var frame = run.FrameAtPlanIndex(loopIndex);
            var resolved = ResolveAnswerSources(run, frame, loopIndex);
            if (resolved.TryGetValue(source, out var visible) && visible.Declaration != null)
                return new VisibleForEachSource(visible.Answer, visible.Declaration, visible.PlanIndex,
                    ForEachSourceExclusion.None);
            var nearestExclusion = ForEachSourceExclusion.NotAnswered;
            var nearestExcludedIndex = -1;
            var cutoff = Math.Min(loopIndex, Math.Min(run.Results.Count, run.Plan.Count) - 1);
            for (var i = cutoff; i >= 0; i--)
            {
                if (!run.Results[i].Answers.TryGetValue(source, out var candidate)) continue;
                var sameFrame = string.Equals(run.Plan[i].FrameId, frame.FrameId, StringComparison.Ordinal);
                // The call-frame test runs FIRST and unconditionally, exactly as OverlayAnswers runs it: an
                // answer inside a call frame, or read from inside one, is invisible no matter what its
                // declaration says, so `scope: run` would change nothing about it.
                if (!sameFrame && CrossesCallBoundary(run, frame, run.FrameAtPlanIndex(i)))
                {
                    if (nearestExcludedIndex < 0)
                    {
                        nearestExclusion = ForEachSourceExclusion.CallBoundary;
                        nearestExcludedIndex = i;
                    }
                    continue;
                }
                if (!sameFrame && !IsRunScoped(run.Plan[i].Step, source))
                {
                    if (nearestExcludedIndex < 0)
                    {
                        nearestExclusion = ForEachSourceExclusion.ScopeOnly;
                        nearestExcludedIndex = i;
                    }
                    continue;
                }
                // Keep walking past excluded rows rather than reporting the first one found: an excluded nearer
                // row does not veto a visible farther one, and reporting it would refuse about an answer the
                // projection never selected.
                // [T220] The winning declaration is read through the ANSWERING row's own frozen owner, not
                // through the run's root: coherence proves this row's step really was authored by the
                // definition that owns the row, so `run.Plan[i].Step` is that owner's declaration and a mixed
                // or foreign row refuses here, before any mutation. Note the naming hazard: `Owner` on the
                // struct below is a GateInput, not a workflow.
                run.RequireCoherentRow(run.Plan[i]);
                return new VisibleForEachSource(
                    candidate, DeclaredGateInput(run.Plan[i].Step, source), i, ForEachSourceExclusion.None);
            }
            return new VisibleForEachSource(null, null, nearestExcludedIndex, nearestExclusion);
        }

        /// <summary>The step id the gap refusal names, and MESSAGE COPY only: it decides nothing and is never used
        /// for ownership. It searches STRICTLY PRECEDING definition rows, nearest first. Strictly preceding is the
        /// whole legal set, because the parser accepts a declarer on this step or any earlier one
        /// (<c>WorkflowParser.cs:932-942</c>) and this shape has already excluded its own gate by construction. A
        /// definition-wide search could name a step AFTER the loop, and "answer it on the step that declares it
        /// before this loop expands" is advice that cannot be followed on a later declarer, because the list is
        /// read when the loop's own row is reached. Nearest-preceding rather than first, because that is the
        /// declaration an agent should go back to when several rows declare the same name. When no preceding row
        /// declares the source the clause is omitted and the rest of the sentence stands alone.</summary>
        private static string DeclaringStepIdFor(WorkflowRunState run, PlannedStep loopRow, string sourceName)
        {
            // [T220] The CURRENT ROW's own frozen owner, so a callee row names a callee declarer rather than a
            // same-named caller step. Still message copy only: it never chooses the winning declaration.
            var steps = run.RowOwner(loopRow).Steps;
            var loopDefIndex = Array.IndexOf(steps, loopRow.Step);
            // A loop whose step is not in this definition has no preceding row here to name honestly.
            if (loopDefIndex < 0) return null;
            return steps.Take(loopDefIndex)
                .LastOrDefault(s => DeclaredGateInput(s, sourceName) != null)?.Id;
        }

        private static Dictionary<string, AnswerValue> BuildCommittedAnswers(
            WorkflowRunState run, CoherentCurrentStep current, IReadOnlyDictionary<string, AnswerValue> answers)
        {
            var declared = DeclaredGateInputNames(current.Planned.Step);
            var filtered = (answers ?? (IReadOnlyDictionary<string, AnswerValue>)new Dictionary<string, AnswerValue>())
                .Where(kv => declared.Contains(kv.Key))
                .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
            var committed = new Dictionary<string, AnswerValue>(StringComparer.Ordinal);
            // A call can supply a question without a second user submission. Record the actual values
            // this gate uses on its own result row so its evidence never outlives an invisible binding.
            if (run.OwnerClosure.Definitions.Any(d => d.Steps.Any(s => s.Call != null)))
            {
                var visible = ResolveAnswerSources(run, current.Frame, current.PlanIndex);
                foreach (var name in declared)
                    if (visible.TryGetValue(name, out var source) && source.Answer != null)
                        committed[name] = RunFrame.CloneAnswer(source.Answer);
            }
            var frozenSource = FrozenForEachSource(current.Planned);
            var result = run.Results[current.PlanIndex];
            if (frozenSource != null && result.Answers.TryGetValue(frozenSource, out var frozenAnswer))
                committed[frozenSource] = RunFrame.CloneAnswer(frozenAnswer);
            foreach (var answer in filtered)
                committed[answer.Key] = RunFrame.CloneAnswer(answer.Value);
            return committed;
        }

        private static void ApplyPendingForEach(WorkflowRunState run, CoherentCurrentStep current, StepResult result)
        {
            var decision = result.PendingForEach;
            if (decision == null) return;
            if (decision.Outcome != "condition_false")
                ExpandForEachAt(run, decision.TargetIndex, decision.Values, null, decision.TargetStepId);
            decision.State = "applied";
        }

        private static PendingForEachExpansion ResolveDeferredForEachDecision(
            WorkflowRunState run, CoherentCurrentStep current, Dictionary<string, AnswerValue> committedAnswers)
        {
            if (IsExpansionOnlyRow(current.Planned)) return null;
            var target = current.PlanIndex + 1;
            if (target >= run.Plan.Count) return null;
            var targetPlanned = run.Plan[target];
            if (!IsUnexpandedDeferredForEachRow(targetPlanned)) return null;
            // A callee's last row did not declare the caller's visible source. Let the caller's
            // explicit loop setup resolve its own answer and declaration after the hand-off returns.
            if (CrossesCallBoundary(run, current.Frame, run.FrameAtPlanIndex(target))) return null;
            var sourceName = targetPlanned.Step.ForEach.InInput;
            if (DeclaredGateInput(current.Planned.Step, sourceName) == null) return null;

            var projection = ProjectedAnswersAt(run, target, current.PlanIndex, committedAnswers);
            projection.TryGetValue(sourceName, out var answer);
            if (answer != null && answer.Declined)
                throw new InvalidOperationException(
                    $"Step '{targetPlanned.InstanceId}' cannot resolve its forEach list: input '{sourceName}' was declined "
                    + $"({(string.IsNullOrWhiteSpace(answer.DeclineReason) ? "no reason recorded" : answer.DeclineReason.Trim())}). "
                    + "A decline names nothing to repeat over, which is not the same claim as an empty list. Answer it with the "
                    + "list, or skip_workflow_step with a reason (the skip is recorded)."
                    + SkipConsequence(run, current.Planned));
            if (answer == null || answer.Value == null)
            {
                var declaration = DeclaredGateInput(current.Planned.Step, sourceName);
                throw new InvalidOperationException(
                    $"Step '{targetPlanned.InstanceId}' cannot resolve its forEach list: input '{sourceName}' is unanswered in this frame. "
                    + "Answer it on the step that declares it before this loop expands. An answered blank list is an empty loop; "
                    + "an unanswered one is a gap, and guessing at it would fabricate the coverage the loop then certifies. "
                    + $"Ask the user: \"{declaration.Question}\" skip_workflow_step with a reason (the skip is recorded)."
                    + SkipConsequence(run, current.Planned));
            }

            var values = SplitInputBackedForEachValues(answer.Value);
            var declarationRule = DeclaredGateInput(current.Planned.Step, sourceName);
            if (values.Count == 0 && declarationRule.Required != "optional")
                throw new InvalidOperationException(
                    $"Step '{targetPlanned.InstanceId}' cannot expand: its forEach list source '{sourceName}' is answered blank, "
                    + "and a blank answer to a non-optional input is the same gap the input gate refuses, not an empty list. "
                    + $"Ask the user: \"{declarationRule.Question}\" "
                    + (declarationRule.Required == "required"
                        ? "(required: it cannot be declined)"
                        : "(answer it, or decline explicitly with {\"declined\": true, \"reason\": \"...\"})")
                    + " Only a source declared `required: optional` may spell an empty loop as a blank answer. "
                    + "skip_workflow_step with a reason (the skip is recorded)."
                    + SkipConsequence(run, current.Planned));
            if (values.Count > targetPlanned.Step.ForEach.MaxIterations)
                throw new InvalidOperationException(
                    $"Step '{targetPlanned.InstanceId}' resolved {values.Count} forEach values, above maxIterations {targetPlanned.Step.ForEach.MaxIterations}. "
                    + "The list was not truncated and the step remains current. skip_workflow_step with a reason (the skip is recorded)."
                    + SkipConsequence(run, current.Planned));

            var copy = FreshForEachValueCopy(values);
            var outcome = copy.Length == 0 ? "empty" : "expand";
            var when = targetPlanned.Step.When;
            if (!string.IsNullOrWhiteSpace(when))
            {
                var expr = WorkflowPredicate.Parse(when, out var parseError);
                var loopDependent = expr != null
                    && WorkflowPredicate.ReferencedRoots(expr).Contains("loop", StringComparer.Ordinal);
                if (!loopDependent)
                {
                    var sourceFacts = run.FrameFacts ?? new PredicateFacts();
                    var facts = new PredicateFacts
                    {
                        Model = sourceFacts.Model,
                        Connection = sourceFacts.Connection,
                        Git = sourceFacts.Git,
                        Session = sourceFacts.Session,
                        Date = sourceFacts.Date,
                        Inputs = projection,
                        LoopIndex = null,
                        LoopValues = null,
                    };
                    if (expr == null || !WorkflowPredicate.Evaluate(expr, facts))
                        outcome = "condition_false";
                }
            }

            return new PendingForEachExpansion
            {
                TargetStepId = targetPlanned.InstanceId,
                TargetIndex = target,
                SourceInput = sourceName,
                Values = copy,
                Outcome = outcome,
                State = "pending",
            };
        }

        /// <summary>The input gate. Every non-optional input must be answered or explicitly declined.
        /// `required: required` may not be declined. The exception carries agent words on Message and person
        /// words on DisplayMessage so each door can render its own line.</summary>
        private static void EnforceInputs(WorkflowStep step, Dictionary<string, AnswerValue> answers)
        {
            var problems = new List<GateProblem>();
            foreach (var input in step.Gate.Inputs)
            {
                answers.TryGetValue(input.Name, out var a);
                var missing = a == null || (!a.Declined && string.IsNullOrWhiteSpace(a.Value));
                if (input.Required == "optional") continue;
                if (missing)
                    problems.Add(new GateProblem
                    {
                        Input = input.Name, Kind = GateCopy.Unanswered,
                        AgentMessage = GateCopy.AgentUnanswered(input),
                        DisplayMessage = GateCopy.DisplayUnanswered(),
                    });
                else if (a.Declined && input.Required == "required")
                    problems.Add(new GateProblem
                    {
                        Input = input.Name, Kind = GateCopy.DeclinedButRequired,
                        AgentMessage = GateCopy.AgentDeclinedButRequired(input),
                        DisplayMessage = GateCopy.DisplayDeclinedButRequired(),
                    });
                else if (a.Declined && string.IsNullOrWhiteSpace(a.DeclineReason))
                    problems.Add(new GateProblem
                    {
                        Input = input.Name, Kind = GateCopy.DeclinedWithoutReason,
                        AgentMessage = GateCopy.AgentDeclinedWithoutReason(input),
                        DisplayMessage = GateCopy.DisplayDeclinedWithoutReason(),
                    });
            }
            if (problems.Count > 0)
                throw GateCopy.Refuse(step.Id, problems);
        }

        /// <summary>Definition-gated policies on answered machine inputs. Seeds without the attribute do not enter
        /// this path, preserving legacy acceptance. no-bare-measures uses the same token scanner and measure
        /// inventory as DAX compilation, so comments, strings, and qualified columns do not create false refusals.</summary>
        private static void EnforceInputPolicies(WorkflowStep step, Dictionary<string, AnswerValue> answers,
            IReadOnlyCollection<string> modelMeasureNames = null,
            IReadOnlyCollection<(string Table, string Name, bool IsCalculated)> modelColumns = null,
            IReadOnlyCollection<string> modelFunctionNames = null,
            IReadOnlyCollection<(string Name, bool IsCalculated)> modelTables = null)
        {
            foreach (var input in step.Gate.Inputs.Where(i => i.DaxPurity == "no-bare-measures"))
            {
                if (!answers.TryGetValue(input.Name, out var answer) || !answer.Answered) continue;
                var functions = DaxBench.ModelFunctionReferences(answer.Value, modelFunctionNames);
                if (functions.Length != 0)
                    throw new InvalidOperationException(
                        $"Step '{step.Id}' rejected: input '{input.Name}' witness must use base columns and contain no model-defined function calls. "
                        + (functions.Length == 1
                            ? $"Inline this function's logic instead: {functions[0]}."
                            : $"Inline these functions' logic instead: {string.Join(", ", functions)}."));
                var calculatedTables = DaxBench.CalculatedTableReferences(answer.Value, modelTables);
                if (calculatedTables.Length != 0)
                    throw new InvalidOperationException(
                        $"Step '{step.Id}' rejected: input '{input.Name}' witness must use base tables. "
                        + (calculatedTables.Length == 1
                            ? $"Rebuild this calculated table's row logic inline from base tables: {calculatedTables[0]}."
                            : $"Rebuild these calculated tables' row logic inline from base tables: {string.Join(", ", calculatedTables)}."));
                var calculatedColumns = DaxBench.CalculatedColumnReferences(answer.Value, modelMeasureNames,
                    modelColumns, out var qualifyBareReference);
                if (calculatedColumns.Length != 0)
                {
                    var named = string.Join(", ", calculatedColumns);
                    throw new InvalidOperationException(
                        $"Step '{step.Id}' rejected: input '{input.Name}' witness must use base columns. "
                        + (calculatedColumns.Length == 1
                            ? $"Inline this column's logic instead: {named}."
                            : $"Inline these columns' logic instead: {named}.")
                        + (qualifyBareReference
                            ? " The bare name also matches a base column. Qualify the intended base-column reference if that is what you meant."
                            : ""));
                }
                var measures = DaxBench.MeasureReferences(answer.Value, modelMeasureNames, modelColumns);
                if (measures.Length == 0) continue;
                throw new InvalidOperationException(
                    $"Step '{step.Id}' rejected: input '{input.Name}' must contain zero measure references. "
                    + "Rebuild the witness semantics from qualified base-column references. "
                    + "Measure references found: " + string.Join(", ", measures.Select(DaxBench.BracketName)) + ".");
            }
        }

        // Token-lean one-liner for a blocking verify: prefer the Missing note (unavailable), else the detail.
        private static string Describe(VerifyResult v) =>
            !string.IsNullOrWhiteSpace(v.Missing) ? $"missing {v.Missing}: {v.Detail}" : v.Detail;

        /// <summary>Content equality between an archived attempt's results and the CURRENT verify field — the
        /// terminal record suppresses a one-item history only when it truly repeats the current evidence.</summary>
        private static bool SameEvidence(VerifyResult[] a, VerifyResult[] b)
        {
            if (a.Length != b.Length) return false;
            for (var i = 0; i < a.Length; i++)
                if (a[i].Kind != b[i].Kind || a[i].Status != b[i].Status || a[i].Detail != b[i].Detail || a[i].Missing != b[i].Missing)
                    return false;
            return true;
        }

        /// <summary>The run-wide accumulated answers (steps in order, later same-name answers win), exposed so the
        /// engine can record witness locks (E3a) from the same view the gate evaluated against.</summary>
        public static IReadOnlyDictionary<string, AnswerValue> AllAnswers(WorkflowRunState run) => AccumulatedAnswers(run);

        /// <summary>[T220] Effective strictness for ONE planned row, resolved through the frozen definition that
        /// authored it and that owner's own per-workflow setting. This is the row form of the four-argument
        /// resolution above and the only one execution should use: DECISION 1.7 unrolls a callee into the
        /// caller's one plan, so handing the run's root to every row reads the caller's frontmatter and the
        /// caller's settings for a step the caller did not write.</summary>
        private static string EffectiveStrictness(WorkflowRunState run, PlannedStep planned)
        {
            var owner = run.RowOwner(planned);
            if (owner != null && !string.IsNullOrEmpty(owner.Name)
                && run.CallStrictnessOverride.TryGetValue(owner.Name, out var over)
                && !string.IsNullOrWhiteSpace(over))
                return over;
            return EffectiveStrictness(owner, planned.Step?.Gate, run.OwnerClosure.SettingsStrictness(owner), run.GlobalStrictness);
        }

        private static bool IsHardStep(WorkflowRunState run, PlannedStep planned) =>
            planned?.Step?.Gate != null
            && string.Equals(EffectiveStrictness(run, planned), "hard", StringComparison.Ordinal);

        private static bool IsHardVerifyStep(WorkflowRunState run, PlannedStep planned) =>
            (planned?.Step?.Gate?.Verify?.Length ?? 0) > 0 && IsHardStep(run, planned);

        /// <summary>Whether this run has a certificate at all. The ONE place <c>CoverageSurface != null</c> is
        /// allowed to decide record shape: the certificate is a run-level artifact folded from every frame, so a
        /// surface anywhere means there is a certificate to describe and to override. For a run with no loops and
        /// no calls this is literally <c>Frames[0].CoverageSurface != null</c>, so nothing moves. Do NOT reuse it
        /// to filter the contributing set — a surfaceless frame still holds anchor revisions, form repairs, ledger
        /// disputes and countersigns, and filtering it out silently drops them.</summary>
        private static bool AnyFrameHasCoverageSurface(WorkflowRunState run) =>
            run != null && run.Frames.Any(f => f.CoverageSurface != null);

        // SkipConsequence is called from eight sites, some inside a submission scope and some outside it. Reading
        // the run-wide predicate rather than the ambient frame is what stops the same run printing or omitting the
        // OVERRIDDEN sentence depending on which door asked.
        private static string SkipConsequence(WorkflowRunState run, PlannedStep planned) =>
            AnyFrameHasCoverageSurface(run) && IsHardStep(run, planned)
                ? " " + OverriddenCertificateConsequence
                : "";

        private static string NormalizeCertificateClaim(string claim)
        {
            var text = (claim ?? "").Trim();
            foreach (var level in new[] { "OVERRIDDEN", "PARTIAL", "FULL" })
                if (text.Equals(level, StringComparison.OrdinalIgnoreCase)
                    // Any non-alphanumeric boundary counts: agents naturally write "FULL." or "PARTIAL,"
                    // and a period must not demote an honest claim to OVERRIDDEN (caught live: the pilot's
                    // "FULL. All four..." normalized to the weakest rung). "FULLY..." still falls through.
                    || (text.StartsWith(level, StringComparison.OrdinalIgnoreCase)
                        && text.Length > level.Length
                        && !char.IsLetterOrDigit(text[level.Length])))
                    return level;
            // An unparseable or absent claim cannot safely lift the certificate. It lands on the weakest rung.
            return "OVERRIDDEN";
        }

        internal static string MinimumCertificateLevel(string agentClaim, string computed)
        {
            int Rank(string level) => string.Equals(level, "FULL", StringComparison.Ordinal) ? 2
                : string.Equals(level, "PARTIAL", StringComparison.Ordinal) ? 1 : 0;
            var claimLevel = NormalizeCertificateClaim(agentClaim);
            var computedLevel = NormalizeCertificateClaim(computed);
            return Rank(claimLevel) <= Rank(computedLevel) ? claimLevel : computedLevel;
        }

        /// <summary>The plan rows this frame owns. Every PlannedStep carries a FrameId and the loop splice removes
        /// the template row before inserting the iteration rows, so these sets are a genuine partition of the plan:
        /// no row is counted twice and none is dropped. A frame may own none — a workflow that is one forEach: step
        /// leaves the top frame with zero rows, and a frame that proved nothing asserts nothing.</summary>
        private static int[] FrameRowIndexes(WorkflowRunState run, RunFrame frame)
        {
            var rows = new List<int>();
            for (var i = 0; i < run.Plan.Count && i < run.Results.Count; i++)
                if (string.Equals(run.Plan[i].FrameId, frame.FrameId, StringComparison.Ordinal)) rows.Add(i);
            return rows.ToArray();
        }

        /// <summary>One frame's open disputes, ShapeId then Coordinate, cloned. The run-level list is these lists
        /// concatenated in frame order: two rows at the same coordinate with different values are two true
        /// observations of two different iterations, not a contradiction, so the fold copies and never reconciles.</summary>
        private static ShapeMismatchLedgerCell[] OpenDisputedCells(RunFrame frame) => frame.ShapeMismatchLedger.Values
            .Where(x => x.Open && string.Equals(x.State, "DISPUTED", StringComparison.Ordinal))
            .OrderBy(x => x.ShapeId, StringComparer.Ordinal)
            .SelectMany(x => (x.Cells ?? Array.Empty<ShapeMismatchLedgerCell>())
                .OrderBy(c => c.Coordinate, StringComparer.Ordinal))
            .Select(x => x.Clone())
            .ToArray();

        private static string WeakerCertificateLevel(string a, string b)
        {
            int Rank(string level) => string.Equals(level, "FULL", StringComparison.Ordinal) ? 2
                : string.Equals(level, "PARTIAL", StringComparison.Ordinal) ? 1 : 0;
            return Rank(a) <= Rank(b) ? a : b;
        }

        /// <summary>This frame's hard-row half. hardSkipped is an OR and allHardPassed an AND over a partition of
        /// the plan, so folding per frame and taking the minimum equals the old run-wide answer term for term. What
        /// the split buys is an honest per-frame entry: a frame whose own spliced hard row failed reads OVERRIDDEN
        /// in its own entry instead of FULL beside a run that says OVERRIDDEN.</summary>
        private static string FrameRowStatus(WorkflowRunState run, int[] rows)
        {
            var hardSkipped = rows.Any(i => run.Results[i].Status == "skipped" && IsHardStep(run, run.Plan[i]));
            var allHardPassed = rows
                // A false authored condition means the obligation never arose. It is outside the certificate
                // population, not a passed gate and not an accountable skip.
                .Where(i => run.Results[i].Status != "not_applicable" && IsHardVerifyStep(run, run.Plan[i]))
                .All(i => run.Results[i].Status == "passed");
            return hardSkipped || !allHardPassed ? "OVERRIDDEN" : "FULL";
        }

        /// <summary>This frame's proof half. The null guard is load-bearing: the run-wide existence gate no longer
        /// proves any particular frame has a surface, and a surfaceless frame is in the contributing set.</summary>
        private static string FrameProofState(RunFrame frame, ShapeMismatchLedgerCell[] disputed) =>
            (frame.CoverageSurface?.CurrentOpenGrains.Length ?? 0) > 0
            || frame.ShapeMismatchCountersigns.Count > 0
            || disputed.Length > 0 ? "PARTIAL" : "FULL";

        /// <summary>The locked lattice of one surface, empty when there is none. Empty is the honest answer and the
        /// point of the whole design: the two unconditional grand_total and cross rows would otherwise print two
        /// anchored grains nobody proved.</summary>
        private static CertificateGrainCoverage[] BuildGrainCoverage(CoverageSurfaceLock surface)
        {
            if (surface == null) return Array.Empty<CertificateGrainCoverage>();
            var open = new HashSet<string>(surface.CurrentOpenGrains, StringComparer.Ordinal);
            var grains = new List<string> { "grand_total" };
            grains.AddRange(surface.CurrentGrid.Select(x => "axis:" + x));
            grains.Add("cross");
            return grains.Distinct(StringComparer.Ordinal).Select(shape =>
            {
                var isOpen = open.Contains(shape);
                return new CertificateGrainCoverage
                {
                    ShapeId = shape,
                    State = isOpen ? "open" : "anchored",
                    Anchored = !isOpen,
                    Open = isOpen,
                };
            }).ToArray();
        }

        /// <summary>The agent's claim, read frame-pinned and scope-blind: the latest TOP-frame row that carries the
        /// name at all wins. Eligibility is <c>Plan[i].FrameId == Frames[0].FrameId</c> and nothing else, so a call
        /// frame's closed namespace can neither supply nor replace the run's claim, and neither can an iteration
        /// frame's row however its input was scoped. Keeping the last row that carries the name rather than the last
        /// ANSWERED one is deliberate: a later decline is a withdrawal, and an unclaimed run belongs on the weakest
        /// rung. Returns null when no top-frame row carries it, which NormalizeCertificateClaim reads as OVERRIDDEN.</summary>
        private static AnswerValue TopFrameCertificateClaim(WorkflowRunState run)
        {
            var topFrameId = run.Frames[0].FrameId;
            AnswerValue claim = null;
            for (var i = 0; i < run.Plan.Count && i < run.Results.Count; i++)
            {
                if (!string.Equals(run.Plan[i].FrameId, topFrameId, StringComparison.Ordinal)) continue;
                if (run.Results[i].Answers.TryGetValue("certificate", out var value)) claim = value;
            }
            return claim;
        }

        private static CertificateFrame BuildCertificateFrame(
            WorkflowRunState run, RunFrame frame, int[] rows, string level, ShapeMismatchLedgerCell[] disputed)
        {
            var entry = new CertificateFrame
            {
                Kind = frame.ProjectionKind,
                StepId = frame.StepId,
                State = frame.State,
                Level = level,
                // Fresh deep clones taken at compute time. The certificate is STORED on the run and cloned into
                // every projection, so it must not hold live StepResult references the way a throwaway view may.
                Steps = rows.Select(i => run.Results[i].Clone()).ToArray(),
                GridColumns = frame.CoverageSurface?.CurrentGrid.ToArray() ?? Array.Empty<string>(),
                Coverage = BuildGrainCoverage(frame.CoverageSurface),
                OpenGrains = frame.CoverageSurface?.CurrentOpenGrains.ToArray() ?? Array.Empty<string>(),
                CountersignedCells = frame.ShapeMismatchCountersigns.Select(x => x.Clone()).ToArray(),
                DisputedCells = disputed.Select(x => x.Clone()).ToArray(),
                AnchorRevisionCount = frame.AnchorRevisions.Count,
                FormRepairCount = frame.AnchorFormRepairCount,
                CoverageSurfaceRevisions = frame.CoverageSurfaceRevisions.Select(x => x.Clone()).ToArray(),
            };
            if (string.Equals(frame.ProjectionKind, "iteration", StringComparison.Ordinal))
            {
                entry.IterationIndex = frame.IterationIndex;
                entry.LoopVariable = frame.LoopVariable;
                entry.LoopValue = frame.LoopValue;
            }
            else if (string.Equals(frame.ProjectionKind, "call", StringComparison.Ordinal))
            {
                entry.Workflow = frame.Workflow;
                entry.Depth = frame.Depth;
                entry.Passed = frame.Passed.ToArray();
                entry.Returned = frame.Returned.ToArray();
            }
            else
            {
                throw new InvalidOperationException($"Unknown workflow frame projection kind '{frame.ProjectionKind}'.");
            }
            return entry;
        }

        /// <summary>The per-frame certificate fold (DECISION 2.3.1 / 2.3.2). Reads every frame explicitly and no
        /// ambient compatibility accessor, so the answer no longer depends on which frame happened to submit last.
        /// The contributing set is every frame with no filter; only the run-level lattice is the top frame's own.</summary>
        internal static WorkflowCertificate ComputeCertificate(WorkflowRunState run)
        {
            if (!AnyFrameHasCoverageSurface(run)) return null;

            var topFrame = run.Frames[0];
            string computed = null;
            var frameEntries = new List<CertificateFrame>();
            var countersigned = new List<ShapeMismatchCountersign>();
            var disputedCells = new List<ShapeMismatchLedgerCell>();
            var surfaceRevisions = new List<CoverageSurfaceRevision>();
            var anchorRevisionCount = 0;
            var formRepairCount = 0;

            foreach (var frame in run.Frames)
            {
                var rows = FrameRowIndexes(run, frame);
                var disputed = OpenDisputedCells(frame);
                var level = WeakerCertificateLevel(FrameRowStatus(run, rows), FrameProofState(frame, disputed));
                computed = computed == null ? level : WeakerCertificateLevel(computed, level);

                // Counts and ordered receipts read every frame, surfaceless ones included: expected_values and a
                // non-coverage-backed dax_equivalence can write revisions, repairs, disputes and countersigns on
                // a frame with no surface while another frame satisfies the existence gate. A filtered sum drops
                // exactly that evidence.
                countersigned.AddRange(frame.ShapeMismatchCountersigns.Select(x => x.Clone()));
                disputedCells.AddRange(disputed.Select(x => x.Clone()));
                surfaceRevisions.AddRange(frame.CoverageSurfaceRevisions.Select(x => x.Clone()));
                anchorRevisionCount += frame.AnchorRevisions.Count;
                formRepairCount += frame.AnchorFormRepairCount;

                // The top frame has no projection kind, so it is never listed: its body IS the run-level body.
                if (frame.ProjectionKind == null) continue;
                frameEntries.Add(BuildCertificateFrame(run, frame, rows, level, disputed));
            }

            var claimAnswer = TopFrameCertificateClaim(run);
            var agentClaim = claimAnswer?.Answered == true ? claimAnswer.Value : null;
            var claimLevel = NormalizeCertificateClaim(agentClaim);
            var coverage = BuildGrainCoverage(topFrame.CoverageSurface);

            var iterationFrames = run.Frames
                .Where(f => string.Equals(f.ProjectionKind, "iteration", StringComparison.Ordinal)).ToArray();
            // Fail-closed: an in_progress frame counts against the run rather than flattering the tally. Every
            // iteration frame is terminal at completion, so this branch guards a silent overcount of passes.
            var failedIterations = iterationFrames
                .Where(f => !string.Equals(f.State, "passed", StringComparison.Ordinal)).ToArray();
            var failedValues = failedIterations.Select(f => f.LoopValue)
                .Take(EquivalenceGate.MaxStoredMismatchCoordinatesPerShape).ToArray();

            var skipped = Enumerable.Range(0, run.Plan.Count)
                .Where(i => run.Results[i].Status == "skipped")
                .Select(i =>
                {
                    var note = run.Results[i].Note ?? "";
                    var reason = note.StartsWith("skipped: ", StringComparison.Ordinal)
                        ? note.Substring("skipped: ".Length) : note;
                    var suffix = " " + OverriddenCertificateConsequence;
                    if (reason.EndsWith(suffix, StringComparison.Ordinal))
                        reason = reason.Substring(0, reason.Length - suffix.Length);
                    return new CertificateSkippedStep
                    {
                        StepId = run.Results[i].StepId,
                        Title = run.Results[i].Title,
                        Reason = reason,
                        EffectiveStrictness = run.Plan[i].Step.Gate == null ? null
                            : EffectiveStrictness(run, run.Plan[i]),
                    };
                }).ToArray();

            return new WorkflowCertificate
            {
                Level = MinimumCertificateLevel(agentClaim, computed),
                AgentClaim = agentClaim,
                ClaimLevel = claimLevel,
                ComputedLevel = computed,
                // The lattice stays with the frame that proved it (C6). Unioning iteration 1's ['Product'[Category]]
                // with iteration 2's ['Date'[Year]] would report an axis anchored that no single proof evaluated,
                // and a certificate that reads stronger than any proof it summarizes is the thing this forbids.
                GridColumns = topFrame.CoverageSurface?.CurrentGrid.ToArray() ?? Array.Empty<string>(),
                Coverage = coverage,
                OpenGrains = topFrame.CoverageSurface?.CurrentOpenGrains.ToArray() ?? Array.Empty<string>(),
                CountersignedCells = countersigned.ToArray(),
                DisputedCells = disputedCells.ToArray(),
                AnchorRevisionCount = anchorRevisionCount,
                FormRepairCount = formRepairCount,
                CoverageSurfaceRevisions = surfaceRevisions.ToArray(),
                // Already walks the whole plan, which includes every spliced iteration row.
                SkippedSteps = skipped,
                Frames = frameEntries.Count == 0 ? null : frameEntries.ToArray(),
                IterationsTotal = iterationFrames.Length == 0 ? (int?)null : iterationFrames.Length,
                IterationsPassed = iterationFrames.Length == 0
                    ? (int?)null : iterationFrames.Length - failedIterations.Length,
                IterationsFailed = iterationFrames.Length == 0 ? (int?)null : failedIterations.Length,
                FailedIterationValues = failedValues.Length == 0 ? null : failedValues,
            };
        }

        internal static bool WhenHolds(string when, IReadOnlyDictionary<string, AnswerValue> answers)
        {
            if (string.IsNullOrWhiteSpace(when)) return true;
            // parser guarantees the form inputs.<name>.answered
            var name = when.Substring("inputs.".Length, when.Length - "inputs.".Length - ".answered".Length);
            return answers.TryGetValue(name, out var a) && a != null && a.Answered;
        }

        private static CoherentCurrentStep RequireCurrentStep(WorkflowRunState run, string stepId, string activity)
        {
            if (run.Status != "active")
                throw new InvalidOperationException($"Run '{run.RunId}' is {run.Status}: no step accepts submissions.");

            var planned = run.Plan[run.StepIndex];
            var current = ResolveCoherentCurrentStep(run, planned, activity);
            EnsureCurrentFrameInProgress(current, activity);
            var expectedId = planned.InstanceId;
            var idMatches = string.Equals(stepId, expectedId, StringComparison.Ordinal);
            var requiresExactId = current.IterationIndex.HasValue
                || !string.Equals(planned.InstanceId, planned.Step.Id, StringComparison.Ordinal);
            if ((requiresExactId && !idMatches)
                || (!string.IsNullOrWhiteSpace(stepId) && !idMatches))
                throw new InvalidOperationException(
                    $"Step '{stepId}' is not the current step: the run is on '{expectedId}' ({planned.Step.Title}). Steps advance in order.");
            return current;
        }

        /// <summary>The refusals that must land BEFORE an outer engine scope installs receipt bookkeeping, and
        /// they are exactly four: the ADDRESS (the run is active and this id is the current row), the current
        /// row's plan/result/frame COHERENCE and its iteration frame's in-progress STATE, a rewrite of a
        /// FROZEN forEach source, and R1 (an unusable deferred list on the declaring submission). The ownership
        /// gate is not counted: it refuses nothing. This is not every refusing request property, and an earlier
        /// revision claiming it was overstated the boundary. R1 qualifies because it is decided from the plan row
        /// and the submitted payload before any answer is stored and before any evidence is executed, so it is
        /// not an event the archive should see.
        /// <para>R2, a current unexpanded deferred-source loop, was counted here until that row gained a
        /// submission of its own (§1.6.1d). Its refusals now ride the expansion-only route instead: the row is an
        /// expansion-only row, so <c>LocalEngine.SubmitWorkflowStepAsync</c> never installs the receipt scope for
        /// it at all. That is STRONGER than landing before the scope, not a weakening, and it is the same
        /// guarantee the preparation and inline setup calls already rely on.</para>
        /// The submission-limit, input-gate, input-policy and verify refusals deliberately stay INSIDE normal
        /// receipt bookkeeping. The runner calls this same path before its own transition mutates the run.</summary>
        internal static string ValidateSubmission(
            WorkflowRunState run, string stepId, IReadOnlyDictionary<string, AnswerValue> answers) =>
            ValidateSubmissionCore(run, stepId, answers).Planned.InstanceId;

        private static CoherentCurrentStep ValidateSubmissionCore(
            WorkflowRunState run, string stepId, IReadOnlyDictionary<string, AnswerValue> answers)
        {
            var current = RequireCurrentStep(run, stepId, "submission");
            // [T220] A `call:` row nobody spliced is refused WHERE IT IS, decided from the current planned row
            // alone with no reference to the payload and no reference to any other row. Running it as though the
            // field were not there would silently drop work. Normal starts initialize every non-loop call;
            // loop setup initializes its iteration calls before returning an actionable row.
            if (current.Planned.Step.Call != null && !current.Planned.CallExpanded && !IsExpansionOnlyRow(current.Planned))
                throw new InvalidOperationException(
                    $"Step '{current.Planned.InstanceId}' is a hand-off to '{current.Planned.Step.Call.Workflow}', but its call structure has not been initialized. "
                    + "Nothing can be submitted against it. Use skip_workflow_step with a reason to record why the hand-off was set aside and move on.");
            var frozenSource = FrozenForEachSource(current.Planned);
            if (frozenSource != null && answers?.ContainsKey(frozenSource) == true)
                throw new InvalidOperationException(
                    $"Step '{current.Planned.InstanceId}' input '{frozenSource}' was fixed at expansion and cannot be submitted or declined as iteration data. Submit only the remaining iteration questions.");
            var committed = BuildCommittedAnswers(run, current, answers);
            var decision = ResolveDeferredForEachDecision(run, current, committed);
            return new CoherentCurrentStep
            {
                PlanIndex = current.PlanIndex,
                Planned = current.Planned,
                Frame = current.Frame,
                IterationIndex = current.IterationIndex,
                LoopVariable = current.LoopVariable,
                LoopValue = current.LoopValue,
                CommittedAnswers = committed,
                DeferredForEach = decision,
                CallInputs = current.Planned.CallExpanded ? ResolveCallInputs(run, current, committed) : null,
            };
        }

        private static Dictionary<string, WorkflowAnswerSource> ResolveCallInputs(
            WorkflowRunState run, CoherentCurrentStep current, IReadOnlyDictionary<string, AnswerValue> committed)
        {
            var target = FrameById(run, CallFrameIdFor(current.Planned));
            if (target.CallInputsCommitted)
                throw new InvalidOperationException($"Hand-off '{current.Planned.InstanceId}' already committed its inputs.");
            var available = ResolveAnswerSources(run, current.Frame, current.PlanIndex, current.PlanIndex, committed);
            var callee = run.OwnerClosure.Require(current.Planned.Step.Call.Workflow);
            var bound = new Dictionary<string, WorkflowAnswerSource>(StringComparer.Ordinal);
            foreach (var pair in current.Planned.Step.Call.With)
            {
                var value = pair.Value ?? "";
                AnswerValue answer;
                var inputName = value.StartsWith("inputs.", StringComparison.Ordinal) ? value.Substring(7) : null;
                var loop = LoopInstructionRef.Match(value);
                if (inputName != null && inputName.Length > 0 && inputName.All(c => char.IsLetterOrDigit(c) || c == '_' || c == '-'))
                {
                    if (!available.TryGetValue(inputName, out var source)
                        || source.Answer == null || (!source.Answer.Declined && source.Answer.Value == null))
                        throw new InvalidOperationException($"Hand-off '{current.Planned.InstanceId}' cannot bind '{pair.Key}': caller input '{inputName}' is unanswered. Answer it before submitting the hand-off, or skip_workflow_step with a reason.");
                    answer = RunFrame.CloneAnswer(source.Answer);
                }
                else if (loop.Success && loop.Index == 0 && loop.Length == value.Length)
                {
                    if (!current.IterationIndex.HasValue
                        || (loop.Groups[1].Value != current.LoopVariable && loop.Groups[1].Value != "index"))
                        throw new InvalidOperationException($"Hand-off '{current.Planned.InstanceId}' cannot bind '{pair.Key}': loop variable '{loop.Groups[1].Value}' is unavailable in this iteration.");
                    answer = new AnswerValue { Value = loop.Groups[1].Value == "index"
                        ? current.IterationIndex.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        : current.LoopValue };
                }
                else answer = new AnswerValue { Value = value };
                var declaration = callee.Steps.SelectMany(s => s.Gate?.Inputs ?? Array.Empty<GateInput>())
                    .Select((input, ordinal) => (Input: input, Ordinal: ordinal)).First(i => i.Input.Name == pair.Key);
                bound[pair.Key] = new WorkflowAnswerSource(answer, declaration.Input, current.PlanIndex, declaration.Ordinal);
            }
            return bound;
        }

        private static void Advance(WorkflowRunState run)
        {
            run.StepIndex++;
            if (run.StepIndex < run.Plan.Count && run.Results[run.StepIndex].Status == "pending")
                run.Results[run.StepIndex].Status = "in_progress";
            AdvancePastInapplicableSteps(run);
        }

        /// <summary>Evaluate each newly-current step condition through the shared predicate evaluator. False
        /// conditions are outside the run's applicable population, and consecutive false steps advance in one
        /// transition so neither door can observe one as actionable.</summary>
        public static void AdvancePastInapplicableSteps(WorkflowRunState run)
        {
            while (run.Status == "active" && run.StepIndex < run.Plan.Count)
            {
                // [T220] The cursor has just arrived at StepIndex, so any call frame it has now left is
                // finished. This sits at the TOP of the body rather than the bottom because the walk RETURNS
                // from inside the loop the moment it finds an actionable row: a call placed after the loop is
                // reached only when the run ends, which would leave every ordinary walk-out of a hand-off
                // in_progress. The post-loop call below still covers the cursor running off the end.
                CompleteExitedCallFrames(run);
                if (run.Results[run.StepIndex].Status == "not_applicable")
                {
                    run.StepIndex++;
                    if (run.StepIndex < run.Plan.Count && run.Results[run.StepIndex].Status == "pending")
                        run.Results[run.StepIndex].Status = "in_progress";
                    continue;
                }
                var planned = run.Plan[run.StepIndex];
                // Every condition on an UNEXPANDED forEach template waits for that template's explicit base-id
                // setup call, whether it is blank, loop-free, mixed, malformed or loop-dependent. Deciding one
                // here would decide it before the source has been validated, which is how a loop-free false or
                // unreadable condition could hide an unusable source, and how a loop-fact predicate could delete
                // its own loop before any iteration existed (ConditionFacts supplies null loop bindings on this
                // row by design). No early parse and no loop-root guard: the shape of the row is the whole test.
                // An expanded-empty row is unaffected; its result is already not_applicable and walked past above.
                if (planned.Step.ForEach != null && !planned.IterationIndex.HasValue && !planned.ForEachExpanded)
                    return;
                var when = planned.Step.When;
                if (string.IsNullOrWhiteSpace(when)) return;

                var current = ResolveCoherentCurrentStep(run, planned, "condition evaluation");
                EnsureCurrentFrameInProgress(current, "condition evaluation");
                var expr = WorkflowPredicate.Parse(when, out var parseError);
                var facts = ConditionFacts(run, current);
                if (expr != null && WorkflowPredicate.Evaluate(expr, facts)) return;

                RecordConditionNotApplicableAndAdvance(run, current, when, expr, parseError, facts);
            }

            // [T220] BEFORE the run-completion branch, not after. The fold reads frame state directly, both for
            // each frame entry's own record and for the iteration tally's fail-closed test, so a frame still
            // in_progress at that moment would ride the certificate as unfinished work in a completed run.
            if (run.Status == "active") CompleteExitedCallFrames(run);

            if (run.Status == "active" && run.StepIndex >= run.Plan.Count)
            {
                run.Status = "completed";
                run.FinishedUtc = DateTime.UtcNow.ToString("o");
                if (AnyFrameHasCoverageSurface(run)) run.Certificate = ComputeCertificate(run);
            }
        }

        /// <summary>The ONE spelling of a not_applicable condition outcome, shared by the advance walk and by both
        /// setup false/unreadable paths. It is shared rather than mirrored because a row marked here by setup and
        /// a row marked here by the walk are read by the same certificate population count, the same record
        /// builder and the same views: two spellings would drift in exactly the places hardest to see, including the note
        /// an operator reads, the strictness stamp on a gate-bearing row, and whether the frame was completed.
        /// <see cref="CompleteIterationFrame"/> is a no-op off an iteration row, so no shape branch is needed: on
        /// an unexpanded top-level template it completes nothing, and on an excluded iteration during the
        /// post-splice walk it completes that iteration's frame as passed.</summary>
        private static void RecordConditionNotApplicableAndAdvance(
            WorkflowRunState run, CoherentCurrentStep current, string when, PredicateExpr expr, string parseError,
            PredicateFacts facts)
        {
            var planned = current.Planned;
            var result = run.Results[run.StepIndex];
            result.Status = "not_applicable";
            result.EffectiveStrictness = planned.Step.Gate == null ? null
                : EffectiveStrictness(run, planned);
            if (expr == null)
                result.Note = $"did not apply: condition '{when}' was unreadable ({parseError}).";
            else
            {
                var values = string.Join(", ", expr.OrGroups.SelectMany(x => x)
                    .GroupBy(x => x.Left, StringComparer.Ordinal).Select(x => x.First())
                    .Select(x => $"{x.Left}={WorkflowPredicate.DescribeFact(x, facts)}"));
                result.Note = $"did not apply: condition '{when}' evaluated false (facts: {values}).";
            }
            CompleteIterationFrame(current, "passed");
            // [T220] Same obligation as the skip path, the other verdict. This path needs no cursor assignment:
            // the walk's ONE existing skip-past clause is exactly the `not_applicable` clause, so it carries the
            // cursor through the disposed block one row at a time. That asymmetry is the whole reason one path
            // needs new code and the other does not.
            if (planned.CallExpanded)
                DisposeCallFrame(run, run.StepIndex,
                    "not_applicable", $"did not apply: the hand-off on '{planned.InstanceId}' did not apply.");
            run.StepIndex++;
            if (run.StepIndex < run.Plan.Count && run.Results[run.StepIndex].Status == "pending")
                run.Results[run.StepIndex].Status = "in_progress";
        }

        private static PredicateFacts ConditionFacts(WorkflowRunState run, CoherentCurrentStep current)
        {
            IReadOnlyDictionary<string, string> loopValues = null;
            if (current.IterationIndex.HasValue)
                loopValues = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [current.LoopVariable] = current.LoopValue,
                };

            var source = run.FrameFacts ?? new PredicateFacts();
            return new PredicateFacts
            {
                Model = source.Model,
                Connection = source.Connection,
                Git = source.Git,
                Session = source.Session,
                Date = source.Date,
                Inputs = AccumulatedAnswers(run, current.Frame, current.PlanIndex),
                LoopIndex = current.IterationIndex,
                LoopValues = loopValues,
            };
        }

        private sealed class CoherentCurrentStep
        {
            public int PlanIndex { get; init; }
            public PlannedStep Planned { get; init; }
            public RunFrame Frame { get; init; }
            public int? IterationIndex { get; init; }
            public string LoopVariable { get; init; }
            public string LoopValue { get; init; }
            public PendingForEachExpansion DeferredForEach { get; init; }
            public Dictionary<string, AnswerValue> CommittedAnswers { get; init; }
            public Dictionary<string, WorkflowAnswerSource> CallInputs { get; init; }
        }

        /// <summary>The one current plan/result/frame coherence path used by conditions, instruction rendering,
        /// submission, and skip. An iteration binding is execution state, so no consumer may infer it from the
        /// authored step or caller-supplied facts. Every disagreement refuses before a consumer can act.</summary>
        private static CoherentCurrentStep ResolveCoherentCurrentStep(
            WorkflowRunState run, PlannedStep planned, string activity)
        {
            var index = run.StepIndex;
            if (index < 0 || index >= run.Plan.Count || index >= run.Results.Count
                || run.Plan.Count != run.Results.Count
                || !ReferenceEquals(run.Plan[index], planned)
                || !string.Equals(run.Results[index].StepId, planned.InstanceId, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"Step '{planned.InstanceId}' {activity} cannot continue because its plan and result row are not aligned.");

            // [T220][T-2443] Ownership coherence, before any control-field or gate refusal reads a frozen value
            // off this row. This gate covers exactly the three callers of this resolver -- submission and skip
            // through RequireCurrentStep, condition evaluation through AdvancePastInapplicableSteps, and
            // current-step instruction rendering in BuildView -- so on those paths a row whose owner cannot be
            // named, or whose step its owner did not author, refuses here rather than being executed against
            // some other workflow's frontmatter. It is NOT the single gate for every row-aware read: the other
            // row-owned readers resolve ownership themselves through WorkflowRunState.RowOwner, which refuses an
            // unnameable owner on its own (EffectiveStrictness above, and the whole-plan scan that calls
            // RequireCoherentRow per row). Do not read this as a claim that one call site guards them all.
            run.RequireCoherentRow(planned);

            // Resolve this once. The exact frame named by the plan owns both predicate facts and rendered values.
            var frame = run.FrameAtPlanIndex(index);
            if (planned.IterationIndex.HasValue)
            {
                var loopVariable = planned.Step.ForEach?.As;
                if (!string.Equals(frame.ProjectionKind, "iteration", StringComparison.Ordinal)
                    || frame.IterationIndex != planned.IterationIndex
                    || !string.Equals(frame.StepId, planned.Step.Id, StringComparison.Ordinal)
                    || string.IsNullOrWhiteSpace(loopVariable)
                    || !string.Equals(frame.LoopVariable, loopVariable, StringComparison.Ordinal)
                    || frame.LoopValue == null)
                    throw new InvalidOperationException(
                        $"Step '{planned.InstanceId}' {activity} cannot continue because its iteration plan and frame metadata are inconsistent.");

                return new CoherentCurrentStep
                {
                    PlanIndex = index,
                    Planned = planned,
                    Frame = frame,
                    IterationIndex = frame.IterationIndex,
                    LoopVariable = frame.LoopVariable,
                    LoopValue = frame.LoopValue,
                };
            }

            if (string.Equals(frame.ProjectionKind, "iteration", StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"Step '{planned.InstanceId}' {activity} cannot continue because an iteration frame has no planned iteration index.");

            return new CoherentCurrentStep { PlanIndex = index, Planned = planned, Frame = frame };
        }

        /// <summary>[T220] Widened from iteration frames to every projected frame. Under the completion rule a
        /// current row inside a finished CALL frame is unreachable, which is exactly why it should throw rather
        /// than proceed: it can only mean the plan and the frames disagree.</summary>
        private static void EnsureCurrentFrameInProgress(CoherentCurrentStep current, string activity)
        {
            if (current.IterationIndex.HasValue
                && !string.Equals(current.Frame.State, "in_progress", StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"Step '{current.Planned.InstanceId}' {activity} cannot continue because its iteration frame is '{current.Frame.State}', not in_progress.");
            if (string.Equals(current.Frame.ProjectionKind, "call", StringComparison.Ordinal)
                && !string.Equals(current.Frame.State, "in_progress", StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"Step '{current.Planned.InstanceId}' {activity} cannot continue because its call frame is '{current.Frame.State}', not in_progress.");
        }

        /// <summary>Finish only the exact iteration frame captured by the shared coherence path. Top-level and
        /// synthetic non-iteration rows deliberately leave their projected frame alone in this runner unit.</summary>
        private static void CompleteIterationFrame(CoherentCurrentStep current, string outcome)
        {
            if (current.IterationIndex.HasValue && !current.Planned.CallExpanded) current.Frame.Complete(outcome);
        }

        private static readonly Regex LoopInstructionRef = new Regex(
            WorkflowParser.LoopReferencePattern,
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static string RenderCurrentInstructions(string instructions, CoherentCurrentStep current,
            string instanceId)
        {
            // A non-iteration step is outside this bounded unit. Its bytes, including loop-shaped text, survive.
            if (!current.IterationIndex.HasValue || string.IsNullOrEmpty(instructions)) return instructions;

            foreach (Match match in LoopInstructionRef.Matches(instructions))
            {
                var name = match.Groups[1].Value;
                if (!string.Equals(name, "index", StringComparison.Ordinal)
                    && !string.Equals(name, current.LoopVariable, StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        $"Step '{instanceId}' cannot render its instructions: reference '[[loop.{name}]]' is not bound in this iteration. "
                        + $"Use '[[loop.index]]' or '[[loop.{current.LoopVariable}]]'.");
            }

            // MatchEvaluator returns the value literally. Regex performs one source pass, so '$', backslashes,
            // line breaks, and loop-shaped text inside a value are data, never replacement syntax or a second pass.
            return LoopInstructionRef.Replace(instructions, match =>
                string.Equals(match.Groups[1].Value, "index", StringComparison.Ordinal)
                    ? current.IterationIndex.Value.ToString(CultureInfo.InvariantCulture)
                    : current.LoopValue);
        }

        // ---- views ------------------------------------------------------------------------------

        /// <summary>A cloned point-in-time snapshot (Plan.cs BuildView discipline) — a later in-place
        /// mutation can't tear a serialized view. Carries the current step's instruction text: authored bytes
        /// for an ordinary row, the exact current frame's loop bindings for an iteration, or the synthetic
        /// setup phrase for an expansion-only unexpanded forEach template. That setup row is summarized on
        /// purpose: it advertises no body work because this call will not run it.</summary>
        public static WorkflowRunView BuildView(WorkflowRunState run)
        {
            var clonedSteps = run.Results.Select(r => r.Clone()).ToArray();
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
                TotalSteps = run.Plan.Count,
                TotalStepsProvisional = run.Plan.Any(p => p.Step.ForEach != null)
                    ? run.Plan.Any(p => p.Step.ForEach != null && !p.ForEachExpanded)
                    : (bool?)null,
                Steps = clonedSteps,
                Frames = BuildFrameViews(run, clonedSteps),
                // E3(a)/blocker-3 — the adjudication receipts (null when none, so the common run stays byte-for-byte lean).
                WitnessLocks = ProjectWitnessLocks(run.WitnessLocks),
                WitnessRevisions = ProjectWitnessRevisions(run.WitnessRevisions),
                PartitionRevisions = ProjectPartitionRevisions(run.PartitionRevisions),
                AnchorLocks = ProjectAnchorLocks(run.RunAnchorLocks),
                AnchorRevisions = ProjectAnchorRevisions(run.AnchorRevisions),
                ShapeMismatchLedger = run.ShapeMismatchLedger.Count == 0 ? null
                    : run.ShapeMismatchLedger.Values.OrderBy(x => x.ShapeId, StringComparer.Ordinal)
                        .Select(x => x.Clone()).ToArray(),
                ShapeMismatchCountersigns = run.ShapeMismatchCountersigns.Count == 0 ? null
                    : run.ShapeMismatchCountersigns.Select(x => x.Clone()).ToArray(),
                Certificate = run.Certificate?.Clone(),
            };
            // Learning Loop L3 distillable hint: only a completed run whose every step PASSED (no skip,
            // no fail) AND that produced at least one PASSED verify (real evidence, not an all-skipped/
            // offline run) is a repeatable recipe worth /distill-workflow.
            var allPassed = run.Status == "completed" && run.Results.All(r => r.Status == "passed");
            var realVerify = run.Results.SelectMany(r => r.VerifyResults).Any(v => v.Status == "passed");
            view.Distillable = allPassed && realVerify;
            view.DistillableWhy = view.Distillable
                ? $"'{run.Def.Name}' completed with all {run.Results.Count} steps passed and verified evidence: a repeatable recipe; run /distill-workflow to capture it."
                : null;
            var cur = run.CurrentStep;
            if (cur != null)
            {
                var planned = run.Plan[run.StepIndex];
                // Always enter the shared coherence path. A corrupt iteration that lost only its planned index
                // must not masquerade as an ordinary row and leak unrendered instructions through the view.
                var current = ResolveCoherentCurrentStep(run, planned, "instruction rendering");
                view.CurrentStep = ProjectCurrentStep(run, planned, current);
            }
            return view;
        }

        /// <summary>One projection of every CurrentStepView member for the current row. Expansion-only
        /// templates get the synthetic setup phrase and hide body questions, ops, verify kinds and strictness.
        /// After a nonempty expansion, #0 is a real iteration and authored metadata (after Unit 11 render)
        /// comes back. Ordinary rows and real iterations keep authored values; an unexpanded earlier-source
        /// template is one of the synthetic setup rows described above.</summary>
        private static CurrentStepView ProjectCurrentStep(
            WorkflowRunState run, PlannedStep planned, CoherentCurrentStep current)
        {
            var step = planned.Step;
            if (IsExpansionOnlyRow(planned))
            {
                return new CurrentStepView
                {
                    StepId = planned.InstanceId,
                    Title = step.Title,
                    Instructions = IsForEachPreparation(planned) ? PreparationSetupInstructions
                        : IsInlineForEachExpansion(planned) ? InlineSetupInstructions
                        : DeferredSetupInstructions,
                    Questions = CurrentQuestions(step.Gate, planned),
                    ProvidedAnswers = ProvidedAnswers(run, current, CurrentQuestions(step.Gate, planned)),
                    VerifyKinds = Array.Empty<string>(),
                    EffectiveStrictness = null,
                    Ops = Array.Empty<string>(),
                };
            }

            return new CurrentStepView
            {
                StepId = planned.InstanceId,
                Title = step.Title,
                Instructions = RenderCurrentInstructions(step.Instructions, current, planned.InstanceId),
                Questions = CurrentQuestions(step.Gate, planned),
                ProvidedAnswers = ProvidedAnswers(run, current, CurrentQuestions(step.Gate, planned)),
                VerifyKinds = step.Gate?.Verify.Select(v => v.Kind).ToArray() ?? Array.Empty<string>(),
                EffectiveStrictness = step.Gate == null ? null
                    : EffectiveStrictness(run, planned),
                Ops = step.Ops,
                HandOff = BuildHandOff(run, planned, step),
            };
        }

        private static HandOffWarning BuildHandOff(WorkflowRunState run, PlannedStep planned, WorkflowStep step)
        {
            if (step?.Call == null || (step.Call.Returns?.Length ?? 0) == 0) return null;
            var callee = run.OwnerClosure.Definitions.FirstOrDefault(d =>
                string.Equals(d.Name, step.Call.Workflow, StringComparison.OrdinalIgnoreCase));
            if (callee == null) return null;
            var strict = run.CallStrictnessOverride.TryGetValue(callee.Name, out var over) && !string.IsNullOrWhiteSpace(over)
                ? over
                : EffectiveStrictness(callee, null, run.OwnerClosure.SettingsStrictness(callee), run.GlobalStrictness);
            if (!string.Equals(strict, "off", StringComparison.Ordinal)) return null;
            var next = run.Plan.Skip(run.StepIndex + 1).Select(p => p.Step).FirstOrDefault(s => s != null && s.Call == null);
            return new HandOffWarning
            {
                Callee = callee.Name,
                CalleeTitle = string.IsNullOrWhiteSpace(callee.Title) ? callee.Name : callee.Title,
                ExpectedReturns = step.Call.Returns,
                NextStepTitle = next?.Title,
                GateOff = true,
            };
        }

        /// <summary>The questions the CURRENT row can actually accept, in authored order.
        /// <list type="bullet">
        /// <item>An unexpanded self-sourced forEach template takes exactly one PREPARATION submission (§1.6.1a),
        /// which answers its list source and REFUSES every other name.</item>
        /// <item>An unexpanded inline forEach template takes a no-answer expansion (§1.6.1b), so it shows no
        /// questions. Each authored input becomes visible on the iteration that owns it.</item>
        /// <item>An unexpanded template whose source was declared on an EARLIER step takes a no-answer expansion
        /// too (§1.6.1d), so it also shows none. Showing the source question would advertise a field that call's
        /// first refusal rejects; falling through to the frozen filter below would return the loop's FULL
        /// authored gate, which is the wrong answer twice over.</item>
        /// <item>An iteration row hides only the source frozen at expansion, because that answer is fixed and a
        /// re-submission of it is refused.</item>
        /// </list>
        /// An expanded non-iteration row and ordinary rows keep the full authored gate.</summary>
        private static GateInput[] CurrentQuestions(GateSpec gate, PlannedStep planned)
        {
            var inputs = gate?.Inputs ?? Array.Empty<GateInput>();
            if (IsInlineForEachExpansion(planned) || IsUnexpandedDeferredForEachRow(planned))
                return Array.Empty<GateInput>();
            if (IsForEachPreparation(planned))
            {
                var source = SelfDeclaredForEachSource(planned.Step);
                return inputs.Where(input => string.Equals(input.Name, source, StringComparison.Ordinal)).ToArray();
            }
            var frozen = FrozenForEachSource(planned);
            return inputs.Where(input => !string.Equals(input.Name, frozen, StringComparison.Ordinal)).ToArray();
        }

        private static Dictionary<string, AnswerValue> ProvidedAnswers(
            WorkflowRunState run, CoherentCurrentStep current, GateInput[] questions)
        {
            if (!run.OwnerClosure.Definitions.Any(d => d.Steps.Any(s => s.Call != null))) return null;
            var visible = ResolveAnswerSources(run, current.Frame, current.PlanIndex);
            var provided = new Dictionary<string, AnswerValue>(StringComparer.Ordinal);
            foreach (var question in questions)
                if (visible.TryGetValue(question.Name, out var source) && source.Answer != null)
                    provided[question.Name] = RunFrame.CloneAnswer(source.Answer);
            return provided.Count == 0 ? null : provided;
        }

        private static WorkflowRunFrameView[] BuildFrameViews(WorkflowRunState run, StepResult[] clonedSteps)
        {
            var projected = new List<WorkflowRunFrameView>();
            var indexes = run.Frames.Where(f => f.ProjectionKind != null)
                .Select((frame, index) => (frame.FrameId, Index: index))
                .ToDictionary(f => f.FrameId, f => f.Index, StringComparer.Ordinal);
            foreach (var frame in run.Frames)
            {
                if (frame.ProjectionKind == null) continue;
                var groupedSteps = run.Plan.Select((planned, index) => new { planned, index })
                    .Where(x => string.Equals(x.planned.FrameId, frame.FrameId, StringComparison.Ordinal))
                    .Select(x => clonedSteps[x.index]).ToArray();
                WorkflowRunFrameView frameView;
                if (frame.ProjectionKind == "iteration")
                {
                    frameView = new WorkflowRunFrameView
                    {
                        Kind = "iteration",
                        StepId = frame.StepId,
                        IterationIndex = frame.IterationIndex.Value,
                        LoopVariable = frame.LoopVariable,
                        LoopValue = frame.LoopValue,
                        State = frame.State,
                        Steps = groupedSteps,
                    };
                }
                else if (frame.ProjectionKind == "call")
                {
                    frameView = new WorkflowRunFrameView
                    {
                        Kind = "call",
                        StepId = frame.StepId,
                        Workflow = frame.Workflow,
                        Depth = frame.Depth.Value,
                        Passed = frame.Passed.ToArray(),
                        Returned = frame.Returned.ToArray(),
                        ReturnNote = string.IsNullOrEmpty(frame.ReturnNote) ? null : frame.ReturnNote,
                        State = frame.State,
                        Steps = groupedSteps,
                    };
                }
                else
                {
                    throw new InvalidOperationException($"Unknown workflow frame projection kind '{frame.ProjectionKind}'.");
                }
                ProjectFrameReceipts(frameView, frame);
                if (frame.ParentFrameId != null && indexes.TryGetValue(frame.ParentFrameId, out var parentIndex))
                    frameView.ParentFrameIndex = parentIndex;
                projected.Add(frameView);
            }
            return projected.Count == 0 ? null : projected.ToArray();
        }

        private static void ProjectFrameReceipts(WorkflowRunFrameView view, RunFrame frame)
        {
            view.WitnessLocks = ProjectWitnessLocks(frame.WitnessLocks);
            view.WitnessRevisions = ProjectWitnessRevisions(frame.WitnessRevisions);
            view.PartitionRevisions = ProjectPartitionRevisions(frame.PartitionRevisions);
            view.AnchorLocks = ProjectAnchorLocks(frame.RunAnchorLocks);
            view.AnchorRevisions = ProjectAnchorRevisions(frame.AnchorRevisions);
            view.ShapeMismatchLedger = frame.ShapeMismatchLedger.Count == 0 ? null
                : frame.ShapeMismatchLedger.Values.OrderBy(x => x.ShapeId, StringComparer.Ordinal)
                    .Select(x => x.Clone()).ToArray();
            view.ShapeMismatchCountersigns = frame.ShapeMismatchCountersigns.Count == 0 ? null
                : frame.ShapeMismatchCountersigns.Select(x => x.Clone()).ToArray();
        }

        private static WitnessLockView[] ProjectWitnessLocks(Dictionary<string, string> locks) => locks.Count == 0 ? null
            : locks.Select(kv => new WitnessLockView { Probe = kv.Key, Hash = kv.Value }).ToArray();

        private static WitnessRevision[] ProjectWitnessRevisions(List<WitnessRevision> revisions) => revisions.Count == 0 ? null
            : revisions.Select(x => new WitnessRevision
            {
                Probe = x.Probe, BeforeHash = x.BeforeHash, AfterHash = x.AfterHash,
                StepId = x.StepId, TimestampUtc = x.TimestampUtc,
            }).ToArray();

        private static PartitionRevision[] ProjectPartitionRevisions(List<PartitionRevision> revisions) => revisions.Count == 0 ? null
            : revisions.Select(x => new PartitionRevision
            {
                Key = x.Key, Before = x.Before, After = x.After, StepId = x.StepId, TimestampUtc = x.TimestampUtc,
            }).ToArray();

        private static AnchorLockView[] ProjectAnchorLocks(Dictionary<string, AnchorRunLock> locks) => locks.Count == 0 ? null
            : locks.Values.Select(x => new AnchorLockView
            {
                AnchorsInput = x.AnchorsInput, InitialHash = x.InitialHash, CurrentHash = x.CurrentHash, StepId = x.StepId,
            }).ToArray();

        private static AnchorRevision[] ProjectAnchorRevisions(List<AnchorRevision> revisions) => revisions.Count == 0 ? null
            : revisions.Select(x => new AnchorRevision
            {
                Key = x.Key,
                AnchorsInput = x.AnchorsInput,
                BeforeHash = x.BeforeHash,
                AfterHash = x.AfterHash,
                StepId = x.StepId,
                TimestampUtc = x.TimestampUtc,
                Changes = x.Changes?.Select(c => new AnchorRevisionChange
                {
                    Context = c.Context,
                    OriginalExpect = c.OriginalExpect,
                    CorrectedExpect = c.CorrectedExpect,
                    ExtractQuery = c.ExtractQuery,
                    ExtractRowCount = c.ExtractRowCount,
                    ExtractTruncated = c.ExtractTruncated,
                    ExtractResultHash = c.ExtractResultHash,
                }).ToArray() ?? Array.Empty<AnchorRevisionChange>(),
            }).ToArray();

        public static WorkflowInfo BuildInfo(WorkflowDef def, string settingsStrictness = null, string globalStrictness = null,
            bool enabled = true, bool active = true, string activeReason = null, bool? gated = null) => new WorkflowInfo
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
            Gated = gated ?? (def.Error == null && def.HasEnforcedGate(settingsStrictness, globalStrictness)),
            Triggers = def.Triggers,
            Error = def.Error,
        };

        /// <summary>The terminal-run record for the experience log (learning-loop §3.1: the audit of
        /// a run must not die with the session). Answers/declines/evidence/outcomes, compact.</summary>
        public static object BuildRunRecord(WorkflowRunState run)
        {
            // pendingForEach is the pairing audit. It is cloned onto the record so a later in-place
            // mutation cannot tear the experience log, and it is omitted when null so the no-loop golden stays exact.
            var steps = run.Results.Select(ProjectTerminalStep).ToArray();
            object ProjectTerminalStep(StepResult r)
            {
                var answers = r.Answers.Select(kv => new
                {
                    name = kv.Key,
                    kv.Value.Value,
                    declined = kv.Value.Declined ? (bool?)true : null,
                    reason = kv.Value.DeclineReason,
                }).ToArray();
                // Immutable evidence history rides the terminal record whenever it says MORE than the current
                // verify field. Compared by CONTENT, not count: a rejected re-submission clears the current results
                // BEFORE any archive, so a one-item history over an empty current field is the ONLY surviving copy
                // of attempt 1's evidence — a count-based "identical to current" shortcut would suppress it.
                var verifyHistory = r.VerifyHistory.Count == 0
                    || (r.VerifyHistory.Count == 1 && SameEvidence(r.VerifyHistory[0].Results, r.VerifyResults))
                    ? null : r.VerifyHistory;
                if (r.PendingForEach == null)
                {
                    return new
                    {
                        r.StepId,
                        r.Status,
                        r.Note,
                        r.EffectiveStrictness,
                        answers,
                        verify = r.VerifyResults,
                        verifyHistory,
                    };
                }
                return new
                {
                    r.StepId,
                    r.Status,
                    r.Note,
                    r.EffectiveStrictness,
                    answers,
                    verify = r.VerifyResults,
                    verifyHistory,
                    pendingForEach = r.PendingForEach.Clone(),
                };
            }
            var witnessLocks = run.WitnessLocks.Count == 0 ? null
                : run.WitnessLocks.Select(kv => new { probe = kv.Key, hash = kv.Value }).ToArray();
            var anchorLocks = run.RunAnchorLocks.Count == 0 ? null : run.RunAnchorLocks.Values.Select(x => new
            {
                x.AnchorsInput,
                x.InitialHash,
                x.CurrentHash,
                x.StepId,
            }).ToArray();

            object LegacyRecord() => new
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
                steps,
                // E3(a)/blocker-3 — the witness/partition locks + revision receipts ride the terminal run record into
                // the experience log. This projection is the pre-v7 shape and must remain byte-stable.
                witnessLocks,
                witnessRevisions = run.WitnessRevisions.Count == 0 ? null : run.WitnessRevisions.ToArray(),
                partitionRevisions = run.PartitionRevisions.Count == 0 ? null : run.PartitionRevisions.ToArray(),
                anchorLocks,
                anchorRevisions = run.AnchorRevisions.Count == 0 ? null : run.AnchorRevisions.ToArray(),
            };

            if (!AnyFrameHasCoverageSurface(run)) return LegacyRecord();

            return new
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
                steps,
                witnessLocks,
                witnessRevisions = run.WitnessRevisions.Count == 0 ? null : run.WitnessRevisions.ToArray(),
                partitionRevisions = run.PartitionRevisions.Count == 0 ? null : run.PartitionRevisions.ToArray(),
                anchorLocks,
                anchorRevisions = run.AnchorRevisions.Count == 0 ? null : run.AnchorRevisions.ToArray(),
                coverageSurfaceRevisions = run.CoverageSurfaceRevisions.Count == 0 ? null
                    : run.CoverageSurfaceRevisions.Select(x => x.Clone()).ToArray(),
                shapeMismatchLedger = run.ShapeMismatchLedger.Count == 0 ? null
                    : run.ShapeMismatchLedger.Values.OrderBy(x => x.ShapeId, StringComparer.Ordinal)
                        .Select(x => x.Clone()).ToArray(),
                shapeMismatchCountersigns = run.ShapeMismatchCountersigns.Count == 0 ? null
                    : run.ShapeMismatchCountersigns.Select(x => x.Clone()).ToArray(),
                certificate = run.Certificate?.Clone(),
            };
        }
    }
}
