// The Learn tour (1.4, contract section 9): chapter scripts, recorded Ask answers and the player that plays a chapter. Pure: no React,
// no window, no bridge. The clock, the notes store and the driver arrive as ports, so test/learn-tour.test.mjs drives every rule
// with a fake clock.
//
// What the player does, one line each:
//   a step is played as a StudioNote with by "tour" through the one notes store, so the strip, the screen move and the cursor
//     are the ones Claude's notes use. The tour calls no engine tool and no assistant.
//   a step stays on screen until the person presses Next (or the shown control). It never moves on by itself: a reader who is
//     still on step 3 when a timer moved the tour to step 4 would press Next and skip step 4. Next always goes to the step after
//     the one on screen.
//   when the person takes the wheel, the tour stops where it is and waits for Continue. It never moves the screen against them.
//   the last step ends the chapter: the end line is the assistant try-out when one is connected, else a plain pointer to connect.
import { readTimeMs } from './notes-store';
import type { StudioNote } from './types';

export type TourWait = 'read' | { ms: number } | { press: true };

export type TourStep = {
  place: string;
  focus?: string | null;
  press?: string | null;
  say: string;
  /** null or missing: take the sentence from the place map. */
  how?: string | null;
  wait: TourWait;
  /** What to say when the tour is about the person's own model and `say` talks about the demo's numbers. */
  sayOwn?: string;
  /** A page that is empty on the demo (it has no data): the step plays on the person's own model only. */
  ownOnly?: boolean;
  /** Index into the demo model's recorded answers. The Ask page shows that answer with the Example tag while the step plays. */
  example?: number;
};

export type TourChapter = {
  version: number;
  id: string;
  title: string;
  minutes: number;
  tryWithClaude: string;
  steps: TourStep[];
};

/** The chapter as it plays: on the person's own model the steps speak of that model, and on the demo the steps that need data are left out. */
export function chapterFor(chapter: TourChapter, own: boolean): TourChapter {
  const steps = chapter.steps
    .filter((s) => own || !s.ownOnly)
    .map((s) => (own ? { ...s, say: s.sayOwn ?? s.say, example: undefined } : s));
  return { ...chapter, steps };
}

export type ChapterEntry = { id: string; title: string; minutes: number; line: string };

export type RecordedColumn = { name: string; type?: string; format?: string | null };
export type RecordedAnswer = {
  question: string;
  measure: string;
  columns: RecordedColumn[];
  rows: unknown[][];
  dax: string;
  chart: 'bar' | 'line' | 'area';
};

export const SAY_MAX = 200;
export const NO_ASSISTANT_END = 'Connect your assistant to try this live.';

/** The sentence under the note: the step's own `how`, else the place's. A place the map does not know gives none. */
export function howOf(step: TourStep, places: readonly { id: string; how: string }[]): string {
  if (step.how) return step.how;
  return places.find((p) => p.id === step.place)?.how ?? '';
}

/** One step as the note the notes store plays. The id is "tour-<chapter>-<n>" (n from 1), as the contract names it. */
export function noteOfStep(chapterId: string, index: number, step: TourStep, how: string, sessionId: string, at: Date): StudioNote {
  return {
    id: `tour-${chapterId}-${index + 1}`,
    say: step.say, how, place: step.place,
    focus: step.focus ?? null, press: step.press ?? null,
    at: at.toISOString(), by: 'tour', sessionId,
  };
}

/** How long a step is meant to be read, or null when the person's press of the shown control ends it. The player does not use it as a timer. */
export function waitMs(step: TourStep): number | null {
  const read = readTimeMs(step.say);
  if (step.wait === 'read') return read;
  if ('press' in step.wait) return null;
  return Math.max(read, step.wait.ms);
}

/** The line that ends a chapter. */
export function endLine(chapter: Pick<TourChapter, 'tryWithClaude'>, assistantConnected: boolean): string {
  return assistantConnected ? chapter.tryWithClaude : NO_ASSISTANT_END;
}

