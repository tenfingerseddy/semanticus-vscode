import { useEffect, useMemo, useRef, useState } from 'react';
import { rpc, onPlanChange, copyText } from './bridge';
import { useTier, isEntitlementError, ProBadge, UpsellNotice } from './pro';
import { uiLabel } from './copy';

// ---- wire types (camelCase, from Semanticus.Engine/Plan.cs) ----------------------------------
interface Grounding { objectRef: string; name: string; kind: string; table?: string; expression?: string; formatString?: string; existingDescription?: string; dataType?: string; siblingNames?: string[]; }
export interface ChangeItem {
  id: string; objectRef: string; objectName: string; kind: string; source: string; category: string;
  severity: string; risk: string; ruleId?: string; target: string; title: string; rationale: string;
  before?: string; after?: string; status: string; generated?: boolean; verifyState?: string;
  note?: string; grounding?: Grounding;
}
interface PlanSummary { total: number; deterministic: number; bpa: number; ai: number; renames: number; needsContent: number; approved: number; rejected: number; applied: number; unverified: number; }
export interface ChangePlanView { planId?: string; scope?: string; revision: number; items: ChangeItem[]; summary: PlanSummary; note?: string; }
interface ApplyPlanReport { revision: number; appliedCount: number; skippedCount: number; failedCount: number; items: ChangeItem[]; bpaViolationsBefore: number; bpaViolationsAfter: number; gradeBefore: string; gradeAfter: string; overallBefore: number; overallAfter: number; note: string; }

const RISK: Record<string, { label: string; color: string }> = {
  safe: { label: 'safe', color: 'var(--sem-good)' },
  ai: { label: 'AI', color: 'var(--sem-accent)' },
  rename: { label: 'rename', color: 'var(--sem-warn)' },
  structural: { label: 'structural', color: 'var(--sem-warn)' },
  m: { label: 'M edit', color: 'var(--sem-warn)' },   // literal M edit (find/replace): not reference-fixed
};
const STATUS: Record<string, { label: string; color: string }> = {
  approved: { label: 'approved', color: 'var(--sem-good)' },
  needs_content: { label: 'needs content', color: 'var(--sem-accent)' },
  proposed: { label: 'review', color: 'var(--sem-warn)' },
  rejected: { label: 'rejected', color: 'var(--sem-muted)' },
  applied: { label: 'applied ✓', color: 'var(--sem-good)' },
  skipped: { label: 'skipped', color: 'var(--sem-warn)' },
  failed: { label: 'failed', color: 'var(--sem-bad)' },
};

type Filter = 'all' | 'deterministic' | 'ai' | 'rename' | 'needs_content';

