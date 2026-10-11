// Typed words whose object is not there any more (join 11, drafts fix round 2, W3).
//
// A calculation item or a start plan line is known by its NAME (its ref, or an id made from it), so when your assistant renames one, the object the
// words were typed for looks gone, and so would a deleted one. The page cannot tell the two apart, so it never drops the words on its own guess:
// it keeps them, says so, and asks. Keep mine moves the words to the object you pick (with the value they were typed over, written to nothing
// until you save), and Discard ends them. An object that has a stable id (an insight, a rule) is followed by that id and never comes here.
// The words move WITH the value they were typed over, never re-based onto the target's current value: if the assistant also changed what the target holds, the person never read that,
// so the row asks (Keep mine / Use the new one) and a save names the value they did read. A target that already holds words the person typed is not offered: a move never overwrites.
import { useState } from 'react';
import { draftModel, draftStore, useHeldDrafts } from './drafts';

export interface OrphanTarget { id: string; label: string }

const excerpt = (v: unknown): string => { const t = typeof v === 'string' ? v : JSON.stringify(v); return t.length > 90 ? t.slice(0, 90) + '...' : t; };

/** The held scopes of the open model under `prefix` whose id (the scope without the prefix) is not in `live` and not in `keep`. `live` null (a list that failed to read) means none. */
export function orphanScopes(prefix: string, live: Iterable<string> | null | undefined, keep: string[] = []) {
  const m = draftModel();
  if (!m || !live) return [];
  const ok = new Set<string>([...live, ...keep]);
  return draftStore.scopes(m, prefix).filter((e) => !ok.has(e.scope.slice(prefix.length)));
}

export function OrphanDrafts({ prefix, live, keep, what, nameOf, targetsFor, onMoved }: {
  prefix: string;
  /** The ids that ARE there, read fresh. null while not read (or a failed read): nothing is asked. */
  live: Iterable<string> | null | undefined;
  keep?: string[];
  /** What the thing is called ("calculation item", "plan line"). */
  what: string;
  /** What to call an object by its id when the draft carries no label. */
  nameOf: (id: string) => string;
  /** The objects the words of these fields could go on now. `id` is the object the words were typed for (the scope without the prefix), for a list that depends on what it was. */
  targetsFor: (fields: string[], id: string) => OrphanTarget[];
  /** Called after words moved, with the id of the object they now sit on: an editor already open there must read them (it holds its own copy of the box). */
  onMoved?: (to: string) => void;
}) {
  useHeldDrafts(prefix);   // re-render when a scope gains or loses a draft
  const [pick, setPick] = useState<Record<string, string>>({});
  const [refused, setRefused] = useState<Record<string, string>>({});
  const m = draftModel();
  const orphans = orphanScopes(prefix, live, keep);
  if (!m || orphans.length === 0) return null;
  return (
    <div data-testid="orphan-drafts" role="alert" className="sem-inset text-(length:--sem-text-label)" style={{ borderLeft: '3px solid var(--sem-warn)' }}>
      <div style={{ color: 'var(--sem-fg)' }}>
        {orphans.length === 1 ? `Words you typed for a ${what} that is not here now are kept.` : `Words you typed for ${orphans.length} ${what}s that are not here now are kept.`} It may have been renamed, or removed. Nothing was saved.
      </div>
      {orphans.map((o) => {
        const id = o.scope.slice(prefix.length), fields = o.fields.map((f) => f.field), targets = targetsFor(fields, id);
        // A target that already holds words the person typed for one of these fields cannot take these: moving would replace them.
        const taken = (t: OrphanTarget) => fields.some((f) => draftStore.get(m, prefix + t.id, f) !== null);
        return (
          <div key={o.scope} data-testid={`orphan-${o.scope}`} className="mt-2 flex flex-col gap-1">
            <div><b>{o.label || nameOf(id)}</b>: <code>{excerpt(o.fields[0]?.draft.value)}</code></div>
            <div className="flex flex-wrap items-center gap-2">
              <select className="sem-field sem-field-sm" aria-label={`The ${what} that ${o.label || nameOf(id)} became`} value={pick[o.scope] ?? ''} data-testid="orphan-pick"
                onChange={(e) => { setPick((p) => ({ ...p, [o.scope]: e.target.value })); setRefused((r) => ({ ...r, [o.scope]: '' })); }}>
                <option value="">Choose the renamed {what}...</option>
                {targets.map((t) => <option key={t.id} value={t.id} disabled={taken(t)}>{t.label}{taken(t) ? ' (already has words you typed)' : ''}</option>)}
              </select>
              <button type="button" className="sem-btn sem-btn-sm" disabled={!pick[o.scope]} data-testid="orphan-keep"
                title="Put what you typed on the one you chose. Nothing is saved until you save it."
                onClick={() => {
                  const to = pick[o.scope]; if (!to) return;
                  const r = draftStore.moveScope(m, o.scope, prefix + to);
                  if (!r.moved) { const t = targets.find((x) => x.id === to); setRefused((x) => ({ ...x, [o.scope]: `Nothing moved: ${t?.label ?? 'that one'} already has words you typed. Your words are kept here.` })); return; }
                  onMoved?.(to);
                }}>Keep mine on it</button>
              <button type="button" className="sem-btn sem-btn-sm" data-testid="orphan-discard" title="Drop what you typed."
                onClick={() => draftStore.clearScope(m, o.scope)}>Discard</button>
            </div>
            {refused[o.scope] && <div role="status" data-testid="orphan-refused" style={{ color: 'var(--sem-muted)' }}>{refused[o.scope]}</div>}
          </div>
        );
      })}
    </div>
  );
}
