import assert from 'node:assert/strict';
import { existsSync, readFileSync } from 'node:fs';
import { test } from 'node:test';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const read = (file) => readFileSync(resolve(root, file), 'utf8');
const extension = read('src/extension.ts');
const program = read('../Semanticus.Engine/Program.cs');
const compare = read('webview/src/compare.tsx');
const pro = read('webview/src/pro.tsx');
const bridge = read('webview/src/bridge.ts');

// D-019: the Pro token must not appear on the engine command line. /proc/<pid>/cmdline is world-readable.
assert.doesNotMatch(extension, /serveArgs\.push\('--license', licenseToken\)/,
  'the owner spawn must not append --license <token> to argv');
assert.doesNotMatch(extension, /args\.push\('--license', licenseToken\)/,
  'the AI Assistant entry must not append --license <token> to argv');
assert.match(extension, /ownerServeArgs\(/, 'the UI owner spawn must use the shared argv builder');
assert.match(extension, /ownerStdinPayload\(/, 'the UI owner must send the token on stdin, not argv');
assert.match(extension, /mcpLaunchArgs\(/, 'the AI Assistant entry must use the shared argv builder');
assert.match(program, /--license-stdin/, 'the engine must accept the token from stdin');
assert.doesNotMatch(program, /FromEnvironmentOrToken\(GetOpt\(args, "--license"\)\)/,
  'the engine must not take the token from the --license argv value as the primary path');
// The owner's stdin is ONE stream and the ORDER is the contract: Serve reads the UI challenge line, then hands the
// SAME reader to the token reader. Swap the two reads and the engine authenticates with the token as the challenge
// while Pro silently goes free. Serve() is not callable from a test (it binds a pipe), so the order is pinned here
// as a source contract, and the shape of the exchange itself is exercised in LicenseTokenDeliveryTests.
const challengeRead = program.indexOf('"--ui-challenge-stdin") >= 0 ? Console.In.ReadLine()');
const ownerTokenRead = program.indexOf('FromArgs(args, Console.In)');
assert.ok(challengeRead >= 0, 'Serve must read the UI challenge line from stdin');
assert.ok(ownerTokenRead >= 0, 'the owner must read its Pro token from the same stdin stream');
assert.ok(challengeRead < ownerTokenRead,
  'Serve must read the challenge BEFORE the token: one stream, challenge line first');

// D-018 / D-020: never claim the OS keychain when the OS keyring is not there to hold it.
assert.match(extension, /canUseOsKeychain\(/, 'license reads must ask whether the OS keychain is actually there');
assert.doesNotMatch(extension, /moved your Pro license into secure storage \(the OS keychain\)/,
  'the migration toast must not claim the OS keychain in a form that can sit beside a keyring error');

// D-174: the environment hint is NOT proof. 1.1.0 wrote the token through SecretStorage with no hint at all, so a
// hint that answers "no keychain" (weston + dbus-run-session lanes, a Code started from a bare session) made every
// token 1.1.0 had stored unreadable after the upgrade. Ask the store itself when the hint says no, and let that
// proven answer drive every licence decision in the file.
const extensionCode = extension.replace(/^\s*(\/\/|\/\*|\*).*$/gm, '');   // comment lines talk about the hint; only code counts

// Guard the BODY of a function, brace-balanced, not the file. An unbounded /function name[\s\S]*?needle/ runs past
// the closing brace into whatever comes next, and the "prove the store works" guard below once matched
// getLicenseToken's own read further down: a helper that never asked the store at all
// (secretStorageProof = Promise.resolve(false)) passed every assertion. Measured, not feared.
function functionBody(src, name) {
  const start = src.indexOf(`function ${name}(`);
  assert.notEqual(start, -1, `${name} must exist in extension.ts`);
  // Walk past the PARAMETER list first: an inline object type (`opts?: { reconcile?: boolean }`) contains a balanced
  // brace pair, and taking the first '{' after the name would carve out that type instead of the body.
  const paren = src.indexOf('(', start);
  let pdepth = 0;
  let close = -1;
  for (let i = paren; i < src.length; i++) {
    if (src[i] === '(') pdepth++;
    else if (src[i] === ')' && --pdepth === 0) { close = i; break; }
  }
  assert.notEqual(close, -1, `${name}'s parameter list is not balanced`);
  const open = src.indexOf('{', close);
  assert.notEqual(open, -1, `${name} must have a body`);
  let depth = 0;
  for (let i = open; i < src.length; i++) {
    if (src[i] === '{') depth++;
    else if (src[i] === '}' && --depth === 0) return src.slice(open + 1, i);
  }
  assert.fail(`${name}'s body is not brace-balanced`);
}
const store = functionBody(extensionCode, 'canUseSecretStorage');
assert.match(store, /context\.secrets\.get\(LICENSE_SECRET_KEY\)/,
  'the licence probe must actually READ the store when the hint says no, not only read the environment');
assert.ok(store.indexOf('canUseOsKeychain()') >= 0 && store.indexOf('canUseOsKeychain()') < store.indexOf('context.secrets.get('),
  'the environment hint must stay the FAST PATH, ahead of the proving read, so the common case never touches SecretStorage');
// D-174 again if a negative is remembered: one rejected read (keyring locked at launch, dbus not up yet) becomes a
// session-long "no keychain", and every later licence decision acts on it. Only the positive is stable enough to
// keep, so a negative must be re-probed.
const memoWrites = [...store.matchAll(/secret\w*\s*=\s*([^;]+);/gi)].map((m) => m[1].trim());
assert.ok(memoWrites.length > 0, 'the proven answer must be remembered for the session');
assert.deepEqual(memoWrites, ['true'],
  'only a POSITIVE may be latched: a rejected read must be re-probed, or a keyring that comes up later stays invisible');

assert.equal((extensionCode.match(/canUseOsKeychain\(\)/g) || []).length, 1,
  'the environment hint is a fast path inside canUseSecretStorage only; every licence decision must use the proven answer');
assert.doesNotMatch(extension, /if \(!canUseOsKeychain\(\)\) \{\s*return \(vscode\.workspace\.getConfiguration\('semanticus'\)\.get<string>\('licenseToken'\)/,
  'the env hint alone must never decide that a stored licence is absent: a keychain token 1.1.0 wrote must still be read');
const readToken = functionBody(extensionCode, 'getLicenseToken');
assert.match(readToken, /if \(!\(await canUseSecretStorage\(context\)\)\)[\s\S]{0,200}?get<string>\('licenseToken'\)/,
  'the plaintext fallback may only run once the store has proven it is unusable');

// D-155 is RETIRED, with the gate it guarded. Merging everything you selected in one step is FREE from
// 2026-09-15 (Kane set the line by feature), so there is no plan-shaped refusal left on the merge path and
// no click-site teaching notice to place. What replaces it is the opposite assertion: no refusal may be
// faked on a path the engine no longer refuses.
assert.doesNotMatch(compare, /data-testid="compare-pro-gated-why"/,
  'a bulk merge is free now, so the click-site upsell must be gone, not merely unreachable');
assert.doesNotMatch(compare, /isEntitlementError/,
  'compare must not soften an entitlement refusal it can no longer receive');

// D-168: Pro options shows the licence. It does not open the marketing page while Pro is active.
assert.match(bridge, /export function showLicense\(\)/, 'Studio must be able to open the licence view without a browser');
assert.match(pro, /isPro \? showLicense : manageLicense/,
  'the header button must show the licence when Pro is active and only open plans when it is not');
assert.doesNotMatch(extension, /'Pro options'[\s\S]{0,80}manageLicenseCmd/,
  'Show License must not send an active Pro subscriber to the marketing page');
assert.match(extension, /msg\?\.type === 'showLicense'[\s\S]*semanticus\.showLicense/,
  'the webview licence view must reach the native Show License command');

const packaging = read('PACKAGING.md');
assert.doesNotMatch(packaging, /\["--license", <token>\]/,
  'PACKAGING.md must not teach appending the Pro token on argv');
assert.doesNotMatch(packaging, /reliable Pro-entitlement channel/,
  'PACKAGING.md must not call --license the reliable Pro channel');
assert.match(packaging, /does not carry a Pro token/,
  'PACKAGING.md must say the generated MCP entry does not carry the token');
const repoRoot = resolve(root, '..');
const privateManifest = resolve(repoRoot, 'tools/release/mirror-manifest.json'); // mirror-optional: internal fulfilment instructions
test('private licence fulfilment instructions', { skip: !existsSync(privateManifest) && 'private instructions excluded from this source snapshot' }, () => {
const fulfillment = readFileSync(resolve(repoRoot, 'docs/subscription-fulfillment.md'), 'utf8'); // mirror-optional: private-only check
assert.doesNotMatch(fulfillment, /spawned with `--license`/,
  'subscription-fulfillment.md must not teach spawning the engine with --license <token>');
assert.doesNotMatch(fulfillment, /via the reliable\s+`--license` flag/,
  'subscription-fulfillment.md must not teach writing the token into .mcp.json via --license');
assert.doesNotMatch(fulfillment, /the `--license` CLI arg/,
  'subscription-fulfillment.md must not rank --license argv as the primary delivery path');
assert.match(fulfillment, /--license-stdin/,
  'subscription-fulfillment.md must teach stdin (--license-stdin) as the owner delivery path');
});

const delivery = await import('../out/licenseDelivery.js');
const token = 'uat-pro-token-not-a-real-secret';
assert.equal(delivery.argsContainLicenseToken(delivery.ownerServeArgs('/tmp/ws')), false);
assert.equal(delivery.argsContainLicenseToken(delivery.mcpLaunchArgs('/tmp/ws')), false);
assert.deepEqual(delivery.ownerServeArgs('/tmp/ws'), ['serve', '--workspace', '/tmp/ws', '--ui-challenge-stdin', '--license-stdin']);
assert.deepEqual(delivery.mcpLaunchArgs('/tmp/ws'), ['mcp', '--workspace', '/tmp/ws']);
assert.equal(delivery.ownerStdinPayload('challenge-value', token), 'challenge-value\n' + token + '\n');
assert.equal(delivery.ownerStdinPayload('challenge-value', ''), 'challenge-value\n\n');
assert.equal(delivery.argsContainLicenseToken(['serve', '--workspace', '/tmp/ws', '--license', token]), true);
assert.equal(delivery.canUseOsKeychain('win32', {}), true);
assert.equal(delivery.canUseOsKeychain('darwin', {}), true);
assert.equal(delivery.canUseOsKeychain('linux', {}), false);
assert.equal(delivery.canUseOsKeychain('linux', { GNOME_KEYRING_CONTROL: '/run/user/1000/keyring' }), true);
assert.equal(delivery.canUseOsKeychain('linux', { KDE_FULL_SESSION: 'true' }), true);

console.log('license truth tests passed');
