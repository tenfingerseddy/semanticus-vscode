import { useEffect, useRef, useState, type CSSProperties, type ReactNode } from 'react';
import { rpc } from './bridge';
import { useConnection } from './connection';
import { ObjectBrowser, type BrowserNode } from './objectbrowser';
import {
  authoringRefusal, limitedEvidenceNote, reconcileSourceFields, sourceAddedSinceAdd, sqlSourceBinding,
  type SqlSourceBinding,
} from './tests-model';
import { useSurfaceEscape } from './surface-escape';

// ===================================================================================================
// The New check drawer (the approved redesign, 2026-09-15).
//
// Kind FIRST, then the thing and the answer you trust. Everything a person must DECIDE is visible;
// only tuning sits behind Advanced. That split is not a taste call: it is the engine's own
// ReconcileContract (cp/tests-engine-b), where `measureRef`, `sql` and `blankPolicy` are Required and
// `groupBy` is "required for a verdict", meaning a check without it can never do better than could not
// check. Anything the engine calls Optional is tuning and is safe to collapse.
//
// There is no tableRowCount saved-test kind and there must not be one: for a table count the MAPPING is
// the check, so this drawer writes set_table_source_mapping and Map source on the page opens the same form.
// ===================================================================================================

export type AuthorShape = 'measureValue' | 'measureReconcile' | 'tableRowCount';
// A caller can open the drawer AT one setting. 'blank' lands on the blank-meaning choice, which is the
// setting a "nothing verifiable" result sends the person back to.
export type AuthorFocus = 'blank' | undefined;

export interface TestDefinitionW {
  id: string; kind: string; title: string; targetTag?: string; targetIdentity?: string;
  targetRef?: string; paramsJson?: string; enabled: boolean; createdBy?: string; createdWhen?: string;
  // A RECORD of the Tests and queries connection the check was agreed against. The engine stamps it at save.
  // It never pins where the check runs: a run always uses whichever connection is current.
  authoredAgainst?: string; authoredAgainstLabel?: string;
}
export interface MeasureRowW { ref: string; name: string; table: string; expression?: string }
export interface ReconcileParamsW {
  measureRef?: string; groupBy?: string[]; sql?: string; sqlGrandTotal?: string; filterDax?: string;
  toleranceAbsolute?: number; toleranceRelative?: number; blankPolicy?: string; maxRows?: number;
  // cp/tests-engine-b: a check names its source by STABLE ID. A rename never breaks it. The four inline
  // endpoint fields stay on the wire so every check saved before named sources keeps running unchanged,
  // but nothing in this drawer writes them any more.
  sqlSourceId?: string; sqlSourceName?: string;
  server?: string; database?: string; authMode?: string; tenantId?: string;
}
export interface MeasureValueParamsW {
  measureRef?: string; expectedValue?: string; filterColumn?: string; filterValue?: string;
  filterDax?: string; expressionDax?: string; provenance?: string; toleranceAbsolute?: number; toleranceRelative?: number;
}
export interface SqlSourceW { id: string; name: string; server?: string; database?: string; authMode?: string; tenantId?: string }
interface TableMappingRowW {
  modelTable: string; source: string; sqlSourceId?: string; sqlSourceName?: string;
  schema?: string; entity?: string; canCount: boolean; reason?: string | null; editable: boolean;
}
interface TableMappingListW { rows: TableMappingRowW[]; note?: string }

const inp: CSSProperties = { background: 'var(--sem-surface-2)', color: 'var(--sem-fg)', borderColor: 'var(--sem-border)' };

// The relative tolerance is STORED as a fraction (the engine's unit) and TYPED as a percent, so the box can
// be labelled "Relative %" and read the same way as the saved-check summary, which already prints a percent.
// An UNTOUCHED field returns the exact stored fraction: percent conversion is not bit-exact both ways for a
// value like 1e-9, and a display unit must never quietly re-round a saved contract.
const percentText = (fraction: number) => { const percent = fraction * 100; return percent === 0 ? '0' : String(Number(percent.toPrecision(12))); };
function useRelativePercent(stored: number): [string, (next: string) => void, number] {
  const [start] = useState(() => ({ fraction: stored, text: percentText(stored) }));
  const [text, setText] = useState(start.text);
  return [text, setText, text === start.text ? start.fraction : (Number(text) || 0) / 100];
}

// Check definitions are sidecar data rather than model edits, so saveTest/deleteTest do not raise model/didChange.
// This local signal keeps the mounted Tests view in sync when DAX Lab saves a check through the same webview.
const testSuiteListeners = new Set<() => void>();
export function onTestSuiteChanged(fn: () => void): () => void {
  testSuiteListeners.add(fn);
  return () => { testSuiteListeners.delete(fn); };
}
export function announceTestSuiteChanged(): void {
  testSuiteListeners.forEach((fn) => { try { fn(); } catch { /* isolate one view */ } });
}

