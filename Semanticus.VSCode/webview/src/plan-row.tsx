import { useEffect, useLayoutEffect, useRef, useState } from 'react';
import { createPortal } from 'react-dom';
import { anchorUnder } from './toolrow';
import * as P from './plan-model';
import { RepeatEditor, type CadenceHold } from './plan-repeat';
import type { PlanRepeat, RepeatCadence, RepeatRequest } from './plan-repeat-model';
import { jumpTarget } from './search-names';
import { sameTrimmed, useKeptText } from './keptwords';
import { draftModel, draftStore, heldDraft, useHeldDrafts } from './drafts';

// ===================================================================================================
// One Plan row, and its detail (lane U1; shared with U3's Send, U5's composer and RP's repeat editor, who send
// patches to this file rather than editing it).
//
// A row is one piece of work with an outcome, and it says three separate facts about it: where it came from
// (the source mark), who does it next (the doer mark, in the doer's hue), and where it stands (the engine's one
// state word). Then the page where it is done by hand, and its buttons. Nothing here decides a state: every word
// is read off the row the engine sent, and every button is a call the engine answers with the whole plan.
// ===================================================================================================

const SOURCE_PATH: Record<P.PlanSource, React.ReactElement> = {
  found: <><circle cx="5" cy="5" r="3.3" fill="none" stroke="currentColor" strokeWidth="1.3" /><path d="M7.5 7.5L11 11" stroke="currentColor" strokeWidth="1.3" /></>,
  jev: <path d="M6 .8l1.4 3.8L11.2 6 7.4 7.4 6 11.2 4.6 7.4.8 6l3.8-1.4z" fill="currentColor" />,
  words: <path d="M1.5 2h9v6.2H5.2L2.4 10.6V8.2H1.5z" fill="none" stroke="currentColor" strokeWidth="1.2" strokeLinejoin="round" />,
  you: <><circle cx="6" cy="3.6" r="2.1" fill="none" stroke="currentColor" strokeWidth="1.2" /><path d="M1.8 11c.3-2.4 2-3.8 4.2-3.8s3.9 1.4 4.2 3.8" fill="none" stroke="currentColor" strokeWidth="1.2" /></>,
  workflow: <><circle cx="2.4" cy="6" r="1.6" fill="currentColor" /><circle cx="9.6" cy="2.4" r="1.6" fill="currentColor" /><circle cx="9.6" cy="9.6" r="1.6" fill="currentColor" /><path d="M3.8 5.3L8.2 3M3.8 6.7L8.2 9" stroke="currentColor" strokeWidth="1.1" /></>,
  repeat: <><path d="M2 6.2a4 4 0 0 1 7-2.7M10 5.8a4 4 0 0 1-7 2.7" fill="none" stroke="currentColor" strokeWidth="1.2" /><path d="M9.6 1.2v2.8H6.8M2.4 10.8V8h2.8" fill="none" stroke="currentColor" strokeWidth="1.2" /></>,
};

export function SourceMark({ source }: { source: P.PlanSource }) {
  return <svg className="pw-ico" viewBox="0 0 12 12" aria-hidden="true">{SOURCE_PATH[source]}</svg>;
}

/** The doer's shape: a square for Semanticus, a circle for you, a diamond for your assistant, so the doer reads without
 *  colour too. Hollow while the row is paused or being checked. */
export function DoerMark({ doer, hollow = false }: { doer: P.PlanDoer; hollow?: boolean }) {
  const fill = hollow ? 'none' : 'currentColor';
  const stroke = hollow ? 'currentColor' : undefined;
  return <svg className={`pw-ico pw-doer-${doer}`} viewBox="0 0 12 12" aria-hidden="true">
    {doer === 'semanticus' && <rect x="2" y="2" width="8" height="8" fill={fill} stroke={stroke} strokeWidth="1.3" />}
    {doer === 'you' && <circle cx="6" cy="6" r="4.3" fill={fill} stroke={stroke} strokeWidth="1.3" />}
    {doer === 'assistant' && <path d="M6 1L11 6 6 11 1 6z" fill={fill} stroke={stroke} strokeWidth="1.3" />}
  </svg>;
}

/** The state column. A row a run is working on shows its own step instead of the engine's word (round 1, K1): a spinner
 *  and Working out, Applying, Checking; then Done in green with its change count; a row the press holds but has not reached
 *  says In this run. `note` is a line the page adds after the word (a finished row's "by your assistant"). */
