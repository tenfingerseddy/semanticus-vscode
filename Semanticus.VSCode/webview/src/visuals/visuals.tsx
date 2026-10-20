// The visual set Jev chooses from. One data shape feeds every visual, so switching between the visuals that fit is a
// real redraw of the same numbers. Each visual is small SVG drawn at the box's real width, on the Studio tokens only.
// The RULES (which shape each visual needs, the limits, the pick order) are lifted from Graphicus at 84812e04 (see
// docs/query-visuals.md); the drawing is new, so no Graphicus file is copied.
import { useEffect, useLayoutEffect, useRef, useState } from 'react';

export type Kind = 'total' | 'bars' | 'groups' | 'line' | 'variance' | 'dumbbell' | 'bullet' | 'slope' | 'multiples' | 'waterfall' | 'sparktable' | 'heat' | 'table';
/** `pctx` is a share shown exactly (up to four places of a percent): a check that compares two numbers must never print two different ones alike. */
export type Fmt = 'int' | 'money' | 'pct' | 'pctx' | 'ms' | 'rows' | 'dec';
export interface Row {
  label: string; v: number | null; v2?: number | null; series?: number[]; note?: string;
  /** The engine's own verdict for this row (differs or not). When set it decides the colour and the row mark, never a gap share worked out here. */
  differs?: boolean;
  /** What the difference cell says instead of the arithmetic (a relationship: "5 not matched"). */
  diffText?: string;
}
export interface Data {
  rows: Row[];
  /** What v is (the actual, the after, this year, the model) and what v2 is (the expected, the before, last year, the source). */
  vLabel: string; v2Label?: string;
  /** The two numbers are not the same kind of thing (an amount and a quantity): show both, but no Difference or %. */
  noGap?: boolean;
  fmt: Fmt;
  /** Allowed difference, as a share (0.005 is half of one percent). */
  tolerance?: number;
  total?: { value: number; label: string; prior?: number; priorLabel?: string; mark?: 'bars' | 'spark' | 'bullet' | 'pair' | 'none' };
  periods?: string[];
  cols?: string[]; cells?: (number | null)[][];
  start?: { label: string; value: number }; endLabel?: string;
  /** Draw from zero (speeds), and label a pair as "412 to 38 ms" instead of the difference. */
  zero?: boolean; pairLabel?: boolean;
  /** The name of the label column in a table (Store Region, Month). */
  dimLabel?: string;
  /** The measure's own number format (the Query page passes the model's). Without it `fmt` decides how a value reads. */
  format?: (v: number) => string;
}

export const KIND_NAME: Record<Kind, string> = {
  total: 'Total card', bars: 'Bars', groups: 'Grouped bars', line: 'Line', variance: 'Variance bars', dumbbell: 'Dumbbell', bullet: 'Bullet',
  slope: 'Slope', multiples: 'Small multiples', waterfall: 'Waterfall', sparktable: 'Sparkline table', heat: 'Heat table', table: 'Table',
};

export function fmt(v: number | null | undefined, f: Fmt, signed = false, compact = false): string {
  if (v === null || v === undefined || Number.isNaN(v)) return '';
  const sign = signed && v > 0 ? '+' : '';
  if (f === 'pct') return sign + (v * 100).toFixed(v !== 0 && Math.abs(v) < 0.001 ? 2 : 1) + '%';
  if (f === 'pctx') return sign + percentText(v * 100, v < 1) + '%';
  if (f === 'ms') return sign + Math.round(v).toLocaleString('en-US') + ' ms';
  // Added for Validation: a number that is not whole keeps up to six places, so 1.004 and 1.001 never both read 1.00. One decimal
  // gets a trailing zero when the figure is 1 or more, as the old Tests page wrote it (money keeps its cents).
  if (f === 'dec') {
    const t = Number(v.toFixed(6));
    if (t === 0) return '0';
    const text = t.toLocaleString('en-US', { maximumFractionDigits: 6 });
    return sign + (!Number.isInteger(t) && Math.abs(t) >= 1 && /[.]\d$/.test(text) ? text + '0' : text);
  }
  if (f === 'money' && compact) {
    const a = Math.abs(v);
    if (a >= 1e6) return sign + (v / 1e6).toFixed(1) + 'M';
    if (a >= 1e4) return sign + Math.round(v / 1e3) + 'k';
  }
  return sign + Math.round(v).toLocaleString('en-US');
}
/** A percent figure (already times 100) with one place at least and four at most, never "-0.0", and never 100 for a share under 1. */
function percentText(p: number, belowWhole = false): string {
  let t = Number(p.toFixed(4));
  if (t === 0) return '0';
  if (belowWhole && t >= 100) t = 99.9999;
  return t.toLocaleString('en-US', { minimumFractionDigits: 1, maximumFractionDigits: 4 });
}
/** A difference: points for a rate (41.8% to 43.1% is down 1.3 points), the value's own format otherwise. */
export const fmtDiff = (v: number, f: Fmt) => (f === 'pct' ? `${v > 0 ? '+' : ''}${(v * 100).toFixed(1)} pts` : f === 'pctx' ? `${v > 0 ? '+' : ''}${percentText(v * 100)} pts` : fmt(v, f, true));
/** Whether a row is drawn as differing: the engine's verdict when the row carries one, else the allowed share. */
const isOut = (d: Data, r: Row, off: number) => (r.differs !== undefined ? r.differs : d.tolerance !== undefined && Math.abs(off) > d.tolerance);
/** A row with no number on a side has no share to draw: no dot, no bar and no figure, only its words. */
const noShare = (r: Row) => r.v == null || r.v2 === null;
const noShareText = (r: Row) => r.diffText ?? r.note ?? '';
/** The smallest step each format prints. A gap under it prints as zero, so the row says so in words instead of two equal values. */
const PRINTED_STEP: Record<Fmt, string> = { int: '1', money: '1', rows: '1', ms: '1 ms', pct: '0.1 pts', pctx: '0.0001 pts', dec: '0.000001' };
/** Words for the two rows a number cannot describe: an unknown that still carries two numbers, and a gap too small to print. Else null. */
export function markText(d: Data, r: Row): string | null {
  if (r.note === 'could not check') return 'could not check';
  if (!r.differs || r.v == null || r.v2 == null || r.v === r.v2) return null;
  return Number(fmtDiff(r.v - r.v2, d.fmt).replace(/[^0-9.]/g, '')) === 0 ? `differs by less than ${PRINTED_STEP[d.fmt]}` : null;
}
const isPct = (f: Fmt) => f === 'pct' || f === 'pctx';
/** A value as the data says it reads: the data's own formatter when it has one, else the unit's. */
const val = (d: Data, v: number | null | undefined, signed = false, compact = false): string =>
  d.format && v !== null && v !== undefined && !Number.isNaN(v) ? (signed && v > 0 ? '+' : '') + d.format(v) : fmt(v, d.fmt, signed, compact);