/** A recorded answer as the Ask page's own answer body reads it. The Ask page adds the Example tag and the one line under it. */
export function answerOf(recorded: RecordedAnswer) {
  const columns = recorded.columns.map((c) => ({ name: c.name, type: c.type }));
  const formats = recorded.columns.map((c) => c.format ?? null);
  const split = recorded.columns.length > 1 ? recorded.columns.slice(1).map((c) => c.name) : [recorded.measure];
  return {
    answer: {
      outcome: 'answered', statement: `This shows ${split.join(' and ')} for each ${recorded.columns[0].name}.`,
      columns, formats, rows: recorded.rows, rowCount: recorded.rows.length, dax: recorded.dax, checks: [], readBy: 'example',
    },
    parts: { measures: split, groupBy: [recorded.columns[0].name], filters: [] },
    viz: recorded.chart,
  };
}

export type TourStatus = 'idle' | 'playing' | 'paused' | 'done';
export type TourSnapshot = {
  status: TourStatus;
  chapterId: string | null;
  /** 0-based index of the step playing or last played. */
  index: number;
  total: number;
  /** The recorded answer the Ask page shows now, or null. */
  example: number | null;
  /** True while the player waits for the person to press the shown control. */
  waitingForPress: boolean;
};

export type TourPorts = {
  push(note: StudioNote): void;
  canMove(): boolean;
  places: readonly { id: string; how: string }[];
  now(): Date;
};

export function createTourPlayer(ports: TourPorts) {
  let chapter: TourChapter | null = null;
  let sessionId = '';
  let status: TourStatus = 'idle';
  let index = 0;
  let example: number | null = null;
  let waitingForPress = false;
  let shown = false;            // the step at `index` has been pushed
  const listeners = new Set<() => void>();
  let snap = build();

  function build(): TourSnapshot {
    return { status, chapterId: chapter?.id ?? null, index, total: chapter?.steps.length ?? 0, example, waitingForPress };
  }
  function emit() { snap = build(); listeners.forEach((fn) => fn()); }

  function finish() {
    status = 'done'; waitingForPress = false; shown = false; emit();
  }

  function advance() {
    waitingForPress = false;
    if (!chapter) return;
    if (index + 1 >= chapter.steps.length) { finish(); return; }
    index += 1; shown = false;
    playStep();
  }

  function playStep() {
    if (!chapter || status === 'idle' || status === 'done') return;
    if (!ports.canMove()) { status = 'paused'; emit(); return; }
    status = 'playing';
    const step = chapter.steps[index];
    shown = true;
    if (typeof step.example === 'number') example = step.example;
    ports.push(noteOfStep(chapter.id, index, step, howOf(step, ports.places), sessionId, ports.now()));
    waitingForPress = waitMs(step) === null;
    emit();
  }

  /** Continue after the person took the wheel: a step that was never shown plays; one that was shown moves on. */
  function resume() {
    if (status !== 'paused') return;
    if (!shown) { playStep(); return; }
    advance();
  }

  return {
    getSnapshot: () => snap,
    subscribe(fn: () => void) { listeners.add(fn); return () => { listeners.delete(fn); }; },

    /** Starts a chapter on the demo model's session. A chapter already playing is replaced. */
    start(next: TourChapter, session: string) {
      chapter = next; sessionId = session; index = 0; example = null; shown = false; waitingForPress = false;
      if (next.steps.length === 0) { status = 'done'; emit(); return; }
      status = 'playing';
      playStep();
    },

    /** The person pressed the control the step showed. Ends a press wait. A press at any other time changes nothing. */
    pressed() { if (status === 'playing' && waitingForPress) advance(); },

    /** Next: plays the step after the one on screen. */
    next() { if (status === 'playing') advance(); else if (status === 'paused') resume(); },

    /** The person took the wheel (or switched to I drive): the tour stops where it is. */
    interrupt() {
      if (status !== 'playing') return;
      status = 'paused'; emit();
    },

    resume,

    /** Leaves the tour. The caller closes the demo model. */
    stop() {
      chapter = null; status = 'idle'; index = 0; example = null; shown = false; waitingForPress = false; emit();
    },
  };
}

export type TourPlayer = ReturnType<typeof createTourPlayer>;
