import { useEffect, useMemo, useRef, useState } from 'react';
import { rpc, onWorkflowLibraryChange } from './bridge';
import { mdToHtml } from './docrender';
import { DesignMode } from './workflowdesign';
import { ScenariosPanel } from './workflowscenarios';
import { EvidenceArtifactDialog, type EvidenceArtifactW, type EvidenceSaveResultW } from './artifactdialog';
import { uiLabel } from './copy';
import {
  liveRuns, mostRecentLive, mostRecentTerminal, runForWorkflow, focusedRun,
  stepGateSkipped, type RunMapState,
} from './workflowruns.mjs';

// ===================================================================================================
// Workflows — a workflow is "a skill with teeth": a named, versioned playbook (markdown instructions per
// step) whose steps chain MCP primitive actions and carry engine-enforced gates. The tab is a stable SECTION
// workbench (Home · Library · Runs · Governance · Author) with an AMBIENT live-run banner, so a long AI
// Assistant run never takes the tab over: the human can still read a playbook or check policy while the agent
// drives a run on the SAME session (dual-drive on one run; both doors watch workflow/didChange). The four
// legible halves stay visible on a playbook and inside a run — actions (op chips), sequence (the numbered
// rail), instructions (verbatim), and constraints (the gate). Design of this rework: the ratified wireframe.
// ===================================================================================================

