import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { copyText, runHostCommand, onEngineState, onReconnect, onStartPlanChange, pickWorkingCopyFolder, rpc, rpcPressed, loadState, saveState } from './bridge';
import { listSqlSources, openSqlSources, type SqlSourceRecord } from './connectionshub';
import { useConnection } from './connection';
import { openStartPlanConsentCard, START_CARD, type ConsentInfoW } from './consent';
import * as M from './startplan-model';
import { draftStore, heldId, useHeldDrafts } from './startplan-drafts';
import { draftModel, draftStore as sharedDrafts, wordsAsText } from './drafts';
import { OrphanDrafts } from './orphandrafts';
import { gradeBand } from './readinessband.mjs';
import './startplan.css';

// ===================================================================================================
// The start plan (lane sf-studio, L12): where a new model from a SQL source is READ, DECIDED and APPROVED before it
// exists. Kane picked option A of the mockups (1 Oct): one list on the Plan page in five sections with a Needs you
// band above them, B's link drawing at the top of Links and dates, C's source name beside the model name.
//
// ---- the calls (the contract's IStartPlanEngine verbs, camelCased, answered with the whole view) --------------
//
//   startModelFromSql(request, 'human')       the start form                              creates the empty model, reads the source
//   getStartPlan()                            the page, and again on startPlan/didChange
//   editStartPlan({op, target, value}, 'human')    keep or drop a line, pin a value, answer a question
//   grantStartDisclosure(digest, 'human')     the first question on Pro: what Jev may look at
//   approveStartPlan(digest)                  PERSON ONLY (HumanGovernanceRpcTarget): the digest the person looked at
//   buildStartPlan(digest, 'human')           one build step, one undo; the person OR the assistant may press it once approved
//   discardStartPlan('human')                 throws the plan away; the empty model stays where it was saved
//
// The assistant's words are NOT written here. get_start_words and write_start_words are the assistant's MCP tools;
// the page shows how many are written and, when they land, the digest moves and an approval made before is void.
//
// Everything the page SAYS is a function of the engine's view (startplan-model.ts), so the test executes it.
// ===================================================================================================

type Tier = M.Tier;

// ---------------------------------------------------------------- the marks

/** A row-wide press target. It is a div with the button role, not a button, so the list card's content starts its padding
 *  in (a full-width button would sit on the card's edge) and the keyboard still works: Enter and Space press it. */
const pressable = (onPress: () => void) => ({
  role: 'button' as const, tabIndex: 0, onClick: onPress,
  onKeyDown: (e: React.KeyboardEvent) => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); onPress(); } },
});

const MARK_PATH: Record<M.Mark['key'], React.ReactElement> = {
  jev: <path d="M6 .8l1.4 3.8L11.2 6 7.4 7.4 6 11.2 4.6 7.4.8 6l3.8-1.4z" fill="currentColor" />,
  rule: <rect x="2" y="2" width="8" height="8" fill="currentColor" />,
  you: <circle cx="6" cy="6" r="4.3" fill="currentColor" />,
  assistant: <path d="M6 1L11 6 6 11 1 6z" fill="currentColor" />,
  default: <circle cx="6" cy="6" r="3.6" fill="none" stroke="currentColor" strokeWidth="1.4" />,
};

/** Who decided it: a shape and a word, so it reads without colour. */
export function Who({ mark, confidence }: { mark: M.Mark; confidence?: number | null }) {
  return <span className={`sp-who sp-who-${mark.key}`} data-who={mark.key}>
    <svg className="sp-ico" viewBox="0 0 12 12" aria-hidden="true">{MARK_PATH[mark.key]}</svg>
    <span>{mark.label}</span>{confidence ? <span className="sp-conf sp-num">{confidence}%</span> : null}
  </span>;
}

// ---------------------------------------------------------------- the link drawing (mockup B)

export function LinkDrawing({ graph }: { graph: M.LinkGraph }) {
  const on = graph.edges.filter((e) => e.active).length, off = graph.edges.length - on;
  const at = new Map(graph.nodes.map((n) => [n.name, n]));
  return <div className="sp-draw" data-testid="sp-link-drawing">
    <svg viewBox={`0 0 ${graph.width} ${graph.height}`} width={graph.width} role="img" aria-label={M.COPY.linksDrawingLabel}>
      <defs><marker id="sp-arrow" viewBox="0 0 8 8" refX="7" refY="4" markerWidth="6" markerHeight="6" orient="auto"><path d="M0 0L8 4L0 8z" className="sp-arrow" /></marker></defs>
      {graph.edges.map((e) => {
        const a = at.get(e.from), b = at.get(e.to);
        if (!a || !b) return null;
        // Two links between one pair of tables leave and arrive apart (offset), so both are seen.
        const off = e.offset ?? 0;
        const x1 = a.x + a.w, y1 = a.y + a.h / 2 + off, x2 = b.x, y2 = b.y + b.h / 2 + off, mx = (x1 + x2) / 2;
        // A link that skips a column goes round the boxes in between, along the top or the bottom of the drawing.
        const lane = e.lane === 'top' ? 6 + off : e.lane === 'bottom' ? graph.height - 6 + off : null;
        const d = lane === null ? `M${x1} ${y1} C${mx} ${y1} ${mx} ${y2} ${x2 - 2} ${y2}`
          : `M${x1} ${y1} C${x1 + 28} ${y1} ${x1 + 28} ${lane} ${x1 + 56} ${lane} L${x2 - 56} ${lane} C${x2 - 28} ${lane} ${x2 - 28} ${y2} ${x2 - 2} ${y2}`;
        return <path key={e.id} className={e.active ? 'sp-edge' : 'sp-edge sp-edge-off'} d={d} markerEnd="url(#sp-arrow)" data-active={e.active ? 'on' : 'off'} />;
      })}
      {graph.nodes.map((n) => <g key={n.name} data-node={n.name}>
        <rect className="sp-node-box" x={n.x} y={n.y} width={n.w} height={n.h} rx="3" />
        <text className="sp-node-name" x={n.x + 8} y={n.y + (n.sourceName ? 16 : 24)}>{n.name}</text>
        {n.sourceName && <text className="sp-node-src" x={n.x + 8} y={n.y + 30}>{n.sourceName}</text>}
      </g>)}
    </svg>
    <div className="sp-draw-t"><b>{M.COPY.linksDrawingTitle}</b>{M.COPY.linksDrawingLead(on, off)}
      <div className="sp-draw-key"><span><i />{M.COPY.linkOn}</span>{off > 0 && <span><i className="off" />{M.COPY.linkOff}</span>}</div></div>
  </div>;
}

// ---------------------------------------------------------------- one row, and what opens under it

/** The key the plan's typed words are held under: the open model's file (App.tsx sets it from the session), else the plan's own destination. Never the session id. */
// The AI readiness letter takes its band colour (Kane, 8 Oct), from the one band function the AI readiness page uses. A figure that is
// not a letter gets none.
const bandClass = (letter: string): string => { const b = gradeBand(letter); return b ? `sem-rag-${b} sem-rag-text` : ''; };
const planModelKey = (view: M.StartPlanView): string => draftModel() || M.modelKeyOf(view);

function whoWords(line: M.StartLine, tier: Tier): string {
  switch (M.lineMark(line, tier).key) {
    case 'jev': return M.COPY.whoJev;
    case 'default': return M.COPY.whoDefault;
    case 'assistant': return M.COPY.whoAssistant;
    case 'you': return M.COPY.whoPerson;
    default: return M.COPY.whoRule;
  }
}

function LineRow({ line, view, tier, open, changed, drafted, onToggle }: {
  line: M.StartLine; view: M.StartPlanView; tier: Tier; open: boolean; changed: boolean; drafted: boolean; onToggle: () => void;
}) {
  const mark = M.lineMark(line, tier), state = M.lineState(line, view, tier), choice = M.lineChoice(line), value = M.lineValue(line);
  const measure = line.item?.kind === 'create_measure';
  return <div className={`sp-row${state.tone === 'need' ? ' sp-row-need' : ''}${line.held ? ' sp-row-held' : ''}${open ? ' sp-row-open' : ''}${changed ? ' sp-changed' : ''}`}
    aria-expanded={open} data-testid={`sp-row-${line.item?.id}`} data-who={mark.key} data-kept={line.kept ? 'yes' : 'no'} data-held={line.held ? 'yes' : 'no'} data-draft={drafted ? 'yes' : 'no'} {...pressable(onToggle)}>
    <Who mark={mark} confidence={M.lineConfidence(line)} />
    <span className="sp-row-t">
      <b><span className="sp-who2"><Who mark={mark} /></span>{M.lineTitle(line)}{choice && <span className="sp-choice"> {'· '}{tier.pro && state.tone === 'need' ? 'for now ' : ''}{choice}</span>}</b>
      <span>{measure && line.item?.after ? <code>{M.lineSub(line)}</code> : M.lineSub(line)}{line.kept && line.nameFlag && <span className="sp-flag" data-testid="sp-name-flag"> {'· '}{M.COPY.nameFlagged}</span>}
        {drafted && <span className="sp-flag sp-flag-draft" data-testid="sp-draft-flag" title={M.COPY.draftKeptTip}> {'· '}{M.COPY.draftFlag}</span>}</span>
    </span>
    <span className={`sp-state sp-state-${state.tone}`} data-state={state.text}>{state.text}</span>
    <span className="sp-row-val sp-num">{value}</span>
  </div>;
}

