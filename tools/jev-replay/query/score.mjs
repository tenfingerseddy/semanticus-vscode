// Score the query.summarize replay: aggregate numbers and the verdict, never a name from a real model.
// Node 26, no packages. Reads ~/.cache/jev-replay-query/results.json (JEV_REPLAY_OUT overrides) and the committed
// labels and bar beside this file; writes score.json and score.md next to the results.
//
//   node tools/jev-replay/query/score.mjs
import { existsSync, readFileSync, writeFileSync } from 'node:fs';
import { homedir } from 'node:os';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const HERE = dirname(fileURLToPath(import.meta.url));

const norm = (s) => String(s ?? '').trim().toLowerCase();

/** True when the chosen names fill the labelled slots exactly: each slot matched by a different chosen name, and
 *  nothing chosen outside every slot. */
export function slotsMatch(slots, chosen) {
    const left = [...chosen];
    if (left.length !== slots.length) return false;
    // Few slots, so try every assignment.
    const tryFrom = (i, used) => {
        if (i === slots.length) return true;
        for (let k = 0; k < left.length; k++) {
            if (used.has(k) || !slots[i].includes(left[k])) continue;
            used.add(k);
            if (tryFrom(i + 1, used)) return true;
            used.delete(k);
        }
        return false;
    };
    return tryFrom(0, new Set());
}

export function filtersMatch(expected, chosen) {
    if (expected.length !== chosen.length) return false;
    const used = new Set();
    for (const e of expected) {
        const want = new Set(e.values.map(norm));
        const k = chosen.findIndex((c, i) => !used.has(i) && e.column.includes(c.column) && c.op === e.op
            && c.values.length === want.size && c.values.every((v) => want.has(norm(v))));
        if (k < 0) return false;
        used.add(k);
    }
    return true;
}

export function topMatch(expected, chosen) {
    if (!expected) return !chosen;
    return !!chosen && chosen.n === expected.n && !!chosen.lowest === !!expected.lowest && expected.by.includes(chosen.by);
}

export function readingMatches(expect, parts) {
    if (!parts || expect === 'decline') return false;
    return slotsMatch(expect.measures, parts.measures ?? [])
        && slotsMatch(expect.groupBy, parts.groupBy ?? [])
        && filtersMatch(expect.filters, parts.filters ?? [])
        && topMatch(expect.top, parts.top);
}

/** One of: right, wrongRan (settled, passed every check, not the label), caught (settled, a check refused it, not the
 *  label), unsure (nothing settled), missing (not asked, or no answer). */
export function jevCategory(label, jev) {
    if (!jev) return 'missing';
    const settled = jev.outcome === 'built' || jev.outcome === 'answered' || jev.outcome === 'refused';
    if (label.expect === 'decline') return settled && jev.outcome !== 'refused' ? 'wrongRan' : settled ? 'caught' : 'right';
    if (!settled) return jev.readBy === 'jev' || (jev.unsure ?? []).length > 0 && jev.note == null ? 'unsure' : 'missing';
    const match = readingMatches(label.expect, jev.parts);
    if (label.refused) return match && jev.outcome === 'refused' ? 'right' : jev.outcome === 'refused' ? 'caught' : 'wrongRan';
    if (match) return jev.outcome === 'refused' ? 'caught' : 'right';
    return jev.outcome === 'refused' ? 'caught' : 'wrongRan';
}

export function wordsCategory(label, arm) {
    if (label.expect === 'decline') return arm.decided ? 'wrongRan' : 'right';
    if (!arm.decided) return 'unsure';
    return readingMatches(label.expect, arm.parts) ? 'right' : 'wrongRan';
}

/** Exact two-sided McNemar test on the discordant pairs: b items only Jev got right, c items only the baseline got
 *  right. Under the null each discordant item is a fair coin, so p is the two-sided binomial tail at min(b, c). */
