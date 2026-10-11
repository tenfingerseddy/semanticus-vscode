// Round 3 of the date.table replay: three decisions scored apart, the Pro path (Jev when sure, else the rule) against
// the rule, with an exact McNemar test (round3-bar.json). Aggregate only.
//
//   node tools/jev-replay/datetable/score-round3.mjs [round3-results.json]

import { readFileSync, writeFileSync } from 'node:fs';
import { homedir } from 'node:os';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));

function choose(n, k) {
    let r = 1;
    for (let i = 1; i <= k; i++) r = (r * (n - k + i)) / i;
    return r;
}

/** Exact two-sided McNemar: b and c are the discordant pairs. */
export function mcnemar(b, c) {
    const n = b + c;
    if (n === 0) return 1;
    const k = Math.min(b, c);
    let tail = 0;
    for (let i = 0; i <= k; i++) tail += choose(n, i);
    return Math.min(1, (2 * tail) / 2 ** n);
}

/** One decision: rows of { key, jev, jevConfidence, rule }. */
/** A harmful table answer (kane-bar.json): a wrong existing table, or a new one when a good one exists. */
export function harmfulTable(answer, key) {
    if (answer == null) return false;
    if (answer.startsWith('table:')) return answer !== key;
    return answer === 'generate' && key !== 'generate';
}

export function decision(rows, bar, harm = null) {
    let pro = 0, rule = 0, jevRaw = 0, b = 0, c = 0, fromJev = 0, proHarm = 0, ruleHarm = 0;
    for (const r of rows) {
        const sure = r.jev != null && r.jevConfidence >= bar.confidenceBar;
        const p = sure ? r.jev : r.rule;
        if (sure) fromJev++;
        const pr = p === r.key, rr = r.rule === r.key;
        if (pr) pro++;
        if (rr) rule++;
        if (r.jev === r.key) jevRaw++;
        if (pr && !rr) b++;
        if (!pr && rr) c++;
        if (harm && harm(p, r.key)) proHarm++;
        if (harm && harm(r.rule, r.key)) ruleHarm++;
    }
    const n = rows.length;
    const share = (k) => (n === 0 ? 0 : Math.round((k / n) * 1000) / 1000);
    const gain = Math.round((share(pro) - share(rule)) * 1000) / 10;
    const p = mcnemar(b, c);
    return {
        n, proPath: pro, rule, jevRaw, fromJev,
        proShare: share(pro), ruleShare: share(rule), jevRawShare: share(jevRaw),
        gainPoints: gain, discordant: { proOnly: b, ruleOnly: c }, p: Math.round(p * 10000) / 10000,
        ship: gain >= bar.minGainPoints && p < bar.maxP,
        harmful: { proPath: proHarm, rule: ruleHarm },
        // Kane's switch-on bar (kane-bar.json, 26 September): beats the rule, not luck, no more harm. Size does not matter.
        kaneOn: pro > rule && p < (bar.kaneMaxP ?? 0.05) && proHarm <= ruleHarm,
    };
}

export function score(results, key, bar) {
    const byId = new Map(key.items.map((k) => [k.id, k]));
    const rows = results.rows.filter((r) => !byId.get(r.id).unverifiable && !byId.get(r.id).ambiguous);
    const need = rows.map((r) => ({ key: byId.get(r.id).needed, jev: r.jevNeeded, jevConfidence: r.jevNeededConfidence, rule: r.ruleNeeded }));
    const needed = rows.filter((r) => byId.get(r.id).needed);
    const tableKey = (k) => (k === 'generate' ? 'generate' : 'table:' + k);
    // The table decision is scored only where the engine offered a table: with none, a new one is the only answer and
    // is not asked, so it is nobody's decision.
    const offered = needed.filter((r) => (r.options ?? []).some((o) => o.startsWith('table:')));
    const table = offered.map((r) => ({ key: tableKey(byId.get(r.id).table), jev: r.jevTable, jevConfidence: r.jevTableConfidence, rule: r.ruleTable }));
    const mark = needed.map((r) => ({ key: true, jev: r.jevMark, jevConfidence: r.jevMarkConfidence, rule: true }));
    const d = { needed: decision(need, bar), table: decision(table, bar, harmfulTable), mark: decision(mark, bar) };
    return {
        point: results.point,
        scored: rows.length,
        apart: results.rows.length - rows.length,
        decisions: d,
        nameRuleTable: offered.filter((r) => r.nameRule === tableKey(byId.get(r.id).table)).length,
        undecided: results.rows.filter((r) => !r.decided).length,
        tokens: { input: results.rows.reduce((a, r) => a + (r.inputTokens ?? 0), 0), output: results.rows.reduce((a, r) => a + (r.outputTokens ?? 0), 0) },
        verdict: d.needed.ship && d.table.ship ? 'SHIP' : 'RULE',
        kaneVerdict: { needed: d.needed.kaneOn, table: d.table.kaneOn, mark: d.mark.kaneOn },
        markToJev: d.mark.ship,
    };
}

function markdown(s) {
    const row = (name, x) => `| ${name} | ${x.n} | ${x.proPath} (${x.proShare}) | ${x.rule} (${x.ruleShare}) | ${x.jevRaw} (${x.jevRawShare}) | ${x.fromJev} | ${x.gainPoints} | ${x.discordant.proOnly}/${x.discordant.ruleOnly} | ${x.p} | ${x.ship ? 'SHIP' : 'RULE'} |`;
    return [
        '# date.table replay, round 3',
        '',
        `Scored models: ${s.scored}; reported apart: ${s.apart}. Undecided asks: ${s.undecided}.`,
        '',
        '| decision | n | Pro path right | rule right | Jev alone right | answered by Jev | gain (points) | Pro only / rule only | McNemar p | verdict |',
        '|---|---|---|---|---|---|---|---|---|---|',
        row('needed', s.decisions.needed),
        row('table', s.decisions.table),
        row('mark', s.decisions.mark),
        '',
        `Name rule on the table decision: ${s.nameRuleTable} of ${s.decisions.table.n}.`,
        `Tokens in ${s.tokens.input}, out ${s.tokens.output}.`,
        '',
        `**Verdict: ${s.verdict}** (mark to Jev: ${s.markToJev ? 'yes' : 'no, the engine always marks'})`,
        '',
        `Harmful table answers: Pro path ${s.decisions.table.harmful.proPath}, rule ${s.decisions.table.harmful.rule}.`,
        `Under Kane's switch-on bar (kane-bar.json, applied after the run): needed ${s.kaneVerdict.needed ? 'ON' : 'off'}, table ${s.kaneVerdict.table ? 'ON' : 'off'}, mark ${s.kaneVerdict.mark ? 'ON' : 'off'}.`,
        '',
    ].join('\n');
}

if (process.argv[1] && fileURLToPath(import.meta.url) === process.argv[1]) {
    const file = process.argv[2] ?? join(homedir(), '.cache', 'jev-replay-datetable', 'round3-results.json');
    const s = score(JSON.parse(readFileSync(file, 'utf8')), JSON.parse(readFileSync(join(here, 'key.json'), 'utf8')),
        JSON.parse(readFileSync(join(here, 'round3-bar.json'), 'utf8')));
    writeFileSync(join(dirname(file), 'round3-score.json'), JSON.stringify(s, null, 1) + '\n');
    writeFileSync(join(dirname(file), 'round3-score.md'), markdown(s));
    process.stdout.write(markdown(s));
}
