// The next step's rules on the Studio side (1.5, next-step lane). Pure, so test/next-line.test.mjs holds every word and every rule.
// The engine chooses the step (rules, Jev or the assistant); the Plan header's slot only draws what NextStepView says.
import type { NextStepStep, NextStepView } from './types';

export const ADDED_TEXT = 'Added. Tell Claude go.';
export const ASK_LABEL = 'Ask your assistant';
export const DO_LABEL = 'Do it';
export const NEXT_SOURCE_PAGE = 'suggest';
export const FAILED_TEXT = 'That did not work. Try again.';

export type LineButton = { act: 'do' | 'ask'; label: string; primary: boolean };
/** "Do it" comes first and is primary only when Semanticus can do the step alone; otherwise "Ask your assistant" is. */
export function buttonsOf(step: Pick<NextStepStep, 'doIt'>): LineButton[] {
  return step.doIt
    ? [{ act: 'do', label: DO_LABEL, primary: true }, { act: 'ask', label: ASK_LABEL, primary: false }]
    : [{ act: 'ask', label: ASK_LABEL, primary: true }];
}

/** What "Ask your assistant" sends: the step's words as a task for the assistant, keyed so a second press is the same task. */
export function askRequest(view: Pick<NextStepView, 'id'>, step: Pick<NextStepStep, 'prompt' | 'ask'>) {
  return {
    words: (step.prompt ?? '').trim() || step.ask,
    doer: 'assistant' as const,
    sourcePage: NEXT_SOURCE_PAGE,
    sendKey: `next:${view.id}`,
  };
}

/** The engine call behind "Do it", by op. Null for an op this Studio does not know (then no "Do it" is drawn). */
export type DoItCall = { kind: 'save' } | { kind: 'rpc'; method: string; params: unknown[] };
export function doItCall(op: string, step: Pick<NextStepStep, 'prompt' | 'ask'>): DoItCall | null {
  if (op === 'save') return { kind: 'save' };
  if (op === 'run_checks') return { kind: 'rpc', method: 'runTests', params: [false, 'human', null, null] };
  if (op === 'draft_workflow') return { kind: 'rpc', method: 'composeTask', params: [(step.prompt ?? '').trim() || step.ask] };
  return null;
}
