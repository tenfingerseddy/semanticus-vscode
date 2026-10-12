using System;
using System.Collections.Generic;
using System.Linq;

namespace Semanticus.Engine
{
    // ============================================================================================
    // The Change-Plan engine (slice 1: the headless plan layer).
    //
    // A ChangePlan is the "pull request for your model": the engine seeds deterministic fixes, the
    // user's Claude fills the AI-content items, the human reviews/approves per item, and apply_plan
    // executes the approved subset as ONE undoable, verify-gated transaction. NOTHING mutates the
    // model until apply_plan. The live plan is a session-held artifact (PlanStore) that broadcasts on
    // the ChangeBus (plan/didChange) so the UI can watch it assemble in real time.
    // ============================================================================================

    /// <summary>One proposed change. <see cref="Kind"/> is the apply-operation; <see cref="After"/> is
    /// the proposed value (null for AI items until Claude authors it). Deterministic items are fully
    /// specified at propose time; AI items carry <see cref="Grounding"/> so Claude can fill them in one shot.</summary>
    public sealed class ChangeItem
    {
        public string Id { get; set; }                 // stable within the plan, e.g. "ci-0007"
        public string ObjectRef { get; set; }
        public string ObjectName { get; set; }
        public string Kind { get; set; }               // op: set_description | set_measure_format | set_summarize_by |
                                                       //     set_column_hidden | set_data_category | set_relationship_crossfilter |
                                                       //     rename | set_dax | set_synonyms | create_linguistic_schema | bpa_fix | mark_date_table
        public string Source { get; set; }             // "deterministic" | "bpa" | "ai"
        public string Category { get; set; }
        public string Severity { get; set; }
        public string Risk { get; set; }               // "safe" | "ai" | "rename" | "structural"
        public string RuleId { get; set; }             // originating rule (BPA/readiness), if any
        public string Target { get; set; }             // semantic property touched (for de-dup): "description","summarizeBy",...
        public string Title { get; set; }              // human one-liner
        public string Rationale { get; set; }          // why this change
        public string Before { get; set; }             // current value (for the diff)
        public string After { get; set; }              // proposed value (null until an AI item is filled)
        public string Status { get; set; }             // proposed | needs_content | approved | rejected | applied | skipped | failed
        public bool Generated { get; set; }            // an AI item whose After was authored by Claude
        public string VerifyState { get; set; }        // null | verified | unverified | failed | degraded | degraded_mismatch  (for set_dax; degraded* = fidelity-gated, skipped unless overridden)
        public string[] VerifyGroupBy { get; set; }    // SUMMARIZECOLUMNS matrix for the equivalence gate (set_dax only)
        public string[] VerifyFilters { get; set; }
        public string Note { get; set; }               // apply/verify result note
        /// <summary>delete_if_unused only. The report basis this removal was REVIEWED against, as one comparable
        /// string (see LocalEngine.ReportScopeSignature). Apply refuses a removal whose basis has moved: the
        /// person read an answer about one set of reports and is applying against another. Deliberately blind to
        /// the model revision and to the time of a reading, so an ordinary model edit or an honest re-read of the
        /// same selection is not a changed basis.</summary>
        public string ReviewedScope { get; set; }

        /// <summary>The cleanup row's own mark, on a line that arrived TICKED because Semanticus suggested it and that nobody has
        /// touched since. It is cleared by any keep, drop or edit of the line (set_plan_item), on either door. A suggested
        /// line is a proposal the person has not looked at: Run the plan never applies it, and a generic apply_plan from the
        /// assistant's door does not either. Only an explicit approval of the whole row (approve, or apply_plan_items naming the
        /// row and its digest) or the person's own keep applies it. Round 5 of m11/plan-harm (Kane, 30 Sep 2026).</summary>
        public bool Suggested { get; set; }

        public Semanticus.Analysis.GroundingBundle Grounding { get; set; } // for AI items

        /// <summary>A shallow field-copy. BuildView clones every item so a broadcast/returned view is an
        /// immutable point-in-time snapshot — a later in-place field mutation can't tear a serialized view.</summary>
        /// <summary>The model revision this line's content was WORKED OUT against, stamped when it went on
        /// the plan and never restamped.
        ///
        /// It exists because a proposal used to be given the versions of the moment somebody READ it. Add a
        /// description for a measure, change that measure's DAX, then open the plan: the old description was
        /// handed back wearing the new revision and read as current, and approving it applied words written
        /// about a formula that had since changed. A line cannot be asked when it was worked out after the
        /// fact, so it is recorded when it is made. This is the same fact the change contract calls
        /// "collected against" (Jev/ChangeContract.cs, CollectedAtRevision).</summary>
        public long ProposedAtRevision { get; set; }

