// What I did (1.4, contract section 5): every note this session, newest last. Pressing a line shows that step again.
// It opens from the strip, and by itself when the queue empties after three or more notes, until the person closes it.
// It opens as ONE calm line, the newest step, and Show all opens the list (copy21: the open list took 270 to 350 px above the Plan and Publish).
// The list is kept by the engine (getStudioNotes, 100 notes) and read on mount, so a reload keeps it; see notes-runtime.ts.
import { useEffect, useRef, useState } from 'react';
import type { StudioNote } from './types';
import './studio-notes.css';

/** "09:41", the person's own clock. A time the engine wrote badly reads as nothing rather than "Invalid Date". */
export function timeOf(at: string): string {
  const t = Date.parse(at);
  return Number.isFinite(t) ? new Date(t).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' }) : '';
}

export function WhatIDid({ log, unread, onShow, onClose }: {
  log: readonly StudioNote[]; unread: ReadonlySet<string>; onShow: (id: string) => void; onClose: () => void;
}) {
  const list = useRef<HTMLOListElement>(null);
  const [all, setAll] = useState(false);
  // Newest last: open scrolled to the end, where the latest step is.
  useEffect(() => { const el = list.current; if (el) el.scrollTop = el.scrollHeight; }, [log.length, all]);
  const latest = log[log.length - 1];
  return (
    <section className="sn-list" data-testid="what-i-did" data-all={all ? '' : undefined} aria-label="What I did">
      <div className="sn-list-head">
        <h2 className="sn-list-title">What I did</h2>
        {!all && latest && (
          <button type="button" className="sn-line sn-line-one" data-testid="what-i-did-latest" data-note={latest.id} onClick={() => onShow(latest.id)}>
            <span className="sn-line-time">{timeOf(latest.at)}</span>
            <span className="sn-line-say sn-line-clip">{latest.say}</span>
            {unread.has(latest.id) ? <span className="sn-dot" role="img" aria-label="Not shown yet" /> : <span />}
          </button>
        )}
        <span className="sn-list-actions">
          {log.length > 1 && (
            <button type="button" className="sem-btn sem-btn-sm sem-btn-quiet" data-testid="what-i-did-all" aria-expanded={all} onClick={() => setAll(!all)}>
              {all ? 'Show less' : `Show all ${log.length}`}
            </button>
          )}
          <button type="button" className="sem-btn sem-btn-sm sem-btn-quiet" data-testid="what-i-did-close" onClick={onClose}>Close</button>
        </span>
      </div>
      {log.length === 0
        ? <p className="sn-how" data-testid="what-i-did-empty">Nothing yet. What your assistant shows you will be listed here.</p>
        : all && (
          <ol className="sn-lines" ref={list}>
            {log.map((note) => (
              <li key={note.id}>
                <button type="button" className="sn-line" data-testid="what-i-did-line" data-note={note.id} onClick={() => onShow(note.id)}
                  title={note.how && note.by !== 'person' ? `To do this yourself: ${note.how}` : undefined}>
                  <span className="sn-line-time">{timeOf(note.at)}</span>
                  <span className="sn-line-say">{note.say}</span>
                  {unread.has(note.id) ? <span className="sn-dot" role="img" aria-label="Not shown yet" data-testid="what-i-did-unread" /> : <span />}
                </button>
              </li>
            ))}
          </ol>
        )}
    </section>
  );
}
