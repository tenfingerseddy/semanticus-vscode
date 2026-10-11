// Acceptance driver for lane U3 (slice 9, Plan at the heart): "Give to your assistant", with Copy beside it, at
// every control the send inventory keeps, driven through the REAL Studio bundle in the harness, in three themes.
//
// The engine is the harness's mock, extended after load: getFixPrompt and bpaGetFixPrompt answer the engine's
// prompt, and addPlanTask records what it was sent and answers a task id the way PlanTasks.AddPlanTaskAsync does.
// Every call a page makes is returned in the results, so a shot never has to be trusted for what was sent.
//
// Scenes, one per control class: an AI readiness finding (idle, then given), a Model quality finding, a storage
// idea, a docs section, a pending change that needs words, the custom rule box, Explain this value, and the
// "On the plan" link opening the Plan, a reconnect to the same model that still reads On the plan, and a task that
// closes (heard through plan/didChange, or read on coming back to the page) bringing Give back. The DAX field is
// checked to still carry Copy only.
//
//   OUTDIR=... SEMANTICUS_BROWSER=/usr/bin/chromium node tools/uishot/cp-send-drive.mjs
import { createServer } from 'node:http';
import { existsSync, mkdirSync, readFileSync, statSync, writeFileSync } from 'node:fs';
import { extname, join, normalize, resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createRequire } from 'node:module';
import { findBrowser, requireSupportedNode } from './browser.mjs';
import { applyTheme } from './themes.mjs';
requireSupportedNode();
const require_ = createRequire(import.meta.url);
const puppeteer = require_('puppeteer-core').default ?? require_('puppeteer-core');

const here = dirname(fileURLToPath(import.meta.url));
const webRoot = resolve(here, '..', '..');
const OUT = process.env.OUTDIR || resolve(webRoot, 'shots');
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
const SCRATCH = resolve(process.env.HOME ?? '/tmp', '.cache', 'semanticus-uishot', `u3-${process.pid}`);
mkdirSync(SCRATCH, { recursive: true });
const browser = await puppeteer.launch({ executablePath: process.env.SEMANTICUS_BROWSER || findBrowser(), headless: true, protocolTimeout: 180000,
  args: ['--no-sandbox', '--disable-gpu', '--disable-dev-shm-usage', '--force-color-profile=srgb', '--hide-scrollbars', `--user-data-dir=${SCRATCH}`] });
const wait = (ms) => new Promise((r) => setTimeout(r, ms));
const THEMES = (process.env.THEMES || 'dark,light,hc').split(',');

function install(withPlan, filler = 0) {
  const H = window.__uishotHarness;
  window.__u3 = { calls: [], tasks: [] };
  const log = (name, params) => window.__u3.calls.push([name, ...(params || [])]);
  const prompt = (p) => ({ prompt: `Fix ${p[0]} on ${p[1]}. Use the Semanticus tools, then rescan.` });
  H.setResponse('getFixPrompt', (p) => { log('getFixPrompt', p); return prompt(p); });
  H.setResponse('bpaGetFixPrompt', (p) => { log('bpaGetFixPrompt', p); return prompt(p); });
  // A storage idea on the Size by table page. The mock answers no facts by default, so the page says "Nothing
  // flagged"; this is the engine's shape (StorageOpportunities.cs) for one column. Rescan re-reads it.
  H.setResponse('getModelFacts', () => ({ revision: 1, storageOpportunities: [
    { ruleId: 'STORAGE-SUMMARIZE', ruleName: 'Numeric key is summed by default', group: 'Behavior cleanup', severity: 2,
      ref: 'column:Sales/OrderDateKey', name: 'OrderDateKey', table: 'Sales', column: 'OrderDateKey', kind: 'summarize',
      message: 'A key column adds up by default, which gives a meaningless total.', bytes: 5200000, effect: 'No storage change' },
  ] }));
  let n = 0;
  H.setResponse('addPlanTask', (p) => {
    log('addPlanTask', p);
    // As PlanTasks.AddPlanTaskAsync does: the same page and key while the task is open is the same task.
    const req = p?.[0] ?? {};
    const same = window.__u3.tasks.find((t) => t.sendKey && t.sendKey === req.sendKey && t.sourcePage === req.sourcePage);
    if (same) return { itemId: same.id, action: 'add', performed: false, sentence: 'This is already on the plan. Nothing was added.' };
    n += 1;
    window.__u3.tasks.push({ id: 'pt-0a1b2c3' + n, sendKey: req.sendKey, sourcePage: req.sourcePage, words: req.words, objects: req.objects });
    return { itemId: 'pt-0a1b2c3' + n, action: 'add', performed: false, sentence: 'Added to the plan for your assistant. It is offered on its next call.' };
  });
  // Only the reconnect scene answers the plan from these tasks: the other scenes keep the harness's own plan.
  if (withPlan) H.setResponse('getModelPlan', () => {
    log('getModelPlan', []);
    // `filler` open rows ahead of the tasks, so a task lands below the fold as it does on a real model's plan.
    const ahead = Array.from({ length: filler }, (_, i) => ({ id: `gap/filler-${i}`, title: `Earlier row ${i + 1}`, objects: [], doer: 'you', stateWord: 'ready' }));
    return { items: [...ahead, ...window.__u3.tasks.map((t) => ({ id: t.id, title: t.words, objects: t.objects || [], doer: 'assistant', stateWord: t.state || 'ready',
      originLink: { kind: 'finding', ref: null, sendKey: t.sendKey } }))] };
  });
}

