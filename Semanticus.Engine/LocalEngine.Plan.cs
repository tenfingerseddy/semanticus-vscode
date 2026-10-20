// The living plan on the engine (docs/redesign/jev-plan.md section 2a, M2).
//
// THE ONE RULE THIS FILE EXISTS TO KEEP. The model is the truth and the plan is a view of it. Every item
// below is derived, on this call, from a fact the engine already had: a readiness finding, a cleanup
// verdict, a report reading, a saved check, a session flag. Nothing here invents work, and nothing here is
// written down and read back later, so the plan cannot drift from the model. The only things remembered
// between calls are the ones the model genuinely does not hold: which items a person skipped and why, which
// items were on the plan last time, and which have since gone (PlanMemory.cs explains why a finished item
// is remembered rather than recomputed).
//
// WHAT COSTS WHAT, AND WHAT IS REMEMBERED. One GATHER is one readiness scan (offline), one cleanup sweep
// (offline, reuses the last report reading and never goes to the network), one model read for the table map
// and one report-usage reading. All of that is a function of the model at one revision and of which reports
// have been read, so it is remembered per (session, revision, report basis) and the second call for the same
// model pays none of it. What is NOT remembered, because each can move without the revision moving, is the
// session read (unsaved changes, the live connection) and the saved-check read from the sidecar: those are
// read fresh on every call. The items themselves are rebuilt from the remembered facts every call, which is
// arithmetic, so a skip shows immediately and the plan still cannot disagree with the model.
//
// The grade projection is the second scan, and it no longer sits on the first look: it is rehearsed in the
// background for that revision and the first plan says so (worth.pending). See StartProjection for the one
// fact that makes a background rehearsal safe rather than dangerous.
//
// THE PROJECTED GRADE IS REHEARSED, NOT ESTIMATED. "What finishing it is worth" needs the grade the safe
// fixes would reach. Guessing it from the category averages is not possible honestly: the real score is
// weighted per RULE by severity inside each category and then gated, and a Scorecard only carries the
// category aggregates, so any arithmetic done out here would be a different formula wearing the same name.
// So it is not estimated. The safe fixes are applied for real inside a DryRunScope rehearsal, the analyzer
// is run on the mutated model INSIDE the same batch, and the batch is always rolled back
// (Session.RehearseAsync: no revision bump, no undo entry, no broadcast). The number is therefore the same
// number apply_safe_fixes would produce, and if the rehearsal cannot run the plan says the grade after is
// not known rather than showing one.
//
// PRO AND FREE. Every operation here is free and returns the whole plan. What Pro buys is the ORDER: the
// plan.rank point scores the items and the list is sorted by value. Without it the same items come back in
// the same sections with the same reasons and the same exact changes, in rule order, carrying no numbers,
// and `orderedBy` says so. That is the line Kane set in docs/redesign/ui-hierarchy.md.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Analysis;
using Semanticus.Decisions;
using Semanticus.Engine.Decisions;
using Semanticus.Engine.Lineage;
using TabularEditor.TOMWrapper;

namespace Semanticus.Engine
{
    public sealed partial class LocalEngine
    {
        /// <summary>How many "this measure has no check" items the plan will raise. A cap, not a sample: past
        /// this many, one more row is not what the person needs, and the item that names the cap says so.</summary>
        private const int MaxCheckItems = 10;

        /// <summary>How many lines of exact change one item shows before it says how many more there are. The
        /// diff is meant to be read, and forty lines is not read.</summary>
        private const int MaxDiffLines = 8;

        private readonly SemaphoreSlim _modelPlanGate = new SemaphoreSlim(1, 1);

        /// <summary>The rows the last call for this model showed that the plan does not remember (an assistant's
        /// "asst/" rows and Jev's rows), so RefreshedWhy can say they came or went. Kept in this engine only, never
        /// in the memory file: remembering them there is what told them as new at every redraw (round 3), and a
        /// read that only saw them must not write. Keyed by the open model's context, so a re-open or a restart
        /// starts with no record, and the first call then says nothing about them rather than guess. Read and
        /// written only under <see cref="_modelPlanGate"/>.</summary>
        private readonly System.Runtime.CompilerServices.ConditionalWeakTable<SessionContext, HashSet<string>> _otherRowsLastCall =
            new System.Runtime.CompilerServices.ConditionalWeakTable<SessionContext, HashSet<string>>();

        /// <summary>The expensive half of the last plan, kept so the next call does not pay for it again. Read
        /// and written only under <see cref="_modelPlanGate"/>, apart from the one background completion in
        /// <see cref="StartProjection"/>, which takes the gate before it publishes.</summary>
        private PlanFactsEntry _planFacts;

        /// <summary>How many times the expensive gather actually ran. A test seam, not an operation: neither
        /// door can reach it and nothing in the engine branches on it. It exists so the cache is proved by
        /// counting the work rather than by timing it, which is the only form of that test that cannot flake.</summary>
        private int _planGathers;
        internal int PlanGatherCountForTest => Volatile.Read(ref _planGathers);

        /// <summary>The last size scan taken in this session, per table, remembered so the Map can show a
        /// size without the plan ever going to the network.
        ///
        /// A size is a LIVE reading (vpaq_scan, over the storage DMVs) and the plan is offline by design, so
        /// the plan can only show a size somebody has already paid for. Until then there is no size, and the
        /// honest answer on the wire is null: not a zero, and not the file size standing in for the memory
        /// size. It is keyed by session id, so the reading cannot follow a person to the next model, and it
        /// is deliberately NOT keyed by revision, because a metadata edit does not invalidate a measured
        /// byte count and dropping it on every edit would mean the Map almost never had one.</summary>
        private sealed class PlanSizeScan
        {
            public string SessionId;
            public Dictionary<string, long> BytesByTable;
        }

        private PlanSizeScan _planSizes;

        /// <summary>Remember a size scan for the plan's Map, so the reading a person already paid for is the
        /// one the Map shows. Called by the scan itself; nothing here runs a scan.</summary>
        internal void RememberSizeScanForPlan(VpaqReport report)
        {
            var s = _sessions.Current;
            if (s == null || report == null || !string.IsNullOrEmpty(report.Error)) return;

            var bytes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in report.Tables ?? Array.Empty<VpaqTable>())
                if (!string.IsNullOrWhiteSpace(t.Name)) bytes[t.Name] = t.Size;

            _planSizes = new PlanSizeScan { SessionId = s.Id, BytesByTable = bytes };
        }

        /// <summary>The table map with this session's measured sizes stamped on. A scan can land without the
        /// revision moving, so the sizes are stamped onto a COPY on every call rather than written into the
        /// remembered facts, which are keyed by revision and would hold a size back until the next edit.</summary>
        private ModelPlanTable[] WithSizes(ModelPlanTable[] tables, Session s)
        {
            var scan = _planSizes;
            if (scan == null || !string.Equals(scan.SessionId, s.Id, StringComparison.Ordinal)) return tables;

            return (tables ?? Array.Empty<ModelPlanTable>()).Select(t =>
            {
                long bytes;
                return new ModelPlanTable
                {
                    Name = t.Name,
                    Kind = t.Kind,
                    Columns = t.Columns,
                    Measures = t.Measures,
                    Relationships = t.Relationships,
                    Findings = t.Findings,
                    SizeBytes = scan.BytesByTable.TryGetValue(t.Name ?? string.Empty, out bytes) ? bytes : (long?)null,
                };
            }).ToArray();
        }

        // ---- the operations --------------------------------------------------------------------------

        public async Task<ModelPlanView> GetModelPlanAsync() => await ComposeAsync(null).ConfigureAwait(false);

        public async Task<ModelPlanView> SkipModelPlanItemAsync(string id, string reason, string origin)
        {
            if (string.IsNullOrWhiteSpace(id))
                throw new ArgumentException("Name the task to set aside. Call get_model_plan for the ids.", nameof(id));
            if (string.IsNullOrWhiteSpace(reason))
                throw new ArgumentException(
                    "Say why you are setting this aside. The Plan shows the reason beside the task, so the "
                    + "next person sees it was a choice and not a mistake.", nameof(reason));

            var trimmed = reason.Trim();
            // The same cap as a pause reason and a note (PlanTasks.MaxNoteChars): the reason is shown beside the row on
            // every read and answer, and it was the one plan text with no cap (verify R5-1: a 45,000-character reason made
            // the skip answer 91,372 characters). Refused on both doors, so nothing is kept cut without the person knowing.
            if (trimmed.Length > PlanTasks.MaxNoteChars)
                throw new ArgumentException("That reason is longer than " + PlanTasks.MaxNoteChars + " characters.", nameof(reason));
            var revision = _sessions.Require().Revision;
            return await UnderReadyGateAsync(async () =>
            {
                // A DONE ROW IS NOT SET ASIDE (verify R5-3). Its work is finished, so there is nothing left to decide not to
                // do, and the skip used to be kept and written to History ("Set aside ...") while the row stayed done and the
                // answer said it was not set aside. Judged on the plan as it reads now, under the same gate as the write, and
                // refused by writing nothing, so both doors get the same plan back and the answer says why.
                var current = await ComposeAsync(null).ConfigureAwait(false);
                var row = current?.Items.FirstOrDefault(i => string.Equals(i.Id, id, StringComparison.Ordinal));
                if (row != null && string.Equals(row.Status, ModelPlanStatuses.Done, StringComparison.Ordinal)) return current;
                var ground = await SkipGroundAsync(id).ConfigureAwait(false);
                return await ComposeAsync(memory =>
                {
                    var existing = memory.Find(id);
                    if (existing != null) { existing.Reason = trimmed; existing.When = DateTimeOffset.UtcNow; }
                    else memory.Skips.Add(existing = new PlanSkip { ItemId = id, Reason = trimmed, When = DateTimeOffset.UtcNow });
                    existing.Ref = ground?.Ref; existing.Tag = ground?.Tag;

                    var title = TitleOf(memory, id);
                    // The reason lives in the detail, not in the sentence, so the timeline shows it as the
                    // person's own words under the line rather than folded into the plan's prose.
                    memory.History.Add(Entry(ModelPlanHistoryKinds.Skipped, ActorOf(origin), id, title, revision,
                        "Set aside " + Lower(title) + ".", new[] { trimmed }));
                }).ConfigureAwait(false);
            }).ConfigureAwait(false);
        }

        public async Task<ModelPlanView> UnskipModelPlanItemAsync(string id, string origin)
        {
            if (string.IsNullOrWhiteSpace(id))
                throw new ArgumentException("Name the task to put back. Call get_model_plan for the ids.", nameof(id));

            var revision = _sessions.Require().Revision;
            return await UnderReadyGateAsync(() => ComposeAsync(memory =>
            {
                memory.Skips.RemoveAll(s => string.Equals(s.ItemId, id, StringComparison.Ordinal));
                var title = TitleOf(memory, id);
                memory.History.Add(Entry(ModelPlanHistoryKinds.Unskipped, ActorOf(origin), id, title, revision,
                    "Put " + Lower(title) + " back on the plan.", Array.Empty<string>()));
            })).ConfigureAwait(false);
        }

        /// <summary>Act on one item. In this milestone exactly one KIND of item writes: a safe-fix row applies
        /// the deterministic fixes for THAT ROW's rules. Everything else answers plainly that it cannot yet and
        /// says what it is waiting for. That is deliberate: an action that half works is worse than one that
        /// says it is not here.
        ///
        /// TWO THINGS THIS METHOD IS CAREFUL ABOUT, both of them Astra findings on PR 385.
        ///
        /// It applies ONE ROW, not the sweep. It used to call apply_safe_fixes, which fixes every category, so
        /// starting "fix 3 safe things in visibility" also fixed the formatting row a person had deliberately
        /// set aside. The rows are grouped by rule category, so the fix is narrowed to that category's rule
        /// ids and every other row, skipped or not, is left exactly as it was.
        ///
        /// It is BOUND to the model it read. Composing a plan awaits a ranking, and a person can open another
        /// model while that await is in flight; the old code then took whatever session was current and wrote
        /// to it. The session and the revision are captured before the plan is read and checked again before
        /// the write AND inside the batch, because the outside check and the dispatcher hop are not one
        /// instant. A model that moved underneath is refused with a sentence, never written to.</summary>
        /// <summary>Act on one item. Five verbs now, and only ONE of them writes to the model.
        ///
        ///   propose   work out the exact changes and store them. Nothing is written.
        ///   approve   apply the kept lines, all of them or none, quoting the digest of what you read.
        ///   cancel    drop the worked-out changes. Nothing is written.
        ///   start     the old single verb: run a safe-fix row straight away, no review step.
        ///   done      only on a target the person typed: they finished it by hand and are saying so. It
        ///             writes the plan's own memory and NOTHING in the model, which is why it comes back
        ///             with Performed false and a sentence that says so.
        ///
        /// THE TARGET BRANCH IS FIRST, AND IT IS THE ONLY VERB SORTED BY THE ID RATHER THAN BY THE WORD. A
        /// target item is not a gap the rules found and has no change set, so none of the four verbs below
        /// can mean anything on it. It is answered here, on the one path both doors share, rather than at
        /// each door: a fork at the doors was how the two lanes shipped it, and two forks are two things
        /// that can drift apart.
        ///
        /// WHICH ARE FREE AND WHICH ARE PRO, and why the line is drawn where it is. Reading the plan,
        /// setting a row aside, putting it back, working changes out, keeping a line, dropping a line and
        /// dropping a set are all FREE: they are the assistant's work and the person's review, and none of
        /// them touches the model. Pro is needed exactly where the run path already needed it, which is a
        /// row the engine runs FOR you: start on an auto row, and approve on an auto row, which is start by
        /// another name on the same row with the same rules. Approve on a row that is yours to do is free,
        /// because there was never an engine run there to sell.</summary>
        /// <remarks><paramref name="digest"/> comes LAST and defaults to null on purpose. Both doors call
        /// this positionally (EngineRpcTarget binds by position over the pipe), so a new parameter anywhere
        /// but the end would silently rebind an existing caller's origin to the digest.</remarks>
        public async Task<ModelPlanActionResult> ModelPlanActionAsync(
            string id, string action, string origin, string digest = null)
        {
            if (string.IsNullOrWhiteSpace(id))
                throw new ArgumentException("Name the task to act on. Call get_model_plan for the ids.", nameof(id));

            var verb = (action ?? "start").Trim().ToLowerInvariant();

            // The head of the switch: a target item, and the one verb it answers. Before everything else,
            // because a target has no change set and no gap, so propose, approve, cancel and start have
            // nothing to work on and every one of them would have to refuse it separately.
            if (PlanTargets.IsTargetAction(id, verb))
                return await MarkPlanTargetDoneAsync(id, origin).ConfigureAwait(false);

            if (verb != "start" && verb != "propose" && verb != "approve" && verb != "cancel")
                throw new ArgumentException(
                    "The actions are 'propose' (work the changes out, nothing is written), 'approve' (apply the "
                    + "ones you kept), 'cancel' (drop them) and 'start' (run a task straight away). An aim you "
                    + "added answers 'done' instead, which records that you finished it by hand. Use "
                    + "skip_model_plan_item to set a task aside and unskip_model_plan_item to put it back.",
                    nameof(action));

            var s = _sessions.Require();
            var readAt = s.Revision;
            // One binding for this call, taken before the first await, so every refusal below records
            // against the model the caller asked about rather than whichever one is open by the time the
            // refusal is reached (Plan/LocalEngine.PlanVerbs.cs, and RefusedAsync's own note).
            var bound = BindPlanMemory();
            var plan = await ComposeAsync(null, bound).ConfigureAwait(false);
            var item = plan.Items.FirstOrDefault(i => string.Equals(i.Id, id, StringComparison.Ordinal));

            if (item == null)
                return new ModelPlanActionResult
                {
                    ItemId = id, Action = verb, Performed = false, Revision = s.Revision, Plan = plan,
                    Sentence = "No task on the Plan has that id. It may be finished or set aside since you last "
                             + "looked at the Plan.",
                };

            // The three review verbs. Propose and cancel write nothing at all, so they are free on every
            // tier; approve writes, and it is gated below on exactly the same condition start is.
            // Both change a row's set, and so the Ready digest: they take the approve gate here, at the door, and not
            // inside, because Run the plan calls ProposeItemAsync while it already holds that gate.
            if (verb == "propose")
                return await UnderReadyGateAsync(() => ProposeItemAsync(s, item, readAt, origin)).ConfigureAwait(false);
            if (verb == "cancel")
                return await UnderReadyGateAsync(() => CancelItemAsync(s, item, origin)).ConfigureAwait(false);
            if (verb == "approve")
            {
                if (string.Equals(item.Who, ModelPlanOwners.Auto, StringComparison.Ordinal)
                    && !Entitlement.FeatureGrants.IsProTier(_entitlement))
                {
                    await NoteFreeProRowsAsync(s, new[] { item }).ConfigureAwait(false);
                    return await RefusedAsync(item, verb, s.Revision, origin, RunIsProSentence, bound).ConfigureAwait(false);
                }
                return await ApproveItemAsync(s, item, digest, readAt, origin).ConfigureAwait(false);
            }

            if (string.Equals(item.Status, ModelPlanStatuses.Skipped, StringComparison.Ordinal))
                return await RefusedAsync(item, verb, s.Revision, origin,
                    "This task is set aside. Put it back first, then start it.", bound).ConfigureAwait(false);

            if (!string.Equals(item.Who, ModelPlanOwners.Auto, StringComparison.Ordinal))
                return await RefusedAsync(item, verb, s.Revision, origin,
                    string.Equals(item.Who, ModelPlanOwners.Assistant, StringComparison.Ordinal)
                        ? "Your assistant writes the words for this task. Ask it to, and it will use the same plan."
                        : "This task changes the model only after you approve it. Work out its changes, look at them and "
                        + "approve them on the Plan, or open " + (item.Jump?.Label ?? "the page it points at") + " and do it there.", bound).ConfigureAwait(false);

            // THE STARTER RULE ROW RUNS THROUGH APPROVE (N10). Approve and Run the plan both apply it, so start does too,
            // on the same path: its change set is worked out if it is not yet, then approved at the digest just read.
            // Same tier gate as approve, and the same one undo step.
            if (StarterMeasuresFamily.IsRuleItem(item.Id))
            {
                if (!Entitlement.FeatureGrants.IsProTier(_entitlement))
                    return await RefusedAsync(item, verb, s.Revision, origin, RunIsProSentence, bound).ConfigureAwait(false);
                var set = item;
                if (!string.Equals(set.ChangeSet?.State, ModelPlanChangeStates.Proposed, StringComparison.Ordinal)
                    || string.IsNullOrEmpty(set.ChangeSet?.Digest))
                {
                    var proposed = await UnderReadyGateAsync(() => ProposeItemAsync(s, item, readAt, origin)).ConfigureAwait(false);
                    set = proposed.Plan?.Items?.FirstOrDefault(i => string.Equals(i.Id, item.Id, StringComparison.Ordinal));
                    if (set?.ChangeSet?.Digest == null || !string.Equals(set.ChangeSet.State, ModelPlanChangeStates.Proposed, StringComparison.Ordinal))
                        return proposed;
                }
                return await ApproveItemAsync(s, set, set.ChangeSet.Digest, s.Revision, origin).ConfigureAwait(false);
            }

            if (!IsSafeFixItem(item.Id) && !IsBpaFixItem(item.Id))
                return await RefusedAsync(item, verb, s.Revision, origin,
                    "Semanticus cannot do this task for you yet. Open "
                    + (item.Jump?.Label ?? "the page it points at") + " and do it there.", bound).ConfigureAwait(false);

            // THE PLAN IS FREE TO READ AND PRO TO RUN (Kane, 22 September 2026). Reading it, setting a row
            // aside and putting it back are free, and they are the same operation family, so the boundary
            // cannot be drawn in FeatureMap: model_plan_action is classified free there on purpose. It is
            // drawn HERE, on the one path that mutates, and below every other refusal so that a row Pro
            // could not run either is still answered with the honest "open the page" line rather than with
            // an upsell for something nobody can run yet.
            //
            // The tier is read through FeatureGrants.IsProTier, the same single computation the ranking uses,
            // and not as a named ProFeature: running the plan is not one of the tabs. Astra finding 5 (P2):
            // the Studio had always reserved Start for Pro and a client calling the agent door directly could
            // start every runnable row.
            if (!Entitlement.FeatureGrants.IsProTier(_entitlement))
            {
                await NoteFreeProRowsAsync(s, new[] { item }, keptOnly: false).ConfigureAwait(false);
                return await RefusedAsync(item, verb, s.Revision, origin, RunIsProSentence, bound).ConfigureAwait(false);
            }

            // The model must still be the one the plan was read from. Checked here, and again inside the batch
            // below, because a mutation can commit between this line and the dispatcher picking the batch up.
            if (!ReferenceEquals(_sessions.Current, s))
                return await RefusedAsync(item, verb, s.Revision, origin,
                    "A different model is open now, so nothing was changed. Open the Plan for this model "
                    + "and start the task from there.", bound).ConfigureAwait(false);
            if (s.Revision != readAt)
                return await RefusedAsync(item, verb, s.Revision, origin,
                    "The model changed while Semanticus worked this out, so nothing was changed. Refresh the Plan "
                    + "and start the task again.", bound).ConfigureAwait(false);

            // The best-practice row has no fix of its own to call: start is "work it out and approve it", the same
            // two steps Run the plan takes on it, so what start does and what the row showed cannot differ.
            if (IsBpaFixItem(item.Id))
            {
                // Its fixes are already in (the row is done, or applied and checking, and the scan asks for nothing
                // new): there is nothing to work out, and working it out would answer with a refusal nobody earned
                // (verify, 28 Sep, L6).
                if (string.Equals(item.State, ModelPlanItemStates.Done, StringComparison.Ordinal)
                    || string.Equals(item.State, ModelPlanItemStates.Applied, StringComparison.Ordinal))
                    return new ModelPlanActionResult
                    {
                        ItemId = item.Id, Action = verb, Performed = false, Revision = s.Revision, Plan = plan,
                        Sentence = "These fixes are already made, so nothing was changed.",
                    };
                var worked = await UnderReadyGateAsync(() => ProposeItemAsync(s, item, readAt, origin)).ConfigureAwait(false);
                var set = worked.Plan?.Items.FirstOrDefault(i => string.Equals(i.Id, item.Id, StringComparison.Ordinal))?.ChangeSet;
                if (set?.Digest == null) return worked;
                return await ApproveItemAsync(s, worked.Plan.Items.First(i => i.Id == item.Id), set.Digest, s.Revision, origin).ConfigureAwait(false);
            }

            var fixes = await ApplyPlanSafeFixesAsync(s, item, readAt, origin).ConfigureAwait(false);
            NoteProFixes(s.Id, item, fixes.Applied.Length, origin: origin);

            // A start that ran and changed nothing is "started", not "done". It started; it finished no work,
            // and a timeline that called it done would report work nobody did.
            var kind = fixes.Applied.Length > 0 ? ModelPlanHistoryKinds.Done : ModelPlanHistoryKinds.Started;
            var line = "Ran " + Lower(item.Title) + ". "
                     + CountOf(fixes.Applied.Length, "change", "changes") + " applied.";
            var after = await ComposeAsync(memory => memory.History.Add(
                Entry(kind, ActorOf(origin), item.Id, item.Title, fixes.Revision, line, fixes.Applied)))
                .ConfigureAwait(false);

            return new ModelPlanActionResult
            {
                ItemId = item.Id,
                Action = verb,
                Performed = fixes.Applied.Length > 0,
                Revision = fixes.Revision,
                Plan = after,
                Sentence = fixes.Applied.Length == 0
                    ? "Nothing needed a fix, so nothing was changed."
                    : CountOf(fixes.Applied.Length, "thing was", "things were") + " fixed. "
                      + (fixes.Scorecard?.Grade == null ? "" : "The AI readiness grade is now " + fixes.Scorecard.Grade + ". ")
                      + "One undo takes it all back.",
            };
        }

        /// <summary>What a free session is told when it starts a row that would have run. The jump stays on
        /// the row in the plan the result carries, so "the page it names" is a real place and not a shrug.</summary>
        internal const string RunIsProSentence =
            RunIsProLead + " Open the page the task names to do it yourself.";

        /// <summary>The Pro line itself, shared word for word by every refusal that draws it.</summary>
        internal const string RunIsProLead = "Running the plan is part of Semanticus Pro.";

        /// <summary>What a free session is told when it asks for several items at once (F-238). There is no one
        /// page to name for a list, and approving Semanticus's own rows is Pro too (verify m11/plan-rows L8), so the way
        /// forward is the free one: each row's own page.</summary>
        internal const string BulkRunIsProSentence =
            RunIsProLead + " Nothing was changed. Open the page each task names to do it yourself.";

        /// <summary>Apply the safe fixes for ONE plan row, as one undoable step, on the session the plan was
        /// read from and only while that model has not moved.
        ///
        /// The row's id is "safefix/&lt;category&gt;" and its findings are the non-waived SafeFix findings in
        /// that category, so the rules to run are those findings' rule ids. They come from the readiness scan
        /// the plan already paid for, not a fresh one.</summary>
        private async Task<SafeFixResult> ApplyPlanSafeFixesAsync(Session s, ModelPlanItem item, long readAt, string origin)
        {
            var category = item.Id.Substring("safefix/".Length);
            var card = await PlanReadinessAsync(s).ConfigureAwait(false);
            var rules = new HashSet<string>(
                (card?.Findings ?? Array.Empty<ReadinessFinding>())
                    .Where(x => !x.Waived)
                    .Where(x => string.Equals(x.Fix, nameof(FixKind.SafeFix), StringComparison.Ordinal))
                    .Where(x => string.Equals(x.Category ?? "BestPractice", category, StringComparison.Ordinal))
                    .Where(x => !JevDecidedFinding(x.RuleId, x.ObjectRef))
                    .Select(x => x.RuleId)
                    .Where(r => !string.IsNullOrWhiteSpace(r)),
                StringComparer.Ordinal);

            List<string> applied = null;
            var rev = await s.MutateAsync(MutationOrigin(origin), "plan: " + item.Title, m =>
            {
                // The last gate, and the only one that cannot be raced: by here the dispatcher is ours, so a
                // revision that still matches means nothing committed between the check outside and this line.
                if (s.Revision != readAt)
                    throw new InvalidOperationException(
                        "The model changed while Semanticus worked this out, so nothing was changed. Refresh the "
                        + "Plan and start the task again.");
                applied = SafeFixes.Apply(m, rules, (rule, c) => JevDecidedFinding(rule, ObjectRefs.For(c)), DeclinedChangesOf(m).Declines);
            }).ConfigureAwait(false);

            var after = await s.ReadAsync(m => new ReadinessAnalyzer().Analyze(m)).ConfigureAwait(false);
            return new SafeFixResult { Revision = rev, Applied = applied?.ToArray() ?? Array.Empty<string>(), Scorecard = after };
        }

        /// <summary>The readiness scan behind the plan for the current model, from what the plan already read
        /// rather than a fresh scan.</summary>
        private async Task<Scorecard> PlanReadinessAsync(Session s)
        {
            await _modelPlanGate.WaitAsync().ConfigureAwait(false);
            try { return (await FactsAsync(s).ConfigureAwait(false)).Readiness; }
            finally { _modelPlanGate.Release(); }
        }

        /// <summary>A start the engine declined: recorded, then answered.
        ///
        /// It is written to the history because a refusal is the part of the story a person most needs and
        /// is least able to reconstruct ("I pressed start and nothing happened"). The plan is composed AFTER
        /// the entry is written, so the plan handed back already carries it and the caller never has to ask
        /// again to see what it just did.
        ///
        /// The refusal sentence is the DETAIL and not the sentence, because the one-line form of a refusal
        /// is which item did not run; the reason is what you open the entry for.
        ///
        /// IT TAKES THE CALLER'S BINDING, and every early refusal passes one. This used to call the unbound
        /// ComposeAsync, which binds at the moment it is CALLED. A verb binds at its top, works, and only
        /// then decides to refuse, so a model exchanged in between put THAT verb's item and THAT verb's
        /// reason into the NEW model's plan memory, under an item id that belongs to the old one. Propose
        /// was the clearest case, because it DETECTS the exchange and then recorded the detection in the
        /// wrong place, but the shape was the same in the early approve, cancel and start refusals.
        ///
        /// A refusal that could not be recorded is still a refusal, so the sentence is answered either way
        /// and the plan is left null, for the reason given on ComposeBoundAsync: the plan of the model that
        /// is open instead is not the plan the caller asked about.</summary>
        private async Task<ModelPlanActionResult> RefusedAsync(
            ModelPlanItem item, string verb, long revision, string origin, string sentence,
            PlanMemoryBinding bound)
        {
            await RefusalWritePauseAsync().ConfigureAwait(false);
            var after = await ComposeBoundAsync(bound, memory => memory.History.Add(Entry(
                ModelPlanHistoryKinds.Refused, ActorOf(origin), item.Id, item.Title, revision,
                "Nothing ran on " + Lower(item.Title) + ".", new[] { sentence }))).ConfigureAwait(false);

            return new ModelPlanActionResult
            {
                ItemId = item.Id, Action = verb, Performed = false, Revision = revision, Plan = after, Sentence = sentence,
            };
        }

