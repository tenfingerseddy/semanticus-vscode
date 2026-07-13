// Rasterize an SVG → PNG in the isolated chrome-headless-shell (same browser the uishot harness uses).
// Usage: node rasterize.mjs <input.svg> <output.png> [sizePx=128] [scale=2]
// Renders the SVG to fill a size×size viewport at deviceScaleFactor=scale, so the PNG is (size*scale)².
import puppeteer from 'puppeteer-core';
import { existsSync, readFileSync, readdirSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { homedir } from 'node:os';

function findBrowser() {
  if (process.env.SEMANTICUS_BROWSER && existsSync(process.env.SEMANTICUS_BROWSER)) return process.env.SEMANTICUS_BROWSER;
  const cacheRoot = join(homedir(), '.cache', 'puppeteer', 'chrome-headless-shell');
  if (existsSync(cacheRoot)) {
    for (const v of readdirSync(cacheRoot)) {
      const exe = join(cacheRoot, v, 'chrome-headless-shell-win64', 'chrome-headless-shell.exe');
      if (existsSync(exe)) return exe;
    }
  }
  for (const c of ['C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe',
                   'C:/Program Files/Google/Chrome/Application/chrome.exe']) if (existsSync(c)) return c;
  throw new Error('No Chromium found. Run `npm install` in tools/uishot, or set SEMANTICUS_BROWSER.');
}

const [, , inSvg, outPng, sizeArg, scaleArg] = process.argv;
if (!inSvg || !outPng) { console.error('usage: node rasterize.mjs <in.svg> <out.png> [size=128] [scale=2]'); process.exit(2); }
const size = Number(sizeArg) || 128;
const scale = Number(scaleArg) || 2;
const svg = readFileSync(resolve(inSvg), 'utf8');

const browser = await puppeteer.launch({ executablePath: findBrowser(), headless: 'shell', args: ['--no-sandbox', '--force-color-profile=srgb'] });
try {
  const page = await browser.newPage();
  await page.setViewport({ width: size, height: size, deviceScaleFactor: scale });
  // Strip any width/height on the root <svg> so it fills the viewport box exactly.
  const fit = svg.replace(/<svg([^>]*?)\swidth="[^"]*"/, '<svg$1').replace(/<svg([^>]*?)\sheight="[^"]*"/, '<svg$1')
                 .replace('<svg', `<svg width="${size}" height="${size}"`);
  await page.setContent(`<!doctype html><html><head><style>*{margin:0;padding:0}html,body{width:${size}px;height:${size}px;background:transparent}</style></head><body>${fit}</body></html>`, { waitUntil: 'networkidle0' });
  await page.screenshot({ path: resolve(outPng), omitBackground: true, clip: { x: 0, y: 0, width: size, height: size } });
  console.log(`rasterize: ${inSvg} -> ${outPng} (${size * scale}×${size * scale}px)`);
} finally { await browser.close(); }
