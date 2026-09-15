// Headless screenshots of the Semanticus webview UI, engine mocked. Renders the built bundles in an ISOLATED
// Chromium (puppeteer's cached chrome-headless-shell — NOT the user's Edge, which hands off to its running
// instance and can't be driven), served over a short-lived local HTTP server (headless Chromium won't
// screenshot file://). Real selector waits, so views are fully painted before capture.
//
// Targets:
//   studio   — the React Studio webview (media/studio). Variant = tab label (default Diagram).
//   propgrid — the Properties grid webview (media/propgrid). Variant = scenario
//              (model | measure | multi | column | formatexpr | lowcl | staledraft | error | empty).
//
// Usage:
//   node shot.mjs                       # studio Diagram -> shots/studio-diagram.png
//   node shot.mjs <Tab>                 # a studio tab by label
//   node shot.mjs studio <Tab> [out]
//   node shot.mjs propgrid [scenario] [out]
//   node shot.mjs all                   # every studio tab + every propgrid scenario (review-everything)
//   UISHOT_GRAPH=mini node shot.mjs Diagram   # focused diagram graph (markers render large)
//   UISHOT_COMPARE=1 node shot.mjs Deploy     # the Compare Target picker opened on the remembered-model registry list
// Chromium via SEMANTICUS_BROWSER, else the NEWEST cached chrome-headless-shell, else Edge/Chrome.
// Needs Node 22.12 or newer (puppeteer-core 25's own floor); browser.mjs says why and checks it.
import { findBrowser, requireSupportedNode } from './browser.mjs';
import { fileURLToPath } from 'node:url';
import { dirname, join, resolve, isAbsolute, extname, normalize } from 'node:path';
import { existsSync, mkdirSync, statSync, readFileSync } from 'node:fs';
import { createServer } from 'node:http';

// THE FLOOR CHECK RUNS BEFORE PUPPETEER IS LOADED, AND THE ORDER IS THE WHOLE POINT. This used to be a
// static `import puppeteer from 'puppeteer-core'`, which the runtime resolves, parses and evaluates before
// one line of this file runs. `requireSupportedNode()` therefore could not fire on the Node versions it
// exists for: on Node 20 the run died inside puppeteer with the opaque error the guard was written to
// replace, and the guard's message was never reached. A dynamic import after the check is what makes the
// check reachable. `browser.mjs` imports no puppeteer, so nothing can reorder this again by accident.
requireSupportedNode();
const puppeteer = (await import('puppeteer-core')).default;

const __dir = dirname(fileURLToPath(import.meta.url));
const webRoot = resolve(__dir, '..', '..');          // Semanticus.VSCode/ (so /media/... and /tools/... resolve)

// Keep this in the same reading order as App.tsx: intent groups first, then the three standalone
// surfaces. `all` is the product-wide visual gate, so an omitted/renamed tab would be a silent coverage hole.
const STUDIO_TABS = [
  'Overview', 'Diagram', 'Lineage', 'Find and replace', 'Data', 'Size by table',
  'Model Spec', 'Advanced Modelling', 'Power Query', 'Docs', 'Model notes',
  'DAX Lab', 'Tests', 'Model quality', 'AI understanding', 'Results',
  'Proposed', 'History', 'Published', 'Workflows', 'Permissions',
];
const PROPGRID_SCENARIOS = ['model', 'measure', 'multi', 'column', 'formatexpr', 'lowcl', 'staledraft', 'error', 'empty'];
// The Connections HUB inventory: the four sections, Work locally, the signed-out (stale) identity, the shared
// account-switch consequences dialog, and the SAME hub hosted full-page as the standalone panel (?view=connections).
const CONNECTION_HUB_STATES = [
  { target: 'studio', variant: 'Diagram', hub: 'open', out: join(__dir, 'shots', 'studio-connections.png') },
  { target: 'studio', variant: 'Diagram', hub: 'setup', out: join(__dir, 'shots', 'studio-connections-setup.png') },
  { target: 'studio', variant: 'Diagram', hub: 'accounts', out: join(__dir, 'shots', 'studio-connections-accounts.png') },
  { target: 'studio', variant: 'Diagram', hub: 'history', out: join(__dir, 'shots', 'studio-connections-history.png') },
  { target: 'studio', variant: 'Diagram', hub: 'work', out: join(__dir, 'shots', 'studio-connections-work-locally.png') },
  { target: 'studio', variant: 'Diagram', hub: 'stale', out: join(__dir, 'shots', 'studio-connections-stale-identity.png') },
  { target: 'studio', variant: 'Diagram', hub: 'switch', out: join(__dir, 'shots', 'studio-connections-switch.png') },
  { target: 'studio', variant: 'Diagram', hub: 'create', out: join(__dir, 'shots', 'studio-connections-create.png') },
  { target: 'studio', variant: 'Diagram', hub: 'open-unsaved', out: join(__dir, 'shots', 'studio-connections-open-unsaved.png') },
  { target: 'studio', variant: 'Diagram', hub: 'recovery', out: join(__dir, 'shots', 'studio-connections-recovery.png') },
  { target: 'studio', variant: 'Diagram', hub: 'local-failure', out: join(__dir, 'shots', 'studio-connections-local-failure.png') },
  { target: 'studio', variant: 'Diagram', hub: 'add', out: join(__dir, 'shots', 'studio-connections-add.png') },
  { target: 'connections', variant: 'standalone', out: join(__dir, 'shots', 'connections-standalone.png') },
  // A <900px narrow capture: the Open view must collapse to a single readable flow with ONE outer scrollbar (no nested).
  { target: 'connections', variant: 'standalone', vw: 780, vh: 1040, out: join(__dir, 'shots', 'connections-standalone-narrow.png') },
];

// findBrowser and the Node-floor check live in browser.mjs, shared by every entry point here. Six copies of
// the same first-directory-wins pick used to sit in this directory, so "which Chromium ran" depended on
// which script you invoked and on readdir order. See browser.mjs for the runtime contract in full.

