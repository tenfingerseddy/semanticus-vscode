// A gated step inside a draft (composer contract 1.3; lane l9-finish).
//
// Contract 1.3: the `yaml gate` fence "is still read in v3 for performer: assistant steps, which run exactly as a guided
// step does today". Until this lane a draft refused such a file (author-gaps B9's named gap). Now the draft runs it:
//
// ONE GUIDED SUB-RUN PER DRAFT RUN. When a draft with a gated assistant's step starts, Semanticus starts a guided
// workflow run of just those steps, in their order, from the exact bytes the draft was made from (Draft.GuidedDocument).
// It is a real run in the session's run store, started through the same phase B as start_workflow, so every gate
// mechanism works as it does in a guided run: the start-of-run snapshots the diff-based verifies read, answers that a
// later gate reads from an earlier step, witness and anchor locks, the workflow-authored measure record, and the
// step-scoped exemption of a team rule. Its steps are the draft's gated steps; everything else in the draft (Semanticus's
// change groups, a person's stop, an assistant's ungated step) runs as the draft runs it.
//
// THE DRAFT HOLDS THE ORDER. The sub-run is submitted with submit_workflow_step, the door a guided step has always used:
// the answers the gate asks, and the step's writes as one change (Composer/DirectStep.cs), so the step is one undo entry.
// Submission is refused unless the draft waits at that step. The guided runner evaluates the gate before the draft moves
// on. A hard gate that blocks has already taken the step's writes back (SubmitWorkflowStepCoreAsync); the draft then
// stops with the gate's own sentence, and the sub-run ends with it. A refusal that is not the gate's verdict (an
// unanswered input, a bad answer) leaves the draft waiting, exactly as a guided step waits for a corrected submission.
//
// PAST A GATED STEP ONLY THE PERSON GOES. Continue on the draft skips the step in the sub-run with a reason, the audited
// way past a guided gate. skip_workflow_step from the assistant is refused, as Continue is the person's own
// (author-gaps, gap 1). abort_workflow on the sub-run stops the draft.
//
// A RESTART ENDS THE SUB-RUN. Guided runs live in the engine's memory and drafts on disk. A draft waiting at a gated
// step when Semanticus restarts reads Stopped with the restart line, and may run again.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine.Composer;
using Semanticus.Engine.Jev;

namespace Semanticus.Engine
{
    public sealed partial class LocalEngine
    {
        /// <summary>Guided sub-run id to the draft it belongs to, for this engine's lifetime.</summary>
        private readonly ConcurrentDictionary<string, string> _draftGuidedRuns = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);

        /// <summary>The draft a guided run belongs to, or null for any other run.</summary>
        private string DraftOfGuidedRun(string runId) =>
            runId != null && _draftGuidedRuns.TryGetValue(runId.Trim(), out var draftId) ? draftId : null;

        /// <summary>True when the draft's guided sub-run is gone: a restart ended it.</summary>
        internal bool GuidedRunLost(Draft d) =>
            d?.Run?.GuidedRunId != null && !_draftGuidedRuns.ContainsKey(d.Run.GuidedRunId);

        /// <summary>A run step's result as the sub-run holds it: its status and note, or nulls when the step is not there.</summary>
        private async Task<(string Status, string Note, VerifyResult[] Verify, bool Current, string RunStatus)> GuidedStepAsync(string runId, string stepId)
        {
            var context = _sessions.CurrentContext;
            await context.WorkflowGate.WaitAsync().ConfigureAwait(false);
            try
            {
                var run = context.WorkflowRuns.Require(runId);
                var i = run.Plan.FindIndex(x => string.Equals(x.Step?.Id, stepId, StringComparison.Ordinal));
                if (i < 0) return (null, null, null, false, run.Status);
                var r = run.Results[i];
                return (r.Status, r.Note, r.VerifyResults ?? Array.Empty<VerifyResult>(), run.Status == "active" && run.StepIndex == i, run.Status);
            }
            finally { context.WorkflowGate.Release(); }
        }

        // ---- the start --------------------------------------------------------------------------------------------

