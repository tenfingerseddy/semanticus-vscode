import { useEffect, useMemo, useState } from 'react';
import { rpc, onWorkflowChange, onWorkflowLibraryChange } from './bridge';
import { mdToHtml } from './docrender';
import { DesignMode } from './workflowdesign';
import { ScenariosPanel } from './workflowscenarios';
import { EvidenceArtifactDialog, type EvidenceArtifactW, type EvidenceSaveResultW } from './artifactdialog';
import { uiLabel } from './copy';

// ===================================================================================================
// Workflows — a workflow is "a skill with teeth": a named, versioned playbook (markdown instructions per
// step) whose steps chain MCP primitive actions and carry engine-enforced gates. This tab makes the four
// legible halves visible — actions (ops chips), sequence (the numbered rail), instructions (verbatim), and
// constraints (the gate) — and lets a human DRIVE a run from the UI exactly as the AI assistant drives it
// over MCP (dual-drive on one run; both doors watch workflow/didChange). See docs/workflow-designer-plan.md.
//
// LIBRARY RAIL + RUN MODE live here; DESIGN MODE (the step editor / creator) is workflowdesign.tsx.
// The rail reuses the Edit-History visual language (circles on a vertical line, tinted-not-loud status),
// so "a sequence of steps over time" reads the same way twice.
// ===================================================================================================

// ---- wire shapes (camelCase, mirror Semanticus.Engine/Workflow.cs) --------------------------------
export interface WorkflowInfo {
  name: string; title: string; description: string; version: number; source: string;
  stepCount: number; gated: boolean; triggers: string[]; error?: string | null;
  // §9a availability (the manual per-workflow kill switch) + §10.6 activation (rule-driven menu curation).
  // enabled:false = can't be started until turned back on; active:false while enabled = off today's menu but
  // still startable. Both stay LISTED (the honesty rule): a hidden workflow can't be re-enabled. Optional so
  // absent reads as the engine default (on) — every check is an explicit `=== false`.
  enabled?: boolean; active?: boolean; activeReason?: string | null;
}
export interface GateInput { name: string; question: string; type: string; required: string }
export interface VerifySpec { kind: string; when?: string; probe?: string; scope?: string; intent?: string }
export interface GateSpec { strictness?: string | null; inputs: GateInput[]; verify: VerifySpec[] }
export interface WorkflowStep { id: string; number: number; title: string; instructions: string; gate?: GateSpec | null; ops: string[] }
export interface WorkflowDef {
  name: string; title: string; description: string; version: number; strictness?: string;
  triggers: string[]; source: string; filePath?: string; error?: string | null; steps: WorkflowStep[];
  provenance?: Record<string, string>;   // unknown frontmatter keys (a distilled workflow carries derived_from here)
}
interface AnswerValue { value?: string | null; declined?: boolean; declineReason?: string | null; answered?: boolean }
interface VerifyResult { kind: string; status: string; detail?: string }
interface StepResult {
  stepId: string; title: string; status: string; note?: string | null;
  answers: Record<string, AnswerValue>; verifyResults: VerifyResult[]; effectiveStrictness?: string | null;
}
interface CurrentStepView {
  stepId: string; title: string; instructions: string; questions: GateInput[];
  verifyKinds: string[]; effectiveStrictness?: string | null; ops: string[];
}
interface WorkflowRunView {
  runId: string; workflow: string; title: string; workflowVersion: number; status: string; abortReason?: string | null;
  startedUtc?: string | null; finishedUtc?: string | null; modelName?: string | null; modelFingerprint?: string | null;
  stepIndex: number; totalSteps: number; steps: StepResult[]; currentStep?: CurrentStepView | null;
}
interface WorkflowEnforcement { mode?: string | null; enforced: boolean; note?: string | null }

// §9c op→workflow BINDING (mandatory routing) — the whole project policy in one read (get_workflow_policy).
// One row per workflow (which ops REQUIRE it, inverted) + the raw op→workflow bindings + any policy contradictions.
// Mirrors Semanticus.Engine/Workflow.cs (WorkflowPolicy / WorkflowBindingView / WorkflowPolicyLint / WorkflowPolicyEntry).
export interface WorkflowPolicyEntry {
  name: string; enabled: boolean; active: boolean; activeReason?: string | null;
  gated: boolean; whenToUse?: string | null; requiredForOps: string[];
}
export interface WorkflowBindingView { op: string; require: string[]; mode: string; userDisablable: boolean }
export interface WorkflowPolicyLint { severity: string; message: string }
export interface WorkflowPolicy {
  activeProfile?: string | null; enforcement?: string | null; workflows: WorkflowPolicyEntry[];
  bindings: WorkflowBindingView[]; lints: WorkflowPolicyLint[];
}

// The v1 bindable authoring chokepoints (the EnforceBindingAsync call sites in LocalEngine.cs) — the ONLY ops a
// binding can route, offered in a fixed order. Labelled in plain language (the "UI never speaks engine" rule):
// the analyst picks "New measure", never "create_measure". An unwired op can't be bound, so only these appear.
const BINDABLE_OPS: { op: string; label: string }[] = [
  { op: 'create_measure', label: 'New measure' },
  { op: 'update_measure', label: 'Edit measure DAX' },
  { op: 'create_table', label: 'New table' },
  { op: 'create_calculated_column', label: 'New calculated column' },
  { op: 'create_relationship', label: 'New relationship' },
  { op: 'create_calculation_item', label: 'New calculation item' },
];
const opLabel = (op: string) => BINDABLE_OPS.find((b) => b.op === op)?.label ?? uiLabel(op);

// The step-node visual language — one glyph + tint per run status (mirrors Edit History's verdict rail).
// Overview mode (no run) uses the neutral 'todo' style with the step number in the circle.
const STEP_STYLE: Record<string, { glyph: string; color: string; label: string }> = {
  passed: { glyph: '✓', color: 'var(--sem-good)', label: 'passed' },
  skipped: { glyph: '⤼', color: 'var(--sem-muted)', label: 'skipped' },
  failed: { glyph: '✕', color: 'var(--sem-bad)', label: 'failed' },
  in_progress: { glyph: '▶', color: 'var(--sem-accent)', label: 'current' },
  pending: { glyph: '○', color: 'var(--sem-muted)', label: 'pending' },
};
const stepStyle = (s: string) => STEP_STYLE[s] ?? STEP_STYLE.pending;

// Verify-evidence chip tint. passed = good, failed = bad, skipped = muted (offline / not-run — never a silent pass).
const VERIFY_COLOR: Record<string, string> = { passed: 'var(--sem-good)', failed: 'var(--sem-bad)', skipped: 'var(--sem-muted)' };
const verifyColor = (s: string) => VERIFY_COLOR[s] ?? 'var(--sem-muted)';

const isDeclined = (a: AnswerValue) => a.declined === true;
const isAnswered = (a: AnswerValue) => !a.declined && a.value != null && a.value !== '';

