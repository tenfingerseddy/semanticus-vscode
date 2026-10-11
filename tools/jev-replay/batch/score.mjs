// Score the batching comparison (m11/jev-batch, Kane 30 Sep 2026): the same objects asked one at a time and one table at a
// time. The protocol, fixed BEFORE the run, is docs/redesign/jev-batch-comparison.md; the numbers below are that file's rules.
//
//   node tools/jev-replay/batch/score.mjs [results.json]
//
// Reads the raw results the live run (Semanticus.Tests/JevBatchComparison.cs) wrote OUTSIDE the repository (default
// ~/.cache/jev-batch-comparison/results.json) and writes score.json and score.md next to it. Object names appear only in the
// changed-answer list, because the models are placeholder-named fixtures and every changed answer must be listed.
import { readFileSync, writeFileSync } from 'node:fs';
import { homedir } from 'node:os';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

/** The point's bar: an answer at or above it is one a line stands on. */
export const BAR = 0.85;
/** PASS needs at least this share of compared answers to have the same top answer. */
export const THRESHOLD = 0.95;

/** The option that leaves a line out, per point and question (the protocol's table). */
export function isNeutral(point, key, choice) {
  const question = key.includes('#') ? key.slice(key.lastIndexOf('#') + 1) : '';
  switch (point) {
    case 'column.summarize': return choice === 'none';
    case 'column.format': return choice === 'text_or_none';
    case 'ai.schema': return choice === 'include';
    case 'measure.folder_and_format': return question === 'format_family' ? choice === 'text_or_none' : choice === 'none';
    case 'compose.draft': return choice !== 'yes';
    default: throw new Error('no neutral option is written down for ' + point);
  }
}

/** A line turned on: sure enough, and not the neutral option. */
export function isOn(point, row) {
  return row.choice != null && row.confidence >= BAR && !isNeutral(point, row.key, row.choice);
}

/** Compare the old and new rows of one point over any number of models. Rows are matched by key within a model. */
export function comparePoint(point, runs) {
  let compared = 0, same = 0, unanswered = 0, sureCompared = 0, sureSame = 0;
  const changed = [], harm = [];
  const requests = { old: 0, new: 0 }, tokens = { old: 0, new: 0 }, wallMs = { old: 0, new: 0 };
  for (const run of runs) {
    for (const arm of ['old', 'new']) {
      requests[arm] += run[arm].requests; tokens[arm] += run[arm].inputTokens; wallMs[arm] += run[arm].wallMs;
    }
    const now = new Map(run.new.rows.map((r) => [r.key, r]));
    for (const o of run.old.rows) {
      const n = now.get(o.key);
      if (!n || o.choice == null || n.choice == null) { unanswered++; continue; }
      compared++;
      const entry = { model: run.model, key: o.key, old: [o.choice, +o.confidence.toFixed(3)], new: [n.choice, +n.confidence.toFixed(3)] };
      // Reported beside the verdict, never in it: among the answers where at least one arm was sure, how many kept their answer.
      if (o.confidence >= BAR || n.confidence >= BAR) { sureCompared++; if (o.choice === n.choice) sureSame++; }
      if (o.choice === n.choice) same++;
      else changed.push(entry);
      if (!isOn(point, o) && isOn(point, n)) harm.push(entry);
    }
  }
  const share = compared === 0 ? 0 : same / compared;
  return {
    point, compared, same, share, sureCompared, sureSame, unanswered, changed, harm, requests, tokens, wallMs,
    verdict: compared > 0 && share >= THRESHOLD && harm.length === 0 ? 'PASS' : 'FAIL',
  };
}

/** The live run writes C# objects, whose properties are Pascal case (Requests, Rows, Choice...): read them as camel case. */
export function camel(value) {
  if (Array.isArray(value)) return value.map(camel);
  if (value && typeof value === 'object') {
    return Object.fromEntries(Object.entries(value).map(([k, v]) => [k.length > 0 && k[0] >= 'A' && k[0] <= 'Z' ? k[0].toLowerCase() + k.slice(1) : k, camel(v)]));
  }
  return value;
}

export function scoreResults(raw) {
  const results = camel(raw);
  const byPoint = new Map();
  for (const model of results.models ?? []) {
    for (const [point, pair] of Object.entries(model.points ?? {})) {
      if (!byPoint.has(point)) byPoint.set(point, []);
      byPoint.get(point).push({ model: model.id, old: pair.old, new: pair.new, old2: pair.old2 ?? null });
    }
  }
  const points = [...byPoint].map(([point, runs]) => {
    const scored = comparePoint(point, runs);
    // The noise floor, when the per-object requests were asked a second time: the same requests against themselves.
    const again = runs.filter((r) => r.old2).map((r) => ({ model: r.model, old: r.old, new: r.old2 }));
    if (again.length > 0) {
      const floor = comparePoint(point, again);
      scored.noise = { compared: floor.compared, share: floor.share, changed: floor.changed.length, harm: floor.harm.length, sureCompared: floor.sureCompared, sureSame: floor.sureSame };
    }
    return scored;
  });
  if (results.compose) points.push(comparePoint('compose.draft', [{ model: 'compose', old: results.compose.old, new: results.compose.new }]));
  return points;
}

function fmt(n) { return Math.round(n).toLocaleString('en-US'); }

export function toMarkdown(points) {
  const lines = ['| point | compared | same top answer | changed | harm | requests old to new | tokens old to new | wall old to new | verdict |', '|---|---|---|---|---|---|---|---|---|'];
  for (const p of points) {
    lines.push(`| ${p.point} | ${p.compared} | ${(p.share * 100).toFixed(1)}% | ${p.changed.length} | ${p.harm.length} | ${p.requests.old} to ${p.requests.new} | ${fmt(p.tokens.old)} to ${fmt(p.tokens.new)} | ${(p.wallMs.old / 1000).toFixed(1)}s to ${(p.wallMs.new / 1000).toFixed(1)}s | ${p.verdict} |`);
  }
  for (const p of points) {
    const sure = p.sureCompared > 0 ? ` Where at least one arm was sure: ${p.sureSame} of ${p.sureCompared} kept their answer.` : '';
    const noise = p.noise ? ` Noise floor (the per-object requests asked again): ${(p.noise.share * 100).toFixed(1)}% of ${p.noise.compared} the same, ${p.noise.changed} changed, ${p.noise.harm} turned on.` : '';
    lines.push('', `${p.point}:${sure}${noise}`);
  }
  for (const p of points) {
    if (p.changed.length === 0) continue;
    lines.push('', `Changed answers, ${p.point} (${p.changed.length}; harm marked *):`);
    const harmKeys = new Set(p.harm.map((h) => h.model + '\u001f' + h.key));
    for (const c of p.changed) lines.push(`- ${harmKeys.has(c.model + '\u001f' + c.key) ? '*' : ''}${c.model}: ${c.key}: ${c.old[0]} (${c.old[1]}) to ${c.new[0]} (${c.new[1]})`);
  }
  return lines.join('\n') + '\n';
}

if (process.argv[1] && fileURLToPath(import.meta.url) === process.argv[1]) {
  const path = process.argv[2] ?? join(homedir(), '.cache', 'jev-batch-comparison', 'results.json');
  const points = scoreResults(JSON.parse(readFileSync(path, 'utf8')));
  writeFileSync(join(dirname(path), 'score.json'), JSON.stringify(points, null, 2));
  writeFileSync(join(dirname(path), 'score.md'), toMarkdown(points));
  process.stdout.write(toMarkdown(points));
}
