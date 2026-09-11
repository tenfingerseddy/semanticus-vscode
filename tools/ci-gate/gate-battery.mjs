// BATTERY for the CI cost gate: the `decide` classifier and the `gate` verdict in .github/workflows/ci.yml.
//
// WHY IT EXISTS. Those two shell scripts decide whether the expensive jobs are bought (`decide` emits four
// outputs since the 2026-08-01 cost trim: code, draft, battery, package) and whether a pull request is
// mergeable. Every bug in them is silent and green: a misclassified file skips a battery and the gate still
// reports success. Reading the YAML is not enough to know what the shell does with an empty variable, so
// this runs the REAL scripts, lifted out of the workflow file, under the same shell GitHub uses (`bash -e`,
// which is what the runner logs print as `shell: /usr/bin/bash -e {0}`).
//
// WHY IT LIFTS THE SCRIPTS INSTEAD OF COPYING THEM. A copy drifts, and a drifted copy passes while the real
// workflow is broken. The extractor asserts on sentinel lines it must find, so a silent mis-extraction fails
// the run instead of testing an empty string.
//
// The `gh` calls are served by a shim on PATH, so no network and no token. Nothing outside a temp directory
// is written.
//
// Run: node tools/ci-gate/gate-battery.mjs
import { readFileSync, writeFileSync, mkdtempSync, mkdirSync, existsSync, chmodSync } from 'node:fs';
import { spawnSync } from 'node:child_process';
import { dirname, resolve, join, delimiter } from 'node:path';
import { fileURLToPath } from 'node:url';
import { tmpdir } from 'node:os';

const repo = resolve(dirname(fileURLToPath(import.meta.url)), '..', '..');
// CI_YML lets a reviewer point the battery at an OLDER copy of the workflow (e.g.
// `git show HEAD:.github/workflows/ci.yml > /tmp/before.yml`) and watch the cases that a fix closes actually
// fail beforehand. A fix nobody watched fail is not proven.
const ciPath = process.env.CI_YML ? resolve(process.env.CI_YML) : resolve(repo, '.github/workflows/ci.yml');
const ci = readFileSync(ciPath, 'utf8').split(/\r?\n/);

// ---------------------------------------------------------------------------------------------------
// Lift the two `run:` blocks out of the workflow.
// ---------------------------------------------------------------------------------------------------
function liftRunBlock(afterMarker, sentinels) {
  const start = ci.findIndex((l) => l.includes(afterMarker));
  if (start < 0) throw new Error(`marker not found in ci.yml: ${afterMarker}`);
  let runLine = -1;
  for (let i = start; i < ci.length; i++) {
    if (/^\s+run: \|\s*$/.test(ci[i])) { runLine = i; break; }
  }
  if (runLine < 0) throw new Error(`no 'run: |' after marker: ${afterMarker}`);
  const runIndent = ci[runLine].match(/^\s*/)[0].length;
  const body = [];
  for (let i = runLine + 1; i < ci.length; i++) {
    const line = ci[i];
    if (line.trim() === '') { body.push(''); continue; }
    const indent = line.match(/^\s*/)[0].length;
    if (indent <= runIndent) break;
    body.push(line.slice(runIndent + 2));
  }
  const script = body.join('\n').replace(/\s+$/, '') + '\n';
  for (const s of sentinels) {
    if (!script.includes(s)) throw new Error(`lifted script for '${afterMarker}' is missing sentinel: ${s}`);
  }
  return script;
}

// The sentinels are the load-bearing lines of each contract: decide must emit all four outputs, and the
// gate must consume the battery and packaging sentinels' results. `battery-ok:$R_BATTERY` was the old
// (pre-trim) gate's loop pair; when the contract changed the extractor aborted on it, honestly, until this
// list was moved to the new one.
const decideScript = liftRunBlock('- id: classify', ['code=false', 'changed.txt', 'GITHUB_OUTPUT', 'battery=$battery', 'package=$package']);
const gateScript = liftRunBlock('needs: [decide, battery-ok', ['DECIDE_RESULT', '$R_BATTERY', '$R_PACKAGING', 'R_FINDINGS', 'R_CIGATE']);

