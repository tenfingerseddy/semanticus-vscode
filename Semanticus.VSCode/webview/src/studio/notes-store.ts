// The Studio's note queue and the suggestion line (1.4, contract sections 5 and 6). Pure: no React, no window, no
// bridge. The clock, the screen move and the driver arrive as a port, so test/studio-notes.test.mjs drives every rule
// with a fake clock and a fake driver.
//
// What it does, one line each:
//   a note shows in the strip and the screen moves to its place (only when the driver may move it), then the note's read
//     time runs, then the next one plays. The read time is clamp(1500 + 55 * length, 2500, 8000) ms.
//   four notes waiting to be shown fold into one: the newest, counting the rest, which go to What I did unread; a note that
//     comes after a fold joins it, so a long walk-through keeps the reader's pace (hero15 H16).
//   a note that presses a control plays at once, so the cursor finds the control while it is still there (hero15 H1).
//   once the work is over (no note and no assistant call for WORK_OVER_MS), a read note leaves the strip, and notes still
//     waiting go to What I did, unread (hero15 H4).
//   when the person has the wheel, or drives, nothing moves: the note waits with a Show me button and the strip says how
//     many more wait. Show me moves the screen once, as the person's own action.
//   Skip to now: no read times, the newest note's place, every note it passes read, no press for a skipped note.
//   the next prompt (the driver's resume) jumps to the newest waiting note and sends the older ones to What I did, unread.
//   the queue emptying after three or more notes opens What I did by itself.
//   a suggestion is one set of next steps; a step the person presses is marked added, and a note newer than the
//     suggestion takes it away.
import type { StudioNote, StudioNotesView, StudioSuggestion } from './types';

/** Four notes waiting to be shown fold into one note that counts them. */
export const FOLD_AT = 4;
/** No note and no assistant call for this long: the work is over (the driver's quiet gap, driver-store.ts QUIET_GAP_MS). */
export const WORK_OVER_MS = 8000;
/** The engine keeps the last 100 notes of a session (LocalEngine.Studio.cs); the Studio keeps the same. */
export const LOG_CAP = 100;
/** The queue emptying after this many notes opens What I did by itself. */
export const LIST_OPENS_AFTER = 3;
export const READ_BASE_MS = 1500;
export const READ_PER_CHAR_MS = 55;
export const READ_MIN_MS = 2500;
export const READ_MAX_MS = 8000;
/** How long a page stays on screen before the next note may move it to another page (drive follow, 13 Oct). A note on the same page still
 *  waits out its read time. This keeps the screen on the tool the assistant is using now, and still never flickers: one move per call. */
export const MOVE_DWELL_MS = 1800;

/** How long a note stays on screen while the person reads it. Reduced motion does not change it. */
export function readTimeMs(say: string): number {
  return Math.min(READ_MAX_MS, Math.max(READ_MIN_MS, READ_BASE_MS + READ_PER_CHAR_MS * (say?.length ?? 0)));
}

/** playing: the screen moved and the read time runs. waiting: held back, the strip offers Show me. done: read. */
export type StripPhase = 'playing' | 'waiting' | 'done';

export type SuggestionView = {
  suggestion: StudioSuggestion;
  /** Indexes of the steps the person pressed and that reached the plan. */
  added: readonly number[];
  /** The one step that did not reach the plan, with the engine's own sentence. */
  failed: { step: number; message: string } | null;
};

export type NotesSnapshot = {
  /** The note in the strip, or null. */
  current: StudioNote | null;
  phase: StripPhase | null;
  /** Notes behind the one in the strip. */
  more: number;
  /** Notes not yet shown, the one in the strip included while it waits. Skip to now needs two. */
  waiting: number;
  /** What I did: every note this session, oldest first, newest last. */
  log: readonly StudioNote[];
  /** Ids that went to What I did without being shown. */
  unread: ReadonlySet<string>;
  /** The note whose screen move Claude just made and whose press may play. The cursor reads this. Null otherwise. */
  playing: StudioNote | null;
  listOpen: boolean;
  suggestion: SuggestionView | null;
};

