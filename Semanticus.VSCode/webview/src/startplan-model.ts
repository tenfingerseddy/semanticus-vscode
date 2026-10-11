// The start plan's page, as plain functions (lane sf-studio, L12). Nothing here touches React or the wire: every
// decision the page makes about WHAT TO SAY is a function of the engine's view, so the test executes it.
//
// The view is Semanticus.Engine/StartPlan/StartPlanContract.cs StartPlanView, camelCased on the wire. The page is
// option A of the spec-first start (Kane, 1 Oct): one list on the Plan page in five sections, a Needs you band, B's
// link drawing at the top of Links and dates, C's source name beside the model name.
//
// THE RULES THIS FILE HOLDS, because a page cannot be trusted to remember them:
//   - A person's approval is bound to the digest they LOOKED at (S1). approveArgs and buildArgs send the view's own
//     digest and nothing else, so a plan that moved under the person is refused by the engine, never approved quietly.
//   - Approval is person-only, and the page says so truthfully (K3): after the person approves, the person or their
//     assistant may press Build. An approval the digest has moved past is shown as stale, never as standing.
//   - Free never reads as Jev (K2): the lines Jev would have decided are marked Default, with no Jev progress and no
//     Jev question. Pro is the only place Jev is named as having decided anything.
//   - Incomplete never reads as done (S10): it says it is not finished and why.
//   - Nothing is drawn that the plan does not say: no links, no drawing.

import { defaultSignInMode } from './copy';

// ---------------------------------------------------------------- the wire shape (camelCase of the contract)

export type StartDecidedBy = 'rule' | 'jev' | 'assistant' | 'person';
export type StartDecideState = 'off' | 'waiting_grant' | 'deciding' | 'stable' | 'incomplete';

export interface StartItem {
  id: string; objectRef?: string; objectName?: string; kind?: string; title?: string; rationale?: string;
  before?: string | null; after?: string | null; status?: string;
}
export interface StartLine {
  item?: StartItem; decidedBy: StartDecidedBy; point?: string | null; word?: string | null; round?: number; kept: boolean; why?: string | null;
  /** Plain words while the line is HELD (StartLine.Held): made by the person or their assistant, its object is out of the plan
   *  right now, so it is kept and shown but NOT built, and it comes back with its object. Null when the line stands. */
  held?: string | null;
  /** True on a line the view makes from the manifest (a time measure): Drop and Keep work on it, a formula cannot be pinned. */
  derived?: boolean;
  /** What the build sets on this line's object besides what its title says, in plain words (the folder, the number format). */
  sets?: StartSet[] | null;
  /** Plain words for why the name of the table or measure this line is about would still be flagged by the naming rules, when nobody has kept it. Null otherwise (StartLine.NameFlag). */
  nameFlag?: string | null;
  /** NOT in the contract. Shown only when the engine sends it. */
  confidence?: number;
}
/** One property the build sets on a line's object, in plain words (StartSet): a measure's folder, its number format. */
export interface StartSet { ref: string; property: string; value?: string; words: string }
/** One column of a table, as the review lists it (StartColumn). */
export interface StartColumn { ref: string; name: string; sourceName?: string | null; dataType?: string; included: boolean; hidden: boolean; key: boolean; summarize?: string | null; nameFlag?: string | null }
export interface StartGroup {
  table?: string | null; sourceName?: string | null; lines: StartLine[];
  /** The table's columns (Tables and columns group only), listed folded under the table's line. */
  columns?: StartColumn[] | null;
  /** The person refused this table's sample: Jev sees its names and settings, never facts worked out from its rows. */
  sampleOff?: boolean;
}
export interface StartCounts { rule: number; jev: number; assistant: number; you: number }
export interface StartSection { id: string; title?: string; summary?: string; counts?: StartCounts; groups: StartGroup[] }
export interface StartNeed { id: string; kind: string; text: string; options?: string[]; recommended?: string | null }
export interface StartFamilyProgress { family: string; asked: number; answered: number; unusable: number; remaining: number }
export interface StartDecide { state: StartDecideState; round?: number; reason?: string | null; progress?: StartFamilyProgress[] }
export interface StartLinkEdge { from: string; to: string; active: boolean; note?: string | null; id?: string }
export interface StartPlanView {
  exists: boolean; id?: string;
  destination?: { folder?: string; name?: string; modelPath?: string };
  source?: { server?: string; database?: string; authMode?: string; tenantId?: string; storageMode?: string };
  sections: StartSection[]; needsYou: StartNeed[];
  decide?: StartDecide; words?: { total: number; open: number };
  grades?: StartGrades;
  digest?: string; approval?: { digest?: string | null; utc?: string | null } | null;
  canBuild?: boolean; blockedBecause?: string | null;
  /** The plan was built and its model still holds what it built (an undo makes it false again). */
  built?: boolean;
  /** Facts from a sample of the source's rows are on, and where the sample read stands. */
  sampleFacts?: boolean; sample?: StartSample | null;
  /** The person chose to carry on without Jev (Pro). */
  jevDeclined?: boolean;
  /** One sentence about what this call did beside its answer (which open model a start closed). */
  notice?: string | null;
  /** The links the build writes, exactly (the engine's own list, after drops and leave-outs): the drawing and the "N links, M on" count are made from it. A plan from a source
   *  that sends none (a fixture, an older engine) is read from its Links lines instead. */
  links?: StartLinkEdge[];
}
export interface StartGrades {
  now?: number | null; afterBuild?: number | null;
  /** Letters A to F. */
  nowGrade?: string | null; afterBuildGrade?: string | null; nowBestPractice?: string | null; afterBuildBestPractice?: string | null;
  /** The errors and warnings the preview's own best practice scan says stay open after the build (what the letter weighs). Absent: not known. */
  afterBuildBestPracticeOpen?: number | null;
  /** True until the decide has settled and the preview is current: "after the build" can still move. */
  afterBuildProvisional?: boolean;
}
export interface StartSample { state: 'none' | 'reading' | 'read' | 'refused'; reason?: string | null; measured?: number; notMeasured?: number }
export interface StartEdit { op: string; target: string; value?: string }
export interface Tier { pro: boolean }

// ---------------------------------------------------------------- the words (one table, so the copy test reads it all)

