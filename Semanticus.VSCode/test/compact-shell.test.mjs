import assert from 'node:assert/strict';
import test from 'node:test';
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

// The compact top (Kane accepted it 2026-09-14). Three rows of 40px above every page, the same everywhere:
// the header row, the area row (area name, segment strip, the ⓘ page notes), and the tool's own row. The
// publish caption, the standalone breadcrumb row and the always-open page guide are what paid for it, so
// this file refuses each of them coming back, and pins the parts that replaced them.
//
// Contract v1.5 supersedes v1.4 section 3: the two publish facts are no longer a VISIBLE caption under the
// Publish… button. They live in that button's tooltip and in the bottom bar's Publish to slot, which is
// where a person looks for a destination anyway.
const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const read = (file) => readFileSync(resolve(root, file), 'utf8');
const app = read('webview/src/App.tsx');
const help = read('webview/src/help.tsx');
const styles = read('webview/src/styles.css');
const contextbar = read('webview/src/contextbar.tsx');
const bridge = read('webview/src/bridge.ts');
const extension = read('src/extension.ts');

// assert.match prints the whole haystack on failure and these files are 30-120 kB, so say it ourselves.
const has = (src, re, msg) => assert.ok(re.test(src), msg);
const hasNot = (src, re, msg) => assert.ok(!re.test(src), msg);

