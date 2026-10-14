// One word for the live write: Publish. Pure helpers so the status chip, the Publish page and its one button
// cannot drift, and so the publish rules have host-free tests.

export const PUBLISH_COMMAND_TITLE = 'Semanticus: Publish to Live Model';
export const PUBLISH_TREE_TOOLTIP = 'Publish to the live model';

export type PublishChipInput = {
  liveBound: boolean;
  changeCount?: number | null;
  liveOnlyCount?: number | null;
  previewError?: string | null;
};

/** Status-bar chip text. Null means hide the chip (the model is not live). */
export function publishChipText(input: PublishChipInput): string | null {
  if (!input.liveBound) return null;
  if (input.previewError) return 'Publish · could not check';
  if (input.changeCount == null) return 'Publish';
  const liveOnly = input.liveOnlyCount ?? 0;
  if (input.changeCount === 0 && liveOnly > 0) {
    return liveOnly === 1 ? 'Publish · 1 to remove' : `Publish · ${liveOnly} to remove`;
  }
  if (input.changeCount === 0) return 'Publish · up to date';
  return input.changeCount === 1 ? 'Publish · 1 change' : `Publish · ${input.changeCount} changes`;
}

// ── The change list ───────────────────────────────────────────────────────────────────────────────
// The row shape is the engine's diff item (compare.tsx ModelDiffItem), restated here so these helpers stay
// free of imports and a test can load this file on its own.
export interface ChangeRow {
  ref: string; objectType: string; name: string; table?: string | null; action: string;
  leftText?: string | null; rightText?: string | null;
}

export const isRemoval = (row: ChangeRow): boolean => row.action === 'Delete';

const KIND_WORD: Record<string, string> = {
  Table: 'table', Column: 'column', Measure: 'measure', Hierarchy: 'hierarchy', Partition: 'partition',
  Relationship: 'relationship', Role: 'role', Perspective: 'perspective', Culture: 'language',
  DataSource: 'data source', Expression: 'shared expression', CompatibilityLevel: 'compatibility level',
};
const KIND_ORDER = ['CompatibilityLevel', 'Table', 'Column', 'Measure', 'Hierarchy', 'Partition', 'Relationship', 'Role', 'Perspective', 'Culture', 'DataSource', 'Expression'];
const kindWord = (row: ChangeRow) => KIND_WORD[row.objectType] ?? row.objectType.toLowerCase();

function parseObject(text?: string | null): Record<string, unknown> | null {
  if (!text) return null;
  const t = text.trim();
  if (!t.startsWith('{')) return null;
  try { const v = JSON.parse(t); return v && typeof v === 'object' && !Array.isArray(v) ? v as Record<string, unknown> : null; }
  catch { return null; }
}

// True when the only property that differs between the two sides is the formula.
function onlyFormulaChanged(row: ChangeRow): boolean {
  const l = parseObject(row.leftText), r = parseObject(row.rightText);
  if (!l || !r) return false;
  const keys = new Set([...Object.keys(l), ...Object.keys(r)]);
  const changed = [...keys].filter((k) => JSON.stringify(l[k]) !== JSON.stringify(r[k]));
  return changed.length === 1 && changed[0] === 'expression';
}

// Annotations the publish carries to the live model (the engine's LiveDeploy.IsOwnedAnnotation). Any other
// annotation stays in your copy, so a change that is only that annotation can never go and is not listed.
const CARRIED_ANNOTATIONS = new Set(['Semanticus_Waivers', 'BestPracticeAnalyzer_IgnoreRules', 'BestPractizeAnalyzer_IgnoreRules']);

function annotationMap(o: Record<string, unknown>): Map<string, string> {
  const out = new Map<string, string>();
  const list = o.annotations;
  if (Array.isArray(list)) for (const a of list) if (a && typeof a === 'object') out.set(String((a as { name?: unknown }).name), JSON.stringify((a as { value?: unknown }).value));
  return out;
}

