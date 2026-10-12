// The asked items and the three facts on every row (Plan at the heart, contract sections 1.2 to 1.5, 2 and 15).
//
// Two kinds of row share one list and must not be confused:
//
//   A DERIVED row (found by a rule, proposed by Jev) is recomputed on every call and never stored. What a
//   person decided about it (held, a doer of their choosing, a note) is kept beside it by the row's stable id,
//   as residue, exactly the way a skip already was.
//
//   An ASKED row (a task typed in the Plan's field, sent from a page, a migrated aim, a repeat's occurrence) is
//   STORED, because nothing in the model can recompute a person's ask. It is a PlanTask in the Plan's own file,
//   with a random id, whether or not any assistant is attached. The queue only delivers it (lane E3).
//
// SOURCE, DOER AND STATE ARE THREE FACTS, and the old Who field mixed two of them: "jev" was a source living
// in the doer's field. Here Jev is a source and never a doer. A Jev row's doer is decided by its kind, as a
// rule row's is: Jev's lines are settings the engine applies, so its doer is Semanticus.
//
// THE STATE WORD IS THE ENGINE'S. One function, StateWordOf, applies the precedence table of contract 1.4, and
// the page, the fold line and the summary all read its answer rather than working one out of their own.
//
// A MODEL WITH NOWHERE TO KEEP THINGS (never saved, not live) still gets its tasks for the session, held in
// engine memory, and every such row says so (contract 2.3).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;

namespace Semanticus.Engine
{
    /// <summary>What the state word is computed from. Each flag is one row of the precedence table.</summary>
    public sealed class PlanStateFacts
    {
        public bool Done { get; set; }
        public bool SetAside { get; set; }
        public bool Checking { get; set; }
        public bool OnIt { get; set; }
        public bool Held { get; set; }
        public bool Waiting { get; set; }
        public bool NeedsYou { get; set; }
    }

    /// <summary>What a person or a Send asks for when a task is added (the human-only <c>addPlanTask</c>).</summary>
    public sealed class PlanTaskRequest
    {
        /// <summary>The task in the person's own words. Required, 280 characters or fewer.</summary>
        public string Words { get; set; }

        /// <summary>One of <see cref="PlanDoers"/> except semanticus. Null means Your assistant (Kane, 24 Sep).</summary>
        public string Doer { get; set; }

        /// <summary>The page a Send came from, as a tool id. Present makes the source "you"; absent, "words". The one word
        /// that is not a page is <see cref="PlanTasks.SuggestSourcePage"/>: a button of the Studio's suggested next steps
        /// (1.4). It keeps the source "words", links back to no page and offers no jump.</summary>
        public string SourcePage { get; set; }

        /// <summary>The page the Plan's field SHOWED for these words when Enter was pressed, as a tool id, or null when
        /// it showed none (plan.tsx sends askbox.tsx fieldTaskPage). The row names it, so the field and the row agree
        /// wherever the field named a page the words alone do not, which is the service's page with Jev on
        /// (verify-askdata-r6 F1). A page this build does not know, and Ask your data, are not kept: the row then
        /// reads the words as before, and the task is added all the same.</summary>
        public string FieldPage { get; set; }

        /// <summary>The engine-built prompt a Send carries, 4000 characters or fewer.</summary>
        public string Brief { get; set; }

        /// <summary>Object refs the task is about.</summary>
        public string[] Objects { get; set; }

        /// <summary>One of <see cref="PlanDoneWhen"/>, or null for the default of the doer.</summary>
        public string DoneWhen { get; set; }

        public PlanOriginLink OriginLink { get; set; }

        /// <summary>For a has_words task, where the words go when it is not the objects' Description: a doc section
        /// ("doc:" + section key + "|" + object ref, or "model") or a change-plan slot ("change:" + item id).</summary>
        public string Slot { get; set; }

        /// <summary>What the Send names: its page plus a stable key for the thing sent (a finding's rule and
        /// object, say). While a task with the same page and key is open on this model's plan, asking again
        /// returns that task instead of adding a second (Sol 6 slice 9 review). Null for a typed task.</summary>
        public string SendKey { get; set; }
    }

    public static class PlanTasks
    {
        public const string IdPrefix = "pt-";
        public const int MaxWordsChars = 280;
        public const int MaxBriefChars = 4000;
        /// <summary>The most of a person's own text one task keeps. Past it the add is refused and nothing is cut.</summary>
        public const int MaxFullTextChars = 100_000;
        public const int MaxNoteChars = 1000;
        public const int MaxSendKeyChars = 400;

        /// <summary>The source page of a task added from a button of the Studio's suggested next steps (suggest_next, 1.4).
        /// It is a word for where the task came from, not a page of the Studio.</summary>
        public const string SuggestSourcePage = "suggest";

        /// <summary>Said on every row of a model that has nowhere to keep its tasks (contract 2.3).</summary>
        public const string NotKeptSentence = "Not kept yet. Save the model to keep your tasks.";

        // The state notes of contract 1.4, word for word.
        public const string OnNextCallNote = "when your assistant next checks the Plan";
        public const string PausedByYouNote = "You paused your assistant's tasks. It gets this one when you turn them back on.";
        public const string SinceLastCallNote = "since its last call";
        public const string YouTookItNote = "you took it";

        /// <summary>The note on a task History reopened (UAT S01-j2-007).</summary>
        public const string TakenBackNote = "Its change was undone, so the task is open again.";

        /// <summary>Said when a task's words were longer than a title: nothing was cut.</summary>
        public const string KeptWholeSentence = "The title is the start of your text. The task keeps all of your text.";

        /// <summary>A title for text longer than a title holds: its start, cut at the last whole word that fits,
        /// with "..." to say there is more. The whole text is kept beside it, so the cut loses nothing.</summary>
        public static string TitleOf(string text)
        {
            var line = System.Text.RegularExpressions.Regex.Replace((text ?? "").Trim(), @"\s+", " ");
            if (line.Length <= MaxWordsChars) return line;
            const string more = "...";
            var room = MaxWordsChars - more.Length;
            var cut = line.LastIndexOf(' ', room);
            return (cut > room / 2 ? line.Substring(0, cut) : line.Substring(0, room)).TrimEnd() + more;
        }

        /// <summary>The one limit on a person's own text, for a task and a draft from words alike: past it nothing is
        /// kept and the refusal names the limit. Nothing is ever cut.</summary>
        public static void RequireWithinFullText(string text, string what, string param)
        {
            if (text != null && text.Length > MaxFullTextChars)
                throw new ArgumentException("That text is " + text.Length.ToString("N0", CultureInfo.InvariantCulture)
                    + " characters long, and " + what + " keeps up to " + MaxFullTextChars.ToString("N0", CultureInfo.InvariantCulture)
                    + ". Nothing was added. Split it into smaller parts.", param);
        }

        public static bool IsTaskId(string id) => id != null && id.StartsWith(IdPrefix, StringComparison.Ordinal);

        /// <summary>"pt-" and 8 lowercase hex from a cryptographic source. Random rather than counted because a
        /// counter restarts with the engine and would name another task (contract 1.3).</summary>
        public static string NewId() =>
            IdPrefix + Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();

        /// <summary>A new id no task in this state already has or answers to. A collision in 32 bits is rare but
        /// not impossible, and the price of checking is one loop.</summary>
        public static string NewIdFor(PlanMemoryState state)
        {
            while (true)
            {
                var id = NewId();
                if (state?.FindTask(id) == null) return id;
            }
        }

