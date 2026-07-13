import assert from 'node:assert/strict';
import { migrateLegacyXmlaEntries } from '../out/legacyXmlaMigration.js';

const entries = [
  { endpoint: 'powerbi://workspace/one', database: 'One', authMode: 'interactive' },
  { endpoint: 'powerbi://workspace/two', database: 'Two', authMode: 'azcli' },
  { endpoint: 'powerbi://workspace/three', database: 'Three', authMode: 'unknown' },
];

const attempted = [];
let cleared = false;
await assert.rejects(() => migrateLegacyXmlaEntries(entries, async (record, mode) => {
  attempted.push([record.endpoint, mode]);
  if (record.database === 'Two') throw new Error('fixture import failure');
}, async () => { cleared = true; }), /fixture import failure/);
assert.deepEqual(attempted, [
  ['powerbi://workspace/one', 'interactive'],
  ['powerbi://workspace/two', 'azcli'],
]);
assert.equal(cleared, false, 'the legacy key must survive any partial import failure');

attempted.length = 0;
const migrated = await migrateLegacyXmlaEntries(entries, async (record, mode) => {
  attempted.push([record.endpoint, mode]);
}, async () => { cleared = true; });
assert.equal(migrated, 3);
assert.equal(cleared, true);
assert.deepEqual(attempted[2], ['powerbi://workspace/three', 'azcli']);

console.log('legacy XMLA migration behavioral tests passed');
