import { useSyncExternalStore } from 'react';

export type DriveMode = 'claudeDrives' | 'iDrive';
export type DriverState = 'idle' | 'claudeDriving' | 'personHasWheel';
/** The attached assistant, as the engine mapped it from the MCP clientInfo (AssistantIdentity.cs). Initials are null for an assistant with no usable name. */
export type AssistantWho = { key: string; name: string; initials: string | null; version?: string | null };
export type DriverSnapshot = {
  mode: DriveMode;
  state: DriverState;
  /** The connection file is in place. It says nothing about whether an assistant has started. */
  assistantConnected: boolean;
  /** An assistant has actually called or shown something. Only this lights the jade edge and the driving words. */
  assistantWorking: boolean;
  /** Who the assistant is, once the engine has read its name; null before. The sign and the bar's words use it. */
  assistant: AssistantWho | null;
  lastPersonInput: number | null;
  /** When the assistant's latest tool call began, from the engine's own clock; null before the first. */
  lastAssistantCall: number | null;
  pendingNotes: number;
  /** Notes consumes this change by keeping the newest waiting note. */
  resumeVersion: number;
};
/**
 * "Until their next prompt" (Kane). The Studio cannot see what the person types to Claude, so it reads the quiet: a run of tool
 * calls is one busy task, and the first call that starts after a gap this long, counted from the last call or the person's last
 * click, is a new prompt. Inside a busy task the wheel stays with the person however long the task runs.
 */
export const QUIET_GAP_MS = 8_000;

/** Is the assistant at work right now: a note is still being shown, or its latest call began less than one quiet gap ago.
 *  The sign pulses only then. Pure, so the bar and the tests ask the same question. */
export function assistantIsWorking(s: DriverSnapshot, now: number): boolean {
  if (!s.assistantWorking) return false;
  if (s.state === 'claudeDriving') return true;
  return s.lastAssistantCall !== null && now - s.lastAssistantCall < QUIET_GAP_MS;
}

/** How long an assistant counts as busy on the Plan page after its latest call. Claude's gaps between calls (thinking, a long write) run past the 8 s quiet gap,
 *  and the page told the person "Save your changes / Do it" in the middle of a drive (hero20 D3). The sign keeps the short gap; the Plan header uses this.
 *  Short on purpose (UAT 18 Oct N21): 40 s after Claude finished the page still said "Working on your model" with nobody working. The turn is over when no call comes in this time.
 *  45 s, not 30 (round 2): a real drive had a 35 s gap between calls while Claude composed 116 descriptions, and the 30 s window showed the next step for about 5 s mid-drive.
 *  45 s covers that gap and is still gone well inside a minute after the last call. */
export const ASSISTANT_ACTIVE_MS = 45_000;
export function assistantIsActive(s: DriverSnapshot, now: number): boolean {
  if (!s.assistantWorking) return false;
  // A note still queued (claudeDriving) is a reason to stay on the call's clock, not to ignore it: a drive that never ended must not read as work for ever.
  return s.lastAssistantCall !== null ? now - s.lastAssistantCall < ASSISTANT_ACTIVE_MS : s.state === 'claudeDriving';
}

const whoOf = (raw: unknown): AssistantWho | null => {
  const w = raw as Partial<AssistantWho> | null | undefined;
  return w && typeof w.key === 'string' && typeof w.name === 'string' && w.name
    ? { key: w.key, name: w.name, initials: typeof w.initials === 'string' && w.initials ? w.initials : null, version: w.version ?? null } : null;
};

