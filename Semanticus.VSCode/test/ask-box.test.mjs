// The ask box: the person's door to route_ask. It was a card on the Overview until slice 11 (Plan-heart lane R1);
// since then it is the Plan's field (typing shows the route suggestion) and Ctrl J, the ask box on every page.
//
// Astra finding 14 (review 2026-09-21): route_ask shipped on the agent door only. `routeAsk(text)` was
// on EngineRpcTarget and nothing in the extension or the webview ever called it, so an agent could ask
// where to go and a person could not. Publishing an RPC method is not shipping a capability, and the
// golden rule in CLAUDE.md is that every capability reaches both doors on one session.
//
// What each check here actually proves, because the three are not equally strong:
//   1. THE CALL. `runAsk` is the whole send, exported on its own, so the method name and the text on the
//      wire are executed here rather than eyeballed. The component calls nothing else.
//   2. THE REPLY. AskReply is rendered for real with react-dom/server, once per shape the engine can
//      answer with, and what a person reads is read back out of the markup.
//   3. THE WIRING. Read off the real source: the form submits into runAsk, the jump presses onGo, and
//      the Plan and the shell mount the two ways in.
// There is no DOM in this suite (no jsdom is installed), so the real typing and the real Enter are
// proved in a real browser by tools/uishot/cp-plan-drive.mjs, scenes ask-empty, ask-answered,
// ask-routed and ask-not-known, which record the params routeAsk was called with.
//
// The reply shapes come from Semanticus.Engine/Plan/AskRoutePoint.cs (AskRouteResult) and the tool ids
// and labels from Semanticus.Engine/Plan/AskRouteCatalog.cs, which this file reads so the box and the
// engine cannot drift apart about what the places are called or about how each thing gets made.
import assert from 'node:assert/strict';
import { readFileSync, existsSync } from 'node:fs';
import { dirname, resolve, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createRequire } from 'node:module';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const repoRoot = resolve(root, '..');
const webview = join(root, 'webview');
const req = createRequire(join(webview, 'package.json'));
const ts = req('typescript');
const React = req('react');
const { renderToStaticMarkup } = req('react-dom/server');

// The same tiny CommonJS loader the Plan page test uses, with the host seams stubbed.
const STUBS = {
  './bridge': {
    rpc: async () => { throw new Error('the test loader answers no calls; pass a send, or drive the app'); },
    onDidChange: () => () => {}, onProgress: () => () => {}, revealInTree: () => {},
    focusSelectInProperties: () => {}, selectInProperties: () => {}, runHostCommand: () => {},
  },
  './connection': { useConnection: () => ({ session: { sessionId: 'test' }, context: null }) },
  './pro': {
    // The box reads the tier now (Kane, 22 Sep: ask is Jev, so it is Pro), so the stub has one that the
    // tests below move. It starts on 'pro' because most of this file is about the answer, not the gate.
    useTier: () => TIER.now,
    useFeature: () => 'denied',
    isEntitlementError: () => false, ProBadge: () => null, UpsellNotice: () => null,
    PENDING_ACCESS: 'Checking your Pro access…', upgradeToPro: () => { TIER.unlocked += 1; },
  },
};
const TIER = { now: 'pro', unlocked: 0 };
const cache = new Map();
function load(file) {
  const full = file.endsWith('.ts') || file.endsWith('.tsx') || file.endsWith('.mjs') || file.endsWith('.js') ? file
    : existsSync(file + '.tsx') ? file + '.tsx' : file + '.ts';
  if (cache.has(full)) return cache.get(full);
  const js = ts.transpileModule(readFileSync(full, 'utf8'), {
    compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022, jsx: ts.JsxEmit.ReactJSX, esModuleInterop: true },
    fileName: full,
  }).outputText;
  const module = { exports: {} };
  cache.set(full, module.exports);
  const localRequire = (name) => {
    if (STUBS[name]) return STUBS[name];
    if (name.startsWith('.')) return load(resolve(dirname(full), name));
    return req(name);
  };
  // eslint-disable-next-line no-new-func
  new Function('require', 'module', 'exports', js)(localRequire, module, module.exports);
  cache.set(full, module.exports);
  return module.exports;
}

