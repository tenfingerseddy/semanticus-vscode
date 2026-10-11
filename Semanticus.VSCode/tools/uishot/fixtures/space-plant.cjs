// Verifier's plant for space-r2-drive.mjs (round 2): loaded into a real visual-check.mjs run with
// `node --require`, it plants one fault into every Studio page that run opens, so the proof exercises the real
// scoreboard and not a copy of its loop. The ratified design says a scene that cannot be measured "is reported as
// not measured, never as a pass".
//   PLANT=hide-notes   the AI instructions page's "Notes" switch is hidden, so the instructions-notes scene's own
//                      click finds nothing. seg() in visual-scenes.mjs ignores that, and the scene then measures the
//                      Instructions tab and scores it as the Notes tab.
//   PLANT=page-error   the Studio page throws once it has loaded. The screen family reports page errors; the space
//                      loop in visual-check.mjs never reads them.
const { createRequire } = require('node:module');
const path = require('node:path');
const req = createRequire(path.join(__dirname, '..', 'visual-scenes.mjs'));
const pp = req('puppeteer-core');
const target = pp.default ?? pp;
const mode = process.env.PLANT || '';
const plant = (m) => {
  if (!location.pathname.endsWith('/harness.html')) return;
  if (m === 'hide-notes') {
    const hide = () => { for (const b of document.querySelectorAll('button')) if (b.textContent.trim() === 'Notes') b.style.setProperty('display', 'none', 'important'); };
    new MutationObserver(hide).observe(document, { subtree: true, childList: true, characterData: true });
  }
  if (m === 'page-error') window.addEventListener('load', () => setTimeout(() => { throw new Error('planted page error'); }, 0));
};
const launch = target.launch.bind(target);
target.launch = async (...args) => {
  const browser = await launch(...args);
  const newPage = browser.newPage.bind(browser);
  browser.newPage = async (...a) => {
    const page = await newPage(...a);
    if (mode) await page.evaluateOnNewDocument(plant, mode);
    return page;
  };
  return browser;
};
