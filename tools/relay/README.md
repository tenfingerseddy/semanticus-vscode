# Semanticus decision relay

The hosted service that funds Jev decisions for Pro, M0b. The full design, every refusal, the ledger state
machine and the open contract points live in
[`docs/redesign/contracts/relay.md`](../../docs/redesign/contracts/relay.md). This file is the runbook.

One Cloudflare worker, no SDK, no dependencies. `POST /v1/decide` and `GET /v1/budget`.

## Layout

| File | What it holds |
|---|---|
| `worker.mjs` | the routes, the gate order, the forward, the logging and the sweep |
| `ledger.mjs` | `LicenceLedger`, the Durable Object: reserve, forward, settle, release, stick, sweep |
| `verify.mjs` | licence verification in WebCrypto, mirroring `LicenseVerifier` and the 14-day grace; a `trial: true` token gets no grace |
| `templates.mjs` | the pinned points and every hard limit |
| `test/` | the suite, its fakes, and the drift guard that reads the engine's own C# |

## The points it serves

One row per registered decision point. The bounds are the engine's, not the relay's own taste, and
`test/drift.test.mjs` reads `DecisionRegistry.cs`, `PlanRankPoint.cs` and `AskRoutePoint.cs` and fails if
this table and those files stop agreeing.

| Point | What it may send | Questions | Most in one call |
|---|---|---|---|
| `measure.folder_and_format` | the measure, its table, its siblings | `display_folder`, `format_family` | 8 |
| `plan.rank` | the model shape and the plan items | one `value:<item id>` score per item | 40 |
| `ask.route` | the words typed and nothing else | `tool` | 8 |
| `measure.starter` | one number column, its facts, the table's columns | `aggregation` | 8 |
| `column.format` | one number or date column, its facts, the table's columns | `format_family` | 8 |
| `compose.draft` | the words typed and the table names | one `step:<template>` per step or argument | 64 |

Forty for `plan.rank` is `PlanRankPoint.MaxScoredItems`, which is what `LocalEngine.Plan.cs` takes from the
open plan in one call.

## The Pro trial

A trial token (`trial: true`, minted by the billing worker's `/trial/confirm`) is refused as `expired` the
second it passes `exp`; paid tokens keep the 14-day grace. It budgets under `sid:trial_<fingerprint>`, one
ledger per trial. There is no separate trial cap today: a trial gets the same `RELAY_LICENCE_TOKENS` monthly
cap as a paid licence, and the house cap (`RELAY_LICENCE_TOKENS * RELAY_LICENCE_COUNT`) does not count trials.

## What one request reference buys

A caller's `idempotencyKey` is hashed together with the licence identity and pinned to a digest of the body
that would be forwarded. It is a record of the question, not a reservation, and every call that actually
goes out holds its own.

| The same reference, sent again | What happens |
|---|---|
| the same question, already answered | the stored answer, no call to the vendor, nothing charged |
| the same question, still running | refused, `already_in_flight` |
| the same question, nothing to serve | a new hold, a new call, charged in its own right |
| a different question | refused, `idempotency_reused` |

A stored answer is kept for 24 hours and only up to 32 KB (`LIMITS.maxCachedReplyBytes`). A bigger answer
is not kept, and the next identical ask simply pays for itself.

## Test

```bash
node --test tools/relay/test/*.test.mjs
```

Offline. A fake vendor, a fake KV and in-memory ledger objects. Nothing reaches the vendor, the house key
or Kane's account. The drift guard reads the engine's C# from the working tree, so it runs here and needs
no build. This suite is the M0b exit gate's first half; the second half is the shadow period.

## Deploy (not yet done)

Nothing below has been run. `wrangler.toml` already carries the engine's public key, the shadow period's
tiny cap and the `relay.semanticus.com.au` custom domain (so the deploy writes the DNS record itself).
What is left is the two namespace ids and the house key:

```bash
npx wrangler kv namespace create RELAY_KV          # and BILLING_KV from tools/fulfillment/worker, if not yet made
node tools/golive/golive.mjs set-kv --billing <BILLING_KV id> --relay <RELAY_KV id>
node tools/golive/golive.mjs check                 # must say "Ready to deploy"
npx wrangler secret put JEV_HOUSE_KEY              # the only credential this worker holds
npx wrangler deploy
```

`STATUS_KV` is billing's `BILLING_KV` (confirmed 21 September; see the comment in `wrangler.toml`), and
`set-kv` writes that one id into both files so the two cannot drift. `test/deploy-config.test.mjs` fails if
the route, the public key or the shared namespace stop matching what the engine and the app expect.

## Operating it

The kill switches are KV writes, read before anything is forwarded. Any non-empty value other than `0`
turns one on, so put the reason in the value.

```bash
npx wrangler kv key put --binding RELAY_KV kill:global "vendor incident 2026-10-01"
npx wrangler kv key put --binding RELAY_KV kill:point:measure.folder_and_format "bad proposals"
npx wrangler kv key delete --binding RELAY_KV kill:global
```

`wrangler tail` shows one JSON line per request: point, hashed subscription, tokens, latency, outcome. It
never shows a token, a snapshot, a question or a reply, and there is no flag that changes that.
