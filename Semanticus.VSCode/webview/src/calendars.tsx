import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { rpc, onDidChange } from './bridge';
import { MenuPop } from './menupop';
import './calendars.css';

// ===================================================================================================
// Calendars (Edit, Advanced modelling). The page opens on a finished plan, not a form: Semanticus has read the Date table, what each
// column holds, the fiscal start in the model's instructions and the measures written the old way (propose_calendar). One press does
// it all as one undo step (setup_calendars). After that the page offers the time measures a calendar makes possible
// (add_time_measures). The assistant calls the same three ops. Mirrors Semanticus.Engine/CalendarPlanTypes.cs (camelCase over the wire).
// Calendars are a Power BI preview feature, and the page says so.
// ===================================================================================================

interface Level { unit: string; level: string; column: string; isNew: boolean; alsoColumn?: string | null; sortBy?: string | null }
interface Ladder { kind: string; name: string; hint?: string | null; levels: Level[]; other: string[]; exists: boolean }
interface Choice { kind: string; name: string; short: string; hint: string; on: boolean; locked: boolean; exists: boolean }
interface Move { measure: string; table: string; ref: string; before: string; after: string; kind: string; calendar: string }
interface Stay { measure: string; why: string }
interface TimeMeasure { name: string; table: string; dax: string; kind: string; calendar: string; base: string }
interface Proposal {
  state: 'ready' | 'no-date-table' | 'set-up' | 'live' | 'unavailable'; note?: string | null; compatibilityLevel: number; willRaiseLevel: boolean;
  dateTable?: string | null; dateColumn?: string | null; otherDateTables: string[]; dateSource?: string | null;
  choices: Choice[]; calendars: Ladder[]; newColumns: string[]; fiscalStartMonth: number; fiscalClue?: string | null;
  moves: Move[]; stays: Stay[]; timeMeasures: TimeMeasure[];
}
interface NumberRow { measure: string; before?: string | null; after?: string | null; same: boolean }
interface NumberCheck { status: 'same' | 'differ' | 'not-checked'; reason?: string | null; slice?: string | null; rows: NumberRow[] }
interface SetupResult {
  revision: number; dateTable: string; createdTable: boolean; fromLevel: number; toLevel: number; calendars: string[]; columnsAdded: string[];
  moved: Move[]; stays: Stay[]; numberCheck?: NumberCheck | null; note?: string | null;
}
interface AddResult { revision: number; created: { ref: string; name: string; expression: string }[]; skipped: string[]; note?: string | null }
interface LevelChoice { calendar: string; unit: string; column: string | null }
interface ColumnRow { name: string; table: string; dataType?: string }

const MONTHS = ['January', 'February', 'March', 'April', 'May', 'June', 'July', 'August', 'September', 'October', 'November', 'December'];
const errText = (e: unknown) => String((e as Error)?.message ?? e);

const tick = <svg width="12" height="12" viewBox="0 0 12 12" aria-hidden="true"><path d="M2.5 6.2 5 8.6 9.6 3.6" fill="none" stroke="currentColor" strokeWidth="1.6" strokeLinecap="round" strokeLinejoin="round" /></svg>;
const plus = <svg width="12" height="12" viewBox="0 0 12 12" aria-hidden="true"><path d="M6 2.5v7M2.5 6h7" fill="none" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" /></svg>;
const caret = <svg width="10" height="10" viewBox="0 0 10 10" aria-hidden="true"><path d="M2.5 3.8 5 6.3l2.5-2.5" fill="none" stroke="currentColor" strokeWidth="1.4" strokeLinecap="round" strokeLinejoin="round" /></svg>;

const PREVIEW_TIP = 'Calendars are a preview feature in Power BI. Microsoft may still change how they work.';
const Preview = () => <span className="cal-preview" title={PREVIEW_TIP} data-testid="cal-preview">Preview</span>;
const plural = (n: number, one: string, many: string) => `${n} ${n === 1 ? one : many}`;

