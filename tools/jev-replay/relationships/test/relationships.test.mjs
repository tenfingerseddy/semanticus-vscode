// Self-test for the relationship.join replay: the labels, the bar and the scorer. No network, no key.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { measure, verdict, score } from '../score.mjs';

const here = join(dirname(fileURLToPath(import.meta.url)), '..');
const labels = JSON.parse(readFileSync(join(here, 'labels.json'), 'utf8'));
const fresh = JSON.parse(readFileSync(join(here, 'labels-fresh.json'), 'utf8'));
const bar = JSON.parse(readFileSync(join(here, 'bar.json'), 'utf8'));

test('the bar was committed before the first run and names its point', () => {
    assert.equal(bar.point, 'relationship.join');
    assert.equal(bar.committedBeforeFirstRun, true);
    assert.ok(bar.exactF1Min > 0 && bar.joinPrecisionMin > 0 && bar.mustBeatRule === true);
});

test('every label names a column of its own model, and no model repeats a from column', () => {
    let n = 0;
    const ids = new Set(labels.models.map((m) => m.id));
    for (const m of fresh.models) assert.ok(!ids.has(m.id), 'the fresh set reuses the model ' + m.id);
    for (const m of labels.models.concat(fresh.models)) {
        const seen = new Set();
        for (const l of m.labels) {
            n++;
            assert.ok(!seen.has(l.from.toLowerCase()), m.id + ' labels ' + l.from + ' twice');
            seen.add(l.from.toLowerCase());
            assert.ok(['one_way', 'both_ways'].includes(l.direction));
            assert.ok(['many_to_one', 'one_to_one'].includes(l.cardinality));
            if (m.tables) {
                for (const ref of [l.from, l.to]) {
                    const [t, c] = ref.split('/');
                    const table = m.tables.find((x) => x.name === t);
                    assert.ok(table && table.columns.some((x) => x.name === c), m.id + ' has no column ' + ref);
                }
            } else assert.ok(m.bim, m.id + ' has neither tables nor a bim');
        }
    }
    assert.ok(n >= 80, n + ' labelled relationships');
});

test('the scorer counts exact matches and the verdict needs Jev to beat the rule', () => {
    const l = [{ from: 'S/a', to: 'A/a', active: true, direction: 'one_way', cardinality: 'many_to_one' },
               { from: 'S/b', to: 'B/b', active: false, direction: 'one_way', cardinality: 'many_to_one' }];
    const right = measure(l, l);
    assert.equal(right.exact.f1, 1);
    const flipped = measure(l, [{ ...l[0] }, { ...l[1], active: true }]);
    assert.equal(flipped.join.f1, 1);
    assert.equal(flipped.exact.hits, 1);
    assert.equal(verdict(bar, right, right).pass, false, 'a tie with the rule is not a win');
    assert.equal(verdict(bar, right, flipped).pass, true);
});

test('score reads a results file shaped like the replay writes it', () => {
    const m = labels.models[0];
    const results = { ranUtc: 'x', requests: 1, inputTokens: 1, models: [{ id: m.id, asked: 1, candidates: [], jev: m.labels.map((x) => ({ ...x, kept: true, word: 'sure' })), rule: [] }] };
    const s = score({ ...labels, models: [m] }, results, bar);
    assert.equal(s.jev.exact.f1, 1);
    assert.equal(s.verdict.pass, true);
});
