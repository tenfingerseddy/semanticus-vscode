using System;
using System.Collections.Generic;
using System.Linq;

namespace Semanticus.Engine
{
    // ============================================================================================
    // The Pro-mode workflow engine (kernel: definitions + run state). See docs/pro-mode-spec.md.
    //
    // A workflow is a user-editable markdown file (steps + YAML gates) — the playbook the agent
    // reads. A RUN is a session-held state machine (like ChangePlanState): nothing about a run is
    // persisted to the model; mutations happen only through the ops the steps route to. Definitions
    // are re-read from disk at call time (hot-editable). Every run transition broadcasts
    // workflow/didChange so both doors see the same live state.
    // ============================================================================================

    /// <summary>A parsed workflow definition. A malformed file is surfaced, not skipped:
    /// <see cref="Error"/> carries the parse failure so the author sees it in list_workflows.</summary>
    public sealed class WorkflowDef
    {
        public string Name { get; set; }               // kebab-case id; must match the filename
        public string Kind { get; set; }               // §10.3: null/"workflow" = a runnable workflow; "template" = a recipe with slots (NOT runnable — instantiate first)
        public string Title { get; set; }
        public string Description { get; set; }
        public string WhenToUse { get; set; }           // §9.5 skills-style routing hint: when to pick THIS one among siblings (optional)
        public int Version { get; set; } = 1;
        public string Strictness { get; set; }         // hard | warn | off — default for all gates
        public string[] Triggers { get; set; } = Array.Empty<string>();  // ops that suggest this workflow (advisory)
        public string[] Tags { get; set; } = Array.Empty<string>();      // §10.6: free-form labels an activation rule can select by (`tag:`); e.g. [client-acme, finance]
        public string Source { get; set; }             // "user" (.semanticus/workflows) | "stock" (shipped library)
        public string FilePath { get; set; }
        public string Error { get; set; }              // parse failure text (null = parsed clean)
        public WorkflowStep[] Steps { get; set; } = Array.Empty<WorkflowStep>();
        public SlotDef[] Slots { get; set; } = Array.Empty<SlotDef>();   // §10.3 template fill-ins (empty for a workflow); the body references each as {{name}}

        /// <summary>Unknown frontmatter keys, preserved verbatim (the parser ignores them for run semantics but
        /// keeps them here for forward-compat + provenance). A DISTILLED workflow (Learning Loop L3) carries
        /// <c>derived_from: [run ids]</c> and friends here — check_workflow surfaces them as an info finding.</summary>
        public Dictionary<string, string> Provenance { get; set; } = new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>True when any gate would actually enforce (resolves above "off") — the entitlement
        /// chokepoint: what's paid is enforcement, not reading the playbook.</summary>
        public bool HasEnforcedGate(string settingsStrictness = null, string globalStrictness = null) =>
            Steps.Any(st => st.Gate != null
                && WorkflowRunner.EffectiveStrictness(this, st.Gate, settingsStrictness, globalStrictness) != "off"
                && (st.Gate.Inputs.Length > 0 || st.Gate.Verify.Length > 0));
    }

    /// <summary>§10.3 — one template SLOT: an org-stable fill-in the user provides ONCE at instantiation
    /// (distinct from a gate INPUT, which is per-run evidence asked fresh every run). Slot values change what a
    /// step SAYS and what a question ASKS — never what the engine ENFORCES (the structure-preserving invariant,
    /// §10.4). Referenced in the body as <c>{{name}}</c>.</summary>
    public sealed class SlotDef
    {
        public string Name { get; set; }               // camelCase id; the {{name}} the body references
        public string Question { get; set; }           // the fill-in prompt (shown in the Studio form / asked by the agent verbatim)
        public string Type { get; set; } = "text";     // the gate-input vocabulary: text | number | enum | objectRef | verification
        public string Required { get; set; } = "required"; // required | optional (optional carries a default:)
        public string Default { get; set; }            // optional-only: used when an optional slot is left blank
        public string Example { get; set; }            // REQUIRED on every slot — the value check_workflow's trial instantiation renders with
        public string Hint { get; set; }               // optional extra guidance
        public string[] Values { get; set; } = Array.Empty<string>();   // enum membership (type: enum)
    }

