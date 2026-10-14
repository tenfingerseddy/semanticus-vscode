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

        /// <summary>For a task handed over from a row that lists findings or objects (the coverage rows, the cleanup): the keys it carries
        /// (<c>rule|object</c> for a finding, an object ref for a removal). The task is done when ITS claims are gone; a finding that
        /// appears later is on a row of its own. Null for every other task.</summary>
        public string[] Claims { get; set; }

        /// <summary>The objects a found row listed when it was given (see PlanTask.Carried).</summary>
        public string[] Carried { get; set; }
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
        public const string PausedByYouNote = "You paused your assistant's tasks. Ask your assistant to run the plan, or turn them back on.";
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

        /// <summary>The claims of the open tasks handed over from a row that lists findings or objects: the work those tasks hold.</summary>
        internal static IEnumerable<string> HeldClaimsOf(IEnumerable<PlanTask> tasks) =>
            (tasks ?? Enumerable.Empty<PlanTask>())
                .Where(t => t != null && IsOpenTask(t) && t.State != PlanTaskStates.SetAside && t.OriginLink?.Kind == PlanOriginKinds.Item)
                .SelectMany(t => t.Claims ?? Array.Empty<string>());

        /// <summary>A short stable hash of a set of claims, so the same set always has the same Send key (string.GetHashCode changes per process).</summary>
        internal static string ClaimsHash(IEnumerable<string> claims)
        {
            using var sha = System.Security.Cryptography.SHA256.Create();
            var bytes = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(string.Join("\n", claims.OrderBy(c => c, StringComparer.Ordinal))));
            return BitConverter.ToString(bytes, 0, 6).Replace("-", "").ToLowerInvariant();
        }

        /// <summary>The ids of found rows a task was given from ("Give to your assistant" on a found row). The task
        /// carries the work from then on, so the row is not shown, offered or run by itself, and once done it is Done
        /// once, as the task. A task set aside lets the row come back.</summary>
        internal static HashSet<string> GivenRows(IEnumerable<PlanTask> tasks, IEnumerable<ModelPlanItem> rows = null)
        {
            var current = (rows ?? Enumerable.Empty<ModelPlanItem>()).Where(i => i?.Id != null).GroupBy(i => i.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
            var openRows = OpenRowIds(current.Values);
            return new HashSet<string>((tasks ?? Enumerable.Empty<PlanTask>())
                // A task that holds claims holds only those: the row stays, minus what the task holds (HeldClaims).
                // A task still to be done carries the row. So does one that is done and checked: it is the one Done row. Only a task that
                // said done while the gap still stands ("checking") leaves the row on the Plan, so a finding is never on no open row (D1).
                .Where(t => t != null && t.State != PlanTaskStates.SetAside && t.OriginLink?.Kind == PlanOriginKinds.Item && !string.IsNullOrWhiteSpace(t.OriginLink.Ref)
                            && (t.Claims == null || t.Claims.Length == 0)
                            && (IsOpenTask(t) || !IsChecking(t, openRows)))
                // A row that lists something the task never carried is the row's work, not the task's (D1): it shows again.
                .Where(t => t.Carried == null || t.Carried.Length == 0 || !current.TryGetValue(t.OriginLink.Ref, out var row)
                            || (row.Writes ?? Array.Empty<string>()).Where(LocalEngine.IsObjectWrite).All(w => t.Carried.Contains(w, StringComparer.Ordinal)))
                .Select(t => t.OriginLink.Ref), StringComparer.Ordinal);
        }

        /// <summary>What the tasks hide from the Plan list, worked out in ONE place so the rows, the tasks and the strip are counted from the same list
        /// (fix-coverage3, Kane 13 Oct: "Plan done must show the right number", three patches that each fixed one pairing and broke another).
        /// Each job is listed once, as a derived row OR as the task that holds it:
        ///  - <see cref="DoneEntries"/>: rows whose own Done record is not listed or counted. A task that took the row's findings holds them
        ///    (a coverage row is never stamped Done by a hand-over), and a finished claim-less task is the one Done entry.
        ///  - <see cref="Tasks"/>: tasks that said done while their gap stands. The row is on the Plan again as the job, so the task is not listed beside it.
        /// The strip's done ids leave out every id this hides.</summary>
        internal sealed class Hidden
        {
            public readonly HashSet<string> DoneEntries = new HashSet<string>(StringComparer.Ordinal);
            public readonly HashSet<string> Tasks = new HashSet<string>(StringComparer.Ordinal);
        }

        /// <param name="derived">The derived rows as they stand now, before the tasks are listed beside them.</param>
        /// <param name="passed">The ids of the tasks whose check passes on this model right now.</param>
        internal static Hidden HiddenBy(IEnumerable<PlanTask> tasks, IEnumerable<ModelPlanItem> derived, ISet<string> passed)
        {
            var hidden = new Hidden();
            var all = (tasks ?? Enumerable.Empty<PlanTask>()).Where(t => t != null).ToList();
            var open = new Dictionary<string, ModelPlanItem>(StringComparer.Ordinal);
            foreach (var i in derived ?? Enumerable.Empty<ModelPlanItem>())
                if (i?.Id != null && (i.Status == ModelPlanStatuses.Ready || i.Status == ModelPlanStatuses.Proposed)) open.TryAdd(i.Id, i);
            foreach (var t in all)
            {
                if (t.State == PlanTaskStates.SetAside || t.OriginLink?.Kind != PlanOriginKinds.Item || string.IsNullOrWhiteSpace(t.OriginLink.Ref)) continue;
                var row = t.OriginLink.Ref;
                var holds = t.Claims != null && t.Claims.Length > 0;
                if (holds || t.State == PlanTaskStates.Done) hidden.DoneEntries.Add(row);
                if (t.State != PlanTaskStates.Done || !(t.DoneWhen == PlanDoneWhen.ModelCheck || t.DoneWhen == PlanDoneWhen.HasWords)) continue;
                // Said done, not proven. A claim-less task leaves its row on the Plan while the row is open; a task that holds claims leaves it
                // only when that row lists the very findings the task held (a later finding on the row is another job).
                // Or when a newer task holds them: the second hand-over of the same findings is the job, and the first is not listed beside it.
                var checking = holds
                    ? !(passed?.Contains(t.Id) ?? false)
                      && ((open.TryGetValue(row, out var r) && Shares(LocalEngine.ClaimsOfRow(r), t.Claims))
                          || all.Any(u => u != t && IsOpenTask(u) && u.State != PlanTaskStates.SetAside && Shares(u.Claims, t.Claims)))
                    : open.ContainsKey(row);
                if (checking) hidden.Tasks.Add(t.Id);
            }
            return hidden;
        }

        /// <summary>The ids of rows whose findings or objects a task holds (a coverage or cleanup row handed over): such a row that leaves the list left because
        /// the task took its findings, which finishes nothing.</summary>
        internal static HashSet<string> RowsHeldByClaims(IEnumerable<PlanTask> tasks) =>
            new HashSet<string>((tasks ?? Enumerable.Empty<PlanTask>())
                .Where(t => t != null && t.State != PlanTaskStates.SetAside && t.OriginLink?.Kind == PlanOriginKinds.Item && !string.IsNullOrWhiteSpace(t.OriginLink.Ref)
                            && t.Claims != null && t.Claims.Length > 0)
                .Select(t => t.OriginLink.Ref), StringComparer.Ordinal);

        private static bool Shares(string[] a, string[] b) =>
            a != null && b != null && a.Intersect(b, StringComparer.OrdinalIgnoreCase).Any();

        private static HashSet<string> OpenRowIds(IEnumerable<ModelPlanItem> rows) =>
            new HashSet<string>((rows ?? Enumerable.Empty<ModelPlanItem>()).Where(i => i?.Id != null && (i.Status == ModelPlanStatuses.Ready || i.Status == ModelPlanStatuses.Proposed)).Select(i => i.Id), StringComparer.Ordinal);

        /// <summary>A done task the engine checks whose row is still open: it said done, the gap stands (the "checking" state word).</summary>
        private static bool IsChecking(PlanTask t, HashSet<string> openRows) =>
            t.State == PlanTaskStates.Done && (t.DoneWhen == PlanDoneWhen.ModelCheck || t.DoneWhen == PlanDoneWhen.HasWords)
            && openRows.Contains(t.OriginLink.Ref);

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
        internal static void StampState(ModelPlanItem item, PlanTask task, bool doneByCheck, string queue, string[] gone = null, Func<bool> assistantAway = null)
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
            // NOTHING KEPT IS NOT THE PERSON'S BY DEFAULT (Kane, 13 Oct 2026: "Needs you" beside an Approve that could not be pressed). With
            // every change unticked Semanticus has nothing to run, and choosing which to keep is a question the assistant asks in chat, then
            // keeps them with set_plan_item and approves. So the row is the assistant's, Ready, and says so. The person can still tick lines
            // on the row, or move it back to themselves.
            if (allDropped && !acceptedAll)
            {
                item.Doer = PlanDoers.Assistant;
                item.Who = WhoOf(PlanDoers.Assistant);
                item.StateNote = string.IsNullOrWhiteSpace(item.ChangeSet?.FailureReason) ? AllUntickedNote
                    : item.ChangeSet.FailureReason + " " + LocalEngine.OpenRowOnPlanSentence;
                allDropped = false;
            }
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
                    Waiting = waiting || WaitsOnJev(item),
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
            if (task == null && WaitsOnJev(item) && item.StateWord == PlanStateWords.Waiting && string.IsNullOrWhiteSpace(item.StateNote))
                item.StateNote = JevWaitNote;
            if (allDropped && item.StateWord == PlanStateWords.SetAside && item.Status != ModelPlanStatuses.Skipped) item.StateNote = AllAcceptedNote;
            else item.StateNote = gone != null && item.StateWord == PlanStateWords.NeedsYou
                ? PlanTaskObjects.GoneSentence(gone)
                : NoteFor(item, task, queue, assistantAway);
            if (item.StateWord == PlanStateWords.Waiting && (string.IsNullOrWhiteSpace(item.StateNote) || item.StateNote == PlanFootprint.OrderedByPlanNote)) item.StateNote = WaitingNote(item, task, queue, assistantAway);
            // A row the assistant leaves says why ON THE ROW (hero run 20, J2: four rows read only "Waiting, ask your assistant"). Its note
            // (set_plan_task_note) leads the state note of an open assistant row, so "Needs a date column: none exists in this model" is what the person reads.
            var assistants = (task != null ? task.Doer : item.Doer) == PlanDoers.Assistant || item.Who == ModelPlanOwners.Assistant;
            if (assistants && !string.IsNullOrWhiteSpace(item.AssistantNote)
                && (item.StateWord == PlanStateWords.Ready || item.StateWord == PlanStateWords.Waiting)
                && !(item.StateNote ?? string.Empty).StartsWith(item.AssistantNote.Trim(), StringComparison.Ordinal))
                item.StateNote = (item.AssistantNote.Trim() + " " + item.StateNote).Trim();
        }

        internal const string WaitingForAssistantNote = "Waiting for your assistant. Ask it to run the plan.";
        internal const string AskAssistantNote = "Ask your assistant to run the plan.";

        /// <summary>A Waiting row always says who it waits for and why (hero run 19, D16). An assistant row whose assistant is not
        /// connected, or whose queue the person paused (By hand), says that instead of leaving "Waiting" with no one to wait for.</summary>
        private static string WaitingNote(ModelPlanItem item, PlanTask task, string queue, Func<bool> assistantAway)
        {
            var mine = (task != null ? task.Doer : item.Doer) == PlanDoers.Assistant || item.Who == ModelPlanOwners.Assistant;
            if (mine && (queue == PlanAssistantQueue.Paused || (assistantAway?.Invoke() ?? false))) return WaitingForAssistantNote;
            var first = item.WaitsFor is { Length: > 0 } ? item.WaitsFor[0] : null;
            if (first == null) return "Waiting for " + (mine ? "your assistant" : "you") + ".";
            var who = first.Doer switch { PlanDoers.You => "you", PlanDoers.Assistant => "your assistant", PlanDoers.Semanticus => "Semanticus", _ => "another task" };
            return "Waiting for " + who + ": " + first.Title + (item.WaitsFor.Length > 1 ? " and " + (item.WaitsFor.Length - 1) + " more" : "")
                 + ". It goes after " + (item.WaitsFor.Length > 1 ? "them" : "that task") + " because they change the same things.";
        }

        /// <summary>The row that says Jev stopped before it looked at everything. Nobody works it: Jev tries again after a later change or in the next session.</summary>
        internal static bool WaitsOnJev(ModelPlanItem item) => item != null && string.Equals(item.Id, AiSchemaFamily.NotFinishedItemId, StringComparison.Ordinal);

        internal const string JevWaitNote = "Jev tries again after the next change to the model, or in the next session. There is nothing for you or your assistant to do.";

        internal const string AllAcceptedNote = "You accepted every change on this task, so Semanticus has nothing to do. Take the acceptance off to bring it back.";
        internal const string AllUntickedNote = "Every change on this task is unticked, so Run the plan skips it. Your assistant keeps the ones that make sense. You can also tick them yourself.";

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
        private static string NoteFor(ModelPlanItem item, PlanTask task, string queue, Func<bool> assistantAway = null)
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
                    // A Ready assistant row with no assistant to take it (none connected, or By hand) says what to do (hero run 19, D16).
                    // A note already written for the row (a restart, a failed line) keeps its place.
                    if ((task != null ? task.Doer : item.Doer) == PlanDoers.Assistant || (task == null && item.Who == ModelPlanOwners.Assistant))
                    {
                        if (task != null && queue == PlanAssistantQueue.Paused) return PausedByYouNote;
                        var written = task != null ? task.ReleaseNote : item.StateNote;
                        if (!string.IsNullOrWhiteSpace(written)) return written;
                        if (queue == PlanAssistantQueue.Paused) return PausedByYouNote;
                        if (assistantAway?.Invoke() ?? false) return AskAssistantNote;
                        if (task != null) return OnNextCallNote;
                    }
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

            var fromRow = task.OriginLink?.Kind == PlanOriginKinds.Item;
            var why = new List<string>
            {
                // Hero run 21, N4: a row the assistant could not do says so FIRST, and no row says the person did what they did not.
                fromRow && !string.IsNullOrWhiteSpace(task.DeclineReason)
                    ? (task.DeclinedBy == PlanDoers.You ? "You could not do this: " : "Your assistant could not do this: ") + task.DeclineReason.Trim()
                : fromRow && task.OriginLink.GivenBy == "you"
                    ? "You gave this to your assistant from the Plan on " + Day(task.CreatedUtc) + "."
                : fromRow
                    ? "Semanticus found this, and it went to your assistant on " + Day(task.CreatedUtc) + "."
                : task.Source == PlanSources.You
                    ? "You sent this from " + (jump?.Label ?? "a page") + " on " + Day(task.CreatedUtc) + "."
                    : task.Source == PlanSources.Repeat
                        ? "A repeat you set up brought this back on " + Day(task.CreatedUtc) + "."
                        : aim ? "You added this aim on " + Day(task.CreatedUtc) + "."
                        : "You added this on " + Day(task.CreatedUtc) + ".",
            };
            // A given Fix task: Review lists more than it fixes (the Info notes and findings on other tasks), as the open Fix row says.
            if (fromRow && task.OriginLink.Ref == LocalEngine.CoverageBpaItemId && !closed) why.Add(LocalEngine.ReviewAlsoListsWords);
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
                AssistantNote = task.AssistantNote,
                Reply = task.Reply,
                FullText = task.FullText,
                Claim = task.Claim == null ? null : new PlanClaimView { ClaimedBy = task.Claim.ClaimedBy, Since = task.Claim.ClaimedUtc },
                OriginLink = task.OriginLink,
                Claims = task.Claims is { Length: > 0 } ? task.Claims : null,   // the findings it holds, so the Plan can tell one job from another
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
                : task.OriginLink?.Kind == PlanOriginKinds.Item ? "Found by Semanticus."
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
                            Held = p.Value.Held, Doer = p.Value.Doer, Note = p.Value.Note, AssistantNote = p.Value.AssistantNote, Absent = p.Value.Absent,
                        }), StringComparer.Ordinal);
                    state.AssistantQueue = held.AssistantQueue;
                    state.Repeats = JsonSerializer.Deserialize<List<PlanRepeat>>(JsonSerializer.Serialize(held.Repeats, TaskCopy), TaskCopy);
                    state.ClearedDone = new List<string>(held.ClearedDone);
                    // What the plan saw and what it saw finish, so work done on an unsaved model still counts (hero20 D3).
                    state.Done = JsonSerializer.Deserialize<List<PlanDone>>(JsonSerializer.Serialize(held.Done, TaskCopy), TaskCopy);
                    state.LastItems = JsonSerializer.Deserialize<List<PlanSeenItem>>(JsonSerializer.Serialize(held.LastItems, TaskCopy), TaskCopy);
                }
                return state;
            }

            // The model got its first file: what the plan kept while it had none moves into the file, once, so the done rows and the
            // percentage carry over and do not start again at nothing (hero20 D3, the 0% after the assistant saved).
            if (_sessionPlanTasks.TryGetValue(bound.Context, out var carried))
            {
                bool any;
                lock (carried) any = carried.Done.Count > 0 || carried.LastItems.Count > 0 || carried.ClearedDone.Count > 0;
                if (any && PlanMemory.Mutate(bound.File, bound.Identity, file =>
                {
                    lock (carried)
                    {
                        foreach (var d in carried.Done) if (!file.Done.Any(x => x.ItemId == d.ItemId)) file.Done.Add(d);
                        foreach (var id in carried.ClearedDone) if (!file.ClearedDone.Contains(id)) file.ClearedDone.Add(id);
                        if (file.LastItems.Count == 0) file.LastItems = carried.LastItems.ToList();
                        carried.Done.Clear(); carried.ClearedDone.Clear(); carried.LastItems.Clear();
                    }
                }))
                    state = PlanMemory.Read(bound.File, bound.Identity);
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
        public Task<ModelPlanActionResult> AddPlanTaskAsync(PlanTaskRequest request) => AddPlanTaskAsync(request, compose: true);

        /// <summary>The same add. <paramref name="compose"/> false skips working out the whole plan to hand back (seconds on a large model) and the push to the
        /// page: a caller adding several tasks at once (Fix them) composes and pushes once at the end, and the result's Plan is empty.</summary>
        internal async Task<ModelPlanActionResult> AddPlanTaskAsync(PlanTaskRequest request, bool compose)
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
                    Source = origin?.Kind == PlanOriginKinds.Item && !string.IsNullOrWhiteSpace(origin.RowSource) ? origin.RowSource
                           : page == null || suggested ? PlanSources.Words : PlanSources.You,
                    SourcePage = page,
                    FieldPage = fieldPage,
                    Brief = brief,
                    Doer = doer,
                    Objects = named ?? objects,
                    Claims = (request.Claims ?? Array.Empty<string>()).Where(c => !string.IsNullOrWhiteSpace(c)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                    Carried = (request.Carried ?? Array.Empty<string>()).Where(c => !string.IsNullOrWhiteSpace(c)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                    Grounds = grounds,
                    DoneWhen = doneWhen,
                    Slot = slot,
                    OriginLink = new PlanOriginLink
                    {
                        Kind = origin?.Kind ?? (page == null ? PlanOriginKinds.Words : PlanOriginKinds.Page),
                        Ref = origin != null ? origin.Ref : page,
                        SendKey = sendKey,
                        GivenBy = origin?.GivenBy,
                        RowSource = origin?.RowSource,
                    },
                    Order = doer == PlanDoers.Assistant ? PlanTasks.NextOrder(memory) : (int?)null,
                    CreatedUtc = DateTimeOffset.UtcNow,
                    State = PlanTaskStates.Open,
                });
            });

            var plan = compose ? await ComposeAsync(null, bound).ConfigureAwait(false) : null;
            if (compose) TellThePlanMoved(bound);
            return new ModelPlanActionResult
            {
                ItemId = id,
                Action = "add",
                Performed = false,
                Revision = plan?.Revision ?? bound.Session.Revision,
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

        /// <summary>One note on a row, or none. The person's and the assistant's are kept apart: each writer sets and clears only its own.</summary>
        public Task<ModelPlanView> SetPlanTaskNoteAsync(string id, string text, string origin = "human")
        {
            var note = string.IsNullOrWhiteSpace(text) ? null : text.Trim();
            if (note != null && note.Length > PlanTasks.MaxNoteChars)
                throw new ArgumentException("That note is longer than " + PlanTasks.MaxNoteChars + " characters.", nameof(text));
            if (string.Equals(origin, "agent", StringComparison.Ordinal))
                return ChangeRowAsync(id, task => task.AssistantNote = note, residue => residue.AssistantNote = note);
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
                return (await GiveRowToAssistantAsync(key, string.Equals(origin, "agent", StringComparison.Ordinal) ? "assistant" : "you").ConfigureAwait(false)).Plan;

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
                    task.DeclineReason = null; task.DeclinedBy = null;   // a new claim cycle: no claim, no reason, a place in the order
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

        /// <summary>A row's Writes entry that names an object (a "table:", "column:" ... ref), not the model itself.</summary>
        internal static bool IsObjectWrite(string w) =>
            !string.IsNullOrWhiteSpace(w) && w.Contains(':') && !w.StartsWith("model:", StringComparison.OrdinalIgnoreCase);

        /// <summary>A found or Jev row, given to the assistant, becomes one task linked back to it (origin "item"). The
        /// task names what the row names, carries the row's own words as its brief, and is checked the way the row is:
        /// descriptions by their words on the named objects, anything else by the row's gap being gone. Giving the same
        /// row again while its task is open is that task (the Send key).</summary>
        private async Task<ModelPlanActionResult> GiveRowToAssistantAsync(string key, string givenBy = "you")
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
                .Where(IsObjectWrite)
                .Distinct(StringComparer.Ordinal).ToArray();
            var words = row.Id.StartsWith("describe/", StringComparison.Ordinal) && objects.Length > 0;
            // The words row's task names the one call that hands over the whole list, first, so no page limit on the
            // brief can cut it: its diff lists only the first objects (verify, 28 Sep, M6).
            // The row's own Brief (Done when / Default, set by AssistantDefaults) goes right after the title, so the assistant that takes the row by
            // hand-over or through Run the plan reads when it is done and the choice to make (hands-off verifier line).
            var brief = string.Join("\n", new[] { row.Title + "." }
                .Concat(string.IsNullOrWhiteSpace(row.Brief) ? Array.Empty<string>() : new[] { row.Brief })
                .Concat(IsWordsItem(row.Id) ? new[] { WordsTaskLine } : Array.Empty<string>())
                .Concat(row.Why ?? Array.Empty<string>())
                .Concat((row.Diff ?? Array.Empty<string>()).Take(40))
                .Where(x => !string.IsNullOrWhiteSpace(x)));
            var title = row.Title ?? row.Id;
            var claims = ClaimsOfRow(row);
            return await AddPlanTaskAsync(new PlanTaskRequest
            {
                Words = title.Length <= PlanTasks.MaxWordsChars ? title : title.Substring(0, PlanTasks.MaxWordsChars - 3).TrimEnd() + "...",
                Doer = PlanDoers.Assistant,
                SourcePage = row.Jump?.Tool,
                Brief = brief.Length <= PlanTasks.MaxBriefChars ? brief : brief.Substring(0, PlanTasks.MaxBriefChars),
                Objects = objects,
                DoneWhen = words ? PlanDoneWhen.HasWords : PlanDoneWhen.ModelCheck,
                OriginLink = new PlanOriginLink { Kind = PlanOriginKinds.Item, Ref = row.Id, GivenBy = givenBy, RowSource = row.Source },
                // The coverage rows and the cleanup keep one id while their contents change, so a second hand-over (new findings after
                // the first) is a second task: the key carries what the task holds.
                Claims = claims,
                Carried = claims.Length == 0 ? objects : null,
                SendKey = "item:" + row.Id + (claims.Length == 0 ? "" : "#" + PlanTasks.ClaimsHash(claims)),
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
