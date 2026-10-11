import assert from 'node:assert/strict';
import { readFileSync, readdirSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { THEMES, STOCK_THEMES, THEME_IDS } from '../tools/uishot/themes.mjs';

// Colour level B (contract section 20, Kane 24 Sep). Every hue the product adds is worked out from the VS Code
// theme, so a mix that reads well on Dark Modern can quietly fail on Light Modern. The screen check only sees a
// colour once a page uses it; this test catches a bad mix at the token, before any page does. It resolves each
// hue token against the page background, the flat inset and every tint, in Dark Modern, Light Modern and Dark
// High Contrast (the harness's three themes) plus High Contrast Light, and fails anything under 4.55 to 1: WCAG
// AA for body text is 4.5, and the 0.05 is headroom for the painted colour, which is rounded to 8 bits (a card the
// maths put at 4.50:1 painted at 4.49). Set TOKEN_CONTRAST_TABLE=1 to print every pair it measured.
//
// It reads the real sources the Studio loads, in the order it loads them (tokens.css, then styles.css), and the
// theme values the screenshot harness applies (tools/uishot/themes.mjs), so a later redefinition in styles.css
// or a missing chart colour in the harness is caught here too. The CSS it has to understand is small: custom
// properties, var() with fallbacks, hex colours, color-mix() in srgb, and rgb(from ...) with a fixed alpha.
// Anything else throws, so a new construct in a colour token is a loud failure, not a silent pass.
//
// Option B (Kane, 27 September 2026) put content on a surface one step off the page, and the second round of its
// review (coordinator's rulings D1 to D7) added what this test now also guards, each of which a pixel review
// found missing before: text on a card, a mark in a card and a hover in a card hold in every stock VS Code theme,
// not just the four above; everything drawn inside a lifted surface is drawn against that surface (the lifted
// rule in tokens.css), so a mark, a hover, a rule and a control edge are as visible there as on the page; the
// lift itself is there, points the right way, and never equals a hover. Reverting the lift, inverting it, making
// the hover the card's colour, or dropping a class from the lifted rule each fails here. It also reads
// oklab(from ...) with calc(<channel> + <number>), the one relative colour form the lifted rule uses.

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const read = (file) => readFileSync(resolve(root, file), 'utf8');
const SOURCES = ['webview/src/tokens.css', 'webview/src/styles.css'];

const HUES = ['--sem-good', '--sem-warn', '--sem-bad', '--sem-doer-engine', '--sem-doer-you', '--sem-doer-agent'];
const GROUNDS = ['--sem-bg', '--sem-surface', '--sem-surface-2', '--sem-hover', '--sem-tint-engine', '--sem-tint-you', '--sem-tint-agent', '--sem-tint-warn', '--sem-tint-select'];
const CHARTS = ['green', 'yellow', 'red', 'blue', 'orange', 'purple'].map((c) => `--vscode-charts-${c}`);
const MIN = 4.55;

// ---- a tiny CSS reader: top-level rules only (@media blocks change spacing, never colour) -------------------
function rules(css) {
  const out = [];
  const text = css.replace(/\/\*[\s\S]*?\*\//g, '');
  let i = 0;
  while (i < text.length) {
    const open = text.indexOf('{', i);
    if (open < 0) break;
    const prelude = text.slice(i, open).replace(/^[\s;]*(@import[^;]*;\s*)*/g, '').trim();
    let depth = 1, j = open + 1;
    while (j < text.length && depth > 0) { if (text[j] === '{') depth++; else if (text[j] === '}') depth--; j++; }
    const body = text.slice(open + 1, j - 1);
    if (!prelude.startsWith('@')) {
      const decls = {};
      for (const m of body.matchAll(/(--[\w-]+)\s*:\s*([^;]+?)\s*(?:;|$)/g)) decls[m[1]] = m[2];
      if (Object.keys(decls).length) out.push({ selectors: split(prelude).map((x) => x.replace(/\s+/g, ' ')), decls });
    }
    i = j;
  }
  return out;
}
const ALL_RULES = SOURCES.flatMap((f) => rules(read(f)));

// Which rules reach <html> (":root", "html") and which reach <body> ("body", "body.a.b", a theme id as
// body[data-vscode-theme-id="X"] or body:is([data-vscode-theme-id="X"], ...)), with specificity.
function spec(sel, el, bodyClass, themeId) {
  if (el === 'html') return sel === ':root' ? 10 : sel === 'html' ? 1 : -1;
  const id = /^body(?:\[data-vscode-theme-id="([^"]+)"\]|:is\((.*)\))$/.exec(sel);
  if (id) {
    const ids = id[1] !== undefined ? [id[1]] : [...id[2].matchAll(/\[data-vscode-theme-id="([^"]+)"\]/g)].map((x) => x[1]);
    return ids.includes(themeId) ? 11 : -1;
  }
  const m = /^body((?:\.[\w-]+)*)$/.exec(sel);
  if (!m) return -1;
  const classes = m[1].split('.').filter(Boolean);
  return classes.every((c) => [bodyClass].flat().includes(c)) ? 1 + 10 * classes.length : -1;
}
function declared(el, bodyClass, themeId) {
  const win = {};   // name -> { spec, order, value }
  ALL_RULES.forEach((r, order) => {
    const s = Math.max(...r.selectors.map((sel) => spec(sel, el, bodyClass, themeId)));
    if (s < 0) return;
    for (const [k, v] of Object.entries(r.decls)) if (!win[k] || s >= win[k].spec) win[k] = { spec: s, order, value: v };
  });
  return Object.fromEntries(Object.entries(win).map(([k, v]) => [k, v.value]));
}

// Custom properties inherit their COMPUTED value, so a token declared on body is worked out again against body's
// values, and a token declared only on :root arrives at body already worked out against the theme on <html>.
function substitute(value, lookup, seen = new Set()) {
  let out = '', i = 0;
  while (i < value.length) {
    const at = value.indexOf('var(', i);
    if (at < 0) { out += value.slice(i); break; }
    out += value.slice(i, at);
    let depth = 1, j = at + 4;
    while (depth > 0) { if (value[j] === '(') depth++; else if (value[j] === ')') depth--; j++; }
    const inner = value.slice(at + 4, j - 1);
    const comma = inner.indexOf(',');
    const name = (comma < 0 ? inner : inner.slice(0, comma)).trim();
    const fallback = comma < 0 ? undefined : inner.slice(comma + 1).trim();
    const got = seen.has(name) ? undefined : lookup(name, new Set([...seen, name]));
    if (got !== undefined) out += got;
    else if (fallback !== undefined) out += substitute(fallback, lookup, seen);
    else throw new Error(`var(${name}) has no value and no fallback`);
    i = j;
  }
  return out;
}
// Every theme VS Code 1.136 ships: the harness's three plus STOCK_THEMES (High Contrast Light among them, whose
// body carries both high-contrast classes, as VS Code gives it). THEME_IDS names the three main ones.
const CHECKED = Object.fromEntries([
  ...Object.entries(THEMES).map(([id, t]) => [id, { ...t, themeId: THEME_IDS[id] }]),
  ...Object.entries(STOCK_THEMES),
]);
const PRIMARY = ['dark', 'light', 'hc', 'hclight'];   // the four Colour level B was designed on
const isHC = (t) => [t.bodyClass].flat().includes('vscode-high-contrast');
const isLight = (t) => [t.bodyClass].flat().some((c) => c === 'vscode-light' || c === 'vscode-high-contrast-light');

// The lifted rule: the one rule whose declarations apply to the children of every content surface. A token read
// "in a card" is that rule's declaration worked out against the card's inherited values, exactly as a child of
// the card computes it. Its selector must keep high contrast out and name the surfaces through :where().
const LIFTED_PREFIX = 'body:not(.vscode-high-contrast) :where(';
const LIFTED = ALL_RULES.find((r) => r.selectors.length === 1 && r.selectors[0].startsWith(LIFTED_PREFIX) && r.selectors[0].endsWith(') > *'));
const LIFTED_LIST = LIFTED ? split(LIFTED.selectors[0].slice(LIFTED_PREFIX.length, -') > *'.length)) : [];

function computed(theme, inCard = false) {
  const t = CHECKED[theme];
  const htmlDecl = { ...declared('html'), ...t.vars };   // the harness sets the theme inline on <html>
  const bodyDecl = declared('body', t.bodyClass, t.themeId);
  const htmlCache = {};
  const htmlVal = (name, seen) => {
    if (!(name in htmlDecl)) return undefined;
    return htmlCache[name] ??= substitute(htmlDecl[name], htmlVal, seen);
  };
  const bodyCache = {};
  const bodyVal = (name, seen) => {
    if (name in bodyDecl) return bodyCache[name] ??= substitute(bodyDecl[name], bodyVal, seen);
    return htmlVal(name, seen);
  };
  let val = bodyVal;
  if (inCard && LIFTED && !isHC(t)) {
    const cardDecl = LIFTED.decls, cardCache = {};
    val = (name, seen) => (name in cardDecl ? cardCache[name] ??= substitute(cardDecl[name], val, seen) : bodyVal(name, seen));
  }
  return (name) => {
    const v = val(name, new Set([name]));
    if (v === undefined) throw new Error('is not defined');
    return v;
  };
}

// ---- colour maths (CSS Color 4/5: color-mix interpolates premultiplied sRGB) --------------------------------
function split(args) {
  const parts = []; let depth = 0, cur = '';
  for (const ch of args) {
    if (ch === '(') depth++; else if (ch === ')') depth--;
    if (ch === ',' && depth === 0) { parts.push(cur.trim()); cur = ''; } else cur += ch;
  }
  parts.push(cur.trim());
  return parts;
}
function hex(h) {
  let s = h.slice(1);
  if (s.length === 3 || s.length === 4) s = [...s].map((c) => c + c).join('');
  if (s.length !== 6 && s.length !== 8) throw new Error(`bad hex ${h}`);
  const n = (k) => parseInt(s.slice(k, k + 2), 16);
  return { r: n(0) / 255, g: n(2) / 255, b: n(4) / 255, a: s.length === 8 ? n(6) / 255 : 1 };
}
function color(expr) {
  const e = expr.trim();
  if (e === 'transparent') return { r: 0, g: 0, b: 0, a: 0 };
  if (e.startsWith('#')) return hex(e);
  let m = /^rgb\(\s*from\s+([\s\S]+?)\s+r\s+g\s+b\s*\/\s*([\d.]+)\s*\)$/.exec(e);
  if (m) return { ...color(m[1]), a: Number(m[2]) };
  m = /^color-mix\(\s*in\s+srgb\s*,([\s\S]*)\)$/.exec(e);
  if (m) {
    const [p, q] = split(m[1]).map((part) => {
      const pm = /^([\s\S]*?)\s+([\d.]+)%$/.exec(part);
      return pm ? { c: color(pm[1]), w: Number(pm[2]) } : { c: color(part), w: undefined };
    });
    let wp = p.w, wq = q.w;
    if (wp === undefined && wq === undefined) { wp = 50; wq = 50; }
    else if (wp === undefined) wp = 100 - wq;
    else if (wq === undefined) wq = 100 - wp;
    const sum = wp + wq;
    if (sum === 0) throw new Error(`color-mix weights sum to zero: ${e}`);
    const alphaMult = sum < 100 ? sum / 100 : 1;
    wp /= sum; wq /= sum;
    const a = p.c.a * wp + q.c.a * wq;
    const ch = (k) => (a === 0 ? 0 : (p.c[k] * p.c.a * wp + q.c[k] * q.c.a * wq) / a);
    return { r: ch('r'), g: ch('g'), b: ch('b'), a: a * alphaMult };
  }
  m = /^oklab\(\s*from\s+([\s\S]+?)\s+([lab]|calc\([^()]*\))\s+([lab]|calc\([^()]*\))\s+([lab]|calc\([^()]*\))\s*\)$/.exec(e);
  if (m) {
    const origin = color(m[1]);
    const [L, A, B] = toOklab(origin);
    const env = { l: L, a: A, b: B };
    const ch = (x) => {
      if (x in env) return env[x];
      const cm = /^calc\(\s*([lab])\s*([+-])\s*(-?[\d.]+)\s*\)$/.exec(x);
      if (!cm) throw new Error(`cannot read oklab channel "${x}"`);
      return env[cm[1]] + (cm[2] === '+' ? 1 : -1) * Number(cm[3]);
    };
    return { ...fromOklab(ch(m[2]), ch(m[3]), ch(m[4])), a: origin.a };
  }
  throw new Error(`cannot read colour "${e}"`);
}
// Oklab (Ottosson 2020, the matrices CSS Color 4 uses). A result outside sRGB is clipped per channel, which is
// what the Studio's Chromium paints: measured on #FFFFFF with l + 0.03, it paints #FFFFFF.
const srgbLin = (v) => (v <= 0.04045 ? v / 12.92 : ((v + 0.055) / 1.055) ** 2.4);
const srgbEnc = (v) => { const c = Math.min(1, Math.max(0, v)); return c <= 0.0031308 ? 12.92 * c : 1.055 * c ** (1 / 2.4) - 0.055; };
function toOklab({ r, g, b }) {
  const [R, G, B] = [srgbLin(r), srgbLin(g), srgbLin(b)];
  const l = Math.cbrt(0.4122214708 * R + 0.5363325363 * G + 0.0514459929 * B);
  const m = Math.cbrt(0.2119034982 * R + 0.6806995451 * G + 0.1073969566 * B);
  const s = Math.cbrt(0.0883024619 * R + 0.2817188376 * G + 0.6299787005 * B);
  return [0.2104542553 * l + 0.793617785 * m - 0.0040720468 * s, 1.9779984951 * l - 2.428592205 * m + 0.4505937099 * s, 0.0259040371 * l + 0.7827717662 * m - 0.808675766 * s];
}
function fromOklab(L, A, B) {
  const l = (L + 0.3963377774 * A + 0.2158037573 * B) ** 3, m = (L - 0.1055613458 * A - 0.0638541728 * B) ** 3, s = (L - 0.0894841775 * A - 1.291485548 * B) ** 3;
  return {
    r: srgbEnc(4.0767416621 * l - 3.3077115913 * m + 0.2309699292 * s),
    g: srgbEnc(-1.2684380046 * l + 2.6097574011 * m - 0.3413193965 * s),
    b: srgbEnc(-0.0041960863 * l - 0.7034186147 * m + 1.707614701 * s),
  };
}
const over = (top, under) => {
  const a = top.a + under.a * (1 - top.a);
  const ch = (k) => (a === 0 ? 0 : (top[k] * top.a + under[k] * under.a * (1 - top.a)) / a);
  return { r: ch('r'), g: ch('g'), b: ch('b'), a };
};
const lum = ({ r, g, b }) => {
  const f = (v) => (v <= 0.04045 ? v / 12.92 : ((v + 0.055) / 1.055) ** 2.4);
  return 0.2126 * f(r) + 0.7152 * f(g) + 0.0722 * f(b);
};
const ratio = (x, y) => { const [hi, lo] = [lum(x), lum(y)].sort((p, q) => q - p); return (hi + 0.05) / (lo + 0.05); };
const toHex = (c) => '#' + [c.r, c.g, c.b].map((v) => Math.round(v * 255).toString(16).padStart(2, '0')).join('').toUpperCase();

// ---- the checks ------------------------------------------------------------------------------------------------
const failures = [];
const rows = [];
const check = (name, fn) => { try { fn(); } catch (e) { failures.push(`${name}: ${e.message}`); } };
const labOf = ({ r, g, b }) => {
  const f = (t) => (t > 0.008856 ? Math.cbrt(t) : 7.787 * t + 16 / 116);
  const [R, G, B] = [srgbLin(r), srgbLin(g), srgbLin(b)];
  const X = f((0.4124 * R + 0.3576 * G + 0.1805 * B) / 0.95047), Y = f(0.2126 * R + 0.7152 * G + 0.0722 * B), Z = f((0.0193 * R + 0.1192 * G + 0.9505 * B) / 1.08883);
  return [116 * Y - 16, 500 * (X - Y), 200 * (Y - Z)];
};
// CIE76, the difference the pixel reviews measured (2.3 is a just-noticeable step; a page mark was 2.8 to 3.4).
const dE = (p, q) => { const [a, b] = [labOf(p), labOf(q)]; return Math.hypot(a[0] - b[0], a[1] - b[1], a[2] - b[2]); };

// The harness must carry the theme's chart colours, or every hue falls back to its Dark Modern default in all three
// themes and Light is never really tested. VS Code 1.136 leaves charts.orange unset in Dark High Contrast (it
// comes from the find-match highlight, which high contrast does not define), so the harness leaves it unset too
// and the token's own fallback is what that theme shows.
for (const [id, t] of Object.entries(THEMES)) {
  check(`${t.label} chart colours`, () => {
    const expected = id === 'hc' ? CHARTS.filter((c) => c !== '--vscode-charts-orange') : CHARTS;
    const missing = expected.filter((c) => !(c in t.vars));
    if (missing.length) throw new Error(`themes.mjs does not set ${missing.join(', ')}`);
  });
}

// Text that must read at 4.5:1: the six hues on the page, every mark and every tint, as before; and every text
// colour (the hues, plain, muted, the accent, links, the diff + and -) on the page, a card, a mark and a hover,
// both on the page and inside a card, where marks and hovers are worked out against the card. Every hue, plain and
// muted text are also checked on every tint, in every theme: a stock theme whose chart colour cannot hold steps
// that hue's lightness (tokens.css, the hue steps), which is what closed the 41 hue-on-tint pairs and the
// warn and orange that failed on their own page in Solarized Light, Quiet Light, Light+ and Visual Studio Light.
// A text colour still under 4.5:1 on its own page after that would be a theme colour the product does not mix
// (a link, say), which no surface can rescue. Such colours are listed here rather than skipped, and the test fails
// if the set changes in either direction, so a new one cannot hide and a fixed one has to come off. Measured on
// 27 September 2026 in all 19 themes, the list is empty.
// The three field kind colours (Kane, 8 Oct) are glyphs in the Queries wells and the Fields drawer: a glyph is text.
const KINDS = ['--sem-kind-measure', '--sem-kind-column', '--sem-kind-table'];
const TEXT = [...HUES, ...KINDS, '--sem-fg', '--sem-muted', '--sem-accent', '--sem-link', '--sem-diff-add', '--sem-diff-del'];
const PAGE_GROUNDS = ['--sem-bg', '--sem-surface', '--sem-surface-2', '--sem-hover'];
const CARD_GROUNDS = ['--sem-surface', '--sem-surface-2', '--sem-hover'];
const KNOWN_PAGE_FAILS = [];
const pageFails = [];
for (const [id, t] of Object.entries(CHECKED)) {
  const val = computed(id), card = computed(id, true);
  const page = color(val('--sem-bg'));
  const resolve = (v, name) => { try { return color(v(name)); } catch (e) { throw new Error(`${name} does not resolve (${e.message})`); } };
  for (const textName of TEXT) {
    let onPage;
    try { onPage = ratio(over(resolve(val, textName), page), page); } catch (e) { failures.push(`${t.label}: ${e.message}`); continue; }
    if (onPage < MIN) {
      pageFails.push(`${t.label} ${textName}`);
      if (PRIMARY.includes(id)) failures.push(`${t.label}: ${textName} is ${onPage.toFixed(2)}:1 on the page itself`);
      continue;
    }
    const pairs = [
      ...(HUES.includes(textName) || textName === '--sem-fg' || textName === '--sem-muted' ? GROUNDS : PAGE_GROUNDS).map((g) => ['page', val, g]),
      ...(isHC(t) ? [] : CARD_GROUNDS.map((g) => ['card', card, g])),
    ];
    for (const [where, v, groundName] of pairs) {
      check(`${t.label}: ${textName} on ${groundName} (${where})`, () => {
        const ground = over(resolve(v, groundName), page);
        const text = over(resolve(v, textName), ground);
        const r = ratio(text, ground);
        rows.push(`${t.label.padEnd(19)} ${textName.padEnd(18)} ${toHex(text)} on ${groundName.padEnd(17)} ${where} ${toHex(ground)}  ${r.toFixed(2)}:1`);
        assert.ok(r >= MIN, `${toHex(text)} on ${toHex(ground)} is ${r.toFixed(2)}:1, under ${MIN}:1`);
      });
    }
  }
}
// A menu's drop shadow (--sem-shadow-pop) falls over the card beside it, and in a light theme that darkens the
// card under muted text: at black 28% a Light Modern card went from #EFEFEF to #E9E9E9 twelve pixels past the
// menu's edge (measured, 2.5% black) and muted text read 4.46:1. The check models the shadow at 15% of its alpha,
// a label about eight pixels from the edge, and wants muted text to hold 4.5:1 there in every light theme.
for (const [id, t] of Object.entries(CHECKED)) {
  if (!isLight(t) || isHC(t)) continue;
  check(`${t.label}: muted text on a card under a menu's shadow`, () => {
    const val = computed(id), page = color(val('--sem-bg'));
    const alpha = Number(val('--sem-shadow-pop-alpha'));
    if (!(alpha >= 0)) throw new Error('--sem-shadow-pop-alpha is not a number');
    const ground = over({ r: 0, g: 0, b: 0, a: 0.15 * alpha }, over(color(val('--sem-surface')), page));
    const text = over(color(val('--sem-muted')), ground), r = ratio(text, ground);
    rows.push(`${t.label.padEnd(19)} --sem-muted         ${toHex(text)} on a shadowed card ${toHex(ground)}  ${r.toFixed(2)}:1`);
    assert.ok(r >= MIN, `${toHex(text)} on ${toHex(ground)} is ${r.toFixed(2)}:1, under ${MIN}:1`);
  });
}
check('the theme colours that fail on their own page', () => {
  assert.deepEqual([...pageFails].sort(), [...KNOWN_PAGE_FAILS].sort());
});

// The lift, the marks and the hover (rulings D1, D2, D5, D7). Numbers are CIE76 on the resolved tokens; the
// pixel drivers in tools/uishot read the same things off screenshots.
//   - A card is off the page in the right direction: lighter in a dark theme, darker in a light one. Dark Modern
//     and Light Modern keep the approved step (6.6 and 5.4); a stock theme with a smaller lift still shows one.
//   - Inside a card a mark differs from the card, and a hover from the card and from a mark, by at least what a
//     mark on the page measured before option B (2.8 to 3.4): 3. On the page a hover does too, and a mark is at
//     least 2.0 (it is the page's own 4% where the lift is 8%, 2.2 in Light+).
//   - A rule or a control edge inside a card is as far from the card as it is from the page on the page.
//   - High contrast: no fills at all, and the lifted rule does not reach it.
for (const [id, t] of Object.entries(CHECKED)) {
  const val = computed(id), card = computed(id, true);
  const c = (v, n) => color(v(n));
  if (isHC(t)) {
    check(`${t.label}: high contrast draws no fills`, () => {
      const bg = toHex(c(val, '--sem-bg'));
      for (const n of ['--sem-surface', '--sem-surface-2', '--sem-hover']) {
        assert.equal(toHex(c(val, n)), bg, `${n} on the page`);
        assert.equal(toHex(c(card, n)), bg, `${n} in a card`);
      }
      // The Optimize chips' washes are fills too: high contrast tells old from new by the strike and the arrow.
      for (const n of ['--sem-wash-bad', '--sem-wash-good']) assert.equal(c(val, n).a, 0, `${n} is not transparent`);
    });
    continue;
  }
  check(`${t.label}: the lift, the marks and the hover`, () => {
    const bg = c(val, '--sem-bg'), surface = c(val, '--sem-surface');
    const lift = dE(surface, bg), dir = labOf(surface)[0] - labOf(bg)[0];
    const pageMark = c(val, '--sem-surface-2'), cardMark = c(card, '--sem-surface-2');
    const pageHover = c(val, '--sem-hover'), cardHover = c(card, '--sem-hover');
    // A mark on the page is where it always was in the 8% themes (the page's own 4%: 2.2 in Light+, whose text
    // colour is pale, to 3.4), so the floor there is 2.0; inside a card, the floor is what a page mark measured
    // before option B.
    const markMin = 2.0;
    rows.push(`${t.label.padEnd(19)} lift ${toHex(bg)} to ${toHex(surface)} dE ${lift.toFixed(1)} | page mark ${dE(pageMark, bg).toFixed(1)} | card mark ${dE(cardMark, surface).toFixed(1)} | hover page ${dE(pageHover, bg).toFixed(1)} card ${dE(cardHover, surface).toFixed(1)} | hover vs mark page ${dE(pageHover, pageMark).toFixed(1)} card ${dE(cardHover, cardMark).toFixed(1)}`);
    assert.ok(isLight(t) ? dir < 0 : dir > 0, `a card must be ${isLight(t) ? 'darker' : 'lighter'} than the page in this theme; its L* moved ${dir.toFixed(1)}`);
    assert.ok(lift >= (id === 'dark' || id === 'light' ? 5 : 1.5), `the card is only dE ${lift.toFixed(1)} off the page`);
    assert.ok(dE(pageMark, bg) >= markMin, `a mark on the page is dE ${dE(pageMark, bg).toFixed(1)} from the page`);
    assert.ok(dE(cardMark, surface) >= 3, `a mark in a card is dE ${dE(cardMark, surface).toFixed(1)} from the card`);
    assert.ok(dE(pageHover, bg) >= 3, `a hover on the page is dE ${dE(pageHover, bg).toFixed(1)} from the page`);
    assert.ok(dE(pageHover, surface) >= 3, `a hover on the page is dE ${dE(pageHover, surface).toFixed(1)} from a card`);
    assert.ok(dE(cardHover, surface) >= 3, `a hover in a card is dE ${dE(cardHover, surface).toFixed(1)} from the card`);
    assert.ok(dE(pageHover, pageMark) >= 3, `a hover on the page is dE ${dE(pageHover, pageMark).toFixed(1)} from a mark`);
    assert.ok(dE(cardHover, cardMark) >= 3, `a hover in a card is dE ${dE(cardHover, cardMark).toFixed(1)} from a mark`);
    for (const n of ['--sem-rule', '--sem-border']) {
      const onPage = dE(c(val, n), bg), inCard = dE(c(card, n), surface);
      const rPage = ratio(c(val, n), bg), rCard = ratio(c(card, n), surface);
      assert.ok(inCard >= 0.95 * onPage, `${n} is dE ${inCard.toFixed(1)} from a card and ${onPage.toFixed(1)} from the page`);
      assert.ok(rCard >= rPage - 0.01, `${n} is ${rCard.toFixed(2)}:1 on a card and ${rPage.toFixed(2)}:1 on the page`);
    }
  });
}

// The lifted rule names every surface. A class whose background is --sem-surface and is not named would draw its
// marks, hovers and rules against the page again (D1). Only whole-box selectors count: a :hover, a pressed state
// or a scrollbar part paints the surface as a state, not as a place other things sit on.
const STUDIO_CSS = ['webview/src/styles.css', 'webview/src/plan-history.css', 'webview/src/plan-guide.css'];
check('the lifted rule', () => {
  assert.ok(LIFTED, `tokens.css has no rule "${LIFTED_PREFIX}...) > *"`);
  const named = new Set(LIFTED_LIST);
  const missing = [];
  for (const f of STUDIO_CSS) {
    const text = read(f).replace(/\/\*[\s\S]*?\*\//g, '');
    for (const m of text.matchAll(/([^{}]+)\{([^{}]*)\}/g)) {
      if (!/(^|;)\s*background(-color)?\s*:\s*var\(--sem-surface\)/.test(m[2])) continue;
      for (const sel of split(m[1].trim())) {
        if (/:hover|\[aria-|::|:focus/.test(sel)) continue;
        const last = sel.trim().split(/\s+/).pop();
        if (!named.has(last)) missing.push(`${f}: ${sel.trim()}`);
      }
    }
  }
  assert.deepEqual(missing, [], 'paints --sem-surface but is not in the lifted rule');
  for (const s of ['[style*="background: var(--sem-surface)"]', '.pw-list', '.ph-rows', '.sem-wf-list', '.xe-grid-framed', '.xe-edge-box']) {
    assert.ok(named.has(s), `the lifted rule does not name ${s}`);
  }
});

// Text on a wash holds 4.5:1 too (ruling F). The Optimize page's before and after chips put plain text on a red
// or green wash over whatever they sit on, which is a card today and could be the page tomorrow, and every check
// above measures text on the card or the page, never on a wash over one. In Solarized Light and Quiet Light,
// whose own text colour is a pale #616161, the wash took it to about 4.3:1 off the pixels. The wash is measured
// over both grounds, in every theme that paints fills at all.
const WASHES = ['--sem-wash-bad', '--sem-wash-good'];
for (const [id, t] of Object.entries(CHECKED)) {
  if (isHC(t)) continue;
  const val = computed(id), card = computed(id, true);
  const page = color(val('--sem-bg'));
  for (const wash of WASHES) {
    for (const [where, v, baseName] of [['page', val, '--sem-bg'], ['card', card, '--sem-surface']]) {
      check(`${t.label}: text on ${wash} (${where})`, () => {
        const base = over(color(v(baseName)), page);
        const ground = over(color(v(wash)), base);
        const text = over(color(v('--sem-fg')), ground), r = ratio(text, ground);
        rows.push(`${t.label.padEnd(19)} --sem-fg           ${toHex(text)} on ${wash.padEnd(16)} ${where} ${toHex(ground)}  ${r.toFixed(2)}:1`);
        assert.ok(r >= MIN, `${toHex(text)} on ${toHex(ground)} is ${r.toFixed(2)}:1, under ${MIN}:1`);
      });
    }
  }
}

// A doer keeps its hue in every theme (colour level B: blue for Semanticus, orange for you, purple for your
// assistant). VS Code hands a webview charts.orange from the find-match highlight, and a theme is free to make
// that a colour with no hue at all: Abyss's is a grey (#eeeeee44) and Quiet Light's is a pink (#EDC9D8). Mixed
// toward the text colour, a grey source stays grey, so the You track loses the one thing that tells it apart
// from the other two, and every contrast check still passes because grey on a dark page reads fine. A theme
// whose source has no hue names its own --sem-you-src (tokens.css, the hue steps). Oklab chroma 0.05 is the
// floor: the doers measure 0.09 to 0.15 where the source is a real orange, and a grey measures under 0.03.
const MIN_CHROMA = 0.05;
const chroma = (c) => { const [, A, B] = toOklab(c); return Math.hypot(A, B); };
for (const [id, t] of Object.entries(CHECKED)) {
  for (const doer of ['--sem-doer-engine', '--sem-doer-you', '--sem-doer-agent']) {
    check(`${t.label}: ${doer} keeps its hue`, () => {
      const val = computed(id);
      const c = color(val(doer)), k = chroma(c);
      rows.push(`${t.label.padEnd(19)} ${doer.padEnd(18)} ${toHex(c)}  oklab chroma ${k.toFixed(3)}`);
      assert.ok(k >= MIN_CHROMA, `${toHex(c)} has oklab chroma ${k.toFixed(3)}, under ${MIN_CHROMA}`);
    });
  }
}

// Content does not sit flush on the page (ruling D, round 1's rule C): a box in the page's own flow that draws
// the card edge at the card radius is a content surface, so it takes the card's colour, never the page's. The
// Plan's What changed card, which every existing person sees above the list after an update, painted
// var(--sem-bg) and so was invisible as a card while the Next list right under it was one. Floating boxes (a
// menu, a tooltip, the tour step) are excluded by position, because a thing over the page is not on it.
check('no card in the page flow paints the page colour', () => {
  const flush = [];
  for (const f of STUDIO_CSS) {
    const text = read(f).replace(/\/\*[\s\S]*?\*\//g, '');
    for (const m of text.matchAll(/([^{}]+)\{([^{}]*)\}/g)) {
      const body = m[2];
      if (!/border-radius\s*:\s*var\(--sem-radius-card\)/.test(body)) continue;
      if (!/var\(--sem-border\)/.test(body)) continue;
      if (/position\s*:\s*(fixed|absolute)/.test(body)) continue;
      if (!/(^|;)\s*background(-color)?\s*:\s*var\(--sem-bg\)/.test(body)) continue;
      flush.push(`${f}: ${m[1].trim().slice(0, 90)}`);
    }
  }
  assert.deepEqual(flush, [], 'a card in the page flow paints var(--sem-bg)');
});

// Nothing inside a card paints the page's colour across it (D3), and no hover paints a mark's or a card's colour
// (D2): both were found in source after the first round fixed one instance each.
// A revert guard for the spellings that were found, not a general ban: the same paint written as
// backgroundColor: 'var(--sem-bg)' or in double quotes passes it (v-surfaceB-final L5).
check('no page-coloured band and no hover on a mark colour', () => {
  const bad = [];
  for (const f of readdirSync(resolve(root, 'webview/src')).filter((x) => x.endsWith('.tsx'))) {
    read(`webview/src/${f}`).split('\n').forEach((line, i) => {
      if (/sticky/.test(line) && /background: 'var\(--sem-bg\)'/.test(line)) bad.push(`${f}:${i + 1} a sticky band in the page colour`);
      if (/hover:bg-(\[var\()?\(?--sem-(surface|surface-2|bg)\)/.test(line)) bad.push(`${f}:${i + 1} a hover on a surface colour`);
    });
  }
  for (const f of STUDIO_CSS) {
    const text = read(f).replace(/\/\*[\s\S]*?\*\//g, '');
    for (const m of text.matchAll(/([^{}]+)\{([^{}]*)\}/g)) {
      if (/:hover/.test(m[1]) && /background(-color)?\s*:\s*var\(--sem-(surface|surface-2|bg)\)/.test(m[2])) bad.push(`${f}: ${m[1].trim().slice(0, 90)}`);
    }
  }
  assert.deepEqual(bad, []);
});

// The content lists option B lifted off the page (D4) paint the card's surface, edge and corner. Taking one out of
// the rule in styles.css leaves every check above green, because a list that is not a surface is not measured as
// one; this names them. The pixel drivers in tools/uishot read the same lists off screenshots.
const CONTENT_LISTS = ['.pw-list', '.ph-rows', '.sem-wf-list', '.xe-grid-framed', '.xe-edge-box'];
check('the content lists are cards', () => {
  const found = new Set();
  const text = read('webview/src/styles.css').replace(/\/\*[\s\S]*?\*\//g, '');
  for (const m of text.matchAll(/([^{}]+)\{([^{}]*)\}/g)) {
    const body = m[2];
    if (!/(^|;)\s*background\s*:\s*var\(--sem-surface\)/.test(body)) continue;
    if (!/border\s*:[^;]*solid var\(--sem-border\)/.test(body) || !/border-radius\s*:\s*var\(--sem-radius-card\)/.test(body)) continue;
    for (const sel of split(m[1].trim())) found.add(sel.trim());
  }
  assert.deepEqual(CONTENT_LISTS.filter((x) => !found.has(x)), [], 'not drawn as a card in styles.css');
});

// An inline style that paints the page colour (D3). Inside a card a field, a chip or a band paints --sem-ground,
// which is the page on the page and the card in a card; the page colour there is a hole in the card. What still
// paints it is listed with its reason, and the test fails when the list changes in either direction, so a
// reverted swap cannot hide and a fixed one has to come off. Measured on 28 September 2026.
const PAGE_COLOUR_INLINE = {
  'artifactdialog.tsx': 1,     // a dialog over the page (R1, the coordinator's ruling pending)
  'connectionshub.tsx': 1,     // the hub's own frame, which is the page
  'consent.tsx': 1,            // the consent card over the page (R1)
  'decisions-settings.tsx': 1, // its switch's knob (the window is a Pro page section now)
  'documentation.tsx': 1,      // the document preview frame, which is the exported page in the reader's theme (K9)
  'help.tsx': 1,               // the Help panel over the page (R1)
  'main.tsx': 1,               // the error screen, which is the page
  'mcode.tsx': 1,              // the Power Query toast over the page (R1)
  'shortcuts.tsx': 1,          // the Shortcuts dialog over the page (R1)
};
check('where an inline style paints the page colour', () => {
  const seen = {};
  for (const f of readdirSync(resolve(root, 'webview/src')).filter((x) => x.endsWith('.tsx'))) {
    const n = (read(`webview/src/${f}`).match(/background: 'var\(--sem-bg\)'/g) ?? []).length;
    if (n) seen[f] = n;
  }
  assert.deepEqual(seen, PAGE_COLOUR_INLINE);
});

if (process.env.TOKEN_CONTRAST_TABLE) console.log(rows.join('\n'));
if (failures.length) {
  console.error(`token contrast: ${failures.length} failure(s)\n  ${failures.join('\n  ')}`);
  process.exit(1);
}
console.log(`token contrast: ${rows.length} text, ground and step checks passed in ${Object.keys(CHECKED).length} themes`);
