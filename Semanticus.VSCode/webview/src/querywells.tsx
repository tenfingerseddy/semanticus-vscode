import { useMemo, useState } from 'react';
import type { DaxModel } from './dax';
import { describe, FilterPopover, type ColumnFilter, type FilterFamily } from './gridfilter';
import { fieldRank, rankFields } from './query-model';
import { familyByName, parseRef, type FieldKind, type PivotConfig, type WellField } from './daxwells';
import { useSurfaceEscape } from './surface-escape';

// Queries wells (Kane, 8 Oct): Rows, Columns, Measures, Filters as four labelled insets, each with its own +, and every
// field a chip coloured by its kind. They replace "the sentence" (option C, 27 Sep); Kane asked to go back to labelled
// wells with colour. Same PivotConfig, same query builder (buildQuery), same drag vocabulary, so everything the sentence
// could do the wells can do: drop a field on a well, click a chip's x to remove it, click a filter to edit it, and the
// swap icon on a measure opens Compare a rewrite. The + opens the add list already on that well, one step.

type Slot = 'values' | 'rows' | 'cols' | 'filters';
const WELLS: { key: Slot; label: string; accepts: FieldKind[]; add: string; find: string; empty: string }[] = [
  { key: 'rows', label: 'Rows', accepts: ['column'], add: 'Add a column to Rows', find: 'Find a column', empty: 'Drop a column here' },
  { key: 'cols', label: 'Columns', accepts: ['column'], add: 'Add a column to Columns', find: 'Find a column', empty: 'Drop a column here' },
  { key: 'values', label: 'Measures', accepts: ['measure'], add: 'Add a measure', find: 'Find a measure', empty: 'Drop a measure here' },
  { key: 'filters', label: 'Filters', accepts: ['column'], add: 'Add a filter', find: 'Find a column', empty: 'Drop a column here' },
];

const meta = 'text-(length:--sem-text-meta)';
const muted = { color: 'var(--sem-muted)' };

// The glyph's shape says the kind as well as its colour, so high contrast (which hands the colours back to the text
// colour) and colour-blind eyes lose nothing.
const GLYPH: Record<FieldKind, string> = { measure: 'fx', column: '▭', table: '▦' };
export function KindGlyph({ kind }: { kind: FieldKind }) {
  return <span className={`sem-fchip-g${kind === 'measure' ? ' sem-fchip-g-fx' : ''}`} aria-hidden="true">{GLYPH[kind]}</span>;
}

