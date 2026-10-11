// Repeat cards (Plan at the heart, lane RP; contract section 1.7, Kane 24 Sep: "yes agree").
//
// A REPEAT IS A SAVED CARD WITH A CADENCE. It lives in the Plan's own file beside the tasks, under the model's
// durable identity, with the same lock, temp file and move discipline (PlanMemory.Mutate).
//
// IT COMES DUE ON A COMPOSE, NEVER ON A TIMER. There is no background thread and no daemon: the check runs when
// the plan is composed, which is every time a model is opened onto the Plan and every time either door reads it.
// If nothing reads the plan, nothing comes due, and the card shows up at the next read.
//
// ONE CARD PER DUE DATE, AND MISSED PERIODS MAKE ONE CARD. When a compose runs at or after NextDueUtc the engine
// adds ONE occurrence, an ordinary PlanTask with Source "repeat" and RepeatId naming the repeat, and moves
// NextDueUtc to the first date on the cadence that is after now, in the same atomic write. So a model opened
// five weeks late gets one card, not five, and a restart can never add the card twice: the file either has the
// card and the moved date, or neither.
//
// AN OPEN OCCURRENCE BLOCKS THE NEXT ONE. A due date that passes while the last card is still open adds nothing;
// its period is absorbed by the card already waiting, and the date moves on. The next card comes only after that
// one is done or set aside AND the next date has passed.
//
// NOTHING RUNS BY ITSELF. The card is a row like any other: an assistant card is offered on its next call, a
// person's card waits for them, a workflow card waits for a person to press Start. A card coming due is not a run
// starting (composer 12), and nothing in the model changes because a date passed.
//
// PRO. Creating and editing a repeat, and the due check itself, are Pro (the Workflows group,
// Entitlement/FeatureMap.cs). On free the saved repeats stop coming due and the Plan says why; open cards stay as
// ordinary rows, and a person can still delete a repeat.
//
// PERSON-ONLY to create, edit, pause and delete (contract 12): the three controls are on HumanGovernanceRpcTarget
// and on no MCP tool. Both doors READ them: get_model_plan lists every repeat and its next due date.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;

namespace Semanticus.Engine
{
    /// <summary>How often a repeat comes due (contract 1.7). "Every" widens each to every N of it, so "every 2
    /// weeks" is weekly with Every = 2 (the stock "Update model notes" repeat, brainstorm section 6).</summary>
    public static class PlanCadences
    {
        public const string Daily = "daily";
        public const string Weekly = "weekly";
        public const string Monthly = "monthly";

        public static readonly string[] All = { Daily, Weekly, Monthly };

