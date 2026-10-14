// The 1.5 gaps lane (hero20 D10, D11): the words a person reads after Fix all, the Undo beside them, and the Pro page footnote.
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { makeLoader, webview } from './helpers/webview-load.mjs';

const load = makeLoader();
const B = load('bpa-detail.ts');
const src = (name) => readFileSync(join(webview, 'src', name), 'utf8');

let failures = 0;
function check(name, fn) {
  try { fn(); console.log(`ok - ${name}`); } catch (e) { failures++; console.error(`not ok - ${name}\n${e.stack}`); }
}

check('D11: the result counts the same thing the button named, then the changes', () => {
  // The hero press: "Fix 119", 24 left over, 143 changes made.
  assert.equal(B.fixAllLine(119, 143, 24), 'Fixed 95 issues (143 changes).');
  assert.equal(B.fixAllLine(119, 119, 0), 'Fixed 119 issues.');
  assert.equal(B.fixAllLine(1, 1, 0), 'Fixed 1 issue.');
  assert.equal(B.fixAllLine(5, 0, 5), 'Fix all changed nothing.');
  assert.equal(B.fixAllLine(3, 4, 3), 'Made 4 changes.');
  // The verifier's probe (v-gaps20): a Double column. The button says Fix 4, the press clears all 4 (the Double to Decimal finding is
  // withheld and was never on the button), 5 changes were made: the line counts what the button counted.
  assert.equal(B.fixAllLine(4, 5, 0), 'Fixed 4 issues (5 changes).');
  // and the page passes the NEW scorecard's auto-fixable count as "left", not the length of every unfixed finding
  assert.match(readFileSync(new URL('../webview/src/bpa.tsx', import.meta.url), 'utf8'), /const left = r\?\.scorecard \? r\.scorecard\.autoFixable :/u);
  assert.doesNotMatch(B.fixAllLine(119, 143, 24), /fixes|—/u);
});

check('D11: Review shows Undo after Fix all, and takes it away once the model changes again', () => {
  const bpa = src('bpa.tsx');
  assert.match(bpa, /data-testid="bpa-fixall-undo"/u, 'the Undo button is on the page');
  assert.match(bpa, /\{undoAt != null && <button/u, 'and only while the press is the latest change');
  assert.match(bpa, /rpc\('undo', 'human'\)/u, 'it takes the press back as the person');
  assert.match(bpa, /\(n\?\.revision \?\? 0\) > undoRef\.current\) setUndoAt\(null\)/u, 'a newer change removes it');
  assert.doesNotMatch(bpa, /Fix all made/u);
});

check('D10: the Pro page footnote is true for a model the assistant opens', () => {
  const consent = src('consent.tsx');
  assert.match(consent, /const FOOTNOTE = 'Each model you open asks you once, whoever opens it\.';/u);
  assert.doesNotMatch(consent, /asks you again, on its own/u);
  assert.doesNotMatch(consent, /FOOTNOTE = '[^']*—/u);
});

check('D10: Studio asks for the open model at once, and after the assistant opens one', () => {
  const consent = src('consent.tsx');
  assert.match(consent, /export function useAskForOpenModel\(\)/u);
  assert.match(consent, /onReconnect\(\(by\) => \{ if \(by === 'otherDoor'\) ask\(\); \}\)/u);
  assert.match(src('decisions-settings.tsx'), /useAskForOpenModel\(\);\s*return <ConsentCard \/>/u, 'mounted once, above the pages');
  // It only asks. The one grant call lives behind the card's OK and the Allow button, never in this path.
  const hook = consent.slice(consent.indexOf('export async function raiseConsentCardForOpenModel'), consent.indexOf('export function useConsentCard'));
  assert.doesNotMatch(hook, /setConsent/u);
});

if (failures) { console.error(`${failures} failed`); process.exit(1); }
