import { useEffect, useMemo, useRef, useState } from 'react';
import { rpc, onDidChange, loadState, saveState, selectInProperties, focusSelectInProperties } from './bridge';
import { RevealBtn, rowKeyProps } from './objectactions';
import { uiLabel } from './copy';

// Wire types mirror Semanticus.Engine/Protocol.cs (SearchResult / SearchHit / SearchSpan / FacetCount), camelCase.
type Span = { start: number; len: number };
type Hit = {
  ref: string; kind: string; name: string; table?: string;
  field: string; matchClass: string; snippet?: string; spans: Span[];
  replaceable: boolean; replaceHint?: string; context?: string;
};
type Facet = { key: string; count: number };
type SearchResultT = {
  query: string; hits: Hit[]; total: number; truncated: boolean; offset: number; error?: string;
  byField: Facet[]; byMatchClass: Facet[]; byKind: Facet[];
};
// replace_in_object result (preview or apply) — mirrors ReplaceResult in Protocol.cs.
type ReplaceRes = {
  changed: boolean; preview?: boolean; blocked?: string; before?: string; after?: string;
  replacements: number; warnings?: string[]; note?: string; references?: number; matchClass?: string;
};
// propose_replace result — the slice of ChangePlanView the hand-off needs.
type PlanViewLite = { items: { id: string }[]; note?: string };

// Analyst-safe fields (human-facing text) are on by default; the power fields (formulas / M / security) are behind an
// explicit, informed toggle so an analyst never accidentally rewrites a formula — the "powerful but safe" default.
const SAFE_FIELDS = ['name', 'description', 'displayFolder', 'formatString'];
const POWER_FIELDS = ['expression', 'mExpression', 'rlsFilter', 'synonyms'];

// "Namedexpression" is an engine kind name; the model calls these parameters and shared queries.
const kindLabel = (kind: string) => (kind === 'namedexpression' ? 'Parameter' : uiLabel(kind));

// Human labels — the UI never speaks engine jargon (spec §10.12).
const FIELD_LABEL: Record<string, string> = {
  name: 'Name', description: 'Description', displayFolder: 'Display folder', formatString: 'Format string',
  expression: 'DAX', mExpression: 'M / query', rlsFilter: 'Security filter', synonyms: 'Synonyms',
};
// Each MatchClass group teaches its own replace semantics — the grouping IS the safety lesson (§6.3).
const CLASS_META: Record<string, { label: string; note: string; order: number }> = {
  ObjectName:   { label: 'Object names',   note: 'References update too.', order: 0 },
  PlainText:    { label: 'Text',           note: '', order: 1 },
  DaxLiteral:   { label: 'DAX text',       note: 'Text inside a string literal: changes what the formula returns.', order: 2 },
  DaxComment:   { label: 'DAX comments',   note: '', order: 3 },
  MExpression:  { label: 'M / query text', note: 'M is not fixed for you. Check it still works.', order: 4 },
  DaxReference: { label: 'References', note: 'Rename the object to change these.', order: 5 },
  DaxCode:      { label: 'Formula code', note: 'Edit the formula to change these.', order: 6 },
};

// The hit kinds whose object refs the selection bus + Reveal-in-tree can actually reach — the same kinds the
// engine's ObjectRefs.Resolve resolves and the Model tree carries. A 'namedexpression' hit (ref 'namedexpr:…')
// resolves in NEITHER, so wiring select/reveal on it would blank the grid and dead-end the reveal; those hits
// stay find-and-replace-only (Replace still works — M replacement goes through a separate path). Kept in sync
// with the host's SELECTABLE_REF_KINDS allowlist (extension.ts), which rejects anything else at the door.
const NAVIGABLE_KINDS = new Set(['measure', 'column', 'table', 'hierarchy', 'calcitem', 'partition', 'function', 'role', 'perspective']);

// Result-type filter chips. Each chip is a PREDICATE over a hit: "the measure", "the column", "the table".
// The where-it-matched chips (DAX, M, Descriptions) are gone: the group cards right under the chips already
// split the results that way, so a second set of buttons said the same thing twice. Selected chips UNION. The
// engine already accepts kinds[] in SearchOptions, but every hit carries kind, so filtering the returned list
// client-side is instant and needs no re-query.
const TYPE_CHIPS: { key: string; label: string; match: (h: Hit) => boolean }[] = [
  { key: 'measures', label: 'Measures', match: (h) => h.kind === 'measure' },
  { key: 'columns', label: 'Columns', match: (h) => h.kind === 'column' },
  { key: 'tables', label: 'Tables', match: (h) => h.kind === 'table' },
];
// The honest remainder chip: a hit no kind chip claims (hierarchy, role and partition names, parameters, ...).
const isOtherHit = (h: Hit) => !TYPE_CHIPS.some((c) => c.match(h));