const TYPES = { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.json': 'application/json', '.png': 'image/png', '.svg': 'image/svg+xml', '.woff2': 'font/woff2', '.map': 'application/json' };

function startServer() {
  const server = createServer((req, res) => {
    try {
      const urlPath = decodeURIComponent((req.url || '/').split('?')[0].split('#')[0]);
      const filePath = normalize(join(webRoot, urlPath));
      if (!filePath.startsWith(webRoot) || !existsSync(filePath) || statSync(filePath).isDirectory()) {
        res.writeHead(404); res.end('not found'); return;
      }
      res.writeHead(200, { 'content-type': TYPES[extname(filePath).toLowerCase()] || 'application/octet-stream' });
      res.end(readFileSync(filePath));
    } catch (e) { res.writeHead(500); res.end(String(e)); }
  });
  return new Promise((r) => server.listen(0, '127.0.0.1', () => r(server)));
}

// One capture = one page. Returns the output path.
async function capture(browser, port, { target, variant, out, hub, vw: jobVw, vh: jobVh }) {
  const page = await browser.newPage();
  const errors = [];
  const expectedNotices = [];
  page.on('pageerror', (e) => errors.push('pageerror: ' + e.message));
  page.on('console', (m) => {
    if (m.type() !== 'error') return;
    const message = m.text();
    // Docs intentionally previews generated HTML in a sandbox WITHOUT allow-scripts. Chromium logs one
    // refusal for each inert script tag in the generated document; that proves the sandbox contract and is
    // not a page failure. Match the complete browser message so any other Docs error still fails visibly.
    const expectedDocsSandboxRefusal = target === 'studio' && variant === 'Docs'
      && message === "Blocked script execution in 'about:srcdoc' because the document's frame is sandboxed and the 'allow-scripts' permission is not set.";
    (expectedDocsSandboxRefusal ? expectedNotices : errors).push('console: ' + message);
  });
  try {
    if (target === 'propgrid') {
      await page.setViewport({ width: 380, height: 1000, deviceScaleFactor: 2 });   // sidebar width
      const url = `http://127.0.0.1:${port}/tools/uishot/propgrid.html?s=${encodeURIComponent(variant)}`;
      await page.goto(url, { waitUntil: 'networkidle0', timeout: 30000 });
      await page.waitForSelector('.hdr, .empty', { timeout: 15000 });
      await new Promise((r) => setTimeout(r, 400));
      await page.screenshot({ path: out, fullPage: true });
    } else if (target === 'connections') {
      // The standalone hub: the SAME studio bundle mounted full-page via ?view=connections — the dedicated
      // "Manage connections" panel hosted when Studio is closed. No footer to click; the hub IS the whole document.
      const vh = Math.max(400, Math.min(6000, jobVh || parseInt(process.env.UISHOT_H || '', 10) || 900));
      const vw = Math.max(768, Math.min(6000, jobVw || parseInt(process.env.UISHOT_W || '', 10) || 1280));
      await page.setViewport({ width: vw, height: vh, deviceScaleFactor: 2 });
      const url = `http://127.0.0.1:${port}/tools/uishot/harness.html?view=connections&conn=1`;
      await page.goto(url, { waitUntil: 'networkidle0', timeout: 30000 });
      await page.waitForFunction(() => (document.body.textContent || '').includes('Open a model'), { timeout: 15000 });
      await new Promise((r) => setTimeout(r, 800));
      await page.screenshot({ path: out, fullPage: !!process.env.UISHOT_FULL });
    } else {
      // UISHOT_H overrides the viewport height (long inner-scrolled content, e.g. an expanded audit trail —
      // the Studio shell scrolls INSIDE the page, so fullPage can't reach below the fold).
      const vh = Math.max(400, Math.min(6000, parseInt(process.env.UISHOT_H || '', 10) || 1000));
      // UISHOT_W overrides the viewport width (wide-monitor layout checks — a tab that caps its content column
      // leaves a right-hand dead zone only a wide viewport reveals). Default 1680 keeps existing baselines stable.
      const vw = Math.max(768, Math.min(6000, parseInt(process.env.UISHOT_W || '', 10) || 1680));
      await page.setViewport({ width: vw, height: vh, deviceScaleFactor: 2 });
      const params = new URLSearchParams();
      if (process.env.UISHOT_GRAPH) params.set('g', process.env.UISHOT_GRAPH);
      if (process.env.UISHOT_SUB) params.set('sub', process.env.UISHOT_SUB);
      if (process.env.UISHOT_EXPAND) params.set('expand', process.env.UISHOT_EXPAND);
      if (process.env.UISHOT_CONN) params.set('conn', '1');   // simulate a live-connected engine (Data/Storage/Query populate)
      if (process.env.UISHOT_DL) params.set('dl', '1');       // render the model as Direct Lake (resident-only storage + Entity partitions)
      if (process.env.UISHOT_ACT) params.set('act', '1');     // simulate the agent running read ops (live activity feed + reflection)
      if (process.env.UISHOT_VE) params.set('ve', process.env.UISHOT_VE);   // Edit History Verified-Edits chain variant (e.g. UISHOT_VE=broken → tampered chain warning)
      if (process.env.UISHOT_EVIDENCE) params.set('evidence', 'open');      // Edit History: expand every audit "Details" row (the typed evidence renderers)
      if (process.env.UISHOT_WF) params.set('wf', process.env.UISHOT_WF);   // Workflows: 'run' → mid-flight run · 'avail' → turned-off + rule-deactivated · 'bind' → §9c "Required for" populated (Pro) · 'bindfree' → same binding, free-tier Pro-gated · 'activate' → §10.6 "Hide when" hidden-state (Pro) · 'activatefree' → same, free-tier Pro-gated · 'scenform' → Scenarios template slot form · 'scenfill' → the form filled + previewed · 'scenpreview' → Client-handoff settings preview (Pro-gated Apply) · 'scenapplied' → the template flow carried through Apply → the "To undo:" result card (else the pre-run overview)
      if (process.env.UISHOT_TIER) params.set('tier', process.env.UISHOT_TIER);   // entitlement: 'pro' flips Pro-gated controls into their enabled state (default free)
      if (process.env.UISHOT_PQ) params.set('pq', process.env.UISHOT_PQ);   // M query: 'menu' opens a column transform menu, 'profile' toggles the profiling strip (both force a live conn)
      if (process.env.UISHOT_TESTS) params.set('tests', process.env.UISHOT_TESTS);   // Tests: overview|report|measures|relationships|security|history → click Run suite, then open that facet
      if (process.env.UISHOT_TESTMAP) params.set('testmap', process.env.UISHOT_TESTMAP);   // Tests SQL mapping review: override|ambiguous|error
      if (process.env.UISHOT_SPEC) params.set('spec', process.env.UISHOT_SPEC);   // Model Spec: empty shows the creation wizard
      if (process.env.UISHOT_LAY) params.set('lay', '1');     // seed an engine-owned diagram layout (get_layout) so the All-tables canvas snaps to it
      if (process.env.UISHOT_CAL) params.set('cal', process.env.UISHOT_CAL);   // Calendars: 'off' → below-1701 CL-upgrade gate state
      if (process.env.UISHOT_DEPLOY) params.set('deploy', process.env.UISHOT_DEPLOY);   // Deploy: rollback, promote, advanced, dataagent, or publish drives the requested decision surface
      if (process.env.UISHOT_LIVE) params.set('live', '1');   // live-bound session so the Publish control is enabled
      if (process.env.UISHOT_PERM) params.set('perm', process.env.UISHOT_PERM);   // Permissions: 'free' → Free-tier read-only variant · 'off' → guardrail turned off (dimmed cells) · 'loading' → reads never answer · 'error' → reads fail (error + retry); default = Pro (editable)
      if (process.env.UISHOT_FR) params.set('fr', process.env.UISHOT_FR);   // Find & Replace: 'empty' -> a 0-item plan + note (the honest Replace-all empty state on Optimize)
      if (process.env.UISHOT_EXPLAIN) params.set('explain', process.env.UISHOT_EXPLAIN);   // Explain This Number: 'blank' → the why-is-this-blank dossier fixture
      if (process.env.UISHOT_SPEC) params.set('spec', process.env.UISHOT_SPEC);   // Spec inline editor: 'measureless' | 'measuregroup' | 'norel' edge-state fixtures
      if (process.env.UISHOT_STATE) params.set('state', process.env.UISHOT_STATE);   // seed persisted webview state (JSON), e.g. '{"input:lab.viz":"matrix"}'
      if (process.env.UISHOT_DAX) params.set('dax', process.env.UISHOT_DAX);   // DAX Lab: cap = announce a capped result; hang = leave Run in flight so Stop is visible
      if (process.env.UISHOT_HOME) params.set('home', process.env.UISHOT_HOME); // Model home: loading, error, empty or nomodel
      if (process.env.UISHOT_ENGINE) params.set('engine', process.env.UISHOT_ENGINE); // 'down' → the host holds NO engine, so every request answers "Engine not connected." and Studio shows its recovery panel
      if (hub && hub !== 'local-failure') params.set('conn', '1');   // failure state starts without an active local query so its retry action is available
      if (hub) params.set('hub', hub);   // the recovery state uses the same real hub with a deterministic RPC failure seam
      if (hub === 'create' || hub === 'open-unsaved') params.set('dirty', '1');   // unsaved-work consent for create and open
      const q = params.toString() ? `?${params.toString()}` : '';
      const url = `http://127.0.0.1:${port}/tools/uishot/harness.html${q}#${encodeURIComponent(variant)}`;
      if (['loop', 'calls'].includes(process.env.UISHOT_WF)) await page.evaluateOnNewDocument(() => {
        window.addEventListener('message', (event) => {
          if (event.data?.type === 'workflowDidChange') window.__uishotLastRun = event.data.payload;
        });
      });
      await page.goto(url, { waitUntil: 'networkidle0', timeout: 30000 });
      await page.waitForSelector('nav button', { timeout: 15000 });
      // UISHOT_CAL is a maintained Advanced Modelling sub-state, not only a mock-data switch. Land on the
      // Calendars panel automatically so both the configured and below-1701 screenshots exercise the real surface.
      if (process.env.UISHOT_CAL && variant === 'Advanced Modelling') {
        const clicked = await page.evaluate(() => {
          const button = [...document.querySelectorAll('button')].find((b) => (b.textContent || '').trim() === 'Calendars');
          if (button) button.click();
          return !!button;
        });
        if (!clicked) throw new Error('Advanced Modelling could not find the Calendars area');
        await new Promise((r) => setTimeout(r, 700));
      }
      if (process.env.UISHOT_RLS && variant === 'Advanced Modelling') {
        const clicked = await page.evaluate(() => {
          const button = [...document.querySelectorAll('button')].find((b) => (b.textContent || '').trim() === 'RLS / OLS');
          if (button) button.click();
          return !!button;
        });
        if (!clicked) throw new Error('Advanced Modelling could not find the RLS / OLS area');
        await new Promise((r) => setTimeout(r, 700));
      }
      if (hub) {
        // The footer "Editing" segment opens the hub overlay from Studio; then navigate to the requested section.
        await page.click('[data-testid="connections-editing"]');
        await page.waitForSelector('[role="dialog"][aria-label="Connections"]', { timeout: 15000 });
        await new Promise((r) => setTimeout(r, 500));
        const clickNav = async (section, expect) => {
          const ok = await page.evaluate((s) => { const b = document.querySelector(`[data-hubnav="${s}"]`); if (b) b.click(); return !!b; }, section);
          if (!ok) throw new Error(`Connections hub could not find the ${section} section`);
          await page.waitForFunction((t) => (document.body.textContent || '').includes(t), { timeout: 15000 }, expect);
        };
        const clickText = async (text, expect) => {
          const ok = await page.evaluate((t) => { const b = [...document.querySelectorAll('button')].find((x) => (x.textContent || '').trim() === t); if (b) b.click(); return !!b; }, text);
          if (!ok) throw new Error(`Connections hub could not find the "${text}" action`);
          if (expect) await page.waitForFunction((t) => (document.body.textContent || '').includes(t), { timeout: 15000 }, expect);
        };
        if (hub === 'setup') await clickNav('setup', 'Current setup');
        else if (hub === 'accounts') await clickNav('accounts', 'Other authentication methods');
        else if (hub === 'history') await clickNav('history', 'All activity');
        else if (hub === 'work') {
          // The "Work locally" outcome (a self-explaining OutcomeButton in the detail; its label carries microcopy, so
          // match on inclusion) opens the Work-locally view.
          const ok = await page.evaluate(() => { const b = [...document.querySelectorAll('[data-testid="hub-outcome"]')].find((x) => (x.textContent || '').includes('Work locally')); if (b) b.click(); return !!b; });
          if (!ok) throw new Error('Connections hub could not find the Work locally outcome');
          await page.waitForFunction(() => (document.body.textContent || '').includes('Work locally from'), { timeout: 15000 });
        }
        else if (hub === 'stale') {
          // Select the signed-out published row (Sandbox) so the detail pane shows the "Sign-in required" status.
          const ok = await page.evaluate(() => { const b = [...document.querySelectorAll('[data-testid="hub-model-row"]')].find((x) => (x.textContent || '').includes('Sandbox')); if (b) b.click(); return !!b; });
          if (!ok) throw new Error('Connections hub could not find the signed-out (Sandbox) row');
          await page.waitForFunction(() => (document.body.textContent || '').includes('Signed out'), { timeout: 15000 });
        } else if (hub === 'switch') {
          // Open the shared Phase 2 account-choice dialog (saved profiles + Use for this open) from the detail's
          // "Choose account for this open" control.
          await clickText('Choose account for this open', 'This choice applies to this open');
        } else if (hub === 'create') {
          // Open the guarded Create-model surface (?dirty=1 makes the unsaved-changes consent state render).
          const ok = await page.evaluate(() => { const b = [...document.querySelectorAll('button')].find((x) => (x.textContent || '').includes('Create blank model')); if (b) b.click(); return !!b; });
          if (!ok) throw new Error('Connections hub could not find the Create blank model card');
          await page.waitForFunction(() => (document.body.textContent || '').includes('replaces the open model, which has unsaved changes'), { timeout: 15000 });
        } else if (hub === 'open-unsaved') {
          const ok = await page.evaluate(() => { const b = document.querySelector('[data-testid="hub-row-open"]'); if (b) b.click(); return !!b; });
          if (!ok) throw new Error('Connections hub could not find an Open row');
          await page.waitForFunction(() => (document.body.textContent || '').includes('Opening another model throws those changes away unless you save first'), { timeout: 15000 });
        } else if (hub === 'recovery') {
          // A real remembered XMLA Query action fails once through the RPC seam. The component must retain the target
          // and query purpose, show the saved-account choice, and retry with the selected profile without changing the
          // tenant default or the dirty editing session. Capture the failure before the explicit retry.
          const row = await page.evaluate(() => {
            const b = [...document.querySelectorAll('[data-testid="hub-model-row"]')].find((x) => (x.textContent || '').includes('Finance'));
            if (b) b.click();
            return !!b;
          });
          if (!row) throw new Error('Connections recovery could not find the Finance remembered model');
          const query = await page.evaluate(() => {
            const b = [...document.querySelectorAll('[data-testid="hub-outcome"]')].find((x) => (x.textContent || '').trim().startsWith('Query this model'));
            if (b) b.click();
            return !!b;
          });
          if (!query) throw new Error('Connections recovery could not find Query this model');
          await page.waitForSelector('[data-testid="hub-account-dialog"][data-purpose="query"]', { timeout: 15000 });
          await page.waitForSelector('[data-testid="hub-account-recovery"]', { timeout: 15000 });
          await page.waitForSelector('[data-testid="hub-sign-in-again"]', { timeout: 15000 });
          await page.screenshot({ path: out.replace(/\.png$/u, '-failure.png'), fullPage: false });
          const assert = (await import('node:assert/strict')).default;
          const failed = await page.evaluate(() => structuredClone(window.__uishotRecovery));
          assert.equal(failed.connectCalls.length, 1, 'the first query attempt must fail once');
          assert.equal(failed.targetId, 'cn1', 'recovery must retain the remembered target');
          assert.equal(failed.purpose, 'query', 'recovery must retain the Query purpose');
          assert.equal(failed.defaultBefore, 'pf1', 'the fixture starts with Kane as the tenant default');
          assert.equal(failed.sessionBefore.hasUnsavedChanges, true, 'the fixture includes a dirty editing session');
          assert.match(await page.$eval('[data-testid="hub-account-error"]', (el) => el.textContent || ''), /Authentication failed for all authenticators/, 'the chooser must show the real RPC failure cause');
          assert.match(await page.$eval('[data-testid="hub-sign-in-again"]', (el) => el.textContent || ''), /Sign in again/, 'the failed profile must keep an explicit sign-in-again action');
          await page.evaluate(() => document.querySelector('[data-testid="hub-use-for-open"]')?.click());
          await page.waitForFunction(() => !document.querySelector('[data-testid="hub-account-dialog"]'), { timeout: 15000 });
          const recovered = await page.evaluate(() => structuredClone(window.__uishotRecovery));
          assert.equal(recovered.connectCalls.length, 2, 'the explicit saved-profile choice must retry exactly once');
          assert.equal(recovered.connectCalls[1][5], false, 'saved-profile retry must not force a new sign-in');
          assert.equal(recovered.connectCalls[1][6], 'pf2', 'retry must carry the selected saved profile');
          assert.equal(recovered.connectCalls[1][7], false, 'per-query retry must not change the tenant default');
          assert.equal(recovered.connectCalls[1][8], null, 'saved-profile retry must not invent a login hint');
          assert.equal(recovered.successes.length, 1, 'the host seam must return one successful retry result');
          assert.equal(recovered.successes[0].profileId, 'pf2', 'the successful result must belong to the selected profile');
          assert.equal(recovered.defaultBefore, 'pf1', 'the remembered default identity remains the same');
          assert.equal(recovered.sessionReads.every((s) => s.sessionId === recovered.sessionBefore.sessionId
            && s.modelName === recovered.sessionBefore.modelName && s.hasUnsavedChanges === true), true,
            'the editing session identity and local edits remain unchanged');
          console.log('  Recovery: real cause and sign-in-again action shown; successful host result closed the chooser with pf2');
        } else if (hub === 'local-failure') {
          // A local desktop Query failure is ordinary operational feedback. It must keep the selected target and its
          // Query action available for retry, without opening the Microsoft account chooser.
          const row = await page.evaluate(() => {
            const b = [...document.querySelectorAll('[data-testid="hub-model-row"]')].find((x) => (x.textContent || '').includes('Contoso local test'));
            if (b) b.click();
            return !!b;
          });
          if (!row) throw new Error('Connections local-failure case could not find the local model');
          const query = await page.evaluate(() => {
            const b = [...document.querySelectorAll('[data-testid="hub-outcome"]')].find((x) => (x.textContent || '').trim().startsWith('Query this model'));
            if (b) b.click();
            return !!b;
          });
          if (!query) throw new Error('Connections local-failure case could not find Query this model');
          await page.waitForFunction(() => (document.body.textContent || '').includes('The local model at localhost:51000 is not ready.'), { timeout: 15000 });
          await page.screenshot({ path: out.replace(/\.png$/u, '-failure.png'), fullPage: false });
          const assert = (await import('node:assert/strict')).default;
          const failed = await page.evaluate(() => structuredClone(window.__uishotLocalFailure));
          assert.equal(failed.calls.length, 1, 'the local Query attempt must fail once');
          assert.equal(failed.calls[0][0], 'localhost:51000', 'the local failure must retain the local target');
          assert.equal(await page.$('[data-testid="hub-account-dialog"]'), null, 'a local failure must not open Microsoft account recovery');
          assert.ok(await page.$('[data-testid="hub-outcome"]'), 'the selected local target must retain its retry action');
          await page.evaluate(() => [...document.querySelectorAll('[data-testid="hub-outcome"]')].find((x) => (x.textContent || '').trim().startsWith('Query this model'))?.click());
          await page.waitForFunction(() => window.__uishotLocalFailure.calls.length === 2, { timeout: 15000 });
          assert.equal(await page.$('[data-testid="hub-account-dialog"]'), null, 'a local retry must stay outside Microsoft account recovery');
          console.log('  Local failure: target stayed selected, cause stayed readable, and Query remained retryable');
        } else if (hub === 'add') {
          // Open "Add a published model" and select Service identity so the tenant field + the live setup preflight render.
          const ok = await page.evaluate(() => { const b = [...document.querySelectorAll('button')].find((x) => (x.textContent || '').includes('Published model') && (x.textContent || '').includes('XMLA endpoint once')); if (b) b.click(); return !!b; });
          if (!ok) throw new Error('Connections hub could not find the Published model card');
          await page.waitForFunction(() => (document.body.textContent || '').includes('Add a published model'), { timeout: 15000 });
          const set = await page.evaluate(() => { const sel = document.querySelector('[data-testid="hub-authmode-select"]'); if (!sel) return false; const setter = Object.getOwnPropertyDescriptor(window.HTMLSelectElement.prototype, 'value').set; setter.call(sel, 'serviceprincipal'); sel.dispatchEvent(new Event('change', { bubbles: true })); return true; });
          if (!set) throw new Error('Add view could not find the sign-in method select');
          await page.waitForFunction(() => (document.body.textContent || '').includes('Service identity setup'), { timeout: 15000 });
        }
        await new Promise((r) => setTimeout(r, 500));
      }
      // UISHOT_COMPARE → on the Deploy (Push) surface, point the Target model picker at a published model and open its
      // registry list, so the endpoint-free connection picker (a chip per remembered model, no typed endpoint) renders.
      if (process.env.UISHOT_COMPARE) {
        await new Promise((r) => setTimeout(r, 700));
        const set = await page.evaluate(() => {
          // The two model-ref kind selects (Source, Target) are the ones offering a 'workspace' option; steer the Target.
          const kinds = [...document.querySelectorAll('select')].filter((s) => [...s.options].some((o) => o.value === 'workspace'));
          const target = kinds[1] || kinds[0];
          if (!target) return false;
          const setter = Object.getOwnPropertyDescriptor(window.HTMLSelectElement.prototype, 'value').set;
          setter.call(target, 'workspace');
          target.dispatchEvent(new Event('change', { bubbles: true }));
          return true;
        });
        if (!set) throw new Error('UISHOT_COMPARE: no workspace-capable model-ref select found (render the Deploy Push surface)');
        await new Promise((r) => setTimeout(r, 400));
        await page.evaluate(() => document.querySelector('[data-testid="compare-workspace-picker"]')?.click());
        await new Promise((r) => setTimeout(r, 500));
      }
      if (variant.toLowerCase() === 'diagram' && !hub) {
        await page.waitForSelector('.react-flow__node', { timeout: 15000 });
        await page.waitForSelector('.react-flow__edge', { timeout: 15000 });
      }
      if (process.env.UISHOT_DEPLOY === 'dataagent' || variant.toLowerCase() === 'data agent') {
        await new Promise((r) => setTimeout(r, 500));
        await page.evaluate(() => window.dispatchEvent(new MessageEvent('message', {
          data: { type: 'navigate', tab: 'dataagent' },
        })));
        try {
          await page.waitForFunction(() => [...document.querySelectorAll('button')]
            .some((b) => (b.textContent || '').includes('Elements') || (b.getAttribute('aria-label') || '').includes('elements for')), { timeout: 15000 });
        } catch {
          const visible = await page.evaluate(() => (document.body.innerText || '').replace(/\s+/g, ' ').slice(0, 2000));
          throw new Error(`Data Agent did not reach its editable-source state. Visible text: ${visible}`);
        }
      }
      // UISHOT_FIND → type into the Search tab's find box (a plain input) so results + the type-filter chips
      // render (e.g. node shot.mjs Search with UISHOT_FIND=sales). Runs BEFORE UISHOT_CLICK so a chip can then
      // be toggled by its label (chips render only once there are results).
      if (process.env.UISHOT_FIND) {
        await new Promise((r) => setTimeout(r, 600));
        const el = await page.$('input[placeholder^="Find in names"]');
        if (el) {
          await el.click();
          await page.keyboard.type(process.env.UISHOT_FIND, { delay: 20 });
          await new Promise((r) => setTimeout(r, 1000));   // debounce (250ms) + mock round-trip + render
        }
      }
      // UISHOT_REPLACE → type into the Search tab's "Replace with…" box (enables the per-row Replace buttons +
      // the Replace all… hand-off), e.g. UISHOT_FIND=sales UISHOT_REPLACE=revenue UISHOT_CLICK=Replace.
      if (process.env.UISHOT_REPLACE) {
        const el = await page.$('input[placeholder^="Replace with"]');
        if (el) {
          await el.click();
          await page.keyboard.type(process.env.UISHOT_REPLACE, { delay: 20 });
          await new Promise((r) => setTimeout(r, 1000));
        }
      }
      // Optionally click a toolbar button (by its text) before capturing — to review an interactive state
      // (e.g. UISHOT_CLICK="Bus matrix" or "Collapse all"). Matches exact or prefix text. Wait first, so any
      // async-rendered control (e.g. the live-activity "Claude" chip, which appears only after an event) exists.
      if (process.env.UISHOT_CLICK) {
        await new Promise((r) => setTimeout(r, 900));
        const clicked = await page.evaluate((label) => {
          const btn = [...document.querySelectorAll('button')].find((b) => {
            const t = (b.textContent || '').trim(), a = b.getAttribute('aria-label') || '';
            return t === label || t.startsWith(label) || a === label || a.startsWith(label);
          });
          if (btn) btn.click();
          return !!btn;
        }, process.env.UISHOT_CLICK);
        if (!clicked) throw new Error(`Could not find button matching UISHOT_CLICK=${process.env.UISHOT_CLICK}`);
        await new Promise((r) => setTimeout(r, 600));
      }
      // A SECOND click after the first settles (e.g. UISHOT_CLICK="List Fabric pipelines" UISHOT_CLICK2="Preview"
      // to load the pipeline board, then run a preview) — for reviewing a state that needs two interactions.
      if (process.env.UISHOT_CLICK2) {
        const clicked = await page.evaluate((label) => {
          const btn = [...document.querySelectorAll('button')].find((b) => {
            const t = (b.textContent || '').trim(), a = b.getAttribute('aria-label') || '';
            return t === label || t.startsWith(label) || a === label || a.startsWith(label);
          });
          if (btn) btn.click();
          return !!btn;
        }, process.env.UISHOT_CLICK2);
        if (!clicked) throw new Error(`Could not find button matching UISHOT_CLICK2=${process.env.UISHOT_CLICK2}`);
        await new Promise((r) => setTimeout(r, 600));
      }
      // Optionally click the first element matching a CSS selector (for non-button targets, e.g. a diagram edge:
      // UISHOT_CLICKSEL=".react-flow__edge"). Dispatches a bubbling click so React's delegated onClick fires.
      if (process.env.UISHOT_CLICKSEL) {
        await new Promise((r) => setTimeout(r, 900));
        await page.evaluate((sel) => {
          const el = document.querySelector(sel);
          if (el) el.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true, view: window }));
        }, process.env.UISHOT_CLICKSEL);
        await new Promise((r) => setTimeout(r, 600));
      }
      // Optionally RIGHT-click (contextmenu) the first element matching a CSS selector — for context-menu
      // driven states (e.g. UISHOT_CTXSEL='td.tnum' opens DAX Lab's Explain-this-number slide-over).
      if (process.env.UISHOT_CTXSEL) {
        await new Promise((r) => setTimeout(r, 900));
        await page.evaluate((sel) => {
          const el = document.querySelector(sel);
          if (el) el.dispatchEvent(new MouseEvent('contextmenu', { bubbles: true, cancelable: true, view: window }));
        }, process.env.UISHOT_CTXSEL);
        await new Promise((r) => setTimeout(r, 800));
      }
      // A text-labelled click that runs AFTER UISHOT_CLICKSEL (so you can open a popover/menu via a CSS target,
      // then click a button inside it by text — e.g. open a column-filter funnel via CLICKSEL, then commit a
      // preset: UISHOT_CLICKSEL='[data-funnel="5"]' UISHOT_CLICK3='YTD').
      if (process.env.UISHOT_CLICK3) {
        await new Promise((r) => setTimeout(r, 400));
        await page.evaluate((label) => {
          const btn = [...document.querySelectorAll('button')].find((b) => {
            const t = (b.textContent || '').trim(), a = b.getAttribute('aria-label') || '';
            return t === label || t.startsWith(label) || a === label || a.startsWith(label);
          });
          if (btn) btn.click();
        }, process.env.UISHOT_CLICK3);
        await new Promise((r) => setTimeout(r, 600));
      }
      // Optionally type into the (first) CodeMirror editor — for reviewing interactive editor states like the
      // M autocomplete popup (e.g. UISHOT_CLICK="M query" UISHOT_TYPE="let Q = Table.Sel"). Focuses the
      // editor, selects-all (deterministic context), types with real key events so activateOnTyping fires.
      if (process.env.UISHOT_TYPE) {
        await new Promise((r) => setTimeout(r, 600));
        const el = await page.$('.cm-content');
        if (el) {
          await el.click();
          await page.keyboard.down('Control'); await page.keyboard.press('KeyA'); await page.keyboard.up('Control');
          await page.keyboard.type(process.env.UISHOT_TYPE, { delay: 25 });
          await new Promise((r) => setTimeout(r, 1100));   // let the async completion source resolve + popup render
        }
      }
      // UISHOT_KEY → send keystrokes to the page before capture (real key events, focus on <body>), for
      // reviewing keyboard-driven states — e.g. UISHOT_KEY='?' opens the keyboard-shortcuts cheat sheet.
      if (process.env.UISHOT_KEY) {
        await new Promise((r) => setTimeout(r, 600));
        await page.keyboard.type(process.env.UISHOT_KEY, { delay: 50 });
        await new Promise((r) => setTimeout(r, 500));
      }
      // UISHOT_WF_FILTER → type into the Workflows library filter box (a plain input, not CodeMirror) to
      // review the filtered/auto-expanded rail. Targets the input by its data attribute.
      if (process.env.UISHOT_WF_FILTER) {
        await new Promise((r) => setTimeout(r, 600));
        const el = await page.$('[data-wf-filter]');
        if (el) {
          await el.click();
          await page.keyboard.type(process.env.UISHOT_WF_FILTER, { delay: 25 });
          await new Promise((r) => setTimeout(r, 700));
        }
      }
      if (process.env.UISHOT_WF_HOME_SEARCH) {
        await new Promise((r) => setTimeout(r, 600));
        const el = await page.$('[data-wf-home-search]');
        if (el) {
          await el.click();
          await page.keyboard.type(process.env.UISHOT_WF_HOME_SEARCH, { delay: 25 });
          await new Promise((r) => setTimeout(r, 700));
        }
      }
      // [T215] UISHOT_WF_OPFILTER → with UISHOT_WF=oppicker, wait for the step designer's op picker
      // (the taxonomy tree) and type into its search box with real key events, to review the
      // filtered, fully-expanded question > shelf grouping.
      if (process.env.UISHOT_WF_OPFILTER) {
        const sel = 'input[placeholder="Search all ops…"]';
        await page.waitForSelector(sel, { timeout: 15000 });
        await page.click(sel);
        await page.keyboard.type(process.env.UISHOT_WF_OPFILTER, { delay: 25 });
        await new Promise((r) => setTimeout(r, 700));
      }
      await new Promise((r) => setTimeout(r, 1200));
      if ((process.env.UISHOT_WF || '').startsWith('editor')) {
        const assert = (await import('node:assert/strict')).default;
        const mode = (process.env.UISHOT_WF || '').split('-')[1] || 'steps';
        const button = async (label) => {
          await page.waitForFunction((text) => [...document.querySelectorAll('button')]
            .some((item) => item.textContent.trim() === text && !item.disabled), { timeout: 15000 }, label);
          await page.evaluate((text) => [...document.querySelectorAll('button')]
            .find((item) => item.textContent.trim() === text).click(), label);
        };
        const setValue = async (selector, value) => page.$eval(selector, (field, next) => {
          const proto = field instanceof HTMLTextAreaElement ? HTMLTextAreaElement.prototype : HTMLInputElement.prototype;
          Object.getOwnPropertyDescriptor(proto, 'value').set.call(field, next);
          field.dispatchEvent(new Event('input', { bubbles: true }));
        }, value);
        await page.waitForSelector('.sem-wf-editor[data-wf-view="steps"]', { timeout: 15000 });
        if (mode === 'forms') {
          await page.$eval('.sem-wf-gate', (element) => element.scrollIntoView({ block: 'start' }));
          await page.waitForFunction(() => [...document.querySelectorAll('input')].some((field) => field.value === 'Continue?')
            && [...document.querySelectorAll('select option')].some((option) => option.textContent === 'bpa_clean'));
          console.log('  Workflow editor: rich question and check fields rendered');
        } else if (mode === 'canvas') {
          await button('Canvas');
          await page.waitForSelector('[data-wf-canvas="true"] .react-flow__node', { timeout: 15000 });
          assert.equal(await page.$$eval('[data-wf-canvas-step]', (items) => items.length), 2);
          await page.click('[data-wf-canvas-step="1"]');
          await page.waitForSelector('[data-wf-step-form="finish"]');
          assert.deepEqual(await page.$$eval('.sem-wf-form-actions button', (items) => items.slice(0, 2).map((item) => item.textContent.trim())), ['Move left', 'Move right']);
          assert.equal(await page.$$eval('[data-wf-canvas="true"] .react-flow__edge-text', (items) => items.length), 0);
          console.log('  Workflow editor: Canvas opened and selected the shared second step');
        } else if (mode === 'source') {
          await button('Source');
          await page.waitForSelector('textarea[aria-label="Workflow source"]', { timeout: 15000 });
          const before = await page.$eval('textarea[aria-label="Workflow source"]', (field) => field.value);
          await setValue('textarea[aria-label="Workflow source"]', before.replace('title: Loop and hand-off', 'title: Loop and hand-off revised'));
          await button('Steps');
          await page.waitForSelector('.sem-wf-editor[data-wf-view="steps"]');
          await button('Workflow settings');
          assert.equal(await page.$eval('.sem-wf-form-card input', (field) => field.value), 'Loop and hand-off revised');
          await button('Undo draft');
          await page.waitForFunction(() => document.querySelector('.sem-wf-form-card input')?.value === 'Loop and hand-off');
          await button('Redo');
          await page.waitForFunction(() => document.querySelector('.sem-wf-form-card input')?.value === 'Loop and hand-off revised');
          await button('Source');
          assert.match(await page.$eval('textarea[aria-label="Workflow source"]', (field) => field.value), /title: Loop and hand-off revised/);
          console.log('  Workflow editor: Source edit projected into Steps and remained in the shared draft');
        } else if (mode === 'stock') {
          await page.waitForFunction(() => document.body.textContent.includes('Built-in') && document.body.textContent.includes('Copy to this project'));
          assert.equal(await page.$eval('.sem-wf-form-card input', (field) => field.matches(':disabled')), true);
          await button('Source');
          assert.equal(await page.$eval('textarea[aria-label="Workflow source"]', (field) => field.readOnly), true);
          await button('Steps');
          await page.waitForSelector('.sem-wf-editor[data-wf-view="steps"]');
          console.log('  Workflow editor: built-in Steps and Source stayed read-only');
        } else if (mode === 'copy') {
          assert.equal(await page.$eval('.sem-wf-form-card input', (field) => field.matches(':disabled')), true);
          await button('Copy to this project');
          await page.waitForFunction(() => document.body.textContent.includes('Project copy created. You can edit it now.'));
          assert.equal(await page.$eval('.sem-wf-form-card input', (field) => field.matches(':disabled')), false);
          assert.equal(await page.evaluate(() => window.__workflowDocument.calls.find((call) => call.method === 'saveWorkflow').params[3]), true);
          assert.equal(await page.evaluate(() => window.__workflowDocument.calls.find((call) => call.method === 'saveWorkflow').params[4]), 'uishot');
          console.log('  Workflow editor: exact built-in copy preserved createOnly and session identity');
        } else if (mode === 'v1') {
          await button('Add step after');
          await page.waitForFunction(() => document.body.textContent.includes('2. New step'));
          await button('Save workflow');
          await page.waitForSelector('[aria-label="Save workflow preview"]');
          const previewCall = await page.evaluate(() => structuredClone(window.__workflowDocument.calls.filter((call) => call.method === 'previewWorkflowEdit').at(-1)));
          const operations = JSON.parse(previewCall.params[3]);
          assert.equal(operations[0].op, 'stabilize_step_ids');
          assert.equal(operations[1].op, 'add_step');
          await page.waitForFunction(() => document.body.textContent.includes('Step ids are made stable so steps can move safely.'));
          console.log('  Workflow editor: v1 stayed editable and stabilized IDs before a structural edit');
        } else if (mode === 'preview' || mode === 'warning' || mode === 'apply' || mode === 'conflict') {
          const title = '.sem-wf-form-card input';
          await setValue(title, 'Review every table carefully');
          await page.waitForFunction(() => document.body.textContent.includes('Unsaved draft.'));
          if (mode === 'conflict') await page.evaluate(() => {
            const fixture = window.__workflowDocument;
            fixture.set(fixture.document.exactText + String.fromCharCode(10) + 'External edit', 'user', true, false);
          });
          await button('Save workflow');
          await page.waitForSelector('[aria-label="Save workflow preview"]');
          const previewCall = await page.evaluate(() => structuredClone(window.__workflowDocument.calls.filter((call) => call.method === 'previewWorkflowEdit').at(-1)));
          assert.equal(previewCall.params[0], 'loop-handoff');
          assert.equal(previewCall.params[6], 'uishot');
          assert.equal(JSON.parse(previewCall.params[3])[0].op, 'set_field');
          if (mode === 'conflict') {
            await page.waitForFunction(() => document.body.textContent.includes('Choose Reload saved file to drop your draft'));
            assert.equal(await page.$eval('[data-wf-step-form="review"] input', (field) => field.value), 'Review every table carefully');
            await button('Review current file');
            await page.waitForFunction(() => document.body.textContent.includes('External edit'));
            await page.waitForFunction(() => document.body.textContent.includes('Read the newer saved file. Keep my draft then checks your draft against it.'));
            assert.equal(await page.$$eval('button', (items) => items.some((item) => item.textContent.trim() === 'Keep my draft')), true);
            console.log('  Workflow editor: conflict kept the draft and exposed the current exact file');
          } else if (mode === 'apply') {
            await button('Apply');
            await page.waitForFunction(() => document.body.textContent.includes('Workflow saved.'));
            const editCall = await page.evaluate(() => structuredClone(window.__workflowDocument.calls.filter((call) => call.method === 'editWorkflowDocument').at(-1)));
            assert.match(editCall.params[3], /\/project\/\.semanticus\/workflows\/loop-handoff\.md$/);
            assert.equal(editCall.params[4], 'human');
            assert.equal(editCall.params[5], 'uishot');
            assert.match(await page.evaluate(() => window.__workflowDocument.document.exactText), /workflow editor preview: set_field/);
            console.log('  Workflow editor: serialized preview applied through the guarded exact writer');
          } else if (mode === 'warning') {
            await page.waitForFunction(() => document.body.textContent.includes('Add a clearer description before sharing this workflow.'));
            console.log('  Workflow editor: warning rendered inline in the save preview');
          } else {
            await page.waitForFunction(() => document.body.textContent.includes('Ready to save. Only the lines below change.'));
            console.log('  Workflow editor: serialized engine preview rendered before apply');
          }
        }
        if (errors.length) throw new Error('Workflow editor browser errors: ' + errors.join('; '));
      }
      if ((process.env.UISHOT_WF || '').startsWith('document')) {
        const assert = (await import('node:assert/strict')).default;
        const sourceField = '#workflow-source';
        const click = async (label) => {
          await page.waitForFunction((text) => [...document.querySelectorAll('button')].some((b) => b.textContent.trim() === text && !b.disabled), {}, label);
          await page.evaluate((text) => [...document.querySelectorAll('button')].find((b) => b.textContent.trim() === text).click(), label);
        };
        const expectText = (text) => page.waitForFunction((value) => document.body.textContent.includes(value), {}, text);
        const edit = async (text) => {
          await page.waitForFunction(() => document.querySelector('#workflow-source') && !document.querySelector('#workflow-source').readOnly);
          await page.$eval(sourceField, (field, value) => {
            Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype, 'value').set.call(field, value);
            field.dispatchEvent(new Event('input', { bubbles: true }));
          }, text);
        };
        const value = () => page.$eval(sourceField, (field) => field.value);
        const fixture = () => page.evaluate(() => structuredClone({ document: window.__workflowDocument.document, source: window.__workflowDocument.source, calls: window.__workflowDocument.calls }));
        const retained = () => page.evaluate(() => {
          const doc = window.__workflowDocument.document;
          return window.acquireVsCodeApi().getState()['workflow-document-draft:' + doc.path];
        });
        const reopen = async () => { await click('Back to design'); await click('Edit file'); await page.waitForSelector(sourceField); };
        await page.waitForSelector(sourceField);
        const initial = await fixture();
        if ((process.env.UISHOT_WF || '').startsWith('document-upgrade')) {
          const previewSelector = '[aria-label="Format upgrade preview"]';
          const preview = async () => { await click('Upgrade format'); await page.waitForSelector(previewSelector); };
          const absentPreview = () => page.waitForFunction(() => !document.querySelector('[aria-label="Format upgrade preview"]'));
          const buttonDisabled = (label) => page.evaluate((text) => [...document.querySelectorAll('button')].find((b) => b.textContent.trim() === text)?.disabled, label);
          const upgradeCalls = async () => (await fixture()).calls.filter((call) => call.method === 'upgradeWorkflow');
          const review = () => page.evaluate(() => structuredClone(window.__workflowDocument.lastUpgradePreview));
          const reset = async () => {
            await page.evaluate(() => { const f = window.__workflowDocument; f.set(f.source); });
            await page.waitForFunction(() => document.querySelector('#workflow-source').value === window.__workflowDocument.source.slice(1).split(String.fromCharCode(13, 10)).join(String.fromCharCode(10)));
          };
          if (process.env.UISHOT_WF === 'document-upgrade-stock') {
            assert.equal(await page.$eval(sourceField, (field) => field.readOnly), true);
            await preview();
            await expectText('Create a project copy, then preview its upgrade before applying.');
            assert.equal(await buttonDisabled('Apply upgrade'), undefined);
            assert.equal((await review()).canApply, false);
            assert.equal((await review()).changed, false);
            assert.equal((await fixture()).document.exactText, initial.source);
            await click('Create project copy');
            await expectText('Project copy created. You can edit it now.');
            await absentPreview();
            assert.equal((await fixture()).document.metadata.schemaVersion, 1);
            const copyCall = (await fixture()).calls.find((call) => call.method === 'saveWorkflow');
            assert.equal(copyCall.params[3], true);
            await preview();
            assert.notEqual((await review()).document.path, initial.document.path);
            await click('Apply upgrade');
            await expectText('File upgraded to format v2.');
            const calls = await upgradeCalls();
            assert.equal(calls.filter((call) => call.params[1] === false).length, 1);
            assert.equal(calls.at(-1).params[3], (await fixture()).document.path);
            console.log('  Upgrade: stock preview, copy-first guard and fresh project preview passed');
          } else {
            const normalized = await value();
            await edit(normalized + 'My local draft');
            assert.equal(await buttonDisabled('Upgrade format'), true);
            await click('Save file');
            await expectText('File saved.');
            assert.equal((await fixture()).document.metadata.schemaVersion, 1);
            assert.equal((await upgradeCalls()).length, 0, 'normal saves never call upgrade');
            await reset();
            await preview();
            const first = await review();
            assert.equal(first.changed, false);
            assert.equal(await buttonDisabled('Apply upgrade'), false);
            assert.equal((await fixture()).document.exactText, initial.source);
            assert.equal(await page.$eval('[aria-label="Proposed source diff"]', (field) => field.textContent), first.diff);
            assert.deepEqual((await upgradeCalls()).at(-1).params, ['loop-handoff', true, first.document.byteHash, first.document.path, 'human']);
            await page.screenshot({ path: out.replace('.png', '-preview.png') });
            await edit(normalized + 'Keep this draft');
            await absentPreview();
            const draft = await retained();
            await page.evaluate(() => { const f = window.__workflowDocument; f.set(f.source + 'External edit'); });
            await expectText('The saved file changed while you were editing.');
            assert.equal(await value(), normalized + 'Keep this draft');
            assert.equal((await retained()).base.byteHash, draft.base.byteHash);
            assert.equal(await buttonDisabled('Upgrade format'), true);
            await reopen();
            assert.equal(await value(), normalized + 'Keep this draft');
            await click('Reload saved file'); await click('Discard edits and reload');
            await expectText('Loaded the saved file.');
            await reset();
            console.log('  Upgrade: exact preview, explicit apply, ordinary saves and dirty-draft preservation passed');

            await preview();
            await page.evaluate(() => { const f = window.__workflowDocument; f.set(f.source + 'Notified edit'); });
            await absentPreview();
            await preview();
            const stale = await review();
            await page.evaluate(() => { const f = window.__workflowDocument; f.set(f.source + 'Unseen edit', 'user', true, false); });
            await click('Apply upgrade');
            await expectText('The workflow changed on disk. Preview again before upgrading.');
            assert.equal((await fixture()).document.exactText, initial.source + 'Unseen edit');
            assert.deepEqual((await upgradeCalls()).at(-1).params, ['loop-handoff', false, stale.document.byteHash, stale.document.path, 'human']);
            await preview();
            const oldPath = (await review()).document.path;
            await page.evaluate(() => { window.__workflowDocument.document.path = '/saved-as/.semanticus/workflows/loop-handoff.md'; });
            await click('Apply upgrade');
            await expectText('The workflow path changed. Reopen the file before upgrading.');
            assert.equal((await upgradeCalls()).at(-1).params[3], oldPath);
            assert.equal(await page.evaluate(() => window.__workflowDocument.upgradeWrites), 0);
            await reset();
            console.log('  Upgrade: notified preview invalidation and unseen hash/path refusal passed');

            await page.evaluate(() => { window.__workflowDocument.upgradeDeferred = true; });
            await click('Upgrade format');
            await page.waitForFunction(() => !!window.__workflowDocument.upgradePending);
            await page.evaluate(() => { const f = window.__workflowDocument; f.set(f.source + 'Newer saved version'); });
            await page.waitForFunction(() => document.querySelector('#workflow-source').value.endsWith('Newer saved version'));
            await page.evaluate(() => {
              const f = window.__workflowDocument;
              window.dispatchEvent(new MessageEvent('message', { data: { type: 'rpcResult', id: f.upgradePendingId, result: f.upgradePending } }));
              f.upgradeDeferred = false;
            });
            await page.waitForFunction(() => !document.querySelector('#workflow-source').readOnly);
            assert.equal(await page.$(previewSelector), null, 'a late preview cannot attach to a newer saved version');
            await reset();
            await page.evaluate(() => { window.__workflowDocument.upgradeParseError = 'Proposed v2 source did not parse.'; });
            await preview();
            await expectText('Proposed v2 source did not parse.');
            assert.equal(await buttonDisabled('Apply upgrade'), undefined);
            await page.evaluate(() => { window.__workflowDocument.upgradeParseError = null; });
            await preview();
            const successful = await review();
            await click('Apply upgrade');
            await expectText('File upgraded to format v2.');
            assert.equal((await fixture()).document.exactText, successful.proposedText);
            assert.equal((await fixture()).document.metadata.schemaVersion, 2);
            assert.equal(await page.evaluate(() => window.__workflowDocument.upgradeWrites), 1);
            await preview();
            await expectText('This workflow already uses format v2. No changes needed.');
            assert.equal(await buttonDisabled('Apply upgrade'), undefined);
            assert.equal((await review()).changed, false);
            assert.equal(await page.evaluate(() => window.__workflowDocument.upgradeWrites), 1);
            console.log('  Upgrade: late preview suppression, parse refusal, exact apply and v2 no-op passed');
          }
        } else if (process.env.UISHOT_WF === 'document-stock') {
          assert.equal(await page.$eval(sourceField, (field) => field.readOnly), true);
          await page.evaluate(() => { window.__workflowDocument.createRace = true; });
          await click('Create project copy');
          await expectText('A project workflow already exists. It was not overwritten.');
          assert.equal((await fixture()).document.exactText, initial.source + 'Existing project copy');
          await click('Reload saved file');
          await page.waitForFunction(() => !document.querySelector('#workflow-source').readOnly);
          await page.evaluate(() => window.__workflowDocument.set(window.__workflowDocument.source, 'stock'));
          await page.waitForFunction(() => document.querySelector('#workflow-source').readOnly);
          await click('Create project copy');
          await expectText('Project copy created. You can edit it now.');
          assert.equal(await page.$eval(sourceField, (field) => field.readOnly), false);
          assert.equal((await fixture()).document.exactText, initial.source);
          assert.equal((await fixture()).calls.filter((call) => call.method === 'saveWorkflow').every((call) => call.params[3] === true), true);
          console.log('  Document: stock read-only, createOnly race refusal and exact project copy passed');
        } else if (process.env.UISHOT_WF === 'document-invalid') {
          await expectText('This file has a parse error.');
          await edit((await value()).replace('schemaVersion: broken', 'schemaVersion: 2'));
          await click('Save file');
          await expectText('File saved.');
          assert.equal((await fixture()).document.exactText, initial.source);
          await click('Back to design');
          await page.waitForSelector('[data-wf-canvas-step]');
          await click('Edit file');
          await page.waitForSelector(sourceField);
          console.log('  Document: broken saved file opens, repairs and refreshes design passed');
        } else {
          const normalized = await value();
          const edited = normalized.replace('Walk the list once per table.', 'Review every table carefully.');
          await edit(edited);
          await click('Save file');
          await expectText('File saved.');
          assert.equal((await fixture()).document.exactText, initial.source.replace('Walk the list once per table.', 'Review every table carefully.'));
          assert.equal((await fixture()).calls.find((call) => call.method === 'editWorkflowDocument').params[3], initial.document.path);
          await click('Back to design');
          await page.waitForSelector('[data-wf-canvas-step]');
          await expectText('Review every table carefully.');
          await click('Edit file');
          await page.waitForSelector(sourceField);
          await click('Save file');
          await expectText('No changes to save.');
          console.log('  Document: rich source, BOM/CRLF/comments/fences, no-op and design refresh passed');

          const invalid = edited.replace('schemaVersion: 2', 'schemaVersion: broken');
          const savedHash = (await fixture()).document.byteHash;
          await edit(invalid);
          await click('Save file');
          await expectText('Invalid schemaVersion in submitted text.');
          assert.equal(await value(), invalid);
          assert.equal((await retained()).base.byteHash, savedHash);
          await reopen();
          assert.equal(await value(), invalid);
          await click('Reload saved file');
          await expectText('Reloading will discard your unsaved text');
          await click('Keep my edits');
          assert.equal(await value(), invalid);
          await click('Reload saved file');
          await click('Discard edits and reload');
          await expectText('Loaded the saved file.');
          assert.equal(await value(), edited);
          console.log('  Document: invalid refusal, retained draft and explicit discard passed');

          const conflicting = edited + 'My local draft';
          await edit(conflicting);
          await page.evaluate(() => { const f = window.__workflowDocument; f.set(f.document.exactText + 'External edit', 'user', true, false); });
          await click('Save file');
          await expectText('The workflow changed on disk.');
          assert.equal(await value(), conflicting);
          assert.equal((await retained()).base.byteHash, savedHash);
          assert.equal((await retained()).latest.exactText, (await fixture()).document.exactText);
          await page.evaluate(() => { const f = window.__workflowDocument; f.set(f.document.exactText + ' plus notification'); });
          await page.waitForFunction(() => document.querySelector('details pre')?.textContent.includes('plus notification'));
          assert.equal(await value(), conflicting);
          assert.equal((await retained()).base.byteHash, savedHash);
          await reopen();
          assert.equal(await value(), conflicting);
          const reviewedHash = (await fixture()).document.byteHash;
          await page.evaluate(() => [...document.querySelectorAll('summary')].find((summary) => summary.textContent.includes('Compare with the saved file')).click());
          await page.evaluate(() => [...document.querySelectorAll('button')].find((button) => button.textContent.trim() === 'Keep my edits and enable Save').scrollIntoView({ block: 'center' }));
          await page.screenshot({ path: join(dirname(out), 'editor-rebase-review.png'), fullPage: true });
          await click('Keep my edits and enable Save');
          assert.equal(await value(), conflicting);
          assert.equal((await retained()).base.byteHash, reviewedHash);
          await click('Save file');
          await expectText('File saved.');
          assert.equal((await fixture()).document.exactText, initial.source.replace('Walk the list once per table.', 'Review every table carefully.') + 'My local draft');
          assert.equal((await fixture()).calls.filter((call) => call.method === 'editWorkflowDocument').at(-1).params[1], reviewedHash);
          console.log('  Document: explicit same-file rebase retains the draft and saves against the reviewed hash');
          await edit(conflicting + ' unsaved again');
          await click('Reload saved file');
          await click('Discard edits and reload');
          await expectText('Loaded the saved file.');
          await page.evaluate(() => { const f = window.__workflowDocument; f.set(f.source); });
          await page.waitForFunction(() => document.querySelector('#workflow-source').value.endsWith('```' + String.fromCharCode(10)));
          assert.equal(await value(), normalized);
          console.log('  Document: stale refusal, dirty library refresh, original fence and clean refresh passed');

          // A refusal arriving after unmount must not erase the persisted draft.
          await edit(invalid);
          await page.evaluate(() => { window.__workflowDocument.deferred = true; });
          await click('Save file');
          await page.waitForFunction(() => !!window.__workflowDocument.pending);
          assert.equal(await page.$eval(sourceField, (field) => field.readOnly), true);
          await click('Back to design');
          await page.evaluate(() => {
            const f = window.__workflowDocument;
            window.dispatchEvent(new MessageEvent('message', { data: { type: 'rpcResult', id: f.pendingId, result: f.pending } }));
            f.deferred = false;
          });
          await click('Edit file');
          await page.waitForSelector(sourceField);
          assert.equal(await value(), invalid);
          assert.equal((await retained()).text.includes('schemaVersion: broken'), true);
          console.log('  Document: refusal after leaving preserves the retained draft passed');
          await click('Reload saved file');
          await click('Discard edits and reload');
          await expectText('Loaded the saved file.');
          await edit(normalized.replace('Walk the list once per table.', 'Review every table carefully.'));
        }
        if (errors.length) throw new Error('Document browser errors: ' + errors.join('; '));
      }
      if (process.env.UISHOT_WF === 'boxes') {
        const step = '[data-wf-canvas-step="1"]';
        await page.waitForSelector(step);
        await page.click(step);
        await page.waitForFunction(() => document.getElementById('workflow-canvas-step')?.value === '1');
        console.log('  Canvas: selection passed');
        const position = () => page.$eval(step, (element) => element.closest('.react-flow__node').style.transform);
        const initial = await position();
        const box = await (await page.$(step)).boundingBox();
        await page.mouse.move(box.x + 70, box.y + 35);
        await page.mouse.down();
        await page.mouse.move(box.x + 110, box.y + 105, { steps: 8 });
        await page.mouse.up();
        await page.waitForFunction(() => window.__workflowLayoutWrites.length === 1);
        const moved = await position();
        if (moved === initial) throw new Error('Canvas step did not move after dragging');
        console.log('  Canvas: dragging passed');
        await page.keyboard.press('Delete');
        if (await page.$$eval('[data-wf-canvas-step]', (items) => items.length) !== 3)
          throw new Error('Delete removed a read-only Canvas step');
        await page.click('[data-wf-view-btn="outline"]');
        await page.click('[data-wf-view-btn="boxes"]');
        await page.waitForSelector(step);
        await page.waitForFunction((expected) => document.querySelector('[data-wf-canvas-step="1"]')?.closest('.react-flow__node').style.transform === expected, {}, moved);
        console.log('  Canvas: layout recall passed');
        await page.select('#workflow-canvas-step', '2');
        await page.waitForFunction(() => document.querySelector('[data-wf-canvas-step="2"]')?.closest('.react-flow__node').classList.contains('selected'));
        console.log('  Canvas: detail navigation passed');
        await page.evaluate(() => [...document.querySelectorAll('button')].find((button) => button.textContent === 'Arrange').click());
        await page.waitForFunction((expected) => document.querySelector('[data-wf-canvas-step="1"]')?.closest('.react-flow__node').style.transform === expected, {}, initial);
        await page.waitForFunction(() => window.__workflowLayoutWrites.length === 2);
        await page.evaluate(() => {
          const current = Object.values(window.__workflowLayouts)[0];
          const next = { ...current, revision: String(Number(current.revision) + 1), positions: { ...current.positions, 'step-2': { x: 320, y: 120 } } };
          window.__workflowLayouts[current.name] = next;
          window.dispatchEvent(new MessageEvent('message', { data: { type: 'workflowLayoutDidChange', payload: next } }));
        });
        await page.waitForFunction(() => document.querySelector('[data-wf-canvas-step="1"]')?.closest('.react-flow__node').style.transform === 'translate(320px, 120px)');
        console.log('  Canvas: shared external update passed');
        await page.evaluate(() => {
          window.__workflowLayoutError = 'Workflow or layout changed. Reload the layout before saving again.';
          [...document.querySelectorAll('button')].find(button => button.textContent === 'Arrange').click();
        });
        await page.waitForFunction(() => document.body.textContent.includes('Layout not saved:'));
        await page.waitForFunction((expected) => document.querySelector('[data-wf-canvas-step="1"]')?.closest('.react-flow__node').style.transform === expected, {}, initial);
        await page.evaluate(() => window.__changeCanvasDefinition('Capture intent updated'));
        await page.waitForFunction(() => document.querySelector('[data-wf-canvas-step="0"]')?.textContent.includes('Capture intent updated'));
        if (!await page.$('[role="alert"]')) throw new Error('Definition refresh discarded the failed layout save');
        if (await position() !== initial) throw new Error('Definition refresh discarded local positions');
        await page.evaluate(() => window.__changeCanvasDefinition('Capture intent'));
        await page.waitForFunction(() => !document.querySelector('[data-wf-canvas-step="0"]')?.textContent.includes('updated'));
        console.log('  Canvas: definition refresh preserves unsaved positions and conflict');
        await page.screenshot({ path: join(dirname(out), 'studio-workflows-layout-error.png'), fullPage: true });
        await page.evaluate(() => {
          window.__workflowLayoutError = null;
          [...document.querySelectorAll('button')].find(button => button.textContent === 'Reload shared layout').click();
        });
        await page.waitForFunction(() => !document.body.textContent.includes('Layout not saved:'));
        await page.waitForFunction(() => document.querySelector('[data-wf-canvas-step="1"]')?.closest('.react-flow__node').style.transform === 'translate(320px, 120px)');
        console.log('  Canvas: failed save preserves local positions; reload recovers shared layout');
        await page.evaluate(() => [...document.querySelectorAll('button')].find(button => button.textContent === 'Reset').click());
        await page.waitForFunction(() => window.__workflowLayoutWrites.length === 3 && Object.keys(window.__workflowLayoutWrites.at(-1).positions).length === 0);
        await page.evaluate(() => [...document.querySelectorAll('button')].find(button => button.textContent === 'Fit all').click());
        if (await page.evaluate(() => window.__workflowLayoutWrites.length) !== 3) throw new Error('Fit all saved a layout');
        console.log('  Canvas: Reset saves empty positions; Fit all does not save');
        await page.evaluate(() => {
          window.__workflowForeignBeforeReply = { 'step-2': { x: 410, y: 190 } };
          [...document.querySelectorAll('button')].find(button => button.textContent === 'Arrange').click();
        });
        await page.waitForFunction(() => window.__workflowLayoutWrites.length === 4
          && document.querySelector('[data-wf-canvas-step="1"]')?.closest('.react-flow__node').style.transform === 'translate(410px, 190px)');
        await page.evaluate(() => [...document.querySelectorAll('button')].find(button => button.textContent === 'Arrange').click());
        await page.waitForFunction(() => window.__workflowLayoutWrites.length === 5);
        if (await page.$('[role="alert"]')) throw new Error('A received foreign layout left the next save on a stale revision');
        console.log('  Canvas: foreign notification before save reply applies and the next save uses its revision');
        await page.select('#workflow-canvas-step', '0');
      }
      if (process.env.UISHOT_WF === 'loop') {
        await page.waitForFunction(() => document.body.textContent.includes('2 of 4 items finished'));
        const expandSkipped = async () => page.evaluate(() => {
          const button = [...document.querySelectorAll('button')].find((item) => item.textContent.includes('Item 2 of 4'));
          if (!button) throw new Error('Skipped loop item is missing');
          button.click();
        });
        await expandSkipped();
        await page.waitForFunction(() => document.body.textContent.includes('Returns is maintained by the finance team.'));
        await expandSkipped();
        await page.waitForFunction(() => !document.body.textContent.includes('Returns is maintained by the finance team.'));
      }
      if (process.env.UISHOT_WF === 'calls') {
        await page.waitForFunction(() => [...document.querySelectorAll('textarea')].some((field) => field.value === 'The customer name recorded on the sales order.'));
        const togglePending = async () => page.evaluate(() => {
          const button = [...document.querySelectorAll('button')].find((item) => item.textContent.includes('Item 2 of 2'));
          if (!button) throw new Error('Second table iteration is missing');
          button.click();
        });
        await togglePending();
        await page.waitForFunction(() => [...document.querySelectorAll('button')].filter((item) => item.textContent.includes('Run workflow · Review table')).length === 2);
        await togglePending();
        await page.click('textarea');
        await page.keyboard.down('Control'); await page.keyboard.press('KeyA'); await page.keyboard.up('Control');
        await page.keyboard.type('Customer name from the original sales order.');
        await page.evaluate(() => {
          const payload = structuredClone(window.__uishotLastRun);
          payload.currentStep.providedAnswers.description.value = 'A newer supplied draft';
          payload.currentStep.instructions += ' Continue with your edits.';
          window.dispatchEvent(new MessageEvent('message', { data: { type: 'workflowDidChange', payload } }));
        });
        await page.waitForFunction(() => document.body.textContent.includes('Continue with your edits.'));
        if (await page.$eval('textarea', (field) => field.value) !== 'Customer name from the original sales order.')
          throw new Error('A run update replaced the unsaved call answer');
      }
      // ?pq waits for the unified M workspace's live sample, then opens a menu / toggles profile.
      if (['loop', 'calls'].includes(process.env.UISHOT_WF)) {
        await page.evaluate(() => {
          const payload = structuredClone(window.__uishotLastRun);
          const stepId = payload.currentStep.stepId;
          for (const row of payload.steps) if (row.stepId === stepId) row.status = 'failed';
          for (const frame of payload.frames ?? [])
            for (const row of frame.steps) if (row.stepId === stepId) row.status = 'failed';
          window.dispatchEvent(new MessageEvent('message', { data: { type: 'workflowDidChange', payload } }));
        });
        await page.waitForFunction(() => {
          const currentFrames = [...document.querySelectorAll('button[aria-expanded="true"]')]
            .filter((button) => button.disabled && (button.textContent.includes('Item ') || button.textContent.includes('Run workflow ·')));
          return currentFrames.length > 0 && currentFrames.every((button) => button.textContent.includes('failed'));
        });
        console.log('  Failed current frames retain their failure label and remain expanded for retry');
      }
      if (process.env.UISHOT_PQ) await new Promise((r) => setTimeout(r, 2200));
      // ?tests drives Run suite → sub-tab; give the run + re-render time to settle.
      if (process.env.UISHOT_TESTS) await new Promise((r) => setTimeout(r, 1500));
      // Make interaction captures executable assertions, not screenshots of whatever happened to render.
      if (process.env.UISHOT_EXPECT) {
        await page.waitForFunction((expected) => (document.body.textContent || '').includes(expected),
          { timeout: 15000 }, process.env.UISHOT_EXPECT);
      }
      // Docs renders the actual export inside an iframe. Scroll that inner document to a named section so visual
      // review can inspect relationship diagrams and other below-the-fold exported content, not only the cover.
      if (variant === 'Docs' && process.env.UISHOT_DOC_SECTION) {
        const iframe = await page.waitForSelector('iframe[title="Documentation preview"]', { timeout: 15000 });
        const frame = await iframe.contentFrame();
        if (!frame) throw new Error('Docs preview iframe was unavailable');
        await frame.waitForFunction((id) => !!document.getElementById(id), { timeout: 15000 }, process.env.UISHOT_DOC_SECTION);
        await frame.evaluate((id) => document.getElementById(id)?.scrollIntoView({ block: 'start' }), process.env.UISHOT_DOC_SECTION);
        await new Promise((r) => setTimeout(r, 500));
      }
      // UISHOT_FULL=1 → capture the whole scrolled page (long content like an expanded audit trail),
      // not just the 1000px viewport.
      await page.screenshot({ path: out, fullPage: !!process.env.UISHOT_FULL });
    }
    console.log(`  ✓ ${target}/${hub ? `Connections:${hub}` : variant} -> ${out} (${statSync(out).size} b)`
      + (errors.length ? `  [${errors.length} page errors]` : '')
      + (expectedNotices.length ? `  [${expectedNotices.length} expected sandbox notices]` : ''));
    if (errors.length) errors.slice(0, 6).forEach((e) => console.log('      ' + e));
  } catch (error) {
    if (/^(document|editor)/.test(process.env.UISHOT_WF || '')) {
      console.error('  Workflow document failure:', ...errors);
      console.error(await page.evaluate(() => document.body.innerText.slice(-5000)));
      await page.screenshot({ path: out });
    }
    throw error;
  } finally {
    await page.close();
  }
  return out;
}

