// The consent card's promise, pinned against what the engine actually sends (F-229, F-231).
//
// The card is fixed copy Kane ratified, and it is a promise about the product. Two findings showed the
// promise and the product drifting apart: the measure point sent formula text the card never mentioned
// (F-229), and the ask box sent typed words the card never mentioned (F-231). On 23 September Kane chose
// to name the typed questions on the card; the formula text stopped being sent instead. These static
// checks keep the three sides in step: the card's words, the engine's declared fields and consent
// version, and the driver that renders the card and checks the version it sends.
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const repo = resolve(root, '..');
// Line-ending proof: the windows-latest CI leg checks out CRLF (.gitattributes is "* text=auto"), and a pattern that
// ends in \n finds nothing there (verify-askdata-r2 R4). Every source is read with LF endings.
const read = (...p) => readFileSync(resolve(...p), 'utf8').replace(/\r\n/g, '\n');
const card = read(root, 'webview', 'src', 'consent.tsx');
const registry = read(repo, 'Semanticus.Core', 'Decisions', 'DecisionRegistry.cs');
const driver = read(root, 'tools', 'uishot', 'consent-drive.mjs');

// ---- the card's "What goes" names the typed questions (F-231) -------------------------------------
const goes = /goes:\s*\[([^\]]*)\]/.exec(card);
assert.ok(goes, 'the card has no "What goes" list the test can read');
const goesLines = [...goes[1].matchAll(/'([^']*)'/g)].map((m) => m[1]);
assert.deepEqual(goesLines, [
  'table, column and measure names',
  'data types, formats, folders',
  'counts and relationships',
  'the questions you type into the ask box',
  'the words you type to describe a task or a workflow',
  'the words of your data questions',
  'measure and column descriptions, when you ask about your data',
  'other names the model gives a measure, when your data question uses one',
  'how a data question was read, in the words of the question, with no values or dates',
], 'the "What goes" list is not the nine lines (the ratified five, and the data questions, descriptions, synonyms and stated-reading lines for Kane\'s sweep)');

// The ask box really does send typed words, so the line is true rather than decorative.
assert.match(registry, /"ask_text"/, 'the ask box no longer sends typed words; the card line would then overclaim');
// So does Jev drafting (composer lane L5): the words a person types to describe a task or a workflow.
assert.match(registry, /"draft_words"/, 'compose.draft no longer sends typed words; the card line would then overclaim');
// And so does Ask your data on Pro (query.summarize): the words of a data question go to Jev to be read into parts.
assert.match(registry, /"question_words"/, 'query.summarize no longer sends the words of a data question; the card line would then overclaim');
// With them go the descriptions of the measures and columns listed (JevQueryReader.cs Snapshot, verify-askdata-r2 R3):
// free text the model's authors wrote, and no other point sends it, so the card names it in its own line. The
// registry's purpose for the point says so; verify-askdata.test.mjs section 5 reads the Snapshot code itself.
assert.match(registry, /names, types, descriptions and folders of the measures and columns/, 'query.summarize no longer says it sends descriptions; the card line would then overclaim');
// And, when a word of the question is one of the model's own synonyms for a measure, which measure that word names
// (Kane's decision 2, 28 Sep; JevQueryReader.MeasureOption). The registry says so, and only for a word the question uses.
assert.match(registry, /one of the model's own other names for a "\s*\+\s*"measure/, 'query.summarize no longer says it names a synonym the question uses; the card line would then overclaim');
// And, before a number is shown, the reading Semanticus built told in the question's own words (29 Sep, the stated_reading
// field; StatedReading.ForJev). The registry declares the field and says so, so the card line is true.
assert.match(registry, /"stated_reading"/, 'query.summarize no longer declares the stated reading; the card line would then overclaim');
assert.match(registry, /the reading told in the question's own words, with "\s*\+\s*"no value, date, row or count/, 'query.summarize no longer says the stated reading holds no value or date; the card line would then overclaim');

// ---- the formula text is not sent, so the card needs no line for it (F-229) -----------------------
assert.doesNotMatch(registry, /"measure_expression"/, 'the measure point still declares the formula text');
assert.match(registry, /"measure_formula_facts"/, 'the measure point does not declare its derived formula facts');

// ---- the version bumped, so a person who agreed to the old card sees it once more -----------------
const version = /public const int ConsentSchemaVersion = (\d+);/.exec(registry);
assert.ok(version, 'the engine consent version could not be read');
assert.equal(Number(version[1]), 5, 'the consent version must be 5: the one release bump of the M11 join (30 Sep), after the card and the field list changed');

// The driver fakes the engine, so its version has to be the engine's or it proves the old card.
assert.match(driver, /setConsentVersion !== 5/, 'the consent driver does not check that OK sends version 5');
assert.doesNotMatch(driver, /schemaVersionInForce: [23]\b/, 'the consent driver still fakes an old version as the version in force');
assert.match(driver, /the questions you type into the ask box/, 'the consent driver does not check the new card line');

// ---- the line that takes permission back is on a screen a person can reach -----------------------
// Slice C took the line out of Connections and left the function in place, so rpc-name-parity still found
// withdrawConsent "called from the webview" and passed while no person could press Withdraw (F-176 again,
// hidden by dead code). A name in a function nobody renders is not a door, so check the render.
const { readdirSync } = await import('node:fs');
const webviewSrc = resolve(root, 'webview', 'src');
const mounts = readdirSync(webviewSrc)
  .filter((f) => /\.tsx$/.test(f) && f !== 'consent.tsx')
  .filter((f) => /<ProAdviceLine\b/.test(read(webviewSrc, f)));
assert.ok(mounts.length > 0,
  'ProAdviceLine is rendered nowhere, so withdrawing permission has no control in VS Code (only the MCP door)');

// ---- the card grants what the engine has in force (verify-askdata defect 7) --------------------------------------
// It granted every field getDecisionPoints names, a second list beside get_consent's; on the combined build that
// included query.summarize's question words while "What goes" never named data questions. One list now.
const cardGrant = /const declared = ([\s\S]*?);\n/.exec(card);
assert.ok(cardGrant, 'the card no longer builds the list it grants in one place the test can read');
assert.match(cardGrant[1], /^consent\?\.fieldsInForce\?\.length \? \[\.\.\.consent\.fieldsInForce\]/,
  'the card grants something other than the engine\'s fields in force (get_consent)');

// ---- copy rules --------------------------------------------------------------------------------
assert.ok(!card.includes('\u2014'), 'the consent card source carries an em dash');

console.log('consent-card: ok');
