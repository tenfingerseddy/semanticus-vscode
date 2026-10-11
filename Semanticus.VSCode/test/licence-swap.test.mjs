// F-242 (Kane, 23 September, the twelfth drop). Remove license on the Pro page appeared to do nothing. The
// engine read the licence once, at start, and this window had adopted an engine it did not start, so the
// "restart the engine we spawned" step never ran and the page went on saying Pro. Once the engine finally
// restarted, Free showed correctly.
//
// A licence change now reaches the RUNNING engine through the person-only applyLicense RPC (the human
// governance target; no assistant door has it), which re-verifies the token and swaps the entitlement in place,
// both ways. The page then shows the tier the ENGINE reports, never an assumption. The restart is kept only as
// the fallback for an engine that cannot swap (one older than this fix, or no connection at all).
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { precheckLicense, swapLicenseInEngine } from '../out/licenseSwap.js';
import { keyResultOf, accountOf } from '../out/proPage.js';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const extension = readFileSync(resolve(root, 'src', 'extension.ts'), 'utf8');

// ---- a fake engine that behaves like the real one ------------------------------------------------------
const GOOD = 'header.payload-signed-by-the-issuer';
function fakeEngine() {
  let info = { tier: 'free', reason: 'no license', features: [] };
  const calls = [];
  return {
    calls,
    get tier() { return info.tier; },
    async applyLicense(token, verifyOnly) {
      calls.push({ token, verifyOnly });
      if (token && token !== GOOD) return { accepted: false, applied: false, reason: 'license missing or invalid: the signature did not verify against the Semanticus public key', entitlement: info };
      if (verifyOnly) return { accepted: true, applied: false, entitlement: info };
      info = token ? { tier: 'pro', licensedTo: 'buyer@contoso.com', features: ['workflows'] } : { tier: 'free', reason: 'no license', features: [] };
      return { accepted: true, applied: true, entitlement: info };
    },
  };
}
const neverRestart = async () => { throw new Error('the restart fallback must not run when the engine can swap'); };

// ---- activate then remove, with no restart --------------------------------------------------------------
{
  const engine = fakeEngine();
  const on = await swapLicenseInEngine(engine, GOOD, neverRestart);
  assert.equal(on.via, 'swap', 'activation reaches the running engine directly');
  assert.equal(on.entitlement.tier, 'pro');
  assert.equal(engine.tier, 'pro');
  assert.equal(keyResultOf(on.entitlement), 'ok', 'the page result is read from the engine answer');
  assert.equal(accountOf(on.entitlement, '', undefined, 0)?.licensedTo, 'buyer@contoso.com');

  const off = await swapLicenseInEngine(engine, '', neverRestart);
  assert.equal(off.via, 'swap', 'removal reaches the running engine directly: no restart, no reload');
  assert.equal(off.entitlement.tier, 'free');
  assert.equal(engine.tier, 'free');
  assert.equal(accountOf(off.entitlement, '', undefined, 0), undefined, 'the page shows no Pro account once the engine says free');
}

// ---- a bad token is refused and changes nothing -----------------------------------------------------------
{
  const engine = fakeEngine();
  const check = await precheckLicense(engine, 'garbage');
  assert.equal(check.refused, 'license missing or invalid: the signature did not verify against the Semanticus public key',
    "the refusal is the engine's own reason");
  assert.deepEqual(engine.calls, [{ token: 'garbage', verifyOnly: true }], 'the pre-check never swaps');
  assert.equal(engine.tier, 'free');
  assert.equal((await precheckLicense(engine, GOOD)).refused, undefined, 'a good token passes the pre-check');
  assert.equal(engine.tier, 'free', 'passing the pre-check swaps nothing either');

  // Refused at the swap too (the pre-check is the page's courtesy; the engine is the gate).
  const refused = await swapLicenseInEngine(engine, 'garbage', neverRestart);
  assert.equal(refused.via, 'swap');
  assert.equal(refused.refused, 'license missing or invalid: the signature did not verify against the Semanticus public key');
  assert.equal(refused.entitlement.tier, 'free', 'a refused token leaves the engine where it was');
}

// ---- the restart survives only as the fallback -----------------------------------------------------------
{
  let restarted = 0;
  const fallback = async () => { restarted++; };
  const older = { applyLicense: async () => { const e = new Error('No method by the name applyLicense'); e.code = -32601; throw e; } };
  const r = await swapLicenseInEngine(older, GOOD, fallback);
  assert.equal(r.via, 'fallback', 'an engine older than the swap falls back to the restart');
  assert.equal(restarted, 1);
  const none = await swapLicenseInEngine(undefined, '', fallback);
  assert.equal(none.via, 'fallback', 'with no engine connected there is nothing to swap, so the fallback runs');
  assert.equal(restarted, 2);
  assert.equal((await precheckLicense(older, 'garbage')).refused, undefined,
    'an engine that cannot check has no opinion: the old path stores and the restart reports the verdict');
}

// ---- the wiring in extension.ts --------------------------------------------------------------------------
function slice(text, startPattern, what) {
  const start = text.search(startPattern);
  assert.notEqual(start, -1, `could not find ${what}`);
  const rest = text.slice(start);
  const end = rest.slice(1).search(/\n}\n/);
  return end === -1 ? rest : rest.slice(0, end + 2);
}
const reverify = slice(extension, /async function reverifyAfterLicenseChange\(/, 'reverifyAfterLicenseChange');
assert.match(reverify, /swapLicenseInEngine\(/, 'a licence change goes to the running engine first');
assert.match(reverify, /getLicenseToken\(context, \{ reconcile: false \}\)/,
  'the engine is handed the token a restart would read, so a swap and a restart agree');
const swapAt = reverify.indexOf('swapLicenseInEngine(');
const restartAt = reverify.indexOf('restartEngineCmd(');
assert.ok(restartAt > swapAt, 'the restart is only reached from the fallback');
assert.match(reverify, /await announce\(swap\.entitlement\b/,
  'the announcement is handed the engine answer');
assert.match(reverify, /entitlementChanged/, 'Studio is told the tier moved, so the chip and locked pages re-read it');

const flow = slice(extension, /async function activateLicenseFlow\(/, 'activateLicenseFlow');
const precheckAt = flow.indexOf('precheckLicense(');
const storeAt = flow.indexOf('applyLicenseToken(');
assert.ok(precheckAt > 0 && precheckAt < storeAt, 'a pasted key is checked by the engine BEFORE anything is stored');

const announce = slice(extension, /async function announceKeyResult\(/, 'announceKeyResult');
assert.match(announce, /known \?\?/, 'the key result uses the engine answer the swap returned');

// ---- the webview hears about it ------------------------------------------------------------------------
const bridge = readFileSync(resolve(root, 'webview', 'src', 'bridge.ts'), 'utf8');
const pro = readFileSync(resolve(root, 'webview', 'src', 'pro.tsx'), 'utf8');
assert.match(bridge, /msg\.type === 'entitlementChanged'/, 'the bridge routes the host notice');
assert.match(pro, /onEntitlementChange\(/, 'the shared tier cache re-reads the engine when the tier moves');

console.log('licence-swap: ok');
