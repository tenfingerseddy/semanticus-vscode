// The suggested next steps (1.4, contract section 6): up to three buttons under the last note. Pressing one adds its words
// to the Plan as a task for the assistant (addPlanTask, source page "suggest"); the assistant gets it on its next call
// and the person tells it to go. Semanticus adds the Plan's own top row as a fourth, plain line when no step names it.
import { useEffect, useState } from 'react';
import { rpc } from '../bridge';
import { topThree } from '../plan-model';
import type { Plan } from '../plan-model';
import { notesStore } from './notes-runtime';
import type { SuggestionView } from './notes-store';
import { ADDED_TEXT, fromYourPlan, leadOf, stepsOf, taskRequest } from './suggest-model';
import './studio-notes.css';

/** The top open row of the Plan, read once per suggestion. Null while unknown, or when the engine cannot say. */
function useTopPlanRow(suggestionId: string): { id: string; title: string } | null {
  const [top, setTop] = useState<{ id: string; title: string } | null>(null);
  useEffect(() => {
    let live = true;
    setTop(null);
    rpc<Plan>('getModelPlan').then((plan) => {
      const row = plan?.items ? topThree(plan)[0] : undefined;
      if (live && row) setTop({ id: row.id, title: row.title });
    }, () => { /* no plan to name: the line is simply left out */ });
    return () => { live = false; };
  }, [suggestionId]);
  return top;
}

export function Suggest({ view }: { view: SuggestionView }) {
  const { suggestion, added, failed } = view;
  const [busy, setBusy] = useState<number | null>(null);
  const steps = stepsOf(suggestion);
  const top = useTopPlanRow(suggestion.id);
  if (steps.length === 0) return null;
  const planLine = fromYourPlan(steps, top);

  const press = async (index: number) => {
    if (busy !== null) return;
    setBusy(index);
    try {
      await rpc('addPlanTask', taskRequest(suggestion, index, steps[index]));
      notesStore.markStepAdded(index);
    } catch (e) {
      notesStore.markStepFailed(index, e instanceof Error ? e.message : 'That did not reach your plan.');
    } finally {
      setBusy(null);
    }
  };

  return (
    <section className="sn-suggest" data-testid="suggest" aria-label="Next steps">
      <p className="sn-say" data-testid="suggest-lead">{leadOf(suggestion)}</p>
      <div className="sn-steps">
        {steps.map((step, index) => added.includes(index)
          ? <span key={index} className="sn-added" role="status" data-testid="suggest-added">{ADDED_TEXT}</span>
          : <button key={index} type="button" className="sem-btn sem-btn-sm" data-testid="suggest-step" disabled={busy !== null}
              title={step.prompt} onClick={() => void press(index)}>{step.label}</button>)}
      </div>
      {failed && <p className="sn-failed" role="alert" data-testid="suggest-failed">{failed.message}</p>}
      {planLine && <p className="sn-how" data-testid="suggest-plan">{planLine}</p>}
    </section>
  );
}
