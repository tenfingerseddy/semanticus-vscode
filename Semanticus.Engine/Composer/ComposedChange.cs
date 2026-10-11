// The composer's change group, as the engine applies it (composer contract 3.2) and rehearses it (section 7).
//
// WHAT THIS IS AND IS NOT. These are the engine-side input and result shapes of ONE change group: a run of
// consecutive performer: semanticus steps whose arguments are already resolved, except for blanks. The draft,
// its questions, its run states and its persistence are lane L4's (Draft.cs, DraftRunner.cs); the v3 format
// keys are lane L1's (Workflow.cs, WorkflowParser.cs). L4 maps a draft's kept change steps into a
// ComposedChange and hands it here. Keeping the shape this small is what lets the three lanes build in
// parallel without editing each other's files.
//
// A STEP MAY HOLD MORE THAN ONE ACTION. The frames' step 1, "Import the Returns table, 7 columns", is one
// create_import_table plus one create_column per source column. The draft builder writes that as one step
// (one row in Studio, one tick in the obligations), so a step carries a list and every action in it lands in
// the same undo entry as the rest of the group.

using System;
using System.Collections.Generic;

namespace Semanticus.Engine.Composer
{
    /// <summary>One change group: the steps the engine applies in ONE MutateAsync, as one undo entry.</summary>
    public sealed class ComposedChange
    {
        /// <summary>The draft this group belongs to. Null outside a draft (a direct assistant step, contract 3.4).</summary>
        public string DraftId { get; set; }

        /// <summary>The undo label. Contract 3.2: "one undo entry, label = the draft title".</summary>
        public string Title { get; set; }

        /// <summary>The saved workflow this change performs, when it came from one. A composed run of a workflow
        /// counts as that workflow's performing step for every op it applies (contract 3.2, step 2), so a binding
        /// that requires this workflow lets the change through. Null for a change drafted from words, which a
        /// binding treats exactly like the bare op.</summary>
        public string Workflow { get; set; }

        public List<ComposedStep> Steps { get; set; } = new List<ComposedStep>();

        /// <summary>True only when the person's own Approve and run covers this exact change (composer contract 5.4):
        /// the draft's held approval, given after every part passed and still standing for the words it approved.
        /// A change that sets who can see which data is refused without it. Never read off the wire.</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public bool PersonApproved { get; set; }

        /// <summary>The draft's answers by name (its inputs and its questions), which a check's <c>when</c> is read
        /// against. Empty outside a draft. Never read off the wire.</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public IReadOnlyDictionary<string, string> Answers { get; set; } = new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>What each property the change writes said when the change was made, keyed "kind|objectRef" (a
        /// draft's <see cref="Draft.Baselines"/>). Compared on the dispatcher inside the write: one that says something
        /// else now stops the change before anything is written (UAT S12-5-004). Null outside a draft. Never read off
        /// the wire.</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public IReadOnlyDictionary<string, string> Baselines { get; set; }
    }

