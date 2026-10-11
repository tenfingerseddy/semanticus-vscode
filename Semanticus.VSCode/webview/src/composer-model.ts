// ===================================================================================================
// The composer's quick path on the Plan: the pure view logic (lane U5, was composer L6).
//
// A draft is one object both doors hold (composer contract 6.1). The engine writes every sentence in it: the summary,
// the notes, the part lines, the banner, the request a person copies. This file only decides WHICH controls a draft
// shows in which state, and names the few button words the approved frames give (workflow-composer-v3.html, Plan
// frames; composer contract 10.3; Plan-heart contract 14). It reads the engine's state and never works one out.
//
// Kept pure (no React, no bridge) so test/composer-draft.test.mjs executes every rule here against the engine's own
// frozen scenes in docs/redesign/contracts/draft-view.json.
// ===================================================================================================

export type DraftState = 'needs_you' | 'waiting_for_assistant' | 'ready' | 'running' | 'done' | 'stopped';
export type BlankState = 'empty' | 'queued' | 'writing' | 'filled' | 'self' | 'generated' | 'needs';

export interface DraftOption { ref: string; label: string }
export interface DraftQuestionView {
  name: string; question: string; type: string; by: string; values?: string[]; step?: string; answer?: string;
  options?: DraftOption[];
  /** Asked when the run stops at its step (a person's stop), not before it runs (lane author-gaps, B9). */
  atStop?: boolean;
}
export interface DraftBlankView {
  step: number; stepId: string; name: string; kind: string; state: BlankState; line: string;
  text?: string; filledBy?: string; requestId?: string; attemptsLeft: number; reason?: string; choices?: string[];
  request?: string;
}
export interface DraftCheckLine { phase: string; text: string }
export interface DraftStepView {
  id: string; number: number; title: string; object?: string; changes: string; tags: string[];
  check?: DraftCheckLine; kept: boolean; status?: string; confidence?: string; note?: string;
  /** A gated step your assistant does: the guided run its checks run in (lane l9-finish). */
  runId?: string;
}
export interface DraftBanner { kind: 'stopped' | 'check_failed' | 'restarted' | 'nothing_changed' | 'overwrite'; title: string; text: string }
/** A value the draft writes changed after the draft was made (UAT S12-5-004): the person keeps theirs or applies the draft. */
export interface DraftOverwriteView { sentence: string; step: number; objectRef: string; was: string; now: string }
export interface DraftMenuItem { id: string; title: string; changes: string; matched: boolean }
export interface DraftView {
  draftId: string; title: string; words?: string; source: string; state: DraftState;
  planWord?: string; planNote?: string; approved: boolean; held?: string; summary: string; notes: string[];
  banner?: DraftBanner; questions: DraftQuestionView[]; blanks: DraftBlankView[]; steps: DraftStepView[];
  after: DraftStepView[]; undoSteps: number; partsWaiting: number; sentence?: string;
  requestText?: string; canTryAgain?: boolean; menu?: DraftMenuItem[]; menuLine?: string; overwrite?: DraftOverwriteView;
  /** The step the run waits at (a person's stop, an assistant's step), and whether Continue is offered (lane author-gaps, B9). */
  pausedAt?: string; canContinue?: boolean;
  /** What one Undo takes back, once it changed the model (B10): the status bar shows it. */
  undoLabel?: string;
  /** Only on the answer to open_in_author (B8): the draft as a format-3 workflow file. */
  document?: { name: string; text: string };
}

/** The Plan row id of a draft is "draft/<dr id>" (Plan-heart contract 1.3). */
export const DRAFT_ROW_PREFIX = 'draft/';
export function isDraftRow(id: string | null | undefined): boolean { return !!id && id.startsWith(DRAFT_ROW_PREFIX); }
export function draftIdOf(rowId: string): string { return isDraftRow(rowId) ? rowId.slice(DRAFT_ROW_PREFIX.length) : rowId; }
export function draftRowId(draftId: string): string { return DRAFT_ROW_PREFIX + draftId; }

// ---- the words the frames give the buttons (composer contract 10.3; Kane 24 Sep: no product names) ----------------

