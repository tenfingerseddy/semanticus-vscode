// ONE VISUAL LANGUAGE, the style half (Kane approved it on 23 September 2026).
//
// Every spacing, text size, corner and control height in the Studio source must come from the named tokens in
// webview/src/tokens.css. The measured starting point was 36 distinct padding, margin and gap values, 22
// rendered text sizes and 22 control heights, so a rule in prose would not hold; this file is the rule.
//
// It RATCHETS. scripts/visual-style-baseline.json records how many off-token values each file has today. A file
// may only go down: a count above its baseline fails, a file not in the baseline fails on its first violation,
// and a count BELOW its baseline also fails until the baseline is lowered, so a cleaned file cannot quietly
// drift back up to its old number. `node scripts/visual-style-scan.mjs --update` lowers it and refuses to
// raise it.
//
// The first half proves the scanner on planted values, so a green run over the real tree means something.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import {
  APPROVED_TOKENS, compareToBaseline, formatViolation, loadTokenNames, readBaseline, scanCss, scanRepo, scanTsx,
} from '../scripts/visual-style-scan.mjs';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const TOKENS = new Set(['--sem-gap', '--sem-card-pad', '--sem-space-2', '--sem-control-h', '--sem-text-body', '--sem-radius-card']);

test('a planted off-token padding fails, and is reported as file:line selector property value', () => {
  const css = '.ok { gap: var(--sem-gap); }\n.planted {\n  padding: 13px;\n}\n';
  const v = scanCss(css, 'planted.css', TOKENS);
  assert.equal(v.length, 1);
  assert.equal(formatViolation(v[0]), 'planted.css:3 .planted padding 13px');
});

test('tokens, 0, auto, inherit, percentages and the declared allow-list pass', () => {
  const css = [
    '.a { padding: 0 var(--sem-space-2); margin: auto; gap: var(--sem-gap); }',
    '.b { margin-left: calc(var(--sem-gap) * -1); border-radius: 50%; font-size: inherit; }',
    '.c { margin-top: -1px; padding-bottom: 1px; line-height: 1; }',
    '@media (max-width: 900px) { .d { padding: var(--sem-card-pad) !important; } }',
  ].join('\n');
  assert.deepEqual(scanCss(css, 'ok.css', TOKENS).map(formatViolation), []);
});

test('an unknown variable, a literal inside calc, and a literal fallback all fail', () => {
  const css = [
    '.a { gap: var(--ovh-gap); }',
    '.b { padding: calc(var(--sem-gap) + 2px); }',
    '.c { margin: var(--sem-gap, 10px); }',
    '.d { font-size: 11.5px; border-top-left-radius: 5px; }',
  ].join('\n');
  assert.deepEqual(scanCss(css, 'bad.css', TOKENS).map(formatViolation), [
    'bad.css:1 .a gap var(--ovh-gap)',
    'bad.css:2 .b padding calc(var(--sem-gap) + 2px)',
    'bad.css:3 .c margin var(--sem-gap, 10px)',
    'bad.css:4 .d font-size 11.5px',
    'bad.css:4 .d border-top-left-radius 5px',
  ]);
});

test('height is checked on controls only', () => {
  const css = '.sem-btn { height: 30px; }\n.wrap { height: 30px; min-height: 420px; }\n.field input { min-height: var(--sem-control-h); }\n';
  assert.deepEqual(scanCss(css, 'h.css', TOKENS).map(formatViolation), ['h.css:1 .sem-btn height 30px']);
});

test('comments and custom property definitions are not declarations', () => {
  const css = '/* padding: 13px; */\n:root { --x-pad: 13px; }\n.a { /* gap: 3px */ gap: var(--sem-gap); }\n';
  assert.deepEqual(scanCss(css, 'c.css', TOKENS).map(formatViolation), []);
});

test('Tailwind classes: scale steps pass, off-scale steps and literal arbitrary values fail', () => {
  const tsx = [
    'export const A = () => (',
    '  <div className="p-3 gap-2 mt-4 text-xs rounded-sm leading-5 px-[13px] text-[11px] rounded-lg py-1.5">',
    '    <span className={`hover:p-4 ${x ? \'m-2\' : \'m-[var(--sem-gap)]\'} p-(--sem-card-pad)`} />',
    '  </div>',
    ');',
  ].join('\n');
  assert.deepEqual(scanTsx(tsx, 'a.tsx', TOKENS).map(formatViolation), [
    'a.tsx:2 <div> className px-[13px]',
    'a.tsx:2 <div> className text-[11px]',
    'a.tsx:2 <div> className rounded-lg',
    'a.tsx:2 <div> className py-1.5',
  ]);
});