export const COPY = {
  // What a real Free plan holds (sf-wiring-real-drive, 2 Oct): the source's own draft and the assistant's words. No line is
  // marked Default there, so the note promises none.
  freeNotice: 'Free plan. Semanticus drafts the model from the source: the tables, the links, the date table and the starter measures. Your assistant writes the words. Jev is Pro, so nothing here was decided by Jev. Best practice fixes come after the build, one at a time.',
  changedTip: 'Lines that moved since your last change here, including what your assistant wrote.',
  bpLetter: (letter: string) => `Best practice ${letter}`,
  bpUnknown: 'not measured yet',
  bpOneAtATime: 'Best practice fixes come one at a time, on the Plan, after the build',
  needsTitle: 'Needs you',
  needsLead: (n: number) => `Jev left ${n} ${n === 1 ? 'line' : 'lines'} for you to decide. Each stays out of the build until you answer. Skip it and it becomes a Plan row after the build.`,
  needsOther: 'These wait on you. The rest of the plan is ready.',
  // A pick the rule made because Jev was not sure is IN the build (the plan uses it); only a line Jev left out stays out (UAT-J7-S2-004).
  needsPicks: (n: number) => `Jev was not sure about ${n} ${n === 1 ? 'total' : 'totals'}, so the plan builds the rule's pick for ${n === 1 ? 'it' : 'each'}. Answer to change it.`,
  leans: 'Jev leans',
  suggested: 'Suggested',
  notNow: 'Not now',
  stateDecided: 'Decided', stateWritten: 'Written', stateDefault: 'Default, no Jev', stateLeftOutNeed: 'Left out until you answer',
  stateLeftOut: 'Left out', stateLeftOutYou: 'Left out by you', stateOpen: 'Stays open', stateOff: 'Kept off', stateHeld: 'Held, not built', stateNotBuilt: 'Not built',
  approveAndBuild: 'Approve and build', approveOnly: 'Approve only', build: 'Build', approveAgain: 'Approve again',
  approveAndBuildTip: 'You approve this exact plan, then it is built as one step. One undo takes it back.',
  approveOnlyTip: 'You approve this exact plan and stop. Then you or your assistant can build it.',
  buildTip: 'Builds the plan you approved, as one step. One undo takes it back.',
  undoLine: 'One undo takes the whole build back.',
  approvalNone: '',
  approvalCurrent: 'You approved this plan. You or your assistant can build it. Any change you make, or words your assistant adds, removes the approval.',
  approvalStale: 'Your approval no longer matches the plan. Something changed after you approved, such as words your assistant wrote. Approve again to build.',
  approvedByYou: 'Approved by you',
  noPlan: 'There is no plan to approve.',
  approveFirst: 'Approve the plan first. Only you can approve it.',
  alreadyApproved: 'You already approved this plan.',
  stillMoving: 'Jev is still deciding. Approve when it stops.',
  waitingGrant: 'Jev is waiting for your answer to the first question.',
  built: 'This plan is already built.',
  discard: 'Discard this plan',
  seeBuilt: 'See what was built',
  backToPlan: 'Back to the Plan',
  building: (name: string) => `Building ${name}. This takes a few seconds. One undo takes it back.`,
  discardTip: 'Throws the plan away. The empty model stays where it was saved.',
  discardBuiltTip: 'Throws the plan away. The model you built stays as it is, and the Plan shows its own tasks again.',
  // the foot, one per state (footOf)
  footWaiting: (n: number) => `${n} ${n === 1 ? 'question waits' : 'questions wait'} on you.`,
  footReady: 'Ready to approve.',
  footDeciding: 'Jev is still deciding.', footDecidingRest: 'You can approve the plan when it stops.',
  footApproved: 'You approved this plan.', footApprovedRest: 'Build makes it as one step. You or your assistant can press Build. One undo takes the whole build back.',
  footStale: 'The plan changed after you approved it.', footStaleRest: 'Look it over and approve it again before it can be built.',
  footBuilt: (name: string) => `Built into ${name}.`,
  footBuiltRest: 'Undo takes the whole build back. Discard the plan when you are done with it, and the Plan shows the model\'s own tasks.',
  builtNote: (name: string) => `${name} was built from this plan. Lines left out or held were not built.`,
  sourceNotBuilt: 'Model not built yet', sourceBuilt: 'Model built from this plan',
  // the grades
  gradeProvisionalJev: 'may still change while Jev works',
  gradeProvisional: 'may still change while the plan is worked out',
  gradeBuilt: 'as built',
  gradeBestPractice: (letter: string) => `Best practice ${letter}`,
  // what the facts count
  heldFact: 'held, not built',
  // a start that made no plan
  startNoPlan: 'Semanticus could not start a plan from that source. Check the server and database, then try again.',
  askAssistant: 'Ask your assistant',
  askAssistantTip: 'Copies a short request. Paste it into your assistant and it writes the words for this plan.',
  readingPlan: 'Reading the plan',
  readingPlanLead: 'Semanticus is reading the source and deciding what to build.',
  planFailed: 'The plan could not be read',
  tryAgain: 'Try again',
  emptyNote: (folder: string) => `The empty model is saved in ${folder}. No table, measure or link is written until you approve and build.`,
  filterHint: 'Open any row to see why, or change it.',
  groupWhole: 'Whole model',
  keep: 'Keep', drop: 'Drop', pin: 'Keep this value',
  rename: 'Rename', keepName: 'Keep this name', nameFlagged: 'Name reads as a code',
  orphanLine: 'A line the plan no longer shows', keepNameNote: 'Keep it and the question closes: the build accepts the finding and your assistant is not asked for a new name.', editWords: 'Edit the words', save: 'Save', cancel: 'Cancel', newName: 'New name', theWords: 'The words',
  // The words editor meeting newer words (UAT-J8-A-002): the same choice every other editor offers (editbase.tsx).
  keepMine: 'Keep mine', useNew: 'Use the new one', yourWords: 'Your words', newWords: 'The new words',
  wordsMoved: 'These words changed while you were editing them. Your words are still here, and nothing was saved yet.',
  keepMineTip: 'Keep what you typed. The next Save writes it over the new words.', useNewTip: 'Drop what you typed and load the new words.',
  wordsChooseFirst: 'Choose Keep mine or Use the new one first.',
  // The typed words outlive the editor (join 8 fix round 2, D1): a row switch, a restarted engine and a failed read never take them.
  draftFlag: 'Draft not saved', renameMoved: (now: string) => `This name changed to "${now}" while you were away. Your words are still here, and nothing was saved yet.`, discardWords: 'Discard my words',
  draftKeptTip: 'You typed words here and have not saved them. Open the row to carry on.',
  engineRestarting: 'Semanticus is not answering; your typed words are kept.',
  engineRestartingNoDraft: 'Semanticus is not answering. The plan shows as it was, and changes wait until it answers.', restartEngine: 'Restart engine', restartEngineTip: 'Start the engine again. Your typed words are kept.',
  unsavedTitle: 'Some rows hold words you have not saved', unsavedLead: 'The build does not include them:', saveFirst: 'Save them first', saveFirstTip: 'Open the first of these rows, so you can save its words. Nothing is built.',
  openedHeld: (name: string, more: number) => `Opened ${name}, where your words are waiting for you to save them.${more > 0 ? ` ${more} more ${more === 1 ? 'row holds' : 'rows hold'} words too.` : ''} Nothing was built.`,
  heldGone: 'The rows that held your words are not in the plan any more. Look at the note above the plan.', buildWithout: 'Build without them', buildWithoutTip: 'Build the plan as it stands. Your unsaved words are not built. They stay on this page, listed after the build, until you discard them.', keptAfterBuildTitle: 'Words you did not save are kept.', keptAfterBuildLead: 'The build did not include them and nothing was written for them. Copy them, or discard them when you are done.', keptGoneLead: 'The plan they were typed for is gone, and nothing was written for them. Copy them, or discard them when you are done.', keptRetiredOne: 'Words kept from a plan that was discarded (1)', keptRetiredMany: (n: number) => `Words kept from a plan that was discarded (${n})`, keptRetiredLead: 'They were typed for a plan that is gone, and nothing was written for them. Copy them, or discard them when you are done.', copyWords: 'Copy',
  readFailed: 'Semanticus could not read the plan just now. The plan shows as it was.', readFailedDraft: ' Your typed words are kept.',
  saveWaits: 'Save waits until Semanticus answers. Your typed words are kept.',
  detailSets: 'The build also sets',
  columnsFold: (n: number) => `${n} ${n === 1 ? 'column' : 'columns'}`,
  columnLeaveOut: 'Leave out', columnBringBack: 'Bring back', columnLeftOut: 'left out', columnHidden: 'hidden', columnKey: 'key', columnRenamed: (from: string) => `was ${from}`,
  gradeBuiltScored: 'Scored on the model as built',
  dateTableNote: 'The plan chooses the date table. Leave the table out or rename it to change it.',
  pinNote: 'A change you make holds: no later round undoes it. Only the lines that depend on it are worked out again.',
  keepNote: 'Keep puts the line back in the build. Drop leaves it out.',
  detailWhy: 'Why', detailChange: 'What changes', detailSource: 'From the source', detailWho: 'Who decides this', detailChoice: 'Your choice',
  whoRule: 'A Semanticus rule. It works the same every time.',
  whoJev: 'Jev, the decision service. It decides how columns total, formats, measure folders and what Copilot sees.',
  whoDefault: 'Jev is Pro, so this keeps its plain default. You can change it here.',
  whoAssistant: 'Your assistant writes it, on its own calls.',
  whoPerson: 'Only you.',
  linksDrawingTitle: 'How the tables link',
  linksDrawingLead: (on: number, off: number) => `${on} link${on === 1 ? '' : 's'} on${off ? `, ${off} kept off` : ''}. An arrow points from the table with many rows to the table with one.`,
  linkOn: 'link on', linkOff: 'kept off',
  linksDrawingLabel: 'The tables of the model and how they link',
  gradeAI: 'AI readiness', gradeBP: 'Model quality',
  gradeNow: 'Raw tables', gradeAfter: 'after the build',
  gradeUnknown: 'not measured yet',
  gradeNotScored: 'The raw tables are not scored yet',
  linesInPlan: 'Lines in the plan', wordsTitle: 'Words',
  legendJev: 'Jev decided', legendRule: 'Semanticus rule', legendYou: 'You', legendAssistant: 'Your assistant wrote', legendDefault: 'Default, no Jev (Free)',
  tabAll: 'All', tabJev: 'Jev', tabDefault: 'Default', tabRule: 'Rules', tabAssistant: 'Your assistant', tabYou: 'You', tabChanged: 'Changed',
  sectionTitles: { tables: 'Tables and columns', links: 'Links and dates', measures: 'Measures', words: 'Words for Copilot', bestpractice: 'Best practice' } as Record<string, string>,
  sectionBlurbs: {
    tables: 'What is built, keys, totals, formats', links: 'Each link on or off, the date table, year to date',
    measures: 'Starter totals, time measures, folders and formats', words: 'Names, descriptions, synonyms, instructions, what Copilot sees',
    bestpractice: 'Fixes made in the build, and what stays open',
  } as Record<string, string>,
  // the start form
  formTitle: 'Start a model from a SQL source',
  formLead: 'Semanticus reads the tables, columns and keys, and a sample of the data, then drafts the whole model for you to approve. No rows are copied into the model and no password is kept.',
  formEndpoint: 'SQL endpoint', formName: 'Model name', formFolder: 'Folder', formBrowse: 'Browse',
  formFolderHint: 'Choose an empty folder. Starting creates an empty model in it, in a new folder named for the model and ending in .SemanticModel. No table, measure or link is written until you approve the plan and build.',
  formStart: 'Read the source', formReading: 'Reading the source',
  formUnsaved: 'The open model has changes that are not saved. Starting a new model replaces it, and those changes are lost.',
  formDiscardStart: 'Discard those changes and read the source',
  formAdd: 'Add one on Connections', formNone: 'No SQL endpoint is saved on Connections yet. Save one there once and the plan uses its sign-in, with no password kept.',
  formReadingList: 'Reading your SQL endpoints from Connections...',
  formListFailed: 'The SQL endpoints saved on Connections could not be read just now.',
  formProNote: 'On Pro, Jev decides totals, formats, folders and what Copilot sees. It asks your leave first, and it never sees rows.',
  formFreeNote: 'On Free, rules make the links and starter measures, and your own assistant writes the words.',
  stepNames: ['Read the source', 'Rules and Jev decide', 'Your assistant writes', 'You approve', 'Build'],
};

