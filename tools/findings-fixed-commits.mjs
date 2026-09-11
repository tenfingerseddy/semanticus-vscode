#!/usr/bin/env node
// THE SUBSTANCE OF `FIXED`. tools/tasks-register-check.mjs proves the SHAPE of a FIXED row: the evidence opens
// with a sha and then says what changed. That is not enough on its own, because a sha-shaped string is not a
// sha, and a real sha that never touched the file the row names is not evidence that the named defect is gone.
//
//   node tools/findings-fixed-commits.mjs [path-to-findings.md]
//
// Two assertions per FIXED row:
//   1. THE COMMIT IS AN ANCESTOR OF main.  git merge-base --is-ancestor <sha> origin/main
//      Existence (`git cat-file -e`) was the first version of this rule and it was WRONG for this repository:
//      every pull request is squash-merged, so a branch commit never reaches main and exists only while its
//      merged branch survives. Deleting one routine branch (#303, --delete-branch) destroyed three cited
//      commits and turned main red — F-078 and F-080 carry the measurement. Ancestry is the claim a register
//      on main can actually keep: the cited commit must be part of main's own history.
//   2. IT TOUCHED WHAT IT CLAIMS. Every path in the row's file:line cell is compared against the commit's
//      changed-file list, and at least one must match.
//
// (2) has a legitimate exception and it is made EXPLICIT rather than silent: a defect can be fixed in a caller,
// or by deleting the file, or somewhere else entirely. That is allowed, and the row must say so in the words
// `NOT THE NAMED FILE:` followed by the reason. Silence is what is forbidden, because silence is how "fixed"
// becomes as cheap to write as it was to write "scheduled" against any task that happened to exist.
//
// WHY THIS IS ITS OWN TOOL, and where it therefore does NOT run. It needs git history. `actions/checkout@v4`
// clones at depth 1 by default, so on the jobs inside the required `gate` any commit older than HEAD is simply
// absent, and this would fail on every honest row. Rather than teach it to shrug at a missing object, which is
// the exact defect family in [T206], it lives in the Task register workflow with `fetch-depth: 0` and it
// REFUSES on a shallow clone, naming the setting. A shallow repository is "I cannot look", never "nothing to
// see", and the difference is the whole reason this file exists separately.

import { execFileSync } from 'node:child_process';
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const NOT_THE_NAMED_FILE = 'NOT THE NAMED FILE:';
const FIXED_EVIDENCE = /^([0-9a-f]{7,40})\b([\s\S]*)$/;

// =====================================================================================================
// A COMMIT DOES NOT PROVE A DEFECT IS GONE. ONLY A TEST DOES. Sol's defeat, 2026-07-30: as first written,
// FIXED proved that some commit touched a path the row named for itself, which is not the same claim.
//
// This is not a new rule for this project, it is golden rule 5: every bug fix starts with a failing test,
// written first and watched to fail. So a FIXED row names a commit AND a test, in the form
//   test: <path>::<name>
// and both halves are checked: the file must exist, it must be in a suite CI ACTUALLY RUNS, and <name> must
// appear literally inside it. Naming a file is not naming a test, which is why the `::<name>` half is required.
//
// The escape hatch works like the other two: legal, never silent. `NO TEST: <reason>` records how the fix was
// proved instead. A finding whose subject is a comment, a manifest entry or a workflow trigger can be genuinely
// untestable, and saying so is worth more than a test written to satisfy a gate.
const FIXED_TEST = /\btest:\s*([^\s:]+(?:\/[^\s:]+)*)::([^|]+?)(?:\s{2,}|$)/;
const NO_TEST = 'NO TEST:';