    public sealed class WorkflowStep
    {
        public string Id { get; set; }                 // "step-1" (from the heading number)
        public int Number { get; set; }
        public string Title { get; set; }
        public string Instructions { get; set; }       // the step body verbatim, gate block excluded
        public GateSpec Gate { get; set; }             // zero or one per step
        public string[] Ops { get; set; } = Array.Empty<string>();  // the declared MCP action chain (advisory — designer visibility, not enforcement)
    }

    /// <summary>One entry of the engine's MCP op catalog (the designer's "chain an action" picker).</summary>
    public sealed class OpInfo
    {
        public string Name { get; set; }
        public string Description { get; set; }        // first sentence only — the picker line, not the full tool doc
    }

    /// <summary>Learning Loop L4 (docs/learning-loop-plan.md §3.3): the ADMISSION DRY-RUN report — the cheap
    /// half of the admission pipeline (parse-valid → dry-run). <see cref="Ok"/> is true when the file parses AND
    /// no <c>warn</c> finding fired (info findings never dock it). The expensive REPLAY check (re-executing the
    /// deterministic steps against the originating snapshot) is a later layer, NOT run here.</summary>
    public sealed class WorkflowCheckReport
    {
        public string Name { get; set; }
        public string ParseError { get; set; }         // null = parsed clean
        public CheckFinding[] Findings { get; set; } = Array.Empty<CheckFinding>();
        public bool Ok { get; set; }                   // parses AND no warn finding
    }

    /// <summary>One dry-run finding: <c>info</c> (advisory, e.g. provenance / thin-equivalence) or <c>warn</c>
    /// (a resolution problem that would make a gate unrunnable — an unknown op, an unresolved input).</summary>
    public sealed class CheckFinding
    {
        public string Severity { get; set; }           // "info" | "warn"
        public string Message { get; set; }
    }

    public sealed class GateSpec
    {
        public string Strictness { get; set; }         // per-gate override (null = inherit)
        public GateInput[] Inputs { get; set; } = Array.Empty<GateInput>();
        public VerifySpec[] Verify { get; set; } = Array.Empty<VerifySpec>();
    }

    public sealed class GateInput
    {
        public string Name { get; set; }
        public string Question { get; set; }
        public string Type { get; set; } = "text";     // verification | text | enum | number | objectRef
        public string Required { get; set; } = "answer-or-decline"; // answer-or-decline | required | optional
    }

    public sealed class VerifySpec
    {
        public string Kind { get; set; }               // deterministic executor name; see WorkflowParser.VerifyKinds
        public string When { get; set; }               // "inputs.<name>.answered" — gate only runs if the input was answered
        public string Probe { get; set; }              // input name carrying the verification value (dax_probe)
        public string Scope { get; set; }              // object | model (rescan kinds)
        public string Intent { get; set; }             // impact_assessment: change | rename | remove | restructure

        // E1 — the typed SHAPE LEDGER over a dax_equivalence proof (docs v5-engine-contract §E1). The comparator
        // always evaluates the grand total + each single-axis subtotal + the full cross; these declare which of
        // those evaluated shapes are PINNED (a mismatch FAILS the gate) vs OPEN (a mismatch is RECORDED in the
        // evidence payload, never fails). Shapes are named canonically: 'grand_total', 'axis:<column>', 'cross'.
        // Both empty (the default) ⇒ every shape pinned = the current behaviour. A shape is OPEN iff it is listed
        // in OpenShapes and NOT in PinnedShapes (pinned wins the overlap).
        public string[] PinnedShapes { get; set; } = Array.Empty<string>();
        public string[] OpenShapes { get; set; } = Array.Empty<string>();
        // E1 ADDENDUM — the run-decided partition: names a SAME-STEP input whose ANSWER lists the open shape ids
        // (the v5 seed derives the partition per run from the requirement's context ledger — it cannot be known at
        // authoring time). Unioned with the static OpenShapes; a declined/unanswered input = all pinned (fail
        // closed); an invalid or non-evaluated id in the answer makes the verify `unavailable`, naming the bad id.
        public string OpenShapesFrom { get; set; }

