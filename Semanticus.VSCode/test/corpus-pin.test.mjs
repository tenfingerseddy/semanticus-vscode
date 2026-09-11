// The readiness-corpus pin has to be a MECHANISM, not a receipt.
//
// `tools/readiness-corpus/models.pinned.json` carries a commit SHA per repo, and the corpus report claims
// "anyone can re-run it". That claim only holds if re-running `fetch` REPRODUCES the pinned commit. The
// original fetch never read the pin file at all: it shallow-cloned each repo's DEFAULT BRANCH, read whatever
// HEAD that landed on, and rewrote the pin with the new observation - so a re-run silently re-pinned the
// corpus to today's upstream instead of reproducing yesterday's numbers.
//
// This test proves the property without the network and without touching the real corpus: a throwaway local
// git repo with two commits, a fixture pin file naming the FIRST one, and then the assertion that fetch
// checks out that first commit and leaves the pin file alone.

import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { mkdtempSync, mkdirSync, writeFileSync, readFileSync, rmSync, existsSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const extensionRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const repoRoot = resolve(extensionRoot, '..');
const scanUrl = new URL(`file://${resolve(repoRoot, 'tools/readiness-corpus/scan.mjs').replace(/\\/g, '/')}`);

const { cmdFetch, provenanceFor } = await import(scanUrl.href);

const root = mkdtempSync(join(tmpdir(), 'corpus-pin-'));
const git = (cwd, ...args) => execFileSync('git', ['-C', cwd, ...args], { encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] }).trim();

