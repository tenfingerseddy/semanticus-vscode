import { useEffect, useMemo, useRef, useState } from 'react';
import { ConnectBar } from './connection';
import { QueryStalenessChip } from './contextbar';
import { ResultGrid } from './grid';
import { ExplainInline } from './explainpanel';
import { DaxVisual, VizSwitcher, type VizType } from './daxvisual';
import { axisDims } from './axisorder';
import { withSortBy } from './daxwells';
import { chartRulePick } from './chartpick.mjs';
import { useDaxModelContext } from './daxeditor';
import { learnExamples } from './studio/learn-state';
import { answerOf } from './studio/tour-model';
import { useSurfaceEscape } from './surface-escape';
import { plainDaxError } from './daxErrorText';
import { isSignInError, SIGN_IN_AGAIN } from './authcopy';
import { AllowButton } from './consent';
import {
  HISTORY_METHOD, addFilter, addGroup, addMeasure, askableColumns, checkDraftOf, chipsOf, cleanParts, columnName, columnRef, filterableColumns, filterKind, filterOps, opFor,
  crowdedChart, needsData, outcomeOf, partsAfter, questionLine, pivotConfigOf, removeFilter, removeGroup, removeMeasure, removeSort, removeTop, resultSetOf, rowLabel,
  emptyFinderLine, runAsk, runWords, setFilterValues, setSort, setTop, swapGroup, swapMeasure, whoAsked,
  type Chip, type CheckDraft, type ColumnInfo, type DataAnswer, type DataAsk, type DataQuestionHistory, type DataQuestionRecord, type QueriesHandoff,
} from './askdata-model';

// ===================================================================================================
// The parts of Ask your data that outlived the page (1.6, Kane 15 Oct: Ask your data and Queries became one page, Query, query.tsx).
// Kept here because the Query page and its question box still use them: the chip menu and its edits, the answer's lines (assumption,
// other readings, refusals), the Save as a check chooser, the answer body the Learn tour's recorded examples draw, and the history helpers.
// The page itself is gone. The engine's ask_data method, the history file and the chips are unchanged (askdata-model.ts).
// ===================================================================================================

export type Menu =
  | { chip: Chip; view: 'root' }
  | { chip: null; view: 'add' | 'add-measure' | 'add-group' | 'add-filter' | 'add-filter-words'; column?: string };

const meta = 'text-(length:--sem-text-meta)';
const muted = { color: 'var(--sem-muted)' };

/** A history row's name: the engine's reading, else the words asked, else the parts as asked. A question refused
 *  before its parts were settled has neither a reading nor words, so its parts name it (verify-askdata defect 1). */
/** A date in a history line stays on one line: the hyphens between its digits do not break ("Date before 2025-10-" / "06"). */
export function unbroken(text: string): string { return text.replace(/(\d)-(?=\d)/g, '$1\u2011'); }

/** The questions the list shows until the person opens the rest (K17). */
export const HISTORY_SHOWN = 8;

/** Repeat questions fold into one row: the same reading asked again shows once, as the newest ask, with how many times.
 *  The list arrives newest first, so the first of a kind is the one that stays. */
export function mergeRepeats(rows: DataQuestionRecord[]): { r: DataQuestionRecord; times: number }[] {
  const out: { r: DataQuestionRecord; times: number }[] = [];
  const at = new Map<string, number>();
  for (const r of rows) {
    // Only a named question merges: two refused asks with no reading are not the same question.
    const key = (r.reading || r.question || questionLine(r.parts) || r.id).trim().toLowerCase();
    const i = at.get(key);
    if (i === undefined) { at.set(key, out.length); out.push({ r, times: 1 }); } else out[i].times++;
  }
  return out;
}

export function rowName(r: DataQuestionRecord): string {
  return r.reading || r.question || questionLine(r.parts) || (r.outcome === 'refused' ? 'A question that was not run' : 'A question with no number yet');
}

/** The line over an answer Jev read from typed words. The chips above it are the reading. */
export const JEV_READ_THIS = 'Jev read this. Semanticus built and checked the query. Change any part to ask again.';

