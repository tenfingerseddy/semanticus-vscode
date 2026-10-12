import { postToHost } from '../bridge';
import type { DriveMode, DriverState } from './driver-store';

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
