import { useEffect, useRef, useState } from 'react';
import { rpc, onReconnect } from './bridge';
import { Panel, Banner, SectionTitle, Pill } from './workflows';
import { useTier, ProBadge, UpsellNotice, isEntitlementError } from './pro';
import { uiLabel } from './copy';

// ===================================================================================================
// Permissions — the human door onto the agent-policy engine (PR #128). This page IS the human: every
// MUTATING call passes origin='human', which is exactly the door the engine trusts to change the
// policy, approve a request, or label a target. (The read methods take no origin at all — reads are
// structurally ungated by design.) Four surfaces, top to bottom:
//   1. Guardrail switch + preset picker (header)  — the switch is FREE, presets are Pro.
//   2. The capability × target matrix              — plain-language rows; engine enum in a tooltip.
//   3. "Waiting for you" approval queue            — polled while the tab is open.
//   4. Targets (the connection registry)           — the labelling home the refusal messages point at.
// Copy rule: "the AI assistant", never "Claude". Plain language; engine terms live only in tooltips.
// This guardrail is a governance control against ACCIDENTS, not a boundary against a hostile agent —
// the credential the user hands the assistant is the real boundary (stated in the footer).
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
interface AgentPolicy { enabled: boolean; preset: string; matrix: Record<string, Record<string, string>>; }
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
    return `Approving allows all read-rows operations on this target (previews, DAX row queries, pivots, debug logging) until it expires in ${mins} minutes.`;
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

const errMsg = (e: unknown) => String((e as Error)?.message ?? e);
// The policy-store's Pro refusal is worded differently from EntitlementGuard's stock phrase, so match BOTH
// (this file's own belt-and-braces on top of the shared isEntitlementError).
const proGated = (e: unknown) => isEntitlementError(e) || /pro feature/i.test(errMsg(e));

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
  { id: 'open', name: 'Open', desc: 'No friction, even production.' },
  { id: 'standard', name: 'Standard', desc: 'Asks before touching UAT or production.' },
  { id: 'cautious', name: 'Cautious', desc: 'Asks for anything beyond local.' },
  { id: 'client', name: 'Client', desc: 'Production is a wall; no data preview off UAT or production.' },
  { id: 'locked', name: 'Locked', desc: 'Maximum separation; asks even locally.' },
];

// The matrix rows, in reading order. `cap` is the engine capability (shown only in a tooltip); `kind` decides how a
// row renders: an editable gated row, a structurally-always-allow local row, or a pinned info strip (allow/deny).
type RowKind = 'gated' | 'localAllow' | 'infoAllow' | 'infoDeny';
interface RowDef { cap: string; title: string; sub: string; kind: RowKind; }
const ROWS: RowDef[] = [
  { cap: 'Read / QueryCalc', title: 'Read & analyze', sub: 'List, inspect, scan, and run measure calculations.', kind: 'infoAllow' },
  { cap: 'QueryData', title: 'Preview data', sub: 'Show rows of source data: table previews, measure pivots, and row-returning DAX queries. Calculation checks can still show the category labels they group by.', kind: 'gated' },
  { cap: 'EditLocal', title: 'Edit the working model', sub: 'Never touches a live target.', kind: 'localAllow' },
  { cap: 'DeployFile', title: 'Write model files', sub: 'Never touches a live target.', kind: 'localAllow' },
  { cap: 'DeployLive', title: 'Deploy to a published model', sub: 'Push metadata changes to a live model.', kind: 'gated' },
  { cap: 'DeployDelete', title: 'Delete from a published model', sub: 'Remove an object from a live model. Irreversible.', kind: 'gated' },
  { cap: 'Rollback', title: 'Roll back a published model', sub: 'Restore a live model to an earlier checkpoint.', kind: 'gated' },
  { cap: 'Refresh', title: 'Refresh live data', sub: 'Refresh a partition on the live model.', kind: 'gated' },
  { cap: 'Governance', title: 'Change these settings', sub: 'Never allowed for the assistant.', kind: 'infoDeny' },
];

const TEACH_PRESET = 'Choosing a preset is a Pro feature. The default guardrail and the on/off switch are free; Pro lets you pick a posture and tune any cell.';
const TEACH_CELL = 'Tuning an individual permission is a Pro feature. The default guardrail and the on/off switch are free; Pro unlocks per-target control.';

