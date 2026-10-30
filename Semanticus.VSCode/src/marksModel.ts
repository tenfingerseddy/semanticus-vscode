// The words and order for a measure's marks. Properties, the mark cards, the run plan, the Checks page and
// the Model tree row all read from here, so one mark never reads two ways (d1 spec: five verdict states, marks that do not apply are hidden).

export interface MarkCell { context?: string; before?: string | null; after?: string | null; beforeText?: string | null; afterText?: string | null; gapText?: string | null }
export interface MarkContext { id: string; column?: { table: string; name: string } | null; shape?: string; role?: string; reason?: string; expectedBehaviour?: string }
export interface MarkTiming { context?: string; medianMs: number; minMs: number; maxMs: number; coldMs?: number | null; beforeMedianMs?: number | null; noiseBandMs?: number; regression?: boolean }
export interface TotalReading { rule: string; type: string; confidence: number; fitScore?: number | null; expected: boolean; source: string }
export interface Mark {
  name: string; state: string; sentence?: string; cells?: MarkCell[]; action?: string; lastRan?: string | null;
  timings?: MarkTiming[]; chosenContexts?: MarkContext[]; total?: TotalReading | null;
  beforeFormatString?: string | null; afterFormatString?: string | null;
  /** Equivalence only: the formula before and after the edit, so the detail can show the edit. */
  beforeFormula?: string | null; afterFormula?: string | null;
  /** Equivalence only: the run's hash of the measure and every measure it uses, as compared. Keep holds by it. */
  inputs?: string | null;
  /** Why the mark reads as it does, from the engine (no_sql, kept, could_not_run, filters_lost, totals_unclear, ...). */
  reasonCode?: string | null;
  /** The actions the engine offers: fix_formula, on_purpose, not_on_purpose, expected. */
  actionCodes?: string[];
  /** Set by the engine's check state: does the mark apply, is a run still producing it, and its verdict. */
  applies?: boolean; pending?: boolean; verdict?: State | null;
  /** While pending: what the run is doing for this mark ("Timing this measure: Waiting for 2 other checks first."). */
  progress?: string | null;
}
export interface MarksResult { measureRef: string; revision: number; running: boolean; version?: number; lastChecked?: string | null; includePerformanceForMeasure?: boolean; performanceSettings?: { slowThresholdMs?: number } | null; marks: Mark[] }
export interface ScoreMatch { score: number | null; level: string; label: string; reason?: string }
export interface RunPick { measureRef: string; inputs?: string; group: string; reason: string }
export interface RunPlanView { id: string; totalMeasures: number; batchSize: number; estimatedSeconds: number; sentence: string; picks: RunPick[]; includePerformance?: boolean; firstRun?: boolean }
/** The engine's one check state of a measure (getMeasureCheckCache, the checks/didChange push, a run's results). */
export interface CheckCache { measureRef: string; lastChecked?: string | null; stale?: boolean; matchScore?: number | null; marks?: Mark[]; cheap?: Mark[];
  version?: number; running?: boolean; keep?: { inputs?: string | null; at?: string } | null; verdict?: State | null }

/** Kane's order: the same seven, in the same place, on every surface. Alignment is the Description score, not a mark. */
export const MARK_ORDER = ['Totals', 'Filters', 'SQL', 'Trusted number', 'Equivalence', 'Contexts', 'Performance'] as const;

/** One verdict vocabulary on every surface (d1 spec section 2). Worst first. */
export type State = 'broken' | 'review' | 'unknown' | 'overridden' | 'verified';
export const STATE_ORDER: State[] = ['broken', 'review', 'unknown', 'overridden', 'verified'];
export const GLYPH: Record<State, string> = { broken: '✗', review: '▲', unknown: '○', overridden: '⊘', verified: '✓' };
export const WORD: Record<State, string> = { broken: 'Broken', review: 'Needs review', unknown: 'Unknown', overridden: 'Overridden', verified: 'Verified' };

// A cause the person can act on keeps its own line; only service-side causes are hidden.
// fx29b: a never-saved model (E), permission moved mid-score (G) and permission given after the line was written (F) each have their own line.
const ACTIONABLE_LINES = 'Scoring needs a Pro plan|Scoring is off for this model|The scoring allowance is used up for now|Pro advice is off for this model|Save the model or open it live to score it|Pro advice changed while this was being scored\\. Check again to score it|Pro advice is on now\\. Check again to score it';
export const ACTIONABLE = new RegExp('(' + ACTIONABLE_LINES + ')\\.$');
/** Pro advice (permission to send this model's details) is off for the open model. Turn on beside the line fixes it (fx29 gap 1). */
export const ADVICE_OFF = 'Pro advice is off for this model.';
/** The one line the Checks page and the card show when scoring is off for a cause the person can act on. */
export function actionableReason(text: string | null | undefined): string | null {
  const t = (text ?? '').trim();
  return ACTIONABLE.test(t) ? t.replace(new RegExp('^.*?(' + ACTIONABLE_LINES + ')\\.$'), '$1.') : null;
}
/** The card's line. What stops scoring now comes first: the engine's scoring state (the page read's `unscored`), which holds
 *  at open before any mark has run (wr1 F1). Then a line a mark carries (permission changed while it was scored). */
