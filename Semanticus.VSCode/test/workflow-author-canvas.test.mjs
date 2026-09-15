import assert from 'node:assert/strict';
import test from 'node:test';
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

// =====================================================================================================
// THE AUTHOR CANVAS IS THE PAGE, NOT A 340px BOX.
//
// Kane, on the Yoga, 2026-09-15: "The canvas editor is too small to be useful. need to rethink layout
// and maximise canvas editor size", and "ideally fullscreen canvas". Measured before this change at
// 1366x768 in the built bundle: the canvas host was 569 by 340 with the step editor holding the right
// half of the page whether or not a step was selected, and four stacked header rows above it.
//
// Every assertion below names one thing that must not silently go back. What they prove is that the
// source and the stylesheet still declare the layout; that it MEASURES right in the built app is
// Semanticus.VSCode/tools/drive-author-canvas.mjs, which drives real clicks at three viewports.
// =====================================================================================================
const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const read = (file) => readFileSync(resolve(root, file), 'utf8');
const designer = read('webview/src/workflowdesign.tsx');
const canvas = read('webview/src/workflowcanvas.tsx');
const workflows = read('webview/src/workflows.tsx');
const css = read('webview/src/styles.css');

// The stylesheet is one file for the whole webview, so every rule assertion below is anchored to a
// selector rather than to a line, and the workflow block is sliced out first.
const block = (selector) => {
  const at = css.indexOf(selector);
  assert.ok(at >= 0, `styles.css must carry a rule for ${selector}`);
  return css.slice(at, css.indexOf('}', at) + 1);
};

