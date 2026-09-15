// Pure reading rules for the Checks > Tests page. No React, no engine calls: every function here turns
// what the engine sent into the words and states the page shows, so the wording can be tested without a
// browser (test/tests-reading-rules.test.mjs runs this file). The page itself only lays these out.

export type ResultChip = 'Pass' | 'Differs' | 'Could not check' | 'Off' | 'Not run';

export interface OutcomeLike {
  verdict?: string;
  missing?: boolean;
}

export interface StoredResult<T> {
  outcome: T;
  whenIso: string;
}

export interface RunGroup {
  id: 'checks' | 'relationships' | 'tableCounts';
  label: string;
  // null means "we have not counted these yet", which is NOT the same as none. Relationships are
  // checked on every run, so before the first run their size is unknown and the group still counts.
  count: number | null;
}

// Five states and no sixth. A Suspect check DID see a difference; it is a difference whose cause lives
// somewhere else, and the row names that cause. Calling it "could not check" would hide a real difference,
// and giving it its own chip would make the person learn a word the engine invented.
export function resultChip(enabled: boolean, outcome?: OutcomeLike | null): ResultChip {
  if (!enabled) return 'Off';
  if (!outcome) return 'Not run';
  if (outcome.missing) return 'Could not check';
  if (outcome.verdict === 'Pass') return 'Pass';
  if (outcome.verdict === 'Fail' || outcome.verdict === 'Suspect') return 'Differs';
  return 'Could not check';
}

// What "all" actually includes, group by group, counted. A group with nothing in it is never offered:
// offering it would let a person ask for a run that the engine then refuses for a reason they cannot see.
export function runScopeGroups(enabledChecks: number, relationships: number | null, tableCounts: number): RunGroup[] {
  return ([
    { id: 'checks', label: 'Enabled saved checks', count: enabledChecks },
    { id: 'relationships', label: 'Relationships', count: relationships },
    { id: 'tableCounts', label: 'Table row counts', count: tableCounts },
  ] as RunGroup[]).filter((group) => group.count == null || group.count > 0);
}

// "Only this" has to mean only this. The page sent 'relationships' for BOTH automatic groups, so picking
// table row counts also probed every relationship and picking relationships also opened a SQL connection
// nobody asked for (Astra, 2026-09-15). Each group now names its own family, which the engine runs alone.
const GROUP_SECTION: Record<RunGroup['id'], string> = {
  checks: 'measures', relationships: 'relationships', tableCounts: 'tableCounts',
};
export function sectionsForGroup(id: RunGroup['id']): string[] {
  return [GROUP_SECTION[id]];
}

// And the menu says which one it is about to run, rather than three identical buttons reading "Only this".
export function runOnlyLabel(group: RunGroup): string {
  return `Run ${group.label.toLocaleLowerCase()} only`;
}

// Zero ticks used to be sent as an empty list, which the engine read as "everything". The refusal happens
// here, in the page, before the request exists.
export function tickedRunRefusal(tickedCount: number): string | null {
  return tickedCount > 0 ? null : 'Tick at least one check first. Nothing is ticked, so there is nothing to run.';
}

// Tick boxes live ONLY on saved-check rows. With no saved checks the run menu still offered "Only what I
// ticked", and the list header still said "Tick to run a subset", so both described a control that was not
// on the page (Kane, live on the Yoga, 2026-09-15). An option nobody can take is not offered.
export function tickingIsPossible(savedCheckCount: number): boolean {
  return savedCheckCount > 0;
}

export function savedChecksHint(savedCheckCount: number): string {
  return tickingIsPossible(savedCheckCount) ? 'Tick to run a subset.' : 'Save a check to run checks one at a time.';
}

// What the ticked option says about itself: the refusal while nothing is ticked, else the count.
export function tickedMenuNote(tickedCount: number, savedCheckCount: number): string {
  const refusal = tickedRunRefusal(tickedCount);
  if (refusal) return refusal;
  return `${tickedCount.toLocaleString()} ${tickedCount === 1 ? 'check' : 'checks'} ticked.`;
}

// A partial rerun replaces only what it ran. Every other row keeps its own older result AND its own older
// date, so the list never dates a result to a run that did not produce it.
export function foldRunResults<T extends { defId: string }>(
  previous: Record<string, StoredResult<T>>, outcomes: T[], whenIso: string,
): Record<string, StoredResult<T>> {
  const next: Record<string, StoredResult<T>> = { ...previous };
  for (const outcome of outcomes) next[outcome.defId] = { outcome, whenIso };
  return next;
}

