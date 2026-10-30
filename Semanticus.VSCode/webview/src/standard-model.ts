// Review > Your standard (Kane picked option B, 18 Oct 2026): the pure half of the page. A standard is eight parts, each with a few plain choices, and it starts from
// one of three presets (Light, Balanced, Strict). The engine owns the catalogue (the parts, their choices, the presets) and every count, so this file holds only the
// shapes the page reads and the small sums it does on them. Nothing here is a fixture: with no engine answer the page says so.
//
// The engine tools behind it (engine lane m17/engine-groundwork; shapes follow design notes sections 2 and 3):
//   getStandard()            the standard in force for the open model (where it came from, every part as held) and the three presets.
//   previewStandard(patch?)  what the standard would change on THIS model; with a patch, what it would change with that patch on top. Nothing is written.
//   setStandard(patch)       a JSON patch of the standard file (null resets a part). Writes the standard file, not the model. It drops the file's comments.
//   applyStandard()          one undo: the sure changes now, and the Plan rows for renames, words and the AI instructions blanks.
// The engine speaks in plain-word values ("by-subject, over 10"), so a choice id here IS the engine's value, and the wire* functions below turn the engine's answers into the page's shapes.

export type PartId = 'checks' | 'names' | 'formats' | 'folders' | 'descriptions' | 'synonyms' | 'keys' | 'instructions';
export type PresetId = 'light' | 'balanced' | 'strict';
export const PRESET_IDS: PresetId[] = ['light', 'balanced', 'strict'];

export interface CatalogueChoice { id: string; label: string; short?: string }
export interface CataloguePart { id: PartId; name: string; ask: string; choices: CatalogueChoice[] }
export interface CataloguePreset { id: PresetId; name: string; line: string; picks: Record<PartId, string> }

/** Where the standard in force comes from: this model's own file, "Your default" on this computer, a shared file, or Balanced built in. */
export type StandardOrigin = 'model' | 'default' | 'shared' | 'builtin';
export interface StandardView {
  name: string;
  origin: StandardOrigin;
  /** The file, shown as a short path (or an address for a shared file). Empty for the built in Balanced. */
  where: string;
  /** The standard file as a full path on this computer, or empty when it is built in or a web address. */
  path: string;
  /** The shared file or address this standard points at, if any. */
  use: string;
  /** Your default file on this computer, and whether it exists yet. */
  userPath: string;
  userExists: boolean;
  /** Plain sentences about anything in the file the engine could not read. */
  notes: string[];
  /** True when the file has comments: the page says they are dropped when it changes the file. */
  hasComments: boolean;
  startsFrom: PresetId;
  picks: Record<PartId, string>;
  presets: CataloguePreset[];
  parts: CataloguePart[];
  /** The standard as its file (YAML), for the File view. */
  yaml: string;
}

export interface Example { what: string; from: string; to: string }
export interface PartEffect { now: number; plan: number; say?: string; examples: Example[] }
export interface PlanLine { part: PartId; text: string; doer: 'assistant' | 'you'; n: number }
export interface Preview {
  /** Changes made now, in total. */
  now: number;
  /** Plan rows this adds. */
  plan: number;
  /** The changes made now, by part, in a few words ("7 formats · 12 folders"). */
  words?: string;
  tasks: PlanLine[];
  parts: Record<PartId, PartEffect>;
  /** Grade now, after the changes made now, and once the Plan is done. Optional: a model with no scan yet has none. */
  air?: [number, number, number];
  bpa?: [number, number, number];
}
export interface Applied { changed: number; words?: string; tasks: PlanLine[]; planRows: number; entryId?: string }

/** What the page asks to change. `reset` parts go back to what the preset gives; `use` null goes back to this model's own file, 'default' points at Your default, anything else is a shared file or address. */
export type StandardPatch = Partial<{ startsFrom: PresetId; picks: Partial<Record<PartId, string>>; reset: PartId[]; use: string | null }>;