/** What a person can actually read: the text, plus the label a screen reader speaks. */
const readable = (html) => html
  .replace(/\s(?:class|style|data-[\w-]+|value|type|id|for|role|placeholder|disabled|aria-live|aria-controls|aria-expanded)="[^"]*"/g, '')
  .replace(/<[^>]+>/g, ' ')
  .replace(/&quot;/g, '"').replace(/&#x27;/g, "'").replace(/&amp;/g, '&').replace(/&#x2F;/g, '/')
  .replace(/\s+/g, ' ')
  .trim();

const A = load(join(webview, 'src', 'askbox.tsx'));
const askSrc = readFileSync(join(webview, 'src', 'askbox.tsx'), 'utf8');
const planSrc = readFileSync(join(webview, 'src', 'plan.tsx'), 'utf8');
const appSrc = readFileSync(join(webview, 'src', 'App.tsx'), 'utf8');

// ===================================================================================================
// 1. The call. One method, one argument, and the text exactly as it was typed.
// ===================================================================================================
const calls = [];
const send = async (method, ...params) => { calls.push({ method, params }); return { tool: 'stats', area: 'model' }; };

await A.runAsk('how big is this model', send);
assert.equal(calls.length, 1, 'one ask is one call');
assert.equal(calls[0].method, 'routeAsk', 'finding 14: the box calls the operation the agent door already had');
assert.deepEqual(calls[0].params, ['how big is this model'],
  'and it sends the typed text, unchanged: nothing here rewrites what a person asked');

// Blank goes to the engine too. RouteAskAsync answers a blank ask with its own plain sentence before it
// asks anything of anybody (LocalEngine.Plan.cs), so this costs nothing and the words stay the
// engine's rather than a second set invented on the page.
calls.length = 0;
await A.runAsk('', send);
assert.deepEqual(calls[0].params, [''], 'a blank ask is still the engine answer, not a page guess');

// A failure is an answer a person can read, not a silent nothing.
const broke = await A.runAsk('anything', async () => { throw new Error('The engine did not answer.'); });
assert.equal(broke.failed, 'The engine did not answer.', 'a thrown call comes back as its message');

// ===================================================================================================
// 2. The reply, rendered. Four shapes, and every one of them is a state the engine really returns.
// ===================================================================================================
const render = (result) => renderToStaticMarkup(React.createElement(A.AskReply, { result, onGo: () => {} }));

// (a) Answered from the engine's own numbers: a sentence AND a place to carry on (Answered() in
//     AskRoutePoint.cs sets both, with confidence 1 and asked false).
const knownResult = {
  tool: 'stats', area: 'model', confidence: 1, confidenceBar: 0.85, asked: false,
  sentence: 'Contoso Retail has 5 tables and 39 measures. It is 122 MB in memory.',
};
const known = render(knownResult);
assert.match(readable(known), /Contoso Retail has 5 tables and 39 measures\. It is 122 MB in memory\./,
  'the answer line is the engine sentence, word for word');
assert.match(known, /data-testid="ask-jump"/, 'and the jump to the page is beside it');
assert.match(readable(known), /Open Size/, 'the jump names the place in the words the strip uses');

// (b) Routed with no sentence, which is the ordinary case: the place IS the answer, so the line says
//     which place and the jump opens it.
const routed = render({ tool: 'lineage', area: 'model', confidence: 0.93, confidenceBar: 0.85, asked: true });
assert.match(readable(routed), /Open Lineage/);
assert.match(readable(routed), /Lineage is the place for that\./,
  'a route with no sentence still gets one answer line, and it says where');

// (c) Nothing matched. The engine's SENTENCE, and never its REASON.
//
// This is the bug Kane hit on 21 September. He typed "create a measure" on a session with no
// permission and read "You have not given permission to send this model's details for advice." That
// sentence is true, and it is not an answer to anything he asked: it is the gate talking about itself.
// The engine now always sends a sentence a person can read and puts the gate's own words in `reason`,
// which is for the assistant door and the logs. This box shows the first and never the second.
const unsure = {
  tool: null, area: null, confidence: 0, confidenceBar: 0.85, asked: false, fallback: 'menu',
  sentence: 'Nothing here matches those words yet. Try a page name, or the name of a thing in the model.',
  reason: 'You have not given permission to send this model\u2019s details for advice.',
};
const notKnown = render(unsure);
assert.match(readable(notKnown), /Nothing here matches those words yet\./,
  'the person reads the plain line about their words');
assert.doesNotMatch(readable(notKnown), /permission/i,
  'and never the gate reason: that is the exact sentence Kane was handed as an answer');
assert.doesNotMatch(notKnown, /data-testid="ask-jump"/, 'and no jump, because there is nowhere to send anyone');
assert.ok(!/\bSorry\b|\bOops\b|\bcould not understand\b/i.test(readable(notKnown)),
  'the page adds no apology of its own on top of the engine sentence');
// A reason with no sentence is a reply with nothing readable in it, and is reported as such rather
// than printing the reason after all.
assert.match(readable(render({ tool: null, fallback: 'menu', reason: 'suggestions like this are Pro' })),
  /did not say/, 'a reason alone is not an answer');

// (c2) A CREATE answer. The line names the thing and the button opens the form, but only where a form
//      exists: four of the eight things Studio can be asked to make have no create form at all.
const madeCalendar = render({ tool: 'advmodels', area: 'model', confidence: 1, asked: false, create: 'calendar' });
assert.match(readable(madeCalendar), /Advanced modelling is the place for that\. Create a calendar there\./);
assert.match(madeCalendar, /data-target="area:calendars"/, 'the button opens the calendars panel, not just the page');

// (c3) A CREATE the Model tree makes. This is the one Kane typed. The Studio has no measure form and it
//      never did, but the product HAS a create-a-measure path: the Model tree's own New Measure command,
//      which the extension runs and which asks for the table when none is selected. So the button runs
//      that command instead of changing tab, and the line says plainly what is about to happen.
const madeMeasure = render({ tool: 'daxlab', area: 'calc', confidence: 1, asked: false, create: 'measure' });
assert.match(readable(madeMeasure), /New measure: pick the table, then write the formula\./,
  'the line says what will happen, not the name of a page that cannot do it');
assert.doesNotMatch(readable(madeMeasure), /Queries is the place for that/,
  'and it no longer names a page whose only answer to "create a measure" is that it cannot');
assert.match(madeMeasure, /data-command="semanticus\.newMeasure"/, 'the button runs the tree command');
assert.match(readable(madeMeasure), /New measure/, 'and it is labelled as the thing it makes');

// The two tables are a MEASUREMENT of this build: four things have a panel, five are made by a command
// the Model tree runs, and two have neither. When one of the two grows a way in it gets an entry and
// this check says so instead of going quiet.
assert.deepEqual(Object.entries(A.ASK_CREATE_DOOR).filter(([, d]) => d === null).map(([k]) => k).sort(),
  ['relationship', 'table'],
  'a relationship is drawn on the Diagram canvas and a table is a row in a Model Spec draft: no form, no command');
for (const [thing, door] of Object.entries(A.ASK_CREATE_DOOR)) {
  if (!door) continue;
  assert.ok(A.ASK_TOOL_LABEL[door.tool], `${thing} opens a place the box knows`);
  assert.match(door.target, /^area:/, 'a deep link is the advmodels area seed App.tsx already carries');
}
assert.deepEqual(Object.keys(A.ASK_CREATE_COMMAND).sort(),
  ['calculated column', 'calculated table', 'calculation item', 'function', 'measure'],
  'the five the Model tree makes, and no others');
for (const [thing, made] of Object.entries(A.ASK_CREATE_COMMAND)) {
  assert.match(made.command, /^semanticus\.new/, `${thing} runs one of the tree create commands`);
  assert.ok(A.ASK_TOOL_LABEL[made.tool], `${thing} belongs to a place the box knows`);
  assert.ok(made.line.endsWith('.'), `${thing} says what will happen in a whole sentence`);
  assert.ok(!/—|–/.test(made.line + made.button), 'no em dashes, anywhere');
}
// A create door is ignored when it does not belong to the place the engine named, so a stale table
// cannot send a person somewhere the answer line did not say.
assert.doesNotMatch(render({ tool: 'lineage', confidence: 1, create: 'calendar' }), /data-target="area:calendars"/);
assert.doesNotMatch(render({ tool: 'lineage', confidence: 1, create: 'measure' }), /data-command="semanticus/);

// (c4) THE BUTTON ITSELF, executed. A create the tree makes must SEND THE COMMAND and must not change
//      tab, which is the whole of Kane's complaint: "create a measure" used to move him to a page that
//      cannot make one. Both halves are asserted, because sending the command AND switching tab would
//      pass a check that only looked for the command.
const ran = [];
const went = [];
const press = (result) => A.openAskAnswer(A.askAction(result), (tool, target) => went.push([tool, target]),
  (command) => ran.push(command));

press({ tool: 'daxlab', area: 'calc', confidence: 1, create: 'measure' });
assert.deepEqual(ran, ['semanticus.newMeasure'], 'the Open button asks the extension to run the tree command');
assert.deepEqual(went, [], 'and nobody is moved to another tab');

press({ tool: 'advmodels', area: 'model', confidence: 1, create: 'calendar' });
assert.deepEqual(went, [['advmodels', 'area:calendars']], 'a thing with a form still opens its form');
assert.deepEqual(ran, ['semanticus.newMeasure'], 'and runs no command');

press({ tool: 'lineage', area: 'model', confidence: 0.93 });
assert.deepEqual(went[1], ['lineage', undefined], 'an ordinary route is still an ordinary tab jump');

for (const [thing, made] of Object.entries(A.ASK_CREATE_COMMAND)) {
  ran.length = 0;
  press({ tool: made.tool, confidence: 1, create: thing });
  assert.deepEqual(ran, [made.command], `${thing} runs its own command and no other`);
}

// (d) An answer with neither a place nor a reason cannot be drawn as a success. This should not happen
//     (every RouteAskAsync path that names no tool sets Reason), so the box says exactly that much.
const bare = render({ tool: null, fallback: 'menu' });
assert.match(readable(bare), /did not say/, 'a reply with nothing in it is reported, never rendered blank');

// The failure from part 1 draws as an error a person can read.
const failed = renderToStaticMarkup(React.createElement(A.AskReply, {
  result: { failed: 'The engine did not answer.' }, onGo: () => {},
}));
assert.match(failed, /role="alert"/);
assert.match(readable(failed), /The engine did not answer\./);

// ===================================================================================================
// 3. The places. The IDS are the engine's; the WORDS are the area strip's.
//
// The engine can only ever name one it knows, so the id set must match AskRouteCatalog.cs exactly.
// The label is a different question: the jump opens a tab, and what the button promises has to be what
// that tab is called when a person lands on it, which is App.tsx's TAB_GROUPS.
//
// THE STRIP IS NOT THE WHOLE CATALOGUE ANY MORE. From 22 September a catalogue entry may be a TAB INSIDE
// another page rather than a button on the strip (AskRouteCatalog.cs:108-111 flags it `isTab`). Reports is
// the first: it lives on Validation. It is still a real destination and still routable, so the box must
// know it, but the strip cannot show it. The rule is therefore two-sided instead of an equal count:
//   every catalogue entry NOT flagged as a tab is on the strip, and every strip tool is in the catalogue.
// A tab entry's promised name is checked against the CATALOGUE's own label, because there is no strip
// button to read it off. Equality of counts would send the next tab-shaped page back into hiding.
// ===================================================================================================
const catalogSrc = readFileSync(join(repoRoot, 'Semanticus.Engine', 'Plan', 'AskRouteCatalog.cs'), 'utf8');
// Split on the constructor so `isTab` is read PER ENTRY. A regex over the whole file can see the flag but
// cannot tell whose it is, which is how a mis-flagged tool would pass unnoticed.
const catalog = catalogSrc.split('new AskRouteTool(').slice(1).map((chunk) => {
  const head = /^"([^"]+)", "([^"]+)", "([^"]+)",/.exec(chunk);
  return head ? { id: head[1], area: head[2], label: head[3], isTab: /\bisTab:\s*true\b/.test(chunk) } : null;
}).filter(Boolean);
assert.equal(catalog.length, 22, 'the catalogue is every place the box can send anybody. It was twenty '
  + 'until 22 September, when Plan was found to be in the strip and in no catalogue, then twenty-two when '
  + 'the seven areas landed with AI instructions and Reports, then twenty-one when the Overview retired in slice 11, '
  + 'then twenty-two again when Ask your data became a page under Explore (26 September).');
