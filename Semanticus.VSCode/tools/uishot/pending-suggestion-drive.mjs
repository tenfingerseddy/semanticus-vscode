// Pending changes, over a plan that carries Jev format lines (THE FORMAT FILL RULE, addendum 3 item 19). A line the engine could
// not prove is drawn as a SUGGESTION: a "suggestion" chip (never "safe"), its plain note as words on the row, and the summary
// counts Jev lines. The proved line beside it keeps its "safe" chip. Shot in every theme; the screen check runs on each.
// Run: node pending-suggestion-drive.mjs   (build the webview first: npm run build:webview in Semanticus.VSCode)
import { createServer } from 'node:http';
import { existsSync, readFileSync, statSync, mkdirSync } from 'node:fs';
import { dirname, extname, join, normalize, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { findBrowser, requireSupportedNode } from './browser.mjs';
import { THEMES, applyTheme } from './themes.mjs';

requireSupportedNode();
const puppeteer = (await import('puppeteer-core')).default;
const dir = dirname(fileURLToPath(import.meta.url));
const webRoot = resolve(dir, '..', '..');
const shots = join(dir, 'shots');
mkdirSync(shots, { recursive: true });
const TYPES = { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.json': 'application/json', '.png': 'image/png', '.svg': 'image/svg+xml', '.woff2': 'font/woff2' };
const server = await new Promise((r) => { const s = createServer((req, res) => {
  const f = normalize(join(webRoot, decodeURIComponent((req.url || '/').split('?')[0].split('#')[0])));
  if (!f.startsWith(webRoot) || !existsSync(f) || statSync(f).isDirectory()) { res.writeHead(404); res.end(); return; }
  res.writeHead(200, { 'content-type': TYPES[extname(f).toLowerCase()] || 'application/octet-stream' }); res.end(readFileSync(f));
}); s.listen(0, '127.0.0.1', () => r(s)); });
const port = server.address().port;
const browser = await puppeteer.launch({ executablePath: findBrowser(), headless: true, args: ['--no-sandbox'] });
const failures = [];
try {
  const page = await browser.newPage();
  page.on('pageerror', (e) => failures.push('pageerror: ' + e.message));
  await page.setViewport({ width: Number(process.env.WIDTH) || 1280, height: 900, deviceScaleFactor: 2 });
  await page.goto(`http://127.0.0.1:${port}/tools/uishot/harness.html?conn=1&live=1&tier=pro&tab=optimize`, { waitUntil: 'networkidle0', timeout: 30000 });
  await page.waitForSelector('nav button', { timeout: 15000 });
  await page.waitForFunction(() => (document.body.textContent || '').includes('Order Share'), { timeout: 15000 });
  for (const id of Object.keys(THEMES)) {
    await applyTheme(page, id);
    await new Promise((r) => setTimeout(r, 300));
    const m = await page.evaluate(() => {
      const note = [...document.querySelectorAll('[data-testid^="pending-suggestion-note-"]')].map((n) => (n.textContent || '').trim());
      const rows = [...document.querySelectorAll('.border-b')].filter((r) => (r.textContent || '').includes('Order Share'));
      const row = rows[rows.length - 1];
      const chips = row ? [...row.querySelectorAll('span')].map((s) => (s.textContent || '').trim()) : [];
      const summary = ((document.body.textContent || '').match(/Jev (\d+)/)?.[1] ?? null) + ' from Jev';
      const box = note[0] ? document.querySelector('[data-testid^="pending-suggestion-note-"]').getBoundingClientRect() : null;
      return { note, chips, summary, noteVisible: !!box && box.width > 100 && box.height > 8 };
    });
    if (m.note.length !== 1 || m.note[0] !== 'Jev suggests this format; check it fits before you run it.') failures.push(`${id}: the suggestion note is not on the row as text: ${JSON.stringify(m.note)}`);
    if (!m.noteVisible) failures.push(`${id}: the note has no visible box`);
    if (!m.chips.includes('suggestion') || m.chips.includes('safe')) failures.push(`${id}: the suggestion chip is wrong: ${JSON.stringify(m.chips)}`);
    if (m.summary !== '2 from Jev') failures.push(`${id}: the summary does not count Jev lines: ${m.summary}`);
    await page.screenshot({ path: join(shots, `pending-suggestion-${id}.png`) });
  }
} finally { await browser.close(); server.close(); }
if (failures.length) { console.error(failures.join('\n')); process.exit(1); }
console.log('pending-suggestion: clean in every theme');
