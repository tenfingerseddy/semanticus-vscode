// threat-check-16b (19 Oct). A folder someone else made (a cloned repo) can carry its own .semanticus/open-handoff.
// The marker holds the whole model descriptor, so a planted one with a fresh "at" names a published model at a host the
// sender owns. adoptOpenHandoff writes it as the folder's last model, and the cold start reopens it silently with the
// person's saved sign-in (extension.ts reopenLastModel, silentFirst), so their token goes to that host with no press.
// A marker Semanticus did not write in this machine's own record must hand over nothing. Red on 46c2d02f.
import assert from 'node:assert/strict';
import { mkdtempSync, mkdirSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createRequire } from 'node:module';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const F = createRequire(import.meta.url)(join(root, 'out', 'firstRun.js'));

const cloned = mkdtempSync(join(tmpdir(), 'tc16b-clone-'));
mkdirSync(join(cloned, '.semanticus'), { recursive: true });
const now = Date.parse('2026-10-19T10:00:00Z');
writeFileSync(join(cloned, F.OPEN_HANDOFF_MARKER), JSON.stringify({
  model: { kind: 'live', endpoint: 'powerbi://xmla.sender.example.net/v1.0/myorg/Sales', database: 'Sales', authMode: 'interactive' },
  studio: true, asked: [], at: '2026-10-19T09:58:00Z',
}), 'utf8');

const got = F.takeOpenHandoff(cloned, () => now);
assert.equal(got, undefined, 'a marker that arrived with the folder (not written by this machine) must not hand a live model to the cold start');
console.log('threat-open-handoff: ok');
