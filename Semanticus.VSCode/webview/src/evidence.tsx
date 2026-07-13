import { useEffect, useState } from 'react';
import { onActivity, type ActivityEvent } from './bridge';
import type { VerifiedEditRecord } from './history';
import { ValueSparkline } from './sparkline';
import { uiLabel } from './copy';

// ===================================================================================================
// Rich evidence rendering (Kane, 2026-07-03: "A user should be able to see the query results tested,
// the different DAX versions etc in a ui not just as text"). Two data tiers, welded per audit row:
//  - RICH: the live ActivityEvent.result broadcasts carry the FULL op results (candidate DAX bodies,
//    per-context mismatch rows, comparison queries, benchmark runs). Session-lifetime only — cached
//    here as events arrive and matched to a record by op + object + time proximity.
//  - DIGEST: the persisted audit chain stores a capped JSON digest (counts, verify states, grid).
//    Older records / reloaded sessions render a typed view of the digest instead.
// Every renderer is casing-tolerant (digest fields are PascalCase-ish, rich results camelCase) and
// falls back to the raw-JSON expander on anything unrecognized — a new op/verdict must never crash.
// ===================================================================================================

// ---- the rich activity cache --------------------------------------------------------------------

interface RichHit { kind: string; target?: string; at: number; result: unknown }
const RICH_KINDS = new Set(['optimize_measure', 'compare_baseline', 'apply_plan', 'blame_value', 'explain_value']);
const WELD_WINDOW_MS = 5 * 60 * 1000;   // a rich result welds only when captured near the record's write

/** Session-lifetime cache of rich op results off the live activity stream (newest first, capped). */
export function useRichEvidence(): RichHit[] {
  const [hits, setHits] = useState<RichHit[]>([]);
  useEffect(() => onActivity((e: ActivityEvent) => {
    if (!RICH_KINDS.has(e.kind) || e.result == null) return;
    setHits((h) => [{ kind: e.kind, target: e.target, at: Date.now(), result: e.result }, ...h].slice(0, 60));
  }), []);
  return hits;
}

/** The rich result for a record, if one was captured live: same op, same object (when both name one),
 * and written within the weld window — the CLOSEST in time wins, so two runs on one measure can't
 * cross-weld. No match = undefined = the typed digest view (honest degradation, never wrong data). */
export function matchRich(hits: RichHit[], r: VerifiedEditRecord): unknown {
  const when = Date.parse(r.when);
  if (Number.isNaN(when)) return undefined;
  let best: RichHit | undefined; let bestGap = WELD_WINDOW_MS;
  for (const h of hits) {
    if (h.kind !== r.op) continue;
    if (h.target && r.objectRef && h.target !== r.objectRef) continue;
    const gap = Math.abs(h.at - when);
    if (gap < bestGap) { best = h; bestGap = gap; }
  }
  return best?.result;
}

// ---- safe access over unknown-shaped JSON --------------------------------------------------------

type Obj = Record<string, unknown>;
const asObj = (v: unknown): Obj | null => (v && typeof v === 'object' && !Array.isArray(v) ? (v as Obj) : null);
const asArr = (v: unknown): unknown[] => (Array.isArray(v) ? v : []);
/** First present key wins — digests carry PascalCase-ish fields, rich results camelCase. */
function pick(v: unknown, ...keys: string[]): unknown {
  const o = asObj(v); if (!o) return undefined;
  for (const k of keys) if (k in o && o[k] != null) return o[k];
  return undefined;
}
const num = (v: unknown): number | undefined => (typeof v === 'number' ? v : undefined);
const str = (v: unknown): string | undefined => (typeof v === 'string' ? v : undefined);
const bool = (v: unknown): boolean | undefined => (typeof v === 'boolean' ? v : undefined);
function parseEvidence(r: VerifiedEditRecord): unknown {
  if (!r.evidence) return null;
  try { return JSON.parse(r.evidence); } catch { return null; }
}

// ---- small building blocks -----------------------------------------------------------------------

function Chip({ label, value, tone }: { label: string; value: string; tone?: 'good' | 'warn' | 'bad' }) {
  const color = tone === 'good' ? 'var(--sem-good)' : tone === 'warn' ? 'var(--sem-warn)' : tone === 'bad' ? 'var(--sem-bad)' : 'var(--sem-fg)';
  return (
    <span className="inline-flex items-baseline gap-1 text-[10px] px-1.5 py-0.5 rounded" style={{ background: 'var(--sem-surface-2)' }}>
      <span style={{ color: 'var(--sem-muted)' }}>{label}</span>
      <span className="font-semibold tnum" style={{ color }}>{value}</span>
    </span>
  );
}

