import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const read = (file) => readFileSync(resolve(root, file), 'utf8');

const pkg = JSON.parse(read('package.json'));
const app = read('webview/src/App.tsx');
const shortcuts = read('webview/src/shortcuts.tsx');
const bridge = read('webview/src/bridge.ts');
const ext = read('src/extension.ts');
const propJs = read('media/propgrid/propgrid.js');
const propCss = read('media/propgrid/propgrid.css');
const styles = read('webview/src/styles.css');
const bindings = pkg.contributes.keybindings;
const itemCtx = pkg.contributes.menus['view/item/context'];

function binding(command, key, whenNeedle) {
  return bindings.find((b) => b.command === command && b.key === key && String(b.when || '').includes(whenNeedle));
}

// --- D-149 Studio and Properties webviews swallow Ctrl+Z, Ctrl+S, Ctrl+Shift+P ----------------------
assert.ok(binding('semanticus.save', 'ctrl+s', 'semanticusStudio'),
  'Studio already owns Ctrl+S; keep that binding');
assert.ok(binding('semanticus.save', 'ctrl+s', 'semanticusProperties'),
  'D-149: Properties must bind Ctrl+S so Save works while the grid has focus');
assert.match(shortcuts, /hostCommandForKey/,
  'D-149: Studio must name the chord-to-host-command helper (webview iframes swallow keys before VS Code sees them)');
assert.match(shortcuts, /workbench\.action\.showCommands/,
  'D-149: Ctrl+Shift+P must forward to the command palette');
assert.match(shortcuts, /semanticus\.save/,
  'D-149: Ctrl+S must forward to semanticus.save');
assert.match(shortcuts, /semanticus\.undo/,
  'D-149: Ctrl+Z (when not typing) must forward to semanticus.undo');
assert.match(bridge, /export function runHostCommand\(/,
  'D-149: the Studio bridge must post a host command rather than hoping the iframe bubbles the key');
assert.match(bridge, /type: 'runCommand'/,
  'D-149: the wire message is runCommand');
assert.match(app, /hostCommandForKey/,
  'D-149: Studio keydown must consult the chord helper');
assert.match(app, /runHostCommand/,
  'D-149: Studio keydown must hand matching chords to the host');
assert.match(app, /addEventListener\('keydown', onKey, true\)/,
  'D-149: Studio must listen in capture so an inner editor cannot eat Ctrl+S first');
assert.match(ext, /msg\?\.type === 'runCommand'/,
  'D-149: the host relay must accept runCommand from Studio');
assert.match(ext, /workbench\.action\.showCommands/,
  'D-149: the host allow-list must include the command palette');
assert.match(propJs, /type: 'runCommand'/,
  'D-149: the Properties grid must forward the same chords');
assert.match(propJs, /workbench\.action\.showCommands/,
  'D-149: Properties must forward Ctrl+Shift+P');
assert.match(ext, /semanticusProperties[\s\S]*runCommand|runCommand[\s\S]*semanticusProperties/,
  'D-149: the Properties provider must execute forwarded host commands');

// Undo while typing stays text undo: the helper must stand down on inputs.
assert.match(shortcuts, /isTypingTarget/,
  'D-149: Ctrl+Z forwarding must keep using the typing-target guard so a description box still undoes text');

// --- D-160 Enter in the description box must commit, not insert a blank line -------------------------
assert.match(propJs, /data-commit-enter/,
  'D-160: Description is the only textarea that commits on Enter');
assert.match(propJs, /Shift\+Enter|shiftKey/,
  'D-160: Shift+Enter must still be able to add a line');
assert.match(propJs, /Enter applies/,
  'D-160: the box must say how to commit (nothing on screen said so)');
assert.doesNotMatch(propJs, /data-commit-enter[\s\S]{0,200}Expression|Expression[\s\S]{0,200}data-commit-enter/,
  'D-160: DAX Expression textareas must keep Enter as a newline');

// --- D-154 a long description must not push the rows below it ----------------------------------------
assert.match(propCss, /data-commit-enter[^{]*\{[^}]*max-height/,
  'D-154: the description box is height-capped so later rows stay where they were');
assert.match(propCss, /data-commit-enter[^{]*\{[^}]*overflow-y:\s*auto/,
  'D-154: extra lines scroll inside the box instead of growing the grid');

// --- D-151 pickers show the current value and offer a clear entry ------------------------------------
assert.match(ext, /\$\(check\)/,
  'D-151: QuickPicks mark the value that is currently set');
assert.match(ext, /label: current === '' \? '\$\(check\) None' : 'None'|None[\s\S]{0,80}Clear the data category/,
  'D-151: Data Category offers None so clearing does not require emptying the grid field');
assert.match(ext, /getObjectProperties[\s\S]*DataCategory|DataCategory[\s\S]*getObjectProperties/,
  'D-151: the Data Category picker reads the current value before it opens');
assert.match(ext, /CrossFilteringBehavior[\s\S]*Currently set|Currently set[\s\S]*CrossFilteringBehavior/,
  'D-151: the cross-filter picker names the direction that is already set');
assert.match(propJs, /option value=/,
  'D-151: enum rows use a value attribute so an empty None option can be selected');
assert.match(propJs, />None</,
  'D-151: a None entry is available on clearable enum rows');

// --- D-163 property grid labels readable at default sidebar width ------------------------------------
assert.match(propCss, /grid-template-columns:\s*minmax\(/,
  'D-163: the label column has a minimum width so Description is not clipped to Desc…');
assert.match(propCss, /\.rowx label[^}]*white-space:\s*normal/,
  'D-163: labels wrap instead of ellipsizing to six characters');
assert.doesNotMatch(propCss, /\.rowx label[^}]*text-overflow:\s*ellipsis/,
  'D-163: label ellipsis at 42% was the defect');

// --- D-164 Studio top-level navigation has an overflow control ---------------------------------------
assert.match(styles, /studio-chrome/,
  'D-164: the Studio header is a named container so it can collapse without a thin scrollbar');
assert.match(styles, /@container studio-chrome/,
  'D-164: a container query, not overflow-x, owns the narrow header');
assert.match(app, /studio-standalone/,
  'D-164: Primer / Workflows / Edits are the cluster that collapses');
assert.match(app, /More Studio pages/,
  'D-164: the overflow control is named for a screen reader');
assert.match(app, />More</,
  'D-164: a visible More button hints that Primer / Workflows / Edits still exist');
assert.doesNotMatch(app, /<header[^>]*overflow-x:\s*auto/,
  'D-164: the header must not rely on a thin horizontal scrollbar');

// --- D-153 a blank model offers its first table from the tree ----------------------------------------
assert.match(ext, /kind === 'emptyModel'|emptyModel/,
  'D-153: an empty open model renders a tree row, not a blank pane');
assert.match(ext, /Add a table/,
  'D-153: the empty-tree row says how to add the first table');
assert.ok(itemCtx.some((m) => m.command === 'semanticus.newTable' && /emptyModel/.test(m.when || '')),
  'D-153: right-click on that row offers New Table');
assert.match(ext, /emptyModel[\s\S]{0,400}semanticus\.newTable|command: 'semanticus\.newTable'[\s\S]{0,200}emptyModel/,
  'D-153: clicking the empty-tree row runs New Table');

console.log('keyboard and grids tests passed');
