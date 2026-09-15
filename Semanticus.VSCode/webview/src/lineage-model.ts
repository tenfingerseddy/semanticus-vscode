// Every sentence and every grouping rule the Impact page says out loud, as pure functions.
//
// They live apart from the components on purpose. Astra's UAT found the page and the engine telling a
// person two different stories about the same reading, and the repair is only worth anything if the
// wording that carries it can be executed and checked rather than eyeballed in a render. Nothing here
// touches React, the bridge or the clock: a caller passes the time formatter in, so the same input
// always produces the same sentence.
//
// Honesty rules these functions enforce, from Astra's copy tables:
//   - never invent a total nobody counted ("Reports have not been checked", not "0 of 3");
//   - a tick is a selection, a reading is a check, and only a reading may be called checked;
//   - a complete reading of the list is still not a claim about every report anywhere;
//   - a stale check is not a check;
//   - nothing is called safe.

// ---- wire shapes, as the engine sends them --------------------------------------------------------
export type ReportState = 'checked' | 'couldNotBeFullyChecked' | 'notChecked' | 'needsChecking';

export interface ScopeReport {
  id: string; kind: 'published' | 'local'; name: string; source?: string;
  /** A published report's OWN coordinates, carried per row. Rebuilding them from whichever workspace the
   * picker happened to show is what silently moved one report into another workspace (Astra, P3). */
  workspaceId?: string; reportId?: string;
  state: ReportState; checkedWhenUtc?: string; note?: string; fieldsUsed?: number; visuals?: number;
}
export interface ReportScope {
  modelName?: string; reports: ScopeReport[];
  listed: number; checked: number; couldNotBeFullyChecked: number; notChecked: number; needsChecking: number;
  checkedWhenUtc?: string; gaps: string[]; summary?: string; note?: string;
}
export interface ImpactNode { ref: string; name: string; kind: string; table?: string; depth: number; via: string; viaName?: string }
export interface ImpactShape {
  root?: string; rootName?: string; rootKind?: string; impacted: ImpactNode[];
  measures: number; columns: number; tables: number; relationships: number; other: number;
}
export interface CleanupItem { ref: string; name: string; kind: string; table?: string; verdict: string; reason: string; blockedBy?: string[]; refCount?: number }

export interface TimeOpts { time: (iso: string) => string }

// ---- small shared helpers -------------------------------------------------------------------------
const plural = (n: number, one: string, many?: string) => `${n} ${n === 1 ? one : many ?? one + 's'}`;

/** "a, b and c", trimmed to three names plus a count so a hundred reports stay one readable line. */
export function names(list: string[], cap = 3): string {
  const clean = list.filter(Boolean);
  if (clean.length === 0) return '';
  if (clean.length === 1) return clean[0];
  if (clean.length <= cap + 1) return clean.slice(0, -1).join(', ') + ' and ' + clean[clean.length - 1];
  return clean.slice(0, cap).join(', ') + ' and ' + (clean.length - cap) + ' more';
}

const named = (scope: ReportScope, state: ReportState) => scope.reports.filter((r) => r.state === state).map((r) => r.name);

/** The one source every checked report came from, when they all share one. Else null. */
function sharedSource(scope: ReportScope): string | null {
  const sources = new Set(scope.reports.filter((r) => r.state === 'checked').map((r) => r.source ?? ''));
  const only = [...sources];
  return only.length === 1 && only[0] ? only[0] : null;
}

const latestCheck = (scope: ReportScope) =>
  scope.reports.map((r) => r.checkedWhenUtc).filter(Boolean).sort().reverse()[0] ?? scope.checkedWhenUtc;

// ---- 1. the reports row ----------------------------------------------------------------------------
/**
 * The five states Astra's table requires, as a headline plus the detail under it. The headline is the
 * claim; the detail carries the names, the source and the time, so a person can see what the claim
 * rests on without leaving the card.
 */
