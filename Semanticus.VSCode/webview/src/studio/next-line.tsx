// The assistant's next step on the Studio side (1.5). The bar under the header on every page is gone (Kane, 13 Oct): the one place
// a next step shows is the Plan header's live slot (plan-head.tsx). This file keeps what the slot reads: the one store (steady, see
// next-store.ts), its load and push wiring, and the two presses of a step.
import { useEffect, useState, useSyncExternalStore } from 'react';
import { onReconnect, onStudioNextStep, rpc, saveModelFromPage } from '../bridge';
import { askAssistant } from './ask-assistant';
import { createNextStore } from './next-store';
import { askRequest, buttonsOf, doItCall, FAILED_TEXT } from './next-model';
import type { NextStepView } from './types';

export const nextStore = createNextStore();

export function useNextStep() {
  return useSyncExternalStore(nextStore.subscribe, nextStore.getSnapshot, nextStore.getSnapshot);
}

/** getNextStep once, then the pushed message only. An older engine has no getNextStep: then the slot says there is nothing waiting. */
export async function loadNextStep(): Promise<void> {
  try { nextStore.set(await rpc<NextStepView>('getNextStep')); } catch { nextStore.set(null); }
}

/** Keeps the one NextStepView fresh: read once, then the pushed message. The Plan header's live slot draws it. */
export function useNextStepLoad(): void {
  useEffect(() => {
    const off = [onStudioNextStep((v) => nextStore.set(v as NextStepView)), onReconnect(() => { void loadNextStep(); })];
    void loadNextStep();
    return () => off.forEach((fn) => fn());
  }, []);
}

function errorText(e: unknown): string {
  const m = e instanceof Error ? e.message : typeof e === 'string' ? e : '';
  return m.trim() || FAILED_TEXT;
}

/** The two presses of a step, "Ask your assistant" and "Do it", and the one text a press leaves in place. */
export function useNextActions(view: NextStepView | null) {
  const { pressed } = useNextStep();
  const [busy, setBusy] = useState(false);
  const step = view?.step ?? null;
  const call = step?.doIt ? doItCall(step.doIt.op, step) : null;
  const mine = view && pressed && pressed.id === view.id ? pressed : null;
  const ask = async () => {
    if (busy || !view || !step) return; setBusy(true);
    try { const r = await askAssistant(askRequest(view, step)); nextStore.press(view.id, r.text, !r.ok); }
    finally { setBusy(false); }
  };
  const doIt = async () => {
    if (busy || !view || !step || !call || !step.doIt) return; setBusy(true);
    try {
      if (call.kind === 'save') {
        if (!(await saveModelFromPage())) { setBusy(false); return; }   // the person closed the save dialog: nothing changed
      } else await rpc(call.method, ...call.params);
      nextStore.press(view.id, step.doIt.done);
    } catch (e) { nextStore.press(view.id, errorText(e), true); }
    finally { setBusy(false); }
  };
  return { busy, mine, buttons: buttonsOf(call && step ? step : { doIt: null }), press: (act: 'ask' | 'do') => void (act === 'ask' ? ask() : doIt()) };
}
