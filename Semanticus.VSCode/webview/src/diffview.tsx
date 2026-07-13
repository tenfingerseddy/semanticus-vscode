import { useEffect, useMemo, useRef, useState } from 'react';
import { propertyDelta, type ModelDiff, type ModelDiffItem } from './compare';

// ═══════════════════════════════════════════════════════════════════════════════════════════════
// The Model Diff view — ONE view, TWO modes. Both share
// one Source/Target pair, one selection set, and one "Only differences" toggle (owned by CompareView,
// passed down). Review is the default from every entry point.
//
//   • Review      — PR-style cards grouped by table then kind; each card maps 1:1 onto applyDiff(selectedRefs).
//   • Side by side — a rail of changed objects → a two-column WORD-LEVEL diff (LCS computed here, no dep).
//
// COLOUR CONTRACT (decision, do not re-litigate): the brand Signal green (--sem-accent/--sem-good) NEVER
// appears inside a diff BODY — there, green must read as "added", not "the primary action". Additions use
// --sem-diff-add, removals --sem-diff-del (both theme-aware, defined in styles.css). Direction follows the
// existing +/~/− legend: Source(left) is the desired state, so a token/object present only in Source is an
// ADD (green, "would be applied"); present only in Target is a REMOVE (red, "would be replaced").
//
// Ref is OPAQUE. `it.ref` is the selection key and object identity — object names legally contain '/' and
// ':', and the ref grammar is escaped, so NEVER parse/split/reconstruct it. Display uses kind/table/name;
// identity + selectedRefs use `ref` verbatim.
// ═══════════════════════════════════════════════════════════════════════════════════════════════

export type DiffMode = 'review' | 'side';

const ACTION_TINT: Record<string, { fg: string; bg: string }> = {
  Create: { fg: 'var(--sem-diff-add)', bg: 'var(--sem-diff-add-bg)' },
  Update: { fg: 'var(--sem-warn)', bg: 'color-mix(in srgb, var(--sem-warn) 14%, transparent)' },
  Delete: { fg: 'var(--sem-diff-del)', bg: 'var(--sem-diff-del-bg)' },
  Equal: { fg: 'var(--sem-muted)', bg: 'transparent' },
};
// Compact glyphs (no emoji — matches the tool's ▸/▾/ⓘ convention) so a card reads its kind at a glance.
const KIND_ICON: Record<string, string> = {
  Table: '▦', Column: '▤', Measure: 'ƒ', Hierarchy: '≣', Partition: '◫', Relationship: '⇄',
  Role: '⚿', Perspective: '◎', Culture: '文', DataSource: '⛁', Expression: 'λ',
};
const KIND_ORDER = ['Table', 'Column', 'Measure', 'Hierarchy', 'Partition', 'Relationship', 'Role', 'Perspective', 'Culture', 'DataSource', 'Expression'];
const MODEL_GROUP = '(model-level)';   // synthetic group for top-level objects (tables/relationships/roles/…) with no owning table

// ─── grouping helpers ────────────────────────────────────────────────────────────────────────────
// The engine sends table=null for TABLE-level items (ModelCompare.cs: Mk(AlmRef.Top("table",…), "Table", name,
// null, …) — the 4th arg is the owning table). So a table's own Create/Delete/Update must group under ITS OWN
// name, beside its columns/measures, or every changed table lumps into (model-level) and Map's one-block-per-table
// grouping collapses. The OTHER top-level kinds (Relationship/Role/Perspective/Culture/DataSource/Expression) also
// carry table=null but genuinely have no owning table, so they belong in (model-level). Do NOT "fix" the engine null.
const groupKeyOf = (it: ModelDiffItem) => (it.objectType === 'Table' ? it.name : it.table) || MODEL_GROUP;
const changedOnly = (items: ModelDiffItem[]) => items.filter((i) => i.action !== 'Equal');