/** True when the row is an update whose only difference is annotations that the publish does not carry. */
export function isUnpublishable(row: ChangeRow): boolean {
  if (row.action !== 'Update') return false;
  const l = parseObject(row.leftText), r = parseObject(row.rightText);
  if (!l || !r) return false;
  const keys = new Set([...Object.keys(l), ...Object.keys(r)]);
  const changed = [...keys].filter((k) => JSON.stringify(l[k]) !== JSON.stringify(r[k]));
  // Right after a new model is created the copy and the live model differ in one setting that never goes from here: the default
  // data source version. A row that differs only in that is a lone row that can never go, so the page does not list it (hero 21, N8).
  // Any other change to the model (its description, language, implicit measures) can go and stays listed.
  if (row.objectType === 'Model') {
    const rest = changed.filter((k) => k !== 'defaultPowerBIDataSourceVersion');
    if (rest.length === 0) return true;
    if (!(rest.length === 1 && rest[0] === 'annotations')) return false;
  }
  if (changed.length !== 1 || changed[0] !== 'annotations') return false;
  const la = annotationMap(l), ra = annotationMap(r);
  const names = [...new Set([...la.keys(), ...ra.keys()])].filter((n) => la.get(n) !== ra.get(n));
  return names.length > 0 && names.every((n) => !CARRIED_ANNOTATIONS.has(n));
}

/** The rows the page lists: the changes that can go. */
export function publishableRows<T extends ChangeRow>(rows: readonly T[]): T[] { return rows.filter((r) => !isUnpublishable(r)); }

// ── Role members, in words ────────────────────────────────────────────────────────────────────────
// A role member is a person or a group in Microsoft Entra. Power BI hands back a guid for each, so the stored name is
// often "obj:<guid>@<tenant>". That is not something to read: a name is shown when there is one, else the kind of member.
export interface RoleMember { memberName?: string; identityProvider?: string; memberType?: string }

/** One member as a person reads it: its name when it has one, else "a Microsoft Entra group" (or user). */
export function memberWords(m: RoleMember): string {
  const name = (m.memberName ?? '').trim();
  if (name && !/^obj:/i.test(name)) return name;
  const type = (m.memberType ?? '').toLowerCase();
  return type === 'user' ? 'a Microsoft Entra user' : type === 'group' ? 'a Microsoft Entra group' : 'a Microsoft Entra member';
}

// The same member whether or not one side carries the server's memberId: the key is name, provider and type.
const memberKey = (m: RoleMember) => [(m.memberName ?? '').toLowerCase(), (m.identityProvider ?? '').toLowerCase(), (m.memberType ?? '').toLowerCase()].join('|');

function membersOf(text?: string | null): RoleMember[] | null {
  const o = parseObject(text);
  if (!o) return null;
  return Array.isArray(o.members) ? o.members.filter((m): m is RoleMember => !!m && typeof m === 'object') : [];
}

/**
 * Who is added to the destination and who is removed from it, in words. Your copy is the wanted side, so a member only in
 * your copy is added and a member only on the destination is removed. Null when neither side lists any members.
 */
export function roleMemberLines(leftText?: string | null, rightText?: string | null): { added: string[]; removed: string[] } | null {
  if (!parseObject(leftText)?.members && !parseObject(rightText)?.members) return null;
  const l = membersOf(leftText), r = membersOf(rightText);
  const count = (list: RoleMember[] | null) => { const c = new Map<string, { m: RoleMember; n: number }>(); for (const m of list ?? []) { const k = memberKey(m); const e = c.get(k); if (e) e.n++; else c.set(k, { m, n: 1 }); } return c; };
  const lc = count(l), rc = count(r);
  const only = (a: Map<string, { m: RoleMember; n: number }>, b: Map<string, { m: RoleMember; n: number }>) => {
    const out: string[] = [];
    for (const [k, e] of a) { const extra = e.n - (b.get(k)?.n ?? 0); if (extra > 0) out.push(extra > 1 ? `${memberWords(e.m)} (${extra})` : memberWords(e.m)); }
    return out.sort((x, y) => x.localeCompare(y));
  };
  return { added: only(lc, rc), removed: only(rc, lc) };
}

const hasItems = (v: unknown) => Array.isArray(v) && v.length > 0;

