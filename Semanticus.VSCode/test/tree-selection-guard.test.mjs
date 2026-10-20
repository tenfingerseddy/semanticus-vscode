import assert from 'node:assert/strict';
import { test } from 'node:test';
import { readFileSync } from 'node:fs';
import ts from 'typescript';

function load() {
  const source = readFileSync(new URL('../src/treeSelectionGuard.ts', import.meta.url), 'utf8');
  const { outputText } = ts.transpileModule(source, { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2020 } });
  const module = { exports: {} };
  new Function('module', 'exports', outputText)(module, module.exports);
  return module.exports;
}
const { createTreeSelectionGuard } = load();
const clock = () => { let t = 10_000; return { now: () => t, tick: (ms) => { t += ms; } }; };

test('N10: a rename refresh that empties the selection is not the person', () => {
  const c = clock();
  const g = createTreeSelectionGuard(c.now);
  assert.equal(g.classify(['column:Store/Store City']), 'person');   // the person selected it
  c.tick(5000);
  g.noteRefresh();                                                    // Claude renamed it, the tree refreshed
  c.tick(40);
  assert.equal(g.classify([]), 'refresh');
});

test('N10: the same selection put back right after a refresh is not the person', () => {
  const c = clock();
  const g = createTreeSelectionGuard(c.now);
  g.classify(['column:Store/Store City']);
  g.noteRefresh();
  c.tick(40);
  assert.equal(g.classify([]), 'refresh');
  c.tick(40);
  assert.equal(g.classify(['column:Store/Store City']), 'refresh');
});

test('N10: a real click right after a refresh still takes the wheel', () => {
  const c = clock();
  const g = createTreeSelectionGuard(c.now);
  g.classify(['column:Store/Store City']);
  g.noteRefresh();
  c.tick(40);
  assert.equal(g.classify(['column:Store/Region']), 'person');
});

test('N10: a click on the old object after the quiet window still takes the wheel', () => {
  const c = clock();
  const g = createTreeSelectionGuard(c.now);
  g.classify(['column:Store/Store City']);
  g.noteRefresh();
  c.tick(40);
  g.classify([]);
  c.tick(5000);
  assert.equal(g.classify(['column:Store/Store City']), 'person');
});

test('N10: with no refresh at all, an empty selection is the person clearing it', () => {
  const c = clock();
  const g = createTreeSelectionGuard(c.now);
  g.classify(['column:Store/Store City']);
  assert.equal(g.classify([]), 'person');
});

test('N10: extension.ts routes the tree selection through the guard and the tree refresh feeds it', () => {
  const src = readFileSync(new URL('../src/extension.ts', import.meta.url), 'utf8');
  assert.match(src, /treeSelectionGuard\.classify\(/);
  assert.match(src, /treeSelectionGuard\.noteRefresh\(\)/);
});

test('after a grid rename the tree redraw that drops the selection leaves the grid on the renamed object', () => {
  const { gridStaysOnRedraw } = load();
  assert.equal(typeof gridStaysOnRedraw, 'function', 'the guard names the one redraw the grid ignores');
  assert.equal(gridStaysOnRedraw('refresh', 0), true, 'a redraw that empties the selection must not send the grid to the model');
  assert.equal(gridStaysOnRedraw('person', 0), false, 'the person clearing the selection still shows the model');
  assert.equal(gridStaysOnRedraw('refresh', 1), false, 'a redraw that puts a selection back shows it');
  const src = readFileSync(new URL('../src/extension.ts', import.meta.url), 'utf8');
  assert.match(src, /gridStaysOnRedraw\(/);
});