        // ---- the history --------------------------------------------------------------------------------

        /// <summary>One structured entry, with <see cref="ModelPlanHistoryEntry.Text"/> filled from the same
        /// sentence so that anything still reading only Text keeps working.</summary>
        private static ModelPlanHistoryEntry Entry(
            string kind, string actor, string itemId, string title, long revision, string sentence, string[] detail) =>
            new ModelPlanHistoryEntry
            {
                When = DateTimeOffset.UtcNow,
                Kind = kind,
                Actor = actor,
                ItemId = itemId,
                Title = title,
                Revision = revision,
                Sentence = sentence,
                Text = sentence,
                Detail = detail ?? Array.Empty<string>(),
            };

        /// <summary>Who the engine records an action as. The origin string is the only thing it knows about
        /// who asked: the agent door says "agent", a step of a run-all says "plan", and everything else is a
        /// person at the keyboard. It is a label on a story, not a permission: both doors can do the same
        /// things and the entitlement checks are elsewhere.</summary>
        private static string ActorOf(string origin) =>
            string.Equals(origin, "agent", StringComparison.OrdinalIgnoreCase) ? ModelPlanActors.Assistant
            : string.Equals(origin, ModelPlanActors.Plan, StringComparison.OrdinalIgnoreCase) ? ModelPlanActors.Plan
            : ModelPlanActors.You;

        /// <summary>The origin a MUTATION is attributed to. "plan" is a label for the history and not a
        /// writer the rest of the engine knows about, so a run-all step commits as the person who pressed
        /// the button.</summary>
        private static string MutationOrigin(string origin) =>
            string.Equals(origin, "agent", StringComparison.OrdinalIgnoreCase) ? "agent" : "human";

        /// <summary>An item's title in the words the person read, from what the plan remembers. Falls back to
        /// the id, which is at least true, rather than to an invented sentence.</summary>
        private static string TitleOf(PlanMemoryState memory, string itemId)
        {
            var seen = memory?.LastItems?.FirstOrDefault(i => string.Equals(i.ItemId, itemId, StringComparison.Ordinal));
            if (!string.IsNullOrWhiteSpace(seen?.Title)) return seen.Title;
            var done = memory?.Done?.FirstOrDefault(d => string.Equals(d.ItemId, itemId, StringComparison.Ordinal));
            return string.IsNullOrWhiteSpace(done?.Title) ? itemId : done.Title;
        }

        private static string[] Titles(IEnumerable<string> titles) =>
            (titles ?? Array.Empty<string>()).Where(t => !string.IsNullOrWhiteSpace(t)).ToArray();

        /// <summary>The plan's top three, as the orientation's next-step hints. This REPLACES the fixed ladder
        /// that SuggestNextActions built: one list, two doors, one source of truth about what to do next.</summary>
        public async Task<NextAction[]> GetNextActionsAsync()
        {
            if (_sessions.Current == null)
                return new[]
                {
                    new NextAction
                    {
                        Op = "open_model",
                        Args = "path: <.pbip | TMDL folder | .bim>",
                        Reason = "No model is open, so there is no plan to make.",
                    },
                };

            var plan = await ComposeAsync(null).ConfigureAwait(false);
            return TopThree(plan);
        }

        /// <summary>The plan's top three as navigation hints. Open, unskipped items only, in the plan's own
        /// order, so the Overview card and this list can never disagree.</summary>
        internal static NextAction[] TopThree(ModelPlanView plan) =>
            (plan?.Items ?? Array.Empty<ModelPlanItem>())
                .Where(IsOpen)
                .Take(3)
                .Select(i => new NextAction
                {
                    Op = "get_model_plan",
                    Args = "itemId: " + i.Id,
                    Reason = i.Title + (string.IsNullOrWhiteSpace(i.Sub) ? "" : ": " + i.Sub) + ".",
                })
                .ToArray();

        private static bool IsOpen(ModelPlanItem i) =>
            string.Equals(i.Status, ModelPlanStatuses.Ready, StringComparison.Ordinal)
            || string.Equals(i.Status, ModelPlanStatuses.Proposed, StringComparison.Ordinal);

        // ---- the seam that binds a plan write to ONE model --------------------------------------------

        /// <summary>Where one session's plan memory lives: the sidecar file and the durable model identity it
        /// is keyed by, both read off the session context that was current at the moment this was taken.
        ///
        /// IT EXISTS BECAUSE RESOLVING THOSE TWO THINGS LATER IS A DIFFERENT MODEL'S ANSWER. SessionContext
        /// says of itself that the whole object is exchanged at once "so work that captured the old context
        /// can never publish into the new model's plan", and that promise only holds for code that actually
        /// captured it. ComposeAsync captured the SESSION and then read the sidecar path and the identity
        /// from _sessions.CurrentContext after two awaits, so a model opened during that wait was handed the
        /// previous model's target, proposal or skip, and the caller was then told its session had gone. The
        /// write had already landed on the wrong model by then (Astra 6 on the Fix slice).
        ///
        /// BOTH HALVES OF THE FIX SLICE GO THROUGH HERE. The target lane's add, remove and mark-done bind
        /// before they call, and the change-set lane's propose has the same shape: it captures a header
        /// under the change-plan gate and stores it through a LATER ComposeAsync, so it binds once at the
        /// top and passes the binding down the same way.</summary>
        internal sealed class PlanMemoryBinding
        {
            internal SessionContext Context { get; init; }
            internal Session Session { get; init; }
            internal string File { get; init; }
            internal string Identity { get; init; }

            /// <summary>True when this model has nowhere durable to keep anything: never saved, not live.
            /// Not an error by itself; it is only an error when something was asked to be kept.</summary>
            internal bool Nowhere => File == null || Identity == null;
        }

        /// <summary>Bind the plan memory to the model open RIGHT NOW, before any await. Every caller that is
        /// going to write must do this first and carry the result; a binding taken after an await is exactly
        /// the bug this type exists to stop.</summary>
        internal PlanMemoryBinding BindPlanMemory()
        {
            if (_planRowBatch.Value != null)
            {
                var bound = _planRowBatch.Value.Bound;
                if (!PlanMemoryStillOpen(bound)) throw new InvalidOperationException(BulkPlanModelSwitchedSentence);
                return bound;
            }
            // ONE read of the context, so the session, the path and the identity are all the same model's.
            // Two reads are two chances to straddle an exchange.
            var context = _sessions.CurrentContext;
            var session = context.Session
                ?? throw new InvalidOperationException("No model is open. Call 'open' first.");
            return new PlanMemoryBinding
            {
                Context = context,
                Session = session,
                File = PlanMemory.FileIn(PlanSidecarDir(context)),
                Identity = PaneIdentity(context.Session, context.Live),
            };
        }

        /// <summary>True while the model this binding was taken from is still the open one. The WHOLE
        /// context is compared, not the session, because the context is the unit SessionManager exchanges.</summary>
        internal bool PlanMemoryStillOpen(PlanMemoryBinding binding) =>
            binding != null && ReferenceEquals(_sessions.CurrentContext, binding.Context);

        /// <summary>What a person is told when the model was swapped while their write was in flight. It says
        /// nothing was kept because nothing was: the refusal happens BEFORE the write, so the target, the
        /// proposal or the skip landed on neither model rather than on the wrong one.</summary>
        internal const string PlanMemoryModelSwitchedSentence =
            "A different model is open now, so nothing was kept. Open the Plan for this model and try "
            + "again there.";

        /// <summary>Write to the plan memory of the model the caller bound to, or refuse. Never writes to any
        /// other model, and never writes half of anything: the two checks are both before the write.</summary>
        private void WritePlanMemory(PlanMemoryBinding binding, Action<PlanMemoryState> change)
        {
            if (!PlanMemoryStillOpen(binding))
                throw new InvalidOperationException(PlanMemoryModelSwitchedSentence);
            if (!PlanMemory.Mutate(binding.File, binding.Identity, CountPlanRowChanges(change)) && binding.Nowhere)
                throw new InvalidOperationException(
                    "Semanticus has nowhere to keep this yet. Save the model to a file first, then try again.");
        }

        /// <summary>The same write, best effort, for the READ path's own bookkeeping. A plan that could not
        /// record what it saw is still a correct plan, so this never throws. It also declines once the model
        /// has been exchanged, and the honest cost is small and worth naming: a refresh that raced a model
        /// swap does not record its item list, so the next refresh note for that model says it was drawn for
        /// the first time. That is better than a note about one model written into another's file.</summary>
        private bool TryWritePlanMemory(PlanMemoryBinding binding, Action<PlanMemoryState> change)
        {
            if (!PlanMemoryStillOpen(binding)) return false;
            if (binding.Nowhere)
            {
                // No file yet: what the plan saw and saw finish is kept for as long as the model is open, and moves into the file at the first save.
                var held = HeldPlanState(binding);
                lock (held) { change(held); PlanTasks.Trim(held); }
                return true;
            }
            return PlanMemory.Mutate(binding.File, binding.Identity, change);
        }

        // ---- composing the plan ----------------------------------------------------------------------

        /// <summary>Build the plan. <paramref name="change"/> is applied to the remembered state before the plan
        /// is composed, so a skip and the plan that reflects it are one call and cannot be read apart.
        ///
        /// <paramref name="bound"/> is for a caller that did work BEFORE calling this and must not have the
        /// model change underneath it in between: it binds once at the top and passes that binding here, so
        /// the whole operation is about one model. Left null, this binds for itself, which is right for every
        /// caller whose first act is to compose.</summary>
        private async Task<ModelPlanView> ComposeAsync(Action<PlanMemoryState> change, PlanMemoryBinding bound = null)
        {
            var batch = _planRowBatch.Value;
            if (batch != null)
            {
                if (bound != null && !ReferenceEquals(bound.Context, batch.Bound.Context))
                    throw new InvalidOperationException(BulkPlanModelSwitchedSentence);
                if (change != null) WritePlanMemory(batch.Bound, change);
                return PlanRowBatchView(batch);
            }
            // Bound BEFORE any await, and everything below reads the model out of the binding rather than
            // out of whatever is current when an await returns.
            var binding = bound ?? BindPlanMemory();
            var s = binding.Session;

            // A test seam, and the only way to prove what happens when the open model is exchanged WHILE a
            // plan write is in flight. Null on every real path; only Semanticus.Tests, through
            // InternalsVisibleTo, ever assigns it.
            var pause = PlanComposePauseForTest;
            if (pause != null) await pause().ConfigureAwait(false);

            // The change plan's lines, read under the CHANGE PLAN's own gate and released before this one is
            // taken. The two gates are never held together anywhere, because both are non-reentrant and a
            // second order would deadlock the day somebody added one (LocalEngine.PlanChanges.cs).
            // THE JEV FAMILIES, before the change plan is read, so the lines already decided for this revision
            // are in the snapshot below and the Jev items are composed from them. This never waits for Jev: a
            // revision nothing has been decided for starts a background run and the plan says Jev is looking
            // (Plan/JevFamilies/LocalEngine.JevFamilies.cs). Pro only; a free session starts nothing.
            var jev = await KickJevFamiliesAsync(binding).ConfigureAwait(false);

            // One line per change: where a Jev line and a rule's line would write the same property of the same
            // object, only one of them is on the plan (LocalEngine.JevFamilies.cs, OneLinePerChange).
            var planLines = OneLinePerChange(await PlanLinesSnapshotAsync().ConfigureAwait(false),
                l => jev.Live && jev.LivePoints.Contains(l.DecisionPoint ?? ""));

            await _modelPlanGate.WaitAsync().ConfigureAwait(false);
            try
            {
                // What is remembered is kept under the model's OWN durable identity, because the sidecar
                // belongs to the folder and two models can share one. No identity (never saved, not live)
                // means nothing can be remembered, and a skip says so rather than landing in a shared bucket.
                // Both come off the binding, so they name the model this call is about and not the one that
                // happens to be open now.
                if (change != null) WritePlanMemory(binding, change);
                // Repeat cards that came due join the tasks before they are read (lane RP, Plan/PlanRepeats.cs).
                // A repeat whose task is set aside or whose object is gone rests (UAT S12-6-002, S12-6-003).
                var repeatGrounds = await RepeatGroundsNowAsync(binding).ConfigureAwait(false);
                ComeDueOnCompose(binding, repeatGrounds);
                // A task whose closing edit History took back is open again before anything reads it (UAT S01-j2-007).
                ReconcileTakenBackTasks(binding);
                // A row set aside whose object was renamed has a new id now; the skip moves to it before anything reads it (hero15 H13).
                await FollowSkipRenamesAsync(binding).ConfigureAwait(false);
                await FollowTaskRenamesAsync(binding).ConfigureAwait(false);

                // The memory is read BEFORE the facts, so every save mark in it (LastSave) was written before the model
                // was read. A later one would credit a save the facts never saw ("Save the changes you have made").
                var memory = ReadPlanState(binding);
                var facts = await FactsAsync(s).ConfigureAwait(false);
                var factsRead = PlanComposeFactsReadForTest;
                if (factsRead != null) await factsRead().ConfigureAwait(false);
                // ONE DECISION PER COLUMN'S BASE MEASURE. While column.summarize is live it picks every number column's
                // explicit base measure (ColumnSummarizeFamily), so the starter rule's items stand aside, exactly as they
                // did for measure.starter (which lost its replay and stays off).
                facts.HeldClaims = new HashSet<string>(PlanTasks.HeldClaimsOf(memory.Tasks.Where(t => SkipFor(memory, t) == null)), StringComparer.OrdinalIgnoreCase);
                facts.SetAsideClaims = new HashSet<string>(PlanTasks.SetAsideClaimsOf(memory.Tasks, t => SkipFor(memory, t) != null), StringComparer.OrdinalIgnoreCase);
                facts.StarterByJev = jev.StarterLive || jev.LivePoints.Contains(DecisionRegistry.ColumnSummarize);
                // ONE OWNER PER FORMAT. A kept line of a live Jev point that sets an object's format owns that object, and so does
                // a SUGGESTION Jev made with some confidence (a format the rules cannot prove, unticked until the person ticks it): the
                // M11 join, item 9, since the rules' row can only advise there (THE FORMAT FILL RULE) and a column with Jev's
                // suggestion beside the rules' advice showed two proposals for one column. A dropped line Jev was UNSURE of does not
                // own, so the rule keeps the column it could not beat.
                facts.JevFormatted = new HashSet<string>(
                    planLines.Where(l => jev.Live && JevFamily.IsJevLine(l) && jev.LivePoints.Contains(l.DecisionPoint ?? "")
                                         && (string.Equals(l.Status, "approved", StringComparison.OrdinalIgnoreCase)
                                             || (l.Suggestion && !string.Equals(l.DecisionWord, JevFamily.Unsure, StringComparison.Ordinal)))
                                         && string.Equals(l.Target, "formatString", StringComparison.OrdinalIgnoreCase)
                                         && !string.IsNullOrWhiteSpace(l.ObjectRef))
                             .Select(l => l.ObjectRef),
                    StringComparer.OrdinalIgnoreCase);
                // The rule's date table row stands aside only while a SURE Jev line is waiting: Jev when sure, else the rule.
                facts.DateTableByJev = jev.DateTableLive && planLines.Any(l => JevFamily.IsJevLine(l) && IsPendingLine(l)
                    && string.Equals(l.DecisionPoint, DecisionRegistry.DateTable, StringComparison.Ordinal));
                var candidates = BuildItems(facts, JevDecidedFinding);
                candidates.AddRange(JevFamily.Items(planLines.Where(IsPendingLine)));
                // A run that stopped before Jev answered anything leaves no "what Copilot sees" row to hang its
                // progress on, and the Trust section then reads as nothing to change for Copilot (verify round 4,
                // r4-11). Added here, before the change state and the state word, so it is an ordinary row.
                var notFinished = JevProgressRow(candidates, s.Id);
                if (notFinished != null) candidates.Add(notFinished);

                // Apply what the person decided. A skip does not remove an item; it marks it, with the reason,
                // so the plan still says the work exists and that somebody chose not to do it.
                foreach (var item in candidates)
                {
                    var skip = memory.Find(item.Id);
                    if (skip == null) continue;
                    item.Status = ModelPlanStatuses.Skipped;
                    item.SkipReason = skip.Reason;
                    item.SkippedUtc = skip.When;
                }

                // The change half, and the fold of the two plans into one list. Every change plan line is
                // filed under the item whose family and objects it serves; a line no item claims opens an
                // item of its own, owned by the assistant, so the plan is the only list a person reads.
                // Arithmetic over what was already read: no model, no gate, no write, so a proposal shows on
                // the next call without the revision having to move (LocalEngine.PlanChanges.cs).
                var sourceVersion = SourceVersionOf(s);
                var claimed = new HashSet<string>(candidates.Select(i => i.Id), StringComparer.Ordinal);
                // ONE membership rule, worked out once. FiledUnder is the only thing that says which item a
                // line belongs to, and every other reader of that question goes through this dictionary, so
                // the set a person is shown, the set cancel drops and the set approve applies cannot disagree
                // about what is in them. It also removes a sentinel string that used to stand in for "no item
                // claims this", which is the kind of value that reads as nothing in every review surface.
                //
                // THE ORDER OF THE LINES IS PART OF THE ANSWER. The digest is computed over the lines in the
                // order they come back, so anything that shuffles them shuffles the digest and an approve
                // quoting yesterday's digest is refused for a reason nobody can see. A dictionary keyed by
                // the line would do exactly that, so this keeps the change plan's own order throughout:
                // ToLookup preserves the order of its source inside each group.
                var filed = planLines.Select(l => (Line: l, ItemId: FiledUnder(l, claimed)))
                                     .Where(x => x.ItemId != null).ToArray();
                candidates.AddRange(AssistantItems(
                    filed.Where(x => x.ItemId.StartsWith(AssistantItemPrefix, StringComparison.Ordinal))
                         .Select(x => x.Line)));
                var linesByItem = filed.ToLookup(x => x.ItemId, x => x.Line, StringComparer.Ordinal);
                var undoneApplies = await UndoneAppliesAsync(s, memory).ConfigureAwait(false);
                foreach (var item in candidates)
                    StampChangeState(item, memory, linesByItem, facts.Revision, sourceVersion, s.Id, binding.Identity, undoneApplies);
                StampJevRefusals(candidates, s.Id);
                StampJevColumnNotes(candidates, s.Id);
                StampJevProgress(candidates, s.Id);
                StampRowWorth(binding, s, candidates, linesByItem);

                // Source, doer and what the person decided about each derived row (contract 1.2 and 2.1).
                foreach (var item in candidates) StampDerived(item, memory);

                // Never waits for the service: an admitted ask runs in the background (RankAsync).
                var ranking = await RankAsync(s, facts, candidates.Where(IsOpen).ToList(), jevSettling: jev.Live && jev.Pending)
                    .ConfigureAwait(false);
                var ordered = Order(candidates, ranking.Scores);

                var now = DateTimeOffset.UtcNow;

                // An item can leave the plan for two very different reasons and only one of them is "done".
                // The other is that the EVIDENCE its family is derived from could not be read this time, so
                // the item could not be computed at all. Recording that as finished reports work nobody did
                // (F-200), so the item is kept, said plainly to be unchecked, and never completed.
                // Footprints, "Waits for" and Ready (Plan/PlanFootprint.cs, contract 5.2 and 5.3), over the rows
                // in plan order. After every commit this compose runs again, so a row whose blocker finished
                // reads Ready on the next call with nothing else asked of anybody. Stamped BEFORE the rows kept
                // below are added: a row whose evidence could not be read this time could not be computed, so
                // its footprint is unknown and it neither waits nor blocks.
                //
                // The asked rows (lane E3) take part too, after the derived rows in plan order: a task's footprint is
                // the objects it names (PlanFootprint.Asked), so a task that names what a Semanticus row changes waits
                // for that row, and the queue offers only what is Ready.
                var asks = memory.Tasks.Where(t => !PlanTasks.IsAim(t))
                    .OrderBy(t => PlanTasks.IsOpenTask(t) ? 0 : 1)
                    .ThenBy(t => t.Doer == PlanDoers.Assistant ? t.Order ?? int.MaxValue : int.MaxValue)
                    .ThenBy(t => t.CreatedUtc)
                    .Select(t => PlanTasks.ItemFor(t, binding.Nowhere, SkipFor(memory, t)))
                    .ToList();
                // A found row the person gave to someone (lane m11/plan-gaps, contract 3.2 and 4) is carried by the task
                // it became: that task is the one row on the Plan and in the queue, so the found row neither waits nor
                // blocks, and it is not shown while the task is open. It comes back if the task is set aside.
                var given = PlanTasks.GivenRows(memory.Tasks.Where(t => SkipFor(memory, t) == null), ordered);
                // The open tasks' objects as they are now: renamed, or gone (UAT S12-5-006, S12-6-002). Read here, before the footprints, because a
                // task whose objects are gone is Needs you and changes nothing, so it must not hold the rows after it (hero run 20, J2, Probe4).
                var taskGrounds = await GroundsNowAsync(s, AllGroundsOf(memory.Tasks.Where(PlanTasks.IsOpenTask))).ConfigureAwait(false);
                var taskHistory = HistoryForTasks(s);
                var goneTasks = memory.Tasks.Where(t => PlanTasks.IsOpenTask(t) && GoneOfTask(t, taskGrounds, taskHistory) is { Length: > 0 })
                                     .Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
                PlanFootprint.Stamp(ordered.Concat(asks).ToList(), await FootprintInputsAsync(s, facts).ConfigureAwait(false),
                    i => IsOpen(i) && !given.Contains(i.Id) && !goneTasks.Contains(i.Id));
                // The rows' writes are known now: a given row that lists something its task never carried is the row's work again (D1).
                given = PlanTasks.GivenRows(memory.Tasks.Where(t => SkipFor(memory, t) == null), ordered);

                var unproven = UnprovenFamilies(facts);
                var kept = unproven.Count == 0 ? new List<ModelPlanItem>() : memory.LastItems
                    .Where(seen => !ordered.Any(i => string.Equals(i.Id, seen.ItemId, StringComparison.Ordinal)))
                    .Where(seen => IsPlansOwnRow(seen.ItemId))
                    .Where(seen => !memory.Done.Any(d => string.Equals(d.ItemId, seen.ItemId, StringComparison.Ordinal)))
                    .Select(seen => (Seen: seen, Family: FamilyOf(unproven, seen.ItemId)))
                    .Where(x => x.Family.Prefix != null)
                    .Select(x => UncheckableItem(x.Seen, x.Family))
                    .ToList();
                ordered.AddRange(kept);

                // A rule row standing aside for a Jev row is not its work being done: the work moved to Jev's row, and
                // comes back to the rule's row if that row goes. The format rows stand aside for a kept Jev line that owns
                // every column they listed, the date table row for a sure Jev line, the starter rows for Jev deciding the
                // starter measures. Neither the Done register nor the History's sentence may count it as done.
                //
                // A row stands aside only WHILE THE GAP IT NAMES IS STILL THERE. Read off the flag alone, the answer
                // was wrong the moment the person closed that gap themselves: the row then leaves because the work is
                // done, whatever Jev's line is doing, and calling that replaced dropped it out of the Done register
                // and told the person a suggestion had taken over work they did (round 3, the sibling of DT4). So the
                // set is COMPUTED from the same builders BuildItems uses, with the stand-aside switched off, and the
                // two can no longer disagree about which rows the rule would have drawn.
                var aside = StoodAsideForJev(facts);
                bool StoodAside(string id) => id != null && aside.Contains(id);
                // "Save the changes you have made" is done by a save that wrote changes, and leaves the plan after an
                // Undo back to the saved model or a discard too (a restart's Discard, a re-open). Every fact it is judged
                // on was read in a dispatcher turn, never late: the save count read WITH the facts (D14), the count the
                // row was last seen at, and the count each save recorded as it marked the model clean (D15, D16).
                var heldRows = PlanTasks.RowsHeldByClaims(memory.Tasks.Where(t => SkipFor(memory, t) == null));
                var saved = SavedSinceUnsavedRowSeen(memory, facts.SavesSessionId, facts.Saves);
                var unsavedOnPlan = ordered.Any(i => string.Equals(i.Id, UnsavedItemId, StringComparison.Ordinal));
                // A row whose findings the person ALL declined is off the plan because they said leave it, not because the gap went: it is
                // dropped, never recorded as done. Worked out by building the rows again with the accepted findings counted, so it can
                // never disagree with what BuildItems left out.
                var declinedAway = new HashSet<string>(
                    BuildItems(facts, JevDecidedFinding, countDeclined: true).Select(i => i.Id), StringComparer.Ordinal);
                var edits = LiveEditsOnHistory(s);
                var ownedIds = new HashSet<string>(edits.SelectMany(e => e.Cause?.Ids ?? Array.Empty<string>()).Where(id => id != null), StringComparer.Ordinal);
                // A change that carried the row's own id closed it, so that credit stands (the row may also hold findings the person did
                // not decline). Declining writes no such change, and an unrelated edit is not a fix.
                // The findings the row held when it was last seen decide it: declined only when EVERY one of them is waived now. One that is
                // gone is work done (it keeps the credit), and a waiver already off the row before it was last seen is not its business
                // (Saved_one_key_declined_earlier_then_the_assistant_fixes_what_the_row_still_asks_counts_done). A row that remembers no
                // findings (an older file, a row built from none) falls back to the rebuild above.
                var waivedNow = new HashSet<string>(
                    (facts.Readiness?.Findings ?? Array.Empty<ReadinessFinding>()).Where(x => x.Waived).Select(x => ClaimKey(x.RuleId, x.ObjectRef)),
                    StringComparer.OrdinalIgnoreCase);
                bool DeclinedNotFixed(PlanSeenItem seen) =>
                    !ownedIds.Contains(seen.ItemId)
                    && (seen.Claims is { Length: > 0 } ? seen.Claims.All(waivedNow.Contains) : declinedAway.Contains(seen.ItemId));
                var leaving = memory.LastItems
                    .Where(seen => !ordered.Any(i => string.Equals(i.Id, seen.ItemId, StringComparison.Ordinal)))
                    .Where(seen => IsPlansOwnRow(seen.ItemId))
                    .Where(seen => !StoodAside(seen.ItemId))
                    .Where(seen => !DeclinedNotFixed(seen))
                    // A coverage or cleanup row whose findings a task took leaves the list because the task holds them, not because they are fixed:
                    // the task is the job and the one place it is done (a row stamped Done beside its own Ready task counted a job that was not done).
                    .Where(seen => !heldRows.Contains(seen.ItemId))
                    .Where(seen => saved || !string.Equals(seen.ItemId, UnsavedItemId, StringComparison.Ordinal))
                    .Where(seen => !memory.Done.Any(d => string.Equals(d.ItemId, seen.ItemId, StringComparison.Ordinal)))
                    .ToList();
                // WHO GETS THE CREDIT (UAT-R2-S10b-004). A row leaves because its gap went, and the owner on the row is only who was
                // to do it. Credit stays with the row only when a change that carried this row's id is on the edit history; an
                // assistant's row that a sibling row's change closed says so, and names that row when the history does.
                // The assistant's usual way is plain edit tools or a task, so its credit is any edit from its door since the row was last seen
                // open, or an edit that carries the row's id. A row that has no seen time (an older file) is credited on the id alone.
                bool AssistantWorked(PlanSeenItem seen) => ownedIds.Contains(seen.ItemId)
                    || (seen.SeenUtc.HasValue && edits.Any(e => string.Equals(e.Origin, "agent", StringComparison.Ordinal) && e.When >= seen.SeenUtc.Value));
                var doneNow = leaving.Select(seen => new PlanDone
                {
                    ItemId = seen.ItemId, Title = seen.Title, Section = seen.Section, When = now,
                    Doer = seen.ItemId == UnsavedItemId && saved ? memory.LastSave?.By ?? PlanDoers.You : seen.Doer,
                    ClosedWhy = seen.Doer == PlanDoers.Assistant && !AssistantWorked(seen)
                        ? ClosedByOtherSentence(leaving.Where(o => o.ItemId != seen.ItemId && ownedIds.Contains(o.ItemId)).Select(o => o.Title).FirstOrDefault())
                        : null,
                }).ToList();

                // An item can also come BACK: undo puts the gap back and the candidate returns. Its old
                // completion record must go with it, or the plan carries the same id twice, once ready and
                // once done, which inflates the progress and hands React two rows with one key (Astra 12).
                var live = new HashSet<string>(ordered.Select(i => i.Id), StringComparer.Ordinal);
                var stillDone = memory.Done.Where(d => !live.Contains(d.ItemId)).ToList();

                // What this refresh saw and will write down, which is what makes the NEXT one able to say what
                // changed. It is the rows the plan owns: an "asst/" row is on the plan only because somebody's
                // assistant put changes on the change plan, and a Jev row only because a family put lines on it, so
                // neither is the plan's to remember and neither may ever count as done (IsPlansOwnRow, above).
                // Remembering one would have the next refresh record it as finished when the changes were simply
                // dropped, permission withdrawn or Pro lapsed, which is work nobody did.
                var seen = ordered.Where(i => IsPlansOwnRow(i.Id)).ToList();
                // WHAT THE PLAN REMEMBERS IS WHAT IT MAY COMPARE ITSELF AGAINST. This sentence, and the Appeared and
                // Gone lists below, are all computed over `seen` and never over `ordered`, because `seen` is exactly
                // what the last refresh wrote down. Comparing the remembered rows against EVERY row now on the plan
                // put the two rows that are never remembered, an assistant's and Jev's, on the new list at every
                // redraw, however long they had been sitting there ("2 items done, 3 new." after Run the plan, the
                // three on the plan since consent; round 3, the sibling of PW1 to PW3). The change plan's own rows
                // are told to the person by the change plan, not by this sentence.
                var note = PlanMemory.RefreshNote(
                    memory.LastItems.Select(i => i.ItemId).ToArray(),
                    seen.Select(i => i.Id).ToArray(),
                    memory.LastItems.Select(i => i.ItemId).Where(StoodAside).ToArray(),
                    doneNow.Select(d => d.ItemId).ToArray());

                // RefreshedWhy is "what changed since the last call", so unlike History it also tells the rows the plan
                // does not remember: an assistant's or Jev's row arriving is a change on the plan (joint verifier, L1 of
                // m11/plan-waits: the call that brought Jev's rows said "Nothing on the plan changed."). Compared with
                // what the LAST CALL showed, held in this engine, never with the memory file, so a row that has been
                // there a while is not new again. One that left is off the plan, never done: it finished nothing the
                // plan can vouch for. With no record for this model yet (a first call in this engine) nothing is said
                // about them. The first draw keeps History's own sentence.
                var othersNow = new HashSet<string>(ordered.Where(i => !IsPlansOwnRow(i.Id)).Select(i => i.Id), StringComparer.Ordinal);
                var why = note;
                if (memory.LastItems.Count > 0 && _otherRowsLastCall.TryGetValue(binding.Context, out var othersBefore))
                    why = PlanMemory.RefreshNote(
                        memory.LastItems.Select(i => i.ItemId).Concat(othersBefore).ToArray(),
                        seen.Select(i => i.Id).Concat(othersNow).ToArray(),
                        memory.LastItems.Select(i => i.ItemId).Where(StoodAside).ToArray(),
                        doneNow.Select(d => d.ItemId).ToArray());
                _otherRowsLastCall.AddOrUpdate(binding.Context, othersNow);

                // A READ WRITES ONLY WHAT IT LEARNED (UAT S00, F-291). The file goes into the person's repository,
                // so a refresh that saw the same rows as last time must leave it exactly as it was: no "Nothing on
                // the plan changed." entry and no tick of a counter, so PlanMemory.Mutate finds nothing to write.
                var planMoved = doneNow.Count > 0
                    || !memory.LastItems.Select(i => i.ItemId).ToHashSet(StringComparer.Ordinal)
                              .SetEquals(seen.Select(i => i.Id));
                TryWritePlanMemory(binding, state =>
                {
                    // Where the unsaved row was last seen, as the facts were read. Cleared once it is off the plan, so
                    // the file carries it only while the row is there to be answered.
                    state.UnsavedSeen = unsavedOnPlan && facts.SavesSessionId != null
                        ? new PlanSaveMark { Session = facts.SavesSessionId, Saves = facts.Saves }
                        : null;
                    state.Done.RemoveAll(d => live.Contains(d.ItemId));   // a gap that came back is not done
                    // A cleared row that opened again is not cleared any more: finishing it again shows it again (Refresh plan).
                    state.ClearedDone.RemoveAll(id => live.Contains(id) || asks.Any(a => a.Id == id && a.Status != ModelPlanStatuses.Done));
                    state.Done.AddRange(doneNow);
                    // The seen time moves only when the plan did, so a read that learned nothing still writes nothing.
                    var before = memory.LastItems.ToDictionary(x => x.ItemId, x => x.SeenUtc, StringComparer.Ordinal);
                    state.LastItems = seen
                        .Select(i => new PlanSeenItem
                        {
                            ItemId = i.Id, Title = i.Title, Section = i.Section, Doer = i.Doer,
                            SeenUtc = !planMoved && before.TryGetValue(i.Id, out var was) && was.HasValue ? was : now,
                            Claims = i.Claims ?? Array.Empty<string>(),
                        })
                        .ToList();
                    // Only against the last DRAW, not the last entry of any kind: a start or a skip sits
                    // between two refreshes now, and comparing against those would write "Nothing on the
                    // plan changed." again every time somebody pressed a button.
                    var lastDraw = state.History.LastOrDefault(h =>
                        string.Equals(h.Kind, ModelPlanHistoryKinds.Drawn, StringComparison.Ordinal)
                        || string.Equals(h.Kind, ModelPlanHistoryKinds.Redrawn, StringComparison.Ordinal));
                    // A person's decision about a derived row outlives a refresh or two where the row is missing
                    // (a revision that briefly hides it), and goes once the row has been gone for a while. "A while"
                    // is counted in refreshes where the plan MOVED: a row already missing last time and still
                    // missing now has not been gone any longer in a sense worth a count, and counting every read
                    // made every read a write.
                    foreach (var key in state.Residue.Keys.ToList())
                    {
                        var residue = state.Residue[key];
                        if (live.Contains(key)) residue.Absent = 0;
                        else if (planMoved) residue.Absent++;
                        if (residue.Absent >= PlanMemory.ResidueAbsentLimit) state.Residue.Remove(key);
                    }

                    // An entry only when something happened: the very first draw, or a plan that moved. A refresh
                    // that saw what the last one saw still says so on the page (RefreshedWhy) and writes nothing.
                    if (lastDraw == null
                        || (planMoved && !string.Equals(lastDraw.Sentence, note, StringComparison.Ordinal)))
                        state.History.Add(new ModelPlanHistoryEntry
                        {
                            When = now,
                            // The very first composition for a model is a draw; every one after it is a
                            // redraw, and the page chips them differently.
                            Kind = memory.LastItems.Count == 0
                                ? ModelPlanHistoryKinds.Drawn
                                : ModelPlanHistoryKinds.Redrawn,
                            Actor = ModelPlanActors.Engine,
                            Revision = facts.Revision,
                            Sentence = note,
                            Text = note,
                            Appeared = Titles(seen
                                .Where(i => !memory.LastItems.Any(s => string.Equals(s.ItemId, i.Id, StringComparison.Ordinal)))
                                .Select(i => i.Title)),
                            // Gone is everything that left; Completed is the part of it the plan recorded as
                            // finished. They overlap on purpose and are kept apart so the page can say which.
                            Gone = Titles(memory.LastItems
                                .Where(s => !seen.Any(i => string.Equals(i.Id, s.ItemId, StringComparison.Ordinal)))
                                .Select(s => s.Title)),
                            Completed = Titles(doneNow.Select(d => d.Title)),
                        });
                });

                var withDone = ordered
                    .Concat(stillDone.Concat(doneNow).Select(DoneItem))
                    .ToArray();

                var next = ordered.FirstOrDefault(i => IsOpen(i) && !given.Contains(i.Id));

                // The ONE call this file makes into the target half (PlanTargets.cs). It adds the rule aims
                // and the person's typed targets to the view, turns each typed target into one item in its
                // own section, and writes the worth's target line. Everything it needs is already here: the
                // typed targets are the second thing the memory remembers, beside the skips.
                var view = PlanTargets.Fold(new ModelPlanView
                {
                    ModelName = facts.ModelName,
                    // The revision the FACTS were read at, which is the model this plan actually describes. It
                    // is the session's revision on every ordinary call; it is stamped from the facts so that a
                    // plan can never claim to describe a model it did not read.
                    Revision = facts.Revision,
                    RefreshedAt = now,
                    RefreshedWhy = why,
                    Worth = Worth(facts, candidates),
                    Next = next == null ? null : new ModelPlanNext { ItemId = next.Id, Line = next.Title },
                    Items = withDone,
                    History = PlanMemory.Read(binding.File, binding.Identity).History.ToArray(),
                    Tables = facts.Tables,
                    OrderedBy = ranking.OrderedBy,
                    BillingUrl = ranking.BillingUrl,
                    Ranked = ranking.Ranked,
                    // Jev is looking while either background ask is out: the families or the value order.
                    JevPending = jev.Pending || ranking.Pending,
                    Note = kept.Count == 0 ? facts.Note : Join2(facts.Note,
                        CountOf(kept.Count, "task on the Plan", "tasks on the Plan")
                        + " could not be checked this time, so Semanticus cannot tell if "
                        + (kept.Count == 1 ? "it is" : "they are") + " finished. "
                        + (kept.Count == 1 ? "It stays" : "They stay") + " on the list, marked as not checked."),
                }, memory.Tasks.Where(PlanTasks.IsAim).ToList(), binding.Nowhere, memory.Skips);
                view.Starts = StartsFor(facts.Tables_);

                // The asked rows that are not aims (built above, before the footprints): open ones after the derived
                // work, in the assistant's order then the order they were asked, and closed ones at the tail with
                // the other finished rows.
                // The composer's drafts and the running guided workflows (lane L4, contract 1.3 and 14): one row each,
                // "draft/<id>" and "run/<wfr id>", after the asked rows. Each carries its own state word, read off the
                // draft or the run; a draft's parts waiting for the assistant are tasks above, each linked back to it.
                var drafts = DraftPlanRows(binding, memory).Concat(RunPlanRows(binding)).ToList();
                // ONE LIST (fix-coverage3): what the tasks hide is worked out once, here, and the rows, the tasks and the strip below all follow it.
                // The checks come first because a task that holds findings is "checking" only while its findings stand.
                var checks = await TaskChecksAsync(s, memory.Tasks, facts, ordered, binding.Context?.Plans.Current).ConfigureAwait(false);
                var hidden = PlanTasks.HiddenBy(memory.Tasks.Where(t => SkipFor(memory, t) == null), ordered, checks);
                var rows = view.Items.Where(i => !given.Contains(i.Id) && !(i.Status == ModelPlanStatuses.Done && hidden.DoneEntries.Contains(i.Id))).ToList();
                var firstDone = rows.FindIndex(i => i.Status == ModelPlanStatuses.Done);
                asks = asks.Where(i => !hidden.Tasks.Contains(i.Id)).ToList();
                var openAsks = asks.Where(i => i.Status != ModelPlanStatuses.Done)
                                   .Concat(drafts.Where(i => i.Status != ModelPlanStatuses.Done)).ToList();
                if (firstDone < 0) rows.AddRange(openAsks); else rows.InsertRange(firstDone, openAsks);
                rows.AddRange(asks.Where(i => i.Status == ModelPlanStatuses.Done));
                rows.AddRange(drafts.Where(i => i.Status == ModelPlanStatuses.Done));
                // Refresh plan took these finished rows off the list. Their record stays in Done, the tasks and History.
                view.Items = ClearedOut(rows, memory.ClearedDone).ToArray();
                var vanishedApplies = VanishedApplies(s, memory, view.Items.Select(i => i.Id).ToHashSet(StringComparer.Ordinal), undoneApplies);

                // LAST: the state word on every row, once every fact it reads is on the row. Anything that fills
                // WaitsFor (lane E2) runs before this line.
                // A rename seen here is kept, so the object is named by its name now from here on. Best effort, like the
                // compose's other writes; the rows below follow the rename either way.
                var renamed = taskGrounds.Concat(repeatGrounds).Where(p => !p.Value.Gone && p.Value.Current != p.Key)
                    .GroupBy(p => p.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().Value, StringComparer.Ordinal);
                if (renamed.Count > 0) WriteRepeatsFromCompose(binding, state => FollowRenamesIn(state, renamed));
                // Rows added after BuildItems (Jev, assistant lines, the words row) get their done-when and default here; a task is the person's own words and gets none.
                AssistantDefaults.Apply(view.Items.Where(i => !PlanTasks.IsTaskId(i.Id)));
                var away = new Lazy<bool>(() => !AnyAssistantConnected());   // read only when a waiting assistant row asks
                Func<bool> assistantAway = () => away.Value;
                foreach (var item in view.Items)
                {
                    if (Composer.DraftRows.IsDraftRow(item.Id) || IsRunRow(item.Id)) continue;   // stamped from the draft or run itself
                    var task = PlanTasks.IsTaskId(item.Id) ? memory.FindTask(item.Id) : null;
                    if (task == null && item.Source == null) StampDerived(item, memory);   // done and unchecked rows
                    if (task != null) PlanTasks.FollowRenames(item, task, taskGrounds);
                    if (task != null) RetitleCoverageTask(item, task, facts);
                    PlanTasks.StampState(item, task, task != null && checks.Contains(task.Id), memory.AssistantQueue,
                        task == null ? null : GoneOfTask(task, taskGrounds, taskHistory), assistantAway);
                }
                view.AssistantQueue = memory.AssistantQueue;
                StampEvents(s, view.Items);
                MarkRowsTheAssistantIsWorking(s, view.Items, memory.AssistantQueue);
                // The repeats, read off the rows as stamped: fix repeats settle and mark their row (lane RP).
                FoldRepeats(binding, view, memory, repeatGrounds);

                // What the assistant reads on its next call, and the number each offered row has in it (contract 6.1),
                // read off the rows as stamped, so the Plan page shows the engine's own words rather than a port.
                var offer = Jev.PlanOffer.From(view);
                view.AssistantSees = offer.Switch == PlanAssistantQueue.Paused
                    ? Jev.PlanTaskQueue.PausedLine(offer.Claimed.Select(t => t.Title))
                    : Jev.PlanTaskQueue.OfferLine(offer);
                var briefs = view.Items.Where(i => !string.IsNullOrEmpty(i.Brief)).ToDictionary(i => i.Id, i => i.Brief, StringComparer.Ordinal);
                view.AssistantBriefs = briefs.Count > 0 ? briefs : null;
                var numbers = offer.Numbers();
                foreach (var item in view.Items)
                    item.OfferNumber = item.Id != null && numbers.TryGetValue(item.Id, out var number) ? number : (int?)null;

                // Run the plan's fence and the strip (lane E5, contract 7.2 and 9), read off the rows as stamped.
                view.ReadyDigest = PlanReadySet.Digest(view.Items, view.Revision);
                view.Strip = PlanStripBuilder.Build(view.Items, view.Worth, new PlanStripInputs
                {
                    DoneWhen = DoneTimes(view.Items, stillDone.Concat(doneNow), memory.Tasks),
                    // A done id whose row is not listed (hidden behind its task) is not counted: the task is the one Done entry.
                    DoneIds = stillDone.Concat(doneNow).Select(d => d.ItemId).Concat(memory.ClearedDone).Concat(vanishedApplies)
                        .Where(id => !hidden.DoneEntries.Contains(id) && !given.Contains(id)),
                    Latest = LatestLedgerEntryForStrip(s),
                    // Jev ran only when this run heard back: a live point whose every ask failed did not update anything.
                    // JEV UPDATED THE PLAN, OR IT DID NOT. Answers alone are not an update: a run can hear back
                    // about every object and agree with all of them, which changes no line and leaves the plan
                    // exactly as the rules left it (verify round 4). The ranking is an update of its own, because
                    // it is what put the rows in the order the person is reading.
                    JevRan = (jev.Live && !jev.Pending && jev.Answered > 0 && jev.Lines > 0) || ranking.Ranked,
                    Now = now,
                    Bpa = await BpaForChecksAsync(s).ConfigureAwait(false),
                });
                view.StartPlan = await StartPlanPointerAsync().ConfigureAwait(false);   // M11: where a start plan is, if there is one
                NoteNextStepPlan(view, s?.Id);   // 1.5: the Next line reads the Plan's ready and done counts off every compose
                return view;
            }
            finally { _modelPlanGate.Release(); }
        }

