import { useEffect, useRef, useState, type CSSProperties, type ReactNode } from 'react';
import { dateText } from '../../src/checkedNumberFormat';
import { useMarksHost, type DescriptionDrafts, type MarksHost } from './marks-host';
import {
  ADVICE_OFF, acceptStates, applies, cardActionable, cardVerdict, checkedAt, contextName, descriptionLine, descriptionScore, detailSentence, ms, orderMarks, placeValue, placeValues, provenanceWords,
  SCORE_TITLE, shownMarks, timingLine, verdictOf, type CardVerdict, type CheckCache, type Mark, type MarksResult, type ScoreMatch, type State,
} from './marks-model';

type Proposal = { id: string | null; state: string; message: string; text?: string };
type DescriptionView = { ref: string; name: string; description: string; oneLine: string; provenance: string; match: ScoreMatch; proposal: Proposal | null };
const hostDrafts = new WeakMap<MarksHost, DescriptionDrafts>();
const errorText = (error: unknown, fallback: string) => error instanceof Error && error.message ? error.message : fallback;
// A raw engine timestamp (it has a time) reads as a date in words. A bare date is one the engine already wrote in the measure's
// own format ("yyyy-mm-dd" shows 2016-09-12, rc28 tester gap 8), so it is left as written.
const cellText = (value?: string | null) => (value ?? '').replace(
  /\b\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}(?::\d{2})?(?:\.\d+)?(?:Z|[+-]\d{2}:?\d{2})?\b/g,
  stamp => dateText(stamp) ?? stamp);

/** The glyph of a state. The word or label always sits beside it, so colour is never alone. */
export function MarkGlyph({ glyph }: { glyph: string }) { return <>{glyph}</>; }

/** The Description as one line with its score. A click opens the full Description. */
export function DescriptionLine({ text, match, onOpen }: { text: string; match?: ScoreMatch | null; onOpen?: () => void }) {
  const score = descriptionScore(match);
  return <button type="button" className="mk-desc-line" data-testid="mk-desc-line" title="Description" aria-label="Description" onClick={onOpen}>
    <span className="mk-desc-text mk-one" data-testid="mk-desc-text">{descriptionLine(text)}</span>
    {score.band && <span className="mk-score" data-testid="mk-score" title={SCORE_TITLE}><span className={`mk-dot mk-dot-${score.band}`} aria-hidden="true" />{score.label}</span>}
  </button>;
}

