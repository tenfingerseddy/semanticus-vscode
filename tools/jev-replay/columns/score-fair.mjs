// Score the third, fair column replay (README, "Third run"). Fixed before any Jev answer existed.
//
//   node score-fair.mjs --dir <cache dir> [--hand <hand labels json>]
//
// Reads <dir>/{main,sort,format}/calls.json and raw-fair.json. Writes <dir>/score-fair.json and score-fair.md, both
// aggregate: counts, accuracies and McNemar tests, no model, table or column name.
//
// THE PRO PATH, per setting: Jev's answer when it is sure (the point's bar, 0.85, read the product's way), otherwise
// the principled rule. "Needs a sort" is the month and day rule OR a sure yes from Jev. The verdict compares the Pro
// path with the principled rule on the fair key, paired, column by column.
import { readFileSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { mcnemarP } from './score.mjs';

export const SURE = 0.85;
export const FAIR_BAR = { minN: 50, margin: 0.05, alpha: 0.05 };

/** The product's reading of "sure" (DecisionAnswer.Confidence): a choice's own confidence; for a noul, max(p, 1 - p). */
export function confidenceOf(ans) {
  if (!ans) return 0;
  if (ans.type === 'noul' || typeof ans.noul === 'number') { const p = Number(ans.noul ?? 0.5); return Math.max(p, 1 - p); }
  return Number(ans.confidence ?? 0);
}
export const sure = (ans) => !!ans && confidenceOf(ans) >= SURE;
export function predOf(ans) {
  if (!ans) return undefined;
  if (ans.type === 'noul' || typeof ans.noul === 'number') return Number(ans.noul ?? 0.5) >= 0.5;
  return ans.choice;
}

export function proPath(setting, ans, rule) {
  if (setting === 'needs_sort') return !!rule || (sure(ans) && predOf(ans) === true);
  return sure(ans) ? predOf(ans) : rule;
}

const okFor = (truth, pred) => (Array.isArray(truth) ? truth.includes(pred) : truth === pred);

/** Flat rows from one call list and its raw answers. `truthOf` lets the hand check swap in hand labels. */
export function flattenFair(calls, raw, truthOf = (c, q) => c.truth[q]) {
  const byId = new Map((raw ?? []).filter((r) => r && !r.error).map((r) => [r.id, r]));
  const rows = [];
  for (const c of calls) {
    const ans = byId.get(c.id)?.answers ?? {};
    const answered = byId.has(c.id);
    const qs = Object.keys(c.questions);
    for (const q of qs) {
      if (q === 'sort_partner') continue;
      const truth = truthOf(c, q);
      if (truth === undefined) continue;
      rows.push(row(q, truth, c.rule[q], ans[q], c.author?.[q], answered, c.why?.[q === 'needs_sort' ? 'sort' : q]));
    }
    if (c.questions.sort_partner) {
      const needs = truthOf(c, 'needs_sort'), set = truthOf(c, 'sort_partner');
      if (needs === undefined) continue;
      if (needs) rows.push(row('sort_partner', set, c.rule.sort_partner, ans.sort_partner, c.author?.sort_partner, answered, c.why?.sort));
      // End to end: what the column ends up sorted by, against none or the correct partners.
      const truthE = needs ? set : ['none'];
      const proNeeds = proPath('needs_sort', ans.needs_sort, c.rule.needs_sort);
      const proPartner = proPath('sort_partner', ans.sort_partner, c.rule.sort_partner);
      const pro = proNeeds ? proPartner : 'none';
      const rule = c.rule.needs_sort ? c.rule.sort_partner : 'none';
      const jevAlone = predOf(ans.needs_sort) ? predOf(ans.sort_partner) : 'none';
      rows.push({
        setting: 'sort_by', truth: truthE, why: c.why?.sort, answered,
        pro: { pred: pro, ok: okFor(truthE, pro) }, rule: { pred: rule, ok: okFor(truthE, rule) },
        jev: { pred: jevAlone, ok: okFor(truthE, jevAlone), sure: sure(ans.needs_sort) },
        author: { pred: c.author?.sort_partner, ok: okFor(truthE, c.author?.sort_partner ?? 'none') },
      });
    }
  }
  return rows;
}

function row(setting, truth, rulePred, ans, authorPred, answered, why) {
  const jp = predOf(ans);
  const pro = proPath(setting, ans, rulePred);
  return {
    setting, truth, why, answered,
    pro: { pred: pro, ok: okFor(truth, pro) },
    rule: { pred: rulePred, ok: okFor(truth, rulePred) },
    jev: { pred: jp, ok: jp !== undefined && okFor(truth, jp), sure: sure(ans), conf: confidenceOf(ans) },
    author: authorPred === undefined ? null : { pred: authorPred, ok: okFor(truth, authorPred) },
  };
}

const acc = (rs, k) => (rs.length ? rs.filter((r) => r[k]?.ok).length / rs.length : null);

export function verdict(rs) {
  const n = rs.length;
  const b = rs.filter((r) => r.pro.ok && !r.rule.ok).length;
  const c = rs.filter((r) => !r.pro.ok && r.rule.ok).length;
  const diff = n ? (b - c) / n : 0, p = mcnemarP(b, c);
  const v = n >= FAIR_BAR.minN && diff >= FAIR_BAR.margin && p < FAIR_BAR.alpha ? 'SHIP' : 'RULE';
  const why = v === 'SHIP' ? null : n < FAIR_BAR.minN ? `n ${n} is under ${FAIR_BAR.minN}` : diff <= 0 ? 'the Pro path does not beat the rule' : 'ahead, but under 5 points or not significant';
  return { verdict: v, n, b, c, diff, p, why };
}

// KANE'S BAR, 26 September 2026 (the coordinator relayed it after the bar above was committed in c55f4403; it replaces
// the 5-point margin for every Jev decision). A setting goes on for Pro when the Pro path beats the rule, the win is
// not luck (exact McNemar p below 0.05), and it makes no more HARMFUL mistakes than the rule. The size of the win does
// not matter. Harmful means a wrong hide, a wrong sort partner, or summing something that must not be summed.
export function harm(setting, truth, pred) {
  switch (setting) {
    case 'hidden': return pred === true && truth === false;
    case 'summarize_by': return pred === 'sum' && truth !== 'sum';
    case 'needs_sort': return pred === true && truth === false;
    case 'sort_by': case 'sort_partner': return pred != null && pred !== 'none' && !(Array.isArray(truth) ? truth : [truth]).includes(pred);
    default: return false;
  }
}

export function kaneVerdict(rs) {
  const b = rs.filter((r) => r.pro.ok && !r.rule.ok).length;
  const c = rs.filter((r) => !r.pro.ok && r.rule.ok).length;
  const p = mcnemarP(b, c);
  const harmPro = rs.filter((r) => harm(r.setting, r.truth, r.pro.pred)).length;
  const harmRule = rs.filter((r) => harm(r.setting, r.truth, r.rule.pred)).length;
  const on = b > c && p < FAIR_BAR.alpha && harmPro <= harmRule;
  const why = on ? null : b <= c ? 'the Pro path does not beat the rule' : p >= FAIR_BAR.alpha ? 'the win could be luck' : 'more harmful mistakes than the rule';
  return { verdict: on ? 'ON' : 'OFF', b, c, p, harmPro, harmRule, why };
}

export function scoreFair(rows) {
  const settings = {};
  for (const s of [...new Set(rows.map((r) => r.setting))]) {
    const rs = rows.filter((r) => r.setting === s);
    const sureRs = rs.filter((r) => r.jev.sure);
    const labels = {};
    for (const r of rs) { const k = Array.isArray(r.truth) ? (r.truth[0] === 'none' ? 'none' : 'a partner') : String(r.truth); labels[k] = (labels[k] ?? 0) + 1; }
    const authored = rs.filter((r) => r.author);
    settings[s] = {
      ...verdict(rs), kane: kaneVerdict(rs), pro: acc(rs, 'pro'), rule: acc(rs, 'rule'), jev: acc(rs, 'jev'),
      author: authored.length ? acc(authored, 'author') : null,
      sureShare: rs.length ? sureRs.length / rs.length : 0, sureAccuracy: acc(sureRs, 'jev'),
      unanswered: rs.filter((r) => !r.answered).length, labels,
    };
  }
  return { bar: FAIR_BAR, sure: SURE, settings };
}

export function toMarkdown(s, title) {
  const pct = (x) => (x == null ? '-' : `${(x * 100).toFixed(1)}%`);
  const L = [`### ${title}`, '', '| setting | n | Pro path | principled rule | Jev alone | author | Jev sure (share / right) | b / c | McNemar p | harmful, Pro / rule | committed bar | Kane\'s bar (26 Sep) |', '|---|---|---|---|---|---|---|---|---|---|---|---|'];
  for (const [k, x] of Object.entries(s.settings)) {
    L.push(`| ${k} | ${x.n} | **${pct(x.pro)}** | ${pct(x.rule)} | ${pct(x.jev)} | ${pct(x.author)} | ${pct(x.sureShare)} / ${pct(x.sureAccuracy)} | ${x.b} / ${x.c} | ${x.p < 0.0001 ? x.p.toExponential(1) : x.p.toFixed(4)} | ${x.kane.harmPro} / ${x.kane.harmRule} | ${x.verdict} | **${x.kane.verdict}** |`);
  }
  return L.join('\n');
}

if (import.meta.url === `file://${process.argv[1]}`) {
  const arg = (k) => { const i = process.argv.indexOf(k); return i > 0 ? process.argv[i + 1] : null; };
  const DIR = arg('--dir');
  const read = (p) => JSON.parse(readFileSync(join(DIR, p), 'utf8'));
  const lists = ['main', 'sort', 'format'].map((n) => ({ n, calls: read(`${n}/calls.json`), raw: read(`${n}/raw-fair.json`) }));
  const tag = (rows, n) => (n === 'format' ? rows.map((r) => ({ ...r, setting: 'format_family (shipped question)' })) : rows);
  const rows = lists.flatMap(({ n, calls, raw }) => tag(flattenFair(calls, raw), n));
  const out = { key: scoreFair(rows) };
  const md = [toMarkdown(out.key, 'Against the fair key (every column)')];
  const handPath = arg('--hand');
  if (handPath) {
    // The hand check: the same answers, scored on the hand-labelled 10% against the hand labels.
    const hand = JSON.parse(readFileSync(handPath, 'utf8'));
    const hk = (c, q) => {
      const s = q === 'needs_sort' || q === 'sort_partner' ? 'sort' : q;
      const h = hand[`${s}|${c.id}`];
      if (h === undefined) return undefined;
      if (s !== 'sort') return h;
      return q === 'needs_sort' ? h.needs : h.partners;
    };
    const hrows = lists.flatMap(({ n, calls, raw }) => tag(flattenFair(calls, raw, hk), n));
    out.hand = scoreFair(hrows);
    md.push('', toMarkdown(out.hand, 'Against the blind hand labels (the 10% sample)'));
  }
  writeFileSync(join(DIR, 'score-fair.json'), JSON.stringify(out, null, 1));
  writeFileSync(join(DIR, 'score-fair.md'), md.join('\n') + '\n');
  console.log(md.join('\n'));
}
