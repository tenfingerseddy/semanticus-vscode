// A change set on a plan item (docs/redesign/contracts/change-contract.md, M3).
//
// WHY THIS IS A SEPARATE FILE FROM ModelPlanView.cs. The plan's wire shape is one idea and these types are a
// second one bolted to it: the change contract's set, expressed in the plan's own words so a page can render
// it without knowing anything about Semanticus.Engine.Jev. Keeping them apart also keeps two lanes editing the
// plan at once out of each other's diffs.
//
// THE ONE RULE. A change set is a PROPOSAL and nothing more. It carries no truth about the model; it carries
// what somebody worked out the model WOULD look like, the versions it was worked out against, and a digest
// over both. Nothing here is ever the reason a change lands: the reason is that a person kept the line and
// approved the set, and the engine then checked that nothing had moved. A set read off a page, off the
// sidecar, or off an assistant's last call is data, never permission.
//
// WHERE IT LIVES. On the item, on the wire, and in the plan's own memory keyed by item id (PlanMemory.cs).
// That is the same sidecar, under the same per-model durable identity, that the skips and the history live
// under, so a proposal cannot leak between two models opened from one folder.

using System;

namespace Semanticus.Engine
{
    /// <summary>Where one item stands in the One Plan list, as distinct from <see
    /// cref="ModelPlanItem.Status"/>, which says where the WORK stands.
    ///
    /// The two are deliberately different questions and it is worth being exact about it, because a reader
    /// who folds them will write a page that lies. <c>Status</c> answers "can this be acted on right now"
    /// (ready, proposed, done, skipped, running, waiting) and every value of it but "skipped" is recomputed
    /// from the model on every call. <c>State</c> answers "how far along the propose-review-approve road is
    /// this one", which is a fact about the change set attached to the item, not about the model.
    ///
    /// HOW "applied" AND "done" RELATE, because this is the pair that gets confused. <c>Applied</c> is the
    /// CHANGE SET landing: the kept lines were written, in one transaction, on one undo entry. <c>Done</c> is
    /// the GAP being gone, which nothing sets and nothing can set: the plan recomputes its gaps from the
    /// model on the next compose, the item is not rebuilt because there is nothing left to rebuild it from,
    /// and PlanMemory records the item as finished at that refresh (PlanMemory.cs, PlanDone). So applied
    /// always comes first and done follows on the next compose. They are NOT the same and the gap between
    /// them is real work: a set can apply in full and the gap can survive it, because the rules that found
    /// the gap and the lines that were kept are not obliged to agree. An item in that position stays on the
    /// plan, in state <see cref="Applied"/>, saying so, which is the honest answer and the reason done is
    /// never stamped by whatever did the writing.</summary>
    public static class ModelPlanItemStates
    {
        /// <summary>A thing the model is meant to reach, named before anybody has looked for a gap against
        /// it. Written by the plan's target half and never by the change half.</summary>
        public const string Target = "target";

        /// <summary>A real gap, with nothing proposed against it yet. The ordinary state of an open item.</summary>
        public const string Gap = "gap";

        /// <summary>A change set has been worked out and is waiting for a person to keep, drop and approve.
        /// NOTHING HAS BEEN WRITTEN. Reaching this state costs the model nothing at all.</summary>
        public const string Proposed = "proposed";

        /// <summary>The kept lines were applied, as one transaction, on one undo entry. It says the SET
        /// landed. It does not say the gap closed; see the note on this class.</summary>
        public const string Applied = "applied";

        /// <summary>The gap is gone. Recomputed, never stamped.</summary>
        public const string Done = "done";

        /// <summary>A person set it aside, with a reason.</summary>
        public const string Skipped = "skipped";
    }

