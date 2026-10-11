import assert from 'node:assert/strict';
import { existsSync, mkdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

// The Free and Pro line by feature (Kane, 2026-09-15, narrowed by him on 2026-09-22). Pro was four whole
// features; it is ONE. Free is the tool, Pro is the tool that works for you: everything a person can do by
// hand or with their own assistant is free, Model Spec, Advanced modelling, Power Query, Docs, Model notes,
// Validation, Reports and the Advanced mode inside Published included. Workflows is the whole paid tab; the
// plan being ranked, scored and run, and the ask box, are paid by TIER and are checked in plan-page.
//
// These are SOURCE contracts on the page half. They prove the boundary exists, that every entry path goes
// through it, that a preview never calls a gated read, and that the old bulk-is-Pro copy is gone. They do
// NOT prove the engine refuses anything: that is the cp/pro-engine lane's half, and nothing here should be
// read as evidence of it. The harness fakes the engine, so what the drivers show is the page's own behaviour
// against a mocked contract.

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
// ONE read path, and it normalises line endings. CI's Windows leg checks these files out CRLF and the
// Linux leg does not, so a contract anchored on a newline held on one and failed on the other (PR 387,
// section 12 below reproduces it). Nothing in this file is a contract about how lines END: every one of
// them is about what a source file says. So the \r goes at the read, once, rather than each pattern
// having to remember it. Section 12 proves this by reading a real CRLF file back through here.
const readAt = (absolute) => readFileSync(absolute, 'utf8').replace(/\r\n/g, '\n');
const read = (file) => readAt(resolve(root, file));
const repo = (file) => readAt(resolve(root, '..', file));

const features = read('webview/src/features.ts');
const pro = read('webview/src/pro.tsx');
const app = read('webview/src/App.tsx');
const preview = read('webview/src/propreview.tsx');
// The Overview (modelhome.tsx) retired in slice 11, lane R1. Its free controls moved to the Plan (contract 11.1).
const planPage = read('webview/src/plan.tsx');
const hub = read('webview/src/connectionshub.tsx');
const deploy = read('webview/src/deploy.tsx');
const harness = read('tools/uishot/harness.html');

// ── 1. One shared feature vocabulary, one id, and the tool map ──────────────────────────────────────
assert.match(features, /export type ProFeature = 'workflows' \| 'askData';/,
  'the feature id is the contract both doors share; it must be spelled once');
assert.match(features, /export const PRO_FEATURES: ProFeature\[\] = \['workflows', 'askData'\];/,
  'the ordered list is what an entitlement with no features claim falls back to');
assert.match(features, /workflows: 'workflows',/, 'workflows must be filed under workflows');
// EXACTLY one tool is gated. Written as the whole map rather than as absences, because an absence check
// passes just as well when the map is empty or when somebody adds a tool nobody thought to name here.
const toolMap = (features.match(/FEATURE_OF_TOOL: Record<string, ProFeature> = \{[\s\S]*?\n\};/) || [''])[0];
// Two since 26 September: Ask your data is a whole Pro page under Explore (wire id askData, ProFeature.cs).
assert.deepEqual([...toolMap.matchAll(/(\w+): '(\w+)',/g)].map((m) => [m[1], m[2]]), [['workflows', 'workflows'], ['askdata', 'askData']],
  'Workflows and Ask your data are the only gated tools: everything else renders its real page on every plan');
// The eight tools Kane freed. Each was gated on 21 September and must be gated by nothing on 22 September.
for (const freed of ['spec', 'advmodels', 'mcode', 'docs', 'knowledge', 'tests', 'evidence', 'dataagent', 'deploy']) {
  assert.doesNotMatch(toolMap, new RegExp(`\\b${freed}: '`), `${freed} is free now and may not be in the gate map`);
}
// A caller in a lane that does not own its file may still ASK about a retired feature. It gets 'granted',
// which is the honest answer, rather than a compile error or a silent 'denied' that would hide a free page.
assert.match(features, /export type RetiredProFeature = 'modelCreate' \| 'tests' \| 'publishedAdvanced';/,
  'the three retired features are named, so each one is a deletion waiting to happen and not a mystery');
assert.match(features, /export function isRetiredFeature\(feature: string\): feature is RetiredProFeature/,
  'and one function answers whether a name is one of them');
assert.match(features, /export function featureOfTool\(tool: string\): ProFeature \| null/,
  'one function answers "is this tool gated", so no caller invents its own map');

// ── 2. useFeature, on the one shared entitlement fetch ──────────────────────────────────────────────
assert.match(pro, /export type FeatureGrant = 'unknown' \| 'granted' \| 'denied'/,
  'the grant is three-valued: before the entitlement answers, the page knows nothing');
assert.match(pro, /export function useFeature\(feature: ProFeature \| RetiredProFeature \| null\): FeatureGrant/,
  'useFeature is the page half of the gate, and still answers about a retired feature');
assert.match(pro, /if \(isRetiredFeature\(feature\)\) return 'granted';/,
  'a freed feature is granted BEFORE the entitlement answers, not held on "checking" for a question the answer no longer depends on');
assert.match(pro, /rpc<\{ tier\?: string; features\?: string\[\] \}>\('getEntitlement'\)/,
  'the one fetch now reads the features array the engine adds');
// A token minted with no features claim must still grant the whole list (mint.mjs:39), so an older engine
// that answers with a tier and no features does not silently demote a paying customer to an empty tier.
assert.match(pro, /Array\.isArray\(value\?\.features\)[\s\S]{0,120}isPaid\(tier\) \? PRO_FEATURES : \[\]/,
  'an entitlement with no features array must fall back to the whole list for a paid tier, none for free');
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
// One surface shows it now, not three: the Overview Checks card and the Advanced mode inside Published both
// lost their gate on 22 September, and with it the state of waiting for a plan to answer about them.
assert.match(app, /PENDING_ACCESS/, 'App.tsx must use the shared pending sentence, not invent its own');
assert.doesNotMatch(deploy, /PENDING_ACCESS/, 'deploy.tsx waits for no entitlement now, so it must not say it is');
// The Plan waits on one where Run the plan would be: while the licence has not answered it shows neither the
// button nor the Pro line, only the shared pending sentence (the Overview's hero did the same until slice 11).
assert.match(planPage, /if \(grant !== 'granted'\) return <span className="plan-muted">\{PENDING_ACCESS\}<\/span>;/,
  'the Plan says it is waiting only where Run the plan would be');
// The stable phrase the engine's refusal carries. Three layers match on it; it may never move.
assert.match(pro, /Semanticus Pro feature/, 'isEntitlementError must keep matching the engine phrase byte for byte');

// ── 3. One render boundary, and every entry path reaches it ─────────────────────────────────────────
assert.match(app, /import \{ ProPreview \} from '\.\/propreview'/, 'App renders the preview, not each tab');
assert.match(app, /const gatedFeature = featureOfTool\(tab\);/,
  'the boundary keys on the OPEN TOOL, so a restored route, a deep link and a keyboard cycle all reach it');
assert.match(app, /const featureGrant = useFeature\(gatedFeature\);/, 'one grant for the open tool');
// The trap that WAS here: TestsView mounts for every open model and is merely hidden with a CSS class, so a
// gate on the visible branch alone missed its background listTests / listTableMappings / listTestRuns calls
// and it needed a second grant of its own. Validation is free, so there is nothing left to gate and the
// second grant is gone rather than left behind comparing against a constant.
assert.doesNotMatch(app, /testsGrant/, 'the second grant went with the gate it protected');
assert.match(app, /\{session\?\.sessionId && <div className=\{tab === 'tests' \|\| tab === 'evidence' \? 'h-full' : 'hidden'\}>/,
  'the persistent Validation mount is shown for both of its tabs and gated by nothing');
assert.match(app, /featureGrant === 'denied' \? \(\s*<ProPreview tool=\{tab\}/,
  'a denied tool renders the preview instead of the real page');
assert.match(app, /featureGrant === 'unknown' \? \(\s*<FeatureChecking/,
  'before the entitlement answers, neither the page nor the preview is the honest answer');
// The boundary sits ABOVE the tab switch, so no tab body can be reached around it.
const boundaryAt = app.indexOf("featureGrant === 'denied'");
const firstTabAt = app.indexOf("tab === 'plan' || tab === 'optimize' || tab === 'spec' ? (");
assert.ok(boundaryAt > 0 && firstTabAt > boundaryAt,
  'the feature boundary must come BEFORE the first tab branch, or a tab body is reachable around it');

// The navigation row keeps the tab in its place and marks it, but the pill is not the gate.
assert.match(app, /const lockedTools = useLockedTools\(\);/, 'the shell marks the locked tools once');
assert.match(app, /<ProBadge show=\{locked\.has\(tool\.id\)\}/, 'a locked tab keeps its place and wears the pill');
assert.match(app, /data-tool=\{tool\.id\}/, 'Find a tool rows carry a stable id, because a pill breaks a text matcher');
assert.match(app, /data-tab=\{/, 'the tab buttons carry a stable id, for the same reason');

// ── 4. The previews: real-looking, soft, and never a gated read ─────────────────────────────────────
// ONE preview now. The other nine went with the gates they explained on 22 September 2026: a replica of a
// page a person can simply open is not a teaching aid, it is a second, worse copy of that page.
assert.match(preview, /'workflows':/, 'Workflows needs its own preview, not a shared blank page');
for (const freed of ['spec', 'advmodels', 'mcode', 'docs', 'knowledge', 'tests', 'evidence', 'dataagent', 'deploy-advanced']) {
  assert.doesNotMatch(preview, new RegExp(`'${freed}': \\{`), `${freed} is free: its locked-page replica must be gone, not merely unreachable`);
}
assert.doesNotMatch(preview, /publishedAdvanced/, 'and the in-page id that only the Published gate used goes with it');
assert.doesNotMatch(preview, /\brpc\s*\(|\brpc</, 'a preview must never call the engine, gated or not');
assert.doesNotMatch(preview, /useEffect/, 'a preview has no effects, so it cannot load a paid read by accident');
assert.match(preview, /data-testid="pro-preview"/, 'the drivers select the preview by a stable id');
assert.match(preview, /data-pro-preview=\{tool\}/, 'each preview says which tool it is standing in for');
assert.match(preview, /data-testid="pro-preview-unlock"/, 'one Unlock Pro action per preview');
assert.doesNotMatch(preview, /sem-card/, 'the preview is a line and an example, with no card inside a card (W2, A11)');
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
  // Workflows is one page (lane W2, contract 21.1 row 8): the list of your workflows, not the old five-section rail.
  workflows: ['Search your workflows', 'New workflow', 'Last run', 'Required for new measures', 'Run', 'Team rules',
    'You choose', 'Your assistant writes'],   // the closing "on the Plan" explainer left the preview (W3)
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
// tests.tsx defaults persist=false, so a run is not written down unless you ask. No surface may promise
// otherwise (Astra: propreview and the Overview Checks card both did). propreview no longer describes
// Validation at all, so the promise it must not make is now only an absence there.
assert.doesNotMatch(preview, /keeps a record of every run/, 'propreview.tsx must not promise a record the product does not keep');
// The sentence that USED to say what actually happens lived inside the Overview's locked Validation copy,
// and went with it. Nothing on the free card describes saving a run at all now, which is honest: the
// Validation page itself is one click away and says it there.

// ── 5. The Overview's Checks card retired with the page in slice 11 (lane R1) ────────────────────────
// Its job (run the checks, read the last result) is Review > Validation, which is free and ungated (above).
// useRemote itself keeps `skip`: it moved to hooks.ts with the rest of the shared read and other callers
// still suppress reads with it.
const hooks = read('webview/src/hooks.ts');
assert.match(hooks, /skip\?: boolean/, 'useRemote keeps a way to not run at all');
assert.match(hooks, /if \(skip\) \{ setState\(\{ status: 'off' \}\); return; \}/,
  'a skipped read sets its own state rather than sitting on a spinner forever');

// ── 5b. Model notes: a FREE page holding one paid section ───────────────────────────────────────────
// The shape no other page has, and the one the 22 September change created by accident. Model notes was
// itself Pro, so its Saved workflows section could call listWorkflows freely: whoever could open the page
// had already paid. Freeing the page left the call behind it, and a free session opening Model notes was
// answered with "Workflows is a Semanticus Pro feature" inside a page it is entitled to.
//
// The fix is not to free the call: the workflow library IS the paid feature and the engine is right to
// refuse it. It is to not ASK. So the section is keyed on the one wire feature there is, and on 'denied'
// it neither calls nor renders a list; it shows the quiet pill, and the rest of the page is untouched.
const knowledge = read('webview/src/knowledge.tsx');
// Minimal pass (8 Oct): the Saved workflows card left Notes (it repeated the Workflows tab, and was the page's only paid section).
// So the page asks nothing about Workflows at all, which is the strongest form of "do not ask a plan that is not granted".
assert.doesNotMatch(knowledge, /useFeature\('workflows'\)|listWorkflows/, 'Notes no longer reads the workflow library, so a Free session cannot be refused one');
assert.doesNotMatch(knowledge, /LearnedWorkflowsSection|knowledge-workflows-pro-pill/, 'and it carries no Pro pill of its own');
// The pill is shared, not a third copy of the same markup. plan.tsx and askbox.tsx each still carry
// their own; when they move to this one, delete their copies and this check keeps holding.
assert.match(pro, /export function ProPill\(/, 'the quiet pill lives in pro.tsx beside upgradeToPro');
// The id a caller hands it becomes the id the driver finds. Without this the check above only proves a
// prop was passed, not that anything on screen carries it.
assert.match(pro, /<div data-testid=\{testId\}/, 'and the id a caller gives it reaches the DOM');
assert.match(pro, /onClick=\{upgradeToPro\}/, 'and its action is the one Unlock Pro command');
// The rest of Model notes must NOT have grown a gate. Exactly one useFeature call in the file: a second
// would mean some other section of a free page had quietly been locked too.
assert.equal((knowledge.match(/useFeature\(/g) || []).length, 0,
  'no section of Model notes is gated; the notes, the lessons and the suggestions are free');

// ── 6. The two free homes ───────────────────────────────────────────────────────────────────────────
// Primer suggestions lost their home when Model notes became Pro. They are free, so they moved onto the AI
// instructions card beside the editor. That whole card moved again on 22 September, out of modelhome.tsx
// and into instructions.tsx, so that it can be both the Overview card and the Edit > AI instructions page;
// the suggestions travelled with it and the checks follow them to the file that now draws them.
const instructionsCard = read('webview/src/instructions.tsx');
// The Overview's tile that counted them retired in slice 11; the page reads and decides them.
assert.match(instructionsCard, /rpc<PrimerSuggestionList>\('listPrimerSuggestions'\)/, 'suggestions are read for the page');
// Minimal pass (8 Oct): a waiting note suggestion is decided on the Notes tab only; the Instructions tab shows a count on the Notes tab.
assert.match(knowledge, /acceptPrimerSuggestion/, 'Accept must exist on a free surface');
assert.match(knowledge, /rejectPrimerSuggestion/, 'Reject must exist on a free surface');
assert.doesNotMatch(instructionsCard, /acceptPrimerSuggestion/, 'the Instructions tab does not repeat the decision');
// set_compatibility_level lost its only control to Pro Advanced Modelling. It is free, so the Overview's model card
// carried it, and since slice 11 the Plan's facts line does (contract 11.1), only when the level is actually
// blocking something.
// Tied to a NAMED blocked capability, not to the number being low. Round one treated every level under
// 1701 as an active blocker and told the person date calculations needed it, which is false: advmodels.tsx
// offers the classic date-table approach below 1701 and it works. Calendars are the one thing refused.
assert.match(planPage, /const LEVEL_BLOCKS: \{ capability: string; floor: number \}\[\]/,
  'what a level blocks is a list of named capabilities, not a bare constant');
assert.match(planPage, /\{ capability: 'Calendars', floor: 1701 \}/, 'and Calendars is the capability 1701 unblocks');
assert.match(planPage, /return LEVEL_BLOCKS\.find\(\(entry\) => level < entry\.floor\) \?\? null;/,
  'the prompt shows only when a named capability is actually below its floor');
assert.match(planPage, /if \(typeof level !== 'number' \|\| level <= 0\) return null;/,
  'an unknown level blocks nothing, because "not known to be blocking" is not "blocking"');
assert.match(planPage, /\{blocked\.capability\} need level \{blocked\.floor\}\./,
  'the sentence names the one capability that is refused, and claims nothing wider');
assert.doesNotMatch(planPage, /Calendars and date calculations need/,
  'the old, too-broad claim must be gone: date calculations work below 1701');
assert.match(planPage, /rpc\('setCompatibilityLevel', blocked\.floor\)/, 'the free control calls the free operation');
assert.match(planPage, /<RaiseLevelAction key=\{sessionId\} level=\{compatibilityLevel\}/,
  'and its done state resets per model, because App renders the Plan unkeyed');
assert.match(app, /compatibilityLevel=\{session\.compatibilityLevel\}/, 'App hands the Plan the session\'s level');
assert.match(planPage, /data-testid="plan-raise-level"/, 'the driver selects the action by a stable id');
assert.match(planPage, /Raise compatibility level/, 'the action says what it does');

// ── 7. Connections reads source usage from free reads only ──────────────────────────────────────────
// Slice C (lane C2, 24 Sep): the SQL sources section left Connections; each model now has one SQL endpoint, and
// the saved sources no model is linked to come back from list_model_sql_endpoints with their use counts. The
// rule this section guards is unchanged: Connections never reads the Pro Tests feature to count use, and a count
// the engine did not carry is never shown or treated as zero.
const hubModel = read('webview/src/connectionshub-model.ts');
const hubParts = read('webview/src/connectionshub-parts.tsx');
assert.match(hub, /rpc<ModelEndpointList>\('listModelSqlEndpoints'\)/,
  'Connections reads what uses a source from the free endpoint listing');
assert.doesNotMatch(hub, /'listTests'/, 'listTests is Pro now');
assert.doesNotMatch(hub, /'listTableMappings'/, 'listTableMappings is Pro now');
assert.doesNotMatch(hub, /'listSqlSourceUsage'/, 'and nothing else is needed to count use');
// A count the engine did not carry is not zero, and neither is one that covers only the open model (F-265). Zero
// is the sentence a person deletes a source on.
assert.match(hubModel, /return s\.usageScope === USAGE_ALL_SAVED_MODELS && typeof s\.checksUsing === 'number' && typeof s\.tableMappingsUsing === 'number'/,
  'Remove opens only on counts the engine actually carried across every saved model');
assert.match(hubModel, /What uses it was not counted/, 'an uncounted source says so');
assert.match(hubParts, /notLinkedUses\(s\)/, 'the row says what was counted through the one rule');

// ── 8. Nothing inside Published is gated (and Advanced is gone) ────────────────────────────────────────────────────────────
// The Advanced mode was the last paid corner of Publish and Kane freed it on 22 September 2026. The three
// flags it drove are gone, the pill is gone, and the replica it fell back to is gone. What is asserted is
// their ABSENCE: a flag left behind reading a constant is exactly how a removed gate half-returns.
assert.doesNotMatch(deploy, /useFeature\(/, 'the Published page asks about no feature now');
assert.doesNotMatch(deploy, /advancedLocked|advancedPending|advancedGrant/, 'and keeps no flag from the gate it lost');
assert.doesNotMatch(deploy, /ProPreview|ProBadge/, 'no upsell of any kind is left on the page');
assert.doesNotMatch(deploy, /advancedOpen|mode === 'advanced'/, 'the Advanced tab is gone from the Publish page (8 October 2026); the assistant still reaches those tools');

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
  // Edit History retired in slice 11 (lane R2); exporting the trail is the Plan History's menu, free and unbadged.
  ['webview/src/plan-history.tsx', /Pro packages the trail as a shareable report/, 'exporting the trail is free now'],
  ['webview/src/plan-history.tsx', /ProBadge/, 'and carries no export pill'],
  ['webview/src/permissions.tsx', /<ProBadge show=\{tier === 'free' && !active\}/, 'the preset pill goes'],
  ['webview/src/permissions.tsx', /Configuring the matrix is a Pro feature/, 'the policy matrix is free now'],
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
// Which entries carry a Pro note is checked in section 10, against the derived Pro set, because a count
// typed here is the same stale literal the READMEs were. A guide that tells someone to pay for a page
// they can already open is the worst copy in the product, and so is one that stays silent about a
// control they cannot press.

// ── 10. Copy outside the webview: the Pro line, DERIVED and never typed ─────────────────────────────
// The two READMEs are the only place the Free and Pro line is written out in sentences for a reader, and
// they were wrong for a week because the sentence was a literal in three places at once: the root README,
// the extension README, and the assertion that pinned them. All three said four features after Kane had
// cut it to one, and each of the three was evidence for the other two.
//
// So the expected set is READ from the two authorities and never spelled here:
//   the ENGINE's ProFeature enum (Semanticus.Engine/Entitlement/ProFeature.cs) is what a licence grants;
//   features.ts is the page's half, and it owns the grants that are keyed on the TIER and carry no wire
//   id, because they are not tabs (the plan ranked, scored and run, and the ask box on the Overview).
// Add a paid thing to either and this fails until somebody writes the words a reader will see.
const proFeatureSrc = repo('Semanticus.Engine/Entitlement/ProFeature.cs');
const enumBody = /public enum ProFeature\s*\{([\s\S]*?)\n\s{4}\}/.exec(proFeatureSrc);
assert.ok(enumBody, 'the engine enum must stay findable: it is the authority on what a licence grants');
const engineFeatures = [...enumBody[1].matchAll(/^\s+([A-Z]\w*),$/gm)].map((m) => m[1]);
assert.ok(engineFeatures.length > 0, 'the engine enum parsed to nothing, so this check would pass empty');
// The tier-keyed grants the page owns. Each one is a named export in features.ts, so a grant that is
// deleted there drops out of the expected set instead of quietly staying in the README forever.
const tierKeyedGrants = ['planRankingOf'].filter((fn) => features.includes(`export function ${fn}(`));
// One README phrase per paid thing, keyed by the name the source uses. This table is the ONLY literal,
// and it is checked against both authorities below, so it cannot drift on its own either.
const PAID_WORDS = {
  Workflows: /\bWorkflows\b/,
  Ask: /\bask box\b/,
  AskData: /\bAsk your data\b/,
  planRankingOf: /\bplan ranked, scored and run\b/,
};
assert.deepEqual(
  Object.keys(PAID_WORDS).sort(), [...engineFeatures, ...tierKeyedGrants].sort(),
  'every paid thing needs README words and every README phrase needs a paid thing behind it. '
  + `The engine grants ${engineFeatures.join(', ')} and the page adds ${tierKeyedGrants.join(', ') || 'nothing'}.`);
// The page's wire-id list is the engine's TAB features and nothing else. A page id with no engine feature
// behind it would show an upsell nobody can buy; an engine tab feature with no page id would lock nothing.
const tabBlock = /TabFeatures = \{([^}]*)\}/.exec(proFeatureSrc);
assert.ok(tabBlock, 'ProFeatures.TabFeatures must stay findable');
const engineWireIds = [...tabBlock[1].matchAll(/ProFeature\.(\w+)/g)]
  .map((m) => new RegExp(`ProFeature\\.${m[1]} => "([^"]+)"`).exec(proFeatureSrc)?.[1])
  .filter(Boolean);
const pageWireIds = [...(/PRO_FEATURES: ProFeature\[\] = \[([^\]]*)\]/.exec(features)?.[1] ?? '')
  .matchAll(/'([^']+)'/g)].map((m) => m[1]);
assert.deepEqual(pageWireIds.sort(), engineWireIds.sort(),
  'features.ts PRO_FEATURES and the engine\'s tab features are one list seen from two sides');

const rootReadme = repo('README.md');
const extReadme = read('README.md');
// The section a reader actually reads, taken whole, so a phrase that only appears in some other paragraph
// does not count as having stated the Pro line.
// Wrapped to one line before matching: these files are hard-wrapped at about 110 columns, so a phrase
// that happens to straddle a line break is the same sentence to a reader and must be the same here.
const proSection = (source, where) => {
  const block = /\n## Free and Pro\n([\s\S]*?)(?=\n## )/.exec(source);
  assert.ok(block, `${where} must still have a "Free and Pro" section`);
  return block[1].replace(/[ \t]*\n(?!\n)[ \t]*/g, ' ');
};
const rootPro = proSection(rootReadme, 'the root README');
const extPro = proSection(extReadme, 'the extension README');
for (const [thing, phrase] of Object.entries(PAID_WORDS)) {
  assert.match(rootPro, phrase, `the root README's Free and Pro section must name ${thing}`);
  assert.match(extPro, phrase, `the extension README's Free and Pro section must name ${thing}`);
}
assert.equal(rootPro, extPro, 'the two READMEs had already drifted once; they say the same words or neither is true');
// Nothing retired may still be sold. Read from features.ts's own retirement list, so the day one of them
// is finally deleted this stops asking about it rather than failing.
const retired = [...(/RETIRED_FEATURES: RetiredProFeature\[\] = \[([^\]]*)\]/.exec(features)?.[1] ?? '')
  .matchAll(/'([^']+)'/g)].map((m) => m[1]);
assert.deepEqual(retired.sort(), ['modelCreate', 'publishedAdvanced', 'tests'],
  'the retired list is what the READMEs must stop selling; if it changed, read the new one');
const RETIRED_SALES = [
  /\bfour whole features\b/i, /Pro unlocks the one-click bulk/i,
  /Create in Model/i, /\bTests and Saved reports\b/i, /\bthe Advanced view under\b/i,
];
for (const [where, section] of [['root README', rootPro], ['extension README', extPro]]) {
  for (const gone of RETIRED_SALES) {
    assert.doesNotMatch(section, gone, `the ${where} still sells something Kane made free on 22 September`);
  }
}

// The user guide's Pro notes, against the same derived set. A help entry gets a note when the page it
// describes has a control a free session cannot press, and no other entry may carry one.
//   the tab features, by their wire id, which is also the help key for the tab they lock;
//   the plan, because the tier-keyed grant takes Run the plan and the scores off a free session.
// The ask box is deliberately NOT here: it sits on the Overview, the Overview is free to open, and a Pro
// note on that entry would tell a reader the whole page is paid. When help gains an entry of its own for
// the box, it needs one and this list grows with it.
// A wire id is not always the help key: askData locks the tool askdata. The tool map says which tool a feature locks.
const toolOfFeature = Object.fromEntries([...toolMap.matchAll(/(\w+): '(\w+)',/g)].map((m) => [m[2], m[1]]));
const paidHelpEntries = [...engineWireIds.map((id) => toolOfFeature[id] ?? id), ...(tierKeyedGrants.length ? ['plan'] : [])].sort();
assert.deepEqual(paidHelpEntries, ['askdata', 'plan', 'workflows'],
  'the paid help entries are derived; if this changed, the guide changes with it');
// A named function rather than an expression, because the CRLF probe at the end of this file runs it
// again over a Windows copy of the same source. Anchored with ^ and $ under /m, which is the second
// thing in this file that a \r quietly breaks.
const helpEntriesWithPro = (source) => [...source.matchAll(/^  "([a-z]+)": \{[\s\S]*?^  \},$/gm)]
  .filter((m) => /\n\s+"pro": "/.test(m[0])).map((m) => m[1]).sort();
const helpProEntries = helpEntriesWithPro(help);
assert.deepEqual(helpProEntries, paidHelpEntries,
  `the guide's Pro notes must be exactly the paid pages. It notes ${helpProEntries.join(', ') || 'none'}.`);
const proNotes = [...help.matchAll(/"pro": "([^"]+)"/g)].map((m) => m[1]);
for (const note of proNotes) {
  assert.match(note, /a Semanticus Pro feature\. /,
    `every Pro note names the feature and then what is still free; "${note}" does not`);
}
assert.match(repo('CHANGELOG.md'), /Free and Pro are now set by feature/, 'the change is in the ship log');
// docs/DOC-MAP.md is on the public mirror's keep list precisely because this test reads it, so its contract is
// unconditional. The other three are deliberately left out of the mirror (tools/release/mirror-manifest.json),
// so they are asserted only in the private repo, which is recognised by that manifest being present: the
// mirror excludes tools/release/. A private deletion of any of the four still fails here. (Codex, PR 377.)
assert.match(repo('docs/DOC-MAP.md'), /Superseded 2026-09-15/, 'the doc map marks its obsolete claims');
const privateRepo = existsSync(resolve(root, '..', 'tools/release/mirror-manifest.json'));
if (privateRepo) {
  assert.match(repo('docs/PLAN.md'), /Superseded 2026-09-15/, 'the plan marks its obsolete claims, and keeps the history');
  assert.match(repo('docs/feature-coverage-matrix.md'), /Superseded 2026-09-15/, 'and so does the coverage matrix');
  assert.match(repo('docs/subscription-fulfillment.md'), /Superseded 2026-09-15/, 'and the fulfilment copy');
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
// The mock's gate must be the product's gate. Workflows reads are the only ones a free plan is refused;
// everything the other three features used to wrap is free and the mock must simply ANSWER it, or a free
// shot shows a refusal the real engine would never send.
const paidOps = (harness.match(/var PAID_OPS = \{[\s\S]*?\n    \};/) || [''])[0];
assert.deepEqual([...paidOps.matchAll(/^      (\w+): \[/gm)].map((m) => m[1]).sort(), ['askData', 'workflows'],
  'the free mock refuses Workflows and Ask your data reads and nothing else');
for (const op of ['listWorkflows', 'getWorkflowPolicy', 'startWorkflow']) {
  assert.match(paidOps, new RegExp(`'${op}'`), `the free mock must refuse ${op}, not answer it`);
}
for (const op of ['listTests', 'listTableMappings', 'runTests', 'listTestRuns', 'getSpec', 'getPartitionM', 'gitLog',
  'createFunction', 'defineCalendar', 'generateTimeIntelligence', 'listEvidence', 'listDataAgents']) {
  assert.doesNotMatch(paidOps, new RegExp(`'${op}'`), `${op} is free now: the mock must answer it on every plan`);
}
assert.match(harness, /var PRO_FEATURE_IDS = \['workflows', 'askData'\];/, 'and the mock grants the same two ids the page knows');
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
assert.match(drive, /\[role="menu"\]\[aria-label="All tools"\] \[data-tool="\$\{tool\}"\]/,
  'and Help > All tools picks its row by id too');
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
// Astra found three paid WRITES missing from the harness's wrapped set (createFunction, defineCalendar,
// generateTimeIntelligence). All three belonged to Create in Model, which Kane freed on 22 September 2026,
// so there is no longer anything for the mock to refuse or the driver to judge about them. The lesson they
// taught survives as the whole-list check above: the mock's wrapped set is asserted, not spot-checked.
// The hold/release seam the pending scenes need.
assert.match(harness, /HELD_METHODS\.has\(msg\.method\)/, 'the harness must be able to park a call in flight');
assert.match(harness, /release: function \(method\)/, 'and answer it later with the fixture it holds by then');

// ── 12. THE WINDOWS CHECKOUT, reproduced here rather than waited for ────────────────────────────────
// CI's Windows leg checks out CRLF and the Linux leg does not, so every contract in this file that
// anchors on a newline holds on one and not the other. PR 387 failed on Windows alone, at the "Free and
// Pro" section reader: /\n## Free and Pro\n/ cannot match \r\n, and the message a Windows reviewer got
// was "the root README must still have a Free and Pro section", which reads like the section is missing.
//
// Nothing in this file is a contract about how lines END. So both readers normalise, once, at the read,
// and this block proves that by writing a CRLF copy of a real source file and reading it back through
// the same reader. A probe that normalised the text itself would only be testing the probe.
const crlfProbe = join(tmpdir(), `semanticus-crlf-probe-${process.pid}`);
mkdirSync(crlfProbe, { recursive: true });
const asWindows = (name, text) => {
  const at = join(crlfProbe, name);
  writeFileSync(at, text.replace(/\r?\n/g, '\r\n'), 'utf8');
  return at;
};
try {
  const windowsReadme = readAt(asWindows('README.md', readFileSync(resolve(root, '..', 'README.md'), 'utf8')));
  assert.match(windowsReadme, /## Free and Pro/, 'the CRLF probe must have written a real README');
  // This one first, because it is the assertion the Windows leg of PR 387 actually failed, word for word.
  assert.equal(proSection(windowsReadme, 'a CRLF checkout of the root README'), rootPro,
    'the Free and Pro section must read the same on a Windows checkout as on a Linux one');
  assert.doesNotMatch(windowsReadme, /\r/, 'the reader must hand every caller LF text, whatever is on disk');
  const windowsHelp = readAt(asWindows('help.tsx', readFileSync(resolve(root, 'webview/src/help.tsx'), 'utf8')));
  assert.deepEqual(helpEntriesWithPro(windowsHelp), helpProEntries,
    'and so must the guide\'s Pro notes, whose parser anchors on ^ and $ under /m');
} finally {
  rmSync(crlfProbe, { recursive: true, force: true });
}

console.log('pro feature line: page contracts passed');
