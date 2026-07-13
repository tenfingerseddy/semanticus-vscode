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
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const __dirname = dirname(fileURLToPath(import.meta.url));
const MODELS = JSON.parse(readFileSync(join(__dirname, 'models.json'), 'utf8'));
const WORK = join(__dirname, 'work');
const RESULTS = join(__dirname, 'results');

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
function cmdFetch() {
    mkdirSync(WORK, { recursive: true });
    const pinned = [];
    for (const m of MODELS.corpus) {
        const dir = join(WORK, m.repo.replace('/', '__'));
        try {
            if (!existsSync(dir)) {
                console.log(`clone ${m.repo} (sparse)`);
                // blobless sparse clone: only the model folder's blobs are fetched (fabric-samples is huge)
                execFileSync('git', ['clone', '--depth', '1', '--filter=blob:none', '--sparse', `https://github.com/${m.repo}.git`, dir], { stdio: 'inherit' });
                // file-style paths (.../Model.bim) sparse to their folder; folder paths sparse as-is
                const sparseTarget = /\.bim$/i.test(m.path) ? dirname(m.path) : m.path;
                execFileSync('git', ['-C', dir, 'sparse-checkout', 'set', sparseTarget], { stdio: 'inherit' });
            }
            const commit = execFileSync('git', ['-C', dir, 'rev-parse', 'HEAD'], { encoding: 'utf8' }).trim();
            pinned.push({ ...m, commit });
            const target = join(dir, m.path);
            if (!existsSync(target)) console.log(`  WARN: path missing after clone: ${m.repo}/${m.path}`);
        } catch (e) {
            console.log(`  FETCH FAILED ${m.repo}: ${String(e.message).slice(0, 120)}`);
            pinned.push({ ...m, commit: null, fetchError: true });
        }
    }
    writeFileSync(join(__dirname, 'models.pinned.json'), JSON.stringify({ pinnedAt: new Date().toISOString(), corpus: pinned }, null, 1));
    console.log(`pinned ${pinned.length} repos -> models.pinned.json`);
}

async function cmdScan() {
    mkdirSync(RESULTS, { recursive: true });
    const pinned = JSON.parse(readFileSync(join(__dirname, 'models.pinned.json'), 'utf8'));
    const rows = [];
    for (const m of pinned.corpus) {
        if (m.fetchError) { rows.push({ repo: m.repo, path: m.path, kind: m.kind, error: 'fetch failed' }); continue; }
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
                repo: m.repo, path: m.path, kind: m.kind, commit: m.commit,
                grade: s.grade ?? s.Grade, score: s.overall ?? s.Overall,
                gatedBy: s.gatedBy ?? s.GatedBy ?? null,
                findings: s.totalFindings ?? s.TotalFindings ?? null,
            });
            console.log(`${m.repo}: ${rows.at(-1).grade} (${rows.at(-1).score})`);
        } catch (e) {
            rows.push({ repo: m.repo, path: m.path, kind: m.kind, error: String(e.message).slice(0, 200) });
            console.log(`${m.repo}: FAILED ${e.message}`);
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

const cmd = process.argv[2];
if (cmd === 'fetch') cmdFetch();
else if (cmd === 'scan') await cmdScan();
else if (cmd === 'report') cmdReport();
else console.log('usage: node scan.mjs fetch | scan | report');
