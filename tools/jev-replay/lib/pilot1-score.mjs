// An exact port of docs/redesign/jev-pilot/score.py, driven from harness records.
//
// This file is an addition to the file list in the brief. It is kept apart from lib/score.mjs on
// purpose: score.mjs is the harness's own general scorer and is free to change, while this file
// must keep reproducing one frozen historical result forever. Mixing them would let a change to
// the general scorer quietly move the pilot's numbers.
//
// Everything here follows score.py exactly, including the parts that look odd:
//   - the noul branch stringifies with Python's str(bool), so rows read "True" and "False";
//   - `float(ans.get("confidence") or 0.0)` turns a missing OR zero confidence into 0.0;
//   - band edges are: below 0.6 is c < 0.6, the middle band is 0.6 <= c <= 0.85 inclusive at
//     both ends, and the top band is c > 0.85;
//   - BAD_DC and UNANSWERABLE_DC are exclusion sets applied only inside the Q3a and Q3b
//     sections, not at flatten time;
//   - Q2a keeps only rows whose stringified truth is not "text_or_none";
//   - Q4a and Q4b split by model id, Q6 and Q6a by extra.had_sort_by;
//   - stability compares run one against run two by call id, per question, and skips the
//     "score" typed answer because score.py's if/elif chain falls through for it.

import { pyStr } from './record.mjs';

export const BANDS = [
  ['below 0.6', (c) => c < 0.6],
  ['0.6 to 0.85', (c) => c >= 0.6 && c <= 0.85],
  ['above 0.85', (c) => c > 0.85],
];

export function bandOf(c) {
  for (const [name, test] of BANDS) if (test(c)) return name;
  return 'below 0.6';
}

// Ground-truth noise in Contoso: PostalCode set on columns that plainly are not postal codes.
export const BAD_DC = new Set([
  'Date[Calendar Year Quarter Number]',
  'Date[Fiscal Year Quarter Number]',
  'Customer[Customer Code]',
]);

// The brief fixes the 11 options; these columns store "Continent", which is not one of them,
// so no answer could have been counted right.
export const UNANSWERABLE_DC = new Set(['Customer[Continent]', 'Store[Continent]']);

function mean(values) {
  let total = 0;
  for (const v of values) total += v;
  return total / values.length;
}

/** Python's round(x, 2): round half to even on the exact value of the double. */
export function pyRound2(x) {
  if (!Number.isFinite(x)) return x;
  const negative = x < 0;
  const digits = Math.abs(x).toFixed(20);
  const dot = digits.indexOf('.');
  const keep = digits.slice(0, dot) + digits.slice(dot + 1, dot + 3);
  const rest = digits.slice(dot + 3);
  let value = BigInt(keep);
  const firstRest = rest[0];
  const restNonZeroAfterFirst = /[1-9]/.test(rest.slice(1));
  if (firstRest > '5' || (firstRest === '5' && restNonZeroAfterFirst)
      || (firstRest === '5' && !restNonZeroAfterFirst && value % 2n === 1n)) {
    value += 1n;
  }
  const out = Number(value) / 100;
  return negative ? -out : out;
}

/** One row per scored answer, in call order then answer-key order, exactly as score.py builds it. */
export function flatten(records) {
  const rows = [];
  for (const record of records) {
    if (record.outcome.errors.length > 0) continue;
    const ans = record.response.raw;
    const truth = record.label.value;
    const base = record.baseline.output;
    let predS; let truthS; let baseS; let conf; let correct; let baseCorrect;
    if (ans.type === 'noul') {
      const p = ans.noul === undefined ? 0.5 : ans.noul;
      const pred = p >= 0.5;
      conf = Math.abs(p - 0.5) * 2;
      correct = pred === Boolean(truth);
      baseCorrect = Boolean(base) === Boolean(truth);
      predS = pyStr(pred);
      truthS = pyStr(Boolean(truth));
      baseS = pyStr(Boolean(base));
    } else {
      const pred = ans.choice === undefined ? null : ans.choice;
      conf = Number(ans.confidence || 0);
      correct = pred === truth;
      baseCorrect = base === truth;
      predS = pyStr(pred);
      truthS = pyStr(truth);
      baseS = pyStr(base);
    }
    rows.push({
      q: record.split.family,
      model: record.split.modelId,
      object: record.context.knowable.object,
      kind: record.context.knowable.kind,
      pred: predS,
      truth: truthS,
      baseline: baseS,
      correct,
      baseline_correct: baseCorrect,
      conf,
      band: bandOf(conf),
      ms: record.outcome.elapsedMs,
      extra: record.context.knowable.extra,
      raw_noul: ans.noul === undefined ? null : ans.noul,
    });
  }
  return rows;
}

