// Score the column replay: Jev with facts vs Jev names-only vs the pilot's trivial rules, per setting.
//
//   node score.mjs [--dir <cache dir>] [--json <out.json>] [--md <out.md>]
//
// Reads calls.json, raw-facts.json and raw-names.json from the private cache dir. Everything it WRITES is aggregate:
// counts, accuracies, calibration bands and value-to-value confusions. No model, table or column name is written,
// and sort-by confusions are reported as categories (none / the labelled column / another candidate), because a
// sort-by answer is itself a column name.
import { readFileSync, writeFileSync } from 'node:fs';
import { homedir } from 'node:os';
import { join } from 'node:path';
import { mulberry32 } from './build-calls.mjs';

export const BANDS = [['below 0.6', (c) => c < 0.6], ['0.6 to 0.85', (c) => c >= 0.6 && c <= 0.85], ['above 0.85', (c) => c > 0.85]];
const bandOf = (c) => (BANDS.find(([, f]) => f(c)) ?? BANDS[0])[0];

// Verdict bar, stated in the results note.
export const BAR = { minN: 50, margin: 0.05, alpha: 0.05, maxMajority: 0.95 };

/** Share of the most common label, and that label: the score of a rule that always answers it. */
export function majority(rs) {
  const m = new Map();
  for (const r of rs) m.set(String(r.truth), (m.get(String(r.truth)) ?? 0) + 1);
  const [value, n] = [...m.entries()].sort((a, b) => b[1] - a[1])[0] ?? [null, 0];
  return { value, share: rs.length ? n / rs.length : 0 };
}

export function parseAnswer(ans) {
  if (!ans) return null;
  if (ans.type === 'noul' || typeof ans.noul === 'number') {
    const p = Number(ans.noul ?? 0.5);
    return { pred: p >= 0.5, conf: Math.abs(p - 0.5) * 2 };
  }
  return { pred: ans.choice, conf: Number(ans.confidence ?? 0) };
}

/** One paired row per (call, question) answered in both arms. */
export function flatten(calls, rawFacts, rawNames) {
  const byId = (raw) => new Map(raw.filter((r) => r && !r.error).map((r) => [r.id, r]));
  const F = byId(rawFacts), N = byId(rawNames);
  const rows = [];
  let unpaired = 0;
  for (const c of calls) {
    for (const q of Object.keys(c.questions)) {
      const f = parseAnswer(F.get(c.id)?.answers?.[q]), n = parseAnswer(N.get(c.id)?.answers?.[q]);
      if (!f || !n) { unpaired++; continue; }
      const truth = c.truth[q];
      const eq = (p) => (typeof truth === 'boolean' ? Boolean(p) === truth : p === truth);
      rows.push({
        q, model: c.model, truth, extra: c.extra ?? {}, flagged: !!c.adjudicate?.[q],
        facts: { ...f, ok: eq(f.pred), band: bandOf(f.conf) },
        names: { ...n, ok: eq(n.pred), band: bandOf(n.conf) },
        rule: { pred: c.baseline[q], ok: eq(c.baseline[q]) },
        typed: typedOf(c, q, eq),
      });
    }
  }
  return { rows, unpaired };
}

/** The stronger rule's answer for this question: the type-aware format rule, or the second run's rules. */
function typedOf(c, q, eq) {
  const p = q === 'format_family' ? c.extra?.formatTyped : c.extra?.typed?.[q];
  return p === undefined ? null : { pred: p, ok: eq(p) };
}

const acc = (rs, k) => (rs.length ? rs.filter((r) => r[k]?.ok).length / rs.length : null);

export function mcnemarP(b, c) {
  const n = b + c;
  if (n === 0) return 1;
  const k = Math.min(b, c);
  let lp = -n * Math.LN2, logC = 0, sum = 0;   // log C(n,0) = 0
  for (let i = 0; i <= k; i++) {
    if (i > 0) logC += Math.log(n - i + 1) - Math.log(i);
    sum += Math.exp(logC + lp);
  }
  return Math.min(1, 2 * sum);
}