export function WorkflowsView({ navTarget }: { navTarget?: { name: string; nonce: number } | null } = {}) {
  const [mode, setMode] = useState<'run' | 'design'>('run');
  const [creating, setCreating] = useState(false);
  const [library, setLibrary] = useState<WorkflowInfo[] | null>(null);
  const [selected, setSelected] = useState<string | null>(null);
  const [def, setDef] = useState<WorkflowDef | null>(null);
  const [run, setRun] = useState<WorkflowRunView | null>(null);
  const [tier, setTier] = useState<string>('free');
  const [libErr, setLibErr] = useState<string | null>(null);
  const [startErr, setStartErr] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [evidenceRun, setEvidenceRun] = useState<WorkflowRunView | null>(null);
  const [enforcement, setEnforcement] = useState<WorkflowEnforcement | null>(null);
  // §9c op→workflow binding ("Required for…"): the whole policy in one read, kept live off the library broadcast
  // (a set_workflow_binding from either door re-broadcasts the library — we refetch the policy alongside it).
  const [policy, setPolicy] = useState<WorkflowPolicy | null>(null);
  const refreshPolicy = () => rpc<WorkflowPolicy>('getWorkflowPolicy').then(setPolicy).catch(() => undefined);
  // A scenario wizard (the Scenarios picker at the top of the tab) is open — hide the per-workflow detail so
  // the screen stays focused on the one thing the analyst is doing (§10.12 layer 1: pick & go).
  const [scenarioActive, setScenarioActive] = useState(false);

  // A feature surface can hand off to the exact playbook that completes the job. Keep the stable workflow id
  // as the route and reset transient authoring/scenario state so the analyst lands on its runnable overview.
  useEffect(() => {
    if (!navTarget?.name) return;
    setScenarioActive(false); setCreating(false); setMode('run'); setStartErr(null); setSelected(navTarget.name);
  }, [navTarget?.nonce]);
  // A scenario applies through the SAME ops the tab already drives (enforcement / binding / instantiate). In
  // the extension the library broadcast refreshes these anyway; refresh explicitly so the tab reflects the
  // new state immediately (and the mocked harness, which does not re-broadcast, still updates live).
  const onScenarioApplied = (freshLibrary?: WorkflowInfo[]) => {
    if (freshLibrary) setLibrary(freshLibrary);
    else rpc<WorkflowInfo[]>('listWorkflows').then(setLibrary).catch(() => undefined);
    rpc<WorkflowEnforcement>('getWorkflowEnforcement').then(setEnforcement).catch(() => undefined);
    refreshPolicy();
  };

  // Load the library + entitlement; keep both live (a save_workflow from either door re-lists via the broadcast).
  useEffect(() => {
    rpc<WorkflowInfo[]>('listWorkflows').then((l) => { setLibrary(l); setLibErr(null); }).catch((e) => setLibErr(String((e as Error).message ?? e)));
    rpc<{ tier?: string }>('getEntitlement').then((e) => setTier(e?.tier ?? 'free')).catch(() => undefined);
    rpc<WorkflowEnforcement>('getWorkflowEnforcement').then(setEnforcement).catch(() => undefined);
    refreshPolicy();
    // A set_workflow_enforcement / _binding / _enabled (either door) re-broadcasts the library — refresh the
    // enforcement mode AND the binding policy alongside it, so the "Required for" control tracks the other door live.
    const offLib = onWorkflowLibraryChange((v) => {
      setLibrary(v as WorkflowInfo[]);
      rpc<WorkflowEnforcement>('getWorkflowEnforcement').then(setEnforcement).catch(() => undefined);
      refreshPolicy();
    });
    // A run transition on EITHER door: adopt it and select its workflow, so a run the AI assistant starts over
    // MCP surfaces live in Studio (dual-drive). Ignore stale views for a run we've since replaced.
    const offRun = onWorkflowChange((v) => {
      const r = v as WorkflowRunView;
      setRun(r);
      setSelected((cur) => (cur === r.workflow ? cur : r.workflow));
    });
    return () => { offLib(); offRun(); };
  }, []);

  // Auto-select the first clean (non-error) workflow so the tab opens on a real overview, not a blank pane.
  useEffect(() => {
    if (selected || !library) return;
    const first = library.find((w) => !w.error) ?? library[0];
    if (first) setSelected(first.name);
  }, [library, selected]);

  // Load the selected workflow's definition (its full instruction text + gates) — free: reading the playbook is content.
  useEffect(() => {
    if (!selected) { setDef(null); return; }
    let alive = true;
    rpc<WorkflowDef>('getWorkflow', selected).then((d) => { if (alive) setDef(d); }).catch(() => { if (alive) setDef(null); });
    return () => { alive = false; };
  }, [selected]);

  const selectedInfo = library?.find((w) => w.name === selected) ?? null;
  // A run belongs to the selected workflow while it's live (or terminal but still the last thing we ran here).
  const activeRun = run && run.workflow === selected ? run : null;

  const startRun = async () => {
    if (!selected) return;
    setBusy(true); setStartErr(null);
    try { const r = await rpc<WorkflowRunView>('startWorkflow', selected, 'human'); setRun(r); }
    catch (e) { setStartErr(String((e as Error).message ?? e)); }   // the engine's EntitlementException message IS the upsell — show it verbatim
    finally { setBusy(false); }
  };

  const submit = async (stepId: string, answersJson: string) => {
    if (!activeRun) return;
    const r = await rpc<WorkflowRunView>('submitWorkflowStep', activeRun.runId, stepId, answersJson, 'human');
    setRun(r);
  };
  const skip = async (stepId: string, reason: string) => {
    if (!activeRun) return;
    const r = await rpc<WorkflowRunView>('skipWorkflowStep', activeRun.runId, stepId, reason, 'human');
    setRun(r);
  };
  const abort = async (reason: string) => {
    if (!activeRun) return;
    const r = await rpc<WorkflowRunView>('abortWorkflow', activeRun.runId, reason, 'human');
    setRun(r);
  };

  // The enforcement kill-switch: off ⇔ per-definition. The engine broadcast refreshes the rail's gated dots.
  const toggleEnforcement = async () => {
    try { setEnforcement(await rpc<WorkflowEnforcement>('setWorkflowEnforcement', enforcement?.enforced === false ? 'default' : 'off', 'human')); }
    catch (e) { setStartErr(String((e as Error).message ?? e)); }
  };

  // The §9a per-workflow availability toggle (Kane's ask: not just the global switch). set_workflow_enabled is
  // free (menu curation is content); the engine returns the fresh library AND re-broadcasts it, so the other
  // door (the AI assistant over MCP) sees the flip live. A refusal is teaching content: show it verbatim.
  const [availErr, setAvailErr] = useState<string | null>(null);
  const toggleEnabled = async (name: string, enabled: boolean) => {
    setAvailErr(null);
    try { setLibrary(await rpc<WorkflowInfo[]>('setWorkflowEnabled', name, enabled, 'human')); }
    catch (e) { setAvailErr(String((e as Error).message ?? e)); }
  };

  // §9c: set (or clear) an op→workflow binding. The engine returns the fresh library AND re-broadcasts it, so the
  // other door sees the mandate flip live; we refetch the policy for immediate feedback. mode 'off' / empty require
  // CLEARS. Setting hard|warn is Pro; the engine's EntitlementException (or a locked-policy refusal) message IS the
  // teaching content — surface it verbatim as a warn banner, never a raw throw (the availability-toggle discipline).
  const [bindErr, setBindErr] = useState<string | null>(null);
  const setBinding = async (op: string, require: string[], mode: string) => {
    setBindErr(null);
    try { setLibrary(await rpc<WorkflowInfo[]>('setWorkflowBinding', op, require, mode, 'human')); await refreshPolicy(); }
    catch (e) { setBindErr(String((e as Error).message ?? e)); }
  };
  // §10.6 dynamic ACTIVATION ("Hide when…"): set/clear the set='off' rule that curates a workflow off TODAY's menu.
  // Same dual-drive shape as binding — the engine returns the fresh library AND re-broadcasts it, so the other door
  // sees the flip live; we refetch the policy for the current-state + lints. Pass BOTH when and set empty to CLEAR
  // (the engine reads that as "remove the rule"). Writing a rule is Pro; the engine's EntitlementException (or a
  // parse refusal) message IS the teaching content — surface it verbatim as a warn banner, never a raw throw.
  const [actErr, setActErr] = useState<string | null>(null);
  const setActivation = async (name: string, when: string, setStr: string) => {
    setActErr(null);
    try { setLibrary(await rpc<WorkflowInfo[]>('setWorkflowActivation', name, when, setStr, 'human')); await refreshPolicy(); }
    catch (e) { setActErr(String((e as Error).message ?? e)); }
  };
  // Plain workflow title for the "also routes to" note (UI never speaks the kebab id when a title exists).
  const titleOf = (name: string) => library?.find((w) => w.name === name)?.title || name;

  return (
    <div className="h-full flex min-h-0">
      <Library
        items={library} selected={creating ? null : selected} err={libErr} availErr={availErr}
        onSelect={(n) => { setCreating(false); setSelected(n); setStartErr(null); }}
        onNew={() => { setCreating(true); setMode('design'); setStartErr(null); }}
        enforcement={enforcement} onToggleEnforcement={toggleEnforcement}
        onToggleEnabled={toggleEnabled}
      />
      <main className="flex-1 min-w-0 overflow-auto">
        {enforcement?.enforced === false && (
          <div className="mx-4 mt-3 rounded-md px-3 py-2 text-[12px] flex items-center gap-2"
            style={{ background: 'color-mix(in srgb, var(--sem-warn, #d7a54a) 14%, transparent)', border: '1px solid color-mix(in srgb, var(--sem-warn, #d7a54a) 45%, transparent)', color: 'var(--sem-fg)' }}>
            <span>!</span>
            <span className="flex-1">Enforcement is <b>OFF</b> model-wide. Gates are skipped and runs record no verified evidence. Good for quick tasks; turn it back on for accountable runs.</span>
            <button onClick={toggleEnforcement} className="text-[11px] px-2 py-0.5 rounded-md font-semibold shrink-0"
              style={{ background: 'var(--sem-accent-soft)', color: 'var(--sem-accent)', border: '1px solid color-mix(in srgb, var(--sem-accent) 40%, transparent)' }}>
              Turn enforcement on
            </button>
          </div>
        )}
        {/* §9.16/§10.6/§10.12 the analyst-facing scenario picker — pick a scenario, answer plain questions,
            preview in plain words, apply through the existing ops. Not while authoring a new workflow (+ New). */}
        {!creating && (
          <div className="p-4 pb-0 min-w-0">
            <ScenariosPanel tier={tier} library={library} onApplied={onScenarioApplied} onActiveChange={setScenarioActive} />
          </div>
        )}
        {scenarioActive ? null : creating ? (
          <div className="flex flex-col gap-4 p-4 min-w-0">
            <DesignMode
              key="new" info={null} def={null} creating
              onSaved={(n) => { setCreating(false); setSelected(n); }}
              onDeleted={() => setCreating(false)}
            />
          </div>
        ) : !selected ? (
          <EmptyMain />
        ) : (
          <div className="flex flex-col gap-4 p-4 min-w-0">
            <Header
              info={selectedInfo} def={def} mode={mode} onMode={setMode}
              run={activeRun} tier={tier} busy={busy} startErr={startErr}
              onStart={startRun} onAbort={abort} onToggleEnabled={toggleEnabled}
              onEvidence={() => activeRun && setEvidenceRun(activeRun)}
              policy={policy} onSetBinding={setBinding} bindErr={bindErr} titleOf={titleOf}
              onSetActivation={setActivation} actErr={actErr}
            />
            {mode === 'design' ? (
              <DesignMode
                key={selected} info={selectedInfo} def={def} creating={false}
                onSaved={(n) => {
                  // reload the def (the engine re-parsed + the library broadcast already refreshed the rail)
                  rpc<WorkflowDef>('getWorkflow', n).then(setDef).catch(() => undefined);
                }}
                onDeleted={() => {
                  // a deleted shadow reverts to stock; a deleted user workflow vanishes — re-resolve either way
                  rpc<WorkflowDef>('getWorkflow', selected).then(setDef).catch(() => { setSelected(null); setMode('run'); });
                }}
              />
            ) : selectedInfo?.error ? (
              <ErrorPanel name={selectedInfo.name} error={selectedInfo.error} />
            ) : def?.error ? (
              <ErrorPanel name={def.name} error={def.error} />
            ) : !def ? (
              <Panel><div className="text-[12px]" style={{ color: 'var(--sem-muted)' }}>Loading workflow…</div></Panel>
            ) : activeRun ? (
              <RunRail run={activeRun} onSubmit={submit} onSkip={skip} onAbort={abort} />
            ) : (
              <OverviewRail def={def} />
            )}
          </div>
        )}
      </main>
      {evidenceRun && <EvidenceArtifactDialog
        title="Workflow evidence"
        subtitle={`${evidenceRun.title || evidenceRun.workflow} · run ${evidenceRun.runId} · ${evidenceRun.modelName || 'current model'}`}
        baseName={`${(evidenceRun.modelName || 'semanticus').replace(/[^\w.-]+/g, '_')}-${evidenceRun.workflow}-evidence`}
        stateKey="workflows.evidence.format"
        load={() => rpc<EvidenceArtifactW>('exportWorkflowEvidence', evidenceRun.runId)}
        save={() => rpc<EvidenceSaveResultW>('saveEvidence', 'workflow', evidenceRun.runId, 'human')}
        onClose={() => setEvidenceRun(null)}
      />}
    </div>
  );
}

// ---- library rail --------------------------------------------------------------------------------
// The stock playbooks map to journey phases so the rail stays browsable at scale (21+ seeds); user &
// learned workflows — anything not in this map — fall to 'Custom', labelled by their source badge.
// Groups render in journey order; the filter box searches name/title/description/triggers and, while
// filtering, auto-expands the matches and hides empty groups.
const GROUP_OF: Record<string, string> = {
  'dimensional-design': 'Design',
  'add-fact-table': 'Build', 'add-dimension': 'Build', 'import-table': 'Build',
  'calendar-setup': 'Build', 'time-intelligence-suite': 'Build', 'field-parameters-setup': 'Build',
  'composite-model-setup': 'Build', 'new-measure': 'Build',
  'incremental-refresh-setup': 'Data',
  'check-blast-radius': 'Quality', 'governed-rename': 'Quality', 'model-hygiene-pass': 'Quality', 'cleanup-unused': 'Quality', 'verify-measure': 'Quality',
  'optimize-dax': 'Quality', 'performance-tune': 'Quality', 'make-ai-ready': 'Quality', 'document-model': 'Quality',
  'secure-with-rls': 'Security',
  'pre-deploy-validation': 'Ship', 'deploy-to-production': 'Ship', 'adopt-source-control': 'Ship',
};
const CUSTOM_GROUP = 'Custom';
const GROUP_ORDER = ['Design', 'Build', 'Data', 'Quality', 'Security', 'Ship', CUSTOM_GROUP];
const groupOf = (w: WorkflowInfo) => GROUP_OF[w.name] ?? CUSTOM_GROUP;
const matchesFilter = (w: WorkflowInfo, f: string) =>
  !f || [w.name, w.title, w.description, ...(w.triggers ?? [])].join(' ').toLowerCase().includes(f);

