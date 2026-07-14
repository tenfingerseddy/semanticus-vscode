# Semanticus fulfillment Worker

A Cloudflare Worker that turns a Paddle sale into a delivered Pro license with no human in the loop:

```
Paddle (transaction.completed) ──POST /paddle──► verify HMAC signature
    → mint the license token (WebCrypto ECDSA P-256 — same wire format the engine verifies offline)
    → email it to the buyer via Resend (owner BCC'd)
    → record the sale in KV   (GET /pending lists anything undelivered)
```

Design invariants:
- **The engine never changes.** It ships only the public key and verifies offline; this Worker holds the
  private key as a Cloudflare secret and is the only online component that can mint.
- **Never lose a sale.** Mint + KV record always happen; delivery is best-effort with status recorded.
  Without a `RESEND_API_KEY` the Worker still works — tokens queue as `pending-delivery` on `/pending`
  for manual sending (the launch-week mode).
- **Idempotent, but self-healing.** Paddle retries webhooks; a duplicate of a *delivered* sale is
  acknowledged and skipped. A duplicate of an **undelivered** sale re-runs completion (resolve the email,
  mint if missing, deliver if now possible) — so the Paddle dashboard's "replay" button doubles as the
  manual recovery tool.
- **Proven compatible.** `node selftest.mjs` round-trips tokens against the real .NET verifier
  (`Semanticus.License verify`) in both directions and proves tampering fails. Run it after touching
  `mint.mjs` or the engine's `LicenseVerifier`.

## Deploy runbook (once, ~15 minutes)

From this directory (`tools/fulfillment/worker/`):

1. **Login:** `npx wrangler login` (opens the browser; use the Cloudflare account that owns semanticus.com.au).
2. **KV:** `npx wrangler kv namespace create FULFILL_KV` → paste the printed `id` into `wrangler.toml`.
3. **Deploy:** `npx wrangler deploy` → note the Worker URL (`https://semanticus-fulfillment.<acct>.workers.dev`).
4. **Paddle destination:** Paddle dashboard → Developer Tools → Notifications → **New destination** →
   URL = `<worker URL>/paddle`, type Webhook, events = **transaction.completed** only. Save, then copy the
   destination's **secret key** (`pdl_ntfset_…`).
5. **Paddle API key:** Developer Tools → Authentication → API keys → create one (read access to Customers
   is all it's used for). The webhook payload carries only a `customer_id` — the buyer's email comes from
   this lookup, unless the checkout passes `customData: { email }` (which then wins; today the site's
   overlay checkout does not collect an email before opening, so the lookup is the production path).
6. **Secrets** (each command prompts for a paste — values never touch a file or shell history):
   ```
   npx wrangler secret put PADDLE_WEBHOOK_SECRET
   npx wrangler secret put PADDLE_API_KEY
   npx wrangler secret put SEMANTICUS_SIGNING_KEY   # the PKCS8 base64 PRIVATE key — MUST be the pair of
                                                    # LicenseEntitlement.EmbeddedPublicKey in the shipped engine
   npx wrangler secret put ADMIN_KEY                # any long random string, e.g. `openssl rand -hex 32`
   npx wrangler secret put RESEND_API_KEY           # optional — add when Resend is set up (step 8)
   ```
7. **Verify:** Paddle → Developer Tools → Notifications → the destination → **Send test event**
   (transaction.completed). Then `curl -H "Authorization: Bearer <ADMIN_KEY>" <worker URL>/pending` —
   the simulated sale should appear (status `pending-delivery` or a mint record; simulator payloads may
   lack a real customer).
8. **Resend (direct-to-buyer email):** resend.com → sign up (free tier: 100/day) → Domains → add
   `semanticus.com.au` → add the DKIM/SPF records it lists to the Cloudflare DNS zone → verified → create
   an API key → `npx wrangler secret put RESEND_API_KEY` → `npx wrangler deploy`. Until then, watch
   `/pending` after any sale and send the queued email by hand.

## Operations

- **A sale arrives:** buyer gets the token email automatically; the owner address gets a BCC.
- **Something looks off:** `GET /pending` (Bearer `ADMIN_KEY`) lists every record not marked `delivered` —
  including `mint-failed` (with the error) and `pending-delivery` (with the ready-to-send token). Keep the
  key itself out of shell history/transcripts (e.g. in `~/.semanticus/fulfillment-admin.key` and
  `curl -H "Authorization: Bearer $(cat ~/.semanticus/fulfillment-admin.key)" <worker URL>/pending`).
- **Watching it live:** `npx wrangler tail` from this directory. Every terminal branch logs one line —
  `paddle: fulfilling` / `re-fulfilling` / `duplicate` / `ignored` / `rejected — <reason>`, then
  `fulfill: recorded <key> <status>` — so a silent 200 always has an explanation. Ids only; tokens, keys,
  and email addresses are never logged.
- **Recovering a stuck sale:** Paddle dashboard → the notification → **Replay**. A replay of an
  undelivered record re-resolves the email, re-mints if the identity improved, and re-attempts delivery;
  a replay of a delivered record is a no-op (`duplicate`).
- **Renewals:** each renewal fires its own `transaction.completed` → a fresh token is emailed. The engine's
  14-day grace window covers payment-retry lag around the boundary.
- **Key rotation:** mint a new keypair (`Semanticus.License keygen`), ship the new public key in an engine
  update, `wrangler secret put SEMANTICUS_SIGNING_KEY` with the new private key. Old tokens keep verifying
  only if the old public key is retained engine-side — otherwise subscribers get fresh tokens on renewal.

## Files

- `worker.mjs` — the Worker (webhook verify, fulfillment, delivery, admin).
- `mint.mjs` — pure WebCrypto minter/verifier shared with the selftest.
- `selftest.mjs` — cross-language proof vs the .NET verifier; `SEMANTICUS_MINT_CONFIG=Verify node selftest.mjs`
  on a dev box where Release bins are locked.
- `wrangler.toml` — bindings + public config; secrets are never in the repo.

## Troubleshooting (field notes, 2026-07-14)

- **Webhook returns 200 but "nothing happens":** check KV first — `npx wrangler kv key list
  --namespace-id <id> --remote`. A 200 with no fulfillment logs usually means the record ALREADY exists
  and the request ended at the duplicate check (or, before the 404 hardening, a mistyped path was being
  ACKed by the catch-all route). It is not a routing or observability failure.
- **`email: null` on records:** the Customers API lookup failed; the record's `error` field now says why
  (e.g. `customer lookup HTTP 403`). A 403 with a known-good key is almost always a corrupted secret —
  interactive `wrangler secret put` pastes can pick up stray whitespace; re-put the secret by piping the
  exact value. The Worker also `trim()`s every secret defensively.
- **Local debugging:** put the secrets in `.dev.vars` (git-ignored — NEVER commit), `npx wrangler dev`,
  and POST a signed payload: `Paddle-Signature: ts=<unix now>;h1=<hex HMAC-SHA256(secret, "<ts>:<raw body>")>`.
  The replay window is 300 s, so the `ts` must be fresh.
- **Token compatibility:** any change to `mint.mjs` or the engine verifier → `node selftest.mjs`
  (round-trips against the real .NET `Semanticus.License verify`). A minted token can also be checked
  one-off: `dotnet run --project ../../../Semanticus.License -c Release -- verify --pub <embedded public
  key> --token <token>`.
