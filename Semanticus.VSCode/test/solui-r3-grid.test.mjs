// Join 11 solui, fix round 3: the Properties grid keeps typed words in sight when the object they were typed on is not the one shown (a rename, a delete, a failed read, another object,
// another model, a view reload). Every held key is in a box, in flight (sent, not yet answered), or counted on one collapsed line, "Words kept for N other objects", with Copy and Discard.
// The line is collapsed by default (another object's words are never drawn into the page until the person opens it: propgrid-drafts.test.mjs). The host says `setOk` when the engine took a
// write, so an accepted write for an object that is not on screen does not come back as a false "kept" line after the view is hidden and shown.
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
function makePage(box, clipboard = []) {
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
    () => api, page.document, window, { platform: 'Linux', clipboard: { writeText: (t) => { clipboard.push(t); return Promise.resolve(); } } }, () => ({}), class {});
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
const OTHER_FILE = '/m/Finance.SemanticModel';
const GP = (name, kind, value, extra = {}) => ({ name, displayName: name, category: 'Basic', kind, value, options: [], readOnly: false, varies: false, ...extra });
const A = '["measure:Sales/Total Sales"]', B = '["column:Sales/Amount"]', REVENUE = '["measure:Sales/Revenue"]';
const load = (key, name, desc = 'Sum of sales.', model = FILE) => ({ type: 'load', key, model, name, multi: false, props: [GP('Name', 'string', name), GP('Description', 'string', desc)] });
const failedRead = (key, name, model = FILE) => ({ type: 'load', key, model, name, multi: false, props: [] });
const WORDS = 'Net sales after returns, in AUD. Excludes intercompany.';
const heldOf = (box) => (box.state && box.state['propgrid.drafts']) || {};
const heldKeys = (box) => Object.keys(heldOf(box));
const keptLine = (page) => { const m = /Words kept for (1|\d+) other objects?/.exec(page.html()); return m ? Number(m[1]) : 0; };
const onScreen = (page, words) => page.root.querySelectorAll('textarea').some((e) => e.value === words) || page.root.querySelectorAll('input').some((e) => e.value === words);

/** The invariant: every held key is in a box, in flight (the caller lists them), or counted on the line. Returns the held objects that are in none of the three. */
function unaccounted(page, box, shown, inflightKeys = []) {
  const keys = heldKeys(box).filter((k) => !inflightKeys.includes(k));
  const lost = [];
  const others = new Set();
  for (const k of keys) {
    const [model, object] = JSON.parse(k);
    const inBox = model === shown.model && object === shown.key && onScreen(page, heldOf(box)[k].value);
    if (!inBox) others.add(JSON.stringify([model, object]));
  }
  if (keptLine(page) !== others.size) lost.push(`the line says ${keptLine(page)} but ${others.size} other object(s) hold words`);
  return lost;
}

test('G1 invariant: a rename of the selected object, a delete, a failed read, another object, another model and a reload each leave every held key in a box or on the line', () => {
  const shown = { model: FILE, key: B };
  const cases = {
    'another object': (p) => { p.send(load(B, 'Amount')); },
    'a rename (the old ref reads with no rows, then the grid follows the new name)': (p) => { p.send(failedRead(A, 'Total Sales')); p.send(load(REVENUE, 'Revenue')); shown.key = REVENUE; },
    'a delete (an empty grid, then another object)': (p) => { p.send({ type: 'empty' }); p.send(load(B, 'Amount')); },
    'another model': (p) => { p.send(load(B, 'Amount', 'Net amount.', OTHER_FILE)); shown.model = OTHER_FILE; },
  };
  for (const [name, move] of Object.entries(cases)) {
    shown.model = FILE; shown.key = B;
    const box = {};
    let page = makePage(box);
    page.send(load(A, 'Total Sales'));
    page.type('Description', WORDS);
    move(page);
    assert.deepEqual(unaccounted(page, box, shown), [], name);
    assert.equal(keptLine(page), 1, name + ': one object holds words');
    // A view reload (a new page over the same persisted state) shows the same line.
    page = makePage(box);
    page.send(load(shown.key, 'x', 'y', shown.model));
    assert.deepEqual(unaccounted(page, box, shown), [], name + ', after a reload');
  }
  // A failed read of the very object on screen: its words are kept, not shown, and the line says so.
  const box = {};
  const page = makePage(box);
  page.send(load(A, 'Total Sales'));
  page.type('Description', WORDS);
  page.send(failedRead(A, 'Total Sales'));
  assert.deepEqual(unaccounted(page, box, { model: FILE, key: A }), []);
  assert.equal(keptLine(page), 1);
  // And the words come back with the row.
  page.send(load(A, 'Total Sales'));
  assert.equal(page.box('Description').value, WORDS);
  assert.equal(keptLine(page), 0, 'in a box again: not on the line');
});

