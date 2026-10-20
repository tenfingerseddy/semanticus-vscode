import { Fragment, useEffect, useRef, useState } from 'react';
import { rpc } from './bridge';
import { useConnection, ConnectBar } from './connection';
import { QueryStalenessChip } from './contextbar';
import { ClaudeRanBanner } from './activity';
import { ResultGrid } from './grid';
import { DaxEditor } from './daxeditor';
import { FieldsPanel } from './fieldspanel';
import { QueryWells } from './querywells';
import { AskField, AskChips, AskLines, AskFreeLine } from './queriesask';
import { ExplainPanel, plainValue } from './explainpanel';
import { MenuPop } from './menupop';
import { AuthorDrawer } from './tests-authoring';
import { ExampleAnswers, OUTCOME_WORD, SaveChooser, when } from './askdata';
import { ExplainInline } from './explainpanel';
import { learnActive } from './studio/learn-state';
import { checkDraftOf, filterWords, outcomeOf } from './askdata-model';
import { explainPayloadFromRow, DaxVisual } from './daxvisual';
import { plainEngineNote } from './enginewords';
import { plainQueryError } from './daxErrorText';
import { gridRowsInTimeOrder } from './tabulate';
import { isSignInError, SIGN_IN_AGAIN } from './authcopy';
import { timingAnnouncement, rowAnnouncement, queryIdentity } from './wire';
import { squash } from './daxsquash';
import { readableTitle } from './validation-model';
import { parseRef } from './daxwells';
import { friendlyHeader } from './resultformat.mjs';
import { Visual, KIND_NAME } from './visuals';
import { SplitBar, RunsStrip, median, speedInsight, isFast, waitMs, visibleScans } from './query-parts';
import { headlineOf, looksLikeDax, matrixConfigOf, readAsOfWells, KIND_SEES } from './query-model';
import { useFeature } from './pro';
import { Banner, CompareBody, TraceNote, scanKind, useQueryPage, type QueryPageProps } from './daxlab';
import { DRAFT, Icon, Pop, RowLine, TABS, TAB_NAME, TimeAll, TimingLine, HistoryButton, useNotebook, type Notebook, type Tab } from './query-notebook';
import { checksLine, figureOf, isRowCount, keepSymbol, keptLine, msWord, saidFor, splitChecks, type Rec } from './query-notebook-model';
import './visuals';
import './query.css';

type Q = ReturnType<typeof useQueryPage>;

const TABLE_ROWS_SHOWN = 20;

// The Query page (1.6): the notebook (Kane picked option C on 16 Oct). One box asks in plain words. Each question is one row in a column,
// newest at the top; the open row shows its number, how it was read in one line, and one visual. The tools (DAX, Table, Speed, Steps,
// Compare, Save, Explain) open on demand in one place under the row. A row can be closed (it stays in the history), pinned, or removed.
// A word that could not apply is always said, in the open row and by a mark on the folded one: a number must not read as if a filter applied.
export function QueryView(props: QueryPageProps) {
  const q = useQueryPage(props);
  const nb = useNotebook(q);
  const { conn } = useConnection();
  const root = useRef<HTMLDivElement>(null);
  // Words carried in from the Plan's field fill the question box and nothing else: a query that runs unasked is work nobody asked for.
  const takenWords = useRef<number | null>(null);
  const navWords = props.navWords;
  useEffect(() => {
    if (!navWords || takenWords.current === navWords.nonce) return;
    takenWords.current = navWords.nonce;
    if (navWords.words.trim()) { q.askState.setWords(navWords.words); q.chooseEntry('words'); }
    props.onNavConsumed?.(navWords.nonce);
  }, [navWords?.nonce]);   // eslint-disable-line react-hooks/exhaustive-deps
  // An empty Query page opens a new empty query, so the fields and the DAX box the page points to are there to use (UAT 17 Oct, F22).
  const askGrant = useFeature('askData');
  useEffect(() => {
    if (askGrant === 'denied' && nb.ready && nb.shown.length === 0 && !nb.activeId && !nb.note) nb.newQuery();
  }, [askGrant, nb.ready, nb.shown.length, nb.activeId, nb.note]);   // eslint-disable-line react-hooks/exhaustive-deps
  // A measure chip's compare mark, the DAX "Compare a rewrite" and the assistant's verify_equivalence all ask for the Compare tab.
  useEffect(() => {
    if (q.sheet === 'compare' || q.sheet === 'steps') {
      if (!nb.activeId) nb.beginDraft();
      nb.setTab(q.sheet); q.setSheet(null);
    }
  }, [q.sheet]);   // eslint-disable-line react-hooks/exhaustive-deps
  // Pressing a number to explain it opens the Explain view of the open row.
  const explainIt = (p: Parameters<Q['setExplain']>[0]) => { q.setExplain(p); nb.setTab('explain'); };
  return (
    <div ref={root} className="sem-page qv-page qn-page" data-testid="query-page"
      onContextMenuCapture={(e) => { q.lastPointer.current = { x: e.clientX, y: e.clientY }; }}
      onKeyDown={(e) => {
        // Ctrl+Enter (Cmd+Enter on a Mac) anywhere on the page runs the query, unless the question box claims it to ask (H7).
        if ((e.target as HTMLElement | null)?.closest?.('[data-ask-field]')) return;
        if ((e.ctrlKey || e.metaKey) && !e.altKey && !e.shiftKey && e.key === 'Enter' && conn?.connected && !q.idle) { e.preventDefault(); e.stopPropagation(); q.run(); }
      }}>
      <h1 className="sr-only">Query</h1>
      {q.checkShape && <AuthorDrawer shape={q.checkShape} editing={q.checkDraft} onClose={() => { q.setCheckShape(null); q.setCheckDraft(null); }} onSaved={() => q.setCheckSaved(true)} />}
      {q.answerDraft && <AuthorDrawer shape="measureValue" handedFrom="queries" editing={q.answerDraft} onClose={() => q.setAnswerDraft(null)} onSaved={() => { q.setAnswerDraft(null); q.setCheckSaved(true); }} />}
      <Head q={q} nb={nb} />
      <TimingLine nb={nb} />
      <Book q={q} nb={nb} onOpenTests={props.onOpenTests} explainIt={explainIt} />
      <div className="sr-only" aria-live="polite" data-testid="queries-ask-announce">{q.askState.announce}</div>
      {q.cellMenu && <MenuPop at={{ x: q.cellMenu.x, y: q.cellMenu.y }} onClose={() => q.setCellMenu(null)} items={[
        { label: 'Explain this number', detail: `${q.cellMenu.payload.measureName} = ${q.cellMenu.payload.value}`, act: () => explainIt(q.cellMenu!.payload) },
        { label: 'Calculation steps', detail: 'Run again and log each step of the calculation', disabled: !conn?.connected || q.idle || !!q.shared, act: () => { nb.setTab('steps'); void q.debug(); } },
      ]} />}
    </div>
  );
}

// ---------------------------------------------------------------- the head: ask in plain words, Time all

function Head({ q, nb }: { q: Q; nb: Notebook }) {
  const { conn } = useConnection();
  // The Pro gate, read here where it is used: Free never reaches the question box or the ask calls (test/free-sends-no-jev.test.mjs).
  const askGrant = useFeature('askData');
  const askOn = askGrant === 'granted';
  const stopping = q.idle && q.busy !== 'measure';
  // Asking from the box opens a row at once: the row says it is running until the answer lands in its own.
  // DAX typed in the box runs as DAX, as its own row; it is never sent to Jev to read as words.
  const asking = {
    ...q.askBox,
    askWords: (t?: string) => {
      const text = (typeof t === 'string' ? t : q.askState.words).trim();
      if (looksLikeDax(text)) {
        if (q.idle || !conn?.connected) return;
        nb.beginDraft(); q.chooseEntry('dax'); q.setQuery(text); q.askState.setWords('');
        void q.runQuery(text);
        return;
      }
      nb.beginDraft(); q.askBox.askWords(t);
    },
  };
  return <div className="qn-head" data-testid="query-bar" role="group" aria-label="Ask">
    {askOn
      ? <AskField ask={asking} disabled={!conn?.connected || (q.idle && q.busy !== 'ask')} trailing={<HistoryButton nb={nb} q={q} />} />
      : askGrant === 'denied'
        ? <><AskFreeLine /><button type="button" className="sem-btn" onClick={nb.newQuery} disabled={q.idle} data-testid="new-query">New query</button><HistoryButton nb={nb} q={q} /></>
        : <div className="qn-grow" />}
    {stopping
      ? <button type="button" className="sem-btn" onClick={q.stopQuery} title="Stop the running query" aria-label="Stop the running query" data-testid="query-stop">Stop</button>
      : askOn && <button type="button" className="sem-btn sem-btn-primary" disabled={!conn?.connected || !q.askState.words.trim() || q.idle} onClick={() => asking.askWords()} data-testid="query-ask">Ask</button>}
    <TimeAll nb={nb} disabled={!conn?.connected || q.idle} />
  </div>;
}

