// The change half of the living plan: propose, keep, drop, approve, cancel
// (docs/redesign/contracts/change-contract.md, M3).
//
// WHAT THIS FILE IS FOR. The plan says what is wrong. Until now the only thing it could do about it was
// "start", which worked one kind of row and wrote immediately. That is the wrong shape for anything a person
// has an opinion about, and it is the wrong shape for an assistant, which should be able to work out a
// change and hand it over rather than make it. So every item can now carry a CHANGE SET: the exact lines,
// worked out against a named revision, written nowhere in the model, reviewable line by line, and applied
// only when somebody quotes the digest of exactly what they read.
//
// THE FOLD, AND WHAT IS AND IS NOT CLAIMED ABOUT IT. There were two lists. The Model Plan says what is wrong
// with the model; the change plan (propose_plan, get_plan, set_plan_item, add_plan_item, apply_plan,
// clear_plan) holds one reviewed batch of edits. This file makes the first a VIEW of the second: a change
// plan line is projected onto the plan item whose family and objects it serves, and a line no item claims
// opens an item of its own. There is now one list a person reads.
//
// What is NOT claimed: the change contract's own executor does not run here. Semanticus.Engine.Jev's
// InMemoryChangeSetService applies items to a dictionary and has never had a real IChangeTarget
// (ChangeContract.cs:258, the only implementation is the in-memory one). What this file takes from the
// contract is its VOCABULARY and its CHECKS, which is the valuable half: the digest over the lines and the
// versions, computed by the contract's own ComputeDigest; the order of checks on accept; refuse rather than
// merge when anything moved; all of the kept lines or none. What applies them is ApplyPlanAsync, the
// existing verified applier, which already holds the DAX equivalence gate, the circular-rewrite guard, the
// rename ordering, the delete recheck and the audit chain. Re-deriving that behind a change-set interface
// would delete verification, not fold it. So the tool descriptions say "in one undoable step, and refuses if
// the model moved", and none of them says "through the change contract".
//
// THE FIVE VERBS AND WHAT EACH COSTS THE MODEL.
//
//   propose   works out the lines and stores the header. WRITES NOTHING. The revision does not move.
//   keep/drop turns one line on or off (set_plan_item's existing approved flag). WRITES NOTHING. The digest
//             changes, because the digest is over the lines, so an approval given before the change cannot
//             be honoured after it. That is the point of it, not a side effect.
//   approve   the one verb that writes. The kept lines, all of them or none, one undo entry.
//   cancel    drops the set. WRITES NOTHING, and the digest can never be accepted afterwards.
//
// WHY PROPOSE DOES NOT MOVE THE REVISION AND WHY THAT MATTERS TO THE CACHE. The plan's expensive half is
// remembered per revision (LocalEngine.Plan.cs, PlanFactsEntry) because everything in it is a function of
// the model at that revision. A proposal is not a fact about the model, so it must not move the revision,
// and it must still show on the very next call. Both hold: the header lives in the plan's MEMORY and the
// lines live in the session's change plan, and the items are rebuilt from the remembered facts on every
// compose. The recompose is arithmetic, it costs nothing, and it picks the new set up. Nothing is regathered.
//
// THE DIGEST IS RECOMPUTED, NEVER READ BACK. What is stored is the header: which job, which revision and
// source version the lines were worked out against, which session, and whether it has been applied. The
// LINES are read live from the change plan on every call and the digest is recomputed over them. So a line
// that somebody re-authored through set_plan_item since the proposal changes the digest, and an approve
// quoting the old one is refused. Storing the digest and trusting it would be the one thing the contract
// exists to prevent.
//
// THE PROMISE APPROVE MAKES ABOUT GOING BACK, SAID OUT LOUD. The change contract asks accept to make a
// durable checkpoint and to refuse rather than write when it cannot (change-contract.md, order of checks,
// step 5). The only durable checkpoint this engine has is a git commit that SAVES THE OPEN MODEL FIRST
// (LocalEngine.HistoryCheckpoints.cs:50-81), refuses outside a repository, refuses on a detached head and
// refuses when somebody else's files are staged. Saving and committing a person's model because they
// approved four property edits is not a thing anybody asked for. No write path in this engine auto-
// checkpoints today, and apply_plan's own sentence already promises the weaker thing. So approve's way back
// is the SESSION UNDO TIMELINE: one undo entry, and the result sentence says so in those words rather than
// implying a durable one. change-contract.md records that choosing between the two promises is Kane's and
// nobody else's; this is the weaker one, implemented behind ONE switch (CheckpointPromise below) and
// disclosed. RefusedNoCheckpoint stays in the contract's enum, unused here on purpose: if it came back for
// every model outside a git repository it would stop meaning "there is nowhere to go back to".
//
// TWO GATES, AND THE ORDER THEY ARE TAKEN IN. The change plan is serialised by SessionContext.PlanGate
// (per context) and the model plan by LocalEngine._modelPlanGate (engine wide). They are NEVER held at the
// same time anywhere in this file. Where both are needed, the change plan is read first, under its own gate,
// released, and the snapshot handed to the model plan's side. Both gates are non-reentrant SemaphoreSlims,
// so nesting them in two orders would deadlock, and nesting them in one order would still deadlock the day
// somebody adds the other order.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Analysis;
using Semanticus.Engine.Decisions;
using TabularEditor.TOMWrapper;

