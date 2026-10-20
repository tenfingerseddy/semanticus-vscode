// propose, approve, cancel on one plan item (docs/redesign/contracts/change-contract.md, M3).
//
// LocalEngine.PlanChanges.cs holds the shapes, the projection and the digest. This file holds the three
// verbs that move a set between states, and the one place that works out what an item's changes actually
// are.
//
// WHAT PROPOSE WILL AND WILL NOT DO, said plainly because the refusals are most of the behaviour.
//
// A diff is a change or it is nothing. An item only proposes when every line it produces is a change the
// engine can actually make, with a real before and a real after. Two families are:
//
//   safefix/<category>   the deterministic readiness fixes, one line per object, through the SAME mapping
//                        propose_plan already uses (MapSafeFix). Not a second copy of it: this file calls it.
//   cleanup/model        every unused column the sweep called safe, as delete_if_unused lines. The ones the rule suggests
//                        (and Jev did not leave out) arrive TICKED and marked Suggested; the rest arrive unticked, each
//                        with its reason. Run the plan never applies a Suggested line: the person looks first.
//
// Everything else refuses, and says which:
//
//   describe/model       descriptions are words. Words are the assistant's work, not a change the engine can
//                        name, so this is a handoff and not a proposal. The assistant proposes onto this same
//                        item through propose_plan or add_plan_item and the lines land here.
//   proposal/<family>    the rules have an opinion and the person has the answer. "Rename it or hide it" is
//                        not a change; it is a choice with two changes behind it. Proposing it would promise
//                        an approve that cannot happen, so the row keeps saying what it says and names the
//                        page where the choice is made.
//   everything else      no mapping exists, and the refusal says so rather than inventing one.
//
// A refusal is not silence. What propose LOOKED AT and left out goes on the set's refused list with the
// reason, so a person reads it before deciding instead of in a report afterwards. That list is the whole
// point of the contract's Refused half.
//
// APPROVE IS ALL OF THE KEPT LINES OR NONE, AND THAT IS STRICTER THAN apply_plan.
// ApplyPlanAsync applies what it can and reports what it could not: a line that throws is marked failed and
// the batch carries on, and a line the engine declines at apply time (a value that moved, an unused verdict
// that no longer holds, a DAX rewrite that could not be proven) is marked skipped and the batch carries on.
// That is right for apply_plan, which is a batch tool. It is NOT what approve promises. So approve checks
// the report and, if ANY kept line did not apply, undoes the whole batch in one step and says which line and
// why. The undo is exact because apply_plan runs inside one MutateAsync, so the batch is one undo entry.
// There is no outcome in which some of the kept lines are in and some are not.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Analysis;
using Semanticus.Decisions;
using Semanticus.Engine.Lineage;
using TabularEditor.TOMWrapper;

namespace Semanticus.Engine
{
    public sealed partial class LocalEngine
    {
        /// <summary>Runs once, after an approval has passed every check it can make before the write and
        /// before the batch is queued. A test seam; see the note at its call site.</summary>
        internal Func<Task> ApprovalQueuedForTest;

        /// <summary>Runs once, inside Run the plan, just after the Ready digest was checked and before the batch is
        /// worked out and applied. A test seam: it is where a person's pause, doer switch or skip used to be able
        /// to land unseen (Sol 6, slice 7, finding 1). Null on every real path and consumed once.</summary>
        internal Func<Task> RunReadyCheckedForTest;

        /// <summary>Runs once, inside propose or cancel, at the moment the verb has finished its work and is
        /// about to record the outcome in the plan memory. A test seam, and the only way to prove the verb
        /// records against the model it bound at its TOP rather than against whichever one is open when the
        /// compose begins: ComposeAsync takes its own binding before it reaches PlanComposePauseForTest, so a
        /// pause inside the compose cannot stand in for this one. Null on every real path, consumed once so
        /// it cannot leak into the next call, and nothing in the engine branches on it. (Same shape as
        /// <see cref="ApprovalQueuedForTest"/>.)</summary>
        internal Func<Task> PlanVerbWriteForTest;

        /// <summary>Runs once, at the moment a verb has decided to REFUSE and before the refusal is recorded.
        /// A test seam, and the only way to stand in the window this bug lived in: between the verb taking
        /// its binding and the refusal composing. The other two seams cannot reach it, because both of them
        /// sit on a verb's success path. Null on every real path, consumed once so it cannot leak into the
        /// next call, and nothing in the engine branches on it.</summary>
        internal Func<Task> PlanRefusalWriteForTest;

        private async Task VerbWritePauseAsync()
        {
            var pause = System.Threading.Interlocked.Exchange(ref PlanVerbWriteForTest, null);
            if (pause != null) await pause().ConfigureAwait(false);
        }

        private async Task RefusalWritePauseAsync()
        {
            var pause = System.Threading.Interlocked.Exchange(ref PlanRefusalWriteForTest, null);
            if (pause != null) await pause().ConfigureAwait(false);
        }

        // ---- one binding per verb -----------------------------------------------------------------------
        //
        // WHY EVERY VERB IN THIS FILE BINDS AT ITS TOP (Astra 6 on the Fix slice, the change-set half).
        // A verb reads the open session at its top, works out its lines or writes the model, and only then
        // records what it did. ComposeAsync binds the plan memory at the moment it is CALLED, which is the
        // later moment, so a model exchanged in between put the history entry and the set's state into the
        // other model's memory, and the caller was handed whatever exception the other model's context threw
        // rather than a sentence. So each verb takes one binding first, reads the session through it, and
        // hands it to every compose it makes. The targets half of this slice does the same thing the same
        // way (Plan/PlanTargets.cs), and both write through the one seam in LocalEngine.Plan.cs.

        /// <summary>Compose the plan of the BOUND model, or answer null because that model is no longer the
        /// open one. Null is never an error here: it is the whole of the "nothing was kept" answer, and the
        /// caller turns it into a sentence. The plan of whatever model is open instead is deliberately not
        /// returned, because it is not the plan the caller asked about.
        ///
        /// The check is made twice on purpose. Once here, so the ordinary case is a sentence rather than an
        /// exception, and once inside WritePlanMemory, which is the only place that can be sure: the compose
        /// waits on two gates after this check and the model can be exchanged while it waits.</summary>
        private async Task<ModelPlanView> ComposeBoundAsync(PlanMemoryBinding bound, Action<PlanMemoryState> change)
        {
            if (!PlanMemoryStillOpen(bound)) return null;
            try
            {
                return await ComposeAsync(change, bound).ConfigureAwait(false);
            }
            catch (InvalidOperationException ex)
                when (string.Equals(ex.Message, PlanMemoryModelSwitchedSentence, StringComparison.Ordinal))
            {
                return null;
            }
        }

        /// <summary>The compose that follows a verb's WRITE to the model (approve, Run the plan) or its rollback. Changes are already in the
        /// model by then, so a model with nowhere to keep the record (built, never saved) must not turn them into a failure: the record
        /// is skipped, the plan is still composed, and the sentence says so (<see cref="UnsavedNote"/>). M11 join part 4, E1.</summary>
        private Task<ModelPlanView> ComposeAfterWriteAsync(PlanMemoryBinding bound, Action<PlanMemoryState> change) =>
            ComposeBoundAsync(bound, bound.Nowhere ? null : change);

        /// <summary>Said after a write to a model that has never been saved: the changes are in the open model and undoable, and the Plan
        /// has no file to keep its record in. Plain words, no blame on the person.</summary>
        internal const string UnsavedNote =
            "This model is not saved to a file yet. The changes are only in the open model, and the Plan does not keep them. Save the model to keep them.";

        /// <summary>What a person is told when the model was exchanged while their verb was in flight. The
        /// wording is the one the plan memory itself refuses with, shared rather than restated, so the two
        /// halves of the slice cannot come to say different things about the same event. The plan is left
        /// null for the reason given on ComposeBoundAsync.</summary>
        private static ModelPlanActionResult ModelSwitchedDuringVerb(ModelPlanItem item, string verb, long revision) =>
            new ModelPlanActionResult
            {
                ItemId = item.Id,
                Action = verb,
                Performed = false,
                Revision = revision,
                Plan = null,
                Sentence = PlanMemoryModelSwitchedSentence,
            };

        // ---- propose ------------------------------------------------------------------------------------

        /// <summary>Hold the lines somebody wrote over, and say on each whether the object it names is still there and still holds what it was
        /// written over (<see cref="RecheckHeldLinesAsync"/>), so the plan shows the right reason at once and not only after the next refresh.
        /// The reason a line carries when nothing is wrong with it is <paramref name="reason"/>. Each line is held once, for the reason the
        /// recheck finds, so a tick the person gave a line already held for that reason stays (<see cref="JevFamily.HoldAuthored"/>);
        /// <paramref name="newPlan"/> takes every tick back first, because nothing on a new plan was ticked yet.</summary>
        private async Task HoldKeptAsync(Session s, List<ChangeItem> kept, string reason, bool newPlan = false)
        {
            if (kept.Count == 0) return;
            if (newPlan) foreach (var line in kept) JevFamily.HoldAuthored(line, reason, keepTick: false);
            await RecheckHeldLinesAsync(s, kept, reason).ConfigureAwait(false);
        }

        /// <summary>A NEW PLAN KEEPS A PERSON'S WORDS. The seeds go on the new plan as always, then every line from the old one that carried
        /// somebody's words (<see cref="ChangeItem.AuthoredAfter"/>) comes across, held. Nothing is merged into a seed: a seed that writes the
        /// same thing sits beside the held line and the person chooses. Called under the plan gate.</summary>
        private async Task AddSeedsKeepingAuthoredAsync(Session s, ChangePlanState plan, List<ChangeItem> kept, IEnumerable<ChangeItem> seeds, long revision, string sourceVersion)
        {
            foreach (var seed in seeds) plan.Add(seed, revision, sourceVersion);
            if (kept.Count == 0) return;
            await HoldKeptAsync(s, kept, JevFamily.HeldCarriedReason, newPlan: true).ConfigureAwait(false);
            foreach (var line in kept) plan.Adopt(line);
        }

        /// <summary>Work out the exact changes for one item and store them. WRITES NOTHING, and the model
        /// revision does not move, so the plan's remembered facts stay valid and the next compose is
        /// arithmetic.
        ///
        /// Proposing again REPLACES what was there. That is what "work them out again" means for a set that
        /// has gone out of date, and it is the only honest answer: the old lines were worked out against a
        /// model that has moved, so keeping them and adding to them would hand a person a diff that is half
        /// true. Anything they kept or dropped goes with it, and the sentence says the changes were worked
        /// out again.</summary>
        private async Task<ModelPlanActionResult> ProposeItemAsync(
            Session s, ModelPlanItem item, long readAt, string origin)
        {
            // One binding, taken before the first await, and used for every read of the session below.
            var bound = BindPlanMemory();

            if (string.Equals(item.Status, ModelPlanStatuses.Skipped, StringComparison.Ordinal))
                return await RefusedAsync(item, "propose", s.Revision, origin,
                    "This task is set aside. Put it back first, then work out its changes.", bound).ConfigureAwait(false);

            // The readiness scan the plan already paid for, not a fresh one.
            var sourceVersion = SourceVersionOf(s);
            var card = await PlanReadinessAsync(s).ConfigureAwait(false);
            // The cleanup row lists EVERY unused column and starts some ticked (CleanupGuard), then, on Pro, Jev may untick some of
            // those (CleanupSuggest). The wait for Jev is bounded and is one the person started by pressing Work it out.
            CleanupSweep cleanup = null;
            if (IsCleanupItem(item.Id))
            {
                cleanup = await PlanUnusedAsync(s).ConfigureAwait(false);
                await AskCleanupJevAsync(s, cleanup).ConfigureAwait(false);
            }
            var unused = cleanup?.Listed;
            var scope = ReportScopeSignature(s);

            // Everything below reads the model and nothing writes to it.
            var removed = RemovedMeasureRefs(s);
            var collected = await s.ReadAsync(m => CollectLinesForItem(m, item, card, unused, scope, removed, cleanup))
                                   .ConfigureAwait(false);

            if (collected.Seeds.Count == 0)
                return await RefusedAsync(item, "propose", s.Revision, origin,
                    NothingToProposeSentence(item, collected.Refused), bound).ConfigureAwait(false);

            // The context this verb bound at its top, not whichever one is current now. Reading it again
            // here would be a second chance to straddle an exchange, and EnsureContextCurrent below is what
            // turns a stale one into a refusal rather than a write on the wrong change plan.
            var context = bound.Context;
            // The model must still be the one the lines were worked out from. Checked here and, for the
            // change plan itself, by EnsureContextCurrent under its own gate.
            if (!ReferenceEquals(_sessions.Current, s) || s.Revision != readAt)
                return await RefusedAsync(item, "propose", s.Revision, origin,
                    "The model changed while Semanticus worked these out, so nothing was suggested. "
                    + "Open the task again to get new changes, then press Run the plan.", bound).ConfigureAwait(false);

            ModelPlanChangeLine[] snapshot;
            await context.PlanGate.WaitAsync().ConfigureAwait(false);
            try
            {
                EnsureContextCurrent(context, "Plan proposal");
                var plan = context.Plans.GetOrStart();
                // Replace, never add to. See the note on this method. The lines to replace are the ones the
                // item's own set displayed, by their handles, for the reason spelled out in CancelItemAsync:
                // a membership rule computed a second time here could disagree with the one that filed them.
                var replacing = new HashSet<string>(
                    (item.ChangeSet?.Lines ?? Array.Empty<ModelPlanChangeLine>())
                        .Select(l => l.PlanItemId)
                        .Where(id => !string.IsNullOrWhiteSpace(id)),
                    StringComparer.OrdinalIgnoreCase);
                // A line somebody wrote over is not the redraw's to replace: it stays, held (Sol 6.1 on the M11 delta, the verifier's join item 2).
                var kept = replacing.Count == 0 ? new List<ChangeItem>()
                    : plan.Snapshot().Where(l => l.AuthoredAfter != null && replacing.Contains(l.Id ?? "")).ToList();
                if (replacing.Count > 0) plan.RemoveSynced(line => replacing.Contains(line.Id ?? ""));
                foreach (var seed in collected.Seeds) plan.Add(seed, readAt, sourceVersion);
                if (kept.Count > 0) await HoldKeptAsync(s, kept, JevFamily.HeldCarriedReason, newPlan: true).ConfigureAwait(false);
                snapshot = collected.Seeds.Select(LineOf).ToArray();
                PublishPlan(plan, s.Revision);
            }
            finally { context.PlanGate.Release(); }

            var header = new PlanChangeHeader
            {
                JobId = "modelplan/" + item.Id,
                SessionId = s.Id,
                State = ModelPlanChangeStates.Proposed,
                Revision = readAt,
                SourceVersion = sourceVersion,
                StaleAtProposal = s.Revision != readAt,
                ProposedAt = DateTimeOffset.UtcNow,
                Refused = collected.Refused.ToArray(),
                Lines = snapshot,
            };

            var count = collected.Seeds.Count;
            var line = "Worked out " + CountOf(count, "change", "changes") + " for " + Lower(item.Title)
                     + ". Nothing was written.";
            await VerbWritePauseAsync().ConfigureAwait(false);
            var after = await ComposeBoundAsync(bound, memory =>
            {
                memory.Changes[item.Id] = header;
                memory.History.Add(Entry(ModelPlanHistoryKinds.Proposed, ActorOf(origin), item.Id, item.Title,
                    readAt, line, snapshot.Select(l => l.Line).ToArray()));
            }).ConfigureAwait(false);
            // Nothing was written to the model by a propose, so a set that could not be recorded is a set
            // that never happened, and saying so is the whole truth.
            if (after == null) return ModelSwitchedDuringVerb(item, "propose", s.Revision);

            return new ModelPlanActionResult
            {
                ItemId = item.Id,
                Action = "propose",
                // Nothing was written. "Performed" means the MODEL changed, and it did not.
                Performed = false,
                Revision = s.Revision,
                Plan = after,
                Sentence = CountOf(count, "change is", "changes are") + " ready for you to look at. "
                         // Approving Semanticus's own row is Pro (ModelPlanActionAsync), so free is not told to approve
                         // it (verify m11/plan-rows L8).
                         + (string.Equals(item.Who, ModelPlanOwners.Auto, StringComparison.Ordinal)
                            && !Entitlement.FeatureGrants.IsProTier(_entitlement)
                            ? "Nothing is changed yet. Approving them needs Semanticus Pro. Open "
                              + (item.Jump?.Label ?? "the page it names") + " to make them yourself."
                            : "Nothing is changed yet. Keep the ones you want, drop the rest, then press Approve.")
                         + (collected.Refused.Count == 0 ? ""
                            : " " + CountOf(collected.Refused.Count, "other thing was", "other things were")
                              // The row lists them (its change set's refused list), on the page and in the row's own read
                              // on the agent door; the short answer does not (verify R5 L-2).
                              + " left out. The task lists each one with the reason."),
            };
        }