// ---------------------------------------------------------------- the notebook

function Book({ q, nb, onOpenTests, explainIt }: { q: Q; nb: Notebook; onOpenTests?: () => void; explainIt: (p: Parameters<Q['setExplain']>[0]) => void }) {
  const { conn } = useConnection();
  const askOn = useFeature('askData') === 'granted';
  const running = q.busy === 'ask' || q.busy === 'run' || q.busy === 'debug';
  // What the assistant ran only to check something is held in one quiet line, so the answers stay in view (UAT 17 Oct, P42b).
  const [checksOpen, setChecksOpen] = useState(false);
  const { rows, checks } = splitChecks(nb.shown, nb.activeId);
  const line = (r: Rec) => {
    const on = r.id === nb.activeId;
    return <div key={r.id} className={`qn-row ${on ? 'qn-row-on' : ''}`} data-testid="notebook-cell">
      <RowLine r={r} on={on} nb={nb} running={on && running && r.id === DRAFT} />
      {on && <RowBody q={q} nb={nb} r={r} onOpenTests={onOpenTests} explainIt={explainIt} />}
    </div>;
  };
  return <section className="sem-card qn-book" data-testid="notebook" aria-label="Your queries" data-selecting={nb.selected.size > 0 ? 'yes' : 'no'}>
    {nb.shown.length === 0 && <div className="qn-empty" data-testid="notebook-empty">
      <span className="qn-faint">{nb.note ?? (nb.ready ? (askOn ? 'Ask a question above. Each question stays here as a row. Pin the ones you want to keep at the top.' : 'Choose fields or write a query above. Each query stays here as a row. Pin the ones you want to keep at the top.') : 'Reading your queries.')}</span>
      {!conn?.connected && <ConnectBar hint="Connect a model that holds data to run queries." />}
      {learnActive(q.learn) && <ExampleAnswers example={q.learn.tour.example} />}
    </div>}
    {rows.map(line)}
    {checks.length > 0 && <div className="qn-checks" data-testid="notebook-checks">
      <button type="button" className="qn-checks-line" aria-expanded={checksOpen} onClick={() => setChecksOpen(!checksOpen)} data-testid="checks-fold">
        <span className="qn-chev" aria-hidden="true">{checksOpen ? Icon.down : Icon.right}</span>{checksLine(checks.length)}
      </button>
      {checksOpen && checks.map(line)}
    </div>}
  </section>;
}

/** An open row that is not the page's live row has not run: it shows what it kept (never another row's rows, line or chart) and a Run. */
function KeptBody({ nb, r, what }: { nb: Notebook; r: Rec; what: string }) {
  const { conn } = useConnection();
  // A row that ran keeps its answer and says when it ran; only a row that never ran says "Not run yet" (UAT 17 Oct, P29 + F58).
  const line = keptLine(r);
  const kept = r.headline || null;
  const blank = kept === 'No rows' || kept === 'Blank';
  // A row count is no answer to lead with (UAT F26): it is said small beside the time, and the chart comes back with Run again.
  const count = isRowCount(kept);
  const failed = !r.headline && r.outcome && r.outcome !== 'answered' ? OUTCOME_WORD[r.outcome] ?? null : null;
  return <div className="qn-answer" data-testid="answer-kept" data-outcome={r.outcome ?? ''}>
    {kept && !count && what === 'answer' && <div className="qn-figure" data-testid="figure"><div className={`qn-figure-n qn-num ${blank ? 'qn-faint' : ''}`}>{kept}</div></div>}
    {r.dropped && what === 'answer' && <Notice tone="warn"><span data-testid="assumption-kept">{r.dropped}</span></Notice>}
    <div className="qv-tools" data-testid="row-not-run">
      <span className="sem-text-meta">{failed ? `${failed}. ` : ''}{count ? `${kept}. ` : ''}{line.text}</span>
      {nb.canRun(r) && <button type="button" className="sem-btn sem-btn-sm sem-btn-primary" onClick={() => nb.runRow(r)} data-testid="row-run">{line.ran ? 'Run again' : 'Run'}</button>}
    </div>
    {!conn?.connected && <ConnectBar hint="Connect a model that holds data to run queries." />}
  </div>;
}

function RowBody({ q, nb, r, onOpenTests, explainIt }: { q: Q; nb: Notebook; r: Rec; onOpenTests?: () => void; explainIt: (p: Parameters<Q['setExplain']>[0]) => void }) {
  const tabs: Tab[] = nb.tab === 'explain' ? [...TABS, 'explain'] : TABS;
  // Only the live row draws the page's result. The draft row is the live one while a new question or query is worked on.
  const notRun = r.id !== DRAFT && nb.liveRow !== r.id;
  // A tour step that names a recorded answer shows it on the Answer tab: an empty New query used to sit on DAX with nothing filled (r2 UAT N18).
  const tourExample = learnActive(q.learn) ? q.learn.tour.example : null;
  useEffect(() => { if (tourExample !== null) nb.setTab('answer'); }, [tourExample]);   // eslint-disable-line react-hooks/exhaustive-deps
  return <div className="qn-body" data-testid="row-body">
    <div className="qn-tabs" role="tablist" aria-label="What to see">
      {tabs.map((t) => <button key={t} type="button" role="tab" className="qn-tab" aria-selected={nb.tab === t} onClick={() => nb.setTab(t)} data-testid={`view-${t}`}>{TAB_NAME[t]}</button>)}
    </div>
    <div className="qn-view" role="tabpanel">
      {nb.tab === 'answer' && (notRun ? <KeptBody nb={nb} r={r} what="answer" /> : <AnswerBody q={q} r={r} explainOpen={() => nb.setTab('explain')} />)}
      {nb.tab === 'dax' && <DaxBody q={q} nb={nb} />}
      {nb.tab === 'table' && (notRun ? <KeptBody nb={nb} r={r} what="table" /> : <TableBody q={q} explainIt={explainIt} />)}
      {nb.tab === 'speed' && <SpeedBody q={q} r={r} timingAll={!!nb.timing} />}
      {nb.tab === 'steps' && <StepsBody q={q} />}
      {nb.tab === 'compare' && <CompareTab q={q} r={r} />}
      {nb.tab === 'save' && <SaveBody q={q} onOpenTests={onOpenTests} />}
      {nb.tab === 'explain' && <ExplainBody q={q} />}
    </div>
  </div>;
}

// ---------------------------------------------------------------- answer: the number, how it was read, one visual

function Notice({ tone, children }: { tone?: 'warn' | 'bad'; children: React.ReactNode }) {
  return <div className={`qv-note ${tone ? `qv-note-${tone}` : ''}`}>{children}</div>;
}

