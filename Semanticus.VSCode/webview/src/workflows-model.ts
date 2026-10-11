// Workflows as one page, the pure half (lane W2; Plan-heart contract 21, composer contract 10.5).
//
// Everything the page decides that is not drawing lives here as plain functions, so
// test/workflows-one-page.test.mjs runs the same code the page runs: which workflows are "yours", the step marks in
// the doer hues, the Required mark and the one state word, the three filters, the summary sentence under an open
// workflow's title, which starters the New panel leads with, what Run does for each kind of workflow, and the
// words "Make it reusable" writes into a copy of a finished run's workflow.
//
// The engine owns every fact: a workflow's steps and who does each (getWorkflow), whether it is a starter or
// required by a team rule (listWorkflows, lane W1), its runs (the Plan's run rows and History). This module only
// reads those and says them in plain words. It never decides what a run did.
import { runOnceKind } from './workflow-v3model';

// ---- the wire (Semanticus.Engine/Workflow.cs, camelCase) -------------------------------------------------

export interface WfInfo {
  name: string; title: string; description?: string; version?: number; source: string;
  stepCount: number; gated?: boolean; error?: string | null; whenToUse?: string | null;
  enabled?: boolean; active?: boolean; activeReason?: string | null;
  /** Contract 21.2: a shipped file Use copies. Your own copies are never starters. */
  starter?: boolean;
  /** A team rule names this workflow. */
  required?: boolean;
  /** Your project copy shadows the shipped version a team rule requires. */
  replacesRequired?: boolean;
}

export interface WfQuestion { name: string; question: string; type?: string; by?: string; values?: string[] | null; default?: string | null }
export interface WfBlank { name: string; kind?: string; fills?: string | null; by?: string; generator?: string | null }
export interface WfCheck { kind: string; phase?: string | null; target?: string | null; min?: string | null; max?: string | null; of?: string[] | null; when?: string | null }
export interface WfGateInput { name: string; question: string; type?: string; required?: string }
export interface WfStep {
  id: string; number?: number; title: string; instructions?: string; ops?: string[];
  gate?: { strictness?: string | null; inputs?: WfGateInput[]; verify?: { kind: string; when?: string | null }[] } | null;
  when?: string | null; forEach?: unknown; call?: { workflow?: string } | null;
  /** Format 3: "semanticus" | "assistant" | "person". A format 1 or 2 step is a guided step, done by the assistant. */
  performer?: string | null;
  /** "prepare" | "change" | "after". */
  phase?: string | null;
  action?: { op?: string } | null;
  questions?: WfQuestion[] | null;
  blanks?: WfBlank[] | null;
  /** Format 3 "Brief for your assistant". */
  brief?: string | null;
  checks?: WfCheck[] | null;
}
export interface WfDef {
  name: string; title: string; description?: string; whenToUse?: string | null; version?: number;
  schemaVersion?: number; source?: string; error?: string | null; kind?: string | null;
  steps: WfStep[];
  assistantNotes?: string | null;
  inputs?: WfQuestion[] | null;
  slots?: { name: string; question: string }[] | null;
  provenance?: Record<string, string> | null;
  tags?: string[] | null;
}

// ---- the three names (contract 21.7) ---------------------------------------------------------------------

/** Contract 21.4: the one true sentence for the switch's off state. */
export const CHECKS_OFF_LINE = 'Checks inside runs are off. Actions a team rule requires still go through their workflow.';
/** Under the "Checks in runs" switch while it is on. The switch itself says on or off, so the line only says what off would do. */
export const CHECKS_SWITCH_NOTE = 'Off skips the checks inside runs. Required workflows stay required.';
export const WORKFLOWS_PRO_LINE = 'Workflows is a Semanticus Pro feature. The app still shows any run already going and anything waiting for you.';
/** Under Describe it in words: what happens, and where (Kane's UAT, 25 Sep: describing is authoring). */
export const DESCRIBE_LINE = 'Semanticus writes the steps. Nothing runs until you press Run.';

