// Self-test for the query.summarize replay: the labels, the bar and the scorer. No network, no key.
//   node --test tools/jev-replay/query/test/query.test.mjs
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { score, scoreMarkdown, readingMatches, jevCategory, slotsMatch, mcnemarExact, nearRepeats } from '../score.mjs';

const HERE = join(dirname(fileURLToPath(import.meta.url)), '..');
const labels = JSON.parse(readFileSync(join(HERE, 'labels.json'), 'utf8'));
const bar = JSON.parse(readFileSync(join(HERE, 'bar.json'), 'utf8'));

test('the bar is Kane\'s switch-on bar (version 2), committed before run 3', () => {
    assert.equal(bar.point, 'query.summarize');
    assert.equal(bar.version, 2);
    assert.equal(bar.committedBeforeFirstRun, true);
    assert.equal(bar.mcnemarAlpha, 0.05);
    assert.equal(bar.harmfulNotAboveBaseline, true);
    assert.match(bar.supersedes, /12925fa3/);
});

test('the exact McNemar test', () => {
    assert.equal(mcnemarExact(0, 0), 1);
    assert.ok(Math.abs(mcnemarExact(10, 0) - 2 / 1024) < 1e-12);
    assert.ok(mcnemarExact(8, 2) > 0.05);   // 0.109
    assert.ok(mcnemarExact(12, 2) < 0.05);  // 0.013
    assert.equal(mcnemarExact(3, 3), 1);
});

test('about sixty labelled questions on the two fixtures, with declines and a refusal', () => {
    assert.ok(labels.items.length >= 55 && labels.items.length <= 70, String(labels.items.length));
    assert.equal(new Set(labels.items.map((i) => i.id)).size, labels.items.length);
    assert.ok(labels.items.filter((i) => i.expect === 'decline').length >= 5);
    assert.ok(labels.items.some((i) => i.refused));
    for (const i of labels.items) {
        assert.ok(['northstar', 'contoso'].includes(i.model), i.id);
        if (i.expect === 'decline') continue;
        assert.ok(i.expect.measures.length >= 1, i.id + ' names no measure');
        for (const f of i.expect.filters) assert.ok(['is', 'is_not', 'after', 'from', 'before', 'up_to'].includes(f.op), i.id);
        if (i.expect.top) assert.ok(i.expect.top.n >= 1 && i.expect.top.by.length >= 1, i.id);
    }
});

test('no real names: every item is on one of the two placeholder models, and no label carries an address', () => {
    assert.deepEqual([...new Set(labels.items.map((i) => i.model))].sort(), ['contoso', 'northstar']);
    const text = JSON.stringify(labels);
    for (const banned of ['@', 'powerbi://', 'http']) assert.ok(!text.includes(banned), banned);
});

test('a reading matches only when every slot, filter and top N matches', () => {
    const expect = { measures: [['Net Sales']], groupBy: [['Store[Region]', 'Customer[Region]']], filters: [{ column: ['Date[Year]'], op: 'is', values: ['2025'] }], top: { n: 10, by: ['Net Sales'], lowest: false } };
    const parts = { measures: ['Net Sales'], groupBy: ['Customer[Region]'], filters: [{ column: 'Date[Year]', op: 'is', values: ['2025'] }], top: { n: 10, by: 'Net Sales', lowest: false } };
    assert.equal(readingMatches(expect, parts), true);
    assert.equal(readingMatches(expect, { ...parts, measures: ['Net Sales', 'Units'] }), false);
    assert.equal(readingMatches(expect, { ...parts, filters: [] }), false);
    assert.equal(readingMatches(expect, { ...parts, top: null }), false);
    assert.equal(readingMatches(expect, { ...parts, top: { n: 10, by: 'Net Sales', lowest: true } }), false);
    assert.equal(slotsMatch([['A', 'B'], ['B']], ['B', 'A']), true);
    assert.equal(slotsMatch([['A'], ['B']], ['B', 'B']), false);
});

test('categories: unsure is never wrong, a decline that ran is wrong, a refusal a check caught is not', () => {
    const item = labels.items.find((i) => i.id === 'ns-01');
    const decline = labels.items.find((i) => i.expect === 'decline');
    assert.equal(jevCategory(item, { outcome: 'needs-you', readBy: 'jev', unsure: ['x'] }), 'unsure');
    assert.equal(jevCategory(item, null), 'missing');
    assert.equal(jevCategory(decline, { outcome: 'needs-you', unsure: ['does not fit'] }), 'right');
    assert.equal(jevCategory(decline, { outcome: 'built', parts: { measures: ['Net Sales'], groupBy: [], filters: [], top: null } }), 'wrongRan');
    assert.equal(jevCategory(item, { outcome: 'refused', parts: { measures: ['Units'], groupBy: [], filters: [], top: null } }), 'caught');
});