export function mcnemarExact(b, c) {
    const n = b + c;
    if (n === 0) return 1;
    const k = Math.min(b, c);
    let tail = 0;
    let term = Math.pow(0.5, n); // C(n, 0) / 2^n
    for (let i = 0; i <= k; i++) {
        tail += term;
        term = term * (n - i) / (i + 1);
    }
    return Math.min(1, 2 * tail);
}

/** The baselines a result row carries: the word match always, the shortlist arm when the replay scored it. */
export const BASELINES = { words: 'words_arm', shortlist: 'shortlist_arm' };
const BASELINE_NAMES = { words: 'the word match', shortlist: 'the shortlist' };

/** Pick the best baseline on these rows: most right, ties broken by fewer harmful mistakes, then by name. */
export function bestBaseline(labels, results) {
    const byId = new Map(labels.items.map((i) => [i.id, i]));
    const tally = Object.entries(BASELINES).filter(([, key]) => results.rows.some((r) => r[key])).map(([name, key]) => {
        let right = 0, harmful = 0;
        for (const row of results.rows) {
            const label = byId.get(row.id);
            if (!label || !row[key]) continue;
            const c = wordsCategory(label, row[key]);
            if (c === 'right') right++;
            if (c === 'wrongRan') harmful++;
        }
        return { name, key, right, harmful };
    });
    tally.sort((a, b) => b.right - a.right || a.harmful - b.harmful || a.name.localeCompare(b.name));
    return { best: tally[0], all: tally };
}

export function score(labels, results, bar) {
    const byId = new Map(labels.items.map((i) => [i.id, i]));
    const count = (xs) => xs.reduce((m, x) => ((m[x] = (m[x] ?? 0) + 1), m), {});
    const jev = [], words = [], ms = [];
    let onlyJev = 0, onlyWords = 0;
    const perModel = {};
    const { best, all } = bestBaseline(labels, results);
    for (const row of results.rows) {
        const label = byId.get(row.id);
        if (!label) continue;
        const j = jevCategory(label, row.jev);
        const w = wordsCategory(label, row[best.key]);
        jev.push(j); words.push(w);
        if (j === 'right' && w !== 'right') onlyJev++;
        if (w === 'right' && j !== 'right') onlyWords++;
        if (typeof row.ms === 'number') ms.push(row.ms);
        (perModel[row.model] ??= { n: 0, jevRight: 0, wordsRight: 0 });
        perModel[row.model].n++;
        if (j === 'right') perModel[row.model].jevRight++;
        if (w === 'right') perModel[row.model].wordsRight++;
    }
    const n = jev.length;
    const jc = count(jev), wc = count(words);
    const share = (k, c) => (c[k] ?? 0) / n;
    ms.sort((a, b) => a - b);
    const q = (p) => (ms.length ? Math.round(ms[Math.min(ms.length - 1, Math.floor(p * ms.length))]) : null);
    const jevRight = share('right', jc), jevWrongRan = share('wrongRan', jc), wordsRight = share('right', wc);
    const p = mcnemarExact(onlyJev, onlyWords);
    const jevHarmful = jc.wrongRan ?? 0, wordsHarmful = wc.wrongRan ?? 0;
    // Version 2 (Kane's switch-on bar, 26 Sep) when the bar carries mcnemarAlpha; version 1 otherwise.
    const pass = bar.mcnemarAlpha !== undefined
        ? onlyJev > onlyWords && p < bar.mcnemarAlpha && (!bar.harmfulNotAboveBaseline || jevHarmful <= wordsHarmful)
        : jevRight >= bar.rightMin && jevWrongRan <= bar.wrongRanMax && (!bar.notBelowWordMatch || jevRight >= wordsRight);
    return {
        point: results.point, ranUtc: results.ranUtc, items: n, requests: results.requests, inputTokens: results.inputTokens,
        jev: { counts: jc, right: jevRight, wrongRan: jevWrongRan }, words: { counts: wc, right: wordsRight, wrongRan: share('wrongRan', wc) },
        baseline: best.name, baselines: all.map(({ name, right, harmful }) => ({ name, right, harmful })),
        mcnemar: { onlyJevRight: onlyJev, onlyBaselineRight: onlyWords, p },
        harmful: { jev: jevHarmful, baseline: wordsHarmful },
        perModel, latencyMs: { p50: q(0.5), p90: q(0.9), max: ms.length ? Math.round(ms[ms.length - 1]) : null },
        bar: { version: bar.version ?? 1, rightMin: bar.rightMin, wrongRanMax: bar.wrongRanMax, notBelowWordMatch: bar.notBelowWordMatch,
            mcnemarAlpha: bar.mcnemarAlpha, harmfulNotAboveBaseline: bar.harmfulNotAboveBaseline },
        verdict: pass ? 'LIVE' : 'NOT LIVE',
    };
}