const dif = (d: Data, v: number): string => (isPct(d.fmt) ? fmtDiff(v, d.fmt) : d.format ? (v > 0 ? '+' : '') + d.format(v) : fmtDiff(v, d.fmt));
const pctOff = (v: number, v2: number) => (v2 === 0 ? (v === 0 ? 0 : 1) : (v - v2) / Math.abs(v2));
/** A change as a percent, or "new" when it grew from zero (a percent of zero is not a number, so +100.0% would be a made-up one). */
const pctText = (v: number, v2: number) => (v2 === 0 && v !== 0 ? 'new' : fmt(pctOff(v, v2), 'pct', true));
/** Blank is not zero: a missing number reads as the word, never as an empty label or a zero. */
const BLANK = 'blank';

function useWidth<T extends HTMLElement>(): [React.RefObject<T | null>, number] {
  const ref = useRef<T | null>(null);
  const [w, setW] = useState(0);
  useLayoutEffect(() => { if (ref.current) setW(ref.current.clientWidth); }, []);
  useEffect(() => {
    const el = ref.current; if (!el) return;
    const ro = new ResizeObserver(() => setW(el.clientWidth));
    ro.observe(el);
    return () => ro.disconnect();
  }, []);
  return [ref, w];
}

const C = {
  main: 'var(--sem-accent)', other: 'var(--sem-series-neutral)', rule: 'var(--sem-rule)', muted: 'var(--sem-muted)', fg: 'var(--sem-fg)',
  bad: 'var(--sem-bad)', good: 'var(--sem-good)', band: 'color-mix(in srgb, var(--sem-fg) 7%, transparent)', faint: 'var(--sem-faint)',
};
/** The label column: as wide as the longest label needs, never more than a quarter of the box. */
const LABEL_W = (w: number, rows: Row[] = []) => Math.min(Math.round(w * 0.28), 200, Math.max(44, 14 + 7 * Math.max(0, ...rows.map((r) => r.label.length))));

/** Draw one data set as one visual. `compact` is the small size a list row or a card uses. */
export function Visual({ kind, data, compact = false }: { kind: Kind; data: Data; compact?: boolean }) {
  const [ref, w] = useWidth<HTMLDivElement>();
  return <div ref={ref} className={`qv-visual qv-visual-${kind}`} data-kind={kind} data-testid={`visual-${kind}`}>
    {w > 0 && drawVisual(kind, data, w, compact)}
  </div>;
}

/** Draw at an exact width. `Visual` measures its box and calls this; tests and the screenshot harness call it directly. */
export function drawVisual(kind: Kind, d: Data, w: number, compact = false) {
  switch (kind) {
    case 'total': return <TotalCard d={d} w={w} compact={compact} />;
    case 'bars': return <Bars d={d} w={w} compact={compact} />;
    case 'groups': return <GroupedBars d={d} w={w} compact={compact} />;
    case 'line': return <Line d={d} w={w} compact={compact} />;
    case 'variance': return <Variance d={d} w={w} compact={compact} />;
    case 'dumbbell': return <Dumbbell d={d} w={w} compact={compact} />;
    case 'bullet': return <Bullet d={d} w={w} compact={compact} />;
    case 'slope': return <Slope d={d} w={w} compact={compact} />;
    case 'multiples': return <Multiples d={d} w={w} compact={compact} />;
    case 'waterfall': return <Waterfall d={d} w={w} compact={compact} />;
    case 'sparktable': return <SparkTable d={d} w={w} />;
    case 'heat': return <Heat d={d} w={w} />;
    default: return <DataTable d={d} />;
  }
}

/** Round a top value up to 1, 2, 2.5 or 5 times a power of ten, so axis ticks read cleanly. */
function nice(v: number) { const p = 10 ** Math.floor(Math.log10(v)); return [1, 1.5, 2, 2.5, 3, 4, 5, 6, 8, 10].map((m) => m * p).find((x) => x >= v) ?? v; }

function Label({ x, y, children, anchor = 'start', muted = false, size = 12, weight = 400 }: { x: number; y: number; children: React.ReactNode; anchor?: 'start' | 'end' | 'middle'; muted?: boolean; size?: number; weight?: number }) {
  return <text x={x} y={y} textAnchor={anchor} dominantBaseline="middle" fontSize={size} fontWeight={weight} fill={muted ? C.muted : C.fg} className="qv-svgtext">{children}</text>;
}
const clip = (s: string, n: number) => (s.length > n ? s.slice(0, n - 1) + '…' : s);

