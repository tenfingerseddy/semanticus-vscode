// The start plan's variant of the permission card (spec-first start, finding S2, lane sf-consent).
//
// A new model started from a SQL source is planned from a hidden source, so the person is told which source and which tables Pro
// will see, and whether a sample of rows is used, BEFORE the first ask; and asked again when a table is added. The engine checks the
// same scope on every ask (Semanticus.Tests/StartPlanConsentTests.cs). These static checks keep the card honest the way
// consent-card.test.mjs does for the ratified card: its words, the order of what it does, and that it leaves the ratified card alone.
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const read = (...p) => readFileSync(resolve(...p), 'utf8').replace(/\r\n/g, '\n');
const card = read(root, 'webview', 'src', 'consent.tsx');

// ---- the copy -------------------------------------------------------------------------------------
const start = /export const START_CARD = \{([\s\S]*?)\n\};/.exec(card);
assert.ok(start, 'consent.tsx has no START_CARD the test can read');
const copy = start[1];

const strings = [...copy.matchAll(/(['"`])((?:\\.|(?!\1).)*)\1/g)].map((m) => m[2]);
assert.ok(strings.length >= 10, 'START_CARD carries fewer lines than the variant needs');
for (const line of strings) {
  assert.doesNotMatch(line, /—|–/, `a dash that reads as an em dash is in the card: ${line}`);
  assert.doesNotMatch(line, /one click|one-click/i, `the card promises one click: ${line}`);
  assert.doesNotMatch(line, /[\u{1F300}-\u{1FAFF}\u{2600}-\u{27BF}]/u, `a colourful emoji is in the card: ${line}`);
  assert.doesNotMatch(line, /\bJev\b|\bTOM\b|\bdigest\b|\bscratch\b/i, `the card speaks engine words: ${line}`);
}

assert.match(copy, /title: 'Let Pro see this source\?'/, 'the card does not ask the question it is for');
assert.match(copy, /Also let Pro see \$\{joinNames\(names\)\}\?/, 'the card does not ask "Also let Pro see ...?" when a table is added');
assert.match(copy, /sampleOn: 'It also sees counts and patterns worked out from a sample of rows\. It never sees the rows\.'/,
  'the sample line is not the promised one: counts and patterns, never rows');
assert.match(copy, /never: \[[^\]]*'rows or values from any table'[^\]]*'tables you left out'/s,
  'the card does not say that rows and left-out tables never go');

// ---- what it does, in order -----------------------------------------------------------------------
const agree = /const agreeStartPlan = \(\) => \{([\s\S]*?)\n  \};/.exec(card);
assert.ok(agree, 'the card has no agreeStartPlan');
const body = agree[1];
assert.match(body, /modelCurrent\s*\?\s*Promise\.resolve/s, 'the model\'s own permission is not skipped when it already stands');
assert.match(body, /rpc<ConsentInfoW>\('setConsent', fields, version, 'human', scope \?\? null\)/,
  'the model permission is not given through the same call, with the model the card was raised about');
assert.ok(body.indexOf("'setConsent'") < body.indexOf('(plan ?? startPlan).grant()'),
  'the tables are granted before the model\'s own permission: the engine would refuse, and the order says what the person agreed to first');
assert.doesNotMatch(body, /rpc[<(][^)]*'(grantStartDisclosure|approveStartPlan)'/,
  'the card calls the grant or the approval by name: the page that raises it owns the grant (the digest it was looking at), and the approval is not this card\'s');

// The answer on the other door must not close THIS card: the model's permission being current is not its question.
assert.match(card, /if \(!open \|\| startPlan\) return;[\s\S]{0,200}const off = onDidChange/,
  'the card still closes itself when the model\'s own permission becomes current, which would answer the wrong question');

// ---- the ratified card is left alone ---------------------------------------------------------------
assert.match(card, /title: 'Let Pro decide for this model\?'/, 'the ratified card\'s title moved');
assert.match(card, /ok: 'OK for this model'/, 'the ratified card\'s button moved');
assert.doesNotMatch(card.replace(/export const START_CARD[\s\S]*?\n\};/, ''), /Let Pro see this source/,
  'the start plan\'s words leaked into the ratified card');

console.log('start-plan consent card: ok');