export const presetOf = (v: StandardView, id: PresetId): CataloguePreset | undefined => v.presets.find((p) => p.id === id);
export const partOf = (v: StandardView, id: PartId): CataloguePart | undefined => v.parts.find((p) => p.id === id);
export const choiceOf = (v: StandardView, id: PartId): CatalogueChoice | undefined => partOf(v, id)?.choices.find((c) => c.id === v.picks[id]);

/** The parts where this standard differs from the preset it starts from: the lines B shows under the presets. */
export function overrides(v: StandardView): CataloguePart[] {
  const base = presetOf(v, v.startsFrom);
  if (!base) return [];
  return v.parts.filter((p) => v.picks[p.id] !== base.picks[p.id]);
}

/** The patch that moves to another preset and keeps the person's own lines: those are written; every other part is reset so it follows the new preset. */
export function presetPatch(v: StandardView, to: PresetId): StandardPatch | null {
  const next = presetOf(v, to);
  if (!next) return null;
  const keep = new Set(overrides(v).map((p) => p.id));
  const picks: Partial<Record<PartId, string>> = {};
  const reset: PartId[] = [];
  for (const p of v.parts) {
    if (keep.has(p.id)) picks[p.id] = v.picks[p.id];
    else if (p.id === 'checks') picks.checks = next.picks.checks;   // the engine cannot reset the level, so it is written (it equals the preset)
    else reset.push(p.id);
  }
  return { startsFrom: to, picks, reset };
}

/** The short count under a preset or on a change line. */
export function countWords(p: Pick<Preview, 'now' | 'plan'>): string {
  const parts: string[] = [];
  if (p.now) parts.push(`${p.now} now`);
  if (p.plan) parts.push(`${p.plan} for the Plan`);
  return parts.length ? parts.join(' · ') : 'No change';
}

/** What one part does here, in a few words. */
export function effectWords(e: PartEffect | undefined): string {
  if (!e) return '';
  if (e.say) return e.say;
  if (e.now) return `${e.now} now`;
  if (e.plan) return `${e.plan} on the Plan`;
  return 'No change';
}

export const GRADE_BANDS: [number, string][] = [[90, 'A'], [80, 'B'], [70, 'C'], [60, 'D']];
export const gradeOf = (n: number): string => (GRADE_BANDS.find(([min]) => n >= min)?.[1] ?? 'F');

/** A count press on a preset shows this many examples for each part it changes, so the kind of change reads at a glance. */
export const EXAMPLES_PER_PART = 1;
export function moreThanShown(e: PartEffect): number {
  return Math.max(0, e.now + e.plan - e.examples.length);
}

/** The words the page says when the engine did not answer. Plain, with the next move. */
export const NO_ENGINE = 'Your standard could not be read. Open a model and try again.';
export const OLD_ENGINE = 'This version of the engine does not know about standards yet. Update Semanticus and reopen the model.';
export function loadError(raw: unknown): string {
  const t = String((raw as Error)?.message ?? raw ?? '');
  if (/method not found|not a known method|unknown method|MethodNotFound|no such method/i.test(t)) return OLD_ENGINE;
  if (/no model|not open|nothing open/i.test(t)) return NO_ENGINE;
  return t || NO_ENGINE;
}

/** "Copy as a page": the standard as short sentences for a client who does not use Semanticus (design notes, option C's idea, kept under File). */
export function standardAsPage(v: StandardView): string {
  const base = presetOf(v, v.startsFrom);
  const lines = [`${v.name} standard`, '', `Starts from ${base?.name ?? 'Balanced'}: ${base?.line ?? ''}`.trim(), ''];
  for (const p of v.parts) {
    const c = choiceOf(v, p.id);
    if (!c) continue;
    lines.push(`${p.name}: ${c.label}.`);
  }
  const ov = overrides(v);
  if (ov.length) lines.push('', `Different from ${base?.name}: ${ov.map((p) => p.name.toLowerCase()).join(', ')}.`);
  return lines.join('\n');
}