// ---- near repeats ---------------------------------------------------------------------------------------------

/** Words that change how a question reads but not what it asks for. */
const FILLER = new Set('a an the by per for each across in of on what is are was our my me show give how much many overall total all'.split(' '));
const family = (model) => (String(model).startsWith('contoso') ? 'contoso' : String(model));
const contentWords = (s) => new Set(String(s).toLowerCase().replace(/[^a-z0-9]+/g, ' ').split(' ').filter((w) => w && !FILLER.has(w)));
const sortedSlots = (slots) => slots.map((s) => [...s].sort()).sort((a, b) => JSON.stringify(a).localeCompare(JSON.stringify(b)));
/** The labelled parts, written so that two labels asking for the same thing compare equal whatever their order. */
export function partsKey(expect) {
    if (expect === 'decline') return 'decline';
    return JSON.stringify({
        measures: sortedSlots(expect.measures), groupBy: sortedSlots(expect.groupBy),
        filters: expect.filters.map((f) => JSON.stringify({ column: [...f.column].sort(), op: f.op, values: [...f.values].map(norm).sort() })).sort(),
        top: expect.top ? { n: expect.top.n, by: [...expect.top.by].sort(), lowest: !!expect.top.lowest } : null,
    });
}

/** A question that is new in its words but not in what it asks: the same labelled parts as an earlier question on the
 *  same model family (the two Contoso arms are one family), with at most two content words different once the filler
 *  words are dropped. An exact-string check lets these through ("average order value across 2024" against "average
 *  order value in 2024"). Each is returned with its closest earlier twin. */
export function nearRepeats(items, earlier, maxWordsDifferent = 2) {
    const out = [];
    for (const i of items) {
        const key = partsKey(i.expect), words = contentWords(i.words);
        let best = null;
        for (const e of earlier) {
            if (family(e.model) !== family(i.model) || partsKey(e.expect) !== key) continue;
            const other = contentWords(e.words);
            const differ = [...words].filter((w) => !other.has(w)).length + [...other].filter((w) => !words.has(w)).length;
            if (differ <= maxWordsDifferent && (!best || differ < best.differ)) best = { differ, earlier: e };
        }
        if (best) out.push({ id: i.id, words: i.words, earlierId: best.earlier.id, earlierWords: best.earlier.words, wordsDifferent: best.differ });
    }
    return out;
}

