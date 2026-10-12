import type { ChangeNotification, ChangeDelta, ActivityEvent, ChangeWords } from '../bridge';

export interface SaidGroup {
  id: string;
  sessionId: string;
  firstRevision: number;
  lastRevision: number;
  deltas: ChangeDelta[];
  title?: string;
  plan?: boolean;
  planChanges?: number;
  saved?: boolean;
  words?: ChangeWords;
}

export class SaidModel {
  current: SaidGroup | null = null;
  private call: { sessionId: string; id: string; tool?: string } | null = null;
  private lastAt = 0;
  startCall(sessionId: string, id: string, tool?: string) {
    if (!this.current?.words || this.current.sessionId !== sessionId || Date.now() - this.lastAt > 20000) this.current = null;
    this.call = { sessionId, id, tool };
  }
  endCall() { this.current = null; this.call = null; }
  activity(e: ActivityEvent) {
    if (this.current?.words && e.kind !== 'edit') { this.endCall(); return; }
    if (!this.current?.plan || e.kind !== 'apply_plan' || e.origin !== 'agent') return;
    const applied = (e.result as { applied?: number } | undefined)?.applied;
    if (typeof applied !== 'number' || applied < 0) return;
    this.current = { ...this.current, planChanges: (this.current.planChanges ?? 0) + applied };
  }
  change(n: ChangeNotification) {
    if (this.current && this.current.sessionId !== n.sessionId) this.endCall();
    const plan = n.cause?.kind === 'plan_item' && n.cause.approvedBy === 'assistant';
    if (n.replay || (!plan && (n.by ?? (n.origin === 'agent' ? 'assistant' : n.origin)) !== 'assistant')) {
      this.endCall();
      return;
    }
    const saved = n.label === 'save' && !n.deltas?.length;
    if (!n.deltas?.length && !saved) return;
    if (this.current && n.revision <= this.current.lastRevision) return;
    if (this.current && this.call?.id !== this.current.id && !sameWords(this.current.words, n.words)) this.current = null;
    if (!this.current) this.current = {
      id: this.call?.sessionId === n.sessionId ? this.call.id : `${n.sessionId}:${n.revision}`,
      sessionId: n.sessionId, firstRevision: n.revision, lastRevision: n.revision, deltas: [],
    };
    this.current = { ...this.current, lastRevision: n.revision,
      plan: this.call?.tool === 'apply_plan_items' || this.current.plan,
      saved, title: n.cause?.title || this.current.title, deltas: [...this.current.deltas, ...(n.deltas ?? [])],
      words: mergeWords(this.current.words, n.words) };
    this.lastAt = Date.now();
  }
}

export function saidCopy(group: SaidGroup | null): string {
  if (!group) return '';
  if (group.saved) return 'Your assistant saved the model.';
  const refs = [...new Set(group.deltas.map((d) => d.ref))];
  const n = refs.length;
  if (group.plan) {
    const count = group.planChanges ?? n;
    return `Your assistant ran the plan. ${count} change${count === 1 ? '' : 's'}.`;
  }
  if (group.title) return `Your assistant ran "${group.title}". ${titledCount(group, refs)}.`;
  if (group.words) return group.words.sentence;
  const kinds = new Set(group.deltas.map((d) => d.kind));
  const verb = kinds.size === 1 && kinds.has('add') ? 'added'
    : kinds.size === 1 && kinds.has('remove') ? 'removed' : 'changed';
  if (n === 1) {
    const ref = refs[0];
    const kind = ref.slice(0, ref.indexOf(':'));
    const rest = ref.slice(ref.indexOf(':') + 1);
    if (kind === 'model' || kind === 'culture') {
      const language = kind === 'culture' || rest.startsWith('Culture');
      const settings: Record<string, string> = { Name: "the model's name", Description: "the model's description", CompatibilityLevel: "the model's compatibility level" };
      const setting = language ? "the model's language" : settings[group.deltas[0].props?.[0] ?? rest] || 'the model settings';
      return `Your assistant ${verb} ${setting}.`;
    }
    const container = ['measure', 'column', 'calcColumn', 'hierarchy', 'level', 'partition', 'calcitem'].includes(kind);
    const name = container && rest.includes('/') ? rest.slice(rest.indexOf('/') + 1) : rest;
    return `Your assistant ${verb} ${name || 'the model'}.`;
  }
  const descriptions = group.deltas.every((d) => d.kind === 'update' && d.props?.length === 1 && d.props[0].toLowerCase() === 'description');
  const types = new Set(refs.map((ref) => ref.split(':')[0] === 'calcColumn' ? 'column' : ref.split(':')[0]));
  const type = types.size === 1 ? [...types][0] : '';
  const noun = descriptions ? 'descriptions' : ['measure', 'column', 'table', 'relationship'].includes(type) ? `${type}s` : 'fields';
  if (n <= 3 && !descriptions) return `Your assistant ${verb} ${joinNames(refs.map(fieldName))}.`;
  return `Your assistant ${verb} ${n} ${noun}.`;
}

