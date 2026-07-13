import { useEffect, useState } from 'react';
import { rpc } from './bridge';
import { UpsellNotice } from './pro';

// ===================================================================================================
// "This number over time" — the sparkline behind the What-moved-this-number panel (feature #3).
// Data = the engine's list_value_history: one point per moment the engine recorded the model's vital
// signs (it does this automatically when a plan is applied, a measure is optimized, the model is
// deployed or saved). Null-valued points are honest gaps — the formulas were recorded there but the
// number itself wasn't observed (no live connection at that moment). UX bar (Kane): plain sentences
// only — this component never says "blame", "checkpoint", "interval" or "vital signs".
// ===================================================================================================

export interface ValuePoint { sessionId?: string; revision: number; when: string; value?: string | null; checkpointOp?: string }
export interface ValueHistory { status: string; measureRef?: string; context?: string; points?: ValuePoint[]; truncated?: boolean; note?: string }

const OP_PLAIN: Record<string, string> = {
  apply_plan: 'applied a change plan',
  optimize_measure: 'optimized a measure',
  deploy_live: 'deployed',
  save_model: 'saved the model',
};
const opPlain = (op?: string) => (op ? OP_PLAIN[op] ?? op.replace(/_/g, ' ') : 'recorded');

function shortWhen(iso: string): string {
  const t = Date.parse(iso);
  if (Number.isNaN(t)) return iso;
  return new Date(t).toLocaleString(undefined, { month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit' });
}

/** Parse the engine's formatted value ("12345.67", "(blank)", text) into a plottable number, or null. */
function numOf(v?: string | null): number | null {
  if (v == null || v === '(blank)') return null;
  const n = Number(v);
  return Number.isFinite(n) ? n : null;
}

/** The inline SVG line: observed points connected in order, a dot per observation, gaps skipped. */
export function Sparkline({ points, width = 360, height = 44 }: { points: ValuePoint[]; width?: number; height?: number }) {
  const obs = points
    .map((p, i) => ({ i, p, n: numOf(p.value) }))
    .filter((x): x is { i: number; p: ValuePoint; n: number } => x.n !== null);
  if (obs.length === 0) return null;
  const pad = 4;
  const min = Math.min(...obs.map((o) => o.n));
  const max = Math.max(...obs.map((o) => o.n));
  const span = max - min || 1;
  const xOf = (i: number) => (points.length <= 1 ? width / 2 : pad + (i / (points.length - 1)) * (width - 2 * pad));
  const yOf = (n: number) => pad + (1 - (n - min) / span) * (height - 2 * pad);
  const path = obs.map((o, k) => `${k === 0 ? 'M' : 'L'}${xOf(o.i).toFixed(1)},${yOf(o.n).toFixed(1)}`).join(' ');
  const last = obs[obs.length - 1];
  return (
    <svg width="100%" viewBox={`0 0 ${width} ${height}`} preserveAspectRatio="none" style={{ display: 'block' }} role="img"
      aria-label={`Value over ${obs.length} observed point${obs.length === 1 ? '' : 's'}`}>
      <path d={path} fill="none" stroke="var(--sem-accent)" strokeWidth={1.5} vectorEffect="non-scaling-stroke" />
      {obs.map((o, k) => (
        <circle key={k} cx={xOf(o.i)} cy={yOf(o.n)} r={k === obs.length - 1 ? 3 : 2}
          fill={k === obs.length - 1 ? 'var(--sem-accent)' : 'var(--sem-surface)'}
          stroke="var(--sem-accent)" strokeWidth={1}>
          <title>{`${shortWhen(o.p.when)}: ${o.p.value} (${opPlain(o.p.checkpointOp)})`}</title>
        </circle>
      ))}
      <circle cx={xOf(last.i)} cy={yOf(last.n)} r={5} fill="none" stroke="var(--sem-accent)" strokeWidth={1} opacity={0.4} />
    </svg>
  );
}

/** Self-fetching wrapper: pulls the measure's recorded history and renders the line + honest coverage
 * copy. Free tier gets the plain invitation (the engine's soft gate), never an error banner. */
export function ValueSparkline({ measureRef, context }: { measureRef: string; context?: string }) {
  const [hist, setHist] = useState<ValueHistory | null>(null);
  const [failed, setFailed] = useState(false);
  useEffect(() => {
    let live = true;
    // Identity changed (another audit row's measure/slice) → drop the previous fetch's state FIRST, or a
    // stale series / a sticky `failed` from an earlier identity would render for (or permanently blank)
    // the new one (PR #86 review finding).
    setHist(null);
    setFailed(false);
    rpc<ValueHistory>('listValueHistory', measureRef, context ?? null)
      .then((h) => { if (live) setHist(h); })
      .catch(() => { if (live) setFailed(true); });
    return () => { live = false; };
  }, [measureRef, context]);

  if (failed) return null;                      // an older engine without the op — silence, not a scare
  if (!hist) return <div className="text-[10px]" style={{ color: 'var(--sem-muted)' }}>Loading this number’s history…</div>;
  if (hist.status === 'pro') {
    return (
      <UpsellNotice>
        See what moved a number, automatically, with Pro. You can still compare snapshots by hand:
        capture a baseline before an edit and compare after (free).
      </UpsellNotice>
    );
  }
  const points = hist.points ?? [];
  const observed = points.filter((p) => numOf(p.value) !== null);
  return (
    <div className="flex flex-col gap-1">
      <div className="text-[9.5px] uppercase tracking-wide" style={{ color: 'var(--sem-muted)' }}>This number over time</div>
      {observed.length >= 2 ? (
        <div className="rounded px-2 py-1.5" style={{ background: 'var(--sem-surface-2)', border: '1px solid var(--sem-border)' }}>
          <Sparkline points={points} />
          <div className="flex flex-wrap gap-x-3 mt-1 text-[10px] tnum" style={{ color: 'var(--sem-muted)' }}>
            <span>first {observed[0].value}</span>
            <span>latest <span style={{ color: 'var(--sem-fg)', fontWeight: 600 }}>{observed[observed.length - 1].value}</span></span>
            <span>{observed.length} of {points.length} points have an observed value</span>
          </div>
        </div>
      ) : (
        <div className="text-[10.5px]" style={{ color: 'var(--sem-muted)' }}>
          {points.length === 0
            ? 'No history recorded for this number yet. It builds up as changes are applied, deployed and saved.'
            : `Not enough observed values to draw a line yet (${observed.length} of ${points.length} recorded points have one; the rest were recorded without a live connection).`}
        </div>
      )}
      {hist.note && <div className="text-[10px]" style={{ color: 'var(--sem-muted)' }}>{hist.note}</div>}
    </div>
  );
}