/** The Description: one line at rest, the full text with its source, Edit, and Draft or Propose behind a click. One Save is one undoable change. */
export function DescriptionBlock({ reference, onMatch, command }: { reference: string; onMatch?: (match: ScoreMatch | null) => void; command?: { id: number; kind: 'open' | 'ask' } }) {
  const host = useMarksHost();
  // A detail can unmount without ending its edit. Keep the words and the value they were typed over together.
  const held: DescriptionDrafts = host.descriptionDrafts ?? hostDrafts.get(host) ?? new Map();
  if (!host.descriptionDrafts) hostDrafts.set(host, held);
  const [view, setView] = useState<DescriptionView | null>(null);
  const [draft, setDraft] = useState<string | null>(() => held.get(reference)?.text ?? null);
  const [base, setBase] = useState(() => held.get(reference)?.base ?? '');
  const [proposal, setProposal] = useState<Proposal | null>(null);
  const [note, setNote] = useState('');
  const [busy, setBusy] = useState(false);
  const [full, setFull] = useState(false);
  const at = useRef(0);
  const savedText = useRef<string | null>(null);
  const holdDraft = (text: string | null, original = base) => {
    if (text === null) held.delete(reference);
    else held.set(reference, { text, base: original });
    setDraft(text);
  };
  const read = () => {
    const mine = ++at.current;
    return host.rpc<DescriptionView>('getMeasureDescription', reference).then(v => {
      if (mine !== at.current) return;
      setView(v); setProposal(v.proposal ?? null); onMatch?.(v.match ?? null);
      if (v.proposal && !['waiting', 'ready', 'kept'].includes(v.proposal.state))
        setNote(v.proposal.message);
      if (savedText.current !== null && v.description !== savedText.current)
        setNote(n => n === 'Kept. Undo takes it back.' || n === 'Saved. Undo takes it back.' ? '' : n);
    }).catch(error => { if (mine === at.current) setNote(errorText(error, 'The Description could not be read.')); });
  };
  useEffect(() => {
    const previous = held.get(reference);
    at.current++; savedText.current = null; setView(null); setDraft(previous?.text ?? null); setBase(previous?.base ?? ''); setProposal(null); setNote(''); setBusy(false); setFull(false);
    void read();
    const off = host.onChange(() => { void read(); });
    return () => { off(); at.current++; };
  }, [reference, held]);
  useEffect(() => {
    if (proposal?.state !== 'waiting') return;
    const timer = setTimeout(() => { void read(); }, 600);
    return () => clearTimeout(timer);
  }, [proposal]);
  // The text the person just saved or kept, on the card until the engine's read of it returns. The old score is not for it.
  const shown = (text: string, provenance: string) => setView(v => v && { ...v, description: text, oneLine: text.split('. ')[0], provenance,
    match: { score: null, level: 'not scored yet', label: 'not scored yet' } });
  const save = async () => {
    if (draft == null) return;
    setBusy(true); setNote('Saving the Description.');
    try {
      // Someone else (your assistant, or an undo) may have changed it while you typed. Their words are not written over.
      const now = await host.rpc<DescriptionView>('getMeasureDescription', reference);
      if ((now?.description ?? '') !== base) {
        setView(now); setBase(now?.description ?? '');
        holdDraft(draft, now?.description ?? '');
        setNote('The Description changed while you typed. Your words are kept. Press Save again to replace the new one.');
        return;
      }
      await host.rpc('setDescription', reference, draft, 'human', base);
      savedText.current = draft;
      holdDraft(null); shown(draft, 'person'); setNote('Saved. Undo takes it back.'); await read();
    } catch (error) { setNote(errorText(error, 'The Description was not saved. Your words are kept.')); }
    finally { setBusy(false); }
  };
  const ask = async () => {
    const has = !!view?.description?.trim();
    setBusy(true); setNote('Asking your assistant.');
    try {
      const p = await host.rpc<Proposal>(has ? 'proposeMeasureDescription' : 'draftMeasureDescription', reference);
      setProposal(p); setNote(p.state === 'waiting' ? '' : p.message);
    } catch (error) { setNote(errorText(error, 'The Description could not be requested.')); }
    finally { setBusy(false); }
  };
  const keep = async () => {
    if (!proposal?.id) return;
    setBusy(true); setNote('Keeping the new Description.');
    try {
      await host.rpc('keepMeasureDescription', proposal.id);
      // The card shows the kept words at once: the read below can wait seconds while the new text is scored (rc28 tester gap 9).
      const kept = proposal.text ?? '';
      savedText.current = kept; holdDraft(null); setProposal(null); shown(kept, 'ai'); setNote('Kept. Undo takes it back.'); await read();
    }
    catch (error) { setNote(errorText(error, 'The Description could not be kept.')); }
    finally { setBusy(false); }
  };
  // The card asks for the full Description (its verdict line, or "Propose a better Description" on a mark).
  const seen = useRef(0);
  useEffect(() => {
    if (!command || command.id === seen.current) return;
    seen.current = command.id; setFull(true);
    if (command.kind === 'ask' && view) void ask();
  }, [command?.id, view === null]);
  const text = view?.description ?? '';
  const score = descriptionScore(view?.match);
  const source = [provenanceWords(view?.provenance), score.words].filter(Boolean).join('. ');
  return <section className="mk-desc" data-testid="mk-description" aria-label="Description">
    {draft != null
      ? <div className="mk-edit">
        <textarea aria-label="Description" data-testid="mk-desc-input" rows={Math.min(8, Math.max(3, Math.ceil(draft.length / 40)))} value={draft}
          onChange={e => holdDraft(e.target.value)} autoFocus />
        <div className="mk-acts">
          <button type="button" className="sem-btn sem-btn-sm sem-btn-primary" disabled={busy || draft.trim() === text.trim()} onClick={() => void save()}>Save</button>
          <button type="button" className="sem-btn sem-btn-sm" disabled={busy} onClick={() => { holdDraft(null); setNote('Nothing was changed.'); }}>Cancel</button>
        </div>
      </div>
      : !view ? null
        : !text.trim() ? <div className="mk-acts"><span className="mk-faint">No Description yet.</span>
          <button type="button" className="sem-btn sem-btn-sm" disabled={busy || proposal?.state === 'waiting' || proposal?.state === 'ready'} onClick={() => void ask()}>Draft a Description</button></div>
          : !full ? <DescriptionLine text={text} match={view.match} onOpen={() => setFull(true)} />
            : <div className="mk-desc-full">
              <p className="mk-desc-text" data-testid="mk-desc-text">{text}</p>
              {source && <p className="mk-faint mk-source" data-testid="mk-provenance">{source}.</p>}
              <div className="mk-acts">
                <button type="button" className="sem-btn sem-btn-sm" disabled={busy} onClick={() => { holdDraft(text, text); setBase(text); setNote(''); }}>Edit</button>
                <button type="button" className="sem-btn sem-btn-sm" disabled={busy || proposal?.state === 'waiting' || proposal?.state === 'ready'} onClick={() => void ask()}>Propose a better one</button>
                <button type="button" className="mk-link" onClick={() => setFull(false)}>Show one line</button>
              </div>
            </div>}
    {proposal?.state === 'ready' && proposal.text && <div className="mk-proposal" data-testid="mk-proposal">
      <span className="mk-label">Suggested by your assistant</span><p className="mk-desc-text">{proposal.text}</p>
      <div className="mk-acts">
        <button type="button" className="sem-btn sem-btn-sm sem-btn-primary" disabled={busy} onClick={() => void keep()}>Keep this one</button>
        <button type="button" className="sem-btn sem-btn-sm" disabled={busy} onClick={() => { setProposal(null); setNote('Discarded. The Description is the same.'); }}>Discard</button>
      </div>
    </div>}
    {(proposal?.state === 'waiting' || note) && <p className="mk-note" role="status" data-testid="mk-desc-note">{proposal?.state === 'waiting' ? proposal.message : note}</p>}
  </section>;
}