// ═══ WORD-LEVEL DIFF (LCS over tokens) ═══════════════════════════════════════════════════════════
// Tokenise keeping whitespace + newlines as their own tokens (so alignment survives reflow), then a
// classic LCS backtrack. Capped: past the cap we skip the O(n·m) table and fall back to line-level so a
// pathological blob never hangs the webview. DAX/JSON objects are well under the cap in practice.
const WORD_CAP = 1600;
type WordSeg = { t: string; k: 'same' | 'add' | 'del' };
function tokenize(s: string): string[] {
  return s.match(/\r\n|\n|[ \t]+|[A-Za-z0-9_]+|[^\sA-Za-z0-9_]/g) ?? [];
}
// Returns aligned per-side segments. `k` on the LEFT side uses 'add' for Source-only tokens (green);
// the RIGHT side uses 'del' for Target-only tokens (red) — see the colour contract above.
function wordDiff(leftText: string, rightText: string): { left: WordSeg[]; right: WordSeg[]; capped: boolean } {
  const a = tokenize(leftText), b = tokenize(rightText);
  if (a.length > WORD_CAP || b.length > WORD_CAP) {
    // fall back: whole-token sets, cheaper and good enough for very large blobs
    const bs = new Set(b), as = new Set(a);
    return {
      left: a.map((t) => ({ t, k: bs.has(t) ? 'same' : 'add' as const })),
      right: b.map((t) => ({ t, k: as.has(t) ? 'same' : 'del' as const })),
      capped: true,
    };
  }
  const n = a.length, m = b.length;
  const dp: Uint16Array[] = Array.from({ length: n + 1 }, () => new Uint16Array(m + 1));
  for (let i = n - 1; i >= 0; i--)
    for (let j = m - 1; j >= 0; j--)
      dp[i][j] = a[i] === b[j] ? dp[i + 1][j + 1] + 1 : Math.max(dp[i + 1][j], dp[i][j + 1]);
  const left: WordSeg[] = [], right: WordSeg[] = [];
  let i = 0, j = 0;
  while (i < n && j < m) {
    if (a[i] === b[j]) { left.push({ t: a[i], k: 'same' }); right.push({ t: b[j], k: 'same' }); i++; j++; }
    else if (dp[i + 1][j] >= dp[i][j + 1]) { left.push({ t: a[i], k: 'add' }); i++; }
    else { right.push({ t: b[j], k: 'del' }); j++; }
  }
  while (i < n) left.push({ t: a[i++], k: 'add' });
  while (j < m) right.push({ t: b[j++], k: 'del' });
  return { left, right, capped: false };
}
const isNewline = (t: string) => t === '\n' || t === '\r\n';
const isBlank = (t: string) => t.trim().length === 0;

function WordPane({ segs, label, tone }: { segs: WordSeg[]; label: string; tone: 'add' | 'del' }) {
  const wordBg = tone === 'add' ? 'var(--sem-diff-add-word)' : 'var(--sem-diff-del-word)';
  return (
    <div className="min-w-0">
      <div className="text-[10px] mb-0.5 uppercase tracking-wide" style={{ color: 'var(--sem-muted)' }}>{label}</div>
      <pre className="text-[11px] m-0 font-mono" style={{ maxHeight: 340, overflow: 'auto', whiteSpace: 'pre-wrap', wordBreak: 'break-word' }}>
        {segs.length === 0 ? <span style={{ color: 'var(--sem-muted)' }}>(absent)</span> : segs.map((s, i) =>
          isNewline(s.t) ? <br key={i} />
            : <span key={i} style={s.k !== 'same' && !isBlank(s.t) ? { background: wordBg, borderRadius: 2 } : undefined}>{s.t}</span>)}
      </pre>
    </div>
  );
}

