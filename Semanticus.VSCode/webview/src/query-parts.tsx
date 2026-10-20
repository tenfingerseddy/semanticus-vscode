// Small parts of the Query page that more than one card uses: the fold, the speed marks, the plain-words speed reading.
import type { BenchmarkResult, ColdWarmBenchmark, ScanEvent, ServerTimings } from './daxlab';
import { timingAnnouncement } from './wire';

/** A fold: a small heading with a chevron, or the chevron alone (`plain`, in a card's title row). Every fold starts open. */
export function Fold({ label, open, onToggle, testid, plain = false }: { label: string; open: boolean; onToggle: () => void; testid: string; plain?: boolean }) {
  return <button type="button" className={`qv-fold ${plain ? 'qv-fold-plain' : ''}`} aria-expanded={open} aria-label={label ? `${label}, ${open ? 'open' : 'closed'}` : open ? 'Fold this card' : 'Unfold this card'} onClick={onToggle} data-testid={testid}>
    <svg width="10" height="10" viewBox="0 0 10 10" aria-hidden="true"><path d={open ? 'M1 3l4 4 4-4' : 'M3 1l4 4-4 4'} fill="none" stroke="currentColor" strokeWidth="1.5" /></svg>{label}
  </button>;
}

/** One bar split into reading data and calculating, drawn to the same scale as its neighbour. */
export function SplitBar({ label, total, se, max, note }: { label: string; total: number; se: number; max: number; note?: string }) {
  const fe = Math.max(0, total - se);
  const w = (v: number) => `${max > 0 ? (v / max) * 100 : 0}%`;
  return <div className="qv-split" data-testid={`split-${label.toLowerCase().replace(/\W+/g, '-')}`}>
    <div className="qv-split-label">{label}</div>
    <div className="qv-split-track"><span className="qv-split-se" style={{ width: w(se) }} /><span className="qv-split-fe" style={{ width: w(fe) }} /></div>
    <div className="qv-split-figs qv-num"><b>{timingAnnouncement(total)}</b><span>{timingAnnouncement(se)} reading · {timingAnnouncement(fe)} calculating{note ? ` · ${note}` : ''}</span></div>
  </div>;
}

/** Every run is numbered, so a strip of cold runs reads 1 2 3 and not "First First First". A cold run carries a mark: its bar is in the neutral colour and its number a dot. */
export const runLabel = (i: number, cold: boolean) => cold ? `${i + 1}\u00b7` : String(i + 1);

/** Runs within 5% of each other agree: a chart of them is five bars of one height, so they are said in one line instead. */
export const runsAgree = (runs: number[]) => {
  if (runs.length < 2) return false;
  const max = Math.max(...runs), min = Math.min(...runs);
  return max > 0 && (max - min) / max <= 0.05;
};

/** The runs in order, each bar as tall as its time on a baseline at zero. Every cold run (nothing kept) is in the neutral colour, not only the first: `cold` says which runs were cold. */
export function RunsStrip({ runs, cold }: { runs: number[]; cold?: boolean[] }) {
  const max = Math.max(1, ...runs);
  const isCold = (i: number) => (cold ? cold[i] === true : i === 0);
  if (runsAgree(runs) && !runs.some((_, i) => cold && isCold(i))) {
    const num = (v: number) => (Number.isInteger(v) ? String(v) : v.toFixed(1));
    return <div className="qv-runs-same sem-text-meta" data-testid="runs-strip">All {runs.length} runs took about the same ({num(Math.min(...runs))} to {timingAnnouncement(Math.max(...runs))}).</div>;
  }
  return <div className="qv-runs" data-testid="runs-strip">{runs.map((r, i) => <div key={i} className="qv-run">
    <span className="qv-run-track"><span className={`qv-run-bar ${isCold(i) ? 'qv-run-cold' : ''}`} style={{ height: `${Math.max(4, (r / max) * 100)}%` }} title={`Run ${i + 1}${isCold(i) ? ' (cold, nothing kept)' : ''}: ${timingAnnouncement(r)}`} /></span><span className="qv-run-n">{runLabel(i, isCold(i))}</span>
  </div>)}</div>;
}