/** The face of a measure's card at rest: the verdict line, the Description line, the marks that apply, the footer. It takes its data as props, so a test can count its words. */
export function CardFace({ verdict, actionable, onTurnOn, description, marks, open, onMark, onVerdict, detail, checked, running, onCheck, add, notes }: {
  verdict: CardVerdict; actionable?: string | null; onTurnOn?: () => void; description: ReactNode; marks: { name: string; state: State; checking?: boolean }[]; open: string | null;
  onMark: (name: string) => void; onVerdict: () => void; detail?: ReactNode; checked: string; running?: boolean; onCheck: () => void; add?: ReactNode; notes?: ReactNode;
}) {
  const rest = verdict.state ? verdict.line.slice(verdict.glyph.length + 1) : verdict.line;
  // An empty line: the marks are still loading. It keeps its height, so nothing moves when they arrive.
  const reading = !verdict.line;
  return <>
    <button type="button" className={`mk-verdict mk-v-${verdict.state ?? 'none'}`} data-testid="mk-verdict" disabled={!verdict.worst} onClick={onVerdict}
      role={running ? 'status' : undefined} aria-busy={reading || undefined}>
      {verdict.glyph && <span className="mk-vglyph" aria-hidden="true">{verdict.glyph}</span>}
      <span className="mk-vtext">{verdict.state ? <><b>{verdict.word}</b>{rest.slice(verdict.word.length)}</> : rest || '\u00a0'}</span>
    </button>
    {actionable && <p className="mk-faint mk-actionable" data-testid="mk-actionable">{actionable}
      {actionable === ADVICE_OFF && onTurnOn && <> <button type="button" className="mk-link" data-testid="mk-turn-on" onClick={onTurnOn}>Turn on</button></>}</p>}
    {description}
    {marks.length > 0 && <div className="mk-marks" data-testid="mk-list" role="list">{marks.map(({ name, state, checking }) =>
      <button key={name} type="button" role="listitem" className={`mk-mark mk-s-${state}${open === name ? ' mk-open' : ''}${checking ? ' mk-checking' : ''}`} aria-expanded={open === name}
        aria-busy={checking || undefined} title={checking ? 'Checking' : undefined}
        data-testid={`mk-row-${name}`} onClick={() => onMark(name)}><span className="mk-glyph" aria-hidden="true">{glyphOf(state)}</span><span className="mk-name">{name}</span></button>)}</div>}
    {detail}
    {notes}
    <div className="mk-foot">
      {checked && <span className="mk-faint" data-testid="mk-last">{checked}</span>}
      <button type="button" className={verdict.state || running || reading ? 'mk-link' : 'sem-btn sem-btn-sm sem-btn-primary'} disabled={running || reading} onClick={onCheck}>Check now</button>
      {add}
    </div>
  </>;
}
const glyphOf = (state: State) => ({ broken: '✗', review: '▲', unknown: '○', overridden: '⊘', verified: '✓' })[state];

