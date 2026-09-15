import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

// ONE control scale, enforced at the source. Tools kept re-inventing their own button size (px-3 py-1.5
// here, px-2.5 py-1 there, px-5 py-1.5 on Run), so the same action was a different control on every page:
// the DAX Lab toolbar shipped 32px / 30.75px / 26px side by side and the Published mode row shipped 32px
// next to 28px. The shared classes in styles.css own height, padding, radius and weight; a call site that
// re-states any of them silently wins over the scale again, so re-stating them is what this file refuses.
//
// One run reports EVERY drift, not just the first. A scale regression usually lands in several call sites
// at once, and failing on the first one hides how wide it is.
const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const read = (file) => readFileSync(resolve(root, file), 'utf8');
const styles = read('webview/src/styles.css');
const daxlab = read('webview/src/daxlab.tsx');
const deploy = read('webview/src/deploy.tsx');
const compare = read('webview/src/compare.tsx');
const diffview = read('webview/src/diffview.tsx');
const app = read('webview/src/App.tsx');

const failures = [];
const check = (name, fn) => { try { fn(); } catch (e) { failures.push(`${name}: ${e.message}`); } };
// assert.match prints the whole haystack on failure; these files are 30-90 kB, so say it ourselves.
const has = (src, re, msg) => { if (!re.test(src)) throw new Error(msg); };