export const WORD = {
  runWithAssistant: 'Run with your assistant',
  approveAndRun: 'Approve and run',
  editInWorkflows: 'Edit in Workflows',
  /** Refine on a draft from words: it adds words and lists the steps again. Nobody is asked, so the name
   *  promises no one (the engine's DraftCopy.AddMoreWords). */
  addMoreWords: 'Add more words',
  writeItYourself: 'Write it yourself',
  copyTheRequest: 'Copy the request',
  copied: 'Copied',
  drop: 'Drop',
  keep: 'Keep',
  askAgain: 'Ask again',
  undo: 'Undo',
  undoStep1: 'Undo step 1',
  continueRun: 'Continue',
  continueWithout: 'Continue without it',
  moveUp: 'Move up',
  moveDown: 'Move down',
  keepItAnyway: 'Keep it anyway',
  askWhy: 'Ask your assistant why',
  makeReusable: 'Make it reusable in Workflows',
  tryAgain: 'Try again',
  checkAgain: 'Check again',
  runAgain: 'Run it again',
  keepYours: 'Keep yours',
  applyTheDraft: 'Apply the draft',
  yoursNow: 'Yours now:',
  add: 'Add',
  save: 'Save',
  cancel: 'Cancel',
  youChoose: 'You choose',
  assistantWrites: 'Your assistant writes',
  beforeItRuns: 'Before it runs',
  afterTheChange: 'After the change',
  nothingChangedYet: 'Nothing has changed yet.',
  draftTheSteps: 'Draft the steps',
  /** Composer contract 10.7, the Plan's Pro line, as approved. It is true since Jev drafts on the Plan door
   *  (JevDraftBuilder.JevDraftsOnPlan, on since 26 Sep): on Pro, Jev drafts the steps from the words, per step they
   *  name. If that switch is ever turned off, this line must say what Pro gives instead. */
  proLine: 'Jev drafts the steps for a task from your words in Pro.',
} as const;

/** What a part is, in the step row's words (the engine's DraftCopy.KindWord). */
export const KIND_WORD: Record<string, string> = {
  dax: 'formula', description: 'description', m: 'query', name: 'name', choice: 'choice',
};

/** The state word on the right of the draft's own head line (composer contract 10.3). The Plan row keeps the Plan's
 *  word; this is the draft's, inside its detail. */
export function stateLabel(v: DraftView): string {
  if (v.state === 'done') return v.banner?.kind === 'check_failed' ? '1 check failed' : 'Done';
  return ({ needs_you: 'Needs you', waiting_for_assistant: 'Waiting for your assistant', ready: 'Ready', running: 'Running', stopped: 'Stopped' } as Record<string, string>)[v.state] ?? v.state;
}

/** True once the draft has run: its steps are fixed and only what came after the change can move. */
export function hasRun(v: DraftView): boolean {
  return v.state === 'running' || v.state === 'done' || v.state === 'stopped' || !!v.pausedAt;
}

/** The step the run waits at, and whether it is a person's stop (its tag is You choose alone) or an assistant's step. */
export function pausedStep(v: DraftView): { step: DraftStepView; person: boolean } | null {
  const step = v.pausedAt ? [...v.steps, ...v.after].find((s) => s.id === v.pausedAt) : undefined;
  return step ? { step, person: !step.tags.includes('asst') } : null;
}

/** Can a step move up or down before the run (edit_draft reorder)? The engine refuses a move that breaks a dependency
 *  or the phases, so this only hides moves that go nowhere. */
export function canMove(v: DraftView, s: DraftStepView, by: -1 | 1): boolean {
  if (hasRun(v)) return false;
  const all = [...v.steps, ...v.after].map((x) => x.number);
  const to = s.number + by;
  return to >= 1 && to <= Math.max(...all);
}

/** A part still open for words: the ones Copy the request and Write it yourself apply to. */
export function isOpenPart(b: DraftBlankView): boolean {
  return b.state === 'empty' || b.state === 'queued' || b.state === 'writing';
}

export type PartAction = 'write' | 'copy' | 'drop' | 'pick' | 'ask_again';

/** The controls on one part in Before it runs (composer contract 9 and 5.4). A part waiting for words offers exactly
 *  three, the same whether or not an assistant is attached: Write it yourself, Copy the request, Drop. A part that
 *  needs the person offers the assistant's choices, Ask again, Write it yourself and Drop. A written part offers none. */
