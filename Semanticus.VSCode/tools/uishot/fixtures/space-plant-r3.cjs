// Verifier's plant for space-r3v-drive.mjs (round 3): loaded into a real visual-check.mjs run with `node --require`,
// it plants one fault into every Studio page that run opens, so the proof exercises the real scoreboard.
//   PLANT=rename-edit   the AI instructions card's "Edit" button reads "Change" instead, the way a copy change would
//                       rename it. The edit-instructions-editing scene finds its button by the exact word "Edit" and
//                       ignores a miss, so it then measures the card's read view and scores it as the editing state.
//                       (Hiding the button would not do: element.click() still fires on a hidden button.)
const { createRequire } = require('node:module');
const path = require('node:path');
const req = createRequire(path.join(__dirname, '..', 'visual-scenes.mjs'));
const pp = req('puppeteer-core');
const target = pp.default ?? pp;
const mode = process.env.PLANT || '';
const plant = (m) => {
  if (!location.pathname.endsWith('/harness.html')) return;
  if (m === 'rename-edit') {
    const rename = () => {
      for (const b of document.querySelectorAll('[data-testid="overview-instructions"] button')) {
        if (b.textContent.trim() === 'Edit') b.textContent = 'Change';
      }
    };
    new MutationObserver(rename).observe(document, { subtree: true, childList: true, characterData: true });
  }
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
