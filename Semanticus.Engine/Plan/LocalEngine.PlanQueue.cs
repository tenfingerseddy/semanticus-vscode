// The queue only delivers (Plan at the heart, lane E3; contract sections 3, 4.1, 6 and 8.4).
//
// A REQUEST NEVER EXISTS WITHOUT AN OWNER RECORD. The assistant's queue is not a store of its own: it is a view
// over the Plan's tasks whose doer is the assistant, computed on every call from the same compose the Plan page
// reads. So the page, the fold line, get_model_summary and list_assistant_requests cannot disagree about what is
// offered, and a task a person adds with no assistant attached is simply a row that waits to be offered.
//
// ONE CAPABILITY, TWO DOORS. Claim, answer and decline reach both doors (contract 3.3, Sol 24 Sep): the assistant
// uses claim_request, complete_request and decline_request; the person uses I'll take it, Mark done and Could
// not, which land here too, on the human-only target. Adding a task, the switch, Pause, Up, the doer and the note
// stay person-only (lane E1).
//
// A CLAIM BELONGS TO ONE ENGINE RUN. The token is kept only as a SHA-256 hash. A claim written by a run that has
// ENDED is released when the plan is next read, with the honest restart line, because the assistant that held it is
// no longer attached to anything that can hear its reply. A different run id is not enough: a second workspace on the
// same model is a second live run, and its claims stay theirs (EngineRunLiveness, Codex on pull request 393).
//
// THE WORKING-ON POINTER (contract 8.4). While an assistant holds a claim, its edits count toward the task it
// claimed most recently. The pointer is kept per reader, set by claim_request, switched by claiming again, and
// cleared by complete_request and decline_request. It is explicit state the assistant set; nothing is inferred
// afterwards. The ledger's cause capture (lane E4) reads it through WorkingTaskFor.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Engine.Jev;

namespace Semanticus.Engine
{
    public sealed partial class LocalEngine
    {
        /// <summary>Minted once per engine start. A claim that names another run is released on read only once that
        /// run is gone (contract 2.2, <see cref="EngineRunLiveness"/>); another live engine's claim is left alone.</summary>
        internal readonly string EngineRunId =
            "er-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();

        /// <summary>Reader to the task it is working on, with the task's words for the cause's title (contract 8.4).</summary>
        private readonly ConcurrentDictionary<string, (string Id, string Title)> _workingOn =
            new ConcurrentDictionary<string, (string Id, string Title)>(StringComparer.Ordinal);

        /// <summary>Reader to the fingerprint of the last offer it was shown (contract 6.1).</summary>
        private readonly ConcurrentDictionary<string, string> _offerShown =
            new ConcurrentDictionary<string, string>(StringComparer.Ordinal);

        /// <summary>One claim, answer or decline at a time, so two assistants reading the same Ready row cannot
        /// both be told it is theirs. The write re-checks under the file lock as well.</summary>
        private readonly SemaphoreSlim _queueGate = new SemaphoreSlim(1, 1);

        private static string ReaderOrDoor(string reader) =>
            string.IsNullOrWhiteSpace(reader) ? ChangeJournal.InProcessReader : reader;

        /// <summary>The task this writer is working on, or null. The key is the same writer id a commit is stamped
        /// with (Session.TrackAsync): "mcp" for the MCP door in this process, the connection id for a pipe.</summary>
        internal string WorkingTaskFor(string writer) =>
            writer != null && _workingOn.TryGetValue(writer, out var t) ? t.Id : null;

        /// <summary>The words of the task this writer is working on, for the cause's title, or null.</summary>
        internal string WorkingTaskTitleFor(string writer) =>
            writer != null && _workingOn.TryGetValue(writer, out var t) ? t.Title : null;

        /// <summary>True when this reader of THIS engine run holds the claim. A reader id ("mcp", a pipe connection's id)
        /// means something only inside the run that made it: another engine on the same model has its own "mcp", and
        /// must not read that one's claim as its own (Codex on pull request 393, finding 4).</summary>
        private bool HeldHere(PlanTaskClaim c, string reader) =>
            c != null && string.Equals(c.Reader, reader, StringComparison.Ordinal)
            && string.Equals(c.EngineRunId, EngineRunId, StringComparison.Ordinal);

