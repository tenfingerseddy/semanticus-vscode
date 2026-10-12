// The wire shape of the living plan (docs/redesign/jev-plan.md section 2a, M2).
//
// One object, four views. The Plan page renders it, the Overview card renders its top three, and the
// assistant reads the same fields through its own door. Both doors return THESE types, so there is no second
// serialisation to drift, which is the same rule DecisionProtocol.cs follows.
//
// Two things about this shape are load-bearing and easy to lose:
//
//   The gaps are never stored. Every field below is derived from the model and the rules, and the derivation
//   is redone on each call. The engine does REMEMBER the expensive readings behind them for as long as the
//   model has not moved (LocalEngine.Plan.cs, PlanFactsEntry), which is a cache of a pure function of one
//   revision, not a stored plan: the moment the revision moves, the readings are taken again. What is
//   STORED, on disk, is only the small human residue: which items a person skipped and why, and the refresh
//   notes. Storing a gap would let the plan disagree with the model, and the plan's whole claim is that it
//   cannot.
//
//   An item's id comes from its SUBJECT, not from its position. "the safe fixes for rule X" and "a check for
//   measure Sales[Margin %]" name themselves, so the same gap keeps the same id across refreshes, a skip
//   sticks to the thing it was about, and the page can keep a row open while the list underneath it moves.
//
// Naming. The change-plan family (propose_plan, apply_plan, get_plan, add_plan_item, set_plan_item,
// clear_plan) already owns the word "plan" on both doors and means something else: one reviewed batch of
// edits. This family is prefixed model_plan / ModelPlan throughout so the two are told apart on the surface
// an assistant reads. docs/redesign/ui-hierarchy.md calls the page "Model Plan", so the prefix is also the
// name of the surface. The fold of the two is a later decision (ui-hierarchy.md, "Changes: Proposed").

using System;
using System.Linq;

namespace Semanticus.Engine
{
    /// <summary>The five goals a plan item can serve, in the order the page lists them. They are a person's own
    /// questions about a model, not the product's tools: is the model right, does it make sense, are the numbers
    /// right, can I trust it, can I ship it.</summary>
    public static class ModelPlanSections
    {
        public const string Structure = "Structure";
        public const string Meaning = "Meaning";
        public const string Numbers = "Numbers";
        public const string Trust = "Trust";
        public const string Ship = "Ship";

        /// <summary>Display order, and the deterministic tie-break order when nothing scores the items.</summary>
        public static readonly string[] InOrder = { Structure, Meaning, Numbers, Trust, Ship };

        public static int Rank(string section)
        {
            for (var i = 0; i < InOrder.Length; i++)
                if (string.Equals(InOrder[i], section, StringComparison.Ordinal)) return i;
            return InOrder.Length;
        }
    }

    /// <summary>Who does an item. "auto" runs on its own, "you" needs the person because the change contract
    /// needs an approval, "asst" needs words and so belongs to the person's own assistant.</summary>
    public static class ModelPlanOwners
    {
        public const string Auto = "auto";
        public const string You = "you";
        public const string Assistant = "asst";

        /// <summary>Jev worked the changes out (M4, the groundwork contract section 2). Only a Pro session
        /// ever has one of these: the item is composed from the lines a Jev family put on the change plan,
        /// and a free session never asks Jev, so it never has such a line to compose from.</summary>
        public const string Jev = "jev";
    }

    // ---- Plan at the heart (contract section 1.2). Source, doer and state are three separate facts on a row.
    // The frozen wire shape, with one row per scene of the mockup, is docs/redesign/contracts/plan-item.json,
    // and PlanTaskTests holds the file and these lists to each other.

    /// <summary>Where a row came from. Jev is a source here and never a doer.</summary>
    public static class PlanSources
    {
        public const string Found = "found";
        public const string Jev = "jev";
        public const string Words = "words";
        public const string You = "you";
        public const string Workflow = "workflow";
        public const string Repeat = "repeat";

        public static readonly string[] All = { Found, Jev, Words, You, Workflow, Repeat };
    }

    /// <summary>What a row is for, set by the engine from where its findings came from (round 1, 8 Oct 2026). The window filters by this and
    /// no longer guesses from the page a row jumps to.</summary>
    public static class PlanGoals
    {
        /// <summary>Any AI readiness rule found it. Wins over <see cref="Quality"/> when a row carries both kinds.</summary>
        public const string Ai = "ai";
        /// <summary>Only best-practice rules found it, or no rule did (the cleanup, a check, saving).</summary>
        public const string Quality = "quality";
        /// <summary>The person asked for it: a task they typed or sent, or lines an assistant put on the change plan.</summary>
        public const string Aims = "aims";

        public static readonly string[] All = { Ai, Quality, Aims };
    }

    /// <summary>Who does a row next. There are three and only three: Jev ranks and picks, it never does.</summary>
    public static class PlanDoers
    {
        public const string Semanticus = "semanticus";
        public const string You = "you";
        public const string Assistant = "assistant";

        public static readonly string[] All = { Semanticus, You, Assistant };