// The honest message for a run that has aged out of the engine's bounded store (MaxHeld) — surfaced whether the
// engine RESOLVES the miss as { note } or REJECTS it, so a raw "run 'wfr-N' not found" never reaches the analyst.
const RUN_AGED_OUT = 'This run is no longer available for an evidence report. Only the most recent runs are kept, and this one has aged out. Start the workflow again to produce a fresh run you can seal.';

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
export interface AnswerValue { value?: string | null; declined?: boolean; declineReason?: string | null; answered?: boolean }
// status: passed | failed | unavailable (blocked, Missing names why) | not_applicable (when: not met) | skipped (legacy advisory)
export interface VerifyResult {
  kind: string; status: string; detail?: string;
  missing?: string | null;                                       // unavailable: the one thing that was absent
  shapes?: { shapeId: string; pinned: boolean; rowsCompared: number; mismatchCount: number; truncated?: boolean }[] | null;
  mismatchCells?: { context: string; valueA?: string; valueB?: string }[] | null;
}
export interface WitnessLockView { probe: string; hash: string }
export interface WitnessRevision { probe: string; beforeHash: string; afterHash: string; stepId: string; timestampUtc: string }
export interface PartitionRevision { key: string; before: string; after: string; stepId: string; timestampUtc: string }
export interface StepResult {
  stepId: string; title: string; status: string; note?: string | null;
  answers: Record<string, AnswerValue>; verifyResults: VerifyResult[]; effectiveStrictness?: string | null;
}
export interface CurrentStepView {
  stepId: string; title: string; instructions: string; questions: GateInput[];
  verifyKinds: string[]; effectiveStrictness?: string | null; ops: string[];
}
export interface WorkflowRunView {
  runId: string; workflow: string; title: string; workflowVersion: number; status: string; abortReason?: string | null;
  startedUtc?: string | null; finishedUtc?: string | null; modelName?: string | null; modelFingerprint?: string | null;
  stepIndex: number; totalSteps: number; steps: StepResult[]; currentStep?: CurrentStepView | null;
  witnessLocks?: WitnessLockView[] | null; witnessRevisions?: WitnessRevision[] | null; partitionRevisions?: PartitionRevision[] | null;
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
// The engine-owned policy bundles (list_workflow_profiles). Applying one is atomic (activate_workflow_profile).
interface WorkflowProfileInfo {
  name: string; title: string; description: string; effects: string[]; pro: boolean; selected: boolean;
}
interface WorkflowProfileResult { activeProfile: string; workflows: WorkflowInfo[]; note?: string | null }

// The v1 bindable authoring chokepoints (the EnforceBindingAsync call sites in LocalEngine.cs) — the ONLY ops a
// binding can route, offered in a fixed order. Labelled in plain language (the "UI never speaks engine" rule):
// the analyst picks "New measure", never the raw op id. An unwired op can't be bound, so only these appear.
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

// Verify-evidence chip tint. passed = good, failed = bad, unavailable = AMBER (blocked: applicable but no
// authoritative evidence — never conflated with the muted, legitimately-not-run styles), not_applicable/skipped = muted.
const VERIFY_COLOR: Record<string, string> = {
  passed: 'var(--sem-good)', failed: 'var(--sem-bad)',
  unavailable: 'var(--sem-warn, #d7a54a)',
  not_applicable: 'var(--sem-muted)', skipped: 'var(--sem-muted)',
};
const verifyColor = (s: string) => VERIFY_COLOR[s] ?? 'var(--sem-muted)';

const isDeclined = (a: AnswerValue) => a.declined === true;
const isAnswered = (a: AnswerValue) => !a.declined && a.value != null && a.value !== '';

// A run belongs to a section only while it is worth showing. Terminal keeps its evidence reachable.
const isTerminal = (r: WorkflowRunView | null) => !!r && r.status !== 'active';

type Section = 'home' | 'library' | 'runs' | 'governance' | 'author';

// ===================================================================================================
// The tab shell — a stable section sidebar + a main pane that changes per section. The ambient live-run banner
// rides across the top of every section (never a takeover). All engine wiring is shared: one library, one run,
// one policy read, all live off the same broadcasts, so an agent-driven change surfaces in Studio immediately.
// ===================================================================================================
export function WorkflowsView({ navTarget, runs, onRunUpdate, markOwnRun, isOwnRun }: {
  navTarget?: { name: string; nonce: number } | null;
  // Runs live at the SHELL (App.tsx) so an agent-driven run isn't missed while this tab is unmounted. We read the
  // shared run-map and fold our own transitions back into it; ownership is tracked by the shell too.
  runs: RunMapState; onRunUpdate: (r: WorkflowRunView) => void;
  markOwnRun: (runId: string) => void; isOwnRun: (runId: string) => boolean;
}) {
  const [section, setSection] = useState<Section>('home');
  const [library, setLibrary] = useState<WorkflowInfo[] | null>(null);
  const [selected, setSelected] = useState<string | null>(null);   // the workflow a playbook page / author pane is showing
  const [openName, setOpenName] = useState<string | null>(null);    // library: a playbook page is open when set
  const [creating, setCreating] = useState(false);                  // author: a brand-new playbook
  const [guided, setGuided] = useState(false);                      // home: the ready-made-workflow setup overlay
  const [libraryFilter, setLibraryFilter] = useState('');
  const [def, setDef] = useState<WorkflowDef | null>(null);
  const [focusedRunId, setFocusedRunId] = useState<string | null>(null);  // Runs: which live run has focus (multi-run)
  const [tier, setTier] = useState<string>('free');
  const [libErr, setLibErr] = useState<string | null>(null);
  const [startErr, setStartErr] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [evidenceRun, setEvidenceRun] = useState<WorkflowRunView | null>(null);
  const [enforcement, setEnforcement] = useState<WorkflowEnforcement | null>(null);
  const [policy, setPolicy] = useState<WorkflowPolicy | null>(null);
  const [profiles, setProfiles] = useState<WorkflowProfileInfo[] | null>(null);
  // Deep-link from a playbook's "Edit rules" to THAT workflow's Governance row (expand + scroll), never just the
  // section. A nonce so repeat clicks on the same row re-fire the scroll.
  const [govTarget, setGovTarget] = useState<{ name: string; nonce: number } | null>(null);
  const govNonce = useRef(0);

  const refreshPolicy = () => rpc<WorkflowPolicy>('getWorkflowPolicy').then(setPolicy).catch(() => undefined);
  const refreshProfiles = () => rpc<WorkflowProfileInfo[]>('listWorkflowProfiles').then(setProfiles).catch(() => setProfiles([]));
  const refreshEnforcement = () => rpc<WorkflowEnforcement>('getWorkflowEnforcement').then(setEnforcement).catch(() => undefined);

  // Load the library + entitlement + policy; keep everything live (a save_workflow / set_*_binding / profile
  // change on EITHER door re-broadcasts the library, and we refetch the policy + enforcement + profiles alongside).
  // The RUN stream is owned by the shell (App.tsx) so it survives leaving this tab — we only read `runs` here.
  useEffect(() => {
    rpc<WorkflowInfo[]>('listWorkflows').then((l) => { setLibrary(l); setLibErr(null); }).catch((e) => setLibErr(String((e as Error).message ?? e)));
    rpc<{ tier?: string }>('getEntitlement').then((e) => setTier(e?.tier ?? 'free')).catch(() => undefined);
    refreshEnforcement(); refreshPolicy(); refreshProfiles();
    const offLib = onWorkflowLibraryChange((v) => {
      setLibrary(v as WorkflowInfo[]);
      refreshEnforcement(); refreshPolicy(); refreshProfiles();
    });
    return () => { offLib(); };
  }, []);

  // A feature surface can hand off to the exact playbook that completes the job (lineage → workflow). Land on
  // its runnable playbook page and reset transient authoring/overlay state.
  useEffect(() => {
    if (!navTarget?.name) return;
    setGuided(false); setCreating(false); setStartErr(null);
    setSection('library'); setOpenName(navTarget.name); setSelected(navTarget.name);
  }, [navTarget?.nonce]);   // eslint-disable-line react-hooks/exhaustive-deps

  // Load the selected workflow's definition (full instructions + gates) — free: reading the playbook is content.
  useEffect(() => {
    if (!selected) { setDef(null); return; }
    let alive = true;
    rpc<WorkflowDef>('getWorkflow', selected).then((d) => { if (alive) setDef(d); }).catch(() => { if (alive) setDef(null); });
    return () => { alive = false; };
  }, [selected]);

  // Derived run views off the shared map (BLOCKER 2): the live set drives the count + banner; the focused run
  // drives the Runs section; the most-recent terminal run drives Home + the playbook's last-run panel.
  const live = liveRuns(runs);
  const liveCount = live.length;
  const bannerRun = mostRecentLive(runs);       // the ambient banner shows the newest live run (+ a count if many)
  const recentRun = mostRecentTerminal(runs);
  const focused = focusedRun(runs, focusedRunId);   // Runs section subject
  const activeProfile = profiles?.find((p) => p.selected) ?? null;

  // ---- navigation helpers (one door per concept) ----
  const openPlaybook = (name: string) => { setSection('library'); setOpenName(name); setSelected(name); setStartErr(null); };
  const backToLibrary = () => { setOpenName(null); };
  const editWorkflow = (name: string) => { setSection('author'); setCreating(false); setSelected(name); };
  const newPlaybook = () => { setSection('author'); setCreating(true); setSelected(null); };
  const gotoLibrary = (filter = '') => { setLibraryFilter(filter); setOpenName(null); setSection('library'); };
  const editRules = (name: string) => { setGovTarget({ name, nonce: ++govNonce.current }); setSection('governance'); };

  // Start a run and enter the run path directly (the ratified Home "Start"). Returns false on refusal so the
  // caller can fall back to the playbook page, where the engine's verbatim upsell/off reason renders.
  const startRun = async (name: string): Promise<boolean> => {
    setBusy(true); setStartErr(null); setSelected(name);
    try {
      const r = await rpc<WorkflowRunView>('startWorkflow', name, 'human');
      markOwnRun(r.runId); onRunUpdate(r); setFocusedRunId(r.runId); setSection('runs');
      return true;
    } catch (e) {
      // the engine's EntitlementException message IS the upsell — show it verbatim, right where Start was pressed
      setStartErr(String((e as Error).message ?? e));
      return false;
    } finally { setBusy(false); }
  };
  // A Home hero "Start" enters the run directly; on refusal it lands on the playbook so the reason is visible. We
  // navigate WITHOUT clearing startErr (openPlaybook would wipe it) — startRun just set the engine's verbatim
  // refusal (the Pro upsell), and it must render on the playbook, right where the reason belongs (HIGH 5).
  const startHero = async (name: string) => {
    if (await startRun(name)) return;
    setSection('library'); setOpenName(name); setSelected(name);
  };
  const submit = async (runId: string, stepId: string, answersJson: string) => {
    onRunUpdate(await rpc<WorkflowRunView>('submitWorkflowStep', runId, stepId, answersJson, 'human'));
  };
  const skip = async (runId: string, stepId: string, reason: string) => {
    onRunUpdate(await rpc<WorkflowRunView>('skipWorkflowStep', runId, stepId, reason, 'human'));
  };
  const abort = async (runId: string, reason: string) => {
    onRunUpdate(await rpc<WorkflowRunView>('abortWorkflow', runId, reason, 'human'));
  };

  // The enforcement kill-switch: off ⇔ per-definition. The engine broadcast refreshes the rail's gated dots.
  const setEnforcementMode = async (turnOff: boolean) => {
    try { setEnforcement(await rpc<WorkflowEnforcement>('setWorkflowEnforcement', turnOff ? 'off' : 'default', 'human')); }
    catch (e) { setStartErr(String((e as Error).message ?? e)); }
  };
  // §9a per-workflow availability. Free (menu curation is content); the engine returns the fresh library AND
  // re-broadcasts it, so the other door sees the flip live. A refusal is teaching content: show it verbatim.
  const [availErr, setAvailErr] = useState<string | null>(null);
  const toggleEnabled = async (name: string, enabled: boolean) => {
    setAvailErr(null);
    try { setLibrary(await rpc<WorkflowInfo[]>('setWorkflowEnabled', name, enabled, 'human')); }
    catch (e) { setAvailErr(String((e as Error).message ?? e)); }
  };
  // §9c op→workflow binding. Setting hard|warn is Pro; the engine's refusal message IS the teaching content.
  const [bindErr, setBindErr] = useState<string | null>(null);
  const setBinding = async (op: string, require: string[], mode: string) => {
    setBindErr(null);
    try { setLibrary(await rpc<WorkflowInfo[]>('setWorkflowBinding', op, require, mode, 'human')); await refreshPolicy(); }
    catch (e) { setBindErr(String((e as Error).message ?? e)); }
  };
  // §10.6 dynamic activation ("Hide when"): set/clear the set='off' rule that curates a workflow off today's menu.
  const [actErr, setActErr] = useState<string | null>(null);
  const setActivation = async (name: string, when: string, setStr: string) => {
    setActErr(null);
    try { setLibrary(await rpc<WorkflowInfo[]>('setWorkflowActivation', name, when, setStr, 'human')); await refreshPolicy(); }
    catch (e) { setActErr(String((e as Error).message ?? e)); }
  };
  // Applying a profile is atomic; the engine hands back its plain-language note + the fresh library.
  const [profileErr, setProfileErr] = useState<string | null>(null);
  const applyProfile = async (name: string): Promise<string> => {
    setProfileErr(null);
    try {
      const r = await rpc<WorkflowProfileResult>('activateWorkflowProfile', name, 'human');
      if (r.workflows) setLibrary(r.workflows);
      await refreshPolicy(); await refreshProfiles();
      return r.note || 'Workflow profile applied.';
    } catch (e) { const msg = String((e as Error).message ?? e); setProfileErr(msg); throw new Error(msg); }
  };
  const titleOf = (name: string) => library?.find((w) => w.name === name)?.title || name;
  const enforced = enforcement?.enforced !== false;

  const shared = { library, policy, tier, titleOf } as const;

  return (
    <div className="h-full flex min-h-0">
      <SectionSidebar
        section={section} liveCount={liveCount} enforced={enforced}
        profileTitle={activeProfile?.title ?? 'Custom'}
        onNavigate={(s) => { setSection(s); if (s !== 'library') setOpenName(null); if (s !== 'home') setGuided(false); }} />
      <main className="flex-1 min-w-0 overflow-auto">
        <div className="p-4 flex flex-col gap-4 min-w-0">
          {/* ambient live-run banner — every section but Runs (there it IS the content) */}
          {bannerRun && section !== 'runs' && (
            <LiveBanner run={bannerRun} liveCount={liveCount} startedByYou={isOwnRun(bannerRun.runId)}
              onContinue={() => { setFocusedRunId(bannerRun.runId); setSection('runs'); }} />
          )}
          {/* honest enforcement-off notice, everywhere the state matters except Governance (which owns the control) */}
          {!enforced && section !== 'governance' && (
            <EnforcementOffBanner onFix={() => setSection('governance')} />
          )}

          {section === 'home' && (guided ? (
            <div className="flex flex-col gap-3">
              <button onClick={() => setGuided(false)} className="text-[11px] font-medium self-start" style={{ color: 'var(--sem-muted)' }}>← Back to Home</button>
              <ScenariosPanel variant="templates" tier={tier} library={library}
                onApplied={(l) => { if (l) setLibrary(l); else rpc<WorkflowInfo[]>('listWorkflows').then(setLibrary).catch(() => undefined); refreshPolicy(); }}
                onActiveChange={() => undefined} />
            </div>
          ) : (
            <HomeSection
              library={library} activeRun={bannerRun} recentRun={recentRun}
              profileTitle={activeProfile?.title ?? 'Custom'} enforced={enforced}
              onStartHero={startHero} onExplain={openPlaybook} onBrowse={gotoLibrary} onGuided={() => setGuided(true)}
              onGovernance={() => setSection('governance')} onOpenRun={() => setSection('runs')} />
          ))}

          {section === 'library' && (openName ? (
            <PlaybookPage
              name={openName} def={def} info={library?.find((w) => w.name === openName) ?? null}
              run={runForWorkflow(runs, openName)} tier={tier} busy={busy} startErr={startErr}
              policy={policy}
              onBack={backToLibrary} onStart={() => startRun(openName)} onEdit={() => editWorkflow(openName)}
              onToggleEnabled={toggleEnabled} onEditRules={() => editRules(openName)}
              onEvidence={(r) => setEvidenceRun(r)} />
          ) : (
            <LibrarySection
              items={library} err={libErr} availErr={availErr} policy={policy}
              initialFilter={libraryFilter}
              onOpen={openPlaybook} onNew={newPlaybook} onToggleEnabled={toggleEnabled} />
          ))}

          {section === 'runs' && (
            <RunsSection run={focused} liveRuns={live} focusedRunId={focusedRunId} onFocusRun={setFocusedRunId}
              startedByYou={!!focused && isOwnRun(focused.runId)}
              onSubmit={submit} onSkip={skip} onAbort={abort}
              onEvidence={() => focused && setEvidenceRun(focused)} onBrowse={gotoLibrary} />
          )}

          {section === 'governance' && (
            <GovernanceSection
              {...shared} enforcement={enforcement} profiles={profiles}
              focusTarget={govTarget}
              profileErr={profileErr} bindErr={bindErr} actErr={actErr}
              onSetEnforcement={setEnforcementMode} onApplyProfile={applyProfile}
              onToggleEnabled={toggleEnabled} onSetBinding={setBinding} onSetActivation={setActivation} />
          )}

          {section === 'author' && (
            <AuthorSection
              creating={creating} info={library?.find((w) => w.name === selected) ?? null} def={def}
              onNew={newPlaybook}
              onSaved={(n) => { setCreating(false); setSelected(n); rpc<WorkflowDef>('getWorkflow', n).then(setDef).catch(() => undefined); }}
              onDeleted={() => { setCreating(false); setSelected(null); setSection('library'); }} />
          )}
        </div>
      </main>
      {evidenceRun && <EvidenceArtifactDialog
        title="Workflow evidence"
        subtitle={`${evidenceRun.title || evidenceRun.workflow} · run ${evidenceRun.runId} · ${evidenceRun.modelName || 'current model'}`}
        baseName={`${(evidenceRun.modelName || 'semanticus').replace(/[^\w.-]+/g, '_')}-${evidenceRun.workflow}-evidence`}
        stateKey="workflows.evidence.format"
        load={() => rpc<EvidenceArtifactW>('exportWorkflowEvidence', evidenceRun.runId).then((a) => {
          // The engine keeps only its most recent runs (MaxHeld). An aged-out run is RESOLVED as { note: "…not
          // found." } (not a rejection), so the round-2 catch alone missed it and the raw note rendered verbatim.
          // Normalize that resolved shape into the same honest, actionable message the rejection path uses (MEDIUM 3).
          if (a?.note && !a.html && !a.json && !a.markdown && /not found/i.test(a.note)) return { error: RUN_AGED_OUT };
          return a;
        }).catch((e) => {
          const msg = String((e as Error).message ?? e);
          if (/not found/i.test(msg)) return { error: RUN_AGED_OUT };
          throw e;
        })}
        save={() => rpc<EvidenceSaveResultW>('saveEvidence', 'workflow', evidenceRun.runId, 'human')}
        onClose={() => setEvidenceRun(null)}
      />}
    </div>
  );
}

// ---- section sidebar -----------------------------------------------------------------------------
// Five stable destinations so Library, Runs and Governance are always one click away, plus a read-only project
// policy card whose only editing door is Governance (one home per concept). Runs carries a live count.
const SECTIONS: { id: Section; label: string; glyph: string }[] = [
  { id: 'home', label: 'Home', glyph: '⌂' },
  { id: 'library', label: 'Library', glyph: '≡' },
  { id: 'runs', label: 'Runs', glyph: '▶' },
  { id: 'governance', label: 'Governance', glyph: '◇' },
  { id: 'author', label: 'Author', glyph: '{}' },
];
function SectionSidebar({ section, liveCount, enforced, profileTitle, onNavigate }: {
  section: Section; liveCount: number; enforced: boolean; profileTitle: string; onNavigate: (s: Section) => void;
}) {
  return (
    <aside className="w-[210px] shrink-0 border-r flex flex-col min-h-0" style={{ borderColor: 'var(--sem-border)', background: 'var(--sem-surface)' }}>
      <div className="px-2.5 py-3 flex flex-col gap-0.5 flex-1 min-h-0">
        <div className="text-[10px] uppercase tracking-wider font-bold px-2.5 pb-2" style={{ color: 'var(--sem-muted)' }}>Workflows</div>
        {SECTIONS.map((s) => {
          const on = s.id === section;
          return (
            <button key={s.id} onClick={() => onNavigate(s.id)} data-section={s.id} aria-current={on ? 'page' : undefined}
              className="flex items-center gap-2.5 px-2.5 py-1.5 rounded-md text-[12.5px] font-semibold text-left transition-colors"
              style={on
                ? { background: 'var(--sem-accent-soft)', color: 'var(--sem-fg)', boxShadow: 'inset 2px 0 var(--sem-accent)' }
                : { color: 'var(--sem-muted)', background: 'transparent' }}>
              <span className="w-3.5 text-center tnum" style={{ color: on ? 'var(--sem-accent)' : 'var(--sem-muted)' }}>{s.glyph}</span>
              <span className="flex-1">{s.label}</span>
              {s.id === 'runs' && liveCount > 0 && (
                <span className="text-[9px] uppercase tracking-wide font-semibold px-1.5 py-0.5 rounded-full" style={{ color: 'var(--sem-accent)', background: 'var(--sem-accent-soft)', border: '1px solid color-mix(in srgb, var(--sem-accent) 40%, transparent)' }}>{liveCount} live</span>
              )}
            </button>
          );
        })}
        <div className="mt-auto rounded-lg border p-2.5" style={{ borderColor: 'var(--sem-border)', background: 'var(--sem-surface-2)' }}>
          <div className="text-[9px] uppercase tracking-wide font-semibold" style={{ color: 'var(--sem-muted)' }}>Project policy</div>
          <div className="text-[11.5px] font-semibold mt-0.5" style={{ color: enforced ? 'var(--sem-fg)' : 'var(--sem-warn, #d7a54a)' }}>{enforced ? 'Enforcement on' : 'Enforcement off'}</div>
          <div className="text-[11px]" style={{ color: 'var(--sem-muted)' }}>{profileTitle} profile</div>
          <button onClick={() => onNavigate('governance')} className="text-[11px] font-semibold mt-1" style={{ color: 'var(--sem-accent)' }}>Review controls</button>
        </div>
      </div>
    </aside>
  );
}

// ---- ambient banners -----------------------------------------------------------------------------
function LiveBanner({ run, liveCount, startedByYou, onContinue }: { run: WorkflowRunView; liveCount: number; startedByYou: boolean; onContinue: () => void }) {
  const more = liveCount > 1;
  return (
    <div className="flex items-center gap-3 rounded-lg px-3 py-2" style={{ background: 'var(--sem-surface-2)', border: '1px solid color-mix(in srgb, var(--sem-accent) 40%, var(--sem-border))' }}>
      <span className="w-2 h-2 rounded-full shrink-0" style={{ background: 'var(--sem-accent)', boxShadow: '0 0 0 4px var(--sem-accent-soft)' }} />
      <span className="text-[12px] font-semibold">{run.title || run.workflow} is running</span>
      <span className="text-[11.5px]" style={{ color: 'var(--sem-muted)' }}>step {Math.min(run.stepIndex + 1, run.totalSteps)} of {run.totalSteps}, started by {startedByYou ? 'you' : 'the AI Assistant'}{more ? ` · ${liveCount} runs live` : ''}</span>
      <button onClick={onContinue} className="ml-auto text-[11px] px-2.5 py-1 rounded-md font-semibold shrink-0" style={{ background: 'var(--sem-accent)', color: 'var(--sem-on-accent)' }}>{more ? 'Continue runs' : 'Continue run'}</button>
    </div>
  );
}
function EnforcementOffBanner({ onFix }: { onFix: () => void }) {
  return (
    <div className="flex items-center gap-2 rounded-md px-3 py-2 text-[12px]" style={{ background: 'color-mix(in srgb, var(--sem-warn, #d7a54a) 14%, transparent)', border: '1px solid color-mix(in srgb, var(--sem-warn, #d7a54a) 45%, transparent)', color: 'var(--sem-fg)' }}>
      <span aria-hidden>!</span>
      <span className="flex-1">Enforcement is <b>off</b> model-wide. New runs start with gates skipped and record no verified evidence. Runs already underway keep the setting they started with. Good for quick tasks; turn it back on for accountable runs.</span>
      <button onClick={onFix} className="text-[11px] px-2 py-0.5 rounded-md font-semibold shrink-0" style={{ background: 'var(--sem-accent-soft)', color: 'var(--sem-accent)', border: '1px solid color-mix(in srgb, var(--sem-accent) 40%, transparent)' }}>Review in Governance</button>
    </div>
  );
}

// ===================================================================================================
// HOME — the calm landing: the current/recent run, the two ratified quick-start jobs, and three quiet cards.
// ===================================================================================================
const HEROES: { workflow: string; title: string; blurb: string }[] = [
  { workflow: 'make-ai-ready', title: 'Make the model AI-ready', blurb: 'Get the model ready for Copilot and Q&A. The AI Assistant fills the gaps and the readiness grade shows the improvement.' },
  { workflow: 'verified-measure', title: 'Author a hard measure', blurb: 'Pin what the requirement says, lock expected values from raw rows, and prove one candidate against an independent raw-row witness.' },
];
function HomeSection({ library, activeRun, recentRun, profileTitle, enforced, onStartHero, onExplain, onBrowse, onGuided, onGovernance, onOpenRun }: {
  library: WorkflowInfo[] | null; activeRun: WorkflowRunView | null; recentRun: WorkflowRunView | null;
  profileTitle: string; enforced: boolean;
  onStartHero: (n: string) => void; onExplain: (n: string) => void; onBrowse: (f?: string) => void; onGuided: () => void; onGovernance: () => void; onOpenRun: () => void;
}) {
  const [q, setQ] = useState('');
  const total = library?.length ?? 0;
  const available = (library ?? []).filter((w) => !w.error && w.enabled !== false).length;
  const heroExists = (n: string) => (library ?? []).some((w) => w.name === n);
  return (
    <div className="flex flex-col gap-4 min-w-0">
      {/* current or most-recent activity — only when there is something to say (calm by default) */}
      {!activeRun && recentRun && (
        <button onClick={onOpenRun} className="flex items-center gap-3 rounded-lg px-3 py-2 text-left" style={{ background: 'var(--sem-surface-2)', border: '1px solid var(--sem-border)' }}>
          <span className="w-2 h-2 rounded-full shrink-0" style={{ background: recentRun.status === 'completed' ? 'var(--sem-good)' : 'var(--sem-muted)' }} />
          <span className="text-[12px] font-semibold">{recentRun.title || recentRun.workflow}</span>
          <span className="text-[11.5px]" style={{ color: 'var(--sem-muted)' }}>Last run {recentRun.status === 'completed' ? 'completed' : uiLabel(recentRun.status).toLowerCase()} · {recentRun.steps.filter((s) => s.status === 'passed').length}/{recentRun.totalSteps} steps passed</span>
          <span className="ml-auto text-[11px] font-semibold" style={{ color: 'var(--sem-accent)' }}>View run →</span>
        </button>
      )}

      <div>
        <h2 className="text-[15px] font-semibold mb-2.5">Start a job</h2>
        <div className="grid gap-3" style={{ gridTemplateColumns: 'repeat(auto-fit, minmax(300px, 1fr))' }}>
          {HEROES.map((h) => (
            <HeroCard key={h.workflow} title={h.title} blurb={h.blurb}
              disabled={library != null && !heroExists(h.workflow)}
              onStart={() => onStartHero(h.workflow)} onExplain={() => onExplain(h.workflow)} />
          ))}
        </div>
      </div>

      <div className="flex items-center gap-3">
        <button onClick={() => onBrowse()} className="text-[12px] px-3 py-1.5 rounded-lg font-medium shrink-0" style={{ background: 'var(--sem-surface-2)', color: 'var(--sem-fg)', border: '1px solid var(--sem-border)' }}>Browse all playbooks ({total})</button>
        <input value={q} onChange={(e) => setQ(e.target.value)} placeholder="Search playbooks"
          onKeyDown={(e) => { if (e.key === 'Enter') onBrowse(q.trim()); }}
          className="flex-1 text-[12px] px-3 py-1.5 rounded-lg outline-none" style={{ background: 'var(--sem-surface-2)', color: 'var(--sem-fg)', border: '1px solid var(--sem-border)' }} />
      </div>

      <div className="grid gap-3" style={{ gridTemplateColumns: 'repeat(auto-fit, minmax(240px, 1fr))' }}>
        <QuietCard kicker="Guided setup" title="Ready-made playbooks" body="Fill in your details once, preview in plain words, then apply." onClick={onGuided} />
        <QuietCard kicker="Library" title={`${total} playbooks, ${available} available`} body="Browse, inspect, or turn any playbook on or off." onClick={() => onBrowse()} />
        <QuietCard kicker="Policy" title={profileTitle} body={enforced ? 'Enforcement on.' : 'Enforcement off; gates are skipped.'} onClick={onGovernance} />
      </div>
    </div>
  );
}
function HeroCard({ title, blurb, disabled, onStart, onExplain }: { title: string; blurb: string; disabled: boolean; onStart: () => void; onExplain: () => void }) {
  return (
    <div className="rounded-xl border p-4" style={{ background: 'var(--sem-surface-2)', borderColor: disabled ? 'var(--sem-border)' : 'color-mix(in srgb, var(--sem-accent) 35%, var(--sem-border))', opacity: disabled ? 0.6 : 1 }}>
      <h3 className="text-[13.5px] font-semibold">{title}</h3>
      <p className="text-[12px] mt-1 mb-2.5" style={{ color: 'var(--sem-muted)' }}>{blurb}</p>
      <div className="flex items-center gap-2">
        <button disabled={disabled} onClick={onStart} className="text-[12px] px-3 py-1.5 rounded-lg font-semibold disabled:opacity-50" style={{ background: 'var(--sem-accent)', color: 'var(--sem-on-accent)' }}>Start</button>
        <button disabled={disabled} onClick={onExplain} className="text-[12px] px-2.5 py-1.5 rounded-lg font-medium disabled:opacity-50" style={{ color: 'var(--sem-muted)' }}>What it does</button>
      </div>
    </div>
  );
}
function QuietCard({ kicker, title, body, onClick }: { kicker: string; title: string; body: string; onClick: () => void }) {
  return (
    <button onClick={onClick} className="text-left rounded-xl border p-3.5 transition-colors" style={{ background: 'var(--sem-surface-2)', borderColor: 'var(--sem-border)' }}>
      <div className="text-[10px] uppercase tracking-wide font-semibold" style={{ color: 'var(--sem-muted)' }}>{kicker}</div>
      <div className="text-[13px] font-semibold mt-1">{title}</div>
      <div className="text-[11.5px] mt-0.5" style={{ color: 'var(--sem-muted)' }}>{body}</div>
    </button>
  );
}

// ===================================================================================================
// LIBRARY — a browsing task: calm scannable rows with plain-language action chips + the §9a availability
// toggle. Everything else about a playbook is on its page. Journey-ordered groups; broken files stay listed.
// ===================================================================================================
// Journey-ordered groups, keyed by the ACTUAL stock seed ids (Semanticus.Engine/workflows/*.md). A stock id that
// slips this map falls to DEFAULT_STOCK_GROUP (Build), never Custom — Custom is reserved for model-authored
// playbooks. Keep this list honest against the seed library; a phantom key (a workflow that does not ship) is worse
// than an omission because it reads as coverage that is not there.
const GROUP_OF: Record<string, string> = {
  'add-relationship': 'Build', 'import-table': 'Build', 'calendar-setup': 'Build',
  'time-intelligence-variants': 'Build', 'new-measure': 'Build', 'refactor-to-calculation-group': 'Build',
  'incremental-refresh-setup': 'Data',
  'check-blast-radius': 'Quality', 'governed-rename': 'Quality', 'model-hygiene-pass': 'Quality',
  'verified-measure': 'Quality', 'optimize-dax': 'Quality', 'make-ai-ready': 'Quality',
  'secure-with-rls': 'Security',
  'deploy-to-production': 'Ship',
};
const CUSTOM_GROUP = 'Custom';
const DEFAULT_STOCK_GROUP = 'Build';   // any built-in not yet mapped lands in Build, not Custom
const GROUP_ORDER = ['Design', 'Build', 'Data', 'Quality', 'Security', 'Ship', CUSTOM_GROUP];
// A stock (built-in) workflow is never "Custom" — if it is missing from the map it gets a sensible default group.
const groupOf = (w: WorkflowInfo) => GROUP_OF[w.name] ?? (w.source === 'stock' ? DEFAULT_STOCK_GROUP : CUSTOM_GROUP);
const matchesFilter = (w: WorkflowInfo, f: string) =>
  !f || [w.name, w.title, w.description, ...(w.triggers ?? [])].join(' ').toLowerCase().includes(f);

function LibrarySection({ items, err, availErr, policy, initialFilter, onOpen, onNew, onToggleEnabled }: {
  items: WorkflowInfo[] | null; err: string | null; availErr: string | null; policy: WorkflowPolicy | null;
  initialFilter: string; onOpen: (n: string) => void; onNew: () => void; onToggleEnabled: (n: string, e: boolean) => void;
}) {
  const [filter, setFilter] = useState(initialFilter);
  useEffect(() => { setFilter(initialFilter); }, [initialFilter]);
  const f = filter.trim().toLowerCase();
  const requiredCount = (name: string) => policy?.workflows.find((w) => w.name === name)?.requiredForOps.length ?? 0;

  const groups = useMemo(() => {
    const kept = (items ?? []).filter((w) => matchesFilter(w, f));
    return GROUP_ORDER.map((name) => ({ name, items: kept.filter((w) => groupOf(w) === name) })).filter((g) => g.items.length > 0);
  }, [items, f]);

  return (
    <div className="flex flex-col gap-3 min-w-0">
      <div className="flex items-center gap-2.5">
        <input value={filter} onChange={(e) => setFilter(e.target.value)} spellCheck={false} data-wf-filter
          onKeyDown={(e) => { if (e.key === 'Escape') setFilter(''); }}
          placeholder="Filter by title, description, or action"
          className="flex-1 text-[12px] px-3 py-1.5 rounded-lg outline-none" style={{ background: 'var(--sem-surface-2)', color: 'var(--sem-fg)', border: '1px solid var(--sem-border)' }} />
        <button onClick={onNew} className="text-[12px] px-3 py-1.5 rounded-lg font-semibold shrink-0" style={{ background: 'var(--sem-accent)', color: 'var(--sem-on-accent)' }}>+ New playbook</button>
      </div>
      {err && <Banner color="var(--sem-bad)">{err}</Banner>}
      {availErr && <Banner color="var(--sem-warn, #d7a54a)">{availErr}</Banner>}
      {items == null ? (
        <div className="text-[12px]" style={{ color: 'var(--sem-muted)' }}>Loading library…</div>
      ) : groups.length === 0 ? (
        <div className="text-[12px]" style={{ color: 'var(--sem-muted)' }}>No workflows match “{filter.trim()}”.</div>
      ) : (
        <div className="flex flex-col gap-3">
          {groups.map((g) => (
            <div key={g.name} className="rounded-xl border overflow-hidden" style={{ borderColor: 'var(--sem-border)' }}>
              <div className="text-[10.5px] uppercase tracking-wide font-bold px-3.5 py-2 border-b" style={{ color: 'var(--sem-muted)', background: 'var(--sem-surface)', borderColor: 'var(--sem-border)' }}>{g.name}</div>
              {g.items.map((w) => <LibraryRow key={w.name} w={w} requiredCount={requiredCount(w.name)} onOpen={() => onOpen(w.name)} onToggleEnabled={onToggleEnabled} />)}
            </div>
          ))}
        </div>
      )}
    </div>
  );
}

// A calm row: the title (opens the page), the state pills, the plain-language action chips, and the §9a switch.
// Off / hidden-now / broken are DISTINCT chips (never conflated). A div, not a button — it hosts the nested switch.
function LibraryRow({ w, requiredCount, onOpen, onToggleEnabled }: {
  w: WorkflowInfo; requiredCount: number; onOpen: () => void; onToggleEnabled: (n: string, e: boolean) => void;
}) {
  const broken = !!w.error;
  const off = w.enabled === false;
  const offMenu = !broken && !off && w.active === false;
  const dim = off ? { opacity: 0.6 } : undefined;
  return (
    <div className="flex items-center gap-3 px-3.5 py-2.5 border-b last:border-b-0 flex-wrap" style={{ borderColor: 'var(--sem-border)', background: 'var(--sem-surface-2)' }} data-workflow={w.name}>
      <div className="min-w-0 flex-1">
        <div className="flex items-center gap-2 flex-wrap">
          {broken
            ? <span className="text-[13px] font-semibold" style={{ color: 'var(--sem-bad)' }}>{w.title || w.name}</span>
            : <button onClick={onOpen} className="text-[13px] font-semibold text-left" style={{ color: 'var(--sem-accent)', ...dim }}>{w.title || w.name}</button>}
          {broken
            ? <Pill tint="var(--sem-bad)">File error</Pill>
            : <>
                {w.gated && <Pill tint="var(--sem-accent)">Gated</Pill>}
                {requiredCount > 0 && <Pill>Required for {requiredCount} action{requiredCount === 1 ? '' : 's'}</Pill>}
                {off && <Pill tint="var(--sem-bad)">Off</Pill>}
                {offMenu && <Pill tint="var(--sem-warn, #d7a54a)">Hidden now</Pill>}
              </>}
        </div>
        <div className="text-[11.5px] mt-0.5 flex items-center gap-1.5 flex-wrap" style={{ color: 'var(--sem-muted)', ...dim }}>
          {broken ? <span>Step could not be read. Open it to repair the file or rebuild it in Author.</span> : (
            <>
              {w.description && <span className="truncate max-w-[52ch]">{w.description}</span>}
              {w.triggers.slice(0, 3).map((t) => (
                <span key={t} className="text-[9.5px] px-1.5 py-0.5 rounded-full" style={{ background: 'var(--sem-surface)', color: 'var(--sem-muted)', border: '1px solid var(--sem-border)' }}>{opLabel(t)}</span>
              ))}
            </>
          )}
        </div>
        {offMenu && (
          <div className="text-[10.5px] mt-1" style={{ color: 'var(--sem-muted)', fontStyle: 'italic' }}>Off today’s menu: {w.activeReason || 'hidden by a project rule'}. You can still open and run it.</div>
        )}
      </div>
      <div className="shrink-0">
        {broken
          ? <button onClick={onOpen} className="text-[11px] font-semibold" style={{ color: 'var(--sem-accent)' }}>View error</button>
          : <AvailabilitySwitch on={!off} label={`Availability: ${w.name}`}
              title={off ? 'Off: this playbook can’t be started until you turn it back on. Click to turn it on.' : 'On: this playbook can be started. Click to turn it off for this project (it stays listed so you can turn it back on).'}
              onToggle={() => onToggleEnabled(w.name, off)} />}
      </div>
    </div>
  );
}

// ===================================================================================================
// PLAYBOOK PAGE — a workflow reads as a document: purpose, sequence, checks, Start. Visible verbs (no ellipsis
// menu): availability, Edit workflow, Start run, and Edit rules are all in the open. Steps expand to verbatim
// instructions in place; governance is summarized here with a deep link, edited only in Governance.
// ===================================================================================================
function PlaybookPage({ name, def, info, run, tier, busy, startErr, policy, onBack, onStart, onEdit, onToggleEnabled, onEditRules, onEvidence }: {
  name: string; def: WorkflowDef | null; info: WorkflowInfo | null; run: WorkflowRunView | null;
  tier: string; busy: boolean; startErr: string | null; policy: WorkflowPolicy | null;
  onBack: () => void; onStart: () => void; onEdit: () => void; onToggleEnabled: (n: string, e: boolean) => void;
  onEditRules: () => void; onEvidence: (r: WorkflowRunView) => void;
}) {
  const broken = !!info?.error;
  const title = broken ? (info?.name || name) : (def?.title || info?.title || name);
  const gated = !!info?.gated;
  const lockedFree = gated && tier !== 'pro';
  const off = !broken && info?.enabled === false;
  const offMenu = !broken && !off && info?.active === false;
  const offButRequired = off && info?.active === true;
  const startBlocked = off && !offButRequired;
  const entry = policy?.workflows.find((w) => w.name === name) ?? null;
  const requiredOps = entry?.requiredForOps ?? [];
  const bindMode = (op: string) => policy?.bindings.find((b) => b.op === op && b.require.includes(name))?.mode ?? null;
  const terminal = isTerminal(run);

  return (
    <div className="flex flex-col gap-3 min-w-0">
      <div className="text-[11.5px]" style={{ color: 'var(--sem-muted)' }}>
        <button onClick={onBack} className="font-semibold" style={{ color: 'var(--sem-accent)' }}>Library</button> / {title}
      </div>

      {broken ? (
        <ErrorPanel name={info?.name || name} error={info?.error || 'This workflow file could not be read.'} />
      ) : (
        <>
          <div className="flex items-start gap-4 flex-wrap">
            <div className="flex-1 min-w-[260px]">
              <div className="flex items-center gap-2 flex-wrap">
                <h2 className="text-[17px] font-semibold" style={off ? { opacity: 0.7 } : undefined}>{title}</h2>
                {info && <SourceBadge source={info.source} />}
                {gated && <Pill tint="var(--sem-accent)">Gated</Pill>}
                {lockedFree && <Pill tint="var(--sem-accent)">Pro to run</Pill>}
                {off && <Pill tint="var(--sem-warn, #d7a54a)">Off</Pill>}
              </div>
              <div className="text-[11.5px] mt-1 tnum" style={{ color: 'var(--sem-muted)' }}>
                {info ? `Version ${info.version}` : ''}{def ? ` · ${def.steps.length} step${def.steps.length === 1 ? '' : 's'}` : ''}
                {def?.strictness ? ` · ${def.strictness === 'hard' ? 'blocking checks' : def.strictness === 'warn' ? 'warning checks' : `${uiLabel(def.strictness).toLowerCase()} checks`}` : ''}
                {terminal ? ' · last run completed' : ''}
              </div>
              {(def?.description || info?.description) && (
                <p className="text-[12.5px] mt-2 max-w-[66ch]" style={{ color: 'var(--sem-muted)' }}>{def?.description || info?.description}</p>
              )}
            </div>
            <div className="flex items-center gap-2 shrink-0">
              {info && (
                <span className="flex items-center gap-1.5">
                  <span className="text-[11px]" style={{ color: 'var(--sem-muted)' }}>{off ? 'Off' : 'Available'}</span>
                  <AvailabilitySwitch on={!off} label={`Availability: ${name}`}
                    title={off ? 'Off: turn it on to start a run.' : 'On: click to turn it off for this project.'}
                    onToggle={() => onToggleEnabled(name, off)} />
                </span>
              )}
              <Button onClick={onEdit}>Edit workflow</Button>
              <Button primary disabled={busy || lockedFree || startBlocked} onClick={onStart}
                title={startBlocked ? 'Turned off for this project. Turn it back on to start a run.'
                  : lockedFree ? 'Starting an enforced workflow is a Pro capability. Free: read the steps below and follow them manually.'
                  : 'Start a run. Each gate is verified as you go.'}>
                {busy ? 'Starting…' : run && run.workflow === name && terminal ? 'Start again' : 'Start run'}
              </Button>
            </div>
          </div>

          {startBlocked && <Banner color="var(--sem-warn, #d7a54a)">This playbook is turned off for this project, so it can’t be started. Use the switch above (or on its row in the Library) to turn it back on.</Banner>}
          {offButRequired && <Banner color="var(--sem-warn, #d7a54a)">Turned off, but a project mandate still requires it ({info?.activeReason || 'required by a policy'}), so it can still be started.</Banner>}
          {offMenu && <Banner color="var(--sem-muted)">Off today’s menu: {info?.activeReason || 'hidden by a project rule'}. This curates the menu; you can still start it, and the run records a note.</Banner>}
          {startErr && <Banner color="var(--sem-accent)">{startErr}</Banner>}

          <div className="grid gap-3" style={{ gridTemplateColumns: 'minmax(0, 1.5fr) minmax(0, 1fr)' }}>
            <Panel>
              <SectionTitle>What this playbook does</SectionTitle>
              {def?.error ? (
                <div className="text-[12px] mt-2" style={{ color: 'var(--sem-bad)' }}>{def.error}</div>
              ) : !def ? (
                <div className="text-[12px] mt-2" style={{ color: 'var(--sem-muted)' }}>Loading steps…</div>
              ) : (
                <div className="relative mt-2">
                  <RailLine />
                  <div className="flex flex-col">{def.steps.map((s) => <OverviewStep key={s.id} step={s} />)}</div>
                </div>
              )}
            </Panel>
            <div className="flex flex-col gap-3">
              <Panel>
                <div className="flex items-center">
                  <SectionTitle>When it applies</SectionTitle>
                  <button onClick={onEditRules} className="ml-auto text-[11px] font-semibold" style={{ color: 'var(--sem-accent)' }}>Edit rules</button>
                </div>
                <div className="text-[11.5px] mt-2" style={{ color: 'var(--sem-muted)' }}>
                  {info?.active === false ? <>Hidden from the menu right now: {info.activeReason || 'a project rule'}.</> : <>Shown on the menu now.</>}
                </div>
                {requiredOps.length > 0 ? (
                  <div className="text-[11.5px] mt-1.5 flex items-center gap-1.5 flex-wrap" style={{ color: 'var(--sem-muted)' }}>
                    Required for:
                    {requiredOps.map((op) => (
                      <span key={op} className="text-[10px] px-1.5 py-0.5 rounded-full" style={{ background: 'var(--sem-surface)', color: 'var(--sem-fg)', border: '1px solid var(--sem-border)' }}>{opLabel(op)}{bindMode(op) ? ` · ${uiLabel(bindMode(op)!)}` : ''}</span>
                    ))}
                  </div>
                ) : (
                  <div className="text-[11.5px] mt-1.5" style={{ color: 'var(--sem-muted)' }}>Required for: nothing yet.</div>
                )}
              </Panel>
              <Panel>
                <SectionTitle>Last completed run</SectionTitle>
                {terminal && run ? (
                  <>
                    <div className="text-[11.5px] mt-1.5" style={{ color: 'var(--sem-muted)' }}>
                      {run.status === 'completed' ? 'Completed' : uiLabel(run.status)} · {run.steps.filter((s) => s.status === 'passed').length} of {run.totalSteps} passed · {run.steps.reduce((n, s) => n + s.verifyResults.filter((v) => v.status === 'passed').length, 0)} verified checks sealed.
                    </div>
                    <button onClick={() => onEvidence(run)} className="text-[11px] font-semibold mt-1.5" style={{ color: 'var(--sem-accent)' }}>Evidence report</button>
                  </>
                ) : (
                  <div className="text-[11.5px] mt-1.5" style={{ color: 'var(--sem-muted)' }}>No completed run recorded this session.</div>
                )}
              </Panel>
            </div>
          </div>
        </>
      )}
    </div>
  );
}

// ===================================================================================================
// RUNS — the focused run treatment: header + one expanded current step, completed steps compressed to records
// that reopen on click, decline-with-reason, and the honest enforcement-off state (read from the RUN's OWN frozen
// snapshot, never the current model-wide flag — a mid-run toggle can't tear a run). Evidence exports only after
// the run is terminal. Up to 8 runs can be live at once, so a switcher focuses one when there is more than one.
// ===================================================================================================
function RunsSection({ run, liveRuns, focusedRunId, onFocusRun, startedByYou, onSubmit, onSkip, onAbort, onEvidence, onBrowse }: {
  run: WorkflowRunView | null; liveRuns: WorkflowRunView[]; focusedRunId: string | null; onFocusRun: (id: string) => void;
  startedByYou: boolean;
  onSubmit: (runId: string, stepId: string, answersJson: string) => Promise<void>;
  onSkip: (runId: string, stepId: string, reason: string) => Promise<void>;
  onAbort: (runId: string, reason: string) => Promise<void>; onEvidence: () => void; onBrowse: () => void;
}) {
  if (!run) {
    return (
      <div className="h-full flex items-center justify-center p-8">
        <div className="text-center max-w-sm">
          <div className="text-[15px] font-semibold mb-1">No active run</div>
          <div className="text-[12px]" style={{ color: 'var(--sem-muted)' }}>Open a playbook from the Library and press Start run. A run appears here whether you or the AI Assistant starts it.</div>
          <button onClick={onBrowse} className="text-[12px] font-semibold mt-3" style={{ color: 'var(--sem-accent)' }}>Browse the Library →</button>
        </div>
      </div>
    );
  }
  const live = run.status === 'active';
  const terminal = isTerminal(run);
  // The switcher lists the LIVE runs AND the focused run when it is a terminal receipt the human chose to keep
  // reading — otherwise the moment a selected run finishes, a still-live run silently becomes unreachable from Runs
  // (HIGH 2). We respect the selection (the completed run stays shown) and keep every live run one click away.
  const switchable = liveRuns.some((r) => r.runId === run.runId) ? liveRuns : [...liveRuns, run];
  return (
    <div className="flex flex-col gap-3 min-w-0">
      {/* multi-run switcher — when there is more than one run to focus (dual-drive: you + the AI Assistant, plus a
          completed run you're still viewing while others run) */}
      {switchable.length > 1 && (
        <div className="flex items-center gap-1.5 flex-wrap">
          <span className="text-[10px] uppercase tracking-wide font-semibold mr-1" style={{ color: 'var(--sem-muted)' }}>{liveRuns.length} run{liveRuns.length === 1 ? '' : 's'} live</span>
          {switchable.map((r) => {
            const on = r.runId === (focusedRunId ?? run.runId);
            const rActive = r.status === 'active';
            return (
              <button key={r.runId} onClick={() => onFocusRun(r.runId)}
                className="text-[11px] px-2 py-0.5 rounded-full font-semibold"
                style={on
                  ? { background: 'var(--sem-accent-soft)', color: 'var(--sem-accent)', border: '1px solid color-mix(in srgb, var(--sem-accent) 40%, transparent)' }
                  : rActive
                    ? { background: 'var(--sem-surface-2)', color: 'var(--sem-muted)', border: '1px solid var(--sem-border)' }
                    : { background: 'var(--sem-surface)', color: 'var(--sem-muted)', border: '1px dashed var(--sem-border)' }}>
                {r.title || r.workflow} · {rActive ? `step ${Math.min(r.stepIndex + 1, r.totalSteps)}/${r.totalSteps}` : uiLabel(r.status)}
              </button>
            );
          })}
        </div>
      )}
      {/* the focused run's own view — keyed by runId so ALL run-local form state (a step's typed answers, an open
          decline/skip/abort reason) resets when focus moves to another run; two runs that share step ids can never
          leak an answer or a reason across a switch (BLOCKER 1). No run-scoped state lives above this boundary —
          focusedRunId belongs to the parent. */}
      <div key={run.runId} className="flex flex-col gap-3 min-w-0">
      <div className="flex items-center gap-2 flex-wrap">
        <h2 className="text-[16px] font-semibold">{run.title || run.workflow}</h2>
        {live
          ? <Pill tint="var(--sem-accent)">Live</Pill>
          : <Pill tint={run.status === 'completed' ? 'var(--sem-good)' : 'var(--sem-muted)'}>{uiLabel(run.status)}</Pill>}
        <Pill>Started by {startedByYou ? 'you' : 'the AI Assistant'}</Pill>
        <div className="ml-auto flex items-center gap-2">
          <button onClick={onEvidence} disabled={!terminal}
            title={terminal ? 'Export the sealed evidence report' : 'Evidence exports after the run completes or is aborted, so an active run is never presented as final.'}
            className="text-[11.5px] px-2.5 py-1 rounded-md font-medium disabled:opacity-45"
            style={{ background: 'var(--sem-surface-2)', color: terminal ? 'var(--sem-accent)' : 'var(--sem-muted)', border: '1px solid var(--sem-border)' }}>
            {terminal ? 'Evidence report' : 'Evidence (after completion)'}
          </button>
          {live && <ReasonButton label="Abort" title="Abort this run. The reason is recorded on the audit trail." onConfirm={(reason) => onAbort(run.runId, reason)} danger />}
        </div>
      </div>
      {/* an aborted run keeps its recorded reason visible (the audit is the point) */}
      {run.status === 'aborted' && run.abortReason && (
        <div className="rounded-md px-3 py-2 text-[11.5px]" style={{ background: 'color-mix(in srgb, var(--sem-bad) 12%, transparent)', border: '1px solid color-mix(in srgb, var(--sem-bad) 34%, transparent)', color: 'var(--sem-fg)' }}>
          Aborted: {run.abortReason}
        </div>
      )}
      {/* A run's gate-skip is shown PER STEP (the current step's card + each completed step's "Ran at skipped"
          chip), never as a run-wide claim: the wire exposes only per-step effectiveStrictness, so an early "off"
          step beside a later HARD step must not read as "the whole run skips gates" (HIGH 6). */}
      <RunRail run={run} onSubmit={onSubmit} onSkip={onSkip} onAbort={onAbort} />
      </div>
    </div>
  );
}

// ===================================================================================================
// GOVERNANCE — one home for every policy control: enforcement (with a consequence preview), the profiles
// (the only home for profiles), and the whole-project table with THREE separate columns for the three distinct
// concepts (On the menu / Required for / Hidden when). Per-workflow editing expands the three-axis rules inline.
// ===================================================================================================
function GovernanceSection({ library, policy, tier, titleOf, enforcement, profiles, focusTarget, profileErr, bindErr, actErr, onSetEnforcement, onApplyProfile, onToggleEnabled, onSetBinding, onSetActivation }: {
  library: WorkflowInfo[] | null; policy: WorkflowPolicy | null; tier: string; titleOf: (n: string) => string;
  enforcement: WorkflowEnforcement | null; profiles: WorkflowProfileInfo[] | null;
  focusTarget: { name: string; nonce: number } | null;
  profileErr: string | null; bindErr: string | null; actErr: string | null;
  onSetEnforcement: (turnOff: boolean) => void; onApplyProfile: (name: string) => Promise<string>;
  onToggleEnabled: (n: string, e: boolean) => void; onSetBinding: (op: string, require: string[], mode: string) => void;
  onSetActivation: (n: string, when: string, setStr: string) => Promise<void>;
}) {
  const enforced = enforcement?.enforced !== false;
  const [expanded, setExpanded] = useState<string | null>(null);
  const tableRef = useRef<HTMLDivElement | null>(null);
  const lints = policy?.lints ?? [];
  // rows: only real, non-broken workflows that carry SOME governance signal or are simply governable — keep the
  // table scannable by showing workflows that have any state to govern (all non-broken workflows qualify).
  const rows = (library ?? []).filter((w) => !w.error);

  // A playbook's "Edit rules" deep-links HERE to that workflow's row — expand it and scroll it into view (never
  // just dump the analyst at the top of Governance to hunt for the row).
  useEffect(() => {
    if (!focusTarget?.name) return;
    setExpanded(focusTarget.name);
    const el = tableRef.current?.querySelector(`[data-gov-row="${CSS.escape(focusTarget.name)}"]`);
    el?.scrollIntoView({ block: 'center', behavior: 'smooth' });
  }, [focusTarget?.nonce]);   // eslint-disable-line react-hooks/exhaustive-deps

  return (
    <div ref={tableRef} className="flex flex-col gap-4 min-w-0">
      <EnforcementCard enforced={enforced} onSetEnforcement={onSetEnforcement} />

      <div>
        <h3 className="text-[13.5px] font-semibold mb-1.5">Project profile <span className="text-[11.5px] font-normal" style={{ color: 'var(--sem-muted)' }}>(one choice sets availability, requirements and check strength; saved with the project)</span></h3>
        {profileErr && <div className="mb-2"><Banner color="var(--sem-warn, #d7a54a)">{profileErr}</Banner></div>}
        <div className="grid gap-3" style={{ gridTemplateColumns: 'repeat(auto-fit, minmax(260px, 1fr))' }}>
          {profiles == null ? <div className="text-[12px]" style={{ color: 'var(--sem-muted)' }}>Loading profiles…</div>
            : profiles.map((p) => <ProfileCard key={p.name} profile={p} tier={tier} onApply={() => onApplyProfile(p.name)} />)}
        </div>
      </div>

      <div>
        <h3 className="text-[13.5px] font-semibold mb-1.5">Playbooks</h3>
        <div className="rounded-xl border overflow-hidden" style={{ borderColor: 'var(--sem-border)' }}>
          <div className="grid items-center gap-2 px-3.5 py-2 text-[10px] uppercase tracking-wide font-bold border-b" style={{ gridTemplateColumns: 'minmax(0,2fr) 90px minmax(0,1.6fr) minmax(0,1.4fr) 60px', color: 'var(--sem-muted)', background: 'var(--sem-surface)', borderColor: 'var(--sem-border)' }}>
            <span>Playbook</span><span>On the menu</span><span>Required for</span><span>Hidden when</span><span></span>
          </div>
          {rows.map((w) => {
            const entry = policy?.workflows.find((p) => p.name === w.name) ?? null;
            const reqOps = entry?.requiredForOps ?? [];
            // Each required op carries its MODE (Hard blocks, Warn flags) and whether it is LOCKED by committed team
            // policy — the "Required for" column must not flatten those to a bare op name (they change what happens).
            const bindFor = (op: string) => policy?.bindings.find((b) => b.op === op && b.require.includes(w.name)) ?? null;
            const off = w.enabled === false;
            const isOpen = expanded === w.name;
            return (
              <div key={w.name} data-gov-row={w.name} className="border-b last:border-b-0" style={{ borderColor: 'var(--sem-border)' }}>
                <div className="grid items-center gap-2 px-3.5 py-2.5 text-[12px]" style={{ gridTemplateColumns: 'minmax(0,2fr) 90px minmax(0,1.6fr) minmax(0,1.4fr) 60px', background: 'var(--sem-surface-2)' }}>
                  <span className="font-medium truncate" style={off ? { opacity: 0.6 } : undefined}>{w.title || w.name}</span>
                  <span>
                    <AvailabilitySwitch on={!off} label={`Availability: ${w.name}`}
                      title={off ? 'Off: turn it on to allow runs.' : 'On: click to turn it off for this project.'}
                      onToggle={() => onToggleEnabled(w.name, off)} />
                  </span>
                  <span className="text-[11px]">
                    {reqOps.length === 0 ? <span style={{ color: 'var(--sem-muted)' }}>None</span> : (
                      <span className="flex flex-wrap gap-1">
                        {reqOps.map((op) => {
                          const m = bindFor(op);
                          const mode = m?.mode ?? null;   // null = a conditional (rule-driven) requirement
                          const locked = m ? !m.userDisablable : false;
                          const tint = mode === 'warn' ? 'var(--sem-warn, #d7a54a)' : 'var(--sem-accent)';
                          return (
                            <span key={op} className="inline-flex items-center gap-1 px-1.5 py-0.5 rounded-full" style={{ background: 'var(--sem-surface)', color: 'var(--sem-fg)', border: '1px solid var(--sem-border)' }}>
                              {opLabel(op)}
                              <span style={{ color: tint }}>· {mode ? uiLabel(mode) : 'Conditional'}</span>
                              {locked && <span title="Locked by committed team policy" style={{ color: 'var(--sem-muted)' }}>· locked</span>}
                            </span>
                          );
                        })}
                      </span>
                    )}
                  </span>
                  <span className="text-[11px]" style={{ color: w.active === false ? 'var(--sem-warn, #d7a54a)' : 'var(--sem-muted)' }}>
                    {w.active === false ? (w.activeReason || 'hidden by a rule') : 'Shown now'}
                  </span>
                  <button onClick={() => setExpanded(isOpen ? null : w.name)} className="text-[11px] font-semibold text-right" style={{ color: 'var(--sem-accent)' }}>{isOpen ? 'Close' : 'Rules'}</button>
                </div>
                {isOpen && (
                  <div className="px-3.5 pb-3 pt-1" style={{ background: 'var(--sem-surface)' }}>
                    <RequiredForControl name={w.name} policy={policy} tier={tier} onSetBinding={onSetBinding} err={bindErr} titleOf={titleOf} />
                    <HideWhenControl info={w} policy={policy} tier={tier} onSetActivation={onSetActivation} err={actErr} />
                  </div>
                )}
              </div>
            );
          })}
        </div>
      </div>

      {lints.length > 0 && (
        <div>
          <h3 className="text-[13.5px] font-semibold mb-1.5">Policy notes</h3>
          <div className="flex flex-col gap-1.5">
            {lints.map((l, i) => (
              <div key={i} className="text-[11.5px] rounded-md px-3 py-2" style={l.severity === 'warn'
                ? { color: 'var(--sem-fg)', background: 'color-mix(in srgb, var(--sem-warn, #d7a54a) 12%, transparent)', border: '1px solid color-mix(in srgb, var(--sem-warn, #d7a54a) 35%, transparent)' }
                : { color: 'var(--sem-muted)', background: 'var(--sem-surface-2)', border: '1px solid var(--sem-border)' }}>
                <span aria-hidden>! </span>{l.message}
              </div>
            ))}
          </div>
        </div>
      )}
    </div>
  );
}

// The model-wide enforcement toggle. Turning it OFF shows a consequence preview first (never a silent flip).
function EnforcementCard({ enforced, onSetEnforcement }: { enforced: boolean; onSetEnforcement: (turnOff: boolean) => void }) {
  const [confirming, setConfirming] = useState(false);
  return (
    <Panel>
      <div className="flex items-start gap-3">
        <div className="flex-1">
          <h3 className="text-[13.5px] font-semibold">Model-wide gate enforcement: {enforced ? 'On' : 'Off'}</h3>
          <div className="text-[11.5px] mt-1" style={{ color: 'var(--sem-muted)' }}>Off skips every gate honestly: runs record no verified evidence. Changing this shows a consequence preview first.</div>
        </div>
        {enforced ? (
          confirming ? (
            <div className="flex items-center gap-2 shrink-0">
              <button onClick={() => { onSetEnforcement(true); setConfirming(false); }} className="text-[11.5px] px-2.5 py-1 rounded-md font-semibold" style={{ background: 'var(--sem-warn, #d7a54a)', color: 'var(--sem-on-accent)' }}>Turn off enforcement</button>
              <button onClick={() => setConfirming(false)} className="text-[11px] px-2 py-1 rounded-md" style={{ color: 'var(--sem-muted)' }}>Cancel</button>
            </div>
          ) : (
            <Button onClick={() => setConfirming(true)}>Turn off</Button>
          )
        ) : (
          <Button primary onClick={() => onSetEnforcement(false)}>Turn on</Button>
        )}
      </div>
      {confirming && (
        <div className="mt-3 rounded-md px-3 py-2 text-[11.5px]" style={{ background: 'color-mix(in srgb, var(--sem-warn, #d7a54a) 12%, transparent)', border: '1px solid color-mix(in srgb, var(--sem-warn, #d7a54a) 40%, transparent)', color: 'var(--sem-fg)' }}>
          After this, every gate is skipped across the model and runs record no verified evidence. Per-workflow strictness is ignored until you turn enforcement back on. Existing runs and audit trails are unchanged.
        </div>
      )}
    </Panel>
  );
}

function ProfileCard({ profile, tier, onApply }: { profile: WorkflowProfileInfo; tier: string; onApply: () => Promise<string> }) {
  const isPro = tier === 'pro';
  const proBlocked = profile.pro && !isPro;
  const [preview, setPreview] = useState(false);
  const [busy, setBusy] = useState(false);
  const [note, setNote] = useState<string | null>(null);
  const apply = async () => { setBusy(true); try { setNote(await onApply()); setPreview(false); } catch { /* error surfaces at the section */ } finally { setBusy(false); } };
  return (
    <div className="rounded-xl border p-3.5" style={{ background: 'var(--sem-surface-2)', borderColor: profile.selected ? 'color-mix(in srgb, var(--sem-accent) 45%, transparent)' : 'var(--sem-border)' }}>
      <div className="flex items-center gap-2">
        <b className="text-[13px]">{profile.title}</b>
        {profile.selected && <Pill tint="var(--sem-good)">Current</Pill>}
        {profile.pro && <Pill tint="var(--sem-accent)">Pro</Pill>}
      </div>
      <div className="text-[11.5px] mt-1" style={{ color: 'var(--sem-muted)' }}>{profile.description}</div>
      {preview && (
        <ul className="mt-2 flex flex-col gap-1">
          {profile.effects.map((e, i) => (
            <li key={i} className="text-[11px] flex items-start gap-1.5" style={{ color: 'var(--sem-fg)' }}><span style={{ color: 'var(--sem-accent)' }}>●</span> {e}</li>
          ))}
          <li className="text-[10.5px] mt-1" style={{ color: 'var(--sem-muted)' }}>To undo: pick Solo analyst, or change individual workflow rules in the table below.</li>
        </ul>
      )}
      {note && <div className="text-[11px] mt-2 rounded-md px-2 py-1" style={{ color: 'var(--sem-good)', background: 'color-mix(in srgb, var(--sem-good) 12%, transparent)' }}>✓ {note}</div>}
      {!profile.selected && (
        <div className="flex items-center gap-2 mt-2">
          {!preview ? (
            <button onClick={() => setPreview(true)} className="text-[11px] font-semibold" style={{ color: 'var(--sem-accent)' }}>Preview changes</button>
          ) : (
            <>
              <button disabled={busy || proBlocked} onClick={apply}
                title={proBlocked ? 'Applying a profile that makes a workflow required is a Pro capability. Browsing and previewing are free.' : 'Apply this profile'}
                className="text-[11px] px-2.5 py-1 rounded-md font-semibold disabled:opacity-45" style={{ background: 'var(--sem-accent)', color: 'var(--sem-on-accent)' }}>
                {busy ? 'Applying…' : proBlocked ? 'Apply · Pro' : 'Apply profile'}
              </button>
              <button onClick={() => setPreview(false)} className="text-[11px]" style={{ color: 'var(--sem-muted)' }}>Cancel</button>
            </>
          )}
        </div>
      )}
    </div>
  );
}

// ===================================================================================================
// AUTHOR — a real authoring destination: the outline + focused-editor designer (workflowdesign.tsx). Entry
// points: the sidebar, "Edit workflow" on a playbook, and "+ New playbook" from the Library (creation mode).
// ===================================================================================================
function AuthorSection({ creating, info, def, onNew, onSaved, onDeleted }: {
  creating: boolean; info: WorkflowInfo | null; def: WorkflowDef | null;
  onNew: () => void; onSaved: (n: string) => void; onDeleted: () => void;
}) {
  if (!creating && !info) {
    return (
      <div className="h-full flex items-center justify-center p-8">
        <div className="text-center max-w-sm">
          <div className="text-[15px] font-semibold mb-1">Author a playbook</div>
          <div className="text-[12px]" style={{ color: 'var(--sem-muted)' }}>Open a workflow from the Library and choose Edit workflow, or start a new one from scratch.</div>
          <button onClick={onNew} className="text-[12px] px-3 py-1.5 rounded-lg font-semibold mt-3" style={{ background: 'var(--sem-accent)', color: 'var(--sem-on-accent)' }}>+ New playbook</button>
        </div>
      </div>
    );
  }
  return (
    <DesignMode key={creating ? 'new' : (info?.name ?? 'edit')} info={creating ? null : info} def={creating ? null : def} creating={creating} layout="outline" onSaved={onSaved} onDeleted={onDeleted} />
  );
}

// ---- §9c "Required for" (op→workflow binding / mandatory routing) --------------------------------
// The third axis (availability · REQUIRED · strictness): which authoring actions MUST route through THIS workflow.
// Reads from get_workflow_policy (the bindings, filtered to this workflow) and writes with set_workflow_binding —
// dual-drive with the AI-assistant door, on the same session. Setting hard/warn is Pro (the enforcement is what's
// paid); reading is free, and CLEARING the last mandate on an action is free too (the engine's rule, mirrored here).
const PRO_BIND_REASON = 'Requiring an action to route through a workflow is a Pro capability. Free: read the steps and follow the workflow manually, or curate the menu with the availability switch.';
const LOCKED_BIND_REASON = 'Locked by committed team policy. Change it in .semanticus/workflow-settings.json via review.';

// A policy lint is either about a REQUIREMENT (binding) or about a `when:` RULE (activation). The two live in
// separate controls, so split them by their plain-language phrasing to surface each lint under the right control.
const isActivationLint = (m: string) => /activation rule|a rule shows|a rule hides|but a rule hides it/i.test(m);
const PRO_ACT_REASON = 'Hiding a workflow when a condition holds is a Pro capability. Free: turn the whole workflow on or off with the switch, or edit the activation list in .semanticus/workflow-settings.json.';
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
  const bindings = policy?.bindings ?? [];
  const requiredOps = (policy?.workflows.find((w) => w.name === name)?.requiredForOps) ?? [];
  const flatForOp = (op: string) => bindings.find((b) => b.op === op && b.require.includes(name)) ?? null;
  const lockedOps = new Set(bindings.filter((b) => !b.userDisablable).map((b) => b.op));
  const addable = BINDABLE_OPS.filter((b) => !requiredOps.includes(b.op) && !lockedOps.has(b.op));
  const lints = (policy?.lints ?? []).filter((l) => l.message.includes(name) && !isActivationLint(l.message));