export function partActions(b: DraftBlankView, v: DraftView): PartAction[] {
  if (hasRun(v)) return [];
  if (b.state === 'needs') return [...(b.choices?.length ? ['pick' as const] : []), 'ask_again', 'write', 'drop'];
  if (isOpenPart(b)) return b.request ? ['write', 'copy', 'drop'] : ['write', 'drop'];
  return [];
}

export type StepControl = 'keepdrop' | 'writedrop' | 'status' | 'none';

/** What sits at the right of a step row. Before a run: Keep and Drop, or, while one of its parts is being written or
 *  needs the person, Write it yourself and Drop in their place (contract 10.3). After a run: the engine's status. */
export function stepControl(s: DraftStepView, v: DraftView): StepControl {
  if (s.status && (hasRun(v) || s.status === 'Dropped' || s.status === 'After the change')) return 'status';
  if (hasRun(v)) return 'none';
  const open = v.blanks.some((b) => b.stepId === s.id && (b.state === 'writing' || b.state === 'needs' || b.state === 'queued'));
  return open && s.kept ? 'writedrop' : 'keepdrop';
}

/** The first part of a step still open for words, for the step row's Write it yourself. */
export function openPartOf(s: DraftStepView, v: DraftView): DraftBlankView | undefined {
  return v.blanks.find((b) => b.stepId === s.id && (isOpenPart(b) || b.state === 'needs'));
}

export type Primary = 'run_with_assistant' | 'approve_and_run' | 'continue' | 'none';

/** The one primary button in the footer (contract 10.3 and 9). Approve and run when the draft is ready, or when a held
 *  draft's parts have all passed. Run with your assistant when parts are still to be sent: one click records the
 *  approval and puts them on the queue, and nothing else happens. While parts wait there is no button that opens an
 *  assistant (Kane 24 Sep): the footer says so instead. */
export function primaryOf(v: DraftView): Primary {
  // A run paused at a person's stop waits for Continue, the person's own (continueDraft on the human-only target).
  if (v.pausedAt && v.canContinue && pausedStep(v)?.person) return 'continue';
  if (hasRun(v)) return 'none';
  if (v.menu && v.steps.filter((s) => s.kept).length === 0) return 'none';
  if (v.state === 'ready') return 'approve_and_run';
  if (v.held && v.blanks.every((b) => !isOpenPart(b) && b.state !== 'needs') && !v.questions.some(unanswered)) return 'approve_and_run';
  const unsent = v.blanks.some((b) => b.state === 'empty');
  if (unsent && !v.approved) return 'run_with_assistant';
  return 'none';
}

/** Approve and run on a held draft is the person's own approval, on the human-only target; everywhere else it is
 *  runDraft (composer contract 5.4, lane L4 approveDraft). */
export function approveCall(v: DraftView): [string, string] {
  return [v.held ? 'approveDraft' : 'runDraft', v.draftId];
}

export function unanswered(q: DraftQuestionView): boolean { return q.answer == null || q.answer === ''; }

/** Questions the person answers in Before it runs. Semanticus's own answers are shown filled in and can be changed. */
export function askedQuestions(v: DraftView): DraftQuestionView[] {
  return v.questions.filter((q) => (q.by === 'you' || q.by === 'semanticus') && !q.atStop);
}

/** The questions of the person's stop the run waits at: asked there, beside Continue. */
export function stopQuestions(v: DraftView): DraftQuestionView[] {
  return v.pausedAt ? v.questions.filter((q) => q.atStop && q.step === v.pausedAt) : [];
}

export type QuestionInput = 'radio' | 'select' | 'text';
export function questionInput(q: DraftQuestionView): QuestionInput {
  if (q.type === 'enum' && (q.values?.length ?? 0) > 0 && (q.values?.length ?? 0) <= 6) return 'radio';
  if ((q.options?.length ?? 0) > 0 || (q.type === 'enum' && (q.values?.length ?? 0) > 0)) return 'select';
  return 'text';
}