// ---------------------------------------------------------------------------------------------------
// Which SQL source an OPEN check is bound to. (Astra, 2026-09-15, P1.)
//
// The drawer used to read its own first load of the source list as "a source appeared while Connections
// was open", so with one saved source in the list it replaced an older check's inline endpoint AND a
// check whose named source had been removed. Saving then wrote that new id, and the check silently
// started asking a different database. Three rules, and none of them guesses:
//
//   1. A named source that is still saved is kept, and the check says so.
//   2. A check with no named source but its own saved endpoint keeps that endpoint. It is not broken,
//      so editing something else about it must not force a rebind.
//   3. A named source that has GONE requires an explicit replacement. Falling back to the inline values,
//      or to the only source left, would run the check somewhere the person never chose.
// ---------------------------------------------------------------------------------------------------
export interface SqlSourceOption { id: string; name: string }
export interface SqlSourceBindingInput {
  savedId?: string;
  savedName?: string;
  inlineServer?: string;
  inlineDatabase?: string;
  available: SqlSourceOption[];
  /** False while the list is still being read. A slow list must never read as a missing source. */
  loaded: boolean;
}
export interface SqlSourceBinding {
  /** The id the picker holds. Empty when the check names no source, or names one that has gone. */
  select: string;
  /** The one plain sentence under the picker, or null when there is nothing to say. */
  note: string | null;
  /** Save must refuse until the person picks a source: the one this check named is not there any more. */
  needsReplacement: boolean;
  /** Save may proceed with no source id: this check carries its own endpoint and still works. */
  keepsInline: boolean;
}
export function sqlSourceBinding(input: SqlSourceBindingInput): SqlSourceBinding {
  const savedId = input.savedId?.trim() || '';
  const inline = input.inlineDatabase?.trim() || input.inlineServer?.trim() || '';
  if (!input.loaded) return { select: savedId, note: null, needsReplacement: false, keepsInline: false };

  if (savedId) {
    const found = input.available.find((source) => source.id === savedId);
    if (found) {
      return { select: found.id, note: `This check uses its saved ${found.name} source.`, needsReplacement: false, keepsInline: false };
    }
    const name = input.savedName?.trim();
    return {
      select: '',
      note: `The SQL source this check used, ${name || 'the one it was saved with'}, is no longer saved. `
        + 'Pick the source it should use now.',
      needsReplacement: true,
      keepsInline: false,
    };
  }
  if (inline) {
    return { select: '', note: `This check uses its saved ${inline} source.`, needsReplacement: false, keepsInline: true };
  }
  return { select: '', note: null, needsReplacement: false, keepsInline: false };
}

// ONE validation, read by BOTH buttons in the drawer. "Try it now" used to call straight through with no
// guard at all, so a check whose named source had gone was tried with the id dropped and the old inline
// address kept, and the engine read the absent id as permission to use it (Astra, 2026-09-15, round two).
// A check the engine would refuse to SAVE is a check it cannot honestly be asked to TRY either.
export interface AuthoringInput {
  blankPolicy?: string;
  selectedSourceId?: string;
  binding: Pick<SqlSourceBinding, 'needsReplacement' | 'keepsInline'>;
}
export function authoringRefusal(input: AuthoringInput): string | null {
  if (!input.blankPolicy) return 'Pick what a blank in the model means.';
  if (input.selectedSourceId) return null;
  if (input.binding.needsReplacement) {
    return 'This check points at a SQL source that is no longer saved. Pick the source it should use now.';
  }
  if (input.binding.keepsInline) return null;
  return 'Pick the SQL source this check asks, or add one.';
}

// The source fields every payload carries, whichever button sent it. A missing named source stays ON the
// payload: dropping it is exactly what let the inline address win, so the bad payload is now impossible to
// build. If a call ever escapes the guard above, the engine refuses it as a dangling source instead.
export function reconcileSourceFields(
  selectedId: string, savedId: string | undefined, savedName: string | undefined, sources: SqlSourceOption[],
): { sqlSourceId?: string; sqlSourceName?: string } {
  const id = selectedId || savedId || '';
  if (!id) return {};
  const found = sources.find((source) => source.id === id);
  return { sqlSourceId: id, sqlSourceName: found?.name ?? (id === savedId ? savedName : undefined) };
}