// A measure check compares ONE number, so the drawer has to know whether a piece of DAX even promises one.
// A bare expression is a scalar the engine wraps in ROW(), so it is one cell. A whole query is one cell only when
// it is a single EVALUATE ROW( ) holding exactly one name and value pair with nothing after it; SUMMARIZECOLUMNS,
// TOPN, a table, a second pair or a trailing ORDER BY all return a table. Astra (2026-09-14) was handed a grouped
// visual query as the formula a trusted-answer check would ask, and the mock acknowledged the save.
// This is the EARLY warning on the text. The hard stop is the engine's own shape gate on the real RESULT
// (LocalEngine.MeasureValueShapeProblem), which is what actually withholds a verdict.
export function daxReturnsOneCell(dax: string): boolean {
  const text = (dax ?? '').trim();
  if (!text) return true;
  if (!/^(EVALUATE|DEFINE)\b/i.test(text)) return true;
  // One walk, aware of "string literals", 'quoted names' and [bracketed names], so a comma or the word EVALUATE
  // sitting inside a name is read as text and never as structure.
  const walk = (from: number, want: 'evaluate' | 'close') => {
    let depth = 0;
    let close = '';
    let commas = 0;
    for (let i = from; i < text.length; i++) {
      const c = text[i];
      if (close) {
        if (c === close) { if (close === '"' && text[i + 1] === '"') i++; else close = ''; }
        continue;
      }
      if (c === '"' || c === "'") { close = c; continue; }
      if (c === '[') { close = ']'; continue; }
      if (c === '(') { depth++; continue; }
      if (c === ')') {
        if (want === 'close' && depth === 0) return { at: i, commas };
        depth--;
        continue;
      }
      if (want === 'close' && depth === 0 && c === ',') { commas++; continue; }
      if (want === 'evaluate' && depth === 0 && /[A-Za-z_]/.test(c)) {
        const word = /^[A-Za-z_]+/.exec(text.slice(i));
        if (word) {
          if (word[0].toUpperCase() === 'EVALUATE') return { at: i + word[0].length, commas };
          i += word[0].length - 1;
        }
      }
    }
    return null;
  };
  const after = walk(0, 'evaluate');
  if (!after) return false;
  const head = /^\s*ROW\s*\(/i.exec(text.slice(after.at));
  if (!head) return false;
  const closed = walk(after.at + head[0].length, 'close');
  if (!closed) return false;
  if (text.slice(closed.at + 1).trim() !== '') return false;
  return closed.commas === 1;
}

// A saved filter and a whole EVALUATE query used to be sent together with nothing said about either. The engine
// ran the query as written and the year filter vanished, so a check could Pass or Fail on the wrong scope
// (Astra, 2026-09-14). The engine now scopes a single EVALUATE with CALCULATETABLE and refuses a query it cannot
// scope. This is the SAME rule said early, in the drawer, so nobody saves a filter that will be refused without
// being told. LocalEngine.BuildMeasureValueDax is the one that decides for real.
export function measureFilterNote(expressionDax: string, filterText: string): { tone: 'ok' | 'warn'; text: string } | null {
  const query = (expressionDax ?? '').trim();
  const filter = (filterText ?? '').trim();
  if (!query || !filter) return null;
  // Built from char codes so no backslash escape can become a raw control byte in this file.
  const NEWLINE = String.fromCharCode(10);
  const DOUBLE = String.fromCharCode(34);
  const SINGLE = String.fromCharCode(39);
  // The bare words that sit OUTSIDE every bracket, string, quoted name and comment, at paren depth zero.
  // Structure only lives out here, so EVALUATE inside ROW("EVALUATE", ...) is read as the text it is.
  const words: string[] = [];
  let depth = 0;
  for (let i = 0; i < query.length; i++) {
    const c = query[i];
    const two = query.slice(i, i + 2);
    if (two === '//' || two === '--') { while (i < query.length && query[i] !== NEWLINE) i++; continue; }
    if (two === '/*') { i += 2; while (i + 1 < query.length && query.slice(i, i + 2) !== '*/') i++; i++; continue; }
    if (c === DOUBLE || c === SINGLE) { const close = c; i++; while (i < query.length && !(query[i] === close && query[i + 1] !== close)) { if (query[i] === close) i++; i++; } continue; }
    if (c === '[') { while (i < query.length && query[i] !== ']') i++; continue; }
    if (c === '(') { depth++; continue; }
    if (c === ')') { if (depth > 0) depth--; continue; }
    if (depth === 0 && /[A-Za-z_]/.test(c)) {
      const word = /^[A-Za-z0-9_]+/.exec(query.slice(i));
      if (word) { words.push(word[0].toUpperCase()); i += word[0].length - 1; }
    }
  }
  if (words[0] !== 'EVALUATE' && words[0] !== 'DEFINE') return null;
  const why = words[0] === 'DEFINE' ? 'your query starts with DEFINE'
    : words.filter((w) => w === 'EVALUATE').length > 1 ? 'your query has more than one EVALUATE'
      : words.includes('ORDER') ? 'your query ends with ORDER BY'
        : words.includes('START') ? 'your query uses START AT'
          : null;
  if (!why) return { tone: 'ok', text: 'Applied around your query. Your query runs inside this filter.' };
  return {
    tone: 'warn',
    // The advice must be something that actually works: a query in one of these shapes is never run as a
    // trusted-answer check, with or without a filter, so taking the filter off would not help (Astra, P3).
    text: 'This check cannot run, because ' + why + '. A trusted-answer check needs one value. '
      + 'Choose Test the measure as saved, or write the query as one value, such as CALCULATE([Total Sales], \'Date\'[Year] = 2024).',
  };
}

const KINDS: { shape: AuthorShape; label: string; hint: string }[] = [
  { shape: 'measureValue', label: 'Trusted answer', hint: 'A number you know is right' },
  { shape: 'measureReconcile', label: 'Compare with source', hint: 'The same number from SQL' },
  { shape: 'tableRowCount', label: 'Table row count', hint: 'Model rows against source rows' },
];

export function AuthorDrawer({
  shape, editing, focus, table, latestSources, onClose, onSaved, onAddSqlSource,
}: {
  shape: AuthorShape; editing?: TestDefinitionW | null; focus?: AuthorFocus; table?: string;
  latestSources?: SqlSourceW[] | null;
  onClose: () => void; onSaved: () => void; onAddSqlSource?: () => void;
}) {
  const [kind, setKind] = useState<AuthorShape>(shape);
  const [measures, setMeasures] = useState<MeasureRowW[]>([]);
  const [sources, setSources] = useState<SqlSourceW[]>([]);
  const [sourcesLoaded, setSourcesLoaded] = useState(false);
  // The id the picker should adopt, when the hub came back with exactly one new record.
  const [adopt, setAdopt] = useState<string | null>(null);
  // The source list AS IT STOOD when the person chose "Add a SQL source...". Null means they never did,
  // and nothing may be adopted. This is the P1 fix: the drawer used to compare against its own empty
  // initial state, so its FIRST load of the list looked like a source appearing while the hub was open,
  // and one saved source then replaced an existing check's binding (Astra, 2026-09-15).
  const addBaseline = useRef<string[] | null>(null);
  const [busy, setBusy] = useState(false);
  const [err, setErr] = useState<string | null>(null);
  const [tryNote, setTryNote] = useState<string | null>(null);
  const locked = !!editing?.id || table != null;
  useEffect(() => { rpc<MeasureRowW[]>('listMeasures').then(setMeasures).catch(() => undefined); }, []);
  useEffect(() => {
    rpc<SqlSourceW[]>('listSqlSources')
      .then((list) => { setSources(list); setSourcesLoaded(true); })
      .catch(() => { setSources([]); setSourcesLoaded(true); });
  }, []);
  // The page re-reads the sources when the hub closes and hands the new list down. The drawer stays put
  // through all of it; only the picker changes, and only when the person went to add a source and exactly
  // one came back.
  useEffect(() => {
    if (!latestSources) return;
    setSources(latestSources);
    setSourcesLoaded(true);
    const baseline = addBaseline.current;
    if (!baseline) return;
    setAdopt(sourceAddedSinceAdd(baseline, latestSources.map((s) => s.id)));
    addBaseline.current = null;
  }, [latestSources]);
  // Escape belongs to whatever is on TOP. Opening Connections from inside a half-finished check and
  // pressing Escape used to close both, throwing the check away to save a source for it.
  useSurfaceEscape(onClose);
  // Taking the baseline is the FIRST thing that happens when Add is chosen, before the hub can answer.
  const beginAddSqlSource = () => {
    addBaseline.current = sources.map((s) => s.id);
    onAddSqlSource?.();
  };

  return (
    <aside className="fixed top-0 right-0 z-20 flex h-full w-[440px] max-w-[100vw] flex-col border-l" style={{ background: 'var(--sem-surface)', borderColor: 'var(--sem-border)' }}>
      <div className="flex items-start justify-between gap-3 border-b px-4 py-3" style={{ borderColor: 'var(--sem-border)' }}>
        <div>
          <div className="text-[10px] font-semibold uppercase tracking-[0.07em]" style={{ color: 'var(--sem-muted)' }}>{editing?.id ? 'Edit' : table ? 'Map source' : 'New check'}</div>
          <h2 className="m-0 mt-1 text-[14px] font-semibold">{KINDS.find((k) => k.shape === kind)?.label ?? 'New check'}</h2>
        </div>
        <button type="button" onClick={onClose} className="text-[12px]" style={{ color: 'var(--sem-muted)' }}>Close</button>
      </div>
      <div className="min-h-0 flex-1 overflow-auto px-4 py-3">
        {err && <div className="mb-3 rounded-md border px-3 py-2 text-[12px]" style={{ color: 'var(--sem-bad)', borderColor: 'var(--sem-bad)' }}>{err}</div>}
        {tryNote && <div className="mb-3 rounded-md border px-3 py-2 text-[12px]" style={{ color: 'var(--sem-muted)', borderColor: 'var(--sem-border)' }}>{tryNote}</div>}
        {!locked && <KindChooser kind={kind} onPick={setKind} />}
        {kind === 'measureValue' && <TrustedAnswerForm measures={measures} editing={editing} busy={busy} setBusy={setBusy} setErr={setErr} setTryNote={setTryNote} onSaved={onSaved} onClose={onClose} />}
        {kind === 'measureReconcile' && <CompareWithSourceForm measures={measures} sources={sources} sourcesLoaded={sourcesLoaded} adopt={adopt} editing={editing} focus={focus} busy={busy} setBusy={setBusy} setErr={setErr} setTryNote={setTryNote} onSaved={onSaved} onClose={onClose} onAddSqlSource={beginAddSqlSource} />}
        {kind === 'tableRowCount' && <TableRowCountForm sources={sources} adopt={adopt} table={table} busy={busy} setBusy={setBusy} setErr={setErr} onSaved={onSaved} onClose={onClose} onAddSqlSource={beginAddSqlSource} />}
      </div>
    </aside>
  );
}

// The kind comes FIRST, and each one is named by what you are doing, not by the engine's word for it.
function KindChooser({ kind, onPick }: { kind: AuthorShape; onPick: (shape: AuthorShape) => void }) {
  return (
    <div className="mb-3.5 grid gap-1.5" style={{ gridTemplateColumns: 'repeat(3,minmax(0,1fr))' }} role="radiogroup" aria-label="What kind of check">
      {KINDS.map((item) => {
        const on = item.shape === kind;
        return <button key={item.shape} type="button" role="radio" aria-checked={on} onClick={() => onPick(item.shape)}
          className="rounded-lg border p-2 text-left"
          style={{ borderColor: on ? 'var(--sem-accent)' : 'var(--sem-border)', background: on ? 'var(--sem-accent-soft)' : 'var(--sem-surface-2)' }}>
          <span className="block text-[12px] font-semibold">{item.label}</span>
          <span className="block text-[11px]" style={{ color: 'var(--sem-muted)' }}>{item.hint}</span>
        </button>;
      })}
    </div>
  );
}

// The three ways to start a check. It is a menu button, and it behaves like every other menu in the app:
// Escape closes it and hands focus back, a click outside closes it, assistive tech is told it is a menu,
// and it hangs under the button that opened it instead of under Run next door.
export function NewCheckMenu({ onPick }: { onPick: (shape: AuthorShape) => void }) {
  const [open, setOpen] = useState(false);
  const wrap = useRef<HTMLDivElement>(null);
  const buttonRef = useRef<HTMLButtonElement>(null);
  useEffect(() => {
    if (!open) return;
    const outside = (event: MouseEvent) => { if (!wrap.current?.contains(event.target as Node)) setOpen(false); };
    const escape = (event: KeyboardEvent) => { if (event.key !== 'Escape' || !open) return; setOpen(false); buttonRef.current?.focus(); };
    window.addEventListener('mousedown', outside); window.addEventListener('keydown', escape);
    return () => { window.removeEventListener('mousedown', outside); window.removeEventListener('keydown', escape); };
  }, [open]);
  const choose = (shape: AuthorShape) => { onPick(shape); setOpen(false); };
  return (
    <div ref={wrap} className="relative">
      <button ref={buttonRef} type="button" aria-haspopup="menu" aria-expanded={open}
        className="inline-flex h-7 items-center rounded-md border px-3 text-[12px] font-semibold" onClick={() => setOpen((v) => !v)}
        style={{ background: 'var(--sem-surface-2)', borderColor: 'var(--sem-border)' }}>New check ▾</button>
      {open && (
        <div role="menu" aria-label="New check" className="absolute left-0 z-20 mt-1 w-[280px] rounded-md border p-1.5" style={{ background: 'var(--sem-surface)', borderColor: 'var(--sem-border)' }}>
          {KINDS.map((item) => <Pick key={item.shape} label={item.label} hint={item.hint} onClick={() => choose(item.shape)} />)}
        </div>
      )}
    </div>
  );
}

function Pick({ label, hint, onClick }: { label: string; hint: string; onClick: () => void }) {
  return (
    <button type="button" role="menuitem" onClick={onClick} className="mb-1 w-full rounded-md px-2 py-1.5 text-left hover:bg-[var(--sem-surface-2)]">
      <span className="block text-[12px] font-semibold">{label}</span>
      <span className="block text-[11px]" style={{ color: 'var(--sem-muted)' }}>{hint}</span>
    </button>
  );
}

function Field({ label, hint, children }: { label: string; hint?: string; children: ReactNode }) {
  return (
    <label className="mb-3 block">
      <span className="mb-1 block text-[11px] font-semibold">{label}</span>
      {children}
      {hint && <span className="mt-1 block text-[10px]" style={{ color: 'var(--sem-muted)' }}>{hint}</span>}
    </label>
  );
}

// ONE disclosure, and it only ever holds tuning. Nothing the engine requires, and nothing that decides
// whether a verdict is reachable, is allowed in here.
function Advanced({ children, open = false, summary = 'tolerance and a complete DAX filter' }: { children: ReactNode; open?: boolean; summary?: string }) {
  return (
    <details open={open} className="mb-3 rounded-md border px-2.5 py-2" style={{ borderColor: 'var(--sem-border)' }}>
      <summary className="cursor-pointer text-[11px] font-semibold">Advanced <span className="font-normal" style={{ color: 'var(--sem-muted)' }}>{summary}</span></summary>
      <div className="mt-2">{children}</div>
    </details>
  );
}

// What it costs, said BEFORE the last click. Astra found both saving and recording carry conditions that
// only announced themselves after the person had finished the form.
function SaveRequirement({ kind }: { kind: AuthorShape }) {
  return (
    <div className="mt-3 rounded-md border px-2.5 py-2 text-[11px]" style={{ borderColor: 'var(--sem-border)', background: 'var(--sem-surface-2)', color: 'var(--sem-muted)' }}>
      {kind === 'tableRowCount'
        ? 'The mapping is kept in a small file beside this model.'
        : 'The check is kept in a small file beside this model. Try it now saves nothing.'}
    </div>
  );
}

function MeasureSelect({ measures, value, onChange }: { measures: MeasureRowW[]; value: string; onChange: (v: string) => void }) {
  return (
    <select value={value} onChange={(e) => onChange(e.target.value)} className="w-full rounded-md border px-2 py-1.5 text-[12px]" style={inp}>
      <option value="">Pick a calculation</option>
      {measures.map((m) => <option key={m.ref} value={m.ref}>{m.name}</option>)}
    </select>
  );
}

// Saved once, named, chosen from a list. The last option opens the Connections hub rather than growing a
// second place to type an endpoint, which is the repeated setup Astra measured in journey B.
function SqlSourceSelect({ sources, value, onChange, onAdd }: {
  sources: SqlSourceW[]; value: string; onChange: (id: string) => void; onAdd?: () => void;
}) {
  const ADD = '__add__';
  return (
    <>
      <select value={value} onChange={(e) => { if (e.target.value === ADD) { onAdd?.(); return; } onChange(e.target.value); }}
        aria-label="SQL source" className="w-full rounded-md border px-2 py-1.5 text-[12px]" style={inp}>
        <option value="">Pick a SQL source</option>
        {sources.map((s) => <option key={s.id} value={s.id}>{s.name}</option>)}
        <option value={ADD}>Add a SQL source…</option>
      </select>
      {sources.length === 0 && <span className="mt-1 block text-[10px]" style={{ color: 'var(--sem-muted)' }}>
        No SQL sources are saved yet. Add one and every check and table mapping can use it.
      </span>}
    </>
  );
}

// Which connection the check is being AGREED against. A saved check keeps the one it was authored on as a
// record, so evidence can later say "authored against X, ran against Y" rather than implying they were the
// same. It is a record, never a pin: the run still uses whichever connection is current.
function QueryTargetNote({ editing }: { editing?: TestDefinitionW | null }) {
  const { conn } = useConnection();
  const current = conn?.connected
    ? `${conn.database || 'the connected model'}${conn.dataSource ? ` on ${conn.dataSource}` : ''}`
    : 'No Tests and queries connection is selected';
  const authored = editing?.authoredAgainstLabel;
  return (
    <div className="mb-3 rounded-md border px-3 py-2 text-[11px]" style={{ background: 'var(--sem-surface-2)', borderColor: 'var(--sem-border)' }}>
      <strong>Authored against {authored || current}</strong>
      <div className="mt-0.5" style={{ color: 'var(--sem-muted)' }}>
        {authored && authored !== current
          ? `You are now on ${current}. The check keeps the connection it was written on as a note. It still runs against whichever Tests and queries connection is current.`
          : 'This is written down with the check. It still runs against whichever Tests and queries connection is current, so you can check the same thing somewhere else.'}
      </div>
    </div>
  );
}

function contextLabel(value: string): string {
  const engineRef = /^column:([^/]+)\/(.+)$/.exec(value);
  if (engineRef) return `${engineRef[1]} · ${engineRef[2]}`;
  const daxRef = /^'((?:''|[^'])+)'\[(.+)\]$/.exec(value);
  if (daxRef) return `${daxRef[1].replace(/''/g, "'")} · ${daxRef[2].replace(/]]/g, ']')}`;
  return value;
}

