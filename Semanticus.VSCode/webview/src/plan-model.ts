// Every sentence, count and grouping rule the Plan page says out loud, as pure functions.
//
// They live apart from the components for the same reason lineage-model.ts does: the page is a view of
// one engine answer, and the wording that carries a number has to be executable rather than eyeballed in
// a render. Nothing here touches React, the bridge or the clock.
//
// Honesty rules these functions enforce:
//   - a count is a count of what the engine sent, never a total nobody measured;
//   - an item below the confidence bar shows NO number, because a score the plan would not act on is not
//     a score a person should read as one;
//   - a skipped item keeps its reason and keeps its place, so skipping is not deleting;
//   - the four status facts (unsaved changes, unread report, failed check, where it publishes) are always
//     produced, whatever the leading fact says, so advice can never select them away;
//   - nothing here decides that an item ran. The engine owns every outcome; these functions only read it;
//   - free is the whole plan. Every item, every reason, every exact change and the jump to the page where
//     a person does it by hand. What Pro adds is the product deciding and doing.

import { editWords } from './copy';

// ---- wire shapes, as the engine sends them --------------------------------------------------------

/** Who does the work. `auto` runs on its own, `you` needs the person, `asst` needs their assistant, and
 *  `jev` means Jev worked the changes out (Pro only: a free session never has a Jev item). */
export type PlanOwner = 'auto' | 'you' | 'asst' | 'jev';
export type PlanStatus = 'ready' | 'proposed' | 'done' | 'skipped' | 'running' | 'waiting' | 'target';
export type PlanSectionName = 'Structure' | 'Meaning' | 'Numbers' | 'Trust' | 'Ship';

/**
 * How far along the propose, review, approve road an item is.
 *
 * IT IS NOT `status`, and the difference is the engine's, not this page's
 * (m3-item-shape.json, `_stateVsStatus`): `status` says where the WORK stands and is recomputed from the
 * model on every call; `state` says how far the item has travelled. The one that catches people out is
 * the last pair:
 *
 *   applied   the change set landed. The kept lines were written, in one transaction, with one undo entry.
 *   done      the gap is GONE, worked out again from the model and never stamped by whatever did the
 *             writing.
 *
 * `applied` always comes first, and a set can apply in full while the gap survives it. The item then
 * stays on the plan in state `applied`, which is the honest answer and not a bug to paper over.
 */
export type PlanState = 'target' | 'gap' | 'proposed' | 'applied' | 'done' | 'skipped';

/** Where one line of a change set came from. `rule` is a readiness or best-practice rule, `decision` is
 *  the plan's own reasoning, `assistant` is the person's Claude, `proven` is work that was checked. */
export type PlanChangeOrigin = 'rule' | 'decision' | 'assistant' | 'proven';

/** How far a change set itself has got. Only `proposed` is waiting on anybody. */
export type PlanChangeState = 'proposed' | 'applied' | 'rolledBack' | 'refused' | 'cancelled';

export interface PlanChangeProvenance {
  kind: PlanChangeOrigin;
  point?: string | null;
  rule?: string | null;
  confidence?: number | null;
  /** The confidence as the one word a person reads: sure, likely or unsure. Sent on a Jev line only, and
   *  worked out by the engine, never here, so the word and the number cannot disagree. */
  confidenceWord?: JevConfidenceWord | null;
}

export type JevConfidenceWord = 'sure' | 'likely' | 'unsure';

/** The engine's own check on a line, done before the line was offered. `exists` says there IS a check
 *  for this kind of change; `text` says what it proved. A choice the check refused never arrives as a line. */
export interface PlanChangeCheck { exists: boolean; text?: string | null }

/** One line of a change set: one edit, with its before and after, and whether it is kept. */
export interface PlanChangeLine {
  id: string;
  /**
   * The id the engine decides this line BY, and it is NOT `id`.
   *
   * `id` is built from the rule and the thing ("VIS-FK/column:Sales/CustomerKey") and is the line's
   * name. `planItemId` is the short handle the change store keeps ("ci-0001"), and it is the one
   * `setPlanItem` takes. Both are strings, so sending the wrong one does not fail: it decides nothing,
   * or it decides some other line. `lineRef` is the one place that picks.
   */
  planItemId?: string;
  /** The engine's own one-line rendering, signed the way a diff line is. */
  line: string;
  op: string;
  target: string;
  property?: string | null;
  before?: string | null;
  after?: string | null;
  /** Kept is written on approve; dropped is not. A line is kept or it is dropped, never neither. */
  kept: boolean;
  /** True while the line is kept ONLY because Semanticus suggested it (the cleanup row's pre-selection) and nobody has ticked,
   *  unticked or edited it since. Run the plan never applies such a line; Approve on the row, the person's own act, does. */
  suggested?: boolean;
  provenance?: PlanChangeProvenance;
  /** Why this change, as one sentence the ENGINE built from what it measured and what was chosen. Jev
   *  picks from a fixed list and writes no text, so this is never Jev's words. Jev lines only. */
  reason?: string | null;
  check?: PlanChangeCheck | null;
  /** True on a Jev format line the engine could NOT prove fits (THE FORMAT FILL RULE). It is a suggestion the person checks and
   *  keeps: the page prints `note` as words on the row and draws no check mark and no confidence word for it. */
  suggestion?: boolean;
  /** The plain sentence a suggestion carries. Null on every other line. */
  note?: string | null;
}

/** A line the engine left OUT, with its reason. It is not a choice anybody has: it is an explanation. */
export interface PlanRefusedLine { target: string; reason: string }

/**
 * The set of exact edits hanging off one plan item.
 *
 * `digest` is computed by the engine over the lines and the two versions (Jev/ChangeContract.cs,
 * ChangeSet.ComputeDigest) and the page NEVER computes one: it carries the digest it was given straight
 * back on approve, which is what makes approve mean "approve exactly what I was shown".
 */
export interface PlanChangeSet {
  id: string;
  digest: string;
  state: PlanChangeState;
  revision?: number;
  sourceVersion?: string;
  /**
   * TWO STALE FLAGS, and the difference is which question they answer
   * (m3-item-shape.json, `_changeSetFields`).
   *
   *   staleAtProposal  STORED. The versions were already not current at the moment the set was worked
   *                    out. Never recomputed.
   *   stale            COMPUTED on every read. The set cannot be applied AS IT STANDS RIGHT NOW, which
   *                    is the ordinary way a set goes bad: it was fine when it was proposed and then
   *                    somebody edited the model. Always true when staleAtProposal is.
   *
   * The page reads EITHER, because a set the engine will refuse must not offer Approve whichever flag
   * says so. Reading only the stored one left Approve on screen after every ordinary edit.
   */
  staleAtProposal?: boolean;
  stale?: boolean;
  staleReason?: string;
  /** The model these changes were worked out against, as the engine's durable identity. It is hashed
   *  into the digest with the session and the proposal, so a digest from one model cannot approve work
   *  on an identical copy of it. Read only: the page never computes it and never sends it. */
  modelIdentity?: string;
  proposedAt?: string;
  appliedAt?: string | null;
  /**
   * The PREVIOUS run for this item, beside the current proposal and never folded into it.
   *
   * An item id names a FAMILY, so work keeps arriving under it: four descriptions approved today and a
   * fifth added tomorrow means a set in state `proposed` with these two set. They are a RECEIPT. Never
   * render the fifth line as applied because these are non-null (m3-item-shape.json,
   * `_changeSetFields`, Astra finding 10).
   */
  lastAppliedAt?: string | null;
  lastAppliedCount?: number;
  keptCount?: number;
  /** The engine's own sentence about the set. Printed as it came. */
  sentence?: string;
  /** An undo marker ('undo:<revision>') once the set is applied, empty before. */
  checkpoint?: string;
  lines: PlanChangeLine[];
  refused?: PlanRefusedLine[];
}

/**
 * One line of what this model should become.
 *
 * TWO SOURCES, and the difference is the whole point of drawing them together. `rules` is the engine
 * reading its own rules back as one plain aim per section, which is the target nobody wrote down and the
 * plan has always been measured against. `you` is a person typing one, which is the first time the plan
 * has been told what it is FOR by the only one who knows.
 */
export interface PlanTarget {
  id: string;
  text: string;
  section?: PlanSectionName;
  source: 'rules' | 'you';
  /** When a person added theirs. Null on a rule aim, which was never added by anybody. */
  when?: string | null;
  /** When a person marked it reached. Null while it is still open. */
  doneUtc?: string | null;
  /** The plan item this target became, when it became one. Null on a rule aim. */
  itemId?: string | null;
}

/** `find` is set on the collisions row only: the clashing names, for the assistant (ModelPlanJump.Find). No page opens with them. */
export interface PlanJump { tool: string; label: string; find?: string[] | null }
/** One way to start a model, offered by an empty model's Plan (ModelPlanStart). */
export interface PlanStart { id: string; label: string; line: string; jump: PlanJump; op?: string }

export interface PlanItem {
  /** A STRING built from what the item is about, never from where it sits in the list: "safefix/Visibility",
   *  "check/measure:Sales/Margin %". It is stable across refreshes, which is how a skip sticks to the thing
   *  it was about. It is a wire id and it is never printed: the little number in the row's left column is
   *  the item's POSITION in the plan as sent (see `positions`). */
  id: string;
  section: PlanSectionName;
  title: string;
  sub: string;
  /** What finishing it is worth, in the person's words. Empty when the engine did not price it. */
  value?: string;
  who: PlanOwner;
  status: PlanStatus;
  /** True for a row the plan can end without (the cleanup, always): the engine sends
   *  it after every other open row and no row waits for it. Its sub line says "Optional" in words. */
  optional?: boolean;
  /** How far along the propose, review, approve road this item is. Optional because an engine older than
   *  the Fix milestone sends none, and `itemState` reads that case honestly rather than guessing. */
  state?: PlanState;
  /** The exact edits waiting on this item, or null when there are none. Only an item in state `proposed`
   *  or `applied` carries one. */
  changeSet?: PlanChangeSet | null;
  /** 0 to 1. Below the bar the page prints nothing at all. */
  confidence: number;
  why: string[];
  /** The exact change, one line each, prefixed + (added), - (removed) or ~ (changed). An ARRAY on the
   *  wire (ModelPlanItem.Diff is string[]); a read only item sends an empty one and the page says so. */
  diff?: string[];
  touches?: string;
  /** The tables this item touches, as the ENGINE names them (ModelPlanItem.Tables), empty when it
   *  touches none. `touches` is a sentence for a person to read; this is the machine-readable half, and
   *  it is what the Map groups by. Optional only because a plan sent by an engine older than this field
   *  carries no array, and that case falls back to reading the prose (see `mapGroups`).
   *
   *  TWO SHAPES ARE LEGAL and the difference is a real difference, not a convenience. A plain STRING is
   *  a table name and nothing more, which is all the engine sent before 21 September. An OBJECT also
   *  carries how much of this item lands on that table, so the map can say "Remove 31 unused things
   *  here" rather than repeating one global title on nine cards. `itemTables` reads both, and reads a
   *  string as a name with NO count, because a zero we invented would be a measurement nobody took. */
  tables?: (string | PlanItemTable)[];
  jump?: PlanJump;
  skipReason?: string;
  /** When a person set it aside, as the engine's timestamp. Present only on a skipped item. */
  skippedUtc?: string;

  // ---- the Plan at the heart (contract 1.2, frozen in docs/redesign/contracts/plan-item.json) -----------
  // Optional here only because an engine older than slice 7 sends none of them; the page reads them first and
  // falls back to nothing, never to a guess. `who`, `status` and `state` above are deprecated on the wire.
  source?: PlanSource;
  sourcePage?: string | null;
  doer?: PlanDoer;
  stateWord?: PlanStateWord;
  /** The assistant's own tool calls are touching this row now: draw it On it, in the assistant's colour. */
  assistantAtWork?: boolean;
  stateNote?: string | null;
  doneWhen?: PlanDoneWhen;
  doneText?: string | null;
  objects?: string[];
  reads?: string[];
  writes?: string[];
  footprintKnown?: boolean;
  waitsFor?: PlanWaitRef[];
  /** Only on a short read of the plan (the agent door): how many rows it waits for in all, when waitsFor lists the first few. */
  waitsForCount?: number | null;
  held?: boolean;
  order?: number | null;
  note?: string | null;
  /** The assistant's own note on the row, kept beside the person's. */
  assistantNote?: string | null;
  reply?: string | null;
  /** The person's whole text, when it was longer than the title (the title is its start). */
  fullText?: string | null;
  claim?: PlanClaim | null;
  originLink?: PlanOriginLink | null;
  /** The ledger entries filed under this row at write time (contract 1.6): what it changed, opened in History. */
  events?: string[];
  /** Its number in what the assistant reads (contract 6.1), from the engine; null when it is not offered. */
  offerNumber?: number | null;
  /** Where the row's findings came from (round 1): any readiness rule makes it 'ai', a best-practice rule 'quality', a row
   *  the person asked for 'aims'. Absent on an older engine; goalOf then reads the old rule. */
  goal?: 'ai' | 'quality' | 'aims';
  /** Every filter the row shows under, its goal first: a row that fixes a rule both AI readiness and Model quality raise is under both.
   *  Absent on an older engine; goalsOf then gives the one goal. */
  goals?: ('ai' | 'quality' | 'aims')[];
  /** The rule ids this row carries, so the Readiness page can say "On the plan" instead of offering a second copy. */
  rules?: string[];
}

/** Where a row came from (contract 1.2). A source, never a doer: Jev is here and nowhere else. */
export type PlanSource = 'found' | 'jev' | 'words' | 'you' | 'workflow' | 'repeat';
/** Who does it next. */
export type PlanDoer = 'semanticus' | 'you' | 'assistant';
/** The one state word, computed by the engine (contract 1.4). The page never derives it. */
export type PlanStateWord = 'done' | 'set_aside' | 'checking' | 'on_it' | 'paused' | 'waiting' | 'needs_you' | 'ready';
export type PlanDoneWhen = 'model_check' | 'has_words' | 'assistant_says' | 'you_mark';
export interface PlanWaitRef { id: string; title: string; doer?: PlanDoer | null }
export interface PlanClaim { claimedBy: string; since?: string | null }
export interface PlanOriginLink { kind: 'finding' | 'page' | 'run' | 'draft' | 'words'; ref?: string | null }