// ---------------------------------------------------------------- the lines

/** A section with this many lines or fewer opens every table at once; a bigger one opens only the tables with a question in them. */
export const OPEN_ALL_UP_TO = 30;

/** The five sections, in the order a person reads them (StartSectionId.All). */
export const SECTION_ORDER = ['tables', 'links', 'measures', 'words', 'bestpractice'];

export interface Mark { key: 'jev' | 'rule' | 'assistant' | 'you' | 'default'; label: string }

/** Who decided a line. On Free the lines Jev would have decided say Default: Jev decided nothing there. */
export function lineMark(line: StartLine, tier: Tier): Mark {
  switch (line.decidedBy) {
    case 'person': return { key: 'you', label: 'You' };
    case 'assistant': return { key: 'assistant', label: 'Assistant' };
    case 'jev': return tier.pro ? { key: 'jev', label: 'Jev' } : { key: 'default', label: 'Default' };
    default: return !tier.pro && line.point ? { key: 'default', label: 'Default' } : { key: 'rule', label: 'Rule' };
  }
}

export const linesOf = (view: Pick<StartPlanView, 'sections'>): StartLine[] =>
  (view.sections ?? []).flatMap((s) => (s.groups ?? []).flatMap((g) => g.lines ?? []));

const lineId = (l: StartLine): string => l.item?.id ?? '';
const sectionOfLine = (view: Pick<StartPlanView, 'sections'>, line: StartLine): string | null =>
  (view.sections ?? []).find((s) => (s.groups ?? []).some((g) => (g.lines ?? []).includes(line)))?.id ?? null;

export interface LineState { text: string; tone: 'good' | 'quiet' | 'need' | 'warn' }

/** Where a line stands, in the engine's plain words. An unsure Jev line is left out until answered, never blocking. */
export function lineState(line: StartLine, view: Pick<StartPlanView, 'sections'> & { built?: boolean }, tier: Tier): LineState {
  // A held line is the person's or their assistant's: it stays on the plan and is shown, but the build skips it.
  if (line.held) return { text: COPY.stateHeld, tone: 'warn' };
  // Once built, a line Jev was unsure of is not waiting on anyone: the build left it out, and it says so.
  if (view.built && !line.kept && line.decidedBy === 'jev') return { text: COPY.stateNotBuilt, tone: 'quiet' };
  if (!line.kept) {
    if (line.decidedBy === 'jev') return { text: COPY.stateLeftOutNeed, tone: 'need' };
    if (line.decidedBy === 'person') return { text: COPY.stateLeftOutYou, tone: 'quiet' };
    if (line.item?.kind === 'create_relationship') return { text: COPY.stateOff, tone: 'quiet' };
    return sectionOfLine(view, line) === 'bestpractice' ? { text: COPY.stateOpen, tone: 'warn' } : { text: COPY.stateLeftOut, tone: 'quiet' };
  }
  if (line.decidedBy === 'assistant' && line.word) return { text: COPY.stateWritten, tone: 'good' };
  if (!tier.pro && lineMark(line, tier).key === 'default') return { text: COPY.stateDefault, tone: 'quiet' };
  return { text: COPY.stateDecided, tone: 'quiet' };
}

export const lineTitle = (line: StartLine): string => line.item?.title || line.item?.objectName || '';
const isMeasure = (line: StartLine) => line.item?.kind === 'create_measure';
/** The second line: a measure's formula when the engine sends one (a base measure carries none), else the reason. */
export const lineSub = (line: StartLine): string => (line.held ? line.held
  : isMeasure(line) && line.item?.after ? line.item.after : (line.why || line.item?.rationale || ''));

/** The source's own name for what a line is about, for the open row: the source table, and the column as the plan names it.
 *  Never the plan's internal ref (column:Sales/Qty, model:..., relationship:...): a person cannot read those. Null when the
 *  line is about the whole model or a link, which the source has no one name for. */
export function lineSource(line: StartLine, group?: Pick<StartGroup, 'table' | 'sourceName'> | null): string | null {
  const ref = line.item?.objectRef ?? '';
  const table = group?.sourceName || group?.table || null;
  if (!table) return null;
  if (ref.startsWith('column:')) {
    const name = (line.item?.objectName || ref.slice(ref.indexOf('/') + 1)).trim();
    return name && ref.includes('/') ? `${table}, column ${name}` : table;
  }
  return table;
}

/** The lines the build will make: kept, and not held. */
export const buildCountOf = (view: Pick<StartPlanView, 'sections'>): number => linesOf(view).filter((l) => l.kept && !l.held).length;
export const heldCountOf = (view: Pick<StartPlanView, 'sections'>): number => linesOf(view).filter((l) => !!l.held).length;
/** The short value the line settles on, beside its title (not for a measure: its formula is the second line). */
export function lineChoice(line: StartLine): string | null {
  const a = line.item?.after;
  if (!a || isMeasure(line) || a.length > 28 || lineTitle(line).includes(a)) return null;
  return a;
}
/** The number in the right-hand cell, when the line's value is one. Never invented. */
export function lineValue(line: StartLine): string | null {
  const a = line.item?.after;
  return a && !isMeasure(line) && /^[\d][\d,.$ ]*$/.test(a) ? a : null;
}
export const lineConfidence = (line: StartLine): number | null =>
  line.decidedBy === 'jev' && typeof line.confidence === 'number' && line.confidence > 0 ? Math.round(line.confidence) : null;

