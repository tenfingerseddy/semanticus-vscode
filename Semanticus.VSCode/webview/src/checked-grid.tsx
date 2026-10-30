import { createRoot } from 'react-dom/client';
import type { ChecksPageView } from './checks-page-model';
import { MarksHostContext, newDescriptionDrafts, type MarksHost } from './marks-host';
import { MeasureMarksPanel } from './marks-panel';
import { adviceRecheck } from './marks-model';
import { RunPlan, type PlanMeasure } from './run-plan';
import { useEffect, useMemo, useState } from 'react';
import './checked.css';
import './marks.css';

// The Properties grid's marks: a measure shows its Description and seven marks, the model shows the run plan.
// propgrid.js holds the page's one VS Code API and hands it over, because a page may take it only once.
type GridBridge = { post: (message: unknown) => void; model: () => string | null; key: () => string | null };
const grid = (window as unknown as { semanticusGrid?: GridBridge }).semanticusGrid;
const waiting = new Map<number, { resolve: (v: unknown) => void; reject: (e: Error) => void }>();
const changed = new Set<() => void>();
const pushed = new Set<(change: unknown) => void>();
let next = 0;
let key: string | null = null, previous: string | null = null;
let requestedMark: { name: string; id: number } | undefined;

const advice = adviceRecheck(() => host.rpc<{ current?: boolean }>('getConsent'));
const host: MarksHost = {
  rpc: <T,>(method: string, ...args: unknown[]) => new Promise<T>((resolve, reject) => {
    if (!grid) { reject(new Error('Properties is not ready yet.')); return; }
    const id = ++next;
    waiting.set(id, { resolve: v => resolve(v as T), reject });
    grid.post({ type: 'marksRpc', id, method, args, model: grid.model() ?? undefined });
  }),
  onChange: fn => { changed.add(fn); return () => { changed.delete(fn); }; },
  // Properties has no permission card of its own: Turn on opens Studio's Pro page with the card up (the same Turn on). The card
  // here checks again once permission is on, as Studio's does (fx29b F: it kept "Pro advice is off" until Check now).
  open: (where, ref, then) => { if (where === 'advice') advice.arm(then); grid?.post({ type: 'marksOpen', where, ref }); },
  undo: () => grid?.post({ type: 'runCommand', command: 'semanticus.undo', model: grid.model() ?? undefined }),
  onCheckState: fn => { pushed.add(fn); return () => { pushed.delete(fn); }; },
  editFormula: ref => grid?.post({ type: 'marksEditFormula', ref, model: grid.model() ?? undefined }),
};

function ModelPlan() {
  const [measures, setMeasures] = useState<PlanMeasure[]>([]);
  useEffect(() => {
    const read = () => host.rpc<ChecksPageView>('getChecksPage').then(p => setMeasures(p.measures.map(m => ({ ref: m.ref, name: m.name, table: m.table, folder: m.folder })))).catch(() => { /* the plan still shows */ });
    void read();
    return host.onChange(() => { void read(); });
  }, []);
  return <RunPlan measures={measures} />;
}

function GridMarks({ selection }: { selection: string[] }) {
  const marksHost = useMemo<MarksHost>(() => ({ ...host, descriptionDrafts: newDescriptionDrafts() }), [grid?.model()]);
  const only = selection.length === 1 ? selection[0] : null;
  if (only === 'model:') return <ModelPlan />;
  if (only?.startsWith('measure:')) return <MarksHostContext.Provider value={marksHost}><MeasureMarksPanel key={only} reference={only} requestedMark={requestedMark} /></MarksHostContext.Provider>;
  return null;
}

const slot = document.createElement('div');
slot.className = 'mk-grid';
slot.setAttribute('data-testid', 'grid-marks');
const root = createRoot(slot);
const render = () => {
  let selection: string[] = [];
  try { selection = key ? JSON.parse(key) as string[] : []; } catch { selection = []; }
  root.render(<MarksHostContext.Provider value={host}><GridMarks selection={selection} /></MarksHostContext.Provider>);
};

window.addEventListener('message', event => {
  const m = event.data as { type?: string; id?: number; result?: unknown; error?: string; key?: string; markRequest?: { name: string; id: number }; payload?: unknown };
  if (m?.type === 'marksRpcResult' && typeof m.id === 'number') {
    const call = waiting.get(m.id); waiting.delete(m.id);
    if (call) m.error != null ? call.reject(new Error(m.error)) : call.resolve(m.result);
  } else if (m?.type === 'checkState') {
    // The engine's check state push: the card here shows the newest version at once.
    pushed.forEach(fn => { try { fn(m.payload); } catch { /* one panel cannot stop another */ } });
  } else if (m?.type === 'didChange') {
    // A model change (an Undo of a kept Description) reads the Description again before the slower property reload (G1 a).
    changed.forEach(fn => { try { fn(); } catch { /* one panel cannot stop another */ } });
    void advice.changed();
  } else if (m?.type === 'load') {
    requestedMark = m.markRequest;
    render();
    // The provider loads the same object again after every model change, so its marks are read again then.
    if (previous === key) { changed.forEach(fn => { try { fn(); } catch { /* one panel cannot stop another */ } }); void advice.changed(); }
  }
});
// The grid replaces its boxes on every render; put the marks back under the header each time.
// propgrid.js handles each message first, so the selection is already the new one here.
window.addEventListener('semanticus-marks-load', () => {
  previous = key; key = grid?.key() ?? null;
  render();
  const header = document.querySelector('#root .hdr');
  if (header && header.nextSibling !== slot) header.after(slot);
});
