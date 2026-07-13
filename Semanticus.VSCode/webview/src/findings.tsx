import { useState } from 'react';
import { revealInTree, copyText } from './bridge';

// A normalized finding/violation row shared by the AI-Readiness and BPA views.
export interface FindingRow {
  ruleId: string; ruleName: string; category: string; severity: number;   // severity 3=error 2=warning 1=info
  objectRef: string; objectName: string; message: string;
  tag?: { label: string; color: string };                                 // optional fix-type badge
  waived?: boolean; waiverReason?: string; waiverRuleLevel?: boolean;       // an accepted finding (shown in the Waived section)
}

const sevColor = (s: number) => (s >= 3 ? 'var(--sem-bad)' : s === 2 ? 'var(--sem-warn)' : 'var(--sem-muted)');
const sevLabel = (s: number) => (s >= 3 ? 'Error' : s === 2 ? 'Warning' : 'Info');

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

// Collapsible Category → Rule → Items tree. Categories expand by default; rules collapse by default (so you
// scan the rule list, then expand a rule to see its items). Right-click an item → reveal it in the Model tree.
export function GroupedFindings({ rows, renderActions, renderRuleActions }: { rows: FindingRow[]; renderActions?: (r: FindingRow) => React.ReactNode; renderRuleActions?: (ruleId: string, category: string) => React.ReactNode }) {
  const [collapsedCats, setCollapsedCats] = useState<Set<string>>(new Set());
  // rules collapse by default; ?expand=all opens them (used by the screenshot harness + a future expand-all).
  const [openRules, setOpenRules] = useState<Set<string>>(() =>
    new URLSearchParams(location.search).get('expand') === 'all'
      ? new Set(rows.map((r) => r.category + '/' + r.ruleId)) : new Set());
  const [menu, setMenu] = useState<Menu>(null);

  const toggle = (set: Set<string>, key: string, setter: (s: Set<string>) => void) => {
    const next = new Set(set); next.has(key) ? next.delete(key) : next.add(key); setter(next);
  };

  const groups = group(rows);
  if (groups.length === 0) return <div className="text-[12px] py-3" style={{ color: 'var(--sem-good)' }}>Nothing to show.</div>;

  return (
    <div className="flex flex-col gap-1.5" onScroll={() => setMenu(null)}>
      {groups.map((cat) => {
        const catCollapsed = collapsedCats.has(cat.category);
        return (
          <div key={cat.category}>
            <Row onClick={() => toggle(collapsedCats, cat.category, setCollapsedCats)}
              className="text-[11px] uppercase tracking-wide font-semibold" style={{ color: 'var(--sem-muted)' }}>
              <Twist open={!catCollapsed} />{cat.category}<span className="ml-1 opacity-70">({cat.count})</span>
            </Row>
            {!catCollapsed && cat.rules.map((g) => {
              const rk = cat.category + '/' + g.ruleId;
              const open = openRules.has(rk);
              return (
                <div key={rk}>
                  <Row onClick={() => toggle(openRules, rk, setOpenRules)} className="text-[12px] pl-3">
                    <Twist open={open} />
                    <span className="w-1.5 h-1.5 rounded-full shrink-0" style={{ background: sevColor(g.severity) }} title={sevLabel(g.severity)} />
                    <span className="font-medium truncate">{g.ruleName}</span>
                    <span className="shrink-0" style={{ color: 'var(--sem-muted)' }}>({g.items.length})</span>
                    {renderRuleActions && <span className="ml-auto shrink-0" onClick={(e) => e.stopPropagation()}>{renderRuleActions(g.ruleId, g.category)}</span>}
                  </Row>
                  {open && g.items.map((r, i) => (
                    <div key={r.objectRef + i}
                      className="flex items-start gap-2.5 py-1 pl-9 pr-1 rounded hover:bg-[var(--sem-surface-2)]"
                      onContextMenu={(e) => { e.preventDefault(); setMenu({ x: e.clientX, y: e.clientY, row: r }); }}>
                      <div className="min-w-0 flex-1">
                        <div className="text-[12px] truncate flex items-center gap-1.5">
                          <span className="font-medium" style={{ color: 'var(--sem-accent)' }}>{r.objectName}</span>
                          {r.tag && <span className="text-[9px] uppercase px-1 py-0.5 rounded" style={{ background: 'var(--sem-surface-2)', color: r.tag.color }}>{r.tag.label}</span>}
                        </div>
                        <div className="text-[11px]" style={{ color: 'var(--sem-muted)' }}>{r.message}</div>
                      </div>
                      {renderActions && <div className="shrink-0 mt-0.5">{renderActions(r)}</div>}
                    </div>
                  ))}
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

function ContextMenu({ menu, onClose }: { menu: NonNullable<Menu>; onClose: () => void }) {
  const item = (label: string, fn: () => void) => (
    <button onClick={() => { fn(); onClose(); }} className="block w-full text-left text-[12px] px-3 py-1.5 hover:bg-[var(--sem-accent-soft)]" style={{ color: 'var(--sem-fg)' }}>{label}</button>
  );
  return (
    <div className="fixed inset-0 z-50" onClick={onClose} onContextMenu={(e) => { e.preventDefault(); onClose(); }}>
      <div className="absolute rounded-md py-1 shadow-lg" onClick={(e) => e.stopPropagation()}
        style={{ left: Math.min(menu.x, window.innerWidth - 200), top: Math.min(menu.y, window.innerHeight - 90), minWidth: 180, background: 'var(--sem-surface)', border: '1px solid var(--sem-border)' }}>
        {item('Reveal in Model tree', () => revealInTree(menu.row.objectRef))}
        {item('Copy reference', () => void copyText(menu.row.objectRef))}
      </div>
    </div>
  );
}

function Row({ children, onClick, className, style }: { children: React.ReactNode; onClick: () => void; className?: string; style?: React.CSSProperties }) {
  return (
    <div onClick={onClick} className={'flex items-center gap-1.5 py-1 cursor-pointer select-none ' + (className ?? '')} style={style}>
      {children}
    </div>
  );
}
function Twist({ open }: { open: boolean }) {
  return <span className="inline-block w-3 shrink-0 text-[10px] transition-transform" style={{ transform: open ? 'none' : 'rotate(-90deg)', color: 'var(--sem-muted)' }}>▾</span>;
}

function FBtn({ children, onClick, disabled, title }: { children: React.ReactNode; onClick?: () => void; disabled?: boolean; title?: string }) {
  return (
    <button title={title} onClick={onClick} disabled={disabled}
      className="text-[11px] px-2 py-0.5 rounded-md font-medium transition-opacity disabled:opacity-40 whitespace-nowrap"
      style={{ background: 'var(--sem-surface-2)', color: 'var(--sem-fg)', border: '1px solid var(--sem-border)' }}>
      {children}
    </button>
  );
}

/// Per-finding waive control. "Waive" reveals an inline REQUIRED reason input (Enter or ✓ to commit, Esc to cancel);
/// a waived finding shows "Un-waive". Used in both the AI-Readiness and BPA findings lists.
export function WaiveControl({ waived, reason, label = 'Waive', title, onWaive, onUnwaive }: { waived?: boolean; reason?: string; label?: string; title?: string; onWaive: (reason: string) => void; onUnwaive: () => void }) {
  const [editing, setEditing] = useState(false);
  const [text, setText] = useState('');
  if (waived) return <FBtn title={reason} onClick={onUnwaive}>Un-waive</FBtn>;
  if (!editing) return <FBtn title={title ?? "Accept this finding (won't count against the score)"} onClick={() => setEditing(true)}>{label}</FBtn>;
  const commit = () => { const t = text.trim(); if (t) { onWaive(t); setEditing(false); setText(''); } };
  return (
    <span className="flex items-center gap-1">
      <input autoFocus value={text} onChange={(e) => setText(e.target.value)} placeholder="reason (required)…" spellCheck={false}
        onKeyDown={(e) => { if (e.key === 'Enter') commit(); else if (e.key === 'Escape') { setEditing(false); setText(''); } }}
        className="text-[11px] px-1.5 py-0.5 rounded outline-none" style={{ width: 150, background: 'var(--sem-surface-2)', color: 'var(--sem-fg)', border: '1px solid var(--sem-border)' }} />
      <FBtn disabled={!text.trim()} onClick={commit}>✓</FBtn>
    </span>
  );
}

/// The "Waived (accepted)" section — every finding consciously accepted, with its reason + an un-waive action. Always
/// shown (collapsed) so the score is never silently inflated: the accepted findings stay visible and auditable.
export function WaivedList({ rows, onUnwaive }: { rows: FindingRow[]; onUnwaive: (r: FindingRow) => void }) {
  const [open, setOpen] = useState(false);
  if (rows.length === 0) return null;
  return (
    <div className="rounded-xl border p-3" style={{ background: 'var(--sem-surface)', borderColor: 'var(--sem-border)' }}>
      <Row onClick={() => setOpen((o) => !o)} className="text-[11px] uppercase tracking-wide font-semibold" style={{ color: 'var(--sem-muted)' }}>
        <Twist open={open} />⊘ Waived (accepted)<span className="ml-1 opacity-70">({rows.length})</span>
      </Row>
      {open && (
        <div className="mt-1 flex flex-col gap-1">
          {rows.map((r, i) => (
            <div key={r.objectRef + r.ruleId + i} className="flex items-start gap-2.5 py-1 pl-6 pr-1" style={{ opacity: 0.8 }}
              onContextMenu={(e) => e.preventDefault()}>
              <div className="min-w-0 flex-1">
                <div className="text-[12px] truncate">
                  <span className="font-medium" style={{ color: 'var(--sem-muted)' }}>{r.objectName}</span>
                  <span className="text-[10px]" style={{ color: 'var(--sem-muted)' }}> · {r.ruleName}</span>
                  {r.waiverRuleLevel && <span className="text-[9px] uppercase px-1 py-0.5 rounded ml-1" style={{ background: 'var(--sem-surface-2)', color: 'var(--sem-warn)' }} title="Whole rule waived model-wide">rule</span>}
                </div>
                {r.waiverReason && <div className="text-[11px]" style={{ color: 'var(--sem-warn)' }}>“{r.waiverReason}”</div>}
              </div>
              <div className="shrink-0 mt-0.5"><FBtn title={r.waiverRuleLevel ? 'Removes the model-wide waiver for this rule (all instances)' : undefined} onClick={() => onUnwaive(r)}>{r.waiverRuleLevel ? 'Un-waive rule' : 'Un-waive'}</FBtn></div>
            </div>
          ))}
        </div>
      )}
    </div>
  );
}
