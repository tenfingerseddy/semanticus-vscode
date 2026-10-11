// The Plan's first-open guidance (Plan-heart contract 10, lane U4): the six-step tour, the What changed card
// after an update, and the two ways back in from Help.
//
// Three kinds of check, and the difference matters when reading a pass:
//   1. The DECISIONS are pure functions and are executed here: which of tour, card or nothing shows for a
//      new person, an existing one and someone who has seen it; which flags each click writes; where the
//      step card sits against its anchor.
//   2. The CARDS and the Help block are rendered for real with react-dom/server through a loader that stubs
//      only the VS Code bridge and the licence hook, and the words a person reads are asserted.
//   3. The host's half (context.globalState keys and the round trip) is a small pure module, src/guideFlags.ts,
//      executed against a fake globalState; bridge.ts is executed against a fake VS Code API.
// The clicks and the anchoring on the real page are driven by tools/uishot/plan-guide-drive.mjs.
import assert from 'node:assert/strict';
import { readFileSync, existsSync } from 'node:fs';
import { dirname, resolve, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createRequire } from 'node:module';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const webview = join(root, 'webview');
const req = createRequire(join(webview, 'package.json'));
const ts = req('typescript');
const React = req('react');
const { renderToStaticMarkup } = req('react-dom/server');