function VerdictPill({ text, tone }: { text: string; tone: 'good' | 'warn' | 'bad' | 'muted' }) {
  const color = tone === 'good' ? 'var(--sem-good)' : tone === 'warn' ? 'var(--sem-warn)' : tone === 'bad' ? 'var(--sem-bad)' : 'var(--sem-muted)';
  return (
    <span className="text-[9px] uppercase tracking-wide font-semibold px-1.5 py-0.5 rounded-full"
      style={{ color, background: `color-mix(in srgb, ${color} 16%, transparent)`, border: `1px solid color-mix(in srgb, ${color} 34%, transparent)` }}>
      {text}
    </span>
  );
}

function DaxBlock({ label, dax, highlight }: { label: string; dax: string; highlight?: boolean }) {
  return (
    <div className="min-w-0">
      <div className="text-[9.5px] uppercase tracking-wide mb-0.5" style={{ color: highlight ? 'var(--sem-good)' : 'var(--sem-muted)' }}>{label}</div>
      <pre className="text-[10.5px] leading-snug px-2 py-1.5 rounded overflow-auto max-h-40 whitespace-pre-wrap break-words"
        style={{ background: 'var(--sem-surface-2)', color: 'var(--sem-fg)', fontFamily: 'var(--vscode-editor-font-family, monospace)',
          border: highlight ? '1px solid color-mix(in srgb, var(--sem-good) 45%, transparent)' : '1px solid var(--sem-border)' }}>{dax}</pre>
    </div>
  );
}

/** The tested query results, as a real grid: one row per filter context, before/after values, changed
 * cells tinted. This is the heart of "see the results, not a JSON string". */
function MismatchGrid({ rows, aLabel, bLabel }: { rows: unknown[]; aLabel: string; bLabel: string }) {
  if (rows.length === 0) return null;
  const shown = rows.slice(0, 12);
  return (
    <div className="overflow-x-auto rounded" style={{ border: '1px solid var(--sem-border)' }}>
      <table className="w-full text-[10.5px]" style={{ borderCollapse: 'collapse' }}>
        <thead>
          <tr style={{ background: 'var(--sem-surface-2)', color: 'var(--sem-muted)' }}>
            <th className="text-left font-medium px-2 py-1">Filter context</th>
            <th className="text-right font-medium px-2 py-1">{aLabel}</th>
            <th className="text-right font-medium px-2 py-1">{bLabel}</th>
          </tr>
        </thead>
        <tbody>
          {shown.map((m, i) => (
            <tr key={i} style={{ borderTop: '1px solid var(--sem-border)' }}>
              <td className="px-2 py-1" style={{ color: 'var(--sem-fg)' }}>{str(pick(m, 'context', 'Context')) ?? 'Not recorded'}</td>
              <td className="px-2 py-1 text-right tnum" style={{ color: 'var(--sem-muted)' }}>{str(pick(m, 'valueA', 'ValueA')) ?? 'Not recorded'}</td>
              <td className="px-2 py-1 text-right tnum font-semibold" style={{ color: 'var(--sem-bad)' }}>{str(pick(m, 'valueB', 'ValueB')) ?? 'Not recorded'}</td>
            </tr>
          ))}
        </tbody>
      </table>
      {rows.length > shown.length && (
        <div className="px-2 py-1 text-[10px]" style={{ color: 'var(--sem-muted)', borderTop: '1px solid var(--sem-border)' }}>+{rows.length - shown.length} more differing context{rows.length - shown.length === 1 ? '' : 's'}</div>
      )}
    </div>
  );
}

/** Warm-median benchmark bars, scaled to the slowest shown — quicker to read than four numbers. */
function BenchBars({ items }: { items: { label: string; ms?: number; winner?: boolean }[] }) {
  const shown = items.filter((i) => typeof i.ms === 'number');
  if (shown.length === 0) return null;
  const max = Math.max(...shown.map((i) => i.ms as number), 1);
  return (
    <div className="flex flex-col gap-1">
      {shown.map((i, k) => (
        <div key={k} className="flex items-center gap-2">
          <span className="text-[10px] w-24 shrink-0 truncate" style={{ color: i.winner ? 'var(--sem-good)' : 'var(--sem-muted)' }}>{i.label}</span>
          <div className="flex-1 h-2 rounded" style={{ background: 'var(--sem-surface-2)' }}>
            <div className="h-2 rounded" style={{ width: `${Math.max(3, ((i.ms as number) / max) * 100)}%`, background: i.winner ? 'var(--sem-good)' : 'var(--sem-accent)', opacity: i.winner ? 1 : 0.55 }} />
          </div>
          <span className="text-[10px] tnum w-14 text-right shrink-0" style={{ color: 'var(--sem-fg)' }}>{i.ms}ms</span>
        </div>
      ))}
    </div>
  );
}

