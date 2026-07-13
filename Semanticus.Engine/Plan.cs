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
                                                       //     rename | set_dax | set_synonyms | enable_qna | bpa_fix | mark_date_table
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
        public string VerifyState { get; set; }        // null | verified | unverified | failed  (for set_dax)
        public string[] VerifyGroupBy { get; set; }    // SUMMARIZECOLUMNS matrix for the equivalence gate (set_dax only)
        public string[] VerifyFilters { get; set; }
        public string Note { get; set; }               // apply/verify result note
        public Semanticus.Analysis.GroundingBundle Grounding { get; set; } // for AI items

        /// <summary>A shallow field-copy. BuildView clones every item so a broadcast/returned view is an
        /// immutable point-in-time snapshot — a later in-place field mutation can't tear a serialized view.</summary>
        public ChangeItem Clone() => (ChangeItem)MemberwiseClone();
    }

    public sealed class PlanSummary
    {
        public int Total { get; set; }
        public int Deterministic { get; set; }
        public int Bpa { get; set; }
        public int Ai { get; set; }
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

        public ChangeItem Add(ChangeItem it) { it.Id = "ci-" + (++_itemSeq).ToString("0000"); _items.Add(it); return it; }
        public ChangeItem Find(string id) => _items.FirstOrDefault(x => x.Id == id);
        public ChangeItem[] Snapshot() => _items.ToArray();
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

        public void Clear() => _current = null;
    }
}