/** More: draw the numbers as another visual that fits (and who chose the one shown), or explain a number. One quiet link under the answer. */
function MoreMenu({ q, explainOpen }: { q: Q; explainOpen: () => void }) {
  const [on, setOn] = useState(false);
  const fits = q.rule && q.rule.fits.length > 1 ? q.rule : null;
  if (!fits && !q.explainShown && q.explainChoices.length === 0) return null;
  const pick = q.jevHere ? q.basePick! : q.rule?.pick;
  const reason = !q.rule ? '' : q.picked ? (q.jevHere ? `Your pick. Jev picked ${KIND_NAME[q.basePick!]}.` : `Your pick. The rules pick ${KIND_NAME[q.rule.pick]}.`) : q.jevHere ? q.jevPick!.reason : q.rule.reason;
  return <span className="qn-rel qn-more">
    <button type="button" className="qn-link" aria-haspopup="menu" aria-expanded={on} onClick={() => setOn(!on)} data-testid="answer-more">More</button>
    {on && <Pop onClose={() => setOn(false)} label="More" className="qn-fitpop">
      {fits && pick && <div className="qv-pickline" data-testid="pickline" data-by={q.jevHere ? 'jev' : 'rules'}>
        <div className="qn-pop-head">{q.jevHere ? 'Jev chose this visual. ' : 'The rules chose this visual. '}{reason} {KIND_SEES[q.kind]}</div>
        {q.picked && <button type="button" role="menuitem" className="qn-menuitem" data-testid="pick-back" onClick={() => { q.setVizOverride(null); setOn(false); }}>{q.jevHere ? "Back to Jev's pick" : "Back to the rules' pick"}</button>}
      </div>}
      {(q.explainShown || q.explainChoices.length > 0) && <button type="button" role="menuitem" className="qn-menuitem" data-testid="answer-explain-open" onClick={() => { setOn(false); if (q.explainShown) q.setExplain(q.explainShown); explainOpen(); }}>Explain this number</button>}
    </Pop>}
  </span>;
}

/** The other visuals that fit these numbers, beside the answer, one press each (UAT 17 Oct, F35: the switch was only under More). */
function VisualSwitch({ q }: { q: Q }) {
  const fits = q.rule && q.rule.fits.length > 1 ? q.rule : null;
  if (!fits) return null;
  const pick = q.jevHere ? q.basePick! : fits.pick;
  return <span className="sem-seg" role="group" aria-label="Show as" data-testid="visual-switch">
    {[pick, ...fits.fits.filter((f) => f !== pick)].map((k) => <button key={k} type="button" className="sem-seg-item" aria-pressed={q.kind === k} data-testid={`fit-${k}`} onClick={() => q.chooseKind(k)}>{KIND_NAME[k]}</button>)}
  </span>;
}

function AnswerBody({ q, r, explainOpen }: { q: Q; r: Rec; explainOpen: () => void }) {
  const { conn, session, connectXmla, busy: connBusy } = useConnection();
  const askOn = useFeature('askData') === 'granted';
  // Chips describe the question's own query. Over a hand-written query they describe another question, so they are not shown (hero 25 H4); the DAX view offers the way back.
  // Hero 26 N1: the assistant's own query never wears the ask box's words, chips or answer: its number sits under its own DAX only.
  const chipsOn = askOn && !q.assistantShown && ((q.chipsHere && q.askOwns) || q.chipsKept);
  const answered = !q.assistantShown && (q.chipsHere || q.wordsOnly) && q.answerRows && q.askState.answer ? q.askState.answer : null;
  const words = q.entry === 'words' || q.chipsHere;
  const running = q.busy === 'run' || q.busy === 'ask' || q.busy === 'debug';
  const label = answered?.statement || (q.res || running ? headlineOf(q.res, q.chartConfig, running) : '');
  const hasRows = !!q.res && (q.res.rowCount ?? q.res.rows.length) > 0 && !q.err;
  const a = q.assistantShown ? null : q.askState.answer;
  const o = outcomeOf(a);
  // Hero 27 P3: the label follows the open row's own origin. The draft (an assistant ask the engine kept no row for) follows the ask it holds.
  const byAssistantRow = r.id === DRAFT ? q.chipsHere && !!q.byAssistant : r.origin === 'agent' && r.kind === 'words';
  // Hero 26 N1 (page): with the assistant's run on screen the person's fields are not read out either; only the assistant's DAX sits under its number.
  const parts = q.assistantShown ? [] : q.chartConfig ? readAsOfWells(q.chartConfig) : q.entry === 'build' && !q.wordsOnly ? readAsOfWells(q.config) : [];   // a question worked out from words holds the query: the wells belong to another row
  const signInThen = (again: () => void) => () => { void connectXmla(session!.liveEndpoint!, session!.liveDatabase ?? '', 'interactive', session!.currentTenant ?? null).then((res) => { if (res.ok) again(); }); };
  const signIn = session?.liveEndpoint && isSignInError(q.err ?? '') ? signInThen(() => void q.runQuery(q.currentQuery())) : null;
  // A question that failed to sign in is asked again the way it was asked (its parts, else its words), as the old Ask page did.
  const askSignIn = !signIn && session?.liveEndpoint && a && o === 'error' && isSignInError(a.error ?? '')
    ? signInThen(() => { if (q.askState.parts && (q.askState.parts.measures ?? []).length) q.askBox.askParts(q.askState.parts); else q.askBox.askWords(); }) : null;
  const tableShown = q.tableData ? { ...q.tableData, rows: q.tableData.rows.slice(0, TABLE_ROWS_SHOWN) } : null;
  const fig0 = figureOf(q.shownRes ?? q.res, q.chartConfig, running);
  const fig = fig0.big && !fig0.blank && !q.assistantShown ? { ...fig0, big: keepSymbol(r.headline ?? saidFor(q.saidHeadline.current[r.id], r), fig0.big) } : fig0;
  return <div className="qn-answer" data-testid="answer" data-outcome={o ?? ''}>
    {(q.assistantShown || byAssistantRow) && <div className="qn-source" data-testid="answer-source">
      {q.assistantShown && <span className="sem-text-meta">The query your assistant ran</span>}
      {byAssistantRow && <span className="sem-doer sem-doer-agent" data-testid="query-by-assistant">Asked by your assistant</span>}</div>}
    {q.checkSaved && q.checkSavedRow === r.id && <Banner color="var(--sem-good)">Check saved.</Banner>}
    {q.claudeEvent && <ClaudeRanBanner event={q.claudeEvent} onClear={() => q.setClaudeEvent(null)} queryShownAbove={q.assistantShown && q.claudeEvent.kind === 'run_dax'} />}
    {q.err && <div className="qv-check-part" data-testid="query-error">
      <Banner color="var(--sem-bad)">{plainQueryError(q.err, q.fieldNames)}</Banner>
      {signIn && <div><button type="button" data-testid="signin-again" disabled={connBusy} className="sem-btn sem-btn-sm sem-btn-primary" onClick={signIn}>{SIGN_IN_AGAIN}</button></div>}
    </div>}
    {running
      ? <div className="qn-running" role="status" data-testid="running"><span className="qn-spin" aria-hidden="true" /><span>{q.busy === 'ask' ? 'Reading, checking and running your question' : 'Running the query'}</span></div>
      : fig.big && !fig.count && <div className="qn-figure" data-testid="figure"><div className={`qn-figure-n qn-num ${fig.blank ? 'qn-faint' : ''}`}>{fig.big}</div>{fig.name && <div className="sem-text-meta" data-testid="figure-name">{fig.name}</div>}</div>}
    {label && !running && <p className="qn-label" data-testid="headline">{label}</p>}
    {/* A part that could not apply is said in the open row, always: the number would read as if it were applied (K21). */}
    <div className="qn-read" data-testid="reading">
      {chipsOn
        ? <AskChips ask={q.askBox} owns={q.chipsHere ? q.askOwns : true} disabled={q.idle} onRebuild={() => q.askBox.askParts(q.askState.parts!)} />
        : parts.length > 0
          ? <div className="qv-reading">{parts.map((p, i) => <span key={i} className="qv-part"><span className="qv-part-name">{p.part}</span><span className="qv-part-value">{p.value}</span></span>)}</div>
          : q.entry === 'dax' ? <div className="sem-text-meta" data-testid="read-dax">Your query runs exactly as written. Nothing is added, dropped or changed.</div>
            : !q.res && !q.err && !running && !a && r.id === DRAFT ? <div className="sem-text-meta" data-testid="read-none">{words ? 'Nothing asked yet. Type a question and press Ask.' : 'No fields chosen yet. Choose them under DAX, in Fields.'}</div> : null}
      {askOn && <AskLines ask={q.askBox} current={q.askState.answer?.dax ? q.answerRows : !q.res} quiet />}
      {/* A row opened again from its parts does not say the word that could not apply, so the row's own line says it (K21). */}
      {r.dropped && !(a && a.assumption) && !running && <Notice tone="warn"><span data-testid="assumption-kept">{r.dropped}</span></Notice>}
      {a && a.error && o === 'error' && <Notice tone="bad">{plainQueryError(a.error, q.fieldNames)}{askSignIn && <button type="button" data-testid="signin-again" disabled={connBusy} className="sem-btn sem-btn-sm sem-btn-primary" onClick={askSignIn}>{SIGN_IN_AGAIN}</button>}</Notice>}
    </div>
    {hasRows && <QueryStalenessChip />}
    {hasRows && q.rule
      ? <div className={`qn-visual ${running ? 'qv-stale' : ''}`} data-testid="answer-visual">
        {q.kind === 'table'
          ? (tableShown ? <><Visual kind="table" data={tableShown} />
            {q.tableData && q.tableData.rows.length > TABLE_ROWS_SHOWN && <div className="sem-text-meta" data-testid="answer-table-note">Showing the first {TABLE_ROWS_SHOWN} of {q.tableData.rows.length.toLocaleString('en-US')} rows. The Table view has all of them.</div>}</>
            : <div className="sem-text-meta">This result is shown as rows in the Table view.</div>)
          // A single number is already the figure above: its total card would say it twice, so it is drawn only when it has more to say (a prior to compare with).
          : q.visual && !(q.kind === 'total' && q.visual.total && q.visual.total.prior == null) ? <Visual kind={q.kind} data={q.visual} /> : null}
        <div className="qn-visual-foot"><VisualSwitch q={q} /><MoreMenu q={q} explainOpen={explainOpen} /></div>
      </div>
      : <div className="qv-empty-answer" data-testid="answer-empty">
        {!q.res && !q.err && !running && !a && r.id === DRAFT && conn?.connected && <div className="sem-text-meta">The answer appears here, with the visual that shows it best.</div>}
        {!conn?.connected && <ConnectBar hint="Connect a model that holds data to run queries." />}
        {q.res && !hasRows && !running && <div className="sem-text-meta">Nothing was added up, so no total is shown in its place.</div>}
      </div>}
    {a && q.chipsHere && o === 'answered' && a.why && <div className="qv-check-part" data-testid="askdata-why">
      <ExplainInline d={a.why} measureName={a.columns?.[a.columns.length - 1]?.name ?? a.why.name ?? ''} format={a.formats?.[a.formats.length - 1] ?? null} />
    </div>}
    {learnActive(q.learn) && <ExampleAnswers example={q.learn.tour.example} />}
    {askOn && q.entry === 'words' && !q.askState.asking && !q.askState.answer && q.askState.parts && (q.askState.parts.measures ?? []).length > 0 && (
      <div className="qv-tools" data-testid="askdata-idle"><span className="sem-text-meta">This question has not run since you came back.</span>
        <button type="button" className="sem-btn sem-btn-sm sem-btn-primary" onClick={() => q.askBox.askParts(q.askState.parts!)}>Run</button></div>
    )}
  </div>;
}