export function SearchView({ sessionId, navQuery, onNavConsumed, onOpenPlan }: {
  // `regex` is set when the query is a pattern that matches several names at once (the Plan's collisions row hands them over).
  sessionId: string; navQuery?: { query: string; scope?: string; regex?: boolean; nonce: number } | null;
  onNavConsumed?: (nonce: number) => void; onOpenPlan?: () => void;
}) {
  // Query state is persisted so a round-trip through another tab (e.g. reviewing a Replace-all plan on the
  // Change Plan tab) comes back to the same search, not a blank box.
  const [find, setFindRaw] = useState(() => loadState<string>('searchFind', ''));
  const [replace, setReplaceRaw] = useState(() => loadState<string>('searchReplace', ''));
  const [caseSensitive, setCase] = useState(() => loadState<boolean>('searchCase', false));
  const [wholeWord, setWord] = useState(() => loadState<boolean>('searchWord', false));
  const [regex, setRegex] = useState(() => loadState<boolean>('searchRegex', false));
  const [includePower, setPowerRaw] = useState(() => loadState<boolean>('searchPower', false));
  // ANY input that feeds a replace (find text, replacement, match modes, the field set) invalidates an open
  // preview panel — it showed a change computed from the OLD inputs. Belt and braces: Apply also re-sends the
  // exact args captured when the preview was taken (see previewReplace), so a click that races an input edit
  // (or the search debounce) can never apply something different from what the panel showed.
  const setFind = (v: string) => { setFindRaw(v); saveState('searchFind', v); setPreview(null); };
  const setReplace = (v: string) => { setReplaceRaw(v); saveState('searchReplace', v); setPreview(null); };
  const setPower = (v: boolean) => { setPowerRaw(v); saveState('searchPower', v); setPreview(null); };
  const setMode = (setter: (v: boolean) => void) => (v: boolean) => { setter(v); setPreview(null); };
  useEffect(() => { saveState('searchCase', caseSensitive); saveState('searchWord', wholeWord); saveState('searchRegex', regex); }, [caseSensitive, wholeWord, regex]);
  const [result, setResult] = useState<SearchResultT | null>(null);
  const [err, setErr] = useState<string | null>(null);
  const [note, setNote] = useState<string | null>(null);
  // The open preview-before-apply panel: which row, what the engine said the change WOULD do, and the exact
  // request args the preview was computed from (Apply re-sends THESE, never re-reads live input state).
  const [preview, setPreview] = useState<{ key: string; hit: Hit; res: ReplaceRes; args: ReturnType<typeof replaceArgs> } | null>(null);
  const [busyAll, setBusyAll] = useState(false);
  // Selected type-filter chips (empty = All). Persisted like other tab state so the filter survives hide/show.
  const [typeFilter, setTypeFilter] = useState<string[]>(() => loadState<string[]>('searchTypeFilter', []));
  const scopeKey = 'searchScope.' + sessionId;
  const [scope, setScopeRaw] = useState<string | null>(() => loadState<string | null>(scopeKey, null));
  const setScope = (value: string | null) => { setScopeRaw(value); saveState(scopeKey, value); setPreview(null); };
  useEffect(() => { saveState('searchTypeFilter', typeFilter); }, [typeFilter]);
  const timer = useRef<number | undefined>(undefined);
  const findRef = useRef<HTMLInputElement | null>(null);
  const runSeq = useRef(0);   // latest-wins: a slower earlier search must not overwrite a newer query's results

  const fields = useMemo(() => (includePower ? [...SAFE_FIELDS, ...POWER_FIELDS] : SAFE_FIELDS), [includePower]);

  // Seed from the findInModel quick-pick "Open in Search & Replace" hand-off — and ALWAYS focus the find box:
  // an empty query is the Ctrl+F "just take me to search" gesture (select-all so a new search types over the old).
  // Focus/select on the NEXT frame, not in this tick: setFind's re-render commits the new controlled value into the
  // input AFTER a same-tick select(), collapsing the selection on non-empty hand-offs. rAF runs after React flushes
  // the update (before paint), so the select-all sticks.
  useEffect(() => {
    if (!navQuery) return;
    if (navQuery.query) setFind(navQuery.query);
    if (navQuery.regex != null && navQuery.query) { setRegex(navQuery.regex); setPreview(null); }
    setScope(navQuery.scope ?? null);
    onNavConsumed?.(navQuery.nonce);
    const raf = requestAnimationFrame(() => { findRef.current?.focus(); findRef.current?.select(); });
    return () => cancelAnimationFrame(raf);
  }, [navQuery?.nonce]);

  async function run() {
    const q = find.trim();
    const seq = ++runSeq.current;   // bump BEFORE the clear-path return too, so clearing the box invalidates any in-flight search (it must not resurrect results)
    if (q.length < 2) { setResult(null); setErr(null); return; }
    try {
      const r = await rpc<SearchResultT>('searchModelEx', { query: q, max: 500, caseSensitive, wholeWord, regex, fields });
      if (seq !== runSeq.current) return;   // a newer query/toggle superseded this search — drop its (possibly slower) result
      setResult(r); setErr(r?.error ?? null);
      setPreview(null);   // fresh results: any open preview panel is stale
    } catch (e) { if (seq === runSeq.current) setErr(String((e as Error).message ?? e)); }
  }

  // Debounced live search on any input change, and re-run when the model changes (a replace elsewhere, undo, etc.).
  useEffect(() => {
    window.clearTimeout(timer.current);
    timer.current = window.setTimeout(() => void run(), 250);
    return () => window.clearTimeout(timer.current);
  }, [find, caseSensitive, wholeWord, regex, includePower]);
  useEffect(() => onDidChange(() => { window.clearTimeout(timer.current); timer.current = window.setTimeout(() => void run(), 300); }), [find, caseSensitive, wholeWord, regex, includePower]);

  function replaceArgs(h: Hit) {
    const span = h.spans?.length ? h.spans[0] : null;   // one-by-one: replace the specific occurrence
    return { ref: h.ref, field: h.field, find: find.trim(), replace, caseSensitive, wholeWord, regex, span, culture: h.context };
  }

  // Step 1 of the one-by-one replace: ask the engine what WOULD change (nothing mutates), open the panel.
  // The args are captured HERE and stored with the preview — Apply re-sends them verbatim.
  async function previewReplace(h: Hit, key: string) {
    setNote(null); setErr(null);
    if (preview?.key === key) { setPreview(null); return; }   // second click folds the panel
    try {
      const args = replaceArgs(h);
      const res = await rpc<ReplaceRes>('replaceInObject', { ...args, preview: true }, 'human');
      setPreview({ key, hit: h, res, args });
    } catch (e) { setErr(String((e as Error).message ?? e)); }
  }

  // Step 2: apply exactly what the panel showed — the args captured at preview time, never current state.
  async function applyReplace() {
    if (!preview) return;
    setNote(null); setErr(null);
    try {
      const res = await rpc<ReplaceRes>('replaceInObject', preview.args, 'human');
      const warn = res?.warnings?.length ? ' ' + res.warnings.join(' ') : '';
      setNote((res?.changed ? 'Replaced.' : (res?.note ?? 'Nothing to replace.')) + warn);
      setPreview(null);
      // onDidChange re-runs the search automatically.
    } catch (e) { setErr(String((e as Error).message ?? e)); }
  }

  // Replace all: build a reviewable Change Plan from every match of the CURRENT query + field set (free),
  // then hand off to the Change Plan tab to review and apply. Nothing changes until Apply there.
  async function replaceAll() {
    setNote(null); setErr(null); setBusyAll(true);
    try {
      const view = await rpc<PlanViewLite>('proposeReplace',
        { query: find.trim(), caseSensitive, wholeWord, regex, fields }, replace, 500, 'human');
      const n = view?.items?.length ?? 0;
      if (n === 0) setNote('No replaceable matches.' + (view?.note ? ' ' + view.note : ''));
      else onOpenPlan?.();
    } catch (e) { setErr(String((e as Error).message ?? e)); }
    finally { setBusyAll(false); }
  }

  const scopeTable = scope?.startsWith('table:') ? scope.slice('table:'.length) : null;
  const scopedHits = useMemo(() => (result?.hits ?? []).filter((hit) => !scopeTable || hit.table === scopeTable || hit.ref === scope), [result, scope, scopeTable]);
  // Per-chip match counts over the returned hits in the current object scope.
  const chipCounts = useMemo(() => {
    const counts: Record<string, number> = { other: 0 };
    for (const c of TYPE_CHIPS) counts[c.key] = 0;
    for (const h of scopedHits) {
      for (const c of TYPE_CHIPS) if (c.match(h)) counts[c.key]++;
      if (isOtherHit(h)) counts.other++;   // same predicate the filter uses — the count is exactly what the chip shows
    }
    return counts;
  }, [scopedHits]);

  const visibleHits = useMemo(() => {
    const hits = scopedHits;
    if (typeFilter.length === 0) return hits;
    const sel = new Set(typeFilter);
    return hits.filter((h) => TYPE_CHIPS.some((c) => sel.has(c.key) && c.match(h)) || (sel.has('other') && isOtherHit(h)));
  }, [scopedHits, typeFilter]);

  const toggleType = (key: string) => setTypeFilter((cur) => (cur.includes(key) ? cur.filter((k) => k !== key) : [...cur, key]));

  const groups = useMemo(() => {
    const g = new Map<string, Hit[]>();
    for (const h of visibleHits) { const a = g.get(h.matchClass) ?? []; a.push(h); g.set(h.matchClass, a); }
    return [...g.entries()].sort((a, b) => (CLASS_META[a[0]]?.order ?? 99) - (CLASS_META[b[0]]?.order ?? 99));
  }, [visibleHits]);

  return (
    // A reading list, so it keeps the centred measure under the space rule (data-readable says so to the space check).
    <div className="sem-page sem-centered-page" data-readable>
      {/* Query bar */}
      {scopeTable && <div className="search-scope"><span>Searching in table <strong>{scopeTable}</strong></span><button type="button" className="sem-btn sem-btn-sm sem-btn-quiet" onClick={() => setScope(null)}>Search all tables</button></div>}
      <div className="sem-card sem-stack">
        <div className="sem-inline">
          <input ref={findRef} autoFocus value={find} onChange={(e) => setFind(e.target.value)} placeholder="Find in names, descriptions, folders…"
            aria-label="Find" title="Type at least 2 characters. Tick Include DAX & M to search formulas and query code." className="sem-field xe-grow" />
          {/* These said "Aa", "\b" and ".*". Two of the three were bare regex tokens with nothing a person
              could read, so they say what they do now and share the app's one selected-state look. */}
          <div className="sem-seg shrink-0 flex-wrap" role="group" aria-label="How to match what you typed">
            <button type="button" className="sem-seg-item" aria-pressed={caseSensitive} onClick={() => setMode(setCase)(!caseSensitive)} title="Match case: treat capitals and lower case as different.">Match case</button>
            <button type="button" className="sem-seg-item" aria-pressed={wholeWord} onClick={() => setMode(setWord)(!wholeWord)} title="Whole word: only match the word on its own, not inside a longer word.">Whole word</button>
            <button type="button" className="sem-seg-item" aria-pressed={regex} onClick={() => setMode(setRegex)(!regex)} title="Regular expression: read what you typed as a search pattern.">Regular expression</button>
          </div>
        </div>
        <div className="sem-inline">
          <input value={replace} onChange={(e) => setReplace(e.target.value)} placeholder="Replace with…"
            aria-label="Replace with" className="sem-field xe-grow" />
          <label className="sem-inline sem-text-meta whitespace-nowrap" title="Also search DAX formulas, M/query code and security filters (power-user fields)">
            <input type="checkbox" checked={includePower} onChange={(e) => setPower(e.target.checked)} /> Include DAX &amp; M
          </label>
          <button onClick={() => void replaceAll()} disabled={busyAll || !!scopeTable || find.trim().length < 2 || !result || result.total === 0}
            title={scopeTable ? 'Search all tables before planning a model-wide replacement.' : 'Plans every match of this search as a reviewable change list. Nothing changes until you apply it there.'}
            className="sem-btn">
            {busyAll ? 'Planning…' : 'Replace all…'}
          </button>
        </div>
      </div>

      {err && <div className="xe-error" role="alert">{regex ? 'Search: ' : ''}{err}</div>}
      {note && <div className="sem-text-meta" role="status">{note}</div>}

      {/* Filter by result type — multi-select chips with counts; All resets. */}
      {result && scopedHits.length > 0 && (
        <div className="sem-inline flex-wrap">
          <span className="sem-text-meta">
            {scopeTable ? `${scopedHits.length} shown in ${scopeTable}` : `${result.total} match${result.total === 1 ? '' : 'es'}${result.truncated ? ' (showing the first ' + result.hits.length + ')' : ''}`}
          </span>
          <FilterChip on={typeFilter.length === 0} onClick={() => setTypeFilter([])}>All</FilterChip>
          {TYPE_CHIPS.filter((c) => chipCounts[c.key] > 0 || typeFilter.includes(c.key)).map((c) => (
            <FilterChip key={c.key} on={typeFilter.includes(c.key)} onClick={() => toggleType(c.key)}>{c.label} {chipCounts[c.key]}</FilterChip>
          ))}
          {(chipCounts.other > 0 || typeFilter.includes('other')) && (
            <FilterChip on={typeFilter.includes('other')} onClick={() => toggleType('other')}>Other {chipCounts.other}</FilterChip>
          )}
        </div>
      )}

      {/* Results grouped by MatchClass */}
      {find.trim().length < 2 ? null : !result || result.total === 0 ? (
        <Hint>{err ? '' : `No matches for “${find.trim()}”. Try turning off whole-word, or Include DAX & M.`}</Hint>
      ) : scopedHits.length === 0 ? (
        <Hint>No matches for “{find.trim()}” in {scopeTable}. Search all tables to see the other results.</Hint>
      ) : visibleHits.length === 0 ? (
        <Hint>All {scopedHits.length} matches are hidden by the type filter above. Click All to show every match.</Hint>
      ) : (
        groups.map(([cls, hits]) => (
          <div key={cls} className="sem-card sem-card-flush">
            <div className="sem-card-head">
              <span className="sem-card-title">{CLASS_META[cls]?.label ?? cls} <span className="sem-num sem-text-meta">{hits.length}</span></span>
              {CLASS_META[cls]?.note ? <span className="sem-text-meta">{CLASS_META[cls]?.note}</span> : null}
            </div>
            <div className="flex flex-col">
              {hits.map((h, i) => {
                const key = h.ref + '|' + h.field + '|' + i;
                const navigable = NAVIGABLE_KINDS.has(h.kind);   // can the bus/Reveal reach this ref? (namedexpr can't)
                return (
                  <div key={key} data-object-ref={h.ref} className="xe-rule-row">
                    <div className={'group sem-list-row' + (h.field === 'name' ? '' : ' sem-list-row-2')}>
                      {/* Selection bus: clicking a hit shows its object in the Properties view (focus stays here).
                          Only for kinds the grid + tree can resolve — otherwise the row is find-and-replace-only. */}
                      {/* The match text stops at a reading width, so Replace sits beside the match, not a screen away. */}
                      <div className={'flex-1 min-w-0 sem-readable' + (navigable ? ' cursor-pointer' : '')}
                        onClick={navigable ? () => selectInProperties(h.ref) : undefined}
                        {...(navigable ? rowKeyProps(() => selectInProperties(h.ref), () => focusSelectInProperties(h.ref)) : {})}
                        title={navigable ? 'Show in Properties' : undefined}>
                        <div className="truncate">
                          <span className="sem-text-meta">{kindLabel(h.kind)}{h.table ? ' · ' + h.table : ''}{h.context && h.field === 'rlsFilter' ? ' · ' + h.context : ''} · </span>
                          {h.name}
                          {h.field !== 'name' && <span className="ml-1 sem-text-meta">({FIELD_LABEL[h.field] ?? h.field})</span>}
                        </div>
                        {/* A name hit prints the name once: the bold name above IS the match, so no second line. */}
                        {h.field !== 'name' && <div className="sem-text-meta sem-num truncate">{highlight(h.snippet ?? '', find, regex)}</div>}
                      </div>
                      {/* An EMPTY replacement is legitimate (it deletes the matched text — e.g. clearing part of a
                          description or folder), so the button never dead-ends on it; the one case that can't work
                          (a name that would become empty) is surfaced by the preview panel's plain refusal. */}
                      {/* Every row keeps the Reveal button's room, shown or not, so Replace stands at the same place on every row. */}
                      {navigable
                        ? <RevealBtn objRef={h.ref} className="invisible group-hover:visible group-focus-within:visible" />
                        : <span aria-hidden="true" className="invisible"><RevealBtn objRef={h.ref} /></span>}
                      {h.replaceable
                        ? <button onClick={() => void previewReplace(h, key)}
                            title="Preview this change before it happens"
                            aria-pressed={preview?.key === key}
                            className="sem-btn sem-btn-sm">Replace</button>
                        : (h.matchClass === 'DaxReference' || h.matchClass === 'DaxCode'
                          ? null   // the group heading already says these are changed elsewhere, so no per-row tag
                          : <span className="sem-text-meta max-w-[220px] text-right" title={h.replaceHint}>read-only</span>)}
                    </div>
                    {preview?.key === key && (
                      <PreviewPanel res={preview.res} isRename={h.field === 'name'}
                        onApply={() => void applyReplace()} onCancel={() => setPreview(null)} />
                    )}
                  </div>
                );
              })}
            </div>
          </div>
        ))
      )}
    </div>
  );
}