  const addOp = (op: string) => {
    const ex = bindings.find((b) => b.op === op);
    const require = ex ? Array.from(new Set([...ex.require, name])) : [name];
    onSetBinding(op, require, ex?.mode ?? 'hard');
  };
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
            const removeBlocked = locked || (!clears && !isPro);
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
                  title={locked ? LOCKED_BIND_REASON : removeBlocked ? PRO_BIND_REASON : clears ? 'Remove this requirement (clears the binding; free).' : `Stop requiring this workflow for ${opLabel(m.op)} (keeps ${others.length} other).`}
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
            <div key={i} className="text-[10.5px] rounded-md px-2 py-1" style={l.severity === 'warn'
              ? { color: 'var(--sem-fg)', background: 'color-mix(in srgb, var(--sem-warn, #d7a54a) 12%, transparent)', border: '1px solid color-mix(in srgb, var(--sem-warn, #d7a54a) 35%, transparent)' }
              : { color: 'var(--sem-muted)', background: 'var(--sem-surface)', border: '1px solid var(--sem-border)' }}>
              <span aria-hidden>! </span>{l.message}
            </div>
          ))}
        </div>
      )}

      {err && (
        <div className="text-[11.5px] mt-2 rounded-md px-2.5 py-1.5" style={{ color: 'var(--sem-fg)', background: 'color-mix(in srgb, var(--sem-warn, #d7a54a) 12%, transparent)', border: '1px solid color-mix(in srgb, var(--sem-warn, #d7a54a) 35%, transparent)' }}>{err}</div>
      )}
    </div>
  );
}

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
              className="w-full text-left text-[12px] px-3 py-1.5 hover:bg-[var(--sem-surface-2)]" style={{ color: 'var(--sem-fg)' }}>{b.label}</button>
          ))}
        </div>
      )}
    </div>
  );
}