// Which source the hub just added, if exactly one. The BASELINE is the list as it stood when the person
// chose "Add a SQL source...", so a null baseline means Add was never invoked and nothing may be adopted.
// That null case is the whole of the P1 defect: the drawer's own first load looked like an addition.
export function sourceAddedSinceAdd(baseline: string[] | null, current: string[]): string | null {
  if (!baseline) return null;
  const known = new Set(baseline);
  const added = current.filter((id) => !known.has(id));
  return added.length === 1 ? added[0] : null;
}

export function comparisonSummary(differ: number, match: number, shown: number, total: number): string {
  const head = differ === 0
    ? `Every comparison matches; ${match.toLocaleString()} of ${total.toLocaleString()}.`
    : `${differ.toLocaleString()} ${differ === 1 ? 'comparison differs' : 'comparisons differ'}; ${match.toLocaleString()} match.`;
  if (shown >= total) return head;
  return `${head} Showing ${shown.toLocaleString()} of ${total.toLocaleString()}; the rest were not kept with the run.`;
}

// Two decimals is right for money and wrong for a tolerance breach: Astra's probe had a difference of
// 0.001 read back as "0.00 higher", which tells a person the check found nothing. So the number keeps
// enough decimals to be non-zero, and a whole number still reads as a whole number.
const amount = (value: number) => {
  const size = Math.abs(value);
  if (size === 0) return '0';
  if (Number.isInteger(value)) return size.toLocaleString();
  const decimals = Math.max(2, Math.min(20, Math.ceil(-Math.log10(size)) + 2));
  return size.toLocaleString(undefined, { minimumFractionDigits: 2, maximumFractionDigits: decimals });
};

// A number a person reads, never 1e-9 and never rounded. Fifteen significant digits with a twenty-decimal
// cap is fine for a tolerance someone typed and wrong for one the engine ACCEPTS: it recorded 1e-21 as "0"
// and cut 0.12345678901234566 short (Astra, round three, on the engine's copy of this rule; the page had
// the same defect one surface over). So the ROUND-TRIP representation is expanded rather than reformatted,
// and `shiftRight` moves the decimal point, which is how a fraction becomes a percent: multiplying by 100
// in floating point turns 1e-7 into 0.000009999999999999999.
//
// This must agree with LocalEngine.TestSuite.cs PlainNumber, which writes the sentence a RECORDED run
// keeps. The History row renders that stored sentence verbatim; this one serves the live opened row.
const plainNumber = (value: number, shiftRight = 0): string => {
  if (!Number.isFinite(value)) return String(value);
  const text = String(value);
  const exponentAt = text.search(/[eE]/);
  let exponent = shiftRight;
  let mantissa = text;
  if (exponentAt >= 0) {
    exponent += Number(text.slice(exponentAt + 1));
    mantissa = text.slice(0, exponentAt);
  }
  const negative = mantissa.startsWith('-');
  if (negative) mantissa = mantissa.slice(1);
  const pointAt = mantissa.indexOf('.');
  const digits = pointAt < 0 ? mantissa : mantissa.slice(0, pointAt) + mantissa.slice(pointAt + 1);
  const point = (pointAt < 0 ? mantissa.length : pointAt) + exponent;

  let plain: string;
  if (point <= 0) plain = `0.${'0'.repeat(-point)}${digits}`;
  else if (point >= digits.length) plain = digits + '0'.repeat(point - digits.length);
  else plain = `${digits.slice(0, point)}.${digits.slice(point)}`;

  if (plain.includes('.')) plain = plain.replace(/0+$/, '').replace(/\.$/, '');
  plain = plain.replace(/^0+(?=\d)/, '');
  if (plain.length === 0 || plain.startsWith('.')) plain = `0${plain}`;
  return negative && plain !== '0' ? `-${plain}` : plain;
};

// What the check allows before it calls a difference a difference. It used to print "1.00e-9 either way
// or 1.00e-7%", which is a notation nobody typed and nobody reads (Astra, question 3).
export function toleranceSentence(absolute?: number, relative?: number): string {
  const abs = absolute ?? 0;
  const rel = relative ?? 0;
  if (!abs && !rel) return 'Allowed difference: none. The numbers must match exactly.';
  if (!rel) return `Allowed difference: ${plainNumber(abs)}.`;
  if (!abs) return `Allowed difference: ${plainNumber(rel, 2)}%.`;
  return `Allowed difference: ${plainNumber(abs)} or ${plainNumber(rel, 2)}%, whichever is larger.`;
}

