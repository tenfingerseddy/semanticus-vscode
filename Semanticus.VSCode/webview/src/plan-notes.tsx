import { useEffect, useRef, useState } from 'react';
import { usePlanChrome } from './plan-chrome';

/** "2 notes" in the bottom bar (1.5, pick C): the Plan page's notes were lines under its header; they are one quiet word here and open on a press.
 *  Nothing is drawn when the page has no notes, or when the Plan is not the page on screen. */
export function PlanNotes() {
  const { notes } = usePlanChrome();
  const [open, setOpen] = useState(false);
  const host = useRef<HTMLSpanElement>(null);
  useEffect(() => {
    if (!open) return undefined;
    const away = (e: MouseEvent) => { if (!host.current?.contains(e.target as Node)) setOpen(false); };
    const key = (e: KeyboardEvent) => { if (e.key === 'Escape') setOpen(false); };
    document.addEventListener('mousedown', away); document.addEventListener('keydown', key);
    return () => { document.removeEventListener('mousedown', away); document.removeEventListener('keydown', key); };
  }, [open]);
  useEffect(() => { if (notes.length === 0) setOpen(false); }, [notes.length]);
  if (notes.length === 0) return null;
  return <span className="pc-notes" ref={host}>
    <button type="button" className="pc-notes-btn" data-testid="plan-notes" aria-expanded={open} onClick={() => setOpen((o) => !o)}>
      {notes.length} {notes.length === 1 ? 'note' : 'notes'}</button>
    {open && <div className="pc-notes-pop" role="dialog" aria-label="Notes about this plan" data-testid="plan-notes-list">
      {notes.map((n) => <p key={n.text}>{n.text}{n.link && <> <button type="button" className="plan-link" onClick={n.link.run}>{n.link.label}</button></>}</p>)}
    </div>}
  </span>;
}