function ContextFields({ value, onChange }: { value: string[]; onChange: (next: string[]) => void }) {
  const [choosing, setChoosing] = useState(false);
  const [manual, setManual] = useState('');
  const add = (refs: string[]) => onChange([...new Set([...value, ...refs.map((ref) => ref.trim()).filter(Boolean)])]);
  const addNodes = (nodes: BrowserNode[]) => add(nodes.map((node) => node.ref));
  return (
    <div className="rounded-md border p-2.5" style={{ borderColor: 'var(--sem-border)' }}>
      <div className="flex flex-wrap gap-1.5">
        {value.map((field) => <span key={field} className="inline-flex items-center gap-1 rounded-full border px-2 py-1 text-[11px]" style={{ borderColor: 'var(--sem-border)', background: 'var(--sem-surface-2)' }}>
          {contextLabel(field)}
          <button type="button" aria-label={`Remove ${contextLabel(field)}`} onClick={() => onChange(value.filter((item) => item !== field))} style={{ color: 'var(--sem-muted)' }}>×</button>
        </span>)}
        {value.length === 0 && <span className="text-[11px]" style={{ color: 'var(--sem-muted)' }}>No fields chosen yet.</span>}
      </div>
      <div className="mt-2 flex flex-wrap items-center gap-2">
        <button type="button" className="min-h-7 rounded-md border px-2.5 text-[11px] font-semibold" onClick={() => setChoosing((open) => !open)} style={{ background: 'var(--sem-surface-2)', borderColor: 'var(--sem-border)' }}>{choosing ? 'Done choosing' : 'Choose fields'}</button>
        <span className="text-[10px]" style={{ color: 'var(--sem-muted)' }}>Pick Product category, Customer region, Date year, or any fields from the model.</span>
      </div>
      {choosing && <div className="mt-2"><ObjectBrowser kinds={['column']} multiSelect dragEnabled={false} height={230}
        pickedRefs={new Set(value)} onPick={(node) => addNodes([node])} onPickMany={addNodes} emptyHint="No columns are available in this model." /></div>}
      <details className="mt-2">
        <summary className="cursor-pointer text-[10px]" style={{ color: 'var(--sem-muted)' }}>Enter a field reference</summary>
        <div className="mt-1.5 flex gap-2">
          <input value={manual} onChange={(event) => setManual(event.target.value)} placeholder="'Table'[Column]" className="min-h-7 min-w-0 flex-1 rounded-md border px-2 font-mono text-[11px]" style={inp} />
          <button type="button" disabled={!manual.trim()} className="rounded-md border px-2 text-[11px] disabled:opacity-40" onClick={() => { add([manual]); setManual(''); }} style={{ borderColor: 'var(--sem-border)' }}>Add</button>
        </div>
      </details>
    </div>
  );
}

