import { useEffect, useState } from 'react';

// ── Can we reach the publish destination? ──────────────────────────────────────────────────────────────
// A publish needs a DESTINATION, not a query connection, so the header keeps naming the destination even with
// no live model attached. But it used to offer an enabled Publish and a full-looking comparison while nothing
// had actually been read from that destination (M17, walkthrough 2026-09-14). The Published page's own
// comparison is the one thing that really talks to the destination, so it publishes what it found here and the
// header's single Publish button reads it. Unknown (null) means we have not tried: Publish stays enabled.
// A flag, not a name. The Published page resolves its destination label from the connection registry, which
// loads a beat after the first comparison can fail, so carrying a name here printed the half-resolved one
// ('Contoso') in a header that says 'Contoso Sales'. The header names the destination; this only says reachable.
//
// The refusal belongs to the model session and the destination it was read for. The header is on every page and
// this store outlives Studio's remount on a new model, while only the Published page cleared it, so a model swapped
// in on another page kept the old model's refusal: Publish greyed out over a model nobody had tried (verifier r2, O5).
export type PublishReach = { unreachable: true } | null;
/** A refusal, and the model session and destination it was read for. */
export type PublishRefusal = { sessionId: string; connectionId: string | null } | null;

let refusal: PublishRefusal = null;
const listeners = new Set<(value: PublishRefusal) => void>();

export function setPublishReach(next: PublishRefusal): void {
  if (refusal?.sessionId === next?.sessionId && refusal?.connectionId === next?.connectionId) return;
  refusal = next;
  listeners.forEach((listener) => { try { listener(next); } catch { /* isolate a view */ } });
}

/** Whether `stored` is a refusal of this model and this destination. Exported for test/publish-surface.test.mjs. */
export function reachFor(stored: PublishRefusal, sessionId: string | null | undefined, connectionId: string | null | undefined): PublishReach {
  if (!stored || !sessionId) return null;
  return stored.sessionId === sessionId && stored.connectionId === (connectionId ?? null) ? { unreachable: true } : null;
}

export function usePublishReach(sessionId: string | null | undefined, connectionId: string | null | undefined): PublishReach {
  const [value, setValue] = useState(refusal);
  useEffect(() => { listeners.add(setValue); setValue(refusal); return () => { listeners.delete(setValue); }; }, []);
  return reachFor(value, sessionId, connectionId);
}