/** The part of the driver store (driver-store.ts, lane driver-bar) the queue needs. */
export type DriverPort = {
  canMoveScreen(): boolean;
  snapshot(): { resumeVersion: number; lastAssistantCall?: number | null };
  subscribe(fn: () => void): () => void;
  /** Tells the bar how many notes are left, so it can go back to idle honestly. */
  reportQueue?(count: number): void;
};

export type NotesDeps = {
  driver: DriverPort;
  /** Moves the Studio to the note's place and focus. Never asked to press anything. */
  moveTo(note: StudioNote): void;
  setTimer(fn: () => void, ms: number): unknown;
  clearTimer(handle: unknown): void;
};

const time = (iso: string | undefined | null) => { const t = Date.parse(iso ?? ''); return Number.isFinite(t) ? t : 0; };

export function createNotesStore(deps: NotesDeps) {
  const driver = deps.driver;
  let session: string | null = null;
  let log: StudioNote[] = [];
  let unread = new Set<string>();
  let fromEngine = new Set<string>();    // read from the engine's ring and not yet delivered live
  let queue: StudioNote[] = [];
  let current: StudioNote | null = null;
  let phase: StripPhase | null = null;
  let playing: StudioNote | null = null;
  let timer: unknown = null;
  let dwellTimer: unknown = null;
  let dwellOver = true;
  let burst = 0;
  let quiet: unknown = null;
  let workOver = false;
  let listOpen = false;
  let suggestion: { suggestion: StudioSuggestion; added: number[]; failed: SuggestionView['failed'] } | null = null;
  const listeners = new Set<() => void>();

  const unseenCurrent = () => (current && phase !== 'done' ? 1 : 0);
  const pending = () => queue.length + unseenCurrent();
  const waitingCount = () => queue.length + (current && phase === 'waiting' ? 1 : 0);
  function build(): NotesSnapshot {
    return {
      current, phase, more: queue.length, waiting: waitingCount(), log: log.slice(), unread: new Set(unread), playing, listOpen,
      suggestion: suggestion ? { suggestion: suggestion.suggestion, added: suggestion.added.slice(), failed: suggestion.failed } : null,
    };
  }
  let snap = build();
  let reported = -1;
  function emit() {
    snap = build();
    const count = pending();
    if (count !== reported) { reported = count; driver.reportQueue?.(count); }
    listeners.forEach((fn) => fn());
  }

  function stopTimer() {
    if (timer !== null) { deps.clearTimer(timer); timer = null; }
    if (dwellTimer !== null) { deps.clearTimer(dwellTimer); dwellTimer = null; }
  }


  /** A note or an assistant call: the work goes on, so the quiet starts again. */
  function activity() {
    workOver = false;
    if (quiet !== null) deps.clearTimer(quiet);
    quiet = deps.setTimer(() => { quiet = null; workOver = true; if (clearIfOver()) emit(); }, WORK_OVER_MS);
  }

  /** The work is over: a read note leaves the strip, and notes still waiting go to What I did, unread. A playing note finishes first. */
  function clearIfOver(): boolean {
    if (!workOver || phase === 'playing' || (!current && queue.length === 0)) return false;
    for (const n of [...(current && phase === 'waiting' ? [current] : []), ...queue]) unread.add(n.id);
    queue = []; current = null; phase = null; playing = null;
    return true;
  }

  /** Notes waiting to be shown fold into the newest when FOLD_AT wait, or when the one before the newest is already a fold. */
  function fold() {
    const held = !!current && phase === 'waiting';
    const waiting = [...(held ? [current!] : []), ...queue];
    const n = waiting.length;
    if (n < 2 || (n < FOLD_AT && !waiting[n - 2].folded)) return;
    const newest = waiting[n - 1];
    let folded = newest.folded ?? 0;
    for (const older of waiting.slice(0, -1)) { unread.add(older.id); folded += 1 + (older.folded ?? 0); }
    const one = { ...newest, folded };
    if (held) { current = one; queue = []; } else queue = [one];
  }

  /** The queue has run out: after three or more notes, What I did opens by itself. */
  function settle() {
    if (queue.length > 0 || phase === 'playing' || phase === 'waiting') return;
    if (burst >= LIST_OPENS_AFTER) listOpen = true;
    burst = 0;
  }

  function startPlaying(note: StudioNote) {
    stopTimer();
    current = note; phase = 'playing'; playing = note;
    unread.delete(note.id);
    deps.moveTo(note);
    timer = deps.setTimer(readEnd, readTimeMs(note.say));
    dwellOver = false;
    dwellTimer = deps.setTimer(() => { dwellTimer = null; dwellOver = true; if (advanceToOtherPage()) emit(); }, MOVE_DWELL_MS);
  }

  /** The note playing has held its page for the dwell and the next note is on another page: the assistant has moved on, so the screen does too. */
  function advanceToOtherPage(): boolean {
    if (phase !== 'playing' || !dwellOver || !playing || queue.length === 0 || !driver.canMoveScreen()) return false;
    if (queue[0].place === playing.place) return false;
    readEnd();
    return true;
  }

  function readEnd() {
    stopTimer();
    if (phase !== 'playing') return;
    phase = 'done'; playing = null;
    pump();
    settle();
    clearIfOver();
    emit();
  }

  /** Takes the next note off the queue when nothing holds it. */
  function pump() {
    if (phase === 'playing' || queue.length === 0) return;
    if (driver.canMoveScreen()) startPlaying(queue.shift()!);
    else if (!current || phase === 'done') { current = queue.shift()!; phase = 'waiting'; }
  }

  /** The person took the wheel (or switched to I drive) while a note played: it stays read, the rest wait. */
  function interrupt() {
    stopTimer();
    if (phase === 'playing') { phase = 'done'; playing = null; }
    pump();
  }

  /** The next prompt, or Let Claude drive: the screen jumps to the newest waiting note, the older ones go unread. */
  function resume() {
    const waiting = [...(current && phase === 'waiting' ? [current] : []), ...queue];
    if (waiting.length === 0) return;
    for (const note of waiting.slice(0, -1)) unread.add(note.id);
    queue = [];
    current = null; phase = null;
    startPlaying(waiting[waiting.length - 1]);
    settle();
  }

  let lastCan = driver.canMoveScreen();
  let lastResume = driver.snapshot().resumeVersion;
  let lastCall = driver.snapshot().lastAssistantCall ?? null;
  const offDriver = driver.subscribe(() => {
    const can = driver.canMoveScreen();
    const version = driver.snapshot().resumeVersion;
    const call = driver.snapshot().lastAssistantCall ?? null;
    if (call !== lastCall) { lastCall = call; activity(); }
    const stopped = lastCan && !can;
    const resumed = can && (!lastCan || version !== lastResume);
    lastCan = can; lastResume = version;
    if (stopped) interrupt();
    else if (resumed) resume();
    else return;
    emit();
  });

  function reset() {
    stopTimer();
    if (quiet !== null) { deps.clearTimer(quiet); quiet = null; }
    workOver = false;
    log = []; unread = new Set(); fromEngine = new Set(); queue = []; current = null; phase = null; playing = null;
    burst = 0; listOpen = false; suggestion = null;
  }

  function adoptSession(id: string | null | undefined) {
    if (!id) return;
    if (session && session !== id) reset();
    session = id;
  }

  function trimLog() {
    while (log.length > LOG_CAP) {
      const gone = log.shift()!;
      unread.delete(gone.id); fromEngine.delete(gone.id);
    }
  }

  return {
    getSnapshot: () => snap,
    subscribe(fn: () => void) { listeners.add(fn); return () => { listeners.delete(fn); }; },
    dispose() { stopTimer(); if (quiet !== null) deps.clearTimer(quiet); offDriver(); listeners.clear(); },

    /** A note arrived (studio/show). A note already known from the engine's ring still plays the first time it arrives live. */
    push(note: StudioNote) {
      if (!note?.id || typeof note.say !== 'string') return;
      adoptSession(note.sessionId);
      activity();
      // An update re-sends a note the Studio has (the engine counting a burst of one kind of call): the words change where the note is,
      // and nothing is queued, moved or counted as a new note. A note nobody has is simply a new note.
      if (note.update) {
        const same = (n: StudioNote | null | undefined) => !!n && n.id === note.id;
        if (log.some(same) || same(current) || queue.some(same)) {
          const { update: _update, ...fresh } = note;
          const swap = (n: StudioNote): StudioNote => (n.id === note.id ? { ...n, say: fresh.say, count: fresh.count } : n);
          log = log.map(swap); queue = queue.map(swap);
          if (current) current = swap(current);
          if (playing) playing = swap(playing);
          emit();
          return;
        }
        note = { ...note, update: false };
      }
      const known = log.some((n) => n.id === note.id);
      if (known && !fromEngine.has(note.id)) return;
      fromEngine.delete(note.id);
      if (!known) { log.push(note); trimLog(); }
      burst += 1;
      if (suggestion && time(note.at) > time(suggestion.suggestion.at)) suggestion = null;
      if (note.press && driver.canMoveScreen()) {
        // A press is now: the notes before it are passed (What I did marks them), and its control is on screen only for a moment.
        for (const older of queue) unread.add(older.id);
        queue = [];
        startPlaying(note);
      } else {
        queue.push(note);
        pump();
        fold();
        advanceToOtherPage();
      }
      emit();
    },

    /** What the engine kept (getStudioNotes): fills What I did and the suggestion. Plays nothing. */
    load(view: StudioNotesView | null | undefined) {
      if (!view) return;
      adoptSession(view.sessionId);
      let added = false;
      for (const note of view.notes ?? []) {
        if (!note?.id || log.some((n) => n.id === note.id)) continue;
        log.push(note); fromEngine.add(note.id); added = true;
      }
      if (added) { log.sort((a, b) => time(a.at) - time(b.at)); trimLog(); }
      if (view.suggestion && !suggestion && view.suggestion.id) suggestion = { suggestion: view.suggestion, added: [], failed: null };
      emit();
    },

    /** Show me, from the strip or a line of What I did: moves the screen once, as the person's own action. */
    showMe(id: string) {
      const note = log.find((n) => n.id === id) ?? (current?.id === id ? current : null);
      if (!note) return;
      deps.moveTo(note);
      unread.delete(id);
      queue = queue.filter((n) => n.id !== id);
      if (current?.id === id && phase === 'waiting') { phase = 'done'; pump(); }
      settle();
      emit();
    },

    /** Drops the read times: the newest note's place, every note on the way read, no press for those it passed. */
    skipToNow() {
      const unseen = [...(current && phase !== 'done' ? [current] : []), ...queue];
      if (unseen.length === 0) return;
      const newest = unseen[unseen.length - 1];
      for (const note of unseen) unread.delete(note.id);
      queue = [];
      if (driver.canMoveScreen()) startPlaying(newest);
      else { stopTimer(); deps.moveTo(newest); current = newest; phase = 'done'; playing = null; }
      settle();
      emit();
    },

    /** The strip's close: a read note leaves the strip. A note still to read stays. */
    dismiss() {
      if (current && phase === 'done' && queue.length === 0) { current = null; phase = null; emit(); }
    },

    setListOpen(open: boolean) { if (!open) burst = 0; if (listOpen !== open) { listOpen = open; emit(); } },

    /** suggest_next arrived (studio/suggest). The same id again changes nothing; a new one replaces the last. */
    setSuggestion(next: StudioSuggestion | null | undefined) {
      if (!next?.id || !Array.isArray(next.steps)) return;
      if (suggestion?.suggestion.id === next.id) return;
      adoptSession(next.sessionId);
      suggestion = { suggestion: next, added: [], failed: null };
      emit();
    },
    markStepAdded(index: number) {
      if (!suggestion || suggestion.added.includes(index)) return;
      suggestion.added.push(index);
      suggestion.failed = null;
      emit();
    },
    markStepFailed(index: number, message: string) {
      if (!suggestion) return;
      suggestion.failed = { step: index, message };
      emit();
    },
    clearSuggestion() { if (suggestion) { suggestion = null; emit(); } },
    reset() { reset(); emit(); },
  };
}

export type NotesStore = ReturnType<typeof createNotesStore>;
