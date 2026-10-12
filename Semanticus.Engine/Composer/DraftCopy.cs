// Every sentence a draft shows, in one place (composer contract 10.3, 10.6 and 5.4).
//
// The engine writes every string a door shows for a draft; Jev never writes one and neither does a page. Keeping
// them here, and only here, is what lets DraftCopyTests hold each shape byte for byte against the contract, in
// the singular and the plural, with no em dash. Anything new is marked "new" in the contract and goes to Kane's
// copy sweep, not to a decision.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Semanticus.Engine.Composer
{
    public static class DraftCopy
    {
        private static string N(int n) => n.ToString(CultureInfo.InvariantCulture);

        private static string Count(int n, string one, string many) => N(n) + " " + (n == 1 ? one : many);

        // ---- the summary sentence (10.3), only these shapes -------------------------------------------------

        public const string RunsByItself = "Runs by itself.";
        // No instruction here until the Studio draft has its own Approve and run (slice 10, lane U5): naming a button
        // that is not there yet would send a person looking for it (Coordinator 25 Sep).
        public const string ReadySentence = "Ready. It runs as one change.";
        public const string RunningSentence = "Running as one change";
        public const string RunningNote = "Every blank is filled and checked.";

        /// <summary>Before it runs: who does what before the change runs by itself.</summary>
        public static string Summary(int questions, int parts)
        {
            if (questions <= 0 && parts <= 0) return RunsByItself;
            if (questions <= 0) return "Your assistant writes " + Count(parts, "part", "parts") + ", then it runs by itself.";
            if (parts <= 0) return "You answer " + Count(questions, "question", "questions") + ", then it runs by itself.";
            return "You answer " + Count(questions, "question", "questions") + " and your assistant writes "
                 + Count(parts, "part", "parts") + ", then it runs by itself.";
        }

        /// <summary>Done, with no after step that loads or publishes.</summary>
        public static string DoneAll(int steps) =>
            steps == 1 ? "Done. One Undo takes back the step." : "Done. One Undo takes back all " + N(steps) + " steps.";

        /// <summary>Done, with a refresh of a table this change made: Undo takes back the change steps.</summary>
        public static string DoneSteps(int lastChangeStep) =>
            lastChangeStep == 1 ? "Done. One Undo takes back step 1." : "Done. One Undo takes back steps 1 to " + N(lastChangeStep) + ".";

        /// <summary>Done, with a publish after the change.</summary>
        public static string DonePublished(string refreshedTable, string stage) =>
            "Done. One Undo takes back the change to the model"
            + (string.IsNullOrWhiteSpace(refreshedTable) ? "" : ", including the refreshed " + refreshedTable + " data")
            + ". Publishing" + (string.IsNullOrWhiteSpace(stage) ? "" : " to " + stage)
            + " happened after it, so Undo cannot take that back.";

        // ---- notes under the summary (10.3), each only when true ---------------------------------------------

        public const string LoadingNote = "Loading the data runs after the change. Undo removes the table and its data.";

        public static string AfterChecksNote(int checks) =>
            checks == 1 ? "1 check happens after it runs; if it fails, one Undo takes the change back."
                        : N(checks) + " checks happen after it runs; if one fails, one Undo takes the change back.";

        public static string PublishNote(string stage) =>
            "Publishing" + (string.IsNullOrWhiteSpace(stage) ? "" : " to " + stage) + " happens after the change. Undo cannot take that back.";

        /// <summary>When every part is written: "Both parts passed their checks; 1 answer is filled in for you to check."</summary>
        public static string PartsPassed(int parts, int prefilled)
        {
            var head = parts == 1 ? "The part passed its checks" : parts == 2 ? "Both parts passed their checks" : "All " + N(parts) + " parts passed their checks";
            if (prefilled <= 0) return head + ".";
            return head + "; " + Count(prefilled, "answer is", "answers are") + " filled in for you to check.";
        }

        /// <summary>Done: "2 checks ran after it; both passed."</summary>
        public static string AfterChecksRan(int ran, int passed)
        {
            if (ran == 1) return passed == 1 ? "1 check ran after it; it passed." : "1 check ran after it; it did not pass.";
            if (passed == ran) return N(ran) + " checks ran after it; " + (ran == 2 ? "both" : "all") + " passed.";
            return N(ran) + " checks ran after it; " + N(passed) + " passed.";
        }

        /// <summary>The run answer's line naming each kept add (run_draft): what the run added, or adds when it runs.</summary>
        internal static string KeptAdds(IReadOnlyList<string> adds, bool ran) =>
            (ran ? "It added: " : "It adds, when it runs: ") + string.Join("; ", adds) + ".";

        public const string NothingChangedYet = "Nothing has changed yet.";

        /// <summary>A draft whose change History took back (UAT S12-3-002).</summary>
        public const string TakenBack = "Its change was undone in History, so nothing it did is in the model now. Approve and run makes the change again.";

        /// <summary>The waiting footer: "Nothing has changed yet. It runs by itself when both parts pass their checks."</summary>
        public static string WaitingNote(int parts) =>
            NothingChangedYet + " It runs by itself when " + (parts == 1 ? "the part passes its checks." : parts == 2 ? "both parts pass their checks." : "all " + N(parts) + " parts pass their checks.");

        public const string OnNextCall = "Your assistant sees this on its next call. Or copy the request.";

        /// <summary>Before the person has sent the parts: new copy (the Studio primary is Run with your assistant).</summary>
        public const string NotSentYet = "Press Run with your assistant, or write the parts yourself.";

        // ---- blank row text (4.2) -----------------------------------------------------------------------------

        public const string BlankEmpty = "Waiting to be written";
        public const string BlankQueued = OnNextCall;
        public const string BlankWriting = "Your assistant is writing";
        public const string BlankNeeds = "Needs you";

        public static string BlankFilled(string text) => "Written by your assistant, checked: " + text;
        public static string BlankSelf(string text) => "Written by you, checked: " + text;
        public const string BlankGenerated = "Valid formula, from a fixed Semanticus template.";

        /// <summary>"Your assistant asks: " and the assistant's reason word for word.</summary>
        public static string AssistantAsks(string reason) => "Your assistant asks: " + reason;

        // ---- holds, evidence, restart, refusals (10.6 and 5.4, new copy) --------------------------------------

        public const string HeldRowSecurity =
            "Rules about who can see which data wait for you to read them. Read the parts, then press Approve and run.";
        public const string HeldPublish =
            "This workflow publishes after the change, so your assistant's writing waits for you to read it. Read the parts, then press Approve and run.";

        public static string Held(string hold) => hold == DraftHolds.RowSecurity ? HeldRowSecurity : HeldPublish;

        /// <summary>run_draft from the agent door on a held draft.</summary>
        public static string HeldRefusal(string hold) =>
            "This one waits for the person because it "
            + (hold == DraftHolds.RowSecurity ? "sets who can see which data" : "publishes after the change")
            + ". When they say yes, call approve_draft.";

        /// <summary>Approve and run pressed on a held draft before every part passed: any part for the assistant went on
        /// its queue and nothing is approved yet, so the person reads the words before approving them.</summary>
        public const string ApproveAfterParts =
            "Nothing has changed yet. Every part passes its checks first. Then read them and press Approve and run.";

        /// <summary>A direct assistant step that tried to set a row filter (Sol 6, slice 8, finding 1).</summary>
        public const string DirectStepRowSecurity =
            "A step your assistant does cannot set who can see which data. Nothing was written. Put the row filter in a draft, "
            + "so the person reads it and presses Approve and run.";

        /// <summary>A direct step refused after its writes landed, when they could not be taken back.</summary>
        public const string DirectStepKept =
            "Its writes stay in the model as one change, because another edit landed after them. Undo takes them back.";

        /// <summary>An outward step that did not run because a check before it could not run. The reason is the
        /// check's own "Could not check: ..." sentence.</summary>
        public static string NotRunUnchecked(string checkText)
        {
            var why = string.IsNullOrWhiteSpace(checkText) ? "Could not check." : checkText.Trim();
            if (!why.StartsWith("Could not check", StringComparison.Ordinal)) why = "Could not check: " + why;
            return "Did not run. " + why;
        }

        /// <summary>"Sales changed while this waited." The name is the table the blank's formula reads.</summary>
        public static string EvidenceMoved(string what) =>
            (string.IsNullOrWhiteSpace(what) ? "The model" : what) + " changed while this waited.";

        public const string Restarted = "This run ended when Semanticus restarted. Your draft is kept. Run it again.";

        /// <summary>A run stopped at a value changed after the draft was made (UAT S12-5-004): "Sales[Amount] has a new
        /// description since this draft was made. Keep yours, or apply the draft?"</summary>
        public static string Overwrite(string objectRef, string kind) =>
            PlanTasks.NameOfRef(objectRef) + " has a new " + PropertyWord(kind) + " since this draft was made. Keep yours, or apply the draft?";

        public const string OverwriteTitle = "Changed since the draft";
        public const string KeptYours = "Kept yours. That step is dropped, so the draft does not change it.";
        public const string AppliedDraft = "Applied the draft over it.";

        private static string PropertyWord(string kind) => kind switch
        {
            "set_description" => "description",
            "set_dax" => "formula",
            "set_measure_format" => "format",
            "set_summarize_by" => "summarize setting",
            "set_data_category" => "data category",
            "set_column_hidden" => "hidden setting",
            "set_sort_by_column" => "sort by column",
            "set_data_type" => "data type",
            "rename" => "name",
            _ => "value",
        };

        public static string AlsoDropped(int[] needs) =>
            needs.Length == 1 ? "Also dropped: needs step " + N(needs[0])
            : "Also dropped: needs steps " + string.Join(", ", needs.Take(needs.Length - 1).Select(N)) + " and " + N(needs[^1]);

        public const string DirectStep = "Your assistant changes the model here. This step is its own undo step.";

        // ---- steps someone else does (composer contract 1.3; lane author-gaps, B9) ------------------------------

        /// <summary>A person's stop, on its step row.</summary>
        public const string PersonStepChanges = "Stops here for you";
        /// <summary>An assistant's step, on its step row.</summary>
        public const string AssistantStepChanges = "Your assistant changes the model";

        /// <summary>Why a run waits at a person's stop (new copy, goes to Kane's copy sweep).</summary>
        public static string PersonStop(int step, string title) =>
            "Step " + N(step) + " stops here for you: " + title + ". Press Continue when you are ready.";

        /// <summary>The summary while a run waits at an assistant's step (new copy).</summary>
        public static string AssistantStepWaiting(int step) =>
            "Your assistant does step " + N(step) + " and sends its changes. They apply as one undo step.";

        /// <summary>Ready, for a draft that runs step by step (contract 3.2: the summary says how many undo steps it makes).</summary>
        public static string ReadyInSteps(int undoSteps) =>
            undoSteps == 0 ? "Ready. It makes no change to the model that Undo would take back."
            : "Ready. It runs as " + Count(undoSteps, "change", "changes") + ", one Undo each.";

        /// <summary>Done, for a draft that ran as several changes: each Undo takes back one, newest first.</summary>
        public static string DoneInSteps(int changes) =>
            changes == 0 ? "Done. It made no change to the model."
            : changes == 1 ? "Done. One Undo takes back the change." : "Done. It made " + N(changes) + " changes. Each Undo takes back one, newest first.";

        /// <summary>The failed after-check banner, for a draft that ran as several changes.</summary>
        public static string CheckFailedInSteps(string rule, int changes) =>
            rule.TrimEnd('.') + ". " + (changes <= 1 ? "One Undo takes back the change." : "Undo takes back its " + N(changes) + " changes one at a time, newest first.");

        /// <summary>Before a step-by-step draft runs: where it will stop for the person.</summary>
        public static string StopsAt(int step) => "It stops for you at step " + N(step) + ".";

        /// <summary>Before a step-by-step draft runs: which step the assistant does when the run reaches it.</summary>
        public static string AssistantDoesStep(int step) => "Your assistant does step " + N(step) + " when the run gets there.";

        // ---- a gated step your assistant does (lane l9-finish; composer contract 1.3) -------------------------------

        /// <summary>A gated assistant's step, on its step row (new copy, goes to Kane's copy sweep).</summary>
        public const string GuidedStep = "Your assistant does this step, and its checks run the way a guided step's do. If a check fails, the draft stops here.";

        /// <summary>A save_evidence step in a draft with no checked steps: there is no record to save.</summary>
        public const string NoCheckedRecord = "Could not run: this draft has no checked steps, so there is no record to save.";

        /// <summary>A gated step whose gate checks nothing (it asks, or only names its ops), on its step row.</summary>
        public const string GuidedStepNoChecks = "Your assistant does this step the way a guided step runs, and the draft waits for it.";

        /// <summary>What a gated step that changes nothing does, in its row's "changes" column.</summary>
        public const string GuidedStepChanges = "Your assistant does this step";

        /// <summary>The summary while a run waits at a gated step.</summary>
        public static string GuidedStepWaiting(int step, bool writes) =>
            "Your assistant does step " + N(step) + " as a guided step. "
            + (writes ? "Its changes apply as one undo step, then its checks run." : "The draft moves on when it passes.");

        /// <summary>The reason a gated step's guided run records when the person continues past it.</summary>
        public const string GuidedSkipReason = "The person continued the draft without this step.";

        /// <summary>Why a draft stopped when its guided checks were stopped.</summary>
        public static string GuidedRunStopped(string why) =>
            "Its checks were stopped" + (string.IsNullOrWhiteSpace(why) ? "." : ": " + why.Trim().TrimEnd('.') + ".");

        /// <summary>Why a draft stopped at a gated step: the gate's own sentence follows.</summary>
        public static string GuidedCheckFailed(int step, string why) =>
            "Step " + N(step) + " did not pass its checks, so nothing from it was kept. " + (why ?? "").Trim();

        /// <summary>After Continue on an assistant's step the assistant had not done (new copy).</summary>
        public const string ContinuedWithout = "You continued without your assistant's changes for this step.";

        /// <summary>What one Undo takes back, for the status bar (composer contract 10.3): "Undo: ran 7 steps from your
        /// words". A draft that did not come from words names itself.</summary>
        public static string UndoLabel(int steps, bool fromWords, string title) =>
            "Undo: ran " + Count(steps, "step", "steps") + (fromWords ? " from your words" : " of \"" + title + "\"");

        public static string OutsideRun(int n) =>
            "Your assistant also changed " + Count(n, "thing", "things") + " outside this run";

        public const string FailedThreeTimes = "failed its checks 3 times";

        public static string TriesLeft(int n) => n == 1 ? "1 try left." : N(n) + " tries left.";

        /// <summary>The menu's line on a draft from words (composer contract 10.6). Jev is never asked to draft
        /// from words today (lane L5b is open), so the line says only what is true: the person picks, and the
        /// steps the words name are listed first (DraftMenu.For). The honesty lane replaced a line that blamed
        /// Jev's confidence for an ask that never happens. When real Jev drafting lands, this and
        /// <see cref="AddMoreWords"/> are the two strings to change.</summary>
        public const string PickTheSteps = "Pick the steps. The ones your words name come first.";

        /// <summary>The name of refine on a draft from words: it adds words and lists the steps again with the
        /// new words weighing most. Nobody is asked, so the name promises no one. The webview's button is
        /// composer-model.ts WORD.addMoreWords.</summary>
        public const string AddMoreWords = "Add more words";

        // ---- Jev drafts (composer lane L5, new copy, goes to Kane's copy sweep) -----------------------------------

        /// <summary>The summary when Jev drafted the steps from the words: what happened, and that nothing changed.</summary>
        public const string JevDrafted = "Jev picked the steps from your words. Nothing has changed yet.";

        /// <summary>The menu's line on a draft Jev drafted: the steps are Jev's, and the list is there to add another.</summary>
        public const string JevDraftedMenuLine = "Jev picked these steps. Add another from the list, or add more words.";

        /// <summary>The menu's line when Jev WAS asked and answered but was sure of no step. Only then: a draft Jev was
        /// never asked about says PickTheSteps.</summary>
        public const string JevNotSure = "Jev was not sure enough to pick the steps. Pick them. The ones your words name come first.";

        /// <summary>A Plan draft where Jev picked some steps and the words named the rest (the per-clause rule).</summary>
        public static string JevDraftedWithWords(int fromWords) =>
            "Jev picked the steps from your words, except " + Count(fromWords, "step", "steps") + " that your words name directly. Nothing has changed yet.";

        /// <summary>The menu's line on a draft Jev and the words drafted together.</summary>
        public const string JevAndWordsMenuLine = "Jev picked some of these steps and your words named the rest. Add another from the list, or add more words.";

        /// <summary>A Plan draft where Jev was asked, was sure of no step, and the words named the steps.</summary>
        public const string WordsDrafted = "Your words name these steps. Nothing has changed yet.";

        /// <summary>The menu's line on a draft the words drafted.</summary>
        public const string WordsDraftedMenuLine = "Your words name these steps. Add another from the list, or add more words.";

        /// <summary>The same, when one or more steps arrived dropped because Jev was not sure of them.</summary>
        public static string JevDraftedWithUnsure(int unsure) => JevDrafted + " " + UnsureDropped(unsure);

        /// <summary>Steps Jev only leaned to, which arrive dropped. Said whoever named the other steps (R3-D7s).</summary>
        public static string UnsureDropped(int unsure) =>
            Count(unsure, "step Jev was not sure of arrives", "steps Jev was not sure of arrive") + " dropped. "
            + (unsure == 1 ? "Keep it to use it." : "Keep them to use them.");

        /// <summary>Add more words, for the steps words typed earlier gave once they were read (R3-D6b-copy).</summary>
        public static string EarlierWordsAdded(IReadOnlyList<string> earlier, int added) =>
            "Jev also read your earlier words " + Quoted(earlier, earlier.Count) + ", which gave " + Count(added, "step", "steps") + ".";

        /// <summary>Add more words while Jev could not answer: the words are kept, not lost (R3-D6b).</summary>
        public const string WordsKeptUnread = "Jev could not read your words just now, so they are kept on the draft and asked again with your next Add more words.";

        /// <summary>Add more words when no ask could leave (the point switched off, no permission): the words are kept, and
        /// nothing promises the next Add more words reads them, because it asks no one either (verifier on
        /// m11/sol-fixes-1, D5). <paramref name="notAsked"/> is the reason, "Jev was not asked: ...".</summary>
        public static string WordsKeptNotAsked(string notAsked) => notAsked + " Your words are kept on the draft.";

        /// <summary>Add more words reads a draft's worth of words at a time, the new words first, so words kept from before
        /// can wait for the next one (verifier on m11/sol-fixes-1, D8). Said whenever any did.</summary>
        public static string EarlierWordsStillWaiting(int waiting) =>
            N(waiting) + " of your earlier words " + (waiting == 1 ? "is" : "are") + " not read yet. The next Add more words reads "
            + (waiting == 1 ? "it." : "more of them.");

        /// <summary>Add more words, when Jev drafted again with the new words.</summary>
        public static string WordsAdded(int added) => "Your new words name " + Count(added, "more step", "more steps") + ".";

        public static string JevRedrafted(int added) =>
            added == 0 ? "Jev read your new words and found no step to add." : "Jev read your new words and added " + Count(added, "step", "steps") + ".";

        /// <summary>Add more words, once Jev drafts on the Plan: the steps really added and kept, counted whoever named
        /// them (D4b), and a step that arrives dropped said apart, never counted as added (D4).</summary>
        public static string MoreWordsAdded(int byJev, int byWords, int dropped)
        {
            var kept = byJev + byWords;
            var head = kept == 0 ? (dropped == 0 ? "Jev read your new words and found no step to add." : "Jev read your new words and was not sure of any step.")
                : byWords == 0 ? "Jev read your new words and added " + Count(kept, "step", "steps") + "."
                : byJev == 0 ? WordsAdded(kept)
                : "Added " + Count(kept, "step", "steps") + " from your new words: Jev picked " + N(byJev) + " and your words named " + N(byWords) + ".";
            return dropped == 0 ? head
                : head + " " + (dropped == 1 ? "1 step Jev was not sure of arrives dropped. Keep it to use it."
                                             : N(dropped) + " steps Jev was not sure of arrive dropped. Keep them to use them.");
        }

        /// <summary>Workflows: words that name more steps than one description reads (D6a, then Sol's finding 4 on
        /// m11/integration 568435d3). Each one past the limit is named, and it is here as a step to fill in.</summary>
        public static string StepsNotRead(IReadOnlyList<string> notRead, int max) => StepsNotRead(notRead, notRead.Count, max);

        /// <summary>As above, where <paramref name="clauses"/> clauses were not read in all: past the few named, the rest are
        /// counted, so the sentence stays short at any length (verifier on m11/sol-fixes-1, D3).</summary>
        public static string StepsNotRead(IReadOnlyList<string> notRead, int clauses, int max) =>
            "Your words name more than " + N(max) + " steps, so Semanticus read the first " + N(max) + ". It did not read "
            + Quoted(notRead, clauses) + (clauses == 1 ? ", which is here as a step to fill in." : ", which are here as steps to fill in.");

        /// <summary>The one step that holds every clause past the ones kept apart (DescribedSteps.KeptNotRead).</summary>
        public static string RestNotReadTitle(int clauses) => "The other " + N(clauses) + " steps in your words";

        /// <summary>That step's What it does: why it is one step, and the words themselves, so none is out of sight.</summary>
        public static string DescribeRestNotRead(int clauses, string words) =>
            "Semanticus reads the first " + N(DescribedSteps.MaxSteps) + " steps of a description, so it did not read these "
            + N(clauses) + ". They are kept here together, in your words: " + words + ". Make each one a step of its own, or remove the ones you do not need.";

        /// <summary>A Plan draft's words no ask read, as a step to fill in when the draft opens in Author (verifier on
        /// m11/sol-fixes-1, D6: Author used to get the drafted steps only).</summary>
        public const string PlanWordsNotRead = "The draft on the Plan did not read these words, so this step has no action yet. Say what it does, then choose what it may use.";

        /// <summary>The same, for the one step that holds the rest of them.</summary>
        public static string PlanRestNotRead(int clauses, string words) =>
            "The draft on the Plan did not read these " + N(clauses) + " steps of your words, so they are kept here together: " + words
            + ". Make each one a step of its own, or remove the ones you do not need.";

        /// <summary>open_in_author, when the words the draft did not read came along as steps to fill in.</summary>
        public static string OpenedWithNotRead(int clauses) =>
            "Your words the draft did not read are here too, as " + (clauses == 1 ? "a step" : "steps") + " to fill in.";

        /// <summary>open_in_author, when words the adding rule left with no step came along as steps to fill in.</summary>
        public static string OpenedWithNoStep(int clauses) =>
            "Your words that no step here does are here too, as " + (clauses == 1 ? "a step" : "steps") + " to fill in, each saying why.";

        /// <summary>A step past the most one description reads, in its What it does field.</summary>
        public static string DescribeNotRead(int max) =>
            "Semanticus reads the first " + N(max) + " steps of a description, so it did not read this one. Say what this step does, then choose what it may use.";

        /// <summary>The Plan's words past the most one draft asks about (D6a). Not "are here": a Plan clause can give no
        /// step, or the draft can open as the menu with none (R2-D6a), so what is true is that only the first ones were
        /// read. Following it works: Add more words asks exactly the words not read yet (R3-D6a).</summary>
        public static string PlanStepsLeftOut(int leftOut, int max) =>
            "Your words name more than " + N(max) + " steps, so only the first " + N(max) + " were read. Add the other "
            + N(leftOut) + " " + WithAddMoreWords + ".";

        /// <summary>The Plan draft's note while words no answered ask has read are kept (Sol's finding 4, R3-D6b): past
        /// the limit, or typed while Jev could not answer. <paramref name="offTheList"/> are the ones whose step the list
        /// of steps does not hold, which only Add more words can add, said plainly.</summary>
        /// <summary>Past the first few words it names, the note counts the rest, so a draft holding thousands of clauses
        /// still has a note a person can read (verifier on m11/sol-fixes-1, D3). <paramref name="notAsked"/> is why no ask
        /// could leave ("Jev was not asked: ..."), said in place of the promise, which would be false (D5).</summary>
        public static string PlanNotRead(IReadOnlyList<string> notRead, IReadOnlyList<string> offTheList = null, string notAsked = null) =>
            "Not read yet: " + Quoted(notRead, notRead.Count) + ". "
            + (notAsked ?? "Add more words asks Jev about " + (notRead.Count == 1 ? "it" : "them") + " again.")
            + (offTheList == null || offTheList.Count == 0 ? ""
               : " The list of steps has no step for " + Quoted(offTheList, offTheList.Count) + ", so "
                 + (notAsked == null ? "Add more words is the only way to add " : "only Jev can add ") + (offTheList.Count == 1 ? "it" : "them") + ".");

        /// <summary>The Plan draft's note for words the adding rule left with no step (AddingRule): the words, then the
        /// rule's reason, which is true of each of them.</summary>
        public static string NoStepFor(IReadOnlyList<string> words, string reason) =>
            "No step for " + Quoted(words, words.Count) + ". " + reason;

        /// <summary>The Plan draft's note for an add the adding rule offers unticked: the person's words, then the one true
        /// line on why it is not ticked.</summary>
        public static string NotTickedFor(IReadOnlyList<string> words, string reason) =>
            "Not ticked: " + Quoted(words, words.Count) + ". " + reason;

        /// <summary>The Plan's sentence when the adding rule offered adds unticked.</summary>
        public static string NotTicked(int n) =>
            Count(n, "step is", "steps are") + " not ticked. The notes say why. Tick " + (n == 1 ? "it" : "them") + " to use " + (n == 1 ? "it." : "them.");

        /// <summary>How many kept words a note or a sentence names by their words; the rest are counted.</summary>
        public const int NamedMost = 5;
        private const int QuotedChars = 80;

        /// <summary>The first <see cref="NamedMost"/> of <paramref name="words"/>, each in quotes and cut near 80 characters,
        /// then "and N more" for the others of <paramref name="total"/>.</summary>
        private static string Quoted(IReadOnlyList<string> words, int total)
        {
            var named = words.Take(NamedMost).Select(w => "\"" + Clip(w) + "\"").ToList();
            var more = total - named.Count;
            return string.Join(", ", named) + (more > 0 ? " and " + N(more) + " more" : "");
        }

        private static string Clip(string w)
        {
            if (w == null || w.Length <= QuotedChars) return w;
            var space = w.LastIndexOf(' ', QuotedChars);
            return w.Substring(0, space > QuotedChars / 2 ? space : QuotedChars).TrimEnd(' ', ',') + "...";
        }

        public const string InAuthor = "in Author";
        public const string WithAddMoreWords = "with Add more words";

        /// <summary>A described step no action matches (the word match), in its What it does field.</summary>
        public const string DescribeNoMatch = "No action Semanticus knows matches these words. Say what this step does, then choose what it may use.";
        /// <summary>A described step whose words do not ask to add, where the word match's first choice was a step that adds
        /// and no other action matches: the adding rule's own reason (AddingRule, version 2). To add, start with add.</summary>
        public const string DescribeNotAnAdd = AddingRule.NoAddReason;
        /// <summary>A described step whose words ask to take something away, where the word match's first choice was a step
        /// that adds: the adding rule's own reason. No step template deletes (delete_object is the assistant's, not a step's).</summary>
        public const string DescribeTakeAway = AddingRule.TakeAwayReason;
        /// <summary>A described step only Jev read as one that adds (the word match found no step), for words that do not
        /// ask to add (m11/completeness round 4).</summary>
        public const string DescribeJevNotAnAdd = "Jev read these words as a step that adds, but they do not ask to add, so no step that adds is chosen. To add something, start with add or create. Say what this step does, then choose what it may use.";
        /// <summary>A draft on the Plan whose words name something a step would add without asking to add it: that step is
        /// not their match (m11/completeness round 4), and no step deletes or removes.</summary>
        public const string PlanNotAnAdd = "Some of your words name something a step would add, but they do not ask to add it, so no step that adds is matched to them. To add it, start with add or create. A draft has no step that deletes or removes yet: do that part yourself, or ask your assistant.";
        /// <summary>A described step Jev said no step template fits.</summary>
        public const string DescribeJevNone = "Jev found no action Semanticus knows that fits these words. Say what this step does, then choose what it may use.";
        /// <summary>A described step Jev leaned towards a template for, but not surely.</summary>
        public const string DescribeJevUnsure = "Jev was not sure enough to choose an action for this step. Say what this step does, then choose what it may use.";

        /// <summary>Added to a description's sentence when Jev chose the actions of some steps.</summary>
        public static string DescribeChosen(int byJev, int steps) =>
            byJev == 0 ? "" : byJev == steps ? " Jev chose what each step may use." : " Jev chose what " + Count(byJev, "step", "steps") + " may use.";

        /// <summary>Why Jev was not asked, for the assistant door and the notes. Never the whole answer to a person.</summary>
        public static string JevNotAsked(string reason) => "Jev was not asked: " + Lower(reason) + ".";

        /// <summary>Why Jev's answer was not used.</summary>
        public static string JevNotUsed(string reason) =>
            Semanticus.Engine.Decisions.JevDecisionOracle.IsNotAvailable(reason)
                ? Semanticus.Engine.Decisions.JevDecisionOracle.NotAvailableReason
                : "Jev's answer was not used: " + Lower(reason) + ".";

        private static string Lower(string s)
        {
            s = (s ?? "no reason given").Trim().TrimEnd('.');
            return s.Length == 0 ? "no reason given" : char.ToLowerInvariant(s[0]) + s.Substring(1);
        }

        /// <summary>After Keep it anyway on a failed after-check (new copy, goes to Kane's copy sweep).</summary>
        public const string KeptAnyway = "You kept it, although a check failed after the change.";

        /// <summary>The first line of Copy the request (section 9).</summary>
        public static string CopyHead(int parts, string title) =>
            "Semanticus has " + Count(parts, "part", "parts") + " for you to write in \"" + title + "\".";

        // ---- banners ----------------------------------------------------------------------------------------

        public const string CheckFailedTitle = "One check failed after the change";

        public static string CheckFailedText(string rule, int lastChangeStep, bool tableGoes) =>
            rule.TrimEnd('.') + ". One Undo takes back "
            + (lastChangeStep == 1 ? "step 1" : "steps 1 to " + N(lastChangeStep))
            + (tableGoes ? ", and the table goes with its data." : ".");

        public static string StoppedTitle(int step, int of) => "Stopped at step " + N(step) + " of " + N(of);

        /// <summary>"So far: Step 1 ran and is kept as one change. Steps 3 to 5 did not run."</summary>
        public static string SoFar(int[] ran, int stoppedAt, int of)
        {
            var parts = new List<string>();
            if (ran.Length > 0)
                parts.Add((ran.Length == 1 ? "Step " + N(ran[0]) + " ran" : "Steps " + Range(ran) + " ran") + " and " + (ran.Length == 1 ? "is" : "are") + " kept as one change.");
            else parts.Add("Nothing changed.");
            if (stoppedAt < of)
                parts.Add(stoppedAt + 1 == of ? "Step " + N(of) + " did not run." : "Steps " + N(stoppedAt + 1) + " to " + N(of) + " did not run.");
            return "So far: " + string.Join(" ", parts);
        }

        private static string Range(int[] steps) =>
            steps.Length == 0 ? "" : steps.Length == 1 ? N(steps[0])
            : steps.Zip(steps.Skip(1), (a, b) => b == a + 1).All(x => x) ? N(steps[0]) + " to " + N(steps[^1])
            : string.Join(", ", steps.Take(steps.Length - 1).Select(N)) + " and " + N(steps[^1]);

        // ---- step rows ----------------------------------------------------------------------------------------

        public static string PhaseWord(string phase) =>
            phase == "before" ? "Checked" : phase == "written" ? "Checked once written" : "Checks after it runs";

        /// <summary>"Adds 1 table and 7 columns", "Changes 4 types", "Sets 1 description", from the step's ops. A step
        /// that only reads says what it reads: "Reads the model", or "Reads the source" for the source's schema.</summary>
        public static string Changes(IEnumerable<string> ops)
        {
            var list = ops.ToList();
            if (list.Count > 0 && list.All(o => OpParameterKinds.GroupOf(o) == OpParameterKinds.Prepare))
                return list.Any(o => o == "get_source_schema") ? "Reads the source" : "Reads the model";
            ops = list;
            var counts = ops.GroupBy(o => Noun(o).Verb + "|" + Noun(o).One + "|" + Noun(o).Many)
                .Select(g => (Key: g.Key.Split('|'), N: g.Count())).ToList();
            var byVerb = counts.GroupBy(c => c.Key[0]).Select(g =>
                g.Key + " " + JoinAnd(g.Select(c => Count(c.N, c.Key[1], c.Key[2])).ToList())).ToList();
            return byVerb.Count == 0 ? "" : string.Join("; ", byVerb);
        }

        private static string JoinAnd(List<string> items) =>
            items.Count <= 1 ? string.Concat(items) : string.Join(", ", items.Take(items.Count - 1)) + " and " + items[^1];

        private static (string Verb, string One, string Many) Noun(string op) => op switch
        {
            "create_import_table" => ("Adds", "table", "tables"),
            "create_column" => ("Adds", "column", "columns"),
            "create_relationship" => ("Adds", "relationship", "relationships"),
            "create_measure" or "starter_measure" => ("Adds", "measure", "measures"),
            "create_calculation_group" => ("Adds", "calculation group", "calculation groups"),
            "create_calculation_item" => ("Adds", "calculation item", "calculation items"),
            "create_role" => ("Adds", "role", "roles"),
            "generate_date_table" => ("Adds", "date table", "date tables"),
            "generate_time_intelligence" => ("Adds", "set of time measures", "sets of time measures"),
            "set_column_data_type" => ("Changes", "type", "types"),
            "update_measure" => ("Changes", "formula", "formulas"),
            "rename_object" => ("Changes", "name", "names"),
            "set_relationship_crossfilter" or "set_relationship_cardinality" or "set_relationship_active" => ("Changes", "relationship", "relationships"),
            "set_description" => ("Sets", "description", "descriptions"),
            "set_measure_format" => ("Sets", "format", "formats"),
            "set_display_folder" => ("Sets", "folder", "folders"),
            "set_synonyms" => ("Sets", "list of synonyms", "lists of synonyms"),
            "set_table_permission" => ("Sets", "row filter", "row filters"),
            "mark_date_table" => ("Sets", "date table mark", "date table marks"),
            "set_column_hidden" => ("Sets", "hidden flag", "hidden flags"),
            "set_summarize_by" => ("Sets", "summary setting", "summary settings"),
            "set_data_category" => ("Sets", "data category", "data categories"),
            "set_sort_by_column" => ("Sets", "sort order", "sort orders"),
            "refresh_partition" => ("Loads", "table's data", "tables' data"),
            "save_model" => ("Saves", "model", "models"),
            "deploy_stage" or "deploy_live" => ("Publishes", "model", "models"),
            _ => ("Runs", "step", "steps"),
        };

        /// <summary>The word for what a blank holds, for the part's title: "Return rate formula".</summary>
        public static string KindWord(string kind) => kind switch
        {
            "dax" => "formula",
            "description" => "description",
            "m" => "Power Query",
            "name" => "name",
            "choice" => "choice",
            _ => "text",
        };

        /// <summary>The words a waiting part carries on the Plan and in the fold: "Return rate formula".</summary>
        public static string PartTitle(string objectName, string stepTitle, string kind) =>
            (string.IsNullOrWhiteSpace(objectName) ? stepTitle : objectName) + " " + KindWord(kind);

        /// <summary>A written check in plain words, for the packet and the Studio line under the Brief.</summary>
        public static string CheckWords(ComposedCheck c) => c.Kind switch
        {
            "valid_dax" => "A valid formula against the model as the draft leaves it.",
            "dax_lint" => "No formula warnings.",
            "refs_within" => "Uses only " + JoinAnd((c.Of ?? Array.Empty<string>()).ToList()) + ".",
            "max_chars" => "Short enough for the field.",
            "no_formula_text" => "Plain words, no formula text.",
            "valid_m" => "Power Query with a let and an in, brackets and quotes balanced.",
            "choice_member" => "One of the options listed.",
            "name_free" => "The name is free.",
            "key_unique" => "The key is unique.",
            "dates_within" => "The dates fall inside the date table.",
            "source_matches" => "The source still has these columns.",
            "result_range" => "The result is between " + Percent(c.Min) + " and " + Percent(c.Max) + ".",
            "row_count" => "The row count is as expected.",
            "dax_probe" => (c.Of?.Length ?? 0) > 0 ? "The measure matches the number you gave." : "The measure returns a value.",
            "bpa_clean" => "It passes the best practice rules.",
            _ => "Check " + c.Kind + ".",
        };

        internal static string Percent(double? v) =>
            v == null ? "?" : (v.Value * 100).ToString("0.##", CultureInfo.InvariantCulture) + "%";

        /// <summary>result_range's failed line: "Return rate is 134%. The check needs 0% to 100%."</summary>
        public static string RangeFailed(string what, double value, double? min, double? max) =>
            what + " is " + Percent(value) + ". The check needs " + Percent(min) + " to " + Percent(max) + ".";

        public const string NotConnected = "Could not check: not connected.";
        public const string NotConnectedRun = "Could not run: not connected.";
    }
}