// The preview-before-apply panel: what the engine says this one replace WOULD do — before and after, the blast
// radius for a rename, any warnings — with Apply/Cancel. Nothing has changed yet when this renders.
function PreviewPanel({ res, isRename, onApply, onCancel }: { res: ReplaceRes; isRename: boolean; onApply: () => void; onCancel: () => void }) {
  const noOp = !res.blocked && (res.replacements ?? 0) === 0;
  return (
    <div className="sem-inset sem-stack xe-preview">
      {res.blocked ? (
        <div className="xe-error">{res.blocked}</div>
      ) : noOp ? (
        <div className="sem-text-meta">Nothing to replace here any more (the text may have just changed).</div>
      ) : (
        <>
          <div className="sem-text-meta sem-num flex items-start gap-2 flex-wrap">
            <span style={{ color: 'var(--sem-muted)', textDecoration: 'line-through' }}>{clipPair(res.before, res.after)[0]}</span>
            <span>→</span>
            <span style={{ color: 'var(--sem-fg)' }}>{clipPair(res.before, res.after)[1] || '(empty)'}</span>
          </div>
          {isRename && (
            <div className="sem-text-meta">
              {(res.references ?? 0) > 0
                ? `Also updates ${res.references} place${res.references === 1 ? '' : 's'} that use this name in formulas, automatically.`
                : 'Nothing in the model references this name in a formula.'}
            </div>
          )}
          {res.note && <div className="sem-text-meta">{res.note}</div>}
          {(res.warnings ?? []).map((w, i) => (
            <div key={i} className="sem-chip sem-chip-warn">{w}</div>
          ))}
        </>
      )}
      <div className="sem-inline">
        {!res.blocked && !noOp && (
          <button onClick={onApply} className="sem-btn sem-btn-sm sem-btn-primary">{isRename ? 'Rename' : 'Apply'}</button>
        )}
        <button onClick={onCancel} className="sem-btn sem-btn-sm">Cancel</button>
      </div>
    </div>
  );
}