// One sentence, leading with the thing the person came to find out. It names numbers only: a trusted-answer
// check has no SQL side, and this sentence is the same shape for both kinds so neither borrows the other's
// vocabulary (Astra's journey A: "SQL result NULL" under a SQL header on a check with no SQL at all).
export function differenceSentence(
  expected?: string, actual?: string, difference?: number | null, sourceLabel = 'the answer you trust',
): string {
  if (difference == null) {
    return `The model answered ${actual || 'nothing'}, and ${sourceLabel} says ${expected || 'nothing'}. `
      + 'One of these is not a number, so we cannot say by how much they differ.';
  }
  if (difference === 0) return `The model matches ${sourceLabel}.`;
  return `The model is ${amount(difference)} ${difference < 0 ? 'lower' : 'higher'} than ${sourceLabel}.`;
}

const sentenceList = (items: string[]) => items.length === 1 ? items[0]
  : `${items.slice(0, -1).join(', ')} and ${items[items.length - 1]}`;

// A part run now earns a REAL letter for what it ran (cp/tests-engine-a (b)); it used to be scored zero
// and labelled "Partial", which told a person nothing about the checks they had just asked for. So the
// page shows the letter and says, in the engine's own words, exactly what that letter covers. It must
// never read as the model's overall grade, and the rows that did not run are accounted for by name.
export function partRunSummary(gradeCovers: string | undefined, skipped: string[]): string {
  const covers = gradeCovers ? `for ${gradeCovers}` : 'for only the part you ran';
  const head = `This grade is ${covers}, not for the whole model. The last full run's grade still stands.`;
  if (skipped.length === 0) return head;
  // The engine names its groups in lower case ("saved checks"), so the sentence has to lift the first
  // letter itself rather than printing "saved checks did not run" mid-paragraph as a new sentence.
  const list = sentenceList(skipped);
  return `${head} ${list.charAt(0).toUpperCase()}${list.slice(1)} did not run, and those rows keep their own earlier result and date.`;
}

export function relationshipAttention(needing: number): string {
  if (needing === 0) return 'Nothing needs attention';
  return `${needing.toLocaleString()} ${needing === 1 ? 'relationship needs' : 'relationships need'} attention`;
}

// ---------------------------------------------------------------------------------------------------
// A relationship whose checks could not be decided is not fine. (Astra, 2026-09-15, finding 5.)
//
// The card counted "fine" as everything minus the failures, so Budget → Scenario, whose checks were all
// unknown, was reported as fine, and a card where EVERY check was unknown showed a Pass header. An
// unknown is its own state on this page everywhere else; it is its own state here too.
// ---------------------------------------------------------------------------------------------------
export type RelationshipVerdict = 'attention' | 'unknown' | 'pass';
export interface RelationshipTally { attention: number; passed: number; unknown: number }

export function relationshipVerdict(checkVerdicts: readonly string[]): RelationshipVerdict {
  if (checkVerdicts.some((v) => v === 'Fail' || v === 'Suspect')) return 'attention';
  if (checkVerdicts.length === 0) return 'unknown';
  return checkVerdicts.every((v) => v === 'Pass') ? 'pass' : 'unknown';
}

export function relationshipTally(all: readonly (readonly string[])[]): RelationshipTally {
  const tally: RelationshipTally = { attention: 0, passed: 0, unknown: 0 };
  for (const checks of all) {
    const verdict = relationshipVerdict(checks);
    if (verdict === 'attention') tally.attention += 1;
    else if (verdict === 'pass') tally.passed += 1;
    else tally.unknown += 1;
  }
  return tally;
}

export function relationshipSummary(tally: RelationshipTally): string {
  const parts: string[] = [];
  if (tally.attention) parts.push(`${tally.attention.toLocaleString()} ${tally.attention === 1 ? 'needs' : 'need'} attention`);
  if (tally.passed) parts.push(`${tally.passed.toLocaleString()} passed`);
  if (tally.unknown) parts.push(`${tally.unknown.toLocaleString()} could not be checked`);
  return parts.length === 0 ? 'Nothing to check.' : `${parts.join('; ')}.`;
}

// Pass means checked and passing. One unknown withholds it, because a card cannot promise something it
// did not look at.
export function relationshipChip(tally: RelationshipTally): ResultChip {
  if (tally.attention > 0) return 'Differs';
  if (tally.unknown > 0 || tally.passed === 0) return 'Could not check';
  return 'Pass';
}