/** The choices of a select or radio, as [value, label]. */
export function choicesOf(q: DraftQuestionView): [string, string][] {
  const choices: [string, string][] = q.options?.length ? q.options.map((o) => [o.ref, o.label]) : (q.values ?? []).map((x) => [x, x]);
  // What the engine holds is what the picker shows. An answer that is not one of the choices (kept before answers were
  // checked, or a list that changed) is shown as it is, never as the first choice (UAT S12-2-005).
  if (q.answer && choices.length > 0 && !choices.some(([value]) => value === q.answer))
    return [[q.answer, `Not in this model: ${q.answer}`], ...choices];
  return choices;
}

/** The name a person reads for one part: "Return rate formula" in the frame, from the step and the kind. */
export function partName(b: DraftBlankView, v: DraftView): string {
  const step = v.steps.find((s) => s.id === b.stepId);
  const what = KIND_WORD[b.kind] ?? b.kind;
  return step?.object ? `${step.object} ${what}` : step ? `${step.title}, ${what}` : `Step ${b.step}, ${what}`;
}

/** The buttons of the banner (contract 10.3). A failed after-check: Ask your assistant why, Keep it anyway, and Undo
 *  as the primary; Semanticus does not guess why. A stop that kept steps: Undo step 1 and Edit in Workflows. A stop
 *  that changed nothing, or a restart: Edit in Workflows, and Run it again. */
export type BannerAction = 'try_again' | 'ask_why' | 'keep_anyway' | 'undo' | 'undo_step' | 'edit' | 'run_again' | 'keep_theirs' | 'apply_draft';
export function bannerActions(v: DraftView): BannerAction[] {
  switch (v.banner?.kind) {
    // A step after the change that could not run (no connection) still has Try again; the banner used to hide it.
    case 'check_failed': return [...(v.canTryAgain ? ['try_again' as const] : []), 'ask_why', 'keep_anyway', 'undo'];
    case 'stopped': return ['undo_step', 'edit'];
    case 'nothing_changed': case 'restarted': return ['edit', 'run_again'];
    // Keep yours is first and plain: nothing the person wrote is lost by pressing it (UAT S12-5-004).
    case 'overwrite': return ['keep_theirs', 'apply_draft'];
    default: return [];
  }
}

/** What the footer shows once it ran: Undo and Make it reusable when Done, and Try again on a step after the change
 *  that did not run (the Sol F-269 follow-up). A failed check shows its buttons in the banner instead. */
export type DoneAction = 'undo' | 'reusable' | 'try_again';
export function doneActions(v: DraftView): DoneAction[] {
  if (v.state !== 'done' || v.banner) return [];
  return [...(v.canTryAgain ? ['try_again' as const] : []), 'undo', 'reusable'];
}

/** Add more words is on a draft from words, before it runs (contract 6: refine). */
export function canRefine(v: DraftView): boolean { return !!v.menuLine && !hasRun(v); }

/** The menu (lane L5a): the matches first, then the rest behind Show all. */
export function menuRows(v: DraftView, showAll: boolean): { rows: DraftMenuItem[]; more: number } {
  const menu = v.menu ?? [];
  const matched = menu.filter((m) => m.matched);
  const shown = showAll || matched.length === 0 ? menu : matched;
  return { rows: shown, more: menu.length - shown.length };
}

/** "Ask your assistant why" puts one task on the Plan for the assistant, in the person's words (it is the person
 *  asking), with the engine's own banner line quoted so nothing is paraphrased. */
export function askWhyWords(v: DraftView): string {
  return `Why did a check fail after "${v.title}"? ${v.banner?.text ?? ''}`.trim();
}

/** Where Edit in Workflows and Make it reusable go: Workflows, Author, with this draft (lane W2 reads the target). */
export function authorTarget(v: DraftView): string { return `draft:${v.draftId}`; }

/** Is this ask a request to MAKE a new model from a source? The engine's AskMakeIntent.Classify is the same rule, word for
 *  word (the lists below are its lists, and test/sql-start.test.mjs fails when they differ), and one list of asks
 *  (test/model-from-sql-asks.json) is run through both doors. The ask is read by its intent: lead-ins are stepped over,
 *  the MAIN VERB is a making verb, a request word with an indefinite object ("I need a new model"), "turn ... into a new
 *  model", the verb "model" on a source, or a bare "new model ...", its object is a model or dataset, a source is named, and
 *  nothing else is asked of the model (no other verb, clause or complement after the source's own name). "yes" opens the
 *  Model Spec, "no" never does, and "unsure" is the one case the words cannot settle: the engine asks the decision service
 *  once and, with none, answers no.
 *  NO PAGE CALLS THIS TODAY (m11 join part 2): the lane wrote it for the Ctrl J bar, and Ctrl J routes nothing (Kane, 27 Sep),
 *  so the page's only route is the Plan's field, which asks the engine (routeAsk) and so meets AskMakeIntent itself. It stays
 *  because test/sql-start.test.mjs holds the two lists and verdicts equal, so the day the bar routes again it routes by the
 *  engine's rule. */
