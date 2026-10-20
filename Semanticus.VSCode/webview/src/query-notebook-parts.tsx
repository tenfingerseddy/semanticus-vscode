// The small, leaf parts of the Query notebook: the icons, the menu, and one row's line. No hooks and no host calls, so the row is drawn the same
// in the page and in the tests (test/query-notebook.test.mjs).
import { useEffect, useRef, type ReactNode } from 'react';
import { isDaxRow, markLabel, rowText, splitFigure, timeWords, type Rec } from './query-notebook-model';
import type { Notebook } from './query-notebook';

/** The row that holds a query nobody has run yet, or a question still being asked, or any answer on a model that keeps no history. */
export const DRAFT = '__draft';

// ---- icons (currentColor, 16 px box)
export const Icon = {
  clock: <svg viewBox="0 0 16 16" width="16" height="16" aria-hidden="true"><circle cx="8" cy="8" r="5.5" fill="none" stroke="currentColor" strokeWidth="1.3" /><path d="M8 5v3.2l2 1.3" fill="none" stroke="currentColor" strokeWidth="1.3" strokeLinecap="round" /></svg>,
  close: <svg viewBox="0 0 16 16" width="16" height="16" aria-hidden="true"><path d="M4.5 4.5l7 7M11.5 4.5l-7 7" stroke="currentColor" strokeWidth="1.4" strokeLinecap="round" /></svg>,
  pin: <svg viewBox="0 0 16 16" width="16" height="16" aria-hidden="true"><path d="M9.8 2.2l4 4-1.6.6-2.3 2.3.2 2.6-1 1-2.6-2.6-3.9 3.9-.7-.7 3.9-3.9L3.2 7.8l1-1 2.6.2 2.3-2.3z" fill="currentColor" /></svg>,
  pinOff: <svg viewBox="0 0 16 16" width="16" height="16" aria-hidden="true"><path d="M9.8 2.2l4 4-1.6.6-2.3 2.3.2 2.6-1 1-2.6-2.6-3.9 3.9-.7-.7 3.9-3.9L3.2 7.8l1-1 2.6.2 2.3-2.3z" fill="none" stroke="currentColor" strokeWidth="1.1" strokeLinejoin="round" /></svg>,
  trash: <svg viewBox="0 0 16 16" width="16" height="16" aria-hidden="true"><path d="M3.5 4.5h9M6.5 4.5V3h3v1.5M5 4.5l.5 8h5l.5-8" fill="none" stroke="currentColor" strokeWidth="1.2" strokeLinecap="round" strokeLinejoin="round" /></svg>,
  down: <svg viewBox="0 0 12 12" width="12" height="12" aria-hidden="true"><path d="M2.5 4.5L6 8l3.5-3.5" fill="none" stroke="currentColor" strokeWidth="1.4" /></svg>,
  right: <svg viewBox="0 0 12 12" width="12" height="12" aria-hidden="true"><path d="M4.5 2.5L8 6l-3.5 3.5" fill="none" stroke="currentColor" strokeWidth="1.4" /></svg>,
};

/** A small menu under its button. A press outside, or Escape, closes it. */
export function Pop({ children, onClose, className = '', label }: { children: ReactNode; onClose: () => void; className?: string; label: string }) {
  const ref = useRef<HTMLDivElement>(null);
  useEffect(() => {
    const out = (e: MouseEvent) => { if (!ref.current?.contains(e.target as Node) && !(e.target as HTMLElement).closest?.('[aria-expanded="true"]')) onClose(); };
    const esc = (e: KeyboardEvent) => { if (e.key === 'Escape') onClose(); };
    window.addEventListener('mousedown', out); window.addEventListener('keydown', esc);
    return () => { window.removeEventListener('mousedown', out); window.removeEventListener('keydown', esc); };
  }, [onClose]);
  return <div ref={ref} className={`qn-pop ${className}`} role="menu" aria-label={label}>{children}</div>;
}


// ---- one row of the notebook

/** The line of a row: the question, a mark when a word could not apply, its number, its time, and its own pin and close. */
export function RowLine({ r, on, nb, running }: { r: Rec; on: boolean; nb: Notebook; running?: boolean }) {
  const t = timeWords(r);
  const fig = splitFigure(r.headline ?? '');
  const live = r.id === DRAFT;
  const ticked = nb.selected.has(r.id);
  return <div className={`qn-line ${on ? 'qn-line-on' : ''}`} data-testid="notebook-row" data-id={r.id} data-pinned={r.pinned ? 'yes' : 'no'} data-open={on ? 'yes' : 'no'}>
    {live ? <span className="qn-tick-gap" aria-hidden="true" />
      : <input type="checkbox" className={`qn-tick ${ticked ? 'qn-tick-on' : ''}`} checked={ticked} onChange={() => nb.toggleSelected(r.id)} aria-label={`Choose to time: ${rowText(r)}`} data-testid="row-tick" />}
    <button type="button" className="qn-line-main" aria-expanded={on} onClick={() => nb.open(r)} data-testid="row-open">
      <span className="qn-chev" aria-hidden="true">{on ? Icon.down : Icon.right}</span>
      <span className={`qn-line-text ${isDaxRow(r) ? 'qn-mono' : ''}`} title={rowText(r)}>{rowText(r)}</span>
      <span className="qn-mark">{r.dropped ? <span className="qn-mark-warn" title={r.dropped} role="img" aria-label={markLabel(r.dropped)} data-testid="row-dropped" /> : null}</span>
      <span className={`qn-line-n qn-num ${r.headline === 'No rows' || r.headline === 'Blank' ? 'qn-faint' : ''}`} title={r.headline ?? undefined}>{running || on || !r.headline ? '' : <>{fig.label && <span className="qn-line-lab">{fig.label}</span>}<span className="qn-line-val">{fig.value}</span></>}</span>
      <span className={`qn-line-t qn-num ${t.slow ? 'qn-slow' : 'qn-faint'}`} data-testid="row-time">{running ? '' : t.text}</span>
    </button>
    {live ? <span className="qn-row-acts" />
      : <span className="qn-row-acts">
        <button type="button" className={`qn-icon qn-icon-sm ${r.pinned ? 'qn-pinned' : 'qn-quiet-act'}`} title={r.pinned ? 'Unpin' : 'Pin to the top'} aria-label={r.pinned ? `Unpin: ${rowText(r)}` : `Pin: ${rowText(r)}`} aria-pressed={!!r.pinned}
          onClick={() => void nb.act(r.pinned ? 'unpin' : 'pin', r.id)} data-testid="row-pin">{r.pinned ? Icon.pin : Icon.pinOff}</button>
        <button type="button" className="qn-icon qn-icon-sm qn-quiet-act" title="Close. It stays in the history." aria-label={`Close: ${rowText(r)}`} onClick={() => void nb.act('close', r.id)} data-testid="row-close">{Icon.close}</button>
      </span>}
  </div>;
}