// Long field values (a whole DAX body, an M query) are windowed around the FIRST point of difference so the
// changed part is always visible; the change plan diff shows full values.
function clipPair(before: string | undefined | null, after: string | undefined | null): [string, string] {
  const b = before ?? '', a = after ?? '';
  const MAX = 220;
  if (b.length <= MAX && a.length <= MAX) return [b, a];
  let d = 0;
  while (d < b.length && d < a.length && b[d] === a[d]) d++;
  const from = Math.max(0, d - 60);
  const win = (s: string) => (from > 0 ? '…' : '') + s.slice(from, from + MAX) + (from + MAX < s.length ? '…' : '');
  return [win(b), win(a)];
}

// Best-effort highlight of the matched substring within the (flattened) snippet. Skipped for regex mode (the snippet
// offsets don't map to the pattern) — the snippet still shows the context window.
function highlight(snippet: string, find: string, regex: boolean) {
  const q = find.trim();
  if (!q || regex) return snippet;
  const parts: React.ReactNode[] = [];
  let i = 0, from = 0, key = 0;
  const lower = snippet.toLowerCase(), needle = q.toLowerCase();
  while ((i = lower.indexOf(needle, from)) >= 0) {
    if (i > from) parts.push(snippet.slice(from, i));
    parts.push(<mark key={key++} style={{ background: 'var(--sem-accent-soft)', color: 'var(--sem-fg)' }}>{snippet.slice(i, i + q.length)}</mark>);
    from = i + q.length;
  }
  parts.push(snippet.slice(from));
  return <>{parts}</>;
}

function FilterChip({ children, on, onClick }: { children: React.ReactNode; on: boolean; onClick: () => void }) {
  return (
    <button onClick={onClick} aria-pressed={on} className="sem-seg-item">
      {children}
    </button>
  );
}
function Hint({ children }: { children: React.ReactNode }) {
  return <div className="sem-text-meta">{children}</div>;
}
