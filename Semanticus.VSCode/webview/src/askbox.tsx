import { useEffect, useRef, useState } from 'react';
import { rpc, runHostCommand } from './bridge';
import { planRankingOf } from './features';
import { PENDING_ACCESS, upgradeToPro, useTier } from './pro';
import { WORD } from './composer-model';

// ===================================================================================================
// The ask box: one input, one answer line, one way through. It was a card on the Overview until slice 11
// (Plan-heart lane R1); it is the Plan's field (typing shows the route suggestion) and Ctrl J on every page now.
//
// WHY IT EXISTS. `route_ask` shipped on the agent door and nowhere else (Astra finding 14, 2026-09-21):
// `routeAsk(text)` sat on EngineRpcTarget and nothing in the extension or the webview ever called it.
// An RPC method nobody calls is not a capability on the human door, and CLAUDE.md's second golden rule
// is that every capability ships through both doors on one session. This is the second door.
//
// WHAT IT MAY DO, from docs/redesign/jev-plan.md M2:
//   Routing only navigates. The most this box ever does is name a place and take you there. It never
//   writes, never runs an operation and never applies anything, which is the only reason it is safe to
//   point at a person's free-typed words at all. A create answer opens the very same form the Model tree
//   opens and writes nothing by itself: the person still names the table and still writes the formula.
//   A sentence only where the engine's own state answers. Every word in the answer line that is not the
//   name of a place comes from the engine (AskRoutePoint.cs), never from here and never from a service.
//   Below the bar, the menu. The engine reports that as a reason with no tool, and this box prints that
//   reason as it was written and offers no door.
//
// PRO ONLY, ON BOTH DOORS (Kane, 22 September: ask is Jev, so it is Pro). This file used to carry the
// opposite note. The engine refuses route_ask on a free session the way it refuses every other Pro
// operation, before any word is matched, so this page does not draw an input a person cannot use: on
// free it draws the quiet pill the Plan page draws, with the same Unlock Pro action. The tier read is
// the Plan card's own (planRankingOf), and `unknown` shows neither, because a paying customer must not
// meet an upsell for one frame while the licence is still being read.
//
// THE REASON IS NEVER THE ANSWER. Kane typed "create a measure" on 21 September and read back "You have
// not given permission to send this model's details for advice." That sentence is true and it is not an
// answer to anything he asked. The engine now always sends a `sentence` a person can read, and `reason`
// carries why it went no further, for the assistant door and the logs. This file shows the sentence and
// never the reason.
//
// The look is the "ask" box from the approved mockup (studio-simplified-mockup.html): an accent-edged
// row with a chevron and one input, and the reply as a plain card under it.
// ===================================================================================================

/** The one method this surface calls. Named once so the wiring test can execute it rather than read it. */
const ASK_METHOD = 'routeAsk';

/** What routeAsk answers with: Semanticus.Engine/Plan/AskRoutePoint.cs, camelCased on the wire by
 *  RpcServer.ConfigureSerializer. Every field is optional here because every one of them is absent on
 *  some real answer, and the difference between absent and empty is the honesty of the reply. */
export interface AskRouteResult {
  /** The place to open, or null when nobody is being sent anywhere. */
  tool?: string | null;
  area?: string | null;
  confidence?: number;
  confidenceBar?: number;
  /** A plain answer the engine worked out from its own state. Null is the ordinary case. */
  sentence?: string | null;
  /** "menu" when the person should be shown the list of places instead of being sent to one. */
  fallback?: string | null;
  /** Why no place was named. FOR THE ASSISTANT DOOR AND THE LOGS, never for the person: it is where the
   *  consent and Pro gates put their own wording, and that wording is about our plumbing. */
  reason?: string | null;
  /** The thing the person asked to make ("measure", "calendar", "role"), or null. Names WHAT, not where
   *  the form is: the door for each thing is ASK_CREATE_DOOR below, because a deep link is a page fact. */
  create?: string | null;
  asked?: boolean;
  servedModelVersion?: string | null;
  /** The OTHER way the same words can be read, when they can be read two ways, and null otherwise. The engine
   *  writes both readings whenever Ask your data is one of them, so neither is ever hidden: the person picks
   *  (coordinator, 28 September). The fields above are the TOP reading. */
  other?: AskReading | null;
  /** Which kind the top reading is, 'task' or 'question'. Null when there is only one reading. */
  reading?: string | null;
  /** Page-owned, and not on the wire: the call itself failed. A failure is an answer a person has to be
   *  told about, so it travels in the same object rather than disappearing. */
  failed?: string;
}

