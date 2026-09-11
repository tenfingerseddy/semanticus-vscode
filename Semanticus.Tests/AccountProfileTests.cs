using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// Connections Phase 2 (T160 item 3): the multi-account profile store + per-open account choice. Proves the store, its
    /// migration from the Phase 1 single record, the tenant-default semantics, the human-only boundary (interactive
    /// sign-in / make-default refused for an agent), and the crash-safe file discipline — all WITHOUT a live MSAL
    /// sign-in (interactive flows can't run in tests; we seed the identity record layer the same way #233's tests do).
    /// The live two-tenant behaviour joins the standalone-door release F5 gate.
    /// </summary>
    [Collection("restore-root")]   // mutates the static ConnectionRegistry root + EntraToken persist dir — serialize it
    public sealed class AccountProfileTests : IDisposable
    {
        private readonly string _root;
        private readonly string _safeRoot;
        private readonly string _prevCacheName;

        public AccountProfileTests(RestoreRootFixture fixture)
        {
            _safeRoot = fixture.Root;
            _root = Path.Combine(Path.GetTempPath(), "sem-acct-profiles-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(_root);
            ConnectionRegistry.RootOverride = _root;
            EntraToken.PersistDirOverride = Path.Combine(_root, "auth");   // isolate the record + profile files from the real home
            _prevCacheName = EntraToken.TokenCacheNameOverride;
            EntraToken.TokenCacheNameOverride = "semanticus-acct-profiles-" + Guid.NewGuid().ToString("N").Substring(0, 8);
        }

        public void Dispose()
        {
            ConnectionRegistry.RootOverride = _safeRoot;
            EntraToken.PersistDirOverride = null;
            EntraToken.TokenCacheNameOverride = _prevCacheName;
            try { Directory.Delete(_root, true); } catch { }
        }

        // The exact JSON AuthenticationRecord.Deserialize reads (no public ctor in this Azure.Identity version), matching
        // ConnectionAccountHistoryTests — so a seeded profile carries a usable Username + a stable HomeAccountId.
        private static string Home(string username, string tenant) => "oid-" + username + "." + tenant;
        private static string RecordJsonFor(string username, string tenant) =>
            "{\"username\":\"" + username + "\",\"authority\":\"login.microsoftonline.com\",\"homeAccountId\":\"" +
            Home(username, tenant) + "\",\"tenantId\":\"" + tenant + "\",\"clientId\":\"client\",\"version\":\"1.0\"}";

        // Seed a signed-in profile (record present) as a successful open would; optionally make it the tenant default.
        private static string SeedSignedIn(string tenant, string username, bool makeDefault = false) =>
            EntraToken.SeedProfileForTests("interactive", tenant, Home(username, tenant), username, RecordJsonFor(username, tenant), makeDefault);

        // The saved-account SLOT the interactive/contoso.com barrier keys on (EntraToken.AuthRecordSlot("interactive","contoso.com")).
        private const string Slot = "interactive|contoso.com";

        // ---- item 1 (sol HIGH, round-11 regression): the #233 ordered barrier - a SILENT Phase 2 winner still advances it ----

        [Fact]
        public void A_silent_open_winner_advances_the_barrier_so_an_older_make_default_cannot_commit_behind_it()
        {
            // EVIDENCE: the Phase 2 draft gated the barrier advance on "captured a new record" (capturedNew && ...), so a
            // SILENT winner skipped CommitAuthRecordOrdered entirely and never advanced _lastAuthCommitBySlot - violating the
            // invariant pinned by A_silent_winner_advances_the_barrier_and_invalidates_an_older_pending_commit. Fixed by
            // UNCONDITIONAL winner advancement: a live-swap winner ALWAYS advances the slot; only the record payload is conditional.
            var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions);
            var older = engine.MintAuthIntentForTest();   // an older make-default, paused before its commit
            var newer = engine.MintAuthIntentForTest();   // a newer SILENT reuse of the default wins its swap
            Assert.True(engine.OpenWinnerAuthForTest(newer, Slot, capturedNew: false, hadDefault: true, makeDefault: false));  // the silent winner advances
            Assert.False(engine.ClaimAuthCommit(older, Slot));   // the older make-default is refused - it can never land behind the newer winner
        }

        [Fact]
        public void A_silent_winner_advances_the_DURABLE_sequence_so_an_older_claim_in_another_process_cannot_win()
        {
            // TWO LocalEngine instances over ONE shared record store (separate in-memory barriers) model the extension +
            // MCP engines. The round-2 fix advanced only the in-memory _lastAuthCommitBySlot, so a silent winner in engine B
            // left the DURABLE Seq untouched and engine A's older claim still won the cross-process CAS (sol round-3 HIGH).
            var path = EntraToken.RecordPathForTests("interactive", "contoso.com");
            Assert.NotNull(path);
            // Seed Alice as the tenant default (durable Seq = 1) — the account that must survive.
            Assert.True(EntraToken.TryClaimRecordWrite(path, 1, () => RecordJsonFor("alice@contoso.com", "contoso.com")));
            // Process A (engine A's slice): an older make-default of Bob MINTS its durable claim at capture, then pauses.
            var claimA = EntraToken.MintClaim(path);
            Assert.True(claimA > 1);
            // Process B: a SEPARATE engine silently wins the live swap (a plain reuse of Alice), advancing the DURABLE Seq.
            var sessionsB = new SessionManager();
            using var engineB = new LocalEngine(sessionsB);
            Assert.True(engineB.OpenWinnerAuthForTest(engineB.MintAuthIntentForTest(), Slot, capturedNew: false, hadDefault: true, makeDefault: false));
            // Process A resumes and commits Bob — REFUSED by the durable CAS (B's silent advance made claimA stale), Alice stands.
            Assert.False(EntraToken.TryClaimRecordWrite(path, claimA, () => RecordJsonFor("bob@contoso.com", "contoso.com")));
            Assert.Equal("alice@contoso.com", EntraToken.ReadSavedAccount("interactive", "contoso.com")?.Username);
        }

        [Fact]
        public void A_saved_profile_make_default_routes_through_the_barrier_and_an_older_one_cannot_win()
        {
            var aId = SeedSignedIn("contoso.com", "megan@contoso.com", makeDefault: true);
            var bId = SeedSignedIn("contoso.com", "admin@contoso.com");
            var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions);
            var older = engine.MintAuthIntentForTest();
            var newer = engine.MintAuthIntentForTest();
            Assert.True(engine.OpenWinnerAuthForTest(newer, Slot, capturedNew: false, hadDefault: true, makeDefault: false));   // newer silent winner advances the slot
            // An older make-default onto B routes the SAME barrier - refused, so SetDefaultProfile never runs and A stays default.
            Assert.False(engine.OpenWinnerAuthForTest(older, Slot, capturedNew: false, hadDefault: true, makeDefault: true, makeDefaultProfileId: bId));
            Assert.True(EntraToken.ListProfiles().Single(p => p.Id == aId).IsDefault);   // unchanged - the stale make-default never landed
            // A NEWEST make-default onto B DOES win the barrier and repoints the default (proving the write path works under the gate).
            var newest = engine.MintAuthIntentForTest();
            Assert.True(engine.OpenWinnerAuthForTest(newest, Slot, capturedNew: false, hadDefault: true, makeDefault: true, makeDefaultProfileId: bId));
            Assert.True(EntraToken.ListProfiles().Single(p => p.Id == bId).IsDefault);
        }

        // ---- the store: seed, list, credential-free, sorting ----

        [Fact]
        public void A_seeded_profile_is_listed_credential_free_and_marked_signed_in()
        {
            SeedSignedIn("contoso.com", "megan@contoso.com");
            var p = EntraToken.ListProfiles().Single();
            Assert.Equal("megan@contoso.com", p.Username);
            Assert.Equal("contoso.com", p.TenantId);
            Assert.Equal("interactive", p.Family);
            Assert.True(p.SignedIn);
            // The public DTO is structurally credential-free: it has no record/token field the JSON could carry.
            Assert.DoesNotContain(typeof(AccountProfile).GetProperties(), pr =>
                pr.Name.IndexOf("record", StringComparison.OrdinalIgnoreCase) >= 0 ||
                pr.Name.IndexOf("token", StringComparison.OrdinalIgnoreCase) >= 0 ||
                pr.Name.IndexOf("secret", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        [Fact]
        public void Two_accounts_on_one_tenant_coexist_as_distinct_profiles_and_only_the_default_is_marked()
        {
            SeedSignedIn("contoso.com", "megan@contoso.com", makeDefault: true);
            SeedSignedIn("contoso.com", "admin@contoso.com");
            var profiles = EntraToken.ListProfiles();
            Assert.Equal(2, profiles.Count);
            Assert.Single(profiles, p => p.IsDefault);
            Assert.True(profiles.Single(p => p.Username == "megan@contoso.com").IsDefault);
            Assert.False(profiles.Single(p => p.Username == "admin@contoso.com").IsDefault);
        }

        [Fact]
        public void Accounts_in_two_tenants_stay_separate_so_neither_repoints_the_other()
        {
            // Kane's real scenario: a Contoso account and an ETS account. Each is its own profile and default; signing in
            // one must never move the other's default — the whole point of Phase 2.
            SeedSignedIn("contoso.com", "megan@contoso.com", makeDefault: true);
            SeedSignedIn("fabrikam.com", "avery@fabrikam.com", makeDefault: true);
            var profiles = EntraToken.ListProfiles();
            Assert.Equal(2, profiles.Count);
            Assert.True(profiles.Single(p => p.TenantId == "contoso.com").IsDefault);
            Assert.True(profiles.Single(p => p.TenantId == "fabrikam.com").IsDefault);
        }

        // ---- migration: the Phase 1 single record becomes the first profile, idempotently, no re-sign-in ----

        [Fact]
        public async Task The_phase1_default_record_migrates_into_a_default_profile_idempotently()
        {
            // A Phase 1 install: a remembered target + a single saved record in its (interactive, tenant) default slot.
            var rec = ConnectionRegistry.Remember("xmla", "powerbi://x/mig", "DS", "DS", "contoso.com", "interactive", "megan@contoso.com");
            var path = EntraToken.RecordPathForTests("interactive", "contoso.com");
            Assert.NotNull(path);
            Assert.True(EntraToken.TryClaimRecordWrite(path, 1, () => RecordJsonFor("megan@contoso.com", "contoso.com")));
            Assert.Empty(EntraToken.ListProfiles());   // not migrated until we list through the engine

            var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions);
            var first = await engine.ListAccountProfilesAsync();
            var migrated = Assert.Single(first);
            Assert.Equal("megan@contoso.com", migrated.Username);
            Assert.True(migrated.IsDefault);    // the migrated account IS the tenant default (it held the default slot)
            Assert.True(migrated.SignedIn);
            // Idempotent: listing again does not create a second profile for the same account.
            var second = await engine.ListAccountProfilesAsync();
            Assert.Single(second);
            Assert.Equal(migrated.Id, second[0].Id);
            _ = rec;
        }

        // ---- item 13: migration stamps NO timestamps (unknown stays unknown); a real sign-in stamps them ----

        [Fact]
        public async Task Migration_does_not_fake_timestamps_but_a_real_sign_in_stamps_them()
        {
            ConnectionRegistry.Remember("xmla", "powerbi://x/ts", "DS", "DS", "contoso.com", "interactive", "megan@contoso.com");
            var path = EntraToken.RecordPathForTests("interactive", "contoso.com");
            Assert.True(EntraToken.TryClaimRecordWrite(path, 1, () => RecordJsonFor("megan@contoso.com", "contoso.com")));
            var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions);
            var migrated = Assert.Single(await engine.ListAccountProfilesAsync());
            Assert.Null(migrated.LastSignInUtc);   // migration stamps nothing — unknown stays honestly unknown (item 13)
            Assert.Null(migrated.LastUseUtc);
            // A real sign-in of the SAME account (same home-account-id → same profile) now stamps both.
            var id = SeedSignedIn("contoso.com", "megan@contoso.com");
            Assert.Equal(migrated.Id, id);
            var after = EntraToken.ListProfiles().Single(p => p.Id == id);
            Assert.NotNull(after.LastSignInUtc);
            Assert.NotNull(after.LastUseUtc);
        }

        // ---- item 14a: case-variant home-account-ids collapse to ONE profile so they can't both read as default ----

        [Fact]
        public void Case_variant_home_account_ids_collapse_to_one_profile_and_one_default()
        {
            var rec = RecordJsonFor("megan@contoso.com", "contoso.com");
            var home = Home("megan@contoso.com", "contoso.com");
            var lower = EntraToken.SeedProfileForTests("interactive", "contoso.com", home, "megan@contoso.com", rec, makeDefault: true);
            var upper = EntraToken.SeedProfileForTests("interactive", "contoso.com", home.ToUpperInvariant(), "megan@contoso.com", rec, makeDefault: false);
            Assert.Equal(lower, upper);                       // same profile id (normalized home-account-id)
            Assert.Single(EntraToken.ListProfiles());          // ...so there is ONE profile, not two
            Assert.Single(EntraToken.ListProfiles(), p => p.IsDefault);   // ...and exactly one default
        }

        // ---- item 15: SetDefault reports the distinct failure reason (not one blanket message) ----

        [Fact]
        public void SetDefaultProfile_reports_distinct_failure_reasons()
        {
            Assert.Equal(EntraToken.SetDefaultResult.NotFound, EntraToken.SetDefaultProfileResult("no-such-id"));
            var signedOut = EntraToken.SeedProfileForTests("interactive", "contoso.com", Home("gone@contoso.com", "contoso.com"), "gone@contoso.com", recordJson: null, makeDefault: false);
            Assert.Equal(EntraToken.SetDefaultResult.SignedOut, EntraToken.SetDefaultProfileResult(signedOut));
            var ok = SeedSignedIn("contoso.com", "megan@contoso.com");
            Assert.Equal(EntraToken.SetDefaultResult.Ok, EntraToken.SetDefaultProfileResult(ok));
        }

        // ---- default semantics: make-default moves the pointer; a per-open touch does NOT ----

        [Fact]
        public void SetDefaultProfile_moves_the_tenant_default_and_a_plain_use_does_not()
        {
            var aId = SeedSignedIn("contoso.com", "megan@contoso.com", makeDefault: true);
            var bId = SeedSignedIn("contoso.com", "admin@contoso.com");
            Assert.True(EntraToken.ListProfiles().Single(p => p.Id == aId).IsDefault);

            // A per-open SELECTION of B (the engine touches last-use, never the default) leaves the default on A.
            EntraToken.TouchProfileUse(bId);
            Assert.True(EntraToken.ListProfiles().Single(p => p.Id == aId).IsDefault);
            Assert.False(EntraToken.ListProfiles().Single(p => p.Id == bId).IsDefault);

            // An EXPLICIT make-default moves it to B.
            Assert.True(EntraToken.SetDefaultProfile(bId));
            Assert.True(EntraToken.ListProfiles().Single(p => p.Id == bId).IsDefault);
            Assert.False(EntraToken.ListProfiles().Single(p => p.Id == aId).IsDefault);
        }

        [Fact]
        public async Task SetDefaultAccountProfile_is_human_only_and_refuses_an_agent()
        {
            var id = SeedSignedIn("contoso.com", "megan@contoso.com", makeDefault: true);
            var b = SeedSignedIn("contoso.com", "admin@contoso.com");
            var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions);
            // An agent cannot repoint the tenant default (blast radius across every remembered model on the tenant).
            await Assert.ThrowsAsync<InvalidOperationException>(() => engine.SetDefaultAccountProfileAsync(b, "agent"));
            Assert.True(EntraToken.ListProfiles().Single(p => p.Id == id).IsDefault);   // unchanged
            // A human may, and it is recorded in the connection history.
            var after = await engine.SetDefaultAccountProfileAsync(b, "human");
            Assert.True(after.Single(p => p.Id == b).IsDefault);
            Assert.Contains(ConnectionHistory.List(), e => e.Kind == "switch" && e.Account == "admin@contoso.com");
        }

        // ---- the human-only boundary (AccountSelectionRefusal), mirroring label_connection ----

        [Fact]
        public void An_agent_is_refused_for_interactive_sign_in_make_default_and_a_signed_out_profile()
        {
            var signedOut = EntraToken.SeedProfileForTests("interactive", "contoso.com", Home("gone@contoso.com", "contoso.com"), "gone@contoso.com", recordJson: null, makeDefault: false);
            Assert.False(EntraToken.ProfileIsSignedIn(signedOut));
            // Agent: every human-only path is refused with a teaching message.
            Assert.NotNull(LocalEngine.AccountSelectionRefusal("agent", forceReauth: true, makeDefault: false, accountProfileId: null));
            Assert.NotNull(LocalEngine.AccountSelectionRefusal("agent", forceReauth: false, makeDefault: true, accountProfileId: null));
            Assert.NotNull(LocalEngine.AccountSelectionRefusal("agent", forceReauth: false, makeDefault: false, accountProfileId: signedOut));
            // Agent MAY select an already-signed-in saved profile (device-local, credential-free, silent).
            var signedIn = SeedSignedIn("contoso.com", "megan@contoso.com");
            Assert.Null(LocalEngine.AccountSelectionRefusal("agent", forceReauth: false, makeDefault: false, accountProfileId: signedIn));
            // A human may do anything (fail-closed only bites a non-human origin).
            Assert.Null(LocalEngine.AccountSelectionRefusal("human", forceReauth: true, makeDefault: true, accountProfileId: signedOut));
            // Fail closed: an UNRECOGNISED origin is treated as not-human for the dangerous paths.
            Assert.NotNull(LocalEngine.AccountSelectionRefusal("", forceReauth: true, makeDefault: false, accountProfileId: null));
        }

        [Fact]
        public async Task Open_live_refuses_an_agent_forced_sign_in_before_touching_the_endpoint()
        {
            var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions);
            // The refusal is checked BEFORE any auth or model export, so a bogus endpoint is never contacted.
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                engine.OpenLiveAsync("powerbi://x/agent", "DS", "interactive", null, "contoso.com", forceReauth: true, origin: "agent"));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                engine.OpenLiveAsync("powerbi://x/agent", "DS", "interactive", null, "contoso.com", makeDefault: true, origin: "agent"));
        }

        // ---- item 3 (BLOCKER): an agent interactive/devicecode call with NO silently-usable account is refused BEFORE a prompt ----

        [Fact]
        public async Task An_agent_interactive_open_with_an_empty_cache_is_refused_before_any_credential_build()
        {
            var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions);
            var tenant = Guid.NewGuid().ToString();   // a unique tenant: the scratch auth dir holds no saved record for it (empty cache)
            Assert.Null(EntraToken.ReadSavedAccount("interactive", tenant));
            // If the pre-refusal did NOT fire, the credential build (and its AuthenticateAsync prompt) would run; the probe
            // would be reached and throw a DIFFERENT exception, failing the InvalidOperationException expectation.
            engine.OpenLiveFailureProbeForTests = () => throw new Exception("REACHED THE BUILD - the agent pre-refusal did not fire");
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                engine.OpenLiveAsync("powerbi://x/agentnoacct", "DS", "interactive", null, tenant, origin: "agent"));
            Assert.Contains("human action", ex.Message);
            Assert.Contains("no saved account", ex.Message);
            // connect_xmla is refused the same way (BOTH doors).
            var ex2 = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                engine.ConnectXmlaAsync("powerbi://x/agentnoacct", "DS", "interactive", null, tenant, origin: "agent"));
            Assert.Contains("no saved account", ex2.Message);
            // The pure boundary: agent devicecode with no account → refused; non-prompting modes and a human → allowed.
            Assert.NotNull(LocalEngine.AgentInteractiveRefusal("agent", "devicecode", tenant, null));
            Assert.Null(LocalEngine.AgentInteractiveRefusal("agent", "azcli", tenant, null));
            Assert.Null(LocalEngine.AgentInteractiveRefusal("agent", "serviceprincipal", tenant, null));
            Assert.Null(LocalEngine.AgentInteractiveRefusal("human", "interactive", tenant, null));   // a human may prompt
        }

        [Fact]
        public void An_agent_may_open_interactive_when_a_saved_account_can_be_reused_silently()
        {
            var tenant = Guid.NewGuid().ToString();
            var path = EntraToken.RecordPathForTests("interactive", tenant);
            try
            {
                // Seed a saved tenant default (a prior human sign-in) → the agent has a silently-usable account, not refused.
                Assert.True(EntraToken.TryClaimRecordWrite(path, 1, () => RecordJsonFor("megan@contoso.com", tenant)));
                Assert.Null(LocalEngine.AgentInteractiveRefusal("agent", "interactive", tenant, null));
                // A signed-in selected profile is also fine for an agent.
                var id = SeedSignedIn(tenant, "admin@contoso.com");
                Assert.Null(LocalEngine.AgentInteractiveRefusal("agent", "interactive", tenant, id));
            }
            finally { foreach (var f in new[] { path, path + ".lock", path + ".tmp" }) { try { if (File.Exists(f)) File.Delete(f); } catch { } } }
        }

        // ---- item 4: makeDefault wire-compat (null legacy caller repoints on a forced re-sign; explicit false does not) ----

        [Fact]
        public void The_make_default_wire_compat_rule_repoints_a_legacy_forced_resign_but_not_an_explicit_phase2_add()
        {
            Assert.True(LocalEngine.RepointDefaultRule(null, forceReauth: true));    // legacy caller: a forced re-sign repoints (Phase 1 preserved)
            Assert.False(LocalEngine.RepointDefaultRule(null, forceReauth: false));  // legacy plain open never repoints
            Assert.False(LocalEngine.RepointDefaultRule(false, forceReauth: true));  // Phase 2 "sign in another account": add a profile, no repoint
            Assert.True(LocalEngine.RepointDefaultRule(true, forceReauth: false));   // explicit make-default
            Assert.True(LocalEngine.RepointDefaultRule(true, forceReauth: true));
        }

        // ---- item 5: a selected profile whose (tenant, family) does not match the request is refused (audit honesty) ----

        [Fact]
        public async Task A_profile_from_a_different_tenant_or_family_is_refused()
        {
            var contoso = SeedSignedIn("contoso.com", "megan@contoso.com");
            Assert.NotNull(LocalEngine.AccountProfileMismatchRefusal(contoso, "interactive", "fabrikam.com"));   // GENUINE mismatch (request names a different tenant) → refused
            Assert.NotNull(LocalEngine.AccountProfileMismatchRefusal(contoso, "devicecode", "contoso.com"));   // wrong family → refused
            Assert.Null(LocalEngine.AccountProfileMismatchRefusal(contoso, "interactive", "contoso.com"));     // matches → allowed
            Assert.Null(LocalEngine.AccountProfileMismatchRefusal(contoso, "interactive", null));              // TENANTLESS request adopts the profile's tenant (round-3 MEDIUM) → allowed
            Assert.Null(LocalEngine.AccountProfileMismatchRefusal(contoso, "interactive", ""));                // (blank tenant too)
            Assert.Null(LocalEngine.AccountProfileMismatchRefusal("no-such-id", "interactive", "contoso.com"));// not-found is not false-refused
            // Through the engine op: a mismatched profile throws BEFORE any auth (a bogus endpoint is never contacted).
            var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions);
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                engine.OpenLiveAsync("powerbi://x/mismatch", "DS", "interactive", null, "fabrikam.com", accountProfileId: contoso, origin: "human"));
            Assert.Contains("different tenant", ex.Message);
        }

        // ---- BuildCredentialForProfile: a signed-in profile pins silently; a signed-out / unknown one does not ----

        [Fact]
        public void BuildCredentialForProfile_pins_a_signed_in_profile_and_returns_null_for_a_signed_out_one()
        {
            var signedIn = SeedSignedIn("contoso.com", "megan@contoso.com");
            var prepared = EntraToken.BuildCredentialForProfile(signedIn);
            Assert.NotNull(prepared);
            Assert.NotNull(prepared.Credential);
            Assert.Equal("megan@contoso.com", prepared.Account);   // the pinned identity is reported
            Assert.False(prepared.HasPendingRecord);              // a silent pin — nothing new to persist

            var signedOut = EntraToken.SeedProfileForTests("interactive", "contoso.com", Home("gone@contoso.com", "contoso.com"), "gone@contoso.com", recordJson: null, makeDefault: false);
            Assert.Null(EntraToken.BuildCredentialForProfile(signedOut));   // no usable record to pin → needs interactive
            Assert.Null(EntraToken.BuildCredentialForProfile("no-such-id"));
        }

        // ---- history: a per-open selection to a DIFFERENT account is a switch; the same account is a plain open ----

        [Fact]
        public void A_per_open_selection_to_a_different_account_is_a_switch_not_an_open()
        {
            Assert.Equal("open", LocalEngine.ConnectHistoryKind(false, "megan@contoso.com", "megan@contoso.com", explicitSelection: true));
            Assert.Equal("switch", LocalEngine.ConnectHistoryKind(false, "megan@contoso.com", "admin@contoso.com", explicitSelection: true));
            Assert.Equal("open", LocalEngine.ConnectHistoryKind(false, "megan@contoso.com", "admin@contoso.com", explicitSelection: false));   // no selection = a plain open
            // Forced sign-in classification is unchanged (the Phase 1 pins still hold).
            Assert.Equal("signin", LocalEngine.ConnectHistoryKind(true, "megan@contoso.com", "megan@contoso.com"));
            Assert.Equal("switch", LocalEngine.ConnectHistoryKind(true, "megan@contoso.com", "admin@contoso.com"));
        }

        // ---- crash-safety: an unreadable store never overwrites silently; the file holds no token ----

        [Fact]
        public void A_corrupt_profile_store_degrades_to_empty_and_a_later_commit_preserves_the_bad_bytes()
        {
            var authDir = Path.Combine(_root, "auth");
            Directory.CreateDirectory(authDir);
            File.WriteAllText(Path.Combine(authDir, "account-profiles.json"), "{ not json");
            Assert.Empty(EntraToken.ListProfiles());   // read degrades, never throws

            // A commit must NOT silently clobber unreadable bytes — it moves them aside first, then writes fresh.
            SeedSignedIn("contoso.com", "megan@contoso.com");
            Assert.Single(EntraToken.ListProfiles());
            Assert.Contains(Directory.GetFiles(authDir), f => f.Contains(".corrupt-"));
        }

        [Fact]
        public void The_profile_store_file_holds_a_username_and_record_but_never_a_token()
        {
            SeedSignedIn("contoso.com", "megan@contoso.com");
            var text = File.ReadAllText(Path.Combine(_root, "auth", "account-profiles.json"));
            Assert.Contains("megan@contoso.com", text);                  // the identity metadata is kept
            Assert.DoesNotContain("password", text.ToLowerInvariant());  // never a credential
            Assert.DoesNotContain("accesstoken", text.ToLowerInvariant());
            Assert.DoesNotContain("bearer ", text.ToLowerInvariant());
            // No JWT-shaped bearer ever reaches the store (the AuthenticationRecord is identity-only).
            Assert.DoesNotMatch(new System.Text.RegularExpressions.Regex(@"eyJ[A-Za-z0-9_-]{5,}\.[A-Za-z0-9_-]{5,}\.[A-Za-z0-9_-]+"), text);
        }

        // ---- reachable from the MCP (agent) door, credential-free ----

        [Fact]
        public async Task List_account_profiles_is_reachable_from_the_MCP_door()
        {
            SeedSignedIn("contoso.com", "megan@contoso.com", makeDefault: true);
            var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions);
            var profiles = await McpTools.ListAccountProfiles(engine);
            Assert.Contains(profiles, p => p.Username == "megan@contoso.com" && p.IsDefault);
        }
    }
}
