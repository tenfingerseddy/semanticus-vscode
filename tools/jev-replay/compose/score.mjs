// Score the compose.draft replay (composer lane L5): aggregate numbers and the verdict against the committed bar.
//
//   node tools/jev-replay/compose/score.mjs [results.json]
//
// Reads the raw results the live replay wrote OUTSIDE the repository (default ~/.cache/jev-replay-compose/results.json),
// the labels and the bar beside this file, and writes score.json and score.md next to the results. It prints and writes
// aggregate numbers only: counts and shares, never a sentence, a word the person typed, or a model name.
import { readFileSync, writeFileSync } from 'node:fs';
import { homedir } from 'node:os';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
export const LABELS = JSON.parse(readFileSync(join(here, 'labels.json'), 'utf8'));
export const BAR = JSON.parse(readFileSync(join(here, 'bar.json'), 'utf8'));

/** The ops that only a Workflows step may use: a Plan draft runs every read before the change, so it never drafts them. */
export const READS = ['benchmark_dax', 'profile_dax'];

const set = (xs) => new Set(xs ?? []);

/** How many of `a` find no partner in `b`, one for one: a multiset difference, so a step asked for twice needs two. */
export function unmatched(a, b) {
  const left = new Map();
  for (const x of b ?? []) left.set(x, (left.get(x) ?? 0) + 1);
  let n = 0;
  for (const x of a ?? []) {
    const c = left.get(x) ?? 0;
    if (c > 0) left.set(x, c - 1); else n++;
  }
  return n;
}

/** A Plan draft is right when its steps, as a LIST, hold each labelled op as many times as it is labelled and nothing
 *  past the label and alsoOk. Compared as multisets, not sets (D7, 26 Sep): a set cannot see a draft that makes one
 *  format step where the words asked for two, nor one that makes two where they asked for one. Not in order, because
 *  the engine orders a draft's steps by what depends on what, never in the order they were typed. */
export function draftRight(got, want, ok = []) {
  return unmatched(want, got) === 0 && unmatched(got, [...(want ?? []), ...(ok ?? [])]) === 0;
}

/** A Workflows step is right when it holds every labelled op and nothing outside the label and alsoOk. One step is one
 *  clause, and the steps are compared clause by clause in the order typed, so the whole description is an ordered list;
 *  within one step its ops are a set, because a step uses each op once. */
export function stepRight(got, want, ok = []) {
  const g = set(got);
  const allowed = set([...(want ?? []), ...(ok ?? [])]);
  return (want ?? []).every((op) => g.has(op)) && [...g].every((op) => allowed.has(op));
}

/** Exact two-sided McNemar p from the two discordant counts. */
export function mcnemar(b, c) {
  const n = b + c;
  if (n === 0) return 1;
  let tail = 0, choose = 1;
  for (let k = 0; k <= Math.min(b, c); k++) { if (k > 0) choose = choose * (n - k + 1) / k; tail += choose; }
  return Math.min(1, 2 * tail / 2 ** n);
}

/** Score one results file against the labels, three arms per door: Jev only (a step Jev left is a miss), the word
 *  match only, and the Pro path (Jev when sure, else the word match), which is what the product shows and what the bar
 *  reads (Kane, 26 Sep). Pure, so the self-test can feed it a fixture. */