export const editLine = (line: StartLine, keep: boolean): StartEdit => ({ op: keep ? 'keep_line' : 'drop_line', target: lineId(line) });
export const pinLine = (line: StartLine): StartEdit => ({ op: 'pin', target: lineId(line), value: line.item?.after ?? '' });

// ---------------------------------------------------------------- what a person can do to one line
// ONLY WHAT THE ENGINE CAN ACT ON IS OFFERED (UAT-J7-S1-003, S2-005, S2-006). Each control below is one engine call the assistant's
// door has too (edit_start_plan, write_start_words), so a control the engine would refuse is never drawn.

export type WordKind = 'description' | 'display_name' | 'synonyms' | 'format';
const WORD_KIND: Record<string, WordKind> = { set_description: 'description', rename: 'display_name', set_synonyms: 'synonyms', set_measure_format: 'format' };

export interface LineControls {
  /** Drop (while kept) or Keep (while left out). False for the date table, which the plan chooses and has no switch for. */
  switchable: boolean;
  /** Keep this value: a value Jev or a rule chose, held as yours. Never on a line made by the plan, a time measure, or a line already yours. */
  pin: boolean;
  /** Rename a table or a base measure: the ref the engine's rename names, and the name it has now. */
  rename: { target: string; current: string } | null;
  /** Edit the words on a line your assistant or you wrote. */
  words: { ref: string; kind: WordKind; current: string } | null;
  /** Keep the name of this table or measure as it is, though the naming rules flag it: write_start_words kind keep_name, from the person's door. Only where the engine flagged a name. */
  keepName: { ref: string; why: string } | null;
}

export function lineControls(line: StartLine): LineControls {
  const kind = line.item?.kind ?? '';
  const made = kind === 'include_table' || kind === 'create_relationship' || kind === 'mark_date_table';
  const word = WORD_KIND[kind];
  const handMade = line.decidedBy === 'person' || line.decidedBy === 'assistant';
  return {
    switchable: !!line.item?.id && kind !== 'mark_date_table',
    pin: line.kept && !!line.item?.after && !line.derived && !made && !handMade,
    rename: kind === 'include_table' && line.item?.objectName ? { target: `table:${line.item.objectName}`, current: line.item.objectName }
      : kind === 'create_measure' && !line.derived && line.item?.objectRef ? { target: line.item.objectRef, current: line.item.objectName ?? '' } : null,
    words: word && line.word != null && handMade && line.item?.objectRef ? { ref: line.item.objectRef, kind: word, current: line.word ?? line.item.after ?? '' } : null,
    keepName: line.kept && line.nameFlag && line.item?.objectRef ? { ref: line.item.objectRef, why: line.nameFlag } : null,
  };
}

/** The model's file identity for a held draft: the plan's destination path, never the session id (a restart opens the same file under a new session). */
export const modelKeyOf = (view: StartPlanView): string => {
  const d = view.destination;
  return d?.modelPath || [d?.folder, d?.name].filter(Boolean).join('/') || view.id || '';
};
/** Every row a draft can be held on, named as the page shows it (a line's title; a column as "name in table"), by the id the draft is held under. */
export function rowNames(view: Pick<StartPlanView, 'sections'>): Map<string, string> {
  const names = new Map<string, string>();
  for (const l of linesOf(view)) if (l.item?.id) names.set(l.item.id, lineTitle(l));
  for (const s of view.sections ?? []) for (const g of s.groups ?? []) for (const c of g.columns ?? []) names.set(`col:${c.ref}`, `${c.name} in ${g.table ?? 'the table'}`);
  return names;
}
/** The rows a typed draft of these fields could go on now (join 11, W3): a line that can be renamed for a `rename` draft, a line whose words are of that kind for a words draft, a column for a rename. */
export function moveTargets(view: Pick<StartPlanView, 'sections'>, fields: string[]): Array<{ id: string; label: string }> {
  const out: Array<{ id: string; label: string }> = [];
  for (const s of view.sections ?? []) for (const g of s.groups ?? []) {
    for (const l of g.lines ?? []) {
      const id = l.item?.id, c = lineControls(l);
      if (id && fields.length > 0 && fields.every((f) => (f === 'rename' ? !!c.rename : c.words?.kind === f))) out.push({ id, label: lineTitle(l) });
    }
    if (fields.length > 0 && fields.every((f) => f === 'rename')) for (const c of g.columns ?? []) out.push({ id: `col:${c.ref}`, label: `${c.name} in ${g.table ?? 'the table'}` });
  }
  return out;
}
/** Where the first row that holds typed, unsaved words is in the page (view order, lines before columns), so the page can open it: its section, its group, and the line or the column. `held` is the draft store's held set. */
export function firstHeldRow(view: Pick<StartPlanView, 'sections'>, held: Iterable<string>): { id: string; name: string; section: string; table: string; column: boolean; count: number } | null {
  const ids = new Set([...held].map((h) => (JSON.parse(h) as [string, string])[0]));
  let first: { id: string; name: string; section: string; table: string; column: boolean } | null = null;
  const seen = new Set<string>();
  for (const pass of ['line', 'column'] as const) for (const s of view.sections ?? []) for (const g of s.groups ?? []) {
    if (pass === 'line') for (const l of g.lines ?? []) { const id = l.item?.id ?? ''; if (id && ids.has(id)) { seen.add(id); first ??= { id, name: lineTitle(l), section: s.id, table: g.table ?? '', column: false }; } }
    else for (const c of g.columns ?? []) { const id = `col:${c.ref}`; if (ids.has(id)) { seen.add(id); first ??= { id, name: `${c.name} in ${g.table ?? 'the table'}`, section: s.id, table: g.table ?? '', column: true }; } }
  }
  return first ? { ...first, count: seen.size } : null;
}
/** The rows that hold typed, unsaved words, named as the page shows them, never as a ref. `held` is the draft store's held set for the model.
 *  A draft whose row is not in the plan any more (the assistant renamed it) is still the person's words: it is listed too, by the name it was kept under (`labels`, the draft store's
 *  labels by line id), else by a plain line, so a build can never drop it without the person being asked (F8, Sol join 10). */
export function unsavedRowNames(view: Pick<StartPlanView, 'sections'>, held: Iterable<string>, labels?: ReadonlyMap<string, string>): string[] {
  const names = rowNames(view);
  const out: string[] = [];
  for (const h of held) {
    const [lineId] = JSON.parse(h) as [string, string];
    const name = names.get(lineId) || labels?.get(lineId) || COPY.orphanLine;
    if (name && !out.includes(name)) out.push(name);
  }
  return out;
}

/** A refusal from the engine names the thing by its ref ("column:dim_client/IND_CD: It contains formula text."). The page says it as the plan shows it. */
export function shownRefusal(text: string, view?: Pick<StartPlanView, 'sections'> | null): string {
  const m = /^([a-z]+):(.+?): (.*)$/s.exec(text);
  if (!m) return text;
  const ref = `${m[1]}:${m[2]}`;
  let shown: string | null = null;
  for (const s of view?.sections ?? []) for (const g of s.groups ?? []) for (const c of g.columns ?? []) if (c.ref === ref) shown = `${c.name} in ${g.table ?? 'the table'}`;
  if (!shown && view) { const l = linesOf(view).find((x) => x.item?.objectRef === ref); if (l?.item?.objectName) shown = l.item.objectName; }
  if (!shown) {
    const slash = m[2].indexOf('/');
    shown = slash > 0 ? `${m[2].slice(slash + 1)} in ${m[2].slice(0, slash)}` : m[2];
  }
  return `${shown}: ${m[3]}`;
}
export const renameEdit = (target: string, name: string): StartEdit => ({ op: 'rename', target, value: name.trim() });
/** The call for a person's edit of words. It carries the words the person started from (`expected`), so the engine refuses a save over words written since. */
export const wordsCall = (words: NonNullable<LineControls['words']>, text: string): [Array<{ ref: string; kind: WordKind; text: string; expected: string }>, string] =>
  [[{ ref: words.ref, kind: words.kind, text: text.trim(), expected: words.current }], 'human'];
