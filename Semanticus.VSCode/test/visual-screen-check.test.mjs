// ONE VISUAL LANGUAGE, the screen half. tools/uishot/screen-judge.mjs turns what measure.js reads off a
// rendered page into five counts: gaps between neighbouring cards that are not 12, page margins that are not
// 24, card padding that is not 16 (12 for a flat inset), top-level card edges off the 12-column grid, and
// cards inside cards. The drivers fail a page whose counts rise above tools/uishot/visual-baseline.json.
//
// This file runs in CI with no browser, so it feeds the judge measurements shaped exactly like measure.js
// output. The same planted 13 px gap is also rendered for real by `node tools/uishot/visual-check.mjs`, whose
// self-test must fail it before any page is scored.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { SPACE_KINDS, compareScreen, compareSpace, contrastRatio, gridLines, judge, judgeSpace, parseColor } from '../tools/uishot/screen-judge.mjs';
import { baselineText } from '../tools/uishot/screencheck.mjs';
import { THEMES } from '../tools/uishot/themes.mjs';

// A 1440-wide page with no scrollbar: content from 0 to 1440, so the grid runs 24 .. 1416.
const ROOT = { x: 0, y: 80, r: 1440, b: 900, contentLeft: 0, contentRight: 1440 };
const lines = gridLines(ROOT);
const col = (i) => lines.starts[i];
const colEnd = (i) => lines.ends[i];
const card = (i, x, r, y, b, extra = {}) => ({ i, name: `section.card${i}`, depth: 0, bordered: true, filled: true, x, r, y, b, w: r - x, h: b - y, insetLeft: 16, ...extra });

// Two half-width cards side by side, and one full-width card under them, all on the grid.
function goodPage(gap = 12) {
  const cards = [
    card(0, col(0), colEnd(5), 104, 300),
    card(1, col(6), colEnd(11), 104, 300),
    card(2, col(0), colEnd(11), 300 + gap, 500),
  ];
  return { root: ROOT, cards, vgaps: [{ a: 0, b: 2, gap, depth: 0 }], hgaps: [{ a: 0, b: 1, gap: col(6) - colEnd(5), depth: 0 }], textLeft: 41 };
}

test('the grid: 12 columns, a 12 px gutter, 24 px in from each side', () => {
  assert.equal(lines.starts.length, 12);
  assert.equal(lines.starts[0], 24);
  assert.equal(lines.ends[11], 1416);
  assert.ok(Math.abs(lines.starts[1] - lines.ends[0] - 12) < 1e-9);
});

test('a page built on the rules scores zero everywhere', () => {
  const r = judge(goodPage());
  assert.deepEqual(r.counts, { gap: 0, margin: 0, padding: 0, grid: 0, nesting: 0, 'contrast-dark': 0, 'contrast-light': 0, 'contrast-hc': 0 });
});

test('a 13 px gap between cards fails, and says so with the number', () => {
  const r = judge(goodPage(13));
  assert.equal(r.counts.gap, 1);
  assert.match(r.details.join('\n'), /gap 13 between section\.card0 and section\.card2 \(want 12\)/);
  const cmp = compareScreen('fixture', r.counts, { gap: 0, margin: 0, padding: 0, grid: 0, nesting: 0 });
  assert.equal(cmp.failures.length, 1);
  assert.match(cmp.failures[0], /fixture: gap 1, baseline 0/);
});

test('half a pixel is inside the tolerance, a whole pixel is not', () => {
  assert.equal(judge(goodPage(12.4)).counts.gap, 0);
  assert.equal(judge(goodPage(11)).counts.gap, 1);
});

test('the page margin: 28 on the left fails, and a card closer than 24 to the right edge fails', () => {
  const p = goodPage();
  p.cards = p.cards.map((c) => ({ ...c, x: c.x + 4 }));
  const r = judge(p);
  assert.equal(r.counts.margin, 1);
  assert.match(r.details.join('\n'), /margin left 28 \(want 24\)/);
  const q = goodPage();
  q.cards[1] = { ...q.cards[1], r: 1430 };
  assert.match(judge(q).details.join('\n'), /margin right 10 \(want 24 or more\)/);
});

