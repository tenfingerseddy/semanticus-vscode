// Steps someone else does, inside a draft (composer contract 1.3, 3.2 and 3.4; lane author-gaps, B9).
//
// A draft used to run only steps Semanticus runs. A composed workflow may also carry:
//   - a person's stop ("Stop and ask me here", performer: person). The run pauses there, and the person presses
//     Continue. Continue is the person's own, on the human-only target, like Approve and run on a held draft: an
//     assistant that could press it would turn "ask me here" into "ask nobody".
//   - an assistant's step (performer: assistant). The run pauses there and puts ONE request on the assistant's queue.
//     The assistant answers it with complete_request, whose text is the step's writes: a JSON list of {op, args} from
//     the composer's list. They are checked (the step's written checks on every formula they carry, then a rehearsal)
//     and apply as ONE MutateAsync labelled with the step's title, so the step is exactly one undo entry (contract
//     3.4). A refusal keeps the claim and counts a try, as a blank does; the third hands the step to the person, who
//     can ask again or continue without it.
//
// THE RUN, STEP BY STEP. Every read still runs first. Then the steps run in order: each run of consecutive steps
// Semanticus runs is one change group (one MutateAsync, one undo entry), each step after the change runs as it
// always has, and each step someone else does pauses the run. The summary counts the undo steps honestly: one per
// change group plus one per assistant's step (contract 3.2). A draft with no such step keeps the one-change path in
// DraftRunner, byte for byte.
//
// ALL WAITING HAPPENS BEFORE A CHANGE, STILL. Every question and every blank is answered and passed before the run
// starts, except a question on a person's stop, which is asked at the stop (contract 1.4). Nothing writes while the
// run waits.