export type MakeIntent = 'yes' | 'no' | 'unsure';
export const MAKING_WORDS = ['build', 'create', 'make', 'generate', 'draft', 'start', 'begin', 'set up', 'spin up', 'put together', 'scaffold', 'produce', 'design'];
export const REQUEST_WORDS = ['i need', 'we need', 'i want', 'we want', 'i d like', 'we d like', 'i would like', 'we would like', 'i d love', 'we d love', 'i would love',
  'i m after', 'i am after', 'looking for', 'i m looking for', 'i am looking for', 'give me', 'get me', 'i require'];
export const CONVERT_WORDS = ['turn', 'convert', 'transform', 'import', 'load', 'pull', 'bring'];
export const PRE_VERBS = ['connect', 'link', 'point', 'hook up', 'use', 'using', 'import', 'load', 'pull', 'take', 'grab', 'read'];
export const LEAD_INS = ['i want you to', 'i need you to', 'i d like you to', 'i want to', 'i need to', 'i would like to', 'i d like to', 'we want to', 'we need to',
  'we d like to', 'i wanna', 'i m trying to', 'i am trying to', 'i m looking to', 'i am looking to', 'i have to', 'open the page to',
  'open the wizard to', 'show me how to', 'walk me through how to', 'walk me through', 'help me to', 'help me', 'let s', 'let us', 'lets',
  'can you', 'could you', 'would you', 'will you', 'can i', 'could i', 'should i', 'shall i', 'should we', 'how do i', 'how can i',
  'how would i', 'how should i', 'how do we', 'how can we', 'how to', 'is it possible to', 'is it possible for me to', 'is there a way to',
  'what is the way to', 'what is the best way to', 'what s the way to', 'what s the best way to', 'tell me how to', 'please', 'pls', 'hey', 'ok', 'okay', 'so', 'now', 'just', 'kindly', 'go ahead and', 'want to', 'need to'];
export const PREPOSITION_WORDS = ['from', 'out', 'using', 'with', 'off', 'on', 'in', 'at', 'over', 'via', 'by', 'through', 'based', 'into', 'for', 'against', 'atop', 'under',
  'connected', 'pointing', 'backed', 'sitting', 'of', 'as'];
/** What may follow a model or dataset for it to be the thing made (the engine's ObjectFollowers). Any other word after it is what
 *  is made, and "model" only names it: "model documentation", "a model diagram" (m11 join part 2, item 7). */
export const OBJECT_FOLLOWERS = ['please', 'pls', 'thanks', 'thank', 'now', 'for', 'called', 'named', 'built', 'made', 'created', 'generated', 'sourced', 'fed', 'drawn', 'pulled',
  'loaded', 'powered'];
export const JOINER_WORDS = ['that', 'which', 'so', 'but', 'while', 'when', 'if', 'because', 'then'];
export const HEAD_WORDS = ['model', 'dataset', 'modeling', 'modelling'];
export const SOURCE_WORDS = ['sql', 'warehouse', 'database', 'db', 'endpoint', 'lakehouse', 'source', 'server', 'fabric', 'synapse', 'datamart', 'rdbms',
  'postgres', 'postgresql', 'mysql', 'oracle', 'snowflake', 'bigquery', 'redshift', 'databricks'];
export const TAIL_WORDS = ['database', 'databases', 'db', 'warehouse', 'warehouses', 'endpoint', 'endpoints', 'analytics', 'server', 'servers', 'source', 'sources',
  'table', 'tables', 'view', 'views', 'lakehouse', 'instance', 'for me', 'please', 'schema', 'schemas', 'metadata', 'keys', 'key',
  'connection', 'connections', 'backend', 'thanks', 'thank you', 'now', 'from scratch', 'to start', 'again'];