// ---------------------------------------------------------------------------------------------------
// Runners.
// ---------------------------------------------------------------------------------------------------
function newSandbox() {
  const dir = mkdtempSync(join(tmpdir(), 'ci-gate-'));
  mkdirSync(join(dir, 'bin'));
  return dir;
}

function toPosix(p) {
  // The bash here is Git Bash on Windows; it needs a POSIX path for the script argument.
  // The shim directory is converted the same way so Git Bash can find `gh`. The join onto the
  // inherited PATH uses path.delimiter, not a colon: Windows PATH is semicolon-separated, and a
  // colon join from PowerShell makes bash lose cat, wc and the shim (F-003).
  const m = /^([A-Za-z]):[\\/](.*)$/.exec(p);
  const rest = (m ? m[2] : p).replace(/\\/g, '/');
  return m ? `/${m[1].toLowerCase()}/${rest}` : rest;
}

// A `gh` shim. The classifier makes two kinds of calls: the changed-file enumeration (`.../files` or
// `.../compare/...`) and, since the cost trim, a live label read (`repos/<r>/pulls/<n>` with no suffix).
// The shim tells them apart by its arguments and serves each from its own payload, with its own exit code,
// so a case can make the label read fail while the file enumeration succeeds.
function writeGhShim(dir, lines, exitCode, labels = '', labelsExit = 0) {
  const p = join(dir, 'bin', 'gh');
  const payload = lines.join('\n') + (lines.length ? '\n' : '');
  writeFileSync(join(dir, 'gh-payload.txt'), payload);
  writeFileSync(join(dir, 'gh-labels.txt'), labels + '\n');
  writeFileSync(
    p,
    [
      '#!/usr/bin/env bash',
      'case "$*" in',
      '  *"/files"*|*"/compare/"*)',
      `    cat "$(dirname "$0")/../gh-payload.txt"; exit ${exitCode} ;;`,
      '  *)',
      `    cat "$(dirname "$0")/../gh-labels.txt"; exit ${labelsExit} ;;`,
      'esac',
    ].join('\n') + '\n',
  );
  // Windows Git Bash treats a shebang file as executable; Linux does not. The battery has to run
  // in both places once it is portable, so the bit is set here rather than assumed.
  chmodSync(p, 0o755);
  return p;
}

function runDecide({ files = [], ghExit = 0, eventName = 'pull_request', prDraft = '', pushBefore = 'aaa', pushAfter = 'bbb', ref = 'refs/pull/294/merge', labels = '', labelsExit = 0 } = {}) {
  const dir = newSandbox();
  writeGhShim(dir, files, ghExit, labels, labelsExit);
  const scriptPath = join(dir, 'decide.sh');
  writeFileSync(scriptPath, decideScript);
  const outPath = join(dir, 'gh-output.txt');
  writeFileSync(outPath, '');
  const r = spawnSync('bash', ['-e', toPosix(scriptPath)], {
    cwd: dir,
    encoding: 'utf8',
    env: {
      ...process.env,
      PATH: `${toPosix(join(dir, 'bin'))}${delimiter}${process.env.PATH}`,
      GITHUB_OUTPUT: toPosix(outPath),
      GITHUB_REF: ref,
      EVENT_NAME: eventName,
      PR_NUMBER: '294',
      PR_DRAFT: prDraft,
      PUSH_BEFORE: pushBefore,
      PUSH_AFTER: pushAfter,
      REPO: 'owner/repo',
    },
  });
  const outputs = {};
  for (const line of readFileSync(outPath, 'utf8').split(/\r?\n/)) {
    const i = line.indexOf('=');
    if (i > 0) outputs[line.slice(0, i)] = line.slice(i + 1);
  }
  return { code: r.status, stdout: (r.stdout || '') + (r.stderr || ''), outputs };
}