function ShowMore({ label, children }: { label: string; children: React.ReactNode }) {
  const [open, setOpen] = useState(false);
  return (
    <div>
      <button onClick={() => setOpen((o) => !o)} className="text-[10px] underline decoration-dotted" style={{ color: 'var(--sem-muted)' }}>
        {open ? `hide ${label}` : `show ${label}`}
      </button>
      {open && <div className="mt-1">{children}</div>}
    </div>
  );
}

function RawJson({ text }: { text: string }) {
  let pretty = text;
  try { pretty = JSON.stringify(JSON.parse(text), null, 2); } catch { /* verbatim */ }
  return (
    <pre className="text-[10px] leading-snug px-2 py-1.5 rounded overflow-auto max-h-56"
      style={{ background: 'var(--sem-surface-2)', color: 'var(--sem-fg)', fontFamily: 'var(--vscode-editor-font-family, monospace)' }}>{pretty}</pre>
  );
}

// ---- per-op renderers -----------------------------------------------------------------------------

/** optimize_measure — the race: baseline vs each candidate's DAX, per-candidate proof + benchmark. */
function OptimizeEvidence({ digest, rich }: { digest: unknown; rich: unknown }) {
  const groupBy = asArr(pick(pick(digest, 'grid'), 'groupBy')).map(String);
  const baselineMs = num(pick(digest, 'baselineWarmMedianMs'));
  const noiseMs = num(pick(digest, 'noiseBandMs'));
  const richCands = asArr(pick(rich, 'candidates'));
  const digestCands = asArr(pick(digest, 'candidates'));
  const cands = richCands.length > 0 ? richCands : digestCands;
  const winnerIdx = num(pick(rich, 'winnerIndex'));
  const applied = bool(pick(rich, 'applied'));
  const baselineDax = str(pick(rich, 'baselineExpression'));

  return (
    <div className="flex flex-col gap-2">
      <div className="flex flex-wrap gap-1.5 items-center">
        {groupBy.length > 0 && <Chip label="equivalence grid" value={groupBy.join(' × ')} />}
        {baselineMs != null && <Chip label="baseline warm-median" value={baselineMs + 'ms'} />}
        {noiseMs != null && <Chip label="noise band" value={'±' + noiseMs + 'ms'} />}
        {applied != null && <Chip label="applied" value={applied ? 'yes' : 'no, kept the original'} tone={applied ? 'good' : undefined} />}
      </div>
      {baselineDax && <DaxBlock label="current body (the baseline both candidates must match)" dax={baselineDax} />}
      {cands.map((c, i) => {
        const idx = num(pick(c, 'index', 'Index')) ?? i;
        const state = str(pick(c, 'verifyState', 'VerifyState')) ?? 'unknown';
        const eq = pick(c, 'equivalence');
        const rows = num(pick(eq, 'rowsCompared')) ?? num(pick(c, 'rows'));
        const mmCount = num(pick(eq, 'mismatchCount')) ?? num(pick(c, 'mismatches'));
        const truncated = bool(pick(eq, 'truncated')) ?? bool(pick(c, 'truncated'));
        const richMms = asArr(pick(eq, 'mismatches'));
        const mms = richMms.length > 0 ? richMms : asArr(pick(c, 'mismatchSamples'));
        const bench = pick(c, 'benchmark');
        const warm = num(pick(bench, 'warmMedianMs')) ?? num(pick(c, 'warmMedianMs'));
        const dax = str(pick(c, 'expression'));
        const note = str(pick(c, 'note', 'Note'));
        const query = str(pick(eq, 'query'));
        const isWinner = winnerIdx != null && idx === winnerIdx && state === 'proven';
        const tone = state === 'proven' ? 'good' : state === 'failed' ? 'bad' : 'warn';
        return (
          <div key={i} className="rounded-lg p-2 flex flex-col gap-1.5"
            style={{ border: isWinner ? '1px solid color-mix(in srgb, var(--sem-good) 45%, transparent)' : '1px solid var(--sem-border)' }}>
            <div className="flex items-center gap-2 flex-wrap">
              <span className="text-[11px] font-semibold" style={{ color: 'var(--sem-fg)' }}>Candidate {idx}</span>
              <VerdictPill text={uiLabel(state)} tone={tone as 'good' | 'warn' | 'bad'} />
              {isWinner && <VerdictPill text="winner" tone="good" />}
              {rows != null && <Chip label="contexts compared" value={String(rows)} />}
              {mmCount != null && mmCount > 0 && <Chip label="differs in" value={mmCount + ' context' + (mmCount === 1 ? '' : 's')} tone="bad" />}
              {truncated && <Chip label="coverage" value="truncated, not proof" tone="warn" />}
              {warm != null && <Chip label="warm-median" value={warm + 'ms'} />}
            </div>
            {dax && <DaxBlock label="candidate DAX" dax={dax} highlight={isWinner} />}
            {mms.length > 0 && <MismatchGrid rows={mms} aLabel="current body" bLabel="candidate" />}
            {note && <div className="text-[10.5px]" style={{ color: 'var(--sem-muted)' }}>{note}</div>}
            {query && <ShowMore label="comparison query"><RawJson text={query} /></ShowMore>}
          </div>
        );
      })}
      <BenchBars items={[
        { label: 'current body', ms: baselineMs },
        ...cands.map((c, i) => ({
          label: `candidate ${num(pick(c, 'index', 'Index')) ?? i}`,
          ms: num(pick(pick(c, 'benchmark'), 'warmMedianMs')) ?? num(pick(c, 'warmMedianMs')),
          winner: winnerIdx != null && (num(pick(c, 'index', 'Index')) ?? i) === winnerIdx,
        })),
      ]} />
    </div>
  );
}