        /// <summary>Why nothing could be proposed, in the words of the family that could not propose. Never a
        /// shrug: every branch names what the thing needs and where a person goes to do it.</summary>
        private static string NothingToProposeSentence(ModelPlanItem item, IReadOnlyList<ModelPlanChangeRefusal> refused)
        {
            var place = item.Jump?.Label ?? "the page it points at";

            // A proposal the assistant does (a rename, a rewrite, a setting): a choice case by case, not words and not one exact change.
            if (IsProposalItem(item.Id) && string.Equals(item.Who, ModelPlanOwners.Assistant, StringComparison.Ordinal))
                return "This needs a choice, case by case, so your assistant makes it, not Semanticus. "
                     + "Ask your assistant to take this task. What it writes comes back here for you to approve.";

            if (string.Equals(item.Who, ModelPlanOwners.Assistant, StringComparison.Ordinal))
                return "This task needs words, so your assistant writes them, not Semanticus. "
                     + "Ask your assistant to do it. What it writes comes back to this task for you to approve.";

            if (IsProposalItem(item.Id))
                return "This needs your choice, case by case, so Semanticus cannot suggest one change. "
                     + "Open " + place + " and choose.";

            return refused.Count == 0
                ? "Semanticus cannot suggest an exact change for this yet. Open " + place + " and do it there."
                : CountOf(refused.Count, "thing was", "things were") + " looked at, but Semanticus could not suggest "
                  + "an exact change for any. Open " + place + " and do it there.";
        }

        // ---- approve ------------------------------------------------------------------------------------

        /// <summary>Apply the kept lines of one item's change set, all of them or none, on one undo entry.
        ///
        /// THE ORDER OF CHECKS IS THE CONTRACT'S ORDER and it is load-bearing
        /// (change-contract.md, "the order of checks on accept, and why it is that order"):
        ///
        ///   1. ALREADY APPLIED wins over everything, even over a model that has since moved, because the
        ///      honest answer to "did my change land?" is yes and not a refusal. Nothing is applied twice.
        ///   2. NOTHING PROPOSED, or the set was dropped.
        ///   3. THE SESSION MOVED. The lines live on the session's change plan, so a set worked out in a
        ///      session that has closed has nothing behind it, whatever its revision says.
        ///   4. THE DIGEST MOVED: what is on the plan now is not what was approved.
        ///   5. THE REVISION MOVED, then THE SOURCE MOVED, each named separately so the sentence says which.
        ///
        /// Steps 1 and 2 come before step 5 or a double submit would say "the model changed" when the
        /// truthful answer is "your change is already in".</summary>
        private async Task<ModelPlanActionResult> ApproveItemAsync(
            Session s, ModelPlanItem item, string digest, long readAt, string origin)
        {
            // One binding, taken before the first await, carried through the write and into both records.
            var bound = BindPlanMemory();
            var set = item.ChangeSet;
            var quoted = (digest ?? "").Trim();

            // Checks 1 to 5, in the contract's order, shared with apply_plan_items so the two cannot come to
            // disagree about what a set that can be approved looks like (ApprovalPrecheck).
            var check = ApprovalPrecheck(item, quoted);
            if (check.Outcome != ApprovalOutcome.Ready)
                return await RefusedAsync(item, "approve", s.Revision, origin, check.Sentence, bound).ConfigureAwait(false);
            var kept = check.Kept;

            // The way back. Under today's promise this is the session undo timeline, which always exists, so
            // there is no RefusedNoCheckpoint branch here on purpose (LocalEngine.PlanChanges.cs).
            var checkpoint = await CheckpointForApproveAsync(s, item).ConfigureAwait(false);
            if (checkpoint.Refusal != null)
                return await RefusedAsync(item, "approve", s.Revision, origin, checkpoint.Refusal, bound).ConfigureAwait(false);

            // WHAT WAS APPROVED, CARRIED INTO THE WRITE. Everything checked above was checked on a model
            // that can still move before the batch runs, so none of it is trusted on its own: the accepted
            // lines, their exact values, the versions and the session travel with the work and are checked
            // again on the single-writer dispatcher, inside the transaction that writes
            // (Plan/PlanApprovalGuard.cs). That also makes the batch all of the kept lines or none, and its
            // rollback is the batch's own, so approve never calls undo.
            var guard = GuardFor(s, item, set, kept, readAt);

            // A TEST SEAM, and the only way to prove the thing this guard exists for. Every check above runs
            // before the batch is queued, so a test that edits between them and the write has nowhere to
            // stand: it needs to run at exactly the moment a second door would. Neither door reaches this,
            // nothing in the engine branches on it, and it is consumed once so it cannot leak into the next
            // call. (Same shape as PlanSeedsReadyForTest on the propose path.)
            var between = System.Threading.Interlocked.Exchange(ref ApprovalQueuedForTest, null);
            if (between != null) await between().ConfigureAwait(false);

            // ONE APPROVAL AT A TIME, FROM THE RECHECK TO THE RECEIPT, AND THAT SPAN IS THE FIX.
            //
            // The already-applied check at the head of this method reads the item off a plan the CALLER
            // composed, which is a moment that has already passed. When both doors approve the same digest
            // at once they both compose a proposed snapshot before either of them gets here: the first
            // applies, and the second, still holding its stale picture, finds no approved lines left on the
            // change plan, is refused as a set that could not be made, and its catch path below overwrites
            // the first call's Applied header with RolledBack. The person is then told their change was put
            // back when it is in the model.
            //
            // So the recheck, the write and the receipt happen under one gate, and the recheck is made HERE
            // rather than earlier, against what is stored rather than against what was composed. Whichever
            // approval reaches the gate second reads the first one's receipt and answers "already in",
            // which is the truth, and never touches the header.
            await _planApproveGate.WaitAsync().ConfigureAwait(false);
            try
            {
                // ASKED AGAIN, AND ASKED OF THE RECORD. Check 1 at the head of this method is the same
                // question put to a snapshot; this is it put to the stored receipt, after any other
                // approval has finished writing one. It asks about THIS SET and not about this item: an
                // item id names a family ("describe/model"), work keeps arriving under it after an earlier
                // set has run, and refusing that new work because the family once applied would be the
                // receipt-as-proposal mix-up the other way round.
                if (AlreadyAppliedNow(bound, item.Id, quoted))
                    return await RefusedAsync(item, "approve", s.Revision, origin,
                        "These changes are already made, so nothing was changed a second time. Undo takes them back.", bound).ConfigureAwait(false);

                // THE MODEL THIS APPROVAL IS ABOUT MUST STILL BE THE OPEN ONE, AND THE CHECK IS HERE RATHER
                // THAN ONLY AT THE RECORD. This is the last instant at which nothing has been written, so it
                // is the only place the one sentence is the whole truth: nothing applied and nothing kept.
                // Left to the record, an exchange got as far as ApplyPlanCoreAsync against the OTHER model's
                // context, which has no change plan of its own, and the caller was handed that context's
                // exception instead of a sentence. PlanApprovalGuard stays exactly as it is: it is the check
                // inside the transaction, and it is what makes the batch safe on the writer thread. This is
                // the check before it.
                if (!PlanMemoryStillOpen(bound))
                    return ModelSwitchedDuringVerb(item, "approve", s.Revision);

                ApplyPlanReport report;
                try
                {
                    // Contract 8.4: the commit carries the item it served and who approved it, stamped as it is written.
                    using var cause = ChangeCause.Begin(ChangeCauseKinds.PlanItem, item.Title, ChangeBy.ApproverFor(origin), item.Id);
                    // A format the row's own other lines made unprovable is left out with its note and the rest goes in (THE FORMAT FILL
                    // RULE, addendum 4 item 25): the best-practice row that stops summing a column and fills the measure that summed it
                    // is the usual case. Every other line that cannot be made still puts the whole set back.
                    report = await ApplyPlanCoreAsync(kept.Select(l => l.PlanItemId).ToArray(),
                                                      MutationOrigin(origin), null, null, new[] { guard }, keepGoingPastFormat: true).ConfigureAwait(false);
                }
                catch (PlanApprovalRefusedException refused)
                {
                    // Nothing was written, or what was written has already been rolled back by the batch itself.
                    var rolledBack = await ComposeAfterWriteAsync(bound, memory =>
                    {
                        // A set the assistant put on the plan has no stored header, so one is written here
                        // rather than the outcome being lost.
                        RememberRollback(memory, item, set, refused);
                        memory.History.Add(Entry(ModelPlanHistoryKinds.Refused, ActorOf(origin), item.Id, item.Title,
                            s.Revision, "Nothing was changed on " + Lower(item.Title) + ".", new[] { refused.Message }));
                    }).ConfigureAwait(false);
                    // The model was exchanged in the narrow window after the batch refused. Nothing was changed
                    // and nothing was kept, which is the same answer, so it is said in the same words.
                    if (rolledBack == null) return ModelSwitchedDuringVerb(item, "approve", s.Revision);

                    return new ModelPlanActionResult
                    {
                        ItemId = item.Id, Action = "approve", Performed = false, Revision = s.Revision, Plan = rolledBack,
                        // The exception's message IS the sentence: every one of them is written as a complete
                        // thing to say to a person and every one ends by saying nothing was changed.
                        Sentence = refused.Message,
                    };
                }

                // Count the committed result, before the slower plan refresh.
                NoteProFixes(s.Id, item, (report.Items ?? Array.Empty<ChangeItem>()).Count(i =>
                    string.Equals(i.Status, "applied", StringComparison.OrdinalIgnoreCase)), origin: origin);
                // The automatic format lines this approval left out (never written), named with their own reason.
                var leftOut = (report.Items ?? Array.Empty<ChangeItem>())
                    .Where(i => string.Equals(i.Status, "skipped", StringComparison.OrdinalIgnoreCase) && IsAutomaticFormatWrite(i)).ToList();
                var leftOutIds = new HashSet<string>(leftOut.Select(i => i.Id ?? ""), StringComparer.OrdinalIgnoreCase);
                var made = kept.Where(l => !leftOutIds.Contains(l.PlanItemId ?? "")).ToArray();
                var left = SkippedSentence(leftOut.Select(i =>
                {
                    var skip = Skip(item, string.IsNullOrWhiteSpace(i.ObjectName) ? i.ObjectRef : i.ObjectName, i.Note);
                    skip.Kind = "format";
                    return skip;
                }).ToList());

                var applied = await ComposeAfterWriteAsync(bound, memory =>
                {
                    var h = HeaderFor(memory, item, set);
                    h.State = ModelPlanChangeStates.Applied;
                    h.AppliedAt = DateTimeOffset.UtcNow;
                    h.AppliedDigest = set.Digest;
                    h.Checkpoint = checkpoint.Id;
                    h.Lines = set.Lines ?? Array.Empty<ModelPlanChangeLine>();
                    memory.History.Add(Entry(ModelPlanHistoryKinds.Approved, ActorOf(origin), item.Id, item.Title,
                        report.Revision,
                        "Approved " + CountOf(made.Length, "change", "changes") + " on " + Lower(item.Title) + ".",
                        made.Select(l => l.Line).ToArray()));
                }).ConfigureAwait(false);

                // THE ONE PLACE THE SWITCHED-MODEL SENTENCE WOULD BE A LIE, so it is not used here. The batch
                // has already committed to the model this approval was about, and the only thing the exchange
                // cost is the record of it. Saying "nothing was kept" would tell a person their changes did not
                // land when they did, which is the same class of untruth the already-applied check at the head
                // of this method exists to prevent. So the outcome is reported as what it is, and the plan is
                // left null because the plan of the model that is open instead is not the one they asked about.
                //
                // The honest cost, named rather than papered over: that model's memory has no Applied header for
                // this set, so its next plan still shows the set as proposed. It cannot be approved a second
                // time, because the batch moved the revision and the set is refused as out of date, and undo
                // still puts the changes back in one step. Losing the record is the small half of this trade;
                // writing the record into another model is the large half, and that is what is refused.
                return new ModelPlanActionResult
                {
                    ItemId = item.Id,
                    Action = "approve",
                    Performed = true,
                    Revision = report.Revision,
                    Plan = applied,
                    Sentence = CountOf(made.Length, "change was", "changes were") + " made in one step. "
                             + (left.Length == 0 ? "" : left + " ")
                             + (report.GradeAfter == null ? "" : "The AI readiness grade is now " + report.GradeAfter + ". ")
                             + "One undo takes " + (made.Length == 1 ? "it" : "them all") + " back."
                             + (bound.Nowhere ? " " + UnsavedNote : ""),
                };
            }
            finally { _planApproveGate.Release(); }
        }

