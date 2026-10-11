// The Connections page's rules, kept apart from the page so a node test can RUN them (slice C, lane C2).
//
// The design is connections-minimal.html (Kane 24 Sep: "connections design approved"): one list of saved models,
// one account button, and a Jobs button for which model does Editing, Tests and queries, and Publish to.
// Contract 19.1 holds the line this file exists to keep: a model that cannot do a job is struck out WITH THE
// ENGINE'S REASON and never hidden, and the page does not re-decide the rule. So nothing below knows what makes a
// model unfit for a job. The reason arrives on the record the engine lists (`testsRefusal`, `publishRefusal`), the
// same sentence the engine throws when the job is attempted, and this file only carries it to the screen.

import type { ConnectionContextModel, ModelConnectionContext } from './connection';

/** One saved model, as list_connections returns it. The two refusals are the engine's, never the page's. */
export interface HubRecord {
  id: string;
  kind: 'xmla' | 'localDesktop' | 'file' | string;
  endpoint: string;
  database?: string;
  modelName?: string;
  tenantId?: string;
  authMode?: string;
  label?: string;
  workingFolder?: string;
  publishConnectionId?: string;
  lastUsedUtc?: string;
  lastAccount?: string;
  useCount?: number;
  /** Why this model cannot answer tests and queries, in the engine's words. Absent when it can. */
  testsRefusal?: string;
  /** Why this model cannot be a publish destination, in the engine's words. Absent when it can. */
  publishRefusal?: string;
}

/** A model's SQL endpoint as the engine reads it back (C1: connection_context.sqlEndpoint, list_model_sql_endpoints). */
export interface SqlEndpointInfo {
  sourceId: string; name?: string; server?: string; database?: string;
  lastTest?: { testedUtc?: string; ok?: boolean } | null;
  missing?: boolean; setBy?: string; setWhenUtc?: string;
}
/** "Found in this model's sources": the Fabric SQL endpoint a reconciliation already works out (C1). */
export interface SqlEndpointSuggestion { server: string; database: string; matchingSourceId?: string; matchingSourceName?: string }
/** One row of list_model_sql_endpoints. */
export interface ModelEndpointRow { modelName?: string; connectionId?: string; sqlEndpoint?: SqlEndpointInfo | null }
/** A saved source no model is linked to, listed once as "Not linked to a model" (contract 19.3). */
export interface NotLinkedSource {
  id: string; name: string; server?: string; database?: string; checksUsing?: number; tableMappingsUsing?: number;
  /** What the counts cover. Only 'savedModels' (every saved model's checks and mappings, open or not) proves a zero. */
  usageScope?: string;
  /** The saved models whose checks or table mappings use it, by name. */
  usingModels?: string[];
}
export interface ModelEndpointList { rows?: ModelEndpointRow[]; notLinked?: NotLinkedSource[]; note?: string }

/** The context the page reads: today's three sides plus what C1 added (the endpoint, the suggestion, the note). */
export type HubContext = ModelConnectionContext & {
  sqlEndpoint?: SqlEndpointInfo | null;
  sqlEndpointSuggestion?: SqlEndpointSuggestion | null;
  note?: string | null;
};

export type Job = 'edit' | 'tests' | 'publish';

/** The three jobs, each with the one line that says what it means (design "Editing, Tests, Publish to"). */
export const JOBS: Array<{ job: Job; label: string; help: string }> = [
  { job: 'edit', label: 'Editing', help: 'The model you change. Any kind: a file, Power BI Desktop, or a workspace model.' },
  { job: 'tests', label: 'Tests and queries', help: 'Where queries and checks run. It must be running: Power BI Desktop or a workspace model.' },
  { job: 'publish', label: 'Publish to', help: 'Where changes go when you publish. A workspace model only. Publishing always shows a review first.' },
];

/**
 * The reason a model cannot do a job, or null when it can. Straight off the engine's record: Editing takes any
 * model, and the other two read the refusal the engine listed. The page never works one out for itself, which is
 * the whole point: when the rule changes in the engine, this screen changes with it and cannot disagree.
 */
export function jobRefusal(r: HubRecord, job: Job): string | null {
  if (job === 'tests') return (r.testsRefusal || '').trim() || null;
  if (job === 'publish') return (r.publishRefusal || '').trim() || null;
  return null;
}

/** The id the context names for one side. Editing a local copy names its record as the SOURCE connection. */
export function sideId(side?: ConnectionContextModel | null): string | null {
  if (!side?.available) return null;
  return side.connectionId || side.sourceConnectionId || null;
}

/** The jobs one saved model holds right now, in the words its row prints. */
export function rowJobs(r: HubRecord, ctx: HubContext | null | undefined): string[] {
  const out: string[] = [];
  if (sideId(ctx?.editing) === r.id) out.push('editing');
  if (sideId(ctx?.querying) === r.id) out.push('tests and queries');
  if (sideId(ctx?.publishing) === r.id) out.push('publish target');
  return out;
}

/** The row's second line: where it lives, its jobs, and its SQL endpoint. Empty parts drop out. */
export function rowSecondLine(where: string, jobs: string[], sqlEndpointName: string | null | undefined): string {
  return [where, jobs.length ? jobs.join(', ') : null, sqlEndpointName ? `SQL endpoint: ${sqlEndpointName}` : null]
    .filter((x): x is string => !!x && !!x.trim()).join(' · ');
}

