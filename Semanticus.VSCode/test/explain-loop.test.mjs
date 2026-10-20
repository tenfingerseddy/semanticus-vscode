// Hero 24 D1 (HARM): a one-number answer started an endless Explain loop, about 3,500 live queries in 13 minutes. The card's request is now
// compared by value and run once; the same request again within a few seconds gets the answer it had. Executed, not read off the source.
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, resolve, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createRequire } from 'node:module';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const req = createRequire(join(root, 'webview', 'package.json'));
const ts = req('typescript');
const file = join(root, 'webview', 'src', 'explainguard.ts');
const module = { exports: {} };
const js = ts.transpileModule(readFileSync(file, 'utf8'), { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 } }).outputText;
new Function('module', 'exports', js)(module, module.exports);
const { explainKey, createExplainGuard, splitOptions } = module.exports;

const payload = () => ({
  measureName: 'Net Sales', value: '39,137,964.25', format: '#,0.00',
  groupBy: [], filters: [{ column: "'Date'[Year]", members: ['2025'] }], extraPredicates: [], entries: [{ field: 'Year', value: '2025', src: 'filters' }],
});

// 1. A new, equal payload object on every render is one request.
assert.equal(explainKey(payload()), explainKey(payload()), 'equal payloads are the same request');
assert.notEqual(explainKey(payload()), explainKey({ ...payload(), filters: [{ column: "'Date'[Year]", members: ['2024'] }] }), 'another context is another request');
assert.equal(explainKey(payload()), explainKey({ ...payload(), value: '1', format: null, entries: [] }), 'the shown value and labels are not part of the request');

// 1b. The model and its revision are part of the request: asked after an edit, the card asks again.
assert.notEqual(explainKey(payload(), { id: 's1', revision: 4 }), explainKey(payload(), { id: 's1', revision: 5 }), 'an edit is a new request');
assert.notEqual(explainKey(payload(), { id: 's1', revision: 4 }), explainKey(payload(), { id: 's2', revision: 4 }), 'another model is a new request');
assert.equal(explainKey(payload(), { id: 's1', revision: 4 }), explainKey(payload(), { id: 's1', revision: 4 }));

// 2. A re-render storm makes one call.
{
  let calls = 0;
  const once = createExplainGuard(3000, () => 1000);
  const work = () => { calls++; return new Promise((done) => setTimeout(() => done({ status: 'ok' }), 20)); };
  const storm = [];
  for (let i = 0; i < 500; i++) storm.push(once(explainKey(payload()), work));
  const results = await Promise.all(storm);
  assert.equal(calls, 1, 'five hundred renders made one call');
  assert.ok(results.every((r) => r === results[0]));
  await once(explainKey(payload()), work);
  assert.equal(calls, 1, 'and a repeat just after the answer makes none');
}

// 3. An old answer is asked again, another request is asked, and a failure is never remembered.
{
  let t = 0; let calls = 0;
  const once = createExplainGuard(3000, () => t);
  const ok = () => { calls++; return Promise.resolve(calls); };
  await once('a', ok); await once('b', ok);
  assert.equal(calls, 2);
  t = 3001;
  assert.equal(await once('a', ok), 3, 'after the window the same request runs again');
  await assert.rejects(once('bad', () => { calls++; return Promise.reject(new Error('no')); }));
  assert.equal(await once('bad', () => { calls++; return Promise.resolve('fine'); }), 'fine', 'a failed request is tried again');
}

// 3b. A dossier that says it failed is shown, then forgotten: pressing the same number again asks again.
{
  let calls = 0;
  const once = createExplainGuard(3000, () => 1000);
  const bad = () => { calls++; return Promise.resolve({ status: 'error' }); };
  await once('k', bad); await once('k', bad);
  assert.equal(calls, 2, 'a failed dossier is asked again');
  const good = () => { calls++; return Promise.resolve({ status: 'ok' }); };
  await once('g', good); await once('g', good);
  assert.equal(calls, 3, 'a good dossier is still served from the window');
}

