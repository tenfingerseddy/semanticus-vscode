// History inside the Plan: the pure view logic (Plan-heart contract 8, mockup plan-heart.html section 4).
//
// Everything the History page decides lives here as a plain function, so test/plan-history.test.mjs runs the
// same code the page runs: the who filter's five choices, the words for who and for a verdict, the undo count
// sentence, where the two seams go, and which action a row offers. The rows themselves are the engine's answer.
//
// The engine decides every fact about undo: `undoable`, `laterCount`, `undone` and `redoable` come from
// EditLedger (contract 8.5) and are never recomputed here. The page only words them.

import { editWords, opLabel, readSafetyCheckOverride, SAFETY_CHECK_COPY } from './copy';

// ---- the wire (Semanticus.Engine/PlanHistory.cs, camelCase over the JSON-RPC door) -----------------------

export type Who = 'you' | 'assistant' | 'semanticus' | 'workflow';
export type WhoFilter = 'all' | Who;

export interface HistoryDelta { kind: string; ref: string; props?: string[] | null; before?: string | null; after?: string | null }
export interface HistoryCause { kind: string; ids?: string[] | null; title?: string | null; approvedBy?: string | null }
export interface RunReportCheck { kind: string; verdict: string; detail?: string | null }
export interface RunReportStep { stepId: string; title?: string | null; outcome?: string | null; submittedBy?: string | null; checks?: RunReportCheck[] | null }
export interface RunReport { runId: string; workflow?: string | null; title?: string | null; status?: string | null; steps?: RunReportStep[] | null; evidence?: string | null }

export interface HistoryRow {
  id: string;
  /** "edit" | "item" | "run" | "draft" | "task" | "decision" | "saved" | "restored" | "record". */
  kind: string;
  when: string;
  by?: string | null;
  title?: string | null;
  cause?: HistoryCause | null;
  deltas?: HistoryDelta[] | null;
  sessionId?: string | null;
  revision?: number;
  undoable?: boolean;
  laterCount?: number;
  redoable?: boolean;
  undone?: boolean;
  verified?: string | null;
  beforeThisSession?: boolean;
  /** An edit of an earlier session the file never held and nothing took back: the model open now lacks it (PlanHistoryRow.NotSaved). */
  notSaved?: boolean;
  beforeRestore?: boolean;
  detail?: string | null;
  report?: RunReport | null;
  children?: HistoryRow[] | null;
  /** The Verified Edits record a "record" row stands for (PlanHistoryRow.Record). */
  record?: VerifiedEditRecord;
}

export interface HistoryView { sessionId?: string; rows: HistoryRow[]; more?: boolean; seamSentence?: string; kept?: boolean }

// The Verified Edits record, as listVerifiedEdits sends it (VerifiedEditsChain / VerifiedEditRecord).
export interface VerifiedEditRecord {
  seq: number; when: string; sessionId: string; revision: number; origin: string; op: string; objectRef?: string;
  verdict: string; summary?: string; overrideReason?: string; evidence?: string; bodyHash: string; prevHash: string; hash: string;
}
export interface VerifiedEditsChain { records: VerifiedEditRecord[]; chainIntact: boolean; firstBrokenSeq: number; note?: string }

export interface HistoryCheckpoint { hash: string; shortHash: string; label: string; author?: string; when: string; sessionId?: string; revision?: number }
export interface HistoryCheckpointList { supported: boolean; checkpoints: HistoryCheckpoint[]; note?: string }

// ---- who ------------------------------------------------------------------------------------------------

/** The who filter: exactly these five (contract 8.2). The id is what getPlanHistory's `by` takes. */
export const WHO_FILTERS: ReadonlyArray<{ id: WhoFilter; label: string }> = [
  { id: 'all', label: 'Everyone' },
  { id: 'you', label: 'You' },
  { id: 'assistant', label: 'Your assistant' },
  { id: 'semanticus', label: 'Semanticus' },
  { id: 'workflow', label: 'Workflows' },
];

export const WHO_WORD: Record<Who, string> = { you: 'You', assistant: 'Your assistant', semanticus: 'Semanticus', workflow: 'Workflow' };

/** The `by` argument for getPlanHistory: null asks for everyone. */
export const whoParam = (filter: WhoFilter): string | null => (filter === 'all' ? null : filter);