/** compare_baseline — which numbers moved, per measure, per filter context. */
function CompareEvidence({ digest, rich }: { digest: unknown; rich: unknown }) {
  const grid = asArr(pick(pick(digest, 'grid'), 'groupBy')).map(String);
  const capturedRev = num(pick(digest, 'capturedRevision'));
  const comparedRev = num(pick(digest, 'comparedRevision'));
  const moved = num(pick(digest, 'moved')) ?? num(pick(rich, 'movedCount'));
  const missing = num(pick(digest, 'missing')) ?? num(pick(rich, 'missingCount'));
  const measures = num(pick(digest, 'measures'));
  const richDiffs = asArr(pick(rich, 'diffs'));
  const digestDiffs = asArr(pick(digest, 'diffs'));
  const diffs = richDiffs.length > 0 ? richDiffs : digestDiffs;

  return (
    <div className="flex flex-col gap-2">
      <div className="flex flex-wrap gap-1.5 items-center">
        {grid.length > 0 && <Chip label="grid" value={grid.join(' × ')} />}
        {measures != null && <Chip label="measures baselined" value={String(measures)} />}
        {moved != null && <Chip label="moved" value={String(moved)} tone={moved > 0 ? 'bad' : 'good'} />}
        {missing != null && missing > 0 && <Chip label="missing" value={String(missing)} tone="bad" />}
        {capturedRev != null && comparedRev != null && <Chip label="revisions" value={`${capturedRev} → ${comparedRev}`} />}
      </div>
      {diffs.map((d, i) => {
        const verdict = str(pick(d, 'verdict', 'Verdict')) ?? 'unknown';
        const name = str(pick(d, 'name', 'Name')) ?? str(pick(d, 'ref', 'Ref')) ?? 'measure';
        const mmCount = num(pick(d, 'mismatchCount', 'MismatchCount'));
        const rows = num(pick(d, 'rowsCompared'));
        const richMms = asArr(pick(d, 'mismatches'));
        const mms = richMms.length > 0 ? richMms : asArr(pick(d, 'mismatchSamples'));
        const note = str(pick(d, 'note'));
        const tone = verdict === 'unchanged' ? 'good' : verdict === 'moved' || verdict === 'missing' ? 'bad' : 'warn';
        return (
          <div key={i} className="rounded-lg p-2 flex flex-col gap-1.5" style={{ border: '1px solid var(--sem-border)' }}>
            <div className="flex items-center gap-2 flex-wrap">
              <span className="text-[11px] font-semibold" style={{ color: 'var(--sem-fg)' }}>{name}</span>
              <VerdictPill text={uiLabel(verdict)} tone={tone as 'good' | 'warn' | 'bad'} />
              {rows != null && <Chip label="contexts" value={String(rows)} />}
              {mmCount != null && mmCount > 0 && <Chip label="moved in" value={mmCount + ' context' + (mmCount === 1 ? '' : 's')} tone="bad" />}
            </div>
            {mms.length > 0 && <MismatchGrid rows={mms} aLabel="captured (before)" bLabel="now (after)" />}
            {note && <div className="text-[10.5px]" style={{ color: 'var(--sem-muted)' }}>{note}</div>}
          </div>
        );
      })}
    </div>
  );
}