test('G2 the line is collapsed: another object\'s words are not in the page until the person opens it; Copy gives the words, Discard drops them', () => {
  const box = {}, copied = [];
  const page = makePage(box, copied);
  page.send(load(A, 'Total Sales'));
  page.type('Description', WORDS);
  page.send(load(B, 'Amount'));
  assert.equal(keptLine(page), 1);
  assert.ok(!page.html().includes('Net sales after returns'), 'collapsed: the words are not drawn');
  page.click('[data-kept-toggle]');
  assert.ok(page.html().includes('Net sales after returns'), 'opened: the words are named');
  assert.match(page.html(), /Sales \/ Total Sales/, 'with the object they were typed for');
  page.click('[data-kept-copy="0"]');
  assert.equal(copied.length, 1); assert.ok(copied[0].includes(WORDS));
  page.click('[data-kept-discard="0"]');
  assert.equal(heldKeys(box).length, 0, 'Discard drops the words');
  assert.equal(keptLine(page), 0);
});

test('G3 setOk: an accepted write for an object that is not on screen ends its hold, so a hidden and shown view has no false "kept" line', () => {
  const box = {};
  let page = makePage(box);
  page.send(load(A, 'Total Sales'));
  page.type('Description', WORDS);
  page.change('Description');
  assert.equal(page.sets().length, 1);
  page.send(load(B, 'Amount'));   // the person selects another object while the write is on its way
  assert.equal(keptLine(page), 0, 'a write on its way is in flight, not "kept"');
  assert.equal(heldKeys(box).length, 1, 'and it is still held under its own object until the engine answers');
  page.send({ type: 'setOk', model: FILE, key: A, name: 'Description', value: WORDS });
  assert.equal(heldKeys(box).length, 0, 'the engine took it: the hold ends with no load of that object');
  // The view is hidden and shown again (no retainContextWhenHidden): a new page over the same state.
  page = makePage(box);
  page.send(load(B, 'Amount'));
  assert.equal(keptLine(page), 0, 'no false line');
  // The control: without setOk the in-flight words come back as "kept" on a new page (the page cannot know the engine took them).
  const box2 = {};
  let p2 = makePage(box2);
  p2.send(load(A, 'Total Sales')); p2.type('Description', WORDS); p2.change('Description'); p2.send(load(B, 'Amount'));
  p2 = makePage(box2); p2.send(load(B, 'Amount'));
  assert.equal(keptLine(p2), 1, 'control: a write never confirmed is listed, the safe direction');
});

test('G3 setOk never clears newer words: words typed after the write was sent stay held', () => {
  const box = {};
  const page = makePage(box);
  page.send(load(A, 'Total Sales'));
  page.type('Description', 'first words');
  page.change('Description');
  page.type('Description', 'second words');
  page.send({ type: 'setOk', model: FILE, key: A, name: 'Description', value: 'first words' });
  const held = Object.values(heldOf(box)).map((h) => h.value);
  assert.deepEqual(held, ['second words'], 'the newer words are still held');
  assert.equal(page.box('Description').value, 'second words');
  // The value is compared trimmed, as the engine trims: an accepted write with a trailing space ends the hold.
  const box2 = {};
  const p2 = makePage(box2);
  p2.send(load(A, 'Total Sales')); p2.type('Description', 'trimmed words '); p2.change('Description'); p2.send(load(B, 'Amount'));
  p2.send({ type: 'setOk', model: FILE, key: A, name: 'Description', value: 'trimmed words' });
  assert.equal(heldKeys(box2).length, 0);
});