/** A one-line text box with Save and Cancel, for a new name or the words on a line. Enter saves, Escape cancels. */
function Editor({ label, value, rows, busy, testid, draft, onDraft, onTook, onSave, onCancel }: {
  label: string; value: string; rows?: number; busy: boolean; testid: string;
  /** What was typed here before and was not saved (held above this editor, join 11), over the value it was typed over. */
  draft?: { text: string; base: string } | null;
  /** Told after every change: what is typed over which value, or null when nothing is typed any more. The caller holds it, so it outlives this editor. */
  onDraft?: (draft: { text: string; base: string } | null) => void;
  /** Told when the engine took exactly `text`: the caller ends the held draft only if it still holds those words (R-B). */
  onTook?: (text: string) => void;
  /** Sends the words. True means the engine accepted them; a refusal or a failure is false, and the words and the editor stay (the page shows the reason). */
  onSave: (text: string) => Promise<boolean> | boolean | void; onCancel: () => void;
}) {
  const [base, setBase] = useState(draft ? draft.base : value);
  const [text, setText] = useState(draft ? draft.text : value);
  const [saving, setSaving] = useState(false);
  const mounted = useRef(true);
  useEffect(() => () => { mounted.current = false; }, []);
  const textRef = useRef(text); textRef.current = text;
  const typed = text.trim() !== base.trim();
  // The value moved under typed words (the assistant renamed it while the editor was shut): the words stay and the person chooses. Nothing typed: follow it.
  const asking = typed && base.trim() !== value.trim();
  useEffect(() => { if (!typed && base !== value) { setText(value); setBase(value); } }, [value]);
  useEffect(() => { onDraft?.(typed ? { text, base } : null); }, [text, base, typed]);
  const keys = (e: React.KeyboardEvent) => {
    if (e.key === 'Escape') { e.preventDefault(); onCancel(); }   // closes the editor and keeps the draft
    if (e.key === 'Enter' && !rows && text.trim() && !asking && !saving) { e.preventDefault(); void save(); }
  };
  // R-B: the draft ends only when the engine accepted EXACTLY the words held. It used to end before the write was sent, so a refusal (a name the plan already has) took the typed name with it.
  // Words typed while the save was pending are not the save's: the editor stays open on them, over the name that was saved.
  const save = async () => {
    const sent = text;
    setSaving(true);
    let took = false;
    try { took = (await onSave(sent)) !== false; } catch { took = false; } finally { setSaving(false); }   // a call that throws is a failed write: the words stay
    if (!took) return;
    onTook?.(sent);
    if (!mounted.current) return;
    if (textRef.current === sent) onCancel(); else setBase(sent);
  };
  return <div className="sp-editor" data-testid={`${testid}-editor`}>
    {rows
      ? <textarea aria-label={label} rows={rows} value={text} autoFocus data-testid={`${testid}-input`} onChange={(e) => setText(e.target.value)} onKeyDown={keys} />
      : <input aria-label={label} value={text} autoFocus data-testid={`${testid}-input`} onChange={(e) => setText(e.target.value)} onKeyDown={keys} />}
    {asking && <div className="sp-words-moved" role="alert" data-testid={`${testid}-moved`}>
      <div>{M.COPY.renameMoved(value)}</div>
      <div className="sp-words-choose">
        <button type="button" className="sem-btn sem-btn-sm" data-testid={`${testid}-keepmine`} title={M.COPY.keepMineTip} onClick={() => setBase(value)}>{M.COPY.keepMine}</button>
        <button type="button" className="sem-btn sem-btn-sm" data-testid={`${testid}-usenew`} title={M.COPY.useNewTip} onClick={() => { setText(value); setBase(value); }}>{M.COPY.useNew}</button>
      </div>
    </div>}
    <span className="sp-editor-acts">
      <button type="button" className="sem-btn sem-btn-sm sem-btn-primary" disabled={busy || saving || asking || !text.trim() || text.trim() === value.trim()} data-testid={`${testid}-save`} onClick={() => void save()}>{M.COPY.save}</button>
      <button type="button" className="sem-btn sem-btn-sm" data-testid={`${testid}-cancel`} onClick={() => { onDraft?.(null); onCancel(); }}>{typed ? M.COPY.discardWords : M.COPY.cancel}</button>
    </span>
  </div>;
}

/** The editor for the words on a line. It keeps the person's draft: a Save does not close it until the engine has taken the words, and when the line's words have
 *  moved under a typed draft (the assistant wrote since, or the engine refused the save for that reason) the newer words are shown beside the draft with the choice
 *  every other editor offers, Keep mine or Use the new one (editbase.tsx). Nothing is saved over the newer words until the person chose (UAT-J8-A-002). */
export function WordsEditor({ start, live, draft, busy, away, onDraft, onTook, onSave, onCancel }: {
  /** The words the person started from. A save names them, and the engine refuses it over newer ones (join 8, K9). */
  start: M.Words;
  /** The words the line holds now, as the page last read them. */
  live: M.Words | null;
  /** The draft to open with; the words themselves when absent. */
  draft?: string;
  busy: boolean;
  /** The engine is not there. The box stays open to type in and Save waits (the draft is held above this editor, so it is kept either way). */
  away?: boolean;
  /** Told after every change: what is typed over which words, or null when nothing is typed any more. The caller holds it, so it outlives this editor (D1). */
  onDraft?: (draft: { text: string; base: string } | null) => void;
  /** Told when the engine took exactly `text`: the caller ends the held draft only if it still holds those words (R-B). */
  onTook?: (text: string) => void;
  onSave: (start: M.Words, text: string) => Promise<M.WordsSaveResult>;
  onCancel: () => void;
}) {
  const [base, setBase] = useState<M.Words>(start);
  const [text, setText] = useState(draft ?? start.current);
  const [said, setSaid] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);
  const mounted = useRef(true);
  useEffect(() => () => { mounted.current = false; }, []);
  const textRef = useRef(text); textRef.current = text;
  const move = M.wordsMove(base, text, live);
  // Nothing typed: the editor follows the newer words. The same words typed: the line is what the draft says, so a save has nothing to confirm.
  useEffect(() => {
    if (move.kind === 'follow') { setText(move.text); setBase(live!); }
    else if (move.kind === 'same') setBase(live!);
  }, [move.kind, live?.current]);
  const asking = move.kind === 'ask';
  const typed = text.trim() !== base.current.trim();
  // The draft is held above this editor: another row opening, a remount on a restarted engine and a failed read must not take it.
  useEffect(() => { onDraft?.(typed ? { text, base: base.current } : null); }, [text, base.current, typed]);
  const save = async () => {
    const sent = text;
    setSaving(true); setSaid(null);
    try {
      const r = await onSave(base, sent);
      // R-B: the draft ends only when the engine accepted EXACTLY the words held. Words typed while the save was pending are not the save's: they stay held, and the editor
      // stays open on them over the words that were saved. A save that completes after the row was left ends only a draft that still holds what it sent.
      if (r.saved) {
        onTook?.(sent);
        if (!mounted.current) return;
        if (textRef.current === sent) { onCancel(); return; }
        setBase({ ...base, current: sent.trim() });
        return;
      }
      // The engine refused. The editor stays open with the draft; a conflict shows itself once the newer words are on the page.
      setSaid(r.moved ? M.COPY.wordsMoved : r.said);
    } finally { setSaving(false); }
  };
  const keys = (e: React.KeyboardEvent) => { if (e.key === 'Escape') { e.preventDefault(); onCancel(); } };
  const box = <textarea aria-label={M.COPY.theWords} rows={3} value={text} autoFocus data-testid="sp-words-input" onChange={(e) => setText(e.target.value)} onKeyDown={keys} />;
  return <div className="sp-editor sp-words-editor" data-testid="sp-words-editor">
    {asking ? <div className="sp-words-moved" role="alert" data-testid="sp-words-moved">
      <div>{M.COPY.wordsMoved}</div>
      <div className="sp-words-pair">
        <div><b>{M.COPY.yourWords}</b>{box}</div>
        <div><b>{M.COPY.newWords}</b><div className="sp-words-theirs" data-testid="sp-words-theirs">{move.theirs}</div></div>
      </div>
      <div className="sp-words-choose">
        <button type="button" className="sem-btn sem-btn-sm" data-testid="sp-words-keepmine" disabled={saving} title={M.COPY.keepMineTip} onClick={() => setBase(live!)}>{M.COPY.keepMine}</button>
        <button type="button" className="sem-btn sem-btn-sm" data-testid="sp-words-usenew" disabled={saving} title={M.COPY.useNewTip} onClick={() => { setText(live!.current); setBase(live!); }}>{M.COPY.useNew}</button>
      </div>
    </div> : box}
    {!asking && said && !(away && said === M.COPY.engineRestarting) && <div className="sp-words-said" role="alert" data-testid="sp-words-said">{said}</div>}
    {away && <div className="sp-words-said" role="status" data-testid="sp-words-away">{M.COPY.saveWaits}</div>}
    <span className="sp-editor-acts">
      <button type="button" className="sem-btn sem-btn-sm sem-btn-primary" data-testid="sp-words-save" disabled={busy || !!away || saving || asking || !text.trim() || !typed}
        title={asking ? M.COPY.wordsChooseFirst : undefined} onClick={() => void save()}>{M.COPY.save}</button>
      <button type="button" className="sem-btn sem-btn-sm" data-testid="sp-words-cancel" onClick={() => { onDraft?.(null); onCancel(); }}>{typed ? M.COPY.discardWords : M.COPY.cancel}</button>
    </span>
  </div>;
}