// ---- §10.6 "Hide when" (dynamic activation) ------------------------------------------------------
function HideWhenControl({ info, policy, tier, onSetActivation, err }: {
  info: WorkflowInfo; policy: WorkflowPolicy | null; tier: string;
  onSetActivation: (name: string, when: string, setStr: string) => Promise<void>; err: string | null;
}) {
  const isPro = tier === 'pro';
  const [when, setWhen] = useState('');
  const [wrote, setWrote] = useState(false);
  useEffect(() => { setWhen(''); setWrote(false); }, [info.name]);

  const entry = policy?.workflows.find((w) => w.name === info.name) ?? null;
  const requiredOps = entry?.requiredForOps ?? [];
  const forced = requiredOps.length > 0;
  const off = info.enabled === false;
  const active = info.active !== false;
  const reason = info.activeReason || null;
  const ruleHiding = !active && !off && !forced;

  const stateLine = forced
    ? { text: `Always on the menu, required for ${requiredOps.map(opLabel).join(', ')}. A hide rule here is overridden.`, tone: 'info' as const }
    : off
      ? { text: 'Turned off with the switch. Turn it back on to use a hide-when rule.', tone: 'muted' as const }
      : ruleHiding
        ? { text: `Hidden from the menu right now: ${reason}. You can still start it on demand.`, tone: 'muted' as const }
        : reason
          ? { text: `On the menu now: ${reason}.`, tone: 'muted' as const }
          : { text: 'Shown on the menu now. No hide rule is active now.', tone: 'muted' as const };

  const lints = (policy?.lints ?? []).filter((l) => l.message.includes(info.name) && isActivationLint(l.message));
  // Keep "Clear rule" available whenever a rule is in effect — a rule that is currently SHOWING it (set:on with a
  // reason) is just as committed as one currently hiding it, so both must be clearable. `reason` present (and not
  // the forced/off states, whose reasons are not activation rules) is the signal a rule fired; `wrote` covers a
  // rule we just applied this session. (A committed rule whose `when:` is currently FALSE leaves no trace in the
  // resolved library data, so it can only be cleared while its condition holds or in the settings file.)
  const showClear = (!off && !forced && !!reason) || lints.length > 0 || wrote;

  const insert = (expr: string) => setWhen((cur) => (cur.trim() ? `${cur.trim()} && ${expr}` : expr));
  const applyBlocked = !isPro || !when.trim();
  const apply = async () => { if (applyBlocked) return; await onSetActivation(info.name, when.trim(), 'off'); setWrote(true); };
  const clear = async () => { await onSetActivation(info.name, '', ''); setWrote(false); };

  return (
    <div className="mt-3 rounded-lg border p-3" style={{ background: 'var(--sem-surface-2)', borderColor: 'var(--sem-border)' }}>
      <div className="flex items-center gap-2 flex-wrap">
        <span className="text-[10px] uppercase tracking-wide font-semibold" style={{ color: 'var(--sem-muted)' }}>Hide when</span>
        <div className="flex-1" />
        {showClear && (
          <button type="button" onClick={clear} title="Remove the hide-when rule for this workflow. It shows on the menu normally again (free)."
            className="text-[11px] px-2 py-0.5 rounded-md font-semibold" style={{ background: 'var(--sem-surface)', color: 'var(--sem-muted)', border: '1px solid var(--sem-border)' }}>Clear rule</button>
        )}
      </div>
      <div className="text-[10.5px] mt-1" style={{ color: 'var(--sem-muted)' }}>
        Drop a workflow off the menu while a condition holds, e.g. hide an experimental workflow on the production workspace, or hide a month-end checklist outside its window. It stays startable on demand; this just curates what is on offer.
      </div>

      <div className="text-[11.5px] mt-2 rounded-md px-2.5 py-1.5" style={stateLine.tone === 'info'
        ? { color: 'var(--sem-fg)', background: 'var(--sem-accent-soft)', border: '1px solid color-mix(in srgb, var(--sem-accent) 34%, transparent)' }
        : { color: 'var(--sem-muted)', background: 'var(--sem-surface)', border: '1px solid var(--sem-border)' }}>
        {stateLine.text}
      </div>

      <div className="mt-2.5 flex items-center gap-1.5 flex-wrap">
        <span className="text-[11px] font-semibold shrink-0" style={{ color: 'var(--sem-muted)' }}>Hide it when</span>
        <input value={when} onChange={(e) => setWhen(e.target.value)} spellCheck={false}
          placeholder="a condition, e.g. connection.workspace ~ '*prod*'"
          onKeyDown={(e) => { if (e.key === 'Enter') void apply(); }}
          className="flex-1 min-w-[180px] text-[11.5px] px-2 py-1 rounded-md outline-none"
          style={{ background: 'var(--sem-surface)', color: 'var(--sem-fg)', border: '1px solid var(--sem-border)', fontFamily: 'ui-monospace,SFMono-Regular,Consolas,monospace' }} />
        <button type="button" disabled={applyBlocked}
          title={!isPro ? PRO_ACT_REASON : !when.trim() ? 'Type a condition first. Use an example below to start.' : 'Save this rule. It takes effect on both doors immediately.'}
          onClick={() => void apply()}
          className="text-[11px] px-2 py-1 rounded-md font-semibold disabled:opacity-45"
          style={{ background: 'var(--sem-accent-soft)', color: 'var(--sem-accent)', border: '1px solid color-mix(in srgb, var(--sem-accent) 40%, transparent)' }}>
          Apply rule{!isPro ? ' · Pro' : ''}
        </button>
      </div>

      <div className="flex items-center gap-1 mt-2 flex-wrap">
        <span className="text-[10px]" style={{ color: 'var(--sem-muted)' }}>Hide when:</span>
        {ACTIVATION_EXAMPLES.map((ex) => (
          <button key={ex.expr} type="button" onClick={() => insert(ex.expr)} title={ex.expr}
            className="text-[10px] px-1.5 py-0.5 rounded-full" style={{ background: 'var(--sem-surface)', color: 'var(--sem-fg)', border: '1px solid var(--sem-border)' }}>{ex.label}</button>
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
            <div key={i} className="text-[10.5px] rounded-md px-2 py-1" style={l.severity === 'warn'
              ? { color: 'var(--sem-fg)', background: 'color-mix(in srgb, var(--sem-warn, #d7a54a) 12%, transparent)', border: '1px solid color-mix(in srgb, var(--sem-warn, #d7a54a) 35%, transparent)' }
              : { color: 'var(--sem-muted)', background: 'var(--sem-surface)', border: '1px solid var(--sem-border)' }}>
              <span aria-hidden>! </span>{l.message}
            </div>
          ))}
        </div>
      )}

      {err && (
        <div className="text-[11.5px] mt-2 rounded-md px-2.5 py-1.5" style={{ color: 'var(--sem-fg)', background: 'color-mix(in srgb, var(--sem-warn, #d7a54a) 12%, transparent)', border: '1px solid color-mix(in srgb, var(--sem-warn, #d7a54a) 35%, transparent)' }}>{err}</div>
      )}
    </div>
  );
}

