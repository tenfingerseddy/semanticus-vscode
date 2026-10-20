// The engine's open result can carry one plain line (a copy saved inside the model folder was left out of the open). The Studio host
// used to drop it: OpenResult had no note field, so a person opening such a folder never saw it, though it travels on the pipe.
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const extension = readFileSync(resolve(root, 'src', 'extension.ts'), 'utf8');

assert.match(extension, /interface OpenResult \{[^}]*note\?: string/, 'OpenResult carries the engine note');
const finish = extension.slice(extension.indexOf('async function finishOpen'), extension.indexOf('async function bindGridAndTree'));
assert.match(finish, /r\.note/, 'finishOpen reads the note');
assert.match(finish, /showInformationMessage\(`Opened [^`]*\$\{note\}`\)/, 'the note is shown in the same message as the open');
assert.ok(!/—/.test(finish), 'no em dash in the open message');
console.log('open-note: ok');