test('the canvas host has no fixed height and fills its parent', () => {
  assert.doesNotMatch(canvas, /height:\s*340/,
    'the canvas must not carry a fixed 340px height: that is the box Kane called too small to be useful');
  assert.doesNotMatch(canvas, /style=\{\{\s*height:\s*\d/,
    'the canvas host must take its height from the layout, never from an inline pixel number');
  assert.match(canvas, /className="sem-wf-canvas"[^>]*data-wf-canvas="true"|data-wf-canvas="true"[^>]*className="sem-wf-canvas"/,
    'the canvas host must keep data-wf-canvas (the harness targets it) and carry the class the stylesheet sizes');
  assert.match(block('.sem-wf-canvas {'), /flex:\s*1/,
    'the canvas host must grow into the space its parent has');
  assert.match(block('.sem-wf-canvas {'), /min-height:\s*0/,
    'without min-height:0 a flex child refuses to shrink and the page grows a scrollbar instead');
  assert.match(block('.sem-wf-canvas-stage {'), /flex:\s*1/,
    'the stage that holds the canvas and the drawer must take the rest of the page height');
  assert.match(block('.sem-wf-canvas-stage {'), /position:\s*relative/,
    'the stage must be the positioning parent, or the drawer cannot overlay it at narrow widths');
  assert.match(block('.sem-wf-author-page {'), /min-height:\s*100%/,
    'the Author page must be at least as tall as the scroll region, or flex:1 below it has nothing to fill');
  assert.match(workflows, /sem-wf-author-page/,
    'the Author section must mount inside the full-height page container');
});

test('the tools are chips inside the canvas, in the named order', () => {
  const tools = canvas.slice(canvas.indexOf('sem-wf-canvas-tools'), canvas.indexOf('sem-wf-canvas-tools') + 2600);
  const order = ['+ Add step', 'Arrange', 'Reset', 'Fit all', 'Full screen'];
  let at = -1;
  for (const label of order) {
    const next = tools.indexOf(label, at + 1);
    assert.ok(next > at, `the canvas tool chips must read ${order.join(' \u00b7 ')}; ${label} is missing or out of order`);
    at = next;
  }
  assert.match(block('.sem-wf-canvas-tools {'), /position:\s*absolute/,
    'the tools must float inside the canvas, not sit in a row above it that steals height');
  assert.match(canvas, /const chip = 'sem-btn sem-btn-sm'/,
    'the tool chips must use the shared 24px control, so a control restyle reaches this surface too');
  assert.equal((tools.match(/className=\{chip\}/g) || []).length, order.length,
    'every one of the five tool chips must be that shared control, not four of five');
});

test('the Author header is one row', () => {
  assert.match(designer, /className="sem-wf-author-head"/,
    'the Author header must be one named row, not a Panel of stacked notices');
  assert.match(block('.sem-wf-author-head {'), /flex-wrap:\s*nowrap/,
    'the Author header must never wrap to a second row');
  assert.match(block('.sem-wf-author-head {'), /min-height:\s*40px/,
    'the Author header is one 40px row on the shared control scale');
  assert.doesNotMatch(designer, /sem-wf-stock-note/,
    'the built-in explanation sentence moves into the Copy button tooltip; it is not a row of its own');
  assert.match(designer, /title=\{[^}]*STOCK_COPY_WHY|STOCK_COPY_WHY\}/,
    'the explanation must survive as the tooltip on Copy to this project, not just be deleted');
});

test('the editor renders only when a step is selected or pinned', () => {
  assert.match(designer, /const drawerOpen =/,
    'whether the drawer exists must be one named decision, not a condition repeated at each use');
  assert.match(designer, /\{drawerOpen && [\s\S]{0,3200}<WorkflowStepForm/,
    'nothing selected must mean no drawer: the canvas has the whole page until a step is picked');
  assert.match(designer, /pinned \? /,
    'Pin must keep the drawer open when the selection clears');
  // Caught by tools/drive-author-canvas.mjs, not by reading: Pin had nothing to hold the drawer open
  // AGAINST, because clicking the canvas background did not clear the selection at all.
  assert.match(canvas, /onPaneClick=\{\(\) => onClear\?\.\(\)\}/,
    'clicking the canvas background must clear the selection, or Pin guards against nothing');
  assert.match(designer, /onClear=\{\(\) => setPick\(null\)\}/,
    'clearing the selection must leave Pin to decide whether the drawer stays');
});

test('the drawer header carries the named controls', () => {
  const head = designer.slice(designer.indexOf('sem-wf-drawer-head'), designer.indexOf('sem-wf-drawer-body'));
  assert.ok(head.length > 200, 'the drawer must have a header of its own above the step form');
  assert.match(head, /Step \$\{drawerIndex \+ 1\} of \$\{steps\.length\}/,
    'the drawer header must say which step of how many is open');
  for (const control of ['Move left', 'Move right', 'Close']) {
    assert.ok(head.includes(control), `the drawer header must carry ${control}`);
  }
  assert.match(head, /Keep this open|Unpin/, 'the drawer header must carry the pin control');
  for (const item of ['Copy step', 'Delete step', 'Add step after']) {
    assert.ok(designer.includes(item), `${item} must stay reachable, in the drawer's more menu`);
  }
  assert.match(designer, /sem-wf-drawer-menu/,
    'Copy step, Delete step and Add step after live in a more menu, not as five buttons in a 40px row');
  assert.match(block('.sem-wf-drawer {'), /width:\s*440px/,
    'the drawer is 440px, the width the mockup was drawn and reviewed at');
  assert.match(css, /@media \(max-width: 1100px\)[\s\S]{0,400}\.sem-wf-drawer\s*\{[^}]*position:\s*absolute/,
    'under 1100px the drawer overlays the canvas instead of squeezing it into nothing');
  assert.match(css, /@media \(prefers-reduced-motion: reduce\)[\s\S]{0,600}\.sem-wf-drawer/,
    'the drawer must respect reduced motion');
  assert.match(designer, /fitSignal/,
    'the canvas must refit when the drawer opens or closes, or the steps sit off to one side');
});

test('full screen hides the rest of the tab, and Escape leaves it', () => {
  const FULL = 'body[data-studio-fullscreen="workflow-canvas"]';
  const hidden = css.slice(css.indexOf(FULL), css.indexOf(FULL) + 1400);
  for (const part of ['.studio-chrome', '.studio-arearow', '.studio-contextbar', 'aside:has(button[data-section])', '.sem-wf-author-head']) {
    assert.ok(hidden.includes(part), `full screen must hide ${part}, or the canvas is not the whole tab`);
  }
  assert.match(hidden, /display:\s*none/, 'the hidden parts must actually be hidden');
  assert.match(designer, /document\.body\.setAttribute\('data-studio-fullscreen'/,
    'full screen must be an attribute on the body that the stylesheet reads, so no other component changes');
  assert.match(designer, /removeAttribute\('data-studio-fullscreen'\)/,
    'leaving full screen must clear the attribute, including when the Author page unmounts');
  assert.match(designer, /'Escape'[\s\S]{0,400}fullscreen/,
    'Escape must leave full screen before it closes the drawer');
  assert.match(designer, /stage\.current\.contains\(target\)/,
    'Escape must reach the drawer from anywhere on this page: after clicking a step the focus is on the canvas, and on a read-only built-in every control in the drawer is disabled');
  assert.match(canvas, /shiftKey[\s\S]{0,200}'F'|'F'[\s\S]{0,200}shiftKey/,
    'Shift+F on the canvas must toggle full screen');
  assert.match(canvas, /Esc to leave/,
    'full screen must say how to get out of it, on the canvas itself');
});

test('the hint sentence is not a row', () => {
  const HINT = 'Select a step to edit it. Steps run in this order.';
  assert.ok(canvas.includes(HINT), 'the hint must survive, as a tooltip on the canvas background');
  assert.doesNotMatch(canvas, new RegExp(`<span[^>]*>\\{[^}]*${HINT.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}`),
    'the hint sentence must not be rendered as a row above the canvas');
  assert.match(canvas, new RegExp(`const CANVAS_HINT = '${HINT.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}'`),
    'the hint sentence must be one named constant, so it cannot drift between its two homes');
  assert.match(canvas, /title=\{steps\.length \? CANVAS_HINT : undefined\}/,
    'the hint belongs on the canvas background as a tooltip, and only where there is something to select');
  assert.match(canvas, /data-wf-canvas-empty/,
    'with no steps the canvas still shows its own centred empty state');
  assert.match(canvas, /Steps run in this order\. Add the first one to start\./,
    'the empty state must say what order steps run in and how to start');
});

test('the counts chip, beside the zoom controls', () => {
  assert.match(canvas, /data-wf-canvas-counts/,
    'the canvas must carry a counts chip the way the Diagram does');
  assert.match(canvas, /CanvasChip/,
    'the counts chip must be the shared canvas chip, not a second chip idiom');
  assert.match(canvas, /steps<\/span>|\{' '}steps|\bsteps\b/,
    'the counts chip reads steps, actions, checks');
});

test('product copy rules', () => {
  for (const [name, text] of [['the designer', designer], ['the canvas', canvas]]) {
    assert.doesNotMatch(text, /[\u2014\u2013]/, `${name} must carry no em or en dashes (product copy rule)`);
    assert.doesNotMatch(text, /\bAI assistant\b|\bthe AI\b/, `${name} must say "your assistant", never "the AI"`);
  }
});
