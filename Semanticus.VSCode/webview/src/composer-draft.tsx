import { useCallback, useEffect, useRef, useState } from 'react';
import { copyText, onDraftChange, onPlanChange, rpc } from './bridge';
import * as C from './composer-model';
import { WorkflowRunDetail } from './workflow-run';
import { sameTrimmed, useKeptText } from './keptwords';
import { draftModel, draftStore, heldDraft } from './drafts';

// ===================================================================================================
// A composer draft, drawn inside its Plan row (lane U5, was composer L6; the approved workflow-composer-v3.html Plan
// frames; composer contract 10.3; Plan-heart contract 14). Shared with Author (lane W2), which draws the same body.
//
// One sentence says who it waits on. "Before it runs" holds every question and every part your assistant writes, with
// the two tags and nothing more. Then the steps, the group After the change, and one footer. Every sentence is the
// engine's (DraftView); the page picks which controls show, from composer-model.ts, and names only the frames' button
// words. Nothing here opens or names an assistant (Kane 24 Sep): a waiting part says the assistant sees it on its next
// call, and offers Write it yourself, Copy the request, and Drop.
//
// ---- the calls, all answered with the whole draft --------------------------------------------------
//
//   getDraft(id)                                     the draft, with the options of each "You choose" question
//   editDraft(id, action, step, blank, question, text)   keep, drop, answer, write, pick, ask_again, check_again,
//                                                    add, refine, try_again, keep_anyway
//   runDraft(id)                                     Run with your assistant, and Approve and run
//   approveDraft(id)                                 Approve and run on a held draft, the person's own    person-only
//   continueDraft(id)                                Continue past a stop that asks the person            person-only
//   undo()                                           Undo: the one entry the change made
//   addPlanTask({ words })                           Ask your assistant why: a task on the Plan           person-only
//
// The draft also arrives on draft/didChange whenever either door moves it, so a part the assistant claims turns to
// "Your assistant is writing" here without a reload.
// ===================================================================================================

const TAG_ICON = {
  asst: <svg viewBox="0 0 12 12" aria-hidden="true" className="cd-ico"><path d="M2 10l1-3 5-5 2 2-5 5z" fill="none" stroke="currentColor" strokeWidth="1.1" /></svg>,
  you: <svg viewBox="0 0 12 12" aria-hidden="true" className="cd-ico"><circle cx="6" cy="4" r="2" fill="none" stroke="currentColor" strokeWidth="1.1" /><path d="M2 11c0-2.5 2-4 4-4s4 1.5 4 4" fill="none" stroke="currentColor" strokeWidth="1.1" /></svg>,
};

export function Tag({ kind }: { kind: 'asst' | 'you' }) {
  return <span className={`cd-tag cd-tag-${kind}`}>{TAG_ICON[kind]}{kind === 'asst' ? C.WORD.assistantWrites : C.WORD.youChoose}</span>;
}

export interface EditArgs { step?: string; blank?: string; question?: string; text?: string }

export interface DraftHandlers {
  onEdit: (action: string, args?: EditArgs) => void | Promise<unknown>;
  onRun: () => void;
  onApprove: () => void;
  onUndo: () => void;
  /** Continue past a person's stop, or past an assistant's step without its changes (the person's own). */
  onContinue?: () => void;
  onGo: (tool: string, target?: string) => void;
  onAskWhy: () => void;
  onCopy: (text: string) => void;
}

