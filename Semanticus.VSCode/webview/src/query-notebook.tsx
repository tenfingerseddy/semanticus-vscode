// The Query notebook (1.6, Kane 16 Oct, option C): each question is one row in a column, newest at the top. The open row holds the page's live
// state (the number, how it was read, the visual, and every tool on demand); the other rows are folded to a line. A row can be pinned (it
// stays at the top and survives a reopen), closed (it leaves the notebook and stays in the history), or removed from the history.
// Everything is kept by the engine beside the model (queryNotebook), so the person's assistant reads and changes the same rows.
import { useEffect, useMemo, useRef, useState } from 'react';
import { rpc } from './bridge';
import { usePersistedState } from './hooks';
import { useClaudeReflection } from './activity';
import { useConnection } from './connection';
import { useFeature } from './pro';
import { OUTCOME_WORD } from './askdata';
import type { QueryNotebookView } from './askdata-model';
import type { ResultSet } from './wire';
import type { ColdWarmBenchmark, useQueryPage } from './daxlab';
import {
  SCOPES, SCOPE_WORD, ago, assistantRowFor, figureOf, historyRows, isProbe, isRowCount, isDaxRow, measuredTimes, noteFor, notebookRows, rowOwnsResult, rowText, scopeCounts, TIME_NOTE_MS, timeScope, timingNote, type Rec, type TimeScope, saidFor } from './query-notebook-model';
import { DRAFT, Icon, Pop } from './query-notebook-parts';
export { DRAFT, Icon, Pop, RowLine } from './query-notebook-parts';

type Q = ReturnType<typeof useQueryPage>;
export type Tab = 'answer' | 'dax' | 'table' | 'speed' | 'steps' | 'compare' | 'save' | 'explain';
export const TABS: Tab[] = ['answer', 'dax', 'table', 'speed', 'steps', 'compare', 'save'];
export const TAB_NAME: Record<Tab, string> = { answer: 'Answer', dax: 'DAX', table: 'Table', speed: 'Speed', steps: 'Steps', compare: 'Compare', save: 'Save', explain: 'Explain' };
export interface Timing { scope: TimeScope; i: number; n: number; text: string; stopping: boolean }

export interface Notebook {
  /** Every row of the model's history, newest first (the engine's order). */
  all: Rec[];
  /** The rows the notebook shows, pinned first, with the draft row on top while a question or a new query is being worked on. */
  shown: Rec[];
  ready: boolean;
  /** One plain line from the engine, for example that an unsaved model keeps no queries. */
  note: string | null;
  activeId: string | null;
  /** The row the page's live result belongs to. Any other open row has not run, so it shows what it kept and a Run. */
  liveRow: string | null;
  /** Run an open row that is not the live one. False when this tier or state has nothing to run it with. */
  canRun: (r: Rec) => boolean;
  runRow: (r: Rec) => void;
  tab: Tab; setTab: (t: Tab) => void;
  selected: ReadonlySet<string>; toggleSelected: (id: string) => void;
  /** Press a folded row to open it, or the open row to fold it. */
  open: (r: Rec) => void;
  newQuery: () => void;
  beginDraft: () => void;
  act: (action: 'pin' | 'unpin' | 'close' | 'reopen' | 'remove', id: string) => Promise<void>;
  /** Bring a history row back into the notebook and open it. */
  bringBack: (r: Rec) => void;
  timing: Timing | null; timeNote: string | null;
  timeAll: (scope: TimeScope) => void; stopTiming: () => void;
  counts: Record<TimeScope, number>;
}

// The last assistant run each model's notebook has opened, so a remount never replays one (the shared hook does the same for the other kinds).
const lastRunSeen: Record<string, number> = {};

