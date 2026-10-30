// The Calendars page (Edit, Advanced modelling), 1.7: the page opens on the engine's plan, one press does it, and nothing on it is
// engine words. These are source contracts; the page is driven for real by tools/uishot/calendars-drive.mjs and the engine by
// Semanticus.Tests/CalendarPlanTests.cs.
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import test from 'node:test';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const read = (f) => readFileSync(resolve(root, f), 'utf8');
const page = read('webview/src/calendars.tsx');
const adv = read('webview/src/advmodels.tsx');
const harness = read('tools/uishot/harness.html');
const shot = read('tools/uishot/shot.mjs');

test('the page reads the plan, sets up in one call, adds measures in one call, and undoes with the shared undo', () => {
  assert.match(page, /rpc<Proposal>\('proposeCalendar'/, 'the first view is the engine plan');
  assert.match(page, /rpc<SetupResult>\('setupCalendars'/, 'one press is one engine call');
  assert.match(page, /rpc<AddResult>\('addTimeMeasures'/, 'time measures are one engine call');
  assert.match(page, /rpc\('undo', 'human'\)/, 'undo is the shared undo');
  assert.doesNotMatch(page, /setCompatibilityLevel|defineCalendar|tagCalendarColumn|window\.confirm/, 'no separate upgrade step, no hand mapping calls, no confirm box');
});

test('the old Calendars panel is gone from the Advanced modelling file, and the sub-page is this one', () => {
  assert.doesNotMatch(adv, /function ClassicDateTable|function TemplateQuickStart|function CalendarCard|Confirm the one-way upgrade/);
  assert.match(adv, /from '\.\/calendars'/);
  assert.match(adv, /area === 'calendars' \? <CalendarsPanel \/>/);
});

test('the page says Preview plainly, in every state', () => {
  assert.match(page, /const Preview = /);
  const states = page.match(/<Preview \/>/g) ?? [];
  assert.ok(states.length >= 3, 'the plan, the live card and the stop card each carry it');
  assert.match(page, /preview feature in Power BI/);
});

test('the copy follows the style guide: no em dash, no "one click", no engine words, no colourful emoji', () => {
  const copy = page.replace(/^\s*\/\/.*$/mg, '');
  assert.doesNotMatch(copy, /—/, 'no em dash');
  assert.doesNotMatch(copy, /one[- ]click/i);
  for (const word of ['TimeUnit', 'compatibility level', 'time-related', 'Gregorian', 'engine']) assert.ok(!new RegExp(word, 'i').test(copy.replace(/\/\*[\s\S]*?\*\//g, '').replace(/rpc[^\n]*/g, '')), `"${word}" is not on the page`);
  assert.doesNotMatch(copy, /[\u{1F300}-\u{1FAFF}]/u, 'no colourful emoji');
});

test('every spacing and size in the page CSS comes from a token', () => {
  const css = read('webview/src/calendars.css');
  assert.doesNotMatch(css.replace(/\/\*[\s\S]*?\*\//g, ''), /(padding|margin|gap)[a-z-]*:[^;]*\b\d+(\.\d+)?px/, 'no raw pixel spacing');
});

test('the mock engine of the drive keeps the plan, setup and undo in step', () => {
  assert.match(harness, /RESPONSES\.proposeCalendar = /);
  assert.match(harness, /RESPONSES\.setupCalendars = /);
  assert.match(harness, /RESPONSES\.addTimeMeasures = /);
  assert.match(shot, /Could not find button matching UISHOT_CLICK=/, 'maintained interaction screenshots must fail when their requested action is absent');
  assert.match(shot, /process\.env\.UISHOT_EXPECT[\s\S]*page\.waitForFunction/, 'maintained interaction screenshots must be able to assert the resulting state');
});
