// Semanticus Pro inside the app (Kane approved the mockup on 23 September: "Okay implement it").
//
// A Get Pro chip in the Studio header, one Semanticus Pro item in Help, and one Pro page where a person
// picks a plan, buys, waits, sees Pro turn on, or pastes a key. Every Unlock Pro in Studio goes to that
// page instead of a quick pick and the website.
//
// Three kinds of check, and a pass means a different thing for each:
//   1. LOGIC, executed. src/proPage.ts (the host's decisions, compiled to out/) and
//      webview/src/propage-model.ts (the page's) are pure on purpose, and every rule the page rests on is
//      a function this file runs.
//   2. The COMPONENTS, rendered for real with react-dom/server through a loader that stubs only the
//      bridge and the licence hooks, once per state, and read back as the words a person sees.
//   3. The WIRING, read off the real source: which command each Unlock Pro reaches, what Help offers, and
//      that the plan the page names is the plan the checkout is opened for. The clicks themselves are
//      driven in a real browser by tools/uishot/pro-in-app-drive.mjs.
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

// ===================================================================================================
// 1a. The host's decisions (src/proPage.ts, compiled).
// ===================================================================================================
const compiled = resolve(root, 'out', 'proPage.js');
assert.ok(existsSync(compiled), 'out/proPage.js is missing. Run npm run compile first.');
const H = await import('../out/proPage.js');
const { BillingError } = await import('../out/proBilling.js');

// The plan a click names is the plan the checkout opens for, and nothing else gets through.
assert.equal(H.planFromMessage('monthly'), 'monthly');
assert.equal(H.planFromMessage('yearly'), 'yearly');
for (const bad of [undefined, null, '', 'Yearly', 'month', 'year', 'free', 42, { plan: 'yearly' }]) {
  assert.equal(H.planFromMessage(bad), undefined, `a page message naming ${JSON.stringify(bad)} opens no checkout`);
}

// A checkout that could not START: unreachable or absent means not open yet; any other answer is an error.
assert.deepEqual(H.checkoutStartFailure(new BillingError('down', 0)), { purchase: 'notOpen' }, 'unreachable is not open yet');
assert.deepEqual(H.checkoutStartFailure(new BillingError('nothing there', 404)), { purchase: 'notOpen' });
for (const status of [400, 500, 502]) {
  const failure = H.checkoutStartFailure(new BillingError('broke', status));
  assert.equal(failure.purchase, 'error', `a service that answered ${status} is open and broken, not unopened`);
  assert.ok(failure.message && !/\u2014/.test(failure.message), 'with a plain sentence and no em dash');
}
assert.equal(H.checkoutStartFailure(new Error('https only')).purchase, 'error', 'a bad address is not "not open yet" either');

// The engine's verdict on a pasted key. The engine verifies; this only sorts its answer.
assert.equal(H.keyResultOf({ tier: 'pro' }), 'ok');
assert.equal(H.keyResultOf({ tier: 'pro', reason: 'license expired 2026-01-01; in grace until 2026-01-15. Renew at x to stay on Pro' }), 'ok',
  'a key inside its grace days still turns Pro on');
assert.equal(H.keyResultOf({ tier: 'free', reason: 'license expired 2026-01-01. Renew at https://semanticus.com.au/pro; the engine keeps working on the free tier' }), 'expired');
assert.equal(H.keyResultOf({ tier: 'free', reason: 'license missing or invalid: the signature did not verify against the Semanticus public key' }), 'invalid');
assert.equal(H.keyResultOf({ tier: 'free', reason: 'license is not a Pro tier' }), 'invalid');
assert.equal(H.keyResultOf(undefined), 'invalid', 'no answer is never a grant');

// What the page may say about the licence: only what is known.
const NOW = 1_790_000_000;
assert.equal(H.accountOf({ tier: 'free' }, '', undefined, NOW), undefined, 'a free session has no account to describe');
assert.deepEqual(H.accountOf({ tier: 'pro', licensedTo: 'you@contoso.com', expiry: NOW + 86400 }, 'sub_1', { sid: 'sub_1', plan: 'yearly' }, NOW),
  { subscription: true, licensedTo: 'you@contoso.com', expiry: NOW + 86400, plan: 'yearly' });