test('the verdict follows the bar: reliably above the baseline, and no more harmful', () => {
    const perfect = (i) => i.expect === 'decline' ? { outcome: 'needs-you', unsure: ['x'] }
        : { outcome: i.refused ? 'refused' : 'built', readBy: 'jev', parts: { measures: i.expect.measures.map((s) => s[0]), groupBy: i.expect.groupBy.map((s) => s[0]), filters: i.expect.filters.map((f) => ({ column: f.column[0], op: f.op, values: f.values })), top: i.expect.top && { n: i.expect.top.n, by: i.expect.top.by[0], lowest: i.expect.top.lowest } } };
    const rows = labels.items.map((i) => ({ id: i.id, model: i.model, ms: 100, jev: perfect(i), words_arm: { decided: false, parts: null } }));
    const s = score(labels, { rows, point: 'query.summarize' }, bar);
    assert.equal(s.jev.right, 1);
    assert.equal(s.verdict, 'LIVE');
    // The baseline right on every item: no discordant pair, so Jev is not reliably above it.
    const tie = rows.map((r) => ({ ...r, words_arm: { decided: r.jev.parts != null, parts: r.jev.parts } }));
    assert.equal(score(labels, { rows: tie, point: 'query.summarize' }, bar).verdict, 'NOT LIVE');
    // More harmful than a baseline that is never harmful: not live, however right.
    const harmful = rows.map((r, k) => (k < 2 && r.jev.parts ? { ...r, jev: { ...r.jev, parts: { ...r.jev.parts, measures: ['Nope'] } } } : r));
    assert.equal(score(labels, { rows: harmful, point: 'query.summarize' }, bar).verdict, 'NOT LIVE');
});

// ---- the split redesign's held-out set (lane m11/jev-query-v2) ------------------------------------------------

const freshBar = JSON.parse(readFileSync(join(HERE, 'fresh-bar.json'), 'utf8'));
const fresh = JSON.parse(readFileSync(join(HERE, freshBar.labels), 'utf8'));

test('the fresh bar is Kane\'s bar against the best non-Jev baseline, committed before any run', () => {
    assert.equal(freshBar.version, 3);
    assert.equal(freshBar.committedBeforeFirstRun, true);
    assert.equal(freshBar.labels, 'fresh-labels.json');
    assert.equal(freshBar.mcnemarAlpha, 0.05);
    assert.equal(freshBar.harmfulNotAboveBaseline, true);
    assert.deepEqual(Object.keys(freshBar.baselines).sort(), ['shortlist', 'words']);
});

test('about eighty fresh questions, none of them an old question, with declines and refusals', () => {
    assert.ok(fresh.items.length >= 75 && fresh.items.length <= 85, String(fresh.items.length));
    assert.equal(new Set(fresh.items.map((i) => i.id)).size, fresh.items.length);
    const old = new Set(labels.items.map((i) => i.words.trim().toLowerCase()));
    for (const i of fresh.items) assert.ok(!old.has(i.words.trim().toLowerCase()), i.id + ' repeats an old question');
    assert.ok(fresh.items.filter((i) => i.expect === 'decline').length >= 8);
    assert.ok(fresh.items.filter((i) => i.refused).length >= 2);
    assert.deepEqual([...new Set(fresh.items.map((i) => i.model))].sort(), ['contoso', 'northstar']);
    const text = JSON.stringify(fresh);
    for (const banned of ['@', 'powerbi://', 'http']) assert.ok(!text.includes(banned), banned);
    for (const i of fresh.items) {
        if (i.expect === 'decline') continue;
        assert.ok(i.expect.measures.length >= 1, i.id);
        for (const f of i.expect.filters) assert.ok(['is', 'is_not', 'after', 'from', 'before', 'up_to'].includes(f.op), i.id);
    }
});