/** The score's markdown head: the table, the baselines, the discordant and harmful lines, the bar and the verdict. */
export function scoreMarkdown(s) {
    const pct = (x) => (100 * x).toFixed(1) + '%';
    // The discordant and harmful lines are against the best baseline, which is not always the word match.
    const best = BASELINE_NAMES[s.baseline] ?? s.baseline;
    return [
        `# query.summarize replay score`,
        ``,
        `Items: ${s.items}. Jev requests: ${s.requests}. Input tokens: ${s.inputTokens}.`,
        ``,
        `| arm | right | wrong and would run | caught by a check | unsure | not asked |`,
        `|---|---|---|---|---|---|`,
        `| Jev | ${s.jev.counts.right ?? 0} (${pct(s.jev.right)}) | ${s.jev.counts.wrongRan ?? 0} (${pct(s.jev.wrongRan)}) | ${s.jev.counts.caught ?? 0} | ${s.jev.counts.unsure ?? 0} | ${s.jev.counts.missing ?? 0} |`,
        `| baseline (${s.baseline}) | ${s.words.counts.right ?? 0} (${pct(s.words.right)}) | ${s.words.counts.wrongRan ?? 0} (${pct(s.words.wrongRan)}) | 0 | ${s.words.counts.unsure ?? 0} | 0 |`,
        ``,
        `Per model: ${Object.entries(s.perModel).map(([m, v]) => `${m} Jev ${v.jevRight}/${v.n}, words ${v.wordsRight}/${v.n}`).join('; ')}.`,
        `Latency per ask (ms): p50 ${s.latencyMs.p50}, p90 ${s.latencyMs.p90}, max ${s.latencyMs.max}.`,
        ``,
        `Baselines (right / harmful): ${s.baselines.map((b) => `${b.name} ${b.right} / ${b.harmful}`).join('; ')}. Best: ${s.baseline}.`,
        `Discordant items: only Jev right ${s.mcnemar.onlyJevRight}, only ${best} right ${s.mcnemar.onlyBaselineRight}; exact McNemar p = ${s.mcnemar.p.toFixed(4)}.`,
        `Harmful mistakes (sure, wrong, would run): Jev ${s.harmful.jev}, ${best} ${s.harmful.baseline}.`,
        s.bar.version >= 2
            ? `Bar (version ${s.bar.version}): Jev right more often than the best baseline with McNemar p < ${s.bar.mcnemarAlpha}, and no more harmful mistakes.`
            : `Bar: right at least ${pct(s.bar.rightMin)}, wrong and would run at most ${pct(s.bar.wrongRanMax)}, not below the word match.`,
        ``,
        `**Verdict: ${s.verdict}**`,
    ];
}

if (process.argv[1] === fileURLToPath(import.meta.url)) {
    const out = process.env.JEV_REPLAY_OUT ?? join(homedir(), '.cache', 'jev-replay-query');
    // JEV_REPLAY_SET=fresh, fresh2 or fresh3 scores that held-out set against its own <set>-bar.json; the default is
    // the old 62 and bar.json.
    const set = process.env.JEV_REPLAY_SET;
    const bar = JSON.parse(readFileSync(join(HERE, /^fresh\d*$/.test(set ?? '') ? set + '-bar.json' : 'bar.json'), 'utf8'));
    const labels = JSON.parse(readFileSync(join(HERE, bar.labels ?? 'labels.json'), 'utf8'));
    const results = JSON.parse(readFileSync(join(out, 'results.json'), 'utf8'));
    const s = score(labels, results, bar);
    writeFileSync(join(out, 'score.json'), JSON.stringify(s, null, 1));
    const md = scoreMarkdown(s);
    // A set with near repeats flagged beside it (<set>-near-repeats.json) is also scored without them. That is a
    // measurement of how much they moved the numbers, never a second verdict: the verdict is the whole set's.
    const flagged = join(HERE, (bar.labels ?? 'labels.json').replace(/-labels\.json$/, '-near-repeats.json'));
    if (existsSync(flagged)) {
        const ids = new Set(JSON.parse(readFileSync(flagged, 'utf8')).items.map((i) => i.id));
        const k = score({ items: labels.items.filter((i) => !ids.has(i.id)) }, results, bar);
        md.push(``, `Without the ${ids.size} flagged near repeats (a measurement, not the verdict): ${k.items} items; Jev ${k.jev.counts.right ?? 0} right, ${k.harmful.jev} harmful; baseline (${k.baseline}) ${k.words.counts.right ?? 0} right, ${k.harmful.baseline} harmful; only Jev right ${k.mcnemar.onlyJevRight}, only the baseline right ${k.mcnemar.onlyBaselineRight}; exact McNemar p = ${k.mcnemar.p.toFixed(4)}.`);
    }
    writeFileSync(join(out, 'score.md'), md.join('\n') + '\n');
    console.log(md.join('\n'));
}