/** One reading of the words: Semanticus.Engine/Plan/AskRoutePoint.cs AskReading, camelCased on the wire. */
export interface AskReading {
  /** 'task' or 'question'. */
  kind?: string | null;
  /** The place this reading is answered on, or null when the words name none, which is ordinary for a task. */
  tool?: string | null;
  area?: string | null;
  confidence?: number;
  /** The one line a person reads for this choice. The engine writes it, this file never does. */
  line?: string | null;
}

/**
 * Every place the engine can send a person, named as the AREA STRIP names them.
 *
 * The ids are the engine's (Semanticus.Engine/Plan/AskRouteCatalog.cs, the tools in TAB_GROUPS).
 * The words are App.tsx's, because this button opens that very tab: being offered "Open Saved reports"
 * and landing on a tab called "Reports" is the page contradicting itself in two adjacent clicks.
 *
 * The engine's four stale labels were closed on 22 September, when the seven areas landed: the catalogue
 * now says Validation, AI readiness, Reports and Pending changes, the same words as the strip. This map
 * is still the page's own, because one entry has no strip button to read a name off: Reports is a TAB on
 * Validation, so its name comes from the catalogue instead. test/ask-box.test.mjs holds both halves.
 */
export const ASK_TOOL_LABEL: Record<string, string> = {
  plan: 'Plan',
  diagram: 'Diagram',
  lineage: 'Lineage',
  search: 'Find and replace',
  data: 'Data',
  askdata: 'Ask your data',
  stats: 'Size',
  spec: 'Model Spec',
  advmodels: 'Advanced modelling',
  mcode: 'Power Query',
  docs: 'Docs',
  knowledge: 'Model notes',
  instructions: 'AI instructions',
  daxlab: 'Queries',
  tests: 'Validation',
  bpa: 'Model quality (BPA)',
  readiness: 'AI readiness',
  evidence: 'Reports',
  optimize: 'Pending changes',
  history: 'History',
  deploy: 'Published',
  workflows: 'Workflows',
};

/**
 * Where each thing is actually MADE, in this build, measured and not assumed.
 *
 * There are three answers and they are all real:
 *
 *   A FORM IN THE STUDIO. Advanced Modelling has a panel per thing and the route already carries them as
 *   `area:<id>` (App.tsx seedForTarget), so the button opens the form itself.
 *   A COMMAND THE MODEL TREE RUNS. A measure, a calculated column, a calculated table, a calculation item
 *   and a function have no Studio form and never did, and they are not unreachable: each is one command
 *   in the extension, the same one the tree's right-click runs, and each resolves its own container
 *   (the tree selection, else a picker) so it works with nothing selected. The button asks the host to
 *   run it, over the runCommand bridge the webview already uses. This is the fix for the thing Kane
 *   typed on 21 September: "create a measure" now opens the real New Measure path.
 *   NOWHERE A BUTTON CAN REACH. A relationship is drawn on the Diagram canvas and a table is a row in a
 *   Model Spec draft. For those the button opens the page and the line promises nothing, because
 *   promising a form that is not built is worse than naming the page and stopping.
 *
 * null in the first table is therefore a MEASUREMENT, not a gap. The day one of the two grows a way in,
 * it moves to a table here and test/ask-box.test.mjs says so.
 */
