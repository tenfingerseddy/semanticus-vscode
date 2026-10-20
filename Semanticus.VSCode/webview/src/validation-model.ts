// Validation C (1.6): the pure half. Turns the engine's check results into the shared visual set's data shape, names the
// visuals that fit each one and the rules' pick among them, and works out the 8-run history. No React, no engine calls:
// tests/validation-view.test.mjs runs this file as it is. Every number shown comes from the engine's own result; nothing
// here invents one. The page asks Jev (checks.visual_kind) which of `fits` to draw, and falls back to `rulesPick`.
import type { Data, Fmt, Kind, Row } from './visuals';

/** The groups a person reads down the page, by the question each one answers (Kane picked Validation C, 15 Oct). */
export const GROUPS = [
  { id: 'sources', question: 'Do my numbers match their sources?' },
  { id: 'changes', question: 'Did my changes move anything?' },
  { id: 'trusted', question: 'Do the trusted answers still hold?' },
  { id: 'whole', question: 'Is the data whole?' },
] as const;
export type GroupId = (typeof GROUPS)[number]['id'];

/** Which group a saved check lives in: a compare-with-source check answers "match their sources", a trusted answer the third. */
export const groupOfKind = (kind?: string): GroupId => (kind === 'measureValue' ? 'trusted' : 'sources');

/** The most checks one Jev call may name (the engine refuses more). */
export const MAX_VISUAL_CHECKS = 40;

// ---- numbers ---------------------------------------------------------------------------------------------------------

/** "2,646,905", "$1,204.50", "41.8%", "(1,234)" to a number; anything else is not a number. A percent becomes a share. */
export function parseNumber(text?: string | number | null): number | null {
  if (text === null || text === undefined) return null;
  if (typeof text === 'number') return Number.isFinite(text) ? text : null;
  let s = text.trim();
  if (!s) return null;
  const negative = /^\(.*\)$/.test(s) || s.startsWith('-') || s.startsWith('−');
  s = s.replace(/^[(\-−+]+|\)$/g, '').replace(/[$£€\s]/g, '').replace(/,/g, '');
  const percent = s.endsWith('%');
  if (percent) s = s.slice(0, -1);
  if (!/^\d+(\.\d+)?$/.test(s)) return null;
  const n = Number(s) / (percent ? 100 : 1);
  return negative ? -n : n;
}

/** One format for a set of numbers: exact shares for a percent, whole numbers when every value is whole, up to six places otherwise. */
export function fmtFor(values: (number | null | undefined)[], percent = false): Fmt {
  if (percent) return 'pctx';
  const known = values.filter((v): v is number => typeof v === 'number');
  return known.every((v) => Number.isInteger(v)) ? 'int' : 'dec';
}

// ---- what fits ------------------------------------------------------------------------------------------------------

/** Every visual the page can draw for a check. A subset fits each check; Jev picks one of them, never another. */
export const VISUAL_KINDS: readonly Kind[] = ['total', 'bars', 'line', 'variance', 'dumbbell', 'bullet', 'slope', 'multiples', 'waterfall', 'sparktable', 'heat', 'table'];

export interface CheckVisual {
  data: Data | null;
  fits: Kind[];
  /** The rules' pick, always one of `fits`. Null only when there is nothing to draw. */
  rulesPick: Kind | null;
}
export const NO_VISUAL: CheckVisual = { data: null, fits: [], rulesPick: null };

/** The one sentence under a visual, in the rules' voice. The engine writes the sentence for a pick it returns; this is for the rules' own pick. */
export function rulesReason(kind: Kind): string {
  switch (kind) {
    case 'total': return 'One number against the one it should match.';
    case 'bullet': return 'One number against its allowed range.';
    case 'dumbbell': return 'Two numbers for each place.';
    case 'variance': return 'The gap is what matters here.';
    case 'bars': return 'Bars make the sizes easy to compare.';
    case 'multiples': return 'Each check over the last runs, on one scale.';
    case 'slope': return 'Two runs for each check.';
    case 'sparktable': return 'A small line beside each number.';
    case 'line': return 'Numbers over time read best as a line.';
    case 'waterfall': return 'Steps that add up to the end value.';
    case 'heat': return 'A grid of numbers, shaded by size.';
    default: return 'The rows themselves are the clearest way to read this.';
  }
}