// ---- Total card with a visual (Graphicus kpi: KpiRules pick order; against last year pair bars, otherwise the split or a spark).
function TotalCard({ d, w, compact }: { d: Data; w: number; compact: boolean }) {
  const t = d.total;
  // A blank number has no total: the card says so instead of drawing one (the rules do not offer it, this is the second guard).
  if (!t) return <div className={`qv-total ${compact ? 'qv-total-compact' : ''}`}><div className="qv-total-figure">
    <div className="qv-total-label">{d.vLabel}</div><div className="qv-total-value qv-num" data-testid="total-value">{BLANK}</div></div></div>;
  const delta = t.prior !== undefined ? t.value - t.prior : null;
  // A failed check whose gap is under the printed step must not print a difference of 0: say it in words, as the table does.
  const deltaWords = delta !== null ? markText(d, { label: t.label, v: t.value, v2: t.prior, differs: d.rows.some((r) => r.differs) }) : null;
  const mark = t.mark ?? (d.rows.length > 1 ? 'bars' : 'spark');
  const markW = compact ? w : Math.max(220, w - 280);
  return <div className={`qv-total ${compact ? 'qv-total-compact' : ''}`}>
    <div className="qv-total-figure">
      <div className="qv-total-label">{t.label}</div>
      <div className="qv-total-value qv-num" data-testid="total-value">{val(d, t.value)}</div>
      {delta !== null && <div className="qv-total-delta qv-num">{deltaWords ?? `${val(d, delta, true)} (${pctText(t.value, t.prior!)})`} vs {t.priorLabel}</div>}
    </div>
    <div className="qv-total-mark">
      {mark === 'bars' && <Bars d={d} w={markW} compact />}
      {mark === 'spark' && <Spark values={d.rows[0]?.series ?? d.rows.map((r) => r.v ?? 0)} w={markW} h={compact ? 36 : 64} labels={d.periods} />}
      {mark === 'bullet' && <Bullet d={{ ...d, rows: [{ label: t.label, v: t.value, v2: t.prior ?? t.value }] }} w={markW} compact />}
      {mark === 'pair' && <Bars d={{ ...d, rows: [{ label: t.priorLabel ?? 'Expected', v: t.prior ?? 0 }, { label: d.vLabel, v: t.value }] }} w={markW} compact keepOrder pairTone />}
    </div>
  </div>;
}

function Spark({ values, w, h, labels }: { values: number[]; w: number; h: number; labels?: string[] }) {
  if (!values.length) return null;
  const lo = Math.min(...values), hi = Math.max(...values), span = hi - lo || 1, pad = 4;
  const px = (i: number) => pad + (i * (w - 2 * pad)) / Math.max(1, values.length - 1);
  const py = (v: number) => h - pad - ((v - lo) * (h - 2 * pad)) / span;
  const path = values.map((v, i) => `${i ? 'L' : 'M'}${px(i).toFixed(1)} ${py(v).toFixed(1)}`).join(' ');
  const last = values.length - 1;
  return <svg width={w} height={h + (labels ? 16 : 0)} role="img" aria-label="Trend">
    <path d={`${path} L${px(last)} ${h} L${px(0)} ${h} Z`} fill="color-mix(in srgb, var(--sem-accent) 12%, transparent)" />
    <path d={path} fill="none" stroke={C.main} strokeWidth={1.6} />
    <circle cx={px(last)} cy={py(values[last])} r={3} fill={C.main} />
    {labels && <><Label x={px(0)} y={h + 9} muted size={11}>{labels[0]}</Label><Label x={px(last)} y={h + 9} anchor="end" muted size={11}>{labels[labels.length - 1]}</Label></>}
  </svg>;
}

// ---- Bars (Graphicus bar: one group, sorted, up to 50 members).
function Bars({ d, w, compact, keepOrder = false, pairTone = false }: { d: Data; w: number; compact: boolean; keepOrder?: boolean; pairTone?: boolean }) {
  // Blank members go last. A loss is drawn to the left of a zero line in its own colour, so it never looks like a gain.
  const rows = keepOrder ? d.rows : [...d.rows].sort((a, b) => (b.v ?? -Infinity) - (a.v ?? -Infinity));
  // The number at the end of a bar gets the room its longest text needs (hero 26 N8: "$22,341,369.25" was cut at 76px).
  const rh = compact ? 22 : 28, lw = LABEL_W(w, rows), vw = Math.max(76, 10 + Math.ceil(7.2 * Math.max(0, ...rows.map((r) => (r.v === null || r.v === undefined ? 0 : val(d, r.v).length)))));
  const known = rows.map((r) => r.v).filter((v): v is number => v !== null && v !== undefined);
  const maxPos = Math.max(0, ...known), maxNeg = Math.max(0, ...known.map((v) => -v));
  const span = maxPos + maxNeg || 1;
  const bw = Math.max(20, w - lw - vw - 8);
  const zeroX = lw + (maxNeg / span) * bw;
  const hasNeg = maxNeg > 0;
  return <svg width={w} height={rows.length * rh + 4} role="img" aria-label="Bars">
    {hasNeg && <line x1={zeroX} x2={zeroX} y1={0} y2={rows.length * rh + 4} stroke={C.muted} />}
    {rows.map((r, i) => {
      const y = i * rh + 2, blank = r.v === null || r.v === undefined, v = r.v ?? 0, bl = (Math.abs(v) / span) * bw;
      const neg = v < 0;
      return <g key={r.label}>
        <Label x={0} y={y + rh / 2} muted={compact}>{clip(r.label, Math.floor((lw - 6) / 6.6))}</Label>
        {blank ? <Label x={zeroX + 6} y={y + rh / 2} muted>{noShareText(r) || BLANK}</Label> : <>
          <rect x={neg ? zeroX - bl : zeroX} y={y + rh * 0.22} width={Math.max(1, bl)} height={rh * 0.56} rx={2} fill={neg ? C.bad : pairTone && i === 0 ? C.other : C.main} />
          <Label x={neg ? zeroX + 6 : zeroX + bl + 6} y={y + rh / 2} muted>{val(d, r.v)}</Label>
        </>}
      </g>;
    })}
  </svg>;
}