        /// <summary>The default way done is known for a doer: the assistant says so, or the person marks it.</summary>
        public static string DefaultDoneWhen(string doer) =>
            doer == PlanDoers.You ? PlanDoneWhen.YouMark : PlanDoneWhen.AssistantSays;

        /// <summary>True for an aim: a typed target, migrated or added through <c>add_plan_target</c>. Only an aim
        /// keeps a section.</summary>
        public static bool IsAim(PlanTask task) => task != null && task.Section != null;

        public static bool IsOpenTask(PlanTask task) => task != null && !PlanTaskStates.IsClosed(task.State);

        /// <summary>The ids of found rows a task was given from ("Give to your assistant" on a found row). The task
        /// carries the work from then on, so the row is not shown, offered or run by itself, and once done it is Done
        /// once, as the task. A task set aside lets the row come back.</summary>
        internal static HashSet<string> GivenRows(IEnumerable<PlanTask> tasks) =>
            new HashSet<string>((tasks ?? Enumerable.Empty<PlanTask>())
                .Where(t => t != null && t.State != PlanTaskStates.SetAside && t.OriginLink?.Kind == PlanOriginKinds.Item && !string.IsNullOrWhiteSpace(t.OriginLink.Ref))
                .Select(t => t.OriginLink.Ref), StringComparer.Ordinal);

        // ---- migration (contract 15) --------------------------------------------------------------------

        /// <summary>Every typed target becomes an aim task: source words, doer you, done when you mark it, its
        /// section kept, a new random id, and the old target id and item id kept as aliases so anything that
        /// names them still resolves. Its dates carry over: when it was added becomes CreatedUtc, and a finished
        /// target becomes a done aim with the same DoneUtc, so it is never offered or counted as open.
        ///
        /// Idempotent: a target whose id is already an alias on a task is not migrated twice.</summary>
        public static void MigrateTargets(PlanMemoryState state)
        {
            if (state?.Targets == null || state.Targets.Count == 0) return;
            foreach (var target in state.Targets.Where(t => t != null && !string.IsNullOrWhiteSpace(t.Id)))
            {
                if (state.FindTask(target.Id) != null) continue;
                var done = target.DoneUtc.HasValue;
                state.Tasks.Add(new PlanTask
                {
                    Id = NewIdFor(state),
                    Words = target.Text,
                    Source = PlanSources.Words,
                    Doer = PlanDoers.You,
                    DoneWhen = PlanDoneWhen.YouMark,
                    OriginLink = new PlanOriginLink { Kind = PlanOriginKinds.Words },
                    Section = PlanTargets.NormaliseSection(target.Section) ?? ModelPlanSections.Structure,
                    CreatedUtc = target.When ?? DateTimeOffset.UtcNow,
                    State = done ? PlanTaskStates.Done : PlanTaskStates.Open,
                    DoneUtc = target.DoneUtc,
                    DoneBy = done ? PlanDoers.You : null,
                    Aliases = new[] { target.Id, PlanTargets.ItemIdFor(target.Id) },
                });
            }
            state.Targets.Clear();
        }

        // ---- keeping the store in shape -----------------------------------------------------------------

        /// <summary>Numbers the assistant's open tasks 1, 2, 3 in their current order and clears the order of
        /// every other task, then drops the oldest closed tasks beyond the cap. Run on every write, so the order
        /// on disk is always a clean sequence.</summary>
        public static void Trim(PlanMemoryState state)
        {
            if (state?.Tasks == null) return;
            CompactOrder(state.Tasks);

            var closed = state.Tasks.Where(t => PlanTaskStates.IsClosed(t.State))
                .OrderByDescending(t => t.DoneUtc ?? t.CreatedUtc)
                .Skip(PlanMemory.MaxClosedTasks)
                .ToHashSet();
            if (closed.Count > 0) state.Tasks.RemoveAll(closed.Contains);
        }

        internal static void CompactOrder(List<PlanTask> tasks)
        {
            var n = 0;
            foreach (var t in tasks.Where(t => t.Doer == PlanDoers.Assistant && IsOpenTask(t))
                                   .OrderBy(t => t.Order ?? int.MaxValue)
                                   .ThenBy(t => t.CreatedUtc)
                                   .ToList())
                t.Order = ++n;
            foreach (var t in tasks.Where(t => t.Doer != PlanDoers.Assistant || !IsOpenTask(t)))
                t.Order = null;
        }

        /// <summary>The next place at the end of the assistant's order.</summary>
        internal static int NextOrder(PlanMemoryState state) =>
            state.Tasks.Where(t => t.Doer == PlanDoers.Assistant && IsOpenTask(t)).Select(t => t.Order ?? 0)
                 .DefaultIfEmpty(0).Max() + 1;

        // ---- the three facts on a derived row -----------------------------------------------------------

        /// <summary>The doer a derived row has by its kind, read off the old owner field. A Jev row's doer is
        /// decided by its kind exactly as a rule row's is (contract 1.2): its lines are settings the engine
        /// applies, so it is Semanticus. Jev is never a doer.</summary>
        public static string DoerOf(string who) =>
            who == ModelPlanOwners.You ? PlanDoers.You
            : who == ModelPlanOwners.Assistant ? PlanDoers.Assistant
            : PlanDoers.Semanticus;   // auto, and jev

        /// <summary>The old owner field for a doer, for readers that still read Who (kept one release).</summary>
        public static string WhoOf(string doer) =>
            doer == PlanDoers.You ? ModelPlanOwners.You
            : doer == PlanDoers.Assistant ? ModelPlanOwners.Assistant
            : ModelPlanOwners.Auto;

        /// <summary>Where a derived row came from. A row an assistant opened by putting lines on the change plan
        /// ("asst/") is the person's words passed through their assistant, so it is "words".</summary>
        public static string SourceOf(string itemId, string assistantPrefix) =>
            JevFamily.IsJevItem(itemId) ? PlanSources.Jev
            : itemId != null && assistantPrefix != null && itemId.StartsWith(assistantPrefix, StringComparison.Ordinal)
                ? PlanSources.Words
            : PlanSources.Found;

        /// <summary>The engine's sentence for how done is known.</summary>
        public static string DoneTextOf(string doneWhen, int objects)
        {
            switch (doneWhen)
            {
                case PlanDoneWhen.HasWords:
                    return objects == 1 ? "Semanticus checks the model: it has words."
                         : objects > 1 ? "Semanticus checks the model: all " + objects.ToString(CultureInfo.InvariantCulture) + " have words."
                         : "Semanticus checks the model for the words.";
                case PlanDoneWhen.AssistantSays:
                    return "Your assistant says it is done.";
                case PlanDoneWhen.YouMark:
                    return "You mark it done.";
                default:
                    return "Semanticus marks it done once the problem is gone.";
            }
        }

        // ---- the state word ----------------------------------------------------------------------------

        /// <summary>The precedence table of contract 1.4: top to bottom, the first that holds wins.</summary>
        public static string StateWordOf(PlanStateFacts f)
        {
            if (f == null) return PlanStateWords.Ready;
            if (f.Done) return PlanStateWords.Done;
            if (f.SetAside) return PlanStateWords.SetAside;
            if (f.Checking) return PlanStateWords.Checking;
            if (f.OnIt) return PlanStateWords.OnIt;
            if (f.Held) return PlanStateWords.Paused;
            if (f.Waiting) return PlanStateWords.Waiting;
            if (f.NeedsYou) return PlanStateWords.NeedsYou;
            return PlanStateWords.Ready;
        }