// Click the first visible element matching a CSS selector, or a button whose text starts with the words given.
function act(kind, target, nth) {
  const visible = (e) => e.getClientRects().length > 0;
  let el = null;
  if (kind === 'sel') el = [...document.querySelectorAll(target)].filter(visible)[nth || 0];
  else if (kind === 'text') el = [...document.querySelectorAll('button')].filter(visible).find((b) => (b.textContent || '').includes(target));
  else if (kind === 'ctx') {
    el = [...document.querySelectorAll(target)].filter(visible)[nth || 0];
    if (el) { el.scrollIntoView({ block: 'center' }); el.dispatchEvent(new MouseEvent('contextmenu', { bubbles: true, cancelable: true, view: window })); }
    return !!el;
  }
  if (!el) return false;
  el.scrollIntoView({ block: 'center' });
  el.click();
  return true;
}
function typeInto(sel, text) {
  const e = [...document.querySelectorAll(sel)].find((x) => x.getClientRects().length);
  if (!e) return false;
  const set = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set;
  set.call(e, text); e.dispatchEvent(new Event('input', { bubbles: true }));
  return true;
}
const GIVE = '[data-send-state="give"]';
const JOBS = [
  { slug: 'readiness-idle', tab: 'readiness', steps: [['focus', GIVE]], desc: 'AI readiness finding: Give to your assistant, Copy beside it' },
  { slug: 'readiness-given', tab: 'readiness', steps: [['sel', GIVE]], desc: 'given: On the plan, Copy, and the next-call line; one addPlanTask with model_check and the finding link' },
  { slug: 'bpa-given', tab: 'bpa', steps: [['sel', GIVE]], desc: 'Model quality finding given: your assistant says' },
  { slug: 'stats-blocked', tab: 'stats', q: '&conn=1', steps: [['didChange'], ['text', 'Rescan'], ['focus', GIVE]], desc: 'storage idea: Give and Copy disabled, as Ask AI was, while the scan is not proven to describe the editing model' },
  { slug: 'docs-given', tab: 'docs', steps: [['sel', GIVE]], desc: 'docs section given' },
  { slug: 'optimize-given', tab: 'optimize', steps: [['sel', GIVE]], desc: 'pending change that needs words, given' },
  { slug: 'rule-box', tab: 'bpa', steps: [['text', 'Custom rules'], ['text', 'New rule'], ['type', 'input[placeholder^="For example"]', 'Every measure has a format string'], ['focus', 'input[placeholder^="For example"]']], desc: 'custom rule: one line first, then Give' },
  { slug: 'rule-given', tab: 'bpa', steps: [['text', 'Custom rules'], ['text', 'New rule'], ['type', 'input[placeholder^="For example"]', 'Every measure has a format string'], ['sel', '[data-send-key^="rule|"] [data-send-state="give"]']], desc: 'custom rule given' },
  { slug: 'explain-given', tab: 'daxlab', q: '&conn=1', steps: [['text', 'Run'], ['sel', 'button[title="Matrix"]'], ['ctx', 'td.tnum'], ['text', 'Explain this number'], ['sel', GIVE]], desc: 'Explain this value given (Queries 27 Sep: run first, then the chart kinds show; a number\'s right-click opens a menu with Explain this number)' },
  { slug: 'on-plan-opens', tab: 'readiness', steps: [['sel', GIVE], ['sel', '[data-send-state="on-plan"]']], desc: 'On the plan opens the Plan' },
  // Journey m11 F4: with another object picked in the tree, On the plan landed on a Plan filtered to that object, and
  // the task just given was not on it. It opens the Plan on the task's own row, with the object filter cleared.
  { slug: 'on-plan-shows-task', tab: 'readiness', plan: true, steps: [['tree', 'measure:Sales/Margin %'], ['sel', GIVE], ['sel', '[data-send-state="on-plan"]']],
    expectRow: true, desc: 'another object picked, then On the plan: the Plan opens on the new task, open, with no filter hiding it' },
  // Journey m11-2: the row was opened but sat about 1280px down, off screen. With 30 rows ahead of it, it must be
  // scrolled into view. The plan's answer is held until after the Plan opens, as a real engine's read lands late.
  { slug: 'on-plan-scrolls-to-task', tab: 'readiness', plan: true, filler: 30, steps: [['sel', GIVE], ['hold', 'getModelPlan'], ['sel', '[data-send-state="on-plan"]'], ['release', 'getModelPlan']],
    expectRow: true, expectInView: true, desc: 'On the plan with a long plan: the opened task row is scrolled into view' },
  // Sol 6 slice 9 review: a reconnect to the same model re-reads the plan, so the given finding still says On the
  // plan and no second task can be given for it.
  { slug: 'readiness-reconnect', tab: 'readiness', plan: true, steps: [['sel', GIVE], ['reconnect']], expectOnPlan: 1, desc: 'given, then a reconnect to the same model: still On the plan, read back from the engine' },
  // Codex F-282: the assistant finishes (or declines, or the person sets aside) the task while the page is open.
  // The engine says plan/didChange, the control re-reads the plan, and a closed task's control offers Give again.
  { slug: 'readiness-closed', tab: 'readiness', plan: true, steps: [['sel', GIVE], ['close'], ['planChange']], expectOnPlan: 0, desc: 'given, then the task is done: plan/didChange brings Give back' },
  // And a page opened after the task closed reads the plan on mount, even with no plan/didChange to hear.
  { slug: 'readiness-remount', tab: 'readiness', plan: true, steps: [['sel', GIVE], ['close'], ['nav', 'bpa'], ['nav', 'readiness']], expectOnPlan: 0, desc: 'given, closed while the page was away: coming back reads the plan and offers Give' },
];

