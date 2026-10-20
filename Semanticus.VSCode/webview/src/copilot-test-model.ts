// The Copilot test page's pure half (Kane, 15 Oct 2026, option A). The engine's rounds, scores and misses, and the words around them. Nothing here touches the
// page or the engine, so the node tests read it directly. Every sentence is plain words: no em dash, no engine words.

export type QKind = 'totals' | 'filters' | 'time' | 'ranking' | 'words' | 'names' | 'cannot';
export type Miss = 'wrong_field' | 'wrong_filter' | 'wrong_number' | 'said_no' | 'should_say_no' | 'dax_failed';

export const KINDS: QKind[] = ['totals', 'filters', 'time', 'ranking', 'words', 'names', 'cannot'];
export const KIND_LABEL: Record<QKind, string> = {
  totals: 'Totals', filters: 'Filters', time: 'Time', ranking: 'Ranking', words: 'Unclear words', names: 'Other names', cannot: 'Cannot answer',
};
export const KIND_HINT: Record<QKind, string> = {
  totals: 'One number for the whole model or one table.',
  filters: 'A number for one product, store, segment or place.',
  time: 'This year, last year, a month, year to date, change on last year.',
  ranking: 'Top and bottom lists, best and worst.',
  words: 'Words that could mean two things, like "sales" or "best".',
  names: 'The words people use that are not the names in the model, like "revenue".',
  cannot: 'Questions this model has no data for. The right answer is to say so.',
};
export const MISSES: Miss[] = ['wrong_field', 'wrong_filter', 'wrong_number', 'said_no', 'should_say_no', 'dax_failed'];
export const MISS_LABEL: Record<Miss, string> = {
  wrong_field: 'Wrong field', wrong_filter: 'Wrong filter', wrong_number: 'Wrong number', said_no: 'Said no, could answer', should_say_no: 'Answered, should say no', dax_failed: 'DAX failed',
};
export const MISS_SHORT: Record<Miss, string> = {
  wrong_field: 'Field', wrong_filter: 'Filter', wrong_number: 'Number', said_no: 'Said no', should_say_no: 'Answered', dax_failed: 'Failed',
};
export const MISS_HINT: Record<Miss, string> = {
  wrong_field: 'Used a different measure or column from the right answer.',
  wrong_filter: 'Right measure, but filtered by the wrong thing or the wrong period.',
  wrong_number: 'Right fields and filter, different number.',
  said_no: 'Said it could not answer, but the model has the data.',
  should_say_no: 'Gave a number for something the model does not hold.',
  dax_failed: 'The DAX did not run.',
};

// ---- the engine's shapes (Semanticus.Engine/CopilotTestTypes.cs) -------------------------------------------------------------------------

export interface CopilotScore { right: number; total: number; couldNotCheck: number; share: number | null }
export interface CopilotMissOut {
  n: number; question: string; kind: string; miss: string; missWords: string; studentDax: string | null; studentAnswer: string | null;
  referenceDax: string | null; referenceAnswer: string | null; detail: string | null; cause: string | null;
}
export interface CopilotGroup { cause: string; fix: string; label: string; fixWords: string; count: number; numbers: number[]; objects: string[]; by: string }
export interface CopilotChangeOut { title: string; where: string; on: string; before: string; after: string; forMisses: number[]; fixed: number }
export interface CopilotKindScore { kind: string; right: number; total: number }
export interface CopilotRound {
  id: string; number: number; when: string; student: string; total: number; answered: number; complete: boolean;
  practice: CopilotScore; heldBack: CopilotScore; all: CopilotScore; byKind: CopilotKindScore[];
  missCounts: Record<string, number>; newMisses: number; changes: CopilotChangeOut[]; groups: CopilotGroup[]; misses: CopilotMissOut[];
}
export interface CopilotRoundsView {
  rounds: CopilotRound[]; bankSize: number; bankByKind: { kind: string; count: number }[]; bankKey: string; heldBack: number; note?: string;
}
export interface CopilotSee { what: string; count: string; on: boolean }
export interface CopilotView { text: string; sees: CopilotSee[]; tables: number; columns: number; measures: number; measuresDescribed: number; otherNames: number; instructionWords: number; leftOut: number }
export interface BankQuestion {
  id: string; question: string; kind?: string | null; tier: string; query?: string | null; expectedValue?: string | null; expectedMatrix?: string[][] | null;
  heldBack?: boolean; expectRefusal?: boolean;
}

// ---- words ------------------------------------------------------------------------------------------------------------------------------