        public static string Normalise(string doer) =>
            All.FirstOrDefault(d => string.Equals(d, (doer ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The one state word on a row, computed by the engine (contract 1.4). The page never derives it.</summary>
    public static class PlanStateWords
    {
        public const string Done = "done";
        public const string SetAside = "set_aside";
        public const string Checking = "checking";
        public const string OnIt = "on_it";
        public const string Paused = "paused";
        public const string Waiting = "waiting";
        public const string NeedsYou = "needs_you";
        public const string Ready = "ready";

        /// <summary>Top to bottom, the first that holds wins.</summary>
        public static readonly string[] InPrecedence = { Done, SetAside, Checking, OnIt, Paused, Waiting, NeedsYou, Ready };

        /// <summary>True for a word a row can still be worked from.</summary>
        public static bool IsOpen(string word) =>
            word != Done && word != SetAside && word != Checking;
    }

    /// <summary>How the engine knows a row is done (contract 1.5).</summary>
    public static class PlanDoneWhen
    {
        public const string ModelCheck = "model_check";
        public const string HasWords = "has_words";
        public const string AssistantSays = "assistant_says";
        public const string YouMark = "you_mark";

        public static readonly string[] All = { ModelCheck, HasWords, AssistantSays, YouMark };

        public static string Normalise(string doneWhen) =>
            All.FirstOrDefault(d => string.Equals(d, (doneWhen ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>What a row's link back points at (contract 1.6).</summary>
    public static class PlanOriginKinds
    {
        public const string Finding = "finding";
        public const string Page = "page";
        public const string Run = "run";
        public const string Draft = "draft";
        public const string Words = "words";
        /// <summary>A found or Jev row the person gave to someone else: Ref is the row's id (lane m11/plan-gaps).</summary>
        public const string Item = "item";

        public static readonly string[] All = { Finding, Page, Run, Draft, Words, Item };
    }

    /// <summary>The switch "Tasks for your assistant: On / Paused" (contract section 4).</summary>
    public static class PlanAssistantQueue
    {
        public const string On = "on";
        public const string Paused = "paused";

        public static readonly string[] All = { On, Paused };
    }

    /// <summary>The link back to where a row came from. For a finding, Ref is "ruleId|objectRef"; for a page,
    /// the tool id; for a run or a draft, its id; for words, null.</summary>
    public sealed class PlanOriginLink
    {
        public string Kind { get; set; }
        public string Ref { get; set; }

        /// <summary>The key a Send gave the task (PlanTaskRequest.SendKey), so a page that lost its memory of a
        /// click, after a reconnect say, reads back which of its controls are already on the plan. Null
        /// for a task that was typed, and on every derived row.</summary>
        public string SendKey { get; set; }
    }

    /// <summary>One open row this row waits for, by id and by the title a person reads.</summary>
    public sealed class PlanWaitRef
    {
        public string Id { get; set; }

        /// <summary>Null on a short read of the plan, which names a row by its id only.</summary>
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public string Title { get; set; }

        /// <summary>Who does the blocking task: "you", "assistant" or "semanticus". Null when not known.</summary>
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public string Doer { get; set; }
    }

    /// <summary>Who holds a task right now. No live timer: the queue records a claim, not activity.</summary>
    public sealed class PlanClaimView
    {
        /// <summary>"you" when the person took it, otherwise the assistant's client name.</summary>
        public string ClaimedBy { get; set; }
        public DateTimeOffset Since { get; set; }
    }

    /// <summary>The picture at the top of the Plan (contract section 9). Lane E5 fills it; until then it is null.</summary>
    public sealed class PlanStrip
    {
        public string GradeNow { get; set; }
        public string GradeAfter { get; set; }

        /// <summary>The Best practice letter (BpaGrading) from the engine's own full scan of this revision, shown
        /// read-only beside the AI readiness grade. Null when the scan could not run.</summary>
        public string BestPractice { get; set; }
        public string BestPracticeWhy { get; set; }

        /// <summary>Semanticus, You, Your assistant, in that order.</summary>
        public PlanStripTrack[] Tracks { get; set; } = Array.Empty<PlanStripTrack>();

        /// <summary>The newest ledger entry, or null.</summary>
        public PlanStripLatest Latest { get; set; }
        public PlanStripUpdated Updated { get; set; }
    }

    public sealed class PlanStripTrack
    {
        /// <summary>One of <see cref="PlanDoers"/>.</summary>
        public string Doer { get; set; }
        public int Done { get; set; }
        public int OnIt { get; set; }
        public int Ready { get; set; }
        public int NeedsYou { get; set; }
        public int Waiting { get; set; }
    }

    public sealed class PlanStripLatest
    {
        public string EntryId { get; set; }
        public DateTimeOffset When { get; set; }
        public string By { get; set; }
        public string Sentence { get; set; }
    }

    public sealed class PlanStripUpdated
    {
        public DateTimeOffset When { get; set; }
        public string By { get; set; }
    }

    /// <summary>Where an item stands. Only "skipped" is remembered between calls; every other status is a fact
    /// about the model right now.</summary>
    public static class ModelPlanStatuses
    {
        /// <summary>The gap is real and something can act on it today.</summary>
        public const string Ready = "ready";

        /// <summary>The gap is real but what to do about it is a proposal a person chooses, not a fix.</summary>
        public const string Proposed = "proposed";

        /// <summary>The gap is gone. Nothing marks an item done; the absence of the gap does.</summary>
        public const string Done = "done";

        /// <summary>A person set it aside, with a reason, and it stays on the plan saying so.</summary>
        public const string Skipped = "skipped";

        /// <summary>Something is acting on it now. Reserved for M3's runner; nothing in this milestone sets it.</summary>
        public const string Running = "running";

        /// <summary>It is with the person's assistant. Reserved for M4's handoff; nothing in this milestone sets it.</summary>
        public const string Waiting = "waiting";

        /// <summary>The person typed this as a target and nothing in the model matches it yet. The ONLY
        /// status whose item is not a gap the rules found: it is an aim the person wrote down, so it is on
        /// the plan because they put it there, and it leaves when they remove it or mark it done. The item
        /// is built in PlanTargets.cs and its diff is empty on purpose.</summary>
        public const string Target = "target";
    }

    /// <summary>What kind of event one history entry records. The seven are exhaustive and the page renders a
    /// chip per kind, so a new kind is a page change as well as an engine one.</summary>
    public static class ModelPlanHistoryKinds
    {
        /// <summary>The plan was composed for the first time for this model.</summary>
        public const string Drawn = "drawn";

        /// <summary>The plan was composed again and the item set moved.</summary>
        public const string Redrawn = "redrawn";

        /// <summary>An item was started and it changed the model.</summary>
        public const string Done = "done";

        /// <summary>An item was started, the engine ran it, and nothing needed changing. It started; it did
        /// not finish anything, and reporting it as done would claim work nobody did.</summary>
        public const string Started = "started";

        /// <summary>A person set an item aside, with a reason.</summary>
        public const string Skipped = "skipped";

        /// <summary>A person put a set-aside item back.</summary>
        public const string Unskipped = "unskipped";

        /// <summary>A start the engine declined, carrying the sentence it declined with.</summary>
        public const string Refused = "refused";

        // ---- the change half (M3). Three more kinds, so the seven above are now ten, and a page that
        // renders a chip per kind needs three more chips. That is deliberate and it is the price of the
        // rule above: a new kind is a page change as well as an engine one, and a change set moving between
        // states is exactly the part of the story a person cannot reconstruct afterwards.

        /// <summary>Changes were worked out for an item and NOTHING WAS WRITTEN. The detail carries the
        /// lines, so the entry says what was proposed and not merely that something was.</summary>
        public const string Proposed = "proposed";

        /// <summary>The kept lines were applied, in one step, on one undo entry. The detail carries exactly
        /// the lines that went in, which is never assumed to be all of them: a dropped line is not here.</summary>
        public const string Approved = "approved";

        /// <summary>A person dropped a set of proposed changes. Nothing was written when they were proposed,
        /// so nothing was undone.</summary>
        public const string Cancelled = "cancelled";

        /// <summary>A person gave a task to their assistant again after it could not be done: a new claim cycle
        /// starts, and this entry keeps the give on the record (lane m11/plan-gaps).</summary>
        public const string Given = "given";
    }

    /// <summary>Who an entry is about. The mapping is fixed: a human origin is "you", an agent origin is
    /// "assistant", a step of a run-all is "plan", and the plan composing itself is "engine".</summary>
    public static class ModelPlanActors
    {
        public const string You = "you";
        public const string Assistant = "assistant";
        public const string Plan = "plan";
        public const string Engine = "engine";
    }

    /// <summary>What kind a table is, for the Map. Exactly one of these is on every table.</summary>
    public static class ModelPlanTableKinds
    {
        /// <summary>A calculated table carrying the ParameterMetadata marker. Classified by the SAME predicate
        /// the Overview's block chart uses (LocalEngine.IsFieldParameterTable), never a second one.</summary>
        public const string FieldParameter = "field parameter";

        /// <summary>DataCategory "Time".</summary>
        public const string DateTable = "date table";

        /// <summary>No columns beyond the row-number column, and one or more measures.</summary>
        public const string MeasureTable = "measure table";

        /// <summary>Nothing but relationship key columns, and two or more relationships.</summary>
        public const string Bridge = "bridge";

        /// <summary>Points at other tables and nothing points at it.</summary>
        public const string Fact = "fact";

        /// <summary>Something points at it.</summary>
        public const string Dimension = "dimension";

        /// <summary>None of the above.</summary>
        public const string Plain = "table";
    }

    /// <summary>Where an item's detail lives, as a place a person can open rather than an operation to run.</summary>
    public sealed class ModelPlanJump
    {
        /// <summary>The tool id this item belongs to, from the shared catalogue (see AskRouteCatalog).</summary>
        public string Tool { get; set; }

        /// <summary>What the link says, in the words the page uses for that place.</summary>
        public string Label { get; set; }

        /// <summary>The names to look for when the place is Find and replace: the fields a "names that Copilot reads as the same word" row
        /// is about (plan statement check 2, item 6). The page opens with them filled in, and an assistant reads the same list from
        /// get_model_plan. Null for every other row.</summary>
        public string[] Find { get; set; }
    }

    /// <summary>One table an item is about, and exactly what the item does inside it.
    ///
    /// The Map draws a block per table and puts the open work inside the block, so "this item is about Sales"
    /// was never enough: the block has to say how much of the item lands here and what it is. All of it is
    /// read off the object references the rules and sweeps already produced, the same source
    /// <see cref="ModelPlanItem.Diff"/> is built from, so an item's lines and its per-table lines cannot
    /// disagree.</summary>
    public sealed class ModelPlanItemTable
    {
        /// <summary>The table's name, as the model spells it.</summary>
        public string Name { get; set; }

        /// <summary>How many of the objects this item touches are in this table. Counted the same way the
        /// item's own total is counted, so the counts across an item's tables sum to that total whenever
        /// every object it touches belongs to a table. An object the item touches twice (two rules, one
        /// column) counts twice, exactly as it does in the total.</summary>
        public int Count { get; set; }

        /// <summary>A short phrase in the plan's words for what is here, for example "24 columns, 7
        /// measures". Counted by kind from the same objects, never a rewording of the title.</summary>
        public string What { get; set; }

        /// <summary>The item's exact-change lines that belong to this table, with the same "+ ", "- " and
        /// "~ " prefixes the item's own diff uses. Every line names the table it is filed under, because the
        /// object in the line is written the way a person writes it ("Sales[CustomerKey]"). A subset of the
        /// item's diff, never capped: the item's list is capped for reading, one table's is not.</summary>
        public string[] Diff { get; set; } = Array.Empty<string>();
    }

    /// <summary>One thing on the plan.</summary>
    public sealed class ModelPlanItem
    {
        /// <summary>The "object|property" keys a derived row's lines would write on the model as read (the
        /// best-practice row only). Engine-side, never on the wire.</summary>
        internal string[] DerivedKeys;

        /// <summary>Stable across refreshes because it is built from what the item is ABOUT, never from where it
        /// sits in the list. Shape: "family/subject", for example "safefix/rule:HIDE_KEY_COLUMNS" or
        /// "check/measure:Sales/Margin %".</summary>
        public string Id { get; set; }

        /// <summary>One of <see cref="ModelPlanSections"/>.</summary>
        public string Section { get; set; }

        /// <summary>The one line a person reads, in their words.</summary>
        public string Title { get; set; }

        /// <summary>The small grey line under the title: which objects, or what is wrong.</summary>
        public string Sub { get; set; }

        /// <summary>What finishing it is worth, as a short phrase. Empty when there is nothing honest to say.</summary>
        public string Value { get; set; }

        /// <summary>One of <see cref="ModelPlanOwners"/>.</summary>
        public string Who { get; set; }

        /// <summary>One of <see cref="ModelPlanStatuses"/>.</summary>
        public string Status { get; set; }

        /// <summary>True for a row the plan can end without: it is offered after every other open row and no other row
        /// waits for it (PlanFootprint.Waits). Today only the cleanup, which is always optional.</summary>
        public bool Optional { get; set; }

        /// <summary>How sure the ranking was about this item's place, from 0 to 1. ZERO means no number is shown:
        /// either nothing scored the plan (no service, not Pro, no permission) or the score was below the bar.
        /// A zero is never rendered as "not confident"; it is rendered as no number at all.</summary>
        public double Confidence { get; set; }

        /// <summary>The facts this item rests on, one plain sentence each, read off the model and the rules. Never
        /// a rewording of the title, and never a confidence in prose.</summary>
        public string[] Why { get; set; } = Array.Empty<string>();

        /// <summary>The exact change, one line each, prefixed "+ " to add, "- " to remove, "~ " to alter. A read
        /// only item carries an empty array and the page says so.</summary>
        public string[] Diff { get; set; } = Array.Empty<string>();

        /// <summary>What it touches, in a person's words ("6 columns in Sales, Customer, Product").</summary>
        public string Touches { get; set; }

        /// <summary>The tables this item is about, sorted by name, one entry each, with what it does inside
        /// each. Empty when it is about no table in particular (the model as a whole, the reports, the save).
        ///
        /// It exists because the Map groups the plan by table and had been doing it by searching the item's
        /// TITLE and its "touches" line for table names. A safe-fix row's title is its rule category and its
        /// touches line is "6 objects", so a table with real findings showed "nothing open" (Astra 15). The
        /// names here are read off the object references the rules produced, never off the diff lines, which
        /// are prose for a person and not a machine-readable list.
        ///
        /// It carried bare names until the Map redesign (21 September). A block on the Map has room to say
        /// how much of an item lands in it and what that is, so the entry carries the count, the phrase and
        /// the item's own change lines for that table. The wire name is unchanged: "tables".</summary>
        public ModelPlanItemTable[] Tables { get; set; } = Array.Empty<ModelPlanItemTable>();

        public ModelPlanJump Jump { get; set; }

        /// <summary>Why a person set this aside. Present only on a skipped item.</summary>
        public string SkipReason { get; set; }

        /// <summary>When they set it aside. Present only on a skipped item.</summary>
        public DateTimeOffset? SkippedUtc { get; set; }

        // ---- the change half (M3). See Plan/ModelPlanChanges.cs for the types and for the exact
        // difference between State and Status, and between "applied" and "done".

        /// <summary>One of <see cref="ModelPlanItemStates"/>: how far along the propose, review, approve road
        /// this item is. <see cref="Status"/> is the different question of whether the work can be acted on
        /// at all, and the two are not folded on purpose.</summary>
        public string State { get; set; }

        /// <summary>The changes worked out for this item, or null when none have been. A set here has
        /// written NOTHING: it is what approve would apply, with the versions it was worked out against and
        /// a digest over both.</summary>
        public ModelPlanChangeSet ChangeSet { get; set; }

        // ---- Plan at the heart (contract 1.2). Additive: Who, Status and State stay populated for one
        // release so an older reader keeps working. New readers read these.

        /// <summary>One of <see cref="PlanSources"/>.</summary>
        public string Source { get; set; }

        /// <summary>One of <see cref="PlanGoals"/>: what the row is for, from where its findings came from. Any AI readiness rule makes it
        /// "ai", best-practice rules alone make it "quality", and a row the person asked for is "aims".</summary>
        public string Goal { get; set; }

        /// <summary>Every filter this row shows under, <see cref="Goal"/> first. A row that fixes a rule both AI readiness and Model quality
        /// raise is "ai" and "quality": the same fix counts under both filters (Kane, 9 Oct 2026). Always stamped; one entry for the rest.</summary>
        public string[] Goals { get; set; }

        /// <summary>The rule ids this row carries, each once, in the order the rules found them. Empty for a row no rule raised (the cleanup, a
        /// task). It is how a page that lists findings says "On the plan" for a finding a row already holds.</summary>
        public string[] Rules { get; set; } = Array.Empty<string>();

        /// <summary>For a row a person sent from a page: that page's tool id. Null otherwise.</summary>
        public string SourcePage { get; set; }

        /// <summary>One of <see cref="PlanDoers"/>. Never "jev".</summary>
        public string Doer { get; set; }

        /// <summary>One of <see cref="PlanStateWords"/>, computed by the engine.</summary>
        public string StateWord { get; set; }

        /// <summary>A short engine-written qualifier on the state word, or null.</summary>
        public string StateNote { get; set; }

        /// <summary>One of <see cref="PlanDoneWhen"/>.</summary>
        public string DoneWhen { get; set; }

        /// <summary>The engine's sentence for how done is known.</summary>
        public string DoneText { get; set; }

        /// <summary>What the row touches, as object refs ("column:Sales/CustomerKey").</summary>
        public string[] Objects { get; set; } = Array.Empty<string>();

        /// <summary>The derived footprint (contract 5.1). Filled by lane E2; empty with
        /// <see cref="FootprintKnown"/> false until then, and whenever the engine cannot tell.</summary>
        public string[] Reads { get; set; } = Array.Empty<string>();
        public string[] Writes { get; set; } = Array.Empty<string>();
        public bool FootprintKnown { get; set; }

        /// <summary>The open rows this one waits for (contract 5.2). Filled by lane E2.</summary>
        public PlanWaitRef[] WaitsFor { get; set; } = Array.Empty<PlanWaitRef>();

        /// <summary>Only on a short read of the plan (the agent door, verify L11, 29 Sep): how many rows this one waits for in
        /// all, when <see cref="WaitsFor"/> lists only the first few, by id. Null when the list is whole. The row's own detail
        /// read (get_model_plan item = its id) lists every one, with its title, a page at a time.</summary>
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public int? WaitsForCount { get; set; }

        /// <summary>Paused on this row by the person (contract section 4).</summary>
        public bool Held { get; set; }

        /// <summary>Place in the assistant's order, 1 first. Only on a task the assistant does.</summary>
        public int? Order { get; set; }

        /// <summary>One note from the person, one reply from the assistant.</summary>
        public string Note { get; set; }
        public string Reply { get; set; }

        /// <summary>The person's whole text, when they gave the Plan more than a title holds. The title is its start.
        /// Null when the words fit the title.</summary>
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public string FullText { get; set; }

        /// <summary>Set while someone holds the task.</summary>
        public PlanClaimView Claim { get; set; }

        public PlanOriginLink OriginLink { get; set; }

        /// <summary>The number the fold line gives this row in the assistant's offer (1, 2, ...), or null when it is
        /// not offered. A draft's parts share their draft's number.</summary>
        public int? OfferNumber { get; set; }

        /// <summary>The ledger entries linked to this row at write time (contract 8.4): the edits whose cause names it.</summary>
        public string[] Events { get; set; } = Array.Empty<string>();

        internal ModelPlanItem ShallowCopy() => (ModelPlanItem)MemberwiseClone();
    }

    /// <summary>What finishing the whole plan is worth, as a before and an after plus the plain lines behind it.</summary>
    public sealed class ModelPlanWorth
    {
        /// <summary>The AI-readiness grade now, for example "B". Null when no grade could be computed.</summary>
        public string From { get; set; }

        /// <summary>The grade the safe fixes alone would reach. Equal to <see cref="From"/> when they move nothing,
        /// and null when no grade could be computed.</summary>
        public string To { get; set; }

        /// <summary>The AI-readiness score now, 0 to 100, rounded, from the same scan as <see cref="From"/>.
        /// Null exactly when <see cref="From"/> is.</summary>
        public int? FromScore { get; set; }

        /// <summary>The score the safe fixes alone would reach, 0 to 100, rounded, from the same rehearsal as
        /// <see cref="To"/>. Null while <see cref="Pending"/> is true and whenever <see cref="To"/> is null: a
        /// number nobody worked out is never sent as a zero or as a copy of <see cref="FromScore"/>. The
        /// Overview draws the gap between the two as a bar and does no arithmetic of its own.</summary>
        public int? ToScore { get; set; }

        /// <summary>The counted reasons, one short line each. Every line is a count from the model, never a claim
        /// about what a person will feel.</summary>
        public string[] Lines { get; set; } = Array.Empty<string>();

        /// <summary>True while <see cref="To"/> is still being worked out. The grade after is REHEARSED, which
        /// costs a second readiness scan, so the first plan for a revision comes back without it rather than
        /// making a person wait for it. A pending number is not an unknown number, and the two must not be
        /// rounded together: <see cref="Lines"/> carries one line saying it is still being worked out, and a
        /// later call has the number.</summary>
        public bool Pending { get; set; }

        /// <summary>True when both grades are known and they are the same one. The page then says "Stays B"
        /// instead of "B to B", which read as a typo on Kane's screenshot. It is deliberately NOT the same as
        /// "From equals To" computed on the page: while <see cref="Pending"/> is true there is no second
        /// grade to compare, and a page doing its own comparison would call a missing number a match.</summary>
        public bool Unchanged { get; set; }

        /// <summary>The target in one line, for example "Stays B, then what you asked for.". It says the
        /// grade now, the grade the safe fixes reach, and, only when the person has typed targets of their
        /// own, that those come after the fixes.
        ///
        /// It is built from the fields above and NEVER works out a grade of its own, so it cannot say
        /// something the rest of the worth does not: while the second grade is still being worked out the
        /// line says so, and a model with no grade at all says that instead of guessing one.
        /// Written by PlanTargets.TargetLine.</summary>
        public string TargetLine { get; set; }
    }

    /// <summary>The single item the plan would do next, named so the Overview and the summary strip agree.</summary>
    public sealed class ModelPlanNext
    {
        public string ItemId { get; set; }

        /// <summary>The one line the strip shows, for example "Fix 9 safe things".</summary>
        public string Line { get; set; }
    }

    /// <summary>One entry in the plan's story: what happened, who did it, and to what.
    ///
    /// It began as a refresh note and nothing else, one sentence in <see cref="Text"/>. The History timeline
    /// (21 September) needs to group by day, chip by kind and show the change lines, and none of that can be
    /// read back out of a sentence, so the entry now carries the parts separately.
    ///
    /// <see cref="Text"/> STAYS and is filled on every new entry with the same sentence, because entries
    /// written before this shape existed have nothing else, and because a reader that only knows about Text
    /// must keep working.</summary>
    public sealed class ModelPlanHistoryEntry
    {
        public DateTimeOffset When { get; set; }

        /// <summary>The one-line form. Always present: on a new entry it is <see cref="Sentence"/>, and on an
        /// entry read back from a file written before the structured shape it is all there is.</summary>
        public string Text { get; set; }

        /// <summary>One of <see cref="ModelPlanHistoryKinds"/>. An old text-only entry reads as "redrawn",
        /// which is what those entries always were.</summary>
        public string Kind { get; set; }

        /// <summary>One of <see cref="ModelPlanActors"/>. An old text-only entry reads as "engine".</summary>
        public string Actor { get; set; }

        /// <summary>The item this is about, or null for a draw or a redraw, which are about the whole plan.</summary>
        public string ItemId { get; set; }

        /// <summary>The item's title in the words the person read at the time. Kept rather than looked up,
        /// because the item may be gone by the time anyone reads this.</summary>
        public string Title { get; set; }

        /// <summary>The model revision at the time. Zero when it could not be known.</summary>
        public long Revision { get; set; }

        /// <summary>The one line a person reads. Same string as <see cref="Text"/>.</summary>
        public string Sentence { get; set; }

        /// <summary>The detail under the sentence: the applied change lines for a done, the reason for a
        /// skip, the refusal sentence for a refused. Empty when there is nothing more to say.</summary>
        public string[] Detail { get; set; } = Array.Empty<string>();

        /// <summary>On a redraw: the titles of items that were not on the plan last time.</summary>
        public string[] Appeared { get; set; } = Array.Empty<string>();

        /// <summary>On a redraw: the titles of items that were on the plan last time and are not now.
        /// <see cref="Completed"/> is the subset of these that the plan recorded as finished; an item can
        /// also leave for other reasons, and the two lists are kept apart so it can say which.</summary>
        public string[] Gone { get; set; } = Array.Empty<string>();

        /// <summary>On a redraw: the titles of items the plan recorded as finished at this refresh.</summary>
        public string[] Completed { get; set; } = Array.Empty<string>();
    }

    /// <summary>How many findings of one category sit on one table.</summary>
    public sealed class ModelPlanTableFinding
    {
        /// <summary>The readiness category, spelled exactly as the findings page spells it, so a count here
        /// and a filter there select the same findings.</summary>
        public string Category { get; set; }

        /// <summary>Active (non-waived) findings of that category on that table. A waived finding is an
        /// accepted one and is not work, so it is not counted.</summary>
        public int Count { get; set; }
    }

    /// <summary>One table, for the Map view. Kind is one of <see cref="ModelPlanTableKinds"/>.</summary>
    public sealed class ModelPlanTable
    {
        public string Name { get; set; }
        public string Kind { get; set; }

        /// <summary>Columns a person can see, counted the way the Overview's block chart counts them: the
        /// row-number column is plumbing and is not one.</summary>
        public int Columns { get; set; }

        public int Measures { get; set; }

        /// <summary>Relationships with this table at either end.</summary>
        public int Relationships { get; set; }

        /// <summary>Bytes this table takes in memory, or null when nobody has measured it. Only a real size
        /// scan of a live model fills it (vpaq_scan); the plan never goes to the network to find out, so on a
        /// model that has not been scanned this stays null rather than becoming a guess or a zero.</summary>
        public long? SizeBytes { get; set; }

        /// <summary>The active readiness findings on this table, grouped by category. Empty when the table
        /// has none.</summary>
        public ModelPlanTableFinding[] Findings { get; set; } = Array.Empty<ModelPlanTableFinding>();
    }

    /// <summary>The whole plan, as one call returns it.</summary>
    public sealed class ModelPlanView
    {
        public string ModelName { get; set; }

        /// <summary>The model revision this plan was read at. On the wire because the expensive half of a plan
        /// is now remembered per revision: a caller that can see WHICH model a plan describes can tell a fresh
        /// plan from one that was handed back, and a test can prove a remembered plan is never a stale one.</summary>
        public long Revision { get; set; }

        /// <summary>When this plan was composed. Always now: the items are recomposed on every call even when
        /// the expensive facts behind them were read earlier, so this is the time of THIS answer.</summary>
        public DateTimeOffset RefreshedAt { get; set; }

        /// <summary>What changed since the last call, in plain words ("1 item done, 1 new"). "The plan was drawn
        /// for the first time" on the first call for a model. Unlike History's sentence it also counts the rows the
        /// plan does not remember (an assistant's and Jev's) as they arrive or leave, compared with the last call
        /// this engine answered for the open model.</summary>
        public string RefreshedWhy { get; set; }

        public ModelPlanWorth Worth { get; set; } = new ModelPlanWorth();

        /// <summary>The top item, or null when nothing is open.</summary>
        public ModelPlanNext Next { get; set; }

        public ModelPlanItem[] Items { get; set; } = Array.Empty<ModelPlanItem>();

        /// <summary>The last two hundred things that happened to this plan, newest last.</summary>
        public ModelPlanHistoryEntry[] History { get; set; } = Array.Empty<ModelPlanHistoryEntry>();

        public ModelPlanTable[] Tables { get; set; } = Array.Empty<ModelPlanTable>();

        /// <summary>How the order was arrived at, in plain words. Present on every plan, because "these are in
        /// rule order" and "these were ranked by value" are different promises and a person is owed which one
        /// they got. On a free session, with no service, or below the bar this says so and no item carries a
        /// number.</summary>
        public string OrderedBy { get; set; }

        /// <summary>Where billing is, set only when the order line says the Pro plan is not active, so the page can show a Billing link
        /// beside it. Null every other time.</summary>
        public string BillingUrl { get; set; }

        /// <summary>True when a value ranking was actually applied. False is the ordinary case and is not a
        /// failure: the plan is complete either way, in rule order, with the same reasons.</summary>
        public bool Ranked { get; set; }

        /// <summary>True while Jev is still looking at this model revision in the background. The plan is
        /// complete without it and never waits for it; the Jev items appear on a later read, and the page is
        /// told through plan/didChange when the run lands. Always false on a free session.</summary>
        public bool JevPending { get; set; }

        /// <summary>An honesty line for anything the plan could not work out (a scan that needs a live
        /// connection, a report reading that was never taken). Null when there is nothing to say.</summary>
        public string Note { get; set; }

        /// <summary>What this model should become: the rule-derived aims, one per section, followed by the
        /// lines the person typed. Two kinds in one list on purpose, told apart by their Source, because the
        /// page shows them as one thing and a person does not care which half the product supplied.
        ///
        /// The aims are DERIVED on every call from the sections' own "why" lines and are never stored; only
        /// the typed lines are remembered, in the plan memory under the model's durable identity. See
        /// PlanTargets.cs. Never empty: there is always one aim per section.</summary>
        public ModelPlanTarget[] Targets { get; set; } = Array.Empty<ModelPlanTarget>();

        /// <summary>The picture at the top (contract 9), built by the engine so both doors read one answer.</summary>
        public PlanStrip Strip { get; set; }

        /// <summary>The ways to start, offered only while the model has no tables (Plan/LocalEngine.PlanBestPractice.cs).
        /// Empty on any model with a table in it.</summary>
        public ModelPlanStart[] Starts { get; set; } = Array.Empty<ModelPlanStart>();

        /// <summary>Set while a spec-first start plan exists (a new model being made from a SQL source): where it is
        /// up to and the tool that reads it, so an assistant that opens the Plan finds it and the Plan page can link to
        /// it. Null, and left out of the JSON, when there is none, so a model without one reads exactly as before.</summary>
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public StartPlan.StartPlanPointer StartPlan { get; set; }

        /// <summary>A fingerprint of the rows Run the plan would take right now (contract 7.2, Sol 24 Sep): every
        /// row with doer semanticus and state word ready, each as its id, its change set digest (or "none") and
        /// its held flag, sorted by id, plus the model revision. Quote it to <c>apply_plan_items</c> with no
        /// items: a hold, a doer switch, a skip or a proposal made after the read changes it even when the model
        /// revision does not move, and the run is refused rather than applying a set nobody read.</summary>
        public string ReadyDigest { get; set; }

        /// <summary>The switch "Tasks for your assistant", one of <see cref="PlanAssistantQueue"/>. On by default.</summary>
        public string AssistantQueue { get; set; } = PlanAssistantQueue.On;

        /// <summary>The text the assistant reads on its next call (contract 6.1): the offer line, or the paused line.
        /// Null when it would read nothing. The page shows this; it never works one out of its own.</summary>
        public string AssistantSees { get; set; }

        /// <summary>The saved repeats and their next due dates (lane RP, contract 1.7), read by both doors. Each
        /// card a repeat brings back is an ordinary row with source "repeat"; its repeat names it in OpenItemId.</summary>
        public PlanRepeatView[] Repeats { get; set; } = Array.Empty<PlanRepeatView>();

        /// <summary>The stock repeats not yet saved on this model, offered for one click. Empty on free.</summary>
        public PlanRepeatStarter[] RepeatStarters { get; set; } = Array.Empty<PlanRepeatStarter>();

        /// <summary>The Pro line when repeats are saved and this session is free, so they are not coming due. Null
        /// otherwise.</summary>
        public string RepeatsNote { get; set; }

        /// <summary>Set only on the agent door's short read (PlanReadShape): one entry per row, saying whether its detail
        /// was left out, how many lines it holds and where its next page starts. Null on the Studio's read, so the frozen
        /// item shape (contracts/plan-item.json) is not touched.</summary>
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public PlanRowDetail[] Detail { get; set; }

        /// <summary>Set only on the agent door's short read (PlanReadShape): what was left out and how to ask for it.</summary>
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public string DetailNote { get; set; }

        internal ModelPlanView ShallowCopy() => (ModelPlanView)MemberwiseClone();
    }

    /// <summary>What the agent door's short read did with one row's detail (its diff lines, per-table lines, change lines,
    /// objects and footprint).</summary>
    public sealed class PlanRowDetail
    {
        public string ItemId { get; set; }

        /// <summary>True when this read carries a page of the row's detail; false when the detail is left out and is asked
        /// for by id.</summary>
        public bool Shown { get; set; }

        /// <summary>How many lines the detail holds in full: the most of its lists.</summary>
        public int Lines { get; set; }

        /// <summary>When a page is shown: the offset of the next page, or null at the end.</summary>
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public int? Next { get; set; }
    }

    /// <summary>What <c>model_plan_action</c> did, or plainly did not do.</summary>
    public sealed class ModelPlanActionResult
    {
        public string ItemId { get; set; }

        /// <summary>The action asked for, echoed back.</summary>
        public string Action { get; set; }

        /// <summary>True only when something actually ran and changed the model.</summary>
        public bool Performed { get; set; }

        /// <summary>One sentence for a person: what happened, or why nothing did.</summary>
        public string Sentence { get; set; }

        /// <summary>The model revision after the action, or the revision it read at when nothing ran.</summary>
        public long Revision { get; set; }

        /// <summary>The plan as it stands after the action, so a caller never has to guess whether to refresh.</summary>
        public ModelPlanView Plan { get; set; }
    }
}
