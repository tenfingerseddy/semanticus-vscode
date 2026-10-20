// Verifier v-fix-save-parent (17 Oct). The Studio's own "Open file or folder" button (the Connections hub, which
// "Semanticus: Open Model" opens) posts openLocalModel, and the host answers it with openFromFile() then bindGridAndTree:
// it never reaches finishOpen, so the engine's open note (a copy saved inside the model folder was left out) is dropped
// there. Only "Open Recent Model" shows it. Fails until the hub's open shows the note too.
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const extension = readFileSync(resolve(root, 'src', 'extension.ts'), 'utf8');
const start = extension.indexOf("msg?.type === 'openLocalModel'");
assert.ok(start > 0, 'the host still answers openLocalModel');
const handler = extension.slice(start, extension.indexOf('return;', start));
assert.match(handler, /finishOpen\(|\.note\b/, "the Studio's Open file or folder shows the engine's open note");
console.log('v-open-note-studio: ok');
