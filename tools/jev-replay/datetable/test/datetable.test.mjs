// Self-test for the date.table replay: the labels, the committed bar and the scorer. No network, no key.
//
//   node --test tools/jev-replay/datetable/test/datetable.test.mjs

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

import { score, right, answerable } from '../score.mjs';

const dir = join(dirname(fileURLToPath(import.meta.url)), '..');
const labels = JSON.parse(readFileSync(join(dir, 'labels.json'), 'utf8'));
const heldout = JSON.parse(readFileSync(join(dir, 'heldout-labels.json'), 'utf8'));
const heldoutBar = JSON.parse(readFileSync(join(dir, 'heldout-bar.json'), 'utf8'));
const bar = JSON.parse(readFileSync(join(dir, 'bar.json'), 'utf8'));

test('the bar was committed before the first run and names every check', () => {
    assert.equal(bar.point, 'date.table');
    assert.equal(bar.committedBeforeFirstRun, true);
    for (const k of ['jevMin', 'beatShippedRule', 'notBelowNameRule', 'harmfulMax', 'confidenceBar']) assert.ok(k in bar, k);
});

test('the held-out bar keeps round 1 thresholds and the held-out set shares no model with round 1', () => {
    for (const k of ['jevMin', 'beatShippedRule', 'notBelowNameRule', 'harmfulMax', 'confidenceBar']) assert.equal(heldoutBar[k], bar[k], k);
    assert.equal(heldoutBar.committedBeforeFirstRun, true);
    const devIds = new Set(labels.items.map((i) => i.id));
    assert.ok(heldout.items.every((i) => !devIds.has(i.id)));
    const devShapes = new Set(labels.items.map((i) => i.tables.map((t) => t.name).sort().join('|')));
    assert.ok(heldout.items.every((i) => !devShapes.has(i.tables.map((t) => t.name).sort().join('|'))));
});

test('every label is a table of its own model, generate or none, with a mark', () => {
    assert.ok(labels.items.length >= 30);
    assert.ok(heldout.items.length >= 25);
    const ids = new Set();
    for (const it of [...labels.items, ...heldout.items]) {
        assert.ok(!ids.has(it.id), 'duplicate id ' + it.id);
        ids.add(it.id);
        const names = it.tables.map((t) => t.name);
        const t = it.label.table;
        assert.ok(t === 'generate' || t === 'none' || names.includes(t), it.id + ' labels ' + t);
        assert.equal(typeof it.label.mark, 'boolean', it.id);
        for (const f of Object.keys(it.facts)) {
            const [table, col] = f.split('/');
            const cols = it.tables.find((x) => x.name === table)?.cols ?? [];
            assert.ok(cols.includes(col + ':DateTime'), it.id + ' has a fact for ' + f + ', which is not a date column');
            assert.ok(['day', 'repeats', 'gaps'].includes(it.facts[f]), it.id + ' ' + f);
        }
    }
});

test('the labels carry no text beyond names and Contoso-style placeholders', () => {
    const text = JSON.stringify(labels);
    assert.doesNotMatch(text, /\u2014/);
});

test('right and answerable', () => {
    const row = { label: 'Date', labelMark: true, alsoOk: [], options: ['none', 'generate', 'table:Date'] };
    assert.equal(right(row, 'table:Date', true), true);
    assert.equal(right(row, 'table:Date', false), false);
    assert.equal(right(row, 'generate', true), false);
    assert.equal(answerable(row), true);
    assert.equal(answerable({ ...row, options: ['none', 'generate'] }), false);
    assert.equal(right({ label: 'none', alsoOk: ['generate'], labelMark: true }, 'generate', true), true);
});

