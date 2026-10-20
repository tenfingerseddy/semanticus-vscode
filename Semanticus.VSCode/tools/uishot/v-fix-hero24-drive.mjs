// Verifier v-fix-hero24: the REAL Studio through the mock engine. D1: explainValue is held (a slow engine) and counted while the page
// re-renders, switches tabs and comes back. D2: the assistant's answer (studioAnswer) and a show_in_studio measure focus (studioShow) in
// both orders; the DAX card must keep the answer's filters and must not say the query was changed by hand.
//   OUTDIR=<dir> node tools/uishot/v-fix-hero24-drive.mjs
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
const OUT = process.env.OUTDIR || '/tmp/vfh-shots';
mkdirSync(OUT, { recursive: true });
const TYPES = { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.json': 'application/json', '.svg': 'image/svg+xml', '.woff2': 'font/woff2' };
const server = createServer((req, res) => {
  const p = normalize(join(webRoot, decodeURIComponent((req.url || '/').split('?')[0])));
  if (!p.startsWith(webRoot) || !existsSync(p) || statSync(p).isDirectory()) { res.writeHead(404); res.end(); return; }
  res.writeHead(200, { 'content-type': TYPES[extname(p)] || 'application/octet-stream' }); res.end(readFileSync(p));
});
await new Promise((r) => server.listen(0, '127.0.0.1', r));
const port = server.address().port;
const SCRATCH = resolve(process.env.HOME ?? '/tmp', '.cache', 'semanticus-uishot', `vfh-${process.pid}`);
mkdirSync(SCRATCH, { recursive: true });
const browser = await puppeteer.launch({ executablePath: process.env.SEMANTICUS_BROWSER || findBrowser(), headless: true,
  args: ['--no-sandbox', '--disable-gpu', '--disable-dev-shm-usage', `--user-data-dir=${SCRATCH}`], env: { ...process.env, TZ: 'UTC' } });
const wait = (ms) => new Promise((r) => setTimeout(r, ms));
const t = (id) => `[data-testid="${id}"]`;
const lines = [];
const fails = [];
const check = (ok, what) => { lines.push(`${ok ? 'ok  ' : 'FAIL'} ${what}`); if (!ok) fails.push(what); return ok; };
const note = (what) => lines.push(`note ${what}`);

async function open(q) {
  const page = await browser.newPage();
  const errors = [];
  page.on('pageerror', (e) => errors.push(e.message));
  await page.setViewport({ width: 1440, height: 1000, deviceScaleFactor: 1 });
  await page.goto(`http://127.0.0.1:${port}/tools/uishot/harness.html?conn=1&live=1&${q}`, { waitUntil: 'networkidle0', timeout: 60000 });
  await wait(2500);
  return { page, errors };
}
const count = (page, m) => page.evaluate((m) => (window.__uishotHarness?.messages || []).filter((x) => x && x.method === m).length, m);
const lastOf = (page, m) => page.evaluate((m) => (window.__uishotHarness?.messages || []).filter((x) => x && x.method === m).pop() || null, m);
const send = (page, data) => page.evaluate((d) => window.dispatchEvent(new MessageEvent('message', { data: d })), data);
const text = (page, sel) => page.evaluate((s) => (document.querySelector(s)?.textContent || '').replace(/\s+/g, ' ').trim(), sel);
const has = (page, sel) => page.evaluate((s) => !!document.querySelector(s), sel);
const askWords = async (page, words) => {
  if (await has(page, t('mode-words'))) { await page.click(t('mode-words')); await wait(200); }
  await page.$eval(`${t('queries-ask')} input`, (e) => { e.value = ''; });
  await page.click(`${t('queries-ask')} input`); await page.type(`${t('queries-ask')} input`, words);
  await page.click(t('query-ask')); await wait(2200);
};
const go = async (page, tab) => { await send(page, { type: 'navigate', tab }); await wait(1200); };
const daxText = (page) => page.evaluate(() => { const c = document.querySelector('[data-testid="dax-card"]'); return c ? (c.querySelector('.cm-content')?.textContent || c.textContent || '') : ''; });

try {
  // ---------------- D1: a one-number answer, a slow engine, a storm of renders ----------------
  {
    const { page, errors } = await open('tab=query&tier=pro');
    await page.evaluate(() => window.__uishotHarness.hold('explainValue'));
    await askWords(page, 'total sales');
    const head = await text(page, t('headline'));
    note(`D1 headline: "${head.slice(0, 80)}"`);
    await wait(1500);
    const c0 = await count(page, 'explainValue');
    check(c0 === 1, `D1 a one-number answer asks explain once by itself (${c0})`);
    // a storm of renders: resizes, theme-like class flips, typing in the box, scrolling
    for (let i = 0; i < 25; i++) {
      await page.setViewport({ width: 1100 + (i % 5) * 70, height: 900 + (i % 3) * 50, deviceScaleFactor: 1 });
      await page.evaluate(() => window.scrollBy(0, 40));
      await wait(60);
    }
    await page.click(`${t('queries-ask')} input`); await page.type(`${t('queries-ask')} input`, ' x'); await wait(800);
    const c1 = await count(page, 'explainValue');
    check(c1 === 1, `D1 25 re-renders while the engine is slow: still one call (${c1})`);
    await page.screenshot({ path: join(OUT, 'd1-held.png') });
    // the slow engine answers
    await page.evaluate(() => window.__uishotHarness.release('explainValue'));
    await wait(2500);
    const c2 = await count(page, 'explainValue');
    check(c2 === 1, `D1 the answer lands and no second call follows (${c2})`);
    check(!/Working it out/.test(await text(page, t('explain-card'))), 'D1 the Explain card is not stuck on "Working it out"');
    await page.screenshot({ path: join(OUT, 'd1-answered.png') });
    // idle 8 s more (past the 3 s window): a loop would show here
    await wait(8000);
    const c3 = await count(page, 'explainValue');
    check(c3 === 1, `D1 8 s idle after the answer: no further call (${c3})`);
    // tab switch away and straight back (inside 3 s)
    await go(page, 'plan'); await go(page, 'query');
    const c4 = await count(page, 'explainValue');
    note(`D1 after a quick tab switch away and back: ${c4} call(s) in total`);
    // leave for longer than the window, then come back
    await go(page, 'plan'); await wait(5000); await go(page, 'query'); await wait(2500);
    const c5 = await count(page, 'explainValue');
    note(`D1 after leaving 5 s and coming back: ${c5} call(s) in total`);
    await wait(8000);
    const c6 = await count(page, 'explainValue');
    check(c6 === c5, `D1 back on the page, 8 s idle: no loop (${c5} -> ${c6})`);
    check(errors.length === 0, `D1 no page errors (${errors.join(' | ').slice(0, 200)})`);
    await page.close();
  }

  // ---------------- D1: a failure, then a press ----------------
  {
    const { page } = await open('tab=query&tier=pro');
    await page.evaluate(() => window.__uishotHarness.setResponse('explainValue', { __uishotError: 'The connection dropped.' }));
    await askWords(page, 'total sales');
    await wait(1500);
    const f0 = await count(page, 'explainValue');
    const errShown = /connection dropped|went wrong/i.test(await text(page, t('explain-card')));
    check(f0 === 1 && errShown, `D1 failure: one call and the error shows (${f0}, shown ${errShown})`);
    await wait(5000);
    check(await count(page, 'explainValue') === f0, 'D1 failure: no automatic retry loop');
    // the engine is back; the person presses the same number again
    await page.evaluate(() => window.__uishotHarness.setResponse('explainValue', undefined));
    const cell = await page.$('[data-testid="queries-result"] .xe-gcell.sem-num, [data-testid="result"] .sem-num');
    if (cell) {
      await cell.evaluate((e) => e.scrollIntoView({ block: 'center' })); await cell.click(); await wait(600);
      const item = await page.evaluateHandle(() => [...document.querySelectorAll('button, [role="menuitem"]')].find((b) => /^Explain/.test(b.textContent.trim())) || null);
      if (item && (await item.evaluate((x) => !!x))) { await item.click(); await wait(1500); }
    }
    const f1 = await count(page, 'explainValue');
    note(`D1 failure: pressing the same number again (cell found ${!!cell}): ${f1 - f0} new call(s); card: "${(await text(page, t('explain-card'))).slice(0, 90)}"`);
    // the fold: close and open the card
    await page.click(t('fold-explain')); await wait(300); await page.click(t('fold-explain')); await wait(1500);
    const f2 = await count(page, 'explainValue');
    check(f2 === f1 + 1, `D1 failure: closing and opening the Explain card asks again (${f1} -> ${f2})`);
    await page.screenshot({ path: join(OUT, 'd1-failure-retry.png') });
    await page.close();
  }

  // ---------------- D2: assistant answer + show_in_studio measure focus ----------------
  const juneParts = { measures: ['Total Sales'], groupBy: [], filters: [{ column: "'Date'[Year]", op: 'is', values: ['2025'] }] };
  const twoParts = { measures: ['Total Sales'], groupBy: [], filters: [{ column: "'Date'[Year]", op: 'is', values: ['2025'] }, { column: "'Store'[Region]", op: 'is', values: ['North'] }] };
  async function scenario(name, { start, parts, parts2, order, measure = 'Total Sales', table = 'Sales' }) {
    const { page, errors } = await open(`tab=${start}&tier=pro`);
    await send(page, { type: 'driveMode', mode: 'claudeDrives', assistantConnected: true });
    await wait(300);
    const mk = async (p) => page.evaluate((p) => window.__askDataMock.answer(p), p);
    const a1 = await mk(parts);
    const answer = (id, a, q) => send(page, { type: 'studioAnswer', payload: { id, sessionId: 'uishot', at: new Date().toISOString(), question: q, answer: a } });
    const show = (id) => send(page, { type: 'studioShow', payload: { id, say: 'Here is the number.', how: 'Open Explore, then Query.', place: 'explore/ask', focus: `measure:${table}/${measure}`, at: new Date().toISOString(), by: 'assistant', auto: false, sessionId: 'uishot' } });
    if (order === 'answer-then-show') { await answer('sa-00000000001', a1, 'q1'); await wait(150); await show('sn-0000000show1'); }
    else if (order === 'show-then-answer') { await show('sn-0000000show1'); await wait(2500); await answer('sa-00000000001', a1, 'q1'); }
    else if (order === 'same-tick') { await page.evaluate((a) => { window.dispatchEvent(new MessageEvent('message', { data: { type: 'studioAnswer', payload: { id: 'sa-00000000001', sessionId: 'uishot', at: new Date().toISOString(), question: 'q1', answer: a } } }));
      window.dispatchEvent(new MessageEvent('message', { data: { type: 'studioShow', payload: { id: 'sn-0000000show1', say: 'Here is the number.', how: 'x', place: 'explore/ask', focus: 'measure:Sales/Total Sales', at: new Date().toISOString(), by: 'assistant', sessionId: 'uishot' } } })); }, a1); }
    await page.waitForSelector(t('query-page'), { timeout: 20000 }).catch(() => {});
    await wait(2500);
    if (parts2) { const a2 = await mk(parts2); await answer('sa-00000000002', a2, 'q2'); await wait(400); await show('sn-0000000show2'); await wait(2500); }
    const want = parts2 || parts;
    const dax = await daxText(page);
    const page_ = await text(page, t('query-page'));
    const filtersKept = want.filters.every((f) => dax.includes(f.values[0]));
    check(filtersKept, `D2 ${name}: the DAX card holds the answer's filters (${dax.replace(/\s+/g, ' ').slice(0, 140)})`);
    check(!/ROW\("Total Sales", \[Total Sales\]\)\s*$/.test(dax.trim()) || filtersKept, `D2 ${name}: no bare ROW seed`);
    check(!/changed the query by hand/.test(page_), `D2 ${name}: no "changed the query by hand"`);
    // Run on it: the runDax query must carry the filter
    const runs0 = await count(page, 'runDax');
    if (await has(page, t('dax-run'))) { await page.click(t('dax-run')); await wait(1500); }
    const run = await lastOf(page, 'runDax');
    if ((await count(page, 'runDax')) > runs0) check(want.filters.every((f) => JSON.stringify(run.params || '').includes(f.values[0])), `D2 ${name}: Run sends the filtered query`);
    else note(`D2 ${name}: Run not pressed (button missing or disabled)`);
    check(errors.length === 0, `D2 ${name}: no page errors (${errors.join(' | ').slice(0, 160)})`);
    await page.screenshot({ path: join(OUT, `d2-${name}.png`) });
    await page.close();
  }
  await scenario('june-on-query', { start: 'query', parts: juneParts, order: 'answer-then-show' });
  await scenario('june-from-plan', { start: 'plan', parts: juneParts, order: 'answer-then-show' });
  await scenario('june-same-tick-from-plan', { start: 'plan', parts: juneParts, order: 'same-tick' });
  await scenario('june-show-then-answer', { start: 'plan', parts: juneParts, order: 'show-then-answer' });
  await scenario('two-filters-from-plan', { start: 'plan', parts: twoParts, order: 'answer-then-show' });
  await scenario('asked-twice', { start: 'plan', parts: juneParts, parts2: twoParts, order: 'answer-then-show' });
} finally {
  writeFileSync(join(OUT, 'results.txt'), lines.join('\n') + '\n');
  console.log(lines.join('\n'));
  console.log(fails.length ? `FAILURES: ${fails.length}` : 'ALL OK');
  await browser.close();
  await new Promise((done) => server.close(done));
}
