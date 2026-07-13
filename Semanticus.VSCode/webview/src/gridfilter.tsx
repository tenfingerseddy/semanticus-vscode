// Advanced per-column filtering for the results grid (ResultGrid). View-only: these predicates run
// client-side over the already-loaded rows in the grid's memoized `view` — they never re-query the
// engine and never touch the model, so there's no dual-drive door here (this is a result-grid concern,
// not a model capability). Deterministic by construction: real number/date comparison, not substring.
//
// Family is decided from the column's CLR type name (the engine sets ColumnDef.Type via
// rdr.GetFieldType(i).Name → "DateTime" | "Int64" | "Decimal" | "Double" | "Boolean" | "String" …);
// when a type hint is missing (some DMV paths) we infer from sampled cell values.

import { useState, type CSSProperties, type ReactNode } from 'react';

export type FilterFamily = 'text' | 'number' | 'date' | 'boolean';

export type TextOp = 'contains' | 'ncontains' | 'eq' | 'starts' | 'ends' | 'blank' | 'nblank';
export type NumOp = 'between' | 'eq' | 'neq' | 'gt' | 'gte' | 'lt' | 'lte' | 'blank' | 'nblank';
export type RelUnit = 'day' | 'week' | 'month' | 'quarter' | 'year';
export type CalPeriod = 'today' | 'thisWeek' | 'thisMonth' | 'thisQuarter' | 'thisYear' | 'ytd';

export type ColumnFilter =
  | { kind: 'text'; op: TextOp; value: string; picked: string[] }
  | { kind: 'number'; op: NumOp; a: number | null; b: number | null }
  | { kind: 'date'; mode: 'rel'; n: number; unit: RelUnit }
  | { kind: 'date'; mode: 'cal'; period: CalPeriod }
  | { kind: 'date'; mode: 'range'; from: string | null; to: string | null }
  | { kind: 'boolean'; value: boolean };

// ─────────────────────────────────────────────────────────────────────────────
// Family detection

const NUM_RE = /int|decimal|double|single|float|number|money|currency|byte|sbyte|real/;

export function familyOf(type: string | undefined, samples: unknown[]): FilterFamily {
  const t = (type || '').toLowerCase();
  if (t) {
    if (t.includes('date')) return 'date';        // DateTime, DateTimeOffset (TimeSpan deliberately excluded)
    if (t === 'boolean' || t === 'bool') return 'boolean';
    if (NUM_RE.test(t)) return 'number';
    return 'text';
  }
  // No hint → infer from a sample of non-null values.
  let nums = 0, bools = 0, dates = 0, n = 0;
  for (const v of samples) {
    if (v === null || v === undefined || v === '') continue;
    n++;
    if (typeof v === 'number') nums++;
    else if (typeof v === 'boolean') bools++;
    else if (typeof v === 'string' && /^\d{4}-\d{2}-\d{2}([ T]|$)/.test(v) && !Number.isNaN(Date.parse(v))) dates++;
    if (n >= 40) break;
  }
  if (n === 0) return 'text';
  if (nums === n) return 'number';
  if (bools === n) return 'boolean';
  if (dates === n) return 'date';
  return 'text';
}

/** One family per column index, computed from type hints + a row sample (memo-friendly: pure). */
export function familiesOf(columns: { type?: string }[], rows: unknown[][]): FilterFamily[] {
  const sampleN = Math.min(rows.length, 200);
  return columns.map((c, ci) => {
    if (c.type) return familyOf(c.type, []);
    const s: unknown[] = [];
    for (let i = 0; i < sampleN && s.length < 40; i++) s.push(rows[i]?.[ci]);
    return familyOf(undefined, s);
  });
}

// ─────────────────────────────────────────────────────────────────────────────
// Value coercion

function toNum(v: unknown): number {
  if (typeof v === 'number') return v;
  if (v === null || v === undefined || v === '') return NaN;
  return Number(String(v).replace(/[^0-9eE.+-]/g, ''));
}

