// The Learn tour (1.4, lane tour), drawn in the real Studio on the mocked engine, in Dark Modern and Light Modern. Each scene posts the host's own messages (studioShow, studioSuggest) to the page and
// reads what the person would see.
//
// Moments: Help's Learn list · the Plan chapter at its run-the-plan step (note, tour bar, cursor) · the Ask chapter with its recorded
// example (Example tag and line) · the chapter's closing line. The host's replies (learnReady) are posted by this script.
// Run: node tour-drive.mjs   (build the webview first: npm run build:webview in Semanticus.VSCode)
//   OUTDIR=<dir> THEMES=dark node tour-drive.mjs
import { createServer } from 'node:http';
import { existsSync, mkdirSync, readFileSync, statSync } from 'node:fs';
import { dirname, extname, join, normalize, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { findBrowser, requireSupportedNode } from './browser.mjs';
import { THEMES, applyTheme } from './themes.mjs';

requireSupportedNode();
const puppeteer = (await import('puppeteer-core')).default;
const here = dirname(fileURLToPath(import.meta.url));
const webRoot = resolve(here, '..', '..');
const OUT = process.env.OUTDIR || join(here, 'shots', 'tour');
const THEME_LIST = (process.env.THEMES || 'dark,light').split(',');
mkdirSync(OUT, { recursive: true });
if (!existsSync(join(webRoot, 'media', 'studio', 'studio.js'))) { console.error('media/studio/studio.js is missing. Run the webview build first.'); process.exit(1); }

const TYPES = { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.json': 'application/json', '.png': 'image/png', '.svg': 'image/svg+xml', '.woff2': 'font/woff2' };
const server = await new Promise((r) => { const s = createServer((req, res) => {
  const f = normalize(join(webRoot, decodeURIComponent((req.url || '/').split('?')[0].split('#')[0])));
  if (!f.startsWith(webRoot) || !existsSync(f) || statSync(f).isDirectory()) { res.writeHead(404); res.end(); return; }
  res.writeHead(200, { 'content-type': TYPES[extname(f).toLowerCase()] || 'application/octet-stream' }); res.end(readFileSync(f));
}); s.listen(0, '127.0.0.1', () => r(s)); });
const port = server.address().port;
const browser = await puppeteer.launch({ executablePath: findBrowser(), headless: true, args: ['--no-sandbox'] });
const failures = [];
const wait = (ms) => new Promise((r) => setTimeout(r, ms));
const text = (page, sel) => page.evaluate((s) => [...document.querySelectorAll(s)].map((e) => (e.textContent || '').trim()), sel);
const PLAN = { planId: 'p1', scope: 'whole model', revision: 1, items: [
  { id: 'datetable/model', section: 'Do now', title: 'Mark Date as the date table', sub: 'Time intelligence needs it', who: 'you', status: 'ready', doer: 'you' },
  { id: 'words/model', section: 'Do now', title: 'Write 30 descriptions', sub: '', who: 'asst', status: 'ready', doer: 'assistant' },
  { id: 'proposal/implicit-measures', section: 'Do next', title: 'Add 1 explicit measure', sub: '', who: 'asst', status: 'ready', doer: 'assistant' },
], summary: {}, strip: null };
const hostCommands = (page) => page.evaluate(() => window.__uishotHarness.messages.filter((m) => m.type === 'runCommand').map((m) => `${m.command}:${m.ref}`));

try {
  for (const theme of THEME_LIST) {
    const page = await browser.newPage();
    page.on('pageerror', (e) => failures.push(`${theme}: pageerror ${e.message}`));
    await page.setViewport({ width: 1280, height: 760, deviceScaleFactor: 2 });
    await page.goto(`http://127.0.0.1:${port}/tools/uishot/harness.html?conn=1&tier=pro&tab=plan`, { waitUntil: 'networkidle0', timeout: 30000 });
    await page.waitForSelector('[data-testid="plan-page"]', { timeout: 20000 });
    await applyTheme(page, theme);
    await page.evaluate((plan) => { const h = window.__uishotHarness; h.setResponse('getModelPlan', plan); h.setResponse('getStudioNotes', { sessionId: 'uishot', notes: [], suggestion: null }); }, PLAN);
    const shot = async (name) => { await applyTheme(page, theme); await wait(250); await page.screenshot({ path: join(OUT, `${name}-${theme}.png`) }); };
    const pickLearn = async (label) => {
      await page.click('button[aria-label="Help"]');
      await page.waitForSelector('[role="menu"]', { timeout: 5000 });
      const items = await page.$$('[role="menu"] [role="menuitem"]');
      for (const it of items) if ((await it.evaluate((e) => e.textContent || '')).startsWith(label)) { await it.click(); return true; }
      return false;
    };

    // 1. Help's Learn list.
    await page.click('button[aria-label="Help"]');
    await page.waitForSelector('[role="menu"]', { timeout: 5000 });
    const menu = (await text(page, '[role="menu"] [role="menuitem"]')).join(' | ');
    for (const t of ['The Plan2 min', 'Ask your data2 min', 'Edit your model2 min', 'Check your model2 min', 'Publish2 min']) if (!menu.includes(t)) failures.push(`${theme}: Help lacks "${t}": ${menu}`);
    if (!(await text(page, '.studio-chip-menu-heading')).includes('Learn Semanticus')) failures.push(`${theme}: no Learn Semanticus heading`);
    await shot('1-help-learn-list');
    await page.keyboard.press('Escape');

    // 2. The Plan chapter: the host is asked, answers learnReady, and the first note plays.
    if (!(await pickLearn('The Plan'))) failures.push(`${theme}: no Plan row in Help`);
    await wait(300);
    const asked = await hostCommands(page);
    if (!asked.includes('semanticus.learn.start:plan')) failures.push(`${theme}: host was asked ${JSON.stringify(asked)}`);
    if (!(await text(page, '[data-testid="tour-bar"]'))[0]?.includes('Opening the demo model')) failures.push(`${theme}: no opening line`);
    await page.evaluate(() => window.postMessage({ type: 'learnReady', chapter: 'plan', modelName: 'Contoso Shop', sessionId: 'uishot' }, '*'));
    await page.waitForSelector('[data-testid="note-say"]', { timeout: 5000 });
    const first = (await text(page, '[data-testid="note-say"]'))[0];
    if (!first.startsWith('This is your Plan.')) failures.push(`${theme}: first note reads "${first}"`);
    // the third step shows the Run the plan control (cursor)
    await page.waitForFunction(() => document.querySelector('[data-testid="note-say"]')?.textContent?.startsWith('Run the plan'), { timeout: 20000 });
    await wait(1500);
    const bar = (await text(page, '[data-testid="tour-step"]'))[0];
    if (!bar?.includes('step 3 of 5')) failures.push(`${theme}: the bar reads "${bar}"`);
    await shot('2-plan-chapter-run-step');

    // 3. Next until the chapter ends: the closing line (no assistant connected in this scene).
    for (let i = 0; i < 4; i++) { await page.click('[data-testid="tour-next"]').catch(() => {}); await wait(150); }
    await page.waitForSelector('[data-testid="tour-end"]', { timeout: 5000 });
    const end = (await text(page, '[data-testid="tour-end"]'))[0];
    if (end !== 'Connect your assistant to try this live.') failures.push(`${theme}: the closing line reads "${end}"`);
    await shot('3-plan-chapter-end');

    // 4. Next chapter (Edit comes after Ask? the chapter order is plan, ask): Ask with its recorded example.
    await page.click('[data-testid="tour-next-chapter"]');
    await wait(300);
    await page.evaluate(() => window.postMessage({ type: 'learnReady', chapter: 'ask', modelName: 'Contoso Shop', sessionId: 'uishot' }, '*'));
    await page.waitForSelector('[data-testid="askdata-page"]', { timeout: 10000 });
    await page.waitForSelector('[data-testid="query-example"]', { timeout: 20000 });
    const tag = (await text(page, '[data-testid="query-example"]'))[0];
    if (tag !== 'Example') failures.push(`${theme}: tag reads "${tag}"`);
    const pageText = (await text(page, '[data-testid="query-examples"]'))[0] || '';
    if (!pageText.includes('Example answer. Live answers need your own model.')) failures.push(`${theme}: the example line is missing`);
    if (!pageText.includes('Computers') || !pageText.includes('3,942,000')) failures.push(`${theme}: the recorded rows are not on screen`);
    await wait(1800);
    await shot('4-ask-example');
    const calls = await page.evaluate(() => window.__uishotHarness.messages.filter((m) => m.type === 'rpc' && /^askData|runDax/.test(m.method)).length);
    if (calls !== 0) failures.push(`${theme}: the tour called the engine ${calls} times for the example`);

    // 5. Leaving gives the model back.
    await page.click('[data-testid="tour-leave"]').catch(async () => { await page.waitForSelector('[data-testid="tour-leave"]'); await page.click('[data-testid="tour-leave"]'); });
    await wait(300);
    const after = await hostCommands(page);
    if (!after.includes('semanticus.learn.end:undefined')) failures.push(`${theme}: leave sent ${JSON.stringify(after)}`);
    if (await page.$('[data-testid="tour-bar"]')) failures.push(`${theme}: the bar stayed after leaving`);

    const all = (await text(page, '[data-testid="tour-bar"], [data-testid="note-strip"], [data-testid="query-examples"]')).join(' ');
    if (all.includes(String.fromCharCode(0x2014))) failures.push(`${theme}: an em dash is on screen`);
    await page.close();
  }
} finally { await browser.close(); server.close(); }
if (failures.length) { console.error(failures.join('\n')); process.exit(1); }
console.log(`tour-drive: clean in ${THEME_LIST.join(' and ')}; shots in ${OUT}`);
