import { useEffect, useRef, useState } from 'react';
import { rpc, onReconnect } from './bridge';
import { Panel, Banner, SectionTitle, Pill } from './workflows';
import { uiLabel } from './copy';
import { useConnection } from './connection';

// ===================================================================================================
// Permissions — the human door onto the agent-policy engine (PR #128). This page IS the human: every
// MUTATING call passes origin='human', which is exactly the door the engine trusts to change the
// policy, approve a request, or label a target. (The read methods take no origin at all — reads are
// structurally ungated by design.) Four surfaces, top to bottom:
//   1. Guardrail switch + preset picker (header)  — the switch is FREE, presets are Pro.
//   2. The capability × target matrix              — plain-language rows; engine enum in a tooltip.
//   3. "Waiting for you" approval queue            — polled while the tab is open.
//   4. One line that says labels are set in Connections (r1 min4: the Targets list left; each model's label is set
//      in one place, the Connections list, and this page only reads it to pick the column).
// Copy rule: "the AI assistant", never "Claude". Plain language; engine terms live only in tooltips.
// This guardrail is a governance control against ACCIDENTS, not a boundary against a hostile agent —
// the credential the user hands the assistant is the real boundary (stated once, in the footer).
//
// Honesty rules this page enforces on ITSELF (a governance surface must never lie about state):
//   • Policy state renders only once getAgentPolicy has ANSWERED — no default-rendered "On · Standard"
//     while the load is slow or failed. Loading and error(+retry) are explicit states.
//   • Every policy write is serialized + generation-guarded: one write in flight at a time, optimistic
//     locally, only the LATEST response lands, and a failed write rolls the control back.
//   • List responses are latest-wins: a stale poll can never resurrect a forgotten target or an already
//     actioned approval (mutations bump the generation before they run).
// ===================================================================================================

// ---- wire shapes (camelCase, mirror Policy/AgentPolicy.cs · ApprovalLedger.cs · Connect/ConnectionRegistry.cs) ----
export interface AgentPolicy { enabled: boolean; preset: string; matrix: Record<string, Record<string, string>>; }
interface ApprovalRecord {
  id: string; capability: string; label: string; intentHash: string;
  summary: string; target: string; requestedUtc: string; grantedUtc?: string | null; expiresUtc?: string | null;
  ttlMinutes?: number;
}

// The true scope of what approving a request grants — a property of the LEDGER, not of the op that asked. A
// QueryData grant is a time-boxed session covering every read-rows operation on the target, so the card must say
// so; the other gated capabilities are consumed by the single action the summary describes. Plain language.
function grantScopeLine(r: ApprovalRecord): string | null {
  const mins = r.ttlMinutes && r.ttlMinutes > 0 ? r.ttlMinutes : 15;
  if (r.capability === 'QueryData') {
    return `Approving also allows other row previews on this target for ${mins} minutes.`;
  }
  return null;
}
interface ConnectionRecord {
  id: string; kind: string; endpoint: string; database: string; modelName: string;
  tenantId?: string; authMode?: string; label?: string | null; workingFolder?: string | null;
  lastUsedUtc?: string; useCount: number;
}

type Action = 'allow' | 'ask' | 'deny';
const LABELS = ['local', 'dev', 'uat', 'prod'] as const;
type Label = typeof LABELS[number];
/** The one word per label, the same words Connections uses (Local, Development, UAT, Production). */
export const LABEL_WORD: Record<Label, string> = { local: 'Local', dev: 'Development', uat: 'UAT', prod: 'Production' };

const errMsg = (e: unknown) => String((e as Error)?.message ?? e);

// True after the component has mounted, false after unmount — the guard for setState in event handlers
// whose awaits can resolve post-unmount. Polling effects use their OWN local `cancelled` flag instead
// (scoped inside the effect, so a StrictMode setup/cleanup/setup cycle can never leave it wrong).
function useMountedRef() {
  const mounted = useRef(false);
  useEffect(() => { mounted.current = true; return () => { mounted.current = false; }; }, []);
  return mounted;
}

// The five named presets, strictness ascending — one plain-English line each (the engine's gradient made legible).
const PRESETS: { id: string; name: string; desc: string }[] = [
  { id: 'open', name: 'Open', desc: 'Allows the listed actions in every environment.' },
  { id: 'standard', name: 'Standard', desc: 'Asks before touching UAT or production.' },
  { id: 'cautious', name: 'Cautious', desc: 'Asks for anything beyond local.' },
  { id: 'client', name: 'Client', desc: 'Blocks production changes and reading rows from UAT or production.' },
  // Locked's two distinguishing cells are "refused" on uat/prod and "asks" on local/dev (AgentPolicy.cs), so the
  // line has to lead with the refusal. It used to say "asks even locally" alone, which read as "it always asks"
  // and left the reader unprepared for a hard refusal on a published model (D-213).
  { id: 'locked', name: 'Locked', desc: 'Writes to published models are refused. Even local writes ask first.' },
];