export type Words = NonNullable<LineControls['words']>;
/** What a save of words came to. `moved` is the engine's refusal over newer words (the editor shows that itself, beside the draft); `said` is any other refusal. */
export interface WordsSaveResult { saved: boolean; moved: boolean; said: string | null }
/** The engine's sentence when the words a save names are no longer the words on the line (StartPlanWords.Apply). */
const WORDS_MOVED = /changed while you were editing them/;
export function wordsSaveResult(r: { written?: number; refused?: string[] } | null | undefined, view?: Pick<StartPlanView, 'sections'> | null): WordsSaveResult {
  if (r?.written) return { saved: true, moved: false, said: null };
  const refused = r?.refused ?? [];
  if (refused.some((x) => WORDS_MOVED.test(x))) return { saved: false, moved: true, said: null };
  return { saved: false, moved: false, said: refused.map((x) => shownRefusal(x, view)).join(' ') || 'The words were not saved.' };
}
export type WordsMove = { kind: 'none' } | { kind: 'same' } | { kind: 'follow'; text: string } | { kind: 'ask'; theirs: string };
/** What the line's words now mean for an open editor. `start` is what the person began from. Nothing typed: follow the new words. Typed: ask, never replace, never save over. */
export function wordsMove(start: Words, draft: string, live: Words | null | undefined): WordsMove {
  if (!live || live.current.trim() === start.current.trim()) return { kind: 'none' };
  if (live.current.trim() === draft.trim()) return { kind: 'same' };
  if (draft.trim() === start.current.trim()) return { kind: 'follow', text: live.current };
  return { kind: 'ask', theirs: live.current };
}
/** The call for a person's keep of a name the naming rules flag: the one write the assistant's door has (keep_name), from the person's own door, so the keep is theirs. */
export const keepNameCall = (ref: string): [Array<{ ref: string; kind: 'keep_name'; text: string }>, string] => [[{ ref, kind: 'keep_name', text: '' }], 'human'];
export const columnRenameEdit = (col: StartColumn, name: string): StartEdit => ({ op: 'rename', target: col.ref, value: name.trim() });
export const columnIncludeEdit = (col: StartColumn, include: boolean): StartEdit => ({ op: 'include_column', target: col.ref, value: include ? 'true' : 'false' });
/** The plain tags under a column's name: its type, then hidden, key, left out. */
export const columnTags = (c: StartColumn): string[] => [c.dataType ?? '', c.hidden ? COPY.columnHidden : '', c.key ? COPY.columnKey : '', c.included ? '' : COPY.columnLeftOut].filter(Boolean);

// ---------------------------------------------------------------- filters and what changed

export interface FilterTab { id: string; label: string; count: number }

export function filterTabs(view: StartPlanView, tier: Tier, changed: Set<string> = new Set()): FilterTab[] {
  const lines = linesOf(view);
  const n = (key: Mark['key']) => lines.filter((l) => lineMark(l, tier).key === key).length;
  return [
    { id: 'all', label: COPY.tabAll, count: lines.length },
    // Free shows a Default filter only when some line is on a plain default: the real Free plan has none.
    ...(tier.pro ? [{ id: 'jev', label: COPY.tabJev, count: n('jev') }] : n('default') > 0 ? [{ id: 'default', label: COPY.tabDefault, count: n('default') }] : []),
    { id: 'rule', label: COPY.tabRule, count: n('rule') },
    { id: 'assistant', label: COPY.tabAssistant, count: n('assistant') },
    { id: 'you', label: COPY.tabYou, count: n('you') },
    { id: 'changed', label: COPY.tabChanged, count: changed.size },
  ];
}

export function lineMatches(filter: string, line: StartLine, tier: Tier, changed: Set<string>): boolean {
  if (filter === 'all') return true;
  if (filter === 'changed') return changed.has(lineId(line));
  return lineMark(line, tier).key === filter;
}

const lineSig = (l: StartLine) => JSON.stringify([l.kept, l.item?.after ?? null, l.item?.title ?? null, l.decidedBy, l.word ?? null]);

/** The lines that differ between two reads of the plan: what an answer moved, and nothing else. The first read has
 *  nothing to compare, so nothing is "changed". */
export function changedLineIds(prev: StartPlanView | null | undefined, next: StartPlanView | null | undefined): Set<string> {
  const out = new Set<string>();
  if (!prev || !next || !prev.exists || !next.exists) return out;
  const before = new Map(linesOf(prev).map((l) => [lineId(l), lineSig(l)]));
  for (const l of linesOf(next)) { const id = lineId(l); if (id && before.get(id) !== lineSig(l)) out.add(id); }
  return out;
}

// ---------------------------------------------------------------- approval and the buttons

export interface Approval { state: 'none' | 'current' | 'stale'; text: string }

/** Where the person's approval stands against the plan on screen. `prev` is the read before this one: an engine that
 *  clears the approval when the plan moves gives no stale record, so a standing approval that has gone is told. */
export function approvalOf(view: StartPlanView, prev?: StartPlanView | null): Approval {
  const a = view.approval?.digest;
  if (a && view.digest && a === view.digest) return { state: 'current', text: COPY.approvalCurrent };
  if (a) return { state: 'stale', text: COPY.approvalStale };
  if (prev && prev.exists && prev.digest !== view.digest && approvalOf(prev).state === 'current') return { state: 'stale', text: COPY.approvalStale };
  return { state: 'none', text: COPY.approvalNone };
}

export interface Act { enabled: boolean; why: string | null }
export interface Actions { approve: Act; build: Act; approveAndBuild: Act }

const MOVING: StartDecideState[] = ['deciding', 'waiting_grant'];
/** Jev is still at work, or waiting for the person's leave. A person who said Not now is not waited on: the plan is theirs to approve. */
const isMoving = (view: StartPlanView): boolean => MOVING.includes(view.decide?.state ?? 'off') && !(view.jevDeclined && view.decide?.state === 'waiting_grant');

export function actionsOf(view: StartPlanView, _tier: Tier, prev?: StartPlanView | null): Actions {
  const no = (why: string): Actions => ({ approve: { enabled: false, why }, build: { enabled: false, why }, approveAndBuild: { enabled: false, why } });
  if (!view.exists || !view.digest) return no(COPY.noPlan);
  if (view.built) return no(view.blockedBecause || COPY.built);
  const state = view.decide?.state ?? 'off';
  if (isMoving(view)) return no(view.blockedBecause || (state === 'waiting_grant' ? COPY.waitingGrant : COPY.stillMoving));
  // WHAT CanBuild MEANS (StartPlanViewBuilder.Gate): true only when the approval matches the digest and nothing else blocks the
  // build, so an unapproved plan has CanBuild false and its BlockedBecause is "You have not approved this plan yet". It is NOT a
  // reason to refuse the person's Approve. Approve stays live whenever the plan is not moving; if the plan is parked the engine
  // refuses the approval with a sentence, which the page shows.
  if (approvalOf(view, prev).state === 'current') {
    const approve: Act = { enabled: false, why: COPY.alreadyApproved };
    const build: Act = view.canBuild === false ? { enabled: false, why: view.blockedBecause || COPY.noPlan } : { enabled: true, why: null };
    return { approve, build, approveAndBuild: approve };
  }
  const approve: Act = { enabled: true, why: null };
  return { approve, build: { enabled: false, why: COPY.approveFirst }, approveAndBuild: approve };
}

export type Primary = 'approve_and_build' | 'build';
export const primaryAction = (view: StartPlanView, tier: Tier, prev?: StartPlanView | null): Primary =>
  approvalOf(view, prev).state === 'current' ? 'build' : 'approve_and_build';

/** The digest the person looked at, and only that: never a re-read, never the engine's newer one. */
export const approveArgs = (view: StartPlanView): [string] => [view.digest ?? ''];
export const buildArgs = (view: StartPlanView): [string, string] => [view.digest ?? '', 'human'];

// ---------------------------------------------------------------- Needs you

