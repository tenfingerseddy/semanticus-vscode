// How a note moves the screen (contract section 1, "Webview"). One function, the same listeners a host `navigate`
// reaches, so it needs nothing the Studio does not already have. Never presses anything: presses are the cursor's.
import { dispatchNavigate, revealInTree } from '../bridge';
import type { StudioNote } from './types';
import placeMap from './places.json';

export type Place = { id: string; tool: string; target?: string; label: string; how?: string };
const PLACES: readonly Place[] = placeMap.places;

export function placeOf(id: string): Place | undefined {
  return PLACES.find((p) => p.id === id);
}

/** The Help button's own event (help.tsx OPEN_HELP_EVENT) and the host message that opens Connections (bridge.ts). */
const OPEN_HELP_EVENT = 'semanticus:open-help';

export function moveScreenTo(note: StudioNote): void {
  const place = placeOf(note.place);
  if (!place) return;
  const focus = note.focus || undefined;
  if (place.tool === 'connections') {
    // The host's own message: bridge.ts hands it to the Connections hub, whose listener lives in App.
    try { window.dispatchEvent(new MessageEvent('message', { data: { type: 'openConnections' } })); } catch { /* no window */ }
    return;
  }
  if (place.tool === 'help') {
    try { window.dispatchEvent(new Event(OPEN_HELP_EVENT)); } catch { /* no window */ }
    return;
  }
  // Selecting an object moves the Model tree and Properties only; the Studio page stays where it is.
  if (place.target === 'focus') {
    if (focus) revealInTree(focus, true);
    return;
  }
  dispatchNavigate({ tab: place.tool, target: place.target ?? (place.tool === 'plan' ? undefined : focus), sessionId: note.sessionId || undefined });
  if (focus) revealInTree(focus, true);
}
