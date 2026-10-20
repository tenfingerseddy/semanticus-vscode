// AI instructions has two tabs: Instructions and Notes.
//
// Kane, 23 September 2026 ("Model notes - agreed implement recommendation"): Model notes stopped being a
// page of its own and became the Notes tab of AI instructions. Both tabs are the written background for
// one model, so they belong on one page: Instructions is what the assistant must start from, Notes is what
// a person should know.
//
// What each check here actually proves, because they are not equally strong:
//   1. THE TABS ARE REAL. `AiInstructionsPage` is rendered for real with react-dom/server, once per tab,
//      and the strip, its pressed state and which pane is showing are read back out of the markup.
//   2. THE SWITCH IS REAL. The Notes button is executed: its onClick is pulled off the rendered element
//      tree and called, and what it asks for is recorded. No source matching.
//   3. NOTES IS THE WHOLE PAGE. The four sections Model notes always had (insights, saved workflows,
//      recall, purge) are found by their own stable ids inside the Notes pane, so a fold that quietly
//      dropped one fails here.
//   4. THE ROUTE. `knowledge` is still a real route id, and App.tsx maps it to the Notes tab. That half is
//      read off the source, because App is the shell and cannot be mounted without a host.
//
// There is no DOM in this suite (no jsdom is installed), so the real click is proved in a real browser by
// tools/uishot/instructions-tabs-drive.mjs, scenes instructions-tab and notes-tab.
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

// The host seams, stubbed. Nothing here answers a call: renderToStaticMarkup never runs an effect, so the
// page paints its loading state and no read is made. That is exactly the state a person sees first.
const STUBS = {
  './bridge': {
    rpc: async () => { throw new Error('the test loader answers no calls'); },
    onActivity: () => () => {}, onDidChange: () => () => {}, onProgress: () => () => {},
    revealInTree: () => {}, focusSelectInProperties: () => {}, selectInProperties: () => {}, runHostCommand: () => {},
  },
  './pro': {
    useFeature: () => 'denied', useTier: () => 'free', useLockedTools: () => new Set(),
    isEntitlementError: () => false, PENDING_ACCESS: 'Checking your Pro access…',
    ProPill: ({ line }) => React.createElement('div', { 'data-testid': 'knowledge-workflows-pro-pill' }, line),
    ProBadge: () => null, UpsellNotice: () => null, upgradeToPro: () => {}, manageSubscription: () => {},
  },
};