// The shell's publish line reuses these exact policy words rather than inventing a second interpretation.
export function permissionPresetDescription(policy: AgentPolicy): string {
  if (!policy.enabled) return 'your assistant permission checks are off';
  const preset = PRESETS.find((item) => item.id === policy.preset);
  if (preset) return preset.desc.charAt(0).toLowerCase() + preset.desc.slice(1);
  if (policy.preset === 'custom') return 'uses your custom assistant permissions';
  return 'live actions are blocked until your assistant permissions are restored';
}

export function publishPermissionDescription(policy: AgentPolicy, targetLabel?: string, hasTarget = false): string {
  if (!policy.enabled) return 'assistant permissions are off';
  if (!hasTarget) return `assistant permissions: ${PRESETS.find((item) => item.id === policy.preset)?.name ?? policy.preset}`;
  const candidate = targetLabel?.trim().toLowerCase();
  const label = candidate === 'local' || candidate === 'dev' || candidate === 'uat' ? candidate : 'prod';
  const action = policy.matrix?.DeployLive?.[label]?.trim().toLowerCase();
  if (action === 'allow') return 'your assistant may publish here without asking';
  if (action === 'ask') return 'your assistant asks you before publishing here';
  return 'your assistant cannot publish here';
}

// The matrix rows, in reading order. `cap` is the engine capability (shown only in a tooltip); `kind` decides how a
// row renders: an editable gated row, a structurally-always-allow local row, or a pinned info strip (allow/deny).
type RowKind = 'gated' | 'localAllow' | 'infoAllow' | 'infoDeny';
interface RowDef { cap: string; title: string; sub: string; kind: RowKind; tip?: string; }
const ROWS: RowDef[] = [
  { cap: 'Read / QueryCalc', title: 'Read & analyze', sub: 'List, inspect, scan, and run measure calculations.', kind: 'infoAllow' },
  { cap: 'QueryData', title: 'Preview data', sub: 'Show rows of data.', kind: 'gated',
    tip: 'Table previews, measure pivots and row-returning DAX queries. Calculation checks can still show the category labels they group by.' },
  { cap: 'EditLocal', title: 'Edit the working model', sub: 'Never touches a live target.', kind: 'localAllow' },
  { cap: 'DeployFile', title: 'Write model files', sub: 'Never touches a live target.', kind: 'localAllow' },
  { cap: 'DeployLive', title: 'Publish to a published model', sub: 'Publish changes to the design of a live model.', kind: 'gated' },
  { cap: 'DeployDelete', title: 'Delete from a published model', sub: 'Cannot be undone.', kind: 'gated' },
  { cap: 'Rollback', title: 'Roll back a published model', sub: 'Restore a live model to an earlier version.', kind: 'gated' },
  { cap: 'Refresh', title: 'Refresh live data', sub: 'Refresh a partition on the live model.', kind: 'gated' },
  { cap: 'Governance', title: 'Change these settings', sub: 'Never allowed for the assistant.', kind: 'infoDeny' },
];

// The preset picker and every cell are FREE from 2026-09-15 (Kane set the line by feature; the agent
// policy is not one of the four). What did NOT change is who may write them: the engine still refuses a
// policy write that did not come from a person, and that refusal is an authority check, not a payment one.

