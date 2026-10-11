import { useEffect, useRef, useState } from 'react';
import { draftModel, draftStore, heldDraft, stable, stillOnModel } from './drafts';

// An editor that saves a whole thing must know what it started from.
//
// UAT 2 Oct (UAT-F-S06a-1-002 and UAT-F-S06b-2-007, class "an editor that saves a stale copy"): the Notes insight editor
// opened with the old text after the assistant changed it, and the source-mapping drawer kept the old mapping; Save wrote
// the old copy back over the assistant's newer change. Nothing was ever shown to the person.
//
// The rule, for every editor that saves a whole object from a copy taken at open:
//   - the copy is taken from what is live NOW, when the editor opens;
//   - if the live thing moves while the editor is open and the person has typed nothing, the editor follows it;
//   - if the person has typed something, their words stay and a banner offers "Keep mine" or "Use the new one";
//   - Save reads the live thing again first, and a Save whose starting point is older than the live thing writes
//     nothing until the person chose (the same shape as the composer draft's Keep yours / Apply banner);
//   - and the write itself carries what the Save confirmed (confirmed()), so the ENGINE compares it with the live thing inside its
//     own mutation: a read at Save can still be a moment behind, and a copy the page holds is never the check (Sol join 6, C4).
//
// And the typed words outlive the editor (join 11, the drafts class). An editor that passes `persist` has its draft held in the shared draft
// store (drafts.ts), by the model's FILE, the editor's scope and the field, together with the value it was typed over. A tab switch, another row,
// a session remount (a restarted engine) and a reload of the page then keep it: the editor opens on the held words and starts from the held
// starting point, so a value that moved meanwhile is asked about (Keep mine / Use the new one) exactly as it is live. The draft ends on a save the
// engine took (the live value becomes the draft), when the editor is closed with Cancel (`editing` goes false while it is mounted), and on Use the new one.
// It does NOT end when the editor unmounts. An editor reads what is held with `heldDraft()` for its first render.

export { stable };
export const sameValue = (a: unknown, b: unknown) => stable(a) === stable(b);

/** True for the engine's refusal of a save whose starting point is no longer the live value (LocalEngine.GuardExpectedValue's words). */
export const movedError = (e: unknown): boolean => /changed in the model after you opened it/.test(String((e as { message?: string })?.message ?? e));

/** What an open editor does when the live thing is read again: nothing, follow it, or ask. */
export type Reconciled = 'same' | 'follow' | 'ask';
export function reconcile(base: unknown, now: unknown, dirty: boolean): Reconciled {
  if (sameValue(base, now)) return 'same';
  return dirty ? 'ask' : 'follow';
}

export interface EditBaseOptions<T> {
  /** The live thing, as the page last read it. `undefined` means not read yet; `null` means it is not there. */
  current: T | null | undefined;
  /** Which thing this editor is on. A different scope is a different thing, not a change to this one: the draft restarts from it. */
  scope?: string;
  /** What the person has in the editor now, in the same shape as `current`. It is "typed something" when it differs from where the draft started. */
  draft: T | null | undefined;
  editing: boolean;
  /** Put a live value into the draft fields. */
  reseed: (now: T) => void;
  /** A fresh read, taken at Save. Without it Save compares against `current`. */
  readNow?: () => Promise<T | null | undefined>;
  /** Hold the draft above the editor (see the top of this file). `scope` is the editor plus the object it is on; `field` names which text. */
  persist?: { scope: string; field?: string };
}