// THE SUITES THAT ACTUALLY RUN. An allowlist rather than "any file with test in the name", because the
// difference between a test and a test-shaped file is whether CI executes it. tools/ci-gate/gate-battery.mjs
// was deliberately absent for exactly that reason until [T209]: it appeared in no CI job at all, which was its
// own finding (F-024). The ci-gate-battery job runs it now, so it is listed below like any other suite. That
// header is kept rather than deleted because the reason it was absent is the reason the list exists.
//
// EVERY ENTRY PROVES ITSELF. The `runner` field was descriptive prose in the first version, so a suite that
// nothing executes could be added to this list and believed, which is the same defect as a comment asserting an
// invariant the code does not hold. Each entry now carries the FILE and the exact COMMAND STRING that runs it,
// and verifySuites() reads that file and refuses the entry when the command is not there. An entry that cannot
// be bound to a real invocation does not belong in the allowlist.
const RUNNING_SUITES = [
    {
        pattern: /^Semanticus\.VSCode\/test\/[^/]+\.test\.mjs$/,
        runner: 'npm test, via Semanticus.VSCode/scripts/run-tests.mjs, inside build-and-smoke',
        // Three links, all asserted: ci.yml runs `npm test`, the package script points at the runner, and the
        // runner discovers *.test.mjs by glob. Any one of them breaking makes this pattern a lie.
        proof: [
            ['.github/workflows/ci.yml', 'run: npm test'],
            ['Semanticus.VSCode/package.json', 'node scripts/run-tests.mjs'],
            ['Semanticus.VSCode/scripts/run-tests.mjs', ".endsWith('.test.mjs')"],
        ],
    },
    {
        pattern: /^Semanticus\.Tests\/.+\.cs$/,
        runner: 'dotnet test Semanticus.Tests in ci.yml',
        proof: [['.github/workflows/ci.yml', 'dotnet test Semanticus.Tests/Semanticus.Tests.csproj']],
    },
    {
        pattern: /^Semanticus\.Dax\.Tests\/.+\.cs$/,
        runner: 'dotnet test Semanticus.Dax.Tests in ci.yml',
        proof: [['.github/workflows/ci.yml', 'dotnet test Semanticus.Dax.Tests/Semanticus.Dax.Tests.csproj']],
    },
    // THE CI COST-GATE BATTERY. It is a battery rather than a *.test.mjs file and it sits outside every suite
    // directory, so no pattern above can reach it and it needs its own entry bound to its own invocation.
    // Its absence here was correct until [T209]: CI ran it nowhere (F-024), so naming one of its cases would
    // have claimed coverage this repository did not execute. The `ci-gate-battery` job now runs it by name on
    // every non-draft ref and `gate` refuses to pass unless that job succeeded, so its cases are real cover
    // and refusing them would call an executed case unrun. The pattern is the exact file, not the directory:
    // a second file added beside it would not be run by that job and must not ride on this proof.
    {
        pattern: /^tools\/ci-gate\/gate-battery\.mjs$/,
        runner: 'node tools/ci-gate/gate-battery.mjs in the ci-gate-battery job of ci.yml',
        proof: [['.github/workflows/ci.yml', 'run: node tools/ci-gate/gate-battery.mjs']],
    },
    // THE FIXED-VERDICT SUITE'S OWN TEST FILE. It is not auto-discovered by anything: it resolves real commits,
    // so it cannot ride `npm test` inside build-and-smoke, which checks out at depth 1. ci.yml runs it by name in
    // finding-queue, which checks out with fetch-depth: 0 and which `gate` requires, and tasks-register.yml runs
    // it as well. It matched no pattern above, so a FIXED row naming one of its cases was refused as unrun while
    // CI was executing it on every non-draft ref (F-140), the same defect as the battery's, one file over.
    //
    // Citing it is not a check sourcing its own controls: what makes it count is ci.yml's invocation line, which
    // this file does not write. Note the standing trap recorded in that test file, though: a plant pointed at the
    // file being searched passes because the fixture string is then literally inside it, so a case name naming
    // this file proves the registration, not the case.
    {
        pattern: /^tools\/findings-fixed-commits\.test\.mjs$/,
        runner: 'node tools/findings-fixed-commits.test.mjs in the finding-queue job of ci.yml',
        proof: [['.github/workflows/ci.yml', 'run: node tools/findings-fixed-commits.test.mjs']],
    },
    // THE PRIVATE MIRROR GATE. It is reached through a chain rather than a `run:` line: the manifest
    // DECLARES it a private gate, release-mirror-gate.test.mjs reads that declaration and SPAWNS each gate
    // it names, and that wrapper sits in Semanticus.VSCode/test where `npm test` discovers it, inside
    // build-and-smoke. Before [T204] nothing enforced ran it, which is why this file's own header used to
    // say so and why F-141 and F-142 had to correct that claim twice.
    //
    // THE PROOF BINDS EXECUTION, NOT JUST PRESENCE, and the first version of this entry did not (F-145).
    // It needled `privateGates(readManifest(root))`, which lives inside the helper body, so the test could
    // have stopped CALLING the helper, or the helper stopped SPAWNING, while all four needles stayed put
    // and this entry went on claiming a dormant suite was CI-run. A FIXED row citing it would then have
    // claimed coverage from something nothing executes, which is the exact defect F-019 is about. The two
    // needles below are the call and the spawn, so removing either breaks the entry.
    {
        pattern: /^tools\/release\/mirror-public\.test\.mjs$/,
        runner: 'spawned by release-mirror-gate.test.mjs, which npm test discovers, inside build-and-smoke',
        proof: [
            ['.github/workflows/ci.yml', 'run: npm test'],
            ['Semanticus.VSCode/scripts/run-tests.mjs', ".endsWith('.test.mjs')"],
            ['tools/release/mirror-manifest.json', '"path": "tools/release/mirror-public.test.mjs"'],
            // the declaration is read, the helper is CALLED by a test, and the gate is SPAWNED
            ['Semanticus.VSCode/test/release-mirror-gate.test.mjs', 'privateGates(readManifest(root))'],
            ['Semanticus.VSCode/test/release-mirror-gate.test.mjs', 'runMirrorGate(repoRoot)'],
            ['Semanticus.VSCode/test/release-mirror-gate.test.mjs', 'spawnSync(process.execPath, [join(root, rel)]'],
        ],
    },
    // One entry per smoke project rather than one alternation, so each is bound to its OWN `dotnet run` line.
    // A single grouped pattern would have let a project with no invocation ride on its siblings' proof.
    ...['Smoke', 'RpcSmoke', 'McpSmoke', 'LearnSmoke', 'LearnBench', 'AirSmoke', 'CicdSmoke'].map(name => ({
        pattern: new RegExp(String.raw`^Semanticus\.${name}\/.+\.cs$`),
        runner: `dotnet run --project Semanticus.${name} in ci.yml`,
        proof: [['.github/workflows/ci.yml', `dotnet run --project Semanticus.${name}`]],
    })),
];

