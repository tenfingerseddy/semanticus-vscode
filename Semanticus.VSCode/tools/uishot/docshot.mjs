// One-off: screenshot a standalone exported-doc HTML file (the docrender output) for self-review.
import { findBrowser, requireSupportedNode } from './browser.mjs';

// THE FLOOR CHECK RUNS BEFORE PUPPETEER IS LOADED, AND THE ORDER IS THE WHOLE POINT. This used to be a
// static `import puppeteer from 'puppeteer-core'`, which the runtime resolves, parses and evaluates before
// one line of this file runs. `requireSupportedNode()` therefore could not fire on the Node versions it
// exists for: on Node 20 the run died inside puppeteer with the opaque error the guard was written to
// replace, and the guard's message was never reached. A dynamic import after the check is what makes the
// check reachable. `browser.mjs` imports no puppeteer, so nothing can reorder this again by accident.
requireSupportedNode();
const puppeteer = (await import('puppeteer-core')).default;


const [, , htmlPath, outPath] = process.argv;
const browser = await puppeteer.launch({ executablePath: findBrowser(), headless: 'shell', args: ['--no-sandbox'] });
const page = await browser.newPage();
await page.setViewport({ width: 1280, height: 1000, deviceScaleFactor: 1 });
await page.goto('file://' + htmlPath.replace(/\\/g, '/'), { waitUntil: 'networkidle0', timeout: 30000 });
await page.screenshot({ path: outPath, fullPage: true });
await browser.close();
console.log('wrote ' + outPath);