        /// <summary>Start the draft's guided sub-run: the kept gated steps, in order, from the bytes the draft was made
        /// from. Called with the draft gate held, after the reads and before any change, so the start-of-run snapshots
        /// are of the model before the run. Null when it started; else the sentence the draft stops with.</summary>
        private async Task<string> StartGuidedRunAsync(Draft d)
        {
            var keep = d.Steps.Where(s => s.Kept && s.Guided).Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
            if (keep.Count == 0) return null;
            try
            {
                var full = WorkflowParser.Parse(d.GuidedDocument ?? "");
                if (full.Error != null) return "The workflow this draft came from no longer reads: " + full.Error;
                var guided = new WorkflowDef
                {
                    SchemaVersion = full.SchemaVersion, Name = full.Name, Kind = full.Kind, Title = full.Title, Description = full.Description,
                    WhenToUse = full.WhenToUse, Version = full.Version, Strictness = full.Strictness, Triggers = full.Triggers, Tags = full.Tags,
                    Source = full.Source, Provenance = full.Provenance,
                    Steps = full.Steps.Where(s => keep.Contains(s.Id)).ToArray(),
                };
                var closure = WorkflowParser.ReachableClosure(guided, WithParkedCallees(LoadWorkflowDefs()), LoadTemplateDefs(), out var problems);
                var handoff = TeachParkedCallees(problems);
                if (handoff.Count > 0) return "Its checks cannot start: " + handoff[0].Message;
                var (run, _) = await BeginWorkflowRunAsync(guided, closure, d.ApprovedBy ?? "human", active: true, forceActive: true, activeReason: null, draftId: d.Id).ConfigureAwait(false);
                _draftGuidedRuns[run.RunId] = d.Id;
                d.Run.GuidedRunId = run.RunId;
                return null;
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is ArgumentException || ex is FormatException)
            {
                return "Its checks cannot start: " + ex.Message;
            }
        }

        /// <summary>End the sub-run with the draft: a stop, an abort, or a finish that left a step unrun.</summary>
        private async Task EndGuidedRunAsync(Draft d, string reason)
        {
            var id = d?.Run?.GuidedRunId;
            if (id == null || !_draftGuidedRuns.ContainsKey(id)) return;
            try
            {
                var (_, _, _, _, status) = await GuidedStepAsync(id, null).ConfigureAwait(false);
                if (status == "active") await AbortWorkflowCoreAsync(id, reason, "human").ConfigureAwait(false);
            }
            catch (InvalidOperationException) { /* the run already aged out of the store */ }
        }

        // ---- the run reaches a gated step ------------------------------------------------------------------------

        /// <summary>At a gated step: true when the sub-run already passed it (a `when:` that did not hold makes it not
        /// applicable, exactly as in a guided run), so the draft moves on without asking anyone.</summary>
        private async Task<bool> GuidedStepAlreadyPastAsync(Draft d, DraftStep st)
        {
            if (d.Run?.GuidedRunId == null) return false;
            var (status, _, _, current, _) = await GuidedStepAsync(d.Run.GuidedRunId, st.Id).ConfigureAwait(false);
            return !current && status != null && status != "pending" && status != "in_progress";
        }

        /// <summary>The lines a gated step's request adds to its packet: the door, the run, and what happens on a failure.</summary>
        private static IEnumerable<string> GuidedPacketLines(Draft d, DraftStep st)
        {
            var run = d.Run?.GuidedRunId ?? "(the draft's guided run)";
            yield return "This step runs as a guided workflow step. Read its questions, ops and checks with get_workflow_run(\"" + run + "\").";
            yield return "Do the work, then call submit_workflow_step with runId \"" + run + "\", stepId \"" + st.Id + "\", your answers, "
                         + "and your changes to the model as writes (a JSON list of {\"op\", \"args\"}). The writes apply as one change, one undo step, and then the step's checks run.";
            yield return "If a check fails, the writes are taken back and the draft stops with the reason. complete_request is not the door for this step.";
        }

        // ---- the doors -------------------------------------------------------------------------------------------

        private DraftStep GuidedStepWaiting(Draft d, string runId) =>
            d.Run?.Outcome == "paused" && string.Equals(d.Run.GuidedRunId, runId, StringComparison.Ordinal)
                ? d.Steps.FirstOrDefault(s => s.Id == d.Run.PausedAt && s.Guided) : null;