// One word for a question with no number: no data and a redirect both read "no number" (hero 25 H6). The row's own line says why.
export const OUTCOME_WORD: Record<string, string> = { built: 'built, not run', 'needs-you': 'no number', refused: 'no number', error: 'did not run' };

/** The one plain notice a failed run reads where the engine sent no failed check of its own. The engine's own
 *  words for it are LocalEngine.Query.cs QueryDidNotRunLine; this is the page's stand-in, not a second version. */
export const QUERY_DID_NOT_RUN = 'The query did not run on the model it was sent to.';

export function when(iso: string): string {
  const t = Date.parse(iso);
  if (!Number.isFinite(t)) return '';
  const d = new Date(t);
  const today = new Date();
  const time = d.toLocaleTimeString(undefined, { hour: '2-digit', minute: '2-digit' });
  return d.toDateString() === today.toDateString() ? time : `${d.toLocaleDateString(undefined, { day: 'numeric', month: 'short' })} ${time}`;
}

const rowCountWord = (n: number) => (n === 1 ? '1 row' : `${n.toLocaleString()} rows`);

export function announcement(a: DataAnswer): string {
  const o = outcomeOf(a);
  if (o === 'answered') return `Answered. ${a.rowCount === 1 ? '1 row' : `${a.rowCount ?? 0} rows`}${a.truncated ? ', stopped at the row limit' : ''}.`;
  if (o === 'refused') return `Not run. ${(a.checks ?? []).filter((c) => !c.passed).map((c) => c.line).join(' ') || a.error || ''}`;
  if (o === 'needs-you') return (a.unsure ?? []).join(' ') || a.note || 'Needs you.';
  if (o === 'built') return 'Built and checked, not run.';
  // The plain notice, not the server's raw text: this is read aloud, and a RootActivityId spoken at somebody is
  // not an announcement (verify-askdata-r5, r5-062).
  return (a.checks ?? []).filter((c) => !c.passed).map((c) => c.line).join(' ') || QUERY_DID_NOT_RUN;
}