// Two bare boxes side by side make the person guess which is which. Each one names its own unit.
function ToleranceInputs({ abs, onAbs, relPercent, onRelPercent }: { abs: string; onAbs: (v: string) => void; relPercent: string; onRelPercent: (v: string) => void }) {
  return (
    <div className="flex gap-2">
      <label className="flex-1 text-[10px]" style={{ color: 'var(--sem-muted)' }}>Absolute
        <input value={abs} onChange={(event) => onAbs(event.target.value)} aria-label="Absolute tolerance" className="mt-0.5 min-h-7 w-full rounded-md border px-2 text-[12px]" style={inp} />
      </label>
      <label className="flex-1 text-[10px]" style={{ color: 'var(--sem-muted)' }}>Relative %
        <input value={relPercent} onChange={(event) => onRelPercent(event.target.value)} aria-label="Relative tolerance in percent" className="mt-0.5 min-h-7 w-full rounded-md border px-2 text-[12px]" style={inp} />
      </label>
    </div>
  );
}

function Footer({ busy, onTry, onSave, tryLabel = 'Try it now', saveLabel = 'Save check' }: {
  busy: boolean; onTry?: () => void; onSave: () => void; tryLabel?: string; saveLabel?: string;
}) {
  return (
    <div className="mt-3 flex flex-wrap items-center gap-2">
      {onTry && <button type="button" disabled={busy} onClick={onTry} className="min-h-7 rounded-md border px-3 py-1 text-[12px]" style={{ background: 'var(--sem-surface-2)', borderColor: 'var(--sem-border)' }}>{tryLabel}</button>}
      <button type="button" disabled={busy} onClick={onSave} className="min-h-7 rounded-md border px-3 py-1 text-[12px] font-semibold" style={{ background: 'var(--sem-accent)', borderColor: 'var(--sem-accent)', color: 'var(--sem-on-accent)' }}>{saveLabel}</button>
    </div>
  );
}