        /// <summary>The one gate an approval's recheck, write and receipt are all made under, so a second
        /// approval of the same set cannot read the plan before the first one has finished recording what it
        /// did. Engine-wide rather than per session because the engine holds one open model at a time, and
        /// an approval is bound to that model anyway; nothing taken inside it ever asks for it again, which
        /// is what keeps it deadlock-free beside the change plan gate and the plan memory gate.</summary>
        private readonly System.Threading.SemaphoreSlim _planApproveGate = new System.Threading.SemaphoreSlim(1, 1);

        /// <summary>A person's change that moves the Ready set (a pause, a doer switch, a skip or its undoing, a set
        /// worked out or dropped) runs under the approve gate, so it can never land between Run the plan's digest
        /// check and its commit (Sol 6, slice 7, finding 1). A control made during a run waits for the run and lands
        /// after it; the next read shows both. Only a scoped row-control batch reuses its already-held gate.</summary>
        private async Task<T> UnderReadyGateAsync<T>(Func<Task<T>> change)
        {
            if (_planRowBatch.Value != null)
            {
                if (!PlanMemoryStillOpen(_planRowBatch.Value.Bound)) throw new InvalidOperationException(BulkPlanModelSwitchedSentence);
                return await change().ConfigureAwait(false);
            }
            await _planApproveGate.WaitAsync().ConfigureAwait(false);
            try { return await change().ConfigureAwait(false); }
            finally { _planApproveGate.Release(); }
        }

        /// <summary>Whether the model this approval is bound to has ALREADY recorded THIS SET as applied.
        /// Read from the stored receipt rather than from the plan the caller composed, because that plan was
        /// composed before the other door's approval and reading it again is the whole point.
        ///
        /// THE DIGEST IS WHAT MAKES IT THIS SET. An item id names a family and new work keeps arriving under
        /// it, so an item whose earlier set applied is not an item with nothing left to do. The receipt
        /// records the digest it applied, and only that digest coming back is a second submit of the same
        /// set.
        ///
        /// A model with nowhere durable to keep anything has no receipt to read, and neither has a receipt
        /// written before the applied digest was recorded, so both answer false and the ordinary path runs.
        /// That is the honest limit of a check that reads a file: two approvals of an unsaved model still
        /// race, and the write itself is still all-or-none under the guard.</summary>
        private bool AlreadyAppliedNow(PlanMemoryBinding bound, string itemId, string digest) =>
            PlanMemoryStillOpen(bound)
            && !string.IsNullOrWhiteSpace(digest)
            && PlanMemory.Read(bound.File, bound.Identity).Changes.TryGetValue(itemId ?? "", out var stored)
            && stored != null
            && string.Equals(stored.State, ModelPlanChangeStates.Applied, StringComparison.Ordinal)
            && string.Equals(stored.AppliedDigest ?? "", digest, StringComparison.OrdinalIgnoreCase);

        /// <summary>The stored header for an item, made if there is not one yet. An assistant proposal has
        /// no header until something happens to it: the lines went on the change plan and nobody called
        /// propose, so the set a person read was stamped for that one answer and thrown away. The moment the
        /// set is applied or rolled back that outcome has to be remembered, so the header is written now,
        /// carrying the versions the set a person actually read was stamped with.</summary>
        private static PlanChangeHeader HeaderFor(PlanMemoryState memory, ModelPlanItem item, ModelPlanChangeSet set)
        {
            if (memory.Changes.TryGetValue(item.Id, out var existing) && existing != null) return existing;
            var header = new PlanChangeHeader
            {
                JobId = set.Id,
                SessionId = set.SessionId,
                State = set.State,
                Revision = set.Revision,
                SourceVersion = set.SourceVersion,
                StaleAtProposal = set.StaleAtProposal,
                ProposedAt = set.ProposedAt,
                Lines = set.Lines ?? Array.Empty<ModelPlanChangeLine>(),
                Refused = set.Refused ?? Array.Empty<ModelPlanChangeRefusal>(),
            };
            memory.Changes[item.Id] = header;
            return header;
        }

        /// <summary>Marks a set rolled back and keeps the line that stopped it, with its reason.</summary>
        private static void RememberRollback(PlanMemoryState memory, ModelPlanItem item, ModelPlanChangeSet set,
                                             PlanApprovalRefusedException refused)
        {
            var h = HeaderFor(memory, item, set);
            h.State = ModelPlanChangeStates.RolledBack;
            h.FailedLineId = refused.Line == null ? null : LineIdFor(refused.Line);
            h.FailedAfter = refused.Line?.After;
            h.FailureReason = refused.Line?.Note ?? refused.Message;
        }

        /// <summary>The way back approve promises, made before anything is written.
        ///
        /// Today that is the session undo timeline and it always exists, so this never refuses. It is a
        /// method rather than a constant because the other promise is a real option Kane may choose
        /// (change-contract.md), and that choice must be one branch here and not a rewrite of approve.</summary>
        private async Task<(string Id, string Refusal)> CheckpointForApproveAsync(Session s, ModelPlanItem item)
        {
            if (ApproveCheckpointPromise == CheckpointPromise.SessionUndo)
                return ("undo:" + s.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture), null);

            var made = await CreateHistoryCheckpointAsync(
                "Before approving changes for " + item.Title, commit: true, origin: "human").ConfigureAwait(false);
            return string.IsNullOrEmpty(made?.Error)
                ? (made.Checkpoint?.Hash ?? "checkpoint", null)
                : (null, made.Error + " Nothing was applied.");
        }

        // ---- what approve checks before it writes, shared ---------------------------------------------

        private enum ApprovalOutcome { Ready, AlreadyIn, NothingKept, Refused }

        /// <summary>Checks 1 to 5 of approve, in the contract's order (see ApproveItemAsync), for one item and
        /// the digest quoted for it. Pure: it reads the composed item and writes nothing. Approve turns every
        /// outcome but Ready into a refusal; apply_plan_items treats AlreadyIn and NothingKept as "nothing to
        /// add from this item" and every Refused as a refusal of the whole list.</summary>
        private static (ApprovalOutcome Outcome, string Sentence, ModelPlanChangeLine[] Kept) ApprovalPrecheck(
            ModelPlanItem item, string quoted)
        {
            var set = item.ChangeSet;

            // 1. Already applied, and this comes FIRST even when the model has since moved, because the
            //    honest answer to "did my change land?" is yes and not a refusal about a revision.
            //
            //    A row that reads DONE is the same answer arriving by the other road: the set applied, the
            //    gap it named went with it, and the plan rebuilt the row as finished rather than as work.
            //    That row carries no set at all (nothing stamps done; the absence of the gap does), so
            //    without this branch a second approve would say "nothing was ever worked out", which is
            //    false and is the exact double-submit lie the contract puts this check first to avoid.
            if (string.Equals(item.State, ModelPlanItemStates.Done, StringComparison.Ordinal))
                return (ApprovalOutcome.AlreadyIn,
                    "That is already done, so nothing was changed a second time.", null);

            if (set != null && string.Equals(set.State, ModelPlanChangeStates.Applied, StringComparison.Ordinal))
                return (ApprovalOutcome.AlreadyIn, AlreadyInSentence, null);

            // 2. Nothing proposed.
            if (set == null || !string.Equals(set.State, ModelPlanChangeStates.Proposed, StringComparison.Ordinal))
                return (ApprovalOutcome.Refused,
                    "Nothing was changed, because this task has no changes yet. Press Show the changes first.", null);

            // 3, 5. The session, the revision and the source. Freshened() worked all three out when the plan
            // was composed and put the reason in plain words; repeating the comparison here would be a second
            // definition of staleness to drift.
            if (set.Stale)
                return (ApprovalOutcome.Refused, set.StaleReason + " Nothing was applied.", null);

            // 4. The digest. A caller that quotes nothing is refused rather than helped: approving a set you
            // did not read is the one thing the digest exists to stop.
            if (quoted.Length == 0)
                return (ApprovalOutcome.Refused,
                    "Name the exact set of changes you are approving. Get the plan again and approve the digest it "
                    + "shows beside this task.", null);
            if (!string.Equals(quoted, set.Digest, StringComparison.OrdinalIgnoreCase))
                return (ApprovalOutcome.Refused,
                    "The changes on the plan are not the ones you approved, so nothing was changed. Look at them "
                    + "again and approve what you see.", null);

            var kept = (set.Lines ?? Array.Empty<ModelPlanChangeLine>())
                .Where(l => l.Kept && !string.IsNullOrWhiteSpace(l.PlanItemId))
                .ToArray();
            if (kept.Length == 0)
                return (ApprovalOutcome.NothingKept,
                    "You dropped every change here, so nothing was changed. Keep at least one, or set the "
                    + "task aside.", null);

            return (ApprovalOutcome.Ready, null, kept);
        }

        private const string AlreadyInSentence =
            "These changes are already made, so nothing was changed a second time. Undo takes them back.";

        /// <summary>WHAT WAS APPROVED, CARRIED INTO THE WRITE. Everything checked before the batch was checked
        /// on a model that can still move before the batch runs, so none of it is trusted on its own: the
        /// accepted lines, their exact values, the versions and the session travel with the work and are
        /// checked again on the single-writer dispatcher, inside the transaction that writes
        /// (Plan/PlanApprovalGuard.cs).
        ///
        /// THE WHOLE LINE, not the handle and the value. The handle is a position in a list that can be
        /// thrown away and started again, so everything the person actually read travels with it.</summary>
        private PlanApprovalGuard GuardFor(Session s, ModelPlanItem item, ModelPlanChangeSet set,
                                           ModelPlanChangeLine[] kept, long readAt)
        {
            var guard = new PlanApprovalGuard
            {
                Digest = set.Digest,
                Revision = readAt,
                SourceVersion = set.SourceVersion,
                SessionId = s.Id,
                ItemId = item.Id,
                ReadSourceVersion = () => SourceVersionOf(s),
            };
            foreach (var line in kept)
                guard.Lines[line.PlanItemId] = new ApprovedLine
                {
                    Op = line.Op,
                    ObjectRef = line.Target,       // the change set spells the object on Target
                    Property = line.Property,
                    Before = line.Before,
                    After = line.After,
                    Status = "approved",
                };
            return guard;
        }

        // ---- apply many items as one step (groundwork contract section 3) ----------------------------
        //
        // Run the plan. Every listed item is checked exactly as approve checks it, every kept line of every
        // item goes into ONE apply batch on the single-writer dispatcher, and each item's own approval guard
        // is checked inside that one transaction against that item's own lines. So the result is one undo
        // step, and it is all of the listed items or none of them: a stale digest, a set that moved, or one
        // line that cannot be made, and nothing is changed and the sentence names the item that stopped it.
        //
        // Two things are NOT refusals, because neither is a reason to hold the rest back. An item whose
        // changes are already in adds nothing (applying the same list twice applies nothing twice), and an
        // item with every line dropped adds nothing (a person dropping lines is a decision, not a fault).
        //
        // Bulk apply IS running the plan, so it is Pro on every item (Kane, 22 September 2026; F-238). The
        // Studio only ever drew Run the plan for Pro, and the agent door reaches this same method, so the line
        // is drawn here, once, for both doors. A free session approves item by item (model_plan_action), which
        // stays free for a row that is yours or your assistant's to do.