/** Everything under the chips for one answer. Exported for the page test, which renders each outcome. */
export function AnswerBody({ answer, parts, connected, view, viz = 'bar', onViz, showDax, onView, onShowDax, showError = false, onShowError, onSignIn, signInBusy, actions, onSwitch, busy, onAllowed }: {
  answer: DataAnswer; parts: DataAsk; connected: boolean; view: 'table' | 'chart'; viz?: VizType; onViz?: (v: VizType) => void;
  showDax: boolean; onView: (v: 'table' | 'chart') => void; onShowDax: (v: boolean) => void;
  /** Whether the server's own words are open under the plain notice. A prop, like showDax, so one render can be
   *  read with them shut and one with them open. */
  showError?: boolean; onShowError?: (v: boolean) => void;
  onSignIn?: () => void; signInBusy?: boolean; actions?: React.ReactNode;
  onSwitch?: (p: DataAsk) => void; busy?: boolean;
  /** Asks the same question again once the Allow button has given permission. Without it the button is not drawn. */
  onAllowed?: () => void;
}) {
  const o = outcomeOf(answer);
  const passed = (answer.checks ?? []).filter((c) => c.passed);
  const rowsCheck = passed.find((c) => c.name === 'rows');
  const [showChecks, setShowChecks] = useState(false);
  const failed = (answer.checks ?? []).filter((c) => !c.passed);
  const unsure = answer.unsure ?? [];
  const note = answer.note && !unsure.includes(answer.note) ? answer.note : null;
  const rs = resultSetOf(answer);
  // The engine built this query and groups by the asked fields only, so it carries no sort-by column. A field whose model
  // sort-by is set (a fiscal month) is therefore read as a field with an order the result cannot show: it claims no time
  // order here, instead of passing for Jan..Dec by its name.
  const model = useDaxModelContext();
  const config = useMemo(() => withSortBy(pivotConfigOf(parts), model), [parts, model]);
  const hasRows = o === 'answered' && rs.rows.length > 0;
  // A question that asked WHY is answered with the explanation, not the number again (Kane, 8 Oct, hero journey 6).
  const why = o === 'answered' ? answer.why ?? null : null;
  // The chart kinds this answer may be drawn as are the ones Queries allows: the same rules, so a card is offered only for
  // one number in one row and two measures are never drawn as their sum (Sol 3.6).
  const rule = hasRows ? chartRulePick({
    dims: axisDims(rs, config),   // the same reading the chart below draws by, so "Dates run left to right" only over an order that was set
    measures: (parts.measures ?? []).length, rows: rs.rows.length,
  }) : null;
  const shownViz: VizType = rule && !rule.fits.includes(viz) ? rule.pick as VizType : viz;
  // A bar chart over hundreds of rows is drawn from the top few, with a line that says so; the table keeps every row (hero19 D15).
  const measureCol = Math.max(0, rs.columns.length - Math.max(1, (parts.measures ?? []).length));
  const crowded = hasRows && shownViz === 'bar' ? crowdedChart(rs.rows, measureCol, rs.columns[measureCol]?.name ?? 'the first number') : null;
  // "Jev read this" shows only over an answer that ran: a reading that was stopped, or only built, was checked and not shown.
  const who = answer.readBy === 'jev' && o === 'answered' ? JEV_READ_THIS : null;

  return (
    <div className="sem-stack-gap" data-testid="askdata-answer" data-outcome={o ?? ''}>
      {answer.statement && <div data-testid="askdata-statement" style={{ color: 'var(--sem-fg)' }}>{answer.statement}</div>}
      <AssumptionLine answer={answer} onSwitch={onSwitch} disabled={busy} />
      {(who || passed.length > 0) && (
        <div className="sem-stack" data-testid="askdata-checks">
          <div className={`${meta} sem-inline flex-wrap`} style={muted}>
            {who && <span>{who}</span>}
            {passed.length > 0 && (
              // The passed checks fold into this one line (their words are its tooltip and "Show checks"). A failed
              // check never folds: it prints in full below. A capped answer says so here, in the engine's own words.
              <span className="sem-num" title={passed.map((c) => c.line).join(' ')}>
                ✓ Checked{o === 'answered' ? ` · ${answer.truncated && rowsCheck ? rowsCheck.line : rowCountWord(answer.rowCount ?? rs.rows.length)}` : ''}
                {o === 'answered' && typeof answer.elapsedMs === 'number' ? ` · ${answer.elapsedMs} ms` : ''}
              </span>
            )}
            {passed.length > 0 && (
              <button type="button" className="sem-btn sem-btn-sm sem-btn-quiet" aria-expanded={showChecks} onClick={() => setShowChecks(!showChecks)}>
                {showChecks ? 'Hide checks' : 'Show checks'}
              </button>
            )}
          </div>
          {showChecks && (
            <div className={`${meta} sem-inline flex-wrap`} style={muted}>
              {passed.map((c) => <span key={c.name + c.line} className="sem-chip sem-chip-ok">{c.line}</span>)}
            </div>
          )}
        </div>
      )}
      {o === 'answered' && <QueryStalenessChip />}
      {failed.length > 0 && (
        <ul className="m-0 p-0 sem-stack" style={{ listStyle: 'none' }} role="alert" data-testid="askdata-refused">
          {failed.map((c) => <Line key={c.name + c.line} tone="bad" text={c.line} />)}
        </ul>
      )}
      {o === 'refused' && failed.length === 0 && answer.error && <div role="alert"><Line tone="bad" text={answer.error} as="div" /></div>}
      {unsure.length > 0 && (
        <ul className="m-0 p-0 sem-stack" style={{ listStyle: 'none' }} data-testid="askdata-unsure">
          {unsure.map((u) => <Line key={u} tone="warn" text={stopLine(answer, u)} />)}
        </ul>
      )}
      {note && <div className={meta} style={muted}>{note}</div>}
      {answer.needsPermission && onAllowed && <AllowButton onAllowed={onAllowed} />}
      {onSwitch && <OtherReadings answer={answer} onSwitch={onSwitch} disabled={busy} />}
      {o === 'error' && (
        <div className="sem-stack" data-testid="askdata-error">
          {/* ONE plain notice. The engine's own failed check says it where there is one, so this only stands in
              when there is not; the server's raw words go behind Show details and are never printed twice
              (verify-askdata-r5, r5-062). */}
          {failed.length === 0 && <div role="alert"><Line tone="bad" text={QUERY_DID_NOT_RUN} as="div" /></div>}
          {answer.error && <button type="button" className="sem-btn sem-btn-sm self-start" aria-expanded={showError}
            data-testid="askdata-error-details" onClick={() => onShowError?.(!showError)}>{showError ? 'Hide details' : 'Show details'}</button>}
          {showError && answer.error && (
            <div className="sem-inset sem-stack" data-testid="askdata-error-raw">
              <span className="sem-label">What the server said</span>
              {/* Wrapped, not scrolled: the end of the server's sentence and its RootActivityId sat behind a sideways
                  scroll (verify-askdata-r6 L6). */}
              <pre className={`m-0 whitespace-pre-wrap break-words ${meta}`}>{plainDaxError(answer.error)}</pre>
            </div>
          )}
          {isSignInError(answer.error) && onSignIn && <button type="button" className="sem-btn sem-btn-sm sem-btn-primary self-start" disabled={signInBusy} onClick={onSignIn}>{SIGN_IN_AGAIN}</button>}
        </div>
      )}
      {needsData(answer) && !connected && <ConnectBar hint="Connect a model that holds data to run this." />}

      {(hasRows || answer.dax) && (
        <div className="sem-inline flex-wrap">
          {hasRows && !why && (
            <div className="sem-seg" role="group" aria-label="Show as">
              <button type="button" className="sem-seg-item" aria-pressed={view === 'table'} onClick={() => onView('table')}>Table</button>
              <button type="button" className="sem-seg-item" aria-pressed={view === 'chart'} onClick={() => onView('chart')}>Chart</button>
            </div>
          )}
          {hasRows && !why && view === 'chart' && onViz && <VizSwitcher viz={shownViz} onViz={onViz} fits={rule?.fits as VizType[] | undefined} />}
          <span className="sem-push sem-inline">
            {answer.dax && <button type="button" className="sem-btn sem-btn-sm" aria-expanded={showDax} onClick={() => onShowDax(!showDax)}>{showDax ? 'Hide DAX' : 'Show DAX'}</button>}
            {actions}
          </span>
        </div>
      )}
      {why && (
        <div className="sem-inset" data-testid="askdata-why">
          <ExplainInline d={why} measureName={answer.columns?.[answer.columns.length - 1]?.name ?? why.name ?? ''} format={answer.formats?.[answer.formats.length - 1] ?? null} />
        </div>
      )}
      {hasRows && !why && (view === 'chart'
        ? <>
            {crowded && <div className={meta} style={muted} data-testid="askdata-chart-top">{crowded.line}</div>}
            <div data-testid="askdata-chart" style={{ height: 'calc(var(--sem-row) * 10)' }}><DaxVisual res={crowded ? { ...rs, rows: crowded.rows } : rs} config={config} viz={shownViz} height={320} /></div>
          </>
        : <ResultGrid columns={rs.columns.map((c, i) => ({ ...c, format: answer.formats?.[i] ?? undefined }))} rows={rs.rows} height={Math.min(360, 72 + rs.rows.length * 26)} truncated={rs.truncated}
            filterable={rs.rows.length > 20} framed={false} />)}
      {showDax && answer.dax && (
        <div className="sem-inset sem-stack" data-testid="askdata-dax">
          <span className="sem-label">DAX, written by Semanticus</span>
          <pre className={`m-0 overflow-x-auto sem-num ${meta}`}>{answer.dax}</pre>
        </div>
      )}
    </div>
  );
}

