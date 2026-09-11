using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Azure.Core;
using Azure.Identity;

namespace Semanticus.Engine
{
    /// <summary>A saved Microsoft identity on THIS device — credential-free metadata plus the serialized MSAL
    /// AuthenticationRecord (account identity: username / home-account-id / tenant / authority, <b>never a token</b>).
    /// Phase 2 keeps MANY of these instead of Phase 1's one-slot-per-(tenant, family) cache, so choosing a different
    /// account for a single open no longer silently repoints every model on the tenant. Tokens stay only in the
    /// encrypted MSAL cache; this record is identity metadata, persisted the same way the single Phase 1 record is.</summary>
    public sealed class AccountProfile
    {
        public string Id { get; set; }             // stable: hash(client|tenant|family|homeAccountId)
        public string Username { get; set; }       // UPN, e.g. megan@contoso.com — a display value, never a secret
        public string TenantId { get; set; }
        public string Family { get; set; }         // sign-in method / credential family: interactive | devicecode
        public string LastSignInUtc { get; set; }
        public string LastUseUtc { get; set; }
        public bool IsDefault { get; set; }        // DERIVED: this account holds the (family, tenant) default slot
        // A saved sign-in RECORD exists on this device — the strongest thing a local, no-network, no-prompt read can prove.
        // It does NOT guarantee a live token: the cached refresh token may have aged out. A HUMAN selecting a stale one gets
        // a fresh Microsoft prompt (their action); an AGENT gets an honest AuthenticationRequiredException, never a prompt
        // (DisableAutomaticAuthentication). So governance never claims more than "a sign-in is saved here".
        public bool SignedIn { get; set; }
    }

    public static partial class EntraToken
    {
        // The multi-account profile store (Phase 2). The AUTHORITY for "which account is the tenant default" stays the
        // Phase 1 default slot (record-<hash(client,tenant,family)>.json) with its crash-safe envelope CAS — untouched,
        // because that is the pointer the #233 saga made safe and the file an UNQUALIFIED open still pins. This store adds
        // a SEPARATE list of remembered accounts (account-profiles.json) so a per-open selection can pin a NON-default
        // account without repointing the default. Each profile carries the identity record (no token); "is default" is
        // DERIVED by matching the default slot's account, so the two can never disagree.
        private const string ProfilesFileName = "account-profiles.json";

        private static readonly System.Text.Json.JsonSerializerOptions ProfileJsonOpts = new()
        {
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            WriteIndented = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        };

        // The on-disk entry: profile metadata + the identity record + a monotonic per-profile counter for forensics /
        // ordering. RecordJson is MSAL's AuthenticationRecord serialization (identity only, NEVER a token) — the same
        // blob the default slot stores. No secret ever lands here (a token lives only in the encrypted MSAL cache).
        private sealed class ProfileEntry
        {
            public string Id { get; set; }
            public string Username { get; set; }
            public string TenantId { get; set; }
            public string Family { get; set; }
            public string HomeAccountId { get; set; }
            public string RecordJson { get; set; }
            public string LastSignInUtc { get; set; }
            public string LastUseUtc { get; set; }
            public long Seq { get; set; }
        }

        private sealed class ProfilesFile { public List<ProfileEntry> Profiles { get; set; } = new(); }

        private static string ProfilesPath() => Path.Combine(PersistDir(), ProfilesFileName);

        // Test seam: the on-disk profile store path, so a test can seed / inspect it without a real sign-in.
        internal static string ProfilesPathForTests() => PersistenceSupported ? ProfilesPath() : null;

        /// <summary>The credential FAMILY that keeps a switchable saved record for an auth mode (interactive | devicecode),
        /// or null for azcli / serviceprincipal / token. The engine keys a profile on this.</summary>
        public static string FamilyOf(string mode) => InteractiveFamily(string.IsNullOrWhiteSpace(mode) ? "azcli" : mode.Trim().ToLowerInvariant());

        // MSAL's own AuthenticationRecord serialization (identity blob — home-account-id / username / tenant / authority,
        // NEVER a token). The exact form LoadRecordFromJson round-trips back.
        internal static string SerializeRecord(AuthenticationRecord rec)
        {
            if (rec == null) return null;
            using var ms = new MemoryStream();
            rec.Serialize(ms);
            return System.Text.Encoding.UTF8.GetString(ms.ToArray());
        }

        // Normalise a home-account-id to ONE canonical form (item 14a): the hash key and the default-match must agree, or a
        // case-variant identifier would mint two profiles that BOTH match the (case-insensitive) default and both read as
        // default. Lower-casing at every use point (hash key + stored value + default compare) removes that ambiguity.
        // Delegates to the ONE canonicaliser (T163 round 5) so the profile store, the live-auth cache keys and every
        // identity comparison cannot drift into three conventions. Keeps the "" form this file's hash key expects.
        private static string NormHome(string homeAccountId) => CanonicalHomeAccountId(homeAccountId) ?? "";

