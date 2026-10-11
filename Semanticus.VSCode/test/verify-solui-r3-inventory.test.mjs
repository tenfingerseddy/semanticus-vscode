// Verifier on m11/solui round 3 (item 3, the box inventory): two manifest lines disputed, each with its trigger, left failing on purpose.
//   D1 the docs| scope (manifest scopes "docs|": lister "unlisted"). Words typed in a Documentation narrative section for a table or measure are held under
//      docs|<object ref>|<section>. When the assistant renames (or deletes) that object, the Narrative pane falls back to the model, the editor for the
//      renamed object opens empty, and nothing on any page names the held words again. They are kept, but no person can reach them: lost work.
//   D2 the + Item form's format box (manifest line advmodels.tsx::FormatStringControl::DaxField::dyn, class "parent": "the parent owns the words"). In the
//      calc group card's + Item form the control gets no `draft`, so a Dynamic format typed and not yet saved lives only in the control's own state. The
//      assistant renames the group and its item and adds another group inside one reload: the leftovers do not pair (one gone, two new), the card is drawn
//      anew, and those words are in no box and not in the store (the name and DAX of the form are held and listed; the format words are not).
import assert from 'node:assert/strict';
import test from 'node:test';
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { webview } from './helpers/webview-load.mjs';
import { makeDisk, makePage, find, findAll, settleAll, wait } from './helpers/drafts-mount.mjs';

globalThis.window ??= globalThis;
globalThis.acquireVsCodeApi ??= () => ({ postMessage() {}, getState: () => ({}), setState() {} });
const MODEL = 'file:/m/Model.bim';
const flat = (el) => (el == null || el === false || el === true ? '' : Array.isArray(el) ? el.map(flat).join('') : typeof el === 'object' ? flat(el.props?.children) : String(el));
const heldAll = (disk) => JSON.stringify([...disk.map.values()]);
const src = (name) => readFileSync(join(webview, 'src', name), 'utf8');

// ---- D1 docs| ---------------------------------------------------------------------------------------------------------------------------------------------
test('D1 docs|: narrative words typed for a table the assistant then renames are shown again or listed (they are held, so a person must be able to reach them)', async () => {
  const disk = makeDisk();
  const world = { sections: { 'table:Sales::purpose': '' }, calls: [] };
  const pg = makePage({
    './bridge': { rpc: async (m, ref, key, ...rest) => {
        world.calls.push([m, ref, key, ...rest]);
        const k = `${ref}::${key}`;
        if (!(k in world.sections)) throw new Error(`No object '${ref}' in the model.`);   // the engine: the old ref is gone after the rename
        return m === 'getDocSection' ? world.sections[k] : undefined;
      }, onDidChange: () => () => undefined, exportDoc() {}, printDoc() {}, openExternal() {}, loadState: disk.loadState, saveState: disk.saveState },
    './hooks': { useFillHeight: () => undefined, useAutoGrow: () => ({ current: null }), usePersistedState: (k, v) => [v, () => undefined] },
    './sendtask': { GiveToAssistant: () => null, docsTask: () => ({}) },
    './activity': { useClaudeReflection: () => undefined },
  }, MODEL);
  const D = pg.load('documentation.tsx');
  const editor = (objRef, value = '') => pg.mount(() => D.SectionEditor({ objRef, objName: objRef.replace(/^table:/, ''), sectionKey: 'purpose', value, onSaved: () => undefined }));
  const a = editor('table:Sales'); await settleAll(); a.rerender();
  find(a.result, (el) => el.type === 'textarea').props.onChange({ target: { value: 'Sales holds one row per order line, net of returns.' } }); await settleAll(); a.rerender();
  // The assistant renames Sales to Revenue. The pane's object list no longer has table:Sales, so it falls back and the Sales editor unmounts (its save on leaving is refused: no such object).
  world.sections = { 'table:Revenue::purpose': '' };
  a.unmount(); await settleAll(); await settleAll();
  assert.ok(heldAll(disk).includes('net of returns'), 'precondition: the words are still held in the store');
  // The person picks Revenue (the same table) in the pane.
  const b = editor('table:Revenue'); await settleAll(); b.rerender();
  const shownOnRenamed = find(b.result, (el) => el.type === 'textarea')?.props.value?.includes('net of returns');
  const listed = /OrphanDrafts|docs\|/.test(src('documentation.tsx').replace(/const draftScope = `docs\|[^`]*`/, '').replace(/sendKey=\{`docs\|[^`]*`\}/, ''));
  assert.ok(shownOnRenamed || listed,
    'the words typed for Sales are held under docs|table:Sales|purpose, but the Revenue editor opens empty and no lister on the Documentation page names them: kept but unreachable');
});