export function StateWord({ item, step, note: extra }: { item: P.PlanItem; step?: P.RunPhase | null; note?: string | null }) {
  if (step) {
    const live = step.step === 'work' || step.step === 'apply' || step.step === 'check';
    const tone = live ? 'spin pw-state-run' : step.step === 'done' ? 'good' : 'faint';
    const note = P.stepNote(step);
    return <span className={`pw-state pw-state-${tone}`} data-state={step.step} data-step={step.step}
      title={step.reason ?? (typeof step.ms === 'number' ? `${P.RUN_STEP_WORD[step.step]}, ${(step.ms / 1000).toFixed(1)} s into the run` : undefined)}>
      <span className="pw-state-mark" aria-hidden="true" />
      <span className="pw-state-w">{P.RUN_STEP_WORD[step.step]}</span>
      {note && <span className="pw-state-note">{note}</span>}
    </span>;
  }
  const word = P.stateWordOf(item);
  // A row another change closed says so in the word's place, with the reason (which change) on hover.
  const closed = P.closedByOther(item);
  const note = closed ? null : extra ?? (item.stateNote && word !== 'needs_you' && item.stateNote.length <= 28 ? item.stateNote : null);
  return <span className={`pw-state pw-state-${closed ? 'faint' : P.stateTone(word)}`} data-state={word}
    title={closed ? item.why?.[item.why.length - 1] : item.stateNote ?? undefined}>
    <span className="pw-state-mark" aria-hidden="true" />
    <span className="pw-state-w">{closed ? P.NO_LONGER_NEEDED : P.STATE_WORD_LABEL[word]}</span>
    {note && <span className="pw-state-note">{note}</span>}
  </span>;
}

/** A row action. The Plan answers `Promise<boolean>` for the ones that send typed words (Mark done): true only when the engine took it. */
export type RowActionHandler = (item: P.PlanItem, action: P.RowActionId, text?: string) => void | Promise<boolean>;

function ActionButton({ item, action, busy, onAction }: { item: P.PlanItem; action: P.RowAction; busy: string | null; onAction: RowActionHandler }) {
  return <button type="button" data-testid={`plan-act-${action.id}`} data-action={action.id}
    className={action.primary ? 'sem-btn sem-btn-sm sem-btn-primary' : 'sem-btn sem-btn-sm'}
    disabled={!!busy || action.disabled} title={action.title}
    onClick={(e) => { e.stopPropagation(); onAction(item, action.id); }}>{action.label}</button>;
}

/** A floating menu: a trigger and a popover drawn into the Studio root (as the Advanced modelling menu is), because the row
 *  clips and an absolute popover would be cut off. Closes on a press away and on Escape. One pattern for the doer menu
 *  and the dots. */
function useFloatingMenu(align: 'start' | 'end') {
  const [open, setOpen] = useState(false);
  const [box, setBox] = useState<{ left: number; top: number } | null>(null);
  const btn = useRef<HTMLButtonElement>(null);
  const pop = useRef<HTMLDivElement>(null);
  useLayoutEffect(() => { if (open) setBox(anchorUnder(btn.current, pop.current, align)); else setBox(null); }, [open, align]);
  useEffect(() => {
    if (!open) return undefined;
    const away = (e: MouseEvent) => { const t = e.target as Node; if (!btn.current?.contains(t) && !pop.current?.contains(t)) setOpen(false); };
    const key = (e: KeyboardEvent) => { if (e.key === 'Escape') { setOpen(false); btn.current?.focus(); } };
    document.addEventListener('mousedown', away);
    document.addEventListener('keydown', key);
    return () => { document.removeEventListener('mousedown', away); document.removeEventListener('keydown', key); };
  }, [open]);
  const place = (node: React.ReactNode) => createPortal(node, (btn.current?.closest('.studio-root') as HTMLElement | null) ?? document.body);
  return { open, setOpen, box, btn, pop, place };
}