export interface ComparisonRowLike { context: string; sql?: number | null; dax?: number | null; delta?: number | null; verdict: string; grandTotal?: boolean }
export interface OutcomeShape {
  expected?: string; actual?: string; difference?: number; rows?: ComparisonRowLike[]; rowsTotal?: number;
  mismatches?: number; matches?: number; missing?: boolean; verdict?: string;
}
export interface CheckVisualOptions { title: string; sourceLabel: string; toleranceRelative?: number; trusted: boolean; expectedText?: string }

const isDiffer = (verdict: string) => verdict === 'Mismatch' || verdict === 'Fail';
/** A row the engine could not judge (blank against none, a number it cannot represent). It is neither a match nor a difference. */
const isUnknown = (verdict: string) => !isDiffer(verdict) && verdict !== 'Match' && verdict !== 'Pass';

/** One check's result as visual data: a single value against its answer, or one pair per place the engine compared. */
export function checkVisual(outcome: OutcomeShape | undefined | null, o: CheckVisualOptions): CheckVisual {
  if (!outcome || outcome.missing) return NO_VISUAL;
  const rows = (outcome.rows ?? []).filter((r) => r.sql != null || r.dax != null);
  const sourceLabel = o.trusted ? 'The answer you trust' : o.sourceLabel.charAt(0).toUpperCase() + o.sourceLabel.slice(1);
  if (!o.trusted && rows.length >= 2) {
    const ordered = [...rows].sort((a, b) => Number(isDiffer(b.verdict)) - Number(isDiffer(a.verdict)));
    // Whether a row differs is the engine's verdict for it, never a gap share worked out here: an absolute allowance is not a share.
    // The check's own relative allowance is kept only to draw its band.
    const data: Data = {
      rows: ordered.map<Row>((r) => ({
        label: r.context, v: r.dax ?? null, v2: r.sql ?? null, differs: isDiffer(r.verdict),
        note: r.dax == null ? 'blank' : r.sql == null ? 'no value' : isUnknown(r.verdict) ? 'could not check' : undefined,
      })),
      vLabel: 'The model', v2Label: sourceLabel, fmt: fmtFor(ordered.flatMap((r) => [r.dax, r.sql])), tolerance: o.toleranceRelative, dimLabel: 'Where',
    };
    // Bars draw only the model's numbers, so two sets that differ must never be offered as Bars: the gap would not show.
    const fits: Kind[] = withoutHiddenGap(['dumbbell', 'variance', 'bars', 'table'], data.rows);
    return { data, fits, rulesPick: rows.length > 12 ? 'variance' : 'dumbbell' };
  }
  // One value against one answer: a trusted answer, a compare-with-source check that kept one comparison or none.
  const only = rows.length === 1 ? rows[0] : null;
  const pctText = /%/.test(`${outcome.expected ?? ''}${outcome.actual ?? ''}`);
  const actual = only ? (only.dax ?? null) : parseNumber(outcome.actual);
  const expected = only ? (only.sql ?? null) : parseNumber(outcome.expected ?? o.expectedText);
  if (actual === null || expected === null) return NO_VISUAL;
  const fmt = fmtFor([actual, expected], !only && pctText);
  const differs = isDiffer(only?.verdict ?? outcome.verdict ?? '');
  const data: Data = {
    rows: [{ label: readableTitle(o.title), v: actual, v2: expected, differs }],
    vLabel: 'The model', v2Label: sourceLabel, fmt, tolerance: o.toleranceRelative, dimLabel: 'Check',
    total: { value: actual, label: readableTitle(o.title), prior: expected, priorLabel: o.trusted ? 'Trusted answer' : sourceLabel, mark: 'pair' },
  };
  return { data, fits: ['total', 'bullet', 'dumbbell', 'table'], rulesPick: 'total' };
}

/** Bars show one number per row, so when any row's two numbers differ the gap is hidden: those sets are not offered Bars. */
function withoutHiddenGap(fits: Kind[], rows: Row[]): Kind[] {
  return rows.some((r) => r.differs) ? fits.filter((k) => k !== 'bars') : fits;
}

