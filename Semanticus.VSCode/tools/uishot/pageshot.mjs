// Minimal: screenshot a local HTML file to PNG via the cached chrome-headless-shell.
// Usage: node pageshot.mjs <input.html> <output.png> [widthPx]
import puppeteer from 'puppeteer-core';
import { existsSync, readdirSync } from 'node:fs';
import { join } from 'node:path';
import { homedir } from 'node:os';
import { pathToFileURL } from 'node:url';

function findChrome() {
  const root = join(homedir(), '.cache', 'puppeteer', 'chrome-headless-shell');
  if (existsSync(root)) {
    for (const v of readdirSync(root)) {
      const exe = join(root, v, 'chrome-headless-shell-win64', 'chrome-headless-shell.exe');
      if (existsSync(exe)) return exe;
    }
  }
  throw new Error('no chrome-headless-shell');
}

const [inHtml, outPng, width] = process.argv.slice(2);
const browser = await puppeteer.launch({ executablePath: findChrome(), headless: true, args: ['--no-sandbox', '--force-color-profile=srgb'] });
const page = await browser.newPage();
await page.setViewport({ width: Number(width) || 940, height: 900, deviceScaleFactor: 2 });
await page.goto(pathToFileURL(inHtml).href, { waitUntil: 'networkidle0' });
await page.screenshot({ path: outPng, fullPage: true });
await browser.close();
console.log('wrote', outPng);