    /// <summary>Where a change set stands. The same five words the contract's state machine uses
    /// (change-contract.md, "the state machine"), spelled for the wire.</summary>
    public static class ModelPlanChangeStates
    {
        public const string Proposed = "proposed";
        public const string Applied = "applied";
        public const string RolledBack = "rolledBack";
        public const string Refused = "refused";
        public const string Cancelled = "cancelled";
    }

    /// <summary>Where one line came from, in the four kinds the contract's provenance type allows
    /// (Jev/ChangeContract.cs, ChangeItemKind). On the wire as a lower-case word so a page never has to know
    /// the enum.</summary>
    public static class ModelPlanChangeKinds
    {
        public const string Rule = "rule";
        public const string Decision = "decision";
        public const string Assistant = "assistant";
        public const string Proven = "proven";
    }

    /// <summary>Which decision point, which rule, how sure. The rules about which combinations are legal are
    /// NOT restated here: this is the wire form of Jev.ChangeProvenance, whose constructor refuses a
    /// dishonest shape outright, and every line on the wire was built from one of those. A proven result
    /// never carries a confidence, and that is enforced where the object is made, not where it is
    /// serialised.</summary>
    public sealed class ModelPlanChangeProvenance
    {
        /// <summary>One of <see cref="ModelPlanChangeKinds"/>.</summary>
        public string Kind { get; set; }

        /// <summary>The decision point, or the assistant request id. Null on a rule line.</summary>
        public string Point { get; set; }

        /// <summary>The rule id. Null on anything but a rule line.</summary>
        public string Rule { get; set; }

        /// <summary>How sure the decision point was, 0 to 1. Null on a rule line and on a proven one, and
        /// null is NOT zero: a rule carries no confidence because it is deterministic, and writing that as
        /// 0 would read as "not confident at all".</summary>
        public double? Confidence { get; set; }

        /// <summary>The same confidence as the one word a person reads: "sure", "likely" or "unsure"
        /// (jev-groundwork-contract.md section 2). Set on a decision line only, and worked out by the engine
        /// from <see cref="Confidence"/> against the point's own bar (JevFamilies/JevFamily.cs), so the word
        /// and the number cannot disagree. Null on every other kind.
        ///
        /// Carried BESIDE the number rather than instead of it: the number was already on the wire and a
        /// page that reads it would have broken if the field had changed type.</summary>
        public string ConfidenceWord { get; set; }
    }

    /// <summary>The engine's own check on one line, done before the line was offered
    /// (jev-groundwork-contract.md section 2). <see cref="Exists"/> says whether there IS a check for this
    /// kind of change; <see cref="Text"/> says what it proved, in plain words. A choice the check refused is
    /// never a line at all: it goes on the set's refused list with the reason instead.</summary>
    public sealed class ModelPlanChangeCheck
    {
        public bool Exists { get; set; }
        public string Text { get; set; }
    }

    /// <summary>One line of a change set: one exact change to one thing, with a keep flag a person owns.</summary>
    public sealed class ModelPlanChangeLine
    {
        /// <summary>Stable within the set and derived from what the line is ABOUT, never from its position,
        /// for the same reason an item id is (ModelPlanView.cs). Shape: "&lt;rule&gt;/&lt;target&gt;". A page
        /// keeps a row open while the list under it moves, and a keep survives a re-read.</summary>
        public string Id { get; set; }

        /// <summary>The id the CHANGE PLAN knows this line by ("ci-0007"), which is what set_plan_item and
        /// apply_plan address. Deliberately not the same as <see cref="Id"/>: this one is a position in a
        /// list that gets rebuilt, and <see cref="Id"/> is what the line is about. Empty when the line is
        /// being shown from a snapshot because the list it was on is gone.</summary>
        public string PlanItemId { get; set; }

        /// <summary>The line a person reads, with the same "+ ", "- " and "~ " prefixes the item's own diff
        /// uses, so the set and the item's <see cref="ModelPlanItem.Diff"/> read as one thing.</summary>
        public string Line { get; set; }

