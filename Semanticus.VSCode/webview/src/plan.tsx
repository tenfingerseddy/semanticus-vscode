import { Suspense, lazy, useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react';
import { createPortal } from 'react-dom';
import { copyText, focusSelectInProperties, onPlanChange, openExternal, onPlanRunProgress, rpc, runHostCommand, saveModelFromPage, selectInProperties } from './bridge';
import { Caret } from './ui';
import { planRankingOf, type PlanRankingGrant } from './features';
import { PENDING_ACCESS, refreshEntitlement, upgradeToPro, useTier } from './pro';
import * as P from './plan-model';
import { useConsentCard } from './consent';
import { OptimizeView } from './optimize';
import { PlanStrip } from './plan-strip';
import { PlanRow, PlanRowDetail, PlanRowWordsKept, withOpenRow, type RowActionHandler } from './plan-row';
import { repeatFace } from './plan-repeat-model';
import { AskReply, ASK_TOOL_LABEL, fieldTaskPage, useAskSuggestion } from './askbox';
import { PlanHistory } from './plan-history';
import { peekRestoreLanding } from './plan-history-model';
import { PlanGuide } from './plan-guide';
import { PlanHelpMark } from './help';
import { RefreshPlanButton } from './plan-refresh';
import { WorkflowRunDetail } from './workflow-run';
import type { WorkflowRunView } from './workflow-ui';
import { ComposerDraft } from './composer-draft';
import { draftIdOf, draftRowId, type DraftView } from './composer-model';
import { objectLabel, refToObject, type ObjectRef } from './route';
import { startPageMode, startPlanBelongs } from './startplan-model';
import { BuiltPlanBand, StartFormKept, StartFromSql, StartPlanPanel, useStartPlan, WordsKeptAfterBuild } from './startplan';
import { draftModel } from './drafts';
import { compareSeedFromSession } from './contextbar';
import type { ApplyDiffResult, ModelDiff } from './compare';
import type { ModelConnectionContext, SessionInfo } from './connection';


/** Said when the Save row's save did not happen (cancelled, or the host could not save). */
const SAVE_NOT_DONE = 'Nothing was saved. Press Save again, or use Ctrl+S.';
// The spec editor is a PANEL this page HOSTS, opened only when a route asks for it. It drags in
// CodeMirror and the DAX language service, which is a large thing to make every reader of the plan pay
// for; lazy keeps it out of the page's own chunk and out of the plan test's module graph.
const SpecPanel = lazy(() => import('./spec').then((m) => ({ default: m.SpecView })));

// ===================================================================================================
// The Plan: where a person starts (Plan at the heart, lane U1; the approved mockup plan-heart.html).
//
// A picture, the facts, one field, and a short list. The strip on top counts three tracks (Semanticus, you, your
// assistant) and filters the list to one; the facts line says what model this is; the one field "Describe a task or
// an aim" adds a row with Enter; the list is Needs you, then Next, seven rows then Show all. Every row says where it
// came from, who does it and where it stands, and every word of that is the engine's.
//
// ---- the calls, all answered with the whole plan ---------------------------------------------------
//
//   getModelPlan()                               the plan, its strip, its readyDigest and the switch
//   movePlanTaskUp(id)                           Move up, in the dots, on the assistant's tasks person-only
//   setPlanItemDoer(id, doer)                    the doer menu on the row                       person-only
//   setAssistantQueue('on' | 'paused')           the switch "Tasks for your assistant"          person-only
//   setPlanTaskNote(id, text)                    one note                                       person-only
//   addPlanTask({ words })                       the field                                      person-only
//   claimPlanTask / completePlanTask             Me (on an assistant task), Mark done
//   skipModelPlanItem / unskipModelPlanItem      Set aside (the dots) and Put back
//   modelPlanAction(id, 'approve', origin, digest)   Approve on a row with a worked-out change set. DIGEST LAST.
//   applyPlanItems([], 'human', readyDigest)     Run the plan: ONE call a wave, fenced on the Ready set (contract 7.2)
//   plan/runProgress (notification)              each row's step during a run: Working out, Applying, Checking, Done
//   undo('human', entryId)                       the receipt's Undo: takes back the whole press
//
// The person-only controls live on HumanGovernanceRpcTarget, which an agent connection never registers; the
// assistant reads each of them on its next call (contract 12). Run the plan is one call a wave, and one press makes
// every wave (plan-model runAllWaves): a plan that moved since it was read is refused by the engine with a sentence,
// which the receipt shows.
//
// Free and Pro (contract 13): everything here is free except Run the plan and the Jev source. On free the Pro line
// stands where Run the plan stands, never a dead button.
// ===================================================================================================

/** What modelPlanAction and addPlanTask answer with. `plan` is the whole plan after the call. */
interface PlanActionResult {
  itemId?: string;
  action?: string;
  performed?: boolean;
  sentence?: string;
  note?: string;
  revision?: number;
  plan?: P.Plan;
}

/** What applyPlanItems answers with. One sentence for the whole step, and the plan after it. */
interface ApplyItemsResult {
  performed?: boolean;
  applied?: number;
  itemIds?: string[];
  sentence?: string;
  revision?: number;
  /** The History entry of this batch: what undo('human', entryId) takes back. */
  entryId?: string;
  plan?: P.Plan;
}

/** The line at the top of the list after a press (round 1, K1). It has a state of its own and the page's re-read never
 *  clears it: it ends only on the next press, an Undo, or a reload. */
export type Receipt = (P.PressReceipt & { saved: boolean; savedText?: string }) | { kind: 'said'; text: string };

/** Save for a model open live from a workspace: the changes are written back to that workspace the way the Publish page writes them
 *  (compare, then apply the ticked changes; a removal stays a choice and is not sent). One press, no question, and the engine keeps
 *  a restore point. Undo of the run still works on the page; the workspace has its own earlier version in Publish. */
export async function saveToService(): Promise<{ ok: boolean; text: string }> {
  const info = await rpc<SessionInfo>('sessionInfo').catch(() => null);
  const context = await rpc<ModelConnectionContext>('connectionContext').catch(() => null);
  if (!info) return { ok: false, text: SAVE_NOT_DONE };
  const seed = compareSeedFromSession(info, null, context);
  const right = seed.right && seed.right.kind === 'workspace' ? seed.right : null;
  const place = right?.label || info.liveDatabase || 'the workspace';
  if (!right) return { ok: false, text: 'Nothing was saved. This model has no workspace to save to. Open Publish to choose one.' };
  try {
    const diff = await rpc<ModelDiff>('compareModels', seed.left, right);
    if (diff?.error) return { ok: false, text: `Nothing was saved to ${place}. ${diff.error}` };
    const refs = (diff?.items ?? []).filter((i) => i.action !== 'Equal' && i.action !== 'Delete').map((i) => i.ref);
    if (refs.length === 0) return { ok: true, text: `Saved. ${place} already has these.` };
    const r = await rpc<ApplyDiffResult>('applyDiff', seed.left, right, refs, true, 'human');
    if (r?.error) return { ok: false, text: `Nothing was saved to ${place}. ${r.error}` };
    if (!r?.applied) return { ok: false, text: `Nothing was saved to ${place}.` };
    return { ok: true, text: `Saved ${r.count === 1 ? '1 change' : `${r.count} changes`} to ${place}.` };
  } catch (failure: unknown) {
    return { ok: false, text: `Nothing was saved to ${place}. ${messageOf(failure)}` };
  }
}

/** The one state word in the product's plan vocabulary: a word in the muted colour, a dot only where it waits on
 *  the reader. Used by the History list. */
export function Pill({ tone, children, title }: { tone: string; children: React.ReactNode; title?: string }) {
  const dot = P.PILL_TONE[tone] ?? 'none';
  return <span className={dot === 'none' ? 'sem-chip' : `sem-chip sem-chip-${dot}`} title={title}>{children}</span>;
}

function messageOf(error: unknown) { return error instanceof Error ? error.message : String(error); }

/** A model with no tables: its ways to start (ModelPlanView.Starts). Drawn whenever the engine sends them, whatever
 *  rows are open, and in place of "Nothing to do on the plan" when none is. */
export function PlanStarts({ starts, onStart }: { starts: P.PlanStart[]; onStart: (s: P.PlanStart) => void }) {
  return <section className="pw-starts" data-testid="plan-starts">
    <div className="pw-grp"><span>This model is empty. Start it one of these ways.</span></div>
    {starts.map((s) => <div key={s.id} className="pw-start" data-testid={`plan-start-${s.id}`}>
      <div className="pw-start-text"><strong>{s.label}</strong><span className="plan-muted">{s.line}</span></div>
      <button type="button" className={`sem-btn sem-btn-sm${s.id === 'sql' ? ' sem-btn-primary' : ''}`}
        data-testid={`plan-start-${s.id}-go`} onClick={() => onStart(s)}>{s.id === 'sql' ? 'Start' : 'Create'}</button>
    </div>)}
  </section>;
}

/** The run area (design section 2). It always says what is left, in words: never a grey button with a hidden reason.
 *    Semanticus has rows       Run the plan 3
 *    running                   Running 2 of 3, with a bar (the rows show their own step)
 *    only assistant rows       Your assistant has 3 tasks, and Copy request
 *    only your rows            2 tasks need your choice. Open one to see what to choose.
 *    nothing open              Plan done
 *  On free the Pro line stands where the button stands, as before. `unknown` draws neither: a paying customer must not
 *  meet an upsell for one frame while the licence is being read. */
export function RunArea({ plan, grant, running, held, steps, onRun, onTell, told }: {
  plan: P.Plan; grant: PlanRankingGrant; running: boolean; held?: readonly string[]; steps?: Readonly<Record<string, P.RunPhase>>;
  onRun: () => void; onTell?: () => void; told?: boolean;
}) {
  if (grant === 'denied') {
    // A line in the Next header, not an inset: the tag, the sentence, and the one live action.
    return <span className="plan-pro" data-testid="plan-pro-line">
      <span className="plan-pro-tag">Pro</span>
      <span className="plan-muted">Pro runs the plan for you, as one step you can undo.</span>
      <button type="button" className="sem-btn sem-btn-sm" data-testid="plan-unlock" onClick={upgradeToPro}>Unlock Pro</button>
    </span>;
  }
  if (grant !== 'granted') return <span className="plan-muted">{PENDING_ACCESS}</span>;
  if (running) {
    const w = P.runningWords(held ?? [], steps ?? {});
    return <span className="sem-btn sem-btn-sm pw-run pw-run-live" role="status" aria-live="polite" data-testid="plan-run-live">
      {w.text}<span className="pw-run-bar" style={{ width: `${Math.round(w.fraction * 100)}%` }} /></span>;
  }
  const area = P.runAreaOf(plan.items);
  if (area.kind === 'run') {
    // The count is the whole press: the ready tasks and the ones that follow them in order. The tip says so, so it matches the strip's counts.
    const ready = P.runSet(plan.items).length, after = area.count - ready;
    return <button type="button" className="sem-btn sem-btn-sm sem-btn-primary" data-testid="plan-run"
      title={`Runs ${area.count} as one step you can undo.${after > 0 ? ` ${ready} ${ready === 1 ? 'is' : 'are'} ready now. The other ${after} run after ${after === 1 ? 'it' : 'them'}.` : ''}`}
      onClick={onRun}>Run the plan <span className="pw-run-n sem-num">{area.count}</span></button>;
  }
  if (area.kind === 'assistant') {
    return <span className="pw-runsay" data-testid="plan-run-assistant">
      <span className="pw-state-mark pw-mark-agent" aria-hidden="true" />
      <span>Your assistant has <b className="sem-num">{area.count}</b> {area.count === 1 ? 'task' : 'tasks'}</span>
      <button type="button" className="sem-btn sem-btn-sm" data-testid="plan-tell" title="Copies a request. Paste it into your assistant to start these tasks." onClick={onTell}>Copy request</button>
      {told && <span className="plan-muted" role="status" data-testid="plan-told">Copied. Paste it into your assistant.</span>}
    </span>;
  }
  if (area.kind === 'you') {
    const n = plan.items.filter((i) => P.isOpenRow(i) && P.doerOf(i) === 'you').length;
    return <span className="plan-muted" data-testid="plan-run-you">{n === 1 ? 'The remaining task needs' : 'The remaining tasks need'} your choice. Open {n === 1 ? 'it' : 'one'} to see what to choose.</span>;
  }
  if (area.kind === 'done') return <span className="pw-state pw-state-good" data-testid="plan-run-done"><span className="pw-state-mark" aria-hidden="true" /><span className="pw-state-w">Plan done</span></span>;
  return null;
}

/** What a press left, at the top of the list: "Ran 3 rows. 38 changes, one undo step." with Undo, Save and See in History.
 *  Held in the page's own state, so the page's re-read of the model never clears it. Undo is offered only while the model
 *  has not moved on since the press, because it takes back everything from the press's first batch on. */
export function RunReceipt({ receipt, canUndo, canSave, busy, onUndo, onSave, onHistory }: {
  receipt: Receipt; canUndo: boolean; canSave: boolean; busy: boolean; onUndo: () => void; onSave: () => void; onHistory: () => void;
}) {
  if (receipt.kind === 'said') {
    return <div className="pw-receipt" role="status" data-testid="plan-run-receipt"><span>{receipt.text}</span></div>;
  }
  return <div className="pw-receipt" role="status" data-testid="plan-run-receipt">
    <span className="pw-receipt-ok">{P.ranWords(receipt.rows)}</span>
    <span>{P.changesWords(receipt.changes)}</span>
    {receipt.saved && <span className="pw-receipt-ok" data-testid="plan-run-saved">{receipt.savedText ?? 'Saved.'}</span>}
    {receipt.note && <span className="plan-muted">{receipt.note}</span>}
    {!canUndo && receipt.entryId && <span className="plan-muted">The model has changed since. History can take it back.</span>}
    <span className="pw-grp-r">
      {canUndo && <button type="button" className="sem-btn sem-btn-sm" data-testid="plan-run-undo" disabled={busy} onClick={onUndo}>Undo</button>}
      {canSave && !receipt.saved && <button type="button" className="sem-btn sem-btn-sm" data-testid="plan-run-save" disabled={busy} onClick={onSave}>Save</button>}
      <button type="button" className="sem-btn sem-btn-sm pw-quiet" data-testid="plan-run-history" onClick={onHistory}>See in History</button>
    </span>
  </div>;
}

type View = 'work' | 'hist';

// ---- the page ---------------------------------------------------------------------------------------

/** Draws nothing. When the order line starts or stops saying the Pro plan is not active, the header chip reads the plan again, so the two agree in the same moment. */
export function ProPlanWatch({ inactive }: { inactive: boolean }) {
  const last = useRef(inactive);
  useEffect(() => {
    if (last.current === inactive) return;
    last.current = inactive;
    refreshEntitlement();
  }, [inactive]);
  return null;
}

export function PlanView({ sessionId, revision, changeNonce, modelName, hostSession, compatibilityLevel, focus, focusNonce, onGo,
  onGoObject, track: trackSeed, trackNonce, focusObject, onClearObject, historyEntry = null, historyNonce, openRow = null, openRowNonce,
  onRunUpdate, fieldTakenNonce, homeNonce, draftStartSql, onDraftStartTaken, showStart, onShowStartTaken }: {
  sessionId: string;
  revision?: number;
  /** Bumped when this webview is told the model changed; the plan is recomputed from the model. */
  changeNonce: number;
  modelName?: string;
  /** The host's session: the facts line reads it, and the spec editor panel is handed it. */
  hostSession?: { modelName?: string; tables?: number; measures?: number; source?: string; liveKind?: string;
    liveBound?: boolean; hasUnsavedChanges?: boolean; hasUnsavedEdits?: boolean } | null;
  /** The model's compatibility level; absent on an engine that does not report it, which means "not known to be
   *  blocking". The facts line offers Raise compatibility level only while the level blocks a named capability. */
  compatibilityLevel?: number;
  /** Which panel a route asked this page to open over the plan. Undefined is the plan itself. */
  focus?: 'changes' | 'draft';
  /** A route asked for the draft panel ON ITS SQL STEP (route.ts SQL_DRAFT_TARGET): the route's nonce, 0 for none. */
  draftStartSql?: number;
  onDraftStartTaken?: () => void;
  /** A route asked for the Plan's start view (route.ts PLAN_START_TARGET): the route's nonce, 0 for none. The start plan when there is one, else the start form. */
  showStart?: number;
  onShowStartTaken?: () => void;
  focusNonce?: number;
  onGo: (tool: string, target?: string) => void;
  /** The object line's navigation, which can hand the Diagram the tables to add (the Overview's, contract 11.1). */
  onGoObject?: (tool: string, target?: string, addTables?: string[]) => void;
  /** A track to open filtered to (the "Your assistant" chip's count opens the assistant's). */
  track?: P.PlanDoer | null;
  trackNonce?: number;
  /** The object selected in the model tree: the list shows its rows under "Showing <object>". */
  focusObject?: { ref: string; label: string } | null;
  onClearObject?: () => void;
  /** A link into History (contract 11.2), bumped by `historyNonce`: the old Edit History id, the object line's
   *  History, or an entry to open ("Show this edit"). Opening the Plan with it switches to the History view. */
  historyEntry?: string | null;
  historyNonce?: number;
  /** A row to open, bumped by `openRowNonce`: Ctrl J opens the Plan on the draft it just made (lane U5). */
  openRow?: string | null;
  openRowNonce?: number;
  /** A run's step moved from its row (lane W2): folded into the shell's run map, so the header chip follows. */
  onRunUpdate?: (r: WorkflowRunView) => void;
  /** Bumped when Ctrl J carried the field's words into the ask box and a draft was made from them: the field empties,
   *  because those words are now the draft (journey m11 F7). */
  fieldTakenNonce?: number;
  /** Bumped when the Plan area button is pressed: the page goes back to its Work view (journey m11 F2). */
  homeNonce?: number;
}) {
  const [plan, setPlan] = useState<P.Plan | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [busy, setBusy] = useState<string | null>(null);
  const [actionError, setActionError] = useState<string | null>(null);
  const [sentence, setSentence] = useState<string | null>(null);
  // THE RUN (round 1, K1). Each lives in its own state, apart from `sentence`, which the page's own re-read clears: the
  // receipt must still be on screen after the re-read that every press starts.
  /** What the last press did. Ends on the next press, an Undo, a model change of session, or a reload; never on a re-read. */
  const [receipt, setReceipt] = useState<Receipt | null>(null);
  /** Each row's step during a run, from plan/runProgress. A finished row keeps its Done and its change count. */
  const [steps, setSteps] = useState<Record<string, P.RunPhase>>({});
  const [stepAt, setStepAt] = useState(() => Date.now());
  const stepStamp = useRef<Record<string, number>>({});
  /** The rows this press holds, in the order the plan lists them: those not reached yet read In this run. */
  const [pressRows, setPressRows] = useState<string[]>([]);
  /** Rows that turned done while the page was open (a run, or the assistant): listed in Done just now. */
  const [justDone, setJustDone] = useState<ReadonlySet<string>>(() => new Set());
  const [told, setTold] = useState(false);
  const [foldOpen, setFoldOpen] = useState(false);
  /** The row whose Repeat... was chosen in the dots: its repeat editor is drawn in its opened detail. */
  const [repeatRow, setRepeatRow] = useState<string | null>(null);
  const prevPlan = useRef<P.Plan | null>(null);
  const lastSeen = useRef(new Map<string, P.PlanItem>());
  /** The last row removed, while its toast offers Undo (Kane's UAT, 25 Sep). */
  const [removed, setRemoved] = useState<{ id: string; title: string; told: boolean } | null>(null);
  useEffect(() => {
    if (!removed) return undefined;
    const t = setTimeout(() => setRemoved(null), 20000);
    return () => clearTimeout(t);
  }, [removed]);
  const [retry, setRetry] = useState(0);
  const generation = useRef(0);

  // A different model is a different plan: nothing of the last press belongs to it.
  useEffect(() => {
    setReceipt(null); setSteps({}); setPressRows([]); setJustDone(new Set()); setRepeatRow(null);
    stepStamp.current = {}; prevPlan.current = null; lastSeen.current = new Map();
  }, [sessionId]);
  // plan/runProgress: the engine says each step of each row. Several rows of one batch share a step.
  useEffect(() => onPlanRunProgress((raw) => {
    const at = Date.now();
    const ids = ((raw as { itemIds?: unknown } | null)?.itemIds ?? []) as unknown[];
    for (const id of ids) if (typeof id === 'string') stepStamp.current[id] = at;
    setStepAt(at);
    setSteps((prev) => P.foldProgress(prev, raw));
    const e = raw as { phase?: string; itemIds?: string[] } | null;
    if (e?.phase === 'done' && Array.isArray(e.itemIds)) setJustDone((prev) => new Set([...prev, ...e.itemIds!]));
  }), []);
  // A step that began and never ended (a run that died, or another door's) stops showing after three minutes.
  useEffect(() => {
    if (!Object.values(steps).some((x) => x.step === 'work' || x.step === 'apply' || x.step === 'check')) return undefined;
    const t = setInterval(() => setStepAt(Date.now()), 15000);
    return () => clearInterval(t);
  }, [steps]);
  // Every row that turns done while the page is open joins Done just now: a press, or the assistant on its own.
  useEffect(() => {
    if (!plan) return;
    const before = prevPlan.current;
    prevPlan.current = plan;
    for (const i of plan.items) lastSeen.current.set(i.id, i);
    if (!before) return;
    const was = new Map(before.items.map((i) => [i.id, P.stateWordOf(i)]));
    const turned = plan.items.filter((i) => P.stateWordOf(i) === 'done' && was.has(i.id) && was.get(i.id) !== 'done').map((i) => i.id);
    if (turned.length > 0) setJustDone((prev) => new Set([...prev, ...turned]));
  }, [plan]);

  // ROWS STAY STILL UNDER THE POINTER (UAT lane p, 26 Sep). The plan re-reads by itself when Jev lands, a batch
  // finishes or the model moves elsewhere, and a row can appear, go or grow ABOVE the one a person is about to press:
  // on lane p a click aimed at Approve landed on another row's Pause. The browser's own scroll anchoring keeps the TOP
  // of the screen still, not the place the pointer is. So before such a quiet update the row under the pointer (else
  // the open row) is remembered with its position, and after the new plan is drawn the scroller moves by however far
  // that row moved. The element under the pointer is held, not only its row's header: a line's Keep or Drop inside an
  // open row moves when that row grows above it, though the header does not. Only for updates the person did not
  // make: after their own press the page is theirs to move.
  const pointer = useRef<{ x: number; y: number } | null>(null);
  useEffect(() => {
    const on = (e: PointerEvent) => { pointer.current = { x: e.clientX, y: e.clientY }; };
    window.addEventListener('pointermove', on, { passive: true });
    return () => window.removeEventListener('pointermove', on);
  }, []);
  const held = useRef<P.PointerAnchor | null>(null);
  const openRef = useRef<string | null>(null);
  const setPlanQuietly = useCallback((next: P.Plan) => {
    held.current = P.rowUnderPointer(pointer.current, openRef.current);
    setPlan(next);
  }, []);
  useLayoutEffect(() => {
    const was = held.current;
    held.current = null;
    if (!was) return;
    // The element that was under the pointer, when the update kept it (React keeps a node whose key held), else its row.
    const kept = was.el && was.el.isConnected ? was.el : null;
    const row = kept ?? document.querySelector(`[data-row="${CSS.escape(was.id)}"]`);
    if (!row) return;
    const moved = row.getBoundingClientRect().top - (kept ? was.elTop : was.top);
    if (Math.abs(moved) < 1) return;
    P.scrollerOf(row).scrollTop += moved;
  }, [plan]);

  // A restore started in History remounts this page on the reopened model; it comes back where the person was.
  const [view, setView] = useState<View>(() => (peekRestoreLanding(sessionId) ? 'hist' : 'work'));
  // A row's "Open in History" (contract 1.6) opens History on that edit; a route's own entry still wins when it moves.
  const [rowEntry, setRowEntry] = useState<string | null>(null);
  const [track, setTrack] = useState<P.PlanDoer | null>(trackSeed ?? null);
  const [goal, setGoal] = useState<P.PlanGoal>('all');
  const [showAll, setShowAll] = useState(false);
  // The rows Next shows: as many as fit the window, never fewer than NEXT_CAP (the space rule, 27 September).
  const [fitCap, setFitCap] = useState(P.NEXT_CAP);
  const [room, setRoom] = useState(0);   // bumped whenever the room for Next can have changed
  const listRef = useRef<HTMLElement | null>(null);
  const [openId, setOpenId] = useState<string | null>(null);
  openRef.current = openId;
  const [field, setField] = useState('');
  const [panel, setPanel] = useState<'none' | 'changes' | 'draft' | 'start'>('none');
  // A new model from a SQL source lives here as a start plan (lane sf-studio): read, decided and approved before it exists.
  const sp0 = useStartPlan(sessionId, revision, changeNonce);
  // The plan belongs only to the model it was made for: one from an earlier SQL start stays out of a model opened live.
  const sp = { ...sp0, view: startPlanBelongs(sp0.view, hostSession) ? sp0.view : null };
  const startPlanActive = sp.view != null;
  // A built plan is a band over this Plan; "See what was built" puts the whole plan back on the page (K25).
  const [showBuilt, setShowBuilt] = useState(false);
  const startMode = startPageMode(sp.view, showBuilt);
  useEffect(() => { if (!sp.view?.built) setShowBuilt(false); }, [sp.view?.built]);
  // What a finished build says, kept apart from `sentence`: the build moves the model's revision, and the plan's own re-read
  // clears `sentence`, which would erase the one line that tells the person the build happened and how to take it back.
  const [builtNote, setBuiltNote] = useState<string | null>(null);

  const grant: PlanRankingGrant = planRankingOf(useTier());
  const pro = grant === 'granted';
  // The generic permission card stands aside while a start plan is open: the start plan asks its own, naming the source
  // and the tables Jev may look at (S2), and a card about "this model's shape" says nothing about the hidden source.
  useConsentCard(pro && plan != null && plan.ranked !== true && !startPlanActive, () => setRetry((n) => n + 1));
  // The field asks where the very words Enter adds are done (P.fieldSubmit), so the page it shows is the page for the
  // row Enter adds, and Enter sends it along (addTask below; verify-askdata-r6 F1).
  const suggestion = useAskSuggestion(pro ? P.fieldSubmit(field)?.words ?? '' : '');

  useEffect(() => { if (trackNonce !== undefined) { setTrack(trackSeed ?? null); setView('work'); } }, [trackSeed, trackNonce]);
  useEffect(() => { if (historyNonce !== undefined) { setRowEntry(null); setView('hist'); } }, [historyEntry, historyNonce]);
  useEffect(() => { if (fieldTakenNonce !== undefined) setField(''); }, [fieldTakenNonce]);
  // Only a press made while this page is up counts. A page that mounts already carrying an older press must not
  // undo the History a route opened it on (the old Edit History id), since a fresh page starts on Work anyway.
  const seenHome = useRef(homeNonce);
  useEffect(() => {
    if (homeNonce === undefined || homeNonce === seenHome.current) return;
    seenHome.current = homeNonce;
    setView('work');
  }, [homeNonce]);
  const scrollTo = useRef<string | null>(null);
  useEffect(() => {
    if (openRowNonce === undefined || !openRow) return;
    // The draft was made after the plan was read, so the plan is read again for its row.
    setView('work'); setTrack(null); setShowAll(true); setOpenId(openRow); setRetry((n) => n + 1);
    scrollTo.current = openRow;
  }, [openRow, openRowNonce]);
  // The row asked for is scrolled to once it is DRAWN. A frame after the ask was too early whenever the plan's read
  // landed later, which it does on a real engine: "On the plan" opened the row about 1280px down and left the
  // person at the top of the page (journey m11-2, F4).
  useEffect(() => {
    const id = scrollTo.current;
    if (!id) return;
    const row = document.querySelector(`[data-row="${CSS.escape(id)}"]`);
    if (!row) return;
    scrollTo.current = null;
    row.scrollIntoView({ block: 'start' });
  });

  useEffect(() => {
    const mine = ++generation.current;
    setLoading(true); setError(null); setSentence(null);
    rpc<P.Plan>('getModelPlan').then(
      (value) => { if (mine === generation.current) { setPlanQuietly(normalize(value)); setLoading(false); } },
      (failure: unknown) => { if (mine === generation.current) { setError(messageOf(failure)); setLoading(false); } },
    );
    return () => { if (generation.current === mine) generation.current++; };
  }, [sessionId, revision, changeNonce, retry]);

  // THE ROUTE DECIDES THE PANEL, every time, including when it asks for none (Codex, PR 388): the shell renders all
  // three route ids through one PlanView, so asking for the plan has to close a panel left open.
  useEffect(() => { setPanel(P.panelFor(focus)); }, [focus, focusNonce]);
  // A route asked for the SQL start (route.ts SQL_DRAFT_TARGET). A new, empty model starts as a start plan; a model that
  // already has tables keeps the wizard's two-step path in the draft panel, and a start plan already open is simply shown.
  useEffect(() => {
    if (!draftStartSql) return;
    if (sp.view) { setPanel('none'); onDraftStartTaken?.(); return; }
    if (typeof hostSession?.tables === 'number' && hostSession.tables === 0) { setPanel('start'); onDraftStartTaken?.(); }
  }, [draftStartSql]);  // eslint-disable-line react-hooks/exhaustive-deps

  // The Studio's plan/start place (the assistant shows its start plan, or the person presses Show me): leave History for the work view, then put the start
  // plan on the page, or the start form when none exists yet. It waits for the first read of the start plan, so it does not guess.
  const [startAsk, setStartAsk] = useState(0);
  useEffect(() => {
    if (!showStart) return;
    setView('work'); setRowEntry(null); setStartAsk(showStart); onShowStartTaken?.();
  }, [showStart]);  // eslint-disable-line react-hooks/exhaustive-deps
  useEffect(() => {
    if (!startAsk || !sp.loaded) return;
    setStartAsk(0);
    if (sp.view) { setPanel('none'); setShowBuilt(true); } else setPanel('start');
  }, [startAsk, sp.loaded, sp.view]);  // eslint-disable-line react-hooks/exhaustive-deps

  // The plan also moves without the model moving: a task, the switch, a hold, Jev landing. The engine says so with
  // plan/didChange, and the page re-reads quietly. Debounced, because one landing can publish twice.
  useEffect(() => {
    let timer: ReturnType<typeof setTimeout> | undefined;
    const off = onPlanChange(() => {
      if (timer) clearTimeout(timer);
      timer = setTimeout(() => {
        const mine = generation.current;
        rpc<P.Plan>('getModelPlan').then((value) => { if (mine === generation.current) setPlanQuietly(normalize(value)); }, () => {});
      }, 150);
    });
    return () => { off(); if (timer) clearTimeout(timer); };
  }, []);

  /** One person control. Every one of them answers with the whole plan, which is the answer. */
  const run = useCallback(async (key: string, method: string, ...args: unknown[]): Promise<{ ok: boolean; value: P.Plan | PlanActionResult | null }> => {
    setBusy(key); setActionError(null); setSentence(null);
    try {
      const value = await rpc<P.Plan | PlanActionResult>(method, ...args);
      const next = value && 'items' in (value as P.Plan) ? value as P.Plan : (value as PlanActionResult | null)?.plan;
      if (next) setPlan(normalize(next));
      const said = (value as PlanActionResult | null)?.sentence;
      if (said && !('items' in (value as P.Plan))) setSentence(said);
      return { ok: true, value };
    } catch (failure: unknown) {
      setActionError(messageOf(failure));
      return { ok: false, value: null };
    } finally {
      setBusy(null);
    }
  }, []);
  const control = useCallback(async (key: string, method: string, ...args: unknown[]) => (await run(key, method, ...args)).value, [run]);
  /** The same call, answered true only when the engine took it. A null answer can mean a refusal or an empty answer, so it cannot tell; this can (round 3: a form that sent words ends them only on true). */
  const controlOk = useCallback(async (key: string, method: string, ...args: unknown[]) => (await run(key, method, ...args)).ok, [run]);

  /** Refresh plan: the engine scans again, adds what is new and clears the finished rows; its sentence is the answer. The rows
   *  that finished while the page was open are finished rows too, so they leave the list with the rest. */
  const refresh = useCallback(async () => {
    const r = await run('refresh', 'refreshPlan');
    if (r.ok) setJustDone(new Set());
  }, [run]);

  /** Enter on the field: a row, never a trip somewhere else. It starts with your assistant (Kane, 24 Sep), and
   *  "I'll do it" is one click away in its detail. */
  const addTask = useCallback(async (text: string) => {
    const asked = P.fieldSubmit(text);
    if (!asked) return;
    // The page the field showed for these very words goes with them, so the row names it too (verify-askdata-r6 F1).
    // The suggestion is only ever the answer for the words now in the field (useAskSuggestion), so it cannot be stale.
    const fieldPage = fieldTaskPage(suggestion);
    const result = await control('field', 'addPlanTask', fieldPage ? { words: asked.words, fieldPage } : { words: asked.words }) as PlanActionResult | null;
    // The new row is last in Next, so the list opens out to show it, opened with its doer switch.
    if (result) {
      setField(''); setShowAll(true);
      if (result.itemId) {
        setOpenId(result.itemId);
        // The new row is last; bring it into view once it has drawn, so Enter visibly did something.
        const id = result.itemId;
        requestAnimationFrame(() => document.querySelector(`[data-row="${CSS.escape(id)}"]`)?.scrollIntoView({ block: 'center' }));
      }
    }
  }, [control, suggestion]);

  /** Draft the steps (lane U5; Plan-heart contract 14): a row from your words becomes a composer draft. The draft
   *  opens under its own row; the task it came from is set aside with the reason, one click from Put back. */
  const draftSteps = useCallback(async (item: P.PlanItem) => {
    setBusy(`${item.id}:draft`); setActionError(null); setSentence(null);
    try {
      const v = await rpc<DraftView>('composeTask', item.title, null, null, null, item.title, 'human');
      if (!v?.draftId) throw new Error('Semanticus made no draft.');
      await rpc('skipModelPlanItem', item.id, 'Drafted as steps.');
      setPlan(normalize(await rpc<P.Plan>('getModelPlan')));
      const row = draftRowId(v.draftId);
      setShowAll(true); setOpenId(row);
      requestAnimationFrame(() => document.querySelector(`[data-row="${CSS.escape(row)}"]`)?.scrollIntoView({ block: 'start' }));
    } catch (failure: unknown) { setActionError(messageOf(failure)); }
    finally { setBusy(null); }
  }, []);

  /** Keep or drop one line of a change set: setPlanItem(lineId, after, approved). It answers with a change plan, not
   *  the plan, so the plan is read again (Astra finding 8). */
  const decideLine = useCallback(async (lineId: string, kept: boolean) => {
    if (!lineId) return;
    setBusy(`line:${lineId}`); setActionError(null); setSentence(null);
    try {
      await rpc('setPlanItem', lineId, null, kept);
      setPlan(normalize(await rpc<P.Plan>('getModelPlan')));
    } catch (failure: unknown) { setActionError(messageOf(failure)); }
    finally { setBusy(null); }
  }, []);

  /** Keep all or Drop all: one press, the same setPlanItem per line the single Keep sends, then ONE read of the plan.
   *  Approve still applies the kept set in one step. */
  const decideAll = useCallback(async (set: P.PlanChangeSet, kept: boolean) => {
    const refs = P.bulkRefs(set, kept);
    if (refs.length === 0) return;
    setBusy(`lines:${kept ? 'keep' : 'drop'}`); setActionError(null); setSentence(null);
    try {
      for (const ref of refs) await rpc('setPlanItem', ref, null, kept);
      setPlan(normalize(await rpc<P.Plan>('getModelPlan')));
    } catch (failure: unknown) { setActionError(messageOf(failure)); }
    finally { setBusy(null); }
  }, []);

  /** Run the plan: ONE press, every wave. Each wave is one engine call; a row that only waited on a row of the same press is
   *  Ready by the next read, so the loop calls again with the new readyDigest (plan-model runAllWaves). */
  const runPlan = useCallback(async () => {
    if (!plan) return;
    const hold = P.pressSet(plan.items).map((i) => i.id);
    if (hold.length === 0 || !P.runCall(plan)) return;
    setBusy('run'); setActionError(null); setSentence(null);
    setReceipt(null); setSteps({}); setJustDone(new Set()); setPressRows(hold); setTold(false);
    stepStamp.current = {};
    try {
      const out = await P.runAllWaves(plan,
        async (call) => {
          const [method, ...args] = call;
          const r = await rpc<ApplyItemsResult>(method, ...args);
          return r?.plan ? { ...r, plan: normalize(r.plan) } : r;
        },
        async () => normalize(await rpc<P.Plan>('getModelPlan')),
        (next) => setPlan(normalize(next)));
      setPlan(normalize(out.plan));
      setReceipt(out.receipt ? { ...out.receipt, saved: false }
        : { kind: 'said', text: out.refusal ?? 'Nothing was ready to run.' });
    } catch (failure: unknown) { setActionError(messageOf(failure)); }
    finally {
      setBusy(null); setPressRows([]);
      // What is left of the steps is what finished: a row's Done and its change count stay beside it.
      setSteps((prev) => Object.fromEntries(Object.entries(prev).filter(([, x]) => x.step === 'done' || x.step === 'skipped')));
    }
  }, [plan]);

  /** The receipt's Undo takes back the whole press: undo('human', firstEntryId) walks back through every batch from the first. */
  const undoPress = useCallback(async () => {
    if (!receipt || receipt.kind !== 'ran' || !receipt.entryId) return;
    setBusy('undo'); setActionError(null);
    try {
      await rpc('undo', 'human', receipt.entryId);
      setReceipt({ kind: 'said', text: 'Undone. The model is back as it was before you pressed Run.' });
      setJustDone(new Set()); setSteps({});
      setPlan(normalize(await rpc<P.Plan>('getModelPlan')));
    } catch (failure: unknown) { setActionError(messageOf(failure)); }
    finally { setBusy(null); }
  }, [receipt]);

  /** One press saves a model opened from files. Nothing saves by itself; a live model is never published from here. */
  const savePress = useCallback(() => {
    setBusy('save'); setActionError(null);
    // A live model saves to its workspace; any other model saves the way Ctrl+S does (the host asks where, for one never saved).
    const live = !!hostSession?.liveBound;
    const done = live
      ? saveToService().then((r) => { if (!r.ok) setActionError(r.text); else setReceipt((p) => (p && p.kind === 'ran' ? { ...p, saved: true, savedText: r.text } : p)); })
      : saveModelFromPage().then((saved) => {
        if (!saved) setActionError(SAVE_NOT_DONE);
        else setReceipt((r) => (r && r.kind === 'ran' ? { ...r, saved: true } : r));
      });
    void done.finally(() => setBusy(null));
  }, [hostSession?.liveBound]);

  const tellAssistant = useCallback(() => {
    void copyText(P.TELL_ASSISTANT).then((ok) => { if (ok) { setTold(true); setTimeout(() => setTold(false), 4000); } });
  }, []);

  const onAction: RowActionHandler = useCallback((item, action, text) => {
    const key = `${item.id}:${action}`;
    switch (action) {
      case 'up': void control(key, 'movePlanTaskUp', item.id); break;
      case 'done': return controlOk(key, 'completePlanTask', item.id, text || null);
      case 'aside': void control(key, 'skipModelPlanItem', item.id, 'Set aside by you.'); break;
      case 'putback': void control(key, 'unskipModelPlanItem', item.id); break;
      // THE DIGEST IS LAST, after the origin: modelPlanAction(id, action, origin, digest).
      case 'approve': void control(key, 'modelPlanAction', item.id, 'approve', 'human', item.changeSet?.digest ?? ''); break;
      // The Save row saves (plan-model savesOnRow). The plan redraws itself when the save rides the change bus.
      case 'save': setBusy(key); setActionError(null);
        void saveModelFromPage().then((saved) => { if (!saved) setActionError(SAVE_NOT_DONE); }).finally(() => setBusy(null)); break;
      case 'open': setOpenId(item.id); break;
      // Repeat... in the dots opens the row and draws the repeat editor in its detail.
      case 'repeat': setOpenId(item.id); setRepeatRow(item.id); break;
      case 'draft': if (grant === 'granted') void draftSteps(item); else upgradeToPro(); break;
      // Remove, person-only on the engine: the row goes, the toast offers Undo (restorePlanRow).
      case 'remove': void control(key, 'removePlanRow', item.id).then((value) => {
        const r = value as { itemId?: string; title?: string; toldAssistant?: boolean } | null;
        if (!r?.itemId) return;
        setOpenId((id) => (id === item.id ? null : id));
        setSentence(null);
        setRemoved({ id: r.itemId, title: r.title || item.title, told: !!r.toldAssistant });
      }); break;
    }
  }, [control, controlOk, draftSteps, grant]);

  /** The doer menu: one choice, one call (plan-model doerCall). */
  const onDoer = useCallback((row: P.PlanItem, doer: P.PlanDoer) => {
    const call = P.doerCall(row, doer);
    if (!call) return;
    const [method, ...args] = call;
    void control(`${row.id}:doer`, method, ...args);
  }, [control]);

  const undoRemove = useCallback(async () => {
    if (!removed) return;
    const back = await control(`${removed.id}:restore`, 'restorePlanRow', removed.id);
    if (back) { setRemoved(null); setSentence(null); }
  }, [control, removed]);

  const planItems = plan?.items ?? [];
  // A row that turned done and that a re-read no longer lists is drawn from the last time it was seen, as Done: Done just
  // now never loses a row the run finished.
  const items = useMemo(() => {
    const here = new Set(planItems.map((i) => i.id));
    const ghosts = [...justDone].filter((id) => !here.has(id) && lastSeen.current.has(id))
      .map((id) => ({ ...lastSeen.current.get(id)!, stateWord: 'done' as const, status: 'done' as const, state: 'done' as const }));
    // During the press the page still holds the plan read before it: a row the engine reported done is drawn done at once.
    const shown = busy === 'run'
      ? planItems.map((i) => (steps[i.id]?.step === 'done' && !P.stateWordOf(i).match(/^(done|checking)$/u) ? { ...i, stateWord: 'done' as const, status: 'done' as const, state: 'done' as const } : i))
      : planItems;
    return ghosts.length === 0 ? shown : [...shown, ...ghosts];
  }, [planItems, justDone, busy, steps]);
  // The steps a run reported, without any that began long ago and never ended.
  const liveSteps = useMemo(() => Object.fromEntries(Object.entries(steps).filter(([id, x]) =>
    x.step === 'done' || x.step === 'skipped' || stepAt - (stepStamp.current[id] ?? stepAt) < 180000)), [steps, stepAt]);
  const holding = useMemo(() => (busy === 'run' ? new Set(pressRows) : null), [busy, pressRows]);
  // The open row keeps the place in the fold it had when it was opened, until it is closed (journey m11 F8). The place is
  // taken the first time the open row is seen there, from the list as it would be drawn anyway.
  const kept = useRef<P.KeptRow | null>(null);
  const work = useMemo(() => {
    const opts = { track, goal, showAll, cap: fitCap, object: focusObject?.ref ?? null, justDone };
    const plain = P.workView(items, opts);
    if (!openId) { kept.current = null; return plain; }
    if (kept.current?.id !== openId) {
      const at = plain.onlyYou.findIndex((r) => r.id === openId);
      kept.current = at >= 0 ? { id: openId, index: at, group: 'you' } : null;
    }
    // The open row stays drawn while it is open, wherever another writer moved it (past the cap of Next, or Done): what the person typed under it stays on screen (R-B).
    return withOpenRow(kept.current ? P.workView(items, { ...opts, keep: kept.current }) : plain, items, openId);
  }, [items, track, goal, showAll, fitCap, focusObject?.ref, openId, justDone]);
  useEffect(() => { if (repeatRow && openId !== repeatRow) setRepeatRow(null); }, [openId, repeatRow]);
  // Counted again from NEXT_CAP whenever the room can have changed: the window resizing, and the Studio text size
  // (Ctrl + wheel), which is CSS zoom on the Studio root and fires no window resize but does change the root's size in
  // its own pixels. Without the second, 100% to 80% left 224 px of empty page under a list that still hid rows behind
  // Show all, in a real window.
  useEffect(() => {
    const onResize = () => { setFitCap(P.NEXT_CAP); setRoom((n) => n + 1); };
    window.addEventListener('resize', onResize);
    const root = document.querySelector('.studio-root');
    let size = root ? `${root.clientWidth}x${root.clientHeight}` : '';
    const ro = root && typeof ResizeObserver !== 'undefined' ? new ResizeObserver(() => {
      const now = `${root.clientWidth}x${root.clientHeight}`;
      if (now !== size) { size = now; onResize(); }
    }) : null;
    if (root && ro) ro.observe(root);
    return () => { window.removeEventListener('resize', onResize); ro?.disconnect(); };
  }, []);
  // Grow Next into the empty space under it: the rows that fit between the Show all line and the bottom of the page's
  // scroll region, measured from a drawn row. Only while the page does not scroll. The room is what is left once every
  // bottom padding, border and margin between the list and <main> is taken off (the Plan page's own 32 px as well as
  // <main>'s: taking <main>'s alone grew one row too many and scrolled the page 15 px in a real window at 90%). Rects
  // are turned into the list's own pixels by the list's measured scale, so this is right in both zoom models.
  useLayoutEffect(() => {
    const list = listRef.current;
    if (!list || showAll || work.nextTotal <= work.next.length) return;
    const main = list.closest('main');
    const row = list.querySelector<HTMLElement>('[data-testid^="plan-row-"]');
    if (!main || !row || main.scrollHeight > main.clientHeight + 1 || !(list.offsetWidth > 0)) return;
    const scale = list.getBoundingClientRect().width / list.offsetWidth;
    let under = 0;
    for (let a: HTMLElement | null = list; a && a !== main; a = a.parentElement) {
      const cs = getComputedStyle(a);
      under += parseFloat(cs.marginBottom) || 0;
      if (a !== list) under += (parseFloat(cs.paddingBottom) || 0) + (parseFloat(cs.borderBottomWidth) || 0);
    }
    const ms = getComputedStyle(main);
    under += (parseFloat(ms.paddingBottom) || 0) + (parseFloat(ms.borderBottomWidth) || 0);
    const free = (main.getBoundingClientRect().bottom - list.getBoundingClientRect().bottom) / scale - under;
    const more = Math.floor(free / (row.getBoundingClientRect().height / scale));
    if (more > 0) setFitCap(work.next.length + more);
  }, [work, showAll, room]);
  const labelOf = useCallback((tool: string) => ASK_TOOL_LABEL[tool], []);

  if (loading && !plan) {
    return <div className="sem-evidence-page plan-page" data-testid="plan-page">
      <div className="model-home-state" role="status"><span className="sem-spin" /> <strong>Opening the plan…</strong>
        <span>Working out what this model still needs.</span></div>
    </div>;
  }
  if (error && !plan) {
    return <div className="sem-evidence-page plan-page" data-testid="plan-page">
      <div className="model-home-state model-home-error" role="alert"><strong>The plan could not be opened</strong>
        <span>{error}</span>
        <button type="button" className="sem-btn" onClick={() => setRetry((n) => n + 1)}>Try again</button></div>
    </div>;
  }

  if (panel === 'start' && !sp.view) {
    return <div className="sem-evidence-page plan-page" data-testid="plan-page" data-panel="start" data-pro={pro ? 'yes' : 'no'}>
      <div className="plan-line">
        <button type="button" className="sem-btn sem-btn-sm" data-testid="plan-panel-back" onClick={() => setPanel('none')}>Back to the plan</button>
        <b data-testid="plan-panel-title">A new model from a SQL source</b>
      </div>
      <StartFromSql pro={pro} onStarted={(v) => { sp.setView(v); setPanel('none'); }} />
    </div>;
  }
  if (panel !== 'none' && panel !== 'start') {
    return <div className="sem-evidence-page plan-page" data-testid="plan-page" data-panel={panel} data-pro={pro ? 'yes' : 'no'}>
      <div className="plan-line">
        <button type="button" className="sem-btn sem-btn-sm" data-testid="plan-panel-back"
          onClick={() => setPanel('none')}>Back to the plan</button>
        <b data-testid="plan-panel-title">{panel === 'draft' ? 'The draft for this model' : 'Every pending change'}</b>
        <span className="plan-muted">{panel === 'draft'
          ? 'A draft is kept apart from the model. Nothing is added until you build it.'
          : 'Nothing is written until you apply.'}</span>
      </div>
      {panel === 'draft'
        ? <Suspense fallback={<div className="model-home-state" role="status"><span className="sem-spin" /> <strong>Opening the draft…</strong></div>}>
          <SpecPanel session={hostSession ?? undefined} startSql={draftStartSql} onStartTaken={onDraftStartTaken} onStartPlan={() => setPanel('start')} />
        </Suspense>
        : <OptimizeView />}
    </div>;
  }

  const showPlan: P.Plan = plan ?? { items: [], history: [], tables: [] };
  const queue = showPlan.assistantQueue === 'paused' ? 'paused' : 'on';
  const updated = P.updatedLine(showPlan.strip);
  const order = P.orderLines(showPlan);
  const grade = P.gradeLine(showPlan.strip);
  const sees = track === 'assistant' ? showPlan.assistantSees ?? null : null;
  // On the assistant's track, the offer is numbered as the fold line numbers it, and two rows say what the switch
  // means for them (mockup section 5).
  const offered = track === 'assistant' ? P.offerNumbers(showPlan) : new Map<string, number>();
  const trackMeta = (item: P.PlanItem): string | null => {
    if (track !== 'assistant') return null;
    const w = P.stateWordOf(item);
    if (w === 'on_it' && item.claim && item.claim.claimedBy !== 'you') return queue === 'paused' ? 'Finishes, even when paused' : null;
    if (w === 'waiting') return 'Not offered yet';
    return null;
  };
  // A step belongs to the press that made it: once a row is open again (new findings under the same family), its Done is gone.
  const stepFor = (item: P.PlanItem): P.RunPhase | null => {
    const st = P.stepOf(item.id, liveSteps, holding);
    if (!st) return null;
    const w = P.stateWordOf(item);
    // While the press runs the plan on the page is the one read before it, so a row the engine reported done is Done here already.
    return st.step === 'done' && busy !== 'run' && w !== 'done' && w !== 'checking' ? null : st;
  };
  const rowProps = (item: P.PlanItem, first = false, recent = false) => ({
    item, items, busy, onAction, onDoer, onGo, labelOf, first,
    lead: offered.has(item.id) ? String(offered.get(item.id)) : null, meta: trackMeta(item),
    open: openId === item.id,
    step: stepFor(item),
    justDone: recent, free: grant === 'denied', grant, canRepeat: repeatFace(item, plan?.repeats, pro) !== 'none' && P.isOpenRow(item) && !P.isDraftItem(item),
    onToggle: () => {
      const opening = openId !== item.id;
      setOpenId((id) => (id === item.id ? null : item.id));
      // The cleanup's lines are worked out when it is opened: that is what Show the changes did.
      if (opening && P.worksOutOnRow(item) && P.stateWordOf(item) === 'needs_you' && P.changeLines(item.changeSet).length === 0) {
        void control(`${item.id}:propose`, 'modelPlanAction', item.id, 'propose', 'human', null);
      }
    },
  });
  const renderRow = (item: P.PlanItem, first = false, recent = false) => <div key={item.id} className={['pw-item', openId === item.id ? 'pw-item-open' : '',
    ['work', 'apply', 'check'].includes(stepFor(item)?.step ?? '') ? 'pw-item-live' : ''].filter(Boolean).join(' ')}>
    <PlanRow {...rowProps(item, first, recent)} />
    {/* A workflow run is a row whose detail is the run itself (lane W2, R3; Plan-heart contract 11.3). */}
    {openId === item.id && item.id.startsWith('run/') && <div className="pw-detail pw-detail-own"><WorkflowRunDetail runId={item.id.slice('run/'.length)} onRunUpdate={onRunUpdate} /></div>}
    {openId === item.id && P.isDraftItem(item) && <ComposerDraft draftId={draftIdOf(item.id)} changeNonce={changeNonce} onGo={onGo} />}
    {openId === item.id && !P.isDraftItem(item) && !item.id.startsWith('run/') && <PlanRowDetail item={item} busy={busy} labelOf={labelOf}
      onHistory={(entry) => { setRowEntry(entry); setView('hist'); }}
      onNote={(row, text) => void control(`${row.id}:note`, 'setPlanTaskNote', row.id, text)}
      repeats={plan?.repeats} isPro={pro} showRepeat={repeatRow === item.id}
      onRepeat={{
        add: (r) => controlOk(`${item.id}:repeat`, 'addPlanRepeat', r),
        change: (id, r) => controlOk(`${item.id}:repeat`, 'setPlanRepeat', id, r),
        remove: (id) => void control(`${item.id}:repeat`, 'deletePlanRepeat', id),
      }} />}
    {openId === item.id && item.changeSet && P.changeLines(item.changeSet).length > 0 && <div className="pw-changes">
      <span />
      <ChangeList set={item.changeSet} position={0} busy={busy} onDecideLine={decideLine} markRules={item.who === 'jev'}
        onDecideAll={P.worksOutOnRow(item) ? (kept) => void decideAll(item.changeSet!, kept) : undefined} />
    </div>}
  </div>;
  // The fold is open when asked, and while a row inside it is open (so a route to a row of yours shows the row).
  const foldShown = foldOpen || (openId != null && work.onlyYou.some((r) => r.id === openId));
  // The engine says whether the receipt may offer Save (unsaved, and not a live model); an older engine says nothing, so the page's own view of the session decides.
  const savable = receipt?.kind === 'ran' && typeof receipt.canSave === 'boolean' ? receipt.canSave : true;
  // Saved another way (Ctrl+S, the close question): the host says nothing of the person's is unsaved, so the line says Saved and offers no button.
  const savedElsewhere = !!hostSession && (hostSession.hasUnsavedEdits ?? hostSession.hasUnsavedChanges) === false;
  const shownReceipt = receipt && receipt.kind === 'ran' && savedElsewhere && !receipt.saved ? { ...receipt, saved: true } : receipt;
  const receiptNode = shownReceipt && <RunReceipt receipt={shownReceipt} busy={!!busy} canSave={savable}
    canUndo={shownReceipt.kind === 'ran' && !!shownReceipt.entryId && !(typeof plan?.revision === 'number' && typeof shownReceipt.revision === 'number' && plan.revision > shownReceipt.revision)}
    onUndo={() => void undoPress()} onSave={savePress}
    onHistory={() => { setRowEntry(shownReceipt.kind === 'ran' ? shownReceipt.entryId : null); setView('hist'); }} />;
  const firstId = work.filtered ? work.rows[0]?.id : (work.next[0] ?? work.onlyYou[0])?.id;

  return <div className="sem-evidence-page plan-page pw-page" data-testid="plan-page" data-pro={pro ? 'yes' : 'no'}
    data-view={view} data-track={track ?? 'all'}>
    <header className="pw-head">
      <h1 className="sem-page-title">Plan</h1>
      <span className="sem-seg" role="group" aria-label="Work or History">
        <button type="button" className="sem-seg-item" aria-pressed={view === 'work'} data-testid="plan-view-work"
          onClick={() => setView('work')}>Work</button>
        <button type="button" className="sem-seg-item" aria-pressed={view === 'hist'} data-testid="plan-view-hist"
          data-tour="history" onClick={() => setView('hist')}>History</button>
      </span>
      <span className="pw-switch" data-tour="queue-switch">
        <span className="plan-muted">Assistant</span>
        <span className="sem-seg" role="group" aria-label="Tasks for your assistant">
          <button type="button" className="sem-seg-item" aria-pressed={queue === 'on'} data-testid="plan-queue-on"
            disabled={!!busy} onClick={() => { if (queue !== 'on') void control('queue', 'setAssistantQueue', 'on'); }}>On</button>
          <button type="button" className="sem-seg-item" aria-pressed={queue === 'paused'} data-testid="plan-queue-paused"
            disabled={!!busy} onClick={() => { if (queue !== 'paused') void control('queue', 'setAssistantQueue', 'paused'); }}>Paused</button>
        </span>
        {/* Help at the Plan and the tour (lane U4). */}
        <PlanHelpMark />
      </span>
      <RefreshPlanButton busy={busy} onRefresh={() => void refresh()} />
    </header>

    {/* The tour draws over the page; the What changed card draws here, in flow (lane U4). */}
    <PlanGuide onView={(v) => setView(v === 'history' ? 'hist' : 'work')} />
    {/* Words typed into the start form when something else took the page (the assistant started a plan, or a model opened): named here, and back in the form when it opens (R-B). */}
    {view === 'work' && <StartFormKept onOpen={sp.view ? undefined : () => setPanel('start')} />}

    {view === 'hist'
      // History inside the Plan (lane U2): the ledger, filtered to the object the tree has selected, if any.
      ? <PlanHistory sessionId={sessionId} objectRef={focusObject?.ref ?? null} focusEntry={rowEntry ?? historyEntry} onGo={(tab) => onGo(tab)}
          pageOf={(ids) => { const it = items.find((i) => ids.includes(i.id) && i.jump?.tool); return it?.jump ? { tool: it.jump.tool, label: P.pagePlace(it.jump) } : null; }} />
      : sp.view && startMode === 'plan'
        // A new model from a SQL source is still a start plan: ONE list, five sections, a Needs you band, and the person's
        // approval before anything is built (lane sf-studio). Once it is built the ordinary plan is on the page at once, under a band (K25).
        ? <StartPlanPanel view={sp.view} onView={(v) => sp.setView(v && v.exists ? v : null)} pro={pro} stale={sp.stale}
          onClose={sp.view.built ? () => setShowBuilt(false) : undefined}
          onReload={() => { void sp.read(); }}
          // The engine answers a build with the BUILT plan (Built, the grades, Discard only), which stays on the page; an older
          // engine answered with no plan, and then the ordinary Plan takes the page back and says it was built.
          onBuilt={(said, kept) => { if (!kept) { sp.setView(null); setBuiltNote(said); } setRetry((n) => n + 1); }} />
        : <>
        {sp.view && startMode === 'band' && <BuiltPlanBand view={sp.view} onOpen={() => setShowBuilt(true)} />}
        {/* No start plan is on screen. Words the person typed on one that is gone (the assistant discarded it) are listed here with Copy and Discard, never deleted for them (D3). */}
        <WordsKeptAfterBuild modelKey={draftModel()} why="gone" />
        <WordsKeptAfterBuild modelKey={draftModel()} why="retired" />
        {/* Words typed on a task that left the plan (the assistant removed it): named here with Discard, never held where nobody sees them (R-B). */}
        {plan && <PlanRowWordsKept items={items} />}
        <PlanStrip plan={showPlan} track={track} onTrack={(t) => { setTrack(t); setShowAll(false); }} onLatest={() => setView('hist')} />

        <div className="pw-facts" data-testid="plan-facts" data-tour="facts">
          <span>{hostSession?.modelName || modelName || showPlan.modelName || 'This model'}</span>
          <span>{sourceWords(hostSession)}</span>
          {typeof hostSession?.tables === 'number' && <span><b className="sem-num">{hostSession.tables}</b> tables</span>}
          {typeof hostSession?.measures === 'number' && <span><b className="sem-num">{hostSession.measures}</b> measures</span>}
          {grade && <span>AI readiness <b>{grade.now}</b>{grade.after && <>, <b>{grade.after}</b> after the plan</>}</span>}
          {hostSession && <span>{(hostSession.hasUnsavedEdits ?? hostSession.hasUnsavedChanges) ? 'Not saved yet' : 'Saved'}</span>}
          {showPlan.repeatsNote && <span data-testid="plan-repeats-note">{showPlan.repeatsNote}</span>}
          {order.map((line, i) => <span key={i} data-testid={i === 0 && showPlan.orderedBy ? 'plan-ordered-by' : 'plan-jev-pending'}>{line}</span>)}
          {showPlan.billingUrl && <button type="button" className="plan-link" data-testid="plan-billing" onClick={() => openExternal(showPlan.billingUrl!)}>Billing</button>}
          <ProPlanWatch inactive={!!showPlan.billingUrl} />
          {updated && <span className="pw-facts-r" data-testid="plan-updated">{updated}</span>}
        </div>
        <RaiseLevelAction key={sessionId} level={compatibilityLevel} onRaised={() => setRetry((n) => n + 1)} />

        <form className="pw-field" data-tour="field" onSubmit={(e) => e.preventDefault()}>
          <textarea data-testid="plan-field" data-ask-carry="yes" className="pw-field-input" value={field} rows={1}
            placeholder={P.FIELD_PLACEHOLDER} aria-label={P.FIELD_PLACEHOLDER} disabled={busy === 'field'}
            onChange={(e) => setField(e.target.value)}
            onKeyDown={(e) => { if (P.fieldAddsOn({ key: e.key, shiftKey: e.shiftKey, isComposing: e.nativeEvent.isComposing })) { e.preventDefault(); void addTask(field); } }} />
          <span className="plan-muted pw-field-hint" data-testid="plan-field-hint">{P.fieldHint(field)}</span>
        </form>
        {pro && field.trim() && suggestion && <div className="pw-suggest" data-testid="plan-field-suggest">
          {/* The typed words go with the person: Open Ask your data lands on the question, not on an empty page. */}
          <AskReply result={suggestion} onGo={onGo} asked={field} />
        </div>}

        {actionError && <div className="plan-error" role="alert">{receipt?.kind === 'ran' ? '' : 'Nothing was changed. '}{actionError}</div>}
        {/* The toast floats over the page wherever the removed row was, so it is drawn into the Studio root, a layer
            above the page's layout, like the row menus. */}
        {removed && createPortal(<div className="pw-toast" role="status" data-testid="plan-toast">
          <span className="pw-trunc" title={removed.title}>{P.REMOVED_WORD.removed(removed.title)}{removed.told ? ` ${P.REMOVED_WORD.told}` : ''}</span>
          <button type="button" className="sem-btn sem-btn-sm" data-testid="plan-toast-undo" disabled={!!busy}
            onClick={() => void undoRemove()}>{P.REMOVED_WORD.undo}</button>
        </div>, (document.querySelector('.studio-root') as HTMLElement | null) ?? document.body)}
        {sentence && <div className="plan-said" role="status" data-testid="plan-said">{sentence}</div>}
        {builtNote && <div className="plan-said" role="status" data-testid="plan-built">{builtNote}</div>}
        {showPlan.note && <div className="plan-line plan-muted" data-testid="plan-note">{showPlan.note}</div>}

        {/* The ways to start, whenever the model has no tables, even with a row open (verify, 28 Sep, H3: a task or a
            model-wide finding on a new model hid them). "From a SQL source" lands on the one SQL entry point of lane
            m11/sql-start (route.ts SQL_DRAFT_TARGET, the same literal: this branch pairs with that one at merge); New
            blank model opens the hub's create form. */}
        {!!showPlan.starts?.length && <PlanStarts starts={showPlan.starts}
          onStart={(s) => onGo(s.jump.tool, s.id === 'sql' ? 'start:sql' : s.id)} />}
        {/* The list is drawn while a row is open too: the last open row, finished by another writer while the person has it open, stays on screen with what they typed under it. */}
        {items.filter(P.isOpenRow).length === 0 && !(openId && items.some((i) => i.id === openId)) && !receipt && justDone.size === 0
          ? <>
            {!showPlan.starts?.length && <div className="model-empty" data-testid="plan-empty"><strong>Nothing to do on the plan</strong>
              <span>The plan is drawn from the model. Describe a task above, or open a model with tables in it and its tasks appear here.</span></div>}
            {/* An empty plan still has an object line: picking an object in the tree must always offer its actions. */}
            {!track && focusObject && <section className="pw-list" data-testid="plan-list">
              <div className="pw-grp"><span>Showing <b>{focusObject.label}</b></span>
                <span className="pw-grp-r"><button type="button" className="plan-link" data-testid="plan-clear-filter"
                  onClick={() => onClearObject?.()}>Clear filter</button></span></div>
              <ObjectActions sessionId={sessionId} objectRef={focusObject.ref}
                onGo={(tool, target, addTables) => (onGoObject ?? ((t: string, g?: string) => onGo(t, g)))(tool, target, addTables)} onHistory={() => setView('hist')} />
            </section>}
          </>
          : work.filtered
            ? <div className={sees !== null || track === 'assistant' ? 'pw-split' : undefined}>
              <section className="pw-list" data-testid="plan-list">
                <div className="pw-grp">
                  <span>Showing <b>{track ? P.DOER_LABEL[track] : focusObject?.label}</b>
                    <span className="pw-grp-n sem-num">{work.rows.length}</span>
                    {track === 'assistant' && <span className="plan-muted"> in the order it takes them</span>}</span>
                  <span className="pw-grp-r">
                    {track === 'assistant' && <span className="plan-muted" data-testid="plan-queue-line">
                      {queue === 'paused' ? 'Paused by you. Your assistant gets no new tasks.' : 'Your assistant gets these when it next checks the Plan'}</span>}
                    <button type="button" className="plan-link" data-testid="plan-clear-filter"
                      onClick={() => { setTrack(null); onClearObject?.(); }}>Clear filter</button>
                  </span>
                </div>
                {receiptNode}
                {!track && focusObject && <ObjectActions sessionId={sessionId} objectRef={focusObject.ref}
                  onGo={(tool, target, addTables) => (onGoObject ?? ((t: string, g?: string) => onGo(t, g)))(tool, target, addTables)} onHistory={() => setView('hist')} />}
                {work.rows.length === 0
                  ? <div className="plan-muted pw-none">Nothing open here.</div>
                  : work.rows.map((r, i) => renderRow(r, i === 0))}
                {work.done.length > 0 && <div data-testid="plan-done">
                  <div className="pw-grp"><span><b>Done</b><span className="pw-grp-n sem-num">{work.done.length}</span></span>
                    <span className="pw-grp-r plan-muted">Open one to see what it changed.</span></div>
                  {work.done.map((r) => renderRow(r))}
                </div>}
              </section>
              {track === 'assistant' && <aside className="pw-sees" data-testid="plan-sees">
                <span className="plan-muted">What your assistant sees on its next call</span>
                {sees
                  ? <pre className="pw-sees-text" data-testid="plan-sees-text">{sees}</pre>
                  : <span className="pw-sees-text">Nothing. No task is ready for it.</span>}
                <ul className="pw-sees-notes">
                  <li><DoerNote doer="assistant" />It sees tasks on its next call. Nothing wakes it.</li>
                  <li><DoerNote doer="you" />Direct work with your assistant carries on either way.</li>
                  <li><DoerNote doer="semanticus" />The switch is only for your assistant.</li>
                </ul>
              </aside>}
            </div>
            : <section className="pw-list" data-testid="plan-list" data-primary ref={listRef}>
              <div className="pw-grp">
                <span><b>Next</b><span className="pw-grp-n sem-num">{work.nextTotal}</span></span>
                <span className="pw-grp-r">
                  <RunArea plan={showPlan} grant={grant} running={busy === 'run'} held={pressRows} steps={liveSteps}
                    onRun={() => void runPlan()} onTell={tellAssistant} told={told} />
                  <span className="sem-seg" role="group" aria-label="Goal">
                    {P.GOAL_ORDER.map((g) => <button key={g} type="button" className="sem-seg-item" aria-pressed={goal === g}
                      data-testid={`plan-goal-${g}`} onClick={() => { setGoal(g); setShowAll(false); }}>{P.GOAL_LABEL[g]}</button>)}
                  </span>
                </span>
              </div>
              {receiptNode}
              {/* Rows that turned done while the page was open stay here until the next press or a reload. */}
              {work.justDone.length > 0 && <div data-testid="plan-just-done">
                <div className="pw-grp"><span><b>Done just now</b><span className="pw-grp-n sem-num">{work.justDone.length}</span></span>
                  <span className="pw-grp-r plan-muted" data-testid="plan-just-done-note">{P.justDoneNote(work.justDone, showPlan)}</span></div>
                {work.justDone.map((r) => renderRow(r, false, true))}
              </div>}
              {work.next.length === 0
                ? <div className="plan-muted pw-none">Nothing else is open{goal === 'all' ? '' : ' for this goal'}.</div>
                : work.next.map((r) => renderRow(r, r.id === firstId))}
              {work.nextTotal > work.next.length && <div className="pw-more">
                <button type="button" className="plan-link" data-testid="plan-show-all" data-primary-more onClick={() => setShowAll(true)}>
                  Show all {work.nextTotal}</button></div>}
              {/* The rows only a person can do sit last, dimmed, in a closed fold. They never hold up the rest. */}
              {work.onlyYou.length > 0 && <div className="pw-fold" data-testid="plan-only-you" data-open={foldShown ? 'yes' : 'no'}>
                <div className="pw-fold-h">
                  <button type="button" className="pw-fold-btn" aria-expanded={foldShown} data-testid="plan-only-you-toggle" onClick={() => setFoldOpen((o) => !o)}>
                    <Caret open={foldShown} />
                    <span><b className="sem-num">{work.onlyYou.length}</b> {P.onlyYouWords(work.onlyYou)}</span>
                    {work.onlyYou.some((r) => P.stateWordOf(r) === 'needs_you') && <span className="pw-fold-warn" data-testid="plan-only-you-warn" role="img" aria-label="One has a question for you" />}
                  </button>
                  <span className="plan-muted pw-fold-hint">Open {work.onlyYou.length === 1 ? 'it' : 'one'} to see what to choose.</span>
                </div>
                {foldShown && work.onlyYou.map((r) => renderRow(r))}
              </div>}
            </section>}
      </>}
  </div>;
}

// ---- moved from the Overview (contract 11.1, lane R1) ----------------------------------------------------

/** What a compatibility level actually blocks, named one capability at a time. A level below a floor is not "held
 *  back": the classic date table works below 1701, and the one thing refused is authoring a calendar. A list, so the
 *  next floor that matters is a second row and cannot widen the first sentence. */
const LEVEL_BLOCKS: { capability: string; floor: number }[] = [
  { capability: 'Custom calendars for time calculations', floor: 1701 },
];
/** The first capability this level actually blocks, or null. Unknown is "not known to be blocking". */
export function blockedCapability(level?: number): { capability: string; floor: number } | null {
  if (typeof level !== 'number' || level <= 0) return null;
  return LEVEL_BLOCKS.find((entry) => level < entry.floor) ?? null;
}

/** Raise compatibility level, under the facts line. Shown only while the level blocks a NAMED capability. Free, and
 *  here because its only other control sits inside Advanced modelling. Keyed by session at the call site, because
 *  `done` is about the model that was raised. */
export function RaiseLevelAction({ level, onRaised }: { level?: number; onRaised: () => void }) {
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [done, setDone] = useState(false);
  const blocked = blockedCapability(level);
  if (!blocked || done) return null;
  const raise = async () => {
    setBusy(true); setError(null);
    try { await rpc('setCompatibilityLevel', blocked.floor); setDone(true); onRaised(); }
    catch (raiseError) { setError(messageOf(raiseError)); }
    finally { setBusy(false); }
  };
  return <div className="pw-raise" data-testid="plan-raise">
    {/* Names the ONE thing that is actually refused. Everything else at this level keeps working. */}
    <span>This model uses compatibility level {level} (the Power BI feature level). {blocked.capability} need level {blocked.floor} or higher.</span>
    <button type="button" className="sem-btn sem-btn-sm" data-testid="plan-raise-level" disabled={busy}
      onClick={() => void raise()}>{busy ? 'Raising…' : 'Raise compatibility level'}</button>
    {error && <span className="plan-error" role="alert">The level was not changed. {error}</span>}
  </div>;
}

/** The picked object's kind, as a word a person uses. */
const OBJECT_KIND_WORD: Record<string, string> = {
  table: 'Table', measure: 'Measure', relationship: 'Relationship', column: 'Column', calcColumn: 'Calculated column',
};

/** The actions for the object selected in the model tree, on the Plan's "Showing <object>" line. They were the
 *  Overview's; History now opens the Plan's own History, which is already filtered to this object. */
export function ObjectActions({ sessionId, objectRef, onGo, onHistory }: {
  sessionId: string; objectRef: string; onGo: (tool: string, target?: string, addTables?: string[]) => void; onHistory: () => void;
}) {
  const object: ObjectRef | undefined = refToObject(objectRef);
  if (!object || object.kind === 'model') return null;
  const ref = objectRef;
  const name = objectLabel(object) || 'Model';
  const main = 'sem-btn sem-btn-sm sem-btn-primary';
  const btn = 'sem-btn sem-btn-sm';
  const quiet = 'sem-btn sem-btn-sm sem-btn-quiet';
  let primary: React.ReactNode;
  let also: React.ReactNode;
  if (object.kind === 'table') {
    primary = <><button className={main} onClick={() => onGo('data', ref)}>Preview data</button><button className={btn} onClick={() => onGo('mcode', ref)}>Edit Power Query</button><button className={btn} onClick={() => onGo('diagram', ref, [object.table])}>Show in diagram</button></>;
    also = <><button className={quiet} onClick={() => onGo('lineage', ref)}>Lineage</button><button className={quiet} onClick={() => onGo('stats', ref)}>Table size</button><button className={quiet} onClick={() => onGo('search', ref)}>Find in this table</button><button className={quiet} onClick={() => runHostCommand('semanticus.newMeasure', ref, sessionId)}>Add measure</button><button className={quiet} onClick={onHistory}>History</button></>;
  } else if (object.kind === 'measure') {
    primary = <><button className={main} onClick={() => runHostCommand('semanticus.editDax', ref, sessionId)}>Edit formula</button><button className={btn} onClick={() => onGo('daxlab', ref)}>Try calculation</button><button className={btn} onClick={() => onGo('tests', ref)}>Add check</button></>;
    also = <><button className={quiet} onClick={() => onGo('lineage', ref)}>Lineage</button><button className={quiet} onClick={() => onGo('search', ref)}>Find and replace</button><button className={quiet} onClick={onHistory}>History</button></>;
  } else if (object.kind === 'relationship') {
    primary = <><button className={main} onClick={() => onGo('diagram', ref)}>Edit in diagram</button><button className={btn} onClick={() => onGo('lineage', ref)}>Lineage</button></>;
    also = <><button className={quiet} onClick={onHistory}>History</button></>;
  } else if (object.kind === 'column' || object.kind === 'calcColumn') {
    primary = <><button className={main} onClick={() => onGo('data', ref)}>Preview data</button><button className={btn} onClick={() => onGo('lineage', ref)}>Lineage</button></>;
    also = <><button className={quiet} onClick={() => { selectInProperties(ref); focusSelectInProperties(ref); }}>Hide or show</button><button className={quiet} onClick={() => onGo('diagram', ref, [object.table])}>Show in diagram</button><button className={quiet} onClick={onHistory}>History</button></>;
  } else {
    primary = <><button className={main} onClick={() => onGo('lineage', ref)}>Lineage</button><button className={btn} onClick={() => { selectInProperties(ref); focusSelectInProperties(ref); }}>Properties</button></>;
    also = <><button className={quiet} onClick={onHistory}>History</button></>;
  }
  const kind = OBJECT_KIND_WORD[object.kind] ?? object.kind;
  return <section className="pw-object" data-testid="plan-object-actions" aria-label={`${kind} ${name}`}>
    <div className="pw-object-buttons">{primary}</div>
    {also && <div className="pw-object-also"><span>Also:</span>{also}</div>}
  </section>;
}

function DoerNote({ doer }: { doer: P.PlanDoer }) {
  return <svg className={`pw-ico pw-doer-${doer}`} viewBox="0 0 12 12" aria-hidden="true">
    {doer === 'semanticus' && <rect x="2" y="2" width="8" height="8" fill="currentColor" />}
    {doer === 'you' && <circle cx="6" cy="6" r="4.3" fill="currentColor" />}
    {doer === 'assistant' && <path d="M6 1L11 6 6 11 1 6z" fill="currentColor" />}
  </svg>;
}

/** Where the model is: an open Power BI Desktop, a workspace, or a file. Said from the session, never guessed. */
function sourceWords(session: { source?: string; liveKind?: string; liveBound?: boolean } | null | undefined): string {
  if (!session) return 'Model';
  if (session.liveKind === 'local') return 'Power BI Desktop';
  if (session.liveBound || session.liveKind === 'xmla') return 'Workspace model';
  return session.source ? 'Model files' : 'Model';
}

// ---- the review list, kept from the Fix milestone: keep or drop each line of a change set ------------

export function ChangeList({ set, position, busy, onDecideLine, onDecideAll, markRules }: {
  set: P.PlanChangeSet; position: number; busy: string | null;
  /** A row Jev works: a line its family's rule chose says "rule" where a Jev line says how sure Jev was. */
  markRules?: boolean;
  onDecideLine: (lineId: string, kept: boolean) => void | Promise<unknown>;
  /** Keep all and Drop all, for a list a person chooses from line by line (the cleanup). */
  onDecideAll?: (kept: boolean) => void;
}) {
  const lines = P.changeLines(set);
  if (lines.length === 0) {
    return <div className="plan-muted" data-testid={`plan-changes-${position}`}>
      {set.sentence || 'Nothing could be worked out for this one.'}</div>;
  }
  // A DETACHED snapshot: the engine cleared the handles because its change plan is gone, and the ids it
  // used to hold now name somebody else's changes. Nothing here can be decided, and the row says so.
  const detached = lines.length > 0 && !lines.some((line) => P.canDecide(set, line));
  return <div className="plan-changes" data-testid={`plan-changes-${position}`} data-lines={lines.length}
    data-kept={P.keptCount(set)} data-detached={detached ? 'yes' : 'no'}>
    {/* The PREVIOUS run, if there was one. A receipt, never a row: an item id names a family, so work
        keeps arriving under it, and the lines below are the new proposal whatever already ran here. */}
    {P.receiptLine(set) && <div className="plan-muted"
      data-testid={`plan-receipt-${position}`}>{P.receiptLine(set)}</div>}
    {detached && <div className="plan-warnline" role="status"
      data-testid={`plan-detached-${position}`}>{P.DETACHED_LINE}</div>}
    {onDecideAll && !detached && <div className="plan-line" data-testid={`plan-bulk-${position}`}>
      <span className="plan-muted plan-grow">{`${P.keptCount(set)} of ${lines.length} kept`}</span>
      <span className="sem-seg shrink-0" role="group" aria-label="Keep or drop every line">
        <button type="button" className="sem-seg-item" disabled={!!busy || P.bulkRefs(set, true).length === 0}
          data-testid={`plan-keep-all-${position}`} onClick={() => onDecideAll(true)}>{P.KEEP_ALL}</button>
        <button type="button" className="sem-seg-item" disabled={!!busy || P.bulkRefs(set, false).length === 0}
          data-testid={`plan-drop-all-${position}`} onClick={() => onDecideAll(false)}>{P.DROP_ALL}</button>
      </span>
    </div>}
    {lines.map((line) => {
      const shown = P.diffLines([line.line])[0];
      // The handle the engine decides this line by. A line the engine sent none for cannot be aimed at,
      // so its Keep and Drop are unpressable rather than firing a call that would decide another line.
      const ref = P.lineRef(line);
      // Three things have to hold before a decision is offered, and the page checks all three: the set
      // is the live proposal, the line has a handle, and the handle is one THIS set holds. A handle the
      // page cannot verify may name somebody else's change (Astra finding 7).
      const decidable = P.canDecide(set, line);
      // WHAT IS ACTUALLY BEING APPROVED. The engine's own sentence is built from the object name and the
      // operation title, so on its own it reads "Margin %: set description" and "Margin %: update DAX".
      // Keep and Approve sat over exactly that, which let a person approve a description they had never
      // read and a formula they had never seen (Astra finding 11).
      const values = P.changeValues(line);
      // JEV'S HALF OF THE LINE (M4): how sure it was, whether the engine checked the choice, and why, in
      // words the engine built. An unsure line arrives dropped and says so in amber until someone keeps it.
      const word = P.jevWord(line);
      const rule = markRules && !word ? P.ruleWord(line) : null;
      const tick = P.checkTick(line);
      const unsure = P.unsureAndDropped(line);
      const suggestion = P.suggestionNote(line);
      // A dropped line stays on the list and goes quiet: muted and struck through, still readable, because
      // taking it away would hide a decision a person can still change their mind about. A kept line carries
      // the accent on its Keep, the one place the review uses it.
      const tone = shown && shown.sign === '+' ? ' plan-add' : shown && shown.sign === '-' ? ' plan-del' : '';
      return <div key={line.id} data-line={line.id} data-ref={ref} data-kept={line.kept ? 'yes' : 'no'}
        data-expression={values.isExpression ? 'yes' : 'no'} data-jev-word={word ?? undefined} data-suggestion={suggestion ? 'yes' : undefined}
        className={line.kept ? 'plan-cl' : 'plan-cl plan-cl-off'}>
        <div className="plan-line">
        <span className={`plan-cl-text${tone}`}>
          {shown ? <>
            <span className="sr-only">{P.DIFF_SIGN_LABEL[shown.sign]}: </span>
            <span aria-hidden="true">{shown.sign === ' ' ? '' : `${shown.sign} `}</span>{shown.text}
          </> : line.line}
        </span>
        {line.provenance?.kind === 'decision' && !line.suggestion && <span className="plan-muted" data-testid={`plan-jev-chose-${position}-${line.id}`}>
          {line.kept || word === 'sure' || word === 'likely' ? 'Jev chose this' : 'Jev asks you'}</span>}
        {word && <span data-testid={`plan-jevword-${position}-${line.id}`} title={P.JEV_WORD_TITLE[word]}>
          <Pill tone={word}>{word}</Pill></span>}
        {rule && <span data-testid={`plan-ruleword-${position}-${line.id}`} title={P.ruleWordTitle(line)}>
          <Pill tone={rule}>{rule}</Pill></span>}
        {tick && <span role="img" aria-label={tick} title={tick} data-testid={`plan-check-${position}-${line.id}`}
          className="plan-tick">{'\u2713'}</span>}
        <span className="sem-seg shrink-0" role="group"
          aria-label={`Keep or drop: ${line.line}`}>
          <button type="button" className="sem-seg-item plan-keep" aria-pressed={line.kept} disabled={!!busy || !decidable}
            data-testid={`plan-keep-${position}-${line.id}`}
            onClick={() => void onDecideLine(ref, true)}>Keep</button>
          <button type="button" className="sem-seg-item" aria-pressed={!line.kept} disabled={!!busy || !decidable}
            data-testid={`plan-drop-${position}-${line.id}`}
            onClick={() => void onDecideLine(ref, false)}>Drop</button>
        </span>
        </div>

        {/* The values, under the sentence. A line the engine sent neither side for draws NOTHING here,
            because an empty before beside an empty after reads as "nothing changes". An empty value
            that the engine really did send says the word "empty" instead, which is a different fact. */}
        {values.hasValues && <div className="plan-values" data-testid={`plan-value-${position}-${line.id}`}>
          <div className="plan-muted">
            {values.target}{values.propertyWords ? <> · <b>{values.propertyWords}</b></> : null}
          </div>
          {values.isExpression
            // A FORMULA IS CODE. Squeezed onto one line of a two-column pair it cannot be read at all,
            // so both sides are blocks, one above the other, in the font code is written in.
            ? <div className="plan-stack">
              <ValueBlock label="Now" text={values.beforeLabel} tone="del" />
              <ValueBlock label="After you approve" text={values.afterLabel} tone="add" />
            </div>
            : <div className="plan-pair">
              <span className="plan-muted">Now</span>
              <span className={values.before.trim() === '' ? 'plan-pair-v plan-muted' : 'plan-pair-v'}>
                {values.beforeLabel}</span>
              <span className="plan-muted">After you approve</span>
              <span className="plan-pair-v plan-add">
                {values.afterLabel}</span>
            </div>}
        </div>}
        {line.reason && <div className="plan-reason" data-testid={`plan-reason-${position}-${line.id}`}>{line.reason}</div>}
        {suggestion && <div className="plan-warnline" data-testid={`plan-suggestion-${position}-${line.id}`}>{suggestion}</div>}
        {unsure && <div className="plan-warnline" data-testid={`plan-unsure-${position}-${line.id}`}>{P.JEV_UNSURE_LINE}</div>}
      </div>;
    })}
  </div>;
}

/** An exact change, one line per line, in the diff colours. Unboxed: it sits inside a card or a row already,
 *  and a box around it would be a card inside a card. The sign is read out, never only coloured. */
function DiffBody({ diff }: { diff: ReturnType<typeof P.diffLines> }) {
  return <>{diff.map((line, index) => <span key={`${line.sign}${line.text}${index}`}
    className={line.sign === '+' ? 'plan-dl plan-add' : line.sign === '-' ? 'plan-dl plan-del' : 'plan-dl'}>
    <span className="sr-only">{P.DIFF_SIGN_LABEL[line.sign]}: </span>
    <span aria-hidden="true">{line.sign === ' ' ? '' : `${line.sign} `}</span>{line.text}</span>)}</>;
}

/** One side of a formula, as a block. A person cannot read DAX on a squeezed single line, and approving
 *  a formula they could not read is the thing the pair exists to stop. */
function ValueBlock({ label, text, tone }: { label: string; text: string; tone: 'add' | 'del' }) {
  return <div>
    <div className="plan-muted">{label}</div>
    <pre className={tone === 'add' ? 'plan-code plan-add' : 'plan-code'}>{text}</pre>
  </div>;
}

// ---- the map ---------------------------------------------------------------------------------------
//
// One card per table, most open work first. It answers the question a person asks before they touch a
// table or hand it to somebody: what is this, what is open HERE, what is wrong here, what has already
// been done here, and where do I go to look at it.
//
// The words on a card are not the plan's words. "Remove 83 unused things" is true of the plan and tells
// a reader nothing about the table in front of them; on a card the same item says "Remove 31 unused
// things here". Every one of those sentences comes from plan-model, pure and tested, because a count in
// a sentence is a claim and a claim has to be executable.
//
// Free and Pro follow the page's gate and nothing else: no score and no Start on free, and the one pill
// that explains it stays where it already is, above the views. A card does not grow a second upsell.

/** A table name as a testid-safe token. The names are the model's, so they carry spaces and case. */

function normalize(value: P.Plan | null | undefined): P.Plan {
  const plan = value ?? ({} as P.Plan);
  return {
    ...plan,
    items: Array.isArray(plan.items) ? plan.items : [],
    history: Array.isArray(plan.history) ? plan.history : [],
    tables: Array.isArray(plan.tables) ? plan.tables : [],
  };
}
