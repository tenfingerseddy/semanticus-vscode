// The data answer the assistant asked for (ask_data), kept for the Ask page (1.4 follow). The engine broadcasts studio/answer when the
// assistant's question is answered; the layer, which is always mounted, takes it here, so a page that mounts a moment later (the note that
// moved the screen to Ask your data is still opening it) still finds it. Memory only: the rows are the model's data, so an answer older than
// a minute is not offered to a page that opens later.
import { useSyncExternalStore } from 'react';
import type { StudioAnswerPayload } from '../bridge';

/** How long an answer waits for a page that has not opened yet. */
export const ANSWER_FRESH_MS = 60_000;

let current: StudioAnswerPayload | null = null;
const listeners = new Set<() => void>();

export function setAssistantAnswer(next: StudioAnswerPayload | null): void {
  if (current?.id === next?.id) return;
  current = next;
  listeners.forEach((fn) => fn());
}
export function getAssistantAnswer(): StudioAnswerPayload | null { return current; }
export function subscribeAssistantAnswer(fn: () => void): () => void {
  listeners.add(fn);
  return () => { listeners.delete(fn); };
}
export function useAssistantAnswer(): StudioAnswerPayload | null {
  return useSyncExternalStore(subscribeAssistantAnswer, getAssistantAnswer, getAssistantAnswer);
}

/** Whether a page for this model may show the answer now: it is for this model, and it is not old. */
export function answerIsFor(answer: StudioAnswerPayload | null, sessionId: string | undefined, now = Date.now()): answer is StudioAnswerPayload {
  if (!answer) return false;
  if (answer.sessionId && sessionId && answer.sessionId !== sessionId) return false;
  const at = Date.parse(answer.at);
  return !Number.isFinite(at) || now - at <= ANSWER_FRESH_MS;
}
