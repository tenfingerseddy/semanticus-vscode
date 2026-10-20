import { useCallback, useEffect, useRef, useState } from 'react';
import { rpc } from './bridge';
import { upgradeToPro } from './pro';
import { usePersistedState } from './hooks';
import { AllowButton, useAskWhenAnswerNeedsPermission, useConsentCard } from './consent';
import { useDaxModelContext } from './daxeditor';
import { AssumptionLine, ChipMenu, Line, OtherReadings, announcement, stopLine, JEV_READ_THIS, type Menu } from './askdata';
import {
  ASK_METHOD, askableColumns, chipsOf, cleanParts, filterableColumns, outcomeOf, partsAfter, shownRowsOf, unreadLine, needsData,
  type ColumnInfo, type DataAnswer, type DataAsk,
} from './askdata-model';
import type { ResultSet } from './wire';

// ===================================================================================================
// The Queries question box (Kane, 26 and 27 Sep; contract H1 to H9 in the Queries pre-flight). PRO ONLY: the
// Queries tab stays free, and DaxLabView mounts <AskField> only when useFeature('askData') is 'granted'. On Free it
// draws one quiet line and nothing here is called. The hook below makes NO call on its own: asking is the only
// thing that reaches the engine, and the chip pickers load their columns on the first menu open.
//
// It reuses, never copies: the askData RPC (the shape runWords / runAsk send), the page's ChipMenu, its
// answer announcement and its check builder. No engine change, no new wire method.
//
// WHERE THE ANSWER GOES (the host contract):
//   H1  the DAX goes into the editor as the tool's own writing (onDax sets query AND generatedQuery);
//   H2  the rows go into the one result, taken from the answer, with no second run (onRows);
//   H3  one busy state and one Stop with everything else in Queries (host passes busy / setBusy);
//   H4  the words, the parts and the DAX the chips wrote are kept per model; rows never are;
//   H5  the person's own query is kept so "Put back my query" can restore it (host);
//   H6  while the chips own the query the only check offered is a number from this answer (host);
//   H7  the run key pressed in the question field asks and stops there (AskField's onKeyDown);
//   H9  a "built, no data" answer shows its note only; the tab's own connect line is the one prompt.
// ===================================================================================================

type Send = (method: string, ...params: unknown[]) => Promise<unknown>;
const meta = 'text-(length:--sem-text-meta)';
const muted = { color: 'var(--sem-muted)' };
// Queries is the place for depth, so it asks for up to 10,000 rows (the page and the card keep the default 500).
// Same method and shape as runWords / runAsk in askdata-model.ts, plus the cap those leave to each surface.
const QUERIES_MAX_ROWS = 10000;
const failed = (e: unknown): DataAnswer => ({ outcome: 'error', error: String((e as Error)?.message ?? e), checks: [], unsure: [] });
async function askOnce(send: Send, body: DataAsk, recordAs?: string | null): Promise<DataAnswer> {
  try { return (await send(ASK_METHOD, { ...body, maxRows: QUERIES_MAX_ROWS, ...(recordAs ? { recordAs } : {}) })) as DataAnswer; } catch (e) { return failed(e); }
}

export interface QueriesAsk {
  words: string; setWords: (w: string) => void;
  parts: DataAsk | null;
  answer: DataAnswer | null;
  chipDax: string;
  asking: boolean; stopped: boolean; announce: string;
  /** The id of the question the answer on the page belongs to (the notebook row), or null. */
  answerId: string | null;
  /** `recordAs` is a notebook row this ask opens again: the engine updates that row in place and adds nothing to the history. */
  askWords: (text?: string, recordAs?: string | null) => void; askParts: (p: DataAsk, recordAs?: string | null) => void; stop: () => void; clear: () => void;
  adopt: (h: { parts: DataAsk; answer: DataAnswer; words?: string }) => void;
}

/** The stand-in for a plan without the words box: it holds no words, asks nothing and sends nothing (Free never reaches the ask calls). */
export const NO_ASK: QueriesAsk = {
  words: '', setWords: () => undefined, parts: null, answer: null, answerId: null, chipDax: '', asking: false, stopped: false, announce: '',
  askWords: () => undefined, askParts: () => undefined, stop: () => undefined, clear: () => undefined, adopt: () => undefined,
};

