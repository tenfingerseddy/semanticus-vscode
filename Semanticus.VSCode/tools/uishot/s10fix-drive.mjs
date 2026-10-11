// Driver for m11/s10fix (5 Oct): the site screenshot run's app findings, seen through the REAL Studio bundle and the mock
// engine. Each scene opens a screen at the size a 1440x900 and a 1280 wide VS Code window leaves a Studio tab (the title bar,
// tab strip and status bar take the rest), in both themes, does what the person did, measures what must be in view, and
// writes a PNG per size and theme to read.
//
//   b4  Power Query: Configure on Incremental refresh must bring the policy controls (Save policy) into view.
//   b5  Plan: a draft whose after check could not run offers Try again.
//   b6  Your assistant > Settings: what Pro decides: the model scope heading stays in view while the switches scroll.
//   b7  Compare preview speaks product words; the tour's first step sits clear of the summary.
//
//   ONLY=b4 OUTDIR=<dir> node tools/uishot/s10fix-drive.mjs
import { createServer } from 'node:http';
import { existsSync, mkdirSync, readFileSync, statSync, writeFileSync } from 'node:fs';
import { extname, join, normalize, resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { findBrowser, requireSupportedNode } from './browser.mjs';
import { applyTheme } from './themes.mjs';
requireSupportedNode();
import { createRequire } from 'node:module';
const require_ = createRequire(import.meta.url);
const puppeteer = require_('puppeteer-core').default ?? require_('puppeteer-core');

const here = dirname(fileURLToPath(import.meta.url));
const webRoot = resolve(here, '..', '..');
const OUT = process.env.OUTDIR || resolve(webRoot, 'shots', 's10fix');
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
const SCRATCH = resolve(process.env.HOME ?? '/tmp', '.cache', 'semanticus-uishot', `s10fix-${process.pid}`);
mkdirSync(SCRATCH, { recursive: true });
const browser = await puppeteer.launch({
  executablePath: process.env.SEMANTICUS_BROWSER || findBrowser(), headless: true, protocolTimeout: 180000,
  args: ['--no-sandbox', '--disable-gpu', '--disable-dev-shm-usage', '--force-color-profile=srgb', '--hide-scrollbars', `--user-data-dir=${SCRATCH}`],
  env: { ...process.env, TZ: 'UTC' },
});
const wait = (ms) => new Promise((r) => setTimeout(r, ms));
const results = {};
const failures = [];
const must = (scene, ok, why) => { if (!ok) failures.push(`${scene}: ${why}`); };
const only = process.env.ONLY ? process.env.ONLY.split(',') : null;
const want = (s) => !only || only.includes(s);
// The Studio tab inside a VS Code window: 1440x900 leaves about 1370x800 (measured on the J9 PNGs: the page area under the tab strip), 1280x800 about 1210x710.
const SIZES = [{ name: '1440', width: 1370, height: 800 }, { name: '1280', width: 1210, height: 710 }];
const THEME_NAMES = ['dark', 'light'];

async function open(query, size, theme) {
  const page = await browser.newPage();
  const errors = [];
  page.on('pageerror', (e) => errors.push('pageerror: ' + e.message));
  await page.setViewport({ width: size.width, height: size.height, deviceScaleFactor: 1 });
  await page.goto(`http://127.0.0.1:${port}/tools/uishot/harness.html?${query}`, { waitUntil: 'networkidle0', timeout: 30000 });
  await applyTheme(page, theme);
  await wait(1500);
  return { page, errors };
}
const clickText = (page, text, within = 'body') => page.evaluate((t, w) => {
  const b = [...document.querySelectorAll(`${w} button`)].find((x) => (x.textContent || '').trim().startsWith(t) && !x.disabled);
  if (b) { b.scrollIntoView({ block: 'center' }); b.click(); }
  return !!b;
}, text, within);
// Is the element fully inside the scroll box that holds it, and inside the window?
const inView = (page, selector) => page.evaluate((sel) => {
  const el = document.querySelector(sel); if (!el) return { found: false };
  const r = el.getBoundingClientRect();
  const box = (el.closest('.overflow-auto') || document.documentElement).getBoundingClientRect();
  const lo = Math.max(0, box.top), hi = Math.min(window.innerHeight, box.bottom);
  return { found: true, top: Math.round(r.top), bottom: Math.round(r.bottom), visible: r.top >= lo && r.bottom <= hi && r.height > 0 };
}, selector);
async function eachView(scene, query, run) {
  for (const size of SIZES) for (const theme of THEME_NAMES) {
    const { page, errors } = await open(query, size, theme);
    const info = await run(page, `${size.name}-${theme}`, size);
    await page.screenshot({ path: join(OUT, `${scene}-${size.name}-${theme}.png`) });
    results[`${scene}-${size.name}-${theme}`] = info;
    if (errors.length) failures.push(`${scene} ${size.name} ${theme}: ` + errors.join(' | '));
    await page.close();
  }
}

// ---- b4: Incremental refresh, Configure ----------------------------------------------------------------------------
if (want('b4')) {
  await eachView('b4', 'tab=mcode', async (page, tag, size) => {
    must('b4 ' + tag, await clickText(page, 'Configure'), 'no Configure button');
    await wait(1200);   // the smooth scroll the button asks for
    const save = await inView(page, 'section[aria-label="Incremental refresh settings"] .sem-btn-primary');
    const card = await inView(page, 'section[aria-label="Incremental refresh settings"]');
    must('b4 ' + tag, card.found && card.top >= 0 && card.top < 200, `the Incremental refresh card is not at the top after Configure: ${JSON.stringify(card)}`);
    const field = await inView(page, 'section[aria-label="Incremental refresh settings"] input[type=number]');
    must('b4 ' + tag, field.found && field.visible, `the first policy field is not in view after Configure: ${JSON.stringify(field)}`);
    // Save policy sits under the form: it must be in view where the window is tall enough for the whole card (1440 wide).
    if (size.name === '1440') must('b4 ' + tag, save.found && save.visible, `Save policy is not in view after Configure: ${JSON.stringify(save)}`);
    return { card, field, save };
  });
}

// ---- b6: Your assistant > Settings: what Pro decides, the model scope heading stays in view ---------------------------
if (want('b6')) {
  await eachView('b6', 'tab=plan&tier=pro&conn=1&live=1', async (page, tag) => {
    // 25 points, like the real engine: the list is longer than the window.
    await page.evaluate(() => {
      const h = window.__uishotHarness;
      h.setResponse('sessionInfo', Object.assign({}, h.response('sessionInfo', []), { sessionId: 'uishot', modelName: 'Northstar' }));
      const pts = Array.from({ length: 25 }, (_, i) => ({ name: 'point.p' + i, enabled: i % 3 !== 0, live: i % 4 !== 3, purpose: 'Choose thing number ' + i + ' for this model. It reads a name and picks one of a few answers.', fields: ['a name'] }));
      h.setResponse('getDecisionPoints', () => pts);
    });
    await page.evaluate(() => { const chip = [...document.querySelectorAll('button[aria-haspopup="menu"]')].find((b) => /Your assistant/.test(b.textContent || '')); chip && chip.click(); });
    await wait(400);
    await page.evaluate(() => { const row = [...document.querySelectorAll('[role="menuitem"]')].find((b) => /what Pro decides/i.test(b.textContent || '')); row && row.click(); });
    await wait(1200);
    // Scroll everything that can scroll in the dialog to its end, as the person does to reach the last switch.
    await page.evaluate(() => { for (const el of document.querySelectorAll('[data-testid="decisions-panel"], [data-testid="decisions-panel"] *')) if (el.scrollHeight > el.clientHeight + 1) el.scrollTop = el.scrollHeight; });
    await wait(300);
    const vis = (sel) => page.evaluate((q) => {
      const el = document.querySelector(q); if (!el) return { found: false };
      const r = el.getBoundingClientRect(); return { found: true, top: Math.round(r.top), bottom: Math.round(r.bottom), visible: r.top >= 0 && r.bottom <= window.innerHeight };
    }, sel);
    const heading = await vis('[data-testid="decisions-panel"] h2');
    const model = await vis('[data-testid="decisions-model"]');
    const close = await vis('[data-testid="decisions-close"]');
    const rows = await page.evaluate(() => [...document.querySelectorAll('[data-testid="decisions-row"]')].map((r) => { const b = r.getBoundingClientRect(); return b.bottom <= window.innerHeight && b.top >= 0; }));
    const last = rows[rows.length - 1];
    must('b6 ' + tag, heading.found && heading.visible, `the heading scrolled out of view: ${JSON.stringify(heading)}`);
    must('b6 ' + tag, model.found && model.visible, `the model scope line scrolled out of view: ${JSON.stringify(model)}`);
    must('b6 ' + tag, close.found && close.visible, `Close scrolled out of view: ${JSON.stringify(close)}`);
    must('b6 ' + tag, rows.length > 20 && last === true, `the last switch is not in view at the end of the scroll (rows ${rows.length})`);
    return { heading, model, close, rows: rows.length, last };
  });
}

// ---- b7: the tour opened from History starts on Work, and its first card clears the model summary ----------------------
if (want('b7')) {
  await eachView('b7', 'tab=plan&tier=pro&conn=1&live=1', async (page, tag) => {
    must('b7 ' + tag, await clickText(page, 'History'), 'no History switch');
    await wait(800);
    const opened = await page.evaluate(() => { const b = document.querySelector('button[aria-label="Help for the Plan"]'); b && b.click(); return !!b; });
    must('b7 ' + tag, opened, 'no Help mark on the Plan');
    await wait(600);
    must('b7 ' + tag, await clickText(page, 'Show the tour'), 'no Show the tour button');
    await wait(2200);   // the card measures for a moment while the page settles
    const state = await page.evaluate(() => {
      const work = document.querySelector('[data-testid="plan-view-work"]');
      const facts = document.querySelector('[data-testid="plan-facts"]');
      const card = document.querySelector('.plan-tour[role="dialog"]');
      const fr = facts && facts.getBoundingClientRect(); const cr = card && card.getBoundingClientRect();
      return { onWork: work ? work.getAttribute('aria-pressed') === 'true' : null, factsBottom: fr ? Math.round(fr.bottom) : null,
        cardTop: cr ? Math.round(cr.top) : null, cardFound: !!card };
    });
    must('b7 ' + tag, state.onWork === true, 'the tour opened from History is still on History');
    must('b7 ' + tag, state.cardFound && state.factsBottom != null && state.cardTop >= state.factsBottom, `the tour card covers the model summary: ${JSON.stringify(state)}`);
    return state;
  });
}

// ---- h1: the assistant moves the publish destination: the page follows, and Publish sends what it showed ----------------
if (want('h1')) {
  await eachView('h1', 'tab=deploy&conn=1&live=1', async (page, tag) => {
    const footer = () => page.evaluate(() => { const f = [...document.querySelectorAll('*')].find((e) => e.children.length === 0 && /^Publish to\s*$/.test((e.textContent || '').trim())); return f ? (f.parentElement.textContent || '').trim() : null; });
    const before = await footer();
    // The host relays the engine's set_publish_destination activity as 'connectionChanged'; the engine's context is now Moved Model.
    await page.evaluate(() => {
      const h = window.__uishotHarness;
      const ctx = JSON.parse(JSON.stringify(h.response('connectionContext', [])));
      ctx.publishing = Object.assign({}, ctx.publishing, { modelName: 'Moved Model', database: 'Moved', connectionId: 'cn1' });
      h.setResponse('connectionContext', () => JSON.parse(JSON.stringify(ctx)));
      h.dispatch({ type: 'connectionChanged' });
    });
    await wait(1200);
    const after = await footer();
    must('h1 ' + tag, before && after && before !== after && /Moved Model/.test(after), `the footer did not follow the new destination: "${before}" then "${after}"`);
    // Press Publish: the preview call must carry the destination the page now shows.
    const pressed = await page.evaluate(() => { const b = [...document.querySelectorAll('button')].find((x) => /^Publish(\.\.\.|\u2026)$/.test((x.textContent || '').trim()) && !x.disabled); b && b.click(); return b ? (b.textContent || '').trim() : false; });
    results['h1-pressed-' + tag] = pressed;
    must('h1 ' + tag, pressed, 'no Publish button');
    await wait(1500);
    const calls = await page.evaluate(() => window.__uishotHarness.messages.filter((x) => x && x.type === 'rpc' && x.method === 'deployLive').map((x) => x.params));
    must('h1 ' + tag, calls.length > 0, 'Publish made no deployLive call');
    const last = calls[calls.length - 1] || [];
    must('h1 ' + tag, last[11] === 'Moved', `the preview did not carry the shown database: ${JSON.stringify(last)}`);
    must('h1 ' + tag, typeof last[10] === 'string' && last[10].length > 0, `the preview did not carry the shown endpoint: ${JSON.stringify(last)}`);
    return { before, after, last };
  });
}

// ---- b2: Data agent: picking a browser sign-in lists nothing until the person presses Sign in ------------------------------
if (want('b2')) {
  await eachView('b2', 'tab=dataagent&tier=pro&conn=1&live=1', async (page, tag) => {
    const listCalls = () => page.evaluate(() => window.__uishotHarness.messages.filter((x) => x && x.type === 'rpc' && x.method === 'listWorkspaces').map((x) => x.params[0]));
    const before = await listCalls();
    // Pick "Sign in in a browser" the way a person does, from the menu.
    await page.evaluate(() => { const sel = document.querySelector('select[aria-label="How to sign in"]'); const set = Object.getOwnPropertyDescriptor(HTMLSelectElement.prototype, 'value').set; set.call(sel, 'interactive'); sel.dispatchEvent(new Event('change', { bubbles: true })); });
    await wait(1500);
    const afterPick = await listCalls();
    const card = await page.evaluate(() => !!document.querySelector('[data-testid="dataagent-sign-in"]'));
    must('b2 ' + tag, afterPick.length === before.length, `picking the browser sign-in listed workspaces at once: ${JSON.stringify(afterPick)}`);
    must('b2 ' + tag, card, 'no Sign in card after picking the browser sign-in');
    await page.screenshot({ path: join(OUT, `b2-${tag}-waiting.png`) });
    must('b2 ' + tag, await clickText(page, 'Sign in', '[data-testid="dataagent-sign-in"]'), 'no Sign in button');
    await wait(1500);
    const afterPress = await listCalls();
    must('b2 ' + tag, afterPress.length === before.length + 1 && afterPress[afterPress.length - 1] === 'interactive', `pressing Sign in did not list with the browser sign-in: ${JSON.stringify(afterPress)}`);
    return { before, afterPick, afterPress, card };
  });
}

await browser.close();
server.close();
writeFileSync(join(OUT, 'results.json'), JSON.stringify({ results, failures }, null, 2));
console.log(JSON.stringify(results, null, 1));
if (failures.length) { console.error('FAILURES:\n  ' + failures.join('\n  ')); process.exit(1); }
console.log('every scene held');
