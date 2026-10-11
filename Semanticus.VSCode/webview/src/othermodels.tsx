// Words kept for other models (join 11, drafts round 3): the safety net under the key.
//
// Typed, unsaved words are filed under the model they were typed on (the engine's ModelIdentity). When that identity ever changes in a way nobody foresaw (a Desktop
// model that restarts with no window title to name it, a file moved), the words are not lost: they stay in the store, and this one quiet line says so. It lists every
// held draft whose model is not the open one, by the model's name, with Copy (the words go to the clipboard) and Discard (the words are dropped). It NEVER attaches
// the words to an editor and NEVER writes anything to a model or the engine, because the page cannot know these words still belong on the open model.
import { useState } from 'react';
import { copyText } from './bridge';
import { draftStore, RESTORED_PREFIX, useOtherModels, wordsAsText } from './drafts';

export function OtherModelWords() {
  const models = useOtherModels();
  const [open, setOpen] = useState(false);
  const [said, setSaid] = useState<Record<string, string>>({});
  if (models.length === 0) return null;
  const words = (n: number) => (n === 1 ? 'one piece of words' : `${n} pieces of words`);
  return (
    <div data-testid="other-model-words" role="status" className="sem-inset text-(length:--sem-text-label)" style={{ color: 'var(--sem-muted)', padding: 'var(--sem-space-2) var(--sem-space-6)' }}>
      <button type="button" className="sem-btn sem-btn-sm sem-btn-quiet" aria-expanded={open} data-testid="other-model-words-toggle" onClick={() => setOpen((o) => !o)}>
        {models.length === 1 ? 'Words kept for 1 other model' : `Words kept for ${models.length} other models`}
      </button>
      {open && models.map((m) => (
        <div key={m.modelKey} data-testid="other-model-row" className="mt-1 flex flex-wrap items-center gap-2">
          <span style={{ color: 'var(--sem-fg)' }}><b>{m.label}</b>, {words(m.drafts.length)}. {m.modelKey.startsWith(RESTORED_PREFIX) ? 'They are kept here to read, and nothing here is saved.' : 'They are not on the open model, and nothing here is saved.'}</span>
          <button type="button" className="sem-btn sem-btn-sm" data-testid="other-model-copy" title="Copy these words so you can paste them where they belong."
            onClick={() => { void copyText(wordsAsText(m.drafts)).then((ok) => setSaid((s) => ({ ...s, [m.modelKey]: ok ? 'Copied.' : 'Could not copy.' }))); }}>Copy</button>
          <button type="button" className="sem-btn sem-btn-sm" data-testid="other-model-discard" title="Drop these words for good."
            onClick={() => draftStore.clearModel(m.modelKey)}>Discard</button>
          {said[m.modelKey] && <span role="status">{said[m.modelKey]}</span>}
        </div>
      ))}
    </div>
  );
}