test('the verdict is taken against the best baseline, not the weakest', () => {
    const labels2 = { items: [
        { id: 'a', expect: { measures: [['M']], groupBy: [['T[A]']], filters: [], top: null } },
        { id: 'b', expect: { measures: [['M']], groupBy: [['T[B]']], filters: [], top: null } },
    ] };
    const right = (col) => ({ decided: true, parts: { measures: ['M'], groupBy: [col], filters: [], top: null } });
    const results = { rows: [
        { id: 'a', model: 'x', jev: { outcome: 'built', readBy: 'jev', parts: { measures: ['M'], groupBy: ['T[A]'], filters: [], top: null } },
          words_arm: right('T[Z]'), shortlist_arm: right('T[A]') },
        { id: 'b', model: 'x', jev: { outcome: 'built', readBy: 'jev', parts: { measures: ['M'], groupBy: ['T[B]'], filters: [], top: null } },
          words_arm: right('T[Z]'), shortlist_arm: right('T[B]') },
    ] };
    const s = score(labels2, results, freshBar);
    assert.equal(s.baseline, 'shortlist');
    assert.equal(s.mcnemar.onlyJevRight, 0);
    assert.equal(s.verdict, 'NOT LIVE');
});

// ---- the second held-out set (160), written after the first fresh set was used up -------------------------------

const fresh2Bar = JSON.parse(readFileSync(join(HERE, 'fresh2-bar.json'), 'utf8'));
const fresh2 = JSON.parse(readFileSync(join(HERE, fresh2Bar.labels), 'utf8'));

test('the second fresh bar is the same rule on a new set, committed before any run', () => {
    assert.equal(fresh2Bar.version, 4);
    assert.equal(fresh2Bar.committedBeforeFirstRun, true);
    assert.equal(fresh2Bar.labels, 'fresh2-labels.json');
    assert.equal(fresh2Bar.mcnemarAlpha, 0.05);
    assert.equal(fresh2Bar.harmfulNotAboveBaseline, true);
    assert.equal(fresh2Bar.rule, freshBar.rule.replace('fresh-labels.json', 'fresh2-labels.json'));
});

test('160 new questions, none from the old 62 or the 80', () => {
    assert.equal(fresh2.items.length, 160);
    assert.equal(new Set(fresh2.items.map((i) => i.id)).size, 160);
    const norm = (s) => s.toLowerCase().replace(/[^a-z0-9]+/g, ' ').trim();
    const old = new Set([...labels.items, ...fresh.items].map((i) => norm(i.words)));
    assert.equal(new Set(fresh2.items.map((i) => norm(i.words))).size, 160);
    for (const i of fresh2.items) assert.ok(!old.has(norm(i.words)), i.id + ' repeats an earlier question');
    assert.ok(fresh2.items.filter((i) => i.expect === 'decline').length >= 16);
    assert.ok(fresh2.items.filter((i) => i.refused).length >= 4);
    const text = JSON.stringify(fresh2);
    for (const banned of ['@', 'powerbi://', 'http']) assert.ok(!text.includes(banned), banned);
    for (const i of fresh2.items) {
        if (i.expect === 'decline') continue;
        assert.ok(i.expect.measures.length >= 1, i.id);
        for (const f of i.expect.filters) assert.ok(['is', 'is_not', 'after', 'from', 'before', 'up_to'].includes(f.op), i.id);
    }
});

// ---- the third held-out set (168), for the reader that ships after the m11/query-split review fixes -------------

const fresh3Bar = JSON.parse(readFileSync(join(HERE, 'fresh3-bar.json'), 'utf8'));
const fresh3 = JSON.parse(readFileSync(join(HERE, fresh3Bar.labels), 'utf8'));

test('the third fresh bar is the same rule again, on a new set and a named reader, committed before any run', () => {
    assert.equal(fresh3Bar.version, 5);
    assert.equal(fresh3Bar.committedBeforeFirstRun, true);
    assert.equal(fresh3Bar.labels, 'fresh3-labels.json');
    assert.equal(fresh3Bar.mcnemarAlpha, 0.05);
    assert.equal(fresh3Bar.harmfulNotAboveBaseline, true);
    assert.equal(fresh3Bar.rule, freshBar.rule.replace('fresh-labels.json', 'fresh3-labels.json'));
    assert.match(fresh3Bar.reader, /^[0-9a-f]{8}$/);
    assert.match(fresh3Bar.ifItFails, /JevReads goes back to false/);
});