/** The draft, drawn from its view. Pure: every press goes to a handler, so the test renders it without an engine. */
export function DraftBody({ view: v, busy, copied, ...h }: DraftHandlers & { view: C.DraftView; busy: string | null; copied?: string | null }) {
  // The words in Add more words and Write it yourself are held by the DRAFT in the draft store (join 11 solui, sweep fix 1, rule R-B): the Plan draws this body under a row that
  // can move between sections (a remount), and Author draws the same draft. When the draft moves on (the assistant ran it) and a box is not offered any more, its words are named
  // with Copy and Discard instead of being kept where nobody sees them.
  const scope = `cdraft|${v.draftId}`;
  const [heldWrite] = useState(() => heldDraft<{ stepId: string; blank: string; text: string }, null>(scope, 'write')?.value);
  const [writing, setWriting] = useState<{ stepId: string; blank: string } | null>(heldWrite ? { stepId: heldWrite.stepId, blank: heldWrite.blank } : null);
  const [draftText, setDraftText] = useState(heldWrite?.text ?? '');
  const [more, setMore] = useState(() => heldDraft<string>(scope, 'more')?.value ?? '');
  // What the two boxes hold right now, read when an answer comes back: a box ends its words only when the engine took the write AND the box still holds exactly the words that were sent (round 3).
  const now = useRef({ more, draftText, writing });
  now.current = { more, draftText, writing };
  useEffect(() => {
    const m = draftModel();
    if (!m) return;
    if (more) draftStore.set(m, scope, 'more', { value: more, base: '', label: v.summary }); else draftStore.clear(m, scope, 'more');
  }, [more, scope]);   // eslint-disable-line react-hooks/exhaustive-deps
  useEffect(() => {
    const m = draftModel();
    if (!m) return;
    if (writing && draftText) draftStore.set(m, scope, 'write', { value: { ...writing, text: draftText }, base: null, label: v.summary }); else draftStore.clear(m, scope, 'write');
  }, [writing?.stepId, writing?.blank, draftText, scope]);   // eslint-disable-line react-hooks/exhaustive-deps
  const [showAllMenu, setShowAllMenu] = useState(false);
  const ran = C.hasRun(v);
  const primary = C.primaryOf(v);
  const waitingNotes = v.notes.filter(isFooterNote);
  const topNotes = v.notes.filter((n) => !isFooterNote(n) && n !== v.summary);
  const asked = C.askedQuestions(v);
  const parts = v.blanks;
  const before = !ran && (asked.length > 0 || parts.length > 0);
  const disabled = !!busy;
  const openWriter = (b: C.DraftBlankView) => { setWriting({ stepId: b.stepId, blank: b.name }); setDraftText(b.text ?? ''); };
  // A part the person is writing that this body does not offer now (the draft ran, or the part went): its words are named below.
  const writingShown = !!writing && before && parts.some((b) => b.stepId === writing.stepId && b.name === writing.blank);
  const writeKept = writing && !writingShown && draftText.trim() ? draftText : '';
  const refineKept = !C.canRefine(v) && more.trim() ? more : '';

  const partRow = (b: C.DraftBlankView) => {
    const acts = C.partActions(b, v);
    const open = writing?.stepId === b.stepId && writing.blank === b.name;
    return <div key={`${b.stepId}/${b.name}`} className="cd-part" data-testid={`cd-part-${b.stepId}-${b.name}`} data-state={b.state}>
      <span className="cd-part-name">{C.partName(b, v)}</span>
      <span className={b.state === 'needs' ? 'cd-part-line cd-warn' : C.isOpenPart(b) ? 'cd-part-line cd-muted' : 'cd-part-line'}>
        {b.state === 'needs' && <span className="cd-dot" aria-hidden="true" />}
        {(b.state === 'filled' || b.state === 'self' || b.state === 'generated') && <span className="cd-tick" aria-hidden="true">{'\u2713'}</span>}
        {b.line}
      </span>
      {acts.length > 0 && <span className="cd-acts">
        {acts.includes('pick') && (b.choices ?? []).map((c) => <button key={c} type="button" className="sem-btn sem-btn-sm" disabled={disabled}
          data-testid="cd-pick" onClick={() => void h.onEdit('pick', { step: b.stepId, blank: b.name, text: c })}>{refName(c)}</button>)}
        {acts.includes('ask_again') && <button type="button" className="sem-btn sem-btn-sm" disabled={disabled} data-testid="cd-ask-again"
          onClick={() => void h.onEdit('ask_again', { step: b.stepId, blank: b.name })}>{C.WORD.askAgain}</button>}
        {acts.includes('write') && <button type="button" className="sem-btn sem-btn-sm" disabled={disabled} data-testid="cd-write"
          onClick={() => openWriter(b)}>{C.WORD.writeItYourself}</button>}
        {acts.includes('copy') && <button type="button" className="sem-btn sem-btn-sm" disabled={disabled} data-testid="cd-copy"
          onClick={() => h.onCopy(b.request ?? '')}>{copied === b.request ? C.WORD.copied : C.WORD.copyTheRequest}</button>}
        {acts.includes('drop') && <button type="button" className="sem-btn sem-btn-sm pw-quiet" disabled={disabled} data-testid="cd-part-drop"
          onClick={() => void h.onEdit('drop', { step: b.stepId })}>{C.WORD.drop}</button>}
      </span>}
      {open && <form className="cd-write" data-testid="cd-writer" onSubmit={(e) => {
        e.preventDefault();
        if (!draftText.trim()) return;
        const sent = draftText.trim();
        void Promise.resolve(h.onEdit('write', { step: b.stepId, blank: b.name, text: sent })).then((ok) => {
          const w = now.current;
          if (ok && w.draftText.trim() === sent && w.writing?.stepId === b.stepId && w.writing.blank === b.name) setWriting(null);
        }, () => undefined);   // a refused write leaves the writer open with the words, under the engine's own error line
      }}>
        <textarea className="cd-textarea" value={draftText} rows={b.kind === 'dax' || b.kind === 'm' ? 3 : 2} autoFocus
          aria-label={`Write the ${C.KIND_WORD[b.kind] ?? b.kind}`} onChange={(e) => setDraftText(e.target.value)} />
        <span className="cd-acts">
          <button type="submit" className="sem-btn sem-btn-sm sem-btn-primary" disabled={disabled || !draftText.trim()}>{C.WORD.save}</button>
          <button type="button" className="sem-btn sem-btn-sm pw-quiet" onClick={() => setWriting(null)}>{C.WORD.cancel}</button>
        </span>
      </form>}
    </div>;
  };

  const stepRow = (s: C.DraftStepView) => {
    const control = C.stepControl(s, v);
    const part = C.openPartOf(s, v);
    const check = s.check;
    // A gated step the run waits at shows its guided step here, with the same questions, Submit and Skip a guided run
    // has, so the person can answer it on this door as the assistant does on the other (lane l9-finish).
    const guided = s.runId && s.status === 'Waiting' && v.pausedAt === s.id;
    const row = <div key={s.id} className={s.kept ? 'cd-step' : 'cd-step cd-step-off'} data-testid={`cd-step-${s.id}`} data-status={s.status ?? ''}>
      <span className="cd-num sem-num">{s.number}</span>
      <span className="cd-step-main">
        <span className="cd-step-head">
          <b>{s.title}</b>
          {s.tags.includes('you') && <Tag kind="you" />}
          {s.tags.includes('asst') && <Tag kind="asst" />}
          {s.object && <span className="cd-muted cd-trunc">{s.object}</span>}
        </span>
        {check && <span className={s.status === 'Failed after it ran' ? 'cd-check cd-warn' : 'cd-check'}><b>{check.phase}:</b> {check.text}</span>}
        {s.note && <span className="cd-muted">{s.note}</span>}
      </span>
      <span className="cd-changes sem-num">{s.changes}</span>
      <span className="cd-acts">
        {control === 'keepdrop' && <span className="sem-seg" role="group" aria-label={`Keep or drop step ${s.number}`}>
          <button type="button" className="sem-seg-item" aria-pressed={s.kept} disabled={disabled} data-testid={`cd-keep-${s.id}`}
            onClick={() => { if (!s.kept) void h.onEdit('keep', { step: s.id }); }}>{C.WORD.keep}</button>
          <button type="button" className="sem-seg-item" aria-pressed={!s.kept} disabled={disabled} data-testid={`cd-drop-${s.id}`}
            onClick={() => { if (s.kept) void h.onEdit('drop', { step: s.id }); }}>{C.WORD.drop}</button>
        </span>}
        {control === 'writedrop' && <>
          {part && <button type="button" className="sem-btn sem-btn-sm" disabled={disabled} onClick={() => openWriter(part)}>{C.WORD.writeItYourself}</button>}
          <button type="button" className="sem-btn sem-btn-sm" disabled={disabled} data-testid={`cd-drop-${s.id}`}
            onClick={() => void h.onEdit('drop', { step: s.id })}>{C.WORD.drop}</button>
        </>}
        {control === 'keepdrop' && s.kept && <span className="cd-move">
          <button type="button" className="sem-btn sem-btn-sm pw-quiet" disabled={disabled || !C.canMove(v, s, -1)} aria-label={`${C.WORD.moveUp}: step ${s.number}`}
            title={C.WORD.moveUp} data-testid={`cd-up-${s.id}`} onClick={() => void h.onEdit('reorder', { step: s.id, text: String(s.number - 1) })}>{'\u2191'}</button>
          <button type="button" className="sem-btn sem-btn-sm pw-quiet" disabled={disabled || !C.canMove(v, s, 1)} aria-label={`${C.WORD.moveDown}: step ${s.number}`}
            title={C.WORD.moveDown} data-testid={`cd-down-${s.id}`} onClick={() => void h.onEdit('reorder', { step: s.id, text: String(s.number + 1) })}>{'\u2193'}</button>
        </span>}
        {control === 'status' && <span className={statusTone(s.status)} data-testid={`cd-status-${s.id}`}>
          {s.status === 'Done' && <span className="cd-tick" aria-hidden="true">{'\u2713'}</span>}
          {s.status === 'Failed after it ran' && <span className="cd-dot" aria-hidden="true" />}
          {s.status}</span>}
      </span>
    </div>;
    return guided ? <div key={s.id} className="cd-guided" data-testid={`cd-guided-${s.id}`}>{row}<WorkflowRunDetail runId={s.runId!} currentOnly /></div> : row;
  };

  return <div className="cd-draft" data-testid={`cd-draft-${v.draftId}`} data-state={v.state}>
    {v.banner && <div className={v.banner.kind === 'check_failed' || v.banner.kind === 'stopped' ? 'cd-banner cd-banner-bad' : 'cd-banner'}
      role={v.banner.kind === 'check_failed' ? 'alert' : 'status'} data-testid="cd-banner" data-kind={v.banner.kind}>
      <div className="cd-banner-head">
        <b>{v.banner.kind === 'check_failed' && <span className="cd-dot" aria-hidden="true" />}{v.banner.title}</b>
        <span className="cd-acts">
          {C.bannerActions(v).map((a) => <BannerButton key={a} action={a} disabled={disabled} h={h} view={v} />)}
        </span>
      </div>
      <p className="cd-p">{v.banner.text}</p>
      {v.overwrite && <p className="cd-p cd-muted" data-testid="cd-overwrite-now">{C.WORD.yoursNow} {v.overwrite.now}</p>}
    </div>}

    {/* The banner already says it when its title is the summary ("Stopped at step 2 of 2"), so it is said once. */}
    {v.banner?.title !== v.summary && <div className="cd-head">
      <b className="cd-summary" data-testid="cd-summary">{v.summary}</b>
      <span className={v.state === 'needs_you' ? 'cd-state cd-warn' : 'cd-state cd-muted'} data-testid="cd-state">
        {v.state === 'needs_you' && <span className="cd-dot" aria-hidden="true" />}{C.stateLabel(v)}</span>
    </div>}
    {topNotes.length > 0 && <div className="cd-notes">{topNotes.map((n) => <p key={n} className="cd-p cd-muted">{n}</p>)}</div>}

    {v.menu && !ran && <section className="cd-menu" data-testid="cd-menu" aria-label="Pick the steps">
      {/* "Not sure enough to choose" is true only while no step is picked. The engine keeps the menu (and its line)
          until the draft runs, so more steps can be added, but once there is a step the warning is stale (journey
          m11 F9: it stayed in yellow on a draft that said Ready). */}
      {v.menuLine !== v.summary && v.steps.length === 0 && <p className="cd-p cd-warn" data-testid="cd-menu-line">{v.menuLine}</p>}
      {(() => {
        const { rows, more: hidden } = C.menuRows(v, showAllMenu);
        return <>
          {rows.map((m) => <div key={m.id} className="cd-menu-row" data-testid={`cd-menu-${m.id}`}>
            <b>{m.title}</b><span className="cd-muted sem-num">{m.changes}</span>
            <button type="button" className="sem-btn sem-btn-sm" disabled={disabled} onClick={() => void h.onEdit('add', { text: m.id })}>{C.WORD.add}</button>
          </div>)}
          {hidden > 0 && <button type="button" className="plan-link cd-more" onClick={() => setShowAllMenu(true)}>Show all {v.menu!.length}</button>}
        </>;
      })()}
    </section>}

    {before && <section className="cd-before" data-testid="cd-before" aria-label={C.WORD.beforeItRuns}>
      {asked.length > 0 && <div className="cd-before-col">
        {asked.map((q) => <Question key={q.name} q={q} draftId={v.draftId} disabled={disabled} onAnswer={(text) => void h.onEdit('answer', { question: q.name, text })} />)}
      </div>}
      {parts.length > 0 && <div className="cd-before-col">
        {parts.length > 0 && <div className="cd-before-h"><Tag kind="asst" /><span className="cd-muted">{parts.length === 1 ? '1 part' : `${parts.length} parts`}</span></div>}
        {parts.map(partRow)}
      </div>}
    </section>}

    {C.stopQuestions(v).length > 0 && <section className="cd-before" data-testid="cd-stop" aria-label="Asked here">
      <div className="cd-before-col">
        {C.stopQuestions(v).map((q) => <Question key={q.name} q={q} draftId={v.draftId} disabled={disabled} onAnswer={(text) => void h.onEdit('answer', { question: q.name, text })} />)}
      </div>
    </section>}

    {v.steps.length > 0 && <div className="cd-steps" data-testid="cd-steps">{v.steps.map(stepRow)}</div>}

    {v.after.length > 0 && <div className="cd-after" data-testid="cd-after">
      <div className="cd-after-h"><b>{C.WORD.afterTheChange}</b>{!ran && v.notes.find(isLoadingNote) && <span className="cd-muted">{v.notes.find(isLoadingNote)}</span>}</div>
      {v.after.map(stepRow)}
    </div>}

    {(writeKept || refineKept) && <div className="cd-banner" role="status" data-testid="cd-words-kept">
      <p className="cd-p">The draft moved on while you typed, so these words were not added to it. Nothing was changed with them.</p>
      {[writeKept && { key: 'write', words: writeKept, drop: () => { setWriting(null); setDraftText(''); } },
        refineKept && { key: 'more', words: refineKept, drop: () => setMore('') }].filter(Boolean).map((k) => {
        const kept = k as { key: string; words: string; drop: () => void };
        return <div key={kept.key} className="cd-acts" data-testid={kept.key === 'write' ? 'cd-write-kept' : 'cd-refine-kept'}>
          <code className="cd-trunc">{kept.words}</code>
          <button type="button" className="sem-btn sem-btn-sm" onClick={() => h.onCopy(kept.words)}>Copy</button>
          <button type="button" className="sem-btn sem-btn-sm pw-quiet" onClick={kept.drop}>Discard</button>
        </div>;
      })}
    </div>}

    <div className="cd-foot" data-testid="cd-foot">
      {C.canRefine(v) && <form className="cd-refine" onSubmit={(e) => {
        e.preventDefault();
        const sent = more.trim();
        if (!sent) return;
        void Promise.resolve(h.onEdit('refine', { text: sent })).then((ok) => { if (ok && now.current.more.trim() === sent) setMore(''); }, () => undefined);
      }}>
        <input className="sem-input pw-input" value={more} placeholder={C.WORD.addMoreWords} aria-label={C.WORD.addMoreWords}
          data-testid="cd-refine" onChange={(e) => setMore(e.target.value)} />
      </form>}
      {waitingNotes.length > 0 && <p className="cd-p cd-foot-note" data-testid="cd-waiting">
        {waitingNotes.map((n, i) => n.startsWith(C.WORD.nothingChangedYet)
          ? <span key={i}><b>{C.WORD.nothingChangedYet}</b>{n.slice(C.WORD.nothingChangedYet.length)} </span>
          : <span key={i} className="cd-muted">{n} </span>)}
      </p>}
      <span className="cd-acts cd-foot-acts">
        {v.requestText && v.state === 'waiting_for_assistant' && <button type="button" className="sem-btn sem-btn-sm" disabled={disabled}
          data-testid="cd-copy-all" onClick={() => h.onCopy(v.requestText!)}>{copied === v.requestText ? C.WORD.copied : C.WORD.copyTheRequest}</button>}
        {!ran && <button type="button" className="sem-btn sem-btn-sm" disabled={disabled} data-testid="cd-edit"
          onClick={() => h.onGo('workflows', C.authorTarget(v))}>{C.WORD.editInWorkflows}</button>}
        {C.doneActions(v).map((a) => a === 'try_again'
          ? <button key={a} type="button" className="sem-btn sem-btn-sm" disabled={disabled} data-testid="cd-try-again" onClick={() => void h.onEdit('try_again')}>{C.WORD.tryAgain}</button>
          : a === 'undo'
            ? <button key={a} type="button" className="sem-btn sem-btn-sm" disabled={disabled} data-testid="cd-undo" onClick={h.onUndo}>{C.WORD.undo}</button>
            : <button key={a} type="button" className="sem-btn sem-btn-sm" disabled={disabled} data-testid="cd-reusable" onClick={() => h.onGo('workflows', C.authorTarget(v))}>{C.WORD.makeReusable}</button>)}
        {primary === 'run_with_assistant' && <button type="button" className="sem-btn sem-btn-sm sem-btn-primary" disabled={disabled}
          data-testid="cd-run-with" onClick={h.onRun}>{C.WORD.runWithAssistant}</button>}
        {primary === 'approve_and_run' && <button type="button" className="sem-btn sem-btn-sm sem-btn-primary" disabled={disabled}
          data-testid="cd-approve" onClick={h.onApprove}>{C.WORD.approveAndRun}</button>}
        {v.pausedAt && v.canContinue && h.onContinue && <button type="button"
          className={primary === 'continue' ? 'sem-btn sem-btn-sm sem-btn-primary' : 'sem-btn sem-btn-sm'} disabled={disabled}
          data-testid="cd-continue" onClick={h.onContinue}>{C.pausedStep(v)?.person ? C.WORD.continueRun : C.WORD.continueWithout}</button>}
      </span>
    </div>
  </div>;
}