// ---------------------------------------------------------------------------------------------------
// Trusted answer. The calculation, the number, and where it came from. No grouping: this kind compares
// ONE value against ONE expected answer, and the engine has no per-group expected answers to compare to.
// No timing budget either: the timing pass only ever receives reconcile plans, so the old field promised
// a measurement this kind never gets (Astra, journey A).
// ---------------------------------------------------------------------------------------------------
function TrustedAnswerForm({ measures, editing, busy, setBusy, setErr, setTryNote, onSaved, onClose }: {
  measures: MeasureRowW[]; editing?: TestDefinitionW | null; busy: boolean;
  setBusy: (v: boolean) => void; setErr: (v: string | null) => void; setTryNote: (v: string | null) => void;
  onSaved: () => void; onClose: () => void;
}) {
  const initial = parseValue(editing);
  const [measureRef, setMeasureRef] = useState(editing?.targetRef || initial.measureRef || '');
  const [filterColumn, setFilterColumn] = useState(initial.filterColumn || '');
  const [filterValue, setFilterValue] = useState(initial.filterValue || '');
  const [expected, setExpected] = useState(initial.expectedValue || '');
  const [provenance, setProvenance] = useState(initial.provenance || '');
  const [filterDax, setFilterDax] = useState(initial.filterDax || '');
  // The DAX Lab hands over an EDITED formula when the person changed it. They choose which one this check is
  // about; picking the saved measure drops the expression rather than quietly testing text the model lacks.
  const handedExpression = initial.expressionDax || '';
  // A handed-over TABLE query is never preselected as the formula this check asks. A measure check can only
  // compare one number, so preselecting it staged a check that could never answer the question on screen.
  const handedIsOneCell = daxReturnsOneCell(handedExpression);
  const [expressionDax, setExpressionDax] = useState(handedExpression);
  const [testExpression, setTestExpression] = useState(!!handedExpression && handedIsOneCell);
  const [abs, setAbs] = useState(String(initial.toleranceAbsolute ?? 1e-6));
  const [relPercent, setRelPercent, relFraction] = useRelativePercent(initial.toleranceRelative ?? 1e-9);
  // A second explicit hand-off changes only the calculation. The rest of the open draft remains the person's.
  useEffect(() => { if (!editing?.id && editing?.targetRef) setMeasureRef(editing.targetRef); }, [editing?.id, editing?.targetRef]);
  // Say what happens to the filter BEFORE the person saves. A complete query is scoped from outside, and some
  // queries cannot be scoped at all, so saving one with a filter saves a check the engine will not run.
  const filterNote = measureFilterNote(testExpression ? expressionDax : '', filterDax || (filterColumn.trim() && filterValue.trim() ? filterColumn + ' = ' + filterValue : ''));
  const def = (): TestDefinitionW => ({
    ...editing,
    id: editing?.id || '', kind: 'measureValue', title: titleFor(measureRef, expected),
    targetRef: measureRef, enabled: editing?.enabled ?? true,
    paramsJson: JSON.stringify({
      measureRef, expectedValue: expected.trim(), filterColumn: filterColumn.trim() || undefined,
      filterValue: filterValue.trim() || undefined, filterDax: filterDax.trim() || undefined,
      expressionDax: testExpression ? (expressionDax.trim() || undefined) : undefined,
      provenance: provenance.trim() || undefined, toleranceAbsolute: Number(abs) || 0, toleranceRelative: relFraction,
    } satisfies MeasureValueParamsW),
  });

  return (
    <>
      <Field label="Which calculation"><MeasureSelect measures={measures} value={measureRef} onChange={setMeasureRef} /></Field>
      <Field label="The number you trust" hint="Type BLANK if the right answer is no value.">
        <input value={expected} onChange={(e) => setExpected(e.target.value)} className="min-h-7 w-full rounded-md border px-2 text-[12px]" style={inp} />
      </Field>
      <Field label="Only where (optional)" hint="Example: Year is 2024.">
        <span className="flex gap-2">
          <input value={filterColumn} onChange={(e) => setFilterColumn(e.target.value)} placeholder="Column" className="min-h-7 flex-1 rounded-md border px-2 text-[12px]" style={inp} />
          <input value={filterValue} onChange={(e) => setFilterValue(e.target.value)} placeholder="Value" className="min-h-7 flex-1 rounded-md border px-2 text-[12px]" style={inp} />
        </span>
      </Field>
      <Field label="Where this number came from (optional)">
        <input value={provenance} onChange={(e) => setProvenance(e.target.value)} placeholder="Finance month-end pack, July" className="min-h-7 w-full rounded-md border px-2 text-[12px]" style={inp} />
      </Field>
      {handedExpression && (
        <div className="mb-3 rounded-md border p-2.5" style={{ borderColor: 'var(--sem-border)' }}>
          <div className="mb-1 text-[11px] font-semibold">Which formula is this check about?</div>
          <label className="block text-[12px]"><input type="radio" name="whichFormula" checked={!testExpression} onChange={() => setTestExpression(false)} /> Test the measure as saved</label>
          <label className="block text-[12px]"><input type="radio" name="whichFormula" checked={testExpression} onChange={() => setTestExpression(true)} /> Test this expression</label>
          <div className="mt-1 text-[10px]" style={{ color: 'var(--sem-muted)' }}>You changed the formula in DAX Lab. The model still holds the saved one, so say which one this check should ask.</div>
          {!handedIsOneCell && <div className="mt-1.5 rounded-md border px-2 py-1.5 text-[10px]" style={{ borderColor: 'var(--sem-warn)', color: 'var(--sem-warn)' }}>Your DAX Lab query returns a table, not one number. A measure check compares one number, so it cannot ask this as it stands. Leave this check on the measure as saved, or pick Test this expression and replace it with a single value.</div>}
          {testExpression && <textarea value={expressionDax} onChange={(event) => setExpressionDax(event.target.value)} rows={4} spellCheck={false} aria-label="The expression to test" className="mt-2 w-full rounded-md border px-2 py-1 font-mono text-[11px]" style={inp} />}
        </div>
      )}
      <QueryTargetNote editing={editing} />
      {/* Filters carried over from a visual open by default: a check that silently hid them would ask a
          different question from the one the person was looking at. */}
      <Advanced open={!!initial.filterDax}>
        <Field label="Tolerance" hint="The answer passes when it is within the absolute or relative amount you set.">
          <ToleranceInputs abs={abs} onAbs={setAbs} relPercent={relPercent} onRelPercent={setRelPercent} />
        </Field>
        <Field label="Use a complete DAX filter" hint="Use this for more than one condition or fields from different tables. It takes priority over the simple Column and Value above.">
          <textarea value={filterDax} onChange={(event) => setFilterDax(event.target.value)} rows={3} placeholder={'\'Product\'[Category] = "Bikes", \'Date\'[Year] = 2026'} spellCheck={false} className="w-full rounded-md border px-2 py-1 font-mono text-[11px]" style={inp} />
        </Field>
        {!!initial.filterDax && <div className="text-[10px]" style={{ color: 'var(--sem-muted)' }}>Carried over from the filters on your visual. Change them here if this check should ask something wider.</div>}
      </Advanced>
      {filterNote && (
        <div className="mb-3 rounded-md border px-2 py-1.5 text-[10px]" style={filterNote.tone === 'warn'
          ? { borderColor: 'var(--sem-warn)', color: 'var(--sem-warn)' }
          : { borderColor: 'var(--sem-border)', color: 'var(--sem-muted)' }}>{filterNote.text}</div>
      )}
      <SaveRequirement kind="measureValue" />
      <Footer busy={busy} onTry={() => void tryDef(def(), setBusy, setErr, setTryNote)} onSave={() => void saveDef(def(), setBusy, setErr, onSaved, onClose)} />
    </>
  );
}