/** The Plan line for a task the standard adds, who does it, in the words the Plan uses. */
export const WHO_WORDS: Record<PlanLine['doer'], string> = { assistant: 'Your assistant', you: 'You' };

// ---------------------------------------------------------------- the engine's answers, turned into the page's shapes
export interface WireChecks { level?: string; off?: string[]; must?: string[]; extraRules?: string }
export interface WireSpec {
  standard?: string; startsFrom?: string; use?: string; checks?: WireChecks | null; names?: string; formats?: Record<string, string> | null;
  folders?: string; descriptions?: string; synonyms?: string; keysAndTotals?: string; aiInstructions?: string;
}
export interface WireState {
  name: string; source: 'model-file' | 'your-default' | 'built-in'; path?: string; writePath?: string; userPath?: string; userExists?: boolean; use?: string; hash?: string;
  effective: WireSpec; written?: WireSpec | null; fileText?: string | null; notes?: string[]; checkedAgainst?: string | null;
}
export interface WireGet { standard: WireState; presets: WireSpec[] }
export interface WirePart { part: string; setting?: string; changesNow: number; tasks: number; examples?: string[]; note?: string | null }
export interface WirePreview { standard?: WireState; parts: WirePart[]; changesNow: number; tasks: number; scoreNow?: number; scoreAfter?: number; sentence?: string }
export interface WireApplied { revision?: number; applied?: string[]; parts: WirePart[]; changesNow: number; tasks: number; planRows?: string[]; sentence?: string }

/** The page's part ids and the engine's names for them. */
export const PART_IDS: PartId[] = ['checks', 'names', 'formats', 'folders', 'descriptions', 'synonyms', 'keys', 'instructions'];
const ENGINE_PART: Record<PartId, string> = {
  checks: 'checks', names: 'names', formats: 'formats', folders: 'folders', descriptions: 'descriptions', synonyms: 'synonyms', keys: 'keys-and-totals', instructions: 'ai-instructions',
};
const PAGE_PART = Object.fromEntries(Object.entries(ENGINE_PART).map(([k, v]) => [v, k])) as Record<string, PartId>;

const FORMAT_KINDS = ['money', 'percent', 'count'];
/** The formats part as one choice id: the kinds that are set, in a fixed order ("percent 0.0%, count #,0"), or "leave". */
export function formatsId(f: Record<string, string> | null | undefined): string {
  const set = FORMAT_KINDS.filter((k) => { const v = Object.entries(f ?? {}).find(([n]) => n.toLowerCase() === k)?.[1]; return !!v && v.trim().toLowerCase() !== 'leave'; })
    .map((k) => `${k} ${Object.entries(f ?? {}).find(([n]) => n.toLowerCase() === k)![1]}`);
  return set.length ? set.join(', ') : 'leave';
}
/** The formats a choice id stands for. Every kind is written, "leave" included, so a choice on top of Strict really leaves a kind alone. */
export function formatsOf(id: string): Record<string, string> {
  const out: Record<string, string> = Object.fromEntries(FORMAT_KINDS.map((k) => [k, 'leave']));
  if (id === 'leave') return out;
  for (const part of id.split(/, (?=money |percent |count )/)) {
    const m = /^(money|percent|count) (.+)$/.exec(part);
    if (m) out[m[1]] = m[2];
  }
  return out;
}

