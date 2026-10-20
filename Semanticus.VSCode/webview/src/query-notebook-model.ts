// The Query notebook's pure half (1.6, Kane 16 Oct, option C): which rows the notebook shows and in what order, what a row says,
// which rows Time all measures, and the figure a row shows. No React here, so the rules run in plain node tests.
//
// A row of the notebook and an item of the full history are the same record (the engine keeps both in one file). The notebook is
// the pinned rows and the open rows; the history is every row. Closing a row takes it out of the notebook and keeps it in the history.
import { questionLine, type DataQuestionRecord } from './askdata-model';
import type { ResultSet } from './wire';
import type { PivotConfig } from './daxwells';
import { formatOf } from './query-model';
import { squash } from './daxsquash';

export type Rec = DataQuestionRecord;

/** Time all measures a chosen set of rows: the rows the person ticked, the pinned rows, or every row of the notebook. */
export type TimeScope = 'selection' | 'pinned' | 'all';
export const SCOPES: TimeScope[] = ['selection', 'pinned', 'all'];
export const SCOPE_WORD: Record<TimeScope, string> = { selection: 'Selected rows', pinned: 'Pinned rows', all: 'All rows' };

/** A first run slower than this is flagged amber in the row. One second is where a person starts to wait. */
export const SLOW_MS = 1000;

const newestFirst = (a: Rec, b: Rec) => Date.parse(b.askedAt) - Date.parse(a.askedAt);

/** The rows the notebook shows: pinned ones first, then the open ones, each group newest first. */
export function notebookRows(all: Rec[]): Rec[] {
  const mine = all.filter((r) => r.pinned || r.inNotebook);
  return [...mine.filter((r) => r.pinned).sort(newestFirst), ...mine.filter((r) => !r.pinned).sort(newestFirst)];
}

/** The full history, newest first. */
export const historyRows = (all: Rec[]): Rec[] => [...all].sort(newestFirst);

/** One line of DAX for a row's name: the spaces squashed, the keyword kept. */
const oneLine = (dax: string) => dax.replace(/\s+/g, ' ').trim();

/** A query the field builder wrote, named from its fields: "Net Sales by Brand and Year". Anything else (a hand-written query, a shape this does
 *  not know) is null and keeps its own text (UAT 18 Oct, N19: the list showed "EVALUATE SUMMARIZECOLUMNS(..."). */
export function daxName(dax: string): string | null {
  const m = /^EVALUATE\s+(?:CALCULATETABLE\(\s*)?SUMMARIZECOLUMNS\(([\s\S]*)$/i.exec(dax.trim());
  if (!m) return null;
  const body = m[1];
  const firstText = body.indexOf('"');
  const groups = [...(firstText < 0 ? body : body.slice(0, firstText)).matchAll(/\[([^\]]+)\]/g)].map((g) => g[1]);
  const measures = [...body.matchAll(/"((?:[^"]|"")*)"\s*,\s*(?:EVALUATEANDLOG\()?\[[^\]]+\]/g)].map((g) => g[1].replace(/""/g, '"'));
  if (!measures.length) return null;
  const what = measures.length > 2 ? `${measures.slice(0, 2).join(', ')} and ${measures.length - 2} more` : measures.join(' and ');
  return groups.length ? `${what} by ${groups.length > 2 ? `${groups.slice(0, 2).join(', ')} and ${groups.length - 2} more` : groups.join(' and ')}` : what;
}

/** What a row is called: the words asked, else how it was read, else the DAX on one line. */
export function rowText(r: Rec): string {
  if (r.kind === 'dax') return daxName(r.dax ?? '') ?? (oneLine(r.dax ?? '') || 'New query');
  return (r.question || r.reading || questionLine(r.parts) || (r.dax ? oneLine(r.dax) : '') || (r.outcome === 'refused' ? 'A question that was not run' : 'A question with no number yet')).trim();
}
export const isDaxRow = (r: Rec) => r.kind === 'dax';

/** The DAX a row can be timed with: its own text. A question that never got as far as a query has none. */
export const timeable = (r: Rec): boolean => !!(r.dax && r.dax.trim());

/** The rows one Time all press measures, in the order the notebook shows them. Rows with no query are left out. */
export function timeScope(scope: TimeScope, shown: Rec[], selected: ReadonlySet<string>): Rec[] {
  const pick = scope === 'selection' ? shown.filter((r) => selected.has(r.id)) : scope === 'pinned' ? shown.filter((r) => r.pinned) : shown;
  return pick.filter(timeable);
}
/** How many rows each choice would time, so the menu can say so and disable an empty one. */
export const scopeCounts = (shown: Rec[], selected: ReadonlySet<string>): Record<TimeScope, number> =>
  ({ selection: timeScope('selection', shown, selected).length, pinned: timeScope('pinned', shown, selected).length, all: timeScope('all', shown, selected).length });