// ---------------------------------------------------------------- DAX: fields, or the query as written

function DaxBody({ q, nb }: { q: Q; nb: Notebook }) {
  const { conn, openConnections } = useConnection();
  const askOn = useFeature('askData') === 'granted';
  const [copied, setCopied] = useState(false);
  const [fieldsOpen, setFieldsOpen] = useState(false);
  const holds = q.chipsHere || q.wordsOnly;   // a question holds the query; the wells and their query then belong to another row
  const built = q.entry === 'build' && !holds && !q.assistantShown;
  const wellsShown = q.entry === 'build' && !q.assistantShown && !holds;
  const shownDax = q.assistantShown ? (q.assistantDax ?? '') : holds || q.entry === 'dax' ? q.query : q.personQuery();
  const hand = askOn && q.chipsHere && !q.askOwns;
  useEffect(() => { setCopied(false); }, [shownDax]);
  const insert = (text: string) => {
    if (q.entry === 'dax' || holds) { q.editorRef.current?.insert(text); return; }
    const f = parseRef(text); if (!f) return;
    if (q.chipsHere) q.leaveQuestion(false);
    if (f.kind === 'measure' && !q.config.values.some((v) => v.ref === f.ref)) q.onWells({ ...q.config, values: [...q.config.values, f] });
    else if (f.kind === 'column' && !q.config.rows.some((v) => v.ref === f.ref)) q.onWells({ ...q.config, rows: [...q.config.rows, f] });
  };
  return <div className="qn-sec" data-testid="dax-card">
    <div className="qn-row-tools">
      <span className="sem-seg" role="group" aria-label="How to write the query">
        <button type="button" className="sem-seg-item" aria-pressed={q.entry === 'build' && !holds} onClick={() => q.chooseEntry('build')} data-testid="mode-build">Fields</button>
        <button type="button" className="sem-seg-item" aria-pressed={q.entry !== 'build' || holds} onClick={() => q.chooseEntry('dax')} data-testid="mode-dax">DAX</button>
      </span>
      {q.fieldsNote && <span className="sem-text-meta" role="status" data-testid="fields-note">{q.fieldsNote}</span>}
      {q.idle
        ? <button type="button" className="sem-btn sem-btn-sm" onClick={q.stopQuery}>Stop</button>
        : <button type="button" className="sem-btn sem-btn-sm sem-btn-primary" disabled={!conn?.connected} onClick={() => { nb.setTab('answer'); q.run(); }} data-testid="dax-run">Run</button>}
      <button type="button" className="sem-btn sem-btn-sm" onClick={() => { void navigator.clipboard?.writeText(shownDax).then(() => setCopied(true)).catch(() => undefined); }} data-testid="dax-copy">{copied ? 'Copied' : 'Copy'}</button>
      <span className="qn-rel">
        <button type="button" className="sem-btn sem-btn-sm sem-btn-quiet" aria-expanded={fieldsOpen} onClick={() => setFieldsOpen(!fieldsOpen)} data-testid="fold-fields">Add field</button>
        {fieldsOpen && <Pop onClose={() => setFieldsOpen(false)} label="Fields" className="qn-fieldpop"><div className="qv-fieldbox" data-testid="queries-fields"><FieldsPanel model={q.model} inUse={q.fieldsInUse} onInsert={insert} expanded /></div></Pop>}
      </span>
      <button type="button" className="sem-btn sem-btn-sm sem-btn-quiet" onClick={() => nb.setTab('compare')} data-testid="dax-compare">Compare a rewrite</button>
      <button type="button" className="sem-btn sem-btn-sm sem-btn-quiet" onClick={q.resetDefault} disabled={q.idle} data-testid="query-reset" title="Clear the fields and the query">Reset</button>
      <span className="sem-text-meta qn-push">Ctrl+Enter runs</span>
    </div>
    {wellsShown && <QueryWells config={q.config} onConfig={q.onWells} onCompare={q.compareValue} compareBadge={q.compareBadge} model={q.model} disabled={q.busy === 'ask'} />}
    {q.assistantShown
      ? <div className="qv-daxbox" data-testid="queries-assistant-box"><pre className="qv-dax-readonly" data-testid="queries-assistant-dax">{q.assistantDax}</pre></div>
      : built
        ? <div className="qv-daxbox">{q.wellsReady ? <pre className="qv-dax-readonly" data-testid="dax-code">{shownDax}</pre> : <div className="sem-text-meta" data-testid="dax-code-none">Choose a number above, and the query appears here. Or press DAX to write one.</div>}</div>
        : <div className="qv-daxbox" data-testid="queries-editor"><DaxEditor ref={q.editorRef} value={q.query} onChange={q.setQuery} model={q.model} minHeight={40} placeholder="EVALUATE TOPN(100, 'Sales')   ·   Ctrl+Space for suggestions" /></div>}
    {q.assistantShown && <div className="qv-note" data-testid="dax-assistant"><span>The query your assistant ran. Your own {q.mode === 'query' ? 'query is' : 'fields are'} kept.</span>
      <button type="button" className="sem-btn sem-btn-sm sem-btn-quiet" disabled={q.idle} onClick={q.backToMine}>{q.mode === 'query' ? 'Back to my query' : 'Back to my fields'}</button></div>}
    {hand && <div className="qv-note qv-note-warn" data-testid="dax-edited">You changed the query by hand, so the Read as parts no longer describe it.
      <button type="button" className="sem-btn sem-btn-sm sem-btn-quiet" disabled={q.idle} onClick={() => q.askBox.askParts(q.askState.parts!)} data-testid="dax-rebuild">Build from the parts again</button></div>}
    {q.putBack && squash(q.putBack) !== squash(q.query) && squash(q.query) === squash(q.generatedQuery) && <div className="qv-note" data-testid="queries-putback">The question replaced the query you wrote.
      <button type="button" className="sem-btn sem-btn-sm sem-btn-quiet" disabled={q.idle} onClick={() => { q.setQuery(q.putBack); q.setPutBack(''); q.chooseEntry('dax'); }}>Put back my query</button></div>}
    <div className="qv-runs-on"><span className="sem-text-meta">Runs on</span><b data-testid="runs-on">{conn?.connected ? (conn.database || conn.dataSource || 'the connected model') : 'no model yet'}</b>
      <button type="button" className="sem-btn sem-btn-sm sem-btn-quiet" data-testid="change-model" onClick={openConnections}>{conn?.connected ? 'Change' : 'Choose test model'}</button></div>
  </div>;
}

