#!/usr/bin/env node
// ProBench runner — drives the engine's MCP stdio door to (1) materialize + freeze the filter-context
// matrix per task, (2) dump reference vectors (gold), (3) execute arm candidates across the SAME frozen
// cells. Deterministic; no LLM anywhere in this file. Scoring lives in compare.py (independent).
//
//   node runner.mjs validate            check every task's schema references resolve on the pinned model
//   node runner.mjs gold                freeze cells + reference vectors -> gold/<task>.json
//   node runner.mjs score <arm>         runs/<arm>/<task>.json {dax} -> runs/<arm>/<task>.scored.json
//
// Requires: SEMANTICUS_ENGINE_DLL env (path to Semanticus.Engine.dll) and a live model the engine can
// see (Power BI Desktop open, or an attached owner engine — the MCP door is attach-or-own).

import { spawn } from 'node:child_process';
import { readFileSync, writeFileSync, mkdirSync, readdirSync, existsSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createHash } from 'node:crypto';

const __dirname = dirname(fileURLToPath(import.meta.url));
const TASKS = JSON.parse(readFileSync(join(__dirname, 'tasks.json'), 'utf8'));
const CONFIG = JSON.parse(readFileSync(join(__dirname, 'config.json'), 'utf8'));

// ---- minimal MCP stdio client -------------------------------------------------------------------
class Mcp {
    constructor(child) { this.child = child; this.id = 0; this.pending = new Map(); this.buf = ''; }
    static async start() {
        const dll = process.env.SEMANTICUS_ENGINE_DLL || CONFIG.engineDll;
        if (!dll || !existsSync(dll)) throw new Error('Set SEMANTICUS_ENGINE_DLL to the Semanticus.Engine.dll path (or config.json engineDll).');
        const child = spawn('dotnet', [dll, '--mcp'], { stdio: ['pipe', 'pipe', 'inherit'] });
        const mcp = new Mcp(child);
        child.stdout.setEncoding('utf8');
        child.stdout.on('data', d => mcp._onData(d));
        await mcp.request('initialize', {
            protocolVersion: '2024-11-05',
            capabilities: {},
            clientInfo: { name: 'probench', version: '1.0' },
        });
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
    notify(method, params) {
        this.child.stdin.write(JSON.stringify({ jsonrpc: '2.0', method, params }) + '\n');
    }
    async tool(name, args) {
        const res = await this.request('tools/call', { name, arguments: args });
        const text = (res.content || []).map(c => c.text || '').join('');
        if (res.isError) throw new Error(`${name}: ${text.slice(0, 500)}`);
        try { return JSON.parse(text); } catch { return text; }
    }
    close() { try { this.child.stdin.end(); this.child.kill(); } catch { /* done */ } }
}

// ---- DAX helpers ---------------------------------------------------------------------------------
const litFor = v => (typeof v === 'number' ? String(v) : `"${String(v).replace(/"/g, '""')}"`);
const cellFilterDax = filters => filters.map(f => `${f.column} = ${litFor(f.value)}`).join(', ');

// One frozen cell -> one query. CALCULATE with equality filters models a single visual cell.
function cellQuery(expr, filters) {
    const body = filters.length ? `CALCULATE ( ${expr}, ${cellFilterDax(filters)} )` : `CALCULATE ( ${expr} )`;
    return `EVALUATE ROW ( "v", ${body} )`;
}

async function runCell(mcp, expr, filters) {
    try {
        const r = await mcp.tool('run_dax', { query: cellQuery(expr, filters), maxRows: 2 });
        if (r && r.error) return { kind: 'ERROR', detail: String(r.error).slice(0, 200) };
        const rows = r.rows || r.Rows || [];
        if (!rows.length) return { kind: 'BLANK' };
        const first = Array.isArray(rows[0]) ? rows[0][0] : Object.values(rows[0])[0];
        if (first === null || first === undefined || first === '') return { kind: 'BLANK' };
        return { kind: 'VALUE', value: first };
    } catch (e) {
        return { kind: 'ERROR', detail: String(e.message).slice(0, 200) };
    }
}

// ---- matrix materialization ----------------------------------------------------------------------
async function membersOf(mcp, column, take) {
    // top members by row presence, deterministic order
    const q = `EVALUATE TOPN ( ${take}, SUMMARIZECOLUMNS ( ${column}, "n", COUNTROWS ( Sales ) ), [n], DESC, ${column}, ASC )`;
    const r = await mcp.tool('run_dax', { query: q, maxRows: take + 1 });
    if (r && r.error) throw new Error(`membersOf(${column}): ${r.error}`);
    const rows = r.rows || [];
    return rows.map(row => (Array.isArray(row) ? row[0] : Object.values(row)[0])).filter(v => v !== null && v !== undefined);
}

async function dateRange(mcp) {
    const r = await mcp.tool('run_dax', { query: `EVALUATE ROW ( "minY", MIN ( 'Date'[Year] ), "maxY", MAX ( 'Date'[Year] ) )`, maxRows: 2 });
    const row = (r.rows || [])[0];
    const vals = Array.isArray(row) ? row : Object.values(row || {});
    return { minYear: Number(vals[0]), maxYear: Number(vals[1]) };
}

async function materializeCells(mcp, task) {
    const m = task.matrix;
    const take = m.maxMembersPerColumn || 3;
    const cols = m.groupBy;
    const memberSets = [];
    for (const c of cols) memberSets.push(await membersOf(mcp, c, take));
    const { minYear, maxYear } = await dateRange(mcp);

    const cells = [];
    // cross product of the materialized members
    const cross = (idx, acc) => {
        if (idx === cols.length) { cells.push({ filters: acc.slice() }); return; }
        for (const v of memberSets[idx]) { acc.push({ column: cols[idx], value: v }); cross(idx + 1, acc); acc.pop(); }
    };
    cross(0, []);
    // single-column slices (grain sensitivity: one filter at a time)
    cols.forEach((c, i) => memberSets[i].slice(0, 2).forEach(v => cells.push({ filters: [{ column: c, value: v }] })));

    const edges = m.edges || [];
    if (edges.includes('grandTotal')) cells.push({ filters: [], edge: 'grandTotal' });
    if (edges.includes('beyondDateRange')) cells.push({ filters: [{ column: "'Date'[Year]", value: maxYear + 5 }], edge: 'beyondDateRange' });
    if (edges.includes('firstYearInData')) {
        const monthCol = cols.find(c => /\[Month\]/.test(c));
        const f = [{ column: "'Date'[Year]", value: minYear }];
        if (monthCol) { const months = await membersOf(mcp, monthCol, 1); if (months.length) f.push({ column: monthCol, value: months[0] }); }
        cells.push({ filters: f, edge: 'firstYearInData' });
    }
    if (edges.includes('fiscalBoundaryMonths')) {
        for (const mm of CONFIG.fiscalBoundaryMonths || []) {
            cells.push({ filters: [{ column: "'Date'[Year]", value: maxYear - 1 }, { column: "'Date'[Month]", value: mm }], edge: 'fiscalBoundary' });
        }
    }
    if (edges.includes('emptyIntersection')) {
        // deliberately likely-empty: rarest member of col0 x beyond-range year when dates present, else rarest x rarest
        const rareQ = `EVALUATE TOPN ( 1, SUMMARIZECOLUMNS ( ${cols[0]}, "n", COUNTROWS ( Sales ) ), [n], ASC, ${cols[0]}, ASC )`;
        const rr = await mcp.tool('run_dax', { query: rareQ, maxRows: 2 });
        const rare = ((rr.rows || [])[0] || [])[0] ?? Object.values((rr.rows || [])[0] || {})[0];
        if (rare !== undefined) cells.push({ filters: [{ column: cols[0], value: rare }, { column: "'Date'[Year]", value: maxYear + 5 }], edge: 'emptyIntersection' });
    }
    // dedupe identical filter sets
    const seen = new Set();
    return cells.filter(c => { const k = JSON.stringify(c.filters); if (seen.has(k)) return false; seen.add(k); return true; });
}

// ---- commands --------------------------------------------------------------------------------------
async function cmdValidate(mcp) {
    let bad = 0;
    for (const col of TASKS.schemaAssumptions.columns) {
        const r = await mcp.tool('run_dax', { query: `EVALUATE ROW ( "n", COUNTROWS ( DISTINCT ( ${col} ) ) )`, maxRows: 2 });
        const ok = !(r && r.error);
        if (!ok) { bad++; console.log(`MISSING  ${col}  (${r.error})`); }
    }
    console.log(bad === 0 ? `schema OK: all ${TASKS.schemaAssumptions.columns.length} columns resolve` : `${bad} schema references FAILED — fix tasks.json or the pinned model before gold`);
    for (const t of TASKS.tasks) {
        const v = await mcp.tool('validate_dax', { expression: t.referenceDax });
        const valid = v.valid !== false && v.Valid !== false;
        if (!valid) { bad++; console.log(`REFERENCE INVALID  ${t.id}`); }
    }
    process.exitCode = bad ? 1 : 0;
}

async function cmdGold(mcp) {
    mkdirSync(join(__dirname, 'gold'), { recursive: true });
    const modelHash = createHash('sha256').update(JSON.stringify(await mcp.tool('get_model_summary', {}))).digest('hex').slice(0, 16);
    for (const t of TASKS.tasks) {
        const cells = await materializeCells(mcp, t);
        for (const c of cells) c.gold = await runCell(mcp, t.referenceDax, c.filters);
        writeFileSync(join(__dirname, 'gold', `${t.id}.json`), JSON.stringify({ task: t.id, modelHash, frozenAt: new Date().toISOString(), cells }, null, 1));
        const errs = cells.filter(c => c.gold.kind === 'ERROR').length;
        console.log(`gold ${t.id}: ${cells.length} cells frozen${errs ? ` (${errs} ERROR cells — check the reference!)` : ''}`);
    }
}

async function cmdScore(mcp, arm) {
    const dir = join(__dirname, 'runs', arm);
    for (const f of readdirSync(dir).filter(f => f.endsWith('.json') && !f.endsWith('.scored.json'))) {
        const run = JSON.parse(readFileSync(join(dir, f), 'utf8'));
        const gold = JSON.parse(readFileSync(join(__dirname, 'gold', `${run.task}.json`), 'utf8'));
        const out = [];
        for (const c of gold.cells) out.push({ filters: c.filters, edge: c.edge, candidate: await runCell(mcp, run.dax, c.filters) });
        writeFileSync(join(dir, f.replace('.json', '.scored.json')), JSON.stringify({ task: run.task, arm, attempts: run.attempts, cells: out }, null, 1));
        console.log(`scored ${arm}/${run.task}: ${out.length} cells`);
    }
}

const [, , cmd, arg] = process.argv;
const mcp = await Mcp.start();
try {
    if (cmd === 'validate') await cmdValidate(mcp);
    else if (cmd === 'gold') await cmdGold(mcp);
    else if (cmd === 'score') { if (!arg) throw new Error('score <arm>'); await cmdScore(mcp, arg); }
    else console.log('usage: node runner.mjs validate | gold | score <arm>');
} finally { mcp.close(); }