        // expected_values — names a TEXT input (this or any prior step, same binding rules as OpenShapesFrom) whose
        // ANSWER carries the LOCKED ANCHORS: a fenced JSON array of {context, expect} the target measure must
        // reproduce. Applies ONLY to the expected_values kind (parser-guarded); the executor parses it defensively
        // (malformed ⇒ the verify is `unavailable`, naming the defect) and locks the set at first evaluation.
        public string Anchors { get; set; }
    }

    // ---- run state ------------------------------------------------------------------------------

    /// <summary>A caller's answer to a gate input: a value OR an explicit decline (never both).
    /// Declines are recorded on the step result — auditable, never silently dropped.</summary>
    public sealed class AnswerValue
    {
        public string Value { get; set; }
        public bool Declined { get; set; }
        public string DeclineReason { get; set; }
        public bool Answered => !Declined && Value != null;
    }

    public sealed class VerifyResult
    {
        public string Kind { get; set; }
        // E2 three-way-plus outcome (docs v5-engine-contract §E2):
        //   passed          — verified.
        //   failed          — evidence produced, the gate was not met. BLOCKS a hard step.
        //   unavailable     — the verify was applicable but could NOT produce AUTHORITATIVE evidence (offline,
        //                     missing probe, zero rows compared, candidate drift, degraded-fidelity comparison).
        //                     BLOCKS a hard step exactly like failed; the Missing field names what was absent.
        //   not_applicable  — a conditional `when:` did not hold; the verify legitimately did not run and the step
        //                     advances (the back-compat path for the stock seeds' conditional verifies).
        //   skipped         — legacy advisory non-blocking skip (kept for perf/optional verifies that honestly
        //                     cannot run offline but must not block; distinct from unavailable's fail-closed).
        public string Status { get; set; }
        public string Detail { get; set; }             // evidence / skip reason ("offline" — never silently passed)
        public string Missing { get; set; }            // E2: on `unavailable`, the one thing that was missing (token-lean)
        // E1 — per-shape outcome of a dax_equivalence proof (null for every other kind): exactly what was proven on
        // each pinned shape and what was only OBSERVED on each open shape (the tool-result contract, §Appendix).
        public ShapeVerifyResult[] Shapes { get; set; }
        // E3(c) — the engine's OWN mismatching contexts (bounded), so adjudication targets engine-reported cells.
        public MismatchCell[] MismatchCells { get; set; }
        // DEEP copy: cloned results feed the append-only evidence archive (an audit surface), so nothing nested may
        // be shared with a live object a later code path could mutate.
        public VerifyResult Clone() => new VerifyResult
        {
            Kind = Kind, Status = Status, Detail = Detail, Missing = Missing,
            Shapes = Shapes?.Select(s => s.Clone()).ToArray(),
            MismatchCells = MismatchCells?.Select(c => c.Clone()).ToArray(),
        };
    }

    /// <summary>E1 — one evaluated equivalence shape in the verify payload. <see cref="Pinned"/> false = OPEN
    /// (a mismatch here was recorded, not gated). Canonical <see cref="ShapeId"/>: 'grand_total' | 'axis:&lt;col&gt;' | 'cross'.</summary>
    public sealed class ShapeVerifyResult
    {
        public string ShapeId { get; set; }
        public bool Pinned { get; set; }
        public int RowsCompared { get; set; }
        public int MismatchCount { get; set; }
        public bool Truncated { get; set; }    // this shape hit the row cap (pinned truncation blocks; open truncation is an observation)
        public MismatchCell[] Sample { get; set; } = Array.Empty<MismatchCell>();   // bounded sample of this shape's mismatching cells
        public ShapeVerifyResult Clone() => new ShapeVerifyResult
        {
            ShapeId = ShapeId, Pinned = Pinned, RowsCompared = RowsCompared, MismatchCount = MismatchCount,
            Truncated = Truncated, Sample = Sample.Select(c => c.Clone()).ToArray(),
        };
    }

    /// <summary>E1/E3(c) — one engine-reported divergence: the filter context and the two values that differed.</summary>
    public sealed class MismatchCell
    {
        public string Context { get; set; }
        public string ValueA { get; set; }
        public string ValueB { get; set; }
        public MismatchCell Clone() => new MismatchCell { Context = Context, ValueA = ValueA, ValueB = ValueB };
    }

