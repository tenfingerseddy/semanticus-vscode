import { createContext, useContext, useEffect, useMemo, useRef, useState } from 'react';
import { revealInTree, copyText } from './bridge';
import { Caret } from './ui';
import { draftModel, draftStore, heldDraft } from './drafts';
import { OrphanDrafts } from './orphandrafts';
import { giveToAssistant, findingBuilder, sentTaskFor } from './sendtask';

// A normalized finding/violation row shared by the AI-Readiness and BPA views.
export interface FindingRow {
  ruleId: string; ruleName: string; category: string; severity: number;   // severity 3=error 2=warning 1=info
  objectRef: string; objectName: string; message: string;
  tag?: { label: string; color: string };                                 // optional fix-type badge
  waived?: boolean; waiverReason?: string; waiverRuleLevel?: boolean;       // an ACCEPTED finding (shown in the Accepted findings section); a related finding is never this
  noLongerFires?: boolean; waivedAsTwin?: boolean;                          // an acceptance whose finding stopped firing; a finding another acceptance covers, which is left alone but is not accepted
  coveredBy?: string; coveredReason?: string;                               // a related finding: the acceptance it stands beside and the reason given for it
  wholeRuleRow?: boolean;                                                   // the one row that stands for a rule-level acceptance, listed whether or not the rule fires
}

// Instrument: the level is a word with a small dot, never a coloured pill; the dot carries the colour.
const sevTone = (s: number) => (s >= 3 ? 'sem-chip-bad' : s === 2 ? 'sem-chip-warn' : 'sem-chip-neutral');
// Severity is SPOKEN, not only coloured. This mark used to be a 6px dot whose one non-colour channel was a
// title tooltip: a screenshot, a screen reader and a red/green colour-blind reader all lose a tooltip, so the
// level was carried by hue alone. The badge keeps the colour and states the level in words.
const sevLabel = (s: number) => (s >= 5 ? 'Critical' : s >= 3 ? 'Error' : s === 2 ? 'Warning' : 'Info');
function SevBadge({ severity }: { severity: number }) {
  return <span className={'sem-chip shrink-0 ' + sevTone(severity)}>{sevLabel(severity)}</span>;
}

// The severity filter. Buckets are "at least" for the top one because the two finding systems number severity
// differently (BPA 1..3, AI-Readiness Info/Medium/High/Critical = 1/2/3/5); a strict equality filter would drop
// every Critical finding from the Errors chip.
type SevFilter = 'all' | 'error' | 'warning' | 'info';
const SEV_BUCKETS: { key: SevFilter; label: string }[] = [
  { key: 'all', label: 'All' },
  { key: 'error', label: 'Errors' },
  { key: 'warning', label: 'Warnings' },
  { key: 'info', label: 'Info' },
];
const FILTER_FROM = 9;
const inBucket = (s: number, f: SevFilter) =>
  f === 'all' || (f === 'error' ? s >= 3 : f === 'warning' ? s === 2 : s <= 1);

interface RuleGroup { ruleId: string; ruleName: string; category: string; severity: number; items: FindingRow[]; }
interface CatGroup { category: string; count: number; rules: RuleGroup[]; }

// Group rows by category → rule, preserving first-seen order within each level.
function group(rows: FindingRow[]): CatGroup[] {
  const cats = new Map<string, Map<string, RuleGroup>>();
  for (const r of rows) {
    let byRule = cats.get(r.category);
    if (!byRule) { byRule = new Map(); cats.set(r.category, byRule); }
    let g = byRule.get(r.ruleId);
    if (!g) { g = { ruleId: r.ruleId, ruleName: r.ruleName, category: r.category, severity: r.severity, items: [] }; byRule.set(r.ruleId, g); }
    g.severity = Math.max(g.severity, r.severity);
    g.items.push(r);
  }
  return [...cats.entries()].map(([category, byRule]) => {
    const rules = [...byRule.values()].sort((a, b) => b.severity - a.severity || b.items.length - a.items.length);
    return { category, count: rules.reduce((n, g) => n + g.items.length, 0), rules };
  }).sort((a, b) => b.count - a.count);
}

type Menu = { x: number; y: number; row: FindingRow } | null;

