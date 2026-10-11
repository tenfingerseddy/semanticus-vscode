import { useMemo } from 'react';
import { propertyDelta, type ModelDiffItem } from './compare';

// ═══════════════════════════════════════════════════════════════════════════════════════════════
// How one changed object reads: a property digest (what moved, old to new) or, when there is no clean
// property delta, a WORD-level two-column diff (LCS computed here, no dependency). The Publish page lists the
// changes and opens one of these under a row when you ask what exactly changed.
//
// COLOUR CONTRACT (decision, do not re-litigate): the brand Signal green (--sem-accent/--sem-good) NEVER
// appears inside a diff BODY, where green must read as "added", not "the primary action". Additions use
// --sem-diff-add, removals --sem-diff-del (both theme-aware, defined in styles.css). Source (left) is the desired
// state, so a token/object present only in Source is an ADD (green); present only in Target is a REMOVE (red).
// ═══════════════════════════════════════════════════════════════════════════════════════════════

// Compact glyphs (no emoji — matches the tool's ▸/▾/ⓘ convention) so a card reads its kind at a glance.
export const KIND_ICON: Record<string, string> = {
  Table: '▦', Column: '▤', Measure: 'ƒ', Hierarchy: '≣', Partition: '◫', Relationship: '⇄',
  Role: '⚿', Perspective: '◎', Culture: '文', DataSource: '⛁', Expression: 'λ',
};

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
      <div className="sem-label mb-1">{label}</div>
      <pre className="text-(length:--sem-text-label) m-0 font-mono" style={{ maxHeight: 340, overflow: 'auto', whiteSpace: 'pre-wrap', wordBreak: 'break-word' }}>
        {segs.length === 0 ? <span style={{ color: 'var(--sem-muted)' }}>(absent)</span> : segs.map((s, i) =>
          isNewline(s.t) ? <br key={i} />
            : <span key={i} style={s.k !== 'same' && !isBlank(s.t) ? { background: wordBg, borderRadius: 'var(--sem-radius-chip)' } : undefined}>{s.t}</span>)}
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
      {d.capped && <div className="text-(length:--sem-text-label) mb-1" style={{ color: 'var(--sem-muted)' }}>Large object: showing an approximate word-level diff.</div>}
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
const Add = ({ v }: { v: string | null }) => v == null ? <span style={{ color: 'var(--sem-muted)' }}>Not present</span> : <span style={{ background: 'var(--sem-diff-add-word)', color: 'var(--sem-fg)', borderRadius: 'var(--sem-radius-chip)', padding: '0 var(--sem-space-1)' }}>{v}</span>;
const Del = ({ v }: { v: string | null }) => v == null ? <span style={{ color: 'var(--sem-muted)' }}>Not present</span> : <span style={{ background: 'var(--sem-diff-del-word)', color: 'var(--sem-fg)', borderRadius: 'var(--sem-radius-chip)', padding: '0 var(--sem-space-1)' }}>{v}</span>;

export function ReviewCardBody({ item, leftLabel, rightLabel }: { item: ModelDiffItem; leftLabel: string; rightLabel: string }) {
  const props = useMemo(() => propertyDelta(item.leftText, item.rightText), [item.leftText, item.rightText]);
  if (!props) return <WordDelta item={item} leftLabel={leftLabel} rightLabel={rightLabel} />;
  // Create = source-only (added); Delete = target-only (removed); Update = only the changed keys, target → source.
  const rows = item.action === 'Update' ? props.filter((p) => p.changed) : props;
  if (rows.length === 0) return <div className="text-(length:--sem-text-label)" style={{ color: 'var(--sem-muted)' }}>No property-level differences.</div>;
  return (
    <table className="w-full text-(length:--sem-text-label) border-collapse">
      <tbody>
        {rows.map((p) => (
          <tr key={p.key}>
            <td className="px-1 py-1 align-top font-mono" style={{ color: 'var(--sem-muted)', width: '26%' }}>{p.key}</td>
            <td className="px-1 py-1 align-top font-mono whitespace-pre-wrap" style={{ wordBreak: 'break-word' }}>
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
