// Minimal: screenshot a local HTML file to PNG via the cached chrome-headless-shell.
// Usage: node pageshot.mjs <input.html> <output.png> [widthPx]
import { findBrowser, requireSupportedNode } from './browser.mjs';
import { pathToFileURL } from 'node:url';

// THE FLOOR CHECK RUNS BEFORE PUPPETEER IS LOADED, AND THE ORDER IS THE WHOLE POINT. This used to be a
// static `import puppeteer from 'puppeteer-core'`, which the runtime resolves, parses and evaluates before
// one line of this file runs. `requireSupportedNode()` therefore could not fire on the Node versions it
// exists for: on Node 20 the run died inside puppeteer with the opaque error the guard was written to
// replace, and the guard's message was never reached. A dynamic import after the check is what makes the
// check reachable. `browser.mjs` imports no puppeteer, so nothing can reorder this again by accident.
requireSupportedNode();
const puppeteer = (await import('puppeteer-core')).default;


const [inHtml, outPng, width] = process.argv.slice(2);
const browser = await puppeteer.launch({ executablePath: findBrowser(), headless: true, args: ['--no-sandbox', '--force-color-profile=srgb'] });
const page = await browser.newPage();
await page.setViewport({ width: Number(width) || 940, height: 900, deviceScaleFactor: 2 });
await page.goto(pathToFileURL(inHtml).href, { waitUntil: 'networkidle0' });
await page.screenshot({ path: outPng, fullPage: true });
await browser.close();
console.log('wrote', outPng);