// ---- the scale itself -------------------------------------------------------------------------
check('control scale tokens', () => {
  has(styles, /--sem-control-h:\s*28px/, 'the shared control height must stay 28px');
  has(styles, /--sem-control-h-sm:\s*24px/, 'the shared dense control height must stay 24px');
  has(styles, /\.sem-btn\s*\{[\s\S]*?height:\s*var\(--sem-control-h\)/, '.sem-btn must own its height');
  has(styles, /\.sem-seg-item\s*\{[\s\S]*?height:\s*var\(--sem-control-h-sm\)/, '.sem-seg-item must own its height');
  // A full-size button can also carry a pressed state (the Published mode row), so the scale owns that
  // look too rather than each call site painting its own border.
  has(styles, /\.sem-btn\[aria-pressed="true"\]/, 'the scale must define the pressed look for a full-size .sem-btn');
  // Tab strips are not segments (see the .sem-tab block in styles.css for why), but their geometry still
  // comes from the scale: the sub-tab row dense, the primary area row above it full size.
  has(styles, /\.sem-tab\s*\{[\s\S]*?height:\s*var\(--sem-control-h-sm\)/, '.sem-tab must take the dense height from the scale');
  has(styles, /\.sem-tab-lg\s*\{[\s\S]*?height:\s*var\(--sem-control-h\)/, '.sem-tab-lg must take the full height from the scale');
});

// ---- helpers ----------------------------------------------------------------------------------
// The opening <button ...> tag around a needle. JSX attributes hold `=>` and nested braces, so the tag
// ends at the first `>` that is outside every brace and string, not at the first `>` at all.
function buttonTag(src, needle, what) {
  const at = src.indexOf(needle);
  if (at === -1) throw new Error(`could not find ${what} (looked for ${JSON.stringify(needle)})`);
  const start = src.lastIndexOf('<button', at);
  if (start === -1) throw new Error(`${what} is not inside a <button>`);
  let depth = 0, quote = null;
  for (let i = start; i < src.length; i++) {
    const c = src[i];
    if (quote) { if (c === quote) quote = null; continue; }
    if (c === '"' || c === "'" || c === '`') { quote = c; continue; }
    if (c === '{') depth++;
    else if (c === '}') depth--;
    else if (c === '>' && depth === 0) return src.slice(start, i + 1);
  }
  throw new Error(`${what}: unterminated <button> tag`);
}

// Bounded by the next top-level declaration rather than by the first `}` in column 0: a component whose
// props type is written across several lines closes a brace in column 0 partway through its own signature
// (`}) {`), and cutting there silently hands back a body with no JSX in it.
const NEXT_DECL = /\n(?:export\s+)?(?:function|const|let|class|type|interface|enum)\s/g;
function fnBody(src, name, what) {
  const m = new RegExp('\\nfunction ' + name + '[<(]').exec(src);
  if (!m) throw new Error(`could not find ${what} (function ${name})`);
  NEXT_DECL.lastIndex = m.index + 1;
  const next = NEXT_DECL.exec(src);
  const body = src.slice(m.index, next ? next.index : src.length);
  if (!body.includes('{')) throw new Error(`${what}: could not read the body of function ${name}`);
  return body;
}

// Utilities that re-state what the shared classes already own. Layout utilities (ml-auto, w-full, gap-,
// opacity, whitespace) stay allowed: they position a control, they do not resize it.
const RESTATES_SCALE = [
  /^h-/, /^min-h-/, /^max-h-/,
  /^p-/, /^px-/, /^py-/, /^pt-/, /^pb-/, /^pl-/, /^pr-/,
  /^text-\[/, /^text-(xs|sm|base|lg|xl)$/,
  /^font-(thin|extralight|light|normal|medium|semibold|bold|extrabold|black)$/,
  /^rounded/, /^leading-/,
];
// A className is either a literal or an expression that picks between literals (primary vs not). Either
// way the union of the strings it can produce is what the browser may end up with, so that is what gets
// checked: a sizing utility hidden in one branch is still a sizing utility.
function classOf(tag) {
  const lit = /className="([^"]*)"/.exec(tag);
  if (lit) return lit[1];
  const at = tag.indexOf('className={');
  if (at === -1) return '';
  let depth = 0, end = -1;
  for (let i = at + 'className='.length; i < tag.length; i++) {
    if (tag[i] === '{') depth++;
    else if (tag[i] === '}' && --depth === 0) { end = i; break; }
  }
  if (end === -1) return '';
  const expr = tag.slice(at, end);
  return [...expr.matchAll(/'([^']*)'|"([^"]*)"|`([^`]*)`/g)].map((m) => m[1] ?? m[2] ?? m[3]).join(' ');
}
const localSizing = (tag) => classOf(tag).split(/\s+/).filter(Boolean)
  .map((t) => t.replace(/^[a-zA-Z-]+:/, ''))
  .filter((t) => RESTATES_SCALE.some((re) => re.test(t)));
// Inline style is the other way to out-vote the scale, and it beats every class.
const INLINE_SCALE = /\b(height|minHeight|maxHeight|padding|paddingTop|paddingBottom|paddingInline|paddingBlock|fontSize|fontWeight|borderRadius|lineHeight)\s*:/;
function inlineSizing(tag) {
  const m = /style=\{\{([\s\S]*?)\}\}/.exec(tag);
  const hit = m && INLINE_SCALE.exec(m[1]);
  return hit ? hit[1] : null;
}
function onTheScale(tag, what, required = 'sem-btn') {
  const cls = classOf(tag);
  if (!cls.split(/\s+/).includes(required)) throw new Error(`${what} must use the shared "${required}" class (className was ${JSON.stringify(cls)})`);
  const local = localSizing(tag);
  if (local.length) throw new Error(`${what} re-states the control scale with ${local.join(' ')}`);
  const inline = inlineSizing(tag);
  if (inline) throw new Error(`${what} re-states the control scale inline with ${inline}`);
}

// ---- DAX Lab toolbar --------------------------------------------------------------------------
check('DAX Lab Save measure check', () => onTheScale(buttonTag(daxlab, '>Save measure check', 'DAX Lab Save measure check'), 'DAX Lab Save measure check'));
check('DAX Lab Reset', () => onTheScale(buttonTag(daxlab, '>Reset</button>', 'DAX Lab Reset'), 'DAX Lab Reset'));
check('DAX Lab Stop', () => onTheScale(buttonTag(daxlab, '>Stop</button>', 'DAX Lab Stop'), 'DAX Lab Stop'));
check('DAX Lab Run', () => {
  const tag = buttonTag(daxlab, '>Run</button>', 'DAX Lab Run');
  onTheScale(tag, 'DAX Lab Run');
  has(tag, /sem-btn-primary/, 'DAX Lab Run is the one filled action in that toolbar');
});
// The Visual / Query pair is a segmented control, so it uses the shared segment, at the height that rule
// gives, and carries its selected state on aria-pressed like every other segment in Studio.
check('DAX Lab mode segment', () => {
  const seg = fnBody(daxlab, 'Seg', 'the DAX Lab mode segment');
  has(seg, /className="sem-seg[" ]/, 'the DAX Lab mode pair must use the shared .sem-seg wrapper');
  has(seg, /className="sem-seg-item"/, 'the DAX Lab mode buttons must use .sem-seg-item');
  has(seg, /aria-pressed=/, 'the DAX Lab mode segment must carry its selected state on aria-pressed');
  for (const tag of seg.match(/<button[\s\S]*?>/g) ?? []) {
    const local = localSizing(tag);
    if (local.length) throw new Error(`the DAX Lab mode segment re-states the scale with ${local.join(' ')}`);
    const inline = inlineSizing(tag);
    if (inline) throw new Error(`the DAX Lab mode segment re-states the scale inline with ${inline}`);
  }
});
// The lab's own button primitive feeds the Performance / Server steps / Verify rows under that toolbar; it
// sized itself, so those rows drifted from the toolbar above them.
check('DAX Lab button primitive', () => onTheScale(buttonTag(fnBody(daxlab, 'Button', 'the DAX Lab button primitive'), '<button', 'the DAX Lab button primitive'), 'the DAX Lab button primitive'));

// ---- Published: the mode row and the controls under it ----------------------------------------
check('Published mode row', () => {
  const modeBtn = fnBody(deploy, 'ModeBtn', 'the Published mode row button');
  onTheScale(buttonTag(modeBtn, '<button', 'the Published mode row button'), 'the Published mode row button');
  has(modeBtn, /aria-pressed=/, 'the Published mode row must carry its selected state on aria-pressed');
});
for (const [file, src, what] of [['deploy.tsx', deploy, 'the Published Btn primitive'], ['compare.tsx', compare, 'the Compare Btn primitive']])
  check(what, () => onTheScale(buttonTag(fnBody(src, 'Btn', `${what} in ${file}`), '<button', what), what));
// The swap arrow shares the Compare again row, so it sits on the same scale rather than on its own.
check('Compare swap button', () => onTheScale(buttonTag(compare, 'swap source/target', 'the Compare swap button'), 'the Compare swap button'));

// ---- the Model Diff mode switcher, directly under the Published compare row --------------------
// It was a hand-rolled two-button strip with its own accent FILL, which made it the loudest thing on the
// Published screen once the rows above it came down to 28px. It is a segment like any other.
check('Model Diff mode switcher', () => {
  const at = diffview.indexOf('MODES.map');
  if (at === -1) throw new Error('could not find the Model Diff mode switcher');
  const region = diffview.slice(Math.max(0, at - 400), at + 600);
  has(region, /className="sem-seg[" ]/, 'the Review / Side by side pair must use the shared .sem-seg wrapper');
  has(region, /className="sem-seg-item"/, 'the Review / Side by side buttons must use .sem-seg-item');
  has(region, /aria-pressed=/, 'the Review / Side by side pair must carry its selection on aria-pressed');
  for (const tag of region.match(/<button[\s\S]*?>/g) ?? []) {
    const local = localSizing(tag);
    if (local.length) throw new Error(`the Model Diff mode switcher re-states the scale with ${local.join(' ')}`);
    const inline = inlineSizing(tag);
    if (inline) throw new Error(`the Model Diff mode switcher re-states the scale inline with ${inline}`);
  }
});

// ---- the area tab strips ------------------------------------------------------------------------
// A tab is NOT a segment: it carries aria-current="page", it can hold a busy glyph and an unseen dot
// positioned against its own box, and it has no segment wrapper. So it gets .sem-tab, which takes height,
// padding, radius and weight from the same scale, and keeps only its own colour at the call site.
for (const [what, name, required] of [
  ['the area sub-tab', 'Tab', 'sem-tab'],
  ['the primary area tab', 'GroupTab', 'sem-tab'],
  ['the Create overflow tab', 'CreateTab', 'sem-tab'],
]) check(what, () => onTheScale(buttonTag(fnBody(app, name, what), '<button', what), what, required));
check('the primary area tab is the full-size step', () => {
  const tag = buttonTag(fnBody(app, 'GroupTab', 'the primary area tab'), '<button', 'the primary area tab');
  has(classOf(tag), /\bsem-tab-lg\b/, 'the primary area row takes the full-size tab step, the sub-tab row below it the dense one');
});

// ---- and no other call site in these files quietly opts back out ------------------------------
for (const [file, src] of [['daxlab.tsx', daxlab], ['deploy.tsx', deploy], ['compare.tsx', compare], ['diffview.tsx', diffview], ['App.tsx', app]])
  check(`${file} sem-btn call sites`, () => {
    for (const tag of src.match(/<button[\s\S]*?>/g) ?? []) {
      const on = classOf(tag).split(/\s+/);
      if (!on.includes('sem-btn') && !on.includes('sem-tab') && !on.includes('sem-seg-item')) continue;
      const local = localSizing(tag);
      if (local.length) throw new Error(`a shared-control call site re-states the control scale with ${local.join(' ')}`);
    }
  });

if (failures.length) {
  console.error(`the shared control scale is not applied in ${failures.length} place(s):`);
  for (const f of failures) console.error('  - ' + f);
  process.exit(1);
}
console.log('shared control scale tests passed');