/** What describeWorkflowSteps answers: the words cut into steps, each with the op Semanticus knows or none. */
export interface DescribedWorkflow {
  words?: string;
  steps?: {
    title: string; op?: string | null; stepName?: string | null; matched?: boolean;
    ops?: string[] | null; performer?: string | null; target?: string | null; checks?: string[] | null;
    chosenBy?: string | null; reason?: string | null; notRead?: boolean;
  }[];
  when?: string | null;
  /** The words of each step past the most one description reads: each is a step to fill in, and the sentence names it. */
  notRead?: string[] | null;
  sentence?: string | null;
  jevAsked?: boolean;
  jevNote?: string | null;
  /** The description as a format 3 workflow, as Author opens it (unsaved). */
  document?: { name: string; text: string } | null;
}

/** A new workflow's title from its description: the first sentence, cut at a word near 60 characters. */
export function describedTitle(words: string): string {
  const first = words.trim().split(/[.\n]/u)[0].trim() || words.trim();
  if (first.length <= 60) return first;
  const cut = first.slice(0, 60);
  // Cut where the person paused (a comma) when there is one late enough, else at a word.
  const comma = cut.lastIndexOf(',');
  const space = cut.lastIndexOf(' ');
  const at = comma > 30 ? comma : space > 30 ? space : cut.length;
  return cut.slice(0, at).replace(/[,;:\s]+$/u, '').replace(/\s+(?:and|then|or)$/iu, '');
}

/** Author's notice after a description: how many steps, how many to fill in, and where "every month" goes. */
export function describedNote(d: DescribedWorkflow): string {
  const when = d.when ? ` "${d.when}" is when it runs, not a step: set it with Repeat on the Plan in this workflow's Settings.` : '';
  return `${d.sentence ?? ''}${when} Nothing is saved until you press Save.`.trim();
}

/** What Free is told on New. It names only what Pro gates today; Jev drafts nothing here, so it promises no Jev. */
export const DESCRIBE_PRO_LINE = 'New workflow and Describe it in words need Pro. Nothing is drafted or counted here on Free.';
export const REPLACES_REQUIRED = 'Replaces a required workflow';

// ---- which workflows are yours ----------------------------------------------------------------------------

export const isMine = (w: WfInfo): boolean => w.source === 'user';

/** The list: your workflows, and any shipped workflow a team rule requires (its Required mark is the one sign a
 *  person working alone ever sees of team rules). A starter nobody requires lives under New, not here. */
export function listRows(all: WfInfo[] | null | undefined): WfInfo[] {
  return (all ?? []).filter((w) => isMine(w) || !!w.required);
}

export type WfFilter = 'required' | 'off' | 'hidden';
export const FILTERS: ReadonlyArray<{ id: WfFilter; label: string }> = [
  { id: 'required', label: 'Required' },
  { id: 'off', label: 'Off' },
  { id: 'hidden', label: 'Hidden' },
];

/** The filters that mean something now: none while no workflow is required, off or hidden (a dead control), all three
 *  once one is. A filter that is pressed always stays, so it can be pressed again to clear it. */
export function visibleFilters(all: WfInfo[] | null | undefined, active: WfFilter | null): ReadonlyArray<{ id: WfFilter; label: string }> {
  const any = (all ?? []).some((w) => w.required || w.enabled === false || w.active === false);
  return any || active ? FILTERS : [];
}

/** The one state word on a row: Off (not offered), Hidden (a rule keeps it off today's menu), or nothing. */
export function stateWord(w: WfInfo): 'Off' | 'Hidden' | null {
  if (w.enabled === false) return 'Off';
  if (w.active === false) return 'Hidden';
  return null;
}

/** The filters replace the Governance table: a filter searches EVERY workflow, starters included, because a
 *  shipped workflow turned off is still a workflow someone may be looking for. */