// ---------------------------------------------------------------- table: every row

function TableBody({ q, explainIt }: { q: Q; explainIt: (p: Parameters<Q['setExplain']>[0]) => void }) {
  // Grid is the rows; Matrix is the same numbers as a grid of the splits, where hovering a number shows the filter context behind it.
  const [view, setView] = useState<'grid' | 'matrix'>('grid');
  // Matrix puts the last split across the top; where that changes nothing it is not offered, since it would look the same as Grid.
  const matrixConfig = matrixConfigOf(q.chartConfig);
  const matrixOk = !!matrixConfig;
  const res = q.res;
  const wrap = useRef<HTMLDivElement>(null);
  const [h, setH] = useState(360);
  useEffect(() => {
    const el = wrap.current; if (!el) return;
    // The table takes the page height under it, so thirty rows are not shown eleven at a time with the page empty below (UAT 18 Oct, N21).
    const place = () => { el.style.setProperty('--qv-top', `${Math.round(el.getBoundingClientRect().top)}px`); el.style.setProperty('--qv-n', String(res?.rows.length ?? 0)); };
    place();
    const ro = new ResizeObserver(() => { place(); setH(Math.max(160, Math.floor(el.clientHeight))); });
    ro.observe(el); setH(Math.max(160, Math.floor(el.clientHeight)));
    window.addEventListener('resize', place);
    return () => { ro.disconnect(); window.removeEventListener('resize', place); };
  }, [res]);
  if (!(res && q.shownRes)) return <div className="sem-text-meta" data-testid="result-empty">{q.busy === 'run' || q.busy === 'ask' || q.busy === 'debug' ? 'Running the query.' : q.err ? '' : (q.entry === 'dax' || q.chipsHere || q.wordsOnly ? 'Press Run to see the rows here.' : q.wellsReady ? 'Press Run to build this result.' : 'Add a measure to build a result.')}</div>;
  return <div className="qn-sec" data-testid="result" data-primary="result">
    <div className="qn-row-tools"><span className="sem-text-meta qv-num" title={queryIdentity(res.query || q.resQuery) ?? undefined}>{rowAnnouncement(res.rowCount, res.truncated)} · {timingAnnouncement(res.elapsedMs)}</span>
      <span className="sem-text-meta">Press a number to explain it.{!q.shared && ' Right-click it for Calculation steps.'}</span>
      {matrixOk && <span className="sem-seg qn-push" role="group" aria-label="Show the result as">
        <button type="button" className="sem-seg-item" aria-pressed={view === 'grid'} onClick={() => setView('grid')} data-testid="result-grid-view">Grid</button>
        <button type="button" className="sem-seg-item" aria-pressed={view === 'matrix'} onClick={() => setView('matrix')} data-testid="result-matrix-view">Matrix</button></span>}</div>
    <div className="qv-grid-wrap" ref={wrap} data-testid="queries-result">
      {view === 'matrix' && matrixConfig
        ? <DaxVisual res={q.shownRes} config={matrixConfig} viz="matrix" height={h} onExplain={(p) => explainIt(p)} chosenOrder />
        : <ResultGrid columns={q.shownRes.columns} rows={gridRowsInTimeOrder(q.shownRes, q.chartConfig)} height={h} truncated={res.truncated} framed={false} friendly countElsewhere
          onCellClick={q.chartConfig ? (row, ci) => { const p = explainPayloadFromRow(q.chartConfig!, row, ci, q.shownRes!.columns[ci]?.format); if (p) explainIt(p); } : undefined}
          onCellMenu={q.chartConfig ? (row, ci) => { const p = explainPayloadFromRow(q.chartConfig!, row, ci, q.shownRes!.columns[ci]?.format); if (p) { q.onExplainNumber(p); return true; } return false; } : undefined} />}
    </div>
  </div>;
}

// ---------------------------------------------------------------- speed: first and repeat runs, then where the time went

/** Whole seconds since `on` turned true: a measure that takes a while says it is still going. */
function useElapsed(on: boolean): number {
  const [n, setN] = useState(0);
  useEffect(() => {
    if (!on) { setN(0); return undefined; }
    const t0 = Date.now();
    const id = setInterval(() => setN(Math.floor((Date.now() - t0) / 1000)), 1000);
    return () => clearInterval(id);
  }, [on]);
  return n;
}

