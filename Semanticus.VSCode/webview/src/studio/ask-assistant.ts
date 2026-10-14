// "Ask your assistant", pressed (1.5, calm Plan page). ONE function handles every press of it: the Plan header's live slot, a Plan row,
// and the Next line on the other pages. A parallel spike (m15/wake-spike) is finding a way for this press, and for Run the plan, to
// wake the person's Claude Code; when it lands it changes this body and nothing else.
//
// Today nothing wakes the assistant. A step the engine suggested becomes one Plan task for the assistant (addPlanTask, keyed so a
// second press is the same task); a row that is already the assistant's is on the Plan, so the press copies a request the person
// pastes into their assistant.
import { copyText, rpc } from '../bridge';
import { ADDED_TEXT, FAILED_TEXT } from './next-model';

export const COPIED_TEXT = 'Copied. Paste it into your assistant.';

export interface AskRequest {
  /** What the assistant is asked to do, in the person's words. */
  words: string;
  doer?: 'assistant';
  sourcePage?: string;
  /** The control's key: while a task with this key is open, the engine answers a second press with that task. */
  sendKey?: string;
  /** Set when the press is about a row that is already on the Plan: nothing is added, the request is copied. */
  rowId?: string;
}
export interface AskOutcome { ok: boolean; text: string }

function errorText(e: unknown): string {
  const m = e instanceof Error ? e.message : typeof e === 'string' ? e : '';
  return m.trim() || FAILED_TEXT;
}

export async function askAssistant(req: AskRequest): Promise<AskOutcome> {
  try {
    if (req.rowId) {
      const ok = await copyText(`Work on this task in my Semanticus plan: ${req.words}`);
      return ok ? { ok: true, text: COPIED_TEXT } : { ok: false, text: FAILED_TEXT };
    }
    const { rowId: _row, ...task } = req;
    await rpc('addPlanTask', task);
    return { ok: true, text: ADDED_TEXT };
  } catch (e) { return { ok: false, text: errorText(e) }; }
}
