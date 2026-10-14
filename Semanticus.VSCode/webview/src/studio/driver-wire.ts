import { postToHost, rpc } from '../bridge';
import { driverStore, type DriveMode, type DriverState } from './driver-store';

type DriverMessage = { type: 'setDriveMode'; mode: DriveMode }
  | { type: 'driverState'; mode: DriveMode; state: DriverState; lastPersonInput: number | null };

// The bridge stamps every message with the page identity; its typed post has no driver messages, so this narrows the call.
export const postDriverMessage = postToHost as unknown as (message: DriverMessage) => void;

// The host posts 'personInput' when the person used the tree or closed the panel; bridge.ts has no listener for it.
export function onPersonInput(fn: (at: number) => void): () => void {
  const handler = (event: MessageEvent) => { if (event.data?.type === 'personInput') fn(event.data.at ?? Date.now()); };
  window.addEventListener('message', handler);
  return () => window.removeEventListener('message', handler);
}

/** The person's choice of who moves the screen: the Studio's own store first, then the host. One function for the driver bar and the Plan's header.
 *  I drive also pauses the assistant's Plan tasks, and handing the wheel back turns them on (1.5: one switch, no second one for the queue). The Plan's header
 *  passes `queue` false because it sets the queue itself and takes the whole plan back in the same call. */
export function setDriveMode(next: DriveMode, queue = true): void {
  driverStore.setMode(next);
  postDriverMessage({ type: 'setDriveMode', mode: next });
  if (queue) void rpc('setAssistantQueue', next === 'iDrive' ? 'paused' : 'on').catch(() => { /* no engine yet: the mode is still set */ });
}