        private static string ProfileId(string family, string tenant, string homeAccountId)
        {
            var key = $"{AuthClientId()}|{(tenant ?? "").Trim().ToLowerInvariant()}|{family}|{NormHome(homeAccountId)}";
            return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key)))[..16];
        }

        // Deserialize a BARE MSAL AuthenticationRecord json (what a profile stores) to a USABLE record, applying the SAME
        // bar LoadRecord uses: a non-blank Username AND a non-blank HomeAccountId. Username alone is not enough — a
        // profile with no stable id reads as signed in, passes the agent selection check and builds a credential, then
        // reports a null identity that falls into the shared "identity unknown" bucket and bypasses the wrong-identity
        // comparison entirely. Unusable means the profile lists as signed out, so a human re-signs once and an agent
        // refuses; that covers ListProfiles, ProfileIsSignedIn, BuildCredentialForProfile and SetDefaultProfileResult,
        // which all resolve their record through here.
        private static AuthenticationRecord LoadRecordFromJson(string recordJson)
        {
            if (string.IsNullOrWhiteSpace(recordJson)) return null;
            try
            {
                using var ms = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(recordJson));
                var rec = AuthenticationRecord.Deserialize(ms);
                return rec == null || string.IsNullOrWhiteSpace(rec.Username) || string.IsNullOrWhiteSpace(rec.HomeAccountId)
                ? null : rec;
            }
            catch { return null; }
        }

        // The raw identity json currently in the default slot for a (family, tenant), or null. Used by migration to fold
        // the Phase 1 single record into a profile verbatim (no re-serialize, no re-sign-in).
        private static string ReadDefaultRecordJson(string family, string tenant)
        {
            try { return ReadEnvelopeState(RecordPath(family, tenant))?.RecordJson; }
            catch { return null; }
        }

        // ---- reads (device-local, credential-free) -----------------------------------------------------------------

        private static List<ProfileEntry> ReadProfilesForDisplay()
        {
            try
            {
                var p = ProfilesPath();
                if (!File.Exists(p)) return new List<ProfileEntry>();
                return System.Text.Json.JsonSerializer.Deserialize<ProfilesFile>(File.ReadAllText(p), ProfileJsonOpts)?.Profiles
                    ?? new List<ProfileEntry>();
            }
            catch { return new List<ProfileEntry>(); }
        }

        // Write path: never overwrite an unreadable store (same rule as ConnectionRegistry) — move the bad bytes aside
        // first so a transient read error can't silently drop every remembered account.
        private static List<ProfileEntry> ReadProfilesForWrite(string p)
        {
            if (!File.Exists(p)) return new List<ProfileEntry>();
            try
            {
                return System.Text.Json.JsonSerializer.Deserialize<ProfilesFile>(File.ReadAllText(p), ProfileJsonOpts)?.Profiles
                    ?? new List<ProfileEntry>();
            }
            catch
            {
                var aside = p + ".corrupt-" + Guid.NewGuid().ToString("N")[..8];
                try { File.Move(p, aside); }
                catch (Exception ex) { throw new IOException($"Refusing to overwrite an unreadable account-profile store at {p} ({ex.Message}).", ex); }
                return new List<ProfileEntry>();
            }
        }

        private static void WriteProfilesFile(string p, List<ProfileEntry> profiles)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(p));
            var tmp = p + ".tmp";
            File.WriteAllText(tmp, System.Text.Json.JsonSerializer.Serialize(new ProfilesFile { Profiles = profiles }, ProfileJsonOpts));
            File.Move(tmp, p, overwrite: true);   // a crash mid-write leaves the old store intact + a .tmp, never a truncated list
        }

        /// <summary>Every remembered account on this device, credential-free. Read-only. <see cref="AccountProfile.IsDefault"/>
        /// is DERIVED from the (family, tenant) default slot, and <see cref="AccountProfile.SignedIn"/> from whether the saved
        /// record is still usable — never guessed, so the list can never claim a live token it can't prove.</summary>
        public static IReadOnlyList<AccountProfile> ListProfiles()
        {
            if (!PersistenceSupported) return Array.Empty<AccountProfile>();
            var entries = ReadProfilesForDisplay();
            var result = new List<AccountProfile>(entries.Count);
            foreach (var e in entries)
            {
                var defaultHome = ProfilesDefaultHome(e.Family, e.TenantId);
                result.Add(new AccountProfile
                {
                    Id = e.Id,
                    Username = e.Username,
                    TenantId = e.TenantId,
                    Family = e.Family,
                    LastSignInUtc = e.LastSignInUtc,
                    LastUseUtc = e.LastUseUtc,
                    IsDefault = defaultHome != null && string.Equals(NormHome(defaultHome), NormHome(e.HomeAccountId), StringComparison.Ordinal),
                    SignedIn = LoadRecordFromJson(e.RecordJson) != null,
                });
            }
            // Newest use first — the picker's most-likely choice sits at the top.
            return result.OrderByDescending(p => p.LastUseUtc ?? p.LastSignInUtc ?? "").ToList();
        }

        // The home-account-id currently holding the (family, tenant) default slot, or null. The default is a DERIVED fact:
        // whichever account's record is in the slot an unqualified open pins.
        private static string ProfilesDefaultHome(string family, string tenant)
        {
            try
            {
                // family is already an interactive/devicecode slot name; RecordPath keys the default slot by (family, tenant).
                var rec = LoadRecord(RecordPath(family, (tenant ?? "").Trim().ToLowerInvariant()));
                return rec?.HomeAccountId;
            }
            catch { return null; }
        }

        internal static AccountProfile FindProfile(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return null;
            return ListProfiles().FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
        }

        // ---- migration + commits (never fail the open they record) --------------------------------------------------

        /// <summary>Fold the Phase 1 single record for a (family, tenant) into the profile list, once. Idempotent: skips
        /// when a profile for that account already exists, so it is safe to call on every list. No re-sign-in — the identity
        /// record is copied verbatim from the default slot. The default slot stays the authority for "which is default";
        /// this only makes the account VISIBLE as a switchable profile (superseding the old single-slot semantics honestly,
        /// not adding a second source of truth for the default).</summary>
        internal static void EnsureMigratedProfile(string mode, string tenantId)
        {
            if (!PersistenceSupported) return;
            var family = InteractiveFamily(string.IsNullOrWhiteSpace(mode) ? "azcli" : mode.Trim().ToLowerInvariant());
            if (family == null) return;   // azcli / serviceprincipal / token keep no record to migrate
            var tenant = string.IsNullOrWhiteSpace(tenantId) ? null : tenantId.Trim();
            try
            {
                var rec = LoadRecord(RecordPath(family, (tenant ?? "").Trim().ToLowerInvariant()));
                if (rec == null || string.IsNullOrWhiteSpace(rec.HomeAccountId)) return;   // nothing usable to migrate
                var id = ProfileId(family, tenant, rec.HomeAccountId);
                if (ReadProfilesForDisplay().Any(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase))) return;   // already a profile
                var json = ReadDefaultRecordJson(family, (tenant ?? "").Trim().ToLowerInvariant());
                CommitProfile(family, tenant, rec.HomeAccountId, rec.Username, json, markSignIn: false, stampUse: false);   // migration stamps no timestamps (item 13)
            }
            catch { /* migration is best-effort; a failed fold never blocks anything */ }
        }

        // Upsert a profile from an authenticated identity. Under the cross-process lock: a full read-modify-write of the
        // whole store, atomic temp+rename. Keyed by account (home-account-id), so two commits for the SAME account are
        // idempotent (same identity, no token) and two for DIFFERENT accounts never contend — the #233 out-of-order hazard
        // is specific to the SHARED default slot, which this store does NOT touch. Timestamp semantics (item 13):
        //   markSignIn = a FRESH sign-in just happened → stamp LastSignIn + LastUse now;
        //   stampUse    = this account was USED for an open → stamp LastUse now (a silent reuse; no new sign-in);
        //   neither (migration) = fold the record in but stamp NOTHING — unknown stays honestly unknown.
        // Best-effort by construction: a profile write never fails the open it records.
        internal static string CommitProfile(string family, string tenantId, string homeAccountId, string username, string recordJson, bool markSignIn, bool stampUse = true)
        {
            if (!PersistenceSupported || string.IsNullOrWhiteSpace(homeAccountId)) return null;
            var id = ProfileId(family, tenantId, homeAccountId);
            try
            {
                var path = ProfilesPath();
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                using (AcquireRecordLock(path))
                {
                    var list = ReadProfilesForWrite(path);
                    var e = list.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
                    var now = DateTimeOffset.UtcNow.ToString("O");
                    if (e == null) { e = new ProfileEntry { Id = id }; list.Add(e); }
                    e.Seq += 1;
                    e.Username = string.IsNullOrWhiteSpace(username) ? e.Username : username;
                    e.TenantId = tenantId;
                    e.Family = family;
                    e.HomeAccountId = NormHome(homeAccountId);   // canonical (item 14a): the stored value agrees with the id hash
                    e.RecordJson = string.IsNullOrWhiteSpace(recordJson) ? e.RecordJson : recordJson;
                    if (markSignIn) e.LastSignInUtc = now;          // a fresh sign-in stamps the sign-in time; migration never does
                    if (markSignIn || stampUse) e.LastUseUtc = now; // a use (or sign-in) stamps last-use; migration leaves it unknown
                    WriteProfilesFile(path, list);
                    return id;
                }
            }
            catch { return null; }
        }

        /// <summary>Record that a profile was USED for an open (updates last-use only). Best-effort.</summary>
        internal static void TouchProfileUse(string id)
        {
            if (!PersistenceSupported || string.IsNullOrWhiteSpace(id)) return;
            try
            {
                var path = ProfilesPath();
                if (!File.Exists(path)) return;
                using (AcquireRecordLock(path))
                {
                    var list = ReadProfilesForWrite(path);
                    var e = list.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
                    if (e == null) return;
                    e.LastUseUtc = DateTimeOffset.UtcNow.ToString("O");
                    WriteProfilesFile(path, list);
                }
            }
            catch { }
        }

        /// <summary>Why a make-default did not take (item 15): so the caller can teach the RIGHT fix instead of one blanket
        /// "signed out" message. Contended = the durable CAS was lost or its claim degraded (another change won the race).</summary>
        internal enum SetDefaultResult { Ok, NotFound, SignedOut, Contended }

        /// <summary>Make a saved profile the tenant DEFAULT: copy its identity record into the (family, tenant) default slot
        /// via the SAME crash-safe CAS the single Phase 1 record uses, so the pointer an unqualified open pins moves
        /// atomically and a stale write can never win. Returns the distinct reason on failure (item 15).</summary>
        internal static SetDefaultResult SetDefaultProfileResult(string id)
        {
            if (!PersistenceSupported || string.IsNullOrWhiteSpace(id)) return SetDefaultResult.NotFound;
            var e = ReadProfilesForDisplay().FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
            if (e == null) return SetDefaultResult.NotFound;
            if (LoadRecordFromJson(e.RecordJson) == null) return SetDefaultResult.SignedOut;   // no usable record to pin
            var path = RecordPath(e.Family, (e.TenantId ?? "").Trim().ToLowerInvariant());
            var claim = MintClaim(path);
            if (claim <= 0) return SetDefaultResult.Contended;   // reservation degraded (round-9 MEDIUM 2) — leave the default untouched
            return TryClaimRecordWrite(path, claim, () => e.RecordJson) ? SetDefaultResult.Ok : SetDefaultResult.Contended;
        }

        /// <summary>Best-effort make-default (true = it took). Used by the barrier path where only success/skip matters.</summary>
        internal static bool SetDefaultProfile(string id) => SetDefaultProfileResult(id) == SetDefaultResult.Ok;

        /// <summary>Build a credential that silently acquires as a SAVED profile (pins its record). No prompt, no write —
        /// the identity is already known. Null when the profile is unknown or signed out (its record is gone), so the caller
        /// can route a HUMAN through an interactive re-sign and REFUSE an agent. Never repoints the default.</summary>
        public static PreparedCredential BuildCredentialForProfile(string id, bool disableInteractive = false)
        {
            if (!PersistenceSupported || string.IsNullOrWhiteSpace(id)) return null;
            var e = ReadProfilesForDisplay().FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
            if (e == null) return null;
            var rec = LoadRecordFromJson(e.RecordJson);
            if (rec == null) return null;   // signed-out profile — needs an interactive re-sign (human-only)
            // disableInteractive (agent): the pinned record acquires silently; DisableAutomaticAuthentication guarantees a
            // stale cache throws AuthenticationRequiredException rather than popping a prompt on the user's machine.
            var cred = BuildCredentialWith(e.Family, (e.TenantId ?? "").Trim().ToLowerInvariant(), rec, disableInteractive);
            return new PreparedCredential { Credential = cred, ResolvedRecord = rec };
        }

        /// <summary>Whether a profile has a SAVED sign-in record to pin (the no-prompt-provable bar). An agent may pick
        /// such a profile; if its cached token has aged out, the agent's acquisition fails with an honest error rather
        /// than a prompt (DisableAutomaticAuthentication). A profile with NO record needs an interactive sign-in (human).</summary>
        public static bool ProfileIsSignedIn(string id)
        {
            if (!PersistenceSupported || string.IsNullOrWhiteSpace(id)) return false;
            var e = ReadProfilesForDisplay().FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
            return e != null && LoadRecordFromJson(e.RecordJson) != null;
        }

        // ---- test seams: exercise the store without a real MSAL sign-in --------------------------------------------

        // Seed a profile (and optionally make it the default) exactly as a successful open would, so per-open selection /
        // default semantics / migration are testable offline. Returns the profile id.
        internal static string SeedProfileForTests(string family, string tenantId, string homeAccountId, string username, string recordJson, bool makeDefault)
        {
            var id = CommitProfile(family, tenantId, homeAccountId, username, recordJson, markSignIn: true);
            if (makeDefault && id != null) SetDefaultProfile(id);
            return id;
        }
    }
}