// The reason typed into Accept finding belongs to ONE finding (its rule and its object), never to a place in the list (join 11 solui, sweep fix 1, rule R-B). A rescan after the
// assistant fixed, accepted or added a finding used to shift the row keys (they carried the index), so the row remounted and the typed reason was gone. The rows are keyed by the
// finding now, and the reason is held in the draft store under the finding, so anything else that remounts the box (a filter, a rule folded and opened, the page itself) opens it
// on the words again. The pages build the box inside renderActions with no identity of their own, so the list hands each row's identity down through this context.
export interface WaiveHoldId { scope: string; label: string }
export const WaiveHold = createContext<WaiveHoldId | null>(null);
/** The scope a finding's typed reason is held under: the list it is on (`holdAs`), the rule, and the object ('*' for the whole rule). */
const waiveScope = (holdAs: string, ruleId: string, objectRef: string) => `waive|${holdAs}|${JSON.stringify([ruleId, objectRef])}`;
/** One key per finding inside its rule: its object, and a count for a second finding on the same object (a stable place, never the index in the list). */
function rowKeys(items: FindingRow[]): string[] {
  const seen = new Map<string, number>();
  return items.map((r) => { const n = seen.get(r.objectRef) ?? 0; seen.set(r.objectRef, n + 1); return n ? `${r.objectRef}#${n}` : r.objectRef; });
}