export function filterRows(all: WfInfo[] | null | undefined, query: string, filter: WfFilter | null): WfInfo[] {
  const q = query.trim().toLowerCase();
  const pool = filter ? (all ?? []) : listRows(all);
  return pool.filter((w) => {
    if (filter === 'required' && !w.required) return false;
    if (filter === 'off' && w.enabled !== false) return false;
    if (filter === 'hidden' && !(w.enabled !== false && w.active === false)) return false;
    if (!q) return true;
    return [w.title, w.name, w.description ?? '', w.whenToUse ?? ''].some((t) => t.toLowerCase().includes(q));
  });
}

// ---- starters (contract 21.2) -------------------------------------------------------------------------------

/** The six starters the New panel leads with, each named by the shipped file Use copies. Which six is Kane's
 *  decision of 24 Sep (contract 21.2); the titles and descriptions are the engine's own. */
export const SIX_STARTERS: ReadonlyArray<string> = [
  'verified-measure', 'optimize-dax', 'deploy-to-production', 'governed-rename', 'secure-with-rls', 'month-end-close',
];

export interface Starter { name: string; title: string; description: string; template: boolean; required: boolean; stepCount: number }

/** The starters in the New panel's order: the six first, then every other shipped workflow and template under
 *  "More starters" until lane W3 parks them, so nothing that ships is out of reach. */
export function starterShelf(all: WfInfo[] | null | undefined, templates: { name: string; title: string; whenToUse?: string | null; slotCount?: number; error?: string | null }[] | null | undefined):
  { lead: Starter[]; more: Starter[] } {
  const items: Starter[] = [];
  for (const w of all ?? []) if (w.starter && !w.error) items.push({ name: w.name, title: w.title || w.name,
    description: w.whenToUse || w.description || '', template: false, required: !!w.required, stepCount: w.stepCount });
  for (const t of templates ?? []) if (!t.error) items.push({ name: t.name, title: plainTemplateTitle(t.title || t.name),
    description: t.whenToUse || '', template: true, required: false, stepCount: 0 });
  const lead = SIX_STARTERS.map((n) => items.find((s) => s.name === n)).filter((s): s is Starter => !!s);
  const more = items.filter((s) => !SIX_STARTERS.includes(s.name)).sort((a, b) => a.title.localeCompare(b.title));
  return { lead, more };
}

/** A starter with setup questions has a title that names them ("Month-end close for {{period_label}}"); on the shelf
 *  it reads in words ("Month-end close for your period") until Use asks them. */
export function plainTemplateTitle(title: string): string {
  return title.replace(/\{\{\s*([A-Za-z_][A-Za-z0-9_]*)\s*\}\}/g, (_, n: string) => 'your ' + n.replace(/_label$|_name$/, '').replace(/_/g, ' ').toLowerCase());
}

/** The name a Use copy gets: the starter's own, or the first free "-2", "-3" when you already have one. */
export function copyName(name: string, all: WfInfo[] | null | undefined): string | null {
  const mine = new Set((all ?? []).filter(isMine).map((w) => w.name));
  if (!mine.has(name)) return null;
  for (let i = 2; ; i++) if (!mine.has(`${name}-${i}`)) return `${name}-${i}`;
}

// ---- the step marks (doer hues) -----------------------------------------------------------------------------

export type Doer = 'semanticus' | 'you' | 'assistant';
export type Tag = 'you' | 'asst';

/** Is this a format 3 step (one the composer runs), or a guided step the assistant does? */
const isComposed = (s: WfStep): boolean => s.performer === 'semanticus' || s.performer === 'person' || !!s.action
  || (s.blanks?.length ?? 0) > 0 || (s.questions?.length ?? 0) > 0;

/** The two tags an outline row carries: "You choose" when a person answers something on the step, "Your assistant
 *  writes" when the assistant writes a part of it. A step with no tag is done by Semanticus. */
export function tagsOf(s: WfStep): Tag[] {
  const tags: Tag[] = [];
  const you = s.performer === 'person' || (s.questions ?? []).some((q) => (q.by ?? 'you') === 'you');
  const asst = (!isComposed(s) && (s.performer ?? 'assistant') === 'assistant') || s.performer === 'assistant'
    || (s.blanks ?? []).some((b) => (b.by ?? 'assistant') !== 'you');
  if (you) tags.push('you');
  if (asst) tags.push('asst');
  return tags;
}