        /// <summary>Stamp the state word and its note on a row whose other facts are set. Called LAST in the
        /// compose, after anything that fills WaitsFor, so Waiting is read off the row itself.
        ///
        /// A derived row's facts come from its old status and change state; a task's from the task.</summary>
        internal static void StampState(ModelPlanItem item, PlanTask task, bool doneByCheck, string queue, string[] gone = null)
        {
            // An open task whose object was deleted needs the person: nobody can do it as it stands (UAT S12-5-006).
            gone = task != null && IsOpenTask(task) && gone is { Length: > 0 } ? gone : null;
            // It waits for nothing: no other row's work can make it doable again. Only the person can.
            if (gone != null) item.WaitsFor = Array.Empty<PlanWaitRef>();
            var waiting = item.WaitsFor != null && item.WaitsFor.Length > 0;
            PlanStateFacts facts;
            var acceptedAll = false;
            // Only a row Semanticus itself runs: an assistant's or a person's row with unticked lines is theirs to finish and is never offered to Run the plan.
            var allDropped = task == null && item.Doer == PlanDoers.Semanticus && EveryLineDropped(item, out acceptedAll);
            if (task == null)
            {
                facts = new PlanStateFacts
                {
                    Done = item.Status == ModelPlanStatuses.Done,
                    // A row whose every line was left out has nothing for Semanticus to run, so it is never Ready: set aside when the person
                    // accepted every one of those changes, and the person's to look at when they are only unticked.
                    SetAside = item.Status == ModelPlanStatuses.Skipped || (allDropped && acceptedAll),
                    Checking = item.State == ModelPlanItemStates.Applied,
                    OnIt = item.Status == ModelPlanStatuses.Running,
                    Held = item.Held,
                    Waiting = waiting,
                    NeedsYou = item.Doer == PlanDoers.You || allDropped,
                };
            }
            else
            {
                var saidDone = task.State == PlanTaskStates.Done;
                var checkedDone = task.DoneWhen == PlanDoneWhen.ModelCheck || task.DoneWhen == PlanDoneWhen.HasWords;
                facts = new PlanStateFacts
                {
                    // A task the engine checks is done when the check passes, whoever said so. One a person or
                    // an assistant closes is done when they said so.
                    Done = checkedDone ? doneByCheck : saidDone,
                    SetAside = task.State == PlanTaskStates.SetAside || item.Status == ModelPlanStatuses.Skipped,
                    // Said done, not yet proven: Applied, checking (contract 3.2).
                    Checking = checkedDone && saidDone && !doneByCheck,
                    OnIt = task.Claim != null || task.State == PlanTaskStates.Claimed,
                    Held = task.Held,
                    Waiting = waiting,
                    NeedsYou = task.Doer == PlanDoers.You || !string.IsNullOrWhiteSpace(task.DeclineReason) || gone != null,
                };
            }

            item.StateWord = StateWordOf(facts);
            if (allDropped && item.StateWord == PlanStateWords.SetAside && item.Status != ModelPlanStatuses.Skipped) item.StateNote = AllAcceptedNote;
            else if (allDropped && item.StateWord == PlanStateWords.NeedsYou && item.Doer != PlanDoers.You)
                item.StateNote = string.IsNullOrWhiteSpace(item.ChangeSet?.FailureReason) ? AllUntickedNote
                    : item.ChangeSet.FailureReason + " " + LocalEngine.OpenRowOnPlanSentence;
            else item.StateNote = gone != null && item.StateWord == PlanStateWords.NeedsYou
                ? PlanTaskObjects.GoneSentence(gone)
                : NoteFor(item, task, queue);
        }

        internal const string AllAcceptedNote = "You accepted every change on this task, so Semanticus has nothing to do. Take the acceptance off to bring it back.";
        internal const string AllUntickedNote = "Every change on this task is unticked, so Run the plan skips it. Tick the ones you want, or set the task aside.";

        /// <summary>The row has a worked-out set with lines, and not one of them is ticked. <paramref name="acceptedAll"/> is true when each of
        /// those lines was left out because the person accepted its finding (the notes the plan puts on such a line), not merely unticked.</summary>
        private static bool EveryLineDropped(ModelPlanItem item, out bool acceptedAll)
        {
            acceptedAll = false;
            var set = item?.ChangeSet;
            if (set == null || !string.Equals(set.State, ModelPlanChangeStates.Proposed, StringComparison.Ordinal)) return false;
            var lines = set.Lines ?? Array.Empty<ModelPlanChangeLine>();
            if (lines.Length == 0 || lines.Any(l => l == null || l.Kept)) return false;
            acceptedAll = lines.All(l => l.Note == LocalEngine.WaivedAfterPlannedNote || l.Note == LocalEngine.DeclinedChangeNote);
            return true;
        }

        /// <summary>The note beside the word. A note an earlier step already wrote is KEPT unless this word has
        /// a note of its own: lane E2 writes "Semanticus ordered these by the plan's order." on a row whose waits
        /// the plan's order decided, and a model with nowhere to keep things says so (E2's patch, 24 Sep).</summary>
        private static string NoteFor(ModelPlanItem item, PlanTask task, string queue)
        {
            switch (item.StateWord)
            {
                case PlanStateWords.OnIt:
                    if (task?.Claim != null)
                        return task.Claim.ClaimedBy == PlanDoers.You ? YouTookItNote : SinceLastCallNote;
                    return item.StateNote;
                case PlanStateWords.NeedsYou:
                    return string.IsNullOrWhiteSpace(task?.DeclineReason) ? item.StateNote : task.DeclineReason;
                case PlanStateWords.Ready:
                    // Only a task for the assistant is offered, so only it carries the offer's note (contract 3.2).
                    if (task != null && task.Doer == PlanDoers.Assistant)
                        return queue == PlanAssistantQueue.Paused ? PausedByYouNote : task.ReleaseNote ?? OnNextCallNote;
                    return item.StateNote;
                default:
                    return item.StateNote;
            }
        }

        // ---- the row a task becomes ---------------------------------------------------------------------

