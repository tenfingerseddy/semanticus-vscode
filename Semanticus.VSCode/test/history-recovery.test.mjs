import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const read = (file) => readFileSync(resolve(root, file), 'utf8');
const history = read('webview/src/history.tsx');
const app = read('webview/src/App.tsx');
const deploy = read('webview/src/deploy.tsx');

assert.match(history, /Local file · source control/, 'Edit History must name the local checkpoint boundary');
assert.match(history, /Published model · pre-write restore points/, 'Edit History must name the published restore boundary');
assert.match(history, /createHistoryCheckpoint[^\n]+false/, 'checkpoint creation must preview before commit');
assert.match(history, /restoreHistoryCheckpoint[^\n]+false/, 'local restore must preview before confirmation');
assert.match(history, /It never pushes/, 'a local checkpoint must not imply remote publication');
assert.match(history, /rollback happens against the named published target/i, 'published rollback must not imply a local-file restore');
assert.match(app, /restoreTarget=\{deployRestoreTarget\}/, 'the exact published restore point must route into Deploy');
assert.match(deploy, /const t = restoreTarget;/, 'Deploy must capture the directed restore target as a null-narrowed local');
assert.match(deploy, /setRestoreId\(found\.some\(\(p\) => p\.id === t\.id\)/, 'Deploy must focus the directed restore point');
assert.doesNotMatch(app, /label: ['"](Checkpoint|Recovery|Restore points)['"]/, 'R7 recovery must not create a top-level tab');

console.log('Edit History durable recovery UI contract tests passed');
