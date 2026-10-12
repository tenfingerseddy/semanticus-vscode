import React, { useEffect, useState } from 'react';
import { rpc, copyText, navigateInStudio, onPlanChange, onReconnect } from './bridge';
import { rowCoveringFinding } from './plan-model';

// Send to your assistant (Plan at the heart, lane U3; the approved send-to-assistant.html, folded into the Plan).
//
// Every "Ask AI" button used to copy a prompt and leave the person to paste it. Now each one gives the same prompt
// to your assistant as a Plan task, and Copy stays beside it, always. Giving is the person's own add-task control
// (addPlanTask, on the human-only target, so no assistant can invent work for itself), with the page's prompt as
// the task's brief. The doer is left to the engine, whose default for a new task is your assistant.
//
// WHAT IT PROMISES, AND WHAT IT DOES NOT. Semanticus cannot wake an assistant. The task is offered on the
// assistant's next Semanticus call, and the control says exactly that. It never says "sent" as if delivery were
// done; it says the task is on the plan, which is true the moment the engine answers.
//
// FREE, AND NOT JEV. Semanticus writes nothing and decides nothing here: it adds one row to the Plan. The engine
// files addPlanTask on the free list for that reason.
//
// HOW DONE IS KNOWN, per class (contract 1.5). A finding on AI readiness or Model quality is checked by the model:
// the engine reads the scan the finding came from, so the rescan that drops it closes the task. A docs section and a
// pending change's value are checked for their words where they go (a has_words slot, not the object's Description).
// A storage idea, an explanation and a rule are "your assistant says". The DAX field is not here at all: its text is
// unsaved, and no assistant can type into a box on the person's screen, so it keeps Copy only.

export const GIVE_LABEL = 'Give to your assistant';
export const ON_PLAN_LABEL = 'On the plan';
export const COPY_LABEL = 'Copy';
export const COPIED_LABEL = 'Copied';
export const NEXT_CALL_NOTE = 'Your assistant sees it on its next call.';
export const GIVE_TITLE = 'Adds this to your Plan as a task for your assistant. ' + NEXT_CALL_NOTE;
export const ON_PLAN_TITLE = 'Open the Plan. ' + NEXT_CALL_NOTE + ' If it is waiting for you, send it any message.';
export const COPY_TITLE = 'Copy the same task as text, to paste to your assistant yourself.';

/** The wire shape of the engine's PlanTaskRequest (Semanticus.Engine/Plan/PlanTasks.cs). */
export interface PlanTaskRequest {
  words: string;
  brief?: string;
  sourcePage?: string;
  objects?: string[];
  doneWhen?: 'model_check' | 'has_words' | 'assistant_says' | 'you_mark';
  originLink?: { kind: 'finding' | 'page' | 'run' | 'draft' | 'words' | 'item'; ref?: string };
  /** has_words only: where the words go when it is not the objects' Description ("doc:<section>|<object>" or
   *  "change:<item id>"; PlanTaskRequest.Slot). */
  slot?: string;
  doer?: 'assistant' | 'you';
  /** The control's key (wireSendKey). While a task with this page and key is open, the engine answers a second
   *  press with that task instead of adding another. */
  sendKey?: string;
}

const MAX_WORDS = 280;   // PlanTasks.MaxWordsChars
const MAX_BRIEF = 4000;  // PlanTasks.MaxBriefChars

/** The Plan row's title: one line, within the engine's limit. */
export function clampWords(text: string): string {
  const one = text.replace(/\s+/g, ' ').trim();
  return one.length <= MAX_WORDS ? one : one.slice(0, MAX_WORDS - 3).trimEnd() + '...';
}
const clampBrief = (text: string) => (text.length <= MAX_BRIEF ? text : text.slice(0, MAX_BRIEF));
const isObjectRef = (ref: string | undefined): ref is string => !!ref && ref !== 'model' && ref.includes(':');
const pageTask = (page: string, words: string, brief: string, objects: (string | undefined)[] = []): PlanTaskRequest => ({
  words: clampWords(words),
  brief: clampBrief(brief),
  sourcePage: page,
  objects: objects.filter(isObjectRef),
  doneWhen: 'assistant_says',
  originLink: { kind: 'page', ref: page },
});