/** Who does the step, for its one mark: the assistant when it writes anything, else you when you choose anything,
 *  else Semanticus. */
export function doerOf(s: WfStep): Doer {
  const tags = tagsOf(s);
  return tags.includes('asst') ? 'assistant' : tags.includes('you') ? 'you' : 'semanticus';
}

export const isAfter = (s: WfStep): boolean => s.phase === 'after';

/** One mark per step, and where the steps after the change begin (a thin line on the strip), or -1. */
export function stepMarks(def: WfDef | null | undefined): { marks: Doer[]; afterAt: number } {
  const steps = def?.steps ?? [];
  const afterAt = steps.findIndex(isAfter);
  return { marks: steps.map(doerOf), afterAt: afterAt > 0 ? afterAt : -1 };
}

// ---- the summary sentence (composer contract 10.3 shapes) ------------------------------------------------

export interface Segment { text: string; hue?: Doer }

const count = (n: number, one: string, many: string) => `${n} ${n === 1 ? one : many}`;

/** What a step's checks really do in a run: the model-wide switch wins, then the step's own gate, then the workflow's.
 *  The engine's own order (WorkflowRunner.EffectiveStrictness); the per-workflow settings file is the one layer a page
 *  cannot see, so a workflow relaxed there reads as its file says until the run itself reports otherwise. */
export function effectiveStrictness(def: WfDef | null | undefined, step: WfStep | null | undefined, enforcement?: string | null): 'hard' | 'warn' | 'off' {
  const word = (v: unknown) => (v === 'hard' || v === 'warn' || v === 'off' ? v : null);
  return word(enforcement) ?? word(step?.gate?.strictness) ?? word((def as { strictness?: string } | null | undefined)?.strictness) ?? 'hard';
}

/** One sentence under the title that says who does what, built from the steps; the counts carry the doer hue.
 *  `enforcement` is the model-wide checks switch ("off" | "warn" | "hard" | null): a sentence about checks says what
 *  the run will actually do, so with the checks off it never says Semanticus checks the work (UAT N4). */
export function summaryOf(def: WfDef | null | undefined, enforcement?: string | null): Segment[] {
  const steps = def?.steps ?? [];
  if (steps.length === 0) return [{ text: 'No steps yet. Add the first one, or describe it in words.' }];
  const questions = (def?.inputs ?? []).filter((q) => (q.by ?? 'you') === 'you').length
    + steps.reduce((n, s) => n + (s.questions ?? []).filter((q) => (q.by ?? 'you') === 'you').length, 0);
  const parts = steps.reduce((n, s) => n + (s.blanks ?? []).filter((b) => (b.by ?? 'assistant') !== 'you').length, 0);
  // Only checks that will run are counted: a step whose checks are off checks nothing.
  const checks = steps.reduce((n, s) => (effectiveStrictness(def, s, enforcement) === 'off' ? n : n + (s.checks?.length ?? 0) + (s.gate?.verify?.length ?? 0)), 0);
  const warnOnly = checks > 0 && steps.every((s) => effectiveStrictness(def, s, enforcement) !== 'hard');
  const composed = steps.some(isComposed);
  if (!composed) {
    const out: Segment[] = [{ text: 'Your assistant does ' }, { text: count(steps.length, 'step', 'steps'), hue: 'assistant' }];
    if (checks > 0 && warnOnly) out.push({ text: ', and Semanticus ' }, { text: 'checks the work', hue: 'semanticus' }, { text: ' and warns when a check fails, but never holds a step back.' });
    else if (checks > 0) out.push({ text: ', and Semanticus ' }, { text: 'checks the work', hue: 'semanticus' }, { text: ' before a step counts as done.' });
    else out.push({ text: '. Nothing is checked by Semanticus.' });
    return out;
  }
  const out: Segment[] = [];
  if (questions > 0) out.push({ text: 'You answer ' }, { text: count(questions, 'question', 'questions'), hue: 'you' });
  if (parts > 0) out.push({ text: questions > 0 ? ' and your assistant writes ' : 'Your assistant writes ' }, { text: count(parts, 'part', 'parts'), hue: 'assistant' });
  if (out.length) out.push({ text: '. Then Semanticus ' }); else out.push({ text: 'Semanticus ' });
  out.push(checks > 0 && !warnOnly
    ? { text: 'makes the change only if every check passes', hue: 'semanticus' }
    : { text: checks > 0 ? 'makes the change and warns when a check fails' : 'makes the change', hue: 'semanticus' });
  out.push({ text: '.' });
  // A composed workflow with a person's stop or an assistant's step runs step by step (lane author-gaps, B9): it says
  // where it stops and which steps the assistant does, so "makes the change" is not read as one change.
  const at = (who: string) => steps.map((s, i) => (s.performer === who ? i + 1 : 0)).filter((n) => n > 0);
  const stops = at('person'), mine = at('assistant');
  if (stops.length) out.push({ text: ` It stops for you at step ${stops.join(', ')}.`, hue: 'you' });
  if (mine.length) out.push({ text: ` Your assistant does step ${mine.join(', ')}.`, hue: 'assistant' });
  return out;
}

