import { useMemo, useState } from 'react';
import type { DaxModel } from './dax';
import { selectInProperties, focusSelectInProperties } from './bridge';
import { RevealBtn, rowKeyProps } from './objectactions';

// The glyphs take the field kind colours (Kane, 8 Oct), the same ones the Queries wells use, so the drawer and the wells speak one
// colour language: measure gold, column blue, table teal. The glyph's shape still says the kind in high contrast.
// The DAX reference text inserted/dragged for each object.
const measureRef = (name: string) => `[${name}]`;
const columnRef = (table: string, name: string) => `'${table}'[${name}]`;
const tableRef = (name: string) => `'${name}'`;

// Engine object refs (the same vocabulary the Model tree and the other navigators use) — the selection bus
// and Reveal-in-tree identify objects by these, never by DAX text.
const measureObjRef = (table: string, name: string) => `measure:${table}/${name}`;
const columnObjRef = (table: string, name: string) => `column:${table}/${name}`;
const tableObjRef = (name: string) => `table:${name}`;

// inUse: the DAX references the result on screen is built from, marked in the list (Queries' mockup, option C).
interface FieldsPanelProps { model: DaxModel; onInsert: (text: string) => void; inUse?: ReadonlySet<string>; }

// A model-fields sidebar for the code editors: tables → their measures + columns. Click an item to insert its
// DAX reference at the cursor, or drag it onto the editor (sets text/plain = the reference). Replaces the
// "drag from the VS Code tree" idea, which isn't reliable across the webview boundary. A click also feeds the
// selection bus so the Properties view follows what you're working with (focus stays here).
export function FieldsPanel({ model, onInsert, inUse }: FieldsPanelProps) {
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
    // The panel draws no box of its own: the card it sits in (DAX Lab's Fields card) is the box.
    <div className="flex flex-col flex-1 min-h-0 gap-2">
      <input value={filter} onChange={(e) => setFilter(e.target.value)} placeholder="Fields… (click or drag to insert)" aria-label="Find a field"
        className="sem-field sem-field-sm w-full" />
      <div className="flex-1 min-h-0 overflow-auto text-xs">
        {tables.length === 0 && <div className="py-2 sem-text-meta">No fields.</div>}
        {tables.map((g) => (
          <div key={g.table}>
            <div onClick={() => toggle(g.table)} className="group flex items-center gap-1 py-1 cursor-pointer select-none"
              draggable onDragStart={drag(tableRef(g.table))} title={tableRef(g.table)}>
              <span className="inline-block w-3 text-(length:--sem-text-label)" style={{ color: 'var(--sem-muted)' }}>{isOpen(g.table) ? '▾' : '▸'}</span>
              <span style={{ color: 'var(--sem-kind-table)' }}>▦</span>
              {/* Click the NAME to select the table (feed the Properties bus); the chevron / rest of the row toggles
                  expand-collapse. Split so selecting a table never thrashes its column list open/closed. The name is
                  a real keyboard control (Enter/Space) so it's reachable without a mouse. */}
              <span className="font-medium truncate flex-1" title={'Show ' + g.table + ' in Properties'}
                {...rowKeyProps(() => selectInProperties(tableObjRef(g.table)), () => focusSelectInProperties(tableObjRef(g.table)))}
                onClick={(e) => { e.stopPropagation(); selectInProperties(tableObjRef(g.table)); }}>{g.table}</span>
              <RevealBtn objRef={tableObjRef(g.table)} className="invisible group-hover:visible group-focus-within:visible" />
            </div>
            {isOpen(g.table) && (
              <div>
                {g.measures.map((m) => (
                  <Item key={'m' + m} icon="ƒ" iconColor="var(--sem-kind-measure)" label={m} ref_={measureRef(m)} objRef={measureObjRef(g.table, m)} onInsert={onInsert} onDragStart={drag(measureRef(m))} used={!!inUse?.has(measureRef(m))} />
                ))}
                {g.columns.map((c) => (
                  <Item key={'c' + c} icon="▭" iconColor="var(--sem-kind-column)" label={c} ref_={columnRef(g.table, c)} objRef={columnObjRef(g.table, c)} onInsert={onInsert} onDragStart={drag(columnRef(g.table, c))} used={!!inUse?.has(columnRef(g.table, c))} />
                ))}
              </div>
            )}
          </div>
        ))}
      </div>
    </div>
  );
}

function Item({ icon, iconColor, label, ref_, objRef, onInsert, onDragStart, used = false }: { icon: string; iconColor: string; label: string; ref_: string; objRef: string; onInsert: (t: string) => void; onDragStart: (e: React.DragEvent) => void; used?: boolean }) {
  // Keyboard control (rowKeyProps = role=button) on the LABEL span, not the row div: the nested Reveal button
  // inside a role=button row is wrong ARIA, and its Enter/Space would bubble into the insert action. The row
  // keeps the mouse onClick for the large click target (Reveal's own click stops propagation).
  const activate = () => { selectInProperties(objRef); onInsert(ref_); };
  return (
    <div data-object-ref={objRef} onClick={activate}
      draggable onDragStart={onDragStart} title={ref_ + '  (click to insert · drag to editor)'}
      className="group flex items-center gap-2 pl-4 py-1 cursor-pointer select-none hover:bg-[var(--sem-hover)]">
      <span style={{ color: iconColor, width: 12, textAlign: 'center' }}>{icon}</span>
      <span className="truncate flex-1" {...rowKeyProps(activate, () => focusSelectInProperties(objRef))}>{label}</span>
      {used && <span className="shrink-0 xe-dot xe-dot-good sem-text-meta" data-field-in-use title="The result on screen uses this field">in use</span>}
      <RevealBtn objRef={objRef} className="invisible group-hover:visible group-focus-within:visible" />
    </div>
  );
}