        /// <summary>Apply every kept line of every listed item in one step, or nothing. With
        /// <paramref name="readyDigest"/> and no items it is Run the plan: every Ready Semanticus row, fenced on the
        /// digest the caller read (contract 7.2).</summary>
        /// <remarks><paramref name="readyDigest"/> comes LAST and defaults to null, for the reason given on
        /// ModelPlanActionAsync: EngineRpcTarget binds by position over the pipe.</remarks>
        public async Task<ApplyPlanItemsResult> ApplyPlanItemsAsync(PlanItemDigest[] items, string origin, string readyDigest = null)
        {
            var asked = (items ?? Array.Empty<PlanItemDigest>()).Where(i => i != null).ToArray();
            var quotedReady = (readyDigest ?? "").Trim();
            if (quotedReady.Length > 0)
            {
                if (asked.Length > 0)
                    throw new ArgumentException(
                        "Pass the items or the ready digest, not both. The ready digest runs every task that is ready "
                        + "for Semanticus. The items run only the tasks you name.", nameof(readyDigest));
                return await RunReadySetAsync(quotedReady, origin).ConfigureAwait(false);
            }
            if (asked.Length == 0)
                throw new ArgumentException(
                    "Name the items to apply, each with the digest the plan showed for it, or pass the plan's "
                    + "readyDigest to run every task that is ready. Call get_model_plan for both.",
                    nameof(items));
            if (asked.Any(i => string.IsNullOrWhiteSpace(i.Id)))
                throw new ArgumentException("Every task needs its id from get_model_plan.", nameof(items));
            if (asked.Select(i => i.Id).Distinct(StringComparer.Ordinal).Count() != asked.Length)
                throw new ArgumentException("Name each task once.", nameof(items));

            var s = _sessions.Require();
            var readAt = s.Revision;
            // One binding, taken before the first await, for every read and every record below.
            var bound = BindPlanMemory();
            var plan = await ComposeAsync(null, bound).ConfigureAwait(false);

            if (!Entitlement.FeatureGrants.IsProTier(_entitlement))
            {
                await NoteFreeProRowsAsync(s, plan.Items.Where(i => asked.Any(a => a.Id == i.Id))).ConfigureAwait(false);
                return await BulkRefusedAsync(bound, null, null, s.Revision, origin, BulkRunIsProSentence).ConfigureAwait(false);
            }

            var ready = new List<(ModelPlanItem Item, ModelPlanChangeLine[] Kept)>();
            var alreadyIn = 0;
            var nothingKept = 0;
            foreach (var ask in asked)
            {
                var quoted = (ask.Digest ?? "").Trim();
                var item = plan.Items.FirstOrDefault(i => string.Equals(i.Id, ask.Id, StringComparison.Ordinal));
                if (item == null)
                {
                    // A set that applied and closed its gap can leave the plan altogether. The receipt is what
                    // says so, and the answer is then "already in", not "no such item".
                    if (AlreadyAppliedNow(bound, ask.Id, quoted)) { alreadyIn++; continue; }
                    return await BulkRefusedAsync(bound, null, ask.Id, s.Revision, origin,
                        "Nothing was changed, because no task on the plan has the id " + ask.Id + ". It may be "
                        + "done or set aside now. Get the plan again.").ConfigureAwait(false);
                }

                var check = ApprovalPrecheck(item, quoted);
                switch (check.Outcome)
                {
                    case ApprovalOutcome.AlreadyIn: alreadyIn++; continue;
                    case ApprovalOutcome.NothingKept: nothingKept++; continue;
                    case ApprovalOutcome.Refused:
                        return await BulkRefusedAsync(bound, item, item.Id, s.Revision, origin,
                            ItemStopped(item, check.Sentence)).ConfigureAwait(false);
                }
                ready.Add((item, check.Kept));
            }

            if (ready.Count == 0)
                return NothingToApply(s.Revision, plan, alreadyIn > 0
                    ? AlreadyInSentence
                    : "Every change on these tasks is dropped, so there was nothing to apply.");

            var shared = await SharedLineRefusalAsync(bound, s, ready, origin).ConfigureAwait(false);
            if (shared != null) return shared;

            // The way back, once for the whole step. Under today's promise it is the session undo timeline.
            var checkpoint = await CheckpointForApproveAsync(s, new ModelPlanItem { Title = "the plan" }).ConfigureAwait(false);
            if (checkpoint.Refusal != null)
                return await BulkRefusedAsync(bound, null, null, s.Revision, origin, checkpoint.Refusal).ConfigureAwait(false);

            // The same gate approve holds from its recheck to its receipt, for the same reason: a second
            // submit of the same list, from either door, reads the first one's receipt and adds nothing.
            await _planApproveGate.WaitAsync().ConfigureAwait(false);
            try
            {
                return await ApplyListedUnderGateAsync(s, bound, plan, readAt, ready, alreadyIn, checkpoint.Id, origin,
                    new List<ApplyPlanSkip>()).ConfigureAwait(false);
            }
            finally { _planApproveGate.Release(); }
        }

        // ---- Run the plan: the Ready Semanticus set, fenced on its digest (contract 7.2, Sol 24 Sep) ----------
        //
        // THE WHOLE RUN IS UNDER THE APPROVE GATE, FROM THE DIGEST TO THE RECEIPT. The Ready set is recomputed
        // here, the digest compared, the rows with no change set worked out, and the batch applied, all while
        // holding the one gate every approval holds. So nothing another approval does can land between the
        // check and the write, and a hold, a doer switch or a skip made after the caller read the plan (none of
        // which moves the model revision) changes the digest and is refused with one sentence. Those controls take
        // the same gate (UnderReadyGateAsync), so one made after the check waits for the commit instead of landing
        // inside it unseen (Sol 6, slice 7, finding 1).
        //
        // THE BATCH NEVER HOLDS A ROW WHOSE BLOCKER IS IN IT. Ready means WaitsFor is empty (contract 5.3), so no
        // member of the set waits for another member. A row that waits for a member is not Ready, is not taken,
        // and turns Ready on the next read once this batch has committed; the next press takes it.
        //
        // WHAT IS SKIPPED, AND NAMED, RATHER THAN REFUSED. A row whose worked-out changes are out of date, a row
        // with every change dropped, and a row nothing could be worked out for are left out and named; the rest
        // applies. None of them is a reason to hold the others back, and re-working an out-of-date set here
        // would apply lines the person may have kept or dropped differently. A removal whose object is in use by
        // the time the batch reaches it is skipped inside the batch the same way (ApplyPlanCoreAsync).

        internal const string PlanMovedSentence = "The plan changed since you last looked. Look at it again, then press Run the plan.";

        private static bool IsPersonOrigin(string origin) => string.Equals(origin, "human", StringComparison.OrdinalIgnoreCase);
        internal const string NothingReadySentence =
            "No task on the plan is ready for Semanticus, so nothing was changed.";

        /// <summary>The most batches one press runs. A row that waits only for rows in the press runs in the next batch of the SAME
        /// press, so a plan of chained Semanticus rows is done by one press (Kane, 8 Oct 2026: it must finish, not stop and ask for
        /// another press). A chain is a few batches long (four on the Wide test model, three on the Contoso model); twenty-five is far past any
        /// real chain and only bounds a plan whose rows keep finding new work after every batch.</summary>
        internal const int MaxRunWaves = 25;

        private async Task<ApplyPlanItemsResult> RunReadySetAsync(string quoted, string origin)
        {
            var s = _sessions.Require();
            // One binding, taken before the first await, for every read and every record below.
            var bound = BindPlanMemory();

            // Free can read the exact Pro work here, but only Pro runs it on either door (F-238).
            if (!Entitlement.FeatureGrants.IsProTier(_entitlement))
            {
                var plan = await ComposeAsync(null, bound).ConfigureAwait(false);
                await NoteFreeProRowsAsync(s, plan.Items.Where(i => i.Doer == PlanDoers.Semanticus
                    && i.StateWord == PlanStateWords.Ready && !i.Held)).ConfigureAwait(false);
                return await BulkRefusedAsync(bound, null, null, s.Revision, origin, BulkRunIsProSentence).ConfigureAwait(false);
            }

            // THE WHOLE PRESS IS UNDER THE ONE GATE, every batch of it. Another approval cannot land between two batches
            // of the same press, and the digest the caller read fences the first batch; the later batches are fenced by
            // the plan this press itself composed after the write before them (a model edit made in between is caught by
            // the revision check and, inside the write, by each row's approval guard).
            await _planApproveGate.WaitAsync().ConfigureAwait(false);
            try
            {
                var run = new RunClock(Guid.NewGuid().ToString("N").Substring(0, 12));
                var first = await RunWaveUnderGateAsync(s, bound, origin, run, wave: 1, quoted, null, new HashSet<string>(StringComparer.Ordinal))
                    .ConfigureAwait(false);
                if (!first.Performed)
                {
                    // Nothing for Semanticus to run is still a press: the assistant's rows go on its queue (verifier, 11 Oct: Kane's model
                    // had only assistant rows left, and the press said "nothing is ready" and queued nothing). A refusal hands nothing over.
                    if (run.NothingToRun) await QueueAssistantRowsAfterRunAsync(bound, first).ConfigureAwait(false);
                    if (run.WentBack.Count > 0)
                        first.Sentence = ((first.Sentence ?? "").Trim() + " " + WentBackSentence(run.WentBack.Select(w => w.Title).Distinct(StringComparer.Ordinal).ToList())).Trim();
                    return first;
                }

                var merged = first;
                var attempted = new HashSet<string>(StringComparer.Ordinal);
                foreach (var skip in first.Skipped.Where(x => x.What == null && x.ItemId != null)) attempted.Add(skip.ItemId);
                for (var wave = 2; wave <= MaxRunWaves && merged.Plan != null; wave++)
                {
                    // A Jev row is stale the moment the batch before moved the model, and Jev looks at it again by itself. Wait for that
                    // (bounded), read the plan again, and run the row in this press: leaving it out made the person press twice.
                    var plan = await LetJevLookAgainAsync(s, bound, run, wave, merged.Plan, attempted).ConfigureAwait(false);
                    var next = await RunWaveUnderGateAsync(s, bound, origin, run, wave, null, plan, attempted).ConfigureAwait(false);
                    merged.Plan = next.Plan ?? merged.Plan;   // the plan read after Jev settled, so the receipt shows the rows it found
                    // A batch that applied nothing ends the press: nothing more was ready, rows that turned Ready were all left out
                    // (named in the skipped list), or it was refused (named in the sentence).
                    if (!next.Performed)
                    {
                        if (next.FailedItemId != null || next.Skipped.Length > 0)
                            merged = MergeWaves(merged, next, run, bound, stopped: next.FailedItemId != null);
                        break;
                    }
                    foreach (var skip in next.Skipped.Where(x => x.What == null && x.ItemId != null)) attempted.Add(skip.ItemId);
                    merged = MergeWaves(merged, next, run, bound, stopped: false);
                }
                // A row whose earlier Jev answer no longer fit the model left the plan and went back to Jev. One true sentence names it.
                if (run.WentBack.Count > 0)
                {
                    var names = run.WentBack.Select(w => w.Title).Distinct(StringComparer.Ordinal).ToList();
                    merged.Sentence = ((merged.Sentence ?? "").Trim() + " " + WentBackSentence(names)).Trim();
                }
                merged.Unsaved = await UnsavedOnWriterAsync(s).ConfigureAwait(false);
                merged.CanSave = merged.Unsaved;   // the result line offers Save on every model; on a live one the window's Save writes to the service (D9). Run never saves by itself.
                await QueueAssistantRowsAfterRunAsync(bound, merged).ConfigureAwait(false);
                return merged;
            }
            finally { _planApproveGate.Release(); }
        }

        /// <summary>The most a press waits for Jev to look again at the rows the last batch changed. Jev answers a table in a few seconds; this only
        /// bounds a Jev that never answers, and then the row is left out and named as before.</summary>
        internal static TimeSpan JevLookAgainWait = TimeSpan.FromSeconds(15);

        /// <summary>When the Semanticus rows are done, the assistant's rows go on its queue (Kane, 11 Oct: he had to switch to Claude Code
        /// and type an instruction for 8 rows). A found row becomes a task linked to it, and the receipt says so. The queue switch is left as
        /// it is: it is On unless someone paused it, and a pause is kept (verifier 5b), so the receipt says the tasks wait for it.
        /// Offering work never starts, wakes or claims for an assistant: it sees the tasks on its next call.</summary>
        private async Task QueueAssistantRowsAfterRunAsync(PlanMemoryBinding bound, ApplyPlanItemsResult result)
        {
            if (result.Plan == null || !PlanMemoryStillOpen(bound)) return;
            var rows = result.Plan.Items.Where(i => i.Doer == PlanDoers.Assistant && !i.Held && i.Claim == null
                && (i.StateWord == PlanStateWords.Ready || i.StateWord == PlanStateWords.Waiting)
                && !Composer.DraftRows.IsDraftRow(i.Id) && !IsRunRow(i.Id)).ToArray();
            if (rows.Length == 0) return;
            // Only a found row with no lines of its own becomes a task. A row that carries the assistant's own proposed lines is already
            // the assistant's, and a task made from it would lose those lines and what the row waits for (a task's footprint is its objects).
            foreach (var row in rows.Where(i => !PlanTasks.IsTaskId(i.Id) && (i.ChangeSet?.Lines?.Length ?? 0) == 0))
            {
                if (!PlanMemoryStillOpen(bound)) return;
                // The press already wrote: a row that finished or moved since the read is simply not queued, never a failed press.
                try { await GiveRowToAssistantAsync(row.Id, "semanticus").ConfigureAwait(false); }
                catch (ArgumentException) { }
            }
            if (!PlanMemoryStillOpen(bound)) return;
            var plan = await ComposeBoundAsync(bound, null).ConfigureAwait(false);
            if (plan == null) return;
            result.Plan = plan;
            TellThePlanMoved(bound);
            // Counted off the plan, not the offer: a paused queue offers nothing, yet its tasks are still queued.
            var mine = plan.Items.Where(i => PlanTasks.IsTaskId(i.Id) && i.Doer == PlanDoers.Assistant && i.Claim == null).ToArray();
            var waiting = mine.Count(i => i.StateWord == PlanStateWords.Waiting);
            var queued = mine.Count(i => i.StateWord == PlanStateWords.Ready) + waiting;
            if (queued == 0) return;
            var paused = plan.AssistantQueue == PlanAssistantQueue.Paused;
            result.Note = (!result.Performed ? ""
                    : result.Skipped.Any(x => x.What == null) ? "Semanticus finished what it can run. " : "Semanticus finished its tasks. ")
                + CountOf(queued, "task is", "tasks are") + " queued for your assistant."
                + (paused
                    ? " The queue is paused, so " + (queued == 1 ? "it waits" : "they wait") + " until you turn it on."
                    : (waiting == 0 ? "" : queued == 1 ? " It waits for another task."
                        : " " + CountOf(waiting, "of them waits", "of them wait") + " for another task.")
                      + (queued == 1 ? " It sees it on its next call." : " It sees them on its next call."));
            result.Sentence = string.IsNullOrEmpty(result.Sentence) ? result.Note : result.Sentence + " " + result.Note;
        }