export function runningSuite(path) {
    return RUNNING_SUITES.find(s => s.pattern.test(path)) ?? null;
}

// Reads every proof link and reports the ones that are not there. Called before any row is judged, and before
// the no-FIXED-rows early return, so the allowlist is bound on every run rather than only when it is consulted.
export function verifySuites(repoRoot) {
    const problems = [];
    for (const suite of RUNNING_SUITES)
        for (const [file, needle] of suite.proof) {
            let text = null;
            try { text = readFileSync(resolve(repoRoot, file), 'utf8'); } catch { /* reported below */ }
            if (text === null)
                problems.push(`the suite ${suite.pattern.source} claims to be run by "${suite.runner}", but its ` +
                    `proof file ${file} cannot be read.`);
            else if (!text.includes(needle))
                problems.push(`the suite ${suite.pattern.source} claims to be run by "${suite.runner}", but ` +
                    `${file} does not contain ${JSON.stringify(needle)}. An allowlist entry that cannot be bound ` +
                    'to a real invocation is prose, and a FIXED row pointing at it would claim coverage nothing runs.');
        }
    return problems;
}

// WHICH FINDINGS MAY DECLARE `NO TEST:`, frozen and pinned by test. As first built the hatch was a plausible
// sentence checked with a substring match, which is no bound at all: every future row could take it. Freezing the
// permitted ids means adding one is an edit to this list that breaks a test and shows up in review, exactly like
// the birth-pull-request constant. The reason is recorded here so the list cannot grow silently either.
// [T209] REMOVED F-014 and F-086 from this list. Both rested on the same stated reason: their only cover
// is a case in tools/ci-gate/gate-battery.mjs, and CI ran that battery nowhere. The ci-gate-battery job
// runs it now, so that reason is false and the exemption became a hole rather than an exception. Both
// have a real battery case to name instead.
//
// F-087 STAYS, with its reason corrected rather than removed. Its subject is the REMOVAL of a v* tag
// trigger, and nothing automated covers that. Measured, not assumed: gate-battery.mjs line 259 carries a
// COMMENT that tags are absent from the packaging routes, which is prose, not an assertion, and
// ci-gate-path.test.mjs asserts nothing about triggers at all. Writing a test purely to retire an
// exemption would be a test written to satisfy a gate, which is the thing the hatch exists to avoid.
export const NO_TEST_ALLOWED = new Map([
    ['F-135', 'a reversible MCP parameter-description correction checked against the actual tool declaration and merged source; a prose-equality test would mirror the wording rather than exercise behavior'],
    ['F-084', 'the fix IS a pre-commit hook behaviour, proven in both directions on its branch and exercised on every local commit, and no CI-run suite executes hooks; the [T209] family reason'],
    ['F-087', 'the fix is the REMOVAL of the v* tag trigger, verified by the YAML diff; no automated check covers it. The battery only COMMENTS that tags are absent from the packaging routes (line 259) and ci-gate-path.test.mjs asserts nothing about triggers, so naming either would cite an assertion that does not exist'],
    ['F-117', 'a dated readiness-status correction, checked against the merged prerequisite and saved runtime logs; a text-equality assertion would mirror the prose rather than prove that the prerequisite was delivered'],
    ['F-126', 'a completed task row moved to the DONE section, verified by inspecting the delivered record; a new prose-equality test would duplicate a reversible documentation edit'],
    ['F-127', 'a delivery note corrected to cite the source fixes actually named by six findings, verified against those rows and delivered ancestry; a text-equality test would repeat the prose without establishing its evidence'],
    ['F-129', 'a duplicated stale preparation report removed from the delivered tree, verified by its deletion and ancestry; a new absence assertion would mirror this reversible documentation cleanup'],
    ['F-133', 'hosted-check identifiers restored in the existing delivery receipt, verified against the actual merged paragraph and run records; a prose-equality test would repeat the text without proving those runs passed'],
    ['F-141', 'the fix is the correction of two comment blocks that told readers no enforced gate invoked the file, which the [T204] wiring had just made false; the enforcement itself is proven by release-mirror-gate.test.mjs spawning every gate the manifest declares, and a text-equality assertion on the corrected prose would mirror the wording rather than prove anything'],
    ['F-142', 'the fix corrects the surviving stale comment in mirror-public.mjs to name the CI-discovered wrapper; verified the merged comment diff against release-mirror-gate.test.mjs invoking the private suite. Existing tests cover enforcement, not this prose correction, and a wording assertion would only repeat the comment'],
    ['F-148', 'the fix corrects the stale WorkflowStep comment after public loop and call execution landed; verified the merged Workflow.cs comment against WorkflowParser admission and WorkflowRunner frame execution. Existing workflow tests cover execution, while a wording assertion would only repeat this reversible prose correction'],
]);