    /// <summary>E3(a) — a witness-revision receipt: the run-locked witness expression hash changed on a later
    /// submission (never silently replaced — the change is appended to the run record with before/after hashes).</summary>
    public sealed class WitnessRevision
    {
        public string Probe { get; set; }              // the gate-input name that feeds the dax_equivalence probe
        public string BeforeHash { get; set; }
        public string AfterHash { get; set; }
        public string StepId { get; set; }             // the step whose submission carried the revised witness
        public string TimestampUtc { get; set; }
    }

    /// <summary>Blocker-3 receipt — the pinned/open SHAPE PARTITION changed between submissions of the same verify
    /// (the laundering path: pin a shape, see its mismatch, re-submit with it open). The change is never silent:
    /// the receipt is appended, THAT submission is refused (`unavailable`), and only the NEXT submission evaluates
    /// fresh under the new locked partition — prior evidence stays in the run record.</summary>
    public sealed class PartitionRevision
    {
        public string Key { get; set; }                // stepId|verifyOrdinal|probe — which verify's partition (ordinal = parse order within the step)
        public string Before { get; set; }             // canonical open set (comma-joined, sorted; "" = all pinned)
        public string After { get; set; }
        public string StepId { get; set; }             // the step whose submission changed it
        public string TimestampUtc { get; set; }
    }

    /// <summary>One changed expected value and its live extract receipt. The raw result is represented by a stable
    /// hash plus row metadata so the audit proves which live evidence was observed without copying source rows into
    /// the workflow record.</summary>
    public sealed class AnchorRevisionChange
    {
        public string Context { get; set; }
        public string OriginalExpect { get; set; }
        public string CorrectedExpect { get; set; }
        public string ExtractQuery { get; set; }
        public int ExtractRowCount { get; set; }
        public bool ExtractTruncated { get; set; }
        public string ExtractResultHash { get; set; }
    }

    /// <summary>expected_values receipt for an ACCEPTED run-level anchor revision. The first anchor set recorded for
    /// an input is immutable. A later changed set is accepted only after every changed expectation names its accepted
    /// original, its proposed correction, and a live row-returning extract query. Before/After are canonical anchor-set
    /// fingerprints, so a pure reorder or re-format is not a change.</summary>
    public sealed class AnchorRevision
    {
        public string Key { get; set; }                // compatibility identity; new run-level receipts use anchorsInput
        public string AnchorsInput { get; set; }
        public string BeforeHash { get; set; }         // canonical anchor-set fingerprint before the change
        public string AfterHash { get; set; }
        public string StepId { get; set; }             // the step whose submission changed it
        public string TimestampUtc { get; set; }
        public AnchorRevisionChange[] Changes { get; set; } = Array.Empty<AnchorRevisionChange>();
    }

    /// <summary>Mutable run-level lock state for one expected_values binding. InitialHash never changes; CurrentHash
    /// and CurrentAnchors advance only after a mechanically valid, live-receipted revision is accepted.</summary>
    public sealed class AnchorRunLock
    {
        public string AnchorsInput { get; set; }
        public string InitialHash { get; set; }
        public string CurrentHash { get; set; }
        public string StepId { get; set; }
        public AnchorGate.Anchor[] CurrentAnchors { get; set; } = Array.Empty<AnchorGate.Anchor>();
    }

    /// <summary>Blocker-1 receipt — one SUBMISSION's verify evidence, archived append-only on the step. A
    /// re-submission replaces the CURRENT results (they drive advancement) but may never erase the evidence an
    /// earlier attempt produced: the mismatch that motivated a partition change must survive into the certificate.</summary>
    public sealed class VerifyAttempt
    {
        public int Ordinal { get; set; }               // 1-based submission ordinal for this step
        public string TimestampUtc { get; set; }
        public VerifyResult[] Results { get; set; } = Array.Empty<VerifyResult>();
        public VerifyAttempt Clone() => new VerifyAttempt { Ordinal = Ordinal, TimestampUtc = TimestampUtc, Results = Results.Select(r => r.Clone()).ToArray() };
    }