// ---------------------------------------------------------------------------------------------------
// Compare with source. Everything the engine REQUIRES is visible, and so is the one thing that decides
// whether a verdict is even reachable.
// ---------------------------------------------------------------------------------------------------
function CompareWithSourceForm({ measures, sources, sourcesLoaded, adopt, editing, focus, busy, setBusy, setErr, setTryNote, onSaved, onClose, onAddSqlSource }: {
  measures: MeasureRowW[]; sources: SqlSourceW[]; sourcesLoaded: boolean; adopt?: string | null; editing?: TestDefinitionW | null; focus?: AuthorFocus; busy: boolean;
  setBusy: (v: boolean) => void; setErr: (v: string | null) => void; setTryNote: (v: string | null) => void;
  onSaved: () => void; onClose: () => void; onAddSqlSource?: () => void;
}) {
  const initial = parseReconcile(editing);
  const [measureRef, setMeasureRef] = useState(editing?.targetRef || initial.measureRef || '');
  // What this check is ALREADY bound to, decided by one rule rather than by whatever the list happens to
  // hold. `picked` is the person's own change; until they make one the binding stands as it was saved.
  const [picked, setPicked] = useState<string | null>(null);
  const binding: SqlSourceBinding = sqlSourceBinding({
    savedId: initial.sqlSourceId,
    savedName: initial.sqlSourceName,
    inlineServer: initial.server,
    inlineDatabase: initial.database,
    available: sources.map((s) => ({ id: s.id, name: s.name })),
    loaded: sourcesLoaded,
  });
  const sqlSourceId = picked ?? binding.select;
  const setSqlSourceId = (id: string) => setPicked(id);
  const [groupBy, setGroupBy] = useState<string[]>(initial.groupBy || []);
  const [sql, setSql] = useState(initial.sql || '');
  const [sqlGrandTotal, setSqlGrandTotal] = useState(initial.sqlGrandTotal || '');
  const [filterDax, setFilterDax] = useState(initial.filterDax || '');
  const [blankPolicy, setBlankPolicy] = useState(initial.blankPolicy || '');
  // A requirement is not a mistake until the person tries to save. Before that the blank-meaning line is a
  // neutral "still needed"; only an attempted Save turns it into the failure colour.
  const [saveAttempted, setSaveAttempted] = useState(false);
  const blankGroup = useRef<HTMLDivElement | null>(null);
  const [abs, setAbs] = useState(String(initial.toleranceAbsolute ?? 0));
  const [relPercent, setRelPercent, relFraction] = useRelativePercent(initial.toleranceRelative ?? 0);
  // Only ever set from a baseline the Add handler captured, so this can no longer fire on a first load.
  useEffect(() => { if (adopt) setPicked(adopt); }, [adopt]);

  const params = (): ReconcileParamsW => ({
    measureRef, sql: sql.trim(), sqlGrandTotal: sqlGrandTotal.trim() || undefined,
    groupBy, filterDax: filterDax.trim() || undefined,
    blankPolicy, toleranceAbsolute: Number(abs) || 0, toleranceRelative: relFraction,
    // The binding travels whichever button sent this, so a payload that names no source and carries an old
    // inline address cannot be built at all.
    ...reconcileSourceFields(sqlSourceId, initial.sqlSourceId, initial.sqlSourceName, sources.map((s) => ({ id: s.id, name: s.name }))),
    maxRows: initial.maxRows,
    // Carried through untouched so a check saved before named sources keeps running exactly as it did.
    server: initial.server, database: initial.database, authMode: initial.authMode, tenantId: initial.tenantId,
  });
  const def = (): TestDefinitionW => ({
    ...editing,
    id: editing?.id || '', kind: 'measureReconcile', title: titleFor(measureRef, 'SQL'),
    targetRef: measureRef, enabled: editing?.enabled ?? true, paramsJson: JSON.stringify(params()),
  });

  // Opened from "Choose what a blank means": land on that control rather than the top of the form.
  useEffect(() => {
    if (focus !== 'blank') return;
    const group = blankGroup.current;
    if (!group) return;
    group.scrollIntoView({ block: 'center' });
    group.querySelector<HTMLInputElement>('input[type="radio"]')?.focus();
  }, [focus]);

  // ONE validation. Try used to call straight through with no guard, which is how a check whose named
  // source had gone was tried against its old inline address (Astra, round two). A check that cannot be
  // saved cannot honestly be tried either, so both buttons ask the same question and show the same words.
  const refusal = () => {
    const message = authoringRefusal({ blankPolicy, selectedSourceId: sqlSourceId, binding });
    if (!message) { setErr(null); return null; }
    setErr(message);
    if (!blankPolicy) { setSaveAttempted(true); blankGroup.current?.scrollIntoView({ block: 'center' }); }
    setTryNote(null);
    return message;
  };
  const evidenceNote = limitedEvidenceNote(groupBy.length);
  return (
    <>
      <Field label="Which calculation"><MeasureSelect measures={measures} value={measureRef} onChange={setMeasureRef} /></Field>
      <Field label="SQL source" hint="Saved once in Connections, then used by any check or table mapping.">
        <SqlSourceSelect sources={sources} value={sqlSourceId} onChange={setSqlSourceId} onAdd={onAddSqlSource} />
        {binding.note && !picked && <span className="mt-1 block text-[10.5px]"
          style={{ color: binding.needsReplacement ? 'var(--sem-warn)' : 'var(--sem-muted)' }}>{binding.note}</span>}
      </Field>
      <Field label="The SQL you accept as the truth">
        <textarea value={sql} onChange={(e) => setSql(e.target.value)} rows={5} spellCheck={false} className="w-full rounded-md border px-2 py-1 font-mono text-[11px]" style={inp} />
      </Field>
      <div className="mb-3 rounded-md border px-2 py-1.5 text-[10.5px]" style={{ borderColor: 'var(--sem-border)', color: 'var(--sem-muted)' }}>
        Your SQL must use the same filter as the check. Semanticus never rewrites your query, so if the two sides are
        narrowed differently they are answering different questions.
      </div>
      <Field label="Only where (optional)" hint="A complete DAX filter for the model side. Narrow your SQL the same way.">
        <textarea value={filterDax} onChange={(event) => setFilterDax(event.target.value)} rows={2} spellCheck={false} aria-label="Only where"
          placeholder={'\'Date\'[Year] = 2026, \'Product\'[Category] = "Bikes"'} className="w-full rounded-md border px-2 py-1 font-mono text-[11px]" style={inp} />
      </Field>
      {!!initial.filterDax && <div className="mb-3 text-[10px]" style={{ color: 'var(--sem-muted)' }}>Carried over from the filters on your visual.</div>}
      <div className="mb-3 block">
        <div className="mb-1 text-[11px] font-semibold">Which groups to compare</div>
        <ContextFields value={groupBy} onChange={setGroupBy} />
        {evidenceNote
          ? <div className="mt-1.5 rounded-md border px-2 py-1.5 text-[10.5px]" style={{ borderColor: 'var(--sem-warn)', color: 'var(--sem-warn)' }}>{evidenceNote}</div>
          : <div className="mt-1 text-[10px]" style={{ color: 'var(--sem-muted)' }}>Fields can come from different tables. The check compares every combination both queries return.</div>}
      </div>
      <div className="mb-3" ref={blankGroup}>
        <div className="mb-1 text-[11px] font-semibold">A blank in the model means</div>
        {([['zero', 'Zero'], ['null', 'No value'], ['distinct', 'Its own group']] as const).map(([v, label]) => (
          <label key={v} className="mr-3 text-[12px]"><input type="radio" name="blank" checked={blankPolicy === v} onChange={() => { setBlankPolicy(v); setSaveAttempted(false); }} /> {label}</label>
        ))}
        {!blankPolicy && <div className="mt-1 text-[11px]" style={{ color: saveAttempted ? 'var(--sem-bad)' : 'var(--sem-muted)' }}>Pick one before you save. There is no sensible default.</div>}
      </div>
      <QueryTargetNote editing={editing} />
      <Advanced summary="tolerance, a complete DAX filter and a separate grand total query">
        <Field label="Tolerance">
          <ToleranceInputs abs={abs} onAbs={setAbs} relPercent={relPercent} onRelPercent={setRelPercent} />
        </Field>
        <Field label="Grand total SQL (optional)" hint="Use this when the grand total needs its own query.">
          <textarea value={sqlGrandTotal} onChange={(e) => setSqlGrandTotal(e.target.value)} rows={2} spellCheck={false} className="w-full rounded-md border px-2 py-1 font-mono text-[11px]" style={inp} />
        </Field>
      </Advanced>
      <SaveRequirement kind="measureReconcile" />
      <Footer busy={busy}
        onTry={() => { if (refusal()) return; void tryDef(def(), setBusy, setErr, setTryNote); }}
        onSave={() => { if (refusal()) return; void saveDef(def(), setBusy, setErr, onSaved, onClose); }} />
    </>
  );
}

