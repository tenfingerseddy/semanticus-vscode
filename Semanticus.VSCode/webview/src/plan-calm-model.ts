// The calm Plan page's pure half (1.5, Kane's pick C "Header does it all", 12 Oct 2026). Nothing here touches the page or the engine, so
// the node tests read it directly: the list grouped by who, the words of the live slot while a run works and when it ends, which quiet
// press a row offers, and the one-row run. The engine's own words (state words, titles, sentences) are never rewritten here.
import * as P from './plan-model';
import { groupForTool, verbFor, type VerbGroup } from './personality/verbs';

/** The page's top-level order: your assistant first (its tasks are the ones the person can hand over), then Semanticus, then you. */
export const GROUP_ORDER: readonly P.PlanDoer[] = ['assistant', 'semanticus', 'you'];

export interface PlanGroup { who: P.PlanDoer; label: string; rows: P.PlanItem[] }

/** The open rows, grouped by who does them, in the engine's order inside each group. A group with no rows is not drawn. */
export function groupsOf(rows: readonly P.PlanItem[]): PlanGroup[] {
  return GROUP_ORDER.map((who) => ({ who, label: P.DOER_LABEL[who], rows: rows.filter((r) => P.doerOf(r) === who) })).filter((g) => g.rows.length > 0);
}

/** The rows the grouped list draws: every open row, the rows that turned done while the page was open, and the open row even when it moved. */
export function calmRows(items: readonly P.PlanItem[], justDone: ReadonlySet<string>, openId: string | null): P.PlanItem[] {
  return items.filter((i) => P.isOpenRow(i) || (P.isDraftItem(i) && P.stateWordOf(i) === 'checking')
    || (justDone.has(i.id) && (P.stateWordOf(i) === 'done' || P.stateWordOf(i) === 'checking')) || i.id === openId);
}

/** A group shows this many rows, then "Show all N" (Kane, 12 Oct: a plan can have a hundred rows). */
export const GROUP_CAP = 10;

// ---- the filter (Kane, 12 Oct): one quiet control, only on a long plan ---------------------------------------------------------

/** The Filter control appears only when the Plan has more rows than this. */
export const FILTER_MIN_ROWS = 15;
export const FILTER_PLACEHOLDER = 'Filter tasks';

/** The words a row can be found by: its title, its sub line, the person's full text and the names of the objects it touches. */
export function rowMatches(item: P.PlanItem, text: string): boolean {
  const q = text.trim().toLowerCase();
  if (!q) return true;
  const hay = [item.title, item.sub, item.fullText ?? '', ...(item.objects ?? [])].join(' ').toLowerCase();
  return q.split(/\s+/u).every((word) => hay.includes(word));
}

/** The rows under the typed words and the goal choice (All, AI ready, Quality). The row the person has open stays, whatever the filter says:
 *  it holds words they are typing, and a filter must not take it from under their hands (R-B). */
export function filterRows(rows: readonly P.PlanItem[], text: string, goal: P.PlanGoal, openId: string | null = null): P.PlanItem[] {
  return rows.filter((r) => r.id === openId || ((goal === 'all' || P.goalsOf(r).includes(goal)) && rowMatches(r, text)));
}

/** The rows of one group the page draws: all of them, or the first GROUP_CAP and the open row even when it sits past the cap. */
export function capRows(rows: readonly P.PlanItem[], all: boolean, openId: string | null): P.PlanItem[] {
  return all || rows.length <= GROUP_CAP ? [...rows] : rows.filter((r, i) => i < GROUP_CAP || r.id === openId);
}

/** The one small chip an active filter shows: "AI ready, 34 of 120". Null when no filter is on. */
export function filterChip(text: string, goal: P.PlanGoal, shown: number, total: number): string | null {
  const parts = [goal === 'all' ? null : P.GOAL_LABEL[goal], text.trim() || null].filter(Boolean);
  return parts.length === 0 ? null : `${parts.join(', ')}, ${shown} of ${total}`;
}

/** The engine's sub line on a row whose evidence could not be read this time (LocalEngine.Plan UncheckableItem). */
export const NOT_CHECKED_SUB = 'could not be checked this time';
export const NOT_CHECKED_WORD = 'Not checked';
export const isNotChecked = (item: P.PlanItem): boolean => (item.sub ?? '').trim() === NOT_CHECKED_SUB;

// ---- the verb of the work in hand ---------------------------------------------------------------------------------------------

/** The verb group a row's work belongs to: the words of its title first (what the row does), then the Plan's own section for it, then
 *  the page it points at. The page comes last because many rows point at a general page (AI readiness) whatever they do, so a
 *  "formats and folders" row must not be called "writing descriptions" (12 Oct, the calm-build verifier). */