/** The dots at the end of the row: the rest of what a row can do, each once, and only what fits this row. */
export function PlanRowMenu({ item, items, busy, onAction, grant, canRepeat }: {
  item: P.PlanItem; items?: P.PlanItem[]; busy: string | null; onAction: RowActionHandler;
  grant?: 'granted' | 'denied' | 'unknown'; canRepeat?: boolean;
}) {
  const actions = P.rowMenuActions(item, { items, grant, canRepeat });
  const m = useFloatingMenu('end');
  if (actions.length === 0) return null;
  return <>
    <button ref={m.btn} type="button" className="sem-btn sem-btn-sm pw-quiet pw-more" aria-label="More for this row" title="More"
      aria-haspopup="menu" aria-expanded={m.open} data-testid="plan-row-more" disabled={!!busy}
      onClick={(e) => { e.stopPropagation(); m.setOpen((o) => !o); }}>···</button>
    {m.open && m.place(<div ref={m.pop} className="compact-menu pw-row-menu" role="menu" aria-label={`More for ${item.title}`} data-testid="plan-row-menu"
      style={m.box ? { left: m.box.left, top: m.box.top } : { left: -9999, top: -9999 }}>
      {actions.map((a) => <button key={a.id} type="button" role="menuitem" data-testid={`plan-menu-${a.id}`} disabled={a.disabled}
        className={a.why ? 'pw-menu-off' : undefined} title={a.title}
        onClick={(e) => { e.stopPropagation(); m.setOpen(false); onAction(item, a.id); }}>{a.label}{a.why && <span className="pw-menu-why">{a.why}</span>}</button>)}
    </div>)}
  </>;
}

/** The doer on the row IS the control (round 1, K5): press it for Semanticus, Your assistant, Me. A choice that cannot work
 *  is faded with its reason, and still reads. This one menu replaces the Who does it segment, I'll take it, Give to your
 *  assistant and Let it run. */
export function DoerMenu({ item, busy, onDoer, word }: { item: P.PlanItem; busy: string | null; onDoer: (item: P.PlanItem, doer: P.PlanDoer) => void; word?: P.PlanStateWord }) {
  const doer = P.doerOf(item);
  const choices = P.doerMenu(item);
  const m = useFloatingMenu('start');
  const mark = <DoerMark doer={doer} hollow={word === 'paused' || word === 'checking'} />;
  if (!choices) return <>{mark}<span className="pw-trunc">{P.DOER_LABEL[doer]}</span></>;
  return <>
    <button ref={m.btn} type="button" className={m.open ? 'pw-doerbtn pw-doerbtn-open' : 'pw-doerbtn'} aria-haspopup="menu" aria-expanded={m.open}
      data-testid="plan-doer-btn" disabled={!!busy} title="Who does this"
      onClick={(e) => { e.stopPropagation(); m.setOpen((o) => !o); }}>
      {mark}<span className="pw-trunc">{P.DOER_LABEL[doer]}</span><span className="pw-doer-car" aria-hidden="true">{'▾'}</span>
    </button>
    {m.open && m.place(<div ref={m.pop} className="compact-menu pw-row-menu pw-doer-menu" role="menu" aria-label={`Who does ${item.title}`} data-testid="plan-doer-menu"
      style={m.box ? { left: m.box.left, top: m.box.top } : { left: -9999, top: -9999 }}>
      {choices.map((c) => <button key={c.doer} type="button" role="menuitemradio" aria-checked={doer === c.doer} disabled={c.disabled}
        className={c.disabled ? 'pw-menu-off' : undefined} data-testid={`plan-doer-opt-${c.doer}`} title={c.why}
        onClick={(e) => { e.stopPropagation(); m.setOpen(false); onDoer(item, c.doer); }}>
        <DoerMark doer={c.doer} /><span>{c.label}</span>
        {c.why ? <span className="pw-menu-why">{c.why}</span> : doer === c.doer ? <span className="pw-menu-why" aria-hidden="true">{'✓'}</span> : null}
      </button>)}
    </div>)}
  </>;
}

/** The step count an On it run shows where the main button would stand ("3 / 7", mockup). */
function stepCount(item: P.PlanItem): string | null {
  const m = /step (\d+) of (\d+)/u.exec(item.stateNote ?? '');
  return m ? `${m[1]} / ${m[2]}` : null;
}

