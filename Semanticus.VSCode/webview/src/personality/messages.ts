// Section 5 of the approved personality page. Flagged lines are deliberately absent.
// Templates need measured facts. Example counts and names from the page are never defaults.
export type Moment = 'empty' | 'first-open' | 'huge-model' | 'big-success' | 'success' | 'waiting' | 'undo' | 'idle' | 'tour' | 'next' | 'jev';
export type Facts = Record<string, string | number>;
type Message = { moment: Moment; line: string; plain: string; facts?: string[]; testOnly?: boolean };
export const MESSAGES = {
  'no-measures': { moment: 'empty', line: 'No measures yet. Every good model started as a pile of columns.', plain: 'No measures yet.' },
  'no-relationships': { moment: 'empty', line: 'These tables have not met yet. Want me to introduce them?', plain: 'No relationships yet. Create one?' },
  'no-matches': { moment: 'empty', line: 'Nothing by that name. Not even a synonym, and we have a lot of those.', plain: 'No matches.' },
  'empty-plan': { moment: 'empty', line: 'Nothing on the plan.', plain: 'Nothing on the plan.' },
  'no-workflows': { moment: 'empty', line: 'No saved workflows yet. Do something twice and I will offer to remember it.', plain: 'No saved workflows yet.' },
  'reading-model': { moment: 'first-open', line: 'Reading your model. This takes about as long as this sentence.', plain: 'Reading your model.' },
  'grade-open': { moment: 'first-open', line: 'Grade {grade}. {fixes} fixes get you to an A. Want them?', plain: 'Grade {grade}. {fixes} fixes raise it to A. Apply them?', facts: ['grade', 'fixes'] },
  'huge-model': { moment: 'huge-model', line: '{tables} tables. I have read them all, so you do not have to.', plain: 'Loaded {tables} tables.', facts: ['tables'] },
  'grade-a': { moment: 'big-success', line: 'Grade A. Copilot will read this like a good book.', plain: 'Grade A.' },
  'zero-findings': { moment: 'big-success', line: 'Zero findings. The best practice check has nothing left to say. Enjoy the quiet.', plain: 'No findings.' },
  'synonyms': { moment: 'big-success', line: '{count} synonyms. Copilot now knows that revenue, sales and turnover are the same thing.', plain: 'Added {count} synonyms.', facts: ['count'] },
  'synonym-fields': { moment: 'big-success', line: 'Synonyms added to {count} fields. Copilot has more words to work with.', plain: 'Synonyms added to {count} fields.', facts: ['count'] },
  'job-wait': { moment: 'waiting', line: 'Still working.', plain: 'Still working.' },
  'described': { moment: 'big-success', line: 'Every field has a description. Future you says thank you.', plain: 'Every field has a description.' },
  'bulk-rename': { moment: 'big-success', line: '{count} columns renamed in {seconds} seconds. That used to be an afternoon.', plain: 'Renamed {count} columns in {seconds} s.', facts: ['count', 'seconds'] },
  'first-publish': { moment: 'big-success', line: 'Your first publish. The model is out in the world now.', plain: 'Published to Test.', testOnly: true },
  'test-published': { moment: 'success', line: 'Published to Test.', plain: 'Published to Test.', testOnly: true },
  'plan-finished': { moment: 'big-success', line: 'Every box ticked. Go and show someone.', plain: 'Plan finished.' },
  'rehearsal': { moment: 'big-success', line: 'Copilot got all {count} questions right. It had good notes.', plain: '{count} of {count} answers correct.', facts: ['count'] },
  'relationship': { moment: 'success', line: '{from} and {to} are now related. One line, a lot less confusion.', plain: 'Relationship created: {from} to {to}.', facts: ['from', 'to'] },
  'star': { moment: 'success', line: 'Snowflake folded into a star. Much easier to draw.', plain: 'Snowflake folded into a star schema.' },
  'single-path': { moment: 'success', line: 'Only one path between these tables now. No more arguments.', plain: 'Ambiguous path removed.' },
  'single-filter': { moment: 'success', line: 'Filters flow one way now. Fewer surprises later.', plain: 'Filter direction set to single.' },
  'faster-measure': { moment: 'success', line: 'Same answer, {times} times faster. The numbers did not even notice.', plain: '{times} times faster, same result.', facts: ['times'] },
  'date-table': { moment: 'success', line: 'Date table marked. Time intelligence is now actually intelligent.', plain: 'Marked as the date table.' },
  'sort-month': { moment: 'success', line: 'Months sort January to December now. April is no longer first.', plain: 'Month now sorts by {column}.', facts: ['column'] },
  'formats': { moment: 'success', line: 'Every number has a format now. Goodbye, 0.333333333.', plain: 'Format strings set on {count} measures.', facts: ['count'] },
  'refresh-wait': { moment: 'waiting', line: 'Still refreshing. The service is counting very carefully.', plain: 'Refreshing. {seconds} s so far.', facts: ['seconds'] },
  'query-wait': { moment: 'waiting', line: 'This query visits every row. There are {rows}, so it is being polite.', plain: 'Running query over {rows} rows.', facts: ['rows'] },
  'jev-wait': { moment: 'waiting', line: 'Jev is weighing {options} options. It does not like to rush.', plain: 'Jev is choosing.', facts: ['options'] },
  'test-wait': { moment: 'waiting', line: 'Waiting on Fabric. We are both very patient.', plain: 'Waiting for the service.', testOnly: true },
  'undo': { moment: 'undo', line: 'Undone. As if it never happened.', plain: 'Undone.' },
  'redo': { moment: 'undo', line: 'Back again. You were right the first time.', plain: 'Redone.' },
  'idle': { moment: 'idle', line: 'All quiet. The model is ready when you are.', plain: '' },
  'tour-start': { moment: 'tour', line: 'This is your model. Let me show you around. Mind the many-to-many on the left.', plain: 'Tour of your model: {stops} stops.', facts: ['stops'] },
  'tour-end': { moment: 'tour', line: 'That was your model. It is in better shape than you think.', plain: 'Tour finished.' },
  'next': { moment: 'next', line: 'Next: describe {count} measures. About a second of work.', plain: 'Next: describe {count} measures.', facts: ['count'] },
  'jev-chose': { moment: 'jev', line: 'Jev chose {column}. It was {confidence}% sure, which for Jev is very sure.', plain: 'Jev chose {column} ({confidence}%).', facts: ['column', 'confidence'] },
} satisfies Record<string, Message>;
export type MessageId = keyof typeof MESSAGES;
export type VoiceEvent = { id: MessageId; facts?: Facts; plain?: string; ok?: boolean; error?: unknown; warning?: unknown; action?: 'delete' | 'publish'; stage?: string; elapsedMs?: number };
export type VoiceLine = { event: VoiceEvent; text: string; plain: string; joke: boolean };
export type VoiceMemory = { used: MessageId[]; lastJoke: number | null; published: boolean };
export const JOKE_INTERVAL_MS = 3 * 60_000;
export const LONG_WAIT_MS = 8_000;

