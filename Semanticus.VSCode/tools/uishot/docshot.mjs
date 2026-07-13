// One-off: screenshot a standalone exported-doc HTML file (the docrender output) for self-review.
import puppeteer from 'puppeteer-core';
import { existsSync, readdirSync } from 'node:fs';
import { homedir } from 'node:os';
import { join } from 'node:path';

function findBrowser() {
  if (process.env.SEMANTICUS_BROWSER && existsSync(process.env.SEMANTICUS_BROWSER)) return process.env.SEMANTICUS_BROWSER;
  const cacheRoot = join(homedir(), '.cache', 'puppeteer', 'chrome-headless-shell');
  if (existsSync(cacheRoot)) for (const v of readdirSync(cacheRoot)) {
    const exe = join(cacheRoot, v, 'chrome-headless-shell-win64', 'chrome-headless-shell.exe');
    if (existsSync(exe)) return exe;
  }
  for (const c of ['C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe', 'C:/Program Files/Google/Chrome/Application/chrome.exe']) if (existsSync(c)) return c;
  throw new Error('No Chromium found.');
}

const [, , htmlPath, outPath] = process.argv;
const browser = await puppeteer.launch({ executablePath: findBrowser(), headless: 'shell', args: ['--no-sandbox'] });
const page = await browser.newPage();
await page.setViewport({ width: 1280, height: 1000, deviceScaleFactor: 1 });
await page.goto('file://' + htmlPath.replace(/\\/g, '/'), { waitUntil: 'networkidle0', timeout: 30000 });
await page.screenshot({ path: outPath, fullPage: true });
await browser.close();
console.log('wrote ' + outPath);
