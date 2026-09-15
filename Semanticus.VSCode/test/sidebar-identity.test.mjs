// The Semanticus side bar contract (Kane's 1.2.0 usability round).
//
// WHY THIS EXISTS. Kane opened the Semanticus container and the Model view's title read "M...": nine inline
// title actions had eaten the name, and Studio was a tab he had to remember to open from the first of those
// icons. The ratified answer is three things, and this file pins all three so a regression is caught without
// a running VS Code:
//   1. the open model is named on the FIRST ROW of the Model tree, above the tables, and carries no actions;
//   2. the Model tree keeps its name because it has NO navigation-group title actions left, and every command
//      that used to sit inline is still reachable from the overflow menu;
//   3. Studio opens by itself when a model session appears, and a reloaded window restores the one Studio tab
//      through a registered webview-panel serializer.
//
// ITEM 1 TOOK FOUR ROUNDS AND THESE ASSERTIONS ARE WHAT STOPS ROUND FIVE. Build one put all twelve former
// title actions into a new webview view and needed two lines of icons. Build two cut it to three icons on one
// row. Build three dropped the icons and left a quiet identity row. All three were a `semanticusBar` webview
// view, and measuring build three in a real Extension Development Host is what settled it: VS Code floors a
// side bar view pane at 120px of body plus its header (_minimumBodySize defaults to 120 in a vertical side
// bar), so a one-line bar could never be one line tall, and its view header repeated the container title.
// A tree row has no pane and no header, so the identity moved into the tree and the bar view is gone.
import assert from 'node:assert/strict';
import { existsSync, readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const read = (...p) => readFileSync(resolve(root, ...p), 'utf8');
const manifest = JSON.parse(read('package.json'));
const extension = read('src', 'extension.ts');

// ---- (a) the Model tree view title is readable again ---------------------------------------------
const viewTitle = manifest.contributes.menus['view/title'];
const modelTitle = viewTitle.filter((m) => typeof m.when === 'string' && m.when.includes('view == semanticusModel'));
const inlineOnModel = modelTitle.filter((m) => (m.group ?? '').startsWith('navigation'));
assert.deepEqual(inlineOnModel.map((m) => m.command ?? m.submenu).sort(), [],
  'the Model tree view must contribute NO navigation-group title actions; inline icons are what ate its name');

// Every command that used to be inline is still on the menu, in an overflow group, so nothing became
// unreachable by moving it.
const FORMERLY_INLINE = [
  'semanticus.openStudio', 'semanticus.openModel', 'semanticus.manageConnections', 'semanticus.findInModel',
  'semanticus.save', 'semanticus.saveToLive', 'semanticus.refresh', 'semanticus.restartEngine',
  'semanticus.connectClaudeCode',
];
for (const command of FORMERLY_INLINE) {
  const entry = modelTitle.find((m) => m.command === command);
  assert.ok(entry, `${command} left the Model view title menu entirely; it must move to an overflow group, not vanish`);
  assert.ok(!(entry.group ?? '').startsWith('navigation'),
    `${command} is still an inline navigation action on the Model view title`);
  assert.ok((entry.group ?? '').length > 0, `${command} has no overflow group, so its order in the menu is undefined`);
}
// The Reference Model view and its three title actions are gone (see section (e)); nothing in the whole
// menu contribution may still name that view.
const referenceMenus = Object.entries(manifest.contributes.menus)
  .flatMap(([where, entries]) => entries.map((m) => ({ where, m })))
  .filter(({ m }) => typeof m.when === 'string' && m.when.includes('semanticusReference'));
assert.deepEqual(referenceMenus.map(({ where, m }) => `${where}: ${m.command ?? m.submenu}`), [],
  'a menu entry still targets the dropped semanticusReference view');

// ---- (b) the bar view is GONE, and nothing is left behind ----------------------------------------
const views = manifest.contributes.views.semanticus;
assert.ok(!views.some((v) => v.id === 'semanticusBar'),
  'the semanticusBar webview view is still contributed; VS Code floors its pane at 120px, which is why it was dropped');
assert.equal(views[0].id, 'semanticusModel', 'the Model tree is the first view in the Semanticus container again');
assert.ok(views.some((v) => v.id === 'semanticusModel' && v.name === 'Model'), 'the tree view is still called Model');
for (const orphan of ['src/sidebarBar.ts', 'media/sidebar']) {
  assert.ok(!existsSync(resolve(root, orphan)), `${orphan} belonged to the dropped bar view and must go with it`);
}
assert.ok(!extension.includes('sidebarBar') && !extension.includes('SidebarBarProvider'),
  'the extension still references the dropped bar provider');
assert.ok(!JSON.parse(readFileSync(resolve(root, '..', 'third-party-manifest.json'), 'utf8')).entries
  .some((e) => (e.path ?? '').includes('media/sidebar')),
  'the borrowed-glyph declaration from the icon-button rounds must be withdrawn with the files');

// ---- (c) Studio opens by itself and survives a window reload --------------------------------------
assert.match(extension, /registerWebviewPanelSerializer\('semanticusStudio'/,
  'a reloaded window must restore the Studio tab through a registered serializer');
// A SERIALIZER THAT IS NEVER REGISTERED RESTORES NOTHING. It is registered inside activate(), so the
// extension has to be activated before VS Code asks for it. Astra's finding 5: the only activation event
// was onFileSystem:semanticus, which fires for a DAX/TMDL virtual document. Reload with the Explorer
// showing, no DAX editors open and only the Studio tab to restore and NOTHING activates the extension, so
// the tab comes back dead. A visible Semanticus view masks it, because a contributed view activates too.
assert.ok(manifest.activationEvents.includes('onWebviewPanel:semanticusStudio'),
  'onWebviewPanel:semanticusStudio must be an activation event, or a cold reload restores a dead Studio tab');
const setting = manifest.contributes.configuration.properties['semanticus.studio.openAutomatically'];
assert.ok(setting, 'the auto-open setting must be contributed so a person can turn it off');
assert.equal(setting.type, 'boolean');
assert.equal(setting.default, true, 'Studio opens by itself unless the person says otherwise');
assert.ok((setting.description ?? '').length > 20, 'the setting must explain itself in plain words');
assert.match(extension, /function maybeAutoOpenStudio/,
  'the auto-open lives in one named function so the guard is in one place');
const autoOpen = extension.slice(extension.indexOf('function maybeAutoOpenStudio'),
  extension.indexOf('function maybeAutoOpenStudio') + 1600);
assert.match(autoOpen, /get<boolean>\('studio\.openAutomatically', true\)/,
  'the auto-open is guarded by the setting, defaulting to on');
assert.match(autoOpen, /if \(studioPanel\) return;/,
  'the auto-open must never create a second Studio when one is already open');
assert.match(extension, /autoOpenedSessionId/,
  'a closed Studio stays closed until the NEXT model open, so the auto-open keys off the session it fired for');

// ---- (d) the identity row IS the first child of the tree root -------------------------------------
const treeProvider = extension.slice(extension.indexOf('class ModelTreeProvider'),
  extension.indexOf('async function copyFromModelCmd'));
assert.ok(treeProvider.length > 500, 'could not slice ModelTreeProvider out of extension.ts');

// It is the first ROOT child, before the tables, and only when a model is actually open.
assert.match(treeProvider, /return \[this\.identityNode\(\), \.\.\.roots\]/,
  'the identity row must be the FIRST root child, ahead of the tables');
assert.match(treeProvider, /private identity\?: ModelIdentity/,
  'the provider must hold the identity it was told, rather than fetching its own copy of the truth');
assert.match(treeProvider, /setIdentity\(/,
  'the provider must be TOLD the identity from the session path, the same signal the dropped bar used');

// What the row renders: name as the label, source and live state as the description, full path as tooltip,
// a model icon, not collapsible, and a click that lands on Studio's Overview.
const item = treeProvider.slice(treeProvider.indexOf("if (n.kind === 'modelIdentity')"),
  treeProvider.indexOf("if (n.kind === 'modelIdentity')") + 2200);
assert.ok(item.length > 200, 'no getTreeItem branch for the identity row');
assert.match(item, /TreeItemCollapsibleState\.None/, 'the identity row is not collapsible');
assert.match(item, /ThemeIcon\('database'\)/, 'the identity row carries a model icon');
assert.match(item, /\.description = /, 'the source and live state ride in the row description, beside the name');
assert.match(item, /\.tooltip = /, 'the full path belongs in the tooltip, where a narrow tree cannot cut it');
assert.match(item, /command: 'semanticus\.studioGoTab'[\s\S]{0,140}arguments: \['modelhome'\]/,
  "clicking the model name must land on Studio's Overview");

// The unsaved marker is a leading dot in the DESCRIPTION, because the icon slot already holds the model icon
// and a TreeItem has only one.
assert.match(treeProvider, /'● '/, 'unsaved changes show as a leading dot on the identity row');

// It must refresh on the same three things the dropped bar watched: the model, its path, and the dirty state.
assert.match(extension, /tree\?\.setIdentity\(/, 'the session path must push the identity into the tree');
assert.match(extension, /function barSource/,
  'the file-name shortening the bar introduced is still what the row uses; a path tail spends the row on folders');
// The dirty flag reaches the UI through the sync-chip fetch, not through setStatusFromInfo, so the row has to
// be refreshed there too or the dot only ever appears on a model open.
const syncChip = extension.slice(extension.indexOf('async function refreshSyncChip'),
  extension.indexOf('async function refreshSyncChip') + 800);
assert.match(syncChip, /tree\?\.setIdentity\(/,
  'the identity row must be refreshed from the sync-chip fetch, which is where an edit flips hasUnsavedChanges');

// The row is not an object: it must not drive the Properties grid or the Studio selection bus.
assert.match(extension, /n\.kind !== 'dfolder' && n\.kind !== 'modelIdentity'/,
  'the identity row must be excluded from the selection that feeds Properties');

// ---- (e) the Reference Model view is GONE, replaced by one command ---------------------------------
// Kane's decision of 2026-09-14: a second permanent tree in the side bar cost a pane and a header to do a
// job that happens a few times a year. The view goes; the engine's reference session stays, and the one
// thing people actually did with the tree (copy objects out of another model) becomes a single command.
assert.ok(!views.some((v) => v.id === 'semanticusReference'),
  'the semanticusReference tree view is still contributed; the whole side bar surface was meant to go');
const contributedCommands = new Set(manifest.contributes.commands.map((c) => c.command));
for (const gone of [
  'semanticus.setReferenceModel', 'semanticus.refreshReferenceModel', 'semanticus.clearReferenceModel',
  'semanticus.copyRefToModel', 'semanticus.copyRefObject', 'semanticus.pasteIntoModel',
]) {
  assert.ok(!contributedCommands.has(gone), `${gone} belonged to the Reference Model tree and must go with it`);
  assert.ok(!extension.includes(`'${gone}'`), `${gone} is still registered in extension.ts`);
}
assert.ok(!extension.includes('ReferenceTreeProvider') && !extension.includes('referenceView'),
  'the reference tree provider and its TreeView are still in extension.ts');
for (const key of manifest.contributes.keybindings) {
  assert.ok(!String(key.when ?? '').includes('semanticusReference'),
    `the keybinding for ${key.command} still scopes itself to the dropped view`);
}
// Nothing that survived lost its keybinding: the Model tree keeps every gesture it had.
const modelKeys = manifest.contributes.keybindings
  .filter((k) => String(k.when ?? '').includes('semanticusModel')).map((k) => `${k.command} ${k.key}`).sort();
for (const kept of [
  'semanticus.undo ctrl+z', 'semanticus.redo ctrl+shift+z', 'semanticus.redo ctrl+y',
  'semanticus.renameObjectInputBox f2', 'semanticus.deleteObject delete', 'semanticus.newMeasure ctrl+alt+n',
  'semanticus.copyObject ctrl+c', 'semanticus.pasteObject ctrl+v', 'semanticus.openStudio ctrl+alt+s',
]) {
  assert.ok(modelKeys.includes(kept), `the Model tree lost its ${kept} keybinding`);
}

// The replacement is ONE command, in the Model view's overflow menu and in the palette.
const copyFrom = manifest.contributes.commands.find((c) => c.command === 'semanticus.copyFromModel');
assert.ok(copyFrom, 'semanticus.copyFromModel is not contributed');
assert.equal(copyFrom.title, 'Copy from another model…', 'the command reads as a plain sentence in the menu');
assert.equal(copyFrom.category, 'Semanticus', 'the palette entry needs the Semanticus category');
assert.ok(!/[—–]/.test(copyFrom.title), 'no em dashes in user-facing copy');
const copyFromMenu = modelTitle.find((m) => m.command === 'semanticus.copyFromModel');
assert.ok(copyFromMenu, 'Copy from another model… is missing from the Model view title menu');
assert.ok(!(copyFromMenu.group ?? '').startsWith('navigation'),
  'Copy from another model… must sit in the … overflow, not as another inline icon');
assert.ok((copyFromMenu.group ?? '').length > 0, 'the overflow entry has no group, so its order is undefined');
const hiddenFromPalette = (manifest.contributes.menus.commandPalette ?? [])
  .filter((e) => e.when === 'false').map((e) => e.command);
assert.ok(!hiddenFromPalette.includes('semanticus.copyFromModel'),
  'the command must be reachable from the command palette');

// The flow: pick where to copy FROM (the same three choices the old Set reference model offered), open the
// reference session silently, then a MULTI-select pick of that model's copyable objects grouped by table.
assert.match(extension, /async function copyFromModelCmd/, 'the command has no implementation');
const copyFlow = extension.slice(extension.indexOf('async function copyFromModelCmd'),
  extension.indexOf('async function cherryPickInto'));
assert.ok(copyFlow.length > 800, 'could not slice the copy-from-model command out of extension.ts');
for (const choice of [/kind: 'file'/, /kind: 'gitref'/, /kind: 'workspace'/]) {
  assert.match(copyFlow, choice, `the source pick lost one of the three ModelRef kinds (${choice})`);
}
assert.match(copyFlow, /listConnections/, 'a saved connection must still be offered as a source');
assert.match(copyFlow, /openReferenceSession\(/,
  'the reference session must be opened the same silent way setReferenceModel did');
assert.match(copyFlow, /canPickMany: true/, 'the object pick must be multi-select');
assert.match(copyFlow, /QuickPickItemKind\.Separator/, 'the objects must be grouped by table with separators');
// The paste path is unchanged, so behaviour and undo are what they always were.
assert.match(copyFlow, /await cherryPickInto\(/,
  'the new command must run the copy through the SAME cherry-pick paste path the reference tree used');
const cherryPick = extension.slice(extension.indexOf('async function cherryPickInto'),
  extension.indexOf('async function cherryPickInto') + 900);
assert.match(cherryPick, /sendRequest<[^>]*>\('cherryPick'/, 'cherryPickInto no longer calls the cherryPick op');

// Opening the reference session is still what binds it engine-side, so the Connections hub's Reference card
// keeps working with no tree behind it.
assert.match(extension, /async function openReferenceSession/, 'no silent reference-session opener');
const opener = extension.slice(extension.indexOf('async function openReferenceSession'),
  extension.indexOf('async function openReferenceSession') + 900);
assert.match(opener, /'listReferenceTree'/, 'the session opener must still call listReferenceTree');
assert.match(extension, /useAsReference[\s\S]{0,900}openReferenceSession\(/,
  "the Connections hub's Use as reference must still bind a reference model");

// Plain copy in everything THIS round wrote: no engine jargon, no em dashes, and if the assistant is
// mentioned at all it is called "your assistant". (Older commands carry their own lanes' copy.)
// Only the strings a person reads: the quick-pick prompts and the notification sentences. (copy-rules.test.mjs
// owns the global sweep for engine operation names in user-visible copy; this is the em dash and assistant check.)
const newCopy = [copyFrom.title, ...(copyFlow.match(/(?:label|detail|description|title|placeHolder|prompt): (?:'[^'\n]*'|`[^`\n]*`)/g) ?? [])];
assert.ok(newCopy.length > 4, 'the copy walk over the new command found almost nothing to check');
for (const text of newCopy) {
  assert.ok(!/[\u2014\u2013]/.test(text), `em dash in new copy: ${text}`);
  assert.ok(!/\bAI\b|\bagent\b/i.test(text) || /your assistant/i.test(text), `name it "your assistant": ${text}`);
}

// ---- (f) the docs do not describe a view that no longer exists -------------------------------------
const shortcuts = readFileSync(resolve(root, '..', 'docs', 'keyboard-shortcuts.md'), 'utf8');
assert.ok(!shortcuts.includes('semanticusReference'),
  'docs/keyboard-shortcuts.md still documents a keybinding scope that no longer exists');
assert.ok(!shortcuts.includes('Reference Model tree'),
  'docs/keyboard-shortcuts.md still documents the Reference Model tree');

console.log('Semanticus side bar identity contract passed');
