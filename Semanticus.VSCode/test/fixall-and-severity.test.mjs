import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

// Source contracts for the three UAT repairs on the Best Practice Analyzer surface:
//   1. "Fix all" reports what it could not clear, instead of reporting a clean model over findings that stayed.
//   2. A refusal message clears when the cause it named is gone.
//   3. The findings list filters by severity, and severity is a word, not only a colour.
// These read the webview SOURCE the way the other contract tests here do. The engine half of each is proven by
// Semanticus.Tests/BpaFixAllTests.cs; this file is what stops the surface from losing the engine's answer.

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const read = (file) => readFileSync(resolve(root, file), 'utf8');
const findings = read('webview/src/findings.tsx');
const bpa = read('webview/src/bpa.tsx');

// ---- 1. the press's honest remainder is shown ---------------------------------------------------------
assert.match(bpa, /interface BpaUnfixed \{/, 'Fix all must have a shape for a finding it could not clear');
assert.match(bpa, /unfixed\?: BpaUnfixed\[\]/, 'the press result must be read for what it could not clear');
assert.match(bpa, /setLeftOver\(r\?\.unfixed \?\? \[\]\)/, 'the remainder must land in state, not be dropped');
assert.match(bpa, /setLeftOver\(\[\]\)/, 'a fresh press must clear the previous press\'s remainder before it runs');
assert.match(bpa, /leftOver\.length > 0 &&/, 'the remainder must reach the page or the count above it stays a promise');
assert.match(bpa, /still on the model, and each one says why it stayed/, 'the reminder must say the findings are still there');
assert.match(bpa, /u\.reason/, 'each leftover finding must show the engine\'s reason');
// Colour is never the only channel: the heading states the count in words too.
assert.match(bpa, /could not be fixed/, 'the leftover heading must say what happened in words');

// ---- 2. a refusal clears when its cause is fixed -------------------------------------------------------
assert.match(bpa, /useEffect\(\(\) => \{ setUpsell\(null\); \}, \[tier\]\)/,
  'the Pro refusal must clear when the tier changes, or it outlives the free plan that caused it');
assert.match(bpa, /setUpsell\(null\); setErr\(null\); setLeftOver\(\[\]\);/,
  'a press must clear the old refusal and the old error before it tries, so neither survives a press that works');

// ---- 3. a severity filter that is not carried by colour alone ------------------------------------------
assert.match(findings, /export function GroupedFindings/, 'the shared findings list is where the filter belongs');
assert.match(findings, /type SevFilter = 'all' \| 'error' \| 'warning' \| 'info'/,
  'the list needs a severity filter with an all option');
assert.match(findings, /aria-pressed=\{on\}/, 'a filter chip must expose its pressed state to assistive tech');
assert.match(findings, /role="group" aria-label="Filter findings by severity"/,
  'the chips must be one labelled group, not four loose buttons');
assert.match(findings, /disabled=\{n === 0\}/, 'a bucket with nothing in it must not be offered');
assert.match(findings, /showing \{shown\.length\} of \{rows\.length\}/,
  'a narrowed list must say what it is hiding');
assert.match(findings, /No findings at this severity\. Pick another filter above\./,
  'an empty bucket must explain itself rather than rendering a blank page');
assert.match(findings, /sevCounts\[b\.key\]/, 'chip counts must come from the unfiltered rows');
assert.match(findings, /\}\), \[rows\]\);/, 'chip counts must not be recomputed from the filtered rows');

// Severity is SPOKEN. The old mark was a 6px dot whose only non-colour channel was a title tooltip, which a
// screenshot and a screen reader both lose.
assert.match(findings, /function SevBadge\(\{ severity \}/, 'severity must render as a labelled badge');
assert.match(findings, /<SevBadge severity=\{g\.severity\} \/>/, 'the rule row must use the badge, not the dot');
assert.doesNotMatch(findings, /width: 6, height: 6/, 'the colour-only severity dot must be gone');
assert.match(findings, /const sevLabel = \(s: number\) =>/, 'the badge needs the level spelled out');

// A strict-equality "Errors" bucket would drop every Critical (readiness scores it 5, BPA scores it 3).
assert.match(findings, /f === 'error' \? s >= 3 : f === 'warning' \? s === 2 : s <= 1/,
  'the buckets must be at-least for Errors, or Critical findings vanish from the Errors chip');

console.log('fix-all and severity UI contract tests passed');