function BannerButton({ action, disabled, h, view }: { action: C.BannerAction; disabled: boolean; h: DraftHandlers; view: C.DraftView }) {
  switch (action) {
    case 'try_again': return <button type="button" className="sem-btn sem-btn-sm" disabled={disabled} data-testid="cd-try-again" onClick={() => void h.onEdit('try_again')}>{C.WORD.tryAgain}</button>;
    case 'ask_why': return <button type="button" className="sem-btn sem-btn-sm" disabled={disabled} data-testid="cd-ask-why" onClick={h.onAskWhy}>{C.WORD.askWhy}</button>;
    case 'keep_anyway': return <button type="button" className="sem-btn sem-btn-sm" disabled={disabled} data-testid="cd-keep-anyway" onClick={() => void h.onEdit('keep_anyway')}>{C.WORD.keepItAnyway}</button>;
    case 'undo': return <button type="button" className="sem-btn sem-btn-sm sem-btn-primary" disabled={disabled} data-testid="cd-undo" onClick={h.onUndo}>{C.WORD.undo}</button>;
    case 'undo_step': return <button type="button" className="sem-btn sem-btn-sm sem-btn-primary" disabled={disabled} data-testid="cd-undo" onClick={h.onUndo}>{C.WORD.undoStep1}</button>;
    case 'edit': return <button type="button" className="sem-btn sem-btn-sm" disabled={disabled} onClick={() => h.onGo('workflows', C.authorTarget(view))}>{C.WORD.editInWorkflows}</button>;
    case 'run_again': return <button type="button" className="sem-btn sem-btn-sm" disabled={disabled} data-testid="cd-run-again" onClick={h.onRun}>{C.WORD.runAgain}</button>;
    case 'keep_theirs': return <button type="button" className="sem-btn sem-btn-sm sem-btn-primary" disabled={disabled} data-testid="cd-keep-theirs" onClick={() => void h.onEdit('keep_theirs')}>{C.WORD.keepYours}</button>;
    case 'apply_draft': return <button type="button" className="sem-btn sem-btn-sm" disabled={disabled} data-testid="cd-apply-draft" onClick={() => void h.onEdit('apply_draft')}>{C.WORD.applyTheDraft}</button>;
  }
}