function Library({ items, selected, err, availErr, onSelect, onNew, enforcement, onToggleEnforcement, onToggleEnabled }: {
  items: WorkflowInfo[] | null; selected: string | null; err: string | null; availErr: string | null;
  onSelect: (n: string) => void; onNew: () => void;
  enforcement: WorkflowEnforcement | null; onToggleEnforcement: () => void;
  onToggleEnabled: (name: string, enabled: boolean) => void;
}) {
  const [filter, setFilter] = useState('');
  const [collapsed, setCollapsed] = useState<Set<string>>(() => new Set());
  const f = filter.trim().toLowerCase();

  // Bucket into journey-ordered groups; empty groups drop out. Filtering searches every field and, since a
  // match can live in any group, forces every surviving group open (collapse state is ignored while filtering).
  const groups = useMemo(() => {
    const kept = (items ?? []).filter((w) => matchesFilter(w, f));
    return GROUP_ORDER
      .map((name) => ({ name, items: kept.filter((w) => groupOf(w) === name) }))
      .filter((g) => g.items.length > 0);
  }, [items, f]);

  const toggle = (name: string) => setCollapsed((s) => {
    const n = new Set(s); if (n.has(name)) n.delete(name); else n.add(name); return n;
  });

  return (
    <aside className="w-[300px] shrink-0 border-r flex flex-col min-h-0" style={{ borderColor: 'var(--sem-border)', background: 'var(--sem-surface)' }}>
      <div className="px-3 py-2.5 border-b shrink-0" style={{ borderColor: 'var(--sem-border)', background: 'var(--sem-surface)' }}>
        <div className="flex items-center gap-2">
          <div className="text-[13px] font-semibold flex-1">Workflows</div>
          <button onClick={onNew} title="Create a new workflow: a playbook the AI Assistant and humans share"
            className="text-[11px] px-2 py-0.5 rounded-md font-semibold"
            style={{ background: 'var(--sem-accent-soft)', color: 'var(--sem-accent)', border: '1px solid color-mix(in srgb, var(--sem-accent) 40%, transparent)' }}>
            + New
          </button>
        </div>
        {/* the enforcement kill-switch — a user doing a quick task can turn gated (Pro-enforced) runs off
            wholesale; the state lives in .semanticus/workflow-settings.json and both doors see it flip */}
        {enforcement && (
          <div className="flex items-center gap-2 mt-2">
            <span className="text-[10.5px] uppercase tracking-wide font-semibold" style={{ color: 'var(--sem-muted)' }}>Enforcement</span>
            {(() => { const on = enforcement.enforced !== false; return (
              <button onClick={onToggleEnforcement} role="switch" aria-checked={on}
                title={on
                  ? (enforcement.mode ? `Every gate runs in ${uiLabel(enforcement.mode).toLowerCase()} mode across the model. Click to turn enforcement off` : 'Gates enforce each workflow’s own strictness. Click to turn enforcement off for quick tasks')
                  : 'Enforcement is OFF. Gates are skipped everywhere. Click to restore per-workflow strictness'}
                className="text-[11px] px-2 py-0.5 rounded-full font-semibold"
                style={on
                  ? { background: 'var(--sem-accent-soft)', color: 'var(--sem-accent)', border: '1px solid color-mix(in srgb, var(--sem-accent) 40%, transparent)' }
                  : { background: 'color-mix(in srgb, var(--sem-warn, #d7a54a) 16%, transparent)', color: 'var(--sem-fg)', border: '1px solid color-mix(in srgb, var(--sem-warn, #d7a54a) 45%, transparent)' }}>
                {on ? (enforcement.mode ? `On · ${uiLabel(enforcement.mode)}` : 'On') : 'Off'}
              </button>
            ); })()}
          </div>
        )}
        {/* filter — instant substring over name/title/description/triggers; Esc or ✕ clears */}
        <div className="relative mt-2">
          <input value={filter} onChange={(e) => setFilter(e.target.value)} spellCheck={false} data-wf-filter
            onKeyDown={(e) => { if (e.key === 'Escape') setFilter(''); }}
            placeholder="Filter workflows…"
            className="w-full text-[11.5px] pl-2 pr-6 py-1 rounded-md outline-none"
            style={{ background: 'var(--sem-surface-2)', color: 'var(--sem-fg)', border: '1px solid var(--sem-border)' }} />
          {filter && (
            <button onClick={() => setFilter('')} title="Clear (Esc)"
              className="absolute right-1 top-1/2 -translate-y-1/2 text-[12px] leading-none px-1" style={{ color: 'var(--sem-muted)' }}>✕</button>
          )}
        </div>
      </div>
      <div className="flex-1 overflow-auto min-h-0">
        {err && <div className="m-3"><Banner color="var(--sem-bad)">{err}</Banner></div>}
        {/* a set_workflow_enabled refusal (e.g. no workspace open) — the engine's message teaches; show it verbatim */}
        {availErr && <div className="m-3"><Banner color="var(--sem-warn, #d7a54a)">{availErr}</Banner></div>}
        {items == null ? (
          <div className="px-3 py-3 text-[12px]" style={{ color: 'var(--sem-muted)' }}>Loading library…</div>
        ) : items.length === 0 ? (
          <div className="px-3 py-3 text-[12px]" style={{ color: 'var(--sem-muted)' }}>No workflows yet. Built-in playbooks ship with Semanticus; your own live in the .semanticus/workflows folder.</div>
        ) : groups.length === 0 ? (
          <div className="px-3 py-3 text-[12px]" style={{ color: 'var(--sem-muted)' }}>No workflows match “{filter.trim()}”.</div>
        ) : (
          <div className="flex flex-col gap-0.5 p-2">
            {groups.map((g) => {
              const open = !!f || !collapsed.has(g.name);
              return (
                <div key={g.name}>
                  <button onClick={() => toggle(g.name)} disabled={!!f}
                    className="w-full flex items-center gap-1.5 px-1 py-1 text-[10.5px] uppercase tracking-wide font-semibold disabled:opacity-100"
                    style={{ color: 'var(--sem-muted)' }}>
                    <span className="inline-block transition-transform text-[8px]" style={{ transform: open ? 'rotate(90deg)' : 'none' }}>▶</span>
                    <span className="flex-1 text-left">{g.name}</span>
                    <span className="tnum">{g.items.length}</span>
                  </button>
                  {open && (
                    <div className="flex flex-col gap-1 pb-1.5">
                      {g.items.map((w) => <LibraryCard key={w.name} w={w} active={w.name === selected} onClick={() => onSelect(w.name)} onToggleEnabled={onToggleEnabled} />)}
                    </div>
                  )}
                </div>
              );
            })}
          </div>
        )}
      </div>
    </aside>
  );
}

// A compact one-glance row: title + an origin/gate marker + the §9a on/off switch, a one-line truncated
// description, and the first few triggers. The full description + step count + strictness live on the
// selected item's header. Off (enabled:false) renders listed-but-dimmed with the reason, never hidden —
// hiding it would strand the user with no way to turn it back on (the §9a honesty rule). A div, not a
// button: the card hosts the nested switch, and nested <button>s are invalid HTML.
function LibraryCard({ w, active, onClick, onToggleEnabled }: {
  w: WorkflowInfo; active: boolean; onClick: () => void; onToggleEnabled: (name: string, enabled: boolean) => void;
}) {
  const broken = !!w.error;
  const off = w.enabled === false;                       // the manual kill switch (§9a)
  const offMenu = !broken && !off && w.active === false; // enabled, but a project rule curates it off today's menu (§10.6)
  const dim = off ? { opacity: 0.55 } : undefined;
  return (
    <div role="button" tabIndex={0} onClick={onClick} aria-pressed={active} data-workflow={w.name}
      // ARIA button pattern: Enter activates on keydown; Space only prevents scroll on keydown and activates
      // on keyup, so holding it can't repeat-fire. Target-guarded so keys on the nested switch never select.
      onKeyDown={(e) => {
        if (e.target !== e.currentTarget) return;
        if (e.key === 'Enter') { e.preventDefault(); onClick(); }
        else if (e.key === ' ') e.preventDefault();
      }}
      onKeyUp={(e) => { if (e.target === e.currentTarget && e.key === ' ') { e.preventDefault(); onClick(); } }}
      className="text-left rounded-lg border px-2.5 py-1.5 transition-colors cursor-pointer"
      style={active
        ? { background: 'var(--sem-accent-soft)', borderColor: 'color-mix(in srgb, var(--sem-accent) 45%, transparent)' }
        : { background: 'var(--sem-surface-2)', borderColor: 'var(--sem-border)' }}>
      <div className="flex items-center gap-2">
        <div className="text-[12px] font-semibold truncate flex-1 min-w-0" style={{ color: broken ? 'var(--sem-bad)' : 'var(--sem-fg)', ...dim }}>{w.title || w.name}</div>
        {broken
          ? <span className="shrink-0 text-[9px] uppercase tracking-wide font-semibold px-1.5 py-0.5 rounded" style={{ color: 'var(--sem-bad)', background: 'color-mix(in srgb, var(--sem-bad) 16%, transparent)', border: '1px solid color-mix(in srgb, var(--sem-bad) 40%, transparent)' }}>error</span>
          : w.source !== 'stock'
            ? <span style={dim}><SourceBadge source={w.source} /></span>
            : w.gated
              ? <span title="Has an enforced gate (starting a run is a Pro capability)" className="shrink-0 w-1.5 h-1.5 rounded-full" style={{ background: 'var(--sem-accent)', ...dim }} />
              : null}
        {/* the per-workflow on/off switch — full-opacity even on a dimmed card so the way back on stays obvious */}
        {!broken && (
          <AvailabilitySwitch on={!off} label={`Availability: ${w.name}`}
            title={off
              ? 'Off: this playbook can’t be started until you turn it back on. Click to turn it on.'
              : 'On: this playbook can be started. Click to turn it off for this project (it stays listed so you can turn it back on).'}
            onToggle={() => onToggleEnabled(w.name, off)} />
        )}
      </div>
      {broken ? (
        <div className="text-[10.5px] mt-0.5 truncate" style={{ color: 'var(--sem-muted)' }}>This file couldn't be read. Select it to see what went wrong.</div>
      ) : w.description ? (
        <div className="text-[10.5px] mt-0.5 truncate" style={{ color: 'var(--sem-muted)', ...dim }}>{w.description}</div>
      ) : null}
      {off && (
        <div className="text-[10.5px] mt-1" style={{ color: 'var(--sem-warn, #d7a54a)' }}
          title="Turned off with the switch above. It stays listed so you can turn it back on.">
          Off: can’t be started until you turn it back on.
        </div>
      )}
      {offMenu && (
        <div className="text-[10.5px] mt-1" style={{ color: 'var(--sem-muted)', fontStyle: 'italic' }}
          title="A project rule curates the menu. This is not the off switch: you can still run it, and the run records a note.">
          Off today’s menu: {w.activeReason || 'hidden by a project rule'}
        </div>
      )}
      {!broken && w.triggers.length > 0 && (
        <div className="flex items-center gap-1 mt-1 flex-wrap" style={dim}>
          {w.triggers.slice(0, 3).map((t) => (
            <span key={t} className="text-[9px] tnum px-1 py-0.5 rounded" style={{ background: 'var(--sem-surface)', color: 'var(--sem-muted)', border: '1px solid var(--sem-border)' }}>{opLabel(t)}</span>
          ))}
          {w.triggers.length > 3 && <span className="text-[9px]" style={{ color: 'var(--sem-muted)' }}>+{w.triggers.length - 3}</span>}
        </div>
      )}
    </div>
  );
}

// The small on/off switch shared by the library cards and the header. Stops propagation so flipping it
// never also selects the card. Muted when off (calm), accent when on. label = the accessible name (the
// switch is a bare track+knob, so assistive tech needs one); type="button" so a host form can't submit it.
function AvailabilitySwitch({ on, label, title, onToggle }: { on: boolean; label: string; title: string; onToggle: () => void }) {
  return (
    <button type="button" role="switch" aria-checked={on} aria-label={label} title={title}
      onClick={(e) => { e.stopPropagation(); onToggle(); }}
      className="shrink-0 relative rounded-full transition-colors"
      style={{
        width: 26, height: 15,
        background: on ? 'var(--sem-accent)' : 'var(--sem-surface)',
        border: `1px solid ${on ? 'var(--sem-accent)' : 'var(--sem-border)'}`,
      }}>
      <span className="absolute rounded-full transition-transform" style={{
        top: 1.5, left: 1.5, width: 10, height: 10,
        background: on ? 'var(--sem-on-accent)' : 'var(--sem-muted)',
        transform: on ? 'translateX(11px)' : 'none',
      }} />
    </button>
  );
}

