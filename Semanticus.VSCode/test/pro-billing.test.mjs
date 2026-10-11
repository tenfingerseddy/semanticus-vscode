import assert from 'node:assert/strict';
import { createServer } from 'node:http';
import { existsSync, readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

// The Pro button, end to end on the parts that can be run: a stub worker on loopback answering the four
// routes from the billing contract, driven through the SHIPPED request path (src/proBilling.ts compiled to
// out/). The vscode-bound half — the quick pick, the progress notice, the URI handler registration and the
// licence storage — is asserted against the source, because it cannot be constructed outside an extension
// host. Every assertion below says which of the two it is.

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const compiled = resolve(root, 'out', 'proBilling.js');
assert.ok(existsSync(compiled), 'out/proBilling.js is missing. Run npm run compile first.');

const {
  PRODUCTION_BILLING_URL, SANDBOX_BILLING_URL, COLLECT_INTERVAL_MS, COLLECT_TIMEOUT_MS,
  newCheckoutNonce, billingEndpoint, createCheckout, portalLink, collectProToken, CollectGate,
  subscriptionIdFromToken, rememberCheckoutNonce, isKnownCheckoutNonce, forgetCheckoutNonce, NONCE_LIFETIME_MS,
  checkForRenewedToken, shouldCheckForRenewal, RENEWAL_CHECK_INTERVAL_MS, isAllowedBillingHost,
  sandboxBillingUrlFor, billingHostCheck, readBillingConfig,
} = await import('../out/proBilling.js');

// ---- the stub worker --------------------------------------------------------------------------------
// One server, scripted per test: `plan` is a queue of answers for /stripe/collect, everything else is fixed.
function stubWorker(plan) {
  const seen = { checkout: [], collect: [], portal: [], renew: [] };
  const server = createServer((req, res) => {
    const url = new URL(req.url, 'http://127.0.0.1');
    const answer = (status, body) => {
      res.writeHead(status, { 'content-type': 'application/json' });
      res.end(body === undefined ? '' : JSON.stringify(body));
    };
    if (url.pathname === '/stripe/renew') {
      seen.renew.push({ auth: req.headers.authorization ?? null });
      const next = (plan.renew ?? []).length > 1 ? plan.renew.shift() : (plan.renew ?? [{ status: 204 }])[0];
      if (next.status === 204) { res.writeHead(204); return res.end(); }
      return answer(next.status, next.body);
    }
    if (url.pathname === '/stripe/checkout' && req.method === 'POST') {
      const chunks = [];
      req.on('data', (c) => chunks.push(c));
      req.on('end', () => {
        seen.checkout.push(JSON.parse(Buffer.concat(chunks).toString('utf8')));
        answer(200, { url: 'https://checkout.stripe.com/c/pay/cs_test_probutton' });
      });
      return;
    }
    if (url.pathname === '/stripe/collect') {
      seen.collect.push(url.searchParams.get('nonce'));
      const next = plan.collect.length > 1 ? plan.collect.shift() : plan.collect[0];
      return answer(next.status, next.body);
    }
    if (url.pathname === '/stripe/portal') {
      seen.portal.push({ sid: url.searchParams.get('sid'), auth: req.headers.authorization ?? null });
      return answer(200, { url: 'https://billing.stripe.com/p/session/test_probutton' });
    }
    return answer(404, { error: 'no route' });
  });
  return new Promise((ready) => {
    server.listen(0, '127.0.0.1', () => ready({
      base: `http://127.0.0.1:${server.address().port}`,
      seen,
      close: () => new Promise((done) => server.close(done)),
    }));
  });
}

// ---- the address guard (runtime) --------------------------------------------------------------------
const SANDBOX_EXAMPLE = 'https://semanticus-billing.kane.workers.dev';
assert.equal(billingEndpoint(PRODUCTION_BILLING_URL + '/', '/stripe/checkout'), 'https://pay.semanticus.com.au/stripe/checkout');
assert.equal(billingEndpoint(SANDBOX_EXAMPLE, '/stripe/collect', { nonce: 'abc' }), SANDBOX_EXAMPLE + '/stripe/collect?nonce=abc');
assert.throws(() => billingEndpoint('http://pay.semanticus.com.au', '/stripe/checkout'), /must start with https/,
  'plain http off the loopback is a downgrade attack on a payment flow');
assert.throws(() => billingEndpoint('', '/stripe/checkout'), /No Semanticus billing address/);
assert.doesNotThrow(() => billingEndpoint('http://127.0.0.1:1234', '/stripe/checkout'),
  'loopback http is the documented exception the tests themselves run on');

// ---- the host allow-list (runtime) -------------------------------------------------------------------
// A repository's .vscode/settings.json must never choose where a licence token is sent. Two guards, and
// both have to hold: the setting is machine scoped so VS Code ignores a workspace value, AND this file
// refuses any address that is not the production worker, its documented sandbox, or loopback, whatever
// the setting says. The second guard is the one that survives a mistake in the first.
assert.equal(isAllowedBillingHost('pay.semanticus.com.au'), true, 'the production worker');
assert.equal(isAllowedBillingHost('PAY.SEMANTICUS.COM.AU'), true, 'host names are not case sensitive');
assert.equal(isAllowedBillingHost('semanticus-billing.kane.workers.dev'), true, 'the documented sandbox pattern');
assert.equal(isAllowedBillingHost('127.0.0.1'), true, 'loopback cannot leave the machine');
assert.equal(isAllowedBillingHost('localhost'), true);
assert.equal(isAllowedBillingHost('pay.semanticus.com.au.attacker.example'), false, 'a suffix is not a match');
assert.equal(isAllowedBillingHost('attacker-pay.semanticus.com.au'), false);
assert.equal(isAllowedBillingHost('attacker.workers.dev'), false, 'workers.dev is shared by everyone');
assert.equal(isAllowedBillingHost('semanticus-billing.attacker.example.com'), false);
assert.equal(isAllowedBillingHost(''), false);

// ---- the billing addresses come from ONE config block (runtime) ----------------------------------------
// package.json's "semanticusBilling" block names the production address, the worker's name and, once the
// Cloudflare account exists, its workers.dev subdomain. The sandbox address is built from those, never
// written out by hand with a placeholder a tester has to fill in.
{
  const unset = { productionUrl: 'https://pay.semanticus.com.au', workerName: 'semanticus-billing', workersDevSubdomain: '' };
  const set = { ...unset, workersDevSubdomain: 'contoso' };
  assert.equal(sandboxBillingUrlFor(unset), undefined, 'no subdomain configured means no sandbox address, not a placeholder');
  assert.equal(sandboxBillingUrlFor(set), 'https://semanticus-billing.contoso.workers.dev');
  assert.throws(() => sandboxBillingUrlFor({ ...unset, workersDevSubdomain: '<subdomain>' }), /subdomain/,
    'a placeholder left in the config is refused, not shipped');

  const openCheck = billingHostCheck(unset);
  assert.equal(openCheck('semanticus-billing.anyone.workers.dev'), true, 'unconfigured: the documented pattern, as before');
  const pinned = billingHostCheck(set);
  assert.equal(pinned('semanticus-billing.contoso.workers.dev'), true, 'configured: the account\'s own sandbox');
  assert.equal(pinned('semanticus-billing.attacker.workers.dev'), false,
    'configured: a worker of the same name on somebody else\'s account is refused');
  assert.equal(pinned('pay.semanticus.com.au'), true);
  assert.equal(pinned('127.0.0.1'), true);

  const shipped = readBillingConfig();
  assert.equal(shipped.productionUrl, PRODUCTION_BILLING_URL, 'the shipped production address is the config block');
  assert.equal(SANDBOX_BILLING_URL, sandboxBillingUrlFor(shipped), 'the shipped sandbox address is built from the config block');
  assert.ok(!String(SANDBOX_BILLING_URL ?? '').includes('<'), 'no placeholder ever reaches a shipped address');
}
assert.throws(() => billingEndpoint('https://attacker.example.com', '/stripe/renew'), /will not send your license/,
  'an address nobody documented is refused before a request is ever built');

// Nothing is sent at all, so nothing carries the bearer token. The fetch is a spy: if it is ever called
// with a hostile base, the token has already left the machine and the test has to fail.
{
  const calls = [];
  const spy = async (url, init) => { calls.push({ url, init }); return { status: 200, json: { url: 'https://evil.example/x', token: 'stolen' } }; };
  await assert.rejects(() => portalLink('https://attacker.example.com', 'payload.signature', spy), /will not send your license/,
    'the portal call must refuse the address rather than prove the licence to it');
  await checkForRenewedToken('https://attacker.example.com', 'payload.signature', spy);
  await assert.rejects(() => createCheckout('https://attacker.example.com', 'monthly', 'n'.repeat(32), spy), /will not send your license/);
  assert.equal(calls.length, 0, 'not one request left this process, so not one bearer token did either');
}

// ---- the nonce (runtime) ----------------------------------------------------------------------------
const nonce = newCheckoutNonce();
assert.equal(nonce.length, 64, '32 bytes of randomness, hex encoded, as the worker contract asks');
assert.notEqual(nonce, newCheckoutNonce(), 'two checkouts must never share a nonce');

// ---- happy path (runtime, stub worker) --------------------------------------------------------------
{
  const worker = await stubWorker({ collect: [{ status: 200, body: { token: 'payload.signature' } }] });
  const checkout = await createCheckout(worker.base, 'yearly', nonce);
  assert.equal(checkout, 'https://checkout.stripe.com/c/pay/cs_test_probutton');
  assert.deepEqual(worker.seen.checkout[0], { price: 'yearly', app: 'vscode', nonce },
    'the checkout call must carry the chosen price, the app and the nonce');
  const outcome = await collectProToken({ base: worker.base, nonce, gate: new CollectGate(), intervalMs: 5, timeoutMs: 2000 });
  assert.deepEqual(outcome, { kind: 'token', token: 'payload.signature' });
  assert.equal(worker.seen.collect[0], nonce, 'collection must be keyed on the nonce that started the checkout');
  await worker.close();
}

// ---- a checkout the worker refuses (runtime) --------------------------------------------------------
{
  const worker = await stubWorker({ collect: [{ status: 404 }] });
  await assert.rejects(() => createCheckout(worker.base + '/nowhere', 'monthly', nonce), /could not start the checkout/);
  await worker.close();
}

// ---- 425 pending, then the token (runtime) ----------------------------------------------------------
{
  const worker = await stubWorker({ collect: [
    { status: 425 }, { status: 404 }, { status: 425 },
    { status: 200, body: { token: 'payload.signature' } },
  ] });
  const outcome = await collectProToken({ base: worker.base, nonce, gate: new CollectGate(), intervalMs: 5, timeoutMs: 4000 });
  assert.deepEqual(outcome, { kind: 'token', token: 'payload.signature' });
  assert.equal(worker.seen.collect.length, 4, 'pending and not-yet-minted are both reasons to keep waiting, not to stop');
  await worker.close();
}

// ---- 410, the link was already spent (runtime) ------------------------------------------------------
{
  const worker = await stubWorker({ collect: [{ status: 410 }] });
  const outcome = await collectProToken({ base: worker.base, nonce, gate: new CollectGate(), intervalMs: 5, timeoutMs: 2000 });
  assert.deepEqual(outcome, { kind: 'gone' }, 'a spent collection link stops the poll at once');
  assert.equal(worker.seen.collect.length, 1, 'there is nothing to gain by asking a spent link twice');
  await worker.close();
}

// ---- cancel (runtime) -------------------------------------------------------------------------------
{
  const worker = await stubWorker({ collect: [{ status: 425 }] });
  const gate = new CollectGate();
  const running = collectProToken({ base: worker.base, nonce, gate, intervalMs: 50, timeoutMs: 60000 });
  setTimeout(() => gate.cancel(), 30);
  const outcome = await running;
  assert.deepEqual(outcome, { kind: 'cancelled' });
  const askedAtCancel = worker.seen.collect.length;
  await new Promise((r) => setTimeout(r, 120));
  assert.equal(worker.seen.collect.length, askedAtCancel, 'a cancelled poll must stop asking, not just stop reporting');
  await worker.close();
}

// ---- timeout (runtime) ------------------------------------------------------------------------------
{
  const worker = await stubWorker({ collect: [{ status: 425 }] });
  const outcome = await collectProToken({ base: worker.base, nonce, gate: new CollectGate(), intervalMs: 5, timeoutMs: 40 });
  assert.equal(outcome.kind, 'timeout');
  assert.equal(outcome.sawPending, true, 'a timeout after a pending session is a different story from silence');
  await worker.close();
}
{
  const worker = await stubWorker({ collect: [{ status: 404 }] });
  const outcome = await collectProToken({ base: worker.base, nonce, gate: new CollectGate(), intervalMs: 5, timeoutMs: 40 });
  assert.deepEqual(outcome, { kind: 'timeout', sawPending: false }, 'nothing ever arrived: the checkout was abandoned');
  await worker.close();
}

// ---- a worker that is down (runtime) ----------------------------------------------------------------
{
  const worker = await stubWorker({ collect: [{ status: 500 }] });
  const outcome = await collectProToken({ base: worker.base, nonce, gate: new CollectGate(), intervalMs: 2, timeoutMs: 60000 });
  assert.equal(outcome.kind, 'failed', 'five bad answers in a row is a failure, not an endless poll');
  await worker.close();
}

// ---- the return link wakes the poll early (runtime) -------------------------------------------------
// This is the mechanism the URI handler drives: without it a person who clicked "return to VS Code" still
// sits out the rest of the current three-second tick.
{
  const worker = await stubWorker({ collect: [
    { status: 425 },
    { status: 200, body: { token: 'payload.signature' } },
  ] });
  const gate = new CollectGate();
  const startedAt = Date.now();
  const running = collectProToken({ base: worker.base, nonce, gate, intervalMs: 5000, timeoutMs: 60000 });
  setTimeout(() => gate.nudge(), 40);
  assert.deepEqual(await running, { kind: 'token', token: 'payload.signature' });
  assert.ok(Date.now() - startedAt < 3000, 'the return link must not wait out the poll tick it arrived during');
  await worker.close();
}

// ---- the portal, with proof (runtime) ----------------------------------------------------------------
// The portal is opened with the LICENCE, not with a subscription id. A sid on its own is a bearer
// capability: whoever holds one could open that customer's portal and cancel their subscription. The
// worker reads the sid out of the signed claims and refuses a sid parameter outright, so this side must
// send the token and must not send the parameter at all.
{
  const worker = await stubWorker({ collect: [{ status: 404 }] });
  const licence = 'payload.signature';
  assert.equal(await portalLink(worker.base, licence), 'https://billing.stripe.com/p/session/test_probutton');
  assert.equal(worker.seen.portal[0].auth, `Bearer ${licence}`, 'the licence must travel as a bearer token');
  assert.equal(worker.seen.portal[0].sid, null, 'a sid parameter is the weaker door, and it is closed');
  await assert.rejects(() => portalLink(worker.base, '   '), /licence/i,
    'with no licence there is nothing to prove, so there is nothing to ask');
  await worker.close();
}

// ---- the renewal check (runtime) ---------------------------------------------------------------------
// A renewal cannot arrive by nonce: that ticket is one-shot and belongs to the first purchase, so a year
// later it is long spent. The app asks with the licence it holds and gets a newer one, or 204.
{
  const worker = await stubWorker({ collect: [{ status: 404 }], renew: [{ status: 200, body: { token: 'newpayload.newsig' } }] });
  const held = 'oldpayload.oldsig';
  assert.deepEqual(await checkForRenewedToken(worker.base, held), { kind: 'renewed', token: 'newpayload.newsig' });
  assert.equal(worker.seen.renew[0].auth, `Bearer ${held}`, 'the renewal check proves itself the same way the portal does');
  await worker.close();
}
{
  const worker = await stubWorker({ collect: [{ status: 404 }], renew: [{ status: 204 }] });
  assert.deepEqual(await checkForRenewedToken(worker.base, 'payload.signature'), { kind: 'current' },
    '204 means the app already holds the newest licence');
  await worker.close();
}
{
  const worker = await stubWorker({ collect: [{ status: 404 }], renew: [{ status: 200, body: { token: 'payload.signature' } }] });
  assert.deepEqual(await checkForRenewedToken(worker.base, 'payload.signature'), { kind: 'current' },
    'the same licence back is not a renewal');
  await worker.close();
}

// A refusal is the one answer worth telling a person about. It used to be swallowed with everything else,
// which is how a paying subscriber whose stored token had gone stale dropped off Pro in silence: the
// worker said 401, the editor said nothing, and nobody found out until Pro stopped working.
for (const status of [401, 403]) {
  const worker = await stubWorker({ collect: [{ status: 404 }], renew: [{ status }] });
  assert.deepEqual(await checkForRenewedToken(worker.base, 'payload.signature'), { kind: 'refused' },
    `a ${status} means the billing service would not accept this licence at all`);
  await worker.close();
}

// Everything else stays silence. This runs once a day in the background, so a worker that is down or a
// malformed answer must never become something a person has to read and can do nothing about.
for (const answer of [{ status: 404 }, { status: 500 }, { status: 200, body: {} }]) {
  const worker = await stubWorker({ collect: [{ status: 404 }], renew: [answer] });
  assert.deepEqual(await checkForRenewedToken(worker.base, 'payload.signature'), { kind: 'silent' },
    `a ${answer.status} from the renewal check must be silent, not an error`);
  await worker.close();
}
assert.deepEqual(await checkForRenewedToken('http://127.0.0.1:1/', 'payload.signature'), { kind: 'silent' },
  'a worker that cannot be reached at all is still silent');
assert.deepEqual(await checkForRenewedToken(PRODUCTION_BILLING_URL, ''), { kind: 'silent' }, 'no licence, nothing to ask');

// ---- how often to ask (runtime) ----------------------------------------------------------------------
assert.equal(RENEWAL_CHECK_INTERVAL_MS, 24 * 60 * 60 * 1000, 'once a day, as the brief asks');
assert.equal(shouldCheckForRenewal(undefined, 1_000_000), true, 'never checked means check now');
assert.equal(shouldCheckForRenewal(0, 1_000_000), true);
const day = RENEWAL_CHECK_INTERVAL_MS;
assert.equal(shouldCheckForRenewal(1_000_000, 1_000_000 + day - 1), false, 'inside the day, do not ask again');
assert.equal(shouldCheckForRenewal(1_000_000, 1_000_000 + day), true);
assert.equal(shouldCheckForRenewal(9_000_000, 1_000_000), true,
  'a clock that went backwards must not park the check forever');

// ---- the unverified subscription-id read (runtime) --------------------------------------------------
// The engine's token is base64url(payload) "." base64url(signature), payload FIRST. Reading it as a
// three-part JWT would hand back the signature, so the shape is pinned by a test.
{
  const payload = Buffer.from(JSON.stringify({ sub: 'a@example.com', tier: 'pro', sid: 'sub_1UHxLIVE', iat: 1, exp: 2 }))
    .toString('base64url');
  assert.equal(subscriptionIdFromToken(`${payload}.c2lnbmF0dXJl`), 'sub_1UHxLIVE');
  const noSid = Buffer.from(JSON.stringify({ sub: 'a@example.com', tier: 'pro' })).toString('base64url');
  assert.equal(subscriptionIdFromToken(`${noSid}.c2ln`), '', 'a Paddle-era token carries no subscription id');
  assert.equal(subscriptionIdFromToken('not-a-token'), '');
  assert.equal(subscriptionIdFromToken(''), '');
}

// ---- the issued-nonce gate on the URI handler (runtime) ---------------------------------------------
// vscode://semanticus-vscode.semanticus-vscode/pro?nonce=… is reachable from any web page, so the handler
// only acts on a nonce THIS editor issued, and only while it could still be live.
{
  let saved = {};
  const store = { get: () => saved, set: async (value) => { saved = value; } };
  const mine = newCheckoutNonce();
  assert.equal(isKnownCheckoutNonce(store, mine), false, 'nothing is collectable before a checkout starts');
  await rememberCheckoutNonce(store, mine);
  assert.equal(isKnownCheckoutNonce(store, mine), true);
  assert.equal(isKnownCheckoutNonce(store, newCheckoutNonce()), false, 'a nonce from somewhere else is refused');
  assert.equal(isKnownCheckoutNonce(store, ''), false);
  const old = newCheckoutNonce();
  await rememberCheckoutNonce(store, old, Date.now() - NONCE_LIFETIME_MS - 1000);
  assert.equal(isKnownCheckoutNonce(store, old), false, 'a link older than the poll window is stale or hostile');
  await forgetCheckoutNonce(store, mine);
  assert.equal(isKnownCheckoutNonce(store, mine), false, 'a collected nonce is spent');
}

// ---- the extension wiring (source) ------------------------------------------------------------------
const read = (file) => readFileSync(resolve(root, file), 'utf8');
const extension = read('src/extension.ts');
const pro = read('webview/src/pro.tsx');
const propreview = read('webview/src/propreview.tsx');
const pkg = JSON.parse(read('package.json'));

assert.ok(pkg.contributes.commands.some((c) => c.command === 'semanticus.upgradeToPro'),
  'Get Pro must be reachable from the command palette');
assert.ok(pkg.contributes.commands.some((c) => c.command === 'semanticus.manageSubscription'),
  'Manage subscription must be reachable from the command palette');
const billing = pkg.contributes.configuration.properties['semanticus.billingUrl'];
assert.ok(billing, 'the worker address must be a setting, not a constant');
assert.equal(billing.default, PRODUCTION_BILLING_URL, 'the shipped default must be the production worker');
// The VS Code half of the fix for the stolen-token finding. Without a scope, a repository's
// .vscode/settings.json is allowed to set this, and opening that repository would point the renewal
// check at whatever server it named, with the saved licence riding along as a bearer token.
assert.equal(billing.scope, 'machine',
  'a workspace .vscode/settings.json must never choose where the licence token is sent');
assert.match(billing.description, /sandbox/i, 'the sandbox address must be documented where a tester will look');
assert.match(billing.description, /workers\.dev/, 'a tester must be told what the sandbox address looks like');
assert.ok(!billing.description.includes('<'), 'the setting help carries no placeholder for a person to fill in');
assert.equal(PRODUCTION_BILLING_URL, 'https://pay.semanticus.com.au', 'the custom domain Kane routes in Cloudflare');
// The app and the worker must agree on the worker's name and its production route, or the sandbox check
// refuses the real sandbox and the default address points at nothing.
// The billing worker's config is private and left out of the public source mirror. The private repo (marked by its
// mirror manifest) always reads it, so a missing file still fails there.
if (existsSync(resolve(root, '../tools/release/mirror-manifest.json'))) { // mirror-optional: marks the private repo
  const toml = read('../tools/fulfillment/worker/wrangler.toml'); // mirror-optional: private billing worker config
  const name = /^name\s*=\s*"([^"]+)"/m.exec(toml);
  assert.ok(name, 'wrangler.toml names the billing worker');
  assert.equal(pkg.semanticusBilling.workerName, name[1], 'package.json and wrangler.toml name the same worker');
  assert.ok(toml.includes(`pattern = "${new URL(PRODUCTION_BILLING_URL).hostname}"`),
    'wrangler.toml routes the production address the app ships as its default');
}