export function PlanRow({ item, items, open, busy, onToggle, onAction, onDoer, onGo, labelOf, first = false, lead, meta, step = null, justDone = false, free = false, grant, canRepeat }: {
  item: P.PlanItem; items: P.PlanItem[]; open: boolean; busy: string | null;
  onToggle: () => void; onAction: RowActionHandler; onDoer: (item: P.PlanItem, doer: P.PlanDoer) => void; onGo: (tool: string, target?: string) => void;
  labelOf?: (tool: string) => string | undefined; first?: boolean;
  /** The place in the offer on the assistant's track ("1", "2"), when it has one. */
  lead?: string | null;
  /** A quiet word where the buttons start ("Finishes, even when paused", "Not offered yet"). */
  meta?: string | null;
  /** Where a run has this row (Working out, Applying, Checking, Done, In this run), when a run does. */
  step?: P.RunPhase | null;
  /** The row turned done while the page was open: it sits in "Done just now". */
  justDone?: boolean;
  /** On free the Run button is Pro, so a Ready Semanticus row with a worked-out set is Approved by hand. */
  free?: boolean;
  grant?: 'granted' | 'denied' | 'unknown';
  canRepeat?: boolean;
}) {
  const source = P.sourceOf(item);
  const doer = P.doerOf(item);
  const word = P.stateWordOf(item);
  const sub = P.subLine(item);
  const main = P.mainAction(item, free);
  const steps = word === 'on_it' ? stepCount(item) : null;
  const draft = P.draftOwnState(item);
  const place = P.pagePlace(item.jump);
  const live = step?.step === 'work' || step?.step === 'apply' || step?.step === 'check';
  const cls = ['pw-row', open ? 'pw-row-open' : '', doer === 'you' ? 'pw-row-you' : '', live ? 'pw-row-live' : '', justDone ? 'pw-row-justdone' : ''].filter(Boolean).join(' ');
  const doneBy = justDone && !step && doer === 'assistant' && !P.closedByOther(item) ? 'by your assistant' : null;
  return <div className={cls} data-testid={`plan-row-${item.id}`} data-row={item.id}
    data-doer={doer} data-state={step ? step.step : word} {...(first ? { 'data-tour': 'row' } : {})}>
    <span className="pw-src">
      <SourceMark source={source} />
      <span className="pw-trunc" title={P.sourceLine(item, labelOf)}>{P.SOURCE_LABEL[source]}</span>
    </span>
    <button type="button" className="pw-title" aria-expanded={open} data-testid={`plan-row-open-${item.id}`} onClick={onToggle}>
      <b className="pw-trunc">{lead && <span className="pw-lead sem-num" data-testid="plan-row-lead">{lead}</span>}{item.title}</b>
      {sub.waits
        ? <span className="pw-sub pw-trunc">{sub.optional ? 'Optional · ' : ''}Waits for: <b>{sub.waits}</b></span>
        : <span className="pw-sub pw-trunc">{sub.text}</span>}
    </button>
    <span className="pw-doer" {...(first ? { 'data-tour': 'doer' } : {})}>
      <DoerMenu item={item} busy={busy} onDoer={onDoer} word={word} />
    </span>
    <StateWord item={item} step={step} note={doneBy} />
    <span className="pw-page">
      {place && item.jump?.tool
        ? <button type="button" className="plan-link pw-trunc" data-testid="plan-row-page" onClick={() => onGo(item.jump!.tool, jumpTarget(item.jump))}>{place}</button>
        : justDone ? null : <span className="plan-muted">No page for this one</span>}
    </span>
    <span className="pw-acts">
      {steps && <span className="pw-steps sem-num">{steps}</span>}
      {meta && <span className="pw-steps" data-testid="plan-row-meta">{meta}</span>}
      {draft && <span className="pw-steps" data-testid="plan-row-draft" title={draft.title}>{draft.text}</span>}
      {/* Open is the way in; once the row is open its own controls are right under it. */}
      {main && !(open && main.id === 'open') && <ActionButton item={item} action={main} busy={busy} onAction={onAction} />}
      <PlanRowMenu item={item} items={items} busy={busy} onAction={onAction} grant={grant} canRepeat={canRepeat} />
    </span>
  </div>;
}

// Words typed on a row are held by the ROW, never by the place it is drawn (join 11 solui, sweep fix 1, rule R-B). The Plan draws a row in Needs you, Next or Done, and a row
// that moves between them (the assistant took it, finished it, or Jev ranked it again) is a new mount there, so words that lived only in the detail were gone. They are held
// in the draft store under `planrow|<row id>` and the detail opens on them again; the open row stays on the list while it is open (withOpenRow), and words for a row that
// left the plan altogether are named on the Plan (PlanRowWordsKept).
export const ROW_WORDS = 'planrow|';
const rowScope = (id: string) => ROW_WORDS + id;

/** The list with the open row on it. A row the person has open stays drawn while it is open, even when another writer moved it past the cap of Next or finished it: closing it
 *  lets it go. A row that is not in the plan at all is not drawn (its words are named by PlanRowWordsKept). */