// ---------------------------------------------------------------------------------------------------
export function PermissionsView({ focusApproval, onApprovalChanged }: {
  focusApproval?: { id: string; nonce: number } | null;
  onApprovalChanged?: () => void;
}) {
  const tier = useTier();                 // 'unknown' until the entitlement answers — a quiet checking state
  const isPro = tier === 'pro';
  const isFree = tier === 'free';
  const mounted = useMountedRef();

  const [policy, setPolicy] = useState<AgentPolicy | null>(null);
  const [policyErr, setPolicyErr] = useState<string | null>(null);   // getAgentPolicy failed (load, or retry)
  const [writeErr, setWriteErr] = useState<string | null>(null);     // a policy write failed for a non-Pro reason
  const [upsell, setUpsell] = useState<string | null>(null);
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

  const teach = (msg: string) => { setUpsell(msg); setWriteErr(null); };

  // One serialized, generation-guarded policy write: optimistic locally, latest response wins, rollback
  // on failure. `optimistic` may be identity when the client can't know the result (a preset swap builds
  // a whole matrix server-side — showing a guessed matrix would be its own lie).
  async function policyWrite(key: string, run: () => Promise<AgentPolicy>, optimistic: (p: AgentPolicy) => AgentPolicy, teachMsg?: string) {
    if (!policy || inflight) return;             // controls are disabled in these states; belt-and-braces
    const before = policy;                       // exact rollback snapshot (writes are serialized)
    const gen = ++genRef.current;
    setInflight(key);
    setWriteErr(null); setUpsell(null);
    setPolicy(optimistic(before));
    try {
      const p = await run();
      if (mounted.current && gen === genRef.current) setPolicy(p);
    } catch (e) {
      if (mounted.current && gen === genRef.current) {
        setPolicy(before);                       // never leave the optimistic state standing over a refusal
        if (teachMsg && proGated(e)) teach(teachMsg); else setWriteErr(errMsg(e));
      }
    } finally {
      if (mounted.current) setInflight((cur) => (cur === key ? null : cur));
    }
  }

  const setPreset = (id: string) => {
    if (isFree) { teach(TEACH_PRESET); return; }
    void policyWrite(`preset:${id}`, () => rpc<AgentPolicy>('setAgentPolicyPreset', id, 'human'), (p) => p, TEACH_PRESET);
  };
  const setCell = (cap: string, label: Label, action: Action) => {
    void policyWrite(`cell:${cap}:${label}`, () => rpc<AgentPolicy>('setAgentPolicyCell', cap, label, action, 'human'),
      (p) => ({ ...p, preset: 'custom', matrix: { ...p.matrix, [cap]: { ...(p.matrix?.[cap] ?? {}), [label]: action } } }),
      TEACH_CELL);
  };
  // The kill-switch is FREE and operable as soon as the policy is known — a user who finds the guardrail
  // in the way must be able to turn it off honestly rather than route around it.
  const setEnabled = (enabled: boolean) => {
    void policyWrite('switch', () => rpc<AgentPolicy>('setAgentPolicyEnabled', enabled, 'human'), (p) => ({ ...p, enabled }));
  };

  return (
    <div className="h-full overflow-auto">
      <div className="sem-evidence-page sem-centered-page flex flex-col gap-4 min-w-0">
        <HeaderPanel policy={policy} policyErr={policyErr} tier={tier} inflight={inflight}
          onToggle={setEnabled} onPreset={setPreset} onRetry={loadPolicy} />
        {writeErr && <Banner color="var(--sem-bad)">{writeErr}</Banner>}
        {upsell && <UpsellNotice onDismiss={() => setUpsell(null)}>{upsell}</UpsellNotice>}
        {policy && <MatrixPanel policy={policy} isPro={isPro} isFree={isFree} inflight={inflight}
          onCell={setCell} onLocked={() => teach(TEACH_CELL)} />}
        <ApprovalsPanel focusApproval={focusApproval} onApprovalChanged={onApprovalChanged} />
        <TargetsPanel />
        <FooterLine />
      </div>
    </div>
  );
}

