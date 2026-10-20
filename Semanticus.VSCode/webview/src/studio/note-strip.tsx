// The note strip (1.4, contract section 5). It replaces the stub lane driver-bar ships: the same `NoteStrip` export, mounted
// by layer.tsx under the driver bar. It shows the assistant's sentence, then "To do this yourself: ..." from the place map,
// and carries Show me, Skip to now, the waiting count and What I did. Presses are the cursor's (cursor.tsx); this file
// only reads and drives the queue (notes-store.ts).
import { notesStore, useNotes, useNotesWire } from './notes-runtime';
import { WhatIDidNow } from './what-i-did';
import { Suggest } from './suggest';
import './studio-notes.css';
import { upgradeToPro } from '../pro';
import { MessageLine } from '../personality/message-line';

export function NoteStrip() {
  useNotesWire();
  const notes = useNotes();
  const { current, phase, more, waiting, log, unread, listOpen, suggestion } = notes;
  const showLog = (id: string) => notesStore.showMe(id);
  return (
    <div className="sn" data-studio-note-strip data-testid="note-strip">
      <MessageLine blocked={!!current || !!suggestion || listOpen} />
      {current ? (
        <div className="sn-note" data-phase={phase ?? undefined} data-by={current.by === 'person' ? 'person' : undefined} role="status" aria-live="polite">
          <div>
            <p className="sn-say" data-testid="note-say">{current.say}</p>
            {current.how && current.by !== 'person' ? <p className="sn-how" data-testid="note-how">To do this yourself: {current.how}</p> : null}
          </div>
          <div className="sn-actions">
            {current.upgrade && <button type="button" className="sem-btn sem-btn-sm sem-btn-quiet" data-testid="note-upgrade" onClick={upgradeToPro}>Unlock Pro</button>}
            {phase === 'waiting' && (
              <button type="button" className="sem-btn sem-btn-sm" data-testid="note-show-me" onClick={() => notesStore.showMe(current.id)}>Show me</button>
            )}
            {current.folded ? <span className="sn-more" data-testid="note-folded">{current.folded} more {current.folded === 1 ? 'step' : 'steps'} in What I did</span> : null}
            {more > 0 && <span className="sn-more" data-testid="note-more">{more} more</span>}
            {waiting >= 2 && (
              <button type="button" className="sem-btn sem-btn-sm sem-btn-quiet" data-testid="note-skip" onClick={() => notesStore.skipToNow()}>Skip to now</button>
            )}
            {log.length > 0 && !listOpen && (
              <button type="button" className="sem-btn sem-btn-sm sem-btn-quiet" data-testid="note-what-i-did" onClick={() => notesStore.setListOpen(true)}>What I did</button>
            )}
            {phase === 'done' && more === 0 && (
              <button type="button" className="sem-btn sem-btn-sm sem-btn-quiet" data-testid="note-close" onClick={() => notesStore.dismiss()}>Close</button>
            )}
          </div>
        </div>
      ) : log.length > 0 && !listOpen ? (
        <div className="sn-bare">
          <button type="button" className="sem-btn sem-btn-sm sem-btn-quiet" data-testid="note-what-i-did" onClick={() => notesStore.setListOpen(true)}>What I did</button>
        </div>
      ) : null}
      {listOpen && <WhatIDidNow log={log} unread={unread} onShow={showLog} onClose={() => notesStore.setListOpen(false)} />}
      {suggestion && <Suggest view={suggestion} />}
    </div>
  );
}