// ---------------------------------------------------------------------------------------------------
export function PermissionsView({ focusApproval, onFocusConsumed, onApprovalChanged, onPolicyChanged }: {
  focusApproval?: { id: string; nonce: number } | null;
  onFocusConsumed?: (nonce: number) => void; onApprovalChanged?: () => void;
  onPolicyChanged?: (policy: AgentPolicy) => void;
}) {
  const mounted = useMountedRef();

  const [policy, setPolicy] = useState<AgentPolicy | null>(null);
  const [policyErr, setPolicyErr] = useState<string | null>(null);   // getAgentPolicy failed (load, or retry)
  const [writeErr, setWriteErr] = useState<string | null>(null);     // a policy write failed
  // The one policy write in flight, by control key ('switch' | 'preset:<id>' | 'cell:<cap>:<label>').
  // Writes are SERIALIZED: while one is in flight every policy control is disabled, so rapid clicks can
  // never stack ("three clicks from Allow" must end at deny, not fire three identical writes).
  const [inflight, setInflight] = useState<string | null>(null);
  // Monotonic generation across loads AND writes: only the response matching the latest issued request
  // may land, so an out-of-order answer can never overwrite newer state.
  const genRef = useRef(0);

  const loadPolicy = () => {
    const gen = ++genRef.current;
    setPolicyErr(null);
    rpc<AgentPolicy>('getAgentPolicy')
      .then((p) => { if (mounted.current && gen === genRef.current) setPolicy(p); })
      .catch((e) => { if (mounted.current && gen === genRef.current) setPolicyErr(errMsg(e)); });
  };
  useEffect(() => {
    loadPolicy();   // gen + mounted guard the async parts; a StrictMode re-run issues its own (newer) load
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);
  useEffect(() => { if (policy) onPolicyChanged?.(policy); }, [policy, onPolicyChanged]);

  // One serialized, generation-guarded policy write: optimistic locally, latest response wins, rollback
  // on failure. `optimistic` may be identity when the client can't know the result (a preset swap builds
  // a whole matrix server-side — showing a guessed matrix would be its own lie).
  async function policyWrite(key: string, run: () => Promise<AgentPolicy>, optimistic: (p: AgentPolicy) => AgentPolicy) {
    if (!policy || inflight) return;             // controls are disabled in these states; belt-and-braces
    const before = policy;                       // exact rollback snapshot (writes are serialized)
    const gen = ++genRef.current;
    setInflight(key);
    setWriteErr(null);
    setPolicy(optimistic(before));
    try {
      const p = await run();
      if (mounted.current && gen === genRef.current) setPolicy(p);
    } catch (e) {
      if (mounted.current && gen === genRef.current) {
        setPolicy(before);                       // never leave the optimistic state standing over a refusal
        setWriteErr(errMsg(e));
      }
    } finally {
      if (mounted.current) setInflight((cur) => (cur === key ? null : cur));
    }
  }

  const setPreset = (id: string) => {
    void policyWrite(`preset:${id}`, () => rpc<AgentPolicy>('setAgentPolicyPreset', id, 'human'), (p) => p);
  };
  const setCell = (cap: string, label: Label, action: Action) => {
    void policyWrite(`cell:${cap}:${label}`, () => rpc<AgentPolicy>('setAgentPolicyCell', cap, label, action, 'human'),
      (p) => ({ ...p, preset: 'custom', matrix: { ...p.matrix, [cap]: { ...(p.matrix?.[cap] ?? {}), [label]: action } } }));
  };
  // The kill-switch is FREE and operable as soon as the policy is known — a user who finds the guardrail
  // in the way must be able to turn it off honestly rather than route around it.
  const setEnabled = (enabled: boolean) => {
    void policyWrite('switch', () => rpc<AgentPolicy>('setAgentPolicyEnabled', enabled, 'human'), (p) => ({ ...p, enabled }));
  };

  return (
    <div className="h-full overflow-auto">
      <div className="sem-evidence-page flex flex-col gap-4 min-w-0">
        <HeaderPanel policy={policy} policyErr={policyErr} inflight={inflight}
          onToggle={setEnabled} onPreset={setPreset} onRetry={loadPolicy} />
        {writeErr && <Banner color="var(--sem-bad)">{writeErr}</Banner>}
        {policy && <MatrixPanel policy={policy} inflight={inflight} onCell={setCell} />}
        <ApprovalsPanel focusApproval={focusApproval} onFocusConsumed={onFocusConsumed} onApprovalChanged={onApprovalChanged} />
        <LabelsLine />
        <FooterLine />
      </div>
    </div>
  );
}

// ---- Header: the title, the free guardrail switch and the preset picker ---------------------------
function HeaderPanel({ policy, policyErr, inflight, onToggle, onPreset, onRetry }: {
  policy: AgentPolicy | null; policyErr: string | null; inflight: string | null;
  onToggle: (v: boolean) => void; onPreset: (id: string) => void; onRetry: () => void;
}) {
  const isCustom = policy?.preset === 'custom';
  const isUnreadable = policy?.preset === 'unreadable';
  const chosen = policy && !isCustom && !isUnreadable ? PRESETS.find((p) => p.id === policy.preset) : null;
  return (
    <div className="flex flex-col gap-3">
      <div className="flex items-center gap-4">
        <div className="flex-1 min-w-0 flex items-center gap-2">
          <ShieldIcon />
          <h1 className="m-0 text-(length:--sem-text-page) font-semibold">Assistant permissions</h1>
        </div>
        {/* The switch renders ONLY once the policy is known — a toggle with a defaulted position would be a
            governance-status lie. While loading/error, its slot says so instead. */}
        {policy
          ? <GuardrailSwitch enabled={policy.enabled} disabled={!!inflight} onToggle={onToggle} />
          : <div className="text-(length:--sem-text-label) shrink-0 pt-1" style={{ color: 'var(--sem-muted)' }}>{policyErr ? 'unavailable' : 'loading…'}</div>}
      </div>

      {policyErr && !policy && (
        <div className="flex items-start gap-2">
          <div className="flex-1 min-w-0">
            <Banner color="var(--sem-bad)">Couldn’t load the permission settings: {policyErr}</Banner>
          </div>
          <button onClick={onRetry} className="sem-btn shrink-0">
            Retry
          </button>
        </div>
      )}
      {!policy && !policyErr && (
        <div className="text-(length:--sem-text-meta)" style={{ color: 'var(--sem-muted)' }}>Loading the current policy…</div>
      )}

      {policy && isUnreadable && (
        <Banner color="var(--sem-warn)">
          Your saved permission settings could not be read. Live actions are blocked until the settings are restored. Choose a preset below to reset them.
        </Banner>
      )}

      {policy && (
        <div className="flex flex-col gap-2">
          {/* One segmented control and one line for the chosen preset; the other four read on hover. */}
          <div className="sem-seg self-start" role="group" aria-label="Preset">
            {PRESETS.map((p) => {
              const active = !isCustom && !isUnreadable && policy.preset === p.id;
              return (
                <button key={p.id} type="button" className="sem-seg-item" aria-pressed={active} disabled={!!inflight}
                  data-testid={`perm-preset-${p.id}`} title={`${p.name}: ${p.desc}`} onClick={() => onPreset(p.id)}>
                  {inflight === `preset:${p.id}` ? 'Applying…' : p.name}
                </button>
              );
            })}
          </div>
          {chosen && <div className="text-(length:--sem-text-meta)" style={{ color: 'var(--sem-muted)' }}>{presetLine(chosen)}</div>}
          {isCustom && (
            <div className="flex items-center gap-2 text-(length:--sem-text-meta)" style={{ color: 'var(--sem-muted)' }}>
              <Pill tint="var(--sem-accent)">Custom</Pill>
              <span>Your grid no longer matches a preset. Keep changing cells, or pick a preset to reset it.</span>
            </div>
          )}
        </div>
      )}
    </div>
  );
}

/** "Standard: asks before touching UAT or production." The chosen preset's name and its one line. */
export function presetLine(preset: { name: string; desc: string }): string {
  return `${preset.name}: ${preset.desc.charAt(0).toLowerCase()}${preset.desc.slice(1)}`;
}

// The global kill-switch — a labelled toggle. Free for everyone; operable as soon as the policy is known.
function GuardrailSwitch({ enabled, disabled, onToggle }: { enabled: boolean; disabled: boolean; onToggle: (v: boolean) => void }) {
  return (
    <div className="flex items-center gap-2 shrink-0">
      <span className="text-(length:--sem-text-meta) font-medium">Permission checks</span>
      <button role="switch" aria-checked={enabled} disabled={disabled} onClick={() => onToggle(!enabled)}
        title={enabled ? 'On: these permission checks apply to the assistant. Click to turn off.' : 'Off: these permission checks are disabled. Click to turn on.'}
        className="relative rounded-full transition-colors disabled:opacity-60" style={{ width: 36, height: 'var(--sem-chip-h)',
          background: enabled ? 'var(--sem-accent)' : 'var(--sem-surface-2)', border: '1px solid var(--sem-border)' }}>
        <span className="absolute rounded-full transition-all" style={{ width: 14, height: 14, top: 2,
          left: enabled ? 18 : 2, background: enabled ? 'var(--sem-on-accent)' : 'var(--sem-muted)' }} />
      </button>
      <span className="text-(length:--sem-text-label) tnum w-6" style={{ color: enabled ? 'var(--sem-accent)' : 'var(--sem-muted)' }}>{enabled ? 'On' : 'Off'}</span>
    </div>
  );
}

// ---- The capability × target matrix --------------------------------------------------------------
/** What can never be changed here, said once under the grid instead of as four rows of fixed cells. Under the Locked
 *  preset the two local rows are not "always": they ask first, and the line says so instead of claiming otherwise. */
export function fixedLine(policy: AgentPolicy): string {
  const local = ROWS.filter((r) => r.kind === 'localAllow');
  const alwaysLocal = !policy.enabled || local.every((r) => LABELS.every((l) => (policy.matrix?.[r.cap]?.[l] ?? 'allow') === 'allow'));
  return alwaysLocal
    ? 'Always allowed: read and check the model, edit the working model, write model files. Never: change these settings.'
    : 'Always allowed: read and check the model. Editing the working model and writing model files ask first. Never: change these settings.';
}

function MatrixPanel({ policy, inflight, onCell }: {
  policy: AgentPolicy; inflight: string | null;
  onCell: (cap: string, label: Label, action: Action) => void;
}) {
  const off = !policy.enabled;
  // Only the rows a person can change. The two local rows are in the grid only under Locked, where they ask first.
  const lockedLocal = !off && ROWS.some((r) => r.kind === 'localAllow' && LABELS.some((l) => (policy.matrix?.[r.cap]?.[l] ?? 'allow') !== 'allow'));
  const grid = ROWS.filter((r) => r.kind === 'gated' || (r.kind === 'localAllow' && lockedLocal));
  const cols = `minmax(230px,1.5fr) repeat(4, minmax(78px,1fr))`;

  return (
    <Panel>
      <SectionTitle>What your assistant may do</SectionTitle>
      {!off && <div className="text-(length:--sem-text-meta) mt-1" style={{ color: 'var(--sem-muted)' }}>Click a cell to change it.</div>}

      {off && (
        <div className="flex items-center gap-2 mt-3 rounded px-3 py-2 text-(length:--sem-text-meta)"
          style={{ background: 'color-mix(in srgb, var(--sem-warn) 12%, transparent)', color: 'var(--sem-fg)', border: '1px solid color-mix(in srgb, var(--sem-warn) 32%, transparent)' }}>
          <WarnDot />
          <span>
            The guardrail is off. The assistant is not being gated by this matrix. The fixed rules still apply:
            reading is always allowed, and the assistant can never change these settings.
          </span>
        </div>
      )}

      <div className="mt-3">
        {/* header */}
        <div style={{ display: 'grid', gridTemplateColumns: cols }}>
          <div className="px-3 py-2 text-(length:--sem-text-label) font-semibold" style={{ color: 'var(--sem-muted)' }}>Capability</div>
          {LABELS.map((l) => (
            <div key={l} className="px-2 py-2 text-center text-(length:--sem-text-label) font-semibold" style={{ color: 'var(--sem-muted)' }}>
              {LABEL_WORD[l]}{l === 'prod' && <span title="A model with no label uses the Production settings." style={{ color: 'var(--sem-warn)' }}> *</span>}
            </div>
          ))}
        </div>
        {/* body */}
        {grid.map((row) => (
          <div key={row.cap} style={{ display: 'grid', gridTemplateColumns: cols, borderTop: '1px solid var(--sem-rule)' }}>
            <RowLabel row={row} />
            {LABELS.map((l) => {
              const key = `cell:${row.cap}:${l}`;
              const action = (policy.matrix?.[row.cap]?.[l] as Action) ?? (row.kind === 'localAllow' ? 'allow' : 'deny');
              return (
                <div key={l} className="flex items-center justify-center px-2 py-2">
                  {row.kind === 'localAllow' ? (
                    // The Locked preset asks even for these (its line says so): show what the matrix says, read-only.
                    <ActionChip action={action} />
                  ) : off ? (
                    // Guardrail off: only the TUNABLE cells dim (they are what stood down).
                    <span style={{ opacity: 0.35 }}><ActionChip action={action} /></span>
                  ) : (
                    <ActionCell action={action} disabled={!!inflight} pending={inflight === key}
                      onClick={() => onCell(row.cap, l, next(action))} />
                  )}
                </div>
              );
            })}
          </div>
        ))}
      </div>

      <div data-testid="perm-fixed-line" className="mt-3 text-(length:--sem-text-label)" style={{ color: 'var(--sem-muted)' }}>{fixedLine(policy)}</div>
    </Panel>
  );
}

function RowLabel({ row }: { row: RowDef }) {
  return (
    <div className="px-3 py-2 min-w-0">
      <div className="flex items-center gap-2 flex-wrap">
        <span className="text-(length:--sem-text-meta) font-medium" title={`${row.tip ? `${row.tip} ` : ''}Capability: ${row.cap}`}>{row.title}</span>
      </div>
      <div className="text-(length:--sem-text-label) mt-1" style={{ color: 'var(--sem-muted)' }}>{row.sub}</div>
    </div>
  );
}

const ACTION_META: Record<Action, { label: string; color: string }> = {
  allow: { label: 'Allow', color: 'var(--sem-good)' },
  ask: { label: 'Ask', color: 'var(--sem-warn)' },
  deny: { label: 'Deny', color: 'var(--sem-bad)' },
};
const next = (a: Action): Action => (a === 'allow' ? 'ask' : a === 'ask' ? 'deny' : 'allow');

// A compact action chip: icon + word (never colour alone — colourblind-safe). The read-only rendering.
function ActionChip({ action }: { action: Action }) {
  const m = ACTION_META[action];
  return (
    <span className="inline-flex items-center gap-1 text-(length:--sem-text-label) font-semibold px-2 py-1 rounded"
      style={{ color: m.color, background: `color-mix(in srgb, ${m.color} 16%, transparent)` }}>
      <ActionIcon action={action} />{m.label}
    </span>
  );
}

// The Pro editable cell: cycles allow → ask → deny. Disabled while ANY policy write is in flight (writes
// are serialized so clicks can't stack); the cell whose write is running shows a pending fade.
function ActionCell({ action, disabled, pending, onClick }: { action: Action; disabled: boolean; pending: boolean; onClick: () => void }) {
  const m = ACTION_META[action];
  return (
    <button onClick={onClick} disabled={disabled} title={`${m.label}. Click to cycle: allow → ask → deny.`}
      className="inline-flex items-center gap-1 text-(length:--sem-text-label) font-semibold px-2 py-1 rounded transition-colors"
      style={{ color: m.color, background: `color-mix(in srgb, ${m.color} 16%, transparent)`,
        border: `1px solid color-mix(in srgb, ${m.color} 34%, transparent)`, opacity: pending ? 0.55 : undefined }}>
      <ActionIcon action={action} />{m.label}
    </button>
  );
}

// ---- "Waiting for you" — the approval queue ------------------------------------------------------
function ApprovalsPanel({ focusApproval, onFocusConsumed, onApprovalChanged }: {
  focusApproval?: { id: string; nonce: number } | null;
  onFocusConsumed?: (nonce: number) => void; onApprovalChanged?: () => void;
}) {
  const mounted = useMountedRef();
  const [items, setItems] = useState<ApprovalRecord[] | null>(null);
  const [err, setErr] = useState<string | null>(null);
  const [busyId, setBusyId] = useState<string | null>(null);
  const [directedId, setDirectedId] = useState<string | null>(null);
  const [focusNotice, setFocusNotice] = useState<string | null>(null);
  const [now, setNow] = useState(() => Date.now());
  // Latest-wins generation: every fetch takes a ticket; a mutation bumps it BEFORE running so a poll
  // response already in flight can never overwrite (resurrect) what the mutation just changed.
  const genRef = useRef(0);
  const cards = useRef(new Map<string, HTMLDivElement>());
  const focusedNonce = useRef(0);

  const pull = () => {
    const gen = ++genRef.current;
    return rpc<ApprovalRecord[]>('listPendingApprovals')
      .then((r) => { if (mounted.current && gen === genRef.current) { setItems(r ?? []); setErr(null); } })
      .catch((e) => { if (mounted.current && gen === genRef.current) setErr(errMsg(e)); });
  };

  // Poll while the tab is mounted (the panel only mounts while Permissions is the active Studio tab).
  useEffect(() => {
    let cancelled = false;                       // scoped to THIS effect — StrictMode-safe
    const tick = () => { if (!cancelled) void pull(); };
    tick();
    const iv = window.setInterval(tick, 5000);
    return () => { cancelled = true; window.clearInterval(iv); };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);
  // Tick relative times / the expiry countdown without an interaction.
  useEffect(() => { const t = window.setInterval(() => setNow(Date.now()), 20000); return () => window.clearInterval(t); }, []);

  const act = async (id: string, method: 'approveAgentAction' | 'denyAgentAction') => {
    setBusyId(id);
    genRef.current++;                            // invalidate any poll response already in flight
    try {
      await rpc(method, id, 'human');
      onApprovalChanged?.();
      if (mounted.current) await pull();
    }
    catch (e) { if (mounted.current) setErr(errMsg(e)); }
    finally { if (mounted.current) setBusyId(null); }
  };

  const list = items ?? [];
  const pending = list.filter((r) => !r.grantedUtc);
  const granted = list.filter((r) => r.grantedUtc);

  // Navigation carries the ledger id, not a summary/target guess. Wait until the poll has rendered that exact card,
  // then bring it into view and keyboard focus; the nonce lets repeated clicks focus the same request again.
  useEffect(() => {
    if (!focusApproval || !items) return;
    if (focusedNonce.current === focusApproval.nonce) return;
    focusedNonce.current = focusApproval.nonce;
    const card = cards.current.get(focusApproval.id);
    if (!card) {
      const record = items.find((x) => x.id === focusApproval.id);
      setDirectedId(null);
      setFocusNotice(record?.grantedUtc
        ? 'That request has already been approved, so it is no longer waiting for you.'
        : 'That request is no longer waiting. It may already have been approved, denied, or expired.');
      onFocusConsumed?.(focusApproval.nonce);
      return;
    }
    setFocusNotice(null);
    setDirectedId(focusApproval.id);
    onFocusConsumed?.(focusApproval.nonce);
    card.scrollIntoView({ behavior: 'smooth', block: 'center' });
    card.focus({ preventScroll: true });
    const clear = window.setTimeout(() => setDirectedId((id) => id === focusApproval.id ? null : id), 1800);
    return () => window.clearTimeout(clear);
  }, [focusApproval?.id, focusApproval?.nonce, items]);

  // Nothing to show, nothing drawn: the section appears when something waits, when a request the person came for is
  // gone, or when the queue could not be read. An expired approval has no action, so it is not listed (the assistant
  // asks again and a new row appears).
  const live = granted.filter((r) => !isExpired(r.expiresUtc, now));
  if (items !== null && !err && pending.length === 0 && live.length === 0 && !focusNotice) return null;

  return (
    <Panel>
      <SectionTitle>Waiting for you {pending.length > 0 && <span style={{ color: 'var(--sem-accent)' }}>({pending.length})</span>}</SectionTitle>
      {focusNotice && <div className="mt-2"><Banner color="var(--sem-warn)">{focusNotice}</Banner></div>}
      {items === null && err ? (
        // The load itself failed — say so and offer a retry; never an eternal "Loading…" under an error.
        <div className="mt-2 flex items-start gap-2">
          <div className="flex-1 min-w-0"><Banner color="var(--sem-bad)">Couldn’t load the approval queue: {err}</Banner></div>
          <button onClick={() => { setErr(null); void pull(); }} className="sem-btn shrink-0">
            Retry
          </button>
        </div>
      ) : items === null ? (
        <div className="text-(length:--sem-text-meta) mt-2" style={{ color: 'var(--sem-muted)' }}>Loading…</div>
      ) : (
        <>
          {err && <div className="mt-2"><Banner color="var(--sem-bad)">{err}</Banner></div>}
          <div className="flex flex-col mt-2">
            {pending.map((r) => (
              <div key={r.id} ref={(node) => { if (node) cards.current.set(r.id, node); else cards.current.delete(r.id); }} tabIndex={-1}
                className="py-3 outline-none border-b"
                style={{ borderColor: 'var(--sem-rule)', boxShadow: directedId === r.id ? 'inset 2px 0 0 var(--sem-warn)' : undefined }}>
                <div className="flex items-start gap-3">
                  <div className="flex-1 min-w-0 sem-row-line">
                    <div className="text-(length:--sem-text-meta) font-medium">{r.summary || capabilityLabel(r.capability)}</div>
                    <div className="flex items-center gap-2 mt-1 flex-wrap">
                      <span className="text-(length:--sem-text-label)" style={{ color: 'var(--sem-muted)' }} title={`Capability: ${r.capability} · label: ${r.label}`}>{capabilityLabel(r.capability)}</span>
                      <Pill tint={LABEL_TINT[r.label] ?? 'var(--sem-muted)'}>{LABEL_WORD[r.label as Label] ?? r.label}</Pill>
                      {r.target && <span className="text-(length:--sem-text-label) tnum" style={{ color: 'var(--sem-muted)' }} title={r.target}>{truncMid(r.target, 44)}</span>}
                      <span className="text-(length:--sem-text-label)" style={{ color: 'var(--sem-muted)' }}>· asked {relTime(r.requestedUtc, now)}</span>
                    </div>
                    {grantScopeLine(r) && (
                      <div className="text-(length:--sem-text-label) mt-1" style={{ color: 'var(--sem-muted)' }}>{grantScopeLine(r)}</div>
                    )}
                  </div>
                  <div className="flex items-center gap-2 shrink-0">
                    <MiniAction color="var(--sem-bad)" disabled={busyId === r.id} onClick={() => act(r.id, 'denyAgentAction')}>Deny</MiniAction>
                    <MiniAction color="var(--sem-good)" disabled={busyId === r.id} onClick={() => act(r.id, 'approveAgentAction')}>Approve</MiniAction>
                  </div>
                </div>
              </div>
            ))}
            {live.map((r) => (
              // Never claim a capability the engine would refuse: ApprovalLedger.IsLiveGrant requires now < expiry, so only
              // a live grant is listed, driven by the ticking `now` so it leaves on the clock, not on fresh data.
              <div key={r.id} className="py-2 flex items-center gap-2 text-(length:--sem-text-label)" style={{ color: 'var(--sem-muted)' }}>
                <span style={{ color: 'var(--sem-good)' }}><ActionIcon action="allow" /></span>
                <span>{r.summary || capabilityLabel(r.capability)}: approved, {r.capability === 'QueryData'
                  ? 'your assistant can preview rows here'
                  : 'waiting for your assistant'}{expiresIn(r.expiresUtc, now)}.</span>
              </div>
            ))}
          </div>
        </>
      )}
    </Panel>
  );
}

// ---- Labels: one line, because each model's label is set in Connections -------------------------
/** How many published models have no label, so the guardrail treats them as production. A model on this computer reads
 *  Local in the Connections list, so it is not counted. Null while the list is unread. */
export function unlabelledCount(conns: ConnectionRecord[] | null): number | null {
  if (!conns) return null;
  return conns.filter((c) => c.kind !== 'localDesktop' && c.kind !== 'file' && (!c.label || !(LABELS as readonly string[]).includes(c.label))).length;
}

export function labelsLine(unlabelled: number | null): string {
  const base = 'Labels are set on each model in Connections.';
  if (!unlabelled) return base;
  return `${base} ${unlabelled === 1 ? '1 model has no label, so it gets' : `${unlabelled} models have no label, so they get`} the Production settings.`;
}

function LabelsLine() {
  const mounted = useMountedRef();
  const { openConnections } = useConnection();
  const [conns, setConns] = useState<ConnectionRecord[] | null>(null);
  useEffect(() => {
    let cancelled = false;                       // scoped to THIS effect — StrictMode-safe
    const kick = () => { rpc<ConnectionRecord[]>('listConnections').then((r) => { if (mounted.current && !cancelled) setConns(r ?? []); }).catch(() => undefined); };
    kick();
    const off = onReconnect(kick);
    return () => { cancelled = true; off(); };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);
  return (
    <div data-testid="perm-labels-line" className="text-(length:--sem-text-meta) px-1" style={{ color: 'var(--sem-muted)' }}>
      {labelsLine(unlabelledCount(conns))}{' '}
      <button type="button" className="sem-btn sem-btn-sm sem-btn-quiet" data-testid="perm-open-connections" onClick={() => openConnections()}>Open Connections</button>
    </div>
  );
}

const LABEL_TINT: Record<string, string> = {
  local: 'var(--sem-good)', dev: 'var(--sem-accent)', uat: 'var(--sem-warn)', prod: 'var(--sem-bad)',
};

// ---- Footer: the honest boundary line ------------------------------------------------------------
function FooterLine() {
  return (
    <div className="text-(length:--sem-text-label)  px-1 pb-2" style={{ color: 'var(--sem-muted)' }}>
      This stops accidents. It is not a security boundary: the sign-in you give your assistant is.
    </div>
  );
}

// ---- small primitives ----------------------------------------------------------------------------
function MiniAction({ children, color, onClick, disabled }: { children: React.ReactNode; color: string; onClick: () => void; disabled?: boolean }) {
  return (
    <button onClick={onClick} disabled={disabled}
      className="text-(length:--sem-text-label) px-3 py-1 rounded font-semibold transition-opacity disabled:opacity-40"
      style={{ color, background: `color-mix(in srgb, ${color} 14%, transparent)`, border: `1px solid color-mix(in srgb, ${color} 36%, transparent)` }}>
      {children}
    </button>
  );
}

// ---- helpers -------------------------------------------------------------------------------------
function capabilityLabel(cap: string): string {
  const r = ROWS.find((x) => x.cap === cap);
  return r ? r.title : cap;
}
// Truncate the MIDDLE of a long string (endpoints read best with both ends kept — server + database tail).
function truncMid(s: string, max: number): string {
  if (!s || s.length <= max) return s || '';
  const keep = max - 1, head = Math.ceil(keep / 2), tail = Math.floor(keep / 2);
  return s.slice(0, head) + '…' + s.slice(s.length - tail);
}
function relTime(iso: string, now: number): string {
  const t = Date.parse(iso); if (isNaN(t)) return '';
  const s = Math.max(0, Math.round((now - t) / 1000));
  if (s < 45) return 'just now';
  const m = Math.round(s / 60); if (m < 60) return `${m}m ago`;
  const h = Math.round(m / 60); if (h < 24) return `${h}h ago`;
  return `${Math.round(h / 24)}d ago`;
}
// A granted record is expired for display exactly when the engine refuses it: ApprovalLedger.IsLiveGrant is
// live only while now < ExpiresUtc, and a missing/unparsable expiry on a granted record is malformed = dead,
// not immortal (mirrors the ledger's fail-closed reading). So the UI can never out-claim the engine.
function isExpired(iso: string | null | undefined, now: number): boolean {
  const t = iso ? Date.parse(iso) : NaN;
  return isNaN(t) || t <= now;
}
// The live-grant countdown suffix. Callers branch on isExpired first, so this only renders for a live grant;
// the '(expired)' fallback keeps it honest if it is ever called on a past record.
function expiresIn(iso: string | null | undefined, now: number): string {
  if (!iso) return '';
  const t = Date.parse(iso); if (isNaN(t)) return '';
  if (t <= now) return ' (expired)';
  const m = Math.round((t - now) / 60000);
  return m >= 1 ? ` (expires in ${m}m)` : ' (expiring soon)';
}

// ---- icons (stroke=currentColor, colour set by the caller) ---------------------------------------
function ActionIcon({ action }: { action: Action }) {
  if (action === 'allow') return (
    <svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.5" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true"><path d="M20 6 9 17l-5-5" /></svg>
  );
  if (action === 'ask') return (
    <svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true"><circle cx="12" cy="12" r="9" /><path d="M9.1 9a3 3 0 0 1 5.8 1c0 2-3 3-3 3" /><path d="M12 17h.01" /></svg>
  );
  return (
    <svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true"><circle cx="12" cy="12" r="9" /><path d="m5.6 5.6 12.8 12.8" /></svg>
  );
}
function ShieldIcon() {
  return <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="var(--sem-accent)" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true"><path d="M12 2 4 5v6c0 5 3.5 8.5 8 11 4.5-2.5 8-6 8-11V5Z" /></svg>;
}
function EyeIcon() {
  return <svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true"><path d="M2 12s3.5-7 10-7 10 7 10 7-3.5 7-10 7-10-7-10-7Z" /><circle cx="12" cy="12" r="3" /></svg>;
}
function LockIcon() {
  return <svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true"><rect x="4" y="11" width="16" height="10" rx="2" /><path d="M8 11V7a4 4 0 0 1 8 0v4" /></svg>;
}
function WarnDot() {
  return <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="var(--sem-warn)" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true" style={{ flexShrink: 0 }}><path d="M12 9v4" /><path d="M12 17h.01" /><circle cx="12" cy="12" r="9" /></svg>;
}
function ClockIcon() {
  return <svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true"><circle cx="12" cy="12" r="9" /><path d="M12 7v5l3 2" /></svg>;
}