        /// <summary>The source version this line's content was worked out against. Empty when the model has
        /// no source on disk to version, which is not the same as unknown and is not treated as one.</summary>
        public string ProposedAtSource { get; set; }

        /// <summary>The Model Plan item a Jev family filed this line under ("jev.measures.table:Sales"). Null
        /// on every line that did not come from a Jev family, which is every line an assistant or a rule put
        /// here. It is what files the line on the plan (LocalEngine.PlanChanges.cs, ItemIdForLine), so the
        /// family and the plan cannot disagree about which item a line belongs to.</summary>
        public string JevItem { get; set; }

        /// <summary>The decision point that chose this line, for a Jev line. Null otherwise.</summary>
        public string DecisionPoint { get; set; }

        /// <summary>How sure the point was, 0 to 1, for a Jev line. Null otherwise.</summary>
        public double? DecisionConfidence { get; set; }

        /// <summary>"sure", "likely" or "unsure", worked out by the engine from the confidence and the point's
        /// bar. Null on a line that is not a Jev line.</summary>
        public string DecisionWord { get; set; }

        /// <summary>The engine-built sentence saying why, for a Jev line. Never text a service wrote.</summary>
        public string Reason { get; set; }

        /// <summary>What the engine checked before offering the line, in plain words. Null when nothing was
        /// checked, which is not the same as a check that passed.</summary>
        public string CheckText { get; set; }

        /// <summary>The arguments a create line needs beyond ObjectRef and After, by the op's own argument names
        /// (create_relationship: fromColumnRef, toColumnRef, crossFilter, isActive). Null on every older kind but one:
        /// a find and replace set_synonyms item (propose_replace) carries the list its write makes and the culture its
        /// words were read from, so apply_plan makes the single replace's write (LocalEngine.ApplyOneItem). The composer writes the rest (composer
        /// contract 3.2); the appliers read it (Plan/JevFamilies/PlanExecutorKinds.cs). Treated as read-only once the
        /// line is built: Clone shares it.</summary>
        public Dictionary<string, string> Args { get; set; }

        /// <summary>The line on the same Jev item this one cannot stand without, as <see cref="JevFamily.NeedsKey"/>
        /// spells it (its kind and object), or null. The base measure family's format, description and hide lines need
        /// their measure, and its Excel line needs the hide (Plan/JevFamilies/ColumnSummarizeFamily.cs). Dropping a line
        /// drops every line that needs it and keeping one keeps what it needs (SetPlanItemAsync), so an approval never
        /// applies half of a pair. Engine-internal: the family rebuilds it on every run, so it is never on the wire.</summary>
        internal string Needs { get; set; }

        /// <summary>True when a PERSON ticked this line (SetPlanItemAsync with approved, from the person's door). The
        /// engine's own approval, given when a line arrives, is not this: a family approves a line the rule proves, and
        /// only a person's tick may later write a format the rule does not prove (THE FORMAT FILL RULE, addendum 2 item 8).
        /// It is also the only status a re-sync carries onto a line worked out again. Cleared when anyone drops the line.</summary>
        public bool KeptByPerson { get; set; }

        /// <summary>The Before the person was looking at when they ticked the line, so a tick is carried only onto a line
        /// about the same write over the same text. A person's keep of "add #,0 over nothing" is not a keep of "add #,0
        /// over the blank spaces I typed since". Null unless <see cref="KeptByPerson"/>.</summary>
        public string KeptAgainst { get; set; }

        /// <summary>What the object computed when the person ticked the line (<c>FormatFill.ComputeDigest</c>: the formula for a
        /// measure, the type and source for a column). A keep belongs to the object and what it computes, never to a name: at carry
        /// time and again at write time a keep whose object now computes something else is void, and the line goes back to an unticked
        /// suggestion (THE FORMAT FILL RULE, addendum 3 item 12). Null unless <see cref="KeptByPerson"/>.</summary>
        public string KeptDigest { get; set; }

        /// <summary>What the object computed when the start plan last looked at a keep of the person's, with every column known by its lineage tag, not its name
        /// (<c>FormatFill.ComputeIdentityDigest</c>). The start plan's rounds use it to carry a keep across a rename: while it still matches, the object computes what it computed
        /// and only names moved, so <see cref="KeptDigest"/> is made current; when it no longer matches the object is something else and the keep is void. Null until the
        /// first round after the keep.</summary>
        public string KeptIdentity { get; set; }