// ---- Grouped bars: one number split by two fields. A cluster for each member, one bar in it for each series (a year for each brand).
// d.rows are the clusters, d.periods name the series, row.series holds each series' number in that order. A missing number draws no bar.
const SERIES_VARS = ['--sem-series-1', '--sem-series-2', '--sem-series-3', '--sem-series-4', '--sem-series-5', '--sem-series-6', '--sem-series-neutral', '--sem-faint'];
function GroupedBars({ d, w, compact }: { d: Data; w: number; compact: boolean }) {
  const names = d.periods ?? [];
  const colour = (j: number) => `var(${SERIES_VARS[j % SERIES_VARS.length]})`;
  const rows = [...d.rows].sort((a, b) => Math.max(0, ...(b.series ?? []).map((v) => v ?? 0)) - Math.max(0, ...(a.series ?? []).map((v) => v ?? 0)));
  const bh = compact ? 10 : 13, gap = 10, ch = names.length * bh + gap;
  const all = rows.flatMap((r) => r.series ?? []).filter((v) => typeof v === 'number' && Number.isFinite(v));
  const maxPos = Math.max(0, ...all), maxNeg = Math.max(0, ...all.map((v) => -v)), span = maxPos + maxNeg || 1;
  const lw = LABEL_W(w, rows), vw = Math.max(76, 10 + Math.ceil(7.2 * Math.max(0, ...all.map((v) => val(d, v).length))));
  const bw = Math.max(20, w - lw - vw - 8), zeroX = lw + (maxNeg / span) * bw;
  const top = 22;
  return <div className="qv-groups" data-testid="groups-chart">
    <div className="qv-legend" role="list">{names.map((n, j) => <span key={n} className="qv-legend-item" role="listitem"><span className="qv-swatch" style={{ background: colour(j) }} aria-hidden="true" />{n}</span>)}</div>
    <svg width={w} height={rows.length * ch + top - gap / 2} role="img" aria-label="Grouped bars">
      {maxNeg > 0 && <line x1={zeroX} x2={zeroX} y1={0} y2={rows.length * ch} stroke={C.muted} />}
      {rows.map((r, i) => {
        const y0 = i * ch;
        return <g key={r.label}>
          <Label x={0} y={y0 + (names.length * bh) / 2} muted={compact}>{clip(r.label, Math.floor((lw - 6) / 6.6))}</Label>
          {names.map((n, j) => {
            const v = r.series?.[j], y = y0 + j * bh;
            if (v === null || v === undefined) return null;
            const bl = (Math.abs(v) / span) * bw;
            return <g key={n}>
              <rect x={v < 0 ? zeroX - bl : zeroX} y={y + 1} width={Math.max(1, bl)} height={bh - 2} rx={2} fill={colour(j)}><title>{`${r.label}, ${n}: ${val(d, v)}`}</title></rect>
              <Label x={v < 0 ? zeroX + 6 : zeroX + bl + 6} y={y + bh / 2} muted size={compact ? 10 : 11}>{val(d, v)}</Label>
            </g>;
          })}
        </g>;
      })}
    </svg>
  </div>;
}

// ---- Line (Graphicus line: an ordered axis, one unit).
function Line({ d, w, compact }: { d: Data; w: number; compact: boolean }) {
  // A blank is a gap: the line breaks there and no dot is drawn, because zero is a number the grid does not show.
  const pts = d.rows.map((r) => r.v ?? null);
  const known = pts.filter((v): v is number => v !== null);
  const h = compact ? 90 : 200, b = 22, top = 10;
  const lo = Math.min(0, ...known) < 0 ? -nice(-Math.min(...known) * 1.05) : 0, hi = nice(Math.max(0, ...known) * 1.05 || 1);
  const ticks = lo < 0 ? [lo, 0, hi] : [0, hi / 2, hi];
  // The left margin is as wide as the longest axis label needs: "$3,000,000.00" was cut to ")000.00" at a fixed 52 px (UAT 17 Oct, F36).
  const tickText = ticks.map((t) => val(d, t, false, true));
  const l = Math.max(52, 14 + Math.ceil(6.2 * Math.max(...tickText.map((x) => x.length))));
  const px = (i: number) => l + (i * (w - l - 10)) / Math.max(1, pts.length - 1);
  const py = (v: number) => top + (h - top - b) * (1 - (v - lo) / (hi - lo));
  // Every Nth label, and the last one: a label that would sit on the last is left out, so the end of the axis never overlaps (F36).
  // Each label gets the room its own text needs: "2024 / January" is wider than the fixed 70 px that ran labels together (UAT 18 Oct, item 65).
  const labelW = 14 + 6.2 * Math.max(0, ...d.rows.map((r) => r.label.length));
  const every = Math.ceil(pts.length / Math.max(2, Math.floor((w - l) / labelW)));
  const last = pts.length - 1;
  const labelled = new Set<number>();
  for (let i = 0; i <= last; i += every) if (last - i >= every * 0.7 || i === last) labelled.add(i);
  labelled.add(last);
  const prior = d.rows.map((r) => r.v2 ?? null);
  const path = (vs: (number | null)[]) => vs.map((v, i) => (v === null ? '' : `${i > 0 && vs[i - 1] !== null ? 'L' : 'M'}${px(i)} ${py(v)}`)).filter(Boolean).join(' ');
  return <svg width={w} height={h} role="img" aria-label="Line">
    {ticks.map((t, ti) => <g key={t}><line x1={l} x2={w - 6} y1={py(t)} y2={py(t)} stroke={C.rule} /><Label x={l - 6} y={py(t)} anchor="end" muted size={11}>{tickText[ti]}</Label></g>)}
    {prior.some((p) => p !== null) && <path d={path(prior)} fill="none" stroke={C.other} strokeDasharray="3 3" strokeWidth={1.4} />}
    <path d={path(pts)} fill="none" stroke={C.main} strokeWidth={2} />
    {pts.map((v, i) => v === null ? <Label key={i} x={px(i)} y={py(lo < 0 ? 0 : lo) - 9} anchor="middle" muted size={10}>{BLANK}</Label> : <circle key={i} cx={px(i)} cy={py(v)} r={2.4} fill={C.main} />)}
    {d.rows.map((r, i) => labelled.has(i) && <Label key={r.label} x={px(i)} y={h - 8} anchor={i === last && last > 0 ? 'end' : 'middle'} muted size={11}>{r.label}</Label>)}
  </svg>;
}