export function withOpenRow(work: P.WorkView, items: P.PlanItem[], openId: string | null): P.WorkView {
  if (!openId) return work;
  const shown = [...work.rows, ...work.justDone, ...work.onlyYou, ...work.next, ...work.done];
  if (shown.some((r) => r.id === openId)) return work;
  const row = items.find((r) => r.id === openId);
  if (!row) return work;
  if (work.filtered) return { ...work, rows: [...work.rows, row] };
  return P.groupOf(row) === 'you' ? { ...work, onlyYou: [...work.onlyYou, row] } : { ...work, next: [...work.next, row] };
}

const keptWords = (field: string, v: unknown): string => {
  if (field === 'ask') return String((v as { words?: string })?.words ?? '');
  if (field === 'repeat') return 'a repeat you were setting';
  return typeof v === 'string' ? v : JSON.stringify(v);
};

/** Words typed on a row that is not in the plan now (the assistant removed it, or it was set aside and left the list), named with Discard. Nothing here writes to the plan. */
export function PlanRowWordsKept({ items }: { items: P.PlanItem[] }) {
  useHeldDrafts(ROW_WORDS);   // re-render when a row gains or loses words
  const m = draftModel();
  const ids = new Set(items.map((i) => i.id));
  const gone = m ? draftStore.scopes(m, ROW_WORDS).filter((e) => !ids.has(e.scope.slice(ROW_WORDS.length))) : [];
  if (!m || gone.length === 0) return null;
  return <div className="plan-line" role="status" data-testid="plan-words-kept">
    <span className="plan-muted">{gone.length === 1 ? 'Words you typed on a task that is not on the plan now are kept. Nothing was saved.' : `Words you typed on ${gone.length} tasks that are not on the plan now are kept. Nothing was saved.`}</span>
    {gone.map((e) => <span key={e.scope} className="pw-acts pw-acts-start" data-testid="plan-words-kept-row">
      <b>{e.label || 'A task'}</b>
      <span className="pw-trunc">{e.fields.map((f) => keptWords(f.field, f.draft.value)).filter(Boolean).join(' / ')}</span>
      <button type="button" className="sem-btn sem-btn-sm pw-quiet" onClick={() => draftStore.clearScope(m, e.scope)}>Discard</button>
    </span>)}
  </div>;
}

/** Everything a row knows, opened under it, for reading: why, done when, what it touches, what it changed, one note and
 *  one reply. No control rows (round 1, K5): who does it is the doer menu on the row, where it is done is the page link on
 *  the row, and the rest is in the dots. The one exception is the repeat editor, which the dots' Repeat... opens here. */