/** "a calendar and a hierarchy" for a table that carries them, else null. */
function tableExtras(row: ChangeRow): string | null {
  const o = parseObject(row.leftText);
  if (!o) return null;
  const parts: string[] = [];
  if (hasItems(o.calendars)) parts.push('a calendar');
  if (hasItems(o.hierarchies)) parts.push('a hierarchy');
  return parts.length ? parts.join(' and ') : null;
}

/** The few words beside a row: what kind of object it is and what happens to it. */
export function rowNote(row: ChangeRow, destination: string): string {
  // The compatibility level is one change on the list. Your copy holds the higher level (left), the destination the lower (right).
  if (row.objectType === 'CompatibilityLevel') return `raise it from ${row.rightText ?? 'the current level'} to ${row.leftText ?? 'the new level'}. Power BI cannot lower it again`;
  if (row.objectType === 'Role' && row.action === 'Update') {
    const lines = roleMemberLines(row.leftText, row.rightText);
    if (lines && (lines.added.length > 0 || lines.removed.length > 0)) return 'role members changed';
  }
  // A new table is told with what comes with it, so a date table reads as the date table, its calendar and its hierarchy.
  if (row.objectType === 'Table') {
    const withIt = tableExtras(row);
    if (row.action === 'Create') return withIt ? `new table, with ${withIt}` : 'new table';
    if (row.action === 'Update') {
      const l = parseObject(row.leftText), r = parseObject(row.rightText);
      if (l && r && JSON.stringify(l.calendars ?? null) !== JSON.stringify(r.calendars ?? null)) return 'calendar changed';
    }
  }
  if (row.action === 'Create') return `new ${kindWord(row)}`;
  if (row.action === 'Delete') return `only on ${destination || 'the destination'}. Tick to remove it there`;
  return onlyFormulaChanged(row) ? 'formula changed' : `${kindWord(row)} changed`;
}

/**
 * The table a row needs and does not have: a child of a new table, or a relationship that points at one, where the
 * new table is not ticked. Null when the row can go. Computed here, from the list and the ticks, so a tick never
 * costs a read of the destination.
 */
export function neededParent(row: ChangeRow, rows: readonly ChangeRow[], ticked: ReadonlySet<string>): { name: string; ref: string } | null {
  if (isRemoval(row)) return null;
  let tables: string[] = [];
  if (row.objectType === 'Relationship') {
    const o = parseObject(row.leftText);
    tables = [o?.fromTable, o?.toTable].filter((t): t is string => typeof t === 'string');
  } else if (row.table && row.objectType !== 'Table') {
    tables = [row.table];
  }
  for (const name of tables) {
    const parent = rows.find((r) => r.objectType === 'Table' && r.action === 'Create' && r.name === name);
    if (parent && !ticked.has(parent.ref)) return { name, ref: parent.ref };
  }
  return null;
}

/** Rows grouped as the page shows them: one group per table (the table's own row first), then "Model". */
export function groupRows<T extends ChangeRow>(rows: readonly T[]): { key: string; label: string; rows: T[] }[] {
  const groups = new Map<string, T[]>();
  for (const row of rows) {
    const key = (row.objectType === 'Table' ? row.name : row.table) || '';
    const list = groups.get(key); if (list) list.push(row); else groups.set(key, [row]);
  }
  const rank = (row: T) => { const i = KIND_ORDER.indexOf(row.objectType); return i < 0 ? 99 : i; };
  return [...groups.entries()]
    .sort((a, b) => (a[0] === '' ? 1 : 0) - (b[0] === '' ? 1 : 0) || a[0].localeCompare(b[0]))
    .map(([key, list]) => ({ key, label: key || 'Model', rows: [...list].sort((a, b) => rank(a) - rank(b) || a.name.localeCompare(b.name)) }));
}

export function publishCounts(rows: readonly ChangeRow[], ticked: ReadonlySet<string>): { changes: number; removals: number } {
  let changes = 0, removals = 0;
  for (const row of rows) { if (!ticked.has(row.ref)) continue; if (isRemoval(row)) removals++; else changes++; }
  return { changes, removals };
}

/**
 * What the engine's failed list says about each row, in plain words. It matches by the row's ref, which is opaque
 * and never taken apart: an entry is the ref alone (the dry run's refused republish) or the ref and a reason in brackets.
 */
