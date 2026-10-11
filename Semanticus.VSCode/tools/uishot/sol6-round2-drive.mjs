// Acceptance driver for m11/sol6-ui round 2: the Properties grid keeps typed, unwritten text through a model change, offers the moved choice, names the value
// a write started from, and says out loud when a Save did not write a format draft. Runs the REAL media/propgrid/propgrid.js in the headless harness page.
// Every scene states what it must show, and the run fails when one does not. Screenshots go to OUTDIR (default shots/sol6-round2).
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
const webRoot = resolve(here, '..', '..');
const OUT = process.env.OUTDIR || resolve(webRoot, 'shots', 'sol6-round2');
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
const SCRATCH = resolve(process.env.HOME ?? '/tmp', '.cache', 'semanticus-uishot', `sol6r2-${process.pid}`);
mkdirSync(SCRATCH, { recursive: true });
const browser = await puppeteer.launch({
  executablePath: process.env.SEMANTICUS_BROWSER || findBrowser(), headless: true, protocolTimeout: 180000,
  args: ['--no-sandbox', '--disable-gpu', '--disable-dev-shm-usage', '--force-color-profile=srgb', '--hide-scrollbars', `--user-data-dir=${SCRATCH}`],
  env: { ...process.env, TZ: 'UTC' },
});
const wait = (ms) => new Promise((r) => setTimeout(r, ms));
const failures = [];
const must = (ok, why) => { if (!ok) failures.push(why); };

async function grid(scenario) {
  const page = await browser.newPage();
  const errors = [];
  page.on('pageerror', (e) => errors.push('pageerror: ' + e.message));
  await page.setViewport({ width: 420, height: 760, deviceScaleFactor: 2 });
  // Record everything the grid posts to the host: the harness's mock host swallows it.
  await page.evaluateOnNewDocument(() => {
    window.__posted = [];
    let installed;
    Object.defineProperty(window, 'acquireVsCodeApi', {
      configurable: true,
      get() { return installed; },
      set(factory) { installed = () => { const api = factory(); const real = api.postMessage; api.postMessage = (m) => { window.__posted.push(m); return real(m); }; return api; }; },
    });
  });
  await page.goto(`http://127.0.0.1:${port}/tools/uishot/propgrid.html?s=${scenario}`, { waitUntil: 'networkidle0', timeout: 30000 });
  await wait(400);
  return { page, errors };
}
const send = (page, msg) => page.evaluate((m) => window.dispatchEvent(new MessageEvent('message', { data: m })), msg);
const posted = (page, type) => page.evaluate((t) => window.__posted.filter((m) => m.type === t), type);
const P = (name, displayName, category, kind, value) => ({ name, displayName, category, kind, value, description: displayName, options: [], readOnly: false, varies: false });
/** The grid's load for the measure, with the description the assistant has now. */
const measureLoad = (description) => ({ type: 'load', key: '["measure:Sales/Total Sales"]', name: 'Total Sales', multi: false, props: [
  P('Name', 'Name', 'Basic', 'string', 'Total Sales'), P('Description', 'Description', 'Basic', 'string', description),
  P('DisplayFolder', 'Display Folder', 'Basic', 'string', 'Sales\\KPIs'), P('FormatString', 'Format String', 'Formatting', 'string', '#,##0.00')] });