// A path out of the file:line cell. Cells hold things like
// `tools/coverage-oracle.ps1:134` or `a.cs:1202,1208 (lane opens at :1102); b/c.json`, and also prose such as
// `docs/findings.md (the verdict set above)`. Take tokens that look like a repository path and strip any
// :line suffix; anything with no slash and no dot is not a path and is ignored.
export function namedPaths(site) {
    const out = new Set();
    for (const token of site.split(/[;,\s]+/)) {
        const bare = token.replace(/[(),;]/g, '').replace(/:.*$/, '');
        if (!bare) continue;
        // A slash-bearing token is a path even without an extension: `.githooks/pre-commit` is a FILE,
        // and requiring an extension here was the missed sibling of the #307 sitePaths fix (F-083's
        // binding). A directory token slips this rule, but a directory never appears in `--name-only`
        // output, so the touch assertion then fails loudly, which is the safe direction.
        if (!/[/\\]/.test(bare) && !/\.[A-Za-z0-9]+$/.test(bare)) continue;
        out.add(bare.replace(/\\/g, '/'));
    }
    return [...out];
}

export function fixedRows(findingsText) {
    const rows = [];
    for (const [index, line] of findingsText.split(/\r?\n/).entries()) {
        const trimmed = line.trim();
        if (!trimmed.startsWith('|')) continue;
        const cells = trimmed.replace(/^\|/, '').replace(/\|$/, '').split('|').map(c => c.trim());
        if (cells.length !== 7 || !/^F-\d{3,}$/.test(cells[0]) || cells[4] !== 'FIXED') continue;
        rows.push({ id: cells[0], site: cells[3], evidence: cells[5], line: index + 1 });
    }
    return rows;
}

