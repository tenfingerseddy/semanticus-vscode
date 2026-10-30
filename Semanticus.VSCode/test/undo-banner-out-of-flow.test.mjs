import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

// m17 P1 (v2-m17 report lines 83-84): the "Undone." line sat in the page flow, so it pushed the page down by its own height (37 px) and
// back again, and the press after an Undo hit the place the control had just left. The line must float over the page and let presses
// through. This reads the rule; tools/uishot/undo-banner-drive.mjs measures the real page (a control must not move by one pixel).
const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const css = readFileSync(resolve(root, 'webview/src/personality/messages.css'), 'utf8');
const rule = css.match(/\.personality-message\s*\{([^}]*)\}/);
assert.ok(rule, 'messages.css has a .personality-message rule');
const body = rule[1];
assert.match(body, /position:\s*fixed/, 'the voice line floats over the page, it takes no room in it');
assert.match(body, /pointer-events:\s*none/, 'a press goes through the voice line to the control under it');
assert.doesNotMatch(body, /position:\s*(static|relative|sticky)/, 'the voice line is never back in the flow');
console.log('PASS the voice line floats and lets presses through');