        /// <summary>When Ready Jev rows are stale because of the batch before, wait for Jev to settle and read the plan again, so they are
        /// not stale and the next batch takes them. Every other case returns the plan as it is. Called under the approve gate; Jev's own run
        /// takes no part in that gate, so waiting here cannot hold it up.</summary>
        private async Task<ModelPlanView> LetJevLookAgainAsync(Session s, PlanMemoryBinding bound, RunClock run, int wave, ModelPlanView plan, HashSet<string> leftOut)
        {
            // The batch before can make measures that have no row yet (Kane, 11 Oct: the Tidy rows for the press's own new measures
            // came only after it ended). So Jev is started for this revision whether or not a Ready row looks stale, the press follows
            // a partial run to its end, and only the plan read after Jev settled decides that the press is done. Bounded by the wait.
            // A Jev that does not answer in that time is not waited for again in the same press (Kane's 10 rows would each wait 120 s):
            // Semanticus stands in for it with Jev's earlier answers, checked against the model as it is (StandInForSilentJevAsync).
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var waited = false;
            while (PlanMemoryStillOpen(bound) && !run.JevSilent)
            {
                var state = await KickJevFamiliesAsync(bound).ConfigureAwait(false);
                if (!state.Live && !waited) return plan;   // no Jev in this session: nothing to wait for, and no second read
                if (!state.Pending) { plan = await ComposeBoundAsync(bound, null).ConfigureAwait(false) ?? plan; break; }
                var looking = PlanReadySet.JevRowsLookingAgain(plan.Items, leftOut).Select(r => r.Id).ToArray();
                var left = JevLookAgainWait - clock.Elapsed;
                waited = true;
                if (left <= TimeSpan.Zero || !await WaitForJevAsync(run, wave, looking, left, clock).ConfigureAwait(false))
                {
                    run.JevSilent = true;
                    plan = await ComposeBoundAsync(bound, null).ConfigureAwait(false) ?? plan;
                    break;
                }
                plan = await ComposeBoundAsync(bound, null).ConfigureAwait(false) ?? plan;
            }
            if (!PlanMemoryStillOpen(bound)) return plan;
            var stale = PlanReadySet.StaleJevRows(plan.Items, leftOut);
            var before = run.WentBack.Count;
            if (stale.Count > 0 && await StandInForSilentJevAsync(s, bound, stale, run.WentBack).ConfigureAwait(false))
                plan = await ComposeBoundAsync(bound, null).ConfigureAwait(false) ?? plan;
            // A row that went back to Jev is told to the window at once, so it never sits on "Waiting for Jev" until the receipt.
            foreach (var (id, _) in run.WentBack.Skip(before))
                PublishRun(run, wave, PlanRunPhases.Skipped, new[] { id }, reason: "Jev is looking at this again, because its earlier answer no longer fits the model.");
            return plan;
        }

        /// <summary>Waits for Jev to settle, at most <paramref name="left"/>, and tells the window every second how long it has waited
        /// ("Waiting for Jev, 0:08"). True when Jev settled in time.</summary>
        private async Task<bool> WaitForJevAsync(RunClock run, int wave, string[] looking, TimeSpan left, System.Diagnostics.Stopwatch clock)
        {
            var settled = JevSettledAsync(left);
            while (true)
            {
                // What Jev is being asked, with the real count, is said with every tick of the wait, whether or not a row is named: the rows Jev
                // looks at again may be none while the press still waits for its answer to a measure the last batch made.
                var choosing = JevChoosing.Words();
                if (looking.Length > 0 || choosing != null)
                    _sessions.Bus.PublishRunProgress(new PlanRunProgress
                    {
                        RunId = run.Id, ItemIds = looking, Phase = PlanRunPhases.WaitJev, Wave = wave, WaitedSeconds = (int)clock.Elapsed.TotalSeconds, Ms = run.Watch.ElapsedMilliseconds,
                        Choosing = choosing,
                    });
                if (settled.IsCompleted) break;
                await Task.WhenAny(settled, Task.Delay(1000)).ConfigureAwait(false);
            }
            return await settled.ConfigureAwait(false);
        }

        /// <summary>Whether the model has unsaved edits, read where the model's writers run. The undo stack is a plain stack: read from a
        /// background thread while another writer pushes to it, it threw "Collection was modified" and a finished press ended in an error
        /// (measured twice on the Yoga, 13 Oct). A read that still fails says there is something to save, which only offers Save.</summary>
        private static async Task<bool> UnsavedOnWriterAsync(Session s)
        {
            try { return await s.ReadAsync(_ => s.HasUnsavedEdits).ConfigureAwait(false); }
            catch (InvalidOperationException) { return true; }
        }

        /// <summary>Jev is still looking at the rows that went back to it, and runs them when it answers.</summary>
        internal static string WentBackSentence(IReadOnlyList<string> names) =>
            "Jev is still looking at " + names.Count + ": " + string.Join("; ", names.Take(3).Select(Lower))
            + (names.Count > 3 ? "; and " + (names.Count - 3) + " more" : "")
            + ". Its earlier answer no longer fits the model, so " + (names.Count == 1 ? "it runs" : "they run") + " when Jev answers.";

        /// <summary>One press's clock and name. Every event of the press carries the same run id and the milliseconds since the press began.</summary>
        private sealed class RunClock
        {
            public RunClock(string id) { Id = id; Watch = System.Diagnostics.Stopwatch.StartNew(); }
            public string Id { get; }
            public System.Diagnostics.Stopwatch Watch { get; }
            /// <summary>The first batch found nothing to write (no row Ready, or every Ready row left out), as opposed to a refusal.</summary>
            public bool NothingToRun { get; set; }
            /// <summary>Jev did not settle in the wait this press allowed, so the press does not wait for it again.</summary>
            public bool JevSilent { get; set; }
            /// <summary>The rows whose earlier Jev answer no longer fit the model, so they left the plan and went back to Jev.</summary>
            public List<(string Id, string Title)> WentBack { get; } = new List<(string Id, string Title)>();
        }

        private void PublishRun(RunClock run, int wave, string phase, string[] itemIds, int? changes = null, string reason = null) =>
            _sessions.Bus.PublishRunProgress(new PlanRunProgress
            {
                RunId = run.Id, ItemIds = itemIds, Phase = phase, Wave = wave, Changes = changes, Reason = reason, Ms = run.Watch.ElapsedMilliseconds,
            });

        /// <summary>The batches of one press as one result: the changes added up, every row once, the first batch's History entry (the
        /// one an undo to that entry takes back, with everything after it), and one sentence for the whole press.</summary>
        private static ApplyPlanItemsResult MergeWaves(ApplyPlanItemsResult merged, ApplyPlanItemsResult next, RunClock run,
                                                       PlanMemoryBinding bound, bool stopped)
        {
            var skipped = merged.Skipped.Concat(next.Skipped)
                .GroupBy(x => (x.ItemId, x.What, x.Reason)).Select(g => g.First()).ToArray();
            var waves = merged.Waves + (next.Performed ? 1 : 0);
            var landed = next.Performed || stopped;
            var total = merged.Applied + next.Applied;
            var alreadyIn = merged.AlreadyIn + next.AlreadyIn;
            var left = SkippedSentence(skipped);
            return new ApplyPlanItemsResult
            {
                Performed = true,
                Applied = total,
                ItemIds = merged.ItemIds.Concat(next.ItemIds).Distinct(StringComparer.Ordinal).ToArray(),   // a row that took two batches is named once
                Skipped = skipped,
                Revision = landed ? next.Revision : merged.Revision,
                Plan = landed ? next.Plan ?? merged.Plan : merged.Plan,
                RunId = run.Id,
                EntryId = merged.EntryId ?? next.EntryId,
                EntryIds = merged.EntryIds.Concat(next.EntryIds).ToArray(),
                Waves = waves,
                AlreadyIn = alreadyIn,
                Sentence = (waves > 1
                               ? "Applied " + CountOf(total, "change", "changes") + " in " + waves + " steps. One undo takes the whole run back."
                               : merged.Sentence)
                         + (alreadyIn == 0 ? ""
                            : " " + CountOf(alreadyIn, "task was", "tasks were") + " already in, so "
                              + (alreadyIn == 1 ? "it was" : "they were") + " not applied a second time.")
                         + (left.Length == 0 ? "" : " " + left)
                         + (!stopped ? "" : " The run stopped there: " + next.Sentence)
                         + (bound.Nowhere && total > 0 ? " " + UnsavedNote : ""),
            };
        }

        /// <summary>One batch of a press: the Ready Semanticus rows, worked out where they have no changes yet, checked as approve checks
        /// them, and written as ONE batch. The caller HOLDS <see cref="_planApproveGate"/>. The first batch of a press quotes the digest the
        /// caller read and composes the plan; a later batch takes the plan the batch before it composed after its write
        /// (<paramref name="composed"/>) and leaves out the rows already left out (<paramref name="leftOut"/>).</summary>
        private async Task<ApplyPlanItemsResult> RunWaveUnderGateAsync(
            Session s, PlanMemoryBinding bound, string origin, RunClock run, int wave, string quoted, ModelPlanView composed, HashSet<string> leftOut)
        {
            var readAt = composed?.Revision ?? s.Revision;
            var plan = composed;
            if (plan == null)
            {
                plan = await ComposeBoundAsync(bound, null).ConfigureAwait(false);
                if (plan == null)
                    return new ApplyPlanItemsResult { Performed = false, Revision = s.Revision, Sentence = PlanMemoryModelSwitchedSentence };

                // A person's press means the plan as it is now: the page cannot "read it again" for them (Kane, 11 Oct:
                // four presses refused in five seconds). An assistant still quotes the set it read, and is fenced on it.
                if (!IsPersonOrigin(origin) && !string.Equals(plan.ReadyDigest ?? "", quoted, StringComparison.OrdinalIgnoreCase))
                    return await BulkRefusedAsync(bound, null, null, s.Revision, origin, PlanMovedSentence).ConfigureAwait(false);
                readAt = plan.Revision;
                // A Ready Jev row already stale at the start (the model changed after Jev looked) gets the same wait and the same stand-in
                // a later batch gets, so the first press runs it instead of leaving it out (Kane, 13 Oct: "does nothing").
                if (wave == 1 && PlanReadySet.StaleJevRows(plan.Items, leftOut).Any(PlanReadySet.IsReady))
                {
                    plan = await LetJevLookAgainAsync(s, bound, run, wave, plan, leftOut).ConfigureAwait(false);
                    readAt = plan.Revision;
                }
            }
            else if (s.Revision != composed.Revision)
            {
                if (!IsPersonOrigin(origin))
                    return NothingToApply(s.Revision, plan, PlanMovedSentence);
                plan = await ComposeBoundAsync(bound, null).ConfigureAwait(false);
                if (plan == null)
                    return new ApplyPlanItemsResult { Performed = false, Revision = s.Revision, Sentence = PlanMemoryModelSwitchedSentence };
                readAt = plan.Revision;
            }

            var set = PlanReadySet.Rows(plan.Items).Where(r => !leftOut.Contains(r.Id)).ToList();
            if (set.Count == 0)
            {
                run.NothingToRun = wave == 1;
                return NothingToApply(s.Revision, plan, NothingReadySentence);
            }

            var checkedSeam = System.Threading.Interlocked.Exchange(ref RunReadyCheckedForTest, null);
            if (checkedSeam != null) await checkedSeam().ConfigureAwait(false);

            var ready = new List<(ModelPlanItem Item, ModelPlanChangeLine[] Kept)>();
            var skipped = new List<ApplyPlanSkip>();
            var alreadyIn = 0;
            // A row left out is told to the window at once, with its reason, so it never sits on "In this run" until the receipt.
            void LeaveOut(ModelPlanItem item, string reason)
            {
                skipped.Add(Skip(item, null, reason));
                PublishRun(run, wave, PlanRunPhases.Skipped, new[] { item.Id }, reason: (reason ?? "").Trim());
            }
            foreach (var row in set)
            {
                var item = row;
                var state = item.ChangeSet?.State;
                // A Jev row that rolled back still has Jev's lines. The generic propose path has no seeds for it and used to send
                // the person to Pending changes on every press (Kane, 11 Oct). Rebuild it from those lines without the one that failed.
                if (JevFamily.IsJevItem(item.Id) && string.Equals(state, ModelPlanChangeStates.RolledBack, StringComparison.Ordinal))
                {
                    var rebuilt = await RebuildRolledBackJevRowAsync(bound, item).ConfigureAwait(false);
                    plan = rebuilt ?? plan;
                    item = rebuilt?.Items.FirstOrDefault(i => string.Equals(i.Id, row.Id, StringComparison.Ordinal));
                    if (item?.ChangeSet == null)
                    {
                        LeaveOut(row, "Jev no longer offers changes for this task. Open this task on the Plan to see what remains.");
                        continue;
                    }
                    state = item.ChangeSet.State;
                }
                if (!string.Equals(state, ModelPlanChangeStates.Proposed, StringComparison.Ordinal)
                    && !string.Equals(state, ModelPlanChangeStates.Applied, StringComparison.Ordinal))
                {
                    // No change set yet: work the lines out now, in this same step, as start does for a safe
                    // fix. Writes nothing to the model; the History entry for the batch carries every line.
                    PublishRun(run, wave, PlanRunPhases.Work, new[] { item.Id });
                    var worked = await ProposeItemAsync(s, item, readAt, origin).ConfigureAwait(false);
                    var now = worked.Plan?.Items.FirstOrDefault(i => string.Equals(i.Id, item.Id, StringComparison.Ordinal));
                    if (now?.ChangeSet == null
                        || !string.Equals(now.ChangeSet.State, ModelPlanChangeStates.Proposed, StringComparison.Ordinal))
                    {
                        LeaveOut(item, worked.Sentence);
                        continue;
                    }
                    item = now;
                }

                if (item.ChangeSet.Stale)
                {
                    LeaveOut(item, StaleRowSentence(item));
                    continue;
                }

                // THE PERSON REVIEWS THE CLEANUP BEFORE ANYTHING IS DELETED (Kane, 30 Sep 2026). A cleanup line that is ticked only
                // because Semanticus suggested it, and that nobody has ticked, unticked or edited, is not reviewed, and Run the
                // plan never applies it. The whole row is left out and named, so nothing is half done; a person's own tick,
                // Keep all or Approve on the row makes the lines theirs. A cleanup row whose every ticked line was touched runs.
                if (IsCleanupItem(item.Id))
                {
                    var unreviewed = (item.ChangeSet.Lines ?? Array.Empty<ModelPlanChangeLine>()).Count(l => l.Kept && l.Suggested);
                    if (unreviewed > 0)
                    {
                        LeaveOut(item, CleanupNotReviewedSentence(unreviewed));
                        continue;
                    }
                }

                // The digest the engine itself composed: the caller's fence was the Ready digest above.
                var check = ApprovalPrecheck(item, item.ChangeSet.Digest);
                switch (check.Outcome)
                {
                    case ApprovalOutcome.AlreadyIn: alreadyIn++; continue;
                    case ApprovalOutcome.NothingKept:
                        LeaveOut(item, string.IsNullOrWhiteSpace(item.ChangeSet.FailureReason)
                            ? "Every change on it is dropped."
                            : item.ChangeSet.FailureReason + " " + OpenRowOnPlanSentence);
                        continue;
                    case ApprovalOutcome.Refused:
                        LeaveOut(item, check.Sentence);
                        continue;
                }
                ready.Add((item, check.Kept));
            }

            if (ready.Count == 0)
            {
                run.NothingToRun = wave == 1;
                // After a batch that wrote, "Nothing was changed" is false for the press (the receipt says 16 changes and that line together).
                var none = NothingToApply(s.Revision, plan, alreadyIn > 0 && skipped.Count == 0
                    ? AlreadyInSentence
                    : (wave > 1 ? "" : "Nothing was changed. ") + SkippedSentence(skipped));
                none.Skipped = skipped.ToArray();
                return none;
            }

            var shared = await SharedLineRefusalAsync(bound, s, ready, origin).ConfigureAwait(false);
            if (shared != null) return shared;

            var checkpoint = await CheckpointForApproveAsync(s, new ModelPlanItem { Title = "the plan" }).ConfigureAwait(false);
            if (checkpoint.Refusal != null)
                return await BulkRefusedAsync(bound, null, null, s.Revision, origin, checkpoint.Refusal).ConfigureAwait(false);

            foreach (var (item, kept) in ready) PublishRun(run, wave, PlanRunPhases.Apply, new[] { item.Id }, changes: kept.Length);
            var result = await ApplyListedUnderGateAsync(s, bound, plan, readAt, ready, alreadyIn, checkpoint.Id, origin, skipped, run, wave)
                .ConfigureAwait(false);
            result.RunId = run.Id;
            if (!result.Performed) foreach (var (item, _) in ready) PublishRun(run, wave, PlanRunPhases.Skipped, new[] { item.Id }, reason: result.Sentence);
            return result;
        }