export function SourceBadge({ source }: { source: string }) {
  const stock = source === 'stock';
  return (
    <span className="shrink-0 text-[9px] uppercase tracking-wide font-semibold px-1.5 py-0.5 rounded"
      title={stock ? 'Built into Semanticus and read-only' : 'A workflow saved with your model'}
      style={stock
        ? { color: 'var(--sem-muted)', background: 'var(--sem-surface)', border: '1px solid var(--sem-border)' }
        : { color: 'var(--sem-accent)', background: 'var(--sem-accent-soft)', border: '1px solid color-mix(in srgb, var(--sem-accent) 40%, transparent)' }}>
      {stock ? 'Built-in' : 'Custom'}
    </span>
  );
}

// ---- main-area header ----------------------------------------------------------------------------
function Header({ info, def, mode, onMode, run, tier, busy, startErr, onStart, onAbort, onToggleEnabled, onEvidence, policy, onSetBinding, bindErr, titleOf, onSetActivation, actErr }: {
  info: WorkflowInfo | null; def: WorkflowDef | null; mode: 'run' | 'design'; onMode: (m: 'run' | 'design') => void;
  run: WorkflowRunView | null; tier: string; busy: boolean; startErr: string | null;
  onStart: () => void; onAbort: (reason: string) => void; onToggleEnabled: (name: string, enabled: boolean) => void;
  onEvidence: () => void;
  policy: WorkflowPolicy | null; onSetBinding: (op: string, require: string[], mode: string) => void;
  bindErr: string | null; titleOf: (name: string) => string;
  onSetActivation: (name: string, when: string, setStr: string) => Promise<void>; actErr: string | null;
}) {
  const broken = !!info?.error;
  const title = broken ? (info?.name || 'Workflow') : (def?.title || info?.title || info?.name || 'Workflow');
  const gated = !!info?.gated;
  const lockedFree = gated && tier !== 'pro';
  const running = run?.status === 'active';
  // §9a/§10.6 states, kept apart on purpose: off = the manual kill switch (start refuses); offMenu = a project
  // rule curates it off today's menu (start still works, the run records a note); offButRequired = off, but a
  // team mandate force-activates it, so the engine still lets it start (the deadlock breaker).
  const off = !broken && info?.enabled === false;
  const offMenu = !broken && !off && info?.active === false;
  const offButRequired = off && info?.active === true;
  const startBlocked = off && !offButRequired;
  return (
    <Panel>
      <div className="flex items-start gap-4">
        <div className="flex-1 min-w-0">
          <div className="flex items-center gap-2 flex-wrap">
            <div className="text-[15px] font-semibold" style={broken ? { color: 'var(--sem-bad)' } : off ? { opacity: 0.65 } : undefined}>{title}</div>
            {info && <SourceBadge source={info.source} />}
            {broken
              ? <span className="text-[9px] uppercase tracking-wide font-semibold px-1.5 py-0.5 rounded-full" style={{ color: 'var(--sem-bad)', background: 'color-mix(in srgb, var(--sem-bad) 16%, transparent)', border: '1px solid color-mix(in srgb, var(--sem-bad) 40%, transparent)' }}>parse error</span>
              : gated
                ? <span className="text-[9px] uppercase tracking-wide font-semibold px-1.5 py-0.5 rounded-full" style={{ color: 'var(--sem-accent)', background: 'var(--sem-accent-soft)', border: '1px solid color-mix(in srgb, var(--sem-accent) 40%, transparent)' }}>gated · Pro</span>
                : <span className="text-[9px] uppercase tracking-wide font-semibold px-1.5 py-0.5 rounded-full" style={{ color: 'var(--sem-good)', background: 'color-mix(in srgb, var(--sem-good) 14%, transparent)' }}>free</span>}
            {off && (
              <span className="text-[9px] uppercase tracking-wide font-semibold px-1.5 py-0.5 rounded-full"
                title="Turned off for this project with the availability switch. It stays listed so you can turn it back on."
                style={{ color: 'var(--sem-warn, #d7a54a)', background: 'color-mix(in srgb, var(--sem-warn, #d7a54a) 14%, transparent)', border: '1px solid color-mix(in srgb, var(--sem-warn, #d7a54a) 40%, transparent)' }}>off</span>
            )}
          </div>
          {!broken && (def?.description || info?.description) && (
            <div className="text-[12px] mt-1" style={{ color: 'var(--sem-muted)', ...(off ? { opacity: 0.65 } : undefined) }}>{def?.description || info?.description}</div>
          )}
          <div className="text-[11px] mt-1 tnum" style={{ color: 'var(--sem-muted)' }}>
            {info ? `Version ${info.version}` : ''}{!broken && def ? ` · ${def.steps.length} step${def.steps.length === 1 ? '' : 's'}` : ''}
            {!broken && def?.strictness ? ` · ${def.strictness === 'hard' ? 'blocking checks' : def.strictness === 'warn' ? 'warning checks' : `${uiLabel(def.strictness).toLowerCase()} checks`}` : ''}
          </div>
          {/* the two menu layers, told apart in plain words (never the predicate) */}
          {startBlocked && (
            <div className="text-[11.5px] mt-2 rounded-md px-2.5 py-1.5" style={{ color: 'var(--sem-fg)', background: 'color-mix(in srgb, var(--sem-warn, #d7a54a) 12%, transparent)', border: '1px solid color-mix(in srgb, var(--sem-warn, #d7a54a) 35%, transparent)' }}>
              This playbook is turned off for this project, so it can’t be started. Use the switch on the right (or on its card in the list) to turn it back on.
            </div>
          )}
          {offButRequired && (
            <div className="text-[11.5px] mt-2 rounded-md px-2.5 py-1.5" style={{ color: 'var(--sem-fg)', background: 'color-mix(in srgb, var(--sem-warn, #d7a54a) 12%, transparent)', border: '1px solid color-mix(in srgb, var(--sem-warn, #d7a54a) 35%, transparent)' }}>
              Turned off, but a project mandate still requires it ({info?.activeReason || 'required by a policy'}), so it can still be started.
            </div>
          )}
          {offMenu && (
            <div className="text-[11.5px] mt-2 rounded-md px-2.5 py-1.5" style={{ color: 'var(--sem-muted)', background: 'var(--sem-surface-2)', border: '1px solid var(--sem-border)' }}>
              Off today’s menu: {info?.activeReason || 'hidden by a project rule'}. This is a project rule curating the menu, not the off switch. You can still start it, and the run records a note.
            </div>
          )}
        </div>
        <div className="flex flex-col items-end gap-2 shrink-0">
          {/* run/design seam — Run drives the state machine, Design edits the file (workflowdesign.tsx) */}
          <div className="flex rounded-lg overflow-hidden" style={{ border: '1px solid var(--sem-border)' }}>
            <ModeTab active={mode === 'run'} onClick={() => onMode('run')}>Run</ModeTab>
            <ModeTab active={mode === 'design'} onClick={() => onMode('design')}>Design</ModeTab>
          </div>
          {/* the same §9a switch as the card, here where the selected workflow's full state reads */}
          {!broken && info && (
            <div className="flex items-center gap-1.5">
              <span className="text-[10px] uppercase tracking-wide font-semibold" style={{ color: 'var(--sem-muted)' }}>{off ? 'Off' : 'On'}</span>
              <AvailabilitySwitch on={!off} label={`Availability: ${info.name}`}
                title={off
                  ? 'Off: this playbook can’t be started until you turn it back on. Click to turn it on.'
                  : 'On: this playbook can be started. Click to turn it off for this project (it stays listed so you can turn it back on).'}
                onToggle={() => onToggleEnabled(info.name, off)} />
            </div>
          )}
          {mode === 'run' && (
            running
              ? <ReasonButton label="Abort run" title="Abort this run. The reason is recorded on the audit trail" onConfirm={onAbort} danger />
              : <Button primary disabled={busy || lockedFree || broken || startBlocked}
                  title={broken ? 'This workflow file could not be read. Fix the error shown below before it can run.'
                    : startBlocked ? 'Turned off for this project. Turn it back on with the switch above to start a run.'
                    : lockedFree ? 'Starting an enforced workflow is a Pro capability. Free: read the steps below and follow them manually.'
                    : 'Start a run. Each gate is verified as you go.'}
                  onClick={onStart}>{busy ? 'Starting…' : run && run.workflow === info?.name && run.status !== 'active' ? 'Start again' : 'Start run'}</Button>
          )}
        </div>
      </div>
      {/* §9c "Required for" — the mandatory-routing control: which authoring actions MUST go through THIS workflow
          (dual-drive with set_workflow_binding, the third axis beside availability + strictness). Hidden for a broken
          file (you can't require a workflow that won't run). */}
      {!broken && info && (
        <RequiredForControl name={info.name} policy={policy} tier={tier} onSetBinding={onSetBinding} err={bindErr} titleOf={titleOf} />
      )}
      {/* §10.6 "Hide when" — dynamic activation: curate a workflow OFF today's menu given the date / connection /
          branch / readiness. Reads active/activeReason from the library entry + the policy lints, writes set='off'
          rules via set_workflow_activation (dual-drive with the AI-assistant door). Hidden for a broken file. */}
      {!broken && info && (
        <HideWhenControl info={info} policy={policy} tier={tier} onSetActivation={onSetActivation} err={actErr} />
      )}
      {startErr && (
        <div className="mt-3 rounded-lg px-3 py-2 text-[12px]" style={{ background: 'color-mix(in srgb, var(--sem-accent) 12%, transparent)', color: 'var(--sem-fg)', border: '1px solid color-mix(in srgb, var(--sem-accent) 34%, transparent)' }}>
          {startErr}
        </div>
      )}
      {run && run.workflow === info?.name && run.status !== 'active' && (
        <div className="mt-3 flex items-center gap-3 rounded-lg px-3 py-2 text-[12px]" style={{ background: 'var(--sem-surface-2)', color: 'var(--sem-fg)', border: '1px solid var(--sem-border)' }}>
          <span className="flex-1">{run.status === 'completed' ? '✓ Run completed' : `Run ${uiLabel(run.status).toLowerCase()}`}
            {run.abortReason ? `: ${run.abortReason}` : ''} · {run.steps.filter((s) => s.status === 'passed').length}/{run.totalSteps} steps passed.</span>
          <button type="button" onClick={onEvidence} className="shrink-0 rounded-md border px-2.5 py-1 text-[11px] font-semibold"
            style={{ borderColor: 'var(--sem-accent)', color: 'var(--sem-accent)', background: 'var(--sem-accent-soft)' }}>Evidence…</button>
        </div>
      )}
    </Panel>
  );
}

// ---- §9c "Required for" (op→workflow binding / mandatory routing) --------------------------------
// The third axis (availability · REQUIRED · strictness): which authoring actions MUST route through THIS workflow.
// Reads from get_workflow_policy (the bindings, filtered to this workflow) and writes with set_workflow_binding —
// dual-drive with the AI-assistant door, on the same session. Setting hard/warn is Pro (the enforcement is what's
// paid); reading is free, and CLEARING the last mandate on an action is free too (the engine's rule, mirrored here).
const PRO_BIND_REASON = 'Requiring an action to route through a workflow is a Pro capability. Free: read the steps below and follow the workflow manually, or curate the menu with the availability switch.';
const LOCKED_BIND_REASON = 'Locked by committed team policy. Change it in .semanticus/workflow-settings.json via review.';