function toDate(v: unknown): number {
  if (v === null || v === undefined || v === '') return NaN;
  if (typeof v === 'number') return v;
  let s = String(v);
  if (/^\d{4}-\d{2}-\d{2}$/.test(s)) s += 'T00:00:00'; // date-only → force LOCAL (matches our local "now")
  return Date.parse(s);
}

function toBool(v: unknown): boolean {
  return v === true || v === 1 || v === 'true' || v === 'True' || v === '1';
}

function txt(v: unknown): string {
  return v === null || v === undefined ? '' : String(v);
}

// ─────────────────────────────────────────────────────────────────────────────
// Relative / calendar date windows → [lo, hi) in epoch ms, anchored to local "now".
//
// Boundary choice (documented, intuitive over pedantic): "Last N days/weeks" = exactly that many
// calendar days INCLUDING today; "Last N months/quarters/years" rolls back N calendar units from the
// start of today; calendar periods snap to the obvious boundaries. hi is the start of tomorrow so the
// whole of today is always included.

const DAY = 86400000;

function startOfToday(now: number): Date {
  const d = new Date(now);
  d.setHours(0, 0, 0, 0);
  return d;
}
function addMonths(d: Date, n: number): Date {
  const r = new Date(d);
  r.setMonth(r.getMonth() + n);
  return r;
}