export function verbGroupOf(item: Pick<P.PlanItem, 'title' | 'jump' | 'section'>): VerbGroup {
  const t = item.title.toLowerCase();
  if (/\b(description|descriptions|describe|synonym|synonyms|copilot|name|names|instructions?)\b/u.test(t)) return 'Readiness';
  if (/\b(hide|hidden|unhide|format|formats|folder|folders|sort|summari[sz]e|display|unused|date table|compatibility)\b/u.test(t)) return 'Modelling';
  if (/\b(relationship|relationships|join|joins|key|keys)\b/u.test(t)) return 'Relationships';
  if (/\b(measure|measures|dax|formula|formulas|calculat\w*)/u.test(t)) return 'DAX';
  switch (item.section) {
    case 'Numbers': return 'DAX';
    case 'Structure': return 'Modelling';
    case 'Meaning': case 'Trust': return 'Readiness';
    case 'Ship': return 'Publishing';
  }
  const tool = item.jump?.tool ?? '';
  if (tool === 'readiness' || tool === 'instructions' || tool === 'knowledge' || tool === 'dataagent') return 'Readiness';
  if (tool === 'relationships' || tool === 'lineage') return 'Relationships';
  if (tool === 'daxlab' || tool === 'meditor') return 'DAX';
  return groupForTool(tool) ?? 'Modelling';
}

/** What Run the plan says while it works: the verb of the row being worked on, and "3 of 10". `step` is the position in the press. */
export function pressWords(held: readonly string[], steps: Readonly<Record<string, P.RunPhase>>, items: readonly P.PlanItem[], quiet: boolean, seed = 0):
  { verb: string; count: string; fraction: number; n: number; total: number } {
  const total = held.length;
  const finished = held.filter((id) => steps[id]?.step === 'done' || steps[id]?.step === 'skipped').length;
  const n = Math.min(finished + 1, Math.max(total, 1));
  const live = held.find((id) => ['work', 'waitjev', 'apply', 'check'].includes(steps[id]?.step ?? ''));
  const current = items.find((i) => i.id === (live ?? held.find((id) => steps[id]?.step !== 'done' && steps[id]?.step !== 'skipped')));
  const verb = verbFor(current ? verbGroupOf(current) : 'Readiness', n - 1, quiet, seed);
  return { verb, count: `${n} of ${total}`, fraction: total === 0 ? 0 : (finished + 0.5) / total, n, total };
}

// ---- the end of a run ---------------------------------------------------------------------------------------------------------

export interface Grade { letter: string; score: number | null }

/** "3 s", "42 s", "2 min". */
export function durationWords(ms: number): string {
  const s = Math.max(1, Math.round(ms / 1000));
  return s < 60 ? `${s} s` : `${Math.round(s / 60)} min`;
}

/** The one line when a run ends: "Done. 10 tasks in 2 min." and, when the run applied lines Jev chose, "Jev chose 37 settings." (the lines the person sees on the rows, said as what they are). The grades are not in it: the three figures beside Plan show them (1.5, Kane's pick A). */
export function doneWords(rows: number, ms: number, jevChose = 0): string {
  return `Done. ${rows} ${rows === 1 ? 'task' : 'tasks'} in ${durationWords(ms)}.${jevChose > 0 ? ` Jev chose ${jevChose} ${jevChose === 1 ? 'setting' : 'settings'}.` : ''}`;
}

// ---- one row, on its own ------------------------------------------------------------------------------------------------------

/** The quiet press a row offers, shown on hover and focus (always, at a narrow width). Null when the row has nothing to press:
 *  its main action (Approve, Mark done, Put back) is already on the row, or it waits, or it is on its way. */
export type RowPressKind = 'run' | 'ask' | 'open';
export const ROW_PRESS_LABEL: Record<RowPressKind, string> = { run: 'Run', ask: 'Ask your assistant', open: 'Open' };

export function rowPressOf(item: P.PlanItem): RowPressKind | null {
  if (!P.isOpenRow(item) || item.id.startsWith('run/') || item.held) return null;
  const word = P.stateWordOf(item);
  const doer = P.doerOf(item);
  if (doer === 'semanticus') return word === 'ready' && !P.isDraftRow(item) ? 'run' : null;
  if (doer === 'assistant') return word === 'ready' ? 'ask' : null;
  return item.jump?.tool && (word === 'needs_you' || word === 'ready') ? 'open' : null;
}

/** What asking the assistant about one row says: the row's own words. The row is already on the Plan, so nothing new is added. */
export const askRowRequest = (item: Pick<P.PlanItem, 'id' | 'title'>) => ({ words: item.title, rowId: item.id, sendKey: `row:${item.id}` });

/** The digest of a row's worked-out changes, when they are there to apply: a proposed set with a digest and at least one kept line. */
export function proposedDigest(item: P.PlanItem | undefined | null): string | null {
  const set = item?.changeSet;
  return set && set.state === 'proposed' && set.digest ? set.digest : null;
}