test('a page with no cards is measured from where its first text starts', () => {
  assert.equal(judge({ root: ROOT, cards: [], vgaps: [], hgaps: [], textLeft: 38 }).counts.margin, 1);
  assert.equal(judge({ root: ROOT, cards: [], vgaps: [], hgaps: [], textLeft: 24 }).counts.margin, 0);
});

test('card padding: a bordered card wants 16, a flat inset wants 12', () => {
  const p = goodPage();
  p.cards[0] = { ...p.cards[0], insetLeft: 12 };
  p.cards.push({ i: 3, name: 'div.inset', depth: 1, bordered: false, filled: true, x: col(0) + 17, r: col(0) + 300, y: 130, b: 200, w: 283, h: 70, insetLeft: 12 });
  const r = judge(p);
  assert.equal(r.counts.padding, 1);
  assert.match(r.details.join('\n'), /section\.card0 padding 12 \(want 16\)/);
});

// Option B lifted the Plan's list into a bordered card, and its Needs you band and the opened row are tinted
// bands that run the card's full width. A band edge to edge is a group of the card's rows, not an inset set in
// from its edges, so its rows start where the card's other rows do: at the card padding. An inset that is set in
// from the card still wants 12.
test('card padding: a flat band that spans its bordered card edge to edge wants the card padding', () => {
  const p = goodPage();
  p.cards[2] = { ...p.cards[2], insetLeft: 16, innerX: col(0) + 1, innerR: colEnd(11) - 1 };
  p.cards.push({ i: 3, name: 'div.band', depth: 1, parent: 2, bordered: false, filled: true, x: col(0) + 1, r: colEnd(11) - 1, y: 320, b: 420, w: colEnd(11) - col(0) - 2, h: 100, insetLeft: 16 });
  p.cards.push({ i: 4, name: 'div.inset', depth: 1, parent: 2, bordered: false, filled: true, x: col(0) + 17, r: colEnd(11) - 17, y: 430, b: 490, w: colEnd(11) - col(0) - 34, h: 60, insetLeft: 16 });
  const r = judge(p);
  assert.equal(r.counts.padding, 1);
  assert.match(r.details.join('\n'), /div\.inset padding 16 \(want 12\)/);
  assert.doesNotMatch(r.details.join('\n'), /div\.band/);
});

test('a card edge off the grid fails with the column it missed', () => {
  const p = goodPage();
  p.cards[1] = { ...p.cards[1], x: col(6) + 10 };
  const r = judge(p);
  assert.equal(r.counts.grid, 1);
  assert.match(r.details.join('\n'), /section\.card1 left edge x=\d+ is 10px off column 7/);
});

test('a bordered card inside a card fails; a flat inset inside a card does not', () => {
  const p = goodPage();
  p.cards.push({ i: 3, name: 'div.inner', depth: 1, bordered: true, filled: true, x: 60, r: 400, y: 130, b: 200, w: 340, h: 70, insetLeft: 16 });
  p.cards.push({ i: 4, name: 'div.inset', depth: 1, bordered: false, filled: true, x: 60, r: 400, y: 210, b: 280, w: 340, h: 70, insetLeft: 12 });
  const r = judge(p);
  assert.equal(r.counts.nesting, 1);
  assert.match(r.details.join('\n'), /div\.inner is a card inside a card/);
});

test('the ratchet: worse in any one kind fails, better or equal passes, a new page must be clean', () => {
  const base = { gap: 2, margin: 1, padding: 3, grid: 0, nesting: 0 };
  assert.deepEqual(compareScreen('p', { gap: 2, margin: 1, padding: 3, grid: 0, nesting: 0 }, base).failures, []);
  assert.deepEqual(compareScreen('p', { gap: 0, margin: 0, padding: 1, grid: 0, nesting: 0 }, base).failures, []);
  assert.match(compareScreen('p', { gap: 1, margin: 1, padding: 3, grid: 1, nesting: 0 }, base).failures.join(), /p: grid 1, baseline 0/);
  assert.match(compareScreen('new', { gap: 1, margin: 0, padding: 0, grid: 0, nesting: 0 }, undefined).failures.join(), /new: not in the baseline/);
  assert.deepEqual(compareScreen('new', { gap: 0, margin: 0, padding: 0, grid: 0, nesting: 0 }, undefined).failures, []);
});

