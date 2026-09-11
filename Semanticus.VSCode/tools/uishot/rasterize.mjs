// Rasterize an SVG → PNG in the isolated chrome-headless-shell (same browser the uishot harness uses).
// Usage: node rasterize.mjs <input.svg> <output.png> [sizePx=128] [scale=2]
// Renders the SVG to fill a size×size viewport at deviceScaleFactor=scale, so the PNG is (size*scale)².
import { findBrowser, requireSupportedNode } from './browser.mjs';
import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';

// THE FLOOR CHECK RUNS BEFORE PUPPETEER IS LOADED, AND THE ORDER IS THE WHOLE POINT. This used to be a
// static `import puppeteer from 'puppeteer-core'`, which the runtime resolves, parses and evaluates before
// one line of this file runs. `requireSupportedNode()` therefore could not fire on the Node versions it
// exists for: on Node 20 the run died inside puppeteer with the opaque error the guard was written to
// replace, and the guard's message was never reached. A dynamic import after the check is what makes the
// check reachable. `browser.mjs` imports no puppeteer, so nothing can reorder this again by accident.
requireSupportedNode();
const puppeteer = (await import('puppeteer-core')).default;


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
