import { findBrowser, requireSupportedNode } from './browser.mjs';
import { readFileSync } from 'node:fs';

// THE FLOOR CHECK RUNS BEFORE PUPPETEER IS LOADED, AND THE ORDER IS THE WHOLE POINT. This used to be a
// static `import puppeteer from 'puppeteer-core'`, which the runtime resolves, parses and evaluates before
// one line of this file runs. `requireSupportedNode()` therefore could not fire on the Node versions it
// exists for: on Node 20 the run died inside puppeteer with the opaque error the guard was written to
// replace, and the guard's message was never reached. A dynamic import after the check is what makes the
// check reachable. `browser.mjs` imports no puppeteer, so nothing can reorder this again by accident.
requireSupportedNode();
const puppeteer = (await import('puppeteer-core')).default;

const svg = readFileSync(process.argv[2],'utf8');
const out = process.argv[3];
const size = Number(process.argv[4]||128);
const html = `<!doctype html><meta charset=utf8><style>html,body{margin:0;padding:0;background:transparent}div{width:${size}px;height:${size}px}svg{width:100%;height:100%;display:block}</style><div>${svg}</div>`;
const b = await puppeteer.launch({ executablePath: findBrowser(), headless:'shell', args:['--no-sandbox','--force-color-profile=srgb'] });
const p = await b.newPage();
await p.setViewport({ width:size, height:size, deviceScaleFactor:1 });
await p.setContent(html, { waitUntil:'networkidle0' });
await p.screenshot({ path: out, omitBackground:true, clip:{x:0,y:0,width:size,height:size} });
await b.close();
console.log('wrote',out);