// stderr is CAPTURED, not inherited. `git cat-file -e` on a missing object prints `fatal: Not a valid object
// name`, and letting that reach the console puts the word "fatal" in the middle of an otherwise green run.
// A gate that prints alarming noise while passing is how people learn to stop reading it.
const git = (repoRoot, args) =>
    execFileSync('git', args, { cwd: repoRoot, encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] }).trim();

// THE TIP THIS TOOL JUDGES ANCESTRY AGAINST. origin/main first, because a feature-branch checkout's local
// `main` may not exist or may be stale; plain `main` second, for a single-branch clone whose remote-tracking
// ref was pruned. No third fallback: a repository where neither resolves is "I cannot look", never "pass".
//
// FULLY QUALIFIED, not the short names (Sol, 2026-08-01): git's short-ref lookup prefers refs/heads/ and
// refs/tags/ over refs/remotes/, so a local branch literally named `origin/main`, or a tag named `main`,
// would silently win the lookup, and a dead branch commit could then PASS ancestry against the impostor —
// the one direction this gate must never fail in. The qualified forms have exactly one possible referent.
//
// STALE IS SAFE, RE-DERIVED ONCE SO NOBODY DERIVES IT AGAIN: a stale local origin/main, like a shallow
// clone, can only REFUSE a freshly merged squash sha (exit 1/2 until a fetch), never falsely pass a dead
// one, and both CI callers (the tasks-register workflow and ci.yml's finding-queue job) check out with
// fetch-depth: 0, so origin/main is present and current there. Confirmed by Sol's review of 8369f90b.
export function mainTip(repoRoot) {
    for (const ref of ['refs/remotes/origin/main', 'refs/heads/main']) {
        try { return { ref, sha: git(repoRoot, ['rev-parse', '--verify', '--quiet', `${ref}^{commit}`]) }; }
        catch { /* try the next ref */ }
    }
    return null;
}

// One question, three honest answers. `missing` and `not-ancestor` are DIFFERENT failures and get different
// messages: a missing object may be a typo'd sha, while a present non-ancestor is almost always a branch
// commit cited from the branch that wrote the row, which squash-merging guarantees will never reach main.
export function ancestryOfMain(repoRoot, sha, tip) {
    try { git(repoRoot, ['cat-file', '-e', `${sha}^{commit}`]); } catch { return 'missing'; }
    try { git(repoRoot, ['merge-base', '--is-ancestor', sha, tip.sha]); return 'ancestor'; }
    catch { return 'not-ancestor'; }
}