// A sentence ends at . ? or ! that is followed by a space or the end: a dot inside a server name ends nothing.
const sentences = (text: string): [string, string] => {
  const m = /^[\s\S]*?[.?!](?=\s|$)/.exec(text);
  return m ? [m[0].trim(), text.slice(m[0].length).trim()] : [text.trim(), ''];
};
export const needTitle = (n: StartNeed): string => sentences(n.text)[0];
export const needBody = (n: StartNeed): string => sentences(n.text)[1];   // everything after the first sentence: the scope and the sample come in its second and third
export const needLean = (n: StartNeed): string | null => n.recommended || null;
/** What the recommended option is called: only a Jev question has Jev leaning to it; the grant and a rule's suggestion are just suggested. */
export const needLeanLabel = (n: StartNeed): string => (n.kind === 'jev' ? COPY.leans : COPY.suggested);

export type NeedAction =
  | { kind: 'grant'; args: [string, string] }
  | { kind: 'edit'; edit: StartEdit; args: [StartEdit, string] };

export const GRANT_ALLOW = 'Allow';

/** What pressing an option asks of the engine. Allow, on the grant or after the person said Not now, is the grant for the
 *  digest they saw. Not now is an answer to the grant (the plan goes on without Jev, and the band then offers Allow).
 *  Every other answer is one edit, made by the person. */
export function needAction(view: StartPlanView, need: StartNeed, option: string): NeedAction {
  const grantish = need.kind === 'grant' || need.kind === 'grant_declined';
  if (grantish && option === GRANT_ALLOW) return { kind: 'grant', args: [view.digest ?? '', 'human'] };
  const edit: StartEdit = { op: 'answer', target: grantish ? 'grant' : need.id, value: option };
  return { kind: 'edit', edit, args: [edit, 'human'] };
}

const JEV_NEEDS = new Set(['jev', 'grant', 'unsure_pick', 'unsure_line', 'oscillation']);
/** Who is asking: Jev for its own questions and its leave to look; Semanticus for everything else (a sample read, a composite
 *  key, the fiscal year, the person's own Not now). */
export const needMark = (n: Pick<StartNeed, 'kind' | 'options'>): Mark | null =>
  // A line with nothing to choose (a sample read, a composite key) asks nothing, so nobody is marked as asking.
  !(n.options ?? []).length ? null : JEV_NEEDS.has(n.kind) ? { key: 'jev', label: 'Jev asks' } : { key: 'rule', label: 'Semanticus asks' };

/** The words on an answer's button. The value sent to the engine is the option itself ("keep", "6/30", "calendar"). */
export function optionLabel(option: string): string {
  if (/^\d{1,2}\/\d{1,2}$/.test(option)) return `Year ends ${option}`;
  if (option === 'calendar') return 'Calendar year';
  return option ? option.charAt(0).toUpperCase() + option.slice(1) : option;
}

/** What the Changed filter holds after a read: every line that moved since the person's own last change. A change the
 *  person makes starts it again with what that change moved; a change from elsewhere (the assistant's next batch of words,
 *  Jev's round) adds to it, so 241 words in seven batches all show, not the last batch alone. */
export function mergeChanged(before: Set<string>, moved: Set<string>, mine: boolean): Set<string> {
  return mine ? new Set(moved) : new Set([...before, ...moved]);
}

/** One table in the permission card: whether it is in the plan, and whether its sample may go to Jev. */
export interface CardTable { name: string; sourceName?: string | null; included: boolean; sampleOff: boolean }

/** What the start plan's permission card shows: the source, the tables the plan brings in, and whether a sample is used. The card lists EVERY table with
 *  the person's two choices on it (leave this table out; read it without sending its sample), the same two the assistant has (edit_start_plan include_table
 *  and set_table_sample), so the choice is made where the question is asked. */
export function grantCardOf(view: StartPlanView): { source: string; tables: string[]; sample: boolean; sampleState: string; choices: CardTable[] } {
  const groups = (view.sections ?? []).find((s) => s.id === 'tables')?.groups.filter((g) => g.table) ?? [];
  const choices = groups.map((g) => ({
    name: g.table as string, sourceName: g.sourceName, sampleOff: !!g.sampleOff,
    included: (g.lines ?? []).some((l) => l.item?.kind === 'include_table' && l.kept),
  }));
  const src = view.source;
  return {
    source: [src?.database, src?.server].filter(Boolean).join(' on ') || 'this source',
    tables: choices.filter((c) => c.included).map((c) => c.name),
    sample: !!view.sampleFacts, sampleState: view.sample?.state ?? 'none', choices,
  };
}

export const tableIncludeEdit = (name: string, include: boolean): StartEdit => ({ op: 'include_table', target: name, value: include ? 'true' : 'false' });
export const tableSampleEdit = (name: string, allow: boolean): StartEdit => ({ op: 'set_table_sample', target: name, value: allow ? 'on' : 'off' });

/** The per-table sample choice on the plan, drawn only after the grant (before it the dialog has the same choice). It narrows the stored grant exactly as
 *  edit_start_plan set_table_sample does (join 8, K6): the one edit, from the person's door. */
export function sampleControlOf(view: StartPlanView, group: StartGroup, tier: Tier): { name: string; allowed: boolean } | null {
  if (!tier.pro || view.built || !group.table || !view.sampleFacts) return null;
  const state = view.decide?.state;
  if (view.jevDeclined || !state || state === 'off' || state === 'waiting_grant') return null;
  if (!(group.lines ?? []).some((l) => l.item?.kind === 'include_table' && l.kept)) return null;
  return { name: group.table, allowed: !group.sampleOff };
}

export interface ConsentLike { current?: boolean; appliesTo?: string | null }
export type GrantCard = ReturnType<typeof grantCardOf> & {
  grant: () => Promise<StartPlanView>;
  /** One of the two table choices. Answers with the card as the plan reads now, bound to the plan's new digest. */
  edit?: (e: StartEdit) => Promise<GrantCard>;
};
export interface AllowDeps {
  getConsent: () => Promise<ConsentLike | null>;
  grant: (digest: string) => Promise<StartPlanView>;
  /** The person's edit (the card's table choices): the plan after it. */
  edit?: (e: StartEdit) => Promise<StartPlanView>;
  openCard: (plan: GrantCard, scope: string) => void;
}

/** The permission card for a plan: its tables and their two choices, and a grant bound to THIS plan's digest. Each choice moves the digest, so the card is made again from the plan that comes back. */
export function grantCardFor(view: StartPlanView, deps: Pick<AllowDeps, 'grant' | 'edit'>): GrantCard {
  const digest = view.digest ?? '';
  return {
    ...grantCardOf(view), grant: () => deps.grant(digest),
    edit: deps.edit ? async (e) => grantCardFor(await deps.edit!(e), deps) : undefined,
  };
}

/** Allow, on the grant or after Not now. The engine refuses a grant until the MODEL's own permission is current
 *  (LocalEngine.GrantStartDisclosureAsync), so it is read first: current, and the grant goes at once with the digest the
 *  person looked at; not current, and the start plan's permission card opens, which gives the model's permission and THEN
 *  the grant on its OK. A model with nowhere to keep permission is sent the grant, and the engine says why it cannot. */