        /// <summary>How long after its latest edit an assistant still counts as working on a row (its gaps between calls run long; the header uses the same short window, ASSISTANT_ACTIVE_MS; UAT 18 Oct N21: a minute left On it standing after Claude had finished; 45 s since a real drive paused 35 s between calls).</summary>
        internal static readonly TimeSpan AssistantAtWorkWindow = TimeSpan.FromSeconds(45);

        /// <summary>A row the assistant's own tool calls touched a moment ago is flagged for the page to draw as On it, in the assistant's colour, and not "Waiting, ask your assistant"
        /// (hero20 D3: it wrote 213 descriptions under a row that said it waited). Read off the edit history, so a direct tool counts as much as a claimed task.
        /// An open assistant row only: a row that is Done, set aside, paused or the person's own keeps its word.</summary>
        private void MarkRowsTheAssistantIsWorking(Session s, IEnumerable<ModelPlanItem> items, string queue)
        {
            if (string.Equals(queue, PlanAssistantQueue.Paused, StringComparison.Ordinal)) return;
            var since = DateTimeOffset.UtcNow - AssistantAtWorkWindow;
            var refs = LiveEditsOnHistory(s).Where(e => string.Equals(e.Origin, "agent", StringComparison.Ordinal) && e.When >= since)
                .SelectMany(e => e.Deltas ?? Array.Empty<ChangeDelta>()).Select(d => d?.Ref).Where(r => !string.IsNullOrEmpty(r))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (refs.Length == 0) return;
            bool Touches(string obj)
            {
                if (string.IsNullOrEmpty(obj)) return false;
                if (refs.Contains(obj, StringComparer.OrdinalIgnoreCase)) return true;
                // A table's row is worked by its columns and measures.
                var colon = obj.IndexOf(':');
                return colon > 0 && obj.Substring(0, colon) == "table" && refs.Any(r => r.Contains(":" + obj.Substring(colon + 1) + "/", StringComparison.OrdinalIgnoreCase));
            }
            foreach (var i in items)
            {
                if (i == null || i.Doer != PlanDoers.Assistant) continue;
                if (i.StateWord != PlanStateWords.Ready && i.StateWord != PlanStateWords.Waiting) continue;
                var objects = (i.Objects ?? Array.Empty<string>())
                    .Concat((i.Claims ?? Array.Empty<string>()).Select(c => c.Substring(c.IndexOf('|') + 1)));
                if (!objects.Any(Touches)) continue;
                i.AssistantAtWork = true;
                // The page draws On it beside the note. A note that says what the row waits for (Jev, "step x of y", another task) stays; only the
                // lines that tell the person to ask the assistant are dropped, because the assistant is already at it.
                if (AsksTheAssistant(i.StateNote)) i.StateNote = null;
            }
        }

        private static bool AsksTheAssistant(string note) =>
            !string.IsNullOrWhiteSpace(note) && (note == PlanTasks.WaitingForAssistantNote || note == PlanTasks.AskAssistantNote || note == PlanTasks.PausedByYouNote
                || note.StartsWith("Waiting for your assistant", StringComparison.Ordinal) || note.StartsWith("Ask your assistant", StringComparison.Ordinal));

        /// <summary>A person's skip on a task, found by the task's id or by any old id it answers to, so a skip
        /// recorded against a migrated target's old id still sets the aim aside.</summary>
        private static PlanSkip SkipFor(PlanMemoryState memory, PlanTask task) =>
            memory.Find(task.Id) ?? (task.Aliases ?? Array.Empty<string>()).Select(memory.Find).FirstOrDefault(x => x != null);

        /// <summary>Source, doer, residue and done-when on a derived row. The doer by kind comes from the old
        /// owner field; a doer the person chose instead is residue and wins.</summary>
        private static void StampDerived(ModelPlanItem item, PlanMemoryState memory)
        {
            // A family that knows better says so when it builds the row (a Jev row whose every line is the rule's is
            // "found", JevFamily.Items); otherwise the id says where the row came from.
            item.Source ??= PlanTasks.SourceOf(item.Id, AssistantItemPrefix);
            item.Doer = PlanTasks.DoerOf(item.Who);
            // What the row is for, when its builder did not say (a builder that read findings did): lines an assistant put on the change plan
            // are the person's words passed through, a Jev row decides what AI readiness would otherwise flag, and the rest (the cleanup, a
            // check, saving) are quality.
            item.Rules ??= Array.Empty<string>();
            item.Goal ??= item.Id != null && item.Id.StartsWith(AssistantItemPrefix, StringComparison.Ordinal) ? PlanGoals.Aims
                        : JevFamily.IsJevItem(item.Id) ? PlanGoals.Ai
                        : PlanGoals.Quality;
            // The filters the row shows under: its goal, and "quality" too when its rule is one Model quality raises as well.
            item.Goals = item.Goals != null && item.Goals.Length > 0 && item.Goals[0] == item.Goal ? item.Goals : new[] { item.Goal };
            item.DoneWhen = PlanDoneWhen.ModelCheck;
            item.DoneText = PlanTasks.DoneTextOf(PlanDoneWhen.ModelCheck, 0);
            // A derived row names what its change set changes, so the row's Objects are the objects its lines
            // target, in line order. A row with no lines worked out yet names nothing, which is true.
            if ((item.Objects == null || item.Objects.Length == 0) && item.ChangeSet?.Lines?.Length > 0)
                item.Objects = item.ChangeSet.Lines.Select(l => l.Target).Where(t => !string.IsNullOrWhiteSpace(t))
                                   .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (item.Id != null && memory.Residue.TryGetValue(item.Id, out var residue))
            {
                if (residue.Doer != null) item.Doer = residue.Doer;
                item.Held = residue.Held;
                item.Note = residue.Note;
                item.AssistantNote = residue.AssistantNote;
            }
        }

        /// <summary>The ids of the tasks whose done-when check passes on this model right now (contract 1.5):
        /// a finding a Send linked is gone from the scan, or every named object has words. The engine reads the
        /// properties; nothing is inferred.</summary>
        private async Task<HashSet<string>> TaskChecksAsync(Session s, List<PlanTask> tasks, PlanFacts facts,
            IReadOnlyList<ModelPlanItem> derived, ChangePlanState changes)
        {
            var passed = new HashSet<string>(StringComparer.Ordinal);
            // A finding sent from Model quality is checked against the Model quality scan, not the readiness scan it
            // is not in (contract 1.5). Scanned only when such a task is open, once per revision.
            var bpa = tasks.Any(t => t.DoneWhen == PlanDoneWhen.ModelCheck && PlanTasks.IsOpenTask(t) && IsBpaFinding(t))
                ? await BpaForChecksAsync(s).ConfigureAwait(false) : null;
            foreach (var t in tasks.Where(t => t.DoneWhen == PlanDoneWhen.ModelCheck))
            {
                var link = t.OriginLink;
                // A row that listed findings or objects (a coverage row, the cleanup) hands over THOSE: the task is done when they are gone, and
                // whatever appears later is on a row of its own (D1). A removal is done when the objects are gone, not refused for it (D2).
                if (link?.Kind == PlanOriginKinds.Item && t.Claims is { Length: > 0 })
                {
                    if (!t.Claims.Any(c => ClaimStands(facts, c))) passed.Add(t.Id);
                    continue;
                }
                // A found row the person gave away: done when the gap it named is gone, which is when the compose no
                // longer derives that row as open work.
                if (link?.Kind == PlanOriginKinds.Item && !string.IsNullOrWhiteSpace(link.Ref))
                {
                    if (!derived.Any(i => IsOpen(i) && string.Equals(i.Id, link.Ref, StringComparison.Ordinal))) passed.Add(t.Id);
                    continue;
                }
                if (link?.Kind != PlanOriginKinds.Finding) continue;
                var (rule, obj) = PlanTasks.SplitFindingRef(link.Ref);
                if (rule == null) continue;
                if (IsBpaFinding(t))
                {
                    // A rule the scan could not evaluate on its scope is unknown, never gone.
                    if (bpa == null || bpa.Unknowns.Any(u => string.Equals(u.RuleId, rule, StringComparison.Ordinal))) continue;
                    var still = bpa.Violations.Any(v => !v.Waived
                        && string.Equals(v.RuleId, rule, StringComparison.Ordinal)
                        && (obj == null || string.Equals(v.ObjectRef, obj, StringComparison.Ordinal)));
                    if (!still) passed.Add(t.Id);
                    continue;
                }
                if (facts.Readiness?.Findings == null) continue;
                var open = facts.Readiness.Findings.Any(f => !f.Waived
                    && string.Equals(f.RuleId, rule, StringComparison.Ordinal)
                    && (obj == null || string.Equals(f.ObjectRef, obj, StringComparison.Ordinal)));
                if (!open) passed.Add(t.Id);
            }

            // has_words (contract 1.5): the named objects, doc section or change-plan slot have a value. A task with a
            // slot is checked on its slot only: an object's Description is not its doc section.
            var words = tasks.Where(t => t.DoneWhen == PlanDoneWhen.HasWords && ((t.Objects?.Length ?? 0) > 0 || t.Slot != null)).ToList();
            if (words.Count > 0)
            {
                var have = await s.ReadAsync(m => words
                    .Where(t => t.Slot == null
                        ? t.Objects.All(r => ObjectRefs.Resolve(m, r) is TabularEditor.TOMWrapper.IDescriptionObject d
                                             && !string.IsNullOrWhiteSpace(d.Description))
                        : SlotHasWords(m, t.Slot, changes))
                    .Select(t => t.Id)
                    .ToList()).ConfigureAwait(false);
                passed.UnionWith(have);
            }
            return passed;
        }

        private static bool IsBpaFinding(PlanTask t) =>
            t.OriginLink?.Kind == PlanOriginKinds.Finding && string.Equals(t.SourcePage, "bpa", StringComparison.Ordinal);

        private (string Session, long Revision, Semanticus.Analysis.BpaScorecard Card) _bpaForChecks;

        private async Task<Semanticus.Analysis.BpaScorecard> BpaForChecksAsync(Session s)
        {
            var cached = _bpaForChecks;
            if (cached.Card != null && cached.Session == s.Id && cached.Revision == s.Revision) return cached.Card;
            try
            {
                var revision = s.Revision;
                var card = await s.ReadAsync(m => BpaAnalyzer.Analyze(m, GetBpaRules(m))).ConfigureAwait(false);
                _bpaForChecks = (s.Id, revision, card);
                return card;
            }
            catch { return null; }   // a scan that cannot run proves nothing: the task stays open
        }

        /// <summary>A doc section ("doc:section|object") or a change-plan slot ("change:item") has words now.</summary>
        private static bool SlotHasWords(TabularEditor.TOMWrapper.Model m, string slot, ChangePlanState changes)
        {
            var (kind, section, target) = PlanTasks.SplitSlot(slot);
            if (kind == PlanTasks.DocSlot)
            {
                var ao = ResolveDocTarget(m, target);
                return ao != null && !string.IsNullOrWhiteSpace(DocStr((ReadDocEnvelope(ao)["sections"] as System.Text.Json.Nodes.JsonObject)?[section]));
            }
            if (kind == PlanTasks.ChangeSlot)
            {
                var item = changes?.Find(target);
                return item != null && (!string.IsNullOrWhiteSpace(item.After) || string.Equals(item.Status, "applied", StringComparison.Ordinal));
            }
            return false;
        }

        /// <summary>Item families that are derived from evidence OUTSIDE the model, with where a person goes
        /// to look at that evidence. An item in one of these vanishes when its evidence cannot be read, which
        /// looks exactly like the gap being closed and is not the same thing at all.</summary>
        private static readonly (string Prefix, string Tool, string Label, string Who)[] EvidenceBackedFamilies =
        {
            ("check/", "tests", "Validation", ModelPlanOwners.Assistant),
        };

        /// <summary>Which of those families cannot be proven on THIS call. A check item needs both the report
        /// reading (which measures the reports use) and the saved checks (which of them are covered); either
        /// one missing and the family cannot be computed, which is why CheckItems yields nothing.</summary>
        private static IReadOnlyList<(string Prefix, string Tool, string Label, string Who)> UnprovenFamilies(PlanFacts f) =>
            f.MeasuresInReports == null || f.CheckedMeasures == null
                ? EvidenceBackedFamilies
                : Array.Empty<(string Prefix, string Tool, string Label, string Who)>();

        /// <summary>Whether a row is the PLAN'S OWN: the rows it writes down, compares itself against next time, and
        /// may record as finished. Nearly every row is on the plan because the model has a gap, so the row leaving
        /// means the gap went, which is what done means. Two kinds are not the plan's: an "asst/" row is there only
        /// because somebody's assistant put changes on the change plan, and a Jev row only because a family put lines
        /// on it. Drop the changes, withdraw permission or let Pro lapse and either row goes having finished nothing,
        /// so recording one as done would credit the person with work nobody did. Asked at BOTH ends, of what a
        /// refresh writes down and of what the memory file hands back, so the two can never disagree about which rows
        /// the plan owns.</summary>
        private static bool IsPlansOwnRow(string id) =>
            (id == null || !id.StartsWith(AssistantItemPrefix, StringComparison.Ordinal)) && !JevFamily.IsJevItem(id);

