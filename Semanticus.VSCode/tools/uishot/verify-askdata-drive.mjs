// verify: three Ask your data defects the verifier found (26 September 2026) that only a real click path shows, driven
// through the real Studio bundle and harness.html's mock engine, the same way tools/uishot/askdata-drive.mjs drives
// the page. Each scene FAILS on the combined m11/jev-query 2a3fa67c + m11/askdata-page 154208a3 build on
// m11/integration 568435d3 and is left failing on purpose (exit 1). Both were first seen live on Northstar Sales
// Working in UAT lane h.
//
//   1. SAVE AS A CHECK TITLES A ROW CHECK AS A TOTAL. checkDraftOf writes "Units for Category Outdoor is 125049", and
//      the New check drawer drops it for titleFor(measure, value): the saved check reads "Units totals 125049" in
//      Validation and in run_tests, though it checks one row with the question's filters. Live: "Units totals 125049"
//      (Category = Outdoor) and "Units totals 509925" (Channel = Store, Channel is not Online, Year from 2024).
//   2. A MODEL SWITCH CARRIES THE LAST MODEL'S QUESTION. The page keeps its question per model (usePersistedState keyed
//      by session id), but the hook reads the key once: when the open model changes while the page is open, the old
//      question stays on screen and is written under the NEW model's key. Live: after create_model "Contoso Scratch",
//      the page showed Northstar's "Units by OrderNumber and Category" with Run.
//   3. THE SPLIT PICKER OFFERS A CALCULATION GROUP'S COLUMNS, which the engine refuses every time. Live: "Time
//      Calculation (Time Intelligence)" offered, picked, and refused.
//
//   Round 2 added scenes 4 and 5; round 3 (verify-askdata-r3) adds scenes 6 and 7, each left failing on purpose.
//
//   OUTDIR=<dir> SEMANTICUS_BROWSER=/usr/bin/chromium node tools/uishot/verify-askdata-drive.mjs
import { createServer } from 'node:http';
import { existsSync, mkdirSync, readFileSync, statSync, writeFileSync } from 'node:fs';
import { extname, join, normalize, resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createRequire } from 'node:module';
import { findBrowser, requireSupportedNode } from './browser.mjs';
requireSupportedNode();
const puppeteer = createRequire(import.meta.url)('puppeteer-core');