        private static InvalidOperationException NotWaitingAt(Draft d, string stepId)
        {
            var at = d.Run?.Outcome == "paused" ? d.Steps.FirstOrDefault(s => s.Id == d.Run.PausedAt) : null;
            var name = string.IsNullOrWhiteSpace(stepId) ? "a step" : "'" + stepId.Trim() + "'";
            return new InvalidOperationException(at == null
                ? "This run belongs to the draft \"" + d.Title + "\", which is not waiting at a step your assistant does now, so " + name + " cannot be submitted. Read get_draft for where it is."
                : "This run belongs to the draft \"" + d.Title + "\", which waits at step " + at.Number.ToString(CultureInfo.InvariantCulture) + " (" + at.Id + "), so " + name + " cannot be submitted now.");
        }

        /// <summary>submit_workflow_step on a draft's sub-run: only the step the draft waits at. Accepted, the draft runs
        /// on; blocked by its hard gate, the draft stops with the gate's sentence.</summary>
        private async Task<WorkflowRunView> SubmitDraftGuidedStepAsync(string draftId, string runId, string stepId, string answersJson, string origin, string callGate, string writesJson)
        {
            var p = BindDrafts();
            WorkflowRunView view = null;
            Exception blocked = null;
            await _draftGate.WaitAsync().ConfigureAwait(false);
            try
            {
                var d = RequireDraft(p, draftId);
                var st = GuidedStepWaiting(d, runId);
                if (st == null || (!string.IsNullOrWhiteSpace(stepId) && !string.Equals(stepId.Trim(), st.Id, StringComparison.Ordinal)))
                    throw NotWaitingAt(d, stepId);
                var before = _sessions.Current?.Revision ?? 0;
                try { view = await SubmitWorkflowStepCoreAsync(runId, st.Id, answersJson, origin, callGate, writesJson).ConfigureAwait(false); }
                catch (Exception ex)
                {
                    // Only the gate's verdict stops the draft: the runner marks a step its hard gate blocked "failed" and
                    // keeps it current. Any other refusal (an unanswered input, a bad answer) leaves the draft waiting.
                    var (status, note, verify, current, _) = await GuidedStepAsync(runId, st.Id).ConfigureAwait(false);
                    if (!(current && status == "failed")) throw;
                    blocked = ex;
                    var why = GateSentence(verify, note);
                    await CancelStepRequestAsync(p, st, "Its checks did not pass.").ConfigureAwait(false);
                    d.Run.Outcome = "stopped";
                    d.Run.PausedAt = null;
                    d.Run.StopStep = st.Number;
                    d.Run.Reason = DraftCopy.GuidedCheckFailed(st.Number, why);
                    d.Run.Revision = _sessions.Current?.Revision ?? d.Run.Revision;
                    d.Run.FinishedUtc = DateTimeOffset.UtcNow;
                    await EndGuidedRunAsync(d, "The draft stopped: step " + st.Number.ToString(CultureInfo.InvariantCulture) + " did not pass its checks.").ConfigureAwait(false);
                    WriteDraft(p, d);
                }
                if (blocked == null)
                {
                    // Accepted. The step's writes, if any, are one undo entry of the draft's run.
                    var after = _sessions.Current?.Revision ?? before;
                    if (after != before)
                    {
                        d.Run.Changed = true;
                        d.Run.UndoEntries++;
                        d.Run.LastUndoSteps = 1;
                        d.Run.Revision = after;
                        if (!d.Run.AppliedStepIds.Contains(st.Id)) d.Run.AppliedStepIds = d.Run.AppliedStepIds.Append(st.Id).ToArray();
                    }
                    if (!string.IsNullOrEmpty(st.RequestId))
                        CloseTask(p, st.RequestId, t => { t.State = PlanTaskStates.Done; t.DoneBy = string.Equals(origin, "agent", StringComparison.OrdinalIgnoreCase) ? PlanDoers.Assistant : PlanDoers.You; });
                    st.RequestId = null;
                    st.Reason = null;
                    d.Run.Passed.Add(st.Id);
                    d = await RunOnAsync(p, d, st.Id).ConfigureAwait(false);
                    view = await GetWorkflowRunAsync(runId).ConfigureAwait(false);
                }
            }
            finally { _draftGate.Release(); }
            await DraftChangedAsync(p, draftId, null).ConfigureAwait(false);
            if (blocked != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(blocked).Throw();
            return view;
        }

        /// <summary>The gate's own sentence: each blocking check's detail, as the guided runner wrote it.</summary>
        private static string GateSentence(VerifyResult[] verify, string note)
        {
            var blockers = (verify ?? Array.Empty<VerifyResult>()).Where(v => v.Status == "failed" || v.Status == "unavailable")
                .Select(v => (!string.IsNullOrWhiteSpace(v.Missing) ? "Missing " + v.Missing + ": " : "") + (v.Detail ?? "").Trim())
                .Where(t => t.Length > 0)
                .Select(t => char.ToUpperInvariant(t[0]) + t.Substring(1))
                .Select(t => t.EndsWith(".", StringComparison.Ordinal) ? t : t + ".")
                .ToList();
            return blockers.Count > 0 ? string.Join(" ", blockers) : (note ?? "").Trim();
        }

        /// <summary>skip_workflow_step on a draft's sub-run: the person's Continue, never the assistant's.</summary>
        private async Task<WorkflowRunView> SkipDraftGuidedStepAsync(string draftId, string runId, string stepId, string reason, string origin)
        {
            if (!string.Equals(origin ?? "human", "human", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("This step belongs to a draft, and only the person continues past a step of a draft (Continue on the draft), "
                    + "the way they continue past any step of it. Submit the step with submit_workflow_step, or hand it to the person with decline_request.");
            var p = BindDrafts();
            await _draftGate.WaitAsync().ConfigureAwait(false);
            try
            {
                var d = RequireDraft(p, draftId);
                var st = GuidedStepWaiting(d, runId);
                if (st == null || (!string.IsNullOrWhiteSpace(stepId) && !string.Equals(stepId.Trim(), st.Id, StringComparison.Ordinal)))
                    throw NotWaitingAt(d, stepId);
                await ContinuePastGuidedAsync(p, d, st, string.IsNullOrWhiteSpace(reason) ? DraftCopy.GuidedSkipReason : reason.Trim()).ConfigureAwait(false);
            }
            finally { _draftGate.Release(); }
            await DraftChangedAsync(p, draftId, null).ConfigureAwait(false);
            return await GetWorkflowRunAsync(runId).ConfigureAwait(false);
        }

        /// <summary>Past a gated step: the sub-run records the skip with its reason (a skipped hard gate is on the run's
        /// record, as in any guided run), the request closes, and the draft runs on. Called with the draft gate held.</summary>
        private async Task<Draft> ContinuePastGuidedAsync(DraftPlace p, Draft d, DraftStep st, string reason)
        {
            if (GuidedRunLost(d)) throw new InvalidOperationException(DraftCopy.Restarted);
            await SkipWorkflowStepCoreAsync(d.Run.GuidedRunId, st.Id, reason, "human").ConfigureAwait(false);
            await CancelStepRequestAsync(p, st, reason).ConfigureAwait(false);
            d.Run.Passed.Add(st.Id);
            if (!d.Run.Skipped.Contains(st.Id)) d.Run.Skipped.Add(st.Id);
            return await RunOnAsync(p, d, st.Id).ConfigureAwait(false);
        }

        /// <summary>abort_workflow on a draft's sub-run: the draft stops with it, and nothing after it runs.</summary>
        private async Task<WorkflowRunView> AbortDraftGuidedRunAsync(string draftId, string runId, string reason, string origin)
        {
            var p = BindDrafts();
            WorkflowRunView view;
            await _draftGate.WaitAsync().ConfigureAwait(false);
            try
            {
                view = await AbortWorkflowCoreAsync(runId, reason, origin).ConfigureAwait(false);
                var d = RequireDraft(p, draftId);
                if (d.Run != null && (d.Run.Outcome == "paused" || d.Run.Outcome == "running") && d.Run.GuidedRunId == runId)
                {
                    var at = d.Steps.FirstOrDefault(s => s.Id == d.Run.PausedAt);
                    if (at != null) await CancelStepRequestAsync(p, at, "The checks were stopped.").ConfigureAwait(false);
                    d.Run.Outcome = "stopped";
                    d.Run.StopStep = at?.Number ?? 0;
                    d.Run.PausedAt = null;
                    d.Run.Reason = DraftCopy.GuidedRunStopped(reason);
                    d.Run.FinishedUtc = DateTimeOffset.UtcNow;
                    WriteDraft(p, d);
                }
            }
            finally { _draftGate.Release(); }
            await DraftChangedAsync(p, draftId, null).ConfigureAwait(false);
            return view;
        }
    }
}
