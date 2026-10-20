// The Studio follows the assistant to the Copilot test page while a round runs (UAT 17 Oct: it stayed on Publish for minutes).
//
// Two things kept it away, and both are about what the Studio assumes, not about the round:
//   1. The engine adds a note for a call only when the call goes to another place than its last note. After one round the last note was the Copilot
//      test, so the next round made no note at all, and the person had left that page since. The Studio never learned the assistant was back there.
//   2. A click made just before the person told the assistant (they pressed Publish, then asked for the test) held the wheel, because only a pause of
//      eight seconds in the assistant's calls gives it back, and the student's answers are that pause, minutes later.
// So the Studio reads the CALL itself. A call that starts, ends or feeds a round is the person's own request: it gives the wheel back unless the person
// clicked AFTER the call, and the screen goes to the Copilot test. Reads such as get_copilot_view do not move it: they happen on every look at the page.
export const COPILOT_PLACE_ID = 'review/copilot-test';
/** The calls of a round: the questions go in, the student's questions come out, the graded answers go in. */
export const COPILOT_FOLLOW_TOOLS: readonly string[] = ['add_interview_questions', 'get_student_questions', 'run_interview_batch'];

/** `roundStart` is the time of the round's first call (roundTracker below). Left out, the call's own time stands in for it. */
export type FollowState = { mode: 'claudeDrives' | 'iDrive'; state: 'idle' | 'claudeDriving' | 'personHasWheel'; lastPersonInput: number | null; roundStart?: number | null };

/** What a call does to the screen: nothing, move, or give the wheel back first and then move. */
export type FollowStep = 'none' | 'move' | 'give-wheel-and-move';

export function copilotFollowStep(call: { tool?: string; at?: string } | null | undefined, s: FollowState): FollowStep {
  if (!call?.tool || !COPILOT_FOLLOW_TOOLS.includes(call.tool)) return 'none';
  if (s.mode !== 'claudeDrives') return 'none';                       // I drive: the screen stays where the person put it
  if (s.state !== 'personHasWheel') return 'move';
  const at = s.roundStart ?? Date.parse(call.at ?? '');
  // The click came before the round started: it was before the assistant started on this, so it does not hold the wheel. A click after the start of
  // the round does, for every later call of that round (the student works for minutes between calls, and the person may look elsewhere meanwhile).
  return s.lastPersonInput !== null && Number.isFinite(at) && at > s.lastPersonInput ? 'give-wheel-and-move' : 'none';
}

/** The start of the round a call belongs to. A round is the add, get and run calls, and every other call made while it is open (a read, a query).
    A pause of 5 minutes between calls closes it, except the pause after get_student_questions: that is the student working, and it can be long.
    A run does not close it: the graded answers may go in as two runs, even when the second comes after a long pause. The next add or get after a run opens a new round. A call outside a round has none. */
export function createRoundTracker(maxGapMs = 5 * 60 * 1000) {
  let start: number | null = null;
  let last = 0;
  let key = '';
  let answer: number | null = null;
  let lastTool = '';
  return {
    /** Returns the round's start for this call, or null when the call is not part of a round. The same call asked twice gets the same answer. */
    see(call: { tool?: string; at?: string } | null | undefined): number | null {
      if (!call?.tool) return null;
      const id = `${call.tool}@${call.at ?? ''}`;
      if (id === key) return answer;
      key = id;
      const at = Date.parse(call.at ?? '');
      const now = Number.isFinite(at) ? at : Date.now();
      if (start !== null && now - last > maxGapMs && lastTool !== 'get_student_questions' && !(lastTool === 'run_interview_batch' && call.tool === 'run_interview_batch')) start = null;
      // After a run, the next add or get is the start of the next round; a second run or a read is still the same one.
      if (start !== null && lastTool === 'run_interview_batch' && (call.tool === 'add_interview_questions' || call.tool === 'get_student_questions')) start = null;
      const inRound = COPILOT_FOLLOW_TOOLS.includes(call.tool);
      if (start === null && inRound) start = now;
      if (start !== null) last = now;
      answer = start;
      lastTool = call.tool;
      return answer;
    },
  };
}

/** The one tracker of the running Studio: the driver and the Copilot follow both read it, so a call is placed in its round once. */
export const copilotRounds = createRoundTracker();

/** The place a note for the Copilot test names, so the one navigation function every note uses can take it. */
export const copilotPlaceNote = (sessionId?: string) => ({ id: 'follow-copilot-test', say: '', how: '', place: COPILOT_PLACE_ID, at: new Date().toISOString(), by: 'assistant', sessionId });
