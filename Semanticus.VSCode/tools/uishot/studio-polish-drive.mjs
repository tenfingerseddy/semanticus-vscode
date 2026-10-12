import assert from 'node:assert/strict';
import { createServer } from 'node:http';
import { readFileSync, existsSync, mkdirSync, statSync } from 'node:fs';
import { dirname, extname, join, normalize, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import puppeteer from 'puppeteer-core';
import { findBrowser, requireSupportedNode } from './browser.mjs';
import { makeStartPlanState, startPlanView } from './fixtures/startplan-fixture.mjs';

requireSupportedNode();
const root = resolve(dirname(fileURLToPath(import.meta.url)), '../..');
const out = process.env.OUTDIR || '/tmp/semanticus-joint-review/m14/studio-polish/shots';
mkdirSync(out, { recursive: true });
const types = { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.woff2': 'font/woff2', '.svg': 'image/svg+xml', '.json': 'application/json' };
const server = createServer((req, res) => {
  const f = normalize(join(root, decodeURIComponent(req.url.split('?')[0])));
  if (!f.startsWith(root + '/') || !existsSync(f) || statSync(f).isDirectory()) { res.writeHead(404); res.end(); return; }
  res.writeHead(200, { 'content-type': types[extname(f)] || 'application/octet-stream' }); res.end(readFileSync(f));
});
await new Promise((r) => server.listen(0, '127.0.0.1', r));
const browser = await puppeteer.launch({ executablePath: findBrowser(), headless: true, args: ['--no-sandbox'] });
const url = `http://127.0.0.1:${server.address().port}/tools/uishot/harness.html?tier=pro&tab=plan`;
const pause = () => new Promise((r) => setTimeout(r, 300));
const shot = async (page, name) => { await pause(); await page.screenshot({ path: join(out, name + '.png') }); };
const post = (page, m) => page.evaluate((m) => window.postMessage(m, '*'), m);
const note = (id, place) => ({ id, sessionId: 'uishot', say: 'Your assistant is checking the model.', how: 'Open the Plan.', place, by: 'assistant', at: new Date().toISOString() });
const planFixture = { planId: 'p1', revision: 1, scope: 'whole model', ranked: false, summary: {}, strip: null, items: [
  { id: 'sales', section: 'Do now', title: 'Describe the Sales measures', objectRefs: ['table:Sales'], sub: 'Make the measures clear.', who: 'asst', doer: 'assistant', status: 'ready' },
  { id: 'product', section: 'Do now', title: 'Describe the Product columns', objectRefs: ['table:Product'], sub: 'Explain the product names.', who: 'asst', doer: 'assistant', status: 'ready' },
] };

async function open(first = false, responses = {}) {
  const page = await browser.newPage();
  page.on('pageerror', (e) => { throw e; });
  await page.setViewport({ width: 1440, height: 900, deviceScaleFactor: 1 });
  await page.evaluateOnNewDocument((first, responses) => {
    let api;
    Object.defineProperty(window, 'acquireVsCodeApi', { configurable: true, set: (v) => { api = v; }, get: () => () => {
      const a = api(); const post = a.postMessage;
      a.postMessage = (m) => {
        if (m.type === 'getFirstRun') { setTimeout(() => window.postMessage({ type: 'firstRun', id: m.id, show: first, connected: false }, '*'), 0); return; }
        if (m.type === 'getGuideFlags') { setTimeout(() => window.postMessage({ type: 'guideFlags', id: m.id, planTour: true, whatChanged: true }, '*'), 0); return; }
        return post.call(a, m);
      };
      return a;
    } });
    let h;
    Object.defineProperty(window, '__uishotHarness', { configurable: true, get: () => h, set: (v) => {
      h = v; for (const [k, value] of Object.entries(responses)) v.setResponse(k, value);
    } });
  }, first, { getModelPlan: planFixture, ...responses });
  await page.goto(url, { waitUntil: 'networkidle0' });
  await page.waitForSelector('[data-testid="plan-page"]');
  return page;
}

try {
  for (const restore of [true, false]) {
    const recovery = await open(false, { sessionInfo: { sessionId: 'uishot', revision: 0, modelName: 'Sales', source: '/models/Sales.bim',
      fileRecovery: { source: '/models/Sales.bim', capturedUtc: '2026-10-11T14:32:00' } } });
    await recovery.waitForSelector('[data-testid="file-recovery-offer"]');
    assert.match(await recovery.$eval('[data-testid="file-recovery-offer"]', (e) => e.innerText), /Semanticus kept your unsaved changes to Sales.bim from 14:32/);
    await recovery.evaluate(() => {
      const h = window.__uishotHarness;
      window.recoveryCalls = [];
      h.setResponse('resolveFileRecovery', (args) => {
        window.recoveryCalls.push(args);
        h.setResponse('sessionInfo', { sessionId: 'uishot', revision: 1, modelName: 'Sales', source: '/models/Sales.bim', hasUnsavedEdits: args[1] });
        return true;
      });
    });
    if (restore) await shot(recovery, 'fix2-recovery-offer');
    await recovery.$eval('[data-testid="file-recovery-offer"]', (e, restore) => [...e.querySelectorAll('button')].find((b) => b.textContent === (restore ? 'Restore them' : 'Discard')).click(), restore);
    await recovery.waitForSelector('[data-testid="file-recovery-offer"]', { hidden: true });
    assert.deepEqual(await recovery.evaluate(() => window.recoveryCalls), [['uishot', restore]]);
    await recovery.close();
  }
  if (process.env.RECOVERY_ONLY) { console.log('file recovery: Restore and Discard browser checks passed'); }
  else {
  const p = await open(true, { getConsent: { current: false, appliesTo: 'sales' } });
  await p.waitForSelector('.first-run');
  await pause();
  assert.equal(await p.$('[data-testid="consent-card"]'), null);
  await shot(p, 'h5-first-run');
  await p.click('.first-run-around');
  await post(p, { type: 'learnReady', chapter: 'plan', modelName: 'Contoso Shop', sessionId: 'uishot' });
  await p.waitForSelector('[data-testid="tour-step"]');
  assert.match(await p.$eval('[data-testid="studio-driver-bar"]', (e) => e.textContent), /The tour is playing/);
  assert.equal(await p.$('[data-testid="consent-card"]'), null);
  await shot(p, 'h5-tour');
  await post(p, { type: 'navigate', tab: 'history', sessionId: 'uishot' });
  await p.waitForSelector('[data-testid="plan-history"]');
  await p.click('[data-testid="tour-leave"]');
  await post(p, { type: 'navigate', tab: 'plan', target: 'work', sessionId: 'uishot' });
  await pause();
  assert.equal(await p.$('[data-testid="plan-history"]'), null);
  await p.evaluate(() => { const h = window.__uishotHarness; h.setResponse('getConsent', { current: true, appliesTo: 'sales' }); h.dispatch({ type: 'didChange', payload: { sessionId: 'uishot', revision: 2 } }); });
  await shot(p, 'h5-leave-work');
  await p.close();

  const s = makeStartPlanState({});
  const v = startPlanView(s); v.approval = { digest: v.digest, by: 'agent' };
  const sp = await open(false, { getStartPlan: v });
  await sp.waitForSelector('[data-testid="sp-approval"]');
  assert.match(await sp.$eval('[data-testid="sp-approval"]', (e) => e.textContent), /Your assistant approved/);
  assert.doesNotMatch(await sp.$eval('[data-testid="sp-strip"]', (e) => e.textContent), /wait on you/);
  await shot(sp, 'h10-start-plan');
  await sp.close();

  const q = await open();
  await post(q, { type: 'driveMode', mode: 'claudeDrives', assistantConnected: true });
  await post(q, { type: 'studioShow', payload: note('n1', 'edit/queries') });
  await q.waitForSelector('[data-testid="queries-result"]');
  await q.click('[data-testid="note-what-i-did"]');
  await q.waitForSelector('[data-testid="what-i-did"]');
  await post(q, { type: 'activity', payload: {
    seq: 9001, kind: 'run_dax', origin: 'agent', ok: true,
    query: 'EVALUATE ROW("Share", [Share])', result: { columns: [{ name: '[Share]', dataType: 'Double' }], rows: [[0.06409]], rowCount: 1, elapsedMs: 12 },
  } });
  await pause();
  assert.equal(await q.$('[data-testid="what-i-did"]'), null);
  assert.doesNotMatch(await q.$eval('body', (e) => e.innerText), /Connect a model to run queries|Choose test model/);
  await shot(q, 'h9-query-result');
  await post(q, { type: 'activity', payload: {
    seq: 9002, kind: 'run_dax', origin: 'agent', ok: true,
    query: 'EVALUATE ROW("Share", [Share])', result: { columns: [{ name: '[Share]', dataType: 'Double', format: '0.0%' }], rows: [[0.06409]], rowCount: 1, elapsedMs: 12 },
  } });
  await pause();
  assert.match(await q.$eval('[data-testid="queries-result"]', (e) => e.innerText), /6.4%/);
  await shot(q, 'h9-query-percent-format');
  await q.evaluate(() => { const h = window.__uishotHarness; h.setResponse('sessionInfo', { __uishotError: 'temporary read failure' }); h.dispatch({ type: 'connectionChanged' }); });
  await pause();
  assert.match(await q.$eval('.studio-root', (e) => e.innerText), /Contoso \(mock\)/);
  await shot(q, 'h15-header-kept');
  await q.close();

  const plan = await open();
  await post(plan, { type: 'treeSelection', ref: 'table:Sales', sessionId: 'uishot', by: 'assistant' });
  await pause();
  assert.doesNotMatch(await plan.$eval('[data-testid="plan-page"]', (e) => e.innerText), /Showing Sales/);
  assert.match(await plan.$eval('[data-testid="plan-page"]', (e) => e.innerText), /Describe the Sales measures/);
  assert.match(await plan.$eval('[data-testid="plan-page"]', (e) => e.innerText), /Describe the Product columns/);
  await shot(plan, 'h11-plan-unfiltered');
  await plan.close();

  const pub = await open(false, { compareModels: { items: [], created: 0, updated: 0, deleted: 0, equal: 1 } });
  await post(pub, { type: 'navigate', tab: 'deploy', sessionId: 'uishot' });
  await pub.waitForSelector('[data-testid="publish-empty"]');
  await shot(pub, 'h8-publish-empty');
  await pub.close();
  console.log(`studio-polish: browser checks passed; screenshots in ${out}`);
  }
} finally { await browser.close(); await new Promise((r) => server.close(r)); }
