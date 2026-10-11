// Score the ai.schema replay (T268, M4 point 5: what Copilot sees): aggregate numbers and the verdict against the bar in
// bar.json, which is Kane's switch-on bar of 26 September (version 3, recorded as set after run 2).
//
//   node tools/jev-replay/ai-schema/score.mjs [results.json]
//
// Reads the raw results the live replay wrote OUTSIDE the repository (default ~/.cache/jev-replay-ai-schema/results.json),
// the labels and the bar beside this file, and writes score.json and score.md next to the results. Aggregate numbers
// only: counts and shares, never an object name.
import { readFileSync, writeFileSync } from 'node:fs';
import { homedir } from 'node:os';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
export const LABELS = JSON.parse(readFileSync(join(here, 'labels.json'), 'utf8'));
export const BAR = JSON.parse(readFileSync(join(here, 'bar.json'), 'utf8'));

/** Exact two-sided McNemar p on b and c discordant pairs (binomial, p = 0.5). */
export function mcnemar(b, c) {
  const n = b + c;
  if (n === 0) return 1;
  const k = Math.min(b, c);
  let tail = 0;
  let coef = 1;
  for (let i = 0; i <= n; i++) {
    if (i > 0) coef = (coef * (n - i + 1)) / i;
    if (i <= k) tail += coef;
  }
  return Math.min(1, (2 * tail) / 2 ** n);
}

/** Confusion counts and shares for one arm, exclude as the positive class. */
export function confusion(pairs) {
  let tp = 0, fp = 0, fn = 0, tn = 0;
  for (const [want, got] of pairs) {
    if (want === 'exclude' && got === 'exclude') tp++;
    else if (want === 'include' && got === 'exclude') fp++;
    else if (want === 'exclude') fn++;
    else tn++;
  }
  const recall = tp + fn ? tp / (tp + fn) : 0;
  const specificity = tn + fp ? tn / (tn + fp) : 0;
  return {
    tp, fp, fn, tn,
    accuracy: (tp + tn) / Math.max(1, tp + fp + fn + tn),
    precision: tp + fp ? tp / (tp + fp) : null,
    recall,
    specificity,
    balancedAccuracy: (recall + specificity) / 2,
  };
}

const r3 = (x) => (x == null ? null : Math.round(x * 1000) / 1000);

