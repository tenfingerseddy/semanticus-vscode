// Working verbs for the status line (1.5, design page section 2). While Claude or Semanticus works, the status line names what is
// happening with a verb from the world of models. The verb follows the work: a DAX job shows DAX verbs, Jev shows Jev verbs.
// Personality Quiet shows the plain words instead. Humour lives only here, while waiting: never in an error or a question.

export type VerbGroup = 'Modelling' | 'DAX' | 'Relationships' | 'Readiness' | 'Publishing' | 'Jev';

export const VERBS: Record<VerbGroup, readonly string[]> = {
  Modelling: ['Starring the schema', 'Hiding the keys', 'Sorting months properly', 'Tidying display folders', 'Naming things carefully',
    'Formatting the numbers', 'Marking the date table', 'Pruning unused columns', 'Folding the snowflake', 'Laying out the tables'],
  DAX: ['CALCULATE-ing', 'Shifting filter context', 'SUMX-ing row by row', 'Declaring VARs', 'RETURNing the favour',
    'Removing filters, ALL of them', 'KEEPFILTERS-ing', 'Transitioning context', 'DIVIDE-ing safely', 'SWITCHing it up',
    'Iterating thoughtfully', 'Measuring twice'],
  Relationships: ['Joining the dots', 'Finding the many side', 'Introducing Sales to Date', 'Matching keys', 'Picking a filter direction',
    'Settling cardinality', 'Untangling a many-to-many', 'Drawing one clean line', 'Following the filter', 'Making a star'],
  Readiness: ['Reading the model', 'Grading gently', 'Writing descriptions', 'Teaching Copilot the words', 'Adding synonyms',
    'Explaining the obvious, kindly', 'Checking best practice', 'Labelling the fields', 'Asking what Copilot would ask', 'Polishing for AI'],
  Publishing: ['Drawing the S', 'Packing the model', 'Checking the workspace', 'Sending to Fabric', 'Asking the service nicely',
    'Refreshing the data', 'Reconnecting the dots', 'Landing the end dot'],
  Jev: ['Jev is weighing it', 'Jev is comparing candidates', 'Jev is ranking options', 'Jev is nearly sure', 'Jev is settling',
    'Jev is checking its sums'],
};

/** What Quiet people get: the plain fact, no joke. */
export const PLAIN: Record<VerbGroup, string> = {
  Modelling: 'Editing the model', DAX: 'Writing DAX', Relationships: 'Editing relationships', Readiness: 'Improving readiness',
  Publishing: 'Publishing', Jev: 'Jev is choosing',
};
export const PLAIN_WORKING = 'Working';
export const DEFAULT_GROUP: VerbGroup = 'Readiness';

/** What the status line says once work has shown no sign of progress for STUCK_MS: the plain fact of what it waits on (Kane, 16 Oct). */
export const WAITING: Record<VerbGroup, string> = {
  Modelling: 'Waiting for the model to finish the edit', DAX: 'Waiting for the query to answer', Relationships: 'Waiting for the relationship check',
  Readiness: 'Waiting for the check to finish', Publishing: 'Waiting for the service to answer', Jev: 'Waiting for Jev to finish this step',
};
export const WAITING_DEFAULT = 'Waiting for the answer';

/** How long work may run without a sign of progress before the status line stops joking and says what it waits on (hero 24 D8; a few seconds, Kane 16 Oct). */
export const STUCK_MS = 6_000;

/** How long each verb stays: calm, never a flicker. */
export const VERB_HOLD_MS = 2300;