// ---- the task each control class gives ------------------------------------------------------------

/** A finding on AI readiness ('air') or Model quality ('bpa'), with the engine's own fix prompt as the brief.
 *  ADVICE is not a fault: when the engine answers Tool "(advice)" (a format rule whose fix the format fill rule
 *  withholds), the task is to review the advice with the person, not to fix it. So its title says "Review", and done is
 *  what the assistant says, because the finding stays when the person keeps the format they have, which is a right
 *  answer. A model check would push the assistant toward the very write no fix is allowed to make. */
export function findingTask(
  system: 'air' | 'bpa',
  f: { ruleId: string; ruleTitle?: string; ruleName?: string; objectRef: string; objectName: string },
  brief: string,
  advice = false,
): PlanTaskRequest {
  const rule = f.ruleTitle ?? f.ruleName ?? f.ruleId;
  const words = advice ? `Review the advice "${rule}" on ${f.objectName}` : `Fix "${rule}" on ${f.objectName}`;
  return {
    words: clampWords(words),
    brief: clampBrief(brief),
    sourcePage: system === 'bpa' ? 'bpa' : 'readiness',
    objects: [f.objectRef].filter(isObjectRef),
    doneWhen: advice ? 'assistant_says' : 'model_check',
    originLink: { kind: 'finding', ref: `${f.ruleId}|${f.objectRef}` },
  };
}

/** The builder a finding row passes: it asks the engine for the same fix prompt Ask AI copied, then makes the task.
 *  The engine's Tool says whether it is advice. */
export function findingBuilder(
  system: 'air' | 'bpa',
  f: { ruleId: string; ruleTitle?: string; ruleName?: string; objectRef: string; objectName: string },
): () => Promise<PlanTaskRequest> {
  return async () => {
    const p = await rpc<{ prompt: string; tool?: string }>(system === 'bpa' ? 'bpaGetFixPrompt' : 'getFixPrompt', f.ruleId, f.objectRef);
    return findingTask(system, f, p.prompt, p.tool === '(advice)');
  };
}

/** A storage opportunity on Size by table: advice, so done is what your assistant says. */
export function storageTask(o: { ruleName: string; ref: string; table: string; column: string }, brief: string): PlanTaskRequest {
  return pageTask('stats', `Look into ${o.table}[${o.column}]: ${o.ruleName}`, brief, [o.ref]);
}

/** One narrative section on Docs. Semanticus checks that the section has words (the engine reads the doc section). */
export function docsTask(objRef: string, objName: string, section: string, brief: string, sectionKey: string): PlanTaskRequest {
  return { ...pageTask('docs', `Write the ${section} docs for ${objName}`, brief, [objRef]),
    doneWhen: 'has_words', slot: `doc:${sectionKey}|${objRef}` };
}

/** Explain this value, from Queries. The answer lives in the assistant's chat. */
export function explainTask(measure: string, value: string, brief: string): PlanTaskRequest {
  return pageTask('daxlab', `Explain why ${measure} shows ${value || 'this value'}`, brief);
}

/** A pending change that needs words. Semanticus checks that the change-plan slot has a value. */
export function slotTask(it: { id: string; objectRef: string; objectName: string; target: string }, brief: string): PlanTaskRequest {
  return { ...pageTask('optimize', `Write the ${it.target} for ${it.objectName}`, brief, [it.objectRef]),
    doneWhen: 'has_words', slot: `change:${it.id}` };
}

/** A custom rule, in the person's own words first. */
export function ruleTask(kind: 'air' | 'bpa', check: string, brief: string): PlanTaskRequest {
  return pageTask(kind === 'bpa' ? 'bpa' : 'readiness', `Write a custom rule: ${check}`, brief);
}

/** What Copy copies: the task's own brief. Once the task is on the plan, it also names it, so an assistant handed
 *  both takes the waiting task instead of starting a second one. */
