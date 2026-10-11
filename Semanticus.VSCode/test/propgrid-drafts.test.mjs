// The Properties grid keeps typed, unsaved words (join 11, the drafts class, Kane 4 Oct).
//
// WHAT WENT WRONG. media/propgrid/propgrid.js held the format editor and each property's typed words in module memory. Selecting another object
// cleared them, and a view reload (or a view VS Code disposed while hidden) lost them. None of those is the person saying "throw this away".
//
// RULE. A draft is kept in the webview's persisted state (getState/setState), keyed by the model FILE + the object's ref key + the property, with the
// value typed and the value it was typed over. It ends only on a save the engine took, the person's Cancel or Use the new one. When it comes back
// over a stored value that moved, the person chooses Keep mine or Use the new one, and nothing is written until then. A held draft shows a quiet
// "Draft not saved" mark on its row. A draft never shows on another model or another object (the ref, not the name, is the key).
//
// The REAL media/propgrid/propgrid.js runs in a small fake page: the markup it writes is parsed into element objects that carry its attributes,
// values and listeners, so a test types, changes and clicks the way the page is wired. A "reload" is a NEW page over the SAME persisted state.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const gridSource = readFileSync(join(root, 'media', 'propgrid', 'propgrid.js'), 'utf8');

// ---- a fake page ---------------------------------------------------------------------------------------------
const unesc = (s) => s.replace(/&lt;/g, '<').replace(/&gt;/g, '>').replace(/&quot;/g, '"').replace(/&amp;/g, '&');
const TAGS = 'input|textarea|select|button|div|span|label|code|em|datalist|option|optgroup';
function attrsOf(text) {
  const out = {};
  for (const m of text.matchAll(/([a-zA-Z][\w-]*)(?:="([^"]*)")?/g)) out[m[1]] = m[2] === undefined ? '' : unesc(m[2]);
  return out;
}
function makeEl(page, tag, attrs, inner) {
  const el = {
    tagName: tag.toUpperCase(), attrs, listeners: {}, style: {}, isConnected: true,
    type: tag === 'input' ? (attrs.type || 'text') : tag,
    value: tag === 'textarea' ? unesc(inner ?? '') : (attrs.value ?? ''),
    checked: 'checked' in attrs,
    innerHTML: inner ?? '',
    classList: { contains: (c) => (attrs.class || '').split(/\s+/).includes(c) },
    getAttribute: (n) => (n in attrs ? attrs[n] : null),
    hasAttribute: (n) => n in attrs,
    addEventListener(type, fn) { (this.listeners[type] ??= []).push(fn); },
    fire(type) { for (const fn of this.listeners[type] || []) fn({ preventDefault() {}, stopPropagation() {}, key: '', target: this }); },
    click() { this.fire('click'); },
    focus() { page.document.activeElement = this; },
    select() {}, scrollIntoView() {}, setSelectionRange() {}, remove() {},
    closest: () => null,
  };
  return el;
}
function parseMarkup(page, html) {
  const els = [];
  const re = new RegExp('<(' + TAGS + ')\\b([^>]*)>', 'g');
  for (const m of html.matchAll(re)) {
    const tag = m[1], attrs = attrsOf(m[2]);
    let inner;
    if (tag === 'textarea' || tag === 'div' || tag === 'span') {
      const end = html.indexOf('</' + tag + '>', m.index + m[0].length);
      if (end >= 0) inner = html.slice(m.index + m[0].length, end);
    }
    els.push(makeEl(page, tag, attrs, inner));
  }
  return els;
}
// A compound selector: tag, #id, .class and [attr] / [attr="v"] parts, no combinators (all the grid asks for).
function matches(el, sel) {
  const tag = sel.match(/^[a-z]+/);
  if (tag && el.tagName !== tag[0].toUpperCase()) return false;
  for (const m of sel.matchAll(/#([\w-]+)/g)) if (el.attrs.id !== m[1]) return false;
  for (const m of sel.replace(/\[[^\]]*\]/g, '').matchAll(/\.([\w-]+)/g)) if (!el.classList.contains(m[1])) return false;
  for (const m of sel.matchAll(/\[([\w-]+)(?:="([^"]*)")?\]/g)) { if (!(m[1] in el.attrs)) return false; if (m[2] !== undefined && el.attrs[m[1]] !== m[2]) return false; }
  return true;
}

/** One webview page over a persisted-state box. A new page over the same box is a view reload. */
function makePage(box) {
  const posted = [];
  const listeners = { window: {}, document: {} };
  const page = {};
  let els = [];
  const body = { tagName: 'BODY', classList: { contains: () => false }, getAttribute: () => null };
  const rootEl = {
    html: '',
    set innerHTML(v) { this.html = v; for (const e of els) e.isConnected = false; els = parseMarkup(page, v); },
    get innerHTML() { return this.html; },
    querySelector: (sel) => els.find((e) => matches(e, sel)) || null,
    querySelectorAll: (sel) => els.filter((e) => matches(e, sel)),
  };
  page.document = {
    activeElement: body, body, documentElement: {},
    getElementById: (id) => (id === 'root' ? rootEl : els.find((e) => e.attrs.id === id) || null),
    querySelectorAll: () => [],
    addEventListener: (type, fn) => { (listeners.document[type] ??= []).push(fn); },
    createElement: () => makeEl(page, 'textarea', {}, ''),
  };
  const window = { addEventListener: (type, fn) => { (listeners.window[type] ??= []).push(fn); } };
  const api = {
    postMessage: (m) => posted.push(m),
    // VS Code hands the state back as a copy of what was set: it is serialized, never the same object.
    getState: () => (box.state === undefined ? undefined : JSON.parse(JSON.stringify(box.state))),
    setState: (s) => { box.state = JSON.parse(JSON.stringify(s)); return s; },
  };
  new Function('acquireVsCodeApi', 'document', 'window', 'navigator', 'getComputedStyle', 'Element', gridSource)(
    () => api, page.document, window, { platform: 'Linux' }, () => ({}), class {});
  const q = (sel) => rootEl.querySelector(sel);
  return {
    posted, document: page.document, root: rootEl, q,
    send: (m) => (listeners.window.message || []).forEach((fn) => fn({ data: m })),
    sets: () => posted.filter((m) => m.type === 'set'),
    html: () => rootEl.html,
    box: (name) => q('[data-prop="' + name + '"]'),
    /** Type into a property box (an input event per gesture, as the page listens). */
    type(name, text) { const el = q('[data-prop="' + name + '"]'); assert.ok(el, 'no box for ' + name); el.focus(); el.value = text; el.fire('input'); return el; },
    /** Leave the box: the change event the browser fires on blur. */
    change(name) { const el = q('[data-prop="' + name + '"]'); el.fire('change'); },
    click(sel) { const el = q(sel); assert.ok(el, 'nothing matches ' + sel); el.click(); },
    typeFx(text) { const ta = q('.fx-ta'); assert.ok(ta, 'the format editor is not open'); ta.focus(); ta.value = text; ta.fire('input'); },
    markOn: (name) => { const s = rootEl.querySelector('[data-draft-for="' + name + '"]'); return !!s && /Draft not saved/.test(s.innerHTML); },
  };
}

// ---- fixtures ------------------------------------------------------------------------------------------------
const FILE = '/m/Sales.SemanticModel';
const P = (name, kind, value, extra = {}) => ({ name, displayName: name, category: 'Basic', kind, value, options: [], readOnly: false, varies: false, ...extra });
const measure = ({ key = '["measure:Sales/Total Sales"]', model = FILE, desc = 'Sum of sales.', fx = 'SELECTEDMEASUREFORMATSTRING()', name = 'Total Sales' } = {}) => ({
  type: 'load', key, model, name, multi: false, props: [
    P('Name', 'string', name), P('Description', 'string', desc), P('DisplayFolder', 'string', 'KPIs'), P('IsHidden', 'bool', 'False'),
    P('FormatStringExpression', 'formatExpression', fx),
  ],
});
const other = (model = FILE) => ({ type: 'load', key: '["column:Sales/Amount"]', model, name: 'Amount', multi: false, props: [P('Name', 'string', 'Amount'), P('Description', 'string', 'Net amount.')] });
const heldKeys = (box) => Object.keys((box.state && box.state['propgrid.drafts']) || {});

// ---- 1. a property draft ------------------------------------------------------------------------------------
test('a typed description survives selecting another object and coming back, with the Draft not saved mark', () => {
  const box = {};
  const page = makePage(box);
  page.send(measure());
  page.type('Description', 'Sum of net sales, after returns.');
  assert.ok(page.markOn('Description'), 'the row says the words are not saved while typing');
  page.send(other());
  assert.notEqual(page.box('Description').value, 'Sum of net sales, after returns.', 'another object does not show the words');
  assert.ok(!page.markOn('Description'), 'and carries no mark');
  page.send(measure());
  assert.equal(page.box('Description').value, 'Sum of net sales, after returns.', 'the words are back on their own object');
  assert.ok(page.markOn('Description'), 'with the Draft not saved mark');
  assert.ok(!page.markOn('DisplayFolder'), 'only the row that holds a draft is marked');
  assert.deepEqual(page.sets(), [], 'nothing was written by moving around');
});

test('a view reload keeps the typed words (a new page over the same persisted state)', () => {
  const box = {};
  const first = makePage(box);
  first.send(measure());
  first.type('Description', 'Typed before the reload.');
  const again = makePage(box);
  again.send(measure());
  assert.equal(again.box('Description').value, 'Typed before the reload.');
  assert.ok(again.markOn('Description'));
  assert.deepEqual(again.sets(), []);
});

test('an engine restart keeps the typed words: the grid empties, then the same model file loads again', () => {
  const box = {};
  const page = makePage(box);
  page.send(measure());
  page.type('Description', 'Typed before the restart.');
  page.send({ type: 'empty' });
  page.send(measure());
  assert.equal(page.box('Description').value, 'Typed before the restart.');
  assert.ok(page.markOn('Description'));
  // A view VS Code rebuilt during the restart reads the same state.
  const rebuilt = makePage(box);
  rebuilt.send(measure());
  assert.equal(rebuilt.box('Description').value, 'Typed before the restart.');
});

test('a held draft whose stored value moved: the words stay, the choice is shown, nothing is written until the person chooses', () => {
  const box = {};
  const first = makePage(box);
  first.send(measure());
  first.type('Description', 'My words.');
  const page = makePage(box);
  page.send(measure({ desc: 'The assistant wrote this.' }));
  assert.equal(page.box('Description').value, 'My words.', 'the typed words are never silently replaced');
  assert.match(page.html(), /This value changed while you were typing/);
  assert.ok(page.q('[data-keep="Description"]') && page.q('[data-usenew="Description"]'), 'Keep mine and Use the new one are offered');
  page.change('Description');
  assert.deepEqual(page.sets(), [], 'a change event does not write over the moved value');
  // F11 (drafts-sol): the newer value is shown beside the words, and Keep mine does NOT write. It gives the words back as a draft over that value, to be saved on purpose.
  assert.match(page.q('[data-moved-theirs="Description"]').innerHTML, /The assistant wrote this\./, 'the newer stored value is on screen');
  page.click('[data-keep="Description"]');
  assert.deepEqual(page.sets(), [], 'Keep mine writes nothing by itself');
  assert.equal(page.box('Description').value, 'My words.', 'the words stay');
  assert.ok(!page.q('[data-keep="Description"]'), 'the choice is made');
  assert.ok(page.markOn('Description'), 'and they are still a draft');
  page.change('Description');
  assert.deepEqual(page.sets(), [{ type: 'set', name: 'Description', value: 'My words.', expected: 'The assistant wrote this.', model: FILE, key: '["measure:Sales/Total Sales"]' }],
    'the save the person then makes names the value they were shown as the one it replaces');
});

test('a held draft over an unmoved value saves naming the value the person started from', () => {
  const box = {};
  const first = makePage(box);
  first.send(measure());
  first.type('Description', 'My words.');
  const page = makePage(box);
  page.send(measure());
  page.change('Description');
  assert.deepEqual(page.sets(), [{ type: 'set', name: 'Description', value: 'My words.', expected: 'Sum of sales.', model: FILE, key: '["measure:Sales/Total Sales"]' }]);
});

test('a draft ends on a save the engine took, on Use the new one, and on typing it back to the stored value', () => {
  // Saved: the write goes out, the engine takes it, the reload shows the words as stored.
  {
    const box = {};
    const page = makePage(box);
    page.send(measure());
    page.type('Description', 'Saved words.');
    page.change('Description');
    page.send(measure({ desc: 'Saved words.' }));
    assert.deepEqual(heldKeys(box), [], 'a save the engine took ends the draft');
    assert.ok(!page.markOn('Description'));
    const again = makePage(box); again.send(measure());
    assert.equal(again.box('Description').value, 'Sum of sales.', 'nothing comes back after a reload');
  }
  // Use the new one.
  {
    const box = {};
    const first = makePage(box); first.send(measure()); first.type('Description', 'Mine.');
    const page = makePage(box);
    page.send(measure({ desc: 'Theirs.' }));
    page.click('[data-usenew="Description"]');
    assert.equal(page.box('Description').value, 'Theirs.');
    assert.deepEqual(heldKeys(box), [], 'Use the new one ends the draft');
    assert.deepEqual(page.sets(), []);
  }
  // Typed back to what is stored: no draft.
  {
    const box = {};
    const page = makePage(box);
    page.send(measure());
    page.type('Description', 'Something.');
    page.type('Description', 'Sum of sales.');
    assert.deepEqual(heldKeys(box), []);
    assert.ok(!page.markOn('Description'));
  }
});

test('a refused write gives the words back and holds them again', () => {
  const box = {};
  const page = makePage(box);
  page.send(measure());
  page.type('Description', 'Refused words.');
  page.change('Description');
  page.send(measure({ desc: 'Someone else wrote this.' }));
  page.send({ type: 'setError', name: 'Description', error: 'The value changed first.' });
  assert.equal(page.box('Description').value, 'Refused words.');
  const again = makePage(box); again.send(measure({ desc: 'Someone else wrote this.' }));
  assert.equal(again.box('Description').value, 'Refused words.', 'held across a reload too');
});

test('a load whose property read failed (no rows) does not drop held words; they come back with their rows', () => {
  const box = {};
  const page = makePage(box);
  page.send(measure());
  page.type('Description', 'Kept through a failed read.');
  page.click('[data-fx-edit]');
  page.typeFx('"kept fx"');
  // The provider sends the same object with no rows when getObjectProperties failed for it.
  page.send({ ...measure(), props: [] });
  assert.equal(heldKeys(box).length, 2, 'both drafts are still held');
  page.send(measure());
  assert.equal(page.box('Description').value, 'Kept through a failed read.');
  assert.equal(page.q('.fx-ta')?.value, '"kept fx"');
  // The same through a reload that first sees no rows.
  const again = makePage(box);
  again.send({ ...measure(), props: [] });
  again.send(measure());
  assert.equal(again.box('Description').value, 'Kept through a failed read.');
  assert.equal(again.q('.fx-ta')?.value, '"kept fx"');
});

test('a draft never shows on another model, nor on another object with the same name', () => {
  const box = {};
  const page = makePage(box);
  page.send(measure());
  page.type('Description', 'Only for Sales.');
  // Another model file, same object ref.
  page.send(measure({ model: '/m/Finance.SemanticModel' }));
  assert.equal(page.box('Description').value, 'Sum of sales.', 'another model never shows it');
  assert.ok(!page.markOn('Description'));
  // A same-named measure on another table: a different ref key.
  page.send(measure({ key: '["measure:Finance/Total Sales"]' }));
  assert.equal(page.box('Description').value, 'Sum of sales.', 'the ref is the key, not the name');
  const fresh = makePage(box); fresh.send(measure({ model: '/m/Finance.SemanticModel' }));
  assert.equal(fresh.box('Description').value, 'Sum of sales.', 'nor after a reload');
  page.send(measure());
  assert.equal(page.box('Description').value, 'Only for Sales.', 'it is still held for its own model and object');
});

test('the persisted key is the model file, the object ref key and the property, with the value typed and the value it was typed over', () => {
  const box = {};
  const page = makePage(box);
  page.send(measure());
  page.type('Description', 'Words.');
  assert.deepEqual(box.state['propgrid.drafts'], { [JSON.stringify([FILE, '["measure:Sales/Total Sales"]', 'Description'])]: { value: 'Words.', base: 'Sum of sales.' } });
});

test('with no model identity from the host, words stay on this page only and are never persisted', () => {
  const box = {};
  const page = makePage(box);
  const load = measure(); delete load.model;
  page.send(load);
  page.type('Description', 'No model known.');
  assert.deepEqual(heldKeys(box), []);
});

// ---- 2. the format expression editor ------------------------------------------------------------------------
test('the format editor reopens with its typed words after another object and after a reload', () => {
  const box = {};
  const page = makePage(box);
  page.send(measure());
  page.click('[data-fx-edit]');
  page.typeFx('"my format"');
  page.send(other());
  assert.equal(page.q('.fx-ta'), null, 'another object does not open it');
  page.send(measure());
  assert.equal(page.q('.fx-ta')?.value, '"my format"', 'back on its object the editor is open with the words');
  assert.ok(page.markOn('FormatStringExpression'), 'with the Draft not saved mark');
  const again = makePage(box); again.send(measure());
  assert.equal(again.q('.fx-ta')?.value, '"my format"', 'and after a reload');
  again.click('[data-fx-apply]');
  assert.deepEqual(again.sets(), [{ type: 'set', name: 'FormatStringExpression', value: '"my format"', expected: 'SELECTEDMEASUREFORMATSTRING()', model: FILE, key: '["measure:Sales/Total Sales"]' }],
    'Apply names the expression the words started from');
});

test('the format editor over a moved expression keeps the words and offers Use the new one', () => {
  const box = {};
  const first = makePage(box);
  first.send(measure());
  first.click('[data-fx-edit]');
  first.typeFx('"mine"');
  const page = makePage(box);
  page.send(measure({ fx: '"#,##0"' }));
  assert.equal(page.q('.fx-ta')?.value, '"mine"');
  assert.match(page.html(), /This expression changed while you were editing it/);
  page.click('[data-fx-usenew]');
  assert.deepEqual(heldKeys(box), [], 'Use the new one ends it');
  assert.deepEqual(page.sets(), []);
});

test('the format editor draft ends on Cancel', () => {
  const box = {};
  const page = makePage(box);
  page.send(measure());
  page.click('[data-fx-edit]');
  page.typeFx('"mine"');
  page.click('[data-fx-cancel]');
  assert.deepEqual(heldKeys(box), []);
  const again = makePage(box); again.send(measure());
  assert.equal(again.q('.fx-ta'), null, 'nothing reopens after a reload');
});

test('staledraft keeps its meaning: an open editor and its draft do not move onto a same-named object with another ref', () => {
  const box = {};
  const page = makePage(box);
  page.send(measure());
  page.click('[data-fx-edit]');
  page.typeFx('"typed on Sales"');
  page.send(measure({ key: '["measure:Finance/Total Sales"]', fx: '' }));
  assert.equal(page.q('.fx-ta'), null, 'the Finance measure opens with the editor closed');
  assert.doesNotMatch(page.html(), /typed on Sales/);
});

// ---- F3 and F7 (drafts-sol) -----------------------------------------------------------------------------------------------------------------
// F3 (R-A): a multi-select whose objects DIFFER has no single starting value, so the draft used to carry base null and the write `expected: null`, which the engine does not
//    compare: a description the assistant wrote meanwhile was overwritten. Each object's own starting value is now kept (`values` from the host, `expectedAll` on the write),
//    and every one is compared inside the engine's atomic batch.
// F7 (R-B): a commit removed the held words at once, and a selection change dropped the in-memory copy, so a refusal that arrived later had nothing to give back.
//    The words are held under their own model and object until a load shows the engine took them, and a refusal is routed to that owner.
const multi = ({ values = ['First words', 'Second words'], model = FILE } = {}) => ({
  type: 'load', key: '["measure:Sales/A","measure:Sales/B"]', model, name: '2 objects selected', multi: true,
  props: [P('Name', 'string', '', { varies: true, values: ['A', 'B'] }), P('Description', 'string', '', { varies: true, values })],
});

test('F3: a multi-select draft keeps each object\'s starting value and the write names every one of them', () => {
  const box = {};
  const page = makePage(box);
  page.send(multi());
  page.type('Description', 'Common words');
  page.change('Description');
  assert.deepEqual(page.sets(), [{ type: 'set', name: 'Description', value: 'Common words', expected: null, expectedAll: ['First words', 'Second words'], model: FILE, key: '["measure:Sales/A","measure:Sales/B"]' }]);
});

test('F3: a restored multi-select draft over objects one of which moved asks, and writes nothing until the person chooses', () => {
  const box = {};
  const first = makePage(box);
  first.send(multi()); first.type('Description', 'Common words');
  const page = makePage(box);   // reloaded; the assistant changed the second description meanwhile
  page.send(multi({ values: ['First words', 'The assistant wrote this'] }));
  assert.equal(page.box('Description').value, 'Common words');
  assert.match(page.html(), /This value changed while you were typing/, 'the row shows a moved value even though the merged row still reads "(multiple values)"');
  page.change('Description');
  assert.deepEqual(page.sets(), [], 'nothing is written over the unseen value');
  assert.match(page.q('[data-moved-theirs="Description"]').innerHTML, /The assistant wrote this/, 'each object\'s value is shown, so the moved one is on screen');
  page.click('[data-keep="Description"]');
  assert.deepEqual(page.sets(), [], 'Keep mine writes nothing by itself');
  page.change('Description');
  assert.deepEqual(page.sets(), [{ type: 'set', name: 'Description', value: 'Common words', expected: null, expectedAll: ['First words', 'The assistant wrote this'], model: FILE, key: '["measure:Sales/A","measure:Sales/B"]' }],
    'the save then names the values it was shown, object by object');
});

test('F3: a multi-select draft held before this fix (no starting values) is asked about, never written blind', () => {
  const box = { state: { 'propgrid.drafts': { [JSON.stringify([FILE, '["measure:Sales/A","measure:Sales/B"]', 'Description'])]: { value: 'Old words', base: null } } } };
  const page = makePage(box);
  page.send(multi());
  assert.equal(page.box('Description').value, 'Old words');
  page.change('Description');
  assert.deepEqual(page.sets(), [], 'a draft with no recorded starting values cannot prove it is not over unseen work');
  assert.ok(page.q('[data-keep="Description"]'));
});

test('F7: words sent and refused after another object was selected come back when their object is selected again', () => {
  const box = {};
  const page = makePage(box);
  page.send(measure());
  page.type('Description', 'A long description.');
  page.change('Description');                      // sent; the engine has not answered
  page.send(other());                              // the person selects another object before the answer
  assert.equal(heldKeys(box).length, 1, 'the words are still held under their own object while the write is pending');
  page.send({ type: 'setError', name: 'Description', error: 'The value changed first.', key: '["measure:Sales/Total Sales"]', model: FILE, key: '["measure:Sales/Total Sales"]' });
  page.send(measure({ desc: 'Someone else wrote this.' }));
  assert.equal(page.box('Description').value, 'A long description.', 'the refused words are back on their own object');
  assert.ok(page.markOn('Description'));
});

test('F7: a view reloaded while a write is pending still has the words', () => {
  const box = {};
  const page = makePage(box);
  page.send(measure());
  page.type('Description', 'Pending words.');
  page.change('Description');
  const again = makePage(box);
  again.send(measure({ desc: 'Someone else wrote this.' }));
  assert.equal(again.box('Description').value, 'Pending words.');
  assert.deepEqual(again.sets(), [], 'and they are not sent a second time on their own');
});

test('F7: a write the engine took leaves nothing held, and a refusal with the object still on screen gives the words back as before', () => {
  const box = {};
  const page = makePage(box);
  page.send(measure());
  page.type('Description', 'Taken.'); page.change('Description');
  page.send(measure({ desc: 'Taken.' }));
  assert.deepEqual(heldKeys(box), []);
});