// ---- D2 the + Item form's Dynamic format -----------------------------------------------------------------------------------------------------------------
function advPage(world) {
  const disk = makeDisk();
  world.listeners = []; world.calls = [];
  const pg = makePage({
    './bridge': { rpc: async (method, ...args) => { world.calls.push([method, ...args]); return structuredClone(world.answer(method, ...args) ?? {}); },
      onDidChange: (f) => { world.listeners.push(f); return () => undefined; }, loadState: disk.loadState, saveState: disk.saveState },
    './daxeditor': { DaxField: () => null }, './objectbrowser': { ObjectBrowser: () => null, useBrowserData: () => ({}) }, './hooks': { useFillHeight: () => undefined },
    '@tanstack/react-virtual': { useVirtualizer: () => ({ getVirtualItems: () => [], getTotalSize: () => 0 }) },
  }, MODEL);
  return { pg, disk, adv: pg.load('advmodels.tsx') };
}
const group = (name, items, precedence = 10) => ({ ref: `table:${name}`, name, precedence, items: items.map((n, i) => ({ ref: `calcitem:${name}/${n}`, name: n, ordinal: i, expression: 'x' })) });
const calcWorld = (groups) => { const w = { groups }; w.answer = (m) => (m === 'listCalculationGroups' ? w.groups : {}); return w; };
const cardOf = (view, name) => findAll(view.result, (el) => el.type?.name === 'CalcGroupCard').find((c) => c.props.group.name === name);
const reloadAll = async (world, view) => { world.listeners.forEach((f) => f()); await wait(420); await settleAll(); view.rerender(); await settleAll(); view.rerender(); };
const mountCard = (pg, el) => pg.mount(() => el.type(el.props));

test('D2 + Item form: a Dynamic format typed and not yet saved outlives a card drawn anew after the assistant renames the group and adds another in one reload', { todo: 'Deferred by Kane 6 Oct 2026: after the M11 release' }, async () => {
  const w = calcWorld([group('Time Intelligence', ['YTD'])]);
  const { pg, adv, disk } = advPage(w);
  const view = pg.mount(() => adv.CalcGroupsPanel()); await settleAll(); view.rerender();
  const before = cardOf(view, 'Time Intelligence').key;
  const card = mountCard(pg, cardOf(view, 'Time Intelligence'));
  find(card.result, (el) => el.type?.name === 'MiniButton' && flat(el) === '+ Item').props.onClick(); await settleAll(); card.rerender();
  find(card.result, (el) => el.type === 'input' && /item name/.test(el.props?.placeholder ?? '')).props.onChange({ target: { value: 'Share' } }); await settleAll(); card.rerender();
  // The form's format control, drawn as its own instance (the fake React does not draw children).
  const fsEl = find(card.result, (el) => el.type?.name === 'FormatStringControl');
  const fs = pg.mount(() => fsEl.type(fsEl.props));
  find(fs.result, (el) => el.type === 'button' && flat(el) === 'Dynamic').props.onClick(); await settleAll(); fs.rerender();
  const DYN = 'IF ( SELECTEDMEASURE () < 1, "0.0%", SELECTEDMEASUREFORMATSTRING () )';
  find(fs.result, (el) => el.props?.ariaLabel === 'Format-string expression').props.onChange(DYN); await settleAll(); fs.rerender();
  assert.equal(find(fs.result, (el) => el.props?.ariaLabel === 'Format-string expression').props.value, DYN, 'precondition: the words are in the box');
  // The assistant renames the group and its item, and adds a group, inside one reload.
  w.groups = [group('Time Intel', ['Year to date']), group('Ratios', ['Pct'])];
  await reloadAll(w, view);
  assert.notEqual(cardOf(view, 'Time Intel').key, before, 'precondition: one gone and two new do not pair, so the card is drawn anew');
  card.unmount(); fs.unmount(); await settleAll();
  assert.ok(heldAll(disk).includes('Share'), 'precondition: the form\'s name is held (and listed by OrphanDrafts calcgroup|)');
  assert.ok(heldAll(disk).includes('SELECTEDMEASUREFORMATSTRING'),
    'the Dynamic format typed in the + Item form is in no box and not in the store: the manifest\'s "parent" class for FormatStringControl::dyn does not hold here');
});