/** Milliseconds in the words a row shows: "38 ms", "1.4 s". */
export const msWord = (ms: number): string => (ms >= 1000 ? `${(ms / 1000).toFixed(ms >= 10000 ? 0 : 1)} s` : `${Math.round(ms)} ms`);

/** The time column of a folded row: the last Time all (first run, then repeat runs), else how long the query took. */
export function timeWords(r: Rec): { text: string; slow: boolean; timed: boolean } {
  if (r.coldMs != null && r.warmMs != null) return { text: `${msWord(r.coldMs)} then ${msWord(r.warmMs)}`, slow: r.coldMs >= SLOW_MS, timed: true };
  // Hero 25 H2: on a shared endpoint the first run is skipped, so a row may hold repeat runs only. That is still a measured row.
  if (r.warmMs != null) return { text: `${msWord(r.warmMs)} repeat`, slow: false, timed: true };
  if (r.coldMs != null) return { text: `${msWord(r.coldMs)} first`, slow: r.coldMs >= SLOW_MS, timed: true };
  if (r.tookMs != null) return { text: msWord(r.tookMs), slow: r.tookMs >= SLOW_MS, timed: false };
  return { text: '', slow: false, timed: false };
}

/** The times Time all keeps for a row: whatever runs were measured. Null when none were (a failed call, or no runs at all). */
export function measuredTimes(c: { error?: string; coldTotal?: { n?: number; avgMs: number } | null; warmTotal?: { n?: number; avgMs: number } | null } | null | undefined): { coldMs?: number; warmMs?: number } | null {
  if (!c || c.error) return null;
  const note: { coldMs?: number; warmMs?: number } = {};
  if (c.coldTotal?.n) note.coldMs = c.coldTotal.avgMs;
  if (c.warmTotal?.n) note.warmMs = c.warmTotal.avgMs;
  return note.coldMs == null && note.warmMs == null ? null : note;
}

/** How long the "Timed N of N" line stays after Time all ends. It is news for a moment, not a fixture of the page. */
export const TIME_NOTE_MS = 15000;

/** The one line Time all leaves behind: how many rows were timed, which was slowest and by how much, and what could not be timed. */
export function timingNote(t: { done: number; failed: number; total: number; repeatOnly: number; why: string | null; slowest?: { text: string; ms: number } | null }): string {
  const left = t.total - t.done - t.failed;
  const parts = [`Timed ${t.done} of ${t.total}.`];
  if (t.slowest && t.done > 1) {
    const name = t.slowest.text.length > 48 ? t.slowest.text.slice(0, 47).trimEnd() + '…' : t.slowest.text;
    parts.push(`Slowest: ${name}, ${msWord(t.slowest.ms)}.`);
  } else if (t.slowest && t.done === 1) parts.push(`${msWord(t.slowest.ms)}.`);
  if (t.repeatOnly > 0) parts.push('First-run time is skipped on a shared server.');
  if (t.failed > 0) parts.push(`${t.failed} could not be timed${t.why ? `: ${t.why}` : ''}.`);
  if (left > 0) parts.push(`${left} left untimed.`);
  return parts.join(' ');
}

/** The person's own words for when a row was last run: "just now", "9 min ago", "yesterday". */
export function ago(iso: string, now = Date.now()): string {
  const t = Date.parse(iso);
  if (!Number.isFinite(t)) return '';
  const min = Math.max(0, Math.round((now - t) / 60000));
  if (min < 1) return 'just now';
  if (min < 60) return `${min} min ago`;
  const h = Math.round(min / 60);
  if (h < 24) return `${h} h ago`;
  const d = Math.round(h / 24);
  return d === 1 ? 'yesterday' : `${d} days ago`;
}

/** The number a row leads with: one value when the result is one value, else how many rows came back. A blank answer says so
 *  in the same place, never a zero (a zero would read as "there were none" when the filter simply matched nothing: K21). */
/** `name` is set when a hand query comes back as one row of several columns: the number is then one column's, and says which. */
export interface Figure { big: string; blank: boolean; name?: string; /** True when `big` is only how many rows came back (the folded row says it; the open answer leads with its chart). */ count?: boolean }
/** A column's name as a person says it: the engine's brackets and table prefix are not shown ("[Total Cost 2025]" is "Total Cost 2025", "'Customer'[City]" is "City"). */
export function plainColumnName(name: string | undefined | null): string | undefined {
  if (!name) return undefined;
  const m = /\[([^\]]+)\]\s*$/.exec(name.trim());
  return (m ? m[1] : name).trim() || undefined;
}