// ---------------------------------------------------------------------------------------------------
// Table row count. ONE identity with Map source on the page: for a table count the mapping IS the check,
// so there is no saved-test kind to send and none is sent.
// ---------------------------------------------------------------------------------------------------
function TableRowCountForm({ sources, adopt, table, busy, setBusy, setErr, onSaved, onClose, onAddSqlSource }: {
  sources: SqlSourceW[]; adopt?: string | null; table?: string; busy: boolean;
  setBusy: (v: boolean) => void; setErr: (v: string | null) => void; onSaved: () => void; onClose: () => void;
  onAddSqlSource?: () => void;
}) {
  const [rows, setRows] = useState<TableMappingRowW[]>([]);
  const [modelTable, setModelTable] = useState(table || '');
  const [sqlSourceId, setSqlSourceId] = useState('');
  const [schema, setSchema] = useState('');
  const [entity, setEntity] = useState('');
  const [loaded, setLoaded] = useState(false);
  useEffect(() => {
    rpc<TableMappingListW>('listTableMappings')
      .then((list) => { setRows(list.rows ?? []); setLoaded(true); })
      .catch(() => { setRows([]); setLoaded(true); });
  }, []);
  // Filling in what the table already has is not a guess: it is the row the engine just sent for it.
  useEffect(() => {
    const row = rows.find((r) => r.modelTable === modelTable);
    if (!row) return;
    setSqlSourceId(row.sqlSourceId || '');
    setSchema(row.schema || '');
    setEntity(row.entity || '');
  }, [modelTable, rows]);
  useEffect(() => { if (adopt) setSqlSourceId(adopt); }, [adopt]);

  const editableRows = rows.filter((row) => row.editable);
  const chosen = rows.find((r) => r.modelTable === modelTable);
  // A mapping could be added from here and removed only through the agent door, while the Connections hub
  // told people to remove their mappings before removing a source (Astra, finding 7). Same operation, both
  // doors: clear_table_source_mapping. Removing it is not removing the table, so the wording says what is
  // left behind.
  const removeMapping = () => {
    if (!chosen?.sqlSourceId) return;
    setBusy(true); setErr(null);
    rpc<boolean>('clearTableSourceMapping', modelTable)
      .then(() => { announceTestSuiteChanged(); onSaved(); onClose(); })
      .catch((e: unknown) => setErr(e instanceof Error ? e.message : String(e)))
      .finally(() => setBusy(false));
  };
  const save = () => {
    if (!modelTable) { setErr('Pick the table to count.'); return; }
    if (!sqlSourceId) { setErr('Pick the SQL source that holds this table, or add one.'); return; }
    if (!schema.trim() || !entity.trim()) { setErr('Say which schema and which table in the source to count.'); return; }
    setBusy(true); setErr(null);
    rpc('setTableSourceMapping', modelTable, sqlSourceId, schema.trim(), entity.trim())
      .then(() => { announceTestSuiteChanged(); onSaved(); onClose(); })
      .catch((e: unknown) => setErr(e instanceof Error ? e.message : String(e)))
      .finally(() => setBusy(false));
  };
  return (
    <>
      <Field label="Which table" hint="The model table whose rows you want counted.">
        <select value={modelTable} onChange={(e) => setModelTable(e.target.value)} className="w-full rounded-md border px-2 py-1.5 text-[12px]" style={inp}>
          <option value="">{loaded ? 'Pick a table' : 'Loading the tables…'}</option>
          {editableRows.map((row) => <option key={row.modelTable} value={row.modelTable}>{row.modelTable}</option>)}
        </select>
      </Field>
      {chosen && !chosen.canCount && chosen.reason && <div className="mb-3 text-[10.5px]" style={{ color: 'var(--sem-muted)' }}>Right now, {chosen.reason}.</div>}
      <Field label="SQL source" hint="Saved once in Connections, then used by any check or table mapping.">
        <SqlSourceSelect sources={sources} value={sqlSourceId} onChange={setSqlSourceId} onAdd={onAddSqlSource} />
      </Field>
      <Field label="Schema" hint="The schema in the source that holds the table.">
        <input value={schema} onChange={(e) => setSchema(e.target.value)} placeholder="sales" className="min-h-7 w-full rounded-md border px-2 text-[12px]" style={inp} />
      </Field>
      <Field label="Table in the source" hint="The table name to count in that schema.">
        <input value={entity} onChange={(e) => setEntity(e.target.value)} placeholder="orders" className="min-h-7 w-full rounded-md border px-2 text-[12px]" style={inp} />
      </Field>
      <div className="mb-3 rounded-md border px-2 py-1.5 text-[10.5px]" style={{ borderColor: 'var(--sem-border)', color: 'var(--sem-muted)' }}>
        This runs when you run everything or choose table row counts. Equal counts pass. Different counts are
        reported as Counts differ, which is not proof of missing rows on its own.
      </div>
      <SaveRequirement kind="tableRowCount" />
      <Footer busy={busy} onSave={save} saveLabel="Save mapping" />
      {chosen?.sqlSourceId && (
        <div className="mt-2.5 border-t pt-2.5" style={{ borderColor: 'var(--sem-border)' }}>
          <button type="button" disabled={busy} onClick={removeMapping}
            className="min-h-7 rounded-md border px-3 py-1 text-[12px] disabled:opacity-40"
            style={{ background: 'var(--sem-surface-2)', borderColor: 'var(--sem-bad)', color: 'var(--sem-bad)' }}>Remove mapping</button>
          <span className="mt-1 block text-[10.5px]" style={{ color: 'var(--sem-muted)' }}>
            Use the model&apos;s detected source if one exists.
          </span>
        </div>
      )}
    </>
  );
}