function report(name, select, rows, note = '') {
  const rs = rows.filter(select);
  if (rs.length === 0) return null;
  const n = rs.length;
  const bands = {};
  for (const [bn] of BANDS) {
    const b = rs.filter((x) => x.band === bn);
    bands[bn] = {
      n: b.length,
      share: b.length / n,
      accuracy: b.length ? b.filter((x) => x.correct).length / b.length : null,
    };
  }
  // Counter over wrong rows, then most_common(3). Python's most_common is a stable sort by
  // count, so ties keep first-seen order; Array.prototype.sort is stable too.
  const counter = new Map();
  for (const x of rs) {
    if (x.correct) continue;
    const key = JSON.stringify([x.truth, x.pred]);
    counter.set(key, (counter.get(key) ?? 0) + 1);
  }
  const confusions = [...counter.entries()]
    .sort((a, b) => b[1] - a[1])
    .slice(0, 3)
    .map(([key, count]) => {
      const [t, p] = JSON.parse(key);
      const ex = rs.filter((x) => x.truth === t && x.pred === p).slice(0, 3);
      return {
        truth: t,
        jev_picked: p,
        count,
        examples: ex.map((e) => ({
          object: e.object,
          model: e.model,
          confidence: pyRound2(e.conf),
        })),
      };
    });
  return {
    name,
    n,
    accuracy: rs.filter((x) => x.correct).length / n,
    baseline_accuracy: rs.filter((x) => x.baseline_correct).length / n,
    mean_confidence: mean(rs.map((x) => x.conf)),
    bands,
    confusions,
    note,
  };
}

export function sectionsOf(rows) {
  const s = [
    report('Q1 display folder (measure)', (x) => x.q === 'display_folder', rows,
      'aw_packt only; contoso has one folder, so the choice would be degenerate'),
    report('Q2 format family (measure)', (x) => x.q === 'format_family', rows),
    report('Q2a format family, measures that carry a formatString',
      (x) => x.q === 'format_family' && x.truth !== 'text_or_none', rows),
    report('Q3 data category (column)', (x) => x.q === 'data_category', rows,
      '10 columns that carry a category plus 10 that do not'),
    report('Q3a data category, dropping 3 rows whose stored category is plainly wrong',
      (x) => x.q === 'data_category' && !BAD_DC.has(x.object), rows),
    report('Q3b data category, answerable rows with sound stored values',
      (x) => x.q === 'data_category' && !BAD_DC.has(x.object) && !UNANSWERABLE_DC.has(x.object),
      rows),
    report('Q4 hidden from report authors (column)', (x) => x.q === 'hidden', rows),
    report('Q4a hidden, aw_packt', (x) => x.q === 'hidden' && x.model === 'aw_packt', rows),
    report('Q4b hidden, contoso', (x) => x.q === 'hidden' && x.model === 'contoso', rows),
    report('Q5 summarize by (numeric column)', (x) => x.q === 'summarize_by', rows,
      'contoso only; aw_packt leaves summarizeBy unset on 239 of 241 columns'),
    report('Q6 sort-by column, columns that have one',
      (x) => x.q === 'sort_by_column' && Boolean(x.extra.had_sort_by), rows),
    report('Q6a sort-by column, decoy columns that have none',
      (x) => x.q === 'sort_by_column' && !x.extra.had_sort_by, rows),
    report('Q7 date table', (x) => x.q === 'date_table', rows),
    report('Q8 relationship: which side is the one side', (x) => x.q === 'one_side', rows),
  ];
  return s.filter(Boolean);
}

