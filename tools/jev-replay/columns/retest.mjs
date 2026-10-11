// The 26 September re-test (README, "Re-test on the question that ships"). Kane: "Yes, re-test". Laptop only; reads
// and writes the private cache dirs, never this repository.
//
//   node retest.mjs build   builds the calls and the labels, prints their SHA-256 (committed BEFORE any answer)
//   node retest.mjs score   scores the answers against the committed labels and bar (retest-bar.json)
//
// WHAT IS ASKED. Exactly what the product sends, one question per call: for each column, the product payload
// (product-payload.mjs, which an engine test pins to the engine's own payload) for column.summarize (the base-measure
// question) and for column.category (the replay's own question). Asked by ask.ts with JEV_ARMS=product.
//
// WHICH COLUMNS. Held out: every summarize and data-category column of the 218 pinned models EXCEPT the first blind
// hand-check sample (the 1,641 items the answer key's fixes were fitted to). Nothing here was asked in this shape before.
//
// THE LABELS. Two reads, both fixed before this run, as before: the fair answer key (fairkey.py, committed in
// c55f4403; its summarize_by label is the aggregation that makes sense, which is what the base measure now takes)
// and the SECOND blind hand-check sample (fresh, zero overlap with the first, never used to tune anything).
//
// THE PRO PATH, the product's: Jev's pick when sure (0.85), else the product's rule. Summarize: a sure sum on a flag
// or a line number is refused and the rule decides (productRuleSummarize). Category: a sure pick the column's type
// cannot take, or a link on values that are not links, is refused, so the column is left as none; below the bar the
// value-share rule (baselineDatacatTyped, the rule the product ports).
import { createHash } from 'node:crypto';
import { mkdirSync, readFileSync, writeFileSync, readdirSync } from 'node:fs';
import { homedir } from 'node:os';
import { join } from 'node:path';
import { toProductPayload } from './product-payload.mjs';
import { productRuleSummarize, flagOrOrdinal, NUMERIC } from './rules.mjs';
import { mcnemarP } from './score.mjs';

const FAIR = join(homedir(), '.cache', 'jev-replay-columns-fair');
const OUT = join(homedir(), '.cache', 'jev-replay-columns-retest');
const BAR = JSON.parse(readFileSync(new URL('./retest-bar.json', import.meta.url), 'utf8'));
const SURE = BAR.sure;
const sha = (s) => createHash('sha256').update(s).digest('hex');
const POINTS = { 'column.summarize': 'summarize_by', 'column.category': 'data_category' };
const NUMBER_CATEGORIES = new Set(['Latitude', 'Longitude', 'PostalCode', 'Barcode']);

function sampleIds(dir) {
  const ids = new Set();
  for (const f of readdirSync(join(FAIR, dir)).filter((x) => /^batch\d+\.json$/.test(x)))
    for (const i of JSON.parse(readFileSync(join(FAIR, dir, f), 'utf8'))) ids.add(i.id);
  return ids;
}

function build() {
  const calls = JSON.parse(readFileSync(join(FAIR, 'run', 'main', 'calls.json'), 'utf8'));
  const tuned = sampleIds('handcheck1');
  const fresh = sampleIds('handcheck');
  const hand = JSON.parse(readFileSync(join(FAIR, 'hand-labels-all.json'), 'utf8'));
  const facts = JSON.parse(readFileSync(join(FAIR, 'jev-columns-facts.json'), 'utf8'));
  const cols = {};
  for (const m of facts.models) for (const t of m.tables) for (const c of t.columns) cols[`${m.model}|${t.name}|${c.name}`] = c;
  const labels = {};
  const digests = {};
  for (const [point, q] of Object.entries(POINTS)) {
    const list = [];
    for (const call of calls) {
      if (!call.questions[q] || tuned.has(`${q}|${call.id}`)) continue;
      const p = toProductPayload(call, point);
      list.push({ id: call.id, questions: p.questions, arms: { product: p.state } });
      const c = cols[call.id];
      labels[`${q}|${call.id}`] = {
        key: call.truth[q],
        hand: fresh.has(`${q}|${call.id}`) ? hand[`${q}|${call.id}`] ?? null : null,
        rule: q === 'summarize_by' ? productRuleSummarize(c) : call.rule[q],
        flag: q === 'summarize_by' ? flagOrOrdinal(c.name) : false,
        dataType: c.dataType,
        urlShare: c.measured?.urlShare ?? null,
      };
    }
    mkdirSync(join(OUT, point), { recursive: true });
    const text = JSON.stringify(list);
    writeFileSync(join(OUT, point, 'calls.json'), text);
    digests[point] = { calls: list.length, sha256: sha(text) };
  }
  const labelText = JSON.stringify(labels);
  writeFileSync(join(OUT, 'labels.json'), labelText);
  const handN = Object.fromEntries(Object.values(POINTS).map((q) =>
    [q, Object.entries(labels).filter(([k, v]) => k.startsWith(q + '|') && v.hand != null).length]));
  console.log(JSON.stringify({ digests, labels: { items: Object.keys(labels).length, sha256: sha(labelText), hand: handN } }, null, 1));
}

