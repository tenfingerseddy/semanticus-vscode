// [T203]: the residual of F-011, which is the missing GUARD and not the missing exclusion.
//
// WHAT WENT WRONG. #285 dropped `tools/release/mirror-public.test.mjs` out of the mirror's excludeFiles, which
// would have published internal release tooling, and #292 restored it. Nothing failed either time. The repair
// landed in 2eaaf95d and is not in question here; what was missing is anything that would have NOTICED. Under
// `tools/release/` the default is publication: a file that no exclusion list mentions goes to the public mirror,
// so an omission is silent by construction and review is the only thing standing between it and the world.
//
// WHAT THIS GATE DOES. It asks git for the real tracked files under `tools/release/`, then requires the manifest
// to hold a DECISION about each one: excluded, or named in `publishableFiles` with a reason a reader can check.
// The exclusion rule and the audit both come from `tools/release/mirror-manifest.mjs`, the same module the mirror
// itself imports, so this cannot police a manifest the mirror does not apply. Nothing here restates the exclusion
// list; a copied list would go stale in exactly the direction that hurt.
//
// WHY HERE. `tools/release/mirror-public.test.mjs` is the natural neighbour, but CI discovers only
// `Semanticus.VSCode/test`, so a guard living beside the mirror would run in no enforced job (that is F-017 and
// [T204], still open). This directory is the enforced one.
//
// LIMITS, because a green run should not be read as more than it is. This proves every tracked release file
// carries a written decision with a nonempty reason of the required length. It does NOT judge whether a decision is
// the right one: a reason saying "publishable because it is harmless" is accepted here, and only the tenant scan,
// the secret scan and a reader can say otherwise. It also skips inside the curated public tree, where the manifest
// it audits has been curated away, so the private repository's run is the one that enforces this.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { existsSync, readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const repoRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..', '..');
// Both reads are annotated `mirror-optional` for the mirror's own coupling detector: these two paths self-exclude,
// so their joint absence is allowed for curated copies and these checks skip when neither is available.
const manifestPath = resolve(repoRoot, 'tools/release/mirror-manifest.json'); // mirror-optional: it IS the exclusion logic
const loaderPath = resolve(repoRoot, 'tools/release/mirror-manifest.mjs'); // mirror-optional: the loader self-excludes too

const present = [manifestPath, loaderPath].filter((p) => existsSync(p));
const curatedTree = present.length === 0;

// Load through the same module the mirror imports, and survive its absence rather than dying on a stack trace:
// the checks below then skip with a reason, and the both-or-neither check owns the failure.
let loader = null;
let loaderError = null;
if (existsSync(loaderPath)) {
  try {
    loader = await import(pathToFileURL(loaderPath).href);
  } catch (err) {
    loaderError = err;
  }
}

const SKIP = loader
  ? false
  : curatedTree
    ? 'manifest and loader absent; private-manifest checks are unavailable in this copy'
    : 'the manifest loader did not load here, which the both-or-neither check below reports as the failure it is';

// git's own list, never a hand-kept one. The point of the whole gate is that a file lands here without anyone
// remembering to tell a test about it.
function trackedReleaseFiles() {
  const out = execFileSync('git', ['ls-files', '--cached', '--', 'tools/release'], { cwd: repoRoot, encoding: 'utf8' });
  return out.split(/\r?\n/).filter(Boolean);
}

// A deliberately broken copy of the real manifest, so every failing case below is proved against the SAME audit
// the passing case uses. A negative control run through a re-implementation proves nothing about this code.
function brokenManifest(mutate) {
  const copy = structuredClone(loader.manifest);
  mutate(copy);
  return copy;
}

// What a mutation ADDED to a finding list. The controls below assert on the delta rather than the whole list, so a
// real undecided file in the repository is reported once, by the check that owns it, instead of turning every
// control into a confusing second failure about a file it was not testing.
function addedBy(after, before) {
  return after.filter((p) => !before.includes(p));
}