// Collapsible Category → Rule → Items tree. Categories expand by default; rules collapse by default (so you
// scan the rule list, then expand a rule to see its items). Right-click an item → reveal it in the Model tree.
export function GroupedFindings({ rows, renderActions, renderRuleActions, categoryOrder, extraFilters, holdAs = 'findings' }: { rows: FindingRow[]; renderActions?: (r: FindingRow) => React.ReactNode; renderRuleActions?: (ruleId: string, category: string) => React.ReactNode; categoryOrder?: string[]; extraFilters?: React.ReactNode;
  /** Which list this is, so a reason typed on one page's finding is never offered on another page's finding with the same rule and object. */
  holdAs?: string }) {
  const [collapsedCats, setCollapsedCats] = useState<Set<string>>(new Set());
  // rules collapse by default; ?expand=all opens them (used by the screenshot harness + a future expand-all).
  const [openRules, setOpenRules] = useState<Set<string>>(() =>
    new URLSearchParams(location.search).get('expand') === 'all'
      ? new Set(rows.map((r) => r.category + '/' + r.ruleId)) : new Set());
  const [menu, setMenu] = useState<Menu>(null);
  const [sev, setSev] = useState<SevFilter>('all');

  const toggle = (set: Set<string>, key: string, setter: (s: Set<string>) => void) => {
    const next = new Set(set); next.has(key) ? next.delete(key) : next.add(key); setter(next);
  };

  // Counts are read off the UNFILTERED rows, so a chip always says how many findings that bucket holds and a
  // filter can never read as "there are none of those" when there are (the old list had no filter at all).
  const sevCounts = useMemo(() => ({
    all: rows.length,
    error: rows.filter((r) => inBucket(r.severity, 'error')).length,
    warning: rows.filter((r) => inBucket(r.severity, 'warning')).length,
    info: rows.filter((r) => inBucket(r.severity, 'info')).length,
  }), [rows]);
  const shown = useMemo(() => rows.filter((r) => inBucket(r.severity, sev)), [rows, sev]);

  let groups = group(shown);
  // Optional fixed category order (the Storage tab wants Can remove → Worth reviewing → Behavior cleanup,
  // an actionability order, not the count order group() defaults to). Unlisted categories keep count order, last.
  if (categoryOrder) {
    const idx = (c: string) => { const i = categoryOrder.indexOf(c); return i < 0 ? categoryOrder.length : i; };
    groups = [...groups].sort((a, b) => idx(a.category) - idx(b.category));
  }
  const severityBar = (
    <div className="sem-seg flex-wrap" role="group" aria-label="Filter findings by level">
      {SEV_BUCKETS.filter((b) => b.key === 'all' || sevCounts[b.key] > 0 || sev === b.key).map((b) => {
        const n = sevCounts[b.key];
        const on = sev === b.key;
        return (
          <button key={b.key} type="button" aria-pressed={on} disabled={n === 0 && !on}
            title={b.key === 'all' ? 'Show every finding' : `Show only ${b.label.toLowerCase()} findings`}
            onClick={() => setSev(b.key)}
            className="sem-seg-item disabled:opacity-40">
            <span>{b.label}</span><span className="sem-num ml-1">{n}</span>
          </button>
        );
      })}
    </div>
  );
  // ONE filter row. The fix-type filter used to sit in its own row above this one, so the page showed two
  // stacked rows that each began with "All" and read as the same control twice.
  // Few findings need no filter: under FILTER_FROM the row is not drawn. A filter that is already on keeps its row, so it can be turned off.
  const bar = (rows.length < FILTER_FROM && sev === 'all') ? null : (
    <div className="flex items-center gap-3 flex-wrap">
      {severityBar}
      {extraFilters}
      {sev !== 'all' && (
        <span className="sem-text-meta">
          showing {shown.length} of {rows.length}
        </span>
      )}
    </div>
  );

  // Reasons typed for a finding this list does not show now (the assistant fixed it, it was renamed, or a filter hides it) are named here with Discard, never held where nobody sees them.
  const prefix = `waive|${holdAs}|`;
  const shownIds = useMemo(() => {
    const ids = new Set<string>();
    for (const r of shown) { ids.add(JSON.stringify([r.ruleId, r.objectRef])); ids.add(JSON.stringify([r.ruleId, '*'])); }
    return ids;
  }, [shown]);
  const kept = (renderActions || renderRuleActions) ? <OrphanDrafts prefix={prefix} live={shownIds} what="finding"
    nameOf={(id) => { try { const [rule, obj] = JSON.parse(id) as string[]; return obj === '*' ? rule : `${obj} (${rule})`; } catch { return id; } }}
    targetsFor={(_fields, id) => { let rule = ''; try { rule = (JSON.parse(id) as string[])[0]; } catch { /* not a finding id */ }
      return shown.filter((r) => r.ruleId === rule).map((r) => ({ id: JSON.stringify([r.ruleId, r.objectRef]), label: `${r.objectName} (${r.ruleName})` })); }} /> : null;
  const holdOf = (ruleId: string, objectRef: string, label: string): WaiveHoldId => ({ scope: waiveScope(holdAs, ruleId, objectRef), label });

  if (rows.length === 0) return <div className="flex flex-col gap-2">{kept}<div className="sem-text-meta py-3">Nothing to show.</div></div>;
  if (groups.length === 0) return <div className="flex flex-col gap-2">{bar}{kept}
    <div className="sem-text-meta py-2">No findings at this severity. Pick another filter above.</div></div>;

  return (
    <div className="flex flex-col gap-2" onScroll={() => setMenu(null)}>
      {bar}
      {kept}
      {groups.map((cat) => {
        const catCollapsed = collapsedCats.has(cat.category);
        return (
          <div key={cat.category}>
            <Row onClick={() => toggle(collapsedCats, cat.category, setCollapsedCats)}
              className="text-(length:--sem-text-body) font-semibold">
              <Twist open={!catCollapsed} />{cat.category}<span className="sem-num font-normal" style={{ color: 'var(--sem-muted)' }}>{cat.count}</span>
            </Row>
            {!catCollapsed && cat.rules.map((g) => {
              const rk = cat.category + '/' + g.ruleId;
              const open = openRules.has(rk);
              return (
                <div key={rk}>
                  <Row onClick={() => toggle(openRules, rk, setOpenRules)} className="group text-(length:--sem-text-meta) pl-3">
                    <Twist open={open} />
                    <SevBadge severity={g.severity} />
                    <span className="font-medium truncate">{g.ruleName}</span>
                    <span className="sem-num shrink-0" style={{ color: 'var(--sem-muted)' }}>{g.items.length}</span>
                    {renderRuleActions && <span className="shrink-0 ml-1" onClick={(e) => e.stopPropagation()}>
                      <RuleMore scope={waiveScope(holdAs, g.ruleId, '*')}>
                        <WaiveHold.Provider value={holdOf(g.ruleId, '*', `${g.ruleName} (whole model)`)}>{renderRuleActions(g.ruleId, g.category)}</WaiveHold.Provider>
                      </RuleMore></span>}
                  </Row>
                  {open && (() => { const keys = rowKeys(g.items); return g.items.map((r, i) => (
                    <div key={keys[i]}
                      className="flex items-start gap-3 py-1 pl-8 pr-1 rounded hover:bg-[var(--sem-hover)]"
                      onContextMenu={(e) => { e.preventDefault(); setMenu({ x: e.clientX, y: e.clientY, row: r }); }}>
                      <div className="min-w-0 flex-1">
                        <div className="text-(length:--sem-text-meta) truncate flex items-center gap-2">
                          <span className="font-medium">{r.objectName}</span>
                          {r.tag && <span className="sem-chip">{r.tag.label}</span>}
                        </div>
                        <div className="sem-text-meta">{r.message}</div>
                        {r.coveredBy && !r.waived && (
                          <div className="sem-text-meta">You accepted a related finding. Accept this one too, or fix it.
                            <span style={{ color: 'var(--sem-muted)' }}> Related: {r.coveredBy}{r.coveredReason ? <>, “{r.coveredReason}”</> : ''}.</span></div>
                        )}
                      </div>
                      {renderActions && <div className="shrink-0 mt-1"><WaiveHold.Provider value={holdOf(r.ruleId, r.objectRef, `${r.objectName} (${r.ruleName})`)}>{renderActions(r)}</WaiveHold.Provider></div>}
                    </div>
                  )); })()}
                </div>
              );
            })}
          </div>
        );
      })}
      {menu && <ContextMenu menu={menu} onClose={() => setMenu(null)} />}
    </div>
  );
}