export function score(results, labels = LABELS, bar = BAR) {
  const byId = new Map(labels.items.map((i) => [i.id, i]));
  const opOf = new Map((results.templates ?? []).map((t) => [t.id ?? t.Id, t.op ?? t.Op]));
  const s = {
    items: 0, splitMismatch: 0, steps: 0, jevRight: 0, wordsRight: 0, hybridRight: 0, jevChose: 0, jevWrongSure: 0,
    compose: 0, composeJevRight: 0, composeWordsRight: 0, composeHybridRight: 0, composeErrors: 0, composeDrafted: 0, jevAsked: 0,
    requests: results.requests ?? null, inputTokens: results.inputTokens ?? null,
    describeMsP50: null, composeMsP50: null,
  };
  s.pairs = { both: 0, proOnly: 0, wordsOnly: 0, neither: 0 };
  s.rebuiltPairs = { both: 0, proOnly: 0, wordsOnly: 0, neither: 0 };
  s.rebuiltRight = 0;
  s.harm = { pro: { dropped: 0, wrong: 0 }, words: { dropped: 0, wrong: 0 }, rebuilt: { dropped: 0, wrong: 0 } };
  const dMs = [], cMs = [];
  for (const row of results.rows ?? []) {
    const label = byId.get(row.id);
    if (!label) continue;
    s.items++;
    if (row.jevAsked) s.jevAsked++;
    if ((row.clauses ?? []).length !== label.clauses.length) { s.splitMismatch++; continue; }
    dMs.push(row.describeMs ?? 0);
    label.clauses.forEach((want, i) => {
      const ok = label.alsoOk?.[i] ?? [];
      const c = row.clauses[i];
      s.steps++;
      const jev = c.jev ?? [];
      if (jev.length) s.jevChose++;
      if (stepRight(jev, want, ok)) s.jevRight++;
      else if (jev.length) s.jevWrongSure++;
      if (stepRight(c.byWords, want, ok)) s.wordsRight++;
      if (stepRight(jev.length ? jev : c.byWords, want, ok)) s.hybridRight++;
    });
    if (row.composeError) { s.composeErrors++; continue; }
    if (!row.compose) continue;
    cMs.push(row.composeMs ?? 0);
    s.compose++;
    const want = label.clauses.flat().filter((op) => !READS.includes(op));
    const ok = (label.alsoOk ?? []).flat().filter((op) => !READS.includes(op));
    // Every list below is a list: a step kept twice counts twice (D7). The word match drafts one step per clause, so
    // its list is the clauses' matches in turn, never folded into a set.
    const jevKept = row.compose.jevDrafted ? (row.compose.kept ?? []).map((id) => opOf.get(id.replace(/-\d+$/u, '')) ?? id) : [];
    const plan = (ops) => (ops ?? []).filter((op) => !READS.includes(op));
    const words = (row.clauses ?? []).flatMap((c) => plan(c.byWords));
    if (row.compose.jevDrafted) s.composeDrafted++;
    if (row.compose.jevDrafted && draftRight(jevKept, want, ok)) s.composeJevRight++;
    if (draftRight(words, want, ok)) s.composeWordsRight++;
    const pro = row.compose.jevDrafted ? jevKept : words;
    // The per-clause rule as the engine runs it since D7 (one step per clause, never merged across clauses), rebuilt
    // from what each clause's own ask chose on Workflows: Jev's sure steps for a clause it was sure of, else that clause's
    // word match. A proxy for the Plan's own asks, whose per-clause answers the run did not record (they differ only in
    // leaving the two reads out). Scored alongside, never in place of, the draft the run really made.
    const rebuilt = (row.clauses ?? []).flatMap((c) => { const j = plan(c.jev); return j.length ? j : plan(c.byWords); });
    const pr = draftRight(pro, want, ok), wr = draftRight(words, want, ok), rr = draftRight(rebuilt, want, ok);
    if (pr) s.composeHybridRight++;
    if (rr) s.rebuiltRight++;
    s.pairs[pr && wr ? 'both' : pr ? 'proOnly' : wr ? 'wordsOnly' : 'neither']++;
    s.rebuiltPairs[rr && wr ? 'both' : rr ? 'proOnly' : wr ? 'wordsOnly' : 'neither']++;
    for (const [side, got] of [['pro', pro], ['words', words], ['rebuilt', rebuilt]]) {
      s.harm[side].dropped += unmatched(want, got);
      s.harm[side].wrong += unmatched(got, [...want, ...ok]);
    }
  }
  const p50 = (xs) => (xs.length ? xs.slice().sort((a, b) => a - b)[Math.floor(xs.length / 2)] : null);
  s.describeMsP50 = p50(dMs);
  s.composeMsP50 = p50(cMs);
  s.describeShare = s.steps ? s.jevRight / s.steps : 0;
  s.wordsShare = s.steps ? s.wordsRight / s.steps : 0;
  s.hybridShare = s.steps ? s.hybridRight / s.steps : 0;
  s.composeShare = s.compose ? s.composeJevRight / s.compose : 0;
  s.composeWordsShare = s.compose ? s.composeWordsRight / s.compose : 0;
  s.composeHybridShare = s.compose ? s.composeHybridRight / s.compose : 0;
  s.mcnemarP = mcnemar(s.pairs.proOnly, s.pairs.wordsOnly);
  s.rebuiltShare = s.compose ? s.rebuiltRight / s.compose : 0;
  s.rebuiltMcnemarP = mcnemar(s.rebuiltPairs.proOnly, s.rebuiltPairs.wordsOnly);
  // Kane's switch-on bar, the Plan door: beats the word match, not by luck, and no more harmful mistakes.
  const kane = (right, p, harm) => right > s.composeWordsRight && p < (bar.alpha ?? 0.05)
    && harm.dropped <= s.harm.words.dropped && harm.wrong <= s.harm.words.wrong;
  s.rebuiltOk = kane(s.rebuiltRight, s.rebuiltMcnemarP, s.harm.rebuilt);
  let describeOk, composeOk;
  if (bar.arm === 'kane-2026-09-26') {
    describeOk = true;
    composeOk = kane(s.composeHybridRight, s.mcnemarP, s.harm.pro);
  } else if (bar.arm === 'hybrid') {
    describeOk = s.hybridShare >= bar.describeMin;
    composeOk = s.composeHybridShare >= bar.composeMin;
  } else {
    describeOk = s.describeShare >= bar.describeMin && (!bar.notBelowWordMatch || s.describeShare >= s.wordsShare);
    composeOk = s.composeShare >= bar.composeMin;
  }
  s.verdict = s.jevAsked === 0 ? 'NOT RUN' : describeOk && composeOk ? 'CLEARS THE BAR' : 'BELOW THE BAR';
  s.describeOk = describeOk;
  s.composeOk = composeOk;
  return s;
}

