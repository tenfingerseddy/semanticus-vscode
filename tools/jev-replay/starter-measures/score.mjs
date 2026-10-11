// Score the starter measures replay: Jev with the product's own facts against the engine's rule.
//
//   node tools/jev-replay/starter-measures/score.mjs [--dir <cache dir>]
//
// Reads starter-facts.json and raw-answers.json from the private cache dir and writes score.json and score.md there.
// Both are AGGREGATE: counts, shares and option ids. No model, table or column name is written, so the numbers can
// be copied into docs/redesign/jev-starter-measures-replay.md as they stand.
//
// THE VERDICT BAR is the columns replay's, fixed before this run (docs/redesign/jev-columns-replay.md, Method):
//   SHIP       Jev beats the rule by at least 5 points on at least 50 scored columns, and an exact McNemar test on
//              the disagreements gives p below 0.05. Jev then decides for Pro (StarterMeasuresFamily.JevDecides).
//   RULE       Jev does not beat the rule. The rule decides.
//   MORE DATA  anything else, and always when one label covers 95% or more of the scored columns (a constant answer
//              would score that well, so the row tests no judgement). The rule decides until there is more data.
import { readFileSync, writeFileSync } from 'node:fs';
import { homedir } from 'node:os';
import { join } from 'node:path';
import { band, factSideRule, isRight, mcnemar } from './starter.mjs';

export function score(facts, raw) {
  const answers = new Map((raw ?? []).filter(Boolean).map((r) => [r.id, r]));
  const rows = [];
  const counts = { columns: 0, other: 0, unanswered: 0 };
  for (const m of facts.models ?? []) {
    m.columns.forEach((c, i) => {
      counts.columns++;
      if (c.label.kind === 'other') { counts.other++; return; }
      const a = answers.get(`${m.model}#${i}`);
      const ans = a?.answers?.aggregation;
      if (!a || a.error || !ans || typeof ans.choice !== 'string') { counts.unanswered++; return; }
      rows.push({ label: c.label, rule: c.rule, jev: ans.choice, confidence: Number(ans.confidence ?? 0) });
    });
  }

  // The rule on every column that has a plain answer, whether or not Jev answered it. This half needs no call,
  // so it stands on its own when the Jev arm could not run.
  const all = (facts.models ?? []).flatMap((m) => m.columns).filter((c) => c.label.kind !== 'other');
  const allPct = (x) => (all.length === 0 ? 0 : Math.round((x / all.length) * 1000) / 10);
  const allLabels = {};
  for (const c of all) { const k = c.label.kind === 'plain' ? c.label.options.join('+') : c.label.kind; allLabels[k] = (allLabels[k] ?? 0) + 1; }
  const ruleOnly = {
    scored: all.length,
    accuracy: allPct(all.filter((c) => isRight(c.rule, c.label)).length),
    needsAMeasure: allPct(all.filter((c) => (c.rule !== 'none') === (c.label.kind === 'plain')).length),
    mostCommonLabel: allPct(Math.max(0, ...Object.values(allLabels))),
    labels: allLabels,
    ruleSaysNone: allPct(all.filter((c) => c.rule === 'none').length),
    // The fact-side rule on the same columns, from the cached facts, so the two rules are compared like for like.
    factSide: allPct(all.filter((c) => c.state && isRight(factSideRule(c.state.column_facts.is_key, c.state.column_facts.relationship_role), c.label)).length),
  };

  const n = rows.length;
  const pct = (x) => (n === 0 ? 0 : Math.round((x / n) * 1000) / 10);
  const labelKey = (l) => (l.kind === 'plain' ? l.options.join('+') : l.kind);
  const labels = {};
  for (const r of rows) labels[labelKey(r.label)] = (labels[labelKey(r.label)] ?? 0) + 1;
  const topShare = n === 0 ? 0 : Math.max(...Object.values(labels)) / n;

  const jevRight = rows.filter((r) => isRight(r.jev, r.label)).length;
  const ruleRight = rows.filter((r) => isRight(r.rule, r.label)).length;
  const mostCommon = Object.entries(labels).sort((a, b) => b[1] - a[1])[0]?.[1] ?? 0;
  const b1 = rows.filter((r) => isRight(r.jev, r.label) && !isRight(r.rule, r.label)).length;
  const c1 = rows.filter((r) => !isRight(r.jev, r.label) && isRight(r.rule, r.label)).length;
  const p = mcnemar(b1, c1);

  // The yes-or-no half: does the column need a measure at all.
  const needs = (l) => l.kind === 'plain';
  const jevNeedRight = rows.filter((r) => (r.jev !== 'none') === needs(r.label)).length;
  const ruleNeedRight = rows.filter((r) => (r.rule !== 'none') === needs(r.label)).length;

  const calibration = {};
  for (const r of rows) {
    const k = band(r.confidence);
    calibration[k] ??= { n: 0, right: 0 };
    calibration[k].n++;
    if (isRight(r.jev, r.label)) calibration[k].right++;
  }
  for (const k of Object.keys(calibration)) {
    calibration[k].share = pct(calibration[k].n);
    calibration[k].accuracy = Math.round((calibration[k].right / calibration[k].n) * 1000) / 10;
  }

  const confusion = {};
  for (const r of rows) {
    if (isRight(r.jev, r.label)) continue;
    const k = `${labelKey(r.label)} answered ${r.jev}`;
    confusion[k] = (confusion[k] ?? 0) + 1;
  }

  const jevPct = pct(jevRight), rulePct = pct(ruleRight);
  let verdict;
  if (n > 0 && topShare >= 0.95) verdict = 'MORE DATA';
  else if (jevPct - rulePct >= 5 && n >= 50 && p < 0.05) verdict = 'SHIP';
  else if (jevPct <= rulePct) verdict = 'RULE';
  else verdict = 'MORE DATA';

  return {
    counts: { ...counts, scored: n },
    labels,
    topLabelShare: Math.round(topShare * 1000) / 10,
    accuracy: { jev: jevPct, rule: rulePct, mostCommonLabel: pct(mostCommon) },
    needsAMeasure: { jev: pct(jevNeedRight), rule: pct(ruleNeedRight) },
    disagreements: { jevRightRuleWrong: b1, ruleRightJevWrong: c1, mcnemarP: Number(p.toPrecision(3)) },
    calibration,
    confusion: Object.fromEntries(Object.entries(confusion).sort((a, b) => b[1] - a[1]).slice(0, 12)),
    verdict: n === 0 ? 'NOT RUN' : verdict,
    ruleOnly,
  };
}