test('168 new questions, none from the old 62, the 80 or the 160', () => {
    assert.equal(fresh3.items.length, 168);
    assert.equal(new Set(fresh3.items.map((i) => i.id)).size, 168);
    const norm = (s) => s.toLowerCase().replace(/[^a-z0-9]+/g, ' ').trim();
    const old = new Set([...labels.items, ...fresh.items, ...fresh2.items].map((i) => norm(i.words)));
    assert.equal(new Set(fresh3.items.map((i) => norm(i.words))).size, 168);
    for (const i of fresh3.items) assert.ok(!old.has(norm(i.words)), i.id + ' repeats an earlier question');
    assert.ok(fresh3.items.filter((i) => i.expect === 'decline').length >= 16);
    assert.ok(fresh3.items.filter((i) => i.refused).length >= 4);
    const text = JSON.stringify(fresh3);
    for (const banned of ['@', 'powerbi://', 'http']) assert.ok(!text.includes(banned), banned);
    for (const i of fresh3.items) {
        if (i.expect === 'decline') continue;
        assert.ok(i.expect.measures.length >= 1, i.id);
        for (const f of i.expect.filters) assert.ok(['is', 'is_not', 'after', 'from', 'before', 'up_to'].includes(f.op), i.id);
    }
});

// Names and counts only. What each model really carries (Northstar: 4 of its 10 visible measures, 6 columns and 3
// tables described, one table with synonyms; contoso-ai: every one described) is proved by opening the models, in
// Semanticus.Tests/JevQueryReplay.cs. Northstar is NOT a raw model, and nothing here says it is.
test('the set spans the Northstar fixture and the Contoso model prepared for AI, with real weight on each', () => {
    const byModel = {};
    for (const i of fresh3.items) byModel[i.model] = (byModel[i.model] ?? 0) + 1;
    assert.deepEqual(Object.keys(byModel).sort(), ['contoso-ai', 'northstar']);
    assert.ok(byModel['northstar'] >= 60, String(byModel['northstar']));
    assert.ok(byModel['contoso-ai'] >= 60, String(byModel['contoso-ai']));
});

// ---- near repeats: new in the words, not in what is asked -------------------------------------------------------

const q = (id, model, words, expect) => ({ id, model, words, expect });
const salesBy = (col) => ({ measures: [['Net Sales']], groupBy: [[col]], filters: [], top: null });

test('a near repeat is the same labelled parts on the same model family, a word or two apart', () => {
    const earlier = [q('e1', 'northstar', 'net sales by region', salesBy('Store[Region]')), q('e2', 'contoso', 'revenue by city', salesBy('Geography[City]'))];
    const found = nearRepeats([
        q('a', 'northstar', 'net sales for each region', salesBy('Store[Region]')),          // filler only: 0 words apart
        q('b', 'northstar', 'show me net sales across every single store region', salesBy('Store[Region]')), // 3 apart
        q('c', 'northstar', 'net sales by region', salesBy('Store[City]')),                  // other parts
        q('d', 'contoso-ai', 'revenue by town', salesBy('Geography[City]')),                  // same family, 2 apart
        q('e', 'northstar', 'revenue by city', salesBy('Geography[City]')),                   // other family
    ], earlier);
    assert.deepEqual(found.map((x) => [x.id, x.earlierId, x.wordsDifferent]), [['a', 'e1', 0], ['d', 'e2', 2]]);
});

const fresh3Near = JSON.parse(readFileSync(join(HERE, 'fresh3-near-repeats.json'), 'utf8'));

test('the near repeats flagged beside the third set are exactly what the rule finds, and the set itself is untouched', () => {
    const earlier = [['labels.json', labels], ['fresh-labels.json', fresh], ['fresh2-labels.json', fresh2]]
        .flatMap(([f, set]) => set.items.map((i) => ({ ...i, id: f + ':' + i.id })));
    assert.deepEqual(nearRepeats(fresh3.items, earlier), fresh3Near.items);
    assert.equal(fresh3Near.items.length, 20);
    assert.equal(fresh3Near.set, 'fresh3-labels.json');
    // Flagged, not removed: every flagged id is still in the set, which is still the 168 committed before its run.
    const ids = new Set(fresh3.items.map((i) => i.id));
    for (const x of fresh3Near.items) assert.ok(ids.has(x.id), x.id);
    assert.equal(fresh3.items.length, 168);
});

// ---- the score's own words (v-query-split-targeted LOW 8) -------------------------------------------------------