// ---- overview rail (playbook steps, no run) ------------------------------------------------------
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
          <div className="flex items-center gap-1 mt-1.5 flex-wrap">{step.ops.map((op) => <OpChip key={op} op={op} />)}</div>
        )}
        {gateBits.length > 0 && (
          <div className="text-[11px] mt-1.5" style={{ color: 'var(--sem-muted)' }}>{gateBits.join(' · ')}</div>
        )}
        <button onClick={() => setOpen((o) => !o)} className="mt-1.5 flex items-center gap-1 text-[11px] font-medium" style={{ color: 'var(--sem-muted)' }}>
          <span className="inline-block transition-transform text-[9px]" style={{ transform: open ? 'rotate(90deg)' : 'none' }}>▶</span>
          {open ? 'Hide instructions' : 'Instructions'}
        </button>
        {open && <Markdown md={step.instructions} className="mt-1.5" />}
      </div>
    </div>
  );
}

// ---- run rail (a run is active or terminal) ------------------------------------------------------
// Actions carry THIS run's id up to the shell so a switch to another live run never targets the wrong run.
function RunRail({ run, onSubmit, onSkip, onAbort }: {
  run: WorkflowRunView;
  onSubmit: (runId: string, stepId: string, answersJson: string) => Promise<void>;
  onSkip: (runId: string, stepId: string, reason: string) => Promise<void>; onAbort: (runId: string, reason: string) => Promise<void>;
}) {
  const rid = run.runId;
  return (
    <Panel>
      <SectionTitle>Run <span style={{ color: 'var(--sem-muted)' }}>· {run.steps.filter((s) => s.status === 'passed').length}/{run.totalSteps} passed</span></SectionTitle>
      <div className="relative mt-2">
        <RailLine />
        <div className="flex flex-col">
          {run.steps.map((s, i) => (
            <RunStep key={s.stepId} n={i + 1} result={s}
              current={run.status === 'active' && run.currentStep?.stepId === s.stepId ? run.currentStep : null}
              onSubmit={(stepId, json) => onSubmit(rid, stepId, json)}
              onSkip={(stepId, reason) => onSkip(rid, stepId, reason)} onAbort={(reason) => onAbort(rid, reason)} />
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
  const notable = !!result.note
    || Object.values(result.answers).some((a) => a.declined)
    || result.verifyResults.some((v) => v.status !== 'passed');
  const [open, setOpen] = useState(notable);
  const hasRecord = !!result.note || Object.keys(result.answers).length > 0 || result.verifyResults.length > 0;
  return (
    <div className="relative flex items-start gap-3 py-2.5">
      <StatusNode status={result.status} />
      <div className="min-w-0 flex-1">
        <div className="flex items-center gap-2 flex-wrap">
          <span className="text-[10px] tnum" style={{ color: 'var(--sem-muted)' }}>{n}</span>
          <span className="text-[12.5px] font-semibold" style={{ color: current || done ? 'var(--sem-fg)' : 'var(--sem-muted)' }}>{result.title}</span>
          <span className="text-[9px] uppercase tracking-wide font-semibold px-1.5 py-0.5 rounded-full" style={{ color: st.color, background: `color-mix(in srgb, ${st.color} 16%, transparent)` }}>{st.label}</span>
          {result.effectiveStrictness && done && (
            <span className="text-[9.5px]" style={{ color: 'var(--sem-muted)' }} title="The strictness this gate actually ran at">Ran at {stepGateSkipped(result.effectiveStrictness) ? 'skipped (gate off)' : uiLabel(result.effectiveStrictness).toLowerCase()}</span>
          )}
        </div>

        {/* every completed step is reopenable — a clean pass must still be inspectable, not just the notable ones */}
        {done && (
          <>
            <button onClick={() => setOpen((o) => !o)} className="mt-1 flex items-center gap-1 text-[11px] font-medium" style={{ color: 'var(--sem-muted)' }}>
              <span className="inline-block transition-transform text-[9px]" style={{ transform: open ? 'rotate(90deg)' : 'none' }}>▶</span>
              {open ? 'Hide record' : 'Record'}
            </button>
            {open && (
              <div className="mt-1.5 flex flex-col gap-2">
                {result.note && <div className="text-[11px] px-2 py-1 rounded" style={{ background: 'var(--sem-surface-2)', color: 'var(--sem-muted)' }}>{result.note}</div>}
                {Object.entries(result.answers).map(([nm, a]) => <AnswerRow key={nm} name={nm} a={a} />)}
                {result.verifyResults.length > 0 && (
                  <div className="flex flex-wrap gap-1.5">{result.verifyResults.map((v, i) => <VerifyChip key={v.kind + i} v={v} />)}</div>
                )}
                {!hasRecord && <div className="text-[11px]" style={{ color: 'var(--sem-muted)' }}>No questions or checks were recorded for this step.</div>}
              </div>
            )}
          </>
        )}

        {current && (
          <CurrentStepCard current={current}
            onSubmit={(json) => onSubmit(current.stepId, json)}
            onSkip={(reason) => onSkip(current.stepId, reason)} onAbort={onAbort} />
        )}
      </div>
    </div>
  );
}

function CurrentStepCard({ current, onSubmit, onSkip, onAbort }: {
  current: CurrentStepView; onSubmit: (answersJson: string) => Promise<void>;
  onSkip: (reason: string) => Promise<void>; onAbort: (reason: string) => Promise<void>;
}) {
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
      setErr(String((e as Error).message ?? e));
    } finally { setBusy(false); }
  };

  // Render from THIS run's frozen strictness snapshot (effectiveStrictness), never the current model-wide toggle:
  // a mid-run enforcement flip can't tear a run, so the UI must show what the engine will actually do.
  const gateSkipped = stepGateSkipped(current.effectiveStrictness);
  const strictnessLabel = current.effectiveStrictness && !gateSkipped
    ? (current.effectiveStrictness === 'hard' ? 'Enforcement on · blocking' : current.effectiveStrictness === 'warn' ? 'Enforcement on · warns' : uiLabel(current.effectiveStrictness))
    : null;

  return (
    <div className="mt-2 rounded-lg border p-3" style={{ background: 'var(--sem-surface-2)', borderColor: 'color-mix(in srgb, var(--sem-accent) 40%, transparent)' }}>
      {strictnessLabel && (
        <div className="mb-2 inline-block text-[9px] uppercase tracking-wide font-semibold px-1.5 py-0.5 rounded-full" style={{ color: 'var(--sem-accent)', background: 'var(--sem-accent-soft)', border: '1px solid color-mix(in srgb, var(--sem-accent) 40%, transparent)' }}>{strictnessLabel}</div>
      )}
      <Markdown md={current.instructions} />

      {current.ops.length > 0 && (
        <div className="flex items-center gap-1 mt-2.5 flex-wrap">
          <span className="text-[10px] uppercase tracking-wide font-semibold mr-1" style={{ color: 'var(--sem-muted)' }}>actions</span>
          {current.ops.map((op) => <OpChip key={op} op={op} />)}
        </div>
      )}

      {/* enforcement off (for THIS run) REPLACES the gate form with the honest skipped state — no half-shown
          questions with a notice tacked on. Enforced: the gate form (questions + the checks the engine will run). */}
      {gateSkipped ? (
        <div className="mt-3 rounded-md px-2.5 py-2 text-[11.5px]" style={{ background: 'var(--sem-surface)', border: '1px solid color-mix(in srgb, var(--sem-warn, #d7a54a) 35%, transparent)', color: 'var(--sem-fg)' }}>
          <div className="font-semibold" style={{ color: 'var(--sem-warn, #d7a54a)' }}>Gate skipped</div>
          <div className="mt-0.5" style={{ color: 'var(--sem-muted)' }}>This step’s gate is off, so its questions and checks are skipped and nothing is recorded as verified for it. Submit to advance the run.</div>
        </div>
      ) : (
        <>
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
                    <span style={{ color: 'var(--sem-muted)' }}>○</span> {uiLabel(k)}
                  </span>
                ))}
              </div>
            </div>
          )}
        </>
      )}

      {err && (
        <div className="mt-3 rounded-lg px-3 py-2 text-[12px]" style={{ background: 'color-mix(in srgb, var(--sem-bad) 12%, transparent)', color: 'var(--sem-bad)', border: '1px solid color-mix(in srgb, var(--sem-bad) 34%, transparent)' }}>{err}</div>
      )}

      <div className="mt-3 flex items-center gap-2">
        <Button primary disabled={busy} onClick={doSubmit}>{busy ? 'Submitting…' : 'Submit step'}</Button>
        <ReasonButton label="Skip with reason" title="Skip this step (a reason is required)." onConfirm={onSkip} />
        <ReasonButton label="Abort" title="Abort the whole run (a reason is required)." onConfirm={onAbort} danger />
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
        <label className="text-[12px] flex-1" style={{ color: 'var(--sem-fg)' }}>{q.question || uiLabel(q.name)}</label>
        <span className="shrink-0 text-[9px] tnum px-1.5 py-0.5 rounded" style={{ background: 'var(--sem-surface-2)', color: 'var(--sem-muted)' }}>{uiLabel(q.type)}</span>
        {required && <span className="shrink-0 text-[9px] uppercase tracking-wide font-semibold px-1.5 py-0.5 rounded" style={{ color: 'var(--sem-bad)', background: 'color-mix(in srgb, var(--sem-bad) 14%, transparent)' }}>required</span>}
      </div>
      {!declined && (
        <textarea value={value} onChange={(e) => onValue(e.target.value)} rows={q.type === 'text' ? 2 : 1} spellCheck={false} placeholder={uiLabel(q.name)}
          className="mt-1.5 w-full text-[12px] px-2 py-1 rounded-md outline-none resize-y"
          style={{ background: 'var(--sem-surface-2)', color: 'var(--sem-fg)', border: '1px solid var(--sem-border)' }} />
      )}
      <div className="mt-1.5 flex items-center gap-2">
        <label className="flex items-center gap-1.5 text-[11px] cursor-pointer" style={{ color: 'var(--sem-muted)' }}>
          <input type="checkbox" checked={declined} onChange={(e) => onDecline(e.target.checked)} />
          Decline to answer and record a reason
        </label>
        {declined && !required && <span className="text-[10px]" style={{ color: 'var(--sem-muted)' }}>Recorded as an explicit decline (auditable).</span>}
      </div>
      {declined && (
        <textarea value={reason} onChange={(e) => onReason(e.target.value)} rows={2} spellCheck={false} placeholder="Why are you declining this question? (required)"
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
        <span className="font-semibold" style={{ color: 'var(--sem-warn)' }}>{uiLabel(name)} · declined</span>
        {a.declineReason && <span style={{ color: 'var(--sem-fg)' }}>: {a.declineReason}</span>}
      </div>
    );
  }
  return (
    <div className="text-[11px] px-2 py-1.5 rounded" style={{ background: 'var(--sem-surface-2)' }}>
      <span className="font-semibold" style={{ color: 'var(--sem-fg)' }}>{uiLabel(name)}</span>
      <span style={{ color: 'var(--sem-muted)' }}> · {isAnswered(a) ? a.value : '(no value)'}</span>
    </div>
  );
}