const fill = (text: string, facts: Facts) => text.replace(/\{([a-z]+)\}/g, (_, key: string) => String(facts[key]));
export const isTestStage = (stage?: string) => stage?.trim().toLowerCase() === 'test';
// ConnectionRegistry labels the acceptance-test destination "uat". Never infer Test from a workspace name.
export const voicePublishStage = (label?: string | null): string | undefined => label?.trim().toLowerCase() === 'uat' ? 'test' : undefined;
export function updateVoiceFacts(line: VoiceLine, facts: Facts, plain: string): VoiceLine {
  return { ...line, event: { ...line.event, facts, plain }, plain, text: line.joke ? fill(MESSAGES[line.event.id].line, facts) : plain };
}

/** One instance per Studio session, shared by every message surface. Rendering never spends a joke. */
export class VoiceSession {
  private used = new Set<MessageId>();
  private lastJoke = -Infinity;
  private published = false;
  private pendingPlan = new Map<string, boolean>();
  constructor(memory?: VoiceMemory | null) {
    if (!memory) return;
    this.used = new Set((memory.used ?? []).filter((id) => id in MESSAGES));
    this.lastJoke = typeof memory.lastJoke === 'number' && Number.isFinite(memory.lastJoke) ? memory.lastJoke : -Infinity;
    this.published = memory.published === true;
  }
  memory(): VoiceMemory { return { used: [...this.used], lastJoke: Number.isFinite(this.lastJoke) ? this.lastJoke : null, published: this.published }; }
  observePlan(model: string, items: { status?: string }[]): VoiceEvent | null {
    const hadWork = this.pendingPlan.get(model) === true;
    this.pendingPlan.set(model, items.some((item) => item.status !== 'done'));
    return hadWork && items.length > 0 && items.every((item) => item.status === 'done') ? { id: 'plan-finished' } : null;
  }
  firstTestPublish(stage?: string): boolean {
    if (!isTestStage(stage) || this.published) return false;
    this.published = true;
    return true;
  }
  choose(event: VoiceEvent, mode: 'on' | 'quiet', now: number): VoiceLine | null {
    const msg: Message = MESSAGES[event.id];
    const facts = event.facts ?? {};
    if (msg.facts?.some((key) => facts[key] == null || (typeof facts[key] === 'number' && !Number.isFinite(facts[key])))) return null;
    const plain = event.plain ?? fill(msg.plain, facts);
    const safe = event.ok !== false && !event.error && !event.warning && event.action !== 'delete'
      && (event.action !== 'publish' || isTestStage(event.stage)) && (!msg.testOnly || isTestStage(event.stage))
      && (msg.moment !== 'waiting' || (event.elapsedMs ?? 0) > LONG_WAIT_MS);
    // No synonym equivalence claim without evidence for those exact terms.
    const accurate = event.id !== 'synonyms' || facts.equivalence === 'revenue,sales,turnover';
    const joke = safe && accurate && mode === 'on' && msg.line !== msg.plain && !this.used.has(event.id) && now - this.lastJoke >= JOKE_INTERVAL_MS;
    if (joke) { this.used.add(event.id); this.lastJoke = now; }
    const text = joke ? fill(msg.line, facts) : plain;
    return text ? { event, text, plain, joke } : null;
  }
}

/** Only successful, complete reads can support these claims. This also serves agent activity results. */
export function resultEvent(method: string, value: unknown): VoiceEvent | null {
  if (!value || typeof value !== 'object') return null;
  const r = value as Record<string, any>;
  if (r.error || r.warning || r.caveat || r.ok === false || r.applied === false || r.ruleErrors?.length || r.unfixed?.length || r.failedRefs?.length || r.rejected?.length) return null;
  if (method === 'aiReadinessScan' || method === 'ai_readiness_scan') {
    return r.grade === 'A' && !r.gatedBy?.length ? { id: 'grade-a' } : null;
  }
  if (method === 'bpaScan' || method === 'bpa_scan' || method === 'bpaFixAll' || method === 'bpa_fix_all') {
    const card = r.scorecard ?? r;
    return card.violationCount === 0 && !card.ruleErrors?.length && card.ruleCount > 0 ? { id: 'zero-findings' } : null;
  }
  if ((method === 'writeWords' || method === 'write_words') && r.applied > 0 && !r.failed && !r.warnings?.length
    && r.words?.verb === 'added synonyms to' && r.words?.noun === 'fields' && r.words.count >= 10) {
    return { id: 'synonym-fields', facts: { count: r.words.count } };
  }
  return null;
}
