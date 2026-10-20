import { useEffect, useRef, useState, type CSSProperties, type ReactNode } from 'react';
import { onActivity, rpc } from './bridge';
import { DraftMark, EditMovedBanner, sameValue, useEditBase } from './editbase';
import { draftModel, draftStore, heldDraft } from './drafts';
import { useConnection } from './connection';
import { ObjectBrowser, type BrowserNode } from './objectbrowser';
import {
  authoringRefusal, defaultSqlSourceId, distinctTitle, guessSumSql, shownNumber, limitedEvidenceNote, reconcileSourceFields, sqlSourceBinding,
  type LinkLike, type MappingLike, type SqlSourceBinding,
} from './tests-model';
import { SqlSourceAddForm, type AddedSource } from './tests-addsource';
import { useSurfaceEscape } from './surface-escape';
import { checkTitle } from './askdata-model';
import { useDaxModelContext } from './daxeditor';
import { grainWords } from './sortgrain';

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
// the check, so this drawer writes set_table_source_mapping and Choose source on the page opens the same form.
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
  budgetMs?: number | null;
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
  server?: string; database?: string;
  schema?: string; entity?: string; canCount: boolean; reason?: string | null; editable: boolean;
}
interface TableMappingListW { rows: TableMappingRowW[]; note?: string }

/** What a saved check's Save needs: the read that decides whether to write, the fields it started from for the write to name, and what to do when the engine refuses. */
export interface SaveFence { before: () => Promise<boolean>; expected: () => string | null; refused: (e: unknown) => Promise<boolean> }
/** The fields of a saved check a Save replaces, as the engine holds them. */
const testFence = (d: TestDefinitionW | null | undefined) => d ? JSON.stringify({ title: d.title ?? null, kind: d.kind ?? null, targetRef: d.targetRef ?? null, paramsJson: d.paramsJson ?? null, enabled: d.enabled ?? true, budgetMs: d.budgetMs ?? null }) : null;

const inp: CSSProperties = { background: 'var(--sem-surface-2)', color: 'var(--sem-fg)', borderColor: 'var(--sem-border)' };

// The relative tolerance is STORED as a fraction (the engine's unit) and TYPED as a percent, so the box can
// be labelled "Relative %" and read the same way as the saved-check summary, which already prints a percent.
// An UNTOUCHED field returns the exact stored fraction: percent conversion is not bit-exact both ways for a
// value like 1e-9, and a display unit must never quietly re-round a saved contract.
const percentText = (fraction: number) => { const percent = fraction * 100; return percent === 0 ? '0' : String(Number(percent.toPrecision(12))); };
const relativeFraction = (text: string, stored: number) => text === percentText(stored) ? stored : (Number(text) || 0) / 100;
// A NEW check against SQL starts on the engine's own default (TolerancePolicy.Default: relative 1e-7, absolute 1e-9). Zero and zero made an
// average or a ratio that agrees to fourteen places read as "differs" (UAT 18 Oct, round 3, R1). A saved check keeps the numbers it saved.
export const SQL_TOLERANCE_ABSOLUTE = 1e-9;
export const SQL_TOLERANCE_RELATIVE = 1e-7;

// ---------------------------------------------------------------------------------------------------
// The drafts (join 11, drafts.ts). What a person typed into a check is THEIR work, so it lives above the
// drawer and outlives it: the drawer closes when the Tests list or the route changes, and the whole page
// remounts on a restarted engine. Each form's fields are one plain value (the whole set, typed or not),
// held by the model file and a scope that names the thing: a saved check by its id, a new check by its
// kind (offered again the next time New check of that kind opens), a table mapping by its table. A draft
// ends on a save the engine took, on Discard draft, or on Use the new one; never on Close.
// ---------------------------------------------------------------------------------------------------
export type TrustedFields = {
  measureRef: string; expected: string; filterColumn: string; filterValue: string; provenance: string;
  filterDax: string; expressionDax: string; testExpression: boolean; abs: string; relPercent: string;
};
export type SqlFields = {
  measureRef: string; picked: string | null; groupBy: string[]; sql: string; sqlGrandTotal: string;
  filterDax: string; blankPolicy: string; abs: string; relPercent: string;
};
/** A check drawer's draft: the fields, and the check they were typed on (the saved definition, a hand-off, or none). */
export type CheckDraft = { fields: TrustedFields | SqlFields; from: TestDefinitionW | null };
type MappingFields = { sqlSourceId: string; schema: string; entity: string };

export const checkDraftScope = (id: string) => `tests-authoring|check|${id}`;
export const newCheckDraftScope = (kind: AuthorShape) => `tests-authoring|new|${kind}`;
export const mappingDraftScope = (table: string) => `tests-authoring|mapping|${table}`;

/** The fields a form shows for a check, exactly as the form used to seed itself from it. */
function fieldsOf(kind: AuthorShape, def: TestDefinitionW | null): TrustedFields | SqlFields {
  if (kind === 'measureReconcile') {
    const p = parseReconcile(def);
    return {
      measureRef: def?.targetRef || p.measureRef || '', picked: null, groupBy: p.groupBy || [], sql: p.sql || '',
      sqlGrandTotal: p.sqlGrandTotal || '', filterDax: p.filterDax || '', blankPolicy: p.blankPolicy || 'zero',
      abs: String(p.toleranceAbsolute ?? SQL_TOLERANCE_ABSOLUTE), relPercent: percentText(p.toleranceRelative ?? SQL_TOLERANCE_RELATIVE),
    };
  }
  const p = parseValue(def);
  const handed = p.expressionDax || '';
  return {
    measureRef: def?.targetRef || p.measureRef || '', expected: p.expectedValue || '', filterColumn: p.filterColumn || '',
    filterValue: p.filterValue || '', provenance: p.provenance || '', filterDax: p.filterDax || '', expressionDax: handed,
    // A handed-over TABLE query is never preselected (see daxReturnsOneCell).
    testExpression: !!handed && daxReturnsOneCell(handed),
    abs: String(p.toleranceAbsolute ?? 1e-6), relPercent: percentText(p.toleranceRelative ?? 1e-9),
  };
}
const draftOf = (kind: AuthorShape, def: TestDefinitionW | null | undefined): CheckDraft => ({ fields: fieldsOf(kind, def ?? null), from: def ?? null });

/** The table of a mapping draft on the open model, so New check > Table row count opens on it again. */
function heldMappingTable(): string {
  const m = draftModel();
  if (!m) return '';
  const prefix = mappingDraftScope('');
  for (const id of draftStore.held(m, prefix)) return (JSON.parse(id) as [string, string])[0].slice(prefix.length);
  return '';
}
/** Which kinds of New check hold a draft on the open model (read when the menu opens). */
function heldNewKinds(): Set<AuthorShape> {
  const m = draftModel(), out = new Set<AuthorShape>();
  if (!m) return out;
  for (const k of ['measureValue', 'measureReconcile'] as const) if (draftStore.get(m, newCheckDraftScope(k))) out.add(k);
  if (draftStore.held(m, mappingDraftScope('')).size) out.add('tableRowCount');
  return out;
}

/** A new check opened with something handed over, while a draft of that kind is held: the person picks, nothing is replaced. */
function HeldDraftBanner({ onKeepMine, onUseNew, disabled }: { onKeepMine: () => void; onUseNew: () => void; disabled?: boolean }) {
  return (
    <div role="alert" className="sem-inset mb-3 text-(length:--sem-text-label)" style={{ borderLeft: '3px solid var(--sem-warn)' }}>
      <div style={{ color: 'var(--sem-fg)' }}>You have a check of this kind that you started and did not save. Your words are still here, and nothing was saved yet.</div>
      <div className="mt-2 flex flex-wrap items-center gap-2">
        <button type="button" disabled={disabled} onClick={onKeepMine} className="sem-btn sem-btn-sm"
          title="Keep the check you started.">Keep mine</button>
        <button type="button" disabled={disabled} onClick={onUseNew} className="sem-btn sem-btn-sm"
          title="Drop the check you started and use what you just sent here.">Use the new one</button>
      </div>
    </div>
  );
}