// ---- Header: intro + the free guardrail switch + the preset picker -------------------------------
function HeaderPanel({ policy, policyErr, tier, inflight, onToggle, onPreset, onRetry }: {
  policy: AgentPolicy | null; policyErr: string | null; tier: string; inflight: string | null;
  onToggle: (v: boolean) => void; onPreset: (id: string) => void; onRetry: () => void;
}) {
  const isCustom = policy?.preset === 'custom';
  const isUnreadable = policy?.preset === 'unreadable';
  return (
    <Panel>
      <div className="flex items-start gap-4">
        <div className="flex-1 min-w-0">
          <div className="flex items-center gap-2">
            <ShieldIcon />
            <div className="text-[15px] font-semibold">Agent permissions</div>
          </div>
          <div className="text-[12px] mt-1" style={{ color: 'var(--sem-muted)' }}>
            Decide what your AI Assistant may do on its own, and where. A preset sets a sensible baseline; tune any
            cell for full control. This governs the assistant to prevent accidents; it is not a security boundary.
          </div>
        </div>
        {/* The switch renders ONLY once the policy is known — a toggle with a defaulted position would be a
            governance-status lie. While loading/error, its slot says so instead. */}
        {policy
          ? <GuardrailSwitch enabled={policy.enabled} disabled={!!inflight} onToggle={onToggle} />
          : <div className="text-[11px] shrink-0 pt-1" style={{ color: 'var(--sem-muted)' }}>{policyErr ? 'unavailable' : 'loading…'}</div>}
      </div>

      {policyErr && !policy && (
        <div className="mt-3 flex items-start gap-2">
          <div className="flex-1 min-w-0">
            <Banner color="var(--sem-bad)">Couldn’t load the agent policy: {policyErr}</Banner>
          </div>
          <button onClick={onRetry} className="shrink-0 text-[12px] px-3 py-1.5 rounded-lg font-medium"
            style={{ background: 'var(--sem-surface-2)', color: 'var(--sem-fg)', border: '1px solid var(--sem-border)' }}>
            Retry
          </button>
        </div>
      )}
      {!policy && !policyErr && (
        <div className="mt-3 text-[12px]" style={{ color: 'var(--sem-muted)' }}>Loading the current policy…</div>
      )}

      {policy && isUnreadable && (
        <div className="mt-3">
          <Banner color="var(--sem-warn)">
            Your saved permission settings couldn’t be read, so the assistant is on safe fail-closed defaults:
            every live action is denied until you repair it. Pick a preset below to reset.
          </Banner>
        </div>
      )}

      {policy && (
        <div className="mt-3">
          <SectionTitle>Preset</SectionTitle>
          <div className="flex flex-wrap gap-2 mt-2">
            {PRESETS.map((p) => (
              <PresetCard key={p.id} preset={p} active={!isCustom && !isUnreadable && policy.preset === p.id}
                tier={tier} busy={inflight === `preset:${p.id}`} disabled={!!inflight}
                onClick={() => onPreset(p.id)} />
            ))}
          </div>
          {isCustom && (
            <div className="flex items-center gap-2 mt-2 text-[12px]" style={{ color: 'var(--sem-muted)' }}>
              <Pill tint="var(--sem-accent)">Custom</Pill>
              <span>Your matrix no longer matches a named preset. Keep tuning cells, or pick a preset above to reset.</span>
            </div>
          )}
        </div>
      )}
    </Panel>
  );
}

function PresetCard({ preset, active, tier, busy, disabled, onClick }: {
  preset: { id: string; name: string; desc: string }; active: boolean; tier: string; busy: boolean; disabled: boolean; onClick: () => void;
}) {
  // Free clicks stay ENABLED (they teach locally, no RPC); an in-flight write and the unknown-tier
  // checking window disable the card instead.
  const blocked = disabled || tier === 'unknown';
  return (
    <button onClick={onClick} disabled={blocked && tier !== 'free'}
      title={tier === 'unknown' ? 'Checking your plan…' : active ? 'Current preset' : `Switch to the ${preset.name} preset`}
      className="text-left rounded-lg px-3 py-2 transition-colors disabled:opacity-60"
      style={{ width: 190, background: active ? 'var(--sem-accent-soft)' : 'var(--sem-surface-2)',
        border: `1px solid ${active ? 'var(--sem-accent)' : 'var(--sem-border)'}`, opacity: busy ? 0.6 : undefined }}>
      <div className="flex items-center gap-1.5">
        <span className="text-[12.5px] font-semibold" style={{ color: 'var(--sem-fg)' }}>{preset.name}</span>
        {busy ? <span className="text-[10px]" style={{ color: 'var(--sem-muted)' }}>applying…</span>
          : active && <span className="text-[9.5px] font-bold uppercase tracking-wide" style={{ color: 'var(--sem-accent)' }}>Current</span>}
        <ProBadge show={tier === 'free'} />
      </div>
      <div className="text-[11px] mt-0.5 leading-snug" style={{ color: 'var(--sem-muted)' }}>{preset.desc}</div>
    </button>
  );
}

// The global kill-switch — a labelled toggle. Free for everyone; operable as soon as the policy is known.
function GuardrailSwitch({ enabled, disabled, onToggle }: { enabled: boolean; disabled: boolean; onToggle: (v: boolean) => void }) {
  return (
    <div className="flex flex-col items-end shrink-0">
      <div className="flex items-center gap-2">
        <span className="text-[12px] font-medium">Agent guardrail</span>
        <button role="switch" aria-checked={enabled} disabled={disabled} onClick={() => onToggle(!enabled)}
          title={enabled ? 'On: the assistant is being gated. Click to turn off.' : 'Off: the assistant is not being gated. Click to turn on.'}
          className="relative rounded-full transition-colors disabled:opacity-60" style={{ width: 40, height: 22,
            background: enabled ? 'var(--sem-accent)' : 'var(--sem-surface-2)', border: '1px solid var(--sem-border)' }}>
          <span className="absolute rounded-full transition-all" style={{ width: 16, height: 16, top: 2,
            left: enabled ? 20 : 3, background: enabled ? 'var(--sem-on-accent)' : 'var(--sem-muted)' }} />
        </button>
        <span className="text-[11px] tnum w-6" style={{ color: enabled ? 'var(--sem-accent)' : 'var(--sem-muted)' }}>{enabled ? 'On' : 'Off'}</span>
      </div>
      <div className="text-[10.5px] mt-1" style={{ color: 'var(--sem-muted)' }}>Free · always available</div>
    </div>
  );
}