export function copyTextFor(brief: string, taskId: string | null | undefined): string {
  if (!taskId) return brief;
  return `${brief}\n\nThis is already on the Semanticus Plan as task ${taskId}. Take that task with claim_request rather than starting a new one.`;
}

// ---- giving, and remembering it was given -------------------------------------------------------

// THE ENGINE IS THE RECORD (Sol 6 slice 9 review). Every Give carries the control's key, and the engine keeps it
// on the task (originLink.sendKey), scoped to the model because each model has its own plan. While that task is
// open, a second press gets the same task back, so nothing this page forgets can put the work on the plan twice.
//
// What this webview has given is only a cache of that, by the control's key, so a page that re-mounts shows
// "On the plan" without waiting. The cache is re-read from the engine's plan whenever that plan can have moved: a
// plan change (the assistant finished or declined the task, or the person set it aside, so its control offers Give
// again and Copy stops naming a closed task), a reconnect (which is also how another model arrives), and every
// mount (a page opened after the task closed, with no change to hear since).
const MAX_SEND_KEY = 400;   // PlanTasks.MaxSendKeyChars

/** The key as the engine keeps it. Most keys are short and go as they are. A longer one (the explain panel's
 *  carries its filters) keeps its start and ends in a hash of the whole, so it stays stable and distinct. */
export function wireSendKey(sendKey: string): string {
  if (sendKey.length <= MAX_SEND_KEY) return sendKey;
  // FNV-1a, 32 bits, twice with different seeds: a stable fingerprint, not a secret.
  const fnv = (seed: number) => {
    let h = seed >>> 0;
    for (let i = 0; i < sendKey.length; i++) { h ^= sendKey.charCodeAt(i); h = Math.imul(h, 0x01000193) >>> 0; }
    return h.toString(16).padStart(8, '0');
  };
  return `${sendKey.slice(0, MAX_SEND_KEY - 18)}#${fnv(0x811c9dc5)}${fnv(0x2f1a7c3b)}`;
}

/** The open tasks on a plan that a Give made, by their key. Done and set aside are closed, so a key whose task
 *  finished is free to be given again. */
export function givenFromPlan(plan: { items?: { id: string; stateWord?: string | null; originLink?: { sendKey?: string | null } | null }[] } | null | undefined): Map<string, string> {
  const out = new Map<string, string>();
  for (const item of plan?.items ?? []) {
    const key = item.originLink?.sendKey;
    if (key && item.stateWord !== 'done' && item.stateWord !== 'set_aside') out.set(key, item.id);
  }
  return out;
}

const given = new Map<string, string>();
/** The plan's rows as last read, for the rows that already carry a readiness finding (round 1, K4). */
type PlanRowsForFindings = { id: string; stateWord?: string | null; status?: string; rules?: string[]; objects?: string[]; originLink?: { sendKey?: string | null } | null }[];
let planRows: PlanRowsForFindings = [];
const givenListeners = new Set<() => void>();
const tell = () => givenListeners.forEach((l) => l());
// Bumped by every Give, so a re-read that started before a Give does not wipe what that Give just said.
let givesSince = 0;
async function rereadFromEngine() {
  const before = givesSince;
  let fresh: Map<string, string>;
  let rows: PlanRowsForFindings = [];
  try {
    const plan = await rpc<{ items?: PlanRowsForFindings }>('getModelPlan');
    fresh = givenFromPlan(plan);
    rows = plan?.items ?? [];
  }
  catch { fresh = new Map(); }   // No plan to read (no model open): offer Give; the engine still answers one task.
  if (before !== givesSince) return;
  given.clear();
  for (const [k, v] of fresh) given.set(k, v);
  planRows = rows;
  tell();
}
// Subscribed when the first control mounts (or the first Give), not at import, so a page that only imports this
// file (the Plan folds in Pending changes) takes on no listener. Once subscribed, it stays for the webview's life:
// the cache is module state, so it must hear every change even while no control is on screen.
let watching = false;
function watchTheEngine() {
  if (watching) return;
  watching = true;
  onReconnect(rereadSoon);
  onPlanChange(rereadSoon);
}
// One read for a burst: a page of finding rows mounts at once, and one landing can publish plan/didChange twice.
let rereadTimer: ReturnType<typeof setTimeout> | null = null;
function rereadSoon() {
  if (rereadTimer != null) return;
  rereadTimer = setTimeout(() => { rereadTimer = null; void rereadFromEngine(); }, 50);
}
/** What a control does when it mounts: watch the engine, and read what is on the plan now. */
export function watchGiven(): void {
  watchTheEngine();
  rereadSoon();
}
function remember(sendKey: string, taskId: string) {
  watchTheEngine();
  givesSince++;
  given.set(wireSendKey(sendKey), taskId);
  tell();
}