// A policy lint is either about a REQUIREMENT (binding) or about a `when:` RULE (activation). The two live in
// separate controls, so split them by their plain-language phrasing to surface each lint under the right control
// (and never in both). Only the activation-topic messages mention a "rule" curating the menu.
const isActivationLint = (m: string) => /activation rule|a rule shows|a rule hides|but a rule hides it/i.test(m);
const PRO_ACT_REASON = 'Hiding a workflow when a condition holds is a Pro capability. Free: turn the whole workflow on or off with the switch above, or edit the activation list in .semanticus/workflow-settings.json.';
// The control writes ONLY set='off' rules ("hide when C"), because that is the single-rule primitive the engine's
// activation resolver actually honors (ResolveActivation in LocalEngine.Workflows.cs): a hide-rule takes the workflow
// off the menu exactly when its condition holds, and it stays visible otherwise. A lone set='on' rule does NOT
// hide-otherwise (the resolver falls through to default-on), so we don't offer it — "show only when C" is expressed
// as "hide when NOT C" (see the "Outside the month-end window" example, which shows the workflow only near month-end).
// The labels describe the SITUATION in which the workflow is HIDDEN; the expr is what the engine parses (grammar in
// WorkflowPredicate.cs — facts joined with && / ||, no parentheses). A starter set, not the whole grammar.
const ACTIVATION_EXAMPLES: { label: string; expr: string }[] = [
  { label: 'On a production workspace', expr: "connection.workspace ~ '*prod*'" },
  { label: 'Off the main branch', expr: "git.branch != 'main'" },
  { label: 'Outside the month-end window', expr: 'date.monthEndOffset < -3' },
  { label: 'Small model (under 50 tables)', expr: 'model.tableCount < 50' },
  { label: 'No row-level security', expr: 'model.hasRls == false' },
  { label: 'Readiness already B or better', expr: "model.readinessGrade >= 'B'" },
];

function RequiredForControl({ name, policy, tier, onSetBinding, err, titleOf }: {
  name: string; policy: WorkflowPolicy | null; tier: string;
  onSetBinding: (op: string, require: string[], mode: string) => void; err: string | null; titleOf: (name: string) => string;
}) {
  const isPro = tier === 'pro';
  const bindings = policy?.bindings ?? [];   // FLAT (object-form) bindings only — the editable form
  // The EFFECTIVE required-for set (get_workflow_policy inverts BOTH forms into requiredForOps): flat bindings AND
  // §9.11 conditional (array-form `when:`) bindings. policy.bindings carries ONLY the flat form, so we drive display
  // off requiredForOps and look up the flat binding per op — a conditional one has none and renders read-only.
  const requiredOps = (policy?.workflows.find((w) => w.name === name)?.requiredForOps) ?? [];
  const flatForOp = (op: string) => bindings.find((b) => b.op === op && b.require.includes(name)) ?? null;
  // Ops governed by a LOCKED flat binding (committed team policy). The UI never offers to append to / rewrite one —
  // committed policy changes belong in a reviewed file edit (the engine's agent door refuses; the human door is
  // deliberately let through, so this is a UI guard, not enforcement — see the escalation note in the PR).
  const lockedOps = new Set(bindings.filter((b) => !b.userDisablable).map((b) => b.op));
  // Add pool: the six chokepoints not already required here AND not locked elsewhere (an unwired op can't be bound).
  const addable = BINDABLE_OPS.filter((b) => !requiredOps.includes(b.op) && !lockedOps.has(b.op));
  // Binding-topic contradictions that mention THIS workflow (the deadlock: off-but-required) — surfaced loudly right
  // where the mandate is edited. Activation-topic lints (a `when:` rule hides/shows it) belong to the "Hide when"
  // control below, so they're excluded here to avoid double-surfacing (the engine phrases both in plain language).
  const lints = (policy?.lints ?? []).filter((l) => l.message.includes(name) && !isActivationLint(l.message));

  // Add: append this workflow to the action's require set (preserving any sibling workflow + its mode), default hard.
  const addOp = (op: string) => {
    const ex = bindings.find((b) => b.op === op);
    const require = ex ? Array.from(new Set([...ex.require, name])) : [name];
    onSetBinding(op, require, ex?.mode ?? 'hard');
  };
  // Remove: if this workflow is the only one required, clear the binding (free); otherwise drop just this workflow
  // and keep the rest at the same mode (a hard/warn re-set — Pro, so it's gated below like any other write).
  const removeOp = (m: WorkflowBindingView) =>
    m.require.length <= 1 ? onSetBinding(m.op, [], 'off') : onSetBinding(m.op, m.require.filter((r) => r !== name), m.mode);

  return (
    <div className="mt-3 rounded-lg border p-3" style={{ background: 'var(--sem-surface-2)', borderColor: 'var(--sem-border)' }}>
      <div className="flex items-center gap-2 flex-wrap">
        <span className="text-[10px] uppercase tracking-wide font-semibold" style={{ color: 'var(--sem-muted)' }}>Required for</span>
        {requiredOps.length > 0 && (
          <span className="text-[9px] tnum px-1.5 py-0.5 rounded-full" style={{ background: 'var(--sem-accent-soft)', color: 'var(--sem-accent)' }}>{requiredOps.length}</span>
        )}
        <div className="flex-1" />
        <AddActionMenu addable={addable} isPro={isPro} onAdd={addOp} />
      </div>
      <div className="text-[10.5px] mt-1" style={{ color: 'var(--sem-muted)' }}>
        Actions listed here must be done inside a run of this workflow. If someone does one on its own, it is either blocked (hard) or allowed but flagged for the record (warn).
      </div>

      {requiredOps.length === 0 ? (
        <div className="text-[11.5px] mt-2" style={{ color: 'var(--sem-muted)' }}>No actions require this workflow yet.</div>
      ) : (
        <div className="flex flex-col gap-1.5 mt-2">
          {requiredOps.map((op) => {
            const m = flatForOp(op);
            // A conditional (array-form `when:`) requirement — in the effective set but with no flat binding to edit.
            // Render it read-only with a plain note; rewriting a `when:` rule is a file/agent job, not this flat editor.
            if (!m) {
              return (
                <div key={op} className="flex items-center gap-2 flex-wrap rounded-md px-2 py-1.5" style={{ background: 'var(--sem-surface)', border: '1px solid var(--sem-border)' }}>
                  <span className="text-[12px] font-medium" style={{ color: 'var(--sem-fg)' }}>{opLabel(op)}</span>
                  <span title="Required by a conditional rule (it only applies when a condition holds). Edit it in .semanticus/workflow-settings.json." className="text-[9px] uppercase tracking-wide font-semibold px-1.5 py-0.5 rounded-full" style={{ color: 'var(--sem-muted)', background: 'var(--sem-surface-2)', border: '1px solid var(--sem-border)' }}>conditional</span>
                  <span className="text-[10px]" style={{ color: 'var(--sem-muted)' }}>set by a rule; edit it in the workflow settings file</span>
                </div>
              );
            }
            const locked = !m.userDisablable;
            const clears = m.require.length <= 1;
            const others = m.require.filter((r) => r !== name);
            const removeBlocked = locked || (!clears && !isPro);   // clearing the last mandate is free; anything else is Pro
            const modeBlocked = locked || !isPro;
            return (
              <div key={op} className="flex items-center gap-2 flex-wrap rounded-md px-2 py-1.5" style={{ background: 'var(--sem-surface)', border: '1px solid var(--sem-border)' }}>
                <span className="text-[12px] font-medium" style={{ color: 'var(--sem-fg)' }}>{opLabel(m.op)}</span>
                <button type="button" disabled={modeBlocked}
                  title={locked ? LOCKED_BIND_REASON : !isPro ? PRO_BIND_REASON
                    : m.mode === 'warn' ? 'Warn: the action on its own is allowed but flagged for the record. Click to switch to blocking it.'
                    : 'Hard: the action on its own is blocked and routed into this workflow. Click to switch to a flagged warning instead.'}
                  onClick={() => onSetBinding(m.op, m.require, m.mode === 'hard' ? 'warn' : 'hard')}
                  className="text-[9px] uppercase tracking-wide font-semibold px-1.5 py-0.5 rounded-full disabled:cursor-default"
                  style={m.mode === 'hard'
                    ? { color: 'var(--sem-accent)', background: 'var(--sem-accent-soft)', border: '1px solid color-mix(in srgb, var(--sem-accent) 40%, transparent)' }
                    : { color: 'var(--sem-warn, #d7a54a)', background: 'color-mix(in srgb, var(--sem-warn, #d7a54a) 14%, transparent)', border: '1px solid color-mix(in srgb, var(--sem-warn, #d7a54a) 40%, transparent)' }}>
                  {uiLabel(m.mode)}
                </button>
                {locked && (
                  <span title={LOCKED_BIND_REASON} className="text-[9px] uppercase tracking-wide font-semibold px-1.5 py-0.5 rounded-full" style={{ color: 'var(--sem-muted)', background: 'var(--sem-surface-2)', border: '1px solid var(--sem-border)' }}>locked</span>
                )}
                {others.length > 0 && (
                  <span className="text-[10px]" style={{ color: 'var(--sem-muted)' }}>also: {others.map(titleOf).join(', ')}</span>
                )}
                <div className="flex-1" />
                <button type="button" disabled={removeBlocked}
                  aria-label={clears ? `Remove requirement: ${opLabel(m.op)}` : `Stop requiring ${opLabel(m.op)} for this workflow`}
                  title={locked ? LOCKED_BIND_REASON
                    : removeBlocked ? PRO_BIND_REASON
                    : clears ? 'Remove this requirement (clears the binding; free).'
                    : `Stop requiring this workflow for ${opLabel(m.op)} (keeps ${others.length} other).`}
                  onClick={() => removeOp(m)}
                  className="text-[13px] leading-none px-1.5 py-0.5 rounded-md disabled:opacity-40"
                  style={{ color: 'var(--sem-muted)', background: 'var(--sem-surface-2)', border: '1px solid var(--sem-border)' }}>✕</button>
              </div>
            );
          })}
        </div>
      )}

      {!isPro && (
        <div className="text-[10px] mt-2 flex items-center gap-1.5" style={{ color: 'var(--sem-muted)' }}>
          <span className="uppercase tracking-wide font-semibold px-1.5 py-0.5 rounded-full" style={{ color: 'var(--sem-accent)', background: 'var(--sem-accent-soft)', border: '1px solid color-mix(in srgb, var(--sem-accent) 40%, transparent)' }}>Pro</span>
          Requiring an action (or changing its mode) is Pro. Reading the rules, and clearing the last requirement, are free.
        </div>
      )}

      {lints.length > 0 && (
        <div className="flex flex-col gap-1 mt-2">
          {lints.map((l, i) => (
            <div key={i} className="text-[10.5px] rounded-md px-2 py-1"
              style={l.severity === 'warn'
                ? { color: 'var(--sem-fg)', background: 'color-mix(in srgb, var(--sem-warn, #d7a54a) 12%, transparent)', border: '1px solid color-mix(in srgb, var(--sem-warn, #d7a54a) 35%, transparent)' }
                : { color: 'var(--sem-muted)', background: 'var(--sem-surface)', border: '1px solid var(--sem-border)' }}>
              <span aria-hidden>! </span>{l.message}
            </div>
          ))}
        </div>
      )}

      {err && (
        <div className="text-[11.5px] mt-2 rounded-md px-2.5 py-1.5" style={{ color: 'var(--sem-fg)', background: 'color-mix(in srgb, var(--sem-warn, #d7a54a) 12%, transparent)', border: '1px solid color-mix(in srgb, var(--sem-warn, #d7a54a) 35%, transparent)' }}>
          {err}
        </div>
      )}
    </div>
  );
}