export function useQueriesAsk({ persistKey, busyElsewhere, setBusy, onDax, onRows, heldRow }: {
  persistKey: string; busyElsewhere: boolean; setBusy: (b: 'ask' | null) => void;
  onDax: (dax: string) => void; onRows: (rs: ResultSet | null, dax: string) => void;
  /** The notebook row the answer on the page belongs to: a part changed on it (a chip) updates that row in place. */
  heldRow?: () => string | null;
}): QueriesAsk {
  const [words, setWords] = usePersistedState('lab.ask.words.' + persistKey, '');
  const [parts, setParts] = usePersistedState<DataAsk | null>('lab.ask.parts.' + persistKey, null);
  const [chipDax, setChipDax] = usePersistedState('lab.ask.dax.' + persistKey, '');
  const [answer, setAnswer] = useState<DataAnswer | null>(null);
  const [asking, setAsking] = useState(false);
  const [stopped, setStopped] = useState(false);
  const [announce, setAnnounce] = useState('');
  const [fromWords, setFromWords] = useState(false);
  const gen = useRef(0);
  const send: Send = useCallback((method, ...params) => rpc(method, ...params), []);

  const settle = (sent: DataAsk | null, a: DataAnswer) => {
    setAnswer(a);
    setParts(a.parts || sent ? partsAfter(sent ?? cleanParts(null), a) : null);
    setAnnounce(announcement(a));
    if (a.dax) {
      setChipDax(a.dax);
      onDax(a.dax);
      // Only an answered question carries rows. A built-only or refused one clears the old rows so the result
      // never shows another question's numbers under this question's chips.
      // A reading the engine's own check flagged shows no rows at all, so no figure: the line in AskLines says what it could not read.
      const rows = shownRowsOf(a);
      onRows(rows ? { ...rows, query: a.dax } : null, a.dax);
    } else {
      // A question stopped before any DAX was written has no query and no rows of its own. It must not inherit the last
      // question's: the new statement and chips over the old numbers said "Year is 2024" above 2025's rows (Sol 3.2).
      setChipDax('');
      onRows(null, '');
    }
  };
  const begin = (line: string) => {
    const mine = ++gen.current;
    setStopped(false); setAsking(true); setBusy('ask'); setAnnounce(line);
    return mine;
  };
  const end = (mine: number) => { if (mine !== gen.current) return false; setAsking(false); setBusy(null); return true; };

  const askWords = (text?: string, recordAs?: string | null) => {
    const q = (typeof text === 'string' ? text : words).trim();
    if (!q || asking || busyElsewhere) return;
    if (typeof text === 'string') setWords(q);
    setFromWords(true);
    const mine = begin('Jev is reading the question, then Semanticus builds, checks and runs the query.');
    void askOnce(send, { question: q }, recordAs).then((a) => { if (end(mine)) settle(a.readBy === 'jev' ? cleanParts(null) : parts, a); });
  };
  const askParts = (p: DataAsk, recordAs?: string | null) => {
    if (asking || busyElsewhere) return;
    setFromWords(false);
    const next = cleanParts(p);
    setParts(next);
    const mine = begin('Semanticus is building, checking and running the query.');
    // A part changed on the answer in front of the person changes that row; opening a folded row names its own.
    void askOnce(send, next, recordAs ?? heldRow?.() ?? null).then((a) => { if (end(mine)) settle(next, a); });
  };
  const stop = () => {
    gen.current++;
    void rpc('cancelDax').catch(() => { /* the ask still settles on the engine */ });
    setAsking(false); setBusy(null); setStopped(true); setAnnounce('The question was stopped.');
  };
  // Open in Queries from Ask your data: the page's answer lands exactly as an answer here would (H1, H2), with no ask.
  const adopt = (h: { parts: DataAsk; answer: DataAnswer; words?: string }) => {
    gen.current++; setAsking(false); setStopped(false); setFromWords(false);
    if (h.words != null) setWords(h.words);
    settle(h.parts, h.answer);
  };
  const clear = () => { gen.current++; setAnswer(null); setParts(null); setChipDax(''); setStopped(false); setAsking(false); setAnnounce(''); };
  useConsentCard(fromWords && !!answer && answer.readBy !== 'jev' && outcomeOf(answer) === 'needs-you', () => askWords());
  useAskWhenAnswerNeedsPermission(!fromWords && !!answer && answer.needsPermission === true);

  // A different model: nothing asked of the last one stays on screen (the persisted words and parts are per model).
  useEffect(() => { gen.current++; setAnswer(null); setAsking(false); setStopped(false); setAnnounce(''); }, [persistKey]);

  return { words, setWords, parts, answer, answerId: answer?.questionId ?? null, chipDax, asking, stopped, announce, askWords, askParts, stop, clear, adopt };
}