export function QueryWells({ config, onConfig, onCompare, compareBadge, model, disabled }: {
  config: PivotConfig; onConfig: (c: PivotConfig) => void; onCompare?: (valueRef: string) => void;
  compareBadge?: { text: string; color: string } | null; model: DaxModel; disabled?: boolean;
}) {
  const [over, setOver] = useState<Slot | null>(null);
  const [editFilter, setEditFilter] = useState<{ idx: number; x: number; y: number } | null>(null);
  const [adding, setAdding] = useState<Slot | null>(null);
  const [dropMsg, setDropMsg] = useState<string | null>(null);

  const addField = (slot: Slot, f: WellField) => {
    if (slot === 'filters') {
      if (config.filters.some((x) => x.ref === f.ref)) return;
      const family = familyByName(f.name);
      onConfig({ ...config, filters: [...config.filters, { ref: f.ref, name: f.name, family, filter: defaultFilter(family) }] });
      return;
    }
    const list = config[slot];
    if (list.some((x) => x.ref === f.ref)) return;
    onConfig({ ...config, [slot]: [...list, f] });
  };
  const drop = (w: typeof WELLS[number]) => (e: React.DragEvent) => {
    e.preventDefault(); setOver(null);
    const f = parseRef(e.dataTransfer.getData('text/plain'));
    if (!f) return;
    if (!w.accepts.includes(f.kind)) {
      setDropMsg(w.key === 'values' ? 'Measures takes a measure, not a column.' : `${w.label} takes a column, not a measure.`);
      setTimeout(() => setDropMsg(null), 2400);
      return;
    }
    addField(w.key, f);
  };
  const removeAt = (slot: Slot, i: number) => onConfig({ ...config, [slot]: (config[slot] as unknown[]).filter((_, j) => j !== i) });

  return (
    <div className="relative" role="group" aria-label="The query: rows, columns, measures and filters" data-testid="queries-wells">
      <div className="sem-wells">
        {WELLS.map((w, wi) => {
          const items = w.key === 'filters' ? config.filters : config[w.key];
          return (
            <div key={w.key} className={`sem-inset sem-well${over === w.key ? ' sem-well-over' : ''}`} data-well={w.key}
              onDragOver={(e) => { e.preventDefault(); setOver(w.key); }} onDragLeave={() => setOver((o) => (o === w.key ? null : o))} onDrop={drop(w)}>
              <div className="sem-well-head">
                <span className="sem-well-label">{w.label}</span>
                <span className="relative sem-push">
                  <button type="button" className="sem-well-add" disabled={disabled} aria-haspopup="menu" aria-expanded={adding === w.key} aria-label={w.add} title={w.add}
                    data-testid={`well-add-${w.key}`} onClick={() => setAdding(adding === w.key ? null : w.key)}>+</button>
                  {adding === w.key && <AddMenu well={w} model={model} config={config} alignRight={wi >= 2}
                    onPick={(f) => { addField(w.key, f); setAdding(null); }} onClose={() => setAdding(null)} />}
                </span>
              </div>
              <div className="sem-well-body">
                {w.key === 'filters'
                  ? config.filters.map((pf, i) => (
                    <span key={pf.ref} className="sem-fchip sem-fchip-column" data-well-chip="filters">
                      <KindGlyph kind="column" />
                      <button type="button" disabled={disabled} className="inline-flex items-center gap-1 min-w-0" style={{ background: 'none', border: 'none', color: 'inherit', font: 'inherit', cursor: 'pointer', padding: 0 }}
                        aria-label={`Edit the filter on ${pf.name}`}
                        onClick={(e) => { const r = (e.currentTarget as HTMLElement).getBoundingClientRect(); setEditFilter({ idx: i, x: r.left, y: r.bottom + 4 }); }}>
                        <span className="sem-fchip-name">{pf.name}</span><span className="sem-fchip-note">{describe(pf.filter)}</span>
                      </button>
                      <XButton label={`Remove the filter on ${pf.name}`} disabled={disabled} onClick={() => removeAt('filters', i)} />
                    </span>
                  ))
                  : (items as WellField[]).map((f, i) => (
                    <span key={f.ref} className={`sem-fchip sem-fchip-${f.kind}`} data-well-chip={w.key}>
                      <KindGlyph kind={f.kind} />
                      <span className="sem-fchip-name">{f.name}</span>
                      {w.key === 'values' && onCompare && (
                        <button type="button" className="sem-fchip-btn inline-flex items-center gap-1" onClick={() => onCompare(f.ref)} aria-label={`Compare a rewrite of ${f.name}`} title="Compare a rewrite of this measure">
                          <span aria-hidden="true">⇄</span>
                          {i === 0 && compareBadge && <span className="xe-msg" style={{ '--xe-dot': compareBadge.color } as React.CSSProperties}>{compareBadge.text}</span>}
                        </button>
                      )}
                      <XButton label={`Remove ${f.name}`} disabled={disabled} onClick={() => removeAt(w.key, i)} />
                    </span>
                  ))}
                {items.length === 0 && <span className={meta} style={muted}>{w.empty}</span>}
              </div>
            </div>
          );
        })}
      </div>
      {dropMsg && <div className="mt-1"><span className="xe-msg" style={{ '--xe-dot': 'var(--sem-warn)' } as React.CSSProperties} role="status">{dropMsg}</span></div>}

      {editFilter && config.filters[editFilter.idx] && (
        <>
          <div className="fixed inset-0" style={{ zIndex: 40 }} onClick={() => setEditFilter(null)} />
          <div className="fixed" style={{ zIndex: 50, top: editFilter.y, left: Math.max(8, Math.min(editFilter.x, window.innerWidth - 296)) }} onClick={(e) => e.stopPropagation()}>
            <FilterPopover family={config.filters[editFilter.idx].family} name={config.filters[editFilter.idx].name} initial={config.filters[editFilter.idx].filter}
              onApply={(filter) => { const next = [...config.filters]; next[editFilter.idx] = { ...next[editFilter.idx], filter }; onConfig({ ...config, filters: next }); setEditFilter(null); }}
              onClear={() => { removeAt('filters', editFilter.idx); setEditFilter(null); }} />
          </div>
        </>
      )}
    </div>
  );
}