// ---- scene A: words typed in Description survive a model change; the choice is offered; Keep mine writes naming the NEW value -------
{
  const { page, errors } = await grid('measure');
  await page.click('textarea[data-prop="Description"]');
  await page.keyboard.down('Control'); await page.keyboard.press('End'); await page.keyboard.up('Control');
  await page.keyboard.type(' MY TYPED WORDS');
  const typed = await page.evaluate(() => document.querySelector('textarea[data-prop="Description"]').value);
  must(typed.endsWith('MY TYPED WORDS'), 'typing did not reach the box');
  // The assistant changes the description; the model change reloads the whole grid.
  await send(page, measureLoad('THE ASSISTANT REWROTE THIS'));
  await wait(200);
  const after = await page.evaluate(() => ({
    text: document.querySelector('textarea[data-prop="Description"]')?.value,
    focused: document.activeElement?.getAttribute?.('data-prop'),
    note: (document.querySelector('[data-keep]')?.closest('.err')?.textContent || '').trim(),
    buttons: [...document.querySelectorAll('[data-keep], [data-usenew]')].map((b) => b.textContent.trim()),
  }));
  must(after.text === typed, `the typed words were replaced by the model change: ${JSON.stringify(after)}`);
  must(after.focused === 'Description', `the box lost its focus: ${after.focused}`);
  must(/changed while you were typing/.test(after.note), `no moved note: ${JSON.stringify(after)}`);
  must(after.buttons.join('|') === 'Keep mine|Use the new one', `buttons: ${after.buttons}`);
  await page.screenshot({ path: join(OUT, 'grid-typed-kept-moved.png') });
  // Pressing Enter while moved writes nothing.
  await page.keyboard.press('Enter');
  must((await posted(page, 'set')).length === 0, 'a write went out while the value was moved and unchosen');
  // Keep mine writes AT ONCE, naming the NEW value as the one it replaces, with no further gesture (no synthetic change event: a real blur sends none).
  const keep = await page.$('[data-keep]');
  must(!!keep, 'no Keep mine button to press');
  if (keep) await keep.click();
  await wait(100);
  const sets = await posted(page, 'set');
  must(sets.length === 1 && sets[0].name === 'Description' && sets[0].value === typed && sets[0].expected === 'THE ASSISTANT REWROTE THIS',
    `Keep mine should write at once, naming the assistant's value: ${JSON.stringify(sets)}`);
  must(!(await page.$('[data-keep]')), 'the choice is still shown after Keep mine wrote');
  must((await page.evaluate(() => document.querySelector('textarea[data-prop="Description"]')?.value)) === typed, 'Keep mine changed the words in the box');
  // The value moves AGAIN before the write lands: the host refuses, and the choice comes back with the words.
  await send(page, measureLoad('THE ASSISTANT REWROTE IT A SECOND TIME'));
  await send(page, { type: 'setError', name: 'Description', error: 'changed since you opened it; nothing was written' });
  await wait(150);
  must(!!(await page.$('[data-keep]')), 'a refused Keep mine did not show the choice again');
  must((await page.evaluate(() => document.querySelector('textarea[data-prop="Description"]')?.value)) === typed, 'a refused Keep mine lost the words');
  await page.screenshot({ path: join(OUT, 'grid-keep-mine-written.png') });
  if (errors.length) failures.push(errors.join(' | '));
  await page.close();
}

// ---- scene B: the plain case. A write names the value the grid showed; nothing is asked ----------------------------------------
{
  const { page, errors } = await grid('measure');
  const original = await page.evaluate(() => document.querySelector('input[data-prop="DisplayFolder"]').value);
  await page.focus('input[data-prop="DisplayFolder"]');
  await page.evaluate(() => document.querySelector('input[data-prop="DisplayFolder"]').select());
  await page.keyboard.type('Finance');
  await page.keyboard.press('Tab');
  await wait(100);
  const sets = await posted(page, 'set');
  must(sets.length === 1 && sets[0].name === 'DisplayFolder' && sets[0].value === 'Finance' && sets[0].expected === original, `plain write: ${JSON.stringify(sets)} (the grid showed ${original})`);
  must(!(await page.evaluate(() => !!document.querySelector('[data-keep]'))), 'a plain write showed a moved note');
  await page.screenshot({ path: join(OUT, 'grid-plain-write.png') });
  if (errors.length) failures.push(errors.join(' | '));
  await page.close();
}

// ---- scene C: Save Model with a format draft typed and the focus moved elsewhere says so, in the host's words ------------------
{
  const { page, errors } = await grid('formatexpr');
  await wait(200);
  await page.click('.fx-ta');
  await page.keyboard.type(' + 1');
  await page.click('input[data-prop="DisplayFolder"]');   // focus moves away from the format box
  await send(page, { type: 'applyFocused', id: 41 });
  await wait(100);
  const applied = (await posted(page, 'applied')).find((m) => m.id === 41);
  must(applied && /Your format draft was not saved; open it and press Apply\./.test(applied.notice || ''), `no notice for the unsaved format draft: ${JSON.stringify(applied)}`);
  must((await posted(page, 'set')).every((m) => m.name !== 'FormatStringExpression'), 'the format draft was written by a Save it was not asked to apply');
  await page.screenshot({ path: join(OUT, 'grid-format-draft-not-saved.png') });
  // The same Save with nothing typed says nothing.
  const quiet = await grid('measure');
  await send(quiet.page, { type: 'applyFocused', id: 42 });
  await wait(100);
  const none = (await posted(quiet.page, 'applied')).find((m) => m.id === 42);
  must(none && !none.notice, `a Save with nothing typed said something: ${JSON.stringify(none)}`);
  await quiet.page.close();
  if (errors.length) failures.push(errors.join(' | '));
  await page.close();
}

await browser.close();
server.close();
writeFileSync(join(OUT, 'results.json'), JSON.stringify({ failures }, null, 2));
if (failures.length) { console.error('scenes did not show what they promised:\n  ' + failures.join('\n  ')); process.exit(1); }
console.log('all scenes showed what they promised');