export function LineDetail({ line, view, tier, busy, away, modelKey, onEdit, onWords, onKeepName }: {
  line: M.StartLine; view: M.StartPlanView; tier: Tier; busy: boolean; onEdit: (e: M.StartEdit) => Promise<boolean> | void;
  /** The engine is not there: the words editor keeps what is typed and Save waits. */
  away?: boolean;
  /** The model's file, which a typed draft is held under (never the session id). Without it nothing is held above the editor. */
  modelKey?: string;
  onWords?: (words: M.Words, text: string) => Promise<M.WordsSaveResult>;
  onKeepName?: (ref: string) => void;
}) {
  const item = line.item;
  const before = item?.before, after = item?.after;
  const group = (view.sections ?? []).flatMap((s) => s.groups ?? []).find((g) => (g.lines ?? []).includes(line));
  const source = M.lineSource(line, group);
  const can = M.lineControls(line);
  const lineId = item?.id ?? '';
  // Words typed earlier and not saved: found by the model's file, this line and the kind of words, so the row opens on them again (D1: a row switch, a remount and a restart keep them).
  const heldDraft = () => (modelKey && can.words ? draftStore.get(modelKey, lineId, can.words.kind) : null);
  // A new name typed and not saved is held the same way (kind 'rename'): found again by the model's file and this line.
  const heldRename = () => (modelKey ? draftStore.get(modelKey, lineId, 'rename') : null);
  const keepRename = (d: { text: string; base: string } | null) => {
    if (!modelKey) return;
    if (d) draftStore.set(modelKey, lineId, 'rename', d); else draftStore.clear(modelKey, lineId, 'rename');
  };
  const tookRename = (text: string) => { if (modelKey) draftStore.clearIfSubmitted(modelKey, lineId, 'rename', text); };
  const tookWords = (text: string) => { if (modelKey && can.words) draftStore.clearIfSubmitted(modelKey, lineId, can.words.kind, text); };
  const [renaming, setRenaming] = useState(() => !!heldRename());
  const [wording, setWording] = useState(() => !!heldDraft());
  // The words the person STARTED from, taken when the editor opens. A live update of the plan while it is open changes `can.words`, and a save must name the words the person
  // was looking at (the engine refuses it over newer ones), never the newer words the text box never showed (Sol, join 8, K9). A held draft was typed over ITS own words.
  const [wordsAtOpen, setWordsAtOpen] = useState<M.Words | null>(() => { const h = heldDraft(); return h && can.words ? { ...can.words, current: h.base } : null; });
  const openWords = () => {
    if (wording || !can.words) return;
    const h = heldDraft();
    setWordsAtOpen(h ? { ...can.words, current: h.base } : can.words); setWording(true);
  };
  const closeWords = () => { setWording(false); setWordsAtOpen(null); };
  // A row that opens on a held draft while the line's words control is not on the page yet (a plan read in flight) takes its start from the draft once it is.
  const startWords: M.Words | null = wordsAtOpen ?? (wording && can.words ? { ...can.words, current: heldDraft()?.base ?? can.words.current } : null);
  const keepDraft = (d: { text: string; base: string } | null) => {
    if (!modelKey || !can.words) return;
    if (d) draftStore.set(modelKey, lineId, can.words.kind, d); else draftStore.clear(modelKey, lineId, can.words.kind);
  };
  return <div className="sp-detail" data-testid={`sp-detail-${item?.id}`}>
    <dl>
      {line.held && <><dt>{M.COPY.stateHeld}</dt><dd data-testid="sp-detail-held">{line.held}</dd></>}
      <dt>{M.COPY.detailWhy}</dt><dd>{line.why || item?.rationale || ''}</dd>
      {(before || after) && <><dt>{M.COPY.detailChange}</dt><dd>{before ? <>{M.shownValue(line, before)} {'to'} </> : null}<code>{M.shownValue(line, after)}</code></dd></>}
      {(line.sets ?? []).length > 0 && <><dt>{M.COPY.detailSets}</dt><dd data-testid="sp-detail-sets">{(line.sets ?? []).map((x) => <div key={x.property}>{x.words}</div>)}</dd></>}
      {source && <><dt>{M.COPY.detailSource}</dt><dd><code data-testid="sp-detail-source">{source}</code></dd></>}
      <dt>{M.COPY.detailWho}</dt><dd>{whoWords(line, tier)}</dd>
    </dl>
    <dl>
      <dt>{M.COPY.detailChoice}</dt>
      <dd>
        <div className="sp-detail-acts">
          {can.switchable && (line.kept
            ? <button type="button" className="sem-btn sem-btn-sm" disabled={busy} data-testid="sp-drop" onClick={() => onEdit(M.editLine(line, false))}>{M.COPY.drop}</button>
            : <button type="button" className="sem-btn sem-btn-sm" disabled={busy} data-testid="sp-keep" onClick={() => onEdit(M.editLine(line, true))}>{M.COPY.keep}</button>)}
          {can.pin && <button type="button" className="sem-btn sem-btn-sm" disabled={busy} data-testid="sp-pin" onClick={() => onEdit(M.pinLine(line))}>{M.COPY.pin}</button>}
          {can.rename && <button type="button" className="sem-btn sem-btn-sm" disabled={busy} data-testid="sp-rename" onClick={() => setRenaming(true)}>{M.COPY.rename}</button>}
          {can.keepName && onKeepName && <button type="button" className="sem-btn sem-btn-sm" disabled={busy} data-testid="sp-keepname" title={can.keepName.why} onClick={() => onKeepName(can.keepName!.ref)}>{M.COPY.keepName}</button>}
          {can.words && onWords && <button type="button" className="sem-btn sem-btn-sm" disabled={busy} data-testid="sp-words" onClick={openWords}>{M.COPY.editWords}</button>}
        </div>
        {renaming && can.rename && <Editor label={M.COPY.newName} value={can.rename.current} busy={busy} testid="sp-rename" draft={heldRename()} onDraft={keepRename} onTook={tookRename}
          onSave={async (t) => (await onEdit(M.renameEdit(can.rename!.target, t))) !== false} onCancel={() => setRenaming(false)} />}
        {wording && startWords && onWords && <WordsEditor start={startWords} live={can.words} busy={busy} away={away} draft={heldDraft()?.text}
          onDraft={keepDraft} onTook={tookWords} onSave={(base, t) => onWords(base, t)} onCancel={closeWords} />}
        {can.keepName && <div className="sp-muted" data-testid="sp-keepname-why">{can.keepName.why} {M.COPY.keepNameNote}</div>}
        <div className="sp-muted">{item?.kind === 'mark_date_table' ? M.COPY.dateTableNote : can.pin ? M.COPY.pinNote : M.COPY.keepNote}</div>
      </dd>
    </dl>
  </div>;
}

/** The columns of one table, folded under its line: names with their types, and a rename and a leave-out for each. */
export function ColumnList({ columns, open, busy, modelKey, onToggle, onEdit, onKeepName }: {
  columns: M.StartColumn[]; open: boolean; busy: boolean; onToggle: () => void; onEdit: (e: M.StartEdit) => Promise<boolean> | void;
  /** The model's file, which a typed new name is held under (never the session id). Without it nothing is held above the editor. */
  modelKey?: string;
  onKeepName?: (ref: string) => void;
}) {
  // A column's new name typed and not saved is held above its editor (kind 'rename'): the list says so while shut, and the column opens on it again.
  const colId = (c: M.StartColumn) => `col:${c.ref}`;
  const heldCols = useHeldDrafts(modelKey ?? '');
  const heldRename = (c: M.StartColumn) => (modelKey ? draftStore.get(modelKey, colId(c), 'rename') : null);
  const [renaming, setRenaming] = useState<string | null>(() => columns.find((c) => heldRename(c))?.ref ?? null);
  const anyHeld = columns.some((c) => heldCols.has(heldId(colId(c), 'rename')));
  return <div className="sp-cols" data-testid="sp-columns" aria-expanded={open}>
    <div className="sp-cols-h" data-testid="sp-columns-toggle" {...pressable(onToggle)}>
      <span className="sp-car" aria-hidden="true">{open ? '▾' : '▸'}</span><b>{M.COPY.columnsFold(columns.length)}</b>
      {anyHeld && <span className="sp-flag sp-flag-draft" data-testid="sp-draft-flag" title={M.COPY.draftKeptTip}> {'· '}{M.COPY.draftFlag}</span>}
    </div>
    {open && <ul className="sp-cols-l">
      {columns.map((c) => <li key={c.ref} className={c.included ? 'sp-col' : 'sp-col sp-col-out'} data-testid={`sp-col-${c.name}`} data-included={c.included ? 'yes' : 'no'}>
        <span className="sp-col-n"><b>{c.name}</b>{c.sourceName && <span className="sp-muted"> {M.COPY.columnRenamed(c.sourceName)}</span>}</span>
        <span className="sp-col-t sp-muted">{M.columnTags(c).join(' · ')}{c.included && c.nameFlag && <span className="sp-col-flag" data-testid="sp-col-flag"> {'· '}{M.COPY.nameFlagged}</span>}</span>
        <span className="sp-col-a">
          {renaming === c.ref
            ? <Editor label={M.COPY.newName} value={c.name} busy={busy} testid="sp-col-rename" draft={heldRename(c)}
              onDraft={(d) => { if (!modelKey) return; if (d) draftStore.set(modelKey, colId(c), 'rename', d); else draftStore.clear(modelKey, colId(c), 'rename'); }}
              onTook={(t) => { if (modelKey) draftStore.clearIfSubmitted(modelKey, colId(c), 'rename', t); }}
              onSave={async (t) => (await onEdit(M.columnRenameEdit(c, t))) !== false} onCancel={() => setRenaming(null)} />
            : <>
              <button type="button" className="sem-btn sem-btn-sm sem-btn-quiet" disabled={busy} data-testid="sp-col-rename-open" onClick={() => setRenaming(c.ref)}>{M.COPY.rename}</button>
              {c.included && c.nameFlag && onKeepName && <button type="button" className="sem-btn sem-btn-sm sem-btn-quiet" disabled={busy} data-testid="sp-col-keepname" title={c.nameFlag} onClick={() => onKeepName(c.ref)}>{M.COPY.keepName}</button>}
              <button type="button" className="sem-btn sem-btn-sm sem-btn-quiet" disabled={busy} data-testid="sp-col-include" onClick={() => onEdit(M.columnIncludeEdit(c, !c.included))}>{c.included ? M.COPY.columnLeaveOut : M.COPY.columnBringBack}</button>
            </>}
        </span>
      </li>)}
    </ul>}
  </div>;
}

// ---------------------------------------------------------------- Needs you

function NeedsBand({ view, tier, busy, onNeed }: {
  view: M.StartPlanView; tier: Tier; busy: boolean; onNeed: (n: M.StartNeed, option: string) => void;
}) {
  const needs = view.needsYou ?? [];
  if (!needs.length || view.built) return null;
  const lead = M.needsLeadFor(needs);
  return <section className="sp-needs" data-testid="sp-needs" aria-label={M.COPY.needsTitle}>
    <div className="sp-needs-h"><b>{M.COPY.needsTitle}</b><span>{lead ?? M.COPY.needsOther}</span></div>
    {needs.map((n) => {
      const lean = M.needLean(n), body = M.needBody(n);
      const mark = M.needMark(n);
      return <div key={n.id} className="sp-need" data-testid={`sp-need-${n.id}`} data-kind={n.kind}>
        {mark ? <Who mark={mark} /> : <span className="sp-need-nomark" aria-hidden="true" />}
        <div className="sp-need-t"><b>{M.needTitle(n)}</b>{body && <span>{body}</span>}</div>
        {(n.options ?? []).length > 0 && <div className="sp-need-opts" role="group" aria-label={M.needTitle(n)}>
          {(n.options ?? []).map((o) => <button key={o} type="button" className="sem-btn sem-btn-sm" disabled={busy} data-testid={`sp-need-${n.id}-opt`}
            onClick={() => onNeed(n, o)}>{M.optionLabel(o)}{lean === o && (n.options ?? []).length > 1 && <span className="sp-lean">{M.needLeanLabel(n)}</span>}</button>)}
        </div>}
      </div>;
    })}
  </section>;
}

// ---------------------------------------------------------------- the body (presentational: props in, markup out)

export interface BodyProps {
  view: M.StartPlanView; pro: boolean; busy: string | null; error: string | null; sentence: string | null;
  changed: Set<string>; prev?: M.StartPlanView | null; copied?: boolean;
  onEdit: (e: M.StartEdit) => Promise<boolean> | void; onNeed: (n: M.StartNeed, option: string) => void;
  /** The person's edit of the words on a line (write_start_words from the person's door). */
  onWords?: (words: M.Words, text: string) => Promise<M.WordsSaveResult>;
  /** The person's keep of a name the naming rules flag (write_start_words kind keep_name from the person's door). */
  onKeepName?: (ref: string) => void;
  onApprove: () => void; onBuild: () => void; onApproveAndBuild: () => void; onDiscard: () => void; onRetry: () => void;
  onAskAssistant?: () => void;
  /** The plan on screen is the last one read: the engine is not there ('down') or the read failed. Every control that writes waits; typing in an open editor does not. */
  stale?: 'down' | 'failed' | null;
  /** Read the plan again (the notice offers it when a read failed). */
  onReread?: () => void;
  /** The build is running (one call, about ten seconds): the page says so with a moving bar, so the press never looks dead (K25). */
  building?: boolean;
  /** A built plan opened from the Plan's band: back to the Plan, where the model's own rows and Run the plan are. */
  onClose?: () => void;
}