function runGate(env) {
  const dir = newSandbox();
  const scriptPath = join(dir, 'gate.sh');
  writeFileSync(scriptPath, gateScript);
  // The default baseline is the ORDINARY green run under the cost trim: a ready code pull request whose
  // battery was bought and passed, and whose packaging was correctly not bought — so vsix, vsix-manifest
  // and packaging-ok default to 'skipped', which the gate REQUIRES when package=false. Cases override from
  // there. BATTERY/PACKAGE and EVENT_NAME/REF are always exported because the gate reads them under
  // `set -u`; a case that wants one absent must pass `undefined` explicitly.
  const merged = {
    PATH: process.env.PATH,
    SYSTEMROOT: process.env.SYSTEMROOT,
    DECIDE_RESULT: 'success',
    CODE: 'true',
    DRAFT: 'false',
    BATTERY: 'true',
    PACKAGE: 'false',
    EVENT_NAME: 'pull_request',
    REF: 'refs/pull/294/merge',
    R_FINDINGS: 'success',
    R_CIGATE: 'success',
    R_BATTERY: 'success',
    R_PACKAGING: 'skipped',
    R_ORACLE: 'success',
    R_BUILD: 'success',
    R_VSIX: 'skipped',
    R_MANIFEST: 'skipped',
    ...env,
  };
  // An `undefined` value means "this variable is not exported at all", which is what a missing job output
  // looks like only when the workflow does not list it in `env:`. Node would otherwise stringify it.
  for (const k of Object.keys(merged)) if (merged[k] === undefined) delete merged[k];
  const r = spawnSync('bash', ['-e', toPosix(scriptPath)], { cwd: dir, encoding: 'utf8', env: merged });
  return { code: r.status, stdout: (r.stdout || '') + (r.stderr || '') };
}

// ---------------------------------------------------------------------------------------------------
// Cases.
// ---------------------------------------------------------------------------------------------------
let failures = 0;
function check(name, ok, detail) {
  if (ok) {
    console.log(`  ok    ${name}`);
  } else {
    console.log(`  FAIL  ${name}  ${detail}`);
    failures++;
  }
}