export function reportsRowLine(scope: ReportScope, opts: TimeOpts): { headline: string; detail: string; action: string } {
  const when = latestCheck(scope);
  if (!scope || scope.listed === 0)
    return {
      headline: 'Reports have not been checked.',
      detail: 'Choose the reports this model should be checked against.',
      action: 'Choose reports',
    };
  if (scope.needsChecking > 0 && scope.checked === 0)
    return {
      headline: 'Needs checking again.',
      detail: `The model changed after ${names(named(scope, 'needsChecking'))} was last read${when ? ` at ${opts.time(when)}` : ''}. A saved choice and an old time cannot stand in for a fresh check.`,
      action: 'Check again',
    };
  if (scope.checked === scope.listed && scope.listed > 0) {
    const source = sharedSource(scope);
    return {
      headline: `All ${plural(scope.listed, 'listed report')} checked${source ? ` in ${source}` : ''}.`,
      detail: `${names(named(scope, 'checked'))}, read${when ? ` at ${opts.time(when)}` : ''}. This says nothing about other workspaces or report files.`,
      action: 'Choose reports',
    };
  }
  // Astra's exact wording for the partly-read case, where the only two states are a clean read and an
  // incomplete one: repeating "report" twice reads as two different populations, which it is not.
  const onlyPartial = scope.checked > 0 && scope.couldNotBeFullyChecked > 0 && scope.notChecked === 0 && scope.needsChecking === 0;
  const parts: string[] = [];
  if (onlyPartial) parts.push(`${scope.checked} checked`, `${scope.couldNotBeFullyChecked} could not be fully checked`);
  if (!onlyPartial && scope.checked > 0) parts.push(`${plural(scope.checked, 'report')} checked`);
  if (!onlyPartial && scope.couldNotBeFullyChecked > 0) parts.push(`${plural(scope.couldNotBeFullyChecked, 'report')} could not be fully checked`);
  if (scope.notChecked > 0) parts.push(`${plural(scope.notChecked, 'listed report')} not checked`);
  if (scope.needsChecking > 0) parts.push(`${plural(scope.needsChecking, 'report')} needing checking again`);
  if (parts.length === 0) parts.push(`${plural(scope.listed, 'listed report')} not checked`);
  const detail: string[] = [];
  if (scope.checked > 0) detail.push(`${names(named(scope, 'checked'))} read${when ? ` at ${opts.time(when)}` : ''}`);
  if (scope.couldNotBeFullyChecked > 0) detail.push(`${names(named(scope, 'couldNotBeFullyChecked'))} could not be fully checked`);
  if (scope.notChecked > 0) detail.push(`${names(named(scope, 'notChecked'))} ${scope.notChecked === 1 ? 'was' : 'were'} not checked`);
  if (scope.needsChecking > 0) detail.push(`${names(named(scope, 'needsChecking'))} ${scope.needsChecking === 1 ? 'needs' : 'need'} checking again`);
  return { headline: parts.join('; ') + '.', detail: detail.join(' · ') + '.', action: 'Choose reports' };
}

/** The short count at the end of the reports row. */
export function reportsRowCount(scope: ReportScope): string {
  if (!scope || scope.listed === 0) return 'not checked';
  if (scope.checked === 0) return `none of ${scope.listed} listed checked`;
  if (scope.checked === scope.listed) return `all ${scope.listed} listed checked`;
  return `${scope.checked} of ${scope.listed} listed checked`;
}

/**
 * What the field's own answer says about reports. `hits` is the NAMES of the checked reports that were found
 * to use the field or something that depends on it.
 *
 * Every sentence here is pinned by docs/report-scope-sentences.json, which the engine's own formatter is held
 * to independently. Astra's ruling 2 on the first build: the two doors already meant different things about a
 * stale reading, and this side named EVERY checked report in a hit sentence when only some contained the
 * field, which told a person that a report which does not use it does. The time is deliberately not in these
 * sentences: it belongs to the reports row, which says it once, and a clock string is the one thing the two
 * doors cannot sensibly compare.
 */
