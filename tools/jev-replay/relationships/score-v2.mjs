// Score the relationship.join replay under bar-v2.json: each decision on its own units, the Pro path (Jev when sure,
// else the rule) against the rule, with an exact McNemar test. Reads the results file the replay writes
// (JEV_REPLAY_RESULTS, default results.json, in JEV_REPLAY_OUT or ~/.cache/jev-replay-relationships) and the labels it
// names. Writes <stem>-v2.json and <stem>-v2.md beside it. Aggregate numbers only.

import { readFileSync, writeFileSync } from 'node:fs';
import { homedir } from 'node:os';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const lc = (s) => (s ?? '').toLowerCase();

/** Exact two-sided McNemar p on the discordant pairs b (arm right, rule wrong) and c (the other way). */
export function mcnemar(b, c) {
    const n = b + c;
    if (n === 0) return 1;
    const k = Math.min(b, c);
    let p = 0, coef = 1;
    for (let i = 0; i <= k; i++) {
        if (i > 0) coef = (coef * (n - i + 1)) / i;
        p += coef;
    }
    return Math.min(1, (2 * p) / 2 ** n);
}

/** Paired comparison over units: each unit is { arm: bool, rule: bool }. */
export function paired(units) {
    const n = units.length;
    const a = units.filter((u) => u.arm).length, r = units.filter((u) => u.rule).length;
    const b = units.filter((u) => u.arm && !u.rule).length, c = units.filter((u) => !u.arm && u.rule).length;
    return { n, arm: n ? a / n : 0, rule: n ? r / n : 0, gain: n ? (a - r) / n : 0, b, c, p: mcnemar(b, c) };
}

export function ships(bar, d) { return d.n > 0 && d.gain >= bar.minPointsOverRule && d.p < bar.maxP; }

/** The units of every decision for one model and one arm against the rule. */
export function units(model, labels, armName) {
    const arm = model[armName], rule = model.rule;
    const byFrom = (rels) => new Map(rels.map((r) => [lc(r.from), r]));
    const A = byFrom(arm), R = byFrom(rule), L = byFrom(labels);
    const cols = new Set([...model.asks.map((a) => lc(a.column)), ...labels.map((l) => lc(l.from))]);
    const target = (m, c) => lc(m.get(c)?.to) || 'none';
    const d1 = [...cols].map((c) => ({ id: c, arm: target(A, c) === target(L, c), rule: target(R, c) === target(L, c) }));

    const tbl = (ref) => lc(ref).split('/')[0];
    const pair = (r) => [tbl(r.from), tbl(r.to)].sort().join('|');
    const pairs = new Set([...model.candidates.map(pair), ...labels.map(pair)]);
    const has = (rels) => new Set(rels.map(pair));
    const PA = has(arm), PR = has(rule), PL = has(labels);
    const d0 = [...pairs].map((p) => ({ id: p, arm: PA.has(p) === PL.has(p), rule: PR.has(p) === PL.has(p) }));

    const both = labels.filter((l) => target(A, lc(l.from)) === lc(l.to) && target(R, lc(l.from)) === lc(l.to));
    const setting = (field) => both.map((l) => ({ id: lc(l.from), arm: A.get(lc(l.from))[field] === l[field], rule: R.get(lc(l.from))[field] === l[field] }));
    return { d0, d1, d2: setting('direction'), d3: setting('active'), d4: setting('cardinality') };
}

export function score(labelsDoc, results, bar) {
    const byId = new Map(labelsDoc.models.map((m) => [m.id, m]));
    const out = { arms: {} };
    for (const armName of ['blend', 'jev', 'jevSure']) {
        const all = { d0: [], d1: [], d2: [], d3: [], d4: [] };
        for (const m of results.models) {
            const u = units(m, byId.get(m.id).labels, armName);
            for (const k of Object.keys(all)) all[k].push(...u[k].map((x) => ({ ...x, id: m.id + ':' + x.id })));
        }
        const decisions = Object.fromEntries(Object.entries(all).map(([k, v]) => [k, paired(v)]));
        for (const d of Object.values(decisions)) d.ships = ships(bar, d);
        out.arms[armName] = decisions;
    }
    const pro = out.arms.blend;
    out.live = pro.d1.ships;
    out.requests = results.requests;
    out.inputTokens = results.inputTokens;
    out.labels = results.labels;
    return out;
}

function pct(x) { return (100 * x).toFixed(1) + '%'; }

if (process.argv[1] && fileURLToPath(import.meta.url) === process.argv[1]) {
    const dir = process.env.JEV_REPLAY_OUT || join(homedir(), '.cache', 'jev-replay-relationships');
    const resultsFile = process.env.JEV_REPLAY_RESULTS || 'results.json';
    const results = JSON.parse(readFileSync(join(dir, resultsFile), 'utf8'));
    const labelsDoc = JSON.parse(readFileSync(join(here, results.labels || 'labels.json'), 'utf8'));
    const bar = JSON.parse(readFileSync(join(here, 'bar-v2.json'), 'utf8'));
    const s = score(labelsDoc, results, bar);
    const stem = resultsFile.replace(/\.json$/, '').replace(/^results/, 'score') + '-v2';
    writeFileSync(join(dir, stem + '.json'), JSON.stringify(s, null, 1));
    const names = { d0: 'D0 tables related', d1: 'D1 join', d2: 'D2 direction', d3: 'D3 active', d4: 'D4 cardinality' };
    const table = (arm) => [
        `| decision | units | arm | rule | gain | arm only right | rule only right | McNemar p | ships |`,
        `|---|---|---|---|---|---|---|---|---|`,
        ...Object.entries(s.arms[arm]).map(([k, d]) => `| ${names[k]} | ${d.n} | ${pct(d.arm)} | ${pct(d.rule)} | ${(100 * d.gain).toFixed(1)} | ${d.b} | ${d.c} | ${d.p.toFixed(4)} | ${d.ships ? 'yes' : 'no'} |`),
    ];
    const md = [
        `# relationship.join replay, bar v2 (${s.labels})`, ``, `Requests ${s.requests}, input tokens ${s.inputTokens}.`, ``,
        `## The Pro path: Jev when sure, else the rule`, ``, ...table('blend'), ``,
        `## For comparison: Jev's kept lines alone (sure or likely)`, ``, ...table('jev'), ``,
        `## For comparison: Jev's sure lines alone`, ``, ...table('jevSure'), ``,
        `Verdict: **${s.live ? 'SHIP: D1 join clears the bar, Jev decides' : 'the rule stays: D1 join does not clear the bar'}**`, ``,
    ].join('\n');
    writeFileSync(join(dir, stem + '.md'), md);
    console.log(md);
}