function SpeedBody({ q, r, timingAll }: { q: Q; r: Rec; timingAll?: boolean }) {
  const { conn } = useConnection();
  const [options, setOptions] = useState(false);
  const [where, setWhere] = useState(false);
  // The numbers are this row's when they were measured for its own query, also when the rows on the page are another query's (a kept row opened
  // and measured without being run): they were not shown at all, so Measure speed seemed to do nothing (UAT 17 Oct).
  const own = q.ownSpeed || (!!q.speedQuery && !!r.dax && squash(q.speedQuery) === squash(r.dax));
  const cw = own ? q.cw : null, bench = own ? q.bench : null, t = own ? q.timings : null;
  const off = !conn?.connected || q.idle || q.emptyQuery;
  // Time all holds the page busy while it measures OTHER rows: this row is not being measured, so it never says so (hero 25 H10).
  const measuring = q.busy === 'measure' && !timingAll;
  const seconds = useElapsed(measuring);
  const measured = !!(cw || bench || t);
  // The headline of the repeat runs is the middle one, as its label says (v-batch1: the average sat under the word "middle").
  const coldAvg = cw?.coldTotal?.n ? cw.coldTotal.avgMs : null, warmMid = cw?.warmTotal?.n ? (cw.warmTotal.runsMs?.length ? median(cw.warmTotal.runsMs) : cw.warmTotal.avgMs) : null;
  const times = coldAvg && warmMid ? coldAvg / Math.max(1, warmMid) : null;
  const runs = cw?.detail.length ? cw.detail.map((d) => d.totalMs) : bench?.runsMs?.length ? bench.runsMs : [];
  const insight = speedInsight(cw, t, bench);
  const fast = isFast(cw, t, bench);
  const wait = waitMs(cw, t, bench);
  const maxTotal = Math.max(coldAvg ?? 0, warmMid ?? 0, t?.totalMs ?? 0);
  // A bar is drawn for a split that exists: the cold and repeat pair when cold runs were made, else the one profiled run (a service model skips the cold runs).
  const pairSplit = !!(cw && cw.traceAvailable && cw.coldSe.n > 0);
  return <div className="qn-sec" data-testid="speed-card">
    <div className="qn-row-tools">
      <button type="button" className="sem-btn sem-btn-sm sem-btn-primary" disabled={off} onClick={q.measureSpeed} data-testid="speed-start"
        title="Run the query first with nothing kept, then again to see repeat speed, then record where the time went">{measuring ? 'Measuring…' : measured ? 'Measure again' : 'Measure speed'}</button>
      {measured && <button type="button" className="sem-btn sem-btn-sm sem-btn-quiet" aria-expanded={where} onClick={() => setWhere(!where)} data-testid="fold-time">Where the time went</button>}
      <button type="button" className="sem-btn sem-btn-sm sem-btn-quiet" aria-expanded={options} onClick={() => setOptions(!options)} data-testid="speed-options">Options</button>
      <span className="sem-text-meta qn-push">{measuring ? '' : q.speedByAssistant && q.speedIsShown && q.res ? 'Timed by your assistant' : measured ? 'Measured for the result on screen' : ''}</span>
    </div>
    {options && <div className="qn-sub" data-testid="speed-options-body">
      <div className="qv-panel-tools">
        <button type="button" className="sem-btn sem-btn-sm" disabled={off} onClick={q.runQuick} data-testid="how-quick" title="Measure elapsed query time without clearing cached results">Quick</button>
        <button type="button" className="sem-btn sem-btn-sm" disabled={off} onClick={q.runProfile} data-testid="how-profile" title="Only record where the time went: the formula and storage split and the heaviest scans">Profile only</button>
        <button type="button" className="sem-btn sem-btn-sm sem-btn-quiet" disabled={off} onClick={() => void q.clearCache('speed')} data-testid="clear-cache"
          title="Throw away results the server kept from earlier runs, so the next run starts cold. Needs a local Power BI Desktop or admin access.">Clear kept results</button>
        {(q.busy === 'quick' || q.busy === 'profile' || q.busy === 'clear') && <span className="sem-text-meta" role="status">{q.busy === 'quick' ? 'Running…' : q.busy === 'profile' ? 'Profiling…' : 'Clearing…'}</span>}
      </div>
      <div className="qv-panel-tools">
        <label className="sem-text-meta qv-inline">runs<input id="lab-runs" type="number" min={1} max={25} value={q.runs} onChange={(e) => q.setRuns(Math.max(1, Math.min(25, Number(e.target.value) || 1)))} className="sem-field sem-field-sm qv-runs-n" data-testid="runs-n" /></label>
        <label className="sem-text-meta qv-inline"><input type="checkbox" checked={q.clearOnRun && (!q.shared || q.confirmShared)} disabled={!!q.shared && !q.confirmShared} onChange={(e) => q.setClearOnRun(e.target.checked)} data-testid="clear-first" />Clear kept results before each first run</label>
      </div>
      {q.shared && <label className="sem-text-meta qv-inline"><span className="xe-dot xe-dot-warn" aria-hidden="true" /><input type="checkbox" checked={q.confirmShared} onChange={(e) => q.setConfirmShared(e.target.checked)} />Shared endpoint: clearing the cache affects all users</label>}
    </div>}
    {q.clearMsg && <div className="sem-text-meta xe-dot xe-dot-good">{q.clearMsg}</div>}
    {q.clearRefusal?.where === 'speed' && <div className="sem-text-meta xe-dot xe-dot-warn" data-testid="clear-refusal">{q.clearRefusal.text}</div>}
    {/* Hero 26 N6: a service model gives the repeat time only (the first run cannot be told), and that row is still timed. */}
    {!measured && (r.coldMs != null || r.warmMs != null) && <div className="sem-text-meta" data-testid="row-timed">Last timed: {r.coldMs != null && r.warmMs != null ? `first run ${msWord(r.coldMs)}, repeat runs ${msWord(r.warmMs)}` : r.warmMs != null ? `repeat runs ${msWord(r.warmMs)}` : `first run ${msWord(r.coldMs as number)}`}.</div>}
    {measuring && <div className="qn-running" role="status" data-testid="speed-progress"><span className="qn-spin" aria-hidden="true" /><span>{q.measureStep ?? 'Measuring'}… {seconds} s</span></div>}
    {q.emptyQuery && <div className="sem-text-meta" data-testid="speed-nothing">There is no query yet. Choose a number in Fields, or write a query in DAX.</div>}
    {!q.emptyQuery && !measuring && !measured && r.coldMs == null && r.warmMs == null && <div className="sem-text-meta" data-testid="queries-speed-none">Not measured yet. Press Measure speed to time this result.</div>}
    {measured && insight && <div className="qv-insight" data-testid="speed-insight">{insight}</div>}
    {measured && <div className="qv-speed-figs" data-testid="queries-speed-answer">
      {fast && wait !== null ? <div><div className="sem-label">Time</div><div className="qv-big qv-num">{msWord(wait)}</div></div> : <>
      {coldAvg !== null && <div><div className="sem-label">First run, nothing kept</div><div className="qv-big qv-num">{msWord(coldAvg)}</div></div>}
      {warmMid !== null && <div><div className="sem-label">Repeat runs, middle of {cw!.warmTotal.n}</div><div className="qv-big qv-num">{msWord(warmMid)}</div></div>}
      {bench && <div><div className="sem-label">{cw ? 'Quick, fastest repeat run' : 'Repeat runs, fastest'}</div><div className="qv-big qv-num">{msWord(bench.warmMinMs)}</div></div>}
      {t && !cw && !bench && <div><div className="sem-label">Server query</div><div className="qv-big qv-num">{msWord(t.totalMs)}</div></div>}
      {times !== null && <div className="qv-speed-say">{times >= 3 ? `Repeat runs are ${Math.round(times)} times faster.` : times >= 1.05 ? `Repeat runs are only ${times.toFixed(1)} times faster.` : times > 0.95 ? 'Repeat runs take about as long as the first run.' : 'Repeat runs are slower than the first run.'}</div>}
      </>}
    </div>}
    {!fast && cw && cw.traceAvailable && cw.coldSe.n > 0 && <div className="qv-splits">
      <SplitBar label="First run" total={cw.coldTotal.avgMs} se={Math.min(cw.coldSe.avgMs, cw.coldTotal.avgMs)} max={maxTotal} />
      {cw.warmSe.n > 0 && <SplitBar label="Repeat runs" total={cw.warmTotal.avgMs} se={Math.min(cw.warmSe.avgMs, cw.warmTotal.avgMs)} max={maxTotal} />}
    </div>}
    {!fast && !(cw && cw.traceAvailable && cw.coldSe.n > 0) && t && t.traceAvailable && t.seQueries > 0 && <div className="qv-splits"><SplitBar label="This run" total={t.totalMs} se={Math.min(t.seMs, t.totalMs)} max={t.totalMs} note={t.seCacheHits ? `${t.seCacheHits} of ${t.seQueries} from memory` : undefined} /></div>}
    {!fast && measured && ((cw && cw.traceAvailable && cw.coldSe.n > 0) || (t && t.traceAvailable && t.seQueries > 0)) && <div className="qv-legend"><span className="qv-key qv-key-se" />Reading data<span className="qv-key qv-key-fe" />Calculating</div>}
    {!fast && cw && !cw.cacheClearAvailable && <Banner color="var(--sem-warn)">{plainEngineNote(cw.note) || 'Cache could not be cleared, so the first run and the repeat runs are about the same.'}</Banner>}
    {where && measured && <TimeDetail q={q} runs={runs} own={own} />}
  </div>;
}