/** The quiet mark and the one way to drop a draft on purpose (drawn in place, not as a component of its own). */
function draftRow(show: boolean, onDiscard: () => void, disabled?: boolean, className = 'mb-3') {
  // The row keeps its height whether or not there is a draft: it used to appear after the first keystroke and push the form down, so a number
  // typed a moment later went into the box that had moved under the cursor (UAT 1.5.104).
  return (
    <div className={className + ' flex flex-wrap items-center gap-2'} style={{ minHeight: 'var(--sem-control-h-sm, 28px)' }} data-testid="draft-row">
      {show && <>
        <DraftMark show />
        <button type="button" disabled={disabled} onClick={onDiscard} className="sem-btn sem-btn-sm" title="Drop what you typed here and put the form back as it was opened.">Discard draft</button>
      </>}
    </div>
  );
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
  { shape: 'measureReconcile', label: 'Check against SQL', hint: 'The same number from SQL' },
  { shape: 'tableRowCount', label: 'Table row count', hint: 'Model rows against source rows' },
];

export function AuthorDrawer({
  shape, editing, focus, table, latestSources, onClose, onSaved, addSourceOnOpen, addSourceHost, onSourcesChanged, handedFrom,
}: {
  shape: AuthorShape; editing?: TestDefinitionW | null; focus?: AuthorFocus; table?: string;
  latestSources?: SqlSourceW[] | null;
  /** Opens with the add-a-source form showing (New check > Add a SQL source). The drawer owns adding a source, so a caller never passes a handler for it. */
  addSourceOnOpen?: boolean;
  /** A host a check named that is not saved: the add-a-source form opens with this server filled in, so one press and a name confirm it. */
  addSourceHost?: string;
  /** A source was added from this drawer: the page reads its list again (no check changed, so nothing is marked as changed). */
  onSourcesChanged?: () => void;
  onClose: () => void; onSaved: () => void;
  /** Where a handed expression came from, so the drawer says so. Absent means DAX Lab, as before. */
  handedFrom?: 'askdata' | 'queries';
}) {
  const [kind, setKind] = useState<AuthorShape>(shape);
  const [measures, setMeasures] = useState<MeasureRowW[]>([]);
  const [sources, setSources] = useState<SqlSourceW[]>([]);
  const [sourcesLoaded, setSourcesLoaded] = useState(false);
  // The source the person just added from this drawer: the picker adopts it, so they are back in the check with it chosen.
  const [adopt, setAdopt] = useState<string | null>(null);
  // The add-a-source form is part of the drawer, not a trip to Connections, so it works wherever the drawer opens (Query included).
  const [adding, setAdding] = useState(!!addSourceOnOpen);
  // What the form starts from: the source the open model is linked to, and which table maps to which source (for a SQL guess).
  const [linkedSourceId, setLinkedSourceId] = useState<string | null>(null);
  const [tableRows, setTableRows] = useState<TableMappingRowW[]>([]);
  const [busy, setBusy] = useState(false);
  const [err, setErr] = useState<string | null>(null);
  const [tryNote, setTryNote] = useState<string | null>(null);
  const locked = !!editing?.id || table != null;
  // A saved check is read again when its drawer opens, never trusted from the list the page last drew: the assistant
  // can save a check (save_test) that the list has not caught up with, and Save writes the whole definition.
  // Then it is read again on every activity, and Save reads it once more. A check that moved under an open drawer
  // is never overwritten silently (UAT-F-S06b-2-007, same class as the mapping form).
  const editId = editing?.id || '';
  const [live, setLive] = useState<TestDefinitionW | null | undefined>(undefined);
  const readLive = async () => ((await rpc<{ definitions?: TestDefinitionW[] }>('listTests')).definitions ?? []).find((d) => d.id === editId) ?? null;
  // The forms' fields live HERE, as one plain value, so the guard compares what was typed with where it started and the draft store
  // can hold it (see the drafts block above). The table row count form keeps its own guard and draft.
  const draftKind = kind === 'tableRowCount' ? null : kind;
  const scope = draftKind ? (editId ? checkDraftScope(editId) : newCheckDraftScope(draftKind)) : '';
  // Where a NEW check of each kind starts, taken once: what it was handed (DAX Lab, Ask your data, Impact), or, opened plainly, the
  // start of the draft it holds, so a check started and left is offered as it was and not asked about.
  const starts = useRef<Partial<Record<AuthorShape, CheckDraft>>>({});
  const startOf = (k: AuthorShape): CheckDraft =>
    (starts.current[k] ??= editing ? draftOf(k, editing) : heldDraft<CheckDraft>(newCheckDraftScope(k))?.base ?? draftOf(k, null));
  const [draft, setDraft] = useState<CheckDraft | null>(() => (!draftKind ? null : heldDraft<CheckDraft>(scope)?.value ?? (editId ? null : startOf(draftKind))));
  useEffect(() => {
    if (!editId) return;
    let gone = false;
    const load = () => readLive().then((d) => { if (!gone) setLive(d); })
      .catch(() => { if (!gone) setDraft((x) => x ?? draftOf(kind, editing)); });
    void load();
    const off = onActivity(() => { void load(); });
    return () => { gone = true; off(); };
  }, [editId]);
  // A saved check is compared with the live one (undefined until it is read, so a held draft keeps its starting point). A new
  // check has nothing saved to move under it: its starting point is the only thing to compare with.
  const current: CheckDraft | null | undefined = !draftKind ? null
    : editId ? (live === undefined ? undefined : live ? draftOf(draftKind, live) : null)
      : startOf(draftKind);
  const guard = useEditBase<CheckDraft | null>({
    current, draft: draft ?? undefined, editing: !!draftKind, scope, persist: draftKind ? { scope } : undefined,
    readNow: editId ? async () => { const d = await readLive(); return d ? draftOf(kind, d) : null; } : undefined,
    reseed: (v) => setDraft(v ?? draftOf(kind, editing)),
  });
  const onFields = (patch: object) => setDraft((d) => d && { ...d, fields: { ...d.fields, ...patch } as CheckDraft['fields'] });
  // Keep mine keeps the typed fields and takes the rest of the newer check (its title, its on/off switch) under them.
  const keepMine = () => { const theirs = guard.theirs; guard.keepMine(); if (theirs) setDraft((d) => d && { ...d, from: theirs.from }); };
  const discard = () => {
    if (!draftKind) return;
    const v = !editId ? draftOf(draftKind, editing)
      : current !== undefined ? (current ?? draftOf(draftKind, editing)) : (guard.baseNow() ?? draftOf(draftKind, editing));
    if (!editId) starts.current[draftKind] = v;
    setDraft(v); guard.rebase(v); setErr(null);
    const m = draftModel(); if (m) draftStore.clear(m, scope);
  };
  // A save the engine took ends the draft. The drawer usually closes straight after, so the store is told now, not on a later render.
  // R-B (drafts-sol F5): it ends only the words the save CARRIED (`sent`). Fields stay editable while the engine is busy, so what is held when the answer comes may be newer;
  // then the draft stays over what was saved and this returns false, which keeps the drawer open on those words.
  const draftRef = useRef(draft); draftRef.current = draft;
  const ended = (sent?: CheckDraft['fields']): boolean => {
    const now = draftRef.current;
    if (sent !== undefined && now && !sameValue(now.fields, sent)) { guard.rebase({ ...now, fields: sent }); return false; }
    if (now) guard.rebase(now);
    const m = draftModel(); if (m && scope) draftStore.clear(m, scope);
    return true;
  };
  const pickKind = (k: AuthorShape) => {
    setKind(k);
    if (k !== 'tableRowCount') setDraft(heldDraft<CheckDraft>(newCheckDraftScope(k))?.value ?? startOf(k));
  };
  // A second explicit hand-off (DAX Lab again, while this drawer is open) changes only the calculation. The rest of the open draft remains the person's.
  const handed = useRef(editing?.targetRef);
  useEffect(() => {
    if (editing?.id || !editing?.targetRef || handed.current === editing.targetRef) return;
    handed.current = editing.targetRef;
    if (kind === 'measureValue') onFields({ measureRef: editing.targetRef });
  }, [editing?.id, editing?.targetRef]);
  // A saved check is written naming the fields the Save read, and the engine compares them inside its own write (Sol join 6, round 2).
  const before: SaveFence | undefined = editId ? { before: guard.beforeSave, expected: () => testFence(guard.confirmed()?.from), refused: guard.refused } : undefined;
  useEffect(() => { rpc<MeasureRowW[]>('listMeasures').then(setMeasures).catch(() => undefined); }, []);
  // Where a new check starts: read when the drawer opens, and again when a source is added from it (that source may become the model's own).
  const readStart = () => {
    rpc<{ sqlEndpoint?: { sourceId?: string } | null }>('connectionContext').then((c) => setLinkedSourceId(c?.sqlEndpoint?.sourceId ?? null)).catch(() => undefined);
    rpc<TableMappingListW>('listTableMappings').then((l) => setTableRows(l.rows ?? [])).catch(() => undefined);
  };
  useEffect(readStart, []);
  useEffect(() => {
    rpc<SqlSourceW[]>('listSqlSources')
      .then((list) => { setSources(list); setSourcesLoaded(true); })
      .catch(() => { setSources([]); setSourcesLoaded(true); });
  }, []);
  // The page re-reads the sources when one is saved elsewhere and hands the new list down; the picker only learns the list.
  useEffect(() => {
    if (!latestSources) return;
    setSources(latestSources);
    setSourcesLoaded(true);
  }, [latestSources]);
  // Escape belongs to whatever is on TOP. Opening Connections from inside a half-finished check and
  // pressing Escape used to close both, throwing the check away to save a source for it.
  useSurfaceEscape(onClose);
  // The form saved a source: it joins the list at once and is chosen in the check. The form closes unless its test failed (it stays for a fix).
  const sourceAdded = (added: AddedSource, keepOpen: boolean, connected?: string) => {
    setTryNote(connected ?? null);   // Save and check: the form closes on success, so the drawer says it connected
    setSources((list) => [...list.filter((x) => x.id !== added.id), { id: added.id, name: added.name }]);
    setSourcesLoaded(true);
    setAdopt(added.id);
    readStart();
    if (!keepOpen) setAdding(false);
    onSourcesChanged?.();
  };
  const beginAddSqlSource = () => setAdding(true);
  // Which saved source each table is counted against: the one the person chose for it, else the model's own endpoint, else the source whose
  // server and database its partition reads. That is what lets a plain SUM over a table start as SQL.
  const sameSource = (r: TableMappingRowW) => sources.find((x) => !!r.server && !!r.database && x.server?.trim().toLowerCase() === r.server.trim().toLowerCase() && x.database?.trim().toLowerCase() === r.database.trim().toLowerCase())?.id;
  const mappings: MappingLike[] = tableRows.map((r) => ({
    modelTable: r.modelTable, schema: r.schema, entity: r.entity, canCount: r.canCount,
    sqlSourceId: r.sqlSourceId || (r.source === 'endpoint' ? linkedSourceId ?? undefined : sameSource(r)),
  }));
  const addSource = adding ? <SqlSourceAddForm initialServer={addSourceHost} onAdded={sourceAdded} onCancel={() => setAdding(false)} /> : null;

  return (
    <aside className="fixed top-0 right-0 z-20 flex h-full w-[440px] max-w-[100vw] flex-col border-l" style={{ background: 'var(--sem-surface)', borderColor: 'var(--sem-border)' }}>
      <div className="flex items-start justify-between gap-3 border-b px-4 py-3" style={{ borderColor: 'var(--sem-border)' }}>
        <div>
          <div className="sem-label">{editing?.id ? 'Edit' : table ? 'Choose source' : 'New check'}</div>
          <h2 className="m-0 mt-1 text-(length:--sem-text-title) font-semibold">{KINDS.find((k) => k.shape === kind)?.label ?? 'New check'}</h2>
        </div>
        <button type="button" onClick={onClose} className="text-(length:--sem-text-meta)" style={{ color: 'var(--sem-muted)' }}>Close</button>
      </div>
      <div className="min-h-0 flex-1 overflow-auto px-4 py-3">
        {!locked && <KindChooser kind={kind} onPick={pickKind} />}
        {guard.moved && (editId
          ? <EditMovedBanner what="check" theirs={guard.theirs} onKeepMine={keepMine} onUseNew={guard.useNew} disabled={busy} />
          : <HeldDraftBanner onKeepMine={keepMine} onUseNew={guard.useNew} disabled={busy} />)}
        {draftRow(guard.drafted, discard, busy)}
        {editId && !draft && <div className="sem-inset text-(length:--sem-text-meta)" style={{ color: 'var(--sem-muted)' }}>Loading the check…</div>}
        {draft && kind === 'measureValue' && <TrustedAnswerForm measures={measures} fields={draft.fields as TrustedFields} onFields={onFields} editing={draft.from} handedFrom={handedFrom} busy={busy} setBusy={setBusy} setErr={setErr} setTryNote={setTryNote} onSaved={onSaved} onClose={onClose} before={before} onWritten={ended} />}
        {draft && kind === 'measureReconcile' && <CompareWithSourceForm measures={measures} fields={draft.fields as SqlFields} onFields={onFields} sources={sources} sourcesLoaded={sourcesLoaded} adopt={adopt} editing={draft.from} focus={focus} busy={busy} setBusy={setBusy} setErr={setErr} setTryNote={setTryNote} onSaved={onSaved} onClose={onClose} onAddSqlSource={beginAddSqlSource} addSource={addSource} linkedSourceId={linkedSourceId} mappings={mappings} before={before} onWritten={ended} />}
        {kind === 'tableRowCount' && <TableRowCountForm sources={sources} adopt={adopt} table={table} busy={busy} setBusy={setBusy} setErr={setErr} onSaved={onSaved} onClose={onClose} onAddSqlSource={beginAddSqlSource} addSource={addSource} linkedSourceId={linkedSourceId} />}
      </div>
      {/* The answer to the last press sits under the buttons, outside the scrolling form: a result drawn at the top of a long form was off screen. */}
      {(err || tryNote) && <div role={err ? 'alert' : 'status'} aria-live="polite" data-testid="author-result" className="border-t px-4 py-2 text-(length:--sem-text-meta)"
        style={{ borderColor: err ? 'var(--sem-bad)' : 'var(--sem-border)', color: err ? 'var(--sem-bad)' : 'var(--sem-fg)', background: 'var(--sem-surface)' }}>{err ?? tryNote}</div>}
    </aside>
  );
}

