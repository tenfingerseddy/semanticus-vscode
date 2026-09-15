// Browser-driven proof for the Checks > Tests redesign (cp/tests-page). It serves THIS worktree's built
// bundle, drives the real page with real clicks, and MEASURES what landed at both review widths.
//
// It is a measuring instrument, not a screenshot tool: every scene writes the rendered text of the tool
// row, the status line and each card into results.json beside its PNG, so a claim about the page can be
// checked against what the DOM actually holds instead of an eyeball.
//
//   OUTDIR=<dir> SEMANTICUS_BROWSER=/usr/bin/chromium node tools/uishot/cp-tests-drive.mjs
import { createServer } from 'node:http';
import { existsSync, mkdirSync, readFileSync, statSync, writeFileSync } from 'node:fs';
import { dirname, extname, join, normalize, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createRequire } from 'node:module';
import { findBrowser, requireSupportedNode } from './browser.mjs';
requireSupportedNode();
const require_ = createRequire(import.meta.url);
const puppeteer = require_('puppeteer-core').default ?? require_('puppeteer-core');

const here = dirname(fileURLToPath(import.meta.url));
const webRoot = resolve(here, '..', '..');
const OUT = process.env.OUTDIR || resolve(webRoot, 'shots', 'cp-tests');
mkdirSync(OUT, { recursive: true });