export function whoOf(by: string | null | undefined): Who {
  return by === 'assistant' || by === 'semanticus' || by === 'workflow' ? by : 'you';
}

/** A Verified Edits record keeps the door it came through, not the new `By`. */
export function whoOfOrigin(origin: string | null | undefined): Who {
  return origin === 'agent' ? 'assistant' : origin === 'system' ? 'semanticus' : 'you';
}

/** The mark beside the word: the doer hues of colour level B. A workflow has no hue of its own. */
export function doerClass(who: Who): string {
  return who === 'assistant' ? 'sem-doer sem-doer-agent'
    : who === 'semanticus' ? 'sem-doer sem-doer-engine'
      : who === 'workflow' ? 'sem-doer ph-doer-workflow'
        : 'sem-doer sem-doer-you';
}

// ---- the Verified word ----------------------------------------------------------------------------------

export type Tone = 'good' | 'warn' | 'bad';

/** Thirteen engine verdicts become three words (mockup, Simplify). A neutral record ("batch", "info", "no answer
 *  yet") gets no word at all: silence is honest, it was not checked. */
export function verifiedWord(verdict: string | null | undefined): { word: string; tone: Tone } | null {
  switch (verdict) {
    case 'proven': case 'validated': case 'safe': case 'deployed': case 'attributed':
      return { word: 'Verified', tone: 'good' };
    case 'needs-review': case 'impact': case 'interval': case 'data-suspected':
      return { word: 'Needs review', tone: 'warn' };
    case 'overridden':
      return { word: 'Overridden', tone: 'bad' };
    // Page only: the record behind this word sits at or after a break in the verified record, so the word the
    // engine joined cannot be trusted and is not shown as Verified.
    case 'broken':
      return { word: 'Not trusted', tone: 'bad' };
    default:
      return null;
  }
}

// ---- undo and redo --------------------------------------------------------------------------------------

const plural = (n: number, one: string, many: string) => (n === 1 ? one : many);

/** The detail's sentence beside Undo to here. `later` is the engine's LaterCount. */
export function undoSentence(later: number): string {
  return later > 0 ? `Takes back this and ${later} later ${plural(later, 'change', 'changes')}.` : 'Takes back this change.';
}

/** The quiet tag after Undo to here on a row. */
export const laterTag = (later: number): string => (later > 0 ? `+${later} later` : '');

export const REDO_SENTENCE = 'Brings this change back.';
export const SESSION_SEAM = { title: 'From an earlier visit', text: 'Undo cannot take these back. Restore a saved version instead.' };
export const RESTORE_SEAM = { title: 'Before the restore', text: 'Undo cannot take these back. Restore a saved version instead.' };
export const BEFORE_SESSION_SENTENCE = 'From an earlier visit. Undo cannot take it back. Restore a saved version instead.';
export const NOT_SAVED_WORD = 'Not saved';
export const NOT_SAVED_SENTENCE = 'This change was never saved, so the model on disk does not have it. Undo is not available.';
export const BEFORE_RESTORE_SENTENCE = 'Before the restore. Undo is not available.';
export const EMPTY_SENTENCE = 'No changes yet. What you, your assistant and Semanticus change shows here.';

/** Every edit on the page, parents opened: the leaves the undo stack is made of. */
export function leaves(rows: HistoryRow[]): HistoryRow[] {
  const out: HistoryRow[] = [];
  for (const r of rows) {
    if (r.children && r.children.length) out.push(...r.children);
    else if (r.kind === 'edit') out.push(r);
  }
  return out;
}

/** How many edits are undone right now (they come back with Redo). */
export const undoneCount = (rows: HistoryRow[]): number => leaves(rows).filter((r) => r.undone && r.redoable).length;

export function undoneBarText(n: number): string {
  return `${n} ${plural(n, 'change', 'changes')} undone. They come back with Redo until you make a new change.`;
}

/** Redo all walks to the newest undone edit, which brings back every undone edit older than it too. */
export function redoAllTarget(rows: HistoryRow[]): string | null {
  const undone = leaves(rows).filter((r) => r.redoable).sort((a, b) => Date.parse(b.when) - Date.parse(a.when));
  return undone[0]?.id ?? null;
}

export type RowAction =
  | { kind: 'undo'; later: number }
  | { kind: 'redo' }
  | { kind: 'restore'; hash: string }
  | { kind: 'none'; why: 'session' | 'restore' | 'nothing' };

