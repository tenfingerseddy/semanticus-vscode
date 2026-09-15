export type Area = 'model' | 'calc' | 'checks' | 'changes' | 'workflows';
export type Destination = Area | 'utility';
export type ToolId =
  | 'modelhome' | 'diagram' | 'lineage' | 'search' | 'data' | 'stats' | 'spec' | 'advmodels' | 'mcode' | 'docs' | 'knowledge'
  | 'daxlab' | 'tests' | 'bpa' | 'readiness' | 'evidence' | 'optimize' | 'history' | 'deploy' | 'compare' | 'dataagent' | 'permissions' | 'workflows';

export type ObjectRef =
  | { kind: 'model' }
  | { kind: 'table'; table: string }
  | { kind: 'measure' | 'column' | 'calcColumn' | 'hierarchy' | 'partition' | 'calcitem'; table: string; name: string }
  | { kind: 'relationship'; id: string }
  | { kind: 'role' | 'perspective' | 'function'; name: string };

// A seed records an explicit hand-off, not ambient selection. Views acknowledge one-use seeds after adopting them so
// returning through area memory or Back cannot replay an old request over work the person has since changed.
export type Seed = {
  kind: 'query' | 'test' | 'search' | 'publish' | 'rescan' | 'addTables' | 'approval' | 'workflow' | 'workflowRuns' | 'advanced';
  value: string | string[];
};
export interface Route { sessionId: string; destination: Destination; tool: ToolId; object?: ObjectRef; seed?: Seed; nonce: number; }

export const DESTINATION_OF_TOOL: Record<ToolId, Destination> = {
  modelhome: 'model', diagram: 'model', lineage: 'model', search: 'model', data: 'model', stats: 'model', spec: 'model', advmodels: 'model', mcode: 'model', docs: 'model', knowledge: 'model',
  daxlab: 'calc', tests: 'checks', bpa: 'checks', readiness: 'checks', evidence: 'checks',
  optimize: 'changes', history: 'changes', deploy: 'changes', compare: 'changes', dataagent: 'changes',
  permissions: 'utility', workflows: 'workflows',
};

export const AREA_HOME: Record<Area, ToolId> = { model: 'modelhome', calc: 'daxlab', checks: 'tests', changes: 'history', workflows: 'workflows' };

// Where an area button lands. Four areas have one fixed home; Changes does not, because its home is whichever
// of its tabs holds the work in front of you. With a change plan open that is Proposed; with none it is History.
// The area strip reads Proposed / History / Published, so always landing on the middle tab read as a bug.
export function areaHomeTool(area: Area, hasPlan = false): ToolId {
  if (area === 'changes') return hasPlan ? 'optimize' : AREA_HOME[area];
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
