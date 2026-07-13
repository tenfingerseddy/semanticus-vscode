// Properties grid webview logic. Loaded by the extension's PropertyGridProvider AND by the uishot screenshot
// harness (tools/uishot/propgrid.html), so this file is the single source of truth — edit here, not inline.
const vscode = acquireVsCodeApi();
const root = document.getElementById('root');
let state = null;
let filter = '';
const collapsed = new Set();   // collapsed category names (persist across object switches in this view)
let lastError = null;          // { name, error } from the most recent failed set
let templates = null;          // format-template catalog (pushed once by the provider; null until then)
let comboOpen = null;          // property name whose format-string picker popup is open
const fx = { open: false, draft: null };   // Format expression editor state (draft survives re-renders + failed applies)

window.addEventListener('message', e => {
  const m = e.data;
  if (m.type === 'load') {
    // Selection identity = the provider's ref key, NOT the header name: two same-named objects on different
    // tables are different targets, and a surviving draft could be Applied to the wrong one. (Name is only a
    // fallback for a host that doesn't send a key.)
    const selKey = (x) => x.key != null ? x.key : x.name;
    if (state && selKey(state) !== selKey(m)) { fx.open = false; fx.draft = null; comboOpen = null; }   // new selection → fresh editors
    state = m; lastError = null;
    // A draft that matches the canonical value has landed (or was never changed) — drop it so later edits
    // by the other driver aren't shadowed by a stale draft.
    if (fx.draft != null) { const p = (state.props || []).find(x => x.kind === 'formatExpression'); if (p && p.value === fx.draft.trim()) fx.draft = null; }
    render();
  }
  else if (m.type === 'setError') { lastError = { name: m.name, error: m.error }; render(); }
  else if (m.type === 'formatTemplates') { templates = m.templates || []; if (state) render(); }
  else if (m.type === 'empty') { state = null; root.innerHTML = '<div class="empty">Open a model to inspect its properties. Select model objects to edit them; clear the selection to return to model settings.</div>'; }
});
function esc(s){ return String(s==null?'':s).replace(/[&<>"]/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;'}[c])); }
// Multiline editor only for genuinely multi-line content — Description, DAX/expression bodies, or any value that
// actually contains a newline. NOT length-based: long single-line values (Format String, lineage tags, source
// queries) stay as text inputs (they scroll horizontally) instead of overflowing a short textarea and showing
// scrollbar arrows that read like number-spinners.
function isMultiline(p){ return p.kind === 'string' && (p.name === 'Description' || /Expression/.test(p.name) || (p.value||'').indexOf('\n') >= 0); }
function oneLine(s){ return String(s||'').replace(/\s+/g, ' ').trim(); }

function render() {
  if (!state) return;
  const f = filter.trim().toLowerCase();
  const groups = {};
  for (const p of state.props) {
    if (f && !((p.displayName||'').toLowerCase().includes(f) || (p.name||'').toLowerCase().includes(f))) continue;
    (groups[p.category] = groups[p.category] || []).push(p);
  }
  let h = '<div class="hdr">' + esc(state.name) + (state.multi ? ' <span class="multi">(multi-edit)</span>' : '') + '</div>';
  h += '<div class="search"><input id="f" type="search" placeholder="Filter properties…" value="' + esc(filter) + '"></div>';
  const cats = Object.keys(groups);
  if (cats.length === 0) h += '<div class="empty">No properties match “' + esc(filter) + '”.</div>';
  for (const cat of cats) {
    const isCol = collapsed.has(cat);
    h += '<div class="cat' + (isCol ? ' collapsed' : '') + '" data-cat="' + esc(cat) + '"><span class="twist">▾</span>' + esc(cat) + '</div>';
    if (!isCol) for (const p of groups[cat]) h += rowHtml(p);
  }
  root.innerHTML = h;
  wire();
}

function rowHtml(p) {
  const dp = 'data-prop="' + esc(p.name) + '"';
  const ph = p.varies ? ' placeholder="(multiple values)"' : '';
  const errHere = lastError && lastError.name === p.name;
  const inv = errHere ? ' invalid' : '';
  let editor;
  if (p.kind === 'formatExpression') editor = fxHtml(p);   // before the readOnly branch: a locked row states WHY
        else if (p.readOnly) editor = '<span class="ro" title="' + esc(p.value) + '">' + (p.varies ? '<em>(multiple)</em>' : esc(p.value) || 'Not set') + '</span>';
  else if (p.kind === 'bool') editor = '<input type="checkbox" class="' + inv + '" ' + dp + (p.value === 'True' ? ' checked' : '') + (p.varies ? ' indeterminate' : '') + '>';
  else if (p.kind === 'enum') editor = '<select class="' + inv + '" ' + dp + '>' + (p.varies ? '<option value="" disabled selected>(multiple)</option>' : '') + (p.options||[]).map(o => '<option' + (!p.varies && o === p.value ? ' selected' : '') + '>' + esc(o) + '</option>').join('') + '</select>';
  else if (p.kind === 'number') editor = '<input type="number" class="' + inv + '" ' + dp + ph + ' value="' + esc(p.value) + '">';
  else if (isMultiline(p)) editor = '<textarea class="' + inv + '" ' + dp + ph + ' rows="' + Math.min(8, Math.max(2, (p.value||'').split('\n').length)) + '">' + esc(p.value) + '</textarea>';
  else {
    // Plain text input — plus the two prefill affordances: engine-supplied suggestions (e.g. display folders
    // already in use) as a native autocomplete, and the curated format-string picker on Format String rows.
    // Free text always stays available.
    const dl = (p.suggestions && p.suggestions.length) ? ' list="dl-' + esc(p.name) + '"' : '';
    editor = '<input type="text" class="' + inv + '" ' + dp + ph + dl + ' value="' + esc(p.value) + '">';
    if (dl) editor += '<datalist id="dl-' + esc(p.name) + '">' + p.suggestions.map(s => '<option value="' + esc(s) + '">').join('') + '</datalist>';
    if (p.name === 'FormatString' && templates && templates.length) {
      editor = '<div class="combo">' + editor
        + '<button class="combo-btn" data-combo="' + esc(p.name) + '" title="Pick a common format">▾</button>'
        + (comboOpen === p.name ? comboPopHtml() : '') + '</div>';
    }
  }
  const err = errHere ? '<div class="err">' + esc(lastError.error) + '</div>' : '';
  const hint = (p.hint && p.kind !== 'formatExpression') ? '<div class="hint">' + esc(p.hint) + '</div>' : '';
  return '<div class="rowx"><label title="' + esc(p.description || p.name) + '">' + esc(p.displayName) + '</label>' + editor + err + hint + '</div>';
}

// The Format String picker popup: the catalog's pinned "common" entries grouped by category, each with its
// documented example output as a live preview. Picking one writes through the same edit path as typing it.
function comboPopHtml() {
  const items = templates.filter(t => t.kind === 'static' && t.common);
  let h = '<div class="combo-pop">';
  let cat = '';
  for (const t of items) {
    if (t.category !== cat) { cat = t.category; h += '<div class="combo-cat">' + esc(cat) + '</div>'; }
    h += '<div class="combo-item" data-fmt="' + esc(t.formatString) + '" title="' + esc(t.formatString + (t.note ? '\n' + t.note : '')) + '">'
       + '<span>' + esc(t.name) + '</span><code>' + esc(t.exampleOutput) + '</code></div>';
  }
  h += '<div class="combo-note">Or type your own format in the box.</div></div>';
  return h;
}

// The measure Format expression row: a status line (does a dynamic format exist?) with an Edit affordance that
// expands into a lightweight DAX editor — monospace textarea, insert-from-template picker (the catalog's dynamic
// entries), Apply/Cancel. Below compatibility level 1601 the row states the requirement instead of a dead control.
function fxHtml(p) {
  if (p.readOnly) return '<span class="ro fx-locked">' + esc(p.hint || 'Not available for this model.') + '</span>';
  const has = !p.varies && (p.value || '').trim().length > 0;
  const status = p.varies ? '<em class="fx-none">(multiple values)</em>'
    : has ? '<code class="fx-prev" title="' + esc(p.value) + '">' + esc(oneLine(p.value)) + '</code>'
    : '<span class="fx-none">None (the static format applies)</span>';
  let h = '<div class="fx-row">' + status + '<button class="btn" data-fx-edit>' + (has ? 'Edit' : 'Add') + '</button></div>';
  if (fx.open) {
    const dyn = (templates || []).filter(t => t.kind === 'dynamic');
    let pick = '';
    if (dyn.length) {
      let cat = '', opts = '<option value="">Insert a template…</option>';
      for (const t of dyn) {
        if (t.category !== cat) { if (cat) opts += '</optgroup>'; cat = t.category; opts += '<optgroup label="' + esc(cat) + '">'; }
        opts += '<option value="' + esc(t.formatString) + '">' + esc(t.name) + '</option>';
      }
      if (cat) opts += '</optgroup>';
      pick = '<select class="fx-tpl">' + opts + '</select>';
    }
    const val = fx.draft != null ? fx.draft : (p.varies ? '' : p.value || '');
    h += '<div class="fx-panel">' + pick
      + '<textarea class="fx-ta" spellcheck="false" rows="' + Math.min(10, Math.max(3, val.split('\n').length + 1)) + '">' + esc(val) + '</textarea>'
      + '<div class="fx-msg"></div>'
      + '<div class="fx-actions"><button class="btn primary" data-fx-apply>Apply</button><button class="btn" data-fx-cancel>Cancel</button>'
      + '<span class="hint">Apply with an empty box to go back to the static format string.</span></div>'
      + '</div>';
  }
  return h;
}

// Cheap structural check before an apply — balanced quotes/parentheses OUTSIDE string literals and quoted names
// ("" and '' are the DAX escapes). Catches the obvious paste accidents without pretending to be a DAX parser.
function daxBalanceProblem(s) {
  let depth = 0, inStr = false, inName = false;
  for (let i = 0; i < s.length; i++) {
    const c = s[i];
    if (inStr) { if (c === '"') { if (s[i + 1] === '"') i++; else inStr = false; } continue; }
    if (inName) { if (c === "'") { if (s[i + 1] === "'") i++; else inName = false; } continue; }
    if (c === '"') inStr = true;
    else if (c === "'") inName = true;
    else if (c === '(') depth++;
    else if (c === ')') { depth--; if (depth < 0) return 'There is a ")" without a matching "(".'; }
  }
  if (inStr) return 'A double-quoted text is not closed.';
  if (inName) return 'A single-quoted name is not closed.';
  if (depth > 0) return 'A "(" is not closed.';
  return null;
}

function autoGrow(ta){ ta.style.height = 'auto'; ta.style.height = (ta.scrollHeight + 2) + 'px'; }
function wire() {
  const fi = document.getElementById('f');
  if (fi) fi.addEventListener('input', () => { filter = fi.value; const at = fi.selectionStart; render(); const nf = document.getElementById('f'); if (nf) { nf.focus(); try { nf.setSelectionRange(at, at); } catch(_){} } });
  root.querySelectorAll('textarea').forEach(ta => { autoGrow(ta); ta.addEventListener('input', () => autoGrow(ta)); });   // size to content → no overflow scrollbar arrows
  root.querySelectorAll('.cat').forEach(el => el.addEventListener('click', () => { const c = el.getAttribute('data-cat'); if (collapsed.has(c)) collapsed.delete(c); else collapsed.add(c); render(); }));
  root.querySelectorAll('[data-prop]').forEach(el => {
    if (el.hasAttribute('indeterminate')) el.indeterminate = true;   // multi-select varies → tri-state checkbox
    el.addEventListener('change', () => {
      lastError = null;
      const value = el.type === 'checkbox' ? String(el.checked) : el.value;
      vscode.postMessage({ type: 'set', name: el.getAttribute('data-prop'), value });
    });
  });

  // Format-string picker: toggle per row; pick → write through the normal edit path; click-away closes.
  root.querySelectorAll('.combo-btn').forEach(b => b.addEventListener('click', (e) => {
    e.stopPropagation();
    const name = b.getAttribute('data-combo');
    comboOpen = comboOpen === name ? null : name;
    render();
  }));
  root.querySelectorAll('.combo-item').forEach(it => it.addEventListener('click', () => {
    lastError = null;
    vscode.postMessage({ type: 'set', name: comboOpen, value: it.getAttribute('data-fmt') });
    comboOpen = null;
    render();
  }));

  // Format expression editor.
  const fxProp = (state.props || []).find(x => x.kind === 'formatExpression');
  const fxEdit = root.querySelector('[data-fx-edit]');
  if (fxEdit) fxEdit.addEventListener('click', () => { fx.open = true; render(); });
  const fxTa = root.querySelector('.fx-ta');
  if (fxTa) fxTa.addEventListener('input', () => { fx.draft = fxTa.value; });
  const fxTpl = root.querySelector('.fx-tpl');
  if (fxTpl) fxTpl.addEventListener('change', () => {
    const v = fxTpl.value;
    if (v && fxTa) { fxTa.value = v; fx.draft = v; autoGrow(fxTa); fxTa.focus(); }
    fxTpl.selectedIndex = 0;
  });
  const fxCancel = root.querySelector('[data-fx-cancel]');
  if (fxCancel) fxCancel.addEventListener('click', () => { fx.open = false; fx.draft = null; render(); });
  const fxApply = root.querySelector('[data-fx-apply]');
  if (fxApply) fxApply.addEventListener('click', () => {
    const value = (fxTa ? fxTa.value : '').trim();
    const problem = value ? daxBalanceProblem(value) : null;
    const msg = root.querySelector('.fx-msg');
    if (problem) { if (msg) msg.textContent = problem; return; }
    lastError = null;
    fx.draft = fxTa ? fxTa.value : null;   // keep the draft: a refusal re-opens with the text intact
    fx.open = false;
    vscode.postMessage({ type: 'set', name: fxProp ? fxProp.name : 'FormatStringExpression', value });
    render();
  });
}
document.addEventListener('click', (e) => {
  if (comboOpen && !(e.target instanceof Element && e.target.closest('.combo'))) { comboOpen = null; render(); }
});
vscode.postMessage({ type: 'ready' });