export function sentTaskFor(sendKey: string): string | null { return given.get(wireSendKey(sendKey)) ?? null; }

/** The findings no open plan row carries (Kane, 11 Oct): AI readiness and Model quality findings join the Plan by themselves,
 *  so Give the rest offers only what is not on it yet, and never a second copy of a row's work. `rows` is the plan as last read. */
export function notOnPlan<T extends { ruleId: string; objectRef: string }>(items: T[], rows: PlanRowsForFindings = planRows): T[] {
  return items.filter((i) => !rowCoveringFinding(rows, i.ruleId, i.objectRef));
}

/** Draw again when the plan as last read moves, for a page that leaves out what a plan row carries (notOnPlan). */
export function usePlanRowsSeen(): void {
  const [, bump] = useState(0);
  useEffect(() => {
    watchGiven();
    const seen = () => bump((n) => n + 1);
    givenListeners.add(seen);
    return () => { givenListeners.delete(seen); };
  }, []);
}

/** Post the task through the person's add-task control. One call, one task, and the same task on a second press. */
export async function giveToAssistant(sendKey: string, build: () => PlanTaskRequest | Promise<PlanTaskRequest>): Promise<{ taskId: string }> {
  const request = await build();
  const result = await rpc<{ itemId: string }>('addPlanTask', { ...request, sendKey: wireSendKey(sendKey) });
  remember(sendKey, result.itemId);
  return { taskId: result.itemId };
}

/** Open the Plan on the task itself: its row opened, and no object filter hiding it (journey m11 F4, where a Plan
 *  still filtered to another object left the task just given off the list). */
export function openPlan(taskId?: string | null): void {
  if (taskId) navigateInStudio('planrow', taskId);
  else navigateInStudio('plan');
}

// ---- the control -----------------------------------------------------------------------------------

type Props = {
  /** Names the thing being given (page, rule and object), so the same finding is not given twice. */
  sendKey: string;
  /** Builds the task. May ask the engine for the prompt first (a finding's fix prompt). */
  build: () => PlanTaskRequest | Promise<PlanTaskRequest>;
  /** Full-size controls where the old button was full size; small in a finding row. */
  size?: 'sm' | 'md';
  primary?: boolean;
  disabled?: boolean;
  disabledTitle?: string;
  /** Give alone waits (the rule author's words); Copy still works. */
  giveDisabled?: boolean;
  /** The words on the Give button when it gives a whole set, e.g. "Give the rest to your assistant". */
  label?: string;
  /** Just the Give button: no Copy beside it and no note under it (a page's one summary button, where Copy would be noise). */
  bare?: boolean;
};