/** What a card shows from the engine's check state. The newest version wins: the state record (read, pushed) or the answer of
 *  the read that starts missing checks. Data with no version (an older engine) prefers the state record. A mark the run
 *  has not reached keeps its last result with a checking cue. */
export function cardState(state: CheckCache | undefined, started: MarksResult | undefined): { marks: Mark[]; running: boolean; lastChecked?: string | null; version: number } {
  const recordFirst = !!state?.marks?.length && (state.version ?? -1) >= (started?.version ?? -1);
  const marks = (recordFirst ? state?.marks : started?.marks ?? state?.marks) ?? [];
  const running = (recordFirst ? state?.running ?? started?.running : started?.running ?? state?.running) ?? false;
  return { marks, running, lastChecked: (recordFirst ? state?.lastChecked : started?.lastChecked) ?? state?.lastChecked ?? started?.lastChecked,
    version: Math.max(state?.version ?? -1, started?.version ?? -1) };
}

/** Marks and Description for one measure: Properties shows this, and the Checks page opens it as a measure's card. It renders
 *  only the engine's check state; every answer is kept per measure, so an answer for another measure never shows here. */
export function MeasureMarksPanel({ reference, name, now, requestedMark }: {
  reference: string; name?: string; now?: Date; requestedMark?: { name: string; id: number };
}) {
  const host = useMarksHost();
  const [states, setStates] = useState<Map<string, CheckCache>>(() => new Map());
  const [started, setStarted] = useState<Map<string, MarksResult>>(() => new Map());
  const [match, setMatch] = useState<ScoreMatch | null>(null);
  const [open, setOpen] = useState<string | null>(null);
  const [command, setCommand] = useState<{ id: number; kind: 'open' | 'ask' }>({ id: 0, kind: 'open' });
  const [note, setNote] = useState('');
  const [error, setError] = useState('');
  const panel = useRef<HTMLDivElement>(null);
  // What stops scoring now: the Checks page hands it over; Properties reads the page itself (wr1 F1).
  const [scoring, setScoring] = useState<string | null>(null);
  const readScoring = () => { if (host.unscored === undefined)
    void host.rpc<{ unscored?: string | null }>('getChecksPage').then(p => setScoring(p?.unscored ?? null)).catch(() => { /* the marks' own lines still show */ }); };
  const accept = (incoming: unknown) => setStates(held => acceptStates(held, incoming) ?? held);
  const start = (refresh = false, includePerformance = false) =>
    host.rpc<MarksResult>('getMeasureMarks', reference, null, refresh, 'human', includePerformance).then(r => {
      if (!r || !Array.isArray(r.marks)) return;
      const ref = r.measureRef || reference;
      setStarted(held => {
        const old = held.get(ref);
        return old?.version != null && r.version != null && r.version < old.version ? held : new Map(held).set(ref, r);
      });
      setError('');
    }).catch(e => setError(errorText(e, 'The marks could not be read.')));
  useEffect(() => {
    setMatch(null); setOpen(null); setNote(''); setError('');
    void start();
    void host.rpc<CheckCache[]>('getMeasureCheckCache').then(accept).catch(() => { /* the card shows what the start answered */ });
    // The engine pushes every change of a check state. A host without the push reads again on a model change.
    const off = host.onCheckState ? host.onCheckState(accept) : host.onChange(() => { void start(); });
    readScoring();
    const offScoring = host.onChange(readScoring);
    return () => { off(); offScoring(); };
  }, [reference]);
  useEffect(() => {
    if (!requestedMark) return;
    if (requestedMark.name === 'Description') setCommand(c => ({ id: c.id + 1, kind: 'open' }));
    else setOpen(requestedMark.name);
  }, [reference, requestedMark?.id]);
  const view = cardState(states.get(reference), started.get(reference));
  const loaded = states.has(reference) || started.has(reference);
  useEffect(() => {
    if (!requestedMark || !loaded) return;
    const target = requestedMark.name === 'Description' ? 'mk-description' : `mk-card-${requestedMark.name}`;
    panel.current?.querySelector(`[data-testid="${target}"]`)?.scrollIntoView({ block: 'nearest' });
  }, [reference, requestedMark?.id, loaded]);
  const marks = orderMarks(view.marks);
  const checked = !!view.lastChecked;
  const shown = shownMarks(marks, checked);
  // Until a first read answers, nothing is known about this measure: the card says it is reading (spec 4.4).
  const reading = !loaded && !error;
  const verdict = cardVerdict(marks, match, { running: view.running, checked, reading });
  const latest = view.lastChecked ?? marks.map(m => m.lastRan).filter((x): x is string => !!x).sort().pop();
  const timing = marks.find(m => m.name === 'Performance' && m.progress)?.progress;
  const actionable = cardActionable(host.unscored !== undefined ? host.unscored : scoring, marks, match);
  // The verdict line says "Checking now" while the run holds the measure; a note would say it twice and outlive the run.
  const recheck = (includePerformance = false) => { setNote(''); void start(true, includePerformance); };
  // Keep is the engine's: it holds by the inputs the edit was compared on, for every window and the assistant.
  const keep = () => { void host.rpc<CheckCache>('keepMeasureEdit', reference).then(state => { accept([state]); setOpen(null); setNote(''); })
    .catch(e => setNote(errorText(e, 'Keep did not work.'))); };
  const openMark = (markName: string) => setOpen(open === markName ? null : markName);
  const limitMs = started.get(reference)?.performanceSettings?.slowThresholdMs;
  const mark = open ? shown.find(x => x.mark.name === open) : undefined;
  return <div ref={panel} className="mk-panel" data-testid="marks-panel" aria-label={name ? `${name} marks` : 'Marks'}>
    <CardFace verdict={verdict} actionable={actionable} onTurnOn={() => host.open('advice', undefined, () => { readScoring(); recheck(); })} marks={shown.map(x => ({ name: x.mark.name, state: x.state, checking: view.running && (x.mark.pending ?? true) }))} open={open}
      running={view.running} checked={checkedAt(latest, now)}
      description={<DescriptionBlock reference={reference} onMatch={setMatch} command={command} />}
      onMark={openMark}
      onVerdict={() => verdict.worst === 'Description' ? setCommand(c => ({ id: c.id + 1, kind: 'open' })) : verdict.worst && setOpen(verdict.worst)}
      onCheck={() => recheck()}
      detail={mark && <MarkCard reference={reference} mark={mark.mark} state={mark.state} others={marks} limitMs={limitMs}
        onRecheck={recheck} onKeep={keep} onPropose={() => setCommand(c => ({ id: c.id + 1, kind: 'ask' }))} />}
      notes={<>{timing && <p className="mk-note" role="status" data-testid="mk-timing">{timingLine(timing)}</p>}
        {error && <p className="mk-note mk-bad" role="alert">{error}</p>}
        {note && !error && <p className="mk-note" role="status" data-testid="mk-note">{note}</p>}</>}
      add={<AddCheck reference={reference} marks={marks} running={view.running} onRecheck={recheck} />} />
  </div>;
}