        /// <summary>What kind of write this is. The only value this milestone produces is "setProperty";
        /// it is on the wire because a page that switches on it will not need changing when there is a
        /// second one.</summary>
        public string Op { get; set; }

        /// <summary>The object, as the engine spells a reference ("column:Sales/CustomerKey").</summary>
        public string Target { get; set; }

        /// <summary>The property being written, for a "setProperty" line. Null on anything else.</summary>
        public string Property { get; set; }

        /// <summary>What the model says now, as text. Null means the thing does not exist yet.</summary>
        public string Before { get; set; }

        /// <summary>What it would say, as text.</summary>
        public string After { get; set; }

        /// <summary>True while this line is part of what approve would apply. A person drops a line by
        /// setting it false, and a dropped line is kept ON the set, visibly dropped, rather than deleted:
        /// the set's digest covers every line either way, so dropping one changes the digest and the
        /// approval a person gives is the approval of exactly what they read.</summary>
        public bool Kept { get; set; } = true;

        /// <summary>True while this line is kept ONLY because Semanticus suggested it (the cleanup row's pre-selection) and nobody
        /// has ticked, unticked or edited it since. Run the plan never applies such a line: a suggestion the person has not
        /// looked at is not something to delete. Approve, which is the person's own act on the whole row, still applies it.</summary>
        public bool Suggested { get; set; }

        public ModelPlanChangeProvenance Provenance { get; set; }

        /// <summary>Why this change, as one sentence the ENGINE built from what it measured and what was
        /// chosen. Never words a decision service wrote: Jev picks from a fixed list and writes no text.
        /// Null on a line that came from anywhere but a Jev family.</summary>
        public string Reason { get; set; }

        /// <summary>The engine's own check on this line, or null when the line came from somewhere that
        /// does not check.</summary>
        public ModelPlanChangeCheck Check { get; set; }

        /// <summary>The change plan line's ARGUMENTS (ChangeItem.Args: a create's table, a relationship's two ends, a
        /// permission's role). Internal, so never on the wire and never in the digest: only the footprint reads them,
        /// because what a line names only in its arguments is still something its order against a rename or a delete
        /// rests on (PlanFootprint.FromLines). Null on a line with none.</summary>
        internal System.Collections.Generic.IReadOnlyDictionary<string, string> Args { get; set; }

        /// <summary>True on a Jev format line the format fill rule could not prove (item 11 of its second addendum): a
        /// suggestion the person checks and keeps, never a line the engine vouches for. Its plain note is
        /// <see cref="Note"/>, and the page shows that as words on the row, never as a check mark or a "sure".</summary>
        public bool Suggestion { get; set; }

        /// <summary>The plain sentence a suggestion carries ("Jev suggests this format; check it fits before you run it."),
        /// or null on any other line.</summary>
        public string Note { get; set; }
    }

    /// <summary>Something the proposal looked at and left OUT, with the reason, so a person reads it BEFORE
    /// deciding rather than in a report afterwards (change-contract.md, "what a change set carries").</summary>
    public sealed class ModelPlanChangeRefusal
    {
        public string Target { get; set; }
        public string Reason { get; set; }
    }

    /// <summary>The change set attached to one plan item.</summary>
    public sealed class ModelPlanChangeSet
    {
        /// <summary>The job id the change contract knows this set by. Derived from the item id, so one item
        /// has at most one open proposal and a second propose replaces the first rather than stacking.</summary>
        public string Id { get; set; }

        /// <summary>sha256 over the lines AND the versions below, computed by the contract
        /// (Jev/ChangeContract.cs, ChangeSet.ComputeDigest). Approve quotes it, so a person approves the set
        /// they read and not whatever the set has become.</summary>
        public string Digest { get; set; }

        /// <summary>One of <see cref="ModelPlanChangeStates"/>.</summary>
        public string State { get; set; }