const stripEntries = catalog.filter((t) => !t.isTab);
const tabEntries = catalog.filter((t) => t.isTab);
// Four places are a tab or a section inside another page rather than a button on the strip, and each one
// is named here so a fifth cannot appear by accident. Reports was the first, on Validation. Pending
// changes and Model Spec joined it on 23 September when Kane folded the Plan area's three pages into one
// ("No point in three similar pages, it makes it very confusing for users"), and Model notes joined it
// as a tab on AI instructions.
//
// THIS LINE IS THE END STATE OF THREE LANES and it is red until all three land: this one folds the Plan
// row, a sibling lane folds Model notes, and the ENGINE lane sets isTab on those three entries in
// AskRouteCatalog.cs. Until the engine lane lands the catalogue still calls them strip entries and the
// two-sided rule below fails, which is the check working and not a check to weaken.
// History joined them in slice 11 (lane R2): Edit History became the History view inside the Plan.
assert.deepEqual(tabEntries.map((t) => t.id).sort(), ['evidence', 'history', 'knowledge', 'optimize', 'spec'],
  'a place that is a tab or a section inside another page needs its own line here');
assert.deepEqual(Object.keys(A.ASK_TOOL_LABEL).sort(), catalog.map((t) => t.id).sort(),
  'the box knows every place the engine can name, and no place it cannot');