/** One track of the strip, counted by the engine (contract 9). */
export interface PlanStripTrack { doer: PlanDoer; done: number; onIt: number; ready: number; needsYou: number; waiting: number }
export interface PlanStrip {
  gradeNow?: string | null;
  gradeAfter?: string | null;
  /** The Best practice letter from the engine's own BPA scan, read-only beside AI readiness. */
  bestPractice?: string | null;
  bestPracticeWhy?: string | null;
  /** The same scan's 0 to 100 score: the number the Review page shows. Null when the scan could not run. */
  bestPracticeScore?: number | null;
  /** Plan done: done tasks (counting the ones History holds) of every task counted. Both come from the engine. */
  tasksDone?: number;
  tasksAll?: number;
  tracks: PlanStripTrack[];
  latest?: { entryId: string; when: string; by: string; sentence: string } | null;
  updated?: { when: string; by: string } | null;
}

/**
 * One table an item touches, in that table's own words.
 *
 * `count` is how many things this item changes IN THIS TABLE, `what` is the engine's plain phrase for
 * them ("24 columns, 7 measures"), and `diff` is the item's exact change filtered to this table. All
 * three are what make a card say something about the table a person is looking at.
 */
export interface PlanItemTable { name: string; count: number; what: string; diff?: string[] }

/**
 * One category of finding sitting on a table, from the readiness scan the plan already runs, so it
 * costs nothing extra to show.
 *
 * The wire is TWO FIELDS and no words: the engine sends the readiness category and a count, exactly as
 * the findings page names them, and the page does the wording. A category this page has no words for
 * still draws, from the category's own name, so one added later is readable rather than invisible.
 */
export interface PlanTableFinding { category: string; count: number }

export interface PlanWorth {
  from?: string;
  to?: string;
  /** The readiness score now, 0 to 100, from the same scan as `from`. Absent exactly when `from` is. */
  fromScore?: number | null;
  /** The score the safe fixes reach, from the same rehearsal as `to`. Null while `pending` and whenever
   *  `to` is null: the Overview draws its gap bar only from a number the engine worked out. */
  toScore?: number | null;
  lines: string[];
  /** The engine saying the grade does not move. "B to B" read as a typo on the card, so a grade that
   *  stays says so in words. It is NOT the same as having no `to`: nobody worked that one out, and this
   *  one was worked out and came back level. */
  unchanged?: boolean;
  /** True while the engine is still working out `to`. The grade after the safe fixes is rehearsed, which
   *  costs a second readiness scan, so the first plan for a model comes back without it rather than making
   *  a person wait. Pending is NOT unknown: `lines` carries the engine's own line saying it is still being
   *  worked out, and the page asks again a few times. The tile never shows a spinner for it. */
  pending?: boolean;
  /** The engine's one line about where the model is heading: the grade now, the grade the safe fixes
   *  reach, and, when a person has typed targets, that they come after. "Stays B, then A" is the shape.
   *  It is the ENGINE's sentence and the page prints it as it came; `targetHeadline` falls back to the
   *  from-and-to reading when an engine sends none. */
  targetLine?: string;
}
export interface PlanNext { itemId: string; line: string }

/**
 * What happened, and these seven are the ENGINE's seven, not a set this page invented (the wire
 * contract published on 21 September, from Plan/PlanMemory.cs):
 *
 *   drawn      the first composition for a model
 *   redrawn    every later one where the item set moved
 *   done       a start that changed something
 *   started    a start that ran and changed nothing
 *   skipped    a person set an item aside
 *   unskipped  and put it back
 *   refused    a start the engine declined
 *
 * The Fix milestone added three more, and they are the review road written down:
 *
 *   proposed   a set of exact changes was worked out for an item
 *   approved   the kept lines were written, as one step
 *   cancelled  the set was dropped without writing anything
 */
export type PlanHistoryKind = 'drawn' | 'redrawn' | 'done' | 'started' | 'skipped' | 'unskipped' | 'refused'
  | 'proposed' | 'approved' | 'cancelled' | 'given';
/** Who did it. Human origin is `you`, agent origin is `assistant`, a step of the page's own run is
 *  `plan`, and a draw or a redraw is the `engine` composing for itself. */
export type PlanHistoryActor = 'you' | 'assistant' | 'plan' | 'engine';

/**
 * One line of the plan's story.
 *
 * TWO WIRES ARE LEGAL here. The old one carried `when` and `text` and nothing else, so twenty rows said
 * "1 item done, 1 new" with no names in them. The new one names the kind, the actor, the item and the
 * revision, and carries the detail a person opens an entry for. `text` did not go away: the engine
 * still sends it, always, and always the same string as `sentence`, so a reader that knows nothing
 * else still works. An old row read back from a file still draws (see `historyEntry`); it just cannot
 * be filtered by anything it never said.
 *
 * ORDER ON THE WIRE IS OLDEST FIRST, up to 200 entries. The page shows newest first, which is a
 * presentation of a chronological list and not an opinion about it: see `historyEntries`.
 */
export interface PlanHistoryRow {
  when: string;
  /** Always present on the new wire and identical to `sentence`; the ONLY field an old row has. */
  text?: string;
  kind?: PlanHistoryKind;
  actor?: PlanHistoryActor;
  itemId?: string;
  title?: string;
  /** The model revision AFTER it. 0 or absent means nothing was written, and prints as nothing. */
  revision?: number;
  sentence?: string;
  /** ONE field carrying three different things, by kind, because that is how the engine sends it:
   *  the applied change lines on a `done` or a `started`, the person's own reason on a `skipped`, and
   *  the refusal sentence on a `refused`. `historyEntry` splits them, so nothing downstream has to. */
  detail?: string[];
  /** A draw or a redraw, BY NAME and never by count. `completed` is the part of `gone` the plan
   *  recorded as finished, so completed is a SUBSET of gone and the page must not add the two. */
  appeared?: string[];
  gone?: string[];
  completed?: string[];
}

/**
 * A table on the map.
 *
 * Every field after `kind` is optional because every one of them is a measurement, and a measurement
 * nobody took must not print as a zero. `sizeBytes` is the clearest case: null is "no size scan has
 * run", and the facts line stays silent about it rather than saying 0 MB.
 */
export interface PlanTable {
  name: string;
  /** "fact", "dimension", "date table", "measure table", "bridge", "field parameter". A measure table
   *  used to come through as a plain "table", which told a reader nothing about what it is for. */
  kind: string;
  columns?: number;
  measures?: number;
  relationships?: number;
  sizeBytes?: number | null;
  findings?: PlanTableFinding[];
}

export interface Plan {
  modelName?: string;
  refreshedAt?: string;
  refreshedWhy?: string;
  worth?: PlanWorth;
  next?: PlanNext;
  /** What this model should become: the rules' own aims, plus what a person typed. Absent on an engine
   *  older than the Fix milestone, and an absent target is drawn as no header rather than as an empty one. */
  targets?: PlanTarget[];
  items: PlanItem[];
  history: PlanHistoryRow[];
  tables: PlanTable[];
  /** How the order was arrived at, in the engine's own plain words (ModelPlanView.OrderedBy). Present on
   *  every plan, because "in rule order" and "in value order" are different promises and a person is owed
   *  which one they got. On a free session the engine says so here and sends every confidence as zero. */
  orderedBy?: string;
  /** Where billing is (ModelPlanView.BillingUrl). Present only when the order line says the Pro plan is not active; the page shows a Billing link beside it. */
  billingUrl?: string;
  /** True only when a value ranking was actually applied (ModelPlanView.Ranked). False is the ordinary
   *  case and is not a failure: the plan is complete either way, same items, same reasons, same diffs. */
  ranked?: boolean;
  /** True while Jev is still looking at this model revision in the background (ModelPlanView.JevPending).
   *  The plan is complete without it and never waits for it; the page shows one quiet line and re-reads
   *  when plan/didChange says the run landed. Never true on a free session. */
  jevPending?: boolean;
  /** The engine's honesty line for anything it could not work out (a scan that needs a live connection, a
   *  report reading nobody has taken). Absent when there is nothing to say. */
  note?: string;
  /** The strip, built by the engine so both doors read one answer (contract 9). */
  strip?: PlanStrip | null;
  /** The ways to start, sent only while the model has no tables (ModelPlanView.Starts). */
  starts?: PlanStart[];
  /** The fence Run the plan quotes back (contract 7.2): the Ready Semanticus set as the page read it. */
  readyDigest?: string | null;
  /** The switch "Tasks for your assistant". */
  assistantQueue?: 'on' | 'paused';
  /** What the assistant reads on its next call, from the engine (contract 6.1); null when it would read nothing. */
  assistantSees?: string | null;
  /** For each assistant row, what the assistant reads to finish it without asking. The page never shows it. */
  assistantBriefs?: Record<string, string> | null;
  revision?: number;
  /** The person's saved repeats and the stock ones offered (lane RP, contract 1.7). */
  repeats?: import('./plan-repeat-model').PlanRepeat[];
  repeatStarters?: import('./plan-repeat-model').PlanRepeatStarter[];
  /** The Pro line on free when repeats are saved; shown beside the facts line. */
  repeatsNote?: string | null;
}

export const SECTIONS: { name: PlanSectionName; why: string }[] = [
  { name: 'Structure', why: 'is the model built right' },
  { name: 'Meaning', why: 'does it make sense to people and to AI' },
  { name: 'Numbers', why: 'are the measures right and tidy' },
  { name: 'Trust', why: 'can the numbers be checked' },
  { name: 'Ship', why: 'can it go to the reports' },
];

// ---- when something happened -------------------------------------------------------------------------

/**
 * The engine sends real timestamps (ModelPlanView.RefreshedAt, ModelPlanHistoryEntry.When are
 * DateTimeOffset), so they arrive as ISO strings. Printing one raw put
 * "2026-09-21T10:42:17.4210000+00:00" in a column sized for "10:42".
 *
 * `now` is passed in rather than read, because nothing in this file may touch the clock: a function that
 * reads the wall clock for itself cannot be tested and cannot be replayed, so the page reads it once per
 * render and hands it down. An unparseable value comes back untouched, so an engine that one day sends a
 * word instead prints the word rather than the browser's "Invalid Date".
 */
export function whenLabel(value: string | undefined | null, now: number): string {
  if (!value) return '';
  const at = Date.parse(value);
  if (!Number.isFinite(at)) return String(value);
  const seconds = Math.round((now - at) / 1000);
  if (seconds < 0) return timeOfDay(at);
  if (seconds < 60) return 'just now';
  const minutes = Math.round(seconds / 60);
  if (minutes < 60) return `${minutes} min ago`;
  const hours = Math.round(minutes / 60);
  if (hours < 24) return `${hours} hour${hours === 1 ? '' : 's'} ago`;
  const days = Math.round(hours / 24);
  if (days < 7) return `${days} day${days === 1 ? '' : 's'} ago`;
  return new Date(at).toISOString().slice(0, 10);
}

const MONTHS = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];
function timeOfDay(at: number): string {
  const d = new Date(at);
  return `${d.getHours()}:${String(d.getMinutes()).padStart(2, '0')}`;
}

// ---- words -----------------------------------------------------------------------------------------

/**
 * The state each item is in, in the person's words.
 *
 * `gap` has NO word of its own and that is deliberate: a gap is the ordinary state of a plan item, and
 * the question a person is actually asking about one is who does it. So a gap row shows the owner chip
 * it has always shown, and only the four states that are really news get a chip (see `rowChips`).
 */
export const STATE_LABEL: Record<PlanState, string> = {
  target: 'target',
  gap: '',
  proposed: 'changes proposed',
  applied: 'changes applied',
  done: 'done',
  skipped: 'skipped',
};

/**
 * The dot a state word carries, by owner and by state. Instrument (look-and-feel direction B, picked by Kane
 * on 23 September 2026) has no coloured pills: an owner or a state is a WORD in the muted colour, and only
 * the two things that wait on the person reading get a small amber dot beside the word: an item that needs
 * you, and a Jev line that arrived dropped because Jev was unsure. It lives here, beside the words, because
 * the Plan page and the Overview both draw them and an owner marked one way on one surface and another way
 * on the next is two different facts to a reader.
 */
export type PillDot = 'none' | 'warn' | 'bad' | 'doer-engine' | 'doer-you' | 'doer-agent';
// Colour level B (Kane, 24 September 2026): the owner word now carries its DOER mark, the hue of whoever does
// the step (Semanticus, You, Your assistant), and state words keep the status colours. Jev keeps none.
export const PILL_TONE: Record<string, PillDot> = {
  target: 'none', applied: 'none', auto: 'doer-engine', you: 'doer-you', asst: 'doer-agent', jev: 'none',
  // The three confidence words on a Jev line. Unsure carries the dot because it arrives dropped and wants a look.
  sure: 'none', likely: 'none', unsure: 'warn', rule: 'none',
  done: 'none', running: 'none', waiting: 'none', skipped: 'none', ready: 'none', proposed: 'none',
};

/**
 * The little number in each row's left column: the item's POSITION in the plan as the engine sent it,
 * 1..n. Never the id, which is a wire string ("safefix/Visibility") and means nothing to a reader.
 *
 * Because it is a position in the ENGINE's order, it is a rank on Pro and a rule-order index on free, and
 * it changes when the plan is reordered. That is correct: it is a pointer at a row, not a name for a
 * thing, and the thing's name is its title.
 */
export function positions(items: PlanItem[]): Map<string, number> {
  const map = new Map<string, number>();
  items.forEach((item, index) => map.set(item.id, index + 1));
  return map;
}

/**
 * Which state an item is in, read tolerantly, from TWO engine lanes that do not agree yet.
 *
 * `state` is the change-set lane's field and it is the one to believe when it is there. The targets lane
 * ships a target as a STATUS instead (`status: 'target'`) and says in as many words that it does not set
 * `state`, leaving that to one line in the other lane's mapper
 * (m3-item-shape.json, targets._note and targets._coordination). Until those two land together the page
 * has to read either, so it reads both, in that order.
 *
 * An engine older than both sends neither, and the honest reading of that is the two states its `status`
 * really does carry, plus `gap` for everything else: an engine that never heard of a change set cannot
 * have an item in `proposed` or `applied`, so the page must not invent one. A value this page does not
 * know reads as `gap` for the same reason a history kind it does not know reads as a redraw: a newer
 * engine must never blank a row.
 */
export function itemState(item: PlanItem): PlanState {
  const sent = item.state;
  if (sent && STATE_LABEL[sent] !== undefined) return sent;
  if (item.status === 'target') return 'target';
  if (item.status === 'done') return 'done';
  if (item.status === 'skipped') return 'skipped';
  return 'gap';
}

/** Below this the plan does not put a number on an item at all. It is the same bar the plan uses to
 *  decide whether to propose the item in the first place, so a number under it would describe something
 *  the plan is not standing behind. */
export const CONFIDENCE_BAR = 0.6;
/** At and above this the plan treats the item as settled enough for one press. */
export const CONFIDENCE_SURE = 0.85;

export interface ConfidenceRead {
  /** The printed number, or null when the item is below the bar and shows nothing. */
  text: string | null;
  band: 'sure' | 'look' | 'unscored';
  /** What the bands mean, shown on hover and on keyboard focus. */
  explain: string;
}