export function verdictOf(rs, ruleKey, arm = 'facts') {
  const n = rs.length;
  if (n < BAR.minN) return { verdict: 'MORE DATA', why: `n ${n} is under ${BAR.minN}` };
  const maj = majority(rs);
  if (maj.share >= BAR.maxMajority) return { verdict: 'MORE DATA', why: `labels are one value on ${Math.round(maj.share * 100)}% of rows, so a constant answer would score that; no test of judgement` };
  const b = rs.filter((r) => r[arm].ok && !r[ruleKey].ok).length;
  const c = rs.filter((r) => !r[arm].ok && r[ruleKey].ok).length;
  const diff = (b - c) / n, p = mcnemarP(b, c);
  if (diff >= BAR.margin && p < BAR.alpha) return { verdict: 'SHIP', diff, p, b, c };
  if (diff <= 0) return { verdict: 'RULE', diff, p, b, c };
  return { verdict: 'MORE DATA', diff, p, b, c, why: 'Jev ahead but under the margin or not significant' };
}

const RANK = { RULE: 0, 'MORE DATA': 1, SHIP: 2 };

function calib(rs, arm) {
  return Object.fromEntries(BANDS.map(([bn]) => {
    const b = rs.filter((r) => r[arm].band === bn);
    return [bn, { n: b.length, share: rs.length ? b.length / rs.length : 0, accuracy: b.length ? b.filter((r) => r[arm].ok).length / b.length : null }];
  }));
}

function confusions(rs, q) {
  const cat = (r, p) => {
    if (q !== 'sort_by_column') return String(p);
    if (p === 'none') return 'none';
    return p === r.truth ? 'the labelled column' : 'another candidate';
  };
  const m = new Map();
  for (const r of rs) if (!r.facts.ok) {
    const k = `${cat(r, r.truth)} -> ${cat(r, r.facts.pred)}`;
    m.set(k, (m.get(k) ?? 0) + 1);
  }
  return [...m.entries()].sort((a, b) => b[1] - a[1]).slice(0, 4).map(([k, v]) => ({ pair: k, count: v }));
}

export function section(name, rs, { ruleKeys = ['rule'] } = {}) {
  if (!rs.length) return { name, n: 0 };
  const adj = rs.filter((r) => !r.flagged);
  const best = (set) => ruleKeys.reduce((a, k) => ((acc(set, k) ?? -1) > (acc(set, a) ?? -1) ? k : a), ruleKeys[0]);
  const raw = verdictOf(rs, best(rs)), adjV = verdictOf(adj, best(adj));
  const verdict = RANK[raw.verdict] <= RANK[adjV.verdict] ? raw.verdict : adjV.verdict;
  return {
    name, n: rs.length, facts: acc(rs, 'facts'), names: acc(rs, 'names'), majorityShare: majority(rs).share,
    rules: Object.fromEntries(ruleKeys.map((k) => [k, acc(rs, k)])), bestRule: best(rs),
    adjudicated: { n: adj.length, dropped: rs.length - adj.length, facts: acc(adj, 'facts'), names: acc(adj, 'names'),
      rules: Object.fromEntries(ruleKeys.map((k) => [k, acc(adj, k)])), bestRule: best(adj) },
    calibration: { facts: calib(rs, 'facts'), names: calib(rs, 'names') },
    confusions: confusions(rs, rs[0].q),
    verdictRaw: raw, verdictAdjudicated: adjV, verdict,
  };
}

function balancedHidden(rs, seed = 20260923) {
  const rand = mulberry32(seed);
  const sh = (a) => { const x = a.slice(); for (let i = x.length - 1; i > 0; i--) { const j = Math.floor(rand() * (i + 1)); [x[i], x[j]] = [x[j], x[i]]; } return x; };
  const h = sh(rs.filter((r) => r.truth === true)), v = sh(rs.filter((r) => r.truth === false));
  const k = Math.min(h.length, v.length);
  return [...h.slice(0, k), ...v.slice(0, k)];
}

