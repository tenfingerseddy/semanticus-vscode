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

/** How long the header's Your assistant button pulses (activity.tsx plays 2.4 s). */
export const PULSE_HOLD_MS = 2400;
let pulseUntil = 0;

/** The pulse is about to play. Whatever opens over the Studio next (the Pro permission card) waits for it, so the one thing that
 *  points at Your assistant is not hidden under a scrim (verifier of fix-firstrun-nofolder, item 4). */
export function holdForPulse(ms: number = PULSE_HOLD_MS): void {
  pulseUntil = Date.now() + ms;
}

/** Resolves at once when the choice will not show, or when it has been closed. It also waits out a pulse that is playing. */
export async function whenFirstRunClosed(): Promise<void> {
  const state = await firstRunOnce();
  // A say card that is waiting (a connect finished in a window that reloaded) counts as the card: nothing else opens over it.
  if (state?.show || state?.say) { if (!closed) await new Promise<void>((resolve) => { waiting.push(resolve); }); }
  // Effects of the same commit (the pulse starts in one, the ask in another) have run by the next tick; then the hold is known.
  await new Promise<void>((resolve) => setTimeout(resolve, 0));
  const left = pulseUntil - Date.now();
  // Every caller waits for the same instant (the first used to clear it and let a second ask through while the pulse still played).
  if (left > 0) await new Promise<void>((resolve) => setTimeout(resolve, left));
}

type TourExit = { fromCard: boolean; phase: 'idle' | 'starting' | 'running' | 'failed'; connected: boolean; open: boolean; seen?: boolean };

/** Leave from the tour returns to the first-run card, but only for a person who came to the tour from the card and has
 *  not connected yet. A tour started from Help, a card that is already open, and a connected person get nothing. A card
 *  the person has already seen this session does not come back: it trapped Kane (17 Oct). The chip pulses instead. */
export function returnsToCard(s: TourExit): boolean {
  return s.fromCard && s.phase === 'idle' && !s.connected && !s.open && !s.seen;
}

/** The same exit, for a card that was already seen: the header's Your assistant button pulses once so connecting later is easy. */
export function pulsesAssistantChip(s: TourExit): boolean {
  return s.fromCard && s.phase === 'idle' && !s.connected && !s.open && !!s.seen;
}

export const ASSISTANT_PULSE_EVENT = 'semanticus:pulse-assistant-chip';
