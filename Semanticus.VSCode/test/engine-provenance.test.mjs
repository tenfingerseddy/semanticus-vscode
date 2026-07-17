import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import {
  decideEngineOwner,
  engineOwnerMatches,
  resolveEngineCandidate,
  shouldAutoHealMcpEntry,
} from '../out/engineResolution.js';

const exists = (candidate) => candidate === 'C:\\extension\\engine\\Semanticus.Engine.exe'
  || candidate === 'C:\\dev\\Semanticus.Engine.dll';

assert.deepEqual(
  resolveEngineCandidate({
    mode: 'development',
    overrideDll: 'C:\\dev\\Semanticus.Engine.dll',
    bundledExecutable: 'C:\\extension\\engine\\Semanticus.Engine.exe',
    exists,
  }),
  { kind: 'dll', path: 'C:\\dev\\Semanticus.Engine.dll' },
  'an Extension Development Host must retain the explicit F5 engine override',
);

assert.deepEqual(
  resolveEngineCandidate({
    mode: 'production',
    overrideDll: 'C:\\dev\\Semanticus.Engine.dll',
    bundledExecutable: 'C:\\extension\\engine\\Semanticus.Engine.exe',
    exists,
  }),
  { kind: 'exe', path: 'C:\\extension\\engine\\Semanticus.Engine.exe' },
  'an installed extension must ignore the F5-only engine override',
);

assert.throws(
  () => resolveEngineCandidate({
    mode: 'production',
    overrideDll: 'C:\\dev\\Semanticus.Engine.dll',
    bundledExecutable: 'C:\\missing\\Semanticus.Engine.exe',
    exists,
  }),
  /bundled Semanticus engine was not found/i,
  'production must fail closed when its bundled engine is missing instead of falling back to a debug DLL',
);

const bundled = { kind: 'exe', path: 'C:\\extension\\engine\\Semanticus.Engine.exe' };
assert.equal(
  engineOwnerMatches(bundled, 'c:\\EXTENSION\\engine\\Semanticus.Engine.exe', 'win32'),
  true,
  'Windows owner-path comparison must be case insensitive',
);
assert.equal(
  engineOwnerMatches(bundled, 'C:\\Program Files\\dotnet\\dotnet.exe', 'win32'),
  false,
  'Marketplace startup must reject a live F5 engine owner',
);
assert.equal(
  decideEngineOwner(bundled, undefined, 'production', 'win32'),
  'legacy',
  'an installed extension must attach to a live legacy owner with a one-time warning',
);
assert.equal(decideEngineOwner(bundled, 'C:\\other\\engine\\Semanticus.Engine.exe', 'production', 'win32'), 'mismatch');
assert.equal(decideEngineOwner(undefined, undefined, 'development', 'win32'), 'development-fallback');
assert.equal(decideEngineOwner(undefined, undefined, 'production', 'win32'), 'unresolved');

const dev = { kind: 'dll', path: 'C:\\dev\\Semanticus.Engine.dll' };
assert.equal(
  engineOwnerMatches(dev, 'C:\\Program Files\\dotnet\\dotnet.exe', 'win32'),
  true,
  'the development host may attach to the workspace dotnet owner',
);
assert.equal(
  engineOwnerMatches(dev, 'C:\\extension\\engine\\Semanticus.Engine.exe', 'win32'),
  false,
  'the development host must not silently attach to a packaged owner either',
);
assert.equal(
  engineOwnerMatches({ kind: 'exe', path: '/opt/semanticus/engine/Semanticus.Engine' }, '/opt/semanticus/engine/Semanticus.Engine', 'linux'),
  true,
  'Unix packaged owners use case-sensitive POSIX path semantics even when the test runs on Windows',
);
assert.equal(
  engineOwnerMatches({ kind: 'dll', path: '/src/Semanticus.Engine.dll' }, '/usr/bin/dotnet', 'linux'),
  true,
  'Unix development owners are recognized by the dotnet executable',
);

const workspace = 'C:\\repos\\Contoso';
const oldGenerated = {
  command: 'C:\\Users\\Kane\\.vscode\\extensions\\semanticus-1.0.1\\engine\\Semanticus.Engine.exe',
  args: ['mcp', '--workspace', workspace, '--license', 'test-token'],
};
const newGenerated = {
  command: 'C:\\Users\\Kane\\.vscode\\extensions\\semanticus-1.0.2\\engine\\Semanticus.Engine.exe',
  args: ['mcp', '--workspace', workspace, '--license', 'new-test-token'],
};
assert.equal(shouldAutoHealMcpEntry(oldGenerated, newGenerated, 'win32'), true,
  'activation must refresh a generated bundled entry after an extension upgrade');
assert.equal(shouldAutoHealMcpEntry(newGenerated, newGenerated, 'win32'), false,
  'activation must not rewrite an already-current entry');
assert.equal(shouldAutoHealMcpEntry({ ...oldGenerated, env: { CUSTOM: '1' } }, newGenerated, 'win32'), false,
  'activation must preserve hand-augmented MCP entries');
assert.equal(shouldAutoHealMcpEntry(
  { ...oldGenerated, args: [...oldGenerated.args, '--custom-flag', 'keep-me'] },
  newGenerated,
  'win32'), false,
  'activation must preserve generated-looking entries with custom arguments');
assert.equal(shouldAutoHealMcpEntry(
  { ...oldGenerated, args: ['mcp', '--workspace', 'C:\\repos\\Other'] },
  newGenerated,
  'win32'), false,
  'activation must not rewrite another workspace entry');
assert.equal(shouldAutoHealMcpEntry(
  { command: 'dotnet', args: ['C:\\dev\\Semanticus.Engine.dll', 'mcp', '--workspace', workspace] },
  newGenerated,
  'win32'), false,
  'activation must not replace a hand-authored development entry');

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const source = readFileSync(resolve(root, 'src', 'extension.ts'), 'utf8');
assert.match(
  source,
  /await autoHealSemanticusMcpEntry\(context\);[\s\S]*?await connectEngine\(context\)/,
  'activation must run the constrained MCP-entry repair before connecting the engine',
);
assert.match(
  source,
  /const rebuild = context\.extensionMode === vscode\.ExtensionMode\.Development[\s\S]*?rebuildEngineOnRestart/,
  'Restart Engine must rebuild source only inside an Extension Development Host',
);

const manifest = JSON.parse(readFileSync(resolve(root, 'package.json'), 'utf8'));
assert.match(
  manifest.contributes.configuration.properties['semanticus.engineDll'].description,
  /Installed extensions ignore this setting/u,
  'the settings UI must state the production boundary plainly',
);

console.log('engine provenance tests passed');