async function tryDef(def: TestDefinitionW, setBusy: (v: boolean) => void, setErr: (v: string | null) => void, setTryNote: (v: string | null) => void) {
  setBusy(true); setErr(null); setTryNote(null);
  try {
    const r = await rpc<{ verdict?: string; message?: string }>('tryTest', def);
    setTryNote(r.message || r.verdict || 'Tried once. Nothing was saved.');
  } catch (e) {
    const msg = e instanceof Error ? e.message : String(e);
    setTryNote(msg.toLowerCase().includes('live') || msg.toLowerCase().includes('connect')
      ? "Can't try this now: no live model to ask." : msg);
  } finally { setBusy(false); }
}

async function saveDef(def: TestDefinitionW, setBusy: (v: boolean) => void, setErr: (v: string | null) => void, onSaved: () => void, onClose: () => void) {
  if (!def.targetRef) { setErr('Pick a calculation.'); return; }
  setBusy(true); setErr(null);
  try {
    await rpc('saveTest', def);
    announceTestSuiteChanged();
    onSaved(); onClose();
  } catch (e) { setErr(e instanceof Error ? e.message : String(e)); }
  finally { setBusy(false); }
}

function parseValue(def?: TestDefinitionW | null): MeasureValueParamsW {
  if (!def?.paramsJson) return {};
  try { return JSON.parse(def.paramsJson) as MeasureValueParamsW; } catch { return {}; }
}
function parseReconcile(def?: TestDefinitionW | null): ReconcileParamsW {
  if (!def?.paramsJson) return {};
  try { return JSON.parse(def.paramsJson) as ReconcileParamsW; } catch { return {}; }
}
function titleFor(measureRef: string, expected: string) {
  const name = (measureRef || '').split('/').pop()?.replace(/^measure:/, '') || 'Calculation';
  return expected && expected !== 'SQL' ? `${name} totals ${expected}` : `${name} ties to source SQL`;
}