export const pct = (v: number | null | undefined): string => (v == null ? 'n/a' : `${Math.round(v * 100)}%`);
export const share = (s: CopilotScore | null | undefined): number | null => (s ? s.share : null);

/** The student the test names, in the words the page shows. */
export function studentLabel(round: Pick<CopilotRound, 'student'> | null | undefined): string {
  const s = (round?.student ?? '').trim();
  return s.length > 0 ? s : 'Haiku 5.5';
}

/** The latest round, whether it is finished or not. */
export const latestOf = (rounds: CopilotRound[]): CopilotRound | null => (rounds.length ? rounds[rounds.length - 1] : null);

/** Which state the round card shows. A round the assistant has begun and not finished is running; a finished one is done. */
export type RoundState = 'none' | 'running' | 'done';
export function roundState(rounds: CopilotRound[], progress: { done: number; total: number } | null): RoundState {
  const last = latestOf(rounds);
  if (!last) return progress ? 'running' : 'none';
  return last.complete && !progress ? 'done' : 'running';
}

/** How far a running round has got, as a share of the bank. `graded` is every answer the engine has graded so far. */
export function progressShare(round: Pick<CopilotRound, 'answered' | 'total'> | null, progress: { done: number; total: number } | null): number {
  if (!round || round.total <= 0) return 0;
  return Math.min(1, round.answered / round.total);
}

/** The score line over the rounds: the practice score and the held-back score of each round, in order. Null where nothing could be checked. */
export function scoreSeries(rounds: CopilotRound[]): { number: number; practice: number | null; held: number | null; all: number | null }[] {
  return rounds.map((r) => ({ number: r.number, practice: share(r.practice), held: share(r.heldBack), all: share(r.all) }));
}

/** The big number: the score on every checked question of the latest round. */
export function headline(rounds: CopilotRound[]): { value: number | null; round: number; running: boolean } {
  const last = latestOf(rounds);
  if (!last) return { value: null, round: 0, running: false };
  return { value: share(last.all), round: last.number, running: !last.complete };
}

/** The line under the big number. */
export function headlineNote(rounds: CopilotRound[], aim: number): string {
  const last = latestOf(rounds);
  if (!last) return 'No round has run yet.';
  const now = share(last.all);
  const before = rounds.length > 1 ? share(rounds[rounds.length - 2].all) : null;
  if (!last.complete) return `Round ${last.number} so far, ${last.answered} of ${last.total} graded.` + (before != null ? ` Round ${last.number - 1} ended at ${pct(before)}.` : '');
  const parts: string[] = [];
  if (before != null && now != null) {
    const d = Math.round((now - before) * 100);
    parts.push(d === 0 ? `Round ${last.number}, the same as round ${last.number - 1}.` : `Round ${last.number}, ${d > 0 ? 'up' : 'down'} ${Math.abs(d)} points on round ${last.number - 1}.`);
  } else parts.push(`Round ${last.number}.`);
  if (now != null) parts.push(now >= aim ? `Above your aim of ${pct(aim)}.` : `Your aim is ${pct(aim)}.`);
  return parts.join(' ');
}

/** What a change says it did, in the round that tried it. */
export function changeOutcome(c: Pick<CopilotChangeOut, 'fixed' | 'forMisses'>, roundDone: boolean): string {
  const of = c.forMisses.length;
  if (of === 0) return roundDone ? 'No miss named' : 'Being tried';
  if (!roundDone) return `For ${of} ${of === 1 ? 'miss' : 'misses'}`;
  return `Fixed ${c.fixed} of ${of}`;
}

/** Group the practice misses by their kind of miss, in the order the page lists them. */
export function missesByKind(misses: CopilotMissOut[]): { miss: Miss; items: CopilotMissOut[] }[] {
  return MISSES.map((m) => ({ miss: m, items: misses.filter((x) => x.miss === m) })).filter((g) => g.items.length > 0);
}

/** The heat table: question kind by kind of miss, from the practice misses of the round. */
export function heatCells(misses: CopilotMissOut[]): { rows: QKind[]; cells: number[][] } {
  const rows = KINDS.filter((k) => k !== 'cannot' || misses.some((m) => m.kind === k));
  return { rows, cells: rows.map((k) => MISSES.map((m) => misses.filter((x) => x.kind === k && x.miss === m).length)) };
}