/** apply_plan — the batch certificate: counts, grade/BPA deltas, the per-item digest as a table. */
function ApplyPlanEvidence({ digest, rich }: { digest: unknown; rich: unknown }) {
  const src = rich ?? digest;
  const applied = num(pick(src, 'applied'));
  const skipped = num(pick(src, 'skipped'));
  const failed = num(pick(src, 'failed'));
  const verified = num(pick(digest, 'verified'));
  const unverified = num(pick(digest, 'unverified'));
  const overridden = num(pick(digest, 'overridden'));
  const gradeB = str(pick(src, 'gradeBefore'));
  const gradeA = str(pick(src, 'gradeAfter'));
  const bpaB = num(pick(src, 'bpaBefore'));
  const bpaA = num(pick(src, 'bpaAfter'));
  const items = asArr(pick(src, 'items'));
  const truncated = bool(pick(src, 'itemsTruncated'));
  return (
    <div className="flex flex-col gap-2">
      <div className="flex flex-wrap gap-1.5 items-center">
        {applied != null && <Chip label="applied" value={String(applied)} tone="good" />}
        {skipped != null && skipped > 0 && <Chip label="skipped" value={String(skipped)} tone="warn" />}
        {failed != null && failed > 0 && <Chip label="failed" value={String(failed)} tone="bad" />}
        {verified != null && <Chip label="verified" value={String(verified)} tone="good" />}
        {unverified != null && unverified > 0 && <Chip label="unverified" value={String(unverified)} tone="warn" />}
        {overridden != null && overridden > 0 && <Chip label="overridden" value={String(overridden)} tone="bad" />}
        {gradeB && gradeA && <Chip label="grade" value={`${gradeB} → ${gradeA}`} tone={gradeA <= gradeB ? 'good' : 'warn'} />}
        {bpaB != null && bpaA != null && <Chip label="BPA" value={`${bpaB} → ${bpaA}`} tone={bpaA <= bpaB ? 'good' : 'warn'} />}
      </div>
      {items.length > 0 && (
        <div className="overflow-x-auto rounded" style={{ border: '1px solid var(--sem-border)' }}>
          <table className="w-full text-[10.5px]" style={{ borderCollapse: 'collapse' }}>
            <thead>
              <tr style={{ background: 'var(--sem-surface-2)', color: 'var(--sem-muted)' }}>
                <th className="text-left font-medium px-2 py-1">Change</th>
                <th className="text-left font-medium px-2 py-1">Object</th>
                <th className="text-left font-medium px-2 py-1">Verify</th>
              </tr>
            </thead>
            <tbody>
              {items.slice(0, 15).map((it, i) => {
                const vs = str(pick(it, 'verifyState', 'VerifyState'));
                return (
                  <tr key={i} style={{ borderTop: '1px solid var(--sem-border)' }}>
                    <td className="px-2 py-1" style={{ color: 'var(--sem-fg)' }}>{uiLabel(str(pick(it, 'kind', 'Kind')), 'Not recorded')}</td>
                    <td className="px-2 py-1" style={{ color: 'var(--sem-muted)' }}>{str(pick(it, 'objectRef', 'ObjectRef')) ?? 'Not recorded'}</td>
                    <td className="px-2 py-1">{vs ? <VerdictPill text={uiLabel(vs)} tone={vs === 'verified' ? 'good' : vs === 'failed' ? 'bad' : 'warn'} /> : <span style={{ color: 'var(--sem-muted)' }}>Not recorded</span>}</td>
                  </tr>
                );
              })}
            </tbody>
          </table>
          {(items.length > 15 || truncated) && (
            <div className="px-2 py-1 text-[10px]" style={{ color: 'var(--sem-muted)', borderTop: '1px solid var(--sem-border)' }}>
              {items.length > 15 ? `+${items.length - 15} more item(s)` : ''}{truncated ? ' · the persisted digest is capped at 100 items' : ''}
            </div>
          )}
        </div>
      )}
    </div>
  );
}

/** deploy_live — the gate verdict: grade, violation counts, the blockers that made it RED. */
function DeployEvidence({ digest }: { digest: unknown }) {
  const grade = str(pick(digest, 'Grade', 'grade'));
  const bpa = num(pick(digest, 'BpaViolations', 'bpaViolations'));
  const blocking = num(pick(digest, 'BpaBlocking', 'bpaBlocking'));
  const total = num(pick(digest, 'TotalChanges', 'totalChanges'));
  const gatePass = bool(pick(digest, 'gatePass', 'GatePass'));
  const endpoint = str(pick(digest, 'endpoint'));
  const database = str(pick(digest, 'database'));
  const blockers = asArr(pick(digest, 'Blockers', 'blockers')).map(String);
  return (
    <div className="flex flex-col gap-1.5">
      <div className="flex flex-wrap gap-1.5 items-center">
        {gatePass != null && <Chip label="gate" value={gatePass ? 'GREEN' : 'RED'} tone={gatePass ? 'good' : 'bad'} />}
        {grade && <Chip label="grade" value={grade} />}
        {bpa != null && <Chip label="BPA violations" value={String(bpa)} tone={bpa > 0 ? 'warn' : 'good'} />}
        {blocking != null && <Chip label="blocking" value={String(blocking)} tone={blocking > 0 ? 'bad' : 'good'} />}
        {total != null && <Chip label="changes deployed" value={String(total)} />}
        {database && <Chip label="target" value={database} />}
      </div>
      {endpoint && <div className="text-[10px] truncate" style={{ color: 'var(--sem-muted)' }} title={endpoint}>{endpoint}</div>}
      {blockers.length > 0 && (
        <ul className="text-[10.5px] pl-4 list-disc" style={{ color: 'var(--sem-bad)' }}>
          {blockers.map((b, i) => <li key={i}>{b}</li>)}
        </ul>
      )}
    </div>
  );
}