export function scoreAll(calls, rawFacts, rawNames, diag = {}) {
  const { rows, unpaired } = flatten(calls, rawFacts, rawNames);
  const Q = (q) => rows.filter((r) => r.q === q);
  const sections = [
    section('hidden, every column', Q('hidden')),
    section('hidden, balanced (equal hidden and visible, seeded)', balancedHidden(Q('hidden'))),
    section('summarize by, numeric columns', Q('summarize_by')),
    section('data category, every text or number column', Q('data_category')),
    section('data category, balanced as the pilot drew it', Q('data_category').filter((r) => r.extra.dcBalanced)),
    section('data category, columns that carry one', Q('data_category').filter((r) => r.truth !== 'none')),
    section('sort by, every column asked', Q('sort_by_column')),
    section('sort by, columns that carry one', Q('sort_by_column').filter((r) => r.extra.hadSortBy)),
    section('sort by, label among the offered candidates', Q('sort_by_column').filter((r) => !r.extra.sortLabelOutsideOptions)),
    section('format, numeric and date columns that carry a format string', Q('format_family'), { ruleKeys: ['rule', 'typed'] }),
  ];
  const pooled = Object.fromEntries(['facts', 'names'].map((arm) => [arm, calib(rows, arm)]));
  const models = Object.keys(diag).length;
  const columns = Object.values(diag).reduce((a, d) => a + (d.columns ?? 0), 0);
  const measured = Object.values(diag).reduce((a, d) => a + (d.measuredColumns ?? 0), 0);
  return { models, columns, measuredColumns: measured, answers: rows.length, unpaired, bar: BAR, sections, pooled };
}

// ---- The second run: summarize by, data category and sort by on public sample models. Everything below was fixed
// before any Jev answer existed (README, "Second run").
//   - Both arms are judged against the stronger of the pilot's rule and the second run's rule on the same facts.
//     A setting ships with the arm that clears the bar; when both do, the more accurate one.
//   - Data category and sort by need at least BAR2.minLabelled columns that carry a value, or the verdict is MORE
//     DATA whatever the accuracy.
//   - Data category is judged on a balanced draw (every labelled column plus as many unlabelled ones, seeded), because
//     most columns carry none and a constant answer would win; it must ALSO be no worse than the rule on every
//     column, or it would flood the plan with categories nobody wanted, and the verdict is RULE.
//   - Sort by is judged on text columns with a one-to-one partner, the only columns the engine would ask about.
//   - A label outside the offered options cannot be answered; those rows are counted and left out.
export const BAR2 = { minLabelled: 50 };

function armVerdicts(rs, ruleKeys) {
  const adj = rs.filter((r) => !r.flagged);
  const best = (set) => ruleKeys.reduce((a, k) => ((acc(set, k) ?? -1) > (acc(set, a) ?? -1) ? k : a), ruleKeys[0]);
  const out = {};
  for (const arm of ['facts', 'names']) {
    const raw = verdictOf(rs, best(rs), arm), adjV = verdictOf(adj, best(adj), arm);
    out[arm] = { raw, adjudicated: adjV, verdict: RANK[raw.verdict] <= RANK[adjV.verdict] ? raw.verdict : adjV.verdict };
  }
  return { out, bestRule: best(rs), adjN: adj.length };
}

function summary2(rs, ruleKeys) {
  if (!rs.length) return { n: 0 };
  return {
    n: rs.length, facts: acc(rs, 'facts'), names: acc(rs, 'names'), majorityShare: majority(rs).share,
    rules: Object.fromEntries(ruleKeys.map((k) => [k, acc(rs, k)])),
    labelled: rs.filter((r) => r.truth !== 'none').length,
    calibration: { facts: calib(rs, 'facts'), names: calib(rs, 'names') },
    confusions: confusions(rs, rs[0].q),
  };
}