export function rowAction(row: HistoryRow): RowAction {
  if (row.kind === 'saved') return row.detail ? { kind: 'restore', hash: row.detail } : { kind: 'none', why: 'nothing' };
  if (row.undoable) return { kind: 'undo', later: row.laterCount ?? 0 };
  if (row.redoable) return { kind: 'redo' };
  if (row.kind === 'decision' || row.kind === 'record' || row.kind === 'restored') return { kind: 'none', why: 'nothing' };
  if (row.beforeRestore) return { kind: 'none', why: 'restore' };
  if (row.beforeThisSession) return { kind: 'none', why: 'session' };
  return { kind: 'none', why: 'nothing' };
}

// ---- the timeline: rows, children and the two seams -----------------------------------------------------

export type Line =
  | { type: 'row'; row: HistoryRow; parent?: HistoryRow }
  | { type: 'seam'; which: 'session' | 'restore' };

/**
 * The list the page draws, newest first. A parent row nests its edits when it is open. Two dividers:
 *   the restore seam, above the first row older than the newest restore (contract 8.7);
 *   the session seam, above the first row from an earlier session (contract 8.9). After a restore the session
 *   the restore replaced is already under the restore seam, so the session seam then marks the sessions before
 *   that one, which is how the mockup draws both.
 */
export function timelineLines(rows: HistoryRow[], open: ReadonlySet<string>): Line[] {
  const lines: Line[] = [];
  const replaced = rows.find((r) => r.beforeRestore && r.sessionId)?.sessionId ?? null;
  let restoreSeam = false;
  let sessionSeam = false;
  for (const row of rows) {
    if (!restoreSeam && row.beforeRestore) {
      lines.push({ type: 'seam', which: 'restore' });
      restoreSeam = true;
    }
    const older = row.beforeThisSession && (row.sessionId ? row.sessionId !== replaced : !replaced);
    if (!sessionSeam && older) {
      lines.push({ type: 'seam', which: 'session' });
      sessionSeam = true;
    }
    lines.push({ type: 'row', row });
    if (row.children && row.children.length && open.has(row.id)) {
      for (const child of row.children) lines.push({ type: 'row', row: child, parent: row });
    }
  }
  return lines;
}

// ---- rows the engine carries for the audit trail and the versions ---------------------------------------
// The engine's History is the whole list (contract 8.8, LocalEngine.History.cs): a Verified Edits record with no edit
// to sit on comes as a "record" row carrying its record, a saved version the ledger never flagged (the rescue a
// restore makes first) as a "saved" row, and a Verified word past a broken record already reads "broken". The page
// merges nothing; it only words the rows.

const PUBLISHES = new Set(['deploy_live', 'deploy_stage']);

/** A publish that went ahead on a red check (the mockup's "one row, the reason word for word"). Since 8 Oct a person
 *  needs no reason, so the record's "overridden" verdict marks it too ("Published with warnings"). */
export const isRedCheck = (r: VerifiedEditRecord) => (!!r.overrideReason || r.verdict === 'overridden') && PUBLISHES.has(r.op);

/** The title of a "record" row: the red check in its own words, otherwise the operation in words. */
function recordTitle(record: VerifiedEditRecord): string {
  if (!isRedCheck(record)) return opLabel(record.op);
  return readSafetyCheckOverride(record.summary)?.kind === 'promote' ? SAFETY_CHECK_COPY.promoteTitle : SAFETY_CHECK_COPY.publishTitle;
}

// ---- words on a row -------------------------------------------------------------------------------------

const MONTHS = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];
const pad = (n: number) => String(n).padStart(2, '0');
const dayKey = (d: Date) => d.getFullYear() * 10000 + d.getMonth() * 100 + d.getDate();

/** Clock time, not "2m ago" (mockup, Simplify): "10:46" today, "Yesterday", then "Sep 21". */
export function clock(iso: string, now: number): string {
  const t = new Date(iso);
  if (Number.isNaN(t.getTime())) return '';
  const today = new Date(now);
  if (dayKey(t) === dayKey(today)) return pad(t.getHours()) + ':' + pad(t.getMinutes());
  const y = new Date(now); y.setDate(y.getDate() - 1);
  if (dayKey(t) === dayKey(y)) return 'Yesterday';
  return MONTHS[t.getMonth()] + ' ' + t.getDate();
}