const tallyOf = (lines: M.StartLine[], tier: Tier): string => {
  const n = (k: M.Mark['key']) => lines.filter((l) => M.lineMark(l, tier).key === k).length;
  return [tier.pro ? ['Jev', n('jev')] : ['Default', n('default')], ['rules', n('rule')], ['assistant', n('assistant')], ['you', n('you')]]
    .filter((x) => x[1] as number > 0).map((x) => `${x[0]} ${x[1]}`).join(' · ');
};

/** The question before a build that would leave typed, unsaved words behind: which rows hold them, and the two ways forward. */
export function UnsavedWordsAsk({ names, busy, onSaveFirst, onBuildWithout }: { names: string[]; busy: boolean; onSaveFirst: () => void; onBuildWithout: () => void }) {
  return <div className="sp-note sp-note-warn" role="alert" data-testid="sp-unsaved-ask">
    <span><b>{M.COPY.unsavedTitle}.</b> {M.COPY.unsavedLead} {names.join(', ')}.</span>
    <span className="sp-editor-acts">
      <button type="button" className="sem-btn sem-btn-sm sem-btn-primary" disabled={busy} data-testid="sp-unsaved-save-first" title={M.COPY.saveFirstTip} onClick={onSaveFirst}>{M.COPY.saveFirst}</button>
      <button type="button" className="sem-btn sem-btn-sm" disabled={busy} data-testid="sp-unsaved-build-without" title={M.COPY.buildWithoutTip} onClick={onBuildWithout}>{M.COPY.buildWithout}</button>
    </span>
  </div>;
}

/** The words typed into the plan's rows and not saved, still held after the plan was built (F14) or after it was discarded by anything but the person's own Discard (D3). Listed by the name the row had,
 *  with what was typed; Copy puts them on the clipboard, Discard ends them. Nothing here writes to the model: the plan is built or gone, and these words have no row to be saved on.
 *  `why` 'retired' lists the words of a plan that went away BEFORE the plan on screen (they were moved out of the rows' scope when this plan arrived, so they are never shown on its rows): one quiet line,
 *  the words behind it only when the person opens it. */
export function WordsKeptAfterBuild({ modelKey, why = 'built' }: { modelKey: string; why?: 'built' | 'gone' | 'retired' }) {
  useHeldDrafts(modelKey);   // re-render when a row gains or loses a draft
  const [open, setOpen] = useState(false);
  const prefix = why === 'retired' ? RETIRED_PREFIX : 'startplan|';
  const kept = sharedDrafts.scopes(modelKey, prefix);
  if (!modelKey || kept.length === 0) return null;
  const rows = kept.map((e) => <div key={e.scope} className="sp-editor-acts" data-testid="sp-kept-row">
    <span><b>{e.label || M.COPY.orphanLine}</b>: <code>{e.fields.map((f) => (typeof f.draft.value === 'string' ? f.draft.value : JSON.stringify(f.draft.value))).join(' / ').slice(0, 120)}</code></span>
    <button type="button" className="sem-btn sem-btn-sm" data-testid="sp-kept-copy" onClick={() => void copyText(wordsAsText(e.fields.map((f) => ({ scope: e.scope, field: f.field, draft: f.draft }))))}>{M.COPY.copyWords}</button>
    <button type="button" className="sem-btn sem-btn-sm" data-testid="sp-kept-discard" onClick={() => sharedDrafts.clearScope(modelKey, e.scope)}>{M.COPY.discardWords}</button>
  </div>);
  if (why === 'retired') return <div className="sp-note" role="status" data-testid="sp-kept-retired">
    <button type="button" className="sem-btn sem-btn-sm sem-btn-quiet" aria-expanded={open} data-testid="sp-kept-retired-toggle" onClick={() => setOpen((o) => !o)}>
      {kept.length === 1 ? M.COPY.keptRetiredOne : M.COPY.keptRetiredMany(kept.length)}</button>
    {open && <><span> {M.COPY.keptRetiredLead}</span>{rows}</>}
  </div>;
  return <div className="sp-note sp-note-warn" role="status" data-testid={why === 'built' ? 'sp-kept-after-build' : 'sp-kept-gone'}>
    <span><b>{M.COPY.keptAfterBuildTitle}</b> {why === 'built' ? M.COPY.keptAfterBuildLead : M.COPY.keptGoneLead}</span>
    {rows}
  </div>;
}

/** A built plan, as a band over the ordinary Plan (K25): what was built, how to take it back, and a way to open the whole plan. The model's own rows,
 *  its grades and Run the plan are on the page under it, so nothing waits behind "Discard this plan". */
export function BuiltPlanBand({ view, onOpen }: { view: M.StartPlanView; onOpen: () => void }) {
  return <div className="sp-note sp-note-good sp-band" role="status" data-testid="sp-built-band">
    <span>{M.builtBandWords(view)}</span>
    <button type="button" className="sem-btn sem-btn-sm" data-testid="sp-built-open" onClick={onOpen}>{M.COPY.seeBuilt}</button>
  </div>;
}