try {
    // ---- a fixture "upstream" with two commits, the second one being the one we must NOT land on ----
    const remote = join(root, 'remote');
    mkdirSync(join(remote, 'sample'), { recursive: true });
    execFileSync('git', ['init', '-q', '-b', 'main', remote], { stdio: 'ignore' });
    git(remote, 'config', 'user.email', 'fixture@example.invalid');
    git(remote, 'config', 'user.name', 'fixture');

    writeFileSync(join(remote, 'sample', 'model.bim'), '{"name":"first"}\n');
    git(remote, 'add', '-A');
    git(remote, 'commit', '-qm', 'first');
    const first = git(remote, 'rev-parse', 'HEAD');

    writeFileSync(join(remote, 'sample', 'model.bim'), '{"name":"second"}\n');
    git(remote, 'commit', '-qam', 'second');
    const second = git(remote, 'rev-parse', 'HEAD');
    assert.notEqual(first, second, 'the fixture needs two DIFFERENT commits or it proves nothing');
    assert.equal(git(remote, 'rev-parse', 'HEAD'), second, 'the fixture default branch must point at the second commit');

    // ---- the corpus definition and the pin, the pin naming the FIRST commit ----
    const entry = { repo: 'fixture/pinned-corpus', path: 'sample/model.bim', kind: 'fixture', url: remote };
    const models = { corpus: [entry] };
    const pinnedPath = join(root, 'models.pinned.json');
    const pinnedBefore = { pinnedAt: '2026-01-01T00:00:00.000Z', corpus: [{ ...entry, commit: first, license: 'fixture' }] };
    writeFileSync(pinnedPath, JSON.stringify(pinnedBefore, null, 1));

    const work = join(root, 'work');
    cmdFetch({ models, work, pinnedPath });

    // ---- 1. the clone must sit ON the pinned commit, not on the default branch's head ----
    const clone = join(work, 'fixture__pinned-corpus');
    const head = git(clone, 'rev-parse', 'HEAD');
    assert.equal(head, first,
        `fetch checked out ${head} but the pin says ${first}: a re-run does not reproduce the pinned corpus`);
    assert.equal(readFileSync(join(clone, 'sample', 'model.bim'), 'utf8').trim(), '{"name":"first"}',
        'the working tree carries the second commit\'s content, so the checkout did not take');

    // ---- 2. the pin file must not have been re-pinned to the newly observed commit ----
    const pinnedAfter = JSON.parse(readFileSync(pinnedPath, 'utf8'));
    assert.equal(pinnedAfter.corpus[0].commit, first,
        `the pin was overwritten with ${pinnedAfter.corpus[0].commit}; a pin that a re-run replaces is not a pin`);
    assert.equal(pinnedAfter.corpus[0].license, 'fixture',
        'the pin lost a field that only the pin file carried, so it was rebuilt from models.json');
    assert.deepEqual(pinnedAfter, pinnedBefore,
        'the pin file was rewritten even though nothing about the corpus changed');

    // ---- 3. an existing clone parked on the WRONG commit must be corrected, not silently trusted ----
    git(clone, 'fetch', '--depth', '1', remote, second);
    git(clone, 'checkout', '-q', '--detach', second);
    assert.equal(git(clone, 'rev-parse', 'HEAD'), second, 'the fixture drift setup failed');

    cmdFetch({ models, work, pinnedPath });
    assert.equal(git(clone, 'rev-parse', 'HEAD'), first,
        'an existing clone at the wrong commit was left there: skip-if-exists trusts a directory it has not checked');

    // ---- 4. an UNPINNED repo still gets pinned, or a new corpus entry could never be added ----
    const fresh = { repo: 'fixture/unpinned-corpus', path: 'sample/model.bim', kind: 'fixture', url: remote };
    const freshPinnedPath = join(root, 'models.unpinned.json');
    writeFileSync(freshPinnedPath, JSON.stringify({ pinnedAt: pinnedBefore.pinnedAt, corpus: [] }, null, 1));
    cmdFetch({ models: { corpus: [fresh] }, work, pinnedPath: freshPinnedPath });
    const freshPinned = JSON.parse(readFileSync(freshPinnedPath, 'utf8'));
    assert.equal(freshPinned.corpus[0].commit, second,
        'a repo with no pin must be pinned to the default branch head it was actually cloned at');

    // ---- 5. THE POINT OF CLAIM. Honouring the pin on fetch is not enough --------------------------
    //
    // `scan` reads models.pinned.json and stamps each row's `commit` into results/scans.json, which is the
    // provenance the published corpus report rests on. It scans whatever is sitting in work/. So
    //   git -C work/<repo> checkout main && node scan.mjs scan && node scan.mjs report
    // republishes NEW scores labelled with the OLD pinned SHA, and nothing anywhere says otherwise.
    // Fixing where the pin is written while leaving where it is claimed just moves the lie downstream.
    //
    // A reader of scans.json is entitled to conclude "these scores are this commit's". So a score may only
    // be published under a pinned commit when the work tree is actually ON that commit.
    {
        const drifted = { ...entry, commit: first };
        assert.equal(git(clone, 'rev-parse', 'HEAD'), first, 'precondition: the clone is on the pinned commit');

        const clean = provenanceFor(drifted, work);
        assert.equal(clean.ok, true, 'a work tree ON the pinned commit must be publishable');
        assert.equal(clean.commit, first, 'a publishable row must carry the pinned commit');

        // Drift it exactly the way a human would, then ask again.
        git(clone, 'checkout', '-q', '--detach', second);
        const drift = provenanceFor(drifted, work);
        assert.equal(drift.ok, false,
            `work tree is at ${second} but the pin says ${first}, and provenanceFor still says publish: ` +
            'scans.json would label new scores with the pinned commit');
        assert.match(String(drift.error), /pinned/u, 'the refusal must say why, in words a report reader can act on');
        assert.equal(drift.observedCommit, second, 'the refusal must record what the tree is ACTUALLY on');

        // A missing clone is not "at the pinned commit" either.
        const absent = provenanceFor({ ...entry, repo: 'fixture/never-cloned', commit: first }, work);
        assert.equal(absent.ok, false, 'a repo with no clone at all cannot be published under a pinned commit');

        // An entry with no pin claims nothing, so there is nothing to contradict.
        const unpinned = provenanceFor({ ...entry, commit: null }, work);
        assert.equal(unpinned.ok, true, 'an entry with no pinned commit claims no provenance and still scans');
        assert.equal(unpinned.commit, null, 'an unpinned row must not invent a commit');

        git(clone, 'checkout', '-q', '--detach', first);   // leave it as we found it
    }

    // ---- 6. the REF is not the TREE ---------------------------------------------------------------
    //
    // Reading HEAD proves which commit was checked out, not what is on disk. Overwrite the model file and the
    // SHA still matches while the bytes the scanner reads are something else entirely, so scans.json labels a
    // score with a commit whose content was never scanned. Same shape as the round-one defect, one level in.
    {
        const clone = join(work, 'fixture__pinned-corpus');
        writeFileSync(join(clone, 'sample', 'model.bim'), '{"name":"tampered by hand"}\n');

        const dirty = provenanceFor({ ...entry, commit: first }, work);
        assert.equal(dirty.ok, false,
            'the work tree is modified but provenanceFor still says publish: it read the ref, not the tree');
        assert.match(String(dirty.error), /modif|dirty|clean/iu, 'the refusal must say the tree is not clean');

        // And fetch must REPAIR it, not skip it because the directory exists and HEAD looks right.
        cmdFetch({ models, work, pinnedPath });
        assert.equal(readFileSync(join(clone, 'sample', 'model.bim'), 'utf8').trim(), '{"name":"first"}',
            'fetch left a tampered work tree in place, so a re-run does not reproduce the pinned content');
        assert.equal(provenanceFor({ ...entry, commit: first }, work).ok, true,
            'after a repairing fetch the tree must be publishable again');
    }

    // ---- 7. an UNTRACKED file changes what is scanned without touching a tracked one ----------------
    //
    // 37 of the 41 corpus entries are folders, which the engine opens as a directory, so dropping a new .tmdl
    // in changes the model being scanned while every tracked file stays pristine. Excluding untracked files
    // made that read as clean AND made it unrepairable, because `checkout --force` does not delete them.
    // Measured on a real cone-sparse clone: a CLEAN sparse tree reports zero lines with untracked reporting on,
    // so the exclusion protected nothing and cost the invariant.
    {
        const clone = join(work, 'fixture__pinned-corpus');
        writeFileSync(join(clone, 'sample', 'injected.tmdl'), 'table Injected\n');

        const injected = provenanceFor({ ...entry, commit: first }, work);
        assert.equal(injected.ok, false,
            'an untracked file changed what would be scanned and provenanceFor still said publish');

        cmdFetch({ models, work, pinnedPath });
        assert.equal(existsSync(join(clone, 'sample', 'injected.tmdl')), false,
            'fetch left an injected untracked file in place, so the false provenance is permanent');
        assert.equal(provenanceFor({ ...entry, commit: first }, work).ok, true,
            'the tree must be publishable again once the injected file is gone');
    }

    console.log('corpus pin: re-running fetch reproduces the pinned commit, does not re-pin, and a drifted');
    console.log('            work tree cannot publish scores under the pinned commit.');
} finally {
    rmSync(root, { recursive: true, force: true });
}