    public sealed class StepResult
    {
        public string StepId { get; set; }
        public string Title { get; set; }
        public string Status { get; set; }             // pending | in_progress | passed | skipped | failed
        public string Note { get; set; }               // skip reason / failure text
        public Dictionary<string, AnswerValue> Answers { get; set; } = new Dictionary<string, AnswerValue>();
        public VerifyResult[] VerifyResults { get; set; } = Array.Empty<VerifyResult>();
        // Blocker 1 — APPEND-ONLY evidence history: every submission that produced verify outcomes is archived here
        // (small DTOs, bounded by the resubmission count; never cleared on re-submit). VerifyResults above stays the
        // CURRENT state that drives advancement; this is the immutable record get_workflow_run + the terminal run
        // record surface. Null-able on the wire only via serialization of an empty list (kept always-present here).
        public List<VerifyAttempt> VerifyHistory { get; set; } = new List<VerifyAttempt>();
        public string EffectiveStrictness { get; set; } // what the gate actually ran at (auditable)

        public StepResult Clone()
        {
            var c = (StepResult)MemberwiseClone();
            c.Answers = Answers.ToDictionary(kv => kv.Key, kv => new AnswerValue
            {
                Value = kv.Value.Value, Declined = kv.Value.Declined, DeclineReason = kv.Value.DeclineReason
            });
            c.VerifyResults = VerifyResults.Select(v => v.Clone()).ToArray();
            c.VerifyHistory = VerifyHistory.Select(a => a.Clone()).ToList();
            return c;
        }
    }

    /// <summary>The mutable, session-held run. NOT internally locked: every access is serialized by
    /// the engine's <c>_workflowGate</c> — the single owner of run-state concurrency across both
    /// doors (same discipline as <see cref="ChangePlanState"/>/<c>_planGate</c>).</summary>
    public sealed class WorkflowRunState
    {
        public string RunId { get; }
        public WorkflowDef Def { get; }                // snapshot at start — a mid-run file edit can't tear the run
        public string SettingsStrictness { get; }      // per-workflow settings override, frozen at start
        public string GlobalStrictness { get; }        // the model-wide enforcement override, frozen at start (a mid-run toggle can't tear the run)
        public int StepIndex { get; set; }
        public string Status { get; set; } = "active"; // active | completed | aborted
        public string AbortReason { get; set; }
        public string StartedUtc { get; set; } = DateTime.UtcNow.ToString("o");
        public string FinishedUtc { get; set; }
        public string Origin { get; set; }
        // Transient origin of the submission currently executing under WorkflowGate. Used by verify-side data reads
        // so they pass through the same agent QueryData policy as run_dax. Never serialized into the run view.
        public string SubmissionOrigin { get; set; }
        public string ModelIdentity { get; set; }
        public string ModelName { get; set; }
        public string ModelFingerprint { get; set; }
        public string SessionId { get; set; }
        public StepResult[] Results { get; }

        // E3(a) — the WITNESS LOCK: the first-submitted normalized-expression hash of each dax_equivalence probe
        // input (keyed by input name), plus every later change as an appended revision receipt. Mutated only under
        // the engine's _workflowGate (like every other run field), so no internal lock is needed.
        public Dictionary<string, string> WitnessLocks { get; } = new Dictionary<string, string>(StringComparer.Ordinal);
        public List<WitnessRevision> WitnessRevisions { get; } = new List<WitnessRevision>();

        // Blocker 3 — the PARTITION LOCK: the canonical effective OPEN set per verify (stepId|verifyOrdinal|probe),
        // locked at the first actually-run comparison; a later submission with a different set appends a receipt and
        // is refused (see EquivalenceGate.RegisterPartition). Same _workflowGate ownership as the witness lock.
        public Dictionary<string, string> PartitionLocks { get; } = new Dictionary<string, string>(StringComparer.Ordinal);
        public List<PartitionRevision> PartitionRevisions { get; } = new List<PartitionRevision>();

        // Legacy per-verify anchor locks remain for compatibility with existing run records and the pure lock helper.
        // Production expected_values execution uses RunAnchorLocks below.
        public Dictionary<string, string> AnchorLocks { get; } = new Dictionary<string, string>(StringComparer.Ordinal);
        // expected_values run-level lock: the earliest answered anchor binding is the immutable initial set. Current
        // advances only after a changed set's per-anchor receipts execute successfully against the pinned live model.
        public Dictionary<string, AnchorRunLock> RunAnchorLocks { get; } = new Dictionary<string, AnchorRunLock>(StringComparer.Ordinal);
        public List<AnchorRevision> AnchorRevisions { get; } = new List<AnchorRevision>();