test('the manifest and its loader are either both present or both curated away', () => {
  assert.equal(present.length === 0 || present.length === 2, true,
    `exactly one of mirror-manifest.json / mirror-manifest.mjs is present (${present.join(', ')}). Half a manifest `
    + 'is a broken release path, not a tolerable absence: the mirror imports the json through that loader.');
  assert.equal(loaderError, null,
    `tools/release/mirror-manifest.mjs is present but did not load, so neither this gate nor the mirror can read `
    + `the exclusion manifest: ${loaderError?.message ?? ''}`);
});

test('every tracked file under tools/release is excluded or justified publishable', { skip: SKIP }, () => {
  const tracked = trackedReleaseFiles();
  assert.ok(tracked.includes('tools/release/mirror-manifest.json'),
    'git does not track the manifest this test just read, so the enumeration is broken and every check below '
    + 'would pass on an empty list');
  const audit = loader.auditPublishDecisions(tracked);
  assert.equal(audit.guarded.length, tracked.length,
    `the guard no longer covers all of tools/release/: git tracks ${tracked.length} file(s) there and `
    + `DECISION_REQUIRED_DIRS (${loader.DECISION_REQUIRED_DIRS.join(', ')}) claims ${audit.guarded.length}`);

  assert.deepEqual(audit.undecided, [],
    `release file(s) the mirror WOULD PUBLISH that the manifest says nothing about: ${audit.undecided.join(', ')}. `
    + 'Exclude each one in tools/release/mirror-manifest.json, or add it to publishableFiles with the reason it is '
    + 'safe to publish. Silence is not a decision here, because silence publishes.');
  assert.deepEqual(audit.unjustified, [],
    `publishableFiles entr(ies) with no usable reason: ${audit.unjustified.join(', ')}. Every entry must say why `
    + 'the file is publishable, in a sentence the next reader can check.');
  assert.deepEqual(audit.stale, [],
    `publishableFiles names path(s) this repository does not track under tools/release/: ${audit.stale.join(', ')}. `
    + 'A decision left behind by a rename or a deletion hides the fact that nobody decided about the new name.');
  assert.deepEqual(audit.contradictory, [],
    `publishableFiles calls path(s) publishable that the exclusion lists drop: ${audit.contradictory.join(', ')}. `
    + 'The exclusion wins in the mirror, so the recorded decision is misleading. Remove one side.');
});

test('the manifest this gate audits is the one the mirror applies', { skip: SKIP }, () => {
  // mirror-optional: mirror-public.mjs self-excludes, so it is absent from the curated tree and only there. This
  // test already skips inside that tree, so its presence here is a hard requirement rather than a tolerated read.
  const mirrorPath = resolve(repoRoot, 'tools/release/mirror-public.mjs'); // mirror-optional
  assert.ok(existsSync(mirrorPath),
    'tools/release/mirror-public.mjs is missing while the manifest is present, so this gate would be auditing data '
    + 'that no mirror reads');
  assert.match(readFileSync(mirrorPath, 'utf8'), /from\s+'\.\/mirror-manifest\.mjs'/u,
    'mirror-public.mjs no longer imports ./mirror-manifest.mjs, so the exclusion rule this gate applies is not the '
    + 'one the mirror applies');
  // Positive and negative control on the shared rule itself: a matcher that answered "excluded" to everything, or
  // to nothing, would make the audit above vacuous in either direction.
  assert.equal(loader.isExcludedByManifest('tools/release/tenant-scan.mjs'), true,
    'positive control failed: the shared matcher does not drop the tenant detector, so it was not reading the '
    + 'exclusion lists and the audit proved nothing');
  assert.equal(loader.isExcludedByManifest('tools/release/verify-release.mjs'), false,
    'negative control failed: the shared matcher claims a published release tool is excluded, so it drops '
    + 'everything and the audit proved nothing');
});

test('a new release file nobody decided about fails the guard', { skip: SKIP }, () => {
  const invented = 'tools/release/new-internal-probe.mjs';
  const tracked = trackedReleaseFiles();
  const before = loader.auditPublishDecisions(tracked).undecided;
  const after = loader.auditPublishDecisions([...tracked, invented]).undecided;
  assert.deepEqual(addedBy(after, before), [invented],
    'the real manifest must report a release file it has never heard of as undecided; this is the case that fires '
    + 'the day someone adds a private script under tools/release/');
});

