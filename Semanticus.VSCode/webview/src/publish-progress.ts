// What the page says while a publish to the live model runs: the step in plain words and how long it has taken.
//
// A publish took minutes and the page said only "deploy live". The engine now tells the page each step as it starts
// (progress/didChange, opKey deploy_live: "Reading the live model", "Sending 85 changes"), and the page counts the seconds itself.
// A finished run sends an event with no words, and the line goes away.
export const DEPLOY_PROGRESS_KEY = 'deploy_live';

export interface DeployProgressEvent { opKey: string; runId: string; done: number; total: number; note?: string | null }
export interface DeployStepNow { runId: string; done: number; words: string; since: number }

/** Fold one engine event into the step now running. `now` is the page clock in ms. Null means nothing is running. */
export function foldDeployProgress(current: DeployStepNow | null, event: DeployProgressEvent, now: number): DeployStepNow | null {
  if (!event || event.opKey !== DEPLOY_PROGRESS_KEY) return current;
  const words = (event.note ?? '').trim();
  if (!words) return current && current.runId !== event.runId ? current : null;   // that run is over; another run's line stays
  if (current && current.runId === event.runId && event.done < current.done) return current;   // a late event never moves the line back
  const same = current && current.runId === event.runId && current.words === words;
  return { runId: event.runId, done: event.done, words, since: same ? current.since : now };
}

/** 0:42, 1:05, 12:30. Whole seconds, never negative. */
export function elapsedClock(ms: number): string {
  const total = Math.max(0, Math.floor(ms / 1000));
  const m = Math.floor(total / 60);
  const s = total % 60;
  return `${m}:${s < 10 ? '0' : ''}${s}`;
}

/** The one calm line: "Reading the live model, 0:42". */
export function deployStepLine(step: DeployStepNow | null, now: number): string | null {
  return step ? `${step.words}, ${elapsedClock(now - step.since)}` : null;
}

// ---- The red-check line (hero19 D7) --------------------------------------------------------------------------------
// While the assistant waits on the person about a red check, the Publish page says so. The engine sends one activity event
// (kind publish_asking) with the line, and a second one that clears it.
export const PUBLISH_ASK_KIND = 'publish_asking';
export interface PublishAskEvent { kind?: string; ok?: boolean; label?: string }

/** Fold one activity event into the line now showing. Null means nothing waits. Other events change nothing. */
export function foldPublishAsk(current: string | null, event: PublishAskEvent | null | undefined): string | null {
  if (!event || event.kind !== PUBLISH_ASK_KIND) return current;
  if (!event.ok) return null;
  const words = (event.label ?? '').trim();
  return words || current;
}
