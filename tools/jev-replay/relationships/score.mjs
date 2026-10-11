// Score the relationship.join replay: aggregate numbers and the verdict against bar.json.
//
// Reads ~/.cache/jev-replay-relationships/results.json (JEV_REPLAY_OUT overrides the folder), written by
// Semanticus.Tests/JevRelationshipReplay.cs, and labels.json beside this file. Writes score.json and score.md beside
// the results. Aggregate numbers only; names appear only in the per-model table, and every name is a placeholder or
// the vendored sample's.

import { readFileSync, writeFileSync } from 'node:fs';
import { homedir } from 'node:os';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));

export function key(r) { return (r.from + '>' + r.to).toLowerCase(); }
export function exactKey(r) {
    return [key(r), r.active ? 'a' : 'i', r.direction, r.cardinality].join('|');
}

/** Precision, recall and F1 of a proposal list against the labels, on joins and on exact relationships. */
export function measure(labels, proposals) {
    const lj = new Set(labels.map(key)), le = new Set(labels.map(exactKey));
    const joinHits = proposals.filter((p) => lj.has(key(p))).length;
    const exactHits = proposals.filter((p) => le.has(exactKey(p))).length;
    const f = (hits) => {
        const precision = proposals.length ? hits / proposals.length : 0;
        const recall = labels.length ? hits / labels.length : 0;
        return { hits, precision, recall, f1: precision + recall ? (2 * precision * recall) / (precision + recall) : 0 };
    };
    return { labels: labels.length, proposed: proposals.length, join: f(joinHits), exact: f(exactHits) };
}

export function verdict(bar, jev, rule) {
    const reasons = [];
    if (jev.exact.f1 < bar.exactF1Min) reasons.push(`Jev exact F1 ${jev.exact.f1.toFixed(3)} is below ${bar.exactF1Min}`);
    if (jev.join.precision < bar.joinPrecisionMin) reasons.push(`Jev join precision ${jev.join.precision.toFixed(3)} is below ${bar.joinPrecisionMin}`);
    if (bar.mustBeatRule && !(jev.exact.f1 > rule.exact.f1)) reasons.push(`Jev exact F1 ${jev.exact.f1.toFixed(3)} does not beat the rule's ${rule.exact.f1.toFixed(3)}`);
    return { pass: reasons.length === 0, reasons };
}

export function score(labelsDoc, results, bar) {
    const byId = new Map(labelsDoc.models.map((m) => [m.id, m]));
    const all = { labels: [], jev: [], jevSure: [], rule: [], asked: 0, candidateHits: 0 };
    const perModel = [];
    for (const r of results.models) {
        const labels = byId.get(r.id).labels;
        const jev = r.jev.filter((p) => p.kept);
        const jevSure = r.jev.filter((p) => p.word === 'sure');
        const tag = (xs) => xs.map((x) => ({ ...x, from: r.id + ':' + x.from, to: r.id + ':' + x.to }));
        all.labels.push(...tag(labels)); all.jev.push(...tag(jev)); all.jevSure.push(...tag(jevSure)); all.rule.push(...tag(r.rule));
        const listed = new Set((r.candidates || []).map((c) => (c.from + '>' + c.to).toLowerCase()));
        const candidateHits = labels.filter((l) => listed.has(key(l))).length;
        all.candidateHits += candidateHits;
        all.asked += r.asked;
        perModel.push({ id: r.id, labels: labels.length, asked: r.asked, candidateRecall: labels.length ? candidateHits / labels.length : 0,
            jev: measure(labels, jev), rule: measure(labels, r.rule) });
    }
    const jev = measure(all.labels, all.jev);
    const rule = measure(all.labels, all.rule);
    const jevSure = measure(all.labels, all.jevSure);
    // Computed after, not the bar: Jev where it answered sure, the rule for every other column.
    const sureFrom = new Set(all.jevSure.map((p) => p.from.toLowerCase()));
    const blend = measure(all.labels, all.jevSure.concat(all.rule.filter((p) => !sureFrom.has(p.from.toLowerCase()))));
    return {
        point: labelsDoc.point, ranUtc: results.ranUtc, requests: results.requests, inputTokens: results.inputTokens,
        asked: all.asked, candidateRecall: all.labels.length ? all.candidateHits / all.labels.length : 0,
        jev, jevSure, rule, blend, perModel, verdict: verdict(bar, jev, rule),
    };
}

function pct(x) { return (100 * x).toFixed(1) + '%'; }

if (process.argv[1] && fileURLToPath(import.meta.url) === process.argv[1]) {
    const dir = process.env.JEV_REPLAY_OUT || join(homedir(), '.cache', 'jev-replay-relationships');
    const resultsFile = process.env.JEV_REPLAY_RESULTS || 'results.json';
    const results = JSON.parse(readFileSync(join(dir, resultsFile), 'utf8'));
    const labelsDoc = JSON.parse(readFileSync(join(here, results.labels || 'labels.json'), 'utf8'));
    const bar = JSON.parse(readFileSync(join(here, 'bar.json'), 'utf8'));
    const s = score(labelsDoc, results, bar);
    const stem = resultsFile.replace(/\.json$/, '').replace(/^results/, 'score');
    writeFileSync(join(dir, stem + '.json'), JSON.stringify(s, null, 1));
    const row = (name, m) => `| ${name} | ${m.proposed} | ${m.join.hits} | ${pct(m.join.precision)} | ${pct(m.join.recall)} | ${m.exact.hits} | ${pct(m.exact.precision)} | ${pct(m.exact.recall)} | ${m.exact.f1.toFixed(3)} |`;
    const md = [
        `# relationship.join replay score (${results.labels || 'labels.json'})`, ``,
        `Labelled relationships: ${s.jev.labels}. Columns asked: ${s.asked}. Candidate recall (label's column asked with its key listed): ${pct(s.candidateRecall)}. Requests: ${s.requests}. Input tokens: ${s.inputTokens}.`, ``,
        `| arm | proposed | joins right | join precision | join recall | exact right | exact precision | exact recall | exact F1 |`,
        `|---|---|---|---|---|---|---|---|---|`,
        row('Jev (kept: sure or likely)', s.jev), row('Jev (sure only)', s.jevSure), row('name rule', s.rule), row('Jev when sure, else rule (after, not the bar)', s.blend), ``,
        `| model | labels | asked | candidate recall | Jev exact F1 | rule exact F1 | Jev join precision | rule join precision |`,
        `|---|---|---|---|---|---|---|---|`,
        ...s.perModel.map((m) => `| ${m.id} | ${m.labels} | ${m.asked} | ${pct(m.candidateRecall)} | ${m.jev.exact.f1.toFixed(3)} | ${m.rule.exact.f1.toFixed(3)} | ${pct(m.jev.join.precision)} | ${pct(m.rule.join.precision)} |`),
        ``, `Verdict: **${s.verdict.pass ? 'PASS, Jev decides' : 'FAIL, the rule stays'}**`, ...s.verdict.reasons.map((r) => `- ${r}`), ``,
    ].join('\n');
    writeFileSync(join(dir, stem + '.md'), md);
    console.log(md);
}
