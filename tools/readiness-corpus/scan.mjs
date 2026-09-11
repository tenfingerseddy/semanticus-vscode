#!/usr/bin/env node
// Readiness corpus scanner — makes the "median community model grades X" claim reproducible.
//
//   node scan.mjs fetch    shallow-clone every corpus repo into work/ (git required)
//   node scan.mjs scan     open each model OFFLINE in the engine, ai_readiness_summary, write results
//   node scan.mjs report   grade distribution + median -> results/corpus-report.md
//
// Scans are offline (TMDL/BIM metadata only — no data, no credentials, nothing executed), so the
// corpus can be anyone's public repos. models.json pins the corpus (repo + path + commit once
// fetched); results/ commits the raw per-model scores next to the summary.

import { spawn, execFileSync } from 'node:child_process';
import { readFileSync, writeFileSync, mkdirSync, existsSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { homedir } from 'node:os';

const __dirname = dirname(fileURLToPath(import.meta.url));
const MODELS = JSON.parse(readFileSync(join(__dirname, 'models.json'), 'utf8'));
const WORK = join(__dirname, 'work');
const RESULTS = join(__dirname, 'results');
const REPO_ROOT = join(__dirname, '..', '..');

// Engine errors quote the ABSOLUTE path they failed on, and results/ is committed. That is how the machine
// owner's home-directory path (which embeds a private tenant name) got published in the mirror: nobody wrote
// it down, a generated error message carried it. So every captured message is redacted HERE, at the one place
// text enters a committed file, rather than trusting each call site to remember.
// The result is also portable: two machines now produce the same rows for the same failure.
const pathRe = (p) => new RegExp(p.split(/[\\/]/).map((s) => s.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')).join('[\\\\/]+'), 'gi');
export function redactLocalPaths(text) {
    return String(text)
        .replace(pathRe(REPO_ROOT), '<repo>')                       // longest first: inside the checkout
        .replace(pathRe(homedir()), '<home>')                       // anything else under this account
        .replace(/[A-Za-z]:[\\/]+Users[\\/]+[^\\/\s"']+/g, '<home>') // backstop: any other Windows profile path
        .replace(/\/(?:home|Users)\/[^/\s"']+/g, '<home>');          // backstop: POSIX/macOS profile path
}

// ---- minimal MCP stdio client (same shape as tools/probench/runner.mjs) --------------------------
class Mcp {
    constructor(child) { this.child = child; this.id = 0; this.pending = new Map(); this.buf = ''; }
    static async start() {
        let dll = process.env.SEMANTICUS_ENGINE_DLL || MODELS.engineDll;
        if (dll && !existsSync(dll)) dll = join(__dirname, dll);   // config path is relative to this folder
        if (!dll || !existsSync(dll)) throw new Error('Set SEMANTICUS_ENGINE_DLL (path to Semanticus.Engine.dll).');
        const child = spawn('dotnet', [dll, '--mcp'], { stdio: ['pipe', 'pipe', 'inherit'] });
        const mcp = new Mcp(child);
        child.stdout.setEncoding('utf8');
        child.stdout.on('data', d => mcp._onData(d));
        await mcp.request('initialize', { protocolVersion: '2024-11-05', capabilities: {}, clientInfo: { name: 'readiness-corpus', version: '1.0' } });
        mcp.notify('notifications/initialized', {});
        return mcp;
    }
    _onData(d) {
        this.buf += d;
        let nl;
        while ((nl = this.buf.indexOf('\n')) >= 0) {
            const line = this.buf.slice(0, nl).trim();
            this.buf = this.buf.slice(nl + 1);
            if (!line) continue;
            let msg; try { msg = JSON.parse(line); } catch { continue; }
            if (msg.id !== undefined && this.pending.has(msg.id)) {
                const { resolve, reject } = this.pending.get(msg.id);
                this.pending.delete(msg.id);
                msg.error ? reject(new Error(msg.error.message)) : resolve(msg.result);
            }
        }
    }
    request(method, params) {
        const id = ++this.id;
        return new Promise((resolve, reject) => {
            this.pending.set(id, { resolve, reject });
            this.child.stdin.write(JSON.stringify({ jsonrpc: '2.0', id, method, params }) + '\n');
        });
    }
    notify(method, params) { this.child.stdin.write(JSON.stringify({ jsonrpc: '2.0', method, params }) + '\n'); }
    async tool(name, args) {
        const res = await this.request('tools/call', { name, arguments: args });
        const text = (res.content || []).map(c => c.text || '').join('');
        if (res.isError) throw new Error(`${name}: ${text.slice(0, 400)}`);
        try { return JSON.parse(text); } catch { return text; }
    }
    close() { try { this.child.stdin.end(); this.child.kill(); } catch { /* done */ } }
}

// ---- commands --------------------------------------------------------------------------------------

/**
 * The corpus definition and the pin file are parameters rather than module constants so the fetch path can
 * be exercised against a throwaway local git repo. `node scan.mjs fetch` passes nothing and gets the real
 * corpus; nothing else may write the committed pin file.
 */
export function cmdFetch(options = {}) {
    const work = options.work ?? WORK;
    const models = options.models ?? MODELS;
    const pinnedPath = options.pinnedPath ?? join(__dirname, 'models.pinned.json');

    const existing = existsSync(pinnedPath)
        ? JSON.parse(readFileSync(pinnedPath, 'utf8'))
        : { pinnedAt: null, corpus: [] };
    const pins = new Map((existing.corpus ?? []).filter(p => p.commit).map(p => [p.repo, p]));

    mkdirSync(work, { recursive: true });
    const pinned = [];
    for (const m of models.corpus) {
        const dir = join(work, m.repo.replace('/', '__'));
        const url = m.url ?? `https://github.com/${m.repo}.git`;
        const pin = pins.get(m.repo);
        try {
            const commit = pin
                ? checkoutPinned(dir, url, m.path, pin.commit)
                : cloneDefaultBranch(dir, url, m.path);

            // The pin ENTRY wins over models.json, so a field only the pin file carries (license, notes) is
            // not silently dropped, and the commit written back is the pinned one, never a fresh observation.
            pinned.push({ ...m, ...(pin ?? {}), commit });
            const target = join(dir, m.path);
            if (!existsSync(target)) console.log(`  WARN: path missing after fetch: ${m.repo}/${m.path}`);
        } catch (e) {
            // Both halves matter: main keeps an existing pin alive across a failed fetch, and this branch
            // redacts local paths out of the message. A fetch error message is one of the two places the
            // employer name and home path reached the public repo, so it is redacted BEFORE it is truncated,
            // which is why the fuller 200-char slice from main is safe to keep.
            console.log(`  FETCH FAILED ${m.repo}: ${redactLocalPaths(e.message).slice(0, 200)}`);
            // A failed fetch must not drop an existing pin: the SHA is still the corpus definition, and the
            // next run has to try for it again rather than re-pin to whatever it can reach.
            pinned.push({ ...m, ...(pin ?? {}), commit: pin?.commit ?? null, fetchError: true });
        }
    }

    // Only write when something actually changed. A re-run that reproduces the corpus must leave the
    // committed pin byte-identical, including pinnedAt - a churning timestamp reads like a re-pin.
    const changed = JSON.stringify(existing.corpus ?? []) !== JSON.stringify(pinned);
    if (!changed) {
        console.log(`pin unchanged: ${pinned.length} repos reproduced at their pinned commits`);
        return;
    }
    writeFileSync(pinnedPath, JSON.stringify({ pinnedAt: new Date().toISOString(), corpus: pinned }, null, 1));
    console.log(`pinned ${pinned.length} repos -> ${pinnedPath}`);
}

/**
 * Put <paramref name="dir"/> ON the pinned commit, whatever it is currently on.
 *
 * `git clone --depth 1` cannot check out an arbitrary SHA - it only ever fetches the default branch tip -
 * so the pinned path does not clone at all: init, point at the remote, fetch the ONE commit by SHA, detach
 * onto it. Verified to work against github.com for a non-tip commit as well as for a tip, and against a
 * local fixture remote. An existing directory is never trusted: if its HEAD is not the pin, it is corrected.
 */
function checkoutPinned(dir, url, path, commit) {
    if (!existsSync(join(dir, '.git'))) {
        mkdirSync(dir, { recursive: true });
        execFileSync('git', ['init', '-q', dir], { stdio: 'inherit' });
        execFileSync('git', ['-C', dir, 'remote', 'add', 'origin', url], { stdio: 'inherit' });
        execFileSync('git', ['-C', dir, 'sparse-checkout', 'init', '--cone'], { stdio: 'inherit' });
        execFileSync('git', ['-C', dir, 'sparse-checkout', 'set', sparseTargetFor(path)], { stdio: 'inherit' });
    }

    // Right ref AND clean tree, or it needs work. Checking only the ref left a hand-edited model file in
    // place, because the directory existed and HEAD looked correct.
    const dirty = dirtyPaths(dir);
    if (head(dir) !== commit || dirty === null || dirty.length > 0) {
        console.log(`checkout ${commit.slice(0, 12)} (pinned)${dirty?.length ? `, discarding ${dirty.length} local change(s)` : ''}`);
        if (!hasCommit(dir, commit))
            execFileSync('git', ['-C', dir, 'fetch', '--depth', '1', '--filter=blob:none', 'origin', commit], { stdio: 'inherit' });

        // --force, because these are throwaway clones of public repos: the pinned content IS the wanted state
        // and a local edit to one is nothing to preserve. Nothing here is ever a user's working copy.
        execFileSync('git', ['-C', dir, 'checkout', '-q', '--detach', '--force', commit], { stdio: 'inherit' });

        // `checkout --force` does NOT remove untracked files, measured, so without this an injected file
        // survives every repair and the pin can never be reproduced again. Safe for the same reason --force is.
        execFileSync('git', ['-C', dir, 'clean', '-qfdx'], { stdio: 'inherit' });
    }

    // A pin that silently lands elsewhere is worse than no pin, so the result is read back, not assumed, and
    // the tree is read as well as the ref.
    const actual = head(dir);
    if (actual !== commit) throw new Error(`HEAD is ${actual} after checking out the pinned ${commit}`);

    const stillDirty = dirtyPaths(dir);
    if (stillDirty === null) throw new Error(`cannot read the work tree state at ${dir}`);
    if (stillDirty.length > 0)
        throw new Error(`work tree still has ${stillDirty.length} modified path(s) after checking out ${commit}: ${stillDirty.slice(0, 3).join(', ')}`);

    return commit;
}

/**
 * The only path that MINTS a pin: a repo with no committed commit yet. Shallow blobless sparse clone of the
 * default branch, exactly as before, and the SHA it lands on becomes the pin for every run after this one.
 */
function cloneDefaultBranch(dir, url, path) {
    if (!existsSync(join(dir, '.git'))) {
        console.log(`clone ${url} (sparse, unpinned)`);
        // blobless sparse clone: only the model folder's blobs are fetched (fabric-samples is huge)
        execFileSync('git', ['clone', '--depth', '1', '--filter=blob:none', '--sparse', url, dir], { stdio: 'inherit' });
        execFileSync('git', ['-C', dir, 'sparse-checkout', 'set', sparseTargetFor(path)], { stdio: 'inherit' });
    }
    return head(dir);
}

function head(dir) {
    try {
        return execFileSync('git', ['-C', dir, 'rev-parse', 'HEAD'], { encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] }).trim();
    } catch {
        return null;   // a fresh `git init` has no HEAD yet
    }
}

function hasCommit(dir, commit) {
    try {
        execFileSync('git', ['-C', dir, 'cat-file', '-e', `${commit}^{commit}`], { stdio: ['ignore', 'ignore', 'ignore'] });
        return true;
    } catch {
        return false;
    }
}

/** File-style paths (.../Model.bim) sparse to their folder; folder paths sparse as-is. */
function sparseTargetFor(path) {
    return /\.bim$/i.test(path) ? dirname(path) : path;
}

/**
 * Whether a scan of `work/<repo>` may be PUBLISHED under this entry's pinned commit.
 *
 * Honouring the pin in `fetch` is not enough, because `fetch` is not where the pin is CLAIMED. `scan` copies
 * each entry's commit into results/scans.json, and that is the provenance the published corpus report rests
 * on, so `git -C work/<repo> checkout main && node scan.mjs scan && node scan.mjs report` would republish new
 * scores wearing the old pinned SHA. A reader of scans.json is entitled to conclude the scores belong to the
 * commit beside them, so the tree is read HERE, at the point of claim, and a mismatch refuses to publish
 * rather than relabelling.
 *
 * An entry with no pinned commit claims no provenance and is scanned as-is; there is nothing to contradict.
 */
export function provenanceFor(m, work = WORK) {
    if (!m.commit) return { ok: true, commit: null };

    const dir = join(work, m.repo.replace('/', '__'));
    const observedCommit = head(dir);

    if (observedCommit !== m.commit) {
        return {
            ok: false,
            commit: m.commit,
            observedCommit,
            error: observedCommit
                ? `work tree is at ${observedCommit}, not the pinned ${m.commit}; run \`node scan.mjs fetch\``
                : `no checkout at ${dir} to verify against the pinned ${m.commit}; run \`node scan.mjs fetch\``,
        };
    }

    // The REF is not the TREE. HEAD proves which commit was checked out; it says nothing about what is on
    // disk now, and the scanner reads the disk. A hand-edited model file leaves the SHA intact and makes the
    // published score describe content that commit never had.
    const modified = dirtyPaths(dir);
    if (modified === null)
        return { ok: false, commit: m.commit, observedCommit, error: `cannot read the work tree state at ${dir}` };
    if (modified.length > 0) {
        return {
            ok: false,
            commit: m.commit,
            observedCommit,
            modified,
            error: `work tree is at the pinned ${m.commit} but is not clean (${modified.length} modified: ` +
                   `${modified.slice(0, 3).join(', ')}); run \`node scan.mjs fetch\``,
        };
    }

    return { ok: true, commit: m.commit, observedCommit };
}

/**
 * Paths git reports as changed OR UNTRACKED, or null if the state cannot be read at all.
 *
 * <para>
 * Untracked files are counted, and excluding them was a real hole rather than a style choice. 37 of the 41
 * corpus entries are folders, which `open_model` reads as a directory, so an injected `.tmdl` changes what is
 * scanned without modifying a single tracked file: with `--untracked-files=no` the tree read as clean and the
 * score published under the pinned SHA. Worse, it is unrepairable that way, because `checkout --force` does
 * not remove untracked files, so the false provenance would be permanent.
 * </para>
 * <para>
 * The reason first given for the exclusion was also simply wrong, and measuring it settled the matter: on a
 * real cone-sparse clone of `microsoft/fabric-samples` at the pinned commit, 375 tracked files with 7 on disk,
 * `git status --porcelain` reports ZERO lines with untracked reporting ON. Files outside the cone do not show
 * up as anything. The exclusion bought nothing and cost the invariant.
 * </para>
 */
function dirtyPaths(dir) {
    try {
        const out = execFileSync('git', ['-C', dir, 'status', '--porcelain'],
            { encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] });
        return out.split('\n').map(l => l.trim()).filter(Boolean);
    } catch {
        return null;
    }
}

async function cmdScan() {
    mkdirSync(RESULTS, { recursive: true });
    const pinned = JSON.parse(readFileSync(join(__dirname, 'models.pinned.json'), 'utf8'));
    const rows = [];
    for (const m of pinned.corpus) {
        if (m.fetchError) { rows.push({ repo: m.repo, path: m.path, kind: m.kind, error: 'fetch failed' }); continue; }
        const provenance = provenanceFor(m, WORK);
        if (!provenance.ok) {
            rows.push({ repo: m.repo, path: m.path, kind: m.kind, commit: m.commit, observedCommit: provenance.observedCommit, error: provenance.error });
            console.log(`${m.repo}: SKIPPED ${provenance.error}`);
            continue;
        }
        let target = join(WORK, m.repo.replace('/', '__'), m.path);
        if (!existsSync(target)) { rows.push({ repo: m.repo, path: m.path, kind: m.kind, error: 'path missing' }); continue; }
        // PBIP folders that carry TMSL (model.bim) instead of TMDL: open the .bim file directly
        if (!existsSync(join(target, 'definition')) && existsSync(join(target, 'model.bim'))) target = join(target, 'model.bim');
        const mcp = await Mcp.start();   // fresh engine per model: no session bleed
        try {
            const open = await mcp.tool('open_model', { path: target });
            if (open && open.error) throw new Error(open.error);
            const s = await mcp.tool('ai_readiness_summary', {});
            // ReadinessSummary fields: Grade, Overall (the 0-100 score), TotalFindings — casing per serializer
            rows.push({
                repo: m.repo, path: m.path, kind: m.kind, commit: provenance.commit,
                grade: s.grade ?? s.Grade, score: s.overall ?? s.Overall,
                gatedBy: s.gatedBy ?? s.GatedBy ?? null,
                findings: s.totalFindings ?? s.TotalFindings ?? null,
            });
            console.log(`${m.repo}: ${rows.at(-1).grade} (${rows.at(-1).score})`);
        } catch (e) {
            rows.push({ repo: m.repo, path: m.path, kind: m.kind, error: redactLocalPaths(e.message).slice(0, 200) });
            console.log(`${m.repo}: FAILED ${redactLocalPaths(e.message)}`);
        } finally { mcp.close(); }
    }
    writeFileSync(join(RESULTS, 'scans.json'), JSON.stringify({ scannedAt: new Date().toISOString(), rows }, null, 1));
}

function cmdReport() {
    const { rows } = JSON.parse(readFileSync(join(RESULTS, 'scans.json'), 'utf8'));
    const ok = rows.filter(r => !r.error && r.score !== undefined && r.score !== null);
    const failed = rows.filter(r => r.error);
    const scores = ok.map(r => Number(r.score)).sort((a, b) => a - b);
    const median = scores.length ? scores[Math.floor(scores.length / 2)] : null;
    const grades = {};
    for (const r of ok) grades[r.grade] = (grades[r.grade] || 0) + 1;
    const gradeOf = s => (s >= 90 ? 'A' : s >= 80 ? 'B' : s >= 70 ? 'C' : s >= 60 ? 'D' : 'F');

    const lines = [
        '# AI-readiness: the public-model corpus scan', '',
        `Scanned **${ok.length} public semantic models** (offline, metadata only) with the Semanticus`,
        'AI-readiness analyzer. Corpus, commits and raw scores are committed beside this file;',
        'anyone can re-run it with `node scan.mjs fetch && scan && report`.', '',
        `**Median score: ${median} (grade ${median !== null ? gradeOf(median) : '?'})**`, '',
        '| Grade | Models |', '|---|---|',
        ...['A', 'B', 'C', 'D', 'F'].filter(g => grades[g]).map(g => `| ${g} | ${grades[g]} |`), '',
        '## Disclosed bias',
        'Public repos skew toward samples and teaching material, which are typically CLEANER than',
        'production client models. If anything, this understates the real-world problem.', '',
        '## Per-model', '',
        '| Model | Kind | Grade | Score |', '|---|---|---|---|',
        ...ok.sort((a, b) => a.score - b.score).map(r => `| ${r.repo} | ${r.kind} | ${r.grade} | ${r.score} |`),
    ];
    if (failed.length) {
        lines.push('', `## Not scannable (${failed.length})`, '');
        for (const r of failed) lines.push(`- ${r.repo}: ${r.error}`);
    }
    writeFileSync(join(RESULTS, 'corpus-report.md'), lines.join('\n') + '\n');
    console.log(lines.slice(0, 14).join('\n'));
}

// Dispatch only when this file IS the program. Importing it must not run a command, so the fetch path can be
// tested against a fixture corpus without also fetching the real one.
if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
    const cmd = process.argv[2];
    if (cmd === 'fetch') cmdFetch();
    else if (cmd === 'scan') await cmdScan();
    else if (cmd === 'report') cmdReport();
    else console.log('usage: node scan.mjs fetch | scan | report');
}