// The rule row's own actions (Accept for the whole model) sit behind a quiet "..." that shows on hover and focus, so a list of
// rules reads as rule names. It opens by itself when a reason was typed for the whole rule and held (a remount keeps the words in view).
function RuleMore({ scope, children }: { scope: string; children: React.ReactNode }) {
  const [open, setOpen] = useState(() => !!heldDraft<string>(scope));
  return (
    <span className="inline-flex items-center gap-1">
      <button type="button" aria-label="More for this rule" aria-expanded={open} title="More for this rule" onClick={() => setOpen((o) => !o)}
        className={'sem-btn sem-btn-sm sem-btn-quiet' + (open ? '' : ' opacity-0 group-hover:opacity-100 focus:opacity-100 group-focus-within:opacity-100')}>⋯</button>
      {open && children}
    </span>
  );
}

function ContextMenu({ menu, onClose }: { menu: NonNullable<Menu>; onClose: () => void }) {
  const item = (label: string, fn: () => void) => (
    <button onClick={() => { fn(); onClose(); }} className="block w-full text-left text-(length:--sem-text-meta) px-3 py-2 hover:bg-[var(--sem-accent-soft)]" style={{ color: 'var(--sem-fg)' }}>{label}</button>
  );
  return (
    <div className="fixed inset-0 z-50" onClick={onClose} onContextMenu={(e) => { e.preventDefault(); onClose(); }}>
      <div className="absolute rounded py-1 shadow-lg" onClick={(e) => e.stopPropagation()}
        style={{ left: Math.min(menu.x, window.innerWidth - 200), top: Math.min(menu.y, window.innerHeight - 90), minWidth: 180, background: 'var(--sem-surface)', border: '1px solid var(--sem-border)' }}>
        {item('Reveal in Model tree', () => revealInTree(menu.row.objectRef))}
        {item('Copy reference', () => void copyText(menu.row.objectRef))}
      </div>
    </div>
  );
}

function Row({ children, onClick, className, style }: { children: React.ReactNode; onClick: () => void; className?: string; style?: React.CSSProperties }) {
  return (
    <div onClick={onClick} className={'flex items-center gap-2 py-1 cursor-pointer select-none ' + (className ?? '')} style={style}>
      {children}
    </div>
  );
}
function Twist({ open }: { open: boolean }) {
  return <span className="inline-flex w-3 shrink-0 justify-start"><Caret open={open} /></span>;
}

function FBtn({ children, onClick, disabled, title, quiet }: { children: React.ReactNode; onClick?: () => void; disabled?: boolean; title?: string; quiet?: boolean }) {
  return (
    <button title={title} onClick={onClick} disabled={disabled}
      className={quiet ? 'sem-btn sem-btn-sm sem-btn-quiet' : 'sem-btn sem-btn-sm'}>
      {children}
    </button>
  );
}

