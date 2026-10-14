// The note strip, What I did and the suggested next steps (1.4, lane notes), drawn in the real Studio on the mocked engine,
// in Dark Modern and Light Modern. Each scene posts the host's own messages (studioShow, studioSuggest) to the page and
// reads what the person would see.
//
// Moments: a note while Claude drives (the note, "To do this yourself", the screen moved) · the same with the person
// holding the wheel (Show me, "2 more", Skip to now) · What I did with an unread line · the suggestion with its three
// buttons and the Plan line · a pressed button.
//
// The driver state comes from the driver store: the drive sends the host's driveMode and personInput messages.
// Run: node studio-notes-drive.mjs   (build the webview first: npm run build:webview in Semanticus.VSCode)
//   OUTDIR=<dir> THEMES=dark node studio-notes-drive.mjs
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
const OUT = process.env.OUTDIR || join(here, 'shots', 'studio-notes');
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

let n = 0;
const note = (say, place, how, extra = {}) => ({
  id: `sn-00000000000${++n}`.slice(0, 15), say, place, how, at: new Date(Date.UTC(2026, 9, 10, 9, 0, n)).toISOString(), by: 'assistant', sessionId: 'uishot', ...extra,
});
const NOTES = [
  note('I am checking which reports use Net Sales.', 'explore/lineage', 'Open Explore, then Lineage, and pick a field.', { focus: 'measure:Sales/Net Sales' }),
  note('Three measures have no description, so I will write them.', 'edit/properties', 'Select the object in the Model tree. Its properties open beside it.', { focus: 'measure:Sales/Net Sales' }),
  note('The model checks found nothing to fix. Here is the grade.', 'review/ai-readiness', 'Open Review, then AI readiness.'),
  note('Everything is saved. Publishing is the last step.', 'publish/changes', 'Open Publish. It lists what will change.'),
];
const SUGGESTION = {
  id: 'sg-000000000001', say: 'Here is what I would do next.', at: new Date(Date.UTC(2026, 9, 10, 9, 5, 0)).toISOString(), sessionId: 'uishot',
  steps: [
    { label: 'Describe the Sales measures', prompt: 'Describe the measures in the Sales table.' },
    { label: 'Run the model checks', prompt: 'Run the model checks and list what fails.' },
    { label: 'Publish the changes', prompt: 'Publish the saved changes to the workspace.' },
  ],
};
const PLAN = { planId: 'p1', scope: 'whole model', revision: 1, items: [
  { id: 'safefix/Visibility', section: 'Do now', title: 'Hide the key columns', sub: 'So assistants stop guessing', who: 'asst', status: 'ready', doer: 'assistant' },
  { id: 'words/model', section: 'Do now', title: 'Write 3 descriptions', sub: '', who: 'asst', status: 'ready', doer: 'assistant' },
], summary: {}, strip: null };

const state = (page, mode, st, resume = false) => page.evaluate((m, s, r) => {
  window.postMessage({ type: 'driveMode', mode: m, assistantConnected: true }, '*');
  if (s === 'personHasWheel') window.postMessage({ type: 'personInput', at: Date.now() }, '*');
}, mode, st, resume);
const post = (page, type, payload) => page.evaluate((t, p) => window.postMessage({ type: t, payload: p }, '*'), type, payload);
const text = (page, sel) => page.evaluate((s) => [...document.querySelectorAll(s)].map((e) => (e.textContent || '').trim()), sel);