export function useEditBase<T>(o: EditBaseOptions<T>) {
  // What the store held for this editor when it mounted: the words it opened on and the value they were typed over.
  // The model this editor was opened on. A draft belongs to THAT model: it is held, and ended, under it, never under whatever model is open when an effect or a late save runs (C2).
  const modelAtMount = useRef(draftModel()).current;
  // The key it is held under: the model the page shows while that is still this editor's model (an unsaved model that got its first Save is the same one under a new key), else the model it opened on.
  const holdKey = () => (stillOnModel(modelAtMount) ? draftModel() : modelAtMount);
  const restored = useRef<{ value: T; base: T } | undefined | null>(null);
  if (restored.current === null) restored.current = o.persist ? heldDraft<T>(o.persist.scope, o.persist.field ?? '') : undefined;
  const awaitingRead = useRef(!!restored.current);   // restored from the store and the live value not read yet: the held starting point stands until it is
  const [base, setBase] = useState<T | null | undefined>(restored.current ? restored.current.base : o.current);
  const [theirs, setTheirs] = useState<{ v: T | null | undefined } | null>(null);
  const live = useRef(o); live.current = o;
  const baseRef = useRef(base); baseRef.current = base;
  const was = useRef(o.editing);
  const scopeRef = useRef(o.scope);
  const confirmedRef = useRef<T | null | undefined>(undefined);   // the live value the last passing beforeSave() saw: what the write names as its starting point

  const typed = () => !sameValue(live.current.draft, baseRef.current);
  const sig = stable(o.current);   // the live thing by value: a caller may build `current` fresh on every render

  useEffect(() => {
    const { current, editing, reseed } = live.current;
    if (!editing) { was.current = false; scopeRef.current = live.current.scope; setBase(current); setTheirs(null); return; }
    // Just opened: the draft was seeded from this. The scope it opened on is the one to compare with later; without it the first
    // live change reads as "another thing altogether" and replaces what the person typed.
    if (!was.current) { was.current = true; scopeRef.current = live.current.scope; setBase(current); setTheirs(null); return; }
    if (current === undefined) { if (!awaitingRead.current) setBase(undefined); return; }   // not read yet: nothing to compare with (a held draft keeps its starting point)
    awaitingRead.current = false;
    if (baseRef.current === undefined || scopeRef.current !== live.current.scope) {   // first read, or another thing altogether: start from it
      scopeRef.current = live.current.scope; setTheirs(null);
      // ...unless the person left words on that thing: they open again, over the value they were typed over (asked about below if it moved).
      const p = live.current.persist, h = p ? heldDraft<T>(p.scope, p.field ?? '') : undefined;
      if (h && !sameValue(h.value, h.base)) {
        reseed(h.value); setBase(h.base);
        if (!sameValue(h.base, current)) setTheirs({ v: current });   // the value moved while those words were away: ask now, as a mount does
        return;
      }
      reseed(current as T); setBase(current); return;
    }
    // What is live now is what the person has typed (their own save landed, or the other door wrote the same words): no conflict.
    if (sameValue(current, live.current.draft)) { setBase(current); setTheirs(null); return; }
    const what = reconcile(baseRef.current, current, typed());
    if (what === 'same') setTheirs(null);
    else if (what === 'follow') { reseed(current as T); setBase(current); setTheirs(null); }
    else setTheirs({ v: current });
  }, [sig, o.editing, o.scope]);

  // Hold what is typed. A draft equal to where it started is nothing to hold; closing the editor while it is mounted ends the draft.
  const pid = o.persist ? JSON.stringify([o.persist.scope, o.persist.field ?? '']) : '';
  const draftSig = stable(o.draft);
  const lastPid = useRef(pid);
  useEffect(() => {
    const p = live.current.persist, m = holdKey();
    // The editor moved to another object (the scope changed): its fields still hold the OLD object's words until the reconcile above reseeds them, and those are not this object's draft.
    if (lastPid.current !== pid) { lastPid.current = pid; return; }
    if (!p || !m) return;
    const field = p.field ?? '';
    if (!live.current.editing) { draftStore.clear(m, p.scope, field); return; }
    if (base === undefined || live.current.draft === undefined) return;
    draftStore.set(m, p.scope, field, { value: live.current.draft, base });
  }, [pid, draftSig, stable(base), o.editing]);

  /** Run before the write. False means do not write: the live thing moved (or the person must choose first). */
  const beforeSave = async (): Promise<boolean> => {
    const startedOn = draftModel();   // the model this Save was pressed on
    const now = live.current.readNow ? await live.current.readNow() : live.current.current;
    // R-A: the page moved to another model while the read was on its way (the assistant opened one, or the person did). The words were typed for the first model, so the write is not made
    // (a write right after this check carries the page's CURRENT model, and the engine would take it as the new model's own). The words stay held under the model they belong to.
    if (!stillOnModel(startedOn)) return false;
    if (sameValue(now, live.current.draft)) { setBase(now); confirmedRef.current = now; return true; }
    const what = reconcile(baseRef.current, now, typed());
    if (what === 'same') { confirmedRef.current = now; return true; }
    if (what === 'follow') { live.current.reseed(now as T); setBase(now); setTheirs(null); return false; }
    setTheirs({ v: now });
    return false;
  };
  const keepMine = () => { if (theirs) setBase(theirs.v); setTheirs(null); };
  const useNew = () => { if (theirs) { live.current.reseed(theirs.v as T); setBase(theirs.v); } setTheirs(null); };
  /** The live value the last passing beforeSave() read. Pass it to the write as the value it expects to replace. */
  const confirmed = () => confirmedRef.current;
  /** The engine refused the write because the live value moved after beforeSave() read it: show the banner now (a fresh read), and say it was handled. */
  const refused = async (e: unknown): Promise<boolean> => {
    if (!movedError(e)) return false;
    await beforeSave();
    return true;
  };
  /** After a save the editor stays open on what it just wrote. */
  const rebase = (v: T | null | undefined) => { setBase(v); setTheirs(null); };
  /** The engine took `submitted`. The draft ends ONLY when the words the editor holds are still exactly those (R-B: the person may have kept typing while the write was on its way); the
   *  draft is ended under the model it belongs to, whatever model is open now. Newer words stay held over the value just saved. Returns true when the editor may close. */
  const saved = (submitted: T | null | undefined, nowLive: T | null | undefined = submitted): boolean => {
    const p = live.current.persist;
    if (p) for (const m of new Set([modelAtMount, holdKey()])) draftStore.clearIfSubmitted(m, p.scope, p.field ?? '', submitted);
    const same = sameValue(live.current.draft, submitted);
    setBase(nowLive); setTheirs(null);   // the saved value is where the draft starts from now (what the engine kept, when it is not quite what was sent)
    return same;
  };
  /** What the editor holds NOW (for a save that finishes after more was typed: the draft it carried is no longer the one on screen). */
  const draftNow = () => live.current.draft;
  /** Where the draft started, read at this moment (for a save that runs from an unmount, where no render follows). */
  const baseNow = () => baseRef.current;
  /** True while the editor holds words that differ from where they started: the thing a quiet "Draft not saved" mark says. */
  const drafted = !!o.persist && o.editing && base !== undefined && o.draft !== undefined && !sameValue(o.draft, base);
  // A newer value that came back as `undefined` is shown as "nothing there" (null), never as "not read yet": Keep mine stays disabled until the newer value is on screen, and an `undefined` that was
  // read would leave it disabled for good. Only the absence of a moved value at all is `undefined` here.
  return { moved: theirs !== null, theirs: theirs ? (theirs.v === undefined ? null : theirs.v) : undefined, beforeSave, confirmed, refused, keepMine, useNew, rebase, saved, baseNow, draftNow, drafted };
}