// ---------------------------------------------------------------------------------------------------
// What DID run, and what the rows that did not run still say. (Astra, 2026-09-15, finding 4.)
//
// A part run rendered relationships from the newest run alone, so a skipped family came back as an empty
// report and the card said "This model has no relationships to check", which is a claim about the model
// made from a fact about the run.
// ---------------------------------------------------------------------------------------------------
export interface RunScopeLike { ran?: string[]; skipped?: string[] }

export function familyRan(scope: RunScopeLike | undefined | null, family: string): boolean {
  if (!scope) return true;
  if (scope.skipped?.includes(family)) return false;
  if (scope.ran && scope.ran.length > 0) return scope.ran.includes(family);
  return true;
}

export interface RetainedFamily<T> { value: T; whenIso: string; modelKey: string }

export function foldFamilyResult<T>(
  previous: RetainedFamily<T> | null,
  next: { ran: boolean; value: T | undefined; whenIso: string; modelKey: string },
): RetainedFamily<T> | null {
  if (next.ran && next.value !== undefined) return { value: next.value, whenIso: next.whenIso, modelKey: next.modelKey };
  if (!previous) return null;
  // A retained result belongs to ONE model. Showing the last model's relationships under this one's name
  // would be worse than showing nothing.
  return previous.modelKey === next.modelKey ? previous : null;
}

// How many relationships the menu should say it can run. It used to come from the NEWEST run, so a
// saved-check-only run handed it the empty skipped report, the count read zero, and runScopeGroups then
// removed "Run relationships only" from the menu: running the saved checks took away the way back to
// running the relationships (Astra, 2026-09-15, round two). Null means "not measured yet", which is not
// zero; only a model that genuinely has none withholds the choice.
export function relationshipMenuCount(
  retained: RetainedFamily<{ summary: { relationships: number } }> | null,
): number | null {
  return retained ? retained.value.summary.relationships : null;
}

export function skippedFamilyNote(family: 'relationships' | 'table counts', whenIso?: string): string {
  if (!whenIso) {
    const lead = family === 'relationships' ? 'Relationships' : 'Table counts';
    return `${lead} did not run this time, and have not been checked yet.`;
  }
  const when = new Date(whenIso);
  const date = Number.isNaN(when.valueOf()) ? whenIso : when.toLocaleDateString();
  return `These ${family} were last checked on ${date}; they did not run this time.`;
}

// ---------------------------------------------------------------------------------------------------
// Why a row could not be checked, short enough for the Expected · actual cell. (Astra, question 1.)
//
// The cell printed "· · ·" on every unknown row, so the one thing the person needed (that the calculation
// is gone, or that nothing is connected) was only reachable by opening the row. The full engine message
// stays in the opened row and in the cell's hover; this is the headline, in the page's own words.
// ---------------------------------------------------------------------------------------------------
export interface UnknownOutcomeLike { actual?: string; missing?: boolean; message?: string }

export function unknownCellText(outcome?: UnknownOutcomeLike | null): string | null {
  if (!outcome) return null;
  if (outcome.actual) return null;
  if (outcome.missing) return 'Calculation missing. Edit this check.';
  const message = outcome.message ?? '';
  if (/no longer exists/i.test(message)) return 'Calculation missing. Edit this check.';
  if (/no live connection|needs a live connection/i.test(message)) return 'No live model to ask.';
  if (/source .*no longer saved|no longer saved/i.test(message)) return 'Its SQL source is no longer saved.';
  if (/returned a table of/i.test(message)) return 'That query returns a table, not one number.';
  if (/returned no rows/i.test(message)) return 'That query returned nothing to compare.';
  if (/has no formula/i.test(message)) return 'This calculation has no formula.';
  if (/no number to trust/i.test(message)) return 'No trusted number was saved for it.';
  if (/BLANK vs NULL/i.test(message)) return 'Blank and no value were not treated as equal.';
  return 'Could not check. Open this row for why.';
}

// A grand-total-only comparison is not a weaker check, it is a check that can never pass: rows sitting on
// the hidden blank row cancel out in the total. The engine's ReconcileContract files groupBy as
// "required for a verdict" for exactly this reason, so the drawer says the cost before the last click.
export function limitedEvidenceNote(groupCount: number): string | null {
  if (groupCount > 0) return null;
  return 'This compares only the overall total, which is limited evidence. '
    + 'A comparison with no groups can show a matching total but can never pass as verified. '
    + 'Add at least one field to compare across.';
}

export const TABLE_COUNT_FOOTER =
  '"Counts differ" means matching snapshots were not confirmed, so this alone does not prove missing rows.';
