// Borrowed-code attribution + licence-claim gate (T186).
//
// WHY THIS EXISTS. `Semanticus.Core/Grammars/DAXLexer.g4` was a verbatim copy of the vendored Tabular
// Editor 2 grammar carrying no attribution at all, and `tools/release/mirror-public.mjs` publishes
// `Semanticus.Core/`. It was being republished naked and nothing could have caught it.
//
// ── WHAT THIS GATE IS, STATED WITHOUT OVERSTATEMENT ────────────────────────────────────────────────
// It keeps DECLARED borrowed code honest, and it makes ONE class of undeclared borrowing detectable.
// It is NOT proof that everything borrowed has been declared, and it must never be cited as if it were.
//
// What it actually enforces:
//   1. Declared files exist, and their BODY still hashes to what was recorded when the licence was
//      verified. For the grammar the body is re-compared to the donor file on every run, so
//      "rules unmodified, header only" stays true instead of merely having been true once.
//   2. Declared file headers carry upstream, author, licence, URL, and for MIT the permission notice
//      itself, because a file published alone does not carry THIRD-PARTY-NOTICES.md.
//   3. THIRD-PARTY-NOTICES.md carries a SECTION PER ENTRY that ties path, licence, URL and version
//      together. A stray matching string elsewhere in the file no longer satisfies anything.
//   4. The webview production dependency closure and `webview/package-lock.json` agree in BOTH
//      directions, so a new production dependency cannot arrive unrecorded; and every production licence is
//      on an ALLOWLIST of licences we have decided we can ship, checked against the lockfile as its OWN
//      check, so a licence question can never be answered by a bookkeeping assertion.
//   5. An exact text copy of a vendored Tabular Editor file must be declared.
//   6. Manifest shape is validated, not merely non-empty: dates are dates, URLs are URLs, versions are
//      commits, and an unpinned version cannot claim a verified licence.
//   7. THE POPULATION IS TEXT, NOT A PAYLOAD, and that is the whole claim. It is the PackageReference and
//      ProjectReference elements LITERALLY WRITTEN in the derived engine project files, plus the
//      manifest's own nuget entries. Every such package that is NOT exempt is declared kind nuget or
//      classified with a reason, and THIRD-PARTY-NOTICES.md carries its licence text; the exempt ones are
//      exactly the named entries in ASSET_EXEMPT_PACKAGES, each carrying measured evidence that the
//      package stays out of the published payload at that exact occurrence. This gate evaluates no
//      MSBuild: it reads no reference out of an imported file (it refuses instead, see 9), it expands
//      no property except a `$(Name)` with exactly ONE unconditioned definition in the same file, it
//      evaluates no Condition, and it proves NOTHING about asset flow -- a PrivateAssets or
//      ExcludeAssets drop is refused unless a named exemption carries measured evidence for that exact
//      occurrence. It is NOT the resolved publish closure: transitive packages, and the whole .NET 8
//      runtime pack the self-contained publish copies into the payload, sit outside it. Deriving that
//      closure is the named open item on the board. Say "the non-exempt references written in these
//      project files", never "every shipped library": the second was written here once and was not true.
//   8. The corrected notices REACH A RECIPIENT: scripts/package.mjs stages THIRD-PARTY-NOTICES.md into the
//      extension folder and scripts/verify-vsix.mjs requires it in the payload, so a fix to the notices is
//      not a fix that stops at the repository.
//   9. A REFERENCE THIS GATE CANNOT SEE FAILS IT. MSBuild hands the scanned projects Directory.Build.props
//      and friends automatically, and honours any Import they declare, so a PackageReference written in
//      one of those files ships exactly like one in the csproj while being invisible to a text scan. Every
//      repo-controlled file the scanned projects receive is read, and the gate FAILS naming it if it
//      declares a reference, or if a declared Import cannot be resolved literally OR cannot be read at
//      all. (An auto-supplied Directory.Build.props is optional and absent-is-fine; a declared Import is
//      a stated dependency, so a missing target is refused by name.) That refusal is what keeps
//      claim 7 true; without it, "the references written in these project files" would quietly stop being
//      the population.
//
// What it does NOT do — the full list lives in the "Borrowing code" section of THIRD-PARTY-NOTICES.md and
// is summarised here so nobody has to go looking: a borrowed file with its attribution REMOVED from a
// non-vendored project is undetectable; a borrowed FRAGMENT pasted into one of our files is undetectable;
// output from a borrowed GENERATOR is undetectable; prose derivation hints ("derived from") are
// deliberately not gated because on this tree they match 49 files of ordinary commentary; NuGet is gated
// only for the PackageReferences literally written in the packaged project files, so transitives, the .NET
// runtime pack and anything an unresolved import would bring in are ungated (an import that could bring one
// in fails the gate rather than being read), and the extension-root npm closure has no gate;
// the donor-copy hash only catches exact copies of vendored
// Tabular Editor files of 512 bytes or more; and where a licence grant is recorded, the gate checks the SHAPE
// of the record, never its truth. The marker scan applies ONE set of patterns to EVERY tracked text file:
// there is no markdown carve-out, because the one that briefly existed let a wholesale borrowed README pass.
// ONE directory is exempt as a class, `docs/provenance/`, and that is a real hole stated rather than hidden: a
// borrowed file dropped in there would not be seen by the marker scan. It is accepted because that tree is
// review prose about this gate, is never compiled, and is deleted wholesale by the public mirror, so nothing
// in it can become shipped undeclared code. The donor-copy scan (section 11) still covers it.
//
// ── MIRROR CONSTRAINT, AND WHY IT BIT ─────────────────────────────────────────────────────────────
// This gate runs TWICE against two different trees: in CI against the full repository, and inside
// `mirror-public.mjs` battery A against the CURATED tree, which is the full tree minus an exclusion
// manifest (`TASKS.md`, `CLAUDE.md`, most of `docs/`, and more). Anything this gate REQUIRES to exist must
// therefore be a path the mirror KEEPS. Round two shipped a regression on exactly this: two
// `docs/archive/*.md` paths were named in an allowlist that asserted they exist, the mirror deletes them,
// so battery A hard-failed and the mirror could not push -- and CI structurally could not see it, because
// CI only ever runs against the full tree.
// Two defences now, because a comment is not a defence:
//   - `MIRROR_VISIBLE_DEPENDENCIES` restates hard dependencies as literal `resolve(repoRoot, '...')` so
//     mirror-public.mjs' own coupling detector can see them. THAT DETECTOR ONLY MATCHES THAT LITERAL FORM;
//     it does not understand allowlists, data tables, or any path built at runtime, so it would NOT have
//     caught the round-two regression. Do not rely on it for anything else.
//   - the "hard dependencies are all kept by the public mirror" check below reads the mirror's exclusion
//     manifest directly and fails if any hard dependency is excluded. That is the check that covers the
//     class. It runs only in the full repo, because mirror-public.mjs self-excludes from the curated tree,
//     which is fine: CI on the full tree is where it needs to fire.
// Anything OPTIONAL (a file the gate tolerates being absent) is safe in either tree, and the marker
// allowlist is present-conditional for exactly that reason.
//
// Run it (submodule FIRST, or the donor-copy scan fails by design rather than skipping):
//   git submodule update --init --depth 1 external/TabularEditor
//   node Semanticus.VSCode/test/attribution-and-license.test.mjs
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { execFileSync } from 'node:child_process';
import { existsSync, readFileSync, readdirSync, statSync } from 'node:fs';
import { dirname, extname, join, relative, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const extensionRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const repoRoot = resolve(extensionRoot, '..');
const read = (p) => readFileSync(resolve(repoRoot, p), 'utf8');
const norm = (s) => s.replace(/\r\n/gu, '\n');
const sha256 = (s) => createHash('sha256').update(s).digest('hex');

// Every check must run, and the SET OF NAMES is asserted at the end, not the count. A count is not a set:
// `assert.equal(checks, 22)` passed just as happily when one check was deleted and another added, so the one
// thing it was meant to prevent, a section quietly disappearing, was exactly what it could not see. The
// declared set below is the contract; the tail compares it to what actually ran and names both directions.
const EXPECTED_CHECK_NAMES = [
  'every path this gate declares as a dependency exists',
  'every path this gate hard-requires is kept by the public mirror',
  'root LICENSE is the Elastic License 2.0',
  'no authoritative doc claims a licence this repository does not use',
  'every manifest entry is fully and validly specified',
  'every declared file exists, and its body still matches what was verified',
  'a donor-body-identical claim is re-proved against the donor tree, not just asserted',
  'declared file headers carry the attribution the licence actually requires',
  'THIRD-PARTY-NOTICES.md has one section per entry tying path, licence, URL and version together',
  'every borrowed file path named in THIRD-PARTY-NOTICES.md is declared in the manifest',
  'every borrowed file embedded in a shipped binary carries its licence in the .vsix NOTICE',
  'the PackageReference parser reads every attribute shape, or fails closed on the count',
  'a commented-out PackageReference is not counted and not parsed',
  'the same package id pinned at two versions across shipped projects fails loudly',
  'the NuGet exact-version pattern accepts every exact spelling and refuses ranges and wildcards',
  'the packaged project population is derived from the project file package.mjs publishes',
  'both notices state the packaged population that the walk derives',
  'RED: a fourth referenced project fails the gate and both notices until it is declared',
  'no MSBuild file the packaged projects import declares a reference this gate cannot read',
  'RED: a rooted, expanded or listed ProjectReference Include is refused by name',
  'a conditioned or repeated $(Property) version is refused, not resolved to the first one',
  'a Condition on the parent ItemGroup travels with the reference',
  'a package dropped from the audit by an asset setting is exempted at that exact occurrence, with measured evidence',
  'every non-exempt PackageReference written in the packaged engine project files carries its licence in THIRD-PARTY-NOTICES.md (asset-drop exemptions are named with measured evidence; the resolved publish closure is open and tracked)',
  'the notice a .vsix recipient receives carries the full third-party notices',
  'every reproduced licence text is complete, checked by normalized digest',
  'acknowledged-unverified exceptions are capped and have not expired',
  'unverified licences are acknowledged, escalated, and not stale',
  'every acknowledged-unverified entry keeps a warning that carries its evidence',
  'every production dependency carries a licence we have decided we can ship',
  'the declared dependency closure and the lockfile agree in both directions',
  'every Apache-2.0 dependency has its section 4(d) NOTICE obligation resolved',
  'every attribution marker is individually proved to work',
  'every tracked file carrying a foreign attribution marker is declared or exempt',
  'the marker exemption lists are bounded, explained, and still needed',
  'no undeclared file is a verbatim copy of a vendored donor file',
  'Semanticus.Dax records that ELv2 governs it TODAY and MIT only at standalone publication',
  'the procedure for borrowing code is written down, including what the gate cannot catch',
];
const ranChecks = [];
const check = (name, fn) => { fn(); ranChecks.push(name); console.log('  [PASS] ' + name); };

// MIRROR VISIBILITY. mirror-public.mjs finds repo-root reads by matching the literal
// `resolve(repoRoot, '<path>')` in this file's source. Reads below go through `read()` with a variable,
// which that regex cannot see, so restating the dependencies as literals keeps the mirror's own gate
// working on this file. Section 1 asserts each one exists.
const MIRROR_VISIBLE_DEPENDENCIES = [
  resolve(repoRoot, 'LICENSE'),
  resolve(repoRoot, 'NOTICE'),
  resolve(repoRoot, 'THIRD-PARTY-NOTICES.md'),
  resolve(repoRoot, 'README.md'),
  resolve(repoRoot, 'RELEASE-CHECKLIST.md'),
  resolve(repoRoot, 'third-party-manifest.json'),
  resolve(repoRoot, 'Semanticus.Dax/LICENSE-NOTE.md'),
  resolve(repoRoot, 'Semanticus.VSCode/webview/package-lock.json'),
  // The notice that actually ships inside the .vsix. It is the ONLY attribution-machinery entry whose
  // protection comes solely from the survival requirement, so misfiling it into PROSE_MENTIONS would have
  // silently dropped that protection. Listed here so the mirror-curation check guards it explicitly.
  resolve(repoRoot, 'Semanticus.VSCode/NOTICE'),
  resolve(repoRoot, 'external/TabularEditor'),
  // The one repo-controlled MSBuild file the packaged projects receive automatically. The imported-
  // reference check hard-requires at least one such file to exist, so a curated tree that dropped it would
  // fail inside the mirror rather than at CI.
  resolve(repoRoot, 'Directory.Build.props'),
  // The two release scripts this gate reads as PREMISES, not as prose. The packaged-population check
  // fails outright if either is missing, because the whole ProjectReference closure is derived from what
  // package.mjs publishes and the notice-reaches-the-recipient check is read out of verify-vsix.mjs. They
  // were absent from this list for as long as those checks have existed, so a curated mirror that dropped
  // one would have failed inside the mirror -- or worse, failed at the RECIPIENT rather than at the
  // preflight, which is the direction that costs a release.
  resolve(repoRoot, 'Semanticus.VSCode/scripts/package.mjs'),
  resolve(repoRoot, 'Semanticus.VSCode/scripts/verify-vsix.mjs'),
];

// The packaged-project closure is a hard dependency of this gate too, and it is DERIVED rather than
// listed, so it cannot go in the literal block above. Defined here, before the curation check, because
// that check runs at module load and needs it; the full rationale for the walk itself is with
// `projectReferenceClosure` further down. `projectReferenceClosure` and its two helpers are function
// declarations precisely so they are callable this early.
const PACKAGE_ENTRY_PROJECT = 'Semanticus.Engine/Semanticus.Engine.csproj';
const SHIPPED_ENGINE_PROJECTS = projectReferenceClosure(PACKAGE_ENTRY_PROJECT, read).sort();

const THIS_REPO_LICENCE = 'Elastic License 2.0';
const manifest = JSON.parse(read('third-party-manifest.json'));
const entries = manifest.entries;
const notices = read('THIRD-PARTY-NOTICES.md');

// Entries whose upstream licence could NOT be verified. A temporary state, and it is temporary BY
// CONSTRUCTION: each needs an `escalation` naming who decides, and an `acknowledgedUntil` date that must
// still be in the future. When that date passes this gate FAILS, which forces the removal to actually
// happen instead of the exception quietly becoming permanent. The set is capped at one so it cannot grow
// into a parking lot.
//
// THE SET IS CURRENTLY EMPTY, and that is the goal state, not a disabled check. Its only member was
// `Semanticus.Analysis/Rules/BPARules-PowerBI.json`, copied from `TabularEditor/BestPracticeRules`, which
// publishes no licence at all. It was removed on 2026-07-30 by REPLACING that corpus with Microsoft's
// MIT-licensed `BestPracticeRules/BPARules.json` at a pinned commit whose LICENSE was actually read, not by
// relabelling the old file. The exception's own `acknowledgedUntilRationale` named that swap as its end
// condition, so this is that condition being met.
//
// An empty set makes the sections below iterate over nothing. That is why `mutation-battery.mjs` no longer
// mutates a real manifest entry for them and injects a SYNTHETIC unverified entry instead: with no real
// acknowledged entry left, mutating the manifest could not reach these assertions, and unreachable
// assertions are the exact defect this battery exists to catch. Do not delete those cases if this set is
// empty; they are the only thing keeping this machinery proven for the next entry that needs it.
const UNVERIFIED_ACKNOWLEDGED = new Set([]);
const UNVERIFIED_ACKNOWLEDGED_CAP = 1;

const SELF_LICENCE_DOCS = ['LICENSE', 'NOTICE', 'THIRD-PARTY-NOTICES.md', 'README.md', 'RELEASE-CHECKLIST.md'];
const THIRD_PARTY_MARKER = {
  'NOTICE': 'This product includes third-party software:',
  'THIRD-PARTY-NOTICES.md': '\n## ',
};

const KINDS = new Set(['copy', 'bundle', 'nuget']);
const ISO_DATE = /^20\d{2}-(?:0[1-9]|1[0-2])-(?:0[1-9]|[12]\d|3[01])$/u;
const HTTPS_URL = /^https:\/\/[a-z0-9][a-z0-9.-]*\.[a-z]{2,}(?:\/\S*)?$/iu;
const GIT_SHA = /^[0-9a-f]{40}$/u;
// NuGet's EXACT-version grammar, and only the exact one. The point of this pattern is to refuse anything
// that does not name a single shipped version, because a notice, a licence ground and a drift pin are all
// statements about one build of one package.
//
// WHAT IT USED TO GET WRONG. `^\d+\.\d+\.\d+([-+][0-9A-Za-z.]+)?$` refused two shapes NuGet itself
// publishes: the four-part `1.2.3.4` that the Microsoft client libraries and most of the System.* packages
// use, and any prerelease tag carrying a hyphen, such as `1.2.3-preview-1` or `1.2.3-rc.1-final`. A pin
// spelled either way could not be recorded at all, which pushes the answer towards "leave it unpinned" --
// the exact hole the pin exists to close.
//
// WHAT IT STILL REFUSES, deliberately: version RANGES (`[1.0,2.0)`, `(,3.0]`), WILDCARDS (`1.*`, `1.2.*`)
// and FLOATING prereleases (`1.2.3-*`). Each of those names a set of versions and lets restore choose, so
// no fact about "the version that ships" can be attached to one. Two to four numeric parts, an optional
// prerelease tag, an optional build-metadata tag, nothing else.
//
// AND EACH TAG IS DOT-SEPARATED IDENTIFIERS THAT ARE NOT EMPTY. The previous spelling
// `-[0-9A-Za-z][0-9A-Za-z.-]*` accepted `1.2.3-a..b` and `1.2.3-a.`, and the `+` branch accepted
// `1.2.3+build..5`, because a dot was just another allowed character. None of those is a version NuGet
// publishes, so the comment above claiming an exact grammar was describing a stricter pattern than the one
// written here. The identifier is spelled out instead, so the claim and the regex are the same thing.
const NUGET_VERSION =
  /^\d+\.\d+(?:\.\d+){0,2}(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$/u;
// MIT's operative sentence. Presence of the licence NAME is not the notice MIT requires.
const MIT_PERMISSION_FRAGMENTS = ['Permission is hereby granted', 'without restriction',
  'The above copyright notice and this permission notice'];
// BSD-3-Clause's operative sentences. Presence of the licence NAME is not the notice BSD requires.
const BSD_NOTICE_FRAGMENTS = [
  'Redistribution and use in source and binary forms',
  'All rights reserved.',
];

// ── THE PERMITTED-LICENCE ALLOWLIST ────────────────────────────────────────────────────────────────
// The licences we have decided this project can ship inside a published artifact. Exact canonical SPDX
// identifiers only. Everything else -- including a licence that is merely permissive but undiscussed, a
// non-SPDX spelling like "GPLv2" or "GNU General Public License v3.0", npm's `UNLICENSED`, `Proprietary`,
// an empty field, or `NO-LICENSE-FIELD` -- is an ESCALATION, because the question "may we ship this?" is a
// decision and not something a regex should infer.
const PERMITTED_LICENCES = new Set(['0BSD', 'Apache-2.0', 'BSD-2-Clause', 'BSD-3-Clause', 'ISC', 'MIT']);

// SPDX EXPRESSION RULE, stated rather than accidental:
//   plain id      -> must be in PERMITTED_LICENCES.
//   "A OR B"      -> a dual licence lets US choose, so it passes only if at least one disjunct is permitted
//                    AND the manifest entry records `licenceChoice` naming the one we take, which must
//                    itself be permitted and must be one of the disjuncts. Choosing is a decision, so it is
//                    written down, not inferred.
//   "A AND B"     -> every conjunct must be permitted; we are bound by all of them.
//   mixed OR+AND, or anything with nesting -> ESCALATION. Precedence games are exactly where a bad licence
//                    would hide, so the gate refuses to guess.
// WITH-exceptions (e.g. "GPL-2.0 WITH Classpath-exception-2.0") are escalations too: the exception changes
// the obligation and a human must read it.
function licenceVerdict(raw, entry) {
  const text = String(raw ?? '').trim();
  if (!text) return { ok: false, why: 'no licence recorded (an unknown, not a licence)' };
  if (/^(?:NO-LICENSE-FIELD|UNVERIFIED|UNLICENSED|UNKNOWN|SEE LICENSE IN .*|Proprietary|Custom)$/iu.test(text)) {
    return { ok: false, why: `"${text}" is not a licence grant; read the package's own LICENSE file or escalate` };
  }
  if (/\bWITH\b/iu.test(text)) return { ok: false, why: `"${text}" carries a licence exception; a human must read it` };
  const flat = text.replace(/[()]/gu, ' ').replace(/\s+/gu, ' ').trim();
  const hasOr = /\bOR\b/iu.test(flat);
  const hasAnd = /\bAND\b/iu.test(flat);
  if (hasOr && hasAnd) return { ok: false, why: `"${text}" mixes OR and AND; the gate refuses to guess precedence` };
  if (hasAnd) {
    const parts = flat.split(/\s+AND\s+/iu).map((s) => s.trim());
    const bad = parts.filter((s) => !PERMITTED_LICENCES.has(s));
    return bad.length
      ? { ok: false, why: `"${text}" binds us to ${bad.join(' and ')}, which is not permitted` }
      : { ok: true };
  }
  if (hasOr) {
    const parts = flat.split(/\s+OR\s+/iu).map((s) => s.trim());
    const okParts = parts.filter((s) => PERMITTED_LICENCES.has(s));
    if (!okParts.length) return { ok: false, why: `"${text}" offers no permitted option` };
    const choice = entry?.licenceChoice;
    if (!choice) {
      return { ok: false, why: `"${text}" is a dual licence; record licenceChoice (one of ${okParts.join(', ')})` };
    }
    if (!parts.includes(choice)) return { ok: false, why: `licenceChoice "${choice}" is not one of "${text}"` };
    if (!PERMITTED_LICENCES.has(choice)) return { ok: false, why: `licenceChoice "${choice}" is not permitted` };
    return { ok: true };
  }
  return PERMITTED_LICENCES.has(flat)
    ? { ok: true }
    : { ok: false, why: `"${text}" is not a permitted SPDX identifier (non-SPDX spellings are escalations)` };
}

// WHAT THIS EXEMPTION LIST IS FOR: files whose JOB is to record third-party attribution. They necessarily
// contain attribution markers, so exempting them is not a concession, it is the definition. It is NOT for
// "documents that happen to mention a licence" -- those go in PROSE_MENTIONS below, which is separate
// precisely so the mirror-curation check does not require them to survive curation. Every entry is an EXACT path (no directory-wide
// exclusions, which would hide a borrowed helper) and every entry is a path the public mirror KEEPS, which
// the mirror-curation check asserts. Declared here rather than beside the scan so the curation check, which
// runs first, can see it.
const ATTRIBUTION_MACHINERY = new Map([
  ['NOTICE', "this repository's own notice file"],
  ['THIRD-PARTY-NOTICES.md', 'the third-party notices themselves, including verbatim licence texts'],
  ['third-party-manifest.json', 'the manifest that records these markers'],
  ['Semanticus.VSCode/NOTICE', 'the notice shipped inside the .vsix'],
  // RESTORED, on its real justification. This exemption was removed in round three on the claim that the gate
  // writes its markers as regex SOURCE (`copyright\s*…`) and therefore cannot match them. That claim was
  // false: line ~296 carries the plain-English literal "licensed under" inside an assertion message, so the
  // file genuinely trips the marker. The exemption only LOOKED unnecessary because two of the five markers
  // had been silently disabled by a raw backspace byte (see the FOREIGN_MARKERS note). With the patterns
  // repaired, replaying the scan yields exactly one hit: this file. The present-conditional check was right
  // both times; the reasoning behind the removal was what was wrong.
  ['Semanticus.VSCode/test/attribution-and-license.test.mjs',
    'this gate quotes "licensed under" verbatim in its own assertion messages while defining the marker set'],
  ['tools/attribution/mutation-battery.mjs',
    'the mutation battery names each marker it breaks, so it quotes the marker phrases verbatim'],
  // The same category as THIRD-PARTY-NOTICES.md, and here for the same reason: its job IS attribution. JSON
  // admits no comment, so this file is where MIT's copyright and permission notice travel beside
  // BPARules-PowerBI.json, quoted verbatim from the upstream LICENSE. Quoting them is precisely what trips the
  // marker scan, which is the scan working. It is an exact path and the public mirror keeps it (it is in
  // neither exclusion list in tools/release/mirror-manifest.json), which this list requires.
  ['Semanticus.Analysis/Rules/BPARules-PowerBI.PROVENANCE.md',
    'records the provenance of the borrowed BPA rule corpus and quotes its MIT copyright and permission '
    + 'notice verbatim'],
]);

// WHY THIS EXISTS. Membership of ATTRIBUTION_MACHINERY is a BLUNT instrument: the marker scan skips an
// exempt path before it even reads the file, so an exempt file could acquire a completely unrelated
// vendor's notice, or a whole borrowed helper, and nothing here would look at it. The "still needed"
// check below only proves that SOME marker is still present, which a file full of new borrowings also
// satisfies. So for exempt files whose attribution content is narrow and known, declare WHICH copyright
// holders they are allowed to name. A new holder appearing is then a failure rather than a blind spot.
//
// Not applied to every exempt path: NOTICE, THIRD-PARTY-NOTICES.md and the manifest legitimately name
// every vendor we use, so constraining them would be a list that has to be edited on every borrowing and
// would fail open the moment it drifted. It is applied where the file documents exactly ONE upstream.
// The value is the EXACT holder name, not a pattern. A substring test was the first attempt and it is
// defeated by the two obvious strings: "Copyright (c) Not Microsoft" contains "Microsoft" and passed, and
// "Copyright ©  Evil" was not even recognised as a copyright line because only the "(c)" spelling was
// matched. Both are covered below: every recognised form is parsed, the holder is extracted, and it must
// EQUAL this string.
const EXEMPTION_COPYRIGHT_SCOPE = new Map([
  ['Semanticus.Analysis/Rules/BPARules-PowerBI.PROVENANCE.md', 'Microsoft'],
]);

// Every spelling we intend to recognise: "(c)", "(C)", the © sign, and an optional year or year range.
// One regex, so the detector and the holder parser cannot drift apart, which is how the © form went
// unnoticed in the first version.
// A SIGIL IS MANDATORY: "(c)", "(C)", "©", or a bare 4-digit year. Making it optional matched the word
// "copyright" in ordinary prose, so MIT's own sentences ("the copyright notice and this permission notice",
// "AUTHORS OR COPYRIGHT HOLDERS BE LIABLE") were read as attribution lines with nonsense holders. An
// attribution line always carries a sigil or a year; a sentence about copyright does not.
const COPYRIGHT_PREFIX =
  /copyright\s*(?:(?:\((?:c|C)\)|©)\s*(?:\d{4}(?:\s*[-–]\s*\d{4})?)?|\d{4}(?:\s*[-–]\s*\d{4})?)\s*/giu;

// Pull the holder out of a copyright line and normalise it for an EXACT comparison. Markdown wrappers and
// trailing prose are cut at the first terminator.
//
// EVERY occurrence on the line is considered, not just the first. A markdown table row reads
// `| Copyright | \`Copyright (c) 2016 Microsoft\` (quoted exactly...) |`, where the first "Copyright" is the
// row LABEL and yields an empty holder; taking only the first match returned "" and the real holder was
// never examined. The first non-empty holder wins instead.
function copyrightHolderOf(line) {
  const re = new RegExp(COPYRIGHT_PREFIX.source, 'giu');
  for (let m = re.exec(line); m !== null; m = re.exec(line)) {
    const holder = line.slice(m.index + m[0].length)
      .split(/[`"'|<(]/u)[0]          // stop at markdown, quote, table-cell or paren boundaries
      .replace(/[.,;:]+$/u, '')
      .replace(/\s+/gu, ' ')
      .trim();
    if (holder) return holder;
  }
  return null;
}
// SEPARATE LIST, and the separation is load-bearing. These are internal documents that DISCUSS licences --
// prose, not borrowed code. They are exempt from the marker scan but they are NOT hard dependencies of this
// gate, so the mirror-curation check must not require them to survive curation. Conflating the two lists is
// exactly what broke the mirror: `docs/archive/*` was asserted to exist and the mirror deletes it. Every
// entry here is validated PRESENT-CONDITIONALLY.
//
// A KEY ENDING IN `/` IS A DIRECTORY CLASS, not a file. It is the ONE place in this gate where a
// directory-wide exemption is permitted, and it is permitted here and not in ATTRIBUTION_MACHINERY because
// the risk is different: ATTRIBUTION_MACHINERY guards files that SHIP, so a directory there could hide a
// borrowed helper inside a published tree. There is exactly one class, `docs/provenance/`, and it exists
// because a per-file list of review records is a trap: every new review record breaks main, so the gate would
// punish documenting it. That is the class of problem this project is trying to eliminate, not create.
// A class is still validated: the "still needed" check below requires at least one tracked file under the
// prefix to actually trip a marker, so a class that has stopped being needed does not sit here unnoticed.
const PROSE_MENTIONS = new Map([
  ['TASKS.md', 'the internal task register, which quotes licence evidence verbatim'],
  ['docs/archive/01-monetization.md', "archived strategy prose comparing other products' licences"],
  ['docs/archive/02-foundation-te2-rewrite.md', "archived strategy prose comparing other products' licences"],
  ['docs/provenance/',
    'review records under docs/provenance/ are prose accounts of this gate itself: they QUOTE the markers it '
    + 'hunts for rather than carrying any borrowed code, they are never compiled, and docs/ is dropped '
    + 'wholesale from the public mirror except an explicit allowlist, so they cannot become shipped '
    + 'undeclared code'],
]);
// Exact path, or inside a declared directory class. Directory classes are the `/`-terminated keys above.
const PROSE_MENTION_CLASSES = [...PROSE_MENTIONS.keys()].filter((k) => k.endsWith('/'));
const proseMentionExempt = (rel) => PROSE_MENTIONS.has(rel)
  || PROSE_MENTION_CLASSES.some((dir) => rel.startsWith(dir));

// --- 1. This gate's declared dependencies exist ---------------------------------------------------
check('every path this gate declares as a dependency exists', () => {
  for (const abs of MIRROR_VISIBLE_DEPENDENCIES) {
    assert.ok(existsSync(abs), `this gate declares a dependency on ${abs}, which does not exist`);
  }
});

// --- 1a. Everything this gate REQUIRES survives the public mirror's curation ----------------------
// The check that covers the round-two regression class. It parses the mirror's exclusion manifest out of
// tools/release/mirror-public.mjs and fails if any hard dependency of this gate would be deleted from the
// curated tree. Runs in the full repo only: mirror-public.mjs self-excludes, so inside the curated tree
// there is nothing to read, and CI on the full tree is where this needs to fire.
check('every path this gate hard-requires is kept by the public mirror', () => {
  // mirror-optional: the mirror script self-excludes, so it is absent from the curated tree and ONLY there.
  // This read is guarded below and the check returns early rather than failing. The annotation is what tells
  // mirror-public.mjs' coupling detector that this is a tolerated absence and not a hard dependency.
  const mirrorPath = resolve(repoRoot, 'tools/release/mirror-public.mjs'); // mirror-optional
  if (!existsSync(mirrorPath)) {
    // Inside the curated tree. Not a silent pass: if the gate had a bad dependency, CI on the full repo
    // would already have failed this check before any mirror ran.
    console.log('       (mirror script absent: this IS the curated tree, so the full-repo run already enforced it)');
    return;
  }
  // THE MANIFEST IS DATA. It used to be JavaScript literals inside mirror-public.mjs, parsed back out here
  // with a regex, and that parser was a liability: one apostrophe inside a code comment ("Kane's internal
  // intent doc") re-paired every quote after it and silently DROPPED TASKS.md, and double quotes, a template
  // literal or a unicode quote would each drop an entry too. Omission is the direction that matters and
  // pairing-shift controls do not catch it. Both sides now read one JSON file, and the parser is gone.
  const manifestPath = resolve(repoRoot, 'tools/release/mirror-manifest.json'); // mirror-optional: self-excludes
  assert.ok(existsSync(manifestPath),
    'tools/release/mirror-manifest.json is missing but mirror-public.mjs is present. The mirror cannot curate '
    + 'without it, so this is a broken release path, not a tolerable absence.');
  const mm = JSON.parse(readFileSync(manifestPath, 'utf8'));
  const excludeDirs = mm.excludeDirs.map((e) => e.path);
  const excludeFiles = mm.excludeFiles.map((e) => e.path);
  const docsKeep = new Set(mm.docsKeep.map((e) => e.path));
  // The script must actually be reading this same file, or the two could drift apart again. Since the tenant-leak
  // gate landed, the read is TWO HOPS: mirror-public.mjs imports ./mirror-manifest.mjs, and that thin loader is the
  // only thing that reads the json. Assert the whole chain. Asserting only that mirror-public.mjs mentions the json
  // would be satisfied by a COMMENT naming it, which is how a check ends up proving nothing.
  const loaderPath = resolve(repoRoot, 'tools/release/mirror-manifest.mjs'); // mirror-optional: self-excludes
  assert.ok(existsSync(loaderPath),
    'tools/release/mirror-manifest.mjs is missing but mirror-public.mjs is present. The mirror imports the manifest '
    + 'through that loader, so this is a broken release path, not a tolerable absence.');
  assert.match(readFileSync(mirrorPath, 'utf8'), /from\s+'\.\/mirror-manifest\.mjs'/u,
    'mirror-public.mjs no longer imports ./mirror-manifest.mjs, so the manifest this check validates is not the '
    + 'one the mirror applies');
  assert.match(readFileSync(loaderPath, 'utf8'), /mirror-manifest\.json/u,
    'tools/release/mirror-manifest.mjs no longer reads mirror-manifest.json, so the json this check validates is '
    + 'not the data the mirror applies');
  for (const e of [...mm.excludeDirs, ...mm.excludeFiles, ...mm.docsKeep]) {
    assert.ok(typeof e.why === 'string' && e.why.length > 8, `mirror-manifest entry ${e.path} records no reason`);
  }
  assert.ok(excludeDirs.length > 3 && excludeFiles.length > 3,
    'parsed a suspiciously small exclusion manifest, so this check would be vacuous');

  const mirrorExcludes = (p) => excludeFiles.includes(p)
    || excludeDirs.some((d) => p === d || p.startsWith(`${d}/`))
    || (p.startsWith('docs/') && !docsKeep.has(p));

  // Hard requirements = the literal dependency list, plus every manifest entry path and its donor path,
  // plus the attribution-machinery exemptions. If any of these is curated away, battery A cannot pass.
  const hard = new Set([
    ...MIRROR_VISIBLE_DEPENDENCIES.map((abs) => relative(repoRoot, abs).split('\\').join('/')),
    ...entries.map((e) => e.path),
    ...entries.filter((e) => e.donorPath).map((e) => e.donorPath),
    ...ATTRIBUTION_MACHINERY.keys(),
    // The DERIVED packaged-project closure. Every csproj in it is read by this gate at module load, so a
    // curated tree missing one cannot even start. Listing the walk's OUTPUT rather than three names is the
    // same discipline the population check uses: a fourth referenced project is covered the day it lands.
    ...SHIPPED_ENGINE_PROJECTS,
  ]);
  const doomed = [...hard].filter(mirrorExcludes).sort();
  assert.deepEqual(doomed, [],
    `this gate hard-requires path(s) that tools/release/mirror-public.mjs DELETES from the curated tree, so `
    + `battery A would fail inside the mirror and it could not push: ${doomed.join(', ')}. `
    + 'Either stop requiring them, make the requirement present-conditional, or change the exclusion manifest deliberately.');

  // Positive control: the parsed manifest must actually be able to exclude something.
  assert.equal(mirrorExcludes('TASKS.md'), true,
    'positive control failed: the parsed exclusion manifest does not exclude TASKS.md, so it was parsed wrongly '
    + 'and this check would pass everything');
  assert.equal(mirrorExcludes('NOTICE'), false,
    'positive control failed: the parsed exclusion manifest claims NOTICE is excluded, which it is not');
});

// --- 2. The repository's own licence --------------------------------------------------------------
check('root LICENSE is the Elastic License 2.0', () => {
  const licence = read('LICENSE');
  assert.match(licence, /Elastic License 2\.0/u, 'root LICENSE is not the Elastic License 2.0');
  assert.doesNotMatch(licence, /^MIT License/mu, 'root LICENSE still carries the superseded MIT text');
  assert.equal(manifest.repositoryLicence, THIS_REPO_LICENCE,
    'the manifest records a different repository licence than LICENSE');
});

// --- 3. No authoritative doc claims a licence this repository does not use ------------------------
check('no authoritative doc claims a licence this repository does not use', () => {
  for (const name of SELF_LICENCE_DOCS) {
    const full = read(name);
    const marker = THIRD_PARTY_MARKER[name];
    const idx = marker ? full.indexOf(marker) : -1;
    const selfRegion = idx >= 0 ? full.slice(0, idx) : full;
    // Trailing punctuation is trimmed rather than excluded from the class: excluding '.' truncated
    // "Elastic License 2.0" to "Elastic License 2".
    for (const m of selfRegion.matchAll(/[Ll]icens(?:ed|e[ds]?)\s+under\s+(?:the\s+)?([^,;\n(]{3,40})/gu)) {
      const named = m[1].replace(/[*`_]/gu, '').replace(/[\s.]+$/u, '').trim();
      assert.match(named, /^Elastic License 2\.0\b/u,
        `${name} says "licensed under ${named}" about this repository; the ratified licence is ${THIS_REPO_LICENCE}`);
    }
    assert.doesNotMatch(selfRegion, /once chosen|licen[cs]e\s+(?:is\s+)?(?:still\s+)?(?:to\s+be|not\s+yet|un)(?:\s+)?(?:chosen|decided|selected)/iu,
      `${name} still presents this repository's licence as undecided; ${THIS_REPO_LICENCE} was ratified in PR #149`);
  }
});

// --- 4. Manifest shape: typed, not merely non-empty -----------------------------------------------
check('every manifest entry is fully and validly specified', () => {
  assert.ok(entries.length > 0, 'the manifest declares nothing, so every later section would be vacuous');
  const seen = new Set();
  for (const e of entries) {
    const where = `manifest entry ${e.path ?? '(no path)'}`;
    for (const field of ['path', 'kind', 'upstream', 'url', 'author', 'licence', 'version', 'verifiedOn',
      'evidence', 'noticeSection', 'upstreamEquivalence']) {
      assert.ok(typeof e[field] === 'string' && e[field].trim().length > 0, `${where} is missing "${field}"`);
    }
    assert.equal(typeof e.licenceVerified, 'boolean', `${where} must state licenceVerified as a boolean`);
    assert.equal(typeof e.header, 'boolean', `${where} must state header as a boolean`);
    assert.ok(!seen.has(e.path), `${where} is declared twice`);
    seen.add(e.path);

    assert.ok(KINDS.has(e.kind), `${where} has kind "${e.kind}"; allowed kinds are ${[...KINDS].join(', ')}`);
    assert.match(e.verifiedOn, ISO_DATE, `${where} verifiedOn "${e.verifiedOn}" is not an ISO date`);
    assert.match(e.url, HTTPS_URL, `${where} url "${e.url}" is not an https URL`);
    // A version must identify a point in the upstream's history, or say plainly that it does not.
    const versionOk = GIT_SHA.test(e.version) || e.version.startsWith('unpinned')
      || (e.kind === 'bundle' && e.version.includes('package-lock.json'))
      || (e.kind === 'nuget' && NUGET_VERSION.test(e.version));
    assert.ok(versionOk,
      `${where} version "${e.version}" is neither a 40-character commit SHA, nor "unpinned…", nor a lockfile reference, nor a NuGet version`);
    // You cannot have verified a licence "at the pinned version" when no version was recorded.
    if (e.version.startsWith('unpinned')) {
      assert.equal(e.licenceVerified, false,
        `${where} claims a verified licence at an unpinned version, which is not something anyone can have checked`);
    }
    assert.doesNotMatch(e.evidence, /\bbelieved\b|\bassumed\b|\bprobably\b|\blikely\b/iu,
      `${where} hedges its licence evidence; verify it at the pinned version or set licence to "UNVERIFIED"`);
    if (!e.licenceVerified) {
      assert.equal(e.licence, 'UNVERIFIED',
        `${where} has licenceVerified false but names a licence anyway; an unverified licence must read "UNVERIFIED"`);
    }
    // header:false is not a free-text escape hatch. Only formats that genuinely cannot carry a comment.
    if (e.header === false) {
      assert.ok(typeof e.headerWaiverReason === 'string' && e.headerWaiverReason.trim().length > 0,
        `${where} carries no attribution header and gives no headerWaiverReason`);
      const commentless = extname(e.path).toLowerCase() === '.json';
      assert.ok(commentless || e.kind === 'bundle' || e.kind === 'nuget',
        `${where} sets header:false, but ${extname(e.path)} files can carry a comment and this is not a generated bundle or NuGet binary. `
        + 'The only exceptions are comment-less formats (JSON), generated bundles, and NuGet binaries.');
    }
    // Every copy must be pinned by content, or its "unmodified" claim decays silently.
    if (e.kind === 'copy') {
      assert.match(e.bodySha256 ?? '', /^[0-9a-f]{64}$/u,
        `${where} is a copy with no valid bodySha256, so nothing stops its content changing under a stale claim`);
    }
  }
});

// --- 5. manifest -> tree: files exist and bodies are unchanged since verification ------------------
// The body is the file with its leading comment block stripped, so our attribution header does not count
// as a modification to upstream's content.
function bodyOf(rel) {
  const lines = norm(read(rel)).split('\n');
  let i = 0;
  while (i < lines.length && (lines[i].startsWith('//') || lines[i].trim() === '')) i++;
  return lines.slice(i).join('\n');
}

check('every declared file exists, and its body still matches what was verified', () => {
  for (const e of entries) {
    if (e.kind === 'nuget') continue; // NuGet binaries have no path in this tree
    assert.ok(existsSync(resolve(repoRoot, e.path)),
      `the manifest declares ${e.path}, which does not exist; a notice naming a file that is gone is a false claim`);
    if (e.kind !== 'copy') continue;
    const actual = sha256(e.path.toLowerCase().endsWith('.json') ? norm(read(e.path)) : bodyOf(e.path));
    assert.equal(actual, e.bodySha256,
      `${e.path} has changed since its licence was verified (body sha256 ${actual}, manifest says ${e.bodySha256}). `
      + 'The manifest and THIRD-PARTY-NOTICES.md still describe the old content. Re-verify the upstream licence at '
      + 'the version you took, then update bodySha256, version and evidence together.');
  }
});

check('a donor-body-identical claim is re-proved against the donor tree, not just asserted', () => {
  const claims = entries.filter((e) => e.upstreamEquivalence === 'donor-body-identical');
  assert.ok(claims.length > 0, 'no entry claims donor-body identity, so this section would be vacuous');
  for (const e of claims) {
    assert.ok(typeof e.donorPath === 'string' && e.donorPath.startsWith('external/'),
      `${e.path} claims donor-body identity but records no donorPath under external/`);
    const donor = resolve(repoRoot, e.donorPath);
    assert.ok(existsSync(donor),
      `${e.path} claims identity with ${e.donorPath}, which is not checked out. Run `
      + '`git submodule update --init --depth 1 external/TabularEditor`. This gate does not skip.');
    assert.equal(sha256(bodyOf(e.path)), sha256(norm(readFileSync(donor, 'utf8'))),
      `${e.path} is no longer identical to ${e.donorPath} once its attribution header is stripped, but the manifest `
      + 'and notices still claim the upstream content is unmodified. Either the copy was edited or the donor pin moved.');

    // The recorded version must be the REAL pinned commit, not merely 40 hex characters. Shape validation
    // accepted 'a'.repeat(40) as a pin, which defeats "an unpinned version cannot claim a verified licence".
    // For a vendored donor the truth is on disk: the submodule gitlink.
    const submodule = e.donorPath.split('/').slice(0, 2).join('/');
    // Read the pin from the INDEX, not from HEAD. `git ls-tree HEAD` is wrong in both trees that matter: the
    // curated tree has no commit yet when the batteries run, and inside the real mirror HEAD is the PUBLIC
    // repo's previous commit, so it would report a stale pin and fail spuriously. The index is what the
    // mirror stages before battery A, and it is what a normal checkout reports too. Falls back to the donor
    // clone's own HEAD, and FAILS if neither is readable: an unresolvable pin is an unknown, not a pass.
    let actual = null;
    try {
      const staged = execFileSync('git', ['ls-files', '-s', submodule], { cwd: repoRoot, encoding: 'utf8' });
      actual = (/^160000 ([0-9a-f]{40})/u.exec(staged.trim()) ?? [])[1] ?? null;
    } catch { /* fall through to the donor clone */ }
    if (!actual) {
      try {
        actual = execFileSync('git', ['rev-parse', 'HEAD'],
          { cwd: resolve(repoRoot, submodule), encoding: 'utf8' }).trim();
      } catch { /* reported below */ }
    }
    assert.ok(/^[0-9a-f]{40}$/u.test(actual ?? ''),
      `could not resolve the ${submodule} pin from the git index or from the donor clone, so ${e.path}'s recorded `
      + 'version cannot be verified. This fails rather than passing, because an unverifiable pin is an unknown.');
    assert.equal(e.version, actual,
      `${e.path} records version ${e.version} but ${submodule} is actually pinned at ${actual}. The recorded pin `
      + 'must be the commit the licence was read at, and 40 hex characters that match nothing is not a pin.');
  }
});

check('declared file headers carry the attribution the licence actually requires', () => {
  for (const e of entries) {
    if (!e.header) continue;
    const head = norm(read(e.path)).split('\n').slice(0, 60).join('\n');
    for (const fragment of [e.upstream, e.author, e.licence, e.url]) {
      assert.ok(head.includes(fragment),
        `${e.path} has no attribution header naming "${fragment}" in its first 60 lines; the mirror publishes this file, so it must carry its own provenance`);
    }
    // MIT requires the copyright notice AND the permission notice in copies. Naming "MIT" is not that.
    if (e.licence === 'MIT') {
      for (const fragment of MIT_PERMISSION_FRAGMENTS) {
        assert.ok(head.includes(fragment),
          `${e.path} names MIT but its header omits the permission notice ("${fragment}"). MIT requires the copyright `
          + 'AND permission notice to be included in copies, and a file published on its own does not carry THIRD-PARTY-NOTICES.md.');
      }
    }
  }
});

// --- 6. manifest -> notices, PER SECTION -----------------------------------------------------------
// A global `includes` let a stray path anywhere in the file satisfy an entry. Each entry now names its own
// `## ` section and everything must be found inside it.
function noticeSection(heading) {
  const marks = [...notices.matchAll(/^## .*$/gmu)];
  const i = marks.findIndex((m) => m[0].includes(heading));
  if (i < 0) return null;
  const start = marks[i].index;
  const end = i + 1 < marks.length ? marks[i + 1].index : notices.length;
  return notices.slice(start, end);
}

check('THIRD-PARTY-NOTICES.md has one section per entry tying path, licence, URL and version together', () => {
  for (const e of entries) {
    const section = noticeSection(e.noticeSection);
    assert.ok(section, `THIRD-PARTY-NOTICES.md has no "## …${e.noticeSection}…" section for ${e.path}`);
    for (const [label, value] of [['path', e.path], ['licence', e.licence], ['URL', e.url], ['version', e.version]]) {
      assert.ok(section.includes(value),
        `the "${e.noticeSection}" section of THIRD-PARTY-NOTICES.md does not state the ${label} "${value}" for ${e.path}`);
    }
  }
});

// --- 7. notices -> manifest: no borrowed FILE described but undeclared ----------------------------
check('every borrowed file path named in THIRD-PARTY-NOTICES.md is declared in the manifest', () => {
  const declared = new Set(entries.map((e) => e.path));
  const tracked = new Set(trackedFiles());
  const orphans = [];
  // Attribution sections only: the "Borrowing code" procedure legitimately names our own machinery.
  const procIdx = notices.indexOf('## Borrowing code');
  assert.ok(procIdx >= 0, 'the Borrowing code procedure section is missing, so this scan cannot be scoped');
  const attributionRegion = notices.slice(notices.indexOf('\n## ', procIdx + 1));
  assert.ok(attributionRegion.length > 500, 'no attribution sections found after the procedure section');
  for (const m of attributionRegion.matchAll(/`([A-Za-z0-9_.-]+(?:\/[A-Za-z0-9_.-]+)+)`/gu)) {
    const p = m[1];
    if (p.startsWith('external/') || p.includes('node_modules/')) continue;   // not our file
    if (!tracked.has(p) || declared.has(p)) continue;
    orphans.push(p);
  }
  assert.deepEqual(orphans, [],
    `THIRD-PARTY-NOTICES.md describes tracked file(s) that the manifest does not declare: ${orphans.join(', ')}`);
});

// --- 7b. The BINARY recipient's notice, not just the source tree's --------------------------------
// THE GAP THIS CLOSES, found in review 2026-07-30. Everything above proves the SOURCE tree is honest:
// THIRD-PARTY-NOTICES.md, the manifest, and per-file headers all live in the repository, and the public
// source mirror carries them. A .vsix recipient receives none of that. They receive `extension/NOTICE`
// and compiled DLLs. Any borrowed file declared with <EmbeddedResource> is compiled INTO one of those
// DLLs and no sidecar document follows it, so unless its licence is in the SHIPPED notice, the binary is
// redistributed with no notice at all. That is a licence-compliance failure, not bookkeeping, and it was
// live: the BPA rule corpus was embedded in Semanticus.Analysis.dll while Semanticus.VSCode/NOTICE named
// only Tabular Editor 2 and the Analysis Services client libraries.
const VSIX_NOTICE_PATH = 'Semanticus.VSCode/NOTICE';

// WHAT THIS RESOLVER DOES NOT DO, stated so nobody reads it as full coverage. It is a TEXT SCAN for
// literal `<EmbeddedResource Include="...">` values in tracked `.csproj` files. It does NOT evaluate the
// MSBuild graph, so it cannot see: a glob or wildcard Include, a resource contributed by a `.props` or
// `.targets` import, one generated during the build, one added by a NuGet package, or one whose path is
// built from an MSBuild property. Entries containing `$(` are skipped outright, which currently means the
// vendored `$(TE2)\TOMWrapper\Messages.resx` is invisible here; that content is Tabular Editor 2's and is
// covered by the Tabular Editor 2 section of the shipped notice, not by this check.
//
// It is therefore a TRIPWIRE for the common case (a hand-added embedded file), not a proof that every
// embedded resource is accounted for. Evaluating the real graph would mean invoking MSBuild from a Node
// test, which this gate deliberately does not do. If an embedded resource ever arrives by any of the routes
// above, this check will not see it and the manifest is the only remaining defence.
function embeddedResourcePaths() {
  const out = new Set();
  for (const proj of trackedFiles()) {
    if (!proj.endsWith('.csproj') || proj.startsWith('external/')) continue;
    let text;
    try { text = read(proj); } catch { continue; }
    const dir = proj.includes('/') ? proj.slice(0, proj.lastIndexOf('/')) : '';
    for (const m of text.matchAll(/<EmbeddedResource\s+Include="([^"]+)"/gu)) {
      const raw = m[1];
      if (raw.includes('$(')) continue;
      const rel = raw.split('\\').join('/');
      // Resolve ../ segments against the project directory.
      const parts = (dir ? dir.split('/') : []).concat(rel.split('/'));
      const stack = [];
      for (const seg of parts) {
        if (seg === '.' || seg === '') continue;
        if (seg === '..') stack.pop();
        else stack.push(seg);
      }
      out.add(stack.join('/'));
    }
  }
  return out;
}

// Embedded resources that are OUR OWN work. Listed rather than ignored, so that a new embedded resource has
// to be classified by a human instead of silently escaping the check below.
const FIRST_PARTY_EMBEDDED = new Map([
  ['Semanticus.Engine/format-templates.json', 'ours: the DAX format-string template catalogue'],
  ['docs/interview-fix-map.json', 'ours: maps interview findings to fix ops'],
  ['docs/interview-hard-pack.json', 'ours: the authored hard-question interview pack'],
]);

// The shipped NOTICE is blocks separated by rules of dashes, not markdown headings, so it needs its own
// splitter. Fragments are matched INSIDE the relevant block: matching the whole file let a fragment that
// belongs to a DIFFERENT entry satisfy this one, and it really did. "MIT" was already in the file for
// Tabular Editor 2, so the BPA entry's licence name was "found" before its own section existed at all.
function vsixNoticeBlock(heading) {
  const blocks = norm(read(VSIX_NOTICE_PATH)).split(/^-{10,}$/mu);
  return blocks.find((b) => {
    const firstLine = b.split('\n').find((l) => l.trim().length > 0) ?? '';
    return firstLine.includes(heading);
  }) ?? null;
}

check('every borrowed file embedded in a shipped binary carries its licence in the .vsix NOTICE', () => {
  const embedded = embeddedResourcePaths();
  const declared = new Set(entries.map((e) => e.path));

  // AN EMBEDDED RESOURCE NOBODY DECLARED IS THE REAL HOLE. Previously this check only looked at files the
  // manifest already declared, so the one thing it could never notice was a borrowed file being embedded
  // without ever being written down, which is precisely how something ships with no notice.
  const unclassified = [...embedded]
    .filter((p) => !declared.has(p) && !FIRST_PARTY_EMBEDDED.has(p))
    .sort();
  assert.deepEqual(unclassified, [],
    `these files are compiled into a shipped binary as embedded resources but are neither declared in `
    + `third-party-manifest.json nor listed as first-party: ${unclassified.join(', ')}. If a file is ours, `
    + 'add it to FIRST_PARTY_EMBEDDED with a reason. If it is borrowed, declare it in the manifest and put '
    + 'its licence in the shipped notice. Silence is not an option: an embedded borrowed file ships inside '
    + 'the DLL with no sidecar document to carry its terms.');

  const shipped = entries.filter((e) => embedded.has(e.path));

  // NON-VACUITY. If this ever finds nothing it has stopped testing anything, and the most likely cause is
  // that the Include= spelling changed rather than that the borrowing stopped.
  assert.ok(shipped.length > 0,
    'no declared borrowed file resolves to an <EmbeddedResource>, so this check proves nothing. Either the '
    + 'manifest no longer declares an embedded file, or embeddedResourcePaths() has stopped matching the '
    + 'csproj spelling. Find out which before deleting this check.');

  for (const e of shipped) {
    assert.ok(typeof e.shippedNoticeHeading === 'string' && e.shippedNoticeHeading.trim().length > 0,
      `${e.path} is an embedded resource, so the shipped notice must carry it, but the manifest entry records `
      + 'no "shippedNoticeHeading" naming its block in that file.');
    const block = vsixNoticeBlock(e.shippedNoticeHeading);
    assert.ok(block,
      `${VSIX_NOTICE_PATH} has no block whose first line contains "${e.shippedNoticeHeading}" for ${e.path}. `
      + 'That file is packaged as extension/NOTICE and is the SUMMARY notice a .vsix recipient receives. '
      + 'THIRD-PARTY-NOTICES.md ships beside it now, so it is no longer the only one; it is still the one '
      + 'a reader opens first, so an embedded file missing from it is missing.');

    const missing = [];
    // The URL and the licence NAME identify it; for MIT the permission notice IS the obligation.
    for (const fragment of [e.url, e.licence]) {
      if (!block.includes(fragment)) missing.push(fragment);
    }
    if (e.licence === 'MIT') {
      for (const fragment of MIT_PERMISSION_FRAGMENTS) {
        if (!block.includes(fragment)) missing.push(fragment);
      }
    }
    assert.deepEqual(missing, [],
      `${e.path} is compiled into a shipped binary as an embedded resource, but its own `
      + `"${e.shippedNoticeHeading}" block in ${VSIX_NOTICE_PATH} (packaged as extension/NOTICE, the notice `
      + `a .vsix recipient reads first) is missing: ${missing.join(' | ')}. Another entry's copy of the `
      + 'same text elsewhere in the file does not count. The PROVENANCE sidecar does not ship in the .vsix, '
      + 'so it cannot discharge this obligation, and the full notices file shipping beside NOTICE does not '
      + 'excuse NOTICE from naming what is embedded in the binaries.');
  }
});

// --- 7b-bis. The corrected licence texts have to reach a RECIPIENT, not just the repository -----------
// THE GAP THIS CLOSES, found in review round four. Every fix above lands in THIRD-PARTY-NOTICES.md, and
// THIRD-PARTY-NOTICES.md did not ship. The .vsix carried Semanticus.VSCode/NOTICE alone, which is a
// summary: it names the Microsoft client libraries and reproduces one MIT text, and it carries none of the
// verbatim BSD-3-Clause, Apache-2.0, ISC or 0BSD bodies and neither corrected EULA link. So the whole round
// of corrections was invisible to the only person the notices exist for. The remedy is to package the file,
// which is what these three assertions hold in place: the packager stages it, the payload verifier requires
// it, and NOTICE points at it by name so a reader knows the rest of the terms are there.
check('the notice a .vsix recipient receives carries the full third-party notices', () => {
  const packager = readFileSync(new URL('../scripts/package.mjs', import.meta.url), 'utf8');
  const verifier = readFileSync(new URL('../scripts/verify-vsix.mjs', import.meta.url), 'utf8');

  assert.match(packager, /copyFileSync\(path\.join\(repoRoot, 'THIRD-PARTY-NOTICES\.md'\), stagedNotices\)/u,
    'scripts/package.mjs no longer copies the repository-root THIRD-PARTY-NOTICES.md into the extension '
    + 'folder before vsce runs. vsce packages the extension folder only, so without that copy the shipped '
    + '.vsix carries no verbatim licence texts at all.');
  assert.match(packager, /stageThirdPartyNotices\(\);/u,
    'scripts/package.mjs defines the staging step but never calls it before packaging');
  assert.match(packager, /cleanThirdPartyNotices\(\);/u,
    'scripts/package.mjs never removes the staged copy, so it would be left behind in the working tree');

  assert.ok(verifier.includes("'extension/THIRD-PARTY-NOTICES.md'"),
    'scripts/verify-vsix.mjs does not require extension/THIRD-PARTY-NOTICES.md in the payload. Staging it '
    + 'without requiring it means a packaging change can stop shipping the notices and nothing fails.');

  const vsixNotice = read(VSIX_NOTICE_PATH);
  assert.ok(vsixNotice.includes('THIRD-PARTY-NOTICES.md'),
    `${VSIX_NOTICE_PATH} does not name THIRD-PARTY-NOTICES.md. The full terms ship beside it, and a reader `
    + 'who is not told so has no reason to look for them.');
  for (const family of ['BSD 3-Clause', 'Apache 2.0', 'ISC', 'BSD Zero Clause']) {
    assert.ok(vsixNotice.includes(family),
      `${VSIX_NOTICE_PATH} does not tell the reader that the ${family} text is in the packaged `
      + 'THIRD-PARTY-NOTICES.md. Naming the file without naming what it holds is not a reference.');
  }
});

// --- 7c. Every DIRECTLY REFERENCED package of the packaged projects, not just one named library -------
// THE GAP THIS CLOSES. A packaged-notice check that names one library (YamlDotNet, or any other single
// id) can stay green while ANTLR and the Dax libraries ride the same `dotnet publish` path into the
// .vsix with no equivalent proof. The population is therefore DERIVED: kind=nuget entries in the
// manifest, plus every shipping PackageReference in the projects that actually get packaged. A hand
// list of library names is how the next one is forgotten.
//
// AND HERE IS WHERE THAT POPULATION STOPS, said plainly because the CHANGELOG once called it "every
// shipped third-party library" and it is not. It is the DIRECT PackageReference elements of the packaged
// projects derived below. `dotnet publish --self-contained` puts three further groups of files in the payload that
// nothing here reads: the transitive packages those references resolve to, the .NET 8 runtime pack, and
// anything a transitive pulls in turn. Deriving the resolved publish closure (from the generated
// .deps.json or a `dotnet list package --include-transitive` run) is a real piece of work and an open item
// on the board, deliberately NOT attempted here. Until it lands, this check is a floor and not a ceiling,
// and no comment, notice or changelog entry may describe it as the whole payload.
// AND THE POPULATION IS DERIVED, BECAUSE A HAND LIST OF THREE WAS ONLY CORRECT BY LUCK.
// `scripts/package.mjs` names Semanticus.Engine.csproj (line 25) and runs `dotnet publish` on it (line
// 101), so what ships is that project plus its ProjectReference closure -- whatever the closure is on the
// day of the build. A fourth referenced project would have shipped with no notice check at all, while this
// file, NOTICE and THIRD-PARTY-NOTICES.md all went on saying "the three projects". The list below is now
// walked out of the csprojs, and the two notices are checked against the same walk, so drift fails.
// `PACKAGE_ENTRY_PROJECT` and `SHIPPED_ENGINE_PROJECTS` are defined near the top of this file instead of
// here, because the public-mirror curation check hard-requires the derived closure and runs first. This
// comment stays where the reasoning is.

// The closure of `entry` under ProjectReference, entry first. PURE over a read function so a fixture tree
// with a fourth reference can be walked without inventing files on disk. Fails closed the same way the
// PackageReference parser does: a `<ProjectReference` the parser cannot read is a project that would ship
// unchecked, so the raw count and the parsed count must agree.
function projectReferenceClosure(entry, readProject) {
  const seen = [];
  const queue = [entry];
  while (queue.length > 0) {
    const proj = queue.shift();
    if (seen.includes(proj)) continue;
    seen.push(proj);
    const text = stripXmlComments(readProject(proj));
    const raw = (text.match(/<ProjectReference\b/gu) ?? []).length;
    const includes = [...text.matchAll(/<ProjectReference\b([^>]*?)\s*(?:\/>|>)/gu)]
      .map((m) => parseXmlAttributes(m[1] ?? '').get('Include'))
      .filter(Boolean);
    assert.equal(includes.length, raw,
      `${proj} contains ${raw} <ProjectReference elements but this gate could read an Include from only `
      + `${includes.length}. A project reference it cannot read is a project that ships with no notice `
      + 'check, so this fails closed.');
    // csproj Includes are relative to the REFERENCING project's own directory, and are written with
    // backslashes.
    //
    // AN ESCAPING REFERENCE IS REFUSED, NOT CLAMPED. `parts.pop()` on an empty array returns undefined and
    // changes nothing, so `..\..\shared\Shared.csproj` from a top-level project quietly resolved to the
    // repo-internal `shared/Shared.csproj`. If a file of that name happened to exist here, the walk would
    // read THIS repository's file, declare the closure complete, and every notice check downstream would
    // then be run against a project that is not the one shipping -- a false pass in the direction that
    // matters. This gate cannot audit a project outside the repository, so it says so by name.
    const base = proj.split('/').slice(0, -1);
    for (const inc of includes) {
      // ONLY A SINGLE LITERAL RELATIVE PATH IS ACCEPTED. Everything else below used to be pushed through
      // the segment walk as if it were one, which produced a path that is not the project MSBuild would
      // load -- and the notice checks downstream would then be run against the wrong file, or against
      // nothing, and pass. This gate reads project TEXT and evaluates no MSBuild, so a reference whose
      // target it cannot name literally is refused by name instead of guessed at.
      assert.ok(!inc.includes(';'),
        `${proj} references "${inc}", which is a semicolon-separated LIST. This gate resolves one literal `
        + 'path per reference and will not split a list, because a list member it mis-split is a project '
        + 'that ships with no notice check. Write one ProjectReference per project.');
      assert.ok(!/[$@%]\(/u.test(inc),
        `${proj} references "${inc}", which contains an MSBuild property or item expression. This gate `
        + 'does not evaluate MSBuild, so it cannot know which file that names. Write the path literally.');
      assert.ok(!/^[\\/]/u.test(inc) && !/^[A-Za-z]:/u.test(inc),
        `${proj} references "${inc}", which is a ROOTED path (leading slash or backslash, a drive letter, `
        + 'or a UNC share). This gate audits repository files reached by a relative path, and a rooted path '
        + 'names a location it cannot audit. Vendor the project into this repository, or declare it as '
        + 'third party.');
      const parts = [...base];
      for (const seg of inc.replace(/\\/gu, '/').split('/')) {
        if (seg === '..') {
          assert.ok(parts.length > 0,
            `${proj} references "${inc}", which resolves to a path OUTSIDE the repository root. This gate `
            + 'audits repository files only, and it will not clamp the path back inside: a clamped path can '
            + 'collide with a same-named internal project and false-pass the notice checks against the '
            + 'wrong file. Vendor the project into this repository, or declare it as third party.');
          parts.pop();
        } else if (seg !== '.' && seg !== '') {
          parts.push(seg);
        }
      }
      queue.push(parts.join('/'));
    }
  }
  return seen;
}

// ── FINDING 4 (closing round): REFERENCES THIS GATE CANNOT SEE, REFUSED RATHER THAN MISSED ───────
// Everything above reads the TEXT of the project files. MSBuild does not: it hands every project the
// nearest `Directory.Build.props` / `.targets` automatically, and honours any `<Import>` the project
// declares. A `PackageReference` written in one of those files ships exactly like one written in the
// csproj, and this gate would never see it -- so "the references written in these project files" would
// quietly stop being the population while still being the sentence in the notices.
//
// The honest form of the narrowing is a REFUSAL, not a wider claim. This gate still evaluates no MSBuild.
// It reads the repo-controlled files those projects receive and FAILS by name if any of them declares a
// reference, which is the state under which the narrowed claim stays true. Today none does; the check
// prints what it read so an empty scan cannot pass as a clean one.
//
// The ancestor walk is a SUPERSET of MSBuild's: MSBuild stops at the first `Directory.Build.props` it
// finds walking up, this reads every ancestor's. Reading a file MSBuild would not use can only make the
// gate refuse more, never less, so the over-read is safe in the direction that matters.
const AUTO_IMPORT_NAMES = ['Directory.Build.props', 'Directory.Build.targets', 'Directory.Packages.props'];

// PURE over a read function that returns null for a file that does not exist.
function importedReferenceProblems(projects, readMaybe) {
  const problems = [];
  const imported = [];
  const queue = [];
  const scanned = [];
  const unreadable = [];
  // The two ways a file enters this walk are NOT the same promise. An AUTO-import candidate is a guess:
  // MSBuild supplies a Directory.Build.props only where one exists, so absent is the normal case. An
  // <Import> written in a scanned project is a STATED dependency, so absent means this gate never read a
  // file that ships, and a PackageReference inside it would reach the package with no notice check.
  const missingDeclared = (by, target, resolved) => `${by} declares <Import Project="${target}">, and this `
    + `gate cannot read ${resolved}. A declared import is a stated dependency, not an optional one, so a `
    + 'file generated later or present only in a release build could add a reference this gate never '
    + 'sees. Commit the file, drop the import, or widen the claim in THIRD-PARTY-NOTICES.md, '
    + 'Semanticus.VSCode/NOTICE and the header of this file to match.';
  const enqueue = (p, declared) => {
    if (declared) {
      if (unreadable.includes(p)) { problems.push(missingDeclared(declared.by, declared.target, p)); return; }
      const queued = queue.find((q) => q.file === p);
      if (queued) { queued.declared ??= declared; return; }
      if (!scanned.includes(p)) queue.push({ file: p, declared });
      return;
    }
    if (!scanned.includes(p) && !queue.some((q) => q.file === p)) queue.push({ file: p, declared: null });
  };
  for (const proj of projects) {
    enqueue(proj);
    const dirs = proj.split('/').slice(0, -1);
    for (let i = dirs.length; i >= 0; i -= 1) {
      const prefix = dirs.slice(0, i).join('/');
      for (const name of AUTO_IMPORT_NAMES) enqueue(prefix ? `${prefix}/${name}` : name);
    }
  }
  while (queue.length > 0) {
    const { file, declared } = queue.shift();
    scanned.push(file);
    const text = readMaybe(file);
    if (text === null || text === undefined) {
      unreadable.push(file);
      if (declared) problems.push(missingDeclared(declared.by, declared.target, file));
      continue;
    }
    const body = stripXmlComments(text);
    if (!projects.includes(file)) {
      imported.push(file);
      const kinds = [...new Set([...body.matchAll(
        /<(PackageReference|ProjectReference|GlobalPackageReference)\b/gu,
      )].map((m) => m[1]))];
      if (kinds.length > 0) {
        problems.push(`${file} declares ${kinds.join(' and ')}, and the packaged projects receive that `
          + 'file from MSBuild. This gate reads project TEXT and cannot see an imported reference, so a '
          + 'package declared there would ship with no notice check at all. Move the reference into the '
          + 'project file that needs it, or teach this gate to read imports and widen the claim in '
          + 'THIRD-PARTY-NOTICES.md, Semanticus.VSCode/NOTICE and the header of this file to match.');
      }
    }
    for (const m of body.matchAll(/<Import\b([^>]*?)\s*\/?>/gu)) {
      const target = parseXmlAttributes(m[1] ?? '').get('Project');
      if (!target) {
        problems.push(`${file} declares an <Import> with no Project attribute this gate can read, so it `
          + 'cannot tell what that import brings in. This fails closed.');
        continue;
      }
      if (/[$@%]\(/u.test(target) || /^[\\/]/u.test(target) || /^[A-Za-z]:/u.test(target)
        || target.includes(';')) {
        problems.push(`${file} imports "${target}". This gate evaluates no MSBuild, so it cannot resolve `
          + 'a rooted path, a property expression or a list, and therefore cannot say whether that import '
          + 'declares a reference. Write the import path literally and relative, or widen the claim.');
        continue;
      }
      const parts = file.split('/').slice(0, -1);
      let escaped = false;
      for (const seg of target.replace(/\\/gu, '/').split('/')) {
        if (seg === '..') {
          if (parts.length === 0) { escaped = true; break; }
          parts.pop();
        } else if (seg !== '.' && seg !== '') parts.push(seg);
      }
      if (escaped) {
        problems.push(`${file} imports "${target}", which resolves OUTSIDE the repository root. This gate `
          + 'audits repository files only and will not clamp the path back inside.');
        continue;
      }
      enqueue(parts.join('/'), { by: file, target });
    }
  }
  return { problems, imported };
}

// The short names the two notices have to agree with, and the count word they have to spell.
const shortProjectName = (p) => p.split('/').pop().replace(/\.csproj$/u, '');
const COUNT_WORDS = ['no', 'one', 'two', 'three', 'four', 'five', 'six', 'seven', 'eight'];

// Everything a notice must say about the packaged population, as a list of problems. PURE, so a fourth
// project can be proved to break the notices without editing them.
function populationProblems(docText, docPath, projects, phrases) {
  const problems = [];
  const word = COUNT_WORDS[projects.length] ?? String(projects.length);
  for (const phrase of phrases) {
    const expected = phrase.replace('{n}', word);
    if (!docText.includes(expected)) {
      problems.push(`${docPath} does not say "${expected}". ${projects.length} project(s) are packaged.`);
    }
  }
  for (const p of projects) {
    if (!docText.includes(shortProjectName(p))) {
      problems.push(`${docPath} does not name ${shortProjectName(p)}, which ships in this package.`);
    }
  }
  return problems;
}

// Where each notice states the population. Both are checked with the SAME derived list.
const POPULATION_NOTICES = [
  { path: VSIX_NOTICE_PATH, phrases: ['the {n} projects that ship in this package'] },
  { path: 'THIRD-PARTY-NOTICES.md', phrases: ['the {n} packaged projects'] },
];

// Packages that ship but do not need a reproduced OSS notice in THIRD-PARTY-NOTICES.md. Listed with a
// reason, the same pattern as FIRST_PARTY_EMBEDDED: a new shipping PackageReference has to be classified
// or declared, and silence is not an option.
//
// THE REASON MUST BE A LEGAL GROUND, NOT A PUBLISHER. Nine entries were removed from this map on
// 2026-08-18 because their reasons named who publishes the package instead of why no notice is owed:
// "Microsoft Azure SDK", "Microsoft redistributable client library", "Microsoft shared-framework family"
// (five packages), a reserved NuGet prefix, and one that asserted the wrong licence outright
// (ModelContextProtocol was called MIT while its shipped nuspec declares Apache-2.0). Publisher identity
// is not a licence exemption: MIT requires the permission notice to accompany copies whoever ships them.
// The "shared-framework" reason was also factually false -- `dotnet publish --self-contained` copies
// those five as ordinary payload assemblies and the .NET 8 runtime pack does not supply them, measured in
// a win-x64 publish. All nine are now declared as kind=nuget with reproduced licence text instead.
//
// The two that remain are the only ones with a real ground: they are not open-source at all, so there is
// no OSS notice to reproduce, and their proprietary EULA is linked in its own notices section.
//
// THE GROUND MUST BE PINNED TO A VERSION AND TO THE RIGHT LINK. Round three found this map asserting a
// true-sounding ground on false evidence: both entries pointed at the notices section, and that section
// carried exactly one EULA link, AdomdClient's `linkid=852895`, for both packages. The shipped
// `Microsoft.AnalysisServices` 19.114.0 nuspec declares `<licenseUrl>` `linkid=852989`, a DIFFERENT
// document, so the notice named the wrong terms for one of the two packages it covered. Nothing could
// catch it, because the classification stored no version and the guard accepted any reason over ten
// characters. A licence ground is a statement about a specific version of a specific package, so each
// entry now pins the version it was verified at and the link read from THAT package's own nuspec, and the
// guard fails when the shipping version drifts away from the pin.
const CLASSIFIED_SHIPPING_PACKAGES = new Map([
  ['Microsoft.AnalysisServices', {
    reason: 'Microsoft proprietary EULA, not an OSS licence; no OSS notice exists to reproduce, and this package\'s own EULA is linked in the Microsoft Analysis Services section',
    verifiedAtVersion: '19.114.0',
    eulaUrl: 'https://go.microsoft.com/fwlink/?linkid=852989',
    eulaReadFrom: 'the <licenseUrl> element of microsoft.analysisservices.nuspec inside the cached microsoft.analysisservices 19.114.0 package',
    noticeSection: 'Microsoft Analysis Services client libraries',
  }],
  ['Microsoft.AnalysisServices.AdomdClient', {
    reason: 'Microsoft proprietary EULA, not an OSS licence; no OSS notice exists to reproduce, and this package\'s own EULA is linked in the Microsoft Analysis Services section',
    verifiedAtVersion: '19.114.0',
    eulaUrl: 'https://go.microsoft.com/fwlink/?linkid=852895',
    eulaReadFrom: 'the <licenseUrl> element of microsoft.analysisservices.adomdclient.nuspec inside the cached microsoft.analysisservices.adomdclient 19.114.0 package',
    noticeSection: 'Microsoft Analysis Services client libraries',
  }],
]);

// ── $(Property) RESOLUTION, WHICH REFUSES RATHER THAN GUESSES ─────────────────────────────────────
// The old form took the FIRST `<Name>value</Name>` anywhere in the file and ignored everything around it.
// MSBuild does not work that way, and two ordinary csproj shapes broke it silently:
//   * a property defined twice, once per target framework, recorded only the first value -- so the notice
//     and the EULA drift gate were pinned to a version that may not be the one shipping;
//   * a definition inside `<PropertyGroup Condition="…">` read as unconditioned, which is the same defect
//     wearing a different hat.
// This gate does NOT evaluate MSBuild conditions and is not going to start. So it refuses: more than one
// definition, or any condition on the one definition, is a hard failure naming the property and every
// condition it saw. Fail closed beats a coin toss on file order.
//
// The occurrences are read from inside PropertyGroups and then COUNTED against a raw scan of the whole
// file, the same fail-closed shape the PackageReference parser uses. A definition this reader cannot see is
// a value it would silently ignore.
// A `<Choose>` / `<When Condition="…">` / `<Otherwise>` around a PropertyGroup is a condition on every
// property inside it, and it is spelled nowhere on the PropertyGroup itself. Reading only the
// PropertyGroup's own `Condition=` therefore reported a `<When>`-guarded definition as UNCONDITIONED --
// the round-five defect wearing its third hat, and the worst of the three, because the refusal machinery
// was already in place and simply never fired. The ancestors are found once, as ranges, and each
// definition is attributed to every range containing it.
function chooseRanges(text) {
  const ranges = [];
  const open = [];
  for (const t of text.matchAll(/<(\/?)(Choose|When|Otherwise)\b([^>]*?)(\/?)>/gu)) {
    const [, closing, tag, attrs, selfClosing] = t;
    if (selfClosing) continue;   // an empty element cannot contain a PropertyGroup
    if (closing) {
      for (let i = open.length - 1; i >= 0; i--) {
        if (open[i].tag !== tag) continue;
        ranges.push({ start: open[i].start, end: t.index, label: open[i].label });
        open.splice(i, 1);
        break;
      }
      continue;
    }
    const condition = parseXmlAttributes(attrs ?? '').get('Condition') ?? null;
    open.push({
      tag,
      start: t.index + t[0].length,
      label: condition ? `<${tag} Condition="${condition}">` : `<${tag}>`,
    });
  }
  // An UNCLOSED Choose/When runs to the end of the file. Treating it as absent would be the silent drop
  // this function exists to stop, so it is kept and its range simply extends to the end.
  for (const o of open) ranges.push({ start: o.start, end: text.length, label: `${o.label} (never closed)` });
  return ranges;
}

function csprojPropertyOccurrences(csprojTextRaw, name) {
  const text = stripXmlComments(csprojTextRaw);
  const elem = new RegExp(`<${name}\\b([^>]*)>([\\s\\S]*?)</${name}\\s*>`, 'gu');
  const raw = (text.match(new RegExp(`<${name}\\b[^>]*>`, 'gu')) ?? []).length;
  const chooses = chooseRanges(text);
  const found = [];
  for (const g of text.matchAll(/(<PropertyGroup\b([^>]*)>)([\s\S]*?)(<\/PropertyGroup\s*>)/gu)) {
    const groupStart = g.index + g[1].length;
    const groupCondition = parseXmlAttributes(g[2] ?? '').get('Condition') ?? null;
    const ancestors = chooses
      .filter((r) => groupStart >= r.start && groupStart < r.end)
      .sort((a, b) => a.start - b.start)
      .map((r) => `inside ${r.label}`);
    for (const p of g[3].matchAll(elem)) {
      const own = parseXmlAttributes(p[1] ?? '').get('Condition') ?? null;
      found.push({
        value: p[2].trim(),
        conditions: [
          ...ancestors,
          groupCondition ? `PropertyGroup Condition="${groupCondition}"` : null,
          own ? `Condition="${own}"` : null,
        ].filter(Boolean),
      });
    }
  }
  return { raw, found };
}

function resolveCsprojProperty(csprojText, rawVersion, where = 'a packaged project') {
  const m = /^\$\(([^)]+)\)$/u.exec(String(rawVersion ?? '').trim());
  if (!m) return rawVersion;
  const name = m[1];
  const { raw, found } = csprojPropertyOccurrences(csprojText, name);
  if (found.length === 0 && raw === 0) return rawVersion;
  assert.equal(found.length, raw,
    `${where} defines $(${name}) ${raw} time(s) but this gate could read ${found.length} of them, so at `
    + 'least one definition sits somewhere it does not look (outside a <PropertyGroup>, or in a shape the '
    + 'reader cannot parse). A definition it cannot see is a value it would silently ignore, so this fails '
    + 'closed. Teach csprojPropertyOccurrences the shape; do not reword the csproj to suit it.');
  const conditions = found.flatMap((f) => f.conditions);
  assert.ok(found.length === 1 && conditions.length === 0,
    `${where} resolves a package version from $(${name}), and this gate cannot tell which value ships.\n`
    + `  definitions: ${found.map((f) => `"${f.value}"${f.conditions.length ? ` under ${f.conditions.join(' + ')}` : ' (unconditioned)'}`).join(', ')}\n`
    + '  This gate does not evaluate MSBuild conditions, so a conditioned or repeated property is refused '
    + 'rather than resolved to whichever definition happens to be written first -- which is what it used to '
    + 'do, pinning the notice and the EULA drift check to a version that may not be the one shipping. Pin '
    + `the PackageReference to a literal version, or give $(${name}) one unconditioned definition.`);
  return found[0].value;
}

// ── THE PackageReference PARSER, AND WHY IT COUNTS BEFORE IT PARSES ───────────────────────────────
// The previous form was a single regex requiring `Include="…"` followed IMMEDIATELY by `Version="…"`.
// MSBuild does not require that, so five ordinary spellings were invisible to the gate and the package
// simply did not exist as far as the notices check was concerned: attributes in the other order, a
// `Condition=` sitting between them, single-quoted values, a `<Version>` CHILD element instead of an
// attribute, and attributes split across lines. An unmatched reference did not fail; it vanished, which is
// the worst failure mode a licence gate can have.
//
// FAIL CLOSED, IN TWO STEPS. First count raw `<PackageReference` occurrences with a spelling that cannot
// miss one. Then parse. If the two numbers disagree, the parser met a form it does not understand and the
// gate HARD-FAILS naming the file, instead of silently shipping a package with no notice. A parsed entry
// with no id or no version is the same failure. `parsePackageReferences` is a pure function over text so
// the fixture check below can prove each shape without touching the real projects.
function parseXmlAttributes(attrText) {
  const out = new Map();
  for (const a of attrText.matchAll(/([A-Za-z_][\w.:-]*)\s*=\s*(?:"([^"]*)"|'([^']*)')/gu)) {
    out.set(a[1], a[2] ?? a[3]);
  }
  return out;
}

// XML COMMENTS ARE NOT REFERENCES, AND READING THEM AS REFERENCES IS WORSE THAN MISSING ONE.
// A csproj routinely carries the previous pin commented out above the live one. The parser used to read
// the comment as an active reference, and because the id map below kept the FIRST occurrence, the dead
// version won and the EULA drift gate then checked a version that does not ship. Comments come out before
// anything is counted or parsed, so the raw count and the parsed list describe the same live text.
// A declaration, not a const arrow, so it is hoisted: the packaged-project walk above runs at module load
// and would otherwise hit the temporal dead zone.
function stripXmlComments(text) { return text.replace(/<!--[\s\S]*?-->/gu, ''); }

// A CONDITION ON THE PARENT ItemGroup IS A CONDITION ON EVERY REFERENCE INSIDE IT. Reading only the
// element's own `Condition=` reported `<ItemGroup Condition="'$(TargetFramework)' == 'net8.0'">` as
// unconditioned, so the refusal below would print "(unconditioned)" beside two versions that a reader can
// see are mutually exclusive in the file. The group's ranges are computed once and each reference is
// attributed to the innermost one containing it.
function itemGroupRanges(csprojText) {
  const ranges = [];
  for (const g of csprojText.matchAll(/(<ItemGroup\b([^>]*)>)([\s\S]*?)(<\/ItemGroup\s*>)/gu)) {
    const start = g.index + g[1].length;
    ranges.push({
      start,
      end: start + g[3].length,
      condition: parseXmlAttributes(g[2] ?? '').get('Condition') ?? null,
    });
  }
  return ranges;
}

function parsePackageReferences(csprojTextRaw) {
  const csprojText = stripXmlComments(csprojTextRaw);
  // The count that cannot miss one: the element name and a word boundary, nothing else.
  const raw = (csprojText.match(/<PackageReference\b/gu) ?? []).length;
  const groups = itemGroupRanges(csprojText);
  const parsed = [];
  for (const m of csprojText.matchAll(
    /<PackageReference\b([^>]*?)\s*(?:\/>|>([\s\S]*?)<\/PackageReference\s*>)/gu,
  )) {
    const attrs = parseXmlAttributes(m[1] ?? '');
    const body = m[2] ?? '';
    const childVersion = /<Version>\s*([^<]*?)\s*<\/Version>/u.exec(body);
    const group = groups.filter((g) => m.index >= g.start && m.index < g.end).pop() ?? null;
    parsed.push({
      id: attrs.get('Include') ?? attrs.get('Update') ?? null,
      version: attrs.get('Version') ?? (childVersion ? childVersion[1] : null),
      condition: attrs.get('Condition') ?? null,
      groupCondition: group ? group.condition : null,
      body,
    });
  }
  return { raw, parsed };
}

// ── ASSET SETTINGS DROP NOTHING WITHOUT EVIDENCE ─────────────────────────────────────────────────
// The two spellings that used to remove a reference from the audit population silently. They are still
// recognised -- a reference carrying one of them really might not ship -- but recognising a setting is
// where this gate's knowledge ENDS. Evaluating what `dotnet publish` actually emits is a different program
// and this file is not going to become it, so the answer is refusal plus recorded evidence.
function assetDropsOf(body) {
  const drops = [];
  if (/<PrivateAssets>\s*all\s*<\/PrivateAssets>/iu.test(body)) drops.push('PrivateAssets=all');
  if (/<ExcludeAssets>[^<]*\bruntime\b/iu.test(body)) drops.push('ExcludeAssets=runtime');
  return drops;
}

// The whole decision, PURE over the exemption map, so the refusal can be proved on a fixture instead of on
// whatever the real csprojs happen to say today. `exempt === null` with a non-empty `drops` is the refusal
// case: the reference stays in the audit population and the caller fails naming it.
function assetDropDecision(id, body, exemptions) {
  const drops = assetDropsOf(body);
  return { drops, exempt: drops.length > 0 ? (exemptions.get(id) ?? null) : null };
}

// The evidence kinds this gate accepts for an asset-based exemption. A closed set, so "trust me" cannot be
// spelled as a new kind. Only one kind exists today because only one kind has actually been produced.
const ASSET_EVIDENCE_KINDS = new Set(['measured-publish-payload']);

// The command every entry below records, so "measured" names a thing that was run rather than a mood.
const ASSET_PUBLISH_COMMAND =
  'dotnet publish Semanticus.Engine/Semanticus.Engine.csproj -c Release -o <tmp>';
// Whitespace is not content. Every free-text field below is judged on its NORMALIZED form, because a
// field holding forty spaces satisfied a raw `.length > 40` test and read as measured evidence.
const squashText = (s) => String(s ?? '').replace(/\s+/gu, ' ').trim();
// The evidence has to name the artifact that was looked at. A filename is the cheapest honest proof that
// somebody opened something; prose alone is the claim under test, not evidence for it.
const ASSET_EVIDENCE_ARTIFACT = /[\w.+-]+\.(?:dll|exe|json|nupkg)\b/u;
const ASSET_MIN = { reason: 40, command: 20, evidence: 60 };

// Packages a setting removes from the audit, each with the reason and the evidence that the removal is
// true. Same shape and same discipline as CLASSIFIED_SHIPPING_PACKAGES: the ground is pinned to a VERSION,
// because "it does not ship" is a fact about one build and the next bump would otherwise inherit the claim
// unread. The check below fails on an entry nothing uses, on an unknown evidence kind, and on version
// drift.
//
// THE EVIDENCE, MEASURED 2026-08-18 and not inferred from the settings:
//   dotnet publish Semanticus.Engine/Semanticus.Engine.csproj -c Release -o <tmp>
// produced 94 managed assemblies. None of the four names below is among them, and three of the four do not
// appear in the publish target of Semanticus.Engine.deps.json at all. Antlr4.Runtime, which carries no
// asset setting, DOES appear there with a runtime asset -- so the measurement can tell the two apart and is
// not a scan that finds nothing.
const ASSET_EXEMPT_PACKAGES = new Map([
  ['Antlr4.CodeGenerator', {
    reason: 'a build-time DAX lexer generator: it runs during compilation and emits C# into the project, and '
      + 'no assembly from the package is part of the published output',
    evidenceKind: 'measured-publish-payload',
    command: ASSET_PUBLISH_COMMAND,
    evidence: 'Antlr4.CodeGenerator.dll is absent from the 94 assemblies of a Release publish of '
      + 'Semanticus.Engine.csproj, and the package has no entry in the publish target of '
      + 'Semanticus.Engine.deps.json',
    verifiedAtVersion: '4.6.6',
    verifiedInProject: 'Semanticus.Core/Semanticus.Core.csproj',
    verifiedDrops: ['PrivateAssets=all'],
  }],
  ['System.Net.Http', {
    reason: 'a version constraint only. Antlr4.Runtime 4.6.6 targets netstandard1.3 and drags the .NET '
      + 'Standard 1.6 meta-package graph in behind it; the constraint pushes that graph off the advisory-'
      + 'bearing versions, and the net8.0 runtime supplies the implementation that actually ships',
    evidenceKind: 'measured-publish-payload',
    command: ASSET_PUBLISH_COMMAND,
    evidence: 'System.Net.Http.dll is absent from the 94 assemblies of a Release publish of '
      + 'Semanticus.Engine.csproj, and the package has no entry in the publish target of '
      + 'Semanticus.Engine.deps.json',
    verifiedAtVersion: '4.3.4',
    verifiedInProject: 'Semanticus.Core/Semanticus.Core.csproj',
    verifiedDrops: ['ExcludeAssets=runtime'],
  }],
  ['System.Security.Cryptography.X509Certificates', {
    reason: 'a version constraint only, from the same netstandard1.6 meta-package graph; the net8.0 runtime '
      + 'supplies the implementation that actually ships',
    evidenceKind: 'measured-publish-payload',
    command: ASSET_PUBLISH_COMMAND,
    evidence: 'System.Security.Cryptography.X509Certificates.dll is absent from the 94 assemblies of a '
      + 'Release publish of Semanticus.Engine.csproj, and the package has no entry in the publish target of '
      + 'Semanticus.Engine.deps.json',
    verifiedAtVersion: '4.3.2',
    verifiedInProject: 'Semanticus.Core/Semanticus.Core.csproj',
    verifiedDrops: ['ExcludeAssets=runtime'],
  }],
  ['System.Private.Uri', {
    reason: 'a version constraint only, from the same netstandard1.6 meta-package graph; the net8.0 runtime '
      + 'supplies the implementation that actually ships',
    evidenceKind: 'measured-publish-payload',
    command: ASSET_PUBLISH_COMMAND,
    evidence: 'System.Private.Uri.dll is absent from the 94 assemblies of a Release publish of '
      + 'Semanticus.Engine.csproj, and the package has no entry in the publish target of '
      + 'Semanticus.Engine.deps.json',
    verifiedAtVersion: '4.3.2',
    verifiedInProject: 'Semanticus.Core/Semanticus.Core.csproj',
    verifiedDrops: ['ExcludeAssets=runtime'],
  }],
]);

// What the population scan actually dropped, so the check below can prove every exemption is still USED.
// An exemption nobody needs is a standing permission to stop auditing a package.
const assetExemptionSightings = [];

// AN EXEMPTION EXCUSES ONE OCCURRENCE, NOT A PACKAGE ID. It was verified against a package id at a
// version, in a named project, carrying a named set of asset settings. Change any of those four and the
// measurement no longer describes what is in the tree, so the exemption stops applying until somebody
// re-measures. The old check keyed the sightings by id into a Map, which kept the LAST sighting and
// validated only that one: a second reference to the same id at another version, in another project, was
// never compared to anything. EVERY sighting is compared here.
//
// PURE over the entry and the sightings, so each refusal is proved on a fixture rather than on whatever
// the real csprojs happen to say today.
function assetExemptionProblems(id, x, sightings) {
  const problems = [];
  const reason = squashText(x.reason);
  const command = squashText(x.command);
  const evidence = squashText(x.evidence);
  if (reason.length < ASSET_MIN.reason) {
    problems.push(`${id}: the reason is ${reason.length} characters once whitespace is normalized, and `
      + `${ASSET_MIN.reason} is the floor. Name why nothing from the package reaches the payload.`);
  }
  if (!ASSET_EVIDENCE_KINDS.has(x.evidenceKind)) {
    problems.push(`${id}: records evidenceKind "${x.evidenceKind}", which is not one this gate accepts `
      + `(${[...ASSET_EVIDENCE_KINDS].join(', ')}). A new kind of evidence is a decision, not a new string.`);
  }
  if (command.length < ASSET_MIN.command) {
    problems.push(`${id}: records no command. "Measured" has to name something that was RUN, or the next `
      + 'reader cannot repeat it and the claim is untestable.');
  }
  if (evidence.length < ASSET_MIN.evidence) {
    problems.push(`${id}: the evidence is ${evidence.length} characters once whitespace is normalized, and `
      + `${ASSET_MIN.evidence} is the floor. Say what was measured and what it showed; "the setting says `
      + 'so" is the claim under test, not evidence for it.');
  } else if (!ASSET_EVIDENCE_ARTIFACT.test(evidence)) {
    problems.push(`${id}: the evidence names no artifact that was checked. Name the file that was looked `
      + 'at (an assembly, a deps.json), not just the conclusion drawn from looking.');
  }
  if (!NUGET_VERSION.test(String(x.verifiedAtVersion ?? ''))) {
    problems.push(`${id}: has no pinned verifiedAtVersion. "It does not ship" is a fact about one build.`);
  }
  const mine = sightings.filter((s) => s.id === id);
  if (mine.length === 0) {
    problems.push(`${id}: is exempted from the audit by an asset setting, but no packaged project drops it `
      + 'any more. Remove the exemption: an entry nobody needs is a standing permission to stop auditing.');
  }
  const wantDrops = [...(x.verifiedDrops ?? [])].join(' and ');
  for (const s of mine) {
    if (s.version !== x.verifiedAtVersion) {
      problems.push(`${id}: is pinned at ${s.version} in ${s.project} but the exemption was measured at `
        + `${x.verifiedAtVersion}. Re-measure the publish payload at the new version before moving the `
        + 'pin; inheriting an evidence claim across a bump is how an unaudited package starts shipping.');
    }
    if (s.project !== x.verifiedInProject) {
      problems.push(`${id}: is dropped by ${s.project}, but the exemption was measured against `
        + `${x.verifiedInProject}. A second project referencing the same id is a second occurrence, and `
        + 'this evidence was never taken about it. Measure it, or reference the package once.');
    }
    if (s.drops.join(' and ') !== wantDrops) {
      problems.push(`${id}: now carries ${s.drops.join(' and ')} in ${s.project}, but the exemption was `
        + `measured with ${wantDrops || 'no asset setting at all'}. A different setting is a different `
        + 'restore instruction and a different payload question, so the old measurement does not answer it.');
    }
  }
  return problems;
}

// An EXACT-VERSION RANGE is a pin, and a tighter one than a bare version. `Version="[18.1.0]"` restores
// 18.1.0 and nothing else, where `Version="18.1.0"` is a floor NuGet may resolve upwards. The gate read
// the raw attribute, so the tighter spelling compared unequal to the `18.1.0` a manifest records and the
// notice reads, and the only ways out were to loosen the pin or to write a range into the manifest where
// a version belongs. Both are worse than teaching the gate the spelling. ONLY the single-version form is
// read: a real range like `[1.0,2.0)` or `(1.0,]` has no one version to name, so it falls through
// unchanged and still fails the comparison loudly, which is the correct answer for a package whose
// shipping version nobody can state.
const EXACT_VERSION_RANGE = /^\[\s*([^,[\]()\s]+)\s*\]$/u;
const readExactPin = (v) => EXACT_VERSION_RANGE.exec(v)?.[1] ?? v;

function shippingPackageReferences() {
  const out = new Map();
  const occurrences = [];
  assetExemptionSightings.length = 0;
  for (const proj of SHIPPED_ENGINE_PROJECTS) {
    const text = stripXmlComments(read(proj));
    const { raw, parsed } = parsePackageReferences(text);
    assert.equal(parsed.length, raw,
      `${proj} contains ${raw} <PackageReference elements but this gate could only parse ${parsed.length}. `
      + 'A reference the parser cannot read is a package that ships with NO notice check at all, so this '
      + 'fails closed rather than passing on the ones it happened to understand. Fix parsePackageReferences, '
      + 'do not reword the csproj to suit it.');
    for (const ref of parsed) {
      assert.ok(ref.id,
        `${proj} has a <PackageReference> with no Include/Update attribute this gate can read. `
        + 'A package with no id cannot be checked against the manifest, so this fails closed.');
      assert.ok(ref.version,
        `${proj} declares PackageReference "${ref.id}" with no Version attribute and no <Version> child `
        + 'element. An unpinned package cannot have its notice pinned to a version, so this fails closed. '
        + 'If the version comes from central package management, teach this parser to read it.');
      // A Condition= does NOT exempt a reference. It may ship in some configuration, and "sometimes
      // shipped" is shipped for licence purposes, so conditioned references stay in the population.
      //
      // NEITHER DO PrivateAssets OR ExcludeAssets, ON THEIR OWN. Those two settings used to `continue`
      // straight past the audit, which reads as "this cannot reach the payload" -- and they do not say
      // that. `PrivateAssets=all` controls what FLOWS TO CONSUMERS of this project, not what lands in this
      // project's own publish output, and the one reference carrying it here also sets
      // `IncludeAssets` with `runtime` in the list. `ExcludeAssets=runtime` is closer to the claim but is
      // still a restore-time instruction whose effect on the published payload nobody had measured. A
      // setting is not evidence. So the drop is refused unless the package is named below with a reason
      // and an evidence kind, and an unnamed one lands in the audit population like everything else.
      const { drops, exempt } = assetDropDecision(ref.id, ref.body, ASSET_EXEMPT_PACKAGES);
      if (drops.length > 0) {
        assert.ok(exempt,
          `${proj} sets ${drops.join(' and ')} on PackageReference "${ref.id}", and this gate will not read `
          + 'that as proof the package stays out of the published payload. Those settings govern asset flow '
          + 'at restore, not the contents of `dotnet publish`. Either declare the package in the manifest '
          + 'with its notice like every other shipping reference, or add it to ASSET_EXEMPT_PACKAGES with a '
          + 'reason, an evidence kind and the evidence itself. Silence is not an option here: a package '
          + 'dropped by a setting is a package shipping with no notice check at all.');
        assetExemptionSightings.push({
          id: ref.id, project: proj, drops, version: readExactPin(resolveCsprojProperty(text, ref.version, proj)),
        });
        continue;
      }
      // The Condition travels WITH the occurrence. It used to be parsed and then dropped here, so a
      // legitimate pair of mutually exclusive references pinning different versions per target framework
      // would be refused below with a message that named only two versions and gave the reader nothing to
      // tell a real conflict from a correct one. Carried through, the refusal can at least name them.
      occurrences.push({
        id: ref.id,
        version: readExactPin(resolveCsprojProperty(text, ref.version, proj)),
        project: proj,
        condition: ref.condition,
        groupCondition: ref.groupCondition,
      });
    }
  }
  const { packages, disagreements } = reconcileShippingReferences(occurrences);
  assert.deepEqual(disagreements, [],
    'the packaged engine projects pin the SAME package id at DIFFERENT versions:\n  '
    + disagreements.map((d) => `${d.id}: ${d.pins.map(describePin).join(', ')}`).join('\n  ')
    + '\nOnly one of them can be the version that ships beside the other, and the notice, the manifest and '
    + 'the EULA drift gate would all be checked against whichever project this scan happened to read first. '
    + 'Pin the id to one version across every packaged project, then move the notice.\n'
    + 'IF THE CONDITIONS ABOVE ARE MUTUALLY EXCLUSIVE this is a legitimate pin and the refusal is this '
    + 'gate\'s limit, not your bug: it reconciles across the whole file and does not evaluate MSBuild '
    + 'conditions, so it cannot tell "two versions, one ships" from "two versions, both ship". Reconciling '
    + 'per evaluated publish target is open work; until it lands, split the notice per condition and record '
    + 'the pair here deliberately rather than deleting a condition to quiet the gate.');
  for (const p of packages) out.set(p.id, p);
  return out;
}

// PURE, so the disagreement can be proved on a fixture rather than on whatever the real csprojs happen to
// say today. THE BUG THIS CLOSES: the scan used to keep the first occurrence of an id and drop the rest,
// so bumping Microsoft.AnalysisServices in Semanticus.Engine while Semanticus.Core still pinned the old
// version left the OLD pin winning, silently, and the drift gate then compared the notice to a version
// that is not the one shipping. Disagreement is now a hard failure, not a coin toss on scan order.
// One pin, rendered so a reader can act on it. The Condition is the whole point: without it the message
// says "1.0 in A.csproj, 2.0 in A.csproj" and reads as a contradiction even when the csproj is correct.
const describePin = (p) => `${p.version} in ${p.project}`
  + (p.condition ? ` when Condition="${p.condition}"` : '')
  + (p.groupCondition ? ` inside an ItemGroup with Condition="${p.groupCondition}"` : '')
  + (p.condition || p.groupCondition ? '' : ' (unconditioned)');

function reconcileShippingReferences(occurrences) {
  const byId = new Map();
  for (const o of occurrences) {
    if (!byId.has(o.id)) byId.set(o.id, []);
    byId.get(o.id).push(o);
  }
  const packages = [];
  const disagreements = [];
  for (const [id, pins] of byId) {
    const versions = [...new Set(pins.map((p) => p.version))];
    if (versions.length > 1) disagreements.push({ id, pins });
    else packages.push({ id, version: versions[0], project: pins[0].project });
  }
  return { packages, disagreements: disagreements.sort((a, b) => a.id.localeCompare(b.id)) };
}

// Every attribute shape MSBuild accepts, proved one at a time. Each fixture states what the OLD
// Include-then-Version regex did with it, because "the parser handles it" is a claim until the shape that
// used to disappear is named. The last fixture is the fail-closed control: a form nothing can parse must
// make the counts disagree, not vanish quietly.
const PACKAGE_REFERENCE_FIXTURES = [
  {
    what: 'Include then Version, self-closing (the only shape the old regex read)',
    xml: '<PackageReference Include="A.Pkg" Version="1.2.3" />',
    id: 'A.Pkg', version: '1.2.3',
  },
  {
    what: 'Version before Include (reversed attribute order)',
    xml: '<PackageReference Version="1.2.3" Include="A.Pkg" />',
    id: 'A.Pkg', version: '1.2.3',
  },
  {
    what: 'a Condition sitting between Include and Version',
    xml: '<PackageReference Include="A.Pkg" Condition="\'$(OS)\' == \'Windows_NT\'" Version="1.2.3" />',
    id: 'A.Pkg', version: '1.2.3', condition: "'$(OS)' == 'Windows_NT'",
  },
  {
    what: 'single-quoted attribute values',
    xml: "<PackageReference Include='A.Pkg' Version='1.2.3' />",
    id: 'A.Pkg', version: '1.2.3',
  },
  {
    what: 'a <Version> CHILD element instead of an attribute',
    xml: '<PackageReference Include="A.Pkg">\n  <Version>1.2.3</Version>\n</PackageReference>',
    id: 'A.Pkg', version: '1.2.3',
  },
  {
    // Measured A/B against the old regex: this one it DID read, because `\s+` spans newlines. Kept as a
    // regression guard, not counted among the shapes that were invisible. Five of the eight below were.
    what: 'attributes split across lines',
    xml: '<PackageReference\n    Include="A.Pkg"\n    Version="1.2.3" />',
    id: 'A.Pkg', version: '1.2.3',
  },
  {
    what: 'a paired element carrying PrivateAssets, with the attributes reversed',
    xml: '<PackageReference Version="1.2.3" Include="A.Pkg">\n  <PrivateAssets>all</PrivateAssets>\n</PackageReference>',
    id: 'A.Pkg', version: '1.2.3', bodyIncludes: 'PrivateAssets',
  },
  {
    what: 'a form no regex here can read: the count must disagree, not the reference disappear',
    xml: '<PackageReference Include="Has>Angle" Version="1.2.3" />',
    unparseable: true,
  },
];

// A commented-out reference is not a reference. Each fixture is the shape a csproj actually takes when a
// pin is bumped and the old line is left behind for the diff to show.
const COMMENTED_REFERENCE_FIXTURES = [
  {
    what: 'a single commented-out reference and nothing else',
    xml: '<!-- <PackageReference Include="A.Pkg" Version="0.0.1" /> -->',
    expect: [],
  },
  {
    what: 'the old pin commented out directly above the live one',
    xml: '<!-- was: <PackageReference Include="A.Pkg" Version="0.0.1" /> -->\n'
      + '<PackageReference Include="A.Pkg" Version="1.2.3" />',
    expect: [{ id: 'A.Pkg', version: '1.2.3' }],
  },
  {
    what: 'a multi-line comment holding a paired element',
    xml: '<!--\n  <PackageReference Include="A.Pkg">\n    <Version>0.0.1</Version>\n  </PackageReference>\n-->\n'
      + '<PackageReference Include="B.Pkg" Version="2.0.0" />',
    expect: [{ id: 'B.Pkg', version: '2.0.0' }],
  },
];

check('a commented-out PackageReference is not counted and not parsed', () => {
  for (const f of COMMENTED_REFERENCE_FIXTURES) {
    const wrapped = `<Project><ItemGroup>\n${f.xml}\n</ItemGroup></Project>`;

    // RED-FIRST, IN THE SAME BREATH. Without the comment strip the raw counter sees every commented
    // element, so this states what the broken parser did before asserting what the fixed one does.
    const rawWithComments = (wrapped.match(/<PackageReference\b/gu) ?? []).length;
    assert.ok(rawWithComments > f.expect.length,
      `fixture "${f.what}" carries no commented reference, so it cannot prove the strip does anything`);

    const { raw, parsed } = parsePackageReferences(wrapped);
    assert.equal(raw, f.expect.length, `fixture "${f.what}" counted a commented reference as live`);
    assert.equal(parsed.length, f.expect.length, `fixture "${f.what}" parsed a commented reference as live`);
    assert.deepEqual(parsed.map((p) => ({ id: p.id, version: p.version })), f.expect,
      `fixture "${f.what}" parsed the wrong live references`);
  }
});

check('the same package id pinned at two versions across shipped projects fails loudly', () => {
  const agree = reconcileShippingReferences([
    { id: 'A.Pkg', version: '1.2.3', project: 'One.csproj' },
    { id: 'A.Pkg', version: '1.2.3', project: 'Two.csproj' },
    { id: 'B.Pkg', version: '9.0.0', project: 'Two.csproj' },
  ]);
  assert.deepEqual(agree.disagreements, [], 'identical pins across projects must not be reported as drift');
  assert.deepEqual(agree.packages.map((p) => `${p.id}@${p.version}`).sort(), ['A.Pkg@1.2.3', 'B.Pkg@9.0.0']);

  // THE FIXTURE THE OLD CODE PASSED. First-occurrence-wins returned 19.87.5 here and said nothing, so the
  // EULA drift gate compared the notice to the version that does NOT ship beside the other.
  const drift = reconcileShippingReferences([
    { id: 'Microsoft.AnalysisServices', version: '19.87.5', project: 'Semanticus.Core.csproj' },
    { id: 'Microsoft.AnalysisServices', version: '20.0.0', project: 'Semanticus.Engine.csproj' },
  ]);
  assert.deepEqual(drift.disagreements.map((d) => d.id), ['Microsoft.AnalysisServices']);
  assert.deepEqual(drift.packages, [],
    'a package whose version is disputed must not be handed on as if one of the two pins were the answer');

  // THE LEGITIMATE CASE THIS GATE STILL REFUSES, and refuses DIAGNOSABLY. Two mutually exclusive
  // conditions may correctly select different versions; this reconciler works over the whole file and never
  // evaluates MSBuild conditions, so it cannot tell that pair from a real conflict. It keeps refusing --
  // fail-closed is right when the alternative is guessing which version ships -- but the pins now carry the
  // conditions, so the message names them and a reader can see WHY they differ. The old code parsed the
  // Condition and dropped it one line later, and this fixture is what proves it no longer does: strip the
  // condition off the occurrence and the two assertions below fail.
  const conditioned = reconcileShippingReferences([
    { id: 'C.Pkg', version: '1.0.0', project: 'One.csproj', condition: "'$(TargetFramework)' == 'net8.0'" },
    { id: 'C.Pkg', version: '2.0.0', project: 'One.csproj', condition: "'$(TargetFramework)' == 'net9.0'" },
  ]);
  assert.deepEqual(conditioned.disagreements.map((d) => d.id), ['C.Pkg'],
    'a conditioned version split must still be refused: this gate cannot prove the conditions exclude '
    + 'each other, and passing it would let two versions ship with one notice.');
  const rendered = conditioned.disagreements[0].pins.map(describePin);
  assert.deepEqual(rendered, [
    "1.0.0 in One.csproj when Condition=\"'$(TargetFramework)' == 'net8.0'\"",
    "2.0.0 in One.csproj when Condition=\"'$(TargetFramework)' == 'net9.0'\"",
  ], 'the refusal must name each pin\'s Condition. Without it the message reads "1.0.0 in One.csproj, '
    + '2.0.0 in One.csproj", which looks like a contradiction in one file and is not diagnosable.');
  assert.deepEqual(
    reconcileShippingReferences([
      { id: 'D.Pkg', version: '1.0.0', project: 'One.csproj' },
      { id: 'D.Pkg', version: '2.0.0', project: 'Two.csproj' },
    ]).disagreements[0].pins.map(describePin),
    ['1.0.0 in One.csproj (unconditioned)', '2.0.0 in Two.csproj (unconditioned)'],
    'an unconditioned pin must say so, or a reader cannot tell "no condition" from "condition not shown"');

  // NON-VACUITY on the real tree, stated rather than assumed: measured 2026-08-18, NONE of the packaged
  // csprojs carries a Condition on any PackageReference, so the branch above is exercised by these
  // fixtures alone today. If one ever does, this assertion is where you will be told to re-read the above.
  for (const proj of SHIPPED_ENGINE_PROJECTS) {
    const withCondition = parsePackageReferences(read(proj)).parsed
      .filter((r) => r.condition || r.groupCondition);
    assert.deepEqual(withCondition.map((r) => r.id), [],
      `${proj} now has conditioned PackageReference(s), on the element or on its parent ItemGroup. That is `
      + 'legal and this gate does not evaluate conditions: it reconciles versions across the whole file. '
      + 'Check that no two of them select different versions of one id, then update this assertion to list '
      + 'the ones you accepted.');
  }
});

// ── The exact-version pattern, proved in BOTH directions ─────────────────────────────────────────
check('the NuGet exact-version pattern accepts every exact spelling and refuses ranges and wildcards', () => {
  // RED, both of these were refused by the old three-part-only pattern, so a real pin could not be
  // recorded at all and the honest way out was to leave it unpinned.
  for (const v of ['1.2.3.4', '19.114.0.0', '1.2.3-preview-1', '1.2.3-rc.1-final', '4.3.2']) {
    assert.match(v, NUGET_VERSION, `${v} is an exact NuGet version and must be accepted as a pin`);
  }
  // The shapes that were already accepted stay accepted.
  for (const v of ['1.0', '13.0.3', '1.4.1', '1.2.3-beta', '1.2.3+build.5', '1.2.3-rc.1+sha.abc']) {
    assert.match(v, NUGET_VERSION, `${v} is an exact NuGet version and must be accepted as a pin`);
  }
  // And the refusals, which are the whole reason this is not just "any version-ish string". Each of these
  // names a SET of versions and lets restore pick, so nothing true can be said about "the version that
  // ships".
  // RED: an EMPTY dot-separated identifier in either tag. The first three were ACCEPTED before
  // 2026-08-18, so the "exact grammar" claimed beside the pattern was wider than the pattern itself. The
  // last two were already refused and are kept so a future loosening of the leading character is caught.
  for (const v of ['1.2.3-a..b', '1.2.3-a.', '1.2.3+build..5', '1.2.3+.5', '1.2.3-.a']) {
    assert.doesNotMatch(v, NUGET_VERSION,
      `"${v}" carries an empty identifier, which is not a version NuGet publishes, so it must not pin`);
  }
  for (const v of ['1.*', '1.2.*', '1.2.3-*', '[1.0,2.0)', '(,3.0]', '[1.0]', '1.2.3.4.5', '1',
    '1.2.3-', '1.2.3+', 'latest', '', ' 1.2.3']) {
    assert.doesNotMatch(v, NUGET_VERSION,
      `"${v}" does not name one shipped version, so it must not be usable as a pin`);
  }

  // `[1.0]` above is refused as a MANIFEST value, and stays refused: the manifest records the version a
  // reader sees in the notice, not the syntax the csproj happens to pin it with. It is the one entry in
  // that list which does name a single version, so on the CSPROJ side it is read rather than refused --
  // otherwise the tightest available pin would be the one spelling this gate could not check.
  assert.equal(readExactPin('[18.1.0]'), '18.1.0', 'an exact-version range names exactly one version');
  assert.equal(readExactPin('[1.2.3-rc.1]'), '1.2.3-rc.1', 'a prerelease exact-version range is still exact');
  assert.equal(readExactPin('18.1.0'), '18.1.0', 'a bare version passes through untouched');
  // FAIL-CLOSED. A real range has no one version to name, so it must survive unchanged and go on to fail
  // the comparison against the manifest, loudly. Returning a bound here would let a package whose shipping
  // version nobody can state pass as though someone had stated it.
  for (const v of ['[1.0,2.0)', '(,3.0]', '[1.0,]', '[1.0, 2.0]', '1.2.*', '[]', '[ ]']) {
    assert.equal(readExactPin(v), v, `"${v}" does not name one version, so it must not be read as a pin`);
  }
});

// ── The packaged-project population, derived and agreed with by both notices ──────────────────────
check('the packaged project population is derived from the project file package.mjs publishes', () => {
  // The premise first: if package.mjs stops publishing this project, the walk below describes nothing.
  const packager = read('Semanticus.VSCode/scripts/package.mjs');
  assert.match(packager, /Semanticus\.Engine\.csproj/u,
    'scripts/package.mjs no longer names Semanticus.Engine.csproj, so PACKAGE_ENTRY_PROJECT is stale and '
    + 'this whole population is derived from the wrong root.');
  assert.match(packager, /'dotnet',\s*\['publish',\s*engineCsproj/u,
    'scripts/package.mjs no longer publishes engineCsproj, so the ProjectReference closure is not what '
    + 'lands in the .vsix. Re-derive the population from whatever it publishes now.');

  assert.ok(SHIPPED_ENGINE_PROJECTS.includes(PACKAGE_ENTRY_PROJECT),
    'the entry project must be in its own closure');
  for (const p of SHIPPED_ENGINE_PROJECTS) {
    assert.ok(existsSync(resolve(repoRoot, p)), `derived packaged project ${p} does not exist`);
  }
  // Stated, not asserted as a constant: this is what the walk finds today. It is printed so a change in
  // the population is visible in the run log and not only in a failure.
  console.log(`         packaged projects (derived): ${SHIPPED_ENGINE_PROJECTS.join(', ')}`);
});

check('both notices state the packaged population that the walk derives', () => {
  for (const n of POPULATION_NOTICES) {
    const problems = populationProblems(read(n.path), n.path, SHIPPED_ENGINE_PROJECTS, n.phrases);
    assert.deepEqual(problems, [],
      'a notice and the shipped payload disagree about what is in the package:\n  ' + problems.join('\n  '));
  }
});

check('RED: a fourth referenced project fails the gate and both notices until it is declared', () => {
  // A fixture tree, not the real one: E references A and B, and A references C. Four projects ship.
  const FIXTURE = new Map([
    ['E/E.csproj', '<Project><ItemGroup>'
      + '<ProjectReference Include="..\\A\\A.csproj" />'
      + '<ProjectReference Include="..\\B\\B.csproj" />'
      + '</ItemGroup></Project>'],
    ['A/A.csproj', '<Project><ItemGroup><ProjectReference Include="..\\C\\C.csproj" /></ItemGroup></Project>'],
    ['B/B.csproj', '<Project><ItemGroup><ProjectReference Include="..\\C\\C.csproj" /></ItemGroup></Project>'],
    ['C/C.csproj', '<Project />'],
  ]);
  const closure = projectReferenceClosure('E/E.csproj', (p) => FIXTURE.get(p));
  assert.deepEqual(closure, ['E/E.csproj', 'A/A.csproj', 'B/B.csproj', 'C/C.csproj'],
    'the walk must be recursive and must visit a diamond-shared project once');

  // The hand list this replaced was three names, so a fourth arriving is exactly the drift that was
  // invisible. Against the notices as they stand today, a four-project population must FAIL.
  for (const n of POPULATION_NOTICES) {
    const problems = populationProblems(read(n.path), n.path, closure, n.phrases);
    assert.ok(problems.length > 0,
      `${n.path} accepted a four-project population unchanged, so this gate would not have caught a fourth `
      + 'referenced project shipping with no notice.');
  }

  // And the fail-closed count guard, on a reference no Include can be read from.
  assert.throws(
    () => projectReferenceClosure('X/X.csproj', () => '<Project><ItemGroup><ProjectReference /></ItemGroup></Project>'),
    /could read an Include from only 0/u,
    'a ProjectReference with no Include must fail the gate, not silently shrink the population');

  // RED: a reference that climbs OUT of the repository. The walker used to clamp it, because `pop()` on an
  // empty array is a no-op, so `..\..\shared\Shared.csproj` from `E/E.csproj` became `shared/Shared.csproj`
  // -- a repo-internal path. The fixture below serves a DIFFERENT file at that internal path, which is the
  // false pass in full: the gate would have audited this repository's Shared.csproj and reported the
  // closure complete while the project that actually ships was never read.
  const clamped = (referenced) => {
    const parts = ['E'];   // the old body, verbatim
    for (const seg of referenced.replace(/\\/gu, '/').split('/')) {
      if (seg === '..') parts.pop();
      else if (seg !== '.' && seg !== '') parts.push(seg);
    }
    return parts.join('/');
  };
  assert.equal(clamped('..\\..\\shared\\Shared.csproj'), 'shared/Shared.csproj',
    'this fixture no longer reproduces the clamp, so the refusal below proves nothing');

  const ESCAPING = new Map([
    ['E/E.csproj', '<Project><ItemGroup>'
      + '<ProjectReference Include="..\\..\\shared\\Shared.csproj" />'
      + '</ItemGroup></Project>'],
    ['shared/Shared.csproj', '<Project><!-- a DIFFERENT, same-named project inside this repo --></Project>'],
  ]);
  assert.throws(() => projectReferenceClosure('E/E.csproj', (p) => ESCAPING.get(p) ?? '<Project />'),
    (e) => {
      assert.match(e.message, /OUTSIDE the repository root/u, 'the refusal must say what it refused');
      assert.match(e.message, /E\/E\.csproj/u, 'the refusal must name the referencing project');
      assert.match(e.message, /\.\.\\\.\.\\shared\\Shared\.csproj/u,
        'the refusal must name the reference exactly as the csproj spells it');
      return true;
    });

  // A reference that goes UP and back DOWN inside the repository is ordinary and still resolves.
  assert.deepEqual(
    projectReferenceClosure('src/E/E.csproj', (p) => (p === 'src/E/E.csproj'
      ? '<Project><ItemGroup><ProjectReference Include="..\\..\\lib\\L\\L.csproj" /></ItemGroup></Project>'
      : '<Project />')),
    ['src/E/E.csproj', 'lib/L/L.csproj'],
    'climbing out of two directories and back down inside the repo is legal and must still resolve');
});

// ── FINDING 4 (closing round): an imported reference is refused, because it cannot be seen ───────
check('no MSBuild file the packaged projects import declares a reference this gate cannot read', () => {
  const readMaybe = (p) => (existsSync(resolve(repoRoot, p)) ? read(p) : null);
  const { problems, imported } = importedReferenceProblems(SHIPPED_ENGINE_PROJECTS, readMaybe);
  assert.deepEqual(problems, [],
    'the packaged projects receive MSBuild files this gate cannot read references out of:\n  '
    + problems.join('\n  '));
  // NON-VACUITY. If nothing was read, the pass above says nothing at all.
  assert.ok(imported.length > 0,
    'no repo-controlled MSBuild import file was found for the packaged projects, so this check proves '
    + 'nothing. Directory.Build.props sits at the repository root; if it is gone, find out why before '
    + 'deleting this check.');
  console.log(`         repo-controlled imports read: ${imported.join(', ')}`);

  // RED: the same walk over a fixture tree whose Directory.Build.props carries a PackageReference. This is
  // the committed twin of the scratch edit that proved it on the real file.
  const FIXTURE = new Map([
    ['A/A.csproj', '<Project><ItemGroup><PackageReference Include="Ok.Pkg" Version="1.0.0" /></ItemGroup></Project>'],
    ['Directory.Build.props', '<Project><ItemGroup>'
      + '<PackageReference Include="Sneaky.Pkg" Version="1.0.0" /></ItemGroup></Project>'],
  ]);
  const red = importedReferenceProblems(['A/A.csproj'], (p) => FIXTURE.get(p) ?? null);
  assert.equal(red.problems.length, 1, 'exactly the imported file must be refused, not the csproj');
  assert.match(red.problems[0], /^Directory\.Build\.props declares PackageReference/u,
    'the refusal must name the file that carries the reference');

  // A commented-out reference in an import is not a reference, the same way it is not one in a csproj.
  const COMMENTED = new Map([
    ['A/A.csproj', '<Project />'],
    ['Directory.Build.props', '<Project><!-- <PackageReference Include="Dead.Pkg" /> --></Project>'],
  ]);
  assert.deepEqual(importedReferenceProblems(['A/A.csproj'], (p) => COMMENTED.get(p) ?? null).problems, []);

  // RED: an import this gate cannot resolve is refused rather than skipped, because "I could not read it"
  // and "it declares nothing" are different answers and only one of them is safe.
  const UNRESOLVABLE = new Map([
    ['A/A.csproj', '<Project><Import Project="$(SharedDir)\\Shared.props" /></Project>'],
  ]);
  const unres = importedReferenceProblems(['A/A.csproj'], (p) => UNRESOLVABLE.get(p) ?? null);
  assert.equal(unres.problems.length, 1);
  assert.match(unres.problems[0], /evaluates no MSBuild/u,
    'an unresolvable import must be refused by name');

  // RED: an import whose path IS literal and relative but whose target cannot be read. "Generated later"
  // and "release-only" are the usual reasons, and both mean the same thing to this gate: a stated
  // dependency it never scanned, which may carry a PackageReference. Absent is fine for an AUTO-import
  // candidate, which MSBuild supplies only if it happens to exist; it is not fine for a declared one.
  const MISSING_DECLARED = new Map([
    ['A/A.csproj', '<Project><Import Project="generated/extra.props" /></Project>'],
  ]);
  const missing = importedReferenceProblems(['A/A.csproj'], (p) => MISSING_DECLARED.get(p) ?? null);
  assert.equal(missing.problems.length, 1,
    'an explicitly declared <Import> whose file cannot be read must be refused, not skipped');
  assert.match(missing.problems[0], /^A\/A\.csproj declares <Import Project="generated\/extra\.props">/u,
    'the refusal must name the project and the path it declared');
  assert.match(missing.problems[0], /A\/generated\/extra\.props/u,
    'the refusal must also name the resolved path that could not be read');

  // GREEN: the auto-import candidates stay optional. Absent Directory.Build.props files are the normal
  // case for every ancestor directory that has none, and refusing them would refuse every repo.
  assert.deepEqual(
    importedReferenceProblems(['A/B/A.csproj'], (p) => (p === 'A/B/A.csproj' ? '<Project />' : null)).problems,
    [],
    'a missing Directory.Build.props is MSBuild-normal and must never be a problem');

  // GREEN: an import that IS resolvable is followed, and a reference inside it is caught two hops down.
  const CHAINED = new Map([
    ['A/A.csproj', '<Project><Import Project="..\\build\\Common.props" /></Project>'],
    ['build/Common.props', '<Project><Import Project="Deep.props" /></Project>'],
    ['build/Deep.props', '<Project><ItemGroup><ProjectReference Include="..\\Z\\Z.csproj" /></ItemGroup></Project>'],
  ]);
  const chained = importedReferenceProblems(['A/A.csproj'], (p) => CHAINED.get(p) ?? null);
  assert.match(chained.problems.join('\n'), /^build\/Deep\.props declares ProjectReference/mu,
    'imports must be followed transitively, or one indirection hides the reference');
});

// ── FINDING 3 (closing round): every Include shape this gate cannot name literally is refused ─────
check('RED: a rooted, expanded or listed ProjectReference Include is refused by name', () => {
  const walk = (include) => projectReferenceClosure('E/E.csproj', (p) => (p === 'E/E.csproj'
    ? `<Project><ItemGroup><ProjectReference Include="${include}" /></ItemGroup></Project>`
    : '<Project />'));

  // Each shape, with the message fragment that proves the refusal is the RIGHT one and not an accident of
  // some later assertion. The escaping-path refusal above covers `..` past the root; these are the shapes
  // that never reached that walk at all and were silently mangled into a repo-internal path instead.
  const REFUSED = [
    ['/abs/Other.csproj', /ROOTED path/u],
    ['\\abs\\Other.csproj', /ROOTED path/u],
    ['C:\\repo\\Other.csproj', /ROOTED path/u],
    ['\\\\server\\share\\Other.csproj', /ROOTED path/u],
    ['$(SharedDir)\\Other.csproj', /MSBuild property or item expression/u],
    ['@(SharedProjects)', /MSBuild property or item expression/u],
    ['..\\A\\A.csproj;..\\B\\B.csproj', /semicolon-separated LIST/u],
  ];
  for (const [include, fragment] of REFUSED) {
    assert.throws(() => walk(include), (e) => {
      assert.match(e.message, fragment, `"${include}" must be refused for the reason it is actually wrong`);
      assert.match(e.message, /E\/E\.csproj/u, 'the refusal must name the referencing project');
      return true;
    }, `"${include}" is not a single literal relative path and must be refused, not resolved`);
  }

  // GREEN, so the refusals above are not just "everything fails": the ordinary shape still walks, and a
  // forward-slash spelling is ordinary too.
  assert.deepEqual(walk('..\\A\\A.csproj'), ['E/E.csproj', 'A/A.csproj']);
  assert.deepEqual(walk('../A/A.csproj'), ['E/E.csproj', 'A/A.csproj']);
});

// ── FINDING 4: $(Property) versions and ItemGroup conditions ──────────────────────────────────────
check('a conditioned or repeated $(Property) version is refused, not resolved to the first one', () => {
  const one = '<Project><PropertyGroup><PkgVersion>1.2.3</PkgVersion></PropertyGroup></Project>';
  assert.equal(resolveCsprojProperty(one, '$(PkgVersion)'), '1.2.3',
    'a single unconditioned definition still resolves; the refusals below must not be the only behaviour');
  assert.equal(resolveCsprojProperty(one, '4.5.6'), '4.5.6', 'a literal version is passed through untouched');
  assert.equal(resolveCsprojProperty(one, '$(Absent)'), '$(Absent)',
    'a property this file does not define is left alone for the caller to fail on');

  // RED 1: two definitions, one per target framework. The old reader returned "1.0.0" and said nothing.
  const perTarget = '<Project>'
    + "<PropertyGroup Condition=\"'$(TargetFramework)' == 'net8.0'\"><PkgVersion>1.0.0</PkgVersion></PropertyGroup>"
    + "<PropertyGroup Condition=\"'$(TargetFramework)' == 'net9.0'\"><PkgVersion>2.0.0</PkgVersion></PropertyGroup>"
    + '</Project>';
  assert.equal(/<PkgVersion>([^<]+)<\/PkgVersion>/u.exec(perTarget)[1], '1.0.0',
    'the fixture must still reproduce first-definition-wins, or the refusal below proves nothing');
  assert.throws(() => resolveCsprojProperty(perTarget, '$(PkgVersion)', 'Fixture.csproj'), (e) => {
    assert.match(e.message, /\$\(PkgVersion\)/u, 'the refusal must name the property');
    assert.match(e.message, /net8\.0/u, 'the refusal must name the conditions');
    assert.match(e.message, /net9\.0/u, 'the refusal must name the conditions');
    assert.match(e.message, /Fixture\.csproj/u, 'the refusal must name the project');
    return true;
  });

  // RED 2: ONE definition, but conditioned. It used to read as unconditioned, which is the same defect.
  const conditioned = '<Project>'
    + "<PropertyGroup Condition=\"'$(OS)' == 'Windows_NT'\"><PkgVersion>1.0.0</PkgVersion></PropertyGroup>"
    + '</Project>';
  assert.throws(() => resolveCsprojProperty(conditioned, '$(PkgVersion)', 'Fixture.csproj'),
    /Windows_NT/u, 'a single conditioned definition must be refused and the condition named');

  // A condition on the property ELEMENT is the same thing one level down.
  const ownCondition = '<Project><PropertyGroup>'
    + "<PkgVersion Condition=\"'$(OS)' == 'Windows_NT'\">1.0.0</PkgVersion>"
    + '</PropertyGroup></Project>';
  assert.throws(() => resolveCsprojProperty(ownCondition, '$(PkgVersion)', 'Fixture.csproj'), /Windows_NT/u);

  // FAIL CLOSED on a definition the reader cannot see, rather than resolving from the ones it can.
  const stray = '<Project><PkgVersion>9.9.9</PkgVersion>'
    + '<PropertyGroup><PkgVersion>1.0.0</PkgVersion></PropertyGroup></Project>';
  assert.throws(() => resolveCsprojProperty(stray, '$(PkgVersion)', 'Fixture.csproj'),
    /sits somewhere it does not look|cannot tell which value ships/u);

  // RED 3: the single-definition form of the same defect. One value, one When, nothing repeated -- so the
  // "more than one definition" arm cannot save it and the ancestor is the only thing that refuses.
  const chosenOnce = '<Project><Choose>'
    + "<When Condition=\"'$(OS)' == 'Windows_NT'\">"
    + '<PropertyGroup><PkgVersion>1.0.0</PkgVersion></PropertyGroup></When>'
    + '</Choose></Project>';
  assert.equal(csprojPropertyOccurrences(chosenOnce, 'PkgVersion').found.length, 1,
    'the fixture must hold exactly one definition, or it is RED 3 again rather than its own case');
  assert.throws(() => resolveCsprojProperty(chosenOnce, '$(PkgVersion)', 'Fixture.csproj'),
    /inside <When Condition="'\$\(OS\)' == 'Windows_NT'">/u,
    'a lone definition inside a When is still conditioned, and this is the arm that used to pass it');

  // RED 4: ONE definition, inside a <Choose><When>. The PropertyGroup carries no Condition of its own and
  // the raw count agrees with the parsed count, so nothing objected: the gate read this as a single
  // UNCONDITIONED definition and pinned the notice to it. The condition is real and it is one element up.
  const chosen = '<Project><Choose>'
    + "<When Condition=\"'$(TargetFramework)' == 'net8.0'\">"
    + '<PropertyGroup><PkgVersion>1.0.0</PkgVersion></PropertyGroup></When>'
    + '<Otherwise><PropertyGroup><PkgVersion>2.0.0</PkgVersion></PropertyGroup></Otherwise>'
    + '</Choose></Project>';
  assert.deepEqual(csprojPropertyOccurrences(chosen, 'PkgVersion').found.map((f) => f.value),
    ['1.0.0', '2.0.0'],
    'the fixture must put two definitions in front of the reader, or the refusal below proves nothing');
  assert.throws(() => resolveCsprojProperty(chosen, '$(PkgVersion)', 'Fixture.csproj'), (e) => {
    assert.match(e.message, /\$\(PkgVersion\)/u, 'the refusal must name the property');
    assert.match(e.message, /inside <When Condition="'\$\(TargetFramework\)' == 'net8\.0'">/u,
      'the refusal must name the Choose/When shape it saw, not just say "conditioned"');
    assert.match(e.message, /inside <Otherwise>/u, 'an Otherwise is a condition too and must be named');
    assert.doesNotMatch(e.message, /\(unconditioned\)/u,
      'a When-guarded definition must never be described as unconditioned');
    return true;
  });

  // An unclosed <When> is refused too, rather than read as absent. A silent drop here would be the same
  // bug: the reader would report a conditioned definition as unconditioned.
  const unclosedWhen = '<Project><Choose><When Condition="\'$(OS)\' == \'Windows_NT\'">'
    + '<PropertyGroup><PkgVersion>1.0.0</PkgVersion></PropertyGroup></Project>';
  assert.throws(() => resolveCsprojProperty(unclosedWhen, '$(PkgVersion)', 'Fixture.csproj'),
    /never closed/u, 'an unclosed When must be reported, not treated as no condition at all');

  // NOT a false alarm: a PropertyGroup that merely sits AFTER a closed Choose has no ancestor.
  const afterChoose = '<Project>'
    + "<Choose><When Condition=\"'$(OS)' == 'Windows_NT'\">"
    + '<PropertyGroup><Unrelated>x</Unrelated></PropertyGroup></When></Choose>'
    + '<PropertyGroup><PkgVersion>3.2.1</PkgVersion></PropertyGroup></Project>';
  assert.equal(resolveCsprojProperty(afterChoose, '$(PkgVersion)'), '3.2.1',
    'the ancestor test must be a range test; a Choose that has already closed conditions nothing');

  // A commented-out definition is not a definition, on either side of the count.
  const commented = '<Project><PropertyGroup>'
    + '<!-- <PkgVersion>0.0.1</PkgVersion> -->\n<PkgVersion>1.2.3</PkgVersion>'
    + '</PropertyGroup></Project>';
  assert.equal(resolveCsprojProperty(commented, '$(PkgVersion)'), '1.2.3');
});

check('a Condition on the parent ItemGroup travels with the reference', () => {
  const xml = '<Project>'
    + "<ItemGroup Condition=\"'$(TargetFramework)' == 'net8.0'\">"
    + '<PackageReference Include="C.Pkg" Version="1.0.0" /></ItemGroup>'
    + '<ItemGroup><PackageReference Include="D.Pkg" Version="2.0.0" /></ItemGroup>'
    + '</Project>';
  const { raw, parsed } = parsePackageReferences(xml);
  assert.equal(raw, 2);
  assert.deepEqual(parsed.map((r) => [r.id, r.condition, r.groupCondition]), [
    ['C.Pkg', null, "'$(TargetFramework)' == 'net8.0'"],
    ['D.Pkg', null, null],
  ], 'the group condition must reach the reference, and must not leak onto the next group');

  // And it must reach the refusal text, which is the only place a reader ever sees it.
  const pins = [
    { id: 'C.Pkg', version: '1.0.0', project: 'One.csproj', groupCondition: "'$(TargetFramework)' == 'net8.0'" },
    { id: 'C.Pkg', version: '2.0.0', project: 'One.csproj', groupCondition: "'$(TargetFramework)' == 'net9.0'" },
  ];
  assert.deepEqual(reconcileShippingReferences(pins).disagreements[0].pins.map(describePin), [
    '1.0.0 in One.csproj inside an ItemGroup with Condition="\'$(TargetFramework)\' == \'net8.0\'"',
    '2.0.0 in One.csproj inside an ItemGroup with Condition="\'$(TargetFramework)\' == \'net9.0\'"',
  ], 'without the group condition this message reads "1.0.0 in One.csproj (unconditioned), 2.0.0 in '
    + 'One.csproj (unconditioned)", which looks like a contradiction in one file and is not diagnosable');
});

check('the PackageReference parser reads every attribute shape, or fails closed on the count', () => {
  for (const f of PACKAGE_REFERENCE_FIXTURES) {
    const wrapped = `<Project><ItemGroup>\n${f.xml}\n</ItemGroup></Project>`;
    const { raw, parsed } = parsePackageReferences(wrapped);
    assert.equal(raw, 1, `fixture "${f.what}" should contain exactly one raw <PackageReference occurrence`);
    if (f.unparseable) {
      assert.notEqual(parsed.length, raw,
        `fixture "${f.what}" was parsed, so it is no longer the fail-closed control. Replace it with a form `
        + 'the parser genuinely cannot read, or this check stops proving the count guard fires.');
      continue;
    }
    assert.equal(parsed.length, 1, `fixture "${f.what}" did not parse to exactly one reference`);
    assert.equal(parsed[0].id, f.id, `fixture "${f.what}" parsed the wrong package id`);
    assert.equal(parsed[0].version, f.version, `fixture "${f.what}" parsed the wrong version`);
    if (f.condition) assert.equal(parsed[0].condition, f.condition, `fixture "${f.what}" lost its Condition`);
    if (f.bodyIncludes) {
      assert.ok(parsed[0].body.includes(f.bodyIncludes), `fixture "${f.what}" lost its element body`);
    }
  }

  // NON-VACUITY, and the reason the fixtures are not enough on their own: the real projects must parse
  // completely too, or every shape above is proved against nothing that ships.
  for (const proj of SHIPPED_ENGINE_PROJECTS) {
    const { raw, parsed } = parsePackageReferences(read(proj));
    assert.equal(parsed.length, raw,
      `${proj}: ${raw} raw <PackageReference occurrences, ${parsed.length} parsed`);
  }
});

check('a package dropped from the audit by an asset setting is exempted at that exact occurrence, with measured evidence', () => {
  // RED: the exact shape that used to vanish. With no exemption for it, a PrivateAssets=all reference on a
  // runtime package must still be IN the population -- `exempt` is null, and the caller refuses. The old
  // body ran `continue` here and the package left the audit without a word.
  const runtimePkg = '<PackageReference Include="Runtime.Pkg" Version="1.0.0">'
    + '<PrivateAssets>all</PrivateAssets>'
    + '<IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>'
    + '</PackageReference>';
  const { parsed } = parsePackageReferences(`<Project><ItemGroup>${runtimePkg}</ItemGroup></Project>`);
  assert.equal(parsed.length, 1, 'the fixture must parse, or nothing below is about a real reference');
  const bare = assetDropDecision(parsed[0].id, parsed[0].body, new Map());
  assert.deepEqual(bare.drops, ['PrivateAssets=all'], 'the setting must still be RECOGNISED');
  assert.equal(bare.exempt, null,
    'an unexempted PrivateAssets=all reference must stay in the audit population. This is the refusal: the '
    + 'setting governs what flows to CONSUMERS of the project, and this very fixture also asks for the '
    + 'package\'s runtime assets, so reading it as "cannot reach the payload" was never sound.');
  // ExcludeAssets=runtime is the second spelling and it is refused the same way.
  const excluded = '<PackageReference Include="Other.Pkg" Version="1.0.0">'
    + '<ExcludeAssets>runtime</ExcludeAssets></PackageReference>';
  const p2 = parsePackageReferences(`<Project><ItemGroup>${excluded}</ItemGroup></Project>`).parsed[0];
  assert.deepEqual(assetDropDecision(p2.id, p2.body, new Map()).drops, ['ExcludeAssets=runtime']);
  assert.equal(assetDropDecision(p2.id, p2.body, new Map()).exempt, null);
  // GREEN: named, and only then dropped.
  const named = new Map([['Runtime.Pkg', { reason: 'x' }]]);
  assert.ok(assetDropDecision(parsed[0].id, parsed[0].body, named).exempt,
    'a named entry is the ONLY thing that drops a reference from the audit');
  // And an entry for a package carrying no asset setting drops nothing, so the map cannot be used to
  // quietly delete an ordinary shipping reference.
  const plain = parsePackageReferences('<Project><ItemGroup>'
    + '<PackageReference Include="Runtime.Pkg" Version="1.0.0" /></ItemGroup></Project>').parsed[0];
  assert.deepEqual(assetDropDecision(plain.id, plain.body, named), { drops: [], exempt: null },
    'ASSET_EXEMPT_PACKAGES may only excuse an ASSET-BASED drop, never a plain reference');

  // NON-VACUITY on the real tree: the scan must actually have met these settings, or every entry below is
  // being validated against nothing.
  shippingPackageReferences();
  const sighted = new Map(assetExemptionSightings.map((s) => [s.id, s]));
  assert.ok(sighted.size > 0,
    'no PackageReference in the packaged projects carries PrivateAssets or ExcludeAssets any more, so this '
    + 'check proves nothing. If the settings are genuinely gone, empty ASSET_EXEMPT_PACKAGES too.');

  // THE REAL TREE, every sighting validated rather than the last one per id.
  const liveProblems = [...ASSET_EXEMPT_PACKAGES]
    .flatMap(([id, x]) => assetExemptionProblems(id, x, assetExemptionSightings));
  assert.deepEqual(liveProblems, [],
    'an asset-based exemption no longer describes the occurrence it was measured against:\n  '
    + liveProblems.join('\n  '));

  // RED, per refusal. Each starts from a GOOD entry and breaks exactly one thing, so a fixture that stops
  // reproducing its defect shows up as a green case rather than as a silent pass.
  const GOOD = {
    reason: 'a build-time generator whose assemblies are not part of the published output of the engine',
    evidenceKind: 'measured-publish-payload',
    command: ASSET_PUBLISH_COMMAND,
    evidence: 'Fixture.Pkg.dll is absent from the assemblies of a Release publish of '
      + 'Semanticus.Engine.csproj, and the package has no entry in the publish target of '
      + 'Semanticus.Engine.deps.json',
    verifiedAtVersion: '1.2.3',
    verifiedInProject: 'A/A.csproj',
    verifiedDrops: ['PrivateAssets=all'],
  };
  const SIGHT = { id: 'Fixture.Pkg', project: 'A/A.csproj', drops: ['PrivateAssets=all'], version: '1.2.3' };
  const problemsFor = (entry, sightings = [SIGHT]) =>
    assetExemptionProblems('Fixture.Pkg', entry, sightings);
  assert.deepEqual(problemsFor(GOOD), [], 'the control entry must pass, or every refusal below is vacuous');

  // FINDING 1: blank evidence. The old test was `evidence.length > 40`, which sixty spaces satisfied.
  assert.ok('                                                                  '.length > 40,
    'this fixture no longer reproduces the raw-length hole, so the refusal below proves nothing');
  const blank = problemsFor({ ...GOOD, evidence: '   \n\t   \n                                          ' });
  assert.ok(blank.some((p) => /once whitespace is normalized/u.test(p)),
    'whitespace-only evidence must be refused. Raw length is not content.');
  assert.ok(problemsFor({ ...GOOD, reason: '        \n      \n\t    \n        \n      \n     ' })
    .some((p) => /once whitespace is normalized/u.test(p)),
    'a whitespace-only reason must be refused the same way');
  assert.ok(problemsFor({ ...GOOD, command: '   ' }).some((p) => /records no command/u.test(p)),
    'evidence that names no command run cannot be repeated by the next reader');
  assert.ok(problemsFor({ ...GOOD, evidence: 'The package was measured and it is genuinely not part of '
    + 'the payload that this project publishes, so no notice is owed for it at all' })
    .some((p) => /names no artifact/u.test(p)),
    'evidence that names no artifact is a conclusion, not a measurement');

  // FINDING 2: identity. A changed asset setting, a second reference at another version, and a sighting in
  // a project the evidence was never taken about.
  assert.ok(problemsFor(GOOD, [{ ...SIGHT, drops: ['ExcludeAssets=runtime'] }])
    .some((p) => /different setting is a different/u.test(p)),
    'a changed asset setting must fail until the payload is re-measured under it');
  const second = { ...SIGHT, project: 'B/B.csproj', version: '9.9.9' };
  // The old body, verbatim: sightings were keyed by id into a Map, so only the LAST one was ever compared
  // and a first-and-only-correct sighting could hide a second wrong one, or the reverse.
  assert.equal(new Map([second, SIGHT].map((s) => [s.id, s])).get('Fixture.Pkg').version, '1.2.3',
    'this fixture no longer reproduces the keyed-by-id shadowing, so the refusal below proves nothing');
  const twice = problemsFor(GOOD, [SIGHT, second]);
  assert.ok(twice.some((p) => /measured at 1\.2\.3/u.test(p)),
    'a SECOND reference at another version must be validated too, not shadowed by the first');
  assert.ok(twice.some((p) => /second project referencing the same id/u.test(p)),
    'a second project is a second occurrence and needs its own measurement');
  assert.ok(problemsFor(GOOD, []).some((p) => /no packaged project drops it any more/u.test(p)),
    'an exemption nothing uses is a standing permission to stop auditing a package');

  // Every drop the scan made is one of these entries, checked from the sighting side too, so a new
  // settings-based drop cannot arrive without an entry.
  const undeclared = assetExemptionSightings.map((s) => s.id).filter((id) => !ASSET_EXEMPT_PACKAGES.has(id));
  assert.deepEqual(undeclared, [], `asset-dropped with no exemption entry: ${undeclared.join(', ')}`);
  console.log(`         asset-exempt (each with measured evidence): ${[...sighted.keys()].sort().join(', ')}`);
});

check('every non-exempt PackageReference written in the packaged engine project files carries its licence in THIRD-PARTY-NOTICES.md (asset-drop exemptions are named with measured evidence; the resolved publish closure is open and tracked)', () => {
  const shipping = shippingPackageReferences();
  assert.ok(shipping.size > 0,
    'no shipping PackageReference was found in the packaged engine projects, so this check proves nothing. '
    + 'Either the engine stopped referencing NuGet packages or the csproj spelling no longer matches.');
  // Positive control: the two libraries this check was opened to cover must still be seen as shipping.
  // A rename of the Include= attribute would otherwise empty the population and pass.
  assert.ok(shipping.has('Antlr4.Runtime'),
    'Antlr4.Runtime is no longer a shipping PackageReference in the packaged engine projects. '
    + 'If it stopped shipping, drop the nuget entry; if the parser stopped matching, fix the parser.');
  assert.ok(shipping.has('Dax.Vpax') && shipping.has('Dax.Model.Extractor'),
    'Dax.Vpax / Dax.Model.Extractor are no longer shipping PackageReferences in the packaged engine projects. '
    + 'If they stopped shipping, drop the nuget entries; if the parser stopped matching, fix the parser.');

  const nuget = entries.filter((e) => e.kind === 'nuget');
  const declaredIds = new Set(nuget.map((e) => e.path));
  const unclassified = [...shipping.keys()]
    .filter((id) => !declaredIds.has(id) && !CLASSIFIED_SHIPPING_PACKAGES.has(id))
    .sort();
  assert.deepEqual(unclassified, [],
    `these PackageReferences ship in the engine payload but are neither declared as kind nuget `
    + `nor classified: ${unclassified.join(', ')}. Declare them in the manifest and put the licence `
    + 'text in THIRD-PARTY-NOTICES.md, or classify them with a reason. A hand list of library names '
    + 'in this test is how the next one is forgotten.');

  assert.ok(nuget.length > 0,
    'no kind=nuget entry is declared, so this check would only prove the classification map. '
    + 'The packaged payload still ships Antlr4.Runtime and the Dax libraries; they belong here.');

  for (const e of nuget) {
    const shipped = shipping.get(e.path);
    assert.ok(shipped,
      `${e.path} is declared as a shipped NuGet binary but is not a shipping PackageReference in `
      + `${SHIPPED_ENGINE_PROJECTS.join(', ')}. If it stopped shipping, remove the entry.`);
    assert.equal(shipped.version, e.version,
      `${e.path} is declared at ${e.version} but the packaged project pins ${shipped.version}. `
      + 'The notice must describe the version that actually ships.');

    const section = noticeSection(e.noticeSection);
    assert.ok(section,
      `THIRD-PARTY-NOTICES.md has no "## …${e.noticeSection}…" section for shipped binary ${e.path}`);
    const missing = [];
    for (const fragment of [e.path, e.licence, e.url, e.version, e.author]) {
      if (!section.includes(fragment)) missing.push(fragment);
    }
    if (e.licence === 'MIT') {
      for (const fragment of MIT_PERMISSION_FRAGMENTS) {
        if (!section.includes(fragment)) missing.push(fragment);
      }
    }
    if (e.licence === 'BSD-3-Clause') {
      for (const fragment of BSD_NOTICE_FRAGMENTS) {
        if (!section.includes(fragment)) missing.push(fragment);
      }
    }
    assert.deepEqual(missing, [],
      `shipped binary ${e.path} has no complete notice in its "${e.noticeSection}" section of `
      + `THIRD-PARTY-NOTICES.md. Missing: ${missing.join(' | ')}. Another section's copy of the same `
      + 'text does not count. The check is driven from the manifest, not from a named-library list.');
  }

  for (const [id, c] of CLASSIFIED_SHIPPING_PACKAGES) {
    assert.ok(c && typeof c === 'object' && !Array.isArray(c),
      `${id} is classified as a shipping package with a bare string. A licence ground is a claim about a `
      + 'specific version of a specific package, so it must record reason, verifiedAtVersion, eulaUrl, '
      + 'eulaReadFrom and noticeSection.');
    assert.ok(typeof c.reason === 'string' && c.reason.length > 10,
      `${id} is classified as a shipping package with no real reason`);
    assert.ok(typeof c.eulaReadFrom === 'string' && c.eulaReadFrom.length > 20,
      `${id} does not record where its EULA link was READ FROM. "It is on the Microsoft site somewhere" is `
      + 'how the wrong linkid was carried for one of these two packages for months.');
    assert.match(c.verifiedAtVersion ?? '', NUGET_VERSION,
      `${id} is classified with no pinned "verifiedAtVersion". An unpinned ground is not a ground: the terms `
      + 'a package ships under are a fact about a version, and the next bump would inherit this claim unread.');
    assert.ok(HTTPS_URL.test(c.eulaUrl ?? ''),
      `${id} records no https EULA url, so "the EULA is linked in the notices" cannot be checked`);
    if (!shipping.has(id)) {
      // Present-conditional: a classified package that no longer ships can be removed, but it must
      // not sit here after it has left the payload, or the map becomes a parking lot.
      assert.ok(false,
        `${id} is classified as a shipping package but is not a shipping PackageReference any more. `
        + 'Remove the classification.');
    }

    // THE DRIFT GATE. The ground was read at one version. If the payload moved to another, nobody has read
    // the new one, so the classification is a claim about a package that is no longer the shipped package.
    assert.equal(shipping.get(id).version, c.verifiedAtVersion,
      `${id} ships at ${shipping.get(id).version} but its licence ground was verified at `
      + `${c.verifiedAtVersion}. Re-read the shipped package's own nuspec <licenseUrl> at the new version, `
      + 'update eulaUrl and the notices section, then move the pin. Inheriting a ground across a version '
      + 'bump is how the wrong EULA link survives.');

    // The reason claims the EULA is linked in a named section. Check the claim rather than its length: the
    // section must exist and must carry THIS package's link and THIS version, not a sibling package's.
    const section = noticeSection(c.noticeSection);
    assert.ok(section,
      `${id} is classified on the ground that its EULA is linked in the "${c.noticeSection}" section of `
      + 'THIRD-PARTY-NOTICES.md, and that section does not exist.');
    // THE LINK MUST BE ATTACHED TO THIS PACKAGE, ON ONE LINE WITH ITS ID AND ITS VERSION. A section-wide
    // `includes` is not enough and was measured not to be: with both packages' links present anywhere in
    // the section, swapping this entry's eulaUrl to its sibling's link still passed. That swap IS the
    // original defect, so a check that survives it is not the check. The id is matched inside backticks
    // because `Microsoft.AnalysisServices` is a prefix of `Microsoft.AnalysisServices.AdomdClient`, and a
    // bare substring test lets the longer package's line answer for the shorter one.
    const token = '`' + id + '`';
    const line = section.split('\n').find((l) => l.includes(token) && l.includes(c.eulaUrl)
      && l.includes(c.verifiedAtVersion));
    assert.ok(line,
      `the "${c.noticeSection}" section of THIRD-PARTY-NOTICES.md has no single line carrying ${token}, `
      + `version ${c.verifiedAtVersion} and its own EULA url ${c.eulaUrl} together. A proprietary package's `
      + "only notice IS the link to its own terms, so a sibling package's link standing in for it means the "
      + 'notice names terms that do not govern this binary. State one line per package.');
  }
});

// --- 7d. A licence is carried WHOLE, or it is not carried ------------------------------------------
// THE GAP THIS CLOSES, found in review round three. Every licence-body check above was a FRAGMENT check:
// three sentences for MIT, two for BSD-3-Clause, and for Apache-2.0 nothing whatsoever. A fragment answers
// "does this look like MIT?" and never "is the whole licence here?". Measured consequence: the warranty
// disclaimer, the limitation of liability, or any middle paragraph could be deleted from a reproduced
// licence and the gate stayed green, and Apache-2.0 was not reproduced at all -- THIRD-PARTY-NOTICES.md
// LINKED to it, and a link is not a notice. Dropping a disclaimer is precisely the edit that costs
// something, so it is precisely the edit a licence gate must not sleep through.
//
// SO EACH REPRODUCED BODY IS PINNED BY DIGEST. The whitespace-normalized SHA-256 of the licence's operative
// text is compared to a known-good value. One deleted word fails.
//
// WHY WHITESPACE-NORMALIZED. Every project re-wraps the same licence, and this file is CRLF in the working
// tree and LF in the index, so a byte digest would pin the line breaks rather than the licence. Collapsing
// every whitespace run to one space compares the WORDS, which is what the licence is.
//
// WHY THE COPYRIGHT LINE SITS OUTSIDE THE DIGEST, AND WHAT NOW HOLDS IT. Each section names its own
// copyright holder, so a whole-block digest would be one unique value per section and would prove nothing
// about completeness. The digest therefore starts at the licence's own operative first words.
//
// That left a hole, found in review round four and measured: deleting the SQLBI copyright line from the
// Dax.Vpax section moved NO digest and failed NO check. The per-entry author assertion did not catch it
// either, because it is satisfied by the metadata bullet ("- Author: SQLBI") several lines above, which is
// this document's own prose and not the reproduced notice. Every MIT, BSD and ISC licence in here requires
// the copyright NOTICE to be reproduced, so a notice missing it is not the notice.
//
// So each row now pins its copyright holders by literal string, checked against the whole region rather
// than against the fenced block: some sections carry the line inside the reproduced text and some carry it
// in the prose beside it, and both reach the recipient. Delete one and the row that pins it fails by name.
//
// AND THE TABLE CANNOT GO STALE QUIETLY. A reverse sweep below finds every reproduced licence body in both
// notice documents and fails on any that no table row pins. Adding a licence text without pinning it is
// how this check would rot back into a fragment check.
const normaliseLicenceText = (s) => s.replace(/\s+/gu, ' ').trim();
const PRE_HEADING_REGION = '(content above the first heading, which no row may pin)';

// Detected on the NORMALIZED text, so line wrapping cannot hide a body from the sweep. The Apache marker
// carries the version line on purpose: "Apache License, Version 2.0" with that comma is the phrase used in
// prose and in the MCP relicensing quote, and matching it would report those as licence bodies.
const LICENCE_BODY_DETECTORS = [
  { family: 'MIT', start: 'Permission is hereby granted, free of charge' },
  { family: 'BSD-3-Clause', start: 'Redistribution and use in source and binary forms' },
  { family: 'Apache-2.0', start: 'Apache License Version 2.0, January 2004' },
  { family: 'ISC', start: 'Permission to use, copy, modify, and/or distribute this software for any purpose with or without fee is hereby granted, provided that' },
  { family: '0BSD', start: 'Permission to use, copy, modify, and/or distribute this software for any purpose with or without fee is hereby granted. THE SOFTWARE' },
];

function licenceBodiesIn(blockText) {
  const flat = normaliseLicenceText(blockText);
  const out = [];
  for (const d of LICENCE_BODY_DETECTORS) {
    const i = flat.indexOf(d.start);
    if (i >= 0) out.push({ family: d.family, digest: sha256(flat.slice(i)), words: flat.slice(i).length });
  }
  return out;
}

// The one MIT body that eight sections share, verified against an upstream artifact rather than against
// itself: `LICENSE.TXT` inside the cached `microsoft.extensions.hosting` 8.0.1 package, 1,139 bytes,
// SHA-256 of the whole file d7a68596ab69b06f… . The same normalized digest is also what the shipped .vsix
// NOTICE carries, which is how we know the two documents reproduce the same licence and not two drafts.
const MIT_CANONICAL_DIGEST = 'fe2a9817987f862eaced948f0468c7f51d2fedfc48c5c505b246a49a3870e9a5';
const UPSTREAM_MIT = 'LICENSE.TXT inside the cached microsoft.extensions.hosting 8.0.1 package';
// Apache-2.0 is an invariant document, so an upstream copy of it is an upstream copy of all of it. Verified
// against the Apache portion of `webview/node_modules/echarts/LICENSE`, an installed Apache-2.0 artifact.
const APACHE_2_0_DIGEST = '0ffddef9e48f8a09aed5caf2d44f7ba1c1be2d9b8e0a6f693b1635b2d5566645';
const UPSTREAM_APACHE = 'the Apache portion of webview/node_modules/echarts/LICENSE';
// PINNED FROM THE COMMITTED TEXT, said plainly rather than dressed up. No upstream artifact for these is on
// disk: `antlr4.runtime.4.6.6.nupkg` and `microsoft.data.sqlclient.7.0.2.nupkg` contain no licence file at
// all (listed their entries, measured 2026-08-18), and the 0BSD body is the wording the tslib package ships
// with no LICENSE file of its own on disk to compare against. (The ISC body used to be pinned this way too
// and no longer is: the d3 packages DO ship LICENSE files, so ISC_DIGEST below is verified against them.)
// A digest pinned this way cannot prove the text was right when it was written down;
// it proves it has not been truncated or edited SINCE, which is the whole of what this check claims.
const PINNED_FROM_COMMITTED = 'pinned from the committed text; no upstream artifact on disk to verify against';
// The ISC body IS verifiable against installed artifacts, unlike the 0BSD one beside it. All eight d3 ISC
// packages in webview/node_modules ship this same normalized body; the digest was taken from each of their
// LICENSE files on 2026-08-18 and all eight agreed. Only their copyright lines differ, which is why two rows
// below share this one constant.
const ISC_DIGEST = '4829cc8e654be69dd1edcebedbda21758a239c230e38f532d6bf4b0adc1a0427';

const REPRODUCED_LICENCE_TEXTS = [
  { doc: 'THIRD-PARTY-NOTICES.md', section: 'Tabular Editor 2', family: 'MIT', copyright: ['Copyright (c) 2025 Tabular Editor ApS'], digest: MIT_CANONICAL_DIGEST, from: UPSTREAM_MIT },
  { doc: 'THIRD-PARTY-NOTICES.md', section: 'ANTLR runtime', family: 'BSD-3-Clause', copyright: ['Copyright (c) 2013 Sam Harwell', 'Copyright (c) 2013 Terence Parr'], digest: '4b05ad22ab5dd8aa7cd9858615f400a3b527dbffe733b6acd592f31807fe9c37', from: PINNED_FROM_COMMITTED },
  { doc: 'THIRD-PARTY-NOTICES.md', section: 'Dax.Vpax', family: 'MIT', copyright: ['Copyright (c) 2019 SQLBI'], digest: MIT_CANONICAL_DIGEST, from: UPSTREAM_MIT },
  { doc: 'THIRD-PARTY-NOTICES.md', section: 'Newtonsoft.Json', family: 'MIT', copyright: ['Copyright (c) 2007 James Newton-King'], digest: MIT_CANONICAL_DIGEST, from: UPSTREAM_MIT },
  { doc: 'THIRD-PARTY-NOTICES.md', section: 'Model Context Protocol C# SDK', family: 'Apache-2.0', copyright: ['© Model Context Protocol a Series of LF Projects, LLC.'], digest: APACHE_2_0_DIGEST, from: UPSTREAM_APACHE },
  { doc: 'THIRD-PARTY-NOTICES.md', section: 'Model Context Protocol C# SDK', family: 'MIT', copyright: ['Copyright (c) Model Context Protocol a Series of LF Projects, LLC'], digest: MIT_CANONICAL_DIGEST, from: UPSTREAM_MIT },
  { doc: 'THIRD-PARTY-NOTICES.md', section: 'StreamJsonRpc', family: 'MIT', copyright: ['Copyright (c) Microsoft Corporation'], digest: MIT_CANONICAL_DIGEST, from: UPSTREAM_MIT },
  { doc: 'THIRD-PARTY-NOTICES.md', section: 'Azure.Identity', family: 'MIT', copyright: ['Copyright (c) 2015 Microsoft'], digest: MIT_CANONICAL_DIGEST, from: UPSTREAM_MIT },
  // ONE CHARACTER SHORT OF THE CANONICAL MIT, and recorded rather than silently normalized: this body ends
  // "IN THE SOFTWARE" with no full stop. That is how the text was written down here, the package ships no
  // licence file to compare it against, and quietly "fixing" a reproduced licence to match a different
  // project's copy would be an edit to someone else's notice. Flagged as an open question, not repaired.
  { doc: 'THIRD-PARTY-NOTICES.md', section: 'Microsoft.Data.SqlClient', family: 'MIT', copyright: ['Copyright (c) .NET Foundation. All rights reserved.'], digest: '380f9badaaa56339a1535576610a246c4114e85f44df6c172c36131b07e26ee9', from: PINNED_FROM_COMMITTED },
  { doc: 'THIRD-PARTY-NOTICES.md', section: '.NET runtime library packages', family: 'MIT', copyright: ['Copyright (c) .NET Foundation and Contributors'], digest: MIT_CANONICAL_DIGEST, from: UPSTREAM_MIT },
  // TWO BSD-3-Clause bodies, not one shared body. BSD-3-Clause is a template each project fills in, and
  // these two differ in the non-endorsement clause ("the author" vs "the copyright holder") and in the
  // liability clause ("COPYRIGHT OWNER" vs "COPYRIGHT HOLDER"). One heading used to claim to cover both
  // while carrying only zrender's text, which is the same claimed-but-absent shape the Apache section had.
  // The section names carry the package so `heading.includes(section)` picks exactly one region each.
  { doc: 'THIRD-PARTY-NOTICES.md', section: 'BSD 3-Clause License (zrender 6.1.0)', family: 'BSD-3-Clause', copyright: ['Copyright (c) 2017, Baidu Inc.'], digest: 'a11806f5afda85b2ec1013ecd567b0e0af3a04a8fcced43b82c0435e377cdc30', from: 'webview/node_modules/zrender/LICENSE' },
  { doc: 'THIRD-PARTY-NOTICES.md', section: 'BSD 3-Clause License (d3-ease 3.0.1)', family: 'BSD-3-Clause', copyright: ['Copyright 2010-2021 Mike Bostock', 'Copyright 2001 Robert Penner'], digest: '8f3332355c69aa0a203cbaf26a33602e255dbe10204886d0b9d0eae54615de82', from: 'webview/node_modules/d3-ease/LICENSE' },
  // TWO ISC ROWS, because there are two NOTICES and only one body. The eight installed d3 ISC packages ship
  // a byte-identical licence body (this digest, measured from each of their LICENSE files on 2026-08-18) and
  // TWO different copyright lines: d3-color says 2010-2022, the other seven say 2010-2021. The document used
  // to reduce both to "Copyright Mike Bostock", a string no installed package ships, and the single pin here
  // held exactly that shortened text -- so restoring the real years would have been invisible to this gate
  // and deleting them again equally so. One row per distinct notice text, each naming its own year range.
  // The body is no longer PINNED_FROM_COMMITTED: an upstream artifact for it IS on disk after all.
  { doc: 'THIRD-PARTY-NOTICES.md', section: 'ISC License (d3-color 3.1.0)', family: 'ISC', copyright: ['Copyright 2010-2022 Mike Bostock'], digest: ISC_DIGEST, from: 'webview/node_modules/d3-color/LICENSE' },
  { doc: 'THIRD-PARTY-NOTICES.md', section: 'ISC License (d3-dispatch, d3-drag, d3-interpolate, d3-selection, d3-timer, d3-transition, d3-zoom)', family: 'ISC', copyright: ['Copyright 2010-2021 Mike Bostock'], digest: ISC_DIGEST, from: 'webview/node_modules/d3-dispatch/LICENSE' },
  { doc: 'THIRD-PARTY-NOTICES.md', section: 'BSD Zero Clause License', family: '0BSD', copyright: ['Copyright (c) Microsoft Corporation'], digest: '1fc595850815e2afbaf6b3aa49edaa2fb51a99028bc3a78d0ebe834ead586914', from: PINNED_FROM_COMMITTED },
  // The notice a .vsix recipient actually receives. Fragments were all that stood behind it too.
  { doc: VSIX_NOTICE_PATH, section: 'Best Practice Analyzer rules', family: 'MIT', copyright: ['Copyright (c) 2016 Microsoft'], digest: MIT_CANONICAL_DIGEST, from: UPSTREAM_MIT },
  { doc: VSIX_NOTICE_PATH, section: 'YamlDotNet', family: 'MIT', copyright: ['Copyright (c) 2008, 2009, 2010, 2011, 2012, 2013, 2014 Antoine Aubry and contributors'], digest: MIT_CANONICAL_DIGEST, from: UPSTREAM_MIT },
];

// Blocks of candidate licence text, per document. Markdown carries them in fenced blocks; the packaged
// NOTICE is plain text divided by rules of dashes.
function licenceBlocks(doc) {
  const text = norm(read(doc));
  if (doc.endsWith('.md')) {
    // EVERY HEADING LEVEL, and the text before the first one. Two ways a body used to escape the sweep:
    // a `####` heading was not a boundary at all, so its licence text was absorbed into the parent `##`
    // region and read as if it belonged to the parent's package; and anything above the first heading was
    // in no region whatsoever, so a licence body pasted at the top of the document was invisible to both
    // the forward check and the reverse sweep. A region stops at the NEXT heading of any level, so a
    // subsection's licence text belongs to that subsection alone.
    const marks = [...text.matchAll(/^(#{1,6}) .*$/gmu)];
    const blocksIn = (region) => [...region.matchAll(/```[a-zA-Z]*\n([\s\S]*?)```/gu)].map((b) => b[1]);
    const regions = marks.map((m, i) => {
      const next = marks[i + 1];
      const region = text.slice(m.index, next ? next.index : text.length);
      return { heading: m[0].replace(/^#+\s*/u, ''), text: region, blocks: blocksIn(region) };
    });
    const preamble = text.slice(0, marks.length > 0 ? marks[0].index : text.length);
    // Named so no row's `section` can be a substring of it: content up here is meant to have no pin.
    return [{ heading: PRE_HEADING_REGION, text: preamble, blocks: blocksIn(preamble) }, ...regions];
  }
  return text.split(/^-{10,}$/mu).map((region) => ({
    heading: region.trim().split('\n')[0] ?? '(top)',
    text: region,
    blocks: [region],
  }));
}

check('every reproduced licence text is complete, checked by normalized digest', () => {
  const covered = new Set();
  for (const row of REPRODUCED_LICENCE_TEXTS) {
    assert.match(row.digest, /^[0-9a-f]{64}$/u, `${row.section}/${row.family} has no sha256 digest`);
    assert.ok((row.from ?? '').length > 10,
      `${row.section}/${row.family} does not record where its digest came from. A pin whose provenance is `
      + 'unwritten is indistinguishable from a pin taken from whatever happened to be there.');
    const regions = licenceBlocks(row.doc).filter((r) => r.heading.includes(row.section));
    // EXACTLY ONE. "At least one" let a row spread itself over every heading that happened to contain its
    // words, so a body could satisfy a pin from a neighbouring section and the failure text would name a
    // region the reader would then not find the text in.
    assert.equal(regions.length, 1,
      `${row.doc} has ${regions.length} sections whose heading contains "${row.section}" (${
        regions.map((r) => r.heading).join(' | ')}); a pin must name exactly one. If the section was `
      + 'renamed, move the pin with it; if a second section now shares the words, make the names distinct.');
    assert.ok(Array.isArray(row.copyright) && row.copyright.length > 0,
      `${row.section}/${row.family} pins no copyright holder. The licence text alone is not the notice: `
      + 'MIT, BSD and ISC each require the copyright notice to be reproduced with it.');
    const missingCopyright = row.copyright.filter((c) => !regions[0].text.includes(c));
    assert.deepEqual(missingCopyright, [],
      `the "${row.section}" section of ${row.doc} no longer states the copyright notice(s) `
      + `${missingCopyright.join(' | ')} that go with its ${row.family} text. Deleting a copyright line `
      + 'does not move the body digest, which is exactly why it is pinned here. Restore the line.');
    const found = regions.flatMap((r) => r.blocks).flatMap(licenceBodiesIn)
      .filter((b) => b.family === row.family);
    assert.ok(found.length > 0,
      `the "${row.section}" section of ${row.doc} reproduces NO ${row.family} licence body. A licence that is `
      + 'linked, summarised or named is not a licence that is carried.');
    const match = found.find((b) => b.digest === row.digest);
    assert.ok(match,
      `the ${row.family} licence text in the "${row.section}" section of ${row.doc} is not the complete, `
      + `unaltered licence. Expected normalized sha256 ${row.digest} (${row.from}), found `
      + `${found.map((b) => `${b.digest} (${b.words} chars)`).join(', ')}. Something in that body has been `
      + 'truncated, edited or re-worded. Restore the text; do not move the digest to match it.');
    covered.add(`${row.doc}|${row.section}|${row.family}|${row.digest}`);
  }

  // NON-VACUITY, stated as the three families the review named, so an emptied table cannot pass.
  for (const fam of ['MIT', 'BSD-3-Clause', 'Apache-2.0']) {
    assert.ok(REPRODUCED_LICENCE_TEXTS.some((r) => r.family === fam),
      `no ${fam} licence body is pinned, so this check no longer covers the family it was opened for`);
  }

  // THE REVERSE SWEEP: a reproduced licence body nobody pinned is the next unnoticed truncation.
  // ONE ROW PER BODY, not "some row". Zero rows means a body nobody would notice being truncated; two or
  // more means two pins claim the same text, and moving one of them would leave the other still green.
  const unpinned = [];
  const doublePinned = [];
  for (const doc of [...new Set(REPRODUCED_LICENCE_TEXTS.map((r) => r.doc))]) {
    for (const region of licenceBlocks(doc)) {
      for (const body of region.blocks.flatMap(licenceBodiesIn)) {
        const pins = REPRODUCED_LICENCE_TEXTS.filter((r) => r.doc === doc
          && region.heading.includes(r.section) && r.family === body.family && r.digest === body.digest);
        const where = `${doc} › ${region.heading} › ${body.family} (${body.digest})`;
        if (pins.length === 0) unpinned.push(where);
        if (pins.length > 1) doublePinned.push(`${where} pinned by ${pins.map((p) => p.section).join(', ')}`);
      }
    }
  }
  assert.deepEqual(unpinned.sort(), [],
    `these reproduced licence bodies are not pinned by any row of REPRODUCED_LICENCE_TEXTS, so nothing `
    + `would notice them being truncated:\n  ${unpinned.join('\n  ')}\n`
    + 'Add a row naming the document, the section, the family, the digest and where the digest came from. '
    + `A body reported under "${PRE_HEADING_REGION}" is above every heading: give it a section of its own.`);
  assert.deepEqual(doublePinned.sort(), [],
    `these reproduced licence bodies are pinned by more than one row, so no single row is load-bearing `
    + `for them:\n  ${doublePinned.join('\n  ')}\n`
    + 'Make the section names distinct enough that each body answers to exactly one pin.');
});

// --- 8. Unverified licences: acknowledged, escalated, and not quietly upgradable -------------------
check('acknowledged-unverified exceptions are capped and have not expired', () => {
  assert.ok(UNVERIFIED_ACKNOWLEDGED.size <= UNVERIFIED_ACKNOWLEDGED_CAP,
    `${UNVERIFIED_ACKNOWLEDGED.size} acknowledged-unverified exceptions, cap is ${UNVERIFIED_ACKNOWLEDGED_CAP}. `
    + 'Shipping code under an unread licence is not a state to accumulate. Resolve one before adding another.');

  // EMPTY IS AN ASSERTED STATE, NOT A SILENT SKIP. With the set empty every loop in this section and the
  // next iterates over nothing, so all four acknowledgement assertions would pass no matter what the
  // manifest said. Rather than let emptiness quietly mean "fine", it is turned into a positive claim: the
  // only thing that legitimises an empty exception list is that NO declared entry is unverified. If an
  // unverified entry ever appears without being acknowledged here, this fails with its name instead of the
  // whole section evaporating. The mutation battery covers the populated case with a synthetic fixture.
  if (UNVERIFIED_ACKNOWLEDGED.size === 0) {
    const unverifiedNow = entries.filter((e) => !e.licenceVerified).map((e) => e.path).sort();
    assert.deepEqual(unverifiedNow, [],
      'UNVERIFIED_ACKNOWLEDGED is empty, which is only true and only safe while every declared entry has a '
      + `verified licence. These are unverified and unacknowledged: ${unverifiedNow.join(', ')}. Either verify `
      + 'the licence at the pinned version or acknowledge the path here with an escalation and an expiry.');
  }

  const today = new Date().toISOString().slice(0, 10);
  for (const p of UNVERIFIED_ACKNOWLEDGED) {
    const entry = entries.find((e) => e.path === p);
    if (!entry) continue;                                    // section below fails on this
    assert.match(entry.acknowledgedUntil ?? '', ISO_DATE,
      `${p} is acknowledged as unverified with no valid "acknowledgedUntil" ISO date. An exception with no expiry `
      + 'is how a gate dies: nothing ever makes the removal happen.');
    assert.ok(entry.acknowledgedUntil > today,
      `${p} acknowledgement EXPIRED on ${entry.acknowledgedUntil} (today is ${today}). Resolve the underlying `
      + 'escalation. If you extend instead, the previous date must be appended to acknowledgedUntilHistory with '
      + 'a reason, and that ledger holds ONE entry: a second extension is not available, by design.');

    // THE LEDGER. Without it, the failure text above invited "record a deliberate new date" with no trace and
    // no limit, which is how a two-month exception becomes a two-year one. Append-only and capped at one, so
    // the second extension cannot be quiet: it has to become a conversation.
    const history = entry.acknowledgedUntilHistory ?? [];
    assert.ok(Array.isArray(history), `${p} acknowledgedUntilHistory must be an array`);
    assert.ok(history.length <= 1,
      `${p} has been extended ${history.length} times. The ledger holds one entry. This exception has outlived `
      + 'its justification: resolve it or escalate it as a new decision, do not extend it again.');
    for (const h of history) {
      assert.match(h.previousDate ?? '', ISO_DATE, `${p} history entry has no valid previousDate`);
      assert.match(h.extendedOn ?? '', ISO_DATE, `${p} history entry has no valid extendedOn date`);
      assert.ok((h.reason ?? '').length > 20, `${p} history entry records no real reason for the extension`);
      assert.ok((h.extendedBy ?? '').length > 2, `${p} history entry does not name who extended it`);
      assert.ok(h.previousDate < entry.acknowledgedUntil,
        `${p} history claims a previousDate (${h.previousDate}) that is not earlier than the current `
        + `acknowledgedUntil (${entry.acknowledgedUntil}); the ledger must record a real extension`);
    }
  }
});

check('unverified licences are acknowledged, escalated, and not stale', () => {
  const unverified = entries.filter((e) => !e.licenceVerified).map((e) => e.path);
  for (const p of unverified) {
    assert.ok(UNVERIFIED_ACKNOWLEDGED.has(p),
      `${p} has an UNVERIFIED upstream licence and is not acknowledged in this test. Verify the licence at the pinned version, or add it to UNVERIFIED_ACKNOWLEDGED with an escalation naming who decides.`);
    const entry = entries.find((e) => e.path === p);
    assert.ok(typeof entry.escalation === 'string' && entry.escalation.trim().length > 0,
      `${p} is acknowledged as unverified but records no escalation, so nobody owns resolving it`);
  }
  for (const p of UNVERIFIED_ACKNOWLEDGED) {
    if (unverified.includes(p)) continue;
    const entry = entries.find((e) => e.path === p);
    assert.ok(entry, `${p} is acknowledged as unverified but is no longer declared in the manifest at all. `
      + 'If it stopped shipping, remove it from UNVERIFIED_ACKNOWLEDGED in the same change.');
    // The previous "MIT" here was assumed from the upstream author's OTHER repositories. An upgrade needs a
    // structured record with a named human against it. THIS CHECKS SHAPE, NOT TRUTH, and cannot do more.
    const g = entry.grant;
    assert.ok(g && typeof g === 'object' && !Array.isArray(g),
      `${p} was upgraded away from UNVERIFIED without a structured "grant" object. Read its doNotUpgradeWithout field.`);
    for (const field of ['grantedBy', 'grantedOn', 'obtainedVia', 'recordedBy']) {
      assert.ok(typeof g[field] === 'string' && g[field].trim().length >= 3,
        `${p} grant is missing "${field}"; an upgrade must name who granted it, when, how, and who recorded it`);
    }
    assert.match(g.grantedOn, ISO_DATE, `${p} grant.grantedOn "${g.grantedOn}" is not an ISO date`);
    const hasUrl = typeof g.evidenceUrl === 'string' && HTTPS_URL.test(g.evidenceUrl);
    const hasQuote = typeof g.quotedGrantText === 'string' && g.quotedGrantText.trim().length >= 60;
    assert.ok(hasUrl || hasQuote,
      `${p} grant records neither an https evidenceUrl nor a quotedGrantText of at least 60 characters, so there is nothing to check it against`);
  }
});

check('every acknowledged-unverified entry keeps a warning that carries its evidence', () => {
  // Length alone was defeatable with filler, so the warning must keep the actual reasons.
  const REQUIRED = ['licenceVerified', 'grant', '404', 'license: null'];
  for (const p of UNVERIFIED_ACKNOWLEDGED) {
    const entry = entries.find((e) => e.path === p);
    if (!entry) continue;
    const w = entry.doNotUpgradeWithout;
    assert.ok(typeof w === 'string', `${p} lost its doNotUpgradeWithout warning`);
    for (const frag of REQUIRED) {
      assert.ok(w.includes(frag),
        `${p} doNotUpgradeWithout no longer mentions "${frag}"; the warning must keep the evidence, not just be long`);
    }
  }
});

// --- 9. The webview dependency closure and the lockfile agree BOTH ways ---------------------------
// This is the direction that was missing: React, ECharts and zrender were in the lockfile and the bundle
// with no manifest or notice entry, and the gate passed.
// --- 9a. MAY WE SHIP IT AT ALL? An allowlist over the LOCKFILE, checked before any bookkeeping ------
// ORDERING IS THE POINT. This started life inside the closure-agreement check, and the assertion-pinned
// mutation battery proved it was UNREACHABLE there: the manifest-vs-lockfile equality assertion fired first,
// so every licence mutation was "caught" by a bookkeeping mismatch and the licence rule never ran. A licence
// question and a bookkeeping question are different questions, so they are now different checks, and this one
// reads the LOCKFILE, which is where a real unpermitted dependency actually shows up.
// ALLOWLIST, NOT DENYLIST: a word-bounded denylist let `GPLv2`, `AGPLv3`, `GNU General Public License v3.0`,
// npm's own `UNLICENSED` and `Proprietary` through as verified licences, because denylists always fail on the
// entry nobody thought of.
check('every production dependency carries a licence we have decided we can ship', () => {
  const lock = JSON.parse(read('Semanticus.VSCode/webview/package-lock.json'));
  const rejected = [];
  let considered = 0;
  for (const [key, meta] of Object.entries(lock.packages ?? {})) {
    if (!key || meta.dev) continue;
    const name = key.replace(/^(?:.*\/)?node_modules\//u, '');
    if (!name) continue;
    considered++;
    const declaredPkg = entries.filter((e) => e.kind === 'bundle')
      .flatMap((e) => e.dependencyClosure ?? []).find((p) => p.name === name);
    const verdict = licenceVerdict(meta.license, declaredPkg);
    if (!verdict.ok) rejected.push(`${name}@${meta.version}: ${verdict.why}`);
  }
  assert.ok(considered > 20, `only ${considered} production packages considered; this check would be vacuous`);
  assert.deepEqual(rejected.sort(), [],
    `production dependency licence(s) are not on the permitted list:\n  ${rejected.join('\n  ')}\n`
    + `Permitted: ${[...PERMITTED_LICENCES].join(', ')}. THIS IS A DISTRIBUTION DECISION, NOT PAPERWORK: `
    + 'writing a notice does NOT make an unpermitted licence shippable, and adding one will not clear this. '
    + 'Remove the dependency, replace it, or escalate it as a named decision for Kane.');

  // The manifest must not RECORD an unpermitted licence either, even if the lockfile disagrees.
  const misrecorded = entries.filter((e) => e.kind === 'bundle').flatMap((e) => e.dependencyClosure ?? [])
    .map((p) => ({ p, v: licenceVerdict(p.licence, p) })).filter((x) => !x.v.ok)
    .map((x) => `${x.p.name}: ${x.v.why}`).sort();
  assert.deepEqual(misrecorded, [],
    `the manifest records unpermitted licence(s):\n  ${misrecorded.join('\n  ')}`);
});

check('the declared dependency closure and the lockfile agree in both directions', () => {
  const bundles = entries.filter((e) => e.kind === 'bundle');
  assert.ok(bundles.length > 0, 'no bundle entry is declared, so this section would be vacuous');
  const lock = JSON.parse(read('Semanticus.VSCode/webview/package-lock.json'));
  const lockProd = new Map();
  for (const [key, meta] of Object.entries(lock.packages ?? {})) {
    if (!key || meta.dev) continue;                       // dev tooling is not bundled into the webview
    const name = key.replace(/^(?:.*\/)?node_modules\//u, '');
    if (name && !lockProd.has(name)) lockProd.set(name, meta);
  }
  assert.ok(lockProd.size > 20, `only ${lockProd.size} production packages parsed from the lockfile; this guard would be vacuous`);

  for (const b of bundles) {
    const closure = b.dependencyClosure;
    assert.ok(Array.isArray(closure) && closure.length > 0, `${b.path} declares no dependencyClosure`);
    const declared = new Map(closure.map((p) => [p.name, p]));

    // direction A: everything declared must be in the lockfile at the stated version and licence
    for (const [name, p] of declared) {
      const meta = lockProd.get(name);
      assert.ok(meta, `${b.path} declares ${name}, which is not a production package in webview/package-lock.json`);
      assert.equal(meta.version, p.version, `${b.path} declares ${name}@${p.version} but the lockfile pins ${meta.version}`);
      assert.equal(meta.license ?? 'NO-LICENSE-FIELD', p.licence,
        `${b.path} declares ${name} as ${p.licence} but the lockfile says ${meta.license ?? 'NO-LICENSE-FIELD'}`);
    }
    // direction B: everything in the lockfile must be declared
    const missing = [...lockProd.keys()].filter((n) => !declared.has(n)).sort();
    assert.deepEqual(missing, [],
      `${missing.length} production dependency(ies) are in webview/package-lock.json but not declared in the manifest, `
      + `so they ship in the bundle unrecorded: ${missing.slice(0, 12).join(', ')}${missing.length > 12 ? ', …' : ''}`);

    // every distinct licence family must be named in the entry's notice section
    const section = noticeSection(b.noticeSection);
    for (const fam of [...new Set(closure.map((p) => p.licence))].sort()) {
      assert.ok(section.includes(fam),
        `the "${b.noticeSection}" notices section never names the ${fam} licence, which ${b.path} bundles`);
    }
  }
});

// The population is DERIVED from the licence, never hand-typed. It used to be a hand-written
// `apacheNoticeRequired` array, and `Array.isArray` accepted an empty one: emptying it reported PASS with the
// gate exiting 0. Since the permitted-licence allowlist admits Apache-2.0 by RULE, the next Apache dependency
// to arrive would have shipped unexamined under a check whose name claims it was examined. A check whose
// population is a list someone remembered to update is not a check on the population.
check('every Apache-2.0 dependency has its section 4(d) NOTICE obligation resolved', () => {
  const bundles = entries.filter((e) => e.kind === 'bundle');
  assert.ok(bundles.length > 0, 'no bundle entry is declared, so this check would be vacuous');
  let apacheSeen = 0;
  for (const b of bundles) {
    const section = noticeSection(b.noticeSection);
    const closure = b.dependencyClosure ?? [];
    // DERIVED: every declared package whose licence is Apache-2.0, including inside an OR/AND expression.
    const apache = closure.filter((p) => /\bApache-2\.0\b/u.test(String(p.licence ?? '')));
    for (const p of apache) {
      apacheSeen++;
      // Whether the package ships a NOTICE is a FACT about the package, so it must be recorded either way,
      // not left undefined. `false` is a legitimate answer; silence is not.
      assert.equal(typeof p.shipsUpstreamNotice, 'boolean',
        `${p.name} is Apache-2.0 but does not record shipsUpstreamNotice as a boolean. Look for a NOTICE file in `
        + `Semanticus.VSCode/webview/node_modules/${p.name}/ and record true or false; section 4(d) only bites `
        + 'when one exists, and "we did not look" is not an answer.');
      if (!p.shipsUpstreamNotice) continue;                 // no upstream NOTICE, so nothing to reproduce
      assert.ok(/Apache Software Foundation|NOTICE/u.test(section ?? ''),
        `${p.name} ships an upstream NOTICE, which Apache-2.0 section 4(d) requires us to carry, but the `
        + `"${b.noticeSection}" section of THIRD-PARTY-NOTICES.md does not reproduce it.`);
    }
  }
  // The closure currently contains echarts, so a zero here means the derivation stopped working rather than
  // that the obligation went away. Vacuity is a failure, not a pass.
  assert.ok(apacheSeen > 0,
    'no Apache-2.0 dependency was found in any declared closure. The current closure contains echarts, so this '
    + 'means the derivation is broken, not that the obligation disappeared.');
});

// --- 10. Marker scan: any tracked file carrying a foreign attribution must be declared -------------
// Broadened after review: case-insensitive, more marker forms, and it no longer skips whole directories or
// file types. It still cannot see an attribution that was removed, which is disclosed, not implied.
const OUR_HOLDERS = /Kane Snyder|Semanticus/u;
// ONE marker set, applied to EVERY tracked text file. The markdown carve-out is GONE: it let a wholesale
// borrowed README, a pasted upstream licence text, and foreign code fenced inside a `.md` all pass in
// mirror-published directories, while the identical bytes in a `.txt` were caught. It bought exactly three
// files, all three mirror-deleted, and all three are handled by the present-conditional exemption below --
// so the reason for going blanket was voided by the other half of the same repair.
// EVERY MARKER CARRIES ITS OWN FIXTURE, and the reason is not tidiness.
// Two of these patterns once ended in a RAW 0x08 BACKSPACE BYTE instead of `\b`, because an escape sequence
// was written into this file through a shell heredoc. `/@licen[cs]e<BS>/` matches nothing, ever. Both markers
// were permanently inert, so a file carrying only `@license MIT` or `Licensed under the Apache License` was
// invisible to the gate whose whole purpose is finding exactly that. It survived review because 0x08 renders
// as NOTHING in every review surface: the file viewer, grep, sed and git diff all showed the intended text.
// The gate still reported 21 of 21 and the mutation battery 39 of 39, because both marker mutations happened
// to fire through element [0].
// THE GENERAL RULE, which matters more than this instance: A LIST-WIDE CONTROL IS NOT A CONTROL FOR THE LIST.
// A control that exercises one element proves one element. Any list of patterns in this gate must have a
// per-element fixture, so deleting or corrupting any single element fails a named check. Applies to
// FOREIGN_MARKERS, MIT_PERMISSION_FRAGMENTS and DISCLOSED_GAPS alike.
const FOREIGN_MARKERS = [
  { name: 'copyright line', re: /copyright\s*(?:\(c\)|©|\d{4})/iu, matches: 'Copyright (c) 2026 Someone Else', misses: 'copyright is a concept' },
  { name: 'SPDX identifier', re: /SPDX-License-Identifier\s*[:=]/iu, matches: 'SPDX-License-Identifier: MIT', misses: 'SPDX identifiers exist' },
  { name: '@license tag', re: /@licen[cs]e\b/iu, matches: '/** @license MIT */', misses: 'see the licenses folder' },
  { name: '"licensed under" phrase', re: /licen[cs]ed\s+under\b/iu, matches: 'Licensed under the Apache License, Version 2.0', misses: 'licensed for internal use' },
  { name: '"all rights reserved"', re: /all\s+rights\s+reserved/iu, matches: 'Copyright Corp. All Rights Reserved.', misses: 'all rights are reserved to nobody' },
];
check('every attribution marker is individually proved to work', () => {
  assert.equal(FOREIGN_MARKERS.length, 5,
    `${FOREIGN_MARKERS.length} attribution markers; the set is fixed at 5. Adding or removing one is a `
    + 'deliberate change to what this gate can see, so update this count on purpose.');
  for (const m of FOREIGN_MARKERS) {
    // POSITIVE: the pattern must match text it exists to find. This is what a raw 0x08 byte fails.
    assert.match(m.matches, m.re,
      `attribution marker "${m.name}" does not match its own fixture ${JSON.stringify(m.matches)}. The pattern `
      + 'is broken or disabled, and this marker is currently detecting nothing. Check for a raw control byte: '
      + 'a collapsed escape renders as nothing in every review surface.');
    // NEGATIVE: and it must not match everything, or it would be noise rather than signal.
    assert.doesNotMatch(m.misses, m.re,
      `attribution marker "${m.name}" also matches ${JSON.stringify(m.misses)}, which is ordinary prose; too broad`);
  }
  // No pattern source may contain a control byte, which is how two of these were silently disabled.
  for (const m of FOREIGN_MARKERS) {
    assert.doesNotMatch(m.re.source, /[\u0000-\u0008\u000B\u000C\u000E-\u001F]/u,
      `attribution marker "${m.name}" contains a raw control byte in its pattern source; an escape sequence was `
      + 'collapsed when the file was written. Never write an escape into source through a shell heredoc.');
  }
});
check('every tracked file carrying a foreign attribution marker is declared or exempt', () => {
  const declared = new Set(entries.map((e) => e.path));
  const hits = [];
  const unreadable = [];
  for (const rel of trackedFiles()) {
    if (declared.has(rel) || ATTRIBUTION_MACHINERY.has(rel) || proseMentionExempt(rel)) continue;
    let buf;
    // NEVER skip silently. A file that cannot be read is an unknown, and an unknown a scan swallows is how a
    // borrowed file passes by being unreadable rather than by being clean.
    try { buf = readFileSync(resolve(repoRoot, rel)); } catch (err) {
      if (err.code === 'ENOENT') continue;          // tracked but absent: legitimate in the curated tree
      unreadable.push(`${rel} (${err.code ?? err.message})`);
      continue;
    }
    if (buf.includes(0)) continue;                                     // binary
    const text = buf.toString('utf8');
    if (!FOREIGN_MARKERS.some((m) => m.re.test(text))) continue;
    // Our own copyright line is not a foreign marker unless a foreign holder or a structured marker appears.
    const foreignHolder = [...text.matchAll(/copyright\s*(?:\(c\)|©)?\s*(?:\d{4}(?:\s*-\s*\d{4})?)?\s*([^\n]{0,60})/giu)]
      .some((m) => m[1].trim().length > 2 && !OUR_HOLDERS.test(m[1]));
    const structured = FOREIGN_MARKERS.slice(1).some((m) => m.re.test(text));
    if (!foreignHolder && !structured) continue;
    hits.push(rel);
  }
  assert.deepEqual(unreadable, [],
    `tracked file(s) could not be read, so the marker scan cannot say anything about them: ${unreadable.join(', ')}`);
  assert.deepEqual(hits, [],
    'tracked file(s) carry a third-party attribution marker but are not declared in third-party-manifest.json '
    + `(and are not exempt): ${hits.join(', ')}`);
});

check('the marker exemption lists are bounded, explained, and still needed', () => {
  assert.ok(ATTRIBUTION_MACHINERY.size <= 8,
    `${ATTRIBUTION_MACHINERY.size} attribution-machinery exemptions. This list is for files whose job IS `
    + 'attribution; it is not a place to park documents that mention a licence. Keep it under 8 or rethink.');
  assert.ok(PROSE_MENTIONS.size <= 8,
    `${PROSE_MENTIONS.size} prose-mention exemptions. If this is growing, the answer is probably to stop `
    + 'quoting licence text into internal documents, not to keep extending the list.');
  // The split between the two lists is a JUDGEMENT, and it was unchecked. Only ATTRIBUTION_MACHINERY is
  // required to survive curation, so quietly moving an entry into PROSE_MENTIONS drops that protection with
  // no signal. Overlap is now an error rather than a preference.
  const overlap = [...ATTRIBUTION_MACHINERY.keys()].filter((p) => PROSE_MENTIONS.has(p)).sort();
  assert.deepEqual(overlap, [],
    `path(s) appear in BOTH ATTRIBUTION_MACHINERY and PROSE_MENTIONS: ${overlap.join(', ')}. The lists are not `
    + 'interchangeable: only the first is required to survive the mirror, so a file in both has an ambiguous '
    + 'guarantee. Pick one.');
  // Only PROSE_MENTIONS may carry a directory class, and only one. ATTRIBUTION_MACHINERY guards files that
  // SHIP, so a directory-wide exemption there could hide a borrowed helper inside a published tree.
  assert.deepEqual([...ATTRIBUTION_MACHINERY.keys()].filter((p) => p.endsWith('/')), [],
    'ATTRIBUTION_MACHINERY carries a directory-class entry. That list exempts files that ship, so it must stay '
    + 'exact paths only; a directory there would hide a borrowed helper in a published tree.');
  assert.ok(PROSE_MENTION_CLASSES.length <= 1,
    `${PROSE_MENTION_CLASSES.length} directory classes in PROSE_MENTIONS (${PROSE_MENTION_CLASSES.join(', ')}). `
    + 'One is deliberate; a second is the list turning into a directory denylist. Justify it as a new decision.');

  const tracked = new Set(trackedFiles());
  for (const [p, reason] of [...ATTRIBUTION_MACHINERY, ...PROSE_MENTIONS]) {
    assert.ok(reason && reason.length > 10, `${p} is exempted with no real reason`);
    // PRESENT-CONDITIONAL. The curated tree is a legitimate SUBSET of the repository, so "this path must
    // exist" is the wrong invariant and asserting it broke the mirror. Absence is fine; what must not happen
    // is an exemption that is no longer needed, so when the file IS present it must still trip a marker.
    if (p.endsWith('/')) {
      // A CLASS IS VALIDATED TOO, or a directory exemption would be the one entry nothing checks. The class
      // must still be EARNING itself: if tracked files exist under the prefix, at least one has to trip a
      // marker. An empty or fully-absent prefix is the curated-tree case and is tolerated, same as a file.
      const under = [...tracked].filter((rel) => rel.startsWith(p))
        .filter((rel) => existsSync(resolve(repoRoot, rel)));
      if (!under.length) continue;
      assert.ok(under.some((rel) => FOREIGN_MARKERS.some((m) => m.re.test(readFileSync(resolve(repoRoot, rel), 'utf8')))),
        `${p} is exempted as a class but not one of its ${under.length} tracked file(s) contains any attribution `
        + 'marker; remove the exemption');
      continue;
    }
    if (!tracked.has(p) || !existsSync(resolve(repoRoot, p))) continue;
    const text = readFileSync(resolve(repoRoot, p), 'utf8');
    assert.ok(FOREIGN_MARKERS.some((m) => m.re.test(text)),
      `${p} is exempted from the marker scan but no longer contains any attribution marker; remove the exemption`);
  }

  // The exemption is blunt (it skips the file before reading it), so where we can say which upstream an
  // exempt file documents, say it and hold it to that. This is what stops a full marker exemption from
  // becoming a place to park someone else's notice.
  // SELF-TEST FIRST, on the two strings that defeated the previous version. A detector nobody tests is how
  // the © spelling slipped through, and a fixture the code under test supplies proves nothing unless the
  // fixture is written down here where a reviewer can see it.
  for (const [line, expected] of [
    ['Copyright (c) 2016 Microsoft', 'Microsoft'],
    ['Copyright (C) 2016 Microsoft', 'Microsoft'],
    ['Copyright © 2016 Microsoft', 'Microsoft'],
    ['Copyright ©  Evil', 'Evil'],
    ['Copyright (c) Not Microsoft', 'Not Microsoft'],
    ['| Copyright | `Copyright (c) 2016 Microsoft` (quoted exactly from that `LICENSE`) |', 'Microsoft'],
    ['Copyright 2016 Microsoft', 'Microsoft'],
    // NEGATIVE cases: prose about copyright is not an attribution line. These come from MIT's own text and
    // from this repository's notices, and treating them as attribution produced garbage holders.
    ['The above copyright notice and this permission notice shall be included in all', null],
    ['AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER', null],
    ['MIT requires the copyright notice and the permission notice to travel with copies', null],
  ]) {
    assert.equal(copyrightHolderOf(line), expected,
      `the copyright-holder parser reads "${line}" as "${copyrightHolderOf(line)}" and not "${expected}". `
      + 'Both of the strings that defeated the substring version must keep resolving to a FOREIGN holder, or '
      + 'this scope check goes back to being decorative.');
  }

  for (const [p, allowedHolder] of EXEMPTION_COPYRIGHT_SCOPE) {
    assert.ok(ATTRIBUTION_MACHINERY.has(p) || PROSE_MENTIONS.has(p),
      `${p} has a declared copyright scope but is not exempt from the marker scan, so the scope is dead code`);
    const abs = resolve(repoRoot, p);
    if (!existsSync(abs)) continue;                     // present-conditional, as everywhere else here
    // Every recognised spelling, not just "(c)": the © form was previously not even seen as a copyright line.
    const copyrightLines = norm(read(p)).split('\n').filter((l) => /copyright/iu.test(l) && copyrightHolderOf(l));
    // NON-VACUITY: no copyright line at all means the scope proves nothing and the file has changed shape.
    assert.ok(copyrightLines.length > 0,
      `${p} declares a copyright scope but carries no recognisable copyright line at all, so the scope `
      + 'asserts nothing. Either the file stopped quoting the notice it exists to carry, or the scope is stale.');
    const foreign = copyrightLines
      .map((l) => ({ line: l.trim(), holder: copyrightHolderOf(l) }))
      .filter((x) => x.holder !== allowedHolder);
    assert.deepEqual(foreign.map((x) => `${x.holder} (in: ${x.line.slice(0, 90)})`), [],
      `${p} is fully exempt from the marker scan, and it carries a copyright line whose holder is not exactly `
      + `"${allowedHolder}". An exempt file is never read by the marker scan, so a new upstream appearing here `
      + 'would otherwise be invisible. Declare the borrowing in the manifest, or keep this file to the one '
      + 'upstream it documents. Holder names are compared EXACTLY, so "Not Microsoft" is foreign.');
  }
});

// --- 11. Undeclared verbatim copies of the vendored donor tree -------------------------------------
const MIN_COPY_BYTES = 512;
const donorRoot = resolve(repoRoot, 'external/TabularEditor');

const normalisedHash = (abs) => {
  const buf = readFileSync(abs);
  if (buf.includes(0)) return null;
  if (buf.length < MIN_COPY_BYTES) return null;
  return sha256(norm(buf.toString('utf8')));
};

// The join, factored out so the positive control exercises THE SAME CODE the real scan uses. Previously the
// control only proved the donor index contained a donor file, which the join itself could not fail.
function findUndeclaredCopies(trackedPaths, donorByHash, declared) {
  const found = [];
  for (const rel of trackedPaths) {
    const abs = resolve(repoRoot, rel);
    let h;
    try { if (!statSync(abs).isFile()) continue; h = normalisedHash(abs); } catch { continue; }
    if (!h) continue;
    const donorPath = donorByHash.get(h);
    if (!donorPath || declared.has(rel)) continue;
    found.push(`${rel} == external/TabularEditor/${donorPath}`);
  }
  return found;
}

check('no undeclared file is a verbatim copy of a vendored donor file', () => {
  // FAIL, never skip. The mirror runs this battery as its pre-push proof, so silently passing there would
  // defeat the only reason the scan exists.
  assert.ok(existsSync(join(donorRoot, 'LICENSE')),
    'external/TabularEditor is not checked out, so the undeclared-donor-copy scan cannot run. This gate FAILS '
    + 'rather than skipping, because skipping it during the pre-push proof is exactly the hole it exists to close. '
    + 'Run: git submodule update --init --depth 1 external/TabularEditor');

  const donorByHash = new Map();
  for (const abs of walkFiles(donorRoot)) {
    let h; try { h = normalisedHash(abs); } catch { continue; }
    if (h && !donorByHash.has(h)) donorByHash.set(h, relative(donorRoot, abs).split('\\').join('/'));
  }
  assert.ok(donorByHash.size > 100,
    `only ${donorByHash.size} hashable donor files found, so this scan would be vacuous; it fails instead`);

  // POSITIVE CONTROL through the real join: present a donor file as if it were tracked, declaring nothing.
  // Exercises hashing, the file read, and the donor-to-tree lookup, not merely the index.
  const controlRel = relative(repoRoot, resolve(donorRoot, 'AntlrGrammars/DAXLexer.g4')).split('\\').join('/');
  assert.equal(findUndeclaredCopies([controlRel], donorByHash, new Set()).length, 1,
    'positive control failed: presenting a known donor file to the join produced no hit, so this scan would detect '
    + 'nothing. The hashing or the lookup is broken.');
  // And declaring it must suppress the hit, or the declared set is not really consulted.
  assert.deepEqual(findUndeclaredCopies([controlRel], donorByHash, new Set([controlRel])), [],
    'positive control failed: declaring the control file did not suppress the hit, so the declared set is ignored');

  assert.deepEqual(findUndeclaredCopies(trackedFiles(), donorByHash, new Set(entries.map((e) => e.path))), [],
    'verbatim copies of vendored donor files are not declared in third-party-manifest.json, so they ship unattributed');
});

// --- 12. The Semanticus.Dax licence note says the right thing, not just the right words ------------
check('Semanticus.Dax records that ELv2 governs it TODAY and MIT only at standalone publication', () => {
  const notePath = 'Semanticus.Dax/LICENSE-NOTE.md';
  assert.ok(existsSync(resolve(repoRoot, notePath)),
    `${notePath} is missing: the planning docs call Semanticus.Dax "the MIT package", so the in-repo copy must state that ${THIS_REPO_LICENCE} governs it until standalone publication`);
  const note = read(notePath);
  // Word presence alone would pass a note saying "MIT governs today; ELv2 later". Bind each licence to its
  // tense instead, and forbid the inversion outright.
  assert.match(note, /Today, in this repository: the Elastic License 2\.0/u,
    `${notePath} does not state, in those terms, that the Elastic License 2.0 governs this code TODAY`);
  assert.match(note, /Planned, at standalone publication: MIT/u,
    `${notePath} does not state, in those terms, that MIT applies only at standalone publication`);
  assert.match(note, /2026-07-23/u, `${notePath} does not cite the ratification date, so the claim is unverifiable`);
  assert.doesNotMatch(note, /MIT\s+(?:currently\s+)?governs|governed\s+by\s+MIT|under\s+MIT\s+today/iu,
    `${notePath} says MIT governs this code today. It does not: the root Elastic License 2.0 does, until the standalone package is actually published.`);
  const eIdx = note.indexOf('Today, in this repository: the Elastic License 2.0');
  const mIdx = note.indexOf('Planned, at standalone publication: MIT');
  assert.ok(eIdx >= 0 && mIdx > eIdx,
    `${notePath} must state the licence in force today BEFORE the planned future one, or a skim reads MIT as current`);
});

// --- 13. The procedure exists and discloses the gaps ----------------------------------------------
check('the procedure for borrowing code is written down, including what the gate cannot catch', () => {
  assert.match(notices, /##\s*Borrowing code/u,
    'THIRD-PARTY-NOTICES.md has no "Borrowing code" procedure section, so the rule for adding a borrowed file is unwritten');
  const section = noticeSection('Borrowing code');
  for (const must of ['third-party-manifest.json', 'attribution-and-license.test.mjs']) {
    assert.ok(section.includes(must), `the Borrowing code procedure never mentions ${must}`);
  }
  // The disclosed limits are load-bearing: without them the gate reads as a guarantee it is not. Checking
  // for the WORD "fragment" was a word-presence oracle -- the same defect corrected in the licence-note
  // check -- so each gap must be disclosed as an actual statement about what is not detected.
  // Patterns are whitespace-tolerant on purpose: this file is hard-wrapped, so a required phrase can be
  // split across lines, and a pattern that only matched the unwrapped form would fail on formatting rather
  // than on meaning.
  const ws = (s) => new RegExp(s.replace(/ /gu, '\\s+'), 'iu');
  const DISCLOSED_GAPS = [
    [ws('borrowed \\*\\*fragment\\*\\*[^.]*is not detected'), 'a pasted borrowed fragment is not detected'],
    [ws('borrowed \\*\\*generator\\*\\*[^.]*is not detected'), "a borrowed generator's output is not detected"],
    [ws('attribution \\*\\*removed\\*\\*[^.]*(?:no signal|nothing detects it)'), 'a stripped-attribution copy is undetectable'],
    [ws('NuGet is gated only for the references written LITERALLY in the project files of the three packaged projects'),
      'NuGet is only gated for the references written literally in the packaged project files'],
    [ws('reference the gate cannot see fails it, rather than being missed'),
      'an imported reference is refused rather than read, which is what keeps the population claim true'],
    // The population statement has to say what is OUTSIDE it, not only what is inside. A list of
    // references reads as a complete list to anyone who does not know that `dotnet publish
    // --self-contained` copies a whole runtime pack and every resolved transitive alongside them.
    [ws('transitive packages those references pull in'), 'transitive packages are ungated'],
    [ws('runtime pack that the self-contained publish copies'), 'the .NET runtime pack is ungated'],
    [ws('Deriving the resolved publish closure is the named open item'),
      'the resolved publish closure is the named open item, not something this gate proves'],
    [ws('evaluates no MSBuild at all'), 'no MSBuild is evaluated, so nothing is proved about asset flow'],
    [ws('extension-root npm closure is ungated'), 'the extension-root npm closure is ungated'],
    [ws('checks the shape of the record, not its truth'), 'a recorded grant is checked for shape, not truth'],
    [ws('exact text copy\\*{0,2} of a vendored Tabular Editor file of 512 bytes'), 'the donor-copy hash only catches exact copies of 512 bytes or more'],
  ];
  for (const [re, what] of DISCLOSED_GAPS) {
    assert.match(section, re,
      `the Borrowing code procedure no longer states that ${what}. Every gap must be written as a claim about `
      + 'what is NOT detected, not merely mentioned as a word, or the procedure overstates what the gate proves.');
  }
  assert.match(section, /not evidence that nothing was borrowed/iu,
    'the procedure must say plainly that a green run is not evidence that nothing was borrowed silently');
});

// --- The SET of check names is asserted, so a section cannot quietly stop running -----------------
// Deliberately not a count. Swapping one check for another keeps a count identical, which is how a
// disappearing section stays invisible; only comparing names catches it, and it names which one moved.
//
// The sentinels are load-bearing: mutation-battery.mjs removes this block when it runs the gate in
// non-fatal mode, because under a mutation some checks are SUPPOSED to fail and this assertion would
// then fire on every case and drown the signal. The battery asserts that the removal actually matched,
// so this block cannot be renamed out from under it silently.
// MUTATION-BATTERY-STRIP-BEGIN
{
  const ran = [...ranChecks].sort();
  const expected = [...EXPECTED_CHECK_NAMES].sort();
  const missing = expected.filter((n) => !ran.includes(n));
  const unexpected = ran.filter((n) => !expected.includes(n));
  const duplicates = ran.filter((n, i) => ran.indexOf(n) !== i);
  assert.deepEqual({ missing, unexpected, duplicates }, { missing: [], unexpected: [], duplicates: [] },
    'the set of checks that ran is not the set this gate declares.\n'
    + `  declared but DID NOT RUN: ${missing.length ? missing.join(' | ') : 'none'}\n`
    + `  ran but NOT DECLARED:     ${unexpected.length ? unexpected.join(' | ') : 'none'}\n`
    + `  ran more than once:       ${duplicates.length ? duplicates.join(' | ') : 'none'}\n`
    + '  If you added or removed a check, update EXPECTED_CHECK_NAMES deliberately. This assertion exists '
    + 'so the gate cannot silently shrink OR silently swap one protection for another.');
}
// MUTATION-BATTERY-STRIP-END
console.log(`borrowed-code attribution and licence gate passed `
  + `(${ranChecks.length} checks, exactly the ${EXPECTED_CHECK_NAMES.length} declared)`);

// --- helpers ---------------------------------------------------------------------------------------
function trackedFiles() {
  return execFileSync('git', ['ls-files'], { cwd: repoRoot, encoding: 'utf8', maxBuffer: 1 << 26 })
    .split('\n').map((s) => s.trim()).filter(Boolean).filter((p) => !p.startsWith('external/'));
}
function walkFiles(dir) {
  const out = [];
  for (const e of readdirSync(dir, { withFileTypes: true })) {
    if (e.name === '.git') continue;
    const abs = join(dir, e.name);
    if (e.isDirectory()) out.push(...walkFiles(abs));
    else if (e.isFile()) out.push(abs);
  }
  return out;
}
