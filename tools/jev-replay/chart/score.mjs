// Score a queries.chart_kind replay against labels.json and bar.json. Aggregate numbers only. Plain Node, no packages.
//
//   node tools/jev-replay/chart/score.mjs                 the rules arm alone: the baseline, offline
//   node tools/jev-replay/chart/score.mjs <results.json>  a Jev run against the bar
//
// A results file is { "arm": "jev", "picks": { "<item id>": { "kind": "line", "by": "jev" } } }. An item with no pick,
// or picked by "rules", is scored as the rules' pick, because that is what the product shows.

import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const repo = join(here, '..', '..', '..');
const { chartRulePick } = await import(pathToFileURL(join(repo, 'Semanticus.VSCode', 'webview', 'src', 'chartpick.mjs')).href);

export { chartRulePick };

export function loadJson(name) { return JSON.parse(readFileSync(join(here, name), 'utf8')); }

/** Exact two-sided McNemar on the discordant counts b and c: the binomial test of min(b, c) at p = 0.5. */
export function mcnemarExact(b, c) {
  const n = b + c;
  if (n === 0) return 1;
  const k = Math.min(b, c);
  let tail = 0;
  let term = Math.pow(0.5, n);   // C(n, 0) / 2^n
  for (let i = 0; i <= k; i++) {
    tail += term;
    term = term * (n - i) / (i + 1);
  }
  return Math.min(1, 2 * tail);
}

/** The rules' pick for every item, from chartpick.mjs itself. */
export function rulesPicks(labels) {
  const picks = {};
  for (const it of labels.items) picks[it.id] = chartRulePick(it.shape).pick;
  return picks;
}

/** Score one arm's picks: how many acceptable, how many harmful. */
export function scoreArm(labels, picks) {
  let acceptable = 0, harmful = 0;
  const right = {};
  for (const it of labels.items) {
    const k = picks[it.id];
    right[it.id] = it.acceptable.includes(k);
    if (right[it.id]) acceptable++;
    if (it.harmful.includes(k)) harmful++;
  }
  return { items: labels.items.length, acceptable, rate: acceptable / labels.items.length, harmful, right };
}

/** The Jev arm's picks as the product would show them: Jev's kind when it picked, else the rules'. */
export function shownPicks(labels, results, rules) {
  const picks = {};
  for (const it of labels.items) {
    const r = results?.picks?.[it.id];
    picks[it.id] = r && r.by === 'jev' && r.kind ? r.kind : rules[it.id];
  }
  return picks;
}

export function verdict(labels, bar, results) {
  const rules = rulesPicks(labels);
  const r = scoreArm(labels, rules);
  const j = scoreArm(labels, shownPicks(labels, results, rules));
  let b = 0, c = 0;
  for (const it of labels.items) {
    if (j.right[it.id] && !r.right[it.id]) b++;
    if (r.right[it.id] && !j.right[it.id]) c++;
  }
  const p = mcnemarExact(b, c);
  const beats = j.acceptable > r.acceptable;
  const significant = p < bar.conditions.mcnemarAlpha;
  const harmOk = j.harmful <= r.harmful;
  return {
    rules: { acceptable: r.acceptable, rate: r.rate, harmful: r.harmful },
    jev: { acceptable: j.acceptable, rate: j.rate, harmful: j.harmful },
    discordant: { jevOnly: b, rulesOnly: c }, p,
    conditions: { beatsRules: beats, significant, harmfulNotAboveRules: harmOk },
    passes: beats && significant && harmOk,
  };
}

function pct(x) { return (100 * x).toFixed(1) + '%'; }

if (import.meta.url === pathToFileURL(process.argv[1] ?? '').href) {
  const labels = loadJson('labels.json');
  const bar = loadJson('bar.json');
  const rules = scoreArm(labels, rulesPicks(labels));
  console.log(`queries.chart_kind replay: ${labels.items.length} items, bar version ${bar.version}`);
  console.log(`rules arm: acceptable ${rules.acceptable}/${rules.items} (${pct(rules.rate)}), harmful ${rules.harmful}`);
  const file = process.argv[2];
  if (!file) {
    console.log('no Jev results given: baseline only. The point stays off.');
  } else {
    const v = verdict(labels, bar, JSON.parse(readFileSync(file, 'utf8')));
    console.log(`jev arm:   acceptable ${v.jev.acceptable}/${labels.items.length} (${pct(v.jev.rate)}), harmful ${v.jev.harmful}`);
    console.log(`discordant: jev only ${v.discordant.jevOnly}, rules only ${v.discordant.rulesOnly}, McNemar exact p = ${v.p.toFixed(4)}`);
    console.log(`beats rules ${v.conditions.beatsRules}, significant ${v.conditions.significant}, harmful not above rules ${v.conditions.harmfulNotAboveRules}`);
    console.log(v.passes ? 'PASSES the bar. Only Kane switches the point on.' : 'does NOT pass the bar. The point stays off.');
    process.exitCode = v.passes ? 0 : 1;
  }
}
