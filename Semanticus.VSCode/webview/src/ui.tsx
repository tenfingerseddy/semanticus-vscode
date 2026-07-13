import type { ReactNode } from 'react';
import { DaxEditor, useDaxModelContext } from './daxeditor';

// Small shared input primitives used by more than one tab (kept minimal — the divergent
// Panel/Button/Banner variants stay local to each tab on purpose).

/** A DAX expression input: a compact CodeMirror editor (syntax highlight + model-aware autocomplete +
 *  drag-to-insert), used by the Pivot and DAX Lab forms. `rows` sets the minimum height. */
export function Mono({ value, onChange, rows }: { value: string; onChange: (v: string) => void; rows: number }) {
  const model = useDaxModelContext();
  return <DaxEditor value={value} onChange={onChange} model={model} lineNumbers={false} minHeight={Math.max(34, rows * 20)} />;
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

/** Split a textarea into trimmed non-empty lines (DAX group-by / filter lists). */
export const lines = (s: string) => s.split('\n').map((x) => x.trim()).filter(Boolean);
