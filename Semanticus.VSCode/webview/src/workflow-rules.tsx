import { useEffect, useState } from 'react';
import { uiLabel } from './copy';
import { CHECKS_OFF_LINE, CHECKS_SWITCH_NOTE, WORKFLOWS_PRO_LINE } from './workflows-model';
import {
  Banner, BINDABLE_OPS, opLabel,
  type WorkflowBindingView, type WorkflowInfo, type WorkflowPolicy, type WorkflowProfileInfo,
} from './workflow-ui';

// ===================================================================================================
// Team rules and a workflow's own rules (lane W2, contract 21.1 rows 2, 6, 26, 34, 47 to 53, and 21.3).
//
// Team rules is one drawer behind a quiet link on the list. A person working alone never meets a policy word on
// the page: the one sign of a team rule is a Required mark on a row. Inside the drawer: which workflows are
// required and for what, the switch for the checks inside runs (named for what it does, 21.4), and the team
// setups as small cards. A workflow's OWN rules (Offered, Required for) live in its Settings drawer, and both
// drawers write through the calls the agent door has: set_workflow_enabled, set_workflow_activation,
// set_workflow_binding, set_workflow_enforcement and the setups' two.
//
// Two knobs stay on the agent door on purpose, and the drawer says so rather than hiding it: saving a starter of
// your own (save_workflow_template), and the warn-or-block strength of a whole model (set_workflow_enforcement
// with "warn" or "hard").
// ===================================================================================================

const LOCKED_REASON = 'Locked by a team rule someone committed with the project. Change it with whoever set it.';

/** Is there any team rule at all? The off line and the policy words appear only then (contract 21.1 row 6). */
export function hasTeamRules(policy: WorkflowPolicy | null | undefined, enforced: boolean): boolean {
  return !enforced || (policy?.bindings ?? []).some((b) => b.require.length > 0 && b.mode !== 'off');
}

const PLURAL: Record<string, string> = {
  create_measure: 'New measures', update_measure: 'Measure formula edits', create_table: 'New tables',
  create_calculated_column: 'New calculated columns', create_relationship: 'New relationships',
  create_calculation_item: 'New calculation items',
};
/** "New measures", "New relationships": the plain name of what a rule covers. */
export const coverWord = (op: string) => PLURAL[op] ?? opLabel(op);

/** The Required mark's words for one workflow: "Required for new measures". */
export function requiredWords(name: string, policy: WorkflowPolicy | null | undefined): string | null {
  const ops = policy?.workflows.find((w) => w.name === name)?.requiredForOps ?? [];
  if (ops.length === 0) return null;
  return 'Required for ' + ops.map((op) => coverWord(op).toLowerCase()).join(', ');
}

export function LockMark() {
  return <svg className="sem-wf-lock" viewBox="0 0 12 12" aria-hidden="true">
    <rect x="2.5" y="5.2" width="7" height="5.3" rx="1" fill="none" stroke="currentColor" strokeWidth="1.1" />
    <path d="M4 5.2V3.8a2 2 0 0 1 4 0v1.4" fill="none" stroke="currentColor" strokeWidth="1.1" />
  </svg>;
}

// ---- the switch for the checks inside runs (21.3, 21.4) ---------------------------------------------------

export function ChecksSwitch({ enforced, showOffLine, disabled, onSet }: {
  enforced: boolean; showOffLine: boolean; disabled?: boolean; onSet: (on: boolean) => void;
}) {
  return <section className="sem-wf-side-sec" data-testid="wf-checks-switch" data-on={enforced ? 'yes' : 'no'}>
    <div className="sem-wf-kv">
      <b>Checks in runs</b>
      <button type="button" role="switch" aria-checked={enforced} aria-label="Checks in runs" className="sem-wf-switch"
        data-testid="wf-checks-toggle" disabled={disabled} onClick={() => onSet(!enforced)}><span /></button>
    </div>
    <span className="sem-wf-note">{enforced ? CHECKS_SWITCH_NOTE : CHECKS_OFF_LINE}</span>
    {!enforced && showOffLine && <span className="sem-wf-note" data-testid="wf-checks-off-line">Runs already underway keep the setting they started with.</span>}
  </section>;
}