assert.equal(H.accountOf({ tier: 'pro', expiry: NOW + 86400 }, 'sub_2', { sid: 'sub_1', plan: 'yearly' }, NOW).plan, undefined,
  'a plan remembered for another subscription is never shown for this one');
assert.deepEqual(H.accountOf({ tier: 'pro', licensedTo: 'Contoso', expiry: 0 }, '', undefined, NOW), { subscription: false, licensedTo: 'Contoso' },
  'a perpetual licence has no end date and no subscription');
assert.equal(H.accountOf({ tier: 'pro', licensedTo: 'dev (SEMANTICUS_DEV_PRO)' }, '', undefined, NOW).licensedTo, undefined,
  'the dev switch is not a person');
const grace = H.accountOf({ tier: 'pro', expiry: NOW - 86400, reason: 'license expired 2026-09-21; in grace until 2026-10-05. Renew at x to stay on Pro' }, 'sub_1', undefined, NOW);
assert.equal(grace.graceUntil, Date.parse('2026-10-05T00:00:00Z') / 1000, 'the grace end is the engine\'s own date');

// ===================================================================================================
// 1b + 2. The page's decisions and its rendering.
// ===================================================================================================
let stubTier = 'free';
const posted = [];
const navigated = [];
const STUBS = {
  './bridge': {
    rpc: async () => { throw new Error('the test loader answers no calls'); },
    runHostCommand: (command) => posted.push({ type: 'runCommand', command }),
    postProAction: (action, extra) => posted.push({ type: 'pro', action, ...(extra || {}) }),
    onProState: () => () => {}, onReconnect: () => () => {},
    navigateInStudio: (tab) => navigated.push(tab),
    manageLicense: () => posted.push({ type: 'manageLicense' }), showLicense: () => posted.push({ type: 'showLicense' }),
  },
};
const cache = new Map();
function load(file) {
  const full = /\.(ts|tsx|mjs)$/.test(file) ? file : existsSync(file + '.tsx') ? file + '.tsx' : file + '.ts';
  if (cache.has(full)) return cache.get(full);
  const source = readFileSync(full, 'utf8');
  const js = ts.transpileModule(source, {
    compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022, jsx: ts.JsxEmit.ReactJSX, esModuleInterop: true },
    fileName: full.endsWith('.mjs') ? `${full.slice(0, -4)}.ts` : full,
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
const srcOf = (f) => readFileSync(join(webview, 'src', f), 'utf8');
const readable = (html) => html
  .replace(/\s(?:class|style|data-[\w-]+|value|type|id|for|role|checked|disabled|href|width|aria-[\w-]+|autocomplete|spellcheck|placeholder|viewBox|fill|stroke|stroke-width|d|x|y|rx|cx|cy|r)="[^"]*"/g, '')
  .replace(/<[^>]+>/g, ' ')
  .replace(/&quot;/g, '"').replace(/&#x27;/g, "'").replace(/&amp;/g, '&').replace(/&#x2F;/g, '/')
  .replace(/\s+/g, ' ');

const M = load(join(webview, 'src', 'propage-model.ts'));

// --- the chip, both ways, and nothing on a guess.
assert.equal(M.chipOf('free'), 'get', 'free wears Get Pro');
assert.equal(M.chipOf('pro'), 'pro', 'Pro wears the quiet Pro chip');
assert.equal(M.chipOf('unknown'), null, 'an entitlement that has not answered draws no chip, never a guess');

// --- the page's states.
const view = (tier, state, known) => M.proPageView(tier, state, known);
assert.equal(view('free', { purchase: 'idle' }).body, 'free');
assert.equal(view('pro', { purchase: 'idle' }).body, 'pro');
assert.equal(view('unknown', { purchase: 'idle' }).body, 'checking', 'nothing answered and nothing known: say so');
assert.equal(view('unknown', { purchase: 'waiting', plan: 'yearly' }, 'free').body, 'free',
  'an engine restarting mid-purchase keeps the page it was on rather than flashing "checking"');
assert.equal(view('unknown', { purchase: 'done', plan: 'yearly' }, 'free').body, 'pro',
  'the engine has verified the purchase, so the page does not fall back to Buy Pro while the tier is re-read');
assert.equal(view('unknown', { purchase: 'idle', key: { result: 'ok' } }, 'free').body, 'pro');
assert.equal(view('free', { purchase: 'done' }).body, 'free', 'but a tier that answered free is the answer');
const waiting = view('free', { purchase: 'waiting', plan: 'monthly' });
assert.equal(waiting.waiting, true);
assert.equal(view('free', { purchase: 'notOpen' }).notice, 'notOpen');
assert.equal(view('free', { purchase: 'notOpen' }).forceKeyOpen, true, 'not open yet opens the key field');
assert.equal(view('free', { purchase: 'error', message: 'x' }).notice, 'error');
assert.equal(view('pro', { purchase: 'done' }).justOn, true, 'a finished purchase is announced on the Pro page');
assert.equal(view('pro', { purchase: 'idle', key: { result: 'ok' } }).justOn, true, 'and so is a key that turned Pro on');
assert.equal(view('pro', { purchase: 'idle' }).justOn, false, 'coming back later is not a new moment');

// --- dates the way the mockup writes them, in UTC so a machine's zone cannot move a day.
assert.equal(M.longDate(Date.parse('2027-09-23T00:00:00Z') / 1000), '23 September 2027');

// --- rendering. ProPageView is the presentational half; ProPage adds only the hooks.
const P = load(join(webview, 'src', 'propage.tsx'));
const noop = () => {};
const HANDLERS = { onPlan: noop, onBuy: noop, onReopen: noop, onCancel: noop, onKeyOpen: noop, onKey: noop, onActivate: noop, onManage: noop, onRemove: noop };
const render = (tier, state, ui = {}) => readable(renderToStaticMarkup(React.createElement(P.ProPageView, {
  tier, state, plan: 'yearly', keyOpen: false, keyValue: '', ...HANDLERS, ...ui,
})));
const raw = (tier, state, ui = {}) => renderToStaticMarkup(React.createElement(P.ProPageView, {
  tier, state, plan: 'yearly', keyOpen: false, keyValue: '', ...HANDLERS, ...ui,
}));

const LIVE_ADDS = [
  'Jev tidies your measures and ranks your plan. Each change lands on your Plan for you to keep or drop.',
  'The Plan field: type a task and Semanticus names the page it is done on, asking Jev when the words alone do not say.',
  'Workflows: work you repeat becomes steps the app checks as you go.',
];

// h17 P2 and P17: a trial's Pro page has the same Remove as the paid page, and says the days left in words (no token counts).
const trialPro = raw('pro', { purchase: 'idle' }, { trial: { tier: 'pro', isTrial: true, trialEndsAt: Date.parse('2026-11-11T00:00:00Z') / 1000, trialDaysLeft: 30 } });
assert.match(trialPro, /data-testid="pro-remove"/, 'a trial can take the licence off this computer');
assert.match(readable(trialPro), /30 days left/);
assert.doesNotMatch(trialPro, /This month: /);

// Free.
const free = render('free', { purchase: 'idle' });
assert.match(free, /Semanticus Pro/);
assert.match(free, /You are on Semanticus Free\./);
assert.match(free, /Monthly USD 10 a month Stop at the end of any month\./);
assert.match(free, /Yearly USD 100 a year The same as 10 months at the monthly price\./);
assert.match(free, /Buy Pro/);
assert.match(free, /Payment opens in your browser on Stripe's secure page\. Stripe handles tax\. Your license comes back here by itself\./);
assert.doesNotMatch(free, /How buying works/, 'the three steps repeated the lock line under Buy Pro (PR1)');
assert.match(free, /What Pro adds/);
for (const line of LIVE_ADDS) assert.ok(free.includes(line), `What Pro adds says only what is live: ${line}`);
assert.match(free, /Everything else in Semanticus stays free\./);
assert.match(free, /Have a license key\?/);
assert.doesNotMatch(free, /Paste your license key/, 'the key field stays closed until asked for');
assert.doesNotMatch(free, /Finish paying/);
assert.match(raw('free', { purchase: 'idle' }), /role="radio" aria-checked="true"[^>]*data-plan="yearly"|data-plan="yearly"[^>]*aria-checked="true"/,
  'the chosen plan is the checked radio');

// Key field open, then each result line.
const keyOpen = render('free', { purchase: 'idle' }, { keyOpen: true });
assert.match(keyOpen, /Have a license key\?/);
assert.match(keyOpen, /Activate/);
assert.match(keyOpen, /Paste the whole key\. It turns Pro on for this computer\./);
assert.match(raw('free', { purchase: 'idle' }, { keyOpen: true }), /type="password"/, 'the field hides the key as it is pasted');
assert.match(raw('free', { purchase: 'idle' }, { keyOpen: true, keyValue: '' }), /data-testid="pro-activate"[^>]*disabled=""|disabled=""[^>]*data-testid="pro-activate"/,
  'Activate waits for a key');
const keyOk = render('free', { purchase: 'idle', key: { result: 'ok', licensedTo: 'you@contoso.com' } }, { keyOpen: true, keyValue: 'x.y' });
assert.match(keyOk, /Pro is on for this computer\. Licensed to you@contoso\.com\./);
const keyBad = render('free', { purchase: 'idle', key: { result: 'invalid' } }, { keyOpen: true, keyValue: 'x.y' });
assert.match(keyBad, /That key is not valid\. Check it was copied in full\./);
const keyOld = render('free', { purchase: 'idle', key: { result: 'expired' } }, { keyOpen: true, keyValue: 'x.y' });
assert.match(keyOld, /That license has expired\. Renew it or buy Pro again\./);

// Waiting.
const wait = render('free', { purchase: 'waiting', plan: 'yearly' });
assert.match(wait, /Finish paying in your browser\. This page updates by itself\./);
assert.match(wait, /Yearly, USD 100 a year\. Stripe's page is open in your browser\. You can keep working while you wait\./);
assert.match(wait, /Open the payment page again/);
assert.match(wait, /Cancel/);
assert.doesNotMatch(raw('free', { purchase: 'waiting', plan: 'yearly' }), /data-testid="pro-buy"/, 'Buy Pro gives way to the wait');

// Not open yet.
const closed = render('free', { purchase: 'notOpen' });
assert.match(closed, /Buying in the app is not open yet\. If you have a license key, activate it below\./);
assert.match(closed, /Paste the whole key/, 'and the key field is open');

// Success, and coming back later with Pro.
const done = render('pro', { purchase: 'done', plan: 'yearly',
  account: { subscription: true, licensedTo: 'you@contoso.com', expiry: Date.parse('2027-09-23T00:00:00Z') / 1000, plan: 'yearly' } });
// Honesty lane: the banner no longer claims Jev is ready. It says Jev's real state, read from the engine.
assert.doesNotMatch(done, /Jev is ready/, 'the banner never claims Jev is ready without asking the engine');
assert.match(done, /Pro is on for this computer\. Checking whether Jev can be reached\./);
assert.match(done, /Semanticus Pro, yearly\./);
assert.match(done, /Licensed to you@contoso\.com\. Renews 23 September 2027\./);
const later = render('pro', { purchase: 'idle',
  account: { subscription: true, licensedTo: 'you@contoso.com', expiry: Date.parse('2027-09-23T00:00:00Z') / 1000, plan: 'yearly' } });
assert.doesNotMatch(later, /Jev is ready/, 'no banner when nothing just happened');
assert.doesNotMatch(later, /Pro is on for this computer\./, 'the licence card says Pro is on, so no line repeats it (PR4)');
assert.match(later, /Semanticus Pro, yearly\. Licensed to you@contoso\.com\. Renews 23 September 2027\./);
assert.match(later, /Manage subscription/);
assert.match(later, /Change the plan or card, or cancel, on Stripe\./);
assert.match(later, /Remove license from this computer/);
assert.doesNotMatch(later, /On this computer|What Pro adds|Everything else in Semanticus stays free/, 'the side card and the sales list are gone for someone who has Pro (PR2, PR3)');
assert.doesNotMatch(later, /Jev (On|Checking|Not reachable)/, 'no Jev row: an On or Checking state is quiet');
// What Pro decides is a section of the Pro page, folded: its 14 switches show only after Choose which.
assert.match(later, /What Pro decides Turn one off and Semanticus stops asking about it\./);
assert.match(later, /Choose which/);
assert.doesNotMatch(raw('pro', { purchase: 'idle' }), /data-testid="decisions-switch"/, 'the switches start folded');
assert.doesNotMatch(free, /What Pro decides/, 'Free users see nothing extra');

// --- Jev's real state (honesty lane): on, not reachable, or no permission, from get_decision_budget and get_consent.
const RELAY_OK = { route: 'relay', serviceAvailable: true, monthlyUsedInputTokens: 5, monthlyCapInputTokens: 10 };
assert.equal(M.jevStateOf(null, null, false).state, 'checking');
assert.equal(M.jevStateOf(null, null, true).state, 'unreachable', 'a budget read that failed is not a Jev that is on');
assert.equal(M.jevStateOf({ route: 'none', serviceAvailable: false }, { current: true }, true).state, 'unreachable');
const down = M.jevStateOf({ route: 'relay', serviceAvailable: true, monthlyUsedInputTokens: null, monthlyReason: 'the advice service could not be reached' }, { current: true }, true);
// Kane, 8 Oct (r1 join): one calm sentence, the same words as the Plan and the Ask; the engine's reason stays in its log.
assert.deepEqual([down.state, down.word, down.line], ['unreachable', 'Not reachable', 'Jev is not available right now.']);
const noPermission = M.jevStateOf(RELAY_OK, { current: false }, true);
assert.deepEqual([noPermission.state, noPermission.word], ['noConsent', 'No permission']);
assert.match(noPermission.line, /until you give permission/);
const jevOn = M.jevStateOf(RELAY_OK, { current: true }, true);
assert.deepEqual([jevOn.state, jevOn.word, jevOn.line], ['on', 'On', 'Jev is on for this model.']);
assert.equal(M.jevStateOf(RELAY_OK, null, true).state, 'on', 'no model open: reachable is on, and the line says permission comes per model');
assert.match(M.jevStateOf(RELAY_OK, null, true).line, /permission/);
const DS = load(join(webview, 'src', 'decisions-settings.tsx'));
// Harm fix (r1 min4): a getDecisionPoints reply that is not a list blanked all of Studio ("(points ?? []).map is not a
// function"). Whatever the engine answers, the rows are a list.
for (const odd of [{}, { error: 'x' }, 'nope', 7, true]) assert.deepEqual(DS.pointsOf(odd), [], `a reply of ${JSON.stringify(odd)} is no rows, not a crash`);
assert.deepEqual(DS.pointsOf(null), []);
assert.deepEqual(DS.pointsOf(undefined), []);
assert.deepEqual(DS.pointsOf([{ name: 'a' }]), [{ name: 'a' }], 'a real list passes through');

const laterOn = render('pro', { purchase: 'idle', account: { subscription: false } }, { jev: jevOn });
assert.doesNotMatch(laterOn, /Jev is not available|asks nothing about this model/, 'Jev being on is the quiet default');
const laterDown = render('pro', { purchase: 'done', account: { subscription: false } }, { jev: down });
assert.match(laterDown, /Pro is on for this computer\. Jev is not available right now\./);
assert.match(raw('pro', { purchase: 'idle', account: { subscription: false } }, { jev: down }), /data-testid="pro-jev"/, 'a Jev that is not reachable is said on the licence card too');
const laterNoPermission = render('pro', { purchase: 'idle', account: { subscription: false } }, { jev: noPermission });
assert.match(laterNoPermission, /Jev asks nothing about this model until you give permission\./);
// Only what is known.
const bare = render('pro', { purchase: 'idle', account: { subscription: false } });
assert.match(bare, /Semanticus Pro\./);
assert.doesNotMatch(bare, /Licensed to|Renews|yearly|monthly|Manage subscription/,
  'an old licence with nothing known shows nothing invented, and no Stripe page it does not have');
const fixedTerm = render('pro', { purchase: 'idle', account: { subscription: false, expiry: Date.parse('2027-01-31T00:00:00Z') / 1000 } });
assert.match(fixedTerm, /Runs until 31 January 2027\./, 'a licence that does not renew is not said to');

// Every string on the page is plain: no em dashes.
for (const html of [free, keyOpen, keyOk, keyBad, keyOld, wait, closed, done, later, bare]) assert.doesNotMatch(html, /\u2014/);

// The chip, rendered.
const chip = (tier, here = false) => renderToStaticMarkup(React.createElement(P.ProChipView, { tier, here, onOpen: noop }));
assert.match(readable(chip('free')), /Get Pro/);
assert.match(chip('free'), /pro-chip-get/);
assert.match(readable(chip('pro')), /Pro/);
assert.doesNotMatch(readable(chip('pro')), /Get Pro/);
assert.match(chip('pro'), /pro-chip-is/);
assert.equal(chip('unknown'), '', 'no chip on a guess');
// The licence is Pro but the Pro service refused it: the chip says so and carries a Billing link, so the screen never says Pro and refuses Pro at once.
const off = renderToStaticMarkup(React.createElement(P.ProChipView, { tier: 'pro', here: false, onOpen: noop, planInactive: true, billingUrl: 'https://semanticus.com.au/pro' }));
assert.match(readable(off), /Pro, plan not active/);
assert.match(off, /data-testid="pro-chip-billing"[^>]*>Billing</);
assert.doesNotMatch(readable(chip('pro')), /Billing|not active/, 'an active plan shows neither');
assert.doesNotMatch(readable(renderToStaticMarkup(React.createElement(P.ProChipView, { tier: 'free', here: false, onOpen: noop, planInactive: true }))), /not active/, 'a free session never shows the Pro plan as inactive');

// ===================================================================================================
// 3. The wiring.
// ===================================================================================================
const pro = srcOf('pro.tsx');
const app = srcOf('App.tsx');
const help = srcOf('help.tsx');
const bridge = srcOf('bridge.ts');
const extension = readFileSync(join(root, 'src', 'extension.ts'), 'utf8');
const pkg = JSON.parse(readFileSync(join(root, 'package.json'), 'utf8'));

// Every Unlock Pro goes to the page. upgradeToPro is the one seam every Unlock Pro calls; it navigates.
const proModule = load(join(webview, 'src', 'pro.tsx'));
navigated.length = 0; posted.length = 0;
proModule.upgradeToPro();
assert.deepEqual(navigated, ['pro'], 'Unlock Pro opens the Pro page');
assert.equal(posted.length, 0, 'and starts no host flow on its own');
for (const file of ['propreview.tsx', 'plan.tsx', 'askbox.tsx', 'decisions-settings.tsx', 'pro.tsx']) {
  const source = srcOf(file);
  for (const m of source.matchAll(/>Unlock Pro</g)) {
    const before = source.slice(Math.max(0, m.index - 400), m.index);
    // m15 trial: a locked button may hand over what was pressed, so it runs once Pro is on.
    assert.match(before, /onClick=\{upgradeToPro\}|onClick=\{\(\) => \{ close\(\); upgradeToPro\(\); \}\}|onClick=\{\(\) => upgradeToPro\(\{ label: '[^']+', tab: '[a-z]+', run: \w+ \}\)\}/, `${file}: an Unlock Pro that does not open the Pro page`);
  }
}
assert.doesNotMatch(pro, /onClick=\{manageLicense\}/, 'the upsell notice no longer sends anyone to the website');
assert.doesNotMatch(pro, /runHostCommand\('semanticus\.upgradeToPro'\)/, 'no Unlock Pro reaches the old quick pick');
const webviewSources = ['App.tsx', 'propreview.tsx', 'plan.tsx', 'askbox.tsx', 'decisions-settings.tsx', 'pro.tsx', 'help.tsx', 'dataagent.tsx', 'propage.tsx'];
for (const file of webviewSources) {
  assert.doesNotMatch(srcOf(file), /semanticus\.com\.au/, `${file} links to the website`);
  assert.doesNotMatch(srcOf(file), /semanticus\.upgradeToPro|semanticus\.manageSubscription/, `${file} reaches a licence command around the page`);
}

// Help has NO Semanticus Pro item (8 Oct 2026): the header chip (Get Pro, or a quiet Pro) is the one Pro door and
// opens the page on every screen.
assert.doesNotMatch(help, /id: 'get-pro'|id: 'manage-subscription'|label: 'Get Pro'|label: 'Manage subscription'/,
  'Get Pro and Manage subscription left Help');
assert.doesNotMatch(help, /section: 'Semanticus Pro'|id: 'semanticus-pro'/, 'and so did the Semanticus Pro item: the header chip is the door');

// The page is a real Studio route, a detour with Done.
assert.match(readFileSync(join(webview, 'src', 'route.ts'), 'utf8'), /pro: 'utility'/);
// Every id resolves through route.ts's landingOf, which accepts any tool DESTINATION_OF_TOOL names.
assert.match(app, /const land = landingOf\(raw\);/, 'pro is a valid tab: goTab resolves it through the landing table');
assert.match(app, /tab === 'pro' \? \([\s\S]{0,400}<ProPage \/>/);
assert.match(app, /<ProChip here=\{tab === 'pro'\}/, 'the chip is in the header');
// Just left of Your assistant.
const header = app.slice(app.indexOf('<HelpButton tab={tab}'), app.indexOf('<div className="studio-publish-block">'));
assert.ok(header.indexOf('<ProChip') > 0 && header.indexOf('<ProChip') < header.indexOf('<LiveActivity'), 'between Help and Your assistant');

// On a folded header (1000 wide) the chip keeps its place and gives up its words. The fold tries each level on
// the real layout, so the chip's narrower level-3 width counts without a copy of it in App.tsx
// (tools/uishot/shell-journey.mjs holds the areas at 1000 and sweeps the widths).
assert.doesNotMatch(app, /FOLD_PRO_CHIP_WIDTH/);
assert.match(srcOf('styles.css'), /\[data-fold="3"\], \[data-fold="4"\]\) \.pro-chip \{ width: 26px;/);

// The bridge carries the page's clicks to the host and the host's state back.
assert.match(bridge, /export function postProAction\(/);
assert.match(bridge, /export function onProState\(/);
assert.match(bridge, /msg\.type === 'proState'/);

// The host: the plan the page names is the plan the checkout opens for.
assert.match(extension, /msg\?\.type === 'pro'\) \{ void handleProPageMessage\(/, 'the Studio relay hands page clicks to one handler');
assert.match(extension, /case 'buy': \{[\s\S]{0,300}planFromMessage\(msg\.plan\)[\s\S]{0,400}upgradeToProCmd\(context, plan\)/);
assert.match(extension, /async function upgradeToProFlow\(context: vscode\.ExtensionContext, plan: BillingPlan\)[\s\S]{0,400}createCheckout\(billingBaseUrl\(\), plan, nonce\)/,
  'and upgradeToProFlow hands exactly that plan to createCheckout');
assert.doesNotMatch(extension, /showQuickPick<vscode\.QuickPickItem & \{ plan: BillingPlan \}>/, 'the plan quick pick is gone');
// Webview content can no longer fire the licence commands at all: the page's clicks go through the one
// handler above, which checks each message's shape, and nothing in Studio needs the old commands.
const allowlist = (extension.match(/const WEBVIEW_HOST_COMMANDS = new Set\(\[([^\]]*)\]\)/) || [])[1];
assert.doesNotMatch(allowlist, /upgradeToPro|manageSubscription/, 'the licence commands left the webview allowlist');
assert.match(extension, /checkoutStartFailure\(e\)/, 'a checkout that cannot start is sorted, not thrown at the person');
// The key reuses activateLicenseFlow, with the pasted token, and never an input box.
assert.match(extension, /case 'activate': \{[\s\S]{0,1200}activateLicenseFlow\(context, \{ token/);
assert.match(extension, /async function activateLicenseFlow\(context: vscode\.ExtensionContext, opts\?: ActivateOptions\)/);
assert.match(extension, /opts\?\.token !== undefined \? opts\.token : await vscode\.window\.showInputBox/);
// Manage subscription is the existing command.
assert.match(extension, /case 'manage':[\s\S]{0,120}manageSubscriptionCmd\(context\)/);
// Licence activation stays a person-only action: no assistant door reaches it.
const mcpSources = ['McpTools.cs', 'McpToolsAuthoring.cs'].map((f) => join(root, '..', 'Semanticus.Engine', f)).filter(existsSync);
for (const f of mcpSources) assert.doesNotMatch(readFileSync(f, 'utf8'), /activateLicense|storeLicense|licenseToken/i, `${f} must not reach licence storage`);
// "Pro Plans and Support" and the palette's Get Pro open the page, not the website.
assert.match(extension, /registerPersonCommand\('semanticus\.manageLicense', \(\) => openProPage\(context\)\)/);
// m15 trial: the status bar's trial items say where they came from; every path still ends on the page.
assert.match(extension, /registerPersonCommand\('semanticus\.upgradeToPro', \(from\?: unknown\) => \{[\s\S]{0,400}?openProPage\(context\);\s*\}\)/);
assert.match(extension, /function openProPage\(context: vscode\.ExtensionContext\)[\s\S]{0,200}navigateStudio\(context, 'pro'\)/);
assert.ok(pkg.contributes.commands.some((c) => c.command === 'semanticus.manageLicense'), 'the palette keeps its way in');

// What Pro decides: rows are for one named model (switch-scope round 4). A click with no model id changes nothing and
// says why; the panel names the model; a notice from the engine under the same id reads the rows again.
{
  const panel = srcOf('decisions-settings.tsx');
  assert.match(panel, /if \(!rowsFor\.current\) \{ setError\(NO_MODEL_ID\); return; \}[\s\S]{0,80}setBusy\(name\)/,
    'a click whose rows cannot name their model fails closed, before any call');
  assert.match(panel, /export const NO_MODEL_ID = '[^']*nothing was changed\./);
  assert.match(panel, /data-testid="decisions-model"[\s\S]{0,200}\{modelName\}/, 'the panel names the model its rows are for');
  assert.match(panel, /id === rowsFor\.current\) \{[\s\S]{0,500}n\.origin === 'system'\) setReads/,
    'an engine notice under the same id reads the rows again');
  assert.doesNotMatch(panel, /\u2014/, 'no em dash in the panel');
}

// What Pro decides is a section of the Pro page, not a window: no dialog, nothing mounted for a Free person, the
// 14 switches folded behind "Choose which", and each row's "what is sent" behind its own small button.
{
  const src = readFileSync(join(webview, 'src', 'decisions-settings.tsx'), 'utf8');
  assert.doesNotMatch(src, /role="dialog"|aria-modal|openDecisionSettings|Unlock Pro/, 'no window, no menu opener, no Free upsell here');
  assert.match(src, /data-testid="decisions-fold"[\s\S]{0,200}Choose which \(\$\{rows\.length\}\)/, 'the switches fold under Choose which (N)');
  assert.match(src, /\{choosing && \([\s\S]{0,120}data-testid="decisions-body"/, 'the rows exist only after Choose which is pressed');
  assert.doesNotMatch(src, /data-testid="decisions-sent"[\s\S]*\(points \?\? \[\]\)\.map/, 'no second list of the 14 labels');
  assert.doesNotMatch(src, /next \?\? \[\]/, 'a reply that is not a list is never passed on as rows (harm fix)');
  assert.match(srcOf('propage.tsx'), /<DecisionSettingsSection \/>/, 'the Pro page mounts the section');
  const proBody = srcOf('propage.tsx');
  const freeBody = proBody.slice(proBody.indexOf('function FreeBody('), proBody.indexOf('function ProBody('));
  assert.doesNotMatch(freeBody, /DecisionSettingsSection/, 'never in the Free body');
  const activity = srcOf('activity.tsx');
  assert.doesNotMatch(activity, /what Pro decides|openDecisionSettings/i, 'Your assistant menu lost the item');
}

console.log('pro-in-app: ok');