        internal static string HashToken(string token) =>
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token ?? string.Empty))).ToLowerInvariant();

        // ---- the offer ------------------------------------------------------------------------------------------

        /// <summary>What the assistant is offered right now, read off one compose.</summary>
        internal async Task<PlanOffer> PlanOfferAsync(ModelPlanView plan = null)
        {
            plan ??= await GetModelPlanAsync().ConfigureAwait(false);
            return PlanOffer.From(plan);
        }

        /// <summary>The fold line for this reader, or null when its offer has not changed since the last line it
        /// was shown (contract 6.1). Never throws: the line is a ride-along.
        ///
        /// Cheap in the common case: the plan is composed only when there is an open, unclaimed task for the
        /// assistant and the switch is on. Otherwise the answer is read off the Plan's file.</summary>
        public async Task<string> PullPlanOfferAsync(string reader = null)
        {
            try
            {
                var key = ReaderOrDoor(reader);
                if (_sessions.Current == null) return null;
                var bound = BindPlanMemory();
                var memory = ReadPlanState(bound);

                string fingerprint, text;
                if (memory.AssistantQueue == PlanAssistantQueue.Paused)
                {
                    var mine = memory.Tasks.Where(t => PlanTasks.IsOpenTask(t) && HeldHere(t.Claim, key)).ToList();
                    fingerprint = "paused|" + string.Join(",", mine.Select(t => t.Id));
                    text = PlanTaskQueue.PausedLine(mine.Select(t => t.Words));
                }
                else if (!memory.Tasks.Any(IsOfferCandidate))
                {
                    fingerprint = "on|";
                    text = null;
                }
                else
                {
                    var offer = await PlanOfferAsync(await ComposeAsync(null, bound).ConfigureAwait(false)).ConfigureAwait(false);
                    fingerprint = "on|" + string.Join(",", offer.Ready.Select(r => r.Id));
                    text = PlanTaskQueue.OfferLine(offer);
                }

                var last = _offerShown.TryGetValue(key, out var seen) ? seen : null;
                _offerShown[key] = fingerprint;
                var line = string.Equals(last, fingerprint, StringComparison.Ordinal) ? null : text;
                // A task this reader held and the person removed is said once, ahead of the offer (PlanRemove).
                var removed = TakeRemovalNotices(key);
                return removed == null ? line : line == null ? removed : removed + "\n" + line;
            }
            catch { return null; }
        }

        /// <summary>A task that could be in the offer before the compose says whether it waits.</summary>
        private static bool IsOfferCandidate(PlanTask t) =>
            PlanTasks.IsOpenTask(t) && !PlanTasks.IsAim(t) && t.Doer == PlanDoers.Assistant && t.Claim == null
            && !t.Held && string.IsNullOrWhiteSpace(t.DeclineReason);

        /// <summary>list_assistant_requests: the offer in order, what this reader already holds, and what is not
        /// offered yet and why.</summary>
        public async Task<AssistantRequestList> ListAssistantRequestsAsync(string reader = null)
        {
            var key = ReaderOrDoor(reader);
            var plan = await GetModelPlanAsync().ConfigureAwait(false);
            var memory = ReadPlanState(BindPlanMemory());
            var offer = PlanOffer.From(plan);
            var paused = offer.Switch == PlanAssistantQueue.Paused;

            var held = memory.Tasks.Where(t => PlanTasks.IsOpenTask(t) && HeldHere(t.Claim, key))
                                   .Select(t => RequestOf(t, plan.Items.FirstOrDefault(i => i.Id == t.Id), AssistantRequestState.Claimed))
                                   .ToArray();
            var listed = paused ? new List<ModelPlanItem>() : offer.Listed();
            return new AssistantRequestList
            {
                Switch = offer.Switch,
                Requests = listed.Select(row =>
                {
                    var r = RequestOf(memory.FindTask(row.Id), row, AssistantRequestState.Queued);
                    r.Order = listed.IndexOf(row) + 1;
                    r.Independent = offer.IsIndependent(row.Id);
                    r.OfferNote = PlanOffer.HasFloor(row) ? null : PlanTaskQueue.UnknownFootprintNote;
                    return r;
                }).ToArray(),
                Held = held,
                NotOffered = paused ? Array.Empty<PlanNotOffered>() : offer.Waiting.Select(w => new PlanNotOffered
                {
                    Id = w.Id,
                    Title = w.Title,
                    WaitsFor = w.WaitsFor?.FirstOrDefault()?.Title,
                }).ToArray(),
                Sentence = paused ? PlanTaskQueue.PausedSentence
                    : listed.Count == 0 ? "Nothing on the Plan is ready for you. Carry on with what the person asks you directly."
                    : PlanTaskQueue.Header(offer, listed.Count),
            };
        }

        /// <summary>Every task on the plan (not an aim) as a request in its true state, the token never included. What a
        /// job reads its request back from once the offer no longer lists it: done is Completed with the reply, handed
        /// back to the person is Declined with the reason, set aside is Cancelled, and anything still open is Queued or
        /// Claimed.</summary>
        internal IReadOnlyList<AssistantRequest> EveryAssistantRequest()
        {
            if (_sessions.Current == null) return Array.Empty<AssistantRequest>();
            var memory = ReadPlanState(BindPlanMemory());
            return memory.Tasks.Where(t => !PlanTasks.IsAim(t)).Select(t =>
            {
                var state = t.State == PlanTaskStates.Done ? AssistantRequestState.Completed
                    : t.State == PlanTaskStates.SetAside ? AssistantRequestState.Cancelled
                    : t.Claim != null ? AssistantRequestState.Claimed
                    // Decline hands the task back to the person: open again, their doer, the reason kept.
                    : !string.IsNullOrWhiteSpace(t.DeclineReason) && t.Doer != PlanDoers.Assistant ? AssistantRequestState.Declined
                    : AssistantRequestState.Queued;
                var r = RequestOf(t, null, state);
                if (state == AssistantRequestState.Completed) r.Answer = t.Reply;
                if (state == AssistantRequestState.Declined || state == AssistantRequestState.Cancelled) r.DeclineReason = t.DeclineReason;
                if (state == AssistantRequestState.Completed || state == AssistantRequestState.Cancelled) r.ClosedUtc = t.DoneUtc?.ToString("o");
                return r;
            }).ToArray();
        }

        private static AssistantRequest RequestOf(PlanTask task, ModelPlanItem row, AssistantRequestState state) => new AssistantRequest
        {
            RequestId = task?.Id ?? row?.Id,
            JobId = task?.OriginLink?.Kind == PlanOriginKinds.Draft ? task.OriginLink.Ref : null,
            Need = task?.Words ?? row?.Title,
            PostedUtc = task?.CreatedUtc.ToString("o"),
            State = state,
            ClaimedBy = task?.Claim?.ClaimedBy,
            ClaimedUtc = task?.Claim?.ClaimedUtc.ToString("o"),
            Note = task?.Note,
            Brief = task?.Brief,
            Objects = task?.Objects ?? Array.Empty<string>(),
            DoneText = row?.DoneText ?? PlanTasks.DoneTextOf(task?.DoneWhen, task?.Objects?.Length ?? 0),
            Order = row?.Order,
        };

        // ---- the assistant's side: claim_request, complete_request, decline_request --------------------------

        internal const string QueuePausedSentence =
            "The person has paused tasks for you. Carry on with what they ask you directly.";
        internal const string RestartSentence =
            "Semanticus restarted while your assistant had this. It is offered again.";

        /// <summary>claim_request. Refused while the switch is paused, while the row is held, waits for another
        /// row or is the person's to do. Otherwise the claim is written onto the task (the token hashed), the
        /// reader's working-on pointer moves to it, and the packet comes back with the token, once.</summary>
        public async Task<HandoffResult> ClaimAssistantRequestAsync(string requestId, string reader = null)
        {
            var key = ReaderOrDoor(reader);
            var id = requestId?.Trim();
            await _queueGate.WaitAsync().ConfigureAwait(false);
            try
            {
                var bound = BindPlanMemory();
                var plan = await ComposeAsync(null, bound).ConfigureAwait(false);
                var memory = ReadPlanState(bound);
                var task = memory.FindTask(id);
                if (task == null || PlanTasks.IsAim(task)) return Refused(HandoffRefusal.NotFound, NotFoundSentence(id));
                var row = plan.Items.FirstOrDefault(i => i.Id == task.Id);
                if (!PlanTasks.IsOpenTask(task))
                    return Refused(HandoffRefusal.AlreadyTerminal, "That task is already finished or set aside. Nothing was changed.", task, row);
                if (IsSetAside(memory, task))
                    return Refused(HandoffRefusal.AlreadyTerminal, SetAsideSentence, task, row);
                var gone = await GoneRefusalAsync(task).ConfigureAwait(false);
                if (gone != null) return Refused(HandoffRefusal.NotReady, gone, task, row);

                var again = task.Claim != null && task.Claim.ClaimedBy != PlanDoers.You && HeldHere(task.Claim, key);
                if (task.Claim != null && !again)
                    return task.Claim.ClaimedBy == PlanDoers.You
                        ? Refused(HandoffRefusal.AlreadyClaimed, "The person took this one themselves. Leave it to them.", task, row)
                        : Refused(HandoffRefusal.AlreadyClaimed, "Another assistant is working on this one. Take another.", task, row);
                if (!again)
                {
                    if (memory.AssistantQueue == PlanAssistantQueue.Paused)
                        return Refused(HandoffRefusal.QueuePaused, QueuePausedSentence, task, row);
                    if (task.Doer != PlanDoers.Assistant)
                        return Refused(HandoffRefusal.NotReady, "This task is the person's to do, not yours. Leave it to them.", task, row);
                    if (task.Held)
                        return Refused(HandoffRefusal.NotReady, "The person paused this task. Take another, or ask them.", task, row);
                    if (row?.WaitsFor is { Length: > 0 } waits)
                        return Refused(HandoffRefusal.NotReady, "This task is not ready yet. It waits for " + waits[0].Title
                            + ". Take another, or come back when that is done.", task, row);
                }

                var token = "ct-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
                EngineRunLiveness.Hold(EngineRunId);   // before the claim names this run, so no reader sees it as gone
                var won = false;
                WritePlanTasks(bound, state =>
                {
                    var mine = state.FindTask(task.Id);
                    // The same questions again under the file lock: first claim wins.
                    if (mine == null || !PlanTasks.IsOpenTask(mine) || IsSetAside(state, mine)) return;
                    if (mine.Claim != null && !(again && HeldHere(mine.Claim, key))) return;
                    if (!again && (state.AssistantQueue == PlanAssistantQueue.Paused || mine.Held || mine.Doer != PlanDoers.Assistant)) return;
                    mine.Claim = new PlanTaskClaim
                    {
                        ClaimedBy = PlanDoers.Assistant,
                        ClaimedUtc = DateTimeOffset.UtcNow,
                        ClaimTokenHash = HashToken(token),
                        EngineRunId = EngineRunId,
                        Reader = key,
                    };
                    mine.State = PlanTaskStates.Claimed;
                    mine.ReleaseNote = null;
                    won = true;
                });
                if (!won) return Refused(HandoffRefusal.AlreadyClaimed, "Someone took this task a moment ago. Take another.", task, row);

                _workingOn[key] = (task.Id, task.Words);
                var after = await ComposeAsync(null, bound).ConfigureAwait(false);
                TellThePlanMoved(bound);
                var claimed = ReadPlanState(bound).FindTask(task.Id);
                var request = RequestOf(claimed, after.Items.FirstOrDefault(i => i.Id == task.Id), AssistantRequestState.Claimed);
                request.ClaimToken = token;          // the one place the token leaves the engine
                request.Grounding = await GroundingForAsync(claimed?.Objects).ConfigureAwait(false);
                // Every word the person gave, handed over once, with the claim (UAT S12-1-005). Lists carry the title only.
                request.FullText = claimed?.FullText;
                return new HandoffResult
                {
                    Request = request,
                    Message = (again ? "You are back on this task. " : "It is yours. ")
                        + "While you hold it, your edits count toward it. Finish with complete_request, or hand it back "
                        + "with decline_request and a reason.",
                };
            }
            finally { _queueGate.Release(); }
        }

        /// <summary>complete_request: the reply goes onto the task and the task is said done. A task the engine
        /// can check reads Applied, checking until the check passes; one the assistant answers is Done.</summary>
        public async Task<HandoffResult> CompleteAssistantRequestAsync(string requestId, string claimToken, string text, string reader = null)
        {
            // A part of a composer draft is checked against the draft in the same call (lane L4, composer 5.4).
            var part = await TryCompleteDraftPartAsync(requestId, claimToken, text, reader).ConfigureAwait(false);
            if (part != null) return part;
            var reply = string.IsNullOrWhiteSpace(text) ? null : text.Trim();
            if (reply != null && reply.Length > MaxReplyChars)
                throw new ArgumentException("That reply is longer than " + MaxReplyChars + " characters. Say it shorter.", nameof(text));
            return await AnswerAsync(requestId, claimToken, reader, (t, now) =>
            {
                t.Reply = reply;
                t.State = PlanTaskStates.Done;
                t.DoneUtc = now;
                t.DoneBy = PlanDoers.Assistant;
            }, t => t.DoneWhen == PlanDoneWhen.ModelCheck || t.DoneWhen == PlanDoneWhen.HasWords
                ? "Thanks. Semanticus checks the model, and the task shows as done once the check passes."
                : "Marked done. The person sees your reply on the Plan.",
                // Nothing can have been done to an object that is not there (UAT S12-5-006).
                GoneRefusalAsync).ConfigureAwait(false);
        }

        /// <summary>decline_request: the task goes back to the person as Needs you, with the reason word for word,
        /// and "Give to your assistant" asks again.</summary>
        public async Task<HandoffResult> DeclineAssistantRequestAsync(string requestId, string claimToken, string reason, string reader = null, string[] choices = null)
        {
            // A part of a composer draft goes to the person with the choices that resolve (lane L4, composer 5.4).
            var part = await TryDeclineDraftPartAsync(requestId, claimToken, reason, choices, reader).ConfigureAwait(false);
            if (part != null) return part;
            var why = RequireReason(reason);
            return await AnswerAsync(requestId, claimToken, reader, (t, _) => Decline(t, why),
                _ => "Handed back to the person with your reason. They can give it to you again.").ConfigureAwait(false);
        }

        private const int MaxReplyChars = 4000;

        private static string RequireReason(string reason)
        {
            var why = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
            if (why == null) throw new ArgumentException("Say why it could not be done, in one line.", nameof(reason));
            if (why.Length > PlanTasks.MaxNoteChars)
                throw new ArgumentException("That reason is longer than " + PlanTasks.MaxNoteChars + " characters.", nameof(reason));
            return why;
        }

        /// <summary>Back to the person: open, no claim, the reason kept, and theirs to do until they give it again.</summary>
        private static void Decline(PlanTask t, string why)
        {
            t.Claim = null;
            t.State = PlanTaskStates.Open;
            t.DeclineReason = why;
            t.Doer = PlanDoers.You;
            t.Order = null;
            t.ReleaseNote = null;
        }

        private async Task<HandoffResult> AnswerAsync(string requestId, string claimToken, string reader,
                                                      Action<PlanTask, DateTimeOffset> change, Func<PlanTask, string> sentence,
                                                      Func<PlanTask, Task<string>> refusal = null)
        {
            var key = ReaderOrDoor(reader);
            var id = requestId?.Trim();
            await _queueGate.WaitAsync().ConfigureAwait(false);
            try
            {
                var bound = BindPlanMemory();
                var memory = ReadPlanState(bound);
                var task = memory.FindTask(id);
                if (task == null || PlanTasks.IsAim(task)) return Refused(HandoffRefusal.NotFound, NotFoundSentence(id));
                if (!PlanTasks.IsOpenTask(task))
                    return Refused(HandoffRefusal.AlreadyTerminal, "That task is already finished or set aside. Nothing was changed.", task);
                // Set aside while it was claimed: the person's decision stands over the claim (UAT S12-5-002).
                if (IsSetAside(memory, task))
                    return Refused(HandoffRefusal.AlreadyTerminal, SetAsideSentence, task);
                if (task.Claim == null || task.Claim.ClaimedBy == PlanDoers.You)
                    return Refused(HandoffRefusal.NotClaimed, "Claim " + task.Id + " with claim_request first, then answer it.", task);
                if (!string.Equals(task.Claim.ClaimTokenHash, HashToken(claimToken), StringComparison.Ordinal))
                    return Refused(HandoffRefusal.WrongClaimToken,
                        "That claim on " + task.Id + " is not the current one. Claim it again before answering.", task);
                var refused = refusal == null ? null : await refusal(task).ConfigureAwait(false);
                if (refused != null) return Refused(HandoffRefusal.NotReady, refused, task);

                var hash = task.Claim.ClaimTokenHash;
                var landed = false;
                WritePlanTasks(bound, state =>
                {
                    var mine = state.FindTask(task.Id);
                    if (mine?.Claim == null || !string.Equals(mine.Claim.ClaimTokenHash, hash, StringComparison.Ordinal)) return;
                    if (IsSetAside(state, mine)) return;
                    change(mine, DateTimeOffset.UtcNow);
                    mine.Claim = null;
                    landed = true;
                });
                if (!landed) return Refused(HandoffRefusal.WrongClaimToken,
                    "That claim on " + task.Id + " ended a moment ago. Nothing was changed.", task);

                ClearPointersTo(task.Id);
                await ComposeAsync(null, bound).ConfigureAwait(false);
                TellThePlanMoved(bound);
                var closed = ReadPlanState(bound).FindTask(task.Id);
                var request = RequestOf(closed, null, closed?.State == PlanTaskStates.Done
                    ? AssistantRequestState.Completed : AssistantRequestState.Declined);
                request.Answer = closed?.Reply;
                request.DeclineReason = closed?.DeclineReason;
                return new HandoffResult { Request = request, Message = sentence(closed ?? task) };
            }
            finally { _queueGate.Release(); }
        }

        internal const string SetAsideSentence =
            "This task was set aside by the person, so it is not yours to finish now. Nothing was changed.";

        /// <summary>A task is set aside by its own state or by the person's Set aside on its row, which is kept beside
        /// it as a skip and leaves the task's state as it was (a claim included). Both are the person's decision.</summary>
        internal static bool IsSetAside(PlanMemoryState memory, PlanTask task) =>
            task != null && (task.State == PlanTaskStates.SetAside || memory?.Find(task.Id) != null
                             || (task.Aliases ?? Array.Empty<string>()).Any(a => memory?.Find(a) != null));

        private void ClearPointersTo(string taskId)
        {
            foreach (var kv in _workingOn.Where(kv => string.Equals(kv.Value.Id, taskId, StringComparison.Ordinal)).ToList())
                _workingOn.TryRemove(kv);
        }

        private string NotFoundSentence(string id) =>
            RemovedSentenceFor(id)
            ?? "Nothing waiting for you is named '" + id + "'. Run list_assistant_requests to see what is offered.";

        private static HandoffResult Refused(HandoffRefusal why, string message, PlanTask task = null, ModelPlanItem row = null) =>
            new HandoffResult
            {
                Refusal = why,
                Message = message,
                Request = task == null ? null : RequestOf(task, row, task.Claim != null ? AssistantRequestState.Claimed : AssistantRequestState.Queued),
            };

        /// <summary>The grounding for a claimed task's objects (contract 3.4), as get_grounding gives it. Names,
        /// types, expressions and descriptions; no rows. Best effort: an object that cannot be read is left out.</summary>
        private async Task<Semanticus.Analysis.GroundingBundle[]> GroundingForAsync(string[] objects)
        {
            var list = new List<Semanticus.Analysis.GroundingBundle>();
            foreach (var o in (objects ?? Array.Empty<string>()).Take(12))
            {
                try { var g = await GetGroundingAsync(o).ConfigureAwait(false); if (g != null) list.Add(g); }
                catch { }
            }
            return list.Count == 0 ? null : list.ToArray();
        }

        // ---- the person's side: I'll take it, Mark done, Could not (contract 3.3) ------------------------------

        /// <summary>I'll take it: the person claims the task. The doer becomes You, the row reads On it "you took
        /// it", and it leaves the assistant's offer. A task the assistant holds cannot be taken: first claim wins.</summary>
        public Task<ModelPlanView> ClaimPlanTaskAsync(string id) =>
            PersonChangeAsync(id, (t, _) =>
            {
                if (t.Claim != null && t.Claim.ClaimedBy != PlanDoers.You)
                    throw new InvalidOperationException(
                        "Your assistant is working on this now. Wait for it to finish, or use Could not to take it back.");
                t.Claim ??= new PlanTaskClaim { ClaimedBy = PlanDoers.You, ClaimedUtc = DateTimeOffset.UtcNow, EngineRunId = EngineRunId };
                t.State = PlanTaskStates.Claimed;
                t.Doer = PlanDoers.You;
                t.Order = null;
                t.DeclineReason = null;
                t.ReleaseNote = null;
            });

        /// <summary>Mark done, with an optional reply shown as the person's. A task the engine can check reads
        /// Applied, checking until the check passes, exactly as after an assistant's completion.</summary>
        public Task<ModelPlanView> CompletePlanTaskAsync(string id, string reply)
        {
            var text = string.IsNullOrWhiteSpace(reply) ? null : reply.Trim();
            if (text != null && text.Length > MaxReplyChars)
                throw new ArgumentException("That reply is longer than " + MaxReplyChars + " characters.", nameof(reply));
            return PersonChangeAsync(id, (t, now) =>
            {
                if (t.Claim != null && t.Claim.ClaimedBy != PlanDoers.You)
                    throw new InvalidOperationException(
                        "Your assistant is working on this now. Wait for it to finish, or use Could not to take it back.");
                if (text != null) t.Reply = text;
                t.State = PlanTaskStates.Done;
                t.DoneUtc = now;
                t.DoneBy = PlanDoers.You;
                t.Claim = null;
                t.ReleaseNote = null;
            });
        }

        /// <summary>Could not: the claim is released (the person's, or the assistant's when the person takes a task
        /// back) and the row reads Needs you with the reason, offering Give to your assistant and Set aside.</summary>
        public Task<ModelPlanView> DeclinePlanTaskAsync(string id, string reason)
        {
            var why = RequireReason(reason);
            return PersonChangeAsync(id, (t, _) => Decline(t, why));
        }

        private async Task<ModelPlanView> PersonChangeAsync(string id, Action<PlanTask, DateTimeOffset> change)
        {
            if (string.IsNullOrWhiteSpace(id))
                throw new ArgumentException("Name the task. Call get_model_plan for the ids.", nameof(id));
            await _queueGate.WaitAsync().ConfigureAwait(false);
            try
            {
                var bound = BindPlanMemory();
                var memory = ReadPlanState(bound);
                var task = memory.FindTask(id.Trim())
                    ?? throw new ArgumentException("No task on the plan has that id. It may have been removed.", nameof(id));
                if (!PlanTasks.IsOpenTask(task) || IsSetAside(memory, task))
                    throw new InvalidOperationException("That task is already finished or set aside. Put it back on the plan first.");
                WritePlanTasks(bound, state =>
                {
                    var mine = state.FindTask(task.Id);
                    if (mine == null || !PlanTasks.IsOpenTask(mine) || IsSetAside(state, mine)) return;
                    change(mine, DateTimeOffset.UtcNow);
                });
                ClearPointersTo(task.Id);   // a task the person took or closed is nobody's working-on task
                var plan = await ComposeAsync(null, bound).ConfigureAwait(false);
                TellThePlanMoved(bound);
                return plan;
            }
            finally { _queueGate.Release(); }
        }

        // ---- the restart line -----------------------------------------------------------------------------------

        /// <summary>True when an assistant's claim in this state was made by an engine run that has ended.</summary>
        internal bool HasStaleClaims(PlanMemoryState state) =>
            state?.Tasks != null && state.Tasks.Any(t => IsStale(t.Claim));

        // Another run's id says only that another engine made the claim. It is stale when that engine is gone: another
        // workspace on the same model is a live engine, and taking its claim would hand one task to two assistants.
        private bool IsStale(PlanTaskClaim c) =>
            c != null && c.ClaimedBy != PlanDoers.You && !string.Equals(c.EngineRunId, EngineRunId, StringComparison.Ordinal)
            && !EngineRunLiveness.IsAlive(c.EngineRunId);

        /// <summary>Release every assistant claim an engine run that has ended made. The task goes back to Ready and
        /// says why, and the old token is gone with the claim.</summary>
        internal void ReleaseStaleClaims(PlanMemoryState state)
        {
            foreach (var t in state.Tasks.Where(t => IsStale(t.Claim)))
            {
                t.Claim = null;
                if (t.State == PlanTaskStates.Claimed) t.State = PlanTaskStates.Open;
                t.ReleaseNote = RestartSentence;
            }
        }

        // ---- posting, for the composer (contract 3.2) ------------------------------------------------------------

        /// <summary>A job's request for words becomes a task for the assistant, so it has an owner record from the
        /// start and waits with or without an assistant attached. Not a door operation: only a job posts.</summary>
        internal async Task<HandoffResult> PostJobRequestAsync(AssistantRequestPost post)
        {
            var words = post?.Need?.Trim();
            if (string.IsNullOrEmpty(words)) throw new ArgumentException("A request says what it needs.", nameof(post));
            if (words.Length > PlanTasks.MaxWordsChars) words = words.Substring(0, PlanTasks.MaxWordsChars);
            var bound = BindPlanMemory();
            string id = null;
            WritePlanTasks(bound, memory =>
            {
                id = PlanTasks.NewIdFor(memory);
                memory.Tasks.Add(new PlanTask
                {
                    Id = id,
                    Words = words,
                    Source = PlanSources.Workflow,
                    Doer = PlanDoers.Assistant,
                    DoneWhen = PlanDoneWhen.AssistantSays,
                    // A draft's part carries its packet and the objects it is about (composer contract 5.3, lane L4).
                    Brief = string.IsNullOrWhiteSpace(post.Brief) ? null
                          : post.Brief.Length <= PlanTasks.MaxBriefChars ? post.Brief : post.Brief.Substring(0, PlanTasks.MaxBriefChars),
                    Objects = post.Objects ?? Array.Empty<string>(),
                    OriginLink = new PlanOriginLink { Kind = PlanOriginKinds.Draft, Ref = post.JobId },
                    Order = PlanTasks.NextOrder(memory),
                    CreatedUtc = DateTimeOffset.UtcNow,
                    State = PlanTaskStates.Open,
                });
            });
            await ComposeAsync(null, bound).ConfigureAwait(false);
            TellThePlanMoved(bound);
            var task = ReadPlanState(bound).FindTask(id);
            return new HandoffResult { Request = RequestOf(task, null, AssistantRequestState.Queued) };
        }

        /// <summary>Set aside every open task a job posted: the job was cancelled, or consent withdrawn.</summary>
        internal Task<int> SetAsideJobRequestsAsync(Func<PlanTask, bool> which, string reason)
        {
            var n = 0;
            var bound = BindPlanMemory();
            WritePlanTasks(bound, memory =>
            {
                foreach (var t in memory.Tasks.Where(t => PlanTasks.IsOpenTask(t) && t.OriginLink?.Kind == PlanOriginKinds.Draft && which(t)))
                {
                    t.State = PlanTaskStates.SetAside;
                    t.DeclineReason = reason;
                    t.Claim = null;
                    t.DoneUtc = DateTimeOffset.UtcNow;
                    n++;
                }
            });
            foreach (var t in ReadPlanState(bound).Tasks.Where(t => !PlanTasks.IsOpenTask(t))) ClearPointersTo(t.Id);
            TellThePlanMoved(bound);
            return Task.FromResult(n);
        }

        /// <summary>The pending summary for the old appender block, read off the file with no compose.</summary>
        internal AssistantPendingSummary PendingPlanRequests()
        {
            try
            {
                if (_sessions.Current == null) return null;
                var memory = ReadPlanState(BindPlanMemory());
                if (memory.AssistantQueue == PlanAssistantQueue.Paused) return null;
                var open = memory.Tasks.Where(t => IsOfferCandidate(t) && !IsSetAside(memory, t)).OrderBy(t => t.Order ?? int.MaxValue).ToArray();
                if (open.Length == 0) return null;
                return new AssistantPendingSummary
                {
                    Pending = open.Length,
                    Requests = open.Select(t => new PendingRequestLine
                    {
                        Id = t.Id, Job = t.OriginLink?.Ref, Need = t.Words, WaitingSince = t.CreatedUtc.ToString("o"),
                    }).ToArray(),
                };
            }
            catch { return null; }
        }
    }
}