        /// <summary>The row for one task. Everything but the state word, which <see cref="StampState"/> sets once
        /// every row is known.</summary>
        internal static ModelPlanItem ItemFor(PlanTask task, bool notKept, PlanSkip skip)
        {
            var aim = IsAim(task);
            var closed = PlanTaskStates.IsClosed(task.State);
            var objects = task.Objects ?? Array.Empty<string>();
            var jump = JumpFor(task);

            var why = new List<string>
            {
                task.OriginLink?.Kind == PlanOriginKinds.Item
                    ? "You gave this to your assistant from the Plan on " + Day(task.CreatedUtc) + "."
                : task.Source == PlanSources.You
                    ? "You sent this from " + (jump?.Label ?? "a page") + " on " + Day(task.CreatedUtc) + "."
                    : task.Source == PlanSources.Repeat
                        ? "A repeat you set up brought this back on " + Day(task.CreatedUtc) + "."
                        : aim ? "You added this aim on " + Day(task.CreatedUtc) + "."
                        : "You added this on " + Day(task.CreatedUtc) + ".",
            };
            // What an aim always said: the engine cannot read the change out of the words, and where it is done
            // by hand, or that the words name no place (a guessed page would be worse than none).
            if (aim)
                why.Add(jump == null
                    ? "Nothing in the model matches it yet, so Semanticus cannot work out the change. "
                      + "The words also do not name a page to do it on."
                    : "Nothing in the model matches it yet, so Semanticus cannot work out the change. "
                      + "Do it yourself on " + jump.Label + ".");
            if (task.DoneUtc.HasValue && task.State == PlanTaskStates.Done)
                why.Add((task.DoneBy == PlanDoers.You ? "You marked it done on " : "It was marked done on ")
                        + Day(task.DoneUtc.Value) + ".");
            if (notKept) why.Add(NotKeptSentence);

            var item = new ModelPlanItem
            {
                Id = task.Id,
                Section = task.Section ?? ModelPlanSections.Structure,
                Title = task.Words,
                Sub = notKept ? NotKeptSentence : SubFor(task, objects),
                Value = string.Empty,
                Who = WhoOf(task.Doer),
                // An open aim keeps the "target" status the current page draws it with, for one release.
                Status = skip != null ? ModelPlanStatuses.Skipped
                       : task.State == PlanTaskStates.Done ? ModelPlanStatuses.Done
                       : aim ? ModelPlanStatuses.Target
                       : ModelPlanStatuses.Ready,
                State = task.State == PlanTaskStates.Done ? ModelPlanItemStates.Done
                      : skip != null ? ModelPlanItemStates.Skipped
                      : aim ? ModelPlanItemStates.Target
                      : ModelPlanItemStates.Gap,
                SkipReason = skip?.Reason,
                SkippedUtc = skip?.When,
                Why = why.ToArray(),
                Diff = Array.Empty<string>(),
                Touches = objects.Length == 0 ? (closed ? "nothing to do" : "nothing yet")
                        : objects.Length == 1 ? "1 object" : objects.Length.ToString(CultureInfo.InvariantCulture) + " objects",
                Tables = Array.Empty<ModelPlanItemTable>(),
                Jump = jump,

                Source = task.Source ?? PlanSources.Words,
                SourcePage = task.SourcePage,
                Doer = task.Doer,
                DoneWhen = task.DoneWhen,
                DoneText = DoneTextOf(task.DoneWhen, objects.Length),
                Objects = objects,
                Held = task.Held,
                Order = task.Doer == PlanDoers.Assistant && !closed ? task.Order : null,
                Note = task.Note,
                Reply = task.Reply,
                FullText = task.FullText,
                Claim = task.Claim == null ? null : new PlanClaimView { ClaimedBy = task.Claim.ClaimedBy, Since = task.Claim.ClaimedUtc },
                OriginLink = task.OriginLink,
                // A row the person asked for. A task sent from a finding still names the rule, so the page that lists findings can say
                // "On the plan" for it, and never offers to send it a second time.
                Goal = PlanGoals.Aims,
                Rules = task.OriginLink?.Kind == PlanOriginKinds.Finding && SplitFindingRef(task.OriginLink.Ref).Rule is string findingRule
                    && !string.IsNullOrWhiteSpace(findingRule) ? new[] { findingRule } : Array.Empty<string>(),
            };
            if (notKept) item.StateNote = NotKeptSentence;
            return item;
        }

        /// <summary>A renamed object shows under its name now: the row's objects and the line under its title follow
        /// the object, which the task found by its lineage tag (UAT S12-6-002).</summary>
        internal static void FollowRenames(ModelPlanItem item, PlanTask task, IReadOnlyDictionary<string, PlanGroundNow> now)
        {
            if (item?.Objects == null || item.Objects.Length == 0 || now == null || now.Count == 0) return;
            var moved = false;
            var objects = item.Objects.Select(o =>
            {
                if (!now.TryGetValue(o, out var n) || n.Gone || string.Equals(n.Current, o, StringComparison.Ordinal)) return o;
                moved = true;
                return n.Current;
            }).ToArray();
            if (!moved) return;
            item.Objects = objects;
            if (item.Sub != NotKeptSentence) item.Sub = SubFor(task, objects);
        }

        private static string SubFor(PlanTask task, string[] objects)
        {
            if (!string.IsNullOrWhiteSpace(task.DeclineReason)) return "Could not: " + task.DeclineReason;
            if (objects.Length > 0)
            {
                var names = objects.Take(3).Select(NameOfRef).ToArray();
                return string.Join(", ", names) + (objects.Length > 3
                    ? " and " + (objects.Length - 3).ToString(CultureInfo.InvariantCulture) + " more"
                    : string.Empty);
            }
            return IsAim(task)
                ? (task.State == PlanTaskStates.Done
                    ? "You added this aim. You marked it done."
                    : "You added this aim. Nothing in the model does it yet.")
                : task.Source == PlanSources.You ? "You added this." : "Your words.";
        }

        public const string DocSlot = "doc";
        public const string ChangeSlot = "change";

        /// <summary>A has_words slot: "doc:" + section key + "|" + object ref (or "model"), or "change:" + a change-plan
        /// item id. Anything else is not a slot.</summary>
        public static (string Kind, string Section, string Target) SplitSlot(string slot)
        {
            if (string.IsNullOrWhiteSpace(slot)) return (null, null, null);
            var colon = slot.IndexOf(':');
            if (colon <= 0) return (null, null, null);
            var kind = slot.Substring(0, colon);
            var rest = slot.Substring(colon + 1).Trim();
            if (kind == ChangeSlot) return rest.Length == 0 ? (null, null, null) : (kind, null, rest);
            if (kind != DocSlot) return (null, null, null);
            var bar = rest.IndexOf('|');
            if (bar <= 0 || bar == rest.Length - 1) return (null, null, null);
            return (kind, rest.Substring(0, bar).Trim(), rest.Substring(bar + 1).Trim());
        }

        public static bool IsSlot(string slot) => SplitSlot(slot).Kind != null;

        /// <summary>A finding link's Ref is "ruleId|objectRef", or a bare rule id for a model-wide finding.</summary>
        public static (string Rule, string Object) SplitFindingRef(string reference)
        {
            if (string.IsNullOrWhiteSpace(reference)) return (null, null);
            var bar = reference.IndexOf('|');
            if (bar < 0) return (reference.Trim(), null);
            var rule = reference.Substring(0, bar).Trim();
            var obj = reference.Substring(bar + 1).Trim();
            return (rule.Length == 0 ? null : rule, obj.Length == 0 ? null : obj);
        }

        /// <summary>"measure:Sales/Total" as "Sales[Total]", "table:Date" as "Date".</summary>
        internal static string NameOfRef(string objRef)
        {
            if (string.IsNullOrWhiteSpace(objRef)) return objRef;
            var colon = objRef.IndexOf(':');
            var rest = colon < 0 ? objRef : objRef.Substring(colon + 1);
            var slash = rest.IndexOf('/');
            return slash < 0 ? rest : rest.Substring(0, slash) + "[" + rest.Substring(slash + 1) + "]";
        }

        /// <summary>A field page worth keeping on a row: a page this build knows, and never Ask your data, which answers
        /// questions and changes nothing. Null for anything else, which is not a refusal: the task is still added.</summary>
        internal static string FieldPageOf(string tool)
        {
            var page = string.IsNullOrWhiteSpace(tool) ? null : AskRouteCatalog.Find(tool.Trim());
            return page == null || string.Equals(page.Id, AskRouteCatalog.DataTool, StringComparison.Ordinal) ? null : page.Id;
        }