export function main(findingsPath, repoRoot, log = console.log, warn = console.error) {
    const rows = fixedRows(readFileSync(findingsPath, 'utf8'));

    log(`${findingsPath}`);
    log(`  FIXED rows: ${rows.length}`);

    // BEFORE anything is judged, and before the early return below: the allowlist this tool relies on must still
    // be bound to real invocations. A stale allowlist is worse than a missing check, because it answers.
    const unbound = verifySuites(repoRoot);
    if (unbound.length > 0) {
        warn('\nThe running-suite allowlist no longer matches the workflows:');
        for (const problem of unbound) warn(`  ${problem}`);
        return 1;
    }

    if (rows.length === 0) {
        // Not a failure. A register can legitimately hold no FIXED row, unlike the codex-source case where an
        // empty result is indistinguishable from the register having been rewritten to dodge the check.
        log('\nNo FIXED rows to verify.');
        return 0;
    }

    try {
        git(repoRoot, ['rev-parse', '--git-dir']);
    } catch (error) {
        warn(`git is not usable in ${repoRoot}: ${error.message}`);
        warn('Refusing rather than reporting a clean run over commits it never looked at.');
        return 2;
    }

    const shallow = git(repoRoot, ['rev-parse', '--is-shallow-repository']) === 'true';

    const tip = mainTip(repoRoot);
    if (tip === null) {
        warn(`neither refs/remotes/origin/main nor refs/heads/main resolves to a commit in ${repoRoot}.`);
        warn('This tool judges every cited sha as an ancestor of main, so with no main tip it cannot look.');
        warn('Refusing rather than reporting a clean run over an ancestry it never checked.');
        return 2;
    }

    const problems = [];
    for (const row of rows) {
        const evidence = FIXED_EVIDENCE.exec(row.evidence);
        if (!evidence) {
            // The shape rule in tools/tasks-register-check.mjs owns this failure; repeating it here would report
            // one defect twice. Skip it, because that check has already refused the commit.
            continue;
        }
        const sha = evidence[1];
        const why = evidence[2];

        // EVERY sha in the cell, not only the leading one. Found the hard way while converting the first four
        // rows: the leading sha was right and the prose cited `c6bf5cf` for a commit that is actually
        // `c6c6bbf5`. Validating position alone let a false citation through, and a wrong sha inside a sentence
        // reads as provenance exactly as strongly as the one the check verified. Inside a FIXED evidence cell a
        // hex run of seven or more characters is a commit reference by convention, so every one is resolved.
        // ALL-DIGIT TOKENS ARE NOT SHA CITATIONS. Measured the moment F-026 was filed: its reason names CI run ids
        // 30513399311 and 30513399301, which are 11 digits and therefore match [0-9a-f]{7,40} perfectly, so the
        // check demanded they resolve as commits. Run ids, issue numbers and byte counts all live in these
        // sentences. A short sha that happens to be all digits must be written out in full, which is a rule the
        // register states rather than a case the check silently mishandles.
        const cites = (why.match(/\b[0-9a-f]{7,40}\b/g) ?? []).filter(t => /[a-f]/.test(t) || t.length === 40);
        for (const cited of new Set(cites)) {
            const state = ancestryOfMain(repoRoot, cited, tip);
            if (state !== 'ancestor' && shallow) {
                warn(`${row.id} cites commit ${cited} in its reason, which does not resolve as an ancestor of ` +
                    `${tip.ref} in this SHALLOW clone.`);
                warn('That is "I cannot look", not "the commit is not on main". Check out with fetch-depth: 0.');
                return 2;
            }
            if (state === 'missing')
                problems.push(`line ${row.line} (${row.id}): the evidence cites ${cited}, which is not a commit ` +
                    'in this repository. A sha inside a sentence reads as provenance just as strongly as the one ' +
                    'the row leads with, so both are resolved.');
            else if (state === 'not-ancestor')
                problems.push(`line ${row.line} (${row.id}): the evidence cites ${cited}, which exists here but ` +
                    `is NOT an ancestor of ${tip.ref}. Every pull request is squash-merged, so a branch commit ` +
                    'never reaches main and survives only until its merged branch is deleted (F-080). Cite the ' +
                    "pull request's squash commit on main instead.");
        }

        // THE TEST HALF RUNS FIRST, AND UNCONDITIONALLY. It used to sit after the path checks, behind two
        // `continue`s, so a row declaring NOT THE NAMED FILE: skipped the test requirement entirely and a row
        // whose commit did not exist skipped it too. The two escape hatches COMPOSED into a full bypass: one
        // hatch turned the other off. An escape hatch that disables an unrelated rule is not an exception, it
        // is a hole, so nothing below may short-circuit this.
        // THE TEST HALF. Checked for every FIXED row, independently of the commit half, because a real commit
        // touching the right file still does not prove the defect cannot come back.
        const test = FIXED_TEST.exec(why);
        if (!test) {
            if (!why.includes(NO_TEST))
                problems.push(`line ${row.line} (${row.id}): FIXED names a commit but no test. A commit shows a ` +
                    'change; only a test shows the defect is gone and stays gone, which is golden rule 5. Add ' +
                    `"test: <path>::<name>", or record how the fix was proved instead as "${NO_TEST} <reason>".`);
            else if (!NO_TEST_ALLOWED.has(row.id))
                problems.push(`line ${row.line} (${row.id}): "${NO_TEST}" is not open to every row. ${row.id} is ` +
                    'not on the frozen list in tools/findings-fixed-commits.mjs, and a hatch any row can take is ' +
                    'not a hatch. If this finding genuinely cannot be covered by a test, add it to NO_TEST_ALLOWED ' +
                    `with its reason, which breaks a test and appears in review. Listed today: ${[...NO_TEST_ALLOWED.keys()].join(', ')}.`);
        } else {
            const [, testPath, testName] = [test[0], test[1], test[2].trim()];
            const suite = runningSuite(testPath);
            if (!suite) {
                problems.push(`line ${row.line} (${row.id}): ${testPath} is in no suite CI runs, so naming it ` +
                    'proves nothing. A test-shaped file that nothing executes is documentation. The suites that ' +
                    `run are: ${RUNNING_SUITES.map(s => s.pattern.source).join(' | ')}.`);
            } else {
                let contents = null;
                try { contents = readFileSync(resolve(repoRoot, testPath), 'utf8'); } catch { /* reported below */ }
                if (contents === null)
                    problems.push(`line ${row.line} (${row.id}): the named test file ${testPath} does not exist.`);
                else if (!contents.includes(testName))
                    problems.push(`line ${row.line} (${row.id}): ${testPath} exists but contains nothing matching ` +
                        `"${testName}". Naming a FILE is not naming a test, which is why the ::name half is ` +
                        'required and why it is looked for inside the file.');
                else
                    log(`  ${row.id}: covered by ${testPath}::${testName} (${suite.runner})`);
            }
        }

        const leadState = ancestryOfMain(repoRoot, sha, tip);
        if (leadState !== 'ancestor') {
            if (shallow) {
                warn(`${row.id} names commit ${sha}, which does not resolve as an ancestor of ${tip.ref}, and ` +
                    'the clone is SHALLOW.');
                warn('That is "I cannot look", not "the commit is not on main". Check out with fetch-depth: 0.');
                return 2;
            }
            problems.push(leadState === 'missing'
                ? `line ${row.line} (${row.id}): commit ${sha} does not exist in this repository. A ` +
                    'sha-shaped string is not a sha, and FIXED is the one verdict that asserts the defect is gone.'
                : `line ${row.line} (${row.id}): commit ${sha} exists here but is NOT an ancestor of ` +
                    `${tip.ref}. Every pull request is squash-merged, so a branch commit never reaches main and ` +
                    'survives only until its merged branch is deleted (F-080). This gate would go red the day ' +
                    "that branch goes; cite the pull request's squash commit on main instead.");
            continue;
        }

        const touched = new Set(git(repoRoot, ['show', '--name-only', '--format=', sha])
            .split('\n').map(p => p.trim()).filter(Boolean));
        const named = namedPaths(row.site);

        if (named.length === 0) {
            if (!why.includes(NOT_THE_NAMED_FILE))
                problems.push(`line ${row.line} (${row.id}): the file:line cell names no file this check can ` +
                    `read, so the touch assertion cannot run. Name a path, or say "${NOT_THE_NAMED_FILE} <why>".`);
            continue;
        }

        const hit = named.filter(p => touched.has(p));
        if (hit.length === 0 && !why.includes(NOT_THE_NAMED_FILE))
            problems.push(`line ${row.line} (${row.id}): commit ${sha} exists but touched none of the files this ` +
                `row names (${named.join(', ')}). A fix elsewhere is legitimate and must say so: write ` +
                `"${NOT_THE_NAMED_FILE} <the reason>" in the evidence. Passing in silence is how FIXED becomes ` +
                'as cheap to write as the fake SCHEDULED rows this register was built to stop.');
        else if (hit.length > 0)
            log(`  ${row.id}: ${sha} exists and touched ${hit.join(', ')}`);
        else
            log(`  ${row.id}: ${sha} exists; the row declares the fix was not in the named file, with a reason`);
    }

    if (problems.length > 0) {
        warn('\nA FIXED verdict does not hold up:');
        for (const problem of problems) warn(`  ${problem}`);
        return 1;
    }

    log(`\nEvery FIXED finding names a commit that is an ancestor of ${tip.ref}, and that touched what the row claims.`);
    return 0;
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
    const repoRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..');
    process.exit(main(process.argv[2] ?? resolve(repoRoot, 'docs/findings.md'), repoRoot));
}
