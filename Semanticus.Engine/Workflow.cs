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
        /// <summary>Format version (docs/workflow-canvas-spec.md §1.2). Absent from the file ⇒ 1, and a v1
        /// file parses under v1 rules forever: unknown keys land in <see cref="Provenance"/> exactly as they
        /// do today. 2 ⇒ strict parsing, where an unrecognised key anywhere is an error. A version this
        /// engine does not read is refused outright, never partially run — that is the whole point of [T169].</summary>
        public int SchemaVersion { get; set; } = 1;

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

        /// <summary>True when the frontmatter carries a `schemaVersion` key that is NOT the one the version
        /// scan read (§1.2: the version is the first key, at column zero, before any nested line). Only a v1
        /// file can carry one and still parse, because v2 refuses it outright, and v1 is frozen so it cannot
        /// be refused. It must not be SILENT either, which is the whole point of [T169], so check_workflow
        /// warns on this flag. Internal: it is a parse observation for the linter, not part of the format.</summary>
        internal bool HasUnreadSchemaVersion { get; set; }

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
        public string Id { get; set; }                 // "step-1" (from the heading number), or the v2 `yaml step` fence's `id:`
        public int Number { get; set; }
        public string Title { get; set; }
        public string Instructions { get; set; }       // the step body verbatim, gate block excluded
        public GateSpec Gate { get; set; }             // zero or one per step
        public string[] Ops { get; set; } = Array.Empty<string>();  // the declared MCP action chain (advisory — designer visibility, not enforcement)

        // ---- format v2 (docs/workflow-canvas-spec.md §1.4-§1.7), all from the optional `yaml step` fence.
        // Step-level `when:`, `forEach:` and `call:` execute through the shared run plan. Admission
        // validates loop inputs and call graphs before registering a run; nested frames carry
        // their owning workflow, supplied inputs and declared returns through each transition.
        //
        // F-069: this used to claim such a file runs straight through, unaffected, which was the opposite of
        // what the engine does and was the dangerous reading to act on: running a `when:` step
        // unconditionally is precisely the behaviour the refusal exists to prevent. The comment predated the
        // refusal and was not revisited when it landed. The exact wording is not quoted here, because a test
        // now asserts that phrase is absent from this file and quoting it would defeat that test -- the same
        // trap the F-075 pair sprang one read later.

        /// <summary>§1.5 — the step runs only when this condition holds. Null = unconditional.
        /// Parsed by <see cref="WorkflowPredicate"/>, the ONE evaluator, so activation, verify-level `when:`
        /// and step-level `when:` can never grow three dialects.</summary>
        public string When { get; set; }

        /// <summary>§1.6 — repeat this step once per item. Null = not a loop.</summary>
        public ForEachSpec ForEach { get; set; }

        /// <summary>§1.7 — hand off to another workflow. Null = not a hand-off.</summary>
        public CallSpec Call { get; set; }

        /// <summary>True when the id came from an explicit `id:` rather than the heading number. The canvas
        /// and the upgrader both need to tell "addressed by name" from "addressed by position".</summary>
        public bool HasExplicitId { get; set; }
    }

    /// <summary>§1.6 — a step repeated once per item in a list. Exactly one of <see cref="InLiteral"/> and
    /// <see cref="InInput"/> is set: the source is an inline literal list or a gate input whose answer is a
    /// comma-or-newline separated list, and nothing else (no op results, no globs, no model queries — a list
    /// that arrives from the model arrives through an answer, so it is on the record).</summary>
    public sealed class ForEachSpec
    {
        public string[] InLiteral { get; set; }        // `in: [Sales, Product]`
        public string InInput { get; set; }            // `in: inputs.tableList` — the input NAME, without the root
        public string As { get; set; }                 // the loop variable; referenced as [[loop.<as>]] in text, loop.<as> in a when:
        public int MaxIterations { get; set; } = DefaultMaxIterations;

        public const int DefaultMaxIterations = 25;
        /// <summary>The hard ceiling a file may not raise past. A longer list is REFUSED at expansion time,
        /// never truncated: a truncated loop is a certificate claiming coverage it does not have.</summary>
        public const int MaxMaxIterations = 100;
    }

    /// <summary>§1.7 — a hand-off to another workflow, unrolled into the caller's plan at run start (a later
    /// slice). The boundary is closed in both directions: the callee sees only <see cref="With"/>, and only
    /// <see cref="Returns"/> comes back, so a callee stays a reusable unit whose own binding validation is
    /// decidable from its file alone.</summary>
    public sealed class CallSpec
    {
        public string Workflow { get; set; }           // must exist, be kind: workflow (never a template), and not be this file
        /// <summary>One of the format's TWO open maps: keys are the CALLEE's declared input names, so it is
        /// validated against the callee by check_workflow rather than against a fixed list. Open in
        /// SPELLING only — a key that is not one of the callee's inputs is still refused.
        ///
        /// The other is `provenance:`, which is a true bag: arbitrary keys, no validation, by design.
        /// F-075: this used to name `with:` as the format's only open map, the claim the spec carried until
        /// it was corrected in the fifth read. Correcting a document and leaving the comment that taught it
        /// is how the wrong version survives, since the comment is what the next builder reads.</summary>
        public Dictionary<string, string> With { get; set; } = new Dictionary<string, string>(StringComparer.Ordinal);
        public string[] Returns { get; set; } = Array.Empty<string>();

        /// <summary>Depth limit counting the top-level run as depth 1 (§1.7). Exceeding it is refused with
        /// the chain named.</summary>
        public const int MaxDepth = 3;
    }

    /// <summary>One reserved spelling (docs/workflow-canvas-spec.md §1.8): a key a later version will define,
    /// refused NOW with its own named reason so an author who read the vision document learns "not yet"
    /// instead of "you typed it wrong". Reserved is not implemented.</summary>
    public sealed class ReservedKeySpec
    {
        public string Key { get; set; }
        public string Scope { get; set; }              // "frontmatter" | "step" | "gate" — load-bearing: a key is reserved WHERE IT WILL LIVE
        public string Reason { get; set; }
    }

    /// <summary>One entry of the engine's MCP op catalog (the designer's "chain an action" picker).</summary>
    public sealed class OpInfo
    {
        public string Name { get; set; }
        public string Description { get; set; }        // first sentence only — the picker line, not the full tool doc
        // [T215] The op's ratified home in the taxonomy tree (question > shelf), stamped from
        // OpTaxonomy so both doors group the surface identically. Null only for an op whose
        // placement has not been ratified yet — OpTaxonomyTests fails the build naming it.
        public string Question { get; set; }
        public string Shelf { get; set; }
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
        // Optional, definition-gated DAX policy. The v7 verified-measure seed uses no-bare-measures on every
        // witnessDax submission so a witness must rebuild semantics from qualified base-column references.
        public string DaxPurity { get; set; }

        /// <summary>§2.2a escape hatch — "run" lifts this name out of a loop's per-iteration answer frame so
        /// the last iteration's answer survives the loop. Null (or "iteration") is the default. Parsed here;
        /// honoured by the runner in a later slice. Deliberately narrow: it is legal only for inputs nothing
        /// verifies against (notes, labels, a ticket reference), and never for `certificate`.</summary>
        public string Scope { get; set; }
    }

    /// <summary>One gap in a gate submission. AgentMessage is for the MCP door; DisplayMessage is for the person.</summary>
    public sealed class GateProblem
    {
        public string Input { get; set; }
        public string Kind { get; set; }
        public string AgentMessage { get; set; }
        public string DisplayMessage { get; set; }
    }

    public static class GateCopy
    {
        public const string Unanswered = "unanswered";
        public const string DeclinedButRequired = "declined-but-required";
        public const string DeclinedWithoutReason = "declined-without-reason";

        public static string DisplayUnanswered() => "This question still needs an answer.";
        public static string DisplayDeclinedButRequired() => "This one cannot be declined. Type an answer.";
        public static string DisplayDeclinedWithoutReason() => "Say why you are declining.";

        public static string AgentUnanswered(GateInput input) =>
            $"'{input.Name}' is unanswered. Ask the user: \"{input.Question}\" " +
            (input.Required == "required"
                ? "(required: it cannot be declined)"
                : "(answer it, or decline explicitly with {\"declined\": true, \"reason\": \"...\"})");

        public static string AgentDeclinedButRequired(GateInput input) =>
            $"'{input.Name}' may not be declined (required). Ask the user: \"{input.Question}\"";

        public static string AgentDeclinedWithoutReason(GateInput input) =>
            $"'{input.Name}' was declined without a reason: a decline is recorded and needs one.";

        public static string AgentSummary(string stepId, System.Collections.Generic.IReadOnlyList<GateProblem> problems) =>
            $"Step '{stepId}' rejected: {problems.Count} gate input(s) unanswered. " + string.Join(" ", problems.Select(p => p.AgentMessage));

        public static string DisplaySummary(System.Collections.Generic.IReadOnlyList<GateProblem> problems)
        {
            if (problems == null || problems.Count == 0) return DisplayUnanswered();
            if (problems.Count == 1) return problems[0].DisplayMessage;
            return problems.Count + " questions still need an answer before this step can be submitted.";
        }

        internal const string ProblemsKey = "semanticus.gateProblems";
        internal const string DisplayKey = "semanticus.gateDisplay";

        public static InvalidOperationException Refuse(string stepId, System.Collections.Generic.IReadOnlyList<GateProblem> problems)
        {
            var ex = new InvalidOperationException(AgentSummary(stepId, problems));
            ex.Data[ProblemsKey] = problems.ToArray();
            ex.Data[DisplayKey] = DisplaySummary(problems);
            return ex;
        }

        public static bool TryRead(Exception ex, out GateProblem[] problems, out string displayMessage)
        {
            problems = ex?.Data[ProblemsKey] as GateProblem[];
            displayMessage = ex?.Data[DisplayKey] as string;
            return problems != null && problems.Length > 0 && !string.IsNullOrEmpty(displayMessage);
        }
    }

    public sealed class GateRefusalException : InvalidOperationException
    {
        public GateProblem[] Problems { get; }
        public string DisplayMessage { get; }
        public GateRefusalException(string stepId, System.Collections.Generic.IReadOnlyList<GateProblem> problems)
            : base(GateCopy.AgentSummary(stepId, problems))
        {
            Problems = problems.ToArray();
            DisplayMessage = GateCopy.DisplaySummary(problems);
        }
    }

    public sealed class HandOffWarning
    {
        public string Callee { get; set; }
        public string CalleeTitle { get; set; }
        public string[] ExpectedReturns { get; set; } = Array.Empty<string>();
        public string NextStepTitle { get; set; }
        public bool GateOff { get; set; }
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

        // v7 def-gated escalation for OPEN mismatches. Absent preserves the v6 report-only behavior. The only
        // supported value, "countersign", requires every current open-shape dispute to be fixed or acknowledged
        // by exact coordinate and value before the verify can pass.
        public string OpenMismatch { get; set; }

        // expected_values / anchor_coverage names a TEXT input (this or any prior step, same binding rules as
        // OpenShapesFrom) whose
        // ANSWER carries the LOCKED ANCHORS: a fenced JSON array of {context, expect} the target measure must
        // reproduce or whose grain coverage must be checked. Applies ONLY to those two kinds (parser-guarded); the
        // executor parses it defensively (malformed means the verify refuses and names the defect).
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
        //   skipped         — legacy offline skip. Hard gates now treat it like unavailable (fail-closed).
        //                     New executors should return unavailable instead.
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
        // In-memory identity carrier only (built at query time, consumed by the coordinate hasher, propagated by
        // Clone). Kept off BOTH serializers: System.Text.Json for the persisted terminal record AND Newtonsoft for the
        // RPC wire, so a v6 payload never gains a `contextParts` field over either door.
        [System.Text.Json.Serialization.JsonIgnore]
        [Newtonsoft.Json.JsonIgnore]
        public MismatchContextPart[] ContextParts { get; set; } = Array.Empty<MismatchContextPart>();
        public string ValueA { get; set; }
        public string ValueB { get; set; }
        public MismatchCell Clone() => new MismatchCell
        {
            Context = Context,
            ContextParts = ContextParts?.Select(p => p.Clone()).ToArray() ?? Array.Empty<MismatchContextPart>(),
            ValueA = ValueA,
            ValueB = ValueB,
        };
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

    /// <summary>The run-level Step-2 coverage surface. Grid columns and open grains are immutable except for
    /// receipted superset additions. Arrays retain declaration order for audit output; membership is set-based.</summary>
    public sealed class CoverageSurfaceLock
    {
        public string[] InitialGrid { get; set; } = Array.Empty<string>();
        public string[] CurrentGrid { get; set; } = Array.Empty<string>();
        public string[] InitialOpenGrains { get; set; } = Array.Empty<string>();
        public string[] CurrentOpenGrains { get; set; } = Array.Empty<string>();
        public string StepId { get; set; }
    }

    /// <summary>An accepted monotonic expansion of the locked coverage surface. Every before/after set and exact
    /// addition is retained so later certificate stages can print the revision without reconstructing history.</summary>
    public sealed class CoverageSurfaceRevision
    {
        public string[] BeforeGrid { get; set; } = Array.Empty<string>();
        public string[] AfterGrid { get; set; } = Array.Empty<string>();
        public string[] AddedGridColumns { get; set; } = Array.Empty<string>();
        public string[] BeforeOpenGrains { get; set; } = Array.Empty<string>();
        public string[] AfterOpenGrains { get; set; } = Array.Empty<string>();
        public string[] AddedOpenGrains { get; set; } = Array.Empty<string>();
        public string StepId { get; set; }
        public string TimestampUtc { get; set; }

        public CoverageSurfaceRevision Clone() => new CoverageSurfaceRevision
        {
            BeforeGrid = BeforeGrid.ToArray(),
            AfterGrid = AfterGrid.ToArray(),
            AddedGridColumns = AddedGridColumns.ToArray(),
            BeforeOpenGrains = BeforeOpenGrains.ToArray(),
            AfterOpenGrains = AfterOpenGrains.ToArray(),
            AddedOpenGrains = AddedOpenGrains.ToArray(),
            StepId = StepId,
            TimestampUtc = TimestampUtc,
        };
    }

    /// <summary>One exact open-shape disagreement retained by the run ledger. Coordinate is the canonical,
    /// copy-pasteable shape-and-cell identity printed by the countersign refusal. Values are named by role rather
    /// than A/B because dax_equivalence evaluates the witness as A and the candidate as B.</summary>
    public sealed class ShapeMismatchLedgerCell
    {
        public string Coordinate { get; set; }
        public string DisplayContext { get; set; }
        public string CandidateValue { get; set; }
        public string WitnessValue { get; set; }

        public ShapeMismatchLedgerCell Clone() => new ShapeMismatchLedgerCell
        {
            Coordinate = Coordinate,
            DisplayContext = DisplayContext,
            CandidateValue = CandidateValue,
            WitnessValue = WitnessValue,
        };
    }

    /// <summary>Cumulative state for one logical grain. At most ten active coordinates are retained; the total count
    /// records every mismatch observation even when the coordinate sample is full. Open marks whether the locked
    /// coverage partition permits countersign. A clean evaluation clears cells but not the cumulative audit count.</summary>
    public sealed class ShapeMismatchLedgerEntry
    {
        public string ShapeId { get; set; }
        public bool Open { get; set; }
        public string State { get; set; } = "OPEN-CLEAN"; // OPEN-CLEAN | DISPUTED | COUNTERSIGNED | PINNED-CLEAN | PINNED-MISMATCH
        public int TotalMismatchCount { get; set; }
        public int LastMismatchCount { get; set; }
        public int LastMismatchReconciledCount { get; set; }
        public ShapeMismatchLedgerCell[] Cells { get; set; } = Array.Empty<ShapeMismatchLedgerCell>();

        public ShapeMismatchLedgerEntry Clone() => new ShapeMismatchLedgerEntry
        {
            ShapeId = ShapeId,
            Open = Open,
            State = State,
            TotalMismatchCount = TotalMismatchCount,
            LastMismatchCount = LastMismatchCount,
            LastMismatchReconciledCount = LastMismatchReconciledCount,
            Cells = Cells.Select(c => c.Clone()).ToArray(),
        };
    }

    /// <summary>Immutable acknowledgment of one exact cell-and-values disagreement. A later mismatch at the same
    /// coordinate is covered only when both values are identical; changed values create a new dispute.</summary>
    public sealed class ShapeMismatchCountersign
    {
        public string ShapeId { get; set; }
        public string Coordinate { get; set; }
        public string CandidateValue { get; set; }
        public string WitnessValue { get; set; }
        public string Stated { get; set; }
        public string StepId { get; set; }
        public string TimestampUtc { get; set; }

        public ShapeMismatchCountersign Clone() => (ShapeMismatchCountersign)MemberwiseClone();
    }

    /// <summary>One grain in the locked coverage lattice. Anchored/Open are the mechanically-enforced XOR map;
    /// State is the compact wire form used by certificate renderers.</summary>
    public sealed class CertificateGrainCoverage
    {
        public string ShapeId { get; set; }
        public string State { get; set; }              // anchored | open
        public bool Anchored { get; set; }
        public bool Open { get; set; }

        public CertificateGrainCoverage Clone() => (CertificateGrainCoverage)MemberwiseClone();
    }

    /// <summary>One skipped workflow step retained on the computed certificate with its authored reason and the
    /// strictness recomputed from the frozen run policy.</summary>
    public sealed class CertificateSkippedStep
    {
        public string StepId { get; set; }
        public string Title { get; set; }
        public string Reason { get; set; }
        public string EffectiveStrictness { get; set; }

        public CertificateSkippedStep Clone() => (CertificateSkippedStep)MemberwiseClone();
    }

    /// <summary>Fresh-array copies for the certificate's Clone discipline. <c>Enumerable.ToArray()</c> hands back
    /// the shared <c>Array.Empty&lt;T&gt;()</c> singleton for an empty source, so two "clones" would share one
    /// array for every empty member. A certificate is stored on the run and cloned into every projection, so each
    /// reference member has to be its own object whatever its length.</summary>
    internal static class CertificateClone
    {
        public static T[] Map<T>(T[] source, Func<T, T> clone)
        {
            if (source == null) return null;
            var copy = new T[source.Length];
            for (var i = 0; i < source.Length; i++) copy[i] = clone(source[i]);
            return copy;
        }

        public static string[] Copy(string[] source)
        {
            if (source == null) return null;
            var copy = new string[source.Length];
            Array.Copy(source, copy, source.Length);
            return copy;
        }
    }

    /// <summary>One projected execution frame's own half of the folded certificate (DECISION 2.3.2): the identity
    /// tuple <c>get_workflow_run</c> already prints, that frame's own level, its own step rows carrying their
    /// verify evidence, and the proof receipts it produced. The lattice stays with the frame that proved it and is
    /// empty when the frame has no coverage surface — never the two unconditional anchored grains, which would
    /// print a proof nobody made. Carries no <c>FrameId</c>: the internal id is not a wire identity, and a reader
    /// joins this list to <c>get_workflow_run</c>'s frames by index over the same population and order.</summary>
    public sealed class CertificateFrame
    {
        public string Kind { get; set; }                // iteration | call
        public string StepId { get; set; }
        public string State { get; set; }
        public string Level { get; set; }               // this frame's own FULL | PARTIAL | OVERRIDDEN

        // The optional identity halves, null-omitted in the WorkflowRunFrameView shape so a call frame does not
        // print empty loop fields and an iteration frame does not print empty call fields.
        [Newtonsoft.Json.JsonProperty(NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore)]
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public int? IterationIndex { get; set; }
        [Newtonsoft.Json.JsonProperty(NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore)]
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public string LoopVariable { get; set; }
        [Newtonsoft.Json.JsonProperty(NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore)]
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public string LoopValue { get; set; }
        [Newtonsoft.Json.JsonProperty(NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore)]
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public string Workflow { get; set; }
        [Newtonsoft.Json.JsonProperty(NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore)]
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public int? Depth { get; set; }
        [Newtonsoft.Json.JsonProperty(NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore)]
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public string[] Passed { get; set; }
        [Newtonsoft.Json.JsonProperty(NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore)]
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public string[] Returned { get; set; }

        public StepResult[] Steps { get; set; } = Array.Empty<StepResult>();
        public string[] GridColumns { get; set; } = Array.Empty<string>();
        public CertificateGrainCoverage[] Coverage { get; set; } = Array.Empty<CertificateGrainCoverage>();
        public string[] OpenGrains { get; set; } = Array.Empty<string>();
        public ShapeMismatchCountersign[] CountersignedCells { get; set; } = Array.Empty<ShapeMismatchCountersign>();
        public ShapeMismatchLedgerCell[] DisputedCells { get; set; } = Array.Empty<ShapeMismatchLedgerCell>();
        public int AnchorRevisionCount { get; set; }
        public int FormRepairCount { get; set; }
        public CoverageSurfaceRevision[] CoverageSurfaceRevisions { get; set; } = Array.Empty<CoverageSurfaceRevision>();

        public CertificateFrame Clone() => new CertificateFrame
        {
            Kind = Kind,
            StepId = StepId,
            State = State,
            Level = Level,
            IterationIndex = IterationIndex,
            LoopVariable = LoopVariable,
            LoopValue = LoopValue,
            Workflow = Workflow,
            Depth = Depth,
            Passed = CertificateClone.Copy(Passed),
            Returned = CertificateClone.Copy(Returned),
            Steps = CertificateClone.Map(Steps, x => x.Clone()),
            GridColumns = CertificateClone.Copy(GridColumns),
            Coverage = CertificateClone.Map(Coverage, x => x.Clone()),
            OpenGrains = CertificateClone.Copy(OpenGrains),
            CountersignedCells = CertificateClone.Map(CountersignedCells, x => x.Clone()),
            DisputedCells = CertificateClone.Map(DisputedCells, x => x.Clone()),
            AnchorRevisionCount = AnchorRevisionCount,
            FormRepairCount = FormRepairCount,
            CoverageSurfaceRevisions = CertificateClone.Map(CoverageSurfaceRevisions, x => x.Clone()),
        };
    }

    /// <summary>The v7 engine-computed terminal certificate. Level is the weaker of ClaimLevel and ComputedLevel.
    /// AgentClaim is retained verbatim; every other field is derived from run state except countersign Stated text.
    /// The lattice fields and the run-level level describe the TOP frame's own proof; the counts and ordered
    /// receipts sum across every frame; each projected frame's own half rides in Frames.</summary>
    public sealed class WorkflowCertificate
    {
        public string Level { get; set; }               // FULL | PARTIAL | OVERRIDDEN
        public string AgentClaim { get; set; }          // verbatim submitted claim
        public string ClaimLevel { get; set; }          // normalized claim used by the ratchet
        public string ComputedLevel { get; set; }
        public string[] GridColumns { get; set; } = Array.Empty<string>();
        public CertificateGrainCoverage[] Coverage { get; set; } = Array.Empty<CertificateGrainCoverage>();
        public string[] OpenGrains { get; set; } = Array.Empty<string>();
        public ShapeMismatchCountersign[] CountersignedCells { get; set; } = Array.Empty<ShapeMismatchCountersign>();
        public ShapeMismatchLedgerCell[] DisputedCells { get; set; } = Array.Empty<ShapeMismatchLedgerCell>();
        public int AnchorRevisionCount { get; set; }
        public int FormRepairCount { get; set; }
        public CoverageSurfaceRevision[] CoverageSurfaceRevisions { get; set; } = Array.Empty<CoverageSurfaceRevision>();
        public CertificateSkippedStep[] SkippedSteps { get; set; } = Array.Empty<CertificateSkippedStep>();

        // The five folded members, every one null-omitted through BOTH doors: Newtonsoft carries the RPC wire and
        // System.Text.Json carries the terminal record. The tallies are int? for exactly that reason — a plain int
        // would serialize "IterationsTotal":0 on every no-loop run and break acceptance check 34 immediately.
        [Newtonsoft.Json.JsonProperty(NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore)]
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public CertificateFrame[] Frames { get; set; }
        [Newtonsoft.Json.JsonProperty(NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore)]
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public int? IterationsTotal { get; set; }
        [Newtonsoft.Json.JsonProperty(NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore)]
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public int? IterationsPassed { get; set; }
        [Newtonsoft.Json.JsonProperty(NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore)]
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public int? IterationsFailed { get; set; }
        [Newtonsoft.Json.JsonProperty(NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore)]
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public string[] FailedIterationValues { get; set; }

        public WorkflowCertificate Clone() => new WorkflowCertificate
        {
            Level = Level,
            AgentClaim = AgentClaim,
            ClaimLevel = ClaimLevel,
            ComputedLevel = ComputedLevel,
            GridColumns = CertificateClone.Copy(GridColumns),
            Coverage = CertificateClone.Map(Coverage, x => x.Clone()),
            OpenGrains = CertificateClone.Copy(OpenGrains),
            CountersignedCells = CertificateClone.Map(CountersignedCells, x => x.Clone()),
            DisputedCells = CertificateClone.Map(DisputedCells, x => x.Clone()),
            AnchorRevisionCount = AnchorRevisionCount,
            FormRepairCount = FormRepairCount,
            CoverageSurfaceRevisions = CertificateClone.Map(CoverageSurfaceRevisions, x => x.Clone()),
            SkippedSteps = CertificateClone.Map(SkippedSteps, x => x.Clone()),
            Frames = CertificateClone.Map(Frames, x => x.Clone()),
            IterationsTotal = IterationsTotal,
            IterationsPassed = IterationsPassed,
            IterationsFailed = IterationsFailed,
            FailedIterationValues = CertificateClone.Copy(FailedIterationValues),
        };
    }

    /// <summary>The exact candidate identity and expression proven by the latest coverage-backed equivalence pass.</summary>
    public sealed class EquivalenceCandidateReceipt
    {
        public string TargetLineageTag { get; set; }
        public string ExpressionHash { get; set; }
        // Audit-time ref only. Identity comparisons use TargetLineageTag; this ref lets any rename seam heal the
        // original name-based answer into a run-local alias without treating an unrelated stale ref as the target.
        public string TargetRefAtProof { get; set; }
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

    /// <summary>The expansion decision recorded beside a deferred forEach source answer. It is the record that
    /// makes the pair observable and the value that makes the splice re-resolution-free. It is not a resume
    /// point: nothing re-executes a pending decision on a later call.</summary>
    public sealed class PendingForEachExpansion
    {
        public string TargetStepId { get; set; }   // the target row's planned instance id at decision time
        public int TargetIndex { get; set; }       // always the declaring row's PlanIndex + 1
        public string SourceInput { get; set; }    // the gate input the list was read from
        public string[] Values { get; set; }       // a fresh string copy of the resolved list; never a discriminator
        public string Outcome { get; set; }        // expand | empty | condition_false   <- the ONLY discriminator
        public string State { get; set; }          // pending | applied | abandoned

        internal PendingForEachExpansion Clone() => new PendingForEachExpansion
        {
            TargetStepId = TargetStepId,
            TargetIndex = TargetIndex,
            SourceInput = SourceInput,
            Values = Values == null ? null : (string[])Values.Clone(),
            Outcome = Outcome,
            State = State,
        };
    }

    public sealed class StepResult
    {
        public string StepId { get; set; }
        public string Title { get; set; }
        public string Status { get; set; }             // pending | in_progress | passed | done | not_applicable | skipped | failed
        public string Note { get; set; }               // condition result / skip reason / failure text
        public Dictionary<string, AnswerValue> Answers { get; set; } = new Dictionary<string, AnswerValue>();
        public VerifyResult[] VerifyResults { get; set; } = Array.Empty<VerifyResult>();
        // Blocker 1 — APPEND-ONLY evidence history: every submission that produced verify outcomes is archived here
        // (small DTOs, bounded by the resubmission count; never cleared on re-submit). VerifyResults above stays the
        // CURRENT state that drives advancement; this is the immutable record get_workflow_run + the terminal run
        // record surface. Null-able on the wire only via serialization of an empty list (kept always-present here).
        public List<VerifyAttempt> VerifyHistory { get; set; } = new List<VerifyAttempt>();
        public string EffectiveStrictness { get; set; } // what the gate actually ran at (auditable)
        // Null-omitted: a run with no deferred loop must keep the acceptance-check-34 no-loop golden exact.
        [Newtonsoft.Json.JsonProperty(NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore)]
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public PendingForEachExpansion PendingForEach { get; set; }

        public StepResult Clone()
        {
            var c = (StepResult)MemberwiseClone();
            c.Answers = Answers.ToDictionary(kv => kv.Key, kv => new AnswerValue
            {
                Value = kv.Value.Value, Declined = kv.Value.Declined, DeclineReason = kv.Value.DeclineReason
            });
            c.VerifyResults = VerifyResults.Select(v => v.Clone()).ToArray();
            c.VerifyHistory = VerifyHistory.Select(a => a.Clone()).ToList();
            if (PendingForEach != null) c.PendingForEach = PendingForEach.Clone();
            return c;
        }
    }

    /// <summary>One executable step instance in a run's mutable plan. Top-level entries retain the source
    /// step id as their instance id; later runner units may splice loop and call instances without changing
    /// the immutable definition snapshot or breaking result alignment.</summary>
    public sealed class PlannedStep
    {
        public WorkflowStep Step { get; }
        public string InstanceId { get; }
        public string FrameId { get; }
        public int? IterationIndex { get; }
        // An empty loop keeps its authored source row so the run can record not_applicable. This bit
        // distinguishes that expanded template from one still waiting for values without altering ForEach.
        internal bool ForEachExpanded { get; }

        /// <summary>[T220] The FROZEN <see cref="WorkflowDef"/> that authored <see cref="Step"/>. DECISION 1.7
        /// unrolls a callee into the caller's one plan, so strictness, gate metadata, provenance and condition
        /// roots are row questions: reading the run's root for a callee row gives a valid-looking wrong answer.
        ///
        /// NOT the same Owner as <c>WorkflowRunner.VisibleForEachSource.Owner</c>, which is a
        /// <see cref="GateInput"/> meaning "the gate-input declaration on the answering row". Different types
        /// on different classes, so they never collide at the compiler, but they sit a few lines apart in
        /// ResolveVisibleForEachSource and they do collide in the reader.
        ///
        /// Null only on a row built through the PUBLIC constructor, which exists for source compatibility and
        /// isolated fixtures. Such a row is resolved against the run's own frozen closure by
        /// <see cref="WorkflowOwnerClosure.OwnerOf"/> and refuses when that cannot name exactly one owner; a
        /// bare <see cref="WorkflowStep"/> never infers a workflow on its own.</summary>
        internal WorkflowDef Owner { get; }

        /// <summary>[T220] This `call:` row has already been spliced into a call frame plus the callee's rows.
        /// A SEPARATE bit from <see cref="ForEachExpanded"/>, deliberately: a later unit lets one row be both a
        /// loop template and a hand-off, and one shared bit could not say which of the two had happened.
        /// Execution state only — it reaches no view, no record and neither serializer.</summary>
        internal bool CallExpanded { get; }

        public PlannedStep(WorkflowStep step, string instanceId, string frameId, int? iterationIndex,
            bool forEachExpanded = false)
            : this(step, instanceId, frameId, iterationIndex, owner: null, forEachExpanded) { }

        internal PlannedStep(WorkflowStep step, string instanceId, string frameId, int? iterationIndex,
            WorkflowDef owner, bool forEachExpanded = false, bool callExpanded = false)
        {
            Step = step ?? throw new ArgumentNullException(nameof(step));
            InstanceId = instanceId ?? throw new ArgumentNullException(nameof(instanceId));
            FrameId = frameId ?? throw new ArgumentNullException(nameof(frameId));
            IterationIndex = iterationIndex;
            ForEachExpanded = forEachExpanded || iterationIndex.HasValue;
            Owner = owner;
            CallExpanded = callExpanded;
        }
    }

    /// <summary>[T220] The pure result of <c>WorkflowRunner.ResolveCallSplice</c>: everything the mutating
    /// splice needs and nothing it could use to re-decide. It holds no run reference and no mutable
    /// definition, so a resolve that is never committed cannot half-expand a hand-off.
    ///
    /// IMMUTABLE, over a COPIED and wrapped row list. It was a settable blueprint, and that was a defect at the
    /// authority boundary: a resolve is a snapshot the caller then holds, so setters and a replaceable
    /// <see cref="Rows"/> let a caller keep the frozen owner's label and swap in another definition's steps
    /// between the pure resolve and the commit. Immutability is not on its own the guard —
    /// <c>WorkflowRunner.SpliceCallAt</c> RE-RESOLVES against the live run and refuses anything that is no
    /// longer what this run resolves now — but it closes the route rather than only detecting the result.
    ///
    /// The constructor validates nothing on purpose. Every admissibility rule lives in one place,
    /// <c>ResolveCallSplice</c>, and a second half-copy of it here is the duplicate-authority shape this
    /// correction removes; fixtures also need to build a deliberately corrupt blueprint to prove the commit
    /// refuses it.</summary>
    internal sealed class CallSplice
    {
        internal WorkflowDef Owner { get; }
        internal int Depth { get; }
        internal string FrameId { get; }
        internal string HeaderInstanceId { get; }
        internal IReadOnlyList<(string InstanceId, WorkflowStep Step)> Rows { get; }

        internal CallSplice(WorkflowDef owner, int depth, string frameId, string headerInstanceId,
            IEnumerable<(string InstanceId, WorkflowStep Step)> rows)
        {
            Owner = owner;
            Depth = depth;
            FrameId = frameId;
            HeaderInstanceId = headerInstanceId;
            Rows = new System.Collections.ObjectModel.ReadOnlyCollection<(string InstanceId, WorkflowStep Step)>(
                (rows ?? Enumerable.Empty<(string InstanceId, WorkflowStep Step)>()).ToList());
        }
    }

    internal sealed record WorkflowAnswerSource(AnswerValue Answer, GateInput Declaration, int PlanIndex, int DeclarationOrder = 0)
    {
        internal WorkflowAnswerSource DeepCopy() => new(RunFrame.CloneAnswer(Answer), Declaration, PlanIndex, DeclarationOrder);
    }

    /// <summary>Proof state for one execution frame. The top-level frame has the run id and preserves the
    /// unlooped runner's former singleton semantics alongside iteration and call frames.</summary>
    public sealed class RunFrame
    {
        public string FrameId { get; }
        public Dictionary<string, string> WitnessLocks { get; } = new Dictionary<string, string>(StringComparer.Ordinal);
        public List<WitnessRevision> WitnessRevisions { get; } = new List<WitnessRevision>();
        public Dictionary<string, string> PartitionLocks { get; } = new Dictionary<string, string>(StringComparer.Ordinal);
        public List<PartitionRevision> PartitionRevisions { get; } = new List<PartitionRevision>();
        public Dictionary<string, AnchorRunLock> RunAnchorLocks { get; } = new Dictionary<string, AnchorRunLock>(StringComparer.Ordinal);
        public List<AnchorRevision> AnchorRevisions { get; } = new List<AnchorRevision>();
        public CoverageSurfaceLock CoverageSurface { get; set; }
        public List<CoverageSurfaceRevision> CoverageSurfaceRevisions { get; } = new List<CoverageSurfaceRevision>();
        public Dictionary<string, ShapeMismatchLedgerEntry> ShapeMismatchLedger { get; }
            = new Dictionary<string, ShapeMismatchLedgerEntry>(StringComparer.Ordinal);
        public List<ShapeMismatchCountersign> ShapeMismatchCountersigns { get; }
            = new List<ShapeMismatchCountersign>();
        public EquivalenceCandidateReceipt LastPassedEquivalenceCandidate { get; set; }
        public int AnchorFormRepairCount { get; set; }

        // Runner-unit seam only. The top-level compatibility frame has no projection metadata, so it never
        // appears on the wire; later loop/call executors can create the two ratified frame kinds here.
        internal string ProjectionKind { get; private set; }
        internal string StepId { get; private set; }
        internal string State { get; private set; }
        internal int? IterationIndex { get; private set; }
        internal string LoopVariable { get; private set; }
        internal string LoopValue { get; private set; }
        internal string Workflow { get; private set; }
        internal int? Depth { get; private set; }
        internal string[] Passed { get; private set; }
        internal string[] Returned { get; private set; }
        internal string ReturnNote { get; set; }

        /// <summary>[T220] The frame this one was created inside, or null on the top-level frame. Depth and
        /// cycle are decided by WALKING this chain, never by parsing a frame id: ids are built by
        /// concatenation and are not a grammar. Optional and null-defaulted so existing fixture call sites
        /// keep compiling; a fixture frame added straight to a top-level run then reads as depth 1, which is
        /// what those fixtures mean.</summary>
        internal string ParentFrameId { get; private set; }

        // A frame starts from one point-in-time answer namespace. It is deliberately absent from every
        // frame view and record projection: seeds are resolution state, not a second answer ledger.
        private Dictionary<string, AnswerValue> _answerSeed;
        private IReadOnlyDictionary<string, WorkflowAnswerSource> _seedSources;
        private Dictionary<string, WorkflowAnswerSource> _boundInputs;
        internal Dictionary<string, WorkflowAnswerSource> ReturnSources { get; private set; }
        internal int? ReturnPlanIndex { get; private set; }
        internal string[] ReturnNames { get; private set; } = Array.Empty<string>();
        internal bool CallInputsCommitted => _boundInputs != null;

        internal void CopyInitialSourcesTo(Dictionary<string, WorkflowAnswerSource> target)
        {
            foreach (var pair in _answerSeed)
                target[pair.Key] = _seedSources != null && _seedSources.TryGetValue(pair.Key, out var source)
                    ? source.DeepCopy() : new WorkflowAnswerSource(CloneAnswer(pair.Value), null, -1);
            if (_boundInputs != null)
                foreach (var pair in _boundInputs) target[pair.Key] = pair.Value.DeepCopy();
        }

        internal void CommitCallInputs(IReadOnlyDictionary<string, WorkflowAnswerSource> inputs)
        {
            if (_boundInputs != null) throw new InvalidOperationException($"Call frame '{FrameId}' already has committed inputs.");
            _boundInputs = inputs.ToDictionary(p => p.Key, p => p.Value.DeepCopy(), StringComparer.Ordinal);
            Passed = _boundInputs.Keys.ToArray();
        }

        internal void PublishReturns(IReadOnlyDictionary<string, WorkflowAnswerSource> answers, int planIndex)
        {
            if (ReturnSources != null) throw new InvalidOperationException($"Call frame '{FrameId}' already returned its answers.");
            if (planIndex < 0) throw new ArgumentOutOfRangeException(nameof(planIndex));
            ReturnSources = answers.ToDictionary(p => p.Key, p => p.Value.DeepCopy(), StringComparer.Ordinal);
            // Expansions only insert at or beyond the cursor. Once this call exits, its return position is fixed.
            ReturnPlanIndex = planIndex;
            Returned = ReturnSources.Keys.ToArray();
        }

        public RunFrame(string frameId) : this(frameId, new Dictionary<string, AnswerValue>()) { }

        internal RunFrame(string frameId, IReadOnlyDictionary<string, AnswerValue> answerSeed)
        {
            FrameId = frameId ?? throw new ArgumentNullException(nameof(frameId));
            SetAnswerSeed(answerSeed);
        }

        internal void SetAnswerSeed(IReadOnlyDictionary<string, AnswerValue> answerSeed)
        {
            if (_answerSeed != null)
                throw new InvalidOperationException($"Frame '{FrameId}' already has an answer seed.");
            if (answerSeed == null) throw new ArgumentNullException(nameof(answerSeed));
            _answerSeed = answerSeed.ToDictionary(kv => kv.Key, kv => CloneAnswer(kv.Value), StringComparer.Ordinal);
        }

        internal void CopyAnswerSeedTo(Dictionary<string, AnswerValue> target)
        {
            foreach (var kv in _answerSeed)
                target[kv.Key] = CloneAnswer(kv.Value);
        }

        internal static AnswerValue CloneAnswer(AnswerValue value) => value == null ? null : new AnswerValue
        {
            Value = value.Value,
            Declined = value.Declined,
            DeclineReason = value.DeclineReason,
        };

        internal static RunFrame CreateIteration(string frameId, string stepId, int iterationIndex,
            string loopVariable, string loopValue, string state,
            IReadOnlyDictionary<string, AnswerValue> answerSeed = null,
            string parentFrameId = null,
            IReadOnlyDictionary<string, WorkflowAnswerSource> seedSources = null) => new RunFrame(
                frameId, answerSeed ?? new Dictionary<string, AnswerValue>())
        {
            ProjectionKind = "iteration",
            StepId = stepId,
            IterationIndex = iterationIndex,
            LoopVariable = loopVariable,
            LoopValue = loopValue,
            State = state,
            ParentFrameId = parentFrameId,
            _seedSources = seedSources?.ToDictionary(p => p.Key, p => p.Value.DeepCopy(), StringComparer.Ordinal),
        };

        internal static RunFrame CreateCall(string frameId, string stepId, string workflow, int depth,
            string[] passed, string[] returned, string state,
            IReadOnlyDictionary<string, AnswerValue> answerSeed = null,
            string parentFrameId = null, string[] requestedReturns = null) => new RunFrame(
                frameId, answerSeed ?? new Dictionary<string, AnswerValue>())
        {
            ProjectionKind = "call",
            StepId = stepId,
            Workflow = workflow,
            Depth = depth,
            Passed = passed?.ToArray() ?? Array.Empty<string>(),
            Returned = returned?.ToArray() ?? Array.Empty<string>(),
            ReturnNames = (requestedReturns ?? returned)?.ToArray() ?? Array.Empty<string>(),
            State = state,
            ParentFrameId = parentFrameId,
        };

        internal void Complete(string state)
        {
            if (State != "in_progress")
                throw new InvalidOperationException($"Frame '{FrameId}' is '{State}', not in_progress.");
            if (state != "passed" && state != "failed")
                throw new ArgumentException("A frame can complete only as passed or failed.", nameof(state));
            State = state;
        }
    }

    /// <summary>[T220] The deep copy that makes a run's definitions FROZEN. <c>LoadWorkflowDefs()</c> hands
    /// back mutable objects and existing tests edit <see cref="WorkflowDef.Strictness"/> in place, so merely
    /// RETAINING the input object is not a freeze: a mid-run file reload or a stray edit would retune a gate
    /// that already ran. Everything an execution or a public identity answer can read is copied — frontmatter,
    /// provenance, every step, gate, input, verify, when, forEach and call — into new containers, never
    /// shared references. A list of selected scalar snapshots was rejected: it would pass today's strictness
    /// test while leaving the next owner-derived field mutable.</summary>
    internal static class WorkflowFreeze
    {
        internal static WorkflowDef Freeze(WorkflowDef def)
        {
            if (def == null) return null;
            return new WorkflowDef
            {
                SchemaVersion = def.SchemaVersion,
                Name = def.Name,
                Kind = def.Kind,
                Title = def.Title,
                Description = def.Description,
                WhenToUse = def.WhenToUse,
                Version = def.Version,
                Strictness = def.Strictness,
                Triggers = Copy(def.Triggers),
                Tags = Copy(def.Tags),
                Source = def.Source,
                FilePath = def.FilePath,
                // Copied for DIAGNOSTICS only: validation runs before a copy can enter a run, so a frozen
                // definition carrying an Error is a refusal that already happened, not one waiting to happen.
                Error = def.Error,
                HasUnreadSchemaVersion = def.HasUnreadSchemaVersion,
                Steps = (def.Steps ?? Array.Empty<WorkflowStep>()).Select(FreezeStep).ToArray(),
                Slots = (def.Slots ?? Array.Empty<SlotDef>()).Select(FreezeSlot).ToArray(),
                Provenance = CopyMap(def.Provenance, StringComparer.Ordinal),
            };
        }

        private static WorkflowStep FreezeStep(WorkflowStep step) => step == null ? null : new WorkflowStep
        {
            Id = step.Id,
            Number = step.Number,
            Title = step.Title,
            Instructions = step.Instructions,
            Ops = Copy(step.Ops),
            When = step.When,
            HasExplicitId = step.HasExplicitId,
            Gate = FreezeGate(step.Gate),
            ForEach = step.ForEach == null ? null : new ForEachSpec
            {
                InLiteral = Copy(step.ForEach.InLiteral),
                InInput = step.ForEach.InInput,
                As = step.ForEach.As,
                MaxIterations = step.ForEach.MaxIterations,
            },
            Call = step.Call == null ? null : new CallSpec
            {
                Workflow = step.Call.Workflow,
                With = CopyMap(step.Call.With, StringComparer.Ordinal),
                Returns = Copy(step.Call.Returns),
            },
        };

        private static GateSpec FreezeGate(GateSpec gate) => gate == null ? null : new GateSpec
        {
            Strictness = gate.Strictness,
            Inputs = (gate.Inputs ?? Array.Empty<GateInput>()).Select(i => i == null ? null : new GateInput
            {
                Name = i.Name, Question = i.Question, Type = i.Type, Required = i.Required,
                DaxPurity = i.DaxPurity, Scope = i.Scope,
            }).ToArray(),
            Verify = (gate.Verify ?? Array.Empty<VerifySpec>()).Select(v => v == null ? null : new VerifySpec
            {
                Kind = v.Kind, When = v.When, Probe = v.Probe, Scope = v.Scope, Intent = v.Intent,
                PinnedShapes = Copy(v.PinnedShapes), OpenShapes = Copy(v.OpenShapes),
                OpenShapesFrom = v.OpenShapesFrom, OpenMismatch = v.OpenMismatch, Anchors = v.Anchors,
            }).ToArray(),
        };

        private static SlotDef FreezeSlot(SlotDef slot) => slot == null ? null : new SlotDef
        {
            Name = slot.Name, Question = slot.Question, Type = slot.Type, Required = slot.Required,
            Default = slot.Default, Example = slot.Example, Hint = slot.Hint, Values = Copy(slot.Values),
        };

        private static string[] Copy(string[] values) => values == null ? null : values.ToArray();

        private static Dictionary<string, string> CopyMap(Dictionary<string, string> map, StringComparer comparer) =>
            map == null ? new Dictionary<string, string>(comparer) : new Dictionary<string, string>(map, comparer);
    }

    /// <summary>[T220] The complete reachable owner closure of one run, frozen once at start and retained for
    /// the run's whole life. Every later splice looks an already-frozen owner up here rather than loading or
    /// freezing a callee mid-run, and every inventory (witness probes, BPA and readiness baselines, condition
    /// roots, entitlement) folds over the same set — a start-local dictionary could not serve any of them.
    ///
    /// The name index is case-insensitive to match <c>WorkflowParser.ByName</c>, so "which file is this name?"
    /// keeps ONE answer across the checker and the runner.</summary>
    internal sealed class WorkflowOwnerClosure
    {
        private readonly Dictionary<string, WorkflowDef> _byName =
            new Dictionary<string, WorkflowDef>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<WorkflowDef, string> _settingsByOwner =
            new Dictionary<WorkflowDef, string>(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<WorkflowStep, WorkflowDef> _ownerByStep =
            new Dictionary<WorkflowStep, WorkflowDef>(ReferenceEqualityComparer.Instance);
        private readonly HashSet<WorkflowStep> _ambiguousSteps =
            new HashSet<WorkflowStep>(ReferenceEqualityComparer.Instance);

        internal WorkflowDef Root { get; }

        internal IReadOnlyCollection<WorkflowDef> Definitions { get; }

        /// <summary>Members must ALREADY be frozen and the root must be first. Settings strictness is stored
        /// by frozen-owner reference rather than by name, so a run whose root has no name still answers for
        /// its own row.</summary>
        internal WorkflowOwnerClosure(IReadOnlyList<(WorkflowDef Def, string Settings)> members)
        {
            if (members == null || members.Count == 0)
                throw new ArgumentException("A workflow owner closure needs at least the root definition.", nameof(members));
            Root = members[0].Def;
            var ordered = new List<WorkflowDef>();
            foreach (var (def, settings) in members)
            {
                if (def == null) continue;
                if (_settingsByOwner.ContainsKey(def)) continue;
                _settingsByOwner[def] = settings;
                ordered.Add(def);
                if (!string.IsNullOrEmpty(def.Name) && !_byName.ContainsKey(def.Name)) _byName[def.Name] = def;
                foreach (var step in def.Steps ?? Array.Empty<WorkflowStep>())
                {
                    if (step == null) continue;
                    // A step object shared by two frozen owners cannot name one, so it names none. Freezing
                    // copies per definition, so this only ever fires on a hand-built closure.
                    if (_ownerByStep.TryGetValue(step, out var existing) && !ReferenceEquals(existing, def))
                        _ambiguousSteps.Add(step);
                    else _ownerByStep[step] = def;
                }
            }
            Definitions = ordered;
        }

        /// <summary>The frozen definition for a reachable name. A miss REFUSES: nothing is loaded or frozen
        /// mid-run, so a name the closure does not hold is a call graph that was never validated.</summary>
        internal WorkflowDef Require(string name) =>
            !string.IsNullOrWhiteSpace(name) && _byName.TryGetValue(name, out var def) ? def
                : throw new InvalidOperationException(
                    $"Workflow '{name}' is not in this run's frozen owner closure ({string.Join(", ", _byName.Keys)}), so no row can be owned by it.");

        internal bool Contains(WorkflowDef owner) => owner != null && _settingsByOwner.ContainsKey(owner);

        /// <summary>The per-workflow settings override frozen for this owner, or null when the owner has none
        /// (including an owner this closure does not hold — an unregistered name has no setting to read).</summary>
        internal string SettingsStrictness(WorkflowDef owner) =>
            owner != null && _settingsByOwner.TryGetValue(owner, out var value) ? value : null;

        /// <summary>The one frozen owner that authored this exact step object, by REFERENCE. Null when no
        /// member owns it or when two do. This is the compatibility resolution for a row built through the
        /// public <see cref="PlannedStep"/> constructor; it resolves against THIS run's frozen closure, and
        /// never pretends a bare step can name a workflow on its own.</summary>
        internal WorkflowDef OwnerOf(WorkflowStep step) =>
            step != null && !_ambiguousSteps.Contains(step) && _ownerByStep.TryGetValue(step, out var owner) ? owner : null;
    }

    /// <summary>The mutable, session-held run. NOT internally locked: every access is serialized by
    /// the engine's <c>_workflowGate</c> — the single owner of run-state concurrency across both
    /// doors (same discipline as <see cref="ChangePlanState"/>/<c>_planGate</c>).</summary>
    public sealed class WorkflowRunState
    {
        public string RunId { get; }

        /// <summary>[T220] The run's complete reachable owner closure, frozen before the run existed and
        /// retained until the store evicts the run. Execution state only: no owner or closure value reaches a
        /// view, a record, a certificate or the wire.</summary>
        internal WorkflowOwnerClosure OwnerClosure { get; }

        // The frozen ROOT: what the caller started, and the answer to every public identity question. A
        // mid-run file edit cannot tear the run because the definition behind this is a deep copy.
        public WorkflowDef Def => OwnerClosure.Root;
        // Per-workflow settings override, frozen at start — the ROOT's. A row resolves its own owner's
        // setting through the closure; this public accessor keeps answering for the workflow the caller named.
        public string SettingsStrictness => OwnerClosure.SettingsStrictness(OwnerClosure.Root);
        public string GlobalStrictness { get; }        // the model-wide enforcement override, frozen at start (a mid-run toggle can't tear the run)
        /// <summary>Per-callee strictness override for this run ("turn its gate on for this run"). Keyed by callee name.</summary>
        internal Dictionary<string, string> CallStrictnessOverride { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
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
        public List<RunFrame> Frames { get; }
        public List<PlannedStep> Plan { get; }
        public List<StepResult> Results { get; }
        // Snapshot of non-answer facts gathered at start for step-level conditions. Input answers and the
        // current plan frame are overlaid by WorkflowRunner at each evaluation.
        public PredicateFacts FrameFacts { get; set; }

        // Compatibility accessors use the submitted plan entry's captured frame for the whole submission.
        // Outside that narrow scope every existing view, record and background read keeps top-level behaviour.
        private RunFrame _submissionFrame;
        private int? _submissionPlanIndex;
        private RunFrame TopFrame => Frames[0];
        private RunFrame ProofFrame => _submissionFrame ?? TopFrame;
        public Dictionary<string, string> WitnessLocks => ProofFrame.WitnessLocks;
        public List<WitnessRevision> WitnessRevisions => ProofFrame.WitnessRevisions;
        public Dictionary<string, string> PartitionLocks => ProofFrame.PartitionLocks;
        public List<PartitionRevision> PartitionRevisions => ProofFrame.PartitionRevisions;
        public Dictionary<string, AnchorRunLock> RunAnchorLocks => ProofFrame.RunAnchorLocks;
        public List<AnchorRevision> AnchorRevisions => ProofFrame.AnchorRevisions;
        public CoverageSurfaceLock CoverageSurface { get => ProofFrame.CoverageSurface; set => ProofFrame.CoverageSurface = value; }
        public List<CoverageSurfaceRevision> CoverageSurfaceRevisions => ProofFrame.CoverageSurfaceRevisions;
        public Dictionary<string, ShapeMismatchLedgerEntry> ShapeMismatchLedger => ProofFrame.ShapeMismatchLedger;
        public List<ShapeMismatchCountersign> ShapeMismatchCountersigns => ProofFrame.ShapeMismatchCountersigns;
        public EquivalenceCandidateReceipt LastPassedEquivalenceCandidate
        {
            get => ProofFrame.LastPassedEquivalenceCandidate;
            set => ProofFrame.LastPassedEquivalenceCandidate = value;
        }
        public int AnchorFormRepairCount { get => ProofFrame.AnchorFormRepairCount; set => ProofFrame.AnchorFormRepairCount = value; }

        internal IDisposable CaptureSubmissionFrame()
        {
            if (_submissionFrame != null)
                throw new InvalidOperationException($"Workflow run '{RunId}' already has a submission frame scope.");
            if (Status != "active" || StepIndex < 0 || StepIndex >= Plan.Count)
                throw new InvalidOperationException($"Workflow run '{RunId}' has no current planned step to submit.");

            var frameId = Plan[StepIndex].FrameId;
            var matches = Frames.Where(f => string.Equals(f.FrameId, frameId, StringComparison.Ordinal)).ToArray();
            if (matches.Length != 1)
                throw new InvalidOperationException(matches.Length == 0
                    ? $"Planned step '{Plan[StepIndex].InstanceId}' names unknown frame '{frameId}'."
                    : $"Planned step '{Plan[StepIndex].InstanceId}' names duplicate frame '{frameId}'.");

            _submissionFrame = matches[0];
            _submissionPlanIndex = StepIndex;
            return new SubmissionFrameScope(this, matches[0], StepIndex);
        }

        private sealed class SubmissionFrameScope : IDisposable
        {
            private WorkflowRunState _owner;
            private readonly RunFrame _frame;
            private readonly int _planIndex;

            public SubmissionFrameScope(WorkflowRunState owner, RunFrame frame, int planIndex)
            {
                _owner = owner;
                _frame = frame;
                _planIndex = planIndex;
            }

            public void Dispose()
            {
                var owner = _owner;
                if (owner == null) return;
                _owner = null;
                var active = owner._submissionFrame;
                var activePlanIndex = owner._submissionPlanIndex;
                owner._submissionFrame = null;
                owner._submissionPlanIndex = null;
                if (!ReferenceEquals(active, _frame) || activePlanIndex != _planIndex)
                    throw new InvalidOperationException($"Workflow run '{owner.RunId}' submission frame scope was replaced before disposal.");
            }
        }

        internal (RunFrame Frame, int PlanIndex) AnswerResolutionContext()
        {
            if (_submissionFrame != null)
                return (_submissionFrame, _submissionPlanIndex.Value);

            if (Status == "active" && StepIndex >= 0 && StepIndex < Plan.Count)
                return (FrameAtPlanIndex(StepIndex), StepIndex);

            return (TopFrame, Plan.Count - 1);
        }

        /// <summary>[T220] The plan row a verify-side read is executing against: the captured submission row
        /// inside a submission scope, the current row outside one. Verify helpers that need row-owned facts
        /// (control-total provenance, the objectRef cutoff) resolve through this rather than through the root,
        /// and through the SAME cutoff the answers they were handed were projected at.</summary>
        internal PlannedStep SubmittedRow
        {
            get
            {
                var index = AnswerResolutionContext().PlanIndex;
                return index >= 0 && index < Plan.Count ? Plan[index] : null;
            }
        }

        // Observe full frame-list searches in bounded-work regressions; never part of the run projection.
        internal Action FrameLookupForTest { get; set; }

        internal RunFrame FrameAtPlanIndex(int planIndex)
        {
            FrameLookupForTest?.Invoke();
            if (planIndex < 0 || planIndex >= Plan.Count)
                throw new ArgumentOutOfRangeException(nameof(planIndex));
            var planned = Plan[planIndex];
            var matches = Frames.Where(f => string.Equals(f.FrameId, planned.FrameId, StringComparison.Ordinal)).ToArray();
            if (matches.Length != 1)
                throw new InvalidOperationException(matches.Length == 0
                    ? $"Planned step '{planned.InstanceId}' names unknown frame '{planned.FrameId}'."
                    : $"Planned step '{planned.InstanceId}' names duplicate frame '{planned.FrameId}'.");
            return matches[0];
        }

        // Legacy per-verify anchor locks remain run-level for compatibility with existing records and the pure helper;
        // acceptance check 17b does not include them in the proof-store move.
        public Dictionary<string, string> AnchorLocks { get; } = new Dictionary<string, string>(StringComparer.Ordinal);
        // Name-based object refs remain untouched in the submitted answers for audit. A measure rename records the
        // old-to-new ref here, and later verifies follow the chain within this run.
        public Dictionary<string, string> ObjectRefAliases { get; }
            = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public WorkflowCertificate Certificate { get; set; }

        /// <summary>The public route in: it DEEP-FREEZES the supplied definition and builds a one-definition
        /// closure over it, so a direct caller or an old fixture cannot create a run over a mutable definition
        /// and then retune a gate that already ran.</summary>
        public WorkflowRunState(string runId, WorkflowDef def, string settingsStrictness, string globalStrictness = null)
            : this(runId, new WorkflowOwnerClosure(new[] { (WorkflowFreeze.Freeze(def), settingsStrictness) }), globalStrictness)
        {
        }

        /// <summary>The production route in, taking an ALREADY-FROZEN closure. It must not freeze again: the
        /// start path resolved, validated, entitled and snapshotted over exactly these objects, and a second
        /// copy here would break closure reference identity for every one of them.</summary>
        internal WorkflowRunState(string runId, WorkflowOwnerClosure closure, string globalStrictness = null)
        {
            RunId = runId; OwnerClosure = closure; GlobalStrictness = globalStrictness;
            var def = closure.Root;
            Frames = new List<RunFrame> { new RunFrame(runId) };
            Plan = def.Steps.Select(s => new PlannedStep(s, s.Id, Frames[0].FrameId, null, def)).ToList();
            Results = Plan.Select(p => new StepResult { StepId = p.InstanceId, Title = p.Step.Title, Status = "pending" }).ToList();
            if (Results.Count > 0) Results[0].Status = "in_progress";
        }

        public WorkflowStep CurrentStep => Status == "active" && StepIndex < Plan.Count ? Plan[StepIndex].Step : null;

        /// <summary>[T220] The frozen definition that authored one planned row, and the ONE place a row-owner
        /// question is answered. A row spliced by production carries its owner; a row built through the public
        /// <see cref="PlannedStep"/> constructor is resolved against this run's frozen closure by the exact
        /// step object it holds. Neither route can end in a guess: an unresolvable row REFUSES.</summary>
        internal WorkflowDef RowOwner(PlannedStep planned)
        {
            if (planned == null) throw new ArgumentNullException(nameof(planned));
            var owner = planned.Owner ?? OwnerClosure.OwnerOf(planned.Step);
            if (owner == null)
                throw new InvalidOperationException(
                    $"Planned step '{planned.InstanceId}' has no workflow owner: its step is not one of this run's frozen definitions' steps. "
                    + "Every row in a run plan must be owned by the frozen definition that authored its exact step.");
            return owner;
        }

        /// <summary>Plan coherence, run before any control-field or gate refusal on a row that is about to
        /// change run state. Owner-step coherence is by REFERENCE: a row that mixes one owner with another
        /// definition's step object is refused rather than executed against the wrong frontmatter.
        ///
        /// Closure MEMBERSHIP is enforced where a row is created — every production splice takes its owner
        /// from <see cref="WorkflowOwnerClosure.Require"/>, so no unfrozen or unvalidated definition can reach
        /// a plan — rather than re-checked on every transition. Re-checking it here would additionally refuse
        /// an isolated fixture that legitimately builds a foreign frozen row through the public constructor,
        /// which the same contract requires keep working.</summary>
        internal WorkflowDef RequireCoherentRow(PlannedStep planned)
        {
            var owner = RowOwner(planned);
            var steps = owner.Steps ?? Array.Empty<WorkflowStep>();
            for (var i = 0; i < steps.Length; i++)
                if (ReferenceEquals(steps[i], planned.Step)) return owner;
            throw new InvalidOperationException(
                $"Planned step '{planned.InstanceId}' names workflow '{owner.Name}' as its owner, but that definition did not author this step. "
                + "A run plan row and its owner are matched by reference, never by name or value.");
        }
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

    /// <summary>What the caller acts on next: the exact planned instance id (the authored id for an ordinary
    /// top-level row), the current step's instruction text (authored bytes on an ordinary row, exact frame-bound
    /// values on an iteration, or the synthetic setup phrase on an expansion-only unexpanded forEach template),
    /// and the questions this row can actually accept. An expansion-only row does not advertise authored body
    /// instructions, ops, verify kinds or strictness, because that call will not run them. This is the
    /// instruction-returning-tool pattern: the agent is taught the address and work at the point of need.</summary>
    public sealed class CurrentStepView
    {
        [Newtonsoft.Json.JsonProperty(NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore)]
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public Dictionary<string, AnswerValue> ProvidedAnswers { get; set; }
        public string StepId { get; set; }
        public string Title { get; set; }
        public string Instructions { get; set; }
        public GateInput[] Questions { get; set; } = Array.Empty<GateInput>();
        public string[] VerifyKinds { get; set; } = Array.Empty<string>();
        public string EffectiveStrictness { get; set; }
        public string[] Ops { get; set; } = Array.Empty<string>();  // the step's declared action chain
        [Newtonsoft.Json.JsonProperty(NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore)]
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public HandOffWarning HandOff { get; set; }
    }

    /// <summary>Concrete RPC-safe carrier for the two frame shapes. Kind is the discriminator; nullable
    /// shape-specific members are omitted so TypeScript still receives its iteration-or-call union.</summary>
    public sealed class WorkflowRunFrameView
    {
        [Newtonsoft.Json.JsonProperty(NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore)]
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public int? ParentFrameIndex { get; set; }
        public string Kind { get; set; }
        public string StepId { get; set; }
        public string State { get; set; }
        public StepResult[] Steps { get; set; } = Array.Empty<StepResult>();
        public WitnessLockView[] WitnessLocks { get; set; }
        public WitnessRevision[] WitnessRevisions { get; set; }
        public PartitionRevision[] PartitionRevisions { get; set; }
        public AnchorLockView[] AnchorLocks { get; set; }
        public AnchorRevision[] AnchorRevisions { get; set; }
        public ShapeMismatchLedgerEntry[] ShapeMismatchLedger { get; set; }
        public ShapeMismatchCountersign[] ShapeMismatchCountersigns { get; set; }

        [Newtonsoft.Json.JsonProperty(NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore)]
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public int? IterationIndex { get; set; }
        [Newtonsoft.Json.JsonProperty(NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore)]
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public string LoopVariable { get; set; }
        [Newtonsoft.Json.JsonProperty(NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore)]
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public string LoopValue { get; set; }

        [Newtonsoft.Json.JsonProperty(NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore)]
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public string Workflow { get; set; }
        [Newtonsoft.Json.JsonProperty(NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore)]
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public int? Depth { get; set; }
        [Newtonsoft.Json.JsonProperty(NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore)]
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public string[] Passed { get; set; }
        [Newtonsoft.Json.JsonProperty(NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore)]
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public string[] Returned { get; set; }
        [Newtonsoft.Json.JsonProperty(NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore)]
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public string ReturnNote { get; set; }
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
        [Newtonsoft.Json.JsonProperty(NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore)]
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public bool? TotalStepsProvisional { get; set; }
        public StepResult[] Steps { get; set; } = Array.Empty<StepResult>();
        // Null for a linear run. Wire serializers omit nulls, preserving the pre-frame payload exactly.
        // Runtime objects keep the two kind-specific shapes without inventing a top-level frame kind.
        [Newtonsoft.Json.JsonProperty(NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore)]
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public WorkflowRunFrameView[] Frames { get; set; }
        public CurrentStepView CurrentStep { get; set; }   // null once terminal

        // E3(a)/blocker-3 — the adjudication RECEIPTS surfaced on get_workflow_run (null when none, to keep the
        // payload lean): the currently-locked witness hash per probe input, every witness revision, and every
        // shape-partition revision recorded on this run.
        public WitnessLockView[] WitnessLocks { get; set; }
        public WitnessRevision[] WitnessRevisions { get; set; }
        public PartitionRevision[] PartitionRevisions { get; set; }
        public AnchorLockView[] AnchorLocks { get; set; }           // expected_values run-level initial/current locks
        public AnchorRevision[] AnchorRevisions { get; set; }   // expected_values — the anchor-set revision receipts
        public ShapeMismatchLedgerEntry[] ShapeMismatchLedger { get; set; }
        public ShapeMismatchCountersign[] ShapeMismatchCountersigns { get; set; }
        public WorkflowCertificate Certificate { get; set; }

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

        /// <summary>Unchanged signature, and it still reaches the PUBLIC run constructor, so a direct caller
        /// gets the same freeze a direct construction gets.</summary>
        public WorkflowRunState Start(WorkflowDef def, string settingsStrictness, string globalStrictness = null) =>
            Add(seq => new WorkflowRunState("wfr-" + seq, def, settingsStrictness, globalStrictness));

        /// <summary>[T220] The production route: the start path has already frozen, validated and entitled the
        /// complete reachable closure, and the run retains that same object.</summary>
        internal WorkflowRunState Start(WorkflowOwnerClosure closure, string globalStrictness = null,
            Action<WorkflowRunState> initialize = null) => Add(seq =>
            {
                var run = new WorkflowRunState("wfr-" + seq, closure, globalStrictness);
                initialize?.Invoke(run);
                return run;
            });

        private WorkflowRunState Add(Func<int, WorkflowRunState> create)
        {
            if (_runs.Count(r => r.Status == "active") >= MaxHeld)
                throw new InvalidOperationException($"{MaxHeld} workflow runs are already active. Finish or abort one (abort_workflow) before starting another.");
            var run = create(_seq + 1);
            _seq++;
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
                ? "No workflow run exists. Start a playbook first."
                : $"Workflow run '{runId}' not found.");
    }
}