/** The field in the head row. Enter asks; the run key asks too and goes no further (H7). */
export function AskField({ ask, disabled, trailing }: { ask: QueriesAsk; disabled: boolean; trailing?: React.ReactNode }) {
  // The field is disabled while it asks, which drops focus to the page. Put it back when the answer lands, so a
  // keyboard user can ask again or Tab on to the chips.
  const input = useRef<HTMLInputElement>(null);
  const hadFocus = useRef(false);
  useEffect(() => { if (!ask.asking && hadFocus.current) { hadFocus.current = false; input.current?.focus(); } }, [ask.asking]);
  return (
    <form className="flex-1 min-w-0 flex items-center gap-2 sem-field" data-ask-field data-testid="queries-ask" aria-label="Ask about your data"
      style={{ paddingRight: 'var(--sem-space-1)' }}
      onSubmit={(e) => { e.preventDefault(); hadFocus.current = document.activeElement === input.current; ask.askWords(); }}>
      <span aria-hidden="true" style={muted}>›</span>
      <input ref={input} value={ask.words} onChange={(e) => ask.setWords(e.target.value)} disabled={disabled || ask.asking}
        onKeyDown={(e) => {
          if ((e.ctrlKey || e.metaKey) && e.key === 'Enter') { e.preventDefault(); e.stopPropagation(); hadFocus.current = true; ask.askWords(); }
        }}
        placeholder="Ask about your data, like net sales by store region for 2025" aria-label="Ask about your data"
        className="flex-1 min-w-0" style={{ background: 'transparent', border: 'none', outline: 'none', color: 'var(--sem-fg)', font: 'inherit' }} />
      <span className={meta} style={muted}>{ask.asking ? 'Jev is reading' : 'Enter to ask Jev'}</span>
      {trailing}
    </form>
  );
}

/** The answer's chips, in the fields row: "Jev read it as" plus the same chips as Ask your data. */
export function AskChips({ ask, owns, disabled, onRebuild, onLeave }: { ask: QueriesAsk; owns: boolean; disabled: boolean; onRebuild: () => void; onLeave?: () => void }) {
  const model = useDaxModelContext();
  const [menu, setMenu] = useState<Menu | null>(null);
  const [columns, setColumns] = useState<ColumnInfo[] | null>(null);
  const [calcGroups, setCalcGroups] = useState<string[]>([]);
  const parts = ask.parts;
  // Pickers load on the first menu open, so an answer nobody edits costs no extra calls.
  useEffect(() => {
    if (!menu || columns) return;
    rpc<ColumnInfo[]>('listColumns').then(setColumns).catch(() => setColumns([]));
    rpc<{ tables?: { name: string; isCalculationGroup?: boolean }[] }>('getModelObjects')
      .then((o) => setCalcGroups((o?.tables ?? []).filter((t) => t.isCalculationGroup).map((t) => t.name)))
      .catch(() => setCalcGroups([]));
  }, [menu, columns]);
  if (!parts || !chipsOf(parts).length) return null;
  // One phrase for how a row was read, whoever read it (hero 25 H6); the tag's colour and tooltip say who.
  const who = 'Read as';
  const locked = disabled || ask.asking || !owns;
  return (
    <div className="flex items-center flex-wrap gap-2 relative" role="group" aria-label="The question, as parts" data-testid="queries-ask-chips" data-owns={owns ? 'yes' : 'no'}
>
      <span className={`${meta} sem-doer ${ask.answer?.readBy === 'jev' ? 'sem-doer-agent' : 'sem-doer-you'}`} title={ask.answer?.readBy === 'jev' ? JEV_READ_THIS : undefined}>{who}</span>
      {chipsOf(parts).map((c) => (
        <button key={`${c.kind}-${c.index}`} type="button" className="sem-btn sem-btn-sm" disabled={locked} data-chip={c.kind} aria-label={c.name}
          aria-haspopup="menu" aria-expanded={menu?.chip?.kind === c.kind && menu.chip.index === c.index}
          onClick={() => setMenu(menu?.chip?.kind === c.kind && menu.chip.index === c.index ? null : { chip: c, view: 'root' })}>
          {c.label} <span aria-hidden="true">▾</span>
        </button>
      ))}
      <button type="button" className="sem-btn sem-btn-sm" disabled={locked} data-chip="add" aria-haspopup="menu" aria-label="Add to the question"
        onClick={() => setMenu(menu && !menu.chip ? null : { chip: null, view: 'add' })}>+ Add</button>
      {!owns && (
        <span className="inline-flex items-center gap-2" data-testid="queries-ask-aside">
          <span className={meta}>You changed the query by hand, so these parts no longer describe it.</span>
          <button type="button" className="sem-btn sem-btn-sm" disabled={disabled || ask.asking} onClick={onRebuild}>Build from the parts again</button>
        </span>
      )}
      {/* The way back to the person's own wells, kept as they were (verify-queries-redesign D8). */}
      {onLeave && <button type="button" className="sem-btn sem-btn-sm sem-btn-quiet" disabled={disabled || ask.asking} onClick={onLeave}
        title="Put the question away and go back to the fields you chose. The question's words stay in the box.">Back to my fields</button>}
      {menu && owns && <ChipMenu menu={menu} parts={parts} measures={model.measures.map((m) => m.name)} columns={askableColumns(model.columns, calcGroups)}
        filterColumns={filterableColumns(askableColumns(columns ?? [], calcGroups))} onMenu={setMenu} onClose={() => setMenu(null)}
        onAsk={(p) => { setMenu(null); ask.askParts(p); }} />}
    </div>
  );
}