export async function allowJev(view: StartPlanView, need: StartNeed, deps: AllowDeps): Promise<StartPlanView | 'card'> {
  const a = needAction(view, need, GRANT_ALLOW);
  if (a.kind !== 'grant') throw new Error('Only the grant is allowed this way.');
  const digest = a.args[0];
  const consent = await deps.getConsent().catch(() => null);
  // A model with nowhere to keep permission is sent the grant, and the engine says why it cannot. Every other model gets the card, even when its own permission is
  // already current: the card is where the person sees which tables and which samples Jev would see, and says no to any of them (UAT-J7-S2-002, -003).
  if (!consent?.appliesTo) return deps.grant(digest);
  deps.openCard(grantCardFor(view, deps), consent.appliesTo);
  return 'card';
}
/** The sentence over the Needs you band: what each kind of question means for the build. A pick the rule made is built; a line Jev left out is not built until the answer. */
export function needsLeadFor(needs: Array<Pick<StartNeed, 'kind'>>): string | null {
  const jev = needs.filter(isJevQuestion);
  if (!jev.length) return null;
  const picks = jev.filter((n) => n.kind === 'unsure_pick').length, lines = jev.length - picks;
  return [lines > 0 ? COPY.needsLead(lines) : null, picks > 0 ? COPY.needsPicks(picks) : null].filter(Boolean).join(' ');
}
export const isJevQuestion = (n: Pick<StartNeed, 'kind'>): boolean => JEV_NEEDS.has(n.kind) && n.kind !== 'grant';
/** The lines in the band that wait on the person. A sample being read, or not read, is news, not a question. */
export const questionsOf = (view: Pick<StartPlanView, 'needsYou'>): StartNeed[] => (view.needsYou ?? []).filter((n) => !n.kind.startsWith('sample_'));

// ---------------------------------------------------------------- Jev's and the words' progress

export interface Note { text: string; tone: 'good' | 'busy' | 'warn' | 'need' }

/** Jev's progress, Pro only. Stable is said; Incomplete says it is not finished and why; Free says nothing. */
export function decideNote(view: StartPlanView, tier: Tier): Note | null {
  if (!tier.pro || !view.decide) return null;
  // The person said Not now: the Needs you line says so and offers Allow, so no Jev progress is told here.
  if (view.jevDeclined) return null;
  const d = view.decide;
  switch (d.state) {
    case 'waiting_grant': return { tone: 'need', text: 'Jev is waiting for you. Answer the first question in Needs you and it starts.' };
    case 'deciding': {
      const p = d.progress ?? [];
      const asked = p.reduce((a, f) => a + f.asked, 0), total = asked + p.reduce((a, f) => a + f.remaining, 0);
      return { tone: 'busy', text: `Jev is deciding. Round ${d.round ?? 1}${total ? `, ${asked} of ${total} looked at` : ''}.` };
    }
    case 'incomplete': return { tone: 'warn', text: `Jev is not finished. ${d.reason ? d.reason + ' ' : ''}What it did not reach becomes Plan rows after the build.` };
    case 'stable': return { tone: 'good', text: 'Jev has looked at everything. A second look changes nothing.' };
    default: return null;
  }
}

/** The plain line under a section's name. The engine's own Summary ("4 changes.") says less than the blurb, so it stands in only
 *  when it says more than a count. */
export function sectionBlurb(sec: StartSection): string {
  const s = (sec.summary ?? '').trim();
  return s && !/^(\d+ changes?|Nothing here yet)\.$/.test(s) ? s : COPY.sectionBlurbs[sec.id] ?? '';
}

export function wordsLine(view: StartPlanView): { done: number; total: number; open: number; text: string } {
  const total = view.words?.total ?? 0, open = Math.min(view.words?.open ?? 0, total);
  const done = total - open;
  return { done, total, open, text: total ? `${done} of ${total}` : 'None yet' };
}

export function totalsOf(view: StartPlanView, tier: Tier): Record<Mark['key'], number> & { all: number; waiting: number } {
  const t = { jev: 0, rule: 0, assistant: 0, you: 0, default: 0, all: 0, waiting: view.needsYou?.length ?? 0 };
  for (const l of linesOf(view)) { t[lineMark(l, tier).key]++; t.all++; }
  return t;
}

// ---------------------------------------------------------------- the foot and the grades

export interface Foot { lead: string; rest: string; state: 'built' | 'stale' | 'approved' | 'deciding' | 'waiting' | 'ready' }

/** What the foot of the page says: one bold lead and the next step, for the state the plan is in. */
export function footOf(view: StartPlanView, tier: Tier, prev?: StartPlanView | null): Foot {
  if (view.built) return { state: 'built', lead: COPY.footBuilt(view.destination?.name || 'the model'), rest: COPY.footBuiltRest };
  const approval = approvalOf(view, prev).state;
  if (approval === 'stale') return { state: 'stale', lead: COPY.footStale, rest: COPY.footStaleRest };
  if (approval === 'current') return { state: 'approved', lead: COPY.footApproved, rest: COPY.footApprovedRest };
  if (tier.pro && view.decide?.state === 'deciding') return { state: 'deciding', lead: COPY.footDeciding, rest: COPY.footDecidingRest };
  const waiting = questionsOf(view).length;
  if (waiting > 0) return { state: 'waiting', lead: COPY.footWaiting(waiting), rest: COPY.undoLine };
  return { state: 'ready', lead: COPY.footReady, rest: COPY.undoLine };
}

export interface Grade { now: string | null; after: string | null; afterNumber: number | null; bestPractice: string | null; bestPracticeOpen: number | null; provisional: boolean; note: string | null }

/** The AI readiness grade after the build, as the letter the engine gives (a number only when it sends no letter), and whether
 *  it can still move. A provisional grade is never shown as the answer: the note says so beside it. */
export function gradeOf(view: StartPlanView, tier: Tier): Grade {
  const g = view.grades ?? {};
  const after = g.afterBuildGrade || (g.afterBuild != null ? String(g.afterBuild) : null);
  const provisional = !view.built && !!g.afterBuildProvisional && after != null;
  const jevWorks = tier.pro && !view.jevDeclined && (view.decide?.state === 'deciding' || view.decide?.state === 'waiting_grant');
  return {
    now: g.nowGrade || (g.now != null ? String(g.now) : null), after, afterNumber: g.afterBuild ?? null,
    bestPractice: g.afterBuildBestPractice || null, bestPracticeOpen: typeof g.afterBuildBestPracticeOpen === 'number' ? g.afterBuildBestPracticeOpen : null, provisional,
    note: provisional ? (jevWorks ? COPY.gradeProvisionalJev : COPY.gradeProvisional) : null,
  };
}

/** What a start's answer means for the form: the plan, or a sentence that says what happened and what to do. */
export function startOutcome(v: StartPlanView | null | undefined): { view: StartPlanView | null; error: string | null } {
  return v?.exists ? { view: v, error: null } : { view: null, error: COPY.startNoPlan };
}

// ---------------------------------------------------------------- the link drawing (mockup B)

export interface GraphNode { name: string; sourceName: string | null; x: number; y: number; w: number; h: number }
export interface GraphEdge { from: string; to: string; active: boolean; id: string; note: string | null;
  /** A link that skips a column runs above or below the boxes in between, so it never passes behind a table it does not touch. */
  lane?: 'top' | 'bottom';
  /** How far this link is drawn from the centre line, in pixels: two links between one pair of tables (a fact with two dates)
   *  are drawn apart, never on top of each other. 0 for a link that is alone. */
  offset?: number }
export interface LinkGraph { nodes: GraphNode[]; edges: GraphEdge[]; width: number; height: number }

const ARROW = /^(.+?)\s*->\s*(.+?)(?:\s*\(.*\))?$/;
const TITLE_LINK = /^(.+?) to (.+?) on .+$/;
/** A link the plan guessed from the names is titled "Linked A[x] to B[y]: how its names matched, and what makes B[y] a key" (LinkGuesser.Words). */
const GUESSED_LINK = /^Linked (.+?)\[[^\]]*\] to (.+?)\[[^\]]*\]: /;

/** A link line the build does not write: "(not built: a table or column it needs is left out)" (LinkGroups). */
const NOT_BUILT = /\(not built\b[^)]*\)\s*$/;

/** The tables the links touch and the links between them: the structured list the engine sends (the links the build writes), else, for a plan
 *  that has none, the plan's own Links lines minus the ones the build does not write (a guess the person dropped, a link to a table left out).
 *  Null when the build writes no link: nothing is drawn that the build does not do. */
