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
// Chromium via SEMANTICUS_BROWSER, else the puppeteer cache, else Edge/Chrome.
import puppeteer from 'puppeteer-core';
import { fileURLToPath } from 'node:url';
import { dirname, join, resolve, isAbsolute, extname, normalize } from 'node:path';
import { existsSync, mkdirSync, statSync, readFileSync, readdirSync } from 'node:fs';
import { homedir } from 'node:os';
import { createServer } from 'node:http';

const __dir = dirname(fileURLToPath(import.meta.url));
const webRoot = resolve(__dir, '..', '..');          // Semanticus.VSCode/ (so /media/... and /tools/... resolve)

// Keep this in the same reading order as App.tsx: intent groups first, then the three standalone
// surfaces. `all` is the product-wide visual gate, so an omitted/renamed tab would be a silent coverage hole.
const STUDIO_TABS = [
  'Diagram', 'Search', 'Lineage', 'Data', 'Statistics',
  'Model Spec', 'Advanced Modelling', 'M Code', 'DAX Lab', 'Change Plan',
  'AI Readiness', 'BPA',
  'Tests', 'Evidence',
  'Deploy', 'Permissions', 'Docs',
  'Primer', 'Workflows', 'Edit History',
];
const PROPGRID_SCENARIOS = ['model', 'measure', 'multi', 'column', 'formatexpr', 'lowcl', 'staledraft', 'error', 'empty'];
const CONNECTION_DRAWER_STATES = [
  { target: 'studio', variant: 'Diagram', drawer: 'open', out: join(__dir, 'shots', 'studio-connections.png') },
  { target: 'studio', variant: 'Diagram', drawer: 'work', out: join(__dir, 'shots', 'studio-connections-work-locally.png') },
];

function findBrowser() {
  if (process.env.SEMANTICUS_BROWSER && existsSync(process.env.SEMANTICUS_BROWSER)) return process.env.SEMANTICUS_BROWSER;
  const cacheRoot = join(homedir(), '.cache', 'puppeteer', 'chrome-headless-shell');
  if (existsSync(cacheRoot)) {
    for (const v of readdirSync(cacheRoot)) {
      const exe = join(cacheRoot, v, 'chrome-headless-shell-win64', 'chrome-headless-shell.exe');
      if (existsSync(exe)) return exe;
    }
  }
  for (const c of ['C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe',
                   'C:/Program Files/Google/Chrome/Application/chrome.exe']) if (existsSync(c)) return c;
  throw new Error('No Chromium found. Run `npm install` here (downloads chrome-headless-shell) or set SEMANTICUS_BROWSER.');
}

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
async function capture(browser, port, { target, variant, out, drawer }) {
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
      if (process.env.UISHOT_CONN) params.set('conn', '1');   // simulate a live-connected engine (Data/Statistics/Query populate)
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
      if (process.env.UISHOT_DEPLOY) params.set('deploy', process.env.UISHOT_DEPLOY);   // Deploy: rollback, promote, advanced or dataagent drives the requested decision surface
      if (process.env.UISHOT_PERM) params.set('perm', process.env.UISHOT_PERM);   // Permissions: 'free' → Free-tier read-only variant · 'off' → guardrail turned off (dimmed cells) · 'loading' → reads never answer · 'error' → reads fail (error + retry); default = Pro (editable)
      if (process.env.UISHOT_FR) params.set('fr', process.env.UISHOT_FR);   // Find & Replace: 'empty' -> a 0-item plan + note (the honest Replace-all empty state on Optimize)
      if (process.env.UISHOT_EXPLAIN) params.set('explain', process.env.UISHOT_EXPLAIN);   // Explain This Number: 'blank' → the why-is-this-blank dossier fixture
      if (process.env.UISHOT_SPEC) params.set('spec', process.env.UISHOT_SPEC);   // Spec inline editor: 'measureless' | 'measuregroup' | 'norel' edge-state fixtures
      if (process.env.UISHOT_STATE) params.set('state', process.env.UISHOT_STATE);   // seed persisted webview state (JSON), e.g. '{"input:lab.viz":"matrix"}'
      const q = params.toString() ? `?${params.toString()}` : '';
      const url = `http://127.0.0.1:${port}/tools/uishot/harness.html${q}#${encodeURIComponent(variant)}`;
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
      if (drawer) {
        await page.click('[data-testid="connections-editing"]');
        await page.waitForSelector('[role="dialog"][aria-label="Connections"]', { timeout: 15000 });
        await new Promise((r) => setTimeout(r, 700));
        if (drawer === 'work') {
          const clicked = await page.evaluate(() => {
            const button = [...document.querySelectorAll('button')].find((b) => (b.textContent || '').trim() === 'Work locally');
            if (button) button.click();
            return !!button;
          });
          if (!clicked) throw new Error('Connections inventory could not find the Work locally action');
          await page.waitForFunction(() => document.body.textContent?.includes('Work locally from'), { timeout: 15000 });
        }
      }
      if (variant.toLowerCase() === 'diagram') {
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
      await new Promise((r) => setTimeout(r, 1200));
      // ?pq waits for the unified M workspace's live sample, then opens a menu / toggles profile.
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
    console.log(`  ✓ ${target}/${drawer ? `Connections:${drawer}` : variant} -> ${out} (${statSync(out).size} b)`
      + (errors.length ? `  [${errors.length} page errors]` : '')
      + (expectedNotices.length ? `  [${expectedNotices.length} expected sandbox notices]` : ''));
    if (errors.length) errors.slice(0, 6).forEach((e) => console.log('      ' + e));
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
          ...CONNECTION_DRAWER_STATES]
         .map((j) => ({ ...j, out: j.out || defaultOut(j.target, j.variant) }));
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

mkdirSync(join(__dir, 'shots'), { recursive: true });
const server = await startServer();
const port = server.address().port;
const browser = await puppeteer.launch({
  executablePath: findBrowser(), headless: 'shell',
  args: ['--no-sandbox', '--disable-gpu', '--force-color-profile=srgb', '--hide-scrollbars'],
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