export function StartPlanBody(p: BodyProps) {
  const { view, pro } = p;
  const tier: Tier = { pro };
  const [filter, setFilter] = useState('all');
  const [openRow, setOpenRow] = useState<string | null>(null);
  const [closedSec, setClosedSec] = useState<Record<string, boolean>>({});
  const [grpOpen, setGrpOpen] = useState<Record<string, boolean>>({});
  const [colsOpen, setColsOpen] = useState<Record<string, boolean>>({});   // the column lists are folded until the person opens one
  const [confirmDiscard, setConfirmDiscard] = useState(false);
  const lines = useMemo(() => M.linesOf(view), [view]);
  const rowIds = useMemo(() => [...M.rowNames(view).keys()], [view]);   // every row a typed draft can be held on: a draft whose row is not among them is asked about
  const graph = useMemo(() => M.linkGraph(view), [view]);
  const tabs = M.filterTabs(view, tier, p.changed);
  const totals = M.totalsOf(view, tier);
  const acts = M.actionsOf(view, tier, p.prev);
  const approval = M.approvalOf(view, p.prev);
  const primary = M.primaryAction(view, tier, p.prev);
  const note = M.decideNote(view, tier);
  const words = M.wordsLine(view);
  const grade = M.gradeOf(view, tier);
  const foot = M.footOf(view, tier, p.prev);
  const built = !!view.built;
  const dest = view.destination, src = view.source;
  const toBuild = M.buildCountOf(view), held = M.heldCountOf(view);
  const busy = !!p.busy || !!p.stale;
  // The typed-and-unsaved words of this model, by file (never the session id). They outlive any editor, so a row says so while its editor is shut.
  const modelKey = planModelKey(view);
  const draftedIds = useHeldDrafts(modelKey);
  // A build leaves out words that are typed and not saved. Before it runs, the rows that hold them are named and the person chooses (join 11, A3).
  const [ask, setAsk] = useState<{ go: () => void } | null>(null);
  // The rows the plan still shows are named as it shows them; a draft whose row is gone is named by the label it was kept under (F8: it is the person's words, so the build asks about it too).
  const keptLabels = new Map(sharedDrafts.scopes(modelKey, 'startplan|').flatMap((e) => (e.label ? [[e.scope.slice('startplan|'.length), e.label] as [string, string]] : [])));
  const unsavedNames = M.unsavedRowNames(view, draftedIds, keptLabels);
  const askBeforeBuild = (go: () => void) => () => { if (unsavedNames.length) setAsk({ go }); else go(); };
  // Save them first opens the first row that holds words (a line, or the column list of its table), and says so: the person saves them there (join 11, W4).
  const [opened, setOpened] = useState<string | null>(null);
  const [movedWords, setMovedWords] = useState(0);   // bumped when typed words are moved onto another row: the open editors read them again
  const saveFirst = () => {
    setAsk(null);
    const where = M.firstHeldRow(view, draftedIds);
    if (!where) { setOpened(M.COPY.heldGone); return; }
    setFilter('all'); setClosedSec((c) => ({ ...c, [where.section]: false })); setGrpOpen((g) => ({ ...g, [`${where.section}/${where.table}`]: true }));
    if (where.column) { setColsOpen((c) => ({ ...c, [where.table]: true })); setOpenRow(null); } else setOpenRow(where.id);
    setOpened(M.COPY.openedHeld(where.name, where.count - 1));
  };
  // The tables the plan BUILDS: the ones whose own line is kept. A table left out stays listed (so it can be put back) but is not a table of the model
  // (UAT-J7-S2-012: the completed page said 11 tables beside a model of 2).
  const tablesNamed = (view.sections ?? []).find((s) => s.id === 'tables')?.groups.filter((g) => g.table && (g.lines ?? []).some((l) => l.item?.kind === 'include_table' && l.kept)).length
    ?? new Set((view.sections ?? []).flatMap((s) => (s.groups ?? []).map((g) => g.table).filter(Boolean))).size;
  const left = lines.filter((l) => !l.kept).length;
  const pct = (n: number) => totals.all ? `${(n / totals.all * 100).toFixed(1)}%` : '0%';

  // A built plan offers no approve and no build: only Discard, at the foot.
  const actionButtons = (where: 'head' | 'foot') => built ? null : <>
    {primary === 'approve_and_build'
      ? <>
        <button type="button" className="sem-btn sem-btn-quiet" disabled={busy || !acts.approve.enabled} title={acts.approve.enabled ? M.COPY.approveOnlyTip : acts.approve.why ?? undefined}
          data-testid={`sp-approve-${where}`} onClick={p.onApprove}>{approval.state === 'stale' ? M.COPY.approveAgain : M.COPY.approveOnly}</button>
        <button type="button" className="sem-btn sem-btn-primary" disabled={busy || !acts.approveAndBuild.enabled}
          title={acts.approveAndBuild.enabled ? M.COPY.approveAndBuildTip : acts.approveAndBuild.why ?? undefined}
          data-testid={`sp-approve-build-${where}`} onClick={askBeforeBuild(p.onApproveAndBuild)}>{M.COPY.approveAndBuild}</button>
      </>
      : <button type="button" className="sem-btn sem-btn-primary" disabled={busy || !acts.build.enabled}
        title={acts.build.enabled ? M.COPY.buildTip : acts.build.why ?? undefined} data-testid={`sp-build-${where}`} onClick={askBeforeBuild(p.onBuild)}>{M.COPY.build}</button>}
  </>;

  return <div className="sp-page" data-testid="sp-page" data-pro={pro ? 'yes' : 'no'} data-decide={view.decide?.state ?? 'off'} data-approval={approval.state} data-built={built ? 'yes' : 'no'}>
    <header className="sp-head">
      <div className="sp-head-l">
        <h2>{dest?.name || 'New model'} <span>{built ? 'built from this plan' : 'the model to build'}</span></h2>
        <p>From <b>{src?.database}</b> (Fabric SQL){src?.server ? <>, {src.server}</> : null}. {built ? M.COPY.builtNote(dest?.name || 'The model') : dest?.folder ? M.COPY.emptyNote(dest.folder) : null}</p>
      </div>
      <div className="sp-head-r">
        {p.onAskAssistant && !built && <button type="button" className="sem-btn" data-testid="sp-ask" title={M.COPY.askAssistantTip} onClick={p.onAskAssistant}>{M.COPY.askAssistant}</button>}
        {built && p.onClose && <button type="button" className="sem-btn" data-testid="sp-built-close" onClick={p.onClose}>{M.COPY.backToPlan}</button>}
        {actionButtons('head')}
      </div>
    </header>

    {!built && <StartNoticeLine planId={view.id} />}
    {!pro && <div className="sp-note" data-testid="sp-free-note"><span><b>{M.COPY.freeNotice.slice(0, M.COPY.freeNotice.indexOf('.') + 1)}</b>{M.COPY.freeNotice.slice(M.COPY.freeNotice.indexOf('.') + 1)}</span></div>}
    {note && <div className={`sp-note sp-note-${note.tone}`} role="status" data-testid="sp-decide-note" data-tone={note.tone}>
      <span>{note.text}</span>
      {note.tone === 'busy' && (view.decide?.progress ?? []).length > 0 && (() => {
        const pr = view.decide!.progress!, asked = pr.reduce((a, f) => a + f.asked, 0), tot = asked + pr.reduce((a, f) => a + f.remaining, 0);
        return <span className="sp-bar" aria-hidden="true"><i style={{ width: `${tot ? Math.round(asked / tot * 100) : 0}%` }} /></span>;
      })()}
    </div>}
    {p.building && <div className="sp-note sp-note-busy" role="status" aria-live="polite" data-testid="sp-building">
      <span>{M.COPY.building(dest?.name || 'the model')}</span>
      <span className="sp-bar sp-bar-wait" aria-hidden="true"><i /></span>
    </div>}
    {!built && approval.state !== 'none' && <div className={`sp-note ${approval.state === 'stale' ? 'sp-note-warn' : 'sp-note-good'}`} role="status" data-testid="sp-approval">{approval.text}</div>}
    {p.stale && <div className="sp-note sp-note-warn" role="status" data-testid="sp-stale" data-stale={p.stale}>
      <span>{p.stale === 'down' ? (draftedIds.size ? M.COPY.engineRestarting : M.COPY.engineRestartingNoDraft) : M.COPY.readFailed + (draftedIds.size ? M.COPY.readFailedDraft : '')}
        {p.stale === 'failed' && p.onReread && <> <button type="button" className="plan-link" data-testid="sp-stale-retry" onClick={p.onReread}>{M.COPY.tryAgain}</button></>}</span>
      {p.stale === 'down' && <button type="button" className="sem-btn sem-btn-sm" data-testid="sp-stale-restart" title={M.COPY.restartEngineTip} onClick={() => runHostCommand('semanticus.restartEngine')}>{M.COPY.restartEngine}</button>}
    </div>}
    {opened && <div className="sp-note" role="status" data-testid="sp-opened-held"><span>{opened}</span><button type="button" className="sem-btn sem-btn-sm sem-btn-quiet" onClick={() => setOpened(null)}>Close</button></div>}
    {/* Words typed into a line that is not in the plan now. A line is known by its name, so a rename by the assistant looks the same as a removal: the words are kept and the person is asked (join 11, W3). */}
    <OrphanDrafts prefix="startplan|" live={p.stale || view.built ? null : rowIds} what="plan change" nameOf={() => 'A change in the plan'}
      targetsFor={(fields) => M.moveTargets(view, fields)} onMoved={() => setMovedWords((n) => n + 1)} />
    {built && <WordsKeptAfterBuild modelKey={modelKey} />}
    <WordsKeptAfterBuild modelKey={modelKey} why="retired" />
    {ask && <UnsavedWordsAsk names={unsavedNames} busy={busy} onSaveFirst={saveFirst} onBuildWithout={() => { const go = ask.go; setAsk(null); go(); }} />}
    {p.error && <div className="sp-note sp-note-bad" role="alert" data-testid="sp-error"><span><b>Nothing was changed.</b> {p.error}</span></div>}
    {p.sentence && <div className="sp-note" role="status" data-testid="sp-said">{p.sentence}</div>}

    <section className="sem-card sp-strip" data-testid="sp-strip" aria-label="The plan at a glance">
      <div className="sp-cell">
        <span className="sp-cell-k">{M.COPY.linesInPlan}</span>
        <span className="sp-cell-v sp-num">{totals.all}<small>{built ? `${toBuild} built` : !pro && totals.default ? `${totals.default} on plain defaults` : 'choices in the plan'}</small></span>
        <span className="sp-gauge" aria-hidden="true">
          {(['jev', 'default', 'rule', 'assistant', 'you'] as const).map((k) => totals[k] > 0 && <i key={k} className={`sp-g-${k}`} style={{ width: pct(totals[k]) }} />)}
        </span>
        <span className="sp-keys" data-testid="sp-chosen-by"><span>Chosen by</span>{M.chosenByParts(totals, pro).map((c) => <span key={c.key}>{c.text}</span>)}</span>
      </div>
      <div className="sp-cell">
        <span className="sp-cell-k">{M.COPY.wordsTitle}</span>
        <span className="sp-cell-v sp-num">{words.total ? <>{words.done}<small>of {words.total}</small></> : <small>None yet</small>}</span>
        <span className="sp-gauge" aria-hidden="true"><i className="sp-g-words" style={{ width: words.total ? `${words.done / words.total * 100}%` : '0%' }} /></span>
        <span className="sp-keys"><Who mark={{ key: 'assistant', label: 'Your assistant' }} /></span>
      </div>
      <div className="sp-cell" data-testid="sp-grade" data-provisional={grade.provisional ? 'yes' : 'no'}>
        <span className="sp-cell-k">{M.COPY.gradeAI}</span>
        <span className="sp-cell-v sp-num">
          {grade.now != null && <><span className={bandClass(grade.now)} data-testid="sp-grade-now">{grade.now}</span><small>now</small></>}
          {grade.undone && grade.now == null && <><span className="sp-after sp-provisional" data-testid="sp-grade-undone">{M.COPY.gradeUndone}</span>{grade.after != null && <small>to</small>}</>}
          {grade.now != null && grade.after != null && <small>to</small>}
          {grade.after != null && <><span className={grade.provisional ? 'sp-after sp-provisional' : `sp-after ${bandClass(grade.after)}`}>{grade.after}</span>
            <small>{grade.afterNumber != null && grade.afterNumber !== Number(grade.after) ? `${grade.afterNumber} ` : ''}{built ? M.COPY.gradeBuilt : M.COPY.gradeAfter}</small></>}
          {grade.now == null && grade.after == null && !grade.undone && <small>{M.COPY.gradeUnknown}</small>}</span>
        {grade.note && <span className="sp-grade-note" data-testid="sp-grade-provisional">{grade.note}</span>}
        <span className="sp-keys"><span>{built ? M.COPY.gradeBuiltScored : grade.undone ? M.COPY.gradeUndoneKey : view.grades?.now == null ? M.COPY.gradeNotScored : `${M.COPY.gradeNow} score ${view.grades.now}`}</span></span>
      </div>
      {/* Best practice after the build. The count of findings that stay open is the one the preview's own best practice scan gives (the errors and
          warnings the letter weighs), never a count of the plan's own Best practice lines, which are not the scan (plan statement check 3, defect 3).
          When the engine sends no count, the plan says the letter it does know. */}
      <div className="sp-cell" data-testid="sp-bp">
        <span className="sp-cell-k">After the build</span>
        {grade.bestPracticeOpen != null
          ? <><span className="sp-cell-v sp-num">{grade.bestPracticeOpen}<small>{grade.bestPracticeOpen === 1 ? 'best practice problem stays open' : 'best practice problems stay open'}</small></span>
            <span className="sp-keys">{grade.bestPractice && <span data-testid="sp-grade-bp">{M.COPY.bpLetter(grade.bestPractice)}{built ? '' : ` ${M.COPY.gradeAfter}`}</span>}
              <span>{pro ? 'Each becomes a task on the Plan' : 'Best practice fixes are one at a time, on the Plan'}</span></span></>
          : <><span className="sp-cell-v sp-num">{grade.bestPractice
              ? <><span data-testid="sp-grade-bp">{M.COPY.bpLetter(grade.bestPractice)}</span><small>{built ? M.COPY.gradeBuilt : M.COPY.gradeAfter}</small></>
              : <small>{M.COPY.bpUnknown}</small>}</span>
            <span className="sp-keys">{M.COPY.bpOneAtATime}</span></>}
      </div>
    </section>

    <div className="sp-facts" data-testid="sp-facts">
      <span><b className="sp-num">{tablesNamed}</b> tables</span>
      {graph && <span><b className="sp-num">{graph.edges.length}</b> links, {graph.edges.filter((e) => e.active).length} on</span>}
      <span><b className="sp-num">{toBuild}</b> {built ? 'changes built' : 'changes ready to build'}</span>
      {held > 0 && <span data-testid="sp-held-count"><b className="sp-num">{held}</b> {M.COPY.heldFact}</span>}
      {left > 0 && <span><b className="sp-num">{left}</b> changes left out</span>}
    </div>

    <NeedsBand view={view} tier={tier} busy={busy} onNeed={p.onNeed} />

    <div className="sp-filterbar" data-testid="sp-filters">
      <div className="sp-filters" role="group" aria-label="Show changes by who decided them">
        {tabs.map((t) => <button key={t.id} type="button" className="sp-filter" aria-pressed={filter === t.id} data-testid={`sp-filter-${t.id}`} title={t.id === 'changed' ? M.COPY.changedTip : undefined} onClick={() => setFilter(t.id)}>{t.label}<b className="sp-num">{t.count}</b></button>)}
      </div>
      <span className="sp-hint">{M.COPY.filterHint}</span>
    </div>

    <div className="sp-list" data-testid="sp-list">
      {M.SECTION_ORDER.map((id) => {
        const sec = (view.sections ?? []).find((s) => s.id === id);
        if (!sec) return null;
        const secLines = (sec.groups ?? []).flatMap((g) => g.lines ?? []);
        const closed = !!closedSec[id];
        if (filter !== 'all' && !secLines.some((l) => M.lineMatches(filter, l, tier, p.changed))) return null;
        const needN = secLines.filter((l) => M.lineState(l, view, tier).tone === 'need').length;
        return <div className="sp-sec" key={id} data-testid={`sp-section-${id}`} data-section={id}>
          <div className="sp-sec-h" aria-expanded={!closed} {...pressable(() => setClosedSec((c) => ({ ...c, [id]: !c[id] })))}>
            <span className="sp-car" aria-hidden="true">{closed ? '▸' : '▾'}</span>
            <b>{sec.title || M.COPY.sectionTitles[id]}</b>
            <span className="sp-sec-q">{M.sectionBlurb(sec)}</span>
            <span className="sp-sec-r">{needN > 0 && <span className="sp-need-n">{needN} {needN === 1 ? 'needs' : 'need'} you</span>}<span>{tallyOf(secLines, tier)}</span></span>
          </div>
          {!closed && <>
            {id === 'links' && graph && filter === 'all' && <LinkDrawing graph={graph} />}
            {(sec.groups ?? []).map((g, gi) => {
              const gl = (g.lines ?? []).filter((l) => M.lineMatches(filter, l, tier, p.changed));
              if (!gl.length) return null;
              const key = `${id}/${g.table ?? ''}`;
              // A section that fits on a screen or two opens every table; a wide source opens only the tables that need the person.
              const dflt = !g.table || secLines.length <= M.OPEN_ALL_UP_TO || (g.lines ?? []).some((l) => M.lineState(l, view, tier).tone === 'need');
              const open = filter !== 'all' ? true : grpOpen[key] ?? dflt;
              return <div key={`${key}/${gi}`} data-group={g.table ?? ''}>
                {g.table && <div className="sp-grp" aria-expanded={open} data-testid={`sp-group-${id}-${g.table}`} {...pressable(() => setGrpOpen((s) => ({ ...s, [key]: !open })))}>
                  <span className="sp-car" aria-hidden="true">{open ? '▾' : '▸'}</span><b>{g.table}</b>
                  {g.sourceName && g.sourceName !== g.table && <span className="sp-grp-src" title="The name in the source">{g.sourceName}</span>}
                  <span className="sp-grp-r sp-num">{gl.length} {gl.length === 1 ? 'change' : 'changes'}</span>
                </div>}
                {g.table && id === 'tables' && (() => {
                  const sample = M.sampleControlOf(view, g, tier);
                  // The choice the grant dialog has, kept after the grant: it narrows the stored grant, as the assistant's set_table_sample does.
                  return sample && <label className="sp-sample" title={START_CARD.tableSampleHint} data-testid="sp-sample" data-table={sample.name} data-allowed={sample.allowed ? 'yes' : 'no'}>
                    <input type="checkbox" checked={sample.allowed} disabled={busy} data-testid="sp-sample-box" onChange={() => p.onEdit(M.tableSampleEdit(sample.name, !sample.allowed))} />
                    <span>{START_CARD.tableSample}</span></label>;
                })()}
                {open && gl.map((l) => {
                  const lid = l.item?.id ?? '';
                  const wk = M.lineControls(l).words?.kind;
                  return <div key={lid} className={openRow === lid ? 'sp-item sp-item-open' : 'sp-item'}>
                    <LineRow line={l} view={view} tier={tier} open={openRow === lid} changed={p.changed.has(lid)} drafted={(!!wk && draftedIds.has(heldId(lid, wk))) || draftedIds.has(heldId(lid, 'rename'))} onToggle={() => setOpenRow((o) => (o === lid ? null : lid))} />
                    {openRow === lid && <LineDetail key={`${lid}:${movedWords}`} line={l} view={view} tier={tier} busy={busy} away={!!p.stale} modelKey={modelKey} onEdit={p.onEdit} onWords={p.onWords} onKeepName={p.onKeepName} />}
                  </div>;
                })}
                {open && g.table && (g.columns ?? []).length > 0 && filter === 'all' && id === 'tables' &&
                  <ColumnList key={`${g.table}:${movedWords}`} columns={g.columns!} open={!!colsOpen[g.table]} busy={busy} modelKey={modelKey} onToggle={() => setColsOpen((c) => ({ ...c, [g.table!]: !c[g.table!] }))} onEdit={p.onEdit} onKeepName={p.onKeepName} />}
              </div>;
            })}
          </>}
        </div>;
      })}
    </div>

    <div className="sp-foot" data-testid="sp-foot">
      <span className="sp-foot-t" data-foot={foot.state}><b>{foot.lead}</b> {foot.rest}</span>
      <span className="sp-foot-r">
        {confirmDiscard
          ? <><span>Discard this plan?</span>
            <button type="button" className="sem-btn sem-btn-sm" disabled={busy} data-testid="sp-discard-yes" onClick={() => { setConfirmDiscard(false); p.onDiscard(); }}>Yes, discard</button>
            <button type="button" className="sem-btn sem-btn-sm" onClick={() => setConfirmDiscard(false)}>Keep it</button></>
          : <button type="button" className={built ? 'sem-btn sem-btn-sm' : 'sem-btn sem-btn-sm sem-btn-quiet'} disabled={busy} title={built ? M.COPY.discardBuiltTip : M.COPY.discardTip} data-testid="sp-discard" onClick={() => setConfirmDiscard(true)}>{M.COPY.discard}</button>}
        {actionButtons('foot')}
      </span>
    </div>
    <div className="sp-source" data-testid="sp-source">
      <span>Source {src?.database} (Fabric SQL)</span><span>{built ? M.COPY.sourceBuilt : M.COPY.sourceNotBuilt}</span>
    </div>
    <div className="sp-legend" aria-label="What the marks mean">
      {pro && <Who mark={{ key: 'jev', label: M.COPY.legendJev }} />}<Who mark={{ key: 'rule', label: M.COPY.legendRule }} />
      <Who mark={{ key: 'you', label: M.COPY.legendYou }} /><Who mark={{ key: 'assistant', label: M.COPY.legendAssistant }} />
      {!pro && totals.default > 0 && <Who mark={{ key: 'default', label: M.COPY.legendDefault }} />}
    </div>
  </div>;
}

