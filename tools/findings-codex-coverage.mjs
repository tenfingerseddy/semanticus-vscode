#!/usr/bin/env node
// THE ONLINE HALF OF THE FINDING QUEUE. It answers the one question a file can never answer about itself:
// does a row exist for every finding the automated reviewer actually left?
//
//   node tools/findings-codex-coverage.mjs [path-to-findings.md]
//
// WHY IT IS SEPARATE FROM tools/tasks-register-check.mjs. That check is offline, deterministic and runs
// inside the required `gate` (via Semanticus.VSCode/test/findings-register.test.mjs). This one needs the
// network and a token, so a local run without one CANNOT perform it. The split is so that the thing which
// cannot run everywhere is NAMED, rather than being a silent skip inside a check that reports success.
//
// WHY IT KEYS ON COMMENT IDS, NOT PR NUMBERS. Keyed on `codex/PR#292`, an agent under deadline collapses
// every finding on that pull request into ONE row and the check is satisfied while nineteen findings are
// unconsumed. Nothing can see it. So `source` reads `codex/PR#<n>/<comment-id>` and this enumerates the live
// ids for every pull request named that way, requiring one row per id. That is the only version of the
// invariant with teeth.
//
// IT FAILS LOUDLY WHEN IT CANNOT ANSWER, and never passes by default. No token, no network, an HTTP error, a
// body that does not parse: all of them exit 2. A gate that reports success when it could not look is worse
// than no gate, because it manufactures confidence. This is the same defect family as a check that supplies
// its own evidence (see [T206] and F-019).
//
// WHAT IT STILL CANNOT SEE, stated rather than implied:
//   - A pull request that is named NOWHERE in the register is never enumerated, so a reviewer's findings can
//     be omitted wholesale by simply not filing one. Closing that needs either the intake control in the
//     agent definition that emits each finding, or the current-pull-request arm noted at the bottom of this
//     file, which is a deliberate open decision rather than an oversight.
//   - Review-level summary bodies are not comments and carry no per-finding id. Only inline review comments
//     (the /pulls/N/comments collection) have stable ids, so only those are enumerable.

import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const REPO = process.env.GITHUB_REPOSITORY || 'tenfingerseddy/semanticus';
const API = process.env.GITHUB_API_URL || 'https://api.github.com';

// Every plausible name for the token, because the one thing that must not happen is a run that skips because
// it looked for the wrong variable. CI supplies GITHUB_TOKEN; a local run has GH_TOKEN or GITHUB_PAT.
const TOKEN = process.env.GITHUB_TOKEN || process.env.GH_TOKEN || process.env.GITHUB_PAT || '';

const SOURCE_CODEX = /^codex\/PR#(\d+)\/(\d+)$/;

// The automated reviewer's account. Matched loosely on purpose: the bot's exact login has changed once
// already, and a rename must not turn this check into a no-op. Anything whose login names codex counts.
const CODEX_AUTHOR = /codex/i;

export function rowedCodexIds(findingsText) {
    const byPr = new Map();       // pr number -> Map(commentId -> { id, claim, site })
    for (const line of findingsText.split(/\r?\n/)) {
        const trimmed = line.trim();
        if (!trimmed.startsWith('|')) continue;
        const cells = trimmed.replace(/^\|/, '').replace(/\|$/, '').split('|').map(c => c.trim());
        if (cells.length !== 7 || !/^F-\d{3,}$/.test(cells[0])) continue;
        const source = SOURCE_CODEX.exec(cells[1]);
        if (!source) continue;
        const pr = Number(source[1]);
        if (!byPr.has(pr)) byPr.set(pr, new Map());
        byPr.get(pr).set(source[2], { id: cells[0], claim: cells[2], site: cells[3] });
    }
    return byPr;
}

// =====================================================================================================
// BINDING A ROW TO ITS OWN COMMENT. Sol's defeat, 2026-07-30: the register was judged only against itself.
// The claim was checked for LENGTH and the file:line cell for non-emptiness, while the live comment body and
// the live comment `path` were fetched and then discarded. So an agent could rewrite a claim to something
// trivial and weaken the site to any path the fixing commit happened to touch, and every check stayed green.
// That is the detector grading its own homework, the same defect class that defeated the tenant gate.
//
// TWO ASSERTIONS, AND THEY ARE NOT THE SAME STRENGTH. Saying so is part of the fix.
//   - THE PATH CHECK IS EXACT. The comment carries `path`; the row's file:line cell must name it. No
//     heuristic, no threshold, no judgement.
//   - THE CLAIM CHECK IS A HEURISTIC. It counts distinctive tokens shared with the live comment body. It
//     cannot tell a faithful paraphrase from an unfaithful one; it can only tell that the row is still about
//     the same subject. It is a floor against hollowing out, not a proof of fidelity.
//
// The comment body is deliberately NOT copied into the register. A stored copy drifts, and a drifted copy is
// worse than none because it reads as authority.
// =====================================================================================================

