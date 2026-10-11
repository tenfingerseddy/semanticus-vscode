// Recomputes manifest.json's own digest and writes it back.
//
// An addition to the file list in the brief. The manifest records its digest so a bar cannot be
// moved after it was committed and still read as committed; that only works if there is one
// obvious way to reseal it after a deliberate change. Run this, then commit the manifest, then
// run the gate. Never run it to make a failing run pass.

import { readFileSync, writeFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { computeManifestDigest, validateManifest } from './lib/manifest.mjs';

const path = process.argv[2]
  ? resolve(process.argv[2])
  : resolve(fileURLToPath(new URL('.', import.meta.url)), 'manifest.json');

const manifest = JSON.parse(readFileSync(path, 'utf8'));
const before = manifest.manifestDigest;
manifest.manifestDigest = computeManifestDigest(manifest);
writeFileSync(path, JSON.stringify(manifest, null, 2) + '\n');
validateManifest(JSON.parse(readFileSync(path, 'utf8')));
console.log('sealed ' + path);
console.log('  was ' + (before || '(unset)'));
console.log('  now ' + manifest.manifestDigest);