// ---------------------------------------------------------------- the container: reads the view, makes the calls

const messageOf = (e: unknown): string => (e instanceof Error ? e.message : String(e));
/** The host's own answer while it holds no engine (App.tsx ENGINE_NOT_CONNECTED). */
const ENGINE_NOT_CONNECTED = 'Engine not connected.';
const isEngineAway = (e: unknown): boolean => messageOf(e) === ENGINE_NOT_CONNECTED;

/** The scope the words of a plan that went away are moved to when another plan arrives (listed by WordsKeptAfterBuild, why 'retired'). */
const RETIRED_PREFIX = 'startplan-gone|';
function retireGoneWords() {
  const m = draftModel();
  if (!m) return;
  for (const e of sharedDrafts.scopes(m, 'startplan|')) sharedDrafts.moveScope(m, e.scope, RETIRED_PREFIX + e.scope.slice('startplan|'.length));
}

/** The plan, read when the model or the engine says it moved. An engine without the verb answers "no plan": an older
 *  engine beside this page must never turn the Plan into an error. */
export function useStartPlan(sessionId: string, revision: number | undefined, changeNonce: number) {
  const [view, setView] = useState<M.StartPlanView | null>(null);
  const [loaded, setLoaded] = useState(false);
  // Why the plan on screen may not be the engine's now: its engine is not there ('down'), or the last read failed ('failed'). A failed read keeps the plan
  // that was on screen, read-only, and never empties the page: whatever is typed into it is the person's, and a plan that cannot be read for a moment is not a plan that is gone (D1c).
  const [stale, setStale] = useState<'down' | 'failed' | null>(null);
  const gen = useRef(0);
  const sawNone = useRef(false);   // the last read found no plan
  const read = useCallback(() => {
    const mine = ++gen.current;
    return rpc<M.StartPlanView>('getStartPlan').then(
      (v) => {
        if (mine !== gen.current) return;
        // The engine answered and there is no plan: it was discarded (by the assistant, another window or this one). The words typed for its lines are NOT deleted here (D3): only the person's
        // own Discard (onDiscard) ends them. They stay held, and the Plan lists them (WordsKeptAfterBuild, why 'gone') with Copy and Discard, because the page promised to keep them until the person discards them.
        // A plan is back after one went away (a read found none): the words kept for the plan that went are not THIS plan's. They move out of the rows' scope to a quiet list, so they are never
        // shown on, or saved into, a line of another plan (uatj8 D1), and they are still the person's to Copy or Discard. They are moved when the new plan arrives, never when the old one goes.
        if (v && v.exists) { if (sawNone.current) { sawNone.current = false; retireGoneWords(); } } else sawNone.current = true;
        setView(v && v.exists ? v : null); setStale(null); setLoaded(true);
      },
      (e: unknown) => { if (mine === gen.current) { setStale(isEngineAway(e) ? 'down' : 'failed'); setLoaded(true); } },
    );
  }, []);
  useEffect(() => { void read(); }, [read, sessionId, revision, changeNonce]);
  useEffect(() => {
    let timer: ReturnType<typeof setTimeout> | undefined;
    const off = onStartPlanChange(() => { if (timer) clearTimeout(timer); timer = setTimeout(() => { void read(); }, 150); });
    // The host says the engine is gone before any read fails, and says it is back: the page reads the plan again then.
    const offEngine = onEngineState((connected) => { if (connected) void read(); else setStale('down'); });
    // A reconnect abandons every request in flight (bridge.ts), so a read cut short that way is read again.
    const offReconnect = onReconnect(() => { void read(); });
    return () => { off(); offEngine(); offReconnect(); if (timer) clearTimeout(timer); };
  }, [read]);
  // With no plan on screen there is nothing to keep, so nothing is stale.
  return { view, setView, loaded, read, stale: view ? stale : null };
}

/** The card's grant puts the plan it returns on the page too, and so does each edit it makes (the card is a window onto the same plan). */
function withViews(card: M.GrantCard, onView: (v: M.StartPlanView | null) => void): M.GrantCard {
  return {
    ...card,
    grant: async () => { const v = await card.grant(); onView(v); return v; },
    edit: card.edit ? async (e) => withViews(await card.edit!(e), onView) : undefined,
  };
}

const BUILT_SAID = 'Built in one step. One undo takes it back.';

const ASSISTANT_PROMPT = 'Use the Semanticus start plan tools to write the words for my new model: read what is open with get_start_words, then write names, descriptions and synonyms with write_start_words. Do not approve or build it; I will do that.';