        /// <summary>The ids of the RULE rows a Jev row is standing in for on this plan, computed from the same
        /// builders <see cref="BuildItems"/> uses with the stand-aside switched off. A row missing from here did not
        /// stand aside: its gap is gone, so it left because the work was done. Asking the flag instead credited a
        /// suggestion with a date table the person marked themselves (round 3).</summary>
        private static HashSet<string> StoodAsideRuleRows(PlanFacts f)
        {
            var aside = new HashSet<string>(StringComparer.Ordinal);
            if (f == null) return aside;
            if (f.DateTableByJev)
            {
                var findings = (f.Readiness?.Findings ?? Array.Empty<ReadinessFinding>()).Where(x => !x.Waived).ToArray();
                // With the DATE-MARK finding left in, DateTableItem draws the date table row when the gap is open and
                // the calendar row (or nothing) when it is not, so only the first id means a row stood aside.
                var date = DateTableItem(findings, f.DateMark, f.HasDateColumn);
                if (string.Equals(date?.Id, "datetable/model", StringComparison.Ordinal)) aside.Add(date.Id);
            }
            if (f.StarterByJev)
                foreach (var item in StarterRuleItems(f)) aside.Add(item.Id);
            return aside;
        }

        private static (string Prefix, string Tool, string Label, string Who) FamilyOf(
            IReadOnlyList<(string Prefix, string Tool, string Label, string Who)> families, string id)
        {
            foreach (var family in families)
                if (id != null && id.StartsWith(family.Prefix, StringComparison.Ordinal)) return family;
            return (null, null, null, null);
        }

        /// <summary>An item the plan carried last time and cannot look at this time. It is NOT done and it is
        /// NOT gone: it is still work as far as anybody knows, and the row says why nobody knows.</summary>
        private static ModelPlanItem UncheckableItem(PlanSeenItem seen, (string Prefix, string Tool, string Label, string Who) family) =>
            new ModelPlanItem
            {
                Id = seen.ItemId,
                Section = string.IsNullOrWhiteSpace(seen.Section) ? ModelPlanSections.Trust : seen.Section,
                Title = seen.Title,
                Sub = "could not be checked this time",
                Status = ModelPlanStatuses.Proposed,
                // The family's doer, never the person's by default: the only family kept this way is the checks, which the
                // assistant writes with save_test (CheckItems). Falling to "you" put every check row in the person's group
                // after a restart lost the report reading (Kane, 13 Oct 2026, on the 1.4.101 test build).
                Who = family.Who ?? ModelPlanOwners.Assistant,
                Value = string.Empty,
                Why = new[]
                {
                    "This task was on the Plan last time.",
                    "Semanticus could not look at what it is about this time, so it cannot tell if it is finished.",
                    "Your assistant can read the reports again and then do it.",
                },
                Diff = Array.Empty<string>(),
                Jump = new ModelPlanJump { Tool = family.Tool, Label = family.Label },
                // Still work as far as anybody knows, so it is a gap and not a finished thing. A set worked
                // out for it earlier is deliberately NOT attached: the evidence it was built from could not
                // be read this time, so nothing here can say the set still describes anything.
                State = ModelPlanItemStates.Gap,
            };

        /// <summary>Two honesty lines as one, either of which may be absent.</summary>
        private static string Join2(string a, string b) =>
            string.IsNullOrWhiteSpace(a) ? b : string.IsNullOrWhiteSpace(b) ? a : a + " " + b;

        /// <summary>When each finished or applied row got there: a done record's time, a task's DoneUtc, and an
        /// applied set's AppliedAt. The done records and the tasks live in the Plan's own file, so a row finished in
        /// an earlier session today is counted too (contract 9).</summary>
        private static Dictionary<string, DateTimeOffset> DoneTimes(
            IEnumerable<ModelPlanItem> items, IEnumerable<PlanDone> done, IEnumerable<PlanTask> tasks)
        {
            var when = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
            foreach (var d in done ?? Array.Empty<PlanDone>())
                if (!string.IsNullOrWhiteSpace(d?.ItemId)) when[d.ItemId] = d.When;
            foreach (var t in tasks ?? Array.Empty<PlanTask>())
                if (!string.IsNullOrWhiteSpace(t?.Id) && t.DoneUtc.HasValue) when[t.Id] = t.DoneUtc.Value;
            foreach (var i in items ?? Array.Empty<ModelPlanItem>())
                if (i?.Id != null && !when.ContainsKey(i.Id) && i.ChangeSet?.AppliedAt is DateTimeOffset applied)
                    when[i.Id] = applied;
            return when;
        }

        /// <summary>The newest edit ledger entry for the strip's "Latest" mark (contract 9): the newest edit still
        /// on the undo stack, or null when the model has none.</summary>
        private PlanStripLatest LatestLedgerEntryForStrip(Session s)
        {
            var e = s == null ? null : _sessions.Ledger.ForModel(s.Id)
                .LastOrDefault(x => x.Kind == EditLedgerKinds.Edit && !x.Undone && !x.Gone);
            return e == null ? null : new PlanStripLatest { EntryId = e.EntryId, When = e.When, By = e.By, Sentence = e.Label };
        }

        /// <summary>What a finished row was for, read off its id (the done record keeps no rule ids). The readiness families are AI
        /// readiness; the best-practice fix, the cleanup, checks, re-reads and saving are quality; a task is the person's aim.</summary>
        private static string GoalOfDoneId(string id)
        {
            if (string.IsNullOrEmpty(id)) return PlanGoals.Quality;
            if (PlanTasks.IsTaskId(id) || id.StartsWith(AssistantItemPrefix, StringComparison.Ordinal)) return PlanGoals.Aims;
            if (string.Equals(id, CoverageBpaItemId, StringComparison.Ordinal)) return PlanGoals.Quality;
            foreach (var prefix in new[] { "safefix/", "words/", "describe/", "proposal/", "datetable/", "calendar/", "starter/" })
                if (id.StartsWith(prefix, StringComparison.Ordinal)) return PlanGoals.Ai;
            return JevFamily.IsJevItem(id) ? PlanGoals.Ai : PlanGoals.Quality;
        }

        /// <summary>The word on a finished row whose gap closed by somebody else's change.</summary>
        internal const string NoLongerNeededNote = "No longer needed";

        /// <summary>The edits on this model's history that still stand: not undone, not cleared from redo.</summary>
        private List<EditLedgerEntry> LiveEditsOnHistory(Session s)
        {
            try
            {
                return _sessions.Ledger.ForModel(s.Id)
                    .Where(e => e != null && !e.Undone && !e.Gone && string.Equals(e.Kind, EditLedgerKinds.Edit, StringComparison.Ordinal))
                    .ToList();
            }
            catch { return new List<EditLedgerEntry>(); }   // History that cannot be read credits nothing: the row says plainly that something else closed it
        }

        private static string ClosedByOtherSentence(string closer) =>
            string.IsNullOrWhiteSpace(closer) ? "Another change closed this." : closer.Trim().TrimEnd('.') + " closed this.";

        private static ModelPlanItem DoneItem(PlanDone d) => new ModelPlanItem
        {
            Id = d.ItemId,
            Goal = GoalOfDoneId(d.ItemId),
            Rules = d.ItemId != null && d.ItemId.StartsWith("starter/", StringComparison.Ordinal) ? new[] { "DAC-IMPLICIT-MEASURE" } : Array.Empty<string>(),
            Section = string.IsNullOrWhiteSpace(d.Section) ? ModelPlanSections.Structure : d.Section,
            Title = d.Title,
            Sub = "done " + Friendly(d.When),
            Status = ModelPlanStatuses.Done,
            // The doer it had while it was open, so a finished row stays on its own track (lane E5). A record
            // written before the doer was kept reads as Semanticus's, as every done row did before.
            Who = d.Doer == null ? ModelPlanOwners.Auto : PlanTasks.WhoOf(d.Doer),
            Value = string.Empty,
            Why = d.ClosedWhy == null
                ? new[] { "This task was on the Plan, and the problem it named is no longer in the model." }
                : new[] { "This task was on the Plan, and the problem it named is no longer in the model.", d.ClosedWhy },
            StateNote = d.ClosedWhy == null ? null : NoLongerNeededNote,
            // Done is the GAP being gone, which is what this row IS. It is not "applied": nothing here knows
            // whether a change set closed the gap or somebody did it by hand, and claiming the first would
            // credit work that may never have happened (Plan/ModelPlanChanges.cs).
            State = ModelPlanItemStates.Done,
        };

        /// <summary>Where this model's plan memory lives: the model's one place (LocalEngine.ModelHome.cs). Never the window's start folder, which
        /// lost it for good (UAT F17: Who does it > Me came back after File > Open Folder); with no folder it waits in the home and moves into
        /// the folder the model is given.</summary>
        private string PlanSidecarDir(SessionContext context) => PlaceFor(context)?.Dir;

        private string PlanMemoryFile() => PlanMemory.FileIn(PlanSidecarDir(_sessions.CurrentContext));

        /// <summary>Write something into the open model's plan memory. A test seam (neither door reaches it),
        /// so a test can say "this item was on the plan last time" without having to manufacture the evidence
        /// that put it there. LastItems is the engine's only record of that fact, so seeding it is the real
        /// state, not a stand-in for one.</summary>
        /// <summary>Awaited inside <see cref="ComposeAsync"/> at the point a real caller is waiting on the
        /// change-plan snapshot and the plan gate, so a test can exchange the open model exactly there.</summary>
        internal Func<Task> PlanComposePauseForTest;

        /// <summary>Awaited inside <see cref="ComposeAsync"/> just after the facts are read, so a test can land a save
        /// (or any commit) between reading the model and finishing the plan. Null on every real path.</summary>
        internal Func<Task> PlanComposeFactsReadForTest;

        internal bool SeedPlanMemoryForTest(Action<PlanMemoryState> change) =>
            PlanMemory.Mutate(PlanMemoryFile(), OpenModelIdentity(), change);

        // ---- the facts -------------------------------------------------------------------------------

        /// <summary>Everything one plan is built from, read once. Each field is nullable where the fact may not
        /// be knowable on this session, and an unknown is carried as an unknown rather than as a zero.</summary>
        private sealed class PlanFacts
        {
            public string ModelName;
            public SessionInfo Session;

            /// <summary>The session <see cref="Session"/> describes and how many saves that wrote edits it had made, read in the same
            /// dispatcher turn as its HasUnsavedChanges. "Save the changes you have made" is judged against these and
            /// never against the session's live count, which a save landing while the plan is composed has already
            /// moved (D14).</summary>
            public string SavesSessionId;
            public int Saves;
            /// <summary>Whether the person's own changes are unsaved, read in the same turn: Session.HasUnsavedEdits, which
            /// leaves out Semanticus's consent marker. "Save the changes you have made" is offered only for these (verifier L2
            /// of m11/m4-relationships: after a failed save and an Undo only the marker was unsaved, and the row still asked).</summary>
            public bool UnsavedEdits;
            public Scorecard Readiness;
            /// <summary>The unused sweep as the cleanup ROW shows it (<see cref="PlanUnusedAsync"/>): the columns that start ticked,
            /// and what the sweep did not call safe. The unticked ones are counted in <see cref="CleanupUnticked"/>, not hidden.</summary>
            public UnusedResult Unused;

            /// <summary>The best-practice row's lines on the model as read, and how many fixes it left out
            /// (Plan/LocalEngine.PlanBestPractice.cs). Null when the Model quality scan could not run.</summary>
            public List<ChangeItem> BpaLines;
            public int BpaLeftOut;
            /// <summary>The scored best-practice findings, each marked claimed or not (Plan/LocalEngine.PlanCoverage.cs). Empty when the scan could not run.</summary>
            public List<BpaCoverFinding> BpaCover = new List<BpaCoverFinding>();
            /// <summary>True when the coverage read of the Model quality scan ran. A task is done by "its findings are gone" only when this is true.</summary>
            public bool BpaCoverKnown;
            /// <summary>The claims (finding keys, object refs) that open tasks handed over from a coverage or cleanup row carry. A held claim is the task's
            /// work, so the row leaves it out; anything that is not held is the row's.</summary>
            public HashSet<string> HeldClaims = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            /// <summary>What tasks the person set aside still hold: the cleanup row leaves these columns out (UAT 18 Oct N22).</summary>
            public HashSet<string> SetAsideClaims = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            /// <summary>The model's compatibility level, 0 when unknown. "Raise compatibility level" is a row only while it blocks a named capability.</summary>
            public int CompatibilityLevel;
            /// <summary>Score points per readiness finding (ReadinessAnalyzer.Lifts), read only when a finding no row claims is likely, so the
            /// coverage row can name what each rule is worth. Null when not read.</summary>
            public IReadOnlyDictionary<string, double> AirLifts;
            /// <summary>The best-practice format findings the words row hands the assistant beside the readiness words: measures the
            /// row cannot prove a format for and columns no readiness rule asks about (BpaFormatItems). Empty when the scan could not run.</summary>
            public List<WordsQueueItem> BpaFormatWords = new List<WordsQueueItem>();
            /// <summary>The inactive-relationship and snowflake best-practice warnings that have no AI readiness twin on the model (a one-to-one
            /// chain the readiness rule does not count), as the proposal findings "Sort out N relationships" carries them, so every such
            /// warning is on a row and one acceptance closes it (PairedAcceptances).</summary>
            public List<ReadinessFinding> BpaOnlyRelationships = new List<ReadinessFinding>();
            /// <summary>The readiness safe findings (<see cref="SharedKeyOf"/>) whose write Model quality raises too: the rule is in both rule sets.</summary>
            public HashSet<string> BpaSharedFindings = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            /// <summary>For each field-name clash (its key, which is the clash finding's object name), the names of the fields that clash:
            /// what the collisions row carries for the assistant (ModelPlanJump.Find).</summary>
            public Dictionary<string, string[]> CollisionNames = new Dictionary<string, string[]>(StringComparer.Ordinal);
            /// <summary>How many reports' use of this model has been read: the sweep is report-aware for these, and
            /// only these (a reading proves nothing about any other report).</summary>
            public int ReportsRead;
            /// <summary>How many chosen reports were checked and could NOT be read (a .pbix, a blocked published report).
            /// They are not read, and the sweep does not count them as read: it falls back to the model alone.</summary>
            public int ReportsUnreadable;
            /// <summary>How many of the sweep's safe columns <see cref="PlanUnusedAsync"/> lists UNTICKED (the rule would not suggest them).</summary>
            public int CleanupUnticked;
            /// <summary>How many unused measures the cleanup list leaves off (a measure's removal files on its proposer's row).</summary>
            public int CleanupMeasures;
            public ModelPlanTable[] Tables = Array.Empty<ModelPlanTable>();
            public int Tables_;
            public int Columns_;
            public int Measures_;
            public bool Connected;

            /// <summary>Measure refs used by the reports that were actually read, or null when no report has
            /// been read for this model. Null and empty mean different things and are kept apart.</summary>
            public HashSet<string> MeasuresInReports;

            /// <summary>For each measure in <see cref="MeasuresInReports"/>, the names of the reports that use it, so a check
            /// row can say which report a broken number would reach.</summary>
            public Dictionary<string, string[]> ReportsByMeasure = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

            /// <summary>Refs of measures a saved check targets, or null when saved checks could not be read
            /// (they are Pro).</summary>
            public HashSet<string> CheckedMeasures;

            public int ReportsNeedingChecking;
            public string[] ReportsUnchecked = Array.Empty<string>();

            /// <summary>The grade the safe fixes would reach, rehearsed. Null when it was not computed, and
            /// also null while <see cref="ProjectionPending"/> is true.</summary>
            public string ProjectedGrade;

            /// <summary>The score (0 to 100) from the same rehearsal as <see cref="ProjectedGrade"/>, and null
            /// exactly when that is.</summary>
            public double? ProjectedScore;

            /// <summary>True while the rehearsal that produces <see cref="ProjectedGrade"/> is still running.
            /// A pending grade is not an unknown grade and the plan must not say it is.</summary>
            public bool ProjectionPending;

            /// <summary>The model revision these facts were read at.</summary>
            public long Revision;

            /// <summary>What the starter measures RULE would add, fact table by fact table (StarterMeasuresFamily.
            /// RuleSummary). Read with the model's shape, so it is a function of the revision like the rest.</summary>
            public List<(string Table, List<(string Column, string Measure)> Measures)> StarterRule =
                new List<(string, List<(string, string)>)>();

            /// <summary>Every column the starter family owns (StarterMeasureTemplates.Candidates): a number column on
            /// a fact table that no measure uses yet, whether it proposes a measure for it or decides none. The old
            /// "Add N explicit measures" item leaves these alone, so one column has one proposal.</summary>
            public HashSet<string> StarterColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            /// <summary>True for a call where Jev decides the starter measures, so the rule's starter items stand
            /// aside. Set per call, never cached: it depends on the tier and the permission, not the model.</summary>
            public bool StarterByJev;

            /// <summary>The objects a live Jev line sets the format of, kept or a suggestion Jev was not unsure of. The rules' format
            /// row leaves these out, so one column has one proposal (N8, and the join's item 9). Set per call, never cached, like <see cref="StarterByJev"/>.</summary>
            public HashSet<string> JevFormatted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            /// <summary>Jev decides the date table (Plan/JevFamilies/DateTableFamily.cs), so the rule's "Mark the date
            /// table" half of DateTableItem stands aside. The calendar half stays: it is not this point's question.</summary>
            public bool DateTableByJev;

            /// <summary>Some table holds a date column. Decides whether the date table row can tell the assistant to add a calculated date table.</summary>
            public bool HasDateColumn;

            /// <summary>No table is marked as the date table and a real (not auto date/time) table holds a date column. DATE-MARK is dormant
            /// without time intelligence DAX, so this is what gives such a model its "Add a date table" row (hero 20, 8s-c).</summary>
            public bool NoDateTable;

            /// <summary>The honesty lines the EXPENSIVE half produced. The cheap half adds its own per call,
            /// and <see cref="Note"/> is joined from both.</summary>
            public List<string> Gaps = new List<string>();

            public string Note;

            /// <summary>The one table and date column the model's DAX reads, when there is exactly one (lane E5).</summary>
            public PlanDateMarkCandidate DateMark;

            /// <summary>A copy for one call. The expensive fields are shared by reference because nothing
            /// mutates them after the gather; the cheap ones are overwritten by the caller. Copying rather than
            /// handing out the cached instance keeps one call's fresh session read out of another call's view.</summary>
            public PlanFacts ForOneCall()
            {
                var copy = (PlanFacts)MemberwiseClone();
                copy.Gaps = new List<string>(Gaps);
                return copy;
            }
        }

        /// <summary>One remembered gather.
        ///
        /// WHY REMEMBERING IS SAFE. Everything the entry holds is a function of the model at ONE revision and
        /// of which reports have been read, and both of those are in the key, so a hit can never describe a
        /// model that has moved. The facts that are deliberately NOT in the key are the ones that change
        /// without the revision moving: unsaved changes, the live connection, and the saved checks (a saved
        /// check is written to the sidecar, not to the model). Those are read fresh on every call and are
        /// never cached, so the plan cannot go stale about them.
        ///
        /// A model switch or a session close gives a new <see cref="Session"/> with a new id, so the entry
        /// simply stops matching and the next call gathers. Nothing has to remember to drop it.</summary>
        private sealed class PlanFactsEntry
        {
            public string SessionId;
            public long Revision;

            /// <summary>Which reports are chosen and which have been read, as ReportScope's own comparable
            /// basis string plus its generation. In the key because the report usage below is derived from it
            /// and a re-selection moves it without moving the model.</summary>
            public string ScopeKey;

            public PlanFacts Core;

            /// <summary>The rehearsed grade once it has landed, or null.</summary>
            public string ProjectedGrade;

            /// <summary>The rehearsed score, landed together with <see cref="ProjectedGrade"/>, or null.</summary>
            public double? ProjectedScore;

            /// <summary>True while the background rehearsal for this entry is still running.</summary>
            public bool ProjectionPending;

            /// <summary>The background rehearsal itself, so a test can wait for the number instead of
            /// sleeping for it. Never awaited by the engine: a plan never waits on a projection.</summary>
            public Task Projection = Task.CompletedTask;
        }

        /// <summary>The background grade rehearsal for the plan last gathered, or an already-finished task when
        /// none is running. A test seam (see <see cref="PlanGatherCountForTest"/>): it makes "wait for the
        /// projected grade" deterministic instead of a sleep.</summary>
        internal Task PlanProjectionForTest => _planFacts?.Projection ?? Task.CompletedTask;

        /// <summary>The facts for THIS call: the remembered expensive half, plus the cheap half read fresh.
        /// Called under <see cref="_modelPlanGate"/>, so two concurrent first calls gather once.</summary>
        private async Task<PlanFacts> FactsAsync(Session s)
        {
            var revision = s.Revision;
            var basis = CaptureScopeBasis(s);
            // Which chosen reports are not current is part of the key too. The signature is blind to the revision on
            // purpose (a removal's review must survive an edit), so a report checked again at a NEW revision left the
            // key as it was, and a Plan gathered in the moment between a commit and that check (the one Run the plan
            // returns) kept "nothing can be called unused yet" until the next commit (fix round 2, 28 Sep).
            //
            // And which readings the Plan answers from: the signature is blind to the time of a reading on purpose, so a
            // report checked AGAIN (it changed, or half of it can no longer be parsed) at the same revision left the
            // key as it was and the cleanup row kept answering from the old reading (verify, 29 Sep, D6).
            var scopeKey = basis.Signature + "|gen:" + basis.Generation.ToString(CultureInfo.InvariantCulture)
                + "|unchecked:" + string.Join("|", basis.Coverage?.Unchecked ?? Array.Empty<string>())
                + "|reading:" + basis.Reading;

            var entry = _planFacts;
            if (entry == null
                || !string.Equals(entry.SessionId, s.Id, StringComparison.Ordinal)
                || entry.Revision != revision
                || !string.Equals(entry.ScopeKey, scopeKey, StringComparison.Ordinal))
            {
                entry = new PlanFactsEntry { SessionId = s.Id, Revision = revision, ScopeKey = scopeKey };
                entry.Core = await GatherAsync(s, revision, basis).ConfigureAwait(false);
                _planFacts = entry;
                StartProjection(s, entry, entry.Core.Readiness);
            }

            var facts = entry.Core.ForOneCall();
            facts.ProjectedGrade = entry.ProjectedGrade;
            facts.ProjectedScore = entry.ProjectedScore;
            facts.ProjectionPending = entry.ProjectionPending;

            // The cheap half. Every one of these can move without the revision moving, so none of it is kept.
            (facts.Session, facts.SavesSessionId, facts.Saves, facts.UnsavedEdits) = await SessionInfoAsync((info, session) =>
            {
                // The person's changes, not Semanticus's marker (Session.HasUnsavedEdits): what the save row is about.
                var edits = session == null ? info.HasUnsavedChanges : info.HasUnsavedChanges && session.HasUnsavedEdits;
                // Set in this dispatcher turn, so a save queued behind it already knows the Plan saw the model unsaved.
                if (session != null && edits) session.PlanSawUnsaved = true;
                return (info, session?.Id, session?.SaveCount ?? 0, edits);
            }).ConfigureAwait(false);
            facts.ModelName = facts.Session?.ModelName;
            facts.Connected = facts.Session?.LiveConnected == true;
            facts.ReportsNeedingChecking = basis.Coverage?.Unchecked?.Length ?? 0;
            facts.ReportsUnchecked = basis.Coverage?.Unchecked ?? Array.Empty<string>();
            facts.Tables = WithSizes(facts.Tables, s);

            // The compatibility level is a database setting the edit timeline does not count: raising it leaves the revision where it was, so the
            // remembered half would go on offering "Raise compatibility level" after it was raised. Read every call, like the saved checks.
            try { facts.CompatibilityLevel = await s.ReadAsync(m => m.Database?.CompatibilityLevel ?? 0).ConfigureAwait(false); }
            catch { /* the remembered level stands */ }

            // Saved checks are written to the sidecar rather than to the model, so a saved check would never
            // move the revision that keys the cache. Read every call or the plan would go on saying a measure
            // has no check after one was saved. They stopped being Pro on 22 September, so every tier reads
            // them and the plan no longer has a tier-shaped hole in it.
            try
            {
                var suite = await ListTestDefinitionsCoreAsync().ConfigureAwait(false);
                facts.CheckedMeasures = new HashSet<string>(
                    (suite?.Definitions ?? Array.Empty<TestDefinition>())
                        .Where(d => d.Enabled && !string.IsNullOrWhiteSpace(d.TargetRef))
                        .Select(d => d.TargetRef),
                    StringComparer.OrdinalIgnoreCase);
            }
            catch
            {
                facts.Gaps.Add("Semanticus could not open the checks, so it cannot tell which measures already have one.");
            }

            facts.Note = facts.Gaps.Count == 0 ? null : string.Join(" ", facts.Gaps);
            return facts;
        }

        private async Task<PlanFacts> GatherAsync(Session s, long revision, ScopeBasis basis)
        {
            Interlocked.Increment(ref _planGathers);
            var facts = new PlanFacts { Revision = revision };
            var gaps = facts.Gaps;

            facts.Readiness = await AiReadinessScanAsync().ConfigureAwait(false);   // a finding whose change another acceptance declined already reads as accepted
            var cleanup = await PlanUnusedAsync(s).ConfigureAwait(false);
            (facts.Unused, facts.ReportsRead, facts.ReportsUnreadable, facts.CleanupUnticked, facts.CleanupMeasures) =
                (cleanup.Unused, cleanup.ReportsRead, cleanup.ReportsUnreadable, cleanup.Unticked, cleanup.Measures.Length);
            try
            {
                var readiness = facts.Readiness;
                var bpa = await s.ReadAsync(m =>
                {
                    var rules = GetBpaRules(m);
                    var card = BpaAnalyzer.Analyze(m, rules);
                    var sharedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var (lines, refused) = BpaPlanLines(m, card, ReadinessSafeLines(m, readiness, JevDecidedFinding), sharedKeys);
                    // The readiness safe fixes whose write Model quality raises as well: their rows count under both filters.
                    var sharedFindings = (readiness?.Findings ?? Array.Empty<ReadinessFinding>())
                        .Where(x => !x.Waived && string.Equals(x.Fix, nameof(FixKind.SafeFix), StringComparison.Ordinal))
                        .Where(x => !JevDecidedFinding(x.RuleId, x.ObjectRef))
                        .Where(x => { var l = MapSafeFix(m, x); return l != null && sharedKeys.Contains(BpaKey(l)); })
                        .Select(SharedKeyOf).ToList();
                    // The format findings the words row asks for: the same list get_words_queue ends with, worked out from the same scan.
                    var readinessWords = WordsFindings(readiness?.Findings, m.Tables.Count)
                        .Select(x => new WordsQueueItem { Kind = WordsKindOf(x), Tool = WordsToolOf(WordsKindOf(x), x.RuleId), ObjectRef = x.ObjectRef })
                        .ToList();
                    var formats = m.Tables.Count > 0
                        ? BpaFormatItems(m, card, rules, readinessWords, ColumnsOnFormatRow(readiness?.Findings))
                        : new List<WordsQueueItem>();
                    var only = card.Violations
                        .Where(v => !v.IsAccepted() && PairedAcceptances.AirTwinOf(v.RuleId) is string air
                                    && !(readiness?.Findings ?? Array.Empty<ReadinessFinding>())
                                        .Any(x => string.Equals(x.RuleId, air, StringComparison.OrdinalIgnoreCase) && WaiverStore.SameRef(x.ObjectRef, v.ObjectRef)))
                        .Select(v => new ReadinessFinding
                        {
                            RuleId = v.RuleId, RuleTitle = v.RuleName, Category = nameof(ReadinessCategory.Relationships), Fix = nameof(FixKind.Proposal),
                            ObjectRef = v.ObjectRef, ObjectName = v.ObjectName, Message = v.Message,
                        }).ToList();
                    var cover = new List<BpaCoverFinding>();
                    var coverOk = false;
                    IReadOnlyDictionary<string, double> lifts = null;
                    try
                    {
                        cover = BpaCoverFindings(m, card, rules, readinessWords, formats, only, ColumnsOnFormatRow(readiness?.Findings), readiness?.Findings ?? Array.Empty<ReadinessFinding>(),
                            new HashSet<string>((facts.Unused?.Items ?? Array.Empty<UnusedItem>()).Where(i => string.Equals(i.Verdict, "safe", StringComparison.Ordinal)).Select(i => i.Ref ?? ""), StringComparer.OrdinalIgnoreCase));
                        // What each rule is worth is read (a second scan) only when a finding that no row is likely to claim exists.
                        if ((readiness?.Findings ?? Array.Empty<ReadinessFinding>()).Any(MayNeedCoverage))
                            lifts = new ReadinessAnalyzer().Lifts(m, out _);
                        coverOk = true;
                    }
                    catch { /* the coverage rows are the safety net: without them the plan is what it was, never worse */ }
                    return (lines, refused.Count, formats, only, sharedFindings, cover, lifts, coverOk);
                }).ConfigureAwait(false);
                facts.BpaCover = bpa.cover;
                facts.BpaCoverKnown = bpa.coverOk;
                facts.AirLifts = bpa.lifts;
                facts.BpaLines = bpa.lines;
                facts.BpaLeftOut = bpa.Item2;
                facts.BpaFormatWords = bpa.formats;
                facts.BpaOnlyRelationships = bpa.only;
                facts.BpaSharedFindings = new HashSet<string>(bpa.sharedFindings, StringComparer.OrdinalIgnoreCase);
            }
            catch { facts.BpaLines = null; }   // a scan that cannot run raises no row; it never claims the model is clean

            try
            {
                facts.CollisionNames = await s.ReadAsync(m => ReadinessRuleSet.FieldNameClashes(m)
                    .GroupBy(c => c.Key, StringComparer.Ordinal)
                    .ToDictionary(g => g.Key, g => g.SelectMany(c => c.Members.Select(x => x.Obj.Name)).Distinct(StringComparer.Ordinal).ToArray(), StringComparer.Ordinal))
                    .ConfigureAwait(false);
            }
            catch { facts.CollisionNames = new Dictionary<string, string[]>(StringComparer.Ordinal); }   // the row then lists what its findings name

            var removedMeasures = RemovedMeasureRefs(s);
            var shape = await s.ReadAsync(m => new
            {
                Tables = m.Tables.Count,
                Columns = m.AllColumns.Count(),
                Measures = m.AllMeasures.Count(),
                Map = m.Tables
                    .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(t => new ModelPlanTable
                    {
                        Name = t.Name,
                        Kind = KindOf(t),
                        // Counted the way the Overview's block chart counts (LocalEngine.BuildGraph): the
                        // row-number column is plumbing and is not a column a person has.
                        Columns = VisibleColumns(t).Count(),
                        Measures = t.Measures.Count,
                        Relationships = RelationshipsOf(t).Length,
                    })
                    .ToArray(),
                Starter = StarterMeasuresFamily.RuleSummary(m, removedMeasures),
                StarterColumns = StarterMeasureTemplates.Candidates(m).SelectMany(x => x.Columns).Select(c => ObjectRefs.For(c)).ToArray(),
                DateMark = PlanDateMark.Candidate(m),
                HasDateColumn = m.AllColumns.Any(c => c.DataType == TabularEditor.TOMWrapper.DataType.DateTime),
                NoDateTable = !m.Tables.Any(t => string.Equals(t.DataCategory, "Time", StringComparison.OrdinalIgnoreCase))
                              && m.Tables.Any(t => !ReadinessRuleSet.IsAutoDateTable(t) && t.Columns.Any(c => c.DataType == TabularEditor.TOMWrapper.DataType.DateTime)),
                Compat = m.Database?.CompatibilityLevel ?? 0,
            }).ConfigureAwait(false);
            facts.DateMark = shape.DateMark;
            facts.HasDateColumn = shape.HasDateColumn;
            facts.NoDateTable = shape.NoDateTable;
            facts.CompatibilityLevel = shape.Compat;
            facts.Tables_ = shape.Tables;
            facts.Columns_ = shape.Columns;
            facts.Measures_ = shape.Measures;
            facts.Tables = shape.Map;
            facts.StarterRule = shape.Starter;
            facts.StarterColumns = new HashSet<string>(shape.StarterColumns, StringComparer.OrdinalIgnoreCase);
            StampFindings(facts.Tables, facts.Readiness);

            // The reports, from the reading already on disk. Nothing here goes to the network. Reports are optional:
            // with none read, MeasuresInReports stays unknown (not "nothing") and the plan says nothing about it
            // (Kane, 8 Oct, K2/K12).
            try
            {
                var coverage = basis?.Coverage;
                if (coverage != null && coverage.Parts.Count > 0)
                {
                    var usage = await s.ReadAsync(m => LineageGraph.AnalyzeReports(m, coverage.Parts, coverage.ChoiceIds))
                                       .ConfigureAwait(false);
                    facts.MeasuresInReports = new HashSet<string>(
                        (usage?.ModelFieldsUsed ?? Array.Empty<string>())
                            .Where(r => r != null && r.StartsWith("measure:", StringComparison.OrdinalIgnoreCase)),
                        StringComparer.OrdinalIgnoreCase);
                    foreach (var g in (usage?.Reports ?? Array.Empty<ReportUsage>())
                                 .SelectMany(r => (r.UsedRefs ?? Array.Empty<string>()).Select(u => (Ref: u, r.Name)))
                                 .Where(p => p.Ref != null && !string.IsNullOrWhiteSpace(p.Name))
                                 .GroupBy(p => p.Ref, StringComparer.OrdinalIgnoreCase))
                        facts.ReportsByMeasure[g.Key] = g.Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                }
            }
            catch
            {
                gaps.Add("Semanticus could not look at the reports, so it cannot tell which measures they use.");
            }

            return facts;
        }