/** One mark opened: one sentence, up to six numbers, where it looked behind one more click, and one action. */
export function MarkCard({ reference, mark, state, others, limitMs, onRecheck, onKeep, onPropose }: {
  reference: string; mark: Mark; state?: State; others: Mark[]; limitMs?: number; onClose?: () => void;
  onRecheck: (includePerformance?: boolean) => void; onKeep?: () => void; onPropose?: () => void;
}) {
  const s = state ?? verdictOf(mark, true) ?? 'unknown';
  const cells = (mark.cells ?? []).slice(0, 6);
  const [looked, setLooked] = useState(false);
  const twoSided = cells.some(c => c.before != null && c.before !== '');
  const beforeHeading = mark.name === 'SQL' ? 'SQL' : mark.name === 'Trusted number' ? 'You trust'
    : mark.name === 'Filters' ? 'Source rows' : mark.name === 'Equivalence' ? 'Before' : 'Reference';
  const afterHeading = mark.name === 'SQL' || mark.name === 'Trusted number' ? 'Measure'
    : mark.name === 'Filters' ? 'Rows used' : mark.name === 'Equivalence' ? 'After' : 'Value';
  const contexts = mark.chosenContexts ?? [];
  const values = placeValues(others);
  // Performance shows its timings table only: the same places again as Where and Value said nothing new (rc28 tester gap 6).
  const timed = mark.name === 'Performance' && !!mark.timings?.length;
  // A narrow card puts the place over its numbers when a row has two or more of them (marks.css, rc28 tester gap 4).
  const stack = (numbers: number) => numbers > 1 ? { className: ' mk-stack', style: { '--mk-n': numbers } as CSSProperties } : { className: '', style: undefined };
  const cellsStack = stack(twoSided ? 2 : 1), timingStack = stack(3);
  return <div className={`mk-card mk-card-${s}`} data-testid={`mk-card-${mark.name}`}>
    <p className="mk-say">{cellText(detailSentence(mark, s, limitMs))}</p>
    {mark.name === 'Equivalence' && (mark.beforeFormula || mark.afterFormula) && mark.beforeFormula !== mark.afterFormula && <div className="mk-edit-diff" data-testid="mk-edit-diff" aria-label="Your edit">
      <code className="mk-was"><span aria-hidden="true">− </span>{mark.beforeFormula}</code>
      <code className="mk-now"><span aria-hidden="true">+ </span>{mark.afterFormula}</code>
    </div>}
    {cells.length > 0 && !timed && <table className={`ck-table mk-cells${mark.name === 'Equivalence' ? ' mk-equivalence-cells' : ''}${cellsStack.className}`} style={cellsStack.style}>
      <thead><tr><th>{mark.name === 'Equivalence' ? 'Place' : 'Where'}</th>{twoSided && <th>{beforeHeading}</th>}<th>{twoSided ? afterHeading : 'Value'}</th></tr></thead>
      <tbody>{cells.map((c, i) => {
        // The engine formats every number once (MarkNumberFormat); a timing or a message has no display text and shows as written.
        const [before, after] = twoSided ? [c.beforeText ?? cellText(c.before), c.afterText ?? cellText(c.after)]
          : ['', mark.name === 'Equivalence' ? c.afterText ?? cellText(c.after) : cellText(placeValue(c, values))];
        return <tr key={i}><td>{cellText(c.context) || 'Grand total'}</td>
          {twoSided && <td data-label={beforeHeading}>{before}</td>}<td data-label={twoSided ? afterHeading : 'Value'}>{after}</td></tr>;
      })}</tbody>
    </table>}
    {timed && <table className={`ck-table mk-cells${timingStack.className}`} style={timingStack.style}>
      <thead><tr><th>Where</th><th>Median</th><th>Spread</th><th>Cold</th></tr></thead>
      <tbody>{mark.timings!.slice(0, 6).map((t, i) => <tr key={i}><td>{cellText(t.context) || 'Grand total'}{t.regression ? ' ▲ slower' : ''}</td><td data-label="Median">{ms(t.medianMs)}{t.beforeMedianMs != null ? ` (was ${ms(t.beforeMedianMs)})` : ''}</td><td data-label="Spread">{ms(t.maxMs - t.minMs)}</td><td data-label="Cold">{ms(t.coldMs)}</td></tr>)}</tbody>
    </table>}
    <MarkAction reference={reference} mark={mark} state={s} onRecheck={onRecheck} onKeep={onKeep} onPropose={onPropose}
      looked={contexts.length > 0 ? { open: looked, toggle: () => setLooked(!looked) } : undefined} />
    {looked && contexts.length > 0 && <div className="mk-contexts" data-testid="mk-contexts">
      <ul>{contexts.map(c => <li key={c.id}><b>{contextName(c)}</b> {c.expectedBehaviour || c.reason}{c.expectedBehaviour && c.reason && c.expectedBehaviour.trim() !== c.reason.trim() ? <span className="mk-faint"> {c.reason}</span> : null}</li>)}</ul>
    </div>}
  </div>;
}

