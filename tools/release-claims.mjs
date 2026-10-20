// Every claim the release's words make, and the code each claim needs (m11/completeness, coordinator ruling D1).
//
// This lane writes the release's copy: the What changed card, Help's "What Jev decides (Pro)", both READMEs,
// docs/assistant-skills.md, the assistant's skill pack and the tool text of a few MCP tools. It merges LAST, after every
// feature lane, so it describes the release as it ships, and some of its claims are only true once another lane has
// merged. Each claim below names the words that make it, what in the source has to be true for it to hold, and, where
// the code arrives with another lane, that lane.
//
//   node tools/release-claims.mjs          STRICT. The integrator runs this after the merge. It fails when any claim the
//                                          words make has no code behind it, whichever lane it waits for.
//   npm test (completeness-copy-truth)     The same list, with one allowance: a claim whose code arrives with a named
//                                          lane is reported as waiting, not failed, so this branch can pass on its own.
//                                          Everything else fails there too: an unbacked claim with no lane (the date
//                                          table is off for the release, so its words may not appear anywhere), what
//                                          goes to Jev, and the README's line on inference.
//
// How a claim is found: each surface is cut into sentences (a Markdown paragraph is joined first, so a wrapped sentence
// is read whole), and a claim's pattern is tested against each sentence. A claim marked `jev` counts only in a sentence
// whose paragraph speaks for Jev (says "Jev", or is Help's Jev section), because "base measures" by hand is free and true.
// What this cannot see: a claim worded so that no pattern here matches it. A new promise needs a new row; the patterns
// are wide on purpose (any "Ask your data", any "date table").
import { readFileSync, readdirSync, existsSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

export const repo = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const read = (rel) => readFileSync(join(repo, rel), 'utf8');
const has = (rel) => existsSync(join(repo, rel));

// ---- what the source says ------------------------------------------------------------------------------------------
const mcp = readdirSync(join(repo, 'Semanticus.Engine')).filter((f) => /^McpTools.*\.cs$/.test(f))
  .map((f) => read(`Semanticus.Engine/${f}`)).join('\n');
const tool = (name) => mcp.includes(`McpServerTool(Name = "${name}")`);
const toolCount = () => [...mcp.matchAll(/McpServerTool\(Name = "/g)].length;
const registry = () => read('Semanticus.Core/Decisions/DecisionRegistry.cs');
const allSource = (dir) => readdirSync(join(repo, dir), { recursive: true }).filter((f) => f.endsWith('.cs'))
  .map((f) => read(join(dir, f))).join('\n');
let engineText = null;
const engine = () => (engineText ??= allSource('Semanticus.Engine'));
let testsText = null;
const tests = () => (testsText ??= allSource('Semanticus.Tests'));
const web = (f) => (has(`Semanticus.VSCode/webview/src/${f}`) ? read(`Semanticus.VSCode/webview/src/${f}`) : '');
/** A family's replay switch, `internal const bool JevDecides`: true, false, or null when the family is not here. */
function jevDecides(family) {
  const rel = `Semanticus.Engine/Plan/JevFamilies/${family}.cs`;
  if (!has(rel)) return null;
  const m = /internal const bool JevDecides\s*=\s*(true|false)\s*;/.exec(read(rel));
  return m ? m[1] === 'true' : null;
}
const constBool = (rel, name) => {
  if (!has(rel)) return null;
  const m = new RegExp(`const bool ${name}\\s*=\\s*(true|false)\\s*;`).exec(read(rel));
  return m ? m[1] === 'true' : null;
};
const need = (what, ok) => ({ what, ok });
/** The Jev reader names a measure's synonym only when the question uses it, which is what m11/jev-query-tune builds
 * (its JevQueryReader.SaidSynonyms, read into the measure's option). Round 4's check wanted a quoted request field that
 * names synonyms, which that lane never sends, so the claim could not pass on the lane it waits for (verifier round 4,
 * F3). Exported so a test can hold the check to that lane's code. */
export const readsSaidSynonyms = (text) => /\bSaidSynonyms\s*\(/.test(text) && /another name for/.test(text);
const synonymsSent = () => readsSaidSynonyms(has('Semanticus.Engine/Query/JevQueryReader.cs') ? read('Semanticus.Engine/Query/JevQueryReader.cs') : '');

// The Plan statement (one click, a best-practice grade, a SQL start) is settled only by a rerun on the joined build,
// after m11/plan-harm, m11/plan-rows, m11/bpa-grade, m11/sql-start and m11/rule-clashes have merged. Until the
// integrator records that the rerun passed by setting this to true, in the same commit that names the rerun, no copy
// may make any of those claims, and the row below fails on this branch, in npm test and in the strict run alike.
const PLAN_STATEMENT_RERUN_PASSED = false;
const PLAN_STATEMENT = new RegExp([
  String.raw`\bBuild from a (?:SQL )?source\b`,
  String.raw`\bone[- ](?:click|button|press) (?:build|best[- ]practices?|plan)\b`,
  String.raw`\bone[- ]click fix(?:es)? (?:for|on|across) (?:every|everything|all|the whole|your whole|each)\b`,
  String.raw`\bfix(?:es|ed|ing)? (?:every|everything|all|each|the whole|your whole)\b[^.]*\b(?:in|with) (?:one|a single) (?:click|press|go)\b`,
  String.raw`\bbest[- ]practices? grade\b`, String.raw`\bgrade [A-F] (?:on|for) best[- ]practices?\b`,
  String.raw`\bgraded? (?:on|for|against) best[- ]practices?\b`,
  String.raw`\b(?:start|begin|build|create)s?\b[^.]{0,40}\bfrom (?:a |your |the )?SQL\b`,
  String.raw`\bSQL start\b`,
  // Paraphrases the round 4 verifier found the row missed (L5): "Model quality now shows an A to F grade", "Pro fixes
  // every issue with one click" (above, "with"), "New model from SQL".
  String.raw`\b(?:model quality|best[- ]practices?)\b[^.]{0,40}\b[A-F][- ]to[- ][A-F] (?:grade|letter)\b`,
  String.raw`\b(?:new|a) (?:semantic )?model from (?:a |your |the )?SQL\b`,
].join('|'), 'i');

// ---- the claims ----------------------------------------------------------------------------------------------------
export const CLAIMS = [
  { id: 'six-areas', re: /\bsix areas\b/i,
    needs: [need("TAB_GROUPS in App.tsx is Plan, Explore, Edit, Review, Publish and Workflows",
      () => ['Plan', 'Explore', 'Edit', 'Review', 'Publish', 'Workflows'].every((l) => new RegExp(`id: '\\w+', label: '${l}', tabs`).test(web('App.tsx')))
        && !/label: 'Overview', tabs/.test(web('App.tsx')))] },
  { id: 'plan-first', re: /Plan is (?:now )?where (?:you|the user) starts?|Overview is gone/i,
    needs: [need('no Overview area in TAB_GROUPS', () => !/label: 'Overview', tabs/.test(web('App.tsx')))] },
  { id: 'history-in-plan', re: /History (?:moved into|lives inside) the Plan|Open History at the top|with History \(/i,
    needs: [need('plan.tsx shows PlanHistory', () => /<PlanHistory\b/.test(web('plan.tsx')))] },
  { id: 'runs-as-rows', re: /Workflow runs show as rows|workflow run is a row|Workflow runs are rows/i,
    needs: [need("a Plan row can come from a run (PlanOriginLink kind 'run')", () => /kind: [^;]*'run'/.test(web('plan-model.ts')))] },
  { id: 'one-switch', re: /One switch pauses/i,
    needs: [need('setAssistantQueue on the shared door (the assistant has set_assistant_queue too, since 10 Oct)', () => /setAssistantQueue\(/.test(read('Semanticus.Engine/EngineRpcTarget.cs')) && tool('set_assistant_queue'))] },
  { id: 'connections', re: /Connections (?:are|is) on one page|SQL endpoint for each model/i,
    needs: [need('the Connections page names a SQL endpoint', () => /SQL endpoint/.test(web('connectionshub.tsx') + web('connectionshub-model.ts')))] },
  { id: 'describe-workflow', re: /describe a workflow in words|workflow you describe|describe_workflow_steps|Turn words into workflow steps/i,
    needs: [need('the describe_workflow_steps tool', () => tool('describe_workflow_steps')),
      need('the Workflows page calls describeWorkflowSteps', () => /'describeWorkflowSteps'/.test(web('workflows.tsx')))] },
  { id: 'jev-drafts-workflows', jev: true, re: /workflow you describe|describe_workflow_steps` for a workflow|a workflow you describe on the Workflows page/i,
    needs: [need('JevDraftBuilder.JevDraftsOnWorkflows is true', () => constBool('Semanticus.Engine/Composer/JevDraftBuilder.cs', 'JevDraftsOnWorkflows') === true)] },
  { id: 'ctrl-j', re: /\bCtrl J\b/,
    needs: [need('Ctrl J opens the ask box (package.json keybinding)', () => /"command": "semanticus\.studioAsk", "key": "ctrl\+j"/.test(read('Semanticus.VSCode/package.json')))] },
  { id: 'jev-drafts-tasks', jev: true, arrivesWith: 'm11/sol-fixes-1 (which carries m11/describe-jev)',
    re: /task you type on the Plan|task typed on the Plan|words that describe a task/i,
    needs: [need('JevDraftBuilder.JevDraftsOnPlan is true (Jev drafts a task typed on the Plan or with Ctrl J)',
      () => constBool('Semanticus.Engine/Composer/JevDraftBuilder.cs', 'JevDraftsOnPlan') === true)] },
  { id: 'ask-your-data', arrivesWith: 'm11/askdata', re: /Ask your data/i,
    needs: [need('the Query page webview/src/query.tsx (Ask your data and Queries became one page in 1.6)', () => has('Semanticus.VSCode/webview/src/query.tsx')),
      need('App.tsx mounts QueryView', () => /<QueryView\b/.test(web('App.tsx')))] },
  { id: 'ask-data-tools', arrivesWith: 'm11/askdata', re: /\bask_data\b|\blist_data_questions\b/,
    needs: [need('the ask_data tool', () => tool('ask_data')), need('the list_data_questions tool', () => tool('list_data_questions'))] },
  { id: 'ask-data-sends', jev: true, arrivesWith: 'm11/askdata', re: /\bdescriptions\b|questions in the ask box(?:,| and) in Ask your data/i,
    needs: [need("the query.summarize point sends question_words and model_fields", () => /"question_words"/.test(registry()) && /"model_fields"/.test(registry())),
      need('JevQueryReader.JevReads is true', () => constBool('Semanticus.Engine/Query/JevQueryReader.cs', 'JevReads') === true)] },
  // The Queries question box (m11/queries-redesign) asks Jev through askData too, so the privacy list names it
  // (verifier round 3, L4).
  { id: 'queries-question-sends', jev: true, arrivesWith: 'm11/queries-redesign', re: /question box on Queries|Queries question box/i,
    needs: [need('the Queries question box, webview/src/queriesask.tsx, asks through the askData method', () =>
      has('Semanticus.VSCode/webview/src/queriesask.tsx') && /\bASK_METHOD\b|'askData'/.test(web('queriesask.tsx')))] },
  // Kane, 28 Sep: the Jev reader also sends the model's own other names (synonyms) for things, and Ask your data says
  // "Showing X. You may have meant Y." when two readings are fair. Neither is in any lane's code on 28 Sep; the words
  // are the release's, so the claims wait for the lane that builds them, m11/jev-query-tune (Kane's decisions 1 and 2
  // in its brief; it branches off m11/query-split, which has neither), and the strict run fails until it lands.
  { id: 'jev-reads-other-names', jev: true, arrivesWith: "m11/jev-query-tune (the Jev reader, Kane's 28 Sep decisions 1 and 2)",
    re: /other names your model gives|\bits synonyms\b/i,
    needs: [need("a measure's option names the synonym the question uses (JevQueryReader.SaidSynonyms, read into the option as 'another name for'), which m11/jev-query-tune builds", synonymsSent)] },
  { id: 'two-readings', arrivesWith: "m11/jev-query-tune (the Jev reader, Kane's 28 Sep decisions 1 and 2)",
    re: /You may have meant|read two ways|names the other/i,
    needs: [need('an answer that says "You may have meant" (engine Query code or the Ask your data page)', () =>
      /You may have meant/.test(has('Semanticus.Engine/Query') ? allSource('Semanticus.Engine/Query') : '') || /You may have meant/.test(web('askdata.tsx') + web('askdata-model.ts')))] },
  { id: 'column-formats', jev: true, re: /column formats|a column's format/i,
    needs: [need('the column.format point', () => /ColumnFormat = "column\.format"/.test(registry())),
      need('ColumnFormatFamily is here and not switched off', () => has('Semanticus.Engine/Plan/JevFamilies/ColumnFormatFamily.cs') && jevDecides('ColumnFormatFamily') !== false)] },
  { id: 'base-measures', jev: true, arrivesWith: 'm11/m4-columns-fair', re: /base measure|column\.summarize point/i,
    needs: [need('ColumnSummarizeFamily.JevDecides is true', () => jevDecides('ColumnSummarizeFamily') === true)] },
  { id: 'what-copilot-sees', jev: true, arrivesWith: 'm11/m4-aischema', re: /what Copilot sees|ai\.schema point/i,
    needs: [need('AiSchemaFamily.JevDecides is true', () => jevDecides('AiSchemaFamily') === true)] },
  // Off for the release (coordinator D1; plan-waits 165ba3d0 DT1: the rule decides for everyone). No lane brings it.
  { id: 'date-table', jev: true, re: /date[ .]table/i,
    needs: [need('DateTableFamily.JevDecides is true', () => jevDecides('DateTableFamily') === true)] },
  { id: 'plan-order', jev: true, arrivesWith: 'm11/m4-relationships', re: /puts the plan in order|the Plan's order|the plan's order|order the plan/i,
    needs: [need('the plan.rank point', () => /PlanRank = "plan\.rank"/.test(registry())),
      // Every live ranking failed with HTTP 422 until e03216bf; its live test lands with the fix.
      need("plan.rank's 422 fix (the test Live_the_vendor_answers_a_plan_rank_request, e03216bf)", () => tests().includes('Live_the_vendor_answers_a_plan_rank_request'))] },
  { id: 'what-pro-decides', re: /what Pro decides/i,
    needs: [need("the Pro page section 'What Pro decides'", () => web('propage.tsx').includes('<DecisionSettingsSection />')),
      need('the set_decision_point_enabled tool switches a point off', () => tool('set_decision_point_enabled'))] },
  { id: 'consent-first', re: /Nothing (?:is sent|goes) (?:to Jev )?until/i,
    needs: [need('the get_consent and set_consent tools', () => tool('get_consent') && tool('set_consent')),
      need('DecisionService reads the consent before it asks', () => /ReadConsent/.test(read('Semanticus.Engine/Decisions/DecisionService.cs')))] },
  { id: 'consent-marker', re: /Semanticus_ConsentId|small marker/i,
    needs: [need('the engine writes the Semanticus_ConsentId annotation', () => engine().includes('ConsentIdAnnotation = "Semanticus_ConsentId"'))] },
  { id: 'tool-count', re: /more than 340 operations/i,
    needs: [need('more than 340 MCP tools declared', () => toolCount() > 340)] },
  { id: 'live-field', re: /'live'/,
    needs: [need('get_decision_points reports Live, and ai.schema reads its own switch', () => /Live = DecisionPointLiveInThisBuild/.test(engine())
      && /DecisionRegistry\.AiSchema => /.test(read('Semanticus.Engine/LocalEngine.Decisions.cs')))] },
  // No lane: until the rerun passes, any sentence that makes the Plan statement's claims fails (verifier round 3, F2).
  // "every one-click fix", "Apply a safe fix in one click" and the Model Spec wizard's "Draft from a SQL source" name
  // what exists now and do not match.
  { id: 'plan-statement',
    // "fix in one click" as the feature's name, not one fix done in one click ("Apply a safe fix in one click", Help).
    re: { test: (s) => (/\bfix in one click\b/i.test(s) && !/\b(?:a|one|each|this|that|the)\s+(?:\w+\s+)?fix in one click\b/i.test(s))
      || PLAN_STATEMENT.test(s) },
    needs: [need("the Plan statement's rerun on the joined build has passed (m11/plan-harm, m11/plan-rows, m11/bpa-grade, m11/sql-start and m11/rule-clashes merged; PLAN_STATEMENT_RERUN_PASSED in tools/release-claims.mjs). Until then no copy may claim one click, a best-practice grade or a SQL start",
      () => PLAN_STATEMENT_RERUN_PASSED)] },
  { id: 'describe-placeholder', re: /stays a placeholder with its reason/i,
    needs: [need('DescribedSteps keeps a sure no, and an add the adding rule does not allow, as placeholders with the reason', () => /DescribeJevNone/.test(read('Semanticus.Engine/Composer/DescribedSteps.cs'))
      && /AddingRule\.Read\(/.test(read('Semanticus.Engine/Composer/DescribedSteps.cs'))
      && has('Semanticus.Engine/Composer/AddingRule.cs') && /NoAddReason/.test(read('Semanticus.Engine/Composer/AddingRule.cs')))] },
];

// ---- the surfaces --------------------------------------------------------------------------------------------------
function quoted(text) { return [...text.matchAll(/'((?:[^'\\\n]|\\.)*)'|"((?:[^"\\\n]|\\.)*)"/g)].map((m) => m[1] ?? m[2]); }
/** Paragraphs (a Markdown paragraph joined, a table row or list item its own), each with its sentences. */
export function paragraphs(text) {
  const units = [];
  for (const block of text.split(/\r?\n\s*\r?\n/)) {
    let item = null;
    for (const line of block.split(/\r?\n/)) {
      const t = line.trim();
      if (!t) continue;
      if (t.startsWith('|')) { if (item) units.push(item); item = null; units.push(t); continue; }
      if (/^([-*]|\d+\.)\s/.test(t) || /^#/.test(t)) { if (item) units.push(item); item = t; continue; }
      item = item ? `${item} ${t}` : t;
    }
    if (item) units.push(item);
  }
  return units.map((u) => ({ text: u, sentences: u.split(/(?<=[.!?])\s+/).map((s) => s.trim()).filter(Boolean) }));
}
function toolText(name) {
  const at = mcp.indexOf(`Name = "${name}"), Description("`);
  if (at < 0) return '';
  const from = mcp.indexOf('Description("', at) + 'Description("'.length;
  return mcp.slice(from, mcp.indexOf('")]', from));
}
/** The release's copy, cut into sentences. allHelp adds every Help section, which only the rows marked for it read. */
export function surfaces({ allHelp: withAllHelp = false } = {}) {
  const guide = read('Semanticus.VSCode/webview/src/plan-guide.tsx');
  const cardId = /WHAT_CHANGED_CARD_ID = '([^']+)'/.exec(guide)[1];
  const cardAt = guide.indexOf(`'${cardId}': {`);
  const card = quoted(guide.slice(guide.indexOf('lines: [', cardAt), guide.indexOf('],', cardAt))).filter((s) => s !== 'lines');
  const help = read('Semanticus.VSCode/webview/src/help.tsx');
  const jevAt = help.indexOf('"What Jev decides (Pro)"');
  const jevHelp = jevAt < 0 ? [] : quoted(help.slice(help.indexOf('[', jevAt), help.indexOf(']', jevAt))).filter(Boolean);
  // Every Help section, for the rows that must see all of Help (the Plan statement: m11/bpa-grade adds a section of its
  // own, verifier round 4 L5). The other rows read the Jev section only, as before.
  const allHelp = quoted(help).filter((q) => /\s/.test(q) && /[.!?]$/.test(q.trim()));
  return [
    ...(withAllHelp ? [{ name: 'Help, every section', only: ['plan-statement'],
      paragraphs: allHelp.map((q) => ({ text: q, sentences: q.split(/(?<=[.!?])\s+/).map((x) => x.trim()).filter(Boolean) })) }] : []),
    { name: `What changed card ${cardId}`, paragraphs: card.map((l) => ({ text: l, sentences: [l] })) },
    { name: 'Help, What Jev decides (Pro)', allJev: true, privacy: true,
      paragraphs: [{ text: jevHelp.join(' '), sentences: jevHelp.flatMap((b) => b.split(/(?<=[.!?])\s+/)) }] },
    ...['README.md', 'Semanticus.VSCode/README.md'].map((rel) => ({ name: rel, privacy: true, paragraphs: paragraphs(read(rel)) })),
    // The repo's own skill is private and left out of the public source mirror; the private repo (marked by its mirror
    // manifest) always reads it, so a missing skill still fails there.
    ...['docs/assistant-skills.md',
      ...(has('tools/release/mirror-manifest.json') ? ['.claude/skills/semanticus/SKILL.md'] : []), // mirror-optional: private skill
      'Semanticus.VSCode/assistant-pack/plugins/semanticus/skills/semanticus/SKILL.md']
      .map((rel) => ({ name: rel, paragraphs: paragraphs(read(rel)) })),
    ...['set_summarize_by', 'mark_date_table', 'set_ai_data_schema', 'get_decision_points', 'describe_workflow_steps']
      .map((op) => ({ name: `MCP ${op}`, paragraphs: paragraphs(toolText(op)) })),
  ];
}

// ---- the check -----------------------------------------------------------------------------------------------------
/** strict: a claim waiting for a lane fails too. Returns { failures, waiting, made, notMade }. */
export function check({ strict }) {
  const failures = [];
  const waiting = new Map();
  const made = new Map();
  const all = surfaces({ allHelp: true });
  for (const surface of all)
    for (const p of surface.paragraphs) {
      const speaksForJev = surface.allJev || /\bJev\b/.test(p.text);
      for (const s of p.sentences)
        for (const c of CLAIMS) {
          if (!c.re.test(s) || (c.jev && !speaksForJev) || (surface.only && !surface.only.includes(c.id))) continue;
          if (!made.has(c.id)) made.set(c.id, []);
          made.get(c.id).push(`${surface.name}: "${s}"`);
        }
    }
  for (const c of CLAIMS) {
    if (!made.has(c.id)) continue;
    const missing = c.needs.filter((n) => !n.ok()).map((n) => n.what);
    if (missing.length === 0) continue;
    const where = made.get(c.id);
    const line = `${c.id}: needs ${missing.join('; ')}. Said ${where.length} time(s), first in ${where[0]}`;
    if (c.arrivesWith && !strict) { waiting.set(c.id, `${line} (arrives with ${c.arrivesWith})`); continue; }
    failures.push(c.arrivesWith ? `${line} (was to arrive with ${c.arrivesWith}; merge it or drop these words)` : line);
  }

  // What goes to Jev (D2): each privacy surface names the words a person types, and names Ask your data's descriptions
  // and a task's words exactly while this build sends them. "Only names, types and counts" is refused outright.
  const reg = registry();
  const typed = /"draft_words"|"ask_text"/.test(reg);
  const plan = constBool('Semanticus.Engine/Composer/JevDraftBuilder.cs', 'JevDraftsOnPlan') === true;
  const askdata = /"question_words"/.test(reg);
  for (const surface of all.filter((x) => x.privacy)) {
    const text = surface.paragraphs.filter((p) => /\bJev\b/.test(p.text) || surface.allJev).map((p) => p.text).join(' ');
    if (/only names, types and counts|names, types and counts go/i.test(text))
      failures.push(`${surface.name} says only names, types and counts go to Jev, but the words a person types go too`);
    if (typed && !/words you type/i.test(text))
      failures.push(`${surface.name} does not say the words a person types go to Jev (compose.draft sends draft_words, ask.route sends ask_text)`);
    if (!/(?:row|value)[^.]*never goes|never goes[^.]*value/i.test(text))
      failures.push(`${surface.name} does not say a row or a value from the data never goes`);
    if (strict && plan && !/describe a task/i.test(text))
      failures.push(`${surface.name} does not say a task's words go to Jev, though JevDraftsOnPlan is true`);
    if (strict && synonymsSent() && !/other names/i.test(text))
      failures.push(`${surface.name} does not say the model's other names (synonyms) go to Jev, though the Jev reader sends them`);
    if (strict && askdata && !/descriptions/i.test(text))
      failures.push(`${surface.name} does not say descriptions go when a person asks about their data (query.summarize sends model_fields)`);
  }

  // The README's own opening (D7): it may not say Semanticus runs no AI inference while its Free and Pro section says Jev
  // makes choices on Pro.
  for (const rel of ['README.md', 'Semanticus.VSCode/README.md']) {
    const t = read(rel);
    if (/\bJev\b/.test(t) && /Semanticus runs no AI inference/i.test(t))
      failures.push(`${rel} says "Semanticus runs no AI inference" and, further down, that Jev makes modelling choices on Pro`);
  }
  return { failures, waiting, made, notMade: CLAIMS.filter((c) => !made.has(c.id)).map((c) => c.id), surfaces: all.length };
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const r = check({ strict: true });
  console.log(`release-claims: ${CLAIMS.length} claims listed, ${r.made.size} made by the words on ${r.surfaces} surfaces`
    + (r.notMade.length ? `; not made anywhere: ${r.notMade.join(', ')}` : ''));
  for (const f of r.failures) console.log('NO CODE BEHIND IT: ' + f);
  if (r.failures.length) { console.log(`release-claims: FAIL, ${r.failures.length} claim(s) without code`); process.exit(1); }
  console.log('release-claims: every claim the words make has its code in this tree');
}