const pct = (x) => (100 * x).toFixed(1) + '%';

export function markdown(s, bar = BAR) {
  const arm = bar.arm === 'hybrid' ? 'Pro path (Jev when sure, else the word match)' : 'Jev only';
  return [
    `| | Jev only | word match only | Pro path |`, `|---|---|---|---|`,
    `| Workflows steps right (of ${s.steps}) | ${s.jevRight} (${pct(s.describeShare)}) | ${s.wordsRight} (${pct(s.wordsShare)}) | ${s.hybridRight} (${pct(s.hybridShare)}) |`,
    `| Plan drafts exactly right (of ${s.compose}) | ${s.composeJevRight} (${pct(s.composeShare)}) | ${s.composeWordsRight} (${pct(s.composeWordsShare)}) | ${s.composeHybridRight} (${pct(s.composeHybridShare)}) |`,
    '',
    `| | value |`, `|---|---|`,
    `| descriptions scored | ${s.items} (split mismatches ${s.splitMismatch}) |`,
    `| steps Jev chose an op for | ${s.jevChose}; of those wrong ${s.jevWrongSure} |`,
    `| Plan drafts Jev drafted (not the menu) | ${s.composeDrafted} of ${s.compose}; errors ${s.composeErrors} |`,
    `| requests, input tokens | ${s.requests}, ${s.inputTokens} |`,
    `| latency p50, Workflows describe / Plan draft | ${s.describeMsP50} ms / ${s.composeMsP50} ms |`,
    `| Plan pairs: both right, only Pro path, only word match, both wrong | ${s.pairs.both}, ${s.pairs.proOnly}, ${s.pairs.wordsOnly}, ${s.pairs.neither} |`,
    `| exact McNemar p, Pro path against word match | ${s.mcnemarP.toFixed(4)} |`,
    `| Plan harmful mistakes, Pro path: dropped, wrong | ${s.harm.pro.dropped}, ${s.harm.pro.wrong} |`,
    `| Plan harmful mistakes, word match: dropped, wrong | ${s.harm.words.dropped}, ${s.harm.words.wrong} |`,
    `| Plan, one step per clause rebuilt from the per-clause asks: right, pairs, McNemar p | ${s.rebuiltRight} (${pct(s.rebuiltShare)}); ${s.rebuiltPairs.both}, ${s.rebuiltPairs.proOnly}, ${s.rebuiltPairs.wordsOnly}, ${s.rebuiltPairs.neither}; ${s.rebuiltMcnemarP.toFixed(4)} |`,
    `| Plan, rebuilt: dropped, wrong; Kane's bar | ${s.harm.rebuilt.dropped}, ${s.harm.rebuilt.wrong}; ${s.rebuiltOk ? 'holds' : 'does not hold'} |`,
    bar.arm === 'kane-2026-09-26' ? `| bar | Kane 26 Sep: beats the word match, McNemar p < ${bar.alpha ?? 0.05}, no more dropped or wrong steps |`
      : `| bar | ${arm}: Workflows ${pct(bar.describeMin)}, Plan ${pct(bar.composeMin)} |`,
    `| verdict | **${s.verdict}** |`,
  ].join('\n');
}

if (process.argv[1] && fileURLToPath(import.meta.url) === process.argv[1]) {
  // node score.mjs [results.json] [labels file] [bar file]: the fresh run is results-fresh.json, labels-fresh.json, bar-fresh.json.
  const file = process.argv[2] ?? join(homedir(), '.cache', 'jev-replay-compose', 'results.json');
  const labels = process.argv[3] ? JSON.parse(readFileSync(join(here, process.argv[3]), 'utf8')) : LABELS;
  const bar = process.argv[4] ? JSON.parse(readFileSync(join(here, process.argv[4]), 'utf8')) : BAR;
  const s = score(JSON.parse(readFileSync(file, 'utf8')), labels, bar);
  const base = file.replace(/\.json$/u, '');
  writeFileSync(base + '-score.json', JSON.stringify(s, null, 1));
  writeFileSync(base + '-score.md', markdown(s, bar) + '\n');
  console.log(markdown(s, bar));
}