/// Per-finding accept control. "Accept finding" reveals an inline REQUIRED reason input (Enter or ✓ to commit, Esc to cancel);
/// an accepted finding shows "Reopen". Used in both the AI-Readiness and BPA findings lists.
/// `onWaive` answers true only when the engine took the acceptance. The box ends its words on that answer and only while it still holds the reason it sent (R-B, "end only on OK, same words"); a refusal leaves the reason typed, held and on screen.
export function WaiveControl({ waived, reason, label = 'Accept', title, onWaive, onUnwaive }: { waived?: boolean; reason?: string; label?: string; title?: string; subtle?: boolean; onWaive: (reason: string) => Promise<boolean>; onUnwaive: () => void }) {
  // The finding this box is on (GroupedFindings hands it down): a reason typed here is held under it and the box opens on it again after a remount (R-B).
  const hold = useContext(WaiveHold);
  const [text, setText] = useState(() => (hold ? heldDraft<string>(hold.scope)?.value ?? '' : ''));
  const [editing, setEditing] = useState(() => text !== '');
  const [sending, setSending] = useState(false);
  const textNow = useRef(text); textNow.current = text;
  useEffect(() => {
    const m = draftModel();
    if (!hold || !m) return;
    if (editing && text) draftStore.set(m, hold.scope, '', { value: text, base: '', label: hold.label });
    else draftStore.clear(m, hold.scope, '');   // the person's Esc, an emptied box, or a reason the page took
  }, [hold?.scope, editing, text]);
  // Accept is a person's task (D4): the assistant's button leads, so Accept is quiet. `subtle` (the rule-header variant) is
  // accepted for callers that still pass it: the rule row now keeps it behind its own "..." (RuleMore), so both look the same.
  if (waived) return <FBtn title={reason} onClick={onUnwaive}>Reopen</FBtn>;
  if (!editing) return <FBtn quiet title={title ?? "Accept this finding (won't count against the score)"} onClick={() => setEditing(true)}>{label}</FBtn>;
  const commit = async () => {
    const t = text.trim();
    if (!t || sending) return;
    const m = draftModel(), at = hold?.scope;
    setSending(true);
    let ok = false;
    try { ok = (await onWaive(t)) === true; } catch { /* a refusal: the reason stays */ }
    setSending(false);
    if (!ok) return;
    // The control may be gone by now (an accepted finding leaves the list), so the held words end here and not in the effect above, and only when they are the reason that was sent.
    if (m && at) draftStore.clearIfSubmitted(m, at, '', t);
    if (textNow.current.trim() === t) { setEditing(false); setText(''); }
  };
  return (
    <span className="flex items-center gap-1">
      <input autoFocus value={text} onChange={(e) => setText(e.target.value)} placeholder="Why is this finding acceptable?" spellCheck={false}
        onKeyDown={(e) => { if (e.key === 'Enter') void commit(); else if (e.key === 'Escape') { setEditing(false); setText(''); } }}
        className="sem-field sem-field-sm" style={{ width: 220 }} />
      <FBtn disabled={!text.trim() || sending} onClick={() => void commit()}>✓</FBtn>
    </span>
  );
}