// A single object's before↔after as a WORD-level two-column diff (used by Side by side, and by a Review card
// that has no clean property delta). Create shows only Source (added); Delete only Target (removed).
function WordDelta({ item, leftLabel, rightLabel }: { item: ModelDiffItem; leftLabel: string; rightLabel: string }) {
  const l = item.leftText ?? '', r = item.rightText ?? '';
  const d = useMemo(() => wordDiff(l, r), [l, r]);
  return (
    <div>
      {d.capped && <div className="text-[10px] mb-1" style={{ color: 'var(--sem-muted)' }}>Large object: showing an approximate word-level diff.</div>}
      <div className="grid grid-cols-2 gap-3">
        <WordPane segs={d.left} label={`${leftLabel} · source`} tone="add" />
        <WordPane segs={d.right} label={`${rightLabel} · target`} tone="del" />
      </div>
    </div>
  );
}

// ═══ REVIEW MODE ═════════════════════════════════════════════════════════════════════════════════
// A compact changed-property digest for a Review card: only the props that moved, as `key: old → new`
// (old in del-tint, new in add-tint). Falls back to the word-diff when neither side is JSON.
// Tinted value chips — add-green for the Source (desired) value, del-red for the Target (current) value.
const Add = ({ v }: { v: string | null }) => v == null ? <span style={{ color: 'var(--sem-muted)' }}>Not present</span> : <span style={{ background: 'var(--sem-diff-add-word)', color: 'var(--sem-fg)', borderRadius: 2, padding: '0 2px' }}>{v}</span>;
const Del = ({ v }: { v: string | null }) => v == null ? <span style={{ color: 'var(--sem-muted)' }}>Not present</span> : <span style={{ background: 'var(--sem-diff-del-word)', color: 'var(--sem-fg)', borderRadius: 2, padding: '0 2px' }}>{v}</span>;

function ReviewCardBody({ item, leftLabel, rightLabel }: { item: ModelDiffItem; leftLabel: string; rightLabel: string }) {
  const props = useMemo(() => propertyDelta(item.leftText, item.rightText), [item.leftText, item.rightText]);
  if (!props) return <WordDelta item={item} leftLabel={leftLabel} rightLabel={rightLabel} />;
  // Create = source-only (added); Delete = target-only (removed); Update = only the changed keys, target → source.
  const rows = item.action === 'Update' ? props.filter((p) => p.changed) : props;
  if (rows.length === 0) return <div className="text-[11px]" style={{ color: 'var(--sem-muted)' }}>No property-level differences.</div>;
  return (
    <table className="w-full text-[11px] border-collapse">
      <tbody>
        {rows.map((p) => (
          <tr key={p.key}>
            <td className="px-1 py-0.5 align-top font-mono" style={{ color: 'var(--sem-muted)', width: '26%' }}>{p.key}</td>
            <td className="px-1 py-0.5 align-top font-mono whitespace-pre-wrap" style={{ wordBreak: 'break-word' }}>
              {item.action === 'Create' && <Add v={p.left} />}
              {item.action === 'Delete' && <Del v={p.right} />}
              {item.action === 'Update' && <><Del v={p.right} /><span style={{ color: 'var(--sem-muted)' }}> → </span><Add v={p.left} /></>}
            </td>
          </tr>
        ))}
      </tbody>
    </table>
  );
}

function ActionBadge({ action }: { action: string }) {
  const t = ACTION_TINT[action] ?? ACTION_TINT.Equal;
  return <span className="text-[9px] px-1 rounded font-semibold uppercase tracking-wide" style={{ background: t.bg, color: t.fg, border: `1px solid ${t.fg}` }}>{action}</span>;
}