/** blame_value — the "What moved this number?" panel (feature #3). Plain sentences only (Kane's UX
 * bar: never "blame" / "checkpoint" / "interval" / "vital signs" in the copy): the value's before →
 * after, the edits inside that window ranked with the most likely cause on top, any formula that
 * changed in the number's dependency tree, and the number's own history as a sparkline. */
function WhatMovedEvidence({ record, digest, rich, sessionId, onJump }: {
  record: VerifiedEditRecord; digest: unknown; rich: unknown; sessionId?: string; onJump?: (revision: number) => void;
}) {
  const src = rich ?? digest;
  const before = str(pick(src, 'before', 'Before'));
  const after = str(pick(src, 'after', 'After'));
  const context = str(pick(src, 'context', 'Context'));
  const verdict = str(pick(rich, 'verdict', 'Verdict')) ?? record.verdict;
  const cause = str(pick(src, 'cause', 'Cause'));
  const untracked = num(pick(src, 'untrackedEdits', 'UntrackedEdits')) ?? 0;
  const cands = asArr(pick(src, 'candidates', 'Candidates'));
  const diffs = asArr(pick(src, 'exprDiffs', 'ExprDiffs'));
  const measureName = (() => {
    const ref = record.objectRef ?? '';
    const rest = ref.slice(ref.indexOf(':') + 1);
    const slash = rest.lastIndexOf('/');
    return slash > 0 ? rest.slice(slash + 1) : rest;
  })();

  const OP_PLAIN: Record<string, string> = {
    apply_plan: 'Applied a change plan', optimize_measure: 'Optimized a measure',
    deploy_live: 'Deployed to the service', save_model: 'Saved the model',
  };
  const opPlain = (op?: string) => (op ? OP_PLAIN[op] ?? op.replace(/_/g, ' ') : 'edit');
  const actor = (o?: string) => (o === 'agent' ? 'AI Assistant' : o === 'system' ? 'System' : 'You');
  // 'measure:Sales/Total Cost' → 'Total Cost' (the raw ref stays in the raw-JSON expander).
  const friendly = (ref?: string) => {
    if (!ref) return 'formula';
    const rest = ref.slice(ref.indexOf(':') + 1);
    const slash = rest.lastIndexOf('/');
    return slash > 0 ? rest.slice(slash + 1) : rest;
  };

  const lead = verdict === 'attributed'
    ? `${measureName} changed between these two points. One edit is in that window; the likely cause.`
    : verdict === 'data-suspected'
      ? `${measureName} changed between these two points, but none of its formulas did; the data itself most likely changed (for example a refresh). Proving that needs a before/after copy of the model, which isn’t kept yet.`
      : verdict === 'inconclusive'
        ? `There isn’t enough recorded history to say what moved ${measureName}.`
        : `${measureName} changed between these two points. These ${cands.length} edits are in that window; the top one is the likely cause.`;

  return (
    <div className="flex flex-col gap-2">
      <div className="text-[11px]" style={{ color: 'var(--sem-fg)' }}>{lead}</div>
      <div className="flex flex-wrap gap-1.5 items-center">
        {before != null && after != null && <Chip label="value" value={`${before} → ${after}`} tone="warn" />}
        {context && context !== '(model context)' && <Chip label="slice" value={context} />}
        {cause === 'formula' && <Chip label="what changed" value="a formula behind it" tone="warn" />}
        {cause === 'structural' && <Chip label="what changed" value="its structure (not a formula)" tone="warn" />}
        {cause === 'data-suspected' && <Chip label="what changed" value="likely the data" tone="warn" />}
      </div>
      {cands.length > 0 && (
        <div className="flex flex-col gap-1">
          {cands.slice(0, 6).map((c, i) => {
            const rev = num(pick(c, 'revision', 'Revision'));
            const op = str(pick(c, 'op', 'Op'));
            const origin = str(pick(c, 'origin', 'Origin'));
            const overlap = num(pick(c, 'overlapScore', 'OverlapScore')) ?? 0;
            const formula = bool(pick(c, 'formulaChanged', 'FormulaChanged')) ?? false;
            const cSession = str(pick(c, 'sessionId', 'SessionId'));
            const likely = i === 0 && verdict !== 'data-suspected' && cands.length > 0;
            // Jump ONLY on a verified same-session id: the timeline is keyed by per-session revision, so a
            // candidate WITHOUT a session id (persisted digests deliberately drop it) could scroll to an
            // unrelated edit that merely shares the number (PR #86 review finding). No id / other session →
            // the plain not-jumpable state below, never a wrong-target jump.
            const canJump = onJump != null && rev != null && rev > 0 && cSession != null && sessionId != null && cSession === sessionId;
            const otherSession = onJump != null && rev != null && rev > 0 && !canJump;
            return (
              <div key={i} className="group/cand flex items-center gap-2 flex-wrap rounded-lg px-2 py-1.5"
                style={{ border: likely ? '1px solid color-mix(in srgb, var(--sem-accent) 45%, transparent)' : '1px solid var(--sem-border)' }}>
                <span className="text-[11px] font-medium" style={{ color: 'var(--sem-fg)' }}>{actor(origin)} · {opPlain(op)}</span>
                {likely && <VerdictPill text={cands.length === 1 ? 'the edit in this window' : 'most likely'} tone="good" />}
                {formula && <Chip label="changed" value="a formula" tone="warn" />}
                {overlap > 0 && <Chip label="touches" value={`${overlap} of this number’s inputs`} />}
                {canJump && (
                  <button onClick={() => onJump(rev)} title="Scroll to this edit in the timeline above (you can undo to it there)."
                    className="text-[10px] px-1.5 py-0.5 rounded"
                    style={{ background: 'var(--sem-surface-2)', color: 'var(--sem-fg)', border: '1px solid var(--sem-border)' }}>
                    Show this edit
                  </button>
                )}
                {otherSession && (
                  <span className="text-[10px]" style={{ color: 'var(--sem-muted)' }}
                    title="This edit isn’t on the current session’s timeline, so it can’t be shown (or undone) from here.">
                    not in this session’s timeline
                  </span>
                )}
                {/* apply/optimize records ARE the edit at that revision; a save/deploy just happened AFTER it */}
                <span className="ml-auto text-[10px] tnum" style={{ color: 'var(--sem-muted)' }}>
                  {rev != null && rev > 0 ? (op === 'apply_plan' || op === 'optimize_measure' ? `edit #${rev}` : `after edit #${rev}`) : ''}
                </span>
              </div>
            );
          })}
          {cands.length > 6 && <div className="text-[10px]" style={{ color: 'var(--sem-muted)' }}>+{cands.length - 6} more edit(s) in this window</div>}
        </div>
      )}
      {untracked > 0 && (
        <div className="text-[10.5px]" style={{ color: 'var(--sem-muted)' }}>
          {untracked} more edit{untracked === 1 ? '' : 's'} happened in this window without a recorded snapshot; they could be the cause too.
        </div>
      )}
      {diffs.length > 0 && (
        <div className="flex flex-col gap-1.5">
          {diffs.slice(0, 2).map((d, i) => {
            const nm = friendly(str(pick(d, 'ref', 'Ref')));
            const b = str(pick(d, 'before', 'Before'));
            const a = str(pick(d, 'after', 'After'));
            return (
              <div key={i} className="grid gap-1.5" style={{ gridTemplateColumns: b && a ? '1fr 1fr' : '1fr' }}>
                {b != null ? <DaxBlock label={`${nm} (before)`} dax={b} /> : <div className="text-[10px] self-end" style={{ color: 'var(--sem-muted)' }}>{nm}: new formula (didn’t exist before)</div>}
                {a != null ? <DaxBlock label={`${nm} (after)`} dax={a} highlight /> : <div className="text-[10px] self-end" style={{ color: 'var(--sem-muted)' }}>{nm}: removed (no longer used by this number)</div>}
              </div>
            );
          })}
          {diffs.length > 2 && <div className="text-[10px]" style={{ color: 'var(--sem-muted)' }}>+{diffs.length - 2} more changed formula(s), see the raw JSON below</div>}
        </div>
      )}
      {record.objectRef && <ValueSparkline measureRef={record.objectRef} context={context ?? undefined} />}
    </div>
  );
}