export function dateWindow(f: Extract<ColumnFilter, { kind: 'date' }>, now: number): [number, number] {
  const today0 = startOfToday(now);
  const t0 = today0.getTime();
  const tomorrow0 = t0 + DAY;

  if (f.mode === 'range') {
    const lo = f.from ? toDate(f.from) : -Infinity;
    const hi = f.to ? toDate(f.to) + DAY : Infinity; // inclusive of the "to" day
    return [lo, hi];
  }
  if (f.mode === 'rel') {
    const n = Math.max(1, f.n | 0);
    switch (f.unit) {
      case 'day': return [t0 - (n - 1) * DAY, tomorrow0];
      case 'week': return [t0 - (n * 7 - 1) * DAY, tomorrow0];
      case 'month': return [addMonths(today0, -n).getTime(), tomorrow0];
      case 'quarter': return [addMonths(today0, -3 * n).getTime(), tomorrow0];
      case 'year': { const d = new Date(today0); d.setFullYear(d.getFullYear() - n); return [d.getTime(), tomorrow0]; }
    }
  }
  // calendar period
  const y = today0.getFullYear(), mo = today0.getMonth();
  switch (f.period) {
    case 'today': return [t0, tomorrow0];
    case 'thisWeek': { const dow = (today0.getDay() + 6) % 7; const s = t0 - dow * DAY; return [s, s + 7 * DAY]; } // Mon-start
    case 'thisMonth': return [new Date(y, mo, 1).getTime(), new Date(y, mo + 1, 1).getTime()];
    case 'thisQuarter': { const q = Math.floor(mo / 3); return [new Date(y, q * 3, 1).getTime(), new Date(y, q * 3 + 3, 1).getTime()]; }
    case 'thisYear': return [new Date(y, 0, 1).getTime(), new Date(y + 1, 0, 1).getTime()];
    case 'ytd': return [new Date(y, 0, 1).getTime(), tomorrow0];
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// Active-ness + predicate compilation

/** A filter only constrains (shows a chip / applies) when it actually carries a usable value. */
export function isActive(f: ColumnFilter): boolean {
  switch (f.kind) {
    case 'text': return f.op === 'blank' || f.op === 'nblank' || f.value.trim() !== '' || f.picked.length > 0;
    case 'number': return f.op === 'blank' || f.op === 'nblank' || (f.op === 'between' ? (f.a != null || f.b != null) : f.a != null);
    case 'date': return f.mode === 'range' ? !!(f.from || f.to) : f.mode === 'rel' ? f.n > 0 : true;
    case 'boolean': return true;
  }
}

export function compile(f: ColumnFilter, now: number): (v: unknown) => boolean {
  switch (f.kind) {
    case 'boolean': return (v) => toBool(v) === f.value;
    case 'number': {
      const { op, a, b } = f;
      return (v) => {
        const blank = v === null || v === undefined || v === '';
        if (op === 'blank') return blank;
        if (op === 'nblank') return !blank;
        const x = toNum(v);
        if (Number.isNaN(x)) return false;
        switch (op) {
          case 'between': return (a == null || x >= a) && (b == null || x <= b);
          case 'eq': return a != null && x === a;
          case 'neq': return a != null && x !== a;
          case 'gt': return a != null && x > a;
          case 'gte': return a != null && x >= a;
          case 'lt': return a != null && x < a;
          case 'lte': return a != null && x <= a;
        }
        return true;
      };
    }
    case 'date': {
      const [lo, hi] = dateWindow(f, now);
      return (v) => { const t = toDate(v); return !Number.isNaN(t) && t >= lo && t < hi; };
    }
    case 'text': {
      const { op } = f;
      const needle = f.value.trim().toLowerCase();
      const picked = f.picked.length ? new Set(f.picked) : null;
      return (v) => {
        const raw = txt(v);
        if (picked && !picked.has(raw)) return false;       // checklist membership (exact display value)
        const s = raw.toLowerCase();
        switch (op) {
          case 'contains': return needle === '' ? true : s.includes(needle);
          case 'ncontains': return needle === '' ? true : !s.includes(needle);
          case 'eq': return needle === '' ? true : s === needle;
          case 'starts': return needle === '' ? true : s.startsWith(needle);
          case 'ends': return needle === '' ? true : s.endsWith(needle);
          case 'blank': return raw === '';
          case 'nblank': return raw !== '';
        }
        return true;
      };
    }
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// Human phrase for the chip / tooltip (the column name is prepended by the caller)

const UNIT_LABEL: Record<RelUnit, string> = { day: 'day', week: 'week', month: 'month', quarter: 'quarter', year: 'year' };
const CAL_LABEL: Record<CalPeriod, string> = { today: 'today', thisWeek: 'this week', thisMonth: 'this month', thisQuarter: 'this quarter', thisYear: 'this year', ytd: 'year to date' };
const NUM_PHRASE: Record<Exclude<NumOp, 'between' | 'blank' | 'nblank'>, string> = { eq: '=', neq: '≠', gt: '>', gte: '≥', lt: '<', lte: '≤' };
const TEXT_PHRASE: Record<Exclude<TextOp, 'blank' | 'nblank'>, string> = { contains: 'contains', ncontains: 'does not contain', eq: 'is', starts: 'starts with', ends: 'ends with' };

export function describe(f: ColumnFilter): string {
  switch (f.kind) {
    case 'boolean': return f.value ? 'is true' : 'is false';
    case 'number': {
      if (f.op === 'blank') return 'is blank';
      if (f.op === 'nblank') return 'is not blank';
      if (f.op === 'between') {
        if (f.a != null && f.b != null) return `is between ${f.a} – ${f.b}`;
        if (f.a != null) return `≥ ${f.a}`;
        return `≤ ${f.b}`;
      }
      return `${NUM_PHRASE[f.op]} ${f.a}`;
    }
    case 'date':
      if (f.mode === 'rel') return `in the last ${f.n} ${UNIT_LABEL[f.unit]}${f.n === 1 ? '' : 's'}`;
      if (f.mode === 'cal') return `is ${CAL_LABEL[f.period]}`;
      if (f.from && f.to) return `is ${f.from} – ${f.to}`;
      if (f.from) return `is on/after ${f.from}`;
      return `is on/before ${f.to}`;
    case 'text': {
      if (f.op === 'blank') return 'is blank';
      if (f.op === 'nblank') return 'is not blank';
      const parts: string[] = [];
      if (f.picked.length) parts.push('is ' + (f.picked.length <= 2 ? f.picked.join(', ') : `${f.picked.slice(0, 2).join(', ')} +${f.picked.length - 2}`));
      if (f.value.trim()) parts.push(`${TEXT_PHRASE[f.op]} "${f.value.trim()}"`);
      return parts.join(' · ') || TEXT_PHRASE[f.op as keyof typeof TEXT_PHRASE] || '';
    }
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// Distinct values for the text checklist (computed from loaded rows; capped to keep it a picker, not a dump)

export function distinctValues(rows: unknown[][], col: number, cap = 500): { value: string; count: number }[] | null {
  const m = new Map<string, number>();
  for (const row of rows) {
    const s = txt(row?.[col]);
    if (s === '') continue;
    m.set(s, (m.get(s) || 0) + 1);
    if (m.size > cap) return null; // too high-cardinality for a checklist — fall back to condition+value only
  }
  return [...m.entries()].map(([value, count]) => ({ value, count })).sort((a, b) => b.count - a.count);
}

// ─────────────────────────────────────────────────────────────────────────────
// Defaults + preset table

function defaultFor(family: FilterFamily): ColumnFilter {
  switch (family) {
    case 'number': return { kind: 'number', op: 'between', a: null, b: null };
    case 'date': return { kind: 'date', mode: 'rel', n: 6, unit: 'month' };
    case 'boolean': return { kind: 'boolean', value: true };
    default: return { kind: 'text', op: 'contains', value: '', picked: [] };
  }
}

const DATE_PRESETS: { label: string; make: () => ColumnFilter }[] = [
  { label: 'Today', make: () => ({ kind: 'date', mode: 'cal', period: 'today' }) },
  { label: 'Last 7 days', make: () => ({ kind: 'date', mode: 'rel', n: 7, unit: 'day' }) },
  { label: 'Last 30 days', make: () => ({ kind: 'date', mode: 'rel', n: 30, unit: 'day' }) },
  { label: 'Last 3 months', make: () => ({ kind: 'date', mode: 'rel', n: 3, unit: 'month' }) },
  { label: 'Last 6 months', make: () => ({ kind: 'date', mode: 'rel', n: 6, unit: 'month' }) },
  { label: 'Last 12 months', make: () => ({ kind: 'date', mode: 'rel', n: 12, unit: 'month' }) },
  { label: 'This month', make: () => ({ kind: 'date', mode: 'cal', period: 'thisMonth' }) },
  { label: 'This quarter', make: () => ({ kind: 'date', mode: 'cal', period: 'thisQuarter' }) },
  { label: 'YTD', make: () => ({ kind: 'date', mode: 'cal', period: 'ytd' }) },
];

// ─────────────────────────────────────────────────────────────────────────────
// UI bits

export function FunnelIcon({ on, className, style }: { on?: boolean; className?: string; style?: CSSProperties }) {
  return (
    <svg viewBox="0 0 16 16" width="12" height="12" className={className} style={style} fill="currentColor" aria-hidden="true">
      <path d="M2 3.2h12v1.5l-4.4 4.6v3.4l-3.2 1.4V9.3L2 4.7z" opacity={on ? 1 : 0.9} />
    </svg>
  );
}

const POP = 280; // popover width

const sel: CSSProperties = { background: 'var(--sem-surface-2)', color: 'var(--sem-fg)', border: '1px solid var(--sem-border)' };

function Field({ children }: { children: ReactNode }) {
  return <div className="text-[10px] uppercase tracking-wide mb-1" style={{ color: 'var(--sem-muted)' }}>{children}</div>;
}

/**
 * The per-column filter editor, shaped by the column's family. Edits a local draft; Apply commits it.
 * Date quick-presets commit immediately (the whole point of "quick"); custom/range/number/text use Apply.
 */
export function FilterPopover(props: {
  family: FilterFamily; name: string; initial?: ColumnFilter; distinct?: { value: string; count: number }[] | null;
  onApply: (f: ColumnFilter) => void; onClear: () => void;
}) {
  const { family, name, initial, distinct, onApply, onClear } = props;
  const [draft, setDraft] = useState<ColumnFilter>(initial ?? defaultFor(family));

  const apply = () => onApply(draft);
  const headIcon =
    family === 'date' ? 'D' : family === 'number' ? '#' : family === 'boolean' ? '◑' : 'T';

  return (
    <div className="rounded-[11px] overflow-hidden flex flex-col text-[12px]"
      style={{ width: POP, background: 'var(--sem-surface)', border: '1px solid var(--sem-border-strong, var(--sem-border))', boxShadow: '0 16px 40px -22px rgba(0,0,0,.85)' }}>
      <div className="flex items-center gap-2 px-3 py-2.5" style={{ borderBottom: '1px solid var(--sem-border)' }}>
        <span style={{ color: 'var(--sem-accent)', fontWeight: 700 }}>{headIcon}</span>
        <span className="font-semibold truncate">{name}</span>
        <span style={{ color: 'var(--sem-muted)' }}>· {family}</span>
      </div>

      <div className="px-3 py-3 flex flex-col gap-3">
        {family === 'date' && <DateBody draft={draft as any} setDraft={setDraft as any} commit={onApply} />}
        {family === 'number' && <NumberBody draft={draft as any} setDraft={setDraft as any} />}
        {family === 'text' && <TextBody draft={draft as any} setDraft={setDraft as any} distinct={distinct} />}
        {family === 'boolean' && <BooleanBody commit={onApply} clear={onClear} value={(draft as any).value} />}
      </div>

      {family !== 'boolean' && (
        <div className="flex items-center gap-2 px-3 py-2.5" style={{ borderTop: '1px solid var(--sem-border)' }}>
          <button onClick={onClear} className="text-[11px]" style={{ color: 'var(--sem-muted)', background: 'none', border: 'none', cursor: 'pointer' }}>Clear</button>
          <button onClick={apply} className="ml-auto text-[12px] font-semibold rounded-md px-4 py-1.5"
            style={{ color: '#fff', background: 'var(--sem-accent)', border: 'none', cursor: 'pointer' }}>Apply</button>
        </div>
      )}
    </div>
  );
}

function eqJSON(a: unknown, b: unknown) { return JSON.stringify(a) === JSON.stringify(b); }

function DateBody({ draft, setDraft, commit }: { draft: Extract<ColumnFilter, { kind: 'date' }>; setDraft: (f: ColumnFilter) => void; commit: (f: ColumnFilter) => void }) {
  const isRange = draft.mode === 'range';
  return (
    <>
      <div className="inline-flex rounded-md p-0.5 self-start" style={{ background: 'var(--sem-surface-2)', border: '1px solid var(--sem-border)' }}>
        {(['rel', 'range'] as const).map((m) => (
          <button key={m} onClick={() => setDraft(m === 'range' ? { kind: 'date', mode: 'range', from: null, to: null } : { kind: 'date', mode: 'rel', n: 6, unit: 'month' })}
            className="text-[11.5px] px-3 py-1 rounded"
            style={(isRange ? m === 'range' : m === 'rel') ? { background: 'var(--sem-accent)', color: 'var(--sem-on-accent)', fontWeight: 600, border: 'none' } : { background: 'none', color: 'var(--sem-muted)', border: 'none', cursor: 'pointer' }}>
            {m === 'rel' ? 'Relative' : 'Range'}
          </button>
        ))}
      </div>

      {!isRange && (
        <>
          <div>
            <Field>Quick ranges</Field>
            <div className="flex flex-wrap gap-1.5">
              {DATE_PRESETS.map((p) => {
                const pf = p.make();
                const on = eqJSON(pf, draft);
                return (
                  <button key={p.label} onClick={() => commit(pf)} className="text-[11.5px] rounded-full px-2.5 py-1"
                    style={on ? { background: 'var(--sem-accent-soft)', border: '1px solid var(--sem-accent)', color: 'var(--sem-fg)', cursor: 'pointer' } : { background: 'var(--sem-surface-2)', border: '1px solid var(--sem-border)', color: 'var(--sem-fg)', cursor: 'pointer' }}>
                    {p.label}
                  </button>
                );
              })}
            </div>
          </div>
          <div>
            <Field>Or custom</Field>
            <div className="flex items-center gap-2">
              <span style={{ color: 'var(--sem-muted)' }}>Last</span>
              <input type="number" min={1} value={draft.mode === 'rel' ? draft.n : 6} onChange={(e) => setDraft({ kind: 'date', mode: 'rel', n: Math.max(1, Number(e.target.value) || 1), unit: draft.mode === 'rel' ? draft.unit : 'month' })}
                className="w-16 text-right rounded-md px-2 py-1 outline-none tnum" style={sel} />
              <select value={draft.mode === 'rel' ? draft.unit : 'month'} onChange={(e) => setDraft({ kind: 'date', mode: 'rel', n: draft.mode === 'rel' ? draft.n : 6, unit: e.target.value as RelUnit })}
                className="rounded-md px-2 py-1 outline-none flex-1" style={sel}>
                <option value="day">days</option><option value="week">weeks</option><option value="month">months</option><option value="quarter">quarters</option><option value="year">years</option>
              </select>
            </div>
          </div>
        </>
      )}

      {isRange && (
        <>
          <div><Field>From</Field>
            <input type="date" value={draft.from ?? ''} onChange={(e) => setDraft({ ...draft, from: e.target.value || null })} className="w-full rounded-md px-2 py-1 outline-none" style={sel} /></div>
          <div><Field>To</Field>
            <input type="date" value={draft.to ?? ''} onChange={(e) => setDraft({ ...draft, to: e.target.value || null })} className="w-full rounded-md px-2 py-1 outline-none" style={sel} /></div>
          <p className="text-[11px]" style={{ color: 'var(--sem-muted)' }}>Leave either side blank for an open-ended range.</p>
        </>
      )}
    </>
  );
}

const NUM_OPS: { v: NumOp; label: string }[] = [
  { v: 'between', label: 'is between' }, { v: 'eq', label: 'equals' }, { v: 'neq', label: 'does not equal' },
  { v: 'gt', label: 'greater than (>)' }, { v: 'gte', label: 'greater or equal (≥)' }, { v: 'lt', label: 'less than (<)' },
  { v: 'lte', label: 'less or equal (≤)' }, { v: 'blank', label: 'is blank' }, { v: 'nblank', label: 'is not blank' },
];

function NumberBody({ draft, setDraft }: { draft: Extract<ColumnFilter, { kind: 'number' }>; setDraft: (f: ColumnFilter) => void }) {
  const num = (s: string): number | null => (s === '' ? null : Number(s));
  const noInput = draft.op === 'blank' || draft.op === 'nblank';
  return (
    <>
      <div>
        <Field>Condition</Field>
        <select value={draft.op} onChange={(e) => setDraft({ ...draft, op: e.target.value as NumOp })} className="w-full rounded-md px-2 py-1.5 outline-none" style={sel}>
          {NUM_OPS.map((o) => <option key={o.v} value={o.v}>{o.label}</option>)}
        </select>
      </div>
      {!noInput && (
        <div className="flex items-center gap-2">
          <input type="number" value={draft.a ?? ''} onChange={(e) => setDraft({ ...draft, a: num(e.target.value) })} placeholder={draft.op === 'between' ? 'min' : 'value'}
            className="rounded-md px-2 py-1.5 outline-none tnum text-right flex-1 min-w-0" style={sel} />
          {draft.op === 'between' && <>
            <span style={{ color: 'var(--sem-muted)' }}>–</span>
            <input type="number" value={draft.b ?? ''} onChange={(e) => setDraft({ ...draft, b: num(e.target.value) })} placeholder="max"
              className="rounded-md px-2 py-1.5 outline-none tnum text-right flex-1 min-w-0" style={sel} />
          </>}
        </div>
      )}
    </>
  );
}

const TEXT_OPS: { v: TextOp; label: string }[] = [
  { v: 'contains', label: 'contains' }, { v: 'ncontains', label: 'does not contain' }, { v: 'eq', label: 'equals' },
  { v: 'starts', label: 'starts with' }, { v: 'ends', label: 'ends with' }, { v: 'blank', label: 'is blank' }, { v: 'nblank', label: 'is not blank' },
];

function TextBody({ draft, setDraft, distinct }: { draft: Extract<ColumnFilter, { kind: 'text' }>; setDraft: (f: ColumnFilter) => void; distinct?: { value: string; count: number }[] | null }) {
  const noInput = draft.op === 'blank' || draft.op === 'nblank';
  const toggle = (val: string) => {
    const has = draft.picked.includes(val);
    setDraft({ ...draft, picked: has ? draft.picked.filter((x) => x !== val) : [...draft.picked, val] });
  };
  return (
    <>
      <div>
        <Field>Condition</Field>
        <select value={draft.op} onChange={(e) => setDraft({ ...draft, op: e.target.value as TextOp })} className="w-full rounded-md px-2 py-1.5 outline-none" style={sel}>
          {TEXT_OPS.map((o) => <option key={o.v} value={o.v}>{o.label}</option>)}
        </select>
      </div>
      {!noInput && <input value={draft.value} onChange={(e) => setDraft({ ...draft, value: e.target.value })} placeholder="Type a value…" spellCheck={false}
        className="w-full rounded-md px-2 py-1.5 outline-none" style={sel} />}
      {distinct && distinct.length > 0 && (
        <div>
          <Field>Or pick values</Field>
          <div className="rounded-md overflow-auto" style={{ maxHeight: 132, background: 'var(--sem-surface-2)', border: '1px solid var(--sem-border)' }}>
            {distinct.slice(0, 500).map((d) => {
              const on = draft.picked.includes(d.value);
              return (
                <label key={d.value} className="flex items-center gap-2 px-2.5 py-1.5 cursor-pointer" style={{ borderBottom: '1px solid color-mix(in srgb,var(--sem-border) 60%, transparent)' }} onClick={() => toggle(d.value)}>
                  <span className="grid place-items-center rounded-[4px] text-[10px]" style={{ width: 14, height: 14, color: '#fff', background: on ? 'var(--sem-accent)' : 'transparent', border: `1px solid ${on ? 'var(--sem-accent)' : 'var(--sem-border)'}` }}>{on ? '✓' : ''}</span>
                  <span className="truncate flex-1">{d.value}</span>
                  <span className="tnum text-[11px]" style={{ color: 'var(--sem-muted)' }}>{d.count}</span>
                </label>
              );
            })}
          </div>
        </div>
      )}
    </>
  );
}

function BooleanBody({ value, commit, clear }: { value: boolean; commit: (f: ColumnFilter) => void; clear: () => void }) {
  const opt = (label: string, on: boolean, onClick: () => void) => (
    <button onClick={onClick} className="text-[11.5px] px-3 py-1 rounded"
      style={on ? { background: 'var(--sem-accent)', color: 'var(--sem-on-accent)', fontWeight: 600, border: 'none', cursor: 'pointer' } : { background: 'none', color: 'var(--sem-muted)', border: 'none', cursor: 'pointer' }}>{label}</button>
  );
  return (
    <div className="inline-flex rounded-md p-0.5 self-start" style={{ background: 'var(--sem-surface-2)', border: '1px solid var(--sem-border)' }}>
      {opt('Any', false, clear)}
      {opt('True', value === true, () => commit({ kind: 'boolean', value: true }))}
      {opt('False', value === false, () => commit({ kind: 'boolean', value: false }))}
    </div>
  );
}