/** Score one results file against the labels. Pure, so the self-test can feed it a fixture. */
export function score(results, labels = LABELS, bar = BAR) {
  const rows = [];
  let answered = 0, asked = 0, jevRawRight = 0;
  const bands = { unsure: [0, 0], likely: [0, 0], sure: [0, 0] };
  const byModel = {};
  for (const model of results.models ?? []) {
    const lab = labels.models.find((m) => m.id === model.id)?.labels ?? {};
    for (const row of model.rows ?? []) {
      asked++;
      if (row.jevChoice) answered++;
      const want = lab[row.ref];
      if (want !== 'include' && want !== 'exclude') continue;
      rows.push({ model: model.id, want, pro: row.pro, jev: row.jev, jevAll: row.jevAllLines, rule: row.rule, includeAll: 'include' });
      if (row.jevChoice === want) jevRawRight++;
      if (row.jevChoice) {
        const c = row.jevConfidence ?? 0;
        const band = c >= (results.confidenceBar ?? 0.85) ? 'sure' : c >= 0.6 ? 'likely' : 'unsure';
        bands[band][0]++;
        if (row.jevChoice === want) bands[band][1]++;
      }
      (byModel[model.id] ??= { rows: 0, exclude: 0 }).rows++;
      if (want === 'exclude') byModel[model.id].exclude++;
    }
  }
  const arm = (k) => confusion(rows.map((r) => [r.want, r[k]]));
  const pro = arm('pro'), jev = arm('jev'), jevAll = arm('jevAll'), rule = arm('rule'), includeAll = arm('includeAll');
  let b = 0, c = 0;
  for (const r of rows) {
    const pr = r.pro === r.want, rr = r.rule === r.want;
    if (pr && !rr) b++;
    if (!pr && rr) c++;
  }
  const p = mcnemar(b, c);
  const excludeLabels = rows.filter((r) => r.want === 'exclude').length;
  const gain = pro.accuracy - rule.accuracy;
  // Kane's switch-on bar (bar.json version 3): beat the rule, not by luck, with no more harmful mistakes (wrong
  // exclusions) than the rule. The size of the win does not matter.
  const harmful = pro.fp - rule.fp;
  let verdict;
  if (gain <= 0 || harmful > bar.maxHarmfulVersusRule) verdict = 'RULE';
  else if (rows.length >= bar.minScoredRows && excludeLabels >= bar.minExcludeLabels && p < bar.maxMcNemarP) verdict = 'SHIP';
  else verdict = 'MORE DATA';
  const shape = (x) => Object.fromEntries(Object.entries(x).map(([k, v]) => [k, typeof v === 'number' && !Number.isInteger(v) ? r3(v) : v]));
  return {
    verdict,
    asked, answered, scoredRows: rows.length, excludeLabels,
    requests: results.requests ?? null, inputTokens: results.inputTokens ?? null,
    jevRawPickAccuracy: r3(jevRawRight / Math.max(1, rows.length)),
    pro: shape(pro), jev: shape(jev), jevAllLinesKept: shape(jevAll), rule: shape(rule), includeAll: shape(includeAll),
    accuracyGain: r3(gain),
    wrongExclusions: { pro: pro.fp, rule: rule.fp },
    mcnemar: { proOnlyRight: b, ruleOnlyRight: c, p: r3(p) },
    calibration: Object.fromEntries(Object.entries(bands).map(([k, [n, right]]) => [k, { n, right: r3(n ? right / n : null) }])),
    byModel,
  };
}

function md(s) {
  const line = (name, x) => `| ${name} | ${x.accuracy} | ${x.balancedAccuracy} | ${x.precision ?? '-'} | ${x.recall} | ${x.specificity} | ${x.tp}/${x.fp}/${x.fn}/${x.tn} |`;
  return [
    `# ai.schema replay score`, '',
    `Verdict: **${s.verdict}**. Scored rows ${s.scoredRows} (exclude labels ${s.excludeLabels}); asked ${s.asked}, answered ${s.answered}; requests ${s.requests}, input tokens ${s.inputTokens}.`, '',
    '| arm | accuracy | balanced accuracy | exclude precision | exclude recall | include kept | tp/fp/fn/tn |', '|---|---|---|---|---|---|---|',
    line('Pro path: Jev when sure, else the rule', s.pro), line('Jev as it arrives (sure and likely kept)', s.jev), line('Jev, every line kept', s.jevAllLinesKept),
    line("today's rules", s.rule), line('include everything', s.includeAll), '',
    `Accuracy gain over the rule ${s.accuracyGain}. McNemar: Pro path only right ${s.mcnemar.proOnlyRight}, rule only right ${s.mcnemar.ruleOnlyRight}, p ${s.mcnemar.p}. Wrong exclusions: Pro path ${s.wrongExclusions.pro}, rule ${s.wrongExclusions.rule}.`,
    `Bar: version ${BAR.version}${BAR.committedBeforeFirstRun ? '' : `, set after run ${BAR.setAfterRun}`}.`,
    `Jev raw pick accuracy ${s.jevRawPickAccuracy}. Calibration: ${Object.entries(s.calibration).map(([k, v]) => `${k} n ${v.n} right ${v.right}`).join('; ')}.`, '',
  ].join('\n');
}

if (process.argv[1] && fileURLToPath(import.meta.url) === process.argv[1]) {
  const file = process.argv[2] ?? join(homedir(), '.cache', 'jev-replay-ai-schema', 'results.json');
  const s = score(JSON.parse(readFileSync(file, 'utf8')));
  writeFileSync(join(dirname(file), 'score.json'), JSON.stringify(s, null, 1));
  writeFileSync(join(dirname(file), 'score.md'), md(s));
  console.log(md(s));
}
