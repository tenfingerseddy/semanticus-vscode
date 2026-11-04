// cx2: before (left) and after (right) sheets of the Connections bar shots, one per theme, width and half of the scenes.
//   PAIRS=<shots/pairs> OUT=<shots/sheets> node tools/uishot/cx2-sheet.mjs
import { mkdirSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import { createRequire } from 'node:module';
import { findBrowser } from './browser.mjs';
const require_ = createRequire(import.meta.url);
const puppeteer = require_('puppeteer-core').default ?? require_('puppeteer-core');
const PAIRS = process.env.PAIRS, OUT = process.env.OUT;
mkdirSync(OUT, { recursive: true });
const scenes = ['list', 'account', 'workspace', 'opening', 'cancelled', 'ws-opening'];
const uri = (f) => 'data:image/png;base64,' + readFileSync(join(PAIRS, f)).toString('base64');
const browser = await puppeteer.launch({ executablePath: findBrowser(), headless: true, args: ['--no-sandbox', `--user-data-dir=${join(OUT, '..', '..', 'tmp', 'sheet-chrome')}`] });
for (const theme of ['dark', 'light']) for (const w of ['1280', '390']) for (const [part, group] of [[1, scenes.slice(0, 3)], [2, scenes.slice(3)]]) {
  const scale = w === '1280' ? 0.5 : 0.62;
  const rows = group.map((sc) => `<div class="r"><div><p>${sc} ${theme} ${w}: BEFORE (fix/cx1)</p><img style="zoom:${scale}" src="${uri(`before-${sc}-${theme}-${w}.png`)}"></div><div><p>AFTER (fix/cx2)</p><img style="zoom:${scale}" src="${uri(`after-${sc}-${theme}-${w}.png`)}"></div></div>`).join('');
  const page = await browser.newPage();
  await page.setViewport({ width: w === '1280' ? 1330 : 560, height: 400 });
  await page.setContent(`<style>body{margin:0;background:#777;font:13px sans-serif}.r{display:flex;gap:16px;padding:6px}p{margin:2px 0;color:#fff}img{display:block;border:1px solid #000}</style>${rows}`);
  await page.screenshot({ path: join(OUT, `${theme}-${w}-${part}.png`), fullPage: true });
  await page.close();
}
await browser.close();
console.log('sheets ok');