export function noUseSentence(name: string, scope: ReportScope, hits: string[] | number, _opts?: TimeOpts): string {
  const hitNames = Array.isArray(hits) ? hits.filter(Boolean) : [];
  const hitCount = Array.isArray(hits) ? hitNames.length : (hits ?? 0);
  if (!scope || scope.listed === 0) return 'Report use is unknown. No reports have been chosen for this model yet.';
  // A reading the model has moved past is NOT a reading that never happened: different fact, different next step.
  if (scope.checked === 0 && scope.couldNotBeFullyChecked === 0 && scope.needsChecking > 0)
    return `Report use needs checking again. The model changed after ${scope.needsChecking === 1 ? '1 listed report was' : `${scope.needsChecking} listed reports were`} read.`;
  if (scope.checked === 0 && scope.couldNotBeFullyChecked === 0)
    return 'Report use is unknown. The chosen reports still need checking.';

  const tail: string[] = [];
  if (scope.notChecked > 0) tail.push(`${scope.notChecked === 1 ? '1 listed report was' : `${scope.notChecked} listed reports were`} not checked`);
  if (scope.couldNotBeFullyChecked > 0) tail.push(`${scope.couldNotBeFullyChecked === 1 ? '1 listed report' : `${scope.couldNotBeFullyChecked} listed reports`} could not be fully checked`);
  if (scope.needsChecking > 0) tail.push(`${scope.needsChecking === 1 ? '1 listed report needs' : `${scope.needsChecking} listed reports need`} checking again`);
  const suffix = tail.length ? ` ${tail.join('; ')}.` : '';

  if (hitCount === 0) {
    const read = named(scope, 'checked');
    return `No use of ${name} or the things that depend on it was found in the `
      + `${scope.checked === 1 ? '1 report checked' : `${scope.checked} reports checked`}`
      + (read.length ? ` (${names(read)})` : '') + '.' + suffix;
  }
  const others = scope.checked - hitCount;
  const otherSentence = others <= 0 ? ''
    : ` The other ${others === 1 ? 'checked report does' : `${others} checked reports do`} not use it.`;
  return `${name} or something that depends on it appears in `
    + `${hitCount === 1 ? '1 checked report' : `${hitCount} checked reports`}`
    + (hitNames.length ? ` (${names(hitNames)})` : '') + '.' + otherSentence + suffix;
}

// ---- 2. the strip ----------------------------------------------------------------------------------
/** "Checked: model - 1 of 3 listed reports (9:41 AM) - Choose reports". The replacement for the yellow
 * model-only banner: it says what the answer covers, not what it does not. */
export function checkedStrip(scope: ReportScope, opts: TimeOpts): { model: string; reports: string; when: string; action: string } {
  const when = latestCheck(scope);
  return {
    model: 'Checked: model',
    // A stale reading is not a count: the one line most people read has to carry the staleness, or it says
    // "0 of 1 listed reports" and nothing about the reason.
    reports: !scope || scope.listed === 0 ? 'no reports checked'
      : scope.needsChecking > 0 && scope.checked === 0 ? `${plural(scope.needsChecking, 'listed report')} needs checking again`
      : scope.checked === 0 ? `0 of ${scope.listed} listed reports`
      : `${scope.checked} of ${scope.listed} listed reports`,
    when: scope && scope.checked > 0 && when ? `(${opts.time(when)})` : '',
    action: 'Choose reports',
  };
}

// ---- 3. what uses it -------------------------------------------------------------------------------
const KIND_ORDER = ['measure', 'column', 'calcColumn', 'relationship', 'table', 'hierarchy', 'calcitem'];
const GROUP_OF: Record<string, string> = { measure: 'measures', column: 'columns', calcColumn: 'columns', relationship: 'relationships', table: 'tables' };
const GROUP_LABEL: Record<string, string> = { measures: 'Measures', columns: 'Columns', relationships: 'Relationships', tables: 'Tables', other: 'Other' };
const GROUP_ORDER = ['measures', 'columns', 'relationships', 'tables', 'other'];

/** "3 measures - 1 column - 1 relationship", or "nothing" when the list is empty. */
export function modelRowCounts(impact: ImpactShape): string {
  if (!impact) return 'nothing';
  const parts: string[] = [];
  if (impact.measures > 0) parts.push(plural(impact.measures, 'measure'));
  if (impact.columns > 0) parts.push(plural(impact.columns, 'column'));
  if (impact.tables > 0) parts.push(plural(impact.tables, 'table'));
  if (impact.relationships > 0) parts.push(plural(impact.relationships, 'relationship'));
  if (impact.other > 0) parts.push(plural(impact.other, 'other thing'));
  return parts.length === 0 ? 'nothing' : parts.join(' · ');
}

/**
 * The answer, as a sentence. The engine writes the same sentence for the assistant
 * (ImpactAssessmentBuilder.Summary); this one exists because the card has to say it the instant a field is
 * picked, before the assessment comes back. Same rule, same words, so the first paint and the answer that
 * follows it cannot disagree.
 */