test('dropping a private release file out of the exclusion list fails the guard', { skip: SKIP }, () => {
  // The F-011 regression itself, replayed: #285 removed exactly this path from excludeFiles.
  const victim = 'tools/release/mirror-public.test.mjs';
  const tracked = trackedReleaseFiles();
  assert.ok(tracked.includes(victim), `${victim} is no longer tracked, so this replay of F-011 proves nothing`);
  const before = loader.auditPublishDecisions(tracked).undecided;
  const after = loader.auditPublishDecisions(tracked, brokenManifest((m) => {
    m.excludeFiles = m.excludeFiles.filter((e) => e.path !== victim);
  })).undecided;
  assert.deepEqual(addedBy(after, before), [victim],
    'with the tenant-leak gate dropped from excludeFiles the guard must name it, which is what #285 needed and '
    + 'did not have');
});

test('an empty, whitespace, missing or empty-worded publishable reason fails the guard', { skip: SKIP }, () => {
  const target = 'tools/release/verify-release.mjs';
  const cases = [
    ['an empty reason', (e) => { e.why = ''; }],
    ['a whitespace-only reason', (e) => { e.why = '     '; }],
    ['no reason at all', (e) => { delete e.why; }],
    ['a reason too short to say anything', (e) => { e.why = 'internal'; }],
    ['a reason that is not text', (e) => { e.why = true; }],
  ];
  const tracked = trackedReleaseFiles();
  const before = loader.auditPublishDecisions(tracked).unjustified;
  for (const [label, mutate] of cases) {
    const after = loader.auditPublishDecisions(tracked, brokenManifest((m) => {
      const entry = m.publishableFiles.find((e) => e.path === target);
      assert.ok(entry, `${target} is no longer in publishableFiles, so this control has nothing to break`);
      mutate(entry);
    })).unjustified;
    assert.deepEqual(addedBy(after, before), [target], `the guard must reject ${label}`);
  }
});

test('a stale or contradictory publishable entry fails the guard', { skip: SKIP }, () => {
  const renamedAway = 'tools/release/deleted-runner.ps1';
  const staleAudit = loader.auditPublishDecisions(trackedReleaseFiles(), brokenManifest((m) => {
    m.publishableFiles.push({ path: renamedAway, why: 'left behind when the file was renamed away' });
  }));
  assert.deepEqual(staleAudit.stale, [renamedAway],
    'the guard must name a decision about a file that is no longer there');

  const bothWays = 'tools/release/tenant-scan.mjs';
  const clashAudit = loader.auditPublishDecisions(trackedReleaseFiles(), brokenManifest((m) => {
    m.publishableFiles.push({ path: bothWays, why: 'claimed publishable while the exclusion list still drops it' });
  }));
  assert.deepEqual(clashAudit.contradictory, [bothWays],
    'the guard must name a path the manifest both excludes and calls publishable');
});

test('the publishable register is load-bearing, not decoration', { skip: SKIP }, () => {
  // If emptying or deleting the register left the audit clean, the register would prove nothing about the files it
  // names. Everything it justifies must be a file that is otherwise published by default.
  const registered = loader.manifest.publishableFiles.map((e) => e.path).sort();
  assert.ok(registered.length > 0, 'publishableFiles is empty, so no published release tool has a recorded reason');
  const tracked = trackedReleaseFiles();
  const before = loader.auditPublishDecisions(tracked).undecided;
  const emptied = loader.auditPublishDecisions(tracked, brokenManifest((m) => { m.publishableFiles = []; })).undecided;
  assert.deepEqual(addedBy(emptied, before), registered,
    'with the register emptied, exactly the files it justifies must show up as undecided');
  const removed = loader.auditPublishDecisions(tracked, brokenManifest((m) => { delete m.publishableFiles; })).undecided;
  assert.deepEqual(addedBy(removed, before), registered,
    'a manifest with no publishableFiles key at all must fail the same way, not throw and not pass');
});