// Tool names are the MCP tools (docs/mcp-surface-inventory.json). Specific names first, then families by shape.
// A read (get_, list_, search_) names no group: it keeps whatever the job was already doing.
const EXACT: Record<string, VerbGroup> = {
  create_relationship: 'Relationships', set_relationship_active: 'Relationships', set_relationship_cardinality: 'Relationships',
  set_relationship_crossfilter: 'Relationships', get_lineage: 'Relationships', impact_assessment: 'Relationships', get_model_graph: 'Relationships',
  mark_date_table: 'Modelling', generate_date_table: 'Modelling', set_sort_by_column: 'Modelling', set_display_folder: 'Modelling',
  rename_display_folder: 'Modelling', set_column_hidden: 'Modelling', set_measure_format: 'Modelling', unused_objects: 'Modelling',
  remove_safe_objects: 'Modelling', save_layout: 'Modelling',
  create_measure: 'DAX', create_measures: 'DAX', update_measure: 'DAX', apply_dax_script: 'DAX', create_calculated_column: 'DAX',
  create_calculated_table: 'DAX', create_calculation_group: 'DAX', create_calculation_item: 'DAX', create_function: 'DAX',
  set_measure_format_expression: 'DAX', generate_time_intelligence: 'DAX', propose_measure_defaults: 'Jev', get_decision_points: 'Jev',
  write_words: 'Readiness', set_description: 'Readiness', set_synonyms: 'Readiness', save_model: 'Publishing',
  set_publish_destination: 'Publishing', prepare_working_copy: 'Publishing', refresh_plan: 'Jev',
};
const PREFIX: [string, VerbGroup][] = [
  ['daxlib_', 'DAX'], ['deploy_', 'Publishing'], ['fabric_git_', 'Publishing'], ['git_', 'Publishing'], ['cicd_', 'Publishing'],
  ['publish_', 'Publishing'], ['rollback_', 'Publishing'], ['refresh_partition', 'Publishing'], ['connect_', 'Publishing'],
  ['ai_readiness', 'Readiness'], ['reading_card', 'Readiness'], ['fix_them', 'Readiness'], ['make_model_ai_ready', 'Readiness'], ['apply_fix', 'Readiness'], ['apply_safe_fixes', 'Readiness'],
  ['bpa_', 'Readiness'], ['waive_', 'Readiness'], ['set_ai_', 'Readiness'], ['create_linguistic', 'Readiness'], ['run_tests', 'Readiness'],
  ['run_interview', 'Readiness'], ['try_test', 'Readiness'], ['autogenerate_spec', 'Readiness'], ['set_doc_section', 'Readiness'],
  ['generate_data_agent', 'Readiness'],
];
const DAX_WORDS = /(^|_)(dax|query|measure|benchmark|profile|evaluate|explain_value|blame_value|reconcile|pivot|probe_measure|vpaq|optimize|ask_data|preview_table|run_dmv|capture_query_plan|verify_dax|clear_cache)(_|$)/;
const READ = /^(get_|list_|search_|connection_status|model_overview|script_objects|describe_|recall_|harness_report)/;

/** The verb group for one MCP tool call, or null for a read that should keep the job's group. */
export function groupForTool(tool: string | null | undefined): VerbGroup | null {
  const t = (tool ?? '').trim().toLowerCase();
  if (!t) return null;
  if (EXACT[t]) return EXACT[t];
  for (const [p, g] of PREFIX) if (t.startsWith(p)) return g;
  if (DAX_WORDS.test(t)) return 'DAX';
  if (READ.test(t)) return null;
  return 'Modelling';
}

/** The verb group for a Studio surface the person is running (tabbusy.mjs PRESERVED_SURFACES), or an engine progress key. */
export function groupForSurface(surface: string): VerbGroup {
  if (surface === 'query' || surface === 'daxlab') return 'DAX';
  if (surface === 'lineage') return 'Relationships';
  if (surface === 'plan') return 'Readiness';
  return groupForTool(surface) ?? 'Modelling';
}

/**
 * The words for one moment of the status line. `step` counts the verbs shown in this job; `seed` picks where a job starts in its
 * group, so two jobs in a row do not open on the same verb. Quiet always says the plain words.
 */
export function verbFor(group: VerbGroup | null, step: number, quiet: boolean, seed = 0): string {
  if (!group) return PLAIN_WORKING;
  if (quiet) return PLAIN[group];
  const list = VERBS[group];
  const i = ((seed + step) % list.length + list.length) % list.length;
  return list[i];
}
