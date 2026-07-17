import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const read = (file) => readFileSync(resolve(root, file), 'utf8');
const view = read('webview/src/mcode.tsx');
const transform = read('webview/src/mtransform.ts');
const pq = read('webview/src/pqtransforms.tsx');

assert.match(view, /rangeStart \? 'updateNamedExpression' : 'createNamedExpression'/, 'RangeStart must be created or upgraded');
assert.match(view, /rangeEnd \? 'updateNamedExpression' : 'createNamedExpression'/, 'RangeEnd must be created or upgraded');
assert.match(view, /label: 'Add range filter'/, 'the missing partition filter must have a direct action');
assert.match(view, /setIncrementalRefreshPolicy[\s\S]*form\.mode, pollingExpressionForSave\(form\.polling\), true\)/, 'saving must preserve polling independently of disclosure state and opt into engine auto-wiring');
assert.match(transform, /incrementalRefreshFilter[\s\S]*Source = \$\{m\.trim\(\)\}/, 'a bare source must be wrapped explicitly');
assert.match(transform, /appendStep\(source, 'Filtered for incremental refresh'/, 'range wiring must use the non-clobbering appendStep kernel');
assert.match(transform, />= RangeStart and \$\{c\} < RangeEnd/, 'the generated filter must be half-open');

// Resource-keyed busy map: unrelated resources may run together, while each action is guarded by its resource
// key and only the running control swaps to progressive copy. Errors stay adjacent to the failed control.
assert.match(view, /type BusyMap = Record<string, RefreshBusyOp>/, 'busy must be a resource-keyed map, not a boolean or flat union');
assert.match(view, /const \[busyByResource, setBusyByResource\] = useState<BusyMap>\(\{\}\)/, 'the keyed map must drive render state');
assert.match(view, /const busyRef = useRef<BusyMap>\(\{\}\)/, 'a synchronous keyed ref must close the second-click window');
assert.match(view, /if \(busyRef\.current\[key\]\) return false;/, 'beginBusy must reject re-entry for the same resource');
for (const [key, label] of [['save-policy', 'Applying…'], ['remove-policy', 'Removing…'], ['create-params', 'Creating…'], ['add-filter', 'Adding…']]) {
  assert.ok(view.includes(`'${key}'`), `${key} must retain its own action key`);
  assert.ok(view.includes(label), `${key} must swap its label to '${label}' while running`);
}
assert.match(view, /policyBusy === 'save-policy' \? 'Applying…' : 'Save policy'/, 'the Save button label must swap only for its own action');
assert.match(view, /policyBusy === 'remove-policy' \? 'Removing…' : 'Remove policy'/, 'the Remove button label must swap only for its own action');
assert.match(view, /actionErrors\[`save-policy:\$\{selected\}`\]/, 'save errors must render beside Save policy');
assert.match(view, /actionErrors\[`remove-policy:\$\{selected\}`\]/, 'remove errors must render beside Remove policy');

// Second-click guards + the table-context token: BOTH save and remove refuse a re-entry while busy, and both
// apply their table-scoped continuations only if the selection still matches the table captured at dispatch
// (switching A→B mid-save must not write A's policy state into B's UI).
const saveBlock = view.slice(view.indexOf('const save = useCallback'), view.indexOf('const remove = useCallback'));
const removeBlock = view.slice(view.indexOf('const remove = useCallback'), view.indexOf('if (err)'));
assert.match(saveBlock, /if \(busyRef\.current\[busyKey\][\s\S]*\) return;/, 'save must guard conflicts through its resource key');
assert.match(saveBlock, /if \(!beginBusy\(busyKey, 'save-policy'\)\) return;/, 'save must atomically claim its resource');
assert.match(removeBlock, /if \(!beginBusy\(busyKey, 'remove-policy'\)\) return;/, 'remove must atomically claim its resource');
assert.match(saveBlock, /const t = selected;/, 'save must capture the table at dispatch');
assert.match(saveBlock, /if \(selectedRef\.current === t\) \{ setPolicy\(p\); setEnabled/, "save's policy continuation applies only to the still-selected table");
assert.match(removeBlock, /if \(selectedRef\.current === t\) \{ setEnabled\(false\); setPolicy\(null\); setForm\(DEFAULT_FORM\); \}/, "remove's state reset applies only to the still-selected table");

// Profile fault isolation (defect: ONE query over ≤12 columns — one bad column nuked the whole strip, and the
// error was swallowed). Now: IFERROR-guarded chunked probing with a solo-probe bisection, per-column "n/a" +
// reason, an honest footer, staleness checks between queries, and connection-vs-column error classification.
assert.match(pq, /const PROFILE_CHUNK = \d+/, 'columns must be profiled in chunks so a failing chunk isolates');
assert.match(pq, /failed\[one\.name\] = msg \|\| skipReason;/, 'a solo-probe failure must capture the real error as the per-column reason');
assert.match(pq, /type ProfileStatus = 'idle' \| 'running' \| 'results' \| 'failed' \| 'stale'/, 'Profile must have an explicit lifecycle');
assert.match(pq, /const reason = profile\.failed\[c\.name\];/, 'the strip must read a per-column failure map');
assert.match(pq, /className="font-semibold">n\/a<\/div>[\s\S]*title=\{reason\}>\{reason\}/, 'a skipped column must render n/a and a visible reason');
assert.match(pq, /<span>\{failedCount\} failed<\/span>/, 'the footer must state how many columns failed');
assert.match(pq, /<span>\{notProfiledCount\} not profiled/, 'the footer must separate columns that were not attempted');
assert.match(pq, /Retry skipped columns/, 'failed-column details must offer an explicit retry');
assert.match(pq, /Profile needs a live connection/, 'the disabled offline action must explain why');
assert.match(pq, /const startProfile = async/, 'Profile must run only through an explicit action');
assert.ok(!pq.includes('profileOn'), 'the mystery Profile toggle and its automatic effect must be gone');
assert.ok(!/function profileDax\(/.test(pq), 'the single all-columns profileDax must be gone');
// Staleness + classification: a stale run stops issuing queries at its NEXT check, and a connection-shaped
// failure stops the profile (strip-level error) instead of fanning out a per-column query storm.
assert.ok((pq.match(/if \(!isCurrent\(\)\) return null;/g) || []).length >= 4, 'staleness must be checked between EVERY query, not once at the end');
assert.match(pq, /if \(connectionShaped\(chunkError\)\) return strip\(chunkError\);/, 'a connection-shaped chunk failure stops the profile with no solo fan-out');
assert.match(pq, /if \(connectionShaped\(msg\)\) return strip\(msg\);/, 'a connection-shaped solo failure stops the remaining probes too');
assert.match(pq, /const markProfileStale = useCallback\([\s\S]*profReq\.current\.cancel\(\);[\s\S]*status: 'stale'/, 'staleness must abort the in-flight run and retain visibly stale results');
assert.match(pq, /profileTableRef\.current = tableName; profReq\.current\.cancel\(\); setProfileDetails\(false\); commitProfile\(idleProfile\(\)\)/, 'changing tables must abort and clear Profile results');
assert.match(pq, /const name = bareColumnName\(c\.name\);/, 'sample-derived headers, profile keys, and DAX refs use the normalized bare model name');

// Dual identity: M operates on the PARTITION-OUTPUT name (SourceColumn), which can differ from the model
// Name — the generators must receive mName, live sample columns resolve it via the doc model, and a column
// with no doc match STANDS DOWN instead of writing M against a guessed name.
assert.match(pq, /mName\?: string/, 'GridCol must carry the M-side identity beside the model name');
assert.match(pq, /mName: resolveMName\(name, docColumns\)/, 'live sample columns resolve mName from the doc model');
assert.match(pq, /gen\.removeColumns\(mText, \[srcName\]\)/, 'M generators receive the SourceColumn name, not the model name');
assert.match(pq, /gen\.renameColumn\(mText, srcName, to\)/, 'rename writes M against the SourceColumn name');
assert.match(pq, /M transforms need the column's source name; not available for this column\./, 'an unresolvable column shows the honest stand-down note');
assert.ok(!/gen\.\w+\(mText, \[?col\.name/.test(pq), 'no generator may receive the model name directly');
// Calculated columns STAND DOWN (no mName): they are absent from the partition output, so no M may be written
// against them. A wire-declared null sourceColumn never falls back to the model name; only an ABSENT field
// (an older engine that predates it) may, and only for data columns. ONE shared resolver (sourceMName) owns
// that rule for BOTH the doc-column mapping and the range-filter generation.
assert.match(view, /mName: sourceMName\(c\)/, 'the doc columns compute mName through the shared resolver');
assert.match(pq, /export function sourceMName\(/, 'the source-name rule is a single exported resolver');
assert.match(pq, /This column is calculated in the model; M transforms do not apply\./, 'the stand-down note explains the calculated case specifically');
assert.match(view, /!c\.isCalculated && \/date\|time\/i/, 'the refresh date-column select filters out calculated columns (the engine rejects them for the same reason)');
assert.match(view, /c\.table === selected && !c\.isCalculated\)/, 'the fallback column list for the date select filters out calculated columns too');
// MAJOR 1: the "Add range filter" prerequisite action resolves the SourceColumn via the SAME resolver and
// REFUSES (honest toast) when it's unknown — it must never fall back to the model name (the exact M the engine
// Save path rejects). The old unsafe `sourceColumn || form.dateColumn` fallback is gone.
assert.match(view, /const mDateCol = col \? sourceMName\(col\) : undefined;/, 'the range filter resolves the SourceColumn via the shared resolver');
assert.match(view, /if \(!mDateCol\) throw new Error\(/, 'the range filter refuses when the source name is unknown (no model-name fallback)');
assert.ok(!/sourceColumn \|\| form\.dateColumn/.test(view), 'the unsafe model-name fallback is gone');
// MAJOR 2: an already-wired table shows the field the partition really filters on (parsed engine-side into the
// policy DTO), so changing the selector can't silently disagree with the M.
assert.match(view, /wiredDateField\?: string \| null/, 'the policy DTO carries the wired source field');
assert.match(view, /hintText=\{policy\?\.wiredDateField/, 'the UI surfaces the wired date field with the selector');
// …and the wired field INITIALIZES the selector: resolve it BACK through the table's columns via the SAME
// sourceMName rule; a UNIQUE match seeds an UNTOUCHED selector (never a user choice — the onChange marks the
// touch, reset per table); ambiguous or no match leaves the selector alone with the note.
assert.match(view, /const dateColTouched = useRef\(false\);/, 'a touched flag guards the wired seed');
assert.match(view, /dateColTouched\.current = false;/, 'the touched flag resets when the table changes');
assert.match(view, /dateColTouched\.current = true; set\('dateColumn'/, 'a user pick marks the selector touched');
assert.match(view, /if \(!wiredColumn \|\| dateColTouched\.current\) return;/, 'the wired seed never overrides a user choice');
assert.match(view, /c\.table === selected && sourceMName\(c\) === wf/, 'the wired field resolves back through the shared source-name rule');
assert.match(view, /matches\.length === 1 \? matches\[0\]\.name : null/, 'only a UNIQUE back-mapping claims the wired column');
// The wired column is ALWAYS offered, even when its type falls outside the date/time heuristic (an integer
// date-key): it is by definition the valid range-filter column, and omitting it made unrelated policy edits
// unsavable (the engine rightly rejects every other column).
assert.match(view, /wiredColumn && !base\.includes\(wiredColumn\) \? \[\.\.\.base, wiredColumn\] : base/, 'the wired column joins the picker options when the type heuristic excludes it');
assert.match(view, /\{dateColumnOptions\.map\(/, 'the selector renders the augmented option list');
// A warning-tier save (the engine saved WITH an advisory) surfaces the advisory instead of swallowing it.
assert.match(view, /r\?\.warning \? /, 'the save toast carries the engine advisory when present');
const wire = read('webview/src/wire.ts');
assert.match(wire, /sourceColumn\?: string \| null/, 'the wire ColumnRow must expose sourceColumn (engine ColumnRow DTO)');

// Selection token is synchronous: every selection path funnels through a setter that updates the ref in the
// SAME tick (a passive effect leaves a pre-render window where a continuation still sees the old table).
assert.match(view, /const setSelected = useCallback\(\(t: string\) => \{ selectedRef\.current = t; setSelectedState\(t\); \}/, 'selection updates the token ref synchronously');
assert.ok(!view.includes('useEffect(() => { selectedRef.current = selected; }'), 'the passive ref-sync effect must be gone');

// --- extract-and-execute (the bridge-timeouts pattern): run the REAL builders, not just pattern-match them ---
const extractFn = (name) => {
  const m = pq.match(new RegExp(`function ${name}\\(([^)]*)\\)[^{]*\\{([\\s\\S]*?)\\n\\}`));
  assert.ok(m, `${name} must remain statically extractable`);
  const params = m[1].split(',').map((p) => p.split(':')[0].trim()).filter(Boolean);
  const body = m[2].replace(/: string\[\]/g, '');   // the one TS annotation inside a body
  return { params, body };
};
const PROFILE_ERR = Number(pq.match(/const PROFILE_ERR = (-?\d+)/)[1]);
const makeFn = (name, prelude = '') => {
  const { params, body } = extractFn(name);
  return Function(...params, `"use strict"; ${prelude}\n${body}`);
};
const bareColumnName = makeFn('bareColumnName');
const profileChunkDax = makeFn('profileChunkDax', `const PROFILE_ERR = ${PROFILE_ERR};`);
const profileScalar = makeFn('profileScalar');
const connectionShaped = makeFn('connectionShaped');
const resolveMName = makeFn('resolveMName');
const sourceMName = makeFn('sourceMName');

// The shared source-name rule, executed (not just pattern-matched): the four legs of the mName discipline that
// both the doc-column mapping and the range-filter generation now depend on.
assert.equal(sourceMName({ name: 'ShipDate', isCalculated: true, sourceColumn: 'ship_dt' }), undefined, 'a calculated column has no M-side identity (stands down)');
assert.equal(sourceMName({ name: 'ShipDate' }), 'ShipDate', 'an ABSENT sourceColumn field (older engine) falls back to the model name');
assert.equal(sourceMName({ name: 'ShipDate', sourceColumn: null }), undefined, 'a wire-declared null NEVER falls back to the model name');
assert.equal(sourceMName({ name: 'ShipDate', sourceColumn: '' }), undefined, 'a wire-declared empty NEVER falls back to the model name');
assert.equal(sourceMName({ name: 'ShipDate', sourceColumn: 'ship_dt' }), 'ship_dt', 'a populated sourceColumn resolves to the partition-output name');

// Error classification: identifiers are stripped BEFORE matching, so a column named after a connection word
// cannot take the strip-level exit; genuine connection shapes (incl. HTTP 5xx "unavailable") still classify.
assert.equal(connectionShaped("Column 'Connection Timeout' cannot be found in table 'Sales'"), false, 'an identifier-containing bind error stays column-shaped');
assert.equal(connectionShaped('The [SessionId] column does not exist in the table'), false, 'a bracketed ref naming a connection word stays column-shaped');
assert.equal(connectionShaped('The expression contains a syntax error'), false, 'a plain bind error stays column-shaped');
assert.equal(connectionShaped('503 Server Unavailable'), true, 'HTTP 5xx unavailability is connection-shaped');
assert.equal(connectionShaped('The connection was lost during the request'), true, 'a lost connection is connection-shaped');
assert.equal(connectionShaped('Not connected. Call connect_xmla or connect_local first.'), true, 'the engine not-connected refusal is connection-shaped');
assert.equal(connectionShaped('The request timed out after 30000 ms'), true, 'a timeout is connection-shaped');
assert.equal(connectionShaped('Column [Session]] Connection Lost] cannot be aggregated'), false, 'an ]]-escaped identifier strips whole and stays column-shaped');
assert.equal(connectionShaped('The connection to the server was lost'), true, 'the connection verb may sit a few words downstream');
assert.equal(connectionShaped('syntax error at line 503'), false, 'a bare 5xx number is not a status');
// minor 3: explicit socket/transport phrases (the classic Windows/.NET wordings) classify as connection-shaped.
assert.equal(connectionShaped('No connection could be made because the target machine actively refused it'), true, 'the WinSock refusal wording is connection-shaped');
assert.equal(connectionShaped('The server did not respond'), true, 'an unresponsive server is connection-shaped');
assert.equal(connectionShaped('The remote endpoint reset the connection mid-request'), true, 'a remote-endpoint failure is connection-shaped');
assert.equal(connectionShaped("Column 'Remote Endpoint Flag' cannot be found"), false, 'a column merely NAMED after a socket phrase stays column-shaped (identifiers strip first)');

// mName resolution: doc match yields the SourceColumn; no match yields undefined (the ops stand down).
assert.equal(resolveMName('SalesAmount', [{ name: 'SalesAmount', mName: 'sales_amount_usd' }]), 'sales_amount_usd', 'a doc match resolves to the SourceColumn');
assert.equal(resolveMName('ETag', [{ name: 'SalesAmount', mName: 'sales_amount_usd' }]), undefined, 'no doc match yields undefined so the M ops stand down');

// Label normalization: ADOMD-qualified → bare model name; bare passes through; ]] un-escapes.
assert.equal(bareColumnName('Sales[Amount]'), 'Amount', 'qualified ADOMD label yields the bare column name');
assert.equal(bareColumnName('Amount'), 'Amount', 'a bare doc-model name passes through');
assert.equal(bareColumnName('Sales[Weight [kg]]]'), 'Weight [kg]', 'an escaped ] inside the column name un-escapes');
assert.equal(bareColumnName('Sales Data[a]]b]'), 'a]b', 'escape handling holds mid-name');
assert.equal(bareColumnName('[Total]'), 'Total', 'an unqualified bracketed label strips too');

// The normalized name composes into VALID table-qualified DAX (the original defect built 'Sales'[Sales[Amount]]]).
const daxQ = profileChunkDax('Sales', [{ name: bareColumnName('Sales[Amount]'), i: 0, numeric: true }]);
assert.ok(daxQ.includes(`IFERROR(DISTINCTCOUNT('Sales'[Amount]), ${PROFILE_ERR})`), 'normalized label composes into a valid ref, IFERROR-guarded');
assert.ok(daxQ.includes(`IFERROR(COUNTBLANK('Sales'[Amount]), ${PROFILE_ERR})`), 'null-count guarded on the same valid ref');
assert.ok(daxQ.includes(`"mn0", IFERROR(MIN('Sales'[Amount]), BLANK())`), 'numeric min guarded, BLANK on error');
assert.ok(!daxQ.includes('[Sales[Amount]'), 'the qualified label is never embedded verbatim');
const daxEsc = profileChunkDax("O'Brien", [{ name: 'a]b', i: 2, numeric: false }]);
assert.ok(daxEsc.includes("'O''Brien'[a]]b]"), 'table quotes and column brackets re-escape on output');

// BLANK-omission rule: a null (DAX BLANK) min/max slot is ABSENT, never coerced to 0.
const rs = { columns: [{ name: '[mn0]' }, { name: '[d0]' }], rows: [[null, 5]] };
assert.ok(Number.isNaN(profileScalar(rs, 'mn0')), 'BLANK (null) is absent, never 0');
assert.equal(profileScalar(rs, 'd0'), 5, 'a real value survives, bracket-insensitive');
assert.ok(Number.isNaN(profileScalar(rs, 'missing')), 'a missing slot is absent');
assert.equal(profileScalar({ columns: [{ name: 'mx0' }], rows: [[0]] }, 'mx0'), 0, 'a legitimate zero is kept');

// The uishot mock must stay faithful to ADOMD (qualified names) so a normalization regression is VISIBLE.
const harness = read('tools/uishot/harness.html');
assert.match(harness, /t \+ '\[' \+ c\.name\.replace\(\/\\\]\/g, '\]\]'\) \+ '\]'/, 'the previewTable mock must return QUALIFIED names like real ADOMD');

console.log('incremental refresh auto-wire + keyed-busy + profile fault-isolation UI contract tests passed');