        /// <summary>Where the work is done by hand: the page a Send came from, else the page the Plan's field named for
        /// the words when they were added (the service's page, where the words alone name none), else the ask box's own
        /// routing of the words, else nowhere, said honestly rather than guessed (as a typed target always did). Never
        /// Ask your data, which answers questions and changes nothing.</summary>
        private static ModelPlanJump JumpFor(PlanTask task)
        {
            var page = string.IsNullOrWhiteSpace(task.SourcePage) ? null : AskRouteCatalog.Find(task.SourcePage);
            if (page != null) return new ModelPlanJump { Tool = page.Id, Label = page.Label };
            if (string.Equals(task.SourcePage, PlanTasks.SuggestSourcePage, StringComparison.Ordinal)) return null;
            var field = string.IsNullOrWhiteSpace(task.FieldPage) ? null : AskRouteCatalog.Find(task.FieldPage);
            if (field != null && !string.Equals(field.Id, AskRouteCatalog.DataTool, StringComparison.Ordinal))
                return new ModelPlanJump { Tool = field.Id, Label = field.Label };
            // The WHOLE text, not the title: the field reads the whole text, so a title cut at 280 characters could name
            // a page the field did not, or lose the one it did (Sol on fix-askdata-final).
            return PlanTargets.JumpFor(task.FullText ?? task.Words);
        }

        /// <summary>The aims as the target half of the view shows them (ModelPlanView.Targets), for readers of
        /// that list. The id is the task's own; the old slug lives on as an alias.</summary>
        internal static ModelPlanTarget TargetOf(PlanTask task) => new ModelPlanTarget
        {
            Id = task.Id,
            Text = task.Words,
            Section = task.Section,
            Source = ModelPlanTargetSources.You,
            When = task.CreatedUtc,
            DoneUtc = task.State == PlanTaskStates.Done ? task.DoneUtc ?? task.CreatedUtc : (DateTimeOffset?)null,
            ItemId = task.Id,
        };

        private static string Day(DateTimeOffset when) => PlanDay.Of(when);
    }

    /// <summary>The day a Plan sentence names, in the PERSON'S time zone: the engine runs on their own machine. It used
    /// to be the UTC day, so a task added at 04:56 on 28 September in Brisbane read "You added this on 27 September"
    /// (UAT, 28 Sep). One place, so the Plan's sentences cannot name two different days for one moment.</summary>
    internal static class PlanDay
    {
        /// <summary>The zone the day is read in. The machine's own; a test sets another for the call it makes.</summary>
        internal static readonly System.Threading.AsyncLocal<TimeZoneInfo> ZoneForTest = new System.Threading.AsyncLocal<TimeZoneInfo>();

        internal static string Of(DateTimeOffset when) =>
            TimeZoneInfo.ConvertTime(when, ZoneForTest.Value ?? TimeZoneInfo.Local).ToString("d MMMM yyyy", CultureInfo.InvariantCulture);
    }

    // ---- the person's controls (contract sections 3.3, 4 and 12) ---------------------------------------------
    //
    // ON BOTH DOORS (Kane, 10 Oct 2026: the person's word in chat is enough). Adding a task, the queue switch,
    // Pause and Resume, Up, the doer switch and the note are on EngineRpcTarget and on the MCP door (add_plan_task,
    // set_assistant_queue, pause_plan_item, move_plan_task_up, set_plan_doer, set_plan_task_note), each through the
    // gate. The assistant READS every one of these settings on get_model_plan, on its next call.

    public sealed partial class LocalEngine
    {
        /// <summary>Tasks, residue and the switch for a model with nowhere durable to keep them, for as long as
        /// that model is open. Keyed by the session context, the unit SessionManager exchanges, so they go when
        /// the model goes and never follow the person to another model.</summary>
        private readonly ConditionalWeakTable<SessionContext, PlanMemoryState> _sessionPlanTasks =
            new ConditionalWeakTable<SessionContext, PlanMemoryState>();

        private static readonly JsonSerializerOptions TaskCopy = new JsonSerializerOptions();

        /// <summary>Everything the plan remembers for the bound model: the file's contents, or, for a model with
        /// nowhere to keep them, the session-held tasks. A copy, so a compose never reads a list a person's
        /// control is changing. A file written before the upgrade is migrated here, once, by writing it back.</summary>
        internal PlanMemoryState ReadPlanState(PlanMemoryBinding bound)
        {
            var state = PlanMemory.Read(bound.File, bound.Identity);
            if (bound.Nowhere)
            {
                var held = _sessionPlanTasks.GetOrCreateValue(bound.Context);
                lock (held)
                {
                    state.Tasks = JsonSerializer.Deserialize<List<PlanTask>>(JsonSerializer.Serialize(held.Tasks, TaskCopy), TaskCopy);
                    state.Residue = new Dictionary<string, PlanRowResidue>(
                        held.Residue.ToDictionary(p => p.Key, p => new PlanRowResidue
                        {
                            Held = p.Value.Held, Doer = p.Value.Doer, Note = p.Value.Note, Absent = p.Value.Absent,
                        }), StringComparer.Ordinal);
                    state.AssistantQueue = held.AssistantQueue;
                    state.Repeats = JsonSerializer.Deserialize<List<PlanRepeat>>(JsonSerializer.Serialize(held.Repeats, TaskCopy), TaskCopy);
                    state.ClearedDone = new List<string>(held.ClearedDone);
                }
                return state;
            }

            // A claim made by an engine run that has ended is released on read, with the restart line (lane E3,
            // contract 2.2): the assistant that held it is attached to nothing that can hear its reply now. Another
            // live engine's claim is not stale (IsStale asks EngineRunLiveness).
            if (HasStaleClaims(state) && TryWritePlanMemory(bound, ReleaseStaleClaims))
                state = PlanMemory.Read(bound.File, bound.Identity);

            if (state.Targets.Count > 0)
            {
                // The lazy migration (contract 15): a write migrates, so the first read after the upgrade writes.
                // A file that cannot be written is migrated for this answer only, which is honest and rare.
                if (TryWritePlanMemory(bound, _ => { })) state = PlanMemory.Read(bound.File, bound.Identity);
                else PlanTasks.MigrateTargets(state);
            }
            return state;
        }

        /// <summary>A task said done stands only while the edit that closed it is still in the model. The edits an
        /// assistant makes while it holds a task are filed under the task (contract 8.4), so the ledger knows when
        /// History takes them back (EditLedger.NewestByCause, the same answer a Plan row's applied set and a draft's
        /// run read). Such a task is OPEN again: offered to its doer, its reply kept, and a note that says why. A redo
        /// of that same edit closes it again as it was. Run on every compose and after every undo or redo, from both
        /// doors, so the fold line, list_assistant_requests and the Plan read one answer (UAT S01-j2-007).
        ///
        /// A task whose edits were made with no claim (a person's edit in Studio, say) has nothing filed under it, so
        /// it is left as it is: a model-check task still reads its check every time.</summary>
        internal void ReconcileTakenBackTasks(PlanMemoryBinding bound)
        {
            var s = bound?.Session;
            if (s == null) return;
            var newest = _sessions.Ledger.NewestByCause(s.Id, ChangeCauseKinds.Task);
            if (newest.Count == 0) return;

            bool Reopens(PlanTask t) => t.State == PlanTaskStates.Done && t.Claim == null
                && newest.TryGetValue(t.Id, out var n) && n.TakenBack;
            bool Closes(PlanTask t) => t.TakenBack != null && t.State == PlanTaskStates.Open && t.Claim == null
                && newest.TryGetValue(t.Id, out var n) && !n.TakenBack && string.Equals(n.EntryId, t.TakenBack.EntryId, StringComparison.Ordinal);

            try
            {
                if (!ReadPlanState(bound).Tasks.Any(t => Reopens(t) || Closes(t))) return;
                WritePlanTasks(bound, state =>
                {
                    foreach (var t in state.Tasks)
                    {
                        if (Reopens(t))
                        {
                            t.TakenBack = new PlanTaskTakenBack { EntryId = newest[t.Id].EntryId, DoneUtc = t.DoneUtc, DoneBy = t.DoneBy };
                            t.State = PlanTaskStates.Open;
                            t.DoneUtc = null;
                            t.DoneBy = null;
                            t.ReleaseNote = PlanTasks.TakenBackNote;
                            t.Order = t.Doer == PlanDoers.Assistant ? PlanTasks.NextOrder(state) : (int?)null;
                        }
                        else if (Closes(t))
                        {
                            t.State = PlanTaskStates.Done;
                            t.DoneUtc = t.TakenBack.DoneUtc;
                            t.DoneBy = t.TakenBack.DoneBy;
                            t.TakenBack = null;
                            if (t.ReleaseNote == PlanTasks.TakenBackNote) t.ReleaseNote = null;
                        }
                    }
                });
            }
            catch { /* best effort on a read path: the next compose tries again */ }
        }