// ---- CLI ----
const a = (process.argv[2] || '').trim();
const b = (process.argv[3] || '').trim();
const c = (process.argv[4] || '').trim();
const outOf = (p) => (isAbsolute(p) ? p : resolve(process.cwd(), p));
const slug = (s) => s.toLowerCase().replace(/\s+/g, '-');
const defaultOut = (target, variant) => join(__dir, 'shots', `${target}-${slug(variant)}.png`);

let jobs = [];
if (a.toLowerCase() === 'all') {
  jobs = [...STUDIO_TABS.map((t) => ({ target: 'studio', variant: t })),
          ...PROPGRID_SCENARIOS.map((s) => ({ target: 'propgrid', variant: s })),
          ...CONNECTION_HUB_STATES]
         .map((j) => ({ ...j, out: j.out || defaultOut(j.target, j.variant) }));
} else if (a.toLowerCase() === 'connections') {
  jobs = [...CONNECTION_HUB_STATES];   // just the Connections hub states (sections, work-locally, stale identity, switch, standalone)
} else if (a.toLowerCase() === 'connections-recovery') {
  jobs = [CONNECTION_HUB_STATES.find((j) => j.hub === 'recovery')];
} else if (a.toLowerCase() === 'connections-local-failure') {
  jobs = [CONNECTION_HUB_STATES.find((j) => j.hub === 'local-failure')];
} else if (a.toLowerCase() === 'connections-state') {
  const state = CONNECTION_HUB_STATES.find((j) => j.hub === (b || 'open'));
  if (!state) throw new Error(`Unknown Connections state: ${b}`);
  jobs = [state];
} else if (a.toLowerCase() === 'studio') {
  const variant = b || 'Diagram';
  jobs = [{ target: 'studio', variant, out: c ? outOf(c) : defaultOut('studio', variant) }];
} else if (a.toLowerCase() === 'propgrid') {
  const variant = b || 'measure';
  jobs = [{ target: 'propgrid', variant, out: c ? outOf(c) : defaultOut('propgrid', variant) }];
} else {
  const variant = a || 'Diagram';                 // bare tab label (back-compat), default Diagram
  jobs = [{ target: 'studio', variant, out: b ? outOf(b) : defaultOut('studio', variant) }];
}