export const ACTION_WORDS = ['refresh', 'reload', 'restart', 'publish', 'deploy', 'update', 'check', 'add', 'remove', 'delete', 'rename', 'fix', 'document', 'compare',
  'validate', 'test', 'run', 'schedule', 'sync', 'secure', 'protect', 'apply', 'enable', 'disable', 'mark', 'hide', 'show', 'list', 'open',
  'edit', 'change', 'move', 'copy', 'share', 'explain', 'describe', 'review', 'optimize', 'optimise', 'tune', 'analyse', 'analyze', 'audit',
  'profile', 'monitor', 'undo', 'redo', 'revert', 'save', 'export', 'reconcile', 'rebuild', 'repair', 'patch', 'diagnose', 'debug', 'inspect',
  'view', 'browse', 'query', 'find', 'search', 'count', 'sort', 'filter', 'group', 'tag', 'label', 'translate', 'format', 'clean', 'merge',
  'split', 'lock', 'unlock', 'grant', 'revoke', 'back', 'restore'];
export const EXISTING_WORDS = ['this', 'that', 'current', 'currently', 'open', 'opened', 'existing', 'already', 'active', 'loaded', 'same', 'present'];
export const QUESTION_STARTS = ['what', 'where', 'which', 'who', 'whom', 'whose', 'why', 'when', 'is', 'are', 'does', 'do', 'did', 'should', 'will', 'has', 'have'];
const INDEFINITE = ['a', 'an', 'new', 'fresh', 'another', 'brand', 'blank'];
const MAKE_IDIOMS = ['sure', 'certain', 'sense', 'room', 'time', 'it', 'that', 'do', 'up', 'way', 'use', 'progress', 'peace'];
const START_IDIOMS = ['up', 'over', 'again'];
const POLITE = ['for me', 'thank you', 'from scratch', 'to start'];
const NEW_WORDS = ['new', 'fresh', 'blank', 'brand', 'another'];
const PRONOUNS = ['it', 'them', 'this', 'that'];
const LOOKED_BACK = ['i have', 'we have', 'we built', 'we made', 'i built', 'i made', 'we created', 'already'];

/** How many tokens of the longest phrase in the list start at index `at`, or 0. */
function phraseAt(tok: string[], at: number, phrases: string[]): number {
  let best = 0;
  for (const p of phrases) {
    const w = p.split(' ');
    if (w.length <= best || at + w.length > tok.length) continue;
    if (w.every((x, k) => tok[at + k] === x)) best = w.length;
  }
  return best;
}
const isSource = (w: string) => SOURCE_WORDS.includes(w);

/** Where the object's noun phrase ends: just after the model or dataset, or -1 when a preposition, another clause or the end of
 *  the ask comes first (the object is then something else: a report, a description, a query). */
function objectEnd(tok: string[], j: number): number {
  for (let k = j; k < tok.length; k++) {
    const w = tok[k];
    if (HEAD_WORDS.includes(w) && objectIsHead(tok, k + 1)) return k + 1;
    if (w === 'and' && k > j && tok[k - 1] === 'tables' && k + 1 < tok.length && tok[k + 1] === 'relationships') return k + 2;
    if (PREPOSITION_WORDS.includes(w) || JOINER_WORDS.includes(w) || w === 'and') return -1;
    if (k - j > 7) return -1;
  }
  return -1;
}

/** Whether the model or dataset just before `next` is the thing made: nothing follows it, or a word that ends the noun phrase does. */
function objectIsHead(tok: string[], next: number): boolean {
  if (next >= tok.length) return true;
  const w = tok[next];
  return PREPOSITION_WORDS.includes(w) || JOINER_WORDS.includes(w) || w === 'and' || OBJECT_FOLLOWERS.includes(w) || isSource(w);
}

/** After the object: a source is named somewhere, no other verb or clause follows it, and only the source's own name and a
 *  polite end come after the last source word. */