export function confidence(value: number | undefined | null): ConfidenceRead {
  const n = typeof value === 'number' && Number.isFinite(value) ? value : null;
  if (n == null || n < CONFIDENCE_BAR) {
    return {
      text: null,
      band: 'unscored',
      explain: 'Not scored. Below 0.60 the plan does not put a number on a task, so none is shown.',
    };
  }
  if (n >= CONFIDENCE_SURE) {
    return {
      text: n.toFixed(2),
      band: 'sure',
      explain: 'Confident: 0.85 and above. The facts behind this one are clear and it is ready for one press.',
    };
  }
  return {
    text: n.toFixed(2),
    band: 'look',
    explain: 'Worth a look: between 0.60 and 0.85. Read the change before you approve it.',
  };
}

// ---- counts and the gauge ----------------------------------------------------------------------------

/** An item nobody is waiting on: finished, or set aside with a reason. A skipped item is deliberately
 *  NOT counted as waiting for anyone. The mockup counted it under "needs me", which made the plan look
 *  like it was asking for something it had already been told to leave alone. */
const waiting = (item: PlanItem) => item.status !== 'done' && item.status !== 'skipped';

export interface PlanGaugeBands {
  done: number; proposed: number; ready: number; you: number; asst: number; target: number; skipped: number;
}
export interface PlanGauge { counts: PlanGaugeBands; shares: PlanGaugeBands }

/**
 * The ink of each band of the plan's gauge, shared by the Plan strip and the Overview's plan bar so a band is
 * one colour everywhere. Colour level B (24 September 2026): the bands a doer owns take that doer's hue (runs
 * on its own is Semanticus, your assistant is the assistant), needs you is the person's blue because it
 * is the state that waits on the reader, to approve keeps the jade because it is the "do this" band, done is
 * the text colour, and the rest are faint.
 */
export const GAUGE_INK: Record<keyof PlanGaugeBands, string> = {
  done: 'var(--sem-fg)', proposed: 'var(--sem-accent)', ready: 'var(--sem-actor-semanticus)', you: 'var(--sem-actor-you)',
  asst: 'var(--sem-actor-assistant)', target: 'var(--sem-faint)', skipped: 'var(--sem-border)',
};

/** The band a readiness score sits in: 75 and over is good, 60 to 74 is careful, under 60 is wrong. The grade
 *  after the plan wears its band's colour rather than the jade, which is kept for "do this". */
export type ScoreBand = 'good' | 'warn' | 'bad';
export function scoreBand(score: number): ScoreBand {
  return score >= 75 ? 'good' : score >= 60 ? 'warn' : 'bad';
}
/**
 * The progress bar, and the only numbers under it.
 *
 * It is a PARTITION: every item is in exactly one band, so the bar can never draw more than the plan.
 * The mockup's bar painted the filter counts, which overlap (an item that is ready and also yours was
 * painted twice), and at 23 items it drew 25 items' worth of bar. The bands here are the question a
 * person is actually asking, in the order they can act on it:
 *   done                already finished
 *   proposed            exact changes are worked out and waiting on one decision
 *   ready               ready and it runs on its own
 *   you                 still waiting for the person
 *   asst                still waiting for their assistant
 *   target              you asked for it and nothing in the model does it yet
 *   skipped             set aside with a reason, waiting for nobody
 * An empty plan draws nothing rather than a full bar of one colour.
 *
 * STATE DECIDES FIRST, then the owner. An item whose changes are waiting on a decision is in the
 * `proposed` band whoever it belongs to, because what a person does about it is the same either way, and
 * an item counted under both its state and its owner would be exactly the double-counting this function
 * exists to stop. `applied` gets no band: the changes landed and the plan is working out whether the gap
 * is really gone, so the item is still open work belonging to whoever it belonged to.
 */
export function gauge(items: PlanItem[]): PlanGauge {
  const done = items.filter((i) => i.status === 'done').length;
  const skipped = items.filter((i) => i.status === 'skipped').length;
  const open = items.filter((i) => waiting(i));
  const inState = (name: PlanState) => open.filter((i) => itemState(i) === name).length;
  const proposed = inState('proposed');
  const target = inState('target');
  const byOwner = (who: PlanOwner) => open.filter((i) => {
    const s = itemState(i);
    return i.who === who && s !== 'proposed' && s !== 'target';
  }).length;
  const counted: PlanGaugeBands = {
    done, proposed, ready: byOwner('auto'), you: byOwner('you'), asst: byOwner('asst'), target, skipped,
  };
  const total = items.length;
  const pct = (n: number) => (total === 0 ? 0 : (n / total) * 100);
  const shares = {} as PlanGaugeBands;
  (Object.keys(counted) as (keyof PlanGaugeBands)[]).forEach((key) => { shares[key] = pct(counted[key]); });
  return { counts: counted, shares };
}

export interface DiffLine { sign: '+' | '-' | '~' | ' '; text: string }

/** The exact change, one line at a time. An item with no diff says so rather than drawing an empty box.
 *  The wire shape is an ARRAY of lines (ModelPlanItem.Diff is string[]). */
export function diffLines(diff?: string[]): DiffLine[] {
  if (!Array.isArray(diff)) return [];
  return diff.filter((line) => typeof line === 'string' && line.length > 0).map((line) => {
    const head = line[0];
    if (head === '+' || head === '-' || head === '~') return { sign: head, text: line.slice(1).trim() };
    return { sign: ' ' as const, text: line };
  });
}

export const DIFF_SIGN_LABEL: Record<DiffLine['sign'], string> = {
  '+': 'added', '-': 'removed', '~': 'changed', ' ': 'unchanged',
};

/** The set on an item, or null. Null is the ordinary answer: most items have no changes worked out. */
export function changeSetOf(item: PlanItem | null | undefined): PlanChangeSet | null {
  const set = item?.changeSet;
  if (!set || typeof set.id !== 'string' || set.id.trim() === '') return null;
  return { ...set, lines: Array.isArray(set.lines) ? set.lines : [], refused: Array.isArray(set.refused) ? set.refused : [] };
}

/** Every line, in the order the engine sent them. Sorting them here would break the one thing a person
 *  is doing on this list, which is reading down it once and deciding as they go. */
export function changeLines(set: PlanChangeSet | null | undefined): PlanChangeLine[] {
  return set ? set.lines : [];
}

/** The lines that would be written. `kept` is the engine's flag and the page never guesses at it. */
export function keptLines(set: PlanChangeSet | null | undefined): PlanChangeLine[] {
  return changeLines(set).filter((line) => line.kept === true);
}

/**
 * The id to send when keeping or dropping this line, or an EMPTY STRING when there is none.
 *
 * It is `planItemId` and never `id`, and the difference is the kind that does not fail: both are
 * strings, so `setPlanItem` would take the wrong one and decide nothing, or decide another line. A line
 * the engine sent no handle for therefore sends NOTHING, and the page draws its Keep and Drop as
 * unpressable rather than firing a call it cannot aim.
 */
export function lineRef(line: PlanChangeLine | null | undefined): string {
  const ref = line?.planItemId;
  return typeof ref === 'string' && ref.trim() !== '' ? ref : '';
}

/**
 * How many would be written, counted off the lines.
 *
 * The engine sends `keptCount` too, and the two agree. This counts the LINES because the lines are what
 * a person is looking at: a Keep that had not reached the engine yet would otherwise leave the button
 * saying one number while the list showed another, and the button is a promise about the list.
 */
export function keptCount(set: PlanChangeSet | null | undefined): number {
  return keptLines(set).length;
}

/** The button that writes. It names the number it would write, so nobody presses it for a different
 *  number than the one they kept. */
export function approveLabel(set: PlanChangeSet | null | undefined): string {
  return `Approve ${keptCount(set)} kept`;
}

/**
 * Are these changes out of date?
 *
 * The engine works a set out against one version of the model. If the model moves after that, the lines
 * no longer describe it and applying them would write yesterday's answer, so the engine refuses. A stale
 * set must not offer Approve at all: a button that can only be refused teaches a person the page is
 * broken, and leaves the recovery nowhere.
 *
 * EITHER FLAG IS ENOUGH. `staleAtProposal` is the stored one and `stale` is recomputed on every read,
 * and the ORDINARY case is the second: a set that was fine when it was worked out and went bad when
 * somebody edited the model. Reading only the stored flag left Approve on screen for exactly that case
 * (Astra finding 9), which is the one that happens every day.
 */
export function isStale(set: PlanChangeSet | null | undefined): boolean {
  return !!set && (set.stale === true || set.staleAtProposal === true);
}

/**
 * A wire reference as a person reads it. "column:Sales/Region" is the engine's way of naming a thing and
 * it is not a name anybody types. An unknown shape comes back untouched, because a reference this
 * function cannot read is still the only name the line has.
 */
export function objectWords(ref: string | null | undefined): string {
  const raw = String(ref ?? '').trim();
  if (!raw) return '';
  const colon = raw.indexOf(':');
  if (colon <= 0) return raw;
  const kind = raw.slice(0, colon);
  const rest = raw.slice(colon + 1);
  const slash = rest.indexOf('/');
  if (kind === 'column' && slash > 0) return `${rest.slice(0, slash)}[${rest.slice(slash + 1)}]`;
  if (kind === 'measure' && slash > 0) return rest.slice(slash + 1);
  if (kind === 'table' || kind === 'measure' || kind === 'column') return rest;
  return rest || raw;
}

/**
 * What the Objects line of a row's details says (verify M5, 29 Sep). A row the person or the assistant typed names
 * its objects; the best-practice row and the words row do not, on purpose: their `objects` stay empty because that
 * list is what the footprint reads. They say what they touch in `touches` ("361 objects"), in the tables they touch
 * and in their change lines, so the page uses those instead of "Nothing named yet", which was untrue: the row
 * touches hundreds of things and the MCP door lists every one.
 *
 * Named objects: all of them, as before. Otherwise a count and the first few tables with what is in each, then how
 * many tables are left; with no tables, the first few objects the change lines name, then how many more.
 */
/** The first few names and "and 7 more": an opened row is a short read, not a list of every object (UAT P13). */
export function firstNames(names: readonly string[], few = 3): string {
  const more = names.length - few;
  return more > 0 ? `${names.slice(0, few).join(', ')} and ${more} more` : names.join(', ');
}

/** A row's why in plain words: the engine's brief to the assistant names tools (get_fix_prompt (system bpa), apply_fix, delete_objects), and a person
 *  reads none of that. A sentence naming a tool is dropped, and what is left is cut at two sentences (UAT F16). */
export function plainWhy(why: readonly string[] | null | undefined, sentences = 2): string {
  const kept: string[] = [];
  for (const w of why ?? []) {
    for (const part of String(w ?? '').split(/(?<=[.!?])\s+/u)) {
      const t = part.trim();
      if (t && !/\b[a-z]+(?:_[a-z]+)+\b|\(system \w+\)/u.test(t)) kept.push(t);
    }
  }
  return kept.slice(0, sentences).join(' ');
}

export function objectsLine(item: Pick<PlanItem, 'objects' | 'touches' | 'tables' | 'diff'>, few = 3): string {
  const named = (item.objects ?? []).filter(Boolean);
  if (named.length) return firstNames(named.map(objectWords), few);
  const count = /^\d[\d,]*\s+\S/u.test(item.touches ?? '') ? String(item.touches).split(/[;.]/u)[0].trim() : '';
  const tables = (item.tables ?? [])
    .map((t) => typeof t === 'string' ? { name: t, what: '' } : { name: t.name, what: t.what ?? '' })
    .filter((t) => t.name);
  if (tables.length) {
    const shown = tables.slice(0, few).map((t) => t.what ? `${t.name} (${t.what})` : t.name).join(', ');
    const more = tables.length - few;
    return `${count ? count + ' in ' : ''}${shown}${more > 0 ? ` and ${more} more table${more === 1 ? '' : 's'}` : ''}.`;
  }
  const lines = (item.diff ?? [])
    .map((l) => String(l).replace(/^[+~-]\s+/u, '').split(':')[0].trim())
    .filter(Boolean);
  if (lines.length) {
    const total = Number.parseInt(count.replace(/,/gu, ''), 10);
    const more = Number.isFinite(total) ? total - Math.min(few, lines.length) : 0;
    return `${lines.slice(0, few).join(', ')}${more > 0 ? ` and ${more} more` : ''}.`;
  }
  // A bare count reads as the row's own words, as N9 had it (no full stop added).
  return count || 'Nothing named yet.';
}

/**
 * The PREVIOUS run for this item, as one quiet line, or nothing.
 *
 * An item id names a FAMILY, so work keeps arriving under it: four descriptions approved today and a
 * fifth added tomorrow. The set then carries `lastAppliedAt` and `lastAppliedCount` beside the new
 * proposal (m3-item-shape.json, `_changeSetFields`). They are a RECEIPT. They are not lines to review,
 * they decide nothing, and they must never make the pending line look as though it has already run,
 * which is the shape of Astra finding 10.
 *
 * So it is one sentence, in the past tense, and the lines under it are still the proposal.
 */
export function receiptLine(set: PlanChangeSet | null | undefined): string {
  const n = Number(set?.lastAppliedCount) || 0;
  if (n <= 0) return '';
  const when = set?.lastAppliedAt ? timeLabel(set.lastAppliedAt) : '';
  const many = `${n} change${n === 1 ? '' : 's'} ${n === 1 ? 'was' : 'were'} applied here before`;
  return when ? `${many}, at ${when}.` : `${many}.`;
}

/**
 * Can this line be decided, right now, through this set?
 *
 * A change plan id ("ci-0001") is A POSITION IN A LIST THAT GETS REBUILT, not a name for a thing. Clear
 * a change plan and add something else, and the new plan hands ci-0001 to whatever is first now. A
 * DETACHED snapshot still displayed the old handle, and the page still enabled its Keep and Drop, so
 * one press changed an unrelated item's approval flag (Astra finding 7).
 *
 * The engine now clears the handle on a detached snapshot. Three things have to hold before the page
 * offers a decision, and the page can check all three:
 *
 *   1. the set is the live PROPOSAL, not an applied or cancelled receipt whose handles have been reused;
 *   2. the line carries a handle at all;
 *   3. that handle is one THIS set holds, so it names this line and not one somebody else is looking at.
 *
 * The third looks redundant for a line taken from the set, and it is the one that matters: it is what
 * makes "a handle the page cannot verify" unrepresentable rather than merely unlikely.
 */
export function canDecide(set: PlanChangeSet | null | undefined, line: PlanChangeLine | null | undefined): boolean {
  if (!set || set.state !== 'proposed') return false;
  const ref = lineRef(line);
  if (!ref) return false;
  return changeLines(set).some((row) => lineRef(row) === ref);
}