export interface RelationshipLike { name: string; manyTable: string; oneTable: string; manyRowCount?: number; refVerdict: string; refCount?: number }
/** Relationships as the share of rows that match their lookup table. A relationship with orphan rows is marked and says how many,
 *  so 5 in 2,000,000 never reads as all of them. A relationship that could not be checked has no bar. */
export function relationshipVisual(rels: RelationshipLike[]): CheckVisual {
  if (rels.length === 0) return NO_VISUAL;
  const rows = rels.map<Row>((r) => {
    const label = `${r.manyTable} to ${r.oneTable}`;
    if (r.refVerdict === 'Pass') return { label, v: 1, v2: 1, differs: false };
    if (r.refVerdict === 'Fail') {
      const orphans = r.refCount != null && r.refCount > 0 ? `${r.refCount.toLocaleString('en-US')} not matched` : 'rows not matched';
      const share = r.refCount != null && r.manyRowCount ? Math.max(0, 1 - r.refCount / r.manyRowCount) : null;
      return { label, v: share, v2: 1, differs: true, diffText: orphans, note: share === null ? orphans : undefined };
    }
    return { label, v: null, v2: 1, note: 'could not check' };
  });
  if (rows.every((r) => r.v === null && !r.differs)) return NO_VISUAL;
  const data: Data = { rows, vLabel: 'Rows that match', v2Label: 'All rows', fmt: 'pctx', dimLabel: 'Relationship' };
  const hidden = rows.some((r) => r.differs);
  return { data, fits: withoutHiddenGap(['bars', 'bullet', 'dumbbell', 'table'], rows), rulesPick: hidden ? 'dumbbell' : 'bars' };
}

export interface CountLike { table: string; model?: number | null; source?: number | null }
/** Table row counts: the model's count and the source's count for each mapped table. Only counted tables are drawn. */
export function countsVisual(counts: CountLike[]): CheckVisual {
  const rows = counts.filter((c) => c.model != null && c.source != null)
    .map<Row>((c) => ({ label: c.table, v: c.model ?? null, v2: c.source ?? null, differs: c.model !== c.source }));
  if (rows.length === 0) return NO_VISUAL;
  const differs = rows.some((r) => r.v !== r.v2);
  const data: Data = { rows, vLabel: 'The model', v2Label: 'The source', fmt: 'rows', dimLabel: 'Table', zero: true };
  // The tables differ in size by orders of magnitude, so a shared scale hides every gap: the gaps are drawn when there are any.
  return { data, fits: withoutHiddenGap(['variance', 'bars', 'dumbbell', 'table'], rows), rulesPick: differs ? 'variance' : 'bars' };
}

// ---- eight runs -----------------------------------------------------------------------------------------------------

export interface RecordedContextLike { verdict: string }
export interface RecordedCheckLike {
  kind: string; name: string; check?: string; verdict: string; contexts?: RecordedContextLike[];
  /** The run's real totals: every comparison, and how many of them differed. The kept contexts are only the worst few. */
  contextsTotal?: number; mismatches?: number;
}
export interface RecordedRunLike { runId: string; when: string; checks: RecordedCheckLike[] }

/** How far off one recorded check was, as a share from 0 to 1. An unknown is not a number, so it has none. */
export function howFarOff(check: RecordedCheckLike): number | null {
  const contexts = check.contexts ?? [];
  const total = check.contextsTotal ?? 0;
  // The engine keeps only the worst rows, so a share worked out from them is wrong whenever it dropped some: use the run's totals.
  if (total > contexts.length) {
    if (check.mismatches != null) return Math.min(1, check.mismatches / total);
    return check.verdict === 'Pass' ? 0 : null;
  }
  if (contexts.length > 0) {
    const bad = contexts.filter((c) => isDiffer(c.verdict)).length;
    return bad / contexts.length;
  }
  if (check.verdict === 'Pass') return 0;
  if (check.verdict === 'Fail') return 1;
  if (check.verdict === 'Suspect') return 0.5;
  return null;
}

