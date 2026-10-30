import { useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import { onCheckState, onDidChange, rpc, runHostCommand } from './bridge';
import { numberReason, relationshipsReason, type ChecksPageView } from './checks-page-model';
import { MarksHostContext, newDescriptionDrafts, type DescriptionDrafts, type MarksHost } from './marks-host';
import { openConsentCard } from './consent';
import { ADVICE_OFF, GLYPH, GROUP_WORDS, acceptStates, checksActionable, checksHeaderParts, checksVerdictLine, groupByState, noStates, stateMarks, type CheckCache, type CheckStates, type RowGroup, type State } from './marks-model';
import { MarkGlyph, MeasureMarksPanel } from './marks-panel';
import { RunPlan } from './run-plan';
import './marks.css';
import './checked.css';
import './checks-page.css';

/** The header: the title and the verdict line, 12 words or fewer (checksHeaderParts). A test counts these words. */
export function ChecksHeader({ verdict, problems, actionable, onTurnOn, tools }: { verdict: string; problems?: string; actionable?: string | null; onTurnOn?: () => void; tools?: ReactNode }) {
  const line = checksHeaderParts(verdict, problems);
  return <div className="va-head cp-head">
    <div className="va-verdict" data-testid="v-verdict" aria-live="polite">
      <h1>Checks</h1>
      <p className="va-sub" data-testid="checks-verdict">{line.verdict}{line.problems && <span className="va-bad">{line.verdict ? ' · ' : ''}{line.problems}</span>}{line.more > 0 && ` (+${line.more} more)`}</p>
      {actionable && <p className="va-sub" data-testid="checks-actionable">{actionable}
        {actionable === ADVICE_OFF && onTurnOn && <> <button type="button" className="va-link cp-turn-on" data-testid="checks-turn-on" onClick={onTurnOn}>Turn on</button></>}</p>}
    </div>
    <div className="va-tools" data-testid="v-head">{tools}</div>
  </div>;
}

const rowGlyph = (state: State | null) => state ? GLYPH[state] : '';
type ListRow = { id: string; name: string; state: State | null; detail?: ReactNode };

/** One row: a glyph, the name and a chevron. Nothing else is on a row at rest. */
export function CheckRow({ row, open, onOpen }: { row: ListRow; open: boolean; onOpen: () => void }) {
  const glyph = rowGlyph(row.state);
  return <div className="cp-rowwrap">
    <div className={`cp-row${open ? ' cp-selected' : ''}`} data-testid={`checks-row-${row.id}`}>
      <button type="button" className="cp-open" onClick={onOpen} aria-expanded={open}>
        <span className={`cp-glyph cp-s-${row.state ?? 'none'}`} aria-hidden="true"><MarkGlyph glyph={glyph} /></span><span className="cp-name">{row.name}</span>
        <span className="cp-chevron" aria-hidden="true">{open ? '⌄' : '›'}</span>
      </button>
    </div>
    {open && <div className="cp-detail">{row.detail}</div>}
  </div>;
}

/** The measures, grouped by verdict, worst first. "Not checked yet" is collapsed when another group exists. */
export function CheckGroups({ groups, open, onOpen, detail, collapsed, onToggle }: {
  groups: { group: RowGroup; state: State | null; rows: { ref: string; name: string; state: State | null }[] }[];
  open: string | null; onOpen: (id: string) => void; detail: (m: { ref: string; name: string }) => ReactNode; collapsed: boolean; onToggle: () => void;
}) {
  return <div className="cp-groups" data-testid="checks-measures">{groups.map(g => {
    const fold = g.group === 'unchecked' && groups.length > 1, shut = fold && collapsed;
    return <section key={g.group} className="cp-group" data-testid={`group-${g.group}`}>
      {fold ? <button type="button" className="cp-ghead" aria-expanded={!shut} onClick={onToggle}><span aria-hidden="true">{shut ? '›' : '⌄'}</span> {GROUP_WORDS[g.group]} <span className="cp-count">{g.rows.length}</span></button>
        : <h2 className="cp-ghead">{g.state && <span className={`cp-s-${g.state}`} aria-hidden="true">{GLYPH[g.state]}</span>} {GROUP_WORDS[g.group]} <span className="cp-count">{g.rows.length}</span></h2>}
      {!shut && <div className="cp-list">{g.rows.map(r => <CheckRow key={r.ref} row={{ id: r.ref, name: r.name, state: r.state, detail: detail(r) }} open={open === r.ref} onOpen={() => onOpen(r.ref)} />)}</div>}
    </section>;
  })}</div>;
}

export function ChecksPage({ page, busy = false, progress, onAdd, onConnect, onChanged, onSqlCheck, more, notices,
  renderNumber, renderTable, after }: {
  page: ChecksPageView; busy?: boolean; progress?: { done: number; total: number; note?: string } | null;
  onRun?: () => void; onAdd: (ref?: string) => void; onConnect: () => void; onChanged: () => void; onSqlCheck?: (ref: string) => void;
  more?: ReactNode | ((cleanup: { disabled: boolean; run: () => void }) => ReactNode); notices?: ReactNode; after?: ReactNode;
  renderNumber?: (id: string) => ReactNode; renderTable?: (id: 'relationships' | 'rowcounts') => ReactNode;
}) {
  const [open, setOpen] = useState<string | null>(null);
  const [collapsed, setCollapsed] = useState(true);
  const descriptionDrafts = useMemo<DescriptionDrafts>(newDescriptionDrafts, [page.sessionId]);
  const [cleaning, setCleaning] = useState(false);
  const [note, setNote] = useState('');
  // The engine's check state, newest version per measure. The first read, every push and every run result go through one rule,
  // so the header, the rows and the card render one state and a late answer never replaces a newer one (ra1 section 2).
  const [states, setStates] = useState<CheckStates>(noStates);
  // Turn on was pressed and the permission card said yes: the advice line goes at once; the next check scores the measures.
  const [adviceOn, setAdviceOn] = useState(false);
  useEffect(() => setAdviceOn(false), [page.sessionId]);
  const accept = (incoming: unknown) => setStates(held => acceptStates(held, incoming) ?? held);
  useEffect(() => {
    setStates(noStates());
    void rpc<CheckCache[]>('getMeasureCheckCache').then(accept).catch(() => { /* the push fills the rows */ });
    return onCheckState(accept);
  }, [page.sessionId]);
  // What stops scoring now, from the page read: the cards show it too, before any mark has run (wr1 F1).
  const unscored = adviceOn && page.unscored === ADVICE_OFF ? null : page.unscored ?? null;
  const host = useMemo<MarksHost>(() => ({
    descriptionDrafts, unscored,
    rpc: (method, ...args) => rpc(method, ...args),
    onChange: fn => onDidChange(() => fn()),
    onCheckState: fn => onCheckState(fn),
    open: (where, ref, then) => { if (where === 'advice') openConsentCard(() => { setAdviceOn(true); then?.(); });
      else if (where === 'checks' && ref) (onSqlCheck ?? onAdd)(ref); else onConnect(); },
    undo: () => runHostCommand('semanticus.undo'),
    editFormula: ref => runHostCommand('semanticus.editDax', ref, page.sessionId),
  }), [onAdd, onConnect, onSqlCheck, descriptionDrafts, page.sessionId, unscored]);
  const clean = async () => {
    setCleaning(true); setNote('');
    try {
      const result = await rpc<{ note: string }>('cleanupMeaningAnnotations');
      setNote(result.note); onChanged();
    } catch (error) { setNote(error instanceof Error ? error.message : 'The old annotations could not be removed.'); }
    finally { setCleaning(false); }
  };
  const definitions = [...page.numbers.definitions].sort((a, b) => a.title.localeCompare(b.title));
  const relationship = relationshipsReason(page.relationships?.relationships ?? []);
  const counted = page.tables.rows.filter(r => r.lastModelCount != null).length;
  const problems = [page.summary.numbersDiffer > 0 && `${page.summary.numbersDiffer} number${page.summary.numbersDiffer === 1 ? ' differs' : 's differ'}`,
    page.summary.keyProblems > 0 && `${page.summary.keyProblems} key problem${page.summary.keyProblems === 1 ? '' : 's'}`,
    page.summary.tableProblems > 0 && `${page.summary.tableProblems} table count${page.summary.tableProblems === 1 ? ' differs' : 's differ'}`].filter(Boolean).join(' · ');
  const rows = page.measures.map(m => {
    const state = states.get(m.ref);
    return { ref: m.ref, name: m.name, marks: stateMarks(state), match: m.descriptionMatch, checked: !!state?.lastChecked };
  });
  const groups = groupByState(rows);
  const actionable = checksActionable(page.unscored, rows, adviceOn);
  // The open card stays in view when its measure moves to another group (wr1 S2): the page keeps its scroll, so the card
  // would end above the view, under the sticky tabs.
  const openGroup = groups.find(g => g.rows.some(r => r.ref === open))?.group;
  const opened = useRef<{ ref: string | null; group?: RowGroup }>({ ref: null });
  useEffect(() => {
    const was = opened.current;
    opened.current = { ref: open, group: openGroup };
    if (!open || !openGroup || was.ref !== open || !was.group || was.group === openGroup) return;
    document.querySelector(`[data-testid="checks-row-${CSS.escape(open)}"]`)?.scrollIntoView({ block: 'start' });
  }, [open, openGroup]);
  const tools = <>{typeof more === 'function' ? more({ disabled: cleaning || busy, run: () => void clean() }) : more}</>;
  const numberState = (id: string): State | null => {
    const verdict = page.results[id]?.verdict;
    return !definitions.find(d => d.id === id)?.enabled ? null : /^(Fail|Suspect)$/.test(verdict ?? '') ? 'broken' : verdict === 'Pass' ? 'verified' : null;
  };
  const plain = (state: State | null, reason: string, body?: ReactNode) => <><p className="mk-say">{reason}</p>{body}</>;
  const detail = (m: { ref: string; name: string }) => open === m.ref ? <MeasureMarksPanel reference={m.ref} name={m.name} /> : null;
  const row = (id: string, name: string, state: State | null, body: ReactNode) => <CheckRow key={id} row={{ id, name, state, detail: body }} open={open === id} onOpen={() => setOpen(open === id ? null : id)} />;
  return <MarksHostContext.Provider value={host}><div className="cp-page" data-testid="checks-page">
    <ChecksHeader verdict={checksVerdictLine(groups, page.summary.total)} problems={problems} actionable={actionable} onTurnOn={() => host.open('advice')} tools={tools} />
    {note && <p role="status">{note}</p>}{notices}
    <RunPlan measures={page.measures.map(m => ({ ref: m.ref, name: m.name, table: m.table, folder: m.folder }))} onRan={accept} progress={busy ? progress : null} />
    <CheckGroups groups={groups} open={open} onOpen={id => setOpen(open === id ? null : id)} detail={detail} collapsed={collapsed} onToggle={() => setCollapsed(!collapsed)} />
    <div className="cp-section"><h2>Numbers you trust</h2><button type="button" className="va-link cp-add" onClick={() => onAdd()}>Add a number you trust</button></div>
    <div className="cp-list">{definitions.map(d => row(`number:${d.id}`, d.title, numberState(d.id),
      open === `number:${d.id}` ? plain(numberState(d.id), numberReason(page.results[d.id], d.enabled), renderNumber?.(d.id)) : null))}
      {definitions.length === 0 && <p className="cp-empty">No numbers saved yet.</p>}</div>
    <div className="cp-section"><h2>Tables and relationships</h2></div>
    <div className="cp-list">
      {row('relationships', 'Relationships', relationship.tone === 'wrong' ? 'review' : relationship.tone === 'checked' ? 'verified' : null,
        open === 'relationships' ? plain(null, relationship.reason, renderTable?.('relationships')) : null)}
      {row('rowcounts', 'Table rows', page.summary.tableProblems ? 'review' : counted ? 'verified' : null, open === 'rowcounts' ? plain(null,
        page.summary.tableProblems ? `${page.summary.tableProblems} table counts differ` : counted ? `Counted in the model: ${counted} tables` : 'Not counted yet',
        <>{renderTable?.('rowcounts')}<div className="cp-optional"><span>Compare the row counts with your source, if you have one.</span><button type="button" className="va-link cp-add" onClick={onConnect}>Connect a source</button></div></>) : null)}
    </div>
    {after}
  </div></MarksHostContext.Provider>;
}