export function cardActionable(unscored: string | null | undefined, marks: Mark[] | null | undefined, match?: { reason?: string } | null): string | null {
  return actionableReason(unscored) ?? (marks ?? []).map(m => actionableReason(m.name === 'Totals' ? m.sentence : '')).find(Boolean)
    ?? actionableReason(match?.reason) ?? null;
}
/** The Checks page's line, from the same scoring state, then the measures' own lines. `adviceOn`: Turn on said yes here, so the
 *  advice line goes before the page is read again. */
export function checksActionable(unscored: string | null | undefined, rows: { marks?: Mark[] | null; match?: { reason?: string } | null }[], adviceOn: boolean): string | null {
  return [actionableReason(unscored), ...rows.flatMap(r => [actionableReason(r.match?.reason), ...(r.marks ?? []).filter(m => m.name === 'Totals').map(m => actionableReason(m.sentence))])]
    .find(line => line && !(adviceOn && line === ADVICE_OFF)) ?? null;
}

export function orderMarks(marks: Mark[] | null | undefined): Mark[] {
  return MARK_ORDER.map(name => (marks ?? []).find(m => m.name === name) ?? { name, state: 'not run' });
}

/** Does this mark apply to the measure? The engine says (MeasureCheckVerdict). A mark from an engine that does not say
 *  applies only when it has a result. A mark that does not apply is never shown and never counts. */
export function applies(mark: Mark): boolean {
  return mark.applies ?? ['passed', 'suspect', 'info', 'unknown'].includes(mark.state);
}

/** The verdict of one mark, or null when it is hidden (does not apply, or has no result yet). The engine sets it; a mark
 *  without one (an older engine, a test) reads from its state alone. `measureRan` is kept for the callers' shape. */
export function verdictOf(mark: Mark, measureRan = false): State | null {
  void measureRan;
  if (!applies(mark)) return null;
  if (mark.verdict !== undefined) return mark.verdict ?? null;
  if (mark.name === 'Totals' && mark.total?.expected && (mark.state === 'passed' || mark.state === 'info')) return 'overridden';
  if (mark.state === 'passed') return 'verified';
  if (mark.state === 'info') return mark.name === 'Filters' ? 'review' : null;
  if (mark.state === 'suspect') return mark.name === 'SQL' || mark.name === 'Trusted number' ? 'broken' : 'review';
  return mark.state === 'unknown' ? 'unknown' : null;
}

/** The marks of a measure that apply and ran, in the fixed order, with their verdicts. `checked`: the measure was in a run, so a mark with its own failure reason is Unknown. */
export function shownMarks(marks: Mark[] | null | undefined, checked = false): { mark: Mark; state: State }[] {
  void checked;
  return orderMarks(marks).flatMap(mark => { const state = verdictOf(mark); return state ? [{ mark, state }] : []; });
}

/** Did one of the seven marks run? The cheap pass a run starts with (Parse, Lint, Orphan keys, Description match) stamps every
 *  measure before the run reaches it; those are not marks, so a measure only waiting is not checked yet (rc28 tester gap 2). */
const ranAny = (marks: Mark[] | null | undefined) => (marks ?? []).some(m => m.lastRan && (MARK_ORDER as readonly string[]).includes(m.name));

/** Why a measure has no verdict yet (gap 2 of the d1 tester). `checked`: the measure was in a run, but no mark applies to it. */
export function notCheckedReason(marks: Mark[] | null | undefined, checked = false): string {
  if (!checked && !ranAny(marks)) return 'no run has included it';
  // The engine's own reason why Filters or Contexts does not apply, as words (not a mark still being produced).
  const own = orderMarks(marks).filter(m => (m.name === 'Filters' || m.name === 'Contexts') && m.reasonCode && m.reasonCode !== 'waiting' && !m.pending)
    .map(m => (m.sentence ?? '').trim()).find(Boolean);
  const clause = own ? own.replace(/[.!?]$/, '').split(/,\s*so\b|;/)[0].trim() : '';
  return clause ? clause[0].toLowerCase() + clause.slice(1) : 'no check fits this measure yet';
}

/** The newest check state per measure. A record older than the one held is dropped: the one rule that replaces the late-read
 *  guards. Reads, pushes and run results all go through it. A record without a version (an older engine) replaces its measure.
 *  Returns null when nothing changed. */
export function acceptStates(held: ReadonlyMap<string, CheckCache>, incoming: unknown): Map<string, CheckCache> | null {
  const list = Array.isArray(incoming) ? incoming : Array.isArray((incoming as { states?: unknown })?.states) ? (incoming as { states: unknown[] }).states : [];
  let next: Map<string, CheckCache> | null = null;
  for (const raw of list) {
    const state = raw as CheckCache;
    if (!state || typeof state.measureRef !== 'string') continue;
    const old = (next ?? held).get(state.measureRef);
    if (old === state || (old?.version != null && state.version != null && state.version < old.version)) continue;
    next ??= new Map(held);
    next.set(state.measureRef, state);
  }
  return next;
}

/** The newest check state of each measure, by measure ref. */
export type CheckStates = Map<string, CheckCache>;
export const noStates = (): CheckStates => new Map();

/** The marks a surface renders from a check state: its full marks once a check ran, else the cheap ones. */
export const stateMarks = (state: CheckCache | null | undefined): Mark[] | undefined => state?.marks?.length ? state.marks : state?.cheap;

