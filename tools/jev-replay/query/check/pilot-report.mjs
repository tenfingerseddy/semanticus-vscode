// (Round 4's two-question gate; round 5 uses gate-report.mjs beside it.) The check's pilot report (the ruling "Jev data questions v2", 29 Sep, item 8): for each known reading that reached the
// check, what the check said, and whether the reading is right for its question by the label.
//   node pilot-report.mjs <labels.json> <results.json> [out.json]
// A results.json comes from the replay with JEV_REPLAY_PICKS (recorded parts, live check) or from a live run. A row's
// reading is the parts it built (also when the check stopped it: the answer carries them, and offers them as they are).
// A reading PASSES the check when the gap's top pick is "nothing" and the answers question is a yes at 0.85.
//   right      the reading is one the label accepts;   harmful   it is a reading the label does not accept (or the label declines).
// The stop rule: no harmful reading passes; at most 5% of right readings fall below the bar.
import { readFileSync, writeFileSync } from 'node:fs';
import { readingMatches } from '../score.mjs';

const BAR = 0.85;
const [labelsPath, resultsPath, outPath] = process.argv.slice(2);
const labels = JSON.parse(readFileSync(labelsPath, 'utf8'));
const results = JSON.parse(readFileSync(resultsPath, 'utf8'));
const byId = new Map(labels.items.map((i) => [i.id, i]));
const accepts = (label, parts) => label.accept !== 'decline' && parts && label.accept.some((e) => readingMatches(e, parts));

const rows = [];
for (const r of results.rows) {
    const label = byId.get(r.id);
    const picks = (r.picks ?? []).find((p) => p['q:check:gap']);
    if (!label || !picks) { if (label) rows.push({ id: r.id, model: r.model, words: r.words, reached: false, outcome: r.jev?.outcome ?? null, unsure: r.jev?.unsure ?? [] }); continue; }
    const gap = picks['q:check:gap'];
    const yesQ = picks['q:check:answers'];
    const pYes = yesQ ? (yesQ.probs?.yes ?? (yesQ.pick === 'yes' ? yesQ.confidence : 1 - yesQ.confidence)) : null;
    const pNothing = gap.probs?.nothing ?? (gap.pick === 'nothing' ? gap.confidence : 0);
    const passes = gap.pick === 'nothing' && pYes != null && pYes >= BAR;
    const runnerUp = Object.entries(gap.probs ?? {}).filter(([k]) => k !== 'nothing').sort((a, b) => b[1] - a[1])[0] ?? null;
    rows.push({
        id: r.id, model: r.model, words: r.words, reached: true, kind: accepts(label, r.jev?.parts) ? 'right' : 'harmful',
        statement: r.jev?.statement ?? null, stated: picks.stated_reading ?? null, reading: r.jev?.reading ?? null,
        gap: gap.pick, gapConfidence: gap.confidence, pNothing, runnerUp, pYes, passes, checkMs: r.checkMs, askOk: !!label.askOk,
        outcome: r.jev?.outcome ?? null,
    });
}
const reached = rows.filter((r) => r.reached);
const right = reached.filter((r) => r.kind === 'right');
const harmful = reached.filter((r) => r.kind === 'harmful');
const harmfulPassing = harmful.filter((r) => r.passes);
const rightBelow = right.filter((r) => !r.passes);
const median = (xs) => { const s = xs.filter((x) => x >= 0).sort((a, b) => a - b); return s.length ? s[Math.floor((s.length - 1) / 2)] : null; };
const out = {
    labels: labelsPath, results: resultsPath, bar: BAR, items: rows.length, reachedTheCheck: reached.length,
    right: right.length, harmful: harmful.length, harmfulPassing: harmfulPassing.map((r) => r.id),
    rightBelowBar: rightBelow.length, rightBelowBarPct: right.length ? +(100 * rightBelow.length / right.length).toFixed(1) : null,
    medianCheckMs: median(reached.map((r) => r.checkMs)),
    stopRule: { noHarmfulPasses: harmfulPassing.length === 0, atMost5PctRightBelow: right.length === 0 || rightBelow.length / right.length <= 0.05 },
    rows,
};
if (outPath) writeFileSync(outPath, JSON.stringify(out, null, 1));
console.log(`reached the check ${reached.length} of ${rows.length}: right ${right.length}, harmful ${harmful.length}`);
console.log(`harmful passing the bar: ${harmfulPassing.length} ${harmfulPassing.map((r) => r.id).join(' ')}`);
console.log(`right below the bar: ${rightBelow.length} of ${right.length} (${out.rightBelowBarPct}%) ${rightBelow.map((r) => r.id).join(' ')}`);
console.log(`median check time: ${out.medianCheckMs} ms; stop rule: ${JSON.stringify(out.stopRule)}`);
for (const r of reached) console.log(`${r.id.padEnd(9)} ${r.kind.padEnd(7)} ${r.passes ? 'PASS' : 'stop'} gap ${String(r.gap).padEnd(13)} (${r.gapConfidence}) P(nothing) ${r.pNothing} P(yes) ${r.pYes} | ${r.words}`);