/** What the chosen calendars let people do, in plain words. Built from the plan, so it never promises a calendar that is not in it. */
export function unlocks(choices: Choice[], hasMeasures: boolean): { title: string; detail: string }[] {
  const on = (k: string) => choices.some((c) => c.kind === k && c.on);
  const out: { title: string; detail: string }[] = [
    { title: 'Totals that follow the calendar', detail: 'Year to date, last year and the rest read the calendar levels, not a date column.' },
  ];
  if (on('fiscal')) out.push({ title: 'Your fiscal year in every visual', detail: 'Year, quarter and year to date on your own year.' });
  if (on('retail') || on('iso') || on('p13')) out.push({ title: 'Week to date, and the same week last year', detail: 'New week measures that only work on a calendar.' });
  if (on('retail')) out.push({ title: 'Last year by retail period', detail: 'Compare period 6 with period 6, not June with June.' });
  if (hasMeasures) out.push({ title: 'The old measures keep working', detail: 'They move to the calendar when their numbers are shown to be the same.' });
  return out;
}

export function CalendarsPanel() {
  const [p, setP] = useState<Proposal | null>(null);
  const [err, setErr] = useState<string | null>(null);
  const [kinds, setKinds] = useState<string[] | null>(null);
  const [fiscal, setFiscal] = useState(0);
  const [levels, setLevels] = useState<LevelChoice[]>([]);
  const [busy, setBusy] = useState<null | 'setup' | 'add' | 'undo'>(null);
  const [more, setMore] = useState(false);
  const [result, setResult] = useState<SetupResult | null>(null);
  const [added, setAdded] = useState<number | null>(null);
  const [cols, setCols] = useState<ColumnRow[]>([]);
  const args = useRef({ kinds, fiscal, levels });
  args.current = { kinds, fiscal, levels };
  const timer = useRef<number | undefined>(undefined);
  const seq = useRef(0);

  const load = useCallback(async () => {
    const n = ++seq.current;
    try {
      const a = args.current;
      const next = await rpc<Proposal>('proposeCalendar', null, a.kinds, a.fiscal, a.levels.length ? a.levels : null);
      if (n !== seq.current) return;
      setP(next); setErr(null);
    } catch (e) {
      if (n !== seq.current) return;
      // A level choice the engine refuses is dropped, so the page never sticks on a plan it cannot show.
      if (args.current.levels.length) { setLevels([]); } else { setErr(errText(e)); }
    }
  }, []);

  useEffect(() => { void load(); }, [kinds, fiscal, levels, load]);
  useEffect(() => {
    const off = onDidChange(() => { window.clearTimeout(timer.current); timer.current = window.setTimeout(() => void load(), 350); });
    return () => { off(); window.clearTimeout(timer.current); };
  }, [load]);
  useEffect(() => {
    let live = true;
    rpc<ColumnRow[]>('listColumns').then((c) => { if (live) setCols(c); }).catch(() => { /* the pickers just have no list */ });
    return () => { live = false; };
  }, [p?.dateTable, p?.state]);

  const dateCols = useMemo(() => cols.filter((c) => c.table === p?.dateTable), [cols, p?.dateTable]);

  if (!p) return <div className="cal-wide"><div className="sem-card"><p className="cal-line cal-faint" role={err ? 'alert' : undefined}>{err ?? 'Reading the model…'}</p></div></div>;
  if (p.state === 'live') return <LiveCard note={p.note} />;
  if (p.state === 'unavailable') return <StopCard title="Calendars need a date column" note={p.note} />;

  const done = p.state === 'set-up';
  const none = p.state === 'no-date-table';
  const planned = p.calendars.filter((c) => !c.exists);
  const existing = p.calendars.filter((c) => c.exists);

  const toggleKind = (k: string) => {
    const on = p.choices.filter((c) => c.on).map((c) => c.kind);
    setKinds(on.includes(k) ? on.filter((x) => x !== k) : [...on, k]);
  };
  const setLevel = (calendar: string, unit: string, column: string | null | undefined) => {
    const rest = levels.filter((l) => !(l.calendar === calendar && l.unit === unit));
    setLevels(column === undefined ? rest : [...rest, { calendar, unit, column }]);
  };

  const setup = async (only?: string) => {
    setBusy('setup'); setErr(null); setAdded(null);
    try {
      const a = args.current;
      const r = await rpc<SetupResult>('setupCalendars', null, only ? [only] : a.kinds, a.fiscal, a.levels.length ? a.levels : null, !only, 'human');
      setResult(only ? result : r); setKinds(null); setLevels([]); setMore(false);
      await load();
    } catch (e) { setErr(errText(e)); } finally { setBusy(null); }
  };
  const undo = async (what: 'setup' | 'measures') => {
    setBusy('undo'); setErr(null);
    try {
      await rpc('undo', 'human');
      if (what === 'setup') { setResult(null); setKinds(null); setLevels([]); }
      setAdded(null);
      await load();
    } catch (e) { setErr(errText(e)); } finally { setBusy(null); }
  };

  const nCol = p.newColumns.length;
  const moves = p.moves;

  return (
    <>
      <div className="cal-main" data-testid="cal-main">
        <div className="sem-card cal-hero">
          <div className="cal-head">
            <div className="cal-title">
              <h2 className="sem-page-title">{done ? (result?.createdTable ? 'Your Date table and calendars are ready' : 'Your calendars are set up') : none ? 'Add a Date table with calendars' : 'Turn the Date table into calendars'}</h2>
              <Preview />
            </div>
            {!done && <p className="cal-line cal-faint">{none
              ? `This model has no Date table. Semanticus will make one from ${p.dateSource ?? 'the order dates'}, then set up the calendars on it.`
              : 'Semanticus read the Date table and your measures. This is its plan.'}</p>}
            {!done && p.otherDateTables.length > 0 && <p className="cal-line cal-faint" data-testid="cal-other">It chose {p.dateTable}. This model also has {p.otherDateTables.join(', ')}. Tell your assistant if you want another.</p>}
          </div>
          {err && <p className="cal-line cal-bad" role="alert" data-testid="cal-err">{err}</p>}
          {done ? <DoneLine result={result} busy={busy} onUndo={() => void undo('setup')} /> : <Choices p={p} onToggle={toggleKind} onMonth={setFiscal} locked={busy !== null} />}
          <div className="cal-ladders" data-testid="cal-ladders">
            {existing.map((c) => <LadderView key={'e' + c.name} ladder={c} set />)}
            {planned.map((c) => <LadderView key={c.kind} ladder={c} cols={dateCols} onLevel={(unit, col) => setLevel(c.kind, unit, col)} chosen={levels} />)}
          </div>
          {done && p.choices.some((c) => !c.exists) && <AddCalendar choices={p.choices.filter((c) => !c.exists)} busy={busy !== null} onAdd={(k) => void setup(k)} />}
          {!done && <div className="cal-row">
            <button type="button" className="sem-btn sem-btn-primary" onClick={() => void setup()} disabled={busy !== null} data-testid="cal-apply">{busy === 'setup' ? 'Setting up…' : 'Set up calendars'}</button>
            <button type="button" className="sem-btn" aria-expanded={more} onClick={() => setMore(!more)} data-testid="cal-more">{more ? 'Hide the changes' : 'See every change'}</button>
            <span className="cal-faint cal-push">You can undo it.</span>
          </div>}
          {done && result && <div className="cal-row"><button type="button" className="sem-btn" aria-expanded={more} onClick={() => setMore(!more)} data-testid="cal-more">{more ? 'Hide the changes' : 'See every change'}</button></div>}
        </div>
        {more && <div className="sem-card"><ChangeList p={p} result={done ? result : null} /></div>}
      </div>
      <div className="cal-side">
        {done
          ? <TimeMeasures p={p} added={added} busy={busy !== null} onAdded={(n) => { setAdded(n); void load(); }} onBusy={(b) => setBusy(b ? 'add' : null)} onError={setErr} onUndo={() => void undo('measures')} />
          : <>
            <div className="sem-card cal-stack"><h3 className="sem-card-title">What you get</h3>
              <ul className="cal-unlocks">{unlocks(p.choices, moves.length > 0).map((u) => <li key={u.title} title={u.detail}><span className="cal-dot" /><span>{u.title}</span></li>)}</ul></div>
            <div className="sem-card cal-stack"><h3 className="sem-card-title">What changes</h3>
              <ul className="cal-summary" data-testid="cal-summary">
                {none && <li className="cal-num">A new {p.dateTable} table</li>}
                <li className="cal-num">{plural(planned.length, 'calendar', 'calendars')}</li>
                <li className="cal-num">{plural(nCol, 'new column', 'new columns')}{none ? '' : ` on ${p.dateTable}`}</li>
                <li className="cal-num">{moves.length === 0 ? 'No measures to move' : plural(moves.length, 'measure can move', 'measures can move')+' if the numbers match'}</li>
                {p.stays.length > 0 && <li className="cal-num">{plural(p.stays.length, 'measure stays', 'measures stay')} as {p.stays.length === 1 ? 'it is' : 'they are'}</li>}
                {p.willRaiseLevel && <li className="cal-num">Model version {p.compatibilityLevel} to 1701</li>}
              </ul></div>
          </>}
      </div>
    </>
  );
}