        public static string Normalise(string kind) =>
            All.FirstOrDefault(k => string.Equals(k, (kind ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A cadence: daily, weekly on a day, or monthly on a date, every N of them.</summary>
    public sealed class PlanRepeatCadence
    {
        /// <summary>One of <see cref="PlanCadences"/>.</summary>
        public string Kind { get; set; }

        /// <summary>Every N days, weeks or months. 1 to 12.</summary>
        public int Every { get; set; } = 1;

        /// <summary>For weekly: the day, "monday" to "sunday".</summary>
        public string Day { get; set; }

        /// <summary>For monthly: the date, 1 to 31. A month shorter than the date comes due on its last day.</summary>
        public int? Date { get; set; }
    }

    /// <summary>One saved repeat, as stored in the Plan's own file.</summary>
    public sealed class PlanRepeat
    {
        /// <summary>"rp-" and 8 lowercase hex from a cryptographic source.</summary>
        public string Id { get; set; }

        /// <summary>The card's title, 280 characters or fewer.</summary>
        public string Words { get; set; }

        /// <summary>A saved workflow's name, or null. A workflow card waits for someone to press Start.</summary>
        public string Workflow { get; set; }

        /// <summary>A found row the engine can apply, by its exact id ("safefix/Visibility"), or null. A fix repeat
        /// mints no card of its own: when it comes due and that row is open, the row itself is marked as the
        /// repeat's card, and Run the plan applies it unchanged (see <see cref="PlanRepeats"/>).</summary>
        public string Fix { get; set; }

        /// <summary>The found row marked as this fix repeat's open card, or null. Latched, never recomputed: it
        /// closes when that row is done, set aside or gone.</summary>
        public string OpenFix { get; set; }

        /// <summary>You or Your assistant for words and workflows; Semanticus for a fix.</summary>
        public string Doer { get; set; }

        /// <summary>How each card's done is known: the doer's own default.</summary>
        public string DoneWhen { get; set; }

        /// <summary>The page the card's work is done on, as a tool id, or null.</summary>
        public string Page { get; set; }

        /// <summary>The stock repeat this was made from, or null.</summary>
        public string Stock { get; set; }

        public PlanRepeatCadence Cadence { get; set; }
        public DateTimeOffset NextDueUtc { get; set; }

        /// <summary>Paused: it does not come due until it is resumed.</summary>
        public bool Held { get; set; }
        public DateTimeOffset CreatedUtc { get; set; }

        /// <summary>When the last card came due, or null before the first.</summary>
        public DateTimeOffset? LastCardUtc { get; set; }

        /// <summary>The task it was made from with Repeat this, or null (UAT S12-6-003). Its row shows the repeat, and
        /// while that task is set aside the repeat rests.</summary>
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public string FromTask { get; set; }

        /// <summary>The objects its cards are about, taken from that task, and the ones of those that were in the model
        /// (UAT S12-6-002). While one of those is gone the repeat rests.</summary>
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public string[] Objects { get; set; }

        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public PlanObjectGround[] Grounds { get; set; }
    }

    /// <summary>What a person asks for when they add or change a repeat. On a change, a null field keeps what the
    /// repeat already has.</summary>
    public sealed class PlanRepeatRequest
    {
        public string Words { get; set; }
        public string Workflow { get; set; }

        /// <summary>A found row's exact id, for a repeat of a Semanticus fix. The row must be on the plan now, done
        /// by Semanticus.</summary>
        public string Fix { get; set; }
        public string Doer { get; set; }
        public PlanRepeatCadence Cadence { get; set; }

        /// <summary>One of the stock repeats' ids ("model-notes", "ai-instructions"). Fills in everything the
        /// request leaves out.</summary>
        public string Stock { get; set; }

        /// <summary>Pause (true) or resume (false). Only on a change.</summary>
        public bool? Held { get; set; }

        /// <summary>The task this is made from, with Repeat this on its row. Only on an add of a words repeat.</summary>
        public string FromTask { get; set; }
    }

    /// <summary>One repeat as both doors read it on the plan.</summary>
    public sealed class PlanRepeatView
    {
        public string Id { get; set; }
        public string Words { get; set; }
        public string Workflow { get; set; }
        public string Fix { get; set; }
        public string Doer { get; set; }
        public string Stock { get; set; }
        public PlanRepeatCadence Cadence { get; set; }

        /// <summary>The cadence in words: "Every 2 weeks, on Monday".</summary>
        public string CadenceText { get; set; }
        public DateTimeOffset NextDueUtc { get; set; }
        public bool Held { get; set; }

        /// <summary>The id of its card that is still open, or null: a task for words and workflows, the found row for
        /// a fix. While there is one, no next card is added.</summary>
        public string OpenItemId { get; set; }

        /// <summary>The task it was made from, whose row shows it, or null.</summary>
        public string FromItemId { get; set; }

        /// <summary>Why it does not come due now though it is not paused, in one sentence, or null: its task is set
        /// aside, or an object it is about is gone.</summary>
        public string Resting { get; set; }
    }

    /// <summary>A stock repeat a person can add with one click.</summary>
    public sealed class PlanRepeatStarter
    {
        public string Stock { get; set; }
        public string Words { get; set; }
        public string Doer { get; set; }
        public string Page { get; set; }
        public PlanRepeatCadence Cadence { get; set; }
        public string CadenceText { get; set; }
    }

    public static class PlanRepeats
    {
        public const string IdPrefix = "rp-";
        public const int MaxEvery = 12;

        /// <summary>How many repeats one model keeps. An add past it is refused.</summary>
        public const int MaxRepeats = 50;

        /// <summary>Said on the plan, on free, when saved repeats exist (contract 1.7).</summary>
        public const string ProLine =
            "Repeats are part of Semanticus Pro. Yours are saved, and they come due again when Pro is back.";

        /// <summary>The note on a workflow card: it waits, and a person starts it.</summary>
        public const string StartItNote = "Press Start on Workflows. Nothing runs by itself.";

        /// <summary>The note on a found row a fix repeat has marked as its card.</summary>
        public const string FixMarkNote = "Your repeat brought this back.";

        public const string StockModelNotes = "model-notes";
        public const string StockAiInstructions = "ai-instructions";

        /// <summary>The two stock repeats (brainstorm section 6). Offered, never added for the person: a card
        /// nobody set up would be work the product invented for them.</summary>
        public static readonly PlanRepeatStarter[] Starters =
        {
            new PlanRepeatStarter
            {
                Stock = StockModelNotes, Words = "Update model notes", Doer = PlanDoers.Assistant, Page = "knowledge",
                Cadence = new PlanRepeatCadence { Kind = PlanCadences.Weekly, Every = 2 },
            },
            new PlanRepeatStarter
            {
                Stock = StockAiInstructions, Words = "Review AI instructions", Doer = PlanDoers.You, Page = "instructions",
                Cadence = new PlanRepeatCadence { Kind = PlanCadences.Monthly, Every = 1 },
            },
        };

        /// <summary>The found-row families a fix repeat may name: rows the engine works out and applies itself.
        /// Not an aim, not an assistant's own row, not a Jev row (those go when Jev's lines go).</summary>
        internal static readonly string[] FixFamilies =
            { "safefix/", "bpafix/", "describe/", "cleanup/", "recheck/", "ship/", "proposal/", "datetable/", "calendar/", "starter/", "check/" };

        private static readonly string[] Days = { "sunday", "monday", "tuesday", "wednesday", "thursday", "friday", "saturday" };

        public static PlanRepeatStarter StarterFor(string stock) =>
            Starters.FirstOrDefault(s => string.Equals(s.Stock, (stock ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase));

        /// <summary>"rp-" and 8 lowercase hex, and no repeat in this state has it already.</summary>
        public static string NewIdFor(PlanMemoryState state)
        {
            while (true)
            {
                var id = IdPrefix + Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();
                if (state?.Repeats == null || !state.Repeats.Any(r => string.Equals(r.Id, id, StringComparison.Ordinal))) return id;
            }
        }

        public static PlanRepeat Find(PlanMemoryState state, string id) =>
            string.IsNullOrWhiteSpace(id) || state?.Repeats == null ? null
            : state.Repeats.FirstOrDefault(r => string.Equals(r.Id, id.Trim(), StringComparison.Ordinal));

        // ---- the cadence ---------------------------------------------------------------------------------------

        /// <summary>The cadence checked and filled: a weekly with no day takes the day of <paramref name="today"/>,
        /// a monthly with no date takes its date. Throws with a sentence a person can act on.</summary>
        public static PlanRepeatCadence Normalise(PlanRepeatCadence cadence, DateTime today)
        {
            if (cadence == null)
                throw new ArgumentException("Say how often it repeats: daily, weekly on a day, or monthly on a date.", nameof(cadence));
            var kind = PlanCadences.Normalise(cadence.Kind)
                ?? throw new ArgumentException("A repeat is daily, weekly or monthly.", nameof(cadence));
            if (cadence.Every < 1 || cadence.Every > MaxEvery)
                throw new ArgumentException("A repeat comes every 1 to " + MaxEvery + " days, weeks or months.", nameof(cadence));

            var clean = new PlanRepeatCadence { Kind = kind, Every = cadence.Every };
            if (kind == PlanCadences.Weekly)
            {
                var day = string.IsNullOrWhiteSpace(cadence.Day) ? Days[(int)today.DayOfWeek] : cadence.Day.Trim().ToLowerInvariant();
                if (!Days.Contains(day))
                    throw new ArgumentException("A weekly repeat names its day, Monday to Sunday.", nameof(cadence));
                clean.Day = day;
            }
            else if (kind == PlanCadences.Monthly)
            {
                var date = cadence.Date ?? today.Day;
                if (date < 1 || date > 31)
                    throw new ArgumentException("A monthly repeat names its date, 1 to 31.", nameof(cadence));
                clean.Date = date;
            }
            return clean;
        }

        private static DateTime LocalDate(DateTimeOffset moment, TimeZoneInfo zone) =>
            TimeZoneInfo.ConvertTime(moment, zone ?? TimeZoneInfo.Local).Date;

        /// <summary>The start of a day in the zone, as a moment.</summary>
        private static DateTimeOffset StartOf(DateTime date, TimeZoneInfo zone)
        {
            var local = DateTime.SpecifyKind(date.Date, DateTimeKind.Unspecified);
            return new DateTimeOffset(local, (zone ?? TimeZoneInfo.Local).GetUtcOffset(local)).ToUniversalTime();
        }

        private static DateTime MonthDate(int year, int month, int date) =>
            new DateTime(year, month, Math.Min(date, DateTime.DaysInMonth(year, month)));

        /// <summary>One whole period on from a due date. A monthly steps from the month, not the day, so the 31st
        /// after a 28th is the 31st again.</summary>
        private static DateTime Step(PlanRepeatCadence c, DateTime due)
        {
            switch (c.Kind)
            {
                case PlanCadences.Daily: return due.AddDays(c.Every);
                case PlanCadences.Weekly: return due.AddDays(7 * c.Every);
                default:
                    var month = new DateTime(due.Year, due.Month, 1).AddMonths(c.Every);
                    return MonthDate(month.Year, month.Month, c.Date ?? due.Day);
            }
        }

        /// <summary>The first due moment after <paramref name="now"/>, at the start of the day in <paramref name="zone"/>.
        /// Today counts as the start of the first period: a repeat set up on its own day comes due one whole period
        /// later, one set up for another day comes due on the next such day.</summary>
        public static DateTimeOffset First(PlanRepeatCadence cadence, DateTimeOffset now, TimeZoneInfo zone)
        {
            var c = Normalise(cadence, LocalDate(now, zone));
            var today = LocalDate(now, zone);
            DateTime first;
            switch (c.Kind)
            {
                case PlanCadences.Daily:
                    first = today.AddDays(c.Every);
                    break;
                case PlanCadences.Weekly:
                    var wanted = Array.IndexOf(Days, c.Day);
                    var ahead = (wanted - (int)today.DayOfWeek + 7) % 7;
                    first = ahead == 0 ? today.AddDays(7 * c.Every) : today.AddDays(ahead);
                    break;
                default:
                    var thisMonth = MonthDate(today.Year, today.Month, c.Date.Value);
                    first = thisMonth > today ? thisMonth
                          : thisMonth == today ? Step(c, thisMonth)
                          : Step(new PlanRepeatCadence { Kind = c.Kind, Every = 1, Date = c.Date }, thisMonth);
                    break;
            }
            return StartOf(first, zone);
        }

        /// <summary>The first due moment on the cadence after <paramref name="now"/>, stepping from <paramref name="due"/>.
        /// Missed periods are stepped over, so the answer is always in the future and one late read makes one card.</summary>
        public static DateTimeOffset NextAfter(PlanRepeatCadence cadence, DateTimeOffset due, DateTimeOffset now, TimeZoneInfo zone)
        {
            var c = Normalise(cadence, LocalDate(due, zone));
            var d = Step(c, LocalDate(due, zone));
            while (StartOf(d, zone) <= now) d = Step(c, d);
            return StartOf(d, zone);
        }

        public static string CadenceText(PlanRepeatCadence cadence)
        {
            if (cadence == null) return string.Empty;
            var n = cadence.Every < 1 ? 1 : cadence.Every;
            string every(string one, string many) => n == 1 ? "Every " + one : "Every " + n.ToString(CultureInfo.InvariantCulture) + " " + many;
            switch (PlanCadences.Normalise(cadence.Kind))
            {
                case PlanCadences.Daily:
                    return every("day", "days");
                case PlanCadences.Weekly:
                    var day = string.IsNullOrWhiteSpace(cadence.Day) ? null
                        : char.ToUpperInvariant(cadence.Day[0]) + cadence.Day.Substring(1).ToLowerInvariant();
                    return every("week", "weeks") + (day == null ? string.Empty : ", on " + day);
                case PlanCadences.Monthly:
                    if (cadence.Date == null) return every("month", "months");
                    var date = cadence.Date.Value;
                    return every("month", "months") + ", on the " + Ordinal(date) + (date >= 29 ? " or the last day" : string.Empty);
                default:
                    return string.Empty;
            }
        }

        internal static string LowerFirst(string s) =>
            string.IsNullOrEmpty(s) ? s : char.ToLowerInvariant(s[0]) + s.Substring(1);

        private static string Ordinal(int n)
        {
            var suffix = (n % 100) is 11 or 12 or 13 ? "th"
                       : (n % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" };
            return n.ToString(CultureInfo.InvariantCulture) + suffix;
        }

        // ---- coming due ---------------------------------------------------------------------------------------

        /// <summary>True while a card of this words or workflow repeat is still open: not done, not set aside.</summary>
        public static bool HasOpenCard(PlanMemoryState state, string repeatId) =>
            state?.Tasks != null && state.Tasks.Any(t => string.Equals(t.RepeatId, repeatId, StringComparison.Ordinal)
                                                        && PlanTasks.IsOpenTask(t) && state.Find(t.Id) == null);

        /// <summary>True when a words or workflow repeat is due now: the cheap test before a write. A fix repeat is
        /// settled after the rows are known (<see cref="SettleFixes"/>).</summary>
        public static bool AnyDue(PlanMemoryState state, DateTimeOffset now, IReadOnlyDictionary<string, PlanGroundNow> grounds = null) =>
            state?.Repeats != null && state.Repeats.Any(r => r.Fix == null && !r.Held && now >= r.NextDueUtc
                                                             && RestingWhy(state, r, grounds) == null);

        /// <summary>Why a repeat that is not paused does not come due now, or null (UAT S12-6-003 and S12-6-002). It
        /// rests, rather than being paused or deleted, so putting the task back or the object coming back starts it
        /// again with nothing for the person to redo. Its date does not move while it rests, as while paused.</summary>
        public static string RestingWhy(PlanMemoryState state, PlanRepeat r, IReadOnlyDictionary<string, PlanGroundNow> grounds)
        {
            if (r?.FromTask != null)
            {
                var from = state?.FindTask(r.FromTask);
                if (from != null && (from.State == PlanTaskStates.SetAside || state.Find(from.Id) != null))
                    return "Its task is set aside, so it does not come due. Put the task back, or stop repeating.";
            }
            var gone = grounds == null ? Array.Empty<string>() : LocalEngine.GoneOf(r?.Grounds, grounds);
            return gone.Length == 0 ? null : PlanTaskObjects.GoneSentence(gone).TrimEnd('.') + ", so it does not come due.";
        }

        /// <summary>The due check for words and workflow repeats over one model's state. Adds at most one card per
        /// repeat and moves each due repeat's date past now. An open card absorbs the period. Returns how many
        /// cards were added.</summary>
        public static int ComeDue(PlanMemoryState state, DateTimeOffset now, TimeZoneInfo zone,
                                  IReadOnlyDictionary<string, PlanGroundNow> grounds = null)
        {
            if (state?.Repeats == null) return 0;
            var added = 0;
            foreach (var r in state.Repeats.Where(r => r.Fix == null && !r.Held && now >= r.NextDueUtc).ToList())
            {
                if (RestingWhy(state, r, grounds) != null) continue;
                if (!HasOpenCard(state, r.Id) && state.Tasks.Count(PlanTasks.IsOpenTask) < PlanMemory.MaxOpenTasks)
                {
                    state.Tasks.Add(CardFor(state, r, now));
                    r.LastCardUtc = now;
                    added++;
                }
                r.NextDueUtc = NextAfter(r.Cadence, r.NextDueUtc, now, zone);
            }
            return added;
        }

        private static PlanTask CardFor(PlanMemoryState state, PlanRepeat r, DateTimeOffset now) => new PlanTask
        {
            Id = PlanTasks.NewIdFor(state),
            Words = r.Words,
            Source = PlanSources.Repeat,
            SourcePage = r.Page,
            Doer = r.Doer,
            DoneWhen = r.DoneWhen ?? PlanTasks.DefaultDoneWhen(r.Doer),
            OriginLink = r.Page == null
                ? new PlanOriginLink { Kind = PlanOriginKinds.Words }
                : new PlanOriginLink { Kind = PlanOriginKinds.Page, Ref = r.Page },
            Order = r.Doer == PlanDoers.Assistant ? PlanTasks.NextOrder(state) : (int?)null,
            CreatedUtc = now,
            State = PlanTaskStates.Open,
            RepeatId = r.Id,
            // The objects of the task it was made from, so the card is about the same thing (UAT S12-6-002).
            Objects = r.Objects ?? Array.Empty<string>(),
            Grounds = r.Grounds,
        };

        /// <summary>A found row a fix repeat could mark: open, not proven yet either way, not set aside.</summary>
        private static bool IsMarkable(ModelPlanItem i) =>
            i != null && PlanStateWords.IsOpen(i.StateWord);

        /// <summary>The due check for fix repeats, run once the rows and their state words are known. A fix repeat
        /// mints no row: its card is the found row it names. So when it comes due and that row is open, the row is
        /// latched as its open card; when the row is done, set aside or gone, the card closes. Nothing open when it
        /// comes due means nothing to do this time: the date moves and no card is made, so the strip never counts
        /// a Done that took no work. Returns true when anything changed and must be written.</summary>
        public static bool SettleFixes(List<PlanRepeat> repeats, IReadOnlyList<ModelPlanItem> items, DateTimeOffset now,
                                       TimeZoneInfo zone, bool pro)
        {
            var changed = false;
            foreach (var r in (repeats ?? new List<PlanRepeat>()).Where(r => r.Fix != null))
            {
                var row = items?.FirstOrDefault(i => string.Equals(i.Id, r.Fix, StringComparison.Ordinal));
                if (r.OpenFix != null && (row == null || row.StateWord == PlanStateWords.Done || row.StateWord == PlanStateWords.SetAside))
                {
                    r.OpenFix = null;
                    changed = true;
                }
                if (!pro || r.Held || now < r.NextDueUtc) continue;
                if (r.OpenFix == null && IsMarkable(row))
                {
                    r.OpenFix = r.Fix;
                    r.LastCardUtc = now;
                }
                r.NextDueUtc = NextAfter(r.Cadence, r.NextDueUtc, now, zone);
                changed = true;
            }
            return changed;
        }

        /// <summary>What both doors read about one repeat.</summary>
        internal static PlanRepeatView ViewOf(PlanMemoryState state, PlanRepeat r,
                                              IReadOnlyDictionary<string, PlanGroundNow> grounds = null) => new PlanRepeatView
        {
            FromItemId = r.FromTask,
            Resting = r.Held ? null : RestingWhy(state, r, grounds),
            Id = r.Id,
            Words = r.Words,
            Workflow = r.Workflow,
            Fix = r.Fix,
            Doer = r.Doer,
            Stock = r.Stock,
            Cadence = r.Cadence,
            CadenceText = CadenceText(r.Cadence),
            NextDueUtc = r.NextDueUtc,
            Held = r.Held,
            OpenItemId = r.Fix != null ? r.OpenFix
                : state.Tasks.FirstOrDefault(t => string.Equals(t.RepeatId, r.Id, StringComparison.Ordinal)
                                                  && PlanTasks.IsOpenTask(t) && state.Find(t.Id) == null)?.Id,
        };

        /// <summary>The repeat's facts on the rows: the cadence under a card's title, the start note on a workflow
        /// card, and the mark on a found row a fix repeat latched. Called after the state words are stamped, so a
        /// note set here is the last word, and a row that waits keeps its "Waits for".</summary>
        internal static void Stamp(ModelPlanView view, PlanMemoryState state)
        {
            foreach (var item in view.Items)
            {
                if (item.Source == PlanSources.Repeat && PlanTasks.IsTaskId(item.Id))
                {
                    var task = state.FindTask(item.Id);
                    var r = Find(state, task?.RepeatId);
                    var waits = item.WaitsFor != null && item.WaitsFor.Length > 0;
                    if (!waits && item.Sub != PlanTasks.NotKeptSentence)
                        item.Sub = r == null ? "Its repeat was deleted." : CadenceText(r.Cadence) + ".";
                    if (r?.Workflow != null && item.StateWord == PlanStateWords.NeedsYou && string.IsNullOrWhiteSpace(task?.DeclineReason))
                        item.StateNote = StartItNote;
                    continue;
                }
                var fix = state.Repeats.FirstOrDefault(x => x.OpenFix != null && string.Equals(x.OpenFix, item.Id, StringComparison.Ordinal));
                if (fix == null) continue;
                item.Why = (item.Why ?? Array.Empty<string>())
                    .Concat(new[] { "A repeat you set up brought this back: " + LowerFirst(CadenceText(fix.Cadence)) + "." })
                    .ToArray();
                if (string.IsNullOrWhiteSpace(item.StateNote) && item.StateWord == PlanStateWords.Ready) item.StateNote = FixMarkNote;
            }
        }
    }
}

namespace Semanticus.Engine
{
    // ---- the person's repeat controls and the due check in the compose (contract 1.7 and 12) ------------------

    public sealed partial class LocalEngine
    {
        /// <summary>The clock the due check reads. The engine machine's own clock, which is the person's own
        /// machine; a test moves it past a due date instead of waiting a week.</summary>
        internal Func<DateTimeOffset> PlanRepeatClock { get; set; } = () => DateTimeOffset.UtcNow;

        /// <summary>The time zone a cadence's days are counted in: the person's own.</summary>
        internal TimeZoneInfo PlanRepeatZone { get; set; } = TimeZoneInfo.Local;

        private bool RepeatsArePro => Entitlement.FeatureGrants.IsProTier(_entitlement);

        private static string RepeatDay(DateTimeOffset when, TimeZoneInfo zone) =>
            TimeZoneInfo.ConvertTime(when, zone ?? TimeZoneInfo.Local).ToString("d MMMM yyyy", CultureInfo.InvariantCulture);

        /// <summary>Save a repeat. Pro, person-only. A words repeat brings back a task for its doer; a workflow repeat
        /// brings back a card that waits for Start; a fix repeat marks the found row it names when it comes due.
        /// The first card comes due one period on, never at once: saving a repeat is not asking for the work now.</summary>
        public async Task<ModelPlanActionResult> AddPlanRepeatAsync(PlanRepeatRequest request)
        {
            RequireProFeature();
            if (request == null) throw new ArgumentException("Say what repeats and how often.", nameof(request));

            var stock = string.IsNullOrWhiteSpace(request.Stock) ? null : PlanRepeats.StarterFor(request.Stock)
                ?? throw new ArgumentException("There is no stock repeat called '" + request.Stock.Trim() + "'. The stock repeats are: "
                    + string.Join(", ", PlanRepeats.Starters.Select(s => s.Stock)) + ".", nameof(request));

            var bound = BindPlanMemory();
            var now = PlanRepeatClock();
            var zone = PlanRepeatZone;
            var cadence = PlanRepeats.Normalise(request.Cadence ?? stock?.Cadence, TimeZoneInfo.ConvertTime(now, zone).Date);
            var fixId = string.IsNullOrWhiteSpace(request.Fix) ? null : request.Fix.Trim();
            var workflow = string.IsNullOrWhiteSpace(request.Workflow) ? null : request.Workflow.Trim();
            if (fixId != null && workflow != null)
                throw new ArgumentException("A repeat is a fix or a workflow, not both.", nameof(request));

            string words = string.IsNullOrWhiteSpace(request.Words) ? stock?.Words : request.Words.Trim();
            // Repeat this on a task's row: the repeat belongs to that task (UAT S12-6-003) and is about its objects.
            var fromId = string.IsNullOrWhiteSpace(request.FromTask) ? null : request.FromTask.Trim();
            PlanTask from = null;
            if (fromId != null)
            {
                if (fixId != null || workflow != null || stock != null)
                    throw new ArgumentException("Only a repeat of words is made from a task.", nameof(request));
                from = ReadPlanState(bound).FindTask(fromId)
                    ?? throw new ArgumentException("No task on the plan has that id. It may have been removed.", nameof(request));
                words ??= from.Words;
            }
            string doer, page = stock?.Page;
            if (fixId != null)
            {
                doer = request.Doer == null ? PlanDoers.Semanticus : PlanDoers.Normalise(request.Doer);
                if (doer != PlanDoers.Semanticus)
                    throw new ArgumentException("A repeated fix is done by Semanticus, on Run the plan. To do it yourself, "
                        + "repeat it as words instead.", nameof(request));
                var row = await FixRowAsync(bound, fixId).ConfigureAwait(false);
                words ??= row.Title;
                page = row.Jump?.Tool;
            }
            else
            {
                if (workflow != null)
                {
                    var def = LoadWorkflowDefs().FirstOrDefault(d => d.Error == null
                                  && string.Equals(d.Name, workflow, StringComparison.OrdinalIgnoreCase))
                        ?? throw new ArgumentException("There is no saved workflow called '" + workflow + "'. Save it on "
                            + "Workflows first, then set it to repeat.", nameof(request));
                    workflow = def.Name;
                    words ??= "Run " + (string.IsNullOrWhiteSpace(def.Title) ? def.Name : def.Title);
                    page = "workflows";
                }
                doer = request.Doer == null ? (stock?.Doer ?? (workflow != null ? PlanDoers.You : PlanDoers.Assistant))
                                            : PlanDoers.Normalise(request.Doer);
                if (doer == null) throw new ArgumentException("The doer is 'assistant' or 'you'.", nameof(request));
                if (doer == PlanDoers.Semanticus) throw new ArgumentException(DoerSemanticusRefusal, nameof(request));
            }
            if (string.IsNullOrWhiteSpace(words))
                throw new ArgumentException("Say what the repeat is, in your own words.", nameof(request));
            if (words.Length > PlanTasks.MaxWordsChars)
                throw new ArgumentException("That repeat is longer than " + PlanTasks.MaxWordsChars
                    + " characters. Shorten it to one line.", nameof(request));

            string id = null;
            var first = PlanRepeats.First(cadence, now, zone);
            WritePlanTasks(bound, memory =>
            {
                if (memory.Repeats.Count >= PlanRepeats.MaxRepeats)
                    throw new InvalidOperationException("This plan already has " + PlanRepeats.MaxRepeats
                        + " repeats. Delete one before adding another.");
                if (fixId != null && memory.Repeats.Any(r => string.Equals(r.Fix, fixId, StringComparison.Ordinal)))
                    throw new ArgumentException("That fix already repeats. Change the repeat you have instead.", nameof(request));
                id = PlanRepeats.NewIdFor(memory);
                memory.Repeats.Add(new PlanRepeat
                {
                    Id = id,
                    Words = words,
                    Workflow = workflow,
                    Fix = fixId,
                    Doer = doer,
                    DoneWhen = fixId != null ? PlanDoneWhen.ModelCheck : PlanTasks.DefaultDoneWhen(doer),
                    Page = page,
                    Stock = stock?.Stock,
                    Cadence = cadence,
                    NextDueUtc = first,
                    CreatedUtc = now,
                    FromTask = from?.Id,
                    Objects = from?.Objects is { Length: > 0 } ? from.Objects : null,
                    Grounds = from?.Grounds,
                });
            });

            var plan = await ComposeAsync(null, bound).ConfigureAwait(false);
            TellThePlanMoved(bound);
            return new ModelPlanActionResult
            {
                ItemId = id,
                Action = "add_repeat",
                Performed = false,
                Revision = plan.Revision,
                Plan = plan,
                Sentence = "Saved. It repeats " + PlanRepeats.LowerFirst(PlanRepeats.CadenceText(cadence)) + ". The first one comes due on "
                    + RepeatDay(first, zone) + ". Nothing runs by itself."
                    + (bound.Nowhere ? " " + PlanTasks.NotKeptSentence : string.Empty),
            };
        }

        /// <summary>The found row a fix repeat names: on the plan now, open, done by Semanticus, in a family the engine
        /// works out and applies itself.</summary>
        private async Task<ModelPlanItem> FixRowAsync(PlanMemoryBinding bound, string fixId)
        {
            var plan = await ComposeAsync(null, bound).ConfigureAwait(false);
            var row = plan.Items.FirstOrDefault(i => string.Equals(i.Id, fixId, StringComparison.Ordinal));
            if (row == null || !PlanRepeats.FixFamilies.Any(p => fixId.StartsWith(p, StringComparison.Ordinal))
                || row.Doer != PlanDoers.Semanticus || PlanTasks.IsTaskId(fixId))
                throw new ArgumentException("Only a row Semanticus does itself, and that is on the plan now, can repeat as a "
                    + "fix. Call get_model_plan for the ids.", nameof(fixId));
            return row;
        }

        /// <summary>Change a repeat: its words, its doer, its cadence, or pause and resume it. Pro, person-only. A new
        /// cadence starts again from today. A card already out is the person's to finish; a change shapes the cards
        /// to come.</summary>
        public async Task<ModelPlanView> SetPlanRepeatAsync(string id, PlanRepeatRequest request)
        {
            RequireProFeature();
            if (request == null) throw new ArgumentException("Say what to change.", nameof(request));
            if (!string.IsNullOrWhiteSpace(request.Fix) || !string.IsNullOrWhiteSpace(request.Workflow) || !string.IsNullOrWhiteSpace(request.Stock))
                throw new ArgumentException("What a repeat brings back cannot change. Delete it and add a new one.", nameof(request));
            var bound = BindPlanMemory();
            var now = PlanRepeatClock();
            var zone = PlanRepeatZone;
            var current = PlanRepeats.Find(ReadPlanState(bound), id)
                ?? throw new ArgumentException("There is no repeat with that id. Call get_model_plan for the repeats.", nameof(id));

            var words = string.IsNullOrWhiteSpace(request.Words) ? null : request.Words.Trim();
            if (words != null && words.Length > PlanTasks.MaxWordsChars)
                throw new ArgumentException("That repeat is longer than " + PlanTasks.MaxWordsChars + " characters.", nameof(request));
            string doer = null;
            if (request.Doer != null)
            {
                doer = PlanDoers.Normalise(request.Doer) ?? throw new ArgumentException("The doer is 'assistant' or 'you'.", nameof(request));
                if (current.Fix != null && doer != PlanDoers.Semanticus)
                    throw new ArgumentException("A repeated fix is done by Semanticus. To do it yourself, delete it and repeat it "
                        + "as words.", nameof(request));
                if (current.Fix == null && doer == PlanDoers.Semanticus)
                    throw new ArgumentException(DoerSemanticusRefusal, nameof(request));
            }
            var cadence = request.Cadence == null ? null
                : PlanRepeats.Normalise(request.Cadence, TimeZoneInfo.ConvertTime(now, zone).Date);

            WritePlanTasks(bound, memory =>
            {
                var r = PlanRepeats.Find(memory, id);
                if (r == null) return;
                if (words != null) r.Words = words;
                if (doer != null && doer != r.Doer)
                {
                    if (r.DoneWhen == PlanTasks.DefaultDoneWhen(r.Doer)) r.DoneWhen = PlanTasks.DefaultDoneWhen(doer);
                    r.Doer = doer;
                }
                if (cadence != null)
                {
                    r.Cadence = cadence;
                    r.NextDueUtc = PlanRepeats.First(cadence, now, zone);
                }
                if (request.Held.HasValue) r.Held = request.Held.Value;
            });
            var plan = await ComposeAsync(null, bound).ConfigureAwait(false);
            TellThePlanMoved(bound);
            return plan;
        }

        /// <summary>Delete a repeat. Free, person-only. A card it already brought back stays as an ordinary row.</summary>
        public async Task<ModelPlanView> DeletePlanRepeatAsync(string id)
        {
            var bound = BindPlanMemory();
            if (PlanRepeats.Find(ReadPlanState(bound), id) == null)
                throw new ArgumentException("There is no repeat with that id. Call get_model_plan for the repeats.", nameof(id));
            WritePlanTasks(bound, memory => memory.Repeats.RemoveAll(r => string.Equals(r.Id, id.Trim(), StringComparison.Ordinal)));
            var plan = await ComposeAsync(null, bound).ConfigureAwait(false);
            TellThePlanMoved(bound);
            return plan;
        }

        // ---- inside the compose ----------------------------------------------------------------------------------

        /// <summary>A write made by the compose itself: best effort, as the compose's own memory write is, and to the
        /// session-held copy for a model with nowhere to keep things.</summary>
        private bool WriteRepeatsFromCompose(PlanMemoryBinding bound, Action<PlanMemoryState> change)
        {
            if (!bound.Nowhere) return TryWritePlanMemory(bound, change);
            if (!PlanMemoryStillOpen(bound)) return false;
            var held = _sessionPlanTasks.GetOrCreateValue(bound.Context);
            lock (held) { change(held); PlanTasks.Trim(held); }
            return true;
        }

        /// <summary>The due check for words and workflow repeats, run at the top of every compose, before the memory
        /// is read, so a card that came due is a row in this same answer. Pro only: on free nothing comes due.</summary>
        internal void ComeDueOnCompose(PlanMemoryBinding bound, IReadOnlyDictionary<string, PlanGroundNow> grounds = null)
        {
            if (!RepeatsArePro) return;
            var now = PlanRepeatClock();
            PlanMemoryState peek;
            if (bound.Nowhere)
            {
                if (!_sessionPlanTasks.TryGetValue(bound.Context, out peek)) return;
                lock (peek) { if (!PlanRepeats.AnyDue(peek, now, grounds)) return; }
            }
            else
            {
                peek = PlanMemory.Read(bound.File, bound.Identity);
                if (!PlanRepeats.AnyDue(peek, now, grounds)) return;
            }
            // The check runs again inside the write, on what is on disk now, so two engines on one model cannot both add
            // the card: the second reads the moved date and adds nothing.
            WriteRepeatsFromCompose(bound, state => PlanRepeats.ComeDue(state, now, PlanRepeatZone, grounds));
        }

        /// <summary>The repeats' grounded objects as they are now, read before the due check so a repeat about an object
        /// that is gone rests (UAT S12-6-002). Empty, and no model read, when no repeat is grounded.</summary>
        internal async Task<Dictionary<string, PlanGroundNow>> RepeatGroundsNowAsync(PlanMemoryBinding bound)
        {
            List<PlanObjectGround> grounds;
            if (bound.Nowhere)
            {
                if (!_sessionPlanTasks.TryGetValue(bound.Context, out var held)) return new Dictionary<string, PlanGroundNow>();
                lock (held) grounds = held.Repeats.SelectMany(r => r.Grounds ?? Array.Empty<PlanObjectGround>()).ToList();
            }
            else grounds = PlanMemory.Read(bound.File, bound.Identity).Repeats.SelectMany(r => r.Grounds ?? Array.Empty<PlanObjectGround>()).ToList();
            return await GroundsNowAsync(bound.Session, grounds).ConfigureAwait(false);
        }

        /// <summary>The repeats' part of the view, run once every row has its state word: fix repeats are settled
        /// against the rows, the rows are marked, and the repeats, the starters and the Pro line are put on the view.</summary>
        internal void FoldRepeats(PlanMemoryBinding bound, ModelPlanView view, PlanMemoryState memory,
                                  IReadOnlyDictionary<string, PlanGroundNow> grounds = null)
        {
            var pro = RepeatsArePro;
            var now = PlanRepeatClock();
            var zone = PlanRepeatZone;
            if (PlanRepeats.SettleFixes(memory.Repeats, view.Items, now, zone, pro))
                WriteRepeatsFromCompose(bound, state => PlanRepeats.SettleFixes(state.Repeats, view.Items, now, zone, pro));

            PlanRepeats.Stamp(view, memory);
            view.Repeats = memory.Repeats.Select(r => PlanRepeats.ViewOf(memory, r, grounds)).ToArray();
            view.RepeatStarters = pro
                ? PlanRepeats.Starters
                    .Where(s => !memory.Repeats.Any(r => string.Equals(r.Stock, s.Stock, StringComparison.Ordinal)))
                    .Select(s =>
                    {
                        var c = PlanRepeats.Normalise(s.Cadence, TimeZoneInfo.ConvertTime(now, zone).Date);
                        return new PlanRepeatStarter
                        {
                            Stock = s.Stock, Words = s.Words, Doer = s.Doer, Page = s.Page,
                            Cadence = c, CadenceText = PlanRepeats.CadenceText(c),
                        };
                    })
                    .ToArray()
                : Array.Empty<PlanRepeatStarter>();
            view.RepeatsNote = !pro && memory.Repeats.Count > 0 ? PlanRepeats.ProLine : null;
        }
    }
}