export const summaryText = (def: WfDef | null | undefined, enforcement?: string | null): string => summaryOf(def, enforcement).map((s) => s.text).join('');

// ---- what Run does ------------------------------------------------------------------------------------------

/** A workflow whose steps Semanticus runs is drafted (compose_task with the workflow) and runs as one change from
 *  its Plan row; a guided workflow starts a run (start_workflow) whose steps the Plan row walks. Either way it
 *  lands on the Plan, never on a page of its own. */
export function runKind(def: WfDef | null | undefined): 'draft' | 'run' {
  // The engine's rule (WorkflowParser.IsComposed; N3, lane l9-finish), the one Author's Run once uses too.
  return runOnceKind(def as unknown as Parameters<typeof runOnceKind>[0]) === 'draft' ? 'draft' : 'run';
}

// ---- last run (History) -------------------------------------------------------------------------------------

export interface RunHistoryRow { kind: string; when: string; report?: { workflow?: string | null; status?: string | null; verdict?: string | null; evidence?: string | null } | null }
export interface LastRun { when: string; status: string; verdict?: string | null }

/** The verdict word a run's report carries: the one kept on it, else the one its sealed evidence opens with (a report
 *  written before the verdict was kept). Null when the report holds neither. */
export function runVerdict(report: RunHistoryRow['report']): string | null {
  if (report?.verdict) return report.verdict;
  if (report?.evidence) {
    try { const v = (JSON.parse(report.evidence) as { verdict?: unknown }).verdict; if (typeof v === 'string' && v) return v; } catch { /* not readable: no verdict */ }
  }
  return null;
}

/** The newest finished run of each workflow, read from History's run entries. */
export function lastRuns(rows: RunHistoryRow[] | null | undefined): Map<string, LastRun> {
  const out = new Map<string, LastRun>();
  for (const r of rows ?? []) {
    const name = r.report?.workflow;
    if (r.kind !== 'run' || !name) continue;
    const seen = out.get(name);
    if (!seen || seen.when < r.when) out.set(name, { when: r.when, status: r.report?.status ?? '', verdict: runVerdict(r.report) });
  }
  return out;
}

/** The words a report's verdict reads as in the list: the report's own, never a kinder one. Verified is "passed";
 *  Unknown stays Unknown (the run finished and nothing was proven). */
const VERDICT_WORD: Record<string, { word: string; ok: boolean | null }> = {
  Verified: { word: 'passed', ok: true },
  Unknown: { word: 'Unknown', ok: null },
  NeedsReview: { word: 'needs review', ok: false },
  Broken: { word: 'broken', ok: false },
  Overridden: { word: 'overridden', ok: false },
};

