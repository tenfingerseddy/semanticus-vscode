// What the rest of the Studio may know about the Learn tour (1.4, lane tour), in a file with no chapter scripts, no bridge and no
// stylesheet. Help and the Ask page read it; tour.tsx (the player, the bar, the scripts) fills it in when the Studio starts.
// Keeping this apart is what lets those two pages stay light: a test that loads Help or Ask does not load the whole tour.
import { useSyncExternalStore } from 'react';
import type { ChapterEntry, RecordedAnswer, TourSnapshot } from './tour-model';

export type LearnPhase = 'idle' | 'starting' | 'running' | 'failed';
export type LearnRuntime = { phase: LearnPhase; chapterId: string | null; message: string | null };
export type LearnView = { runtime: LearnRuntime; tour: TourSnapshot };

const IDLE: LearnView = {
  runtime: { phase: 'idle', chapterId: null, message: null },
  tour: { status: 'idle', chapterId: null, index: 0, total: 0, example: null, waitingForPress: false },
};
let view: LearnView = IDLE;
const listeners = new Set<() => void>();
export function setLearnView(next: LearnView): void {
  view = next;
  listeners.forEach((fn) => fn());
}
const subscribe = (fn: () => void) => { listeners.add(fn); return () => { listeners.delete(fn); }; };
const getView = () => view;
export function useLearn(): LearnView {
  return useSyncExternalStore(subscribe, getView, getView);
}
/** True while a chapter plays on the demo model. The Ask page reads it to show its recorded examples. */
export const learnActive = (v: LearnView) => v.runtime.phase === 'running';

type Registration = { chapters: readonly ChapterEntry[]; examples: readonly RecordedAnswer[]; start(chapterId: string): void };
let registration: Registration | null = null;
export function registerLearn(next: Registration): void { registration = next; }
/** The chapters Help lists. Empty until the tour has loaded, which is before any page draws. */
export const learnChapters = (): readonly ChapterEntry[] => registration?.chapters ?? [];
/** The recorded Ask answers of the demo model. */
export const learnExamples = (): readonly RecordedAnswer[] => registration?.examples ?? [];
/** Starts a chapter. Help's Learn list calls it for each chapter, and the first run's "Show me around" calls it with "plan". */
export function startTour(chapterId: string): void { registration?.start(chapterId); }