function restOk(tok: string[], end: number, sourceBefore: boolean): boolean {
  const rest = tok.slice(end);
  if (!sourceBefore && !rest.some(isSource)) return false;
  if (rest.some((w) => JOINER_WORDS.includes(w) || ACTION_WORDS.includes(w))) return false;
  for (let a = 0; a + 1 < rest.length; a++)
    if (rest[a] === 'and' && (ACTION_WORDS.includes(rest[a + 1]) || PRONOUNS.includes(rest[a + 1])
      || MAKING_WORDS.some((v) => v.split(' ')[0] === rest[a + 1]))) return false;
  let last = -1;
  for (let a = 0; a < rest.length; a++) if (isSource(rest[a])) last = a;
  const tail = rest.slice(last + 1);
  let at = 0;
  while (at < tail.length) {
    const polite = POLITE.find((p) => { const w = p.split(' '); return at + w.length <= tail.length && w.every((x, q) => tail[at + q] === x); });
    if (polite) { at += polite.split(' ').length; continue; }
    if (TAIL_WORDS.includes(tail[at]) || /^\p{N}+$/u.test(tail[at])) { at++; continue; }
    return false;
  }
  return true;
}

type Parsed = 'yes' | 'no' | 'noverb';
function parseIntent(tok: string[], i: number): Parsed {
  const n = tok.length;
  // 1. A making verb, first one in the ask. Before it only a clause that names the source may stand.
  for (let m = i; m < Math.min(n, i + 14); m++) {
    const k = phraseAt(tok, m, MAKING_WORDS);
    if (k === 0) continue;
    const pre = tok.slice(i, m);
    if (m > i && !(phraseAt(pre, 0, PRE_VERBS) > 0 && ['and', 'then', 'to'].includes(pre[pre.length - 1]) && pre.some(isSource))) break;
    let j = m + k;
    if (tok[m] === 'make' && j < n && MAKE_IDIOMS.includes(tok[j])) return 'no';
    if (tok[m] === 'start' && j < n && START_IDIOMS.includes(tok[j])) return 'no';
    if (j < n && (tok[j] === 'me' || tok[j] === 'us')) j++;
    if ((tok[m] === 'start' || tok[m] === 'begin') && j < n && tok[j] === 'with') j++;
    const sourceBefore = pre.some(isSource);
    if ((tok[m] === 'start' || tok[m] === 'begin') && j < n && tok[j] === 'from') return restOk(tok, j, sourceBefore) ? 'yes' : 'no';
    const end = objectEnd(tok, j);
    if (end < 0) return 'no';
    const np = tok.slice(j, end);
    if (np.some((w) => EXISTING_WORDS.includes(w))) return 'no';
    // "build the model from sql again": made before, so it exists. "build a new model from sql again" is still a new one.
    if (tok.slice(end).includes('again') && !np.some((w) => INDEFINITE.includes(w))) return 'no';
    return restOk(tok, end, sourceBefore || np.some(isSource)) ? 'yes' : 'no';
  }
  // 2. The verb "model" on a source: "model this database from scratch".
  if (tok[i] === 'model') {
    const j = i + 1;
    let k = j;
    while (k < n && !PREPOSITION_WORDS.includes(tok[k]) && !JOINER_WORDS.includes(tok[k]) && tok[k] !== 'and') k++;
    if (!tok.slice(j, k).some(isSource)) return 'no';
    return restOk(tok, k, true) ? 'yes' : 'no';
  }
  // 3. "I need a new model from ...": a request word, and an indefinite or new object.
  const r = phraseAt(tok, i, REQUEST_WORDS);
  if (r > 0) {
    const j = i + r;
    if (j >= n || !INDEFINITE.includes(tok[j])) return 'no';
    const end = objectEnd(tok, j);
    if (end < 0) return 'no';
    const np = tok.slice(j, end);
    if (np.some((w) => EXISTING_WORDS.includes(w))) return 'no';
    return restOk(tok, end, np.some(isSource)) ? 'yes' : 'no';
  }
  // 4. A bare "new model please, from SQL Server".
  if (NEW_WORDS.includes(tok[i])) {
    const end = objectEnd(tok, i);
    if (end < 0) return 'no';
    return restOk(tok, end, tok.slice(i, end).some(isSource)) ? 'yes' : 'no';
  }
  // 5. "turn these tables into a new model", "import my database as a new model", "convert the tables to a new model".
  if (CONVERT_WORDS.includes(tok[i])) {
    let k = i + 1;
    while (k < n && tok[k] !== 'into' && tok[k] !== 'as' && tok[k] !== 'to') {
      if (JOINER_WORDS.includes(tok[k])) return 'no';
      k++;
    }
    if (k >= n || !tok.slice(i + 1, k).some(isSource)) return 'no';
    // What is turned is a source; a model turned into another kind of model is a change to a model that exists.
    if (tok.slice(i + 1, k).some((w) => HEAD_WORDS.includes(w))) return 'no';
    const j = k + 1;
    if (j >= n || !INDEFINITE.includes(tok[j])) return 'no';
    const end = objectEnd(tok, j);
    if (end < 0) return 'no';
    return restOk(tok, end, true) ? 'yes' : 'no';
  }
  return 'noverb';
}