// ---- the calendar choices and the fiscal start --------------------------------------------------------------------

function Choices({ p, onToggle, onMonth, locked }: { p: Proposal; onToggle: (k: string) => void; onMonth: (m: number) => void; locked: boolean }) {
  const fiscalOn = p.choices.some((c) => c.kind === 'fiscal' && c.on);
  return (
    <div className="cal-choices">
      <span className="cal-label">Your years</span>
      <div className="cal-row" role="group" aria-label="Your years">
        {p.choices.map((c) => <button key={c.kind} type="button" className="cal-choice" aria-pressed={c.on} disabled={c.locked || locked}
          title={c.locked ? 'The measures you have now use the calendar year' : c.hint} onClick={() => onToggle(c.kind)} data-testid={`cal-kind-${c.kind}`}>
          {c.on ? tick : plus}{c.short}</button>)}
      </div>
      {fiscalOn && <p className="cal-line cal-faint" data-testid="cal-clue">{p.fiscalClue ? p.fiscalClue + ' ' : ''}<FiscalMonth month={p.fiscalStartMonth} onPick={onMonth} /></p>}
    </div>
  );
}

function FiscalMonth({ month, onPick }: { month: number; onPick: (m: number) => void }) {
  const [open, setOpen] = useState(false);
  return (
    <span className="cal-rel">
      <button type="button" className="cal-link" aria-expanded={open} onClick={() => setOpen(!open)} data-testid="cal-fiscal-month">Starts in {MONTHS[month - 1] ?? 'July'} {caret}</button>
      {open && <MenuPop items={MONTHS.map((m, i) => ({ label: m, act: () => onPick(i + 1) }))} onClose={() => setOpen(false)} />}
    </span>
  );
}