        /// <summary>Change the tasks, residue or switch of the bound model, or refuse when another model is open
        /// now. A model with nowhere durable to keep them changes its session-held copy.</summary>
        internal void WritePlanTasks(PlanMemoryBinding bound, Action<PlanMemoryState> change)
        {
            if (!PlanMemoryStillOpen(bound))
                throw new InvalidOperationException(PlanMemoryModelSwitchedSentence);
            if (bound.Nowhere)
            {
                var held = _sessionPlanTasks.GetOrCreateValue(bound.Context);
                lock (held) { CountPlanRowChanges(change)(held); PlanTasks.Trim(held); }
                return;
            }
            WritePlanMemory(bound, change);
        }

        /// <summary>The page is pushed a plan change after a person's control, as it is after a Jev run: it
        /// re-reads. The agent door learns on its next call (golden rule 2's stated cost).</summary>
        private void TellThePlanMoved(PlanMemoryBinding bound)
        {
            if (_planRowBatch.Value != null) return;
            try
            {
                if (ReferenceEquals(_sessions.CurrentContext, bound.Context))
                    PublishPlan(bound.Context?.Plans.Current, bound.Session.Revision);
            }
            catch { }
        }

        /// <summary>Add a task to the Plan. A task typed in the field starts with Your assistant as its doer
        /// (Kane, 24 Sep); the person switches it with "I'll do it". A Send passes the page it came from, the
        /// brief it carries, the objects, how done is known and the link back.
        ///
        /// Posting needs no assistant attached: the task is stored in the Plan's own file either way, and the
        /// queue only delivers it. Free on every tier: asking for a task runs no Jev.</summary>
        public async Task<ModelPlanActionResult> AddPlanTaskAsync(PlanTaskRequest request)
        {
            var words = request?.Words?.Trim();
            if (string.IsNullOrEmpty(words))
                throw new ArgumentException("Say what the task is, in your own words.", nameof(request));
            // MORE THAN A TITLE HOLDS IS KEPT, NEVER CUT (UAT S12-1-005: a 5,000-word paste was saved as its first 280
            // characters). The title is the start of the text and the whole text is kept with the task; past the one
            // limit, nothing is added and the refusal names the limit.
            string fullText = null;
            // Line breaks the person typed are kept the same way (N15): the title is one line, and the words as typed
            // go beside it.
            if (words.Length > PlanTasks.MaxWordsChars || words.IndexOf((char)10) >= 0 || words.IndexOf((char)13) >= 0)
            {
                PlanTasks.RequireWithinFullText(words, "a task", "request");
                fullText = words;
                words = PlanTasks.TitleOf(words);
            }
            var brief = string.IsNullOrWhiteSpace(request.Brief) ? null : request.Brief.Trim();
            if (brief != null && brief.Length > PlanTasks.MaxBriefChars)
                throw new ArgumentException("The brief is longer than " + PlanTasks.MaxBriefChars + " characters.", nameof(request));

            var doer = request.Doer == null ? PlanDoers.Assistant : PlanDoers.Normalise(request.Doer);
            if (doer == null)
                throw new ArgumentException("The doer is 'assistant' or 'you'.", nameof(request));
            if (doer == PlanDoers.Semanticus)
                throw new ArgumentException(DoerSemanticusRefusal, nameof(request));

            var objects = (request.Objects ?? Array.Empty<string>())
                .Where(o => !string.IsNullOrWhiteSpace(o)).Select(o => o.Trim())
                .Distinct(StringComparer.Ordinal).ToArray();
            var origin = request.OriginLink;
            if (origin != null && !PlanOriginKinds.All.Contains(origin.Kind))
                throw new ArgumentException("The link back is one of: " + string.Join(", ", PlanOriginKinds.All) + ".", nameof(request));

            var doneWhen = request.DoneWhen == null
                ? (origin?.Kind == PlanOriginKinds.Finding ? PlanDoneWhen.ModelCheck : PlanTasks.DefaultDoneWhen(doer))
                : PlanDoneWhen.Normalise(request.DoneWhen);
            if (doneWhen == null)
                throw new ArgumentException("How done is known is one of: " + string.Join(", ", PlanDoneWhen.All) + ".", nameof(request));
            // A check the engine cannot run is not offered as one: it would leave the task checking forever.
            var itemLink = origin?.Kind == PlanOriginKinds.Item && !string.IsNullOrWhiteSpace(origin.Ref);
            if (doneWhen == PlanDoneWhen.ModelCheck && !itemLink
                && (origin?.Kind != PlanOriginKinds.Finding || PlanTasks.SplitFindingRef(origin.Ref).Rule == null))
                throw new ArgumentException("Semanticus can check the model for this only when the task is linked to a "
                    + "finding. Link the finding, or let whoever does it say when it is done.", nameof(request));
            var slot = string.IsNullOrWhiteSpace(request.Slot) ? null : request.Slot.Trim();
            if (slot != null && !PlanTasks.IsSlot(slot))
                throw new ArgumentException("A slot is \"doc:<section>|<object>\" or \"change:<item id>\".", nameof(request));
            if (doneWhen == PlanDoneWhen.HasWords && objects.Length == 0 && slot == null)
                throw new ArgumentException("Name the objects that should have words, so Semanticus can check them.", nameof(request));

            var page = string.IsNullOrWhiteSpace(request.SourcePage) ? null : request.SourcePage.Trim();
            // A suggested next step is the person's own words that Claude drafted for them to press: not a page, so no
            // "you" source mark, no link back and no jump (contract section 6). The word is kept for the record.
            var suggested = string.Equals(page, PlanTasks.SuggestSourcePage, StringComparison.Ordinal);
            var sendKey = string.IsNullOrWhiteSpace(request.SendKey) ? null : request.SendKey.Trim();
            if (sendKey != null && sendKey.Length > PlanTasks.MaxSendKeyChars)
                throw new ArgumentException("The send key is longer than " + PlanTasks.MaxSendKeyChars + " characters.", nameof(request));
            // What the task is about, and which of those objects are in the model now (UAT S12-6-002).
            var (named, grounds) = await GroundNewTaskAsync(words, objects).ConfigureAwait(false);
            // A task typed in the Plan's field keeps the page the field SHOWED for exactly these words, so the row and
            // the field agree (verify-askdata-r6 F1). It is the page the person saw, sent with Enter, and not asked
            // again or read back from a cache: an answer on screen outlives any cache, and a second ask could come back
            // different. A Send names its own page, and a linked task is not typed words.
            var fieldPage = page == null && origin == null ? PlanTasks.FieldPageOf(request.FieldPage) : null;
            if (suggested && origin == null) origin = new PlanOriginLink { Kind = PlanOriginKinds.Words };
            var bound = BindPlanMemory();
            string id = null;
            var existing = false;
            WritePlanTasks(bound, memory =>
            {
                // The same Send again, while its task is still open on this model's plan, is the same task: a page
                // that lost its memory of the click (a reconnect) must not put the work on the plan twice. The plan
                // memory is this model's own, so the key is scoped to the model; the page makes it a page's key.
                // Checked inside the write, so two presses racing each other still make one task.
                var same = sendKey == null ? null : memory.Tasks.FirstOrDefault(t => PlanTasks.IsOpenTask(t)
                    && string.Equals(t.SourcePage, page, StringComparison.Ordinal)
                    && string.Equals(t.OriginLink?.SendKey, sendKey, StringComparison.Ordinal));
                if (same != null) { id = same.Id; existing = true; return; }
                if (memory.Tasks.Count(PlanTasks.IsOpenTask) >= PlanMemory.MaxOpenTasks)
                    throw new InvalidOperationException("This plan already has " + PlanMemory.MaxOpenTasks
                        + " open tasks. Finish or set some aside before adding more.");
                id = PlanTasks.NewIdFor(memory);
                memory.Tasks.Add(new PlanTask
                {
                    Id = id,
                    Words = words,
                    FullText = fullText,
                    Source = page == null || suggested ? PlanSources.Words : PlanSources.You,
                    SourcePage = page,
                    FieldPage = fieldPage,
                    Brief = brief,
                    Doer = doer,
                    Objects = named ?? objects,
                    Grounds = grounds,
                    DoneWhen = doneWhen,
                    Slot = slot,
                    OriginLink = new PlanOriginLink
                    {
                        Kind = origin?.Kind ?? (page == null ? PlanOriginKinds.Words : PlanOriginKinds.Page),
                        Ref = origin != null ? origin.Ref : page,
                        SendKey = sendKey,
                    },
                    Order = doer == PlanDoers.Assistant ? PlanTasks.NextOrder(memory) : (int?)null,
                    CreatedUtc = DateTimeOffset.UtcNow,
                    State = PlanTaskStates.Open,
                });
            });

            var plan = await ComposeAsync(null, bound).ConfigureAwait(false);
            TellThePlanMoved(bound);
            return new ModelPlanActionResult
            {
                ItemId = id,
                Action = "add",
                Performed = false,
                Revision = plan.Revision,
                Plan = plan,
                Sentence = existing
                    ? "This is already on the plan. Nothing was added."
                    : (doer == PlanDoers.Assistant
                        ? "Added to the plan for your assistant. Your assistant sees it the next time it checks the plan."
                        : "Added to the plan for you.")
                    + (!existing && fullText != null ? " " + PlanTasks.KeptWholeSentence : string.Empty)
                    + (bound.Nowhere ? " " + PlanTasks.NotKeptSentence : string.Empty),
            };
        }