/** "Tuesday · passed", "1 September · stopped", "Today · Unknown". A finished run says what its report says. A run whose
 *  report carries no verdict (written by an older build) says only that it finished: never "passed". */
export function lastRunLine(last: LastRun | undefined, now: Date = new Date()): { text: string; ok: boolean | null } {
  if (!last) return { text: 'Never run', ok: null };
  const d = new Date(last.when);
  if (Number.isNaN(d.getTime())) return { text: 'Never run', ok: null };
  const day = new Date(now.getFullYear(), now.getMonth(), now.getDate()).getTime();
  const that = new Date(d.getFullYear(), d.getMonth(), d.getDate()).getTime();
  const days = Math.round((day - that) / 86400000);
  const when = days === 0 ? 'Today' : days === 1 ? 'Yesterday' : days < 7 && days > 0
    ? d.toLocaleDateString('en-GB', { weekday: 'long' }) : d.toLocaleDateString('en-GB', { day: 'numeric', month: 'long' });
  const status = (last.status || '').toLowerCase();
  const finished = status === 'completed' || status === 'done' || status === 'passed';
  const said = last.verdict ? VERDICT_WORD[last.verdict] : undefined;
  const shown = status === 'aborted' ? { word: 'stopped', ok: false as boolean | null }
    : finished ? (said ?? { word: 'finished', ok: null })
      : { word: status || 'ended', ok: false as boolean | null };
  return { text: `${when} · ${shown.word}`, ok: shown.ok };
}

/** The mark under a row's name: "From a run" for a workflow made from a finished run, "from a starter" for a copy
 *  of a shipped workflow, and the step count. */
export function subLine(w: WfInfo, def: WfDef | null | undefined): string {
  const steps = count(w.stepCount, 'step', 'steps');
  if (def?.provenance && def.provenance['derived_from']) return `From a run · ${steps}`;
  if (w.starter) return `${steps} · a starter`;
  if (def?.provenance && (def.provenance['template'] || def.provenance['slot_values'])) return `${steps} · from a starter`;
  return steps;
}

// ---- Make it reusable ---------------------------------------------------------------------------------------

/** A copy of a workflow's file under a new name and title, carrying the run it came from (`derived_from`), which is
 *  how every surface tells a workflow made from a run. Only the frontmatter lines named here change, so the copy
 *  differs from its source in its identity and its provenance and nothing else. A format 1 file keeps the run as a
 *  top-level key (format 1 keeps unknown keys as provenance); format 2 and 3 files keep it under `provenance:`. */
export function reusableCopy(text: string, name: string, title: string, runId: string | null): string {
  const nl = text.includes('\r\n') ? '\r\n' : '\n';
  const lines = text.split(/\r?\n/);
  if (lines[0] !== '---') throw new Error('This workflow file has no settings block, so it cannot be copied here.');
  const end = lines.indexOf('---', 1);
  if (end < 0) throw new Error('This workflow file has no end to its settings block, so it cannot be copied here.');
  const head = lines.slice(1, end);
  const top = (key: string) => head.findIndex((l) => l.startsWith(key + ':'));
  const schema = (() => { const i = top('schemaVersion'); return i < 0 ? 1 : Number.parseInt(head[i].slice('schemaVersion:'.length).trim(), 10) || 1; })();
  const set = (key: string, value: string) => { const i = top(key); if (i >= 0) head[i] = `${key}: ${value}`; else head.push(`${key}: ${value}`); };
  set('name', name);
  set('title', JSON.stringify(title));
  if (!runId) return ['---', ...head, ...lines.slice(end)].join(nl);
  if (schema <= 1) {
    const i = top('derived_from');
    if (i >= 0) head[i] = `derived_from: ${runId}`; else head.push(`derived_from: ${runId}`);
  } else {
    const p = top('provenance');
    if (p < 0) head.push('provenance:', `  derived_from: ${runId}`);
    else {
      let j = p + 1;
      while (j < head.length && /^\s/.test(head[j])) {
        if (head[j].trim().startsWith('derived_from:')) { head.splice(j, 1); continue; }
        j++;
      }
      head.splice(j, 0, `  derived_from: ${runId}`);
    }
  }
  return ['---', ...head, ...lines.slice(end)].join(nl);
}