const norm = (s: string | undefined | null, empty: string): string => { const t = (s ?? '').trim().toLowerCase(); return t || empty; };
function pickOf(spec: WireSpec, part: PartId): string {
  switch (part) {
    case 'checks': return norm(spec.checks?.level, 'balanced');
    case 'names': return norm(spec.names, 'leave');
    case 'formats': return formatsId(spec.formats);
    case 'folders': { const t = norm(spec.folders, 'none'); return t === 'by-subject' ? 'by-subject, always' : t; }
    case 'descriptions': return norm(spec.descriptions, 'none');
    case 'synonyms': return norm(spec.synonyms, 'none');
    case 'keys': return norm(spec.keysAndTotals, 'leave');
    case 'instructions': return (spec.aiInstructions ?? '').trim() || 'none';
  }
}
export const picksOf = (spec: WireSpec): Record<PartId, string> => Object.fromEntries(PART_IDS.map((p) => [p, pickOf(spec, p)])) as Record<PartId, string>;

const PRESET_LINES: Record<PresetId, string> = {
  light: 'Only the checks that change answers. Little is touched.',
  balanced: 'The usual checks. Folders and descriptions for the measures that need them.',
  strict: 'Everything tidy: names, formats, folders, descriptions and other names on all you can see.',
};
const PRESET_NAMES: Record<PresetId, string> = { light: 'Light', balanced: 'Balanced', strict: 'Strict' };

/** What each part is called and the plain choices for it. A choice id is the engine's own value. */
export const CATALOGUE: CataloguePart[] = [
  { id: 'checks', name: 'Checks', ask: 'Which checks reach your Plan?', choices: [
    { id: 'light', label: 'Only checks that change answers', short: 'Answer-changing only' },
    { id: 'balanced', label: 'Every check, the usual set', short: 'Every check' },
    { id: 'strict', label: 'Every check, and the warnings too', short: 'Every check and warning' }] },
  { id: 'names', name: 'Names', ask: 'How should tables and columns be named?', choices: [
    { id: 'leave', label: 'Leave names as they are', short: 'Leave' },
    { id: 'title-case', label: 'Title Case', short: 'Title Case' },
    { id: 'title-case-no-prefix', label: 'Title Case, and drop prefixes like Dim and Fact', short: 'Title Case, no prefix' }] },
  { id: 'formats', name: 'Formats', ask: 'Which number formats should measures use?', choices: [
    { id: 'leave', label: 'Leave formats as they are', short: 'Leave' },
    { id: 'percent 0.0%, count #,0', label: 'Percent as 0.0% and counts as whole numbers', short: 'Percent and counts' },
    { id: 'money $#,0.00, percent 0.0%, count #,0', label: 'Money, percent and counts', short: 'Money, percent, counts' }] },
  { id: 'folders', name: 'Folders', ask: 'Should measures sit in folders?', choices: [
    { id: 'none', label: 'No folders', short: 'None' },
    { id: 'by-subject, over 10', label: 'By subject, once more than 10 measures have no folder', short: 'By subject, over 10' },
    { id: 'by-subject, always', label: 'By subject, always', short: 'By subject, always' }] },
  { id: 'descriptions', name: 'Descriptions', ask: 'Which objects need a description?', choices: [
    { id: 'none', label: 'None', short: 'None' },
    { id: 'measures', label: 'Measures', short: 'Measures' },
    { id: 'measures, tables', label: 'Measures and tables', short: 'Measures and tables' },
    { id: 'all-visible', label: 'Everything people can see', short: 'All you can see' }] },
  { id: 'synonyms', name: 'Other names', ask: 'Should objects have other names for Copilot to match?', choices: [
    { id: 'none', label: 'None', short: 'None' },
    { id: '2, measures and tables', label: '2 other names for each measure and table', short: '2, measures and tables' },
    { id: '2, all-visible', label: '2 other names for everything people can see', short: '2, all you can see' }] },
  { id: 'keys', name: 'Keys and totals', ask: 'What happens to keys and ids?', choices: [
    { id: 'leave', label: 'Leave them as they are', short: 'Leave' },
    { id: 'hide keys, no-sum ids', label: 'Hide keys, and stop ids from adding up', short: 'Hide keys, no totals on ids' },
    { id: 'hide keys and technical, no-sum ids', label: 'Also hide technical columns', short: 'Hide keys and technical' }] },
  { id: 'instructions', name: 'AI instructions', ask: 'Should the model have AI instructions?', choices: [
    { id: 'none', label: 'Not asked for', short: 'Not asked for' }] },
];