namespace Semanticus.Engine
{
    public sealed partial class LocalEngine
    {
        /// <summary>What approve promises about going back. One switch, because which promise a person is
        /// given is Kane's decision and not this lane's (change-contract.md, "the weaker checkpoint promise,
        /// if Kane wants one"). Today it is the undo timeline; flipping it to the durable checkpoint is a
        /// one-word change here plus the branch in <see cref="CheckpointForApproveAsync"/>.</summary>
        internal enum CheckpointPromise
        {
            /// <summary>One undo entry, gone when the model closes. Always available.</summary>
            SessionUndo,

            /// <summary>A git commit for this model, which also saves the model first and is unavailable
            /// outside a repository. NOT the default, and not chosen by this lane.</summary>
            DurableCheckpoint,
        }

        internal CheckpointPromise ApproveCheckpointPromise = CheckpointPromise.SessionUndo;

        // ---- reading the change plan's lines -----------------------------------------------------------

        /// <summary>A snapshot of the change plan's lines, taken under the change plan's OWN gate and handed
        /// back so the model plan can be composed without either gate being held while the other is taken.
        /// An empty array when no change plan is open, which is the ordinary case and not a failure.</summary>
        private async Task<ChangeItem[]> PlanLinesSnapshotAsync()
        {
            var context = _sessions.CurrentContext;
            if (context == null) return Array.Empty<ChangeItem>();
            await context.PlanGate.WaitAsync().ConfigureAwait(false);
            try { return context.Plans.Current?.Snapshot() ?? Array.Empty<ChangeItem>(); }
            finally { context.PlanGate.Release(); }
        }