const results = [];
for (const theme of THEMES) {
  for (const job of JOBS) {
    const page = await browser.newPage();
    const errors = [];
    page.on('pageerror', (e) => errors.push('pageerror: ' + e.message));
    page.on('console', (m) => { if (m.type() === 'error') errors.push('console: ' + m.text()); });
    await page.setViewport({ width: 1280, height: 900, deviceScaleFactor: 2 });
    let note = '';
    try {
      await page.goto(`http://127.0.0.1:${port}/tools/uishot/harness.html?tab=${job.tab}&expand=all${job.q || ''}`, { waitUntil: 'networkidle0', timeout: 30000 });
      await page.evaluate(install, !!job.plan, job.filler || 0);
      await applyTheme(page, theme);
      await wait(2200);
      for (const [kind, target, value] of job.steps) {
        let ok = true;
        if (kind === 'type') ok = await page.evaluate(typeInto, target, value);
        else if (kind === 'reconnect') ok = await page.evaluate(() => { window.dispatchEvent(new MessageEvent('message', { data: { type: 'reconnected' } })); return true; });
        else if (kind === 'close') ok = await page.evaluate(() => { window.__u3.tasks.forEach((t) => { t.state = 'done'; }); return window.__u3.tasks.length > 0; });
        else if (kind === 'planChange') ok = await page.evaluate(() => { window.dispatchEvent(new MessageEvent('message', { data: { type: 'planDidChange', payload: { revision: 3 } } })); return true; });
        else if (kind === 'nav') ok = await page.evaluate((tab) => { window.dispatchEvent(new MessageEvent('message', { data: { type: 'navigate', tab } })); return true; }, target);
        else if (kind === 'hold') ok = await page.evaluate((m) => window.__uishotHarness.hold(m), target);
        else if (kind === 'release') ok = await page.evaluate((m) => window.__uishotHarness.release(m) >= 0, target);
        else if (kind === 'tree') ok = await page.evaluate((ref) => { window.dispatchEvent(new MessageEvent('message', { data: { type: 'treeSelection', sessionId: 'uishot', ref } })); return true; }, target);
        else if (kind === 'didChange') ok = await page.evaluate(() => { window.dispatchEvent(new MessageEvent('message', { data: { type: 'didChange', payload: { revision: 2, changes: [] } } })); return true; });
        else if (kind === 'focus') ok = await page.evaluate((s) => { const e = [...document.querySelectorAll(s)].find((x) => x.getClientRects().length); if (e) e.scrollIntoView({ block: 'center' }); return !!e; }, target);
        else ok = await page.evaluate(act, kind, target, 0);
        if (!ok) note += `${kind} "${target}" not found; `;
        await wait(900);
      }
      const seen = await page.evaluate(() => {
        const all = (sel) => [...document.querySelectorAll(sel)].filter((e) => e.getClientRects().length).map((e) => (e.textContent || '').replace(/\s+/g, ' ').trim());
        return {
          give: all('[data-send-state="give"]').length,
          onPlan: all('[data-send-state="on-plan"]').length,
          copy: all('[data-send-state="copy"]').length,
          nextCall: document.body.innerText.includes('Your assistant sees it on its next call.'),
          askAi: /\bAsk AI\b|Ask your assistant for help|Ask your assistant to explain/.test(document.body.innerText),
          activeTab: document.querySelector('[data-tab][aria-current="page"], [data-tab][aria-selected="true"]')?.getAttribute('data-tab') ?? null,
          calls: window.__u3.calls,
          // The row of the task just given, on the Plan: drawn, opened, and not behind a filter.
          taskRow: (() => {
            const id = window.__u3.tasks[window.__u3.tasks.length - 1]?.id;
            const row = id && document.querySelector(`[data-testid="plan-list"] [data-row="${id}"]`);
            return { onPlan: !!document.querySelector('[data-testid="plan-page"]'), drawn: !!row && row.getClientRects().length > 0,
              open: !!row && !!row.closest('.pw-item-open'),
              inView: (() => {
                if (!row) return false;
                const r = row.getBoundingClientRect();
                const main = document.querySelector('main')?.getBoundingClientRect() ?? { top: 0, bottom: window.innerHeight };
                return r.top >= main.top - 1 && r.top < main.bottom - 24;
              })(),
              showing: (document.querySelector('[data-testid="plan-list"] .pw-grp') || {}).textContent?.replace(/\s+/g, ' ').trim() ?? null };
          })(),
          // Scoped to the send controls: other copy on these pages is not this lane's.
          emDash: all('[data-send-key]').join(' ').includes('\u2014'),
          productName: /\b(Claude|Codex|ChatGPT)\b/.test(all('[data-send-key]').join(' ')),
        };
      });
      const out = join(OUT, `u3-${job.slug}-${theme}-1280x900.png`);
      await page.screenshot({ path: out, fullPage: false });
      if (job.expectOnPlan === 0 && seen.onPlan !== 0) note += `expected no On the plan once the task closed, saw ${seen.onPlan}; `;
      if (job.expectOnPlan === 0 && seen.give < 1) note += 'expected Give to come back; ';
      if (job.expectOnPlan === 0 && seen.activeTab && seen.activeTab !== job.tab) note += `expected to be back on ${job.tab}, on ${seen.activeTab}; `;
      if (job.expectOnPlan != null && seen.onPlan < job.expectOnPlan) note += `expected ${job.expectOnPlan} On the plan, saw ${seen.onPlan}; `;
      if (job.expectRow && !seen.taskRow.onPlan) note += 'On the plan did not open the Plan; ';
      if (job.expectRow && !seen.taskRow.drawn) note += `the task just given is not drawn on the Plan (list says "${seen.taskRow.showing}"); `;
      if (job.expectRow && seen.taskRow.drawn && !seen.taskRow.open) note += 'the task row is not opened; ';
      if (job.expectInView && seen.taskRow.drawn && !seen.taskRow.inView) note += 'the opened task row is off screen; ';
      if (job.plan && seen.calls.filter((c) => c[0] === 'addPlanTask').length !== 1) note += 'expected exactly one addPlanTask; ';
      results.push({ theme, job: job.slug, desc: job.desc, seen, errors, note, out });
    } catch (e) {
      results.push({ theme, job: job.slug, desc: job.desc, errors, note: note + 'FATAL: ' + e.message });
    } finally { await page.close(); }
  }
}
await browser.close();
server.close();
writeFileSync(join(OUT, 'results-u3-send.json'), JSON.stringify(results, null, 2));
const bad = results.filter((r) => r.note || r.errors.length || r.seen?.emDash || r.seen?.askAi || r.seen?.productName);
console.log(`u3 send: ${results.length} scenes, ${bad.length} with a problem`);
for (const r of bad) console.log(`  ${r.theme} ${r.job}: ${r.note} ${r.errors.join(' | ')}${r.seen?.emDash ? ' EM DASH' : ''}${r.seen?.askAi ? ' ASK AI LEFT' : ''}${r.seen?.productName ? ' PRODUCT NAME' : ''}`);