// One unit for the person and for Claude: the things the call was asked about. A rename also rewrites the formulas that use the
// names, and those are said apart ("26 fields, and 3 formulas that use their names"), never added into the same number.
function titledCount(group: SaidGroup, refs: string[]): string {
  const words = group.words;
  if (!words?.targets?.length) return `${refs.length} change${refs.length === 1 ? '' : 's'}`;
  const asked = new Set(words.targets);
  // The engine names some targets by words ("the model's description"), not by ref, so none may match: count the changes then.
  if (!refs.some((ref) => asked.has(ref))) return `${refs.length} change${refs.length === 1 ? '' : 's'}`;
  const count = asked.size;
  const head = `${count} ${count === 1 ? words.noun.replace(/s$/, '') : words.noun}`;
  const extra = refs.filter((ref) => !asked.has(ref));
  if (!extra.length) return head;
  const renamed = group.deltas.some((d) => d.props?.includes('Name'));
  const formulas = extra.filter((ref) => group.deltas.some((d) => d.ref === ref && d.props?.includes('Expression')));
  if (renamed && formulas.length === extra.length) return `${head}, and ${formulas.length} formula${formulas.length === 1 ? '' : 's'} that use${formulas.length === 1 ? 's' : ''} ${count === 1 ? 'its' : 'their'} name${count === 1 ? '' : 's'}`;
  return `${head}, and ${extra.length} other change${extra.length === 1 ? '' : 's'} that came with ${count === 1 ? 'it' : 'them'}`;
}

function fieldName(ref: string): string {
  const colon = ref.indexOf(':');
  const kind = ref.slice(0, colon);
  const rest = ref.slice(colon + 1);
  return ['measure', 'column', 'calcColumn', 'hierarchy', 'level', 'partition', 'calcitem'].includes(kind)
    && rest.includes('/') ? rest.slice(rest.indexOf('/') + 1) : rest;
}
function joinNames(names: string[]): string {
  return names.length <= 2 ? names.join(' and ') : `${names.slice(0, -1).join(', ')} and ${names[names.length - 1]}`;
}
export function sameWords(a?: ChangeWords, b?: ChangeWords): boolean {
  return !!a && !!b && a.verb === b.verb && a.noun === b.noun;
}
export function mergeWords(a?: ChangeWords, b?: ChangeWords): ChangeWords | undefined {
  if (!b) return undefined;
  if (!sameWords(a, b)) return b;
  const targets = [...new Set([...a!.targets, ...b.targets])];
  const count = targets.length || a!.count + b.count;
  const what = count <= 3 && targets.length && b.noun !== 'findings'
    ? joinNames(targets.map(fieldName)) : `${count} ${count === 1 ? b.noun.replace(/s$/, '') : b.noun}`;
  return { ...b, targets, count, sentence: `Your assistant ${b.verb} ${what}.` };
}
export function appendEdit(feed: ActivityEvent[], event: ActivityEvent): ActivityEvent[] {
  const previous = feed.find(e => e.kind === 'edit' && e.target === event.target);
  if (previous && sameWords(previous.words, event.words)) {
    const words = mergeWords(previous.words, event.words)!;
    event = { ...event, words, label: words.sentence };
  }
  const rest = feed.filter(e => e.kind !== 'edit' || e.target !== event.target);
  const head = rest[0];
  if (head?.kind === 'edit' && head.editEpoch === event.editEpoch && sameWords(head.words, event.words)) {
    const words = mergeWords(head.words, event.words)!;
    return [{ ...event, words, label: words.sentence }, ...rest.slice(1)].slice(0, 60);
  }
  return [event, ...rest].slice(0, 60);
}

interface UndoRow { id: string; sessionId?: string | null; revision?: number; kind: string; by?: string | null; cause?: { approvedBy?: string | null } | null; deltas?: { ref: string }[] | null; undoable?: boolean; undone?: boolean; children?: UndoRow[] | null }
export function canUndoGroup(rows: UndoRow[], group: SaidGroup): boolean {
  const flatten = (items: UndoRow[]): UndoRow[] => items.flatMap((row) => [row, ...flatten(row.children ?? [])]);
  const edits = flatten(rows)
    .filter((row) => row.kind === 'edit' && row.sessionId === group.sessionId && !row.undone);
  const refs = new Set(group.deltas.map((d) => d.ref));
  return !!undoEntry(rows, group) && edits.filter((row) => (row.revision ?? 0) > group.lastRevision)
    .every((row) => (row.by === 'assistant' || row.cause?.approvedBy === 'assistant')
      && !!row.deltas?.length && row.deltas.every((d) => [...refs].every((ref) => !refsOverlap(ref, d.ref))));
}

function refsOverlap(a: string, b: string): boolean {
  const contains = (table: string, child: string) => table.startsWith('table:') && child.includes(`:${table.slice(6)}/`);
  return a === b || contains(a, b) || contains(b, a);
}
export function undoEntry(rows: UndoRow[], group: SaidGroup): string | undefined {
  for (const row of rows) {
    if (row.kind === 'edit' && row.sessionId === group.sessionId && row.revision === group.firstRevision && row.undoable) return row.id;
    const child = row.children && undoEntry(row.children, group);
    if (child) return child;
  }
  return undefined;
}