/** For a form with no live thing to fence (a NEW object's fields, a plain input): hold what is typed over the value it started from, while `active`.
 *  It never clears by itself: the form's own Cancel / a save the engine took call `draftStore.clear`. (An unmount, or another form opening, leaves the draft held.) */
export function useHoldDraft<T>(o: { persist?: { scope: string; field?: string }; draft: T | undefined; base: T | undefined; active: boolean }) {
  const draftSig = stable(o.draft), baseSig = stable(o.base);
  useEffect(() => {
    const m = draftModel();
    if (!o.persist || !m || !o.active || o.draft === undefined || o.base === undefined) return;
    draftStore.set(m, o.persist.scope, o.persist.field ?? '', { value: o.draft, base: o.base });
  }, [o.persist?.scope, o.persist?.field, o.active, draftSig, baseSig]);
  return !!o.persist && o.active && o.draft !== undefined && o.base !== undefined && !sameValue(o.draft, o.base);
}

/** The quiet mark an editor shows while it holds typed words that are not saved. They are kept if the person leaves. */
export function DraftMark({ show, className }: { show: boolean; className?: string }) {
  if (!show) return null;
  return <span className={'sem-draft-mark' + (className ? ' ' + className : '')} data-testid="draft-mark" role="status"
    title="You typed here and have not saved it. It is kept if you leave this page.">Draft not saved</span>;
}

/** The newer value as text a person can read beside their own words: text as it is, anything else as plain JSON, nothing as a sentence. */
export function movedText(v: unknown): string {
  if (v === null) return '(nothing is there now)';
  if (typeof v === 'string') return v === '' ? '(empty now)' : v;
  try { return JSON.stringify(v, null, 2); } catch { return String(v); }
}

/** The banner every editor shows while its starting point is older than the live thing. It SHOWS the newer value (`theirs`) beside the person's words: Keep mine is not pressable until that value is
 *  on screen, because Keep mine makes the next Save write over it, and a person cannot choose to replace what they have not read (F11, Sol UAT J10-B-001..004). `undefined` means it is not read yet. */
export function EditMovedBanner({ what, theirs, onKeepMine, onUseNew, disabled }: { what: string; theirs: unknown; onKeepMine: () => void; onUseNew: () => void; disabled?: boolean }) {
  const shown = theirs !== undefined;
  return (
    <div role="alert" className="sem-inset mt-2 text-(length:--sem-text-label)" style={{ borderLeft: '3px solid var(--sem-warn)' }}>
      <div style={{ color: 'var(--sem-fg)' }}>This {what} changed while you were editing it. Your words are still here, and nothing was saved yet.</div>
      <div className="mt-2" data-testid="moved-theirs">
        <b>The new {what}:</b>
        <pre className="sem-inset m-0 mt-1 whitespace-pre-wrap break-words" style={{ maxHeight: 160, overflow: 'auto', fontFamily: 'var(--sem-font-num)', fontSize: 'var(--sem-text-meta)' }}>{shown ? movedText(theirs) : 'Reading the new version...'}</pre>
      </div>
      <div className="mt-2 flex flex-wrap items-center gap-2">
        <button type="button" disabled={disabled || !shown} onClick={onKeepMine} className="sem-btn sem-btn-sm" data-testid="moved-keep-mine"
          title={shown ? 'Keep what you typed. The next Save writes it over the new version shown above.' : 'The new version is not on screen yet.'}>Keep mine</button>
        <button type="button" disabled={disabled} onClick={onUseNew} className="sem-btn sem-btn-sm"
          title="Drop what you typed and load the new version.">Use the new one</button>
      </div>
    </div>
  );
}
