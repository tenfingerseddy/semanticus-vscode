import { useSyncExternalStore } from 'react';
import { onDidChange } from '../../bridge';

let session: string | undefined;
let sequence = 0;
const made = new Map<string, { key: number; at: number }>();
const listeners = new Set<() => void>();
onDidChange(change => {
  if (change.sessionId !== session) { session = change.sessionId; made.clear(); }
  let changed = false;
  for (const delta of change.deltas ?? []) {
    if (delta.kind === 'add' && delta.ref.startsWith('relationship:')) {
      made.set(delta.ref.slice('relationship:'.length), { key: ++sequence, at: performance.now() });
      changed = true;
    }
  }
  while (made.size > 32) made.delete(made.keys().next().value!);
  if (changed) listeners.forEach(listener => listener());
});
const subscribe = (listener: () => void) => { listeners.add(listener); return () => { listeners.delete(listener); }; };

/** Existing edges do not animate on open, pan, zoom, route changes or a table drag. */
export function useRelationshipMotion(id: string) {
  return useSyncExternalStore(subscribe, () => {
    const event = made.get(id);
    return event && performance.now() - event.at < 1800 ? event.key : 0;
  });
}