using System;
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
        // ---- the run ---------------------------------------------------------------------------------------------

        /// <summary>Run the draft on from the start, or from just after <paramref name="resumeAfter"/>, until it pauses at
        /// a step someone else does, stops, or finishes. Called with the draft gate held.</summary>
        private async Task<Draft> RunOnAsync(DraftPlace p, Draft d, string resumeAfter)
        {
            var seq = d.Steps.Where(s => s.Kept && s.Phase != DraftPhases.Prepare).OrderBy(s => s.Number).ToList();
            var i = resumeAfter == null ? 0 : seq.FindIndex(s => s.Id == resumeAfter) + 1;
            d.Run.Outcome = "running";
            d.Run.PausedAt = null;
            d.Run.EngineRunId = EngineRunId;
            EngineRunLiveness.Hold(EngineRunId);
            WriteDraft(p, d);
            PublishDraftQuietly(p, d);

            while (i < seq.Count)
            {
                var st = seq[i];
                if (!st.BySemanticus)
                {
                    // A gated step the guided sub-run already passed (its `when:` did not hold) needs nobody (lane l9-finish).
                    if (st.Guided && await GuidedStepAlreadyPastAsync(d, st).ConfigureAwait(false))
                    {
                        d.Run.Passed.Add(st.Id);
                        i++;
                        continue;
                    }
                    if (st.ByAssistant) await PostStepRequestAsync(p, d, st).ConfigureAwait(false);
                    d.Run.Outcome = "paused";
                    d.Run.PausedAt = st.Id;
                    d.Run.Revision = _sessions.Current?.Revision ?? d.Run.Revision;
                    WriteDraft(p, d);
                    return d;
                }
                if (st.Phase == DraftPhases.Change)
                {
                    var group = new List<DraftStep>();
                    while (i < seq.Count && seq[i].BySemanticus && seq[i].Phase == DraftPhases.Change) group.Add(seq[i++]);
                    ComposedChangeResult r;
                    try { r = await ApplyComposedChangeAsync(ChangeOf(d, group), d.ApprovedBy ?? "human").ConfigureAwait(false); }
                    catch (Exception ex)
                    {
                        r = new ComposedChangeResult { Outcome = ComposedOutcome.Failed, Revision = _sessions.Current?.Revision ?? 0, Reason = "Nothing changed. " + ex.Message };
                    }
                    Record(d, r, group.Count);
                    d.Run.BeforeChecks = d.Run.BeforeChecks.Concat(r.BeforeChecks ?? Array.Empty<ComposedCheckResult>()).ToArray();
                    if (r.Outcome != ComposedOutcome.Applied)
                    {
                        // The group's own step numbers, not its place inside the group, so the banner names the step.
                        var at = r.StopStep > 0 && r.StopStep <= group.Count ? group[r.StopStep - 1] : group[0];
                        d.Run.Outcome = r.Outcome == ComposedOutcome.Stopped ? "stopped" : r.Outcome == ComposedOutcome.NotReady ? "not_ready" : "failed";
                        d.Run.StopStep = at.Number;
                        d.Run.Reason = r.StopStep > 0 && r.Reason != null
                            ? r.Reason.Replace("Step " + r.StopStep.ToString(CultureInfo.InvariantCulture) + " ", "Step " + at.Number.ToString(CultureInfo.InvariantCulture) + " ", StringComparison.Ordinal)
                            : r.Reason;
                        d.Run.FinishedUtc = DateTimeOffset.UtcNow;
                        await EndGuidedRunAsync(d, "The draft stopped at step " + at.Number.ToString(CultureInfo.InvariantCulture) + ".").ConfigureAwait(false);
                        WriteDraft(p, d);
                        return d;
                    }
                    d.Run.After.AddRange(await RunAfterAsync(d, null, group, Array.Empty<DraftStep>(), d.Run.After).ConfigureAwait(false));
                    WriteDraft(p, d);
                    continue;
                }
                d.Run.After.AddRange(await RunAfterAsync(d, null, Array.Empty<DraftStep>(), new[] { st }, d.Run.After).ConfigureAwait(false));
                WriteDraft(p, d);
                i++;
            }
            d.Run.Outcome = "applied";
            d.Run.FinishedUtc = DateTimeOffset.UtcNow;
            await EndGuidedRunAsync(d, "The draft finished.").ConfigureAwait(false);
            WriteDraft(p, d);
            return d;
        }

        /// <summary>What one applied change adds to the run: its steps, its revision, and one more undo entry.</summary>
        private static void Record(Draft d, ComposedChangeResult r, int steps)
        {
            d.Run.Revision = r.Revision;
            d.Run.AppliedStepIds = d.Run.AppliedStepIds.Concat(r.AppliedStepIds ?? Array.Empty<string>()).Distinct(StringComparer.Ordinal).ToArray();
            if (!r.Changed) return;
            d.Run.Changed = true;
            d.Run.UndoEntries++;
            d.Run.LastUndoSteps = (r.AppliedStepIds ?? Array.Empty<string>()).Length;
        }

        /// <summary>How many undo entries the draft makes when it runs through (contract 3.2): one per run of consecutive
        /// change steps Semanticus runs, one per assistant's step. A person's stop makes none but splits the groups.</summary>
        internal static int UndoStepsOf(Draft d)
        {
            var n = 0;
            var inGroup = false;
            foreach (var s in d.Steps.Where(s => s.Kept && s.Phase == DraftPhases.Change).OrderBy(s => s.Number))
            {
                if (s.BySemanticus) { if (!inGroup) n++; inGroup = true; continue; }
                inGroup = false;
                // A gated step counts only when its gate names an op that changes the model (lane l9-finish): a gated
                // review that writes nothing makes no undo entry, so the summary does not promise one.
                if (s.ByAssistant && (!s.Guided || s.GuidedWrites)) n++;
            }
            return n;
        }

        // ---- reorder (composer contract 6: "only where dependencies allow") ---------------------------------------

        /// <summary>Move a step to a new position. Refused when a step would then come before a step it needs (one that
        /// creates something it names), or when the order would break the draft's phases (reads first, then the change,
        /// then the steps after it). The steps are numbered again; a question keeps its step by id.</summary>
        private static string Reorder(Draft d, DraftStep st, string to)
        {
            var order = d.Steps.OrderBy(s => s.Number).ToList();
            if (!int.TryParse(to?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var place) || place < 1 || place > order.Count)
                throw new ArgumentException("Say where to move it: a position from 1 to " + order.Count.ToString(CultureInfo.InvariantCulture) + ".", "text");
            var from = st.Number;
            order.Remove(st);
            order.Insert(place - 1, st);
            if (order.Select(s => s.Id).SequenceEqual(d.Steps.OrderBy(s => s.Number).Select(s => s.Id))) return "It is already there.";
            for (var i = 0; i < order.Count; i++)
                for (var j = i + 1; j < order.Count; j++)
                {
                    var made = CreatedBy(order[j]).ToList();
                    if (made.Count == 0) continue;
                    if (order[i].Actions.SelectMany(a => a.Args.Values).Where(v => v != null).Any(v => made.Any(r => Names(v, r))))
                        throw new InvalidOperationException("Step " + order[i].Number.ToString(CultureInfo.InvariantCulture) + " needs step "
                            + order[j].Number.ToString(CultureInfo.InvariantCulture) + ", so it cannot come before it.");
                }
            var old = d.Steps.ToDictionary(s => s.Id, s => s.Number, StringComparer.Ordinal);
            for (var i = 0; i < order.Count; i++) order[i].Number = i + 1;
            try { CheckStepOrder(new Draft { Steps = order }); }
            catch (ArgumentException)
            {
                foreach (var s in d.Steps) s.Number = old[s.Id];
                throw new InvalidOperationException("Reads come first, then the steps that change the model, then the steps after the change, so step "
                    + from.ToString(CultureInfo.InvariantCulture) + " cannot move there.");
            }
            var renumber = old.ToDictionary(kv => kv.Value, kv => d.Steps.First(s => s.Id == kv.Key).Number);
            foreach (var s in d.Steps.Where(s => s.DroppedWith is int)) s.DroppedWith = renumber[s.DroppedWith.Value];
            d.Steps = order;
            return "Moved step " + from.ToString(CultureInfo.InvariantCulture) + " to " + place.ToString(CultureInfo.InvariantCulture) + ".";
        }

        /// <summary>Does an argument value name something a step creates: the same ref, or a column or measure of a
        /// table it creates. The same rule NeedsOf uses to drop a step with the step it needs.</summary>
        private static bool Names(string v, string r) =>
            string.Equals(v, r, StringComparison.OrdinalIgnoreCase)
            || (r.StartsWith("table:", StringComparison.Ordinal) && (v.StartsWith("column:" + r.Substring(6) + "/", StringComparison.OrdinalIgnoreCase)
                                                                  || v.StartsWith("measure:" + r.Substring(6) + "/", StringComparison.OrdinalIgnoreCase)));

        // ---- the person's Continue -------------------------------------------------------------------------------

        /// <summary>Continue past the step the run waits at. On a person's stop, once its questions are answered; on an
        /// assistant's step, without the assistant's changes (its request is closed). The person's own, on the
        /// human-only target: no MCP op reaches it.</summary>
        public async Task<DraftView> ContinueDraftAsync(string draftId)
        {
            RequireProFeature();
            var p = BindDrafts();
            string id, sentence;
            await _draftGate.WaitAsync().ConfigureAwait(false);
            try
            {
                var d = RequireDraft(p, draftId);
                id = d.Id;
                if (d.Run?.Outcome != "paused")
                    throw new InvalidOperationException("Nothing is waiting for you to continue. The run is not paused at a step.");
                var at = d.Steps.First(s => s.Id == d.Run.PausedAt);
                if (at.Guided)
                {
                    // Past a gated step: recorded in its guided run as a skip with the reason, the audited way past a gate.
                    d = await ContinuePastGuidedAsync(p, d, at, DraftCopy.GuidedSkipReason).ConfigureAwait(false);
                    sentence = DraftCopy.ContinuedWithout + (d.Run.Outcome == "applied" ? " The rest ran." : "");
                    goto continued;
                }
                if (at.ByPerson)
                {
                    var open = d.Questions.FirstOrDefault(q => q.Step == at.Id && q.Required && q.Answer == null);
                    if (open != null) throw new ArgumentException("Answer first: " + open.Question);
                    sentence = "Continued.";
                }
                else
                {
                    await CancelStepRequestAsync(p, at, "The person continued without it.").ConfigureAwait(false);
                    sentence = DraftCopy.ContinuedWithout;
                }
                d.Run.Passed.Add(at.Id);
                d = await RunOnAsync(p, d, at.Id).ConfigureAwait(false);
                if (d.Run.Outcome == "applied") sentence += " The rest ran.";
            continued:;
            }
            finally { _draftGate.Release(); }
            return await DraftChangedAsync(p, id, sentence).ConfigureAwait(false);
        }

        // ---- an assistant's step on the queue --------------------------------------------------------------------

        /// <summary>True when an assistant's step waits for the person: declined, or refused three times.</summary>
        internal static bool StepRequestNeedsYou(DraftStep st, Func<string, PlanTask> task) =>
            st.Reason != null && (string.IsNullOrEmpty(st.RequestId) || !PlanTasks.IsOpenTask(task(st.RequestId)));

        private async Task PostStepRequestAsync(DraftPlace p, Draft d, DraftStep st)
        {
            if (!string.IsNullOrEmpty(st.RequestId) && PlanTasks.IsOpenTask(TaskReader(p)(st.RequestId))) return;
            var r = await AssistantRequests.PostAsync(new AssistantRequestPost
            {
                JobId = d.Id,
                Need = st.Title,
                Brief = StepPacketOf(d, st),
                Objects = Array.Empty<string>(),
            }).ConfigureAwait(false);
            if (!r.Ok) { st.Reason = r.Message; st.RequestId = null; return; }
            st.RequestId = r.Request.RequestId;
            st.Attempts = 0;
            st.Reason = null;
        }

        private Task CancelStepRequestAsync(DraftPlace p, DraftStep st, string why)
        {
            if (string.IsNullOrEmpty(st.RequestId)) return Task.CompletedTask;
            CloseTask(p, st.RequestId, t => { t.State = PlanTaskStates.SetAside; t.DeclineReason = why; });
            st.RequestId = null;
            return Task.CompletedTask;
        }

        /// <summary>The packet an assistant's step carries: the notes, the Brief, what to send and how, and the checks.</summary>
        private static string StepPacketOf(Draft d, DraftStep st)
        {
            var lines = new List<string>();
            if (!string.IsNullOrWhiteSpace(d.AssistantNotes)) lines.Add("Notes for you: " + d.AssistantNotes.Trim());
            if (!string.IsNullOrWhiteSpace(st.Brief)) lines.Add("Brief: " + st.Brief.Trim());
            lines.Add("Do step " + st.Number.ToString(CultureInfo.InvariantCulture) + ", " + st.Title + ", of \"" + d.Title + "\".");
            if (st.Guided)
            {
                lines.AddRange(GuidedPacketLines(d, st));
                foreach (var q in d.Questions.Where(q => q.Answer != null && q.Step == st.Id)) lines.Add(q.Question + ": " + q.Answer + ".");
                var guided = string.Join("\n", lines);
                return guided.Length <= PlanTasks.MaxBriefChars ? guided : guided.Substring(0, PlanTasks.MaxBriefChars);
            }
            lines.Add("Send its changes with complete_request: the text is a JSON list of {\"op\", \"args\"} from the composer's change ops "
                      + "(create_measure, update_measure, set_description, set_measure_format and the rest). They apply as one change, one undo step. "
                      + "Send [] when the step needs no change.");
            var checks = st.Checks.Select(DraftCopy.CheckWords).ToList();
            if (checks.Count > 0) lines.Add("Checks they must pass: " + string.Join(" ", checks));
            foreach (var q in d.Questions.Where(q => q.Answer != null && q.Step == st.Id)) lines.Add(q.Question + ": " + q.Answer + ".");
            var text = string.Join("\n", lines);
            return text.Length <= PlanTasks.MaxBriefChars ? text : text.Substring(0, PlanTasks.MaxBriefChars);
        }

        /// <summary>The draft and the assistant's step a request carries, or nulls.</summary>
        private (Draft Draft, DraftStep Step) DraftStepOf(DraftPlace p, PlanTask task)
        {
            if (task?.OriginLink?.Kind != PlanOriginKinds.Draft || !DraftStore.IsDraftId(task.OriginLink.Ref)) return (null, null);
            var d = ReadDraft(p, task.OriginLink.Ref);
            var st = d?.Steps.FirstOrDefault(s => s.ByAssistant && string.Equals(s.RequestId, task.Id, StringComparison.Ordinal));
            return st == null ? (null, null) : (d, st);
        }

        /// <summary>complete_request on an assistant's step: the text is the step's writes. They are checked, then
        /// applied as one change, and the run goes on. Null when the request is not a step's.</summary>
        internal async Task<HandoffResult> TryCompleteDraftStepAsync(string requestId, string claimToken, string text)
        {
            var p = BindDrafts();
            if (DraftStepOf(p, ReadPlanState(p.Bound).FindTask(requestId?.Trim())).Draft == null) return null;

            HandoffResult result;
            string draftId;
            await _draftGate.WaitAsync().ConfigureAwait(false);
            try
            {
                PlanTask task;
                Draft d;
                DraftStep st;
                List<ComposedAction> writes;
                await _queueGate.WaitAsync().ConfigureAwait(false);
                try
                {
                    task = ReadPlanState(p.Bound).FindTask(requestId.Trim());
                    (d, st) = DraftStepOf(p, task);
                    if (d == null) return null;
                    draftId = d.Id;
                    var refused = ClaimProblem(task, claimToken);
                    if (refused != null) return refused;
                    if (d.Run?.Outcome != "paused" || d.Run.PausedAt != st.Id)
                        return StepRefused(HandoffRefusal.AlreadyTerminal, "The run is not waiting on this step now. Nothing was changed.", task, st);
                    // A gated step is submitted the guided way, so its gate runs (lane l9-finish).
                    if (st.Guided)
                        return StepRefused(HandoffRefusal.NotReady, "This step runs as a guided workflow step, so it is submitted with submit_workflow_step(runId \""
                            + d.Run.GuidedRunId + "\", stepId \"" + st.Id + "\", answers, writes), which runs its checks. Nothing was changed.", task, st);
                    try { writes = string.IsNullOrWhiteSpace(text) || text.Trim() == "[]" ? new List<ComposedAction>() : DirectStepWrites(text); }
                    catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException)
                    {
                        return StepFailed(p, d, st, task, ex.Message);
                    }
                }
                finally { _queueGate.Release(); }

                // Outside the queue gate: the checks and the change.
                if (writes.Count > 0)
                {
                    var problem = await WrittenChecksOnWritesAsync(d, st, writes).ConfigureAwait(false);
                    if (problem != null) { result = StepFailed(p, d, st, task, problem); goto done; }
                    var change = new ComposedChange { DraftId = d.Id, Title = st.Title, Workflow = d.Workflow, Answers = AnswersOf(d) };
                    var cs = new ComposedStep { Id = st.Id, Title = st.Title, Checks = st.Checks.Where(c => c.Phase == "before").ToList() };
                    cs.Actions.AddRange(writes);
                    change.Steps.Add(cs);
                    ComposedChangeResult r;
                    try { r = await ApplyComposedChangeAsync(change, "agent").ConfigureAwait(false); }
                    catch (Exception ex) { r = new ComposedChangeResult { Outcome = ComposedOutcome.Failed, Reason = "Nothing changed. " + ex.Message }; }
                    if (r.Outcome != ComposedOutcome.Applied) { result = StepFailed(p, d, st, task, (r.Reason ?? "Nothing changed.").Replace("Step 1 ", "", StringComparison.Ordinal)); goto done; }
                    Record(d, r, 1);
                    if (!d.Run.AppliedStepIds.Contains(st.Id)) d.Run.AppliedStepIds = d.Run.AppliedStepIds.Append(st.Id).ToArray();
                    d.Run.LastUndoSteps = 1;
                }
                CloseTask(p, task.Id, t => { t.Reply = text; t.State = PlanTaskStates.Done; t.DoneBy = PlanDoers.Assistant; });
                st.RequestId = null;
                st.Reason = null;
                d.Run.Passed.Add(st.Id);
                d.Run.After.AddRange(await RunAfterAsync(d, null, new[] { st }, Array.Empty<DraftStep>(), d.Run.After).ConfigureAwait(false));
                d = await RunOnAsync(p, d, st.Id).ConfigureAwait(false);
                result = new HandoffResult
                {
                    Request = RequestOf(ReadPlanState(p.Bound).FindTask(task.Id), null, AssistantRequestState.Completed),
                    Message = (writes.Count == 0 ? "Done, with no change." : "Checked and applied as one change, one undo step.")
                              + (d.Run.Outcome == "paused" ? " The run waits at the next step." : d.Run.Outcome == "applied" ? " The rest ran. Read get_draft for how it went." : " The run stopped. Read get_draft for why."),
                };
            done:;
            }
            finally { _draftGate.Release(); }
            await DraftChangedAsync(p, draftId, null).ConfigureAwait(false);
            return result;
        }

        /// <summary>decline_request on an assistant's step: the step waits for the person, with the reason.</summary>
        internal async Task<HandoffResult> TryDeclineDraftStepAsync(string requestId, string claimToken, string reason)
        {
            var p = BindDrafts();
            if (DraftStepOf(p, ReadPlanState(p.Bound).FindTask(requestId?.Trim())).Draft == null) return null;
            var why = RequireReason(reason);
            HandoffResult result;
            string draftId;
            await _draftGate.WaitAsync().ConfigureAwait(false);
            try
            {
                await _queueGate.WaitAsync().ConfigureAwait(false);
                try
                {
                    var task = ReadPlanState(p.Bound).FindTask(requestId.Trim());
                    var (d, st) = DraftStepOf(p, task);
                    if (d == null) return null;
                    draftId = d.Id;
                    var refused = ClaimProblem(task, claimToken);
                    if (refused != null) return refused;
                    CloseTask(p, task.Id, t => { t.State = PlanTaskStates.SetAside; t.DeclineReason = why; });
                    st.RequestId = null;
                    st.Reason = DraftCopy.AssistantAsks(why);
                    WriteDraft(p, d);
                    result = new HandoffResult
                    {
                        Request = RequestOf(ReadPlanState(p.Bound).FindTask(task.Id), null, AssistantRequestState.Declined),
                        Message = "Handed back to the person with your reason.",
                    };
                }
                finally { _queueGate.Release(); }
            }
            finally { _draftGate.Release(); }
            await DraftChangedAsync(p, draftId, null).ConfigureAwait(false);
            return result;
        }

        /// <summary>A refused submission: the claim stays and a try is counted; the third hands the step to the person.</summary>
        private HandoffResult StepFailed(DraftPlace p, Draft d, DraftStep st, PlanTask task, string why)
        {
            st.Attempts++;
            if (st.Attempts >= DraftTries)
            {
                st.Reason = why;
                st.RequestId = null;
                CloseTask(p, task.Id, t => { t.State = PlanTaskStates.SetAside; t.DeclineReason = DraftCopy.FailedThreeTimes; });
                WriteDraft(p, d);
                return StepRefused(HandoffRefusal.ChecksFailed, why + " That was the third try, so this step now waits for the person.", task, st);
            }
            WriteDraft(p, d);
            return StepRefused(HandoffRefusal.ChecksFailed, why + " " + DraftCopy.TriesLeft(DraftTries - st.Attempts)
                + " Fix it and call complete_request again with the same claimToken.", task, st);
        }

        private static HandoffResult StepRefused(HandoffRefusal why, string message, PlanTask task, DraftStep st)
        {
            var r = RequestOf(task, null, task?.Claim != null ? AssistantRequestState.Claimed : AssistantRequestState.Queued);
            r.AttemptsLeft = Math.Max(0, DraftTries - st.Attempts);
            return new HandoffResult { Refusal = why, Message = message, Request = r };
        }

        /// <summary>The step's written checks on every formula its writes carry, against a rehearsal of the writes (so a
        /// formula over a measure the same writes create is judged against the model it will meet). Null when they pass;
        /// else the sentence. A rehearsal that fails is itself the refusal: nothing is written.</summary>
        private async Task<string> WrittenChecksOnWritesAsync(Draft d, DraftStep st, List<ComposedAction> writes)
        {
            var s = _sessions.Require();
            var change = new ComposedChange { DraftId = d.Id, Title = st.Title, Answers = AnswersOf(d) };
            var cs = new ComposedStep { Id = st.Id, Title = st.Title };
            cs.Actions.AddRange(writes);
            change.Steps.Add(cs);
            CompiledChange compiled;
            try { compiled = ComposedChangeCompiler.Compile(change, BlankFill.Require); }
            catch (Exception ex) { return ex.Message; }
            var formulas = writes.SelectMany(w => w.Args.Where(kv => kv.Value != null && OpParameterKinds.AuthoredKind(w.Op, kv.Key) == "dax").Select(kv => kv.Value)).ToList();
            var checks = st.Checks.Where(c => c.Phase == "written").ToList();
            if (formulas.Count > 0 && !checks.Any(c => c.Kind == "valid_dax")) checks.Insert(0, new ComposedCheck { Kind = "valid_dax", Phase = "written" });
            var blank = new ComposedBlank { Name = "formula", Kind = "dax" };
            var r = await RehearseComposedAsync(s, compiled.Steps,
                m => formulas.SelectMany(f => checks.Select(c => RunWrittenCheck(m, c, blank, f, 1, st.Id))).ToArray()).ConfigureAwait(false);
            if (!r.Ok) return "Nothing was written. " + r.Problem;
            var failed = r.Value.Where(c => c.Status != ComposedCheckStatus.Passed).Select(c => c.Text).Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
            return failed.Count == 0 ? null : string.Join(" ", failed.Select(t => t.TrimEnd().EndsWith(".") ? t.Trim() : t.Trim() + "."));
        }
    }
}