function ReviewMode({ diff, visible, selected, onSelectedChange, failed, leftLabel, rightLabel, focusKey }: {
  diff: ModelDiff; visible: ModelDiffItem[];
  selected: Set<string>; onSelectedChange: (n: Set<string>) => void; failed?: Set<string>;
  leftLabel: string; rightLabel: string; focusKey?: string;
}) {
  const [expanded, setExpanded] = useState<Set<string>>(() => new Set());
  const toggleExpand = (ref: string) => setExpanded((s) => { const n = new Set(s); if (n.has(ref)) n.delete(ref); else n.add(ref); return n; });

  // group by table, then by kind (both in a stable order); model-level objects sort last.
  const groups = useMemo(() => {
    const byTable = new Map<string, ModelDiffItem[]>();
    for (const it of visible) { const k = groupKeyOf(it); (byTable.get(k) ?? byTable.set(k, []).get(k)!).push(it); }
    return [...byTable.entries()]
      .sort((a, b) => (a[0] === MODEL_GROUP ? 1 : 0) - (b[0] === MODEL_GROUP ? 1 : 0) || a[0].localeCompare(b[0]))
      .map(([table, items]) => {
        const byKind = new Map<string, ModelDiffItem[]>();
        for (const it of items) { (byKind.get(it.objectType) ?? byKind.set(it.objectType, []).get(it.objectType)!).push(it); }
        const kinds = [...byKind.entries()].sort((a, b) => {
          const ia = KIND_ORDER.indexOf(a[0]), ib = KIND_ORDER.indexOf(b[0]);
          return (ia < 0 ? 99 : ia) - (ib < 0 ? 99 : ib) || a[0].localeCompare(b[0]);
        });
        return { table, items, kinds };
      });
  }, [visible]);

  // A focused group scrolls into view and receives a one-shot flash.
  const groupRefs = useRef<Record<string, HTMLDivElement | null>>({});
  useEffect(() => {
    if (!focusKey) return;
    const el = groupRefs.current[focusKey];
    if (el) { el.scrollIntoView({ behavior: 'smooth', block: 'start' }); el.classList.remove('sem-flash'); void el.offsetWidth; el.classList.add('sem-flash'); }
  }, [focusKey]);

  const setGroup = (items: ModelDiffItem[], on: boolean) => {
    const next = new Set(selected);
    for (const it of items) { if (it.action === 'Equal') continue; if (on) next.add(it.ref); else next.delete(it.ref); }
    onSelectedChange(next);
  };

  if (visible.length === 0) return <div className="text-[12px] p-3" style={{ color: 'var(--sem-muted)' }}>Nothing to review here.</div>;

  return (
    <div className="flex flex-col gap-3">
      {groups.map(({ table, items, kinds }) => {
        const selectable = items.filter((i) => i.action !== 'Equal');
        const sel = selectable.filter((i) => selected.has(i.ref)).length;
        return (
          <div key={table} ref={(el) => { groupRefs.current[table] = el; }} className="rounded-lg" style={{ border: '1px solid var(--sem-border)', background: 'var(--sem-surface)' }}>
            <div className="flex items-center gap-2 px-3 py-1.5 rounded-t-lg text-[12px] sticky top-0 z-10" style={{ background: 'var(--sem-surface-2)', borderBottom: '1px solid var(--sem-border)' }}>
              <span className="font-semibold">{table === MODEL_GROUP ? 'Model-level' : table}</span>
              <span style={{ color: 'var(--sem-muted)' }}>({items.length})</span>
              {selectable.length > 0 && (
                <span className="ml-auto flex items-center gap-2 text-[11px]">
                  <span style={{ color: 'var(--sem-muted)' }}>{sel}/{selectable.length}</span>
                  <button className="underline" style={{ color: 'var(--sem-accent)' }} onClick={() => setGroup(items, true)}>all</button>
                  <button className="underline" style={{ color: 'var(--sem-muted)' }} onClick={() => setGroup(items, false)}>none</button>
                </span>
              )}
            </div>
            <div className="p-2 flex flex-col gap-1.5">
              {kinds.map(([kind, kitems]) => (
                <div key={kind}>
                  <div className="text-[10px] uppercase tracking-wide px-1 mb-1 flex items-center gap-1" style={{ color: 'var(--sem-muted)' }}>
                    <span>{KIND_ICON[kind] ?? '•'}</span><span>{kind}</span>
                    {kitems.some((i) => i.matchedByName) && <span title="Identified by name (a rename reads as delete+create).">· ⓘ matched by name</span>}
                  </div>
                  {kitems.map((it) => {
                    const open = expanded.has(it.ref);
                    const isEqual = it.action === 'Equal';
                    const t = ACTION_TINT[it.action] ?? ACTION_TINT.Equal;
                    return (
                      <div key={it.ref} className="rounded mb-1" style={{ border: '1px solid var(--sem-border)', background: failed?.has(it.ref) ? 'var(--sem-diff-del-bg)' : 'var(--sem-surface-2)', opacity: isEqual ? 0.72 : 1 }}>
                        <div className="flex items-center gap-2 px-2 py-1.5 text-[12px]">
                          {!isEqual
                            ? <input type="checkbox" checked={selected.has(it.ref)} onChange={(e) => { const n = new Set(selected); if (e.target.checked) n.add(it.ref); else n.delete(it.ref); onSelectedChange(n); }} title="Include this change in the merge" />
                            : <span style={{ width: 13, display: 'inline-block' }} />}
                          <span className="shrink-0" style={{ color: 'var(--sem-muted)', width: 12, textAlign: 'center' }}>{KIND_ICON[it.objectType] ?? '•'}</span>
                          <button className="flex items-center gap-1.5 flex-1 min-w-0 text-left" style={{ background: 'transparent', border: 'none', color: 'var(--sem-fg)', cursor: 'pointer' }} onClick={() => toggleExpand(it.ref)}>
                            <span style={{ color: 'var(--sem-muted)' }}>{open ? '▾' : '▸'}</span>
                            <span className="font-medium truncate">{it.name}</span>
                          </button>
                          {failed?.has(it.ref) && <span className="text-[10px] shrink-0" style={{ color: 'var(--sem-diff-del)' }} title="The rehearsal reported this would fail to apply (often a missing dependency; select its parent too).">would fail</span>}
                          <ActionBadge action={it.action} />
                        </div>
                        {open && (
                          <div className="px-3 py-2" style={{ borderTop: `1px solid ${t.fg}`, background: t.bg }}>
                            <ReviewCardBody item={it} leftLabel={leftLabel} rightLabel={rightLabel} />
                          </div>
                        )}
                      </div>
                    );
                  })}
                </div>
              ))}
            </div>
          </div>
        );
      })}
    </div>
  );
}

