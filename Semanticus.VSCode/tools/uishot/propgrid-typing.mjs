// Real keys are essential: setting value in script does not fire change on removal.
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { test } from 'node:test';
import puppeteer from 'puppeteer-core';
import { findBrowser } from './browser.mjs';

for (const update of ['load', 'checkedPending']) {
  test(`typing survives ${update} mid-word and Save writes the whole draft`, async () => {
    const browser = await puppeteer.launch({ executablePath: findBrowser(), headless: true,
      args: ['--no-sandbox', '--disable-dev-shm-usage'] });
    try {
      const page = await browser.newPage();
      await page.setContent('<div id="root"></div>');
      await page.evaluate(() => {
        window.sent = [];
        let held = {};
        window.acquireVsCodeApi = () => ({
          postMessage: m => window.sent.push(m), getState: () => held, setState: s => { held = s; },
        });
      });
      await page.addScriptTag({ content: readFileSync(new URL('../../media/propgrid/propgrid.js', import.meta.url), 'utf8') });
      const model = { type: 'load', model: 'typing.bim', key: '["measure:Sales/Ratio"]', name: 'Ratio',
        props: [{ name: 'Expression', displayName: 'Expression', category: 'Basic', kind: 'string',
          value: '1 // ', multiline: true, options: [], readOnly: false, varies: false }] };
      await page.evaluate(m => dispatchEvent(new MessageEvent('message', { data: m })), model);
      const selector = 'textarea[data-prop="Expression"]';
      await page.click(selector);
      await page.keyboard.press('End');
      await page.keyboard.type('dra', { delay: 120 });
      const verdict = { checkResult: { verdict: 'checked' } };
      await page.evaluate(m => dispatchEvent(new MessageEvent('message', { data: m })),
        update === 'load' ? { ...model, checked: verdict } : { type: update, key: model.key, checked: verdict });
      await page.keyboard.type('ft', { delay: 120 });
      const before = await page.$eval(selector, el => ({
        value: el.value, focused: document.activeElement === el, sets: window.sent.filter(m => m.type === 'set'),
      }));
      assert.deepEqual(before, { value: '1 // draft', focused: true, sets: [] });
      await page.evaluate(() => dispatchEvent(new MessageEvent('message', { data: { type: 'applyFocused', id: 1 } })));
      const sets = await page.evaluate(() => window.sent.filter(m => m.type === 'set'));
      assert.equal(sets.length, 1);
      assert.equal(sets[0].value, '1 // draft');
    } finally { await browser.close(); }
  });
}