export function linkGraph(view: StartPlanView): LinkGraph | null {
  const edges: GraphEdge[] = [];
  if (Array.isArray(view.links)) view.links.forEach((l, i) => edges.push({ from: l.from, to: l.to, active: l.active, id: l.id ?? `l${i}`, note: l.note ?? null }));
  else {
    for (const sec of view.sections ?? []) {
      if (sec.id !== 'links') continue;
      for (const l of sec.groups.flatMap((g) => g.lines)) {
        if (!l.item?.objectRef?.startsWith('relationship:')) continue;
        // Listed for the person to see, not written by the build: a dropped link (status "rejected") and one whose table or column is left out.
        if (l.item.status === 'rejected' || NOT_BUILT.test(l.item.title ?? '')) continue;
        // The engine titles a declared link "From to To on Column" and a guessed one "Linked From[x] to To[y]: how, and key" (with " (off)" when it
        // is not live) and names it by identity, and its Kept flag is whether the link is on. "A -> B" in the name is read too, for a source that gives it.
        const t = GUESSED_LINK.exec(l.item.title ?? '') ?? TITLE_LINK.exec(l.item.title ?? ''), a = ARROW.exec(l.item.objectName ?? '');
        const ends = t ? [t[1], t[2]] : a ? [a[1], a[2]] : null;
        if (ends) edges.push({ from: ends[0].trim(), to: ends[1].trim(), active: l.kept && !/\(off\)\s*$/.test(l.item.title ?? ''), id: l.item.id || l.item.objectRef, note: l.why ?? null });
      }
    }
  }
  if (!edges.length) return null;
  const source = new Map<string, string>();
  for (const s of view.sections ?? []) for (const g of s.groups ?? []) if (g.table && g.sourceName && !source.has(g.table)) source.set(g.table, g.sourceName);
  const names = [...new Set(edges.flatMap((e) => [e.from, e.to]))];
  // Columns by depth from the tables nothing points at (the facts), so a snowflake reads left to right.
  const depth = new Map<string, number>(names.map((n) => [n, 0]));
  for (let pass = 0; pass < names.length; pass++) {
    let moved = false;
    for (const e of edges) { const d = (depth.get(e.from) ?? 0) + 1; if (d > (depth.get(e.to) ?? 0) && d <= names.length) { depth.set(e.to, d); moved = true; } }
    if (!moved) break;
  }
  const cols = Math.max(...depth.values()) + 1;
  const W = 132, H = 38, GX = 84, GY = 18, LANE = 16, PARALLEL = 10;
  const perCol: string[][] = Array.from({ length: cols }, () => []);
  for (const n of names) perCol[depth.get(n) ?? 0].push(n);
  // A link that skips a column leaves from the upper half of its column over the top, from the lower half under the bottom.
  for (const e of edges) {
    if (Math.abs((depth.get(e.to) ?? 0) - (depth.get(e.from) ?? 0)) <= 1) continue;
    const col = perCol[depth.get(e.from) ?? 0];
    e.lane = col.indexOf(e.from) * 2 < col.length - 1 || col.length === 1 ? 'top' : 'bottom';
  }
  // Links between one pair of tables, whichever way they point, are spread evenly about the centre of the boxes they join.
  const pairs = new Map<string, GraphEdge[]>();
  for (const e of edges) { const k = JSON.stringify([e.from, e.to].sort()); pairs.set(k, [...(pairs.get(k) ?? []), e]); }
  for (const group of pairs.values()) group.forEach((e, i) => { e.offset = group.length > 1 ? (i - (group.length - 1) / 2) * PARALLEL : 0; });
  const top = edges.some((e) => e.lane === 'top') ? LANE : 0, bottom = edges.some((e) => e.lane === 'bottom') ? LANE : 0;
  const rows = Math.max(...perCol.map((c) => c.length));
  const inner = rows * H + (rows - 1) * GY;
  const height = inner + 8 + top + bottom;
  const nodes: GraphNode[] = [];
  perCol.forEach((col, ci) => {
    const colH = col.length * H + (col.length - 1) * GY;
    col.forEach((name, ri) => nodes.push({ name, sourceName: source.get(name) ?? null, w: W, h: H, x: 4 + ci * (W + GX), y: 4 + top + (inner - colH) / 2 + ri * (H + GY) }));
  });
  return { nodes, edges, width: cols * W + (cols - 1) * GX + 8, height };
}

// ---------------------------------------------------------------- the start form

export interface SqlRecord { id?: string; name?: string; server?: string; database?: string; authMode?: string; tenantId?: string }
export interface StartRequest { server: string; database: string; authMode: string; tenantId?: string; storageMode: string; folder: string; name: string;
  /** The person's own say-so to replace an open model that has unsaved changes (StartModelRequest.DiscardUnsaved). Never sent by default. */
  discardUnsaved?: boolean }

export function startRequest(r: SqlRecord, name: string, folder: string, storageMode: string, discardUnsaved = false): StartRequest {
  const out: StartRequest = { server: (r.server ?? '').trim(), database: (r.database ?? '').trim(), authMode: (r.authMode ?? '').trim() || 'azcli', storageMode, folder: folder.trim(), name: name.trim() };
  const tenant = (r.tenantId ?? '').trim();
  if (tenant) out.tenantId = tenant;
  if (discardUnsaved) out.discardUnsaved = true;
  return out;
}

/** The record a TYPED endpoint starts with. It signs in the way the person already signs in, because a typed endpoint is the same kind of thing as a saved one
 *  (hero journey 5, 8 Oct: it asked the Azure command line, which a set-up account cannot use, and then opened a browser). In order: the endpoint the person picked in
 *  the list, the first saved endpoint, the way the open model was opened, then the browser sign-in the rest of the product starts on. */
export function typedEndpoint(typed: { server?: string; database?: string }, saved: SqlRecord[], picked: SqlRecord | undefined, openModel: { authMode?: string | null; tenantId?: string | null } | null | undefined): SqlRecord {
  const known = [picked, ...saved, openModel].find((r) => !!r && !!(r.authMode ?? '').trim());
  return { server: typed.server, database: typed.database, authMode: defaultSignInMode(known), tenantId: (known?.tenantId ?? '').trim() || undefined };
}

/** What is missing before Start can be pressed, in words; null when nothing is. */
export function startProblem(r: Pick<SqlRecord, 'server' | 'database'>, name: string, folder: string): string | null {
  if (!(r.server ?? '').trim() || !(r.database ?? '').trim()) return 'Choose a SQL endpoint first.';
  if (!name.trim()) return 'Give the model a name.';
  if (/[\\/]/.test(name)) return 'A model name cannot hold a slash.';
  if (!folder.trim()) return 'Choose an empty folder to keep it in.';
  return null;
}

/** What the Plan page draws for a start plan (K25, the live demo, 9 Oct). While the plan is being decided it holds the page. Once it is built it is a
 *  short band over the ordinary Plan, so the model's own rows, its grades and Run the plan are on the page the moment the build ends. The person
 *  opens the whole built plan from the band, and it holds the page again. */
export type StartPageMode = 'none' | 'plan' | 'band';
export function startPageMode(view: Pick<StartPlanView, 'built'> | null | undefined, showBuilt: boolean): StartPageMode {
  if (!view) return 'none';
  return view.built && !showBuilt ? 'band' : 'plan';
}

/** The band's one line: what was built, how many lines went in, how to take it back. */
export function builtBandWords(view: Pick<StartPlanView, 'destination' | 'sections'>): string {
  return `${view.destination?.name || 'The model'} was built from this plan. ${buildCountOf(view)} lines built. One undo takes it back.`;
}

/** The start plan belongs only to the model it was made for. A model opened live is not the model this plan builds, so an
 *  older plan from a SQL start stays out of its Plan. A built plan, and a session with no file of its own, are left as they are. */
export function startPlanBelongs(view: Pick<StartPlanView, 'destination' | 'built'> | null | undefined, session: { liveBound?: boolean; source?: string } | null | undefined): boolean {
  if (!view) return false;
  if (!session || view.built) return true;
  if (session.liveBound) return false;
  const norm = (p?: string) => (p ?? '').replace(/\\/g, '/').replace(/\/+$/, '').toLowerCase();
  const dest = norm(view.destination?.modelPath), src = norm(session.source);
  if (!dest || !src) return true;
  return src === dest || src.startsWith(dest + '/') || dest.startsWith(src + '/');
}
