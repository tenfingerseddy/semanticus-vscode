import puppeteer from 'puppeteer-core';
import { readFileSync, existsSync, readdirSync } from 'node:fs';
import { homedir } from 'node:os';
import { join } from 'node:path';
function findBrowser(){ const r=join(homedir(),'.cache','puppeteer','chrome-headless-shell'); for(const v of readdirSync(r)){const e=join(r,v,'chrome-headless-shell-win64','chrome-headless-shell.exe'); if(existsSync(e))return e;} throw new Error('no chromium'); }
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
