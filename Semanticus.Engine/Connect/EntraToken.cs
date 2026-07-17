using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Azure.Core;
using Azure.Identity;

namespace Semanticus.Engine
{
    /// <summary>
    /// Acquires an Entra (Azure AD) bearer token for Power BI / Analysis Services XMLA. The token is
    /// passed as the connection-string Password for powerbi:// endpoints (the established token-auth
    /// pattern), so the engine owns auth — no MSOLAP popups; headless/agent/CI connections work.
    /// Modes: azcli (uses `az login`, headless-friendly), interactive (browser), devicecode
    /// (prints a URL+code to stderr), token (caller-supplied raw token).
    /// </summary>
    public static class EntraToken
    {
        private static readonly string[] Scopes = { "https://analysis.windows.net/powerbi/api/.default" };
        // A Fabric SQL endpoint (Warehouse / Lakehouse SQL analytics endpoint, *.datawarehouse.fabric.microsoft.com)
        // is a TDS/SQL resource — it wants the Azure SQL / database audience, NOT the Power BI XMLA audience. The
        // credential is the same; only the scope differs. Unlike XMLA, the SQL endpoint accepts ordinary Entra
        // tokens (azcli / service principal), so no first-party-appid wall here.
        private static readonly string[] SqlScopes = { "https://database.windows.net/.default" };
        // The Fabric REST API (api.fabric.microsoft.com/v1 — workspaces, deployment pipelines, items, git) is its
        // own resource with its own audience. Same credential, different scope. Like the SQL endpoint (and unlike
        // XMLA), it accepts ordinary Entra tokens (azcli / service principal) — no first-party-appid wall.
        private static readonly string[] FabricScopes = { "https://api.fabric.microsoft.com/.default" };

        // The netcore AMO/ADOMD client cannot perform interactive AAD itself ("interactive authentication is not
        // supported, an external access-token is required") — so unlike Tabular Editor (full MSOLAP on .NET
        // Framework, which does its own AAD), WE must acquire the token and inject it. Azure.Identity CAN pop a
        // browser on net8; the catch is which CLIENT the token is issued to: Power BI / Fabric XMLA only accepts
        // tokens whose appid is a Microsoft first-party client that's pre-registered for it (which is why TE2 works
        // in ANY tenant with no app registration and no admin — it rides MSOLAP's first-party client; and why a
        // generic dev client like Azure CLI is rejected with "failed for all authenticators").
        //
        // So we mint the token under the SAME first-party public client Power BI Desktop itself uses to reach the
        // XMLA endpoint: "Power Query for Excel" (a672d62c…) — documented by Microsoft as "Public client, used in
        // Power BI Desktop and the gateway" (learn.microsoft.com/power-query/connector-authentication). Being
        // first-party + public, it's pre-consented everywhere (no registration, no admin) and its appid is on the
        // endpoint's accepted list by definition. Override via SEMANTICUS_ENTRA_CLIENT_ID if ever needed. Service
        // principal remains the app-only path (no appid wall) for headless/CI.
        private const string PowerBIDesktopPublicClientId = "a672d62c-fc7b-4e81-a576-e60dc46e951d";
        private static string AuthClientId()
        {
            var v = Environment.GetEnvironmentVariable("SEMANTICUS_ENTRA_CLIENT_ID");
            return string.IsNullOrWhiteSpace(v) ? PowerBIDesktopPublicClientId : v.Trim();
        }

        /// <summary>Bearer token string only (for the ADOMD connection-string Password= path).</summary>
        public static async Task<string> AcquireAsync(string mode, string rawToken, CancellationToken ct, string tenantId = null)
            => (await AcquireFullAsync(mode, rawToken, ct, tenantId).ConfigureAwait(false)).Token;

        /// <summary>Token + expiry. AMO/TOM needs both (Server.AccessToken) — its managed auth rejects the
        /// connection-string Password= form that ADOMD accepts. One-shot: builds a fresh credential each call,
        /// so do NOT use this on a hot path for interactive auth — prefer <see cref="BuildCredential"/> + reuse
        /// (see <c>Session</c>'s live-auth cache), which is what stops the browser re-prompting.</summary>
        public static async Task<AccessToken> AcquireFullAsync(string mode, string rawToken, CancellationToken ct, string tenantId = null)
        {
            mode = string.IsNullOrWhiteSpace(mode) ? "azcli" : mode.Trim().ToLowerInvariant();

            if (mode == "token")
            {
                if (string.IsNullOrWhiteSpace(rawToken)) throw new InvalidOperationException("auth mode 'token' requires a raw access token.");
                // Use the token's REAL expiry (its JWT 'exp' claim) — NOT a fabricated one — so the session's
                // token-reuse/skew logic can't be fooled into handing a short-lived caller token to a multi-minute
                // write as if it were still valid. If it isn't a decodable JWT, assume a SHORT window (forces the
                // caller to re-supply rather than silently reusing a possibly-dead token).
                var exp = ReadJwtExpiry(rawToken) ?? DateTimeOffset.UtcNow.AddMinutes(5);
                return new AccessToken(rawToken, exp);
            }

            return await GetTokenAsync(BuildCredential(mode, tenantId), ct).ConfigureAwait(false);
        }