function Question({ q, draftId, disabled, onAnswer }: { q: C.DraftQuestionView; draftId: string; disabled: boolean; onAnswer: (text: string) => void }) {
  // A typed answer stays when the question is answered underneath it (R-B); the newer answer is named under the box. The engine trims an answer, so the compare is trimmed, and the
  // words are held under the draft (round 3), so a row that moves between sections (a remount) opens on them.
  const { text, setText, moved, useLive } = useKeptText(q.answer ?? '', q.name, { same: sameTrimmed, hold: { scope: `cdraft|${draftId}`, field: `answer:${q.name}` } });
  const input = C.questionInput(q);
  const name = `cd-q-${q.name}`;
  return <div className="cd-q" data-testid={name}>
    <div className="cd-before-h"><Tag kind="you" /><span className="cd-muted">{q.question}</span></div>
    {input === 'radio' && <div className="cd-radios" role="radiogroup" aria-label={q.question}>
      {C.choicesOf(q).map(([value, label]) => <label key={value} className="cd-radio">
        <input type="radio" name={name} value={value} checked={q.answer === value} disabled={disabled} onChange={() => onAnswer(value)} />
        <span>{label}</span>
      </label>)}
    </div>}
    {input === 'select' && <select className="sem-input pw-input cd-select" aria-label={q.question} value={q.answer ?? ''} disabled={disabled}
      onChange={(e) => { if (e.target.value) onAnswer(e.target.value); }}>
      {!q.answer && <option value="">Choose one</option>}
      {C.choicesOf(q).map(([value, label]) => <option key={value} value={value}>{label}</option>)}
    </select>}
    {input === 'text' && <form className="pw-note" onSubmit={(e) => { e.preventDefault(); if (text.trim()) onAnswer(text.trim()); }}>
      <input className="sem-input pw-input" value={text} aria-label={q.question} disabled={disabled} onChange={(e) => setText(e.target.value)} />
      <button type="submit" className="sem-btn sem-btn-sm" disabled={disabled || !text.trim() || text.trim() === q.answer}>{C.WORD.save}</button>
      {moved !== null && <span className="cd-muted" data-testid="cd-q-moved">It was answered: {moved}. Your words are kept. <button type="button" className="sem-btn sem-btn-sm" onClick={useLive}>Use that answer</button></span>}
    </form>}
  </div>;
}

