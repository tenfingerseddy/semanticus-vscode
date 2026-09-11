// THE HISTORY-DEPENDENT HALF of the finding queue's suite, and it lives here rather than in
// Semanticus.VSCode/test/ for a measured reason.
//
// These blocks resolve real commits with `git cat-file`, so they need real history. `npm test` auto-discovers
// everything in Semanticus.VSCode/test, and both build-and-smoke legs check out at the default depth of 1, so
// putting them there turned every run red with "the clone is SHALLOW" — which was the tool telling the truth
// about its environment, not a logic error. Run 30513399311 is that failure. An assertion belongs where its
// precondition holds, so it moved to the two jobs that check out with fetch-depth: 0.
//
// It is NOT auto-discovered by anything. Semanticus.VSCode/test/findings-register.test.mjs asserts that both
// workflows invoke this file by name, for the same reason the FINDINGS_SWEEP variable is asserted: a suite that
// nothing runs is indistinguishable from a suite that does not exist.
//
//   node tools/findings-fixed-commits.test.mjs

import assert from 'node:assert/strict';
import { mkdtempSync, writeFileSync, rmSync, readFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { execFileSync } from 'node:child_process';

const repoRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const toUrl = rel => `file://${resolve(repoRoot, rel).split('\\').join('/')}`;
const { fixedRows, namedPaths, runningSuite, verifySuites, NO_TEST_ALLOWED, main: mainFixed,
    mainTip, ancestryOfMain } = await import(toUrl('tools/findings-fixed-commits.mjs'));

// THE PRECONDITION, CHECKED FIRST AND NAMED. Without this the whole file fails as an opaque "2 !== 0" and the
// reader has to work out that the environment, not the code, is what changed.
{
    const shallow = execFileSync('git', ['rev-parse', '--is-shallow-repository'],
        { cwd: repoRoot, encoding: 'utf8' }).trim();
    assert.equal(shallow, 'false',
        'this suite resolves real commits, so it needs real history, and this clone is SHALLOW. Check out with ' +
        'fetch-depth: 0. It is not run by `npm test` precisely because build-and-smoke checks out at depth 1.');
}

const HEADER = [
    '| id | source | claim (one line) | file:line | verdict | evidence or task id | date |',
    '|----|--------|------------------|-----------|---------|---------------------|------|',
];
const REASON = 'A reason long enough to be a reason rather than a placeholder, which is all the floor checks.';
const row = (id, source, verdict, link, opts = {}) => `| ${id} | ${source} | ${opts.claim ?? 'A claim that is long enough to be a claim.'} | ${opts.site ?? 'some/file.cs:12'} | ${verdict} | ${link} | ${opts.date ?? '2026-07-30'} |`;
const findings = (...rows) => [...HEADER, ...rows].join(String.fromCharCode(10));

// The test half is asserted in its own block below. The commit-half fixtures declare it away so each one plants
// exactly one thing, which is the whole discipline of a plant-then-catch case.
// Synthetic ancestry/hatch fixtures use the still-allowed F-084 identifier.
// T203 gives the real F-011 row a CI-run regression, so it no longer needs an exemption.
const COVER = 'NO TEST: this fixture is exercising the commit half, so the test half is declared away on purpose. ';

// ---- 14c. FIXED: the SUBSTANCE, planted twice against the real repository ---------------------------
//
// The two failures the ruling asked for by name: a sha that does not exist, and a real sha that never touched
// the file the row claims. Both are run against this repository's actual history, because a fixture cannot
// prove that `git cat-file` was ever called.
{
    const dir = mkdtempSync(join(tmpdir(), 'fixed-'));
    try {
        const say = [];
        const run = text => {
            const p = join(dir, 'findings.md');
            writeFileSync(p, text);
            say.length = 0;
            const code = mainFixed(p, repoRoot, l => say.push(String(l)), l => say.push(String(l)));
            return { code, output: say.join('\n') };
        };

        // A real commit that really touched the file the row names. 2eaaf95d is #292, which rewrote the mirror
        // manifest; if this ever stops holding, the assertion below is the thing that should be re-measured.
        const real = run(findings(row('F-084', 'measured/PM', 'FIXED', '2eaaf95d it restored the dropped exclusion.'+COVER,
            { site: 'tools/release/mirror-manifest.json' })));
        assert.equal(real.code, 0, `a FIXED row naming a real commit that touched the named file must pass:\n${real.output}`);
        assert.match(real.output, /F-084: 2eaaf95d exists and touched tools\/release\/mirror-manifest\.json/u);

        // PLANT 1: a sha-shaped string that is not a sha.
        const ghost = run(findings(row('F-084', 'measured/PM', 'FIXED', 'deadbeefdeadbeef it claims a commit nobody made.'+COVER,
            { site: 'tools/release/mirror-manifest.json' })));
        assert.equal(ghost.code, 1, 'a FIXED row naming a non-existent commit passed');
        assert.match(ghost.output, /commit deadbeefdeadbeef does not exist in this repository/u);

        // PLANT 2: a real commit that never touched the file the row names.
        const wrong = run(findings(row('F-084', 'measured/PM', 'FIXED', '2eaaf95d it changed something else entirely.'+COVER,
            { site: 'Semanticus.Core/ChangeBus.cs' })));
        assert.equal(wrong.code, 1, 'a FIXED row whose commit never touched the named file passed');
        assert.match(wrong.output, /touched none of the files this row names \(Semanticus\.Core\/ChangeBus\.cs\)/u);

        // PLANT 3: a correct leading sha with a FALSE sha in the prose. This is not hypothetical: the first four
        // converted rows shipped with `c6bf5cf` in a reason where the commit is `c6c6bbf5`, and validating
        // position alone let it through. A sha inside a sentence reads as provenance just as strongly.
        const falseCitation = run(findings(row('F-084', 'measured/PM', 'FIXED',
            '2eaaf95d it restored the exclusion; the state before it was c6bf5cfa, measured.'+COVER,
            { site: 'tools/release/mirror-manifest.json' })));
        assert.equal(falseCitation.code, 1, 'a false sha in the prose passed because only the leading one was resolved');
        assert.match(falseCitation.output, /the evidence cites c6bf5cfa, which is not a commit in this repository/u);

        // And a TRUE second citation still passes, so the rule is not simply banning prose shas.
        const trueCitation = run(findings(row('F-084', 'measured/PM', 'FIXED',
            '2eaaf95d it restored the exclusion; the state before it was c6c6bbf5, measured at both.'+COVER,
            { site: 'tools/release/mirror-manifest.json' })));
        assert.equal(trueCitation.code, 0, `a correct second sha citation was rejected:\n${trueCitation.output}`);

        // The escape hatch works, and ONLY in these words. A fix elsewhere is legitimate; silence is not.
        const elsewhere = run(findings(row('F-084', 'measured/PM', 'FIXED',
            '2eaaf95d NOT THE NAMED FILE: the defect was in the caller, which this commit rewrote instead.'+COVER,
            { site: 'Semanticus.Core/ChangeBus.cs' })));
        assert.equal(elsewhere.code, 0, 'a declared fix-elsewhere was rejected despite carrying its reason');
        assert.match(elsewhere.output, /the row declares the fix was not in the named file, with a reason/u);

        // No FIXED rows is not a failure, unlike the codex-source case.
        assert.equal(run(findings(row('F-001', 'measured/PM', 'ACCEPTED', REASON))).code, 0);
    } finally {
        rmSync(dir, { recursive: true, force: true });
    }

    assert.deepEqual(namedPaths('tools/coverage-oracle.ps1:134'), ['tools/coverage-oracle.ps1']);
    assert.deepEqual(namedPaths('a/b.cs:1202,1208 (lane opens at :1102); c/d.json'), ['a/b.cs', 'c/d.json']);
    assert.deepEqual(namedPaths('docs/findings.md (the verdict set above)'), ['docs/findings.md']);
    assert.equal(fixedRows(findings(row('F-001', 'measured/PM', 'FIXED', '2eaaf95d a real fix here.'))).length, 1);
    assert.equal(fixedRows(findings(row('F-001', 'measured/PM', 'ACCEPTED', REASON))).length, 0);
}

// ---- 14d. FIXED must name a TEST, in a suite that actually runs -------------------------------------
//
// Sol's defeat: a commit cannot prove a defect is gone, only a test can. This is golden rule 5 of the project,
// not a new rule. So the four plants are: no test at all, a test file in no CI-run suite, a file that does not
// exist, and a name that is not inside the file it claims. Naming a FILE is not naming a test.
{
    const dir = mkdtempSync(join(tmpdir(), 'fixedtest-'));
    try {
        const say = [];
        const run = text => {
            const p = join(dir, 'findings.md');
            writeFileSync(p, text);
            say.length = 0;
            return { code: mainFixed(p, repoRoot, l => say.push(String(l)), l => say.push(String(l))), get output() { return say.join('\n'); } };
        };
        const fixedRow = evidence => findings(row('F-084', 'measured/PM', 'FIXED', evidence,
            { site: 'tools/release/mirror-manifest.json' }));

        const none = run(fixedRow('2eaaf95d it restored the dropped exclusion and nothing else.'));
        assert.equal(none.code, 1, 'FIXED passed while naming no test at all');
        assert.match(none.output, /names a commit but no test.*golden rule 5/su);

        // The mirror matrix now runs through T204. Use the real fulfillment selftest,
        // which no workflow or extension test invokes, for the unrun-file refusal.
        // Its named case exists, so an absent-case error cannot hide a widened suite match.
        const unrun = run(fixedRow(
            '2eaaf95d it restored the exclusion. test: tools/fulfillment/worker/selftest.mjs::keygen produced a keypair'));
        assert.equal(unrun.code, 1, 'a test file in no CI-run suite was accepted as cover');
        assert.match(unrun.output, /tools\/fulfillment\/worker\/selftest\.mjs is in no suite CI runs/u);

        // THE OTHER DIRECTION, and the correction this block used to have backwards. The gate battery WAS the
        // example above, because CI ran it nowhere (F-024). [T209] gave it the `ci-gate-battery` job, which runs
        // it by name on every non-draft ref and which `gate` requires, so a row naming one of its cases is real
        // cover. Refusing it would call a case CI genuinely executes unrun, and that refusal is what the two
        // assertions here watch. The named case is a real label in that file; the verifier looks for it inside
        // the file, so a renamed case fails here rather than passing quietly.
        const battery = run(fixedRow(
            '2eaaf95d it restored the exclusion. test: tools/ci-gate/gate-battery.mjs::ci-gate-battery red on a CODE change'));
        assert.equal(battery.code, 0,
            `a case in the gate battery, which CI now runs in its own required job, was refused as cover:\n${battery.output}`);
        assert.match(battery.output,
            /covered by tools\/ci-gate\/gate-battery\.mjs::ci-gate-battery red on a CODE change \(node tools\/ci-gate\/gate-battery\.mjs in the ci-gate-battery job/u,
            'the battery row passed without being attributed to the job that actually runs it');

        const ghost = run(fixedRow('2eaaf95d it restored it. test: Semanticus.VSCode/test/does-not-exist.test.mjs::whatever'));
        assert.equal(ghost.code, 1, 'a named test file that does not exist was accepted');
        assert.match(ghost.output, /named test file Semanticus\.VSCode\/test\/does-not-exist\.test\.mjs does not exist/u);

        // The target is a DIFFERENT test file on purpose. Pointing this plant at THIS file made it pass, because
        // the fixture string was then literally present in the file being searched. That is a real limit of a
        // substring lookup and it is recorded in the tool: a file that MENTIONS a test name satisfies the check.
        // It still cannot be satisfied by naming a file and nothing else, which is the fake it exists to stop.
        const absent = 'zzz-no-such-assertion-in-the-task-register-file';
        const wrongName = run(fixedRow(`2eaaf95d it restored it. test: Semanticus.VSCode/test/tasks-register.test.mjs::${absent}`));
        assert.equal(wrongName.code, 1, 'a test NAME absent from the named file was accepted');
        assert.match(wrongName.output, /contains nothing matching "zzz-no-such-assertion/u);

        // The declared hatch, and a real test, both pass. F-084 is on the frozen NO_TEST list.
        assert.equal(run(fixedRow('2eaaf95d it restored it. NO TEST: nothing asserts the exclusion set today, and adding the guard is T203.')).code, 0);
        assert.equal(run(fixedRow('2eaaf95d it restored it. test: Semanticus.VSCode/test/findings-register.test.mjs::FIXED: the shape rule')).code, 0);

        // PLANT: NO TEST: taken by a row that is NOT on the frozen list. A hatch any row can take is not a hatch,
        // it is the default. Adding one has to be an edit to the list that breaks this test and appears in review.
        const unlisted = run(findings(row('F-099', 'measured/PM', 'FIXED',
            '2eaaf95d it fixed something. NO TEST: it would be inconvenient to write one for this.',
            { site: 'tools/release/mirror-manifest.json' })));
        assert.equal(unlisted.code, 1, 'an unlisted row took the NO TEST hatch, so the hatch is the default');
        assert.match(unlisted.output, /F-099 is not on the frozen list/u);
        // F-014 and F-086 were removed here by [T209]. Both gave the same reason: their only cover is a case in
        // tools/ci-gate/gate-battery.mjs, and that battery ran in no CI job. The ci-gate-battery job runs it
        // now, so the reason is false in this source and an exemption resting on a false reason is a hole
        // rather than an exception. Both have a real battery case to cite instead.
        //
        // F-087 DELIBERATELY REMAINS. Its subject is the removal of a v* tag trigger, and nothing automated
        // covers that: the battery only comments that tags are absent, and ci-gate-path.test.mjs asserts
        // nothing about triggers. Inventing a test to retire the exemption would be a test written to satisfy
        // a gate. This list is meant to shrink when a reason stops being true, not whenever it is convenient.
        assert.deepEqual([...NO_TEST_ALLOWED.keys()], ['F-135', 'F-084', 'F-087', 'F-117', 'F-126', 'F-127', 'F-129', 'F-133', 'F-141', 'F-142', 'F-148'],
            'the NO TEST allowlist changed; every entry must be a deliberate, reviewed exception with a reason');
        for (const [, why] of NO_TEST_ALLOWED)
            assert.ok(why.length > 40, 'a NO TEST exception carries no real reason');

        // PLANT: THE TWO HATCHES COMPOSED INTO A FULL BYPASS. `NOT THE NAMED FILE:` used to `continue` before the
        // test requirement was ever reached, so one hatch switched the other off. F-084 is allowed NO TEST, but it
        // is NOT allowed to skip the test rule by declaring the path one, and this row declares only the path one.
        const composed = run(findings(row('F-084', 'measured/PM', 'FIXED',
            '2eaaf95d NOT THE NAMED FILE: the fix landed in a caller, so no named path was touched.',
            { site: 'somewhere in the calling code, no readable path' })));
        assert.equal(composed.code, 1, 'NOT THE NAMED FILE: still switches off the test requirement');
        assert.match(composed.output, /names a commit but no test/u,
            'the composed bypass failed for the wrong reason, so the reordering is not what fixed it');

        // A row with no readable path AND no test declares both, and only then passes.
        assert.equal(run(findings(row('F-084', 'measured/PM', 'FIXED',
            '2eaaf95d NOT THE NAMED FILE: the fix landed in a caller. NO TEST: nothing covers the manifest today, the guard is T203.',
            { site: 'somewhere in the calling code, no readable path' }))).code, 0);
    } finally {
        rmSync(dir, { recursive: true, force: true });
    }

    assert.equal(runningSuite('Semanticus.Tests/DualDriveTests.cs')?.runner.includes('dotnet test'), true);
    assert.equal(runningSuite('tools/ci-gate/gate-battery.mjs')?.runner.includes('ci-gate-battery job'), true,
        'the gate battery has run in its own required CI job since [T209], so it must now count as a suite');
    // F-140. This file is invoked by ci.yml in finding-queue and by tasks-register.yml, so a FIXED row must be
    // able to cite it. It matched no pattern until that entry was added, which made CI refuse rows pointing at a
    // suite CI was itself running. An end-to-end plant is deliberately NOT added for it: pointing a plant at the
    // file being searched passes on the fixture's own text, as this file records above, so the registration is
    // what is asserted here and the sabotage controls are what prove the assertion bites.
    assert.equal(runningSuite('tools/findings-fixed-commits.test.mjs')?.runner.includes('finding-queue job'), true,
        'ci.yml runs this file, so a FIXED row naming one of its cases must not be refused as unrun');
    // F-143. The private mirror gate is reached through the manifest and the wrapper rather than a `run:`
    // line, so it needs its own entry before any row can cite one of its cases.
    assert.equal(runningSuite('tools/release/mirror-public.test.mjs')?.runner.includes('release-mirror-gate'), true,
        'the manifest-declared private gate is spawned under npm test, so its cases are citable');
    assert.equal(runningSuite('tools/fulfillment/worker/selftest.mjs'), null,
        'a real selftest that no CI job runs must still not count as a suite');
    assert.equal(runningSuite('docs/findings.md'), null);

    // EVERY ALLOWLIST ENTRY IS BOUND TO A REAL INVOCATION. The `runner` field was descriptive prose at first, so a
    // suite that nothing executes could be listed and believed, which is a comment asserting an invariant the code
    // does not hold. verifySuites reads each entry's proof file and looks for the exact command string.
    assert.deepEqual(verifySuites(repoRoot), [],
        'a running-suite allowlist entry cannot be bound to a command in ci.yml or package.json, so it is prose');

    // F-145, PLANTED IN BOTH DIRECTIONS. The mirror-gate entry is the one whose proof is a chain, so it is
    // the one that can rot without any needle disappearing. Copy the four real proof files into a temp tree,
    // strip exactly one execution link, and require verifySuites to refuse. Stripping the CALL and stripping
    // the SPAWN are separate plants, because either one alone leaves the suite dormant.
    {
        const { mkdtempSync: mk, writeFileSync: write, mkdirSync: md, rmSync: rm } = await import('node:fs');
        const proofFiles = [
            '.github/workflows/ci.yml',
            'Semanticus.VSCode/scripts/run-tests.mjs',
            'Semanticus.VSCode/test/release-mirror-gate.test.mjs',
            'tools/release/mirror-manifest.json',
        ];
        for (const [label, needle] of [['the test never calls the helper', 'runMirrorGate(repoRoot)'],
            ['the helper never spawns the gate', 'spawnSync(process.execPath, [join(root, rel)]']]) {
            const dir = mk(join(tmpdir(), 'mirrorproof-'));
            try {
                for (const rel of proofFiles) {
                    const text = readFileSync(resolve(repoRoot, rel), 'utf8');
                    const out = join(dir, rel);
                    md(dirname(out), { recursive: true });
                    write(out, rel.endsWith('release-mirror-gate.test.mjs') ? text.split(needle).join('/* removed */') : text);
                }
                const problems = verifySuites(dir);
                assert.ok(problems.some(p => p.includes(needle)),
                    `verifySuites accepted a mirror-gate chain where ${label}, so the entry outlives its execution`);
            } finally { rm(dir, { recursive: true, force: true }); }
        }
    }

    // The failing direction, watched: point an entry's proof at a file that cannot contain its command.
    {
        const { mkdtempSync: mk, writeFileSync: write, rmSync: rm } = await import('node:fs');
        const empty = mk(join(tmpdir(), 'suites-'));
        try {
            write(join(empty, 'package.json'), '{}');
            const problems = verifySuites(empty);
            assert.ok(problems.length >= 4,
                'verifySuites found nothing wrong in a directory containing no workflows at all, so it is not reading');
            assert.ok(problems.some(p => /does not contain|cannot be read/u.test(p)));
        } finally { rm(empty, { recursive: true, force: true }); }
    }
}

// ---- 14e. REACHABLE IS NOT ON MAIN: a present commit that is no ancestor of main must refuse ---------
//
// The [T218] defect, planted rather than remembered: every pull request here is squash-merged, so a branch
// commit EXISTS in a full clone (`git cat-file -e` passes) while being an ancestor of nothing on main, and
// it evaporates the day its merged branch is deleted — which #303's --delete-branch did to three cited
// commits, turning main red (F-078, F-080). The plant is a commit object minted with `git commit-tree` and
// referenced by no ref at all: cat-file resolves it, ancestry must refuse it. Watched red against the
// existence-based tool before the ancestry rule landed.
{
    const emptyTree = execFileSync('git', ['mktree'], { cwd: repoRoot, encoding: 'utf8', input: '' }).trim();
    const stray = execFileSync('git',
        ['commit-tree', emptyTree, '-m', 'a commit that exists and is an ancestor of nothing, minted by findings-fixed-commits.test.mjs'],
        { cwd: repoRoot, encoding: 'utf8',
          env: { ...process.env,
              GIT_AUTHOR_NAME: 'plant', GIT_AUTHOR_EMAIL: 'plant@test', GIT_AUTHOR_DATE: '2026-01-01T00:00:00Z',
              GIT_COMMITTER_NAME: 'plant', GIT_COMMITTER_EMAIL: 'plant@test', GIT_COMMITTER_DATE: '2026-01-01T00:00:00Z' } }).trim();

    // The precondition of the plant, asserted so a failure below cannot be blamed on the wrong thing: the
    // stray commit really does exist here, and really is not an ancestor of main.
    execFileSync('git', ['cat-file', '-e', `${stray}^{commit}`], { cwd: repoRoot });
    const tip = mainTip(repoRoot);
    assert.ok(tip !== null, 'neither origin/main nor main resolves in this clone, so ancestry cannot be judged');
    assert.equal(ancestryOfMain(repoRoot, stray, tip), 'not-ancestor',
        'the minted stray commit judged as something other than not-ancestor, so the plant itself is broken');
    assert.equal(ancestryOfMain(repoRoot, 'deadbeefdeadbeef', tip), 'missing');
    assert.equal(ancestryOfMain(repoRoot, tip.sha, tip), 'ancestor', 'the main tip is an ancestor of itself');

    const dir = mkdtempSync(join(tmpdir(), 'fixedanc-'));
    try {
        const say = [];
        const run = text => {
            const p = join(dir, 'findings.md');
            writeFileSync(p, text);
            say.length = 0;
            return { code: mainFixed(p, repoRoot, l => say.push(String(l)), l => say.push(String(l))), get output() { return say.join('\n'); } };
        };

        // PLANT 1: the stray commit as the LEADING sha. Existence used to pass this; ancestry must not.
        const lead = run(findings(row('F-084', 'measured/PM', 'FIXED', `${stray} it claims a fix from a branch that never reached main.`+COVER,
            { site: 'tools/release/mirror-manifest.json' })));
        assert.equal(lead.code, 1, 'a FIXED row leading with a commit that exists but is no ancestor of main passed');
        assert.match(lead.output, /exists here but is NOT an ancestor of/u);
        assert.match(lead.output, /squash-merged/u, 'the refusal does not explain the squash-merge trap');

        // PLANT 2: the stray commit MID-SENTENCE behind a good leading sha. Both positions are resolved, so
        // both must take the ancestry rule; validating the lead alone is the F-026-era hole in a new suit.
        const cited = run(findings(row('F-084', 'measured/PM', 'FIXED',
            `2eaaf95d it restored the exclusion; first attempted on the branch as ${stray}, measured.`+COVER,
            { site: 'tools/release/mirror-manifest.json' })));
        assert.equal(cited.code, 1, 'a non-ancestor sha in the prose passed because only the leading one was judged');
        assert.match(cited.output, /the evidence cites [0-9a-f]{40}, which exists here but is NOT an ancestor of/u);

        // CONTROL: a true ancestor still passes, so the tightening did not simply ban everything.
        const good = run(findings(row('F-084', 'measured/PM', 'FIXED', '2eaaf95d it restored the dropped exclusion.'+COVER,
            { site: 'tools/release/mirror-manifest.json' })));
        assert.equal(good.code, 0, `an ancestor-of-main commit was refused by the ancestry rule:\n${good.output}`);
    } finally {
        rmSync(dir, { recursive: true, force: true });
    }
}

// ---- 14f. A SHADOW REF MUST NOT STEAL THE TIP: qualified lookup, planted in a synthetic repository -----
//
// Sol's must-fix on 8369f90b: git's SHORT-ref lookup prefers refs/heads/ and refs/tags/ over refs/remotes/,
// so a local branch literally named `origin/main`, or a tag named `main`, would win a short lookup, and a
// dead branch commit could then PASS ancestry against the impostor — the one unacceptable direction. The
// plant lives in a SYNTHETIC throwaway repository, never in this one: a test that writes a shadow branch
// into the real repo and crashes before cleanup leaves the trap armed for every later run. Watched red
// first: under the short-ref lookup the stray commit judged as an ancestor (a dead commit passing).
{
    const tmp = mkdtempSync(join(tmpdir(), 'shadowref-'));
    try {
        // stderr is piped, not inherited: proving the shadow wins the SHORT lookup makes git print
        // "warning: refname 'origin/main' is ambiguous", which is the plant working, not a problem to show.
        const g = args => execFileSync('git', args, { cwd: tmp, encoding: 'utf8',
            stdio: ['pipe', 'pipe', 'pipe'],
            env: { ...process.env,
                GIT_AUTHOR_NAME: 'plant', GIT_AUTHOR_EMAIL: 'plant@test', GIT_AUTHOR_DATE: '2026-01-01T00:00:00Z',
                GIT_COMMITTER_NAME: 'plant', GIT_COMMITTER_EMAIL: 'plant@test', GIT_COMMITTER_DATE: '2026-01-01T00:00:00Z' } }).trim();
        g(['init', '--quiet', '--initial-branch', 'work']);
        const emptyTree = execFileSync('git', ['mktree'], { cwd: tmp, encoding: 'utf8', input: '' }).trim();
        const realMain = g(['commit-tree', emptyTree, '-m', 'the real main tip']);
        const stray = g(['commit-tree', emptyTree, '-m', 'a stray commit that must never be judged the tip']);
        g(['update-ref', 'refs/remotes/origin/main', realMain]);

        // PLANT 1: a local BRANCH named origin/main, pointing at the stray. Prove the shadow actually
        // shadows — the short name must resolve to the stray, or this block tests nothing.
        g(['update-ref', 'refs/heads/origin/main', stray]);
        assert.equal(g(['rev-parse', '--verify', 'origin/main^{commit}']), stray,
            'the shadow branch did not win the short-ref lookup, so the plant proves nothing on this git');
        let tip = mainTip(tmp);
        assert.equal(tip?.ref, 'refs/remotes/origin/main');
        assert.equal(tip?.sha, realMain, 'a local branch named origin/main stole the tip from the remote ref');
        assert.equal(ancestryOfMain(tmp, stray, tip), 'not-ancestor',
            'the stray commit judged as an ancestor, so a dead commit would PASS — the unacceptable direction');
        assert.equal(ancestryOfMain(tmp, realMain, tip), 'ancestor');

        // PLANT 2: the FALLBACK path. Remove the remote ref so refs/heads/main is the tip, and plant a TAG
        // named main at the stray; tags beat heads in short-ref lookup, and must lose to the qualified form.
        g(['update-ref', '-d', 'refs/remotes/origin/main']);
        g(['update-ref', '-d', 'refs/heads/origin/main']);
        g(['update-ref', 'refs/heads/main', realMain]);
        g(['tag', 'main', stray]);
        assert.equal(g(['rev-parse', '--verify', 'main^{commit}']), stray,
            'the shadow tag did not win the short-ref lookup, so the plant proves nothing on this git');
        tip = mainTip(tmp);
        assert.equal(tip?.ref, 'refs/heads/main');
        assert.equal(tip?.sha, realMain, 'a tag named main stole the tip from the local branch');
        assert.equal(ancestryOfMain(tmp, stray, tip), 'not-ancestor');

        // CONTROL: with every main-shaped ref gone the tool refuses rather than guessing a tip.
        g(['update-ref', '-d', 'refs/heads/main']);
        g(['tag', '-d', 'main']);
        assert.equal(mainTip(tmp), null, 'with no main ref at all, mainTip invented a tip instead of refusing');
    } finally {
        rmSync(tmp, { recursive: true, force: true });
    }
}

console.log('FIXED substance: a commit must be an ANCESTOR OF MAIN and touch what the row claims, a test must');
console.log('                 exist in a suite CI runs, and neither escape hatch can switch the other off.');