        /// <summary>What kind of table this is, most specific first.
        ///
        /// THE FIELD-PARAMETER TEST IS NOT A NEW ONE. It is LocalEngine.IsFieldParameterTable, the same
        /// predicate the Overview's block chart classifies by (LocalEngine.cs, BuildGraph -&gt;
        /// GraphTable.IsFieldParameter). Two classifiers for one idea drift, and then the Map and the
        /// Overview disagree about the same table in the same session.
        ///
        /// The order matters and is deliberate. A field parameter is a disconnected calculated table and
        /// would otherwise fall through to "table"; a measure table has no columns to make it a dimension; a
        /// bridge has relationships on both sides and would otherwise be called a dimension, which hides
        /// exactly the thing a person needs to see about it.</summary>
        internal static string KindOf(Table t)
        {
            if (IsFieldParameterTable(t)) return ModelPlanTableKinds.FieldParameter;
            if (string.Equals(t.DataCategory, "Time", StringComparison.OrdinalIgnoreCase)) return ModelPlanTableKinds.DateTable;

            var columns = VisibleColumns(t).ToArray();
            if (columns.Length == 0 && t.Measures.Count > 0) return ModelPlanTableKinds.MeasureTable;

            var mine = RelationshipsOf(t);
            // A bridge is nothing but the keys it joins on. Every column it has is an end of one of its own
            // relationships, and it has at least two of them, or it is just a small dimension.
            if (mine.Length >= 2 && columns.Length > 0
                && columns.All(c => mine.Any(r => r.FromColumn == c || r.ToColumn == c)))
                return ModelPlanTableKinds.Bridge;

            var many = mine.Any(r => r.FromTable == t);
            var one = mine.Any(r => r.ToTable == t);
            if (many && !one) return ModelPlanTableKinds.Fact;
            if (one) return ModelPlanTableKinds.Dimension;
            return ModelPlanTableKinds.Plain;
        }

        /// <summary>The columns a person has. The row-number column is plumbing, and counting it would make
        /// every measure table look like it had one column.</summary>
        private static IEnumerable<Column> VisibleColumns(Table t) =>
            t.Columns.Where(c => c.Type != ColumnType.RowNumber);

        /// <summary>Every relationship with this table at either end.</summary>
        private static SingleColumnRelationship[] RelationshipsOf(Table t) =>
            t.Model == null
                ? Array.Empty<SingleColumnRelationship>()
                : t.Model.Relationships.OfType<SingleColumnRelationship>()
                    .Where(r => r.FromTable == t || r.ToTable == t)
                    .ToArray();

        /// <summary>File the active readiness findings onto the tables they are about, grouped by the SAME
        /// category names the findings page groups by, so a count here and a filter there select the same
        /// findings. A waived finding is an accepted one and is not work, so it is not counted. A finding
        /// whose reference names no table is about the model and is filed nowhere rather than onto a guess.</summary>
        private static void StampFindings(ModelPlanTable[] tables, Scorecard card)
        {
            var byTable = (card?.Findings ?? Array.Empty<ReadinessFinding>())
                .Where(x => x != null && !x.Waived)
                .Select(x => new { Table = TableOfRef(x.ObjectRef), Category = x.Category ?? "BestPractice" })
                .Where(x => x.Table != null)
                .GroupBy(x => x.Table, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    g => g.Key,
                    g => g.GroupBy(x => x.Category, StringComparer.Ordinal)
                          .OrderBy(c => c.Key, StringComparer.Ordinal)
                          .Select(c => new ModelPlanTableFinding { Category = c.Key, Count = c.Count() })
                          .ToArray(),
                    StringComparer.OrdinalIgnoreCase);

            foreach (var table in tables ?? Array.Empty<ModelPlanTable>())
            {
                ModelPlanTableFinding[] found;
                table.Findings = byTable.TryGetValue(table.Name ?? string.Empty, out found)
                    ? found
                    : Array.Empty<ModelPlanTableFinding>();
            }
        }

        /// <summary>Start the grade rehearsal for one gather, so the FIRST look does not wait for a second
        /// readiness scan. The plan comes back with the grade still pending and one line saying so; the next
        /// call returns the number.
        ///
        /// WHY THIS IS SAFE TO PUT IN THE BACKGROUND, and the one fact it rests on. The rehearsal works by
        /// turning on a dry-run flag and then really mutating the model, so a flag that leaked into another
        /// caller's flow would silently turn a person's edit into a rehearsal that is rolled back. It cannot
        /// leak here: <c>DryRunScope.Current</c> is an AsyncLocal (DryRunScope.cs:12), and Task.Run runs the
        /// delegate on a COPY of the execution context, so the collector set below is invisible to every other
        /// flow. Were that flag a plain static or a [ThreadStatic], this would have to stay on the request
        /// path and the first look would keep paying for two scans.
        ///
        /// The mutation itself is one dispatcher work item that always rolls back (Session.RehearseAsync), and
        /// the dispatcher is single-writer, so no other caller can read the model mid-rehearsal either.</summary>
        private void StartProjection(Session s, PlanFactsEntry entry, Scorecard now)
        {
            // Nothing to rehearse: the grade after is the grade now, and it is known immediately.
            if (now == null || now.SafeFixCount <= 0)
            {
                entry.ProjectedGrade = now?.Grade;
                entry.ProjectedScore = now?.Grade == null ? null : now.Overall;
                return;
            }

            entry.ProjectionPending = true;
            entry.Projection = Task.Run(async () =>
            {
                var rehearsed = await ProjectedScorecardAsync(s, now).ConfigureAwait(false);
                await _modelPlanGate.WaitAsync().ConfigureAwait(false);
                try
                {
                    // Publish onto THIS entry only. If the model moved while the rehearsal ran, the entry has
                    // already been replaced and this number is about a model that no longer exists.
                    if (!ReferenceEquals(_planFacts, entry)) return;
                    // The letter and the number come off ONE scorecard, so they cannot describe two models.
                    entry.ProjectedGrade = rehearsed?.Grade;
                    entry.ProjectedScore = rehearsed?.Grade == null ? null : rehearsed.Overall;
                    entry.ProjectionPending = false;
                }
                finally { _modelPlanGate.Release(); }
            });
        }

        /// <summary>The grade the safe fixes would reach, worked out by REHEARSING them: the real fixes are
        /// applied, the real analyzer runs on the changed model inside the same batch, and the batch is rolled
        /// back. Nothing is written, no revision moves, and nothing is broadcast (Session.RehearseAsync).
        ///
        /// It returns null rather than a guess whenever it cannot do that honestly: no safe fixes to project,
        /// or the rehearsal failed. A grade after that was estimated would be the worst kind of number here,
        /// because the whole tile exists to tell a person whether the work is worth doing.</summary>
        private async Task<Scorecard> ProjectedScorecardAsync(Session s, Scorecard now)
        {
            if (now == null || now.SafeFixCount <= 0) return now;

            var previous = DryRunScope.Current;
            try
            {
                DryRunScope.Current = new DryRunCollector();
                Scorecard rehearsed = null;
                await s.MutateAsync("engine", "plan: rehearse the safe fixes", m =>
                {
                    SafeFixes.Apply(m, null, null, DeclinedChangesOf(m).Declines);
                    rehearsed = new ReadinessAnalyzer().Analyze(m);
                }).ConfigureAwait(false);
                return rehearsed;
            }
            catch
            {
                // A rehearsal that fails has still rolled back (the batch reverts on the exception path too),
                // so the model is untouched and the only loss is the number.
                return null;
            }
            finally { DryRunScope.Current = previous; }
        }

        // ---- the items -------------------------------------------------------------------------------

        private static bool IsSafeFixItem(string id) => id != null && id.StartsWith("safefix/", StringComparison.Ordinal);

        /// <summary>Whether the scan holds a DATE-MARK finding at all, accepted or not. The "Add a date table" fallback is for a
        /// model where the rule is dormant; once the rule has fired and the person accepted it, the person's choice stands and
        /// the fallback must not bring the row back under the same id.</summary>
        private static bool DateMarkFindingExists(PlanFacts f) =>
            (f.Readiness?.Findings ?? Array.Empty<ReadinessFinding>()).Any(x => string.Equals(x.RuleId, "DATE-MARK", StringComparison.OrdinalIgnoreCase));

        private static List<ModelPlanItem> BuildItems(PlanFacts f, Func<string, string, bool> decidedByJev = null, bool countDeclined = false)
        {
            var items = new List<ModelPlanItem>();
            // A finding whose object a live Jev family decided is not the rule's to act on here (JevDecidedFinding): a safe
            // fix is not its to make and a proposal not its to offer, because the family's row makes that change for the
            // column. The scan itself is never filtered.
            var open = (f.Readiness?.Findings ?? Array.Empty<ReadinessFinding>()).Where(x => !x.Waived || countDeclined).ToArray();
            var findings = open
                .Where(x => decidedByJev == null || !decidedByJev(x.RuleId, x.ObjectRef))
                .ToArray();

            items.AddRange(SafeFixItems(findings, f.BpaSharedFindings));
            var bpaFix = BpaFixItem(f);
            if (bpaFix != null) items.Add(bpaFix);
            var words = WordsItem(findings, f.Tables_, f.BpaFormatWords);
            if (words != null) items.Add(words);
            var cleanup = CleanupItem(f);
            if (cleanup != null) items.Add(cleanup);
            items.AddRange(CheckItems(f));
            var publish = PublishItem(f);
            if (publish != null) items.Add(publish);
            var date = DateTableItem(f.DateTableByJev
                ? findings.Where(x => !string.Equals(x.RuleId, "DATE-MARK", StringComparison.OrdinalIgnoreCase)).ToArray()
                : findings, f.DateMark, f.HasDateColumn, f.NoDateTable && !f.DateTableByJev && !DateMarkFindingExists(f), f.CompatibilityLevel);
            if (date != null) items.Add(date);
            var recheck = RecheckItem(f);
            if (recheck != null) items.Add(recheck);
            items.AddRange(ProposalItems(findings.Concat(f.BpaOnlyRelationships).ToArray(), f.StarterColumns, f.JevFormatted, f.CollisionNames));
            if (!f.StarterByJev) items.AddRange(StarterRuleItems(f));

            var compat = CompatibilityItem(f);
            if (compat != null) items.Add(compat);

            // Last: whatever scores and no row above acts on goes to the assistant's coverage row for its page.
            items.AddRange(CoverageItems(f, open, decidedByJev, items.ToArray()));
            // Each assistant row says when it is done and what to choose without asking.
            AssistantDefaults.Apply(items);
            return items;
        }

        internal const string UnsavedItemId = "ship/unsaved";

        // "Save the changes you have made" is judged on three save counts, each read in a dispatcher turn and none of
        // them late (verifier round 5):
        //   the row's mark    (PlanMemoryState.UnsavedSeen): the session, and how many saves that WROTE edits it had
        //                     made, as the facts were read the last time the row was on the plan. Read with the facts,
        //                     never when the compose finishes, or a save that lands mid-compose counts as seen (D14).
        //   the facts' count  (PlanFacts.Saves): the same count for the session open now, read with the facts.
        //   the save's mark   (PlanMemoryState.LastSave): what a save that wrote edits recorded in the turn that marked
        //                     the model clean, kept beside the model so the next engine sees it after a restart (D16).
        // A save of a model that was already clean (Undo back to what the files hold, then Save) wrote nothing and
        // moves none of them (D15), so which order the undo, the save and the plan reads come in cannot change the row.

        /// <summary>Whether a save that wrote edits landed after the unsaved row was last seen on the plan, by the session
        /// that saw it: counted in the facts when that session is still open, and in the save's own mark when it is
        /// not (a restart, a re-open). A discard is no save, so those changes read as thrown away, not saved.</summary>
        private static bool SavedSinceUnsavedRowSeen(PlanMemoryState memory, string sessionId, int saves)
        {
            var seen = memory.UnsavedSeen;
            if (seen?.Session == null) return false;
            if (string.Equals(seen.Session, sessionId, StringComparison.Ordinal)) return saves > seen.Saves;
            var last = memory.LastSave;
            return last != null && string.Equals(last.Session, seen.Session, StringComparison.Ordinal) && last.Saves > seen.Saves;
        }

        /// <summary>A save that wrote edits, after the Plan read this model unsaved, records itself beside the model, so
        /// "Save the changes you have made" still reads as done when the next read is another engine's (D16). The two
        /// facts come from the turn that marked the model clean. Best effort like the read path's own bookkeeping: a
        /// save that could not record this is still a save, and the cost is the row reading off the plan after a
        /// restart, the behaviour before this mark existed.</summary>
        private void NotePlanSave(Session s, int saves, string origin)
        {
            try
            {
                var binding = BindPlanMemory();
                if (!ReferenceEquals(binding.Session, s)) return;
                TryWritePlanMemory(binding, state => state.LastSave = new PlanSaveMark { Session = s.Id, Saves = saves, By = ChangeBy.ApproverFor(origin) });
            }
            catch { /* the save itself landed; see above */ }
        }

        /// <summary>The ids of the rule rows that are off the plan this call only because a live Jev family owns their
        /// work: the format rows whose every column a kept Jev line formats, and the starter rows while Jev decides the
        /// starters. Worked out by building those rows again without Jev's ownership, so it can never disagree with
        /// what BuildItems left out.</summary>
        private static HashSet<string> StoodAsideForJev(PlanFacts f)
        {
            var aside = StoodAsideRuleRows(f);
            if (f.JevFormatted.Count > 0)
            {
                var findings = (f.Readiness?.Findings ?? Array.Empty<ReadinessFinding>()).Where(x => !x.Waived).ToArray();
                var shown = new HashSet<string>(ProposalItems(findings, f.StarterColumns, f.JevFormatted).Select(i => i.Id), StringComparer.Ordinal);
                foreach (var item in ProposalItems(findings, f.StarterColumns, new HashSet<string>(StringComparer.OrdinalIgnoreCase)))
                    if (!shown.Contains(item.Id)) aside.Add(item.Id);
            }
            if (f.StarterByJev)
                foreach (var item in StarterRuleItems(f)) aside.Add(item.Id);
            return aside;
        }

        /// <summary>The starter measures, decided by the RULE: one item per fact table with at least one line.
        /// Shown to every tier exactly like the other rule items, and stood aside only for a session where Jev
        /// decides them instead (Plan/JevFamilies/StarterMeasuresFamily.cs, "who decides"). Its lines are worked
        /// out by propose, like a safe fix's.</summary>
        private static IEnumerable<ModelPlanItem> StarterRuleItems(PlanFacts f) =>
            (f.StarterRule ?? new List<(string, List<(string, string)>)>()).Select(t =>
            {
                var diff = t.Measures.Select(x => "+ " + x.Measure + ": from " + x.Column).Take(8).ToArray();
                return new ModelPlanItem
                {
                    Id = StarterMeasuresFamily.RuleItemId(t.Table),
                    Section = ModelPlanSections.Meaning,
                    Title = "Add " + CountOf(t.Measures.Count, "simple measure", "simple measures") + " in " + t.Table,
                    Sub = Join(t.Measures.Select(x => x.Measure), 4),
                    Value = "one answer per number",
                    // The starter measures answer DAC-IMPLICIT-MEASURE (a number column totalled by whatever drops it on a report).
                    Rules = new[] { "DAC-IMPLICIT-MEASURE" },
                    Goal = PlanGoals.Ai,
                    Who = ModelPlanOwners.Auto,
                    Status = ModelPlanStatuses.Ready,
                    Why = new[]
                    {
                        StarterMeasuresFamily.Missing(t.Measures.Count, t.Table),
                        "Each one totals its column the way the column is already set to. Key columns get none. One undo takes them all back.",
                    },
                    Diff = diff,
                    Touches = CountOf(t.Measures.Count, "measure", "measures") + " in " + t.Table,
                    Tables = new[]
                    {
                        new ModelPlanItemTable
                        {
                            Name = t.Table,
                            Count = t.Measures.Count,
                            What = CountOf(t.Measures.Count, "simple measure", "simple measures"),
                            Diff = diff,
                        },
                    },
                    Jump = new ModelPlanJump { Tool = "optimize", Label = "Pending changes" },
                };
            });

        /// <summary>The distinct rule ids of some findings, in the order the rules found them: what a row carries in <see cref="ModelPlanItem.Rules"/>.</summary>
        private static string[] RuleIdsOf(IEnumerable<ReadinessFinding> findings) =>
            (findings ?? Array.Empty<ReadinessFinding>()).Select(f => f?.RuleId).Where(r => !string.IsNullOrWhiteSpace(r))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        /// <summary>"ai" when any finding comes from an AI readiness rule, "quality" when every one is a best-practice rule that has no
        /// readiness twin (the synthetic relationship findings a best-practice scan adds, whose rule id is the best-practice rule's).</summary>
        private static string GoalOfFindings(IEnumerable<ReadinessFinding> findings) =>
            (findings ?? Array.Empty<ReadinessFinding>()).Any(f => f != null && PairedAcceptances.AirTwinOf(f.RuleId) == null)
                ? PlanGoals.Ai : PlanGoals.Quality;

        /// <summary>The key a readiness safe finding is known by in <see cref="PlanFacts.BpaSharedFindings"/>.</summary>
        private static string SharedKeyOf(ReadinessFinding x) => (x?.RuleId ?? "") + "|" + (x?.ObjectRef ?? "");

        /// <summary>"ai" and "quality" for a row that fixes a rule both pages raise: a finding Model quality also raises (a rule with a twin on
        /// the other page, or a safe fix whose write a best-practice rule makes too). Null for any other row, which keeps its one goal.</summary>
        private static string[] BothGoals(IEnumerable<ReadinessFinding> findings, ISet<string> shared)
        {
            var list = (findings ?? Array.Empty<ReadinessFinding>()).Where(x => x != null).ToList();
            if (GoalOfFindings(list) != PlanGoals.Ai) return null;
            return list.Any(x => PairedAcceptances.AirTwinOf(x.RuleId) != null || PairedAcceptances.BpaTwinOf(x.RuleId) != null
                                 || (shared != null && shared.Contains(SharedKeyOf(x))))
                ? new[] { PlanGoals.Ai, PlanGoals.Quality } : null;
        }