// ---- a team setup as a small card (21.3: two setups plus "No team rules") ---------------------------------

function SetupCard({ profile, isPro, onApply }: { profile: WorkflowProfileInfo; isPro: boolean; onApply: () => Promise<string> }) {
  const [preview, setPreview] = useState(false);
  const [busy, setBusy] = useState(false);
  const [note, setNote] = useState<string | null>(null);
  const blocked = profile.pro && !isPro;
  const apply = async () => { setBusy(true); try { setNote(await onApply()); setPreview(false); } catch { /* shown by the drawer */ } finally { setBusy(false); } };
  return <div className={profile.selected ? 'sem-wf-setup sem-wf-setup-on' : 'sem-wf-setup'} data-testid={`wf-setup-${profile.name}`}>
    <b>{profile.title}</b>
    <span className="sem-wf-note">{profile.description}</span>
    {preview && <ul className="sem-wf-effects">{profile.effects.map((e, i) => <li key={i}>{e}</li>)}</ul>}
    {note && <span className="sem-wf-note" role="status">{note}</span>}
    {profile.selected ? <span className="sem-wf-note">In use</span>
      : !preview ? <button type="button" className="sem-btn sem-btn-sm sem-btn-quiet sem-wf-start" onClick={() => setPreview(true)}>Show what it changes</button>
        : <span className="sem-wf-acts">
          <button type="button" className="sem-btn sem-btn-sm sem-btn-primary" disabled={busy || blocked} data-testid={`wf-setup-apply-${profile.name}`}
            title={blocked ? WORKFLOWS_PRO_LINE : undefined} onClick={() => void apply()}>{busy ? 'Applying…' : 'Use this setup'}</button>
          <button type="button" className="sem-btn sem-btn-sm sem-btn-quiet" onClick={() => setPreview(false)}>Cancel</button>
        </span>}
  </div>;
}

// ---- "+ Require a workflow" -----------------------------------------------------------------------------------

function RequireForm({ library, onRequire, onCancel }: {
  library: WorkflowInfo[]; onRequire: (op: string, workflow: string) => void; onCancel: () => void;
}) {
  const [op, setOp] = useState(BINDABLE_OPS[0].op);
  const [workflow, setWorkflow] = useState('');
  const choices = library.filter((w) => !w.error);
  return <form className="sem-wf-require" data-testid="wf-require-form" onSubmit={(e) => { e.preventDefault(); if (workflow) onRequire(op, workflow); }}>
    <select className="sem-field sem-field-sm" aria-label="What it covers" value={op} onChange={(e) => setOp(e.target.value)}>
      {BINDABLE_OPS.map((b) => <option key={b.op} value={b.op}>{coverWord(b.op)}</option>)}
    </select>
    <span className="sem-wf-note">go through</span>
    <select className="sem-field sem-field-sm" aria-label="Which workflow" value={workflow} onChange={(e) => setWorkflow(e.target.value)}>
      <option value="">Choose a workflow</option>
      {choices.map((w) => <option key={w.name} value={w.name}>{w.title || w.name}</option>)}
    </select>
    <button type="submit" className="sem-btn sem-btn-sm sem-btn-primary" disabled={!workflow}>Require it</button>
    <button type="button" className="sem-btn sem-btn-sm sem-btn-quiet" onClick={onCancel}>Cancel</button>
  </form>;
}

// ---- the Team rules drawer ------------------------------------------------------------------------------------

