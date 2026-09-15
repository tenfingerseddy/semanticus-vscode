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
// The fixed 1500px container query was itself the next defect: it hid the utilities on a 1440 window that had
// room for them. A measured fold owns the narrow header now, and it must still never be overflow-x.
assert.doesNotMatch(styles, /@container studio-chrome/,
  'D-164: no fixed-width container query decides the fold any more');
assert.match(styles, /\.studio-chrome\[data-fold="measure"\]/,
  'D-164: the measuring pass has a state in which every header part is visible');
assert.match(styles, /\[data-fold="4"\][^}]*\.studio-groups \{ display: none/,
  'D-164: the five areas are the LAST thing to fold, at the deepest level');
assert.match(app, /new ResizeObserver/,
  'D-164: the fold is measured from the real header width, not guessed from a breakpoint');
assert.match(app, /dataset\.fold = String\(measureHeaderFold\(el\)\)/,
  'D-164: one measure per resize frame writes one data attribute');
assert.match(app, /studio-groups/,
  'D-164: the five primary areas are the cluster that collapses');
assert.match(app, /More Studio tools/,
  'D-164: the overflow control is named for a screen reader');
assert.match(app, />More</,
  'D-164: a visible More button keeps the five areas and shared actions reachable');
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

// --- the Model row's Create dropdown is reachable from the keyboard alone ----------------------------
const createSegment = (app.match(/function CreateTab\(\{[\s\S]*?\n\/\/ Primary area button\./) || [''])[0];
assert.ok(createSegment, 'the Create dropdown segment must exist in the Studio shell');
assert.match(createSegment, /aria-haspopup="menu"/, 'the segment announces that it opens a menu');
assert.match(createSegment, /aria-expanded=\{open\}/, 'the segment announces whether the menu is open');
assert.match(createSegment, /role="menu"/, 'the list is a menu, and its children are menu items');
assert.match(createSegment, /e\.key === 'Enter' \|\| e\.key === ' ' \|\| e\.key === 'ArrowDown'/,
  'Enter, Space and the arrows all open the menu from the segment');
assert.match(createSegment, /key === 'ArrowDown'[\s\S]{0,160}key === 'ArrowUp'/,
  'arrow keys move the highlight inside the menu');
assert.match(createSegment, /key === 'Escape'[\s\S]{0,120}closeAndReturnFocus/,
  'Escape closes the menu and hands focus back to the segment');
assert.match(createSegment, /closeAndReturnFocus = \(\) => \{ setOpen\(false\); buttonRef\.current\?\.focus\(\); \}/,
  'closing always returns focus to the segment button, never to the document');
// This used to pin a bare focus(). preventScroll joined it on 2026-09-14: the menu used to live inside
// .area-tabs, which is overflow-x: auto, and focusing the highlighted item made the browser scroll the segment
// strip to reveal it, leaving the row showing one scrolled segment. The menu is portalled out of the strip now,
// but the argument stays, because a focus() a browser decides to "reveal" can scroll any ancestor that scrolls.
assert.match(createSegment, /itemRefs\.current\[highlight\]\?\.focus\(\{ preventScroll: true \}\)/,
  'the highlighted item actually holds focus, and taking it never scrolls a container underneath');
assert.match(styles, /\.area-tabs-create-menu button\.is-highlighted/,
  'the highlighted item is visibly marked, not only focused');

// --- the Your assistant chip and Help open menus on the same keyboard contract -----------------------
// Both grew from a plain button when the gear left the header, so they must follow the contract the Create
// segment already set: Enter or ArrowDown opens, arrows move, Enter picks, Escape closes and hands focus
// back. A menu you can only reach with a mouse is not a replacement for a header button.
const activity = read('webview/src/activity.tsx');
const help = read('webview/src/help.tsx');
for (const [name, source, button] of [['Your assistant', activity, 'chipRef'], ['Help', help, 'buttonRef']]) {
  assert.match(source, /aria-haspopup="menu"/, `${name} announces that it opens a menu`);
  assert.match(source, /aria-expanded=\{open\}/, `${name} announces whether the menu is open`);
  assert.match(source, /role="menu"/, `${name} renders a real menu of menu items`);
  assert.match(source, /e\.key === 'Enter' \|\| e\.key === ' ' \|\| e\.key === 'ArrowDown'/,
    `Enter, Space and ArrowDown all open ${name}`);
  assert.match(source, /key === 'ArrowDown'[\s\S]{0,200}key === 'ArrowUp'/,
    `arrow keys move the highlight inside ${name}`);
  assert.match(source, /key === 'Escape'[\s\S]{0,140}closeAndReturnFocus/,
    `Escape closes ${name} and hands focus back`);
  assert.match(source, new RegExp(`closeAndReturnFocus = \\(\\) => \\{ setOpen\\(false\\); ${button}\\.current\\?\\.focus\\(\\); \\}`),
    `${name} returns focus to the control you opened, never to the document`);
  assert.match(source, /itemRefs\.current\[highlight\]\?\.focus\(\)/,
    `the highlighted item in ${name} actually holds focus`);
}
assert.match(styles, /\.studio-chip-menu button\.is-highlighted/,
  'the highlighted item in a header chip menu is visibly marked, not only focused');

// --- Astra finding 4: the live feed is a focus target of its own -------------------------------------
// Picking Live activity REPLACES the menu with the feed, so focusing the menu on the way out put the ring on a
// node that was about to unmount and it fell to the document. Escape is handled on the feed, which never had
// focus, so the feed stayed open behind an expanded chip with no keyboard way out.
assert.match(activity, /const feedRef = useRef<HTMLDivElement>\(null\);/, 'the feed needs a ref to focus');
assert.match(activity, /if \(view === 'feed'\) \{ feedRef\.current\?\.focus\(\); return; \}/,
  'the focus effect moves the ring INTO the newly mounted feed');
assert.doesNotMatch(activity, /item\.id === 'feed'[^\n]*menuRef\.current\?\.focus\(\)/,
  'picking the feed must not focus the menu it is about to unmount');
assert.match(activity, /ref=\{feedRef\} tabIndex=\{-1\}/, 'the feed is focusable so its own Escape handler receives keys');
assert.match(activity, /key === 'Tab'\) \{ e\.preventDefault\(\); closeAndReturnFocus\(\); \}/,
  'Tab dismisses the menu to its chip instead of stranding focus on an unmounting row');


// --- Astra spot review 3 (2026-09-14): a popover may not survive losing the keyboard ----------------
// Open the page notes with Space, press Tab once, press Escape: focus moved into the tool behind the notes,
// so Escape belonged to that tool and the notes stayed over the canvas with no keyboard way out. Reproduced on
// Overview, Diagram and Lineage. The same shape on Live activity: Tab out of the feed and Escape did nothing
// while the chip still read aria-expanded="true". Two rules close it: a small dialog keeps Tab inside itself,
// and any popover that loses focus to something outside itself closes. The behaviour is proved in the browser
// by tools/uishot/shell-journey.mjs; these patterns pin the decision in the source.
const helpSrc = read('webview/src/help.tsx');
const activitySrc = read('webview/src/activity.tsx');

assert.match(helpSrc, /function PageNotesButton[\s\S]{0,3000}onBlur=\{/,
  'the page notes must close when focus leaves them, not only on Escape');
assert.match(helpSrc, /document\.hasFocus\(\)/,
  'but a window losing focus is not a person leaving the notes, so that case is excluded');
assert.match(helpSrc, /relatedTarget/,
  'the decision is where focus actually went, not merely that it left an element');
assert.match(helpSrc, /if \(e\.key !== 'Tab'\) return;/,
  'Tab is handled by the notes dialog itself');
assert.match(helpSrc, /stops\[stops\.length - 1\]/,
  'and Shift+Tab from the dialog wraps to the last stop inside it, never to the page behind');
assert.match(helpSrc, /studio-page-notes-close/,
  'the dialog has a close affordance, so trapping Tab always has somewhere to put the ring');

assert.match(activitySrc, /onBlur=\{/,
  'the assistant chip popovers close when focus leaves the surface');
assert.match(activitySrc, /relatedTarget/,
  'decided by where focus went, so a click or Tab inside the feed never closes it');
assert.match(activitySrc, /Tab stays native/,
  'the feed still lets Tab walk its entries: it is a list, not a dialog');

console.log('keyboard and grids tests passed');