        /// <summary>The safe fixes, one item per rule family. The family is the readiness CATEGORY, because that
        /// is the grouping the rules themselves already use and the one the findings page groups by, so an item
        /// here and a filter there select the same things.</summary>
        private static IEnumerable<ModelPlanItem> SafeFixItems(IReadOnlyList<ReadinessFinding> findings, ISet<string> shared = null) =>
            findings
                .Where(x => string.Equals(x.Fix, nameof(FixKind.SafeFix), StringComparison.Ordinal))
                .GroupBy(x => x.Category ?? "BestPractice", StringComparer.Ordinal)
                .OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g =>
                {
                    var touches = Touches(g, "~ ", x => ": " + Lower(RuleTitle(x)));
                    return new ModelPlanItem
                    {
                        Id = "safefix/" + g.Key,
                        Section = SectionForCategory(g.Key),
                        Title = SafeFixTitle(g.Key, g.Count()),
                        Sub = Join(g.Select(x => x.ObjectName).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase), 4),
                        Value = "raises AI readiness",
                        Rules = RuleIdsOf(g),
                        Goal = PlanGoals.Ai,
                        Goals = BothGoals(g, shared),
                        Who = ModelPlanOwners.Auto,
                        Status = ModelPlanStatuses.Ready,
                        Why = g.Select(x => x.DisplayMessage ?? x.Message)
                               .Where(m => !string.IsNullOrWhiteSpace(m))
                               .Distinct(StringComparer.Ordinal)
                               .Take(4)
                               .ToArray(),
                        Diff = Diff(touches),
                        Touches = Objects(g.Count()),
                        Tables = TableDetail(touches),
                        Objects = RefsOf(g),
                        Claims = ClaimsOf(g),
                        Jump = new ModelPlanJump { Tool = "readiness", Label = "AI readiness" },
                    };
                });

        /// <summary>The measures this model's History says were removed, where the removal still stands: the newest
        /// edit that added or removed the ref, not undone, removed it. Read from the edit ledger, which keeps earlier
        /// sessions of the same model too, so a removal is remembered across a restart and forgotten by an undo.</summary>
        internal HashSet<string> RemovedMeasureRefsForTest(Session s) => RemovedMeasureRefs(s);

        private HashSet<string> RemovedMeasureRefs(Session s)
        {
            var removed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var e in _sessions.Ledger.ForModel(s.Id))
                {
                    if (e == null || e.Undone || e.Gone || !string.Equals(e.Kind, EditLedgerKinds.Edit, StringComparison.Ordinal)) continue;
                    // A measure the person left out of the start plan this commit built: refused on purpose, so no family offers it (UAT-J7-S2-009). The entry is
                    // skipped above once the build is undone, so the refusal goes back with it.
                    foreach (var r in e.Cause?.Declined ?? Array.Empty<string>()) removed.Add(StarterMeasuresFamily.DeclinedKey(r));
                    foreach (var d in e.Deltas ?? Array.Empty<ChangeDelta>())
                    {
                        if (d?.Ref == null || !d.Ref.StartsWith("measure:", StringComparison.OrdinalIgnoreCase)) continue;
                        if (string.Equals(d.Kind, "remove", StringComparison.Ordinal)) removed.Add(d.Ref);
                        else if (string.Equals(d.Kind, "add", StringComparison.Ordinal)) removed.Remove(d.Ref);
                    }
                }
            }
            catch { /* History that cannot be read forgets nothing new: the rule offers what it always did */ }
            return removed;
        }

        /// <summary>The cleanup row is ALWAYS OPTIONAL: it is offered last, nothing on the plan waits for it, and it
        /// says why (Kane, 28 Sep: anyone who follows the Plan must not be hurt). Reading one report proves nothing
        /// about the others, so a report reading changes what the row offers, never whether it is optional.</summary>
        internal const string CleanupOptionalSentence =
            "Optional. Semanticus has not looked at any report that uses this model yet, so it cannot tell if a report shows these. No other task waits for this one.";

        /// <summary>The optional sentence once <paramref name="reports"/> reports have been read: what was read, and
        /// that the others may still use these.</summary>
        internal static string CleanupOptionalReadSentence(int reports) =>
            "Optional. " + (reports == 1
                ? "Semanticus looked at 1 report that uses this model, and it does not show these"
                : "Semanticus looked at " + CountOf(reports, "report", "reports") + " that use this model, and none of them shows these")
            + ". Other reports may still use them. No other task waits for this one.";

        /// <summary>The optional sentence when the chosen reports were checked and none could be read (a .pbix, a blocked
        /// published report): the row answers from the model alone and says so.</summary>
        internal static string CleanupOptionalUnreadableSentence(int reports) =>
            "Optional. Semanticus could not open " + (reports == 1 ? "the 1 report you chose" : "the " + reports + " reports you chose")
            + ", so it cannot tell if a report shows these. No other task waits for this one.";

        /// <summary>The sentence that says what the row is FOR and that the person decides (Kane, 30 Sep 2026: "its more of a
        /// suggestion list as even a legit deletion may be required for a future report. only a user can properly assess this with
        /// the business context"). One constant, because the row, the tests and the drivers all read it.</summary>
        internal const string CleanupPurposeSentence =
            "This list helps keep the model small. It is only a suggestion, so look it over before anything is removed. Untick any column you want to keep, "
            + "or set the whole task aside. A column that nothing uses today may still be needed for a report you build later, and only you know that.";

        /// <summary>How many columns the row lists and where they start, in plain words. Ticked is what the rule suggests; unticked
        /// is listed anyway, each with its reason on its line.</summary>
        internal static string CleanupCountSentence(int ticked, int unticked)
        {
            var total = ticked + unticked;
            if (unticked == 0)
                return total == 1 ? "1 unused column is listed, and it starts ticked."
                                  : CountOf(total, "unused column is", "unused columns are") + " listed, and all of them start ticked.";
            if (ticked == 0)
                return CountOf(total, "unused column is", "unused columns are") + " listed. None start ticked, and each one says why.";
            return CountOf(total, "unused column is", "unused columns are") + " listed. " + ticked.ToString("N0", CultureInfo.InvariantCulture)
                 + " start ticked. " + unticked.ToString("N0", CultureInfo.InvariantCulture)
                 + " start unticked, and each one says why (for example, it is a key, a date, a name or a number). Open this task to see them all.";
        }

        private static ModelPlanItem CleanupItem(PlanFacts f)
        {
            var held = f.HeldClaims;
            var safe = (f.Unused?.Items ?? Array.Empty<UnusedItem>())
                .Where(i => string.Equals(i.Verdict, "safe", StringComparison.Ordinal))
                .Where(i => held == null || held.Count == 0 || !held.Contains(i.Ref ?? "\u0001"))   // a column an open task holds is that task's to remove
                .Where(i => f.SetAsideClaims == null || f.SetAsideClaims.Count == 0 || !f.SetAsideClaims.Contains(i.Ref ?? "\u0001"))   // and one a set-aside task held stays unlisted
                .ToArray();
            var unticked = f.CleanupUnticked;
            var total = safe.Length + unticked;
            if (total == 0) return null;
            var them = total == 1 ? "it" : "them";

            var why = new List<string>();
            why.Add(f.ReportsRead > 0 ? CleanupOptionalReadSentence(f.ReportsRead)
                : f.ReportsUnreadable > 0 ? CleanupOptionalUnreadableSentence(f.ReportsUnreadable) : CleanupOptionalSentence);
            why.Add(CleanupPurposeSentence);
            why.Add(CleanupCountSentence(safe.Length, unticked));
            why.Add("Nothing in the model uses " + them + (f.ReportsRead == 0 ? "."
                : f.ReportsRead == 1 ? ", and neither does the report Semanticus looked at." : ", and neither do the reports Semanticus looked at."));
            why.Add("A version is saved first, so one undo puts " + them + " back.");
            why.Add("Your assistant reads the lineage first (get_lineage, unused_objects), removes only what nothing uses, and tells you what it removed.");
            if (f.CleanupMeasures > 0)
                why.Add(CountOf(f.CleanupMeasures, "unused measure is", "unused measures are") + " not on this list. Remove a measure on its own, when you are sure no report needs it.");
            if (!string.IsNullOrWhiteSpace(f.Unused?.Caveat)) why.Add(f.Unused.Caveat);

            // The cleanup sweep names each object's table itself, so this one needs no ref parse.
            var touches = safe.Select(i => new PlanTouch
            {
                Table = string.IsNullOrWhiteSpace(i.Table) ? null : i.Table,
                Kind = string.Equals(i.Kind, "calcColumn", StringComparison.OrdinalIgnoreCase) ? "column" : i.Kind,
                Line = "- " + i.Kind + " " + (string.IsNullOrWhiteSpace(i.Table) ? i.Name : i.Table + "[" + i.Name + "]"),
            }).ToArray();

            return new ModelPlanItem
            {
                Id = "cleanup/model",
                Section = ModelPlanSections.Structure,
                // The title and the diff carry what STARTS TICKED. The unticked columns are counted in the sentence above and are
                // each a line, with a reason, once the person presses Work it out.
                Title = safe.Length > 0 ? "Remove " + CountOf(safe.Length, "unused column", "unused columns")
                                        : "Review " + CountOf(unticked, "unused column", "unused columns"),
                Sub = safe.Length > 0 ? "Optional: " + Join(safe.Select(i => i.Name), 4) : "Optional: none start ticked",
                Value = "a smaller, clearer model",
                Optional = true,
                // It names the columns it removes: the coverage step leaves the best-practice finding on each of them to this row.
                Objects = safe.Select(i => i.Ref).Where(r => !string.IsNullOrWhiteSpace(r)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                // The assistant reads the lineage and the reports before it removes anything, says what it removed, and one Undo puts it back
                // (Kane, 13 Oct 2026: assigning tasks to a human is the last resort).
                Who = ModelPlanOwners.Assistant,
                Status = ModelPlanStatuses.Ready,
                Why = why.ToArray(),
                Diff = Diff(touches),
                Touches = safe.Length > 0 ? Objects(safe.Length) : "your assistant decides which to remove",
                Tables = TableDetail(touches),
                Jump = new ModelPlanJump { Tool = "lineage", Label = "Lineage" },
            };
        }

        /// <summary>One item per measure that the reports actually use and that no saved check covers. Only ever
        /// raised when BOTH facts are known: with no report reading, or with saved checks unreadable, the plan
        /// says so in its note and raises nothing, because "no check" and "we could not look" are not the same
        /// thing and only one of them is work.</summary>
        private static IEnumerable<ModelPlanItem> CheckItems(PlanFacts f)
        {
            if (f.MeasuresInReports == null || f.CheckedMeasures == null) yield break;

            var uncovered = f.MeasuresInReports
                .Where(r => !f.CheckedMeasures.Contains(r))
                .OrderBy(r => r, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            foreach (var measureRef in uncovered.Take(MaxCheckItems))
            {
                // The measure's own name, as the person sees it in a report: a measure name is unique in the model, so the
                // table in front of it ("Key Measures/# New Customers") only made the title harder to read (Kane, 11 Oct 2026).
                var body = measureRef.Substring(measureRef.IndexOf(':') + 1);
                var name = body.Substring(body.LastIndexOf('/') + 1);
                yield return new ModelPlanItem
                {
                    Id = "check/" + measureRef,
                    Section = ModelPlanSections.Trust,
                    Title = "Add a check for " + name,
                    Sub = "a report shows it and no check tests it",
                    Value = "one more number you can trust",
                    // Not "auto": nothing in the engine writes a check by itself (calling it auto put a Start on the row that the
                    // engine then refused, F-199). The assistant drafts it with save_test and the person accepts the ground truth
                    // on the Tests page, so the row is the assistant's, not the person's (Kane, 8 Oct 2026: automation first,
                    // the person last).
                    Who = ModelPlanOwners.Assistant,
                    Status = ModelPlanStatuses.Ready,
                    Why = new[]
                    {
                        ReportsShowing(f, measureRef) + " " + name + ", but no check tests it.",
                        "Add one so a change that breaks it is caught.",
                        "Your assistant writes the check. You can see it on the Validation page.",
                    },
                    Diff = new[] { CheckLine(measureRef, name) },
                    Touches = "1 measure",
                    Tables = TableDetail(new[] { CheckTouch(measureRef, name) }),
                    Jump = new ModelPlanJump { Tool = "tests", Label = "Validation" },
                };
            }

            if (uncovered.Length > MaxCheckItems)
            {
                yield return new ModelPlanItem
                {
                    Id = "check/more",
                    Section = ModelPlanSections.Trust,
                    Title = CountOf(uncovered.Length - MaxCheckItems, "more measure needs", "more measures need") + " a check",
                    Sub = "see them on the Validation page",
                    Value = string.Empty,
                    Who = ModelPlanOwners.Assistant,
                    Status = ModelPlanStatuses.Proposed,
                    Why = new[]
                    {
                        "The Plan lists the first " + MaxCheckItems + " one by one and counts the rest here, so the list stays short.",
                    },
                    // No lines of their own: the row counts what is left rather than listing it, which is
                    // the whole point of the cap. The per-table detail still counts them, so a Map block can
                    // say how many of the uncounted measures are its own.
                    Diff = Array.Empty<string>(),
                    Touches = CountOf(uncovered.Length - MaxCheckItems, "measure", "measures"),
                    Tables = TableDetail(uncovered.Skip(MaxCheckItems).Select(r => new PlanTouch
                    {
                        Table = TableOfRef(r), Kind = KindOfRef(r), Line = null,
                    })),
                    Jump = new ModelPlanJump { Tool = "tests", Label = "Validation" },
                };
            }
        }

        private static ModelPlanItem PublishItem(PlanFacts f)
        {
            if (f.Session?.HasUnsavedChanges != true || !f.UnsavedEdits) return null;
            // A kept copy holds every change so far: the save this row asks for has happened, and only publishing is left (UAT 18 Oct N23).
            if (f.Session.KeptCopyPath != null) return null;

            var why = new List<string> { "This model has changes that are not saved yet." };
            if (f.Session.DiskDiverged)
                why.Add("The file on disk has also changed since you opened it, so look at it before you save over it.");
            if (f.Session.LiveBound)
                why.Add("It is connected to " + (f.Session.LiveDatabase ?? "a published model") + ", so only Publish puts the changes in front of the people who use its reports.");

            return new ModelPlanItem
            {
                Id = UnsavedItemId,
                Section = ModelPlanSections.Ship,
                Title = "Save the changes you have made",
                Sub = f.Session.DiskDiverged ? "the file on disk has changed too" : "they are only in Semanticus so far",
                Value = "nothing is lost",
                // Saving is one call (save_model) and one press (Kane, 8 Oct 2026), so the row is the assistant's; the person's Save stays on the row.
                Who = ModelPlanOwners.Assistant,
                Status = ModelPlanStatuses.Ready,
                Why = why.ToArray(),
                Diff = Array.Empty<string>(),
                Touches = f.ModelName,
                Jump = new ModelPlanJump { Tool = "history", Label = "History" },
            };
        }

        /// <summary>The date table row. When the model's own DAX names the one table and date column to mark
        /// (<see cref="PlanDateMark"/>), the row is Semanticus's: the engine can work out the exact change, and Run
        /// the plan does it (lane E5, the mockup's "Mark Date as the date table"). Otherwise it stays yours, because
        /// which table is the date table is a choice.</summary>
        internal static ModelPlanItem DateTableItem(IReadOnlyList<ReadinessFinding> findings, PlanDateMarkCandidate mark = null, bool hasDateColumn = true,
                                                    bool noDateTable = false, int compatibilityLevel = 0)
        {
            var date = findings.FirstOrDefault(x => string.Equals(x.RuleId, "DATE-MARK", StringComparison.OrdinalIgnoreCase));
            var calendar = findings.FirstOrDefault(x => string.Equals(x.RuleId, "CAL-TI-NO-CALENDAR", StringComparison.OrdinalIgnoreCase));
            if (date == null && calendar == null) return noDateTable ? NoDateTableItem(compatibilityLevel) : null;

            var why = new List<string>();
            if (date != null) why.Add(date.DisplayMessage ?? date.Message);
            if (calendar != null) why.Add(calendar.DisplayMessage ?? calendar.Message);
            // A model with a date column and no date table gets one from a tool; one with no date column says why on the row
            // (Kane, 13 Oct 2026: the assistant never tried a calculated date table).
            if (mark == null && date != null)
                why.Add(hasDateColumn
                    ? "A table holds a date column but no table is the date table, so your assistant adds a calculated date table (generate_date_table), which also marks it. One Undo takes it back."
                    : "No table in this model has a date column, so there is nothing to build a date table from. Add a date column at the source, or tell your assistant where the dates are.");

            var touches = new List<PlanTouch>();
            if (date != null && mark != null)
                touches.Add(Touch(mark.TableRef, mark.Table, "~ ", ": mark it as the date table, on " + mark.Column));
            else if (date != null)
                touches.Add(Touch(date.ObjectRef, date.ObjectName, "~ ", ": mark it as the date table"));
            // A calendar is a model-level thing, so it is filed under no table on purpose. Filing it under
            // whichever table the rule happened to point at would put a line on a Map block that does not
            // name that block.
            if (calendar != null)
                touches.Add(new PlanTouch { Table = null, Kind = "table", Line = "+ a calendar" });

            return new ModelPlanItem
            {
                Id = date != null ? "datetable/model" : "calendar/model",
                Section = ModelPlanSections.Structure,
                Title = date != null && mark != null ? "Mark " + mark.Table + " as the date table"
                      : date != null ? "Mark the date table" : "Define a calendar",
                Sub = date != null ? "year to date and last year numbers need it" : "the model has year to date style formulas but no calendar",
                Value = "year to date and last year work",
                Rules = RuleIdsOf(new[] { date, calendar }.Where(x => x != null)),
                Claims = ClaimsOf(findings.Where(x => string.Equals(x.RuleId, "DATE-MARK", StringComparison.OrdinalIgnoreCase)
                                                       || string.Equals(x.RuleId, "CAL-TI-NO-CALENDAR", StringComparison.OrdinalIgnoreCase))),
                Goal = PlanGoals.Ai,
                // Semanticus marks it when the model names the one table and column; otherwise the assistant picks one with the person's
                // words or the model's own DAX to go on, and the person reviews it (Kane, 8 Oct 2026).
                Who = date != null && mark != null ? ModelPlanOwners.Auto : ModelPlanOwners.Assistant,
                Status = ModelPlanStatuses.Ready,
                Why = why.Where(w => !string.IsNullOrWhiteSpace(w)).ToArray(),
                Diff = Diff(touches),
                Touches = date != null && mark != null ? mark.Table : date?.ObjectName ?? "the model",
                Tables = TableDetail(touches),
                Jump = new ModelPlanJump { Tool = "advmodels", Label = "Advanced modelling" },
            };
        }

        /// <summary>The model has a date column and no date table, and no rule fires because it uses no time intelligence DAX (DATE-MARK is
        /// dormant then, on purpose: the grade does not mark such a model down). The row is still the useful next step, so it comes from the
        /// model's facts: add a calculated date table (marked), and at 1701 a calendar with the usual hierarchy (below 1701 a generated table has no
        /// declared columns yet, so a hierarchy cannot be built on it).</summary>
        private static ModelPlanItem NoDateTableItem(int compatibilityLevel)
        {
            var calendar = compatibilityLevel >= 1701;
            var touches = new List<PlanTouch> { new PlanTouch { Table = null, Kind = "table", Line = "+ a date table" } };
            if (calendar) touches.Add(new PlanTouch { Table = null, Kind = "table", Line = "+ a calendar" });
            return new ModelPlanItem
            {
                Id = "datetable/model",
                Section = ModelPlanSections.Structure,
                Title = "Add a date table",
                Sub = "the model has dates but no date table",
                Value = "year to date and last year work",
                Rules = Array.Empty<string>(),
                Goal = PlanGoals.Ai,
                Who = ModelPlanOwners.Assistant,
                Status = ModelPlanStatuses.Ready,
                Why = new[]
                {
                    "A table holds a date column but no table is the date table.",
                    calendar
                        ? "Your assistant adds a calculated date table with a calendar, marks it, and adds the usual hierarchy (year, quarter, month, day). One Undo takes it back."
                        : "Your assistant adds a calculated date table and marks it. One Undo takes it back.",
                },
                Diff = Diff(touches),
                Touches = "the model",
                Tables = TableDetail(touches),
                Jump = new ModelPlanJump { Tool = "advmodels", Label = "Advanced modelling" },
            };
        }

        /// <summary>The start of a check row's first reason: which report shows the measure, by name when the reading names it.</summary>
        private static string ReportsShowing(PlanFacts f, string measureRef)
        {
            var names = f.ReportsByMeasure != null && f.ReportsByMeasure.TryGetValue(measureRef, out var n) ? n : Array.Empty<string>();
            return names.Length switch
            {
                0 => "A report shows",
                1 => "The report '" + names[0] + "' shows",
                2 => "The reports '" + names[0] + "' and '" + names[1] + "' show",
                _ => "The reports '" + names[0] + "', '" + names[1] + "' and " + (names.Length - 2) + " more show",
            };
        }

        private static ModelPlanItem RecheckItem(PlanFacts f)
        {
            if (f.ReportsNeedingChecking <= 0) return null;

            return new ModelPlanItem
            {
                Id = "recheck/reports",
                Section = ModelPlanSections.Trust,
                Title = "Look at " + CountOf(f.ReportsNeedingChecking, "report", "reports") + " again",
                Sub = Join(f.ReportsUnchecked, 3),
                Value = "you know what a change would break",
                // The reports are already chosen, so reading them again is one call (check_reports): the assistant's row, not the
                // person's (Kane, 13 Oct 2026: the person is the last resort). A published report needs the person's yes in chat, which
                // the assistant asks for. The engine does not read reports by itself, so this is not "auto" (F-199).
                Who = ModelPlanOwners.Assistant,
                Status = ModelPlanStatuses.Ready,
                Why = new[]
                {
                    "Semanticus has not looked at " + (f.ReportsNeedingChecking == 1 && f.ReportsUnchecked.Length == 1
                        ? "the report '" + f.ReportsUnchecked[0] + "'" : CountOf(f.ReportsNeedingChecking, "report", "reports"))
                        + " since the model last changed.",
                    "Until it does, what the plan says a change would break in " + (f.ReportsNeedingChecking == 1 ? "that report" : "those reports")
                        + " may be out of date.",
                    "Your assistant reads " + (f.ReportsNeedingChecking == 1 ? "it" : "them") + " again. A published report needs your yes in chat first.",
                    "To do it yourself, open Lineage and press Check reports or Check again.",
                },
                Diff = Array.Empty<string>(),
                Touches = Join(f.ReportsUnchecked, 3),
                Jump = new ModelPlanJump { Tool = "lineage", Label = "Lineage" },
            };
        }

        /// <summary>Everything the rules propose rather than fix, grouped into FAMILIES, where a family is one
        /// job a person recognises and not a rule-id prefix. PROPOSED rather than ready, because the rule has an
        /// opinion and the person has the answer.
        ///
        /// WHAT KANE SAW ON THE YOGA BUILD (23 September), because it is the whole reason this is shaped this
        /// way. The Date block on the Map carried a row called "Look at 24 suggestions". Its reasons were four
        /// copies of one rule's message with a different term swapped into each ("These visible objects all
        /// normalise to the Q&amp;A term 'date' ..." then the same for three more terms), and its "exactly what
        /// changes" lines read "~ Date: object names collide under Q&amp;A term matching", which is the finding
        /// restated, not a change. Both came from the same shortcut: Why was every finding's own sentence
        /// verbatim and each diff line was the rule's TITLE.
        ///
        /// A rule message is written to explain ONE finding to somebody looking at that finding. Twenty-four of
        /// them stacked up is not an explanation of the job; it is the same paragraph twenty-four times. So the
        /// FAMILY supplies the words and the findings supply only the count and the examples: a verb-led title
        /// with the count, one sentence saying what was found and why it matters, one compact line of up to
        /// four examples, and one sentence saying what fixing it earns. Three sentences whether there are two
        /// findings or two hundred.
        ///
        /// A DIFF IS A CHANGE OR IT IS NOTHING. A family whose change the engine can name states it once per
        /// case ("~ Date: rename it or hide it"). A family whose fix depends on a choice only the person can
        /// make (which end of a relationship, which rewrite of a formula) carries NO lines at all and says so
        /// in what it touches, because a finding sentence sitting under "exactly what changes" is a claim about
        /// what would happen that is not true.</summary>
        private static IEnumerable<ModelPlanItem> ProposalItems(IReadOnlyList<ReadinessFinding> findings,
                                                                ISet<string> starterColumns = null,
                                                                ISet<string> jevFormatted = null,
                                                                IReadOnlyDictionary<string, string[]> collisionNames = null) =>
            (findings ?? Array.Empty<ReadinessFinding>())
                .Where(IsProposalFinding)
                .Where(x => !ProposalsOwnedByAnotherItem.Contains(x.RuleId ?? string.Empty))
                // ONE OWNER PER COLUMN. A column the starter family owns gets its measure there (or its "none"),
                // so the old explicit-measures item drops it and goes away once nothing is left.
                .Where(x => !(string.Equals(x.RuleId, "DAC-IMPLICIT-MEASURE", StringComparison.OrdinalIgnoreCase)
                              && starterColumns != null && starterColumns.Contains(x.ObjectRef ?? string.Empty)))
                // The same for a format: an object a kept Jev line formats is that line's, so the rules' format row
                // drops it and goes away once nothing is left (N8).
                .Where(x => !((x.RuleId ?? string.Empty).StartsWith("FMT-", StringComparison.OrdinalIgnoreCase)
                              && jevFormatted != null && jevFormatted.Contains(x.ObjectRef ?? string.Empty)))
                .GroupBy(ProposalFamily, StringComparer.Ordinal)
                .OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g =>
                {
                    var words = ProposalWordsFor(g.Key);
                    var count = g.Count();

                    // One touch per finding whether or not there is a line to show. The per-table detail counts
                    // and files the work either way, which is what lets a Map block say how much of an item is
                    // its own even when the item deliberately shows no lines.
                    var touches = g.Select(x =>
                    {
                        var name = Qualified(x.ObjectRef, x.ObjectName);
                        var change = OldNameTermOnly(x) ? "remove its old name from its synonyms" : words.Change;
                        return new PlanTouch
                        {
                            Table = TableOfRef(x.ObjectRef),
                            Kind = KindOfRef(x.ObjectRef),
                            Name = name,
                            Line = change == null ? null : words.ChangePrefix + name + ": " + change,
                        };
                    }).ToArray();

                    var why = new List<string> { words.Lead(count) };
                    if (g.Key == "dates" || g.Key == "relationships")
                        why.AddRange(g.Select(x => x.DisplayMessage ?? x.Message).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.Ordinal).Take(4));
                    var examples = ProposalExamples(touches.Select(t => t.Name));
                    if (examples != null) why.Add(examples);
                    why.Add(words.Earns);

                    return new ModelPlanItem
                    {
                        Id = "proposal/" + g.Key,
                        Section = words.Section,
                        Title = words.Title(count),
                        Sub = Join(g.Select(x => x.ObjectName).Where(n => !string.IsNullOrWhiteSpace(n)), 4),
                        Value = words.Value,
                        Rules = RuleIdsOf(g),
                        Goal = GoalOfFindings(g),
                        Goals = BothGoals(g, null),
                        // AUTOMATION FIRST, THE PERSON LAST (Kane, 8 Oct 2026). Choosing a rename, a rewrite or a setting is work the
                        // assistant does with the person's words and the rule's reasons, so these rows are the assistant's. Only the Copilot
                        // limits stay the person's: they are a trade-off about the data, not an edit (docs: D4 table).
                        Who = PersonOnlyFamilies.Contains(g.Key) ? ModelPlanOwners.You : ModelPlanOwners.Assistant,
                        Status = ModelPlanStatuses.Proposed,
                        Why = why.ToArray(),
                        Diff = Diff(touches),
                        // The count stays at the front even on the honest "nothing changes yet" line: the Map
                        // reads it to check its own per-table counts add up to the row's total.
                        Touches = words.Change == null
                            ? Objects(count) + "; no change until you choose, the page shows each case"
                            : Objects(count),
                        Tables = TableDetail(touches, words.CaseOne, words.CaseMany),
                        // The row names what it covers, so its detail never says "Nothing named yet." over its cases (N9).
                        Objects = RefsOf(g),
                        Claims = ClaimsOf(g),
                        // A line only Model quality raises (its twin on AI readiness is not firing) can be accepted nowhere else, and the
                        // Diagram accepts nothing, so that row opens Model quality.
                        Jump = g.Any(x => PairedAcceptances.AirTwinOf(x.RuleId) != null)
                            ? new ModelPlanJump { Tool = "bpa", Label = "Model quality" }
                            : new ModelPlanJump { Tool = words.Tool, Label = words.Label, Find = g.Key == "collisions" ? CollidingNames(g, collisionNames) : null },
                    };
                });

        /// <summary>The proposal families only the person can do. Copilot limits (the fallback family of the scale rules too) are a
        /// choice about what the model holds, not an edit anyone can propose.</summary>
        private static readonly HashSet<string> PersonOnlyFamilies =
            new HashSet<string>(StringComparer.Ordinal);

        /// <summary>The names a collisions row carries for the assistant (ModelPlanJump.Find): every field of every clash it lists (a clash finding is filed
        /// under the clash's key; any other collision finding names its own object), each name once, at most <see cref="FindNameCap"/>.</summary>
        private static string[] CollidingNames(IEnumerable<ReadinessFinding> findings, IReadOnlyDictionary<string, string[]> clashes)
        {
            var names = new List<string>();
            foreach (var x in findings)
            {
                if (clashes != null && x.ObjectName != null && clashes.TryGetValue(x.ObjectName, out var members)) names.AddRange(members);
                else if (!string.IsNullOrWhiteSpace(x.ObjectName)) names.Add(x.ObjectName);
            }
            var distinct = names.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.Ordinal).Take(FindNameCap).ToArray();
            return distinct.Length == 0 ? null : distinct;
        }

        /// <summary>The most names a row carries: a long list is no help to a reader.</summary>
        internal const int FindNameCap = 40;

        /// <summary>The five DAX-rewrite rules whose fix is words the assistant writes (a better formula) and not a change the engine can
        /// make. They are AI content to the scorer, but they are not WORDS (the words row carries descriptions, names, synonyms and
        /// instructions), so they used to make no row at all. They join the formulas row, beside the proposals about formulas
        /// (round 1, 8 Oct 2026).</summary>
        internal static readonly HashSet<string> DaxRewriteRules = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "BP-DAX-IFERROR", "BP-DAX-IFERROR-DIV", "BP-DAX-IFERROR-SEARCH", "BP-DAX-ISBLANK-EQ", "BP-DAX-DIV-GUARD",
        };

        /// <summary>A finding the proposal rows carry: a proposal, or a DAX rewrite.</summary>
        internal static bool IsProposalFinding(ReadinessFinding x) =>
            x != null && (string.Equals(x.Fix, nameof(FixKind.Proposal), StringComparison.Ordinal)
                          || (string.Equals(x.Fix, nameof(FixKind.AiContent), StringComparison.Ordinal) && DaxRewriteRules.Contains(x.RuleId ?? string.Empty)));

        /// <summary>Proposal findings that already have a row of their own, so they must not also appear inside
        /// a family row. DATE-MARK is the whole of "Mark the date table" (DateTableItem), and before this it was
        /// counted twice: once as that row and once inside the catch-all.</summary>
        private static readonly HashSet<string> ProposalsOwnedByAnotherItem =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "DATE-MARK" };

        /// <summary>The words one proposal family speaks. Every word a person reads on the row comes from here,
        /// so adding a family is one entry and NO finding's own prose ever reaches the row.</summary>
        private sealed class ProposalWords
        {
            /// <summary>The verb-led row title, given the finding count. Never "look at" anything: a plan row
            /// names the job, and "look at 24 suggestions" is what the plan says when it has not worked out
            /// what the job is.</summary>
            public Func<int, string> Title;

            /// <summary>What the rules found and why it matters, said ONCE for the whole family.</summary>
            public Func<int, string> Lead;

            /// <summary>What finishing it earns, in one sentence.</summary>
            public string Earns;

            /// <summary>The short worth phrase on the row. Empty when there is nothing honest to say.</summary>
            public string Value = string.Empty;

            public string Section = ModelPlanSections.Trust;
            public string Tool = "readiness";
            public string Label = "AI readiness";

            /// <summary>The change this family makes to ONE object, e.g. "rename it or hide it". NULL when the
            /// engine cannot name a change until the person has chosen, and then the row shows no lines at
            /// all rather than printing the finding back as if it were a change.</summary>
            public string Change;

            /// <summary>The diff prefix the change takes: "+ " adds, "- " removes, "~ " alters.</summary>
            public string ChangePrefix = "~ ";

            /// <summary>What one of this family's cases is called, for the per-table phrase ("3 name collisions
            /// here: ..."). The generic "3 columns, 1 measure" is true of every family at once.</summary>
            public string CaseOne = "case";
            public string CaseMany = "cases";
        }

        /// <summary>A name clash that exists only through an old name term: its fix is clearing the old word from the
        /// field it is filed on, never a rename (the rule's message carries <see cref="ReadinessRuleSet.OldNameTermOnly"/>).</summary>
        private static bool OldNameTermOnly(ReadinessFinding f) =>
            string.Equals(f?.RuleId, "DAC-QNA-NAME-COLLISIONS", StringComparison.OrdinalIgnoreCase)
            && (f.Message ?? string.Empty).Contains(ReadinessRuleSet.OldNameTermOnly, StringComparison.Ordinal);

        /// <summary>Which job a finding belongs to, read off the RULE ID. Not off the readiness category: a
        /// category is a scoring bucket and mixes jobs, so a linguistic-schema name collision and a missing verified answer
        /// are both DataAgentConfig and they are not the same piece of work. That is exactly how twenty-four
        /// collisions ended up in a bucket called "other".</summary>
        private static string ProposalFamily(ReadinessFinding f)
        {
            var id = f?.RuleId ?? string.Empty;
            bool Starts(string prefix) => id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);

            // Collisions first: DAC-QNA-NAME-COLLISIONS would otherwise be filed with the AI settings, and it
            // is a renaming job, not a settings job.
            if (id.IndexOf("NAME-COLLISION", StringComparison.OrdinalIgnoreCase) >= 0
                || string.Equals(id, "NAME-DUP", StringComparison.OrdinalIgnoreCase)
                || string.Equals(id, "SYN-COLLIDE", StringComparison.OrdinalIgnoreCase)) return "collisions";
            if (id.IndexOf("SORTBY", StringComparison.OrdinalIgnoreCase) >= 0) return "sort-order";
            if (Starts("DESC-")) return "descriptions";
            if (Starts("NAME-")) return "names";
            if (Starts("CAT-")) return "categories";
            if (Starts("FMT-")) return "formats";
            if (Starts("VIS-")) return "visibility";
            if (Starts("REL-") || PairedAcceptances.AirTwinOf(id) != null) return "relationships";   // a best-practice-only twin rides the same row
            if (Starts("DATE-") || Starts("CAL-")) return "dates";
            if (Starts("BP-DAX-")) return "formulas";
            if (Starts("SYN-")) return "synonyms";
            if (Starts("LIMIT-")) return "limits";
            // Two DataAgentConfig rules that are their own job and not a setting: a column people total by
            // accident, and two measures that are the same calculation twice.
            if (string.Equals(id, "DAC-IMPLICIT-MEASURE", StringComparison.OrdinalIgnoreCase)) return "implicit-measures";
            if (Starts("MEAS-")) return "duplicate-measures";
            if (Starts("DAC-") || Starts("AISCHEMA-") || Starts("CFG-") || Starts("MODE-")) return "ai-settings";
            return "in-" + (f?.Category ?? "BestPractice");
        }

        /// <summary>The families, and the words each one speaks.</summary>
        private static readonly Dictionary<string, ProposalWords> ProposalFamilyWords =
            new Dictionary<string, ProposalWords>(StringComparer.Ordinal)
        {
            ["collisions"] = new ProposalWords
            {
                Title = n => "Rename or hide fields with " + CountOf(n, "name Copilot confuses", "names Copilot confuses"),
                Lead = n => CountOf(n, "word is", "words are") + " used by more than one visible field, and "
                          + "Copilot can only hold one meaning for each word.",
                Earns = "Renaming or hiding the duplicates lets every word mean one thing, so questions stop landing on the wrong field.",
                Value = "Copilot answers about the right thing",
                Section = ModelPlanSections.Meaning,
                Change = "rename it or hide it",
                CaseOne = "name collision", CaseMany = "name collisions",
            },
            ["names"] = new ProposalWords
            {
                Title = n => "Rename " + CountOf(n, "unclear thing", "unclear things"),
                Lead = n => CountOf(n, "name is", "names are") + " too general, hard to decode or full of modelling jargon, "
                          + "so nobody can tell from the name what is inside.",
                Earns = "Clear names let a reader, and an assistant, pick the right field without asking anyone.",
                Value = "readers understand them",
                Section = ModelPlanSections.Meaning,
                Change = "give it a clearer name",
                CaseOne = "unclear name", CaseMany = "unclear names",
            },
            ["descriptions"] = new ProposalWords
            {
                Title = n => "Add " + CountOf(n, "missing description", "missing descriptions"),
                Lead = n => CountOf(n, "visible thing has", "visible things have") + " no description, so a reader "
                          + "and an assistant both have to guess what it is for.",
                Earns = "A description puts the meaning in the field list, where the person choosing the field is looking.",
                Value = "assistants stop guessing",
                Section = ModelPlanSections.Meaning,
                Tool = "knowledge", Label = "Model notes",
                Change = "add a description", ChangePrefix = "+ ",
                CaseOne = "missing description", CaseMany = "missing descriptions",
            },
            ["synonyms"] = new ProposalWords
            {
                Title = n => "Sort out " + CountOf(n, "synonym", "synonyms"),
                Lead = n => CountOf(n, "synonym does", "synonyms do") + " not do what it looks like it does, so the "
                          + "words people actually type do not reach the field they mean.",
                Earns = "Sorting them out lets people ask in their own words and still get the right field.",
                Value = "people ask in their own words",
                Section = ModelPlanSections.Meaning,
                CaseOne = "synonym", CaseMany = "synonyms",
            },
            ["categories"] = new ProposalWords
            {
                Title = n => "Say what " + CountOf(n, "column holds", "columns hold"),
                Lead = n => CountOf(n, "column holds", "columns hold") + " something special, like a web address, "
                          + "an image or a place, and nothing on the model says so.",
                Earns = "Saying so is what makes maps, links and pictures work instead of showing raw text.",
                Value = "maps and links work",
                Section = ModelPlanSections.Meaning,
                Change = "say what it holds",
                CaseOne = "column", CaseMany = "columns",
            },
            ["sort-order"] = new ProposalWords
            {
                Title = n => "Set " + CountOf(n, "sort order", "sort orders"),
                Lead = n => CountOf(n, "text column sorts", "text columns sort") + " alphabetically, so months, "
                          + "sizes and grades come out in the wrong order everywhere they are used.",
                Earns = "Setting the sort order puts them in real order on every visual at once.",
                Value = "months sort properly",
                Section = ModelPlanSections.Numbers,
                Change = "set its sort order",
                CaseOne = "column", CaseMany = "columns",
            },
            ["formats"] = new ProposalWords
            {
                Title = n => "Set the format on " + CountOf(n, "field", "fields"),
                Lead = n => CountOf(n, "number or date field has", "number and date fields have") + " no format, "
                          + "so the same value reads differently depending on where it is shown.",
                Earns = "One format on the field means every report, and every answer, shows it the same way.",
                Value = "numbers read the same everywhere",
                Section = ModelPlanSections.Numbers,
                Change = "set its format",
                CaseOne = "field", CaseMany = "fields",
            },
            ["visibility"] = new ProposalWords
            {
                Title = n => "Hide " + CountOf(n, "technical field", "technical fields"),
                Lead = n => CountOf(n, "field is", "fields are") + " only there to make the model work, like a key, not something to report on. "
                          + (n == 1 ? "It is" : "They are") + " visible, so " + (n == 1 ? "it shows" : "they show") + " up in field lists and in Copilot answers.",
                Earns = "Hiding them leaves only the fields somebody should actually pick.",
                Value = "only real fields are offered",
                Section = ModelPlanSections.Structure,
                Change = "hide it",
                CaseOne = "visible technical field", CaseMany = "visible technical fields",
            },
            ["relationships"] = new ProposalWords
            {
                Title = n => "Fix " + CountOf(n, "relationship with a model warning", "relationships with model warnings"),
                Lead = n => CountOf(n, "relationship needs", "relationships need") + " a decision, because how two "
                          + "tables join decides what every number filtered through them means.",
                Earns = "Getting them right is what makes totals add up and what lets Copilot walk from one table to the next.",
                Value = "totals add up",
                Section = ModelPlanSections.Structure,
                Tool = "diagram", Label = "Diagram",
                CaseOne = "relationship", CaseMany = "relationships",
            },
            ["dates"] = new ProposalWords
            {
                Title = n => "Fix " + CountOf(n, "date setting with a model warning", "date settings with model warnings"),
                Lead = n => CountOf(n, "date setting needs", "date settings need") + " a choice. "
                          + "Date settings affect year to date, last year and running totals.",
                Earns = "Settling them is what makes time calculations return the period you asked for.",
                Value = "time calculations work",
                Section = ModelPlanSections.Structure,
                Tool = "advmodels", Label = "Advanced modelling",
                CaseOne = "date setting", CaseMany = "date settings",
            },
            ["formulas"] = new ProposalWords
            {
                Title = n => "Rewrite " + CountOf(n, "formula", "formulas"),
                Lead = n => CountOf(n, "formula uses", "formulas use") + " a pattern that is slow, or that swallows "
                          + "real errors and returns a number anyway.",
                Earns = "A rewrite makes them faster and lets a genuine problem show up instead of hiding.",
                Value = "faster, honest calculations",
                Section = ModelPlanSections.Trust,
                Tool = "query", Label = "Query",
                CaseOne = "formula", CaseMany = "formulas",
            },
            ["limits"] = new ProposalWords
            {
                Title = n => "Work around " + CountOf(n, "Copilot limit", "Copilot limits"),
                Lead = n => "This model runs into " + CountOf(n, "published limit", "published limits")
                          + " on what Copilot and data agents can take in at once.",
                Earns = "Your assistant trims or splits the part with the least use, without asking, and says what it did. Working inside the limits is what stops Copilot quietly ignoring part of the model.",
                Value = "Copilot sees the whole model",
                Section = ModelPlanSections.Trust,
                CaseOne = "limit", CaseMany = "limits",
            },
            ["implicit-measures"] = new ProposalWords
            {
                Title = n => "Add " + CountOf(n, "explicit measure", "explicit measures"),
                Lead = n => CountOf(n, "visible number column is", "visible number columns are") + " totalled by "
                          + "whatever drops it on a report, so the same question can come back with two answers.",
                Earns = "One written measure fixes the answer, and it is the thing Copilot will reach for.",
                Value = "one answer per question",
                Section = ModelPlanSections.Numbers,
                Tool = "query", Label = "Query",
                Change = "add an explicit measure for it", ChangePrefix = "+ ",
                CaseOne = "column totalled by accident", CaseMany = "columns totalled by accident",
            },
            ["duplicate-measures"] = new ProposalWords
            {
                Title = n => "Merge " + CountOf(n, "duplicate measure", "duplicate measures"),
                Lead = n => CountOf(n, "measure is", "measures are") + " the same calculation written more than "
                          + "once, so a fix to one leaves the other saying something different.",
                Earns = "Keeping one of each means a change lands everywhere it should, and nobody has to pick between two names for one number.",
                Value = "one name per number",
                Section = ModelPlanSections.Structure,
                CaseOne = "duplicate measure", CaseMany = "duplicate measures",
            },
            ["ai-settings"] = new ProposalWords
            {
                Title = n => "Set " + CountOf(n, "option for Copilot and data agents", "options for Copilot and data agents"),
                Lead = n => CountOf(n, "setting decides", "settings decide") + " what Copilot and data agents are "
                          + "allowed to read and how they answer, and " + (n == 1 ? "it is" : "they are") + " not decided yet.",
                Earns = "Deciding them is what makes an assistant answer from the fields you chose rather than everything it can see.",
                Value = "the assistant reads the right fields",
                Section = ModelPlanSections.Trust,
                CaseOne = "setting", CaseMany = "settings",
            },
        };

        /// <summary>The words for a family, falling back to the finding's readiness CATEGORY for a rule the
        /// list above does not know (a custom rule, or a built-in one added since). The fallback still leads
        /// with a verb and still says what it earns: the one thing a person must never be handed is a row
        /// telling them to go and look at something.</summary>
        private static ProposalWords ProposalWordsFor(string family)
        {
            ProposalWords known;
            if (ProposalFamilyWords.TryGetValue(family, out known)) return known;

            var category = family.StartsWith("in-", StringComparison.Ordinal) ? family.Substring(3) : family;
            var spoken = SpokenCategory(category);
            return new ProposalWords
            {
                Title = n => "Decide " + CountOf(n, "open question", "open questions") + " about " + spoken,
                Lead = n => CountOf(n, "issue about", "issues about") + " " + spoken + " "
                          + (n == 1 ? "needs" : "need") + " a choice that Semanticus cannot make for you.",
                Earns = "Deciding them clears the last of the " + spoken + " issues off the Plan.",
                Section = SectionForCategory(category),
                CaseOne = "open question", CaseMany = "open questions",
            };
        }

        /// <summary>A readiness category in plain words, for the fallback family's title and reasons.
        /// "DataAgentConfig" spaced and lower-cased reads "data Agent Config", which is a code word with a
        /// space in it and not something a person says.</summary>
        private static string SpokenCategory(string category) => category switch
        {
            "Naming" => "names",
            "Descriptions" => "descriptions",
            "Synonyms" => "synonyms",
            "Relationships" => "how the tables join",
            "Visibility" => "what is visible",
            "Formatting" => "formats",
            "CopilotLimits" => "Copilot limits",
            "DataAgentConfig" => "AI settings",
            "BestPractice" => "model quality",
            _ => Spaced(category ?? string.Empty).ToLowerInvariant(),
        };

        /// <summary>How many examples one family's reasons name before they start counting instead.</summary>
        private const int MaxProposalExamples = 4;

        /// <summary>Up to four examples on ONE line, then a count of the rest. This is the line that replaced
        /// twenty-four copies of a rule message, so it has to stay one sentence however many findings there
        /// are. Null when the findings name nothing, and the row then shows two sentences rather than an empty
        /// one.</summary>
        private static string ProposalExamples(IEnumerable<string> names)
        {
            var all = (names ?? Array.Empty<string>())
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (all.Length == 0) return null;
            if (all.Length <= MaxProposalExamples)
                return "Applies to: " + AndList(all) + ".";
            return "For example " + string.Join("; ", all.Take(MaxProposalExamples)) + "; and "
                 + (all.Length - MaxProposalExamples).ToString("N0", CultureInfo.InvariantCulture) + " more.";
        }

        /// <summary>"a, b and c", the way a person writes a short list.</summary>
        private static string AndList(IReadOnlyList<string> all) =>
            all.Count == 1
                ? all[0]
                : string.Join(", ", all.Take(all.Count - 1)) + " and " + all[all.Count - 1];

        /// <summary>A readiness category to one of the five goals. The mapping is a product decision, not a
        /// derivation, so it lives in one place and is readable as a list.</summary>
        private static string SectionForCategory(string category) => category switch
        {
            "Naming" => ModelPlanSections.Meaning,
            "Descriptions" => ModelPlanSections.Meaning,
            "Synonyms" => ModelPlanSections.Meaning,
            "Relationships" => ModelPlanSections.Structure,
            "Visibility" => ModelPlanSections.Structure,
            "Formatting" => ModelPlanSections.Numbers,
            "CopilotLimits" => ModelPlanSections.Trust,
            "DataAgentConfig" => ModelPlanSections.Trust,
            _ => ModelPlanSections.Trust,
        };

        // ---- worth -----------------------------------------------------------------------------------

        private static ModelPlanWorth Worth(PlanFacts f, IReadOnlyList<ModelPlanItem> items)
        {
            var lines = new List<string>();
            var open = items.Where(IsOpen).ToArray();

            var safeFixes = f.Readiness?.SafeFixCount ?? 0;
            if (safeFixes > 0) lines.Add(CountOf(safeFixes, "thing fixed", "things fixed") + " on their own");

            // The best-practice row and the words row are part of what the plan is worth (verify, 28 Sep, L5).
            var bpaFixes = open.Any(i => IsBpaFixItem(i.Id)) ? (f.BpaLines?.Count ?? 0) : 0;
            if (bpaFixes > 0) lines.Add(CountOf(bpaFixes, "best-practice issue", "best-practice issues") + " fixed");

            // Only a cleanup the plan counts on: an optional one is not progress toward anything.
            var cleanup = open.Any(i => i.Id == "cleanup/model" && !i.Optional) ? (f.Unused?.SafeCount ?? 0) : 0;
            if (cleanup > 0) lines.Add(CountOf(cleanup, "unused thing", "unused things") + " gone");

            var checks = open.Count(i => i.Id.StartsWith("check/", StringComparison.Ordinal));
            if (checks > 0) lines.Add(CountOf(checks, "measure", "measures") + " with a check");

            // The one words row carries the description findings (their rule ids are DESC-*), so "everything described" rests on it.
            if (open.Any(i => IsWordsItem(i.Id) && (i.Rules ?? Array.Empty<string>()).Any(r => r.StartsWith("DESC-", StringComparison.OrdinalIgnoreCase))))
                lines.Add("everything described");
            if (open.Any(i => IsWordsItem(i.Id))) lines.Add("the words from your assistant");

            var from = f.Readiness?.Grade;
            var to = f.ProjectedGrade;
            // The numbers travel with the letters and are null exactly when they are, so the page can never
            // draw a bar for a grade nobody worked out.
            int? fromScore = from == null ? null : (int)Math.Round(f.Readiness.Overall, MidpointRounding.AwayFromZero);
            int? toScore = f.ProjectionPending || to == null || f.ProjectedScore == null
                ? null : (int)Math.Round(f.ProjectedScore.Value, MidpointRounding.AwayFromZero);
            // Three different things, and the plan must not round any of them into another: the number is
            // known, the number is still being worked out, or the number could not be worked out at all.
            if (f.ProjectionPending)
                lines.Add("the grade the safe fixes would reach is still being worked out");
            else if (from != null && to == null)
                lines.Add("the grade after the fixes could not be worked out on this model, so it is not shown");

            return new ModelPlanWorth
            {
                From = from,
                To = to,
                FromScore = fromScore,
                ToScore = toScore,
                Lines = lines.ToArray(),
                Pending = f.ProjectionPending,
                // Only a real comparison of two known grades. While the second one is still being worked out
                // there is nothing to compare, and calling that a match would say "Stays B" about a number
                // nobody has.
                Unchanged = !f.ProjectionPending && from != null && to != null
                            && string.Equals(from, to, StringComparison.Ordinal),
            };
        }

        // ---- ranking ---------------------------------------------------------------------------------

        private sealed class PlanRanking
        {
            public IReadOnlyDictionary<string, (double Value, double Confidence)> Scores =
                new Dictionary<string, (double, double)>(StringComparer.Ordinal);
            public bool Ranked;
            public string OrderedBy;
            /// <summary>Set with the "plan is not active" sentence: where billing is, for the page's link.</summary>
            public string BillingUrl;
            /// <summary>A plan.rank ask is out in the background for this plan; the page shows Jev is looking.</summary>
            public bool Pending;
        }

        // THE PLAN NEVER WAITS FOR THE RANKING EITHER (T268 wiring). The value order is a plan.rank ask to the live
        // service, and it used to be awaited inside every compose, so get_model_plan waited on the network. Now:
        //
        //   STOPPED BEFORE IT LEAVES   not Pro, the point off, no service, no scope, no permission. These are
        //                              answered here and now by the service itself (it returns at its first gate
        //                              without sending anything), in its own words, and nothing is started. A
        //                              free session therefore never starts a run, not even to count.
        //   ADMITTED                   the ask runs in the background, one run per (session, revision, what is
        //                              asked), exactly like the Jev family runs. The read returns at once: in the
        //                              order last bought for THIS revision if there is one, otherwise in rule
        //                              order, with Pending set so the plan says Jev is looking. A model change
        //                              cancels a run about an older revision. When a run lands the page is told
        //                              (plan/didChange) and re-reads; the agent door sees the order on its next call.
        //
        // A run that landed is remembered for its key whatever it said, including a shrug, so a read that finds it
        // does not ask again: without that, "landed, re-read, ask again, land again" would loop. The key carries the
        // permission's generation and the decision service itself, so a fresh grant or a different service asks
        // afresh rather than reusing an answer bought under the old one.

        private sealed class PlanRankRun
        {
            public string Key;
            public string SessionId;
            public long Revision;
            public CancellationTokenSource Cancel = new CancellationTokenSource();
            public Task Task = Task.CompletedTask;
            public bool Done;
            public DecisionOutcome Outcome;
            public string Failure;
        }

        private readonly object _planRankGate = new object();
        private PlanRankRun _planRankRun;        // the newest run, in flight or landed
        private PlanRankRun _planRankLanded;     // the newest run that landed uncancelled: the last known order
        private int _planRankRunsStarted;
        private int _planRankTrackingAttached;
        private Action _planRankWanted;          // the newest ask a read wanted while a run for the same revision was still out (started when that run lands)
        private string _planRankWantedKey;

        /// <summary>How many background ranking runs this engine has started. A free session must leave it at zero.</summary>
        internal int PlanRankRunsStartedForTest => Volatile.Read(ref _planRankRunsStarted);

        /// <summary>Wait until no ranking run is in flight. Tests only; a real caller never waits for the ranking.</summary>
        internal async Task PlanRankSettledForTest()
        {
            for (var i = 0; i < 100; i++)
            {
                PlanRankRun run;
                lock (_planRankGate) run = _planRankRun;
                if (run == null || run.Done) return;
                try { await run.Task.ConfigureAwait(false); } catch { }
            }
        }

        /// <summary>The plan's value order. <paramref name="jevSettling"/> is set while the families are still working out
        /// this revision's Jev rows: the list is about to change, so no ranking run is started for it (the last order
        /// bought for this revision, or rule order, stands meanwhile). Asked then, the ranking was of the rows without
        /// Jev's, and the read after Jev landed cancelled it (charged, and logged as failed) or bought a second order.
        /// After every restart that was one wasted ask per open (ai.schema verifier, round 4). The families' landing
        /// tells the page, and the read it makes starts the one ranking for the settled list.</summary>
        private async Task<PlanRanking> RankAsync(Session s, PlanFacts f, IReadOnlyList<ModelPlanItem> open, bool jevSettling = false)
        {
            var point = DecisionRegistry.Require(DecisionRegistry.PlanRank);

            if (open.Count == 0)
                return new PlanRanking { OrderedBy = "Nothing is open on the plan." };

            var scored = open.Take(PlanRankPoint.MaxScoredItems).ToList();
            var decisions = Decisions;
            var isPro = Entitlement.FeatureGrants.IsProTier(_entitlement);
            var enabled = DecisionPointEnabled(point.Name);
            var fence = new DecisionSessionFence(s.Id, s.Revision, () =>
            {
                var current = _sessions.Current;
                return (current == null ? null : current.Id, current == null ? -1L : current.Revision);
            });

            // The service's own first gates, read without asking. Anything that stops the ask before it could leave
            // the machine is answered in the foreground by the service itself, which returns at that gate.
            string scope = null;
            long generation = -1;
            var admitted = false;
            try
            {
                if (enabled && isPro && decisions.Oracle.Available)
                {
                    scope = decisions.ReadScope == null ? null : await decisions.ReadScope().ConfigureAwait(false);
                    if (!string.IsNullOrEmpty(scope))
                    {
                        var consent = decisions.ReadConsent(scope);
                        if (consent.Covers(point, DecisionRegistry.ConsentSchemaVersion, out _))
                        {
                            admitted = true;
                            generation = consent.Generation;
                        }
                    }
                }
            }
            catch { admitted = false; }

            if (!admitted)
            {
                DecisionOutcome refused;
                try
                {
                    refused = await decisions.AskAsync(point.Name,
                        PlanRankPoint.Snapshot(f.ModelName, f.Tables_, f.Columns_, f.Measures_, f.Connected, scored),
                        PlanRankPoint.Questions(scored), isPro, enabled, fence, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex) { return RankingFrom(null, ex.Message, open, point); }
                return RankingFrom(refused, null, open, point);
            }

            var key = string.Join("\n", s.Id, s.Revision.ToString(CultureInfo.InvariantCulture), scope,
                generation.ToString(CultureInfo.InvariantCulture),
                System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(decisions).ToString(CultureInfo.InvariantCulture),
                string.Join("\u001f", scored.Select(i => i.Id)));

            PlanRankRun landed = null, lastKnown = null;
            lock (_planRankGate)
            {
                var run = _planRankRun;
                if (run != null && string.Equals(run.Key, key, StringComparison.Ordinal))
                {
                    if (run.Done) landed = run;
                }
                else if (!jevSettling)
                {
                    var snapshot = PlanRankPoint.Snapshot(f.ModelName, f.Tables_, f.Columns_, f.Measures_, f.Connected, scored);
                    var questions = PlanRankPoint.Questions(scored);
                    if (run != null && !run.Done && !run.Cancel.IsCancellationRequested
                        && string.Equals(run.SessionId, s.Id, StringComparison.Ordinal) && run.Revision == s.Revision)
                    {
                        // A different list of rows (a Jev line landed, a row was skipped) for the SAME model revision. The ask in flight is
                        // already charged, so cancelling it buys nothing: it was ~19 percent of a live run's tokens (15 Oct). Let it land,
                        // then ask once for the newest list. Reads in between only replace what is wanted.
                        _planRankWantedKey = key;
                        _planRankWanted = () => StartPlanRankRunLocked(key, s, decisions, point, isPro, enabled, fence, snapshot, questions);
                    }
                    else
                    {
                        _planRankWanted = null; _planRankWantedKey = null;
                        StartPlanRankRunLocked(key, s, decisions, point, isPro, enabled, fence, snapshot, questions);
                    }
                }
                if (landed == null)
                {
                    var last = _planRankLanded;
                    if (last != null && string.Equals(last.SessionId, s.Id, StringComparison.Ordinal) && last.Revision == s.Revision)
                        lastKnown = last;
                }
            }

            if (landed != null) return RankingFrom(landed.Outcome, landed.Failure, open, point);

            // Still out. The last order bought for this revision stands meanwhile; with none, rule order.
            var meanwhile = lastKnown != null
                ? RankingFrom(lastKnown.Outcome, lastKnown.Failure, open, point)
                : new PlanRanking { OrderedBy = "In the order Semanticus found the tasks, while Jev works out which matter most." };
            meanwhile.Pending = true;
            return meanwhile;
        }

        /// <summary>Start the background ask for one key, cancelling the run before it. Caller holds
        /// <see cref="_planRankGate"/>. The request is built by the caller, from the items as they were read.</summary>
        private void StartPlanRankRunLocked(string key, Session s, DecisionService decisions, DecisionPoint point, bool isPro,
            bool enabled, DecisionSessionFence fence, IReadOnlyDictionary<string, object> snapshot, IReadOnlyList<DecisionQuestion> questions)
        {
            var old = _planRankRun;
            if (old != null && !old.Done) { try { old.Cancel.Cancel(); } catch { } }
            var run = new PlanRankRun { Key = key, SessionId = s.Id, Revision = s.Revision };
            _planRankRun = run;
            Interlocked.Increment(ref _planRankRunsStarted);
            AttachPlanRankTracking();
            var context = _sessions.CurrentContext;

            run.Task = Task.Run(async () =>
            {
                try
                {
                    run.Outcome = await decisions.AskAsync(point.Name, snapshot, questions, isPro, enabled, fence, run.Cancel.Token)
                        .ConfigureAwait(false);
                }
                catch (Exception ex) { run.Failure = ex.Message; }

                bool current, cancelled;
                lock (_planRankGate)
                {
                    cancelled = run.Cancel.IsCancellationRequested;
                    run.Done = true;
                    current = ReferenceEquals(_planRankRun, run);
                    // A cancelled run is about a model that moved: it decided nothing, so it is forgotten and the
                    // next read asks for the model that is open now.
                    if (cancelled) { if (current) { _planRankRun = null; _planRankWanted = null; _planRankWantedKey = null; } }
                    else _planRankLanded = run;

                    // A read asked about a newer list while this one was out: ask once for it now, if the model has not moved on.
                    if (current && !cancelled && _planRankWanted != null)
                    {
                        var next = _planRankWanted;
                        var nextKey = _planRankWantedKey;
                        _planRankWanted = null; _planRankWantedKey = null;
                        var live = _sessions.Current;
                        if (!string.Equals(nextKey, run.Key, StringComparison.Ordinal) && live != null
                            && string.Equals(live.Id, run.SessionId, StringComparison.Ordinal) && live.Revision == run.Revision)
                        {
                            next();
                            return;   // the new run tells the page when it lands; this one's order already stands for the rows it covered
                        }
                    }
                }

                // Tell the page once, whatever it said: "Jev is looking" has to go away either way.
                if (current && !cancelled)
                {
                    try
                    {
                        if (ReferenceEquals(_sessions.Current, s) && ReferenceEquals(_sessions.CurrentContext, context))
                            PublishPlan(context?.Plans.Current, s.Revision);
                    }
                    catch { }
                }
            });
        }

        /// <summary>A model change cancels a ranking run about an older revision. It does not start a new one: the
        /// ranking needs the plan's items, so the next read (which the page makes on the change) asks for them.</summary>
        private void AttachPlanRankTracking()
        {
            if (Interlocked.Exchange(ref _planRankTrackingAttached, 1) != 0) return;
            _sessions.Bus.Changed += n =>
            {
                try
                {
                    var current = _sessions.Current;
                    lock (_planRankGate)
                    {
                        var run = _planRankRun;
                        if (run == null || run.Done) return;
                        if (current != null && string.Equals(run.SessionId, current.Id, StringComparison.Ordinal) && run.Revision == current.Revision) return;
                        try { run.Cancel.Cancel(); } catch { }
                    }
                }
                catch { }
            };
        }

        /// <summary>Turn one plan.rank outcome into the plan's order. The same words and the same numbers whether
        /// the outcome came back at once (a gate stopped it) or from a background run.</summary>
        private static PlanRanking RankingFrom(DecisionOutcome outcome, string failure, IReadOnlyList<ModelPlanItem> open, DecisionPoint point)
        {
            var ranking = new PlanRanking();
            // Every line below is ONE plain sentence that says Jev did not order the tasks this time, why in the person's words, and that the
            // tasks are in the order Semanticus found them. No exception text, no count of what Jev did not look at, no promise to try again.
            // Jev unreachable, timed out, down or refusing without a reason the person can act on: the same line from one place, read by both doors.
            if (JevDecisionOracle.IsNotAvailable(failure) || (outcome != null && outcome.Asked && !outcome.Answers.Decided
                                                              && JevDecisionOracle.IsNotAvailable(outcome.Answers.NotDecidedReason)))
            {
                ranking.OrderedBy = NotAvailableOrder;
                return ranking;
            }
            // The Pro service did not accept the subscription: said once, with the billing address for the page's link.
            if (JevDecisionOracle.IsSubscriptionInactive(failure) || (outcome != null && outcome.Asked && !outcome.Answers.Decided
                                                                      && JevDecisionOracle.IsSubscriptionInactive(outcome.Answers.NotDecidedReason)))
            {
                ranking.OrderedBy = PlanInactiveOrder;
                ranking.BillingUrl = Entitlement.LicenseEntitlement.ManageUrl;
                return ranking;
            }
            if (failure != null || outcome == null)
            {
                // An ordering that could not be bought is not a failed plan: the items, the reasons and the exact changes are all already
                // computed, only the sort is missing, and the order found is a real order. The exception's own text is not for a person.
                ranking.OrderedBy = "Jev could not put the tasks in order this time, so they are in the order Semanticus found them.";
                return ranking;
            }

            if (!outcome.Asked)
            {
                ranking.OrderedBy = NotOrderedBecause(outcome.SkippedReason);
                return ranking;
            }

            if (!outcome.Answers.Decided)
            {
                var why = outcome.Answers.NotDecidedReason;
                // Asked, no answer, and the budget forbids another try: the reason is already a whole thought, so it is said short.
                ranking.OrderedBy = string.Equals(why, JevDecisionOracle.AskedThenStoppedReason, StringComparison.Ordinal)
                    ? "Jev did not answer this time and this session has used its suggestion budget, so the tasks are in the order Semanticus found them."
                    : NotOrderedBecause(PlainNotDecided(why));
                return ranking;
            }

            var scores = PlanRankPoint.Scores(outcome.Answers, point.ConfidenceBar);
            if (scores.Count == 0)
            {
                ranking.OrderedBy = "Jev gave no answer that could reorder the tasks, so they are in the order Semanticus found them.";
                return ranking;
            }

            foreach (var item in open)
            {
                (double Value, double Confidence) score;
                // The place is Jev's expected value whatever it was sure of; Scores zeroes the confidence under the bar, so no number shows.
                if (scores.TryGetValue(item.Id, out score)) item.Confidence = score.Confidence;
            }

            ranking.Scores = scores;
            ranking.Ranked = true;
            ranking.OrderedBy = open.All(i => scores.ContainsKey(i.Id))
                ? "Tasks with the greatest expected benefit come first."
                : "Tasks with advice come first, ordered by expected benefit. The rest follow in the order Semanticus found them.";
            return ranking;
        }

        /// <summary>What the Plan's order line says when the last ask failed in the class nobody can act on
        /// (<see cref="JevDecisionOracle.IsNotAvailable"/>). The one place the page and the assistant read it.</summary>
        internal const string NotAvailableOrder = "Jev could not be reached this time, so the tasks are in the order Semanticus found them.";

        /// <summary>What the order line says when the relay refused an inactive subscription (the page adds a Billing link beside it).</summary>
        internal const string PlanInactiveOrder = "Jev did not put these in order because your Pro plan is not active. Open billing to fix it.";

        /// <summary>One sentence for a reason Jev did not order the tasks: the reason's first sentence (the relay writes two, and a second one
        /// would not read after "because"), lower-cased so Jev keeps its capital, with no full stop of its own.</summary>
        private static string NotOrderedBecause(string reason)
        {
            var r = (reason ?? "").Trim();
            var stop = r.IndexOf(". ", StringComparison.Ordinal);
            if (stop > 0) r = r.Substring(0, stop);
            r = r.TrimEnd('.', '!', '?', ' ');
            if (r.Length == 0) r = "no answer came back";
            return "Jev did not put the tasks in order this time because " + Lower(r) + ", so they are in the order Semanticus found them.";
        }

        /// <summary>A reason an ask was not decided, in the words the Plan shows a person. The gates already speak
        /// plainly (no permission, no allowance left, the relay's own sentences) and pass through. The reader's refusals
        /// name question ids and wire fields ("the answer to 'value:cleanup/model' was rejected: the legend's rung
        /// '0' ..."), and a refused request is a status number, so those become one plain sentence each; the exact
        /// reason is still in the engine's log line for the ask.</summary>
        internal static string PlainNotDecided(string reason)
        {
            var r = reason ?? "";
            if (r.StartsWith("the reply ", StringComparison.Ordinal) || r.StartsWith("the answer to '", StringComparison.Ordinal))
                return "the answer from the advice service did not fit what was asked";
            if (System.Text.RegularExpressions.Regex.IsMatch(r, @"^the advice service answered \d+$"))
                return "the advice service turned the request down";
            if (r.StartsWith("The snapshot is ", StringComparison.Ordinal))
                return "this plan is too large to send for advice in one request";
            return string.IsNullOrWhiteSpace(r) ? "no answer came back" : Lower(r);
        }

        /// <summary>Sort. Open items first, by score where there is one, then by section, then by id so the order
        /// is total and a refresh that changes nothing changes nothing on screen. Skipped items sink below the
        /// open ones but stay on the plan, which is the whole point of a skip.
        ///
        /// AN ITEM JEV GAVE NO ANSWER FOR sorts at PlanRankPoint.UnscoredValue (between "none" and "small") and keeps the
        /// rules' order among the others there. An item Jev expects to be worth nothing therefore comes after the ones it
        /// could not score, so it never leads them (verifier round 2, D8: an unscored item used to score -1, below "none",
        /// and a sure "none" jumped above every item Jev could not score). The place is Jev's expected value, whatever it was
        /// sure of (m11/jev-batch R1).</summary>
        private static List<ModelPlanItem> Order(
            IReadOnlyList<ModelPlanItem> items, IReadOnlyDictionary<string, (double Value, double Confidence)> scores)
        {
            (double Value, double Confidence) found;
            double ScoreOf(ModelPlanItem i) => scores != null && scores.TryGetValue(i.Id, out found) ? found.Value : PlanRankPoint.UnscoredValue;

            // An OPTIONAL row is never first (the cleanup, always): it sits below
            // every other open row, whatever it scored, and above the rows set aside.
            return items
                .OrderBy(i => string.Equals(i.Status, ModelPlanStatuses.Skipped, StringComparison.Ordinal) ? 2 : i.Optional ? 1 : 0)
                .ThenByDescending(ScoreOf)
                // On a tie the person's rows come after the automated ones: plan order is also the order a loop between two rows is broken
                // in (PlanFootprint.Waits), so a row only the person can do never wins a tie against a row Semanticus or the assistant does.
                .ThenBy(i => string.Equals(i.Doer, PlanDoers.You, StringComparison.Ordinal) ? 1 : 0)
                .ThenBy(i => ModelPlanSections.Rank(i.Section))
                .ThenBy(i => i.Id, StringComparer.Ordinal)
                .ToList();
        }

        // ---- the box ---------------------------------------------------------------------------------

        /// <summary>The box, in three passes, in this order and no other.
        ///
        ///   THE STATE. A handful of fixed phrasings the engine answers from its own numbers. Free of every
        ///   cost there is: no service, no permission, nothing off the machine.
        ///   THE WORDS. The plain nouns and verbs each page is about (AskRoutePoint.MatchWords). Also free,
        ///   also local, and it is where nearly every real ask now lands.
        ///   THE SERVICE. Only what the first two could not place, and only with Pro and permission.
        ///
        /// The words pass is new, and it is the fix for what Kane hit on 21 September: he typed "create a
        /// measure" on a session with no permission and read back "You have not given permission to send this
        /// model's details for advice." Everything that was not one of the fixed phrasings went straight to
        /// the service, so with the service gated the gate's own sentence WAS the answer. It is not an answer.
        /// The gate reason still travels, in Reason, where the assistant door and the logs can see it; the
        /// sentence a person reads is either a place or AskRoutePoint.NoMatchSentence.
        ///
        /// A DATA QUESTION LANDS BY ITS WORDS OR BY THE SERVICE, AND IT IS ALWAYS OFFERED BESIDE THE TASK READING
        /// (verify-askdata-r3, r4 V1, then the coordinator's ruling of 28 September). The words pass places a number
        /// split by something on Ask your data (AskRoutePoint.DataQuestionMatch) with no permission, and the service
        /// may place what the words could not. Where Ask your data is not in this session's tier the same question
        /// goes to Queries with a quiet Pro line. Two things follow:
        ///   A line that nothing placed is not sent to Ask your data because it names one of the model's fields. That
        ///   fallback sent nearly every task typed in the Plan's field there ("Give Net Sales a currency format"), since
        ///   nearly every task names a field; it now reads the no-match line, and the Plan's field shows nothing.
        ///   A line Ask your data DOES place comes back with BOTH readings (AskRoutePoint.Readings): the task, which
        ///   Enter keeps as a row whatever else is named, and the question. Nothing is hidden by a word list any more,
        ///   because five rounds proved one cannot tell a task from a question and it failed in both directions. The
        ///   question is FIRST only where the line plainly asks (AskRoutePoint.AsksAQuestion); what placed it never
        ///   decides the order (verify-askdata-r6 F3).
        /// So a question that only names a field ("what were sales last month?") lands only once the service places it.
        ///
        /// THE WHOLE OPERATION IS PRO (Kane, 22 September: ask is Jev, so it is Pro). The refusal is the
        /// ordinary entitlement refusal every other Pro operation throws, and it is thrown FIRST, so a free
        /// session never reaches the word matching or the service. The page does not rely on it: askbox.tsx
        /// draws the quiet Pro pill instead of an input on a free session, so the throw is what a stale page
        /// or the assistant door meets, not something a person is shown.</summary>
        public async Task<AskRouteResult> RouteAskAsync(string text)
        {
            RequireProFeature();

            var point = DecisionRegistry.Require(DecisionRegistry.AskRoute);
            var result = new AskRouteResult { ConfidenceBar = point.ConfidenceBar, Fallback = "menu" };

            if (string.IsNullOrWhiteSpace(text))
            {
                result.Sentence = "Type what you are trying to do and this will find the place for it.";
                result.Reason = result.Sentence;
                return result;
            }

            if (text.Length > AskRoutePoint.MaxAskChars)
            {
                result.Sentence = "That is longer than this box takes ("
                    + AskRoutePoint.MaxAskChars.ToString(CultureInfo.InvariantCulture)
                    + " characters), so nothing was sent. Say it in a sentence.";
                result.Reason = result.Sentence;
                return result;
            }

            // The engine's own state first, and free. A question it can answer from its own numbers is answered
            // from them, with no service involved and nothing leaving the machine.
            var known = AskRoutePoint.AnswerFromState(text, await DeterministicFactsAsync().ConfigureAwait(false));
            if (known != null)
            {
                known.ConfidenceBar = point.ConfidenceBar;
                return known;
            }

            // A request the words cannot settle either way ("get my warehouse into a semantic model": a model, a source and
            // something that sounds like making one, but no main verb the rules know) is the one thing asked of the service
            // before the words, and once: the same call carries the place question, so an ask that turns out not to be a request
            // for a new model is not asked a second time. With no service the answer is no, which is today's routing.
            DecisionOutcome outcome = null;
            Exception serviceError = null;
            if (AskMakeIntent.Classify(text) == MakeIntent.Unsure)
            {
                try { outcome = await AskTheServiceAsync(point, text, withIntent: true).ConfigureAwait(false); }
                catch (Exception ex) { serviceError = ex; }
                if (outcome != null && outcome.Asked && outcome.Answers.Decided
                    && AskRoutePoint.ProposedNewModel(outcome.Answers, point.ConfidenceBar, out var intentConfidence))
                {
                    result.Tool = AskRoutePoint.ModelSpecTool;
                    result.Area = AskRouteCatalog.AreaOf(AskRoutePoint.ModelSpecTool);
                    result.Confidence = intentConfidence;
                    result.Fallback = null;
                    result.Asked = true;
                    result.ServedModelVersion = outcome.Answers.ServedModelVersion;
                    return result;
                }
            }

            // Then the words. A clear winner here is a complete answer and the service is never called.
            var askData = Entitlement.FeatureGrants.Grants(_entitlement, Entitlement.ProFeature.AskData);
            // The whole words pass is one call (AskRoutePoint.PlaceByWords), so the labelled sets can be measured
            // against the very code this runs. Where it places a line on Ask your data it comes back with both
            // readings and the person picks; neither is hidden (fix-askdata-r6).
            var byWords = AskRoutePoint.PlaceByWords(result, text, askData);
            if (byWords != null) return byWords;

            // Neither the words nor the service placed the line: the one no-match sentence, and why it went no further
            // stays in Reason. Confidence stays whatever the service said, or 0 when it was not asked.
            AskRouteResult Nowhere(string reason)
            {
                result.Sentence = AskRoutePoint.NoMatchSentence;
                result.Reason = reason;
                return result;
            }

            if (outcome == null && serviceError == null)
            {
                try { outcome = await AskTheServiceAsync(point, text, withIntent: false).ConfigureAwait(false); }
                catch (Exception ex) { serviceError = ex; }
            }
            if (serviceError != null)
            {
                return Nowhere("The box could not work out where to send you: " + Lower(serviceError.Message));
            }

            if (!outcome.Asked)
            {
                // The gate's words go in Reason and stay there. This is the exact path that put a consent
                // refusal in front of Kane as though it were an answer to "create a measure".
                return Nowhere(Capital(outcome.SkippedReason) + ".");
            }

            result.Asked = true;
            result.ServedModelVersion = outcome.Answers.ServedModelVersion;

            if (!outcome.Answers.Decided)
            {
                return Nowhere(Capital(outcome.Answers.NotDecidedReason) + ".");
            }

            double confidence;
            var tool = AskRoutePoint.ProposedTool(outcome.Answers, point.ConfidenceBar, out confidence);
            result.Confidence = confidence;

            if (tool == null)
            {
                return Nowhere("Nothing here matched that well enough to send you anywhere, so here is everything.");
            }

            // The service's Ask your data is read the same way the words' is: both readings are named, the question
            // is first only where the line plainly asks, and the person picks; it goes to the same Query page
            // where the tier has no question box. The question reading keeps the SERVICE'S OWN confidence, never a fixed 1, wherever it
            // sits, so a task read first carries the service's number on 'other'.
            if (string.Equals(tool, AskRouteCatalog.DataTool, StringComparison.Ordinal))
                return AskRoutePoint.Readings(result, text, askData, questionConfidence: confidence);

            // The service chose among the word groups; a person and the assistant are sent to the PAGE (Query for both of
            // the Query groups).
            result.Tool = AskRouteCatalog.PageOf(tool);
            result.Area = AskRouteCatalog.AreaOf(result.Tool);
            result.Fallback = null;
            return result;
        }

        /// <summary>The one call the box makes to the decision service: which place, and, for an ask the words could not settle,
        /// whether it asks for a new model (AskRoutePoint.IntentQuestions). Both questions travel in the same request.</summary>
        private Task<DecisionOutcome> AskTheServiceAsync(DecisionPoint point, string text, bool withIntent)
        {
            var session = _sessions.Current;
            var fence = session == null ? null : new DecisionSessionFence(session.Id, session.Revision, () =>
            {
                var current = _sessions.Current;
                return (current == null ? null : current.Id, current == null ? -1L : current.Revision);
            });
            var questions = withIntent ? AskRoutePoint.Questions().Concat(AskRoutePoint.IntentQuestions()).ToList() : AskRoutePoint.Questions();
            return Decisions.AskAsync(
                point.Name, AskRoutePoint.Snapshot(text), questions,
                isPro: Entitlement.FeatureGrants.IsProTier(_entitlement),
                enabled: DecisionPointEnabled(point.Name),
                fence: fence,
                ct: CancellationToken.None);
        }

        /// <summary>The counts the box can answer a question from. Cheap: one session read and one sidecar read,
        /// and every field that cannot be known on this session stays null.</summary>
        private async Task<AskDeterministicFacts> DeterministicFactsAsync()
        {
            var facts = new AskDeterministicFacts();
            try
            {
                var info = await SessionInfoAsync().ConfigureAwait(false);
                facts.ModelName = info?.ModelName;
                facts.TableCount = info?.Tables;
                facts.MeasureCount = info?.Measures;
                facts.UnsavedChangeCount = info?.HasUnsavedChanges == true ? 1 : 0;
            }
            catch { /* no session: every count stays null and the sentence says it does not know */ }

            // Saved checks are free on every tier since 22 September, so the count is read for everybody. A
            // read that fails still leaves the count null, which the sentence reports as not known rather
            // than as none.
            try
            {
                var suite = await ListTestDefinitionsCoreAsync().ConfigureAwait(false);
                facts.SavedCheckCount = suite?.Definitions?.Count(d => d.Enabled) ?? 0;
            }
            catch { }

            return facts;
        }

        // ---- words -----------------------------------------------------------------------------------

        private static string[] Diff(IEnumerable<PlanTouch> touches) =>
            Diff((touches ?? Array.Empty<PlanTouch>()).Select(t => t?.Line));

        private static string[] Diff(IEnumerable<string> lines)
        {
            var all = lines.Where(l => !string.IsNullOrWhiteSpace(l)).Distinct(StringComparer.Ordinal).ToArray();
            if (all.Length <= MaxDiffLines) return all;
            return all.Take(MaxDiffLines)
                      .Concat(new[] { "  and " + (all.Length - MaxDiffLines).ToString("N0", CultureInfo.InvariantCulture) + " more" })
                      .ToArray();
        }

        // ---- what an item touches, per table ---------------------------------------------------------

        /// <summary>One object an item acts on: the table it lives in, what kind of thing it is, and the one
        /// exact-change line that describes it.
        ///
        /// An item's DIFF and its PER-TABLE DETAIL are built from the same list of these, which is the only
        /// arrangement in which the two cannot drift. The alternative, filing finished diff lines under
        /// tables by reading the prose back, is guesswork that fails silently: the lines are sentences
        /// written for a person ("~ Sales[City]: geographic column has no data category"), not a
        /// machine-readable list.</summary>
        private sealed class PlanTouch
        {
            /// <summary>The owning table, or null when the object belongs to none (a relationship, a role,
            /// the model itself). A touch with no table is still counted in the item's total; it is simply
            /// filed under nothing, rather than under a guess.</summary>
            public string Table;

            /// <summary>The object's kind, for the per-table phrase: "column", "measure", "table" and so on.</summary>
            public string Kind;

            /// <summary>The exact-change line, prefix and all. Null when the item shows no lines (the capped
            /// "more measures need a check" row counts what is left rather than listing it, and a proposal
            /// family whose fix is the person's choice shows no lines at all).</summary>
            public string Line;

            /// <summary>The object written the way a person writes it, "Sales[Amount]". Carried separately
            /// from <see cref="Line"/> because a proposal row names examples in its reasons and cases in its
            /// per-table phrase even when it has no change line to show. Reading the name back out of the
            /// line would be the prose-parsing this class exists to avoid.</summary>
            public string Name;
        }

        /// <summary>One touch per finding, with the line built from a table-qualified object name so that
        /// every line names the table it will be filed under.</summary>
        private static PlanTouch[] Touches(
            IEnumerable<ReadinessFinding> findings, string prefix, Func<ReadinessFinding, string> tail) =>
            (findings ?? Array.Empty<ReadinessFinding>())
                .Where(x => x != null)
                .Select(x => Touch(x.ObjectRef, x.ObjectName, prefix, tail(x)))
                .ToArray();

        private static PlanTouch Touch(string objectRef, string fallbackName, string prefix, string tail) =>
            new PlanTouch
            {
                Table = TableOfRef(objectRef),
                Kind = KindOfRef(objectRef),
                Line = prefix + Qualified(objectRef, fallbackName) + tail,
            };

        private static string CheckLine(string measureRef, string name) =>
            "+ check: " + Qualified(measureRef, name);

        private static PlanTouch CheckTouch(string measureRef, string name) => new PlanTouch
        {
            Table = TableOfRef(measureRef),
            Kind = KindOfRef(measureRef),
            Line = CheckLine(measureRef, name),
        };

        /// <summary>The per-table detail for one item: which tables it lands in, how much of it lands in
        /// each, in the plan's words, with that table's own change lines.
        ///
        /// COUNT counts touches, not distinct objects, because the item's own total counts touches too (two
        /// rules on one column are two things to fix). That is what makes the counts across an item's tables
        /// sum to its total whenever every object it touches has a table. DIFF is de-duplicated, so it can be
        /// shorter than Count, and it is never capped: the item's list is capped so a person can read it, and
        /// one table's slice of it does not need to be.</summary>
        private static ModelPlanItemTable[] TableDetail(IEnumerable<PlanTouch> touches) =>
            TableDetail(touches, null, null);

        /// <summary>The same detail, with the phrase naming the FAMILY'S cases instead of the objects' kinds.
        /// A proposal row passes its own words here ("3 name collisions here: Date and Sales[Date]") because
        /// the Map block is where a person decides whether the work in front of them is theirs, and "3
        /// columns, 1 measure" is true of every family at once and so tells them nothing.</summary>
        private static ModelPlanItemTable[] TableDetail(IEnumerable<PlanTouch> touches, string caseOne, string caseMany) =>
            (touches ?? Array.Empty<PlanTouch>())
                .Where(t => t != null && !string.IsNullOrWhiteSpace(t.Table))
                .GroupBy(t => t.Table, StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .Select(g => new ModelPlanItemTable
                {
                    Name = g.First().Table,
                    Count = g.Count(),
                    What = caseOne == null ? KindPhrase(g) : CasePhrase(g, caseOne, caseMany),
                    Diff = g.Select(t => t.Line)
                            .Where(l => !string.IsNullOrWhiteSpace(l))
                            .Distinct(StringComparer.Ordinal)
                            .ToArray(),
                })
                .ToArray();

        /// <summary>The kinds a per-table phrase can name, in the order it names them.</summary>
        private static readonly (string Kind, string One, string Many)[] KindWords =
        {
            ("column", "column", "columns"),
            ("measure", "measure", "measures"),
            ("table", "table", "tables"),
            ("hierarchy", "hierarchy", "hierarchies"),
            ("level", "level", "levels"),
            ("partition", "partition", "partitions"),
            ("calcitem", "calculation item", "calculation items"),
        };

        /// <summary>"24 columns, 7 measures". Counted from the objects themselves, so it is always checkable
        /// against the table's own diff, and never a rewording of the item's title.</summary>
        private static string KindPhrase(IEnumerable<PlanTouch> touches)
        {
            var all = (touches ?? Array.Empty<PlanTouch>()).ToArray();
            if (all.Length == 0) return string.Empty;

            var parts = new List<string>();
            var named = 0;
            foreach (var word in KindWords)
            {
                var n = all.Count(t => string.Equals(t.Kind, word.Kind, StringComparison.OrdinalIgnoreCase));
                if (n == 0) continue;
                parts.Add(CountOf(n, word.One, word.Many));
                named += n;
            }
            if (named < all.Length) parts.Add(CountOf(all.Length - named, "thing", "things"));
            return string.Join(", ", parts);
        }

        /// <summary>"3 name collisions here: Date, Sales[Amount] and Product[SKU]". Names only THIS table's
        /// cases, in the family's own word for a case, and lists up to three of them so the block stays a
        /// block. The names come off the touches themselves, so the phrase can never name something the
        /// table's own change lines do not.</summary>
        private static string CasePhrase(IEnumerable<PlanTouch> touches, string one, string many)
        {
            var all = (touches ?? Array.Empty<PlanTouch>()).ToArray();
            if (all.Length == 0) return string.Empty;

            var head = CountOf(all.Length, one, many) + " here";
            var names = all.Select(t => t.Name)
                           .Where(n => !string.IsNullOrWhiteSpace(n))
                           .Distinct(StringComparer.Ordinal)
                           .ToArray();
            return names.Length == 0 ? head : head + ": " + Join(names, 3);
        }

        /// <summary>An object written the way a person writes it: "Sales[CustomerKey]" for a column or a
        /// measure, "Sales" for a table. This is what makes every per-table diff line name its own table. The
        /// rules put only the bare name in ObjectName ("CustomerKey"), so a line built from that named no
        /// table at all and could not honestly be filed under one.</summary>
        private static string Qualified(string objectRef, string fallback)
        {
            var table = TableOfRef(objectRef);
            if (table == null) return string.IsNullOrWhiteSpace(fallback) ? "the model" : fallback;

            var body = objectRef.Substring(objectRef.IndexOf(':') + 1);
            var slash = body.LastIndexOf('/');
            if (slash < 0) return table;
            var member = body.Substring(slash + 1);
            return string.IsNullOrWhiteSpace(member) ? table : table + "[" + member + "]";
        }

        private static string KindOfRef(string objectRef)
        {
            if (string.IsNullOrWhiteSpace(objectRef)) return "thing";
            var colon = objectRef.IndexOf(':');
            return colon <= 0 ? "thing" : objectRef.Substring(0, colon);
        }

        /// <summary>Object-reference kinds whose body begins with the owning table's name.</summary>
        private static readonly HashSet<string> TableOwningRefKinds = new HashSet<string>(StringComparer.Ordinal)
        {
            "table", "column", "measure", "hierarchy", "level", "partition", "calcitem",
        };

        private static string TableOfRef(string objectRef)
        {
            if (string.IsNullOrWhiteSpace(objectRef)) return null;
            var colon = objectRef.IndexOf(':');
            if (colon <= 0) return null;
            if (!TableOwningRefKinds.Contains(objectRef.Substring(0, colon))) return null;

            var body = objectRef.Substring(colon + 1);
            var slash = body.IndexOf('/');
            var table = slash < 0 ? body : body.Substring(0, slash);
            return string.IsNullOrWhiteSpace(table) ? null : table;
        }

        private static string RuleTitle(ReadinessFinding f) =>
            string.IsNullOrWhiteSpace(f.RuleTitle) ? (f.RuleId ?? "a finding") : PlainRuleTitle(f.RuleTitle);

        /// <summary>A rule's title without its bracketed group ("[Formatting] Hide foreign keys" reads "Hide foreign keys"): the group is the rule list's own sorting word, not something a person reads.</summary>
        private static string PlainRuleTitle(string title)
        {
            if (string.IsNullOrWhiteSpace(title)) return title;
            var t = title.TrimStart();
            if (t.Length > 2 && t[0] == '[')
            {
                var close = t.IndexOf(']');
                if (close > 0 && close + 1 < t.Length) return t.Substring(close + 1).TrimStart();
            }
            return title;
        }

        /// <summary>What a safe-fix task is about, in the words a person uses for it (the readiness category's own name says little: "naming", "visibility").</summary>
        private static string SafeFixTitle(string category, int n)
        {
            var noun = category switch
            {
                "Naming" => "naming",
                "Descriptions" => "description",
                "Synonyms" => "synonym",
                "Relationships" => "relationship",
                "Visibility" => "visibility",
                "Formatting" => "number format",
                "CopilotLimits" => "Copilot limit",
                "DataAgentConfig" => "data agent setup",
                "BestPractice" => "best practice",
                _ => Lower(Spaced(category ?? "BestPractice")),
            };
            return "Fix " + CountOf(n, noun + " problem", noun + " problems");
        }

        private static string Objects(int n) => CountOf(n, "object", "objects");

        /// <summary>The objects a set of findings is about, once each, in the order found: a rule row's Objects.</summary>
        private static string[] RefsOf(IEnumerable<ReadinessFinding> findings) =>
            findings.Select(x => x.ObjectRef).Where(r => !string.IsNullOrWhiteSpace(r))
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        private static string CountOf(int n, string one, string many) =>
            n.ToString("N0", CultureInfo.InvariantCulture) + " " + (n == 1 ? one : many);

        private static string Join(IEnumerable<string> names, int max)
        {
            var all = (names ?? Array.Empty<string>()).Where(n => !string.IsNullOrWhiteSpace(n)).ToArray();
            if (all.Length == 0) return string.Empty;
            return all.Length <= max
                ? string.Join(", ", all)
                : string.Join(", ", all.Take(max)) + " and "
                  + (all.Length - max).ToString("N0", CultureInfo.InvariantCulture) + " more";
        }

        /// <summary>"CopilotLimits" to "Copilot limits". The categories are code words and a person reads them.</summary>
        private static string Spaced(string pascal)
        {
            if (string.IsNullOrEmpty(pascal)) return pascal;
            var sb = new System.Text.StringBuilder();
            for (var i = 0; i < pascal.Length; i++)
            {
                if (i > 0 && char.IsUpper(pascal[i]) && !char.IsUpper(pascal[i - 1])) sb.Append(' ');
                sb.Append(pascal[i]);
            }
            return sb.ToString();
        }

        /// <summary>Lower-case the first letter only, so a sentence can carry another sentence inside it without
        /// a capital appearing in the middle. Leaves an all-caps or already-lower start alone.</summary>
        private static string Lower(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            if (s.Length > 1 && char.IsUpper(s[0]) && char.IsUpper(s[1])) return s;
            // Jev is a name, and keeps its capital wherever a sentence starts with it (low item 6: "jev was asked").
            if (s.StartsWith("Jev", StringComparison.Ordinal) && (s.Length == 3 || !char.IsLetter(s[3]))) return s;
            return char.ToLowerInvariant(s[0]) + s.Substring(1);
        }

        /// <summary>A reason as one sentence: a capital at the front and a full stop at the end, whichever way it arrived
        /// (the relay writes whole sentences, the engine writes clauses).</summary>
        private static string AsReadSentence(string reason)
        {
            var r = (reason ?? "").Trim();
            if (r.Length == 0) return r;
            r = char.ToUpperInvariant(r[0]) + r.Substring(1);
            return r.EndsWith(".", StringComparison.Ordinal) || r.EndsWith("!", StringComparison.Ordinal)
                   || r.EndsWith("?", StringComparison.Ordinal) ? r : r + ".";
        }

        private static string Capital(string s) =>
            string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);

        private static string Friendly(DateTimeOffset when)
        {
            if (when == DateTimeOffset.MinValue) return "earlier";
            var ago = DateTimeOffset.UtcNow - when;
            if (ago < TimeSpan.FromMinutes(2)) return "just now";
            if (ago < TimeSpan.FromHours(1)) return ((int)ago.TotalMinutes).ToString(CultureInfo.InvariantCulture) + " minutes ago";
            if (ago < TimeSpan.FromDays(1)) return ((int)ago.TotalHours).ToString(CultureInfo.InvariantCulture) + " hours ago";
            return when.ToLocalTime().ToString("d MMM", CultureInfo.InvariantCulture);
        }
    }
}