        /// <summary>What the object this format line is about computed when the line was WORKED OUT (<c>FormatFill.ComputeDigest</c>, taken
        /// on the same read of the model that Jev was asked about), so a line is stamped with the object it was worked out for
        /// (THE FORMAT FILL RULE, addendum 4 item 23). A tick, an approve or a write on a line whose object's digest no longer matches is
        /// refused with a note, whether Jev has answered again or is silent, and a silent Jev never makes an old line look current. Null on a
        /// line that is not a Jev format line.</summary>
        public string WorkedOutDigest { get; set; }

        /// <summary>The value somebody WROTE over a Jev line's own (set_plan_item with a value, from either door), or null when nobody
        /// did. A Jev family works its lines out again whenever the model changes, and a line whose value differs is a different line,
        /// so without this a person's own words on a starter measure's description vanished on the next read with no word and no undo
        /// entry. The re-sync keeps the line the family still proposes for that object and property, with this value instead
        /// (Sol 6.1, 1 Oct, finding 1.3). Never set on a Jev format line, which has its own rule. Engine-internal: never on the wire.</summary>
        internal string AuthoredAfter { get; set; }

        /// <summary>Who dropped this line: "person" or "assistant", or null when nobody did (the engine's own drop of an unsure line is
        /// not a decision). Only a drop somebody made carries onto the line worked out again (addendum 3 item 21, L1).</summary>
        public string DroppedBy { get; set; }

        /// <summary>True for a Jev format line the rule could not prove: only a suggestion, whose plain note is <see cref="CheckText"/>. Sent so
        /// every surface that lists change plan lines (the Pending changes panel included) shows it as a suggestion, never as "safe".</summary>
        public bool Suggestion => string.Equals(Source, "jev", StringComparison.Ordinal) && Semanticus.Engine.Decisions.JevFormatSuggestion.IsSuggestion(CheckText);
        /// <summary>The status a seeded line had when a waiver on its finding left it out (Plan/LocalEngine.PlanWaivers.cs),
        /// so removing the waiver puts it back as it was. Engine-internal: never on the wire.</summary>
        internal string HeldFromStatus { get; set; }

        /// <summary>The findings whose asks this one line carries, when there is more than one (the AI-instructions line, a
        /// rename two rules ask for). Engine-internal: never on the wire. Null on a line with one ask. Read-only once set:
        /// Clone shares it (Plan/LocalEngine.PlanDeclined.cs).</summary>
        internal List<PlanAsk> Asks { get; set; }

        public ChangeItem Clone() => (ChangeItem)MemberwiseClone();
    }

    public sealed class PlanSummary
    {
        public int Total { get; set; }
        public int Deterministic { get; set; }
        public int Bpa { get; set; }
        public int Ai { get; set; }
        /// <summary>Lines a Jev family chose. Counted in a category of their own so no line is in none (a Jev line's Source is "jev").</summary>
        public int Jev { get; set; }
        public int Renames { get; set; }
        public int NeedsContent { get; set; }
        public int Approved { get; set; }
        public int Rejected { get; set; }
        public int Applied { get; set; }
        public int Unverified { get; set; }
    }

    /// <summary>Wire snapshot of the current plan (broadcast on plan/didChange and returned by the plan tools).</summary>
    public sealed class ChangePlanView
    {
        public string PlanId { get; set; }
        public string Scope { get; set; }              // null = whole model; "table:Name" = scoped
        public long Revision { get; set; }
        public ChangeItem[] Items { get; set; } = Array.Empty<ChangeItem>();
        public PlanSummary Summary { get; set; } = new PlanSummary();
        public string Note { get; set; }               // plan-level honesty line (e.g. matches propose_replace could NOT turn into items)
    }

    /// <summary>Result of apply_plan: per-item final states + the before→after model deltas (BPA, grade).</summary>
    public sealed class ApplyPlanReport
    {
        public long Revision { get; set; }
        public int AppliedCount { get; set; }
        public int SkippedCount { get; set; }
        public int FailedCount { get; set; }
        public int RejectedCount { get; set; }   // structurally rejected pre-mutation (circular rewrite) — NOT overridable, distinct from skipped
        public ChangeItem[] Items { get; set; } = Array.Empty<ChangeItem>();   // the targeted items, final state
        public int BpaViolationsBefore { get; set; }
        public int BpaViolationsAfter { get; set; }
        public string GradeBefore { get; set; }
        public string GradeAfter { get; set; }
        public double OverallBefore { get; set; }
        public double OverallAfter { get; set; }
        public string Note { get; set; }

        // Learning Loop L3: the engine offers the moment (docs/learning-loop-plan.md §3.2). A clean
        // multi-item apply that didn't regress the grade is a repeatable recipe — the /distill-workflow
        // hint. NOT a claim that it WILL be distilled: the agent still parameterizes + the admission
        // gates (L4) still guard the library. Deterministic, engine-set — never self-graded.
        public bool Distillable { get; set; }
        public string DistillableWhy { get; set; }
    }