/** The Learn tour's recorded answers (1.4, lane tour). The demo model holds no data to ask, so the tour shows answers recorded on
 *  the same model, tagged Example. It never calls the engine. `example` is the answer the tour step names, if any. */
export function ExampleAnswers({ example }: { example: number | null }) {
  const [picked, setPicked] = useState<number | null>(example);
  const [view, setView] = useState<'table' | 'chart'>('table');
  const [showDax, setShowDax] = useState(false);
  useEffect(() => { if (example !== null) setPicked(example); }, [example]);
  const examples = learnExamples();
  const shown = picked !== null && examples[picked] ? answerOf(examples[picked]) : null;
  const [viz, setViz] = useState<VizType | null>(null);
  return (
    <div className="st-example" data-testid="query-examples">
      <span className={meta} style={muted}>Questions this model can answer:</span>
      <div className="sem-inline flex-wrap">
        {examples.map((a, i) => (
          <button key={a.question} type="button" className="sem-btn sem-btn-sm" aria-pressed={picked === i} data-testid="query-example-pick"
            onClick={() => { setPicked(i); setViz(null); }}>{a.question}</button>
        ))}
      </div>
      {shown && (
        <>
          <div className="sem-inline">
            <span className="st-example-tag" data-testid="query-example">Example</span>
            <p className="st-example-line">Example answer. Live answers need your own model.</p>
          </div>
          <AnswerBody answer={shown.answer as DataAnswer} parts={shown.parts as DataAsk} connected view={view} viz={viz ?? shown.viz} onViz={setViz}
            showDax={showDax} onView={setView} onShowDax={setShowDax} />
        </>
      )}
    </div>
  );
}