/** The two waiting sentences sit in the footer, beside the actions, as the frame draws them. */
function isFooterNote(n: string): boolean {
  return n.startsWith(C.WORD.nothingChangedYet) || n === 'Your assistant sees this on its next call. Or copy the request.';
}
function isLoadingNote(n: string): boolean { return n.startsWith('Loading the data runs after the change'); }

function statusTone(status?: string): string {
  if (status === 'Failed after it ran' || status === 'Stopped') return 'cd-state cd-warn';
  if (status === 'Done') return 'cd-state';
  return 'cd-state cd-muted';
}

/** "measure:Returns/Return rate" as "Returns[Return rate]", "column:Returns/Quantity" as "Quantity" for a choice. */
function refName(ref: string): string {
  const rest = ref.includes(':') ? ref.slice(ref.indexOf(':') + 1) : ref;
  const slash = rest.indexOf('/');
  return slash < 0 ? rest : rest.slice(slash + 1);
}

function messageOf(error: unknown) { return error instanceof Error ? error.message : String(error); }

/** A draft, read from the engine and kept current, with every press sent to it. */
export function ComposerDraft({ draftId, changeNonce, onGo }: {
  draftId: string;
  /** Bumped when the model or the plan moved; the draft is read again. */
  changeNonce?: number;
  onGo: (tool: string, target?: string) => void;
}) {
  const [view, setView] = useState<C.DraftView | null>(null);
  const [busy, setBusy] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [said, setSaid] = useState<string | null>(null);
  const [copied, setCopied] = useState<string | null>(null);
  const mine = useRef(0);

  const read = useCallback(() => {
    const n = ++mine.current;
    rpc<C.DraftView>('getDraft', draftId).then((v) => { if (n === mine.current && v) setView(v); },
      (e: unknown) => { if (n === mine.current) setError(messageOf(e)); });
  }, [draftId]);
  useEffect(() => { read(); }, [read, changeNonce]);
  useEffect(() => onDraftChange((payload) => {
    const v = payload as C.DraftView | null;
    if (v && v.draftId === draftId) { mine.current++; setView(v); }
  }), [draftId]);
  useEffect(() => {
    let t: ReturnType<typeof setTimeout> | undefined;
    const off = onPlanChange(() => { if (t) clearTimeout(t); t = setTimeout(read, 150); });
    return () => { off(); if (t) clearTimeout(t); };
  }, [read]);

  /** Sends one press. True only when the engine took it: a refusal shows its error line and answers false, so a box that sent words can keep them. */
  const call = useCallback(async (key: string, method: string, ...args: unknown[]): Promise<boolean> => {
    setBusy(key); setError(null); setSaid(null);
    try {
      const v = await rpc<C.DraftView | null>(method, ...args);
      if (v && typeof v === 'object' && 'draftId' in v) { mine.current++; setView(v); setSaid(v.sentence ?? null); }
      else read();
      return true;
    } catch (e: unknown) { setError(messageOf(e)); return false; }
    finally { setBusy(null); }
  }, [read]);

  if (!view) {
    return error
      ? <div className="plan-error cd-error" role="alert">The draft could not be read. {error}</div>
      : <div className="cd-draft cd-muted" role="status"><span className="sem-spin" /> Reading the draft</div>;
  }
  return <>
    {error && <div className="plan-error cd-error" role="alert" data-testid="cd-error">Nothing was changed. {error}</div>}
    {said && <div className="plan-said cd-said" role="status" data-testid="cd-said">{said}</div>}
    <DraftBody view={view} busy={busy} copied={copied} onGo={onGo}
      onEdit={(action, a = {}) => call(`edit:${action}`, 'editDraft', view.draftId, action, a.step ?? null, a.blank ?? null, a.question ?? null, a.text ?? null)}
      onRun={() => void call('run', 'runDraft', view.draftId)}
      onApprove={() => { const [m, id] = C.approveCall(view); void call('approve', m, id); }}
      onUndo={() => void call('undo', 'undo')}
      onContinue={() => void call('continue', 'continueDraft', view.draftId)}
      onAskWhy={() => void call('askwhy', 'addPlanTask', { words: C.askWhyWords(view) })}
      onCopy={(text) => { void copyText(text).then((ok) => { if (ok) { setCopied(text); setTimeout(() => setCopied(null), 1500); } }); }} />
  </>;
}
