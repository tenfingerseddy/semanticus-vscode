// The one note queue of the running Studio: the pure store bound to the driver, the screen and the clock, plus the hooks
// the strip, What I did and the suggestion line read it through.
import { useEffect, useSyncExternalStore } from 'react';
import { rpc, onReconnect, onStudioShow, onStudioSuggest } from '../bridge';
import { driverStore } from './driver-store';
import { moveScreenTo } from './notes-nav';
import { createNotesStore, type NotesSnapshot } from './notes-store';
import type { StudioNote, StudioNotesView, StudioSuggestion } from './types';

export const notesStore = createNotesStore({
  driver: {
    canMoveScreen: () => driverStore.canMoveScreen(),
    snapshot: () => driverStore.getSnapshot(),
    subscribe: (fn) => driverStore.subscribe(fn),
    reportQueue: (count) => driverStore.noteQueueSize(count),
  },
  moveTo: moveScreenTo,
  setTimer: (fn, ms) => window.setTimeout(fn, ms),
  clearTimer: (handle) => window.clearTimeout(handle as number),
});

export function useNotes(): NotesSnapshot {
  return useSyncExternalStore(notesStore.subscribe, notesStore.getSnapshot, notesStore.getSnapshot);
}

/** Reads what the engine kept (a reload or a reopened Studio keeps its list) and listens for new notes and suggestions. */
export function useNotesWire(): void {
  useEffect(() => {
    // The listeners go in first, so a note flushed by the host on studioReady is never missed while the read is out.
    const off = [onStudioShow((note) => notesStore.push(note as StudioNote)), onStudioSuggest((s) => notesStore.setSuggestion(s as StudioSuggestion))];
    const load = () => { void loadNotes(); };
    load();
    off.push(onReconnect(load));
    return () => off.forEach((fn) => fn());
  }, []);
}

/** getStudioNotes: an engine one release behind does not have it, and then there is simply nothing to list. */
export async function loadNotes(): Promise<void> {
  try { notesStore.load(await rpc<StudioNotesView>('getStudioNotes')); } catch { /* nothing kept, or an older engine */ }
}