/** One choice in a job's picker. Struck out when `refusal` is set, never left out. */
export interface JobOption { id: string; name: string; detail: string; refusal: string | null; current: boolean }

export function jobOptions(records: HubRecord[], job: Job, currentId: string | null,
  nameOf: (r: HubRecord) => string, kindWord: (r: HubRecord) => string): JobOption[] {
  return records.map((r) => {
    const refusal = jobRefusal(r, job);
    return { id: r.id, name: nameOf(r), detail: refusal ?? kindWord(r), refusal, current: r.id === currentId };
  });
}

/** The plain word for a model's kind, shown beside it in a picker. */
export function kindWord(r: HubRecord): string {
  if (r.kind === 'file') return 'file on this computer';
  if (r.kind === 'localDesktop') return 'Power BI Desktop';
  return 'workspace model';
}

/** The SQL endpoint the engine has linked to one saved model, if any. */
export function endpointFor(list: ModelEndpointList | null | undefined, connectionId: string): SqlEndpointInfo | null {
  const row = (list?.rows ?? []).find((x) => x.connectionId === connectionId);
  return row?.sqlEndpoint ?? null;
}

/** The one scope whose zero means unused: every saved model's checks and table mappings, open or not. */
export const USAGE_ALL_SAVED_MODELS = 'savedModels';

function usageCounted(s: NotLinkedSource): boolean {
  return s.usageScope === USAGE_ALL_SAVED_MODELS && typeof s.checksUsing === 'number' && typeof s.tableMappingsUsing === 'number';
}

/**
 * A not-linked source may be removed only when nothing counts on it in ANY saved model. A count the engine did not
 * carry is NOT a zero, and neither is a zero that covers only the open model: zero is the sentence a person
 * deletes a source on, so an unknown or partial count keeps Remove shut (Codex on pull request 394, F-265).
 */
export function notLinkedRemovable(s: NotLinkedSource): boolean {
  return usageCounted(s) && (s.checksUsing as number) + (s.tableMappingsUsing as number) === 0;
}

const plural = (n: number, word: string) => `${n} ${word}${n === 1 ? '' : 's'}`;

/** The row's use line, said only as far as the engine counted. */
export function notLinkedUses(s: NotLinkedSource): string {
  if (!usageCounted(s)) return 'What uses it was not counted';
  const checks = s.checksUsing as number, maps = s.tableMappingsUsing as number;
  if (checks + maps === 0) return 'No saved model uses it';
  const models = (s.usingModels ?? []).filter((m) => !!m && !!m.trim());
  return `${plural(checks, 'check')} and ${plural(maps, 'table mapping')}${models.length ? ` in ${models.join(', ')}` : ''} use it`;
}

/**
 * What the Remove confirmation says, or null when it has nothing true to say. It claims nothing uses the source
 * only when every saved model was counted and the count was zero; otherwise Remove is shut and there is no
 * confirmation to show.
 */
export function removeConfirmSentence(s: NotLinkedSource): string | null {
  if (!notLinkedRemovable(s)) return null;
  return 'No saved model uses it as its SQL endpoint, and no check or table mapping in any saved model points at it. A check you point at it later would have to be set up again.';
}

/** How many saved models a make-default moves. The page says this number BEFORE anything moves (contract 19.1). */
export function defaultMoveCount(records: HubRecord[], tenantId: string | undefined, family: string | null,
  familyOf: (authMode?: string) => string | null): number {
  const tenant = (tenantId || '').toLowerCase();
  return records.filter((o) => o.kind === 'xmla' && (o.tenantId || '').toLowerCase() === tenant && familyOf(o.authMode) === family).length;
}

export function defaultMoveSentence(n: number, tenantId: string | undefined): string {
  const where = tenantId ? `the ${tenantId} tenant` : 'this sign-in with no tenant recorded';
  return `${n} saved model${n === 1 ? '' : 's'} on ${where} will open with it from now on.`;
}

/**
 * Where an old section name lands on the one page. Other surfaces still ask for 'setup' (Deploy's "Change publish
 * destination"), 'accounts' and 'sqlsources' (the Tests page), and the extension host's commands send the same
 * names, so each keeps meaning something: the Change panel, the account menu, and the open model's SQL endpoint.
 */
export type HubPanel = 'none' | 'change' | 'account' | 'sql';
export function routeSection(section: string): { view: 'open' | 'history' | 'add' | 'work' | 'desktop' | 'workspace'; panel: HubPanel } {
  switch (section) {
    case 'setup': return { view: 'open', panel: 'change' };
    case 'accounts': return { view: 'open', panel: 'account' };
    case 'sqlsources': return { view: 'open', panel: 'sql' };
    case 'history': case 'add': case 'work': case 'desktop': case 'workspace': return { view: section, panel: 'none' };
    default: return { view: 'open', panel: 'none' };
  }
}

/** The search box shows once the list is long enough to need one (design: more than 8 models). */
export const SEARCH_FROM = 9;

/** Whether the "From a SQL source" form stays on the Connections page when the open model changes. A model that opens ends the start: a form left on the
 *  page would still show the sign-in error of the start that failed before it (plan statement check 3, defect 6). The form stays while the same model is
 *  open, and when it was opened over a model that is already there (it asks first, and the model has not changed). */
export function startFormStaysOpen(formOpen: boolean, wasSessionId: string | null | undefined, nowSessionId: string | null | undefined): boolean {
  if (!formOpen) return false;
  return !(nowSessionId && nowSessionId !== (wasSessionId ?? null));
}
