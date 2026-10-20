// The one NextStepView of the running Studio and what a press left in place. Read once (getNextStep), then only the
// pushed studioNextStep message changes it. No polling.
//
// STEADY (slot-calm, Kane 13 Oct: "the suggestion flicked in and out"). The engine works the step out again after every
// change (Jev takes about 1.3 s on Pro), and in between it can answer with the rules' step, with no step, or the read can
// fail. None of that is a real change, so the step on show stays until a real one arrives:
//   - a failed read, or an answer with no step: the step stays for HOLD_EMPTY_MS, then there really is no step;
//   - the rules' answer where Jev's pick is on show: the pick stays for HOLD_RULES_MS, so Jev's pick lands as ONE change;
//   - "off" and "hidden" are the person's word and show at once; a new step from the same kind of chooser shows at once.
// A hold is bounded from its FIRST answer, so a stream of answers never keeps a stale step on show.
export type Pressed = { id: string; text: string; failed?: boolean } | null;
export type NextSnapshot = { view: import('./types').NextStepView | null; pressed: Pressed };
type View = import('./types').NextStepView;

export const HOLD_EMPTY_MS = 4000;
export const HOLD_RULES_MS = 3000;

const usable = (v: View | null | undefined): boolean => !!v && v.on && !v.hidden && !!v.step && (v.step.ask ?? '').trim() !== '';

export function createNextStore(
  now: () => number = () => Date.now(),
  schedule: (fn: () => void, ms: number) => () => void = (fn, ms) => { const t = setTimeout(fn, ms); return () => clearTimeout(t); },
) {
  let snap: NextSnapshot = { view: null, pressed: null };
  let pending: { view: View | null; since: number; limit: number } | null = null;
  let cancel: (() => void) | null = null;
  let current: string | null = null;
  const subs = new Set<() => void>();
  const emit = () => subs.forEach((fn) => fn());
  const clearHold = () => { pending = null; if (cancel) { cancel(); cancel = null; } };
  const adopt = (view: View | null) => {
    clearHold();
    // A press stays in place while the step it answered is the one shown; a new step clears it.
    const keep = snap.pressed && view && view.id === snap.pressed.id && !view.hidden ? snap.pressed : null;
    snap = { view, pressed: keep };
    emit();
  };
  /** How long the step on show stays before this answer replaces it; 0 means at once. */
  const holdFor = (incoming: View | null): number => {
    const cur = snap.view;
    if (!usable(cur)) return 0;
    if (incoming && (!incoming.on || incoming.hidden)) return 0;
    if (!usable(incoming)) return HOLD_EMPTY_MS;
    if (incoming!.id === cur!.id) return 0;
    return cur!.step!.chosenBy === 'jev' && incoming!.step!.chosenBy !== 'jev' ? HOLD_RULES_MS : 0;
  };
  return {
    getSnapshot: () => snap,
    subscribe(fn: () => void) { subs.add(fn); return () => { subs.delete(fn); }; },
    set(view: View | null) {
      const wait = holdFor(view);
      if (wait <= 0) { adopt(view); return; }
      const since = pending ? pending.since : now();
      const limit = pending ? pending.limit : wait;
      pending = { view, since, limit };
      if (cancel) cancel();
      cancel = schedule(() => { const p = pending; cancel = null; if (p) adopt(p.view); }, Math.max(0, since + limit - now()));
    },
    /** The model the step belongs to. Another model starts clean: "Added. Tell Claude go." was drawn on Contoso for a press made on another model (UAT F14). */
    setScope(scope: string) {
      if (scope === current) return;
      current = scope;
      if (snap.pressed) { snap = { ...snap, pressed: null }; emit(); }
    },
    press(id: string, text: string, failed = false) { snap = { ...snap, pressed: { id, text, failed } }; emit(); },
    clearPress() { snap = { ...snap, pressed: null }; emit(); },
  };
}