assert.match(extension, /registerPersonCommand\('semanticus\.upgradeToPro'/, 'the Pro button needs a real command');
assert.match(extension, /registerPersonCommand\('semanticus\.manageSubscription'/, 'Manage subscription needs a real command');
// From 23 September 2026 Studio buys through the Semanticus Pro page, whose clicks reach the host as 'pro'
// messages (handleProPageMessage), so the licence commands left the webview allowlist.
assert.match(extension, /msg\?\.type === 'pro'\) \{ void handleProPageMessage\(/,
  'Studio reaches the purchase through the Pro page handler');
assert.match(extension, /registerUriHandler/, 'the browser must be able to hand the return link back to the editor');
assert.match(extension, /isKnownCheckoutNonce/, 'the URI handler must refuse a nonce this editor never issued');
// The quick pick and the progress notice became the Pro page (test/pro-in-app.test.mjs holds its copy): the
// plan cards carry both prices and say Stripe takes the card, and the page's Cancel stops the wait.
assert.match(extension, /case 'cancel':[\s\S]{0,200}proCollectionInFlight\?\.gate\.cancel\(\)/, 'the wait must be cancellable');
assert.match(extension, /Pro is on/, 'a confirmed subscription must say so in plain words');
// The whole point of routing through the existing flow: the epoch, the revision and the keychain hint
// machinery are what make a concurrent licence change safe, and a second storage path would not have them.
assert.match(extension, /async function upgradeToProCmd[\s\S]{0,4000}applyLicenseToken\(/,
  'a collected token must be stored through the same path Activate License uses');
// The window is wide because the clear-the-licence branch sits between the two; what matters is that the
// pasted-token path ends in the same call the Stripe path does.
assert.match(extension, /async function activateLicenseFlow[\s\S]{0,14000}applyLicenseToken\(/,
  'Activate License must still go through that one path, or the two have drifted');
assert.match(extension, /async function applyLicenseToken[\s\S]{0,3000}storeLicenseSecretVerified/,
  'the shared path is the one that bumps the epoch and verifies the read back');

// The portal is opened with the licence, never with a subscription id lifted off the token.
assert.match(extension, /async function manageSubscriptionCmd[\s\S]{0,1600}portalLink\(billingBaseUrl\(\),\s*token/,
  'Manage subscription must hand portalLink the licence token, not a bare sid');
assert.doesNotMatch(extension, /portalLink\(billingBaseUrl\(\),\s*sid\b/,
  'a bare sid is the door the worker now refuses');

// The renewal check: once a day and on activation, applied through the one storage path.
assert.match(extension, /checkForRenewedToken/, 'a renewal must be able to reach a running window');
assert.match(extension, /shouldCheckForRenewal/, 'the check is rate limited, not run on every event');
// 3000 keeps this inside checkForLicenceRenewal itself, which runs to about 3900 characters.
assert.match(extension, /async function checkForLicenceRenewal[\s\S]{0,3000}applyLicenseToken\(/,
  'a renewed licence goes through the same path Activate License and Get Pro use');
// A refusal must reach the person. Silence here is how a paying subscriber drops off Pro without ever
// being told the billing service stopped accepting the licence on their disk.
assert.match(extension, /async function checkForLicenceRenewal[\s\S]{0,2500}'refused'[\s\S]{0,600}show(Warning|Information|Error)Message/,
  'a refused renewal must be surfaced, not swallowed');
assert.match(extension, /activate[\s\S]{0,60000}checkForLicenceRenewal\(/,
  'the check has to run on activation, or a window that is already open never sees a renewal');

// A background renewal must not overwrite a newer choice a person made while it was in flight.
// The snapshot IS the guard: applyLicenseToken refuses to store when the revision or the attempt epoch
// has moved since the snapshot was opened. Taking that snapshot AFTER the network call made the guard
// useless, because the newer state became its own baseline. So the order is the fix, and the order is
// what is asserted here: snapshot, then network, then a second look at the single-flight guard.
assert.match(extension, /async function checkForLicenceRenewal[\s\S]{0,1600}openLicenseSnapshot\([\s\S]{0,900}checkForRenewedToken\(/,
  'the licence snapshot and the operation epoch must be captured BEFORE the network call');
assert.doesNotMatch(extension, /async function checkForLicenceRenewal[\s\S]{0,1600}checkForRenewedToken\([\s\S]{0,900}openLicenseSnapshot\(/,
  'a snapshot taken after the response treats the newer state as its own baseline');
assert.match(extension, /checkForRenewedToken\([\s\S]{0,1200}if \(licenseInteractiveFlowActive\) return;/,
  'the single-flight guard must be looked at again after the await, not only before it');

assert.match(pro, /export function upgradeToPro\(\)[\s\S]{0,120}navigateInStudio\('pro'\)/,
  'Studio reaches buying through the Pro page, never its own billing call');
assert.match(propreview, /data-testid="pro-preview-unlock"[\s\S]{0,160}onClick=\{upgradeToPro\}/,
  'the locked-page replica Unlock Pro action must open the Pro page');

// No billing protocol may live in the webview: the extension host owns every worker call.
for (const [name, source] of [['pro.tsx', pro], ['propreview.tsx', propreview]]) {
  assert.doesNotMatch(source, /stripe|billingUrl/i, `${name} must not carry billing protocol`);
}

console.log('Pro billing tests passed');