const cache = new Map();
function load(file) {
  const full = file.endsWith('.ts') || file.endsWith('.tsx') || file.endsWith('.mjs') ? file
    : existsSync(file + '.tsx') ? file + '.tsx' : existsSync(file + '.ts') ? file + '.ts' : file + '.mjs';
  if (cache.has(full)) return cache.get(full);
  // The fileName is handed over with a .ts extension on purpose. TypeScript reads the EXTENSION to decide
  // whether a file is ESM, and it leaves a .mjs file's `export` statements alone however the module option
  // is set, so the plain ES modules this webview ships (storagecalc.mjs and its siblings) came back
  // untranspiled and died on `Unexpected token 'export'` inside the Function below. The contents are
  // unchanged; only the name this call is told is different.
  const js = ts.transpileModule(readFileSync(full, 'utf8'), {
    compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022, jsx: ts.JsxEmit.ReactJSX, esModuleInterop: true },
    fileName: full.endsWith('.mjs') ? full.slice(0, -4) + '.ts' : full,
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

const src = (f) => readFileSync(join(webview, 'src', f), 'utf8');
const I = load(join(webview, 'src', 'instructions.tsx'));
const app = src('App.tsx');

const page = (tab, onTab = () => {}) => React.createElement(I.AiInstructionsPage, {
  sessionId: 'test', revision: 1, changeNonce: 0, tab, onTab, onOpenWorkflows: () => {},
});

// ---- 1. the tab strip is real, and only one pane is showing ----------------------------------------
for (const [tab, pressed, other] of [['instructions', 'instructions', 'notes'], ['notes', 'notes', 'instructions']]) {
  const html = renderToStaticMarkup(page(tab));
  const on = new RegExp(`data-page-tab="${pressed}" aria-pressed="true"`);
  const off = new RegExp(`data-page-tab="${other}" aria-pressed="false"`);
  assert.match(html, on, `on ${tab} the ${pressed} tab must read as selected`);
  assert.match(html, off, `and the ${other} tab must not`);
  assert.match(html, />Instructions</, 'the strip must offer Instructions');
  assert.match(html, />Notes</, 'the strip must offer Notes');
}

// Which pane is SHOWING, not merely which exists: the other pane stays mounted (hidden) so a half-written
// note survives a look at the instructions, which is the whole reason it is not unmounted.
const notesHtml = renderToStaticMarkup(page('notes'));
const instructionsHtml = renderToStaticMarkup(page('instructions'));
assert.match(notesHtml, /class="min-h-0 flex-1" data-testid="instructions-notes-pane"/, 'the Notes pane shows on the Notes tab');
assert.match(instructionsHtml, /class="hidden" data-testid="instructions-notes-pane"/, 'and is hidden, not thrown away, on the Instructions tab');
assert.match(instructionsHtml, /sem-instructions-page/, 'the Instructions pane shows on the Instructions tab');

// ---- 2. the switch is executed, not eyeballed ------------------------------------------------------
const clicks = [];
// `InstructionsTabs` holds no hooks, so it is called as a plain function and its buttons are pressed for
// real. The strip is the switch; nothing else on the page decides which tab is open.
const strip = findAll(I.InstructionsTabs({ tab: 'instructions', onTab: (next) => clicks.push(next) }),
  (el) => el.props && el.props['data-page-tab']);
assert.deepEqual(strip.map((el) => el.props['data-page-tab']), ['instructions', 'notes'], 'two tabs, Instructions first');
strip.find((el) => el.props['data-page-tab'] === 'notes').props.onClick();
strip.find((el) => el.props['data-page-tab'] === 'instructions').props.onClick();
assert.deepEqual(clicks, ['notes', 'instructions'], 'each tab asks for its own route, and nothing else');

// Minimal pass (8 Oct): the foot link is gone (the strip above it is the switch), and a waiting note suggestion is a count on the Notes tab
// instead of a second copy of the card on the Instructions tab.
const card = src('instructions.tsx');
assert.doesNotMatch(card, /Notes for people are separate/, 'the Instructions card has no second link to the Notes tab');
assert.doesNotMatch(card, /data-testid="overview-suggestion"/, 'the Notes suggestion is not repeated on the Instructions tab');
assert.match(card, /notesWaiting=\{/, 'the Notes tab carries the count of waiting suggestions');
const withCount = findAll(I.InstructionsTabs({ tab: 'instructions', onTab: () => {}, notesWaiting: 2 }), (el) => el.props && el.props['data-page-tab'] === 'notes')[0];
assert.match(JSON.stringify(withCount.props.children), /"2"|2/, 'the count shows on the Notes tab');

// ---- 3. Notes is the whole of Model notes, not a trimmed copy ---------------------------------------
// What the assistant learned (lessons, Recall, Delete) is ONE closed fold under the notes; its parts show when it is opened.
assert.match(notesHtml, /id="knowledge-learned"/, 'the Notes tab must still carry the lessons fold');
assert.match(notesHtml, /What your assistant learned/, 'and it is named for what it holds');
assert.match(notesHtml, /aria-expanded="false"/, 'and it is closed by default');
assert.doesNotMatch(notesHtml, /Saved workflows/, 'saved workflows are on the Workflows tab, not repeated here');
assert.doesNotMatch(notesHtml, /knowledge-workflows-pro-pill/, 'so the Notes tab has no Pro pill');
assert.match(notesHtml, /Model notes/, 'and the notes keep their own name inside the tab');

// ---- 4. the route: knowledge is still real, and it is the Notes tab ---------------------------------
assert.match(app, /const instructionsTab: InstructionsTab = tab === 'knowledge' \? 'notes' : 'instructions';/,
  'a deep link to knowledge must open AI instructions on the Notes tab');
assert.match(app, /tab === 'instructions' \|\| tab === 'knowledge' \?[\s\S]{0,200}<AiInstructionsPage /,
  'and both ids must mount the one page');
assert.match(app, /onTab=\{\(next\) => goTab\(next === 'notes' \? 'knowledge' : 'instructions'\)\}/,
  'switching tabs must move the ROUTE, so the strip and a deep link cannot disagree');
assert.match(app, /TAB_TO_GROUP\.knowledge = 'edit';/, 'knowledge keeps an area, so the Edit strip stays lit on it');
// Since slice 11 every id goes through route.ts's landing table (test/retired-routes.test.mjs walks them all).
assert.match(readFileSync(join(webview, 'src', 'route.ts'), 'utf8'), /knowledge: 'edit'/,
  'knowledge stays a valid route, or an old saved page silently lands nowhere');
assert.match(app, /tab === 'knowledge' \? 'instructions' : tab/, 'the Edit row lights AI instructions while Notes is open');

// The Edit row itself: Model notes is no longer a segment of its own.
const groupsBlock = /const TAB_GROUPS[\s\S]*?\n\];/.exec(app)[0];
const editRow = /\{ id: 'edit', label: 'Edit', tabs: \[([\s\S]*?)\] \}/.exec(groupsBlock);
assert.ok(editRow, 'the Edit row must still be findable');
assert.deepEqual([...editRow[1].matchAll(/\{ id: '([a-z]+)', label: '([^']+)' \}/g)].map((m) => [m[1], m[2]]), [
  ['instructions', 'AI instructions'],
  ['advmodels', 'Advanced modelling'], ['mcode', 'Power Query'],
], 'the Edit row is AI instructions, Advanced modelling and Power Query (Queries moved to Explore > Query)');
assert.doesNotMatch(groupsBlock, /\{ id: 'knowledge', label: 'Model notes' \}/,
  'Model notes is a tab inside AI instructions now, so it is not a strip button anywhere');

console.log('AI instructions two-tab tests passed');

/** Every element in a rendered tree for which `pick` is true, in document order. */
function findAll(node, pick, out = []) {
  if (Array.isArray(node)) { for (const child of node) findAll(child, pick, out); return out; }
  if (!node || typeof node !== 'object' || !node.props) return out;
  if (pick(node)) out.push(node);
  findAll(node.props.children, pick, out);
  return out;
}