test('Tailwind axis gaps are read as their step: a half step fails, a scale step passes', () => {
  // gap-x-1.5 once parsed as `gap` with the value `x-1.5`, which is not a number, so it passed unseen.
  const tsx = '<div className="gap-x-1.5 gap-y-0.5 sm:gap-x-2.5 gap-x-4 gap-y-1 gap-x-[13px] gap-y-[var(--sem-gap)]" />';
  assert.deepEqual(scanTsx(tsx, 'g.tsx', TOKENS).map(formatViolation), [
    'g.tsx:1 <div> className gap-x-1.5',
    'g.tsx:1 <div> className gap-y-0.5',
    'g.tsx:1 <div> className sm:gap-x-2.5',
    'g.tsx:1 <div> className gap-x-[13px]',
  ]);
});

test('Tailwind height classes are checked on controls only', () => {
  const tsx = '<div className="h-9 min-h-[300px]"><button className="h-9 px-2" /><input className="h-7" /></div>';
  assert.deepEqual(scanTsx(tsx, 'h.tsx', TOKENS).map(formatViolation), ['h.tsx:1 <button> className h-9']);
});

test('inline style objects: a number is a pixel literal, a token string passes', () => {
  const tsx = [
    'const box = { padding: 13, gap: \'var(--sem-gap)\', fontSize: 11, borderRadius: 0 };',
    'const X = () => <button style={{ height: 30, margin: \'0 var(--sem-space-2)\' }} />;',
  ].join('\n');
  assert.deepEqual(scanTsx(tsx, 's.tsx', TOKENS).map(formatViolation), [
    's.tsx:1 object padding 13',
    's.tsx:1 object fontSize 11',
    's.tsx:2 <button> style height 30',
  ]);
});

test('the ratchet: up fails, new file fails, equal passes, down fails until the baseline is lowered', () => {
  const base = { 'a.css': 3, 'b.tsx': 2 };
  assert.deepEqual(compareToBaseline({ 'a.css': 3, 'b.tsx': 2 }, base).failures, []);
  assert.match(compareToBaseline({ 'a.css': 4, 'b.tsx': 2 }, base).failures.join('\n'), /a\.css.*4.*baseline 3/);
  assert.match(compareToBaseline({ 'a.css': 3, 'b.tsx': 2, 'c.tsx': 1 }, base).failures.join('\n'), /c\.tsx.*new file.*1/);
  assert.match(compareToBaseline({ 'a.css': 1, 'b.tsx': 2 }, base).failures.join('\n'), /a\.css.*1.*baseline 3.*lower/);
  // A file that reached zero and then left the tree is not a failure; one that reached zero stays at zero.
  assert.deepEqual(compareToBaseline({ 'a.css': 3 }, { ...base, 'b.tsx': 0 }).failures, []);
});