function decide(verdicts, rs) {
  const ship = ['facts', 'names'].filter((a) => verdicts[a].verdict === 'SHIP');
  if (ship.length) {
    const arm = ship.sort((a, b) => acc(rs, b) - acc(rs, a))[0];
    return { verdict: 'SHIP', shipArm: arm };
  }
  if (['facts', 'names'].every((a) => verdicts[a].verdict === 'RULE')) return { verdict: 'RULE' };
  return { verdict: 'MORE DATA' };
}

export function section2(name, q, rs, { ruleKeys = ['rule', 'typed'], balanced = null, outsideOptions = 0 } = {}) {
  const full = summary2(rs, ruleKeys);
  const labelled = full.labelled ?? 0;
  const base = { name, q, full, labelled, outsideOptions };
  if (labelled < BAR2.minLabelled) {
    return { ...base, verdict: 'MORE DATA', why: `${labelled} labelled columns, under ${BAR2.minLabelled}` };
  }
  if (!balanced) {
    const { out, bestRule } = armVerdicts(rs, ruleKeys);
    return { ...base, bestRule, verdictFacts: out.facts, verdictNames: out.names, ...decide(out, rs) };
  }
  // Data category: the balanced draw decides, the full population guards against flooding.
  const bal = summary2(balanced, ruleKeys);
  const { out, bestRule } = armVerdicts(balanced, ruleKeys);
  const fullBest = ruleKeys.reduce((a, k) => ((acc(rs, k) ?? -1) > (acc(rs, a) ?? -1) ? k : a), ruleKeys[0]);
  const guard = {};
  for (const arm of ['facts', 'names']) {
    guard[arm] = { arm: acc(rs, arm), rule: acc(rs, fullBest), ok: acc(rs, arm) >= acc(rs, fullBest) };
    if (out[arm].verdict === 'SHIP' && !guard[arm].ok) out[arm] = { ...out[arm], verdict: 'RULE', why: 'worse than the rule on every column' };
  }
  return {
    ...base, bestRule, fullBestRule: fullBest, guard,
    balanced: { ...bal, verdictFacts: armVerdicts(balanced, ruleKeys).out.facts, verdictNames: armVerdicts(balanced, ruleKeys).out.names },
    verdictFacts: out.facts, verdictNames: out.names, ...decide(out, balanced),
  };
}

/** The second run's three sections, from flattened rows (or from calls and raw answers). */
export function scoreSecond(rowsOrCalls, rawFacts, rawNames, diag = {}, seed = 20260926) {
  const rows = rawFacts ? flatten(rowsOrCalls, rawFacts, rawNames).rows : rowsOrCalls;
  const Q = (q) => rows.filter((r) => r.q === q);
  const dcAll = Q('data_category');
  const dc = dcAll.filter((r) => !r.extra?.dcOutsideOptions);
  const rand = mulberry32(seed);
  const sh = (a) => { const x = a.slice(); for (let i = x.length - 1; i > 0; i--) { const j = Math.floor(rand() * (i + 1)); [x[i], x[j]] = [x[j], x[i]]; } return x; };
  const withCat = dc.filter((r) => r.truth !== 'none');
  const dcBalanced = [...withCat, ...sh(dc.filter((r) => r.truth === 'none')).slice(0, withCat.length)];
  const sections = [
    section2('summarize by, numeric columns', 'summarize_by', Q('summarize_by')),
    section2('data category, text and number columns', 'data_category', dc, { balanced: dcBalanced, outsideOptions: dcAll.length - dc.length }),
    section2('sort by, text columns with a one-to-one partner', 'sort_by_column', Q('sort_by_column').filter((r) => r.extra?.sortTextPop)),
  ];
  const models = Object.keys(diag).length;
  const columns = Object.values(diag).reduce((a, d) => a + (d.columns ?? 0), 0);
  return { models, columns, answers: rows.length, bar: BAR, bar2: BAR2, sections };
}