    public sealed class ComposedStep
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public List<ComposedAction> Actions { get; set; } = new List<ComposedAction>();
        public List<ComposedBlank> Blanks { get; set; } = new List<ComposedBlank>();
        public List<ComposedCheck> Checks { get; set; } = new List<ComposedCheck>();
    }

    /// <summary>One op from the executable allowlist and its arguments, by the op's own MCP argument names.
    /// A value may be <c>{{blanks.&lt;name&gt;}}</c>, filled from the step's blank. An array argument (refs,
    /// terms, variants) is written as a JSON array.</summary>
    public sealed class ComposedAction
    {
        public string Op { get; set; }
        public Dictionary<string, string> Args { get; set; } = new Dictionary<string, string>(StringComparer.Ordinal);
    }

    /// <summary>Text a step needs that neither Semanticus nor Jev can write (contract 1.5).</summary>
    public sealed class ComposedBlank
    {
        public string Name { get; set; }
        /// <summary>dax | description | m | name | choice</summary>
        public string Kind { get; set; }
        /// <summary>The action argument this text becomes.</summary>
        public string Fills { get; set; }
        /// <summary>The submitted text. Null while the blank is still open.</summary>
        public string Text { get; set; }
        public int? MaxChars { get; set; }
        /// <summary>For kind choice: the options the engine listed.</summary>
        public string[] Choices { get; set; }
    }

    /// <summary>A check with its phase (contract 1.7). This lane runs the before and written kinds.</summary>
    public sealed class ComposedCheck
    {
        public string Kind { get; set; }
        /// <summary>before | written | after</summary>
        public string Phase { get; set; }
        /// <summary>What the check reads: a column ref for key_unique and dates_within, a table ref for
        /// source_matches, a blank name for a written check. Null means the step's own object or blanks.</summary>
        public string Target { get; set; }
        public double? Min { get; set; }
        public double? Max { get; set; }
        /// <summary>refs_within: the tables allowed. dates_within: the date column. choice_member: the options.</summary>
        public string[] Of { get; set; }
        /// <summary>The workflow's condition for this check (the predicate grammar, read against the draft's
        /// answers as inputs). Null means always. A check whose condition does not hold does not run.</summary>
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public string When { get; set; }
    }

    public static class ComposedCheckStatus
    {
        public const string Passed = "passed";
        public const string Failed = "failed";
        /// <summary>Never a tick, and never a pass (contract 1.7): "Could not check: not connected".</summary>
        public const string CouldNotCheck = "could_not_check";
    }

    public sealed class ComposedCheckResult
    {
        public string StepId { get; set; }
        public int StepNumber { get; set; }
        public string Kind { get; set; }
        public string Phase { get; set; }
        public string Blank { get; set; }
        public string Status { get; set; }
        /// <summary>The engine's sentence: what was found, or why it could not be checked.</summary>
        public string Text { get; set; }
    }

    public static class ComposedOutcome
    {
        /// <summary>Every step applied, one undo entry.</summary>
        public const string Applied = "applied";
        /// <summary>A before-check failed at step N: the steps before it applied as one undo entry and are kept.</summary>
        public const string Stopped = "stopped";
        /// <summary>An applier threw: the whole batch rolled back and nothing changed.</summary>
        public const string Failed = "failed";
        /// <summary>A blank is still open, so nothing was tried.</summary>
        public const string NotReady = "not_ready";
    }

    public sealed class ComposedChangeResult
    {
        public string Outcome { get; set; }
        /// <summary>The revision after the change, or the unchanged revision when nothing was written.</summary>
        public long Revision { get; set; }
        /// <summary>True when the model changed and one undo entry was made.</summary>
        public bool Changed { get; set; }
        public string[] AppliedStepIds { get; set; } = Array.Empty<string>();
        /// <summary>1-based number of the step that stopped or failed the run; 0 when none did.</summary>
        public int StopStep { get; set; }
        public int StepCount { get; set; }
        /// <summary>The applier's sentence for a failed step, or the failed check's sentence for a stop.</summary>
        public string Reason { get; set; }
        public ComposedCheckResult[] BeforeChecks { get; set; } = Array.Empty<ComposedCheckResult>();
        /// <summary>The line notes the appliers left, in order ("applied: already in the model, so nothing changed").</summary>
        public string[] Notes { get; set; } = Array.Empty<string>();
        /// <summary>Set when nothing was written because a property the change writes no longer says what it said
        /// when the change was made (UAT S12-5-004).</summary>
        public ComposedMoved Moved { get; set; }
    }

    /// <summary>The first line whose object moved under it: the step, the object, the kind, then and now.</summary>
    public sealed class ComposedMoved
    {
        public string StepId { get; set; }
        public int StepNumber { get; set; }
        public string ObjectRef { get; set; }
        public string Kind { get; set; }
        public string Was { get; set; }
        public string Now { get; set; }
    }

    /// <summary>The written checks run for one blank against the rehearsed draft (contract section 7).</summary>
    public sealed class BlankCheckResult
    {
        /// <summary>True only when every check passed. A check that could not run is not a pass.</summary>
        public bool Passed { get; set; }
        public ComposedCheckResult[] Checks { get; set; } = Array.Empty<ComposedCheckResult>();
        /// <summary>Set when the draft itself could not be rehearsed up to the blank's step.</summary>
        public string RehearsalProblem { get; set; }
    }

    /// <summary>The before-run dry run of a whole change group: what it would add and change.</summary>
    public sealed class DraftRehearsalReport
    {
        public bool WouldSucceed { get; set; }
        /// <summary>"Step N could not be made: ..." when a step failed in the rehearsal.</summary>
        public string Problem { get; set; }
        public int FailedStep { get; set; }
        /// <summary>Line counts by applier kind, from the lines that applied in the rehearsal
        /// (create_import_table 1, create_column 7, create_relationship 2, ...). The sentence is L4's (DraftCopy).</summary>
        public Dictionary<string, int> Kinds { get; set; } = new Dictionary<string, int>(StringComparer.Ordinal);
        public int DeltaCount { get; set; }
    }
}