export const ASK_CREATE_DOOR: Record<string, { tool: string; target: string } | null> = {
  table: null,
  relationship: null,
  'calculation group': { tool: 'advmodels', target: 'area:calcgroups' },
  role: { tool: 'advmodels', target: 'area:rlsols' },
  perspective: { tool: 'advmodels', target: 'area:perspectives' },
  calendar: { tool: 'advmodels', target: 'area:calendars' },
};

/**
 * The five the Model tree makes, and the command that makes each one.
 *
 * `tool` is the place the ENGINE says owns the thing, and a command is only offered when the engine's
 * own answer named that place, so a stale entry here cannot run something the answer line did not say.
 * `line` is the whole answer a person reads: it says what is about to happen in the order it happens,
 * because "DAX Lab is the place for that" was true and useless.
 * The command ids are package.json contributes.commands, and test/ask-create-command.test.mjs holds
 * these five to that file and to the handlers, so a renamed command fails there rather than in front of
 * a person pressing a button that does nothing.
 */
export const ASK_CREATE_COMMAND: Record<string, { tool: string; command: string; button: string; line: string }> = {
  measure: {
    tool: 'daxlab', command: 'semanticus.newMeasure', button: 'New measure',
    line: 'New measure: pick the table, then write the formula.',
  },
  'calculated column': {
    tool: 'daxlab', command: 'semanticus.newCalcColumn', button: 'New calculated column',
    line: 'New calculated column: pick the table, then write the formula.',
  },
  'calculated table': {
    tool: 'daxlab', command: 'semanticus.newCalcTable', button: 'New calculated table',
    line: 'New calculated table: it opens with a starter formula for you to replace.',
  },
  function: {
    tool: 'daxlab', command: 'semanticus.newFunction', button: 'New function',
    line: 'New function: it opens with a starter formula for you to replace.',
  },
  'calculation item': {
    tool: 'advmodels', command: 'semanticus.newCalcItem', button: 'New calculation item',
    line: 'New calculation item: pick the calculation group, then write the formula.',
  },
};

/** How the send is made. The component passes the bridge; a test passes its own and reads what went. */
export type AskSend = (method: string, ...params: unknown[]) => Promise<AskRouteResult>;

const bridgeSend: AskSend = (method, ...params) => rpc<AskRouteResult>(method, ...params);

/**
 * One ask, one call.
 *
 * The text goes on the wire exactly as it was typed. Nothing here trims it, expands it, or decides it is
 * too empty to bother the engine with: RouteAskAsync answers a blank ask with its own plain sentence
 * before it asks anything of anybody, so a page guess would only be a second set of words saying the
 * same thing less well.
 *
 * A thrown call comes back as a result carrying `failed`, because the box has to say what happened and
 * an exception that reaches React says nothing to anyone.
 */
export async function runAsk(text: string, send: AskSend = bridgeSend): Promise<AskRouteResult> {
  try {
    const result = await send(ASK_METHOD, text);
    return result ?? { failed: 'The engine answered with nothing at all.' };
  } catch (error) {
    return { failed: error instanceof Error ? error.message : String(error) };
  }
}

/**
 * What the one button under the answer does, or null when there is no button.
 *
 * Two shapes, because there are two honest things a button can do here: OPEN a place (with a deep link
 * to a form when the thing being made has one), or RUN the extension command that makes the thing. An id
 * the box does not know is not a place: the engine only ever names one it knows, so anything else is a
 * drift and printing it raw would show a person a wire string.
 */
export type AskAction =
  | { kind: 'open'; tool: string; label: string; target?: string; form?: string }
  | { kind: 'run'; command: string; label: string; line: string };

export function askAction(result: AskRouteResult | null, asked = ''): AskAction | null {
  const tool = result?.tool;
  if (!tool) return null;
  const label = ASK_TOOL_LABEL[tool];
  if (!label) return null;
  // A create answer runs the real command where there is one, opens the form where there is one, and
  // opens the page where there is neither. Each door has to agree with the tool the engine named, or the
  // button would quietly do something other than the line above it says.
  const made = result?.create ? ASK_CREATE_COMMAND[result.create] : undefined;
  if (made && made.tool === tool) return { kind: 'run', command: made.command, label: made.button, line: made.line };
  const door = result?.create ? ASK_CREATE_DOOR[result.create] : null;
  if (door && door.tool === tool) return { kind: 'open', tool, label, target: door.target, form: result!.create! };
  return openTool(tool, label, asked);
}