/** The answer's own lines: passed checks, refusals, unsure lines, the note. Never a second connect prompt (H9). */
export function AskLines({ ask, current, quiet = false }: { ask: QueriesAsk; current: boolean; quiet?: boolean }) {
  // `quiet` (the Query page): its headline says the statement and its Checks part lists the passed checks, so neither is said twice here.
  const a = ask.answer;
  if (ask.asking) return <div className={meta} data-testid="queries-ask-reading">{ask.words.trim() && ask.announce.startsWith('Jev') ? 'Jev is reading the question, then Semanticus builds, checks and runs the query.' : 'Semanticus is building, checking and running the query.'}</div>;
  if (ask.stopped) return <div className={meta} style={muted} data-testid="queries-ask-stopped">The question was stopped. Nothing was kept from it. Ask again, or change a part.</div>;
  if (!a) return null;
  const o = outcomeOf(a);
  const passed = (a.checks ?? []).filter((c) => c.passed);
  const failed = (a.checks ?? []).filter((c) => !c.passed);
  const unsure = a.unsure ?? [];
  const note = a.note && !unsure.includes(a.note) ? a.note : null;
  return (
    <div className="flex flex-col gap-1" data-testid="queries-ask-lines" data-outcome={o ?? ''}>
      {/* What the answer shows, in one sentence, first: the same statement as Ask your data (the M11 join). */}
      {/* Only while it still describes what is on screen (`current`): a statement says what the answer's query shows, so once the
          person has changed the query by hand, or another result has replaced the rows, it is not about them (Sol 3.2). */}
      {a.statement && current && !quiet && <div data-testid="queries-ask-statement" style={{ color: 'var(--sem-fg)' }}>{a.statement}</div>}
      {current && !a.flagged && <AssumptionLine answer={a} onSwitch={ask.askParts} />}
      {unreadLine(a) && <ul className="m-0 p-0 sem-stack" style={{ listStyle: 'none' }} data-testid="queries-ask-unread"><Line tone="warn" text={unreadLine(a)!} /></ul>}
      {passed.length > 0 && !quiet && <div className="flex flex-wrap gap-2">{passed.map((c) => <span key={c.name + c.line} className={`sem-chip sem-chip-ok ${meta}`}>{c.line}</span>)}</div>}
      {failed.length > 0 && <ul className="m-0 p-0 sem-stack" style={{ listStyle: 'none' }} role="alert">{failed.map((c) => <Line key={c.name + c.line} tone="bad" text={c.line} />)}</ul>}
      {o === 'refused' && failed.length === 0 && a.error && <div role="alert"><Line tone="bad" text={a.error} as="div" /></div>}
      {unsure.length > 0 && <ul className="m-0 p-0 sem-stack" style={{ listStyle: 'none' }}>{unsure.map((u) => <Line key={u} tone="warn" text={stopLine(a, u)} />)}</ul>}
      {note && <div className={meta} style={muted}>{note}</div>}
      {a.needsPermission && <AllowButton onAllowed={() => ask.askWords()} />}
      {/* Two good readings (Kane, 28 Sep): the page's own line and switch, asking the other parts here. */}
      <OtherReadings answer={a} onSwitch={ask.askParts} />
      {needsData(a) && !note && <div className={meta} style={muted}>This query needs a model that holds data to run.</div>}
    </div>
  );
}

/** Free: one quiet line, no field, no call. The line promises no chips (they are Pro too). Pressing it opens the Pro page, like every other Pro press
 *  (UAT 17 Oct, F22: it did nothing). It sends nothing: the page it opens is the Pro page. */
export function AskFreeLine() {
  return (
    <button type="button" className="flex-1 min-w-0 flex items-center gap-2 sem-btn-quiet" style={{ background: 'transparent', border: 'none', padding: 0, textAlign: 'left', cursor: 'pointer', font: 'inherit', color: 'inherit' }}
      data-testid="queries-ask-pro-pill" onClick={() => upgradeToPro({ label: 'Ask in plain words', tab: 'query', run: () => undefined })}>
      <span className="sem-label">Pro</span>
      <span className={`${meta} truncate`} style={muted}>Ask in plain words</span>
    </button>
  );
}