/// The "Accepted findings" section — every finding consciously accepted, with its reason + an un-waive action. Always
/// shown (collapsed) so the score is never silently inflated: the accepted findings stay visible and auditable.
/// The header "N accepted" count is a real control that opens this list (D-079); pass open/onOpenChange to drive it.
export function WaivedList({ rows, onUnwaive, open, onOpenChange, embedded }: { rows: FindingRow[]; onUnwaive: (r: FindingRow) => void; open?: boolean; onOpenChange?: (open: boolean) => void;
  /** The last fold row of the findings card instead of a card of its own. The id stays, so a link to the accepted list still lands. */
  embedded?: boolean }) {
  const [internal, setInternal] = useState(false);
  const isOpen = open ?? internal;
  const setOpen = (next: boolean) => { onOpenChange ? onOpenChange(next) : setInternal(next); };
  if (rows.length === 0) return null;
  return (
    <div id="waived-findings" className={embedded ? 'mt-2 border-t pt-2' : 'sem-card'} style={embedded ? { borderColor: 'var(--sem-border)' } : undefined}>
      <Row onClick={() => setOpen(!isOpen)} className={embedded ? 'text-(length:--sem-text-meta)' : 'sem-card-title'}>
        <Twist open={isOpen} />{embedded ? 'Accepted' : 'Accepted findings'}<span className="sem-num font-normal" style={{ color: 'var(--sem-muted)' }}>{rows.length}</span>
      </Row>
      {isOpen && (
        <div className="mt-1 flex flex-col gap-1">
          {rows.map((r, i) => (
            <div key={r.objectRef + r.ruleId + i} className="flex items-start gap-3 py-1 pl-6 pr-1"
              onContextMenu={(e) => e.preventDefault()}>
              <div className="min-w-0 flex-1">
                <div className="text-(length:--sem-text-meta) truncate">
                  <span className="font-medium" style={{ color: 'var(--sem-muted)' }}>{r.objectName}</span>
                  <span style={{ color: 'var(--sem-muted)' }}> · {r.ruleName}</span>
                  {r.waiverRuleLevel && <span className="sem-chip sem-chip-warn ml-2" title="Whole rule accepted across the model">whole rule</span>}
                  {r.noLongerFires && <span className="sem-chip sem-chip-neutral ml-2" title="The finding no longer shows up, but your acceptance still keeps this change from being made.">no longer fires</span>}
                </div>
                {r.wholeRuleRow && <div className="sem-text-meta">{r.message}</div>}
                {r.waiverReason && <div className="sem-text-meta">“{r.waiverReason}”</div>}
              </div>
              <div className="shrink-0 mt-1"><FBtn title={r.waiverRuleLevel ? 'Reopens every instance of this rule across the model' : undefined} onClick={() => onUnwaive(r)}>{r.waiverRuleLevel ? 'Reopen rule' : 'Reopen'}</FBtn></div>
            </div>
          ))}
        </div>
      )}
    </div>
  );
}

/// "Give the rest to your assistant": one press puts every finding this app cannot fix on the Plan, one task each, with the same
/// key as the row's own Give button, so those rows read "On the plan" and a second press adds nothing twice (the engine answers
/// an open task with itself). Used on Model quality and on AI readiness. `items` are the findings with no automatic fix.
export function GiveTheRest({ system, items }: {
  system: 'air' | 'bpa';
  items: { ruleId: string; ruleName: string; objectRef: string; objectName: string }[];
}) {
  const [progress, setProgress] = useState<{ at: number; of: number } | null>(null);
  const [err, setErr] = useState<string | null>(null);
  const keyOf = (i: { ruleId: string; objectRef: string }) => `${system === 'bpa' ? 'bpa' : 'readiness'}|${i.ruleId}|${i.objectRef}`;
  const [, bump] = useState(0);
  const left = items.filter((i) => !sentTaskFor(keyOf(i)));
  if (items.length === 0) return null;
  const give = async () => {
    const todo = left.slice();
    setErr(null); setProgress({ at: 0, of: todo.length });
    try {
      for (let n = 0; n < todo.length; n++) {
        const i = todo[n];
        await giveToAssistant(keyOf(i), findingBuilder(system, i));
        setProgress({ at: n + 1, of: todo.length });
      }
    } catch (e) { setErr(String((e as Error)?.message ?? e)); }
    finally { setProgress(null); bump((x) => x + 1); }
  };
  return (
    <span className="inline-flex flex-wrap items-center gap-2">
      <button type="button" className="sem-btn" disabled={!!progress || left.length === 0}
        title={left.length === 0 ? 'Every finding this app cannot fix is already on your Plan.' : `Adds the ${left.length} finding${left.length === 1 ? '' : 's'} this app cannot fix to your Plan, one task each. Your assistant sees them on its next call.`}
        onClick={() => void give()} data-testid="give-the-rest">
        {progress ? `Adding ${progress.at} of ${progress.of}...` : left.length === 0 ? 'On the plan' : 'Give the rest to your assistant'}
      </button>
      {err && <span role="alert" className="sem-text-meta flex items-start gap-1"><span className="sem-chip sem-chip-bad shrink-0" aria-hidden="true" />{err}</span>}
    </span>
  );
}