// ═══ SIDE-BY-SIDE MODE ═══════════════════════════════════════════════════════════════════════════
function SideBySideMode({ visible, leftLabel, rightLabel }: { visible: ModelDiffItem[]; leftLabel: string; rightLabel: string }) {
  const [sel, setSel] = useState<string | null>(null);
  // default-select the first changed object (fall back to the first item) whenever the population changes.
  const current = useMemo(() => visible.find((i) => i.ref === sel) ?? changedOnly(visible)[0] ?? visible[0] ?? null, [sel, visible]);
  const railGroups = useMemo(() => {
    const m = new Map<string, ModelDiffItem[]>();
    for (const it of visible) { const k = groupKeyOf(it); (m.get(k) ?? m.set(k, []).get(k)!).push(it); }
    return [...m.entries()].sort((a, b) => (a[0] === MODEL_GROUP ? 1 : 0) - (b[0] === MODEL_GROUP ? 1 : 0) || a[0].localeCompare(b[0]));
  }, [visible]);

  if (visible.length === 0) return <div className="text-[12px] p-3" style={{ color: 'var(--sem-muted)' }}>Nothing to show here.</div>;
  return (
    <div className="flex gap-3" style={{ minHeight: 320 }}>
      <div className="rounded-lg shrink-0" style={{ border: '1px solid var(--sem-border)', width: 240, maxHeight: 460, overflow: 'auto', background: 'var(--sem-surface)' }}>
        {railGroups.map(([table, items]) => (
          <div key={table}>
            <div className="px-2 py-1 text-[10px] uppercase tracking-wide sticky top-0" style={{ background: 'var(--sem-surface-2)', color: 'var(--sem-muted)', borderBottom: '1px solid var(--sem-border)' }}>{table === MODEL_GROUP ? 'Model-level' : table}</div>
            {items.map((it) => {
              const active = current?.ref === it.ref;
              const t = ACTION_TINT[it.action] ?? ACTION_TINT.Equal;
              return (
                <button key={it.ref} onClick={() => setSel(it.ref)} className="flex items-center gap-1.5 w-full px-2 py-1 text-left text-[12px]"
                  style={{ background: active ? 'var(--sem-accent-soft)' : 'transparent', borderLeft: active ? '2px solid var(--sem-accent)' : '2px solid transparent', color: it.action === 'Equal' ? 'var(--sem-muted)' : 'var(--sem-fg)' }}>
                  <span className="shrink-0" style={{ color: 'var(--sem-muted)', width: 12, textAlign: 'center' }}>{KIND_ICON[it.objectType] ?? '•'}</span>
                  <span className="truncate flex-1 min-w-0">{it.name}</span>
                  <span className="shrink-0" style={{ color: t.fg }}>{it.action === 'Create' ? '+' : it.action === 'Delete' ? '−' : it.action === 'Update' ? '~' : '='}</span>
                </button>
              );
            })}
          </div>
        ))}
      </div>
      <div className="flex-1 min-w-0 rounded-lg p-3" style={{ border: '1px solid var(--sem-border)', background: 'var(--sem-surface)' }}>
        {current ? (
          <>
            <div className="flex items-center gap-2 mb-2 text-[12px]">
              <span className="shrink-0" style={{ color: 'var(--sem-muted)' }}>{KIND_ICON[current.objectType] ?? '•'}</span>
              <span className="font-semibold truncate">{current.table ? `${current.table} · ` : ''}{current.name}</span>
              <ActionBadge action={current.action} />
              <span className="ml-auto text-[10px] flex items-center gap-2" style={{ color: 'var(--sem-muted)' }}>
                <span><span style={{ color: 'var(--sem-diff-add)' }}>■</span> in {leftLabel} only</span>
                <span><span style={{ color: 'var(--sem-diff-del)' }}>■</span> in {rightLabel} only</span>
              </span>
            </div>
            <WordDelta item={current} leftLabel={leftLabel} rightLabel={rightLabel} />
          </>
        ) : <div className="text-[12px]" style={{ color: 'var(--sem-muted)' }}>Select an object.</div>}
      </div>
    </div>
  );
}