test('tokens.css defines exactly the approved values', () => {
  const tokens = readFileSync(resolve(root, 'webview/src/tokens.css'), 'utf8');
  const rootBlock = /:root\s*\{([\s\S]*?)\n\}/.exec(tokens)?.[1] ?? '';
  const defined = Object.fromEntries([...rootBlock.replace(/\/\*[\s\S]*?\*\//g, '').matchAll(/(--sem-[\w-]+)\s*:\s*([^;]+);/g)].map((m) => [m[1], m[2].trim()]));
  const missing = [];
  for (const [name, value] of Object.entries(APPROVED_TOKENS)) if (defined[name] !== value) missing.push(`${name}: want ${value}, found ${defined[name]}`);
  assert.deepEqual(missing, []);
  // The scanner's idea of a token is what tokens.css defines, so an alias cannot launder a literal.
  const names = loadTokenNames(root);
  for (const name of Object.keys(APPROVED_TOKENS)) assert.ok(names.has(name), `${name} must be a token the scanner accepts`);
  for (const [name, value] of Object.entries(defined)) {
    if (name in APPROVED_TOKENS || name.startsWith('--sem-font-')) continue;
    assert.match(value, /^(0 )?var\(--sem-[\w-]+\)( var\(--sem-[\w-]+\))?$/, `${name} is an alias, so it must point at a token, not hold a literal (${value})`);
  }
});

// Instrument (Kane picked look-and-feel direction B on 23 September 2026): every surface and text colour comes
// from the VS Code theme, and the product adds exactly one colour of its own, the jade accent.
test('colour tokens derive from the VS Code theme, with one accent and a light and high-contrast mapping', () => {
  const tokens = readFileSync(resolve(root, 'webview/src/tokens.css'), 'utf8').replace(/\/\*[\s\S]*?\*\//g, '');
  const block = (selector) => new RegExp(selector.replace(/[.*+?^${}()|[\]\\]/g, '\\$&') + '\\s*\\{([^}]*)\\}').exec(tokens)?.[1];
  const colours = block(':root, body');
  assert.ok(colours, 'tokens.css needs a `:root, body { }` colour block, so derived colours recompute where the theme class sits');
  const light = block('body.vscode-light, body.vscode-high-contrast-light');
  const hc = block('body.vscode-high-contrast, body.vscode-high-contrast-light');
  assert.ok(light, 'tokens.css needs a light mapping');
  assert.ok(hc, 'tokens.css needs a high-contrast mapping');
  const decls = (b) => Object.fromEntries([...b.matchAll(/(--sem-[\w-]+)\s*:\s*([^;]+);/g)].map((m) => [m[1], m[2].trim()]));
  const dark = decls(colours);
  for (const name of ['--sem-bg', '--sem-fg', '--sem-muted', '--sem-surface', '--sem-surface-2', '--sem-border', '--sem-rule', '--sem-accent', '--sem-on-accent', '--sem-warn', '--sem-bad']) {
    assert.ok(name in dark, `${name} must be defined in the colour block`);
  }
  assert.equal(dark['--sem-accent'], '#3CC08F');
  assert.equal(decls(light)['--sem-accent'], '#0A7A55');
  assert.match(dark['--sem-bg'], /^var\(--vscode-editor-background/);
  assert.match(dark['--sem-fg'], /^var\(--vscode-foreground/);
  assert.match(decls(hc)['--sem-border'], /var\(--vscode-contrastBorder/);
  // A hex colour is allowed only as a fallback inside var(--vscode-..., #hex), as the accent and its ink, or as one of the three field
  // kind colours (Kane, 8 Oct): VS Code gives a webview no variable for its syntax colours, so those are the editor's own values, fixed.
  const OWN = ['--sem-accent', '--sem-on-accent', '--sem-kind-measure', '--sem-kind-column', '--sem-kind-table'];
  for (const [where, b] of [['colour', colours], ['light', light], ['high contrast', hc]]) {
    for (const [name, value] of Object.entries(decls(b))) {
      const bare = value.replace(/var\(--vscode-[\w-]+\s*,\s*#[0-9a-fA-F]{3,8}\)/g, '');
      if (/#[0-9a-fA-F]{3,8}\b/.test(bare)) assert.ok(OWN.includes(name), `${name} in the ${where} block holds a colour of its own (${value}); derive it from the theme`);
    }
  }
});

test('the studio bundle ships tokens.css for the Properties grid, byte for byte', () => {
  const src = readFileSync(resolve(root, 'webview/src/tokens.css'));
  const shipped = readFileSync(resolve(root, 'media/studio/tokens.css'));
  assert.ok(src.equals(shipped), 'media/studio/tokens.css is stale: run `cd webview && npm run build`');
  const main = readFileSync(resolve(root, 'webview/src/main.tsx'), 'utf8');
  assert.ok(main.indexOf("import './tokens.css'") !== -1 && main.indexOf("import './tokens.css'") < main.indexOf("import './styles.css'"),
    'main.tsx must import tokens.css before styles.css');
  const ext = readFileSync(resolve(root, 'src/extension.ts'), 'utf8');
  assert.match(ext, /'studio', 'tokens\.css'/, 'the Properties grid webview must link media/studio/tokens.css');
  const harness = readFileSync(resolve(root, 'tools/uishot/propgrid.html'), 'utf8');
  assert.ok(harness.indexOf('media/studio/tokens.css') !== -1 && harness.indexOf('media/studio/tokens.css') < harness.indexOf('media/propgrid/propgrid.css'),
    'the propgrid harness must load tokens.css before propgrid.css');
});

test('the real tree is at or under its checked-in baseline', () => {
  const { counts, violations } = scanRepo(root);
  const { failures } = compareToBaseline(counts, readBaseline(root));
  if (failures.length) {
    const detail = failures.map((f) => {
      const file = f.split(':')[0];
      const all = violations.filter((v) => v.file === file);
      const lines = all.slice(0, 40).map((v) => '    ' + formatViolation(v));
      const more = all.length > 40 ? `\n    ... and ${all.length - 40} more: node scripts/visual-style-scan.mjs --list ${file}` : '';
      return `${f}\n${lines.join('\n')}${more}`;
    });
    assert.fail(`style check:\n${detail.join('\n')}`);
  }
});