function sentence(text: string | undefined): string {
  const t = (text ?? '').trim();
  if (!t) return '';
  const s = t[0].toUpperCase() + t.slice(1);
  return /[.!?]$/.test(s) ? s : s + '.';
}

// The model stays free of imports (said.test.mjs and the tree load it alone). The webview turns a timestamp in a sentence into a date in words.
const tidy = (text: string): string => text;
const article = (word: string) => /^[aeiou]/i.test(word) ? 'an' : 'a';
/** "recalc (recomputed at the total)" reads "recalculation"; the brackets are engine detail. */
export function totalType(mark: Mark): string {
  const t = (mark.total?.type ?? '').replace(/\s*\(.*\)\s*$/, '').trim();
  return t === 'recalc' ? 'recalculation' : t || 'sum';
}
const aType = (mark: Mark) => { const t = totalType(mark); return `${article(t)} ${t}`; };

const number = (value: string | null | undefined): number | null => {
  const n = Number(String(value ?? '').replace(/[,$%\s]/g, ''));
  return String(value ?? '').trim() && Number.isFinite(n) ? n : null;
};
/** A gap with no format: whole above 100, else enough places to be non-zero, so 0.001 never reads "0". */
const amount = (n: number) => {
  const size = Math.abs(n);
  if (size >= 100) return Math.round(size).toLocaleString('en-US');
  if (Math.round(size * 100) !== 0) return String(Math.round(size * 100) / 100);
  return size.toLocaleString('en-US', { maximumSignificantDigits: 2, maximumFractionDigits: 20 });
};
/** "5 lower" or "12 higher": the gap between the number the person trusts and the measure. `bare`: the amount only ("differs from SQL by 5"). */
function gapWords(mark: Mark, bare = false): string | null {
  for (const c of mark.cells ?? []) {
    const before = number(c.before), after = number(c.after);
    if (before == null || after == null || before === after) continue;
    // The engine writes the gap in the measure's format (MarkNumberFormat.Gap); the fallbacks serve a cell with no gap text.
    const gap = c.gapText?.trim() || formattedGap(c) || amount(after - before);
    return bare ? gap : `${gap} ${after < before ? 'lower' : 'higher'}`;
  }
  return null;
}
/** A cell's value as the engine wrote it in the measure's format (MarkNumberFormat), else as checked. */
const cellValue = (cell: MarkCell | undefined): string => (cell?.afterText ?? cell?.after ?? '').trim();
// "$1,000.00", "-$5.00", "$-5.00", "($5.00)", "12.50%": an optional bracket or minus, the format's prefix, the digits, its suffix.
const FORMATTED = /^(\()?(-)?([^\d.()-]*)(-)?([\d,]*\.?\d+)([^\d()]*)(\))?$/;
function shown(text: string | null | undefined) {
  const m = FORMATTED.exec((text ?? '').trim());
  if (!m || !m[1] !== !m[7]) return null;
  const size = Number(m[5].replace(/,/g, ''));
  return { prefix: m[3], suffix: m[6], digits: m[5], value: m[1] || m[2] || m[4] ? -size : size };
}
/** The gap in the format both numbers show ("$628,370.00" and "-$5.00" differ by "$628,375.00"). null when the two texts
 *  do not share one plain format, or the gap rounds to nothing at its places: the raw gap says it then. */
function formattedGap(c: MarkCell): string | null {
  const a = shown(c.beforeText), b = shown(c.afterText);
  if (!a || !b || a.prefix !== b.prefix || a.suffix !== b.suffix) return null;
  const decimals = (b.digits.split('.')[1] ?? '').length;
  const gap = Math.abs(b.value - a.value);
  if (!Number.isFinite(gap) || Math.round(gap * 10 ** decimals) === 0) return null;
  const digits = gap.toLocaleString('en-US', { minimumFractionDigits: decimals, maximumFractionDigits: decimals, useGrouping: a.digits.includes(',') || b.digits.includes(',') });
  return `${b.prefix}${digits}${b.suffix}`;
}

export function ms(value: number | null | undefined): string {
  if (value == null || !Number.isFinite(value)) return '';
  return value >= 1000 ? `${(value / 1000).toFixed(1)} s` : `${Math.round(value)} ms`;
}
const seconds = (value: number) => `${Math.round(value / 100) / 10} s`.replace('.0 s', ' s');
const placeOf = (context: string | undefined) => (context ?? '').trim() && !/^grand total$/i.test((context ?? '').trim()) ? context!.trim() : 'Grand total';
function slowest(mark: Mark): MarkTiming | undefined { return [...(mark.timings ?? [])].sort((a, b) => b.medianMs - a.medianMs)[0]; }