test('the scorer applies every check of the bar', () => {
    const base = { decided: true, measured: false, options: ['none', 'generate', 'table:Date'], alsoOk: [], labelMark: true, jevConfidence: 0.9, inputTokens: 1, outputTokens: 1, ms: 1 };
    const rows = [];
    for (let i = 0; i < 10; i++) rows.push({ ...base, id: 'a' + i, label: 'Date', jev: 'table:Date', jevMark: true, shipped: null, nameRule: 'table:Date', line: { op: 'mark_date_table', target: 'table:Date', kept: true } });
    const s = score({ point: 'date.table', rows }, bar);
    assert.equal(s.jev.right, 10);
    assert.equal(s.checks.beatShippedRule, true);
    assert.equal(s.checks.notBelowNameRule, true);
    assert.equal(s.verdict, 'SHIP');

    // Tie with the name rule, and one harmful mark too many: the rule stays.
    rows[0] = { ...rows[0], jev: 'table:Other', options: [...base.options, 'table:Other'], line: { op: 'mark_date_table', target: 'table:Other', kept: true } };
    rows[1] = { ...rows[1], jev: 'table:Other', options: [...base.options, 'table:Other'], line: { op: 'mark_date_table', target: 'table:Other', kept: true } };
    const t = score({ point: 'date.table', rows }, bar);
    assert.equal(t.harmful, 2);
    assert.equal(t.checks.notBelowNameRule, false);
    assert.equal(t.verdict, 'RULE');
});

// ---- round 3 --------------------------------------------------------------------------------------------------

import { mcnemar, decision, harmfulTable } from '../score-round3.mjs';
import { deriveKey } from '../derive-key.mjs';

test('round 3: the committed key is what derive-key.mjs derives, and the bar was committed first', () => {
    const key = JSON.parse(readFileSync(join(dir, 'key.json'), 'utf8'));
    const bar3 = JSON.parse(readFileSync(join(dir, 'round3-bar.json'), 'utf8'));
    assert.equal(bar3.committedBeforeFirstRun, true);
    const items = [...labels.items, ...heldout.items];
    assert.equal(key.items.length, items.length);
    for (const [n, it] of items.entries()) {
        const { needed, table, mark } = deriveKey(it);
        assert.deepEqual([key.items[n].id, key.items[n].needed, key.items[n].table, key.items[n].mark], [it.id, needed, table, mark]);
    }
});

test('round 3: exact McNemar and the Pro path', () => {
    assert.equal(mcnemar(0, 0), 1);
    assert.ok(Math.abs(mcnemar(6, 0) - 0.03125) < 1e-9);
    assert.ok(mcnemar(5, 0) > 0.05);
    const bar3 = { confidenceBar: 0.85, minGainPoints: 5, maxP: 0.05 };
    const rows = [];
    for (let i = 0; i < 10; i++) rows.push({ key: 'a', jev: 'a', jevConfidence: 0.9, rule: 'b' });
    for (let i = 0; i < 10; i++) rows.push({ key: 'a', jev: 'b', jevConfidence: 0.5, rule: 'a' });
    const d = decision(rows, bar3);
    assert.equal(d.proPath, 20);
    assert.equal(d.rule, 10);
    assert.equal(d.fromJev, 10);
    assert.equal(d.ship, true);
});

test('Kane bar: beats the rule, not luck, no more harm; size does not matter', () => {
    const bar3 = { confidenceBar: 0.85, minGainPoints: 5, maxP: 0.05 };
    const rows = [];
    for (let i = 0; i < 7; i++) rows.push({ key: 'table:Date', jev: 'table:Date', jevConfidence: 0.9, rule: null });
    for (let i = 0; i < 93; i++) rows.push({ key: 'table:Date', jev: 'table:Date', jevConfidence: 0.9, rule: 'table:Date' });
    const d = decision(rows, bar3, harmfulTable);
    assert.equal(d.kaneOn, true);
    assert.equal(d.ship, true);
    rows[0] = { key: 'table:Date', jev: 'table:Other', jevConfidence: 0.9, rule: null };
    assert.equal(decision(rows, bar3, harmfulTable).kaneOn, false);   // one harmful mark more than the rule
    assert.equal(harmfulTable('generate', 'table:Date'), true);
    assert.equal(harmfulTable('generate', 'generate'), false);
});