const STOPWORDS = new Set(('the a an and or of to in on for it its is are was were be been that this these those ' +
    'with without by from as at not no non any all each every some so than then there here which what when where ' +
    'who whom whose how why if but also both only same such other another more most less least can could should ' +
    'would may might must will shall do does did done have has had having i we you they he she them their our your ' +
    'my me us').split(' '));

// FOUR distinctive tokens, and the number is measured rather than picked. Against the seven live Codex comments
// on #285, #292 and #293, the real rows share 7, 8, 8, 9, 9, 10 and 14 tokens with their own comment bodies,
// while three separately written hollow claims ("A claim that is long enough to be a claim...", "Codex raised a
// point on this pull request...", "A minor style nit in a test file...") share 2, 0 and 2. A floor of four sits
// clear of both ends. Hyphenated and dotted words are split and a trailing plural is stripped, without which a
// faithful paraphrase scored 1: `heading-form`/`heading-based` and `definitions`/`definition` are the same
// subject written twice.
const CLAIM_OVERLAP_FLOOR = 4;

export function distinctiveTokens(text) {
    const stripped = String(text)
        .replace(/!\[[^\]]*\]\([^)]*\)/g, ' ')     // badge images, which every Codex comment opens with
        .replace(/<[^>]+>/g, ' ')                  // the <sub> wrappers around those badges
        .toLowerCase();
    const out = new Set();
    for (const word of stripped.match(/[a-z][a-z0-9_.\-/]*/g) ?? [])
        for (let part of word.split(/[-/_.]/)) {
            if (part.length < 4) continue;
            if (part.endsWith('s') && part.length > 4) part = part.slice(0, -1);
            if (STOPWORDS.has(part)) continue;
            out.add(part);
        }
    return out;
}

export function claimOverlap(claim, body) {
    const claimTokens = distinctiveTokens(claim);
    const bodyTokens = distinctiveTokens(body);
    return [...claimTokens].filter(t => bodyTokens.has(t)).length;
}

// The site cell holds things like `a/b.cs:12; c/d.json` or `.github/workflows/ci.yml:14-25 (as reviewed)`.
// A token counts as a path when it ends in an extension OR contains a slash: `.githooks/pre-commit` is a
// real anchor with no extension at all (found when #305's comments could not be bound), and requiring an
// extension made such a comment permanently unbindable. The cost is that prose like `and/or` also surfaces,
// which is harmless where this is consumed: every caller asks "does the set INCLUDE the comment's own
// path", so a stray extra entry can never satisfy a binding the cell does not carry.
export function sitePaths(site) {
    const out = new Set();
    for (const token of String(site).split(/[;,\s]+/)) {
        const bare = token.replace(/[(),;]/g, '').replace(/:.*$/, '');
        if (bare && (/\.[A-Za-z0-9]+$/.test(bare) || /[/\\]/.test(bare))) out.add(bare.replace(/\\/g, '/'));
    }
    return [...out];
}

// The declared escape hatch for a comment that carries no path at all (a pull-request-level comment). Legal,
// and never silent, exactly like the FIXED hatch.
export const CANNOT_BIND = 'CANNOT BIND:';

async function apiPage(path) {
    const url = `${API}${path}`;
    let response;
    try {
        response = await fetch(url, {
            headers: {
                accept: 'application/vnd.github+json',
                authorization: `Bearer ${TOKEN}`,
                'user-agent': 'semanticus-findings-coverage',
                'x-github-api-version': '2022-11-28',
            },
        });
    } catch (error) {
        throw new Error(`could not reach ${url}: ${error.message}`);
    }
    if (!response.ok) throw new Error(`${url} returned HTTP ${response.status} ${response.statusText}`);
    let body;
    try { body = await response.json(); } catch (error) { throw new Error(`${url} returned a body that is not JSON: ${error.message}`); }
    if (!Array.isArray(body)) throw new Error(`${url} returned ${typeof body}, not the expected array of comments`);
    return body;
}