const familyKey = (c: RecordedCheckLike) => (c.kind === 'relationship' ? 'Relationships' : c.kind === 'tableRowCount' ? 'Table row counts' : c.name);

/** One value per member per run, oldest to newest. A relationship or a table count joins its family: the run's share of failures. */
export function historySeries(runsNewestFirst: RecordedRunLike[], limit = 8): { label: string; series: number[] }[] {
  const runs = [...runsNewestFirst].slice(0, limit).reverse();
  const byMember = new Map<string, number[]>();
  for (const run of runs) {
    const seen = new Map<string, number[]>();
    for (const check of run.checks) {
      const value = howFarOff(check);
      if (value === null) continue;
      const key = familyKey(check);
      seen.set(key, [...(seen.get(key) ?? []), value]);
    }
    for (const [key, values] of seen) byMember.set(key, [...(byMember.get(key) ?? []), values.reduce((a, b) => a + b, 0) / values.length]);
  }
  return [...byMember].map(([label, series]) => ({ label, series })).filter((m) => m.series.length >= 2);
}

/** The eight-run card's data: one small multiple per member, on one shared scale (how far off, as a share). */
export function historyVisual(runsNewestFirst: RecordedRunLike[], limit = 8): CheckVisual {
  const members = historySeries(runsNewestFirst, limit)
    .sort((a, b) => (b.series[b.series.length - 1] > 0 ? 1 : 0) - (a.series[a.series.length - 1] > 0 ? 1 : 0) || a.label.localeCompare(b.label))
    .slice(0, 12);
  if (members.length === 0) return NO_VISUAL;
  const data: Data = {
    rows: members.map<Row>((m) => ({
      label: m.label, v: m.series[m.series.length - 1], v2: m.series.length > 1 ? m.series[m.series.length - 2] : undefined,
      series: m.series, note: m.series[m.series.length - 1] > 0 ? 'Differs now' : undefined,
    })),
    vLabel: 'Off now', v2Label: 'Off before', fmt: 'pctx', dimLabel: 'Check',
  };
  return { data, fits: ['multiples', 'sparktable'], rulesPick: 'multiples' };
}

/** "Did my changes move anything?": the same members in the newest two recorded runs, now beside the run before. */
export function changesVisual(runsNewestFirst: RecordedRunLike[]): CheckVisual {
  if (runsNewestFirst.length < 2) return NO_VISUAL;
  const raw = (run: RecordedRunLike) => {
    const out = new Map<string, number[]>();
    for (const check of run.checks) {
      const value = howFarOff(check);
      if (value !== null) out.set(familyKey(check), [...(out.get(familyKey(check)) ?? []), value]);
    }
    return new Map([...out].map(([k, v]) => [k, v.reduce((a, b) => a + b, 0) / v.length]));
  };
  const a = raw(runsNewestFirst[0]), b = raw(runsNewestFirst[1]);
  const rows = [...a].filter(([k]) => b.has(k)).map<Row>(([label, v]) => ({ label, v, v2: b.get(label)! }))
    .sort((x, y) => Math.abs((y.v ?? 0) - (y.v2 ?? 0)) - Math.abs((x.v ?? 0) - (x.v2 ?? 0)) || x.label.localeCompare(y.label));
  if (rows.length === 0) return NO_VISUAL;
  const data: Data = { rows, vLabel: 'This run', v2Label: 'Run before', fmt: 'pctx', dimLabel: 'Check' };
  return { data, fits: ['dumbbell', 'slope', 'variance', 'table'], rulesPick: 'dumbbell' };
}

/** How many of the members moved between the two runs, for the group's one-line verdict. */
export function movedCount(v: CheckVisual): number {
  return (v.data?.rows ?? []).filter((r) => (r.v ?? 0) !== (r.v2 ?? 0)).length;
}

// ---- Jev's pick -----------------------------------------------------------------------------------------------------

export interface VisualAskCheck {
  id: string; title: string; measure: string; kindWord: string; status: 'pass' | 'differs' | 'off' | 'could_not_check';
  rows: number; hasPrior: boolean; runs: number; hasSplit: boolean; fits: string[]; rulesPick: string;
}
export interface VisualPick { id: string; kind: string; by: 'jev' | 'rules'; reason?: string; confidence?: number; note?: string }
export interface VisualPicks { picks?: VisualPick[]; note?: string }

