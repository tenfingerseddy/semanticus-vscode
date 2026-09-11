using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.Entitlement;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// [T163] round 2 — the two defects an adversarial review found in the first pass, plus the regression pin for
    /// the session live-credential cache.
    ///
    /// These drive REAL credential builds (authMode "devicecode" against an ISOLATED, EMPTY auth store), so they
    /// observe production behaviour rather than a computed flag: a non-interactive credential throws
    /// AuthenticationRequiredException and NEVER reaches the device-code prompt callback, which Azure.Identity
    /// invokes only when it is genuinely about to ask a human for something.
    /// </summary>
    [Collection("restore-root")]
    public sealed class AgentNonInteractiveDoorTests : IDisposable
    {
        private readonly string _root;
        private readonly string _safeRoot;
        private readonly string _authDir;
        private readonly string _prevPersistDir;
        private readonly string _prevCacheName;
        private int _prompts;

        public AgentNonInteractiveDoorTests(RestoreRootFixture fixture)
        {
            _safeRoot = fixture.Root;
            _root = Path.Combine(Path.GetTempPath(), "sem-t163r2-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(_root);
            ConnectionRegistry.RootOverride = Path.Combine(_root, "registry");
            AgentPolicyStore.RootOverride = _root;
            ApprovalLedger.RootOverride = _root;
            // An EMPTY auth store: no saved AuthenticationRecord, so silent acquisition is impossible for every
            // identity. A credential that may prompt therefore WILL; one that may not throws instead. This is what
            // makes the assertions bite on the real Azure.Identity build rather than on our own bookkeeping.
            _authDir = Path.Combine(_root, "auth");
            Directory.CreateDirectory(_authDir);
            _prevPersistDir = EntraToken.PersistDirOverride;
            EntraToken.PersistDirOverride = _authDir;
            _prevCacheName = EntraToken.TokenCacheNameOverride;
            EntraToken.TokenCacheNameOverride = "semanticus-t163r2-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            EntraToken.DeviceCodePromptForTests = (info, ct) =>
            {
                Interlocked.Increment(ref _prompts);
                // Abort instead of printing a code and waiting on a human. Reaching here at all is the failure.
                throw new InvalidOperationException("T163-PROMPT-REACHED");
            };
        }

        public void Dispose()
        {
            EntraToken.PersistDirOverride = _prevPersistDir;
            EntraToken.TokenCacheNameOverride = _prevCacheName;
            EntraToken.DeviceCodePromptForTests = null;
            ConnectionRegistry.RootOverride = _safeRoot;
            AgentPolicyStore.RootOverride = _safeRoot;
            ApprovalLedger.RootOverride = _safeRoot;
            try { Directory.Delete(_root, true); } catch { }
        }

        private sealed class Fake : IEntitlement
        {
            public bool IsPro => true;
            public EntitlementInfo Info => new EntitlementInfo { Tier = "pro" };
        }

        // ---- BLOCKER 1: prepare_working_copy must not launder an agent origin into human authority ----
        // Deliberately does NOT install WorkingCopyConnectForTests: the existing coverage substitutes that stub and
        // therefore never reaches the real ConnectXmlaAsync call this defect lives on.
        [Fact]
        public async Task Prepare_working_copy_does_not_launder_agent_origin_into_a_prompt_capable_query_connect()
        {
            var record = ConnectionRegistry.Remember("xmla", "powerbi://example/workspace", "Published", "Published", null, "devicecode");
            var snapshotDir = Path.Combine(_root, "snapshot");
            Directory.CreateDirectory(snapshotDir);
            var snapshot = Path.Combine(snapshotDir, "Published.bim");
            File.Copy(TestModels.FindBim(), snapshot);

            var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, new Fake());
            // The local snapshot needs no Entra auth, exactly as in the reported repro — so the run reaches the
            // query attachment, which is where the origin was being dropped.
            engine.WorkspaceTokenExportForTests = _ => Task.FromResult(new LiveModelExport.Snapshot
            {
                BimPath = snapshot,
                DatabaseName = "Published",
                DatabaseCount = 1,
                DatabaseNames = new[] { "Published" }
            });

            var result = await engine.PrepareWorkingCopyAsync(record.Id, Path.Combine(_root, "models"), commit: true, origin: "agent");

            Assert.True(result.Opened);                       // the local working copy still succeeds
            Assert.False(result.QueryConnected);              // the published query model could not attach...
            Assert.Equal(0, Volatile.Read(ref _prompts));     // ...and NO device-code prompt was ever reached
            // The attach must actually have REACHED the guarded connect and been refused THERE. Without this the test
            // stays green if the query connect is removed altogether, or if it fails for any unrelated reason. This
            // exact message is the agent pre-refusal, which only fires for a non-human origin.
            Assert.Contains("is a human action, and this device has no saved account to reuse silently", result.Error ?? "");
        }

        // ---- BLOCKER 2 regression: an agent must never be handed a human's prompt-capable live credential ----
        // Records every acquisition, so the test can tell "the agent rode the human's instance" from "the agent
        // built its own".
        private sealed class RecordingCredential : Azure.Core.TokenCredential
        {
            public int Calls;
            private Azure.Core.AccessToken Issue()
            {
                Interlocked.Increment(ref Calls);
                return new Azure.Core.AccessToken("seeded-human-token", DateTimeOffset.UtcNow.AddHours(1));
            }
            public override Azure.Core.AccessToken GetToken(Azure.Core.TokenRequestContext requestContext, CancellationToken cancellationToken) => Issue();
            public override ValueTask<Azure.Core.AccessToken> GetTokenAsync(Azure.Core.TokenRequestContext requestContext, CancellationToken cancellationToken)
                => new ValueTask<Azure.Core.AccessToken>(Issue());
        }

        private const string CloudEndpoint = "powerbi://api.powerbi.com/v1.0/myorg/T163";

        // What Azure.Identity says when a credential that MAY NOT prompt cannot get a token silently. Asserting the
        // message alongside a zero prompt count is what proves the production build carried DisableAutomaticAuthentication.
        private void AssertRefusedWithoutPrompting(string message)
        {
            Assert.Equal(0, Volatile.Read(ref _prompts));
            Assert.Contains("Interactive authentication is needed", message ?? "");
        }

        private static async Task<LocalEngine> PassingModelAsync(SessionManager sessions)
        {
            var engine = new LocalEngine(sessions, new Fake());
            await engine.CreateModelAsync("Passing", 1604);
            var t = await engine.CreateTableAsync("Sales", "human");
            var amt = await engine.CreateColumnAsync(t, "Sales Amount", "Decimal", "Sales Amount", "human");
            await engine.CreateColumnAsync(t, "Customer Name", "String", "Customer Name", "human");
            await engine.SetColumnMetadataAsync(amt, true, null, null, null, "human");
            var mref = await engine.CreateMeasureAsync(t, "Total Sales", "SUM ( Sales[Sales Amount] )", "human");
            await engine.SetMeasureFormatAsync(mref, "#,0", "human");
            await engine.SetDescriptionAsync(mref, "The sum of all sales amounts across the model.", "human");
            // A real date table (DataCategory "Time" + an IsKey DateTime column with a format string). Without it the
            // Microsoft BPA corpus blocks the deploy gate on MODEL_SHOULD_HAVE_A_DATE_TABLE (sev 2, no auto-fix) and
            // deploy_live refuses on the RED gate BEFORE it ever reaches the identity/acquisition path these tests pin.
            var dt = await engine.CreateTableAsync("Date", "human");
            var dkey = await engine.CreateColumnAsync(dt, "Date", "DateTime", "Date", "human");
            await engine.SetObjectPropertyAsync(dkey, "IsKey", "true", "human");
            await engine.SetObjectPropertyAsync(dkey, "FormatString", "yyyy-mm-dd", "human");
            await engine.MarkDateTableAsync(dt, "Date", "human");
            return engine;
        }

        [Fact]
        public async Task An_agent_never_inherits_the_prompt_capable_live_credential_a_human_left_in_the_session()
        {
            var sessions = new SessionManager();
            using var engine = await PassingModelAsync(sessions);
            await engine.SetAgentPolicyEnabledAsync(false, "human");   // the permission gate is a separate protection

            // A human opened this session earlier: their prompt-capable credential sits in the live-auth cache.
            var human = new RecordingCredential();
            sessions.Current.SeedLiveCredential(LocalEngine.LiveAuthKey("devicecode", null, null), nonInteractive: false, human);

            var token = await ReviewFenceTest.TokenAsync(engine, CloudEndpoint, "Passing", "agent");
            var ex = await Assert.ThrowsAnyAsync<Exception>(() =>
                engine.DeployLiveAsync(CloudEndpoint, "Passing", "devicecode", null, null, commit: true, origin: "agent", overrideReason: null, confirmToken: token));

            Assert.Equal(0, Volatile.Read(ref human.Calls));           // the agent did NOT ride the human's instance
            Assert.Equal(0, Volatile.Read(ref _prompts));              // and built one that cannot prompt
            Assert.Contains("Interactive authentication is needed", ex.Message);
        }

        // ---- BLOCKER A: deploy_gate is reachable by an AGENT over the RPC door, not only over MCP ----
        // RemoteEngine is the agent proxy: it handshakes as RpcConnectionRole.Agent, so RpcServer installs an
        // EngineRpcTarget whose _origin is "agent". A test that only pins McpTools.DeployGate's current null argument
        // would miss this entirely, which is how the hardcoded "human" survived the first sweep.
        [Fact]
        public async Task An_agent_over_the_rpc_door_cannot_make_deploy_gate_prompt_via_its_compare_target()
        {
            var pipe = "semanticus-t163-gate-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            var sessions = new SessionManager();
            using var owner = await PassingModelAsync(sessions);
            using var server = new RpcServer(sessions, owner, pipe);
            using var serverCts = new CancellationTokenSource();
            var serverTask = server.RunAsync(serverCts.Token);
            RemoteEngine remote = null;
            try
            {
                remote = await RemoteEngine.ConnectAsync(pipe);
                // A workspace compare target on device-code auth: the gate's compare leg would sign in to read it.
                var target = new ModelRef { Kind = "workspace", Endpoint = "powerbi://api.powerbi.com/v1.0/myorg/T163", Database = "Published", AuthMode = "devicecode" };

                // REACHABILITY first. The gate swallows its compare failure, so a zero prompt count alone would stay
                // green if compareTarget processing were deleted outright. This seam sits AT the compare acquisition
                // site, so firing it proves the agent-origin RPC call really drives that leg.
                var reached = 0;
                owner.WorkspaceTokenExportForTests = _ =>
                {
                    Interlocked.Increment(ref reached);
                    throw new InvalidOperationException("T163-COMPARE-ACQUISITION-REACHED");
                };
                await remote.DeployGateAsync(target, "agent");
                Assert.True(Volatile.Read(ref reached) > 0, "deploy_gate never reached the compare acquisition for its compareTarget");

                // Now the REAL acquisition: it must refuse rather than display a device code.
                owner.WorkspaceTokenExportForTests = null;
                var gate = await remote.DeployGateAsync(target, "agent");
                Assert.NotNull(gate);
                Assert.Equal(0, Volatile.Read(ref _prompts));
            }
            finally
            {
                remote?.Dispose();
                serverCts.Cancel();
                try { await serverTask; } catch { }
                sessions.Dispose();
            }
        }

        // ---- BLOCKER B: the two-slot cache must not switch account identity ----

        // The OPEN-TIME seeding itself: both slots must exist, hold the SAME stable identity, and differ only in
        // whether they may prompt. Deleting the second seed makes this fail, which the earlier version of this test
        // did not (it asserted only that a later refusal fired, and passed with the seeding removed entirely).
        [Fact]
        public async Task Open_time_seeding_puts_one_identity_in_both_slots_with_opposite_prompt_capability()
        {
            var record = BuildRecord("bob@contoso.com", "home-bob", "t1");
            foreach (var openedByAgent in new[] { false, true })
            {
                var sessions = new SessionManager();
                using var session = await sessions.OpenAsync(TestModels.FindBim());
                var opened = new RecordingCredential();
                var prepared = new EntraToken.PreparedCredential { Credential = opened, ResolvedRecord = record };

                LocalEngine.SeedLiveCredentialSlots(session, "k", openedByAgent, opened, prepared, "devicecode", "t1");

                // The opening driver keeps the very instance that already authenticated (the no-second-prompt rule).
                Assert.Same(opened, session.PeekLiveCredentialForTest(openedByAgent));

                var built = session.PeekLiveCredentialForTest(!openedByAgent);
                Assert.NotNull(built);                                                  // the OTHER slot was seeded at all
                Assert.Equal(!openedByAgent, DisableAutomaticAuthenticationOf(built));  // ...with the opposite prompt-capability
                // The pinned record is attached on every OS (the Linux cache used to skip this).
                Assert.Equal("home-bob", PinnedHomeAccountIdOf(built));             // ...and the SAME stable identity
            }
        }

        // Fix 3: a UPN is not an identity. Alice is deleted and recreated with the same UPN, so the saved default now
        // carries the SAME username under a DIFFERENT principal. A to-source renewal must refuse, not sail through.
        [Fact]
        public async Task A_to_source_renewal_refuses_a_recycled_upn_because_identity_is_the_home_account_id()
        {
            const string bob = "bob@contoso.com";
            SeedTenantDefaultRecord("devicecode", "t1", bob, "home-bob-RECREATED");

            var sessions = new SessionManager();
            using var engine = await PassingModelAsync(sessions);
            await engine.SetAgentPolicyEnabledAsync(false, "human");
            // Opened as the ORIGINAL principal — same UPN, different home account id.
            sessions.Current.LiveOrigin = new LiveOrigin(CloudEndpoint, "Passing", "t1", "devicecode", bob, homeAccountId: "home-bob-ORIGINAL");

            var token = await ReviewFenceTest.TokenAsync(engine, CloudEndpoint, "Passing", "agent");
            var ex = await Assert.ThrowsAnyAsync<Exception>(() =>
                engine.DeployLiveAsync(CloudEndpoint, "Passing", "devicecode", null, "t1", commit: true, origin: "agent", overrideReason: null, confirmToken: token));

            Assert.Contains("will not run a live write as a different account", ex.Message);
            Assert.Equal(0, Volatile.Read(ref _prompts));
        }

        // Fix 2: an EXPLICIT different target is not bound to the source identity. Opened on tenant t1 as Bob, then
        // deploying to a different model on tenant t2 whose saved account is Carol is a normal, previously working
        // flow. It must resolve t2's own identity, not refuse because Bob != Carol.
        [Fact]
        public async Task An_explicit_different_target_resolves_its_own_identity_instead_of_the_source_account()
        {
            SeedTenantDefaultRecord("devicecode", "t2", "carol@contoso.com", "home-carol");

            var sessions = new SessionManager();
            using var engine = await PassingModelAsync(sessions);
            await engine.SetAgentPolicyEnabledAsync(false, "human");
            sessions.Current.LiveOrigin = new LiveOrigin(CloudEndpoint, "Passing", "t1", "devicecode", "bob@contoso.com", homeAccountId: "home-bob");

            var token = await ReviewFenceTest.TokenAsync(engine, "powerbi://api.powerbi.com/v1.0/myorg/Other", "OtherModel", "agent");
            var ex = await Assert.ThrowsAnyAsync<Exception>(() =>
                engine.DeployLiveAsync("powerbi://api.powerbi.com/v1.0/myorg/Other", "OtherModel", "devicecode", null, "t2",
                    commit: true, origin: "agent", overrideReason: null, confirmToken: token));

            // It fails because an agent cannot sign in silently here — NOT because we refused the identity.
            Assert.DoesNotContain("will not run a live write as a different account", ex.Message);
            AssertRefusedWithoutPrompting(ex.Message);
        }

        // Holds on EVERY platform, including the CI Ubuntu leg where no MSAL record persists and therefore no saved
        // identity exists to compare: whatever else happens, an agent renewal never reaches a prompt.
        [Fact]
        public async Task A_to_source_agent_renewal_never_prompts_even_where_no_record_persists()
        {
            var sessions = new SessionManager();
            using var engine = await PassingModelAsync(sessions);
            await engine.SetAgentPolicyEnabledAsync(false, "human");
            sessions.Current.LiveOrigin = new LiveOrigin(CloudEndpoint, "Passing", "t1", "devicecode", "bob@contoso.com", homeAccountId: "home-bob");

            var token = await ReviewFenceTest.TokenAsync(engine, CloudEndpoint, "Passing", "agent");
            var ex = await Assert.ThrowsAnyAsync<Exception>(() =>
                engine.DeployLiveAsync(CloudEndpoint, "Passing", "devicecode", null, "t1", commit: true, origin: "agent", overrideReason: null, confirmToken: token));

            // Not just "no prompt": the acquisition must actually have been REACHED and refused, otherwise this would
            // pass for a run that failed before ever building a credential.
            AssertRefusedWithoutPrompting(ex.Message);
        }

        // ---- The source/target classification must fail CLOSED ----
        // Different UNBINDS the wrong-identity check, so it must be the NARROW, PROVEN case and binding the default.
        // Every row below that is not provably a different workspace or dataset must bind, however it is spelled —
        // enumerating spellings is unwinnable, so the rule is "prove it or bind it".
        [Fact]
        public async Task The_live_target_classification_only_unbinds_on_a_proven_different_workspace_or_dataset()
        {
            var sessions = new SessionManager();
            using var session = await sessions.OpenAsync(TestModels.FindBim());
            const string ep = "powerbi://api.powerbi.com/v1.0/myorg/Contoso";
            // Source tenant deliberately NULL: a human who opened without naming a tenant is the case that used to be
            // falsely refused when a later deploy supplied the real one.
            session.LiveOrigin = new LiveOrigin(ep, "Sales", null, "devicecode", "bob@contoso.com", homeAccountId: "home-bob");

            // Collect EVERY failing row rather than stopping at the first: a classification matrix that reports one
            // row at a time hides how wide a regression is, and the whole point here is the shape of the failure set.
            var failures = new System.Collections.Generic.List<string>();
            void ExpectOn(Session sess, LocalEngine.LiveTargetKind expected, string e, string d, string t, string why)
            {
                var actual = LocalEngine.ClassifyLiveTarget(sess, e, d, t);
                if (expected != actual)
                    failures.Add($"{why}: expected {expected} but got {actual} — endpoint [{e}] database [{d}] tenant [{t}]");
            }
            void Expect(LocalEngine.LiveTargetKind expected, string e, string d, string t, string why)
                => ExpectOn(session, expected, e, d, t, why);

            const string SRC = "Sales";
            var S = LocalEngine.LiveTargetKind.Source;
            var D = LocalEngine.LiveTargetKind.Different;

            // --- the SAME model, however spelled: all must BIND ---
            Expect(S, ep, SRC, null, "exact");
            Expect(S, ep + "/", SRC, null, "trailing slash");
            Expect(S, ep.ToUpperInvariant(), "SALES", null, "casing");
            Expect(S, "powerbi://api.powerbi.com/v1.0/myorg/Cont%6Fso", SRC, null, "percent-encoded path");
            Expect(S, "powerbi://api.powerbi.com:443/v1.0/myorg/Contoso", SRC, null, "explicit default port");
            Expect(S, "powerbi://api.powerbi.com//v1.0//myorg//Contoso", SRC, null, "duplicate slashes");
            Expect(S, "powerbi://api.powerbi.com/v1.0/myorg/./Contoso", SRC, null, "dot segment");
            Expect(S, "powerbi://api.powerbi.com/v1.0/myorg/Other/../Contoso", SRC, null, "parent segment");
            Expect(S, ep + "?x=1#frag", SRC, null, "query and fragment");
            Expect(S, "  " + ep + "  ", SRC, null, "surrounding whitespace");
            Expect(S, "pbiazure://api.powerbi.com/v1.0/myorg/Contoso", SRC, null, "scheme alias");
            Expect(S, "asazure://api.powerbi.com/v1.0/myorg/Contoso", SRC, null, "scheme we never allow-listed");
            Expect(S, "powerbi://api.powerbi.com/v1.0/contoso.com/Contoso", SRC, null, "tenant-name route");
            Expect(S, "powerbi://api.powerbi.com/v1.0/72f988bf-0000-0000-0000-000000000000/Contoso", SRC, null, "tenant-guid route");
            Expect(S, ep, null, null, "null database cannot prove a different dataset");
            Expect(S, ep, "", null, "empty database");
            Expect(S, "!! not a uri !!", SRC, null, "unparseable endpoint proves nothing");
            Expect(S, "localhost:56789", SRC, null, "schemeless host:port proves nothing");
            Expect(S, "powerbi://api.powerbi.com./v1.0/myorg/Contoso", SRC, null, "terminal DNS root dot");
            Expect(S, "powerbi://API.PowerBI.com./v1.0/myorg/Contoso", SRC, null, "terminal dot plus casing");
            Expect(S, "powerbi://./v1.0/myorg/Contoso", SRC, null, "a host of only dots names nothing");
            // An endpoint that proved nothing cannot have a differing DATASET turned into proof on its own: the two
            // names might be on the very same server.
            Expect(S, "!! not a uri !!", "OtherDataset", null, "unparseable endpoint with a different database");
            Expect(S, "localhost:56789", "OtherDataset", null, "schemeless endpoint with a different database");
            // A literal percent IS part of the name: Contoso%X is a different workspace from Contoso, and the
            // bare-vs-escaped equivalence is pinned on its own fixture below.
            Expect(D, "powerbi://api.powerbi.com/v1.0/myorg/Contoso%25X", "Sales", null, "a literal percent is a different name");
            Expect(S, null, null, null, "blank target is the deploy-to-source form");

            // --- tenant NEVER decides model identity (the false-refusal blocker) ---
            Expect(S, ep, SRC, "t1", "explicit tenant against a session opened with none");
            Expect(S, ep, SRC, "contoso.com", "tenant domain");
            Expect(S, ep, SRC, "72f988bf-0000-0000-0000-000000000000", "tenant guid");

            // --- a workspace is TENANT-SCOPED: the same workspace and dataset name can exist in two tenants ---
            const string tA = "11111111-1111-1111-1111-111111111111";
            const string tB = "22222222-2222-2222-2222-222222222222";
            var guidRoute = new SessionManager();
            using var guidSession = await guidRoute.OpenAsync(TestModels.FindBim());
            guidSession.LiveOrigin = new LiveOrigin($"powerbi://api.powerbi.com/v1.0/{tA}/Finance", "Model", null, "devicecode", "a@x", homeAccountId: "home-a");
            ExpectOn(guidSession, D, $"powerbi://api.powerbi.com/v1.0/{tB}/Finance", "Model", null, "same workspace and dataset name in ANOTHER tenant");
            ExpectOn(guidSession, S, $"powerbi://api.powerbi.com/v1.0/{tA}/Finance", "Model", null, "same tenant route");
            ExpectOn(guidSession, S, "powerbi://api.powerbi.com/v1.0/myorg/Finance", "Model", null, "myorg is unknown, so it binds");
            ExpectOn(guidSession, S, "powerbi://api.powerbi.com/v1.0/contoso.com/Finance", "Model", null, "a domain alias is unknown, so it binds");

            // --- a bare percent and its escaped form are the same workspace (the measured System.Uri policy) ---
            var pct = new SessionManager();
            using var pctSession = await pct.OpenAsync(TestModels.FindBim());
            const string barePct = "powerbi://api.powerbi.com/v1.0/myorg/A%ZZ";
            const string escapedPct = "powerbi://api.powerbi.com/v1.0/myorg/A%25ZZ";
            // Assert the KEYS directly: classification alone would pass even if the bare form produced a NULL key,
            // because a null key short-circuits to Source. Non-null and equal is the claim being made.
            Assert.NotNull(LocalEngine.CanonicalXmlaKey(barePct));
            Assert.NotNull(LocalEngine.CanonicalXmlaKey(escapedPct));
            Assert.Equal(LocalEngine.CanonicalXmlaKey(barePct), LocalEngine.CanonicalXmlaKey(escapedPct));
            pctSession.LiveOrigin = new LiveOrigin(barePct, "Model", null, "devicecode", "a@x", homeAccountId: "home-a");
            ExpectOn(pctSession, S, escapedPct, "Model", null, "bare percent vs its escaped form");
            // ...and the reverse direction: an escaped-form source against a bare-form target.
            var pctRev = new SessionManager();
            using var pctRevSession = await pctRev.OpenAsync(TestModels.FindBim());
            pctRevSession.LiveOrigin = new LiveOrigin(escapedPct, "Model", null, "devicecode", "a@x", homeAccountId: "home-a");
            ExpectOn(pctRevSession, S, barePct, "Model", null, "escaped form as the source, bare form as the target");

            // --- percent-encoded unicode: composed and decomposed spellings are the same workspace ---
            var uni2 = new SessionManager();
            using var uniPath = await uni2.OpenAsync(TestModels.FindBim());
            uniPath.LiveOrigin = new LiveOrigin("powerbi://api.powerbi.com/v1.0/myorg/Caf%C3%A9", "Model", null, "devicecode", "a@x", homeAccountId: "home-a");
            ExpectOn(uniPath, S, "powerbi://api.powerbi.com/v1.0/myorg/Cafe%CC%81", "Model", null, "NFD-encoded form of the same name");
            ExpectOn(uniPath, S, "powerbi://api.powerbi.com/v1.0/myorg/Café", "Model", null, "unescaped composed form");
            ExpectOn(uniPath, D, "powerbi://api.powerbi.com/v1.0/myorg/Cafe", "Model", null, "a genuinely different name");

            // --- PROVEN different: these may unbind ---
            Expect(D, "powerbi://api.powerbi.com/v1.0/myorg/Other", SRC, null, "different workspace");
            Expect(D, "powerbi://other.powerbi.com/v1.0/myorg/Contoso", SRC, null, "different host");
            Expect(D, ep, "OtherDataset", null, "different dataset");

            Assert.True(failures.Count == 0,
                $"{failures.Count} classification row(s) wrong:" + Environment.NewLine + "  "
                + string.Join(Environment.NewLine + "  ", failures));

            // --- a unicode host and its punycode form are the same server ---
            var uni = new SessionManager();
            using var uniSession = await uni.OpenAsync(TestModels.FindBim());
            uniSession.LiveOrigin = new LiveOrigin("powerbi://пример.example/v1.0/myorg/W", "Sales", null, "devicecode", "b", homeAccountId: "home-bob");
            Assert.Equal(S, LocalEngine.ClassifyLiveTarget(uniSession, "powerbi://xn--e1afmkfd.example/v1.0/myorg/W", "Sales", null));

            // --- no live binding at all: nothing to protect ---
            using var bare = await new SessionManager().OpenAsync(TestModels.FindBim());
            Assert.Equal(D, LocalEngine.ClassifyLiveTarget(bare, ep, SRC, null));
            Assert.Equal(D, LocalEngine.ClassifyLiveTarget(null, ep, SRC, null));

        }

        // The attack end to end: presenting the source model in a slightly different form must not let an agent write
        // it as the tenant default. The final case is the CONTROL — a genuinely different model still resolves its own
        // identity, so this test cannot pass by simply refusing everything.
        [Fact]
        public async Task An_agent_cannot_escape_the_identity_check_by_respelling_the_endpoint()
        {
            SeedTenantDefaultRecord("devicecode", "t1", "alice@contoso.com", "home-alice");
            SeedTenantDefaultRecord("devicecode", "t2", "carol@contoso.com", "home-carol");

            foreach (var (variant, tenant, mustRefuseIdentity) in new[]
            {
                (CloudEndpoint + "/", "t1", true),                                  // trailing slash
                (CloudEndpoint.Replace("powerbi://", "pbiazure://"), "t1", true),   // scheme alias
                (CloudEndpoint.ToUpperInvariant(), "t1", true),                     // casing
                (CloudEndpoint.Replace("/myorg/", "/contoso.com/"), "t1", true),    // tenant-name route
                (CloudEndpoint.Replace("T163", "T16%33"), "t1", true),              // percent-encoding
                (CloudEndpoint + "?x=1#f", "t1", true),                             // query and fragment
                (CloudEndpoint.Replace("api.powerbi.com/", "api.powerbi.com./"), "t1", true),   // terminal DNS root dot
                ("powerbi://api.powerbi.com/v1.0/myorg/Different", "t2", false),    // CONTROL: a real other model
            })
            {
                _prompts = 0;
                var sessions = new SessionManager();
                using var engine = await PassingModelAsync(sessions);
                await engine.SetAgentPolicyEnabledAsync(false, "human");
                sessions.Current.LiveOrigin = new LiveOrigin(CloudEndpoint, "Passing", "t1", "devicecode", "bob@contoso.com", homeAccountId: "home-bob");

                var token = await ReviewFenceTest.TokenAsync(engine, variant, mustRefuseIdentity ? "Passing" : "DifferentModel", "agent");
                var ex = await Assert.ThrowsAnyAsync<Exception>(() =>
                    engine.DeployLiveAsync(variant, mustRefuseIdentity ? "Passing" : "DifferentModel", "devicecode", null, tenant,
                        commit: true, origin: "agent", overrideReason: null, confirmToken: token));

                if (mustRefuseIdentity)
                {
                    // Bound to the source identity, and refused because the saved default is a different principal.
                    Assert.Contains("will not run a live write as a different account", ex.Message);
                    // It must never have got as far as building the tenant default's credential.
                    Assert.DoesNotContain("Interactive authentication is needed", ex.Message);
                }
                else
                {
                    Assert.DoesNotContain("will not run a live write as a different account", ex.Message);
                    AssertRefusedWithoutPrompting(ex.Message);   // resolved its OWN identity, then refused to prompt
                }
                Assert.Equal(0, Volatile.Read(ref _prompts));
            }
        }

        // ---- MEDIUM 1: a named record with no home-account id is UNUSABLE ----
        // Accepting it on the strength of a username alone dropped a NAMED interactive account into the shared
        // "identity unknown" bucket, which bypasses the wrong-identity comparison entirely.
        [Fact]
        public void A_saved_record_without_a_home_account_id_is_not_usable()
        {
            SeedTenantDefaultRecord("devicecode", "t9", "dave@contoso.com", homeAccountId: "");
            Assert.Null(EntraToken.ReadSavedAccount("devicecode", "t9"));      // unusable ⇒ a human recaptures, an agent refuses

            SeedTenantDefaultRecord("devicecode", "t9", "dave@contoso.com", "home-dave");
            Assert.Equal("home-dave", EntraToken.ReadSavedAccount("devicecode", "t9")?.HomeAccountId);
        }

        // ---- MEDIUM 2: ONE canonical form for a home-account id, everywhere ----
        // MSAL embeds a tenant GUID whose casing is not guaranteed stable, so comparing raw would falsely REJECT the
        // same principal once its slot had been evicted.
        [Fact]
        public void Home_account_ids_are_canonicalised_the_same_way_in_the_key_and_the_comparison()
        {
            Assert.Equal(LocalEngine.LiveAuthKey("devicecode", "t1", "Home-ABC"),
                         LocalEngine.LiveAuthKey("devicecode", "t1", "home-abc"));
            Assert.Equal(LocalEngine.LiveAuthKey("interactive", "t1", "home-abc"),
                         LocalEngine.LiveAuthKey("entramfa", "t1", "HOME-ABC"));   // the interactive aliases share one record slot
            // A blank id is "identity unknown", not an identity: it must not collide with a real one.
            Assert.NotEqual(LocalEngine.LiveAuthKey("devicecode", "t1", "home-abc"),
                            LocalEngine.LiveAuthKey("devicecode", "t1", "   "));
            // Recordless modes keep the mode|tenant bucket.
            Assert.Equal(LocalEngine.LiveAuthKey("azcli", "t1", "home-abc"),
                         LocalEngine.LiveAuthKey("azcli", "t1", null));
        }

        // ---- MEDIUM 3: the PROFILE record loader is the path that changed, so exercise IT ----
        // The earlier test drove the default-record loader instead, and would have stayed green with the profile
        // change deleted.
        [Fact]
        public void A_profile_whose_record_has_no_home_account_id_is_signed_out_on_every_read()
        {
            // A profile entry that looks fine, whose STORED RECORD carries a username but no stable identity.
            var id = EntraToken.SeedProfileForTests("devicecode", "t9", "home-dave", "dave@contoso.com",
                RecordJson("dave@contoso.com", "", "t9"), makeDefault: false);
            Assert.NotNull(id);

            var listed = System.Linq.Enumerable.FirstOrDefault(EntraToken.ListProfiles(), x => x.Id == id);
            Assert.NotNull(listed);
            Assert.False(listed.SignedIn);                                            // lists as signed out...
            Assert.False(EntraToken.ProfileIsSignedIn(id));                           // ...reads as signed out...
            Assert.Null(EntraToken.BuildCredentialForProfile(id));                    // ...builds no credential...
            Assert.Equal(EntraToken.SetDefaultResult.SignedOut, EntraToken.SetDefaultProfileResult(id));   // ...and cannot become the default
        }

        // ---- LOW: the capture gate must apply the bar its comment claims ----
        [Fact]
        public void The_capture_gate_treats_a_record_with_no_home_account_id_as_unusable()
        {
            Assert.True(EntraToken.ShouldCaptureRecord(BuildRecord("dave@contoso.com", "", "t9"), forceReauth: false));
            Assert.False(EntraToken.ShouldCaptureRecord(BuildRecord("dave@contoso.com", "home-dave", "t9"), forceReauth: false));
            Assert.True(EntraToken.ShouldCaptureRecord(BuildRecord("dave@contoso.com", "home-dave", "t9"), forceReauth: true));
        }

        // A real saved DEFAULT-slot record on disk (the #233 crash-safe envelope), so ReadSavedAccount resolves this
        // account exactly as it would after a genuine sign-in.
        private static void SeedTenantDefaultRecord(string mode, string tenant, string username, string homeAccountId)
        {
            var path = EntraToken.RecordPathForTests(mode, tenant);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var envelope = "{\"Seq\":1,\"IssuedSeq\":1,\"RecordJson\":"
                         + System.Text.Json.JsonSerializer.Serialize(RecordJson(username, homeAccountId, tenant)) + "}";
            File.WriteAllText(path, envelope);
        }

        private static string RecordJson(string username, string homeAccountId, string tenant)
            => "{\"username\":\"" + username + "\",\"authority\":\"login.microsoftonline.com\",\"homeAccountId\":\""
             + homeAccountId + "\",\"tenantId\":\"" + tenant + "\",\"clientId\":\"a672d62c-fc7b-4e81-a576-e60dc46e951d\"}";

        private static Azure.Identity.AuthenticationRecord BuildRecord(string username, string homeAccountId, string tenant)
        {
            using var ms = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(RecordJson(username, homeAccountId, tenant)));
            return Azure.Identity.AuthenticationRecord.Deserialize(ms);
        }

        // Azure.Identity keeps both of these as auto-property backing fields on the credential. Reading them is how a
        // test can assert what was actually BUILT rather than what we believe we asked for. A package upgrade that
        // renames them fails loudly here rather than silently weakening the assertion.
        private static T CredentialField<T>(object credential, string property)
        {
            for (var t = credential.GetType(); t != null && t != typeof(object); t = t.BaseType)
            {
                var f = t.GetField("<" + property + ">k__BackingField",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                if (f != null) return (T)f.GetValue(credential);
            }
            throw new InvalidOperationException($"{credential.GetType().Name} has no backing field for '{property}'. "
                + "Azure.Identity's shape changed; update this assertion rather than dropping it.");
        }

        private static bool DisableAutomaticAuthenticationOf(object credential)
            => CredentialField<bool>(credential, "DisableAutomaticAuthentication");

        private static string PinnedHomeAccountIdOf(object credential)
            => CredentialField<Azure.Identity.AuthenticationRecord>(credential, "Record")?.HomeAccountId;

        [Fact]
        public async Task A_human_still_reuses_their_own_cached_live_credential()
        {
            var sessions = new SessionManager();
            using var engine = await PassingModelAsync(sessions);

            var human = new RecordingCredential();
            sessions.Current.SeedLiveCredential(LocalEngine.LiveAuthKey("devicecode", null, null), nonInteractive: false, human);

            // The push itself fails against a fake endpoint; what matters is that the SEEDED credential was the one
            // used, so splitting the cache did not reintroduce a second prompt for the human.
            var token = await ReviewFenceTest.TokenAsync(engine, CloudEndpoint, "Passing");
            await Assert.ThrowsAnyAsync<Exception>(() =>
                engine.DeployLiveAsync(CloudEndpoint, "Passing", "devicecode", null, null, commit: true, origin: "human", overrideReason: null, confirmToken: token));

            Assert.Equal(1, Volatile.Read(ref human.Calls));
            Assert.Equal(0, Volatile.Read(ref _prompts));
        }
    }
}