// Paginated deliberately. A pull request with more than one page of review comments is exactly the case where
// a truncated read would report full coverage over half the findings.
export async function liveCodexIds(pr) {
    const ids = [];
    for (let page = 1; page <= 20; page++) {
        const body = await apiPage(`/repos/${REPO}/pulls/${pr}/comments?per_page=100&page=${page}`);
        for (const comment of body) {
            const login = comment?.user?.login ?? '';
            // The BODY is carried here and compared in memory. It is never written into the register: a stored
            // copy of a review comment drifts, and a drifted copy reads as authority.
            if (CODEX_AUTHOR.test(login))
                ids.push({ id: String(comment.id), path: comment.path, login, body: comment.body ?? '' });
        }
        if (body.length < 100) return ids;
    }
    throw new Error(`pull request #${pr} has more than 20 pages of review comments, which this refuses to guess about`);
}

// THE SWEEP. Ruled 2026-07-30: intake is NOT blocked per pull request. The blocking version would cost a
// second push on every reviewed pull request, about $1.26 each, forever, and it changes merge behaviour
// repository-wide. Instead this sweep asserts that every pull request merged ABOVE THE FROZEN FLOOR has its
// live Codex comment ids rowed.
//
// THERE IS NO TIME WINDOW, AND THAT IS THE FIX. It used to sweep the last fourteen days, and Codex defeated it
// on #296 (comment 3679929366, anchored on this very enumeration): a merged pull request whose Codex comment
// nobody rowed left the swept set on day fifteen, and the required gate went green having proved nothing. That
// directly contradicts the only invariant the queue has, that an omission turns `main` red and KEEPS it red.
// It is the defect family this project keeps shipping, a check that switches itself off, with TIME as the
// switch, so nobody had to be fooled for it to happen. The window is REMOVED rather than widened, because a
// wider window is the same bug with a longer fuse.
//
// THE FULL SWEEP IS ALSO THE CHEAP OPTION, measured rather than assumed (2026-07-30): exactly 1 closed pull
// request sits above #295 and it merged, so the walk below reads ONE page. That number was first written here as
// 2, which was wrong: #297 was still OPEN and a `state=closed` listing does not contain it. Corrected because a
// number with no command beside it is the next stale premise. The command is
//   gh api "repos/<repo>/pulls?state=closed&sort=created&direction=desc&per_page=100" --jq '[.[]|select(.number>295)]|length'
// and the live run agrees: FINDINGS_SWEEP=1 printed "sweep: 1 pull request(s) merged above #295".
// Ordered by creation descending the
// numbers descend monotonically, so it stops AT the floor instead of reading the repository's history, and its
// cost grows only with merges above the floor. Persisting an unresolved set was the brief's fallback and it is
// not needed: a stored set is state that can drift or be pruned, and the floor already bounds the work.
//
// IT IS DETECTIVE, NOT PREVENTIVE, and that is a real weakness rather than a framing. It fires AFTER the merge,
// so a finding can sit unrowed for the gap between a merge and the next sweep. What it buys is that the
// staleness cannot PERSIST: an unrowed finding turns `main` red and stays red. What it does not buy is stopping
// the merge that created the gap. Anyone who wants that must accept the per-pull-request cost above.
// THE FLOOR, and why it and not a date is what bounds the sweep. MEASURED: the fourteen days before the queue
// was born hold 60 merged pull requests carrying 82 live Codex review comments, 75 of them unrowed. That is not
// a bug in the sweep, it IS the historical backlog, and it is deliberately scoped to a second pass in [T208]
// rather than seeded here. A sweep that lands red on day one turns `main` red and keeps it red, which trains
// everyone to ignore it: the same failure as the random red in F-001. The floor excludes that backlog for good,
// and unlike a date it cannot move without a reviewable code change.
//
// So the sweep is floored at a pull-request NUMBER, read from the register itself so it is reviewable rather
// than buried here, and a MISSING floor is a failure rather than a default in either direction. A number is
// used instead of a date because merge timestamps around the floor are ambiguous at day granularity and a
// number never is.
//
// HONEST CONSEQUENCE, stated rather than discovered later: on the day this lands the sweep has nothing to
// check, because nothing has merged above the floor yet. It starts biting on the next merge. That is the price
// of not laundering 75 unconsumed findings into a green run, and raising the floor later would be the same
// laundering with a newer number.
// FROZEN, not configured. Sol's defeat: a floor read from the register is a DIAL. Any value at or above 295
// passed, so an agent could advance it past reviews it had omitted and stay green, which is a way to declare
// the backlog handled without handling it. A staleness check must not carry its own adjustment.
//
// So the birth pull request is a constant here, and the register's line must EQUAL it. Raising the floor is now
// a code change that breaks a test and appears in a diff, which is the only form of "we are skipping some
// history" that a reviewer can see.
export const QUEUE_BIRTH_PR = 295;
const SWEEP_FLOOR = /^sweep-from-pr:\s*(\d+)\s*$/m;

