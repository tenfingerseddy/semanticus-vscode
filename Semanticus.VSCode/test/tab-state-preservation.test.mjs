// Tab-state preservation contracts (feat/tab-state-preservation).
//
// Two layers, both executing the code the webview ships:
//  1. Behavioral — the pure tab-bar busy-affordance mapping (webview/src/tabbusy.mjs), the storage-logic.mjs pattern.
//  2. Source contract — the holders that make results survive a Studio tab switch are mounted ABOVE the conditional
//     tab body (so switching tabs does not unmount them), their model/connection invalidation still fires from the
//     holder, and the wrong-to-preserve surfaces (M Code / its Profile) stay cancellation-scoped (unmounted on switch).
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { busyAffordance, PRESERVED_SURFACES } from '../webview/src/tabbusy.mjs';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const read = (p) => readFileSync(resolve(root, p), 'utf8');
const app = read('webview/src/App.tsx');
const lineage = read('webview/src/lineage.tsx');
const daxlab = read('webview/src/daxlab.tsx');

let passed = 0;
const ok = (name, fn) => { fn(); passed++; console.log('  [PASS] ' + name); };

// Understand group holds all three converted surfaces; Change holds M Code. A minimal tab->group map for the mapping.
const TAB_TO_GROUP = { diagram: 'understand', search: 'understand', lineage: 'understand', daxlab: 'understand', data: 'understand', stats: 'understand', mcode: 'change', optimize: 'change' };

// ---- busy affordance maps to the RIGHT tab ----
ok('busy surface in the OPEN group but not the active tab marks its secondary tab, never the group', () => {
  // On DAX Lab (understand open); a Storage scan is running.
  const { tabs, groups } = busyAffordance(['stats'], 'daxlab', 'understand', TAB_TO_GROUP);
  assert.deepEqual([...tabs], ['stats']);
  assert.equal(groups.size, 0);
});
ok('busy surface in a CLOSED group bubbles to that group button, not any secondary tab', () => {
  // On the M Code tab (Change open); a cloud analysis is running on Lineage (Understand, closed).
  const { tabs, groups } = busyAffordance(['lineage'], 'mcode', 'change', TAB_TO_GROUP);
  assert.equal(tabs.size, 0);
  assert.deepEqual([...groups], ['understand']);
});
ok('the ACTIVE tab never shows the busy glyph (progress is already visible inline)', () => {
  const { tabs, groups } = busyAffordance(['lineage'], 'lineage', 'understand', TAB_TO_GROUP);
  assert.equal(tabs.size, 0);
  assert.equal(groups.size, 0);
});
ok('a standalone tab (no active group) still bubbles busy surfaces to their group buttons', () => {
  // On the Workflows standalone surface (activeGroup null); a DAX Lab op runs.
  const { tabs, groups } = busyAffordance(['daxlab'], 'workflows', null, TAB_TO_GROUP);
  assert.equal(tabs.size, 0);
  assert.deepEqual([...groups], ['understand']);
});
ok('multiple busy surfaces in the open group each mark their own tab and dedupe the group', () => {
  const { tabs, groups } = busyAffordance(['stats', 'lineage'], 'daxlab', 'understand', TAB_TO_GROUP);
  assert.deepEqual([...tabs].sort(), ['lineage', 'stats']);
  assert.equal(groups.size, 0);
});
ok('an unknown surface id is ignored, never crashes the mapping', () => {
  const { tabs, groups } = busyAffordance(['ghost'], 'daxlab', 'understand', TAB_TO_GROUP);
  assert.equal(tabs.size, 0);
  assert.equal(groups.size, 0);
});

// ---- the wrong-to-preserve surface list is PINNED ----
ok('PRESERVED_SURFACES is exactly the three converted surfaces', () => {
  assert.deepEqual([...PRESERVED_SURFACES].sort(), ['daxlab', 'lineage', 'stats']);
});
ok('M Code and its Profile are NOT preserved (unmount stays a cancellation boundary, PR #246)', () => {
  assert.ok(!PRESERVED_SURFACES.includes('mcode'), 'mcode must never join the preserved surfaces');
  assert.ok(!PRESERVED_SURFACES.includes('profile'), 'the M Code Profile must never join the preserved surfaces');
});