// ---- Variance bars (Graphicus variance: actual minus expected from a zero line, signed labels; 2 to 30 members).
function Variance({ d, w, compact }: { d: Data; w: number; compact: boolean }) {
  const rh = compact ? 20 : 28, lw = LABEL_W(w, d.rows), vw = Math.max(compact ? 92 : 132, 8 + 7 * Math.max(0, ...d.rows.map((r) => r.diffText?.length ?? 0)));
  const diffs = d.rows.map((r) => (noShare(r) ? 0 : (r.v ?? 0) - (r.v2 ?? 0)));
  const offs = d.rows.map((r) => pctOff(r.v ?? 0, r.v2 ?? 0));
  const max = Math.max(...diffs.map(Math.abs)) || 1;
  const bw = Math.max(40, w - lw - vw - 8), mid = lw + bw / 2;
  // Each row's allowed difference is its own share of its own expected value, so the band is drawn per row.
  const tolW = (r: Row) => (d.tolerance ? Math.min(bw / 2, ((d.tolerance * Math.abs(r.v2 ?? 0)) / max) * (bw / 2)) : 0);
  return <svg width={w} height={d.rows.length * rh + 18} role="img" aria-label="Variance bars">
    <line x1={mid} x2={mid} y1={0} y2={d.rows.length * rh} stroke={C.muted} />
    {d.rows.map((r, i) => {
      const y = i * rh, len = (Math.abs(diffs[i]) / max) * (bw / 2);
      const out = isOut(d, r, offs[i]);
      const tw = tolW(r);
      if (noShare(r)) return <g key={r.label}>
        <Label x={0} y={y + rh / 2} muted={!out}>{clip(r.label, Math.floor((lw - 6) / 6.6))}</Label>
        <Label x={w} y={y + rh / 2} anchor="end" muted={!out} weight={out ? 600 : 400}>{noShareText(r)}</Label>
      </g>;
      return <g key={r.label}>
        {tw > 0 && <g stroke={C.muted} strokeWidth={1.5}><line x1={mid - tw} x2={mid - tw} y1={y + rh * 0.25} y2={y + rh * 0.75} /><line x1={mid + tw} x2={mid + tw} y1={y + rh * 0.25} y2={y + rh * 0.75} /></g>}
        <Label x={0} y={y + rh / 2} muted={!out}>{clip(r.label, Math.floor((lw - 6) / 6.6))}</Label>
        <rect x={diffs[i] < 0 ? mid - len : mid} y={y + rh * 0.25} width={Math.max(diffs[i] === 0 ? 0 : 1.5, len)} height={rh * 0.5} rx={1.5} fill={out ? C.bad : C.other} />
        {diffs[i] === 0 && <circle cx={mid} cy={y + rh / 2} r={2.5} fill={C.muted} />}
        <Label x={w} y={y + rh / 2} anchor="end" muted={!out} weight={out ? 600 : 400}>{r.diffText ?? markText(d, r) ?? (diffs[i] === 0 ? 'same' : `${dif(d, diffs[i])}${compact || isPct(d.fmt) ? '' : `  (${pctText(d.rows[i].v ?? 0, d.rows[i].v2 ?? 0)})`}`)}</Label>
      </g>;
    })}
    {!compact && <Label x={mid} y={d.rows.length * rh + 10} anchor="middle" muted size={11}>{d.tolerance ? `Ticks: the allowed difference (${fmt(d.tolerance, 'pct')} of each value)` : `${d.vLabel} minus ${d.v2Label}`}</Label>}
  </svg>;
}

// ---- Dumbbell (Graphicus dumbbell: hollow dot for the other value, solid for the actual; scale not forced to zero).
function Dumbbell({ d, w, compact }: { d: Data; w: number; compact: boolean }) {
  const rh = compact ? 20 : 28, lw = LABEL_W(w, d.rows), vw = Math.max(d.pairLabel ? 130 : compact ? 70 : 110, 8 + 7 * Math.max(0, ...d.rows.map((r) => r.diffText?.length ?? 0)));
  const all = d.rows.filter((r) => !noShare(r)).flatMap((r) => [r.v ?? 0, r.v2 ?? 0]);
  if (all.length === 0) all.push(0);
  let lo = Math.min(...all), hi = Math.max(...all); const pad = (hi - lo) * 0.08 || Math.abs(hi) * 0.05 || 1; lo = d.zero ? 0 : lo - pad; hi += pad;
  const bw = Math.max(40, w - lw - vw - 12);
  const px = (v: number) => lw + ((v - lo) / (hi - lo)) * bw;
  const keyY = d.rows.length * rh + 10;
  return <svg width={w} height={d.rows.length * rh + (compact ? 4 : 22)} role="img" aria-label="Dumbbell">
    {d.rows.map((r, i) => {
      const y = i * rh + rh / 2, a = px(r.v2 ?? 0), b = px(r.v ?? 0);
      const off = r.v2 ? pctOff(r.v ?? 0, r.v2) : 0, out = isOut(d, r, off);
      if (noShare(r)) return <g key={r.label}>
        <Label x={0} y={y} muted={!out}>{clip(r.label, Math.floor((lw - 6) / 6.6))}</Label>
        <line x1={lw} x2={lw + bw} y1={y} y2={y} stroke={C.rule} />
        <Label x={w} y={y} anchor="end" muted={!out} weight={out ? 600 : 400}>{noShareText(r)}</Label>
      </g>;
      return <g key={r.label}>
        <Label x={0} y={y} muted={!out}>{clip(r.label, Math.floor((lw - 6) / 6.6))}</Label>
        <line x1={lw} x2={lw + bw} y1={y} y2={y} stroke={C.rule} />
        <line x1={Math.min(a, b)} x2={Math.max(a, b)} y1={y} y2={y} stroke={out ? C.bad : C.other} strokeWidth={3} />
        <circle cx={a} cy={y} r={4.5} fill="var(--sem-surface)" stroke={C.other} strokeWidth={1.6} />
        <circle cx={b} cy={y} r={4.5} fill={out ? C.bad : C.main} />
        <Label x={w} y={y} anchor="end" muted={!out} weight={out ? 600 : 400}>{r.diffText ?? markText(d, r) ?? (d.pairLabel ? `${val(d, r.v2).replace(' ms', '')} to ${val(d, r.v)}` : r.v === r.v2 ? 'same' : dif(d, (r.v ?? 0) - (r.v2 ?? 0)))}</Label>
      </g>;
    })}
    {!compact && <g>
      <circle cx={lw + 5} cy={keyY} r={4} fill="var(--sem-surface)" stroke={C.other} strokeWidth={1.6} /><Label x={lw + 14} y={keyY} muted size={11}>{d.v2Label}</Label>
      <circle cx={lw + 24 + (d.v2Label?.length ?? 0) * 6.5} cy={keyY} r={4} fill={C.main} /><Label x={lw + 33 + (d.v2Label?.length ?? 0) * 6.5} y={keyY} muted size={11}>{d.vLabel}</Label>
    </g>}
  </svg>;
}

