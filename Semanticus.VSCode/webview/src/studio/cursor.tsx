import { useLayoutEffect } from 'react';
import { driverStore } from './driver-store';
import { moveScreenTo } from './notes-nav';
import { useNotes } from './notes-runtime';
import pressMap from './presses.json';
import type { StudioNote } from './types';
import './studio-cursor.css';

export function pressFor(note: { press?: string | null; tool?: string | null }) {
  return note.press
    ? pressMap.presses.find((press) => press.id === note.press)
    : pressMap.presses.find((press) => press.tools.includes(note.tool ?? ''));
}

/** How long the real control shows pressed (Kane, 11 Oct: "seeing the buttons clicked clearly" matters most). */
export const PRESS_MS = 1500;
/** How long a press waits for its page to open and draw the button. A page that has not drawn it by then gets no ring and the press stays noted. */
export const PRESS_WAIT_MS = 4000;

type Timers = { set(fn: () => void, ms: number): unknown; clear(id: unknown): void };
type Control = { setAttribute(name: string, value: string): void; removeAttribute(name: string): void };

/** The control itself shows pressed and ringed (data-claude-press, studio-cursor.css) for PRESS_MS. The returned stop lets go at once
 *  (the person took the wheel, or the next note plays). There is no mouse pointer: the ring on the real button is the whole press. */
export function pressControl(control: Control, timers: Timers): () => void {
  control.setAttribute('data-claude-press', 'on');
  let release: unknown = null;
  release = timers.set(() => { release = null; control.removeAttribute('data-claude-press'); }, PRESS_MS);
  return () => {
    if (release !== null) timers.clear(release);
    release = null;
    control.removeAttribute('data-claude-press');
  };
}

export type PressPort<T> = {
  now(): number;
  frame(fn: () => void): number;
  cancelFrame(id: number): void;
  /** False once the person has the wheel or drives: nothing is shown, and nothing already shown stays. */
  canMove(): boolean;
  /** The control as drawn on the current page, or null when this page does not have it (yet). */
  find(): T | null;
  /** Opens the page where the control is. Called at most once, and only when the note did not already open it. */
  openPage(): void;
  /** Rings the control, brought into view first. Returns the let-go. */
  show(control: T): () => void;
};

/** The assistant pressed a real button. If the button is on this page, ring it now. If not, open its page, then look on every frame until
 *  it is drawn (the page may load its data first) and ring it. Never moves a mouse pointer, never clicks. Gives up quietly after
 *  PRESS_WAIT_MS. The returned stop cancels the wait and lets go of a ring that is showing.
 *  Why it waits: the old check looked once, on the frame after the note, and only for a button fully inside the window, so a button on
 *  another page, still loading, or below the fold never got a ring (Kane's session, 12 Oct). */
export function playPress<T>(port: PressPort<T>, openPage: boolean): () => void {
  if (!port.canMove()) return () => {};
  const start = port.now();
  let frame: number | null = null;
  let letGo: (() => void) | null = null;
  let stopped = false;
  const stop = () => {
    stopped = true;
    if (frame !== null) port.cancelFrame(frame);
    frame = null;
    letGo?.(); letGo = null;
  };
  const look = () => {
    frame = null;
    if (stopped) return;
    if (!port.canMove()) { stop(); return; }
    const control = port.find();
    if (control) { letGo = port.show(control); return; }
    if (port.now() - start >= PRESS_WAIT_MS) return;
    frame = port.frame(look);
  };
  if (openPage && port.find() === null) port.openPage();
  look();
  return stop;
}

/** A button that is really drawn: it has a size and is not hidden. It does not have to be inside the window; showing it scrolls it in. */
function drawn(selector: string): HTMLElement | null {
  return [...document.querySelectorAll<HTMLElement>(selector)].find((element) => {
    const rect = element.getBoundingClientRect();
    const style = getComputedStyle(element);
    return rect.width > 0 && rect.height > 0 && style.visibility !== 'hidden' && style.display !== 'none';
  }) ?? null;
}

/** The ring on the real button for every assistant press. Draws nothing of its own. */
export function StudioCursor(): null {
  const { playing } = useNotes();
  useLayoutEffect(() => {
    const press = playing && pressFor(playing);
    if (!playing || !press || !driverStore.canMoveScreen()) return;
    const note = playing;
    const stop = playPress<HTMLElement>({
      now: () => performance.now(),
      frame: (fn) => requestAnimationFrame(fn),
      cancelFrame: (id) => cancelAnimationFrame(id),
      canMove: driverStore.canMoveScreen,
      find: () => drawn(press.selector),
      // The note already opened its own place. Only a press whose button lives on another page than that needs its page opened here.
      openPage: () => moveScreenTo({ ...note, place: press.place, focus: '' } as StudioNote),
      show: (control) => {
        control.scrollIntoView?.({ block: 'nearest', inline: 'nearest' });
        return pressControl(control, { set: (fn, ms) => window.setTimeout(fn, ms), clear: (id) => window.clearTimeout(id as number) });
      },
    }, note.place !== press.place);
    const off = driverStore.subscribe(() => { if (!driverStore.canMoveScreen()) stop(); });
    return () => { off(); stop(); };
  }, [playing]);
  return null;
}