        internal const string DoerSemanticusRefusal =
            "Semanticus cannot do this one by itself. Give it to your assistant, or do it yourself.";

        /// <summary>Pause or resume one row. A held row keeps its place, is never offered, and Run the plan skips
        /// it. On a task it is the task's own flag; on a derived row it is residue kept by the row's id.</summary>
        public Task<ModelPlanView> HoldPlanItemAsync(string id, bool held) =>
            ChangeRowAsync(id, task => task.Held = held, residue => residue.Held = held);

        /// <summary>One note from the person on a row, or none. Empty clears it.</summary>
        public Task<ModelPlanView> SetPlanTaskNoteAsync(string id, string text)
        {
            var note = string.IsNullOrWhiteSpace(text) ? null : text.Trim();
            if (note != null && note.Length > PlanTasks.MaxNoteChars)
                throw new ArgumentException("That note is longer than " + PlanTasks.MaxNoteChars + " characters.", nameof(text));
            return ChangeRowAsync(id, task => task.Note = note, residue => residue.Note = note);
        }

        /// <summary>Switch who does a row: "Give to your assistant", "I'll do it", and "Let it run" only on a row
        /// the engine can apply with no words. On a task the done-when follows the doer where it was the doer's
        /// own default, and the task joins or leaves the end of the assistant's order.</summary>
        public async Task<ModelPlanView> SetPlanItemDoerAsync(string id, string doer, string origin = "human")
        {
            var to = PlanDoers.Normalise(doer)
                ?? throw new ArgumentException("The doer is one of: " + string.Join(", ", PlanDoers.All) + ".", nameof(doer));

            // "Give to your assistant" on a found row (contract 3.2 and 4). The queue offers tasks only, so a doer word
            // kept beside the row reached nobody, and describe/model, whose doer is the assistant by its kind, was never
            // offered at all. Giving it makes a linked task through the Send path, and that task is what is offered.
            var key = id?.Trim();
            if (to == PlanDoers.Assistant && !string.IsNullOrEmpty(key) && !PlanTasks.IsTaskId(key)
                && ReadPlanState(BindPlanMemory()).FindTask(key) == null)
                return (await GiveRowToAssistantAsync(key).ConfigureAwait(false)).Plan;

            PlanTask given = null;
            var plan = await ChangeRowAsync(id,
                task =>
                {
                    if (to == PlanDoers.Semanticus) throw new ArgumentException(DoerSemanticusRefusal, nameof(doer));
                    if (task.Claim != null && task.Doer != to)
                        throw new InvalidOperationException(
                            "Someone is working on this now. Wait for it to finish, or say it could not be done.");
                    // "Give to your assistant" after Could not asks again even when the doer is already the assistant:
                    // returning early on an unchanged doer left the reason on it, so it was never offered again.
                    var declined = !string.IsNullOrWhiteSpace(task.DeclineReason);
                    if (task.Doer == to && !declined) return;
                    if (task.DoneWhen == PlanTasks.DefaultDoneWhen(task.Doer)) task.DoneWhen = PlanTasks.DefaultDoneWhen(to);
                    task.Doer = to;
                    task.DeclineReason = null;   // a new claim cycle: no claim, no reason, a place in the order
                    task.Claim = null;
                    task.ReleaseNote = null;
                    task.Order = null;
                    if (to == PlanDoers.Assistant && declined) given = task;   // a give after Could not is recorded
                },
                (residue, natural) =>
                {
                    if (to == PlanDoers.Semanticus && natural != PlanDoers.Semanticus)
                        throw new ArgumentException(DoerSemanticusRefusal, nameof(doer));
                    residue.Doer = to == natural ? null : to;
                },
                reorder: true).ConfigureAwait(false);
            if (given == null) return plan;

            // The person's give is kept on the Plan's record (a decision that wrote nothing, so both doors' History
            // shows it), then the plan is read again so the answer carries the entry.
            var bound = BindPlanMemory();
            var revision = _sessions.Current?.Revision ?? 0;
            TryWritePlanMemory(bound, state => state.History.Add(new ModelPlanHistoryEntry
            {
                When = DateTimeOffset.UtcNow, Kind = ModelPlanHistoryKinds.Given,
                Actor = string.Equals(origin, "agent", StringComparison.OrdinalIgnoreCase) ? ModelPlanActors.Assistant : ModelPlanActors.You,
                ItemId = given.Id, Title = given.Words, Revision = revision,
                Sentence = "Gave \"" + given.Words + "\" to your assistant again.", Text = "Gave \"" + given.Words + "\" to your assistant again.",
            }));
            plan = await ComposeAsync(null, bound).ConfigureAwait(false);
            TellThePlanMoved(bound);
            return plan;
        }

