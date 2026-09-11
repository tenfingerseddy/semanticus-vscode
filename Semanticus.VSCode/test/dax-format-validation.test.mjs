import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import ts from 'typescript';
import { resolveEditDaxNode } from '../out/editDaxTarget.js';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const read = (file) => readFileSync(resolve(root, file), 'utf8');

async function loadTypeScriptModule(file) {
    const source = readFileSync(file, 'utf8');
    const output = ts.transpileModule(source, {
        compilerOptions: { module: ts.ModuleKind.ESNext, target: ts.ScriptTarget.ES2022 },
        fileName: file,
    }).outputText;
    return import(`data:text/javascript;base64,${Buffer.from(output).toString('base64')}`);
}

const formatMode = await loadTypeScriptModule(resolve(root, 'webview/src/formatMode.ts'));
const daxErrorText = await loadTypeScriptModule(resolve(root, 'webview/src/daxErrorText.ts'));
const daxValidity = await loadTypeScriptModule(resolve(root, 'webview/src/daxValidity.ts'));

let passed = 0;
const test = (name, fn) => { fn(); passed++; console.log('  [PASS] ' + name); };

test('inferFormatMode: empty is static inherit (D-036)', () => {
    assert.equal(formatMode.inferFormatMode(''), 'static');
    assert.equal(formatMode.inferFormatMode('   '), 'static');
    assert.equal(formatMode.inferFormatMode(null), 'static');
});
test('inferFormatMode: a stored expression stays dynamic, including a quoted preset (D-036)', () => {
    assert.equal(formatMode.inferFormatMode('0.0%'), 'dynamic');
    assert.equal(formatMode.inferFormatMode('"0.0%"'), 'dynamic');
    assert.equal(formatMode.inferFormatMode('SELECTEDMEASUREFORMATSTRING()'), 'dynamic');
});

test('plainDaxError strips olii tags (D-073)', () => {
    assert.equal(
        daxErrorText.plainDaxError('The value for column <olii>UAT Missing Measure</olii> cannot be determined.'),
        'The value for column UAT Missing Measure cannot be determined.',
    );
    assert.equal(daxErrorText.plainDaxError('ok'), 'ok');
});

test('validity count can be revealed without hover (D-167)', () => {
    assert.equal(daxValidity.validityLabel(false, true, 0, 1), '1 warning');
    assert.equal(daxValidity.validityCanReveal(false, true, 0, 1), true);
    assert.equal(daxValidity.validityCanReveal(true, true, 0, 1), false);
    assert.equal(daxValidity.validityCanReveal(false, true, 0, 0), false);
});

const daxKinds = new Set(['measure', 'function', 'calcColumn', 'calcitem']);
test('palette Edit DAX with no node uses the tree selection (D-057)', () => {
    assert.equal(resolveEditDaxNode(undefined, undefined, daxKinds), undefined);
    const sel = { ref: 'measure:Sales/M', name: 'M', kind: 'measure' };
    assert.equal(resolveEditDaxNode(undefined, [sel], daxKinds), sel);
    const clicked = { ref: 'measure:Sales/X', name: 'X', kind: 'measure' };
    assert.equal(resolveEditDaxNode(clicked, [sel], daxKinds), clicked);
    assert.equal(resolveEditDaxNode({ ref: 'table:Sales', name: 'Sales', kind: 'table' }, [sel], daxKinds), sel);
});

test('extension Edit DAX command does not dereference a missing node (D-057)', () => {
    const src = read('src/extension.ts');
    assert.match(src, /registerCommand\('semanticus\.editDax',\s*\(n\?: TreeNode\) => editDaxCmd\(n\)\)/);
    assert.match(src, /resolveEditDaxNode/);
    assert.doesNotMatch(src, /registerCommand\('semanticus\.editDax',\s*\(n: TreeNode\) => openDax\(n\)\)/);
});

test('FormatStringControl infers mode from inferFormatMode (D-036)', () => {
    const src = read('webview/src/advmodels.tsx');
    assert.match(src, /inferFormatMode/);
});

test('validity pill is a button when there are issues (D-167)', () => {
    const src = read('webview/src/daxeditor.tsx');
    assert.match(src, /validityCanReveal/);
    assert.match(src, /Show details/);
});

console.log(`dax-format-validation: ${passed} passed`);