export function failureReasons(failed: readonly string[] | null | undefined, rows: readonly ChangeRow[]): Map<string, string> {
  const out = new Map<string, string>();
  for (const text of failed ?? []) {
    const row = rows.find((r) => text === r.ref || text.startsWith(r.ref + ' ('));
    if (!row) continue;
    out.set(row.ref, plainFailure(text === row.ref ? '' : text.slice(row.ref.length + 2).replace(/\)$/, '')));
  }
  return out;
}

/** The engine's reason, in plain words. A bare ref with no reason is the dry run's refused republish. */
export function plainFailure(reason: string): string {
  const r = (reason || '').toLowerCase();
  if (!r || r.includes('republish')) return 'Power BI has a different object with this name, so this change cannot replace it.';
  if (r.includes('did not carry it') || r.includes('object type') || r.includes('did not sync')) return 'Power BI does not take this kind of change from here.';
  if (r.includes('delete refused')) return 'Power BI would not remove it. Compare again and try once more.';
  // Words the person should never read: how the engine moves a change, and the other tools it points at.
  if (/tmdl|xmla|shell|apply path|metadata|lineage|live deploy|deploy via|merged locally|\bTOM\b/i.test(reason)) return 'This change stays in your copy for now. Semanticus cannot send it to Power BI yet.';
  return reason.trim();
}

/** The engine's whole-push error, in plain words. A level raise that landed carries the service's own text after the sentence the person needs;
 *  that text goes through plainFailure so no engine word reaches the page. A raise Power BI refused shows the person's sentence and then ONLY the
 *  service's first sentence, and only when it is plain words: no error code, no activity id, no "Technical Details", nothing after them, and
 *  nothing at all when the first sentence names how the engine works. Any other error is shown as it comes. */
export function plainPublishError(error: string): string {
  const text = (error || '').trim();
  const landed = /^(The compatibility level is now \d+\. Nothing else was published\.)\s*([\s\S]*)$/.exec(text);
  const refused = /^(Power BI would not raise the compatibility level from \d+ to \d+, so nothing was published)[.:]?\s*([\s\S]*)$/.exec(text);
  if (refused && !landed) {
    const head = refused[1] + '.';
    const first = firstPlainSentence(refused[2]);
    return first ? `${head} ${first}` : head;
  }
  const m = landed;
  if (!m) return text;
  const reason = m[2].replace(/\s*Technical Details:[\s\S]*$/i, '').trim();
  return reason ? `${m[1]} ${plainFailure(reason)}` : m[1];
}

/** The first sentence of a service message when it reads as plain words; otherwise empty. Codes and the engine's own vocabulary are not plain. */
function firstPlainSentence(message: string): string {
  const cut = (message || '').replace(/\s*Technical Details:[\s\S]*$/i, '').trim();
  const sentence = /^[\s\S]+?[.!?](?=\s|$)/.exec(cut)?.[0]?.trim() ?? '';
  if (!sentence) return '';
  if (/0x[0-9a-f]{4,}|root\s*activity|activity\s*id|error\s*code|technical details|tmdl|xmla|shell|apply path|metadata|lineage|live deploy|deploy via|merged locally|\bTOM\b/i.test(sentence)) return '';
  return sentence;
}

/** The changes that did not go, named in one plain phrase: "1 change could not go (Model)". Up to two names, then a count. */
export function couldNotGoLine(failed: ReadonlyMap<string, string>, rows: readonly ChangeRow[]): string {
  const left = rows.filter((r) => failed.has(r.ref));
  const count = failed.size;
  const label = (r: ChangeRow) => r.name || r.ref;
  const shown = left.slice(0, 2).map(label);
  const rest = count - shown.length;
  const named = shown.length === 0 ? '' : ` (${shown.join(', ')}${rest > 0 ? ` and ${rest} more` : ''})`;
  return `${count === 1 ? '1 change' : `${count} changes`} could not go${named}.`;
}

// ── The one button and the lines around it ────────────────────────────────────────────────────────
export type PublishLabelInput = { changes: number; removals: number; target: string; busy?: boolean; checking?: boolean; empty?: boolean };