// The "+ Require an action" picker — a small inline popover of the bindable actions not already required. Pro to use
// (the write is gated); when free it shows disabled-with-reason (the same gated affordance the Start button uses).
function AddActionMenu({ addable, isPro, onAdd }: { addable: { op: string; label: string }[]; isPro: boolean; onAdd: (op: string) => void }) {
  const [open, setOpen] = useState(false);
  const none = addable.length === 0;
  const disabled = !isPro || none;
  return (
    <div className="relative">
      <button type="button" disabled={disabled}
        title={none ? 'No more actions can be required here (the rest already route through this workflow or are locked by committed team policy).' : !isPro ? PRO_BIND_REASON : 'Require an action to route through this workflow'}
        onClick={() => setOpen((o) => !o)}
        className="text-[11px] px-2 py-0.5 rounded-md font-semibold disabled:opacity-45"
        style={{ background: 'var(--sem-accent-soft)', color: 'var(--sem-accent)', border: '1px solid color-mix(in srgb, var(--sem-accent) 40%, transparent)' }}>
        + Require an action{!isPro ? ' · Pro' : ''}
      </button>
      {open && !disabled && (
        <div className="absolute right-0 z-20 mt-1 rounded-lg py-1 shadow-lg" style={{ background: 'var(--sem-surface)', border: '1px solid var(--sem-border)', minWidth: 190 }}>
          {addable.map((b) => (
            <button key={b.op} type="button" onClick={() => { onAdd(b.op); setOpen(false); }}
              className="w-full text-left text-[12px] px-3 py-1.5 hover:bg-[var(--sem-surface-2)]"
              style={{ color: 'var(--sem-fg)' }}>{b.label}</button>
          ))}
        </div>
      )}
    </div>
  );
}

// ---- §10.6 "Hide when" (dynamic activation) ------------------------------------------------------
// The third availability axis (availability · required · HIDE-WHEN): curate a workflow OFF today's menu with a
// condition over facts the engine knows (date / connection / git / model / session). The control writes ONLY set='off'
// ("hide when C") rules, because that is the single-rule primitive the engine's resolver actually honors: a hide rule
// takes the workflow off the menu exactly WHEN its condition holds and leaves it visible otherwise. (A lone set='on'
// rule does NOT hide-otherwise — the resolver falls through to default-on — so "show only when C" is written here as
// "hide when NOT C".) Reads the CURRENT resolution from the library entry (active / activeReason — a plain reason,
// never the raw predicate) plus the policy lints; writes with set_workflow_activation (dual-drive with the AI-assistant
// door). Hiding is NOT a lock: a hidden workflow is still startable on demand, and one REQUIRED by a binding stays on
// the menu (a hide rule is then overridden). Writing a rule is Pro; reading and CLEARING a rule are free. The grammar
// lives in one place (WorkflowPredicate.cs); the engine validates on write and its refusal message IS the teaching
// content (shown verbatim, never a raw throw).
function HideWhenControl({ info, policy, tier, onSetActivation, err }: {
  info: WorkflowInfo; policy: WorkflowPolicy | null; tier: string;
  onSetActivation: (name: string, when: string, setStr: string) => Promise<void>; err: string | null;
}) {
  const isPro = tier === 'pro';
  const [when, setWhen] = useState('');
  // Whether THIS session wrote a rule for this workflow. A hide rule whose condition doesn't currently hold looks
  // exactly like "no rule" in the resolution (active + null reason) — the engine never surfaces the raw predicate — so
  // without this flag a just-written non-matching rule would strand (no Clear). Reset on workflow change / on clear.
  const [wrote, setWrote] = useState(false);
  useEffect(() => { setWhen(''); setWrote(false); }, [info.name]);

  const entry = policy?.workflows.find((w) => w.name === info.name) ?? null;
  const requiredOps = entry?.requiredForOps ?? [];
  const forced = requiredOps.length > 0;          // a binding force-activates it — a hide rule is overridden
  const off = info.enabled === false;             // the manual kill switch (the availability switch above governs)
  const active = info.active !== false;
  const reason = info.activeReason || null;
  const ruleHiding = !active && !off && !forced;  // a hide rule currently matches → off the menu now

  // The plain-language current-state line — "On the menu" vs "Hidden right now: <reason>", never the predicate.
  const stateLine = forced
    ? { text: `Always on the menu, required for ${requiredOps.map(opLabel).join(', ')}. A hide rule here is overridden.`, tone: 'info' as const }
    : off
      ? { text: 'Turned off with the switch above. Turn it back on to use a hide-when rule.', tone: 'muted' as const }
      : ruleHiding
        ? { text: `Hidden from the menu right now: ${reason}. You can still start it on demand.`, tone: 'muted' as const }
        : reason
          ? { text: `On the menu now: ${reason}.`, tone: 'muted' as const }
          : { text: 'On the menu. No hide-when rule is in effect.', tone: 'muted' as const };

  // Activation-topic lints for THIS workflow (unreadable condition, never-fires term, a rule overridden by a
  // requirement) — the inline validation, phrased in plain language by the engine. These ALSO prove a rule EXISTS even
  // when it isn't currently in effect (a dead / overridden / unreadable rule), so they drive the Clear affordance too.
  const lints = (policy?.lints ?? []).filter((l) => l.message.includes(info.name) && isActivationLint(l.message));
  // Offer Clear whenever a rule plausibly EXISTS: it's hiding now, OR a lint reports one, OR we wrote one this session
  // (covers the non-matching case). Clear is free + idempotent, so a stray offer is harmless, never a stranded rule.
  const showClear = ruleHiding || lints.length > 0 || wrote;

  const insert = (expr: string) => setWhen((cur) => (cur.trim() ? `${cur.trim()} && ${expr}` : expr));
  const applyBlocked = !isPro || !when.trim();
  const apply = async () => { if (applyBlocked) return; await onSetActivation(info.name, when.trim(), 'off'); setWrote(true); };
  const clear = async () => { await onSetActivation(info.name, '', ''); setWrote(false); };   // both empty ⇒ engine removes the rule (free)

  return (
    <div className="mt-3 rounded-lg border p-3" style={{ background: 'var(--sem-surface-2)', borderColor: 'var(--sem-border)' }}>
      <div className="flex items-center gap-2 flex-wrap">
        <span className="text-[10px] uppercase tracking-wide font-semibold" style={{ color: 'var(--sem-muted)' }}>Hide when</span>
        <div className="flex-1" />
        {showClear && (
          <button type="button" onClick={clear}
            title="Remove the hide-when rule for this workflow. It shows on the menu normally again (free)."
            className="text-[11px] px-2 py-0.5 rounded-md font-semibold"
            style={{ background: 'var(--sem-surface)', color: 'var(--sem-muted)', border: '1px solid var(--sem-border)' }}>
            Clear rule
          </button>
        )}
      </div>
      <div className="text-[10.5px] mt-1" style={{ color: 'var(--sem-muted)' }}>
        Drop a workflow off the menu while a condition holds, e.g. hide an experimental workflow on the production workspace, or hide a month-end checklist outside its window (so it only shows near month-end). It stays startable on demand; this just curates what's on offer.
      </div>

      {/* current state, in plain words (never the predicate) */}
      <div className="text-[11.5px] mt-2 rounded-md px-2.5 py-1.5"
        style={stateLine.tone === 'info'
          ? { color: 'var(--sem-fg)', background: 'var(--sem-accent-soft)', border: '1px solid color-mix(in srgb, var(--sem-accent) 34%, transparent)' }
          : { color: 'var(--sem-muted)', background: 'var(--sem-surface)', border: '1px solid var(--sem-border)' }}>
        {stateLine.text}
      </div>

      {/* the editor: the condition + Apply. Writing is Pro; the field is editable so the gated affordance reads
          (disabled Apply with the reason), mirroring the "Required for" control. */}
      <div className="mt-2.5 flex items-center gap-1.5 flex-wrap">
        <span className="text-[11px] font-semibold shrink-0" style={{ color: 'var(--sem-muted)' }}>Hide it when</span>
        <input value={when} onChange={(e) => setWhen(e.target.value)} spellCheck={false}
          placeholder="a condition, e.g. connection.workspace ~ '*prod*'"
          onKeyDown={(e) => { if (e.key === 'Enter') void apply(); }}
          className="flex-1 min-w-[180px] text-[11.5px] px-2 py-1 rounded-md outline-none"
          style={{ background: 'var(--sem-surface)', color: 'var(--sem-fg)', border: '1px solid var(--sem-border)', fontFamily: 'ui-monospace,SFMono-Regular,Consolas,monospace' }} />
        <button type="button" disabled={applyBlocked}
          title={!isPro ? PRO_ACT_REASON : !when.trim() ? 'Type a condition first. A hide-when rule needs one; use an example below to start.' : 'Save this rule. It takes effect on both doors immediately.'}
          onClick={() => void apply()}
          className="text-[11px] px-2 py-1 rounded-md font-semibold disabled:opacity-45"
          style={{ background: 'var(--sem-accent-soft)', color: 'var(--sem-accent)', border: '1px solid color-mix(in srgb, var(--sem-accent) 40%, transparent)' }}>
          Apply rule{!isPro ? ' · Pro' : ''}
        </button>
      </div>

      {/* example conditions the analyst can drop in (appended with && so compound conditions build up) */}
      <div className="flex items-center gap-1 mt-2 flex-wrap">
        <span className="text-[10px]" style={{ color: 'var(--sem-muted)' }}>Hide when:</span>
        {ACTIVATION_EXAMPLES.map((ex) => (
          <button key={ex.expr} type="button" onClick={() => insert(ex.expr)} title={ex.expr}
            className="text-[10px] px-1.5 py-0.5 rounded-full"
            style={{ background: 'var(--sem-surface)', color: 'var(--sem-fg)', border: '1px solid var(--sem-border)' }}>
            {ex.label}
          </button>
        ))}
      </div>

      {!isPro && (
        <div className="text-[10px] mt-2 flex items-center gap-1.5" style={{ color: 'var(--sem-muted)' }}>
          <span className="uppercase tracking-wide font-semibold px-1.5 py-0.5 rounded-full" style={{ color: 'var(--sem-accent)', background: 'var(--sem-accent-soft)', border: '1px solid color-mix(in srgb, var(--sem-accent) 40%, transparent)' }}>Pro</span>
          Setting a hide-when rule is Pro. Reading the menu, and clearing a rule, are free.
        </div>
      )}

      {lints.length > 0 && (
        <div className="flex flex-col gap-1 mt-2">
          {lints.map((l, i) => (
            <div key={i} className="text-[10.5px] rounded-md px-2 py-1"
              style={l.severity === 'warn'
                ? { color: 'var(--sem-fg)', background: 'color-mix(in srgb, var(--sem-warn, #d7a54a) 12%, transparent)', border: '1px solid color-mix(in srgb, var(--sem-warn, #d7a54a) 35%, transparent)' }
                : { color: 'var(--sem-muted)', background: 'var(--sem-surface)', border: '1px solid var(--sem-border)' }}>
              <span aria-hidden>! </span>{l.message}
            </div>
          ))}
        </div>
      )}

      {err && (
        <div className="text-[11.5px] mt-2 rounded-md px-2.5 py-1.5" style={{ color: 'var(--sem-fg)', background: 'color-mix(in srgb, var(--sem-warn, #d7a54a) 12%, transparent)', border: '1px solid color-mix(in srgb, var(--sem-warn, #d7a54a) 35%, transparent)' }}>
          {err}
        </div>
      )}
    </div>
  );
}