/** explain_value — the "Explain this number" dossier digest (feature #2): the value in its context, the
 * blank checklist verdicts, and the sum-of-parts (or the engine's honest refusal to show one). Plain
 * sentences only; the full interactive view is the DAX Lab panel — this is the audit-trail record. */
function ExplainedNumberEvidence({ record, digest, rich }: { record: VerifiedEditRecord; digest: unknown; rich: unknown }) {
  const src = rich ?? digest;
  const value = str(pick(src, 'value', 'Value'));
  const isBlank = bool(pick(src, 'isBlank', 'IsBlank'));
  const summary = str(pick(rich, 'summary', 'Summary')) ?? record.summary;
  const likely = str(pick(src, 'blankLikelyCause', 'BlankLikelyCause')) ?? str(pick(pick(rich, 'blank'), 'likelyCauseId'));
  const checks = asArr(pick(src, 'blankChecks')).length > 0 ? asArr(pick(src, 'blankChecks')) : asArr(pick(pick(rich, 'blank'), 'checks'));
  const contrib = pick(src, 'contributors', 'Contributors');
  const additive = bool(pick(contrib, 'additive', 'Additive'));
  const dim = str(pick(contrib, 'dimension', 'Dimension'));
  const contribRows = asArr(pick(contrib, 'rows', 'Rows'));
  const contribNote = str(pick(contrib, 'note', 'Note'));
  const rlsRoles = asArr(pick(src, 'rlsRoles')).map(String);
  return (
    <div className="flex flex-col gap-2">
      {summary && <div className="text-[11px]" style={{ color: 'var(--sem-fg)' }}>{summary}</div>}
      <div className="flex flex-wrap gap-1.5 items-center">
        {value != null && !isBlank && <Chip label="value" value={value} tone="good" />}
        {isBlank === true && <VerdictPill text="blank" tone="warn" />}
        {isBlank === true && likely && <Chip label="likely reason" value={likely.replace(/-/g, ' ')} tone="warn" />}
        {dim && additive === true && <Chip label="split by" value={dim} />}
        {additive === false && <Chip label="breakdown" value="not shown, parts don't sum" tone="warn" />}
        {rlsRoles.length > 0 && <Chip label="row security" value={rlsRoles.join(', ')} />}
      </div>
      {checks.length > 0 && (
        <div className="flex flex-col gap-1">
          {checks.slice(0, 6).map((c, i) => {
            const result = str(pick(c, 'result', 'Result'));
            const tone = result === 'cause' ? 'bad' : result === 'ok' ? 'good' : 'warn';
            return (
              <div key={i} className="flex items-start gap-2 text-[10.5px]">
                <VerdictPill text={result === 'cause' ? 'Cause' : result === 'ok' ? 'Ruled out' : uiLabel(result, 'Not available')} tone={tone as 'good' | 'warn' | 'bad'} />
                <span style={{ color: 'var(--sem-muted)' }}>{str(pick(c, 'finding', 'Finding'))}</span>
              </div>
            );
          })}
        </div>
      )}
      {additive === true && contribRows.length > 0 && (
        <div className="flex flex-col gap-0.5">
          {contribRows.slice(0, 6).map((r, i) => (
            <div key={i} className="flex items-baseline gap-2 text-[10.5px]">
              <span className="truncate" style={{ color: 'var(--sem-fg)' }}>{str(pick(r, 'member', 'Member'))}</span>
              <span className="ml-auto tnum" style={{ color: 'var(--sem-fg)' }}>{str(pick(r, 'value', 'Value'))}</span>
              <span className="tnum w-10 text-right" style={{ color: 'var(--sem-muted)' }}>{num(pick(r, 'pct', 'Pct')) != null ? num(pick(r, 'pct', 'Pct')) + '%' : ''}</span>
            </div>
          ))}
        </div>
      )}
      {additive === false && contribNote && <div className="text-[10.5px]" style={{ color: 'var(--sem-muted)' }}>{contribNote}</div>}
    </div>
  );
}