        /// <summary>Why Run the plan left the cleanup row alone. One sentence, and the way forward.</summary>
        internal static string CleanupNotReviewedSentence(int unreviewed) =>
            CountOf(unreviewed, "removal is", "removals are") + " ticked only because Semanticus suggested "
            + (unreviewed == 1 ? "it" : "them") + ", and you have not looked at "
            + (unreviewed == 1 ? "it" : "them") + " yet. Nothing was removed. Open the task, untick what you want to keep, then press Approve.";

        private static ApplyPlanSkip Skip(ModelPlanItem item, string what, string reason) =>
            new ApplyPlanSkip { ItemId = item?.Id, Title = item?.Title, What = what, Reason = (reason ?? "").Trim() };

        internal const string OpenRowOnPlanSentence = "Open this task on the Plan to choose its changes.";

        /// <summary>A rolled-back Jev row, rebuilt from Jev's live lines: the decisions on them stay, and only the exact line and value
        /// that failed is dropped (with its reason, so a row left with nothing kept says why). A legacy header with no failed line
        /// recorded just gets a fresh header over the current lines. Returns the plan composed after, or null when the model left.</summary>
        private async Task<ModelPlanView> RebuildRolledBackJevRowAsync(PlanMemoryBinding bound, ModelPlanItem item)
        {
            ReadPlanState(bound).Changes.TryGetValue(item.Id, out var old);
            string failedTarget = null;
            await bound.Context.PlanGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!PlanMemoryStillOpen(bound)) return null;
                var changes = bound.Context.Plans.Current;
                var failed = old?.FailedLineId == null ? null : changes?.Snapshot().FirstOrDefault(l =>
                    string.Equals(l.JevItem, item.Id, StringComparison.Ordinal) && IsPendingLine(l)
                    && LineIdFor(l) == old.FailedLineId && l.After == old.FailedAfter);
                if (failed != null)
                {
                    failedTarget = failed.ObjectRef;
                    // Dropped as a person's untick is, so the drop holds when Jev answers for this object again; a tick brings it back.
                    failed.Status = "rejected";
                    failed.KeptByPerson = false;
                    failed.KeptAgainst = null;
                    failed.KeptDigest = null;
                    failed.DroppedBy = "person";
                    failed.Note = old.FailureReason;
                    PlanLineExecutor.FollowKeep(failed, changes.Snapshot().Where(IsPendingLine), keep: false);
                    PublishPlan(changes, bound.Session.Revision);
                }
            }
            finally { bound.Context.PlanGate.Release(); }

            var plan = await ComposeBoundAsync(bound, m => m.Changes.Remove(item.Id)).ConfigureAwait(false);
            var fresh = plan?.Items.FirstOrDefault(i => string.Equals(i.Id, item.Id, StringComparison.Ordinal));
            if (fresh?.ChangeSet != null && failedTarget != null && !string.IsNullOrWhiteSpace(old?.FailureReason))
                plan = await ComposeBoundAsync(bound, m =>
                {
                    var h = HeaderFor(m, fresh, fresh.ChangeSet);
                    h.FailureReason = old.FailureReason;
                    h.Refused = (h.Refused ?? Array.Empty<ModelPlanChangeRefusal>())
                        .Append(new ModelPlanChangeRefusal { Target = failedTarget, Reason = old.FailureReason }).ToArray();
                }).ConfigureAwait(false);
            return plan;
        }

        /// <summary>One sentence naming what was left out, or empty when nothing was.</summary>
        internal static string SkippedSentenceForTest(IReadOnlyList<ApplyPlanSkip> skipped) => SkippedSentence(skipped);

        private static string SkippedSentence(IReadOnlyList<ApplyPlanSkip> skipped)
        {
            if (skipped == null || skipped.Count == 0) return "";
            var rows = skipped.Where(x => x.What == null).ToList();
            // A row Jev is still looking at is not a failure: it runs when Jev answers, and the press says so in one plain line.
            var jevLooking = rows.Where(x => (x.Reason ?? "").StartsWith(JevStillLookingReason, StringComparison.Ordinal)).ToList();
            rows = rows.Except(jevLooking).ToList();
            // A line the person's acceptance left out at apply (accepted after the row was worked out) is named as that, not as a removal.
            var accepted = skipped.Where(x => x.What != null && (x.Reason == WaivedAfterPlannedNote || x.Reason == DeclinedChangeNote)).ToList();
            var fixes = skipped.Where(x => x.What != null && !accepted.Contains(x) && x.IsFix).ToList();
            var formats = skipped.Where(x => x.What != null && !accepted.Contains(x) && !x.IsFix && x.Kind == "format").ToList();
            var lines = skipped.Where(x => x.What != null && !accepted.Contains(x) && !x.IsFix && x.Kind != "format").ToList();
            var parts = new List<string>();
            if (accepted.Count > 0)
                parts.Add(CountOf(accepted.Count, "change was", "changes were") + " left out because you accepted "
                          + (accepted.Count == 1 ? "it" : "them") + ": " + string.Join(", ", accepted.Select(l => l.What)) + ".");
            if (jevLooking.Count > 0)
                parts.Add("Jev is still looking at " + jevLooking.Count + "; " + (jevLooking.Count == 1 ? "it runs" : "they run") + " when Jev answers.");
            if (rows.Count > 0)
                parts.Add(CountOf(rows.Count, "task was", "tasks were") + " left out: "
                          + string.Join("; ", rows.Take(5).Select(r => Lower(r.Title) + " (" + r.Reason.TrimEnd('.') + ")"))
                          + (rows.Count > 5 ? "; and " + (rows.Count - 5) + " more, still on the plan." : "."));
            if (fixes.Count > 0)
                parts.Add(CountOf(fixes.Count, "best-practice fix was", "best-practice fixes were") + " left out because "
                          + (fixes.Count == 1 ? "it is" : "they are") + " yours to choose: "
                          + string.Join("; ", fixes.Take(3).Select(x => x.What + " (" + x.Reason.TrimEnd('.') + ")"))
                          + (fixes.Count > 3 ? "; and " + (fixes.Count - 3) + " more." : ".")
                          + " Model quality lists " + (fixes.Count == 1 ? "it." : "them."));
            if (lines.Count > 0)
                parts.Add(CountOf(lines.Count, "removal was", "removals were") + " left out because "
                          + (lines.Count == 1 ? "it is" : "they are") + " in use now: "
                          + string.Join(", ", lines.Select(l => l.What)) + ".");
            if (formats.Count > 0)
                parts.Add(CountOf(formats.Count, "format was", "formats were") + " left out, and the rest was applied: "
                          + string.Join("; ", formats.Select(l => l.What + " (" + (l.Reason ?? "").TrimEnd('.') + ")")) + ".");
            return string.Join(" ", parts);
        }

        /// <summary>One change can only be approved once. Two items both carrying the same line would make two
        /// approvals for one write, and neither guard could say which of them the write was for.</summary>
        private async Task<ApplyPlanItemsResult> SharedLineRefusalAsync(
            PlanMemoryBinding bound, Session s, List<(ModelPlanItem Item, ModelPlanChangeLine[] Kept)> ready, string origin)
        {
            var lineIds = ready.SelectMany(r => r.Kept.Select(l => l.PlanItemId)).ToArray();
            var shared = lineIds.GroupBy(x => x, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
            if (shared == null) return null;
            var both = ready.First(r => r.Kept.Any(l => string.Equals(l.PlanItemId, shared.Key, StringComparison.OrdinalIgnoreCase))).Item;
            return await BulkRefusedAsync(bound, both, both.Id, s.Revision, origin,
                ItemStopped(both, "One of its changes is also on another task. Get the plan again "
                                  + "and apply what you see.")).ConfigureAwait(false);
        }

        /// <summary>The write and the receipt of a bulk apply. The caller HOLDS <see cref="_planApproveGate"/>.
        /// Every kept line of every item goes into ONE apply batch; each item's own guard is checked inside it.</summary>
        private async Task<ApplyPlanItemsResult> ApplyListedUnderGateAsync(
            Session s, PlanMemoryBinding bound, ModelPlanView plan, long readAt,
            List<(ModelPlanItem Item, ModelPlanChangeLine[] Kept)> ready, int alreadyIn, string checkpointId, string origin,
            List<ApplyPlanSkip> skipped, RunClock run = null, int wave = 1)
        {
            var guards = ready.Select(r => GuardFor(s, r.Item, r.Item.ChangeSet, r.Kept, readAt)).ToArray();
            var byId = ready.ToDictionary(r => r.Item.Id, r => r, StringComparer.Ordinal);

            var stillReady = ready.Where(r => !AlreadyAppliedNow(bound, r.Item.Id, r.Item.ChangeSet.Digest)).ToList();
            if (stillReady.Count == 0)
                return NothingToApply(s.Revision, plan, AlreadyInSentence);
            if (stillReady.Count != ready.Count)
            {
                alreadyIn += ready.Count - stillReady.Count;
                guards = guards.Where(g => stillReady.Any(r => r.Item.Id == g.ItemId)).ToArray();
            }

            if (!PlanMemoryStillOpen(bound))
                return new ApplyPlanItemsResult
                {
                    Performed = false, Revision = s.Revision, Plan = null, Sentence = PlanMemoryModelSwitchedSentence,
                };

            ApplyPlanReport report;
            try
            {
                // Contract 8.4: one commit, every item id in the batch, stamped as it is written.
                using var cause = ChangeCause.Begin(ChangeCauseKinds.PlanItem,
                    stillReady.Count == 1 ? stillReady[0].Item.Title : "Run the plan", ChangeBy.ApproverFor(origin),
                    stillReady.Select(r => r.Item.Id).ToArray());
                //
                // Plan order, renames last, removals after that (ApplyOrder), and a removal whose object is in use
                // by the time the batch reaches it is skipped and named rather than rolling the rest back.
                report = await ApplyPlanCoreAsync(
                    stillReady.SelectMany(r => r.Kept.Select(l => l.PlanItemId)).ToArray(),
                    MutationOrigin(origin), null, null, guards, keepGoingPastInUseRemovals: true).ConfigureAwait(false);
            }
            catch (PlanApprovalRefusedException refused)
            {
                // Nothing was written: the batch's own rollback put back whatever it had done, and the
                // lines of every item are as they were. Only the item that stopped it is marked.
                var culprit = refused.Guard?.ItemId != null && byId.TryGetValue(refused.Guard.ItemId, out var hit)
                    ? hit.Item : null;
                var sentence = culprit == null
                    ? refused.Message
                    : refused.Line != null
                        ? LineStopped(culprit, refused.Line)
                        : ItemStopped(culprit, refused.Message);
                var rolledBack = await ComposeAfterWriteAsync(bound, memory =>
                {
                    if (culprit != null)
                    {
                        RememberRollback(memory, culprit, culprit.ChangeSet, refused);
                    }
                    memory.History.Add(Entry(ModelPlanHistoryKinds.Refused, ActorOf(origin), culprit?.Id, culprit?.Title ?? "the plan",
                        s.Revision, "Nothing was changed on the plan.", new[] { sentence }));
                }).ConfigureAwait(false);
                // Run the plan: one row that cannot be made does not hold back the others. The batch's rollback put the
                // model and every line back, so the other rows' guards still hold; they go in again as one undo step.
                if (run != null && rolledBack != null && culprit != null)
                {
                    var reason = refused.Line?.Note ?? refused.Message;
                    skipped.Add(Skip(culprit, null, reason));
                    PublishRun(run, wave, PlanRunPhases.Skipped, new[] { culprit.Id }, reason: reason);
                    var rest = stillReady.Where(r => r.Item.Id != culprit.Id).ToList();
                    if (rest.Count > 0)
                        return await ApplyListedUnderGateAsync(s, bound, rolledBack, readAt, rest, alreadyIn,
                            checkpointId, origin, skipped, run, wave).ConfigureAwait(false);
                    return new ApplyPlanItemsResult
                    {
                        Performed = false, Revision = s.Revision, Plan = rolledBack, FailedItemId = culprit.Id,
                        FailedLine = refused.Line?.Id, Skipped = skipped.ToArray(),
                        Sentence = "Nothing was changed. " + SkippedSentence(skipped),
                    };
                }
                return new ApplyPlanItemsResult
                {
                    Performed = false,
                    Revision = s.Revision,
                    Plan = rolledBack,
                    FailedItemId = culprit?.Id,
                    FailedLine = refused.Line?.Id,
                    Sentence = rolledBack == null ? PlanMemoryModelSwitchedSentence : sentence,
                };
            }

            // The removals the batch kept because their object was in use by then, named by the line.
            var lineOwner = stillReady.SelectMany(r => r.Kept.Select(l => (Line: l.PlanItemId, r.Item)))
                .GroupBy(x => x.Line, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().Item, StringComparer.OrdinalIgnoreCase);
            foreach (var kept in (report.Items ?? Array.Empty<ChangeItem>())
                         .Where(i => string.Equals(i.Status, "skipped", StringComparison.OrdinalIgnoreCase)))
            {
                lineOwner.TryGetValue(kept.Id ?? "", out var owner);
                var skip = Skip(owner, string.IsNullOrWhiteSpace(kept.ObjectName) ? kept.ObjectRef : kept.ObjectName, kept.Note);
                if (IsAutomaticFormatWrite(kept)) skip.Kind = "format";
                skip.IsFix = string.Equals(OpAliases.CanonicalPlanKind((kept.Kind ?? "").Trim()), "bpa_fix", StringComparison.OrdinalIgnoreCase);
                if (skip.IsFix) skip.RuleId = kept.RuleId;
                skipped.Add(skip);
            }

            var total = report.AppliedCount;
            var landedIds = new HashSet<string>((report.Items ?? Array.Empty<ChangeItem>())
                .Where(i => string.Equals(i.Status, "applied", StringComparison.OrdinalIgnoreCase)).Select(i => i.Id),
                StringComparer.OrdinalIgnoreCase);
            foreach (var (item, kept) in stillReady)
                NoteProFixes(s.Id, item, kept.Count(l => landedIds.Contains(l.PlanItemId)), origin: origin);
            // The receipt names what was written: a line the batch left out (in use, or a fix the row refuses) is not in it.
            var leftOutLines = new HashSet<string>(
                (report.Items ?? Array.Empty<ChangeItem>())
                    .Where(i => string.Equals(i.Status, "skipped", StringComparison.OrdinalIgnoreCase)).Select(i => i.Id ?? ""),
                StringComparer.OrdinalIgnoreCase);
            // The History entry this batch wrote: what an undo to that entry takes back. Read before the recompose, which can be slow.
            var entryId = LedgerEntryOf(s, stillReady.Select(r => r.Item.Id).ToArray());
            // The write is in; the plan is read again to check it. That read is the slow part, so the window is told first.
            if (run != null) PublishRun(run, wave, PlanRunPhases.Check, stillReady.Select(r => r.Item.Id).ToArray());
            var applied = await ComposeAfterWriteAsync(bound, memory =>
            {
                foreach (var (item, allKept) in stillReady)
                {
                    var kept = allKept.Where(l => !leftOutLines.Contains(l.PlanItemId ?? "")).ToArray();
                    if (kept.Length == 0 && allKept.Length > 0) continue;   // every line of it was left out: nothing of it landed
                    var h = HeaderFor(memory, item, item.ChangeSet);
                    h.State = ModelPlanChangeStates.Applied;
                    h.AppliedAt = DateTimeOffset.UtcNow;
                    h.AppliedDigest = item.ChangeSet.Digest;
                    h.Checkpoint = checkpointId;
                    h.Lines = item.ChangeSet.Lines ?? Array.Empty<ModelPlanChangeLine>();
                    memory.History.Add(Entry(ModelPlanHistoryKinds.Approved, ActorOf(origin), item.Id, item.Title,
                        report.Revision,
                        "Approved " + CountOf(kept.Length, "change", "changes") + " on " + Lower(item.Title) + ".",
                        kept.Select(l => l.Line).ToArray()));
                }
            }).ConfigureAwait(false);

            // Each row's own count of what landed, for the window (a row whose every line was left out landed nothing).
            if (run != null)
                foreach (var (item, allKept) in stillReady)
                {
                    var landed = allKept.Count(l => !leftOutLines.Contains(l.PlanItemId ?? ""));
                    if (landed == 0 && allKept.Length > 0)
                        PublishRun(run, wave, PlanRunPhases.Skipped, new[] { item.Id },
                            reason: skipped.FirstOrDefault(x => x.ItemId == item.Id)?.Reason ?? "Every change on it was left out.");
                    else PublishRun(run, wave, PlanRunPhases.Done, new[] { item.Id }, changes: landed);
                }

            // As in approve: the batch has committed, so a model exchanged after it does not make the
            // changes untrue. The sentence says what happened and the plan is left null.
            var left = SkippedSentence(skipped);
            var unsaved = await UnsavedOnWriterAsync(s).ConfigureAwait(false);
            return new ApplyPlanItemsResult
            {
                Performed = total > 0,
                Applied = total,
                // Only the rows where something landed: "2 tasks" must not count a row whose every line was left out (Kane, 13 Oct 2026).
                ItemIds = stillReady.Where(r => r.Kept.Length == 0 || r.Kept.Any(l => !leftOutLines.Contains(l.PlanItemId ?? ""))).Select(r => r.Item.Id).ToArray(),
                Skipped = skipped.ToArray(),
                Revision = report.Revision,
                EntryId = entryId,
                EntryIds = entryId == null ? Array.Empty<string>() : new[] { entryId },
                Waves = total > 0 ? 1 : 0,
                AlreadyIn = alreadyIn,
                Unsaved = unsaved,
                CanSave = unsaved,
                Plan = applied,
                Sentence = "Applied " + CountOf(total, "change", "changes") + " as one step you can undo."
                         + (alreadyIn == 0 ? ""
                            : " " + CountOf(alreadyIn, "task was", "tasks were") + " already in, so "
                              + (alreadyIn == 1 ? "it was" : "they were") + " not applied a second time.")
                         + (left.Length == 0 ? "" : " " + left)
                         + (bound.Nowhere && total > 0 ? " " + UnsavedNote : ""),
            };
        }

        /// <summary>What a press says about a row whose changes were worked out before the model last changed. A Jev row (jev.*) is worked out
        /// again by Jev itself, on the next read of the plan: the press that just wrote moved the model, so Jev looks again. Telling the person to
        /// "work them out again" sent them to cancel it, which drops Jev's lines and leaves the row empty until Jev answers again (M11 join part 4,
        /// E2, measured on the 15-table model with Jev live). Any other row is worked out again by the person, as before.</summary>
        internal const string JevStillLookingReason = "Jev is still looking at what the last step changed, and this task runs when Jev answers.";

        internal static string StaleRowSentence(ModelPlanItem item) =>
            item != null && item.Id != null && item.Id.StartsWith("jev.", StringComparison.Ordinal)
                ? JevStillLookingReason + " Open this task on the Plan to see what remains."
                : "The model changed after these changes were worked out. Open the task again to get new changes, then press Run the plan.";

        /// <summary>"Nothing was changed, because of this item", then the item's own reason.</summary>
        private static string ItemStopped(ModelPlanItem item, string reason) =>
            "Nothing was changed, because of " + Lower(item.Title) + ". " + reason;

        /// <summary>The item and the line that stopped the batch, in one sentence.</summary>
        private static string LineStopped(ModelPlanItem item, ChangeItem line)
        {
            var name = string.IsNullOrWhiteSpace(line.ObjectName) ? line.ObjectRef : line.ObjectName;
            var why = (line.Note ?? "it could not be made").Trim().TrimEnd('.');
            return "Nothing was changed, because " + name + " in " + Lower(item.Title)
                 + " could not be made: " + why + ".";
        }

        /// <summary>The History entry the batch just wrote, found by the plan rows it served (its cause), newest first. Null when History has no
        /// such entry (a model with nowhere to keep one still has the session's own, so this is rare) and never an error: the receipt's Undo
        /// then has no id and the window falls back to a plain undo.</summary>
        private string LedgerEntryOf(Session s, string[] itemIds)
        {
            try
            {
                return _sessions.Ledger.Entries(s.Id)
                    .Where(e => string.Equals(e.Kind, EditLedgerKinds.Edit, StringComparison.Ordinal)
                                && string.Equals(e.Cause?.Kind, ChangeCauseKinds.PlanItem, StringComparison.Ordinal)
                                && e.Cause.Ids != null && e.Cause.Ids.Intersect(itemIds, StringComparer.Ordinal).Any())
                    .Select(e => e.EntryId).LastOrDefault();
            }
            catch { return null; }
        }

        private static ApplyPlanItemsResult NothingToApply(long revision, ModelPlanView plan, string sentence) =>
            new ApplyPlanItemsResult { Performed = false, Revision = revision, Plan = plan, Sentence = sentence };

        /// <summary>The whole list refused before anything was written: recorded against the item that
        /// stopped it, on the model this call bound, then answered.</summary>
        private async Task<ApplyPlanItemsResult> BulkRefusedAsync(
            PlanMemoryBinding bound, ModelPlanItem item, string itemId, long revision, string origin, string sentence)
        {
            await RefusalWritePauseAsync().ConfigureAwait(false);
            var after = await ComposeBoundAsync(bound, memory => memory.History.Add(Entry(
                ModelPlanHistoryKinds.Refused, ActorOf(origin), item?.Id ?? itemId, item?.Title ?? "the plan", revision,
                "Nothing ran on the plan.", new[] { sentence }))).ConfigureAwait(false);
            return new ApplyPlanItemsResult
            {
                Performed = false, Revision = revision, Plan = after, FailedItemId = itemId, Sentence = sentence,
            };
        }

        // ---- cancel -------------------------------------------------------------------------------------

        /// <summary>Drop the set. Nothing was written when it was proposed, so nothing has to be undone, and
        /// the digest can never be approved afterwards because the lines behind it are gone.</summary>
        private async Task<ModelPlanActionResult> CancelItemAsync(Session s, ModelPlanItem item, string origin)
        {
            // One binding, taken before the first await, and used for every read of the session below.
            var bound = BindPlanMemory();
            var set = item.ChangeSet;
            if (set != null && string.Equals(set.State, ModelPlanChangeStates.Applied, StringComparison.Ordinal))
                return await RefusedAsync(item, "cancel", s.Revision, origin,
                    "These changes are already made, so there is nothing to drop. Use undo instead.", bound).ConfigureAwait(false);

            if (set == null)
                return await RefusedAsync(item, "cancel", s.Revision, origin,
                    "This task has no changes waiting, so nothing was dropped.", bound).ConfigureAwait(false);

            // The context this verb bound at its top, so the lines it drops are the ones the person was
            // shown on the model they were looking at.
            var context = bound.Context;
            await context.PlanGate.WaitAsync().ConfigureAwait(false);
            try
            {
                EnsureContextCurrent(context, "Plan cancel");
                var plan = context.Plans.Current;
                if (plan != null)
                {
                    // EXACTLY THE LINES THE PERSON WAS SHOWN, by the handles the set itself carries.
                    // This used to ask ItemIdForLine which item a line belonged to, and that function only
                    // knows the three families the plan raises itself: it never returns an "asst/" id,
                    // because those are assigned by FiledUnder when nothing else claims a line. So dropping
                    // an assistant's set said it was dropped and left every line on the plan, the next
                    // compose rebuilt the same set, and the digest the person had already approved still
                    // worked. Going by the displayed handles cannot drift from what was displayed, because
                    // it IS what was displayed.
                    var members = new HashSet<string>(
                        (set.Lines ?? Array.Empty<ModelPlanChangeLine>())
                            .Select(l => l.PlanItemId)
                            .Where(id => !string.IsNullOrWhiteSpace(id)),
                        StringComparer.OrdinalIgnoreCase);
                    plan.Remove(line => members.Contains(line.Id ?? ""));
                    PublishPlan(plan, s.Revision);
                }
            }
            finally { context.PlanGate.Release(); }

            var count = set.Lines?.Length ?? 0;
            // A Jev set dropped as a whole stays dropped: the next read would otherwise put the same advice
            // straight back (Plan/JevFamilies/LocalEngine.JevFamilies.cs).
            RememberJevDismissed(item);
            await VerbWritePauseAsync().ConfigureAwait(false);
            var after = await ComposeBoundAsync(bound, memory =>
            {
                memory.Changes.Remove(item.Id);
                memory.History.Add(Entry(ModelPlanHistoryKinds.Cancelled, ActorOf(origin), item.Id, item.Title,
                    s.Revision, "Dropped " + CountOf(count, "change", "changes") + " on " + Lower(item.Title) + ".",
                    Array.Empty<string>()));
            }).ConfigureAwait(false);
            // A cancel writes nothing to the model, so a drop that could not be recorded is a drop nobody
            // has to be told about in any other words than these.
            if (after == null) return ModelSwitchedDuringVerb(item, "cancel", s.Revision);

            return new ModelPlanActionResult
            {
                ItemId = item.Id, Action = "cancel", Performed = false, Revision = s.Revision, Plan = after,
                Sentence = "Nothing was changed. " + CountOf(count, "change was", "changes were") + " dropped.",
            };
        }

        // ---- working out an item's lines ----------------------------------------------------------------

        private static bool IsCleanupItem(string id) => string.Equals(id, "cleanup/model", StringComparison.Ordinal);

        /// <summary>The rule id on every removal line Work it out makes for the cleanup row, the way the starter rule marks
        /// its lines: the row carries only lines its own rule made (THE CLEANUP OFFER RULE, version 2, item 4).</summary>
        internal const string CleanupRuleId = "CLEANUP-UNUSED";

        private static bool IsProposalItem(string id) =>
            id != null && id.StartsWith("proposal/", StringComparison.Ordinal);

        /// <summary>What one item's changes actually are, read off the model and written nowhere.
        ///
        /// The safe fixes go through <c>MapSafeFix</c>, the SAME mapping propose_plan uses, called rather
        /// than copied. That matters more than it looks: the mapping knows that CAT-GEO only produces a line
        /// when the column's name resolves to a geographic category, and a second copy of that rule here
        /// would drift from it the first time either changed.</summary>
        private (List<ChangeItem> Seeds, List<ModelPlanChangeRefusal> Refused) CollectLinesForItem(
            Model m, ModelPlanItem item, Scorecard card, UnusedResult unused, string reviewedScope, ISet<string> removedMeasures = null,
            CleanupSweep cleanup = null)
        {
            var seeds = new List<ChangeItem>();
            var refused = new List<ModelPlanChangeRefusal>();

            if (IsSafeFixItem(item.Id))
            {
                var category = item.Id.Substring("safefix/".Length);
                foreach (var f in (card?.Findings ?? Array.Empty<ReadinessFinding>())
                             .Where(x => !x.Waived)
                             .Where(x => string.Equals(x.Fix, nameof(FixKind.SafeFix), StringComparison.Ordinal))
                             .Where(x => string.Equals(x.Category ?? "BestPractice", category, StringComparison.Ordinal))
                             .Where(x => !JevDecidedFinding(x.RuleId, x.ObjectRef)))
                {
                    var seed = MapSafeFix(m, f);
                    if (seed != null) seeds.Add(seed);
                    else refused.Add(new ModelPlanChangeRefusal
                    {
                        Target = f.ObjectRef,
                        Reason = "Semanticus has no exact change for this one, so it was left out.",
                    });
                }
                return (seeds, refused);
            }

            // The best-practice row: the Model quality scan's own fixes, less the ones it must not make
            // (Plan/LocalEngine.PlanBestPractice.cs). Worked out on the model as it is now.
            if (IsBpaFixItem(item.Id))
            {
                var (bpaLines, bpaRefused) = BpaPlanLines(m, BpaAnalyzer.Analyze(m, GetBpaRules(m)), ReadinessSafeLines(m, card, JevDecidedFinding));
                seeds.AddRange(bpaLines);
                refused.AddRange(bpaRefused);
                return (seeds, refused);
            }

            // The starter measures the rule decides, one fact table per item (Plan/JevFamilies/
            // StarterMeasuresFamily.cs). Every line goes through the model's DAX check before it is offered.
            if (StarterMeasuresFamily.IsRuleItem(item.Id))
            {
                var table = item.Id.Substring(StarterMeasuresFamily.RuleItemPrefix.Length);
                if (table.StartsWith("table:", StringComparison.Ordinal)) table = table.Substring("table:".Length);
                StarterMeasuresFamily.RuleLines(m, table, seeds, refused, removedMeasures);
                return (seeds, refused);
            }

            // Mark the date table, when the model's own DAX names the one table and date column (PlanDateMark). The
            // same mark the direct op makes; with no single answer the row stays the person's choice.
            if (string.Equals(item.Id, "datetable/model", StringComparison.Ordinal))
            {
                var mark = PlanDateMark.Candidate(m);
                if (mark == null)
                {
                    refused.Add(new ModelPlanChangeRefusal
                    {
                        Target = null,
                        Reason = "Semanticus cannot tell which table and date column to mark. You choose.",
                    });
                    return (seeds, refused);
                }
                var ctx = ReadItemContext(m, mark.TableRef, "mark_date_table");
                seeds.Add(new ChangeItem
                {
                    ObjectRef = mark.TableRef,
                    ObjectName = ctx.name ?? mark.Table,
                    Kind = "mark_date_table",
                    Source = "deterministic",
                    Category = CategoryForKind("mark_date_table"),
                    Severity = "High",
                    Risk = RiskForKind("mark_date_table"),
                    // The line stands for DATE-MARK ("No marked date table") and its write is about the table's data category, the
                    // property RuleAbout names for that rule. A line with no rule id is never asked about an acceptance, so this one
                    // marked a Date table whose "mark it" finding the person had accepted (round 7, F1).
                    RuleId = "DATE-MARK",
                    Target = "dataCategory",
                    Title = "Mark " + mark.Table + " as the date table",
                    Rationale = "Your date measures use " + mark.Table + "[" + mark.Column + "], but no table is marked as the date table.",
                    Before = ctx.before,
                    After = mark.Column,
                    Status = "approved",
                });
                return (seeds, refused);
            }

            if (IsCleanupItem(item.Id))
            {
                // Every column the sweep calls unused is a line: ticked when the rule suggests it (and Jev did not leave it out),
                // unticked otherwise, and each carries its reason. Ticked lines come first. Nothing is hidden: a column the
                // shape or a belt would hold is still listed.
                var ordered = new List<(UnusedItem Item, CleanupGuard.Pick Pick)>();
                foreach (var u in (unused?.Items ?? Array.Empty<UnusedItem>()))
                {
                    if (!string.Equals(u.Verdict, "safe", StringComparison.Ordinal))
                    {
                        refused.Add(new ModelPlanChangeRefusal
                        {
                            Target = u.Ref,
                            Reason = string.IsNullOrWhiteSpace(u.Reason)
                                ? "Something still uses this, so it was left out."
                                : u.Reason,
                        });
                        continue;
                    }
                    if (cleanup == null || !cleanup.Picks.TryGetValue(u.Ref ?? "", out var pick)) continue;
                    ordered.Add((u, pick));
                }
                foreach (var measure in cleanup?.Measures ?? Array.Empty<UnusedItem>())
                    refused.Add(new ModelPlanChangeRefusal
                    {
                        Target = measure.Ref,
                        Reason = "Measures are not removed here. Remove a measure by itself when you are sure no report uses it.",
                    });

                foreach (var (u, pick) in ordered.OrderBy(x => x.Pick.Ticked ? 0 : 1))   // stable: sweep order inside each half
                {
                    var ctx = ReadItemContext(m, u.Ref, "delete_if_unused");
                    seeds.Add(new ChangeItem
                    {
                        ObjectRef = u.Ref,
                        ObjectName = ctx.name,
                        Kind = "delete_if_unused",
                        Source = "deterministic",
                        // The mark that says this line is the cleanup row's own. add_plan_item never sets a rule id, so
                        // an assistant's removal cannot carry it and files on the assistant's row (ItemIdForLine).
                        RuleId = CleanupRuleId,
                        Category = CategoryForKind("delete_if_unused"),
                        Severity = "Medium",
                        Risk = RiskForKind("delete_if_unused"),
                        Target = TargetForKind("delete_if_unused"),
                        Title = "Remove unused " + (u.Kind ?? "object"),
                        Rationale = u.Reason ?? "Nothing in the model uses it.",
                        Before = ctx.before,
                        After = null,
                        // THE PRE-SELECTION. A suggested line arrives ticked and marked Suggested, and stays marked until the person
                        // (or the assistant they told) keeps, drops or edits it. Run the plan never applies a Suggested line, so the
                        // person always looks before anything is removed. An unticked line arrives dropped, as every removal
                        // did before: only a person's tick makes it apply.
                        Status = pick.Ticked ? "approved" : "proposed",
                        Suggested = pick.Ticked,
                        // The one plain reason on the line, built by the engine. Jev writes no text.
                        Reason = pick.Reason,
                        DecisionPoint = pick.JevLeftOut ? DecisionRegistry.CleanupSuggest : null,
                        DecisionConfidence = pick.JevConfidence,
                        DecisionWord = pick.JevWord,
                        // The set of checked reports this verdict was reviewed against. Apply refuses a removal whose basis has
                        // moved, so a person cannot approve an answer about one set of reports and have it acted on against
                        // another.
                        ReviewedScope = reviewedScope,
                    });
                }
                return (seeds, refused);
            }

            return (seeds, refused);
        }

        // ---- lines no item claims -----------------------------------------------------------------------

        /// <summary>One item per kind of change the assistant proposed that no existing item claims, so the
        /// plan stays the only list a person reads.
        ///
        /// Grouped by the KIND of change rather than one row per line, because thirty renames are one job and
        /// thirty rows are not a plan. Who is "asst" on every one: the lines came from the person's own
        /// assistant, which is who they go back to if the words are wrong.</summary>
        private static IEnumerable<ModelPlanItem> AssistantItems(IEnumerable<ChangeItem> unclaimed) =>
            (unclaimed ?? Array.Empty<ChangeItem>())
                .Where(l => l != null && !string.IsNullOrWhiteSpace(l.Kind))
                .GroupBy(l => l.Kind, StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g =>
                {
                    var count = g.Count();
                    var category = g.Select(x => x.Category).FirstOrDefault(c => !string.IsNullOrWhiteSpace(c));
                    return new ModelPlanItem
                    {
                        Id = AssistantItemPrefix + g.Key,
                        Section = SectionForCategory(category ?? "BestPractice"),
                        Title = Capital(DefaultTitle(g.Key)) + ", " + CountOf(count, "change", "changes"),
                        Sub = Join(g.Select(x => x.ObjectName).Where(n => !string.IsNullOrWhiteSpace(n)), 4),
                        Value = string.Empty,
                        Who = ModelPlanOwners.Assistant,
                        Status = ModelPlanStatuses.Proposed,
                        Why = new[]
                        {
                            "Your assistant worked " + CountOf(count, "change", "changes") + " out and put "
                            + (count == 1 ? "it" : "them") + " here for you to look at.",
                            "Nothing has been written. Keep the ones you want, drop the rest, then approve them.",
                        },
                        Diff = g.Select(HumanLine).Take(MaxDiffLines).ToArray(),
                        Touches = Objects(count),
                        Jump = new ModelPlanJump { Tool = "optimize", Label = "Pending changes" },
                    };
                });
    }

    /// <summary>One item named for bulk apply, with the digest the caller was shown for it.</summary>
    public sealed class PlanItemDigest
    {
        public string Id { get; set; }
        public string Digest { get; set; }
    }

    /// <summary>What <c>apply_plan_items</c> did, or plainly did not do.</summary>
    public sealed class ApplyPlanItemsResult
    {
        public bool Performed { get; set; }
        public int Applied { get; set; }
        public string[] ItemIds { get; set; } = Array.Empty<string>();
        public string FailedItemId { get; set; }
        public string FailedLine { get; set; }
        public string Sentence { get; set; }
        /// <summary>The assistant handoff after a press, shown beside the run receipt on the page.</summary>
        public string Note { get; set; }
        public long Revision { get; set; }
        public ModelPlanView Plan { get; set; }

        /// <summary>The History entry of this run's first batch: what <c>undo(entryId)</c> takes back, with every batch after it, so the
        /// whole press goes in one undo. Null when nothing was written or History has no entry.</summary>
        public string EntryId { get; set; }

        /// <summary>The History entry of each batch of the press, in order. <see cref="EntryId"/> is the first.</summary>
        public string[] EntryIds { get; set; } = Array.Empty<string>();

        /// <summary>The name of the press these results and the plan/runProgress events share. Null for apply by items.</summary>
        public string RunId { get; set; }

        /// <summary>How many batches the press ran: more than one when a row waited for a row in the press. Zero when nothing was written.</summary>
        public int Waves { get; set; }

        /// <summary>True when the open model has changes that are not saved. The receipt shows Save only when this and
        /// <see cref="CanSave"/> are both true.</summary>
        public bool Unsaved { get; set; }

        /// <summary>True when there is something to save. The window runs its own Save (the host asks where, for a model never saved; on a
        /// model open live from a workspace it writes the changes back to that workspace, as Publish does); Run the plan never saves by itself.</summary>
        public bool CanSave { get; set; }

        /// <summary>How many rows were already in. Not on the wire: the sentence says it.</summary>
        internal int AlreadyIn { get; set; }

        /// <summary>What Run the plan left out and why, each named: a row it could not take, or a removal whose
        /// object was in use by the time the batch reached it (lane E5, Sol 24 Sep). Empty when nothing was.</summary>
        public ApplyPlanSkip[] Skipped { get; set; } = Array.Empty<ApplyPlanSkip>();
    }

    /// <summary>One thing a run of the plan left out. <see cref="What"/> is null for a whole row, and names the
    /// object for one removal inside a row.</summary>
    public sealed class ApplyPlanSkip
    {
        public string ItemId { get; set; }
        public string Title { get; set; }
        public string What { get; set; }
        public string Reason { get; set; }
        /// <summary>"format" for an automatic format write the rule refused at the moment of writing; null for a removal left out
        /// because its object is in use. The sentence says which, so it never calls a format an in-use removal.</summary>
        public string Kind { get; set; }

        /// <summary>True for a best-practice fix the write-time check refused. Internal, so not on either wire: the
        /// sentence groups by it.</summary>
        internal bool IsFix { get; set; }

        /// <summary>The best-practice rule a left-out fix belongs to (null for anything else), so an answer too short to list
        /// every fix can say which bpa_scan read lists the rest (verify R5-2). On both wires: the agent door's answer is
        /// projected on the far side of the pipe, where an internal field would not arrive.</summary>
        public string RuleId { get; set; }
    }
}