/** The last headline a row said, with the query that said it. A symbol is carried only while the query is the same (a count typed into a
 *  row that said "$" is not dollars: v-fix-hero26 r2). */
export interface SaidHeadline { dax: string; headline: string }
export function saidFor(said: SaidHeadline | undefined, r: { dax?: string | null }): string | null {
  return said && said.dax === (r.dax ?? '') ? said.headline : null;
}

/** A number run again with no measure format keeps the currency symbol its row already said ("$3,028,989.86" stays so, never "3,028,989.86"). */
export function keepSymbol(said: string | null | undefined, big: string): string {
  const kept = /(^|: )(-?)([$€£¥])\d/.exec(said ?? '');
  return kept && /^-?[\d,]+(\.\d+)?$/.test(big) ? big.replace(/^(-?)(\d)/, (_m, sign: string, digit: string) => sign + kept[3] + digit) : big;
}

/** A folded figure in two pieces: the label that may shorten ("Total Cost 2025:") and the number that never breaks (hero 27 P2). */
export function splitFigure(headline: string): { label: string; value: string } {
  const i = headline.lastIndexOf(': ');
  return i < 0 ? { label: '', value: headline } : { label: headline.slice(0, i + 1), value: headline.slice(i + 2) };
}

export function figureOf(res: ResultSet | null, config: PivotConfig | null, running = false): Figure {
  if (running) return { big: '', blank: false };
  if (!res) return { big: '', blank: false };
  const rows = res.rowCount ?? res.rows.length;
  if (!rows) return { big: 'No rows', blank: true };
  const nDims = config ? config.rows.length + config.cols.length : Math.max(0, res.columns.length - 1);
  if (rows === 1 && res.rows[0]) {
    let i = config ? nDims : res.columns.length - 1;
    // UAT 17 Oct, F57: a one-row query of several columns (the assistant's check: Total 2024, All years, Blank brand ...) led with its LAST column,
    // which was blank, so 36,111,492.86 showed as "Blank". A blank last column gives way to the first number, which says which it is. A whole
    // number under a column named for a year, a date or a key is a label, not the answer, so it never takes the lead.
    if (!config && res.columns.length > 1 && (res.rows[0][i] === null || res.rows[0][i] === undefined)) {
      const labelled = (c: number) => Number.isInteger(res.rows[0][c]) && /\b(year|month|quarter|date|key|id|code)\b/i.test(plainColumnName(res.columns[c]?.name) ?? '');
      const lead = res.rows[0].findIndex((x, c) => typeof x === 'number' && !labelled(c));
      if (lead >= 0) i = lead;
    }
    const v = res.rows[0][i];
    if (v === null || v === undefined) return { big: 'Blank', blank: true };
    // Hero 25 H4: with no fields behind it (a hand query) and several columns, a bare number from the last column reads as THE answer. It says which.
    const named = !config && res.columns.length > 1 ? { name: plainColumnName(res.columns[i]?.name) } : {};
    if (typeof v === 'number') return { big: formatOf(res.columns[i]?.format).format(v), blank: false, ...named };
    if (typeof v === 'string' && res.columns.length === 1) return { big: v, blank: false };
  }
  return { big: `${rows.toLocaleString('en-US')} ${rows === 1 ? 'row' : 'rows'}`, blank: false, count: true };
}

/** The small note the page sends for a row once it knows its number. Null when nothing changed, so the page sends nothing twice. */
export function noteFor(r: Rec, figure: Figure, dropped: string | null, tookMs: number | null, saidBefore?: string | null): { headline?: string; dropped?: string; tookMs?: number } | null {
  const note: { headline?: string; dropped?: string; tookMs?: number } = {};
  // A run again of a row that said "$" keeps it: a hand-run has no measure format to say it, so the row's own symbol stays (hero 26 N8).
  const big = keepSymbol(r.headline ?? saidBefore, figure.big);
  const said = figure.name ? `${figure.name}: ${big}` : big;
  if (figure.big && said !== (r.headline ?? '')) note.headline = said;
  if (dropped !== null && dropped !== (r.dropped ?? '')) note.dropped = dropped;
  if (tookMs !== null && Math.round(tookMs) !== Math.round(r.tookMs ?? -1)) note.tookMs = tookMs;
  return Object.keys(note).length ? note : null;
}

/** Hero 25 H1: a row's figure, speed and "dropped" line are written only by that row's own run. The rows on the page belong to a row only when
 *  the assistant did not make them and the query that made them is the row's own DAX (the engine clips very long DAX, so a clipped copy still counts). */
export function rowOwnsResult(row: Rec, producedBy: string, assistantShown: boolean): boolean {
  if (assistantShown) return false;
  const mine = squash(row.dax ?? '');
  if (!mine || !producedBy) return true;
  return sameDax(producedBy, mine);
}