function MarkAction({ reference, mark, state, onRecheck, onKeep, onPropose, looked }: {
  reference: string; mark: Mark; state: State; onRecheck: (includePerformance?: boolean) => void; onKeep?: () => void; onPropose?: () => void;
  looked?: { open: boolean; toggle: () => void };
}) {
  const host = useMarksHost();
  const [note, setNote] = useState('');
  const [busy, setBusy] = useState(false);
  // An action note belongs to this result. A new edit supplies a new Equivalence result.
  useEffect(() => { if (mark.name === 'Equivalence') setNote(''); }, [reference, mark]);
  const status = note ? <p className="mk-note" role="status" data-testid="mk-action-note">{note}</p> : null;
  // No count on the button: the sentence's "<n> places" counts the cells checked (D5), and a second number beside it counted
  // something else, the places it picked (wr1 F9). The list behind it shows them.
  const where = looked ? <button type="button" className="mk-link" aria-expanded={looked.open} onClick={looked.toggle}>Where it looked</button> : null;
  const check = <button type="button" className="sem-btn sem-btn-sm" onClick={() => { setNote('Checking again.'); onRecheck(mark.name === 'Performance'); }}>Check again</button>;
  const codes = mark.actionCodes ?? [];
  // One press, then a note and a fresh check. The write itself is named at the press, so each call site reads plainly.
  const act = async (write: () => Promise<unknown>, working: string, done: string, failed: string) => {
    setBusy(true); setNote(working);
    try { await write(); setNote(done); onRecheck(); }
    catch (error) { setNote(errorText(error, failed)); }
    finally { setBusy(false); }
  };
  const expected = <button key="expected" type="button" className="sem-btn sem-btn-sm" disabled={busy}
    onClick={() => void act(() => host.rpc('setMeasureTotals', null, reference, true), 'Saving that this total is expected.', 'Saved. This measure will no longer warn about its totals.', 'The expected total was not saved.')}>This is expected</button>;
  if (mark.name === 'Totals' && (codes.includes('expected') || (!mark.actionCodes && state === 'review')))
    return <><div className="mk-acts">{expected}</div>{status}</>;
  if (mark.name === 'Equivalence') {
    if (state !== 'review') return status;
    return <><div className="mk-acts"><button type="button" className="sem-btn sem-btn-sm sem-btn-primary" onClick={() => onKeep?.()}>Keep</button>
      <button type="button" className="sem-btn sem-btn-sm" onClick={() => { host.undo(); setNote('Undo sent. The numbers are compared again.'); }}>Undo my edit</button></div>{status}</>;
  }
  // A lost filter: two actions of equal weight (ra1 item 3). Rewriting the Description to fit the formula is behind the full Description.
  if (mark.name === 'Filters' && (codes.includes('fix_formula') || codes.includes('on_purpose') || codes.includes('not_on_purpose'))) {
    const onPurpose = (on: boolean) => void act(() => host.rpc('setMeasureFiltersOnPurpose', reference, on), 'Saving.',
      on ? 'Saved. This measure no longer warns about this filter.' : 'Saved. The filter warning is back.', 'That was not saved.');
    return <><div className="mk-acts">
      {codes.includes('fix_formula') && <button type="button" className="sem-btn sem-btn-sm" onClick={() => host.editFormula?.(reference)}>Fix the formula</button>}
      {codes.includes('on_purpose') && <button type="button" className="sem-btn sem-btn-sm" disabled={busy} onClick={() => onPurpose(true)}>It is on purpose</button>}
      {codes.includes('not_on_purpose') && <button type="button" className="sem-btn sem-btn-sm" disabled={busy} onClick={() => onPurpose(false)}>It is not on purpose</button>}
      {where}</div>{status}</>;
  }
  return <><div className="mk-acts">{check}{where}</div>{status}</>;
}