export function PlanRowDetail({ item, busy, onNote, labelOf, repeats, isPro, onRepeat, showRepeat, onHistory }: {
  item: P.PlanItem; busy: string | null; labelOf?: (tool: string) => string | undefined;
  /** Opens the Plan's History on one of this row's edits (contract 1.6: a Done row shows exactly what it changed). */
  onHistory?: (entryId: string) => void;
  onNote: (item: P.PlanItem, text: string) => void;
  /** The repeat editor (lane RP): the saved repeats, the tier, and the three person-only repeat calls. */
  repeats?: PlanRepeat[] | null; isPro?: boolean;
  onRepeat?: { add: (r: RepeatRequest) => void | Promise<boolean>; change: (id: string, r: RepeatRequest) => void | Promise<boolean>; remove: (id: string) => void };
  /** The dots' Repeat... was pressed on this row: the editor is drawn here. */
  showRepeat?: boolean;
}) {
  // Words typed in the note stay when the note changes underneath them (the assistant, or the page's own re-read); the newer note is the one shown above (R-B).
  const scope = rowScope(item.id);
  const [heldNote] = useState(() => heldDraft<string, string>(scope, 'note'));
  const { text: note, setText: setNote, moved: noteMoved, useLive: useLiveNote } = useKeptText(item.note ?? '', item.id, { same: sameTrimmed });   // the engine trims a note: a trailing space is not a change
  // The note the words were typed against: the held one when the detail opened on held words, else the live note whenever the box follows it.
  const noteBase = useRef(heldNote ? String(heldNote.base ?? '') : item.note ?? '');
  useEffect(() => { if (heldNote) setNote(heldNote.value); }, []);   // eslint-disable-line react-hooks/exhaustive-deps
  const firstHold = useRef(true);
  useEffect(() => {
    const m = draftModel(), live = item.note ?? '';
    if (firstHold.current) { firstHold.current = false; if (heldNote) return; }   // the held words go into the box first
    if (!m) return;
    if (sameTrimmed(note, live)) { noteBase.current = live; draftStore.clear(m, scope, 'note'); return; }
    draftStore.set(m, scope, 'note', { value: note, base: noteBase.current, label: item.title });
  }, [note, item.note, scope]);   // eslint-disable-line react-hooks/exhaustive-deps
  // The cadence being set in the repeat form, held under the row with its other words.
  const repeatHold: CadenceHold = {
    read: () => heldDraft<RepeatCadence>(scope, 'repeat')?.value,
    write: (c, start) => { const m = draftModel(); if (m) draftStore.set(m, scope, 'repeat', { value: c, base: start, label: item.title }); },
    clear: () => { const m = draftModel(); if (m) draftStore.clear(m, scope, 'repeat'); },
  };
  // A note that moved while the held words were away is named as one that moved while they were typed.
  const live = item.note ?? '';
  const shownMoved = noteMoved ?? (heldNote && !sameTrimmed(noteBase.current, live) && !sameTrimmed(note, live) ? live : null);
  const doer = P.doerOf(item);
  const why = (item.why ?? []).filter(Boolean);
  const replyWord = item.claim?.claimedBy === 'you' || doer === 'you' ? 'Your reply' : 'Its reply';
  const hasEvents = (item.events?.length ?? 0) > 0;
  return <div className="pw-detail" data-testid={`plan-detail-${item.id}`}>
    <span className="pw-detail-lbl plan-muted">Details</span>
    <div className="pw-detail-l">
      <dl className="pw-kv">
        <dt>From</dt><dd data-testid="plan-detail-source">{P.sourceLine(item, labelOf)}</dd>
        <dt>Why</dt><dd>{why.length ? why.join(' ')
          : P.sourceOf(item) === 'words' || P.sourceOf(item) === 'you' ? 'You asked for it.' : 'Semanticus gave no reason for this one.'}</dd>
        <dt>Done when</dt><dd>{item.doneText || 'You mark it done.'}</dd>
        {item.fullText && <><dt>Your text</dt><dd className="pw-fulltext" data-testid="plan-detail-fulltext">{item.fullText}</dd></>}
        <dt>Objects</dt><dd className="pw-objects">{P.objectsLine(item)}</dd>
        {item.stateWord === 'needs_you' && item.stateNote && <><dt>Needs</dt><dd>{item.stateNote}</dd></>}
      </dl>
      <dl className="pw-kv pw-convo">
        <dt><DoerMark doer="you" />Your note</dt>
        <dd>{item.note || <span className="plan-muted">No note.</span>}</dd>
        {/* THE REPLY, ONLY WHERE SOMEBODY REPLIES. The assistant reads a note on its own rows on its next call, so
            that promise is made there. Semanticus does not read notes, so a row it runs has no reply line unless
            one was really written, and a row that is yours says only that nothing has been written yet. */}
        {(doer !== 'semanticus' || item.reply) && <>
          <dt><DoerMark doer={doer === 'you' ? 'you' : 'assistant'} />{replyWord}</dt>
          <dd>{item.reply || <span className="plan-muted">{P.noReplyWords(doer, item)}</span>}</dd>
        </>}
      </dl>
      <form className="pw-note" onSubmit={(e) => { e.preventDefault(); onNote(item, note); }}>
        <input className="sem-input pw-input" data-testid="plan-note" value={note} maxLength={1000}
          placeholder={item.note ? 'Change your note' : 'Add a note'} aria-label="Your note"
          onChange={(e) => setNote(e.target.value)} />
        <button type="submit" className="sem-btn sem-btn-sm" disabled={!!busy || note.trim() === (item.note ?? '').trim()}>Save note</button>
      {shownMoved !== null && <span className="plan-muted" data-testid="plan-note-moved">The note changed while you typed. Your words are kept. <button type="button" className="sem-btn sem-btn-sm" onClick={useLiveNote}>Use the new note</button></span>}
      </form>
    </div>
    <div className="pw-detail-r">
      {hasEvents && <div className="pw-field-row" data-testid={`plan-events-${item.id}`}>
        <span className="plan-muted">What it changed</span>
        <span className="pw-acts pw-acts-start">
          <span>{P.eventsLine(item.events!.length)}</span>
          {onHistory && <button type="button" className="plan-link" data-testid={`plan-events-open-${item.id}`}
            onClick={() => onHistory(item.events![item.events!.length - 1])}>Open in History</button>}
        </span>
      </div>}
      {showRepeat && onRepeat && <RepeatEditor row={item} repeats={repeats} hold={repeatHold} isPro={!!isPro} busy={busy}
        onAdd={onRepeat.add} onChange={onRepeat.change} onDelete={onRepeat.remove} />}
    </div>
  </div>;
}