const needsStudio = jobs.some((j) => j.target !== 'propgrid');
if (needsStudio && !existsSync(join(webRoot, 'media', 'studio', 'studio.js'))) {
  console.error('media/studio/studio.js is missing. Run npm run build:webview in Semanticus.VSCode first.');
  process.exit(1);
}

mkdirSync(join(__dir, 'shots'), { recursive: true });
const server = await startServer();
const port = server.address().port;
const browserExecutable = findBrowser();
const browser = await puppeteer.launch({
  executablePath: browserExecutable,
  // chrome-headless-shell accepts the shell mode. Arch's full Chromium fallback needs the normal headless
  // switch or the DevTools connection closes before the first page can load.
  headless: browserExecutable.endsWith('/chromium') ? true : 'shell',
  args: ['--no-sandbox', '--disable-gpu', '--disable-dev-shm-usage', '--force-color-profile=srgb', '--hide-scrollbars'],
});
let failed = 0;
try {
  console.log(`uishot: ${jobs.length} capture(s)`);
  for (const job of jobs) {
    try { await capture(browser, port, job); }
    catch (e) { failed++; console.error(`  ✗ ${job.target}/${job.variant}: ${(e && e.message) || e}`); }
  }
} finally {
  await browser.close();
  server.close();
}
process.exit(failed ? 1 : 0);