const here = dirname(fileURLToPath(import.meta.url));
const webRoot = resolve(here, '..', '..');
const OUT = process.env.OUTDIR || resolve(webRoot, 'shots', 'verify-askdata');
mkdirSync(OUT, { recursive: true });
const TYPES = { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.json': 'application/json', '.svg': 'image/svg+xml', '.woff2': 'font/woff2' };
const server = createServer((req, res) => {
  const p = normalize(join(webRoot, decodeURIComponent((req.url || '/').split('?')[0])));
  if (!p.startsWith(webRoot) || !existsSync(p) || statSync(p).isDirectory()) { res.writeHead(404); res.end(); return; }
  res.writeHead(200, { 'content-type': TYPES[extname(p)] || 'application/octet-stream' }); res.end(readFileSync(p));
});
await new Promise((r) => server.listen(0, '127.0.0.1', r));
const port = server.address().port;
const SCRATCH = resolve(process.env.HOME ?? '/tmp', '.cache', 'semanticus-uishot', `verify-askdata-${process.pid}`);
mkdirSync(SCRATCH, { recursive: true });
const browser = await puppeteer.launch({ executablePath: process.env.SEMANTICUS_BROWSER || findBrowser(), headless: true,
  args: ['--no-sandbox', '--disable-gpu', '--disable-dev-shm-usage', '--force-color-profile=srgb', '--hide-scrollbars', `--user-data-dir=${SCRATCH}`],
  env: { ...process.env, TZ: 'UTC' } });
const wait = (ms) => new Promise((r) => setTimeout(r, ms));
const failures = [];
const results = [];
const expect = (ok, what) => { results.push({ ok: !!ok, what }); if (!ok) failures.push(what); return ok; };

async function open(q) {
  const page = await browser.newPage();
  await page.setViewport({ width: 1440, height: 900, deviceScaleFactor: 1 });
  await page.goto(`http://127.0.0.1:${port}/tools/uishot/harness.html?tab=askdata&${q}`, { waitUntil: 'networkidle0', timeout: 45000 });
  await wait(2800);
  return page;
}
const clickText = (page, label, scope = 'body') => page.evaluate((l, s) => {
  const b = [...document.querySelectorAll(`${s} button`)].find((x) => (x.getAttribute('aria-label') || x.textContent || '').replace(/\s+/g, ' ').trim().startsWith(l));
  if (b) b.click(); return !!b;
}, label, scope);
const chip = (page, kind, i = 0) => page.evaluate((k, n) => { const b = document.querySelectorAll(`[data-chip="${k}"]`)[n]; if (b) b.click(); return !!b; }, kind, i);
const menuPick = async (page, label) => { await wait(200); return clickText(page, label, '[data-testid="askdata-menu"]'); };
const settle = () => wait(900);
const chipLabels = (page) => page.evaluate(() => [...document.querySelectorAll('[data-chip]')].map((b) => (b.textContent || '').replace(/\s+/g, ' ').trim()));

// Total Sales by Region, Year = 2025, from the empty page, the way askdata-drive.mjs builds it.
async function build(page) {
  await chip(page, 'add'); await menuPick(page, 'Total Sales'); await settle();
  await chip(page, 'add'); await menuPick(page, 'A column to split by'); await wait(250);
  await menuPick(page, 'Region'); await settle();
  await chip(page, 'add'); await menuPick(page, 'A filter'); await wait(250);
  await menuPick(page, 'Year'); await wait(250);
  await page.focus('[data-testid="askdata-menu"] input[aria-label="Values to keep"]');
  await page.keyboard.type('2025');
  await page.keyboard.press('Enter'); await settle();
}

try {
  // 1. Save as a check keeps the page's title for a row check.
  {
    const page = await open('tier=pro');
    await build(page);
    expect(await page.$('[data-outcome="answered"]'), 'scene 1: the question is answered');
    await clickText(page, 'Save as a check'); await wait(400);
    await clickText(page, 'Continue', '[data-testid="askdata-save"]'); await wait(1200);
    const saved = await clickText(page, 'Save check'); await wait(1200);
    expect(saved, 'scene 1: the drawer has a Save check button');
    const def = await page.evaluate(() => {
      const m = (window.__uishotHarness?.messages || []).filter((x) => x && x.method === 'saveTest').pop();
      return m ? (m.params || [])[0] : null;
    });
    await page.screenshot({ path: join(OUT, '01-saved-check.png') });
    expect(def, 'scene 1: saveTest was called');
    const title = def?.title ?? '';
    const params = (() => { try { return JSON.parse(def?.paramsJson || '{}'); } catch { return {}; } })();
    expect(/TREATAS/.test(params.expressionDax || ''), `scene 1: the saved check asks one row (expression ${params.expressionDax})`);
    expect(!/\btotals\b/i.test(title),
      `scene 1: a check of one row with the question's filters is saved as "${title}", which claims a total`);
    await page.close();
  }
  // 2. A model switch while the page is open does not carry the old model's question.
  {
    const page = await open('tier=pro');
    await build(page);
    const before = await chipLabels(page);
    expect(before.some((l) => l.startsWith('Total Sales')), `scene 2: the first model's question is built (${before.join(' | ')})`);
    // The open model changes the way an assistant's create_model changes it: the engine's model/didChange reaches
    // the Studio as 'didChange', ConnectionProvider re-reads sessionInfo (debounced), and no 'reconnected' is sent,
    // so nothing resets the route and the page stays mounted (live, lane h).
    await page.evaluate(() => {
      const h = window.__uishotHarness;
      h.setResponse('sessionInfo', { sessionId: 'verify-other-model', modelName: 'Contoso Other', revision: 1, tables: 1, measures: 0, hasUnsavedChanges: true });
      h.dispatch({ type: 'didChange', payload: { sessionId: 'verify-other-model', revision: 1, origin: 'agent', changes: [] } });
    });
    await wait(2500);
    const on = !!(await page.$('[data-testid="askdata-page"]'));
    const after = await chipLabels(page);
    await page.screenshot({ path: join(OUT, '02-after-model-switch.png') });
    expect(!(on && after.some((l) => l.startsWith('Total Sales'))),
      `scene 2: the open model is now "Contoso Other" and Ask your data still shows the last model's question (${after.join(' | ')})`);
    await page.close();
  }
  // 3. The split picker offers no column the engine will always refuse. askdata.tsx leaves dates and true or false
  // columns out of the FILTER picker because "a chip it will always refuse is a trap", but the SPLIT picker lists a
  // calculation group's columns, which the engine refuses every time ("... is a calculation group, and a question
  // cannot use its column yet"). Live in lane h: "Time Calculation (Time Intelligence)" was offered, picked, refused,
  // and left one more "A question that was not run" row. The harness's calculation group is UAT Detail.
  {
    const page = await open('tier=pro');
    await chip(page, 'add'); await menuPick(page, 'Total Sales'); await settle();
    await chip(page, 'add'); await menuPick(page, 'A column to split by'); await wait(300);
    const offered = await page.evaluate(() => [...document.querySelectorAll('[data-testid="askdata-menu"] button')].map((b) => (b.textContent || '').trim()));
    await page.screenshot({ path: join(OUT, '03-split-picker.png') });
    const calc = offered.filter((l) => /\(UAT Detail\)$/.test(l));
    expect(offered.length > 3, `scene 3: the split picker lists columns (${offered.length})`);
    expect(calc.length === 0, `scene 3: the split picker offers calculation group columns the engine always refuses (${calc.join(', ')})`);
    await page.close();
  }
  // 4. ROUND 2 (verify-askdata-r2, 26 Sep, m11/askdata 74c214b5): FAILS there, left failing on purpose. The defect 3 fix
  // keeps the handed title only while nothing changes. The drawer's field is "The number you trust", so a person types
  // the number they trust; the moment it differs, the title falls back to titleFor ("<measure> totals <number>"),
  // which ignores the row's filters, while the check still asks the one row (Test this expression, CALCULATE with the
  // question's TREATAS filters). The saved check then claims a total it never checks, the defect 3 symptom again.
  {
    const page = await open('tier=pro');
    await build(page);
    await clickText(page, 'Save as a check'); await wait(400);
    await clickText(page, 'Continue', '[data-testid="askdata-save"]'); await wait(1200);
    const typed = await page.evaluate(() => {
      const label = [...document.querySelectorAll('label')].find((l) => (l.textContent || '').trim().startsWith('The number you trust'));
      const input = label && label.querySelector('input');
      if (!input) return null;
      const set = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set;
      set.call(input, '1356'); input.dispatchEvent(new Event('input', { bubbles: true }));
      return input.value;
    });
    expect(typed === '1356', 'scene 4: the number you trust takes a typed value');
    await wait(300);
    await clickText(page, 'Save check'); await wait(1200);
    const def = await page.evaluate(() => {
      const m = (window.__uishotHarness?.messages || []).filter((x) => x && x.method === 'saveTest').pop();
      return m ? (m.params || [])[0] : null;
    });
    await page.screenshot({ path: join(OUT, '04-saved-check-number-typed.png') });
    const title = def?.title ?? '';
    const params = (() => { try { return JSON.parse(def?.paramsJson || '{}'); } catch { return {}; } })();
    expect(def && /TREATAS/.test(params.expressionDax || ''), `scene 4: the saved check still asks one row (expression ${params.expressionDax})`);
    expect(!/\btotals\b/i.test(title),
      `scene 4: the same one-row check with the number typed as 1356 is saved as "${title}", which claims a total`);
    await page.close();
  }
  // 5. ROUND 2 (verify-askdata-r2): FAILS on 74c214b5, left failing on purpose. The builder's own open finding: a
  // finder that matches nothing shows nothing at all (the menu is just "Split by" over the typed words), so a person
  // cannot tell a typo from a column the model does not have. Every other empty state on the page says what happened.
  {
    const page = await open('tier=pro');
    await chip(page, 'add'); await menuPick(page, 'Total Sales'); await settle();
    await chip(page, 'add'); await menuPick(page, 'A column to split by'); await wait(300);
    await page.keyboard.type('zzqx'); await wait(400);
    const menuText = await page.evaluate(() => (document.querySelector('[data-testid="askdata-menu"]')?.innerText || '').replace(/\s+/g, ' ').trim());
    await page.screenshot({ path: join(OUT, '05-finder-no-match.png') });
    expect(/\b(no|nothing|none)\b/i.test(menuText.replace(/^Split by\s*/, '')),
      `scene 5: the split finder matches nothing for "zzqx" and says nothing (the menu reads "${menuText}")`);
    await page.close();
  }
  // ROUND 3 (verify-askdata-r3, 27 Sep, m11/askdata fa4fb162): scenes 6 and 7 FAIL there and are left failing on
  // purpose. The R2 fix keeps the handed title while only the number changes, and hands every other change to
  // titleFor ("<measure> totals <number>"), which reads neither the filter nor the expression. So the false total
  // comes back through the drawer's other two fields: an "Only where" filter added to the handed one-row check
  // (scene 6), or the row edited inside its expression (scene 7). Both checks still ask one row.
  const setInput = (page, placeholder, value) => page.evaluate((ph, v) => {
    const input = [...document.querySelectorAll('input')].find((i) => i.getAttribute('placeholder') === ph);
    if (!input) return null;
    const set = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set;
    set.call(input, v); input.dispatchEvent(new Event('input', { bubbles: true }));
    return input.value;
  }, placeholder, value);
  const savedDef = (page) => page.evaluate(() => {
    const m = (window.__uishotHarness?.messages || []).filter((x) => x && x.method === 'saveTest').pop();
    return m ? (m.params || [])[0] : null;
  });
  {
    const page = await open('tier=pro');
    await build(page);
    await clickText(page, 'Save as a check'); await wait(400);
    await clickText(page, 'Continue', '[data-testid="askdata-save"]'); await wait(1200);
    expect(await setInput(page, 'Column', 'Channel') === 'Channel', 'scene 6: the Only where column takes a value');
    expect(await setInput(page, 'Value', 'Online') === 'Online', 'scene 6: the Only where value takes a value');
    await wait(300);
    await clickText(page, 'Save check'); await wait(1200);
    const def = await savedDef(page);
    await page.screenshot({ path: join(OUT, '06-saved-check-only-where.png') });
    const title = def?.title ?? '';
    const params = (() => { try { return JSON.parse(def?.paramsJson || '{}'); } catch { return {}; } })();
    expect(def && /TREATAS/.test(params.expressionDax || ''), `scene 6: the saved check still asks one row (expression ${params.expressionDax})`);
    expect(!/\btotals\b/i.test(title),
      `scene 6: the one-row check with Only where Channel = Online added is saved as "${title}", which claims a total`);
    await page.close();
  }
  {
    const page = await open('tier=pro');
    await build(page);
    await clickText(page, 'Save as a check'); await wait(400);
    await clickText(page, 'Continue', '[data-testid="askdata-save"]'); await wait(1200);
    const edited = await page.evaluate(() => {
      const ta = [...document.querySelectorAll('textarea')].find((t) => /TREATAS/.test(t.value));
      if (!ta) return null;
      const set = Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype, 'value').set;
      set.call(ta, ta.value.replace(/"[A-Za-z]+"/, '"North"')); ta.dispatchEvent(new Event('input', { bubbles: true }));
      return ta.value;
    });
    expect(edited, 'scene 7: the handed expression can be edited');
    await wait(300);
    await clickText(page, 'Save check'); await wait(1200);
    const def = await savedDef(page);
    await page.screenshot({ path: join(OUT, '07-saved-check-expression-edited.png') });
    const title = def?.title ?? '';
    const params = (() => { try { return JSON.parse(def?.paramsJson || '{}'); } catch { return {}; } })();
    expect(def && /TREATAS/.test(params.expressionDax || ''), `scene 7: the saved check still asks one row (expression ${params.expressionDax})`);
    expect(!/\btotals\b/i.test(title),
      `scene 7: the one-row check with its row edited from Coast to North is saved as "${title}", which claims a total`);
    await page.close();
  }
} finally {
  await browser.close(); server.close();
}
writeFileSync(join(OUT, 'results.json'), JSON.stringify({ results, failures }, null, 2));
for (const r of results) console.log(`  [${r.ok ? 'PASS' : 'FAIL'}] ${r.what}`);
if (failures.length) { console.error(`verify-askdata-drive: ${failures.length} failed`); process.exit(1); }
console.log('verify-askdata-drive: every scene held');