console.log('CLASSIFIER (decide) — which changed files buy the battery');
const classify = [
  // [label, changed paths, expected code output]
  //
  // MARKDOWN DEFAULTS TO CODE ([T202] / F-010, stage 1 of docs/notes/T202-ci-cost-classifier-design.md).
  // The markdown skip arm is gone, so every one of these was `code=false` before that change and is
  // `code=true` after it. The design measured the whole cost of this: 12 of 200 first-parent commits on
  // `main` classified prose, so 6% is the ceiling on what defaulting markdown to code can spend, against
  // six named live gate inputs that were being missed. The proved prose allowlist is EMPTY because the
  // repo-wide control-byte sweep and the attribution marker scan both take their input from `git ls-files`
  // and so read all tracked markdown, and both run only when the battery is bought. What is still prose
  // is therefore only what the arms below leave prose: LICENSE, .gitignore, and *.html under docs/.
  ['markdown, unnamed, at the repository root -> now code', ['CLAUDE.md'], 'true'],
  ['markdown under docs/ -> now code', ['docs/note.md'], 'true'],
  ['prose beside code (Semanticus.VSCode/docs/x.md) -> now code', ['Semanticus.VSCode/docs/x.md'], 'true'],
  // AN UNKNOWN FUTURE MARKDOWN PATH. This is the whole of F-010: the named-input arm can only list readers
  // that exist, so a markdown file added tomorrow with a battery-gated reader was classified prose by
  // omission. Under default-to-code it is code before anybody has to notice it.
  ['a markdown path that does not exist yet (F-010: no future miss by omission)',
    ['Semanticus.Analysis/Rules/NOT-YET-WRITTEN.PROVENANCE.md'], 'true'],
  ['a future note under a directory nobody has audited', ['docs/kernel-b/B1-not-written-yet.md'], 'true'],
  // THE SIX LIVE GATE INPUTS THE OLD MARKDOWN ARM MISSED. Each reader below was located in the design
  // audit (section 4), not assumed from the path. Each of these six returned `code=false` from the real
  // lifted classifier before this change.
  ['docs/supported-platforms.md (release-docs.test.mjs asserts the public support contract)',
    ['docs/supported-platforms.md'], 'true'],
  ['docs/workflow-canvas-spec.md (WorkflowV2SolUnscoped{Three,Five}Tests read it under dotnet test)',
    ['docs/workflow-canvas-spec.md'], 'true'],
  ['a skill frontmatter file (SkillNamingTests reads the directory name and each name: field)',
    ['.claude/skills/verify-semanticus/SKILL.md'], 'true'],
  ['the BPA provenance file (attribution ATTRIBUTION_MACHINERY / EXEMPTION_COPYRIGHT_SCOPE)',
    ['Semanticus.Analysis/Rules/BPARules-PowerBI.PROVENANCE.md'], 'true'],
  ['the provenance directory class (attribution requires a tracked file under the prefix to trip a marker)',
    ['docs/provenance/2026-07-30-pr289-round3-guards-review.md'], 'true'],
  ['a docs/archive PROSE_MENTIONS entry (attribution still-needed assertion)',
    ['docs/archive/01-monetization.md'], 'true'],
  // STILL PROSE. These arms are untouched by [T202] and are what the prose lane now consists of.
  ['LICENSE and .gitignore', ['LICENSE', '.gitignore'], 'false'],
  ['the published progress page (docs/progress.html)', ['docs/progress.html'], 'false'],
  ['a source file', ['Semanticus.Core/Thing.cs'], 'true'],
  ['gate input JSON under docs/', ['docs/mcp-surface-inventory.json'], 'true'],
  ['the workflow itself', ['.github/workflows/ci.yml'], 'true'],
  // Markdown that is gate INPUT rather than documentation. Each reader is named in the ci.yml comment.
  ['THIRD-PARTY-NOTICES.md (attribution-gate INPUT)', ['THIRD-PARTY-NOTICES.md'], 'true'],
  ['TASKS.md (task-register INPUT)', ['TASKS.md'], 'true'],
  // The finding queue. The named-input arm is what classifies it, and that arm is still matched first; since
  // [T202] the `docs/*` arm would also reach it, but the explicit entry is the record of WHICH files were
  // proved and why, so it stays. Without either, editing the register would be a markdown-only change that
  // skips the required check which reads it.
  ['docs/findings.md (finding-queue INPUT)', ['docs/findings.md'], 'true'],
  ['README.md (attribution-gate INPUT)', ['README.md'], 'true'],
  ['CHANGELOG.md (release-docs INPUT)', ['CHANGELOG.md'], 'true'],
  ['RELEASE-CHECKLIST.md (both gates INPUT)', ['RELEASE-CHECKLIST.md'], 'true'],
  ['Semanticus.Dax/LICENSE-NOTE.md (attribution-gate INPUT)', ['Semanticus.Dax/LICENSE-NOTE.md'], 'true'],
  ['Semanticus.VSCode/README.md (packaged into the VSIX)', ['Semanticus.VSCode/README.md'], 'true'],
  ['a stock workflow seed', ['Semanticus.Engine/workflows/governed-rename.md'], 'true'],
  ['a parked workflow seed', ['Semanticus.Engine/workflows-parked/x.md'], 'true'],
  ['a workflow template seed', ['Semanticus.Engine/workflow-templates/hard-measure.md'], 'true'],
  ['third-party-manifest.json (attribution-gate INPUT)', ['third-party-manifest.json'], 'true'],
  // THE RENAME UNION. The old case renamed code onto `docs/note.md`, which since [T202] is code on its own,
  // so that pairing would now pass whether the union were classified or not. The prose half has to be a name
  // that is STILL prose for this case to prove anything, hence LICENSE.
  ['a rename of code onto a prose name (LICENSE)', ['LICENSE', 'Semanticus.Core/Thing.cs'], 'true'],
];
for (const [label, files, expected] of classify) {
  const r = runDecide({ files });
  check(`${label} -> code=${expected}`, r.code === 0 && r.outputs.code === expected,
    `exit=${r.code} code=${JSON.stringify(r.outputs.code)}`);
}
{
  const r = runDecide({ files: [], ghExit: 1 });
  check('the diff cannot be enumerated -> code=true (fail safe toward spending)',
    r.code === 0 && r.outputs.code === 'true', `exit=${r.code} code=${JSON.stringify(r.outputs.code)}`);
}
{
  // The cap must be what decides this, so the file list has to be one the classifier would otherwise call
  // prose. `docs/n*.md` was that list until [T202]; markdown is code now, so it would score true past a
  // deleted cap check and prove nothing. `*.html` under docs/ is the prose that survives stage 1.
  const many = Array.from({ length: 3000 }, (_, i) => `docs/n${i}.html`);
  const r = runDecide({ files: many });
  check('the file list hits the API cap -> code=true',
    r.code === 0 && r.outputs.code === 'true', `exit=${r.code} code=${JSON.stringify(r.outputs.code)}`);
}
{
  const r = runDecide({ files: ['docs/note.md'], prDraft: 'true' });
  check('a draft pull request -> draft=true', r.code === 0 && r.outputs.draft === 'true',
    `exit=${r.code} draft=${JSON.stringify(r.outputs.draft)}`);
}