        // Read the 'exp' (expiry, Unix seconds) claim from a JWT access token WITHOUT validating its signature —
        // we only need the lifetime for cache/skew decisions; the Analysis Services server does the real validation.
        // Entra/XMLA access tokens are JWTs, so this normally succeeds; returns null on any non-JWT / malformed input.
        private static DateTimeOffset? ReadJwtExpiry(string token)
        {
            try
            {
                var parts = token.Split('.');
                if (parts.Length < 2) return null;
                var payload = parts[1].Replace('-', '+').Replace('_', '/');   // base64url -> base64
                switch (payload.Length % 4) { case 2: payload += "=="; break; case 3: payload += "="; break; }
                var json = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(payload));
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                return doc.RootElement.TryGetProperty("exp", out var e) && e.TryGetInt64(out var exp)
                    ? DateTimeOffset.FromUnixTimeSeconds(exp)
                    : (DateTimeOffset?)null;
            }
            catch { return null; }
        }

        /// <summary>Build the Azure.Identity credential for a non-token auth mode. The KEY to no-re-prompt:
        /// an Azure.Identity credential keeps its token cache (access + refresh token) in memory for the
        /// instance's lifetime, so reusing ONE instance across a session's live ops (open → deploy → refresh)
        /// serves the cached token and silently renews via the refresh token — the browser pops exactly once.
        /// Building a fresh instance per op (the old bug) gave each a blank cache, re-popping every time.
        /// `mode` must be normalised lower-case; "token" has no credential (caller supplies it) and is rejected.
        /// <paramref name="skipSavedRecord"/> (the "use a different account" path) builds the interactive/device-code
        /// credential WITHOUT pinning the saved AuthenticationRecord, so MSAL can't silently reuse the cached
        /// identity and instead shows the account picker — see <see cref="BuildCredentialAsync"/>.</summary>
        public static TokenCredential BuildCredential(string mode, string tenantId, bool skipSavedRecord = false)
        {
            // Optional Entra tenant override — lets us target a tenant the current `az login` isn't the
            // home of (e.g. the model lives in tenant B while az is signed into tenant A). null = default.
            tenantId = string.IsNullOrWhiteSpace(tenantId) ? null : tenantId.Trim();
            // The SYNC reuse path loads the saved record from disk HERE and hands it to BuildCredentialWith; the async
            // open path (BuildCredentialAsync) instead reads ONCE up front and passes THAT snapshot in, so the credential
            // it pins and the account it reports can't drift apart (round-10 HIGH). skipSavedRecord ("use a different
            // account") pins nothing, so MSAL falls into interactive auth and shows the account picker.
            var family = InteractiveFamily(mode);
            var rec = (skipSavedRecord || family == null || !PersistenceSupported) ? null : LoadRecord(RecordPath(family, tenantId));
            return BuildCredentialWith(mode, tenantId, rec);
        }

        // Build the Azure.Identity credential from an ALREADY-LOADED saved record (null = pin nothing → picker / no
        // silent reuse). Kept separate from the disk read so the async open path can read the record exactly once and
        // thread that single snapshot through both the credential it pins AND the account it reports. `mode` and
        // `tenant` must be normalised (lower-case mode, trimmed tenant) by the caller.
        private static TokenCredential BuildCredentialWith(string mode, string tenant, AuthenticationRecord savedRecord)
        {
            return mode switch
            {
                "serviceprincipal" or "sp" => ClientSecret(tenant),
                "interactive" or "entra" or "entramfa" or "mfa" => new InteractiveBrowserCredential(InteractiveOptions(tenant, savedRecord)),
                "devicecode" => new DeviceCodeCredential(DeviceCodeOptions(tenant, savedRecord)),
                "token" => throw new InvalidOperationException("BuildCredential does not handle 'token' mode (the caller supplies the token)."),
                _ => new AzureCliCredential(new AzureCliCredentialOptions { TenantId = tenant }),
            };
        }

        // The saved-record FAMILY for an auth mode — the interactive modes share one MSAL record slot, devicecode has its
        // own, and azcli / serviceprincipal / token keep NO AuthenticationRecord. ONE source of truth for RecordPath keys,
        // ReadSavedAccount, AuthRecordSlot, and the credential builders, so they always agree on which modes persist an
        // account. `mode` must be normalised (lower-case).
        private static string InteractiveFamily(string mode) => mode switch
        {
            "interactive" or "entra" or "entramfa" or "mfa" => "interactive",
            "devicecode" => "devicecode",
            _ => null,
        };

        // ---- Persistent interactive token cache (survives engine restarts) -------------------------------------
        // "Save my XMLA sign-in." Azure.Identity keeps the access+refresh token in an on-disk MSAL cache when given
        // TokenCachePersistenceOptions (encrypted at rest — DPAPI on Windows). We ALSO persist the AuthenticationRecord
        // (account identity — username/home-account-id/tenant/authority, NOT a token) so a later run knows WHICH
        // cached account to use and acquires silently, with no browser pop, until the refresh token ages out (~90d).
        // Gated to Windows: on Linux/macOS the cache needs libsecret/Keychain and would throw, so we skip persistence
        // there and fall back to the in-memory (re-prompt-per-restart) behaviour rather than breaking auth.
        private const string TokenCacheName = "semanticus-xmla";
        private static bool PersistenceSupported => OperatingSystem.IsWindows();

