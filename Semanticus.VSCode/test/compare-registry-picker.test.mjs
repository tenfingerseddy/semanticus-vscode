import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

// Compare's Published-model picker SELECTS a remembered connection record — it never accepts a typed XMLA endpoint.
// This pins the ratified rule "Add connection is the only place an endpoint is ever typed" (docs/design-records-2026-07-12.md
// T13/R8): every other surface, Compare included, picks a record. See connectionshub.tsx for the shared registry pattern.
const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const read = (file) => readFileSync(resolve(root, file), 'utf8');
const compare = read('webview/src/compare.tsx');
const hub = read('webview/src/connectionshub.tsx');   // the drawer was retired; the hub is the shared manager now
const bridge = read('webview/src/bridge.ts');
const extension = read('src/extension.ts');

// (1) The old free-text endpoint entry is gone: no endpoint-shaped placeholder, and no keystroke writes an endpoint.
// Broad patterns (not the exact former strings) so a reworded endpoint field or a renamed event arg still trips the guard.
assert.doesNotMatch(compare, /placeholder="[^"]*(?:powerbi:\/\/|xmla endpoint)/i,
  'Compare must not offer a typed XMLA endpoint field — a published model is selected from the registry');
assert.doesNotMatch(compare, /endpoint:\s*\w+\.target\.value/,
  'no keystroke may write a workspace endpoint — the endpoint comes from the selected registry record');

// (2) The picker reads the shared registry (the same op that backs the Connections drawer) and renders each record.
assert.match(compare, /rpc<ConnRecord\[\]>\('listConnections'\)/,
  'the picker must list remembered records via the shared listConnections registry op');
assert.match(compare, /function WorkspacePicker/, 'the Published-model side must be its own record-selecting picker');
assert.match(compare, /records\.map\(/, 'the picker must render one selectable row per remembered record');

// (3) Each record shows its environment chip, reusing the drawer wording (uat/prod/local, else "Production safeguards").
assert.match(compare, /r\.label \|\| 'Production safeguards'/,
  'the environment chip must reuse the manager pattern: declared label, else the fail-closed "Production safeguards"');
assert.match(hub, /text: 'Production safeguards'/,
  'the Connections hub remains the source of the "Production safeguards" fail-closed wording the picker copies');

// (4) Not-yet-remembered models are added in the shared Connections manager, not a second endpoint form here.
assert.match(compare, /\+ Add a published model/, 'the picker must offer the Add-a-published-model action');
assert.match(compare, /onAddConnection=\{openConnections\}/,
  'Add a published model must open the shared Connections manager, never grow its own form');

// (5) Add connection stays the ONE place an endpoint is typed: the hub keeps its endpoint field; Compare does not.
assert.match(hub, /data-testid="hub-endpoint-input"/, 'the Connections hub remains the only typed-endpoint surface');

// (6) The reload signal is live end to end, so a model added or forgotten in the manager reaches a mounted picker
// without a remount. Each leg is pinned so removing any one leg fails this test (the reviewed MEDIUM/LOW regression).
// (6a) The picker subscribes to the shared signal.
assert.match(compare, /onConnectionChange\(/, 'the Compare picker must reload its records on onConnectionChange');
// (6b) The manager's human add broadcasts in-webview (connect_xmla emits no activity, so nothing else relays it).
assert.match(bridge, /export function announceConnectionChange\b/, 'bridge must expose the in-webview connection-change broadcast');
assert.match(hub, /announceConnectionChange\(\)/, 'the Connections hub must broadcast after a successful "Add a published model"');
// (6c) The host relays registry mutations from EITHER door (a human forget/label emits activity too), covering the
// forget/label case the webview add-broadcast cannot see.
for (const k of ['remember_xmla_connection', 'label_connection', 'forget_connection']) {
  assert.match(extension, new RegExp(`kind === '${k}'`), `the host relay must post connectionChanged for ${k}`);
}
assert.match(extension, /registryChange[\s\S]*postToPanels\(\{ type: 'connectionChanged' \}\)/,
  'a registry mutation must reach BOTH webview panels (Studio + standalone hub) as a connectionChanged nudge');

console.log('Compare registry-picker contract tests passed');
