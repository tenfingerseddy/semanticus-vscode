// Validation A (1.6): the small pieces the page is built from. A menu that closes on Escape and on a press outside, the status
// mark, and the four icons. No data and no engine calls here; tests.tsx holds those.
import { useEffect, useRef, useState, type ReactNode } from 'react';

const svg = (d: string) => <svg viewBox="0 0 16 16" width="16" height="16" aria-hidden="true"><path d={d} fill="none" stroke="currentColor" strokeWidth="1.4" strokeLinecap="round" strokeLinejoin="round" /></svg>;
export const Icon = {
  more: <svg viewBox="0 0 16 16" width="16" height="16" aria-hidden="true"><circle cx="3.5" cy="8" r="1.2" fill="currentColor" /><circle cx="8" cy="8" r="1.2" fill="currentColor" /><circle cx="12.5" cy="8" r="1.2" fill="currentColor" /></svg>,
  down: svg('M4 6.5l4 4 4-4'),
  right: svg('M6.5 4l4 4-4 4'),
  close: svg('M4 4l8 8M12 4l-8 8'),
};

export type Mood = 'pass' | 'differs' | 'accepted' | 'cannot' | 'off';
export const moodOf = (chip: string): Mood => (chip === 'Pass' ? 'pass' : chip === 'Differs' ? 'differs' : chip === 'Accepted' ? 'accepted' : chip === 'Off' || chip === 'Not run' ? 'off' : 'cannot');

export function Mark({ mood, word }: { mood: Mood; word: string }) {
  return <span className={`va-mark va-m-${mood}`} role="img" aria-label={word} />;
}

/** A button that opens a small list. Escape closes it and returns focus; a press outside closes it. */
export function Menu({ label, trigger, align = 'right', testid, className = '', children }: {
  label: string; trigger: ReactNode; align?: 'left' | 'right'; testid?: string; className?: string; children: (close: () => void) => ReactNode;
}) {
  const [open, setOpen] = useState(false);
  const wrap = useRef<HTMLSpanElement>(null);
  const button = useRef<HTMLButtonElement>(null);
  useEffect(() => {
    if (!open) return;
    const outside = (event: MouseEvent) => { if (!wrap.current?.contains(event.target as Node)) setOpen(false); };
    const escape = (event: KeyboardEvent) => { if (event.key !== 'Escape') return; setOpen(false); button.current?.focus(); };
    window.addEventListener('mousedown', outside); window.addEventListener('keydown', escape);
    return () => { window.removeEventListener('mousedown', outside); window.removeEventListener('keydown', escape); };
  }, [open]);
  return <span ref={wrap} className="va-rel">
    <button ref={button} type="button" className={`sem-btn ${className}`} aria-label={label} aria-haspopup="menu" aria-expanded={open} data-testid={testid} onClick={() => setOpen(!open)}>{trigger}</button>
    {open && <div role="menu" aria-label={label} className={`va-pop ${align === 'right' ? 'va-pop-right' : ''}`}>{children(() => setOpen(false))}</div>}
  </span>;
}

export function Item({ children, onClick, disabled, danger, title }: { children: ReactNode; onClick: () => void; disabled?: boolean; danger?: boolean; title?: string }) {
  return <button type="button" role="menuitem" className={`va-item ${danger ? 'va-bad' : ''}`} disabled={disabled} title={title} onClick={onClick}>{children}</button>;
}
