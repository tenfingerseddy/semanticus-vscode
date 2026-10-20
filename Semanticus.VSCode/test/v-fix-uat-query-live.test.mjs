// v-fix-uat-query verifier (18 Oct 2026), on m16/fix-uat-query 88bf6c1e. A real `claude -p` in the connected folder asked "what were
// total net sales in 2025, just the one number". The assistant ran EVALUATE ROW("Total2025", CALCULATE([Net Sales], 'Date'[Year] = 2025))
// and answered 39,137,964.25 in chat (direct DAX equal). The Studio folded that run under "Your assistant checked 1 thing" and kept the
// older card open, because every ROW(...) query counts as a check (PROBE_DAX). The person's answer was hidden behind a fold that says it
// was only a check; the brief asked that "the card the assistant just made is the one open". A one-value ROW is the usual shape of a
// one-number answer; the checks the UAT saw (F57: Total 2024, All years, Blank brand) carried several values side by side.
// Evidence: /tmp/semanticus-joint-review/m16/v-fix-uat-query/shots/k119-after-claude4.png, k120-total2025-open.png; claude/c4.jsonl.
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, resolve, join } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { createRequire } from 'node:module';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const webview = join(root, 'webview');
const req = createRequire(join(webview, 'package.json'));
const ts = req('typescript');
const file = join(webview, 'src', 'query-notebook-model.ts');
const js = ts.transpileModule(readFileSync(file, 'utf8'), { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 }, fileName: 'm.ts' }).outputText;
const mod = { exports: {} };
const stub = () => new Proxy({}, { get: () => () => undefined });
new Function('require', 'module', 'exports', js)((n) => { try { return req(n); } catch { return stub(); } }, mod, mod.exports);
const { isProbeDax } = mod.exports;

const answer = 'EVALUATE ROW("Total2025", CALCULATE([Net Sales], \'Date\'[Year] = 2025))';
const check = 'EVALUATE ROW("Total2024", CALCULATE([Net Sales], \'Date\'[Year] = 2024), "SumOfBrands", SUMX(VALUES(\'Product\'[Brand]), CALCULATE([Net Sales], \'Date\'[Year] = 2024)))';
assert.equal(isProbeDax(check), true, 'a cross-check of two values side by side stays a check');
assert.equal(isProbeDax(answer), false, 'the assistant\'s one-number answer was folded as a check and never opened (live, 39,137,964.25)');

// Round 3: the month axis drew September and December in full and they touched; they are drawn as three letters.
const { axisLabelText } = await import(pathToFileURL(join(webview, 'src', 'periodorder.mjs')).href);
assert.equal(axisLabelText('September'), 'Sep');
assert.equal(axisLabelText('December'), 'Dec');
assert.equal(axisLabelText('May'), 'May');
assert.equal(axisLabelText('2024-09'), '2024-09');
assert.equal(axisLabelText('Western'), 'Western', 'only a month name is shortened');
assert.equal(axisLabelText('Marketing'), 'Marketing');
assert.equal(axisLabelText(null), '');
console.log('v-fix-uat-query-live: ok');