const sameDax = (a: string, b: string) => !!a && !!b && (a === b || a.startsWith(b) || b.startsWith(a));

/** The row the assistant's own run_dax got (the engine keeps one per query), found by the DAX the page was told about. Null when the model keeps no history. */
export function assistantRowFor(all: Rec[], dax: string | null | undefined): Rec | null {
  const want = squash(dax ?? '');
  if (!want) return null;
  const hit = all.filter((r) => r.origin === 'agent' && r.kind === 'dax' && sameDax(squash(r.dax ?? ''), want)).sort(newestFirst)[0];
  return hit ?? null;
}

/** True when two rows ask the same thing (the engine's own key), so the page does not offer a row twice. */
export const sameQuestion = (a: Rec, b: Rec) => squash(rowText(a)).toLowerCase() === squash(rowText(b)).toLowerCase();

// ---- the assistant's checks (UAT 17 Oct, P42b + F57) ----

/** A query the assistant ran only to check something (a measure tried inside DEFINE, a one-row cross-check, a list of values), not an answer
 *  for the person. Judged by the shape of the DAX, since the engine keeps no purpose for a run. */
export const PROBE_DAX = /DEFINE\s+MEASURE|EVALUATE\s+UNION\s*\(\s*SELECTCOLUMNS/i;
const ROW_CALL = /EVALUATE\s+ROW\s*\(/i;
/** How many name and value pairs the first ROW( ... ) call holds, counting top-level commas only (quotes and brackets are skipped). */
function rowPairs(dax: string): number {
  const at = dax.search(ROW_CALL);
  if (at < 0) return 0;
  let i = dax.indexOf('(', at) + 1, depth = 1, commas = 0;
  for (; i < dax.length && depth > 0; i++) {
    const ch = dax[i];
    if (ch === '"') { for (i++; i < dax.length && !(dax[i] === '"' && dax[i + 1] !== '"'); i++) if (dax[i] === '"') i++; }
    else if (ch === "'") { for (i++; i < dax.length && dax[i] !== "'"; i++); }
    else if (ch === '(' || ch === '[' || ch === '{') depth++;
    else if (ch === ')' || ch === ']' || ch === '}') depth--;
    else if (ch === ',' && depth === 1) commas++;
  }
  return Math.floor((commas + 1) / 2);
}
/** A one-value ROW is the usual shape of a one-number answer (UAT 18 Oct: 39,137,964.25 folded under "checked 1 thing"), so it opens as an
 *  answer. Several values side by side (a total beside the sum of its parts) are a cross-check, so they fold with the other checks. */
export const isProbeDax = (dax: string | null | undefined): boolean => !!dax && (PROBE_DAX.test(dax) || rowPairs(dax) >= 2);
/** A row of the assistant's that only checked something. A pinned row is the person's keep, so it is never folded. */
export const isProbe = (r: Rec): boolean => r.origin === 'agent' && r.kind === 'dax' && !r.pinned && isProbeDax(r.dax);

/** The rows to draw: every answer row in place, and the assistant's checks held apart (folded into one line), except the open row. */
export function splitChecks(shown: Rec[], openId: string | null): { rows: Rec[]; checks: Rec[] } {
  const checks = shown.filter((r) => isProbe(r) && r.id !== openId);
  return { rows: shown.filter((r) => !checks.includes(r)), checks };
}
/** The one line a fold of the assistant's checks says. */
export const checksLine = (n: number): string => `Your assistant checked ${n === 1 ? '1 thing' : `${n} things`}`;

// ---- what a kept row says when it opens (UAT 17 Oct, P29 + F58) ----

/** A headline that is only how many rows came back ("24 rows"): the rows are never kept, so it has no figure to lead with. */
export const isRowCount = (headline: string | null | undefined): boolean => !!headline && /^[\d,]+ rows?$/.test(headline);

/** A row that ran before keeps its number and when it ran; only a row that never ran says "Not run yet". */
export function keptLine(r: Rec, now = Date.now()): { ran: boolean; text: string } {
  const ran = !!r.headline || r.rowCount != null || r.tookMs != null;
  return ran ? { ran, text: `Last run ${ago(r.askedAt, now)}.` } : { ran, text: 'Not run yet.' };
}

/** What the yellow mark on a row says. The line is the engine's, and it is either something it assumed and applied ("Counting customers with 2 or
 *  more orders.") or something it left out ("Year is not filtered."). Calling an applied reading "Not applied" was wrong (pro UAT 18 Oct, N6). */
export function markLabel(line: string): string {
  return /\bnot\b/i.test(line) ? `Not applied: ${line}` : `Assumed: ${line}`;
}