// ---- the dispatcher --------------------------------------------------------------------------------

/** Typed evidence for an audit record: rich view when a live result welded, digest view otherwise,
 * raw JSON always one toggle away (and the whole renderer for any unrecognized op). */
export function Evidence({ record, rich, sessionId, onJump }: {
  record: VerifiedEditRecord; rich?: unknown; sessionId?: string; onJump?: (revision: number) => void;
}) {
  const digest = parseEvidence(record);
  const body = (() => {
    if (digest == null && rich == null) return null;
    switch (record.op) {
      case 'optimize_measure': return <OptimizeEvidence digest={digest} rich={rich} />;
      case 'compare_baseline': return <CompareEvidence digest={digest} rich={rich} />;
      case 'apply_plan': return <ApplyPlanEvidence digest={digest} rich={rich} />;
      case 'blame_value': return <WhatMovedEvidence record={record} digest={digest} rich={rich} sessionId={sessionId} onJump={onJump} />;
      case 'explain_value': return <ExplainedNumberEvidence record={record} digest={digest} rich={rich} />;
      case 'deploy_live':
      case 'deploy_stage': return <DeployEvidence digest={digest} />;
      default: return null;
    }
  })();
  if (!body) return record.evidence ? <RawJson text={record.evidence} /> : null;
  return (
    <div className="flex flex-col gap-1.5">
      {rich != null && <div className="text-[9.5px] uppercase tracking-wide" style={{ color: 'var(--sem-muted)' }}>full evidence · captured live this session</div>}
      {body}
      {record.evidence && <ShowMore label="raw evidence JSON"><RawJson text={record.evidence} /></ShowMore>}
    </div>
  );
}
