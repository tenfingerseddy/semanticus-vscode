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
        public string Status { get; set; }             // passed | failed | skipped
        public string Detail { get; set; }             // evidence / skip reason ("offline" — never silently passed)
        public VerifyResult Clone() => (VerifyResult)MemberwiseClone();
    }

    public sealed class StepResult
    {
        public string StepId { get; set; }
        public string Title { get; set; }
        public string Status { get; set; }             // pending | in_progress | passed | skipped | failed
        public string Note { get; set; }               // skip reason / failure text
        public Dictionary<string, AnswerValue> Answers { get; set; } = new Dictionary<string, AnswerValue>();
        public VerifyResult[] VerifyResults { get; set; } = Array.Empty<VerifyResult>();
        public string EffectiveStrictness { get; set; } // what the gate actually ran at (auditable)

        public StepResult Clone()
        {
            var c = (StepResult)MemberwiseClone();
            c.Answers = Answers.ToDictionary(kv => kv.Key, kv => new AnswerValue
            {
                Value = kv.Value.Value, Declined = kv.Value.Declined, DeclineReason = kv.Value.DeclineReason
            });
            c.VerifyResults = VerifyResults.Select(v => v.Clone()).ToArray();
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
        public string ModelIdentity { get; set; }
        public string ModelName { get; set; }
        public string ModelFingerprint { get; set; }
        public string SessionId { get; set; }
        public StepResult[] Results { get; }

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

        // Learning Loop L3 (docs/learning-loop-plan.md §3.2): a run that completed with every step
        // passed and at least one REAL verify (not all-skipped/offline) is repeatable — the
        // /distill-workflow hint. Engine-set on the terminal view; the agent still parameterizes + the
        // admission gates guard the library.
        public bool Distillable { get; set; }
        public string DistillableWhy { get; set; }
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
