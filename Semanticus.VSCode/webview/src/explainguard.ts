// Explain this number asks the live model. A card that renders again must not ask again (hero 24 D1: a new payload object on every render
// sent about 3,500 queries in 13 minutes). The request is its measure and its context, compared by value; the same request while it runs joins
// the one running, and the same request again within a few seconds gets that answer. The engine holds the same rule for every door.

export interface ExplainRequestLike {
  measureName: string;
  groupBy?: string[] | null;
  filters?: { column: string; members: string[] }[] | null;
  extraPredicates?: string[] | null;
  /** The column the person chose to split the number by; absent when the engine picks it. */
  splitBy?: string | null;
}

/** What the engine is asked, and nothing it is not asked (the shown value, the format and the labels do not change the query). */
export function explainKey(p: ExplainRequestLike, model: { id?: string | null; revision?: number | null } = {}): string {
  // The model and its revision are part of the request: asked after an edit, the card describes the edited measure.
  return JSON.stringify([model.id ?? '', model.revision ?? 0, p.measureName, p.groupBy ?? [], (p.filters ?? []).map((f) => [f.column, f.members]), p.extraPredicates ?? [], p.splitBy ?? '']);
}

/** The columns offered as the split: the engine's pick first, then the others that also explain most of the number. They come with the engine's own
 *  pick; an answer to a column the person chose does not carry them, so the row stays as it was while they switch. */
export function splitOptions(kept: string[], c: { dimension?: string; additive: boolean; alternatives?: string[] | null } | null | undefined): string[] {
  if (c && c.additive && c.dimension && c.alternatives && c.alternatives.length > 0) return [c.dimension, ...c.alternatives];
  return kept;
}

export function createExplainGuard(windowMs = 3000, now: () => number = () => Date.now()) {
  const runs = new Map<string, { promise: Promise<unknown>; doneAt: number | null }>();
  return function once<T>(key: string, work: () => Promise<T>): Promise<T> {
    const known = runs.get(key);
    if (known && (known.doneAt === null || now() - known.doneAt <= windowMs)) return known.promise as Promise<T>;
    const entry: { promise: Promise<unknown>; doneAt: number | null } = { promise: Promise.resolve(), doneAt: null };
    entry.promise = work().then(
      (r) => {
        // An answer that says it failed is shown, then forgotten: pressing the same number again asks again.
        if ((r as { status?: string } | null)?.status && (r as { status?: string }).status !== 'ok') { if (runs.get(key) === entry) runs.delete(key); }
        else entry.doneAt = now();
        return r;
      },
      (e) => { if (runs.get(key) === entry) runs.delete(key); throw e; },   // a failure is never remembered: the next press tries again
    );
    runs.set(key, entry);
    return entry.promise as Promise<T>;
  };
}

/** The one guard of this page. */
export const explainOnce = createExplainGuard();
