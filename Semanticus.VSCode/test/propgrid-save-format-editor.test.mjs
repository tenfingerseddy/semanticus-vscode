// Sol join 6, C3: Save Model (Ctrl+S, the palette) must not apply an open format expression editor it was not asked to.
//
// WHAT WENT WRONG. The Properties grid's "apply the focused field" ran for every Save. It clicked the format editor's Apply button whenever
// that editor was OPEN, before looking at focus. Two selected measures with different dynamic formats open the editor EMPTY (the values differ);
// with nothing typed and the focus elsewhere, Ctrl+S sent that empty value to every selected measure and removed each one's dynamic format. The same
// forced Apply also wrote a stale draft over a newer expression when the page had already marked the draft as moved.
//
// RULE. A Save commits the one field the person is in. The format editor counts only while its own box has focus AND the person typed a change AND
// its stored expression has not moved; then the person chooses with Apply or "Use the new one".
//
// The REAL media/propgrid/propgrid.js runs in a small fake page whose root hands back the editor's buttons the way the markup would.
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const gridSource = readFileSync(join(root, 'media', 'propgrid', 'propgrid.js'), 'utf8');

function makePage() {
  const posted = [];
  const listeners = { window: {}, document: {} };
  const els = {};
  const mk = (name, props = {}) => (els[name] = { name, listeners: {}, classList: { contains: (c) => (props.classes || []).includes(c) }, getAttribute: () => null, value: '',
    addEventListener(type, fn) { (this.listeners[type] ??= []).push(fn); }, click() { (this.listeners.click || []).forEach((fn) => fn({})); }, focus() { page.document.activeElement = this; }, ...props });
  const ta = mk('ta', { classes: ['fx-ta'], tagName: 'TEXTAREA' });
  const apply = mk('apply'); const edit = mk('edit'); const cancel = mk('cancel'); const useNew = mk('usenew');
  const body = { tagName: 'BODY', classList: { contains: () => false }, getAttribute: () => null };
  const rootEl = {
    html: '',
    // new markup, new elements: the old listeners die with them
    set innerHTML(v) { this.html = v; for (const e of Object.values(els)) e.listeners = {}; },
    get innerHTML() { return this.html; },
    querySelector(sel) {
      const open = this.html.includes('data-fx-apply');
      const map = { '[data-fx-apply]': open && apply, '.fx-ta': open && ta, '[data-fx-edit]': this.html.includes('data-fx-edit') && edit,
        '[data-fx-cancel]': open && cancel, '[data-fx-usenew]': this.html.includes('data-fx-usenew') && useNew };
      return map[sel] || null;
    },
    querySelectorAll: () => [],
  };
  const page = {
    document: {
      activeElement: body, body, documentElement: {},
      getElementById: (id) => (id === 'root' ? rootEl : null),
      querySelectorAll: () => [],
      addEventListener: (type, fn) => { (listeners.document[type] ??= []).push(fn); },
    },
  };
  const window = { addEventListener: (type, fn) => { (listeners.window[type] ??= []).push(fn); } };
  const Element = class {};
  new Function('acquireVsCodeApi', 'document', 'window', 'navigator', 'getComputedStyle', 'Element', gridSource)(
    () => ({ postMessage: (m) => posted.push(m) }), page.document, window, { platform: 'Linux' }, () => ({}), Element);
  return {
    posted, els, ta, apply, edit, body, document: page.document, rootEl,
    send: (m) => listeners.window.message.forEach((fn) => fn({ data: m })),
    /** What a Save asks of the page: the host posts "applyFocused" and the page answers "applied" after any write. */
    save: () => { const before = posted.length; listeners.window.message.forEach((fn) => fn({ data: { type: 'applyFocused', id: 7 } })); return posted.slice(before); },
    sets: () => posted.filter((m) => m.type === 'set'),
  };
}

const measure = (varies, value = '') => ({ type: 'load', key: 'm', name: varies ? '2 measures' : 'Sales', multi: varies, props: [
  { name: 'FormatStringExpression', displayName: 'Format expression', kind: 'formatExpression', category: 'Formatting', value, varies },
] });
const typeInto = (page, text) => { page.ta.value = text; (page.ta.listeners.input || []).forEach((fn) => fn({})); };
const openEditor = (page) => { page.edit.click(); };

// 1. Sol's probe: two measures with different formats, the editor open and EMPTY, nothing typed, focus elsewhere, Ctrl+S.
{
  const page = makePage();
  page.send(measure(true));
  openEditor(page);
  assert.ok(page.rootEl.innerHTML.includes('data-fx-apply'), 'the editor is open');
  const answered = page.save();
  assert.deepEqual(page.sets(), [], 'a Save writes nothing from an editor nobody typed in');
  assert.deepEqual(answered.map((m) => m.type), ['applied'], 'the Save still gets its answer');
}

// 2. The same, with the focus left in the empty box: still nothing typed, still nothing written.
{
  const page = makePage();
  page.send(measure(true));
  openEditor(page);
  page.ta.focus();
  page.save();
  assert.deepEqual(page.sets(), [], 'an empty box the person only clicked into is not a change');
}

// 3. Typed text, but the focus moved to another field: only the field that has focus is committed.
{
  const page = makePage();
  page.send(measure(false, 'OLD'));
  openEditor(page);
  typeInto(page, 'NEW');
  page.document.activeElement = page.body;
  page.save();
  assert.deepEqual(page.sets(), [], 'a draft in an editor that does not have the focus is not applied by a Save');
}

// 4. Typed text with the focus in the box: Save commits it (the one thing the Save is for).
{
  const page = makePage();
  page.send(measure(false, 'OLD'));
  openEditor(page);
  typeInto(page, 'NEW');
  page.ta.focus();
  page.save();
  assert.deepEqual(page.sets().map((m) => [m.name, m.value]), [['FormatStringExpression', 'NEW']], 'the focused, typed editor is committed');
}

// 5. Typed text in a multi-select editor (empty base): committed when focused.
{
  const page = makePage();
  page.send(measure(true));
  openEditor(page);
  typeInto(page, 'SELECTEDMEASUREFORMATSTRING()');
  page.ta.focus();
  page.save();
  assert.deepEqual(page.sets().map((m) => m.value), ['SELECTEDMEASUREFORMATSTRING()']);
}

// 6. The stored expression moved under the typed words: a Save never picks for the person.
{
  const page = makePage();
  page.send(measure(false, 'OLD'));
  openEditor(page);
  typeInto(page, 'MINE');
  page.ta.focus();
  page.send(measure(false, 'THEIRS'));   // the assistant wrote a different expression while the editor is open
  assert.ok(page.rootEl.innerHTML.includes('data-fx-usenew'), 'the page offers the choice');
  page.ta.focus();
  page.save();
  assert.deepEqual(page.sets(), [], 'a Save does not write the stale draft over the newer expression');
  page.apply.click();   // the person chooses Apply: an explicit act
  assert.deepEqual(page.sets().map((m) => m.value), ['MINE'], 'the explicit Apply still writes the person\'s words');
}

// 7. Clearing an existing expression on purpose (typed, focused, differs from the stored one) is a change and is committed.
{
  const page = makePage();
  page.send(measure(false, 'OLD'));
  openEditor(page);
  typeInto(page, '');
  page.ta.focus();
  page.save();
  assert.deepEqual(page.sets().map((m) => m.value), [''], 'emptying a stored expression on purpose still goes through');
}

console.log('propgrid save format editor: ok');