const SAVED_CHECKING = 'Saved. Checking it now.';
/** Add a check: the set-up of the three marks a person turns on, only for the ones that do not apply yet. */
export function AddCheck({ reference, marks, running = false, onRecheck }: { reference: string; marks: Mark[]; running?: boolean; onRecheck: (includePerformance?: boolean) => void }) {
  const host = useMarksHost();
  const [menu, setMenu] = useState(false);
  const [form, setForm] = useState(false);
  const [note, setNote] = useState('');
  const [busy, setBusy] = useState(false);
  const grand = marks.flatMap(m => m.cells ?? []).find(c => /grand total/i.test(c.context ?? ''))?.after ?? '';
  const [value, setValue] = useState(grand && grand !== 'BLANK' ? grand : '');
  // "Saved. Checking it now." lasts as long as that check: it goes when the check ran the number, or when the card stops
  // checking (rc34 tester Next 4: it stayed 30 minutes and more). `saved` holds the Trusted number result seen at the save.
  const saved = useRef<{ ran: string | null; running: boolean } | null>(null);
  const trustedRan = marks.find(m => m.name === 'Trusted number')?.lastRan ?? null;
  useEffect(() => {
    const at = saved.current;
    if (!at) return;
    if (running) { at.running = true; return; }
    if (at.running || trustedRan !== at.ran) { saved.current = null; setNote(n => n === SAVED_CHECKING ? '' : n); }
  }, [running, trustedRan]);
  const missing = ['SQL', 'Trusted number', 'Performance'].filter(n => { const m = marks.find(x => x.name === n); return !m || !applies(m); });
  if (!missing.length && !form) return null;
  const sql = () => {
    setMenu(false);
    host.rpc<unknown[]>('listSqlSources').then(sources => {
      if (!Array.isArray(sources) || sources.length === 0) { host.open('connections'); setNote('No SQL source found. Connect one.'); }
      else { host.open('checks', reference); setNote('Checks is open with this measure. Pick the SQL that should match it.'); }
    }).catch(() => { host.open('connections'); setNote('No SQL source found. Connect one.'); });
  };
  const time = async () => {
    setMenu(false); setBusy(true); setNote('Timing. About 30 seconds.');
    try { await host.rpc('setMeasurePerformance', null, reference, true); onRecheck(true); }
    catch (error) { setNote(errorText(error, 'Performance could not be included.')); }
    finally { setBusy(false); }
  };
  const save = async () => {
    setBusy(true); setNote('Saving your number and checking it.');
    try {
      await host.rpc('saveTest', { id: '', kind: 'measureValue', title: `${reference.split('/').pop()}: All rows`, targetRef: reference, enabled: true,
        paramsJson: JSON.stringify({ measureRef: reference, expectedValue: value.trim(), provenance: 'person' }) });
      saved.current = { ran: trustedRan, running: false };
      setNote(SAVED_CHECKING); setForm(false); onRecheck();
    } catch (error) { setNote(errorText(error, 'The number was not saved.')); }
    finally { setBusy(false); }
  };
  return <span className="mk-add">
    {missing.length > 0 && <button type="button" className="mk-link" aria-haspopup="menu" aria-expanded={menu} data-testid="mk-add" onClick={() => setMenu(!menu)}>Add a check</button>}
    {menu && <div className="mk-menu" role="menu" data-testid="mk-add-menu">
      {missing.includes('SQL') && <button type="button" role="menuitem" onClick={sql}>Compare with SQL</button>}
      {missing.includes('Trusted number') && <button type="button" role="menuitem" onClick={() => { setMenu(false); setForm(true); setNote(''); }}>Save a number you trust</button>}
      {missing.includes('Performance') && <button type="button" role="menuitem" disabled={busy} onClick={() => void time()}>Time this measure</button>}
    </div>}
    {form && <div className="mk-trusted" data-testid="mk-trusted">
      <p className="mk-sub"><b>Which number do you trust?</b></p>
      <label className="mk-field"><span>Number</span><input value={value} inputMode="decimal" aria-label="Number you trust" onChange={e => setValue(e.target.value)} /></label>
      <div className="mk-acts"><button type="button" className="sem-btn sem-btn-sm sem-btn-primary" disabled={busy || !value.trim()} onClick={() => void save()}>Save and run</button></div>
    </div>}
    {note && <p className="mk-note" role="status" data-testid="mk-add-note">{note}</p>}
  </span>;
}