// A refusal bound, not a truncation bound. 60 pages is 6000 closed pull requests above the floor; reaching it
// means the walk could not finish, and it throws rather than returning what it happened to have read.
const SWEEP_MAX_PAGES = 60;

export function sweepFloor(findingsText) {
    const match = SWEEP_FLOOR.exec(findingsText);
    return match ? Number(match[1]) : null;
}

// EVERY merged pull request above `floor`, with no date arithmetic anywhere in it. `fetchPage` is injectable so
// the enumeration logic can be asserted offline; it supplies pages, never the verdict.
//
// `sort=created&direction=desc` is load-bearing and not a preference. Pull request numbers are assigned in
// creation order, so this is the ONLY ordering under which the numbers descend monotonically, and that is what
// lets the walk stop AT the floor rather than reading the whole closed history. The previous `sort=updated`
// could not support a stop at all (an old pull request touched today appears first), which is how a date filter
// ended up doing the bounding. Both properties are asserted, because an ordering the code silently depends on is
// the next stale premise: an out-of-order page THROWS rather than stopping early, since a short walk is the
// aged-out bug by another route.
export async function mergedPullRequests(floor, fetchPage = apiPage) {
    const numbers = [];
    let previous = Infinity;
    for (let page = 1; page <= SWEEP_MAX_PAGES; page++) {
        const body = await fetchPage(`/repos/${REPO}/pulls?state=closed&sort=created&direction=desc&per_page=100&page=${page}`);
        for (const pr of body) {
            if (pr.number >= previous)
                throw new Error(`the pull request list returned #${pr.number} after #${previous}, so it is not in ` +
                    'creation order and stopping at the floor by number would cut the sweep short');
            previous = pr.number;
            if (pr.number <= floor) return numbers;     // numbers descend, so everything left is below the floor
            if (pr.merged_at) numbers.push(pr.number);
        }
        // A short page is the end of the closed history with the floor never reached, which is complete rather
        // than truncated: there is nothing below the floor to stop at.
        if (body.length < 100) return numbers;
    }
    throw new Error(`walked ${SWEEP_MAX_PAGES} pages of closed pull requests without reaching the floor #${floor}. ` +
        'Refusing rather than returning a partial set, which would report full coverage over every pull request ' +
        'this never read.');
}