function VerifyChip({ v }: { v: VerifyResult }) {
  const color = verifyColor(v.status);
  // unavailable is BLOCKED-with-a-reason (amber '!'), distinct from the muted not-run glyph — the two must never read alike.
  const glyph = v.status === 'passed' ? '✓' : v.status === 'failed' ? '✕' : v.status === 'unavailable' ? '!' : '⤼';
  const tip = v.status === 'unavailable' && v.missing ? `Missing: ${v.missing}${v.detail ? `
${v.detail}` : ''}` : v.detail;
  return (
    <span title={tip} className="text-[10px] tnum px-1.5 py-0.5 rounded flex items-center gap-1"
      style={{ color, background: `color-mix(in srgb, ${color} 14%, transparent)`, border: `1px solid color-mix(in srgb, ${color} 34%, transparent)` }}>
      <span>{glyph}</span> {uiLabel(v.kind)}{v.status === 'unavailable' ? ' (blocked)' : ''}
    </span>
  );
}

// ---- small primitives ----------------------------------------------------------------------------
// The small on/off switch shared by the library rows, playbook header, and governance table. Stops propagation so
// flipping it never also opens a row. Muted when off (calm), accent when on. label = the accessible name.
function AvailabilitySwitch({ on, label, title, onToggle }: { on: boolean; label: string; title: string; onToggle: () => void }) {
  return (
    <button type="button" role="switch" aria-checked={on} aria-label={label} title={title}
      onClick={(e) => { e.stopPropagation(); onToggle(); }}
      className="shrink-0 relative rounded-full transition-colors"
      style={{ width: 26, height: 15, background: on ? 'var(--sem-accent)' : 'var(--sem-surface)', border: `1px solid ${on ? 'var(--sem-accent)' : 'var(--sem-border)'}` }}>
      <span className="absolute rounded-full transition-transform" style={{ top: 1.5, left: 1.5, width: 10, height: 10, background: on ? 'var(--sem-on-accent)' : 'var(--sem-muted)', transform: on ? 'translateX(11px)' : 'none' }} />
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
    <div className="relative z-10 shrink-0 w-8 h-8 rounded-full flex items-center justify-center text-[13px] font-bold" title={st.label}
      style={filled ? { background: st.color, color: '#fff' } : { background: 'var(--sem-surface-2)', color: st.color, border: `2px solid color-mix(in srgb, ${st.color} 60%, transparent)` }}>
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
      style={tint ? { color: tint, background: `color-mix(in srgb, ${tint} 14%, transparent)` } : { color: 'var(--sem-muted)', background: 'var(--sem-surface)', border: '1px solid var(--sem-border)' }}>
      {children}
    </span>
  );
}
function Markdown({ md, className = '' }: { md: string; className?: string }) {
  return <div className={`wf-md text-[12px] ${className}`} style={{ color: 'var(--sem-fg)' }} dangerouslySetInnerHTML={{ __html: mdToHtml(md || '') }} />;
}

// A button that reveals an inline reason field (skip / abort both require a reason) — no modal, stays in flow.
function ReasonButton({ label, title, onConfirm, danger }: { label: string; title?: string; onConfirm: (reason: string) => void | Promise<void>; danger?: boolean }) {
  const [open, setOpen] = useState(false);
  const [reason, setReason] = useState('');
  const [busy, setBusy] = useState(false);
  const color = danger ? 'var(--sem-bad)' : 'var(--sem-fg)';
  if (!open) {
    return (
      <button onClick={() => setOpen(true)} title={title} className="text-[12px] px-3 py-1.5 rounded-lg font-medium whitespace-nowrap"
        style={{ background: 'var(--sem-surface-2)', color, border: '1px solid var(--sem-border)' }}>{label}</button>
    );
  }
  const confirm = async () => {
    if (!reason.trim()) return;
    setBusy(true);
    try { await onConfirm(reason.trim()); setOpen(false); setReason(''); } finally { setBusy(false); }
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