function TimeDetail({ q, runs, own }: { q: Q; runs: number[]; own: boolean }) {
  const [read, setRead] = useState<number | null>(null);
  const [everyRun, setEveryRun] = useState(false);
  const cw = own ? q.cw : null, bench = own ? q.bench : null, t = own ? q.timings : null;
  const allScans = t?.scans ?? [];
  const scans = visibleScans(allScans);
  const totalScan = scans.reduce((n, s) => n + s.durationMs, 0) || 1;
  let at = 0;
  // A column is drawn only for runs that happened: no "First run" column of "Not run" when nothing was run cold.
  const cols = cw ? [{ id: 'first', head: 'First run', s: cw.coldTotal }, { id: 'repeat', head: 'Repeat runs', s: cw.warmTotal }].filter((c) => c.s.n > 0) : [];
  const showSe = !!cw?.detail.some((d) => d.profiled !== false && d.seMs >= 1);
  return <div className="qn-sub" data-testid="time-card">
    {runs.length > 1 && <RunsStrip runs={runs} cold={cw?.detail.length ? cw.detail.map((d) => d.cold) : undefined} />}
    {cols.length > 0 && <div className="qv-check-part">
      <div className="sem-label" title="What you wait for: the server time plus the network and the page">Round trip.{cols.length === 1 ? (cols[0].id === 'repeat' ? ' Repeat runs only.' : ' First run only.') : ''}</div>
      <table className="qv-table" data-testid="coldwarm">{cols.length > 1 && <thead><tr><th>{''}</th>{cols.map((c) => <th key={c.id} className="qv-r">{c.head}</th>)}</tr></thead>}
        <tbody>
          <tr><td>Average</td>{cols.map((c) => <td key={c.id} className="qv-r qv-num">{timingAnnouncement(c.s.avgMs)}</td>)}</tr>
          <tr><td>Fastest</td>{cols.map((c) => <td key={c.id} className="qv-r qv-num">{timingAnnouncement(c.s.minMs)}</td>)}</tr>
          <tr><td>Slowest</td>{cols.map((c) => <td key={c.id} className="qv-r qv-num">{timingAnnouncement(c.s.maxMs)}</td>)}</tr>
          <tr><td>Spread</td>{cols.map((c) => <td key={c.id} className="qv-r qv-num">{`±${c.s.stdDevMs.toFixed(1)} ms`}</td>)}</tr>
        </tbody></table>
    </div>}
    {t && !t.traceAvailable && <TraceNote note={t.note || 'Storage timing data was unavailable.'} kind="profileUnavailable" />}
    {t && t.traceAvailable && <div className="qv-check-part">
      <div className="qv-split-figs qv-num">
        <span className="sem-text-meta">One profiled run:</span>
        <span><b>{t.totalMs} ms</b> on the server</span>
        {t.wallMs != null && <span title="What you wait for: the server time plus the network and the page">{t.wallMs} ms round trip</span>}
        <span><b>{t.seQueries}</b> storage {t.seQueries === 1 ? 'query' : 'queries'}</span>
        {t.seCacheHits != null && t.seCacheHits > 0 && <span>{t.seCacheHits} from the storage cache</span>}
        {t.seCpuMs >= 1 && <span>{t.seCpuMs} ms storage CPU</span>}{t.seParallelism > 0 && <span>{t.seParallelism}× parallelism</span>}
      </div>
      {allScans.length === 0 ? <TraceNote note={t.note || 'No storage scan events were returned.'} kind={t.seQueries === 0 ? 'profileEmpty' : 'scanDetailUnavailable'} />
        : scans.length === 0 ? <div className="sem-text-meta">No scan took 1 ms or more.</div>
        : <><div className="sem-label">Heaviest scans, in order (press one to see what it asked)</div>
          <table className="qv-table qv-seq"><tbody>{scans.map((s, i) => { const left = (at / totalScan) * 100; at += s.durationMs; return <Fragment key={i}>
            <tr className="qv-click" onClick={() => setRead(read === i ? null : i)} data-testid={`seq-${i}`}><td>{scanKind(s.subclass)}{s.line != null ? ` ${s.line}` : ''}</td>
              <td className="qv-seq-bar-h"><span className="qv-seq-bar" style={{ marginLeft: `${left}%`, width: `${Math.max(1, (s.durationMs / totalScan) * 100)}%` }} /></td>
              <td className="qv-r qv-num">{s.durationMs} ms</td><td className="qv-r qv-num">{s.rows.toLocaleString('en-US')} rows</td></tr>
            {read === i && <tr><td colSpan={4}><code className="qv-xm">{s.query || 'The endpoint did not return the text of this scan.'}</code></td></tr>}</Fragment>; })}</tbody></table></>}
    </div>}
    {(cw?.detail.length || bench?.runsMs?.length) ? <div className="qv-check-part">
      <button type="button" className="sem-btn sem-btn-sm sem-btn-quiet" aria-expanded={everyRun} onClick={() => setEveryRun(!everyRun)} data-testid="fold-every-run">{everyRun ? 'Hide every run' : 'Show every run'}</button>
      {everyRun && <table className="qv-table" data-testid="every-run"><thead><tr><th>Run</th><th>Kind</th><th className="qv-r">Round trip</th>{cw && showSe && <th className="qv-r">Storage</th>}</tr></thead>
        <tbody>
          {cw?.detail.map((d, i) => <tr key={`c${i}`}><td>{d.index}</td><td>{d.cold ? 'first' : 'repeat'}</td><td className="qv-r qv-num">{timingAnnouncement(d.totalMs)}</td>{showSe && <td className="qv-r qv-num">{d.profiled === false ? '' : msWord(d.seMs)}</td>}</tr>)}
          {bench?.runsMs.map((ms, i) => <tr key={`b${i}`}><td>{i + 1}</td><td>{i === 0 ? 'first (quick)' : 'repeat (quick)'}</td><td className="qv-r qv-num">{timingAnnouncement(ms)}</td>{cw && showSe && <td />}</tr>)}
        </tbody></table>}
    </div> : null}
  </div>;
}

// ---------------------------------------------------------------- explain

function ExplainBody({ q }: { q: Q }) {
  const sel = q.explainShown;
  return <div className="qn-sec" data-testid="explain-card">
    {sel
      ? <ExplainPanel payload={sel} inline />
      : q.explainChoices.length > 0
        ? <div className="ex-pick" data-testid="explain-pick">
          <div className="sem-text-meta">Which number? Press one.</div>
          {q.explainChoices.map((p, i) => <button key={i} type="button" className="ex-pick-row" data-testid="explain-pick-row" onClick={() => q.setExplain(p)}>
            <span>{p.entries.filter((e) => e.src === 'rows' || e.src === 'cols').map((e) => plainValue(e.value)).join(', ') || p.measureName}</span>
            <span className="qv-num">{p.value}</span>
          </button>)}
          {q.explainRowCount > q.explainChoices.length && <div className="sem-text-meta">Press a number in the Table for the others.</div>}
        </div>
        : <div className="sem-text-meta" data-testid="explain-none">{q.res ? 'Press a number in the Table to explain it. A result written by hand has no measure to explain.' : 'No number yet. Run a query, then press a number in the Table.'}</div>}
  </div>;
}

// ---------------------------------------------------------------- compare a rewrite, and the steps

function CompareTab({ q, r }: { q: Q; r: Rec | null }) {
  const { conn } = useConnection();
  // A row kept across a reload has not run, so the page shows no measure of its own: Compare starts from the number the row kept.
  const kept = r?.parts?.measures?.[0];
  const { exprA, setExprA } = q;
  useEffect(() => { if (!exprA && kept) setExprA(`[${kept.replace(/]/g, ']]')}]`); }, [exprA, kept]);   // eslint-disable-line react-hooks/exhaustive-deps
  return <div className="qn-sec" data-testid="compare-card">
    {q.compareBadge && <span className="xe-dot sem-text-meta" style={{ '--xe-dot': q.compareBadge.color } as React.CSSProperties} data-testid="queries-compare-verdict">Last comparison: {q.compareBadge.text}</span>}
    <div data-testid="queries-sheet-compare"><CompareBody {...{ exprA: q.exprA, setExprA: q.setExprA, exprB: q.exprB, setExprB: q.setExprB, groupByDerived: q.groupByDerived, filterLinesDerived: q.filterLinesDerived, conn, busy: q.busy, runVerify: q.runVerify, eqEv: q.eqEv, eqStale: q.eqStale, vErr: q.vErr, customVerify: q.customVerify, setVerifyGroupBy: q.setVerifyGroupBy, subject: q.compareSubject, compareChoices: q.compareChoices, compareChosen: q.compareChosen, onPickCompare: q.pickCompare, measureFormats: q.formatMeasures, filterWordsShown: (q.qParts?.filters ?? []).map(filterWords) }}
      onUseVisual={() => q.setCustomVerify(false)} /></div>
  </div>;
}