// ---- The capability × target matrix --------------------------------------------------------------
function MatrixPanel({ policy, isPro, isFree, inflight, onCell, onLocked }: {
  policy: AgentPolicy; isPro: boolean; isFree: boolean; inflight: string | null;
  onCell: (cap: string, label: Label, action: Action) => void; onLocked: () => void;
}) {
  const off = !policy.enabled;
  const grid = ROWS.filter((r) => r.kind === 'gated' || r.kind === 'localAllow');
  const infoTop = ROWS.find((r) => r.kind === 'infoAllow')!;
  const infoBottom = ROWS.find((r) => r.kind === 'infoDeny')!;
  const cols = `minmax(230px,1.5fr) repeat(4, minmax(78px,1fr))`;

  return (
    <Panel>
      <SectionTitle>What the assistant may do</SectionTitle>
      <div className="text-[11.5px] mt-1" style={{ color: 'var(--sem-muted)' }}>
        Columns are your target labels. <strong style={{ color: 'var(--sem-fg)' }}>Unlabelled targets are treated as production.</strong>
        {isPro && !off && <> Click a cell to change it (allow → ask → deny).</>}
      </div>

      {off && (
        <div className="flex items-center gap-2 mt-3 rounded-lg px-3 py-2 text-[12px]"
          style={{ background: 'color-mix(in srgb, var(--sem-warn) 12%, transparent)', color: 'var(--sem-fg)', border: '1px solid color-mix(in srgb, var(--sem-warn) 32%, transparent)' }}>
          <WarnDot />
          <span>
            The guardrail is off. The assistant is not being gated by this matrix. The fixed rules still apply:
            reading is always allowed, and the assistant can never change these settings.
          </span>
        </div>
      )}

      {/* Pinned info strip — always allowed, structural (not per-target). Stays bright when the guardrail is
          off: it is a fact about the engine, not a cell of the (stood-down) matrix. */}
      <div className="mt-3"><InfoStrip kind="allow" row={infoTop} /></div>

      <div className="mt-2 rounded-xl overflow-hidden" style={{ border: '1px solid var(--sem-border)' }}>
        {/* header */}
        <div style={{ display: 'grid', gridTemplateColumns: cols, background: 'var(--sem-surface-2)' }}>
          <div className="px-3 py-2 text-[11px] uppercase tracking-wide font-semibold" style={{ color: 'var(--sem-muted)' }}>Capability</div>
          {LABELS.map((l) => (
            <div key={l} className="px-2 py-2 text-center text-[11px] uppercase tracking-wide font-semibold" style={{ color: 'var(--sem-muted)' }}>
              {l}{l === 'prod' && <span title="Unlabelled targets are treated as production" style={{ color: 'var(--sem-warn)' }}> *</span>}
            </div>
          ))}
        </div>
        {/* body */}
        {grid.map((row, i) => (
          <div key={row.cap} style={{ display: 'grid', gridTemplateColumns: cols,
            borderTop: '1px solid var(--sem-border)', background: i % 2 ? 'color-mix(in srgb, var(--sem-surface-2) 40%, transparent)' : 'transparent' }}>
            <RowLabel row={row} />
            {LABELS.map((l) => {
              const key = `cell:${row.cap}:${l}`;
              const action = (policy.matrix?.[row.cap]?.[l] as Action) ?? 'deny';
              return (
                <div key={l} className="flex items-center justify-center px-2 py-2">
                  {row.kind === 'localAllow' ? (
                    // A structural fact, not a policy choice — a distinct outline "Always" chip, never dimmed.
                    <FixedChip />
                  ) : off ? (
                    // Guardrail off: only the TUNABLE cells dim (they are what stood down).
                    <span style={{ opacity: 0.35 }}><ActionChip action={action} /></span>
                  ) : isPro ? (
                    <ActionCell action={action} disabled={!!inflight} pending={inflight === key}
                      onClick={() => onCell(row.cap, l, next(action))} />
                  ) : isFree ? (
                    <LockedCell action={action} onClick={onLocked} />
                  ) : (
                    // Tier still resolving — a quiet read-only chip, neither editable nor lock-adorned.
                    <span title="Checking your plan…"><ActionChip action={action} /></span>
                  )}
                </div>
              );
            })}
          </div>
        ))}
      </div>

      {/* Pinned info strip — never allowed, structural (holds even with the guardrail off). */}
      <div className="mt-2"><InfoStrip kind="deny" row={infoBottom} /></div>
    </Panel>
  );
}