// ---- Bullet (Graphicus bullet: the actual bar over the allowed band, a tick for the target; scale from zero).
function Bullet({ d, w, compact }: { d: Data; w: number; compact: boolean }) {
  const rh = compact ? 22 : 30, lw = LABEL_W(w, d.rows), vw = compact ? 64 : 100;
  const max = Math.max(...d.rows.flatMap((r) => [r.v ?? 0, (r.v2 ?? 0) * (1 + (d.tolerance ?? 0))])) * 1.05 || 1;
  const bw = Math.max(40, w - lw - vw - 10);
  const px = (v: number) => lw + (v / max) * bw;
  const tol = d.tolerance ?? 0;
  return <svg width={w} height={d.rows.length * rh + 4} role="img" aria-label="Bullet">
    {d.rows.map((r, i) => {
      const y = i * rh + 2, t = r.v2 ?? 0, out = r.differs !== undefined ? r.differs : tol > 0 && Math.abs(pctOff(r.v ?? 0, t)) > tol;
      if (r.v == null) return <g key={r.label}>
        <Label x={0} y={y + rh / 2} muted={!out}>{clip(r.label, Math.floor((lw - 6) / 6.6))}</Label>
        <Label x={w} y={y + rh / 2} anchor="end" muted={!out}>{noShareText(r)}</Label>
      </g>;
      return <g key={r.label}>
        <Label x={0} y={y + rh / 2} muted={!out}>{clip(r.label, Math.floor((lw - 6) / 6.6))}</Label>
        <rect x={lw} y={y + rh * 0.18} width={bw} height={rh * 0.64} fill={C.band} />
        {tol > 0 && <rect x={px(t * (1 - tol))} y={y + rh * 0.18} width={Math.max(2, px(t * (1 + tol)) - px(t * (1 - tol)))} height={rh * 0.64} fill="color-mix(in srgb, var(--sem-fg) 16%, transparent)" />}
        <rect x={lw} y={y + rh * 0.36} width={Math.max(1, px(r.v ?? 0) - lw)} height={rh * 0.28} fill={out ? C.bad : C.main} />
        <line x1={px(t)} x2={px(t)} y1={y + rh * 0.1} y2={y + rh * 0.9} stroke={C.fg} strokeWidth={2} />
        <Label x={w} y={y + rh / 2} anchor="end" muted={!out}>{val(d, r.v)}</Label>
      </g>;
    })}
  </svg>;
}

// ---- Slope (Graphicus slope: exactly two points, 3 to 20 members; the members that moved most are drawn on top).
function Slope({ d, w, compact }: { d: Data; w: number; compact: boolean }) {
  const h = compact ? 110 : Math.max(180, d.rows.length * 26 + 40), top = 32, bot = 12;
  const lw = Math.min(200, w * 0.3), x1 = lw, x2 = w - lw;
  const all = d.rows.flatMap((r) => [r.v ?? 0, r.v2 ?? 0]);
  const lo = Math.min(...all), hi = Math.max(...all), span = hi - lo || 1;
  const py = (v: number) => top + (h - top - bot) * (1 - (v - lo) / span);
  const moved = d.rows.map((r) => Math.abs(pctOff(r.v ?? 0, r.v2 ?? 0)));
  // Labels keep 15 px apart: close values push their labels down (then back up if they run off the bottom), so none overlap.
  const spread = (ys: number[]) => {
    const idx = ys.map((y, i) => [y, i] as const).sort((x, y) => x[0] - y[0]);
    const out = [...ys]; let last = -Infinity;
    for (const [y, i] of idx) { out[i] = Math.max(y, last + 15); last = out[i]; }
    return out;
  };
  const leftY = spread(d.rows.map((r) => py(r.v2 ?? 0))), rightY = spread(d.rows.map((r) => py(r.v ?? 0)));
  const svgH = compact ? h : Math.max(h, ...leftY, ...rightY) + 10;
  const order = d.rows.map((_, i) => i).sort((a, b) => moved[a] - moved[b]);
  return <svg width={w} height={svgH} role="img" aria-label="Slope">
    <Label x={x1} y={8} anchor="end" muted size={11}>{d.v2Label}</Label><Label x={x2} y={8} muted size={11}>{d.vLabel}</Label>
    <line x1={x1} x2={x1} y1={top - 6} y2={h - bot} stroke={C.rule} /><line x1={x2} x2={x2} y1={top - 6} y2={h - bot} stroke={C.rule} />
    {order.map((i) => {
      const r = d.rows[i], hot = moved[i] > (d.tolerance ?? 0.0001);
      return <g key={r.label} opacity={hot ? 1 : 0.75}>
        <line x1={x1} x2={x2} y1={py(r.v2 ?? 0)} y2={py(r.v ?? 0)} stroke={hot ? C.main : C.other} strokeWidth={hot ? 2 : 1.3} />
        <circle cx={x1} cy={py(r.v2 ?? 0)} r={3} fill={C.other} /><circle cx={x2} cy={py(r.v ?? 0)} r={3} fill={hot ? C.main : C.other} />
        {!compact && <><Label x={x1 - 8} y={leftY[i]} anchor="end" muted>{clip(r.label, 14)} {val(d, r.v2)}</Label>
          <Label x={x2 + 8} y={rightY[i]} muted={!hot}>{val(d, r.v)} {clip(r.label, 14)}</Label></>}
      </g>;
    })}
  </svg>;
}

