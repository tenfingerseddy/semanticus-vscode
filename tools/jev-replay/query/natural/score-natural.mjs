// DIAGNOSTIC / TUNING ONLY (r-jev-unsure, 28 Sep): score a natural-question replay where each label lists EVERY
// reasonable reading ("accept"). Uses score.mjs's own matching, unchanged, once per accepted reading.
//   node score-natural.mjs <labels.json> <results.json> [out.json]
import { readFileSync, writeFileSync } from 'node:fs';
import { readingMatches, mcnemarExact } from '../score.mjs';

const [labelsPath, resultsPath, outPath] = process.argv.slice(2);
const labels = JSON.parse(readFileSync(labelsPath, 'utf8'));
const results = JSON.parse(readFileSync(resultsPath, 'utf8'));
const byId = new Map(labels.items.map((i) => [i.id, i]));

const anyMatch = (accept, parts) => accept !== 'decline' && accept.some((e) => readingMatches(e, parts));

/** right, harmful (settled, would run, no reasonable reading), caught (a check refused it), unsure, missing. */
export function jevCat(label, jev) {
    if (!jev) return 'missing';
    const settled = ['built', 'answered', 'refused'].includes(jev.outcome);
    if (label.accept === 'decline') return !settled ? 'right' : jev.outcome === 'refused' ? 'caught' : 'harmful';
    if (!settled) return 'unsure';
    if (jev.outcome === 'refused') return 'caught';
    return anyMatch(label.accept, jev.parts) ? 'right' : 'harmful';
}
export function armCat(label, arm) {
    if (!arm) return 'missing';
    if (label.accept === 'decline') return arm.decided ? 'harmful' : 'right';
    if (!arm.decided) return 'unsure';
    return anyMatch(label.accept, arm.parts) ? 'right' : 'harmful';
}

const tally = () => ({ n: 0, right: 0, harmful: 0, unsure: 0, caught: 0, missing: 0, unsureAskOk: 0 });
const add = (t, c, label) => { t.n++; t[c]++; if (c === 'unsure' && label.askOk) t.unsureAskOk++; };
const arms = { jev: tally(), words: tally(), shortlist: tally() };
const byType = {}, byModel = {};
let onlyJev = 0, onlyBase = 0;
const rows = [];
// The best baseline is chosen the way score.mjs chooses it: most right, then fewer harmful.
const cats = results.rows.filter((r) => byId.has(r.id)).map((r) => {
    const label = byId.get(r.id);
    return { r, label, j: jevCat(label, r.jev), w: armCat(label, r.words_arm), s: armCat(label, r.shortlist_arm) };
});
for (const c of cats) { add(arms.jev, c.j, c.label); add(arms.words, c.w, c.label); add(arms.shortlist, c.s, c.label); }
const best = arms.shortlist.right > arms.words.right || (arms.shortlist.right === arms.words.right && arms.shortlist.harmful <= arms.words.harmful) ? 'shortlist' : 'words';
for (const { r, label, j, w, s } of cats) {
    const b = best === 'shortlist' ? s : w;
    if (j === 'right' && b !== 'right') onlyJev++;
    if (b === 'right' && j !== 'right') onlyBase++;
    for (const [bucket, key] of [[byType, label.type], [byModel, r.model]]) {
        bucket[key] ??= { jev: tally(), base: tally() };
        add(bucket[key].jev, j, label); add(bucket[key].base, b, label);
    }
    rows.push({ id: r.id, model: r.model, type: label.type, words: r.words, jev: j, base: b,
        understood: r.jev?.reading ?? null, unsure: r.jev?.unsure ?? [], outcome: r.jev?.outcome ?? null, askOk: !!label.askOk });
}
const out = { diagnostic: results.diagnostic ?? null, labels: labelsPath, ranUtc: results.ranUtc, requests: results.requests, inputTokens: results.inputTokens,
    arms, best, mcnemar: { onlyJevRight: onlyJev, onlyBaselineRight: onlyBase, p: mcnemarExact(onlyJev, onlyBase), note: 'for information only' },
    byType, byModel, rows };
if (outPath) writeFileSync(outPath, JSON.stringify(out, null, 1));
const f = (t) => `${t.right}/${t.n} right, ${t.harmful} harmful, ${t.unsure} unsure (${t.unsureAskOk} fair to ask)`;
console.log(`Jev: ${f(arms.jev)}\nwords: ${f(arms.words)}\nshortlist: ${f(arms.shortlist)}\nbest baseline: ${best}; only Jev ${onlyJev}, only baseline ${onlyBase}, McNemar p ${out.mcnemar.p.toFixed(4)}`);
for (const [k, v] of Object.entries(byModel)) console.log(`  model ${k}: Jev ${f(v.jev)} | base ${f(v.base)}`);
for (const [k, v] of Object.entries(byType)) console.log(`  type ${k}: Jev ${f(v.jev)} | base ${f(v.base)}`);
