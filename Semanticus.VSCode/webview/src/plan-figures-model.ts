// The Plan header's three small figures (1.5, Kane's pick A "Three small figures", 12 Oct 2026). Pure: no page, no engine. Every number
// here is read off an engine answer and never worked out again: AI ready is the AI readiness scan (the reading card the header already
// reads), Best practice is the BPA scan's score and letter (the strip carries them, the number the Review page shows), Plan done is the
// engine's own count of done tasks over all tasks (the strip's tasksDone and tasksAll, which count the ones History holds).
import type { Grade } from './plan-calm-model';
import type { PlanStrip } from './plan-model';

export type FigureKey = 'ai' | 'bp' | 'plan';

export interface GradeFigure { key: 'ai' | 'bp'; label: string; letter: string; score: number | null }
export interface PlanFigure { key: 'plan'; label: string; done: number; all: number; percent: number }
export type Figure = GradeFigure | PlanFigure;

export const FIGURE_LABEL: Record<FigureKey, string> = { ai: 'AI ready', bp: 'Best practice', plan: 'Plan done' };

/** The percent of tasks done. 100 only when every task is done and 0 only when none is, so a long plan never rounds into a claim. */
export function percentOf(done: number, all: number): number {
  if (all <= 0) return 0;
  if (done >= all) return 100;
  if (done <= 0) return 0;
  return Math.min(99, Math.max(1, Math.round((done / all) * 100)));
}

export function aiFigure(grade: Grade | null | undefined): GradeFigure | null {
  return grade && grade.letter ? { key: 'ai', label: FIGURE_LABEL.ai, letter: grade.letter, score: grade.score } : null;
}

export function bpFigure(strip: Pick<PlanStrip, 'bestPractice' | 'bestPracticeScore'> | null | undefined): GradeFigure | null {
  const letter = strip?.bestPractice;
  return letter ? { key: 'bp', label: FIGURE_LABEL.bp, letter, score: typeof strip?.bestPracticeScore === 'number' ? strip.bestPracticeScore : null } : null;
}

/** Plan done is left out when the engine sent no counts (an older engine) or the plan has no task at all. */
export function planFigure(strip: Pick<PlanStrip, 'tasksDone' | 'tasksAll'> | null | undefined): PlanFigure | null {
  if (!strip || typeof strip.tasksDone !== 'number' || typeof strip.tasksAll !== 'number' || strip.tasksAll <= 0) return null;
  return { key: 'plan', label: FIGURE_LABEL.plan, done: strip.tasksDone, all: strip.tasksAll, percent: percentOf(strip.tasksDone, strip.tasksAll) };
}

/** The figures in their order. A figure with nothing behind it is not drawn, so a number is never a guess. */
export function figuresOf(grade: Grade | null | undefined, strip: PlanStrip | null | undefined): Figure[] {
  return [aiFigure(grade), bpFigure(strip), planFigure(strip)].filter((f): f is Figure => f !== null);
}

export interface FigureTip { head: string; what: string; src: string }

/** What a figure's tooltip says: what it measures and where it comes from. The same words for hover and keyboard focus. */
export function tipOf(f: Figure): FigureTip {
  if (f.key === 'plan') return {
    head: `Plan, ${f.done} of ${f.all} ${f.all === 1 ? 'task' : 'tasks'} done`,
    what: "Every task on this plan: yours, your assistant's and Semanticus's.",
    src: 'Counted from this Plan. Done tasks are in History.',
  };
  if (f.key === 'ai') return {
    head: f.score === null ? 'AI readiness' : `AI readiness, ${f.score} of 100`,
    what: 'How well Copilot and data agents can read this model: names, descriptions, synonyms and keys.',
    src: 'From the AI readiness check. Details in Review, AI readiness.',
  };
  return {
    head: f.score === null ? 'Best practice' : `Best practice, ${f.score} of 100`,
    what: 'Open errors and warnings for each object in the model. A means none are open.',
    src: 'From the Best practice rules. Details in Review, Model quality.',
  };
}

/** The plain words a screen reader says for a figure, before its tooltip. */
export function spokenOf(f: Figure): string {
  if (f.key === 'plan') return `${f.label}, ${f.percent} percent`;
  return f.score === null ? `${f.label}, grade ${f.letter}` : `${f.label}, grade ${f.letter}, ${f.score} of 100`;
}