function AddCalendar({ choices, busy, onAdd }: { choices: Choice[]; busy: boolean; onAdd: (kind: string) => void }) {
  return (
    <div className="cal-choices" data-testid="cal-add-calendar">
      <span className="cal-label">Add a calendar</span>
      <div className="cal-row">{choices.map((c) => <button key={c.kind} type="button" className="cal-choice" disabled={busy} onClick={() => onAdd(c.kind)} data-testid={`cal-add-${c.kind}`}>{plus}{c.short}</button>)}</div>
    </div>
  );
}

// ---- one calendar as a ladder ------------------------------------------------------------------------------------------

function LadderView({ ladder, set = false, cols = [], onLevel, chosen = [] }: {
  ladder: Ladder; set?: boolean; cols?: ColumnRow[]; onLevel?: (unit: string, column: string | null | undefined) => void; chosen?: LevelChoice[];
}) {
  return (
    <div className={`cal-ladder ${set ? 'cal-ladder-set' : ''}`} data-testid={`cal-ladder-${ladder.kind}`}>
      <div className="cal-ladder-head"><span className="cal-ladder-name">{ladder.name}</span>{ladder.hint && <span className="cal-faint">{ladder.hint}</span>}</div>
      <ol className="cal-rungs">
        {ladder.levels.map((l) => (
          <li key={l.unit} className={`cal-rung ${l.isNew ? 'cal-rung-new' : ''}`} title={`${l.level}: ${l.column}${l.sortBy ? `, sorted by ${l.sortBy}` : ''}`}>
            <span className="cal-node" aria-hidden="true" />
            <span className="cal-level">{l.level}</span>
            {set || !onLevel ? <span className="cal-col cal-col-set"><span className="cal-col-name">{l.column}</span></span>
              : <RungColumn level={l} cols={cols} picked={chosen.some((c) => c.calendar === ladder.kind && c.unit === l.unit)} calendar={ladder.name} onPick={(c) => onLevel(l.unit, c)} />}
          </li>
        ))}
      </ol>
      {ladder.other.length > 0 && <div className="cal-other cal-faint">Also on this calendar: {ladder.other.join(', ')}</div>}
    </div>
  );
}