/** A scan under 1 ms is noise at this resolution: it is not listed, and not drawn as a bar. */
export const visibleScans = <S extends Pick<ScanEvent, 'durationMs'>>(scans: S[]) => scans.filter((s) => s.durationMs >= 1);

export const median = (xs: number[]) => { if (!xs.length) return 0; const s = [...xs].sort((a, b) => a - b); return s[Math.floor(s.length / 2)]; };

/** A query that answers in under this many milliseconds is fast: there is nothing to say about where the time went. */
export const FAST_MS = 100;

/** What the measured numbers say, in one plain sentence. It reads only the numbers measured: no guess about the query.
 *  A fast query is told so and nothing more. A slow one gets the split between reading data and calculating, from SERVER time
 *  (the round trip is mostly the network), and never as a share made of a 0 ms storage reading. */
/** The one time the page quotes for a measured query: the longer of the first run and the middle repeat run, or null when nothing was measured.
 *  The verdict, the figure and the row header all say this number (UAT 18 Oct, N12: 74, 94 and 73 ms for one query). */
export function waitMs(cw: ColdWarmBenchmark | null, t: ServerTimings | null, bench?: BenchmarkResult | null): number | null {
  const seen: number[] = [];
  if (cw?.coldTotal?.n) seen.push(cw.coldTotal.avgMs);
  if (cw?.warmTotal?.n) seen.push(cw.warmTotal.runsMs?.length ? median(cw.warmTotal.runsMs) : cw.warmTotal.avgMs);
  if (!cw && bench?.runsMs?.length) seen.push(bench.firstMs, bench.warmMedianMs);
  if (!seen.length && t) seen.push(t.wallMs ?? t.totalMs);
  return seen.length ? Math.max(...seen) : null;
}
const slowestWait = waitMs;

export const isFast = (cw: ColdWarmBenchmark | null, t: ServerTimings | null, bench?: BenchmarkResult | null) => {
  const w = slowestWait(cw, t, bench);
  return w !== null && w < FAST_MS;
};

export function speedInsight(cw: ColdWarmBenchmark | null, t: ServerTimings | null, bench?: BenchmarkResult | null): string | null {
  const wait = slowestWait(cw, t, bench);
  if (wait !== null && wait < FAST_MS) return `Fast: ${Math.round(wait)} ms. Nothing to fix.`;
  if (cw?.coldTotal?.n && cw.warmTotal.n) {
    const cold = cw.coldTotal.avgMs, warm = cw.warmTotal.avgMs;
    const times = warm > 0 ? cold / warm : 0;
    const seShare = cw.traceAvailable && cold > 0 && cw.coldSe.avgMs >= 1 ? cw.coldSe.avgMs / cold : null;
    if (!cw.cacheClearAvailable) return 'The kept results could not be cleared, so the first run and the repeat runs are about the same.';
    if (times >= 3) return seShare !== null && seShare > 0.6
      ? 'Most of the first run is reading data. After that the data is in memory, so repeat runs are much faster.'
      : 'Repeat runs are much faster than the first run, because the server keeps what it already worked out.';
    return seShare !== null && seShare < 0.4
      ? 'Most of the time is calculation, not reading data, so a repeat run is only a little faster.'
      : 'A repeat run is only a little faster than the first run.';
  }
  if (t && t.traceAvailable && t.seQueries > 0 && t.totalMs > 0) {
    // The words match the split: over 60% is "most", under 40% the other way, and in between neither side is said to win.
    const share = t.seMs / t.totalMs, se = Math.round(share * 100);
    // Under 1 ms of storage time is a reading too small to turn into a percent, and 0 or 100% of anything is not a finding.
    if (t.seMs < 1 || se <= 0) return 'Most of the server time is calculation, not reading data.';
    if (se >= 100) return 'Nearly all of the server time is reading data.';
    if (share >= 0.6) return `Most of the server time is reading data (${se}%).`;
    if (share <= 0.4) return `Most of the server time is calculation (${100 - se}%), not reading data.`;
    return `The server time is split between reading data (${se}%) and calculation (${100 - se}%).`;
  }
  // A measured query always gets a line: a verdict that vanished after Measure again left the person with numbers and no word (UAT 18 Oct, N12).
  return wait !== null ? `Takes about ${timingAnnouncement(wait)}.` : null;
}