// The kind comes FIRST, and each one is named by what you are doing, not by the engine's word for it.
function KindChooser({ kind, onPick }: { kind: AuthorShape; onPick: (shape: AuthorShape) => void }) {
  return (
    <div className="mb-4 grid gap-2" style={{ gridTemplateColumns: 'repeat(3,minmax(0,1fr))' }} role="radiogroup" aria-label="What kind of check">
      {KINDS.map((item) => {
        const on = item.shape === kind;
        return <button key={item.shape} type="button" role="radio" aria-checked={on} onClick={() => onPick(item.shape)}
          className="rounded border p-2 text-left"
          style={{ borderColor: on ? 'var(--sem-accent)' : 'var(--sem-border)', background: on ? 'var(--sem-accent-soft)' : 'var(--sem-surface-2)' }}>
          <span className="block text-(length:--sem-text-meta) font-semibold">{item.label}</span>
          {/* On the picked kind's accent tint the muted colour falls under 4.5:1 in Light, so the hint takes the text colour there. */}
          <span className="block text-(length:--sem-text-label)" style={{ color: on ? 'var(--sem-fg)' : 'var(--sem-muted)' }}>{item.hint}</span>
        </button>;
      })}
    </div>
  );
}

// The three ways to start a check. It is a menu button, and it behaves like every other menu in the app:
// Escape closes it and hands focus back, a click outside closes it, assistive tech is told it is a menu,
// and it hangs under the button that opened it instead of under Run next door.
export function NewCheckMenu({ onPick, onAddSqlSource }: { onPick: (shape: AuthorShape) => void; onAddSqlSource?: () => void }) {
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
  const held = open ? heldNewKinds() : null;   // a kind with a check started and not saved says so
  return (
    <div ref={wrap} className="relative">
      <button ref={buttonRef} type="button" aria-haspopup="menu" aria-expanded={open}
        className="inline-flex h-7 items-center rounded border px-3 text-(length:--sem-text-meta) font-semibold" onClick={() => setOpen((v) => !v)}
        style={{ background: 'var(--sem-surface-2)', borderColor: 'var(--sem-border)' }}>New check ▾</button>
      {open && (
        <div role="menu" aria-label="New check" className="absolute left-0 z-20 mt-1 w-[280px] rounded border p-2" style={{ background: 'var(--sem-surface)', borderColor: 'var(--sem-border)' }}>
          <NewCheckChoices held={held} onPick={choose} onAddSqlSource={onAddSqlSource ? () => { onAddSqlSource(); setOpen(false); } : undefined} />
        </div>
      )}
    </div>
  );
}