/** Why a mark is not Verified: the words that follow "Needs review:" or "Broken:". */
export function reasonOf(mark: Mark, state: State): string { return tidy(reasonLine(mark, state)); }
function reasonLine(mark: Mark, state: State): string {
  const text = (mark.sentence ?? '').trim();
  switch (mark.name) {
    case 'Equivalence': {
      const n = mark.cells?.length ?? 0;
      return n ? `your edit moved ${n >= 6 ? '6 or more' : n} number${n === 1 ? '' : 's'}` : 'your edit moved numbers';
    }
    case 'Filters':
      if (mark.state === 'info') { const scope = /ignores (.+?) filters/i.exec(text)?.[1]; return scope ? `ignores ${scope} filters` : 'ignores some filters'; }
      return /no related key|no matching key|orphan|key/i.test(text) ? 'rows with no matching key' : 'a filter does not change it';
    case 'Contexts': return 'blank or error in some places';
    case 'Totals': return `the total does not fit ${aType(mark)}`;
    case 'Performance': {
      if (mark.timings?.some(t => t.regression)) return 'slower after your edit';
      const t = slowest(mark);
      return t ? `slow, ${ms(t.medianMs)} at ${placeOf(t.context)}` : 'slow';
    }
    case 'SQL': return state === 'broken' ? (gapWords(mark, true) ? `differs from SQL by ${gapWords(mark, true)}` : 'differs from SQL') : text;
    case 'Trusted number': return state === 'broken' ? (gapWords(mark) ? `${gapWords(mark)} than your number` : 'differs from your number') : text;
    default: return text;
  }
}

export interface CardVerdict { state: State | null; glyph: string; word: string; reason: string; line: string; worst: string | null; more: number; ranCount: number }
/** The one line a measure leads with (spec 4.4). `match`: the Description score, which counts only when it does not match. */
export function cardVerdict(marks: Mark[] | null | undefined, match?: ScoreMatch | null, opts: { running?: boolean; checked?: boolean } = {}): CardVerdict {
  const shown = shownMarks(marks, opts.checked).map(({ mark, state }) => ({ name: mark.name, state, reason: reasonOf(mark, state), mark }));
  if (match && match.score != null && match.level === 'does not match')
    shown.push({ name: 'Description', state: 'review', reason: 'the Description does not match the formula', mark: { name: 'Description', state: 'suspect' } });
  const none = (line: string): CardVerdict => ({ state: null, glyph: '', word: '', reason: '', line, worst: null, more: 0, ranCount: 0 });
  if (opts.running) return none('Checking now');
  if (!shown.length) {
    // A measure that was in a run with no mark that applies is not "not checked yet": nobody knows if it is right (R8).
    const reason = notCheckedReason(marks, opts.checked);
    if (opts.checked || ranAny(marks))
      return { state: 'unknown', glyph: GLYPH.unknown, word: WORD.unknown, reason, line: `${GLYPH.unknown} ${WORD.unknown}: ${reason}`, worst: null, more: 0, ranCount: 0 };
    return { ...none(`Not checked yet: ${reason}`), reason };
  }
  const worst = [...shown].sort((a, b) => STATE_ORDER.indexOf(a.state) - STATE_ORDER.indexOf(b.state))[0];
  // A held measure's marks all wait on the same publish: one line says it, not "(+2 more)" (fx33).
  // The same goes for the marks an edit made stale (fx34).
  const sameHold = (x: { mark: Mark }) => ['held', 'stale'].includes(worst.mark.reasonCode ?? '') && x.mark.reasonCode === worst.mark.reasonCode;
  const problems = shown.filter(x => x.state !== 'verified' && x !== worst && !sameHold(x)).length;
  const state = worst.state, glyph = GLYPH[state], word = WORD[state];
  let reason: string, sep = ':';
  if (state === 'verified') {
    sep = '';
    const equivalence = shown.find(x => x.name === 'Equivalence');
    reason = `by ${shown.length} check${shown.length === 1 ? '' : 's'}${equivalence ? '. Nothing moved.' : ''}`;
  } else if (state === 'overridden') reason = worst.name === 'Filters' ? 'ignores filters on purpose' : 'total marked as expected';
  else if (state === 'unknown') reason = unknownReason(worst.mark);
  else reason = worst.reason;
  const more = state === 'verified' ? 0 : problems;
  return { state, glyph, word, reason, more, worst: worst.name, ranCount: shown.length,
    line: `${glyph} ${word}${sep} ${reason}${more ? ` (+${more} more)` : ''}` };
}

/** Why a mark that applies is Unknown. Totals between its two cuts is not clearly right, so it is never a pass (ra1 item 1). */
function unknownReason(mark: Mark): string {
  switch (mark.reasonCode) {
    case 'totals_unclear': return `the total is not clearly right for ${aType(mark)}`;
    // A change outside the measure (a column's source) is not on the connected model yet (fx33, rc32 tester gap 1).
    case 'held': return 'publish this change to check it';
    // A result from before an edit of the measure, until its rerun replaces it (fx34, rc33 tester gap 1).
    case 'stale': return 'your edit changed this measure, so it is checked again';
    default: return mark.name === 'Totals' ? 'Totals could not be scored' : `${mark.name} could not run`;
  }
}

/** One mark as the card shows it: its state, glyph and label (never colour alone). */
export function markView(mark: Mark, measureRan = false): { name: string; state: State | null; glyph: string; word: string } {
  const state = verdictOf(mark, measureRan);
  return { name: mark.name, state, glyph: state ? GLYPH[state] : '', word: state ? WORD[state] : '' };
}