/** The new workflow's file name, kebab-case, from the run's workflow and date: "optimize-dax-from-a-run", then -2. */
export function reusableName(workflow: string, all: WfInfo[] | null | undefined): string {
  const taken = new Set((all ?? []).map((w) => w.name));
  const base = `${workflow}-from-a-run`;
  if (!taken.has(base)) return base;
  for (let i = 2; ; i++) if (!taken.has(`${base}-${i}`)) return `${base}-${i}`;
}

// ---- a title typed in words becomes a file name (contract 21.1 row 55) --------------------------------------

/** "Publish to Test with checks" becomes "publish-to-test-with-checks". Null when no letter survives. `all` is every
 *  name already in use: the saved workflows, and the new ones whose unsaved work is kept (see freeName). */
export function nameFromTitle(title: string, all: ReadonlyArray<{ name: string }> | null | undefined): string | null {
  const base = title.toLowerCase().normalize('NFKD').replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '').replace(/^[0-9-]+/, '').slice(0, 48).replace(/-+$/, '');
  if (!base) return null;
  return freeName(base, all);
}

/** `base`, or `base-2`, `base-3` and on, whichever no name in `all` holds. A new workflow is kept, unsaved, under its
 *  file name, so a name must be free among the kept new workflows too, not only the saved ones: two new workflows with
 *  one title shared one key, and opening the second dropped or overwrote the work kept for the first (verifier on
 *  m11/sol-fixes-1, D2). */
export function freeName(base: string, all: ReadonlyArray<{ name: string }> | null | undefined): string {
  const taken = new Set((all ?? []).map((w) => w.name));
  if (!taken.has(base)) return base;
  for (let i = 2; ; i++) if (!taken.has(`${base}-${i}`)) return `${base}-${i}`;
}

/** The notice when the unsaved work a person opened was made to a same-named workflow in another model: it stays kept,
 *  and only that model can take it back (verifier on m11/sol-fixes-1 round 2). */
export const keptElsewhere = (title: string): string =>
  `The unsaved changes kept for ${title} were made to the workflow of that name in another model. They are still kept. Open that model to get them back.`;

/** Where a kept entry belongs, said when another kept entry has the same title (verifier on m11/sol-fixes-1 round 3). */
export const heldPlaceWords = (place: { kind: 'model'; folder: string } | { kind: 'new'; file: string } | null | undefined): string =>
  !place ? '' : place.kind === 'model' ? ` in the model in ${place.folder}` : ` (new, file ${place.file})`;

/** The notice when the kept work opened belongs to another model and this model's own kept work for it is open. */
export const keptElsewhereOwnOpen = (title: string): string =>
  `The unsaved changes you opened were made to ${title} in another model. They are still kept. Open that model to get them back. These are the unsaved changes kept for this model's own ${title}.`;

/** Kept work for a saved workflow that is no longer in this model (the assistant deleted it, or it is another model's):
 *  it opens as a new workflow with the work, so it can be saved again (verifier on m11/sol-fixes-1 round 3). */
export const keptForGone = (title: string): string =>
  `There is no workflow named ${title} in this model now, so your kept changes open here as a new workflow. Save it to keep it in this model, or go back to keep the changes for later.`;

/** Author opened for a saved workflow that is no longer in this model. */
export const WORKFLOW_GONE = 'This workflow is not in this model now. Any unsaved changes to it are kept on the Workflows list.';

/** A new workflow's file name, taken while its changes were kept (the assistant saved one of that name, say). */
export const nameTakenLine = (taken: string, free: string): string =>
  `A workflow named ${taken} was saved while your changes were kept here, so this one needs its own file name. Your changes are still here. Save them as ${free}, or close this and keep them for later.`;