/** The label is the guard: it names the count and the place, and says so when something is removed. */
export function publishLabel(input: PublishLabelInput): string {
  if (input.busy) return 'Publishing...';
  if (input.checking) return 'Checking...';
  if (input.empty) return 'Nothing to publish';
  const target = input.target || 'the destination';
  const { changes, removals } = input;
  if (changes <= 0 && removals <= 0) return 'Nothing ticked';
  const n = changes === 1 ? '1 change' : `${changes} changes`;
  if (removals > 0 && changes > 0) return `Publish ${n} and remove ${removals} from ${target}`;
  if (removals > 0) return `Remove ${removals} from ${target}`;
  return `Publish ${n} to ${target}`;
}

export function checkingCopy(target: string): string { return `Checking ${target || 'the destination'} for changes.`; }
export function unreachableCopy(target: string): string { return `Could not reach ${target || 'the destination'}.`; }

export function nothingToPublishCopy(target: string): string {
  // The button already says Nothing to publish; this line says why, once (hero20 D13).
  return `${target || 'The destination'} already has everything in your copy.`;
}

/** The list after a good publish, before the page compares again: what went out is gone from it at once, so the button and the
 *  list do not sit on screen for seconds saying there is still something to publish (hero19 D13). A refused row stays. */
export function leftAfterPublish<T extends { ref: string }>(items: T[], appliedRefs: string[] | null | undefined, failedRefs: string[] | null | undefined): T[] {
  const gone = new Set(appliedRefs ?? []);
  const refused = (failedRefs ?? []).map((f) => String(f));
  return items.filter((i) => !gone.has(i.ref) || refused.some((f) => f.startsWith(i.ref)));
}

export function publishedLine(count: number, target: string): string {
  const t = target || 'the destination';
  return count <= 0
    ? `Nothing needed to change. ${t} already had these.`
    : `Published ${count === 1 ? '1 change' : `${count} changes`} to ${t}. Your copy did not change.`;
}
export function undoneLine(target: string, when: string): string { return `${target || 'The destination'} is back to how it was before ${when}.`; }
export function notPublishedLine(target: string): string { return `Nothing was published to ${target || 'the destination'}.`; }

/** The time of day, as "09:42", in the person's own clock. */
export function clockTime(date: Date): string {
  const two = (n: number) => String(n).padStart(2, '0');
  return `${two(date.getHours())}:${two(date.getMinutes())}`;
}
const MONTHS = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];
/** A saved version's time, as "2 Oct 14:15", in the person's own clock. */
export function shortWhen(utc: string | Date): string {
  const d = utc instanceof Date ? utc : new Date(utc);
  if (!Number.isFinite(d.getTime())) return 'an earlier time';
  return `${d.getDate()} ${MONTHS[d.getMonth()]} ${clockTime(d)}`;
}

// ── Earlier versions and Promote ─────────────────────────────────────────────────────────────────
export function restoreLabel(target: string, when: string): string { return `Restore ${target || 'the destination'} to ${when}`; }
export function restoreDetail(restored: number, removed: number, target: string): string {
  const t = target || 'The destination';
  if (restored <= 0 && removed <= 0) return `${t} already matches this version.`;
  const put = restored === 1 ? '1 object' : `${restored} objects`;
  const gone = removed === 1 ? '1 object' : `${removed} objects`;
  if (removed <= 0) return `This puts back ${put}.`;
  if (restored <= 0) return `This removes ${gone} that came after this version.`;
  return `This puts back ${put} and removes ${gone} that came after this version.`;
}
export function promoteLabel(count: number, from: string, to: string): string {
  if (count <= 0) return 'Nothing to promote';
  return `Promote ${count === 1 ? '1 item' : `${count} items`} from ${from} to ${to}`;
}

// A rollback result is read from `applied` and `failedRefs`, never from the absence of an error: the engine returns
// applied=false with a note when the target already matches the point, and a committed rollback can still carry
// objects it could not restore. The line claims exactly what happened.
export type RollbackOutcome = { applied: boolean; failedRefs?: string[] | null; note?: string | null; error?: string | null };
export function rollbackResultLine(r: RollbackOutcome, target: string, when: string): string {
  if (r.error) return r.error;
  if (!r.applied) return r.note || `${target} already matches this version. Nothing to restore.`;
  const failed = r.failedRefs ?? [];
  const base = `Restored ${target} to the version from ${when}`;
  if (failed.length > 0) {
    const names = failed.slice(0, 3).join(', ') + (failed.length > 3 ? ` and ${failed.length - 3} more` : '');
    return `${base}, except ${failed.length} object${failed.length === 1 ? '' : 's'} that could not be restored: ${names}.`;
  }
  return `${base}. Your copy did not change.`;
}

