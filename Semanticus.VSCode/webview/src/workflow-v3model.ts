// ===================================================================================================
// Author's format-3 fields, the pure part (lane author-gaps, B7). Kept free of React so a node test runs it.
//
// THE SHAPE IS THE ENGINE'S. Each list Author writes (a step's questions, blanks and checks, the workflow's inputs)
// goes back whole with one set_field, and the next change to the same list names this one as what it expects. The
// engine compares the two exactly, so the value sent must be what the engine will read back from the file: every key
// present, and the defaults the reader fills in (a question is text, needed, answered by you; a blank is asked of your
// assistant first; a check runs in the one phase its kind runs in). These builders are that shape, in one place.
// ===================================================================================================

import type { WorkflowEditBlank, WorkflowEditCheck, WorkflowEditQuestion } from './workflowdraft.mjs';

export type Performer = 'semanticus' | 'assistant' | 'person';

/** Who does a step, in the words Author shows (composer contract 10.5: the two tags; a step with no tag is Semanticus's). */
export const WHO_WORDS: Record<string, string> = {
  semanticus: 'Semanticus does it',
  assistant: 'Your assistant does it',
  person: 'You: stop and ask me here',
};

/** When a check runs, by its phase (composer contract 1.7 and 10.5: Checked, Checked once written, Checks after it runs). */
export const RUNS_WHEN: Record<string, string> = {
  before: 'Checked first',
  written: 'Checked once written',
  after: 'After it runs',
};

export function questionValue(q: Partial<WorkflowEditQuestion> & { name: string }): WorkflowEditQuestion {
  const type = q.type || 'text';
  return {
    name: q.name, question: q.question ?? null, type, required: q.required || 'required', by: q.by || 'you',
    values: type === 'enum' ? (q.values ?? []) : [], from: q.from ?? null, jev: q.jev ?? null, default: q.default ?? null,
  };
}

export function blankValue(b: Partial<WorkflowEditBlank> & { name: string }): WorkflowEditBlank {
  return {
    name: b.name, kind: b.kind ?? null, fills: b.fills ?? null, by: b.by || 'assistant', dependsOn: b.dependsOn ?? [],
    maxChars: b.maxChars ?? null, choices: b.choices ?? null, generator: b.generator ?? null,
  };
}

export function checkValue(c: Partial<WorkflowEditCheck> & { kind: string }): WorkflowEditCheck {
  return {
    kind: c.kind, phase: c.phase ?? null, target: c.target ?? null,
    min: c.min == null || c.min === '' ? null : String(c.min), max: c.max == null || c.max === '' ? null : String(c.max),
    of: c.of ?? [], when: c.when ?? null,
  };
}

/** The phase a check kind runs in, from the engine's list of kinds by phase. */
export function phaseOfCheck(kinds: Record<string, string[]>, kind: string): string {
  return Object.entries(kinds).find(([, list]) => list.includes(kind))?.[0] ?? 'after';
}

/** A name not yet taken: "answer", then "answer2", "answer3". */
export function nextName(basis: string, taken: string[]): string {
  if (!taken.includes(basis)) return basis;
  for (let i = 2; ; i++) if (!taken.includes(`${basis}${i}`)) return `${basis}${i}`;
}

/** True when the workflow is format 3, so its steps carry who does them and Author edits the composer fields. */
export function isV3(schemaVersion: number | null | undefined): boolean { return (schemaVersion ?? 0) >= 3; }

interface RunOnceStep {
  performer?: string | null; action?: unknown; brief?: string | null;
  questions?: unknown[] | null; blanks?: unknown[] | null; checks?: unknown[] | null;
}
interface RunOnceDefinition {
  schemaVersion?: number | null; steps?: RunOnceStep[] | null; inputs?: unknown[] | null; assistantNotes?: string | null;
}

/** Run once (Plan-heart contract 21.5): a workflow that runs as a draft is drafted (compose_task with the unsaved text);
 *  any other runs as a guided run (start_workflow with the unsaved text). The rule is the engine's
 *  (WorkflowParser.IsComposed, N3 in lane l9-finish): format 3 with a step Semanticus runs or a person's stop, or any
 *  field only a draft runs, which a draft runs on the assistant's steps too. */
export function runOnceKind(def: RunOnceDefinition | null | undefined): 'draft' | 'guided' {
  if (!def || !isV3(def.schemaVersion)) return 'guided';
  const has = (list: unknown[] | null | undefined) => (list?.length ?? 0) > 0;
  const draftOnly = (def.steps ?? []).some((s) => s.performer === 'semanticus' || s.performer === 'person' || s.action != null
    || has(s.questions) || has(s.blanks) || has(s.checks) || (s.brief ?? null) !== null);
  return draftOnly || has(def.inputs) || (def.assistantNotes ?? null) !== null ? 'draft' : 'guided';
}

/** Each check kind in plain words, for Author's check picker (lane l9-finish). The engine's ids never reach the screen:
 *  a kind this list does not know yet reads as a plain fallback, so a newer engine cannot put an id on screen either. */
export const CHECK_NAMES: Record<string, string> = {
  name_free: 'The name is free',
  key_unique: 'The key has no repeats',
  dates_within: 'The dates fall inside the date table',
  source_matches: 'The source still has these columns',
  valid_dax: 'The formula is valid DAX',
  dax_lint: 'The formula has no warnings',
  refs_within: 'The formula uses only the named tables',
  max_chars: 'The text is short enough',
  no_formula_text: 'The text has no formula in it',
  valid_m: 'The query is well formed',
  choice_member: 'The choice is one of the options',
  result_range: 'The result is in a range',
  row_count: 'The row count is as expected',
  dax_probe: 'The measure gives the number you know',
  bpa_clean: 'It passes the best practice rules',
};

export function checkName(kind: string): string {
  return CHECK_NAMES[kind] ?? 'Another check Semanticus runs';
}