test('the score names the best baseline in its discordant and harmful lines, whichever baseline won', () => {
    const s = {
        items: 100, requests: 100, inputTokens: 0, perModel: {}, latencyMs: { p50: 1, p90: 1, max: 1 },
        jev: { counts: { right: 50, wrongRan: 30 }, right: 0.5, wrongRan: 0.3 },
        words: { counts: { right: 55, wrongRan: 39 }, right: 0.55, wrongRan: 0.39 },
        baseline: 'shortlist', baselines: [{ name: 'shortlist', right: 55, harmful: 39 }, { name: 'words', right: 40, harmful: 49 }],
        mcnemar: { onlyJevRight: 7, onlyBaselineRight: 12, p: 0.36 }, harmful: { jev: 30, baseline: 39 },
        bar: { version: 2, mcnemarAlpha: 0.05 }, verdict: 'NOT LIVE',
    };
    const md = scoreMarkdown(s).join('\n');
    assert.match(md, /only the shortlist right 12;/);
    assert.match(md, /Jev 30, the shortlist 39\./);
    assert.doesNotMatch(md, /word match (right )?(12|39)/);
    const w = scoreMarkdown({ ...s, baseline: 'words' }).join('\n');
    assert.match(w, /only the word match right 12;/);
    assert.match(w, /Jev 30, the word match 39\./);
});

// ---- what the record and the README say about Northstar (v-query-split-targeted LOW 7) --------------------------

const ROOT = join(HERE, '..', '..', '..');
const readme = readFileSync(join(HERE, 'README.md'), 'utf8');
const record = readFileSync(join(ROOT, 'docs', 'redesign', 'jev-query-replay.md'), 'utf8');
const makeSource = readFileSync(join(HERE, 'northstar-data', 'make.mjs'), 'utf8');
const plain = (t) => t.replace(/\*\*/g, '').replace(/\s+/g, ' ');

test('the record and the README never call Northstar a raw model again', () => {
    for (const [name, text] of [['README.md', readme], ['jev-query-replay.md', record]]) {
        const t = plain(text);
        assert.doesNotMatch(t, /no descriptions and no synonyms/i, `${name} says Northstar has no descriptions`);
        for (const m of t.matchAll(/\b(?:a|the)\s+raw\s+model\b/gi)) {
            assert.match(t.slice(Math.max(0, m.index - 8), m.index), /\bnot\s+$/i, `${name} calls a model raw: "${t.slice(m.index - 40, m.index + 40)}"`);
        }
    }
});

test('the value files are named a smaller universe that holds every labelled value, and they do', () => {
    for (const [name, text] of [['README.md', readme], ['jev-query-replay.md', record], ['make.mjs', makeSource]]) {
        assert.match(plain(text), /smaller universe/i, `${name} no longer says the value files are a smaller universe`);
    }
    // make.mjs names every label set it covers, the third one included (LOW 9).
    for (const set of ['labels.json', 'fresh-labels.json', 'fresh2-labels.json', 'fresh3-labels.json']) assert.ok(makeSource.includes(set), `make.mjs does not name ${set}`);
    // The claim itself: every value a Northstar label filters on is in one of the columns that label accepts.
    const columns = new Map();
    const valuesOf = (ref) => {
        if (!columns.has(ref)) {
            const [, table, column] = /^(\w+)\[(\w+)\]$/.exec(ref);
            const [head, ...rows] = readFileSync(join(HERE, 'northstar-data', table + '.csv'), 'utf8').trim().split('\n');
            const at = head.split(',').indexOf(column);
            assert.ok(at >= 0, `${table}.csv has no ${column} column`);
            columns.set(ref, new Set(rows.map((r) => (r.match(/("([^"]|"")*"|[^,]*)(,|$)/g) ?? [])[at]?.replace(/,$/, '').replace(/^"|"$/g, '').replace(/""/g, '"').toLowerCase())));
        }
        return columns.get(ref);
    };
    let checked = 0;
    const missing = [];
    for (const set of [labels, fresh, fresh2, fresh3]) {
        for (const item of set.items) {
            if (item.model !== 'northstar' || typeof item.expect !== 'object') continue;
            for (const f of item.expect.filters) for (const v of f.values) {
                checked++;
                if (!f.column.some((c) => valuesOf(c).has(String(v).toLowerCase()))) missing.push(`${item.id} ${f.column.join('|')} = ${v}`);
            }
        }
    }
    assert.deepEqual(missing, []);
    assert.ok(checked > 100, `only ${checked} labelled values checked`);
});