/** The words the person typed, carried into Ask your data so the page opens on the question rather than empty
 *  (verify-askdata-r4, still open at r5). Only Ask your data takes them: no other page has a words box to put
 *  them in, and a target it does not understand would be a hand-off that quietly does nothing. */
export const ASK_WORDS_TARGET = 'words:';

/** The words Open Ask your data carries: what was typed, on one line, because the question box is one line and a line
 *  break typed with Shift+Enter would otherwise run two words together. A line that only NAMES the page ("go to ask
 *  your data", "I need ask your data") asked for the page and not a question on it, so it carries nothing and the page
 *  opens empty (verify-askdata-r6 L4). */
export function carriedWords(asked: string): string {
  const words = asked.replace(/\s+/gu, ' ').trim();
  const plain = words.toLowerCase().replace(/[^\p{L}\p{N}]+/gu, ' ').trim();
  return ONLY_THE_PAGE.test(plain) ? '' : words;
}

/** The page's name with nothing around it but asking for the page: "open", "go to", "use", "I would like to", "please",
 *  "for me". Only these words, so a line with anything else in it keeps its words. */
const ONLY_THE_PAGE = /^(?:(?:please|just|now|can|could|would|will|you|i|we|d|need|want|like|to|go|take|bring|me|us|open|use|show|see|get|the|page|tab)\s+)*ask your data(?:\s+(?:page|tab|please|now|for|me|us|thanks|thank|you))*$/u;

function openTool(tool: string, label: string, asked: string): AskAction {
  const words = tool === 'askdata' ? carriedWords(asked) : '';
  return tool === 'askdata' && words
    ? { kind: 'open', tool, label, target: ASK_WORDS_TARGET + words }
    : { kind: 'open', tool, label };
}

/**
 * The SECOND CHOICE under the answer, or null when there is only one reading.
 *
 * The engine names both readings whenever the same words can be a task and a question, and the page shows both
 * (coordinator, 28 September). This is the button for the one that is not first. A task reading with no page is
 * still a real choice and still shows its line; it simply has no button, because Enter is what takes it.
 */
export function askOther(result: AskRouteResult | null, asked = ''): AskAction | null {
  const tool = result?.other?.tool;
  if (!tool) return null;
  const label = ASK_TOOL_LABEL[tool];
  return label ? openTool(tool, label, asked) : null;
}

/**
 * The press. One place decides what the button does, so the answer line and the button cannot disagree.
 *
 * A command goes to the host over the runCommand bridge the webview already uses (bridge.ts
 * runHostCommand), and NOTHING ELSE happens: no tab change, because moving a person away from the page
 * while a picker opens over it is two things happening at once. `run` is a parameter so a test executes
 * this rather than reading it.
 */
export function openAskAnswer(going: AskAction | null, onGo: (tool: string, target?: string) => void,
  run: (command: string) => void = runHostCommand): void {
  if (!going) return;
  if (going.kind === 'run') { run(going.command); return; }
  onGo(going.tool, going.target);
}

/**
 * The one line a person reads.
 *
 * In order: the engine's sentence when it worked the answer out from its own numbers; otherwise the name
 * of the place it is sending them, which is the whole answer to "where do I do this"; otherwise the
 * engine's own reason for sending them nowhere, word for word. The last line is the one that must never
 * be improved upon. "Nothing here matched that well enough to send you anywhere" is honest, and an
 * apology written on top of it would be the page pretending to know why.
 */