// ═══ THE VIEW (mode switcher + shared header) ════════════════════════════════════════════════════
export function DiffView({ diff, leftLabel, rightLabel, selected, onSelectedChange, failed, onlyDiffs, onToggleOnlyDiffs, equalsLoading, forceReviewNonce }: {
  diff: ModelDiff;
  leftLabel: string; rightLabel: string;
  selected: Set<string>;
  onSelectedChange: (next: Set<string>) => void;
  failed?: Set<string>;
  onlyDiffs: boolean;
  onToggleOnlyDiffs: (next: boolean) => void;
  equalsLoading?: boolean;
  forceReviewNonce?: number;   // bump to force Review (footer/status-bar entry always lands there)
}) {
  // Remember the last-used mode for the session; default Review.
  const [mode, setMode] = useState<DiffMode>(() => {
    // sessionStorage access can THROW in some webview contexts — guard the read (changeMode already guards the
    // write) so a throwing storage can't crash the whole DiffView render. Fall back to Review.
    try {
      const v = (typeof sessionStorage !== 'undefined' && sessionStorage.getItem('sem-diff-mode')) as DiffMode | null;
      return v === 'side' ? v : 'review';
    } catch { return 'review'; }
  });
  const changeMode = (m: DiffMode) => { setMode(m); try { sessionStorage.setItem('sem-diff-mode', m); } catch { /* ignore */ } };
  // Every explicit entry (footer / status-bar seed) lands on Review, regardless of the remembered mode.
  const seenForce = useRef<number | undefined>(undefined);
  useEffect(() => { if (forceReviewNonce != null && forceReviewNonce !== seenForce.current) { seenForce.current = forceReviewNonce; setMode('review'); } }, [forceReviewNonce]);

  const changed = useMemo(() => changedOnly(diff.items), [diff.items]);
  const equalItems = useMemo(() => diff.items.filter((i) => i.action === 'Equal'), [diff.items]);
  const visible = onlyDiffs ? changed : diff.items;

  const MODES: { id: DiffMode; label: string; hint: string }[] = [
    { id: 'review', label: 'Review', hint: 'PR-style cards you can select and merge' },
    { id: 'side', label: 'Side by side', hint: 'Word-level before/after for one object' },
  ];

  return (
    <div>
      <div className="flex items-center gap-2 flex-wrap mb-2 text-[12px]">
        {/* mode switcher — same affordance as Lineage's force/DAG/tree segmented control */}
        <div className="inline-flex rounded-md overflow-hidden" style={{ border: '1px solid var(--sem-border)' }}>
          {MODES.map((m) => (
            <button key={m.id} onClick={() => changeMode(m.id)} title={m.hint}
              className="px-2.5 py-1 text-[12px] font-medium"
              style={{ background: mode === m.id ? 'var(--sem-accent)' : 'var(--sem-surface-2)', color: mode === m.id ? 'var(--sem-on-accent)' : 'var(--sem-fg)', borderRight: m.id === 'review' ? '1px solid var(--sem-border)' : undefined }}>
              {m.label}
            </button>
          ))}
        </div>

        <span className="text-[11px]" style={{ color: 'var(--sem-muted)' }}>
          <span style={{ color: 'var(--sem-diff-add)' }}>+{diff.created}</span>{' '}
          <span style={{ color: 'var(--sem-warn)' }}>~{diff.updated}</span>{' '}
          <span style={{ color: 'var(--sem-diff-del)' }}>−{diff.deleted}</span>
        </span>

        <span className="ml-auto flex items-center gap-3">
          {/* running count of the shared selection */}
          <span className="text-[11px]" style={{ color: 'var(--sem-muted)' }}>{selected.size} of {changed.length} selected</span>
          {/* Only differences — defaults ON; identical objects hidden behind it, with a count. */}
          <label className="flex items-center gap-1.5 text-[11px] cursor-pointer" style={{ color: 'var(--sem-muted)' }} title="Hide objects that are identical in both models.">
            <input type="checkbox" checked={onlyDiffs} onChange={(e) => onToggleOnlyDiffs(e.target.checked)} />
            Only differences
          </label>
          {onlyDiffs
            ? (diff.equal > 0 && <button className="text-[11px] underline" style={{ color: 'var(--sem-muted)' }} onClick={() => onToggleOnlyDiffs(false)} title="Show the identical objects too">{equalsLoading ? 'loading…' : `${diff.equal} identical hidden`}</button>)
            : <span className="text-[11px]" style={{ color: 'var(--sem-muted)' }}>{equalsLoading ? 'loading…' : `${equalItems.length} identical shown`}</span>}
        </span>
      </div>

      {mode === 'review' && <ReviewMode diff={diff} visible={visible} selected={selected} onSelectedChange={onSelectedChange} failed={failed} leftLabel={leftLabel} rightLabel={rightLabel} />}
      {mode === 'side' && <SideBySideMode visible={visible} leftLabel={leftLabel} rightLabel={rightLabel} />}
    </div>
  );
}
