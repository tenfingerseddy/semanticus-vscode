// Verifier, join 11 solui round 3 check (item 4, the Properties grid kept line). LEFT FAILING on purpose.
// Trigger: the person's own write is on its way (the change event on leaving the box sent it), the person goes back into the box and types newer words, then selects
// another object before the engine answers (a busy engine: the single writer is held by a long assistant operation). persistObject writes EVERY in-flight entry over the
// held state (propgrid.js persistObject, the `for (const [k, v] of inflight)` loop), so on the new object's render the older sent words overwrite the newer typed ones
// under the first object's key; the newer words are in no box, not in flight and not on the line. Round 3's setOk then ends the hold on the older words, and nothing is left.
// Pre-existing on join aacc05ec (the drafts-sol F7 in-flight hold); not introduced by round 3, which only adds the setOk that clears the remaining trace.
// The page harness is the lane's own (test/solui-r3-grid.test.mjs), copied unchanged.
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


test('V-R3-G1. newer words typed while the person\'s own write is in flight survive selecting another object, and the engine\'s OK for the older words does not end them', { todo: 'Deferred by Kane 6 Oct 2026: after the M11 release' }, () => {
  const box = {};
  const page = makePage(box);
  page.send(load(A, 'Total Sales'));
  page.type('Description', 'first words');
  page.change('Description');                 // sent; the engine has not answered
  assert.equal(page.sets().length, 1);
  page.type('Description', 'second words');   // the person goes back in and types more
  assert.equal(heldOf(box)[JSON.stringify([FILE, A, 'Description'])].value, 'second words', 'control: held while on screen');
  page.send(load(B, 'Amount'));               // then selects another object before the answer
  const kA = JSON.stringify([FILE, A, 'Description']);
  assert.equal(heldOf(box)[kA]?.value, 'second words', 'the newer words are still held under their object after selecting another one');
  page.send({ type: 'setOk', model: FILE, key: A, name: 'Description', value: 'first words' });
  assert.equal(heldOf(box)[kA]?.value, 'second words', 'the OK for the older words does not end the newer ones');
  assert.equal(keptLine(page), 1, 'and the line names them');
});