        public WorkflowRunState(string runId, WorkflowDef def, string settingsStrictness, string globalStrictness = null)
        {
            RunId = runId; Def = def; SettingsStrictness = settingsStrictness; GlobalStrictness = globalStrictness;
            Results = def.Steps.Select(s => new StepResult { StepId = s.Id, Title = s.Title, Status = "pending" }).ToArray();
            if (Results.Length > 0) Results[0].Status = "in_progress";
        }

        public WorkflowStep CurrentStep => Status == "active" && StepIndex < Def.Steps.Length ? Def.Steps[StepIndex] : null;
    }

    // ---- wire views (broadcast on workflow/didChange; cloned point-in-time snapshots) -------------

    /// <summary>The model-wide workflow-enforcement state (get/set_workflow_enforcement). Mode is the
    /// TOP of the strictness resolution — when set it wins over per-gate and per-workflow values (it is
    /// the owner's deliberate kill-switch, not a default): mode ?? gate ?? per-workflow ?? frontmatter ?? hard.</summary>
    public sealed class WorkflowEnforcement
    {
        public string Mode { get; set; }       // "hard" | "warn" | "off" | null (null = no override; definitions decide)
        public bool Enforced { get; set; }     // false only when Mode == "off" — the at-a-glance answer
        public string Note { get; set; }
    }

    /// <summary>§9.12 — the whole workflow POLICY for THIS project in one compact, token-lean object
    /// (get_workflow_policy, free/read-only): the model-wide enforcement mode, one row per workflow
    /// (availability + gated + its routing hint + which ops REQUIRE it, inverted from the bindings), and the
    /// raw op→workflow bindings. Rides the session-start orientation primer so Claude self-routes into the
    /// right workflow instead of discovering mandates by rejection. No step bodies, no descriptions beyond
    /// whenToUse — this is the map, not the territory.</summary>
    public sealed class WorkflowPolicy
    {
        public string ActiveProfile { get; set; }      // standard | team-standard | consulting-delivery | custom
        public string Enforcement { get; set; }        // global mode: "hard" | "warn" | "off" | null (no override; each workflow's own strictness applies)
        public WorkflowPolicyEntry[] Workflows { get; set; } = Array.Empty<WorkflowPolicyEntry>();
        public WorkflowBindingView[] Bindings { get; set; } = Array.Empty<WorkflowBindingView>();
        // §10.6:653 — policy lints surfaced LOUDLY (never blocking): a rule targeting an unknown workflow, an
        // unreadable `when:`, a rule a manual disable makes dead, a binding↔activation contradiction, a binding
        // requiring a turned-off workflow (the deadlock), conflicting rules. Read-only; free.
        public WorkflowPolicyLint[] Lints { get; set; } = Array.Empty<WorkflowPolicyLint>();
    }

    public sealed class WorkflowProfileInfo
    {
        public string Name { get; set; }
        public string Title { get; set; }
        public string Description { get; set; }
        public string[] Effects { get; set; } = Array.Empty<string>();
        public bool Pro { get; set; }
        public bool Selected { get; set; }
    }

    public sealed class WorkflowProfileResult
    {
        public string ActiveProfile { get; set; }
        public WorkflowInfo[] Workflows { get; set; } = Array.Empty<WorkflowInfo>();
        public WorkflowPolicy Policy { get; set; }
        public string Note { get; set; }
    }

    /// <summary>One policy-lint finding (§10.6:653). Severity is plain: "warn" (a real contradiction to fix) or
    /// "info" (a deterministic-but-smelly overlap). Message is analyst-facing plain language (§10.12).</summary>
    public sealed class WorkflowPolicyLint
    {
        public string Severity { get; set; }   // "warn" | "info"
        public string Message { get; set; }
    }

    public sealed class WorkflowPolicyEntry
    {
        public string Name { get; set; }
        public bool Enabled { get; set; } = true;       // §9a availability — on the menu?
        public bool Active { get; set; } = true;        // §10.6 dynamic activation — is a rule currently showing/hiding it?
        public string ActiveReason { get; set; }        // plain-language reason when not the zero-config default (never a predicate echo)
        public bool Gated { get; set; }                 // any enforced gate → starting it is Pro
        public string WhenToUse { get; set; }           // §9.5 routing hint (the only prose here — keep it token-lean)
        public string[] RequiredForOps { get; set; } = Array.Empty<string>();   // ops whose binding names THIS workflow (inverted from Bindings)
    }