const TYPES = { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.json': 'application/json', '.png': 'image/png', '.svg': 'image/svg+xml', '.woff2': 'font/woff2', '.map': 'application/json' };
const server = createServer((req, res) => {
  const p = normalize(join(webRoot, decodeURIComponent((req.url || '/').split('?')[0].split('#')[0])));
  if (!p.startsWith(webRoot) || !existsSync(p) || statSync(p).isDirectory()) { res.writeHead(404); res.end('nf'); return; }
  res.writeHead(200, { 'content-type': TYPES[extname(p).toLowerCase()] || 'application/octet-stream' });
  res.end(readFileSync(p));
});
await new Promise((r) => server.listen(0, '127.0.0.1', r));
const port = server.address().port;
// Chromium's own scratch, on real disk rather than the shared tmpfs.
const SCRATCH = resolve(process.env.HOME ?? '/tmp', '.cache', 'semanticus-uishot', `run-${process.pid}`);
mkdirSync(SCRATCH, { recursive: true });
const browser = await puppeteer.launch({
  executablePath: process.env.SEMANTICUS_BROWSER || findBrowser(), headless: true, protocolTimeout: 180000,
  args: ['--no-sandbox', '--disable-gpu', '--disable-dev-shm-usage', '--force-color-profile=srgb', '--hide-scrollbars',
    // --disable-dev-shm-usage sends chromium's scratch to TMPDIR, and /tmp here is a tmpfs that other
    // work fills. At 89% full a screenshot fails as "Unable to capture screenshot", which reads exactly
    // like a component fault and is not one. Keep the scratch on real disk.
    `--user-data-dir=${SCRATCH}`],
});
const wait = (ms) => new Promise((r) => setTimeout(r, ms));

// Response overrides have to be in place before studio.js issues its first call, so they ride in on the
// setter for __uishotHarness (the harness assigns it once, before the bundle loads).
const seed = (pairs) => `
  let __h; Object.defineProperty(window, '__uishotHarness', { configurable: true,
    get: () => __h,
    set: (v) => { __h = v; ${pairs.map(([m, val]) => `v.setResponse(${JSON.stringify(m)}, ${JSON.stringify(val)});`).join(' ')} } });`;

// A part run: the engine grades what it RAN and names what its letter covers. No "Partial" anywhere.
const PART_RUN = {
  runId: 'run-part', when: '2026-09-15T11:42:00Z', live: true, persisted: false,
  environment: 'Contoso Retail on localhost:51542', durationMs: 4100, definitionCount: 5,
  health: { overall: 78, grade: 'C', gatedBy: [], coveragePct: 66, checked: 12, passed: 9, failed: 2, suspect: 0, notVerifiable: 1, missing: 0, rootFailures: 2, categories: [] },
  scope: { mode: 'sections', ran: ['relationships'], skipped: ['saved checks', 'table counts'], gradeCovers: 'relationships only' },
  relationships: { relationships: [], summary: { relationships: 0, checked: 0, passed: 0, failed: 0, suspect: 0, notVerifiable: 0, coveragePct: 0 } },
};

const MEASURE = `(() => {
  const text = (sel) => { const el = document.querySelector(sel); return el ? el.innerText.replace(/\\n+/g, ' | ').trim() : null; };
  const cards = [...document.querySelectorAll('main h2')].map((h) => h.textContent.trim());
  const rows = [...document.querySelectorAll('[role="button"][aria-expanded]')].map((r) => r.innerText.replace(/\\n+/g, ' | ').trim());
  const drawer = document.querySelector('aside');
  return {
    toolrow: text('.sem-tests-toolrow'),
    status: text('.sem-tests-status'),
    cards,
    rows: rows.slice(0, 8),
    drawer: drawer ? drawer.innerText.replace(/\\n+/g, ' | ').trim().slice(0, 1400) : null,
    openRow: (() => {
      const open = document.querySelector('[role="button"][aria-expanded="true"]');
      if (!open) return null;
      const parts = [];
      let node = open.nextElementSibling;
      while (node && !node.getAttribute('aria-expanded')) { parts.push(node.innerText); node = node.nextElementSibling; }
      return { title: open.innerText.split(String.fromCharCode(10))[0], detail: parts.join(' | ').replace(/\\n+/g, ' | ').trim().slice(0, 1200) };
    })(),
    // What the new scenes are ABOUT, read off the DOM rather than off a screenshot.
    hub: (() => { const h = document.querySelector('[role="dialog"]'); return h ? h.innerText.replace(/\\n+/g, ' | ').trim().slice(0, 500) : null; })(),
    drawerOpen: !!document.querySelector('aside'),
    sqlSourcePicked: (() => {
      const sel = document.querySelector('select[aria-label="SQL source"]');
      return sel ? (sel.options[sel.selectedIndex]?.text ?? '') : null;
    })(),
    sourceNote: (() => {
      const sel = document.querySelector('select[aria-label="SQL source"]');
      const field = sel?.closest('label');
      return field ? field.innerText.replace(/\\n+/g, ' | ').trim() : null;
    })(),
    menu: (() => { const m = document.querySelector('[role="menu"]'); return m ? m.innerText.replace(/\\n+/g, ' | ').trim() : null; })(),
    relationshipsCard: (() => {
      const h = [...document.querySelectorAll('main h2')].find((x) => x.textContent.trim() === 'Relationships');
      return h ? h.closest('div[class*="rounded"]')?.innerText.replace(/\\n+/g, ' | ').trim().slice(0, 900) : null;
    })(),
    tableCountsCard: (() => {
      const h = [...document.querySelectorAll('main h2')].find((x) => x.textContent.trim() === 'Table row counts');
      return h ? h.closest('div[class*="rounded"]')?.innerText.replace(/\\n+/g, ' | ').trim().slice(0, 900) : null;
    })(),
    historyCard: (() => {
      const h = [...document.querySelectorAll('main h2')].find((x) => x.textContent.trim() === 'History');
      return h ? h.closest('div[class*="rounded"]')?.innerText.replace(/\\n+/g, ' | ').trim().slice(0, 1400) : null;
    })(),
    tickBoxes: document.querySelectorAll('input[type="checkbox"][aria-label^="Tick "]').length,
    hasSecurityWord: /\\bSecurity\\b/.test(document.body.innerText),
    hasInterviewWord: /Model Interview/.test(document.body.innerText),
    hasPartialWord: /Partial/i.test(document.body.innerText),
    emDash: (document.body.innerText.match(/\\u2014/g) || []).length,
    docWidth: document.documentElement.scrollWidth,
    viewWidth: document.documentElement.clientWidth,
  };
})()`;

// SCENES=<comma list> drives only those scenes. The full 45 take a while and this machine is shared, so a
// change that touches one surface can be proved against that surface without a forty-minute run. A partial
// run is reported as partial: results.json records which scenes it held.
const ONLY = (process.env.SCENES || '').split(',').map((x) => x.trim()).filter(Boolean);
const results = [];
// A step is either a label to click, or one of:
//   { key: 'Escape' }              press a key on the window, which is where both surfaces listen
//   { type: 'label', text: '...' } type into the input whose label or placeholder starts with `label`
//   { select: 'aria-label', option: 'visible text' }  choose an option in a <select>
//   { tab: 'AI understanding' }    move to another Studio tab
//   { wait: 1200 }                 give a reload time to land
async function scene(name, { query = 'conn=1&live=1&tier=pro', overrides = [], steps = [], scroll = null, widths = [[1366, 768], [1440, 900]] }) {
  if (ONLY.length && !ONLY.includes(name)) return;
  const page = await browser.newPage();
  const errors = [];
  page.on('pageerror', (e) => errors.push('pageerror: ' + e.message));
  page.on('console', (m) => { if (m.type() === 'error') errors.push('console: ' + m.text()); });
  await page.setViewport({ width: 1366, height: 768, deviceScaleFactor: 1 });
  if (overrides.length) await page.evaluateOnNewDocument(seed(overrides));
  await page.goto(`http://127.0.0.1:${port}/tools/uishot/harness.html?${query}`, { waitUntil: 'networkidle0', timeout: 30000 });
  await page.waitForSelector('nav button', { timeout: 15000 });
  await wait(600);
  // Every scene starts on Checks > Tests.
  await page.evaluate(() => {
    const vis = (el) => el.getClientRects().length > 0;
    const area = [...document.querySelectorAll('nav[aria-label="Studio areas"] button')].filter(vis).find((b) => b.textContent.trim() === 'Checks');
    if (area) area.click();
  });
  await wait(900);
  const notes = [];
  for (const step of steps) {
    if (typeof step === 'object' && step.wait) { await wait(step.wait); continue; }
    if (typeof step === 'object' && step.key) {
      // Dispatched on the window, because that is where both the drawer and the hub listen. A key sent to
      // the focused element alone would not exercise the thing Astra found.
      await page.evaluate((key) => window.dispatchEvent(new KeyboardEvent('keydown', { key, bubbles: true })), step.key);
      await wait(700);
      continue;
    }
    if (typeof step === 'object' && step.tab) {
      const ok = await page.evaluate((label) => {
        const vis = (el) => el.getClientRects().length > 0;
        const el = [...document.querySelectorAll('nav button, [role="tab"]')].filter(vis)
          .find((b) => (b.textContent || '').trim() === label);
        if (el) el.click();
        return !!el;
      }, step.tab);
      if (!ok) notes.push(`tab "${step.tab}" not found`);
      await wait(1200);
      continue;
    }
    if (typeof step === 'object' && step.testid) {
      // The stable hook the component already carries. A label match is fine for one-of-a-kind buttons and
      // useless for "Save" beside "Save and test connection".
      const ok = await page.evaluate((s) => {
        const el = document.querySelector(`[data-testid="${s.testid}"]`);
        if (!el) return false;
        if (s.type == null) { el.click(); return true; }
        const setter = Object.getOwnPropertyDescriptor(el.constructor.prototype, 'value').set;
        setter.call(el, s.type);
        el.dispatchEvent(new Event('input', { bubbles: true }));
        return true;
      }, step);
      if (!ok) notes.push(`testid "${step.testid}" not found`);
      await wait(step.type == null ? 900 : 300);
      continue;
    }
    if (typeof step === 'object' && step.type != null) {
      const ok = await page.evaluate((s) => {
        const vis = (el) => el.getClientRects().length > 0;
        const fields = [...document.querySelectorAll('input, textarea')].filter(vis);
        const el = fields.find((f) => {
          const label = f.closest('label')?.innerText ?? '';
          return label.trim().startsWith(s.label) || (f.getAttribute('placeholder') || '').startsWith(s.label)
            || (f.getAttribute('aria-label') || '').startsWith(s.label);
        });
        if (!el) return false;
        const setter = Object.getOwnPropertyDescriptor(el.constructor.prototype, 'value').set;
        setter.call(el, s.type);
        el.dispatchEvent(new Event('input', { bubbles: true }));
        return true;
      }, { label: step.label ?? step.into, type: step.type });
      if (!ok) notes.push(`field "${step.label ?? step.into}" not found`);
      await wait(400);
      continue;
    }
    if (typeof step === 'object' && step.select) {
      const ok = await page.evaluate((s) => {
        const vis = (el) => el.getClientRects().length > 0;
        const el = [...document.querySelectorAll('select')].filter(vis)
          .find((f) => (f.getAttribute('aria-label') || f.closest('label')?.innerText || '').trim().startsWith(s.select));
        if (!el) return false;
        const option = [...el.options].find((o) => o.text.trim().startsWith(s.option));
        if (!option) return false;
        const setter = Object.getOwnPropertyDescriptor(el.constructor.prototype, 'value').set;
        setter.call(el, option.value);
        el.dispatchEvent(new Event('change', { bubbles: true }));
        return true;
      }, step);
      if (!ok) notes.push(`option "${step.option}" in "${step.select}" not found`);
      await wait(900);
      continue;
    }
    const ok = await page.evaluate((label) => {
      const vis = (el) => el.getClientRects().length > 0;
      const el = [...document.querySelectorAll('button, [role="button"], [role="menuitem"], [role="radio"], input[type="checkbox"]')]
        .filter(vis).find((b) => (b.getAttribute('aria-label') || b.textContent || '').trim().startsWith(label));
      if (el) { el.click(); return true; }
      // A native radio carries its words on the label around it, not on itself.
      const radio = [...document.querySelectorAll('input[type="radio"]')].filter(vis)
        .find((r) => (r.closest('label')?.innerText ?? '').trim().startsWith(label));
      if (radio) { radio.click(); return true; }
      return false;
    }, step);
    if (!ok) notes.push(`control "${step}" not found`);
    await wait(900);
  }
  const shots = {};
  const measured = {};
  for (const [w, h] of widths) {
    await page.setViewport({ width: w, height: h, deviceScaleFactor: 2 });
    await wait(600);
    measured[`${w}`] = await page.evaluate(MEASURE);
    const file = join(OUT, `${name}-${w}.png`);
    await page.screenshot({ path: file, fullPage: false });
    shots[`${w}`] = file;
    if (scroll) {
      await page.evaluate((sel) => {
        if (sel === 'bottom') {
          // Every scroller on the page, because Studio nests them and guessing one was wrong.
          for (const el of [document.scrollingElement, ...document.querySelectorAll('*')]) {
            if (el && el.scrollHeight > el.clientHeight + 4) el.scrollTop = el.scrollHeight;
          }
          return;
        }
        const el = [...document.querySelectorAll('main h2')].find((h) => h.textContent.trim() === sel);
        if (el) el.scrollIntoView({ block: 'center' });
      }, scroll);
      await wait(400);
      const scrolled = join(OUT, `${name}-${w}-scrolled.png`);
      await page.screenshot({ path: scrolled, fullPage: false });
      shots[`${w}-scrolled`] = scrolled;
    }
  }
  results.push({ scene: name, notes, errors, shots, measured });
  await page.close();
}

// 1. The page at rest, after a full run.
await scene('at-rest', { steps: ['Run all enabled checks'], scroll: 'Table row counts' });
// 2. A row opened: the one sentence, the four facts, and the actions.
await scene('row-open', { steps: ['Run all enabled checks', 'Net Revenue ties to finance extract'] });
// 2b. The trusted-answer row opened: no SQL column anywhere in it.
await scene('row-open-trusted', { steps: ['Run all enabled checks', 'Total Sales 2024 totals 1,234'] });
// 3. The New check drawer, one scene per kind. The first click opens the MENU; the second picks a kind.
await scene('drawer-trusted', { steps: ['New check', 'Trusted answer'] });
await scene('drawer-compare', { steps: ['New check', 'Compare with source'] });
await scene('drawer-tablecount', { steps: ['New check', 'Table row count'] });
// 2c. The relationship detail, with the values that do not match read from the engine.
await scene('relationship-detail', { steps: ['Run all enabled checks', 'Show details', 'Show the values that do not match'], scroll: 'Relationships' });
// 4. The scope menu, which has to list exactly what "all" includes.
await scene('scope-menu', { steps: ['What to run'] });
// 5. First visit with nothing saved.
await scene('empty', { overrides: [['listTests', { definitions: [], unreadableLines: 0 }]] });
// 6. No live test model.
await scene('disconnected', { query: 'tier=pro' });
// 7. A part run: a real letter, what it covers, and what did not run.
await scene('part-run', { overrides: [['runTests', PART_RUN]], steps: ['Run all enabled checks'] });


// ===================================================================================================
// Astra's rejection, 2026-09-15. One scene per finding, plus the states she could not screenshot.
// Single width (1366x768) unless the finding is about layout.
// ===================================================================================================
const W1 = { widths: [[1366, 768]] };

// --- 1 (P1). Opening an existing check must preserve its SQL source. ---------------------------------
// (a) A check saved before named sources existed keeps its own endpoint and says so.
await scene('fix1-inline-source-kept', {
  query: 'conn=1&live=1&tier=pro&checksrc=inline',
  steps: ['Run all enabled checks', 'More for Net Revenue ties to finance extract', 'Edit'], ...W1,
});
// (b) A check whose named source has gone asks for a replacement instead of taking the one that is left.
await scene('fix1-missing-source-refused', {
  query: 'conn=1&live=1&tier=pro&checksrc=missing',
  steps: ['Run all enabled checks', 'More for Net Revenue ties to finance extract', 'Edit', 'Save check'], ...W1,
});

// --- 2 (P1) + 12. The whole hub-save-return journey, click by click, with a shot at every step. -------
const HUB_JOURNEY = [
  'New check', 'Compare with source',                                    // the drawer, on the compare kind
  { select: 'SQL source', option: 'Add a SQL source' },                  // the hub opens OVER it
];
await scene('journey-1-drawer-open', { steps: ['New check', 'Compare with source'], ...W1 });
await scene('journey-2-hub-over-drawer', { steps: HUB_JOURNEY, ...W1 });
await scene('journey-3-escape-closes-only-the-hub', { steps: [...HUB_JOURNEY, { key: 'Escape' }], ...W1 });
const HUB_SAVE = [
  ...HUB_JOURNEY, { testid: 'hub-sql-add' },
  { testid: 'hub-sql-name', type: 'Month end extract' },
  { testid: 'hub-sql-server', type: 'finance-sql.database.windows.net' },
  { testid: 'hub-sql-database', type: 'MonthEnd' },
  { testid: 'hub-sql-save' }, { wait: 1200 },
];
await scene('journey-4-source-saved-in-the-hub', { steps: HUB_SAVE, ...W1 });
await scene('journey-5-drawer-back-with-the-new-source', { steps: [...HUB_SAVE, { key: 'Escape' }], ...W1 });
// The check is finished and saved with the source that was just added, then reopened to prove the binding
// survived the round trip. That is the whole of Astra's "a complete hub-save-return click journey remains
// unverified".
const CHECK_FILLED = [
  ...HUB_SAVE, { key: 'Escape' },
  { select: 'Which calculation', option: 'Total Sales' },
  { label: 'The SQL you accept', type: 'SELECT SUM(Amount) AS total FROM fact.Sales' },
  'Zero',
];
await scene('journey-6-check-filled-in', { steps: CHECK_FILLED, ...W1 });
await scene('journey-7-check-saved', { steps: [...CHECK_FILLED, 'Save check', { wait: 1200 }], ...W1 });
await scene('journey-8-reopened-keeps-the-source', {
  steps: [...CHECK_FILLED, 'Save check', { wait: 1200 },
    'More for Total Sales ties to source SQL', 'Edit', { wait: 900 }], ...W1,
});

// --- 3. "Only this" names the family it runs. --------------------------------------------------------
await scene('fix3-scope-menu-names-each-family', { steps: ['Run all enabled checks', 'What to run'], ...W1 });
await scene('fix3-table-counts-only', {
  steps: ['Run all enabled checks', 'What to run', 'Run table row counts only'], scroll: 'Table row counts', ...W1,
});

// --- 4. A partial run keeps the automatic families' last result and date. ----------------------------
await scene('fix4-partial-keeps-relationships', {
  steps: ['Run all enabled checks', 'What to run', 'Run enabled saved checks only'], scroll: 'Relationships', ...W1,
});

// --- 5 (P1 live). Unknown relationships are counted separately and cannot produce Pass. ---------------
await scene('fix5-relationship-tally', { steps: ['Run all enabled checks'], scroll: 'Relationships', ...W1 });

// --- 6. History opens the evidence the run kept. ------------------------------------------------------
await scene('fix6-recorded-run-evidence', {
  steps: ['Run all enabled checks', 'Open the run recorded on 7/11/2026',
    'Open what Net Revenue ties to finance extract found'],
  scroll: 'bottom', ...W1,
});
// The opened run scored the old way, which Astra had no screenshot of.
// r1 is the run recorded before access rules left Tests; only that one carries the old-grade flag.
await scene('fix6-old-grade-label', {
  steps: ['Run all enabled checks', 'Open the run recorded on 7/4/2026'], scroll: 'bottom', ...W1,
});

// --- 7. Remove mapping, from the drawer. --------------------------------------------------------------
await scene('fix7-remove-mapping', {
  steps: ['Run all enabled checks', 'Change source'], scroll: 'Table row counts', ...W1,
});

// --- 8 + 10. The could-not-check reason, and the tolerance sentence. ----------------------------------
await scene('fix8-unknown-row-reason', { steps: ['Run all enabled checks'], ...W1 });
await scene('fix10-tolerance-sentence', {
  steps: ['Run all enabled checks', 'Total Sales 2024 totals 1,234'], ...W1,
});

// --- 9. The grade cap reads as a sentence. ------------------------------------------------------------
await scene('fix9-grade-cap-sentence', { steps: ['Run all enabled checks'], ...W1 });

// --- 11. Saved interview questions have a home again. ------------------------------------------------
await scene('fix11-interview-under-ai-understanding', { steps: [{ tab: 'AI understanding' }, { wait: 1500 }], ...W1 });

// --- 13. The run menu never offers a choice nobody can make. ------------------------------------------
await scene('fix13-menu-with-no-saved-checks', {
  overrides: [['listTests', { definitions: [], unreadableLines: 0 }]], steps: ['What to run'], ...W1,
});
await scene('fix13-menu-with-one-ticked', {
  steps: ['Run all enabled checks', 'Tick Total Sales ties to the GL', 'What to run'], ...W1,
});

// --- The states Astra listed as "no supplied screenshot". ---------------------------------------------
await scene('state-turn-off-failed', {
  query: 'conn=1&live=1&tier=pro&testfail=turnoff',
  steps: ['Run all enabled checks', 'More for Total Sales ties to the GL', 'Turn off'], ...W1,
});
await scene('state-delete-failed', {
  query: 'conn=1&live=1&tier=pro&testfail=delete',
  steps: ['Run all enabled checks', 'More for Total Sales ties to the GL', 'Delete', 'Delete'], ...W1,
});
await scene('state-history-after-record-it', {
  steps: ['Run all enabled checks', 'Record it', { wait: 1200 }], scroll: 'History', ...W1,
});
await scene('state-filter-transition', {
  steps: ['Run all enabled checks', 'Could not check'], ...W1,
});
await scene('state-connect-inside-an-expanded-row', {
  query: 'tier=pro', steps: ['Total Sales ties to the GL'], ...W1,
});


// ===================================================================================================
// ROUND TWO (Astra, 2026-09-15, second pass). One scene per finding.
// ===================================================================================================

// F1 (P1). Try it now must refuse exactly where Save refuses. Astra's screen showed Try CLEARING the Save
// refusal and running on; the payload it sent carried the old inline address with the named source gone.
await scene('r2-f1-try-refused-on-missing-source', {
  query: 'conn=1&live=1&tier=pro&checksrc=missing',
  steps: ['Run all enabled checks', 'More for Net Revenue ties to finance extract', 'Edit',
    'Save check', 'Try it now'], ...W1,
});
// And the same drawer once a replacement is picked: both buttons stop refusing.
await scene('r2-f1-try-allowed-after-replacement', {
  query: 'conn=1&live=1&tier=pro&checksrc=missing',
  steps: ['Run all enabled checks', 'More for Net Revenue ties to finance extract', 'Edit',
    { select: 'SQL source', option: 'Contoso warehouse' }, 'Try it now'], ...W1,
});

// F2. Full run, then a saved-check-only run, then the menu: "Run relationships only" must still be there
// with the count it last measured, and the drawer's table-count sentence must say when it runs.
await scene('r2-f2-menu-before-partial', {
  steps: ['Run all enabled checks', 'What to run'], ...W1,
});
await scene('r2-f2-menu-after-saved-checks-only', {
  steps: ['Run all enabled checks', 'What to run', 'Run enabled saved checks only', 'What to run'], ...W1,
});
await scene('r2-f2-table-count-drawer-copy', {
  steps: ['Run all enabled checks', 'Change source'], scroll: 'Table row counts', ...W1,
});

// F3. The recorded tolerance, read back in History. The fixture's check is set to 0.000001234567.
await scene('r2-f3-recorded-tolerance', {
  steps: ['Run all enabled checks', 'Open the run recorded on 7/11/2026',
    'Open what Net Revenue ties to finance extract found'],
  scroll: 'bottom', ...W1,
});

await browser.close();
server.close();
writeFileSync(join(OUT, 'results.json'), JSON.stringify(results, null, 2));
console.log(JSON.stringify(results.map((r) => ({ scene: r.scene, notes: r.notes, errors: r.errors })), null, 2));