function RowLabel({ row }: { row: RowDef }) {
  return (
    <div className="px-3 py-2 min-w-0">
      <div className="flex items-center gap-1.5 flex-wrap">
        <span className="text-[12.5px] font-medium" title={`Capability: ${row.cap}`}>{row.title}</span>
      </div>
      <div className="text-[11px] mt-0.5" style={{ color: 'var(--sem-muted)' }}>{row.sub}</div>
    </div>
  );
}

// A pinned, non-editable strip for the two structural rows: always-allow (Read) and never-allow (Governance).
function InfoStrip({ kind, row }: { kind: 'allow' | 'deny'; row: RowDef }) {
  const allow = kind === 'allow';
  const color = allow ? 'var(--sem-good)' : 'var(--sem-bad)';
  return (
    <div className="flex items-center gap-3 rounded-lg px-3 py-2" style={{ background: 'var(--sem-surface-2)', border: '1px solid var(--sem-border)' }}>
      <span style={{ color }}>{allow ? <EyeIcon /> : <LockIcon />}</span>
      <div className="flex-1 min-w-0">
        <span className="text-[12.5px] font-medium" title={`Capability: ${row.cap}`}>{row.title}</span>
        <span className="text-[11px] ml-2" style={{ color: 'var(--sem-muted)' }}>{row.sub}</span>
      </div>
      <span className="text-[11px] font-semibold px-2 py-0.5 rounded" style={{ color, background: `color-mix(in srgb, ${color} 14%, transparent)` }}>
        {allow ? 'Always allowed' : 'Never allowed'}
      </span>
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
    <span className="inline-flex items-center gap-1 text-[11px] font-semibold px-2 py-1 rounded-md"
      style={{ color: m.color, background: `color-mix(in srgb, ${m.color} 16%, transparent)` }}>
      <ActionIcon action={action} />{m.label}
    </span>
  );
}

// A structurally-fixed cell: an outline "Always" chip, visually distinct from a policy-derived Allow so a
// fact never reads as a choice.
function FixedChip() {
  return (
    <span title="Fixed: this never touches a live target, so it is always allowed."
      className="inline-flex items-center gap-1 text-[11px] font-medium px-2 py-1 rounded-md"
      style={{ color: 'var(--sem-muted)', background: 'transparent', border: '1px dashed var(--sem-border)' }}>
      <ActionIcon action="allow" />Always
    </span>
  );
}

// The Pro editable cell: cycles allow → ask → deny. Disabled while ANY policy write is in flight (writes
// are serialized so clicks can't stack); the cell whose write is running shows a pending fade.
function ActionCell({ action, disabled, pending, onClick }: { action: Action; disabled: boolean; pending: boolean; onClick: () => void }) {
  const m = ACTION_META[action];
  return (
    <button onClick={onClick} disabled={disabled} title={`${m.label}. Click to cycle: allow → ask → deny.`}
      className="inline-flex items-center gap-1 text-[11px] font-semibold px-2 py-1 rounded-md transition-colors"
      style={{ color: m.color, background: `color-mix(in srgb, ${m.color} 16%, transparent)`,
        border: `1px solid color-mix(in srgb, ${m.color} 34%, transparent)`, opacity: pending ? 0.55 : undefined }}>
      <ActionIcon action={action} />{m.label}
    </button>
  );
}

// The Free-tier cell: read-only, but never a dead end — the lock is the affordance, and a click teaches
// (a local upsell notice; no RPC is attempted).
function LockedCell({ action, onClick }: { action: Action; onClick: () => void }) {
  const m = ACTION_META[action];
  return (
    <button onClick={onClick} title={`${m.label}. Configuring the matrix is a Pro feature; click to learn more.`}
      className="inline-flex items-center gap-1 text-[11px] font-semibold px-2 py-1 rounded-md"
      style={{ color: m.color, background: `color-mix(in srgb, ${m.color} 16%, transparent)` }}>
      <ActionIcon action={action} />{m.label}
      <span style={{ color: 'var(--sem-muted)', display: 'inline-flex' }}><LockMini /></span>
    </button>
  );
}