/** One level's column, changeable in place: press it to use another column of the Date table, or to leave the level out. */
function RungColumn({ level, cols, picked, calendar, onPick }: { level: Level; cols: ColumnRow[]; picked: boolean; calendar: string; onPick: (column: string | null | undefined) => void }) {
  const [open, setOpen] = useState(false);
  const isDate = level.unit === 'Date';
  const choices = cols.filter((c) => (/date|time/i.test(c.dataType ?? '') ? isDate : !isDate) && c.name !== level.column);
  const items = [
    ...(picked ? [{ label: 'Use the one Semanticus chose', act: () => onPick(undefined) }] : []),
    ...choices.map((c) => ({ label: c.name, act: () => onPick(c.name) })),
    ...(isDate ? [] : [{ label: 'Leave this level out', act: () => onPick(null) }]),
  ];
  return (
    <span className="cal-rel">
      <button type="button" className={`cal-col ${level.isNew ? 'cal-col-new' : ''}`} aria-expanded={open} onClick={() => setOpen(!open)}
        title={`Change the column for ${level.level.toLowerCase()} in ${calendar}`} data-testid={`cal-rung-${calendar}-${level.level}`}>
        <span className="cal-col-name">{level.column}</span>{level.isNew && <span className="cal-new">new</span>}
      </button>
      {open && items.length > 0 && <MenuPop items={items} onClose={() => setOpen(false)} />}
    </span>
  );
}

// ---- after the step --------------------------------------------------------------------------------------------------------

function numberLine(r: SetupResult | null): { text: string; bad: boolean } | null {
  const c = r?.numberCheck;
  if (!r || !c || r.moved.length === 0) return null;
  if (c.status === 'same') return { text: `Same numbers as before${c.slice ? ' on ' + c.slice : ''}.`, bad: false };
  if (c.status === 'differ') {
    const n = c.rows.filter((x) => !x.same).length;
    return { text: `${plural(n, 'measure gave', 'measures gave')} different numbers. Undo puts them back.`, bad: true };
  }
  return { text: c.reason ?? 'The numbers were not compared.', bad: false };
}

function DoneLine({ result, busy, onUndo }: { result: SetupResult | null; busy: string | null; onUndo: () => void }) {
  const line = numberLine(result);
  return (
    <div className={`cal-done ${line?.bad ? 'cal-done-bad' : ''}`} data-testid="cal-done">
      <span className="cal-ok-big">{tick}</span>
      <span>{result
        ? <>{plural(result.calendars.length, 'calendar', 'calendars')} set up{result.moved.length > 0 ? `. ${plural(result.moved.length, 'measure', 'measures')} moved.` : '.'}{line ? ' ' + line.text : ''}</>
        : 'The calendars are on the model.'}</span>
      {result && <button type="button" className="sem-btn sem-btn-sm cal-push" disabled={busy !== null} onClick={onUndo} data-testid="cal-undo">Undo</button>}
    </div>
  );
}