export async function main(findingsPath, log = console.log, warn = console.error) {
    const byPr = rowedCodexIds(readFileSync(findingsPath, 'utf8'));

    if (!TOKEN) {
        warn('No GitHub token in GITHUB_TOKEN, GH_TOKEN or GITHUB_PAT, so the live Codex comment ids CANNOT be');
        warn('enumerated. This is a FAILURE and not a skip: the whole point of this check is that a register');
        warn('cannot be trusted to describe comments nobody looked at. Run it in CI, or export a token.');
        return 2;
    }

    if (byPr.size === 0) {
        warn(`${findingsPath} names no source in codex/PR#<n>/<comment-id> form, so there is nothing to`);
        warn('enumerate. That is a FAILURE rather than a pass, because it is indistinguishable from the register');
        warn('having been rewritten to avoid this check. If the register genuinely has no automated-review rows,');
        warn('this assertion is the wrong shape and should be changed on purpose.');
        return 1;
    }

    // The enumerated set is the pull requests the register names, PLUS, when sweeping, every pull request merged
    // above the floor whether the register mentions it or not. The second half is what makes an omitted review
    // visible: a finding can be left out of the register, but the merge cannot be hidden, and it cannot age out.
    const sweep = process.env.FINDINGS_SWEEP === '1';
    const enumerated = new Set(byPr.keys());
    if (sweep) {
        const floor = sweepFloor(readFileSync(findingsPath, 'utf8'));
        if (floor === null) {
            warn(`${findingsPath} carries no "sweep-from-pr: <n>" line, so the sweep does not know where the`);
            warn('historical backlog ends. Refusing: sweeping from zero reports 75 unconsumed findings and turns');
            warn('main permanently red, and sweeping from nothing reports success over everything.');
            return 2;
        }
        if (floor !== QUEUE_BIRTH_PR) {
            warn(`${findingsPath} says "sweep-from-pr: ${floor}" but the queue's birth pull request is`);
            warn(`#${QUEUE_BIRTH_PR}, and that number is frozen in tools/findings-codex-coverage.mjs.`);
            warn('A floor that can be edited is a dial: raise it past a review you omitted and the sweep stays');
            warn('green while the finding is gone. Changing it has to be a code change a reviewer can see.');
            return 2;
        }
        let merged;
        try { merged = await mergedPullRequests(floor); } catch (error) {
            warn(`Could not list the pull requests merged above #${floor}: ${error.message}`);
            warn('Failing rather than sweeping an empty set, which would report success over everything.');
            return 2;
        }
        log(`  sweep: ${merged.length} pull request(s) merged above #${floor}`);
        for (const pr of merged) enumerated.add(pr);
    }

    const problems = [];
    for (const pr of [...enumerated].sort((a, b) => a - b)) {
        const rowed = byPr.get(pr) ?? new Map();
        let live;
        try { live = await liveCodexIds(pr); } catch (error) {
            warn(`Could not enumerate the review comments on #${pr}: ${error.message}`);
            warn('Failing rather than assuming. An unreachable API is not an empty result.');
            return 2;
        }
        const liveIds = new Set(live.map(c => c.id));
        log(`  #${pr}: ${live.length} live Codex comment(s), ${rowed.size} rowed`);

        for (const comment of live) {
            const row = rowed.get(comment.id);
            if (!row) {
                problems.push(`#${pr} comment ${comment.id} (on ${comment.path}) has NO row in the register. ` +
                    'Every automated finding gets its own row and its own verdict; arrival is triage.');
                continue;
            }

            // EXACT. The comment says which file it is about, so the row does not get to choose.
            if (!comment.path) {
                if (!row.site.includes(CANNOT_BIND))
                    problems.push(`${row.id}: #${pr} comment ${comment.id} carries no path, so the exact binding ` +
                        `cannot run. Record why in the file:line cell as "${CANNOT_BIND} <reason>" rather than ` +
                        'leaving an unbindable row looking bound.');
            } else if (!sitePaths(row.site).includes(comment.path)) {
                problems.push(`${row.id}: the file:line cell names ${sitePaths(row.site).join(', ') || '(no path)'} ` +
                    `but #${pr} comment ${comment.id} is on ${comment.path}. The comment decides what the finding ` +
                    'is about, not the row. A site cell the row picks for itself can be weakened to whatever a ' +
                    'fixing commit happened to touch, which is the row grading its own homework.');
            }

            // HEURISTIC, and labelled as such wherever it appears. It cannot judge fidelity; it can only refuse
            // a claim that has stopped being about the same subject as the comment it cites.
            const overlap = claimOverlap(row.claim, comment.body);
            if (overlap < CLAIM_OVERLAP_FLOOR)
                problems.push(`${row.id}: the claim shares only ${overlap} distinctive token(s) with #${pr} ` +
                    `comment ${comment.id}, below the floor of ${CLAIM_OVERLAP_FLOOR}. This is a HEURISTIC, not ` +
                    'a fidelity proof: it catches a claim hollowed out until only its length is left, which is ' +
                    'all the offline check was ever able to measure.');
        }

        for (const [id, row] of rowed)
            if (!liveIds.has(id))
                problems.push(`${row.id} claims #${pr} comment ${id}, which is not a live Codex comment on that ` +
                    'pull request. A source that names nothing is worse than an empty cell, because it reads as ' +
                    'provenance.');
    }

    if (problems.length > 0) {
        warn('\nThe finding queue does not cover the automated reviews:');
        for (const problem of problems) warn(`  ${problem}`);
        warn('\nEnumerate them yourself with:');
        warn(`  gh api repos/${REPO}/pulls/<n>/comments --jq '.[].id'`);
        return 1;
    }

    log('\nEvery live Codex review comment on every pull request named in the register has exactly one row.');
    return 0;
}

// THE OPEN DECISION, left as one arm rather than taken quietly. Adding the pull request under review to the
// enumerated set (`process.env.FINDINGS_PR`) would close intake for NEW reviews, not just filed ones: a pull
// request could not merge until every Codex comment on it had a row. That is what the design asks for, and it
// costs a second push on every reviewed pull request, because the review arrives after the run that would
// have to see it. It changes merge behaviour repository-wide, so it is the architect's call, not this file's.

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
    const repoRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..');
    const findingsPath = process.argv[2] ?? resolve(repoRoot, 'docs/findings.md');
    console.log(`${findingsPath} (live enumeration against ${REPO})`);
    // process.exitCode, NOT process.exit(). Calling process.exit() while fetch still holds a pooled socket
    // aborts the Windows event loop with a libuv assertion and exit code 127, which is a failure by accident
    // rather than by verdict. Measured 2026-07-30 on the HTTP 401 path: the correct message printed and then
    // the process died at 127 instead of 2. Setting the code and letting the loop drain reports the verdict.
    process.exitCode = await main(findingsPath);
}