console.log('SPEND (decide) — the battery and package outputs of the 2026-08-01 cost trim');
// [label, runDecide options, expected battery, expected package]
const spend = [
  ['code pull request, ready -> battery bought, packaging not',
    { files: ['Semanticus.Core/Thing.cs'] }, 'true', 'false'],
  // The documents-only lane after [T202]: markdown no longer reaches it, so the prose input here is
  // docs/progress.html, which the `*.html` exemption inside the `docs/*` arm still leaves prose.
  ['documents-only pull request -> neither bought',
    { files: ['docs/progress.html'] }, 'false', 'false'],
  ['a markdown-only pull request now BUYS the battery ([T202])',
    { files: ['docs/note.md', 'CLAUDE.md'] }, 'true', 'false'],
  ['draft code pull request -> neither bought (draft never buys)',
    { files: ['Semanticus.Core/Thing.cs'], prDraft: 'true' }, 'false', 'false'],
  // THE MAIN-PUSH DEDUP: the one legal battery=false on a code change. The squashed tree just passed the
  // battery as a pull request; only the finding queue, the oracle and the register workflow re-run.
  ['push to main, code -> battery NOT re-bought (dedup)',
    { files: ['Semanticus.Core/Thing.cs'], eventName: 'push', ref: 'refs/heads/main' }, 'false', 'false'],
  ['push to main, documents only -> neither bought',
    { files: ['docs/progress.html'], eventName: 'push', ref: 'refs/heads/main' }, 'false', 'false'],
  ['push to another branch, code -> battery bought (it did not just pass as a PR)',
    { files: ['Semanticus.Core/Thing.cs'], eventName: 'push', ref: 'refs/heads/ai-readiness-rules' }, 'true', 'false'],
  // THE PACKAGING ROUTES. v* tags are deliberately absent: publish.yml owns them (PR #308 review).
  ['push to a release/** branch -> packaging bought',
    { files: ['Semanticus.Core/Thing.cs'], eventName: 'push', ref: 'refs/heads/release/1.2' }, 'true', 'true'],
  ['pull request carrying the release label -> packaging bought',
    { files: ['Semanticus.Core/Thing.cs'], labels: 'enhancement,release' }, 'true', 'true'],
  ['labels that merely CONTAIN "release" -> packaging NOT bought',
    { files: ['Semanticus.Core/Thing.cs'], labels: 'pre-release,released' }, 'true', 'false'],
  ['the label read fails -> packaging not bought, battery unaffected (visible skip, dispatch is the recovery)',
    { files: ['Semanticus.Core/Thing.cs'], labels: '', labelsExit: 1 }, 'true', 'false'],
  ['workflow_dispatch -> everything bought on demand',
    { files: [], eventName: 'workflow_dispatch', ref: 'refs/heads/main' }, 'true', 'true'],
];
for (const [label, opts, wantBattery, wantPackage] of spend) {
  const r = runDecide(opts);
  const ok = r.code === 0 && r.outputs.battery === wantBattery && r.outputs.package === wantPackage;
  check(`${label} -> battery=${wantBattery} package=${wantPackage}`, ok,
    `exit=${r.code} battery=${JSON.stringify(r.outputs.battery)} package=${JSON.stringify(r.outputs.package)}`);
}
{
  // Every path must emit ALL FOUR outputs: the gate refuses a run where any of them is missing, so a decide
  // path that exits early without writing them turns every ref down that path unmergeable.
  const paths = [
    ['enumeration failure path', { files: [], ghExit: 1 }],
    ['API-cap path', { files: Array.from({ length: 3000 }, (_, i) => `docs/n${i}.html`) }],
    ['classified path, prose verdict', { files: ['docs/progress.html'] }],
    ['classified path, markdown code verdict', { files: ['docs/note.md'] }],
  ];
  for (const [label, opts] of paths) {
    const r = runDecide(opts);
    const ok = r.code === 0 && ['code', 'draft', 'battery', 'package'].every(
      (k) => r.outputs[k] === 'true' || r.outputs[k] === 'false');
    check(`the ${label} emits all four outputs as booleans`, ok,
      `exit=${r.code} outputs=${JSON.stringify(r.outputs)}`);
  }
}