/** Said once a new workflow is saved under the free name. */
export const savedUnderFreeName = (saved: string, taken: string): string =>
  `Saved as ${saved}, because a workflow named ${taken} was saved while your changes were kept.`;

/** A workflow file's text under another file name: its top-level `name:` line, which must match the file name. */
export function withWorkflowName(text: string, name: string): string {
  return text.replace(/^name:[^\r\n]*/m, `name: ${name}`);
}

// ---- the older format notice (contract 21.6) -----------------------------------------------------------------

export const UPGRADE_LINE = 'This workflow uses an older format. Upgrade it';
/** Offered on your own file in format 1; a shipped file is never upgraded in place. A stock starter's copy is already
 *  written in the current format (F11). A filled template keeps its provenance as format 1 top-level keys, which
 *  format 2 refuses, so it is not offered an upgrade that would only be refused. */
export const canUpgrade = (def: WfDef | null | undefined, mine: boolean): boolean => mine && !!def && !def.error && (def.schemaVersion ?? 1) < 2
  && !(def.provenance && (def.provenance['template'] || def.provenance['slot_values']));

// ---- a format 3 check in plain words (the engine's own sentences, Composer/DraftCopy.cs CheckWords) --------

const joinAnd = (items: string[]): string => items.length <= 1 ? (items[0] ?? '') : `${items.slice(0, -1).join(', ')} and ${items[items.length - 1]}`;
const percent = (v: string | null | undefined): string => {
  if (v == null || v === '') return '?';
  const n = Number(v);
  return Number.isFinite(n) ? `${Math.round(n * 100 * 100) / 100}%` : '?';
};

/** The same sentence DraftCopy.CheckWords writes for the packet and the draft's check line, so Author and the
 *  Plan row say one thing about one check. test/workflows-one-page.test.mjs holds the two byte for byte. */
export function checkWords(c: WfCheck): string {
  switch (c.kind) {
    case 'valid_dax': return 'A valid formula against the model as the draft leaves it.';
    case 'dax_lint': return 'No formula warnings.';
    case 'refs_within': return 'Uses only ' + joinAnd(c.of ?? []) + '.';
    case 'max_chars': return 'Short enough for the field.';
    case 'no_formula_text': return 'Plain words, no formula text.';
    case 'valid_m': return 'Power Query with a let and an in, brackets and quotes balanced.';
    case 'choice_member': return 'One of the options listed.';
    case 'name_free': return 'The name is free.';
    case 'key_unique': return 'The key is unique.';
    case 'dates_within': return 'The dates fall inside the date table.';
    case 'source_matches': return 'The source still has these columns.';
    case 'result_range': return 'The result is between ' + percent(c.min) + ' and ' + percent(c.max) + '.';
    case 'row_count': return 'The row count is as expected.';
    default: return 'Check ' + c.kind + '.';
  }
}

/** When a check runs, in the engine's words (DraftCopy.PhaseWord). */
export const phaseWord = (phase: string | null | undefined): string =>
  phase === 'before' ? 'Checked' : phase === 'written' ? 'Checked once written' : 'Checks after it runs';

// ---- a team rule saved from the page's older list (UAT stale-copy class) ---------------------------------------------

/** The controls hand over the whole list of workflows a rule requires, built from the list the page drew. The person's
 *  change is the difference between that list (`shown`) and what they handed over, and it is applied to the rule as it
 *  stands now (`live`): a workflow the assistant added or dropped in the same rule since the page drew is never undone.
 *  A rule left requiring nothing is switched off. */
export function bindingChange(shown: string[], live: { require: string[]; mode: string } | null | undefined, require: string[], mode: string): { require: string[]; mode: string } {
  const added = require.filter((n) => !shown.includes(n));
  const removed = shown.filter((n) => !require.includes(n));
  const next = Array.from(new Set([...(live?.require ?? []).filter((n) => !removed.includes(n)), ...added]));
  if (next.length === 0) return { require: [], mode: 'off' };
  return { require: next, mode: added.length ? mode : (live?.mode && live.mode !== 'off' ? live.mode : mode) };
}
