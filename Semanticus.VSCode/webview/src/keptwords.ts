import { useEffect, useRef, useState } from 'react';
import { draftModel, draftStore, heldDraft } from './drafts';

/** Two texts are the same when they are the same after trimming. The engine trims a note, an answer and a name, so a trailing space the person typed is not a different value (join 11 solui, round 3). */
export const sameTrimmed = (a: string, b: string): boolean => a.trim() === b.trim();

export interface KeptTextOptions {
  /** What "the same text" means for this box. The default is exact (===). A box whose engine trims what it stores passes `sameTrimmed`. */
  same?: (a: string, b: string) => boolean;
  /** Hold the typed words in the draft store under this scope and field, so they outlive the box (a remount, a renamed key, a swapped tab). The box opens on held words, and the hold ends when the box is the live value again. */
  hold?: { scope: string; field?: string; label?: string };
}

/// A text box that follows a live value until the person types in it (join 11 solui, U2, rule R-B: a draft ends only when the engine accepted exactly that text or the person
/// discarded it). Typed words are never replaced when the live value moves: `moved` then holds the newer value so the page can show it. The box adopts the live value again when
/// nothing was typed, when the live value becomes exactly these words (the engine took them), or when `resetKey` changes (another row or question: not this draft).
/// Round 3: `same` says what "exactly" means (trimmed for a field the engine trims), and `hold` mirrors the typed words into the draft store, so a remount cannot lose them.
export function useKeptText(live: string, resetKey?: string, options: KeptTextOptions = {}): { text: string; setText: (t: string) => void; moved: string | null; useLive: () => void } {
  const same = options.same ?? ((a: string, b: string) => a === b);
  const hold = options.hold;
  const holdId = hold ? JSON.stringify([hold.scope, hold.field ?? '']) : '';
  // What the store holds for this box now: the words and the live value they were typed over.
  const held = (): { value: string; base: string } | undefined => {
    const h = hold ? heldDraft<string, string>(hold.scope, hold.field ?? '') : undefined;
    return h && typeof h.value === 'string' ? { value: h.value, base: typeof h.base === 'string' ? h.base : '' } : undefined;
  };
  const [first] = useState(held);
  const [text, setText] = useState(first ? first.value : live);
  const base = useRef(first ? first.base : live);   // the live value the words were typed against
  const key = useRef(resetKey);
  const id = useRef(holdId);
  const moving = useRef(false);   // the effect below changed the words this pass, so the one after it must not hold the old ones
  useEffect(() => {
    if (key.current !== resetKey || id.current !== holdId) {
      key.current = resetKey; id.current = holdId; moving.current = true;
      const h = held();
      base.current = h ? h.base : live; setText(h ? h.value : live);
      return;
    }
    if (same(text, base.current) || same(text, live)) { if (text !== live) moving.current = true; base.current = live; setText(live); }
  }, [live, resetKey, holdId]);   // eslint-disable-line react-hooks/exhaustive-deps
  // Mirror the typed words into the store; end the hold when the box is the live value again. Words held at mount are written back as they are.
  useEffect(() => {
    if (!hold) return;
    if (moving.current) { moving.current = false; return; }
    const m = draftModel();
    if (!m) return;
    if (same(text, live) || same(text, base.current)) { draftStore.clear(m, hold.scope, hold.field ?? ''); return; }
    draftStore.set(m, hold.scope, hold.field ?? '', { value: text, base: base.current, ...(hold.label ? { label: hold.label } : {}) });
  }, [text, live, resetKey, holdId]);   // eslint-disable-line react-hooks/exhaustive-deps
  const moved = !same(text, base.current) && !same(text, live) && !same(live, base.current) ? live : null;
  return { text, setText, moved, useLive: () => { base.current = live; setText(live); } };
}