// ---- the theme check (Instrument, 23 September 2026) ------------------------------------------------------
// Every page renders in Dark Modern, Light Modern and Dark High Contrast, and no text on it may fall below
// 4.5:1 against what it sits on. measure.js reports each text box under 4.5 per theme; the judge counts them.
test('colours parse the way Chromium reports them, including color-mix results', () => {
  assert.deepEqual(parseColor('rgb(31, 31, 31)'), [31, 31, 31, 1]);
  assert.deepEqual(parseColor('rgba(0, 0, 0, 0)'), [0, 0, 0, 0]);
  const mixed = parseColor('color(srgb 0.8 0.4 0.2 / 0.5)');
  assert.deepEqual(mixed.map((v) => Math.round(v * 100) / 100), [204, 102, 51, 0.5]);
});

test('contrast ratios are WCAG ratios', () => {
  assert.equal(Math.round(contrastRatio([255, 255, 255], [0, 0, 0]) * 10) / 10, 21);
  assert.ok(contrastRatio([204, 204, 204], [31, 31, 31]) > 4.5);
  assert.ok(contrastRatio([118, 118, 118], [255, 255, 255]) >= 4.5);
  assert.ok(contrastRatio([130, 130, 130], [255, 255, 255]) < 4.5);
});

test('text below 4.5:1 counts once per theme it fails in', () => {
  const p = goodPage();
  p.contrast = {
    dark: [],
    light: [{ name: 'span.hint', text: 'Scanned 11 July', ratio: 3.9, fg: 'rgb(130,130,130)', bg: 'rgb(255,255,255)' }],
    hc: [{ name: 'span.hint', text: 'Scanned 11 July', ratio: 2.1, fg: 'x', bg: 'y' }, { name: 'b', text: 'Run', ratio: 1.2, fg: 'x', bg: 'y' }],
  };
  const r = judge(p);
  assert.equal(r.counts['contrast-dark'], 0);
  assert.equal(r.counts['contrast-light'], 1);
  assert.equal(r.counts['contrast-hc'], 2);
  assert.match(r.details.join('\n'), /light: "Scanned 11 July" in span\.hint is 3\.9:1 \(want 4\.5\)/);
});

test('the three themes carry the VS Code variables the tokens read', () => {
  assert.deepEqual(Object.keys(THEMES), ['dark', 'light', 'hc']);
  for (const [id, t] of Object.entries(THEMES)) {
    for (const v of ['--vscode-editor-background', '--vscode-foreground', '--vscode-editor-font-family', '--vscode-contrastBorder', '--vscode-errorForeground', '--vscode-editorWarning-foreground']) {
      assert.ok(v in t.vars, `${id} must set ${v}`);
    }
    assert.ok(t.bodyClass.startsWith('vscode-'), `${id} must set the body class VS Code sets`);
  }
  assert.equal(THEMES.dark.vars['--vscode-editor-background'], '#1F1F1F');
  assert.equal(THEMES.light.vars['--vscode-editor-background'], '#FFFFFF');
  assert.equal(THEMES.hc.vars['--vscode-contrastBorder'], '#6FC3DF');
});

// ---- the space family (Kane's space rule, 27 September 2026) --------------------------------------------------
// A clean measurement at one window, shaped like screencheck.mjs spaceInPage output.
const spaceAt = (vw, vh, extra = {}) => ({ vw, vh, rootW: vw, availH: vh - 120, rootScrolls: false, emptyBelow: 0, scrollers: [], hiddenActions: [], caps: [], boxes: [], primary: null, clipped: [], wrapped: [], docScrollX: 0, ...extra });
const cleanSpace = () => ({ wide: spaceAt(2000, 1200), laptop: spaceAt(1440, 900), narrow: spaceAt(900, 900) });