// ---- "Waiting for you" — the approval queue ------------------------------------------------------
function ApprovalsPanel({ focusApproval, onApprovalChanged }: {
  focusApproval?: { id: string; nonce: number } | null;
  onApprovalChanged?: () => void;
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
      return;
    }
    setFocusNotice(null);
    setDirectedId(focusApproval.id);
    card.scrollIntoView({ behavior: 'smooth', block: 'center' });
    card.focus({ preventScroll: true });
    const clear = window.setTimeout(() => setDirectedId((id) => id === focusApproval.id ? null : id), 1800);
    return () => window.clearTimeout(clear);
  }, [focusApproval?.id, focusApproval?.nonce, items]);

  return (
    <Panel>
      <SectionTitle>Waiting for you {pending.length > 0 && <span style={{ color: 'var(--sem-accent)' }}>({pending.length})</span>}</SectionTitle>
      {focusNotice && <div className="mt-2"><Banner color="var(--sem-warn)">{focusNotice}</Banner></div>}
      {items === null && err ? (
        // The load itself failed — say so and offer a retry; never an eternal "Loading…" under an error.
        <div className="mt-2 flex items-start gap-2">
          <div className="flex-1 min-w-0"><Banner color="var(--sem-bad)">Couldn’t load the approval queue: {err}</Banner></div>
          <button onClick={() => { setErr(null); void pull(); }} className="shrink-0 text-[12px] px-3 py-1.5 rounded-lg font-medium"
            style={{ background: 'var(--sem-surface-2)', color: 'var(--sem-fg)', border: '1px solid var(--sem-border)' }}>
            Retry
          </button>
        </div>
      ) : items === null ? (
        <div className="text-[12px] mt-2" style={{ color: 'var(--sem-muted)' }}>Loading…</div>
      ) : (
        <>
          {err && <div className="mt-2"><Banner color="var(--sem-bad)">{err}</Banner></div>}
          {list.length === 0 ? (
            <div className="text-[12px] mt-2" style={{ color: 'var(--sem-muted)' }}>
              Nothing waiting. When the assistant needs your approval, it appears here.
            </div>
          ) : (
            <div className="flex flex-col gap-2 mt-2">
              {pending.map((r) => (
                <div key={r.id} ref={(node) => { if (node) cards.current.set(r.id, node); else cards.current.delete(r.id); }} tabIndex={-1}
                  className="rounded-lg px-3 py-2.5 outline-none"
                  style={{ background: 'var(--sem-surface-2)', border: `1px solid ${directedId === r.id ? 'var(--sem-warn)' : 'var(--sem-border)'}`, boxShadow: directedId === r.id ? '0 0 0 2px color-mix(in srgb, var(--sem-warn) 24%, transparent)' : undefined }}>
                  <div className="flex items-start gap-3">
                    <div className="flex-1 min-w-0">
                      <div className="text-[12.5px] font-medium">{r.summary || capabilityLabel(r.capability)}</div>
                      <div className="flex items-center gap-1.5 mt-1 flex-wrap">
                        <Pill tint={LABEL_TINT[r.label] ?? 'var(--sem-muted)'} title={`Capability: ${r.capability} · label: ${r.label}`}>{capabilityLabel(r.capability)}</Pill>
                        <Pill tint={LABEL_TINT[r.label] ?? 'var(--sem-muted)'}>{r.label}</Pill>
                        {r.target && <span className="text-[11px] tnum" style={{ color: 'var(--sem-muted)' }} title={r.target}>{truncMid(r.target, 44)}</span>}
                        <span className="text-[11px]" style={{ color: 'var(--sem-muted)' }}>· asked {relTime(r.requestedUtc, now)}</span>
                      </div>
                      {grantScopeLine(r) && (
                        <div className="text-[11px] mt-1" style={{ color: 'var(--sem-muted)' }}>{grantScopeLine(r)}</div>
                      )}
                    </div>
                    <div className="flex items-center gap-1.5 shrink-0">
                      <MiniAction color="var(--sem-good)" disabled={busyId === r.id} onClick={() => act(r.id, 'approveAgentAction')}>Approve</MiniAction>
                      <MiniAction color="var(--sem-bad)" disabled={busyId === r.id} onClick={() => act(r.id, 'denyAgentAction')}>Deny</MiniAction>
                    </div>
                  </div>
                </div>
              ))}
              {granted.map((r) => {
                // Never claim a capability the engine would refuse: ApprovalLedger.IsLiveGrant requires
                // now < expiry, so once the clock passes it the "can run" sentence is false even though the
                // record is still in `items` (the poll's catch keeps stale items across an RPC outage).
                // Driven by the ticking `now`, not by fresh data, so it flips to expired on the clock.
                const expired = isExpired(r.expiresUtc, now);
                const tint = expired ? 'var(--sem-muted)' : 'var(--sem-good)';
                return (
                  <div key={r.id} className="rounded-lg px-3 py-2.5 flex items-center gap-3"
                    style={{ background: `color-mix(in srgb, ${tint} 8%, transparent)`, border: `1px solid color-mix(in srgb, ${tint} 26%, transparent)` }}>
                    <span style={{ color: tint }}>{expired ? <ClockIcon /> : <ActionIcon action="allow" />}</span>
                    <div className="flex-1 min-w-0">
                      <div className="text-[12.5px]">{r.summary || capabilityLabel(r.capability)}</div>
                      <div className="text-[11px] mt-0.5" style={{ color: 'var(--sem-muted)' }}>
                        {expired
                          ? <>This approval has expired. The assistant must ask again.</>
                          : r.capability === 'QueryData'
                            ? <>Approved. The assistant can run read-rows operations on this target{expiresIn(r.expiresUtc, now)}.</>
                            : <>Approved. Waiting for the assistant to act{expiresIn(r.expiresUtc, now)}.</>}
                      </div>
                    </div>
                  </div>
                );
              })}
            </div>
          )}
        </>
      )}
    </Panel>
  );
}