        /// <summary>The model revision the lines were WORKED OUT against, which is not the same as the
        /// revision they were proposed at when a caller hands over a list it built earlier
        /// (change-contract.md, Astra B4).</summary>
        public long Revision { get; set; }

        /// <summary>The source version they were worked out against. Empty when the model has no source on
        /// disk to version, which is honest rather than a placeholder: an unsaved model has no source that
        /// can move under the proposal.</summary>
        public string SourceVersion { get; set; }

        /// <summary>True when the versions above were ALREADY not the ones the model carried at the moment
        /// the set was worked out (change-contract.md, Astra B4: a caller handing over a list it built five
        /// minutes ago). The set is still handed back, stamped with the OLD versions, so the digest stays
        /// honest and approve refuses it for the right reason. Stored, and never recomputed: it is a fact
        /// about one past moment.</summary>
        public bool StaleAtProposal { get; set; }

        /// <summary>True when the set cannot be applied as it stands RIGHT NOW, because the model or the
        /// source has moved since it was worked out. Computed on every read and never stored, because it is
        /// a comparison against a model that keeps moving. Always true when <see cref="StaleAtProposal"/>
        /// is. Only ever computed for a set in state proposed: an applied set's revision is behind by
        /// definition and calling that stale would be nonsense.
        ///
        /// A stale set is shown, not swept away, and the page says "these changes are out of date, work them
        /// out again". There is no silent re-apply and no silent re-propose: both would hand a person a
        /// different diff from the one they read.</summary>
        public bool Stale { get; set; }

        /// <summary>Plain words for <see cref="Stale"/>. Empty when the set is current.</summary>
        public string StaleReason { get; set; }

        /// <summary>The session the lines were worked out in. A set from another session cannot be applied
        /// and is shown out of date: the change plan the lines live on belongs to the session, and a
        /// revision counter restarts when a model is opened again, so a set from a closed session could
        /// otherwise look current while the lines behind it were gone.</summary>
        public string SessionId { get; set; }

        /// <summary>The model these changes were worked out against, as the engine's own durable identity
        /// for a model. Hashed into the digest, so a digest from one model can never approve work on
        /// another, however alike the two are.</summary>
        public string ModelIdentity { get; set; }

        public DateTimeOffset ProposedAt { get; set; }

        /// <summary>When the set was applied. Null until it is.</summary>
        public DateTimeOffset? AppliedAt { get; set; }

        /// <summary>When something was last applied FOR THIS ITEM, which is a different question from
        /// whether THIS set applied.
        ///
        /// An item's id names a family, so work keeps arriving under it: a person can approve four
        /// descriptions today and their assistant can add a fifth tomorrow. The fifth has not run. Reading
        /// the item's stored "applied" state and hanging the newest lines off it reported the fifth as
        /// already in, and then refused to approve it for the same reason. So the receipt is carried here,
        /// beside the proposal, and never folded into it. Null when nothing has ever been applied here.</summary>
        public DateTimeOffset? LastAppliedAt { get; set; }

        /// <summary>How many changes that earlier run applied. Zero when there was no earlier run.</summary>
        public int LastAppliedCount { get; set; }

        public ModelPlanChangeLine[] Lines { get; set; } = Array.Empty<ModelPlanChangeLine>();

        public ModelPlanChangeRefusal[] Refused { get; set; } = Array.Empty<ModelPlanChangeRefusal>();

        /// <summary>How many lines are kept. On the wire rather than counted on the page, so the sentence and
        /// the count cannot disagree.</summary>
        public int KeptCount { get; set; }

        /// <summary>The one line a person reads about the set as a whole.</summary>
        public string Sentence { get; set; }

        /// <summary>The undo entry, or the checkpoint, the applied set can be put back from. Empty until the
        /// set is applied.</summary>
        public string Checkpoint { get; set; }

        internal ModelPlanChangeSet ShallowCopy() => (ModelPlanChangeSet)MemberwiseClone();
    }
}