/** The ways to start a check, as menu rows. Validation's "..." menu opens the same list in its New check panel. */
export function NewCheckChoices({ held, onPick, onAddSqlSource }: { held?: Set<AuthorShape> | null; onPick: (shape: AuthorShape) => void; onAddSqlSource?: () => void }) {
  const drafts = held ?? heldNewKinds();
  return <>
    {KINDS.map((item) => <Pick key={item.shape} label={item.label} hint={item.hint} drafted={drafts.has(item.shape)} onClick={() => onPick(item.shape)} />)}
    {/* The SQL sources count and its button left the tool row; the one door is here, last, since a check compares against a source. */}
    {onAddSqlSource && <Pick label="Add a SQL source" hint="Connect a database to compare against." onClick={onAddSqlSource} />}
  </>;
}

function Pick({ label, hint, drafted, onClick }: { label: string; hint: string; drafted?: boolean; onClick: () => void }) {
  return (
    <button type="button" role="menuitem" onClick={onClick} className="mb-1 w-full rounded px-2 py-2 text-left hover:bg-[var(--sem-hover)]">
      <span className="block text-(length:--sem-text-meta) font-semibold">{label}{drafted ? <DraftMark show className="ml-2" /> : null}</span>
      <span className="block text-(length:--sem-text-label)" style={{ color: 'var(--sem-muted)' }}>{hint}</span>
    </button>
  );
}

function Field({ label, hint, children }: { label: string; hint?: string; children: ReactNode }) {
  return (
    <label className="mb-3 block">
      <span className="mb-1 block text-(length:--sem-text-label) font-semibold">{label}</span>
      {children}
      {hint && <span className="mt-1 block text-(length:--sem-text-label)" style={{ color: 'var(--sem-muted)' }}>{hint}</span>}
    </label>
  );
}

// ONE disclosure, and it only ever holds tuning. Nothing the engine requires, and nothing that decides
// whether a verdict is reachable, is allowed in here.
function Advanced({ children, open = false, summary = 'tolerance and a complete DAX filter' }: { children: ReactNode; open?: boolean; summary?: string }) {
  return (
    <details open={open} className="sem-inset mb-3">
      <summary className="cursor-pointer text-(length:--sem-text-label) font-semibold">Advanced <span className="font-normal" style={{ color: 'var(--sem-muted)' }}>{summary}</span></summary>
      <div className="mt-2">{children}</div>
    </details>
  );
}

// What it costs, said BEFORE the last click. Astra found both saving and recording carry conditions that
// only announced themselves after the person had finished the form.
function SaveRequirement({ kind }: { kind: AuthorShape }) {
  return (
    <div className="sem-inset mt-3 text-(length:--sem-text-label)" style={{ color: 'var(--sem-muted)' }}>
      {kind === 'tableRowCount'
        ? 'The chosen source is kept in a small file beside this model.'
        : 'The check is kept in a small file beside this model. Try it now saves nothing.'}
    </div>
  );
}

function MeasureSelect({ measures, value, onChange }: { measures: MeasureRowW[]; value: string; onChange: (v: string) => void }) {
  return (
    <select value={value} onChange={(e) => onChange(e.target.value)} className="w-full rounded border px-2 py-2 text-(length:--sem-text-meta)" style={inp}>
      <option value="">Pick a calculation</option>
      {measures.map((m) => <option key={m.ref} value={m.ref}>{m.name}</option>)}
    </select>
  );
}

