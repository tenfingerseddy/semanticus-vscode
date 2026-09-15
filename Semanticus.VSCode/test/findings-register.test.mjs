// THE FINDING QUEUE'S GATE, and the reason it is a gate rather than a document.
//
// Two things are asserted here, and the second one is the load-bearing one:
//   1. EVERY invariant is planted and caught. A gate whose failure path has never been observed may be
//      refusing nothing. Each block below writes a register that violates exactly one rule and asserts that
//      the check fails AND names it. A check that fails without saying why trains people to re-run it.
//   2. THE REAL REGISTERS PASS. This file is discovered by `npm test`, which runs inside build-and-smoke,
//      which is what the required `gate` context reports. Without the live assertion at the bottom, the
//      invariants would be enforced only by an unrequired sibling workflow, and a green required check over a
//      broken register is exactly the lie this queue exists to stop.
//
// The ONLINE assertion is not here: enumerating live Codex comment ids needs the network and a token, so it
// lives in tools/findings-codex-coverage.mjs and runs in the Task register workflow. Its offline half (parsing
// the rowed comment ids out of the register) IS asserted here, because that part is deterministic.

import assert from 'node:assert/strict';
import { existsSync, mkdtempSync, writeFileSync, rmSync } from 'node:fs';
import { test } from 'node:test';
import { tmpdir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const repoRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..', '..');
const privateManifest = resolve(repoRoot, 'tools/release/mirror-manifest.json'); // mirror-optional: private register suite
if (!existsSync(privateManifest)) {
    test('private finding registers and policy records', { skip: 'private records are excluded from this source snapshot' }, () => {});
} else {

const toUrl = rel => `file://${resolve(repoRoot, rel).replace(/\\/g, '/')}`;
const { analyseFindings, checkFindings, mainFindings, taskIndex, VERDICTS } = await import(toUrl('tools/tasks-register-check.mjs'));
const { rowedCodexIds, sweepFloor, QUEUE_BIRTH_PR, claimOverlap, sitePaths, distinctiveTokens, mergedPullRequests } =
    await import(toUrl('tools/findings-codex-coverage.mjs'));
const { fixedRows, namedPaths, runningSuite, verifySuites, NO_TEST_ALLOWED, main: mainFixed } =
    await import(toUrl('tools/findings-fixed-commits.mjs'));

const HEADER = [
    '| id | source | claim (one line) | file:line | verdict | evidence or task id | date |',
    '|----|--------|------------------|-----------|---------|---------------------|------|',
];

const REASON = 'A reason long enough to be a reason rather than a placeholder, which is all the floor checks.';

const row = (id, source, verdict, link, opts = {}) => `| ${id} | ${source} | ${opts.claim ?? 'A claim that is long enough to be a claim.'} | ${opts.site ?? 'some/file.cs:12'} | ${verdict} | ${link} | ${opts.date ?? '2026-07-30'} |`;

const findings = (...rows) => [...HEADER, ...rows].join('\n');

// A tasks register with one live task that back-references F-001, one task whose row is in a bold done state,
// one whose state cannot be read, and one that exists only as a dated DONE-log citation (which is what a
// shipped task looks like here: the definition row is deliberately gone).
const TASKS = [
    '- [T195] A live task. Filed as F-001 in docs/findings.md.',
    '        More detail on the continuation line.',
    '- [T196] A live task with no back-reference at all.',
    '- [T197] Something else — **MERGED 2026-07-24 as #265. Filed as F-001 in docs/findings.md.',
    '- [T198] A row whose state cannot be read: PROCESS DEFECT, NOW CLOSED AT SOURCE. Filed as F-001 here.',
    '',
    '## DONE — append-only log (newest first)',
    '- 2026-07-22 [T099] shipped it.',
].join('\n');

const fails = (text, tasks = TASKS) => checkFindings(text, tasks).failures;
const holds = (text, tasks = TASKS) => {
    const failures = fails(text, tasks);
    assert.deepEqual(failures, [], 'a register that violates nothing must produce no failures');
};

// ---- 0. the clean case, so every plant below is a difference of exactly one thing --------------------
const CLEAN = findings(
    row('F-001', 'measured/PM', 'SCHEDULED', '[T195]'),
    row('F-002', 'codex/PR#292/3676328713', 'ACCEPTED', REASON),
    row('F-003', 'sol/review-2026-07-30', 'REFUTED', REASON),
);
holds(CLEAN);

// ---- 1. a duplicate finding id ----------------------------------------------------------------------
{
    const planted = fails(findings(
        row('F-001', 'measured/PM', 'ACCEPTED', REASON),
        row('F-001', 'measured/PM', 'ACCEPTED', REASON),
    ));
    assert.equal(planted.length, 1, 'a duplicate finding id was not caught');
    assert.match(planted[0], /F-001 is defined 2 times, on lines 3, 4/u, 'the duplicate was caught without naming it');
}

// ---- 2. a verdict outside the closed set, and OPEN specifically --------------------------------------
{
    for (const bogus of ['OPEN', 'WONTFIX', 'open', 'Scheduled', 'DEFERRED', '']) {
        const planted = fails(findings(row('F-001', 'measured/PM', bogus, REASON)));
        assert.ok(planted.some(f => /is not in the closed set/u.test(f)),
            `verdict "${bogus}" was accepted, so the set is not closed`);
    }
    // OPEN is the one that was deliberately deleted, so its rejection is pinned by name.
    const open = fails(findings(row('F-001', 'measured/PM', 'OPEN', 'owner: Kane, and a reason of real length here.')));
    assert.ok(open.some(f => /There is no OPEN state/u.test(f)), 'OPEN was rejected without saying it was deleted on purpose');
    // OPEN stays deleted even though FIXED was later added. The two are not the same decision: an owner cannot
    // be verified by CI, and a commit can. Pinning both together is what stops FIXED being read as a licence to
    // put OPEN back.
    assert.ok(!VERDICTS.includes('OPEN'), 'OPEN came back into the closed set');
}

// ---- 3. a SCHEDULED row pointing at a task that is DONE ---------------------------------------------
{
    // (a) the shape a shipped task actually has here: no definition row, only a dated DONE-log citation.
    const gone = fails(findings(row('F-001', 'measured/PM', 'SCHEDULED', '[T099]')));
    assert.ok(gone.some(f => /names T99, which has no definition row/u.test(f)),
        'a finding scheduled against a shipped task was not caught');

    // (b) a row still in the live list but explicitly in a done state.
    const done = fails(findings(row('F-001', 'measured/PM', 'SCHEDULED', '[T197]')));
    assert.ok(done.some(f => /is in a\s+DONE state/u.test(f)),
        'a finding scheduled against a task marked done in place was not caught');

    // (c) a state this check cannot read fails as unreadable, not as a pass. Guessing here would be the
    //     same defect as a check that reports success over the part of a file it happens to understand.
    const ambiguous = fails(findings(row('F-001', 'measured/PM', 'SCHEDULED', '[T198]')));
    assert.ok(ambiguous.some(f => /cannot tell whether/u.test(f)),
        'an unreadable task state passed silently');
}

// ---- 4. a SCHEDULED row whose task does not back-reference it ---------------------------------------
{
    const planted = fails(findings(row('F-001', 'measured/PM', 'SCHEDULED', '[T196]')));
    assert.equal(planted.length, 1, 'exactly one rule should have fired');
    assert.match(planted[0], /does not mention F-001.*BIDIRECTIONAL/su,
        'a one-way pointer satisfied SCHEDULED, which makes "scheduled" fakeable');

    // And a task that does not exist at all is a different message, because it is a different mistake.
    const missing = fails(findings(row('F-001', 'measured/PM', 'SCHEDULED', '[T999]')));
    assert.ok(missing.some(f => /has no definition row in TASKS.md/u.test(f)));
}

// ---- 5. a SCHEDULED row whose link cell is not exactly one task id ----------------------------------
{
    for (const bad of ['', 'T195', 'soon', '[T195] and [T196]', 'see [T195]', REASON]) {
        const planted = fails(findings(row('F-001', 'measured/PM', 'SCHEDULED', bad)));
        assert.ok(planted.some(f => /needs its link cell to be exactly one task id/u.test(f)),
            `SCHEDULED accepted the link cell "${bad}"`);
    }
}

// ---- 6. ACCEPTED with an empty or fake reason, and REFUTED with no evidence -------------------------
{
    const empty = fails(findings(row('F-001', 'measured/PM', 'ACCEPTED', '')));
    assert.ok(empty.some(f => /ACCEPTED requires a non-empty reason/u.test(f)), 'ACCEPTED passed with an empty reason');

    for (const placeholder of ['n/a', 'TBD', 'none', '-', 'pending']) {
        const planted = fails(findings(row('F-001', 'measured/PM', 'ACCEPTED', placeholder)));
        assert.ok(planted.some(f => /requires a non-empty reason/u.test(f)), `ACCEPTED passed with "${placeholder}"`);
    }

    const short = fails(findings(row('F-001', 'measured/PM', 'ACCEPTED', 'not worth it')));
    assert.ok(short.some(f => /below the 40-character floor/u.test(f)), 'ACCEPTED passed with a reason under the floor');

    const asTask = fails(findings(row('F-001', 'measured/PM', 'ACCEPTED', '[T195]')));
    assert.ok(asTask.some(f => /carries a task id where its reason belongs/u.test(f)),
        'a terminal verdict pointing at future work was accepted');

    const refuted = fails(findings(row('F-001', 'measured/PM', 'REFUTED', '')));
    assert.ok(refuted.some(f => /REFUTED requires a non-empty evidence/u.test(f)), 'REFUTED passed with no evidence');
}

// ---- 7. a codex source that is not keyed on a comment id -------------------------------------------
//
// THE DEFEAT THIS EXISTS FOR. Keyed on the pull-request number alone, one row satisfies the check for twenty
// findings and nothing can see the other nineteen.
{
    for (const bad of ['codex/PR#292', 'codex/292/1', 'codex/PR#292/', 'codex/PR#292/abc', 'codex']) {
        const planted = fails(findings(row('F-001', bad, 'ACCEPTED', REASON)));
        assert.ok(planted.some(f => /Identity is per FINDING, not per PR/u.test(f)),
            `the source "${bad}" was accepted, so a whole review can hide in one row`);
    }
    // A non-codex source is free text on purpose, and that limit is stated in the register itself.
    holds(findings(row('F-001', 'measured/PM', 'ACCEPTED', REASON)));
    holds(findings(row('F-001', 'codex/PR#292/3676328713', 'ACCEPTED', REASON)));
}

// ---- 8. an empty source, claim, site or a malformed date --------------------------------------------
{
    assert.ok(fails(findings(row('F-001', '', 'ACCEPTED', REASON))).some(f => /source cell is empty/u.test(f)));
    assert.ok(fails(findings(row('F-001', 'measured/PM', 'ACCEPTED', REASON, { claim: 'too short' })))
        .some(f => /too short to be a claim/u.test(f)));
    assert.ok(fails(findings(row('F-001', 'measured/PM', 'ACCEPTED', REASON, { site: '' })))
        .some(f => /file:line cell is empty/u.test(f)));
    for (const date of ['30-07-2026', 'today', '2026-7-30', ''])
        assert.ok(fails(findings(row('F-001', 'measured/PM', 'ACCEPTED', REASON, { date }))).some(f => /is not YYYY-MM-DD/u.test(f)),
            `the date "${date}" was accepted`);
}

// ---- 9. a row with the wrong cell count fails rather than being reparsed ----------------------------
//
// A pipe inside a claim shifts every cell right, so the verdict column would be read from the wrong text. A
// silently reparsed row is worse than a rejected one.
{
    const planted = fails(findings('| F-001 | measured/PM | a claim with a | pipe in it | some/file.cs:1 | SCHEDULED | [T195] | 2026-07-30 |'));
    assert.ok(planted.some(f => /has 8 cells, not 7/u.test(f)), 'a row with the wrong cell count was reparsed instead of rejected');
}

// ---- 10. PARTIAL BLINDNESS fails, rather than passing over the rows it can read ---------------------
//
// The same rule the task register learned: an id in leading position that the row rule cannot parse means the
// check is only partly reading the file, and reporting a clean run over that part is the worse outcome.
{
    const planted = fails([...HEADER, row('F-001', 'measured/PM', 'ACCEPTED', REASON), '- F-002 filed as a bullet, not a table row.'].join('\n'));
    assert.ok(planted.some(f => /could not parse it, so the check is only partly reading the register/u.test(f)),
        'a finding written in a shape the parser cannot read was silently ignored');

    const { rows, unrecognised } = analyseFindings(['| F-001 | a | b | c | SCHEDULED | [T195] | 2026-07-30 |', '* F-002 also invisible'].join('\n'));
    assert.equal(rows.length, 1, 'the readable row must still be counted while the unreadable one is reported');
    assert.equal(unrecognised.length, 1);
    assert.equal(unrecognised[0].id, 'F-002');
}

// ---- 11. TOTAL blindness fails ---------------------------------------------------------------------
{
    const planted = fails('# A register with the table deleted.\n\nNothing here at all.');
    assert.ok(planted.some(f => /No finding rows found at all/u.test(f)), 'an empty register reported success');
}

// ---- 12. the task index reads state and back-references the way the failures claim ------------------
{
    const index = taskIndex(TASKS);
    assert.ok(index.has(195) && index.has(196) && index.has(197) && index.has(198));
    assert.ok(!index.has(99), 'a dated DONE-log citation was treated as a task definition');
    assert.match(index.get(195).block, /F-001/u, 'the block must include continuation lines, where a back-reference may sit');
    assert.doesNotMatch(index.get(196).block, /F-001/u, 'the block leaked into the next task row');
}

// ---- 13. mainFindings turns each verdict into the right exit code -----------------------------------
//
// checkFindings was the only thing under test once, which left the entry point, the only place a verdict
// becomes an exit code, unverified. `if (failures.length > 0)` becoming `if (false)` would keep every
// assertion above green while the gate stopped failing.
{
    const dir = mkdtempSync(join(tmpdir(), 'findings-'));
    try {
        const say = [];
        const run = (findingsText, tasksText = TASKS) => {
            const f = join(dir, 'findings.md');
            const t = join(dir, 'TASKS.md');
            writeFileSync(f, findingsText);
            writeFileSync(t, tasksText);
            say.length = 0;
            const code = mainFindings(f, t, l => say.push(String(l)), l => say.push(String(l)));
            return { code, output: say.join('\n') };
        };

        const clean = run(CLEAN);
        assert.equal(clean.code, 0, 'a clean register must exit 0');
        assert.match(clean.output, /finding rows:\s+3/u);
        assert.match(clean.output, /SCHEDULED:\s+1/u);
        assert.match(clean.output, /every SCHEDULED row is/u);

        const broken = run(findings(row('F-001', 'measured/PM', 'SCHEDULED', '[T196]')));
        assert.equal(broken.code, 1, 'mainFindings returned 0 for a register that violates an invariant');
        assert.match(broken.output, /does not mention F-001/u, 'the exit code failed but the report did not name it');
        assert.doesNotMatch(broken.output, /every SCHEDULED row is/u, 'success text printed alongside a failing exit code');
    } finally {
        rmSync(dir, { recursive: true, force: true });
    }
}

// ---- 14. the online tool's offline half: which comment ids the register claims ----------------------
{
    const parsed = rowedCodexIds(findings(
        row('F-001', 'codex/PR#292/3676328713', 'ACCEPTED', REASON),
        row('F-002', 'codex/PR#285/3669626634', 'REFUTED', REASON),
        row('F-003', 'codex/PR#285/3669626638', 'ACCEPTED', REASON),
        row('F-004', 'measured/PM', 'ACCEPTED', REASON),
    ));
    assert.deepEqual([...parsed.keys()].sort((a, b) => a - b), [285, 292]);
    assert.deepEqual([...parsed.get(285).keys()].sort(), ['3669626634', '3669626638']);
    // The whole row is carried now, not just the id: the claim and the site cell are what the live comment is
    // compared against, and discarding them was the defect Sol landed.
    assert.equal(parsed.get(292).get('3676328713').id, 'F-001');
    assert.equal(typeof parsed.get(292).get('3676328713').claim, 'string');
    assert.equal(typeof parsed.get(292).get('3676328713').site, 'string');
}

// ---- 14b. FIXED: the shape rule ---------------------------------------------------------------------
//
// FIXED was added after the three-state set was proved wrong in use. It is the one verdict that asserts a
// defect is GONE, so its evidence is a commit rather than prose, and it is verified rather than read.
{
    const good = row('F-001', 'measured/PM', 'FIXED', '2eaaf95d it deleted the paths-ignore this row was about.');
    holds(findings(good));

    for (const bad of ['', 'fixed in #292', 'see 2eaaf95d for the fix', 'ZZZZZZZ it looks like hex but is not', REASON])
        assert.ok(fails(findings(row('F-001', 'measured/PM', 'FIXED', bad))).some(f => /must open its evidence cell with the commit sha/u.test(f)),
            `FIXED accepted the evidence "${bad}", so a verdict that claims a defect is gone named no change`);

    // A sha and then silence is not a row anyone can read without running git.
    assert.ok(fails(findings(row('F-001', 'measured/PM', 'FIXED', '2eaaf95d done'))).some(f => /then says nothing/u.test(f)));

    // FIXED needs no task, and must not be confused with SCHEDULED: it is terminal.
    assert.deepEqual(VERDICTS, ['SCHEDULED', 'FIXED', 'ACCEPTED', 'REFUTED'], 'the closed set changed without this test changing');
}

// The test half is asserted separately in 14d. These fixtures declare it away so each one plants exactly
// one thing, which is the whole discipline of a plant-then-catch case.
const COVER = 'NO TEST: this fixture is exercising the commit half, so the test half is declared away on purpose. ';

// ---- 14e2. THE WORKFLOW ACTUALLY ENABLES THE SWEEP --------------------------------------------------
//
// THE HIGHEST-VALUE ASSERTION IN THIS FILE, and it exists because the bug it catches shipped. `ci.yml` ran
// findings-codex-coverage.mjs without FINDINGS_SWEEP, so the frozen floor and the merged-pull-request
// enumeration never ran in CI: the required job enumerated only the pull requests the register already named,
// which is vacuous against the failure it exists to catch. An agent omits every row for a newly merged pull
// request, the register names nothing, and a check that reads only the register finds nothing missing.
//
// A feature enabled only by a variable nobody sets is indistinguishable from a feature that is not there, so
// the INVOCATION is under test, not just the tool. Deleting the env line from the workflow fails here by name.
{
    const { readFileSync: read } = await import('node:fs');
    const ci = read(resolve(repoRoot, '.github/workflows/ci.yml'), 'utf8');

    const steps = ci.split(/^      - name: /m);
    const sweepSteps = steps.filter(s => s.includes('findings-codex-coverage.mjs'));
    assert.ok(sweepSteps.length >= 1,
        'ci.yml does not run tools/findings-codex-coverage.mjs at all, so the required gate never enumerates ' +
        'the live Codex comments. That is defeat 5 all over again.');

    for (const step of sweepSteps)
        assert.match(step, /FINDINGS_SWEEP:\s*'1'/,
            "a ci.yml step runs findings-codex-coverage.mjs WITHOUT FINDINGS_SWEEP: '1'. Without it the tool " +
            'enumerates only the pull requests the register already names, so an omitted review is invisible and ' +
            'the required job passes vacuously. This is the bug that shipped in the first version of that job.');

    // And the tool must still be the thing that refuses when the variable is set but the floor is wrong, which is
    // asserted in 14f. Here we only pin that the switch is thrown.
    assert.ok(ci.includes('GITHUB_TOKEN: ${{ github.token }}'),
        'the sweep step has no token, so it would exit 2 in CI on every run');

    // THE SPLIT SUITE MUST STILL BE INVOKED. The history-dependent FIXED assertions live in
    // tools/findings-fixed-commits.test.mjs, which nothing auto-discovers, because `npm test` runs in a job that
    // checks out at depth 1 and they need real history. A suite nothing runs is indistinguishable from a suite
    // that does not exist, which is the same reasoning as the FINDINGS_SWEEP assertion above, so both workflows
    // that check out with fetch-depth: 0 must name it.
    const registerWf = read(resolve(repoRoot, '.github/workflows/tasks-register.yml'), 'utf8');
    for (const [name, text] of [['ci.yml', ci], ['tasks-register.yml', registerWf]])
        assert.ok(text.includes('node tools/findings-fixed-commits.test.mjs'),
            `${name} does not run tools/findings-fixed-commits.test.mjs, so the FIXED verdict's failure paths are ` +
            'never exercised. It is not auto-discovered anywhere, on purpose.');

    // And every job that runs it must have real history, or it fails honestly on every run.
    for (const [name, text] of [['ci.yml', ci], ['tasks-register.yml', registerWf]]) {
        const jobs = text.split(/^  [a-z][a-z0-9-]*:$/m).filter(j => j.includes('findings-fixed-commits'));
        assert.ok(jobs.length >= 1, `${name} has no job running the FIXED tooling`);
        for (const job of jobs)
            assert.match(job, /fetch-depth:\s*0/,
                `a job in ${name} runs the FIXED tooling without fetch-depth: 0. It resolves real commits, so a ` +
                'shallow clone makes it refuse, which is what run 30513399311 did on both build-and-smoke legs.');
    }
}

// ---- 14f. the sweep floor is FROZEN, not a dial -----------------------------------------------------
//
// Sol's defeat: any value at or above 295 passed, so an agent could advance the floor past reviews it had
// omitted and stay green. That is declaring the backlog handled without handling it. The birth pull request is
// now a constant and the register must EQUAL it, so moving it is a code change that breaks this test.
{
    const { readFileSync: read } = await import('node:fs');
    assert.equal(QUEUE_BIRTH_PR, 295, 'the queue birth pull request moved; that must be a deliberate, reviewed change');
    const floor = sweepFloor(read(resolve(repoRoot, 'docs/findings.md'), 'utf8')); // mirror-optional: this suite runs only with the private manifest
    assert.equal(floor, QUEUE_BIRTH_PR,
        `docs/findings.md says sweep-from-pr: ${floor} while the frozen birth pull request is ${QUEUE_BIRTH_PR}. ` +
        'Raising the floor hides every omitted review below it.');
    assert.equal(sweepFloor('no floor anywhere in this text'), null, 'a missing floor must read as null, never as zero');
    // A raised floor is caught by inequality, which is the whole point of freezing it.
    assert.notEqual(sweepFloor('sweep-from-pr: 400'), QUEUE_BIRTH_PR, 'a raised floor was treated as equal to the frozen one');
}

// ---- 14g. the row is bound to its LIVE comment, not to itself ---------------------------------------
//
// Sol's defeat 1 and 3, which are one defect: the row supplied the thing it was judged against. Only the claim's
// LENGTH and the site cell's non-emptiness were checked, while the live comment's body and path were fetched and
// discarded. The path check is EXACT and the claim check is a HEURISTIC, and the difference is stated wherever
// it appears rather than blurred.
{
    // EXACT. Verified against the real bodies of the seven live comments on #285, #292 and #293 in the sweep
    // measurements: sitePaths must surface the comment's own path out of a cell that also carries line numbers
    // and prose, or the binding would fail on honest rows.
    assert.deepEqual(sitePaths('Semanticus.VSCode/test/attribution-and-license.test.mjs:90 (Codex anchored it here); .github/workflows/ci.yml:14-25 (the subject, as reviewed)'),
        ['Semanticus.VSCode/test/attribution-and-license.test.mjs', '.github/workflows/ci.yml']);
    // Extensionless slash-bearing tokens surface too: `Semanticus.VSCode/NOTICE` used to be dropped, and
    // `.githooks/pre-commit` (a REAL Codex anchor on #305) was permanently unbindable under the old
    // extension-only rule. Every consumer asks includes(), so a stray extra path cannot fake a binding.
    assert.deepEqual(sitePaths('THIRD-PARTY-NOTICES.md:5; Semanticus.VSCode/NOTICE'),
        ['THIRD-PARTY-NOTICES.md', 'Semanticus.VSCode/NOTICE']);
    assert.deepEqual(sitePaths('.githooks/pre-commit:150'), ['.githooks/pre-commit']);
    assert.deepEqual(sitePaths('no path at all here'), []);

    // HEURISTIC. The floor is 4, measured: the seven real rows score 7 to 14 against their own comment bodies,
    // and three separately written hollow claims score 2, 0 and 2. Hyphen splitting and plural stripping are
    // load-bearing, not cosmetic: without them a faithful paraphrase of #293's comment scored 1.
    const body = 'Count the existing heading-based task definition. When another row allocates `T165`, this regex ' +
        'ignores the existing live definition at `TASKS.md:472` because it is written as `## [T165]`, not a bullet.';
    const faithful = 'P1: the task-register check counts only bullet definitions, so a heading-form allocation of ' +
        'an id is invisible to the duplicate rule and can collide with a bullet unseen.';
    assert.ok(claimOverlap(faithful, body) >= 4,
        `a faithful paraphrase scored ${claimOverlap(faithful, body)}, below the floor: the floor would reject honest rows`);
    for (const hollow of [
        'A claim that is long enough to be a claim about something in this repository.',
        'Codex raised a point on this pull request and it has been considered carefully here.',
        'A minor style nit in a test file, not worth acting on for the current release.',
    ])
        assert.ok(claimOverlap(hollow, body) < 4, `a hollowed claim scored ${claimOverlap(hollow, body)}, at or above the floor`);

    // The badge markup every Codex comment opens with must not contribute tokens, or every claim would score
    // free points for the words in an image URL.
    const badge = '**<sub><sub>![P1 Badge](https://img.shields.io/badge/P1-orange?style=flat)</sub></sub>**';
    assert.equal(distinctiveTokens(badge).size, 0, 'the badge markup contributed tokens, inflating every overlap');

    // The register rows carry the comment path they are bound to. Pinned here so a row cannot be weakened to a
    // path the fixing commit merely happened to touch without this failing offline as well as online.
    const { readFileSync: read } = await import('node:fs');
    const live = rowedCodexIds(read(resolve(repoRoot, 'docs/findings.md'), 'utf8')); // mirror-optional: this suite runs only with the private manifest
    const anchors = {
        3669626634: 'Semanticus.VSCode/test/attribution-and-license.test.mjs',
        3669626638: 'THIRD-PARTY-NOTICES.md',
        3669626642: 'Semanticus.VSCode/test/attribution-and-license.test.mjs',
        3669626647: 'Semanticus.VSCode/test/attribution-and-license.test.mjs',
        3669626649: 'Semanticus.VSCode/test/attribution-and-license.test.mjs',
        3676328713: 'tools/release/mirror-public.test.mjs',
        3677593577: 'tools/tasks-register-check.mjs',
        3679929366: 'tools/findings-codex-coverage.mjs',
        3681051154: 'Semanticus.Engine/LocalEngine.cs',
        3681051166: 'Semanticus.Analysis/Rules/BPARules-PowerBI.json',
        3681051170: 'Semanticus.Analysis/Bpa.cs',
        3681051178: 'Semanticus.Analysis/ReadinessAnalyzer.cs',
        // All three #299 comments were left on TASKS.md, because that is where the ratification was
        // written. The files each one is ABOUT are named second in its site cell, which is why the row
        // has to keep the anchor as well as the subject: the anchor is what binds it to the review.
        3687514927: 'TASKS.md',
        3687514929: 'TASKS.md',
        3687514932: 'TASKS.md',
        // Left on PLAN.md, but about the two strategy headers that contradict it. Same pattern as the
        // three above: the anchor is where the reviewer stood, not the whole subject of the finding.
        3688381707: 'docs/PLAN.md',
        // PR #302. Both were left unread when that pull request was merged, which is how main went red a
        // second time in one day. The anchors are where the reviewer stood; each row names its subject too.
        3690678510: 'docs/progress.html',
        3690678518: 'TASKS.md',
        // PR #304. A blanket find-and-replace rewrote a sha that was doing a different job in the same
        // file: evidence in three rows, and the identity of the failed commit in a fourth.
        3691203625: 'docs/findings.md',
        // PR #307: the lone carriage return that broke the table render while every check stayed green.
        3693097724: 'docs/findings.md',
        // PR #308, the CI cost trim: the broken gate battery and the double-billed release tags.
        3693214313: '.github/workflows/ci.yml',
        3693214316: '.github/workflows/ci.yml',
        // PR #309: F-085's first citation named a test that could not catch a lone CR.
        3693464580: 'docs/findings.md',
        // PR #310: the scanner's own header still claimed CR was unconditionally legal.
        3693589923: 'Semanticus.VSCode/test/source-byte-hygiene.test.mjs',
        // PR #311, the taxonomy: shelf order lost to an alphabetical pre-sort, and the planning records.
        3693594789: 'Semanticus.VSCode/webview/src/workflowdesign.tsx',
        3693594791: 'Semanticus.Engine/OpTaxonomy.cs',
        // PR #314: an explicitly declared import that cannot be read fell into the optional-absence continue.
        3800514518: 'Semanticus.VSCode/test/attribution-and-license.test.mjs',
        // PR #321, the A3 step-2 plan: ratification, init metadata, a durable prerequisite and extension metadata.
        3886552838: 'docs/kernel-a/A3-public-api-baseline-plan.md',
        3886552840: 'docs/kernel-a/A3-B1-red-first-build-design.md',
        3886552841: 'docs/kernel-a/A3-B1-red-first-build-design.md',
        3886552845: 'docs/kernel-a/A3-B1-red-first-build-design.md',
        // PR #323: the no-match mutation stopped at the detector's own positive control before the enforcer probe.
        3886622578: 'tools/release/mirror-public.test.mjs',
        // PR #324, the T193 decision: the remote positive control and the full IPv4 loopback range.
        3886673141: 'docs/notes/T193-localhost-open-live-decision.md',
        3886673144: 'docs/notes/T193-localhost-open-live-decision.md',
        // PR #325: shipped finding tasks stayed in NEXT and T209 still named the shipped T197 obstacle.
        3886668056: 'TASKS.md',
        3886668059: 'TASKS.md',
        // PR #326: T182 note still pointed at 2.5 for the visitor command and still asked to add it.
        3886690936: 'docs/notes/T182-walker-api-boundary.md',
        // PR #329: the moved DONE entries must preserve the log's newest-first order.
        3887090569: 'TASKS.md',
        // PR #330: the same test must also keep those shipped task definitions out of NEXT.
        3887169510: 'docs/findings.md',
        3887169728: 'docs/findings.md',
        // PR #331: recovery's skipped-token flush copies existing leading trivia without a per-item poll.
        3887999991: 'Semanticus.Dax/Syntax/Parsing/Parser.cs',
        // PR #332: the plan-verify gate tests miss every unverified rung except missing evidence.
        3888097669: 'Semanticus.Tests/PlanVerifyGateTests.cs',
        // PR #334: an unavailable live Fabric lane still carried a PASS headline.
        3888613720: 'Semanticus.CicdSmoke/Program.cs',
        // PR #338: the checklist links a stale RC publication procedure.
        3888932792: 'RELEASE-CHECKLIST.md',
        // PR #343: the current readiness note retained a completed prerequisite as pending.
        3940646566: 'docs/notes/T-3835-canvas-runner-readiness.md',
        // PR #340: preserve a URI authority before the loopback refusal.
        3888973374: 'Semanticus.Engine/LocalEngine.cs',
        3940806794: 'Semanticus.Engine/WorkflowParser.cs',
        3941710459: 'Semanticus.Engine/LocalEngine.Workflows.cs',
        3941710461: 'Semanticus.Engine/LocalEngine.Workflows.cs',
        // PR #339: three CommonMark boundaries in the task-register allocation scanner.
        3888940004: 'tools/tasks-register-check.mjs',
        3888940008: 'tools/tasks-register-check.mjs',
        3888940010: 'tools/tasks-register-check.mjs',
        // PR #347/#348: named spaces and list-relative fence indentation.
        3942433047: 'tools/tasks-register-check.mjs',
        3942471148: 'tools/tasks-register-check.mjs',
        3942471150: 'tools/tasks-register-check.mjs',
        3942594408: 'TASKS.md',
        3942594409: 'docs/notes/T229-delivery-check.md',
        3942722273: 'docs/findings.md',
        3942722275: 'docs/notes/flock-next-comparison-scope.md',
        // PR #353: typed BPA fix eligibility and quoted-literal decoding.
        3943553688: 'Semanticus.Analysis/Bpa.cs',
        3943553690: 'Semanticus.Analysis/Bpa.cs',
        // PR #354: the unknown-scope finding was closed before its full correction.
        3943677082: 'docs/findings.md',
        3943805199: 'docs/notes/v1-delivery-receipt.md',
        3943909748: 'Semanticus.Engine/McpTools.cs',
        3943909752: 'Semanticus.Engine/McpTools.cs',
        3944119313: 'Semanticus.Analysis/ReadinessRules.cs',
        3944119317: 'Semanticus.Analysis/ReadinessRules.cs',
        3944119323: 'Semanticus.Analysis/ReadinessRules.cs',
        3944119330: 'docs/ai-readiness-catalog.json',
        3944273935: '.github/workflows/ci.yml',
        // PR #303, the review of the fix for those two. BOTH sit on docs/findings.md, which is unusual and
        // worth saying: this is the register reviewing its own rows, so the anchor and the subject coincide
        // for once. One is about the durability of the commit those rows cite, the other about the tests
        // they name not covering what they claim.
        3690866434: 'docs/findings.md',
        3690866441: 'docs/findings.md',
        // PR #301, the unscoped review of the workflow format v2 slice. All three sit on the parser.
        3689113632: 'Semanticus.Engine/WorkflowParser.cs',
        3689113637: 'Semanticus.Engine/WorkflowParser.cs',
        3689113646: 'Semanticus.Engine/WorkflowParser.cs',
        // PR #306: the reader-first ruling recorded in the register while the plan and the tracker said
        // the old thing. Anchored on TASKS.md, where the reviewer stood.
        3692886041: 'TASKS.md',
        // PR #305: both on the pre-commit hook. Rowed ahead of that merge on the [T218] branch, because
        // #305 merges after this train and must find its comments already consumed.
        3692885928: '.githooks/pre-commit',
        3692885934: '.githooks/pre-commit',
        // PR #359: the mirror gate wiring. Anchored on the new wrapper, which is where the reviewer stood;
        // the row also names tools/release/mirror-public.test.mjs, which is the file whose claims went stale.
        3944371090: 'Semanticus.VSCode/test/release-mirror-gate.test.mjs',
        // PR #361: left on the F-141 row itself, which is where the reviewer stood. The row also
        // names tools/release/mirror-public.mjs, the file that still carried the claim.
        3945249307: 'docs/findings.md',
        // PR #362: on the release line itself, which is where the reviewer stood and where the
        // residual count still is.
        3946211229: 'tools/release/mirror-public.mjs',
        // PR #363: left on the F-019 row as it went FIXED, which is where the lost signal was.
        3946403065: 'docs/findings.md',
        // PR #364, both on my own work in that pull request: the suite proof, and the F-144 link.
        3946476587: 'tools/findings-fixed-commits.mjs',
        3946476585: 'docs/findings.md',
        3953831350: 'Semanticus.VSCode/webview/src/workflowruns.mjs',
        3954229762: 'Semanticus.Engine/WorkflowParser.cs',
        3954266910: 'Semanticus.VSCode/webview/src/workflowdocument.tsx',
        3954266912: 'Semanticus.VSCode/webview/src/workflowlayout.mjs',
        3954382477: 'Semanticus.VSCode/webview/src/workflowlayout.mjs',
        3954382480: 'Semanticus.Engine/WorkflowUpgrade.cs',
        3998351876: 'Semanticus.VSCode/package.json',
        3998351878: 'Semanticus.VSCode/scripts/assistant-pack.mjs',
        4000562048: 'docs/redesign/task-briefs/UX01.md',
        4000562051: 'tools/render-redesign-progress.py',
        4000562055: 'tools/render-redesign-progress.py',
        4000562058: 'docs/redesign/task-briefs/UX16.md',
    };
    for (const [pr, rows_] of live)
        for (const [commentId, r] of rows_) {
            const anchor = anchors[commentId];
            assert.ok(anchor, `#${pr} comment ${commentId} is rowed but not pinned here; add its live anchor path`);
            assert.ok(sitePaths(r.site).includes(anchor),
                `${r.id} names ${sitePaths(r.site).join(', ')} but its comment is anchored on ${anchor}`);
        }
}

// ---- 14e. no workflow comment claims a paths-ignore that no workflow declares --------------------
//
// This is F-009's cover, and it exists because a comment asserting an invariant the repository does not hold is
// a defect in itself. `.github/workflows/tasks-register.yml` justified living outside ci.yml by citing
// `paths-ignore: '**/*.md'` on ci.yml; #294 deleted paths-ignore outright and the justification silently became
// a false statement about this repository.
//
// THE RULE HAD TO BE SHARPENED, and the first version of it is worth recording because it was wrong in an
// instructive way: "no comment may mention paths-ignore" fired on the CORRECTED header, which mentions it in
// order to say it is gone. A rule that cannot tell "used to say X" from "asserts X" would push authors toward
// deleting the history rather than recording it. So the rule is: a comment may mention paths-ignore only if one
// is actually declared, OR the mention is marked as past. Explaining a deletion stays legal; asserting a
// mechanism that no longer exists does not.
{
    const { readFileSync: read, readdirSync } = await import('node:fs');
    const dir = resolve(repoRoot, '.github/workflows');
    const workflows = readdirSync(dir).filter(n => n.endsWith('.yml') || n.endsWith('.yaml'));
    assert.ok(workflows.length >= 2, 'no workflows found, so this assertion would pass by looking at nothing');

    const PAST = /used to|no longer|is gone|was gone|deleted|removed|replaced/i;

    // Declarations only: a real `paths-ignore:` key, never the words inside a comment.
    const declares = text => text.split(/\r?\n/).some(l => !/^\s*#/.test(l) && /^\s*paths-ignore\s*:/.test(l));
    const commentsOf = text => text.split(/\r?\n/).filter(l => /^\s*#/.test(l)).join('\n');

    const judge = (name, text, anyDeclared) => {
        const commentary = commentsOf(text);
        if (!/paths-ignore/.test(commentary)) return null;
        if (anyDeclared) return null;
        return PAST.test(commentary) ? null : name;
    };

    const anyDeclared = workflows.some(n => declares(read(join(dir, n), 'utf8')));
    for (const name of workflows) {
        const offender = judge(name, read(join(dir, name), 'utf8'), anyDeclared);
        assert.equal(offender, null,
            `${name} has a comment asserting a paths-ignore is in force, and no workflow in this repository ` +
            'declares one, and the mention is not marked as past. That is F-009: a confident comment claiming ' +
            'an invariant the code does not hold.');
    }

    // THE FAILING DIRECTION, watched rather than assumed. The exact sentence that was live before this branch,
    // judged by the same function: it mentions paths-ignore, nothing declares one, and it is written in the
    // present tense, so it must be caught.
    const oldHeader = [
        "# THIS DELIBERATELY DOES NOT LIVE IN ci.yml. That workflow carries `paths-ignore: '**/*.md'` so a",
        '# documents-only pull request buys no run at all, which is precisely the shape of pull request that breaks',
        '# this invariant. A check that is skipped by the changes most likely to break it is not a check.',
        'on:',
        '  push:',
        '    branches: [main]',
    ].join('\n');
    assert.equal(judge('the-old-header.yml', oldHeader, false), 'the-old-header.yml',
        'the sentence this finding was actually about is not caught, so this test proves nothing');

    // And a real declaration makes the same mention legal again, because then the comment is true.
    assert.equal(judge('with-a-real-one.yml', oldHeader, true), null);
}

// ---- 14h. THE SWEEP CANNOT AGE A FINDING OUT --------------------------------------------------------
//
// Codex's finding on #296, comment 3679929366, anchored on the enumeration line itself: the swept set was
// filtered by a 14-day merge window, so a merged pull request above the floor whose Codex comment nobody
// rowed simply LEFT the swept set on day fifteen. The required gate then went green having proved nothing,
// which directly contradicts the invariant this whole queue rests on: an omission turns `main` red and KEEPS
// it red. This is the one defect family this project keeps shipping, a check that switches itself off, and
// here TIME was the switch, so no reviewer and no test had to be fooled for it to happen.
//
// The window is gone rather than widened, because any window is the same bug with a longer fuse. Enumeration
// is now every merged pull request above the frozen birth floor, and the walk stops at the floor by NUMBER.
// That stop is only sound while the API returns creation order descending, so both the ordering it asks for
// and the monotonicity it relies on are asserted here instead of trusted.
{
    const OLD = '2026-01-05T00:00:00Z';     // far outside any 14-day window, and it only ages further
    const pr = (number, merged_at = OLD) => ({ number, merged_at });

    const asked = [];
    // A page source, injected. It supplies the pages, NOT the verdict: the thing under test is which numbers
    // survive enumeration, which is the tool's own logic and not something this fixture can hand it.
    const pages = list => async path => {
        asked.push(path);
        const page = Number((/[?&]page=([0-9]+)/.exec(path) ?? [])[1] ?? 1);
        return list[page - 1] ?? [];
    };

    // (a) THE CASE CODEX NAMED, and the one that must go red before the fix. Two merged pull requests above
    //     the frozen floor, both months old. Under the 14-day filter this set came back EMPTY.
    const swept = await mergedPullRequests(QUEUE_BIRTH_PR, pages([[
        pr(297), pr(296), pr(295), pr(294),
    ]]));
    assert.deepEqual([...swept].sort((a, b) => a - b), [296, 297],
        `the sweep enumerated ${JSON.stringify(swept)} instead of [296,297]: a merged pull request above the ` +
        'frozen floor aged out, so an unrowed finding turns invisible and the required gate goes green having ' +
        'proved nothing. Time must never be able to clear a finding.');

    // (b) THE ORDERING THE FLOOR STOP DEPENDS ON. sort=updated puts an old pull request touched today first,
    //     so numbers do not descend and a stop at the floor would truncate the set silently.
    assert.ok(asked.length >= 1, 'nothing was requested, so this case proves nothing');
    assert.match(asked[0], /sort=created/, 'the sweep no longer asks for creation order, but stopping at the ' +
        'floor by number is only sound while the numbers descend monotonically');
    assert.match(asked[0], /direction=desc/, 'the sweep asks for ascending order, which starts the walk at the ' +
        'oldest pull request in the repository and stops it immediately at the floor');
    assert.ok(!/[?&](since|days)=/.test(asked[0]), 'a time parameter came back into the enumeration request');

    // (c) THE FLOOR STILL HOLDS. It is the ONLY thing that may exclude a pull request, because it is frozen in
    //     code and a reviewer can see it move. Nothing at or below it is swept.
    assert.deepEqual(await mergedPullRequests(QUEUE_BIRTH_PR, pages([[pr(295), pr(294), pr(293)]])), [],
        'the floor stopped excluding the historical backlog, which lands main permanently red on day one');

    // (d) A CLOSED-BUT-UNMERGED pull request is not a merge and carries no obligation.
    assert.deepEqual(await mergedPullRequests(QUEUE_BIRTH_PR, pages([[pr(299, null), pr(298), pr(295)]])), [298],
        'a closed pull request that never merged was swept, or a real merge beside it was dropped');

    // (e) PAGINATION EXHAUSTION REFUSES. A full page every time and the floor never reached is the one case
    //     where the old code fell out of its loop and RETURNED what it had, reporting full coverage over the
    //     pull requests it never read. Same family as the aged-out finding: quietly deciding it had looked.
    let descending = 999999;
    const endless = async () => Array.from({ length: 100 }, () => pr(descending--));
    await assert.rejects(() => mergedPullRequests(QUEUE_BIRTH_PR, endless), /without reaching the floor/,
        'the sweep truncated a history it could not finish walking instead of refusing, which reports full ' +
        'coverage over pull requests nobody enumerated');

    // (f) OUT-OF-ORDER PAGES REFUSE rather than stopping early. If the API ever stops returning creation order
    //     the floor stop would cut the walk short, and a short walk is the aged-out bug by another route.
    await assert.rejects(() => mergedPullRequests(QUEUE_BIRTH_PR, pages([[pr(297), pr(299)]])), /creation order/,
        'pull requests arriving out of order were accepted, so the floor stop can truncate the sweep silently');

    // (g) NO TIME ARITHMETIC LEFT IN THE SOURCE. The fix is the removal, so the removal is what is pinned:
    //     re-adding a window is a diff that fails here by name rather than a quiet regression.
    const { readFileSync: read } = await import('node:fs');
    const tool = read(resolve(repoRoot, 'tools/findings-codex-coverage.mjs'), 'utf8');
    for (const banned of ['SWEEP_DAYS', '24 * 60 * 60 * 1000', 'merged_at) >='])
        assert.ok(!tool.includes(banned),
            `tools/findings-codex-coverage.mjs contains ${JSON.stringify(banned)}, so a time window is back in ` +
            'the enumeration and a finding nobody rows can age out of the sweep again.');
}

// ---- 15. THE LIVE REGISTERS. This is what makes it a gate rather than a unit test. ------------------
{
    const { readFileSync } = await import('node:fs');
    const live = checkFindings(
        readFileSync(resolve(repoRoot, 'docs/findings.md'), 'utf8'), // mirror-optional: this suite runs only with the private manifest
        readFileSync(resolve(repoRoot, 'TASKS.md'), 'utf8'), // mirror-optional: this suite runs only with the private manifest
    );
    assert.deepEqual(live.failures, [], `docs/findings.md does not hold:\n  ${live.failures.join('\n  ')}`);
    assert.ok(live.rows.length >= 20, `the register has ${live.rows.length} rows; it was seeded with 22 and rows are never deleted`);
    for (const r of live.rows) assert.ok(VERDICTS.includes(r.verdict));
}

// ---- THE RATIFICATION MUST STAY RATIFIED. -----------------------------------------------------------
// Codex found the same fault three times on #299 and it is the reason F-033, F-034 and F-035 exist: a
// decision was ratified in one place while the canonical places still said the old thing, so a coordinator
// reading the register kept treating settled work as blocked. Prose cannot hold that on its own, because
// nothing fails when it drifts back. These three sections are what makes the ratification checkable, and
// each finding row names the one that covers it.
const readDoc = async rel => {
    const { readFileSync } = await import('node:fs');
    return readFileSync(resolve(repoRoot, rel), 'utf8');
};

// ---- 15a. THE T195 T197 DONE RECORDS STAY NEWEST FIRST (covers F-103) -------------------------------
{
    const tasks = await readDoc('TASKS.md');
    const doneStart = tasks.indexOf('## DONE');
    const beforeDone = tasks.slice(0, doneStart);
    const done = tasks.slice(doneStart);
    const positions = ['[T195]', '[T196]', '[T197]'].map(id => done.indexOf(id));
    for (const id of ['T195', 'T196', 'T197'])
        assert.ok(!beforeDone.includes(`- [${id}]`),
            `${id} is shipped and must not have a live definition before the DONE log`);
    assert.ok(positions.every(position => position >= 0),
        'the shipped T195, T196 and T197 records must all remain in the append-only DONE log');
    assert.ok(positions[0] < positions[1] && positions[1] < positions[2],
        'the T195, T196 and T197 DONE records are not newest first by their #322, #320 and #318 merge order');
}

// A task's definition is its `- [Tn]` line plus every line up to the next definition. Scoping to the row
// matters: "OPEN DECISION FOR KANE" is legitimate prose elsewhere in a register that still has open
// decisions, so a whole-file search would either be vacuous or fail on somebody else's honest row.
const taskRow = (tasks, id) => {
    const lines = tasks.split('\n');
    const start = lines.findIndex(l => l.startsWith(`- [${id}]`));
    assert.notEqual(start, -1, `TASKS.md has no definition row for ${id}, so its ratified status cannot be checked`);
    let end = start + 1;
    while (end < lines.length && !/^- \[T\d+\]/.test(lines[end])) end++;
    return lines.slice(start, end).join('\n');
};

// ---- 15b. THE T209 ROW NAMES ONLY THE CURRENT CI GAP (covers F-102) -------------------------------
{
    const tasks = await readDoc('TASKS.md');
    const t209 = taskRow(tasks, 'T209');
    assert.ok(t209.includes('ci-gate-battery') && t209.includes('docs/notes/T209-ci-delivery.md'),
        'T209 must name the implemented CI job and its current delivery evidence');
    assert.ok(!t209.includes('Fix T197 first') && !t209.includes('fails from PowerShell'),
        'T209 still presents the already-fixed T197 PowerShell problem as live work');
}

// ---- 16. THE T166 AND T167 ROWS STAY RATIFIED IN THE CANONICAL REGISTER (covers F-033) ---------------
{
    const tasks = await readDoc('TASKS.md');
    for (const id of ['T166', 'T167']) {
        const row = taskRow(tasks, id);
        assert.ok(!row.includes('OPEN DECISION FOR KANE'),
            `TASKS.md's ${id} row still reads "OPEN DECISION FOR KANE". Kane answered both halves on ` +
            '2026-07-31, and this register is canonical, so a coordinator reading it will treat ratified ' +
            'work as blocked.');
        assert.ok(row.includes('RATIFIED BY KANE 2026-07-31'),
            `TASKS.md's ${id} row does not carry the ratification. Removing the old status is only half ` +
            'the fix: the row has to say what was decided.');
    }
    // The waiting list is the other half of F-033. Item 18 used to say the pair blocks both lanes and the
    // whole next milestone, which is the sentence that actually stops work.
    assert.ok(/ANSWERED BY KANE 2026-07-31/.test(tasks),
        'the waiting-on-Kane list does not record that [T166] and [T167] were answered, so the pair still ' +
        'reads as a live blocker even though both rows are ratified.');
}

// ---- 17. THE PLAN DOCUMENT STAYS ON THE RATIFIED LANE ORDER (covers F-034) ---------------------------
{
    const plan = await readDoc('docs/PLAN.md');
    assert.ok(plan.includes('The ratified forward order (Kane, 2026-07-31)'),
        'docs/PLAN.md no longer carries the ratified forward order. It is the designated lane-status ' +
        'document, so anyone following it would schedule the superseded programme.');
    assert.ok(/SUPERSEDED as the forward order/.test(plan),
        'docs/PLAN.md does not mark the 2026-07-10 eight-step sequence as superseded, so two different ' +
        'forward programmes both read as governing and a builder can honestly pick either.');
    // Kane's 2026-08-01 ruling: the YAML reader slice ([T217]) runs BEFORE the run engine. Both have now
    // moved, so the lane-status document must say the reader is DONE before it names [T220] as the runner.
    // Covers F-082, the third instance of the recorded-in-one-place family.
    assert.ok(/YAML reader slice \[T217\] is DONE[\s\S]{0,240}\[T220\] runner/.test(plan),
        'docs/PLAN.md does not record the delivered YAML reader before the active T220 runner work. Kane ' +
        'ratified reader-first on 2026-08-01, and the lane record must preserve that order after both move.');
    // The precise regression, in the exact shape it shipped in: a bolded lane heading claiming the linear
    // Workflows designer is the thing being built now. It shipped long ago, so the claim was false twice.
    const buildingNow = plan.split('\n').filter(l => l.includes('NOW BUILDING') && l.includes('Remaining in this lane'));
    assert.deepEqual(buildingNow, [],
        'docs/PLAN.md advertises a lane as NOW BUILDING in the heading form that claimed the linear ' +
        `Workflows designer was the forward item: ${buildingNow.join(' | ')}`);
}

// ---- 18. THE DOCTRINE CARRIES THE GRANTED CANVAS EXCEPTION (covers F-035) ----------------------------
{
    const doctrine = await readDoc('docs/strategy/08-complexity-doctrine.md');
    assert.ok(/exception[\s\S]{0,80}workflow canvas \(Kane, 2026-07-31\)/.test(doctrine),
        'docs/strategy/08-complexity-doctrine.md does not name the granted canvas exception, so a builder ' +
        'consulting the normative doctrine alone is still right to reject the approved canvas shape.');
    assert.ok(doctrine.includes('second view of the existing Workflows'),
        'the doctrine names an exception without its width. An exception whose bounds are not written down ' +
        'is the one that grows, which is what Rule 1 exists to stop.');
    // `\s+` and not a literal space: the doctrine is hard-wrapped and this phrase straddles a line break.
    assert.ok(/no\s+wider/.test(doctrine),
        'the doctrine does not say the exception is granted exactly that wide and no wider, which is the ' +
        'clause that stops it being read as a general licence.');
}

// ---- 19. THE STRATEGY HEADERS DO NOT POINT AT THE SUPERSEDED ORDER (covers F-036) -------------------
//
// The same fault one layer deeper, and the reason this section is separate from 17: reconciling PLAN.md
// alone is not enough while the normative doctrine and the strategy index still tell a builder that the
// 2026-07-10 eight-step sequence replaces PLAN.md's "what next". A builder who opens either of those
// first would be sent back to the superseded order and would be right to go.
{
    for (const rel of ['docs/strategy/08-complexity-doctrine.md', 'docs/strategy/00-INDEX.md']) {
        const doc = await readDoc(rel);
        assert.ok(!/replaces the \*\*?"what next"/.test(doc) && !/\*\*replaces the "what next"\*\*/.test(doc),
            `${rel} still says its build sequence replaces the "what next" in PLAN.md. Kane's 2026-07-31 ` +
            'ratification is the forward order, so this header contradicts the plan and wins by being ' +
            'normative.');
        assert.ok(/SUPERSEDED as the ORDER of work/.test(doc),
            `${rel} does not record that the 2026-07-10 sequence is superseded as the order of work. ` +
            'Deleting the old claim is only half of it: silence reads as "no opinion", not "settled".');
    }
}

// ---- 20. THE PUBLISHED PAGE DOES NOT CONTRADICT ITS OWN MERGED CLAIM (covers F-076) -----------------
// The redesign replaces the dashboard with a view generated from TASKS.md. Keep this historical
// regression on its archived artifact; later dashboards need not repeat a July milestone forever.
//
// F-076 was NOT "the page is inaccurate", which is not a checkable claim and would have made this section
// theatre. It was a SPLIT-BRAIN page: the stat tiles and the revision footer were moved forward to say
// slice 1 had merged, while every current-state section underneath still described it as unmerged, in
// review round 3, at the old suite count. Both halves shipped in one revision, so the page disagreed with
// ITSELF, in public, on the artifact Kane governs the project from. Self-contradiction is checkable.
//
// THE BODY/FOOTER SPLIT IS WHAT MAKES IT CHECKABLE, and it is not a convenience. The <footer> is the
// revision LOG and it is history: revision 13 legitimately quotes "2,448 tests" and "IN REVIEW ROUND 3"
// while describing the very staleness it corrected. Forbidding those strings document-wide would forbid
// the record of the fix, so a correct page would fail. The assertion is therefore scoped to everything
// BEFORE <footer>, which is the page's claim about the present.
{
    const page = await readDoc('docs/archive/progress-2026-09-06.html');
    const cut = page.indexOf('<footer>');
    assert.notEqual(cut, -1,
        'docs/progress.html has no <footer>, so its current state cannot be told apart from its revision ' +
        'log and every assertion below would be reading the wrong half of the page. If the footer is ' +
        'renamed, this section must be re-scoped rather than deleted.');
    const body = page.slice(0, cut);

    // THE POSITIVE ANCHOR RUNS FIRST, and it is the reason this section is not just a string blacklist.
    // A test that only forbids markers goes green the moment the subject is deleted from the page, which
    // is the same "guard that names a hazard it does not contain" fault F-076 itself is an instance of.
    //
    // The anchor is deliberately THE STAT TILE, and it is quoted in the lower-case form the tile uses. The
    // tile is the half that WAS moved forward in the split-brain revision, so anchoring here is what makes
    // the two checks below fire against that revision instead of being masked by it. Anchoring on a string
    // the fix introduced would have made the anchor and the markers the same edit, and this section would
    // then never have been watched failing for the reason it claims to exist.
    assert.ok(body.includes('merged 31 Jul as #301'),
        'docs/progress.html no longer claims in its body that slice 1 merged as #301. The contradiction ' +
        'check below is vacuous without that claim, because there is then nothing left to contradict.');

    // The two markers in the exact form they shipped in, one per section that was left behind: the build
    // diagram node, and the pre-merge suite count quoted as if current.
    for (const [marker, what] of [
        ['BUILT, IN REVIEW ROUND 3', 'the build diagram still labels slice 1 as in its third review round'],
        ['2,448', 'a current-state section still quotes the pre-merge suite count of 2,448 tests'],
    ])
        assert.ok(!body.includes(marker),
            `docs/progress.html says slice 1 merged, but ${what} (${JSON.stringify(marker)} appears before ` +
            '<footer>). That is the exact split-brain shape of F-076: the tiles and the footer moved ' +
            'forward while a current-state section did not, so finished work and cleared blockers read as ' +
            'active. If the string is genuinely historical, it belongs in the revision log, not above it.');
}

// ---- 21. [T169] IS CLOSED IN EVERY CANONICAL PLACE (covers F-077) -----------------------------------
//
// Section 16's disease one task later, and filed while the same coordinator was spending the day closing
// that exact family: a status written in ONE place while every other canonical place still says the old
// thing. Slice 1 was marked merged in a single row, and the task register and the roadmap went on keeping
// [T169] open and blocking. An agent reading either would treat settled work as blocked and would be right
// to, because these documents are canonical.
//
// Three places are asserted because three places said it independently, and updating one of them without
// the others is precisely the defect. Two of the three are in TASKS.md and they are read by different
// people for different reasons, which is why the waiting list is not covered by the definition row.
{
    const tasks = await readDoc('TASKS.md');
    const row = taskRow(tasks, 'T169');
    assert.ok(/CLOSED 2026-07-31/.test(row),
        "TASKS.md's [T169] definition row does not record that it CLOSED on 2026-07-31 when slice 1 merged " +
        'as #301. It was the must-fix that gated the whole workflow-format lane, so a row still reading ' +
        'open blocks every later format change that is in fact unblocked.');

    // The waiting list is the half that actually STOPS work: item 20 said [T169] blocks any future
    // workflow-format change. Struck through is how this register closes a waiting item, keeping the
    // original text as the record of what it was rather than deleting the history.
    const waiting = tasks.split('\n').find(l => /^20\./.test(l.trim()) && l.includes('T169'));
    assert.ok(waiting && waiting.includes('~~'),
        'the waiting-on-Kane list still carries [T169] as a live item. The definition row and the waiting ' +
        'list are read independently, and F-077 was one being updated without the other, so closing the ' +
        `row alone is not enough. Item 20 reads: ${waiting ?? '(no item 20 naming T169 at all)'}`);

    // The roadmap is the third, and the one an agent planning the next slice opens first.
    const plan = await readDoc('docs/PLAN.md');
    assert.ok(/merged to `main` on\s+2026-07-31 as #301/.test(plan),
        'docs/PLAN.md does not record that stage 1 slice 1 merged as #301. It is the designated lane-status ' +
        'document, so while it reads as unmerged, [T169] being closed in the task register never reaches ' +
        'anyone who is following the plan.');
    assert.ok(!/is built on branch\s+`feat\/workflow-format-v2` and is in review/.test(plan),
        'docs/PLAN.md still says slice 1 is on a branch and in review, which is the exact sentence F-077 ' +
        'was filed against. Adding the merge note without removing this leaves both readings available on ' +
        'one page, and a reader is entitled to believe either.');
}

console.log(`finding queue: ${VERDICTS.join('/')} is closed, every invariant was planted and caught, and the`);
console.log('               live docs/findings.md holds against the live TASKS.md.');
console.log('               The ratification holds in all three canonical places.');
}
