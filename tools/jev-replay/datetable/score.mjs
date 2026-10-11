// The date.table replay's arithmetic and verdict (T268, M4 point 3). Aggregate only: no model, table or column name
// is written, only ids of the labelled cases (which are the repository's own) and counts.
//
//   node tools/jev-replay/datetable/score.mjs [results.json] [bar file]
//
// Reads ~/.cache/jev-replay-datetable/results.json (written by Semanticus.Tests/JevDateTableReplay.cs) and the committed
// bar.json, and writes score.json and score.md beside the results.

import { readFileSync, writeFileSync } from 'node:fs';
import { homedir } from 'node:os';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));

/** Whether a choice (and its mark) is the labelled decision. */
export function right(row, choice, mark) {
    if (choice == null) return false;
    const ok = [row.label, ...(row.alsoOk ?? [])].map((x) => (x === 'generate' || x === 'none' ? x : 'table:' + x));
    if (!ok.includes(choice)) return false;
    if (choice === 'none') return true;
    return mark === row.labelMark;
}

/** A model is answerable when its labelled decision is one the engine offered. */
export function answerable(row) {
    if (row.label === 'generate' || row.label === 'none') return true;
    return (row.options ?? []).includes('table:' + row.label);
}

export function score(results, bar) {
    const rows = results.rows ?? [];
    const scored = rows.filter(answerable);
    const apart = rows.filter((r) => !answerable(r));
    const n = scored.length;
    const jev = scored.filter((r) => r.decided && right(r, r.jev, r.jevMark)).length;
    const shipped = scored.filter((r) => right(r, r.shipped, true)).length;
    const byName = scored.filter((r) => right(r, r.nameRule, true)).length;
    // A kept line that marks an existing table other than the labelled one: the error that costs a person something.
    const harmful = scored.filter((r) => r.line && r.line.kept && r.line.op === 'mark_date_table'
        && !right(r, 'table:' + String(r.line.target).replace(/^table:/, ''), true)).length;
    const undecided = rows.filter((r) => !r.decided).length;
    const sure = scored.filter((r) => r.decided && r.jevConfidence >= bar.confidenceBar);
    const sureRight = sure.filter((r) => right(r, r.jev, r.jevMark)).length;
    const measured = scored.filter((r) => r.measured);
    const files = scored.filter((r) => !r.measured);
    const share = (k, of) => (of === 0 ? 0 : Math.round((k / of) * 1000) / 1000);

    const checks = {
        jevMin: share(jev, n) >= bar.jevMin,
        beatShippedRule: !bar.beatShippedRule || jev > shipped,
        notBelowNameRule: !bar.notBelowNameRule || jev >= byName,
        harmful: harmful <= bar.harmfulMax,
    };
    const verdict = Object.values(checks).every(Boolean) ? 'SHIP' : 'RULE';
    return {
        point: results.point,
        answerable: n,
        unanswerable: apart.map((r) => r.id),
        jev: { right: jev, share: share(jev, n) },
        shippedRule: { right: shipped, share: share(shipped, n) },
        nameRule: { right: byName, share: share(byName, n) },
        jevOnMeasured: { right: measured.filter((r) => r.decided && right(r, r.jev, r.jevMark)).length, of: measured.length },
        jevOnFiles: { right: files.filter((r) => r.decided && right(r, r.jev, r.jevMark)).length, of: files.length },
        nameRuleOnFiles: { right: files.filter((r) => right(r, r.nameRule, true)).length, of: files.length },
        sure: { answers: sure.length, right: sureRight },
        harmful,
        undecided,
        wrong: scored.filter((r) => !(r.decided && right(r, r.jev, r.jevMark))).map((r) => ({ id: r.id, label: r.label, jev: r.jev, mark: r.jevMark, confidence: r.jevConfidence })),
        ruleWrong: scored.filter((r) => !right(r, r.nameRule, true)).map((r) => r.id),
        shippedWrong: scored.filter((r) => !right(r, r.shipped, true)).map((r) => r.id),
        tokens: { input: rows.reduce((a, r) => a + (r.inputTokens ?? 0), 0), output: rows.reduce((a, r) => a + (r.outputTokens ?? 0), 0) },
        msMean: rows.length ? Math.round(rows.reduce((a, r) => a + (r.ms ?? 0), 0) / rows.length) : 0,
        checks,
        verdict,
    };
}

function markdown(s) {
    const lines = [
        '# date.table replay score',
        '',
        `Answerable models: ${s.answerable}. Unanswerable, reported apart: ${s.unanswerable.length} (${s.unanswerable.join(', ') || 'none'}).`,
        '',
        '| who | right | share |',
        '|---|---|---|',
        `| Jev | ${s.jev.right} | ${s.jev.share} |`,
        `| shipped rule | ${s.shippedRule.right} | ${s.shippedRule.share} |`,
        `| name rule | ${s.nameRule.right} | ${s.nameRule.share} |`,
        '',
        `Jev with data: ${s.jevOnMeasured.right} of ${s.jevOnMeasured.of}. Jev from files: ${s.jevOnFiles.right} of ${s.jevOnFiles.of} (name rule ${s.nameRuleOnFiles.right}).`,
        `Sure answers: ${s.sure.answers}, right ${s.sure.right}. Harmful kept marks: ${s.harmful}. Undecided asks: ${s.undecided}.`,
        `Tokens in ${s.tokens.input}, out ${s.tokens.output}. Mean time per model ${s.msMean} ms.`,
        '',
        `Checks: ${Object.entries(s.checks).map(([k, v]) => k + ' ' + (v ? 'pass' : 'fail')).join(', ')}.`,
        '',
        `**Verdict: ${s.verdict}**`,
        '',
    ];
    return lines.join('\n');
}

if (process.argv[1] && fileURLToPath(import.meta.url) === process.argv[1]) {
    const file = process.argv[2] ?? join(homedir(), '.cache', 'jev-replay-datetable', 'results.json');
    const results = JSON.parse(readFileSync(file, 'utf8'));
    const bar = JSON.parse(readFileSync(join(here, process.argv[3] ?? (results.set === 'heldout-labels.json' ? 'heldout-bar.json' : 'bar.json')), 'utf8'));
    const s = score(results, bar);
    const stem = results.set === 'heldout-labels.json' ? 'heldout-score' : 'score';
    writeFileSync(join(dirname(file), stem + '.json'), JSON.stringify(s, null, 1) + '\n');
    writeFileSync(join(dirname(file), stem + '.md'), markdown(s));
    process.stdout.write(markdown(s));
}