export function verdictSentence(impact: ImpactShape, intent: 'change' | 'rename' | 'remove' | 'restructure' = 'change'): string {
  if (!impact) return '';
  const name = impact.rootName ?? 'this field';
  const parts: string[] = [];
  if (impact.measures > 0) parts.push(plural(impact.measures, 'measure'));
  if (impact.columns > 0) parts.push(plural(impact.columns, 'column'));
  if (impact.tables > 0) parts.push(plural(impact.tables, 'table'));
  if (impact.relationships > 0) parts.push(plural(impact.relationships, 'relationship'));
  if (impact.other > 0) parts.push(plural(impact.other, 'other thing'));
  if (parts.length === 0) return `Nothing in the model uses ${name}.`;
  const list = parts.length === 1 ? parts[0] : parts.slice(0, -1).join(', ') + ' and ' + parts[parts.length - 1];
  return intent === 'remove' ? `Deleting ${name} would break ${list}.` : `${name} is used by ${list}.`;
}

/** How directly this thing uses the field, in words a person can act on. Never a depth number. */
export function directness(n: ImpactNode): string {
  if (!n) return '';
  if (n.kind === 'calcColumn' && n.depth === 1) return 'calculated from it';
  if (n.kind === 'relationship') return 'relationship';
  if (n.depth <= 1) {
    if (n.via === 'sortBy') return 'sorts by it';
    if (n.via === 'hierarchy') return 'in a hierarchy of it';
    if (n.via === 'contains') return 'in this table';
    if (n.via === 'rls') return 'a security rule uses it';
    return 'directly';
  }
  return n.viaName ? `via ${n.viaName}` : 'further down the chain';
}

const rank = (n: ImpactNode) => n.depth * 100 + Math.max(0, KIND_ORDER.indexOf(n.kind));

/** Every dependant, grouped by kind and sorted by how directly it uses the field. */
export function dependantGroups(impact: ImpactShape): { id: string; label: string; items: ImpactNode[] }[] {
  const buckets = new Map<string, ImpactNode[]>();
  for (const n of impact?.impacted ?? []) {
    const id = GROUP_OF[n.kind] ?? 'other';
    const list = buckets.get(id); if (list) list.push(n); else buckets.set(id, [n]);
  }
  return GROUP_ORDER.filter((id) => buckets.has(id)).map((id) => {
    const items = buckets.get(id)!.slice().sort((a, b) => rank(a) - rank(b) || a.name.localeCompare(b.name));
    return { id, label: `${GROUP_LABEL[id]} · ${items.length}`, items };
  });
}

/** The few names shown on the row before Show all is pressed: the most direct ones first. */
export function previewNames(impact: ImpactShape, take = 5): ImpactNode[] {
  return (impact?.impacted ?? []).slice().sort((a, b) => rank(a) - rank(b) || a.name.localeCompare(b.name)).slice(0, take);
}

/** One page of a long list. Kane's amendment: every dependant is reachable, so the list pages rather than
 * stopping at a cap with "and N more", which is a number you cannot open. */
export const PAGE_SIZE = 100;
export function pageOf<T>(items: T[], page: number, size = PAGE_SIZE): T[] {
  return (items ?? []).slice(0, Math.max(0, (page + 1)) * size);
}
/** How many are still beyond the page shown, so the control can say what pressing it gets you. */
export function remaining<T>(items: T[], page: number, size = PAGE_SIZE): number {
  return Math.max(0, (items?.length ?? 0) - (page + 1) * size);
}

export function showAllLabel(impact: ImpactShape): string {
  return `Show all ${impact?.impacted?.length ?? 0}`;
}

// ---- 4. removal ------------------------------------------------------------------------------------
/** Why Propose removal is blocked, or null when nothing uses it. Names what has to change first. */
export function removalBlock(impact: ImpactShape): string | null {
  const total = impact?.impacted?.length ?? 0;
  if (total === 0) return null;
  const kinds = new Set((impact.impacted ?? []).map((n) => GROUP_OF[n.kind] ?? 'other'));
  const noun = kinds.size === 1
    ? plural(total, [...kinds][0].replace(/s$/, ''))
    : plural(total, 'thing');
  const it = impact.rootKind === 'measure' || impact.rootKind === 'column' || impact.rootKind === 'calcColumn' ? 'this field' : 'it';
  return total === 1
    ? `${noun} needs ${it}. Update or remove it before deleting it.`
    : `These ${noun} need ${it}. Update or remove them before deleting it.`;
}