test('space: a page that fills its window scores zero in every space kind', () => {
  const r = judgeSpace(cleanSpace());
  assert.deepEqual(r.counts, Object.fromEntries(SPACE_KINDS.map((k) => [k, 0])));
});

test('space: the AI instructions faults of 27 September are caught, once each, with the element named', () => {
  const s = cleanSpace();
  // 328 px of text hidden in a 260 px box with 555 px empty below; Save 3 px visible inside it, at two sizes.
  const box = { name: 'div.xe-instr-scroll', h: 260, hiddenPx: 328 };
  const save = { name: 'button.sem-btn.sem-btn-primary', text: 'Save', visible: 3 / 24 };
  s.wide = { ...s.wide, emptyBelow: 555, scrollers: [box], hiddenActions: [save] };
  s.laptop = { ...s.laptop, emptyBelow: 255, scrollers: [box], hiddenActions: [save] };
  const r = judgeSpace(s);
  assert.equal(r.counts['boxed-scroll'], 1);
  assert.equal(r.counts['hidden-action'], 1);
  assert.match(r.details.join('\n'), /wide: div\.xe-instr-scroll scrolls 328 px inside itself while 555 px of the box it sits in is empty/);
  assert.match(r.details.join('\n'), /"Save" .* is 13% visible inside a scroller/);
});

test('space: a box that scrolls is fine when the page has no room, and 80 px is the line', () => {
  const s = cleanSpace();
  s.wide = { ...s.wide, emptyBelow: 80, scrollers: [{ name: 'div.list', h: 400, hiddenPx: 900 }] };
  assert.equal(judgeSpace(s).counts['boxed-scroll'], 0);
  s.wide = { ...s.wide, emptyBelow: 81 };
  assert.equal(judgeSpace(s).counts['boxed-scroll'], 1);
  s.wide = { ...s.wide, rootScrolls: true };
  assert.equal(judgeSpace(s).counts['boxed-scroll'], 0);
});

test('space: a box that scrolls inside a stretched card is caught by the card\'s own empty space', () => {
  // The fault the check could not see: the card runs to the page bottom, so there is no empty band under the page's
  // lowest content, and the empty space is inside the card instead. slack is that space, measured per box.
  const s = cleanSpace();
  s.wide = { ...s.wide, emptyBelow: 0, scrollers: [{ name: 'div.xe-instr-text', h: 200, hiddenPx: 584, slack: 551 }] };
  assert.equal(judgeSpace(s).counts['boxed-scroll'], 1);
  assert.match(judgeSpace(s).details.join('\n'), /scrolls 584 px inside itself while 551 px of the box it sits in is empty/);
  s.wide = { ...s.wide, scrollers: [{ name: 'div.xe-instr-text', h: 200, hiddenPx: 584, slack: 80 }] };
  assert.equal(judgeSpace(s).counts['boxed-scroll'], 0, '80 px is the line for a box, as it is for a page');
});

test('space: a hidden control counts while the page does not scroll, and a list row does not', () => {
  // The ratified rule 2 is "less than 75% visible inside a scrolling ancestor, while the page root does not scroll".
  // It used to need 80 px of empty page as well, which is why the Docs side panel hid its own Save unflagged.
  const s = cleanSpace();
  const save = { name: 'button.sem-btn', text: 'Save', visible: 0, box: 'aside.sem-card', listRow: false };
  s.laptop = { ...s.laptop, emptyBelow: 0, hiddenActions: [save] };
  assert.equal(judgeSpace(s).counts['hidden-action'], 1, 'a full page that hides a panel\'s Save still fails');
  s.laptop = { ...s.laptop, hiddenActions: [{ ...save, listRow: true }] };
  assert.equal(judgeSpace(s).counts['hidden-action'], 0, 'a row of a long list is the list working, not a hidden action');
  s.laptop = { ...s.laptop, rootScrolls: true, hiddenActions: [save] };
  assert.equal(judgeSpace(s).counts['hidden-action'], 0, 'a page that scrolls can reach it');
});