export function askAnswerLine(result: AskRouteResult | null): string {
  if (!result) return '';
  if (result.failed) return result.failed;
  // The engine's own sentence first, where it has one: a question it answered from its own numbers is
  // answered with the number, not with the name of a page that also happens to fit.
  if (result.sentence && result.sentence.trim()) return result.sentence.trim();
  const going = askAction(result);
  if (going) {
    // A thing the Model tree makes answers with what is about to happen, not with the name of a page.
    if (going.kind === 'run') return going.line;
    const place = `${going.label} is the place for that.`;
    // The second sentence exists only where the button really opens a form. Saying "Create a table
    // there" about a page with no table form would be the page promising something it cannot do.
    return going.form ? `${place} Create a ${going.form} there.` : place;
  }
  return 'The engine did not say where to send you, and gave no reason for it.';
}

/** The reply under the box. Exported so it can be rendered on its own, one shape at a time.
 *
 *  `asked` is what the person typed, and it exists for one reason: Open Ask your data carries the words onto that
 *  page instead of opening it empty. */
export function AskReply({ result, onGo, asked = '' }: {
  result: AskRouteResult | null;
  onGo: (tool: string, target?: string) => void;
  asked?: string;
}) {
  if (!result) return null;
  const line = askAnswerLine(result);
  const going = askAction(result, asked);
  const other = askOther(result, asked);
  if (result.failed) {
    return <div className="ask-reply ask-reply-failed" role="alert" data-testid="ask-reply">{line}</div>;
  }
  // Instrument: the answer is a line under the box with a rule down its left, not a box of its own. The box
  // it answers is right above it, so a second box would only be a card standing in the page's gap.
  // One ROW per reading, stacked. A reading is a line and the one way through to it, and putting a second reading's
  // line and button on the same row as the first's spread four things across the page with nothing saying which
  // belonged to which.
  // aria-live on the whole reply, so the second choice is spoken with the first when the answer arrives: the person's
  // focus is still in the input, and a choice nobody is told about is not offered to them (verify-askdata-r6 L9).
  return <div className="ask-reply" data-testid="ask-reply" aria-live="polite">
    <div className="ask-reply-row">
      <p className="ask-reply-line">{line}</p>
      {going && <button type="button" className="sem-btn sem-btn-sm shrink-0" data-testid="ask-jump"
        data-target={going.kind === 'open' ? going.target ?? '' : ''}
        data-command={going.kind === 'run' ? going.command : ''}
        onClick={() => openAskAnswer(going, onGo)}>{going.kind === 'run' ? going.label : `Open ${going.label}`}</button>}
    </div>
    {/* The other reading, when the same words can be read two ways. Its line is the engine's, never written here. */}
    {result.other?.line && <div className="ask-reply-row ask-reply-second" data-testid="ask-other">
      <p className="ask-reply-line">{result.other.line}</p>
      {other && <button type="button" className="sem-btn sem-btn-sm shrink-0" data-testid="ask-other-jump"
        data-target={other.kind === 'open' ? other.target ?? '' : ''}
        onClick={() => openAskAnswer(other, onGo)}>Open {other.label}</button>}
    </div>}
  </div>;
}

/**
 * The page the Plan's field shows for the TASK in an answer, which Enter sends with the words so the row names the same
 * page (verify-askdata-r6 F1). The one reading's place where there is one reading (never Ask your data, which answers
 * questions and is never a task's page), and the task reading's place where there are two. Null when the field shows
 * no page for the task. Semanticus.Tests/AskRouteTests.cs FieldTaskPage is the same rule on the engine's answers.
 */
export function fieldTaskPage(answer: AskRouteResult | null | undefined): string | null {
  if (!answer || answer.failed) return null;
  if (!answer.reading) return answer.tool && answer.tool !== 'askdata' ? answer.tool : null;
  if (answer.reading === 'task') return answer.tool ?? null;
  return answer.other?.kind === 'task' ? answer.other.tool ?? null : null;
}

/** How long the Plan's field waits after the last key before it asks where the words are done. */
export const ASK_SUGGEST_MS = 700;
/** Fewer characters than this are not a question yet, so nothing is asked. */
export const ASK_SUGGEST_MIN = 8;