/** The other good reading, when Jev was torn between two (Kane, 28 Sep): the engine's line ("Showing Net Sales. You
 *  may have meant Units.") and one button that asks the other reading's parts. Those parts are read by "you", so the
 *  switch never calls Jev. Shared with the Queries question box, which renders it under its own answer lines. */
export function OtherReadings({ answer, onSwitch, disabled }: { answer: DataAnswer; onSwitch: (p: DataAsk) => void; disabled?: boolean }) {
  const others = answer.others ?? [];
  // An answer that says what it assumed carries its other readings on that one line (AssumptionLine), as buttons only.
  if (!others.length || answer.assumption) return null;
  return (
    <ul className="m-0 p-0 sem-stack" style={{ listStyle: 'none' }} data-testid="askdata-others" aria-label="Another reading">
      {others.map((x) => (
        <li key={x.line} className={`sem-inline flex-wrap ${meta}`} style={{ alignItems: 'baseline' }}>
          {!x.anyway && <span style={{ color: 'var(--sem-fg)' }}>{x.line}</span>}
          <button type="button" className="sem-btn sem-btn-sm" disabled={disabled} onClick={() => onSwitch(x.parts)}
            title={x.anyway ? `${x.line} ${x.reading ?? ''}`.trim() : x.reading ?? undefined}>{x.anyway ? 'Show it anyway' : `Show ${x.instead} instead`}</button>
        </li>
      ))}
    </ul>
  );
}

/** What the answer assumed where the words left room, in the engine's one plain line (K16, Kane 8 Oct: answer first, say the
 *  assumption, let the person change it), with each other reading one button on the same line. The buttons ask that reading's
 *  parts, so a switch never calls Jev. Shared with the Queries question box. */
export function AssumptionLine({ answer, onSwitch, disabled }: { answer: DataAnswer; onSwitch?: (p: DataAsk) => void; disabled?: boolean }) {
  if (!answer.assumption) return null;
  const others = onSwitch ? (answer.others ?? []).filter((x) => !x.anyway) : [];
  return (
    <div className={`sem-inline flex-wrap ${meta}`} style={{ alignItems: 'baseline' }} data-testid="askdata-assumption">
      <span style={{ color: 'var(--sem-fg)' }}>{answer.assumption}</span>
      {others.map((x) => (
        <button key={x.line + x.instead} type="button" className="sem-btn sem-btn-sm" disabled={disabled} onClick={() => onSwitch?.(x.parts)}
          title={`${x.line} ${x.reading ?? ''}`.trim()}>{`Show ${x.instead} instead`}</button>
      ))}
    </div>
  );
}

/** A stop line under the statement the answer already said first: the engine starts it with that statement, so it is said once. */
export function stopLine(answer: DataAnswer, u: string): string {
  return answer.statement && u.startsWith(answer.statement) ? u.slice(answer.statement.length).trim() || u : u;
}

/** One line from the engine with a dot in its tone. It wraps: a refusal or a note is a sentence, not a chip. */
export function Line({ tone, text, as = 'li' }: { tone: 'bad' | 'warn'; text: string; as?: 'li' | 'div' }) {
  const Tag = as;
  return (
    <Tag className={`sem-inline ${meta}`} style={{ alignItems: 'baseline' }}>
      <span className={`sem-chip sem-chip-${tone}`} aria-hidden="true" />
      <span style={{ color: 'var(--sem-fg)' }}>{text}</span>
    </Tag>
  );
}

