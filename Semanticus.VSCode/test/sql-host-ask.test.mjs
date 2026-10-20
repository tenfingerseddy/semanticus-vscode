// A model can name a SQL server nobody has saved. The engine asks the person ONE plain question through their window and sends no sign-in
// until the answer is yes. The window answers the engine's askSqlHost request with a modal Yes / No box; closing the box is a no.
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const ext = readFileSync(resolve(root, 'src/extension.ts'), 'utf8');

assert.match(ext, /connected\.onRequest\('askSqlHost'/, 'the window must answer the engine\'s askSqlHost request');
assert.match(ext, /This model reads from \$\{host\}\. Send your sign-in there\?/, 'the question must read exactly as the engine words it');
const handler = ext.slice(ext.indexOf("connected.onRequest('askSqlHost'"));
assert.match(handler.slice(0, 600), /modal: true/, 'the question must be a modal box the person cannot miss');
assert.match(handler.slice(0, 600), /return pick === yes/, 'anything but Yes, including a closed box, is a no');
assert.ok(!/—/.test(handler.slice(0, 600)), 'no em dash in the copy');
console.log('sql-host-ask: ok');
