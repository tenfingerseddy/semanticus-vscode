// Renders docs/semanticus-synopsis.html in headless Chromium and writes PNGs for design review.
// Usage: node shoot-synopsis.mjs
import { chromium } from 'playwright';
import { pathToFileURL } from 'node:url';
import path from 'node:path';

const file = pathToFileURL(path.resolve('../../docs/semanticus-synopsis.html')).href;
const OUT = process.env.OUT || '.';
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

const browser = await chromium.launch();

// Desktop — full page + above-the-fold
const desk = await browser.newPage({ viewport: { width: 1280, height: 900 }, deviceScaleFactor: 1 });
await desk.goto(file); await sleep(400);
await desk.screenshot({ path: `${OUT}/synopsis-fold.png` });
await desk.screenshot({ path: `${OUT}/synopsis-full.png`, fullPage: true });
const h = await desk.evaluate(() => document.body.scrollHeight);
console.log('desktop full height (px):', h);

// Crisp element shots of the sections under review
const hi = await browser.newPage({ viewport: { width: 1280, height: 900 }, deviceScaleFactor: 2 });
await hi.goto(file); await sleep(400);
for (const [sel, name] of [['.versus','sec-versus'], ['.dd','sec-dualdrive'], ['.caps','sec-caps'], ['.scorewrap','sec-scorecard'], ['.road','sec-roadmap']]) {
  const el = hi.locator(sel).first();
  await el.scrollIntoViewIfNeeded();
  await el.screenshot({ path: `${OUT}/${name}.png` }).catch((e) => console.log('skip', name, e.message));
}

// Mobile — responsive check
const mob = await browser.newPage({ viewport: { width: 414, height: 900 }, deviceScaleFactor: 1 });
await mob.goto(file); await sleep(400);
await mob.screenshot({ path: `${OUT}/synopsis-mobile.png`, fullPage: true });

await browser.close();
console.log('wrote synopsis-fold.png, synopsis-full.png, synopsis-mobile.png to', OUT);