export function pooledCalibration(rows) {
  const pooled = {};
  for (const [bn] of BANDS) {
    const b = rows.filter((x) => x.band === bn);
    pooled[bn] = {
      n: b.length,
      share: b.length / rows.length,
      accuracy: b.length ? b.filter((x) => x.correct).length / b.length : null,
    };
  }
  return pooled;
}

/** Run one against run two, by call id, per question. Score-typed answers fall through. */
export function stability(recordsRun1, recordsRun2) {
  const run2 = new Map();
  for (const r of recordsRun2) {
    if (r.outcome.errors.length > 0) continue;
    run2.set(JSON.stringify([r.context.knowable.callId, r.split.family]), r.response.raw);
  }
  const counts = new Map();
  const order = [];
  for (const r of recordsRun1) {
    if (r.outcome.errors.length > 0) continue;
    const key = JSON.stringify([r.context.knowable.callId, r.split.family]);
    if (!run2.has(key)) continue;
    const a1 = r.response.raw;
    const a2 = run2.get(key);
    let same;
    if (a1.type === 'noul') {
      same = ((a1.noul === undefined ? 0.5 : a1.noul) >= 0.5)
        === ((a2.noul === undefined ? 0.5 : a2.noul) >= 0.5);
    } else if (a1.type === 'choice') {
      same = a1.choice === a2.choice;
    } else {
      continue;
    }
    const q = r.split.family;
    if (!counts.has(q)) {
      counts.set(q, [0, 0]);
      order.push(q);
    }
    const slot = counts.get(q);
    slot[1] += 1;
    if (same) slot[0] += 1;
  }
  const out = {};
  for (const q of order) {
    const [sameCount, total] = counts.get(q);
    out[q] = { n: total, same_answer: sameCount, rate: sameCount / total };
  }
  return out;
}

/** The Q8 second question. Extra, not part of the required comparison list. */
export function q8Clarity(recordsRun1) {
  const rows = [];
  for (const r of recordsRun1) {
    if (r.outcome.errors.length > 0) continue;
    if (r.split.family !== 'one_side') continue;
    const clarity = r.response.secondary;
    if (!clarity) continue;
    rows.push({ score: clarity.score, correct: r.response.raw.choice === r.label.value });
  }
  const right = rows.filter((x) => x.correct).map((x) => x.score);
  const wrong = rows.filter((x) => !x.correct).map((x) => x.score);
  return {
    n: rows.length,
    mean_score_when_right: right.length ? mean(right) : null,
    mean_score_when_wrong: wrong.length ? mean(wrong) : null,
  };
}

/** The run block. Deduped by call id, because tokens and latency are per call, not per answer. */
export function runBlock(recordsRun1) {
  const calls = new Map();
  for (const r of recordsRun1) {
    if (!calls.has(r.context.knowable.callId)) calls.set(r.context.knowable.callId, r);
  }
  const all = [...calls.values()];
  const ok = all.filter((r) => r.outcome.errors.length === 0);
  const lat = ok.map((r) => r.outcome.elapsedMs).sort((a, b) => a - b);
  const tokIn = ok.reduce((a, r) => a + r.outcome.inputTokens, 0);
  const tokOut = ok.reduce((a, r) => a + r.outcome.outputTokens, 0);
  return {
    calls: all.length,
    ok: ok.length,
    failed: all.length - ok.length,
    answers_scored: flatten(recordsRun1).length,
    input_tokens: tokIn,
    output_tokens: tokOut,
    latency_ms: {
      mean: Math.round(lat.reduce((a, b) => a + b, 0) / lat.length),
      p50: lat[Math.floor(lat.length / 2)],
      p90: lat[Math.floor(lat.length * 0.9)],
      max: lat[lat.length - 1],
      over_2000ms: lat.filter((x) => x > 2000).length,
    },
  };
}