/**
 * What the Plan's field shows for one answer, or null for nothing. A place named, or a sentence the engine answered
 * from its own numbers, is a suggestion. An answer that names no place and falls back to the menu is not: the field
 * is "Describe a task or an aim", so words nothing placed are simply a task, and Enter keeps them as one. Telling a
 * person "Nothing here matches those words yet. Try a page name" under a task they are typing would be advice for a
 * different box (fix-askdata-r5, V1: when no page placed the line, show no suggestion and keep the words as a task).
 * A failed answer is not a suggestion either: the field says nothing rather than an error under a row.
 *
 * An answer with TWO READINGS is always a suggestion, even where the first one names no page: the point of it is
 * the choice, and "Keep it as a task. Semanticus does not know a page for this one." over "Or ask it as a question." is
 * the honest pair for a
 * line the words placed on Ask your data but that reads as a task (coordinator, 28 September).
 */
export function fieldSuggestion(answer: AskRouteResult | null | undefined): AskRouteResult | null {
  if (!answer || answer.failed) return null;
  if (answer.tool || answer.other) return answer;
  if (!answer.sentence || answer.fallback === 'menu') return null;
  return answer;
}

/**
 * The same route suggestion, for the Plan's one field (Plan at the heart, lane U1; contract 11.1: "typing shows the
 * ask box's route suggestions, Enter adds a row"). It only ever NAMES a place, exactly as the box does: the field's
 * Enter adds the row and never follows the suggestion. Pass '' to ask for nothing (a free session, an empty field).
 *
 * AN ANSWER IS SHOWN ONLY FOR THE WORDS IT ANSWERS. An answer to older words is dropped when newer words have been
 * typed, and it is not shown while they are: it used to stay up through the wait and the call, with its button already
 * carrying the newer words (verify-askdata-r6 L10). The words are compared as sent, so pass the same text Enter adds.
 */
export function useAskSuggestion(text: string, send: AskSend = bridgeSend): AskRouteResult | null {
  const [answered, setAnswered] = useState<{ words: string; result: AskRouteResult | null } | null>(null);
  const asked = useRef(0);
  const current = text.trim();
  useEffect(() => {
    const words = text.trim();
    const mine = ++asked.current;
    if (words.length < ASK_SUGGEST_MIN) { setAnswered(null); return undefined; }
    const timer = setTimeout(() => {
      void runAsk(words, send).then((answer) => {
        if (mine !== asked.current) return;
        setAnswered({ words, result: fieldSuggestion(answer) });
      });
    }, ASK_SUGGEST_MS);
    return () => clearTimeout(timer);
  }, [text, send]);
  return answered && answered.words === current ? answered.result : null;
}

// ===================================================================================================
// Ctrl J on every page (lane U5; composer contract 10.2 and 10.7): the words become a drafted task.
//
// Ctrl J opens one line under the header, whatever page is open. Whatever is typed drafts: compose_task with the
// words, and the Plan opens with that draft's row open. On Pro, Jev drafts its steps (JevDraftsOnPlan), or the draft
// opens as the menu of steps when Jev cannot be asked (lane L5a); either way the line promises only what happens:
// the draft opens in the Plan. Free shows the Plan's Pro
// line and nothing else, and counts nothing. Esc closes it.
//
// CTRL J DOES NOT ROUTE (Kane, 27 Sep: Ctrl J's question routing is out of this release). It used to send words that
// read as a question to route_ask, and three verify rounds each found a task that was routed and lost ("Month Name
// should sort by Month Number"). Now nothing is routed, so no task can be lost; asking where something is lives in
// the Plan's field (useAskSuggestion above) and in route_ask on the assistant door.
// ===================================================================================================

export const ASK_BAR_PLACEHOLDER = 'Describe a task';
export const ASK_BAR_HINT = 'Esc closes it';
/** While the draft is made. Honest about what happens next: the Plan opens with the draft. */
export const ASK_BAR_DRAFTING = 'Drafting. Opening it in the Plan.';