export function SaveChooser({ answer, value, onChange, onCancel, onContinue, ready }: {
  answer: DataAnswer; value: { row: number; measure: number }; onChange: (v: { row: number; measure: number }) => void;
  onCancel: () => void; onContinue: () => void; ready: boolean;
}) {
  const rows = (answer.rows ?? []).slice(0, 200);
  const measures = answer.parts?.measures ?? [];
  useSurfaceEscape(onCancel);
  return (
    <div className="sem-inset sem-stack" data-testid="askdata-save" role="group" aria-label="Save as a check">
      <span className={meta}>A check keeps one number. Pick the number and the row it should stay at. It runs with your other checks in Validation.</span>
      <div className="sem-inline flex-wrap">
        {measures.length > 1 && (
          <label className={`sem-inline ${meta}`}>Number
            <select className="sem-field" value={value.measure} onChange={(e) => onChange({ ...value, measure: Number(e.target.value) })}>
              {measures.map((m, i) => <option key={m} value={i}>{m}</option>)}
            </select>
          </label>
        )}
        <label className={`sem-inline ${meta}`}>Row
          <select className="sem-field" value={value.row} onChange={(e) => onChange({ ...value, row: Number(e.target.value) })}>
            {rows.map((_, i) => <option key={i} value={i}>{rowLabel(answer, i)}</option>)}
          </select>
        </label>
        <button type="button" className="sem-btn sem-btn-sm sem-btn-primary" disabled={!ready} onClick={onContinue}>Continue</button>
        <button type="button" className="sem-btn sem-btn-sm" onClick={onCancel}>Cancel</button>
      </div>
      {!ready && <span className={meta} style={muted}>This row cannot be kept as a check yet: a blank, or a split by a date, cannot be pinned to one row.</span>}
    </div>
  );
}

