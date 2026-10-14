import { useEffect, useMemo, useRef, useState } from 'react';
import { rpc, onPlanChange } from './bridge';
import { GiveToAssistant, slotTask, type PlanTaskRequest } from './sendtask';
import { uiLabel } from './copy';
import { BulkCount } from './personality/motion';
import { pendingSummary, pendingNote, groupState, readableFormat, rowNote } from './pending-copy';
import { plainRuleName } from './rule-words';

// ---- wire types (camelCase, from Semanticus.Engine/Plan.cs) ----------------------------------
interface Grounding { objectRef: string; name: string; kind: string; table?: string; expression?: string; formatString?: string; existingDescription?: string; dataType?: string; siblingNames?: string[]; }
export interface ChangeItem {
  id: string; objectRef: string; objectName: string; kind: string; source: string; category: string;
  severity: string; risk: string; ruleId?: string; target: string; title: string; rationale: string;
  before?: string; after?: string; status: string; generated?: boolean; verifyState?: string;
  note?: string; grounding?: Grounding;
  /** A Jev format line Semanticus could not prove fits: only a suggestion, with its plain note in checkText. */
  suggestion?: boolean; checkText?: string;
}
interface PlanSummary { total: number; deterministic: number; bpa: number; ai: number; jev?: number; renames: number; needsContent: number; approved: number; rejected: number; applied: number; unverified: number; }
export interface ChangePlanView { planId?: string; scope?: string; revision: number; items: ChangeItem[]; summary: PlanSummary; note?: string; }
interface ApplyPlanReport { revision: number; appliedCount: number; skippedCount: number; failedCount: number; items: ChangeItem[]; bpaViolationsBefore: number; bpaViolationsAfter: number; gradeBefore: string; gradeAfter: string; overallBefore: number; overallAfter: number; note: string; }

const RISK: Record<string, { label: string; color: string }> = {
  safe: { label: 'safe', color: 'var(--sem-good)' },
  // A Jev format Semanticus could not prove fits is a suggestion, never "safe" (THE FORMAT FILL RULE, addendum 3 item 19).
  suggestion: { label: 'suggestion', color: 'var(--sem-warn)' },
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
  applied: { label: 'applied', color: 'var(--sem-good)' },
  skipped: { label: 'skipped', color: 'var(--sem-warn)' },
  failed: { label: 'failed', color: 'var(--sem-bad)' },
};

type Filter = 'all' | 'deterministic' | 'ai' | 'jev' | 'rename' | 'needs_content';

// Words typed into a line's Author box belong to THAT line: its object and what it sets (join 11 solui, sweep fix 1, rule R-B). A plan the assistant proposed again can give the
// same id to another line, so the words are kept with the line they were typed for and shown only on it; words whose line is not on the list now are named above the list.
interface AuthorWords { text: string; at: string; label: string }
const lineAt = (it: ChangeItem) => JSON.stringify([it.objectRef, it.target, it.kind]);

// The apply report's numbers are wire data, and wire data can arrive without a field — an older engine, a
// partial failure, a host that answered the call with an empty object. Reading .toFixed off one of those threw
// during render, and with no boundary around the tool views that replaced the WHOLE Studio with "Studio could
// not finish loading", losing the person's place after a press that may well have changed their model (B1,
// walkthrough 2026-09-14). One guard, used by every number in the report.
function scoreText(value: number | undefined): string {
  return typeof value === 'number' && Number.isFinite(value) ? value.toFixed(0) : 'not reported';
}

