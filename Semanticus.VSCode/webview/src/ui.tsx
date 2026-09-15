import { useState, type ReactNode } from 'react';
import { DaxEditor, useDaxModelContext } from './daxeditor';

// Small shared input primitives used by more than one tab (kept minimal — the divergent
// Panel/Button/Banner variants stay local to each tab on purpose).

/** A DAX expression input: a compact CodeMirror editor (syntax highlight + model-aware autocomplete +
 *  drag-to-insert), used by the Pivot and DAX Lab forms. `rows` sets the minimum height. */
export function Mono({ value, onChange, rows }: { value: string; onChange: (v: string) => void; rows: number }) {
  const model = useDaxModelContext();
  return <DaxEditor value={value} onChange={onChange} model={model} lineNumbers={false} minHeight={Math.max(34, rows * 20)} />;
}

/**
 * The row-expand triangle. Drawn as a shape, not a character: the ▾ and ▶ glyphs fall back to a tiny
 * comma-like mark wherever the UI font has no triangle, which is what shipped and read as broken.
 * Points right when closed, down when open.
 */
export function Caret({ open, className = '' }: { open: boolean; className?: string }) {
  return (
    <svg width="8" height="8" viewBox="0 0 8 8" aria-hidden="true" focusable="false"
      className={`inline-block shrink-0 transition-transform ${className}`}
      style={{ transform: open ? 'rotate(90deg)' : 'none', color: 'var(--sem-muted)' }}>
      <path d="M2 0.8 L6.6 4 L2 7.2 Z" fill="currentColor" />
    </svg>
  );
}

/** Labelled control row. */
export function Field({ label, children }: { label: string; children: ReactNode }) {
  return (
    <div className="flex flex-col gap-1">
      <label className="text-[11px]" style={{ color: 'var(--sem-muted)' }}>{label}</label>
      {children}
    </div>
  );
}

/**
 * A file path, folded away. A Windows path is an implementation detail to almost everyone reading the page,
 * so the page says what the thing is and the path is one click away.
 */
export function PathDisclosure({ path, label = 'Where is this saved?', className = '' }: { path?: string | null; label?: string; className?: string }) {
  const [open, setOpen] = useState(false);
  if (!path) return null;
  return (
    <span className={`inline-flex min-w-0 items-center gap-1.5 ${className}`}>
      <button type="button" onClick={() => setOpen((o) => !o)} aria-expanded={open}
        className="shrink-0 text-[10.5px]" style={{ color: 'var(--sem-muted)', background: 'none', border: 0, padding: 0, textDecoration: 'underline', textUnderlineOffset: 2, cursor: 'pointer' }}>
        {open ? 'Hide where it is saved' : label}
      </button>
      {open && <span className="min-w-0 break-all text-[10.5px]" style={{ color: 'var(--sem-muted)' }}>{path}</span>}
    </span>
  );
}

/** Split a textarea into trimmed non-empty lines (DAX group-by / filter lists). */
export const lines = (s: string) => s.split('\n').map((x) => x.trim()).filter(Boolean);