// The stage as a person writes it. The registry stores canonical ids, which read like a typo in a sentence
// ("to Contoso Sales · uat"). A label outside the standard four is somebody's own word, so it prints as typed.
// Display only: permission decisions still key off the raw label.
const PUBLISH_STAGE_NAMES: Record<string, string> = { local: 'Local', dev: 'Development', uat: 'UAT', prod: 'Production' };
export function publishStageName(label: string): string { return PUBLISH_STAGE_NAMES[label.trim().toLowerCase()] ?? label; }

// ---- Publish as a new model (r1/newmodel) ----
// A model with no destination yet can go to a workspace as a new model. It signs in the way this person last signed
// in to a workspace or the model's source; with none saved, it signs in in a browser.
export type SignInLike = { authMode?: string | null; tenantId?: string | null } | null | undefined;
export function newModelSignIn(known: SignInLike[]): { authMode: string; tenantId: string | null } {
  const k = known.find((r) => !!r && !!(r.authMode ?? '').trim());
  return { authMode: (k?.authMode ?? '').trim() || 'interactive', tenantId: (k?.tenantId ?? '').trim() || null };
}

export function newModelLabel(input: { busy: boolean; workspace: string | null }): string {
  if (input.busy) return 'Publishing...';
  return input.workspace ? `Publish to ${input.workspace}` : 'Publish';
}

/** The name the new-model card offers: the model's own name, or the next free "Name 2", "Name 3" when the workspace already has it. Only a default: a name the person types is theirs,
 *  and the engine refuses a taken one before anything is sent. */
export function freeNewModelName(name: string, taken: string[]): string {
  const has = new Set(taken.map((t) => t.trim().toLowerCase()));
  if (!has.has(name.trim().toLowerCase())) return name;
  for (let n = 2; ; n++) { const next = `${name.trim()} ${n}`; if (!has.has(next.toLowerCase())) return next; }
}

// ---- Where the assistant is publishing (D6, launch video 12 Oct) ----
// The engine says it with a start and an end (kind publish_target, only for the assistant's real publish). While it runs, the new-model card shows that target
// and not "Choose a workspace" and the open model's name.
export type AssistantPublish = { phase: 'start' | 'end'; workspace: string; name: string };
export function assistantPublish(e: { kind: string; origin?: string; result?: unknown } | null | undefined): AssistantPublish | null {
  if (!e || e.kind !== 'publish_target' || e.origin === 'human') return null;
  const r = (e.result ?? {}) as { phase?: unknown; workspace?: unknown; name?: unknown };
  if ((r.phase !== 'start' && r.phase !== 'end') || typeof r.workspace !== 'string' || !r.workspace.trim()) return null;
  return { phase: r.phase, workspace: r.workspace.trim(), name: typeof r.name === 'string' ? r.name.trim() : '' };
}
/** The restore point the assistant's compare publish just made, so the Publish page offers the same Undo publish as the person's own press. */
export function assistantPublishUndo(e: { kind: string; origin?: string; result?: unknown } | null | undefined): { id: string; count: number } | null {
  if (!e || e.kind !== 'apply_model_diff' || e.origin === 'human') return null;
  const r = (e.result ?? {}) as { applied?: unknown; count?: unknown; restorePointId?: unknown };
  if (r.applied !== true || typeof r.restorePointId !== 'string' || !r.restorePointId) return null;
  const count = typeof r.count === 'number' ? r.count : 0;
  return count > 0 ? { id: r.restorePointId, count } : null;
}
export function assistantPublishLine(t: { workspace: string; name: string }): string {
  return t.name ? `Your assistant is publishing ${t.name} to ${t.workspace}.` : `Your assistant is publishing to ${t.workspace}.`;
}
