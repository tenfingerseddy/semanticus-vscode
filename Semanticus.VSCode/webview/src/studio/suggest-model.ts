// The suggestion line's rules (1.4, contract section 6). Pure, so test/studio-notes.test.mjs holds every limit.
import { DEFAULT_SUGGEST_SAY, type StudioStep, type StudioSuggestion } from './types';

/** The limits suggest_next enforces on the engine side; the Studio draws only what meets them. */
export const STEPS_MIN = 2;
export const STEPS_MAX = 3;
export const LABEL_MIN = 3;
export const LABEL_MAX = 40;
export const PROMPT_MAX = 280;
export const SAY_MAX = 200;

/** What a pressed button says. */
export const ADDED_TEXT = 'Added. Tell Claude go.';
export const FROM_PLAN_PREFIX = 'From your plan: ';
/** The source page the engine knows a suggested step by (PlanTasks.SuggestSourcePage). */
export const SUGGEST_SOURCE_PAGE = 'suggest';

/** The steps to draw: at most three, each with a label of 3 to 40 characters and words to send. The engine already
 *  refuses the rest, so this keeps an older or odd engine from drawing a broken button. */
export function stepsOf(suggestion: Pick<StudioSuggestion, 'steps'> | null | undefined): StudioStep[] {
  const out: StudioStep[] = [];
  for (const step of suggestion?.steps ?? []) {
    const label = (step?.label ?? '').trim();
    const prompt = (step?.prompt ?? '').trim();
    if (label.length < LABEL_MIN || label.length > LABEL_MAX || !prompt || prompt.length > PROMPT_MAX) continue;
    out.push({ label, prompt });
    if (out.length === STEPS_MAX) break;
  }
  return out;
}

/** The lead line above the buttons. */
export function leadOf(suggestion: Pick<StudioSuggestion, 'say'> | null | undefined): string {
  const say = (suggestion?.say ?? '').trim();
  return say && say.length <= SAY_MAX ? say : DEFAULT_SUGGEST_SAY;
}

/** Semanticus's own fourth line: the top row of the Plan, unless a step's words already name that row by its id or title. */
export function fromYourPlan(steps: readonly StudioStep[], top: { id?: string; title?: string } | null | undefined): string | null {
  const title = (top?.title ?? '').trim();
  if (!title) return null;
  const id = (top?.id ?? '').trim().toLowerCase();
  const lower = title.toLowerCase();
  const named = steps.some((s) => {
    const words = s.prompt.toLowerCase();
    return words.includes(lower) || (id !== '' && words.includes(id));
  });
  return named ? null : FROM_PLAN_PREFIX + title;
}

/** What pressing a step sends to the engine: the step's own words as a task for the assistant, from the suggest source.
 *  The send key makes a second press of the same step on the same suggestion the same task. */
export function taskRequest(suggestion: Pick<StudioSuggestion, 'id'>, index: number, step: StudioStep) {
  return {
    words: step.prompt,
    doer: 'assistant' as const,
    sourcePage: SUGGEST_SOURCE_PAGE,
    sendKey: `suggest:${suggestion.id}:${index}`,
  };
}
