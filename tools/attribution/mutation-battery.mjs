// MUTATION BATTERY for the borrowed-code attribution gate.
//
// WHY IT IS IN THE REPOSITORY. "25 mutations, 0 unproved" was previously unrepeatable, because the harness
// only ever existed in a scratch directory. A proof nobody else can run is a claim, not a proof.
//
// WHY IT PINS THE ASSERTION AND NOT THE SECTION. Version one scored a mutation as proved if ANY check failed,
// which is the cannot-fail defect class it existed to disprove. Version two pinned each mutation to the CHECK
// it must break -- better, and still not enough: a single check contains many assertions, so a licence
// mutation could be "caught" by that check's notice-coverage assertion while the licence assertion never
// fired at all. That is precisely how a word-bounded reciprocal-licence denylist survived two review rounds
// looking green. So every mutation now declares BOTH the check it must break AND a pattern the failure
// MESSAGE must match. The run fails if the message does not match, if nothing failed, or if the wrong check
// failed.
//
// It perturbs the manifest, and for two cases a source file, runs the gate, and restores everything in a
// `finally`. Nothing is left changed. Exit code is non-zero if any mutation is unproved.
//
// Run: node tools/attribution/mutation-battery.mjs
//   (needs the donor submodule: git submodule update --init --depth 1 external/TabularEditor)
import { readFileSync, writeFileSync } from 'node:fs';
import { spawnSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const repo = resolve(dirname(fileURLToPath(import.meta.url)), '..', '..');
const manPath = resolve(repo, 'third-party-manifest.json');
const gatePath = resolve(repo, 'Semanticus.VSCode/test/attribution-and-license.test.mjs');
const grammarPath = resolve(repo, 'Semanticus.Core/Grammars/DAXLexer.g4');
const lockPath = resolve(repo, 'Semanticus.VSCode/webview/package-lock.json');
const noticesPath = resolve(repo, 'THIRD-PARTY-NOTICES.md');
const originalMan = readFileSync(manPath, 'utf8');
const originalGate = readFileSync(gatePath, 'utf8');
const originalGrammar = readFileSync(grammarPath, 'utf8');
const originalLock = readFileSync(lockPath, 'utf8');
const originalNotices = readFileSync(noticesPath, 'utf8');

// --- THE ACKNOWLEDGED-UNVERIFIED FIXTURE ----------------------------------------------------------
// Every declared entry's licence is verified on this tree, so the gate's `UNVERIFIED_ACKNOWLEDGED` set is
// EMPTY and its four acknowledgement assertions iterate over nothing. Nine mutations here used to reach them
// by perturbing the real BPARules-PowerBI.json entry, which was the one acknowledged exception until the
// corpus was replaced with Microsoft's MIT-licensed rules on 2026-07-30.
//
// Mutating a real entry can no longer reach those assertions, and MEASURED, not assumed: with the set empty,
// four of the nine reported "NOTHING FAILED" and five were masked by the notices-section check firing first,
// because flipping a manifest licence to UNVERIFIED contradicts THIRD-PARTY-NOTICES.md. A masked assertion
// scores as caught while the rule under test never runs, which is the whole defect class this battery exists
// to disprove. So the subject is now SYNTHETIC and is installed across all three inputs the gate reads:
// the manifest, the notices, and the gate's own set. Nothing is left behind; `finally` restores all three.
//
// The fixture path must be a tracked, existing `.json`: the gate requires a declared file to exist, and
// `header: false` is legal only for a comment-less format, so a `.md` fixture would trip the header check
// instead of the assertion under test.
const ACK_PATH = 'docs/mcp-surface-inventory.json';
const ACK_HEADING = 'Synthetic unverified fixture (mutation battery only)';
const ACK_URL = 'https://example.invalid/publishes-no-licence';
const ackBodySha = createHash('sha256')
  .update(readFileSync(resolve(repo, ACK_PATH), 'utf8').split('\r\n').join('\n'))
  .digest('hex');

const ackEntry = () => ({
  path: ACK_PATH,
  kind: 'copy',
  upstream: 'fixture/publishes-no-licence',
  upstreamPath: 'fixture.json',
  url: ACK_URL,
  author: 'fixture author (asserted, not stated upstream)',
  licence: 'UNVERIFIED',
  licenceVerified: false,
  version: 'unpinned',
  verifiedOn: '2026-07-30',
  evidence: 'SYNTHETIC fixture, installed by tools/attribution/mutation-battery.mjs for the duration of one '
    + 'gate run and restored immediately afterwards. It stands in for an acknowledged-unverified entry so the '
    + 'acknowledgement assertions stay proved on a tree where every real licence is verified.',
  header: false,
  headerWaiverReason: 'JSON admits no comment syntax, so no attribution can travel inside the file.',
  escalation: 'Fixture only. A real entry names the human who decides; this one exists to be mutated.',
  // Must keep the fragments the warning check requires: licenceVerified, grant, 404, license: null.
  doNotUpgradeWithout: 'STOP. Do not set licenceVerified true without a structured grant object: this '
    + 'fixture upstream publishes no licence, its LICENSE is HTTP 404, and the GitHub API returns '
    + 'license: null.',
  noticeSection: ACK_HEADING,
  upstreamEquivalence: 'none-recorded',
  bodySha256: ackBodySha,
  acknowledgedUntil: '2099-12-31',
});

// Install the fixture, then let the case apply its own defect to it.
const withAck = (defect) => (m) => {
  const e = ackEntry();
  m.entries.push(e);
  if (defect) defect(e);
  return m;
};

// The notices section is generated FROM THE MUTATED ENTRY, never from the pristine one. That is what stops
// the notices-section check from firing first and masking the assertion under test: if a case flips the
// fixture's licence to MIT, the notices say MIT too, so the only thing left to fail is the acknowledgement
// rule. It cannot hide a real notices defect either, because the fixture is not a real borrowed file.
const ackNotices = (entry) => `${originalNotices}\n## ${ACK_HEADING}\n\n`
  + `Synthetic fixture, present only while the mutation battery runs.\n\n`
  + `- Path: \`${entry.path}\`\n- License: **${entry.licence}**\n- Source: <${entry.url}>\n`
  + `- Version: **${entry.version}**\n`;

const ackGate = (gateSrc) => {
  const from = 'const UNVERIFIED_ACKNOWLEDGED = new Set([]);';
  if (!gateSrc.includes(from)) {
    throw new Error('the mutation battery could not find the empty UNVERIFIED_ACKNOWLEDGED set to seed. '
      + 'If a real acknowledged entry was added, point the fixture at it or delete this fixture, but do NOT '
      + 'leave the acknowledgement assertions with no subject.');
  }
  return gateSrc.replace(from, `const UNVERIFIED_ACKNOWLEDGED = new Set([${JSON.stringify(ACK_PATH)}]);`);
};

const drop = (m, p) => { m.entries = m.entries.filter((e) => e.path !== p); return m; };
const entry = (m, suffix) => m.entries.find((e) => e.path.endsWith(suffix));
const pkg = (m, name) => entry(m, 'studio.js').dependencyClosure.find((p) => p.name === name);
// A licence mutation has to move the LOCKFILE, because that is the source of truth and is where a real
// unpermitted dependency appears. Mutating only the manifest produced a bookkeeping mismatch instead, which
// is how the old harness scored licence mutations as proved while the licence rule never ran.
const setLockLicence = (name, licence) => (lockText) => {
  const l = JSON.parse(lockText);
  for (const [key, meta] of Object.entries(l.packages ?? {})) {
    if (key.replace(/^(?:.*\/)?node_modules\//u, '') === name && !meta.dev) meta.license = licence;
  }
  return JSON.stringify(l, null, 2);
};

// [label, mutate(manifest), mustBreak (exact check name), mustSay (regex on the message), lockMutate?]
const MUTATIONS = [
  // --- undeclaring things ---------------------------------------------------------------------------
  // The obvious "just undeclare the grammar" does NOT reach the donor-copy scan: the file now carries an
  // attribution header so it is no longer byte-identical to the donor. Other directions catch it. To
  // exercise the donor-copy scan end to end the file must ALSO be stripped -- see NAKED_COPY below.
  ['undeclare the grammar copy', (m) => drop(m, 'Semanticus.Core/Grammars/DAXLexer.g4'),
    'every tracked file carrying a foreign attribution marker is declared or exempt',
    /DAXLexer\.g4/],

  ['undeclare the bundled studio.js', (m) => drop(m, 'Semanticus.VSCode/media/studio/studio.js'),
    'every tracked file carrying a foreign attribution marker is declared or exempt',
    /studio\.js/],

  ['undeclare mstdlib.ts, which the notices still describe', (m) => drop(m, 'Semanticus.VSCode/webview/src/mstdlib.ts'),
    'every borrowed file path named in THIRD-PARTY-NOTICES.md is declared in the manifest',
    /mstdlib\.ts/],

  // --- the licence ALLOWLIST, mutated in the LOCKFILE because that is the source of truth ----------
  // Every one of these passed the word-bounded denylist this replaced. The manifest is moved in step with
  // the lockfile so the bookkeeping check stays quiet and the LICENCE assertion is what has to fire.

  ["a bundled dependency spelled GPLv2 (defeated the old denylist)",
    (m) => { const p = pkg(m, 'react'); p.licence = 'GPLv2'; return m; },
    'every production dependency carries a licence we have decided we can ship',
    /"GPLv2" is not a permitted SPDX identifier/, setLockLicence('react', 'GPLv2')],

  ["a bundled dependency spelled GNU General Public License v3.0",
    (m) => { const p = pkg(m, 'react'); p.licence = 'GNU General Public License v3.0'; return m; },
    'every production dependency carries a licence we have decided we can ship',
    /not a permitted SPDX identifier/, setLockLicence('react', 'GNU General Public License v3.0')],

  ["npm's own UNLICENSED proprietary marker",
    (m) => { const p = pkg(m, 'react'); p.licence = 'UNLICENSED'; return m; },
    'every production dependency carries a licence we have decided we can ship',
    /is not a licence grant/, setLockLicence('react', 'UNLICENSED')],

  ["a bundled dependency marked Proprietary",
    (m) => { const p = pkg(m, 'react'); p.licence = 'Proprietary'; return m; },
    'every production dependency carries a licence we have decided we can ship',
    /is not a licence grant/, setLockLicence('react', 'Proprietary')],

  ["canonical AGPL-3.0-only",
    (m) => { const p = pkg(m, 'react'); p.licence = 'AGPL-3.0-only'; return m; },
    'every production dependency carries a licence we have decided we can ship',
    /not a permitted SPDX identifier/, setLockLicence('react', 'AGPL-3.0-only')],

  ["no upstream licence field at all",
    (m) => { const p = pkg(m, 'react'); p.licence = 'NO-LICENSE-FIELD'; return m; },
    'every production dependency carries a licence we have decided we can ship',
    /is not a licence grant/, setLockLicence('react', 'NO-LICENSE-FIELD')],

  ["an empty licence string",
    (m) => { const p = pkg(m, 'react'); p.licence = ''; return m; },
    'every production dependency carries a licence we have decided we can ship',
    /no licence recorded/, setLockLicence('react', '')],

  ["a licence exception (WITH)",
    (m) => { const p = pkg(m, 'react'); p.licence = 'GPL-2.0 WITH Classpath-exception-2.0'; return m; },
    'every production dependency carries a licence we have decided we can ship',
    /carries a licence exception/, setLockLicence('react', 'GPL-2.0 WITH Classpath-exception-2.0')],

  ["a dual licence with no recorded choice",
    (m) => { const p = pkg(m, 'react'); p.licence = '(MIT OR Apache-2.0)'; return m; },
    'every production dependency carries a licence we have decided we can ship',
    /is a dual licence; record licenceChoice/, setLockLicence('react', '(MIT OR Apache-2.0)')],

  ["a dual licence where neither option is permitted",
    (m) => { const p = pkg(m, 'react'); p.licence = '(GPL-3.0-only OR AGPL-3.0-only)'; return m; },
    'every production dependency carries a licence we have decided we can ship',
    /offers no permitted option/, setLockLicence('react', '(GPL-3.0-only OR AGPL-3.0-only)')],

  ["a licenceChoice that is not one of the disjuncts",
    (m) => { const p = pkg(m, 'react'); p.licence = '(MIT OR Apache-2.0)'; p.licenceChoice = 'ISC'; return m; },
    'every production dependency carries a licence we have decided we can ship',
    /is not one of/, setLockLicence('react', '(MIT OR Apache-2.0)')],

  ["an AND expression binding us to an unpermitted licence",
    (m) => { const p = pkg(m, 'react'); p.licence = 'MIT AND GPL-3.0-only'; return m; },
    'every production dependency carries a licence we have decided we can ship',
    /binds us to GPL-3\.0-only/, setLockLicence('react', 'MIT AND GPL-3.0-only')],

  ["a mixed OR/AND expression the gate must refuse to guess",
    (m) => { const p = pkg(m, 'react'); p.licence = 'MIT OR Apache-2.0 AND GPL-3.0-only'; return m; },
    'every production dependency carries a licence we have decided we can ship',
    /mixes OR and AND/, setLockLicence('react', 'MIT OR Apache-2.0 AND GPL-3.0-only')],

  // --- the closure, both directions ----------------------------------------------------------------
  ['drop one production dependency from the closure',
    (m) => { const b = entry(m, 'studio.js'); b.dependencyClosure = b.dependencyClosure.filter((p) => p.name !== 'react'); return m; },
    'the declared dependency closure and the lockfile agree in both directions',
    /not declared in the manifest/],

  ['declare a bundled version the lockfile does not pin', (m) => { pkg(m, 'react').version = '99.0.0'; return m; },
    'the declared dependency closure and the lockfile agree in both directions',
    /but the lockfile pins/],

  // The Apache-2.0 population is DERIVED from the licence, so `shipsUpstreamNotice: false` is now a legitimate
  // answer (no upstream NOTICE, nothing to carry). What must fail is failing to ANSWER, and losing the text.
  ['leave the Apache-2.0 NOTICE question unanswered',
    (m) => { delete pkg(m, 'echarts').shipsUpstreamNotice; return m; },
    'every Apache-2.0 dependency has its section 4(d) NOTICE obligation resolved',
    /does not record shipsUpstreamNotice as a boolean/],

  ['hide an Apache-2.0 dependency by mislabelling its licence',
    (m) => { pkg(m, 'echarts').licence = 'MIT'; return m; },
    'every Apache-2.0 dependency has its section 4(d) NOTICE obligation resolved',
    /derivation is broken/],

  // --- the unverified-licence exception, on the SYNTHETIC fixture ----------------------------------
  // The trailing `true` installs the fixture across manifest, notices and the gate's set. See the
  // ACKNOWLEDGED-UNVERIFIED FIXTURE block above for why these cannot use a real entry any more.
  ['quiet upgrade: flip licenceVerified with no grant',
    withAck((b) => { b.licenceVerified = true; b.licence = 'MIT'; b.version = 'a'.repeat(40); }),
    'unverified licences are acknowledged, escalated, and not stale',
    /without a structured "grant" object/, null, true],

  // The round-two bypass, verbatim: a prose string that satisfied "over 20 chars, has a date, has a quote".
  ['quiet upgrade: a grant that is prose, not a record',
    withAck((b) => {
      b.licenceVerified = true; b.licence = 'MIT'; b.version = 'a'.repeat(40);
      b.grant = "Granted MIT on 2026-07-29: 'approved'";
    }),
    'unverified licences are acknowledged, escalated, and not stale',
    /without a structured "grant" object/, null, true],

  ['quiet upgrade: a grant object that names nobody',
    withAck((b) => {
      b.licenceVerified = true; b.licence = 'MIT'; b.version = 'a'.repeat(40);
      b.grant = { grantedOn: '2026-07-29', obtainedVia: 'email', evidenceUrl: 'https://example.com/grant' };
    }),
    'unverified licences are acknowledged, escalated, and not stale',
    /grant is missing "grantedBy"/, null, true],

  ['quiet upgrade: a grant with nothing to check it against',
    withAck((b) => {
      b.licenceVerified = true; b.licence = 'MIT'; b.version = 'a'.repeat(40);
      b.grant = { grantedBy: 'fixture upstream', grantedOn: '2026-07-29', obtainedVia: 'a phone call', recordedBy: 'Claude' };
    }),
    'unverified licences are acknowledged, escalated, and not stale',
    /neither an https evidenceUrl nor a quotedGrantText/, null, true],

  ['gut the do-not-upgrade warning with filler',
    withAck((b) => { b.doNotUpgradeWithout = 'x'.repeat(400); }),
    'every acknowledged-unverified entry keeps a warning that carries its evidence',
    /no longer mentions/, null, true],

  ['an expired acknowledgement',
    withAck((b) => { b.acknowledgedUntil = '2026-01-01'; }),
    'acknowledged-unverified exceptions are capped and have not expired',
    /acknowledgement EXPIRED/, null, true],

  ['an acknowledgement with no expiry at all',
    withAck((b) => { delete b.acknowledgedUntil; }),
    'acknowledged-unverified exceptions are capped and have not expired',
    /no valid "acknowledgedUntil"/, null, true],

  ['a second extension of the same acknowledgement',
    withAck((b) => {
      b.acknowledgedUntilHistory = [
        { previousDate: '2026-08-31', extendedOn: '2026-08-31', reason: 'first extension, recorded properly for the test', extendedBy: 'test' },
        { previousDate: '2099-11-30', extendedOn: '2026-09-15', reason: 'second extension, which the ledger must refuse', extendedBy: 'test' },
      ];
    }),
    'acknowledged-unverified exceptions are capped and have not expired',
    /has been extended 2 times/, null, true],

  // --- manifest shape ------------------------------------------------------------------------------
  // Left UNPINNED deliberately: this is the case that proves a verified claim needs a real version.
  ['claim a verified licence at an unpinned version',
    withAck((b) => { b.licenceVerified = true; b.licence = 'MIT'; }),
    'every manifest entry is fully and validly specified',
    /verified licence at an unpinned version/, null, true],

  ['verifiedOn that is not a date', (m) => { entry(m, 'DAXLexer.g4').verifiedOn = 'recently'; return m; },
    'every manifest entry is fully and validly specified', /verifiedOn "recently" is not an ISO date/],

  ['url that is not a URL', (m) => { entry(m, 'DAXLexer.g4').url = 'TabularEditor'; return m; },
    'every manifest entry is fully and validly specified', /is not an https URL/],

  ['header:false on a file that can carry a comment',
    (m) => { const g = entry(m, 'DAXLexer.g4'); g.header = false; g.headerWaiverReason = 'do not feel like it'; return m; },
    'every manifest entry is fully and validly specified', /files can carry a comment/],

  ['hedged licence evidence', (m) => { entry(m, 'DAXLexer.g4').evidence = 'believed permissive, upstream looks like MIT'; return m; },
    'every manifest entry is fully and validly specified', /hedges its licence evidence/],

  ['declare nothing at all', (m) => { m.entries = []; return m; },
    'every manifest entry is fully and validly specified', /declares nothing/],

  // --- provenance preservation ---------------------------------------------------------------------
  ['declare a path that does not exist',
    (m) => { m.entries.push({ ...entry(m, 'DAXLexer.g4'), path: 'Semanticus.Core/Grammars/GONE.g4' }); return m; },
    'every declared file exists, and its body still matches what was verified', /which does not exist/],

  ['stale body hash (content changed under a verified claim)',
    (m) => { entry(m, 'DAXLexer.g4').bodySha256 = 'b'.repeat(64); return m; },
    'every declared file exists, and its body still matches what was verified', /has changed since its licence was verified/],

  ['a donor pin that is 40 hex characters but matches nothing',
    (m) => { entry(m, 'DAXLexer.g4').version = 'c'.repeat(40); return m; },
    'a donor-body-identical claim is re-proved against the donor tree, not just asserted',
    /is actually pinned at/],

  ['point the notice section at the wrong heading',
    (m) => { entry(m, 'DAXLexer.g4').noticeSection = 'ANTLR runtime'; return m; },
    'THIRD-PARTY-NOTICES.md has one section per entry tying path, licence, URL and version together',
    /does not state the/],
];

// PER-MARKER MUTATIONS. The old battery had two marker mutations and BOTH fired through FOREIGN_MARKERS[0],
// so markers one to four could be deleted or corrupted and the battery still reported 39 of 39. That is how a
// raw backspace byte kept two markers permanently inert through two review rounds. Each marker is now broken
// individually, in the GATE source, and the per-marker control must be the thing that fails.
const MARKER_NAMES = ['copyright line', 'SPDX identifier', '@license tag', '"licensed under" phrase',
  '"all rights reserved"'];
function corruptMarker(gateSrc, index) {
  // Replace that marker's regex with one that cannot match, exactly as a collapsed escape would.
  const lines = gateSrc.split('\n');
  let seen = -1;
  for (let i = 0; i < lines.length; i++) {
    if (!/^\s*\{ name: '/u.test(lines[i]) || !lines[i].includes('re: /')) continue;
    seen++;
    if (seen !== index) continue;
    lines[i] = lines[i].replace(/re: \/[^,]*\/iu,/u, 're: /(?!)/iu,');   // a regex that never matches
    return lines.join('\n');
  }
  throw new Error(`could not find marker index ${index} to corrupt`);
}

// Two mutations edit a FILE rather than the manifest.
const EDIT_RULE = ['edit a grammar rule while keeping the attribution header intact',
  'a donor-body-identical claim is re-proved against the donor tree, not just asserted',
  /no longer identical to/];
const NAKED_COPY = ['the ORIGINAL defect: naked verbatim donor copy, header stripped and undeclared',
  'no undeclared file is a verbatim copy of a vendored donor file',
  /verbatim copies of vendored donor files are not declared/];

// Non-fatal harness that reports every failing check AND its message. The count assertion is removed: it
// would fire on every mutation and drown the signal.
// ---- DECLARED COLLATERAL -------------------------------------------------------------------------
// Some mutations genuinely break more than one check, and that is information, not noise: undeclaring a
// borrowed file SHOULD also break the notices-orphan scan. Undeclared collateral is now fatal, so the
// legitimate cases are written down here, keyed by label so the 45 tuples above need no edit. The scorer
// also fails on a STALE entry (one that no longer fires), so this map cannot rot into a blanket amnesty.
const CLOSURE = 'the declared dependency closure and the lockfile agree in both directions';
const NOTICE_4D = 'every Apache-2.0 dependency has its section 4(d) NOTICE obligation resolved';
const DONOR = 'a donor-body-identical claim is re-proved against the donor tree, not just asserted';
const ORPHAN = 'every borrowed file path named in THIRD-PARTY-NOTICES.md is declared in the manifest';
const MARKER = 'every tracked file carrying a foreign attribution marker is declared or exempt';
const EXEMPTIONS = 'the marker exemption lists are bounded, explained, and still needed';
const HEADERS = 'declared file headers carry the attribution the licence actually requires';
const SECTIONS = 'THIRD-PARTY-NOTICES.md has one section per entry tying path, licence, URL and version together';
const BODY_HASH = 'every declared file exists, and its body still matches what was verified';
const ACK_STALE = 'unverified licences are acknowledged, escalated, and not stale';
const VSIX_NOTICE = 'every borrowed file embedded in a shipped binary carries its licence in the .vsix NOTICE';
const NUGET_NOTICE = 'every non-exempt PackageReference written in the packaged engine project files carries its licence in THIRD-PARTY-NOTICES.md (asset-drop exemptions are named with measured evidence; the resolved publish closure is open and tracked)';

// EACH ALLOWANCE IS A NAME **AND** A MESSAGE PATTERN. A name-only allowance launders a regression: once
// "the declared dependency closure and the lockfile agree in both directions" is allowed to fail for this
// mutation, it may fail for a COMPLETELY DIFFERENT REASON and still be waved through, which is the same
// "a count is not a set" mistake one level down. The pattern pins WHY it is allowed to fail.
const VACUOUS_DONOR = /no entry claims donor-body identity/u;
const VACUOUS_BUNDLE = /no bundle entry is declared/u;
const ORPHAN_UNDECLARED = /describes tracked file\(s\) that the manifest does not declare/u;
const NOTICES_MISSING_LICENCE = /notices section never names the .* licence/u;
const APACHE_NO_FLAG = /does not record shipsUpstreamNotice as a boolean/u;
const MARKER_UNDECLARED = /carry a third-party attribution marker but are not declared/u;
const EXEMPT_NO_MARKER = /is exempted from the marker scan but no longer contains any attribution marker/u;
const FILE_MISSING = /ENOENT: no such file or directory/u;
const SECTION_NO_PATH = /does not state the path/u;
const SECTION_NO_VERSION = /does not state the version/u;
const BODY_CHANGED = /has changed since its licence was verified/u;
const ACK_NO_GRANT = /was upgraded away from UNVERIFIED without a structured "grant" object/u;
// Emptying the manifest makes the embedded BPA rules file an UNDECLARED embedded resource, and that
// assertion fires before the vacuity one. Pinning the message is what caught the change of reason.
const VSIX_UNCLASSIFIED = /neither declared in third-party-manifest\.json nor listed as first-party/u;
const NUGET_UNDECLARED = /neither declared as kind nuget nor classified/u;
const CLOSURE_LOCKFILE_DISAGREES = /declares echarts as MIT but the lockfile says Apache-2\.0/u;

const ALSO_BREAKS = new Map([
  // Undeclaring a file removes it from every manifest-driven check at once.
  ['undeclare the grammar copy', [[DONOR, VACUOUS_DONOR], [ORPHAN, ORPHAN_UNDECLARED]]],
  ['undeclare the bundled studio.js',
    [[ORPHAN, ORPHAN_UNDECLARED], [CLOSURE, VACUOUS_BUNDLE], [NOTICE_4D, VACUOUS_BUNDLE]]],
  ['undeclare mstdlib.ts, which the notices still describe', [[MARKER, MARKER_UNDECLARED]]],
  // A licence mutation moves the LOCKFILE, so the notices-name-the-licence half of the closure check fires
  // too. That is the pairing the harness note describes: the licence rule must fire, and this rides along.
  ['a bundled dependency spelled GPLv2 (defeated the old denylist)', [[CLOSURE, NOTICES_MISSING_LICENCE]]],
  ['a bundled dependency spelled GNU General Public License v3.0', [[CLOSURE, NOTICES_MISSING_LICENCE]]],
  ["npm's own UNLICENSED proprietary marker", [[CLOSURE, NOTICES_MISSING_LICENCE]]],
  ['a bundled dependency marked Proprietary', [[CLOSURE, NOTICES_MISSING_LICENCE]]],
  ['canonical AGPL-3.0-only', [[CLOSURE, NOTICES_MISSING_LICENCE]]],
  ['no upstream licence field at all', [[CLOSURE, NOTICES_MISSING_LICENCE]]],
  ['a licence exception (WITH)', [[CLOSURE, NOTICES_MISSING_LICENCE]]],
  ['a dual licence with no recorded choice',
    [[CLOSURE, NOTICES_MISSING_LICENCE], [NOTICE_4D, APACHE_NO_FLAG]]],
  ['a dual licence where neither option is permitted', [[CLOSURE, NOTICES_MISSING_LICENCE]]],
  ['a licenceChoice that is not one of the disjuncts',
    [[CLOSURE, NOTICES_MISSING_LICENCE], [NOTICE_4D, APACHE_NO_FLAG]]],
  ['an AND expression binding us to an unpermitted licence', [[CLOSURE, NOTICES_MISSING_LICENCE]]],
  ['a mixed OR/AND expression the gate must refuse to guess',
    [[CLOSURE, NOTICES_MISSING_LICENCE], [NOTICE_4D, APACHE_NO_FLAG]]],
  ['hide an Apache-2.0 dependency by mislabelling its licence', [[CLOSURE, CLOSURE_LOCKFILE_DISAGREES]]],
  // Upgrading the synthetic fixture without a grant trips the shape check AND the acknowledgement check.
  ['claim a verified licence at an unpinned version', [[ACK_STALE, ACK_NO_GRANT]]],
  // Emptying the manifest removes every entry, so every manifest-driven check has nothing left to hold.
  ['declare nothing at all', [[DONOR, VACUOUS_DONOR], [ORPHAN, ORPHAN_UNDECLARED],
    [VSIX_NOTICE, VSIX_UNCLASSIFIED], [NUGET_NOTICE, NUGET_UNDECLARED],
    [CLOSURE, VACUOUS_BUNDLE], [NOTICE_4D, VACUOUS_BUNDLE],
    [MARKER, MARKER_UNDECLARED], [EXEMPTIONS, EXEMPT_NO_MARKER]]],
  ['declare a path that does not exist',
    [[DONOR, FILE_MISSING], [HEADERS, FILE_MISSING], [SECTIONS, SECTION_NO_PATH]]],
  ['a donor pin that is 40 hex characters but matches nothing', [[SECTIONS, SECTION_NO_VERSION]]],
  // Editing the grammar changes its bytes, so the recorded body hash stops matching as well.
  ['edit a grammar rule while keeping the attribution header intact', [[BODY_HASH, BODY_CHANGED]]],
  ['the ORIGINAL defect: naked verbatim donor copy, header stripped and undeclared',
    [[DONOR, VACUOUS_DONOR], [ORPHAN, ORPHAN_UNDECLARED]]],
  // Breaking marker 0 also breaks the "still needed" check, which relies on markers to prove an exemption
  // is still earning its place.
  ['disable attribution marker 0 (copyright line)', [[EXEMPTIONS, EXEMPT_NO_MARKER]]],
]);

// THE SET OF MUTATION LABELS, asserted in both directions further down. DECLARED_CASE_COUNT pinned only
// the TOTAL, so a swap, a duplicate label, or an ALSO_BREAKS key naming a case that no longer exists all
// passed. Same defect as EXPECTED_CHECKS in the gate, same fix: compare the set, not the size.
const DECLARED_CASE_LABELS = [
  'undeclare the grammar copy',
  'undeclare the bundled studio.js',
  'undeclare mstdlib.ts, which the notices still describe',
  'a bundled dependency spelled GPLv2 (defeated the old denylist)',
  'a bundled dependency spelled GNU General Public License v3.0',
  "npm's own UNLICENSED proprietary marker",
  'a bundled dependency marked Proprietary',
  'canonical AGPL-3.0-only',
  'no upstream licence field at all',
  'an empty licence string',
  'a licence exception (WITH)',
  'a dual licence with no recorded choice',
  'a dual licence where neither option is permitted',
  'a licenceChoice that is not one of the disjuncts',
  'an AND expression binding us to an unpermitted licence',
  'a mixed OR/AND expression the gate must refuse to guess',
  'drop one production dependency from the closure',
  'declare a bundled version the lockfile does not pin',
  'leave the Apache-2.0 NOTICE question unanswered',
  'hide an Apache-2.0 dependency by mislabelling its licence',
  'quiet upgrade: flip licenceVerified with no grant',
  'quiet upgrade: a grant that is prose, not a record',
  'quiet upgrade: a grant object that names nobody',
  'quiet upgrade: a grant with nothing to check it against',
  'gut the do-not-upgrade warning with filler',
  'an expired acknowledgement',
  'an acknowledgement with no expiry at all',
  'a second extension of the same acknowledgement',
  'claim a verified licence at an unpinned version',
  'verifiedOn that is not a date',
  'url that is not a URL',
  'header:false on a file that can carry a comment',
  'hedged licence evidence',
  'declare nothing at all',
  'declare a path that does not exist',
  'stale body hash (content changed under a verified claim)',
  'a donor pin that is 40 hex characters but matches nothing',
  'point the notice section at the wrong heading',
  'edit a grammar rule while keeping the attribution header intact',
  'the ORIGINAL defect: naked verbatim donor copy, header stripped and undeclared',
  'disable attribution marker 0 (copyright line)',
  'disable attribution marker 1 (SPDX identifier)',
  'disable attribution marker 2 (@license tag)',
  'disable attribution marker 3 ("licensed under" phrase)',
  'disable attribution marker 4 ("all rights reserved")',
  'delete d3-color\'s year-bearing ISC copyright notice',
  'delete the seven-package year-bearing ISC copyright notice',
];

// EVERY REWRITE BELOW IS ASSERTED TO HAVE MATCHED. Both of these are string surgery on another file's
// source, and a string rewrite that stops matching does not raise anything: it silently returns the input
// unchanged. That exact thing just happened. The gate's `check` definition gained one token, this
// replacement quietly became a no-op, the gate stayed FATAL, so it aborted at the first failing check,
// emitted no `FAILED>>` lines, and all 45 mutations reported "NOTHING FAILED - the gate accepted the
// mutation". A battery that reports total failure when its own harness breaks is no better than one that
// reports total success; either way the number is not about the gate. So: match, or throw.
function rewrite(src, from, to, what) {
  const out = typeof from === 'string' ? src.replace(from, to) : src.replace(from, to);
  if (out === src) {
    throw new Error(`the mutation battery could not apply its "${what}" rewrite to the gate source. `
      + 'It patches the gate by matching literal text, so an edit to that text disables the patch silently. '
      + 'Re-sync the pattern in mutation-battery.mjs with attribution-and-license.test.mjs; do NOT ignore '
      + 'this, because without the patch every mutation scores as "NOTHING FAILED".');
  }
  return out;
}

const nonFatal = (() => {
  let src = rewrite(originalGate,
    "const check = (name, fn) => { fn(); ranChecks.push(name); console.log('  [PASS] ' + name); };",
    "const check = (name, fn) => { try { fn(); ranChecks.push(name); } catch (e) { "
    + "console.log('FAILED>> ' + name + ' :: ' + String(e.message).split(String.fromCharCode(10)).join(' ')); } };",
    'non-fatal check() harness');
  // The declared-check-name assertion must go: under a mutation some checks are MEANT to fail, so it would
  // fire every time and bury the per-mutation signal. Delimited by sentinels so it survives rewording.
  src = rewrite(src,
    /\/\/ MUTATION-BATTERY-STRIP-BEGIN[\s\S]*?\/\/ MUTATION-BATTERY-STRIP-END/u, '',
    'strip the declared-check-name assertion');
  return src;
})();

// Run the gate and parse the failures it reported.
//
// A NON-ZERO EXIT WITH NOTHING PARSED IS A HARD ERROR, not an empty list. The old version returned
// whatever it could parse and threw the exit status away, so a gate that DIED (a syntax error from a bad
// rewrite, an uncaught throw outside any check, a missing module) produced zero `FAILED>>` lines and read
// as "nothing failed". For the baseline that means green; for a mutation it means "the gate accepted it".
// Either way the harness reports on itself instead of on the gate, which is exactly what happened once
// already this round.
function runGate() {
  const r = spawnSync(process.execPath, [gatePath], { cwd: resolve(repo, 'Semanticus.VSCode'), encoding: 'utf8' });
  const out = (r.stdout || '') + (r.stderr || '');
  const parsed = out.split('\n').filter((l) => l.startsWith('FAILED>> ')).map((l) => {
    const body = l.replace('FAILED>> ', '');
    const i = body.indexOf(' :: ');
    return { name: body.slice(0, i).trim(), message: body.slice(i + 4) };
  });
  if (r.error) throw new Error(`could not spawn the gate at all: ${r.error.message}`);
  if (r.status !== 0 && parsed.length === 0) {
    throw new Error('the gate exited '
      + `${r.status === null ? `on signal ${r.signal}` : r.status} but reported no parseable failure. It did `
      + 'not evaluate its checks, so neither the baseline nor any mutation below means anything. Raw output '
      + `follows:\n${out.trim().slice(0, 2000)}`);
  }
  return parsed;
}
const failures = runGate;

// A mutation is PROVED only when the named check fires with the named message AND NOTHING ELSE FAILS.
//
// WHY COLLATERAL IS NOW FATAL. The old scorer looked for its expected hit and ignored every other failing
// check. That reintroduced, one level up, the very defect the assertion-pinning was meant to kill: a
// mutation could be scored PROVED while the tree was broken in some unrelated way, and a genuinely broken
// baseline would be invisible behind a wall of green. Any case that legitimately breaks more than one
// check declares the others in `alsoBreaks`, so the collateral is written down and reviewed rather than
// silently tolerated.
const ranCaseLabels = [];
function score(label, mustBreak, mustSay, failed, alsoBreaks = []) {
  ranCaseLabels.push(label);
  const hit = failed.find((f) => f.name === mustBreak);
  // An allowance is [checkName, messagePattern] and BOTH must match. A failure under an allowed name whose
  // message is not the allowed one is a NEW failure wearing a permitted label, so it counts as collateral.
  const isAllowed = (f) => f.name === mustBreak
    || alsoBreaks.some(([n, re]) => f.name === n && re.test(f.message));
  const extras = failed.filter((f) => !isAllowed(f)).map((f) => `${f.name} :: ${f.message.slice(0, 140)}`);
  const unusedAllowance = alsoBreaks
    .filter(([n, re]) => !failed.some((f) => f.name === n && re.test(f.message)))
    .map(([n, re]) => `${n} (${re})`);

  if (hit && mustSay.test(hit.message) && extras.length === 0 && unusedAllowance.length === 0) {
    console.log(`PROVED     | ${label}`);
    return 0;
  }
  console.log(`NOT PROVED | ${label}`);
  console.log(`     must break : "${mustBreak}"`);
  console.log(`     must say   : ${mustSay}`);
  if (!failed.length) console.log('     actually   : NOTHING FAILED - the gate accepted the mutation');
  else if (!hit) console.log(`     actually   : other checks failed: ${failed.map((f) => f.name).join(' | ')}`);
  else if (!mustSay.test(hit.message)) console.log(`     actually   : right check, WRONG ASSERTION: ${hit.message.slice(0, 200)}`);
  if (extras.length) console.log(`     collateral : also failed, undeclared: ${extras.join(' | ')}`);
  if (unusedAllowance.length) {
    console.log(`     stale      : alsoBreaks names ${unusedAllowance.join(' | ')}, which did NOT fail. `
      + 'Remove it: an allowance nobody needs is a hole waiting for a real regression.');
  }
  return 1;
}

// ---- THE CASE COUNT IS ASSERTED, NOT JUST PRINTED ------------------------------------------------
// "mutations=45 unproved=0" was only ever a console.log, and the exit code looked at `unproved` alone.
// So deleting a mutation was the cheapest way to make this battery green: the deleted case cannot be
// unproved, the total silently drops, and nothing anywhere objects. A number that is printed is not a
// number that is checked.
// ---- THE CASE LABELS ARE ASSERTED AS A SET, NOT AS A TOTAL --------------------------------------
// "mutations=45 unproved=0" was only ever a console.log and the exit code looked at `unproved` alone, so
// deleting a case was the cheapest way to be green. Pinning the TOTAL fixed only half of that: a swap, a
// duplicated label, or an ALSO_BREAKS key naming a case that no longer exists all kept the total at 45.
// This is the same "a count is not a set" defect the gate's EXPECTED_CHECKS had, so it gets the same fix.
const markerLabelFor = (i) => `disable attribution marker ${i} (${MARKER_NAMES[i]})`;

// ---- THE YEAR-BEARING ISC NOTICES --------------------------------------------------------------
// THE DEFECT THESE CLOSE. THIRD-PARTY-NOTICES.md used to reduce all eight installed d3 ISC packages to
// "Copyright Mike Bostock", and the gate's single ISC row pinned exactly that shortened string. The
// installed packages ship two different lines: d3-color says 2010-2022, the other seven say 2010-2021. So
// the document stated a notice no package ships, and the pin agreed with the document rather than with the
// packages -- the licence BODY digest is identical across all eight, so nothing about the body could ever
// have caught it. The body is not the notice; ISC requires the copyright line to travel with it.
//
// Each case deletes ONE year-bearing line and must make the digest check fail BY SECTION NAME. Deleting a
// copyright line moves no body digest, which is the whole reason the copyright is pinned separately.
const ISC_NOTICE_CASES = [
  ['delete d3-color\'s year-bearing ISC copyright notice',
    '### ISC License (d3-color 3.1.0)', 'Copyright 2010-2022 Mike Bostock',
    /the "ISC License \(d3-color 3\.1\.0\)" section of THIRD-PARTY-NOTICES\.md no longer states the copyright notice/u],
  ['delete the seven-package year-bearing ISC copyright notice',
    '### ISC License (d3-dispatch, d3-drag, d3-interpolate, d3-selection, d3-timer, d3-transition, d3-zoom)',
    'Copyright 2010-2021 Mike Bostock',
    /the "ISC License \(d3-dispatch, d3-drag, d3-interpolate, d3-selection, d3-timer, d3-transition, d3-zoom\)" section of THIRD-PARTY-NOTICES\.md no longer states the copyright notice/u],
];
const ISC_NOTICE_CHECK = 'every reproduced licence text is complete, checked by normalized digest';

// SECTION-SCOPED, not a whole-file delete, and the reason is a real collision. "Copyright 2010-2021 Mike
// Bostock" is ALSO the d3-ease BSD-3-Clause notice and appears again in the holders table, so removing every
// occurrence would fail three rows at once and the case would score off whichever fired first. Cutting the
// line inside one section proves that section's pin and nothing else. A rewrite that matches nothing throws,
// exactly like `rewrite` above: a mutation that silently applied nothing scores as "NOTHING FAILED", which
// reads as a gate defect when it is a harness defect.
function deleteCopyrightInSection(notices, heading, copyrightLine) {
  const start = notices.indexOf(heading);
  if (start < 0) throw new Error(`the ISC mutation could not find the "${heading}" heading in THIRD-PARTY-NOTICES.md`);
  const after = notices.indexOf('\n### ', start + heading.length);
  const end = after < 0 ? notices.length : after;
  const region = notices.slice(start, end);
  const cut = region.split('\n').filter((l) => !l.includes(copyrightLine)).join('\n');
  if (cut === region) {
    throw new Error(`the ISC mutation found no "${copyrightLine}" line inside the "${heading}" section. `
      + 'The notice was renamed or rewrapped; re-sync this case rather than leaving it inert.');
  }
  return notices.slice(0, start) + cut + notices.slice(end);
}

const definedCaseLabels = [
  ...MUTATIONS.map((m) => m[0]),
  EDIT_RULE[0],
  NAKED_COPY[0],
  ...MARKER_NAMES.map((_, i) => markerLabelFor(i)),
  ...ISC_NOTICE_CASES.map((c) => c[0]),
];
{
  const dupes = definedCaseLabels.filter((l, i) => definedCaseLabels.indexOf(l) !== i);
  const missing = DECLARED_CASE_LABELS.filter((l) => !definedCaseLabels.includes(l));
  const undeclared = definedCaseLabels.filter((l) => !DECLARED_CASE_LABELS.includes(l));
  if (dupes.length || missing.length || undeclared.length) {
    const none = 'none';
    console.log('the mutation cases this battery DEFINES are not the set it DECLARES:');
    console.log(`  declared but not defined: ${missing.length ? missing.join(' | ') : none}`);
    console.log(`  defined but not declared: ${undeclared.length ? undeclared.join(' | ') : none}`);
    console.log(`  duplicate labels:         ${dupes.length ? dupes.join(' | ') : none}`);
    throw new Error('the defined mutation-case set does not match DECLARED_CASE_LABELS (detail above). '
      + 'Update DECLARED_CASE_LABELS deliberately. Deleting or swapping a case must never be a silent way '
      + 'to make this run green.');
  }
  // An allowance for a case that no longer exists is dead permission; it would silently widen the next
  // case that happens to reuse the label.
  const orphanAllowances = [...ALSO_BREAKS.keys()].filter((l) => !definedCaseLabels.includes(l));
  if (orphanAllowances.length) {
    throw new Error(`ALSO_BREAKS declares collateral for case(s) that do not exist: ${orphanAllowances.join(' | ')}`);
  }
}

// ---- THE CLEAN BASELINE, against the REAL GATE, and it must come first --------------------------
// WITHOUT IT THIS WHOLE BATTERY CAN BE GREEN AGAINST A BROKEN TREE. Every mutation asks "does the gate
// notice THIS damage"; none of them asks "was the tree undamaged to begin with". The acknowledgement cases
// make that worse rather than better: they inject their own manifest entry, their own notice section and
// their own gate-set membership, all derived from each other, so they would keep scoring PROVED with the
// REAL BPA entry deleted, mislabelled, or pointing at a licence nobody read.
//
// IT RUNS THE UNMODIFIED GATE ON PURPOSE. Running the rewritten non-fatal copy would make the baseline
// depend on the very rewrites this harness performs, and would skip the declared-check-name assertion that
// the rewrite strips, so a gate that had quietly lost a whole check could still baseline green. `gatePath`
// still holds the original here: the non-fatal copy is not written until the line after this block.
{
  const r = spawnSync(process.execPath, [gatePath], { cwd: resolve(repo, 'Semanticus.VSCode'), encoding: 'utf8' });
  const out = ((r.stdout || '') + (r.stderr || '')).trim();
  if (r.error) throw new Error(`could not run the gate for the baseline: ${r.error.message}`);
  if (r.status !== 0) {
    console.log('BASELINE FAILED - the REAL gate does not pass on the unmutated tree, so no mutation below');
    console.log('would mean anything. Every result is withheld until this is fixed. Gate output:');
    console.log(out.slice(0, 3000));
    throw new Error('mutation battery aborted: the gate fails before any mutation is applied (exit '
      + `${r.status === null ? `signal ${r.signal}` : r.status})`);
  }
  console.log('BASELINE    | the REAL gate passes unmutated, with every declared check running, so a');
  console.log('            | failure below is caused by the mutation and nothing else');
}

writeFileSync(gatePath, nonFatal);
let problems = 0;
let total = 0;
try {
  for (const [label, mutate, mustBreak, mustSay, lockMutate, ack, alsoBreaks] of MUTATIONS) {
    const mutated = mutate(JSON.parse(originalMan));
    writeFileSync(manPath, JSON.stringify(mutated, null, 2));
    writeFileSync(lockPath, lockMutate ? lockMutate(originalLock) : originalLock);
    if (ack) {
      // The notices section is built from the MUTATED entry, so a licence or version change cannot make the
      // notices check fire first and mask the acknowledgement assertion under test.
      const fixture = mutated.entries.find((e) => e.path === ACK_PATH);
      if (!fixture) throw new Error(`${label} is flagged as an acknowledgement case but installed no fixture`);
      writeFileSync(noticesPath, ackNotices(fixture));
      writeFileSync(gatePath, ackGate(nonFatal));
    }
    problems += score(label, mustBreak, mustSay, failures(), ALSO_BREAKS.get(label) ?? []);
    if (ack) {
      writeFileSync(noticesPath, originalNotices);
      writeFileSync(gatePath, nonFatal);
    }
    writeFileSync(lockPath, originalLock);
    total++;
  }

  writeFileSync(manPath, originalMan);
  writeFileSync(grammarPath,
    originalGrammar.replace("ABS:                                     'ABS'", "ABS:                                     'ABSOLUTE'"));
  problems += score(EDIT_RULE[0], EDIT_RULE[1], EDIT_RULE[2], failures(), ALSO_BREAKS.get(EDIT_RULE[0]) ?? []);
  total++;

  // Strip ONLY the leading comment block. Filtering every `//` line also removes the grammar's own internal
  // DAXCharStream note, which is upstream content, so the result would not match the donor and the scan would
  // correctly not fire. The pinned harness surfaced that too.
  const lines = originalGrammar.split('\n');
  let i = 0;
  while (i < lines.length && (lines[i].startsWith('//') || lines[i].trim() === '')) i++;
  writeFileSync(grammarPath, lines.slice(i).join('\n'));
  writeFileSync(manPath, JSON.stringify(drop(JSON.parse(originalMan), 'Semanticus.Core/Grammars/DAXLexer.g4'), null, 2));
  problems += score(NAKED_COPY[0], NAKED_COPY[1], NAKED_COPY[2], failures(), ALSO_BREAKS.get(NAKED_COPY[0]) ?? []);
  total++;

  // --- per-marker controls: break each marker on its own -------------------------------------------
  writeFileSync(manPath, originalMan);
  writeFileSync(grammarPath, originalGrammar);
  for (let i = 0; i < MARKER_NAMES.length; i++) {
    // Corrupt the marker in the NON-FATAL gate copy, so the harness still reports every failing check.
    writeFileSync(gatePath, corruptMarker(nonFatal, i));
    const markerLabel = markerLabelFor(i);
    problems += score(markerLabel,
      'every attribution marker is individually proved to work',
      new RegExp(`marker "${MARKER_NAMES[i].replace(/"/gu, '"')}" does not match its own fixture`),
      failures(), ALSO_BREAKS.get(markerLabel) ?? []);
    total++;
  }
  writeFileSync(gatePath, nonFatal);

  // --- the year-bearing ISC notices: delete one, the digest check must name its section ------------
  for (const [label, heading, copyrightLine, mustSay] of ISC_NOTICE_CASES) {
    writeFileSync(noticesPath, deleteCopyrightInSection(originalNotices, heading, copyrightLine));
    problems += score(label, ISC_NOTICE_CHECK, mustSay, failures(), ALSO_BREAKS.get(label) ?? []);
    writeFileSync(noticesPath, originalNotices);
    total++;
  }
} finally {
  writeFileSync(manPath, originalMan);
  writeFileSync(gatePath, originalGate);
  writeFileSync(grammarPath, originalGrammar);
  writeFileSync(lockPath, originalLock);
  writeFileSync(noticesPath, originalNotices);
  console.log(`\n(manifest, gate, grammar and notices restored) mutations=${total} unproved=${problems}`);
  // The run is only green if EVERY declared case actually ran. An early throw, a `continue` added to the
  // loop, or a case skipped for any reason would otherwise leave `problems` at 0 and exit 0.
  // The run is green only if EVERY declared case actually ran, checked by NAME. An early throw, a stray
  // `continue`, or a case skipped for any reason would otherwise leave `problems` at 0 and exit 0.
  const didNotRun = DECLARED_CASE_LABELS.filter((l) => !ranCaseLabels.includes(l));
  const ranTwice = ranCaseLabels.filter((l, i) => ranCaseLabels.indexOf(l) !== i);
  if (didNotRun.length || ranTwice.length) {
    console.log(`INCOMPLETE: ${ranCaseLabels.length} of ${DECLARED_CASE_LABELS.length} declared cases ran. `
      + 'A partial run is not a pass, whatever the unproved count says.');
    if (didNotRun.length) console.log(`  never ran: ${didNotRun.join(' | ')}`);
    if (ranTwice.length) console.log(`  ran twice: ${ranTwice.join(' | ')}`);
    process.exitCode = 1;
  } else {
    process.exitCode = problems ? 1 : 0;
  }
}