function TimeMeasures({ p, added, busy, onAdded, onBusy, onError, onUndo }: {
  p: Proposal; added: number | null; busy: boolean; onAdded: (n: number) => void; onBusy: (b: boolean) => void; onError: (e: string) => void; onUndo: () => void;
}) {
  const can = p.timeMeasures;
  const [off, setOff] = useState<string[]>([]);
  const on = can.filter((m) => !off.includes(m.name));
  if (added != null && can.length === 0) {
    return <div className="sem-card cal-stack"><h3 className="sem-card-title">Add time measures</h3>
      <div className="cal-line" data-testid="cal-added"><span className="cal-good">{tick}</span> {plural(added, 'measure', 'measures')} added. <button type="button" className="cal-link" onClick={onUndo}>Undo</button></div></div>;
  }
  if (can.length === 0) return null;
  const go = async () => {
    onBusy(true);
    try {
      const r = await rpc<AddResult>('addTimeMeasures', on.map((m) => m.name), 'human');
      onAdded(r.created.length);
    } catch (e) { onError(errText(e)); } finally { onBusy(false); }
  };
  return (
    <div className="sem-card cal-stack" data-testid="cal-addm">
      <h3 className="sem-card-title">Add time measures</h3>
      {added != null && <div className="cal-line" data-testid="cal-added"><span className="cal-good">{tick}</span> {plural(added, 'measure', 'measures')} added. <button type="button" className="cal-link" onClick={onUndo}>Undo</button></div>}
      {can.map((m) => <label key={m.name} className="cal-check">
        <input type="checkbox" checked={!off.includes(m.name)} onChange={() => setOff(off.includes(m.name) ? off.filter((x) => x !== m.name) : [...off, m.name])} />
        <span>{m.name}</span><span className="cal-faint cal-dax cal-check-dax" title={m.dax}>{m.dax}</span></label>)}
      <div className="cal-row"><button type="button" className="sem-btn" disabled={busy || on.length === 0} onClick={() => void go()} data-testid="cal-addm-go">Add {plural(on.length, 'measure', 'measures')}</button></div>
    </div>
  );
}

// ---- every change ---------------------------------------------------------------------------------------------------------

function ChangeList({ p, result }: { p: Proposal; result: SetupResult | null }) {
  const cols = result ? result.columnsAdded : p.newColumns;
  const moved = result ? result.moved : p.moves;
  const stays = result ? result.stays : p.stays;
  const rows = result?.numberCheck?.rows ?? [];
  const raised = result ? result.fromLevel < result.toLevel : p.willRaiseLevel;
  const from = result ? result.fromLevel : p.compatibilityLevel;
  return (
    <div className="cal-changes" data-testid="cal-changes">
      {cols.length > 0 && <div className="cal-changes-sec">
        <div className="cal-label">{result ? 'New columns' : p.state === 'no-date-table' ? 'The new Date table' : `New columns on ${p.dateTable}`}</div>
        <div className="cal-wrap">{cols.map((c) => <span key={c} className="cal-tag">{c}</span>)}</div>
      </div>}
      {(moved.length > 0 || stays.length > 0) && <div className="cal-changes-sec">
        <div className="cal-label">Measures</div>
        <table className="cal-table"><thead><tr><th>Measure</th><th>Now</th><th>After</th>{rows.length > 0 && <th className="cal-r">Numbers</th>}</tr></thead>
          <tbody>
            {moved.map((m) => { const r = rows.find((x) => x.measure === m.measure); return (
              <tr key={m.ref}><td>{m.measure}</td><td className="cal-dax cal-faint">{m.before}</td><td className="cal-dax">{m.after}</td>
                {rows.length > 0 && <td className="cal-r cal-num">{r ? (r.same ? <span className="cal-good">{tick} Same</span> : <span className="cal-bad">{r.before ?? r.after}</span>) : ''}</td>}</tr>); })}
            {stays.map((x) => <tr key={x.measure}><td>{x.measure}</td><td colSpan={rows.length > 0 ? 3 : 2} className="cal-faint">{x.why}</td></tr>)}
          </tbody></table>
      </div>}
      {raised && <div className="cal-changes-sec">
        <div className="cal-label">Model version</div>
        <p className="cal-line">The model goes from version {from} to 1701, the first that holds calendars. Undo puts it back. Once you publish, it stays at 1701.</p>
      </div>}
    </div>
  );
}

// ---- the states calendars cannot work in ---------------------------------------------------------------------------------

function LiveCard({ note }: { note?: string | null }) {
  return (
    <div className="sem-card cal-hero cal-wide-card" data-testid="cal-live">
      <div className="cal-title"><h2 className="sem-page-title">Calendars go on the model this one reads from</h2><Preview /></div>
      <p className="cal-line cal-faint">{note ?? 'This model reads another Power BI model live, and Power BI does not allow calendars on a live connection. Set them up on the model it reads from.'}</p>
    </div>
  );
}

function StopCard({ title, note }: { title: string; note?: string | null }) {
  return (
    <div className="sem-card cal-hero" data-testid="cal-stop">
      <div className="cal-title"><h2 className="sem-page-title">{title}</h2><Preview /></div>
      <p className="cal-line cal-faint">{note}</p>
    </div>
  );
}