export function TeamRulesDrawer({ library, policy, policyError = null, enforced, profiles, isPro, error, onClose, onSetEnforcement, onApplyProfile, onSetBinding, onOpenWorkflow }: {
  library: WorkflowInfo[]; policy: WorkflowPolicy | null; policyError?: string | null; enforced: boolean; profiles: WorkflowProfileInfo[] | null; isPro: boolean;
  error?: string | null;
  onClose: () => void; onSetEnforcement: (on: boolean) => void; onApplyProfile: (name: string) => Promise<string>;
  onSetBinding: (op: string, require: string[], mode: string) => void; onOpenWorkflow: (name: string) => void;
}) {
  const [requiring, setRequiring] = useState(false);
  const titleOf = (n: string) => library.find((w) => w.name === n)?.title || n;
  const bindings = (policy?.bindings ?? []).filter((b) => b.require.length > 0 && b.mode !== 'off');
  const rules = hasTeamRules(policy, enforced);
  return <SideDrawer title="Team rules" sub="for this model's folder" testId="wf-team-rules" onClose={onClose}>
    <p className="sem-wf-note">Make a workflow the required way to do a task.</p>
    {!isPro && <Banner color="var(--sem-muted)">{WORKFLOWS_PRO_LINE}</Banner>}
    {error && <Banner color="var(--sem-warn)">{error}</Banner>}

    <section className="sem-wf-side-sec" data-testid="wf-required-list">
      <b>Required workflows</b>
      {/* "None" only when the rules were read and hold none. A rule that could not be read is not "no rule"
          (UAT-S02-2-005: the drawer said None beside a warning that named a required workflow). */}
      {!policy
        ? <span className="sem-wf-note" data-testid="wf-rules-unread">{policyError ? 'The team rules could not be read, so this cannot say which workflows are required.' : 'Reading the team rules…'}</span>
        : bindings.length === 0 && <span className="sem-wf-note">None yet.</span>}
      {bindings.map((b) => <div key={b.op} className="sem-wf-kv sem-wf-kv-rule" data-testid={`wf-rule-${b.op}`}>
        <span><span className="sem-wf-req"><LockMark />{coverWord(b.op)}</span> go through{' '}
          {b.require.map((n, i) => <span key={n}>{i > 0 ? ' or ' : ''}<b>{titleOf(n)}</b></span>)}
          {b.mode === 'warn' && <span className="sem-wf-note"> (flagged, not stopped)</span>}
          {!b.userDisablable && <span className="sem-wf-note" title={LOCKED_REASON}> (locked)</span>}
        </span>
        <span className="sem-wf-acts">
          <button type="button" className="sem-btn sem-btn-sm sem-btn-quiet" onClick={() => onOpenWorkflow(b.require[0])}>Change</button>
          <button type="button" className="sem-btn sem-btn-sm sem-btn-quiet" disabled={!isPro || !b.userDisablable}
            title={!b.userDisablable ? LOCKED_REASON : 'Stop requiring it. Require it again to put it back.'}
            data-testid={`wf-rule-remove-${b.op}`} onClick={() => onSetBinding(b.op, [], 'off')}>Remove</button>
        </span>
      </div>)}
      {requiring
        ? <RequireForm library={library} onCancel={() => setRequiring(false)}
            onRequire={(op, name) => { const ex = (policy?.bindings ?? []).find((x) => x.op === op);
              onSetBinding(op, Array.from(new Set([...(ex?.require ?? []), name])), ex?.mode && ex.mode !== 'off' ? ex.mode : 'hard'); setRequiring(false); }} />
        : <button type="button" className="sem-btn sem-btn-sm sem-btn-quiet sem-wf-start" disabled={!isPro}
            data-testid="wf-require-add" onClick={() => setRequiring(true)}>+ Require a workflow</button>}
    </section>

    <ChecksSwitch enforced={enforced} showOffLine={rules} disabled={!isPro} onSet={onSetEnforcement} />

    <section className="sem-wf-side-sec">
      <b>Start from a set of team rules</b>
      <div className="sem-wf-setups">
        {(profiles ?? []).map((p) => <SetupCard key={p.name} profile={p} isPro={isPro} onApply={() => onApplyProfile(p.name)} />)}
      </div>
    </section>
  </SideDrawer>;
}