export function StartPlanPanel({ view, onView, onBuilt, onReload, pro, stale, onClose }: {
  view: M.StartPlanView; onView: (v: M.StartPlanView | null) => void;
  /** The plan on screen is the last one read (the engine is not there, or the read failed). */
  stale?: 'down' | 'failed' | null;
  /** The build ran. `kept` is true when the engine answered with the built plan, which the page now shows; false when an older
   *  engine answered with no plan, so the ordinary Plan takes the page back and says it. */
  onBuilt: (sentence: string, kept: boolean) => void;
  /** Read the plan again. A refused call means the plan is not what the page shows, so the page shows the plan as it is now. */
  onReload: () => void; pro: boolean;
  /** Set when a built plan is opened from the Plan's band: the page offers the way back to the Plan. */
  onClose?: () => void;
}) {
  const [busy, setBusy] = useState<string | null>(null);
  const [building, setBuilding] = useState(false);
  const [error, setError] = useState<string | null>(null);
  // What the page last said, tied to the plan it was said about: once the plan moves, a sentence about the old one is gone.
  const [said, setSaid] = useState<{ text: string; digest: string | undefined; whileBuilt?: boolean } | null>(null);
  // "Built in one step" is true only while the plan is built: after an undo of the build the same plan (the same digest) is a plan again, and the sentence is gone (join 8, K15).
  const sentence = said && said.digest === view.digest && (!said.whileBuilt || !!view.built) ? said.text : null;
  const prev = useRef<M.StartPlanView | null>(null);
  const [changed, setChanged] = useState<Set<string>>(new Set());
  const [shownPrev, setShownPrev] = useState<M.StartPlanView | null>(null);
  // What moved since the person's own last change: the Changed filter and the marks on rows. The person's change starts it
  // again with what that change moved; changes from elsewhere (each batch of the assistant's words, a round of Jev) add to it.
  const mine = useRef(false);
  useEffect(() => {
    if (prev.current && prev.current.digest !== view.digest) {
      const moved = M.changedLineIds(prev.current, view), reset = mine.current;
      mine.current = false;
      setChanged((c) => M.mergeChanged(c, moved, reset)); setShownPrev(prev.current);
    }
    prev.current = view;
  }, [view]);

  // Typed words end with the plan that took them: a built plan has no lines left to edit. A line that is not there any more does NOT take its words: it may have been
  // renamed (a line is known by its name), so the words are kept and the plan asks (OrphanDrafts, join 11, W3). Each held draft is named as the page shows its row, so it can still be named then.
  // Only a plan that was read fresh decides: a plan kept on screen through an outage may be older than the engine's.
  // A draft typed AFTER the plan was last read gains its name too (the effect runs again when a row gains or loses a draft): the list of words kept for a plan that is gone names its rows.
  const heldRows = [...useHeldDrafts(planModelKey(view))].sort().join('|');
  useEffect(() => {
    if (stale) return;
    const m = planModelKey(view);
    // A built plan keeps the words that were held when it was built (F14): the person chose "Build without them" or had none, and nothing here says they may be dropped. The built page lists them
    // (WordsKeptAfterBuild) with Copy and Discard; only that Discard, or the plan's own Discard, ends them.
    if (view.built) return;
    const names = M.rowNames(view);
    for (const e of sharedDrafts.scopes(m, 'startplan|')) { const name = names.get(e.scope.slice('startplan|'.length)); if (name) for (const f of e.fields) sharedDrafts.setLabel(m, e.scope, f.field, name); }
  }, [view, stale, heldRows]);

  // True when the engine took the call. The editors read it: a refused or failed edit keeps the person's typed words (R-B).
  const run = useCallback(async (key: string, fn: () => Promise<void>): Promise<boolean> => {
    setBusy(key); setError(null); setSaid(null); mine.current = true;
    try { await fn(); return true; } catch (e) { setError(messageOf(e)); onReload(); return false; } finally { setBusy(null); }
  }, [onReload]);

  const onEdit = useCallback((e: M.StartEdit): Promise<boolean> => run('edit', async () => {
    onView(await rpc<M.StartPlanView>('editStartPlan', e, 'human'));
  }), [run, onView]);

  // The person's edit of the words on a line: the same write the assistant's door makes, from the person's own door, so the line becomes theirs.
  // The answer goes back to the editor, which stays open on a refusal: a conflict (newer words arrived) is shown there beside the draft, and any other refusal is said there too.
  const onWords = useCallback(async (words: M.Words, text: string): Promise<M.WordsSaveResult> => {
    setBusy('words'); setError(null); setSaid(null); mine.current = true;
    try {
      const r = await rpc<{ written?: number; refused?: string[]; view?: M.StartPlanView }>('writeStartWords', ...M.wordsCall(words, text));
      if (r?.view) onView(r.view);
      return M.wordsSaveResult(r, view);
    } catch (e) { onReload(); return { saved: false, moved: false, said: isEngineAway(e) ? M.COPY.engineRestarting : messageOf(e) }; }
    finally { setBusy(null); }
  }, [onView, onReload, view]);

  // The person keeps a name the naming rules flag: the assistant's own keep_name, written from the person's door.
  const onKeepName = useCallback((ref: string) => void run('words', async () => {
    const r = await rpc<{ written?: number; refused?: string[]; view?: M.StartPlanView }>('writeStartWords', ...M.keepNameCall(ref));
    if (r?.view) onView(r.view);
    if (!r?.written) throw new Error((r?.refused ?? []).map((x) => M.shownRefusal(x, view)).join(' ') || 'The name was not kept.');
  }), [run, onView, view]);

  const onNeed = useCallback((need: M.StartNeed, option: string) => void run('need', async () => {
    const a = M.needAction(view, need, option);
    if (a.kind === 'edit') { onView(await rpc<M.StartPlanView>('editStartPlan', ...a.args)); return; }
    // Allow: the model's own permission first (the engine refuses the grant without it), through the start plan's card when
    // it is not current, then the grant for the digest the person looked at.
    const r = await M.allowJev(view, need, {
      getConsent: () => rpc<ConsentInfoW>('getConsent'),
      grant: (digest) => rpc<M.StartPlanView>('grantStartDisclosure', digest, 'human'),
      // The card's two choices per table are edits of the plan; each answer is the plan after it, shown behind the card too.
      edit: async (e) => { const v = await rpc<M.StartPlanView>('editStartPlan', e, 'human'); onView(v); return v; },
      openCard: (plan, scope) => openStartPlanConsentCard(withViews(plan, onView), onReload, scope),
    });
    if (r !== 'card') onView(r);
  }), [run, view, onView, onReload]);

  // A build answers with the built plan: the page shows it (Built, the grades, Discard only) and says how to take it back.
  const builtThen = useCallback((v: M.StartPlanView | undefined) => {
    const kept = !!v?.exists && !!v.built;
    if (kept) { onView(v!); setSaid({ text: BUILT_SAID, digest: v!.digest, whileBuilt: true }); }
    onBuilt(BUILT_SAID, kept);
  }, [onView, onBuilt]);

  // The person's approval, for the digest THIS page showed. If the plan moved while they pressed, the engine refuses and
  // the page shows the plan again: nothing is approved on the strength of a plan the person did not look at.
  const approveThen = useCallback(async (andBuild: boolean) => {
    const seen = view;
    const approved = await rpc<M.StartPlanView>('approveStartPlan', ...M.approveArgs(seen));
    if (approved?.approval?.digest !== seen.digest) {
      if (approved) onView(approved);
      throw new Error('The plan changed while you approved it, so nothing was approved. Look at it again, then approve.');
    }
    onView(approved);
    if (!andBuild) return;   // the approval note says the rest, and stays while the approval stands
    setBuilding(true);
    let r: { built?: boolean; refused?: string | null; view?: M.StartPlanView };
    try { r = await rpc<{ built?: boolean; refused?: string | null; view?: M.StartPlanView }>('buildStartPlan', ...M.buildArgs(seen)); } finally { setBuilding(false); }
    if (r?.built) { builtThen(r.view); return; }
    if (r?.view) onView(r.view);
    throw new Error(r?.refused || 'The build did not run.');
  }, [view, onView, builtThen]);

  const onApprove = useCallback(() => void run('approve', () => approveThen(false)), [run, approveThen]);
  const onApproveAndBuild = useCallback(() => void run('approve', () => approveThen(true)), [run, approveThen]);
  const onBuild = useCallback(() => void run('build', async () => {
    setBuilding(true);
    let r: { built?: boolean; refused?: string | null; view?: M.StartPlanView };
    try { r = await rpc<{ built?: boolean; refused?: string | null; view?: M.StartPlanView }>('buildStartPlan', ...M.buildArgs(view)); } finally { setBuilding(false); }
    if (r?.built) { builtThen(r.view); return; }
    if (r?.view) onView(r.view);
    throw new Error(r?.refused || 'The build did not run.');
  }), [run, view, onView, builtThen]);
  const onDiscard = useCallback(() => void run('discard', async () => {
    await rpc('discardStartPlan', 'human');
    draftStore.clearModel(planModelKey(view));   // the plan's lines are gone, so words typed for them have nowhere to go
    onView(null);
  }), [run, onView, view]);
  const onAsk = useCallback(() => { void copyText(ASSISTANT_PROMPT).then(() => setSaid({ text: 'Copied. Paste it into your assistant. It writes the words; you approve.', digest: view.digest })); }, [view.digest]);

  return <StartPlanBody view={view} pro={pro} busy={busy} error={error} sentence={sentence} changed={changed} prev={shownPrev}
    onEdit={onEdit} onWords={onWords} onKeepName={onKeepName} onNeed={onNeed} onApprove={onApprove} onBuild={onBuild} onApproveAndBuild={onApproveAndBuild}
    onDiscard={onDiscard} onRetry={() => onView(view)} onAskAssistant={onAsk} stale={stale} onReread={onReload} building={building} onClose={onClose} />;
}

// ---------------------------------------------------------------- the start form

export type SavedSources = SqlSourceRecord[] | null | 'failed';

// What the person typed into the start form is held in the page's own state until a start they pressed succeeds or they discard it (join 11 solui, sweep fix 1, rule R-B).
// The form is swapped off the page when anything else starts a plan (the assistant, another window) or opens a model, and a start opens a NEW model, so these words are
// not one model's draft: they are held by the page, not in the per-model draft store. StartFormKept names them while the form is not on screen.
interface StartFormWords { picked: string; typing: boolean; server: string; database: string; name: string; folder: string }
const FORM_KEY = 'studio.startForm';
const formListeners = new Set<() => void>();
const typedInForm = (w: StartFormWords | null | undefined): w is StartFormWords => !!w && !!(w.server || w.database || w.name || w.folder);
function heldFormWords(): StartFormWords | null {
  try { const w = loadState<StartFormWords | null>(FORM_KEY, null); return typedInForm(w) ? w : null; } catch { return null; }
}
function holdFormWords(w: StartFormWords | null) {
  const next = typedInForm(w) ? w : null;
  if (JSON.stringify(next) === JSON.stringify(heldFormWords())) return;
  try { saveState(FORM_KEY, next); } catch { /* state unavailable: the words live while the form is open */ }
  for (const l of [...formListeners]) l();
}
/** End the held start form words: a start the person pressed succeeded, or they discarded them. */
export const clearStartFormWords = () => holdFormWords(null);
function useHeldFormWords(): StartFormWords | null {
  const [w, setW] = useState(heldFormWords);
  useEffect(() => { const sync = () => setW(heldFormWords()); sync(); formListeners.add(sync); return () => { formListeners.delete(sync); }; }, []);
  return w;
}