// 3c. The split the person chose is part of the request; the row of columns is the engine's own pick, kept while they switch.
{
  assert.notEqual(explainKey({ ...payload(), splitBy: "'Product'[Category]" }), explainKey(payload()), 'another split is another request');
  assert.equal(explainKey({ ...payload(), splitBy: null }), explainKey(payload()), 'no chosen split is the engine pick');
  const auto = { dimension: "'Date'[Quarter]", additive: true, alternatives: ["'Product'[Category]", "'Store'[Region]"] };
  assert.deepEqual(splitOptions([], auto), ["'Date'[Quarter]", "'Product'[Category]", "'Store'[Region]"], 'the pick first, then the others');
  const chosen = { dimension: "'Product'[Category]", additive: true, alternatives: [] };
  assert.deepEqual(splitOptions(splitOptions([], auto), chosen), ["'Date'[Quarter]", "'Product'[Category]", "'Store'[Region]"], 'an answer to a chosen column leaves the row as it was');
  assert.deepEqual(splitOptions(["'x'[y]"], { additive: false, alternatives: [] }), ["'x'[y]"], 'no split to offer keeps what was offered');
  assert.deepEqual(splitOptions([], null), [], 'nothing yet, nothing offered');
}

// 4. The page is wired to it: the card keys its effect by the request, and the question's fields are built once per question.
const panel = readFileSync(join(root, 'webview', 'src', 'explainpanel.tsx'), 'utf8');
assert.match(panel, /const key = explainKey\(\{ \.\.\.payload, splitBy \}, model\);/);
assert.match(panel, /explainOnce\(key, \(\) => rpc<ExplainDossier>\('explainValue'/);
assert.match(panel, /\}, \[key\]\);/, 'the effect depends on the request, not on the payload object');
assert.match(panel, /'explainValue', p\.measureName,[\s\S]*?true, splitBy, 5\)/, 'the chosen split goes to the engine');
assert.match(panel, /<SplitBy options=\{opts\}/, 'the row of columns is on the card');
assert.doesNotMatch(panel, /\}, \[payload\]\);/);
const lab = readFileSync(join(root, 'webview', 'src', 'daxlab.tsx'), 'utf8');
assert.match(lab, /const chipConfig = useMemo\(\(\) => \(ask\.parts \? pivotConfigOf\(ask\.parts, ask\.chipDax\) : null\), \[ask\.parts, ask\.chipDax\]\);/);
assert.doesNotMatch(lab, /\? pivotConfigOf\(ask\.parts!, ask\.chipDax\) : null\)\n/, 'no new config object per render');
// 5. The card explains the number the person picked, never row one on its own choice; two columns of one name say their table; no inner scroll box.
assert.match(lab, /const defaultExplain = explainRowCount === 1 && explainChoices\.length === 1 \? explainChoices\[0\] : null;/, 'only an answer of one number is explained without a press');
const qv = readFileSync(join(root, 'webview', 'src', 'query.tsx'), 'utf-8');
assert.match(qv, /data-testid="explain-pick"/, 'an answer of many rows asks which number');
assert.doesNotMatch(qv, /qv-explain-box/, 'the Explain card has no inner scroll box');
assert.doesNotMatch(readFileSync(join(root, 'webview', 'src', 'query.css'), 'utf-8'), /qv-explain-box/);
assert.match(panel, /columnLabel\(o, options\)/, 'the Split by chips tell two columns of one name apart');
assert.match(panel, /Show the query text/, 'the query text of a source sits behind a disclosure');
// 6. A new number is never first asked with the old choice (the choice is stored with its number), and every share shows one decimal.
assert.match(panel, /const splitBy = choice\.cell === cellKey \? choice\.by : null;/, 'a choice belongs to the number it was made on');
assert.doesNotMatch(panel, /useEffect\(\(\) => \{ setSplitBy\(null\)/, 'no reset after the first ask');
assert.match(panel, /r\.pct\.toFixed\(1\)/, 'shares have the same decimals on every row');
assert.match(readFileSync(join(root, 'webview', 'src', 'evidence.tsx'), 'utf8'), /\.toFixed\(1\) \+ '%'/, 'the evidence card too');
console.log('explain-loop: ok');