const calls = { rpc: [], set: [], asked: [] };
const STUBS = {
  './bridge': {
    rpc: async (method, ...params) => { calls.rpc.push({ method, params }); return { rows: [] }; },
    requestGuideFlags: async (cardId) => { calls.asked.push(cardId); return { planTour: false, whatChanged: false }; },
    setGuideFlag: (flag, cardId) => { calls.set.push([flag, cardId]); },
    runHostCommand: () => {},
  },
  './pro': { useTier: () => 'pro', upgradeToPro: () => {} },
  './plan-guide.css': {},
};
const cache = new Map();
function transpile(full) {
  return ts.transpileModule(readFileSync(full, 'utf8'), {
    compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022, jsx: ts.JsxEmit.ReactJSX, esModuleInterop: true },
    fileName: full.endsWith('.mjs') ? `${full.slice(0, -4)}.ts` : full,
  }).outputText;
}
function load(file, stubs = STUBS) {
  const full = /\.(ts|tsx|mjs)$/.test(file) ? file : existsSync(file + '.tsx') ? file + '.tsx' : file + '.ts';
  if (stubs === STUBS && cache.has(full)) return cache.get(full);
  const module = { exports: {} };
  if (stubs === STUBS) cache.set(full, module.exports);
  const localRequire = (name) => {
    if (stubs[name]) return stubs[name];
    if (name.startsWith('.')) return load(resolve(dirname(full), name), stubs);
    return req(name);
  };
  // eslint-disable-next-line no-new-func
  new Function('require', 'module', 'exports', transpile(full))(localRequire, module, module.exports);
  if (stubs === STUBS) cache.set(full, module.exports);
  return module.exports;
}
const readable = (html) => html.replace(/<[^>]+>/g, ' ').replace(/&quot;/g, '"').replace(/&#x27;/g, "'").replace(/&amp;/g, '&').replace(/\s+/g, ' ').trim();
const h = React.createElement;

const G = load(join(webview, 'src', 'plan-guide.tsx'));
const H = load(join(webview, 'src', 'help.tsx'));

// ===================================================================================================
// 1. The six steps, anchored to real parts of the Plan, in the mockup's words.
// ===================================================================================================
assert.equal(G.PLAN_TOUR.length, 6, 'the tour is six steps');
assert.deepEqual(G.PLAN_TOUR.map((s) => s.anchor[0]), ['strip', 'row', 'doer', 'field', 'queue-switch', 'history-row'],
  'anchored, in order, to the strip, a row, a row\'s doer, the field, the queue switch and a History row');
assert.deepEqual(G.PLAN_TOUR.map((s) => s.title), [
  'The work on your model', 'Every row answers three questions', 'How Semanticus works', 'Add your own',
  "Your assistant's tasks", 'What changed, and Undo',
]);
assert.equal(G.PLAN_TOUR[3].body, 'Describe a task or an aim in your own words. It becomes a row. Hand it to your assistant, or do it yourself.');
assert.equal(G.PLAN_TOUR[5].view, 'history', 'the last step opens History, where its anchor is');
assert.ok(G.PLAN_TOUR[5].anchor.includes('history'), 'and falls back to the History switch when no row has drawn yet');
assert.equal(G.PLAN_TOUR[2].how, true, 'step three is the four-line how it works');
assert.equal(G.WHAT_CHANGED_CARD_ID, 'm11-1', 'the card id is a content id, not the package version');
assert.deepEqual(G.WHAT_CHANGED['m11-1'].lines, [
  'The Plan is now where you start. The Overview is gone.',
  'Edit History moved into the Plan. Open History at the top.',
  'Workflow runs show as rows in the Plan.',
  "One switch pauses your assistant's tasks.",
  'Connections are on one page, with a SQL endpoint for each model.',
  'Workflows is one page. With Pro, a workflow you describe in words becomes steps.',
  'With Pro, Ask your data, under Explore, answers a question about your numbers. If it can be read two ways, it names the other.',
  'With Pro, Jev picks column formats and base measures, and chooses what Copilot sees.',
]);
assert.equal(Object.keys(G.WHAT_CHANGED).length, 1, 'one card per build');

// ===================================================================================================
// 2. Which one shows on first open.
// ===================================================================================================
const none = { planTour: false, whatChanged: false };
const ID = G.WHAT_CHANGED_CARD_ID;
assert.equal(G.decideGuide(none, true, ID), 'tour', 'a new person, whose Plan history is empty, gets the tour');
assert.equal(G.decideGuide(none, false, ID), 'card', 'an existing person, with history, gets the What changed card');
assert.equal(G.decideGuide({ planTour: true, whatChanged: false }, true, ID), 'none', 'someone who finished the tour sees nothing');
assert.equal(G.decideGuide({ planTour: true, whatChanged: false }, false, ID), 'none', 'on any model');
assert.equal(G.decideGuide({ planTour: false, whatChanged: true }, false, ID), 'none', 'a card once seen is never shown again');
assert.equal(G.decideGuide(none, false, 'no-such-card'), 'none', 'a card id with no card shows nothing');
assert.equal(G.decideGuide(none, false, null), 'none', 'and a release with no card id shows nothing');
assert.equal(G.decideGuide(null, true, ID), 'none', 'no answer from the host means no guide, never a nag');
assert.equal(G.decideGuide(none, null, ID), 'none', 'and no answer about the history means the same');

// ===================================================================================================
// 3. Every click, and the flag it writes. Closing sets the flag; nothing shows twice.
// ===================================================================================================
const start = { mode: 'tour', step: 0 };
let r = G.guideStep(start, { type: 'next' });
assert.deepEqual(r.state, { mode: 'tour', step: 1 }); assert.deepEqual(r.set, []);
r = G.guideStep(r.state, { type: 'back' });
assert.deepEqual(r.state, { mode: 'tour', step: 0 });
r = G.guideStep(r.state, { type: 'back' });
assert.deepEqual(r.state, { mode: 'tour', step: 0 }, 'Back on step one stays on step one');
r = G.guideStep({ mode: 'tour', step: 5 }, { type: 'next' });
assert.deepEqual(r.state, { mode: 'tour', step: 5 }, 'Next on the last step goes nowhere; Done is what ends it');
r = G.guideStep({ mode: 'tour', step: 2 }, { type: 'close' });
assert.deepEqual(r.state, { mode: 'none', step: 0 }, 'Close works at any step');
assert.deepEqual(r.set, ['planTour'], 'and closing the tour sets its flag');
r = G.guideStep({ mode: 'tour', step: 5 }, { type: 'close' });
assert.deepEqual(r.set, ['planTour'], 'Done on the last step is the same close');
r = G.guideStep({ mode: 'card', step: 0 }, { type: 'close' });
assert.deepEqual(r.state.mode, 'none');
assert.deepEqual([...r.set].sort(), ['planTour', 'whatChanged'],
  'Done on the card sets its flag, and the tour\'s: an existing person is not new on the next empty model');
r = G.guideStep({ mode: 'card', step: 0 }, { type: 'showTour' });
assert.deepEqual(r.state, { mode: 'tour', step: 0 }, 'Show the tour on the card starts the tour at step one');
assert.deepEqual(r.set, ['whatChanged'], 'and the card is done with');
assert.deepEqual(G.shownFlags('tour'), ['whatChanged'], 'the tour showing sets the card\'s flag, so a new person never gets this card later');
assert.deepEqual(G.shownFlags('card'), ['whatChanged'], 'the card showing sets its own flag, so it cannot show twice');
assert.deepEqual(G.shownFlags('none'), []);

// ===================================================================================================
// 4. Where the step card sits.
// ===================================================================================================
const bounds = { left: 0, top: 0, right: 1440, bottom: 900 };
const card = { width: 400, height: 170 };
let at = G.placeTourCard({ left: 20, top: 90, right: 1160, bottom: 245 }, card, bounds, 'start');
assert.deepEqual(at, { left: 20, top: 263 }, 'under its anchor, left-aligned with it, 18 below it');
assert.equal(G.placeTourCard({ left: 4, top: 90, right: 300, bottom: 120 }, card, bounds, 'start').left, 16, 'and kept 16 inside the frame');
at = G.placeTourCard({ left: 20, top: 820, right: 400, bottom: 860 }, card, bounds, 'start');
assert.equal(at.top, 820 - 18 - 170, 'above its anchor when there is no room below');
at = G.placeTourCard({ left: 900, top: 100, right: 1400, bottom: 140 }, card, bounds, 'end');
assert.equal(at.left, 1000, 'right-aligned steps line up with the anchor\'s right edge');
at = G.placeTourCard({ left: 1300, top: 100, right: 1420, bottom: 140 }, card, bounds, 'start');
assert.equal(at.left, 1440 - 16 - 400, 'and never run off the right');
at = G.placeTourCard(null, card, bounds, 'start');
assert.deepEqual(at, { left: 520, top: 96 }, 'a missing anchor centres the card near the top rather than hiding the step');

// UAT-J9-S10a-007: step one's card used to sit on the model summary line right under the strip. It now clears it.
at = G.placeTourCard({ left: 20, top: 90, right: 1160, bottom: 245 }, card, bounds, 'start', 290);
assert.deepEqual(at, { left: 20, top: 302 }, 'a step that names a block to clear sits 12 below the bottom of that block');
at = G.placeTourCard({ left: 20, top: 90, right: 1160, bottom: 245 }, card, bounds, 'start', 100);
assert.deepEqual(at, { left: 20, top: 263 }, 'a block above the anchor gap changes nothing');
assert.deepEqual(G.PLAN_TOUR[0].clearOf, 'facts', 'step one clears the model summary line');
// UAT-J9-S10a-006: the tour opened from History kept describing the work strip. Every step but the last lives in Work.
assert.deepEqual(G.PLAN_TOUR.map((_, i) => G.tourView(i)), ['work', 'work', 'work', 'work', 'work', 'history']);
assert.match(readFileSync(new URL('../webview/src/plan-guide.tsx', import.meta.url), 'utf8'), /if \(wantView === 'work'\) \{[^}]*onView\('work'\)/, 'a tour step in Work puts the page on Work, even when the tour was opened from History');

// ===================================================================================================
// 5. What a person reads.
// ===================================================================================================
const first = readable(renderToStaticMarkup(h(G.TourCard, { step: 0, onAct: () => {} })));
assert.ok(first.includes('1 of 6') && first.includes('The work on your model') && first.includes('Next') && first.includes('Close'), first);
assert.ok(!/\bBack\b/.test(first), 'no Back on step one');
const last = readable(renderToStaticMarkup(h(G.TourCard, { step: 5, onAct: () => {} })));
assert.ok(last.includes('6 of 6') && last.includes('Back') && last.includes('Done') && last.includes('Reopen it from ? or Help.'), last);
assert.ok(!/\bNext\b/.test(last), 'no Next on the last step');
const how = readable(renderToStaticMarkup(h(G.TourCard, { step: 2, onAct: () => {} })));
for (const line of ['Semanticus makes some changes itself, the same way every time.', 'Your assistant writes the ones that need words.',
  'Jev picks what comes first. Pro only.', 'You approve, and every change can be undone.']) assert.ok(how.includes(line), line);
const cardText = readable(renderToStaticMarkup(h(G.WhatChangedCard, { cardId: ID, onAct: () => {} })));
assert.ok(cardText.startsWith('What changed in this version'), cardText);
for (const line of G.WHAT_CHANGED[ID].lines) assert.ok(cardText.includes(line), line);
assert.ok(cardText.includes('Show the tour') && cardText.includes('Done'));
assert.equal(renderToStaticMarkup(h(G.WhatChangedCard, { cardId: 'no-such-card', onAct: () => {} })), '', 'a card id with no card draws nothing');
const guideSrc = readFileSync(join(webview, 'src', 'plan-guide.tsx'), 'utf8');
for (const [name, text] of [['plan-guide.tsx', guideSrc], ['help.tsx', readFileSync(join(webview, 'src', 'help.tsx'), 'utf8')]]) {
  assert.ok(!text.includes(String.fromCharCode(0x2014)), `${name} carries no em dash`);
  assert.ok(!/[\u0000-\u0008\u000b\u000c\u000e-\u001f]/.test(text), `${name} carries no raw control byte`);
}
assert.ok(/role="dialog"/.test(renderToStaticMarkup(h(G.TourCard, { step: 0, onAct: () => {} }))), 'the step card is a dialog');

// ===================================================================================================
// 6. Help reopens both.
// ===================================================================================================
const went = [];
const items = H.guideHelpItems('bpa', (t) => went.push(t));
assert.deepEqual(items.map((i) => i.label), ['Show the tour', 'What changed in this version'], 'the two entries, in this order');
items[0].run();
assert.deepEqual(went, ['plan'], 'from another page, Show the tour goes to the Plan first');
assert.equal(G.takePlanGuideRequest(), 'tour', 'and asks the Plan for the tour');
assert.equal(G.takePlanGuideRequest(), null, 'once');
items[1].run();
assert.equal(G.takePlanGuideRequest(), 'card', 'What changed in this version asks for the card');
went.length = 0;
H.guideHelpItems('plan', (t) => went.push(t))[0].run();
assert.deepEqual(went, [], 'already on the Plan, it does not navigate');
assert.equal(G.takePlanGuideRequest(), 'tour');
const reopen = readable(renderToStaticMarkup(h(H.PlanGuideReopen, { onGo: () => {} })));
assert.ok(reopen.includes('New here, or want a refresher?') && reopen.includes('Show the tour') && reopen.includes('What changed in this version'), reopen);
const mark = renderToStaticMarkup(h(H.PlanHelpMark, {}));
assert.ok(/aria-label="Help for the Plan"/.test(mark) && readable(mark) === '?', 'the Plan header has its own "?"');
// The Help answers that pointed at Edit History now point at the Plan's History.
const helpSrc = readFileSync(join(webview, 'src', 'help.tsx'), 'utf8');
const where = helpSrc.slice(helpSrc.indexOf('const WHERE'));
assert.ok(!/Publish > Edit History/.test(where), 'no "Where do I" answer sends anyone to Publish > Edit History');
assert.ok(/'See what your assistant changed', a: 'Plan > History/.test(where));
assert.ok(/'Undo or redo a change', a: '[^']*Plan > History/.test(where));

// ===================================================================================================
// 7. The round trip: bridge.ts asks, the host answers from context.globalState, a click writes one key.
// ===================================================================================================
{
  const posted = [];
  const win = new EventTarget();
  win.setTimeout = setTimeout; win.clearTimeout = clearTimeout;
  const prev = { window: globalThis.window, acquire: globalThis.acquireVsCodeApi };
  globalThis.window = win;
  globalThis.acquireVsCodeApi = () => ({ postMessage: (m) => posted.push(m), getState: () => ({}), setState: () => {} });
  const B = load(join(webview, 'src', 'bridge.ts'), {});
  const pending = B.requestGuideFlags('plan-heart-1');
  const ask = posted.find((m) => m.type === 'getGuideFlags');
  assert.ok(ask && typeof ask.id === 'number' && ask.cardId === 'plan-heart-1', 'the webview asks the host, naming the card');
  win.dispatchEvent(new MessageEvent('message', { data: { type: 'guideFlags', id: ask.id, planTour: true, whatChanged: false } }));
  assert.deepEqual(await pending, { planTour: true, whatChanged: false }, 'and gets the two flags back');
  B.setGuideFlag('planTour');
  B.setGuideFlag('whatChanged', 'plan-heart-1');
  assert.deepEqual(posted.filter((m) => m.type === 'setGuideFlag').map((m) => [m.flag, m.cardId]), [['planTour', undefined], ['whatChanged', 'plan-heart-1']]);
  const unanswered = B.requestGuideFlags('plan-heart-1');
  win.dispatchEvent(new Event('pagehide'));
  assert.equal(await unanswered, null, 'a view that closes before the host answers settles to no flags, never a hang');
  globalThis.window = prev.window; globalThis.acquireVsCodeApi = prev.acquire;
}
{
  const F = load(join(root, 'src', 'guideFlags.ts'), {});
  const store = new Map();
  const state = { get: (k) => store.get(k), update: async (k, v) => { store.set(k, v); } };
  assert.equal(F.PLAN_TOUR_KEY, 'semanticus.guide.planTour');
  assert.equal(F.whatChangedKey('plan-heart-1'), 'semanticus.guide.whatChanged.plan-heart-1');
  assert.deepEqual(F.readGuideFlags(state, 'plan-heart-1'), { planTour: false, whatChanged: false }, 'a fresh profile has neither');
  await F.writeGuideFlag(state, { flag: 'planTour' }, () => '2026-09-25T10:00:00.000Z');
  assert.deepEqual(store.get('semanticus.guide.planTour'), { done: true, at: '2026-09-25T10:00:00.000Z' });
  await F.writeGuideFlag(state, { flag: 'whatChanged', cardId: 'plan-heart-1' });
  assert.equal(store.get('semanticus.guide.whatChanged.plan-heart-1'), true);
  assert.deepEqual(F.readGuideFlags(state, 'plan-heart-1'), { planTour: true, whatChanged: true });
  assert.deepEqual(F.readGuideFlags(state, 'plan-heart-2'), { planTour: true, whatChanged: false }, 'the next card is its own flag');
  const before = store.size;
  for (const bad of [{ flag: 'whatChanged' }, { flag: 'whatChanged', cardId: '../x' }, { flag: 'whatChanged', cardId: 'A'.repeat(80) },
    { flag: 'studioZoom' }, { flag: 'planTour', cardId: 7 }, null]) await F.writeGuideFlag(state, bad);
  assert.equal(store.size, before, 'the host writes only the two named keys, and a card id only of its own shape');
  assert.deepEqual(F.readGuideFlags(state, '../x'), { planTour: true, whatChanged: false }, 'and reads no key off a bad id');
  const extension = readFileSync(join(root, 'src', 'extension.ts'), 'utf8');
  assert.ok(/msg\?\.type === 'getGuideFlags'[\s\S]{0,400}readGuideFlags\(extCtx\.globalState/.test(extension), 'the host answers from globalState');
  assert.ok(/msg\?\.type === 'setGuideFlag'[\s\S]{0,200}writeGuideFlag\(extCtx\.globalState/.test(extension), 'and writes to it');
}

console.log('plan-guide: tour, What changed and the Help reopen checks passed');