function StepsBody({ q }: { q: Q }) {
  const [kind, setKind] = useState<'logical' | 'physical'>('logical');
  // A refusal belongs to the row it was pressed on: closing this card or opening another row puts it away (pro UAT 18 Oct, N7).
  useEffect(() => () => q.setClearRefusal(null), []);   // eslint-disable-line react-hooks/exhaustive-deps
  const { conn } = useConnection();
  const off = !conn?.connected || q.idle || q.emptyQuery;
  const plan = q.ownSpeed ? q.plan : null;
  const tree = plan ? (kind === 'logical' ? plan.logicalTree : plan.physicalTree) : [];
  const a = q.askState.answer;
  const passed = (a?.checks ?? []).filter((c) => c.passed);
  const hasRows = !!q.res && (q.res.rowCount ?? q.res.rows.length) > 0 && !q.err;
  return <div className="qn-sec" data-testid="steps-card">
    {q.emptyQuery && <div className="sem-text-meta" data-testid="steps-nothing">There is no query yet. Choose a number in Fields, or write a query in DAX.</div>}
    {(passed.length > 0 || hasRows) && <ul className="qv-checks" data-testid="checks">
      {hasRows && q.res && <li>✓ {q.chipsHere && q.answerRows ? 'Checked' : 'Ran'} · {rowAnnouncement(q.res.rowCount, q.res.truncated)} · {timingAnnouncement(q.res.elapsedMs)}</li>}
      {passed.filter((c) => c.name !== 'rows').map((c) => <li key={c.name + c.line}>✓ {c.line}</li>)}
    </ul>}
    <div className="qv-check-part" data-testid="queries-server-steps"><div className="qv-panel-tools"><span className="sem-label">Query plan</span>
      <span className="sem-seg qv-push"><button type="button" className="sem-seg-item" aria-pressed={kind === 'logical'} onClick={() => setKind('logical')} data-testid="plan-logical">Logical</button>
        <button type="button" className="sem-seg-item" aria-pressed={kind === 'physical'} onClick={() => setKind('physical')} data-testid="plan-physical">Physical</button></span>
      <button type="button" className="sem-btn sem-btn-sm" disabled={off} onClick={q.runPlan} data-testid="plan-record">{q.busy === 'plan' ? 'Recording…' : plan ? 'Record again' : 'Record the steps'}</button>
      <button type="button" className="sem-btn sem-btn-sm sem-btn-quiet" disabled={off} onClick={() => void q.clearThenPlan()} title="Throw away results the server kept from earlier runs, then record the steps. The server may still return nothing.">Clear kept results, then record</button></div>
      {q.clearRefusal?.where === 'steps' && <div className="sem-text-meta xe-dot xe-dot-warn" data-testid="steps-clear-refusal">{q.clearRefusal.text}{q.shared && <label className="qv-inline"> <input type="checkbox" checked={q.confirmShared} onChange={(e) => q.setConfirmShared(e.target.checked)} data-testid="steps-confirm-shared" />Clear it</label>}</div>}
      {plan && !plan.traceAvailable && <div className="sem-text-meta xe-dot xe-dot-warn">{plainEngineNote(plan.note) || 'unavailable on this endpoint (needs local Power BI Desktop or admin XMLA)'}</div>}
      {plan && plan.traceAvailable ? <pre className="qv-plan" data-testid="plan">{tree.length ? tree.map((n, i) => <span key={i} title={n.detail || undefined}>{`${'  '.repeat(Math.min(n.level, 24))}${n.operator}${n.records != null ? `  #${n.records.toLocaleString('en-US')}` : ''}${i < tree.length - 1 ? '\n' : ''}`}</span>) : 'No result'}</pre>
        : !plan && <div className="sem-text-meta">No steps recorded yet. Some servers do not provide this detail.</div>}
    </div>
    <div className="qv-check-part" data-testid="queries-steps"><div className="qv-panel-tools"><span className="sem-label">Calculation steps{q.logs ? ` · ${q.logs.length} step${q.logs.length === 1 ? '' : 's'}` : ''}</span>
      {!q.shared && <button type="button" className="sem-btn sem-btn-sm qv-push" disabled={off} onClick={() => void q.debug()} data-testid="log-measures">{q.busy === 'debug' ? 'Running…' : q.logs ? 'Log again' : 'Log each measure'}</button>}</div>
      {!q.shared && <div className="sem-text-meta">Running this replaces the result with the logged run.</div>}
      {q.shared && !q.logs?.length && <div className="sem-text-meta" data-testid="steps-service-note">This server does not send calculation steps (EVALUATEANDLOG works in Power BI Desktop only).</div>}
      {q.logNote && !q.shared && <TraceNote note={q.logNote} kind="debug" />}
      {!q.shared && !q.logs && !q.logNote && q.busy !== 'debug' && <div className="sem-text-meta">No calculation steps have been logged. Choose Log each measure in Fields, or put EVALUATEANDLOG(expr, "label") around a part of your query.</div>}
      {q.logs && q.logs.length > 0 && <div className="qv-steplist" data-testid="measure-log">{q.logs.map((e, i) => <div key={i} className="qv-step" data-testid="step-row">
        <div className="qv-step-head"><b>{e.label || `log ${i + 1}`}</b><span className="truncate sem-text-meta" title={e.expression}>{e.expression}</span><span className="qv-r qv-num sem-text-meta">{e.rowCount} {e.rowCount === 1 ? 'row' : 'rows'}</span></div>
        {e.columns.length > 0 && <ResultGrid columns={e.columns.map((c) => ({ name: c }))} rows={e.rows} height={Math.min(180, 34 + e.rows.length * 26)} filterable={false} friendly framed={false} />}
      </div>)}</div>}
    </div>
  </div>;
}

// ---------------------------------------------------------------- saving a check

interface SavedCheck { id: string; title: string; kind: string; createdWhen?: string }
const KIND_WORD: Record<string, string> = { measureValue: 'trusted answer', measureReconcile: 'against SQL' };

function SaveBody({ q, onOpenTests }: { q: Q; onOpenTests?: () => void }) {
  const [saved, setSaved] = useState<SavedCheck[] | null>(null);
  useEffect(() => {
    let alive = true;
    rpc<{ definitions?: SavedCheck[] }>('listTests').then((r) => { if (alive) setSaved(r.definitions ?? []); }).catch(() => { if (alive) setSaved([]); });
    return () => { alive = false; };
  }, [q.checkSaved, q.persistKey]);
  const canNumber = q.askOwns && q.canSaveAnswer;
  return <div className="qn-sec" data-testid="save-card">
    <div className="sem-label">Save as a check</div>
    <div className="qv-saveopts">
      {q.chipsHere
        ? <button type="button" className="qv-saveopt" disabled={!canNumber} onClick={() => q.setSaving({ row: 0, measure: 0 })} data-testid="save-number">
          <b>Save a number from this answer</b><span className="sem-text-meta">{canNumber ? 'Pick the number and the row it should stay at. The question\'s filters travel with it.' : q.askOwns ? 'This answer has no rows to keep.' : 'You changed the query by hand. Build from the parts again to save a number from the answer.'}</span></button>
        : <>
          <button type="button" className="qv-saveopt" onClick={() => q.openMeasureCheck('measureReconcile')} data-testid="save-sql"><b>Check against SQL</b><span className="sem-text-meta">Compare this number with SQL you trust.</span></button>
          <button type="button" className="qv-saveopt" onClick={() => q.openMeasureCheck('measureValue')} data-testid="save-trusted"><b>Check a trusted answer</b><span className="sem-text-meta">Save the number you expect.</span></button>
          {q.editedExpression && <div className="sem-text-meta">You have edited the formula, so the next screen asks whether to check the measure as saved or this expression.</div>}
        </>}
    </div>
    {q.saving && q.askState.answer && canNumber && (
      <SaveChooser answer={q.askState.answer} value={q.saving} onChange={q.setSaving} onCancel={() => q.setSaving(null)}
        onContinue={() => { const d = checkDraftOf(q.askState.answer!, q.saving!.row, q.saving!.measure, q.model.measures); if (d) { q.setAnswerDraft(d); q.setSaving(null); } }}
        ready={!!checkDraftOf(q.askState.answer, q.saving.row, q.saving.measure, q.model.measures)} />
    )}
    {q.checkSaved && <div className="qv-note" data-testid="saved">Check saved.{onOpenTests && <button type="button" className="sem-btn sem-btn-sm sem-btn-quiet" data-testid="open-validation" onClick={onOpenTests}>Open Validation</button>}</div>}
    <div className="qv-check-part"><div className="sem-label">Checks saved on this model</div>
      {saved === null ? <div className="sem-text-meta">Reading the checks…</div> : saved.length === 0 ? <div className="sem-text-meta">None yet.</div>
        : <table className="qv-table qv-saved"><tbody>{saved.slice(0, 8).map((s) => <tr key={s.id}><td>{readableTitle(s.title) || friendlyHeader(s.kind)}</td><td className="sem-text-meta">{KIND_WORD[s.kind] ?? friendlyHeader(s.kind)}</td><td className="sem-text-meta qv-r">{s.createdWhen ? when(s.createdWhen) : ''}</td></tr>)}</tbody></table>}
    </div>
  </div>;
}