// ---- 1. the header is one row ------------------------------------------------------------------
test('v1.5 the publish caption is gone and the Publish tooltip carries both facts', () => {
  hasNot(app, /studio-permission-summary/, 'the visible publish caption must not render any more');
  hasNot(styles, /studio-permission-summary/, 'the caption rules must go with the caption');
  hasNot(styles, /header\.studio-chrome\s*\{[^}]*padding-bottom:\s*24px/,
    'the extra bottom padding existed only to hang the caption under the row');
  // Both facts, in one title, on the one publish control.
  has(app, /title=\{unreachable \?[\s\S]{0,400}\$\{publishLine\}\. \$\{permissionLine\}/,
    'the Publish button tooltip must still name the destination AND the assistant permission');
  has(app, /data-testid="publish-button"/, 'the one publish control stays');
  has(styles, /header\.studio-chrome\s*\{[^}]*min-height:\s*40px/, 'the header row is 40px tall');
});

test('the two publish facts still have a home on the page', () => {
  has(contextbar, />Publish to</, 'the bottom bar keeps the destination slot');
  // The caption used to be the shortcut into Assistant permissions. That door is the header's Your assistant
  // chip, which is why removing the caption costs nobody the page.
  has(read('webview/src/activity.tsx'), /id: 'permissions', label: 'Permissions'/,
    'the assistant permission page is still one click from the header');
});

// ---- 2. the area row replaces the breadcrumb row ------------------------------------------------
test('the breadcrumb is no longer a block of its own', () => {
  hasNot(app, /studio-breadcrumb/, 'the standalone breadcrumb row is merged into the area row');
  hasNot(styles, /\.studio-breadcrumb/, 'its rules go with it');
  has(styles, /\.studio-arearow\s*\{[^}]*min-height:\s*40px/, 'the area row is 40px tall');
});

// This test used to assert the opposite: that Back appeared as soon as the history stack had something in it.
// Kane pressed that button on the Yoga on 2026-09-14 and it did not take him anywhere, so he retired the button
// rather than have it repaired. The area row now goes straight from the area name to the segment strip, and the
// history stack went with the button — there is nothing left to keep in sync with nothing.
test('there is no Back button and no history stack behind it', () => {
  hasNot(app, /studio-back/, 'the Back button is gone from the area row');
  hasNot(app, /← Back/, 'and so is its label');
  hasNot(app, /canGoBack|backDepth|setBackDepth|backStack/, 'and the stack, the depth and the prop that gated it');
  hasNot(app, /const goBack|onBack=/, 'and the handler and the prop that carried it');
});

// Settings is the one page with no segment strip of its own, so it is the one page that needs a way out. Done is
// that way out, and the rules below are the ones that make it a promise the button can keep — which is exactly
// what the old Back could not do.
test('Settings gets one Done, and Done always lands somewhere', () => {
  has(app, /isSettings = route\.destination === 'utility'/, 'Settings is the utility destination, named once');
  has(app, /\{isSettings && <button[^>]*data-testid="settings-done"[^>]*>Done<\/button>\}/,
    'Done renders on Settings pages and nowhere else');
  has(app, /data-testid="settings-done" className="sem-btn sem-btn-sm"/, 'Done is on the shared dense control step');
  // ONE auto margin at the right-hand end, on the group, not two competing ones. PageNotesButton brings its own
  // margin-left:auto (styles.css .studio-page-notes-wrap), so an ml-auto on Done as well splits the free space and
  // leaves Done halfway across the row. That is not a style nit: it is what the first build of this actually did.
  has(app, /<div className="ml-auto flex items-center gap-2">\s*\{isSettings && <button[\s\S]{0,200}<PageNotesButton tab=\{tab\} \/>\s*<\/div>/,
    'Done and the page notes are one right-hand group with a single auto margin');
  hasNot(app, /data-testid="settings-done"[^>]*ml-auto/, 'and Done does not carry a competing auto margin of its own');
  // ONE page to return to, not a stack, recorded only on the way IN.
  has(app, /const returnTo = useRef<Route \| undefined>/, 'the return page is a single route, not a history stack');
  has(app, /if \(next\.destination === 'utility' && current\.destination !== 'utility'\)/,
    'it is recorded on the way into Settings, so a second Settings route cannot overwrite it');
  // The restamp is the whole fix. goRoute drops any route whose session is not the live one, and a page recorded
  // during the cold host hand-off carries an empty stamp, so without this Done would be inert exactly as Back was.
  has(app, /sessionId: sid \|\| remembered\.sessionId/, 'the remembered page is restamped with the live session');
  has(app, /if \(!remembered\) \{ goTab\('modelhome'\); return; \}/, 'and with nothing remembered, Done goes to Overview');
  // A real model swap forgets it; the session merely becoming known does not.
  has(app, /if \(previousSid\) returnTo\.current = undefined;/,
    'only a real session change clears the return page, never the cold hand-off that simply learns the session');
  // Escape is the keyboard Done, and only on Settings.
  has(app, /if \(!isSettings\) return;[\s\S]{0,600}event\.key !== 'Escape'/, 'Escape is bound only while Settings is open');
  has(app, /isTypingTarget\(event\.target\) \|\| isTypingTarget\(document\.activeElement\)/,
    'and it never fires while the person is in a field');
  has(app, /document\.querySelector\('\[role="menu"\], \[role="dialog"\]'\)/,
    'an open menu or dialog owns Escape, not the page behind it');
});

test('the area row leads with the area and falls back to the tool name off the strip', () => {
  has(app, /studio-arearow-area/, 'the area name leads the row');
  has(app, /studio-arearow-tool/, 'a page that is not one of the area segments names itself instead');
  has(app, /const showStrip = /, 'one rule decides strip versus trail');
});

// Kane hit this one himself on the Yoga (2026-09-15): Overview > Preview data, and the row lost every segment
// because the route had a table in it. The rule is now about the AREA's tools only. The object left the row
// entirely rather than moving to a chip: the page already names what it was opened with, and a chip's clear
// control could only drop the ROUTE's object while the page kept its own selection, so it would have claimed
// to do something it had not done.
test('an object in the route never takes the segment strip away', () => {
  has(app, /const showStrip = !!group && group\.tabs\.length > 1;/, 'the strip follows the area, not the object');
  hasNot(app, /const showStrip =[^;]*route\.object/, 'the object has no say in whether the strip is shown');
  hasNot(app, /studio-arearow-object|arearow-clear-object/, 'and nothing on the row names the object');
  hasNot(styles, /\.studio-arearow-object/, 'nor paints one');
  // The tool name is never on the row twice. With the strip up, the active segment is the tool's name, so the
  // fallback <strong> is the ONLY place toolLabel may render, and the ternary has exactly two arms.
  has(app, /\? <AreaTabs group=\{group!\}[\s\S]{0,300}: <strong className="studio-arearow-tool">\{toolLabel\}<\/strong>\}/,
    'the tool name renders only when there is no strip to say it');
});

// The second exit. With Back retired, the area buttons are the standing navigation, and the one for the area you
// are already in was returning you to the page you were already looking at. It goes home instead.
test('the area you are already in is a way out, not a way back to the same page', () => {
  has(app, /routeRef\.current\.destination === area \? undefined : lastInArea\.current\[area\]/,
    'area memory is skipped for the area you are currently in');
  has(app, /else goTab\(areaHomeTool\(area, hasPlanRef\.current\)\);/, 'so that click lands on the area home');
});

test('the page guide is behind the ⓘ on every page, including Overview', () => {
  hasNot(app, /<PageGuide/, 'no page keeps an always-open guide block');
  hasNot(help, /export function PageGuide/, 'the guide block is replaced, not merely unused');
  has(app, /<PageNotesButton tab=\{tab\}/, 'every page gets the notes button');
  has(help, /export function PageNotesButton/, 'the notes button owns the guide content');
  has(help, /role="dialog"/, 'the notes open as a dialog, not a details block');
  has(help, /aria-label="Page notes"/, 'the dialog is named for a screen reader');
  has(help, /Getting started/, 'the Getting started steps move into the notes');
  has(help, /guide\.lead/, 'so does the page sentence');
  // Same keyboard contract as the header menus: Enter opens, Escape closes and returns focus.
  has(help, /function PageNotesButton[\s\S]{0,2600}closeAndReturnFocus/,
    'Escape must close the notes and put focus back on the ⓘ');
  has(styles, /\.studio-page-notes\b/, 'the ⓘ rides the shared dense control scale');
});

// Kane asked for this explicitly on 2026-09-14: the guide's words MOVE behind the ⓘ, they are not reworded on the
// way. Every string below is the one the old open guide block rendered (git show 9c55f4c5:…/help.tsx).
test('the Getting started content is retained word for word', () => {
  has(help, /\{guide\.lead\}/, 'the page sentence is the same string, not a paraphrase');
  has(help, /<GettingStarted steps=\{guide\.start\} \/>/, 'the numbered steps are the same array');
  has(help, />Getting started</, 'the heading still reads Getting started');
  has(help, />Open Help above for more detail, related tasks and explanations of common terms\.</,
    'the closing line is verbatim');
  // Uppercasing it in CSS changes how the heading reads on screen, and "word for word" includes the word.
  hasNot(help, /uppercase[^>]*>Getting started</, 'the heading is not transformed into GETTING STARTED');
});

// ---- 3. the bottom bar is one line --------------------------------------------------------------
test('the bottom bar is one 34px line of three slots plus the pill', () => {
  has(contextbar, /minHeight: 34/, 'the bar is 34px, not 38');
  has(contextbar, />Editing</);
  has(contextbar, />Tests</);
  has(contextbar, />Publish to</);
  has(contextbar, /connection-sync/, 'the sync pill stays');
  has(contextbar, />Review changes →</, 'so does Review changes');
  has(styles, /\.studio-contextbar\s*\{[^}]*grid-template-columns:[^;]*auto auto/,
    'three roles then the pill then Review changes, on one row');
  hasNot(styles, /@media \(max-width: 740px\)\s*\{\s*\.studio-contextbar \{ grid-template-columns: repeat\(2/,
    'the old multi-row stack is replaced by the Tests fold');
});

test('under 900px Tests folds into the pill tooltip', () => {
  has(styles, /@media \(max-width: 899px\)[\s\S]{0,300}connections-querying[\s\S]{0,80}display: none/,
    'the Tests slot is hidden under 900px');
  has(contextbar, /testsFolded/, 'the component knows when it is folded');
  has(contextbar, /max-width: 899px/, 'and it watches the same breakpoint the stylesheet uses');
  has(contextbar, /Tests: \$\{queryMain\}/, 'the folded Tests fact lands in the pill tooltip');
});

test('the unsaved warning may still take a second line', () => {
  has(styles, /\.studio-contextbar \.connection-sync-warn/, 'the warning keeps its own row rule');
  has(contextbar, /connection-sync-warn/, 'and the component still applies it while unsaved edits exist');
});

// ---- 4. Ctrl+scroll zoom ------------------------------------------------------------------------
test('zoom clamps at 80 and 130 and steps by 10', () => {
  has(app, /const ZOOM_MIN = 80/);
  has(app, /const ZOOM_MAX = 130/);
  has(app, /const ZOOM_STEP = 10/);
  has(app, /const ZOOM_DEFAULT = 100/);
  has(app, /Math\.min\(ZOOM_MAX, Math\.max\(ZOOM_MIN,/, 'the clamp is one expression both doors use');
});

// This used to pin the exact selector `.react-flow, [data-own-zoom]`. `canvas` joined it on 2026-09-14, after
// Astra found the ECharts lineage graph carrying neither marker, so the exact list moved to section 6 below
// with the reason written beside it. What stays here is the RULE, which has not changed: a canvas that zooms
// itself keeps its own Ctrl+wheel, and that is why this listener is not a blanket preventDefault on the body.
test('a canvas keeps its own Ctrl+wheel', () => {
  has(app, /if \(target\?\.closest\?\.\('[^']*\[data-own-zoom\][^']*'\)\) return;/,
    'the wheel handler bows out over anything that owns its own zoom gesture');
});

test('Ctrl+0 resets and the wheel is the other door', () => {
  has(app, /e\.key === '0'/, 'Ctrl+0 (or Cmd+0) resets');
  has(app, /'wheel'[\s\S]{0,120}\{ passive: false/, 'the wheel handler must be able to preventDefault');
  has(app, /ctrlKey \|\| /, 'Cmd counts as Ctrl on a Mac');
  has(app, /style=\{\{ zoom:/, 'the level is applied as CSS zoom on the app root');
});

test('the level is remembered per workspace', () => {
  has(bridge, /export function postStudioZoom/, 'the webview posts the level to the host');
  has(bridge, /export function onStudioZoom/, 'and listens for the level the host has stored');
  has(extension, /setStudioZoom/, 'the host takes the level');
  has(extension, /STUDIO_ZOOM_KEY/, 'and keeps it under one named key');
  has(extension, /workspaceState\.update\(STUDIO_ZOOM_KEY/, 'in workspaceState, so it is per workspace');
  has(extension, /workspaceState\.get<number>\(STUDIO_ZOOM_KEY\)/, 'and reads it back');
  has(extension, /type: 'studioZoom'/, 'and sends it when Studio reports itself ready');
});

test('a change says so for a second, a no-op reset does not', () => {
  has(app, /Studio at \$\{/, 'the toast names the level');
  has(styles, /\.studio-zoom-toast/, 'the toast has a style of its own');
  has(styles, /prefers-reduced-motion: reduce\)[\s\S]{0,400}\.studio-zoom-toast/,
    'reduced motion must stop the toast animating');
  has(app, /if \(next === level\.current\) return;/, 'nothing is announced when nothing changed');
});

test('Help offers the same thing without a wheel', () => {
  has(help, /section: 'Text size'/, 'the Help menu gains a Text size section');
  has(help, /id: 'textsize-smaller', label: 'Smaller'/, 'Smaller');
  has(help, /id: 'textsize-larger', label: 'Larger'/, 'Larger');
  has(help, /id: 'textsize-reset', label: 'Reset'/, 'and Reset');
  has(app, /onTextSize=\{/, 'the shell wires them to the one zoom control');
});

// ---- 5. copy ------------------------------------------------------------------------------------
test('the new copy follows the house rules', () => {
  const strings = [
    ...app.matchAll(/Studio at \$\{[^}]+\}%/g),
    ...help.matchAll(/label: '([^']+)'/g),
  ].map((m) => m[0]);
  for (const s of strings) assert.ok(!s.includes('—'), `no em dash in ${s}`);
  hasNot(help, /function PageNotesButton[\s\S]{0,2600}—/, 'no em dash in the page notes');
  hasNot(app, /studio-arearow[\s\S]{0,1200}—/, 'no em dash in the area row');
});

// ---- 6. Astra's third spot review: zoom geometry, popovers and the scrolling strip ---------------
// 2026-09-14. Four P2s at Studio zoom or on the keyboard, plus the Create menu Kane hit on his first click.
// Every one is geometry or focus, so these patterns only pin the DECISION; the proof is the browser journey in
// tools/uishot/shell-journey.mjs, which measures the boxes and probes document.activeElement at each step.
const toolrow = read('webview/src/toolrow.tsx');
const lineagegraph = read('webview/src/lineagegraph.tsx');

test('the last fold is decided by fit, not by a fixed width', () => {
  // 769 CSS px is what a 1000px window gives the header at 130% Studio zoom. The old rule capped the fold at
  // level 3 unless the header was under 720px, level 3 still did not fit, and the areas nav painted Workflows
  // over More: clicking the middle of Workflows opened the More menu.
  hasNot(app, /const deepest = header\.clientWidth < FOLD_AREAS_BELOW \? 4 : 3/,
    'the fixed-width cap on the deepest fold is gone');
  has(app, /if \(header\.clientWidth < FOLD_AREAS_BELOW\) return 4;/,
    'FOLD_AREAS_BELOW survives only as the early shortcut for a genuinely narrow header');
  has(app, /for \(let level = 1; level <= 3; level\+\+\) if \(saving\[level\] >= overflow\) return level;/,
    'levels 1 to 3 are still taken shallowest-first, so the header never folds more than it has to');
  has(app, /saving\[4\]/, 'level 4 still has a measured saving of its own');
  has(styles, /\n\.studio-groups \{[^}]*overflow: hidden/,
    'and the areas nav clips its own buttons instead of painting them outside its box');
});

test('a canvas keeps its own Ctrl+wheel, and a canvas element is always one', () => {
  // The ECharts lineage graph had neither marker, so a Ctrl+wheel on the part of its canvas that ZRender did
  // not consume resized the whole of Studio instead. Marking the container is the fix; the element-level rule
  // is the floor, so no chart added later can lose its own gesture by being forgotten here.
  has(app, /closest\?\.\('\.react-flow, \[data-own-zoom\], canvas'\)/,
    'React Flow, anything marked data-own-zoom, and any canvas element are all left alone');
  has(lineagegraph, /data-own-zoom/, 'the ECharts graph container declares that it owns its zoom gesture');
});

test('a popover is placed in one coordinate space, the zoomed root its own', () => {
  // getBoundingClientRect reports SCREEN pixels; a fixed child of a zoomed root reads its left/top in the
  // root's own zoomed pixels. Writing one into the other multiplied the position by the zoom a second time:
  // measured at 1000px and 130%, the Show menu ran 65.7px past the right edge and sat 46.7px below its button.
  has(toolrow, /export function cssZoomFactor/, 'one shared reading of the effective zoom');
  has(toolrow, /currentCSSZoom/, 'from the browser, where the browser offers it');
  has(toolrow, /export function anchorUnder/, 'and one shared placement for every anchored popover');
  has(toolrow, /b\.bottom \/ z/, 'the measured rect is divided by the zoom before it becomes a CSS pixel');
  has(toolrow, /closest\('\.studio-root'\)/, 'and the clamp is the zoomed root, not the raw viewport');
  has(app, /anchorUnder/, 'the Create menu is placed by the same helper as the tool row menu');
});

test('the Create menu is not rendered inside the strip that scrolls', () => {
  // .area-tabs is overflow-x: auto so the segment strip shrinks instead of wrapping. An absolutely positioned
  // menu inside it made the strip scrollable in both directions, and focusing the highlighted item scrolled the
  // strip to reveal it, leaving one scrolled segment and two scrollbars. Kane hit this on his first click.
  hasNot(app, /area-tabs-create-menu absolute left-0 top-full/, 'the absolute-inside-the-scroller menu is gone');
  has(app, /createPortal/, 'the menu is portalled out to the Studio root');
  has(app, /preventScroll: true/, 'and focusing an item can never scroll a container under it');
  has(styles, /\.area-tabs \{[^}]*overflow-x: auto/, 'the strip still scrolls rather than wrapping');
  has(styles, /\.area-tabs \{[^}]*overflow-y: hidden/, 'but nothing inside it can add a vertical scrollbar');
  has(styles, /\.area-tabs-create-menu \{[^}]*position: fixed/, 'the menu is placed, not flowed');
});