// ---- Targets — the connection registry (the labelling home) --------------------------------------
function TargetsPanel() {
  const mounted = useMountedRef();
  const [conns, setConns] = useState<ConnectionRecord[] | null>(null);
  const [err, setErr] = useState<string | null>(null);
  const [busyId, setBusyId] = useState<string | null>(null);
  // Latest-wins generation, same pattern as the approvals: a stale listConnections response must never
  // resurrect a forgotten row or an old label. Mutations bump the ticket before they run.
  const genRef = useRef(0);

  const pull = () => {
    const gen = ++genRef.current;
    return rpc<ConnectionRecord[]>('listConnections')
      .then((r) => { if (mounted.current && gen === genRef.current) { setConns(r ?? []); setErr(null); } })
      .catch((e) => { if (mounted.current && gen === genRef.current) setErr(errMsg(e)); });
  };
  useEffect(() => {
    let cancelled = false;                       // scoped to THIS effect — StrictMode-safe
    const kick = () => { if (!cancelled) void pull(); };
    kick();
    const off = onReconnect(kick);
    return () => { cancelled = true; off(); };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const mutate = async (id: string, run: () => Promise<unknown>) => {
    setBusyId(id);
    genRef.current++;                            // invalidate any list response already in flight
    try { await run(); if (mounted.current) await pull(); }
    catch (e) { if (mounted.current) setErr(errMsg(e)); }
    finally { if (mounted.current) setBusyId(null); }
  };
  const label = (id: string, lbl: string) => mutate(id, () => rpc('labelConnection', id, lbl, 'human'));
  const forget = (id: string) => mutate(id, () => rpc('forgetConnection', id, 'human'));

  const list = conns ?? [];
  return (
    <Panel>
      <SectionTitle>Targets</SectionTitle>
      <div className="text-[11.5px] mt-1" style={{ color: 'var(--sem-muted)' }}>
        Label each connection so the guardrail knows what it is. An unlabelled target is treated as production.
        Labelling is free: it is the declaration the assistant’s permissions are gated on.
      </div>
      {conns === null && err ? (
        <div className="mt-2 flex items-start gap-2">
          <div className="flex-1 min-w-0"><Banner color="var(--sem-bad)">Couldn’t load the connection list: {err}</Banner></div>
          <button onClick={() => { setErr(null); void pull(); }} className="shrink-0 text-[12px] px-3 py-1.5 rounded-lg font-medium"
            style={{ background: 'var(--sem-surface-2)', color: 'var(--sem-fg)', border: '1px solid var(--sem-border)' }}>
            Retry
          </button>
        </div>
      ) : conns === null ? (
        <div className="text-[12px] mt-2" style={{ color: 'var(--sem-muted)' }}>Loading…</div>
      ) : (
        <>
          {err && <div className="mt-2"><Banner color="var(--sem-bad)">{err}</Banner></div>}
          {list.length === 0 ? (
            <div className="text-[12px] mt-2" style={{ color: 'var(--sem-muted)' }}>
              No connections yet. Open a live model (Power BI Desktop or an XMLA endpoint) and it appears here to label.
            </div>
          ) : (
            <div className="flex flex-col gap-2 mt-3">
              {list.map((c) => {
                const unlabelled = !c.label || !(LABELS as readonly string[]).includes(c.label);
                return (
                  <div key={c.id} className="rounded-lg px-3 py-2.5" style={{ background: 'var(--sem-surface-2)', border: '1px solid var(--sem-border)' }}>
                    <div className="flex items-start gap-3">
                      <div className="flex-1 min-w-0">
                        <div className="flex items-center gap-2">
                          <span className="text-[12.5px] font-medium truncate">{c.modelName || c.database || 'Model'}</span>
                          {unlabelled && <Pill tint="var(--sem-warn)" title="No label set: the guardrail treats this as production.">treated as production</Pill>}
                        </div>
                        <div className="text-[11px] mt-0.5 tnum" style={{ color: 'var(--sem-muted)' }} title={c.endpoint}>
                          {truncMid(c.endpoint || uiLabel(c.kind), 52)}{c.database ? <> · <span title={c.database}>{c.database}</span></> : null}
                        </div>
                      </div>
                      <div className="shrink-0"><LabelPicker value={unlabelled ? null : (c.label as Label)} busy={busyId === c.id} onPick={(l) => void label(c.id, l)} /></div>
                    </div>
                    {unlabelled && (
                      <div className="flex justify-end mt-1.5">
                        <ConfirmButton label="Forget" busy={busyId === c.id} onConfirm={() => void forget(c.id)}
                          title="Remove this unlabelled connection from the registry" />
                      </div>
                    )}
                  </div>
                );
              })}
            </div>
          )}
        </>
      )}
    </Panel>
  );
}

const LABEL_TINT: Record<string, string> = {
  local: 'var(--sem-good)', dev: 'var(--sem-accent)', uat: 'var(--sem-warn)', prod: 'var(--sem-bad)',
};

// The per-row label picker — the four labels plus Clear (unlabelled). Free + always operable (human-only in the engine).
function LabelPicker({ value, busy, onPick }: { value: Label | null; busy: boolean; onPick: (label: string) => void }) {
  return (
    <div className="inline-flex items-center rounded-lg overflow-hidden" style={{ border: '1px solid var(--sem-border)', opacity: busy ? 0.5 : 1 }}>
      {LABELS.map((l) => {
        const active = value === l;
        return (
          <button key={l} disabled={busy} onClick={() => onPick(l)} title={`Label this target as ${l}`}
            className="text-[11px] px-2 py-1 font-medium transition-colors"
            style={active ? { background: LABEL_TINT[l], color: 'var(--sem-on-accent)' } : { background: 'transparent', color: 'var(--sem-muted)' }}>
            {l}
          </button>
        );
      })}
      <button disabled={busy} onClick={() => onPick('')} title="Clear the label (treated as production)"
        className="text-[11px] px-2 py-1 font-medium transition-colors"
        style={value === null ? { background: 'var(--sem-surface)', color: 'var(--sem-fg)' } : { background: 'transparent', color: 'var(--sem-muted)' }}>
        Clear
      </button>
    </div>
  );
}

// ---- Footer: the honest boundary line ------------------------------------------------------------
function FooterLine() {
  return (
    <div className="text-[11px] leading-relaxed px-1 pb-2" style={{ color: 'var(--sem-muted)' }}>
      This guardrail governs your AI Assistant and prevents accidents. It is not a security boundary against a hostile
      agent; the credential you give the assistant is the real boundary.
    </div>
  );
}

// ---- small primitives ----------------------------------------------------------------------------
function MiniAction({ children, color, onClick, disabled }: { children: React.ReactNode; color: string; onClick: () => void; disabled?: boolean }) {
  return (
    <button onClick={onClick} disabled={disabled}
      className="text-[11px] px-2.5 py-1 rounded-md font-semibold transition-opacity disabled:opacity-40"
      style={{ color, background: `color-mix(in srgb, ${color} 14%, transparent)`, border: `1px solid color-mix(in srgb, ${color} 36%, transparent)` }}>
      {children}
    </button>
  );
}

// An inline "are you sure?" for a destructive action — no modal, stays in the row (matches the Workflows ReasonButton feel).
function ConfirmButton({ label, title, onConfirm, busy }: { label: string; title?: string; onConfirm: () => void; busy?: boolean }) {
  const [armed, setArmed] = useState(false);
  if (!armed) {
    return (
      <button onClick={() => setArmed(true)} title={title} className="text-[11px] px-2 py-1 rounded-md font-medium"
        style={{ background: 'transparent', color: 'var(--sem-muted)', border: '1px solid var(--sem-border)' }}>{label}</button>
    );
  }
  return (
    <span className="inline-flex items-center gap-1.5">
      <span className="text-[11px]" style={{ color: 'var(--sem-muted)' }}>Forget this target?</span>
      <button disabled={busy} onClick={onConfirm} className="text-[11px] px-2 py-1 rounded-md font-semibold disabled:opacity-40"
        style={{ background: 'var(--sem-bad)', color: 'var(--sem-on-accent)' }}>{busy ? '…' : 'Yes, forget'}</button>
      <button onClick={() => setArmed(false)} className="text-[11px] px-2 py-1 rounded-md" style={{ color: 'var(--sem-muted)' }}>Cancel</button>
    </span>
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
function LockMini() {
  return <svg width="9" height="9" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.5" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true"><rect x="4" y="11" width="16" height="10" rx="2" /><path d="M8 11V7a4 4 0 0 1 8 0v4" /></svg>;
}
function WarnDot() {
  return <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="var(--sem-warn)" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true" style={{ flexShrink: 0 }}><path d="M12 9v4" /><path d="M12 17h.01" /><circle cx="12" cy="12" r="9" /></svg>;
}
function ClockIcon() {
  return <svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true"><circle cx="12" cy="12" r="9" /><path d="M12 7v5l3 2" /></svg>;
}
