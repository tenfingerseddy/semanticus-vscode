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
- **Idempotent.** Paddle retries webhooks; duplicate transaction ids are acknowledged and skipped.
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
   is all it's used for).
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
  including `mint-failed` (with the error) and `pending-delivery` (with the ready-to-send token).
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