/** Each kind's share right over the rounds. A kind with no checked question in a round has no point there. */
export function kindSeries(rounds: CopilotRound[]): { kind: QKind; series: number[]; now: number | null }[] {
  return KINDS.map((k) => {
    const series = rounds.map((r) => { const s = r.byKind.find((x) => x.kind === k); return s && s.total > 0 ? s.right / s.total : null; });
    const filled = series.filter((v): v is number => v != null);
    return { kind: k, series: filled, now: filled.length ? filled[filled.length - 1] : null };
  }).filter((k) => k.series.length > 0);
}

/** The bank by kind: how many questions, and how many were right in the latest round (when it has been graded). */
export function bankRows(view: { bankByKind: { kind: string; count: number }[] } | null, latest: CopilotRound | null): { kind: QKind; n: number; right: number | null; missed: number | null }[] {
  return KINDS.map((k) => {
    const n = view?.bankByKind.find((x) => x.kind === k)?.count ?? 0;
    const s = latest?.byKind.find((x) => x.kind === k);
    // Red is a graded miss. A question not graded yet, or a bank no round has run, is neither right nor missed (UAT 17 Oct: red bars before any round).
    return { kind: k, n, right: s ? s.right : null, missed: s ? Math.max(0, s.total - s.right) : null };
  }).filter((r) => r.n > 0);
}

/** The words of the page head's one line. */
export function headSummary(view: CopilotRoundsView | null, round: CopilotRound | null): string[] {
  const out: string[] = [];
  if (round) { const v = share(round.all); out.push(`${pct(v)} right${round.complete ? '' : ' so far'}`); }
  if (view) out.push(`${view.bankSize} questions, ${view.bankByKind.filter((k) => k.count > 0).length} kinds`);
  out.push(`Student: ${studentLabel(round)}`);
  return out;
}

/** The task the page's two buttons give the person's assistant. They name the skill, so an assistant that has not read it still knows the loop. */
export function bankTask(): { words: string; brief: string } {
  return {
    words: 'Write the Copilot test questions',
    brief: 'Use the interview-model skill. Write a bank of 100 or more questions for the open model in the words a person would use, balanced across totals, filters, time, ranking, words, names and cannot. Give each a reference DAX, and add them all with add_interview_questions. Then run the first round.',
  };
}
export function roundTask(next: number): { words: string; brief: string } {
  return {
    words: `Run Copilot test round ${next}`,
    brief: `Use the interview-model skill. Run round ${next} of the Copilot test: get_copilot_view and get_student_questions, run the student on groups of 25, send every answer to run_interview_batch in one call, then read the groups, change the AI instructions, descriptions and other names as one undoable step, and report both scores.`,
  };
}

export const WHO_SEES = 'It sees only what Copilot sees. It runs inside your assistant, on your plan. Semanticus runs no AI of its own.';
export const HELD_NOTE = 'Your assistant never sees the misses of the held back questions, so their score cannot be tuned to the bank.';
/** The line under the page head: what a press of New question bank or Run round does. Nothing can wake an assistant, so it says when the assistant learns of the task. */
export function assistantNote(connected: boolean): string {
  return connected
    ? 'Your assistant starts each task on its next call.'
    : 'No assistant is connected yet, so each task waits on your Plan until one is.';
}

/** The one line when the model has changes the published model does not. The test grades the published model, so the changes would count as misses. */
export function unpublishedLine(changes: number, removals: number): string {
  const n = `${changes} ${changes === 1 ? 'change is' : 'changes are'} not published yet`;
  const rm = removals > 0 ? ` ${removals === 1 ? 'One removal stays' : `${removals} removals stay`} for you to choose on Publish.` : '';
  return `${n}. The Copilot test grades the published model, so it would count them as misses.${rm}`;
}
/** What is still unpublished once a publish has landed: the box must not keep the count it had before (UAT 18 Oct N24: "Published 68 changes" under "71 changes are not published yet").
 *  Only what the publish reported as not gone stays. A publish that wrote nothing leaves the box as it was. Pure, so the page clears the box the moment the publish ends. */
export function gapAfterPublish<G extends { refs: string[] }>(gap: G, result: { applied?: boolean; count?: number; failedRefs?: string[] | null }): G | null {
  if (!result.applied) return gap;
  const left = new Set(result.failedRefs ?? []);
  const refs = gap.refs.filter((r) => left.has(r));
  return refs.length > 0 ? { ...gap, refs } : null;
}
export const PUBLISH_THEN_TEST = 'Publish, then test';

export const CHANGES_NOTE = 'They are in your model, not saved. Undo takes any of them back.';