/** "Customer[Tier]" for a column, the bare name for a measure or a table, "Model" for the model. */
export function objectName(ref: string): string {
  if (ref === 'primer') return 'Primer';   // a Primer write (F-353): the ref names the document, not a model object
  const colon = ref.indexOf(':');
  const kind = colon > 0 ? ref.slice(0, colon) : '';
  const rest = colon > 0 ? ref.slice(colon + 1) : ref;
  const slash = rest.indexOf('/');
  if (slash > 0 && (kind === 'column' || kind === 'hierarchy' || kind === 'partition')) {
    return rest.slice(slash + 1) + ' in ' + rest.slice(0, slash);   // "Margin % in Sales", not the formula spelling Sales[Margin %]
  }
  return slash > 0 ? rest.slice(rest.lastIndexOf('/') + 1) : rest;
}

const PROPERTY_WORD: Record<string, string> = {
  IsHidden: 'Hidden', Description: 'Description', FormatString: 'Format', SortByColumn: 'Sort by', SummarizeBy: 'Summarize by',
  Expression: 'Formula', Name: 'Name', DisplayFolder: 'Folder', DataType: 'Data type', DataCategory: 'Data category',
  AiInstructions: 'AI instructions', Synonyms: 'Synonyms', Annotations: 'Notes kept with it', IsActive: 'Active', CrossFilteringBehavior: 'Filter direction',
};

/** A property name in plain words ("Sort by", not "SortByColumn"). */
export function propertyWord(prop: string | null | undefined): string {
  if (!prop) return 'Changed';
  if (PROPERTY_WORD[prop]) return PROPERTY_WORD[prop];
  const words = prop.replace(/^Is(?=[A-Z])/, '').replace(/([a-z0-9])([A-Z])/g, '$1 $2').toLowerCase();
  return words.charAt(0).toUpperCase() + words.slice(1);
}

function valueWord(value: string | null | undefined, missing: string): string {
  if (value == null || value === '') return missing;
  if (value === 'True') return 'Yes';
  if (value === 'False') return 'No';
  return value;
}

/** The before and after table in the detail panel (mockup): object, change, before, after. */
export function changeRows(row: HistoryRow): Array<{ ref: string; object: string; change: string; before: string; after: string }> {
  const source = row.children && row.children.length ? row.children.flatMap((c) => c.deltas ?? []) : row.deltas ?? [];
  return source.filter((d) => d.kind !== 'check').map((d) => ({
    ref: d.ref,
    object: objectName(d.ref),
    change: d.kind === 'add' ? 'Added' : d.kind === 'remove' ? 'Removed' : propertyWord(d.props?.[0]),
    before: d.kind === 'add' ? '(new)' : valueWord(d.before, '(empty)'),
    after: d.kind === 'remove' ? '(removed)' : valueWord(d.after, '(empty)'),
  }));
}

/** The objects a row touched, for its sub line: two names, then "and N more". */
export function objectsLine(row: HistoryRow): string {
  const refs = [...new Set((row.deltas ?? []).filter((d) => d.kind !== 'check').map((d) => objectName(d.ref)))];
  if (refs.length === 0) return '';
  return refs.length <= 2 ? refs.join(', ') : refs.slice(0, 2).join(', ') + ' and ' + (refs.length - 2) + ' more';
}

const words = (title: string | null | undefined) => {
  const t = (title ?? '').trim();
  if (!t) return 'An edit';
  // A sentence the engine wrote is kept as it is; an operation id is put into words.
  return /\s/.test(t) && !/_/.test(t) ? editWords(t) : opLabel(t);
};

/** The row's bold line. */
export function rowTitle(row: HistoryRow, parent?: HistoryRow): string {
  if (row.kind === 'record' && row.record) return recordTitle(row.record);
  if (row.kind === 'saved') return 'Saved version: ' + (row.title ?? '').replace(/^Saved a version: /, '');
  if (parent) {
    const ds = (row.deltas ?? []).filter((d) => d.kind !== 'check');
    if (ds.length === 1) return objectName(ds[0].ref) + ' · ' + propertyWord(ds[0].props?.[0]).toLowerCase();
    // Many objects, one kind of change: "4 measures · format", as the mockup names a batch.
    const kinds = new Set(ds.map((d) => d.ref.slice(0, Math.max(0, d.ref.indexOf(':')))));
    const props = new Set(ds.map((d) => d.props?.[0] ?? ''));
    if (ds.length > 1 && kinds.size === 1 && props.size === 1) {
      const kind = [...kinds][0];
      const noun = KIND_PLURAL[kind] ?? 'items';
      return ds.length + ' ' + noun + ' · ' + propertyWord([...props][0]).toLowerCase();
    }
    return words(row.title);
  }
  if (row.kind === 'item' || row.kind === 'run' || row.kind === 'draft' || row.kind === 'task') return row.title || row.cause?.title || words(row.title);
  return words(row.title);
}