export function OptimizeView({ seedNonce }: { seedNonce?: number } = {}) {
  const [plan, setPlan] = useState<ChangePlanView | null>(null);
  const [report, setReport] = useState<ApplyPlanReport | null>(null);
  const [applyElapsedMs, setApplyElapsedMs] = useState(0);
  const [busy, setBusy] = useState<'analyse' | 'apply' | 'safe' | false>(false);
  const [err, setErr] = useState<string | null>(null);
  const [drafts, setDrafts] = useState<Record<string, AuthorWords>>({});
  // Held by the line's object, target and kind, never by its id: ids restart in every plan, so a new line can carry the id of an old one whose words are still kept (Sol join 12, A8-3).
  const draftOf = (it: ChangeItem) => drafts[lineAt(it)]?.text ?? '';
  const setDraft = (it: ChangeItem, text: string) => setDrafts((d) => {
    const { [lineAt(it)]: _was, ...rest } = d;
    return text ? { ...rest, [lineAt(it)]: { text, at: lineAt(it), label: `${it.title} (${it.objectName})` } } : rest;
  });
  const dropDraft = (key: string) => setDrafts((d) => { const { [key]: _gone, ...rest } = d; return rest; });
  // R-B: a saved line ends only the words the engine took. Words typed while the reply was pending stay (and are saved by the next press).
  const dropDraftIfSent = (key: string, sent: string) => setDrafts((d) => {
    if (d[key]?.text.trim() !== sent) return d;
    const { [key]: _gone, ...rest } = d; return rest;
  });
  const [filter, setFilter] = useState<Filter>('all');
  const [collapsed, setCollapsed] = useState<Set<string>>(new Set());   // collapsed category sections (expanded by default)

  async function load() { try { setPlan(await rpc<ChangePlanView>('getPlan')); setErr(null); } catch (e) { setErr(String((e as Error).message ?? e)); } }
  useEffect(() => {
    void load();
    const off = onPlanChange((v) => {
      const next = v as ChangePlanView;
      setPlan(next);
      if ((next.summary?.applied ?? 0) === 0) setReport(null);
    });
    return off;
  }, []);

  // Arrived via "Review these together →" (AI Readiness / BPA): seed the plan ONLY if it's currently empty, so an
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
    // A typed value names the one the line showed; the engine compares it inside its own write. A tick or a drop has no typed value to protect.
    const shown = after == null ? null : (plan?.items?.find((i) => i.id === id)?.after ?? '');
    try { setPlan(await rpc<ChangePlanView>('setPlanItem', id, after, approved, 'human', shown)); return true; }
    catch (e) { setErr(String((e as Error).message ?? e)); return false; }
  }
  async function apply(ids: string[] | null, which: 'apply' | 'safe') {
    setBusy(which); setErr(null);
    const started = performance.now();
    try { const r = await rpc<ApplyPlanReport>('applyPlan', ids, 'human'); setApplyElapsedMs(performance.now() - started); setReport(r); await load(); }
    // Applying the whole approved set in one step is FREE from 2026-09-15 (Kane's feature line), so a
    // failure here is a real failure and keeps its loud red treatment.
    catch (e) { setErr(String((e as Error).message ?? e)); } finally { setBusy(false); }
  }
  async function clear() { try { await rpc('clearPlan', 'human'); setReport(null); await load(); } catch { /* ignore */ } }

  // The prompt Ask AI used to copy, now the brief of a Plan task for your assistant (Copy stays beside it).
  function slotTaskFor(it: ChangeItem): PlanTaskRequest {
    const g = it.grounding;
    const ctx = [
      g?.expression ? `Current DAX: ${g.expression}` : '',
      g?.siblingNames?.length ? `Sibling fields: ${g.siblingNames.slice(0, 15).join(', ')}` : '',
    ].filter(Boolean).join('. ');
    const prompt =
      `Using the Semanticus MCP, author the value for change-plan item ${it.id}: "${it.title}" on ${it.objectName} ` +
      `(${it.kind}). ${it.rationale} ${ctx} ` +
      `Then call set_plan_item("${it.id}", "<your value>") and re-check with get_plan.`;
    return slotTask(it, prompt);
  }

  const items = plan?.items ?? [];
  const safeIds = useMemo(() => items.filter((i) => (i.source === 'deterministic' || i.source === 'bpa') && i.status === 'approved').map((i) => i.id), [items]);
  const filtered = useMemo(() => items.filter((i) => {
    switch (filter) {
      case 'deterministic': return i.source === 'deterministic' || i.source === 'bpa';
      case 'ai': return i.source === 'ai';
      case 'jev': return i.source === 'jev';
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
  // Words typed for a line the list does not show now: the assistant proposed again or cleared the plan, or the id now names another line.
  const keptWords = Object.entries(drafts).filter(([key]) => !filtered.some((it) => lineAt(it) === key));
  const toggleCat = (cat: string) => setCollapsed((set) => { const n = new Set(set); n.has(cat) ? n.delete(cat) : n.add(cat); return n; });

  return (
    // No page frame of its own: the Plan hosts this list as a panel inside its own page frame, so a second
    // frame here put every card 48 in instead of 24. It fills the Plan's width like every other list (the space
    // rule, 27 September 2026: a 1,468 px cap left 266 px blank on each side of a 2000 px window).
    <div className="sem-stack-gap">
      <Panel>
        <div className="flex items-center gap-4">
          {/* The count is a number and a word, not a filled tile: Instrument draws no tiles. */}
          <div className="shrink-0">
            <div className="text-(length:--sem-text-page) font-semibold sem-num" style={{ color: 'var(--sem-fg)' }}>{s?.total ?? 0}</div>
            <div className="sem-label">changes</div>
          </div>
          <div className="flex-1 min-w-0">
            <div className="text-sm font-semibold">Pending changes</div>
            <div className="text-xs mt-1" style={{ color: 'var(--sem-muted)' }}>
              {hasPlan
                ? (items.length === 0
                  ? (plan?.note ? 'No changes could be planned. The note below explains why.' : 'No applicable changes were found.')
                  : <><div data-testid="opt-summary">{pendingSummary(s!)}</div><div data-testid="opt-note">{pendingNote(s!)}</div></>)
                : 'Pending changes are local. Applied changes stay in your copy until you publish.'}
            </div>
            {hasPlan && plan?.scope && <div className="text-(length:--sem-text-label) mt-1" style={{ color: 'var(--sem-muted)' }}>scope: {plan.scope}</div>}
          </div>
          <div className="flex flex-col gap-2 shrink-0">
            <Button primary disabled={busy !== false} onClick={analyse}>{busy === 'analyse' ? 'Analysing…' : hasPlan ? 'Re-analyse' : 'Analyse model'}</Button>
            {hasPlan && <Button disabled={busy !== false || (s?.approved ?? 0) === 0} onClick={() => apply(null, 'apply')}
              title="Apply every approved change as one undoable step.">
              {busy === 'apply' ? 'Applying…' : `Apply approved (${s?.approved ?? 0})`}
            </Button>}
            {hasPlan && safeIds.length > 0 && <Button disabled={busy !== false} onClick={() => apply(safeIds, 'safe')}
              title="Apply these safe changes in one undoable step.">
              {busy === 'safe' ? 'Applying…' : `Apply safe only (${safeIds.length})`}
            </Button>}
            {hasPlan && <Button disabled={busy !== false} onClick={clear}>Clear</Button>}
          </div>
        </div>
      </Panel>

      {err && <Banner color="var(--sem-bad)">{err}</Banner>}
      {keptWords.length > 0 && <div className="sem-inset text-(length:--sem-text-label)" role="status" data-testid="opt-words-kept" style={{ borderLeft: '3px solid var(--sem-warn)' }}>
        <div style={{ color: 'var(--sem-fg)' }}>{keptWords.length === 1 ? 'Words you typed for a change that is not in this list now are kept.' : `Words you typed for ${keptWords.length} changes that are not in this list now are kept.`} It may have been filled, changed again, or hidden by a filter. Nothing was saved.</div>
        {keptWords.map(([id, w]) => <div key={id} className="mt-1 flex items-center gap-2 flex-wrap">
          <b>{w.label}</b>: <code>{w.text}</code>
          <MiniButton onClick={() => dropDraft(id)}>Discard</MiniButton>
        </div>)}
      </div>}
      {hasPlan && plan?.note && <Banner color="var(--sem-warn)">{plan.note}</Banner>}

      {report && (
        <Panel>
          {(report.appliedCount ?? 0) > 1 && (report.failedCount ?? 0) === 0 && <BulkCount count={report.appliedCount} elapsedMs={applyElapsedMs} eventKey={report} />}
          <div className="flex items-center gap-4 flex-wrap">
            <div className="text-(length:--sem-text-body) font-semibold xe-dot xe-dot-good">{report.appliedCount ?? 0} applied</div>
            {(report.skippedCount ?? 0) > 0 && <div className="text-xs xe-dot xe-dot-warn">{report.skippedCount} skipped</div>}
            {(report.failedCount ?? 0) > 0 && <div className="text-xs xe-dot xe-dot-bad">{report.failedCount} failed</div>}
            <Stat label="BPA" from={report.bpaViolationsBefore ?? 'unknown'} to={report.bpaViolationsAfter ?? 'unknown'} good="down" />
            <Stat label="Grade" from={report.gradeBefore ?? 'unknown'} to={report.gradeAfter ?? 'unknown'} />
            <Stat label="Score" from={scoreText(report.overallBefore)} to={scoreText(report.overallAfter)} good="up" />
          </div>
        </Panel>
      )}

      {!hasPlan && !report && (
        <Panel><div className="text-xs" style={{ color: 'var(--sem-muted)' }}>Nothing pending yet. Click <b>Analyse model</b> (or ask your assistant to “work out the changes for the whole model”) to assemble a list. Nothing is changed until you apply.</div></Panel>
      )}

      {hasPlan && (
        <div className="flex items-center gap-1">
          <Chip active={filter === 'all'} onClick={() => setFilter('all')}>All {s?.total ?? 0}</Chip>
          <Chip active={filter === 'deterministic'} onClick={() => setFilter('deterministic')}>Automatic {(s?.deterministic ?? 0) + (s?.bpa ?? 0)}</Chip>
          <Chip active={filter === 'ai'} onClick={() => setFilter('ai')}>Assistant {s?.ai ?? 0}</Chip>
          <Chip active={filter === 'jev'} onClick={() => setFilter('jev')}>Jev {s?.jev ?? 0}</Chip>
          <Chip active={filter === 'rename'} onClick={() => setFilter('rename')}>Renames {s?.renames ?? 0}</Chip>
          <Chip active={filter === 'needs_content'} onClick={() => setFilter('needs_content')}>Needs content {s?.needsContent ?? 0}</Chip>
        </div>
      )}

      {groups.length > 1 && (
        <div className="flex items-center">
          <button onClick={() => setCollapsed(allCollapsed ? new Set() : new Set(groups.map(([c]) => c)))}
            className="text-(length:--sem-text-label) px-2 py-0 rounded-(--sem-radius-control) font-medium" style={{ color: 'var(--sem-muted)', background: 'var(--sem-surface-2)' }}>
            {allCollapsed ? 'Expand all' : 'Collapse all'}
          </button>
        </div>
      )}

      {groups.map(([cat, list]) => {
        const isCollapsed = collapsed.has(cat);
        const state = groupState(list);
        return (
          <Panel key={cat}>
            {/* Collapsible category section — mirrors the AI Readiness / BPA Category headers. */}
            <div onClick={() => toggleCat(cat)}
              className="flex items-center gap-2 cursor-pointer select-none text-(length:--sem-text-label) font-semibold"
              style={{ color: 'var(--sem-muted)' }}>
              <Twist open={!isCollapsed} />
              <span>{cat}</span>
              <span>({list.length})</span>
              {state && <span className="ml-1 normal-case font-medium xe-dot xe-dot-good" data-testid="opt-group-state">{state}</span>}
            </div>
            {!isCollapsed && (
              <div className="flex flex-col mt-1">
                {list.map((it) => (
                  <Row key={it.id} it={it}
                    draft={draftOf(it)}
                    onDraft={(v) => setDraft(it, v)}
                    onToggle={() => setItem(it.id, null, it.status !== 'approved')}
                    onSave={() => { const v = draftOf(it).trim(); if (v) void setItem(it.id, v, null).then((ok) => { if (ok) dropDraftIfSent(lineAt(it), v); }); }}
                    onReject={() => setItem(it.id, null, false)}
                    onGive={() => slotTaskFor(it)}
                    onApply={() => void apply([it.id], 'apply')}
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

export function Row({ it, draft, onDraft, onToggle, onSave, onReject, onGive, onApply }: {
  it: ChangeItem; draft: string; onDraft: (v: string) => void;
  onToggle: () => void; onSave: () => void; onReject: () => void; onGive: () => PlanTaskRequest; onApply: () => void;
}) {
  const risk = it.source === 'ai' ? RISK.ai : it.suggestion ? RISK.suggestion : (RISK[it.risk] ?? RISK.safe);
  const st = STATUS[it.status] ?? { label: uiLabel(it.status), color: 'var(--sem-muted)' };
  const approved = it.status === 'approved';
  const done = it.status === 'applied';
  const needsContent = it.status === 'needs_content';
  const isDax = it.kind === 'set_dax';
  // A format string is shown the way people read it: no backslashes (hero19 D10).
  const isFormat = it.kind === 'set_format_string';
  const shown = (text: string) => (isFormat ? readableFormat(text) : text);
  // Words typed for this line stay on it when another writer filled it (or it was applied): the box is kept with them, and the value now there is named (R-B).
  const typed = draft.trim();
  const filledUnder = !needsContent && !!typed && it.after?.trim() !== typed;

  return (
    <div className="flex items-start gap-3 py-2 border-b last:border-0" style={{ borderColor: 'var(--sem-border)', opacity: it.status === 'rejected' ? 0.45 : 1 }}>
      <button onClick={onToggle} disabled={done || needsContent} title={approved ? 'Approved. Click to unapprove' : it.suggestion ? 'Keep this suggestion. Check it fits first.' : 'Click to approve'}
        className={'mt-1 xe-check' + (approved || done ? ' is-on' : '')}
        style={{ cursor: done || needsContent ? 'default' : 'pointer' }}>
        {(approved || done) ? '✓' : null}
      </button>

      <div className="min-w-0 flex-1 sem-row-line">
        <div className="flex items-center gap-2 flex-wrap">
          <span className="text-(length:--sem-text-label) px-2 py-0 rounded shrink-0" style={{ background: 'var(--sem-surface-2)', color: risk.color }}>{risk.label}</span>
          <span className="text-xs font-medium truncate">{plainRuleName(isFormat ? readableFormat(it.title) : it.title)}</span>
          <span className="text-(length:--sem-text-label) truncate" style={{ color: 'var(--sem-muted)' }}>· {it.objectName}</span>
          {/* The status colour is the dot; the word stays in the text colour, so it reads at 4.5:1 in every theme. */}
          <span className="ml-auto text-(length:--sem-text-label) px-2 py-0 rounded shrink-0 xe-dot" style={{ '--xe-dot': st.color, background: 'var(--sem-surface-2)' } as React.CSSProperties}>{st.label}</span>
        </div>

        {/* before → after diff */}
        <div className="mt-1 text-(length:--sem-text-label) flex items-start gap-2 flex-wrap" style={{ fontFamily: isDax ? 'var(--vscode-editor-font-family, monospace)' : undefined }}>
          {it.before != null && <span className="px-1 rounded" style={{ background: 'var(--sem-wash-bad)', color: 'var(--sem-fg)', textDecoration: it.after != null ? 'line-through' : undefined }}>{trunc(shown(it.before))}</span>}
          {it.after != null
            ? <><span style={{ color: 'var(--sem-muted)' }}>→</span><span className="px-1 rounded" style={{ background: 'var(--sem-wash-good)', color: 'var(--sem-fg)' }}>{trunc(shown(it.after))}</span></>
            : <span>→ (awaiting authored value)</span>}
        </div>

        {it.suggestion && it.checkText && <div className="text-(length:--sem-text-label) mt-1 xe-dot xe-dot-warn" data-testid={`pending-suggestion-note-${it.id}`}>{it.checkText}</div>}
        {rowNote(it.status, it.note) && <div className={'text-(length:--sem-text-label) mt-1 xe-dot ' + (it.status === 'failed' ? 'xe-dot-bad' : 'xe-dot-warn')}>{rowNote(it.status, it.note)}</div>}
        {it.verifyState === 'verified' && <div className="text-(length:--sem-text-label) mt-1 xe-dot xe-dot-good">verified equivalent ✓</div>}

        {/* AI content authoring */}
        {filledUnder && <div className="text-(length:--sem-text-label) mt-2 xe-dot xe-dot-warn" role="status" data-testid={`opt-author-moved-${it.id}`}>
          {done ? 'This change was applied while you typed. Your words were not used: ' : 'This line was filled while you typed. It holds: '}
          <b>{done ? typed : trunc(it.after ?? '(nothing)')}</b>.{done ? '' : ' Your words are kept below. Save writes yours over it.'}
          {' '}<button type="button" className="sem-btn sem-btn-sm" onClick={() => onDraft('')}>Discard my words</button>
        </div>}
        {((needsContent && !done) || (filledUnder && !done)) && (
          <div className="flex items-center gap-2 mt-2">
            <input value={draft} onChange={(e) => onDraft(e.target.value)} placeholder={`Author ${it.target}…`} spellCheck={false}
              className="flex-1 text-(length:--sem-text-label) px-2 py-1 rounded-(--sem-radius-control) outline-none"
              style={{ background: 'var(--sem-surface-2)', color: 'var(--sem-fg)', border: '1px solid var(--sem-border)' }} />
            <MiniButton disabled={!draft.trim()} onClick={onSave}>Save</MiniButton>
            <GiveToAssistant sendKey={`slot|${it.id}`} build={onGive} />
          </div>
        )}
      </div>

      {/* Apply keeps its place in every row, whether or not this row has anything to apply. A row's actions belong
          in ONE column down the list, and an action only some rows carry used to push the ✕ beside it 63 px sideways
          from row to row. Kept as a width and nothing else when it does not apply: invisible, not clickable, not
          reachable by keyboard and not read out. */}
      <MiniButton onClick={onApply} hidden={!(approved && !done)}>Apply</MiniButton>
      {!done && !needsContent && it.status !== 'rejected' && (
        <button onClick={onReject} title="Reject (exclude from apply)" className="shrink-0 mt-1 text-(length:--sem-text-label) px-2 py-0 rounded-(--sem-radius-control)" style={{ color: 'var(--sem-muted)' }}>✕</button>
      )}
    </div>
  );
}

function Stat({ label, from, to, good }: { label: string; from: string | number; to: string | number; good?: 'up' | 'down' }) {
  const improved = good === 'down' ? Number(to) < Number(from) : good === 'up' ? Number(to) > Number(from) : to !== from;
  return (
    <div className="flex items-baseline gap-2">
      <span className="text-(length:--sem-text-label)" style={{ color: 'var(--sem-muted)' }}>{label}</span>
      <span className="text-xs tnum" style={{ color: 'var(--sem-muted)' }}>{from}</span>
      <span className="text-(length:--sem-text-label)" style={{ color: 'var(--sem-muted)' }}>→</span>
      <span className={'text-(length:--sem-text-body) font-semibold tnum' + (improved ? ' xe-dot xe-dot-good' : '')}>{to}</span>
    </div>
  );
}

function trunc(s: string) { return s.length > 90 ? s.slice(0, 90) + '…' : s; }

// ---- local primitives -----------------------------------------------------------------------
function Panel({ children }: { children: React.ReactNode }) {
  return <div className="sem-card">{children}</div>;
}
function Button({ children, onClick, primary, disabled, title }: { children: React.ReactNode; onClick?: () => void; primary?: boolean; disabled?: boolean; title?: string }) {
  return (
    <button onClick={onClick} disabled={disabled} title={title}
      className={primary ? 'sem-btn sem-btn-primary' : 'sem-btn'}>
      {children}
    </button>
  );
}
/** `hidden` keeps the button's WIDTH and takes away everything else, so a row without this action still lines its
 *  other actions up with the rows that have it. It is disabled and aria-hidden as well as invisible, so it cannot
 *  be clicked, tabbed to or read out. */
function MiniButton({ children, onClick, disabled, hidden }: { children: React.ReactNode; onClick?: () => void; disabled?: boolean; hidden?: boolean }) {
  return (
    <button onClick={onClick} disabled={disabled || hidden} tabIndex={hidden ? -1 : undefined} aria-hidden={hidden || undefined}
      className="sem-btn sem-btn-sm" style={hidden ? { visibility: 'hidden' } : undefined}>
      {children}
    </button>
  );
}
function Chip({ children, active, onClick }: { children: React.ReactNode; active: boolean; onClick: () => void }) {
  return (
    <button onClick={onClick} className="text-(length:--sem-text-label) px-2 py-0 rounded-(--sem-radius-control) font-medium"
      style={active ? { background: 'var(--sem-surface-2)', color: 'var(--sem-fg)', fontWeight: 'var(--sem-weight-strong)' } : { color: 'var(--sem-muted)', background: 'var(--sem-surface-2)' }}>
      {children}
    </button>
  );
}
function Banner({ children, color }: { children: React.ReactNode; color: string }) {
  return <div className="rounded-(--sem-radius-card) px-3 py-2 text-xs" style={{ background: 'color-mix(in srgb,' + color + ' 14%, transparent)', color }}>{children}</div>;
}
// Disclosure chevron — same affordance as the AI Readiness / BPA findings tree.
function Twist({ open }: { open: boolean }) {
  return <span className="inline-block w-3 shrink-0 text-(length:--sem-text-label) transition-transform" style={{ transform: open ? 'none' : 'rotate(-90deg)', color: 'var(--sem-muted)' }}>▾</span>;
}