export function toMarkdown2(s) {
  const L = ['| setting | n (labelled) | Jev with facts | Jev names only | pilot rule | stronger rule | most common label | verdict |', '|---|---|---|---|---|---|---|---|'];
  for (const x of s.sections) {
    const f = x.balanced ?? x.full;
    const tag = x.balanced ? ' (balanced draw)' : '';
    L.push(`| ${x.name}${tag} | ${f.n ?? 0} (${f.labelled ?? 0}) | **${pct(f.facts)}** | ${pct(f.names)} | ${pct(f.rules?.rule)} | ${pct(f.rules?.typed)} | ${pct(f.majorityShare)} | ${x.verdict}${x.shipArm ? ` (${x.shipArm})` : ''} |`);
    if (x.balanced) L.push(`| ${x.name} (every column) | ${x.full.n} (${x.full.labelled}) | **${pct(x.full.facts)}** | ${pct(x.full.names)} | ${pct(x.full.rules.rule)} | ${pct(x.full.rules.typed)} | ${pct(x.full.majorityShare)} | guard |`);
  }
  return L.join('\n');
}

const pct = (x) => (x == null ? '-' : `${Math.round(x * 100)}%`);

export function toMarkdown(s) {
  const L = [];
  L.push(`| setting | n | Jev with facts | Jev names only | rule | most common label | adjudicated n (dropped) | adj. facts / names / rule | verdict |`);
  L.push('|---|---|---|---|---|---|---|---|---|');
  for (const x of s.sections) {
    if (!x.n) { L.push(`| ${x.name} | 0 | - | - | - | - | - | - | not asked |`); continue; }
    const rule = Object.entries(x.rules).map(([k, v]) => `${pct(v)}${Object.keys(x.rules).length > 1 ? ` (${k})` : ''}`).join(', ');
    const a = x.adjudicated;
    const arule = pct(a.rules[a.bestRule]);
    L.push(`| ${x.name} | ${x.n} | **${pct(x.facts)}** | ${pct(x.names)} | ${rule} | ${pct(x.majorityShare)} | ${a.n} (${a.dropped}) | ${pct(a.facts)} / ${pct(a.names)} / ${arule} | ${x.verdict} |`);
  }
  L.push('', '| setting | arm | below 0.6 (share / acc) | 0.6 to 0.85 | above 0.85 |', '|---|---|---|---|---|');
  for (const x of s.sections) {
    if (!x.n) continue;
    for (const arm of ['facts', 'names']) {
      const c = x.calibration[arm];
      L.push(`| ${x.name} | ${arm} | ${BANDS.map(([bn]) => `${pct(c[bn].share)} / ${pct(c[bn].accuracy)}`).join(' | ')} |`);
    }
  }
  return L.join('\n');
}

if (import.meta.url === `file://${process.argv[1]}`) {
  const arg = (k, d) => { const i = process.argv.indexOf(k); return i > 0 ? process.argv[i + 1] : d; };
  const DIR = arg('--dir', join(homedir(), '.cache', 'jev-replay-columns'));
  const read = (f) => JSON.parse(readFileSync(join(DIR, f), 'utf8'));
  let diag = {};
  try { diag = read('calls-diag.json'); } catch { /* optional */ }
  const second = process.argv.includes('--second');
  const s = (second ? scoreSecond : scoreAll)(read('calls.json'), read('raw-facts.json'), read('raw-names.json'), diag);
  writeFileSync(arg('--json', join(DIR, 'score.json')), JSON.stringify(s, null, 1));
  const md = second ? toMarkdown2(s) : toMarkdown(s);
  writeFileSync(arg('--md', join(DIR, 'score.md')), md + '\n');
  console.log(md);
}
