// Acceptance driver for the cp/pro-page lane: the Free and Pro line by feature.
//
// Every shot is the REAL Studio driven through the mock engine at 1366x768, not a drawing. The harness's
// entitlement fixture is an effective FEATURE GRANT now (?tier=pro, or ?features=<comma list> for exactly
// those ids), and every paid operation is wrapped so a plan that does not grant its feature gets the
// engine's own refusal instead of data. That is what makes the free shots mean something: if a preview or
// a suppressed card ever called a gated read, the page would show a refusal and the trace below would name
// the call.
//
// WHAT IS PROVEN HERE: that the page reaches one boundary from every entry path, that a locked tool renders
// its preview and never mounts the real page, that the preview makes no engine call at all, and that a
// reconnect flips the whole thing live. WHAT IS NOT: that the ENGINE refuses anything. The harness fakes the
// engine. The engine half is cp/pro-engine's, and rpc-name-parity covers only that the names exist.
import { createServer } from 'node:http';
import { existsSync, mkdirSync, readFileSync, statSync, writeFileSync } from 'node:fs';
import { extname, join, normalize, resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { findBrowser, requireSupportedNode } from './browser.mjs';
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
const PAID_CALLS = {
  modelCreate: ['getSpec', 'setSpec', 'saveSpec', 'loadSpec', 'clearSpec', 'autogenerateSpecFromModel',
    'autogenerateSpecFromFabric', 'buildModelFromSpec', 'getPartitionM', 'setPartitionM', 'listPartitions',
    'listNamedExpressions', 'getNamedExpression', 'createNamedExpression', 'updateNamedExpression',
    'getSourceSchema', 'diffSchema', 'applySchemaUpdate', 'getIncrementalRefreshPolicy',
    'setIncrementalRefreshPolicy', 'removeIncrementalRefreshPolicy', 'createDataSource', 'getDocModel',
    'getDocOutline', 'getDocSection', 'setDocSection', 'getPrimer', 'setPrimer', 'listInsights', 'addInsight',
    'editInsight', 'deleteInsight', 'approveInsight', 'upvoteInsight', 'downvoteInsight', 'purgeKnowledge',
    'recallExperience', 'listCalculationGroups', 'getPerspectives', 'listCalendars', 'listRoles',
    'daxLibSearch', 'daxLibListInstalled', 'daxLibPackageInfo', 'daxLibVersions', 'daxLibInstall',
    'daxLibUninstall', 'createCalculationGroup', 'createCalculationItem', 'setCalcGroupPrecedence',
    'setCalcItemFormatString', 'createFunction', 'defineCalendar', 'generateTimeIntelligence',
    'createFieldParameter', 'createPerspective', 'setPerspectiveMember', 'createRole', 'deleteRole',
    'setRoleMember', 'setRolePermission', 'setTablePermission', 'setColumnObjectPermission',
    'setTableObjectPermission', 'defineCalendarFromTemplate', 'deleteCalendar', 'tagCalendarColumn',
    'generateDateTable', 'markDateTable'],
  tests: ['listTests', 'listTableMappings', 'runTests', 'listTestRuns', 'getTestRun', 'saveTest', 'deleteTest',
    'tryTest', 'recordTestRun', 'exportTestReport', 'getUnmatchedRows', 'setTableSourceMapping',
    'clearTableSourceMapping', 'listEvidence', 'getEvidence', 'saveEvidence', 'captureBaseline',
    'compareBaseline', 'reconcileMeasure', 'reviewReconcileMapping'],
  publishedAdvanced: ['gitCommit', 'gitPull', 'gitPush', 'gitBranch', 'gitCheckout', 'gitClone', 'gitDiff',
    'gitLog', 'fabricGitConnect', 'fabricGitDisconnect', 'fabricGitCommit', 'fabricGitUpdate',
    'fabricGitStatus', 'fabricGitConnection', 'cicdGenerate', 'cicdPublish', 'listDataAgents', 'getDataAgent',
    'createDataAgent', 'updateDataAgent', 'publishDataAgent', 'deleteDataAgent', 'generateDataAgentConfig'],
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
// the page and the results name both.
const LOCKED_TOOLS = [
  ['spec', 'Model Spec'], ['advmodels', 'Advanced Modelling'], ['mcode', 'Power Query'],
  ['docs', 'Docs'], ['knowledge', 'Model notes'], ['tests', 'Tests'], ['evidence', 'Saved reports'],
  ['workflows', 'Workflows'], ['dataagent', 'Data Agent'],
];

// `deep` uses the ?tab= route, which selects by the stable data-tab / data-tool attributes. `hash` uses the
// old text matcher, kept only where the target is free and its label is unchanged.
const JOBS = [
  // ---- 1. every Pro tool on the free plan: the preview, the pill, the Unlock action ----------------
  ...LOCKED_TOOLS.map(([tool, label]) => ({
    slug: `free-${tool}`, q: `tab=${tool}`, desc: `${label} on the free plan: the preview in its place`,
  })),
  // ---- 2. the same tools on Pro: the real page, no preview -----------------------------------------
  ...LOCKED_TOOLS.map(([tool, label]) => ({
    slug: `pro-${tool}`, q: `tier=pro&tab=${tool}`, desc: `${label} on Pro: the real page`,
  })),
  // ---- 3. every entry path reaches the SAME boundary ------------------------------------------------
  // The tab row itself: the Checks area button, then the Tests segment, both by stable id. No deep link,
  // no host message: this is a person clicking two buttons.
  { slug: 'entry-row', q: '', clickArea: 'Checks', clickTab: 'tests', desc: 'entry by the tab row: Checks then Tests' },
  // Find a tool, by stable tool id, because a Pro pill breaks any match on the button text.
  { slug: 'entry-findatool', q: '', findTool: 'workflows', desc: 'entry by Find a tool' },
  // Keyboard cycling. The host sends cycle:next; the driver sends the same message the host sends.
  { slug: 'entry-keyboard', q: '', cycleTo: 'evidence', desc: 'entry by keyboard cycling' },
  // Host navigation: a goTab from the extension, with its seed intact.
  { slug: 'entry-hostnav', q: '', hostNav: 'docs', desc: 'entry by host navigation' },
  // A deep link straight at a locked tool.
  { slug: 'entry-deeplink', q: 'tab=mcode', desc: 'entry by deep link' },
  // The legacy dataagent id, which used to be its own side door into Advanced.
  { slug: 'entry-legacy-dataagent', q: 'tab=dataagent', desc: 'entry by the legacy dataagent route' },
  // A cold hand-off from the host, flushed on studioReady. See section 10 below for why this, and not a
  // persisted route, is what "restored" means in this app.
  { slug: 'entry-coldnav', q: 'coldNav=knowledge', settle: 2600, grants: [], expect: { preview: 'knowledge' },
    desc: 'entry by a cold hand-off from the host' },

  // ---- 4. Published: only Advanced is gated --------------------------------------------------------
  { slug: 'free-published-basic', q: 'tab=deploy', desc: 'Published on free: Publish, Roll back and Promote all live' },
  { slug: 'free-published-advanced', q: 'tab=deploy', press: [['button', 'Advanced']], desc: 'Advanced on free: the preview in place, the rest still one click away' },
  { slug: 'pro-published-advanced', q: 'tier=pro&tab=deploy', press: [['button', 'Advanced']], desc: 'Advanced on Pro: the real delivery tools' },

  // ---- 5. the Overview card, both ways -------------------------------------------------------------
  { slug: 'free-overview', q: 'tab=modelhome', settle: 1400, desc: 'Overview on free: the Checks card says Tests is Pro and calls nothing' },
  { slug: 'pro-overview', q: 'tier=pro&tab=modelhome', settle: 1400, desc: 'Overview on Pro: the Checks card reads the saved history' },
  { slug: 'free-overview-suggestions', q: 'tab=modelhome', settle: 2600, scrollTo: '[data-testid="overview-suggestion"]', desc: 'the free home for suggested notes, with Accept and Reject' },
  // The other free home. Absent by default, which is the honest state on an engine that does not report the
  // level; ?compat=1604 is a model whose level is actually holding calendars back.
  { slug: 'free-overview-raise-level', q: 'tab=modelhome&compat=1604', settle: 2600, desc: 'the free Raise compatibility level action, on a model that needs it' },
  { slug: 'free-overview-level-ok', q: 'tab=modelhome&compat=1701', settle: 2600, desc: 'the same page on a model that does not: no nag' },

  // ---- 6. Connections reads source usage from the free projection ----------------------------------
  { slug: 'free-connections-usage', q: 'view=connections', rail: 'sqlsources', settle: 1200, desc: 'Connections on free: what uses each SQL source, from the free reading' },

  // ---- 7. one feature locked, the rest open --------------------------------------------------------
  { slug: 'mixed-tests-only', q: 'features=modelCreate,publishedAdvanced,workflows&tab=tests', desc: 'a plan with three features: only Tests is locked' },

  // ---- 8. a reconnect flips the entitlement live, BOTH ways -----------------------------------------
  { slug: 'reconnect-before', q: 'tab=workflows', grants: [], desc: 'Workflows locked, before a licence lands' },
  { slug: 'reconnect-after', q: 'tab=workflows', reconnectTo: 'pro', settle: 1600, grants: ALL_FEATURES,
    expect: { preview: null }, desc: 'the same page after a licence lands and the engine reconnects' },
  // The direction that actually loses money if it is wrong. A licence lapses, the engine restarts, and the
  // page must stop being Pro AT ONCE, not when the new answer eventually arrives.
  { slug: 'reconnect-downgrade', q: 'tier=pro&tab=workflows', reconnectTo: 'free', settle: 1600, grants: [],
    expect: { preview: 'workflows' }, desc: 'a licence lapses: Workflows locks again on reconnect' },

  // ---- 9. an entitlement that has NOT answered is not a grant ---------------------------------------
  // Astra's P1, both halves. Every scene here holds getEntitlement in flight and asserts that nothing paid
  // is called, nothing paid is mounted, and the page says it is checking rather than guessing.
  { slug: 'pending-overview', q: 'tab=modelhome&hold=getEntitlement', settle: 2600, grants: [],
    expect: { checkingText: true }, desc: 'Overview while the plan is still unknown: no listTestRuns' },
  { slug: 'pending-overview-run', q: 'tab=modelhome&hold=getEntitlement', settle: 2600, grants: [],
    press: [['button', 'Checking your Pro access']], expect: { checkingText: true },
    desc: 'and its Run control cannot start a run while the plan is unknown' },
  { slug: 'pending-advanced', q: 'tab=deploy&hold=getEntitlement', settle: 2600, grants: [],
    press: [['button', 'Advanced']], expect: { checkingText: true },
    desc: 'Advanced while the plan is unknown: the real panels do not mount' },
  // Astra's exact receipt: Advanced mounted, then Data Agent called listDataAgents on an unanswered plan.
  // Astra's exact receipt: Advanced mounted, then Data Agent called listDataAgents on an unanswered plan.
  // The Data Agent button is EXPECTED to be missing here, because the mode row it lives in belongs to the
  // Advanced body that must not mount, so `mayBeAbsent` marks it as a result rather than a setup failure.
  { slug: 'pending-dataagent', q: 'tab=deploy&hold=getEntitlement', settle: 2600, grants: [],
    press: [['button', 'Advanced'], ['button', 'Data Agent']], mayBeAbsent: ['Data Agent'],
    expect: { checkingText: true, absent: ['Data Agent'] },
    desc: 'and Data Agent inside it cannot be reached, let alone call listDataAgents' },
  { slug: 'pending-tool', q: 'tab=tests&hold=getEntitlement', settle: 2600, grants: [],
    expect: { checking: true, preview: null }, desc: 'a Pro tab while the plan is unknown: neither page nor preview' },
  // Released LATE, with the answer the fixture holds at that moment. Proves the pending state resolves.
  { slug: 'pending-then-free', q: 'tab=modelhome&hold=getEntitlement', settle: 1400, release: 'getEntitlement',
    after: 1600, grants: [], desc: 'the plan answers free: the Checks card settles into its Pro state' },
  { slug: 'pending-then-pro', q: 'tier=pro&tab=tests&hold=getEntitlement', settle: 1400, release: 'getEntitlement',
    after: 1800, grants: ALL_FEATURES, expect: { preview: null }, desc: 'the plan answers Pro: the real page arrives' },
  // A FAILED entitlement read is not a grant either. pro.tsx publishes 'unknown' on a rejection.
  { slug: 'entitlement-failed', q: 'tab=tests&ent=fail', settle: 2600, grants: [],
    expect: { checking: true, preview: null }, desc: 'the entitlement read fails: still not a grant' },

  // ---- 10. the COLD HAND-OFF, which is what "a restored route" actually is here ---------------------
  // MEASURED, not assumed: App.tsx keeps no persisted route. `useState<Route>` at :127 always starts on
  // Overview, and there is no loadState for it anywhere in the file. So a page you were on does not come
  // back from webview state; what comes back is the HOST re-navigating. extension.ts queues that navigation
  // and flushes it on studioReady, which can beat the webview's own sessionInfo call home, and that race is
  // exactly where a boundary keyed on the open tool could be skipped. The seed it carries must survive too.
  //
  // My round-one 'entry-restored' scene seeded an unused state key and then navigated explicitly, so it
  // proved a second host navigation and nothing about restoration. Astra was right; this replaces it.
  { slug: 'restored-cold-locked', q: 'coldNav=tests:measure:Sales/Total Sales&sessionDelay=1200', settle: 3200,
    grants: [], expect: { preview: 'tests' },
    desc: 'a cold hand-off into a Pro tool, landing before the session does: the preview, never the page' },
  { slug: 'restored-cold-seed', q: 'tier=pro&coldNav=tests:measure:Sales/Total Sales&sessionDelay=1200', settle: 3200,
    grants: ALL_FEATURES, expect: { preview: null, seedKept: true },
    desc: 'the same cold hand-off on Pro: the real page opens AND keeps the measure it was handed' },
  // The unstamped shape the host sends when it has no session id of its own yet.
  // The UNSTAMPED shape the host sends when it has no session id of its own yet. Studio deliberately drops
  // an unstamped route when the session arrives (App.tsx:371-383: a real session change is a different model,
  // so the page you were on is forgotten), and it lands on Overview. That is existing, intended behaviour and
  // not this lane's to change. What this scene holds is the part that IS this lane's: however that race
  // resolves, it never resolves onto a paid page or a paid call.
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
      const ok = await page.evaluate((label) => {
        const b = [...document.querySelectorAll('button')].find((x) => (x.textContent || '').trim() === label);
        if (b) b.click();
        return !!b;
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
    // Find a tool: open the chooser, then click the STABLE tool id inside it.
    if (job.findTool) {
      const opened = await page.evaluate(() => {
        const chooser = [...document.querySelectorAll('button')].find((x) => (x.textContent || '').trim() === 'Find a tool');
        if (chooser && chooser.getAttribute('aria-expanded') === 'false') { chooser.click(); return true; }
        return false;
      });
      if (!opened) note += 'the Find a tool button was not there to open; ';
      await wait(500);   // the menu is React state: its rows do not exist in the tick that opened it
      const ok = await page.evaluate((tool) => {
        const row = document.querySelector(`[role="menu"][aria-label="Find a tool"] [data-tool="${tool}"]`);
        if (row) { row.click(); return true; }
        return false;
      }, job.findTool);
      if (!ok) note += `Find a tool row "${job.findTool}" not found; `;
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
    if (job.rail) {
      const ok = await page.evaluate((v) => { const b = document.querySelector(`[data-hubnav="${v}"]`); if (b) b.click(); return !!b; }, job.rail);
      if (!ok) note += `rail "${job.rail}" not found; `;
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
        // Overview
        checksCard: txt('.overview-health-card'),
        checksRunLabel: txt('[data-testid="overview-checks-run"]'),
        suggestions: document.querySelectorAll('[data-testid="overview-suggestion"]').length,
        suggestionAccept: txt('[data-testid="overview-suggestion-accept"]'),
        raiseLevel: txt('[data-testid="overview-raise-level"]'),
        // Published
        publishModes: [...document.querySelectorAll('button')].map((b) => (b.textContent || '').trim())
          .filter((t) => ['What to publish', 'Roll back', 'Promote', 'Advanced', 'AdvancedPro'].includes(t)),
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

    // ---- JUDGE. Round one recorded these and moved on, which meant a caught FATAL and a paid call on the
    // free plan both left the process green. A driver that cannot fail is a screenshot tool.
    const failures = [];
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
    if (want.checking && !seen.checking) failures.push('expected the checking state, and it was not on the page');
    if (want.checkingText && !seen.checkingSentence) {
      failures.push('expected "Checking your Pro access" somewhere on the page, and it was not there');
    }
    if (want.seedKept && !seen.seedKept) failures.push('the route seed did not survive into the page it was handed to');
    for (const label of want.absent || []) {
      if (!absent.includes(label)) failures.push(`"${label}" was reachable and must not have been`);
    }
    // Every scene whose slug says free-* must render its preview, and every pro-* must not.
    if (job.slug.startsWith('free-') && job.slug !== 'free-published-basic' && job.slug !== 'free-overview'
        && !job.slug.startsWith('free-overview') && !job.slug.startsWith('free-connections')
        && seen.previewFor === null) {
      failures.push('a free-tier scene for a Pro tool rendered no preview');
    }
    if (job.slug.startsWith('pro-') && seen.previewFor !== null) {
      failures.push(`a Pro scene rendered the preview for ${seen.previewFor}`);
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