/** The catalogue for one view: if a part holds a value the catalogue does not list (the file was written by hand), that value is shown as a choice too. */
export function catalogueFor(picks: Record<PartId, string>[]): CataloguePart[] {
  return CATALOGUE.map((part) => {
    const extra = [...new Set(picks.map((p) => p[part.id]))].filter((id) => !part.choices.some((c) => c.id === id));
    return extra.length ? { ...part, choices: [...part.choices, ...extra.map((id) => ({ id, label: id, short: id }))] } : part;
  });
}

/** The standard as a file, for the File view when none is on disk yet (the built in Balanced). */
export function specAsYaml(spec: WireSpec): string {
  const lines = [`standard: ${spec.standard ?? 'Balanced'}`, `starts-from: ${spec.startsFrom ?? 'balanced'}`, `checks:`, `  level: ${norm(spec.checks?.level, 'balanced')}`];
  lines.push(`names: ${norm(spec.names, 'leave')}`);
  const f = formatsOf(formatsId(spec.formats));
  const set = FORMAT_KINDS.filter((k) => f[k] !== 'leave');
  if (set.length) { lines.push('formats:'); for (const k of set) lines.push(`  ${k}: "${f[k]}"`); }
  lines.push(`folders: "${pickOf(spec, 'folders')}"`, `descriptions: "${pickOf(spec, 'descriptions')}"`, `synonyms: "${pickOf(spec, 'synonyms')}"`,
    `keys-and-totals: "${pickOf(spec, 'keys')}"`, `ai-instructions: ${pickOf(spec, 'instructions')}`);
  return lines.join('\n') + '\n';
}

/** The line the engine writes at the top of every standard file it saves: not the person's comment. */
const ENGINE_HEADER = /^# A Semanticus standard\..*$/m;
const shortPath = (p: string): string => p.split(/[\\/]/).filter(Boolean).slice(-2).join(' / ');
const startOf = (spec: WireSpec): PresetId => (PRESET_IDS.includes(norm(spec.startsFrom, 'balanced') as PresetId) ? norm(spec.startsFrom, 'balanced') as PresetId : 'balanced');

/** getStandard, as the page reads it. */
export function viewFromWire(w: WireGet): StandardView {
  const st = w.standard;
  const picks = picksOf(st.effective);
  const presets: CataloguePreset[] = PRESET_IDS.map((id) => {
    const spec = w.presets.find((p) => norm(p.startsFrom, '') === id) ?? w.presets[PRESET_IDS.indexOf(id)];
    return { id, name: PRESET_NAMES[id], line: PRESET_LINES[id], picks: picksOf(spec ?? {}) };
  });
  const pointsAtDefault = !!st.use && !!st.userPath && st.use === st.userPath;
  const origin: StandardOrigin = st.source === 'built-in' ? 'builtin' : st.source === 'your-default' || pointsAtDefault ? 'default' : st.use ? 'shared' : 'model';
  const where = origin === 'shared' ? st.use! : origin === 'builtin' ? '' : shortPath(st.path ?? '');
  const text = st.fileText ?? '';
  return {
    name: st.name, origin, where, path: st.path ?? '', use: st.use ?? '', userPath: st.userPath ?? '', userExists: !!st.userExists, notes: st.notes ?? [],
    hasComments: /^\s*#|\s#\s/m.test(text.replace(ENGINE_HEADER, '')), startsFrom: startOf(st.effective), picks, presets,
    parts: catalogueFor([picks, ...presets.map((p) => p.picks)]), yaml: text || specAsYaml(st.effective),
  };
}

/** One example line from the engine ("Margin %: no format to 0.0%"). It is free text, so the page shows it whole and never splits it. */
export function exampleOf(line: string): Example {
  return { what: line, from: '', to: '' };
}