export const PROPOSE_NOTE = 'This adds a proposal to Changes > Proposed. The field stays until you apply the proposal.';

// ---- 5. cleanup ------------------------------------------------------------------------------------
/** The three groups. The engine's verdict tokens never reach the screen; the label says what was
 * established, and none of them says "safe". */
export const CLEANUP_GROUPS: { id: string; verdict: string; label: string }[] = [
  { id: 'noUse', verdict: 'safe', label: 'No use found in these checks' },
  { id: 'used', verdict: 'usedByUnusedOnly', label: 'Still used' },
  { id: 'check', verdict: 'caution', label: 'Needs checking' },
];

export function cleanupGroups(items: CleanupItem[]): { id: string; label: string; items: CleanupItem[] }[] {
  return CLEANUP_GROUPS
    .map((g) => ({ id: g.id, label: g.label, items: (items ?? []).filter((i) => i.verdict === g.verdict) }))
    .filter((g) => g.items.length > 0);
}

/** "4 candidates: 2 with no use found, 1 still used, 1 needing review - checked against the model and
 * 1 of 3 listed reports". Every number is one this page established. */
export function cleanupHeadline(items: CleanupItem[], scope: ReportScope): string {
  const list = items ?? [];
  const basis = `checked against the model and ${!scope || scope.listed === 0 ? 'no reports'
    : scope.checked === 0 ? `none of ${scope.listed} listed reports`
    : `${scope.checked} of ${scope.listed} listed reports`}`;
  if (list.length === 0) return `No cleanup candidates · ${basis}`;
  const parts = [
    `${list.filter((i) => i.verdict === 'safe').length} with no use found`,
    `${list.filter((i) => i.verdict === 'usedByUnusedOnly').length} still used`,
    `${list.filter((i) => i.verdict === 'caution').length} needing review`,
  ];
  return `${plural(list.length, 'candidate')}: ${parts.join(', ')} · ${basis}`;
}

export function proposeSelectedLabel(n: number): string {
  return `Propose removing ${n} selected`;
}

export const CLEANUP_CAVEAT =
  'A report that was not checked could still use one of these. The proposal is rechecked against the same reports when you apply it.';

// ---- 6. add a check --------------------------------------------------------------------------------
export type AddCheckPlan =
  | { kind: 'measure'; ref: string; name: string }
  | { kind: 'choose'; prompt: string; options: { ref: string; name: string; table?: string }[]; tableRowCount?: string };

/**
 * A saved check runs one measure. A measure therefore has a real handoff. A column does not, so it asks
 * which of the measures that depend on it the check should use, and offers its table's row count when
 * there is no measure to pick. It never pushes a column into a measure-only form, and never drops it.
 */
export function addCheckPlan(impact: ImpactShape): AddCheckPlan {
  if (impact?.rootKind === 'measure' && impact.root)
    return { kind: 'measure', ref: impact.root, name: impact.rootName ?? impact.root };
  const options = (impact?.impacted ?? [])
    .filter((n) => n.kind === 'measure')
    .slice().sort((a, b) => rank(a) - rank(b) || a.name.localeCompare(b.name))
    .map((n) => ({ ref: n.ref, name: n.name, table: n.table }));
  const table = impact?.root && impact.root.includes(':') ? impact.root.split(':')[1].split('/')[0] : undefined;
  return { kind: 'choose', prompt: 'A check runs one measure. Which measure should this check use?', options, tableRowCount: table };
}

// ---- 7. where an old link lands --------------------------------------------------------------------
/** Safe to remove and Published reports are gone as sub-tabs. A link to either still has to land on
 * something real: the cleanup view, and the Choose reports drawer. */
export function landingFor(mode: string): { mode: 'graph' | 'tree' | 'impact'; cleanup?: boolean; drawer?: boolean } {
  if (mode === 'unused') return { mode: 'impact', cleanup: true };
  if (mode === 'reports') return { mode: 'impact', drawer: true };
  if (mode === 'graph' || mode === 'tree' || mode === 'impact') return { mode: mode as 'graph' | 'tree' | 'impact' };
  return { mode: 'impact' };
}
