// The gate's report (round 5 of the ruling "Jev data questions v2"): for each labelled reading that reached the check, whether
// the gate let it run or stopped it, against the label.
//   node gate-report.mjs <labels.json> <results.json> [out.json]
// A results.json comes from the replay with JEV_REPLAY_PICKS (recorded parts, live check). A reading REACHED the check when a
// q:check:gap answer was kept for it. It PASSED when the answer was not stopped by the check (built or answered).
//   right    the reading is one the label accepts;   harmful   it is not (or the label declines).
// The stop rule: no harmful reading passes, and at most 12% of right readings are stopped (needless asks).
import { readFileSync, writeFileSync } from 'node:fs';
import { readingMatches } from '../score.mjs';

const [labelsPath, resultsPath, outPath] = process.argv.slice(2);
const labels = JSON.parse(readFileSync(labelsPath, 'utf8'));
const results = JSON.parse(readFileSync(resultsPath, 'utf8'));
const byId = new Map(labels.items.map((i) => [i.id, i]));
const accepts = (label, parts) => label.accept !== 'decline' && parts && label.accept.some((e) => readingMatches(e, parts));
const pYesOf = (x) => (x.pick === 'yes' ? x.confidence : 1 - x.confidence);

const rows = [];
for (const r of results.rows) {
    const label = byId.get(r.id);
    if (!label) continue;
    const picks = r.picks ?? [];
    const gap = picks.find((p) => p['q:check:gap']);
    if (!gap) { rows.push({ id: r.id, model: r.model, words: r.words, reached: false, outcome: r.jev?.outcome ?? null }); continue; }
    const g = gap['q:check:gap'];
    const seconds = picks.filter((p) => p['q:check:real']).map((p) => ({ pYes: +pYesOf(p['q:check:real']).toFixed(3), stated: undefined }));
    const passes = r.jev?.outcome === 'answered' || r.jev?.outcome === 'built';
    const runnerUp = Object.entries(g.probs ?? {}).filter(([k]) => k !== 'nothing').sort((a, b) => b[1] - a[1])[0] ?? null;
    rows.push({
        id: r.id, model: r.model, words: r.words, reached: true, kind: accepts(label, r.jev?.parts) ? 'right' : 'harmful',
        gap: g.pick, gapConfidence: g.confidence, pNothing: g.probs?.nothing ?? 0, runnerUp, seconds, passes, checkMs: r.checkMs,
        outcome: r.jev?.outcome ?? null, statement: r.jev?.statement ?? null, note: r.jev?.note ?? null, unsure: r.jev?.unsure ?? [],
        stated: gap.stated_reading ?? null, askOk: !!label.askOk,
    });
}
const reached = rows.filter((r) => r.reached);
const right = reached.filter((r) => r.kind === 'right');
const harmful = reached.filter((r) => r.kind === 'harmful');
const harmfulPassing = harmful.filter((r) => r.passes);
const rightStopped = right.filter((r) => !r.passes);
const median = (xs) => { const s = xs.filter((x) => x >= 0).sort((a, b) => a - b); return s.length ? s[Math.floor((s.length - 1) / 2)] : null; };
const out = {
    labels: labelsPath, results: resultsPath, items: rows.length, reachedTheCheck: reached.length,
    right: right.length, harmful: harmful.length, harmfulPassing: harmfulPassing.map((r) => r.id),
    rightStopped: rightStopped.map((r) => r.id), rightStoppedCount: rightStopped.length,
    rightStoppedPct: right.length ? +(100 * rightStopped.length / right.length).toFixed(1) : null,
    secondOpinionCalls: reached.reduce((n, r) => n + r.seconds.length, 0),
    medianCheckMs: median(reached.map((r) => r.checkMs)),
    medianCheckMsFirstOnly: median(reached.filter((r) => r.seconds.length === 0).map((r) => r.checkMs)),
    rows,
};
if (outPath) writeFileSync(outPath, JSON.stringify(out, null, 1));
console.log(`reached the check ${reached.length} of ${rows.length}: right ${right.length}, harmful ${harmful.length}`);
console.log(`harmful passing: ${harmfulPassing.length} ${harmfulPassing.map((r) => r.id).join(' ')}`);
console.log(`right stopped: ${rightStopped.length} of ${right.length} (${out.rightStoppedPct}%) ${rightStopped.map((r) => r.id).join(' ')}`);
console.log(`second-opinion calls ${out.secondOpinionCalls}; median check ms ${out.medianCheckMs} (readings with no second opinion: ${out.medianCheckMsFirstOnly})`);
for (const r of reached) console.log(`${r.id.padEnd(9)} ${r.kind.padEnd(7)} ${r.passes ? 'pass' : 'STOP'} gap ${String(r.gap).padEnd(13)} (${r.gapConfidence}) P(nothing) ${r.pNothing} ${r.runnerUp ? r.runnerUp[0] + ' ' + r.runnerUp[1] : ''} 2nd ${JSON.stringify(r.seconds.map((s) => s.pYes))} | ${r.words}`);