// Saved once, named, chosen from a list. A source is added from the button under the list, in a small form inside this drawer, and saved to
// the same list Connections shows, so there is still one place the sources live.
function SqlSourceSelect({ sources, value, onChange, onAdd, addForm }: {
  sources: SqlSourceW[]; value: string; onChange: (id: string) => void; onAdd?: () => void; addForm?: ReactNode;
}) {
  // Adding a source is a button of its own, never an item in the list. An item in a native list acts the moment the arrow keys move over it,
  // which opened a form over the check just from moving through the choices (UAT 1.5.104).
  return (
    <>
      <select value={value} onChange={(e) => onChange(e.target.value)}
        aria-label="SQL source" className="w-full min-w-0 rounded border px-2 py-2 text-(length:--sem-text-meta)" style={inp}>
        <option value="">Pick a SQL source</option>
        {sources.map((s) => <option key={s.id} value={s.id}>{s.name}</option>)}
      </select>
      {addForm ?? (onAdd && <button type="button" className="sem-btn sem-btn-sm sem-btn-quiet mt-1" onClick={onAdd} data-testid="check-add-source-open">Add a SQL source…</button>)}
      {sources.length === 0 && !addForm && <span className="mt-1 block text-(length:--sem-text-label)" style={{ color: 'var(--sem-muted)' }}>
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
    <div className="sem-inset mb-3 text-(length:--sem-text-label)">
      <strong>Authored against {authored || current}</strong>
      <div className="mt-1" style={{ color: 'var(--sem-muted)' }}>
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
    <div className="rounded border p-3" style={{ borderColor: 'var(--sem-border)' }}>
      <div className="flex flex-wrap gap-2">
        {value.map((field) => <span key={field} className="sem-chip sem-chip-outline">
          {contextLabel(field)}
          <button type="button" aria-label={`Remove ${contextLabel(field)}`} onClick={() => onChange(value.filter((item) => item !== field))} style={{ color: 'var(--sem-muted)' }}>×</button>
        </span>)}
        {value.length === 0 && <span className="text-(length:--sem-text-label)" style={{ color: 'var(--sem-muted)' }}>No fields chosen yet.</span>}
      </div>
      <div className="mt-2 flex flex-wrap items-center gap-2">
        <button type="button" className="min-h-7 rounded border px-3 text-(length:--sem-text-label) font-semibold" onClick={() => setChoosing((open) => !open)} style={{ background: 'var(--sem-surface-2)', borderColor: 'var(--sem-border)' }}>{choosing ? 'Done choosing' : 'Choose fields'}</button>
        <span className="text-(length:--sem-text-label)" style={{ color: 'var(--sem-muted)' }}>Pick Product category, Customer region, Date year, or any fields from the model.</span>
      </div>
      {choosing && <div className="mt-2"><ObjectBrowser kinds={['column']} multiSelect dragEnabled={false} height={230}
        pickedRefs={new Set(value)} onPick={(node) => addNodes([node])} onPickMany={addNodes} emptyHint="No columns are available in this model." /></div>}
      <details className="mt-2">
        <summary className="cursor-pointer text-(length:--sem-text-label)" style={{ color: 'var(--sem-muted)' }}>Enter a field reference</summary>
        <div className="mt-2 flex gap-2">
          <input value={manual} onChange={(event) => setManual(event.target.value)} placeholder="'Table'[Column]" className="min-h-7 min-w-0 flex-1 rounded border px-2 font-mono text-(length:--sem-text-label)" style={inp} />
          <button type="button" disabled={!manual.trim()} className="sem-btn sem-btn-sm" onClick={() => { add([manual]); setManual(''); }}>Add</button>
        </div>
      </details>
    </div>
  );
}

// Two bare boxes side by side make the person guess which is which. Each one names its own unit.
function ToleranceInputs({ abs, onAbs, relPercent, onRelPercent }: { abs: string; onAbs: (v: string) => void; relPercent: string; onRelPercent: (v: string) => void }) {
  return (
    <div className="flex gap-2">
      <label className="flex-1 text-(length:--sem-text-label)" style={{ color: 'var(--sem-muted)' }}>Absolute
        <input value={abs} onChange={(event) => onAbs(event.target.value)} aria-label="Absolute tolerance" className="mt-1 min-h-7 w-full rounded border px-2 text-(length:--sem-text-meta)" style={inp} />
      </label>
      <label className="flex-1 text-(length:--sem-text-label)" style={{ color: 'var(--sem-muted)' }}>Relative %
        <input value={relPercent} onChange={(event) => onRelPercent(event.target.value)} aria-label="Relative tolerance in percent" className="mt-1 min-h-7 w-full rounded border px-2 text-(length:--sem-text-meta)" style={inp} />
      </label>
    </div>
  );
}

function Footer({ busy, onTry, onSave, tryLabel = 'Try it now', saveLabel = 'Save check' }: {
  busy: boolean; onTry?: () => void; onSave: () => void; tryLabel?: string; saveLabel?: string;
}) {
  return (
    // Sticky at the bottom of the scrolling form, so Try it now and Save are in reach whatever the form's length and never slide under the answer.
    // The form's own padding sits below a sticky bar's edge, so the bar reaches down through it (-bottom-3 here, pb-3 inside) or the fields scrolling by show through under the buttons.
    <div className="sticky -bottom-3 z-10 -mx-4 -mb-3 mt-3 flex flex-wrap items-center gap-2 px-4 pb-3 pt-2" style={{ background: 'var(--sem-surface)' }}>
      {onTry && <button type="button" disabled={busy} onClick={onTry} className="min-h-7 rounded border px-3 py-1 text-(length:--sem-text-meta)" style={{ background: 'var(--sem-surface-2)', borderColor: 'var(--sem-border)' }}>{tryLabel}</button>}
      <button type="button" disabled={busy} onClick={onSave} className="min-h-7 rounded border px-3 py-1 text-(length:--sem-text-meta) font-semibold" style={{ background: 'var(--sem-accent)', borderColor: 'var(--sem-accent)', color: 'var(--sem-on-accent)' }}>{saveLabel}</button>
    </div>
  );
}

// ---------------------------------------------------------------------------------------------------
// Trusted answer. The calculation, the number, and where it came from. No grouping: this kind compares
// ONE value against ONE expected answer, and the engine has no per-group expected answers to compare to.
// No timing budget either: the timing pass only ever receives reconcile plans, so the old field promised
// a measurement this kind never gets (Astra, journey A).
// ---------------------------------------------------------------------------------------------------
export function TrustedAnswerForm({ measures, fields, onFields, editing, handedFrom, busy, setBusy, setErr, setTryNote, onSaved, onClose, before, onWritten }: {
  measures: MeasureRowW[];
  /** Every field of the form, held by the drawer (the draft). `editing` is the check they were typed on. */
  fields: TrustedFields; onFields: (patch: Partial<TrustedFields>) => void;
  editing?: TestDefinitionW | null; handedFrom?: 'askdata' | 'queries'; busy: boolean;
  setBusy: (v: boolean) => void; setErr: (v: string | null) => void; setTryNote: (v: string | null) => void;
  onSaved: () => void; onClose: () => void;
  /** Run before the write: false means the saved check moved and the person must choose first. */
  before?: SaveFence;
  /** The engine took the save: the draft ends. */
  /** The engine took the write, which carried `sent`. False means the fields were edited meanwhile: they stay held and the drawer stays open (R-B). */
  onWritten?: (sent: TrustedFields) => boolean | void;
}) {
  const initial = parseValue(editing);
  const { measureRef, filterColumn, filterValue, expected, provenance, filterDax, expressionDax, testExpression, abs, relPercent } = fields;
  const setMeasureRef = (v: string) => onFields({ measureRef: v });
  const setFilterColumn = (v: string) => onFields({ filterColumn: v });
  const setFilterValue = (v: string) => onFields({ filterValue: v });
  // The number from a Query answer is the raw ratio (0.5812049141182254). It is SHOWN the way the measure shows it (58.1%) until the person
  // types, and a typed "58.1%" is understood by the engine (round 2 N11).
  const model = useDaxModelContext();
  const measureName = measureRef.replace(/^measure:/, '').replace(/^[^/]*\//, '');
  const formatString = (model.measures ?? []).find((x) => x.name === measureName)?.formatString;
  const [typedExpected, setTypedExpected] = useState(false);
  const setExpected = (v: string) => { setTypedExpected(true); onFields({ expected: v }); };
  const expectedShown = typedExpected ? expected : shownNumber(expected, formatString);
  const setProvenance = (v: string) => onFields({ provenance: v });
  const setFilterDax = (v: string) => onFields({ filterDax: v });
  const setExpressionDax = (v: string) => onFields({ expressionDax: v });
  const setTestExpression = (v: boolean) => onFields({ testExpression: v });
  const setAbs = (v: string) => onFields({ abs: v });
  const setRelPercent = (v: string) => onFields({ relPercent: v });
  // The DAX Lab hands over an EDITED formula when the person changed it. They choose which one this check is
  // about; picking the saved measure drops the expression rather than quietly testing text the model lacks.
  const handedExpression = initial.expressionDax || '';
  // A handed-over TABLE query is never preselected as the formula this check asks (fieldsOf). A measure check can only
  // compare one number, so preselecting it staged a check that could never answer the question on screen.
  const handedIsOneCell = daxReturnsOneCell(handedExpression);
  const relFraction = relativeFraction(relPercent, initial.toleranceRelative ?? 1e-9);
  // Say what happens to the filter BEFORE the person saves. A complete query is scoped from outside, and some
  // queries cannot be scoped at all, so saving one with a filter saves a check the engine will not run.
  const filterNote = measureFilterNote(testExpression ? expressionDax : '', filterDax || (filterColumn.trim() && filterValue.trim() ? filterColumn + ' = ' + filterValue : ''));
  // The title is read off the check every time (checkTitle, askdata-model.ts): the calculation, every condition it
  // asks (the formula's, Only where, the written filter) and the number. A title carried beside the check and kept
  // while "nothing that matters" changed went false twice, each time through a field it did not watch: "Units totals
  // 125049" for a one-row check once the number was typed (verify-askdata-r2 R2), then once Only where was added or
  // the row in the formula was edited (verify-askdata-r3 R2). The one title kept is a SAVED check's, while it still
  // asks exactly what it asked when it was opened, because that title may be one the assistant chose (save_test).
  const asks = (m: string, fc: string, fv: string, fd: string, x: string, n: string) => JSON.stringify([m, fc.trim(), fv.trim(), fd.trim(), x.trim(), n.trim()]);
  const savedAsks = asks(editing?.targetRef || initial.measureRef || '', initial.filterColumn || '', initial.filterValue || '',
    initial.filterDax || '', handedExpression && handedIsOneCell ? handedExpression : '', String(initial.expectedValue ?? ''));
  const title = () => {
    const x = testExpression ? expressionDax : '';
    if (editing?.id && editing.title?.trim() && asks(measureRef, filterColumn, filterValue, filterDax, x, expected) === savedAsks) return editing.title;
    return checkTitle(measureRef, expectedShown, { expressionDax: x, filterColumn, filterValue, filterDax });
  };
  const def = (): TestDefinitionW => ({
    ...editing,
    id: editing?.id || '', kind: 'measureValue', title: title(),
    targetRef: measureRef, enabled: editing?.enabled ?? true,
    paramsJson: JSON.stringify({
      measureRef, expectedValue: expectedShown.trim(), filterColumn: filterColumn.trim() || undefined,
      filterValue: filterValue.trim() || undefined, filterDax: filterDax.trim() || undefined,
      expressionDax: testExpression ? (expressionDax.trim() || undefined) : undefined,
      provenance: provenance.trim() || undefined, toleranceAbsolute: Number(abs) || 0, toleranceRelative: relFraction,
    } satisfies MeasureValueParamsW),
  });

  return (
    <>
      <Field label="Which calculation"><MeasureSelect measures={measures} value={measureRef} onChange={setMeasureRef} /></Field>
      <Field label="The number you trust" hint="Type BLANK if the right answer is no value.">
        <input value={expectedShown} onChange={(e) => setExpected(e.target.value)} className="min-h-7 w-full rounded border px-2 text-(length:--sem-text-meta)" style={inp} />
      </Field>
      <Field label="Only where (optional)" hint="Example: Year is 2024.">
        <span className="flex gap-2">
          <input value={filterColumn} onChange={(e) => setFilterColumn(e.target.value)} placeholder="Column" className="min-h-7 min-w-0 flex-1 rounded border px-2 text-(length:--sem-text-meta)" style={inp} />
          <input value={filterValue} onChange={(e) => setFilterValue(e.target.value)} placeholder="Value" className="min-h-7 min-w-0 flex-1 rounded border px-2 text-(length:--sem-text-meta)" style={inp} />
        </span>
      </Field>
      <Field label="Where this number came from (optional)">
        <input value={provenance} onChange={(e) => setProvenance(e.target.value)} placeholder="Finance month-end pack, July" className="min-h-7 w-full rounded border px-2 text-(length:--sem-text-meta)" style={inp} />
      </Field>
      {handedExpression && (
        <div className="sem-inset mb-3">
          <div className="mb-1 text-(length:--sem-text-label) font-semibold">Which formula is this check about?</div>
          <label className="block text-(length:--sem-text-meta)"><input type="radio" name="whichFormula" checked={!testExpression} onChange={() => setTestExpression(false)} /> Test the measure as saved</label>
          <label className="block text-(length:--sem-text-meta)"><input type="radio" name="whichFormula" checked={testExpression} onChange={() => setTestExpression(true)} /> Test this expression</label>
          <div className="mt-1 text-(length:--sem-text-label)" style={{ color: 'var(--sem-muted)' }}>{handedFrom === 'askdata' || handedFrom === 'queries'
            ? `This number came from your question in Query, with its filters and its row. Test this expression to keep them, or the measure as saved to check its total.`
            : 'You changed the formula in DAX Lab. The model still holds the saved one, so say which one this check should ask.'}</div>
          {!handedIsOneCell && <div className="mt-2 rounded border px-2 py-2 text-(length:--sem-text-label)" style={{ borderColor: 'var(--sem-warn)', color: 'var(--sem-warn)' }}>Your DAX Lab query returns a table, not one number. A measure check compares one number, so it cannot ask this as it stands. Leave this check on the measure as saved, or pick Test this expression and replace it with a single value.</div>}
          {testExpression && <textarea value={expressionDax} onChange={(event) => setExpressionDax(event.target.value)} rows={4} spellCheck={false} aria-label="The expression to test" className="mt-2 w-full rounded border px-2 py-1 font-mono text-(length:--sem-text-label)" style={inp} />}
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
          <textarea value={filterDax} onChange={(event) => setFilterDax(event.target.value)} rows={3} placeholder={'\'Product\'[Category] = "Bikes", \'Date\'[Year] = 2026'} spellCheck={false} className="w-full rounded border px-2 py-1 font-mono text-(length:--sem-text-label)" style={inp} />
        </Field>
        {!!initial.filterDax && <div className="text-(length:--sem-text-label)" style={{ color: 'var(--sem-muted)' }}>Carried over from the filters on your visual. Change them here if this check should ask something wider.</div>}
      </Advanced>
      {filterNote && (
        <div className="mb-3 rounded border px-2 py-2 text-(length:--sem-text-label)" style={filterNote.tone === 'warn'
          ? { borderColor: 'var(--sem-warn)', color: 'var(--sem-warn)' }
          : { borderColor: 'var(--sem-border)', color: 'var(--sem-muted)' }}>{filterNote.text}</div>
      )}
      <SaveRequirement kind="measureValue" />
      <Footer busy={busy} onTry={() => void tryDef(def(), setBusy, setErr, setTryNote)} onSave={() => void saveDef(def(), setBusy, setErr, onSaved, onClose, before, onWritten && (() => onWritten(fields)))} />
    </>
  );
}

// ---------------------------------------------------------------------------------------------------
// Compare with source. Everything the engine REQUIRES is visible, and so is the one thing that decides
// whether a verdict is even reachable.
// ---------------------------------------------------------------------------------------------------
export function CompareWithSourceForm({ measures, fields, onFields, sources, sourcesLoaded, adopt, editing, focus, busy, setBusy, setErr, setTryNote, onSaved, onClose, onAddSqlSource, addSource, linkedSourceId, mappings, before, onWritten }: {
  measures: MeasureRowW[];
  /** Every field of the form, held by the drawer (the draft). `editing` is the check they were typed on. */
  fields: SqlFields; onFields: (patch: Partial<SqlFields>) => void;
  sources: SqlSourceW[]; sourcesLoaded: boolean; adopt?: string | null; editing?: TestDefinitionW | null; focus?: AuthorFocus; busy: boolean;
  setBusy: (v: boolean) => void; setErr: (v: string | null) => void; setTryNote: (v: string | null) => void;
  onSaved: () => void; onClose: () => void; onAddSqlSource?: () => void;
  /** The add-a-source form, when the drawer is showing it in place of the button. */
  addSource?: ReactNode;
  /** The source the open model is linked to, and which table maps to which source: where a new check starts. */
  linkedSourceId?: string | null; mappings?: MappingLike[];
  before?: SaveFence;
  /** The engine took the write, which carried `sent`. False means the fields were edited meanwhile: they stay held and the drawer stays open (R-B). */
  onWritten?: (sent: SqlFields) => boolean | void;
}) {
  const initial = parseReconcile(editing);
  const model = useDaxModelContext();
  const { measureRef, picked, groupBy, sql, sqlGrandTotal, filterDax, blankPolicy, abs, relPercent } = fields;
  const setMeasureRef = (v: string) => onFields({ measureRef: v });
  // What this check is ALREADY bound to, decided by one rule rather than by whatever the list happens to
  // hold. `picked` is the person's own change; until they make one the binding stands as it was saved.
  const setPicked = (id: string) => onFields({ picked: id });
  const binding: SqlSourceBinding = sqlSourceBinding({
    savedId: initial.sqlSourceId,
    savedName: initial.sqlSourceName,
    inlineServer: initial.server,
    inlineDatabase: initial.database,
    available: sources.map((s) => ({ id: s.id, name: s.name })),
    loaded: sourcesLoaded,
  });
  // A new check starts on the model's own source (or the only one saved). It is shown, not stored, so it makes no draft; the person's own pick wins.
  const startSourceId = binding.select || binding.keepsInline ? '' : defaultSqlSourceId(linkedSourceId, sources);
  const sqlSourceId = picked ?? (binding.select || startSourceId);
  const setSqlSourceId = (id: string) => setPicked(id);
  const setGroupBy = (v: string[]) => onFields({ groupBy: v });
  const typedSql = useRef(false);   // once the person has touched the box (even to empty it) the guess never comes back over their words
  const setSql = (v: string) => { typedSql.current = true; onFields({ sql: v }); };
  // A plain SUM of one column, over a table that maps to this source, starts the SQL as a guess the person can change. Shown, not stored:
  // once they type anything it is theirs, and until then nothing is a draft.
  const measureExpression = measures.find((m) => m.ref === measureRef)?.expression;
  // The groups are part of the guess: the SQL must select them first, in the order chosen, or the check fails on its own defaults.
  const [links, setLinks] = useState<LinkLike[]>([]);
  useEffect(() => { rpc<{ relationships?: LinkLike[] }>('getModelGraph').then((g) => setLinks(g.relationships ?? [])).catch(() => setLinks([])); }, []);
  const guess = sql.trim() === '' && !typedSql.current && !editing?.id ? guessSumSql(measureExpression, mappings ?? [], sqlSourceId, groupBy, links) : null;
  const sqlText = sql !== '' || typedSql.current ? sql : guess ?? '';
  const setSqlGrandTotal = (v: string) => onFields({ sqlGrandTotal: v });
  const setFilterDax = (v: string) => onFields({ filterDax: v });
  const setBlankPolicy = (v: string) => onFields({ blankPolicy: v });
  // A requirement is not a mistake until the person tries to save. Before that the blank-meaning line is a
  // neutral "still needed"; only an attempted Save turns it into the failure colour.
  const [saveAttempted, setSaveAttempted] = useState(false);
  const blankGroup = useRef<HTMLDivElement | null>(null);
  const setAbs = (v: string) => onFields({ abs: v });
  const setRelPercent = (v: string) => onFields({ relPercent: v });
  const relFraction = relativeFraction(relPercent, initial.toleranceRelative ?? SQL_TOLERANCE_RELATIVE);
  // Only ever set from a baseline the Add handler captured, so this can no longer fire on a first load.
  useEffect(() => { if (adopt) setPicked(adopt); }, [adopt]);

  const params = (): ReconcileParamsW => ({
    measureRef, sql: sqlText.trim(), sqlGrandTotal: sqlGrandTotal.trim() || undefined,
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
    id: editing?.id || '', kind: 'measureReconcile', title: sqlTitleFor(measureRef, groupBy),
    targetRef: measureRef, enabled: editing?.enabled ?? true, paramsJson: JSON.stringify(params()),
  });
  // Two checks of one calculation can differ only in their SQL, so a NEW check takes a name no saved check has (round 2 N15).
  const nameNew = async (d: TestDefinitionW): Promise<TestDefinitionW> => {
    if (d.id) return d;
    try {
      const saved = ((await rpc<{ definitions?: TestDefinitionW[] }>('listTests')).definitions ?? []).map((x) => x.title ?? '');
      return { ...d, title: distinctTitle(d.title, saved) };
    } catch { return d; }
  };

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
  const grain = grainWords(groupBy, model.columns);   // a sort-ordered field is compared on its own, which can differ from the grid (join 6, J4)
  return (
    <>
      <Field label="Which calculation"><MeasureSelect measures={measures} value={measureRef} onChange={setMeasureRef} /></Field>
      <Field label="SQL source" hint="Saved once in Connections, then used by any check or table mapping.">
        <SqlSourceSelect sources={sources} value={sqlSourceId} onChange={setSqlSourceId} onAdd={onAddSqlSource} addForm={addSource} />
        {binding.note && !picked && <span className="mt-1 block text-(length:--sem-text-label)"
          style={{ color: binding.needsReplacement ? 'var(--sem-warn)' : 'var(--sem-muted)' }}>{binding.note}</span>}
      </Field>
      <Field label="The SQL you accept as the truth" hint={guess ? 'A first guess from the column in this calculation. Change it if your source is different.' : undefined}>
        <textarea value={sqlText} onChange={(e) => setSql(e.target.value)} rows={5} spellCheck={false} aria-label="The SQL you accept as the truth" className="w-full min-w-0 rounded border px-2 py-1 font-mono text-(length:--sem-text-label)" style={inp} />
      </Field>
      <div className="sem-inset mb-3 text-(length:--sem-text-label)" style={{ color: 'var(--sem-muted)' }}>
        Your SQL must use the same filter as the check. Semanticus never rewrites your query, so if the two sides are
        narrowed differently they are answering different questions.
      </div>
      <Field label="Only where (optional)" hint="A complete DAX filter for the model side. Narrow your SQL the same way.">
        <textarea value={filterDax} onChange={(event) => setFilterDax(event.target.value)} rows={2} spellCheck={false} aria-label="Only where"
          placeholder={'\'Date\'[Year] = 2026, \'Product\'[Category] = "Bikes"'} className="w-full rounded border px-2 py-1 font-mono text-(length:--sem-text-label)" style={inp} />
      </Field>
      {!!initial.filterDax && <div className="mb-3 text-(length:--sem-text-label)" style={{ color: 'var(--sem-muted)' }}>Carried over from the filters on your visual.</div>}
      <div className="mb-3 block">
        <div className="mb-1 text-(length:--sem-text-label) font-semibold">Which groups to compare</div>
        <ContextFields value={groupBy} onChange={setGroupBy} />
        {evidenceNote
          ? <div className="mt-1 text-(length:--sem-text-label)" style={{ color: 'var(--sem-muted)' }}>{evidenceNote}</div>
          : <div className="mt-1 text-(length:--sem-text-label)" style={{ color: 'var(--sem-muted)' }}>Fields can come from different tables. The check compares every combination both queries return.</div>}
        {grain && <div className="mt-1 text-(length:--sem-text-label)" style={{ color: 'var(--sem-muted)' }} data-testid="check-grain">{grain}</div>}
      </div>
      <div className="mb-3" ref={blankGroup}>
        <div className="mb-1 text-(length:--sem-text-label) font-semibold">A blank in the model means</div>
        {([['zero', 'Zero'], ['null', 'No value'], ['distinct', 'Its own group']] as const).map(([v, label]) => (
          <label key={v} className="mr-3 text-(length:--sem-text-meta)"><input type="radio" name="blank" checked={blankPolicy === v} onChange={() => { setBlankPolicy(v); setSaveAttempted(false); }} /> {label}</label>
        ))}
        {blankPolicy === 'zero' && <div className="mt-1 text-(length:--sem-text-label)" style={{ color: 'var(--sem-muted)' }}>A blank counts as zero, the usual choice for a total. Change it if it should not.</div>}
        {!blankPolicy && <div className="mt-1 text-(length:--sem-text-label)" style={{ color: saveAttempted ? 'var(--sem-bad)' : 'var(--sem-muted)' }}>Pick one to continue.</div>}
      </div>
      <QueryTargetNote editing={editing} />
      <Advanced summary="tolerance, a complete DAX filter and a separate grand total query">
        <Field label="Tolerance">
          <ToleranceInputs abs={abs} onAbs={setAbs} relPercent={relPercent} onRelPercent={setRelPercent} />
        </Field>
        <Field label="Grand total SQL (optional)" hint="Use this when the grand total needs its own query.">
          <textarea value={sqlGrandTotal} onChange={(e) => setSqlGrandTotal(e.target.value)} rows={2} spellCheck={false} className="w-full rounded border px-2 py-1 font-mono text-(length:--sem-text-label)" style={inp} />
        </Field>
      </Advanced>
      <SaveRequirement kind="measureReconcile" />
      <Footer busy={busy}
        onTry={() => { if (refusal()) return; void tryDef(def(), setBusy, setErr, setTryNote); }}
        onSave={() => { if (refusal()) return; void nameNew(def()).then((d) => saveDef(d, setBusy, setErr, onSaved, onClose, before, onWritten && (() => onWritten(fields)))); }} />
    </>
  );
}

// ---------------------------------------------------------------------------------------------------
// Table row count. ONE identity with Choose source on the page: for a table count the mapping IS the check,
// so there is no saved-test kind to send and none is sent.
// ---------------------------------------------------------------------------------------------------
export function TableRowCountForm({ sources, adopt, table, busy, setBusy, setErr, onSaved, onClose, onAddSqlSource, addSource, linkedSourceId }: {
  sources: SqlSourceW[]; adopt?: string | null; table?: string; busy: boolean;
  setBusy: (v: boolean) => void; setErr: (v: string | null) => void; onSaved: () => void; onClose: () => void;
  onAddSqlSource?: () => void; addSource?: ReactNode;
  /** The source the open model is linked to: a table with no source of its own starts on it, as a check against SQL does. */
  linkedSourceId?: string | null;
}) {
  const [rows, setRows] = useState<TableMappingRowW[]>([]);
  // A mapping the person typed and did not save opens again as typed: by its table, or (New check > Table row count, no table
  // given) on the table that holds one.
  const [modelTable, setModelTable] = useState(() => table || heldMappingTable());
  const [held] = useState(() => (modelTable ? heldDraft<MappingFields>(mappingDraftScope(modelTable)) : undefined));
  const [pickedSourceId, setSqlSourceId] = useState(held?.value.sqlSourceId ?? '');
  const [schema, setSchema] = useState(held?.value.schema ?? '');
  const [entity, setEntity] = useState(held?.value.entity ?? '');
  const [loaded, setLoaded] = useState(false);
  const loadRows = () => rpc<TableMappingListW>('listTableMappings')
    .then((list) => { setRows(list.rows ?? []); setLoaded(true); })
    .catch(() => { setRows([]); setLoaded(true); });
  // The mapping can be changed from the other door (set_table_source_mapping) while this drawer is open, so the
  // rows are read again on every activity and the editor below decides what that does to what is typed.
  useEffect(() => { void loadRows(); return onActivity(() => { void loadRows(); }); }, []);
  // Filling in what the table already has is not a guess: it is the row the engine just sent for it. The guard
  // does it, on first read and whenever the table changes, and keeps it honest when the row moves under the person
  // (UAT-F-S06b-2-007: the drawer kept the old mapping after MCP changed it, and Save wrote the old one back).
  // A table with no source of its own starts on the model's source (or the only one saved). Shown, not stored: it makes no draft.
  const sqlSourceId = pickedSourceId || (modelTable ? defaultSqlSourceId(linkedSourceId, sources) : '');
  const mappingOf = (row?: TableMappingRowW) => ({ sqlSourceId: row?.sqlSourceId || '', schema: row?.schema || '', entity: row?.entity || '' });
  const readRow = useRef<TableMappingRowW | undefined>(undefined);   // the row the last Save read: what the stored mapping was when it did
  const guard = useEditBase<MappingFields>({
    current: loaded ? mappingOf(rows.find((r) => r.modelTable === modelTable)) : undefined,
    draft: { sqlSourceId: pickedSourceId, schema, entity }, editing: true, scope: modelTable,
    persist: modelTable ? { scope: mappingDraftScope(modelTable) } : undefined,
    reseed: (v) => { setSqlSourceId(v.sqlSourceId); setSchema(v.schema); setEntity(v.entity); },
    readNow: async () => { const row = ((await rpc<TableMappingListW>('listTableMappings')).rows ?? []).find((r) => r.modelTable === modelTable); readRow.current = row; return mappingOf(row); },
  });
  useEffect(() => { if (adopt) setSqlSourceId(adopt); }, [adopt]);
  const putBack = (v: MappingFields) => { setSqlSourceId(v.sqlSourceId); setSchema(v.schema); setEntity(v.entity); guard.rebase(v); };
  // The engine took the write (or the person removed the mapping): the draft for this table ends now, before the drawer closes.
  // R-B (drafts-sol F5): it ends only the words the save CARRIED. The boxes stay editable while the engine is busy, so what is typed when the answer comes may be newer: then it stays held.
  const nowRef = useRef({ sqlSourceId: pickedSourceId, schema, entity }); nowRef.current = { sqlSourceId: pickedSourceId, schema, entity };
  const ended = (sent?: MappingFields): boolean => {
    if (sent && !sameValue({ ...nowRef.current, schema: nowRef.current.schema.trim(), entity: nowRef.current.entity.trim() }, sent)) { guard.rebase(sent); return false; }
    guard.rebase({ sqlSourceId: pickedSourceId, schema, entity });
    const m = draftModel(); if (m && modelTable) draftStore.clear(m, mappingDraftScope(modelTable));
    return true;
  };
  const discard = () => {
    const v = loaded ? mappingOf(rows.find((r) => r.modelTable === modelTable)) : guard.baseNow();
    if (v) putBack(v);
    const m = draftModel(); if (m && modelTable) draftStore.clear(m, mappingDraftScope(modelTable));
  };
  const heldTables = draftModel() ? new Set([...draftStore.held(draftModel(), mappingDraftScope(''))].map((id) => (JSON.parse(id) as [string, string])[0].slice(mappingDraftScope('').length))) : new Set<string>();

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
      .then(() => { ended(); announceTestSuiteChanged(); onSaved(); onClose(); })
      .catch((e: unknown) => setErr(e instanceof Error ? e.message : String(e)))
      .finally(() => setBusy(false));
  };
  const save = async () => {
    if (!modelTable) { setErr('Pick the table to count.'); return; }
    if (!sqlSourceId) { setErr('Pick the SQL source that holds this table, or add one.'); return; }   // sqlSourceId: the person's pick, else the model's own source
    if (!schema.trim() || !entity.trim()) { setErr('Say which schema and which table in the source to count.'); return; }
    setBusy(true); setErr(null);
    try {
      // A Save that started from an older mapping than the live one writes nothing until the person chooses.
      if (!(await guard.beforeSave())) return;
      // The write names the STORED mapping the Save read (a source the model only detects is not a stored mapping, so it starts empty); the engine compares it inside its own write.
      const was = readRow.current?.source === 'mapping' ? readRow.current : undefined;
      const expected = JSON.stringify({ sqlSourceId: was?.sqlSourceId ?? '', schema: was?.schema ?? '', entity: was?.entity ?? '' });
      try { await rpc('setTableSourceMapping', modelTable, sqlSourceId, schema.trim(), entity.trim(), 'human', expected); }
      catch (e) { if (await guard.refused(e)) return; throw e; }
      const owns = ended({ sqlSourceId: pickedSourceId, schema: schema.trim(), entity: entity.trim() } as MappingFields);   // what the write carried (the engine was given the trimmed names)
      announceTestSuiteChanged(); onSaved(); if (owns) onClose();
    } catch (e) { setErr(e instanceof Error ? e.message : String(e)); }
    finally { setBusy(false); }
  };
  return (
    <>
      <Field label="Which table" hint="The model table whose rows you want counted.">
        <select value={modelTable} onChange={(e) => setModelTable(e.target.value)} className="w-full rounded border px-2 py-2 text-(length:--sem-text-meta)" style={inp}>
          <option value="">{loaded ? 'Pick a table' : 'Loading the tables…'}</option>
          {editableRows.map((row) => <option key={row.modelTable} value={row.modelTable}>{heldTables.has(row.modelTable) ? `${row.modelTable} (draft not saved)` : row.modelTable}</option>)}
        </select>
      </Field>
      {chosen && !chosen.canCount && chosen.reason && <div className="mb-3 text-(length:--sem-text-label)" style={{ color: 'var(--sem-muted)' }}>Right now, {chosen.reason}.</div>}
      <Field label="SQL source" hint="Saved once in Connections, then used by any check or table mapping.">
        <SqlSourceSelect sources={sources} value={sqlSourceId} onChange={setSqlSourceId} onAdd={onAddSqlSource} addForm={addSource} />
      </Field>
      <Field label="Schema" hint="The schema in the source that holds the table.">
        <input value={schema} onChange={(e) => setSchema(e.target.value)} placeholder="sales" className="min-h-7 w-full rounded border px-2 text-(length:--sem-text-meta)" style={inp} />
      </Field>
      <Field label="Table in the source" hint="The table name to count in that schema.">
        <input value={entity} onChange={(e) => setEntity(e.target.value)} placeholder="orders" className="min-h-7 w-full rounded border px-2 text-(length:--sem-text-meta)" style={inp} />
      </Field>
      <div className="sem-inset mb-3 text-(length:--sem-text-label)" style={{ color: 'var(--sem-muted)' }}>
        This runs when you run everything or choose table row counts. Equal counts pass. Different counts are
        reported as Counts differ, which is not proof of missing rows on its own.
      </div>
      <SaveRequirement kind="tableRowCount" />
      {guard.moved && <EditMovedBanner what="mapping" theirs={guard.theirs} onKeepMine={guard.keepMine} onUseNew={guard.useNew} disabled={busy} />}
      {draftRow(guard.drafted, discard, busy, 'mt-3')}
      <Footer busy={busy} onSave={() => void save()} saveLabel="Save check" />
      {chosen?.sqlSourceId && (
        <div className="mt-3 border-t pt-3" style={{ borderColor: 'var(--sem-border)' }}>
          <button type="button" disabled={busy} onClick={removeMapping}
            className="sem-btn sem-btn-sm">Remove source</button>
          <span className="mt-1 block text-(length:--sem-text-label)" style={{ color: 'var(--sem-muted)' }}>
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
    // Only the engine's own no-model error means there is nothing to ask; a slow or unreachable source says its own reason.
    setTryNote(/no open model/i.test(msg) ? "Can't try this now: no live model to ask." : msg);
  } finally { setBusy(false); }
}

async function saveDef(def: TestDefinitionW, setBusy: (v: boolean) => void, setErr: (v: string | null) => void, onSaved: () => void, onClose: () => void, before?: SaveFence, onWritten?: () => boolean | void) {
  if (!def.targetRef) { setErr('Pick a calculation.'); return; }
  setBusy(true); setErr(null);
  try {
    if (before && !(await before.before())) return;
    try { await rpc('saveTest', def, 'human', before?.expected() ?? null); }
    catch (e) { if (before && await before.refused(e)) return; throw e; }   // the check moved after the read: the banner asks, nothing was written
    const owns = onWritten ? onWritten() !== false : true;   // the engine took it: the draft ends, if it still holds what was sent (a refused or failed save keeps it)
    announceTestSuiteChanged();
    onSaved(); if (owns) onClose();
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
/** A source reconcile's title. A trusted answer's comes from checkTitle, which reads the check itself. A grouped check says its groups,
 *  so two checks of one calculation (the total and the one by Brand) never share a title. */
export function sqlTitleFor(measureRef: string, groupBy: string[] = []) {
  const name = (measureRef || '').split('/').pop()?.replace(/^measure:/, '') || 'Calculation';
  const groups = groupBy.map((g) => (/^column:[^/]+\/(.+)$/.exec(g)?.[1] ?? /\[([^\]]+)\]$/.exec(g)?.[1] ?? g).trim()).filter(Boolean);
  return `${name}${groups.length ? ' by ' + groups.join(' and ') : ''} ties to source SQL`;
}
