// The reading card's pure half (Kane's pick A, 12 Oct 2026): the engine's card, and the words around it. Nothing here touches the page or the
// engine, so the node tests read it directly. The engine's own wording (ReadingCardBuilder) is plain already; these are the card's labels.

export type ReadingWho = 'semanticus' | 'assistant' | 'you';

export interface ReadingIssue {
  key: string;
  count: number;
  headline: string;
  why: string;
  who: ReadingWho;
  holdsGrade: boolean;
  /** "open", or "queued" when the assistant already has a writing job for it on the plan. */
  state: 'open' | 'queued';
  ruleIds: string[];
}

export interface ReadingCard {
  show: boolean;
  grade: string;
  score: number;
  gradeAfter: string | null;
  scoreAfter: number | null;
  heldBy: string[];
  safeFixCount: number;
  writingCount: number;
  writingJobs: number;
  openFindings: number;
  top: ReadingIssue[];
  revision: number;
  next: string;
}

export interface ReadingJob { rowId: string; words: string; count: number; key: string }

export interface FixThemResult {
  revision: number;
  fixed: number;
  gradeBefore: string;
  scoreBefore: number;
  gradeAfter: string;
  scoreAfter: number;
  jobs: ReadingJob[];
  sentence: string;
}

/** The beats of the card, in order. `reading` is the Link working, `grade` is the grade and the top three, `fixed` is the note with Undo. */
export type ReadingBeat = 'reading' | 'grade' | 'fixed';

/** The pill on an issue: who fixes it. After Fix them an issue shows what became of it. */
export function whoLabel(i: ReadingIssue, afterFix: boolean): string {
  if (i.who === 'semanticus') return afterFix ? 'Fixed' : 'Semanticus fixes';
  if (i.who === 'assistant') return afterFix || i.state === 'queued' ? 'Your assistant has it' : 'Your assistant writes';
  return 'You decide';
}

/** The line under the grade: what Fix them reaches, or, after it, where the grade came from. */
export function gradeNote(card: ReadingCard, fix: FixThemResult | null): string {
  if (fix) return fix.gradeBefore !== fix.gradeAfter ? `Was ${fix.gradeBefore}. ${fix.jobs.length > 0 ? 'Your assistant has the rest.' : ''}`.trim() : 'Your assistant has the rest.';
  if (card.gradeAfter && card.gradeAfter !== card.grade) return `AI readiness. ${card.gradeAfter} after Fix them.`;
  return 'AI readiness';
}

/** The line next to Fix them: what the press does. Empty when there is nothing to press. */
export function fixLine(card: ReadingCard): string {
  return card.next || '';
}

/** True when pressing Fix them does something: a safe fix to apply, or a writing job to give. */
export function canFix(card: ReadingCard): boolean {
  return card.safeFixCount > 0 || card.writingJobs > 0;
}

/** The sentence under the card after Fix them, from the engine. Falls back to the counts when an older engine sent none. */
export function receiptWords(fix: FixThemResult): string {
  if (fix.sentence) return fix.sentence;
  return `Semanticus fixed ${fix.fixed.toLocaleString('en-US')} things.`;
}

/** Undo is offered only while the model is still as Fix them left it: the assistant's first write, or anything else, ends it (History can still take it back).
 *  The page learns the new revision a moment after the press, so a revision BEFORE the fix's own is the page catching up, not the model moving on. */
export function canUndoFix(fix: FixThemResult | null, revision: number | undefined): boolean {
  return !!fix && fix.fixed > 0 && (revision === undefined || revision <= fix.revision);
}