const KIND_PLURAL: Record<string, string> = { measure: 'measures', column: 'columns', calcColumn: 'columns', culture: 'languages', level: 'levels', table: 'tables', relationship: 'relationships',
  hierarchy: 'hierarchies', partition: 'partitions', role: 'roles', perspective: 'perspectives' };

const CAUSE_WORD: Record<string, string> = { item: 'Plan task', run: 'Workflow run', draft: 'Change group', task: 'Task for your assistant' };
const EDIT_WORD: Record<Who, string> = { you: 'Your own edit', assistant: 'Your assistant\'s edit', semanticus: 'Semanticus', workflow: 'A workflow step' };

/** The quiet line under a row's title: what kind of thing it was, and for an edit, the objects it touched. */
export function metaLine(row: HistoryRow): string {
  const who = whoOf(row.by);
  if (row.kind === 'saved') {
    return who === 'semanticus' ? 'Saved version · saved for you before the restore'
      : who === 'assistant' ? 'Saved version · saved by your assistant' : 'Saved version · saved by you';
  }
  if (row.kind === 'restored') return 'The model went back to a saved version. Where you were is saved too.';
  if (row.kind === 'decision') return 'Plan decision';
  if (row.kind === 'record') {
    const r = row.record;
    if (r && isRedCheck(r)) return r.overrideReason ? '"' + r.overrideReason + '"' : SAFETY_CHECK_COPY.noReason;
    return r?.summary ?? 'A check';
  }
  if (CAUSE_WORD[row.kind]) {
    const parts = [CAUSE_WORD[row.kind]];
    const approved = row.cause?.approvedBy;
    if (approved === 'you') parts.push('you approved');
    else if (approved === 'assistant') parts.push('your assistant approved');
    const steps = row.report?.steps?.length ?? 0;
    if (row.kind === 'run' && steps) parts.push(steps + ' ' + plural(steps, 'step', 'steps'));
    const n = row.children?.length ?? 0;
    parts.push(n + ' ' + plural(n, 'change', 'changes'));
    return parts.join(' · ');
  }
  const objects = objectsLine(row);
  const line = objects ? EDIT_WORD[who] + ' · ' + objects : EDIT_WORD[who];
  return row.notSaved ? NOT_SAVED_WORD + ' · ' + line : line;
}

/** The one confirmation a restore asks (mockup): the question in bold, then what happens. */
export function restoreQuestion(label: string, whenIso: string, now: number): { question: string; text: string } {
  const c = clock(whenIso, now);
  const when = /^\d\d:\d\d$/.test(c) ? 'saved at ' + c : c === 'Yesterday' ? 'saved yesterday' : c ? 'saved on ' + c : 'saved earlier';
  return {
    question: `Restore ${label}, ${when}?`,
    text: 'The model goes back to how it was then. Semanticus first saves where you are now as a new version, so you can come back.',
  };
}

/** One restore question at a time: asking about another version replaces it, asking again closes it. */
export function askRestore(current: string | null, rowId: string): string | null {
  return current === rowId ? null : rowId;
}

/** What a restore's answer does to the page. `restored` comes first: the engine answers restored AND an error when
 *  the files came back but the checkpoint commit after them failed (RestoreHistoryCheckpointAsync). That restore
 *  happened, so the question closes (a second press would save and restore again) and the error is a warning, as
 *  the Edit History view does. Only an answer that restored nothing keeps the question, with the error as a failure. */
export function restoreOutcome(r: { restored: boolean; error?: string | null; note?: string | null }): { settled: boolean; msg: string | null; tone: 'warn' | 'bad' | null } {
  if (r.restored) return { settled: true, msg: r.error || null, tone: r.error ? 'warn' : null };
  // The restored flag is the answer, not the absence of an error: a refusal can come back with no error text.
  return { settled: false, msg: r.error || r.note || 'Nothing was restored.', tone: 'bad' };
}

