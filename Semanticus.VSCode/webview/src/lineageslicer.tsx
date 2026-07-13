import { useEffect, useMemo, useRef, useState } from 'react';
import { KIND_COLOR, KIND_GLYPH, resolveColor, type FacetOption } from './lineagetypes';

// A compact, reusable multi-select "slicer" dropdown (Power-BI-style): a button that shows the facet name + a count
// badge, opening a searchable checkbox list with Select-all / Clear. Shared by the graph AND tree lineage views so a
// builder can scope the picture to a SUBSET of tables or fields — the single search box only picks one node.
// Closes on outside-click / Escape. Options render only while open (a 300-measure list stays cheap until needed).
export function MultiSelect({ label, options, selected, onChange, width = 240, title }: {
  label: string;
  options: FacetOption[];
  selected: Set<string>;
  onChange: (next: Set<string>) => void;
  width?: number;
  title?: string;
}) {
  const [open, setOpen] = useState(false);
  const [q, setQ] = useState('');
  const wrap = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (!open) return;
    const onDoc = (e: MouseEvent) => { if (wrap.current && !wrap.current.contains(e.target as Node)) setOpen(false); };
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') setOpen(false); };
    document.addEventListener('mousedown', onDoc);
    document.addEventListener('keydown', onKey);
    return () => { document.removeEventListener('mousedown', onDoc); document.removeEventListener('keydown', onKey); };
  }, [open]);

  const ql = q.trim().toLowerCase();
  const filtered = useMemo(
    () => (ql ? options.filter((o) => (o.label + ' ' + (o.sub ?? '')).toLowerCase().includes(ql)) : options),
    [options, ql],
  );
  const count = selected.size;
  const toggle = (v: string) => { const n = new Set(selected); n.has(v) ? n.delete(v) : n.add(v); onChange(n); };
  // Select-all applies to the CURRENT filter (so a builder can filter to "Sales*" then tick them all); Clear wipes all.
  const selectAllFiltered = () => { const n = new Set(selected); for (const o of filtered) n.add(o.value); onChange(n); };

  const btnActive = count > 0;
  return (
    <div ref={wrap} className="relative">
      <button onClick={() => setOpen((o) => !o)} title={title ?? `Filter by ${label.toLowerCase()}`}
        className="flex items-center gap-1 text-[11px] px-2 py-0.5 rounded-md font-medium transition-[transform,filter] duration-100 active:scale-95 hover:brightness-110"
        style={btnActive
          ? { background: 'var(--sem-accent)', color: 'var(--sem-on-accent)' }
          : { background: 'var(--sem-surface-2)', color: 'var(--sem-muted)', border: '1px solid var(--sem-border)' }}>
        {label}
        {count > 0 && <span className="tnum px-1 rounded" style={{ background: 'rgba(255,255,255,0.25)', fontSize: 9 }}>{count}</span>}
        <span style={{ fontSize: 8, opacity: 0.8 }}>▾</span>
      </button>
      {open && (
        <div className="absolute z-20 mt-1 rounded-md overflow-hidden" style={{ width, background: 'var(--sem-surface)', border: '1px solid var(--sem-border)', boxShadow: '0 8px 24px rgba(0,0,0,0.45)' }}>
          <div className="p-1.5 border-b" style={{ borderColor: 'var(--sem-border)' }}>
            <input autoFocus value={q} onChange={(e) => setQ(e.target.value)} placeholder={`Filter ${label.toLowerCase()}…`} spellCheck={false}
              className="w-full text-[12px] px-2 py-1 rounded outline-none" style={{ background: 'var(--sem-bg)', color: 'var(--sem-fg)', border: '1px solid var(--sem-border)' }} />
            <div className="flex items-center justify-between mt-1 px-0.5">
              <button onMouseDown={(e) => { e.preventDefault(); selectAllFiltered(); }} className="text-[10px] hover:underline" style={{ color: 'var(--sem-accent)' }}>Select all{ql ? ' shown' : ''}</button>
              <span className="text-[10px] tnum" style={{ color: 'var(--sem-muted)' }}>{count} selected</span>
              <button onMouseDown={(e) => { e.preventDefault(); onChange(new Set()); }} className="text-[10px] hover:underline" style={{ color: 'var(--sem-muted)' }}>Clear</button>
            </div>
          </div>
          <div className="overflow-auto" style={{ maxHeight: 260 }}>
            {filtered.length === 0 && <div className="text-[11px] px-2 py-3 text-center" style={{ color: 'var(--sem-muted)' }}>No matches</div>}
            {filtered.map((o) => {
              const on = selected.has(o.value);
              return (
                <label key={o.value} className="flex items-center gap-2 px-2 py-1 text-[12px] cursor-pointer hover:bg-[var(--sem-surface-2)]" style={{ color: 'var(--sem-fg)' }}>
                  <input type="checkbox" checked={on} onChange={() => toggle(o.value)} style={{ accentColor: 'var(--sem-accent)' }} />
                  {o.kind && <span className="shrink-0" style={{ color: resolveColor(KIND_COLOR[o.kind], '#9aa0aa') }}>{KIND_GLYPH[o.kind] ?? '•'}</span>}
                  <span className="truncate flex-1">{o.label}</span>
                  {o.sub && <span className="shrink-0 text-[10px] truncate" style={{ color: 'var(--sem-muted)', maxWidth: 90 }}>{o.sub}</span>}
                </label>
              );
            })}
          </div>
        </div>
      )}
    </div>
  );
}