// ---- a workflow's own rules, in its Settings drawer (21.1 rows 26, 34, 50, 51) ---------------------------------

/** The six conditions "Only when" offers, read positively: a rule shows the workflow while its condition holds. */
export const OFFERED_WHEN: ReadonlyArray<{ label: string; expr: string }> = [
  { label: 'On a production workspace', expr: "connection.workspace ~ '*prod*'" },
  { label: 'On the main branch', expr: "git.branch == 'main'" },
  { label: 'In the month-end window', expr: 'date.monthEndOffset >= -3' },
  { label: 'A small model (under 50 tables)', expr: 'model.tableCount < 50' },
  { label: 'No row-level security', expr: 'model.hasRls == false' },
  { label: 'Readiness B or better', expr: "model.readinessGrade >= 'B'" },
];

export type Offered = 'always' | 'when' | 'off';
export const offeredOf = (info: WorkflowInfo): Offered =>
  info.enabled === false ? 'off' : info.activeReason && !/^required when/i.test(info.activeReason) ? 'when' : 'always';

export function OfferedControl({ info, isPro, required, onEnabled, onActivation }: {
  info: WorkflowInfo; isPro: boolean; required: boolean;
  /** clearRule: also clear a saved Only when rule once it is on (Always on a workflow that is off). */
  onEnabled: (on: boolean, clearRule?: boolean) => void; onActivation: (when: string, set: string) => void;
}) {
  const current = offeredOf(info);
  const [picking, setPicking] = useState(current === 'when');
  useEffect(() => { setPicking(offeredOf(info) === 'when'); }, [info.name, info.enabled, info.activeReason]);  // eslint-disable-line react-hooks/exhaustive-deps
  const shown: Offered = picking && current !== 'off' ? 'when' : current;
  return <div className="sem-wf-offered" data-testid="wf-offered" data-offered={current}>
    <div className="sem-wf-kv">
      <span>Offered</span>
      <span className="sem-seg" role="group" aria-label="Offered">
        <button type="button" className="sem-seg-item" aria-pressed={shown === 'always'} disabled={!isPro}
          onClick={() => { setPicking(false); if (current === 'off') onEnabled(true, true); if (current === 'when') onActivation('', ''); }}>Always</button>
        <button type="button" className="sem-seg-item" aria-pressed={shown === 'when'} disabled={!isPro}
          onClick={() => { setPicking(true); if (current === 'off') onEnabled(true); }}>Only when…</button>
        <button type="button" className="sem-seg-item" aria-pressed={shown === 'off'} disabled={!isPro || required}
          title={required ? 'A team rule requires this workflow, so it stays offered.' : undefined}
          onClick={() => { setPicking(false); onEnabled(false); }}>Off</button>
      </span>
    </div>
    {shown === 'when' && <div className="sem-wf-chips" role="group" aria-label="Only when">
      {OFFERED_WHEN.map((c) => <button key={c.expr} type="button" className="sem-chip sem-chip-outline" disabled={!isPro}
        onClick={() => onActivation(c.expr, 'on')}>{c.label}</button>)}
    </div>}
    {info.activeReason && <span className="sem-wf-note" data-testid="wf-offered-reason">Now {info.active === false ? 'not offered' : 'offered'}: {info.activeReason}.</span>}
    {info.enabled === false && <span className="sem-wf-note">Off. It is not offered and cannot be started until you turn it back on.</span>}
  </div>;
}