const groupsBlock = /const TAB_GROUPS[\s\S]*?\n\];/.exec(readFileSync(join(webview, 'src', 'App.tsx'), 'utf8'));
assert.ok(groupsBlock, 'TAB_GROUPS must still be findable in App.tsx');
const strip = new Map([...groupsBlock[0].matchAll(/\{ id: '([a-z]+)', label: '([^']+)' \}/g)]
  .map((m) => [m[1], m[2]]));
// Twenty-one until 23 September, then eighteen: minus Pending changes and Model Spec (folded into Plan)
// and minus Model notes (a tab on AI instructions). Sixteen since slice 11: minus the Overview and Edit History.
// Seventeen since 26 September: plus Ask your data, after Data in Explore.
assert.equal(strip.size, 17, `the strip is the catalogue minus the places that are a tab, found ${strip.size}`);
assert.deepEqual([...strip.keys()].sort(), stripEntries.map((t) => t.id).sort(),
  'every catalogue entry that is not a tab is on the strip, and every strip tool is in the catalogue');
for (const tool of stripEntries) {
  assert.equal(A.ASK_TOOL_LABEL[tool.id], strip.get(tool.id),
    `${tool.id} must be offered by the name the tab actually has`);
}
for (const tool of tabEntries) {
  assert.equal(A.ASK_TOOL_LABEL[tool.id], tool.label,
    `${tool.id} is a tab inside another page, so the box must offer it by the catalogue's own name for it`);
}
const stale = stripEntries.filter((tool) => tool.label !== strip.get(tool.id))
  .map((tool) => `${tool.id}: engine "${tool.label}", strip "${strip.get(tool.id)}"`);
assert.deepEqual(stale, [], 'the engine catalogue names a place by a word the strip does not use. AskRouteCatalog.cs claims its labels come from TAB_GROUPS; bring it back in step with the strip, do not delete the check. (Four labels were stale when the ask box was built, 21 September 2026; the engine fixed them the same day.)');
// Every thing the engine can NAME has exactly one way to be made here, and it agrees with the engine
// about which place owns it. A thing added to the catalogue with nowhere to make it fails here, which is
// the drift that let "create a measure" answer with a page that cannot make one.
const things = [...catalogSrc.matchAll(/new AskCreateThing\("([^"]+)", "([^"]+)",/g)]
  .map((m) => ({ name: m[1], tool: m[2] }));
assert.equal(things.length, 11, 'the engine knows eleven things a person can ask to make');
for (const thing of things) {
  const door = A.ASK_CREATE_DOOR[thing.name];
  const made = A.ASK_CREATE_COMMAND[thing.name];
  assert.ok(door !== undefined || made, `${thing.name} is named by the engine and made nowhere on this page`);
  assert.ok(!(door && made), `${thing.name} has two ways to be made; a person can only be offered one`);
  assert.equal((made ?? door ?? { tool: thing.tool }).tool, thing.tool,
    `${thing.name} must be made in the place the engine says owns it`);
}

// A tool id nobody knows is not printed raw at a person.
assert.match(readable(render({ tool: 'no-such-tool', confidence: 0.99 })), /did not say/,
  'an unknown place is treated as no place, not shown as a wire id');

// ===================================================================================================
// 4. The wiring, read off the source.
// ===================================================================================================
assert.match(askSrc, /const ASK_METHOD = 'routeAsk'/, 'one place names the method');
assert.match(askSrc, /send\(ASK_METHOD, text\)/, 'and the ask is that method and the typed text');
assert.match(askSrc, /rpc<AskRouteResult>\(method, \.\.\.params\)/,
  'the default send is the bridge rpc, with nothing in between');
// RETIRED by fix-askdata-r4 (Kane, 27 Sep: Ctrl J's question routing is out of this release): "the Ctrl J box asks
// through the same function this file executed above". Ctrl J drafts every line now and routes none.
{
  const bar = askSrc.slice(askSrc.indexOf('export function AskBar'), askSrc.indexOf('export const DRAFT_PRO_LINE'));
  assert.match(bar, /rpc<[^>]*>\('composeTask', words,/, 'Ctrl J drafts the words it is given');
  assert.doesNotMatch(bar, /runAsk|routeAsk|isQuestion|AskReply/, 'and never routes them or shows a route answer');
}
assert.match(askSrc, /void runAsk\(words, send\)/, 'and so does the Plan field\'s suggestion');
assert.match(askSrc, /<form[^>]*onSubmit=/, 'Enter sends, because the input sits in a form');
assert.match(askSrc, /onClick=\{\(\) => openAskAnswer\(going, onGo\)\}/,
  'one button, one decision: the press goes through the function this file executed above');
assert.match(askSrc, /import \{ rpc, runHostCommand \} from '\.\/bridge'/,
  'the command goes through the bridge the webview already uses to reach the extension, not a second channel');
assert.match(askSrc, /data-testid="ask-bar-input"/);
// Ctrl J only drafts now (fix-askdata-r4), so its plain words say that; they said "Ask about this model" while it routed.
assert.match(askSrc, /'Describe a task'/, 'the box says what it is for in plain words');
assert.doesNotMatch(askSrc, /—|–/, 'no em dashes, anywhere');
// The box IS gated in the page now. Kane, 22 September: ask is Jev, so it is Pro. The engine refuses a
// free session before it matches a word, so an input here would only ever produce a refusal.
assert.match(askSrc, /planRankingOf\(useTier\(\)\)/,
  'the tier read is the Plan card own one, so the two surfaces cannot disagree about who has Pro');
// And the reason never reaches the page.
assert.doesNotMatch(askSrc, /result\.reason/, 'the reason is for the assistant door, never for a person');
// Routing only navigates (jev-plan M2). Nothing on this surface may write.
for (const method of ['applyPlan', 'modelPlanAction', 'applySafeFixes', 'setProperty']) {
  assert.ok(!askSrc.includes(method), `the box never acts, so it must not call ${method}`);
}

// The Overview's card retired in slice 11 (lane R1): AskBox went with it, and the two ways in are the Plan's field
// and Ctrl J (contract 11.1). Each hands the answer the shell's own navigation, TARGET AND ALL: without the second
// argument a create answer would land on the page and not on the form its own line just named.
assert.doesNotMatch(askSrc, /export function AskBox\b/, 'the Overview card is gone, not merely unmounted');
// It asks about the very words Enter adds (fieldSubmit's), so the row can keep the page the field showed (verify-askdata-r6 F1).
assert.match(planSrc, /const suggestion = useAskSuggestion\(pro \? P\.fieldSubmit\(field\)\?\.words \?\? '' : ''\);/, 'the Plan field asks on Pro only');
assert.match(planSrc, /<AskReply result=\{suggestion\} onGo=\{onGo\} asked=\{field\} \/>/,
  'and its answer jumps with the Plan\'s navigation, carrying the typed words so Open Ask your data lands on the question');
// The box also takes a key and the words carried from the Plan field (journey m11 F7), so the match allows props first.
// RETIRED by fix-askdata-r4 (Ctrl J routing out of this release): "Ctrl J hands its answer the shell's goTab". Ctrl J
// has no answer to hand over, so it takes no onGo.
assert.doesNotMatch(appSrc, /<AskBar [^>]*?onGo=/, 'Ctrl J takes no navigation, because it routes nothing');

// ===================================================================================================
// 5. The tier. Pro gets the input; free gets the pill and no input at all. Rendered on the Ctrl J box, the ask
//    box on every page since the Overview's card retired (slice 11); the Plan field's gate is the line above.
// ===================================================================================================
const box = (tier) => { TIER.now = tier; return renderToStaticMarkup(React.createElement(A.AskBar, { onGo: () => {}, onDrafted: () => {}, onClose: () => {} })); };

const proBox = box('pro');
assert.match(proBox, /data-testid="ask-bar-input"/, 'Pro types into the box');
assert.doesNotMatch(proBox, /data-testid="ask-bar-pro"/, 'and meets no upsell for something they bought');

const freeBox = box('free');
assert.doesNotMatch(freeBox, /data-testid="ask-bar-input"/,
  'free gets no input: the engine refuses the call, so a box that always answers with a refusal is worse than a sentence');
assert.match(freeBox, /data-testid="ask-bar-pro"/);
assert.match(readable(freeBox), /Unlock Pro/, 'and the same way through that the Plan page offers');

const unknownBox = box('unknown');
assert.doesNotMatch(unknownBox, /data-testid="ask-bar-input"/);
assert.doesNotMatch(unknownBox, /data-testid="ask-bar-pro"/,
  'while the licence is still being read, neither the box nor the upsell is the honest thing to draw');
assert.match(readable(unknownBox), /Checking your Pro access/);
TIER.now = 'pro';

// ===================================================================================================
// 6. The Overview's cards retired in slice 11, and the old margin rules stay gone.
// ===================================================================================================
const css = readFileSync(join(webview, 'src', 'styles.css'), 'utf8');
assert.doesNotMatch(css, /\.ovh\b/, 'the Overview\'s rules left with the page');
assert.ok(!/\.overview-cols \{/.test(css) && !/\.overview-health \{/.test(css), 'the old margin rules stay gone');

// ===================================================================================================
// 5. Ctrl J's Enter, EXECUTED (fix-askdata-r5, V4). Section 4's check on Ctrl J is a regex over AskBar's source, and a
//    revert that routes a question through rpc('routeAsk', words) passed it and every other CI test (verify-askdata-r4,
//    mutant J1b; only composer-drive, which CI does not run, caught it). There is no DOM here, so the component is
//    run for real under a tiny React stand-in: its hooks hold their first value, its JSX becomes plain objects, and the
//    form's own onSubmit is pressed. Every call that reaches the bridge is recorded, whatever path it took.
// ===================================================================================================
{
  const barCalls = [];
  const shimReact = {
    useState: (init) => [typeof init === 'function' ? init() : init, () => {}],
    useRef: (init) => ({ current: init }),
    useEffect: () => {}, useMemo: (f) => f(), useCallback: (f) => f,
  };
  const jsx = (type, props) => ({ type, props: props ?? {} });
  const shimCache = new Map();
  const shimStubs = {
    ...STUBS,
    react: shimReact,
    'react/jsx-runtime': { jsx, jsxs: jsx, Fragment: 'fragment' },
    './bridge': {
      ...STUBS['./bridge'],
      rpc: async (method, ...params) => {
        barCalls.push([method, ...params]);
        return method === 'composeTask' ? { draftId: 'dr-test' } : { tool: 'askdata', area: 'explore' };
      },
    },
  };
  const loadShim = (file) => {
    const full = file.endsWith('.ts') || file.endsWith('.tsx') || file.endsWith('.mjs') || file.endsWith('.js') ? file
      : existsSync(file + '.tsx') ? file + '.tsx' : file + '.ts';
    if (shimCache.has(full)) return shimCache.get(full);
    const js = ts.transpileModule(readFileSync(full, 'utf8'), {
      compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022, jsx: ts.JsxEmit.ReactJSX, esModuleInterop: true },
      fileName: full,
    }).outputText;
    const module = { exports: {} };
    shimCache.set(full, module.exports);
    const localRequire = (name) => {
      if (shimStubs[name]) return shimStubs[name];
      if (name.startsWith('.')) return loadShim(resolve(dirname(full), name));
      return req(name);
    };
    // eslint-disable-next-line no-new-func
    new Function('require', 'module', 'exports', js)(localRequire, module, module.exports);
    shimCache.set(full, module.exports);
    return module.exports;
  };
  const find = (node, test) => {
    if (!node || typeof node !== 'object') return null;
    if (Array.isArray(node)) { for (const n of node) { const hit = find(n, test); if (hit) return hit; } return null; }
    if (test(node)) return node;
    return find(node.props?.children, test);
  };
  const Shimmed = loadShim(join(webview, 'src', 'askbox.tsx'));
  TIER.now = 'pro';
  for (const words of [
    'what were net sales last month?',
    'net sales by store region for 2025',
    'where do I add a hierarchy',
    'how big is this model',
    'Sort MonthName by MonthNumber',
  ]) {
    barCalls.length = 0;
    const drafted = [];
    const tree = Shimmed.AskBar({ initialText: words, onDrafted: (id) => drafted.push(id), onClose: () => {} });
    const form = find(tree, (n) => n.type === 'form' && typeof n.props.onSubmit === 'function');
    assert.ok(form, 'the Pro Ctrl J box draws a form whose submit is Enter');
    form.props.onSubmit({ preventDefault: () => {} });
    await new Promise((r) => setTimeout(r, 0));
    assert.deepEqual(barCalls, [['composeTask', words, null, null, null, null, 'human']],
      `Ctrl J's Enter on "${words}" makes exactly one call, the draft, and routes nothing (calls: ${JSON.stringify(barCalls)})`);
    assert.deepEqual(drafted, ['dr-test'], 'and the Plan opens on the draft it made');
  }
}

// ===================================================================================================
// 6. The Plan field shows nothing when no place is named (fix-askdata-r5, V1). The field is "Describe a task or an
//    aim": words nothing placed are a task, and Enter keeps them as one, so the no-match line ("Try a page name") is
//    not a suggestion there. A place named, or a sentence the engine answered from its own numbers, still is.
// ===================================================================================================
{
  const noPlace = { tool: null, fallback: 'menu', confidence: 0, asked: false,
    sentence: 'Nothing here matches those words yet. Try a page name, or the name of a thing in the model.', reason: 'gate' };
  assert.equal(A.fieldSuggestion(noPlace), null, 'a no-place answer shows nothing under the Plan field');
  assert.equal(A.fieldSuggestion({ ...noPlace, asked: true, confidence: 0.8 }), null, 'nor does one the service left unplaced');
  assert.equal(A.fieldSuggestion({ failed: 'The engine did not answer.' }), null, 'a failed answer is not a suggestion');
  assert.equal(A.fieldSuggestion(null), null);
  const place = { tool: 'diagram', area: 'explore', confidence: 1, asked: false };
  assert.equal(A.fieldSuggestion(place), place, 'a place named is a suggestion');
  const told = { tool: null, confidence: 1, asked: false, sentence: 'Northstar has 7 tables and 21 measures.' };
  assert.equal(A.fieldSuggestion(told), told, 'a sentence answered from the engine\'s own numbers is shown');
  assert.match(askSrc, /setAnswered\(\{ words, result: fieldSuggestion\(answer\) \}\)/, 'and the field\'s hook shows exactly what fieldSuggestion keeps');
}

// ===================================================================================================
// 7. TWO READINGS (coordinator, 28 September, fix-askdata-r6). The same words can be a task and a question, and the
//    engine now names both whenever Ask your data is one of them. The page shows both, in the order the engine set,
//    and Open Ask your data carries the typed words onto that page.
// ===================================================================================================
{
  const withWords = (result, asked) => readable(renderToStaticMarkup(
    React.createElement(A.AskReply, { result, asked, onGo: () => {} })));

  // (a) A task first, naming no page: the row's own words, with Ask your data as the named second choice. Before
  //     this the field read "Ask your data is the place for that." over a row that read the line below.
  const taskFirst = {
    tool: null, area: null, confidence: 0, asked: false, reading: 'task',
    // fix-askdata-final (verify-askdata-r6 L3): the task's own line, the same first or second, so the first thing a
    // person reads says what Enter does.
    sentence: 'Keep it as a task. Semanticus does not know a page for this one.',
    other: { kind: 'question', tool: 'askdata', area: 'explore', confidence: 1, line: 'Or ask it as a question.' },
  };
  const T = withWords(taskFirst, 'Totals per region are double counting');
  assert.match(T, /Semanticus does not know a page for this one\./, 'the task reading says what the row says');
  assert.match(T, /Or ask it as a question\./, 'and Ask your data is named as the second choice');
  assert.doesNotMatch(T, /Ask your data is the place for that/, 'never as the answer');
  assert.match(T, /Open Ask your data/, 'with a way through to it');
  assert.equal(A.fieldSuggestion(taskFirst), taskFirst, 'and the Plan field shows it, because the choice IS the point');

  // (b) A question first: Ask your data, with keeping it as a task still named.
  const questionFirst = {
    tool: 'askdata', area: 'explore', confidence: 1, asked: false, reading: 'question',
    other: { kind: 'task', tool: null, confidence: 0, line: 'Keep it as a task. Semanticus does not know a page for this one.' },
  };
  const Q = withWords(questionFirst, 'please show me net sales by region');
  assert.match(Q, /Ask your data is the place for that\./, 'the question reading names the page');
  assert.match(Q, /Keep it as a task\./, 'and keeping it as a task is still named');

  // The typed words travel with the button, so Ask your data opens on the question and not empty (round 4's note).
  const jump = A.askAction(questionFirst, 'please show me net sales by region');
  assert.equal(jump.target, 'words:please show me net sales by region', 'Open Ask your data carries the words');
  assert.equal(A.askAction(questionFirst).target, undefined, 'and carries nothing when there are no words');
  assert.equal(A.askAction({ tool: 'lineage' }, 'what uses Net Sales').target, undefined,
    'only Ask your data takes them: no other page has a box to put them in');
  assert.equal(A.askOther(taskFirst, 'Totals per region are double counting').target,
    'words:Totals per region are double counting', 'the second choice carries them too');
  assert.equal(A.askOther(questionFirst, 'anything'), null, 'a task reading with no page has no button, only its line');

  // fix-askdata-final (verify-askdata-r6 L4). A line that only names the page asked for the page, so the box opens empty
  // rather than holding "go to ask your data"; a real question that also names the page keeps its words; and words
  // typed across a Shift+Enter line break reach the one-line box on one line.
  for (const only of ['go to ask your data', 'open Ask your data', 'I need ask your data', 'ask your data please',
    'Ask your data', 'take me to the ask your data page', 'use ask your data', 'please open Ask your data for me',
    'I would like to open Ask your data', "I'd like to see ask your data"]) {
    assert.equal(A.carriedWords(only), '', `"${only}" only names the page`);
    assert.equal(A.askAction(questionFirst, only).target, undefined, `"${only}" opens Ask your data empty`);
  }
  assert.equal(A.carriedWords('check net sales by region in ask your data'), 'check net sales by region in ask your data',
    'a question that names the page keeps its words');
  const twoLines = 'Totals per region' + String.fromCharCode(10) + 'are double counting';
  assert.equal(A.askOther(taskFirst, twoLines).target, 'words:Totals per region are double counting', 'on one line');

  // fix-askdata-final (verify-askdata-r6 F1): the page Enter sends with the words is the page the field shows for the
  // TASK: the one reading's place (never Ask your data), or the task reading's place, first or second.
  assert.equal(A.fieldTaskPage({ tool: 'advmodels', confidence: 0.9, asked: true }), 'advmodels', 'the service named a page');
  assert.equal(A.fieldTaskPage({ tool: 'history', sentence: 'Nothing has changed.' }), 'history', 'the engine answered from its state');
  assert.equal(A.fieldTaskPage({ tool: 'askdata' }), null, 'Ask your data alone is never a task page');
  assert.equal(A.fieldTaskPage(taskFirst), null, 'task first, no page');
  assert.equal(A.fieldTaskPage({ ...taskFirst, tool: 'daxlab' }), 'daxlab', 'task first, with a page');
  assert.equal(A.fieldTaskPage(questionFirst), null, 'question first, the task has no page');
  assert.equal(A.fieldTaskPage({ ...questionFirst, other: { ...questionFirst.other, tool: 'daxlab' } }), 'daxlab', 'question first, the task has one');
  assert.equal(A.fieldTaskPage({ tool: null, fallback: 'menu', sentence: 'Nothing here matches' }), null, 'nothing placed');
  assert.equal(A.fieldTaskPage({ failed: 'x', tool: 'daxlab' }), null, 'a failed call shows nothing');
  assert.equal(A.fieldTaskPage(null), null);
  const planSrcF1 = readFileSync(join(webview, 'src', 'plan.tsx'), 'utf8');
  assert.match(planSrcF1, /const fieldPage = fieldTaskPage\(suggestion\);/, 'Enter reads the page off the suggestion on screen');
  assert.match(planSrcF1, /'addPlanTask', fieldPage \? \{ words: asked\.words, fieldPage \} : \{ words: asked\.words \}/,
    'and sends it with the words (driven for real in cp-plan-drive, scene field-enter-keeps-the-page)');

  // fix-askdata-final (verify-askdata-r6 L9): the whole reply is the live region, so the second choice is spoken with the
  // first instead of only the first line.
  const live = renderToStaticMarkup(React.createElement(A.AskReply, { result: taskFirst, asked: 'x', onGo: () => {} }));
  assert.match(live, /^<div class="ask-reply"[^>]*aria-live="polite"/, 'the reply itself is the live region');
  assert.equal((live.match(/aria-live/g) || []).length, 1, 'and nothing inside it is a second one');
  assert.match(live, /aria-live="polite"[\s\S]*Or ask it as a question\./, 'the second choice is inside it');

  // The shell turns that target into the seed Ask your data adopts, and the page fills its question box with it.
  const appSrcR6 = readFileSync(join(webview, 'src', 'App.tsx'), 'utf8');
  // These four read source text, so on their own they prove the lines exist, not that the wiring works; the press and
  // the box are driven for real in tools/uishot/cp-plan-drive.mjs, scene field-open-ask-carries-words (verify-askdata-r6 L2).
  assert.match(appSrcR6, /tool === 'askdata' && target\?\.startsWith\('words:'\)/, 'the shell reads the words off the target');
  assert.match(appSrcR6, /kind: 'askWords'/, 'and hands them over as a one-use seed');
  const askDataSrc = readFileSync(join(webview, 'src', 'askdata.tsx'), 'utf8');
  assert.match(askDataSrc, /setWords\(navWords\.words\)/, 'Ask your data puts them in its question box');
  assert.match(askDataSrc, /onNavConsumed\?\.\(navWords\.nonce\)/, 'and acknowledges the seed so it cannot replay');
}

console.log('Ask box: routeAsk called with the typed text, the reply shapes rendered including the three '
  + 'create shapes, the button press executed for a command and for a tab, every place name checked '
  + 'against the engine catalogue, and the Pro pill and the Pro input rendered per tier on the Ctrl J box.');
