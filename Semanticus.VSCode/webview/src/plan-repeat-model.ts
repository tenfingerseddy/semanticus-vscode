// Repeat cards on the Plan, the pure half (lane RP; contract section 1.7, Kane 24 Sep: "yes agree").
//
// The engine owns every fact here: which repeats are saved, when each comes due next, and which row is a repeat's
// card that is still open. This module only decides what the row detail offers, and says it in plain words. It
// does not work out a due date and it never adds a card: a card comes due when the engine composes the plan.
//
// Three kinds of repeat, one editor:
//   words     a task typed on the Plan (Your words, You added) comes back for the same doer
//   fix       a row Semanticus does itself: when it comes due, that same row is marked as the repeat's card and
//             Run the plan applies it; no second row is made
//   workflow  saved on Workflows; its card waits for a person to press Start (added from Workflows, edited here)

export type CadenceKind = 'daily' | 'weekly' | 'monthly';

export interface RepeatCadence {
  kind: CadenceKind;
  every: number;
  day?: string | null;
  date?: number | null;
}

/** One repeat as get_model_plan lists it (ModelPlanView.Repeats). */
export interface PlanRepeat {
  id: string;
  words: string;
  workflow?: string | null;
  fix?: string | null;
  doer: 'semanticus' | 'you' | 'assistant';
  stock?: string | null;
  cadence: RepeatCadence;
  cadenceText: string;
  nextDueUtc: string;
  held: boolean;
  openItemId?: string | null;
  /** The task it was made from with Repeat this: that row shows it (UAT S12-6-003). */
  fromItemId?: string | null;
  /** Why it does not come due now though not paused: its task is set aside, or its object is gone. */
  resting?: string | null;
}

/** A stock repeat offered for one click (ModelPlanView.RepeatStarters). */
export interface PlanRepeatStarter {
  stock: string;
  words: string;
  doer: 'you' | 'assistant';
  page?: string | null;
  cadence: RepeatCadence;
  cadenceText: string;
}

/** What addPlanRepeat and setPlanRepeat take (PlanRepeatRequest). A null field on a change keeps what is saved. */
export interface RepeatRequest {
  words?: string;
  workflow?: string;
  fix?: string;
  doer?: string;
  cadence?: RepeatCadence;
  stock?: string;
  held?: boolean;
  /** The task a words repeat is made from, so the repeat belongs to it. */
  fromTask?: string;
}

/** The fields of a Plan row this module reads. Kept narrow so it works on the old and the new row shapes. */
export interface RepeatRow {
  id: string;
  title: string;
  source?: string | null;
  doer?: string | null;
  stateWord?: string | null;
}

export const DAYS = ['monday', 'tuesday', 'wednesday', 'thursday', 'friday', 'saturday', 'sunday'] as const;

/** The found-row families a fix repeat may name: rows Semanticus works out and applies itself
 *  (PlanRepeats.FixFamilies in the engine). */
export const FIX_FAMILIES = ['safefix/', 'describe/', 'cleanup/', 'recheck/', 'ship/', 'proposal/', 'datetable/', 'calendar/', 'starter/', 'check/'];

export const PRO_LINE = 'Repeats are part of Semanticus Pro. Yours are saved, and they come due again when Pro is back.';

const capital = (s: string): string => (s ? s[0].toUpperCase() + s.slice(1) : s);

function ordinal(n: number): string {
  const tens = n % 100;
  const suffix = tens >= 11 && tens <= 13 ? 'th' : ({ 1: 'st', 2: 'nd', 3: 'rd' } as Record<number, string>)[n % 10] ?? 'th';
  return `${n}${suffix}`;
}

/** The cadence in words, the same sentence the engine writes (PlanRepeats.CadenceText), for the editor's preview
 *  before a repeat is saved. A saved repeat shows the engine's own cadenceText. */
export function cadenceText(c: RepeatCadence): string {
  const n = c.every && c.every > 0 ? c.every : 1;
  const every = (one: string, many: string) => (n === 1 ? `Every ${one}` : `Every ${n} ${many}`);
  if (c.kind === 'daily') return every('day', 'days');
  if (c.kind === 'weekly') return every('week', 'weeks') + (c.day ? `, on ${capital(c.day)}` : '');
  if (c.date == null) return every('month', 'months');
  return `${every('month', 'months')}, on the ${ordinal(c.date)}${c.date >= 29 ? ' or the last day' : ''}`;
}

/** The repeat this row belongs to: the repeat whose open card it is, or, for a found row, the fix repeat that
 *  names it (that row IS the fix repeat's card whenever it comes due). */