export function ChipMenu({ menu, parts, measures, columns, filterColumns, onMenu, onClose, onAsk }: {
  menu: Menu; parts: DataAsk; measures: string[]; columns: { table: string; name: string }[]; filterColumns: ColumnInfo[];
  onMenu: (m: Menu) => void; onClose: () => void; onAsk: (p: DataAsk) => void;
}) {
  const ref = useRef<HTMLDivElement>(null);
  const [find, setFind] = useState('');
  const [text, setText] = useState(() => (menu.chip?.kind === 'filter' ? (parts.filters ?? [])[menu.chip.index]?.values.join(', ') ?? '' : ''));
  const kindOf = (col: string) => filterKind(filterColumns.find((x) => columnRef(x.table, x.name) === col)?.dataType);
  // A date chip opens on a range word ("is" is not one a column of days takes), and a text column never keeps one.
  const firstOp = (col: string | undefined, current: string) => (col ? opFor(kindOf(col), current) : current);
  const [op, setOp] = useState(() => (menu.chip?.kind === 'filter'
    ? firstOp((parts.filters ?? [])[menu.chip.index]?.column, (parts.filters ?? [])[menu.chip.index]?.op ?? 'is') : 'is'));
  const wordsFor = menu.view === 'add-filter-words' ? menu.column : undefined;
  useEffect(() => { if (wordsFor) setOp((o) => firstOp(wordsFor, o)); }, [wordsFor]);
  useSurfaceEscape(() => {
    const back = document.querySelector<HTMLElement>(menu.chip ? `[data-chip="${menu.chip.kind}"][aria-expanded="true"]` : '[data-chip="add"]');
    onClose();
    back?.focus();
  });
  useEffect(() => { setFind(''); ref.current?.querySelector<HTMLElement>('input, button')?.focus(); }, [menu.view, menu.chip?.kind, menu.chip?.index]);
  const item = (label: string, act: () => void, key?: string, current?: boolean) => (
    <button key={key ?? label} type="button" role="menuitem" className={`sem-btn sem-btn-sm${current ? ' sem-btn-primary' : ''}`} style={{ justifyContent: 'flex-start', textAlign: 'left' }} onClick={act}>{label}</button>
  );
  const f = find.trim().toLowerCase();
  const matchCols = (list: { table: string; name: string }[]) => list.filter((c) => !f || `${c.table} ${c.name}`.toLowerCase().includes(f)).slice(0, 60);
  // Enter takes the top match, the same as pressing it (UAT 18 Oct). Only a match counts: a Sort or Remove line under the list is not one.
  const finder = (label: string) => <input className={`sem-field ${meta}`} value={find} onChange={(e) => setFind(e.target.value)} placeholder={label} aria-label={label}
    onKeyDown={(e) => { if (e.key === 'Enter') { e.preventDefault(); ref.current?.querySelector<HTMLElement>('[data-finder-list] > button:not([disabled])')?.click(); } }} />;
  const colItems = (list: { table: string; name: string }[], act: (ref: string) => void, current?: string) =>
    matchCols(list).map((c) => { const r = columnRef(c.table, c.name); return item(`${c.name} (${c.table})`, () => act(r), r, r === current); });
  const measureItems = (act: (m: string) => void, current?: string) =>
    measures.filter((m) => !f || m.toLowerCase().includes(f)).slice(0, 60).map((m) => item(m, () => act(m), m, m === current));
  // A finder that matched nothing used to show nothing, so a typo and a column the model lacks looked the same
  // (verify-askdata-r2 R6). The list says so instead, in the words the person typed.
  const found = (items: React.ReactNode[], what: 'number' | 'column') => items.length
    ? <div className="sem-stack" data-finder-list>{items}</div>
    : <span className={meta} style={muted} role="status" data-testid="askdata-finder-empty">{emptyFinderLine(what, find)}</span>;

  let body: React.ReactNode = null;
  const c = menu.chip;
  if (c?.kind === 'measure') {
    const current = (parts.measures ?? [])[c.index];
    body = <>
      <span className="sem-label">Swap {current} for</span>{finder('Find a number')}
      {found(measureItems((m) => onAsk(swapMeasure(parts, c.index, m)), current), 'number')}
      {item(`Sort by ${current}, largest first`, () => onAsk(setSort(parts, current, true)))}
      {(parts.measures ?? []).length > 1 ? item(`Remove ${current}`, () => onAsk(removeMeasure(parts, c.index))) : <span className={meta} style={muted}>A question needs at least one number.</span>}
    </>;
  } else if (c?.kind === 'group') {
    const current = (parts.groupBy ?? [])[c.index];
    body = <>
      <span className="sem-label">Swap {columnName(current)} for</span>{finder('Find a column')}
      {found(colItems(columns, (r) => onAsk(swapGroup(parts, c.index, r)), current), 'column')}
      {item(`Sort by ${columnName(current)}, A to Z`, () => onAsk(setSort(parts, current, false)))}
      {item(`Remove ${columnName(current)}`, () => onAsk(removeGroup(parts, c.index)))}
    </>;
  } else if (c?.kind === 'filter') {
    const fl = (parts.filters ?? [])[c.index];
    body = <>
      <span className="sem-label">Show only where {columnName(fl.column)}</span>
      <FilterWords op={op} onOp={setOp} kind={kindOf(fl.column)} text={text} onText={setText} onApply={() => onAsk(setFilterValues(parts, c.index, text, op))} />
      {item('Remove this filter', () => onAsk(removeFilter(parts, c.index)))}
    </>;
  } else if (c?.kind === 'top') {
    body = <>
      <span className="sem-label">How many</span>
      <div className="sem-inline flex-wrap">{[3, 5, 10, 20, 50].map((n) => item(String(n), () => onAsk(setTop(parts, n, parts.topBy ?? undefined, !!parts.lowest)), 'n' + n, parts.top === n))}</div>
      <span className="sem-label">Ranked by</span>
      <div className="sem-stack">{(parts.measures ?? []).map((m) => item(m, () => onAsk(setTop(parts, parts.top ?? 10, m, !!parts.lowest)), 'by' + m, parts.topBy === m))}</div>
      {item(parts.lowest ? 'Show the highest instead' : 'Show the lowest instead', () => onAsk(setTop(parts, parts.top ?? 10, parts.topBy ?? undefined, !parts.lowest)))}
      {item('Remove the top, show every row', () => onAsk(removeTop(parts)))}
    </>;
  } else if (c?.kind === 'sort') {
    body = <>
      {item(parts.descending ? 'Smallest first' : 'Largest first', () => onAsk(setSort(parts, parts.sortBy!, !parts.descending)))}
      {item('Remove the order', () => onAsk(removeSort(parts)))}
    </>;
  } else if (menu.view === 'add') {
    body = <>
      <span className="sem-label">Add to this question</span>
      {item('A number', () => onMenu({ chip: null, view: 'add-measure' }))}
      {item('A column to split by', () => onMenu({ chip: null, view: 'add-group' }))}
      {item('A filter', () => onMenu({ chip: null, view: 'add-filter' }))}
      {!parts.top && item(`Top 10 by ${(parts.measures ?? [])[0]}`, () => onAsk(setTop(parts, 10)))}
    </>;
  } else if (menu.view === 'add-measure') {
    body = <><span className="sem-label">Choose a number</span>{finder('Find a number')}
      {found(measureItems((m) => onAsk(addMeasure(parts, m))), 'number')}</>;
  } else if (menu.view === 'add-group') {
    body = <><span className="sem-label">Split by</span>{finder('Find a column')}
      {found(colItems(columns.filter((x) => !(parts.groupBy ?? []).includes(columnRef(x.table, x.name))), (r) => onAsk(addGroup(parts, r))), 'column')}</>;
  } else if (menu.view === 'add-filter') {
    body = <><span className="sem-label">Filter on</span>{finder('Find a column')}
      <span className={meta} style={muted}>A date keeps a range: after, from, before or up to one day. Yes or no columns cannot be filtered yet.</span>
      {found(colItems(filterColumns, (r) => onMenu({ chip: null, view: 'add-filter-words', column: r })), 'column')}</>;
  } else if (menu.view === 'add-filter-words' && menu.column) {
    const col = menu.column;
    body = <><span className="sem-label">Show only where {columnName(col)}</span>
      <FilterWords op={op} onOp={setOp} kind={kindOf(col)} text={text} onText={setText} onApply={() => onAsk(addFilter(parts, col, text, op))} />
      {item('Back', () => onMenu({ chip: null, view: 'add-filter' }))}</>;
  }
  return (
    <div ref={ref} role="menu" aria-label="Change the question" data-testid="askdata-menu"
      className="sem-card sem-stack absolute z-30 overflow-auto" style={{ top: '100%', left: 0, marginTop: 'var(--sem-space-1)', width: 'calc(var(--sem-row) * 10)', maxHeight: 'calc(var(--sem-row) * 12)' }}>
      {body}
    </div>
  );
}