        /// <summary>A found or Jev row, given to the assistant, becomes one task linked back to it (origin "item"). The
        /// task names what the row names, carries the row's own words as its brief, and is checked the way the row is:
        /// descriptions by their words on the named objects, anything else by the row's gap being gone. Giving the same
        /// row again while its task is open is that task (the Send key).</summary>
        private async Task<ModelPlanActionResult> GiveRowToAssistantAsync(string key)
        {
            var bound = BindPlanMemory();
            var plan = await ComposeAsync(null, bound).ConfigureAwait(false);
            var row = plan.Items.FirstOrDefault(i => string.Equals(i.Id, key, StringComparison.Ordinal));
            if (row == null)
            {
                // Already given and open: the row is carried by its task, so giving it again is that task.
                var open = ReadPlanState(bound).Tasks.FirstOrDefault(t => PlanTasks.IsOpenTask(t)
                    && t.OriginLink?.Kind == PlanOriginKinds.Item && string.Equals(t.OriginLink.Ref, key, StringComparison.Ordinal));
                if (open != null)
                    return new ModelPlanActionResult { ItemId = open.Id, Action = "add", Performed = false, Revision = plan.Revision, Plan = plan,
                        Sentence = "This is already on the plan. Nothing was added." };
                throw new ArgumentException("No task on the plan has that id. It may be done or set aside now. "
                    + "Get the plan again.", "id");
            }
            if (Composer.DraftRows.IsDraftRow(row.Id) || IsRunRow(row.Id))
                throw new ArgumentException("Hand over a draft or a workflow run from inside it, not from its task.", "id");
            if (!PlanStateWords.IsOpen(row.StateWord))
                throw new ArgumentException("That task is done or set aside, so there is nothing to hand over.", "id");

            var objects = (row.Writes ?? Array.Empty<string>())
                .Where(w => !string.IsNullOrWhiteSpace(w) && w.Contains(':') && !w.StartsWith("model:", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.Ordinal).ToArray();
            var words = row.Id.StartsWith("describe/", StringComparison.Ordinal) && objects.Length > 0;
            // The words row's task names the one call that hands over the whole list, first, so no page limit on the
            // brief can cut it: its diff lists only the first objects (verify, 28 Sep, M6).
            var brief = string.Join("\n", new[] { row.Title + "." }
                .Concat(IsWordsItem(row.Id) ? new[] { WordsTaskLine } : Array.Empty<string>())
                .Concat(row.Why ?? Array.Empty<string>())
                .Concat((row.Diff ?? Array.Empty<string>()).Take(40))
                .Where(x => !string.IsNullOrWhiteSpace(x)));
            var title = row.Title ?? row.Id;
            return await AddPlanTaskAsync(new PlanTaskRequest
            {
                Words = title.Length <= PlanTasks.MaxWordsChars ? title : title.Substring(0, PlanTasks.MaxWordsChars - 3).TrimEnd() + "...",
                Doer = PlanDoers.Assistant,
                SourcePage = row.Jump?.Tool,
                Brief = brief.Length <= PlanTasks.MaxBriefChars ? brief : brief.Substring(0, PlanTasks.MaxBriefChars),
                Objects = objects,
                DoneWhen = words ? PlanDoneWhen.HasWords : PlanDoneWhen.ModelCheck,
                OriginLink = new PlanOriginLink { Kind = PlanOriginKinds.Item, Ref = row.Id },
                SendKey = "item:" + row.Id,
            }).ConfigureAwait(false);
        }

        /// <summary>Move a task for the assistant one place up in its order. The order is the order the offer,
        /// list_assistant_requests and the summary name them in. The first task stays first.</summary>
        public async Task<ModelPlanView> MovePlanTaskUpAsync(string id)
        {
            var bound = BindPlanMemory();
            var memory = ReadPlanState(bound);
            var task = memory.FindTask(id?.Trim());
            if (task == null || task.Doer != PlanDoers.Assistant || !PlanTasks.IsOpenTask(task))
                throw new ArgumentException(
                    "Only an open task for your assistant has a place in its order. Call get_model_plan for the ids.", nameof(id));

            WritePlanTasks(bound, state =>
            {
                PlanTasks.CompactOrder(state.Tasks);
                var mine = state.FindTask(task.Id);
                var above = state.Tasks.FirstOrDefault(t => t.Doer == PlanDoers.Assistant && PlanTasks.IsOpenTask(t)
                                                            && t.Order == mine?.Order - 1);
                if (mine == null || above == null) return;
                above.Order = mine.Order;
                mine.Order -= 1;
            });
            var plan = await ComposeAsync(null, bound).ConfigureAwait(false);
            TellThePlanMoved(bound);
            return plan;
        }

        /// <summary>The switch, "Tasks for your assistant: On / Paused". Paused governs new offers and claims
        /// only: a task already taken may finish, and nothing wakes an assistant either way.</summary>
        public async Task<ModelPlanView> SetAssistantQueueAsync(string state)
        {
            var to = PlanAssistantQueue.All.FirstOrDefault(q => string.Equals(q, (state ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException("The switch is 'on' or 'paused'.", nameof(state));
            var bound = BindPlanMemory();
            WritePlanTasks(bound, memory => memory.AssistantQueue = to);
            var plan = await ComposeAsync(null, bound).ConfigureAwait(false);
            TellThePlanMoved(bound);
            return plan;
        }

        /// <summary>One change to one row, task or derived. A derived row must be on the plan now, so a control
        /// can never leave residue under an id nobody can see.</summary>
        private Task<ModelPlanView> ChangeRowAsync(string id, Action<PlanTask> onTask, Action<PlanRowResidue> onResidue) =>
            ChangeRowAsync(id, onTask, (residue, _) => onResidue(residue), reorder: false);

        private async Task<ModelPlanView> ChangeRowAsync(
            string id, Action<PlanTask> onTask, Action<PlanRowResidue, string> onResidue, bool reorder)
        {
            if (string.IsNullOrWhiteSpace(id))
                throw new ArgumentException("Name the task. Call get_model_plan for the ids.", nameof(id));
            return await UnderReadyGateAsync(() => ChangeRowUnderGateAsync(id.Trim(), onTask, onResidue, reorder)).ConfigureAwait(false);
        }

        private async Task<ModelPlanView> ChangeRowUnderGateAsync(
            string key, Action<PlanTask> onTask, Action<PlanRowResidue, string> onResidue, bool reorder)
        {
            var bound = BindPlanMemory();

            var task = ReadPlanState(bound).FindTask(key);
            if (task != null)
            {
                WritePlanTasks(bound, memory =>
                {
                    var mine = memory.FindTask(task.Id);
                    if (mine == null) return;
                    onTask(mine);
                    if (reorder && mine.Doer == PlanDoers.Assistant && mine.Order == null && PlanTasks.IsOpenTask(mine))
                        mine.Order = PlanTasks.NextOrder(memory);
                });
            }
            else
            {
                var before = await ComposeAsync(null, bound).ConfigureAwait(false);
                var row = before.Items.FirstOrDefault(i => string.Equals(i.Id, key, StringComparison.Ordinal))
                    ?? throw new ArgumentException(
                        "No task on the plan has that id. It may be done or set aside now. "
                        + "Get the plan again.", "id");
                var natural = PlanTasks.DoerOf(row.Who);
                WritePlanTasks(bound, memory =>
                {
                    if (!memory.Residue.TryGetValue(key, out var residue))
                        memory.Residue[key] = residue = new PlanRowResidue();
                    onResidue(residue, natural);
                    residue.Absent = 0;
                    if (residue.IsEmpty) memory.Residue.Remove(key);
                });
            }

            var plan = await ComposeAsync(null, bound).ConfigureAwait(false);
            TellThePlanMoved(bound);
            return plan;
        }
    }
}
