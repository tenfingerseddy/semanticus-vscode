// Verifier sweep for m11/s10fix B4 (5 Oct). The Incremental refresh card collapsed to 2px because it clipped its content
// inside a fixed-height grid scroll box. This opens every Studio tab through the real bundle and the mock engine, at a
// 1440x900 and a 1280x800 window and at the Studio-tab sizes those windows leave, and flags every card that sits on a
// grid and clips its content while showing less than half of it. It also opens Incremental refresh with Configure.
//
//   OUTDIR=<dir> node tools/uishot/verify-s10fix-b4sweep-drive.mjs
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
const OUT = process.env.OUTDIR || resolve(webRoot, 'shots', 'v-s10fix-b4');
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
const SCRATCH = resolve(process.env.HOME ?? '/tmp', '.cache', 'semanticus-uishot', `v-s10fix-${process.pid}`);
mkdirSync(SCRATCH, { recursive: true });
const browser = await puppeteer.launch({
  executablePath: process.env.SEMANTICUS_BROWSER || findBrowser(), headless: true, protocolTimeout: 180000,
  args: ['--no-sandbox', '--disable-gpu', '--disable-dev-shm-usage', '--force-color-profile=srgb', '--hide-scrollbars', `--user-data-dir=${SCRATCH}`],
  env: { ...process.env, TZ: 'UTC' },
});
const wait = (ms) => new Promise((r) => setTimeout(r, ms));
const SIZES = [
  { name: '1440x900', width: 1440, height: 900 }, { name: '1280x800', width: 1280, height: 800 },
  { name: 'tab1440', width: 1370, height: 800 }, { name: 'tab1280', width: 1210, height: 710 },
];
const TABS = (process.env.TABS || 'plan,open,explore,search,data,diagram,lineage,stats,edit,calcgroups,calendars,fieldparams,perspectives,rls,mcode,spec,spec-sql,daxlab,daxlib,optimize,askdata,review,readiness,bpa,tests,evidence,knowledge,instructions,docs,compare,deploy,publish,production,dataagent,advmodels,workflows,utility,pro').split(',');
const findings = [];
const results = {};

// Every element that sits directly on a grid, clips (overflow other than visible) and shows less than half of what it holds.
const collapsed = () => [...document.querySelectorAll('*')].filter((el) => {
  const parent = el.parentElement; if (!parent) return false;
  if (!/grid/.test(getComputedStyle(parent).display)) return false;
  const cs = getComputedStyle(el);
  if (cs.display === 'none' || cs.visibility === 'hidden') return false;
  if (cs.overflowY === 'visible' && cs.overflowX === 'visible') return false;
  const h = el.getBoundingClientRect().height;
  return el.scrollHeight > 60 && h < el.scrollHeight * 0.5;
}).map((el) => ({ tag: el.tagName.toLowerCase(), cls: String(el.className).slice(0, 80), aria: el.getAttribute('aria-label'),
  height: Math.round(el.getBoundingClientRect().height), scrollHeight: el.scrollHeight, overflow: getComputedStyle(el).overflowY,
  text: (el.textContent || '').trim().slice(0, 60) }));

for (const size of SIZES) for (const tab of TABS) {
  const page = await browser.newPage();
  const errors = [];
  page.on('pageerror', (e) => errors.push(e.message));
  await page.setViewport({ width: size.width, height: size.height, deviceScaleFactor: 1 });
  await page.goto(`http://127.0.0.1:${port}/tools/uishot/harness.html?tab=${tab}&tier=pro&conn=1&live=1`, { waitUntil: 'networkidle0', timeout: 30000 });
  await applyTheme(page, 'dark');
  await wait(1200);
  // Negative control: BREAK=1 puts back the grid rows the base had, so the sweep can be seen to catch the 2px card.
  if (process.env.BREAK) { await page.evaluate(() => { for (const el of document.querySelectorAll('.sem-page')) el.style.gridAutoRows = 'auto'; }); await wait(300); }
  const bad = await page.evaluate(collapsed);
  results[`${tab}@${size.name}`] = bad.length;
  for (const b of bad) findings.push({ tab, size: size.name, ...b });
  if (bad.length) await page.screenshot({ path: join(OUT, `collapsed-${tab}-${size.name}.png`) });
  if (tab === 'mcode') {
    for (const theme of ['dark', 'light']) {
      await applyTheme(page, theme);
      const clicked = await page.evaluate(() => { const b = [...document.querySelectorAll('button')].find((x) => (x.textContent || '').trim().startsWith('Configure') && !x.disabled); b && b.click(); return !!b; });
      await wait(1200);
      const card = await page.evaluate(() => {
        const s = document.querySelector('section[aria-label="Incremental refresh settings"]'); if (!s) return null;
        const r = s.getBoundingClientRect(); const save = s.querySelector('.sem-btn-primary'); const sr = save && save.getBoundingClientRect();
        return { height: Math.round(r.height), scrollHeight: s.scrollHeight, top: Math.round(r.top), saveTop: sr ? Math.round(sr.top) : null, saveBottom: sr ? Math.round(sr.bottom) : null, inner: window.innerHeight };
      });
      results[`mcode-configure@${size.name}-${theme}`] = { clicked, card };
      if (!clicked || !card || card.height + 2 < card.scrollHeight) findings.push({ tab: 'mcode-configure', size: size.name, theme, clicked, card });
      await page.screenshot({ path: join(OUT, `mcode-configure-${size.name}-${theme}.png`) });
    }
  }
  if (errors.length) results[`${tab}@${size.name}-errors`] = errors.slice(0, 3);
  await page.close();
}
await browser.close();
server.close();
writeFileSync(join(OUT, 'results.json'), JSON.stringify({ results, findings }, null, 2));
console.log(JSON.stringify(findings, null, 1));
console.log(findings.length ? `${findings.length} collapsed card(s)` : 'no collapsed card on any tab');
process.exit(findings.length ? 1 : 0);