/** The product's Pro path for one item. */
export function proPick(q, answer, l) {
  const sure = answer && (answer.confidence ?? 0) >= SURE;
  if (q === 'summarize_by') {
    if (sure && !(answer.choice === 'sum' && l.flag)) return answer.choice;
    return l.rule;
  }
  if (!sure) return l.rule;
  const pick = answer.choice;
  if (pick === 'none') return 'none';
  if (NUMERIC.has(l.dataType) && !NUMBER_CATEGORIES.has(pick)) return 'none';   // refused: a place for a number
  if (!NUMERIC.has(l.dataType) && l.dataType !== 'string') return 'none';
  if ((pick === 'WebUrl' || pick === 'ImageUrl') && l.urlShare !== null && l.urlShare <= 0) return 'none';
  return pick;
}

const harm = (q, truth, pred) => q === 'summarize_by' && pred === 'sum' && truth !== 'sum';

function read(q, rows, which) {
  const rs = rows.filter((r) => r.l[which] != null);
  const t = (r) => r.l[which];
  const b = rs.filter((r) => r.pro === t(r) && r.l.rule !== t(r)).length;
  const c = rs.filter((r) => r.pro !== t(r) && r.l.rule === t(r)).length;
  const p = mcnemarP(b, c);
  const harmPro = rs.filter((r) => harm(q, t(r), r.pro)).length;
  const harmRule = rs.filter((r) => harm(q, t(r), r.l.rule)).length;
  const pct = (f) => rs.length ? +(100 * rs.filter(f).length / rs.length).toFixed(1) : null;
  const pass = b > c && p < BAR.alpha && harmPro <= harmRule;
  return { n: rs.length, pro: pct((r) => r.pro === t(r)), rule: pct((r) => r.l.rule === t(r)), jev: pct((r) => r.a?.choice === t(r)),
    sure: pct((r) => (r.a?.confidence ?? 0) >= SURE), b, c, p, harmPro, harmRule, kane: pass ? 'PASS' : 'FAIL' };
}

function score() {
  const labels = JSON.parse(readFileSync(join(OUT, 'labels.json'), 'utf8'));
  const out = {};
  for (const [point, q] of Object.entries(POINTS)) {
    const raw = JSON.parse(readFileSync(join(OUT, point, 'raw-product.json'), 'utf8'));
    const errors = raw.filter((r) => !r || r.error).length;
    const rows = raw.filter((r) => r && !r.error).map((r) => {
      const l = labels[`${q}|${r.id}`];
      const a = r.answers?.[q];
      return { id: r.id, l, a, pro: proPick(q, a, l) };
    });
    const key = read(q, rows, 'key');
    const hand = read(q, rows, 'hand');
    // The committed bar (retest-bar.json): Kane's bar on the blind hand read, and no more harmful mistakes than the
    // rule on the key read. Anything else switches the point off.
    const on = hand.kane === 'PASS' && key.harmPro <= key.harmRule;
    const tokens = raw.reduce((s, r) => s + (r?.usage?.input_tokens ?? 0), 0);
    out[point] = { calls: raw.length, errors, version: raw.find((r) => r?.jev_version)?.jev_version ?? null, tokens, key, hand, verdict: on ? 'ON' : 'OFF' };
  }
  writeFileSync(join(OUT, 'score.json'), JSON.stringify(out, null, 1));
  console.log(JSON.stringify(out, null, 1));
}

if (import.meta.url === `file://${process.argv[1]}`) {
  const what = process.argv[2];
  if (what === 'build') build();
  else if (what === 'score') score();
  else throw new Error('usage: retest.mjs build | score');
}