/** What a person reads where the buttons would have worked. A dead control that explains nothing is the
 *  thing this page keeps refusing to ship, so the reason stands beside the dead buttons. */
export const DETACHED_LINE = 'These changes are from an older list, so they cannot be kept or dropped. '
  + 'Work them out again.';

/**
 * What one line actually DOES, read off the wire: the thing, the property, the value now and the value
 * being approved.
 *
 * The engine's `line` sentence is built from the object name and the operation title
 * (LocalEngine.PlanChanges.cs:301), so on its own it says "Margin %: set description" and "Margin %:
 * update DAX". Keep and Approve were offered over exactly that, which let a person approve a
 * description they had never read and a formula they had never seen (Astra finding 11). The values are
 * on the wire, per line, and this is what puts them on screen.
 *
 * `hasValues` is false when the engine sent neither side, and a line like that shows NO pair rather
 * than an empty before and an empty after, which would read as "nothing changes here".
 *
 * An EMPTY value is not a missing one. A measure with no description has an empty `before`, and that is
 * a fact worth printing; it prints as the word "empty", because a blank cell in a before-and-after
 * reads as though nothing is being changed at all.
 */
export interface PlanChangeValues {
  /** The thing, in words a person reads: "Margin %", "Sales[Region]". */
  target: string;
  /** The property as the engine names it ("aiDataSchema"). The page shows `propertyWords`. */
  property: string;
  /** The property in plain words: "What Copilot sees", never "aiDataSchema". */
  propertyWords: string;
  before: string;
  after: string;
  beforeLabel: string;
  afterLabel: string;
  /** Did the engine send either side? A line with neither draws no pair at all. */
  hasValues: boolean;
  /** Is this a block of code rather than a value? A formula squeezed onto one line cannot be read. */
  isExpression: boolean;
}

/** The properties whose values are CODE, by what the engine calls them. The value's own shape decides
 *  too: anything carrying a line break is a block, whatever the property is named. */
const EXPRESSION_PROPERTY = /expression|dax|formula|partition|query|calculation/i;

/** The engine's property names in the words the page shows. A name missing here is split at its capitals rather
 *  than shown as code ("isAvailableInMdx" would read "Is available in mdx"). */
export const PROPERTY_WORDS: Record<string, string> = {
  aiDataSchema: 'What Copilot sees',
  aiInstructions: 'AI instructions',
  description: 'Description',
  formatString: 'Format',
  displayFolder: 'Display folder',
  mExpression: 'Power Query formula',
  summarizeBy: 'Summarize by',
  isHidden: 'Hidden',
  dataCategory: 'Data category',
  name: 'Name',
  object: 'Object',
  synonyms: 'Synonyms',
  crossFilter: 'Filter direction',
  expression: 'Formula',
  dateTable: 'Date table',
  linguisticSchema: 'Language settings',
  sortByColumn: 'Sort by',
  dataType: 'Data type',
  isAvailableInMdx: 'Available to Excel and MDX',
};

export function propertyWords(property: string | null | undefined): string {
  const p = String(property ?? '').trim();
  if (!p) return '';
  if (PROPERTY_WORDS[p]) return PROPERTY_WORDS[p];
  if (!/^[a-z][A-Za-z0-9]*$/u.test(p) || !/[A-Z]/u.test(p)) return p;
  const words = p.replace(/([a-z0-9])([A-Z])/gu, '$1 $2').toLowerCase();
  return words.charAt(0).toUpperCase() + words.slice(1);
}

export function changeValues(line: PlanChangeLine | null | undefined): PlanChangeValues {
  const property = String(line?.property ?? '').trim();
  const before = typeof line?.before === 'string' ? line.before : '';
  const after = typeof line?.after === 'string' ? line.after : '';
  const hasValues = typeof line?.before === 'string' || typeof line?.after === 'string';
  // Text made only of blank characters is a VALUE a person typed, not "empty": saying "empty" over it invites a tick that writes
  // over their text (THE FORMAT FILL RULE, addendum 3 item 18). Nothing is called empty unless it holds no character at all.
  const said = (value: string) => (value === '' ? 'empty'
    : value.trim() === '' ? `${value.length} blank character${value.length === 1 ? '' : 's'} you typed` : value);
  const BREAK = String.fromCharCode(10);
  return {
    target: objectWords(line?.target),
    property,
    propertyWords: propertyWords(property),
    before,
    after,
    beforeLabel: said(before),
    afterLabel: said(after),
    hasValues,
    isExpression: (property !== '' && EXPRESSION_PROPERTY.test(property))
      || before.includes(BREAK) || after.includes(BREAK),
  };
}

/**
 * What an item whose changes are waiting offers, and where the free line runs through it.
 *
 * READING THE WORK AND REFUSING IT IS ALWAYS FREE. Keep, Drop, Drop all, Skip and Work them out again
 * are a person looking at their own assistant's work and saying yes or no to it, which is the tool. A
 * person must always be able to refuse, so nothing about saying no is ever gated.
 *
 * ONE THING IS PRO, and it is the same thing Start is: APPROVE ON AN ITEM THAT RUNS ON ITS OWN. That is
 * the product doing the work in one press, wearing a different word. Approve on an item that is yours or
 * your assistant's is free, because there the press is the only way the work ever gets done at all.
 * `canRun` is required for exactly that reason.
 */
export type ReviewAction = 'approve' | 'drop-all' | 'propose-again' | 'skip';

export function reviewActions(item: PlanItem, canRun: boolean): ReviewAction[] {
  const set = changeSetOf(item);
  if (!set || itemState(item) !== 'proposed') return [];
  // A Jev set is worked out again by the engine on the next read, so there is no "work them out again"
  // to press: offering it would only ever be refused.
  if (isStale(set)) return item.who === 'jev' ? ['drop-all', 'skip'] : ['propose-again', 'drop-all', 'skip'];
  if (item.who === 'auto' && !canRun) return ['drop-all', 'skip'];
  return ['approve', 'drop-all', 'skip'];
}

// ---- the panels a route opens over the plan -------------------------------------------------------

/** Which panel a route is asking the Plan page to show, if any. */
export type PlanPanel = 'none' | 'changes' | 'draft';

/**
 * The route's focus, as a panel. TOTAL on purpose: every input has an answer, and the answer for a plain
 * plan route is `none`.
 *
 * Codex found the hole this closes (PR 388, comment 4070396721). The page used to read the focus with a
 * chain of ifs that only ever OPENED a panel. With the review panel up, pressing the Plan area button
 * routes back to `plan`, so focus goes undefined, the chain fell through, and nothing closed the panel.
 * The shell renders `plan`, `optimize` and `spec` through ONE unkeyed PlanView, so React keeps the
 * component and its state, and a person who asked for the plan kept looking at the panel.
 *
 * A total mapping cannot fall through, which is why this is a value rather than a branch: "no panel" is
 * an answer the page has to act on, not a case somebody has to remember to write.
 */
export function panelFor(focus: string | null | undefined): PlanPanel {
  if (focus === 'changes') return 'changes';
  if (focus === 'draft') return 'draft';
  return 'none';
}

// ---- the plan's own story (the Overview reads it through historyEntries) --------------------------------

export const HISTORY_KIND_LABEL: Record<PlanHistoryKind, string> = {
  done: 'done',
  skipped: 'set aside',
  unskipped: 'put back',
  refused: 'nothing ran',
  redrawn: 'redrawn',
  drawn: 'drawn for the first time',
  started: 'started',
  // The review road. "changes proposed" is not a decision and did not touch the model; "changes
  // approved" is the one that wrote; "changes dropped" is a person saying no and nothing being written.
  proposed: 'changes proposed',
  approved: 'changes approved',
  cancelled: 'changes dropped',
  // The person gave a task to their assistant again after it could not be done.
  given: 'given again',
};

export const HISTORY_ACTOR_LABEL: Record<PlanHistoryActor, string> = {
  you: 'you', assistant: 'your assistant', plan: 'the plan', engine: 'Semanticus',
};

/** The time down the left of the timeline. The DATE is in the day heading above it, so this is only
 *  ever a time of day, and it reads the way a clock does rather than the way a log does. */
export function timeLabel(when: string): string {
  const at = Date.parse(when);
  if (!Number.isFinite(at)) return '';
  const d = new Date(at);
  const hours = d.getHours();
  const suffix = hours < 12 ? 'am' : 'pm';
  const twelve = hours % 12 === 0 ? 12 : hours % 12;
  return `${twelve}:${String(d.getMinutes()).padStart(2, '0')} ${suffix}`;
}

export interface PlanHistoryEntry {
  when: string;
  kind: PlanHistoryKind;
  label: string;
  actor?: PlanHistoryActor;
  /** The muted line beside the title, already joined: "your assistant · revision 131". */
  who: string;
  title: string;
  sentence: string;
  /** The applied change lines, on a `done` or a `started` and nowhere else. */
  detail: string[];
  /** The person's own words, on a `skipped`. */
  reason: string;
  /** The engine's refusal, on a `refused`. `sentence` usually says the same thing; this is the field
   *  the entry opens onto, and it is empty unless the engine sent one. */
  refusal: string;
  appeared: string[];
  /** What LEFT the plan without being finished: `gone` minus `completed`. The engine sends completed as
   *  a subset of gone, so printing both raw would name the same item twice and read as two events. */
  dropped: string[];
  completed: string[];
  itemId?: string;
  revision?: number;
  /** True for a row off the OLD wire, which carried a time and a sentence and nothing else. The page
   *  reads it to know not to offer an expander over detail the row never had. */
  legacy: boolean;
}

const strings = (value: unknown): string[] =>
  (Array.isArray(value) ? value.filter((line) => typeof line === 'string' && line.length > 0) : []);

/**
 * One history row, read.
 *
 * `detail` is ONE field carrying three different things by kind, because that is how the engine sends
 * it, and this is the one place that is untangled: change lines on a done or a started, the person's
 * reason on a skip, the refusal sentence on a refusal. Everything downstream reads the named field.
 *
 * An OLD row has a `text` and no `kind`. It draws as a REDRAWN entry with that text as its sentence,
 * which is the honest reading of it: the old wire only ever recorded the plan changing, in counts
 * ("1 item done, 1 new"), and nothing in it says who acted or which item it was about. It is marked
 * `legacy` so the page never offers it an expander over detail it has not got.
 *
 * A kind this page does not know draws as a redraw too, so a newer engine never blanks a row. It is not
 * marked legacy, because it is not one.
 */
export function historyEntry(row: PlanHistoryRow): PlanHistoryEntry {
  const legacy = !row.kind;
  const kind: PlanHistoryKind = row.kind && HISTORY_KIND_LABEL[row.kind] ? row.kind : 'redrawn';
  const detail = strings(row.detail);
  // Revision 0 is what an old row is read back with, and "revision 0" on screen is noise standing
  // where a fact should be. A revision is only printed when something was really written.
  const revision = typeof row.revision === 'number' && Number.isFinite(row.revision) && row.revision > 0
    ? row.revision : undefined;
  const who = [
    row.actor && HISTORY_ACTOR_LABEL[row.actor] ? HISTORY_ACTOR_LABEL[row.actor] : '',
    revision === undefined ? '' : `revision ${revision}`,
  ].filter(Boolean).join(' · ');
  const completed = strings(row.completed);
  const gone = strings(row.gone);
  return {
    when: row.when,
    kind,
    label: HISTORY_KIND_LABEL[kind],
    actor: row.actor,
    who,
    title: row.title ?? '',
    sentence: row.sentence ?? row.text ?? '',
    // The change lines, on every kind that HAS change lines. The three review kinds carry them for the
    // same reason a done does: the point of the entry is what the lines said.
    detail: kind === 'done' || kind === 'started' || kind === 'proposed' || kind === 'approved'
      || kind === 'cancelled' ? detail : [],
    reason: kind === 'skipped' ? detail.join(' ') : '',
    refusal: kind === 'refused' ? detail.join(' ') : '',
    appeared: strings(row.appeared),
    dropped: gone.filter((name) => !completed.includes(name)),
    completed,
    itemId: row.itemId,
    revision,
    legacy,
  };
}

export type HistoryFilter = 'all' | 'changes' | 'decisions' | 'redraws';

/**
 * What each chip keeps.
 *
 * They are NOT a partition and are not meant to be. "Nothing ran" is not a change to the model, not a
 * decision anybody made, and not a redraw, so it appears under Everything and nowhere else. Sweeping it
 * into "changes to the model" to make the sets add up would be the page saying the model changed when
 * it did not.
 */
export const HISTORY_FILTER_KINDS: Record<HistoryFilter, PlanHistoryKind[]> = {
  all: ['drawn', 'redrawn', 'done', 'started', 'skipped', 'unskipped', 'refused',
    'proposed', 'approved', 'cancelled', 'given'],
  // Approving is what WROTE, so it is a change to the model; proposing is not, whatever it is called.
  changes: ['done', 'approved'],
  // Dropping a set is a person saying no, which is the same kind of thing as setting an item aside.
  // Proposing is nobody deciding anything, so it sits under Everything and nowhere else, exactly as
  // "nothing ran" does. Sweeping it in to make the sets add up would be the page calling the engine
  // working something out a decision somebody made.
  decisions: ['skipped', 'unskipped', 'cancelled', 'given'],
  redraws: ['redrawn', 'drawn'],
};

/**
 * The timeline, newest first, filtered and searched.
 *
 * THE WIRE IS OLDEST FIRST and this reverses it. That is not the page overruling the engine the way a
 * re-sort of the ITEMS would be: the plan's order is a decision the engine made and the page keeps,
 * while the history is a chronological list and "newest first" is how a person reads one. Reversing
 * is exact and keeps ties in the order the engine wrote them, which a sort by timestamp would not.
 */
export function historyEntries(plan: Plan, filter: HistoryFilter = 'all', query = ''): PlanHistoryEntry[] {
  const q = query.trim().toLowerCase();
  const keep = HISTORY_FILTER_KINDS[filter] ?? HISTORY_FILTER_KINDS.all;
  return (plan.history ?? []).map(historyEntry).reverse()
    .filter((entry) => keep.includes(entry.kind))
    .filter((entry) => {
      if (!q) return true;
      const hay = [entry.when, entry.label, entry.title, entry.sentence, entry.reason, entry.refusal,
        entry.who, ...entry.detail, ...entry.appeared, ...entry.dropped, ...entry.completed]
        .join(' ').toLowerCase();
      return hay.includes(q);
    });
}

/** The same top three the whole page leads with. Open work only, in the ENGINE's order, so the card and
 *  the page can never disagree about what is next. Ranked on Pro, rule order on free: the card does not
 *  decide that and, since the sort came out, no longer undoes it either. */
export function topThree(plan: Plan): PlanItem[] {
  return plan.items.filter((i) => i.status !== 'done' && i.status !== 'skipped').slice(0, 3);
}