/** Ctrl J (Cmd J on a Mac) with no other modifier. */
export function isAskKey(e: { ctrlKey: boolean; metaKey: boolean; altKey: boolean; shiftKey: boolean; key: string }): boolean {
  const mac = typeof navigator !== 'undefined' && /Mac/i.test(navigator.platform ?? '');
  const mod = mac ? e.metaKey : e.ctrlKey;
  return mod && !e.altKey && !e.shiftKey && (e.key === 'j' || e.key === 'J');
}

export function AskBar({ onDrafted, onClose, initialText = '' }: {
  /** Words carried in from the field Ctrl J was pressed in (journey m11 F7). */
  initialText?: string;
  /** A task was drafted: open the Plan with this draft. */
  onDrafted: (draftId: string) => void;
  onClose: () => void;
}) {
  const grant = planRankingOf(useTier());
  const [text, setText] = useState(initialText);
  const [busy, setBusy] = useState(false);
  const [failed, setFailed] = useState<string | null>(null);
  const generation = useRef(0);
  // One request at a time, set before the first await: a second Enter while a draft is on its way sends nothing, so
  // it never drafts the task twice (Sol on slice 10, F-289). State is too late for this, because two key presses can
  // land before React draws the busy state.
  const inFlight = useRef(false);
  const input = useRef<HTMLInputElement | null>(null);
  useEffect(() => { input.current?.focus(); }, []);

  async function submit() {
    const words = text.trim();
    if (!words || inFlight.current) return;
    inFlight.current = true;
    const mine = ++generation.current;
    setFailed(null); setBusy(true);
    try {
      const view = await rpc<{ draftId?: string } | null>('composeTask', words, null, null, null, null, 'human');
      if (mine !== generation.current) return;
      if (view?.draftId) { onDrafted(view.draftId); onClose(); }
      else setFailed('The engine made no draft.');
    } catch (error) {
      if (mine === generation.current) setFailed(error instanceof Error ? error.message : String(error));
    } finally {
      if (mine === generation.current) setBusy(false);
      inFlight.current = false;
    }
  }

  return <section className="cd-askbar" data-testid="ask-bar" aria-label={ASK_BAR_PLACEHOLDER}
    onKeyDown={(e) => { if (e.key === 'Escape') { e.preventDefault(); e.stopPropagation(); onClose(); } }}>
    {grant === 'denied' ? <div className="ask-pro" data-testid="ask-bar-pro">
      <span className="ask-pro-tag">Pro</span>
      <span className="ask-pro-line">{DRAFT_PRO_LINE}</span>
      <button type="button" className="sem-btn sem-btn-sm sem-btn-primary shrink-0" onClick={upgradeToPro}>Unlock Pro</button>
      <button type="button" className="sem-btn sem-btn-sm pw-quiet shrink-0" ref={(b) => b?.focus()} onClick={onClose}>Close</button>
    </div>
      : grant === 'unknown' ? <div className="ask-hint">{PENDING_ACCESS}</div>
      : <>
        <form className="cd-askbar-row" onSubmit={(e) => { e.preventDefault(); void submit(); }}>
          <span aria-hidden="true" className="ask-field-mark">›</span>
          <input ref={input} className="cd-askbar-input" data-testid="ask-bar-input" value={text} placeholder={ASK_BAR_PLACEHOLDER}
            aria-label={ASK_BAR_PLACEHOLDER} disabled={busy} onChange={(e) => setText(e.target.value)} />
          {busy && <span className="sem-spin shrink-0" />}
          <span className="plan-muted shrink-0">{busy ? ASK_BAR_DRAFTING : ASK_BAR_HINT}</span>
        </form>
        {failed && <div className="ask-reply ask-reply-failed" role="alert" data-testid="ask-bar-failed">Nothing was drafted. {failed}</div>}
      </>}
  </section>;
}

/** Composer contract 10.7: the Plan's line, which Ctrl J shows on free. */
export const DRAFT_PRO_LINE = WORD.proLine;
