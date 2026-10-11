// Join 6, J2c (v-uat-ui-4 note c): "Apply mine anyway" sends the editor's CURRENT text, not the first apply's. The text it picks is executed (the pure
// helper in src/scriptResend.ts); the wiring in extension.ts (both Apply commands hand the editor's document on, and the button sends what the helper
// picks, for the request and for the result it reports) is pinned in source, the way the rest of the extension's host code is.
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, resolve, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createRequire } from 'node:module';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const req = createRequire(join(root, 'webview', 'package.json'));
const ts = req('typescript');
const helperSrc = readFileSync(join(root, 'src', 'scriptResend.ts'), 'utf8');
const module = { exports: {} };
new Function('module', 'exports', ts.transpileModule(helperSrc, { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 } }).outputText)(module, module.exports);
const { scriptToResend } = module.exports;

const open = (text) => ({ isClosed: false, getText: () => text });
assert.equal(scriptToResend(open('// @object measure:T/A\n20\n'), '// @object measure:T/A\n10\n'), '// @object measure:T/A\n20\n', 'an open editor: what it holds now');
assert.equal(scriptToResend({ isClosed: true, getText: () => { throw new Error('closed'); } }, 'first text'), 'first text', 'a closed editor: the text that was sent');
assert.equal(scriptToResend({ isClosed: false, getText: () => { throw new Error('gone'); } }, 'first text'), 'first text', 'an editor that cannot be read: the text that was sent');
assert.equal(scriptToResend(undefined, 'first text'), 'first text', 'no editor given: the text that was sent');

const ext = readFileSync(join(root, 'src', 'extension.ts'), 'utf8');
assert.match(ext, /import \{ scriptToResend, type ScriptSource \} from '\.\/scriptResend'/, 'extension imports the helper');
const report = ext.slice(ext.indexOf('async function reportScriptApply'), ext.indexOf('async function applyTmdlScriptCmd'));
assert.match(report, /const now = scriptToResend\(source, script\)/, 'Apply mine anyway reads the editor now');
assert.match(report, /sendRequest<ScriptApplyResult>\(method, now, 'human', c, sessionId \?\? null\)/, 'and sends that text, not the first call\'s');
assert.doesNotMatch(report, /sendRequest<ScriptApplyResult>\(method, script,/, 'the first call\'s text is never re-sent');
assert.match(report, /reportScriptApply\(kind, method, now, /, 'a second conflict offers the same button on the newer text');
assert.match(ext, /applyTmdlScript', script, 'human', null, sessionId \?\? null\), ed\.document, sessionId\)/, 'Apply TMDL hands over its editor');
assert.match(ext, /applyDaxScript', script, 'human', null, sessionId \?\? null\), ed\.document, sessionId\)/, 'Apply DAX hands over its editor');
assert.match(report, /Nothing to write: the model already holds everything in this script\./, 'a script that wrote nothing and says nothing is not reported as "Applied 0"');

console.log('script-resend: all checks passed');