export function RequiredForControl({ name, policy, isPro, titleOf, onSetBinding }: {
  name: string; policy: WorkflowPolicy | null; isPro: boolean; titleOf: (name: string) => string;
  onSetBinding: (op: string, require: string[], mode: string) => void;
}) {
  const [adding, setAdding] = useState(false);
  const bindings = policy?.bindings ?? [];
  const ops = policy?.workflows.find((w) => w.name === name)?.requiredForOps ?? [];
  const locked = new Set(bindings.filter((b) => !b.userDisablable).map((b) => b.op));
  const addable = BINDABLE_OPS.filter((b) => !ops.includes(b.op) && !locked.has(b.op));
  const lints = (policy?.lints ?? []).filter((l) => l.message.includes(name));
  const binding = (op: string): WorkflowBindingView | undefined => bindings.find((b) => b.op === op && b.require.includes(name));
  return <div className="sem-wf-required" data-testid="wf-required-for">
    {ops.length === 0 && <span className="sem-wf-note">Nothing has to go through this workflow.</span>}
    {ops.map((op) => {
      const b = binding(op);
      const others = (b?.require ?? []).filter((r) => r !== name);
      return <div key={op} className="sem-wf-kv">
        <span><span className="sem-wf-req"><LockMark />Required for {coverWord(op).toLowerCase()}</span>
          {others.length > 0 && <span className="sem-wf-note"> (or {others.map(titleOf).join(', ')})</span>}
          {!b && <span className="sem-wf-note"> (by a rule with a condition)</span>}</span>
        {b && <button type="button" className="sem-btn sem-btn-sm sem-btn-quiet" disabled={!isPro || !b.userDisablable}
          title={!b.userDisablable ? LOCKED_REASON : undefined}
          onClick={() => others.length ? onSetBinding(op, others, b.mode) : onSetBinding(op, [], 'off')}>Stop requiring</button>}
      </div>;
    })}
    {adding
      ? <span className="sem-wf-chips" role="group" aria-label="Require it for">
        {addable.map((b) => <button key={b.op} type="button" className="sem-chip sem-chip-outline"
          onClick={() => { const ex = bindings.find((x) => x.op === b.op);
            onSetBinding(b.op, Array.from(new Set([...(ex?.require ?? []), name])), ex?.mode && ex.mode !== 'off' ? ex.mode : 'hard'); setAdding(false); }}>
          {coverWord(b.op)}</button>)}
        <button type="button" className="sem-btn sem-btn-sm sem-btn-quiet" onClick={() => setAdding(false)}>Cancel</button>
      </span>
      : <button type="button" className="sem-btn sem-btn-sm sem-btn-quiet sem-wf-start" disabled={!isPro || addable.length === 0}
          data-testid="wf-required-add" onClick={() => setAdding(true)}>+ Require it for an action</button>}
    {lints.map((l, i) => <span key={i} className="sem-wf-note">{uiLabel(l.message)}</span>)}
  </div>;
}

// ---- one drawer shape for Team rules and Settings --------------------------------------------------------------

export function SideDrawer({ title, sub, testId, onClose, children }: {
  title: string; sub?: string | null; testId: string; onClose: () => void; children: React.ReactNode;
}) {
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape' && !e.defaultPrevented) { e.preventDefault(); onClose(); } };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [onClose]);
  return <>
    <div className="sem-wf-scrim" onClick={onClose} aria-hidden="true" />
    <aside className="sem-wf-side" role="dialog" aria-label={title} data-testid={testId}>
      <div className="sem-wf-side-head">
        <h2>{title}</h2>
        {sub && <span className="sem-wf-note">{sub}</span>}
        <button type="button" className="sem-btn sem-btn-sm sem-btn-quiet sem-wf-close" aria-label="Close" title="Close. Escape does the same." onClick={onClose}>
          <svg viewBox="0 0 12 12" aria-hidden="true"><path d="M3 3l6 6M9 3l-6 6" stroke="currentColor" strokeWidth="1.3" /></svg>
        </button>
      </div>
      <div className="sem-wf-side-body">{children}</div>
    </aside>
  </>;
}