// THERE IS NO nextCardLine HERE ANY MORE. It built the Overview card's sentence out of the FILTER counts,
// and those filters overlap by design: an item that is ready and whose owner is you is in both, so a plan
// of five read "5 things left, 5 ready to run and 2 waiting for you", which is seven things. Kane read
// that on the Yoga build on 21 September. The Overview drew the plan from the `gauge` partition instead,
// whose bands add up to the plan exactly, until it retired in slice 11. The card that carried the line,
// plannext.tsx, went with the one-screen Overview on 23 September. The builder was deleted rather than
// repointed because nothing called it: a second line-builder kept alive for nobody is the next caller's bug.

// ---- Jev lines ------------------------------------------------------------------------------------

/** The confidence word on a Jev line, or null on any other line. Read off the engine, never worked out
 *  here from the number: the engine owns where the bands are cut. */
export function jevWord(line: PlanChangeLine): JevConfidenceWord | null {
  if (line.provenance?.kind !== 'decision') return null;
  // "sure" beside a line the engine could not prove would vouch for it. A suggestion carries no word (the engine sends none
  // either; this is the page's own guard, so an older or a hand-made wire cannot bring it back).
  if (line.suggestion) return null;
  const w = line.provenance?.confidenceWord;
  return w === 'sure' || w === 'likely' || w === 'unsure' ? w : null;
}

/** What each word means on the chip's hover, in the mockup's words. */
export const JEV_WORD_TITLE: Record<JevConfidenceWord, string> = {
  sure: 'Jev is sure. Kept for you.',
  likely: 'Jev thinks this is likely right. Kept for you.',
  unsure: 'Jev is unsure, so this arrives dropped. Keep it to include it.',
};

/** The line under an unsure change that nobody has kept yet. Once a person keeps it, it is theirs, and
 *  the nudge goes. */
export const JEV_UNSURE_LINE = 'unsure: have a look. Keep it to include it.';

/** The marker on a line a family's RULE chose inside a Jev row, the same place a Jev line shows how sure it was
 *  (verifier D5, 26 Sep): without it a rule's line and Jev's read alike. Only rows Jev works carry it, because on
 *  a rule's own row every line is the rule's and a marker would say nothing. */
export const RULE_WORD = 'rule';
export const RULE_WORD_TITLE = "Semanticus's own rule chose this, because Jev was not sure. Kept for you.";

export function ruleWord(line: PlanChangeLine): typeof RULE_WORD | null {
  return line.provenance?.kind === 'rule' ? RULE_WORD : null;
}

/** Good practice a family applies to every column it looks at (column.summarize: stop adding up by itself, hide the
 *  column once its measure exists). Nobody picked it and Jev was not unsure, so its marker says so. */
export const POLICY_RULES = new Set(['SUMMARIZE-OFF', 'BASE-MEASURE-HIDE']);
export const POLICY_WORD_TITLE = "Good practice Semanticus applies to every number column here. Nobody had to choose it.";

export function ruleWordTitle(line: PlanChangeLine): string {
  const rule = line.provenance?.rule ?? '';
  return POLICY_RULES.has(rule) ? POLICY_WORD_TITLE : RULE_WORD_TITLE;
}

/** The words on the row of a Jev suggestion: the engine's plain note, printed, never only a hover. Null on any other line. A
 *  line the engine flagged as a suggestion but sent no note for still says so, in the same plain words. */
export const SUGGESTION_NOTE = 'Jev suggests this format; check it fits before you run it.';
export function suggestionNote(line: PlanChangeLine): string | null {
  if (!line.suggestion) return null;
  const text = String(line.note ?? '').trim();
  return text || SUGGESTION_NOTE;
}

export function unsureAndDropped(line: PlanChangeLine): boolean {
  return jevWord(line) === 'unsure' && !line.kept;
}

/** The tick beside a line the engine checked, and what the tick's hover says. Null when there was no
 *  check, which is shown as nothing rather than as a pass. */
export function checkTick(line: PlanChangeLine): string | null {
  if (line.suggestion || !line.check?.exists) return null;
  const text = String(line.check.text ?? '').trim();
  return text ? `Checked: ${text}` : 'Checked before it was offered.';
}

// =====================================================================================================
// THE PLAN AT THE HEART (lane U1, contract sections 1, 4, 7 and 9, mockup plan-heart.html sections 2, 3, 5, 6)
//
// Source, doer and state are three separate facts on a row, and all three come from the engine. Nothing
// below derives a state word: `stateWordOf` reads the wire and only falls back for an engine older than
// slice 7, which sends none. The counts on the strip are the engine's; the page only draws them.
// =====================================================================================================

export const SOURCE_LABEL: Record<PlanSource, string> = {
  found: 'Found by Semanticus', jev: 'Semanticus Pro', words: 'Your words', you: 'You added', workflow: 'Workflow', repeat: 'Repeat',
};
export const DOER_LABEL: Record<PlanDoer, string> = { semanticus: 'Semanticus', you: 'You', assistant: 'Your assistant' };
export const DOER_ORDER: PlanDoer[] = ['semanticus', 'you', 'assistant'];
/** The engine's note on a done row whose gap another change closed; the row then says so and never credits its owner. */
export const NO_LONGER_NEEDED = 'No longer needed';
/** The short form of a note too long for the state cell, so a Waiting row or a Ready row nobody can take says who and what to do without a hover. The full
 *  note stays on hover. Null when the note has no short form (the cell then shows it only when it fits). */
