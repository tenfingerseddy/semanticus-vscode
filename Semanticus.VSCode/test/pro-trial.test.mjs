// The free Pro month (Kane, 11 Oct 2026; trial mockup option B).
//
// Three kinds of check:
//   1. The PROTOCOL, executed: src/proTrial.ts (compiled to out/) against a stub worker on loopback that answers
//      /trial/start and /trial/claim the way the contract (section A) says.
//   2. The page's DECISIONS, executed: webview/src/propage-model.ts transpiled and run (which view, the ending line,
//      the tally words, the first-week list).
//   3. The WIRING, read off the source: the token goes through applyLicenseToken, the start is one flow with two doors (the
//      page button and the assistant's start_trial), a locked button's press runs once Pro is on.
import assert from 'node:assert/strict';
import { createServer } from 'node:http';
import { existsSync, readFileSync } from 'node:fs';
import { dirname, resolve, join } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { createRequire } from 'node:module';
import { mkdtempSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
assert.ok(existsSync(resolve(root, 'out', 'proTrial.js')), 'out/proTrial.js is missing. Run npm run compile first.');
const T = await import(pathToFileURL(resolve(root, 'out', 'proTrial.js')).href);
const { CollectGate } = await import(pathToFileURL(resolve(root, 'out', 'proBilling.js')).href);

let passed = 0;
async function test(name, fn) { await fn(); passed++; console.log(`  ok  ${name}`); }

// ---- 1. the protocol, against a stub worker ---------------------------------------------------------------
function stubWorker(script) {
  const seen = { start: [], claim: [] };
  const server = createServer((req, res) => {
    const url = new URL(req.url, 'http://127.0.0.1');
    const answer = (status, body) => { res.writeHead(status, { 'content-type': 'application/json' }); res.end(body === undefined ? '' : JSON.stringify(body)); };
    if (url.pathname === '/trial/start' && req.method === 'POST') {
      let body = '';
      req.on('data', (c) => { body += c; });
      req.on('end', () => { const j = JSON.parse(body); seen.start.push(j); const a = script.start(j); answer(a.status, a.body); });
      return;
    }
    if (url.pathname === '/trial/claim') {
      seen.claim.push(url.searchParams.get('nonce'));
      const a = script.claim.length > 1 ? script.claim.shift() : script.claim[0];
      return answer(a.status, a.body);
    }
    answer(404, { error: 'nf' });
  });
  return new Promise((r) => server.listen(0, '127.0.0.1', () => r({ base: `http://127.0.0.1:${server.address().port}`, seen, close: () => server.close() })));
}

await test('start: posts the email and nonce, and "sent" comes back', async () => {
  const w = await stubWorker({ start: () => ({ status: 200, body: { state: 'sent' } }), claim: [{ status: 425 }] });
  const nonce = T.newTrialNonce();
  assert.match(nonce, /^[A-Za-z0-9_-]{22,128}$/, 'the nonce fits the worker NONCE_RE');
  assert.equal(await T.startTrial(w.base, 'ana@contoso.com', nonce), 'sent');
  assert.deepEqual(w.seen.start, [{ email: 'ana@contoso.com', nonce }]);
  w.close();
});

await test('start: "used" is an answer, not an error', async () => {
  const w = await stubWorker({ start: () => ({ status: 200, body: { state: 'used' } }), claim: [{ status: 404 }] });
  assert.equal(await T.startTrial(w.base, 'ana@contoso.com', T.newTrialNonce()), 'used');
  w.close();
});

await test('start: 400 and 429 become plain sentences', async () => {
  for (const [status, words] of [[400, /does not look right/], [429, /Wait a minute/], [500, /could not start just now \(500\)/]]) {
    const w = await stubWorker({ start: () => ({ status }), claim: [{ status: 404 }] });
    await assert.rejects(T.startTrial(w.base, 'ana@contoso.com', T.newTrialNonce()), (e) => words.test(e.message) && !/—/.test(e.message));
    w.close();
  }
});

await test('claim: waits through 425 and 404, then the token, on /trial/claim with the same nonce', async () => {
  const w = await stubWorker({ start: () => ({ status: 200, body: { state: 'sent' } }),
    claim: [{ status: 404 }, { status: 425 }, { status: 425 }, { status: 200, body: { token: 'tok.sig' } }] });
  const gate = new CollectGate();
  const out = await T.collectTrialToken({ base: w.base, nonce: 'n'.repeat(30), gate, intervalMs: 5 });
  assert.deepEqual(out, { kind: 'token', token: 'tok.sig' });
  assert.equal(w.seen.claim.length, 4);
  assert.ok(w.seen.claim.every((n) => n === 'n'.repeat(30)));
  w.close();
});

await test('claim: 410 is gone, cancel stops at once', async () => {
  const w = await stubWorker({ start: () => ({ status: 200 }), claim: [{ status: 410 }] });
  assert.equal((await T.collectTrialToken({ base: w.base, nonce: 'n'.repeat(30), gate: new CollectGate(), intervalMs: 5 })).kind, 'gone');
  w.close();
  const w2 = await stubWorker({ start: () => ({ status: 200 }), claim: [{ status: 425 }] });
  const gate = new CollectGate();
  const run = T.collectTrialToken({ base: w2.base, nonce: 'n'.repeat(30), gate, intervalMs: 60000 });
  setTimeout(() => gate.cancel(), 30);
  assert.equal((await run).kind, 'cancelled');
  w2.close();
});

// F-290: the claim survives a reload. The slot is a Map standing in for SecretStorage, so a second "window" sees what the first saved.
function slotOver(map) {
  return { get: async () => map.get('k'), set: async (v) => { map.set('k', v); }, delete: async () => { map.delete('k'); } };
}
await test('a pending claim saved before a reload is collected after it', async () => {
  const w = await stubWorker({ start: () => ({ status: 200, body: { state: 'sent' } }), claim: [{ status: 425 }, { status: 200, body: { token: 'tok.sig' } }] });
  const backing = new Map();
  const nonce = T.newTrialNonce();
  await T.savePendingClaim(slotOver(backing), { nonce, email: 'ana@contoso.com', startedAt: Date.now() });
  // the reload: a new slot over the same storage, no memory of the first window
  const out = await T.resumeTrialClaim(slotOver(backing), { base: w.base, gate: new CollectGate(), intervalMs: 5 });
  assert.equal(out.kind, 'token');
  assert.equal(out.token, 'tok.sig');
  assert.equal(out.claim.email, 'ana@contoso.com');
  assert.ok(w.seen.claim.every((n) => n === nonce), 'polled with the saved nonce');
  assert.equal(backing.size, 0, 'the claim is cleared once collected');
  w.close();
});
await test('a claim the worker says is gone is cleared; a cancel keeps it; nothing saved resumes nothing', async () => {
  const w = await stubWorker({ start: () => ({ status: 200 }), claim: [{ status: 410 }] });
  const backing = new Map();
  await T.savePendingClaim(slotOver(backing), { nonce: T.newTrialNonce(), email: 'ana@contoso.com', startedAt: Date.now() });
  assert.equal((await T.resumeTrialClaim(slotOver(backing), { base: w.base, gate: new CollectGate(), intervalMs: 5 })).kind, 'gone');
  assert.equal(backing.size, 0);
  w.close();
  const w2 = await stubWorker({ start: () => ({ status: 200 }), claim: [{ status: 425 }] });
  await T.savePendingClaim(slotOver(backing), { nonce: T.newTrialNonce(), email: 'ana@contoso.com', startedAt: Date.now() });
  const gate = new CollectGate();
  const run = T.resumeTrialClaim(slotOver(backing), { base: w2.base, gate, intervalMs: 60000 });
  setTimeout(() => gate.cancel(), 30);
  assert.equal((await run).kind, 'cancelled');
  assert.equal(backing.size, 1, 'a cancelled wait keeps the claim: the link may still be pressed');
  w2.close();
  const empty = new Map();
  assert.equal((await T.resumeTrialClaim(slotOver(empty), { base: 'http://127.0.0.1:1', gate: new CollectGate(), intervalMs: 5 })).kind, 'none');
});
await test('a claim older than the email link is dropped, and so is an unreadable one', async () => {
  const backing = new Map();
  const old = Date.now() - T.PENDING_CLAIM_LIFETIME_MS - 1000;
  await T.savePendingClaim(slotOver(backing), { nonce: T.newTrialNonce(), email: 'ana@contoso.com', startedAt: old });
  assert.equal(await T.loadPendingClaim(slotOver(backing)), undefined);
  assert.equal(backing.size, 0);
  backing.set('k', '{not json');
  assert.equal(await T.loadPendingClaim(slotOver(backing)), undefined);
  assert.equal(backing.size, 0);
});

await test('the address check still holds: a foreign host is refused before anything is sent', async () => {
  await assert.rejects(T.startTrial('https://example.com', 'ana@contoso.com', T.newTrialNonce()), /will not send/);
});

await test('email shape, phase, ending words, the offer rule and its own count', async () => {
  assert.equal(T.trialEmailOf('  ana@contoso.com '), 'ana@contoso.com');
  assert.equal(T.trialEmailOf('ana'), undefined);
  assert.equal(T.trialPhaseOf({ tier: 'pro', isTrial: true, trialDaysLeft: 16 }), 'on');
  assert.equal(T.trialPhaseOf({ tier: 'pro', isTrial: true, trialDaysLeft: 3 }), 'ending');
  assert.equal(T.trialPhaseOf({ tier: 'free', trialEnded: true }), 'ended');
  assert.equal(T.trialPhaseOf({ tier: 'pro' }), 'none');
  assert.equal(T.trialEndsWords(3), 'Pro trial ends in 3 days.');
  assert.equal(T.trialEndsWords(1), 'Pro trial ends tomorrow.');
  assert.equal(T.shouldOfferTrial({ tier: 'free', handChanges: 24 }), true);
  assert.equal(T.shouldOfferTrial({ tier: 'free', handChanges: 3 }), false, 'needs a real count');
  assert.equal(T.shouldOfferTrial({ tier: 'free', handChanges: 24, offered: true }), false, 'once, ever');
  assert.equal(T.shouldOfferTrial({ tier: 'free', handChanges: 24, trialEnded: true }), false);
  assert.equal(T.shouldOfferTrial({ tier: 'pro', handChanges: 24 }), false);
  assert.match(T.trialOfferWords(24).text, /You applied 24 changes by hand\. Try Pro free for 30 days/);
  assert.equal(T.handChangesIn({ origin: 'human', deltas: [{}, {}, {}] }), 3);
  assert.equal(T.handChangesIn({ origin: 'agent', deltas: [{}] }), 0, 'the assistant is not the person');
  assert.equal(T.handChangesIn({ origin: 'human', replay: 'undo', deltas: [{}] }), 0, 'undo is not a new change');
});

// ---- 2. the page's decisions -------------------------------------------------------------------------------
const req = createRequire(join(root, 'webview', 'package.json'));
const ts = req('typescript');
const src = readFileSync(join(root, 'webview', 'src', 'propage-model.ts'), 'utf8');
const js = ts.transpileModule(src, { compilerOptions: { module: ts.ModuleKind.ESNext, target: ts.ScriptTarget.ES2020 } }).outputText;
const dir = mkdtempSync(join(tmpdir(), 'trialm-'));
writeFileSync(join(dir, 'm.mjs'), js);
const M = await import(pathToFileURL(join(dir, 'm.mjs')).href);

await test('the page picks the right trial view', async () => {
  assert.equal(M.trialViewOf('free', {}, undefined, false), 'offer');
  assert.equal(M.trialViewOf('free', {}, { step: 'waiting' }, false), 'waiting');
  assert.equal(M.trialViewOf('free', {}, { step: 'used' }, false), 'used');
  assert.equal(M.trialViewOf('free', { trialEnded: true }, undefined, false), 'end');
  assert.equal(M.trialViewOf('free', { trialEnded: true }, undefined, true), 'used', 'the end card put away: plain words and Buy Pro');
  assert.equal(M.trialViewOf('pro', { isTrial: true }, undefined, false), 'month');
  assert.equal(M.trialViewOf('pro', { isTrial: false }, undefined, false), 'none');
});

await test('the ending line shows only in the last three days', async () => {
  assert.equal(M.trialEndingLine({ tier: 'pro', isTrial: true, trialDaysLeft: 16 }), null);
  assert.equal(M.trialEndingLine({ tier: 'pro', isTrial: true, trialDaysLeft: 3 }), 'Pro trial ends in 3 days.');
  assert.equal(M.trialEndingLine({ tier: 'pro', isTrial: false, trialDaysLeft: 3 }), null);
});

await test('tally words, the span, and the first-week list ticks from true counts', async () => {
  assert.equal(M.countWords(1240), '1,240');
  assert.equal(M.trialSpan(Date.parse('2026-10-11T00:00:00Z') / 1000, Date.parse('2026-11-10T00:00:00Z') / 1000), '11 October to 10 November 2026');
  const start = Date.parse('2026-10-11T00:00:00Z') / 1000;
  const week = M.firstWeekOf({ startedAt: start, changesApplied: 38, steps: 1, jevChoices: 0, workflowsRun: 0 }, start + 86400);
  assert.deepEqual(week.map((i) => i.done), [true, false]);
  assert.equal(M.firstWeekOf({ startedAt: start, changesApplied: 38, steps: 1 }, start + 8 * 86400), null, 'gone after day 7');
  assert.equal(M.firstWeekOf({ startedAt: start, changesApplied: 38, steps: 1, jevChoices: 2, workflowsRun: 1 }, start + 86400), null, 'gone when both are done');
});

// ---- 3. the wiring, read off the source ---------------------------------------------------------------------
const ext = readFileSync(join(root, 'src', 'extension.ts'), 'utf8');
const flow = ext.slice(ext.indexOf('async function startTrialFlow'), ext.indexOf('function noteHandChanges'));
await test('the trial token goes through applyLicenseToken, the same path as a key and a purchase', async () => {
  assert.match(flow, /applyLicenseToken\(context, outcome\.token\.trim\(\), opened, canStore\)/);
  assert.match(flow, /reverifyAfterLicenseChange/);
});
await test('starting a trial has two doors and one flow: the page button and the assistant (F-293)', async () => {
  assert.match(ext, /case 'trialStart':\s*await beginTrial\(context, msg\.email\);/, 'the page button runs beginTrial');
  assert.match(ext, /onNotification\('host\/startTrial', .*beginTrial\(extCtx, v\?\.email\)/, 'the engine asks the window the same way');
  const begin = ext.slice(ext.indexOf('async function beginTrial'), ext.indexOf('async function startTrialFlow'));
  assert.match(begin, /trialEmailOf\(rawEmail\)/);
  assert.match(begin, /startTrialFlow\(context, email\)/);
  // the assistant's yes is checked in the engine, before the notification is ever sent: start_trial refuses without personSaidYes
  const engine = readFileSync(join(root, '..', 'Semanticus.Engine', 'LocalEngine.HostRequests.cs'), 'utf8');
  assert.ok(engine.indexOf('if (!personSaidYes)') < engine.indexOf('PublishHostStartTrial'), 'no yes, no email');
});
await test('the assistant sets personality, sounds and volume through the window, which also reports them (F-292)', async () => {
  assert.match(ext, /onNotification\('host\/settings', .*applyHostPersonality/);
  assert.match(ext, /sendRequest\('reportHostSettings', \.\.\.personalityReport\(\)\)/);
  const ps = readFileSync(join(root, 'src', 'personalitySettings.ts'), 'utf8');
  assert.match(ps, /export async function applyHostPersonality[\s\S]*writePersonalitySetting\(\{ key: 'personality'/, 'one write path for the page and the assistant');
});
await test('the pending claim is saved before the wait, cleared on cancel, and picked up when the engine connects (F-290)', async () => {
  assert.match(flow, /savePendingClaim\(/, 'saved once the email is sent');
  assert.ok(flow.indexOf('savePendingClaim(') < flow.indexOf('collectTrialToken('), 'saved before the wait starts');
  assert.match(ext, /case 'trialCancel':[\s\S]{0,200}clearPendingClaim/, 'cancel forgets it');
  assert.match(ext, /void resumePendingTrial\(/, 'resumed on start');
});
await test('a locked button press runs once Pro is on', async () => {
  const pro = readFileSync(join(root, 'webview', 'src', 'pro.tsx'), 'utf8');
  const page = readFileSync(join(root, 'webview', 'src', 'propage.tsx'), 'utf8');
  const plan = readFileSync(join(root, 'webview', 'src', 'plan.tsx'), 'utf8');
  assert.match(pro, /export function takePendingRun/);
  assert.match(page, /if \(tier !== 'pro'\) return;\s*const pending = takePendingRun\(\);/);
  assert.match(plan, /upgradeToPro\(\{ label: 'Run the plan', tab: 'plan', run: afterUnlock \?\? \(\(\) => undefined\) \}\)/, 'hero 25 H3: the press that asked for Pro is worked out after the unlock, never run');
});
await test('trial copy has no em dash and never says one click', async () => {
  for (const f of [join(root, 'src', 'proTrial.ts'), join(root, 'webview', 'src', 'trialline.tsx')]) {
    const text = readFileSync(f, 'utf8');
    assert.doesNotMatch(text, /—/);
    assert.doesNotMatch(text, /one click/i);
  }
  const page = readFileSync(join(root, 'webview', 'src', 'propage.tsx'), 'utf8');
  const trialPart = page.slice(page.indexOf('// ---- the free month'), page.indexOf('function Adds'));
  assert.doesNotMatch(trialPart, /—|one click/i);
});

console.log(`pro-trial: ${passed} passed`);
