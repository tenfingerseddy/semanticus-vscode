import { requestFirstRun, type FirstRunState } from './bridge';

// One question to the host per page, shared by the first-run choice and the Plan's tour. The tour must not open
// over the choice (two cards, two grabs at the keyboard), so it waits here until the choice has been closed.

let asked: Promise<FirstRunState | null> | null = null;
let closed = false;
let waiting: Array<() => void> = [];

export function firstRunOnce(): Promise<FirstRunState | null> {
  if (!asked) asked = requestFirstRun();
  return asked;
}

export function markFirstRunClosed(): void {
  closed = true;
  const go = waiting; waiting = [];
  for (const f of go) f();
}

/** Resolves at once when the choice will not show, or when it has been closed. */
export async function whenFirstRunClosed(): Promise<void> {
  const state = await firstRunOnce();
  if (!state?.show || closed) return;
  await new Promise<void>((resolve) => { waiting.push(resolve); });
}

/** Leave from the tour returns to the first-run card, but only for a person who came to the tour from the card and has
 *  not connected yet. A tour started from Help, a card that is already open, and a connected person get nothing. */
export function returnsToCard(s: { fromCard: boolean; phase: 'idle' | 'starting' | 'running' | 'failed'; connected: boolean; open: boolean }): boolean {
  return s.fromCard && s.phase === 'idle' && !s.connected && !s.open;
}