// ---- Small multiples (Graphicus small_multiples: 2 to 12 members, one shared scale, 4 across from 520 px, 2 below).
function Multiples({ d, w, compact }: { d: Data; w: number; compact: boolean }) {
  const across = w >= 520 ? Math.min(4, d.rows.length) : 2, gap = 12;
  const cw = (w - gap * (across - 1)) / across, ch = compact ? 46 : 70;
  const all = d.rows.flatMap((r) => r.series ?? []);
  const lo = Math.min(0, ...all), hi = Math.max(...all) || 1;
  return <div className="qv-multiples" style={{ gridTemplateColumns: `repeat(${across}, minmax(0, 1fr))`, gap }}>
    {d.rows.map((r) => {
      const s = r.series ?? [], px = (i: number) => 2 + (i * (cw - 4)) / Math.max(1, s.length - 1), py = (v: number) => ch - 2 - ((v - lo) / (hi - lo)) * (ch - 6);
      return <div key={r.label} className="qv-multiple">
        <div className="qv-multiple-head"><span className="truncate">{r.label}</span><span className="qv-num">{val(d, s[s.length - 1])}</span></div>
        <svg width={cw} height={ch} role="img" aria-label={r.label}>
          <line x1={0} x2={cw} y1={ch - 2} y2={ch - 2} stroke={C.rule} />
          <path d={s.map((v, i) => `${i ? 'L' : 'M'}${px(i).toFixed(1)} ${py(v).toFixed(1)}`).join(' ')} fill="none" stroke={r.note ? C.bad : C.main} strokeWidth={1.6} />
          {s.length > 0 && <circle cx={px(s.length - 1)} cy={py(s[s.length - 1])} r={2.6} fill={r.note ? C.bad : C.main} />}
        </svg>
        {r.note && <div className="qv-multiple-note">{r.note}</div>}
      </div>;
    })}
  </div>;
}

// ---- Waterfall (Graphicus waterfall: floating steps from a running total; totals neutral; 3 to 12 steps).
function Waterfall({ d, w, compact }: { d: Data; w: number; compact: boolean }) {
  const start = d.start ?? { label: 'Start', value: 0 };
  const steps = d.rows;
  const end = start.value + steps.reduce((s, r) => s + (r.v ?? 0), 0);
  const bars: { label: string; from: number; to: number; total: boolean }[] = [{ label: start.label, from: 0, to: start.value, total: true }];
  let run = start.value;
  for (const s of steps) { bars.push({ label: s.label, from: run, to: run + (s.v ?? 0), total: false }); run += s.v ?? 0; }
  bars.push({ label: d.endLabel ?? 'End', from: 0, to: end, total: true });
  const h = compact ? 120 : 220, top = 22, bot = 26, l = 6;
  const vals = bars.flatMap((b) => [b.from, b.to]);
  const lo = Math.min(...vals.filter((v) => v > 0)) * 0.85, hi = Math.max(...vals) * 1.04;
  const floor = start.value > 0 && lo > 0 ? lo : 0;
  const py = (v: number) => top + (h - top - bot) * (1 - (Math.max(v, floor) - floor) / (hi - floor));
  const slot = (w - l) / bars.length, bw = Math.min(64, slot * 0.62);
  return <svg width={w} height={h} role="img" aria-label="Waterfall">
    {floor > 0 && <Label x={w} y={8} anchor="end" muted size={10}>The scale starts at {val(d, floor, false, true)}</Label>}
    {bars.map((b, i) => {
      const x = l + i * slot + (slot - bw) / 2, y1 = py(Math.max(b.from, b.to)), y2 = py(Math.min(b.from, b.to));
      const up = b.to >= b.from;
      return <g key={b.label + i}>
        <rect x={x} y={y1} width={bw} height={Math.max(1.5, y2 - y1)} rx={2} fill={b.total ? C.other : up ? C.main : C.bad} />
        {i < bars.length - 1 && <line x1={x + bw} x2={x + slot} y1={py(b.to)} y2={py(b.to)} stroke={C.faint} strokeDasharray="2 2" />}
        <Label x={x + bw / 2} y={y1 - 9} anchor="middle" size={11} muted={!b.total}>{b.total ? val(d, b.to) : val(d, b.to - b.from, true)}</Label>
        <Label x={x + bw / 2} y={h - 12} anchor="middle" muted size={11}>{clip(b.label, Math.max(6, Math.floor(slot / 7)))}</Label>
      </g>;
    })}
  </svg>;
}

