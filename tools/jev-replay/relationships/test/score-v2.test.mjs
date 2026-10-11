// Self-test for the bar v2 scorer: McNemar, the split units and the ship rule. No network, no key.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { mcnemar, paired, ships, units } from '../score-v2.mjs';

const here = join(dirname(fileURLToPath(import.meta.url)), '..');
const bar = JSON.parse(readFileSync(join(here, 'bar-v2.json'), 'utf8'));

test('the v2 bar was committed before its run and says what ships', () => {
    assert.equal(bar.version, 2);
    assert.equal(bar.committedBeforeAnyJevAnswerOfItsRun, true);
    assert.equal(bar.minPointsOverRule, 0.05);
    assert.equal(bar.maxP, 0.05);
});

test('exact McNemar matches known values', () => {
    assert.equal(mcnemar(0, 0), 1);
    assert.ok(Math.abs(mcnemar(10, 0) - 2 / 1024) < 1e-12);
    assert.ok(Math.abs(mcnemar(6, 1) - 0.125) < 1e-12);
});

test('a gain without significance does not ship, and neither does significance without the gain', () => {
    const u = (a, r, n) => Array.from({ length: n }, () => ({ arm: a, rule: r }));
    assert.equal(ships(bar, paired([...u(true, false, 3), ...u(true, true, 20)])), false);   // +13 points, p 0.25
    assert.equal(ships(bar, paired([...u(true, false, 12), ...u(true, true, 200)])), true);  // +5.7 points, p < 0.001
    assert.equal(ships(bar, paired([...u(true, false, 12), ...u(true, true, 300)])), false); // p fine, gain 3.8
});

test('the units split the decisions: a wrong direction is not a wrong join', () => {
    const labels = [{ from: 'S/a', to: 'A/a', active: true, direction: 'both_ways', cardinality: 'many_to_one' }];
    const rel = (direction) => [{ from: 'S/a', to: 'A/a', active: true, direction, cardinality: 'many_to_one' }];
    const model = { asks: [{ column: 'S/a' }, { column: 'S/b' }], candidates: [{ from: 'S/a', to: 'A/a' }], blend: rel('both_ways'), rule: rel('one_way') };
    const u = units(model, labels, 'blend');
    assert.deepEqual(u.d1.map((x) => [x.arm, x.rule]), [[true, true], [true, true]]);   // S/b: none is right for both
    assert.deepEqual(u.d2.map((x) => [x.arm, x.rule]), [[true, false]]);
    assert.equal(u.d0.length, 1);
});