// ---- overview rail (no run) ----------------------------------------------------------------------
// The pre-run view: every step on a vertical rail so actions · sequence · instructions · constraints are
// legible at a glance. Instructions collapse into an expander (the full verbatim body is one click away).
function OverviewRail({ def }: { def: WorkflowDef }) {
  return (
    <Panel>
      <SectionTitle>Steps <span style={{ color: 'var(--sem-muted)' }}>({def.steps.length})</span></SectionTitle>
      <div className="relative mt-2">
        <RailLine />
        <div className="flex flex-col">
          {def.steps.map((s) => <OverviewStep key={s.id} step={s} />)}
        </div>
      </div>
    </Panel>
  );
}

function OverviewStep({ step }: { step: WorkflowStep }) {
  const [open, setOpen] = useState(false);
  const g = step.gate;
  const gateBits = g
    ? [g.inputs.length ? `${g.inputs.length} question${g.inputs.length === 1 ? '' : 's'}` : null,
       g.verify.length ? `Checks: ${g.verify.map((v) => uiLabel(v.kind)).join(', ')}` : null,
       `strictness ${g.strictness || 'inherit'}`].filter(Boolean)
    : [];
  return (
    <div className="relative flex items-start gap-3 py-2.5">
      <NumberNode n={step.number} />
      <div className="min-w-0 flex-1">
        <div className="flex items-center gap-2 flex-wrap">
          <span className="text-[12.5px] font-semibold">{step.title}</span>
          {g && g.inputs.length + g.verify.length > 0 && (
            <span className="text-[9px] uppercase tracking-wide font-semibold px-1.5 py-0.5 rounded-full" style={{ color: 'var(--sem-accent)', background: 'var(--sem-accent-soft)' }}>gate</span>
          )}
        </div>
        {step.ops.length > 0 && (
          <div className="flex items-center gap-1 mt-1.5 flex-wrap">
            {step.ops.map((op) => <OpChip key={op} op={op} />)}
          </div>
        )}
        {gateBits.length > 0 && (
          <div className="text-[11px] mt-1.5" style={{ color: 'var(--sem-muted)' }}>{gateBits.join(' · ')}</div>
        )}
        <button onClick={() => setOpen((o) => !o)}
          className="mt-1.5 flex items-center gap-1 text-[11px] font-medium" style={{ color: 'var(--sem-muted)' }}>
          <span className="inline-block transition-transform text-[9px]" style={{ transform: open ? 'rotate(90deg)' : 'none' }}>▶</span>
          {open ? 'Hide instructions' : 'Instructions'}
        </button>
        {open && <Markdown md={step.instructions} className="mt-1.5" />}
      </div>
    </div>
  );
}

// ---- run rail (a run is active or terminal) ------------------------------------------------------
function RunRail({ run, onSubmit, onSkip, onAbort }: {
  run: WorkflowRunView; onSubmit: (stepId: string, answersJson: string) => Promise<void>;
  onSkip: (stepId: string, reason: string) => Promise<void>; onAbort: (reason: string) => Promise<void>;
}) {
  return (
    <Panel>
      <SectionTitle>Run <span style={{ color: 'var(--sem-muted)' }}>· {run.steps.filter((s) => s.status === 'passed').length}/{run.totalSteps} passed</span></SectionTitle>
      <div className="relative mt-2">
        <RailLine />
        <div className="flex flex-col">
          {run.steps.map((s, i) => (
            <RunStep key={s.stepId} n={i + 1} result={s}
              current={run.status === 'active' && run.currentStep?.stepId === s.stepId ? run.currentStep : null}
              onSubmit={onSubmit} onSkip={onSkip} onAbort={onAbort} />
          ))}
        </div>
      </div>
    </Panel>
  );
}

function RunStep({ n, result, current, onSubmit, onSkip, onAbort }: {
  n: number; result: StepResult; current: CurrentStepView | null;
  onSubmit: (stepId: string, answersJson: string) => Promise<void>;
  onSkip: (stepId: string, reason: string) => Promise<void>; onAbort: (reason: string) => Promise<void>;
}) {
  const st = stepStyle(result.status);
  const done = result.status === 'passed' || result.status === 'skipped' || result.status === 'failed';
  // Auto-open the record when it carries something worth seeing — a decline, a failed/skipped verify, or a note —
  // so the notable outcomes surface without a click; a clean passed step stays collapsed (calm by default).
  const notable = !!result.note
    || Object.values(result.answers).some((a) => a.declined)
    || result.verifyResults.some((v) => v.status !== 'passed');
  const [open, setOpen] = useState(notable);
  return (
    <div className="relative flex items-start gap-3 py-2.5">
      <StatusNode status={result.status} />
      <div className="min-w-0 flex-1">
        <div className="flex items-center gap-2 flex-wrap">
          <span className="text-[10px] tnum" style={{ color: 'var(--sem-muted)' }}>{n}</span>
          <span className="text-[12.5px] font-semibold" style={{ color: current ? 'var(--sem-fg)' : done ? 'var(--sem-fg)' : 'var(--sem-muted)' }}>{result.title}</span>
          <span className="text-[9px] uppercase tracking-wide font-semibold px-1.5 py-0.5 rounded-full" style={{ color: st.color, background: `color-mix(in srgb, ${st.color} 16%, transparent)` }}>{st.label}</span>
          {result.effectiveStrictness && done && (
            <span className="text-[9.5px] tnum" style={{ color: 'var(--sem-muted)' }} title="The strictness this gate actually ran at">@ {result.effectiveStrictness}</span>
          )}
        </div>

        {/* completed step: recorded answers/declines + verify evidence, in an expander */}
        {done && (result.note || Object.keys(result.answers).length > 0 || result.verifyResults.length > 0) && (
          <>
            <button onClick={() => setOpen((o) => !o)} className="mt-1 flex items-center gap-1 text-[11px] font-medium" style={{ color: 'var(--sem-muted)' }}>
              <span className="inline-block transition-transform text-[9px]" style={{ transform: open ? 'rotate(90deg)' : 'none' }}>▶</span>
              {open ? 'Hide record' : 'Record'}
            </button>
            {open && (
              <div className="mt-1.5 flex flex-col gap-2">
                {result.note && <div className="text-[11px] px-2 py-1 rounded" style={{ background: 'var(--sem-surface-2)', color: 'var(--sem-muted)' }}>{result.note}</div>}
                {Object.entries(result.answers).map(([name, a]) => <AnswerRow key={name} name={name} a={a} />)}
                {result.verifyResults.length > 0 && (
                  <div className="flex flex-wrap gap-1.5">
                    {result.verifyResults.map((v, i) => <VerifyChip key={v.kind + i} v={v} />)}
                  </div>
                )}
              </div>
            )}
          </>
        )}

        {/* the CURRENT step — full instructions + gate panel + verify checklist + actions */}
        {current && (
          <CurrentStepCard current={current}
            onSubmit={(json) => onSubmit(current.stepId, json)}
            onSkip={(reason) => onSkip(current.stepId, reason)}
            onAbort={onAbort} />
        )}
      </div>
    </div>
  );
}

function CurrentStepCard({ current, onSubmit, onSkip, onAbort }: {
  current: CurrentStepView; onSubmit: (answersJson: string) => Promise<void>;
  onSkip: (reason: string) => Promise<void>; onAbort: (reason: string) => Promise<void>;
}) {
  // Per-input local state: a value, or a decline with a reason. Building the answers payload from this map.
  const [vals, setVals] = useState<Record<string, string>>({});
  const [declined, setDeclined] = useState<Record<string, boolean>>({});
  const [reasons, setReasons] = useState<Record<string, string>>({});
  const [err, setErr] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const declineIncomplete = current.questions.some((q) => declined[q.name] && !(reasons[q.name] || '').trim());

  const doSubmit = async () => {
    if (declineIncomplete) { setErr('A declined question needs a reason before you can submit.'); return; }
    setBusy(true); setErr(null);
    try {
      const payload: Record<string, unknown> = {};
      for (const q of current.questions) {
        if (declined[q.name]) payload[q.name] = { declined: true, reason: (reasons[q.name] || '').trim() };
        else if ((vals[q.name] || '').trim() !== '') payload[q.name] = vals[q.name];
      }
      await onSubmit(JSON.stringify(payload));
    } catch (e) {
      // A thrown gate rejection is the instructive content — show it verbatim, never swallow it. The run stays put.
      setErr(String((e as Error).message ?? e));
    } finally { setBusy(false); }
  };

  return (
    <div className="mt-2 rounded-lg border p-3" style={{ background: 'var(--sem-surface-2)', borderColor: 'color-mix(in srgb, var(--sem-accent) 40%, transparent)' }}>
      <Markdown md={current.instructions} />

      {current.ops.length > 0 && (
        <div className="flex items-center gap-1 mt-2.5 flex-wrap">
          <span className="text-[10px] uppercase tracking-wide font-semibold mr-1" style={{ color: 'var(--sem-muted)' }}>actions</span>
          {current.ops.map((op) => <OpChip key={op} op={op} />)}
        </div>
      )}

      {current.questions.length > 0 && (
        <div className="mt-3 flex flex-col gap-3">
          <div className="text-[10px] uppercase tracking-wide font-semibold" style={{ color: 'var(--sem-muted)' }}>Gate</div>
          {current.questions.map((q) => (
            <GateField key={q.name} q={q}
              value={vals[q.name] ?? ''} declined={!!declined[q.name]} reason={reasons[q.name] ?? ''}
              onValue={(v) => setVals((m) => ({ ...m, [q.name]: v }))}
              onDecline={(d) => setDeclined((m) => ({ ...m, [q.name]: d }))}
              onReason={(r) => setReasons((m) => ({ ...m, [q.name]: r }))} />
          ))}
        </div>
      )}

      {current.verifyKinds.length > 0 && (
        <div className="mt-3">
          <div className="text-[10px] uppercase tracking-wide font-semibold mb-1" style={{ color: 'var(--sem-muted)' }}>Engine will verify</div>
          <div className="flex flex-wrap gap-1.5">
            {current.verifyKinds.map((k) => (
              <span key={k} className="text-[10px] tnum px-1.5 py-0.5 rounded flex items-center gap-1" style={{ background: 'var(--sem-surface)', color: 'var(--sem-muted)', border: '1px solid var(--sem-border)' }}>
                <span style={{ color: 'var(--sem-muted)' }}>○</span> {k}
              </span>
            ))}
          </div>
        </div>
      )}

      {err && (
        <div className="mt-3 rounded-lg px-3 py-2 text-[12px]" style={{ background: 'color-mix(in srgb, var(--sem-bad) 12%, transparent)', color: 'var(--sem-bad)', border: '1px solid color-mix(in srgb, var(--sem-bad) 34%, transparent)' }}>
          {err}
        </div>
      )}

      <div className="mt-3 flex items-center gap-2">
        <Button primary disabled={busy} onClick={doSubmit}>{busy ? 'Submitting…' : 'Submit step'}</Button>
        <ReasonButton label="Skip" title="Skip this step (a reason is required)" onConfirm={onSkip} />
        <ReasonButton label="Abort" title="Abort the whole run (a reason is required)" onConfirm={onAbort} danger />
      </div>
    </div>
  );
}