function markdown(s) {
  const lines = [
    `scored ${s.counts.scored} of ${s.counts.columns} candidate columns (other ${s.counts.other}, unanswered ${s.counts.unanswered})`,
    '',
    '| | accuracy | needs a measure |',
    '|---|---|---|',
    `| Jev with facts | ${s.accuracy.jev}% | ${s.needsAMeasure.jev}% |`,
    `| rule | ${s.accuracy.rule}% | ${s.needsAMeasure.rule}% |`,
    `| most common label | ${s.accuracy.mostCommonLabel}% | |`,
    '',
    `disagreements: Jev right and rule wrong ${s.disagreements.jevRightRuleWrong}, the other way ${s.disagreements.ruleRightJevWrong}, exact McNemar p ${s.disagreements.mcnemarP}`,
    '',
    '| band | share | right |', '|---|---|---|',
    ...['below 0.6', '0.6 to 0.85', 'above 0.85'].map((k) => `| ${k} | ${s.calibration[k]?.share ?? 0}% | ${s.calibration[k]?.accuracy ?? '-'}% |`),
    '',
    `labels: ${JSON.stringify(s.labels)} (top label ${s.topLabelShare}%)`,
    `misses: ${JSON.stringify(s.confusion)}`,
    '',
    `verdict: ${s.verdict}`,
    '',
    `rule on every column with a plain answer: ${s.ruleOnly.scored} columns, right ${s.ruleOnly.accuracy}%, needs a measure right ${s.ruleOnly.needsAMeasure}%, most common label ${s.ruleOnly.mostCommonLabel}%, rule says none on ${s.ruleOnly.ruleSaysNone}%`,
    `fact-side rule (not a key, not on a one side: sum; else none) on the same columns: right ${s.ruleOnly.factSide}%`,
    `labels: ${JSON.stringify(s.ruleOnly.labels)}`,
  ];
  return lines.join('\n') + '\n';
}

if (import.meta.url === `file://${process.argv[1]}`) {
  const i = process.argv.indexOf('--dir');
  const dir = i > 0 ? process.argv[i + 1] : join(homedir(), '.cache', 'jev-replay-starter');
  const facts = JSON.parse(readFileSync(join(dir, 'starter-facts.json'), 'utf8'));
  const raw = JSON.parse(readFileSync(join(dir, 'raw-answers.json'), 'utf8'));
  const s = score(facts, raw);
  writeFileSync(join(dir, 'score.json'), JSON.stringify(s, null, 2));
  writeFileSync(join(dir, 'score.md'), markdown(s));
  process.stdout.write(markdown(s));
}