export function createDriverStore(mode: DriveMode = 'iDrive') {
  let snapshot: DriverSnapshot = {
    mode, state: 'idle', assistantConnected: false, assistantWorking: false, assistant: null, lastPersonInput: null, lastAssistantCall: null,
    pendingNotes: 0, resumeVersion: 0,
  };
  const listeners = new Set<() => void>();
  const update = (patch: Partial<DriverSnapshot>) => {
    const next = { ...snapshot, ...patch };
    if (Object.keys(patch).every((key) => next[key as keyof DriverSnapshot] === snapshot[key as keyof DriverSnapshot])) return;
    snapshot = next;
    for (const fn of listeners) fn();
  };
  /** The engine's answer to "who is attached" (the entitlement read or an assistant/call). A missing answer keeps what was known. */
  function setAssistant(raw: unknown) {
    const who = whoOf(raw);
    const now = snapshot.assistant;
    // A fresh object always differs by identity, so compare the fields: every call carries the same answer and must not redraw the bar.
    if (who && !(now && now.key === who.key && now.name === who.name && now.initials === who.initials)) update({ assistant: who });
  }
  const canMoveScreen = () => snapshot.mode === 'claudeDrives' && snapshot.state !== 'personHasWheel';
  const resume = (state: DriverState = snapshot.pendingNotes ? 'claudeDriving' : 'idle') => update({
    state,
    pendingNotes: Math.min(snapshot.pendingNotes, 1), resumeVersion: snapshot.resumeVersion + 1,
  });
  return {
    getSnapshot: () => snapshot,
    subscribe(fn: () => void) { listeners.add(fn); return () => { listeners.delete(fn); }; },
    canMoveScreen,
    setAssistant,
    setConnected(connected: boolean) { update({ assistantConnected: connected || snapshot.assistantConnected }); },
    setMode(next: DriveMode) {
      if (next !== snapshot.mode) update({ mode: next, state: 'idle' });
    },
    noteShow() {
      update({ assistantWorking: true, pendingNotes: snapshot.pendingNotes + 1,
        state: canMoveScreen() ? 'claudeDriving' : snapshot.state });
    },
    /** Notes owns the FIFO; this count lets the bar resume an empty queue honestly. */
    noteQueueSize(count: number) {
      update({ pendingNotes: Math.max(0, count),
        state: count === 0 && snapshot.state === 'claudeDriving' ? 'idle' : snapshot.state });
    },
    notePersonInput(at = Date.now()) {
      update({ lastPersonInput: at,
        state: snapshot.mode === 'claudeDrives' ? 'personHasWheel' : 'idle' });
    },
    /** `roundStart` is when the Copilot test round this call belongs to began (copilot-follow.ts), or null. A click made during that round holds the
        wheel for the rest of it: a quiet gap inside a round is the student working, not a new prompt. */
    noteAssistantCall(at: string, assistant?: unknown, roundStart: number | null = null) {
      const started = Date.parse(at);
      update({ assistantConnected: true, assistantWorking: true });
      setAssistant(assistant);
      if (!Number.isFinite(started)) return;
      const before = snapshot.lastAssistantCall;
      // Only calls that began after the person's input count, so the quiet is measured from the later of the last call and the click.
      const since = Math.max(before ?? -Infinity, snapshot.lastPersonInput ?? -Infinity);
      const clickedDuringRound = roundStart !== null && snapshot.lastPersonInput !== null && snapshot.lastPersonInput > roundStart;
      if (snapshot.mode === 'claudeDrives' && snapshot.state === 'personHasWheel' && !clickedDuringRound
          && snapshot.lastPersonInput !== null && started > snapshot.lastPersonInput && started - since >= QUIET_GAP_MS) resume('claudeDriving');
      update({ lastAssistantCall: Math.max(before ?? -Infinity, started) });
    },
    letClaudeDrive() {
      if (snapshot.mode === 'claudeDrives' && snapshot.state === 'personHasWheel') resume();
    },
  };
}

export const driverStore = createDriverStore();
export const canMoveScreen = driverStore.canMoveScreen;
export const notePersonInput = driverStore.notePersonInput;
export const noteAssistantCall = driverStore.noteAssistantCall;
export const setAssistant = driverStore.setAssistant;
export const setMode = driverStore.setMode;
export const noteShow = driverStore.noteShow;
export const noteQueueSize = driverStore.noteQueueSize;
export const letClaudeDrive = driverStore.letClaudeDrive;
export const subscribeDriver = driverStore.subscribe;
export function useDriver(): DriverSnapshot {
  return useSyncExternalStore(driverStore.subscribe, driverStore.getSnapshot, driverStore.getSnapshot);
}