// The one sentence a start sends back beside its plan (a free name was taken: "Gold 2"). The answer carries it once, so it is held here for that plan and drawn on the plan page.
let heldNotice: { planId: string; text: string } | null = null;
const noticeListeners = new Set<() => void>();
export function holdStartNotice(v: M.StartPlanView | null | undefined) {
  heldNotice = v?.exists && v.id && v.notice ? { planId: v.id, text: v.notice } : null;
  for (const l of [...noticeListeners]) l();
}
/** One calm line under the plan's title: what the start did that the person did not type. */
export function StartNoticeLine({ planId }: { planId?: string }) {
  const [, tick] = useState(0);
  useEffect(() => { const f = () => tick((n) => n + 1); noticeListeners.add(f); return () => { noticeListeners.delete(f); }; }, []);
  if (!heldNotice || !planId || heldNotice.planId !== planId) return null;
  return <div className="sp-note" role="status" data-testid="sp-start-notice"><span>{heldNotice.text}</span></div>;
}

/** The start form's typed words while the form is not on the page (a start plan holds the page, or the person went back to the Plan), with Copy and Discard. They come back into
 *  the form when it opens again. Nothing here starts anything. */
export function StartFormKept({ onOpen }: { onOpen?: () => void }) {
  const w = useHeldFormWords();
  if (!w) return null;
  const parts = [w.server && `SQL endpoint ${w.server}`, w.database && `database ${w.database}`, w.name && `model name ${w.name}`, w.folder && `folder ${w.folder}`].filter(Boolean) as string[];
  return <div className="sp-note" role="status" data-testid="sp-form-kept">
    <span><b>Words you typed for a new model from a SQL source are kept.</b> Nothing was started with them. {parts.join(', ')}.</span>
    <span className="sp-editor-acts">
      {onOpen && <button type="button" className="sem-btn sem-btn-sm" data-testid="sp-form-kept-open" onClick={onOpen}>Open the form</button>}
      <button type="button" className="sem-btn sem-btn-sm" onClick={() => void copyText(parts.join('\n'))}>{M.COPY.copyWords}</button>
      <button type="button" className="sem-btn sem-btn-sm" data-testid="sp-form-kept-discard" onClick={clearStartFormWords}>Discard</button>
    </span>
  </div>;
}

export function StartForm({ sources, busy, error, onStart, onCancel, onAddOnConnections, onRetry, pro, needsDiscard = false, openModelSignIn }: {
  sources: SavedSources; busy: boolean; error: string | null;
  onStart: (r: M.StartRequest) => void;
  /** Ends the read in progress. The button shows only while the source is being read. */
  onCancel?: () => void; onAddOnConnections: () => void; onRetry?: () => void; pro: boolean;
  /** How the open model was opened (its connection's sign-in): a typed endpoint signs in the same way when no saved endpoint says otherwise. */
  openModelSignIn?: { authMode?: string | null; tenantId?: string | null } | null;
  /** The open model has unsaved changes and the person has not yet said to discard them: the form asks, in words. */
  needsDiscard?: boolean;
}) {
  const list = Array.isArray(sources) ? sources : [];
  const [held] = useState(heldFormWords);   // the words typed before the form was last swapped off the page
  const [picked, setPicked] = useState(held?.picked ?? '');
  const [typing, setTyping] = useState(held?.typing ?? false);
  const [typed, setTyped] = useState({ server: held?.server ?? '', database: held?.database ?? '' });
  const [name, setName] = useState(held?.name ?? '');
  const [folder, setFolder] = useState(held?.folder ?? '');
  useEffect(() => { holdFormWords({ picked, typing, server: typed.server, database: typed.database, name, folder }); }, [picked, typing, typed.server, typed.database, name, folder]);
  const [problem, setProblem] = useState<string | null>(null);
  const chosen = list.find((r) => r.id === picked) ?? list[0];
  const useTyped = typing || (Array.isArray(sources) && list.length === 0);
  // A typed endpoint signs in the way a saved one does (the first rule: the endpoint picked, then the first saved, then the open model's own way in).
  const rec: M.SqlRecord = useTyped ? M.typedEndpoint(typed, list, chosen, openModelSignIn) : chosen ?? {};
  const modelName = name.trim() || (rec.database ?? '').trim();
  const go = () => {
    const bad = M.startProblem(rec, modelName, folder);
    setProblem(bad);
    if (!bad) onStart(M.startRequest(rec, modelName, folder, 'import', needsDiscard));
  };
  const browse = () => { void pickWorkingCopyFolder().then((p) => { if (p) setFolder(p); }); };
  return <section className="sp-form" data-testid="sp-form">
    <h2>{M.COPY.formTitle}</h2>
    <p>{M.COPY.formLead}</p>
    <p>{pro ? M.COPY.formProNote : M.COPY.formFreeNote}</p>
    <div className="sp-field"><span>{M.COPY.formEndpoint}</span>
      {sources === null && <span className="sp-muted">{M.COPY.formReadingList}</span>}
      {sources === 'failed' && <span className="sp-muted" data-testid="sp-form-failed">{M.COPY.formListFailed}{onRetry && <> <button type="button" className="plan-link" onClick={onRetry}>{M.COPY.tryAgain}</button></>}</span>}
      {Array.isArray(sources) && list.length > 0 && !typing && <div className="sp-pick" role="radiogroup" aria-label="SQL endpoints from Connections">
        {list.map((r) => <label key={r.id}><input type="radio" name="sp-src" checked={chosen?.id === r.id} onChange={() => setPicked(r.id)} />
          <span><b>{r.name}</b> {r.server} {r.database}</span></label>)}
      </div>}
      {Array.isArray(sources) && list.length === 0 && <span className="sp-muted">{M.COPY.formNone}{' '}<button type="button" className="plan-link" data-testid="sp-form-add" onClick={onAddOnConnections}>{M.COPY.formAdd}</button></span>}
      {useTyped && <div className="sp-field-row">
        <input aria-label="SQL endpoint" placeholder="server.example.com" value={typed.server} onChange={(e) => setTyped({ ...typed, server: e.target.value })} />
        <input aria-label="Database" placeholder="Database" value={typed.database} onChange={(e) => setTyped({ ...typed, database: e.target.value })} />
      </div>}
      {Array.isArray(sources) && list.length > 0 && <button type="button" className="plan-link" onClick={() => setTyping((t) => !t)}>{typing ? 'Use a SQL endpoint from Connections' : 'Type an endpoint instead'}</button>}
    </div>
    <label className="sp-field"><span>{M.COPY.formName}</span>
      <input data-testid="sp-form-name" value={name} placeholder={(rec.database ?? '') || 'New model'} onChange={(e) => setName(e.target.value)} /></label>
    <div className="sp-field"><span>{M.COPY.formFolder}</span>
      <div className="sp-field-row">
        <input data-testid="sp-form-folder" aria-label={M.COPY.formFolder} value={folder} placeholder="/path/to/an/empty/folder" onChange={(e) => setFolder(e.target.value)} />
        <button type="button" className="sem-btn" data-testid="sp-form-browse" onClick={browse}>{M.COPY.formBrowse}</button>
      </div>
      <span className="sp-muted">{M.COPY.formFolderHint}</span></div>
    {needsDiscard && <div className="sp-note sp-note-warn" role="alert" data-testid="sp-form-unsaved"><span>{M.COPY.formUnsaved}</span></div>}
    {(problem || error) && <div className="sp-note sp-note-bad" role="alert" data-testid="sp-form-error"><span>{problem || error}</span></div>}
    <div className="sp-editor-acts"><button type="button" className="sem-btn sem-btn-primary" disabled={busy} data-testid="sp-form-start" onClick={go}>{busy ? M.COPY.formReading : needsDiscard ? M.COPY.formDiscardStart : M.COPY.formStart}</button>
      {busy && onCancel && <button type="button" className="sem-btn" data-testid="sp-form-cancel" onClick={onCancel}>{M.COPY.cancel}</button>}</div>
  </section>;
}

/** The form with its own data: the saved endpoints from Connections, and the call that creates the model and reads the
 *  source. On success the whole view comes back and the Plan draws it. The Plan page hosts it, and so does Connections'
 *  From a SQL source, which passes its own way to the saved endpoints (it IS Connections). */
export function StartFromSql({ pro, onStarted, onAddOnConnections }: {
  pro: boolean; onStarted: (v: M.StartPlanView) => void; onAddOnConnections?: () => void;
}) {
  const [sources, setSources] = useState<SavedSources>(null);
  const [nonce, setNonce] = useState(0);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const { openConnections, context } = useConnection();
  const openModelSignIn = [context?.publishing, context?.editing, context?.querying].find((c) => !!c?.authMode);
  useEffect(() => {
    let live = true;
    setSources(null);
    void listSqlSources().then((r) => { if (live) setSources(r ?? []); }, () => { if (live) setSources('failed'); });
    return () => { live = false; };
  }, [nonce]);
  const [needsDiscard, setNeedsDiscard] = useState(false);
  // Which press is live: Cancel moves it on, so a read that ends later (the engine ends it with a sentence) says nothing to a form that was cancelled.
  const run = useRef(0);
  // The open model's unsaved changes are read when the button is pressed, as Connections does, and never replaced without the
  // person saying so: the first press asks, the second (a different button) discards. The engine refuses without it too.
  const start = async (req: M.StartRequest) => {
    const mine = ++run.current;
    setBusy(true); setError(null);
    if (!req.discardUnsaved) {
      const s = await rpc<{ hasUnsavedChanges?: boolean }>('sessionInfo').catch(() => null);
      if (mine !== run.current) return;
      if (s?.hasUnsavedChanges) { setNeedsDiscard(true); setBusy(false); return; }
    }
    rpcPressed<M.StartPlanView>('startModelFromSql', req, 'human').then(
      (v) => { if (mine !== run.current) return; setBusy(false); const o = M.startOutcome(v); if (o.view) { clearStartFormWords(); holdStartNotice(o.view); onStarted(o.view); } else setError(o.error); },
      (e: unknown) => { if (mine !== run.current) return; setBusy(false); setError(messageOf(e)); },
    );
  };
  // Cancel ends the read the engine is waiting on (a sign-in the person closed never answers) and frees the start. The form is usable again at once.
  const cancel = () => { run.current++; setBusy(false); void rpc('discardStartPlan', 'human').catch(() => undefined); };
  return <StartForm sources={sources} busy={busy} error={error} onStart={(r) => { void start(r); }} onCancel={cancel} pro={pro} needsDiscard={needsDiscard} openModelSignIn={openModelSignIn}
    onAddOnConnections={onAddOnConnections ?? (() => openSqlSources(openConnections))} onRetry={() => setNonce((n) => n + 1)} />;
}
