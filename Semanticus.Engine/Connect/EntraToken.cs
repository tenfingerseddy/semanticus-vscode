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

            return mode switch
            {
                "serviceprincipal" or "sp" => ClientSecret(tenantId),
                "interactive" or "entra" or "entramfa" or "mfa" => new InteractiveBrowserCredential(InteractiveOptions(tenantId, skipSavedRecord)),
                "devicecode" => new DeviceCodeCredential(DeviceCodeOptions(tenantId, skipSavedRecord)),
                "token" => throw new InvalidOperationException("BuildCredential does not handle 'token' mode (the caller supplies the token)."),
                _ => new AzureCliCredential(new AzureCliCredentialOptions { TenantId = tenantId }),
            };
        }

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
            try { if (File.Exists(path)) { using var fs = File.OpenRead(path); return AuthenticationRecord.Deserialize(fs); } }
            catch { /* corrupt / version-mismatched record — ignore and re-authenticate */ }
            return null;
        }
        private static void SaveRecord(string path, AuthenticationRecord record)
        {
            try { using var fs = File.Create(path); record.Serialize(fs); }
            catch { /* best-effort: a failed save just means we prompt again next restart */ }
        }

        // skipSavedRecord = the "use a different account" path: keep the shared encrypted token cache attached (so
        // the freshly chosen account still persists), but do NOT pin the saved AuthenticationRecord — without a
        // pinned account MSAL falls into interactive auth and shows the account picker (its default prompt) instead
        // of silently re-using the cached identity. The new record is captured + overwritten in BuildCredentialAsync.
        private static InteractiveBrowserCredentialOptions InteractiveOptions(string tenant, bool skipSavedRecord = false)
        {
            var o = new InteractiveBrowserCredentialOptions { TenantId = tenant, ClientId = AuthClientId() };
            if (PersistenceSupported)
            {
                o.TokenCachePersistenceOptions = new TokenCachePersistenceOptions { Name = TokenCacheName };
                var rec = skipSavedRecord ? null : LoadRecord(RecordPath("interactive", tenant));
                if (rec != null) o.AuthenticationRecord = rec;     // → GetToken acquires silently from the encrypted cache
            }
            return o;
        }
        private static DeviceCodeCredentialOptions DeviceCodeOptions(string tenant, bool skipSavedRecord = false)
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
                var rec = skipSavedRecord ? null : LoadRecord(RecordPath("devicecode", tenant));
                if (rec != null) o.AuthenticationRecord = rec;
            }
            return o;
        }

        /// <summary>Like <see cref="BuildCredential"/>, but for the interactive / device-code families it also
        /// captures + persists the AuthenticationRecord on the FIRST sign-in (when none is saved yet), so every
        /// later run reconnects silently from the encrypted cache. Use this on the open path; the sync
        /// <see cref="BuildCredential"/> already loads a saved record, so a seeded-then-reused credential stays silent.
        /// <paramref name="forceReauth"/> is the "use a different account" path: it ignores the saved record, ALWAYS
        /// runs the interactive sign-in (showing the account picker), and OVERWRITES the saved record with the newly
        /// chosen identity — the cure for "stuck signed in as the wrong account" when the cache holds one slot per
        /// (client, tenant). No-op for serviceprincipal/azcli (those modes have no record to switch).</summary>
        public static async Task<TokenCredential> BuildCredentialAsync(string mode, string tenantId, CancellationToken ct, bool forceReauth = false)
        {
            mode = string.IsNullOrWhiteSpace(mode) ? "azcli" : mode.Trim().ToLowerInvariant();
            if (mode == "token") throw new InvalidOperationException("BuildCredentialAsync does not handle 'token' mode (the caller supplies the token).");
            // When switching accounts, build the credential WITHOUT the pinned record so MSAL shows the picker; the
            // forced AuthenticateAsync below then captures + persists the new account onto the credential and on disk.
            var cred = BuildCredential(mode, tenantId, skipSavedRecord: forceReauth);
            if (!PersistenceSupported) return cred;
            var tenant = string.IsNullOrWhiteSpace(tenantId) ? null : tenantId.Trim();
            try
            {
                // Only the interactive families have an AuthenticateAsync to prime + capture. Prompt when forcing a
                // switch OR when no record exists yet; otherwise the cred built above acquires silently from the cache.
                if (cred is InteractiveBrowserCredential ibc)
                {
                    var path = RecordPath("interactive", tenant);
                    if (forceReauth || !File.Exists(path)) SaveRecord(path, await ibc.AuthenticateAsync(new TokenRequestContext(Scopes), ct).ConfigureAwait(false));
                }
                else if (cred is DeviceCodeCredential dcc)
                {
                    var path = RecordPath("devicecode", tenant);
                    if (forceReauth || !File.Exists(path)) SaveRecord(path, await dcc.AuthenticateAsync(new TokenRequestContext(Scopes), ct).ConfigureAwait(false));
                }
            }
            catch { /* if priming fails, the normal GetToken path will prompt as before — no worse than today */ }
            return cred;
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