export function useNotebook(q: Q): Notebook {
  const { conn } = useConnection();
  const askOn = useFeature('askData') === 'granted';
  const key = q.persistKey;
  const [all, setAll] = useState<Rec[]>([]);
  const [ready, setReady] = useState(false);
  const [note, setNote] = useState<string | null>(null);
  const [activeRaw, setActive] = usePersistedState<string | null>('lab.nb.active.' + key, null);
  const [tab, setTabRaw] = useState<Tab>('answer');
  const [selected, setSelected] = useState<ReadonlySet<string>>(new Set());
  const [timing, setTiming] = useState<Timing | null>(null);
  const [timeNote, setTimeNote] = useState<string | null>(null);
  // The row the page's live rows and numbers belong to. A row opened with no connection to run it on holds nothing live, so it is never noted.
  const [liveRow, setLiveRow] = useState<string | null>(null);
  const stop = useRef(false);
  // The last figure written to a row. A row run again starts clean: the engine resets a question it asks again, so the same figure is said again.
  const noting = useRef<string>('');
  const keyRef = useRef(key);
  keyRef.current = key;

  const apply = (v: QueryNotebookView | null | undefined) => { if (v) { setAll(v.questions ?? []); setNote(v.note ?? null); } setReady(true); };
  const reload = async (): Promise<Rec[] | null> => {
    const at = keyRef.current;
    try { const v = await rpc<QueryNotebookView>('queryNotebook', 'list'); if (keyRef.current === at) { apply(v); return v?.questions ?? null; } }
    catch { if (keyRef.current === at) setReady(true); }
    return null;
  };
  useEffect(() => { setAll([]); setReady(false); setNote(null); setSelected(new Set()); void reload(); /* eslint-disable-next-line react-hooks/exhaustive-deps */ }, [key]);
  // The assistant asked a question or tidied the notebook: its rows are the same file, so read it again.
  useClaudeReflection('ask_data', () => { void reload(); });
  useClaudeReflection('query_notebook', () => { void reload(); });
  // Every query the assistant ran is a row of its own (the engine keeps it); the checks it ran fold away, so read the list for each.
  useEffect(() => { if (q.ranSeq > 0) void reload(); }, [q.ranSeq]);   // eslint-disable-line react-hooks/exhaustive-deps
  // Hero 25 H1: the assistant ran a query of its own. The engine kept it as a row of its own; that row opens and holds the rows, so they are never
  // drawn into (or saved as the figure of) a row the person owns. With no history kept, the draft row holds them. The run_dax event has ONE
  // consumer (the Query page's hook in daxlab.tsx, which shows the assistant's rows); the notebook follows the event that hook keeps.
  const ranEvent = q.claudeEvent?.kind === 'run_dax' ? q.claudeEvent : null;
  useEffect(() => {
    if (!ranEvent || lastRunSeen[key] === ranEvent.seq) return;
    lastRunSeen[key] = ranEvent.seq;
    const r = ranEvent.result as { query?: string | null; error?: string | null; cancelled?: boolean } | undefined;
    if (!r || ranEvent.error || r.error || r.cancelled) return;
    const ran = r.query ?? ranEvent.query ?? null;
    void reload().then((list) => {
      const mine = assistantRowFor(list ?? [], ran);
      // The assistant's row keeps its number, so it opens on it after the person leaves Query and comes back (UAT F58). The page notes it
      // here because the run's rows are not the ones the row-owner effect below writes (they belong to the assistant, not to the person).
      if (mine && !mine.headline) {
        const n = noteFor(mine, figureOf(ranEvent.result as ResultSet, null), null, null);
        if (n?.headline) void rpc('noteQueryRow', mine.id, { headline: n.headline }).then(() => reload()).catch(() => undefined);
      }
      // A check the assistant ran (a probe) never takes the page from the row that is open.
      if (mine && isProbe(mine) && activeIdNow.current) return;
      setTabRaw('answer');
      if (mine) { setActive(mine.id); setLiveRow(mine.id); q.activeRow.current = mine.id; }
      else { setActive(DRAFT); setLiveRow(DRAFT); q.activeRow.current = null; }
    });
  }, [ranEvent?.seq]);   // eslint-disable-line react-hooks/exhaustive-deps

  const rows = useMemo(() => notebookRows(all), [all]);
  const activeId = activeRaw && (activeRaw === DRAFT || rows.some((r) => r.id === activeRaw)) ? activeRaw : null;
  // The page's run buttons and chip changes ask which row they belong to, and say when they have added or changed one.
  q.activeRow.current = activeId && activeId !== DRAFT ? activeId : null;
  const activeIdNow = useRef<string | null>(null);
  activeIdNow.current = activeId;
  useEffect(() => { q.moveCompareTo(activeId && activeId !== DRAFT ? activeId : null); }, [activeId]);   // eslint-disable-line react-hooks/exhaustive-deps
  for (const r of rows) if (r.headline) q.saidHeadline.current[r.id] = { dax: r.dax ?? '', headline: r.headline };
  // The new row is read first and opened after, so the page never shows a moment with no open row between the draft and the real row.
  // A draft that becomes a row takes its Compare with it; any other row starts on its own (pro UAT 18 Oct, round 3, P5).
  const land = (id: string) => { if (!q.activeRow.current) q.moveCompareTo(id, true); void reload().then(() => { setActive(id); setLiveRow(id); }); };
  q.onRecorded.current = land;
  // A measure the assistant points the Studio at seeds a query of its own: it opens as the draft, and the open row keeps its DAX.
  q.onSeeded.current = () => { setTabRaw('dax'); setActive(DRAFT); setLiveRow(null); q.activeRow.current = null; };
  const setTab = (t: Tab) => setTabRaw(t);
  // A question that landed is the open row; its id is the engine's. A model that keeps no history gives none, so the draft row holds it.
  useEffect(() => {
    const id = q.askBox.answerId;
    if (id) land(id);
    else if (q.askState.answer) { setLiveRow(DRAFT); void reload(); }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [q.askState.answer]);

  // Hero 26 N1: when the assistant's run is on screen and the engine kept no row for it, the draft holds the assistant's own DAX and nothing
  // else: never the ask-box words or the chips of the row the person has open.
  const draft: Rec | null = activeId === DRAFT ? (q.assistantShown ? {
    id: DRAFT, askedAt: new Date().toISOString(), kind: 'dax', question: null, dax: q.currentQuery(), outcome: 'answered', inNotebook: true,
  } : {
    id: DRAFT, askedAt: new Date().toISOString(), kind: q.entry === 'words' || q.chipsHere ? 'words' : 'dax',
    question: q.entry === 'words' || q.chipsHere ? (q.askState.words.trim() || null) : null, dax: q.entry === 'words' || q.chipsHere || !q.res ? null : q.currentQuery(), outcome: 'answered', inNotebook: true,
  }) : null;
  const shown = useMemo(() => (draft ? [draft, ...rows] : rows), [rows, draft?.question, draft?.dax, draft?.kind, activeId]);   // eslint-disable-line react-hooks/exhaustive-deps

  // A row that is open when the page comes back (the person went to another tab and returned) still owns the rows the page holds, when its own
  // query made them: it shows its answer and its chart at once, instead of "Run again" over a result that is still there (UAT 17 Oct, P29).
  useEffect(() => {
    const row = rows.find((r) => r.id === activeId);
    if (!row || liveRow || !q.res || q.err || q.idle || !row.dax || !q.producedBy) return;
    if (rowOwnsResult(row, q.producedBy, q.assistantShown)) setLiveRow(row.id);
  }, [activeId, rows, q.res, q.err, q.idle, liveRow, q.producedBy, q.assistantShown]);   // eslint-disable-line react-hooks/exhaustive-deps

  // The open row's number, said once it is known: the engine keeps it with the row, so a folded row shows its number without running.
  useEffect(() => {
    const row = rows.find((r) => r.id === activeId);
    if (!row || q.idle || liveRow !== row.id || (!q.res && !q.err)) return;
    // A row's figure is written only by that row's own run: never by rows the assistant made, or by another query's rows.
    if (!rowOwnsResult(row, q.producedBy, q.assistantShown)) return;
    // A question opened again from its parts is not read again, so it does not say the word that could not apply: the row keeps what it said.
    const dropped = isDaxRow(row) ? '' : (q.askState.answer?.assumption ?? row.dropped ?? '');
    const n = noteFor(row, figureOf(q.shownRes ?? q.res, q.chartConfig), dropped, q.speedMs ?? null, saidFor(q.saidHeadline.current[row.id], row));
    if (!n) return;
    const sig = row.id + JSON.stringify(n);
    if (noting.current === sig) return;
    noting.current = sig;
    void rpc('noteQueryRow', row.id, n).then(() => reload()).catch(() => undefined);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [rows, activeId, q.idle, q.res, q.err, q.shownRes, liveRow, q.askState.answer, q.assistantShown]);

  const act: Notebook['act'] = async (action, id) => {
    try { apply(await rpc<QueryNotebookView>('queryNotebook', action, id)); } catch { /* the list below stays as it was */ }
    if ((action === 'close' || action === 'remove') && id === activeRaw) setActive(null);
    if (action === 'close' || action === 'remove') setSelected((s) => { const n = new Set(s); n.delete(id); return n; });
  };

  // A DAX row runs as written. A words row is asked again from its parts, else its words; without the ask there is nothing to run it with.
  const canRun = (r: Rec) => r.id !== DRAFT && !!conn?.connected && !q.idle && (isDaxRow(r) || (askOn && !!(r.question || (r.parts && (r.parts.measures ?? []).length))));
  function runRow(r: Rec) {
    if (!canRun(r)) return;
    noting.current = '';
    if (isDaxRow(r)) { q.chooseEntry('dax'); q.setQuery(r.dax ?? ''); setLiveRow(r.id); void q.runQuery(r.dax ?? ''); return; }
    q.chooseEntry('words');
    if (r.parts && (r.parts.measures ?? []).length) q.askBox.askParts(r.parts, r.id);
    else if (r.question) q.askBox.askWords(r.question, r.id);
  }
  function open(r: Rec) {
    if (r.id === activeId) { setActive(null); return; }
    setTabRaw('answer'); setActive(r.id); setLiveRow(null);
    q.activeRow.current = r.id;
    if (r.id === DRAFT) return;
    if (isDaxRow(r)) { q.chooseEntry('dax'); q.setQuery(r.dax ?? ''); }
    else if (askOn) q.chooseEntry('words');
    // Hero 26 N10: a folded row that kept its number opens on that number and does not ask again (14 asks for 6 questions). Run asks again.
    // A row with no kept number (never run, or a part that could not apply) still runs once on opening.
    // Hero 27 P1: a row that opens on its kept number shows ITS OWN DAX in the DAX tab, never the last query of the session (another row's, or the assistant's).
    // A row count ("24 rows") is no number to open on, and the rows were never kept: such a row runs once on opening so its chart is there, not "Run again" (UAT 18 Oct).
    const keepsNumber = !!r.headline && r.outcome === 'answered' && !isRowCount(r.headline);
    if (keepsNumber) { q.setAssistantDax(null); q.chooseEntry('dax'); if (!q.askBox.asking) q.askBox.clear(); q.setQuery(r.dax ?? ''); }
    if (keepsNumber) return;
    runRow(r);
  }
  function beginDraft() { setTabRaw('answer'); setActive(DRAFT); setLiveRow(null); q.activeRow.current = null; }
  function newQuery() {
    // A new query is an EMPTY builder that runs nothing. It used to keep the fields of the row last open, so the next change landed on that row
    // (a pinned one, UAT F37) and an old guess ran as if it were a question (P30).
    q.askBox.clear(); q.askState.setWords(''); q.clearWells(); q.chooseEntry('build'); beginDraft(); setTabRaw('dax');
  }
  function bringBack(r: Rec) {
    if (r.inNotebook || r.pinned) { open(r); return; }
    void rpc<QueryNotebookView>('queryNotebook', 'reopen', r.id).then((v) => {
      apply(v);
      const back = v.questions.find((x) => x.id === r.id);
      if (back) open(back);
    }).catch(() => undefined);
  }

  const counts = useMemo(() => scopeCounts(rows, selected), [rows, selected]);
  // Time all: the existing speed call (first run with nothing kept, then repeat runs) for a chosen set of rows, ONE QUERY AT A TIME.
  async function runTiming(scope: TimeScope) {
    const todo = timeScope(scope, rows, selected);
    if (!todo.length || timing || q.idle || !conn?.connected) return;
    stop.current = false; setTimeNote(null);
    q.setBusy('measure');
    let done = 0, failed = 0, repeatOnly = 0;
    let why: string | null = null;
    let slowest: { text: string; ms: number } | null = null;
    try {
      for (let i = 0; i < todo.length; i++) {
        if (stop.current) break;
        const r = todo[i];
        setTiming({ scope, i: i + 1, n: todo.length, text: rowText(r), stopping: false });
        try {
          const c = await rpc<ColdWarmBenchmark>('benchmarkColdWarm', r.dax, q.runs, q.clearOnRun && (!q.shared || q.confirmShared), q.confirmShared);
          const times = measuredTimes(c);
          if (times) {
            await rpc('noteQueryRow', r.id, times); done++; if (times.coldMs == null) repeatOnly++;
            const ms = times.warmMs ?? times.coldMs;
            if (ms != null && (!slowest || ms > slowest.ms)) slowest = { text: rowText(r), ms };
          }
          else { failed++; why ??= c?.error ?? c?.note ?? 'the query returned no times'; }
        } catch (e) { failed++; why ??= String((e as Error)?.message ?? e); }
        await reload();
      }
    } finally { q.setBusy(null); setTiming(null); }
    setTimeNote(timingNote({ done, failed, total: todo.length, repeatOnly, why, slowest }));
  }
  // "Timed N of N" is news for a moment: it leaves the page by itself.
  useEffect(() => {
    if (!timeNote) return undefined;
    const t = setTimeout(() => setTimeNote(null), TIME_NOTE_MS);
    return () => clearTimeout(t);
  }, [timeNote]);

  return {
    all, shown, ready, note, activeId, liveRow, canRun, runRow, tab, setTab, selected,
    toggleSelected: (id) => setSelected((s) => { const n = new Set(s); if (n.has(id)) n.delete(id); else n.add(id); return n; }),
    open, newQuery, beginDraft, act, bringBack, timing, timeNote, timeAll: (s) => { void runTiming(s); },
    stopTiming: () => { stop.current = true; setTiming((t) => (t ? { ...t, stopping: true } : t)); }, counts,
  };
}

// ---- the head: the box that asks in words, Time all, and the history

/** Time all: one press that measures speed (first run and repeat runs) for the rows the person chooses. */
export function TimeAll({ nb, disabled }: { nb: Notebook; disabled: boolean }) {
  const [menu, setMenu] = useState(false);
  return <span className="qn-rel">
    <button type="button" className="sem-btn" aria-haspopup="menu" aria-expanded={menu} disabled={disabled || !!nb.timing || nb.shown.length === 0} onClick={() => setMenu(!menu)} data-testid="time-all">
      {nb.timing ? 'Timing' : 'Time all'}
    </button>
    {menu && <Pop onClose={() => setMenu(false)} className="qn-pop-right" label="Time all">
      <div className="qn-pop-head">Measure speed for</div>
      {SCOPES.map((s) => <button key={s} type="button" role="menuitem" className="qn-menuitem" disabled={nb.counts[s] === 0} data-testid={`time-scope-${s}`}
        title={nb.counts[s] === 0 ? (s === 'selection' ? 'Tick the box at the left of a row first.' : s === 'pinned' ? 'Pin a row first.' : 'No row has a query to time.') : undefined}
        onClick={() => { setMenu(false); nb.timeAll(s); }}>{SCOPE_WORD[s]}<span className="qn-faint qn-num">{nb.counts[s]}</span></button>)}
    </Pop>}
  </span>;
}

/** Where the timing is up to. One line, with Stop. */
export function TimingLine({ nb }: { nb: Notebook }) {
  if (!nb.timing && !nb.timeNote) return null;
  return <div className="qn-timing" role="status" data-testid="time-progress">
    {nb.timing
      ? <><span className="qn-spin" aria-hidden="true" /><span>{nb.timing.stopping ? 'Stopping after this one' : `Timing ${nb.timing.i} of ${nb.timing.n}`}</span><span className="qn-faint qn-ellip">{nb.timing.text}</span>
        <button type="button" className="sem-btn sem-btn-sm qn-push" disabled={nb.timing.stopping} onClick={nb.stopTiming} data-testid="time-stop">Stop</button></>
      : <span className="qn-faint">{nb.timeNote}</span>}
  </div>;
}

/** The clock in the question box: every question and query asked of this model, newest first. Press one to bring it back. */
export function HistoryButton({ nb, q }: { nb: Notebook; q: Q }) {
  const [on, setOn] = useState(false);
  const list = useMemo(() => historyRows(nb.all), [nb.all]);
  return <span className="qn-rel">
    <button type="button" className="qn-icon" title="Asked before" aria-label="Asked before" aria-haspopup="menu" aria-expanded={on} onClick={() => setOn(!on)} data-testid="history-open">{Icon.clock}</button>
    {on && <Pop onClose={() => setOn(false)} className="qn-histpop" label="Asked before">
      <div className="qn-pop-head">Asked before</div>
      <button type="button" role="menuitem" className="qn-menuitem" disabled={q.idle} onClick={() => { setOn(false); nb.newQuery(); }} data-testid="history-new">New query</button>
      {nb.note && <div className="qn-pop-head" data-testid="history-note">{nb.note}</div>}
      {list.length === 0 && !nb.note && <div className="qn-pop-head" data-testid="history-empty">Nothing asked yet. Each question and each query is kept here.</div>}
      <div className="qn-histlist" data-testid="history-list">
        {list.map((r) => <div key={r.id} className="qn-hist" data-testid="history-row" data-open={r.inNotebook || r.pinned ? 'yes' : 'no'}>
          <button type="button" className="qn-hist-main" disabled={q.idle} aria-label={`Run again: ${rowText(r)}`} onClick={() => { setOn(false); nb.bringBack(r); }} title={rowText(r)}>
            <span className={`qn-hist-q ${isDaxRow(r) ? 'qn-mono' : ''}`}>{rowText(r)}</span>
            <span className="qn-num qn-hist-n">{r.headline ?? (r.outcome && r.outcome !== 'answered' ? OUTCOME_WORD[r.outcome] ?? r.outcome : '')}</span>
            <span className="qn-faint qn-hist-t">{r.origin === 'agent' ? 'Assistant, ' : ''}{ago(r.askedAt)}</span>
          </button>
          <button type="button" className={`qn-icon qn-icon-sm ${r.pinned ? 'qn-pinned' : ''}`} title={r.pinned ? 'Unpin' : 'Pin'} aria-label={r.pinned ? `Unpin: ${rowText(r)}` : `Pin: ${rowText(r)}`} aria-pressed={!!r.pinned} onClick={() => void nb.act(r.pinned ? 'unpin' : 'pin', r.id)} data-testid="history-pin">{r.pinned ? Icon.pin : Icon.pinOff}</button>
          <button type="button" className="qn-icon qn-icon-sm" title="Remove from the history" aria-label={`Remove: ${rowText(r)}`} onClick={() => void nb.act('remove', r.id)} data-testid="history-remove">{Icon.trash}</button>
        </div>)}
      </div>
    </Pop>}
  </span>;
}