        private static string PersistDir()
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Semanticus", "auth");
            Directory.CreateDirectory(dir);
            return dir;
        }
        // One record per identity (auth client + tenant + credential family). Tenant is normalised (Entra ids/domains
        // are case-insensitive) so a differently-cased re-supply still finds the saved record.
        private static string RecordPath(string family, string tenant)
        {
            var key = $"{AuthClientId()}|{(tenant ?? "").Trim().ToLowerInvariant()}|{family}";
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key)))[..16];
            return Path.Combine(PersistDir(), $"record-{hash}.json");
        }
        private static AuthenticationRecord LoadRecord(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                var text = File.ReadAllText(path);
                string recordJson;
                var env = TryReadEnvelope(text);
                if (env != null)
                {
                    // A recognised envelope: use ONLY its folded record. A reservation-only envelope (RecordJson=null —
                    // a claim minted before any record committed) yields NO record; NEVER deserialize the envelope shell
                    // itself (round-10 HIGH). The old `?? text` fallback did exactly that, and MSAL's lenient Deserialize
                    // handed back a blank pseudo-record that then got PINNED as the credential's identity.
                    if (string.IsNullOrWhiteSpace(env.RecordJson)) return null;
                    recordJson = env.RecordJson;
                }
                else
                {
                    recordJson = text;   // a genuinely legacy BARE MSAL record (no envelope field names) — deserialize the file directly
                }
                using var ms = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(recordJson));
                var rec = AuthenticationRecord.Deserialize(ms);
                // Apply the USABLE bar (non-blank Username) BEFORE anyone pins this for silent acquisition: Deserialize is
                // lenient and can return a blank record for a non-record blob, and a blank identity must never reach Azure
                // Identity as a pinned account (round-10 HIGH point 5).
                return rec == null || string.IsNullOrWhiteSpace(rec.Username) ? null : rec;
            }
            catch { /* corrupt / version-mismatched record — ignore and re-authenticate */ }
            return null;
        }

        // The first-sign-in / re-capture gate (round-9 HIGH 1): prompt + capture when forcing a switch, or when NO
        // USABLE saved record actually loads. Decided on the DESERIALIZED record — NOT File.Exists — because a
        // reservation-only envelope (a claim minted before any record committed; the file exists but RecordJson=null)
        // carries no account: File.Exists mistook it for a saved sign-in, so the next open SKIPPED AuthenticateAsync and
        // never captured/committed the identity. "Usable" = a non-blank Username (MSAL's Deserialize is lenient and hands
        // back an empty record for a non-record envelope, so a null check alone isn't enough) — the SAME bar ReadSavedAccount
        // uses, so this captures exactly when there is no account to silently reuse.
        internal static bool ShouldCaptureRecord(AuthenticationRecord loaded, bool forceReauth)
            => forceReauth || loaded == null || string.IsNullOrWhiteSpace(loaded.Username);

        // Path overload: loads the record once and defers to the pure form above. The PRODUCTION open path
        // (BuildCredentialAsync) never uses this — it reads the record ONCE and passes it in, so the pin decision and
        // the capture decision share one snapshot (round-10 HIGH). Retained for the direct on-disk-gate unit test.
        internal static bool ShouldCaptureRecord(string path, bool forceReauth)
            => ShouldCaptureRecord(LoadRecord(path), forceReauth);

        // The saved-account record and its per-slot claim now travel in ONE JSON envelope, so a single atomic temp+rename
        // replaces BOTH (round-7 HIGH 2). Round-6 wrote the record, then a SEPARATE ".seq" sidecar — a crash between the two
        // left a new record beside a STALE sequence, and a later stale write could then win the compare-and-swap. RecordJson
        // holds MSAL's own AuthenticationRecord serialization verbatim; Seq is the durable per-slot counter.
        private sealed class RecordEnvelope
        {
            public long Seq { get; set; }
            // The RESERVATION high-water (round-8 HIGH 2): the largest claim ever HANDED OUT by MintClaim, persisted the
            // moment it is minted. Seq is the largest claim ever COMMITTED. Two concurrent stagings must get DISTINCT claims,
            // so minting reads max(IssuedSeq, Seq) and bumps IssuedSeq under the lock — a read-without-reserving handed both
            // the same N+1, and the CAS then refused the SECOND (newer) commit as equal, pinning the OLDER identity. IssuedSeq
            // is never below Seq; a legacy/committed-only envelope reads IssuedSeq == Seq.
            public long IssuedSeq { get; set; }
            public string RecordJson { get; set; }
        }

        // Parse the envelope, or null when the text is a legacy BARE MSAL record. Round-7 disambiguated on a non-empty
        // RecordJson, but round-8's reservation-only envelope (a claim minted BEFORE any record is committed) legitimately
        // has RecordJson=null — so we must instead recognise the envelope by its OWN field names (Seq / IssuedSeq /
        // RecordJson). A legacy MSAL AuthenticationRecord carries none of those (its keys are username/authority/tenantId/…),
        // so their absence cleanly means "legacy" without a version byte, while a reservation-only envelope still parses.
        private static RecordEnvelope TryReadEnvelope(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(text);
                if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object) return null;
                var isEnvelope = doc.RootElement.TryGetProperty("Seq", out _)
                    || doc.RootElement.TryGetProperty("IssuedSeq", out _)
                    || doc.RootElement.TryGetProperty("RecordJson", out _);
                return isEnvelope ? System.Text.Json.JsonSerializer.Deserialize<RecordEnvelope>(text) : null;
            }
            catch { return null; }
        }

        // The persisted per-slot claim for this record path: the sequence folded into the envelope, or — a one-time
        // MIGRATION read — a pre-envelope ".seq" sidecar, so upgrading an installed engine doesn't reset the high-water
        // mark and let a stale in-flight write win. 0 when neither exists (an unreadable claim reads as "none", so the
        // newer write wins and rewrites it).
        internal static long ReadPersistedSeq(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    var env = TryReadEnvelope(File.ReadAllText(path));
                    if (env != null) return env.Seq;
                }
            }
            catch { /* unreadable envelope → fall through to the migration sidecar / none */ }
            try
            {
                var seqPath = path + ".seq";   // migration read ONLY — this sidecar format is never written anymore
                if (File.Exists(seqPath) && long.TryParse(File.ReadAllText(seqPath), out var s)) return s;
            }
            catch { }
            return 0;
        }

        // Read the full on-disk envelope state (never null), folding a legacy BARE MSAL record (RecordJson = the whole
        // file, committed Seq from the migration sidecar) and normalising IssuedSeq to never sit below the committed Seq.
        // MintClaim and the commit CAS both read through this so they agree on the same high-water marks under the lock.
        private static RecordEnvelope ReadEnvelopeState(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    var text = File.ReadAllText(path);
                    var env = TryReadEnvelope(text);
                    if (env != null)
                    {
                        if (env.IssuedSeq < env.Seq) env.IssuedSeq = env.Seq;   // a pre-round-8 envelope had no IssuedSeq → treat committed Seq as the floor
                        return env;
                    }
                    // Legacy bare MSAL record: the file itself is the record; its committed claim is the migration sidecar.
                    var legacy = ReadPersistedSeq(path);
                    return new RecordEnvelope { Seq = legacy, IssuedSeq = legacy, RecordJson = text };
                }
            }
            catch { /* unreadable → fall through to the sidecar / none, so a stale write still can't win */ }
            var seq = ReadPersistedSeq(path);
            return new RecordEnvelope { Seq = seq, IssuedSeq = seq, RecordJson = null };
        }

        // One atomic temp+rename of the whole envelope, retiring any migrated ".seq" sidecar. Held only for the microseconds
        // of the write, always inside AcquireRecordLock.
        private static void WriteEnvelopeAtomic(string path, RecordEnvelope env)
        {
            var json = System.Text.Json.JsonSerializer.Serialize(env);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, path, overwrite: true);
            try { var sidecar = path + ".seq"; if (File.Exists(sidecar)) File.Delete(sidecar); } catch { }
        }

        // Mint THIS sign-in's per-slot claim and RESERVE it durably (round-8 HIGH 2). Under the record lock: read
        // max(IssuedSeq, Seq), claim +1, and WRITE the bumped IssuedSeq back — PRESERVING the committed record + Seq — before
        // releasing. The round-7 code read persisted+1 without reserving, so two concurrent stagings both saw N and both got
        // N+1; the newer commit was then refused as EQUAL and the older identity kept the pointer. Reserving under the lock
        // hands the two stagings DISTINCT claims (N+1, N+2), so the newer always out-CASes the older. A DURABLE counter, never
        // wall-clock: a clock correction / VM restore must never let a newer sign-in stamp a LOWER tick and lose. On a
        // reservation FAILURE (round-9 MEDIUM 2) return NO committable claim (0), NOT an unreserved persisted+1: the old
        // fallback could hand two concurrent stagings the SAME persisted+1, and the commit CAS would then let whichever
        // wrote FIRST win — pinning the OLDER identity instead of the newer staging. A zero claim never commits (the CAS
        // refuses it and CommitAuthRecord skips it), so the sign-in still works for THIS session while the saved pointer
        // stays put. A real reservation is always >= 1, so 0 is an unambiguous "no claim". Internal so the "mint twice
        // before either commit" ordering is unit-testable.
        internal static long MintClaim(string path)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                using (AcquireRecordLock(path))
                {
                    var env = ReadEnvelopeState(path);
                    var claim = Math.Max(env.IssuedSeq, env.Seq) + 1;
                    env.IssuedSeq = claim;                 // reserve durably, preserving the committed record + Seq
                    WriteEnvelopeAtomic(path, env);
                    return claim;
                }
            }
            catch (Exception ex)
            {
                // Honest degrade: the account authenticates for this session, but with no reserved claim its commit is a
                // no-op so the saved pointer is unchanged (rather than racing an unreserved claim into the CAS).
                Console.Error.WriteLine("[auth] could not reserve a save-slot claim; this sign-in works for the session but won't update the saved account. " + ex.Message);
                return 0;
            }
        }

        private static void SaveRecord(string path, AuthenticationRecord record, long claimSeq)
        {
            // The AuthenticationRecord is the tenant-wide "who opens next" pointer, shared by BOTH engine processes (the VS
            // Code extension and the MCP server each own one). TryClaimRecordWrite compare-and-swaps the durable per-slot
            // claim under a cross-process lock and folds record+claim into ONE atomic envelope, so neither a concurrent
            // commit nor a crash mid-write can leave the pointer inconsistent with its claim. Best-effort: a refused/failed
            // save just means we prompt again next restart, never a thrown connect.
            TryClaimRecordWrite(path, claimSeq, () =>
            {
                using var ms = new MemoryStream();
                record.Serialize(ms);
                return System.Text.Encoding.UTF8.GetString(ms.ToArray());
            });
        }

        // Cross-process compare-and-swap for the shared saved-account record (round-7 HIGH 2). Under the per-path lock, read
        // the persisted per-slot claim, write ONLY when this claim is strictly newer (a stale OR equal claim is refused), and
        // persist the record + claim as ONE JSON envelope via a single temp+rename — so there is no window where a new record
        // sits beside an old claim. Held only for the microseconds of the temp-write + rename. Internal so the two-process
        // ordering is unit-testable: the `recordPayload` callback stands in for MSAL's record serialization, exercising the
        // exact CAS + atomic-envelope discipline without a real sign-in. Best-effort: any failure returns false → re-prompt.
        internal static bool TryClaimRecordWrite(string path, long claimSeq, Func<string> recordPayload)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                using (AcquireRecordLock(path))
                {
                    var env = ReadEnvelopeState(path);
                    if (claimSeq <= env.Seq) return false;   // a newer (or equal) COMMITTED claim already won — refuse the stale write
                    env.Seq = claimSeq;
                    env.IssuedSeq = Math.Max(env.IssuedSeq, claimSeq);   // never let a commit lower the reservation high-water
                    env.RecordJson = recordPayload();
                    WriteEnvelopeAtomic(path, env);          // ONE atomic rename replaces the account pointer + both counters
                    return true;
                }
            }
            catch { return false; /* best-effort: a failed claim just means we prompt again next restart */ }
        }

        // Cross-process lock over ONE record file, keyed by its path — the same primitive ConnectionRegistry uses. Held
        // only for the microseconds of a temp-write + rename, so contention is rare; a timeout degrades to "prompt again".
        private static IDisposable AcquireRecordLock(string path)
        {
            var lockPath = path + ".lock";
            for (var i = 0; i < 100; i++)
            {
                try { return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose); }
                catch (IOException) { System.Threading.Thread.Sleep(15); }
            }
            throw new IOException("Could not acquire the auth-record lock.");
        }

        // The credential options keep the shared encrypted token cache attached (so a freshly chosen account still
        // persists) and pin the AuthenticationRecord the CALLER already loaded — a null `rec` (the "use a different
        // account" path, or no saved record) pins nothing, so MSAL falls into interactive auth and shows the account
        // picker instead of silently re-using a cached identity. These no longer read disk themselves: the caller reads
        // the record ONCE and passes it here, so the pinned identity can't diverge from the reported one (round-10 HIGH).
        private static InteractiveBrowserCredentialOptions InteractiveOptions(string tenant, AuthenticationRecord rec)
        {
            var o = new InteractiveBrowserCredentialOptions { TenantId = tenant, ClientId = AuthClientId() };
            if (PersistenceSupported)
            {
                o.TokenCachePersistenceOptions = new TokenCachePersistenceOptions { Name = TokenCacheName };
                if (rec != null) o.AuthenticationRecord = rec;     // → GetToken acquires silently from the encrypted cache
            }
            return o;
        }
        private static DeviceCodeCredentialOptions DeviceCodeOptions(string tenant, AuthenticationRecord rec)
        {
            var o = new DeviceCodeCredentialOptions
            {
                TenantId = tenant,
                ClientId = AuthClientId(),
                DeviceCodeCallback = (info, _) => { Console.Error.WriteLine("[auth] " + info.Message); return Task.CompletedTask; },
            };
            if (PersistenceSupported)
            {
                o.TokenCachePersistenceOptions = new TokenCachePersistenceOptions { Name = TokenCacheName };
                if (rec != null) o.AuthenticationRecord = rec;
            }
            return o;
        }

        /// <summary>A credential ready to acquire a token, plus the freshly authenticated MSAL record that must NOT be
        /// persisted until the open it authorizes SUCCEEDS. Deferring the write is what stops a FAILED open from silently
        /// repointing the tenant-wide saved account: the interactive/device-code sign-in already happened (and this
        /// credential holds it in memory for the current attempt), but the on-disk record — the pointer the NEXT open
        /// pins — is only overwritten by <see cref="CommitAuthRecord"/> once the endpoint has authorized. On failure the
        /// caller never commits, so the old record stands and the next open still uses the prior account.</summary>
        public sealed class PreparedCredential
        {
            public TokenCredential Credential { get; internal set; }
            internal AuthenticationRecord PendingRecord { get; set; }   // null = nothing new to persist (silent reuse / azcli / sp)
            internal string PendingRecordPath { get; set; }
            // THIS sign-in's durable per-slot claim — the cross-process CAS token (round-7 HIGH 2). The in-process barrier
            // orders commits within ONE engine, but the extension and MCP server are SEPARATE processes sharing the record
            // file, so an older process's write could land AFTER a newer one's. RESERVED durably under the record lock (NOT
            // wall-clock: a clock correction / VM restore would let a newer sign-in stamp a LOWER tick and lose) when the
            // pending record is captured, it lets the on-disk write compare-and-swap: a stale (or equal) claim is refused.
            // 0 means the reservation degraded (round-9 MEDIUM 2) — CommitAuthRecord skips it rather than racing an
            // unreserved claim, so the saved pointer is left unchanged.
            internal long PendingClaimSeq { get; set; }
            /// <summary>The account (UPN) the pending sign-in resolved to, when a NEW record is awaiting commit — the truth
            /// of who this attempt authenticated as, BEFORE the record is on disk (so a display read doesn't lag to the
            /// old account). Null when no new record is pending.</summary>
            public string PendingAccount => PendingRecord?.Username;
            public bool HasPendingRecord => PendingRecord != null && !string.IsNullOrWhiteSpace(PendingRecordPath);
            /// <summary>The saved record the credential was ACTUALLY constructed from — the freshly captured record when
            /// this sign-in captured a new account, else the saved record we PINNED for silent reuse. Read from the SAME
            /// once-loaded snapshot the credential was built from (round-10 HIGH), so the reported live identity can't
            /// drift from the pinned one via a second disk read racing a cross-process commit. Null for azcli / sp / token
            /// (no MSAL record) and on non-persistence platforms.</summary>
            internal AuthenticationRecord ResolvedRecord { get; set; }
            /// <summary>The account (UPN) this credential will connect as — the constructed/pinned identity, NOT a fresh
            /// disk read. The live-connection sites report THIS so the reported account matches the credential actually
            /// used. Null when there is no MSAL record (azcli / sp / token).</summary>
            public string Account => ResolvedRecord?.Username;
        }

        /// <summary>Like <see cref="BuildCredential"/>, but for the interactive / device-code families it also
        /// captures the AuthenticationRecord on the FIRST sign-in (when none is saved yet) or a forced switch — WITHOUT
        /// persisting it. The caller persists via <see cref="CommitAuthRecord"/> only after the open it authorizes
        /// succeeds, so a failed open can never silently change the tenant-wide saved account. Use this on the open path;
        /// the sync <see cref="BuildCredential"/> already loads a saved record, so a seeded-then-reused credential stays
        /// silent.
        /// <paramref name="forceReauth"/> is the "use a different account" path: it ignores the saved record, ALWAYS
        /// runs the interactive sign-in (showing the account picker), and marks the newly chosen identity for commit —
        /// the cure for "stuck signed in as the wrong account" when the cache holds one slot per (client, tenant).
        /// No-op for serviceprincipal/azcli (those modes have no record to switch).</summary>
        public static async Task<PreparedCredential> BuildCredentialAsync(string mode, string tenantId, CancellationToken ct, bool forceReauth = false)
        {
            mode = string.IsNullOrWhiteSpace(mode) ? "azcli" : mode.Trim().ToLowerInvariant();
            if (mode == "token") throw new InvalidOperationException("BuildCredentialAsync does not handle 'token' mode (the caller supplies the token).");
            var tenant = string.IsNullOrWhiteSpace(tenantId) ? null : tenantId.Trim();
            // READ-ONCE (round-10 HIGH): load the saved record EXACTLY ONCE here and thread that single snapshot through
            // (a) which record the credential PINS, (b) whether we capture, and (c) which account we REPORT. Building the
            // credential and deciding capture from SEPARATE file reads let a cross-process commit land between them: A
            // pinned Alice, B committed Bob, A's capture gate then saw a usable Bob and SKIPPED capture, so A opened as
            // pinned Alice while a later disk read reported Bob — the wrong-live-account defect. One read makes all three
            // agree. forceReauth ("use a different account") ignores the saved record so MSAL shows the picker; a
            // reservation-only envelope loads as NO record (LoadRecord never deserializes the shell), correctly forcing
            // capture instead of pinning a blank pseudo-record.
            var loaded = LoadPinnedRecord(mode, tenant, forceReauth, out var path);

            // Build the credential FROM that one snapshot — the options wiring no longer re-reads disk.
            var cred = BuildCredentialWith(mode, tenant, loaded);
            var prepared = new PreparedCredential { Credential = cred, ResolvedRecord = loaded };
            if (path == null) return prepared;   // azcli / sp / no-persistence: no record to capture or report

            // The capture decision derives from the SAME loaded snapshot (no re-read). Capture on a forced switch OR when
            // no USABLE record loaded (a reservation-only envelope counts as none — round-9 HIGH 1); otherwise the cred
            // acquires silently from the cache and the pinned `loaded` record is the reported identity.
            if (!ShouldCaptureRecord(loaded, forceReauth)) return prepared;

            // Prime + capture the record from THIS interactive acquisition and STAGE it (not write) — see
            // PreparedCredential — so a failed open leaves the old one, and the token the caller's GetToken uses next
            // comes from the SAME credential (no second prompt on success). The captured record BECOMES the reported
            // account, superseding the (null) loaded one.
            //
            // A FAILED prime is PROPAGATED, never swallowed (HIGH 4). Swallowing it fell through to a SECOND interactive
            // GetToken prompt whose success had NO captured record — so a switch to Bob was silently remembered as the
            // prior Alice. An honest throw (cancelled / wrong tenant / no consent) lets the caller report it and leave
            // the saved account untouched, with no silent re-prompt.
            if (cred is InteractiveBrowserCredential ibc)
            {
                prepared.PendingRecord = await ibc.AuthenticateAsync(new TokenRequestContext(Scopes), ct).ConfigureAwait(false);
                prepared.PendingRecordPath = path; prepared.PendingClaimSeq = MintClaim(path); prepared.ResolvedRecord = prepared.PendingRecord;
            }
            else if (cred is DeviceCodeCredential dcc)
            {
                prepared.PendingRecord = await dcc.AuthenticateAsync(new TokenRequestContext(Scopes), ct).ConfigureAwait(false);
                prepared.PendingRecordPath = path; prepared.PendingClaimSeq = MintClaim(path); prepared.ResolvedRecord = prepared.PendingRecord;
            }
            return prepared;
        }

        /// <summary>Persist the freshly authenticated record — call ONLY after the open/authorization it belongs to has
        /// succeeded. Until then a failed open must leave the prior saved account untouched (see PreparedCredential).
        /// No-op when nothing is pending (silent reuse / azcli / serviceprincipal / not persistence-supported).</summary>
        public static void CommitAuthRecord(PreparedCredential prepared)
        {
            if (prepared == null || !prepared.HasPendingRecord) return;
            if (prepared.PendingClaimSeq <= 0)
            {
                // No durable claim was reserved (MintClaim degraded under contention, round-9 MEDIUM 2) — skip the commit
                // honestly rather than racing an unreserved claim into the CAS. The account authenticated for this session;
                // the saved pointer stays on the prior identity.
                Console.Error.WriteLine("[auth] skipping saved-account update: no reserved claim for this sign-in.");
                return;
            }
            SaveRecord(prepared.PendingRecordPath, prepared.PendingRecord, prepared.PendingClaimSeq);
        }

        // The ONE saved-record read shared by BuildCredentialAsync's pin, capture, and report decisions (round-10 HIGH):
        // a single load, so the pinned identity and the reported account can never diverge. Returns the on-disk record
        // PATH too (via out) so the capture branch reuses it without recomputing. Null record when forcing a switch, for
        // a record-less mode (azcli / sp), on a non-persistence platform, or when only a reservation-only envelope exists
        // (LoadRecord returns null for RecordJson=null — a blank pseudo-record is NEVER handed to Azure Identity).
        private static AuthenticationRecord LoadPinnedRecord(string mode, string tenant, bool forceReauth, out string path)
        {
            var family = InteractiveFamily(mode);
            path = (family != null && PersistenceSupported) ? RecordPath(family, tenant) : null;
            return (path != null && !forceReauth) ? LoadRecord(path) : null;
        }

        // Test seam (round-10 HIGH): the EXACT record BuildCredentialAsync would pin + report for this (mode, tenant) —
        // the value fed to Azure Identity's AuthenticationRecord. Lets a test assert a reservation-only envelope yields
        // NO record (never a blank pseudo-record) without driving a real sign-in.
        internal static AuthenticationRecord LoadPinnedRecordForTests(string mode, string tenantId, bool forceReauth)
        {
            mode = string.IsNullOrWhiteSpace(mode) ? "azcli" : mode.Trim().ToLowerInvariant();
            var tenant = string.IsNullOrWhiteSpace(tenantId) ? null : tenantId.Trim();
            return LoadPinnedRecord(mode, tenant, forceReauth, out _);
        }

        // Test seam (round-10 HIGH): the on-disk record PATH BuildCredentialAsync reads for a (mode, tenant), so a test
        // can seed that exact file and interleave a cross-process commit against the read-once discipline. Null for the
        // record-less modes (azcli / serviceprincipal / token).
        internal static string RecordPathForTests(string mode, string tenantId)
        {
            var family = InteractiveFamily(string.IsNullOrWhiteSpace(mode) ? "azcli" : mode.Trim().ToLowerInvariant());
            return family == null ? null : RecordPath(family, string.IsNullOrWhiteSpace(tenantId) ? null : tenantId.Trim());
        }

        /// <summary>The account (UPN + tenant) a persisted MSAL sign-in belongs to. A display hint, never an identity
        /// lock — the live token still decides who connects.</summary>
        public sealed class SavedAccount
        {
            public string Username { get; set; }
            public string TenantId { get; set; }
        }

        /// <summary>Read the saved account for an interactive/device-code sign-in on THIS device, if one is persisted —
        /// a genuine silent probe (a local disk read, no network, no prompt). Returns null for azcli / serviceprincipal
        /// / token (no MSAL AuthenticationRecord exists) and when nobody has interactively signed in for this
        /// (mode, tenant) yet — which a surface reads as "account unknown", not "signed in". Keyed identically to the
        /// sign-in path (<see cref="RecordPath"/>), so it finds exactly the record a later connect would reuse.</summary>
        // Test seam: stands in for the on-disk MSAL AuthenticationRecord read, so the probe's "tenant-wide record wins
        // over the per-target hint" logic can be exercised without a real interactive sign-in. Applied only AFTER the
        // family gate (so azcli/serviceprincipal still resolve to null exactly as in production). Null → the real disk
        // read runs. Signature (mode, tenant) → username; null/empty username reads as "no saved record".
        internal static Func<string, string, string> SavedAccountForTests;

        public static SavedAccount ReadSavedAccount(string mode, string tenantId)
        {
            mode = string.IsNullOrWhiteSpace(mode) ? "azcli" : mode.Trim().ToLowerInvariant();
            var family = InteractiveFamily(mode);   // azcli / serviceprincipal / token keep no AuthenticationRecord to name
            if (family == null) return null;
            var tenant = string.IsNullOrWhiteSpace(tenantId) ? null : tenantId.Trim();
            if (SavedAccountForTests != null)
            {
                var u = SavedAccountForTests(mode, tenant);
                return string.IsNullOrWhiteSpace(u) ? null : new SavedAccount { Username = u, TenantId = tenant };
            }
            if (!PersistenceSupported) return null;
            var rec = LoadRecord(RecordPath(family, tenant));
            return rec == null || string.IsNullOrWhiteSpace(rec.Username)
                ? null
                : new SavedAccount { Username = rec.Username, TenantId = rec.TenantId };
        }

        /// <summary>The saved-account SLOT a sign-in of this (mode, tenant) writes — family+tenant, matching
        /// <see cref="RecordPath"/>'s (family, tenant) key. Null for azcli / serviceprincipal / token (no
        /// AuthenticationRecord to order). Lets the engine key its ordered-commit barrier PER SLOT so a sign-in in one
        /// tenant/family never invalidates a pending commit in another (HIGH 3).</summary>
        public static string AuthRecordSlot(string mode, string tenantId)
        {
            mode = string.IsNullOrWhiteSpace(mode) ? "azcli" : mode.Trim().ToLowerInvariant();
            var family = InteractiveFamily(mode);   // azcli / serviceprincipal / token keep no AuthenticationRecord to order
            if (family == null) return null;
            var tenant = string.IsNullOrWhiteSpace(tenantId) ? "" : tenantId.Trim().ToLowerInvariant();
            return family + "|" + tenant;
        }

        /// <summary>Acquire a token for the XMLA scope from a (possibly reused) credential.</summary>
        public static async Task<AccessToken> GetTokenAsync(TokenCredential cred, CancellationToken ct)
            => await cred.GetTokenAsync(new TokenRequestContext(Scopes), ct).ConfigureAwait(false);

        /// <summary>Acquire a token for an arbitrary scope set (e.g. the SQL/database resource) from a credential.</summary>
        public static async Task<AccessToken> GetTokenAsync(TokenCredential cred, string[] scopes, CancellationToken ct)
            => await cred.GetTokenAsync(new TokenRequestContext(scopes), ct).ConfigureAwait(false);

        /// <summary>Bearer token for a Fabric SQL endpoint (TDS) — the same auth modes as XMLA, but the
        /// SQL/database scope. Used as <c>SqlConnection.AccessToken</c> for deterministic schema introspection.</summary>
        public static async Task<string> AcquireSqlAsync(string mode, string rawToken, CancellationToken ct, string tenantId = null)
        {
            mode = string.IsNullOrWhiteSpace(mode) ? "azcli" : mode.Trim().ToLowerInvariant();
            if (mode == "token")
            {
                if (string.IsNullOrWhiteSpace(rawToken)) throw new InvalidOperationException("auth mode 'token' requires a raw access token scoped to the SQL/database resource.");
                return rawToken;
            }
            var tok = await GetTokenAsync(BuildCredential(mode, tenantId), SqlScopes, ct).ConfigureAwait(false);
            return tok.Token;
        }

        /// <summary>Bearer token for the Fabric REST API (api.fabric.microsoft.com) — same auth modes as XMLA, the
        /// Fabric resource scope. Used as the <c>Authorization: Bearer</c> header for the ALM cloud lane (workspaces,
        /// deployment pipelines, items, git). Static (not session-cached) to avoid handing a Fabric call a token
        /// minted for the XMLA/SQL audience.</summary>
        // Test-only seam: the offline deploy-feature tests set this to return a canned bearer so the LocalEngine deploy
        // ops (which always pass rawToken=null) run end-to-end against FabricRest.TestClientFactory with no live tenant
        // and no Azure.Identity. Null in production → the real credential path below runs. Internal + null-by-default,
        // so it can never weaken a production auth path (same risk profile as DaxLibRest.ClientFactoryForTests).
        internal static Func<string> FabricTokenForTests;

        public static async Task<string> AcquireFabricAsync(string mode, string rawToken, CancellationToken ct, string tenantId = null)
        {
            if (FabricTokenForTests != null) return FabricTokenForTests();
            mode = string.IsNullOrWhiteSpace(mode) ? "azcli" : mode.Trim().ToLowerInvariant();
            if (mode == "token")
            {
                if (string.IsNullOrWhiteSpace(rawToken)) throw new InvalidOperationException("auth mode 'token' requires a raw access token scoped to the Fabric API resource (https://api.fabric.microsoft.com).");
                return rawToken;
            }
            var tok = await GetTokenAsync(BuildCredential(mode, tenantId), FabricScopes, ct).ConfigureAwait(false);
            return tok.Token;
        }

        // Service-principal (client-secret) auth — the reliable principal for Power BI / Fabric XMLA, which
        // routinely walls delegated *user* tokens ("MWC token NotAuthorized" / "failed for all authenticators")
        // even for the model owner. Reads standard AZURE_* env vars, falling back to FABRIC_*/POWERBI_* names.
        private static TokenCredential ClientSecret(string tenantId)
        {
            var clientId = Env("AZURE_CLIENT_ID", "FABRIC_CLIENT", "POWERBI_CLIENT_ID");
            var secret = Env("AZURE_CLIENT_SECRET", "FABRIC_SECRET", "POWERBI_CLIENT_SECRET");
            var tenant = string.IsNullOrWhiteSpace(tenantId) ? Env("AZURE_TENANT_ID", "FABRIC_TENANT", "POWERBI_TENANT_ID") : tenantId;
            if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(secret) || string.IsNullOrWhiteSpace(tenant))
                throw new InvalidOperationException("serviceprincipal auth needs a client id + secret + tenant. Set AZURE_CLIENT_ID/AZURE_CLIENT_SECRET/AZURE_TENANT_ID (or FABRIC_CLIENT/FABRIC_SECRET/FABRIC_TENANT).");
            return new ClientSecretCredential(tenant, clientId, secret);
        }

        private static string Env(params string[] names)
        {
            foreach (var n in names) { var v = Environment.GetEnvironmentVariable(n); if (!string.IsNullOrWhiteSpace(v)) return v; }
            return null;
        }
    }
}