// ---- results survive a tab switch: the holders are mounted ABOVE the conditional tab body ----
ok('all three holders wrap Shell (the tab body is a Shell child, so switching tabs cannot unmount them)', () => {
  // Providers open before <Shell ...> and close after </Shell> — the conditional `tab === ...` render lives inside.
  assert.match(app, /<LineageTabStateProvider>[\s\S]*<DaxLabTabStateProvider>[\s\S]*<StorageTabStateProvider active=\{tab === 'stats'\}>[\s\S]*<Shell[\s\S]*<\/Shell>[\s\S]*<\/StorageTabStateProvider>[\s\S]*<\/DaxLabTabStateProvider>[\s\S]*<\/LineageTabStateProvider>/,
    'the Lineage/DaxLab/Storage holders must enclose Shell so the tab-conditional body they feed cannot remount them');
});
ok('Storage activates lazily on first visit (active={tab === stats}) rather than scanning at Studio startup', () => {
  assert.match(app, /<StorageTabStateProvider active=\{tab === 'stats'\}>/);
  assert.match(app, /function StorageTabStateProvider\(\{ active, children \}[\s\S]*const \[activated, setActivated\] = useState\(active\)[\s\S]*if \(!activated\) return;/,
    'the storage holder must gate its metadata/scan effects on first activation');
});

// ---- invalidation still clears from the holder (connection/model change) ----
ok('Lineage model-identity invalidation lives in the holder and still bumps the generations + clears discovery', () => {
  const provider = lineage.slice(lineage.indexOf('export function LineageTabStateProvider'), lineage.indexOf('export function LineageView'));
  assert.match(provider, /const modelSwitched = prevIdentity\.current !== undefined && prevIdentity\.current !== ident/);
  assert.match(provider, /if \(modelSwitched\) \{[\s\S]*cloudGen\.current\+\+; localGen\.current\+\+;[\s\S]*activeCloudRunId\.current = null;[\s\S]*setReports\(null\); setSel\(new Set\(\)\); setConsent\(false\); setError\(null\); onAnalyzed\(null, null\);/,
    'a model swap must still orphan in-flight cloud/local work and clear every discovered thing');
});
ok('Lineage progress correlation stays exact and outlives the tab (holder-scoped onProgress by runId)', () => {
  const provider = lineage.slice(lineage.indexOf('export function LineageTabStateProvider'), lineage.indexOf('export function LineageView'));
  assert.match(provider, /onProgress\(\(p\) => \{[\s\S]*p\.opKey === 'analyze_cloud_reports' && p\.runId === activeCloudRunId\.current/,
    'only the holder\'s current runId may advance the visible cloud progress');
});
ok('Storage scan/enrichment invalidation stays fail-closed in the holder (session clear + connection-swap clear + reconnect)', () => {
  const provider = app.slice(app.indexOf('function StorageTabStateProvider'), app.indexOf('function StatsView'));
  assert.match(provider, /useEffect\(\(\) => \{ clearScan\(\); \}, \[session\?\.sessionId, clearScan\]\)/, 'a different editing session must drop the scan');
  assert.match(provider, /onReconnect\(\(\) => \{ reset\(\); clearScan\(\); loadStaged\(\); \}\)/, 'a reconnect must clear the scan measured against the old connection');
  assert.match(provider, /scanGen\.current\+\+;[\s\S]*persistedFor\.current = null;/, 'clearScan must invalidate in-flight work and the persistence bracket');
});

// ---- DAX Lab equivalence/result invalidation stays context-keyed in the holder ----
ok('DAX Lab reflections + the execution-context key live in the holder (results/verdict survive but stale-check unchanged)', () => {
  const provider = daxlab.slice(daxlab.indexOf('export function DaxLabTabStateProvider'), daxlab.indexOf('export function DaxLabView'));
  assert.match(provider, /const execContextKey = JSON\.stringify\(\[[\s\S]*session\?\.sessionId \?\? null, session\?\.revision \?\? null,/,
    'the execution-context key must be computed in the holder from the same identity fields');
  assert.match(provider, /useClaudeReflection\('verify_equivalence'[\s\S]*requestKey: null[\s\S]*contextKey: execContextKey/,
    'a reflected equivalence result must still stamp null provenance + the execution context so it can go stale');
});
ok('DAX Lab equivalence staleness is still DERIVED from the live context key (a swap/edit invalidates the verdict)', () => {
  assert.match(daxlab, /eqEv\.contextKey != null && eqEv\.contextKey !== execContextKey/,
    'evidence must go stale when the execution context changes, whether or not the tab was hidden');
});

// ---- M Code stays a cancellation boundary (rendered inside the tab body → unmounts on switch) ----
ok('M Code is rendered inside the Shell tab-conditional (unmounts on tab switch), never lifted to a holder', () => {
  assert.match(app, /tab === 'mcode' \? \([\s\S]*<MCodeView navTarget=\{pqTarget\} \/>/);
  assert.ok(!app.includes('MCodeTabStateProvider'), 'M Code must not gain a state-preservation holder');
});

console.log(`\nTab-state preservation contract passed (${passed} checks).`);
