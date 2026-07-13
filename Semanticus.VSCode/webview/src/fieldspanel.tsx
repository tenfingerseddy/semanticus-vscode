import { useMemo, useState } from 'react';
import type { DaxModel } from './dax';

// The DAX reference text inserted/dragged for each object.
const measureRef = (name: string) => `[${name}]`;
const columnRef = (table: string, name: string) => `'${table}'[${name}]`;
const tableRef = (name: string) => `'${name}'`;

interface FieldsPanelProps { model: DaxModel; onInsert: (text: string) => void; }

// A model-fields sidebar for the code editors: tables → their measures + columns. Click an item to insert its
// DAX reference at the cursor, or drag it onto the editor (sets text/plain = the reference). Replaces the
// "drag from the VS Code tree" idea, which isn't reliable across the webview boundary.
export function FieldsPanel({ model, onInsert }: FieldsPanelProps) {
  const [filter, setFilter] = useState('');
  const [open, setOpen] = useState<Set<string>>(new Set());

  const tables = useMemo(() => {
    const f = filter.trim().toLowerCase();
    const byTable = model.tables.map((t) => ({
      table: t,
      measures: model.measures.filter((m) => m.table === t).map((m) => m.name),
      columns: model.columns.filter((c) => c.table === t).map((c) => c.name),
    }));
    if (!f) return byTable.filter((g) => g.measures.length || g.columns.length);
    // filter: keep tables whose name matches (all children) or that have matching children
    return byTable.map((g) => {
      if (g.table.toLowerCase().includes(f)) return g;
      return { ...g, measures: g.measures.filter((m) => m.toLowerCase().includes(f)), columns: g.columns.filter((c) => c.toLowerCase().includes(f)) };
    }).filter((g) => g.measures.length || g.columns.length);
  }, [model, filter]);

  const isOpen = (t: string) => filter.trim() !== '' || open.has(t);
  const toggle = (t: string) => { const n = new Set(open); n.has(t) ? n.delete(t) : n.add(t); setOpen(n); };

  const drag = (text: string) => (e: React.DragEvent) => { e.dataTransfer.setData('text/plain', text); e.dataTransfer.effectAllowed = 'copy'; };

  return (
    <div className="flex flex-col h-full min-h-0 overflow-hidden" style={{ border: '1px solid var(--sem-border)', borderRadius: 8, background: 'var(--sem-surface-2)' }}>
      <div className="px-2 py-1.5" style={{ borderBottom: '1px solid var(--sem-border)' }}>
        <input value={filter} onChange={(e) => setFilter(e.target.value)} placeholder="Fields… (click or drag to insert)"
          style={{ width: '100%', boxSizing: 'border-box', background: 'var(--sem-surface-2)', color: 'var(--sem-fg)', border: '1px solid var(--sem-border)', borderRadius: 4, fontSize: 11, padding: '3px 6px' }} />
      </div>
      <div className="flex-1 min-h-0 overflow-auto py-1 text-[12px]">
        {tables.length === 0 && <div className="px-2 py-2" style={{ color: 'var(--sem-muted)' }}>No fields.</div>}
        {tables.map((g) => (
          <div key={g.table}>
            <div onClick={() => toggle(g.table)} className="flex items-center gap-1 px-2 py-0.5 cursor-pointer select-none"
              draggable onDragStart={drag(tableRef(g.table))} title={tableRef(g.table)}>
              <span className="inline-block w-3 text-[9px]" style={{ transform: isOpen(g.table) ? 'none' : 'rotate(-90deg)', color: 'var(--sem-muted)' }}>▾</span>
              <span style={{ color: '#4ec9b0' }}>▦</span>
              <span className="font-medium truncate">{g.table}</span>
            </div>
            {isOpen(g.table) && (
              <div>
                {g.measures.map((m) => (
                  <Item key={'m' + m} icon="ƒ" iconColor="var(--sem-accent)" label={m} ref_={measureRef(m)} onInsert={onInsert} onDragStart={drag(measureRef(m))} />
                ))}
                {g.columns.map((c) => (
                  <Item key={'c' + c} icon="▭" iconColor="#9cdcfe" label={c} ref_={columnRef(g.table, c)} onInsert={onInsert} onDragStart={drag(columnRef(g.table, c))} />
                ))}
              </div>
            )}
          </div>
        ))}
      </div>
    </div>
  );
}

function Item({ icon, iconColor, label, ref_, onInsert, onDragStart }: { icon: string; iconColor: string; label: string; ref_: string; onInsert: (t: string) => void; onDragStart: (e: React.DragEvent) => void }) {
  return (
    <div onClick={() => onInsert(ref_)} draggable onDragStart={onDragStart} title={ref_ + '  (click to insert · drag to editor)'}
      className="flex items-center gap-1.5 pl-7 pr-2 py-0.5 cursor-pointer select-none hover:bg-[var(--sem-surface-2)]">
      <span style={{ color: iconColor, width: 12, textAlign: 'center' }}>{icon}</span>
      <span className="truncate">{label}</span>
    </div>
  );
}