/** The engine call that applies ONE row, with the digest the plan showed for it (applyPlanItems, items, origin). */
export const applyOneCall = (id: string, digest: string): [string, unknown[], string] => ['applyPlanItems', [{ id, digest }], 'human'];

// ---- the one live slot in the header ------------------------------------------------------------------------------------------

/** What the single slot between the grade and the switch says. One thing at a time, never two. */
export type Slot =
  | { kind: 'press'; who: P.PlanDoer; verb: string; count: string }
  | { kind: 'work'; who: P.PlanDoer; verb: string; count: string | null }
  | { kind: 'done'; text: string }
  | { kind: 'said'; text: string }
  | { kind: 'wheel'; text: string }
  | { kind: 'next' }
  | { kind: 'quiet'; text: string };

export interface SlotInput {
  /** Run the plan, or one row, is running now. */
  press: { verb: string; count: string } | null;
  /** What the last press left: "Done. 10 tasks in 2 min." or a plain sentence. */
  receipt: { kind: 'ran' | 'said'; text: string } | null;
  /** The Link's job, when it is running and is not this page's own press. */
  work: { who: 'assistant' | 'person' | 'semanticus'; verb: string; progress: string | null } | null;
  driver: { connected: boolean; mode: 'claudeDrives' | 'iDrive'; wheel: boolean; assistantWorking: boolean; name: string };
  /** The engine suggested a next step the person can press. */
  hasNext: boolean;
}

/** The slot is never empty (Kane, 13 Oct): with no step to suggest, this quiet line holds the place. */
export const NOTHING_WAITING = 'Nothing waiting for your assistant';
/** What the slot says while the assistant drives and works: its work, never a step for the person to press (hero19 D3). */
export const ASSISTANT_WORKING = 'Working on your model';

/** The end-of-run line ("Done. 10 tasks in 2 min. Undo") stays until the person's next action (hero19 D8). It goes only when
 *  another writer moved the model on and its Undo went with it: a line with nothing left to press has nothing to keep. */
export function receiptStays(r: { kind: 'ran' | 'said'; entryId?: string | null; revision?: number | null }, planRevision: number | null | undefined): boolean {
  if (r.kind !== 'ran' || !r.entryId) return true;
  return !(typeof planRevision === 'number' && typeof r.revision === 'number' && planRevision > r.revision);
}

/** Reads of the plan, in order. A read lands when it is newer than the one on screen, even if a later read was started
 *  meanwhile, so a long run of edits (each one starts a read) cannot keep every read from landing (hero19 D3). A reset (another model)
 *  drops every read that is still on its way. */
export function createRereadGate() {
  let started = 0;
  let shown = 0;
  return {
    start: () => ++started,
    accept: (n: number) => { if (n <= shown) return false; shown = n; return true; },
    reset: () => { shown = started; },
  };
}
/** The one quiet cross-fade when the suggested step really changes. */
export const STEP_FADE_MS = 250;

/** "Your assistant" is a name only at the start of a sentence: mid-sentence it is lower case. A real name (Claude Code) stays as it is. */
export const midSentence = (name: string): string => name === 'Your assistant' ? 'your assistant' : name;

/** The priority is: your own press, then work somebody else is doing, then what the last press left, then who has the wheel, then the assistant's own work, then the next step. */
export function slotOf(i: SlotInput): Slot {
  if (i.press) return { kind: 'press', who: 'semanticus', verb: i.press.verb, count: i.press.count };
  if (i.work) return { kind: 'work', who: i.work.who === 'assistant' ? 'assistant' : 'semanticus', verb: i.work.verb, count: i.work.progress };
  if (i.receipt) return i.receipt.kind === 'ran' ? { kind: 'done', text: i.receipt.text } : { kind: 'said', text: i.receipt.text };
  if (i.driver.connected && i.driver.mode === 'claudeDrives' && i.driver.wheel) return { kind: 'wheel', text: `You have the wheel. Your next message to ${midSentence(i.driver.name)} gives it back.` };
  if (i.driver.connected && i.driver.mode === 'claudeDrives' && i.driver.assistantWorking) return { kind: 'work', who: 'assistant', verb: ASSISTANT_WORKING, count: null };
  if (i.hasNext) return { kind: 'next' };
  return { kind: 'quiet', text: NOTHING_WAITING };
}

/** The switch shows "I drive" when the person drives, has paused the assistant's tasks, or has the wheel ("You have the wheel" never sits beside a pressed "Claude drives"): one switch for all three (1.5), so none is hidden. */
export function driveValue(mode: 'claudeDrives' | 'iDrive', queue: 'on' | 'paused', wheel = false): 'claudeDrives' | 'iDrive' {
  return mode === 'iDrive' || queue === 'paused' || wheel ? 'iDrive' : 'claudeDrives';
}