const BROKEN_FIX = 'Fix the formula, or the number it is compared with, then check again.';
// The engine's line for a result from before an edit (fx34). A stale mark reads it whatever text came with it: the rc34 engine
// sent a stale Totals with the cut "turns on when Jev scoring is released" line (fx35, rc34 tester gap 1).
const STALE_LINE = 'Your edit changed this measure. This result is from before the edit.';
/** The sentence at the top of a mark's detail (spec 4.4 "mark detail"). */
export function detailSentence(mark: Mark, state: State, limitMs = 3000): string { return tidy(detailText(mark, state, limitMs)); }
function detailText(mark: Mark, state: State, limitMs: number): string {
  const text = (mark.sentence ?? '').trim();
  const cells = mark.cells ?? [];
  if (mark.reasonCode === 'stale') return STALE_LINE;
  if (mark.reasonCode === 'held') return sentence(text);
  switch (mark.name) {
    case 'Totals':
      if (state === 'verified') return `Checked as ${aType(mark)}. The total fits its rows.`;
      if (state === 'overridden') return `Checked as ${aType(mark)}. You marked this total as expected.`;
      return `Checked as ${aType(mark)}. ${sentence(text)}`;
    case 'Filters': {
      if (state === 'overridden') { const scope = /ignores (.+?) filters/i.exec(text)?.[1]; return `Ignores ${scope ? `${scope} ` : ''}filters on purpose, as you said.`; }
      if (state === 'verified') {
        const stated = /^(?:passed\.\s*)?(ignores .+ on purpose, as the Description says)\.?$/i.exec(text);
        if (stated) return sentence(stated[1]);
        const n = /in (\d+) places/i.exec(text)?.[1] ?? (cells.length || '');
        return n ? `No ignored filter in ${n} places.` : 'No ignored filter.';
      }
      if (mark.state === 'info') {
        const every = /every place shows (.+?)\.(?:\s|$)/i.exec(text)?.[1], grand = /the grand total is (.+?)\.(?:\s|$)/i.exec(text)?.[1];
        const functions = /check the (.+?)\.?$/i.exec(text)?.[1];
        return [every ? `Every place shows ${every}.` : grand ? `The grand total is ${grand}.` : '', 'The Description does not say so.',
          functions ? `If that is not meant, check the ${functions}.` : 'If that is not meant, check the formula.'].filter(Boolean).join(' ');
      }
      return sentence(text);
    }
    case 'Contexts': {
      if (state !== 'verified') return sentence(text);
      if (/^checked inside/i.test(text)) return sentence(text);
      // "Places" means the cells checked, as on Filters, not the contexts picked (rc27 tester gap 6).
      const n = /(\d+) places/i.exec(text)?.[1] ?? (cells.length || '');
      return n ? `Values in all ${n} places.` : 'Values in all places checked.';
    }
    // Broken says what to fix (d1 tester gap 1): the formula, or the number it is compared with.
    case 'SQL': return state === 'verified' ? (cells.length ? `Same as SQL in ${cells.length} places.` : 'Same as SQL.')
      : state === 'broken' ? `${gapWords(mark, true) ? `Differs from SQL by ${gapWords(mark, true)}.` : sentence(text)} ${BROKEN_FIX}` : sentence(text);
    case 'Trusted number':
      return state === 'verified' ? `Same as your number${cellValue(cells[0]) ? `: ${cellValue(cells[0])}` : ''}.`
        : state === 'broken' ? `${gapWords(mark) ? `${gapWords(mark)} than your number.` : sentence(text)} ${BROKEN_FIX}` : sentence(text);
    case 'Equivalence': {
      if (state === 'verified') return cells.length ? `Nothing moved in ${cells.length} places.` : 'Nothing moved.';
      const n = cells.length;
      return n ? `Your edit moved numbers in ${n >= 6 ? '6 or more' : n} place${n === 1 ? '' : 's'}.` : 'Your edit moved numbers.';
    }
    case 'Performance': {
      const t = slowest(mark), place = placeOf(t?.context);
      if (state === 'verified') return t ? `Slowest: ${place}, ${ms(t.medianMs)}. Limit ${seconds(limitMs)}.` : sentence(text);
      if (mark.timings?.some(x => x.regression)) {
        const r = mark.timings.find(x => x.regression)!;
        return `Slower after your edit: ${ms(r.beforeMedianMs)} to ${ms(r.medianMs)}.`;
      }
      return t ? `Slowest: ${place}, ${ms(t.medianMs)}. Over the ${seconds(limitMs)} limit.` : sentence(text);
    }
    default: return sentence(text);
  }
}

/** The hover on the score: what it compares (d1 tester gap 5), so the band itself keeps Kane's words. */
export const SCORE_TITLE = 'How well the Description matches the formula';
/** The Description score. A score that does not match counts in the verdict; a score that is not served is hidden. */
export function descriptionScore(match: ScoreMatch | null | undefined): { label: string; words: string; reason: string; review: boolean; score: number | null; band: 'good' | 'warn' | null } {
  if (!match || match.score == null || /not scored/i.test(match.level)) return { label: '', words: '', score: null, review: false, band: null, reason: match?.reason?.trim() ?? '' };
  // Kane's bands, 27 Oct 05:49 (spec 4.4 "Kept copy: Description line"). What the score compares is in the hover (SCORE_TITLE).
  const band = match.level === 'matches' ? 'Matches' : match.level === 'does not match' ? 'Does not match' : 'Could be improved';
  return { label: match.level === 'matches' ? `${band} ${match.score}%` : `${band}, ${match.score}%`, words: `${band}, ${match.score}%`, score: match.score,
    review: match.level === 'does not match', band: match.level === 'matches' ? 'good' : 'warn', reason: match.reason?.trim() ?? '' };
}

