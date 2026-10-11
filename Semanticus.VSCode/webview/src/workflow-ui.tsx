import { useEffect, useState } from 'react';
import { rpc } from './bridge';
import { mdToHtml } from './docrender';
import { opTitle } from './copy';
import { Caret } from './ui';

// The wire shapes and the small shared pieces of the Workflows surface (lane W2 moved them here from workflows.tsx
// when the page became one list and Author). workflows.tsx re-exports every name, so the files that import them
// from there (dataagent, permissions, the canvas, the forms, Author) are unchanged.

// ---- wire shapes (camelCase, mirror Semanticus.Engine/Workflow.cs) --------------------------------
export interface WorkflowInfo {
  name: string; title: string; description: string; version: number; source: string;
  stepCount: number; gated: boolean; triggers: string[]; error?: string | null;
  whenToUse?: string | null;
  // §9a availability (the manual per-workflow kill switch) + §10.6 activation (rule-driven menu curation).
  // enabled:false = can't be started until turned back on; active:false while enabled = off today's menu but
  // still startable. Both stay LISTED (the honesty rule): a hidden workflow can't be re-enabled. Optional so
  // absent reads as the engine default (on), every check is an explicit `=== false`.
  enabled?: boolean; active?: boolean; activeReason?: string | null;
  // Contract 21.2 (lane W1): a shipped file Use copies; a team rule names it; your copy shadows a required one.
  starter?: boolean; required?: boolean; replacesRequired?: boolean;
}
export interface GateInput { name: string; question: string; type: string; required: string }
export interface VerifySpec { kind: string; when?: string; probe?: string; scope?: string; intent?: string }
export interface GateSpec { strictness?: string | null; inputs: GateInput[]; verify: VerifySpec[] }
export interface ForEachSpec {
  inLiteral?: string[] | null;
  inInput?: string | null;
  as?: string;
  maxIterations?: number;
}
export interface CallSpec {
  workflow?: string;
  with?: Record<string, string>;
  returns?: string[];
}
export interface WorkflowStep {
  id: string; number: number; title: string; instructions: string; gate?: GateSpec | null; ops: string[];
  when?: string | null;
  forEach?: ForEachSpec | null;
  call?: CallSpec | null;
  hasExplicitId?: boolean;
  // Format 3 (composer contract 1.3 to 1.7): who does the step, its phase, what Semanticus runs, the questions,
  // the parts the assistant writes with their Brief, and the checks with the phase each runs in.
  performer?: string | null; phase?: string | null; action?: { op?: string } | null;
  questions?: { name: string; question: string; type?: string; by?: string; values?: string[] | null; default?: string | null }[] | null;
  blanks?: { name: string; kind?: string; fills?: string | null; by?: string; generator?: string | null }[] | null;
  brief?: string | null;
  checks?: { kind: string; phase?: string | null; target?: string | null; min?: string | null; max?: string | null; of?: string[] | null; when?: string | null }[] | null;
}
export interface WorkflowDef {
  name: string; title: string; description: string; version: number; strictness?: string;
  triggers: string[]; source: string; filePath?: string; error?: string | null; steps: WorkflowStep[];
  provenance?: Record<string, string>;   // unknown frontmatter keys (a distilled workflow carries derived_from here)
  schemaVersion?: number;
  kind?: string | null;
  whenToUse?: string | null;
  tags?: string[];
  slots?: { name: string; question: string }[];
  /** Format 3 "Notes for your assistant" and "Inputs asked each run". */
  assistantNotes?: string | null;
  inputs?: { name: string; question: string; type?: string; by?: string; values?: string[] | null; default?: string | null }[] | null;
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
export interface AnchorLockView { anchorsInput: string; initialHash: string; currentHash: string; stepId: string }
export interface AnchorRevisionChange {
  context: string; originalExpect: string; correctedExpect: string; extractQuery: string;
  extractRowCount: number; extractTruncated: boolean; extractResultHash: string;
}
export interface AnchorRevision {
  key?: string | null; anchorsInput: string; beforeHash: string; afterHash: string; stepId: string;
  timestampUtc: string; changes: AnchorRevisionChange[];
}
export interface ShapeMismatchLedgerEntry {
  shapeId: string; open: boolean; state: string; totalMismatchCount: number; lastMismatchCount: number;
  lastMismatchReconciledCount: number;
  cells: { coordinate: string; displayContext: string; candidateValue: string; witnessValue: string }[];
}
export interface ShapeMismatchCountersign {
  shapeId: string; coordinate: string; candidateValue: string; witnessValue: string;
  stated: string; stepId: string; timestampUtc: string;
}
export interface StepResult {
  stepId: string; title: string; status: string; note?: string | null;
  answers: Record<string, AnswerValue>; verifyResults: VerifyResult[]; effectiveStrictness?: string | null;
}
export interface CurrentStepView {
  stepId: string; title: string; instructions: string; questions: GateInput[];
  verifyKinds: string[]; effectiveStrictness?: string | null; ops: string[];
  providedAnswers?: Record<string, AnswerValue> | null;
  /** F12: the questions Semanticus reads as data, which the assistant fills (Workflow.cs CurrentStepView). */
  assistantFills?: string[] | null;
  handOff?: { callee?: string; calleeTitle?: string; expectedReturns?: string[]; nextStepTitle?: string; gateOff?: boolean } | null;
}
export interface WorkflowRunFrameProof {
  stepId: string; state: string; steps: StepResult[];
  parentFrameIndex?: number | null;
  witnessLocks?: WitnessLockView[]; witnessRevisions?: WitnessRevision[]; partitionRevisions?: PartitionRevision[];
  anchorLocks?: AnchorLockView[]; anchorRevisions?: AnchorRevision[];
  shapeMismatchLedger?: ShapeMismatchLedgerEntry[]; shapeMismatchCountersigns?: ShapeMismatchCountersign[];
}
export interface WorkflowIterationFrame extends WorkflowRunFrameProof {
  kind: 'iteration'; iterationIndex: number; loopVariable: string; loopValue: string;
}
export interface WorkflowCallFrame extends WorkflowRunFrameProof {
  kind: 'call'; workflow: string; depth: number; passed: string[]; returned: string[]; returnNote?: string | null;
}
export type WorkflowRunFrame = WorkflowIterationFrame | WorkflowCallFrame;
export interface WorkflowRunView {
  runId: string; workflow: string; title: string; workflowVersion: number; status: string; abortReason?: string | null;
  /** The draft this run is the guided sub-run of (lane l9-finish). */
  draftId?: string | null;
  startedUtc?: string | null; finishedUtc?: string | null; modelName?: string | null; modelFingerprint?: string | null;
  stepIndex: number; totalSteps: number; totalStepsProvisional?: boolean | null; steps: StepResult[]; frames?: WorkflowRunFrame[]; currentStep?: CurrentStepView | null;
  witnessLocks?: WitnessLockView[] | null; witnessRevisions?: WitnessRevision[] | null; partitionRevisions?: PartitionRevision[] | null;
}
/** The run's own count of its steps, in the words each step reads (F14): "1 done · 3 skipped", or
 *  "1 passed · 1 done · 2 to go" while it runs. It used to say "0 of 4 steps passed" beside a step marked done,
 *  because only a passed check was counted. Passed means a check passed; done means finished with nothing to check. */
export function runTally(run: WorkflowRunView): string {
  const count = (status: string) => run.steps.filter((s) => s.status === status).length;
  const parts: string[] = [];
  const add = (n: number, words: string) => { if (n > 0) parts.push(`${n} ${words}`); };
  add(count('passed'), 'passed');
  add(count('done'), 'done');
  add(count('failed'), 'failed');
  add(count('skipped'), 'skipped');
  add(count('not_applicable'), 'did not apply');
  const settled = ['passed', 'done', 'failed', 'skipped', 'not_applicable'].reduce((n, st) => n + count(st), 0);
  // A total still provisional (a loop not yet expanded) is a floor, so what is left is "at least N", like totalStepsText.
  const left = Math.max(run.totalSteps, run.steps.length) - settled;
  if (run.status === 'active' && left > 0) parts.push(run.totalStepsProvisional ? `at least ${left} to go` : `${left} to go`);
  return parts.length ? parts.join(' · ') : run.status === 'active' ? 'not started' : 'no steps ran';
}

export function totalStepsText(run: WorkflowRunView) {
  return run.totalStepsProvisional ? `at least ${run.totalSteps} steps` : `${run.totalSteps} steps`;
}
export interface WorkflowEnforcement { mode?: string | null; enforced: boolean; note?: string | null }

// §9c op→workflow BINDING (mandatory routing), the whole project policy in one read (get_workflow_policy).
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
export interface WorkflowProfileInfo {
  name: string; title: string; description: string; effects: string[]; pro: boolean; selected: boolean;
}
export interface WorkflowProfileResult { activeProfile: string; workflows: WorkflowInfo[]; note?: string | null }

// The v1 bindable authoring chokepoints (the EnforceBindingAsync call sites in LocalEngine.cs), the ONLY ops a
// binding can route, offered in a fixed order. Labelled in plain language (the "UI never speaks engine" rule):
// the analyst picks "New measure", never the raw op id. An unwired op can't be bound, so only these appear.
export const BINDABLE_OPS: { op: string; label: string }[] = [
  { op: 'create_measure', label: 'New measure' },
  { op: 'update_measure', label: 'Edit measure DAX' },
  { op: 'create_table', label: 'New table' },
  { op: 'create_calculated_column', label: 'New calculated column' },
  { op: 'create_relationship', label: 'New relationship' },
  { op: 'create_calculation_item', label: 'New calculation item' },
];
export const opLabel = (op: string) => BINDABLE_OPS.find((b) => b.op === op)?.label ?? opTitle(op);

// "hard" / "warn" / "inherit" are engine words. A person reads what actually happens to their run.
export function strictnessWords(s?: string | null): string {
  return s === 'hard' ? 'must pass' : s === 'warn' ? 'warn only' : 'as the workflow says';
}

// One plain sentence for a step, built from what the step really is: who does the work, what it asks you,
// and how many checks it records. The workflow's own instructions are written for the assistant, so they
// are never the sentence a person reads first, they sit behind a disclosure.
export function stepSummary(ops: string[], gate?: GateSpec | null): string {
  const parts: string[] = [];
  parts.push(ops.length
    ? `Your assistant does the work: ${ops.map((op) => opTitle(op).toLowerCase()).join(', ')}.`
    : 'Your assistant does this part.');
  const asks = gate?.inputs.length ?? 0;
  if (asks) parts.push(`It asks you ${asks} question${asks === 1 ? '' : 's'} first.`);
  const checks = gate?.verify.length ?? 0;
  if (checks) parts.push(`${checks} check${checks === 1 ? '' : 's'} ${checks === 1 ? 'is' : 'are'} recorded before the run moves on.`);
  return parts.join(' ');
}

// The engine's op catalog, read once per session. It carries a one-sentence description per op, which is
// the honest explanation to hang on a chip; the short title comes from opTitle.
let opHintCache: Promise<Record<string, string>> | null = null;
export function opHints(): Promise<Record<string, string>> {
  opHintCache ??= rpc<{ name: string; description: string }[]>('getOpCatalog')
    .then((list) => Object.fromEntries((list ?? []).map((o) => [o.name, o.description])))
    .catch(() => ({}));
  return opHintCache;
}

// The step-node visual language, one glyph + tint per run status (mirrors Edit History's verdict rail).
// Overview mode (no run) uses the neutral 'todo' style with the step number in the circle.
export const STEP_STYLE: Record<string, { glyph: string; color: string; label: string }> = {
  passed: { glyph: '✓', color: 'var(--sem-good)', label: 'passed' },
  done: { glyph: '·', color: 'var(--sem-muted)', label: 'done' },
  not_applicable: { glyph: '◇', color: 'var(--sem-muted)', label: 'did not apply' },
  skipped: { glyph: '⤼', color: 'var(--sem-muted)', label: 'skipped' },
  failed: { glyph: '✕', color: 'var(--sem-bad)', label: 'failed' },
  in_progress: { glyph: '▶', color: 'var(--sem-accent)', label: 'current' },
  pending: { glyph: '○', color: 'var(--sem-muted)', label: 'pending' },
};
export const stepStyle = (s: string) => STEP_STYLE[s] ?? STEP_STYLE.pending;

// Verify-evidence chip tint. passed = good, failed = bad, unavailable = AMBER (blocked: applicable but no
// authoritative evidence, never conflated with the muted, legitimately-not-run styles), not_applicable/skipped = muted.
export const VERIFY_COLOR: Record<string, string> = {
  passed: 'var(--sem-good)', failed: 'var(--sem-bad)',
  unavailable: 'var(--sem-warn)',
  not_applicable: 'var(--sem-muted)', skipped: 'var(--sem-muted)',
};
export const verifyColor = (s: string) => VERIFY_COLOR[s] ?? 'var(--sem-muted)';

export const isDeclined = (a: AnswerValue) => a.declined === true;
export const isAnswered = (a: AnswerValue) => !a.declined && a.value != null && a.value !== '';

// A run belongs to a section only while it is worth showing. Terminal keeps its evidence reachable.
export const isTerminal = (r: WorkflowRunView | null) => !!r && r.status !== 'active';

// ---- small primitives ----------------------------------------------------------------------------
// The small on/off switch shared by the library rows, playbook header, and governance table. Stops propagation so
// flipping it never also opens a row. Muted when off (calm), accent when on. label = the accessible name.
export function AvailabilitySwitch({ on, label, title, onToggle }: { on: boolean; label: string; title: string; onToggle: () => void }) {
  return (
    <button type="button" role="switch" aria-checked={on} aria-label={label} title={title}
      onClick={(e) => { e.stopPropagation(); onToggle(); }}
      className="shrink-0 relative rounded-full transition-colors"
      style={{ width: 32, height: 'var(--sem-chip-h)', background: on ? 'var(--sem-accent)' : 'var(--sem-surface)', border: `1px solid ${on ? 'var(--sem-accent)' : 'var(--sem-border)'}` }}>
      <span className="absolute rounded-full transition-transform" style={{ top: 2, left: 2, width: 14, height: 14, background: on ? 'var(--sem-on-accent)' : 'var(--sem-muted)', transform: on ? 'translateX(12px)' : 'none' }} />
    </button>
  );
}

export function RailLine() {
  return <div className="absolute top-1 bottom-1 w-px" style={{ left: 15, background: 'var(--sem-border)' }} />;
}
export function NumberNode({ n }: { n: number }) {
  return (
    <div className="relative z-10 shrink-0 w-8 h-8 rounded-full flex items-center justify-center text-(length:--sem-text-meta) sem-num"
      style={{ background: 'var(--sem-ground)', color: 'var(--sem-fg)', border: '1px solid var(--sem-border)' }}>{n}</div>
  );
}
export function StatusNode({ status }: { status: string }) {
  const st = stepStyle(status);
  // An outline in the state's colour, the glyph in the text colour: a filled disc with white on it failed contrast
  // on the lighter state colours.
  return (
    <div className="relative z-10 shrink-0 w-8 h-8 rounded-full flex items-center justify-center text-(length:--sem-text-body)" title={st.label}
      style={{ background: 'var(--sem-ground)', color: 'var(--sem-fg)', border: `1px solid ${st.color}` }}>
      {st.glyph}
    </div>
  );
}
// The assistant's own instructions, collapsed. They are written for the assistant (tool names, conventions,
// journey references), so they are never the first thing a person reads, but they must stay reachable.
export function AssistantInstructions({ md, open, onToggle, className = 'mt-2' }: { md: string; open: boolean; onToggle: () => void; className?: string }) {
  if (!md?.trim()) return null;
  return (
    <>
      <button onClick={onToggle} aria-expanded={open} className={`${className} flex items-center gap-1 text-(length:--sem-text-label) font-medium`} style={{ color: 'var(--sem-muted)' }}>
        <Caret open={open} />
        Instructions for your assistant
      </button>
      {open && <Markdown md={md} className="mt-2" />}
    </>
  );
}

export function OpChip({ op, onStage }: { op: string; onStage?: (id: string) => void }) {
  const label = opTitle(op);
  const [hint, setHint] = useState<string | null>(null);
  useEffect(() => { let live = true; void opHints().then((m) => { if (live) setHint(m[op] ?? null); }); return () => { live = false; }; }, [op]);
  const tip = hint ? `${hint} Your assistant does that part.` : 'Your assistant does that part.';
  const clickable = op === 'add_plan_item' || op === 'get_plan';
  const go = async () => {
    if (op === 'get_plan') {
      window.dispatchEvent(new CustomEvent('semanticus-open-tab', { detail: 'Pending changes' }));
      return;
    }
    if (op === 'add_plan_item' && onStage) {
      const plan = await rpc<{ items?: { id: string; objRef?: string; after?: string }[] }>('getPlan');
      const first = plan.items?.[0];
      if (!first?.objRef) return;
      const next = await rpc<{ items?: { id: string }[] }>('addPlanItem', first.objRef, 'rename', first.after ?? '', first.objRef, null, null);
      const id = next.items?.[next.items.length - 1]?.id;
      if (id) onStage(id);
    }
  };
  if (!clickable) {
    return (
      <span title={tip} className="sem-chip sem-chip-outline">{label}</span>
    );
  }
  return (
    <button type="button" title={tip} onClick={() => void go()} className="sem-chip sem-chip-outline">{label}</button>
  );
}
// The state dot, in the state's colour. An svg rather than a pseudo-element, so the dot is part of the line's
// content and a line that starts with one starts at the card's padding.
export function Dot({ color }: { color: string }) {
  return <svg aria-hidden className="sem-wf-dot" viewBox="0 0 8 8"><circle cx="4" cy="4" r="4" style={{ fill: color }} /></svg>;
}
// A state is a word, with a dot in its colour when it has one. No pill, no fill.
export function Pill({ children, tint, title }: { children: React.ReactNode; tint?: string; title?: string }) {
  return (
    <span title={title} className="sem-chip">
      {tint && <Dot color={tint} />}
      {children}
    </span>
  );
}
export function Markdown({ md, className = '' }: { md: string; className?: string }) {
  return <div className={`wf-md text-(length:--sem-text-meta) ${className}`} style={{ color: 'var(--sem-fg)' }} dangerouslySetInnerHTML={{ __html: mdToHtml(md || '') }} />;
}

// A button that reveals an inline reason field (skip / abort both require a reason), no modal, stays in flow.
/** A button that asks why before it acts. The box asks one plain question (F13: it used to read "Skip with reason
 *  reason…"); callers name it, and the default is the plain "Say why". defaultOpen is for a render test. */
export function ReasonButton({ label, title, onConfirm, danger, placeholder, defaultOpen }: {
  label: string; title?: string; onConfirm: (reason: string) => void | Promise<void>; danger?: boolean; placeholder?: string; defaultOpen?: boolean;
}) {
  const [open, setOpen] = useState(!!defaultOpen);
  const [reason, setReason] = useState('');
  const [busy, setBusy] = useState(false);
  const color = danger ? 'var(--sem-bad)' : 'var(--sem-fg)';
  if (!open) {
    return (
      <button onClick={() => setOpen(true)} title={title} className="sem-btn">{label}</button>
    );
  }
  const confirm = async () => {
    if (!reason.trim()) return;
    setBusy(true);
    try { await onConfirm(reason.trim()); setOpen(false); setReason(''); } finally { setBusy(false); }
  };
  return (
    <span className="flex items-center gap-2">
      <input value={reason} onChange={(e) => setReason(e.target.value)} autoFocus placeholder={placeholder ?? 'Say why'} spellCheck={false}
        className="sem-field sem-field-sm w-52" />
      <button onClick={confirm} disabled={busy || !reason.trim()} className="sem-btn sem-btn-sm">{busy ? '…' : label}</button>
      <button onClick={() => { setOpen(false); setReason(''); }} className="sem-btn sem-btn-sm sem-btn-quiet">Cancel</button>
    </span>
  );
}

export function ErrorPanel({ name, error }: { name: string; error: string }) {
  return (
    <Panel>
      <div className="text-(length:--sem-text-body) font-semibold" style={{ color: 'var(--sem-bad)' }}>This workflow file has a format error</div>
      <div className="text-(length:--sem-text-meta) mt-1" style={{ color: 'var(--sem-muted)' }}>
        <span className="tnum">{name}</span> is still listed here rather than hidden, but it can't run until the error below is fixed.
      </div>
      <pre className="mt-2 rounded px-3 py-2 text-(length:--sem-text-meta) whitespace-pre-wrap" style={{ background: 'var(--sem-surface-2)', color: 'var(--sem-fg)', fontFamily: 'var(--sem-font-num)' }}>{error}</pre>
    </Panel>
  );
}

export function Panel({ children }: { children: React.ReactNode }) {
  return <div className="sem-card">{children}</div>;
}
export function SectionTitle({ children }: { children: React.ReactNode }) {
  return <div className="sem-card-title">{children}</div>;
}
export function Button({ children, onClick, primary, disabled, title }: { children: React.ReactNode; onClick?: () => void; primary?: boolean; disabled?: boolean; title?: string }) {
  return (
    <button onClick={onClick} disabled={disabled} title={title}
      className={primary ? 'sem-btn sem-btn-primary' : 'sem-btn'}>
      {children}
    </button>
  );
}
// A notice is a line of text with a dot in its colour, not a tinted box: the colour marks the state and the
// words stay in the text colour, so they read at full contrast in every theme.
export function Banner({ children, color }: { children: React.ReactNode; color: string }) {
  return (
    <div className="sem-wf-notice-line">
      <Dot color={color} />
      <div className="min-w-0 flex-1">{children}</div>
    </div>
  );
}
