// The composer's draft (composer contract section 4, 6 and 10.1; Plan-heart contract 1.3 and 14).
//
// ONE DRAFT, BOTH DOORS. A draft is the one object the Plan row, the Studio and an assistant all hold: its steps,
// its questions, its blanks, keep or drop per step, the approval, and what happened when it ran. It is stored as
// it is, so a restart keeps it (DraftStore). Every sentence a door shows is built by the engine (DraftCopy); a
// door never writes one of its own.
//
// SIX STATES INSIDE, ONE WORD OUTSIDE. The draft keeps the six run states of composer contract 4.1. The Plan row
// shows one of the Plan's state words, mapped once (DraftRows, Plan-heart contract 14), so the Plan, the fold line
// and the summary read the same answer.
//
// ALL WAITING HAPPENS BEFORE THE CHANGE. Nothing is written to the model while a question or a blank is open. The
// change group then runs as ONE MutateAsync through lane L3's ApplyComposedChangeAsync, so it is one undo entry.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace Semanticus.Engine.Composer
{
    /// <summary>The six run states (composer contract 4.1).</summary>
    public static class DraftStates
    {
        public const string NeedsYou = "needs_you";
        public const string WaitingForAssistant = "waiting_for_assistant";
        public const string Ready = "ready";
        public const string Running = "running";
        public const string Done = "done";
        public const string Stopped = "stopped";

        public static readonly string[] All = { NeedsYou, WaitingForAssistant, Ready, Running, Done, Stopped };
    }

    /// <summary>The blank states (composer contract 4.2).</summary>
    public static class DraftBlankStates
    {
        /// <summary>Not yet on the queue: nobody has been asked.</summary>
        public const string Empty = "empty";
        /// <summary>On the queue, unclaimed.</summary>
        public const string Queued = "queued";
        /// <summary>An assistant holds the request.</summary>
        public const string Writing = "writing";
        /// <summary>Written by an assistant and checked.</summary>
        public const string Filled = "filled";
        /// <summary>Written by the person and checked.</summary>
        public const string Self = "self";
        /// <summary>Written by Semanticus from a fixed template.</summary>
        public const string Generated = "generated";
        /// <summary>Needs the person: a decline, a third failed check, or evidence that moved.</summary>
        public const string Needs = "needs";

        public static bool Passed(string s) => s == Filled || s == Self || s == Generated;
    }

    public static class DraftSources
    {
        /// <summary>An explicit list of steps from an assistant or a person.</summary>
        public const string Steps = "steps";
        /// <summary>A saved v3 workflow.</summary>
        public const string Workflow = "workflow";
        /// <summary>Drafted from the person's words (lane L5).</summary>
        public const string Words = "words";
    }

    public static class DraftPhases
    {
        public const string Change = "change";
        public const string After = "after";
        /// <summary>A read before the change (composer contract 2.4, prepare-phase reads): it runs first, changes
        /// nothing, and a read that cannot run stops the draft before anything changes (Codex, F-276).</summary>
        public const string Prepare = "prepare";
    }

    /// <summary>The check kinds a draft runs, by phase (composer contract 1.7). The guided-run verify kinds stay with
    /// guided runs. One list, read by compose_task and by the parser's admission query, so they cannot disagree.</summary>
    public static class DraftCheckKinds
    {
        public static readonly IReadOnlyCollection<string> Before = new HashSet<string>(StringComparer.Ordinal)
            { "name_free", "key_unique", "dates_within", "source_matches" };
        public static readonly IReadOnlyCollection<string> Written = new HashSet<string>(StringComparer.Ordinal)
            { "valid_dax", "dax_lint", "refs_within", "max_chars", "no_formula_text", "valid_m", "choice_member" };
        public static readonly IReadOnlyCollection<string> After = new HashSet<string>(StringComparer.Ordinal) { "result_range", "row_count", "dax_probe", "bpa_clean" };

        public static bool Runs(string phase, string kind) =>
            phase == "before" ? Before.Contains(kind) : phase == "written" ? Written.Contains(kind) : phase == "after" && After.Contains(kind);
    }

    /// <summary>Why a held draft waits for the person (composer contract 5.4, decision 2's two exceptions).</summary>
    public static class DraftHolds
    {
        /// <summary>A kept set_table_permission action, however its filter was supplied.</summary>
        public const string RowSecurity = "row_security";
        /// <summary>A draft with a blank whose after group publishes.</summary>
        public const string Publish = "publish";
    }

    /// <summary>A clause of a draft's words that the adding rule left with no step, and why (<see cref="Draft.NoStep"/>).</summary>
    public sealed class DraftWordsWithoutStep
    {
        public string Words { get; set; }
        public string Reason { get; set; }
        /// <summary>For an unticked add (<see cref="Draft.Unticked"/>), the id of its step, so the note is said only while
        /// that step is still unticked. Null when the words name no step to tick.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string StepId { get; set; }
    }

    /// <summary>One draft, as stored. The view both doors read is <see cref="DraftView"/>.</summary>
    public sealed class Draft
    {
        /// <summary>"dr-" and 8 lowercase hex, random so a restart never reuses one.</summary>
        public string Id { get; set; }
        /// <summary>The model's durable identity. A draft belongs to one model.</summary>
        public string ModelIdentity { get; set; }
        public string Title { get; set; }
        /// <summary>The person's words, when it was drafted from words.</summary>
        public string Words { get; set; }
        /// <summary>The newest words a person added with Add more words, which the menu ranks first. Null until then.</summary>
        public string LastWords { get; set; }
        /// <summary>The words as typed, one entry for the first words and one for each Add more words, because
        /// <see cref="Words"/> joins them with a space and so loses where one ended (R2-D6b). A draft still on the menu
        /// asks Jev about each entry as clauses of its own. Null until the first Add more words, and on a draft stored
        /// before this was kept, whose words then count as one entry.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public List<string> WordsTyped { get; set; }
        /// <summary>The words of each step the words named past the most one draft reads (DescribedSteps.MaxSteps), as
        /// typed, until Add more words reads them. The draft's notes name them, so they stay in sight after the call
        /// that made the draft (Sol on m11/integration 568435d3, finding 4). Null when there are none.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public List<string> NotRead { get; set; }
        /// <summary>Why no ask could leave when the words in <see cref="NotRead"/> were kept ("Jev was not asked: ..."), or
        /// null when one could and only did not answer. The note says it in place of a promise that Add more words asks
        /// Jev again, which is false while the point is off or permission is not given (verifier on m11/sol-fixes-1, D5).</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string NotReadWhy { get; set; }
        /// <summary>The words of each clause the adding rule left with no step (AddingRule: a clause that asks to take
        /// something away, or one whose only step would add when its words do not ask to add), each with the rule's reason.
        /// They are kept as placeholders, never as steps: the draft's notes name them, and Author opens each as a step to
        /// fill in. Add more words does not ask about them again. Null when there are none.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public List<DraftWordsWithoutStep> NoStep { get; set; }
        /// <summary>The words of each clause whose add the adding rule offers UNTICKED (AddingRule: the clause holds an adding
        /// verb and a check failed), each with the rule's one true line on why. The step is on the draft, dropped, and the
        /// note is said while it stays dropped. Null when there are none.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public List<DraftWordsWithoutStep> Unticked { get; set; }
        /// <summary>One of <see cref="DraftSources"/>.</summary>
        public string Source { get; set; }
        /// <summary>The saved workflow it came from, when it did.</summary>
        public string Workflow { get; set; }
        /// <summary>"Notes for your assistant", frozen when the draft was made.</summary>
        public string AssistantNotes { get; set; }
        /// <summary>human or agent: the door that composed it.</summary>
        public string Origin { get; set; }
        public DateTimeOffset CreatedUtc { get; set; }
        public DateTimeOffset UpdatedUtc { get; set; }

        /// <summary>True once Jev drafted this draft's steps from its words (lane L5); the menu then carries no line
        /// saying Jev was not sure.</summary>
        public bool JevDrafted { get; set; }
        /// <summary>True when Jev was asked to draft from the words, answered, and was sure of no step: the menu's line
        /// then says Jev was not sure. Never set when Jev was not asked.</summary>
        public bool JevNotSure { get; set; }
        /// <summary>Steps of a Plan draft the words named because Jev was not sure of their clause (the per-clause rule).</summary>
        public int WordSteps { get; set; }
        public List<DraftQuestion> Questions { get; set; } = new List<DraftQuestion>();
        public List<DraftStep> Steps { get; set; } = new List<DraftStep>();
        /// <summary>The workflow text a draft with a gated step was made from (lane l9-finish): its gates run in a guided
        /// sub-run built from these exact bytes when the draft runs, so a later edit of the file cannot change a gate the
        /// person already drafted. Null when no step carries a gate.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string GuidedDocument { get; set; }

        /// <summary>Approval is a flag, not a state (contract 4.1). Approved and ready runs by itself.</summary>
        public bool Approved { get; set; }
        /// <summary>human or agent.</summary>
        public string ApprovedBy { get; set; }
        public DateTimeOffset? ApprovedUtc { get; set; }
        /// <summary>The person's own Approve and run on a held draft, from the human-only target.</summary>
        public bool PersonApproved { get; set; }
        /// <summary>The digest of the content the person approved (LocalEngine.ApprovedContentOf). The approval stands
        /// only while the draft still matches it, and is cleared the moment it does not.</summary>
        public string PersonApprovedDigest { get; set; }

        /// <summary>What happened when it ran. Null until it runs.</summary>
        public DraftRun Run { get; set; }

        /// <summary>The state as last worked out. Always recomputed on read; kept so the file says what it was.</summary>
        public string State { get; set; }

        /// <summary>What each property a change step writes said in the model when the draft first knew which object
        /// the step writes, keyed "kind|objectRef" (UAT S12-5-004). The run is refused on the dispatcher when one of
        /// them says something else by then, so a draft never silently replaces a newer edit.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public Dictionary<string, string> Baselines { get; set; }

        /// <summary>The newer edit a run stopped at, until the person chooses: keep theirs, or apply the draft.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public DraftOverwrite Overwrite { get; set; }
    }

    /// <summary>A property a draft would write that changed after the draft was made (UAT S12-5-004).</summary>
    public sealed class DraftOverwrite
    {
        public string StepId { get; set; }
        public int StepNumber { get; set; }
        public string ObjectRef { get; set; }
        /// <summary>The change kind (set_description, set_dax and the rest), which names the property.</summary>
        public string Kind { get; set; }
        /// <summary>What it said when the draft was made.</summary>
        public string Was { get; set; }
        /// <summary>What it says now.</summary>
        public string Now { get; set; }
    }

    /// <summary>The choice both doors are shown when a run stopped at a newer edit.</summary>
    public sealed class DraftOverwriteView
    {
        public string Sentence { get; set; }
        public int Step { get; set; }
        public string ObjectRef { get; set; }
        public string Was { get; set; }
        public string Now { get; set; }
    }

    public sealed class DraftQuestion
    {
        public string Name { get; set; }
        public string Question { get; set; }
        /// <summary>text | enum | number | objectRef</summary>
        public string Type { get; set; }
        /// <summary>you | assistant | semanticus</summary>
        public string By { get; set; }
        public string[] Values { get; set; } = Array.Empty<string>();
        public string From { get; set; }
        public string Default { get; set; }
        public bool Required { get; set; } = true;
        /// <summary>The step it belongs to, or null for a workflow input asked in Before it runs.</summary>
        public string Step { get; set; }
        public string Answer { get; set; }
        /// <summary>you or assistant: who answered.</summary>
        public string AnsweredBy { get; set; }
    }

    public sealed class DraftStep
    {
        public string Id { get; set; }
        public int Number { get; set; }
        public string Title { get; set; }
        /// <summary>change | after</summary>
        public string Phase { get; set; }
        public List<ComposedAction> Actions { get; set; } = new List<ComposedAction>();
        public List<DraftBlank> Blanks { get; set; } = new List<DraftBlank>();
        public List<ComposedCheck> Checks { get; set; } = new List<ComposedCheck>();
        /// <summary>"Brief for your assistant": advice for this step's blanks.</summary>
        public string Brief { get; set; }
        /// <summary>What the step does, in words, written as the step's body when the draft becomes a workflow file (Author's
        /// What it does). A described placeholder carries its reason here, as the seeded-steps fallback does. Null otherwise.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string Body { get; set; }
        public bool Kept { get; set; } = true;
        /// <summary>For an add the engine ticked from the person's words (the adding rule, version 6): the clause it was ticked
        /// for, as typed. Every later typing reads all the words again and unticks it when they take it back; a step the
        /// person ticked or dropped by hand carries none, so their own choice stands. Null otherwise.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string TickedFromWords { get; set; }
        /// <summary>Set when a drop of another step dropped this one: the number it needed.</summary>
        public int? DroppedWith { get; set; }

        /// <summary>Who does the step (composer contract 1.3): semanticus (null reads the same, so a draft stored before
        /// this field keeps its meaning), assistant or person. A person's step is a stop: the run pauses there until the
        /// person presses Continue. An assistant's step pauses the run until the assistant submits the step's writes,
        /// which apply as one change of their own (contract 3.4).</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string Performer { get; set; }

        /// <summary>An assistant's step: the Plan task that carries its request while one is open.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string RequestId { get; set; }
        /// <summary>An assistant's step: failed submissions on the current request (three hand it to the person).</summary>
        public int Attempts { get; set; }
        /// <summary>An assistant's step that needs the person: the last refusal, or the assistant's own reason.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string Reason { get; set; }
        /// <summary>An assistant's step that carries a guided gate (composer contract 1.3: it "runs exactly as a guided
        /// step does today"). It runs as a step of the draft's guided sub-run: the assistant submits it with
        /// submit_workflow_step, and the gate decides before the draft moves on.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool Guided { get; set; }
        /// <summary>A gated step whose gate names an op that changes the model, so it counts as an undo step.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool GuidedWrites { get; set; }
        /// <summary>A gated step whose gate runs a check (a verify), so a failure can stop the draft there.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool GuidedChecks { get; set; }

        [JsonIgnore] public bool ByPerson => Performer == DraftPerformers.Person;
        [JsonIgnore] public bool ByAssistant => Performer == DraftPerformers.Assistant;
        /// <summary>A step Semanticus runs itself.</summary>
        [JsonIgnore] public bool BySemanticus => !ByPerson && !ByAssistant;
    }

    /// <summary>The three performers of a draft's step (composer contract 1.3).</summary>
    public static class DraftPerformers
    {
        public const string Semanticus = "semanticus";
        public const string Assistant = "assistant";
        public const string Person = "person";
    }

    public sealed class DraftBlank
    {
        public string Name { get; set; }
        /// <summary>dax | description | m | name | choice</summary>
        public string Kind { get; set; }
        /// <summary>The action argument the text becomes.</summary>
        public string Fills { get; set; }
        /// <summary>assistant or you: who is asked first.</summary>
        public string By { get; set; } = "assistant";
        public string[] DependsOn { get; set; } = Array.Empty<string>();
        public int? MaxChars { get; set; }
        public string[] Choices { get; set; }
        /// <summary>A generator id (composer contract 1.5 and 2.3): when it applies, Semanticus writes the blank from a
        /// fixed template and the blank never reaches the assistant (Codex, F-277).</summary>
        public string Generator { get; set; }

        /// <summary>One of <see cref="DraftBlankStates"/>, as last stored. Queued and writing are read off the
        /// request's own task when the view is built, so a claim or a restart shows without a write here.</summary>
        public string State { get; set; } = DraftBlankStates.Empty;
        /// <summary>The text that passed its checks.</summary>
        public string Text { get; set; }
        /// <summary>you, assistant or semanticus.</summary>
        public string FilledBy { get; set; }
        /// <summary>The Plan task that carries the request to the assistant ("pt-..."), while one is open.</summary>
        public string RequestId { get; set; }
        /// <summary>Failed tries on the current request (three closes it, composer contract 5.4).</summary>
        public int Attempts { get; set; }
        /// <summary>Why it needs the person: the assistant's reason word for word, the last failed check, or the
        /// evidence that moved.</summary>
        public string Reason { get; set; }
        /// <summary>Object refs the assistant offered with a decline, only those that resolve to real objects.</summary>
        public string[] AskChoices { get; set; }
        /// <summary>The person's pick among <see cref="AskChoices"/>, carried on the next request.</summary>
        public string Picked { get; set; }
        /// <summary>Why a blank in needs waits for the person: declined, failed (three tries) or evidence (it passed,
        /// then the model changed under it).</summary>
        public string NeedsWhy { get; set; }
    }

    /// <summary>What happened when the draft ran.</summary>
    public sealed class DraftRun
    {
        public DateTimeOffset StartedUtc { get; set; }
        public DateTimeOffset? FinishedUtc { get; set; }
        /// <summary>The engine run that started it. A running draft whose engine run has ended was cut off by a restart.</summary>
        public string EngineRunId { get; set; }
        /// <summary>running, applied, stopped, failed, not_ready or restarted.</summary>
        public string Outcome { get; set; }
        public long Revision { get; set; }
        public bool Changed { get; set; }
        public string[] AppliedStepIds { get; set; } = Array.Empty<string>();
        public int StopStep { get; set; }
        public string Reason { get; set; }
        public ComposedCheckResult[] BeforeChecks { get; set; } = Array.Empty<ComposedCheckResult>();
        public List<DraftAfterResult> After { get; set; } = new List<DraftAfterResult>();
        /// <summary>The change group's line counts by kind, from the rehearsal just before it ran.</summary>
        public Dictionary<string, int> Kinds { get; set; }
        /// <summary>The person pressed Keep it anyway on a failed after-check (composer contract 10.3).</summary>
        public bool KeptAnyway { get; set; }

        /// <summary>A run with a step someone else does (a person's stop, an assistant's step) pauses there: the step's
        /// id while the run waits on it (Outcome "paused"), else null.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string PausedAt { get; set; }
        /// <summary>The steps the run has passed that someone else does: a person's stop the person continued, and an
        /// assistant's step whose writes applied (or that was continued without them).</summary>
        public List<string> Passed { get; set; } = new List<string>();
        /// <summary>How many undo entries this run has made so far: one per change group, one per assistant step that
        /// wrote something.</summary>
        public int UndoEntries { get; set; }
        /// <summary>How many steps the newest undo entry covers, for the status bar's undo label.</summary>
        public int LastUndoSteps { get; set; }
        /// <summary>The gated steps the person continued past: recorded as skipped in the guided sub-run.</summary>
        public List<string> Skipped { get; set; } = new List<string>();
        /// <summary>The guided sub-run the draft's gated steps run in (lane l9-finish), from the start of the run.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string GuidedRunId { get; set; }
    }

    public static class DraftAfterStatus
    {
        public const string Ran = "ran";
        public const string Passed = "passed";
        public const string Failed = "failed";
        public const string CouldNotRun = "could_not_run";
        public const string CouldNotCheck = "could_not_check";
        /// <summary>Not run because a check before an outward step failed (composer contract 1.6).</summary>
        public const string NotRun = "not_run";
    }

    /// <summary>One after step or one after check, as it came out.</summary>
    public sealed class DraftAfterResult
    {
        public string StepId { get; set; }
        public int StepNumber { get; set; }
        /// <summary>"step" for an after step, "check" for an after check, "read" for a read before the change.</summary>
        public string What { get; set; }
        public string Op { get; set; }
        public string Kind { get; set; }
        public string Status { get; set; }
        public string Text { get; set; }
    }

    // ---- the view, one shape for both doors (composer contract 6.1) --------------------------------------------

    public sealed class DraftView
    {
        public string DraftId { get; set; }
        public string Title { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string Words { get; set; }
        /// <summary>"From your words", "From the Plan", "Saved" or the workflow's name.</summary>
        public string Source { get; set; }
        /// <summary>One of <see cref="DraftStates"/>.</summary>
        public string State { get; set; }
        /// <summary>The Plan's word for the draft's row, and its note (Plan-heart contract 14).</summary>
        public string PlanWord { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string PlanNote { get; set; }
        public bool Approved { get; set; }
        /// <summary>The reason a held draft waits for the person, or null.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string Held { get; set; }
        /// <summary>The engine-built summary sentence (contract 10.3).</summary>
        public string Summary { get; set; }
        public string[] Notes { get; set; } = Array.Empty<string>();
        /// <summary>A banner the draft needs shown: stopped, one check failed, the restart line. Null when none.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public DraftBanner Banner { get; set; }
        public DraftQuestionView[] Questions { get; set; } = Array.Empty<DraftQuestionView>();
        public DraftBlankView[] Blanks { get; set; } = Array.Empty<DraftBlankView>();
        public DraftStepView[] Steps { get; set; } = Array.Empty<DraftStepView>();
        /// <summary>Set when the run stopped because a value it would write changed after the draft was made: edit_draft
        /// 'keep_theirs' drops that step, 'apply_draft' writes the draft over it (UAT S12-5-004).</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public DraftOverwriteView Overwrite { get; set; }
        public DraftStepView[] After { get; set; } = Array.Empty<DraftStepView>();
        /// <summary>How many undo entries the run makes (one per change group).</summary>
        public int UndoSteps { get; set; }
        /// <summary>The parts waiting for the assistant.</summary>
        public int PartsWaiting { get; set; }
        /// <summary>What happened, once it ran: who filled each blank, the before and after checks.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public DraftRecord Record { get; set; }
        /// <summary>The engine's one line about the last action (edit_draft, run_draft), or null.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string Sentence { get; set; }
        /// <summary>Copy the request (composer contract 9): every part still waiting for words, as plain text for any
        /// assistant, chat or person. Null when nothing waits for words.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string RequestText { get; set; }
        /// <summary>True when an after step did not run or an after check could not check, so Try again has work.</summary>
        public bool CanTryAgain { get; set; }
        /// <summary>The menu (composer contract 8, below the bar): the steps a draft from words may pick from. Null on a
        /// draft that did not come from words.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public DraftMenuItem[] Menu { get; set; }
        /// <summary>DraftCopy.PickTheSteps while the menu shows. Null otherwise.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string MenuLine { get; set; }
        /// <summary>The step the run is paused at (a person's stop, or an assistant's step), or null.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string PausedAt { get; set; }
        /// <summary>True while the run waits at a person's stop: Continue is the person's, on the human-only target.</summary>
        public bool CanContinue { get; set; }
        /// <summary>What one Undo takes back, once the draft changed the model ("Undo: ran 7 steps from your words").
        /// Written by the engine for the status bar; null until the draft changed something.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string UndoLabel { get; set; }
        /// <summary>The draft as a format-3 workflow, only on the answer to edit_draft open_in_author. Null otherwise.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public DraftDocument Document { get; set; }
    }

    /// <summary>A draft rendered as a format-3 workflow file (composer contract 10.1), for Author to open unsaved.</summary>
    public sealed class DraftDocument
    {
        /// <summary>A workflow name free in this model's library.</summary>
        public string Name { get; set; }
        /// <summary>The whole file, frontmatter and steps.</summary>
        public string Text { get; set; }
    }

    /// <summary>One step a draft from words may pick (lane L5a): the engine's own template over an allowlisted op.</summary>
    public sealed class DraftMenuItem
    {
        public string Id { get; set; }
        public string Title { get; set; }
        /// <summary>What it changes, in the step row's words ("Adds 1 measure").</summary>
        public string Changes { get; set; }
        /// <summary>True when the person's words named it; the rest follow, below the matches.</summary>
        public bool Matched { get; set; }
    }

    /// <summary>One option of a question that lists from the model (composer contract 2.2): the ref the engine uses
    /// and the name a person reads.</summary>
    public sealed class DraftOption
    {
        public string Ref { get; set; }
        public string Label { get; set; }
    }

    public sealed class DraftBanner
    {
        /// <summary>stopped | check_failed | restarted | nothing_changed</summary>
        public string Kind { get; set; }
        public string Title { get; set; }
        public string Text { get; set; }
    }

    public sealed class DraftQuestionView
    {
        public string Name { get; set; }
        public string Question { get; set; }
        public string Type { get; set; }
        public string By { get; set; }
        public string[] Values { get; set; } = Array.Empty<string>();
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string Step { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string Answer { get; set; }
        /// <summary>The options a question that lists from the model offers, read when the view is built. Null for a
        /// question with no list, or one whose list lives outside the model (the source, the service).</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public DraftOption[] Options { get; set; }
        /// <summary>True for a question on a person's stop: it is asked when the run stops there, not before it runs
        /// (composer contract 1.4). Left out when false.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool AtStop { get; set; }
    }

    public sealed class DraftBlankView
    {
        public int Step { get; set; }
        public string StepId { get; set; }
        public string Name { get; set; }
        public string Kind { get; set; }
        public string State { get; set; }
        /// <summary>The row text of contract 4.2.</summary>
        public string Line { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string Text { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string FilledBy { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string RequestId { get; set; }
        public int AttemptsLeft { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string Reason { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string[] Choices { get; set; }
        /// <summary>This part's request, to copy, while it waits for words. Null once it is written.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string Request { get; set; }
    }

    public sealed class DraftStepView
    {
        public string Id { get; set; }
        public int Number { get; set; }
        public string Title { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string Object { get; set; }
        /// <summary>"Adds 1 table", "Changes 4 types", "Sets 1 description".</summary>
        public string Changes { get; set; }
        /// <summary>"you" and "asst": the two tags.</summary>
        public string[] Tags { get; set; } = Array.Empty<string>();
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public DraftCheckLine Check { get; set; }
        public bool Kept { get; set; }
        /// <summary>Done, Running, Waiting, Failed after it ran, Stopped, Did not run, Dropped; null before a run.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string Status { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string Confidence { get; set; }
        /// <summary>"Also dropped: needs step N" on a step a drop took with it.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string Note { get; set; }
        /// <summary>A gated step your assistant does: the guided run its gate runs in, once the draft runs.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string RunId { get; set; }
    }

    public sealed class DraftCheckLine
    {
        /// <summary>"Checked", "Checked once written" or "Checks after it runs".</summary>
        public string Phase { get; set; }
        public string Text { get; set; }
    }

    public sealed class DraftRecord
    {
        public DraftFillRecord[] Blanks { get; set; } = Array.Empty<DraftFillRecord>();
        public ComposedCheckResult[] BeforeChecks { get; set; } = Array.Empty<ComposedCheckResult>();
        public DraftAfterResult[] After { get; set; } = Array.Empty<DraftAfterResult>();
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string Ran { get; set; }
    }

    public sealed class DraftFillRecord
    {
        public string Blank { get; set; }
        /// <summary>"Your assistant", "You, with Write it yourself", "Semanticus, fixed template".</summary>
        public string FilledBy { get; set; }
        public string Checks { get; set; }
    }

    /// <summary>One step as compose_task takes it: a single op with its arguments, or a titled step with several
    /// actions, its own blanks, checks and brief. Unknown fields are refused.</summary>
    public sealed class ComposeStepInput
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public string Op { get; set; }
        public Dictionary<string, System.Text.Json.JsonElement> Args { get; set; }
        public List<ComposeActionInput> Actions { get; set; }
        public List<DraftBlank> Blanks { get; set; }
        public List<ComposedCheck> Checks { get; set; }
        public string Brief { get; set; }
        /// <summary>semanticus (the default), assistant or person. A step someone else does names no op.</summary>
        public string Performer { get; set; }
    }

    public sealed class ComposeActionInput
    {
        public string Op { get; set; }
        public Dictionary<string, System.Text.Json.JsonElement> Args { get; set; }
    }
}
