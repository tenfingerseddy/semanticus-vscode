// Acceptance driver for the Free and Pro line as Kane reset it on 22 September 2026: free is the tool, Pro
// is the tool that works for you.
//
// Every shot is the REAL Studio driven through the mock engine at 1366x768, not a drawing. The harness's
// entitlement fixture is an effective FEATURE GRANT (?tier=pro, or ?features=<comma list> for exactly those
// ids), and every paid operation is wrapped so a plan that does not grant its feature gets the engine's own
// refusal instead of data. That is what makes the free shots mean something: if a preview or a suppressed
// card ever called a gated read, the page would show a refusal and the trace below would name the call.
//
// WHAT CHANGED ON 22 SEPTEMBER. This driver walked NINE locked previews. Eight of those tabs are free now
// (Model Spec, Advanced modelling, Power Query, Docs, Model notes, Validation, Reports, and the Advanced
// half of Published with the Data Agent inside it), so the previews it was photographing no longer exist
// and the scenes that asserted them were photographing nothing. One tab is still gated: Workflows.
//
// So the shot list turned over. Half of it is the same proof aimed at the one remaining boundary, and the
// other half is the COUNTER-PROOF the freeing needs and nothing had: each freed tab, on a FREE session,
// rendering its real page, with no preview, no Pro pill, and no refusal anywhere on screen. A tab that was
// freed in features.ts but still refused by the engine's mock would show its refusal here, and a tab that
// kept a stale pill would show that too.
//
// WHAT IS PROVEN HERE: that the page reaches one boundary from every entry path, that the locked tool
// renders its preview and never mounts the real page, that the preview makes no engine call at all, that
// every freed tab opens for real on free, and that a reconnect flips the whole thing live.
//
// WHAT IS NOT: that the ENGINE refuses anything. The harness fakes the engine; the engine half is the
// engine lane's, and rpc-name-parity covers only that the names exist.
//
// AND NOT THE OTHER TWO PAID THINGS. Pro is three things, not one: Workflows, the plan ranked, scored and
// run, and the ask box. The other two are keyed on the TIER and have no wire id (features.ts:50-56), they
// live on the Plan page (the Overview's share of them retired with it in slice 11), and they are driven, with measurements rather than
// eyeballs, by tools/uishot/cp-plan-drive.mjs: free-list, free-pill, free-item-open, free-map,
// free-overview-card and free-ask-pill. They are NOT duplicated here. That driver has its own plan fixture
// (fixtures/plan-fixture.mjs); harness.html mocks neither routeAsk nor the model plan, so a second copy
// here would need a second fixture and would prove the fixture, not the page.
//
//   OUTDIR=<dir> TMPDIR=<dir> SEMANTICUS_BROWSER=/usr/bin/chromium node tools/uishot/cp-pro-drive.mjs
import { createServer } from 'node:http';
import { existsSync, mkdirSync, readFileSync, statSync, writeFileSync } from 'node:fs';
import { extname, join, normalize, resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { findBrowser, requireSupportedNode } from './browser.mjs';
import { checkScreen, screenFailures } from './screencheck.mjs';
import { connectionsRoot } from './visual-scenes.mjs';
requireSupportedNode();
import { createRequire } from 'node:module';
const require_ = createRequire(import.meta.url);
const puppeteer = require_('puppeteer-core').default ?? require_('puppeteer-core');

const here = dirname(fileURLToPath(import.meta.url));
const webRoot = resolve(here, '..', '..');            // Semanticus.VSCode
const OUT = process.env.OUTDIR || resolve(webRoot, 'shots', 'cp-pro');
mkdirSync(OUT, { recursive: true });
const TYPES = { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.json': 'application/json', '.png': 'image/png', '.svg': 'image/svg+xml', '.woff2': 'font/woff2', '.map': 'application/json' };
const server = createServer((req, res) => {
  const url = req.url || '/';
  const p = normalize(join(webRoot, decodeURIComponent(url.split('?')[0].split('#')[0])));
  if (!p.startsWith(webRoot) || !existsSync(p) || statSync(p).isDirectory()) { res.writeHead(404); res.end('nf'); return; }
  res.writeHead(200, { 'content-type': TYPES[extname(p).toLowerCase()] || 'application/octet-stream' });
  res.end(readFileSync(p));
});
await new Promise((r) => server.listen(0, '127.0.0.1', r));
const port = server.address().port;
// findBrowser prefers a cached chrome-headless-shell. A fallback to a full browser can hand the launch to a
// running chromium and close the session mid-drive, which reads as a component failure and is not one.
const executablePath = process.env.SEMANTICUS_BROWSER || findBrowser();
// Chromium's own scratch, on real disk. /tmp on this machine is a tmpfs that other work fills AND carries a
// user quota, so a screenshot written there fails as "Unable to capture screenshot" or "Unknown system
// error -122", both of which read exactly like a component fault and are not one.
const SCRATCH = resolve(process.env.HOME ?? '/tmp', '.cache', 'semanticus-uishot', `pro-${process.pid}`);
mkdirSync(SCRATCH, { recursive: true });
const browser = await puppeteer.launch({
  executablePath, headless: true, protocolTimeout: 180000,
  args: ['--no-sandbox', '--disable-gpu', '--disable-dev-shm-usage', '--force-color-profile=srgb', '--hide-scrollbars',
    `--user-data-dir=${SCRATCH}`],
  // Times on screen are formatted in the viewer's own zone. Pin the browser to UTC so a shot taken on one
  // machine reads the same as a shot taken on another.
  env: { ...process.env, TZ: 'UTC' },
});
const wait = (ms) => new Promise((r) => setTimeout(r, ms));
const LABEL = process.env.SHOTLABEL || 'after';
// SCENES=<comma list> drives only those scenes. The full set takes a while and this machine is shared.
const ONLY = (process.env.SCENES || '').split(',').map((x) => x.trim()).filter(Boolean);

// Every operation behind a Pro feature, by its rpc name. The driver FAILS when a scene on a plan that does
// not grant the feature makes one of these calls. Kept beside the harness's own wrapped set on purpose: the
// harness refuses them, this list judges them, and a name missing from either is a hole in both.
//
// THREE LISTS WENT ON 22 SEPTEMBER, with the gates they judged: modelCreate (69 ops), tests (20) and
// publishedAdvanced (23). Every name they held is free now, so judging a free session for calling one
// would fail a page for working. They are not commented out here: the freed tabs are judged from the
// other side instead, by FREED_TOOLS below, which requires the real page and forbids a refusal.
const PAID_CALLS = {
  workflows: ['listWorkflows', 'getWorkflow', 'getWorkflowDocument', 'getWorkflowLayout', 'getWorkflowPolicy',
    'getWorkflowEnforcement', 'getWorkflowTemplate', 'listWorkflowTemplates', 'listWorkflowProfiles',
    'saveWorkflow', 'saveWorkflowLayout', 'saveWorkflowTemplate', 'deleteWorkflow', 'deleteWorkflowTemplate',
    'startWorkflow', 'submitWorkflowStep', 'skipWorkflowStep', 'abortWorkflow', 'checkWorkflow',
    'replayCheckWorkflow', 'editWorkflowDocument', 'previewWorkflowEdit', 'exportWorkflowEvidence',
    'instantiateWorkflowTemplate', 'upgradeWorkflow', 'activateWorkflowProfile', 'setWorkflowActivation',
    'setWorkflowBinding', 'setWorkflowEnabled', 'setWorkflowEnforcement'],
};
const ALL_FEATURES = Object.keys(PAID_CALLS);
const paidCallsFor = (granted) => new Set(ALL_FEATURES.filter((f) => !granted.includes(f)).flatMap((f) => PAID_CALLS[f]));

// Every tool the free plan does not reach, and the feature it belongs to. Kept in the driver so a shot list
// and webview/src/features.ts cannot drift apart silently: the seen block below reads the feature back off
// the page and the results name both. It is ONE row now, which is the whole shape of the 22 September
// change: FEATURE_OF_TOOL in features.ts has exactly one entry and so does this.
const LOCKED_TOOLS = [
  ['workflows', 'Workflows'],
];

// The eight tabs that STOPPED being paid, by the name the area strip gives them today, each with ONE rpc
// its real page makes on mount. Every one of those calls was on a PAID_CALLS list here until 22 September,
// so the third column is the counter-proof stated precisely: on a free session the page mounts, makes the
// call that used to be refused, and is answered.
//
// The call matters more than it looks. "No preview rendered" is satisfied just as well by a tab that has
// quietly stopped existing, and strip membership cannot stand in for it either: the Edit area folds
// Advanced modelling and Power Query into a dropdown, so neither has a [data-tab] button to find, and
// Reports is a tab inside Validation. Asking what the page CALLED is the one question that has the same
// answer however the strip is drawn. `dataagent` was the legacy route into the Advanced half of Published; that
// half is hidden now and the route lands on the Changes tab.
// A freed page may still hold ONE paid section. Model notes is the only one today: the notes are free and
// the Saved workflows list inside them is not, so that section shows the quiet pill and makes no call.
const FREED_PILLS = {
  knowledge: ['knowledge-workflows-pro-pill'],
};

const FREED_TOOLS = [
  ['spec', 'Model Spec', 'getSpec'],
  ['advmodels', 'Advanced modelling', 'getPerspectives'],
  ['mcode', 'Power Query', 'getIncrementalRefreshPolicy'],
  ['docs', 'Docs', 'getDocOutline'],
  ['knowledge', 'Model notes', 'getPrimer'],
  ['tests', 'Validation', 'listTests'],
  ['evidence', 'Reports', 'listEvidence'],
];

// `deep` uses the ?tab= route, which selects by the stable data-tab / data-tool attributes. `hash` uses the
// old text matcher, kept only where the target is free and its label is unchanged.
const JOBS = [
  // ---- 1. the ONE tool still behind the line: locked on free, real on Pro -------------------------
  ...LOCKED_TOOLS.map(([tool, label]) => ({
    slug: `locked-${tool}-free`, q: `tab=${tool}`, expect: { preview: tool },
    desc: `${label} on the free plan: the preview in its place`,
  })),
  ...LOCKED_TOOLS.map(([tool, label]) => ({
    slug: `locked-${tool}-pro`, q: `tier=pro&tab=${tool}`, expect: { preview: null },
    desc: `${label} on Pro: the real page`,
  })),

  // ---- 2. THE COUNTER-PROOF: every freed tab opens for real on a FREE session ----------------------
  // These are the scenes the 22 September change actually needs. Each one opens on free and must show
  // the real page: no preview, no pill on its strip button, and no refusal sentence anywhere.
  ...FREED_TOOLS.map(([tool, label, call]) => ({
    slug: `freed-${tool}`, q: `tab=${tool}`, settle: 2600, freed: tool, mustCall: call,
    expect: { preview: null, pills: FREED_PILLS[tool] ?? [] },
    desc: `${label} on the free plan: the real page, because it is not paid any more`,
  })),
  // Model notes is free AND holds one paid section, a shape no other page has: the page opens in full,
  // the Saved workflows section shows the quiet pill in place of the list, and listWorkflows is not
  // called at all. PAID_CALLS above judges the call; `pills` judges what stands there instead. This is
  // the Pro half of the same page, and it must still read the library.
  { slug: 'pro-knowledge', q: 'tier=pro&tab=knowledge', settle: 2600, mustCall: 'listWorkflows',
    expect: { preview: null },
    desc: 'Model notes on Pro: the Saved workflows section reads the library, as it always did' },
  // The same free page, scrolled to the pill, because the section sits well below the fold and a shot of
  // the top of the page is not a shot of the thing this fix put there.
  { slug: 'freed-knowledge-pill', q: 'tab=knowledge', settle: 2600, freed: 'knowledge', mustCall: 'getPrimer',
    scrollTo: '[data-testid="knowledge-workflows-pro-pill"]',
    expect: { preview: null, pills: ['knowledge-workflows-pro-pill'] },
    desc: 'the quiet pill where the workflow library stands on Pro, and no call behind it' },

  // ---- 3. every entry path reaches the SAME boundary, which is now Workflows ------------------------
  // The strip itself: the Workflows area button, then its one segment, both by stable id. No deep link,
  // no host message: this is a person clicking two buttons.
  // Workflows is an area with ONE tool, and App.tsx draws the segment row only when an area has more than
  // one (showStrip), so there is no second button to click here and asking for one would be asking for a
  // control that does not exist. The area button IS the whole path.
  { slug: 'entry-row', q: '', clickArea: 'Workflows', expect: { preview: 'workflows' },
    desc: 'entry by the area strip: the Workflows button, which is the whole area' },
  // Find a tool, by stable tool id, because a Pro pill breaks any match on the button text.
  { slug: 'entry-findatool', q: '', findTool: 'workflows', expect: { preview: 'workflows' },
    desc: 'entry by Find a tool' },
  // Keyboard cycling. The host sends cycle:next; the driver sends the same message the host sends.
  { slug: 'entry-keyboard', q: '', cycleTo: 'workflows', expect: { preview: 'workflows' },
    desc: 'entry by keyboard cycling' },
  // Host navigation: a goTab from the extension, with its seed intact.
  { slug: 'entry-hostnav', q: '', hostNav: 'workflows', expect: { preview: 'workflows' },
    desc: 'entry by host navigation' },
  // A deep link straight at the locked tool.
  { slug: 'entry-deeplink', q: 'tab=workflows', expect: { preview: 'workflows' }, desc: 'entry by deep link' },
  // A cold hand-off from the host, flushed on studioReady. See section 9 below for why this, and not a
  // persisted route, is what "restored" means in this app.
  { slug: 'entry-coldnav', q: 'coldNav=workflows', settle: 2600, grants: [], expect: { preview: 'workflows' },
    desc: 'entry by a cold hand-off from the host' },
  // The legacy dataagent id lands on the Publish page: the Data Agent screen is hidden for now (8 October 2026).
  { slug: 'entry-legacy-dataagent', q: 'tab=dataagent', settle: 2600,
    expect: { preview: null }, desc: 'the legacy dataagent route lands on the Publish page, Changes tab' },

  // ---- 4. Published: nothing inside it is gated any more -------------------------------------------
  { slug: 'freed-published-basic', q: 'tab=deploy', expect: { preview: null },
    desc: 'Publish on free: Changes, Earlier versions and Promote all live' },

  // ---- 5. the start page, which waits for no entitlement to open --------------------------------------
  // The Overview retired in slice 11 (lane R1). Its free homes moved: the start page is the Plan, Raise
  // compatibility level is on the Plan's facts line, and suggested notes are decided on Edit > AI instructions.
  { slug: 'free-plan', q: 'tab=plan', settle: 1400, expect: { preview: null },
    desc: 'the Plan on free: the start page opens for everyone' },
  { slug: 'pro-plan', q: 'tier=pro&tab=plan', settle: 1400, expect: { preview: null },
    desc: 'the Plan on Pro: the same page' },
  { slug: 'free-instructions-suggestions', q: 'tab=instructions', settle: 2600, scrollTo: '[data-testid="overview-suggestion"]',
    expect: { preview: null, suggestions: true }, desc: 'the free home for suggested notes, with Accept and Reject' },
  // The other free home. Absent by default, which is the honest state on an engine that does not report the
  // level; ?compat=1604 is a model whose level is actually holding calendars back.
  { slug: 'free-plan-raise-level', q: 'tab=plan&compat=1604', settle: 2600, expect: { raiseLevel: 'Raise compatibility level' },
    desc: 'the free Raise compatibility level action, on a model that needs it' },
  { slug: 'free-plan-level-ok', q: 'tab=plan&compat=1701', settle: 2600, expect: { raiseLevel: null },
    desc: 'the same page on a model that does not: no nag' },

  // ---- 6. Connections reads source usage from the free projection ----------------------------------
  // Slice C removed the SQL sources section and its rail: each model row now names its SQL endpoint, and
  // saved sources no model uses are listed under "Not linked to a model", on the one page.
  { slug: 'free-connections-usage', q: 'view=connections', settle: 1200,
    desc: 'Connections on free: each model names its SQL endpoint, and unused saved sources are listed apart' },

  // ---- 7. a PAID tier whose features list is empty --------------------------------------------------
  // This replaces the old three-features-of-four scene, which cannot exist when there is one feature. It
  // is the shape a token minted by a newer engine for a plan without Workflows would have, and the page
  // must believe the array rather than the tier: paid, and still locked.
  { slug: 'pro-tier-no-features', q: 'tier=pro&features=&tab=workflows', grants: [],
    expect: { preview: 'workflows' },
    desc: 'a paid tier that grants no feature: Workflows is still locked, because the array is the answer' },

  // ---- 8. a reconnect flips the entitlement live, BOTH ways -----------------------------------------
  { slug: 'reconnect-before', q: 'tab=workflows', grants: [], expect: { preview: 'workflows' },
    desc: 'Workflows locked, before a licence lands' },
  { slug: 'reconnect-after', q: 'tab=workflows', reconnectTo: 'pro', settle: 1600, grants: ALL_FEATURES,
    expect: { preview: null }, desc: 'the same page after a licence lands and the engine reconnects' },
  // The direction that actually loses money if it is wrong. A licence lapses, the engine restarts, and the
  // page must stop being Pro AT ONCE, not when the new answer eventually arrives.
  { slug: 'reconnect-downgrade', q: 'tier=pro&tab=workflows', reconnectTo: 'free', settle: 1600, grants: [],
    expect: { preview: 'workflows' }, desc: 'a licence lapses: Workflows locks again on reconnect' },

  // ---- 9. an entitlement that has NOT answered is not a grant ---------------------------------------
  // Astra's P1. Every scene here holds getEntitlement in flight and asserts that nothing paid is called,
  // nothing paid is mounted, and the page says it is checking rather than guessing. There are fewer of
  // them than there were, and that is the point: the Overview checks card and the Advanced half of
  // Published no longer ask, so they no longer have a state of waiting to be told.
  { slug: 'pending-workflows', q: 'tab=workflows&hold=getEntitlement', settle: 2600, grants: [],
    expect: { checking: true, preview: null },
    desc: 'the paid tab while the plan is unknown: neither the page nor the preview' },
  // The other half, and the one that would catch a freed tab still keyed on the entitlement: a free page
  // must open FULLY while the plan is unknown, because its answer no longer depends on the answer.
  { slug: 'pending-freed-page', q: 'tab=tests&hold=getEntitlement', settle: 2600, grants: [], freed: 'tests',
    expect: { preview: null, checking: false },
    desc: 'a freed tab while the plan is unknown: the real page, because it stopped asking' },
  { slug: 'pending-plan', q: 'tab=plan&hold=getEntitlement', settle: 2600, grants: [],
    expect: { preview: null, checking: false },
    desc: 'the start page while the plan is unknown: it opens, and only Run the plan waits to be told' },
  // Released LATE, with the answer the fixture holds at that moment. Proves the pending state resolves.
  { slug: 'pending-then-free', q: 'tab=workflows&hold=getEntitlement', settle: 1400, release: 'getEntitlement',
    after: 1800, grants: [], expect: { preview: 'workflows' },
    desc: 'the plan answers free: the preview arrives' },
  { slug: 'pending-then-pro', q: 'tier=pro&tab=workflows&hold=getEntitlement', settle: 1400, release: 'getEntitlement',
    after: 1800, grants: ALL_FEATURES, expect: { preview: null }, desc: 'the plan answers Pro: the real page arrives' },
  // A FAILED entitlement read is not a grant either. pro.tsx publishes 'unknown' on a rejection.
  { slug: 'entitlement-failed', q: 'tab=workflows&ent=fail', settle: 2600, grants: [],
    expect: { checking: true, preview: null }, desc: 'the entitlement read fails: still not a grant' },

  // ---- 10. the COLD HAND-OFF, which is what "a restored route" actually is here ---------------------
  // MEASURED, not assumed: App.tsx keeps no persisted route. `useState<Route>` always starts on the Plan,
  // and there is no loadState for it anywhere in the file. So a page you were on does not come back from
  // webview state; what comes back is the HOST re-navigating. extension.ts queues that navigation and
  // flushes it on studioReady, which can beat the webview's own sessionInfo call home, and that race is
  // exactly where a boundary keyed on the open tool could be skipped. The seed it carries must survive too.
  { slug: 'restored-cold-locked', q: 'coldNav=workflows&sessionDelay=1200', settle: 3200,
    grants: [], expect: { preview: 'workflows' },
    desc: 'a cold hand-off into the paid tool, landing before the session does: the preview, never the page' },
  // The seed half, now driven on a FREE session into a tab that used to be paid. It proves two things at
  // once: Validation opens for real without a licence, and the measure it was handed survives the race.
  { slug: 'restored-cold-seed-free', q: 'coldNav=tests:measure:Sales/Total Sales&sessionDelay=1200', settle: 3200,
    grants: [], freed: 'tests', expect: { preview: null, seedKept: true },
    desc: 'the same hand-off into a freed tool on free: the real page, and it keeps the measure' },
  // The UNSTAMPED shape the host sends when it has no session id of its own yet. Studio deliberately drops
  // an unstamped route when the session arrives (a real session change is a different model, so the page
  // you were on is forgotten), and it lands on the Plan. That is existing, intended behaviour and not this
  // lane's to change. What this scene holds is the part that IS this lane's: however that race resolves,
  // it never resolves onto a paid page or a paid call.
  { slug: 'restored-cold-unstamped', q: 'coldNav=workflows&coldNavStamp=none&sessionDelay=1200', settle: 3200,
    grants: [], expect: { preview: null }, desc: 'an unstamped cold hand-off is dropped, and never onto a paid page' },
];

const clickText = (spec) => {
  const [sel, text] = spec;
  const el = [...document.querySelectorAll(sel)]
    .find((x) => (x.textContent || '').trim().startsWith(text) && x.getClientRects().length);
  if (el) el.click();
  return !!el;
};

const results = [];
for (const job of JOBS) {
  if (ONLY.length && !ONLY.includes(job.slug)) continue;
  const page = await browser.newPage();
  const errors = [];
  // EVERY rpc the page makes, in order. This is the evidence that a preview calls nothing: the trace for a
  // locked tool must contain no operation belonging to its feature.
  const calls = [];
  page.on('pageerror', (e) => errors.push('pageerror: ' + e.message));
  page.on('console', (m) => { if (m.type() === 'error') errors.push('console: ' + m.text()); });
  await page.setViewport({ width: 1366, height: 768, deviceScaleFactor: 2 });
  let note = '';
  let midFlight = null;
  let callsBeforeReconnect = 0;
  const absent = [];
  try {
    await page.goto(`http://127.0.0.1:${port}/tools/uishot/harness.html?${job.q}`, { waitUntil: 'networkidle0', timeout: 45000 });
    await page.waitForSelector('.studio-root, #root > *', { timeout: 20000 });
    // The deep link re-sends its navigate for about two seconds (Studio resets the route when the session
    // lands), so the default settle has to outlast that or a shot catches the page mid-reset.
    await wait(job.settle ?? 2600);

    // The tab row, clicked the way a person clicks it: the area button, then the segment by its stable id.
    if (job.clickArea) {
      // By TITLE, not by text. Every area button's title is "<label> (Ctrl+Shift+N)", and the label alone
      // stopped matching the moment an area could be locked: the Workflows button's text is "WorkflowsPro",
      // because the pill is a child element inside it.
      const ok = await page.evaluate((label) => {
        const b = document.querySelector(`nav[aria-label="Studio areas"] button[title^="${label} ("]`);
        if (b) { b.click(); return true; }
        return false;
      }, job.clickArea);
      if (!ok) note += `area button "${job.clickArea}" not found; `;
      await wait(600);
    }
    if (job.clickTab) {
      const ok = await page.evaluate((tool) => {
        const b = document.querySelector(`[data-tab="${tool}"]`);
        if (b && b.getClientRects().length) { b.click(); return true; }
        return false;
      }, job.clickTab);
      if (!ok) note += `tab segment "${job.clickTab}" not found; `;
      await wait(900);
    }
    // All tools (Help > All tools; the header's Find a tool button left on 8 Oct 2026): open Help, choose All tools,
    // then click the STABLE tool id inside the list.
    if (job.findTool) {
      const opened = await page.evaluate(() => {
        const help = [...document.querySelectorAll('button')].find((x) => x.getAttribute('aria-label') === 'Help');
        if (help && help.getAttribute('aria-expanded') === 'false') { help.click(); return true; }
        return false;
      });
      if (!opened) note += 'the Help button was not there to open; ';
      await wait(400);   // the menu is React state: its rows do not exist in the tick that opened it
      await page.evaluate(() => { const item = [...document.querySelectorAll('[role="menu"][aria-label="Help"] button')].find((x) => (x.textContent || '').trim().startsWith('All tools')); if (item) item.click(); });
      await wait(500);
      const ok = await page.evaluate((tool) => {
        const row = document.querySelector(`[role="menu"][aria-label="All tools"] [data-tool="${tool}"]`);
        if (row) { row.click(); return true; }
        return false;
      }, job.findTool);
      if (!ok) note += `All tools row "${job.findTool}" not found; `;
      await wait(900);
    }
    // Keyboard cycling: the host relays cycle:next, which is exactly what the keybinding sends.
    if (job.cycleTo) {
      const landed = await page.evaluate(async (want) => {
        for (let i = 0; i < 24; i++) {
          window.dispatchEvent(new MessageEvent('message', { data: { type: 'navigate', tab: 'cycle:next' } }));
          await new Promise((r) => setTimeout(r, 60));
          if (document.querySelector(`[data-pro-preview="${want}"]`)) return true;
        }
        return false;
      }, job.cycleTo);
      if (!landed) note += `cycling never reached "${job.cycleTo}"; `;
      await wait(700);
    }
    // Host navigation, the same message extension.ts sends for a goTab.
    if (job.hostNav) {
      await page.evaluate((tab) => window.dispatchEvent(new MessageEvent('message', { data: { type: 'navigate', tab } })), job.hostNav);
      await wait(900);
    }
    for (const spec of job.press || []) {
      const ok = await page.evaluate(clickText, spec);
      if (!ok && !(job.mayBeAbsent || []).includes(spec[1])) note += `action "${spec.join(' ')}" not found; `;
      if (!ok) absent.push(spec[1]);
      await wait(800);
    }
    // A licence lands, or lapses: the engine restarts and the host announces a reconnect, so pro.tsx
    // re-fetches. The new answer is HELD first, so the window between the reconnect and the answer is a real
    // window rather than an instant. That window is where round one was wrong: the old grant stayed
    // published, so a Pro page kept mounting and kept calling after the licence had already gone.
    if (job.reconnectTo) {
      const wanted = job.reconnectTo === 'pro' ? ALL_FEATURES : [];
      // Everything up to this line was made under the OLD plan and was allowed. Only what comes after the
      // reconnect is judged against the new one.
      callsBeforeReconnect = (await page.evaluate(() => (window.__uishotHarness?.messages || []).length));
      const before = await page.evaluate(() => !!document.querySelector('[data-testid="pro-preview"]'));
      const shouldStartLocked = job.reconnectTo === 'pro';
      if (before !== shouldStartLocked) {
        note += `the page was ${before ? 'locked' : 'unlocked'} before the reconnect, so the flip proves nothing; `;
      }
      await page.evaluate((features) => {
        window.__uishotHarness.setResponse('getEntitlement', { tier: features.length ? 'pro' : 'free', features });
        window.__uishotHarness.hold('getEntitlement');
        window.__uishotHarness.dispatch({ type: 'reconnected' });
      }, wanted);
      await wait(900);
      // MID-FLIGHT. Nothing paid may be mounted or called on the strength of an answer that has not landed.
      midFlight = await page.evaluate((from) => ({
        preview: document.querySelector('[data-testid="pro-preview"]')?.getAttribute('data-pro-preview') ?? null,
        checking: !!document.querySelector('[data-testid="feature-checking"]'),
        checkingSentence: (document.body.textContent || '').includes('Checking your Pro access'),
        calls: (window.__uishotHarness?.messages || []).slice(from).map((m) => m.method).filter(Boolean),
      }), callsBeforeReconnect);
      await page.evaluate(() => window.__uishotHarness.release('getEntitlement'));
      await wait(job.after ?? 1400);
    }
    if (job.release) {
      const answered = await page.evaluate((m) => window.__uishotHarness.release(m), job.release);
      if (!answered) note += `nothing was held for "${job.release}", so releasing it proves nothing; `;
      await wait(job.after ?? 1400);
    }
    if (job.scrollTo) {
      await page.evaluate((sel) => { const el = document.querySelector(sel); if (el) el.scrollIntoView({ block: 'center' }); }, job.scrollTo);
      await wait(400);
    }

    // What the page actually SAYS and actually CALLED. No shot is reported as proving anything this block
    // does not show.
    const seen = await page.evaluate(() => {
      const txt = (sel) => { const e = document.querySelector(sel); return e ? (e.textContent || '').replace(/\s+/g, ' ').trim() : null; };
      const preview = document.querySelector('[data-testid="pro-preview"]');
      return {
        previewFor: preview ? preview.getAttribute('data-pro-preview') : null,
        previewHeading: txt('[data-testid="pro-preview"] h1'),
        previewSentence: txt('[data-testid="pro-preview"] p'),
        exampleLabel: !!(document.body.textContent || '').includes('Example. This is not your model.'),
        unlock: txt('[data-testid="pro-preview-unlock"]'),
        softControls: document.querySelectorAll('[data-testid="pro-preview"] [aria-disabled="true"]').length,
        liveControls: document.querySelectorAll('[data-testid="pro-preview"] button:not([data-testid="pro-preview-unlock"])').length,
        checking: !!document.querySelector('[data-testid="feature-checking"]'),
        // Every quiet Pro pill on the page, by its test id. A pill is what ONE SECTION of an otherwise
        // free page shows where a paid control stood, and it is a different thing from the whole-page
        // preview: the page around it has to keep working. Matched by suffix, so a new pill is measured
        // without anybody having to remember to list it here.
        proPills: [...document.querySelectorAll('[data-testid$="-pro-pill"]')].map((e) => e.getAttribute('data-testid')),
        // The pending sentence, wherever it appears: the whole-page state, the Overview Checks card or the
        // Advanced body. One sentence, so one search.
        checkingSentence: (document.body.textContent || '').includes('Checking your Pro access'),
        // The seed a cold hand-off carried. Tests turns a measure target into an open New check drawer with
        // that measure already chosen, so the drawer's own select is the evidence the seed survived.
        seedKept: [...document.querySelectorAll('aside select')].some((el) => (el.value || '').includes('Total Sales')),
        createSegment: (() => {
          const seg = [...document.querySelectorAll('.area-tabs-create > button')][0];
          if (!seg) return null;
          const pill = [...seg.querySelectorAll('span')].some((x) => (x.textContent || '').trim() === 'Pro');
          return { label: (seg.textContent || '').replace(/\s+/g, ' ').trim(), pill };
        })(),
        // The tab row: which tools are still listed, and which wear the pill.
        tabsListed: [...document.querySelectorAll('[data-tab]')].map((b) => b.getAttribute('data-tab')),
        // The pill is an ELEMENT whose own text is exactly "Pro". Matching the button's text instead would
        // count Proposed and Promote as locked, which is how a pill check quietly becomes meaningless.
        tabsWithPill: [...document.querySelectorAll('[data-tab]')]
          .filter((b) => [...b.querySelectorAll('span')].some((x) => (x.textContent || '').trim() === 'Pro'))
          .map((b) => b.getAttribute('data-tab')),
        // The Overview's free homes, where they live since slice 11: suggestions on AI instructions, the level on the Plan
        suggestions: document.querySelectorAll('[data-testid="overview-suggestion"]').length,
        suggestionAccept: txt('[data-testid="overview-suggestion-accept"]'),
        raiseLevel: txt('[data-testid="plan-raise-level"]'),
        // Published
        publishModes: [...document.querySelectorAll('button')].map((b) => (b.textContent || '').trim())
          .filter((t) => ['Changes', 'Earlier versions', 'Promote', 'Advanced', 'AdvancedPro'].includes(t)),
        // Connections
        sqlUsage: [...document.querySelectorAll('[data-testid="hub-sql-row"]')].map((r) => (r.textContent || '').replace(/\s+/g, ' ').trim()),
        // A refusal reaching the screen is a FAILURE of the page half: it means something called a gated op.
        refusalOnScreen: (document.body.textContent || '').includes('Semanticus Pro feature'),
        // The harness records every host message the webview posts, and an rpc is one of them. This is the
        // whole call trace for the scene: a locked tool's trace must contain no operation of its feature.
        calls: (window.__uishotHarness?.messages || []).map((m) => m.method).filter(Boolean),
      };
    });
    calls.push(...(seen.calls || []));
    delete seen.calls;
    const out = join(OUT, `${LABEL}-${job.slug}-1366x768.png`);
    await page.screenshot({ path: out, fullPage: false });
    // One Visual Language: the page this scene landed on, measured at 1440x900 against its baseline. It runs
    // after the trace above is read, so a relayout cannot add a call to the evidence.
    const screen = await checkScreen(page, `pro-drive:${job.slug}`, {
      // The standalone Connections hub has no <main>: measured from body, the ratified modal backdrop reads as a
      // card and the hub nests inside it. Its root is the hub itself, found the way the scoreboard finds it.
      prepare: job.q.startsWith('view=connections') ? connectionsRoot : null,
    });

    // ---- JUDGE. Round one recorded these and moved on, which meant a caught FATAL and a paid call on the
    // free plan both left the process green. A driver that cannot fail is a screenshot tool.
    const failures = [...screen.failures];
    const grants = job.grants ?? (job.q.includes('tier=pro') ? ALL_FEATURES : []);
    const forbidden = paidCallsFor(grants);
    const judged = job.reconnectTo ? calls.slice(callsBeforeReconnect) : calls;
    const madeAnyway = [...new Set(judged)].filter((c) => forbidden.has(c)).sort();
    if (madeAnyway.length) failures.push(`called ${madeAnyway.join(', ')} on a plan that does not grant it`);
    if (errors.length) failures.push(`page errors: ${errors.slice(0, 3).join(' | ')}`);
    if (note) failures.push(note.trim());
    // A reconnect must not leave a window in which the OLD grant is still acted on.
    if (midFlight) {
      const midMade = [...new Set(midFlight.calls)].filter((c) => forbidden.has(c)).sort();
      if (midMade.length) failures.push(`mid-reconnect, before the new answer landed, called ${midMade.join(', ')}`);
      if (job.reconnectTo === 'free' && midFlight.preview === null && !midFlight.checking && !midFlight.checkingSentence) {
        failures.push('mid-reconnect the page was neither locked nor checking, so it was still acting on the old grant');
      }
    }
    const want = job.expect ?? {};
    if ('preview' in want && seen.previewFor !== want.preview) {
      failures.push(`expected preview ${JSON.stringify(want.preview)}, got ${JSON.stringify(seen.previewFor)}`);
    }
    // Three-valued on purpose: `checking: false` is a real expectation now, because a freed page that
    // still sits in "Checking your Pro access" is failing in a way no preview check would see.
    if ('checking' in want && seen.checking !== want.checking) {
      failures.push(`expected checking=${want.checking}, and the page was ${seen.checking}`);
    }
    if (want.checkingText && !seen.checkingSentence) {
      failures.push('expected "Checking your Pro access" somewhere on the page, and it was not there');
    }
    for (const pill of want.pills || []) {
      if (!(seen.proPills || []).includes(pill)) {
        failures.push(`expected the quiet Pro pill "${pill}" on this page, and it was not there`);
      }
    }
    if (want.seedKept && !seen.seedKept) failures.push('the route seed did not survive into the page it was handed to');
    // The Overview's two free homes, measured where they live since slice 11 (lane R1).
    if ('raiseLevel' in want && seen.raiseLevel !== want.raiseLevel) {
      failures.push(`expected Raise compatibility level to read ${JSON.stringify(want.raiseLevel)}, got ${JSON.stringify(seen.raiseLevel)}`);
    }
    if (want.suggestions && !(seen.suggestions > 0 && seen.suggestionAccept === 'Accept')) {
      failures.push(`expected suggested notes with Accept, got ${seen.suggestions} and ${JSON.stringify(seen.suggestionAccept)}`);
    }
    for (const label of want.absent || []) {
      if (!absent.includes(label)) failures.push(`"${label}" was reachable and must not have been`);
    }
    // The slug heuristic is gone. It grew four exceptions, and by the end a free-* scene that rendered no
    // preview could be either the bug it was written to catch or one of the exceptions. Every scene now
    // says what it expects in `expect`, above, and these three rules apply to ALL of them.
    //
    // (a) A REFUSAL ON SCREEN is always a failure, on either plan. On a locked page it means the preview
    //     called something. On a freed page it means the tab is free on the page and still refused
    //     underneath, which is the exact half-freed state this pass exists to rule out.
    if (seen.refusalOnScreen) {
      failures.push('the engine refusal sentence reached the screen, so something paid was called');
    }
    // (b) The PILL may only ever sit on a tool that is genuinely gated. A tab that was freed and kept its
    //     pill still tells a person to pay for a page they can already open, and nothing else checks it.
    const lockedIds = LOCKED_TOOLS.map(([id]) => id);
    const wrongPills = (seen.tabsWithPill || []).filter((id) => !lockedIds.includes(id));
    if (wrongPills.length) {
      failures.push(`the Pro pill is on ${wrongPills.join(', ')}, which nothing gates any more`);
    }
    // (c) A scene that names a freed tool must have MADE that tool's own read. See FREED_TOOLS: "no
    //     preview" alone is satisfied by a tab that quietly stopped existing, and the call is the only
    //     question whose answer does not depend on how the strip happens to be drawn.
    if (job.mustCall && !calls.includes(job.mustCall)) {
      failures.push(`this page must have called ${job.mustCall} and it did not, so nothing here proves it opened`);
    }
    results.push({ job: job.slug, desc: job.desc, seen, midFlight, calls, errors, note, failures, out });
  } catch (e) {
    results.push({ job: job.slug, desc: job.desc, errors, calls, note, failures: ['FATAL: ' + e.message] });
  } finally {
    await page.close();
  }
}
await browser.close();
server.close();
writeFileSync(join(OUT, `results-cp-pro-${LABEL}.json`), JSON.stringify(results, null, 2));
console.log(JSON.stringify(results, null, 2));

// The whole point of the block above. A scene that failed fails the PROCESS, so this driver is a gate and
// not a screenshot tool, and a run of it that exits 0 is worth quoting in a report.
const broken = results.filter((r) => r.failures?.length);
if (broken.length) {
  console.error(`\ncp-pro-drive FAILED on ${broken.length} of ${results.length} scene(s):`);
  for (const r of broken) console.error(`  ${r.job}: ${r.failures.join(' | ')}`);
  process.exitCode = 1;
} else {
  console.error(`\ncp-pro-drive: ${results.length} scenes, all clean.`);
}