test('space: a page cap on a wide screen fails only while the page scrolls, and data-readable excuses it', () => {
  const s = cleanSpace();
  const cap = { name: 'div.sem-centered-page', w: 1468, maxWidth: '1468px', readable: false };
  s.wide = { ...s.wide, caps: [cap] };
  assert.equal(judgeSpace(s).counts['page-cap'], 0, 'a page that fits has nothing to scroll past');
  s.wide = { ...s.wide, rootScrolls: true };
  assert.equal(judgeSpace(s).counts['page-cap'], 1);
  s.wide = { ...s.wide, caps: [{ ...cap, readable: true }] };
  assert.equal(judgeSpace(s).counts['page-cap'], 0);
});

test('space: a box the same size at 2000 x 1200 and 1440 x 900 does not grow with the window', () => {
  const s = cleanSpace();
  const hub = { path: 'div:0/div:1', name: 'div.hub', w: 1128, h: 788 };
  s.wide = { ...s.wide, boxes: [hub] };
  s.laptop = { ...s.laptop, boxes: [hub] };
  assert.equal(judgeSpace(s).counts['fixed-box'], 1);
  s.wide = { ...s.wide, boxes: [{ ...hub, w: 1840, h: 1116 }] };
  assert.equal(judgeSpace(s).counts['fixed-box'], 0);
});

test('space: a data-primary list that stops at a count with room below fails; one that shows in full does not', () => {
  const s = cleanSpace();
  s.wide = { ...s.wide, emptyBelow: 222, primary: { name: 'section.pw-list', h: 700, more: 'Show all 10', clipped: false } };
  assert.equal(judgeSpace(s).counts['primary-small'], 1);
  s.wide = { ...s.wide, primary: { name: 'section.pw-list', h: 200, more: null, clipped: false } };
  assert.equal(judgeSpace(s).counts['primary-small'], 0, 'a short answer that shows in full is fine');
  s.wide = { ...s.wide, primary: { name: 'div.result', h: 96, more: null, clipped: true } };
  assert.equal(judgeSpace(s).counts['primary-small'], 1);
});

test('space: at 900 wide a sideways scroll, a cut-off label and a wrapped button each count', () => {
  const s = cleanSpace();
  s.narrow = { ...s.narrow, docScrollX: 40, clipped: [{ name: 'button.a', text: 'Choose fields' }], wrapped: [{ name: 'button.b', text: 'Show all' }] };
  assert.equal(judgeSpace(s).counts.narrow, 3);
});

test('space: the ratchet holds each kind, and a page not in the space baseline must be clean', () => {
  const counts = { ...Object.fromEntries(SPACE_KINDS.map((k) => [k, 0])), 'boxed-scroll': 2 };
  assert.equal(compareSpace('edit-instructions', counts, { 'boxed-scroll': 2 }).failures.length, 0);
  assert.match(compareSpace('edit-instructions', counts, { 'boxed-scroll': 1 }).failures[0], /space boxed-scroll 2, baseline 1/);
  assert.match(compareSpace('new-page', counts, undefined).failures[0], /not in the space baseline/);
  assert.deepEqual(compareSpace('edit-instructions', { ...counts, 'boxed-scroll': 0 }, { 'boxed-scroll': 2 }).lowerable, ['boxed-scroll']);
});

test('space: a seeded run carries the reason a count is recorded, and writes none when there is none', () => {
  // A seeded run rewrites the whole baseline file from the object in memory. spaceNotes is the only thing in it
  // that no run recomputes, so a rewrite that forgets it silently throws away why every non-zero count is allowed
  // to be non-zero, and the next reader sees three bare counts with nothing to say they were accepted on purpose.
  const file = { total: 0, pages: {}, space: { 'edit-queries': { 'boxed-scroll': 1 } }, spaceNotes: { 'edit-queries': 'Accepted on 27 September; the redesign lane takes it to 0.' } };
  const written = JSON.parse(baselineText(file));
  assert.deepEqual(written.spaceNotes, file.spaceNotes);
  assert.deepEqual(written.space, file.space);
  assert.equal('spaceNotes' in JSON.parse(baselineText({ total: 0, pages: {} })), false);
  assert.equal(baselineText(file).endsWith('\n'), true);
});
