import assert from 'node:assert/strict';
import { existsSync, readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

// The Free and Pro line by feature (Kane, 2026-09-15). Pro is four whole features, not a list of big
// buttons: Model > Create, Checks > Tests and Saved reports, Changes > Published > Advanced, and Workflows.
// Everything else is free, bulk applies included.
//
// These are SOURCE contracts on the page half. They prove the boundary exists, that every entry path goes
// through it, that a preview never calls a gated read, and that the old bulk-is-Pro copy is gone. They do
// NOT prove the engine refuses anything: that is the cp/pro-engine lane's half, and nothing here should be
// read as evidence of it. The harness fakes the engine, so what the drivers show is the page's own behaviour
// against a mocked contract.

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const read = (file) => readFileSync(resolve(root, file), 'utf8');
const repo = (file) => readFileSync(resolve(root, '..', file), 'utf8');

const features = read('webview/src/features.ts');
const pro = read('webview/src/pro.tsx');
const app = read('webview/src/App.tsx');
const preview = read('webview/src/propreview.tsx');
const modelhome = read('webview/src/modelhome.tsx');
const hub = read('webview/src/connectionshub.tsx');
const deploy = read('webview/src/deploy.tsx');
const harness = read('tools/uishot/harness.html');

// ── 1. One shared feature vocabulary, four ids, and the tool map ────────────────────────────────────
assert.match(features, /export type ProFeature = 'modelCreate' \| 'tests' \| 'publishedAdvanced' \| 'workflows'/,
  'the four feature ids are the contract both doors share; they must be spelled once');
assert.match(features, /export const PRO_FEATURES: ProFeature\[\] = \['modelCreate', 'tests', 'publishedAdvanced', 'workflows'\]/,
  'the ordered list is what an entitlement with no features claim falls back to');
for (const [tool, feature] of [
  ['spec', 'modelCreate'], ['advmodels', 'modelCreate'], ['mcode', 'modelCreate'], ['docs', 'modelCreate'],
  ['knowledge', 'modelCreate'], ['tests', 'tests'], ['evidence', 'tests'], ['dataagent', 'publishedAdvanced'],
  ['workflows', 'workflows'],
]) {
  assert.match(features, new RegExp(`${tool}: '${feature}'`), `${tool} must be filed under ${feature}`);
}
// Published itself stays free. Only the Advanced mode inside it is gated, and that gate lives in deploy.tsx.
assert.doesNotMatch(features, /\bdeploy: '/, 'the Published tab must stay free; only its Advanced mode is gated');
assert.doesNotMatch(features, /\b(bpa|readiness|optimize|history|lineage|permissions|compare): '/,
  'Model quality, AI understanding, Proposed, History, Lineage, permissions and compare are free');
assert.match(features, /export function featureOfTool\(tool: string\): ProFeature \| null/,
  'one function answers "is this tool gated", so no caller invents its own map');

// ── 2. useFeature, on the one shared entitlement fetch ──────────────────────────────────────────────
assert.match(pro, /export type FeatureGrant = 'unknown' \| 'granted' \| 'denied'/,
  'the grant is three-valued: before the entitlement answers, the page knows nothing');
assert.match(pro, /export function useFeature\(feature: ProFeature \| null\): FeatureGrant/,
  'useFeature is the page half of the gate');
assert.match(pro, /rpc<\{ tier\?: string; features\?: string\[\] \}>\('getEntitlement'\)/,
  'the one fetch now reads the features array the engine adds');
// A token minted with no features claim must still grant all four (mint.mjs:39), so an older engine that
// answers with a tier and no features does not silently demote a paying customer to a new empty tier.
assert.match(pro, /Array\.isArray\(value\?\.features\)[\s\S]{0,120}isPaid\(tier\) \? PRO_FEATURES : \[\]/,
  'an entitlement with no features array must fall back to all four for a paid tier, none for free');
assert.match(pro, /export function useTier\(\): string/, 'useTier stays, because the licence button still reads the tier');
// A reconnect must INVALIDATE before it re-asks. Dropping the promise alone left the old grant published,
// so between a licence lapsing and the new answer landing a Pro page stayed mounted and kept calling
// (Astra: Workflows called listWorkflows, getWorkflowEnforcement, getWorkflowPolicy, listWorkflowProfiles).
assert.match(pro, /generation\+\+;\s*\n\s*tierPromise = null;\s*\n\s*publish\(\{ tier: 'unknown', features: \[\] \}\);/,
  'a reconnect must publish unknown AT ONCE, before the new answer is even asked for');
assert.match(pro, /mine === generation \? publish\(readEntitlement\(value\)\) : entitlement/,
  'and an answer from the fetch a reconnect replaced must be discarded, never published');
assert.match(pro, /export const PENDING_ACCESS = 'Checking your Pro access…';/,
  'one pending sentence, spelled once, because three surfaces show it');
for (const [file, source] of [['App.tsx', app], ['modelhome.tsx', modelhome], ['deploy.tsx', deploy]]) {
  assert.match(source, /PENDING_ACCESS/, `${file} must use the shared pending sentence, not invent its own`);
}
// The stable phrase the engine's refusal carries. Three layers match on it; it may never move.
assert.match(pro, /Semanticus Pro feature/, 'isEntitlementError must keep matching the engine phrase byte for byte');

// ── 3. One render boundary, and every entry path reaches it ─────────────────────────────────────────
assert.match(app, /import \{ ProPreview \} from '\.\/propreview'/, 'App renders the preview, not each tab');
assert.match(app, /const gatedFeature = featureOfTool\(tab\);/,
  'the boundary keys on the OPEN TOOL, so a restored route, a deep link and a keyboard cycle all reach it');
assert.match(app, /const featureGrant = useFeature\(gatedFeature\);/, 'one grant for the open tool');
assert.match(app, /const testsGrant = useFeature\('tests'\);/,
  'the hidden TestsView mount needs its own grant, because it is not the open tool');
// The trap: TestsView mounts for every open model and is merely hidden with a CSS class. A gate on the
// visible branch alone misses its background listTests / listTableMappings / listTestRuns calls.
assert.match(app, /\{session\?\.sessionId && testsGrant === 'granted' && <div className=\{tab === 'tests' \? 'h-full' : 'hidden'\}>/,
  'the persistent TestsView must not mount at all on free, so its effects never run');
assert.match(app, /featureGrant === 'denied' \? \(\s*<ProPreview tool=\{tab\}/,
  'a denied tool renders the preview instead of the real page');
assert.match(app, /featureGrant === 'unknown' \? \(\s*<FeatureChecking/,
  'before the entitlement answers, neither the page nor the preview is the honest answer');
// The boundary sits ABOVE the tab switch, so no tab body can be reached around it.
const boundaryAt = app.indexOf("featureGrant === 'denied'");
const firstTabAt = app.indexOf("tab === 'modelhome' ? (");
assert.ok(boundaryAt > 0 && firstTabAt > boundaryAt,
  'the feature boundary must come BEFORE the first tab branch, or a tab body is reachable around it');

// The navigation row keeps the tab in its place and marks it, but the pill is not the gate.
assert.match(app, /const lockedTools = useLockedTools\(\);/, 'the shell marks the locked tools once');
assert.match(app, /<ProBadge show=\{locked\.has\(tool\.id\)\}/, 'a locked tab keeps its place and wears the pill');
assert.match(app, /data-tool=\{tool\.id\}/, 'Find a tool rows carry a stable id, because a pill breaks a text matcher');
assert.match(app, /data-tab=\{/, 'the tab buttons carry a stable id, for the same reason');

// ── 4. The previews: real-looking, soft, and never a gated read ─────────────────────────────────────
for (const tool of ['spec', 'advmodels', 'mcode', 'docs', 'knowledge', 'tests', 'evidence', 'workflows', 'dataagent', 'deploy-advanced']) {
  assert.match(preview, new RegExp(`'${tool}':`), `${tool} needs its own preview, not a shared blank page`);
}
assert.doesNotMatch(preview, /\brpc\s*\(|\brpc</, 'a preview must never call the engine, gated or not');
assert.doesNotMatch(preview, /useEffect/, 'a preview has no effects, so it cannot load a paid read by accident');
assert.match(preview, /data-testid="pro-preview"/, 'the drivers select the preview by a stable id');
assert.match(preview, /data-pro-preview=\{tool\}/, 'each preview says which tool it is standing in for');
assert.match(preview, /data-testid="pro-preview-unlock"/, 'one Unlock Pro action per preview');
assert.match(preview, /Unlock Pro/, 'the action says what it does in plain words');
assert.match(preview, /Example\. This is not your model\./,
  'example data must be labelled as an example, or it reads as a measurement of the open model');
assert.match(preview, /aria-disabled="true"/, 'every control in a preview is soft, not live');
assert.match(preview, /pointerEvents: 'none' as const/, 'a soft control must not be clickable either');
// DIM THE CONTROLS, NOT THE CONTENT. Round one wrapped the whole example block in `aria-hidden` at 0.62
// opacity, which hid the entire preview from a screen reader and greyed the very thing it exists to show.
// The ATTRIBUTE, not the word: the file's own comments explain why it is absent, and a comment is not markup.
assert.doesNotMatch(preview, /aria-hidden=/,
  'a preview is content, not decoration: nothing in it may be hidden from assistive tech');
assert.match(preview, /const SOFT = \{ pointerEvents: 'none' as const, opacity: 0\.5 \};/,
  'softness belongs to the control kit, so it cannot be applied to a block of content by accident');

// A REPLICA, not a summary. Round one rendered every tool as the same short list of rows, which showed that
// a page exists and nothing about what it does. Each preview must carry its own real layout, the real
// control names in their real order, and one complete example outcome. These are the minimum contents
// Astra listed, one row per preview, checked as the literal text a person would read on the real page.
const PREVIEW_MUST_SHOW = {
  spec: ['Build into model →', 'Autogenerate from model', 'SUM ( Sales[SalesAmount] )', 'many to one', 'Build result'],
  advmodels: ['Perspectives', 'Field parameters', 'Calc groups', 'RLS / OLS', 'DaxLib', 'SELECTEDMEASURE', 'precedence 10'],
  mcode: ['M editor', 'Applied steps (5)', 'Sql.Database', 'Table.RemoveColumns', 'Save M', 'Preview'],
  docs: ['Include', 'Markdown', 'Print / PDF', 'Export…', 'Semantic Model Documentation', 'Net sales after returns'],
  knowledge: ['Business context', 'Gotchas', 'Insights (3)', 'fiscal year starts on 1 July', 'Where it came from'],
  tests: ['Run all enabled checks', 'New check', 'Expected · Actual', 'Last run', '11,532,660.44', 'differs',
    'Relationships', 'Table row counts'],
  evidence: ['Tests run ·', 'Workflow run ·', '12 of 15 checked', 'What this report says was checked', 'could not check'],
  workflows: ['Home', 'Library', 'Runs', 'Governance', 'Author', 'What you end up with', 'Required check',
    'waiting for you'],
  dataagent: ['Sign in as', 'Workspace', 'published', 'draft', 'Instructions', 'Example questions', 'Publish the agent'],
  'deploy-advanced': ['Source Control', 'Fabric Git', 'Automated delivery', 'Data Agent', 'Readiness gate',
    'What would be committed'],
};
for (const [tool, must] of Object.entries(PREVIEW_MUST_SHOW)) {
  const at = preview.indexOf(`'${tool}': {`);
  assert.ok(at > 0, `${tool} needs its own preview, not a shared blank page`);
  // The body runs to the next preview key, so each list is checked against that preview alone.
  const nextAt = Object.keys(PREVIEW_MUST_SHOW).map((k) => preview.indexOf(`'${k}': {`))
    .filter((i) => i > at).sort((x, y) => x - y)[0] ?? preview.length;
  const body = preview.slice(at, nextAt);
  for (const needle of must) {
    assert.ok(body.includes(needle), `the ${tool} preview must show "${needle}", or it is a summary not a replica`);
  }
}
// The replicas are built from a shared presentational kit, so ten pages cannot drift into ten dialects.
for (const part of ['function Table(', 'function Rail(', 'function Panel(', 'function Card(', 'function Code(', 'function Ctl(']) {
  assert.ok(preview.includes(part), `the replica kit needs ${part}, so every preview draws the same way`);
}
// Contoso only. A real tenant name in a shipped preview is a leak.
assert.match(preview, /Contoso/, 'the example data uses the placeholder model');
// tests.tsx defaults persist=false, so a run is not written down unless you ask. Neither surface may promise
// otherwise (Astra: propreview and the Overview Checks card both did).
for (const [name, source] of [['propreview.tsx', preview], ['modelhome.tsx', modelhome]]) {
  assert.doesNotMatch(source, /keeps a record of every run/, `${name} must not promise a record the product does not keep`);
  assert.match(source, /lets you save the results/, `${name} must say what actually happens`);
}

// ── 5. Overview: the Checks card, and the rule that "not denied" is not "granted" ───────────────────
// The behaviour is proven by cp-pro-drive.mjs, which holds getEntitlement in flight and FAILS the run if a
// paid call escapes. What these lines guard is the SHAPE that made that behaviour possible to get wrong:
// a denied-only comparison. Round one shipped `=== 'denied'` here and in deploy, so an entitlement that had
// not answered read as an entitlement that granted, and Overview called listTestRuns five times while it
// was still in flight (Astra, 2026-09-15). The comparison must be positive, everywhere.
assert.match(modelhome, /const testsGrant = useFeature\('tests'\);/,
  'Overview must hold the whole three-valued grant, not a boolean collapsed from it');
assert.match(modelhome, /useRemote<ChecksHistory>\(\(\) => rpc<ChecksHistory>\('listTestRuns', 1\), keys, testsGrant !== 'granted'\)/,
  'listTestRuns must be suppressed unless the plan is KNOWN to grant Tests');
assert.match(modelhome, /const paid = grant === 'granted';/, 'the card keys its paid branches on granted');
assert.match(modelhome, /const pending = grant === 'unknown';/, 'and has a real state for a plan that has not answered');
assert.match(modelhome, /if \(!paid\) return;/, 'and the run handler itself refuses to outrun the plan');
assert.match(modelhome, /disabled=\{pending \|\| \(paid && running\)\}/, 'the Run control is dead while the plan is unknown');
assert.match(modelhome, /skip\?: boolean/, 'useRemote needs a way to not run at all');
assert.match(modelhome, /if \(skip\) \{ setState\(\{ status: 'off' \}\); return; \}/,
  'a skipped read sets its own state rather than sitting on a spinner forever');
assert.match(modelhome, /locked \? <ProBadge show \/> : null/, 'the Checks card wears the pill on free, and only on free');
assert.match(modelhome, /<div className="overview-v">Tests is a Pro feature\.<\/div>/,
  'the card names the feature in its headline slot, not a paragraph in a headline');
assert.match(modelhome, /Model quality and AI understanding still check your model for free\./,
  'and says in its detail what is still free');
assert.match(modelhome, /locked \? 'See Tests' :/, 'Run tests becomes a way into the preview on free');
assert.match(modelhome, /onClick=\{\(\) => \{ if \(locked\) onOpen\(\); else if \(paid\) void run\(\); \}\}/,
  'a press may only run on a GRANTED plan; free opens the preview and unknown does nothing at all');

// ── 6. The two free homes ───────────────────────────────────────────────────────────────────────────
// Primer suggestions lost their home when Model notes became Pro. They are free, so they move to the
// Overview AI card beside the instructions editor.
assert.match(modelhome, /rpc<PrimerSuggestionList>\('listPrimerSuggestions'\)/, 'suggestions are read on the free card');
assert.match(modelhome, /acceptPrimerSuggestion/, 'Accept must exist on a free surface');
assert.match(modelhome, /rejectPrimerSuggestion/, 'Reject must exist on a free surface');
assert.match(modelhome, /data-testid="overview-suggestion"/, 'the driver selects a suggestion by a stable id');
assert.match(modelhome, /data-testid="overview-suggestion-accept"/, 'and its Accept');
assert.match(modelhome, /data-testid="overview-suggestion-reject"/, 'and its Reject');
// set_compatibility_level lost its only control to Pro Advanced Modelling. It is free, so the model card
// carries it, and only when the level is actually blocking something.
// Tied to a NAMED blocked capability, not to the number being low. Round one treated every level under
// 1701 as an active blocker and told the person date calculations needed it, which is false: advmodels.tsx
// offers the classic date-table approach below 1701 and it works. Calendars are the one thing refused.
assert.match(modelhome, /const LEVEL_BLOCKS: \{ capability: string; floor: number \}\[\]/,
  'what a level blocks is a list of named capabilities, not a bare constant');
assert.match(modelhome, /\{ capability: 'Calendars', floor: 1701 \}/, 'and Calendars is the capability 1701 unblocks');
assert.match(modelhome, /return LEVEL_BLOCKS\.find\(\(entry\) => level < entry\.floor\) \?\? null;/,
  'the prompt shows only when a named capability is actually below its floor');
assert.match(modelhome, /if \(typeof level !== 'number' \|\| level <= 0\) return null;/,
  'an unknown level blocks nothing, because "not known to be blocking" is not "blocking"');
assert.match(modelhome, /\{blocked\.capability\} need level \{blocked\.floor\}\./,
  'the sentence names the one capability that is refused, and claims nothing wider');
assert.doesNotMatch(modelhome, /Calendars and date calculations need/,
  'the old, too-broad claim must be gone: date calculations work below 1701');
assert.match(modelhome, /rpc\('setCompatibilityLevel', blocked\.floor\)/, 'the free control calls the free operation');
assert.match(modelhome, /<RaiseLevelAction key=\{sessionId\}/,
  'and its done state resets per model, because App renders ModelHome unkeyed');
assert.match(modelhome, /data-testid="overview-raise-level"/, 'the driver selects the action by a stable id');
assert.match(modelhome, /Raise compatibility level/, 'the action says what it does');

// ── 7. Connections reads source usage from one free projection ──────────────────────────────────────
assert.match(hub, /rpc<SqlSourceUsage\[\]>\('listSqlSourceUsage'\)/,
  'Connections must read the free projection, at the bare-array shape the engine door returns');
assert.doesNotMatch(hub, /rpc<\{ sqlSourceUse\?: SqlSourceUse\[\] \}>\('listTests'\)/, 'listTests is Pro now');
assert.doesNotMatch(hub, /rpc<\{ rows\?: TableSourceMappingRow\[\] \}>\('listTableMappings'\)/, 'listTableMappings is Pro now');
assert.match(hub, /interface SqlSourceUsage \{ id: string; name: string; checksUsing: number; checkTitles: string\[\]; tableMappingsUsing: number; mappedTables: string\[\]; \}/,
  'the projection carries usage only: no definitions, no expected values, no verdicts, no row counts');
// A successful but PARTIAL answer is not zero use. Zero is the sentence a person deletes a source on.
assert.match(hub, /if \(!row\) return null;/, 'a source the projection did not mention is not a source nothing uses');
assert.match(hub, /return typeof value === 'number' \? value : null;/, 'and a count it did not carry is not zero either');
assert.match(hub, /What uses this source was not counted\./, 'an uncounted source says so');
// Scoped to the USAGE path deliberately. The delete-refusal block further down keeps its `?? 0`, and that
// is correct: a refusal only happens because something IS in use, and the engine sends the counts with it.
const usageBlock = hub.slice(hub.indexOf('const loadSqlUse'), hub.indexOf('const openSqlForm'));
assert.doesNotMatch(usageBlock, /\?\? 0/, 'no usage count may fall back to zero');

// ── 8. Only Advanced is gated inside Published ──────────────────────────────────────────────────────
assert.match(deploy, /const advancedGrant = useFeature\('publishedAdvanced'\);/,
  'deploy needs the whole grant for its Advanced mode, not a boolean collapsed from it');
assert.match(deploy, /const advancedOpen = advancedGrant === 'granted';/,
  'the live Advanced body must key on GRANTED; round one keyed on "not denied" and mounted it while unknown');
assert.match(deploy, /mode === 'advanced' && advancedLocked &&[\s\S]{0,120}?<ProPreview tool="deploy-advanced" embedded \/>/,
  'a locked Advanced renders the preview IN PLACE, and embedded so Published keeps its own page padding');
assert.match(deploy, /mode === 'advanced' && advancedOpen &&/, 'and the real Advanced panels only when granted');
assert.match(deploy, /mode === 'advanced' && advancedPending &&/, 'with a third state for a plan that has not answered');
assert.doesNotMatch(deploy, /!advancedLocked/,
  'no branch on this page may treat "not denied" as permission');
assert.match(deploy, /<ProBadge show=\{advancedLocked\} \/>/, 'the Advanced button keeps its place and wears the pill');
// Publish, Promote, compare and restore points stay free, so their branches must not mention the grant.
for (const free of ["mode === 'push'", "mode === 'rollback'", "mode === 'promote'"]) {
  const line = deploy.split('\n').find((l) => l.includes(free) && l.includes('Locked'));
  assert.equal(line, undefined, `${free} must stay free`);
}

// ── 9. The old bulk-is-Pro copy and pills are gone ──────────────────────────────────────────────────
const removals = [
  ['webview/src/App.tsx', /Pro applies all \$\{card\?\.safeFixCount/, 'apply all safe fixes is free now'],
  ['webview/src/App.tsx', /<ProBadge show=\{tier === 'free'\} variant="onAccent" \/>/, 'the bulk-apply pill goes'],
  ['webview/src/bpa.tsx', /Pro fixes all/, 'fix all is free now'],
  ['webview/src/bpa.tsx', /<ProBadge show=\{tier === 'free'\}/, 'the fix-all pill goes'],
  ['webview/src/compare.tsx', /tier === 'free' && selected\.size > 1/, 'a multi-item apply is free now'],
  ['webview/src/compare.tsx', /<ProBadge show=\{tier === 'free' && submitCount > 1\}/, 'the compare pill goes'],
  ['webview/src/optimize.tsx', /Pro applies the whole approved set/, 'applying the approved set is free now'],
  ['webview/src/optimize.tsx', /<ProBadge show=\{tier === 'free'/, 'both optimize pills go'],
  ['webview/src/history.tsx', /Pro packages the trail as a shareable report/, 'exporting the trail is free now'],
  ['webview/src/history.tsx', /<ProBadge show=\{tier === 'free'\}/, 'both export pills go'],
  ['webview/src/permissions.tsx', /<ProBadge show=\{tier === 'free' && !active\}/, 'the preset pill goes'],
  ['webview/src/permissions.tsx', /Configuring the matrix is a Pro feature/, 'the policy matrix is free now'],
  ['webview/src/interview.tsx', /saving questions is part of Pro/, 'saving a question is free now'],
  ['webview/src/interview.tsx', /part of Pro/, 'and its tooltip'],
  ['webview/src/sparkline.tsx', /See what moved a number, automatically, with Pro/, 'blame is free now'],
  ['webview/src/tests-authoring.tsx', /Saving a check needs Semanticus Pro/, 'Tests is Pro as a whole; the per-action line goes'],
  ['webview/src/tests.tsx', /Recording needs Semanticus Pro/, 'same'],
];
for (const [file, pattern, why] of removals) {
  assert.doesNotMatch(read(file), pattern, `${file}: ${why}`);
}
// The workflows copy stays, but it names the feature rather than the old run-with-checks split.
const help = read('webview/src/help.tsx');
assert.doesNotMatch(help, /Applying a batch in one step is Pro/, 'help: bulk apply is free now');
assert.doesNotMatch(help, /Bulk fixes and accepting a rule for the whole model are Pro/, 'help: bulk fixes are free now');
assert.doesNotMatch(help, /Exporting the audit trail is Pro/, 'help: exporting the trail is free now');
assert.doesNotMatch(help, /Free to read, Pro to run with the checks required/, 'help: the workflow library is Pro now');
assert.match(help, /"pro": "Tests is a Semanticus Pro feature\./, 'help: Tests names the whole feature');
assert.match(help, /"pro": "Workflows is a Semanticus Pro feature\./, 'help: Workflows names the whole feature');
assert.match(help, /"pro": "Create in Model is a Semanticus Pro feature\./, 'help: the five Create tools name the feature');
assert.match(help, /"pro": "Advanced publishing is a Semanticus Pro feature\./, 'help: Published names what is gated inside it');

// ── 10. Copy outside the webview ────────────────────────────────────────────────────────────────────
const rootReadme = repo('README.md');
const extReadme = read('README.md');
const tierBlock = /Semanticus Pro unlocks four whole features[\s\S]{0,480}?compare and restore points\./;
assert.match(rootReadme, tierBlock, 'the root README states the four features');
assert.match(extReadme, tierBlock, 'the extension README states the same four features');
assert.equal(
  tierBlock.exec(rootReadme)[0], tierBlock.exec(extReadme)[0],
  'the two READMEs had already drifted; they must now say the same words');
assert.doesNotMatch(rootReadme, /Pro unlocks the one-click bulk/i, 'the bulk sentence is wrong now');
assert.match(repo('CHANGELOG.md'), /Free and Pro are now set by feature/, 'the change is in the ship log');
// The public mirror drops docs/ apart from a short keep list (tools/release/mirror-manifest.json), so these four
// are asserted only where they exist. In the private repo they always exist, so the guard stays strict there;
// the 1.2.0 mirror run failed on docs/PLAN.md before this guard.
for (const [file, why] of [
  ['docs/DOC-MAP.md', 'the doc map marks its obsolete claims'],
  ['docs/PLAN.md', 'the plan marks its obsolete claims, and keeps the history'],
  ['docs/feature-coverage-matrix.md', 'and so does the coverage matrix'],
  ['docs/subscription-fulfillment.md', 'and the fulfilment copy'],
]) {
  if (!existsSync(resolve(root, '..', file))) continue;
  assert.match(repo(file), /Superseded 2026-09-15/, why);
}

// ── 11. The harness fakes the engine at the SAME contract ───────────────────────────────────────────
assert.match(harness, /\{ tier: entitlementTier, features: entitlementFeatures \}/,
  'the tier-only fixture becomes an effective feature grant');
assert.match(harness, /\?\s*\{ __uishotError: 'The engine could not read your licence\.' \}/,
  'and ?ent=fail makes the read fail, because a failed entitlement is not a grant either');
assert.match(harness, /Unlock Pro from Pro Plans and Support\./,
  'the mock refusal must be the engine\'s real sentence, not an older paraphrase');
assert.doesNotMatch(harness, /Or upgrade to Semanticus Pro\./,
  'the old paraphrase never matched EntitlementGuard and must go');
for (const op of ['listTests', 'listTableMappings', 'runTests', 'listTestRuns', 'getSpec', 'getPartitionM', 'listWorkflows', 'gitLog']) {
  assert.match(harness, new RegExp(`'${op}'`), `the free mock must refuse ${op}, not answer it`);
}
assert.match(harness, /RESPONSES\.listSqlSourceUsage = cfg\.listSqlSourceUsage \|\| SQL_SOURCES\.map/,
  'the new free projection is mocked at the bare-array shape the engine door returns');
assert.match(harness, /id: rec\.id, name: rec\.name, checksUsing: titles\.length, checkTitles: titles,\s*\n\s*tableMappingsUsing: tables\.length, mappedTables: tables/,
  'and at the exact field names Connections reads');
// The deep link. The harness selected tabs by hash and by EXACT button text, which a Pro pill breaks: a
// locked Tests segment reads "TestsPro" and nothing matching on words can find it again.
assert.match(harness, /new URLSearchParams\(location\.search\)\.get\('tab'\)/,
  'a ?tab= deep link must exist, not be assumed');
assert.match(harness, /type: 'navigate', tab: want/,
  'the deep link sends the host navigate message, which is what a real deep link becomes; it never matches on text');
assert.match(harness, /if \(\+\+sent >= 6\)/,
  'and it re-sends past the route reset Studio does when the session id lands, or the link is thrown away');
// The tab row is a DIFFERENT entry path, driven by the stable data-tab id in cp-pro-drive.mjs.
const drive = read('tools/uishot/cp-pro-drive.mjs');
assert.match(drive, /document\.querySelector\(`\[data-tab="\$\{tool\}"\]`\)/,
  'the row entry path clicks a segment by its stable id, never by its label');
assert.match(drive, /\[role="menu"\]\[aria-label="Find a tool"\] \[data-tool="\$\{tool\}"\]/,
  'and Find a tool picks its row by id too');
// The whole point of the free shots: a locked page must make no call at all.
assert.match(drive, /window\.__uishotHarness\?\.messages \|\| \[\]/,
  'the driver records the real call trace from the harness, not a guess');
// And JUDGES it. Round one recorded every result and exited 0 regardless, so a caught FATAL and a paid call
// on the free plan both left the gate green.
assert.match(drive, /process\.exitCode = 1;/, 'a failed scene must fail the process, or the driver is a screenshot tool');
assert.match(drive, /called \$\{madeAnyway\.join\(', '\)\} on a plan that does not grant it/,
  'a paid call on a plan that does not grant it must be a failure, not a note');
assert.match(drive, /mid-reconnect, before the new answer landed, called/,
  'and so must a paid call made in the window between a reconnect and its answer');
assert.match(drive, /failures: \['FATAL: ' \+ e\.message\]/,
  'a caught exception must be a failure too, not a note beside a green run');
// The round-one "restored" case injected an unused state key and then navigated, proving a second host
// navigation. App.tsx keeps no persisted route at all, so the real restore path is the host's cold hand-off.
assert.doesNotMatch(drive, /studio\.route/, 'the fake restored-route seed is gone');
assert.match(drive, /coldNav=tests:measure:Sales\/Total Sales&sessionDelay=1200/,
  'the restore case must drive the real cold hand-off, and land it before the session does');
assert.match(drive, /seedKept/, 'and must prove the seed survived into the page it was handed to');
assert.doesNotMatch(app, /loadState<Route>|loadState\('studio\.route'/,
  'if a persisted route is ever added, the restore scenes above have to change with it');
// The three paid writes Astra found missing from the harness's wrapped set.
for (const op of ['createFunction', 'defineCalendar', 'generateTimeIntelligence']) {
  assert.match(harness, new RegExp(`'${op}'`), `the free mock must refuse ${op}`);
  assert.match(drive, new RegExp(`'${op}'`), `and the driver must judge ${op} as paid`);
}
// The hold/release seam the pending scenes need.
assert.match(harness, /HELD_METHODS\.has\(msg\.method\)/, 'the harness must be able to park a call in flight');
assert.match(harness, /release: function \(method\)/, 'and answer it later with the fixture it holds by then');

console.log('pro feature line: page contracts passed');