export function stateNoteShort(note: string | null | undefined, assistantConnected = true): string | null {
  if (!note) return null;
  // Plain words, never a phrase that reads like a button: "ask your assistant" was text that did nothing when pressed (UAT K7), and with no
  // assistant at all it sent the person to someone who is not there (UAT F8).
  const toAssistant = assistantConnected ? 'for your assistant' : 'needs an assistant';
  const waits = /^Waiting for (you|your assistant|Semanticus|another task)\b/u.exec(note);
  if (waits) return /^Waiting for your assistant\. Ask it/u.test(note) ? toAssistant : `for ${waits[1]}`;
  if (/^Ask your assistant to run the plan/u.test(note)) return toAssistant;
  if (/^You paused your assistant's tasks\. Ask your assistant/u.test(note)) return 'paused by you';
  return null;
}

/** A reason on a closed row: the first 30 characters, cut at a word, with three dots when it was cut. The whole reason is on hover and in the open row. */
export function reasonShort(note: string | null | undefined): string | null {
  const t = (note ?? '').replace(/\s+/gu, ' ').trim();
  if (!t) return null;
  if (t.length <= 30) return t;
  const cut = t.slice(0, 30);
  const at = cut.lastIndexOf(' ');
  return `${(at > 12 ? cut.slice(0, at) : cut).replace(/[.,;:]+$/u, '')}...`;
}

export const closedByOther = (item: PlanItem): boolean => item.stateWord === 'done' && item.stateNote === NO_LONGER_NEEDED;

export const STATE_WORD_LABEL: Record<PlanStateWord, string> = {
  needs_you: 'Needs you', ready: 'Ready', waiting: 'Waiting', paused: 'Paused', on_it: 'On it',
  checking: 'Applied, checking', done: 'Done', set_aside: 'Set aside',
};

/** The engine flags a row assistantAtWork for 45 s after the assistant's last edit (AssistantAtWorkWindow). The page reads again just after, so On it ends by itself. */
export const AT_WORK_REREAD_MS = 46_000;

/** The state word as sent. An engine older than slice 7 sends none; its own status is then read, never guessed past. */
export function stateWordOf(item: PlanItem): PlanStateWord {
  if (item.assistantAtWork && (item.stateWord === 'ready' || item.stateWord === 'waiting')) return 'on_it';
  if (item.stateWord) return item.stateWord;
  if (item.status === 'done') return 'done';
  if (item.status === 'skipped') return 'set_aside';
  return 'ready';
}

/** The doer as sent, or the old owner read across for an engine that sends none. */
export function doerOf(item: PlanItem): PlanDoer {
  if (item.doer) return item.doer;
  return item.who === 'you' ? 'you' : item.who === 'asst' ? 'assistant' : 'semanticus';
}

/** What a quiet plan update must not move. */
export interface PointerAnchor {
  /** The row, by id: found again after the update when the element under the pointer is gone. */
  id: string;
  top: number;
  /** The element under the pointer itself (a line's Keep or Drop inside an open row, say), and where it was. */
  el: Element | null;
  elTop: number;
}

/** The place a quiet plan update must not move: the element under the pointer when it is in a row (its header, or
 *  the open row's detail and lines below it), else the open row when it is on screen. Null when neither is, and then
 *  the browser's own scroll anchoring is enough. Reads the DOM, so it runs only on the page.
 *
 *  The ELEMENT, not only the row's header: an open row's Keep and Drop sit below its reasons, outside the header, and
 *  when that same row grew above the pointer the header held still while the control under the pointer moved (verify
 *  round 2, R2-6: 16px on lane p). */
export function rowUnderPointer(pointer: { x: number; y: number } | null, openId: string | null): PointerAnchor | null {
  const at = pointer ? document.elementFromPoint(pointer.x, pointer.y) : null;
  const inRow = at instanceof Element ? (at.closest('[data-row]') ?? at.closest('.pw-item')) : null;
  let row = inRow ? (inRow.matches('[data-row]') ? inRow : inRow.querySelector('[data-row]')) : null;
  let el: Element | null = row && at instanceof Element ? at : null;
  if (!row && openId) {
    const open = document.querySelector(`[data-row="${CSS.escape(openId)}"]`);
    const box = open?.getBoundingClientRect();
    if (open && box && box.bottom > 0 && box.top < window.innerHeight) { row = open; el = null; }
  }
  const id = row?.getAttribute('data-row');
  return row && id ? { id, top: row.getBoundingClientRect().top, el, elTop: el ? el.getBoundingClientRect().top : 0 } : null;
}

/** The element that scrolls a row: its nearest ancestor that scrolls up and down, else the page itself. */
export function scrollerOf(el: Element): Element {
  for (let p = el.parentElement; p; p = p.parentElement) {
    const y = getComputedStyle(p).overflowY;
    if ((y === 'auto' || y === 'scroll') && p.scrollHeight > p.clientHeight) return p;
  }
  return document.scrollingElement ?? document.documentElement;
}

/** The empty reply line. Only the assistant reads a note on its next call, so only its rows promise that. */
export function noReplyWords(doer: PlanDoer, item: Pick<PlanItem, 'claim'>): string {
  return doer === 'assistant' || item.claim?.claimedBy === 'assistant'
    ? 'No reply yet. It reads this on its next call.' : 'No reply yet.';
}

export function sourceOf(item: PlanItem): PlanSource {
  return item.source ?? (item.who === 'jev' ? 'jev' : 'found');
}

// ---- Jev on the calm row (1.5, Kane's pick A of the Jev plan) -------------------------------------------------------------

/** Jev's mark: a spark and the word Jev after the title. Only a row whose source is Jev AND that carries a line Jev chose (the "Jev is
 *  still looking" row has no lines, so it is not "chosen by Jev"). */
export function jevChoseRow(item: PlanItem): boolean {
  return sourceOf(item) === 'jev' && changeLines(item.changeSet).some((l) => l.provenance?.kind === 'decision');
}

/** What the row is worth in AI ready points, as the engine measured it by trying the row on the model ("+4 AI ready"). Null when it
 *  said nothing: no number is ever worked out here. */
export const AI_WORTH = /^\+\d+ AI ready$/u;
export function aiWorthOf(item: PlanItem): string | null {
  const v = item.value?.trim();
  return v && AI_WORTH.test(v) ? v : null;
}

/** Jev's reason for a row in one line: the row's first fact (what is missing and what it costs), which the engine writes. Null when there is none. */
export function jevReasonOf(item: PlanItem): string | null {
  if (!jevChoseRow(item)) return null;
  const first = (item.why ?? []).map((w) => w.trim()).find(Boolean);
  return first ?? null;
}

/** How many lines of these rows Jev chose and the person kept, so the Done line can say "Jev chose 37." */
export function jevChoseCount(items: readonly PlanItem[]): number {
  let n = 0;
  for (const item of items) for (const line of keptLines(item.changeSet)) if (jevWord(line) !== null) n++;
  return n;
}

/** The same count over only the rows a press really ran (a press can stop after an earlier wave, or skip a row). */
export function jevChoseRan(held: readonly PlanItem[], ranIds: readonly string[] | undefined): number {
  const ran = new Set(ranIds ?? []);
  return jevChoseCount(held.filter((i) => ran.has(i.id)));
}

/** Still to do: not done, not set aside, and not applied-and-checking (which is off the work list, contract 1.4). */
export function isOpenRow(item: PlanItem): boolean {
  const w = stateWordOf(item);
  return w !== 'done' && w !== 'set_aside' && w !== 'checking';
}

/** A task: an asked row with a random id (contract 1.3). Only these have a place in the assistant's order. */
export const isTask = (item: PlanItem): boolean => item.id.startsWith('pt-');

/** Which area holds each page, so a jump reads "Review › AI readiness". Mirrors App.tsx TAB_GROUPS; the page test
 *  holds the two together. */
export const PAGE_AREA: Record<string, string> = {
  plan: 'Plan', history: 'Plan',
  data: 'Explore', query: 'Explore', askdata: 'Explore', diagram: 'Explore', lineage: 'Explore', stats: 'Explore',
  search: 'Edit', daxlab: 'Explore', instructions: 'Edit', advmodels: 'Edit', mcode: 'Edit', knowledge: 'Edit',
  bpa: 'Review', readiness: 'Review', copilottest: 'Review', tests: 'Review', docs: 'Review', evidence: 'Review',
  deploy: 'Publish',
  workflows: 'Workflows',
};

/** "Review › AI readiness". A page whose label is its area's own name is said once. */
export function pagePlace(jump: PlanJump | null | undefined): string {
  if (!jump?.tool) return '';
  const area = PAGE_AREA[jump.tool];
  const label = jump.label || jump.tool;
  return !area || area === label ? label : `${area} › ${label}`;
}

/** The source mark's words. A Send says the page it came from (contract 1.2, SourcePage). */
export function sourceLine(item: PlanItem, labelOf?: (tool: string) => string | undefined): string {
  const source = sourceOf(item);
  if (source === 'you' && item.sourcePage) {
    const label = labelOf?.(item.sourcePage) ?? item.sourcePage;
    return `${SOURCE_LABEL.you} · ${pagePlace({ tool: item.sourcePage, label })}`;
  }
  return SOURCE_LABEL[source];
}

/** Who acts next on the task a row waits for. No doer on the wire keeps the plain words. */
export const WAITS_WHO: Record<string, string> = { you: 'Waits for you', assistant: 'Waits for your assistant', semanticus: 'Waits for Semanticus' };

/** The sub line: the engine already writes "Waits for: <title>" there when a row waits (PlanFootprint.cs), so the
 *  page reads the waits off the wire only to bold the title it names. An optional row keeps its word in front: it
 *  nearly always waits, and the waits line replaces the sub line that said "Optional" (verify, 28 Sep, L1). */
export function subLine(item: PlanItem, items?: PlanItem[]): { waits: string | null; who: string; text: string; optional?: boolean; aside?: PlanItem; why?: string } {
  const waits = item.waitsFor?.[0];
  if (waits && stateWordOf(item) === 'waiting') {
    const more = (item.waitsFor?.length ?? 0) - 1;
    // A row it waits for that the person set aside never turns Ready by itself, so say so, and the row offers Put back for it (verifier, 11 Oct).
    const aside = item.waitsFor!.map((w) => items?.find((i) => i.id === w.id)).find((i) => i && stateWordOf(i) === 'set_aside');
    if (aside) {
      return { waits: null, who: 'Waits for', text: `Waits for '${aside.title}', which you set aside` + (more > 0 ? ` and ${more} more` : ''), optional: !!item.optional, aside };
    }
    return { waits: item.waitsFor!.map((w) => w.title).join('; '), who: WAITS_WHO[waits.doer ?? ''] ?? 'Waits for', text: '', optional: !!item.optional, why: sharesFields(item, items) ? WAITS_WHY : WAITS_WHY_PLAIN };
  }
  return { waits: null, who: 'Waits for', text: (item.sub ?? '').replace(/: /u, ' · ') };
}

/** Why a row waits, in one plain line (hero16b section D). "Same fields" is said only when the row and every row it waits for name a field in common; the wait data carries no reason, so
 *  anything else gets the plain line, which is always true. */
export const WAITS_WHY = 'same fields, so it goes second';
export const WAITS_WHY_PLAIN = "it needs that task's changes first";

function sharesFields(item: PlanItem, items?: PlanItem[]): boolean {
  const mine = new Set((item.objects ?? []).filter(Boolean));
  if (!mine.size || !items) return false;
  return item.waitsFor!.every((w) => (items.find((i) => i.id === w.id)?.objects ?? []).some((o) => mine.has(o)));
}

/** The fold's words after its count. Some of its rows wait for another task, so say how the number splits and match the strip's words. */
export function onlyYouWords(rows: PlanItem[]): string {
  const n = rows.length;
  const waiting = rows.filter((r) => stateWordOf(r) === 'waiting').length;
  return `${n === 1 ? 'task needs' : 'tasks need'} your choice` + (waiting > 0 && waiting < n ? ` (${n - waiting} ready, ${waiting} waiting)` : '');
}

/** The dot beside a state word. Needs you is the person's blue; done is good; the rest are plain rings. */
export function stateTone(word: PlanStateWord): 'you' | 'good' | 'ring' | 'faint' | 'spin' {
  if (word === 'needs_you') return 'you';
  if (word === 'done') return 'good';
  if (word === 'on_it') return 'spin';
  if (word === 'waiting' || word === 'set_aside') return 'faint';
  return 'ring';
}

// ---- actions ------------------------------------------------------------------------------------------
//
// A row has three controls and no more (round 1, K5): the doer menu (who does it), at most one main button, and the
// dots. Every control is on a row once. Pause, Resume, Could not, Choose, Answer, Show the changes and the Queue group
// are gone from the page; their engine verbs stay for the agent door.

/** U3's GIVE_LABEL (sendtask.tsx), the Send buttons' words. Spelled here rather than imported because sendtask.tsx
 *  reaches the bridge and this module stays pure; test/plan-heart.test.mjs holds the two strings equal. */
export const GIVE_TO_ASSISTANT = 'Give to your assistant';

export type RowActionId = 'approve' | 'done' | 'putback' | 'open' | 'save' | 'up' | 'repeat' | 'draft' | 'aside' | 'remove';
export interface RowAction { id: RowActionId; label: string; primary?: boolean; disabled?: boolean; title?: string;
  /** Said on the right of a faded menu item: why it cannot be used. */
  why?: string }

export const ACTION_WORD: Record<RowActionId, string> = {
  approve: 'Approve', done: 'Mark done', putback: 'Put back', open: 'Open', save: 'Save',
  up: 'Move up', repeat: 'Repeat…', draft: 'Draft steps', aside: 'Set aside', remove: 'Remove',
};

/** The Plan's "Save the changes you have made" row (engine ship/unsaved). Its button SAVES, the way Ctrl+S does; it
 *  used to be Choose, which opened History and saved nothing (UAT plan statement check, 28 Sep). */
export function savesOnRow(item: PlanItem): boolean {
  return item.id === 'ship/unsaved';
}

/** Remove (Kane's UAT, 25 Sep: "no way to delete a task once you add it"): a row that was ASKED for, a task or a draft,
 *  can be taken off the Plan, with Undo. A row Semanticus found comes back while the gap is there, so Set aside stays
 *  its way off; a draft's part goes with its draft, never alone; a draft that is running is left to finish. The
 *  engine's removePlanRow (person-only) refuses the same rows. */
export function removable(item: PlanItem): boolean {
  if (isDraftItem(item)) return item.status !== 'running';
  return isTask(item) && item.originLink?.kind !== 'draft';
}

/** The toast after a removal: what went, and the one way back. */
export const REMOVED_WORD = {
  undo: 'Undo',
  removed: (title: string) => `Removed "${title}" from the Plan.`,
  told: 'Your assistant was on it and reads this on its next call.',
};

/** A composer draft's row (Plan-heart contract 1.3, "draft/<dr id>"). Its controls live in the draft itself, drawn
 *  under the row (lane U5), so the row offers one thing: Open, the primary when the draft waits on the person. */
export function isDraftItem(item: PlanItem): boolean { return item.id.startsWith('draft/'); }

/** Draft the steps shows on an open task typed in your words (Plan-heart contract 14), not one taken or done. */
export function offersDraftSteps(item: PlanItem): boolean {
  return isTask(item) && sourceOf(item) === 'words' && isOpenRow(item) && stateWordOf(item) !== 'on_it';
}

/** Held by the person? Taken by the person? */
const heldByYou = (item: PlanItem) => item.claim?.claimedBy === 'you';

/** Move up: only on an open task for the assistant (contract 4, Kane; the engine refuses it anywhere else). */
export function showsUp(item: PlanItem): boolean {
  const w = stateWordOf(item);
  return isTask(item) && doerOf(item) === 'assistant' && isOpenRow(item) && w !== 'on_it';
}

/** Whether Move up can move this row: the first task in the assistant's order has nowhere to go. */
export function upDisabled(item: PlanItem, items: PlanItem[]): boolean {
  const mine = items.filter(showsUp).map((i) => i.order ?? Number.MAX_SAFE_INTEGER);
  return mine.length === 0 || (item.order ?? Number.MAX_SAFE_INTEGER) <= Math.min(...mine);
}

/** A change set waiting on one decision: the row's Approve (contract 1.4, "Ready with an Approve action"). */
export function approvable(item: PlanItem): boolean {
  const set = item.changeSet;
  return !!set && set.state === 'proposed' && !!set.digest && doerOf(item) === 'semanticus'
    && stateWordOf(item) === 'ready';
}

/** Keep all and Drop all on a change list (Kane's standing rule: one click stays one click). */
export const KEEP_ALL = 'Keep all';
export const DROP_ALL = 'Drop all';

/** The handles one Keep all (kept true) or Drop all (kept false) press decides: every line this set can decide
 *  whose decision would change, in the set's order. A line already so decided is left alone, so a press sends
 *  only real changes, and a set that is not the live proposal decides nothing. */
export function bulkRefs(set: PlanChangeSet | null | undefined, kept: boolean): string[] {
  return changeLines(set).filter((line) => canDecide(set, line) && line.kept !== kept).map(lineRef);
}

/** Why Approve waits on a person's row: no line is kept yet (the cleanup ticks the lines it suggests, so this shows only when none start ticked). */
export const KEEP_ONE_FIRST = 'Tick the changes you want, then approve them.';

/** A derived row whose lines Semanticus works out on the row itself (modelPlanAction propose). The cleanup: its
 *  lines are the unused objects, each a delete the person keeps or drops. Opening the row works them out. */
export function worksOutOnRow(item: PlanItem): boolean {
  return item.id.startsWith('cleanup/');
}

/** The row's one main button, or none. Only a row that needs the person gets one:
 *   Approve   a change list waits on the person (on free also a Ready Semanticus row with a worked-out set, since
 *             Run the plan is Pro),
 *   Mark done the person holds a task,
 *   Save      the Save row,
 *   Open      a draft,
 *   Put back  a set-aside row. */
export function mainAction(item: PlanItem, free = false): RowAction | null {
  const w = stateWordOf(item);
  if (isDraftItem(item)) {
    // Applied, checking still has work in the draft (Try again on a step after the change), so it is offered too.
    if ((isOpenRow(item) || w === 'checking') && w !== 'on_it') return { id: 'open', label: ACTION_WORD.open, primary: w === 'needs_you' || w === 'ready' };
    return null;
  }
  if (w === 'on_it') return heldByYou(item) ? { id: 'done', label: ACTION_WORD.done, primary: true } : null;
  if (!isOpenRow(item)) return w === 'set_aside' ? { id: 'putback', label: ACTION_WORD.putback } : null;
  if (item.id.startsWith('run/')) return null;
  const set = item.changeSet;
  if (!isTask(item) && set && set.state === 'proposed' && set.digest) {
    const lines = changeLines(set).length > 0;
    if ((w === 'needs_you' && lines) || (free && approvable(item))) {
      // Never a button that cannot be pressed (Kane, 13 Oct 2026: "Needs you" beside a greyed Approve). With nothing kept the press is Open,
      // where the changes are ticked; Approve comes back once one is kept.
      if (lines && keptCount(set) === 0) return { id: 'open', label: ACTION_WORD.open, primary: true, title: KEEP_ONE_FIRST };
      return { id: 'approve', label: ACTION_WORD.approve, primary: true };
    }
  }
  // The save row is the assistant's since 13 Oct (one call for it), and the person's one press stays on the row whoever does it.
  if (!isTask(item) && savesOnRow(item) && (w === 'needs_you' || w === 'ready')) return { id: 'save', label: ACTION_WORD.save, primary: true };
  if (isTask(item) && (doerOf(item) === 'you' || item.doneWhen === 'you_mark')) return { id: 'done', label: ACTION_WORD.done, primary: true };
  return null;
}

/** Said beside a Move up that cannot move: the task is already first. */
export const UP_FIRST_WHY = 'Already first';

/** What the dots menu holds for this row: the rest, each once, and only what fits the row. */
export function rowMenuActions(item: PlanItem, ctx: { items?: PlanItem[]; grant?: 'granted' | 'denied' | 'unknown'; canRepeat?: boolean } = {}): RowAction[] {
  if (isDraftItem(item)) return removable(item) ? [{ id: 'remove', label: ACTION_WORD.remove }] : [];
  if (item.id.startsWith('run/')) return [];
  if (!isOpenRow(item)) return removable(item) ? [{ id: 'remove', label: ACTION_WORD.remove }] : [];
  const out: RowAction[] = [];
  if (showsUp(item)) {
    // A disabled item says why on its right (hero19 D21): the first task in the order has nowhere to go.
    const first = upDisabled(item, ctx.items ?? [item]);
    out.push(first ? { id: 'up', label: ACTION_WORD.up, disabled: true, why: UP_FIRST_WHY } : { id: 'up', label: ACTION_WORD.up, disabled: false });
  }
  if (ctx.canRepeat) out.push({ id: 'repeat', label: ACTION_WORD.repeat });
  if (offersDraftSteps(item) && ctx.grant && ctx.grant !== 'unknown') {
    out.push(ctx.grant === 'granted' ? { id: 'draft', label: ACTION_WORD.draft } : { id: 'draft', label: ACTION_WORD.draft, why: 'Pro' });
  }
  out.push({ id: 'aside', label: ACTION_WORD.aside });
  if (removable(item)) out.push({ id: 'remove', label: ACTION_WORD.remove });
  return out;
}

/** "Give to your assistant" on a found or Jev row: the engine makes a task linked to it (setPlanItemDoer on the
 *  row, PlanTasks.cs GiveRowToAssistantAsync), and that task is what the assistant is offered. It is offered even
 *  when the row's own doer is already the assistant (Describe), because the queue offers tasks only. */
export function givesAsTask(item: PlanItem): boolean {
  return !isTask(item) && !isDraftItem(item) && !item.id.startsWith('run/') && isOpenRow(item)
    && stateWordOf(item) !== 'on_it' && doerOf(item) !== 'semanticus';
}

/** The row's "What it changed" line: how many edits History has filed under it. */
export function eventsLine(count: number): string {
  return count === 1 ? '1 change, filed in History' : `${count} changes, filed in History`;
}

export interface DoerChoice { doer: PlanDoer; label: string; disabled?: boolean; why?: string }

/** The doer menu on the row (round 1, K5): Semanticus, Your assistant, Me. A choice that cannot work is faded with its
 *  reason. Semanticus runs only where it needs no words and has a change it can apply (the engine refuses it on any
 *  task: PlanTasks.cs, DoerSemanticusRefusal). Null where the doer is not the person's to change: a draft, a run, a
 *  row that is finished or set aside, and a row the assistant is working on. */
export function doerMenu(item: PlanItem): DoerChoice[] | null {
  if (isDraftItem(item) || item.id.startsWith('run/') || !isOpenRow(item) || stateWordOf(item) === 'on_it') return null;
  const words = item.doneWhen === 'has_words' || item.doneWhen === 'assistant_says';
  const canRun = !isTask(item) && !words && (doerOf(item) === 'semanticus' || !!item.changeSet?.digest);
  return [
    { doer: 'semanticus', label: 'Semanticus', disabled: !canRun, why: canRun ? undefined : words ? 'Needs words' : 'Semanticus cannot apply this one by itself' },
    { doer: 'assistant', label: 'Your assistant' },
    { doer: 'you', label: 'Me' },
  ];
}

/** The one engine call a doer choice makes, or null when the choice changes nothing. Me takes an assistant task as
 *  the person's own (claimPlanTask); on any other row Me sets the doer. */
export function doerCall(item: PlanItem, choice: PlanDoer): [string, ...unknown[]] | null {
  const now = doerOf(item);
  if (choice === 'you') {
    if (now === 'you') return null;
    return isTask(item) && now === 'assistant' ? ['claimPlanTask', item.id] : ['setPlanItemDoer', item.id, 'you'];
  }
  if (choice === 'assistant') {
    // A found row already the assistant's is still given as a task, because the queue offers tasks only.
    if (now === 'assistant' && !givesAsTask(item)) return null;
    return ['setPlanItemDoer', item.id, 'assistant'];
  }
  return now === 'semanticus' ? null : ['setPlanItemDoer', item.id, 'semanticus'];
}

// ---- the list -----------------------------------------------------------------------------------------

/** Next shows at least this many rows, then Show all (U1 lane: "Needs you then Next, 7 rows then Show all"). Since the
 *  space rule (27 September 2026) the page passes `cap`, the rows that fit the window, so a tall screen is not left
 *  empty under a list that stopped at seven; this stays the floor. */
export const NEXT_CAP = 7;

export type PlanGoal = 'all' | 'ai' | 'quality' | 'aims';
/** The filters (round 1, Q4): All, AI ready, Quality. A row the person asked for shows under All. */
export const GOAL_ORDER: PlanGoal[] = ['all', 'ai', 'quality'];
export const GOAL_LABEL: Record<PlanGoal, string> = { all: 'All', ai: 'AI ready', quality: 'Quality', aims: 'Your aims' };
const AI_PAGES = new Set(['readiness', 'instructions', 'knowledge', 'dataagent']);

/** Which goal a row serves. The engine says (`goal`: where the row's findings came from). An engine that sends none is
 *  read by the old rule, so the filter keeps working beside an older engine. */
export function goalOf(item: PlanItem): Exclude<PlanGoal, 'all'> {
  if (item.goal === 'ai' || item.goal === 'quality' || item.goal === 'aims') return item.goal;
  const source = sourceOf(item);
  if (source === 'words' || source === 'you' || source === 'repeat') return 'aims';
  if (item.jump?.tool && AI_PAGES.has(item.jump.tool)) return 'ai';
  if (item.section === 'Meaning') return 'ai';
  return 'quality';
}

/** Every filter a row shows under: the engine's list (its goal first), else the one goal. */
export function goalsOf(item: PlanItem): Exclude<PlanGoal, 'all'>[] {
  const own = goalOf(item);
  const sent = (item.goals ?? []).filter((g, i, all) => (g === 'ai' || g === 'quality' || g === 'aims') && all.indexOf(g) === i);
  return sent.length > 0 && sent[0] === own ? sent : [own];
}

export interface WorkView {
  /** Set while a track or an object narrows the list: every row of it, no cap, no fold. */
  filtered: boolean;
  rows: PlanItem[];
  /** Rows that turned done while the page was open (a run, or the assistant): kept at the top until the next press or a
   *  reload. The rest of the done rows stay in History. */
  justDone: PlanItem[];
  /** Semanticus rows, then the assistant's, in the engine's order, capped until Show all. */
  next: PlanItem[];
  /** How many rows Next holds in all, shown or not. */
  nextTotal: number;
  /** The rows only a person can do: the closed "Only you can do N" fold, in the engine's order. */
  onlyYou: PlanItem[];
  /** A track's Done and Applied, checking rows, the ones its strip segment counts (contract 9), listed under its
   *  open rows so a Done row can be opened with what it changed (contract 1.6). Empty unless a track is chosen. */
  done: PlanItem[];
}

/** The row a person has open, and its place in its group when they opened it. While it stays open it keeps that
 *  place, even when its state moves it out (journey m11 F8: answering a draft's question made it Ready, and it left
 *  for the end of Next, past the cap, from under the person working on it). Closing it lets it move. */
export interface KeptRow { id: string; index: number; group?: 'auto' | 'you' }

/** Which group an open row is listed in: the person's fold, or the automated rows. */
export const groupOf = (item: PlanItem): 'auto' | 'you' => (doerOf(item) === 'you' ? 'you' : 'auto');

/** Semanticus rows, then the assistant's, then (as a fold) the rows only the person can do. A track or an object
 *  filter shows that slice whole. Done, set aside and applied-checking rows are History's, not the list's, except the
 *  rows that turned done while the page was open (`justDone`). */
export function workView(items: PlanItem[], opts: { track?: PlanDoer | null; object?: string | null; goal?: PlanGoal; showAll?: boolean; cap?: number; keep?: KeptRow | null; justDone?: ReadonlySet<string> | null } = {}): WorkView {
  const goal = opts.goal ?? 'all';
  // A draft whose step after the change did not run reads Applied, checking, and still has the person's Try again in
  // it (lane U5), so it stays on the list where an applied row would leave it.
  const open = items.filter((i) => isOpenRow(i) || (isDraftItem(i) && stateWordOf(i) === 'checking'))
    .filter((i) => goal === 'all' || goalsOf(i).includes(goal));
  if (opts.track || opts.object) {
    const rows = open.filter((i) => (!opts.track || doerOf(i) === opts.track)
      && (!opts.object || (i.objects ?? []).some((o) => touchesObject(o, opts.object!))));
    // The assistant's track reads in the order it takes them (mockup section 5).
    if (opts.track === 'assistant') rows.sort((a, b) => (a.order ?? 1e9) - (b.order ?? 1e9));
    // The track's finished rows (the strip's done segments). The contract names no Done list of its own; the
    // track filter is where the strip already sends the person (lane m11/plan-gaps, logged, reversible).
    const opened = new Set(rows.map((r) => r.id));
    const done = opts.track ? items.filter((i) => doerOf(i) === opts.track && !opened.has(i.id)
      && (stateWordOf(i) === 'done' || stateWordOf(i) === 'checking')
      && (!opts.object || (i.objects ?? []).some((o) => touchesObject(o, opts.object!)))) : [];
    return { filtered: true, rows, justDone: [], next: [], nextTotal: 0, onlyYou: [], done };
  }
  const justDone = opts.justDone && opts.justDone.size > 0
    ? items.filter((i) => opts.justDone!.has(i.id) && !isOpenRow(i) && (stateWordOf(i) === 'done' || stateWordOf(i) === 'checking')
      && (goal === 'all' || goalsOf(i).includes(goal)))
    : [];
  // Semanticus first, then the assistant, each in the engine's order (the engine sorts a person's row after an automated one on a tie).
  const auto = [...open.filter((i) => groupOf(i) === 'auto' && doerOf(i) === 'semanticus'), ...open.filter((i) => doerOf(i) === 'assistant')];
  const onlyYou = open.filter((i) => groupOf(i) === 'you');
  // The open row keeps the place it had in its group.
  const kept = opts.keep ? [...auto, ...onlyYou].find((i) => i.id === opts.keep!.id) : undefined;
  if (kept) {
    const home = opts.keep!.group ?? groupOf(kept);
    const from = groupOf(kept) === 'you' ? onlyYou : auto;
    from.splice(from.indexOf(kept), 1);
    const into = home === 'you' ? onlyYou : auto;
    into.splice(Math.max(0, Math.min(opts.keep!.index, into.length)), 0, kept);
  }
  return { filtered: false, rows: [], justDone, next: opts.showAll ? auto : auto.slice(0, Math.max(NEXT_CAP, opts.cap ?? 0)), nextTotal: auto.length, onlyYou, done: [] };
}

const sameRef = (a: string, b: string) => a.toLowerCase() === b.toLowerCase();

/** Whether a row's object is the filter's object or inside it. The engine names the column or measure a row touches
 *  ("column:Sales/CustomerKey"), not its table, so a table filter ("table:Sales") takes every "<kind>:Sales/..." ref.
 *  Matching only "table:Sales/..." showed a table with nothing open while it had open rows (F-192's defect). */
function touchesObject(ref: string, filter: string): boolean {
  const r = ref.toLowerCase(), f = filter.toLowerCase();
  if (r === f || r.startsWith(`${f}/`)) return true;
  if (!f.startsWith('table:')) return false;
  const colon = r.indexOf(':');
  return colon > 0 && r.slice(colon + 1).startsWith(`${f.slice('table:'.length)}/`);
}

/** A composer draft's row (DraftRows.Prefix in DraftRunner.cs). */
export const isDraftRow = (item: PlanItem): boolean => item.id.startsWith('draft/');

/** What the engine takes in ONE call: every Ready Semanticus row that is not held and is not a draft (contract 7.2). The
 *  engine recomputes this set under its gate and refuses if the digest moved. A draft runs on its own, never inside the
 *  batch (composer contract 3.2); a step-by-step draft makes one undo step per run of changes (author-gaps B9). */
export function runSet(items: PlanItem[]): PlanItem[] {
  return items.filter((i) => doerOf(i) === 'semanticus' && stateWordOf(i) === 'ready' && !i.held && !isDraftRow(i));
}

/** What ONE press runs (round 1, K1): the Ready Semanticus rows, plus every Semanticus row whose waits are all in that
 *  set, again and again until nothing more joins. The page makes one call per wave until the set is run, so the button's
 *  count is the whole press and "Run the plan 3" is three rows, not one. A row held, or a draft, never joins. */
export function pressSet(items: PlanItem[]): PlanItem[] {
  const take = new Set(runSet(items).map((i) => i.id));
  const waiting = items.filter((i) => doerOf(i) === 'semanticus' && stateWordOf(i) === 'waiting' && !i.held && !isDraftRow(i)
    && (i.waitsFor?.length ?? 0) > 0);
  let grew = true;
  while (grew) {
    grew = false;
    for (const i of waiting) {
      if (!take.has(i.id) && i.waitsFor!.every((w) => take.has(w.id))) { take.add(i.id); grew = true; }
    }
  }
  return items.filter((i) => take.has(i.id));
}

/** What the run area says (design section 2): never a grey button with a hidden reason. */
export type RunAreaState =
  | { kind: 'run'; count: number }
  | { kind: 'assistant'; count: number }
  | { kind: 'you' }
  | { kind: 'done' }
  | { kind: 'none' };

export function runAreaOf(items: PlanItem[]): RunAreaState {
  const count = pressSet(items).length;
  if (count > 0) return { kind: 'run', count };
  const open = items.filter(isOpenRow);
  const mine = open.filter((i) => doerOf(i) === 'assistant');
  if (mine.length > 0) return { kind: 'assistant', count: mine.length };
  if (open.some((i) => doerOf(i) === 'you')) return { kind: 'you' };
  return open.length === 0 ? { kind: 'done' } : { kind: 'none' };
}

/** The open plan row that already carries a readiness finding, or null (round 1, K4). The row carries it when its `rules`
 *  hold the finding's rule id and, when the row lists objects, its `objects` hold the finding's object. Matched without
 *  regard to case, as the engine's own refs are. The Readiness page says "On the plan" for such a finding and offers no
 *  second copy of it. */
export function rowCoveringFinding(items: ReadonlyArray<{ id: string; rules?: string[]; objects?: string[]; stateWord?: string | null; status?: string }> | null | undefined,
  ruleId: string, objectRef: string): string | null {
  const rule = ruleId.toLowerCase();
  const object = objectRef.toLowerCase();
  for (const row of items ?? []) {
    if (row.stateWord === 'done' || row.stateWord === 'set_aside' || row.status === 'done' || row.status === 'skipped') continue;
    if (!row.rules?.some((r) => r.toLowerCase() === rule)) continue;
    if (!row.objects || row.objects.length === 0 || row.objects.some((o) => o.toLowerCase() === object)) return row.id;
  }
  return null;
}

/** What the person tells their assistant to start on the plan (the Tell your assistant button copies it). */
export const TELL_ASSISTANT = 'Work through my Semanticus plan.';

/** What a Semanticus row's step looks like while a run works on it. 'queued' is a row the press holds that the run has not reached. */
export type RunStep = 'queued' | 'work' | 'waitjev' | 'apply' | 'check' | 'done' | 'skipped';
export const RUN_STEP_WORD: Record<RunStep, string> = {
  queued: 'In this run', work: 'Working out', waitjev: 'Waiting for Jev', apply: 'Applying', check: 'Checking', done: 'Done', skipped: 'Skipped',
};

/** One row's step: the word and, where the engine said it, the count or the reason. `ms` is when the step began, in ms since the press started. */
export interface RunPhase { step: RunStep; changes?: number; reason?: string; ms?: number; waited?: number }

/** plan/runProgress (the wire contract). `itemIds` is every row the step is about: the one batch check names the rows of the wave.
 *  `ms` is milliseconds since the press started, at the moment the step began (the engine's one stopwatch for the press). */
export interface RunProgressEvent { runId: string; itemIds: string[]; phase: 'work' | 'waitjev' | 'apply' | 'check' | 'done' | 'skipped'; wave: number; changes?: number; reason?: string; ms: number; waitedSeconds?: number; choosing?: string }

const asEvent = (v: unknown): RunProgressEvent | null => {
  const e = v as Partial<RunProgressEvent> | null;
  return e && Array.isArray(e.itemIds) && typeof e.phase === 'string' ? e as RunProgressEvent : null;
};

/** Folds one event into the per-row steps. A step that names several rows (the batch) gives no count to any one of them,
 *  because a batch total on every row would be a wrong number; a single-row step and every done carry their own count. */
export function foldProgress(steps: Readonly<Record<string, RunPhase>>, raw: unknown): Record<string, RunPhase> {
  const e = asEvent(raw);
  if (!e) return steps as Record<string, RunPhase>;
  const next = { ...steps };
  for (const id of e.itemIds) {
    const own = e.itemIds.length === 1 || e.phase === 'done';
    next[id] = {
      step: e.phase,
      ...(own && typeof e.changes === 'number' ? { changes: e.changes } : {}),
      ...(e.reason ? { reason: e.reason } : {}),
      ...(typeof e.ms === 'number' ? { ms: e.ms } : {}),
      ...(typeof e.waitedSeconds === 'number' ? { waited: e.waitedSeconds } : {}),
    };
  }
  return next;
}

/** What Jev is being asked right now, said by the engine while the press waits for it ("Jev is choosing how 48 columns add up and what Copilot
 *  sees in 396 fields"). A waitjev event says it (or says nothing is out: null); any other step of the press ends it. Undefined for anything else.
 *  It belongs to the press, not to a row, because a wait can name no row at all (the rows Jev looks at again may be none). */
export function choosingOf(raw: unknown): string | null | undefined {
  const e = asEvent(raw);
  if (!e) return undefined;
  if (e.phase === 'waitjev') return typeof e.choosing === 'string' && e.choosing.trim() ? e.choosing.trim() : null;
  return null;
}

/** "0:08": seconds as minutes and seconds. */
export const clockWords = (seconds: number): string => `${Math.floor(Math.max(0, seconds) / 60)}:${String(Math.max(0, seconds) % 60).padStart(2, '0')}`;

/** The state column's word for a step. While the press waits for Jev it counts: "Waiting for Jev, 0:08". */
export function stepWords(step: RunPhase): string {
  return step.step === 'waitjev' && typeof step.waited === 'number' ? `${RUN_STEP_WORD.waitjev}, ${clockWords(step.waited)}` : RUN_STEP_WORD[step.step];
}

/** "Applying 33 changes", "Done, 4 changes", "In this run": the state column's words for a step. Count first, as the engine counts. */
export function stepNote(step: RunPhase): string | null {
  if (step.step === 'skipped') return step.reason ?? null;
  if (typeof step.changes === 'number' && (step.step === 'apply' || step.step === 'done')) return step.changes === 1 ? '1 change' : `${step.changes} changes`;
  return null;
}

/** The step shown for a row: the engine's step when it sent one, "In this run" while the press holds the row and the run has not reached it. */
export function stepOf(id: string, steps: Readonly<Record<string, RunPhase>>, holding: ReadonlySet<string> | null): RunPhase | null {
  return steps[id] ?? (holding && holding.has(id) ? { step: 'queued' } : null);
}

/** "Running 2 of 3": the row now being worked on, of the rows the press holds. */
export function runningWords(held: readonly string[], steps: Readonly<Record<string, RunPhase>>): { text: string; fraction: number } {
  const total = held.length;
  const finished = held.filter((id) => steps[id]?.step === 'done' || steps[id]?.step === 'skipped').length;
  return { text: `Running ${Math.min(finished + 1, Math.max(total, 1))} of ${total}`, fraction: total === 0 ? 0 : (finished + 0.5) / total };
}

/** The receipt, from what the waves of a press did. "Ran 3 rows." and "38 changes, one undo step." */
export interface PressReceipt {
  kind: 'ran';
  rows: number;
  changes: number;
  /** The rows the waves really ran, so "Jev chose N." counts what was written and not what the press held. */
  ranIds?: string[];
  /** The History entry of the FIRST wave: undo(entryId) walks back to and including it, so it takes back the whole press. */
  entryId: string | null;
  /** The model revision the press ended on: an Undo is offered only while the model has not moved on since. */
  revision: number | null;
  /** What the last wave said when it did not run (a refusal). */
  note: string | null;
  /** The engine says the receipt may offer Save: the model has unsaved changes and is not a live one (ApplyPlanItemsResult.canSave). Absent on an older engine. */
  canSave?: boolean;
}
/** The line beside "Done just now": the list holds what finished while the page was open, so it says what the other counts on the page
 *  include that it does not (hero16 N8, H7). A task whose problem went away on its own is listed but was not run; a task finished
 *  before this visit is counted in the strip and in Refresh plan, but is not listed. */
export function justDoneNote(justDone: PlanItem[], plan: Plan): string {
  const parts = ['Open one to see what it changed.'];
  const closed = justDone.filter(closedByOther).length;
  if (closed > 0) parts.push(`${closed} ${closed === 1 ? 'was' : 'were'} no longer needed, so ${closed === 1 ? 'it was' : 'they were'} not run.`);
  const all = (plan.strip?.tracks ?? []).reduce((n, t) => n + Math.max(0, t.done), 0);
  const earlier = all - justDone.length;
  if (earlier > 0) parts.push(`${earlier} more ${earlier === 1 ? 'task' : 'tasks'} finished earlier.`);
  return parts.join(' ');
}
export const ranWords = (rows: number): string => `Ran ${rows} ${rows === 1 ? 'task' : 'tasks'}.`;
export const changesWords = (changes: number): string => `${changes} ${changes === 1 ? 'change' : 'changes'}, one undo step.`;

/** The result of one applyPlanItems call, as far as the loop reads it. */
export interface WaveResult { performed?: boolean; applied?: number; itemIds?: string[]; sentence?: string; note?: string; revision?: number; entryId?: string; canSave?: boolean; plan?: Plan }

/** The most waves one press makes. Three rows in a chain need three; five is a ceiling, never a goal. */
export const MAX_WAVES = 5;

/** One press: call, then call again with the new readyDigest while the plan still has rows Semanticus can run. (The engine
 *  now runs every wave inside one call, so the plan it answers with has none left and this makes one call; the loop is the
 *  safety net for an engine that runs one wave a call.) Stops at
 *  MAX_WAVES, on a refusal, and when a wave ran nothing. The first wave's entryId is kept for Undo. `apply` makes the
 *  engine call; `reread` reads the plan when a wave answered without one. */
export async function runAllWaves(start: Plan, apply: (call: NonNullable<ReturnType<typeof runCall>>) => Promise<WaveResult | null | undefined>,
  reread: () => Promise<Plan>, onPlan?: (plan: Plan) => void): Promise<{ receipt: PressReceipt | null; refusal: string | null; plan: Plan }> {
  let plan = start;
  const receipt: PressReceipt = { kind: 'ran', rows: 0, changes: 0, entryId: null, revision: null, note: null };
  let refusal: string | null = null;
  for (let wave = 0; wave < MAX_WAVES; wave++) {
    const call = runCall(plan);
    if (!call) break;
    let result: WaveResult | null | undefined;
    try { result = await apply(call); }
    catch (failure: unknown) {
      // A wave that threw after an earlier one wrote: the model HAS changed, so the receipt says what was done and why it stopped.
      if (receipt.rows === 0) throw failure;
      refusal = failure instanceof Error ? failure.message : String(failure);
      break;
    }
    if (!result?.performed) {
      plan = result?.plan ?? await reread();
      onPlan?.(plan);
      refusal = result?.sentence ?? null;
      break;
    }
    receipt.rows += result.itemIds?.length ?? 0;
    (receipt.ranIds ??= []).push(...(result.itemIds ?? []));
    receipt.changes += result.applied ?? 0;
    receipt.entryId ??= result.entryId ?? null;
    if (typeof result.revision === 'number') receipt.revision = result.revision;
    if (typeof result.canSave === 'boolean') receipt.canSave = result.canSave;
    if (result.note) receipt.note = result.note;
    plan = result.plan ?? await reread();
    onPlan?.(plan);
    if ((result.itemIds?.length ?? 0) === 0) break;
  }
  if (receipt.rows === 0) return { receipt: null, refusal, plan };
  receipt.note = laterWaveNote(refusal) ?? receipt.note;
  return { receipt, refusal: null, plan };
}

/** What a wave that ran nothing said, as a note under a receipt that DID change the model. "Nothing was changed" is true of that wave and false of the
 *  press, and the line beside "16 changes" must not say both (Kane, 8 Oct, hand-on finding). Anything else the engine said stands as it was. */
export function laterWaveNote(said: string | null): string | null {
  if (!said) return null;
  const rest = said.replace(/^Nothing was changed\.\s*/u, '').replace(/^Nothing was changed, because\s*/u, 'It stopped because ').trim();
  return rest.length > 0 ? rest : null;
}

/** The one call Run the plan makes: no items, the view's readyDigest last (EngineRpcTarget.applyPlanItems). */
export function runCall(plan: Plan): [string, unknown[], string, string] | null {
  if (!plan.readyDigest || runSet(plan.items).length === 0) return null;
  return ['applyPlanItems', [], 'human', plan.readyDigest];
}

/** What a Ready draft's row says where its buttons start: it is Semanticus's to run, but on its own, so a person
 *  reading a Ready row that Run the plan leaves out is told why. Null on every other row. */
export function draftOwnState(item: PlanItem): { text: string; title: string } | null {
  if (!isDraftRow(item) || doerOf(item) !== 'semanticus' || stateWordOf(item) !== 'ready' || item.held) return null;
  return {
    text: 'Runs on its own, not with Run the plan',
    title: 'A draft is not part of Run the plan. It runs on its own, and the draft says how many undo steps it makes.',
  };
}

// ---- the strip ----------------------------------------------------------------------------------------

export type StripSeg = 'done' | 'on_it' | 'needs_you' | 'ready' | 'waiting';
/** One segment per row, up to this many per track, then a count (contract 9). */
export const STRIP_CAP = 12;

export function stripSegments(track: PlanStripTrack, cap = STRIP_CAP): { segs: StripSeg[]; more: number } {
  const all: StripSeg[] = [
    ...Array<StripSeg>(Math.max(0, track.done)).fill('done'),
    ...Array<StripSeg>(Math.max(0, track.onIt)).fill('on_it'),
    ...Array<StripSeg>(Math.max(0, track.needsYou)).fill('needs_you'),
    ...Array<StripSeg>(Math.max(0, track.ready)).fill('ready'),
    ...Array<StripSeg>(Math.max(0, track.waiting)).fill('waiting'),
  ];
  return { segs: all.slice(0, cap), more: Math.max(0, all.length - cap) };
}

/** The three counts at the end of a track: done, on it, and what is open, said as "need you" when all of it does. */
export function trackCounts(track: PlanStripTrack): { key: string; text: string; you?: boolean }[] {
  const open = track.ready + track.needsYou;
  const needs = track.needsYou > 0 && track.needsYou === open;
  return [
    { key: 'done', text: `${track.done} done` },
    { key: 'onit', text: `${track.onIt} on it` },
    needs ? { key: 'open', text: `${track.needsYou} need you`, you: true }
      : { key: 'open', text: `${track.ready} ready${track.doer === 'semanticus' ? ' first' : ''}` },
    ...(!needs && track.needsYou > 0 ? [{ key: 'needs', text: `${track.needsYou} need you`, you: true }] : []),
    // Beside "Run the plan 13", "1 ready first" and "12 follow it" add up to the button: the rows that wait for a row in the same run.
    ...(track.waiting > 0 ? [{ key: 'waiting', text: track.doer === 'semanticus' ? `${track.waiting} ${track.waiting === 1 ? 'follows' : 'follow'} it` : `${track.waiting} waiting` }] : []),
  ];
}

export function stripTracks(plan: Plan): PlanStripTrack[] {
  const tracks = plan.strip?.tracks ?? [];
  return DOER_ORDER.map((doer) => tracks.find((t) => t.doer === doer)
    ?? { doer, done: 0, onIt: 0, ready: 0, needsYou: 0, waiting: 0 });
}

/** "Grade B now to A when the plan is done". The after-grade only when the engine worked one out. */
export function gradeLine(strip: PlanStrip | null | undefined): { now: string; after: string | null } | null {
  if (!strip?.gradeNow) return null;
  return { now: strip.gradeNow, after: strip.gradeAfter && strip.gradeAfter !== strip.gradeNow ? strip.gradeAfter : null };
}

/** The Latest mark: the newest ledger entry, its time and its sentence. It opens History. */
export function latestLine(strip: PlanStrip | null | undefined): { time: string; sentence: string; entryId: string } | null {
  const l = strip?.latest;
  if (!l?.sentence) return null;
  const at = Date.parse(l.when);
  return { time: Number.isNaN(at) ? '' : clockOf(at), sentence: editWords(l.sentence), entryId: l.entryId };
}

/** Said while the engine reports Jev is still looking at this revision (ModelPlanView.JevPending). */
export const JEV_LOOKING_LINE = 'Jev is still looking at this model. What it finds shows up here.';

/** The order the plan is in, in the engine's own words (ModelPlanView.OrderedBy), and one line while Jev is still
 *  looking. Both were on the wire and never drawn, so a plan in rule order looked the same as a ranked one
 *  (honesty lane). The sentence is drawn as the engine wrote it: the engine decides what is true. */
const NO_SERVICE_ORDER = /no suggestion service is set up/i;

export function orderLines(plan: Plan): string[] {
  const lines: string[] = [];
  // Jev simply not being set up (Free, no key) is not news: the rows are in rule order and the page says nothing (Kane, 8 Oct).
  if (plan.orderedBy?.trim() && !NO_SERVICE_ORDER.test(plan.orderedBy)) lines.push(plan.orderedBy.trim());
  if (plan.jevPending) lines.push(JEV_LOOKING_LINE);
  return lines;
}

/** "Jev updated this after your change at 10:46" when Jev ran (Pro), else the honest free line (contract 13). */
export function updatedLine(strip: PlanStrip | null | undefined): string | null {
  const u = strip?.updated;
  if (!u?.when) return null;
  const at = Date.parse(u.when);
  if (Number.isNaN(at)) return null;
  return u.by === 'jev' ? `Semanticus Pro updated the Plan at ${clockOf(at)}` : `Updated after your last change at ${clockOf(at)}`;
}

function clockOf(at: number): string {
  const d = new Date(at);
  return `${String(d.getHours()).padStart(2, '0')}:${String(d.getMinutes()).padStart(2, '0')}`;
}

/** The assistant chip's count (contract 11.4): the assistant track's Ready plus On it. */
export function assistantChipCount(plan: Plan | null | undefined): number {
  const t = plan?.strip?.tracks?.find((x) => x.doer === 'assistant');
  return t ? t.ready + t.onIt : 0;
}

// ---- the one field ------------------------------------------------------------------------------------

export const FIELD_PLACEHOLDER = 'Add a task';
export const FIELD_HINT = 'Enter adds it to the Plan. Shift+Enter starts a new line.';
/** How much of a task's words its title holds (PlanTasks.MaxWordsChars). Longer text is kept whole by the engine. */
export const FIELD_MAX = 280;
/** The field's hint once the text is longer than a title (UAT S12-1-005: nothing is cut, and it says so first). */
export const FIELD_LONG_HINT = 'Its start becomes the title. The whole text is kept with the task.';

/** What Enter does with the field: add a row, or nothing. It never navigates (U1 lane test). Nothing is cut: text
 *  longer than a title goes whole, and the engine makes its start the title and keeps every word. The person's line
 *  breaks are kept (N15): runs of spaces close up inside a line, and more than one empty line in a row is one. */
export function fieldSubmit(text: string): { words: string } | null {
  const words = text.split(/\r?\n/u).map((line) => line.replace(/[^\S\n]+/gu, ' ').trim()).join('\n')
    .replace(/\n{3,}/gu, '\n\n').trim();
  if (!words) return null;
  return { words };
}

/** Whether a key press in the field adds the task: Enter alone. Shift+Enter makes a new line, and a key pressed while
 *  an input method is still composing a character is that character, not an add. */
export function fieldAddsOn(key: { key: string; shiftKey?: boolean; isComposing?: boolean }): boolean {
  return key.key === 'Enter' && !key.shiftKey && !key.isComposing;
}

/** The hint beside the field: Enter adds it, or, for text longer than a title, what happens to it. */
export function fieldHint(text: string): string {
  return text.trim().length > FIELD_MAX ? FIELD_LONG_HINT : FIELD_HINT;
}

// ---- what the assistant sees (contract 6.1) --------------------------------------------------------------
// The queue panel shows the text the assistant reads on its next call. The engine puts it on the plan view
// (ModelPlanView.AssistantSees) and numbers each offered row (ModelPlanItem.OfferNumber), a draft's parts under their
// draft's one number, so the page shows the engine's own words and never works out a copy of its own.

/** The numbers the fold line gives the offered rows, by id, as the engine numbered them. */
export function offerNumbers(plan: Plan): Map<string, number> {
  const out = new Map<string, number>();
  for (const i of plan.items ?? []) if (typeof i.offerNumber === 'number') out.set(i.id, i.offerNumber);
  return out;
}