// ---- Sparkline table (Graphicus sparkline_table: each row on its own scale, ending in a dot).
function SparkTable({ d, w }: { d: Data; w: number }) {
  const sw = Math.min(160, Math.max(80, w * 0.25));
  return <table className="qv-table qv-sparktable">
    <thead><tr><th>{d.dimLabel ?? ''}</th><th>Trend</th><th className="qv-r">{d.vLabel}</th>{d.v2Label && <th className="qv-r">{d.v2Label}</th>}</tr></thead>
    <tbody>{d.rows.map((r) => <tr key={r.label} className={r.note ? 'qv-row-bad' : ''}>
      <td>{r.label}{r.note && <div className="qv-cellnote">{r.note}</div>}</td>
      <td><Spark values={r.series ?? []} w={sw} h={22} /></td>
      <td className="qv-r qv-num">{val(d, r.v)}</td>
      {d.v2Label && <td className="qv-r qv-num">{val(d, r.v2)}</td>}
    </tr>)}</tbody>
  </table>;
}

// ---- Heat table (new: Graphicus has matrix with faint in-cell bars; a model developer reads counts of problems, so the cell is shaded).
function Heat({ d, plain = false }: { d: Data; w: number; plain?: boolean }) {
  const max = Math.max(1, ...(d.cells ?? []).flat().map((v) => v ?? 0));
  return <table className="qv-table qv-heat">
    <thead><tr><th>{d.dimLabel ?? ''}</th>{d.cols?.map((c) => <th key={c} className="qv-r">{c}</th>)}</tr></thead>
    <tbody>{d.rows.map((r, i) => <tr key={r.label}><td>{r.label}</td>{(d.cells?.[i] ?? []).map((v, j) => {
      const a = v ? 14 + Math.round((v / max) * 46) : 0;
      return <td key={j} className="qv-r qv-num qv-heatcell" style={{ background: v && !plain ? `color-mix(in srgb, var(--sem-bad) ${a}%, transparent)` : undefined }}>{v === null ? '' : v === 0 ? '0' : val(d, v)}</td>;
    })}</tr>)}</tbody>
  </table>;
}

// ---- The table (always offered, always one press away).
export function DataTable({ d }: { d: Data }) {
  if (d.cols && d.cells) return <Heat d={d} w={0} plain />;
  const two = d.rows.some((r) => r.v2 !== undefined);
  return <table className="qv-table" data-testid="data-table">
    <thead><tr><th>{d.dimLabel ?? ''}</th><th className="qv-r">{d.vLabel}</th>{two && <><th className="qv-r">{d.v2Label}</th>{!d.noGap && <><th className="qv-r">Difference</th><th className="qv-r">%</th></>}</>}</tr></thead>
    <tbody>{d.rows.map((r) => {
      const known = r.v != null && r.v2 != null;
      const diff = known ? r.v! - r.v2! : 0, off = two && known && r.v2 ? pctOff(r.v!, r.v2!) : 0;
      const out = two && !d.noGap && isOut(d, r, off);
      return <tr key={r.label} className={out ? 'qv-row-bad' : ''}>
        <td>{r.label}</td><td className="qv-r qv-num">{r.v == null ? <span className="sem-text-meta">{r.note === 'could not check' ? r.note : BLANK}</span> : val(d, r.v)}</td>
        {two && <><td className="qv-r qv-num">{r.v2 == null ? <span className="sem-text-meta">{r.note === 'blank' ? 'none' : r.note ?? 'none'}</span> : val(d, r.v2)}</td>{!d.noGap && <><td className="qv-r qv-num">{r.diffText ?? markText(d, r) ?? (!known ? '' : diff === 0 ? '0' : dif(d, diff))}</td><td className="qv-r qv-num">{known && r.v2 && !isPct(d.fmt) ? fmt(off, 'pct', true) : ''}</td></>}</>}
      </tr>;
    })}
      {d.total && !two && <tr className="qv-row-total"><td>Total</td><td className="qv-r qv-num">{val(d, d.total.value)}</td></tr>}
    </tbody>
  </table>;
}

/** Jev's spark mark (the same glyph the Plan uses for a Jev row), then the word. */
export function JevMark({ title }: { title?: string }) {
  return <span className="qv-jev" title={title ?? 'Jev chose this visual'}><svg viewBox="0 0 12 12" width="12" height="12" aria-hidden="true"><path d="M6 .8l1.4 3.8L11.2 6 7.4 7.4 6 11.2 4.6 7.4.8 6l3.8-1.4z" fill="currentColor" /></svg>Jev</span>;
}

/** The mark for a pick the chart rules made: the same size as Jev's, in the plain text colour, never claiming Jev chose. */
export function RulesMark() {
  return <span className="qv-rules" title="Picked by the chart rules">Rules</span>;
}

/** The line under a visual: who chose it and why, and the other visuals that fit these numbers (each one press). The rules' pick shows
 *  (Jev is off, below its bar, or the person is on the free plan) when `by` is 'rules' or `byJev` is false: then the mark says Rules, never Jev. */
export function PickLine({ pick, fits, reason, onPick, current, by: byProp = 'jev', byJev, sees, onBack }: { pick: Kind; fits: Kind[]; reason: string; current: Kind; onPick: (k: Kind) => void; by?: 'jev' | 'rules'; byJev?: boolean; sees?: string; onBack?: () => void }) {
  const by = byJev === false ? 'rules' : byProp;
  return <div className="qv-pickline" data-testid="pickline" data-by={by}>
    {by === 'jev' ? <JevMark /> : <span className="qv-jev qv-rules" title="The rules chose this visual">Rules</span>}<span className="qv-pick-reason">{reason}{sees ? ` ${sees}` : ''}</span>
    {onBack && <button type="button" className="sem-btn sem-btn-sm sem-btn-quiet" onClick={onBack} data-testid="pick-back">{by === 'jev' ? "Back to Jev's pick" : "Back to the rules' pick"}</button>}
    <span className="qv-fits" role="group" aria-label="Other visuals that fit">
      {[pick, ...fits.filter((f) => f !== pick)].map((k) => <button key={k} type="button" className="qv-fit" aria-pressed={current === k} data-testid={`fit-${k}`} onClick={() => onPick(k)}>{KIND_NAME[k]}</button>)}
    </span>
  </div>;
}