console.log('VERDICT (gate) — what the one required check reports');
const verdicts = [
  ['documents only, ready', { CODE: 'false', DRAFT: 'false', R_BATTERY: 'skipped', R_ORACLE: 'skipped', R_BUILD: 'skipped', R_VSIX: 'skipped', R_MANIFEST: 'skipped' }, 0],
  // THE FINDING QUEUE IS CHECKED BEFORE THE DOCUMENTS-ONLY EARLY RETURN. Both cases below would have exited 0
  // under the previous gate, which is the whole reason those jobs moved out of the unrequired workflow: a
  // control that can be red while a merge succeeds is theatre. The documents-only case is the important one,
  // because the register IS markdown.
  ['finding queue red on a CODE change', { CODE: 'true', R_FINDINGS: 'failure' }, 1],
  ['finding queue red on a DOCUMENTS-ONLY change', { CODE: 'false', R_FINDINGS: 'failure', R_BATTERY: 'skipped', R_ORACLE: 'skipped', R_BUILD: 'skipped', R_VSIX: 'skipped', R_MANIFEST: 'skipped' }, 1],
  ['finding queue SKIPPED on a non-draft ref (it should always run)', { CODE: 'true', R_FINDINGS: 'skipped' }, 1],
  ['finding queue result missing entirely', { CODE: 'true', R_FINDINGS: undefined }, 1],
  // THE CI GATE BATTERY IS CHECKED BEFORE THE DOCUMENTS-ONLY EARLY RETURN, same reason as the
  // finding queue: a control a prose change can skip is a control a prose change will break.
  // F-024 was exactly that skip. Both directions, then the two ways of not having a result.
  ['ci-gate-battery red on a CODE change', { CODE: 'true', R_CIGATE: 'failure' }, 1],
  ['ci-gate-battery red on a DOCUMENTS-ONLY change', { CODE: 'false', R_CIGATE: 'failure', R_BATTERY: 'skipped', R_ORACLE: 'skipped', R_BUILD: 'skipped', R_VSIX: 'skipped', R_MANIFEST: 'skipped' }, 1],
  ['ci-gate-battery SKIPPED on a non-draft ref (it should always run)', { CODE: 'true', R_CIGATE: 'skipped' }, 1],
  ['ci-gate-battery result missing entirely', { CODE: 'true', R_CIGATE: undefined }, 1],
  ['code, ready, whole battery green', { CODE: 'true' }, 0],
  ['code, ready, one matrix job red', { CODE: 'true', R_BUILD: 'failure', R_BATTERY: 'skipped' }, 1],
  ['a draft pull request', { DRAFT: 'true', CODE: 'true', R_BATTERY: 'skipped', R_ORACLE: 'skipped', R_BUILD: 'skipped', R_VSIX: 'skipped', R_MANIFEST: 'skipped' }, 1],
  ['the decide job itself failed', { DECIDE_RESULT: 'failure', CODE: '', DRAFT: '' }, 1],
  // The MUST-2 cases: a code output that is neither 'true' nor 'false' must never read as documents-only.
  ['CODE is empty (output missing)', { CODE: '' }, 1],
  ['CODE is unset entirely', { CODE: undefined }, 1],
  ['CODE is malformed ("flase")', { CODE: 'flase' }, 1],
  ['CODE is the wrong case ("False")', { CODE: 'False' }, 1],
  ['DRAFT is malformed ("yes")', { DRAFT: 'yes', CODE: 'true' }, 1],
  // The 2026-08-01 cost-trim states. The defaults are already case 2 of the four-case table (battery
  // bought and green, packaging correctly skipped), which 'code, ready, whole battery green' covers above.
  ['BATTERY is empty (output missing)', { BATTERY: '' }, 1],
  ['PACKAGE is malformed ("nope")', { PACKAGE: 'nope' }, 1],
  // Case 4: packaging bought. Both sides of the coin, then the inconsistencies.
  ['release run: packaging bought, all five legs + manifest + sentinel green',
    { PACKAGE: 'true', R_PACKAGING: 'success', R_VSIX: 'success', R_MANIFEST: 'success' }, 0],
  ['packaging bought but SKIPPED (a skip where success was owed is never a pass)',
    { PACKAGE: 'true', R_PACKAGING: 'skipped', R_VSIX: 'skipped', R_MANIFEST: 'skipped' }, 1],
  ['packaging bought, one leg red (sentinel correctly skipped, still not green)',
    { PACKAGE: 'true', R_PACKAGING: 'skipped', R_VSIX: 'failure', R_MANIFEST: 'skipped' }, 1],
  ['packaging NOT bought but it RAN (the other direction of the same lie)',
    { PACKAGE: 'false', R_PACKAGING: 'success', R_VSIX: 'success', R_MANIFEST: 'success' }, 1],
  // Case 3: the main-push dedup, and the fence around it. battery=false with code=true is legal on a push
  // to main and NOWHERE else; and on main the heavy jobs must be exactly skipped, not failed, not run.
  ['push to main: battery not re-bought, heavy jobs exactly skipped, oracle green',
    { BATTERY: 'false', EVENT_NAME: 'push', REF: 'refs/heads/main', R_BATTERY: 'skipped', R_BUILD: 'skipped' }, 0],
  ['battery=false with code=true OUTSIDE a push to main (decide mis-derived the spend)',
    { BATTERY: 'false', EVENT_NAME: 'pull_request', REF: 'refs/pull/294/merge', R_BATTERY: 'skipped', R_BUILD: 'skipped' }, 1],
  ['battery=false on a push to a NON-main branch is just as illegal',
    { BATTERY: 'false', EVENT_NAME: 'push', REF: 'refs/heads/release/1.2', R_BATTERY: 'skipped', R_BUILD: 'skipped' }, 1],
  ['push to main but build-and-smoke RAN AND FAILED (skip owed, failure delivered)',
    { BATTERY: 'false', EVENT_NAME: 'push', REF: 'refs/heads/main', R_BATTERY: 'skipped', R_BUILD: 'failure' }, 1],
  ['push to main with the coverage oracle red (still bought on every code change)',
    { BATTERY: 'false', EVENT_NAME: 'push', REF: 'refs/heads/main', R_BATTERY: 'skipped', R_BUILD: 'skipped', R_ORACLE: 'failure' }, 1],
  ['battery bought but battery-ok SKIPPED on a code pull request',
    { R_BATTERY: 'skipped', R_BUILD: 'skipped' }, 1],
];
for (const [label, env, expected] of verdicts) {
  const e = { ...env };
  if (Object.prototype.hasOwnProperty.call(e, 'CODE') && e.CODE === undefined) e.CODE = undefined;
  const r = runGate(e);
  check(`${label} -> exit ${expected}`, r.code === expected,
    `exit=${r.code}\n        ${r.stdout.trim().split('\n').join('\n        ')}`);
}

console.log(failures === 0 ? '\nAll cases hold.' : `\n${failures} case(s) failed.`);
process.exit(failures === 0 ? 0 : 1);