function GateField({ q, value, declined, reason, onValue, onDecline, onReason }: {
  q: GateInput; value: string; declined: boolean; reason: string;
  onValue: (v: string) => void; onDecline: (d: boolean) => void; onReason: (r: string) => void;
}) {
  const required = q.required === 'required';
  return (
    <div className="rounded-lg p-2.5" style={{ background: 'var(--sem-surface)', border: '1px solid var(--sem-border)' }}>
      <div className="flex items-start gap-2">
        <label className="text-[12px] flex-1" style={{ color: 'var(--sem-fg)' }}>{q.question || q.name}</label>
        <span className="shrink-0 text-[9px] tnum px-1.5 py-0.5 rounded" style={{ background: 'var(--sem-surface-2)', color: 'var(--sem-muted)' }}>{uiLabel(q.type)}</span>
        {required && <span className="shrink-0 text-[9px] uppercase tracking-wide font-semibold px-1.5 py-0.5 rounded" style={{ color: 'var(--sem-bad)', background: 'color-mix(in srgb, var(--sem-bad) 14%, transparent)' }}>required</span>}
      </div>
      {!declined && (
        <textarea value={value} onChange={(e) => onValue(e.target.value)} rows={q.type === 'text' ? 2 : 1} spellCheck={false}
          placeholder={q.name}
          className="mt-1.5 w-full text-[12px] px-2 py-1 rounded-md outline-none resize-y"
          style={{ background: 'var(--sem-surface-2)', color: 'var(--sem-fg)', border: '1px solid var(--sem-border)' }} />
      )}
      <div className="mt-1.5 flex items-center gap-2">
        <label className="flex items-center gap-1.5 text-[11px] cursor-pointer" style={{ color: 'var(--sem-muted)' }}>
          <input type="checkbox" checked={declined} onChange={(e) => onDecline(e.target.checked)} />
          Decline…
        </label>
        {declined && !required && <span className="text-[10px]" style={{ color: 'var(--sem-muted)' }}>Recorded as an explicit decline (auditable).</span>}
      </div>
      {declined && (
        <textarea value={reason} onChange={(e) => onReason(e.target.value)} rows={2} spellCheck={false}
          placeholder="Why are you declining this question? (required)"
          className="mt-1.5 w-full text-[12px] px-2 py-1 rounded-md outline-none resize-y"
          style={{ background: 'var(--sem-surface-2)', color: 'var(--sem-fg)', border: '1px solid color-mix(in srgb, var(--sem-warn) 45%, transparent)' }} />
      )}
    </div>
  );
}

function AnswerRow({ name, a }: { name: string; a: AnswerValue }) {
  if (isDeclined(a)) {
    return (
      <div className="text-[11px] px-2 py-1.5 rounded" style={{ background: 'color-mix(in srgb, var(--sem-warn) 12%, transparent)', border: '1px solid color-mix(in srgb, var(--sem-warn) 30%, transparent)' }}>
        <span className="font-semibold" style={{ color: 'var(--sem-warn)' }}>{name} · declined</span>
        {a.declineReason && <span style={{ color: 'var(--sem-fg)' }}>: {a.declineReason}</span>}
      </div>
    );
  }
  return (
    <div className="text-[11px] px-2 py-1.5 rounded" style={{ background: 'var(--sem-surface-2)' }}>
      <span className="font-semibold" style={{ color: 'var(--sem-fg)' }}>{name}</span>
      <span style={{ color: 'var(--sem-muted)' }}> · {isAnswered(a) ? a.value : '(no value)'}</span>
    </div>
  );
}

function VerifyChip({ v }: { v: VerifyResult }) {
  const color = verifyColor(v.status);
  const glyph = v.status === 'passed' ? '✓' : v.status === 'failed' ? '✕' : '⤼';
  return (
    <span title={v.detail} className="text-[10px] tnum px-1.5 py-0.5 rounded flex items-center gap-1"
      style={{ color, background: `color-mix(in srgb, ${color} 14%, transparent)`, border: `1px solid color-mix(in srgb, ${color} 34%, transparent)` }}>
      <span>{glyph}</span> {uiLabel(v.kind)}
    </span>
  );
}


// ---- small primitives ----------------------------------------------------------------------------
export function RailLine() {
  return <div className="absolute top-1 bottom-1 w-px" style={{ left: 15, background: 'var(--sem-border)' }} />;
}
export function NumberNode({ n }: { n: number }) {
  return (
    <div className="relative z-10 shrink-0 w-8 h-8 rounded-full flex items-center justify-center text-[11px] font-bold tnum"
      style={{ background: 'var(--sem-surface-2)', color: 'var(--sem-fg)', border: '1px solid var(--sem-border)' }}>{n}</div>
  );
}
function StatusNode({ status }: { status: string }) {
  const st = stepStyle(status);
  const filled = status === 'passed' || status === 'in_progress';
  return (
    <div className="relative z-10 shrink-0 w-8 h-8 rounded-full flex items-center justify-center text-[13px] font-bold"
      title={st.label}
      style={filled
        ? { background: st.color, color: '#fff' }
        : { background: 'var(--sem-surface-2)', color: st.color, border: `2px solid color-mix(in srgb, ${st.color} 60%, transparent)` }}>
      {st.glyph}
    </div>
  );
}
export function OpChip({ op }: { op: string }) {
  const label = op === 'export_workflow_evidence' ? 'Evidence report' : uiLabel(op);
  return (
    <span className="text-[10px] tnum px-1.5 py-0.5 rounded flex items-center gap-1" style={{ background: 'var(--sem-surface-2)', color: 'var(--sem-fg)', border: '1px solid var(--sem-border)' }}>
      <span style={{ color: 'var(--sem-accent)' }}>▸</span> {label}
    </span>
  );
}
export function Pill({ children, tint, title }: { children: React.ReactNode; tint?: string; title?: string }) {
  return (
    <span title={title} className="text-[9.5px] uppercase tracking-wide font-semibold px-1.5 py-0.5 rounded"
      style={tint
        ? { color: tint, background: `color-mix(in srgb, ${tint} 14%, transparent)` }
        : { color: 'var(--sem-muted)', background: 'var(--sem-surface)', border: '1px solid var(--sem-border)' }}>
      {children}
    </span>
  );
}
function Markdown({ md, className = '' }: { md: string; className?: string }) {
  // Reuse the pure, escape-first Markdown→HTML renderer (docrender.mdToHtml): paragraphs, `code`, **bold**, lists.
  // The instruction body is verbatim (never summarised) — that's the instruction-returning-tool contract.
  return <div className={`wf-md text-[12px] ${className}`} style={{ color: 'var(--sem-fg)' }} dangerouslySetInnerHTML={{ __html: mdToHtml(md || '') }} />;
}

function ModeTab({ active, onClick, children }: { active: boolean; onClick: () => void; children: React.ReactNode }) {
  return (
    <button onClick={onClick} className="text-[11px] px-2.5 py-1 font-semibold transition-colors"
      style={active ? { background: 'var(--sem-accent)', color: 'var(--sem-on-accent)' } : { background: 'var(--sem-surface-2)', color: 'var(--sem-muted)' }}>
      {children}
    </button>
  );
}

// A button that reveals an inline reason field (skip / abort both require a reason) — no modal, stays in flow.
function ReasonButton({ label, title, onConfirm, danger }: { label: string; title?: string; onConfirm: (reason: string) => void | Promise<void>; danger?: boolean }) {
  const [open, setOpen] = useState(false);
  const [reason, setReason] = useState('');
  const [busy, setBusy] = useState(false);
  const color = danger ? 'var(--sem-bad)' : 'var(--sem-fg)';
  if (!open) {
    return (
      <button onClick={() => setOpen(true)} title={title}
        className="text-[12px] px-3 py-1.5 rounded-lg font-medium whitespace-nowrap"
        style={{ background: 'var(--sem-surface-2)', color, border: '1px solid var(--sem-border)' }}>{label}</button>
    );
  }
  const confirm = async () => {
    if (!reason.trim()) return;
    setBusy(true);
    try { await onConfirm(reason.trim()); setOpen(false); setReason(''); }
    finally { setBusy(false); }
  };
  return (
    <span className="flex items-center gap-1.5">
      <input value={reason} onChange={(e) => setReason(e.target.value)} autoFocus placeholder={`${label} reason…`} spellCheck={false}
        className="text-[12px] px-2 py-1 rounded-md outline-none w-52" style={{ background: 'var(--sem-surface-2)', color: 'var(--sem-fg)', border: `1px solid color-mix(in srgb, ${color} 45%, transparent)` }} />
      <button onClick={confirm} disabled={busy || !reason.trim()} className="text-[11px] px-2 py-1 rounded-md font-medium disabled:opacity-40" style={{ background: color === 'var(--sem-bad)' ? 'var(--sem-bad)' : 'var(--sem-accent)', color: 'var(--sem-on-accent)' }}>{busy ? '…' : label}</button>
      <button onClick={() => { setOpen(false); setReason(''); }} className="text-[11px] px-2 py-1 rounded-md" style={{ color: 'var(--sem-muted)' }}>Cancel</button>
    </span>
  );
}

function ErrorPanel({ name, error }: { name: string; error: string }) {
  return (
    <Panel>
      <div className="text-[13px] font-semibold" style={{ color: 'var(--sem-bad)' }}>This workflow file does not parse</div>
      <div className="text-[12px] mt-1" style={{ color: 'var(--sem-muted)' }}>
        <span className="tnum">{name}</span> is still listed here rather than hidden, but it can't run until the error below is fixed.
      </div>
      <pre className="mt-2 rounded-lg px-3 py-2 text-[12px] whitespace-pre-wrap" style={{ background: 'color-mix(in srgb, var(--sem-bad) 10%, transparent)', color: 'var(--sem-bad)', border: '1px solid color-mix(in srgb, var(--sem-bad) 30%, transparent)', font: '12px/1.5 ui-monospace,SFMono-Regular,Consolas,monospace' }}>{error}</pre>
    </Panel>
  );
}

function EmptyMain() {
  return (
    <div className="h-full flex items-center justify-center p-8">
      <div className="text-center max-w-sm">
        <div className="text-[15px] font-semibold mb-1">Select a workflow</div>
        <div className="text-[12px]" style={{ color: 'var(--sem-muted)' }}>Pick a playbook from the left to see its steps, actions, instructions and gates, then start a verified run.</div>
      </div>
    </div>
  );
}

export function Panel({ children }: { children: React.ReactNode }) {
  return <div className="rounded-xl border p-4" style={{ background: 'var(--sem-surface)', borderColor: 'var(--sem-border)' }}>{children}</div>;
}
export function SectionTitle({ children }: { children: React.ReactNode }) {
  return <div className="text-[11px] uppercase tracking-wide font-semibold" style={{ color: 'var(--sem-muted)' }}>{children}</div>;
}
export function Button({ children, onClick, primary, disabled, title }: { children: React.ReactNode; onClick?: () => void; primary?: boolean; disabled?: boolean; title?: string }) {
  return (
    <button onClick={onClick} disabled={disabled} title={title}
      className="text-[12px] px-3 py-1.5 rounded-lg font-medium transition-opacity disabled:opacity-40 whitespace-nowrap"
      style={primary ? { background: 'var(--sem-accent)', color: 'var(--sem-on-accent)' } : { background: 'var(--sem-surface-2)', color: 'var(--sem-fg)', border: '1px solid var(--sem-border)' }}>
      {children}
    </button>
  );
}
export function Banner({ children, color }: { children: React.ReactNode; color: string }) {
  return <div className="rounded-lg px-3 py-2 text-[12px]" style={{ background: `color-mix(in srgb, ${color} 14%, transparent)`, color, border: `1px solid color-mix(in srgb, ${color} 40%, transparent)` }}>{children}</div>;
}
