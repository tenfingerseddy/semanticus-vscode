// What the calm Plan page hands to the Studio's own chrome (1.5, pick C). While the Plan's header is on screen it carries the driver
// switch and the live slot, so the driver bar stands aside on that page; and the page's two notes live in the bottom
// bar as "2 notes". One tiny store, so the page and the shell never import each other.
import { useSyncExternalStore } from 'react';

/** One note: a sentence, and sometimes one link (Billing). */
export interface PlanNote { text: string; link?: { label: string; run: () => void } }
export interface PlanChrome { calm: boolean; notes: readonly PlanNote[] }
let snap: PlanChrome = { calm: false, notes: [] };
const subs = new Set<() => void>();
const emit = () => subs.forEach((fn) => fn());
const sameNotes = (a: readonly PlanNote[], b: readonly PlanNote[]) => a.length === b.length && a.every((x, i) => x.text === b[i].text && x.link?.label === b[i].link?.label);

export const planChrome = {
  getSnapshot: (): PlanChrome => snap,
  subscribe(fn: () => void) { subs.add(fn); return () => { subs.delete(fn); }; },
  /** The Plan's calm header is up (true), or the page left or shows a panel (false). Leaving also takes the notes away. */
  setCalm(calm: boolean) { if (snap.calm === calm && (calm || snap.notes.length === 0)) return; snap = { calm, notes: calm ? snap.notes : [] }; emit(); },
  setNotes(notes: readonly PlanNote[]) { if (sameNotes(snap.notes, notes)) return; snap = { ...snap, notes }; emit(); },
};

export function usePlanChrome(): PlanChrome {
  return useSyncExternalStore(planChrome.subscribe, planChrome.getSnapshot, planChrome.getSnapshot);
}