export function repeatForRow(rowId: string, repeats: PlanRepeat[] | null | undefined): PlanRepeat | null {
  return (repeats ?? []).find((r) => r.openItemId === rowId || (r.fix != null && r.fix === rowId) || r.fromItemId === rowId
    || (r.workflow != null && rowId === WORKFLOW_ROW + r.workflow)) ?? null;
}

/** A saved workflow's own row in its Settings drawer (lane W2, contract 21.5 "Repeat on the Plan"): not a Plan row,
 *  just the key the editor reads a workflow repeat by. Its card waits for a person to press Run. */
export const WORKFLOW_ROW = 'workflow:';

/** The repeat a row can be SET UP from: a fix for a row Semanticus does itself, words for a task the person asked
 *  for. Null when the row cannot repeat: a Jev row, an assistant's own row, an aim, a workflow run, or a row that
 *  is already a repeat's card. */
export function repeatRequestFor(row: RepeatRow, repeats: PlanRepeat[] | null | undefined): RepeatRequest | null {
  if (!row?.id || repeatForRow(row.id, repeats)) return null;
  if ((repeats ?? []).some((r) => r.fix === row.id)) return null;
  if (row.source === 'workflow-def' && row.id.startsWith(WORKFLOW_ROW)) return { workflow: row.id.slice(WORKFLOW_ROW.length), doer: 'you' };
  if (row.source === 'found' && row.doer === 'semanticus' && FIX_FAMILIES.some((p) => row.id.startsWith(p))) return { fix: row.id };
  if ((row.source === 'words' || row.source === 'you') && row.id.startsWith('pt-') && (row.doer === 'you' || row.doer === 'assistant'))
    return { words: row.title, doer: row.doer, fromTask: row.id };
  return null;
}

/** "28 September", in the person's own calendar. */
export function dueDay(iso: string): string {
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return '';
  return d.toLocaleDateString('en-GB', { day: 'numeric', month: 'long' });
}

/** The line under a saved repeat, in the order the engine's rules apply: paused, then a card still open, then the date. */
export function dueLine(r: PlanRepeat, isPro: boolean): string {
  if (!isPro) return 'Not coming due on free.';
  if (r.held) return 'Paused. It does not come due until you resume it.';
  if (r.resting) return r.resting;
  if (r.openItemId) return `This one is still open. The next comes after it is done and ${dueDay(r.nextDueUtc)} has passed.`;
  return r.fix
    ? `Next on ${dueDay(r.nextDueUtc)}. Semanticus marks this row then, and Run the plan does it.`
    : `Next on ${dueDay(r.nextDueUtc)}. Nothing runs by itself.`;
}

/** Which face the editor shows on a row. */
export type RepeatFace = 'none' | 'offer' | 'saved' | 'pro';

export function repeatFace(row: RepeatRow, repeats: PlanRepeat[] | null | undefined, isPro: boolean): RepeatFace {
  if (repeatForRow(row.id, repeats)) return 'saved';
  if (!repeatRequestFor(row, repeats)) return 'none';
  return isPro ? 'offer' : 'pro';
}

/** A cadence checked the way the engine checks it, so the Save button is off rather than refused. */
/** The same schedule: every field the form edits (a form value and a held one are both whole cadences, so a key-by-key comparison is exact). */
export const sameCadence = (a: RepeatCadence, b: RepeatCadence): boolean => a.kind === b.kind && a.every === b.every && (a.day ?? null) === (b.day ?? null) && (a.date ?? null) === (b.date ?? null);

export function cadenceProblem(c: RepeatCadence): string | null {
  if (!c || !['daily', 'weekly', 'monthly'].includes(c.kind)) return 'Choose how often.';
  if (!Number.isInteger(c.every) || c.every < 1 || c.every > 12) return 'Every 1 to 12.';
  if (c.kind === 'weekly' && !(DAYS as readonly string[]).includes(c.day ?? '')) return 'Choose a day.';
  if (c.kind === 'monthly' && !(Number.isInteger(c.date) && (c.date as number) >= 1 && (c.date as number) <= 31)) return 'Choose a date, 1 to 31.';
  return null;
}

/** The cadence a new repeat starts from: every week, on today's day. */
export function defaultCadence(now: Date = new Date()): RepeatCadence {
  const day = DAYS[(now.getDay() + 6) % 7];
  return { kind: 'weekly', every: 1, day, date: null };
}

/** A cadence switched to a kind, keeping only what that kind uses. */
export function withKind(c: RepeatCadence, kind: CadenceKind, now: Date = new Date()): RepeatCadence {
  if (kind === 'weekly') return { kind, every: c.every, day: c.day ?? DAYS[(now.getDay() + 6) % 7], date: null };
  if (kind === 'monthly') return { kind, every: c.every, day: null, date: c.date ?? now.getDate() };
  return { kind, every: c.every, day: null, date: null };
}