    /// <summary>One op→workflow binding as surfaced to both doors (§9.3): the op that is routed, the required
    /// workflow set (Claude picks among them by whenToUse), the mode, and whether a contributor may turn it off
    /// locally (§9.10C — false = committed team policy, only changeable by a reviewed file edit).</summary>
    public sealed class WorkflowBindingView
    {
        public string Op { get; set; }
        public string[] Require { get; set; } = Array.Empty<string>();
        public string Mode { get; set; }               // "hard" | "warn"
        public bool UserDisablable { get; set; } = true;
    }

    /// <summary>List entry for list_workflows (free — the library is open content).</summary>
    public sealed class WorkflowInfo
    {
        public string Name { get; set; }
        public string Title { get; set; }
        public string Description { get; set; }
        public string WhenToUse { get; set; }          // §9.5 routing hint — surfaced beside Description so a caller can pick among siblings
        public int Version { get; set; }
        public string Source { get; set; }
        public int StepCount { get; set; }
        public bool Enabled { get; set; } = true;      // §9a availability toggle (workflow-settings.json) — false = off the menu, NOT dropped from the list (the designer must still see it to re-enable)
        // §10.6 dynamic activation: is this workflow on the CURRENT menu (given today's date / connection / branch / …)?
        // Distinct from Enabled — availability is the manual toggle, activation is the rule-driven curation. Default true
        // (zero-config invisibility: no activation rules ⇒ every workflow Active with a null reason, same cost as before).
        public bool Active { get; set; } = true;
        public string ActiveReason { get; set; }        // plain-language reason when NOT the default (never a predicate echo, §10.12); null when Active by default
        public bool Gated { get; set; }                // any enforced gate → starting it is Pro
        public string[] Triggers { get; set; } = Array.Empty<string>();
        public string Error { get; set; }
    }

    /// <summary>list_workflow_templates entry — the shelf summary (§10.5): name/title/whenToUse/version/source +
    /// a slot summary (count + names). <see cref="Error"/> surfaces a malformed template file — never silently
    /// skipped (same honesty rule as WorkflowInfo).</summary>
    public sealed class WorkflowTemplateInfo
    {
        public string Name { get; set; }
        public string Title { get; set; }
        public string WhenToUse { get; set; }
        public int Version { get; set; }
        public string Source { get; set; }             // "stock" | "user"
        public int SlotCount { get; set; }
        public string[] Slots { get; set; } = Array.Empty<string>();   // slot names (the summary)
        public string Error { get; set; }
    }

    /// <summary>get_workflow_template — the full template definition (§10.5): the slot declarations the caller
    /// must fill PLUS the raw markdown body (with the {{slot}} references intact) so both doors see exactly what
    /// will render. Symmetric with save_workflow_template (markdown in, markdown out).</summary>
    public sealed class WorkflowTemplate
    {
        public string Name { get; set; }
        public string Title { get; set; }
        public string Description { get; set; }
        public string WhenToUse { get; set; }
        public int Version { get; set; }
        public string Source { get; set; }
        public string Error { get; set; }
        public SlotDef[] Slots { get; set; } = Array.Empty<SlotDef>();
        public string Markdown { get; set; }           // the raw template file (body references {{slot}})
    }

    /// <summary>What the caller acts on next: the current step's FULL instruction text (verbatim —
    /// the instruction-returning-tool pattern; the agent is taught at the point of need) plus the
    /// gate's unanswered questions.</summary>
    public sealed class CurrentStepView
    {
        public string StepId { get; set; }
        public string Title { get; set; }
        public string Instructions { get; set; }
        public GateInput[] Questions { get; set; } = Array.Empty<GateInput>();
        public string[] VerifyKinds { get; set; } = Array.Empty<string>();
        public string EffectiveStrictness { get; set; }
        public string[] Ops { get; set; } = Array.Empty<string>();  // the step's declared action chain
    }