try {
  for (const theme of THEME_LIST) {
    const page = await browser.newPage();
    page.on('pageerror', (e) => failures.push(`${theme}: pageerror ${e.message}`));
    await page.setViewport({ width: Number(process.env.WIDTH) || 1280, height: 760, deviceScaleFactor: 2 });
    await page.goto(`http://127.0.0.1:${port}/tools/uishot/harness.html?conn=1&tier=pro&tab=plan`, { waitUntil: 'networkidle0', timeout: 30000 });
    await page.waitForSelector('[data-testid="plan-page"]', { timeout: 20000 });
    await applyTheme(page, theme);
    await page.evaluate((plan, kept) => {
      const h = window.__uishotHarness;
      h.setResponse('getModelPlan', plan);
      h.setResponse('addPlanTask', { itemId: 'pt-00000001' });
      h.setResponse('getStudioNotes', { sessionId: 'uishot', notes: kept, suggestion: null });
    }, PLAN, []);
    const shot = async (name) => { await applyTheme(page, theme); await wait(250); await page.screenshot({ path: join(OUT, `${name}-${theme}.png`) }); };
    const strip = () => page.$('[data-testid="note-strip"]');

    // 1. Claude drives: the note shows, with the place's own sentence beneath it.
    await state(page, 'claudeDrives', 'idle');
    await post(page, 'studioShow', NOTES[0]);
    await page.waitForSelector('[data-testid="note-say"]', { timeout: 5000 });
    const say = (await text(page, '[data-testid="note-say"]'))[0];
    const how = (await text(page, '[data-testid="note-how"]'))[0];
    if (say !== NOTES[0].say) failures.push(`${theme}: the note reads "${say}"`);
    if (how !== `To do this yourself: ${NOTES[0].how}`) failures.push(`${theme}: the how line reads "${how}"`);
    if (await page.$('[data-testid="note-show-me"]')) failures.push(`${theme}: Show me is offered while Claude drives`);
    await shot('1-note-claude-drives');

    // 2. The person has the wheel: three notes wait, nothing moves.
    await wait(2700);   // let the first note's read time run out
    await state(page, 'claudeDrives', 'personHasWheel');
    for (const k of [1, 2, 3]) await post(page, 'studioShow', NOTES[k]);
    await page.waitForSelector('[data-testid="note-show-me"]', { timeout: 5000 });
    const more = (await text(page, '[data-testid="note-more"]'))[0];
    if (more !== '2 more') failures.push(`${theme}: the count reads "${more}", expected "2 more"`);
    if (!(await page.$('[data-testid="note-skip"]'))) failures.push(`${theme}: Skip to now is missing with three notes waiting`);
    await shot('2-note-person-has-wheel');

    // 3. The next prompt: the newest note plays, the two before it wait in What I did, unread.
    await state(page, 'claudeDrives', 'claudeDriving', true);
    await wait(300);
    await page.click('[data-testid="note-what-i-did"]');
    await page.waitForSelector('[data-testid="what-i-did"]', { timeout: 5000 });
    // It opens as one calm line (copy21); the list is one press away.
    if (await page.$('[data-testid="what-i-did-line"]')) failures.push(`${theme}: What I did opens as a list, not one line`);
    await shot('3a-what-i-did-one-line');
    await page.click('[data-testid="what-i-did-all"]');
    await page.waitForSelector('[data-testid="what-i-did-line"]', { timeout: 5000 });
    const lines = await text(page, '[data-testid="what-i-did-line"] .sn-line-say');
    if (lines.length !== 4) failures.push(`${theme}: What I did lists ${lines.length} lines, expected 4`);
    const unread = (await page.$$('[data-testid="what-i-did-unread"]')).length;
    if (unread !== 2) failures.push(`${theme}: ${unread} unread marks, expected 2`);
    await shot('3-what-i-did');

    // 4. The suggestion under the notes, with the Plan's own top row as the last line.
    await post(page, 'studioSuggest', SUGGESTION);
    await page.waitForSelector('[data-testid="suggest-step"]', { timeout: 5000 });
    await page.waitForSelector('[data-testid="suggest-plan"]', { timeout: 5000 });
    const labels = await text(page, '[data-testid="suggest-step"]');
    if (labels.join('|') !== SUGGESTION.steps.map((s) => s.label).join('|')) failures.push(`${theme}: buttons read ${JSON.stringify(labels)}`);
    const planLine = (await text(page, '[data-testid="suggest-plan"]'))[0];
    if (planLine !== 'From your plan: Hide the key columns') failures.push(`${theme}: the plan line reads "${planLine}"`);
    await shot('4-suggestion');

    // 5. Press a step: its words reach the Plan as a task from the suggest source, and the button says so.
    await page.click('[data-testid="suggest-step"]');
    await page.waitForSelector('[data-testid="suggest-added"]', { timeout: 5000 });
    const added = (await text(page, '[data-testid="suggest-added"]'))[0];
    if (added !== 'Added. Tell Claude go.') failures.push(`${theme}: the pressed step reads "${added}"`);
    const sent = await page.evaluate(() => window.__uishotHarness.messages.filter((m) => m.type === 'rpc' && m.method === 'addPlanTask'));
    const request = sent[0]?.params?.[0];
    if (!request || request.sourcePage !== 'suggest' || request.doer !== 'assistant' || request.words !== SUGGESTION.steps[0].prompt) failures.push(`${theme}: addPlanTask sent ${JSON.stringify(request)}`);
    await shot('5-suggestion-pressed');

    const all = (await text(page, '[data-testid="note-strip"]')).join(' ');
    if (all.includes(String.fromCharCode(0x2014))) failures.push(`${theme}: an em dash is on screen`);
    await page.close();
  }
} finally { await browser.close(); server.close(); }
if (failures.length) { console.error(failures.join('\n')); process.exit(1); }
console.log(`studio-notes-drive: clean in ${THEME_LIST.join(' and ')}; shots in ${OUT}`);
