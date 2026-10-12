// The areas Kane picked on 22 September 2026 (docs/redesign/ui-hierarchy.md). They name where a journey
// STARTS, not everything reachable once you are there: Explore starts at "what is in this model", Edit starts
// at "change something", Review starts at "is it any good". Analyse was renamed Review in the same decision.
// There were seven until the Plan became the heart (slice 11, lane R1): the Overview retired and the Plan is
// the first area and the start page. Every older area word still resolves, in AREA_ALIASES below, so a saved
// route, an old deep link or an old keybinding lands somewhere sensible instead of nowhere.
export type Area = 'plan' | 'explore' | 'edit' | 'review' | 'publish' | 'workflows';
export type Destination = Area | 'utility';
export type ToolId =
  | 'plan' | 'optimize' | 'spec'
  | 'data' | 'askdata' | 'diagram' | 'lineage' | 'stats'
  | 'search' | 'daxlab' | 'knowledge' | 'instructions' | 'advmodels' | 'mcode'
  | 'bpa' | 'readiness' | 'tests' | 'docs' | 'evidence'
  | 'deploy' | 'compare' | 'dataagent' | 'permissions' | 'workflows' | 'pro';

export type ObjectRef =
  | { kind: 'model' }
  | { kind: 'table'; table: string }
  | { kind: 'measure' | 'column' | 'calcColumn' | 'hierarchy' | 'partition' | 'calcitem'; table: string; name: string }
  | { kind: 'relationship'; id: string }
  | { kind: 'role' | 'perspective' | 'function'; name: string };

// A seed records an explicit hand-off, not ambient selection. Views acknowledge one-use seeds after adopting them so
// returning through area memory or Back cannot replay an old request over work the person has since changed.
export type Seed = {
  kind: 'query' | 'test' | 'search' | 'publish' | 'rescan' | 'addTables' | 'approval' | 'workflow' | 'workflowRuns' | 'advanced'
    // A composer draft handed to Author (Edit in Workflows, Make it reusable; lanes U5 and W2): the value is its dr- id.
    | 'draft'
    // Open in Queries from Ask your data (26 Sep): the value is the DAX text the engine built, adopted once.
    | 'daxText'
    // Open Ask your data from the Plan's field: the value is what the person typed, adopted once into the
    // question box so the page opens on their words instead of empty (fix-askdata-r6).
    | 'askWords'
    // The new model wizard's SQL step, asked for by SQL_DRAFT_TARGET below. One use: the draft panel says when it took it.
    | 'specStart'
    // The Plan's start view, asked for by show_in_studio place plan/start (route tool 'plan', target PLAN_START_TARGET): the start plan when
    // there is one, else the start form. One use: the Plan says when it took it.
    | 'planStart';
  value: string | string[];
};

/**
 * THE ONE ENTRY POINT to starting a model from a SQL source: route tool 'spec' with this target. The Plan page opens
 * its draft panel on the wizard's SQL step, which offers the SQL endpoints saved on Connections first. Connections'
 * "From a SQL source", Find a tool and the host command `semanticus.draftFromSqlSource` all use it, and so should any
 * Plan row that offers it (the empty model's offer, lane m11/plan-rows): `onGo('spec', SQL_DRAFT_TARGET)` from a
 * page, `navigateInStudio('spec', SQL_DRAFT_TARGET)` from anywhere else in the webview.
 */
export const SQL_DRAFT_TARGET = 'start:sql';
/** Route tool 'plan' with this target puts the Plan's start view on screen: the start plan, or the start form when there is none yet (place plan/start). */
export const PLAN_START_TARGET = 'start';
export interface Route { sessionId: string; destination: Destination; tool: ToolId; object?: ObjectRef; seed?: Seed; nonce: number; }

export const DESTINATION_OF_TOOL: Record<ToolId, Destination> = {
  // Model Spec and Pending changes are not pages of their own any more: they are the two other ways of
  // looking at the one living plan, so they sit in its area (Kane, 22 September 2026).
  plan: 'plan', optimize: 'plan', spec: 'plan',
  data: 'explore', askdata: 'explore', diagram: 'explore', lineage: 'explore', stats: 'explore',
  search: 'edit', daxlab: 'edit', knowledge: 'edit', instructions: 'edit', advmodels: 'edit', mcode: 'edit',
  // `evidence` keeps its id so old links and saved state still resolve, but it is no longer a place of its
  // own: it opens Validation on its Reports tab. Its area is therefore Validation's area.
  bpa: 'review', readiness: 'review', tests: 'review', docs: 'review', evidence: 'review',
  deploy: 'publish', compare: 'publish', dataagent: 'publish',
  // The Semanticus Pro page is a detour like Assistant permissions: no area of its own, and Done returns you.
  permissions: 'utility', pro: 'utility', workflows: 'workflows',
};

