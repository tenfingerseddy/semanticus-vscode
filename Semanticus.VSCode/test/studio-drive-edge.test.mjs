import assert from 'node:assert/strict';
import { test } from 'node:test';
import { readFileSync } from 'node:fs';
import { createRequire } from 'node:module';
import ts from 'typescript';

const require = createRequire(new URL('../webview/package.json', import.meta.url));
function load(name, link = {}) {
  const source = readFileSync(new URL(`../webview/src/studio/${name}`, import.meta.url), 'utf8');
  const { outputText } = ts.transpileModule(source, { compilerOptions: {
    module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2020, jsx: ts.JsxEmit.ReactJSX,
  } });
  const module = { exports: {} };
  new Function('require', 'module', 'exports', outputText)((id) => link[id] ?? require(id), module, module.exports);
  return module.exports;
}
const storeModule = load('driver-store.ts');
const edge = load('drive-edge.tsx', { './driver-store': storeModule, './assistant-working': { useAssistantWorking: () => false } });
const { renderToStaticMarkup } = require('react-dom/server');
const css = readFileSync(new URL('../webview/src/studio/studio-cursor.css', import.meta.url), 'utf8');
const sourceText = readFileSync(new URL('../webview/src/studio/drive-edge.tsx', import.meta.url), 'utf8');

const at = (ms) => new Date(ms).toISOString();
// What the page draws, from the same store the driver bar reads: the phase after each change. "working" is the tile's own question.
function watch(store, now) {
  let phase = 'off';
  const seen = [];
  const step = () => {
    const snapshot = store.getSnapshot();
    phase = edge.nextEdgePhase(phase, edge.edgeIsOn(snapshot, storeModule.assistantIsWorking(snapshot, now())));
    seen.push(phase);
  };
  store.subscribe(step);
  step();
  return seen;
}

test('the line is on while the assistant works and drives, and off on handback, idle and no assistant', () => {
  let now = 1000;
  const s = storeModule.createDriverStore('claudeDrives');
  const on = () => edge.edgeIsOn(s.getSnapshot(), storeModule.assistantIsWorking(s.getSnapshot(), now));
  assert.equal(on(), false, 'no assistant connected: no line');
  s.setConnected(true);
  assert.equal(on(), false, 'connected but nothing has called: no line');
  s.noteAssistantCall(at(now));
  assert.equal(on(), true, 'the assistant is working: the line shows');
  now += storeModule.QUIET_GAP_MS + 1;
  assert.equal(on(), false, 'idle after a quiet gap: the line goes');
  s.noteAssistantCall(at(now));
  assert.equal(on(), true, 'working again');
  s.notePersonInput(now);
  assert.equal(on(), false, 'a click took the wheel');
  s.letClaudeDrive();
  s.noteAssistantCall(at(now));
  assert.equal(on(), true, 'Let Claude drive gives it back');
  s.setMode('iDrive');
  assert.equal(on(), false, 'I drive');
  s.setMode('claudeDrives');
  assert.equal(on(), true);
  assert.equal(edge.edgeIsOn(s.getSnapshot(), false), false, 'the tile is still: the line is gone');
});

test('fades in on work, fades out on handback, and a repeated state does nothing', () => {
  let now = 1000;
  const s = storeModule.createDriverStore('claudeDrives');
  const seen = watch(s, () => now);
  s.setConnected(true);
  s.noteAssistantCall(at(now));
  s.noteShow();
  s.noteShow();
  assert.deepEqual(seen.slice(-3), ['on', 'on', 'on']);
  s.notePersonInput(now);
  assert.equal(seen.at(-1), 'fade');
  s.notePersonInput(now + 1);
  assert.equal(seen.at(-1), 'fade', 'the same handback twice does not restart the fade');
  assert.equal(edge.nextEdgePhase('off', false), 'off');
  assert.equal(edge.nextEdgePhase('fade', true), 'on', 'work again while it fades shows the line again');
});

test('the overlay is invisible to the pointer, so a click anywhere in the Studio reaches its target', () => {
  assert.equal(renderToStaticMarkup(edge.DriveEdgeView({ phase: 'off' })), '');
  for (const phase of ['on', 'fade']) {
    const html = renderToStaticMarkup(edge.DriveEdgeView({ phase }));
    assert.match(html, /data-testid="studio-drive-edge"/);
    assert.match(html, new RegExp(`data-edge="${phase}"`));
    assert.match(html, /pointer-events:none/);
    assert.match(html, /aria-hidden="true"/);
    // A click is judged by isPersonInput on the element underneath, and the edge sits on no studio control.
    assert.doesNotMatch(html, /onclick|tabindex|<button/i);
  }
});

test('motion rules: one 1px line, opacity only, tokens only, reduced motion keeps the still line, no timers', () => {
  const block = css.slice(css.indexOf('.sc-edge {'));
  assert.match(block, /\.sc-edge \{[^}]*border: 1px solid var\(--sem-accent\)/);
  assert.doesNotMatch(block, /#[0-9a-f]{3,8}\b|rgb\(|transition|2px/i);
  const rest = Number('0.' + block.match(/\.sc-edge \{[^}]*opacity: \.(\d+)/)[1]) * 100;
  assert.ok(rest >= 35 && rest <= 45, `rest strength ${rest}`);
  const breathe = block.match(/@keyframes sc-edge-breathe \{(.*)\}/)[1];
  assert.match(block, /sc-edge-breathe 3\.[0-9]s ease-in-out 300ms infinite/, 'a slow breath of about 3.6 s');
  const levels = [...breathe.matchAll(/opacity: \.(\d+)/g)].map((m) => Number('0.' + m[1]) * 100);
  assert.ok(Math.min(...levels) >= 25 && Math.max(...levels) <= 50, `breath ${levels}`);
  for (const [, body] of block.matchAll(/@keyframes [\w-]+ \{(.*)\}/g)) assert.doesNotMatch(body.replace(/opacity:\s*\.?\d+/g, ''), /[a-z-]+\s*:/, 'only opacity is animated');
  assert.match(block, /@media \(prefers-reduced-motion: reduce\) \{ \.sc-edge \{ animation: none/);
  assert.match(block, /body\.vscode-reduce-motion \.sc-edge \{ animation: none/);
  assert.doesNotMatch(sourceText, /setTimeout|setInterval|requestAnimationFrame|—/);
  const layer = readFileSync(new URL('../webview/src/studio/layer.tsx', import.meta.url), 'utf8');
  assert.match(layer, /<DriveEdge \/>/, 'the layer mounts the line');
});