export function OptimizeView({ seedNonce }: { seedNonce?: number } = {}) {
  const [plan, setPlan] = useState<ChangePlanView | null>(null);
  const [report, setReport] = useState<ApplyPlanReport | null>(null);
  const [busy, setBusy] = useState<'analyse' | 'apply' | 'safe' | false>(false);
  const [err, setErr] = useState<string | null>(null);
  const [upsell, setUpsell] = useState<string | null>(null);   // a free click on a bulk apply teaches, never errors
  const tier = useTier();
  const [drafts, setDrafts] = useState<Record<string, string>>({});
  const [filter, setFilter] = useState<Filter>('all');
  const [collapsed, setCollapsed] = useState<Set<string>>(new Set());   // collapsed category sections (expanded by default)

  async function load() { try { setPlan(await rpc<ChangePlanView>('getPlan')); setErr(null); } catch (e) { setErr(String((e as Error).message ?? e)); } }
  useEffect(() => {
    void load();
    const off = onPlanChange((v) => setPlan(v as ChangePlanView));
    return off;
  }, []);

  // Arrived via "Review as a plan →" (AI Readiness / BPA): seed the plan ONLY if it's currently empty, so an
  // in-progress plan (with the user's approvals/edits) is never clobbered. Otherwise just show what's there.
  const seededRef = useRef(0);
  useEffect(() => {
    if (!seedNonce || seededRef.current === seedNonce) return;
    seededRef.current = seedNonce;
    (async () => {
      const cur = await rpc<ChangePlanView>('getPlan').catch(() => null);
      if (cur) setPlan(cur);
      if (!cur || (cur.items?.length ?? 0) === 0) await analyse();
    })();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [seedNonce]);

  async function analyse() {
    setBusy('analyse'); setErr(null); setReport(null);
    try { setPlan(await rpc<ChangePlanView>('proposePlan', null, true, 40, 'human')); }
    catch (e) { setErr(String((e as Error).message ?? e)); } finally { setBusy(false); }
  }
  async function setItem(id: string, after: string | null, approved: boolean | null) {
    try { setPlan(await rpc<ChangePlanView>('setPlanItem', id, after, approved, 'human')); }
    catch (e) { setErr(String((e as Error).message ?? e)); }
  }
  async function apply(ids: string[] | null, which: 'apply' | 'safe') {
    setBusy(which); setErr(null); setUpsell(null);
    try { const r = await rpc<ApplyPlanReport>('applyPlan', ids, 'human'); setReport(r); await load(); }
    catch (e) {
      // A free click on a bulk apply gets the plain invitation, not a raw exception in a red banner.
      if (isEntitlementError(e)) setUpsell('Applying changes one at a time is free. Pro applies the whole approved set in one undoable transaction and re-checks your score.');
      else setErr(String((e as Error).message ?? e));
    } finally { setBusy(false); }
  }
  async function clear() { try { await rpc('clearPlan', 'human'); setReport(null); await load(); } catch { /* ignore */ } }

  async function copyForClaude(it: ChangeItem) {
    const g = it.grounding;
    const ctx = [
      g?.expression ? `Current DAX: ${g.expression}` : '',
      g?.siblingNames?.length ? `Sibling fields: ${g.siblingNames.slice(0, 15).join(', ')}` : '',
    ].filter(Boolean).join('. ');
    const prompt =
      `Using the Semanticus MCP, author the value for change-plan item ${it.id}: "${it.title}" on ${it.objectName} ` +
      `(${it.kind}). ${it.rationale} ${ctx} ` +
      `Then call set_plan_item("${it.id}", "<your value>") and re-check with get_plan.`;
    await copyText(prompt);
  }

  const items = plan?.items ?? [];
  const safeIds = useMemo(() => items.filter((i) => (i.source === 'deterministic' || i.source === 'bpa') && i.status === 'approved').map((i) => i.id), [items]);
  const filtered = useMemo(() => items.filter((i) => {
    switch (filter) {
      case 'deterministic': return i.source === 'deterministic' || i.source === 'bpa';
      case 'ai': return i.source === 'ai';
      case 'rename': return i.kind === 'rename';
      case 'needs_content': return i.status === 'needs_content';
      default: return true;
    }
  }), [items, filter]);
  const groups = useMemo(() => Object.entries(
    filtered.reduce((m, i) => { (m[i.category] = m[i.category] || []).push(i); return m; }, {} as Record<string, ChangeItem[]>),
  ).sort((a, b) => b[1].length - a[1].length), [filtered]);

  const s = plan?.summary;
  // A plan EXISTS whenever it has an id — even with 0 items (e.g. a Replace-all where every match was a
  // read-only reference). Hiding a 0-item plan would also hide its note, which is exactly the line that
  // explains WHY nothing could be included.
  const hasPlan = !!plan?.planId;
  const allCollapsed = groups.length > 0 && groups.every(([c]) => collapsed.has(c));
  const toggleCat = (cat: string) => setCollapsed((set) => { const n = new Set(set); n.has(cat) ? n.delete(cat) : n.add(cat); return n; });

  return (
    <div className="sem-evidence-page sem-centered-page flex flex-col gap-4">
      <Panel>
        <div className="flex items-center gap-5">
          <div className="flex flex-col items-center justify-center w-20 h-20 rounded-2xl shrink-0" style={{ background: 'var(--sem-surface-2)', boxShadow: `inset 0 0 0 2px ${hasPlan ? 'var(--sem-accent)' : 'var(--sem-border)'}` }}>
            <div className="text-3xl font-bold tnum" style={{ color: 'var(--sem-fg)' }}>{s?.total ?? 0}</div>
            <div className="text-[10px]" style={{ color: 'var(--sem-muted)' }}>changes</div>
          </div>
          <div className="flex-1 min-w-0">
            <div className="text-[15px] font-semibold">Change Plan</div>
            <div className="text-[12px] mt-0.5" style={{ color: 'var(--sem-muted)' }}>
              {hasPlan
                ? (items.length === 0
                  ? (plan?.note ? 'No changes could be planned. The note below explains why.' : 'No applicable changes were found.')
                  : <>{s!.deterministic + s!.bpa} deterministic · {s!.ai} AI-authored · {s!.renames} rename{s!.renames === 1 ? '' : 's'} · {s!.approved} approved · {s!.needsContent} need content{s!.unverified ? ` · ${s!.unverified} unverified` : ''}</>)
                : 'A reviewable “pull request for your model”: analyse, review every change as a diff, then apply the approved set in one undoable transaction.'}
            </div>
            {hasPlan && plan?.scope && <div className="text-[11px] mt-0.5" style={{ color: 'var(--sem-muted)' }}>scope: {plan.scope}</div>}
          </div>
          <div className="flex flex-col gap-2 shrink-0">
            <Button primary disabled={busy !== false} onClick={analyse}>{busy === 'analyse' ? 'Analysing…' : hasPlan ? 'Re-analyse' : 'Analyse model'}</Button>
            {hasPlan && <Button disabled={busy !== false || (s?.approved ?? 0) === 0} onClick={() => apply(null, 'apply')}
              title={tier === 'free' && (s?.approved ?? 0) > 1
                ? 'Pro applies the whole approved set in one undoable transaction. Applying one change at a time stays free.'
                : 'Apply every approved change as one undoable transaction.'}>
              {busy === 'apply' ? 'Applying…' : `Apply approved (${s?.approved ?? 0})`}
              <ProBadge show={tier === 'free' && (s?.approved ?? 0) > 1} />
            </Button>}
            {hasPlan && safeIds.length > 0 && <Button disabled={busy !== false} onClick={() => apply(safeIds, 'safe')}
              title={tier === 'free' && safeIds.length > 1
                ? 'Pro applies all the safe changes in one undoable transaction. Applying one at a time stays free.'
                : 'Apply the deterministic safe changes as one undoable transaction.'}>
              {busy === 'safe' ? 'Applying…' : `Apply safe only (${safeIds.length})`}
              <ProBadge show={tier === 'free' && safeIds.length > 1} />
            </Button>}
            {hasPlan && <Button disabled={busy !== false} onClick={clear}>Clear</Button>}
          </div>
        </div>
      </Panel>

      {err && <Banner color="var(--sem-bad)">{err}</Banner>}
      {upsell && <UpsellNotice onDismiss={() => setUpsell(null)}>{upsell}</UpsellNotice>}
      {hasPlan && plan?.note && <Banner color="var(--sem-warn)">{plan.note}</Banner>}

      {report && (
        <Panel>
          <div className="flex items-center gap-4 flex-wrap">
            <div className="text-[13px] font-semibold" style={{ color: 'var(--sem-good)' }}>{report.appliedCount} applied</div>
            {report.skippedCount > 0 && <div className="text-[12px]" style={{ color: 'var(--sem-warn)' }}>{report.skippedCount} skipped</div>}
            {report.failedCount > 0 && <div className="text-[12px]" style={{ color: 'var(--sem-bad)' }}>{report.failedCount} failed</div>}
            <Stat label="BPA" from={report.bpaViolationsBefore} to={report.bpaViolationsAfter} good="down" />
            <Stat label="Grade" from={report.gradeBefore} to={report.gradeAfter} />
            <Stat label="Score" from={report.overallBefore.toFixed(0)} to={report.overallAfter.toFixed(0)} good="up" />
          </div>
        </Panel>
      )}

      {!hasPlan && !report && (
        <Panel><div className="text-[12px]" style={{ color: 'var(--sem-muted)' }}>No change plan yet. Click <b>Analyse model</b> (or ask your AI Assistant to “propose a change plan for the whole model”) to assemble one. Nothing is changed until you apply.</div></Panel>
      )}

      {hasPlan && (
        <div className="flex items-center gap-1">
          <Chip active={filter === 'all'} onClick={() => setFilter('all')}>All {s?.total ?? 0}</Chip>
          <Chip active={filter === 'deterministic'} onClick={() => setFilter('deterministic')}>Deterministic {(s?.deterministic ?? 0) + (s?.bpa ?? 0)}</Chip>
          <Chip active={filter === 'ai'} onClick={() => setFilter('ai')}>AI {s?.ai ?? 0}</Chip>
          <Chip active={filter === 'rename'} onClick={() => setFilter('rename')}>Renames {s?.renames ?? 0}</Chip>
          <Chip active={filter === 'needs_content'} onClick={() => setFilter('needs_content')}>Needs content {s?.needsContent ?? 0}</Chip>
        </div>
      )}

      {groups.length > 1 && (
        <div className="flex items-center -mb-2">
          <button onClick={() => setCollapsed(allCollapsed ? new Set() : new Set(groups.map(([c]) => c)))}
            className="text-[10px] px-2 py-0.5 rounded-md font-medium" style={{ color: 'var(--sem-muted)', background: 'var(--sem-surface-2)' }}>
            {allCollapsed ? 'Expand all' : 'Collapse all'}
          </button>
        </div>
      )}

      {groups.map(([cat, list]) => {
        const isCollapsed = collapsed.has(cat);
        const approvedN = list.filter((i) => i.status === 'approved' || i.status === 'applied').length;
        return (
          <Panel key={cat}>
            {/* Collapsible category section — mirrors the AI Readiness / BPA Category headers. */}
            <div onClick={() => toggleCat(cat)}
              className="flex items-center gap-1.5 cursor-pointer select-none text-[11px] uppercase tracking-wide font-semibold"
              style={{ color: 'var(--sem-muted)' }}>
              <Twist open={!isCollapsed} />
              <span>{cat}</span>
              <span className="opacity-70">({list.length})</span>
              {approvedN > 0 && <span className="ml-1 normal-case font-medium" style={{ color: 'var(--sem-good)' }}>· {approvedN} approved</span>}
            </div>
            {!isCollapsed && (
              <div className="flex flex-col mt-1">
                {list.map((it) => (
                  <Row key={it.id} it={it}
                    draft={drafts[it.id] ?? ''}
                    onDraft={(v) => setDrafts((d) => ({ ...d, [it.id]: v }))}
                    onToggle={() => setItem(it.id, null, it.status !== 'approved')}
                    onSave={() => { const v = (drafts[it.id] ?? '').trim(); if (v) void setItem(it.id, v, null); }}
                    onReject={() => setItem(it.id, null, false)}
                    onCopy={() => copyForClaude(it)}
                  />
                ))}
              </div>
            )}
          </Panel>
        );
      })}
    </div>
  );
}

function Row({ it, draft, onDraft, onToggle, onSave, onReject, onCopy }: {
  it: ChangeItem; draft: string; onDraft: (v: string) => void;
  onToggle: () => void; onSave: () => void; onReject: () => void; onCopy: () => void;
}) {
  const risk = RISK[it.risk] ?? RISK.safe;
  const st = STATUS[it.status] ?? { label: uiLabel(it.status), color: 'var(--sem-muted)' };
  const approved = it.status === 'approved';
  const done = it.status === 'applied';
  const needsContent = it.status === 'needs_content';
  const isDax = it.kind === 'set_dax';

  return (
    <div className="flex items-start gap-2.5 py-2 border-b last:border-0" style={{ borderColor: 'var(--sem-border)', opacity: it.status === 'rejected' ? 0.45 : 1 }}>
      <button onClick={onToggle} disabled={done || needsContent} title={approved ? 'Approved. Click to unapprove' : 'Click to approve'}
        className="mt-0.5 w-4 h-4 rounded-[5px] shrink-0 flex items-center justify-center"
        style={{ border: `1.5px solid ${approved || done ? 'var(--sem-good)' : 'var(--sem-border)'}`, background: approved || done ? 'var(--sem-good)' : 'transparent', color: '#fff', cursor: done || needsContent ? 'default' : 'pointer' }}>
        {(approved || done) ? <span className="text-[10px] leading-none">✓</span> : null}
      </button>

      <div className="min-w-0 flex-1">
        <div className="flex items-center gap-2 flex-wrap">
          <span className="text-[10px] uppercase px-1.5 py-0.5 rounded shrink-0" style={{ background: 'var(--sem-surface-2)', color: risk.color }}>{risk.label}</span>
          <span className="text-[12px] font-medium truncate">{it.title}</span>
          <span className="text-[11px] truncate" style={{ color: 'var(--sem-muted)' }}>· {it.objectName}</span>
          <span className="ml-auto text-[10px] px-1.5 py-0.5 rounded shrink-0" style={{ color: st.color, background: 'var(--sem-surface-2)' }}>{st.label}</span>
        </div>

        {/* before → after diff */}
        <div className="mt-1 text-[11px] flex items-start gap-1.5 flex-wrap" style={{ fontFamily: isDax ? 'var(--vscode-editor-font-family, monospace)' : undefined }}>
          {it.before != null && <span className="px-1 rounded" style={{ background: 'color-mix(in srgb, var(--sem-bad) 12%, transparent)', color: 'var(--sem-muted)', textDecoration: it.after != null ? 'line-through' : undefined }}>{trunc(it.before)}</span>}
          {it.after != null
            ? <><span style={{ color: 'var(--sem-muted)' }}>→</span><span className="px-1 rounded" style={{ background: 'color-mix(in srgb, var(--sem-good) 14%, transparent)', color: 'var(--sem-fg)' }}>{trunc(it.after)}</span></>
            : <span style={{ color: 'var(--sem-accent)' }}>→ (awaiting authored value)</span>}
        </div>

        {it.note && <div className="text-[10px] mt-0.5" style={{ color: it.status === 'failed' ? 'var(--sem-bad)' : 'var(--sem-warn)' }}>{it.note}</div>}
        {it.verifyState === 'verified' && <div className="text-[10px] mt-0.5" style={{ color: 'var(--sem-good)' }}>verified equivalent ✓</div>}

        {/* AI content authoring */}
        {needsContent && !done && (
          <div className="flex items-center gap-1.5 mt-1.5">
            <input value={draft} onChange={(e) => onDraft(e.target.value)} placeholder={`Author ${it.target}…`} spellCheck={false}
              className="flex-1 text-[11px] px-2 py-1 rounded-md outline-none"
              style={{ background: 'var(--sem-surface-2)', color: 'var(--sem-fg)', border: '1px solid var(--sem-border)' }} />
            <MiniButton disabled={!draft.trim()} onClick={onSave}>Save</MiniButton>
            <MiniButton onClick={onCopy}>Ask AI</MiniButton>
          </div>
        )}
      </div>

      {!done && !needsContent && it.status !== 'rejected' && (
        <button onClick={onReject} title="Reject (exclude from apply)" className="shrink-0 mt-0.5 text-[11px] px-1.5 py-0.5 rounded-md" style={{ color: 'var(--sem-muted)' }}>✕</button>
      )}
    </div>
  );
}

function Stat({ label, from, to, good }: { label: string; from: string | number; to: string | number; good?: 'up' | 'down' }) {
  const improved = good === 'down' ? Number(to) < Number(from) : good === 'up' ? Number(to) > Number(from) : to !== from;
  return (
    <div className="flex items-baseline gap-1.5">
      <span className="text-[10px] uppercase tracking-wide" style={{ color: 'var(--sem-muted)' }}>{label}</span>
      <span className="text-[12px] tnum" style={{ color: 'var(--sem-muted)' }}>{from}</span>
      <span className="text-[11px]" style={{ color: 'var(--sem-muted)' }}>→</span>
      <span className="text-[13px] font-semibold tnum" style={{ color: improved ? 'var(--sem-good)' : 'var(--sem-fg)' }}>{to}</span>
    </div>
  );
}

function trunc(s: string) { return s.length > 90 ? s.slice(0, 90) + '…' : s; }

// ---- local primitives -----------------------------------------------------------------------
function Panel({ children }: { children: React.ReactNode }) {
  return <div className="rounded-xl border p-4" style={{ background: 'var(--sem-surface)', borderColor: 'var(--sem-border)' }}>{children}</div>;
}
function Button({ children, onClick, primary, disabled, title }: { children: React.ReactNode; onClick?: () => void; primary?: boolean; disabled?: boolean; title?: string }) {
  return (
    <button onClick={onClick} disabled={disabled} title={title}
      className="text-[12px] px-3 py-1.5 rounded-lg font-medium transition-opacity disabled:opacity-40 whitespace-nowrap"
      style={primary ? { background: 'var(--sem-accent)', color: 'var(--sem-on-accent)' } : { background: 'var(--sem-surface-2)', color: 'var(--sem-fg)', border: '1px solid var(--sem-border)' }}>
      {children}
    </button>
  );
}
function MiniButton({ children, onClick, disabled }: { children: React.ReactNode; onClick?: () => void; disabled?: boolean }) {
  return (
    <button onClick={onClick} disabled={disabled}
      className="text-[11px] px-2 py-0.5 rounded-md font-medium transition-opacity disabled:opacity-40 whitespace-nowrap"
      style={{ background: 'var(--sem-surface-2)', color: 'var(--sem-fg)', border: '1px solid var(--sem-border)' }}>
      {children}
    </button>
  );
}
function Chip({ children, active, onClick }: { children: React.ReactNode; active: boolean; onClick: () => void }) {
  return (
    <button onClick={onClick} className="text-[10px] px-2 py-0.5 rounded-md font-medium"
      style={active ? { background: 'var(--sem-accent-soft)', color: 'var(--sem-fg)' } : { color: 'var(--sem-muted)', background: 'var(--sem-surface-2)' }}>
      {children}
    </button>
  );
}
function Banner({ children, color }: { children: React.ReactNode; color: string }) {
  return <div className="rounded-lg px-3 py-2 text-[12px]" style={{ background: 'color-mix(in srgb,' + color + ' 14%, transparent)', color }}>{children}</div>;
}
// Disclosure chevron — same affordance as the AI Readiness / BPA findings tree.
function Twist({ open }: { open: boolean }) {
  return <span className="inline-block w-3 shrink-0 text-[10px] transition-transform" style={{ transform: open ? 'none' : 'rotate(-90deg)', color: 'var(--sem-muted)' }}>▾</span>;
}