/** The Description line is cut to a few words, so the card stays within its word budget at any width. Brackets and slashes split a word, as they do when words are counted. */
export const DESCRIPTION_LINE_WORDS = 6;
const WORDS = /[A-Za-z0-9][A-Za-z0-9%:.,'’()\-]*/g;
export const countWords = (text: string): number => (text.match(WORDS) ?? []).length;
export function descriptionLine(text: string | null | undefined, words = DESCRIPTION_LINE_WORDS): string {
  const chunks = (text ?? '').trim().split(/\s+/).filter(Boolean);
  const kept: string[] = [];
  let used = 0;
  for (const chunk of chunks) {
    used += Math.max(1, countWords(chunk));
    if (used > words) break;
    kept.push(chunk);
  }
  return kept.length === chunks.length ? kept.join(' ') : kept.join(' ') + '…';
}

/** The worst state of the marks that apply and ran, plus a Description that does not match. null: nothing has run. */
export function rowVerdict(marks: Mark[] | null | undefined, match?: ScoreMatch | null, checked = false): State | null {
  return cardVerdict(marks, match, { checked }).state;
}

const firstSentence = (text: string | null | undefined) => (text ?? '').trim().split(/(?<=[.!?])\s+/)[0] ?? '';
const HOVER_WORDS = 25;
const wordCount = countWords;

/** The Model tree row: the name and one glyph (the glyph is the description). The hover is three short lines. */
export function treeRowItem(name: string, marks: Mark[] | null | undefined, description?: { oneLine?: string; text?: string; match?: ScoreMatch | null; checked?: boolean }):
  { description: string; tooltip: string; state: State | null; mark: string; label: string } {
  const verdict = cardVerdict(marks, description?.match, { checked: description?.checked });
  // A screen reader hears the measure name first, then its state (rc27 tester gap 4).
  const label = `${name}, ${verdict.state ? verdict.line.slice(verdict.glyph.length + 1) : verdict.line}`;
  // Not checked yet is the whole hover (spec 4.1, wr1 S3); the card keeps its reason clause.
  if (!verdict.state) {
    const line = verdict.line.startsWith('Not checked yet') ? 'Not checked yet' : verdict.line;
    return { description: '', tooltip: line, state: null, mark: 'Totals', label: `${name}, ${line}` };
  }
  const ran = shownMarks(marks, description?.checked).map(({ mark, state }) => `${GLYPH[state]} ${mark.name}`).join('   ');
  const used = wordCount(verdict.line) + wordCount(ran);
  const line3 = descriptionLine(firstSentence(description?.text ?? description?.oneLine), Math.max(0, Math.min(DESCRIPTION_LINE_WORDS * 2, HOVER_WORDS - used)));
  return { description: verdict.glyph, tooltip: [verdict.line, ran, line3].filter(Boolean).join('\n'), state: verdict.state, mark: verdict.worst ?? 'Totals', label };
}

export interface CheckRow { ref: string; name: string; marks?: Mark[]; match?: ScoreMatch | null; checked?: boolean }
export type RowGroup = 'broken' | 'review' | 'unknown' | 'verified' | 'unchecked';
export const GROUP_ORDER: RowGroup[] = ['broken', 'review', 'unknown', 'verified', 'unchecked'];
export const GROUP_WORDS: Record<RowGroup, string> = { broken: 'Broken', review: 'Needs review', unknown: 'Unknown', verified: 'Verified', unchecked: 'Not checked yet' };
/** The Checks page groups its measures by verdict, worst first, then by name. A measure whose worst mark is Overridden sits with Verified. */
export function groupByState<T extends CheckRow>(rows: T[]): { group: RowGroup; state: State | null; rows: (T & { state: State | null })[] }[] {
  const withState = rows.map(r => ({ ...r, state: rowVerdict(r.marks, r.match, r.checked) }));
  const groupOf = (state: State | null): RowGroup => state === null ? 'unchecked' : state === 'overridden' ? 'verified' : state;
  return GROUP_ORDER.map(group => ({ group, state: group === 'unchecked' ? null : group as State,
    rows: withState.filter(r => groupOf(r.state) === group).sort((a, b) => a.name.localeCompare(b.name)) })).filter(g => g.rows.length > 0);
}

/** The header's verdict line: `1 needs review · 9 verified · 2 not checked yet`. A part that is zero is left out. */
export function checksVerdictLine(groups: { group: RowGroup; rows: unknown[] }[], total: number): string {
  const n = (g: RowGroup) => groups.find(x => x.group === g)?.rows.length ?? 0;
  if (n('unchecked') === total) return `${total} measure${total === 1 ? '' : 's'}. Not checked yet.`;
  return ([['broken', 'broken'], ['review', n('review') === 1 ? 'needs review' : 'need review'], ['unknown', 'unknown'], ['verified', 'verified'], ['unchecked', 'not checked yet']] as const)
    .filter(([g]) => n(g) > 0).map(([g, w]) => `${n(g)} ${w}`).join(' · ');
}

/** The Checks header budget (spec 4.2): the title and the verdict line, 12 words or fewer in every state. */
export const HEADER_WORDS = 12;
/** The verdict line the header shows. Every part that is not zero is on it, Verified included: a verdict shows its coverage
 *  (spec 4.2, R8; rc28 tester gap 3 found "1 needs review · 1 unknown" with 10 verified left out). Only when the line would pass
 *  the 12-word budget do the calm ones (Verified, Not checked yet) leave, as their group headings show them, so what needs
 *  action leads (R1) and Unknown never leaves (R8). Action parts that still do not fit fold into "(+<n> more)". */
export function checksHeaderParts(verdict: string, problems = '', budget = HEADER_WORDS - 1): { verdict: string; problems: string; more: number } {
  const split = (s: string) => s.split(' · ').map(x => x.trim()).filter(Boolean);
  const calm = /^\d+ (verified|not checked yet)$/;
  const all = [...split(verdict).map(text => ({ text, issue: false })), ...split(problems).map(text => ({ text, issue: true }))];
  const total = all.reduce((n, p) => n + countWords(p.text), 0);
  let kept = all;
  if (total > budget) {
    // Over the budget the calm counts leave first: their group headings below show them, one look away (R2).
    const action = all.filter(p => !calm.test(p.text));
    kept = [];
    let used = 0;
    const fits = action.reduce((n, p) => n + countWords(p.text), 0) <= budget;
    for (const part of action) {
      const cost = countWords(part.text);
      // When an action part must fold too, every part kept leaves room for the two words of "(+<n> more)".
      if (!fits && used + cost + 2 > budget) break;
      kept.push(part); used += cost;
    }
  }
  const more = kept === all ? 0 : all.filter(p => !calm.test(p.text)).length - kept.length;
  return { verdict: kept.filter(p => !p.issue).map(p => p.text).join(' · '), problems: kept.filter(p => p.issue).map(p => p.text).join(' · '), more };
}

const GROUPS: Record<string, string> = { changed: 'Changed', core: 'Core', reports: 'Used in reports', risky: 'Risky',
  match: 'Description may not match', suspect: 'Suspect last time', oldest: 'Oldest check', selected: 'Ticked' };
const GROUP_WORD: Record<string, string> = { changed: 'changed', core: 'core', reports: 'used in reports', risky: 'risky',
  match: 'low score', suspect: 'suspect last time', oldest: 'oldest', selected: 'ticked' };

export function planGroups(plan: RunPlanView): { group: string; label: string; picks: RunPick[] }[] {
  const out: { group: string; label: string; picks: RunPick[] }[] = [];
  for (const p of plan.picks) {
    let g = out.find(x => x.group === p.group);
    if (!g) out.push(g = { group: p.group, label: GROUPS[p.group] ?? p.group, picks: [] });
    g.picks.push(p);
  }
  return out;
}

// Five warm runs and one cold run per measure, each about as long as the measure's checks.
const PERFORMANCE_FACTOR = 2;
export function planSeconds(plan: { estimatedSeconds: number }, includePerformance: boolean): number {
  return plan.estimatedSeconds * (includePerformance ? 1 + PERFORMANCE_FACTOR : 1);
}
export function aboutTime(seconds: number): string {
  if (seconds < 60) { const s = Math.max(1, Math.ceil(seconds)); return `About ${s} second${s === 1 ? '' : 's'}`; }
  const m = Math.max(1, Math.ceil(seconds / 60));
  return `About ${m} minute${m === 1 ? '' : 's'}`;
}
/** The card's line while a measure is timed (spec 4.4, R4): "Timing. About 30 seconds." The engine's progress counts runs and
 *  names the cold run; only the time it gives is kept. Before the engine gives one, the timing takes about 30 seconds. */
export function timingLine(progress: string | null | undefined): string {
  const left = /about (\d+) (second|minute)s?/i.exec(progress ?? '');
  return `Timing. ${left ? aboutTime(Number(left[1]) * (left[2].toLowerCase() === 'minute' ? 60 : 1)) : aboutTime(30)}.`;
}
/** The plan in one sentence. Include performance changes the estimate here, so every place that shows it agrees. */
export function planSummary(plan: RunPlanView, includePerformance: boolean): { sentence: string; seconds: number; time: string } {
  const seconds = planSeconds(plan, includePerformance), groups = planGroups(plan);
  const time = aboutTime(seconds);
  const parts = groups.map(g => `${g.picks.length} ${GROUP_WORD[g.group] ?? g.group}`);
  const line = (p: string[]) => `Checking ${plan.picks.length} of ${plan.totalMeasures}${p.length ? ': ' + p.join(', ') : ''}. ${time}.`;
  // Choose and Run are two of the 15 words (spec 4.3). Over the budget the last groups fold into "<n> more", as the header
  // folds its parts (checksHeaderParts); Choose lists every group (wr1 F8).
  let kept = parts;
  for (let n = parts.length - 1; n >= 1 && countWords(line(kept)) > PLAN_WORDS - 2; n--)
    kept = [...parts.slice(0, n), `${groups.slice(n).reduce((sum, g) => sum + g.picks.length, 0)} more`];
  return { seconds, time, sentence: line(kept) };
}
/** The run plan's budget at rest, Choose and Run included (spec 4.3). */
export const PLAN_WORDS = 15;

export type Selection = { kind: 'measures'; measures: string[] } | { kind: 'table'; table: string } | { kind: 'folder'; table?: string; folder: string } | { kind: 'changed' };
export function selectionRequest(selection: Selection, includePerformance: boolean) {
  const base = { kind: selection.kind, measures: selection.kind === 'measures' ? [...selection.measures] : [] as string[] };
  const extra = selection.kind === 'table' ? { table: selection.table }
    : selection.kind === 'folder' ? { ...(selection.table ? { table: selection.table } : {}), folder: selection.folder } : {};
  return { selection: { ...base, ...extra }, includePerformance, cap: 40 };
}

const two = (n: number) => String(n).padStart(2, '0');
/** "Last checked last night at 02:10". An overnight sweep reads as last night, not as the weekday (tester note 14). */
export function lastChecked(iso: string | null | undefined, now = new Date()): string {
  if (!iso) return 'Not checked yet';
  const at = new Date(iso);
  if (Number.isNaN(at.getTime())) return 'Not checked yet';
  const clock = `${two(at.getHours())}:${two(at.getMinutes())}`;
  const day = (d: Date) => new Date(d.getFullYear(), d.getMonth(), d.getDate()).getTime();
  const days = Math.round((day(now) - day(at)) / 86_400_000);
  if (days === 0) return `Last checked ${at.getHours() < 6 && now.getHours() >= 6 ? 'last night' : 'today'} at ${clock}`;
  if (days === 1) return `Last checked ${at.getHours() >= 18 ? 'last night' : 'yesterday'} at ${clock}`;
  return `Last checked ${at.toLocaleDateString('en-AU', { weekday: 'short', day: 'numeric', month: 'short' })} at ${clock}`;
}

/** "Checked 06:44" for today; the day first for an older check. */
export function checkedAt(iso: string | null | undefined, now = new Date()): string {
  const at = iso ? new Date(iso) : null;
  if (!at || Number.isNaN(at.getTime())) return '';
  const clock = `${two(at.getHours())}:${two(at.getMinutes())}`;
  const day = (d: Date) => new Date(d.getFullYear(), d.getMonth(), d.getDate()).getTime();
  const days = Math.round((day(now) - day(at)) / 86_400_000);
  return days === 0 ? `Checked ${clock}` : days === 1 ? `Checked yesterday ${clock}` : `Checked ${at.toLocaleDateString('en-AU', { day: 'numeric', month: 'short' })} ${clock}`;
}

/** The saved plan uses UTC. The person reads the same local clock as the mark cards. */
export function runReason(reason: string, now = new Date()): string {
  const oldest = /^Oldest check: (.+?)\.?$/.exec(reason);
  if (!oldest) return reason;
  return lastChecked(oldest[1].replace(' ', 'T'), now) + '.';
}

/** The source of a Description, shown inside the full Description. Nothing when it is not recorded. */
export function provenanceWords(provenance: string | undefined): string {
  return provenance === 'person' ? 'Written by a person' : provenance === 'ai' ? 'Drafted by AI' : '';
}

const placeKey = (context: string | null | undefined) => placeOf(context ?? undefined).toLowerCase();
/** One value per place for a measure (d1 tester gap 4). Every mark reads the same measure in the same place, so a place shows
 *  one value on every mark. Filters and Contexts read it in the model's own format, so they speak first; Totals follows. */
export function placeValues(marks: Mark[] | null | undefined): Map<string, string> {
  const values = new Map<string, string>();
  for (const name of ['Filters', 'Contexts', 'Totals']) for (const c of (marks ?? []).find(m => m.name === name)?.cells ?? []) {
    if (c.before != null && c.before !== '') continue;
    const key = placeKey(c.context), value = (c.afterText ?? c.after ?? '').trim();
    if (value && !/^could not check/i.test(value) && !values.has(key)) values.set(key, value);
  }
  return values;
}
/** The value a one-value cell shows: the measure's value in that place, the same on every mark. */
export function placeValue(cell: MarkCell, values: Map<string, string>): string {
  return values.get(placeKey(cell.context)) ?? cell.afterText ?? cell.after ?? '';
}

export function contextName(c: MarkContext): string {
  if (c.column?.name) return c.shape === 'GrandTotal' ? 'Grand total' : `${c.column.table} ${c.column.name}`;
  return c.shape === 'GrandTotal' ? 'Grand total' : c.id;
}

/** Properties has no permission card: its Turn on opens Studio's (fx29b F). Studio's card checks the measure again once
 *  permission is on; this is the same for a host that only hears "the model changed". Armed by Turn on with the card's
 *  recheck, it asks the engine on each change and runs the recheck once, when permission is current. */
export function adviceRecheck(read: () => Promise<{ current?: boolean } | null | undefined>) {
  let then: (() => void) | null = null;
  return {
    arm(fn?: () => void) { then = fn ?? null; },
    async changed() {
      const pending = then;
      if (!pending) return;
      let consent: { current?: boolean } | null | undefined;
      try { consent = await read(); } catch { return; }
      if (consent?.current && then === pending) { then = null; pending(); }
    },
  };
}