export const AREA_HOME: Record<Area, ToolId> = {
  plan: 'plan', explore: 'data', edit: 'search',
  review: 'bpa', publish: 'deploy', workflows: 'workflows',
};

// Every area word this shell has ever used, pointing at the area that holds that work today. Saved state and old
// deep links outlive a rename, and an unknown word makes the area button do nothing at all, which is how a person
// ends up pressing a button that never responds. The five retired on 22 September 2026 (model, calc, checks,
// changes, workflows) and the five retired before them (understand, change, improve, prove, ship) all land
// somewhere sensible: `changes` goes to Publish because two of its three tools did. `overview` and `model` go to
// the Plan: the Overview retired in slice 11 and the Plan is the start page now (contract 15).
export const AREA_ALIASES: Record<string, Area> = {
  plan: 'plan', explore: 'explore', edit: 'edit', review: 'review', publish: 'publish', workflows: 'workflows',
  overview: 'plan', model: 'plan', calc: 'edit', checks: 'review', changes: 'publish',
  understand: 'explore', change: 'edit', improve: 'review', prove: 'review', ship: 'publish',
};

/** Where a tool id lands: the tool that renders, and for a retired page the view inside it. */
export interface Landing { tool: ToolId; view?: 'history' }

// Tool ids that are not tools any more but still arrive: from a saved link, an older host, an engine one release
// behind, a plan jump or the keyboard. The Overview's jobs moved to the Plan and Edit History became the Plan's
// History view (Plan-heart contract 11 and 15, lanes R1 and R2), so both land there rather than nowhere. Compare
// has been an alias for Published since it folded into that page.
const RETIRED_TOOLS: Record<string, Landing> = {
  modelhome: { tool: 'plan' },
  overview: { tool: 'plan' },
  history: { tool: 'plan', view: 'history' },
  compare: { tool: 'deploy' },
};

/** The one table every navigation goes through. Null only for an id Studio never answered to, which the shell
 *  refuses rather than guessing at (test/retired-routes.test.mjs walks every id that was ever sent). */
export function landingOf(raw: string): Landing | null {
  if (Object.prototype.hasOwnProperty.call(RETIRED_TOOLS, raw)) return RETIRED_TOOLS[raw];
  return Object.prototype.hasOwnProperty.call(DESTINATION_OF_TOOL, raw) ? { tool: raw as ToolId } : null;
}

// Where an area button lands: the first tool in that area's row, every time.
//
// There used to be an exception. Changes read Pending changes / History / Published, its home was History,
// and always landing on the MIDDLE segment read as a bug, so the button landed on Pending changes whenever a
// change set existed. The seven-area rows have no middle-tab home left: Plan opens on Plan, Publish opens on
// Published, and both are the first segment in their own row. So the exception is gone rather than moved, and
// with it the plan read the shell used to make on every session just to place one button.
export function areaHomeTool(area: Area): ToolId {
  return AREA_HOME[area];
}

export function refToObject(ref?: string): ObjectRef | undefined {
  if (!ref) return undefined;
  if (ref === 'model' || ref === 'model:') return { kind: 'model' };
  const colon = ref.indexOf(':');
  if (colon < 1) return undefined;
  const kind = ref.slice(0, colon);
  const rest = ref.slice(colon + 1);
  if (kind === 'table') return rest ? { kind: 'table', table: rest } : undefined;
  if (kind === 'relationship') return rest ? { kind: 'relationship', id: rest } : undefined;
  if (kind === 'role' || kind === 'perspective' || kind === 'function') return rest ? { kind, name: rest } : undefined;
  if (kind === 'measure' || kind === 'column' || kind === 'calcColumn' || kind === 'hierarchy' || kind === 'partition' || kind === 'calcitem') {
    const slash = rest.indexOf('/');
    return slash > 0 && slash < rest.length - 1 ? { kind, table: rest.slice(0, slash), name: rest.slice(slash + 1) } : undefined;
  }
  return undefined;
}

export function objectToRef(object: ObjectRef): string {
  if (object.kind === 'model') return 'model:';
  if (object.kind === 'table') return `table:${object.table}`;
  if (object.kind === 'relationship') return `relationship:${object.id}`;
  if (object.kind === 'role' || object.kind === 'perspective' || object.kind === 'function') return `${object.kind}:${object.name}`;
  if ('table' in object) return `${object.kind}:${object.table}/${object.name}`;
  return 'model:';
}

export function objectLabel(object?: ObjectRef): string | undefined {
  if (!object || object.kind === 'model') return undefined;
  if (object.kind === 'table') return object.table;
  if (object.kind === 'relationship') return object.id;
  return object.name;
}

export function tableOfObject(object?: ObjectRef): string | undefined {
  if (!object) return undefined;
  if (object.kind === 'table' || object.kind === 'measure' || object.kind === 'column' || object.kind === 'calcColumn'
    || object.kind === 'hierarchy' || object.kind === 'partition' || object.kind === 'calcitem') return object.table;
  return undefined;
}