    /// <summary>The mutable, session-held plan (the list of items + id allocation). NOT internally locked:
    /// every access is serialized by the engine's <c>_planGate</c> SemaphoreSlim — the single owner of
    /// plan-state concurrency across both doors — so an inner lock would be dead weight.</summary>
    public sealed class ChangePlanState
    {
        private int _itemSeq;
        private readonly List<ChangeItem> _items = new List<ChangeItem>();

        public string PlanId { get; }
        public string Scope { get; }
        public string Note { get; set; }   // plan-level annotation (set by propose_replace; carried on every view)

        public ChangePlanState(string planId, string scope) { PlanId = planId; Scope = scope; }

        /// <summary>Put a line on the plan, stamped with what it was worked out against.
        ///
        /// The versions are REQUIRED rather than defaulted, so that a new caller cannot quietly add a line
        /// with no record of the model it was worked out for. A line with no stamp reads as current forever,
        /// which is exactly the bug this parameter exists to make impossible.</summary>
        public ChangeItem Add(ChangeItem it, long revision, string sourceVersion)
        {
            it.Id = "ci-" + (++_itemSeq).ToString("0000");
            it.ProposedAtRevision = revision;
            it.ProposedAtSource = sourceVersion ?? "";
            _items.Add(it);
            return it;
        }
        /// <summary>Put a line carried over from an earlier plan on this one. It keeps the stamps it was worked out with (<see cref="Add"/> would
        /// restamp it as worked out now) and gets an id from this plan's own count. Only a person's words travel this way
        /// (<see cref="ChangeItem.AuthoredAfter"/>), held, by <c>LocalEngine.AddSeedsKeepingAuthored</c>.</summary>
        internal ChangeItem Adopt(ChangeItem it)
        {
            it.Id = "ci-" + (++_itemSeq).ToString("0000");
            _items.Add(it);
            return it;
        }
        public ChangeItem Find(string id) => _items.FirstOrDefault(x => x.Id == id);

        /// <summary>Drop every line the predicate matches, and say how many went. The Model Plan needs it:
        /// working an item's changes out AGAIN has to replace what was there, because the old lines were
        /// worked out against a model that has since moved and keeping them beside the new ones would hand a
        /// person a diff that is half true (LocalEngine.PlanVerbs.cs, ProposeItemAsync). Ids are never
        /// reused: the sequence only ever counts up, so a dropped "ci-0003" cannot come back meaning
        /// something else.</summary>
        public int Remove(Predicate<ChangeItem> match) => _items.RemoveAll(match);

        /// <summary>Drop the lines a Jev family sync no longer stands behind, EXCEPT any line somebody wrote over (<see cref="ChangeItem.AuthoredAfter"/>).
        /// Words a person or their assistant wrote are never the family's to take off the plan, whatever the family proposes next or
        /// whether it is switched off: they are held instead (<see cref="JevFamily.HoldAuthored"/>) so the text stays visible and waits
        /// for a fresh tick. Every place a sync removes lines goes through here, so a new one cannot remove authored work by forgetting
        /// to ask (Sol 6.1 on the M11 delta, finding A1, the second strike of 1.3).</summary>
        internal int RemoveSynced(Predicate<ChangeItem> match) => _items.RemoveAll(l => l.AuthoredAfter == null && match(l));
        public ChangeItem[] Snapshot() => _items.ToArray();

        internal ChangePlanState Copy()
        {
            var copy = new ChangePlanState(PlanId, Scope) { Note = Note, _itemSeq = _itemSeq };
            copy._items.AddRange(_items.Select(x => x.Clone()));
            return copy;
        }
    }

    /// <summary>Holds the one current change plan for the session and allocates plan ids. Access is serialized
    /// by the engine's <c>_planGate</c>, so no internal lock is needed.</summary>
    public sealed class PlanStore
    {
        private ChangePlanState _current;
        private int _planSeq;

        public ChangePlanState Current => _current;
        public ChangePlanState StartNew(string scope) => _current = new ChangePlanState("plan-" + (++_planSeq), scope);

        /// <summary>Return the current plan, creating an empty whole-model one if none exists (used by add_plan_item).</summary>
        public ChangePlanState GetOrStart() => _current ??= new ChangePlanState("plan-" + (++_planSeq), null);

        public ChangePlanState Require() =>
            _current ?? throw new InvalidOperationException("No change plan is open. Call propose_plan (or add_plan_item) first.");

        internal void Replace(ChangePlanState plan) => _current = plan;

        public void Clear() => _current = null;
    }
}