    public sealed class WorkflowRunView
    {
        public string RunId { get; set; }
        public string Workflow { get; set; }
        public string Title { get; set; }
        public int WorkflowVersion { get; set; }
        public string Status { get; set; }
        public string AbortReason { get; set; }
        public string StartedUtc { get; set; }
        public string FinishedUtc { get; set; }
        public string ModelName { get; set; }
        public string ModelFingerprint { get; set; }
        public int StepIndex { get; set; }
        public int TotalSteps { get; set; }
        public StepResult[] Steps { get; set; } = Array.Empty<StepResult>();
        public CurrentStepView CurrentStep { get; set; }   // null once terminal

        // E3(a)/blocker-3 — the adjudication RECEIPTS surfaced on get_workflow_run (null when none, to keep the
        // payload lean): the currently-locked witness hash per probe input, every witness revision, and every
        // shape-partition revision recorded on this run.
        public WitnessLockView[] WitnessLocks { get; set; }
        public WitnessRevision[] WitnessRevisions { get; set; }
        public PartitionRevision[] PartitionRevisions { get; set; }
        public AnchorLockView[] AnchorLocks { get; set; }           // expected_values run-level initial/current locks
        public AnchorRevision[] AnchorRevisions { get; set; }   // expected_values — the anchor-set revision receipts

        // Learning Loop L3 (docs/learning-loop-plan.md §3.2): a run that completed with every step
        // passed and at least one REAL verify (not all-skipped/offline) is repeatable — the
        // /distill-workflow hint. Engine-set on the terminal view; the agent still parameterizes + the
        // admission gates guard the library.
        public bool Distillable { get; set; }
        public string DistillableWhy { get; set; }
    }

    /// <summary>E3(a) — one locked witness on the run view: the probe input name and its current SHA-256 lock hash.</summary>
    public sealed class WitnessLockView
    {
        public string Probe { get; set; }
        public string Hash { get; set; }
    }

    /// <summary>The first and currently accepted expected_values fingerprints for one run-level binding.</summary>
    public sealed class AnchorLockView
    {
        public string AnchorsInput { get; set; }
        public string InitialHash { get; set; }
        public string CurrentHash { get; set; }
        public string StepId { get; set; }
    }

    /// <summary>Holds the session's live runs and allocates run ids. Access is serialized by the
    /// engine's <c>_workflowGate</c>, so no internal lock is needed. Bounded like BaselineStore —
    /// the oldest terminal run is dropped first; live runs are never evicted silently.</summary>
    public sealed class WorkflowRunStore
    {
        private const int MaxHeld = 8;
        private readonly List<WorkflowRunState> _runs = new List<WorkflowRunState>();
        private int _seq;

        public WorkflowRunState Start(WorkflowDef def, string settingsStrictness, string globalStrictness = null)
        {
            if (_runs.Count(r => r.Status == "active") >= MaxHeld)
                throw new InvalidOperationException($"{MaxHeld} workflow runs are already active — finish or abort one (abort_workflow) before starting another.");
            var run = new WorkflowRunState("wfr-" + (++_seq), def, settingsStrictness, globalStrictness);
            _runs.Add(run);
            if (_runs.Count > MaxHeld)
            {
                // evict the oldest TERMINAL run; an active run is never dropped (the cap above refuses instead)
                var oldest = _runs.FirstOrDefault(r => r.Status != "active");
                if (oldest != null) _runs.Remove(oldest);
            }
            return run;
        }

        /// <summary>The currently-active runs (for the orientation primer). Access is serialized by the
        /// engine's _workflowGate, like every other store read.</summary>
        public IReadOnlyList<WorkflowRunState> ActiveRuns() => _runs.Where(r => r.Status == "active").ToArray();

        /// <summary>Null/empty id = the most recent run (the common single-run session).</summary>
        public WorkflowRunState Get(string runId) => string.IsNullOrWhiteSpace(runId)
            ? _runs.LastOrDefault()
            : _runs.FirstOrDefault(r => r.RunId == runId);

        public WorkflowRunState Require(string runId) => Get(runId)
            ?? throw new InvalidOperationException(string.IsNullOrWhiteSpace(runId)
                ? "No workflow run exists. Call start_workflow first."
                : $"Workflow run '{runId}' not found.");
    }
}