/** One call per run names every check that has something to draw, and only those. Words and counts, never a number. */
export function visualAsk(checks: VisualAskCheck[]): { checks: VisualAskCheck[] } | null {
  const named = checks.filter((c) => c.fits.length > 0 && c.rulesPick).slice(0, MAX_VISUAL_CHECKS);
  return named.length > 0 ? { checks: named } : null;
}

/** The pick the page draws for one check: Jev's when Jev made it and it fits, the rules' otherwise, with the sentence to show. */
export function chosenVisual(c: CheckVisual, pick: VisualPick | undefined): { kind: Kind | null; by: 'jev' | 'rules'; reason: string } {
  if (!c.rulesPick) return { kind: null, by: 'rules', reason: '' };
  if (pick && pick.by === 'jev' && (c.fits as string[]).includes(pick.kind)) {
    return { kind: pick.kind as Kind, by: 'jev', reason: pick.reason || rulesReason(pick.kind as Kind) };
  }
  return { kind: c.rulesPick, by: 'rules', reason: rulesReason(c.rulesPick) };
}

/** The one-line verdict beside a group's question, from the status words of the checks in it. */
export function groupVerdict(words: ('Pass' | 'Differs' | 'Could not check' | 'Off' | 'Not run')[]): string {
  const total = words.length;
  if (total === 0) return 'No checks yet';
  const bad = words.filter((w) => w === 'Differs').length;
  const off = words.filter((w) => w === 'Off').length;
  const unknown = words.filter((w) => w === 'Could not check').length;
  const notRun = words.filter((w) => w === 'Not run').length;
  if (bad) return `${bad} of ${total} differ`;
  if (unknown) return `${unknown} of ${total} could not check`;
  if (notRun === total) return total > 1 ? `${total} not run yet` : 'Not run yet';
  if (off) return `${total - off} pass, ${off} off`;
  return total > 1 ? `All ${total} pass` : 'Pass';
}

/** The status word the page's summary counts and the prototype's strip uses. */
export const statusOf = (chip: string): 'pass' | 'differs' | 'off' | 'could_not_check' =>
  chip === 'Pass' ? 'pass' : chip === 'Differs' ? 'differs' : chip === 'Off' ? 'off' : 'could_not_check';

/** A figure the engine sent as plain digits ("291894", "1000.5") in the grouped form a person reads ("291,894"). Anything else is left as it came. */
export function plainFigure(text: string | null | undefined): string {
  if (text == null) return '';
  const t = text.trim();
  if (!/^-?\d+(\.\d+)?$/.test(t)) return text;
  const [whole, frac] = t.split('.');
  const grouped = whole.replace(/\B(?=(\d{3})+(?!\d))/g, ',');
  return frac ? `${grouped}.${frac}` : grouped;
}

/** The engine's activity sentences carry object references ("measure:Sales/Net Sales") and status words ("(Reconciled)"). A person reads names and plain verdicts. */
export function plainToolNote(text: string | null | undefined): string {
  let t = (text ?? '').trim();
  const reconciled = /^Reconciled '(?:measure:)?(?:[^'/]*\/)?([^']+)' vs SQL \((\w+)\)$/.exec(t);
  if (reconciled) {
    const verdict = reconciled[2] === 'Reconciled' ? 'they match' : reconciled[2] === 'Mismatch' ? 'they differ' : 'could not check';
    return `Compared ${reconciled[1]} with its source: ${verdict}.`;
  }
  t = t.replace(/'((?:measure|column|table):[^']+)'/g, '$1');   // a quoted reference loses its quotes with its prefix
  t = t.replace(/\bmeasure:(?:[^/]+\/)?(.+?)(?=$| [a-z])/g, (_all, name: string) => name);
  t = t.replace(/\bcolumn:([^\s/]+)\/(.+?)(?=$| [a-z])/g, (_all, table: string, column: string) => `${table} ${column}`);
  return t.replace(/\btable:(\S+)/g, (_all, table: string) => table);
}