/** No main verb the rules know, yet the ask names a model, a source and something that sounds like making one, and nothing
 *  says the model is the one that is open. This is the only case the decision service is asked about. */
function soundsLikeMaking(tok: string[]): boolean {
  if (!tok.some((w) => HEAD_WORDS.includes(w)) || !tok.some(isSource)) return false;
  for (let k = 0; k < tok.length; k++) {
    if (!HEAD_WORDS.includes(tok[k])) continue;
    for (let x = Math.max(0, k - 3); x < k; x++) if (EXISTING_WORDS.includes(tok[x])) return false;
    const after = tok.slice(k + 1, k + 4).join(' ');
    if (LOOKED_BACK.some((p) => after.includes(p))) return false;
  }
  const makeish = [...MAKING_WORDS.map((v) => v.split(' ')[0]), 'new', 'fresh', 'into', 'turn', 'convert', 'transform', 'import'];
  return tok.some((w) => makeish.includes(w));
}

/** Lower-cased, punctuation as spaces, as the engine does it. Letters are Unicode on purpose: an accented name is a word. */
const tokens = (text: string): string[] => text.toLowerCase().replace(/[^\p{L}\p{N}]+/gu, ' ').trim().split(' ').filter((w) => w.length > 0);

export function classifyMakeIntent(text: string): MakeIntent {
  const tok = tokens(text);
  if (tok.length === 0) return 'no';
  let i = 0;
  for (;;) { const n = phraseAt(tok, i, LEAD_INS); if (n === 0 || i >= tok.length) break; i += n; }
  if (i >= tok.length) return 'no';
  if (i === 0 && QUESTION_STARTS.includes(tok[0])) return 'no';
  if (ACTION_WORDS.includes(tok[i])) return 'no';
  const parsed = parseIntent(tok, i);
  if (parsed === 'yes') return 'yes';
  if (parsed === 'no') return 'no';
  return soundsLikeMaking(tok) ? 'unsure' : 'no';
}

/** Words that NAME the new model wizard rather than a change to this model: "model spec", "build a model from scratch", and any
 *  request to make a model from a source (classifyMakeIntent). The lane's ask bar routed them so the answer was the place (the
 *  Model Spec, whose empty-model view is the wizard); Ctrl J now routes nothing (Kane, 27 Sep), and the engine's words pass
 *  holds the same rule for the Plan's field. Plan statement check, 28 Sep, defect 6: they
 *  drafted a task with a pick list of edit steps, none of which builds a model. Deliberately narrow: "build a date table" is
 *  still a task. */
const PLACE_ASK = [
  /^(?:(?:open|show|find|go to)\s+)?(?:me\s+)?(?:the\s+)?(?:model spec|new model wizard|model spec wizard)\b/i,
  /(?:^| )(?:build|create|make|start|draft|generate|set up|new)(?: me)?(?: (?:a|an|the|my|our|new|power bi|semantic|starter|data|tabular|first))* model from scratch(?: |$)/u,
];
export function isPlaceAsk(text: string): boolean {
  const t = text.toLowerCase().replace(/[^\p{L}\p{N}]+/gu, ' ').trim();
  return PLACE_ASK.some((re) => re.test(t)) || classifyMakeIntent(text) === 'yes';
}
/** An ask the words cannot settle (classifyMakeIntent says "unsure"): the engine asks the decision service once (a new model,
 *  this model, or neither) and, with no service, routes it as it would any other ask. */
export function isMakeUnsure(text: string): boolean {
  return classifyMakeIntent(text) === 'unsure';
}