/** What a person was looking at when their restore reopened the model. The restore is a new session, and Studio
 *  remounts every page on a new session (App keys its pages by it), so without this the Plan came back on Work and a
 *  half restore's warning went with the old page. Held at module level, which survives the remount; given only to the
 *  session the restore reopened (the engine names it), taken once, and stale after a minute. Kept by time alone, a
 *  restore whose remount never came put its line on the next model opened within the minute (verifier r2, O4). */
let restoreLanding: { msg: string | null; tone: 'warn' | 'bad' | null; sessionId: string; at: number } | null = null;
const RESTORE_LANDING_MS = 60_000;
export function noteRestoreLanding(o: { msg: string | null; tone: 'warn' | 'bad' | null }, sessionId: string | null | undefined, now = Date.now()): void {
  restoreLanding = sessionId ? { msg: o.msg, tone: o.tone, sessionId, at: now } : null;
}
export function peekRestoreLanding(sessionId: string | null | undefined, now = Date.now()): boolean {
  return !!restoreLanding && !!sessionId && restoreLanding.sessionId === sessionId && now - restoreLanding.at < RESTORE_LANDING_MS;
}
export function takeRestoreLanding(sessionId: string | null | undefined, now = Date.now()): { msg: string | null; tone: 'warn' | 'bad' | null } | null {
  if (!peekRestoreLanding(sessionId, now)) return null;
  const landing = restoreLanding!;
  restoreLanding = null;
  return { msg: landing.msg, tone: landing.tone };
}

/** The detail's quiet last line (mockup, Simplify: hashes and numbers become one line in details). */
export function footLine(row: HistoryRow, record: VerifiedEditRecord | undefined, chain: VerifiedEditsChain | null): string {
  const parts: string[] = [];
  if (row.revision) parts.push('Session change ' + row.revision);
  if (record) parts.push('verified record ' + record.seq);
  if (record && chain) parts.push(chain.chainIntact || record.seq < chain.firstBrokenSeq ? 'record intact' : 'record broken');
  return parts.join(' · ');
}

/** The Verified Edits record that proves this row: the same session and revision (contract 8.4). When several
 *  share one revision, a warning outranks a plain pass, so "Overridden" is never hidden behind "Verified". */
export function recordFor(row: HistoryRow, chain: VerifiedEditsChain | null): VerifiedEditRecord | undefined {
  if (row.record) return row.record;
  const keys = row.children && row.children.length ? row.children.map((c) => c.sessionId + '|' + c.revision) : [row.sessionId + '|' + row.revision];
  const rank = (v: string) => (v === 'overridden' ? 3 : v === 'needs-review' || v === 'impact' ? 2 : verifiedWord(v) ? 1 : 0);
  let best: VerifiedEditRecord | undefined;
  for (const r of chain?.records ?? []) {
    if (r.revision <= 0 || !keys.includes(r.sessionId + '|' + r.revision)) continue;
    if (!best || rank(r.verdict) > rank(best.verdict)) best = r;
  }
  return best;
}

/** Find the row for an edit by its revision in this session ("Show this edit" in the evidence panel). */
export function rowForRevision(rows: HistoryRow[], sessionId: string | null | undefined, revision: number): { row: HistoryRow; parent?: HistoryRow } | null {
  for (const r of rows) {
    for (const c of r.children ?? []) if (c.sessionId === sessionId && c.revision === revision) return { row: c, parent: r };
    if (!r.children?.length && r.sessionId === sessionId && r.revision === revision && r.kind === 'edit') return { row: r };
  }
  return null;
}

/** A red-check publish, read out of its recorded sentence: what the check found, and where it went. The recorded
 *  sentence is hash-chained and never edited; this is display only (copy.ts, readSafetyCheckOverride). */
export function redCheckLines(record: VerifiedEditRecord): string[] {
  const red = readSafetyCheckOverride(record.summary);
  if (!red) return [];
  return [
    SAFETY_CHECK_COPY.found + ' ' + red.problems + ' ' + (red.kind === 'promote' ? SAFETY_CHECK_COPY.promoteWentAhead : SAFETY_CHECK_COPY.publishWentAhead),
    SAFETY_CHECK_COPY.destinationLabel + ' ' + red.destination,
  ];
}