export interface Notice { key: string; tone: 'ok' | 'warn' | 'bad'; text: string }
/** One note at most: the first of these, in the order the caller gives them, that has something to say. */
export function oneNotice(candidates: (Notice | null | undefined | false)[]): Notice | null {
  for (const c of candidates) if (c && c.text.trim()) return c;
  return null;
}

/** A title the engine wrote with bare digits ("Net Sales totals 75000000") with the separators a person reads. A year stays a year. */
export function readableTitle(title: string | null | undefined): string {
  return (title ?? '').replace(/(?<![\d.,])\d{4,}(?:\.\d+)?(?![\d,])/g, (digits) => {
    const whole = digits.split('.')[0];
    if (!digits.includes('.') && whole.length === 4 && Number(whole) >= 1900 && Number(whole) <= 2100) return digits;
    return plainFigure(digits);
  });
}

/** Every check is in exactly one state, so the parts below always add up to the checks. A check that is turned off is not counted. */
export interface BoardTally { total: number; passed: number; differs: number; cannot: number; notRun: number; accepted: number }
export function boardTally(chips: string[]): BoardTally {
  const live = chips.filter((c) => c !== 'Off');
  const count = (word: string) => live.filter((c) => c === word).length;
  return { total: live.length, passed: count('Pass'), differs: count('Differs'), cannot: count('Could not check'), notRun: count('Not run'), accepted: count('Accepted') };
}

/** The big line: how many of ALL the checks pass. A check that has not run is still a check, so it stays in the total. */
export function boardHeadline(t: BoardTally): { big: string; rest: string } {
  const ran = t.passed + t.differs + t.cannot + t.accepted > 0;
  return ran
    ? { big: t.passed.toLocaleString('en-US'), rest: ` of ${t.total.toLocaleString('en-US')} pass` }
    : { big: t.total.toLocaleString('en-US'), rest: ` ${t.total === 1 ? 'check' : 'checks'}, not run yet` };
}

/** The line under it: each state that is not a pass, once. Before anything has run it says nothing, because the headline already does. */
export function boardParts(t: BoardTally): { kind: 'differs' | 'cannot' | 'accepted' | 'notRun' | 'ok'; n: number; text: string }[] {
  if (t.passed + t.differs + t.cannot + t.accepted === 0) return [];
  const parts: { kind: 'differs' | 'cannot' | 'accepted' | 'notRun' | 'ok'; n: number; text: string }[] = [];
  if (t.differs > 0) parts.push({ kind: 'differs', n: t.differs, text: `${t.differs.toLocaleString('en-US')} ${t.differs === 1 ? 'differs' : 'differ'}` });
  if (t.cannot > 0) parts.push({ kind: 'cannot', n: t.cannot, text: `${t.cannot.toLocaleString('en-US')} could not check` });
  // An accepted difference is named on its own, never inside "pass": "4 of 5 pass · 1 accepted".
  if (t.accepted > 0) parts.push({ kind: 'accepted', n: t.accepted, text: `${t.accepted.toLocaleString('en-US')} accepted` });
  if (t.notRun > 0) parts.push({ kind: 'notRun', n: t.notRun, text: `${t.notRun.toLocaleString('en-US')} not run yet` });
  if (parts.length === 0) parts.push({ kind: 'ok', n: 0, text: 'Nothing needs a look' });
  return parts;
}

/** Run checks adds the two model checks to the person's own. One line says so, so the count is never a surprise. Null when neither is in the run. */
export function quickChecksNote(relationships: boolean, tableCounts: boolean): string | null {
  if (relationships && tableCounts) return 'Two quick model checks ran too: Relationships and Table row counts.';
  if (relationships) return 'A quick model check ran too: Relationships.';
  if (tableCounts) return 'A quick model check ran too: Table row counts.';
  return null;
}

/** Why the Run beside "N not run yet" cannot do anything, in one sentence; null when it can run them. Row counts are part of Run checks. */
export function notRunRunWhy(items: { family?: 'relationships' | 'rowcounts'; row?: unknown }[], liveModel: boolean): string | null {
  if (items.length === 0) return null;
  if (!liveModel) return 'There is no live test model to ask. Connect a test model, then run the checks.';
  return null;
}