export function GiveToAssistant({ sendKey, build, size = 'sm', primary, disabled, disabledTitle, giveDisabled, label, bare }: Props) {
  const [taskId, setTaskId] = useState<string | null>(() => sentTaskFor(sendKey));
  const [busy, setBusy] = useState(false);
  const [copied, setCopied] = useState(false);
  const [err, setErr] = useState<string | null>(null);
  useEffect(() => {
    watchGiven();
    setTaskId(sentTaskFor(sendKey));
    const sync = () => setTaskId(sentTaskFor(sendKey));
    givenListeners.add(sync);
    return () => { givenListeners.delete(sync); };
  }, [sendKey]);

  const btn = 'sem-btn' + (size === 'sm' ? ' sem-btn-sm' : '');
  const give = async () => {
    setBusy(true); setErr(null);
    try { setTaskId((await giveToAssistant(sendKey, build)).taskId); }
    catch (e) { setErr(String((e as Error)?.message ?? e)); }
    finally { setBusy(false); }
  };
  const copy = async () => {
    setErr(null);
    try {
      const request = await build();
      if (await copyText(copyTextFor(request.brief ?? request.words, taskId))) {
        setCopied(true); window.setTimeout(() => setCopied(false), 1600);
      }
    } catch (e) { setErr(String((e as Error)?.message ?? e)); }
  };

  return (
    <span className="inline-flex flex-wrap items-center gap-1" data-send-key={sendKey}>
      {taskId
        ? <button type="button" className={btn + ' sem-btn-quiet'} title={ON_PLAN_TITLE} onClick={() => openPlan(taskId)} data-send-state="on-plan">{ON_PLAN_LABEL}</button>
        : <button type="button" className={btn + (primary ? ' sem-btn-primary' : '')} disabled={disabled || giveDisabled || busy}
            title={(disabled || giveDisabled) && disabledTitle ? disabledTitle : GIVE_TITLE} onClick={() => void give()} data-send-state="give">
            {busy ? 'Adding...' : (label ?? GIVE_LABEL)}
          </button>}
      {!bare && <button type="button" className={btn} disabled={disabled} title={disabled && disabledTitle ? disabledTitle : COPY_TITLE}
        onClick={() => void copy()} data-send-state="copy">{copied ? COPIED_LABEL : COPY_LABEL}</button>}
      {/* Said once the task is on the plan, and always where there is room for it (a panel footer, a form). */}
      {!bare && (taskId || size === 'md') && <span className="sem-text-meta">{NEXT_CALL_NOTE}</span>}
      {err && <span role="alert" className="sem-text-meta flex items-start gap-1"><span className="sem-chip sem-chip-bad shrink-0" aria-hidden="true" />{err}</span>}
    </span>
  );
}

/** A readiness finding's fix control. A finding that is already inside a plan row says On the plan and opens that row; it
 *  does not offer a second copy of the finding (round 1, K4). Any other finding offers Give to your assistant as before. */
export function GiveOrOnPlan({ ruleId, objectRef, ...give }: Props & { ruleId: string; objectRef: string }) {
  const [rowId, setRowId] = useState<string | null>(() => rowCoveringFinding(planRows, ruleId, objectRef));
  useEffect(() => {
    watchGiven();
    const sync = () => setRowId(rowCoveringFinding(planRows, ruleId, objectRef));
    sync();
    givenListeners.add(sync);
    return () => { givenListeners.delete(sync); };
  }, [ruleId, objectRef]);
  if (rowId) {
    return <button type="button" className="sem-btn sem-btn-sm sem-btn-quiet" title={ON_PLAN_ROW_TITLE} data-send-state="on-plan-row"
      data-plan-row={rowId} onClick={() => openPlan(rowId)}>{ON_PLAN_LABEL}</button>;
  }
  return <GiveToAssistant {...give} />;
}
export const ON_PLAN_ROW_TITLE = 'This finding is on the Plan. Open its row.';

/** The custom rule author's control: the person says what the rule should check in one line, then gives it or
 *  copies it. Give waits for their words; Copy works without them and leaves a place to fill in. */
export function RuleCheckBox({ kind, prompt }: { kind: 'air' | 'bpa'; prompt: (check: string) => string }) {
  const [check, setCheck] = useState('');
  const words = check.trim();
  return (
    <span className="flex flex-wrap items-center gap-2">
      <label className="flex items-center gap-2 sem-text-meta">
        <span>What should the rule check?</span>
        <input value={check} onChange={(e) => setCheck(e.target.value)} maxLength={200} spellCheck
          placeholder="For example: every measure has a format string"
          className="sem-field" style={{ minWidth: '18rem' }} />
      </label>
      <GiveToAssistant sendKey={`rule|${kind}|${words}`} size="md"
        giveDisabled={!words} disabledTitle="Say what the rule should check first."
        build={() => ruleTask(kind, words, prompt(words || '<describe the practice in plain words>'))} />
    </span>
  );
}