function XButton({ label, onClick, disabled }: { label: string; onClick: () => void; disabled?: boolean }) {
  return <button type="button" aria-label={label} title={label} disabled={disabled} onClick={onClick} className="sem-fchip-btn">✕</button>;
}

function AddMenu({ well, model, config, alignRight, onPick, onClose }: {
  well: typeof WELLS[number]; model: DaxModel; config: PivotConfig; alignRight: boolean; onPick: (f: WellField) => void; onClose: () => void;
}) {
  const [find, setFind] = useState('');
  useSurfaceEscape(() => { onClose(); document.querySelector<HTMLElement>(`[data-testid="well-add-${well.key}"]`)?.focus(); });
  const f = find.trim().toLowerCase();
  const used = new Set([...config.values, ...config.rows, ...config.cols].map((x) => x.ref).concat(config.filters.map((x) => x.ref)));
  const options = useMemo(() => {
    // Exact name first, then starts-with, then the rest, then by description: the finder reads what the model says a field is, not only what
    // it is called (UAT 17 Oct, F41), and Enter takes the first (18 Oct, N6).
    if (well.key === 'values') return rankFields(f, model.measures).slice(0, 60)
      .map((m): WellField => ({ kind: 'measure', ref: `[${m.name}]`, name: m.name, table: m.table }));
    // A column is found by its own name first, and by "Table name" words (Date year) after the names that match.
    return model.columns.map((c, i) => ({ c, i, k: fieldRank(f, c.name, c.description) || (`${c.table} ${c.name}`.toLowerCase().includes(f) ? 1.5 : 0) }))
      .filter((e) => e.k > 0).sort((a, b) => b.k - a.k || a.i - b.i).slice(0, 60)
      .map(({ c }): WellField => ({ kind: 'column', ref: `'${c.table}'[${c.name}]`, name: c.name, table: c.table }));
  }, [well, model, f]);
  return (
    <div role="menu" aria-label={well.add} className="sem-card sem-stack absolute z-30 overflow-auto" data-testid="well-add-menu"
      style={{ top: '100%', ...(alignRight ? { right: 0 } : { left: 0 }), marginTop: 'var(--sem-space-1)', width: 'calc(var(--sem-row) * 10)', maxHeight: 'calc(var(--sem-row) * 12)' }}>
      <span className="sem-label">{well.add}</span>
      <input autoFocus className={`sem-field ${meta}`} value={find} onChange={(e) => setFind(e.target.value)} placeholder={well.find} aria-label={well.find}
        onKeyDown={(e) => { if (e.key === 'Enter') { e.preventDefault(); const top = options.find((o) => !used.has(o.ref)); if (top) onPick(top); } }} />
      {options.length === 0 && <span className={meta} style={muted} role="status">Nothing in this model matches "{find}".</span>}
      {options.map((o) => (
        <button key={o.ref} type="button" role="menuitem" disabled={used.has(o.ref)} className="sem-btn sem-btn-sm" style={{ justifyContent: 'flex-start', textAlign: 'left' }} onClick={() => onPick(o)}>
          {o.kind === 'column' ? `${o.name} (${o.table})` : o.name}
        </button>
      ))}
    </div>
  );
}

function defaultFilter(family: FilterFamily): ColumnFilter {
  switch (family) {
    case 'number': return { kind: 'number', op: 'between', a: null, b: null };
    case 'date': return { kind: 'date', mode: 'rel', n: 6, unit: 'month' };
    case 'boolean': return { kind: 'boolean', value: true };
    default: return { kind: 'text', op: 'contains', value: '', picked: [] };
  }
}