const TASK_WORDS: Partial<Record<PartId, (n: number) => string>> = {
  names: (n) => `Rename ${n} ${n === 1 ? 'object' : 'objects'}`,
  descriptions: (n) => `Write ${n} ${n === 1 ? 'description' : 'descriptions'}`,
  synonyms: (n) => `Add other names to ${n} ${n === 1 ? 'object' : 'objects'}`,
  instructions: () => 'Write the AI instructions',
};
const NOUN: Record<PartId, string> = { checks: 'checks', names: 'names', formats: 'formats', folders: 'folders', descriptions: 'descriptions', synonyms: 'other names', keys: 'keys and totals', instructions: 'AI instructions' };

function partsOf(parts: WirePart[]): { map: Record<PartId, PartEffect>; tasks: PlanLine[]; words: string } {
  const map = Object.fromEntries(PART_IDS.map((id) => [id, { now: 0, plan: 0, examples: [] as Example[] }])) as Record<PartId, PartEffect>;
  const tasks: PlanLine[] = [];
  const said: string[] = [];
  for (const p of parts) {
    const id = PAGE_PART[p.part];
    if (!id) continue;
    const e: PartEffect = { now: p.changesNow, plan: p.tasks, examples: (p.examples ?? []).map(exampleOf) };
    if (p.note && (id === 'checks' || e.now + e.plan === 0)) e.say = p.note;
    map[id] = e;
    if (p.tasks > 0) tasks.push({ part: id, text: (TASK_WORDS[id] ?? ((n: number) => `${n} for the Plan`))(p.tasks), doer: 'assistant', n: p.tasks });
    if (p.changesNow > 0) said.push(`${p.changesNow} ${NOUN[id]}`);
  }
  return { map, tasks, words: said.join(' · ') };
}

/** previewStandard, as the page reads it. */
export function previewFromWire(w: WirePreview): Preview {
  const { map, tasks, words } = partsOf(w.parts);
  const out: Preview = { now: w.changesNow, plan: w.tasks, words: words || undefined, tasks, parts: map };
  if (typeof w.scoreNow === 'number' && typeof w.scoreAfter === 'number') out.air = [w.scoreNow, w.scoreAfter, w.scoreAfter];
  return out;
}

/** applyStandard, as the page reads it. */
export function appliedFromWire(w: WireApplied): Applied {
  const { tasks, words } = partsOf(w.parts);
  return { changed: w.changesNow, words: words || undefined, tasks, planRows: (w.planRows ?? []).length };
}

/** The JSON patch the engine takes for a change the page asks for. Null when there is nothing to say. */
export function wirePatch(v: StandardView, patch: StandardPatch): string | null {
  const out: Record<string, unknown> = {};
  if (patch.startsFrom) out.preset = patch.startsFrom;
  for (const id of patch.reset ?? []) out[ENGINE_PART[id]] = null;
  for (const [k, value] of Object.entries(patch.picks ?? {})) {
    const id = k as PartId;
    if (value === undefined) continue;
    if (id === 'checks') out.checks = { level: value };
    else if (id === 'formats') out.formats = formatsOf(value);
    else out[ENGINE_PART[id]] = value;
  }
  if (patch.use !== undefined) {
    // A shared file only fills the parts this model's own file does not write, so pointing at one clears the model's own lines.
    if (patch.use === null) out.use = null;
    else {
      out.use = patch.use === 'default' ? v.userPath : patch.use;
      out.preset = null;
      for (const id of PART_IDS) out[ENGINE_PART[id]] = null;
    }
  }
  return Object.keys(out).length ? JSON.stringify(out) : null;
}

/** The whole standard as a patch: used to save the standard in force as Your default. */
export function wholeStandard(v: StandardView): StandardPatch {
  return { startsFrom: v.startsFrom, picks: { ...v.picks } };
}
