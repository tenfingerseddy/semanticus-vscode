// The ask router's guide for a tool is the HELP PAGE's own first line, character for character.
//
// Why this file exists, and why it is the second copy of a check that already exists.
//
// `Semanticus.Tests/AskRouteTests.cs` already holds this rule: it parses help.tsx, walks
// AskRouteCatalog.Tools, and asserts each tool's Guide equals that tool's help lead. It is a good test and
// it is not the problem. The problem is WHERE it runs. A webview lane is Node only: it edits help.tsx,
// runs `node scripts/run-tests.mjs`, sees green, commits, and finds out on CI that a .NET test it never
// ran is red because of a sentence it reworded. That happened on jev/plan-unstitch (23 September), which
// reworded the Pending changes and Model Spec leads for the unstitch and had no way to know.
//
// So the same comparison is made from the Node side, off the same two files. Neither copy is the source
// of truth; the two FILES are, and each door checks them with the tools it has. The C# regexes at
// AskRouteTests.cs:40-62 are the model for the parsing here, deliberately, so the two cannot read the
// same file differently and disagree about what it says.
//
// It compares the ONE thing that has to match. The catalogue's words, areas, labels and synonyms are its
// own business; only Guide is the help page's sentence, so only Guide is checked here.
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, resolve, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const extensionRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const repoRoot = resolve(extensionRoot, '..');
const helpPath = join(extensionRoot, 'webview', 'src', 'help.tsx');
const catalogPath = join(repoRoot, 'Semanticus.Engine', 'Plan', 'AskRouteCatalog.cs');

/** A C# string literal's body, as the compiler would read it. The catalogue carries no verbatim (@"…")
 *  strings and help.tsx is JSON-shaped, so the same small unescape serves both; anything it does not know
 *  is left alone rather than silently eaten, because a wrong unescape would turn a real mismatch into a
 *  false pass. */
function unescape(literal) {
  return literal.replace(/\\(u[0-9a-fA-F]{4}|.)/g, (whole, what) => {
    if (what[0] === 'u') return String.fromCharCode(parseInt(what.slice(1), 16));
    if (what === 'n') return '\n';
    if (what === 't') return '\t';
    if (what === 'r') return '\r';
    if (what === '"' || what === "'" || what === '\\' || what === '/') return what;
    return whole;
  });
}

// ---- the help pages' own first lines, by tool id ------------------------------------------------------
// Parsed, never hand-copied: a list of sentences in a test is a list of sentences that rots.
const helpSrc = readFileSync(helpPath, 'utf8');
const helpStart = helpSrc.indexOf('const HELP: Record<string, TabHelp> = {');
assert.ok(helpStart > 0, 'help.tsx no longer declares the HELP table the router copies from');
const helpBody = helpSrc.slice(helpStart);

const leads = new Map();
const entryPattern = /^ {2}"(\w+)": \{/gm;
const entries = [...helpBody.matchAll(entryPattern)];
for (let i = 0; i < entries.length; i += 1) {
  const from = entries[i].index;
  const to = i + 1 < entries.length ? entries[i + 1].index : helpBody.length;
  const lead = /"lead": "((?:[^"\\]|\\.)*)"/.exec(helpBody.slice(from, to));
  if (lead) leads.set(entries[i][1], unescape(lead[1]));
}
assert.ok(leads.size > 10, `help.tsx should yield a lead for every tab; parsed ${leads.size}`);

// ---- the catalogue's guides, by tool id ---------------------------------------------------------------
const catalogSrc = readFileSync(catalogPath, 'utf8');
// new AskRouteTool("id", "area", "Label",
//     "the guide",
const toolPattern = /new AskRouteTool\("(\w+)",\s*"(\w+)",\s*"((?:[^"\\]|\\.)*)",\s*\n\s*"((?:[^"\\]|\\.)*)"/g;
const tools = [...catalogSrc.matchAll(toolPattern)].map((m) => ({ id: m[1], label: unescape(m[3]), guide: unescape(m[4]) }));

// A parity test that parsed NOTHING would pass every time and police nothing, which is the failure mode
// this pair of counts exists to stop. The declared count is read off the file itself, so adding a tool
// does not need this number edited; a tool the regex cannot read does.
const declared = (catalogSrc.match(/new AskRouteTool\(\s*"/g) || []).length;   // a literal tool; the word groups build theirs from variables
assert.ok(declared > 10, `AskRouteCatalog.cs should declare the router's tools; found ${declared}`);
assert.equal(tools.length, declared,
  `every AskRouteTool must be readable here: ${declared} declared, ${tools.length} parsed. A tool whose `
  + 'guide is written in a shape this regex does not know is a tool nothing is checking.');
assert.equal(new Set(tools.map((t) => t.id)).size, tools.length, 'every tool id names one tool');

// ---- the rule ------------------------------------------------------------------------------------------
// Semanticus.Tests/AskRouteTests.cs: The_catalogue_words_are_the_help_pages_own_words.
const missing = tools.filter((t) => !leads.has(t.id)).map((t) => t.id);
assert.deepEqual(missing, [],
  `the router knows tools that help.tsx has no entry for: ${missing.join(', ')}`);

const drifted = tools.filter((t) => t.guide !== leads.get(t.id));
if (drifted.length > 0) {
  const lines = drifted.map((t) => [
    `  ${t.id} (${t.label})`,
    `    help.tsx lead : ${JSON.stringify(leads.get(t.id))}`,
    `    catalogue     : ${JSON.stringify(t.guide)}`,
  ].join('\n')).join('\n');
  assert.fail(
    `${drifted.length} ask-router guide(s) no longer say what the help page says.\n${lines}\n\n`
    + 'The catalogue quotes the help page, so the fix is to copy the lead into\n'
    + 'Semanticus.Engine/Plan/AskRouteCatalog.cs, not to change the help page back.\n'
    + 'Semanticus.Tests/AskRouteTests.cs fails the same way on CI; this is the local copy.');
}

console.log(`Ask-router parity: ${tools.length} catalogue guides are their help page's own lead, `
  + `character for character, across ${leads.size} help entries.`);
