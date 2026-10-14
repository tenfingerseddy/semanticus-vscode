// While the assistant waits on the person about a red check, the Publish page says so (hero19 D7).
// The engine sends one activity event (kind publish_asking) with the line, and a second one that clears it. The line is kept here,
// outside any page, so it is still there when the person opens Publish after the assistant asked.
import { useEffect, useState } from 'react';
import { onActivity } from './bridge';
import { foldPublishAsk } from './publish-progress';

let line: string | null = null;
const listeners = new Set<(v: string | null) => void>();
const set = (v: string | null) => { if (v === line) return; line = v; listeners.forEach((l) => l(v)); };
onActivity((e) => set(foldPublishAsk(line, e)));

/** The person published it themselves: nothing waits any more. */
export function clearPublishAsk(): void { set(null); }

export function usePublishAsk(): string | null {
  const [v, setV] = useState<string | null>(line);
  useEffect(() => { listeners.add(setV); setV(line); return () => { listeners.delete(setV); }; }, []);
  return v;
}