function FilterWords({ op, onOp, kind, text, onText, onApply }: {
  op: string; onOp: (v: string) => void; kind: 'text' | 'number' | 'date'; text: string; onText: (v: string) => void; onApply: () => void;
}) {
  const ops = filterOps(kind);
  const date = kind === 'date';
  return (
    <form className="sem-stack" onSubmit={(e) => { e.preventDefault(); if (text.trim()) onApply(); }}>
      <select className={`sem-field ${meta}`} value={op} onChange={(e) => onOp(e.target.value)} aria-label="How it filters">
        {ops.map(([v, l]) => <option key={v} value={v}>{l}</option>)}
      </select>
      {date
        ? <input type="date" className={`sem-field ${meta}`} value={text} onChange={(e) => onText(e.target.value)} aria-label="The day the range starts or ends on" />
        : <input className={`sem-field ${meta}`} value={text} onChange={(e) => onText(e.target.value)} placeholder="Values, separated by commas" aria-label="Values to keep" />}
      <span className="text-(length:--sem-text-label)" style={muted}>{date
        ? 'One day. From keeps that day and later; before keeps the days before it.'
        : "The words are matched against the column's own values. A word with no match is refused, never guessed."}</span>
      <button type="submit" className="sem-btn sem-btn-sm sem-btn-primary self-start" disabled={!text.trim()}>Apply</button>
    </form>
  );
}