        /// <summary>Which plan item one change plan line belongs to, or null when no item claims it.
        ///
        /// Read off what the line IS, never off where it was added from, so a line the assistant added
        /// through add_plan_item lands on the same item as the identical line the engine worked out. The
        /// three families that claim lines are the three that can name an exact change: the safe fixes
        /// (grouped by readiness category, exactly as SafeFixItems groups its rows), the cleanup sweep, and
        /// the descriptions. Everything else is unclaimed, which is not a failure: an unclaimed line opens an
        /// item of its own so the plan stays the only list.</summary>
        private static string ItemIdForLine(ChangeItem line)
        {
            if (line == null) return null;
            // A Jev family names the item itself (JevFamilies/JevFamily.cs), and the plan composes that item
            // from these same lines, so the filing and the item cannot disagree.
            if (JevFamily.IsJevLine(line)) return line.JevItem;
            // A starter measure the rule decided files under its fact table's rule item. Before the safe fixes,
            // because it is deterministic too and would otherwise file under "safefix/Measures".
            if (string.Equals(line.RuleId, StarterMeasuresFamily.RuleId, StringComparison.Ordinal))
                return StarterMeasuresFamily.RuleItemId(TableOfObjectRef(line.ObjectRef ?? ""));
            if (string.Equals(line.Kind, "delete_if_unused", StringComparison.OrdinalIgnoreCase))
            {
                // The cleanup row says what it offers and that it never offers a measure, a key, a name, a date or the rest,
                // and "Keep all" on it would take every line on it along. So it carries ONLY the lines its own Work it out
                // made (marked with its rule id). Any other removal, a measure or a column, is the assistant's own
                // proposal and files on the assistant's row (verify, 29 Sep, D9 and B5).
                return string.Equals(line.RuleId, CleanupRuleId, StringComparison.Ordinal) ? "cleanup/model" : null;
            }
            // Descriptions are words, and the words have ONE row (describe/model was folded into it, round 1, 8 Oct 2026).
            if (string.Equals(line.Kind, "set_description", StringComparison.OrdinalIgnoreCase))
                return WordsItemId;
            if (string.Equals(line.Kind, "bpa_fix", StringComparison.OrdinalIgnoreCase))
                return BpaFixItemId;
            // Marking the date table is the date table row's one change, whoever worked it out (lane E5): the
            // engine's own line carries a category and would otherwise file under a safe-fix row.
            if (string.Equals(line.Kind, "mark_date_table", StringComparison.OrdinalIgnoreCase))
                return "datetable/model";
            if (string.Equals(line.Source, "deterministic", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(line.Category))
                return "safefix/" + line.Category;
            return null;
        }

        /// <summary>Which item a line is actually FILED under on this compose, or null for a line that belongs
        /// on no row. The family it serves when the plan has such an item, and the assistant's own row for that
        /// kind of change when it does not: a WAITING line whose gap has since closed, or one for a family the
        /// plan never raises, still belongs somewhere a person can see it.
        ///
        /// THE ONE LINE THAT BELONGS NOWHERE is a line that already RAN for a family item that has since left
        /// the plan. Approving a safe fix closes its gap, the item is not composed again, and its applied lines
        /// stay on the change plan as the receipt of that run. Filing them under an assistant row opened
        /// "asst/set_column_hidden" with no change set, a row saying the assistant proposed work that had in
        /// fact already run (P0b carried finding, PlanFootprintTests). The run's record is the item's done entry
        /// and the history, not a new row. An assistant's own applied lines are unaffected: their family IS the
        /// assistant row, so they stay its receipt as before.</summary>
        private static string FiledUnder(ChangeItem line, HashSet<string> claimed)
        {
            var id = ItemIdForLine(line);
            if (id != null && claimed.Contains(id)) return id;
            if (id != null && !IsPendingLine(line)) return null;
            return AssistantItemPrefix + (line.Kind ?? "");
        }

        /// <summary>The id prefix for an item the assistant's own changes opened. One constant, because
        /// three places have to agree about it: the projection that files a line under it, the compose that
        /// opens the item, and cancel, which has to drop exactly the lines the projection displayed.</summary>
        internal const string AssistantItemPrefix = "asst/";

        /// <summary>The id one line carries inside a set. Built from what the line is ABOUT, so a keep
        /// survives a re-read and a page can hold a row open while the list under it moves. The change plan's
        /// own "ci-0007" id is carried beside it, because that is what set_plan_item and apply_plan address,
        /// and it is deliberately NOT the line id: it is a position in a list that gets rebuilt.</summary>
        private static string LineIdFor(ChangeItem line) =>
            (string.IsNullOrWhiteSpace(line.RuleId) ? line.Kind : line.RuleId) + "/" + line.ObjectRef
            + (string.IsNullOrWhiteSpace(line.Target) ? "" : "#" + line.Target);

        // ---- projecting a set onto an item -------------------------------------------------------------

        /// <summary>Stamp one item with where it stands, and hang the change set built from the lines that
        /// belong to it.
        ///
        /// Pure arithmetic over what is already remembered and one array that was already read: it touches no
        /// model, takes no gate and writes nothing, which is what lets a proposal appear on the very next
        /// compose without the expensive half being gathered again.</summary>
        private static void StampChangeState(
            ModelPlanItem item, PlanMemoryState memory, ILookup<string, ChangeItem> linesByItem,
            long revision, string sourceVersion, string sessionId, string modelIdentity,
            ISet<string> undoneApplies = null)
        {
            if (item == null) return;

            PlanChangeHeader stored = null;
            if (item.Id != null && memory?.Changes != null) memory.Changes.TryGetValue(item.Id, out stored);
            // AN APPLIED HEADER WHOSE COMMIT WAS UNDONE IS NOT A RECEIPT FOR ANYTHING IN THE MODEL. The header is
            // written when the set lands and nothing rewrites it on undo, so read on its own it kept the row
            // "applied, checking": blocking nothing, and skipped as already in by the next run. The ledger knows
            // (UndoneApplies), and a row whose changes are no longer in the model is open again (contract 5.2).
            if (stored != null && undoneApplies != null && undoneApplies.Contains(item.Id ?? "")
                && string.Equals(stored.State, ModelPlanChangeStates.Applied, StringComparison.Ordinal))
                stored = null;

            var live = linesByItem == null ? Array.Empty<ChangeItem>() : linesByItem[item.Id ?? ""].ToArray();

            // A LINE THAT HAS ALREADY RUN IS A RECEIPT, NOT A PROPOSAL, and the two must never be mixed.
            // An item's id names a family ("describe/model"), so work keeps arriving under it after an
            // earlier set has been applied. Reading the stored "applied" state and hanging the newest lines
            // off it reported changes that had never run as already in, and then refused to approve them for
            // the same reason. Which of the two a line belongs to is a fact about the LINE.
            var pending = live.Where(IsPendingLine).ToArray();
            var landed = live.Where(l => !IsPendingLine(l)).ToArray();

            // A BEST-PRACTICE PRESS CAN UNCOVER NEW FIXES (hiding a key is what makes it qualify for the MDX rule), so
            // an applied receipt is not the row's last word: when the scan now asks for a write the receipt never
            // made, that is new work and the row is open again, not "applied, checking" for ever.
            if (IsBpaFixItem(item.Id) && pending.Length == 0 && stored != null
                && string.Equals(stored.State, ModelPlanChangeStates.Applied, StringComparison.Ordinal)
                && (item.DerivedKeys ?? Array.Empty<string>()).Except(landed.Select(BpaKey), StringComparer.OrdinalIgnoreCase).Any())
            {
                stored = null;
                landed = Array.Empty<ChangeItem>();
            }

            if (pending.Length > 0)
            {
                // New work. A fresh proposal, whatever the stored header says about an earlier run, and the
                // earlier run is carried beside it as a receipt rather than folded into it.
                var proposal = ProposalHeaderFor(stored, pending, sessionId, modelIdentity);
                item.ChangeSet = SetFor(proposal, pending, revision, sourceVersion, sessionId, modelIdentity,
                                        receipt: stored);
            }
            else if (stored != null)
            {
                item.ChangeSet = SetFor(stored, landed, revision, sourceVersion, sessionId, modelIdentity,
                                        receipt: null);
            }

            // A set-aside item keeps its set on show, because hiding it would lose work a person may want
            // back, but the item is set aside and that is what its state says.
            if (string.Equals(item.Status, ModelPlanStatuses.Skipped, StringComparison.Ordinal))
            {
                item.State = ModelPlanItemStates.Skipped;
                return;
            }

            if (item.ChangeSet == null) { item.State = ModelPlanItemStates.Gap; return; }

            switch (item.ChangeSet.State)
            {
                case ModelPlanChangeStates.Applied:
                    // The set landed. The gap may or may not have closed with it, and this state does not
                    // guess: if it closed, the item is not rebuilt at all on the next compose and the plan
                    // records it done (Plan/ModelPlanChanges.cs).
                    item.State = ModelPlanItemStates.Applied;
                    break;
                case ModelPlanChangeStates.Proposed:
                    item.State = ModelPlanItemStates.Proposed;
                    break;
                default:
                    // Refused, rolled back or cancelled: nothing was written, so the gap stands exactly as it
                    // did. The set stays attached so a person can read why it did not go in.
                    item.State = ModelPlanItemStates.Gap;
                    break;
            }
        }

        /// <summary>The plan items whose last applied set is no longer in the model. Two ways to know, one question:
        ///
        ///   THE TIMELINE. The newest edit ledger entry caused by that item is undone, or gone from both stacks
        ///   (EditLedger.NewestByCause, the same answer a draft's run and a task said done read).
        ///
        ///   THE MODEL. A restore opens a new session, and a hand edit is not the item's own undo, so neither moves
        ///   the item's ledger entry; yet the model can plainly be back where it was (UAT S01-j5-004: the four keys
        ///   visible again, the row still "applied"). The header keeps each line's before and after, so a line whose
        ///   object reads its BEFORE again, and not its after, says the set is no longer all in. Contract 5.2: a row
        ///   whose applied changes are no longer in the model is open again. A set that went in and only left part of
        ///   the gap open (after is still there) stays applied: that is "checking", not "undone".
        ///
        /// Read only for items whose stored header says applied, so a plan with nothing applied never touches the
        /// ledger or the model. An item with no such entry and no readable line is left as its header says.</summary>
        private async Task<HashSet<string>> UndoneAppliesAsync(Session s, PlanMemoryState memory)
        {
            var undone = new HashSet<string>(StringComparer.Ordinal);
            var applied = memory?.Changes?
                .Where(kv => kv.Value != null && string.Equals(kv.Value.State, ModelPlanChangeStates.Applied, StringComparison.Ordinal))
                .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
            if (s == null || applied == null || applied.Count == 0) return undone;
            foreach (var (id, newest) in _sessions.Ledger.NewestByCause(s.Id, ChangeCauseKinds.PlanItem))
                if (newest.TakenBack && applied.ContainsKey(id)) undone.Add(id);

            var toRead = applied.Where(kv => !undone.Contains(kv.Key) && (kv.Value.Lines?.Length ?? 0) > 0).ToList();
            if (toRead.Count == 0) return undone;
            try
            {
                var reverted = await s.ReadAsync(m => toRead
                    .Where(kv => kv.Value.Lines.Where(l => l != null && l.Kept).Any(l => LineIsBackToBefore(m, l)))
                    .Select(kv => kv.Key).ToList()).ConfigureAwait(false);
                undone.UnionWith(reverted);
            }
            catch { /* a read that fails says nothing either way; the header stands */ }
            return undone;
        }

        /// <summary>True when the object a landed line wrote reads that line's BEFORE again and not its after: the
        /// change is no longer in the model, however it left. Read with the change plan's own reader
        /// (LiveValueForPlanItem), so before, after and now are spelled the same way.</summary>
        private static bool LineIsBackToBefore(TabularEditor.TOMWrapper.Model m, ModelPlanChangeLine line)
        {
            var it = new ChangeItem { Kind = line.Op, ObjectRef = line.Target, Target = line.Property, Before = line.Before, After = line.After };
            if (IsRemovalKind(it.Kind)) return LiveValueForPlanItem(m, it) == "exists";
            if (string.Equals(it.Kind, "create_measure", StringComparison.OrdinalIgnoreCase))
            {
                try { return ObjectRefs.Resolve(m, it.ObjectRef) == null; } catch { return false; }
            }
            var now = LiveValueForPlanItem(m, it);
            return now != null && !PlanTextEquals(line.Before, line.After)
                && PlanTextEquals(now, line.Before) && !PlanTextEquals(now, line.After);
        }

        /// <summary>Whether a change plan line is still WAITING to be decided and run, as opposed to being
        /// the record of a run that already happened. The four waiting statuses are the ones add_plan_item
        /// and set_plan_item produce; "rejected" is a waiting line a person has dropped, which is still part
        /// of the proposal and is shown, visibly dropped, rather than vanishing.</summary>
        private static bool IsPendingLine(ChangeItem line) =>
            string.Equals(line.Status, "approved", StringComparison.OrdinalIgnoreCase)
            || string.Equals(line.Status, "proposed", StringComparison.OrdinalIgnoreCase)
            || string.Equals(line.Status, "needs_content", StringComparison.OrdinalIgnoreCase)
            || string.Equals(line.Status, "rejected", StringComparison.OrdinalIgnoreCase);

        /// <summary>The header for a set of lines that are still waiting.
        ///
        /// THE VERSIONS COME FROM THE LINES, not from the moment somebody read the plan, and that is the
        /// whole of this method. A proposal used to be stamped with the revision of whoever opened the page,
        /// so a description written about a measure, left while that measure's formula changed, came back
        /// wearing the new revision and read as current. Every line records what it was worked out against
        /// (Plan.cs, ProposedAtRevision), so the set is only as fresh as its OLDEST part, and it stays out of
        /// date until somebody works it out again.
        ///
        /// A stored header is reused only when it is itself a proposal. An applied one is a receipt for
        /// different lines and is handed back separately.</summary>
        private static PlanChangeHeader ProposalHeaderFor(
            PlanChangeHeader stored, ChangeItem[] pending, string sessionId, string modelIdentity)
        {
            var oldest = pending.OrderBy(l => l.ProposedAtRevision).First();
            var reusable = stored != null
                && (string.Equals(stored.State, ModelPlanChangeStates.Proposed, StringComparison.Ordinal)
                    || string.Equals(stored.State, ModelPlanChangeStates.RolledBack, StringComparison.Ordinal));

            // Only as fresh as the oldest thing in it: a stored proposal that has had a newer line added to
            // it is still bound to whatever it was first worked out against, and a stored proposal whose
            // lines are older than it claims is corrected down to them.
            var revision = reusable ? Math.Min(stored.Revision, oldest.ProposedAtRevision) : oldest.ProposedAtRevision;
            var source = reusable && stored.Revision <= oldest.ProposedAtRevision
                ? stored.SourceVersion
                : oldest.ProposedAtSource;

            return new PlanChangeHeader
            {
                JobId = reusable ? stored.JobId : "modelplan/" + (pending[0].Id ?? ""),
                // A live line is on THIS session's change plan by construction, and a change plan belongs to
                // one session context, so a line that can be read here belongs to this model and this
                // session. The stored identity still guards a set read back from the sidecar.
                SessionId = sessionId,
                ModelIdentity = modelIdentity,
                State = reusable ? stored.State : ModelPlanChangeStates.Proposed,
                Revision = revision,
                SourceVersion = source ?? "",
                StaleAtProposal = reusable && stored.StaleAtProposal,
                ProposedAt = reusable ? stored.ProposedAt : DateTimeOffset.UtcNow,
                Refused = reusable ? stored.Refused : Array.Empty<ModelPlanChangeRefusal>(),
                FailureReason = reusable ? stored.FailureReason : null,
                Lines = reusable ? stored.Lines : Array.Empty<ModelPlanChangeLine>(),
            };
        }

        /// <summary>The set a person reads, built fresh from the header and the lines that are live right
        /// now. The digest is recomputed here, over these lines and the header's versions, by the contract's
        /// own function, so the digest on the wire is always the digest approve will demand.</summary>
        private static ModelPlanChangeSet SetFor(
            PlanChangeHeader header, ChangeItem[] live, long revision, string sourceVersion, string sessionId,
            string modelIdentity, PlanChangeHeader receipt)
        {
            // The lines as they are NOW. When the session that proposed them is gone they cannot be read
            // back (the change plan lives in the session), so the snapshot taken at proposal is shown
            // instead, marked out of date. A person then sees what WAS proposed rather than an empty row.
            var fromSession = live.Length > 0;
            var lines = fromSession
                ? live.Select(LineOf).ToArray()
                : Detached(header.Lines);

            var set = new ModelPlanChangeSet
            {
                Id = header.JobId,
                State = header.State,
                Revision = header.Revision,
                SourceVersion = header.SourceVersion,
                SessionId = header.SessionId,
                ModelIdentity = header.ModelIdentity,
                StaleAtProposal = header.StaleAtProposal,
                ProposedAt = header.ProposedAt,
                AppliedAt = header.AppliedAt,
                Checkpoint = header.Checkpoint,
                Lines = lines,
                Refused = header.Refused ?? Array.Empty<ModelPlanChangeRefusal>(),
                FailureReason = header.FailureReason,
                KeptCount = lines.Count(l => l.Kept),
                // The previous run, carried BESIDE this proposal rather than folded into it, so a person can
                // see that something landed here before without the new work being reported as landed too.
                LastAppliedAt = receipt?.AppliedAt,
                LastAppliedCount = receipt?.Lines?.Count(l => l.Kept) ?? 0,
            };
            set.Digest = DigestOf(set);

            // Only a set that is still waiting can go out of date. An applied set's revision is behind by
            // definition, and calling that stale would be nonsense.
            if (string.Equals(set.State, ModelPlanChangeStates.Proposed, StringComparison.Ordinal))
            {
                if (!string.Equals(header.ModelIdentity ?? "", modelIdentity ?? "", StringComparison.Ordinal))
                {
                    set.Stale = true;
                    set.StaleReason = AnotherModel;
                }
                else if (!string.Equals(header.SessionId ?? "", sessionId ?? "", StringComparison.Ordinal))
                {
                    set.Stale = true;
                    set.StaleReason = ModelWasReopened;
                }
                else if (!fromSession && (header.Lines?.Length ?? 0) > 0)
                {
                    set.Stale = true;
                    set.StaleReason = ChangesWereDropped;
                }
                else if (set.Revision != revision)
                {
                    set.Stale = true;
                    set.StaleReason = ChangesOutOfDate;
                }
                else if (!string.Equals(set.SourceVersion ?? "", sourceVersion ?? "", StringComparison.Ordinal))
                {
                    set.Stale = true;
                    set.StaleReason = SourceOutOfDate;
                }
            }

            set.Sentence = SentenceFor(set);
            return set;
        }

        /// <summary>A snapshot of lines that are no longer on any change plan, safe to hand out.
        ///
        /// THE HANDLES ARE CUT. A stored line remembers the "ci-0001" the change plan knew it by, and that
        /// id is a POSITION in a list that gets rebuilt: clear the plan, add one unrelated change, and
        /// "ci-0001" now names that change instead. Handing the old snapshot back with its handle intact let
        /// a Keep or a Drop aimed at a safe fix nobody can see any more land on somebody else's rename. A
        /// detached line is for READING, so it carries no handle and nothing can be decided through it.
        ///
        /// The objects are copied rather than blanked in place, because the stored ones are what a later
        /// read shows and what an approve was bound to.</summary>
        private static ModelPlanChangeLine[] Detached(ModelPlanChangeLine[] snapshot) =>
            (snapshot ?? Array.Empty<ModelPlanChangeLine>()).Select(l => new ModelPlanChangeLine
            {
                Id = l.Id,
                PlanItemId = "",
                Line = l.Line,
                Op = l.Op,
                Target = l.Target,
                Property = l.Property,
                Before = l.Before,
                After = l.After,
                Kept = l.Kept,
                Suggested = l.Suggested,
                Provenance = l.Provenance,
                Reason = l.Reason,
                Check = l.Check,
                Args = l.Args,
                Suggestion = l.Suggestion,
                Note = l.Note,
            }).ToArray();

        /// <summary>One change plan line as one change set line.</summary>
        private static ModelPlanChangeLine LineOf(ChangeItem line) => new ModelPlanChangeLine
        {
            Id = LineIdFor(line),
            PlanItemId = line.Id,
            Line = HumanLine(line),
            Op = line.Kind,
            Target = line.ObjectRef,
            Property = line.Target,
            Before = line.Before,
            After = line.After,
            // KEPT IS THE CHANGE PLAN'S OWN APPROVAL and not a second flag beside it. One truth, so a keep
            // made through set_plan_item on either door and a keep made on the Plan page are the same act.
            Kept = string.Equals(line.Status, "approved", StringComparison.OrdinalIgnoreCase),
            Suggested = line.Suggested && string.Equals(line.Status, "approved", StringComparison.OrdinalIgnoreCase),
            Provenance = ProvenanceOf(line),
            Reason = line.Reason,
            // A SUGGESTION IS NOT A CHECK. The note on a Jev format line the rule could not prove is Semanticus saying it did
            // NOT check that the format fits, so it goes out as its own field the page prints as words, never as a check
            // mark (and neither door sends a "sure" beside it).
            Check = string.IsNullOrWhiteSpace(line.CheckText) || JevFormatSuggestion.IsSuggestion(line.CheckText)
                ? null : new ModelPlanChangeCheck { Exists = true, Text = line.CheckText },
            Suggestion = JevFormatSuggestion.IsSuggestion(line.CheckText),
            Note = JevFormatSuggestion.IsSuggestion(line.CheckText) ? line.CheckText : null,
            // A copy: the change plan's own dictionary is the applier's, and a footprint must not see it move.
            Args = line.Args == null ? null : new Dictionary<string, string>(line.Args, StringComparer.Ordinal),
        };

        /// <summary>The line a person reads, in the plan's own "+ ", "- ", "~ " shape so an item's change set
        /// and its <see cref="ModelPlanItem.Diff"/> read as one thing.</summary>
        private static string HumanLine(ChangeItem line)
        {
            var prefix = string.Equals(line.Kind, "delete_if_unused", StringComparison.OrdinalIgnoreCase) ? "- "
                       : string.IsNullOrEmpty(line.Before) || string.Equals(line.Before, "(none)", StringComparison.OrdinalIgnoreCase) ? "+ "
                       : "~ ";
            var name = string.IsNullOrWhiteSpace(line.ObjectName) ? line.ObjectRef : line.ObjectName;
            var what = string.IsNullOrWhiteSpace(line.Title) ? line.Kind : line.Title;
            return prefix + name + ": " + Lower(what);
        }

        /// <summary>Where a line came from, in the four kinds the contract allows. Built through the
        /// contract's own provenance type so a dishonest combination cannot reach the wire: a rule line
        /// carries no confidence, and a line the assistant authored names the request it came from.</summary>
        private static ModelPlanChangeProvenance ProvenanceOf(ChangeItem line)
        {
            // A line a Jev family chose. The decision kind, which the contract's constructor refuses without
            // both a point and a confidence, so a Jev line with either missing cannot reach the wire as one.
            if ((JevFamily.IsJevLine(line) || string.Equals(line.DecisionPoint, Semanticus.Decisions.DecisionRegistry.CleanupSuggest, StringComparison.Ordinal))
                && line.DecisionConfidence.HasValue && !string.IsNullOrWhiteSpace(line.DecisionPoint))
            {
                var decided = Wire(new Jev.ChangeProvenance(Jev.ChangeItemKind.Decision,
                    point: line.DecisionPoint, confidence: line.DecisionConfidence));
                // No confidence word on a suggestion, on either door: "sure" beside a format the rule could not prove says the opposite of
                // "check it fits" (addendum 3 item 17).
                decided.ConfidenceWord = JevFormatSuggestion.IsSuggestion(line.CheckText) ? null : line.DecisionWord;
                return decided;
            }

            // A line the assistant filled in. Generated says the value was authored rather than worked out,
            // which is exactly the assistant kind, and the plan item id is the request it came from.
            if (line.Generated || string.Equals(line.Source, "ai", StringComparison.OrdinalIgnoreCase))
                return Wire(new Jev.ChangeProvenance(Jev.ChangeItemKind.Assistant, point: line.Id ?? line.ObjectRef));

            // A rule line must name its rule. A line with no rule id is not dressed up as one: it names the
            // op it came from as the point instead, under the kind that needs no rule.
            if (!string.IsNullOrWhiteSpace(line.RuleId))
                return Wire(new Jev.ChangeProvenance(Jev.ChangeItemKind.Rule, rule: line.RuleId));

            return Wire(new Jev.ChangeProvenance(Jev.ChangeItemKind.Assistant, point: line.Id ?? line.Kind));
        }

        private static ModelPlanChangeProvenance Wire(Jev.ChangeProvenance p) => new ModelPlanChangeProvenance
        {
            Kind = p.Kind switch
            {
                Jev.ChangeItemKind.Rule => ModelPlanChangeKinds.Rule,
                Jev.ChangeItemKind.Decision => ModelPlanChangeKinds.Decision,
                Jev.ChangeItemKind.Proven => ModelPlanChangeKinds.Proven,
                _ => ModelPlanChangeKinds.Assistant,
            },
            Point = p.Point,
            Rule = p.Rule,
            Confidence = p.Confidence,
        };

        /// <summary>The digest, computed by the CONTRACT (Jev/ChangeContract.cs, ChangeSet.ComputeDigest), so
        /// there is one definition of what a digest covers and this file cannot drift from it. It covers the
        /// lines AND the two versions, which is what makes an approval an approval of one exact diff against
        /// one exact model.
        ///
        /// Every line goes in, kept or dropped. Dropping a line therefore changes the digest, so an approval
        /// a person gave before they dropped it cannot be honoured after. That is the whole mechanism.</summary>
        private static string DigestOf(ModelPlanChangeSet set)
        {
            var items = (set.Lines ?? Array.Empty<ModelPlanChangeLine>()).Select(l => new Jev.ChangeItem(
                op: (l.Op ?? "") + (l.Kept ? "|kept" : "|dropped"),
                target: l.Target ?? "",
                before: l.Before,
                after: l.After,
                provenance: new Jev.ChangeProvenance(
                    l.Provenance?.Kind == ModelPlanChangeKinds.Rule ? Jev.ChangeItemKind.Rule : Jev.ChangeItemKind.Assistant,
                    point: l.Provenance?.Kind == ModelPlanChangeKinds.Rule ? null : (l.Provenance?.Point ?? l.Id ?? "line"),
                    rule: l.Provenance?.Kind == ModelPlanChangeKinds.Rule ? (l.Provenance?.Rule ?? "rule") : null)));

            // Scope binds the digest to ONE model in ONE session. Without it the hash is a fact about
            // content alone, and two copies of a model produce the same content.
            return new Jev.ChangeSet(set.Id, set.Revision, set.SourceVersion, "", items,
                                     refused: null, staleReason: null, scope: ScopeOf(set)).Digest;
        }

        /// <summary>The world a set belongs to: the model's own durable identity and the session it was
        /// worked out in. The model identity is the one the engine already computes for everything else it
        /// keeps beside a model (PaneIdentity: "disk:&lt;hash of the path&gt;" or
        /// "live:&lt;endpoint|database&gt;"), so a proposal, a skip and a saved check all agree about which
        /// model they are about.
        ///
        /// A model with NO durable identity contributes an empty half rather than a made-up one. The session
        /// id is always real, so two unsaved models still cannot share a digest.</summary>
        private static string ScopeOf(ModelPlanChangeSet set) =>
            (set.ModelIdentity ?? "") + "\u001d" + (set.SessionId ?? "");

        /// <summary>The one line a person reads about a set. Counts, never characterises, and never says
        /// "safe" or "ready": whether a change is a good idea is not something a count can know.</summary>
        private static string SentenceFor(ModelPlanChangeSet set)
        {
            var total = set.Lines?.Length ?? 0;
            switch (set.State)
            {
                case ModelPlanChangeStates.Applied:
                    return CountOf(set.KeptCount, "change was", "changes were") + " applied in one step. "
                         + "Undo puts " + (set.KeptCount == 1 ? "it" : "them") + " back.";
                case ModelPlanChangeStates.Cancelled:
                    return "These changes were dropped. Nothing was changed.";
                case ModelPlanChangeStates.Refused:
                    return string.IsNullOrWhiteSpace(set.StaleReason)
                        ? "These changes were not applied. Nothing was changed."
                        : set.StaleReason;
                case ModelPlanChangeStates.RolledBack:
                    return "One of these changes could not be made, so the whole set was put back. Nothing was changed.";
                default:
                    if (set.Stale) return set.StaleReason;
                    if (total == 0) return "Nothing to change here.";
                    return CountOf(total, "change", "changes") + " worked out, "
                         + set.KeptCount.ToString("N0", CultureInfo.InvariantCulture)
                         + " kept. Nothing is changed yet.";
            }
        }

        internal const string ChangesOutOfDate =
            "The model changed after these changes were worked out, so they are out of date. Work them out again.";

        internal const string SourceOutOfDate =
            "The file on disk changed after these changes were worked out, so they are out of date. Work them out again.";

        internal const string ModelWasReopened =
            "The model was opened again since these changes were worked out, so they are out of date. Work them out again.";

        internal const string AnotherModel =
            "These changes were worked out against a different model, so they cannot be applied here. Work them out again.";

        internal const string ChangesWereDropped =
            "The list these changes were on has been cleared, so they are out of date. Work them out again.";

        // ---- the source version -------------------------------------------------------------------------

        /// <summary>What the model's SOURCE looks like right now, as one comparable string, so a proposal
        /// worked out before somebody edited the files under us is refused rather than applied on top.
        ///
        /// A METADATA STAMP, NOT A CONTENT HASH, and the difference is disclosed rather than glossed. The
        /// engine does have a content hash of the whole source (<c>ModelDiskStamp</c>, LocalEngine.cs) and the
        /// save guard uses it, but it reads every byte of every file and the plan composes on every call. So
        /// this one is names, lengths and write times: it catches an ordinary external edit, a checkout, and
        /// a file appearing or going away, and it does NOT catch a rewrite that leaves the length and the
        /// write time exactly as they were. The revision check beside it is the strong half.
        ///
        /// An unsaved model has no source that can move, and a live model's source is not on this disk at
        /// all. Both come back empty, which is honest: there is nothing to compare, so the source check
        /// passes and only the revision check stands. Empty is never a hash and is never mistaken for one.</summary>
        private string SourceVersionOf(Session s)
        {
            var source = s?.SourcePath;
            if (string.IsNullOrWhiteSpace(source) || ExperienceStore.IsEphemeralAnchor(source)) return "";

            try
            {
                var full = Path.GetFullPath(source);
                if (File.Exists(full))
                {
                    var fi = new FileInfo(full);
                    return "f:" + fi.Length.ToString(CultureInfo.InvariantCulture)
                         + ":" + fi.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture);
                }
                if (!Directory.Exists(full)) return "";

                long count = 0, bytes = 0, newest = 0;
                foreach (var file in Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories))
                {
                    var relative = Path.GetRelativePath(full, file).Replace('\\', '/');
                    // The engine's own sidecar lives in here and it moves every time the plan remembers
                    // anything, this very header included. Counting it would make every proposal go stale
                    // the instant it was stored.
                    if (IsEngineOrGitSidecar(relative)) continue;
                    var fi = new FileInfo(file);
                    count++;
                    bytes += fi.Length;
                    if (fi.LastWriteTimeUtc.Ticks > newest) newest = fi.LastWriteTimeUtc.Ticks;
                }
                return "d:" + count.ToString(CultureInfo.InvariantCulture)
                     + ":" + bytes.ToString(CultureInfo.InvariantCulture)
                     + ":" + newest.ToString(CultureInfo.InvariantCulture);
            }
            catch
            {
                // A source that cannot be read is not a source that moved, and refusing every approval over
                // it would be worse than the risk. Empty means "no source check", exactly as for a model
                // that has no source at all.
                return "";
            }
        }
    }
}
