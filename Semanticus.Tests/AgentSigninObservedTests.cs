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
    /// [T163] The BROWSER lane of "an agent can never trigger an interactive sign-in" — gate T163,
    /// block 8 of docs/release-evidence/kane-gate-pack.md, automated.
    ///
    /// What was already proven and is NOT rebuilt here: AgentNonInteractiveTests drives the real
    /// Azure.Identity build with authMode "devicecode" against an isolated empty store and asserts
    /// DeviceCodePromptForTests is never reached. AgentNonInteractiveDoorTests pins the identity
    /// rules. Both are offline and both stand.
    ///
    /// The gap this closes. Those tests use the DEVICE-CODE lane because it has an in-process seam.
    /// The browser lane has none: InteractiveBrowserCredential.AuthenticateAsync (EntraToken.cs:615)
    /// hands a URL to the machine's default browser, so "no sign-in window appeared" is not
    /// observable from inside the process at all. AgentNonInteractiveTests therefore asserts the
    /// human side on option OBJECTS only, with the explicit note that running it "would open a
    /// browser". That is the honest limit of the offline suite, and it is why block 8 still asked a
    /// human to watch his own screen for 25 minutes.
    ///
    /// Until 28 Sep these tests were also driven by an EXTERNAL observer (tools/release/signin-sentinel.ps1, paired by
    /// tools/release/run-block8.ps1), whose positive control ran the human-door call below for real and raised the
    /// machine's default browser. On 27 Sep a suite run did exactly that on a person's screen. No test may open a
    /// browser any more: an engine may open one only when its command line opts in (see InteractiveSignIn), which
    /// no test does, and InteractiveSignIn.BrowserForTests stands in for the browser here. So the positive control is
    /// now measured from inside the process on every run: the human door reaches the stand-in, and every agent-door
    /// call below is asserted never to reach it. run-block8.ps1's watched positive run can no longer be produced by this
    /// suite, so that runner reports VOID rather than PASS until it has another source for its control.
    ///
    /// SAFETY. Isolation covers BOTH halves of the auth store, which is not one thing:
    ///   - EntraToken.PersistDirOverride redirects the auth-record JSON to a temp directory.
    ///   - EntraToken.TokenCacheNameOverride renames the encrypted MSAL token cache. That cache
    ///     lives in the platform credential store under a NAME, not under a path, so
    ///     PersistDirOverride alone would leave the PRODUCTION cache attached
    ///     (EntraToken.cs, TokenCachePersistenceOptions) and a real credential built here could
    ///     read the tokens a real sign-in persisted.
    /// With both set, the real records and the real token cache are never read or written. The
    /// written procedure this replaces DELETED the real records and restored them afterwards.
    ///
    /// The positive control additionally runs under a SEMANTICUS_ENTRA_CLIENT_ID that is not a
    /// registered application, so even a real browser could not complete the flow. That is ASSERTED by
    /// checking no record was persisted, not assumed from the id.
    /// </summary>
    [Collection("restore-root")]
    public sealed class AgentSigninObservedTests : IDisposable
    {
        private readonly string _root;
        private readonly string _safeRoot;
        private readonly string _prevPersistDir;
        private readonly string _prevCacheName;
        private readonly string _prevClientId;
        private readonly string _authDir;
        private int _prompts;
        // Every time a call reached the point where a browser would open. The stand-in records it and fails the sign-in.
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> _browser = new System.Collections.Concurrent.ConcurrentQueue<string>();

        // A client id that is not expected to be a registered application, so the flow cannot complete
        // even if something got past the stand-in. This is an ASSUMPTION about a value, not a guarantee
        // from code: the control asserts the outcome instead, by checking no auth record was persisted
        // afterwards. If this id ever became a real registration, that assertion is what would catch it.
        private const string UnregisteredClientId = "00000000-dead-4bee-8000-000000000163";

        // What Azure.Identity says when a credential that MAY NOT prompt is asked for a token it
        // cannot get silently. Pinning it is what proves the production build carried
        // DisableAutomaticAuthentication; without it the same call reaches a prompt instead.
        private const string AuthRequired = "Interactive authentication is needed";

        public AgentSigninObservedTests(RestoreRootFixture fixture)
        {
            _safeRoot = fixture.Root;
            _root = Path.Combine(Path.GetTempPath(), "sem-t163-obs-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(_root);
            ConnectionRegistry.RootOverride = Path.Combine(_root, "registry");
            AgentPolicyStore.RootOverride = _root;
            ApprovalLedger.RootOverride = _root;
            _authDir = Path.Combine(_root, "auth");
            Directory.CreateDirectory(_authDir);
            _prevPersistDir = EntraToken.PersistDirOverride;
            EntraToken.PersistDirOverride = _authDir;          // the real auth-record JSON is never touched
            // ...and the encrypted MSAL cache is renamed, because it is keyed by NAME and not by
            // path: without this the production cache stays attached and a real credential built
            // here could read tokens a real sign-in persisted.
            _prevCacheName = EntraToken.TokenCacheNameOverride;
            EntraToken.TokenCacheNameOverride = "semanticus-t163-observed-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            EntraToken.FabricTokenForTests = null;            // no canned bearer: a credential must really be built
            _prevClientId = Environment.GetEnvironmentVariable("SEMANTICUS_ENTRA_CLIENT_ID");
            InteractiveSignIn.BrowserForTests = what => _browser.Enqueue(what);   // no call here can reach a real browser
            EntraToken.DeviceCodePromptForTests = (info, ct) =>
            {
                Interlocked.Increment(ref _prompts);
                throw new InvalidOperationException("T163-PROMPT-REACHED");   // reaching here at all is the failure
            };
        }

        public void Dispose()
        {
            EntraToken.PersistDirOverride = _prevPersistDir;
            EntraToken.TokenCacheNameOverride = _prevCacheName;
            EntraToken.DeviceCodePromptForTests = null;
            InteractiveSignIn.BrowserForTests = null;
            EntraToken.FabricTokenForTests = null;
            Environment.SetEnvironmentVariable("SEMANTICUS_ENTRA_CLIENT_ID", _prevClientId);
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

        private void AssertRefusedWithoutPrompting(string message)
        {
            Assert.Equal(0, Volatile.Read(ref _prompts));
            Assert.Empty(_browser);   // the agent's call never even reached the point where a browser would open
            Assert.Contains(AuthRequired, message ?? "");
        }

        /// <summary>
        /// A model complete enough that the deploy gate is GREEN. Without this the live paths stop at
        /// the gate ("the deploy gate is RED") and never reach the auth chokepoint, so a refusal would
        /// be the wrong refusal. The agent-permissions gate is switched off for the same reason: it is
        /// a separate protection, and it also fails before auth.
        /// </summary>
        private static async Task BuildGatePassingModelAsync(LocalEngine engine, string name)
        {
            await engine.CreateModelAsync(name, 1604);
            var t = await engine.CreateTableAsync("Sales", "human");
            var amt = await engine.CreateColumnAsync(t, "Sales Amount", "Decimal", "Sales Amount", "human");
            await engine.CreateColumnAsync(t, "Customer Name", "String", "Customer Name", "human");
            await engine.SetColumnMetadataAsync(amt, true, null, null, null, "human");
            var mref = await engine.CreateMeasureAsync(t, "Total Sales", "SUM ( Sales[Sales Amount] )", "human");
            await engine.SetMeasureFormatAsync(mref, "#,0", "human");
            await engine.SetDescriptionAsync(mref, "The sum of all sales amounts across the model.", "human");

            // A MARKED DATE TABLE IS NOW PART OF "GATE GREEN". MODEL_SHOULD_HAVE_A_DATE_TABLE ships at severity 2
            // with no auto-fix, so it blocks, and a model without one cannot pass. Without this the gate goes RED
            // first and DeployLiveAsync throws "the deploy gate is RED (1 blocker)" instead of the interactive-auth
            // refusal, so the sign-in assertion would be reading the wrong refusal and proving nothing about
            // sign-in. Repairing the fixture is the honest fix here; relaxing the gate to suit a test is not.
            var dt = await engine.CreateTableAsync("Date", "human");
            var dkey = await engine.CreateColumnAsync(dt, "Date", "DateTime", "Date", "human");
            await engine.SetObjectPropertyAsync(dkey, "IsKey", "true", "human");
            await engine.SetObjectPropertyAsync(dkey, "FormatString", "yyyy-mm-dd", "human");
            await engine.MarkDateTableAsync(dt, "Date", "human");

            await engine.SetAgentPolicyEnabledAsync(false, "human");
        }

        // ---- the negative run: every chokepoint, on the BROWSER lane, from the agent door --------
        // authMode "interactive" is the lane with no in-process seam (EntraToken.cs:146 ->
        // InteractiveBrowserCredential). The existing suite only ever drives "devicecode" here.

        [Fact]
        public async Task Agent_browser_lane_fabric_chokepoint_refuses_and_raises_nothing()
        {
            using var engine = new LocalEngine(new SessionManager(), new Fake());
            var r = await engine.FabricGitStatusAsync("ws-t163", "interactive", null, "agent", CancellationToken.None);
            AssertRefusedWithoutPrompting(r.Error);
        }

        [Fact]
        public async Task Agent_browser_lane_xmla_chokepoint_refuses_and_raises_nothing()
        {
            using var engine = new LocalEngine(new SessionManager(), new Fake());
            var ex = await Assert.ThrowsAnyAsync<Exception>(() =>
                engine.ListReportsAsync("ws-t163", "interactive", null, "agent", CancellationToken.None));
            AssertRefusedWithoutPrompting(ex.Message);
        }

        [Fact]
        public async Task Agent_browser_lane_sql_chokepoint_refuses_and_raises_nothing()
        {
            using var engine = new LocalEngine(new SessionManager(), new Fake());
            var ex = await Assert.ThrowsAnyAsync<Exception>(() =>
                engine.AutogenerateSpecFromFabricAsync("s.datawarehouse.fabric.microsoft.com", "wh", "interactive", "import", "agent"));
            AssertRefusedWithoutPrompting(ex.Message);
        }

        [Fact]
        public async Task Agent_browser_lane_data_agent_tools_refuse_and_raise_nothing()
        {
            using var engine = new LocalEngine(new SessionManager(), new Fake());
            bool? viaChokepoint = null;
            engine.DataAgentDisableInteractiveForTests = d => viaChokepoint = d;
            var r = await engine.ListDataAgentsAsync("ws-t163", "interactive", null, "agent", CancellationToken.None);
            AssertRefusedWithoutPrompting(r.Error);
            Assert.True(viaChokepoint);
        }

        [Fact]
        public async Task Agent_browser_lane_deploy_live_refuses_and_raises_nothing()
        {
            var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, new Fake());
            // The model has to pass the deploy gate, or deploy_live stops at a RED gate and the auth
            // chokepoint is never reached — the assertion would then prove nothing about sign-in.
            await BuildGatePassingModelAsync(engine, "T163Obs");

            var token = await ReviewFenceTest.TokenAsync(engine, "powerbi://api.powerbi.com/v1.0/myorg/T163", "T163Obs", "agent");
            var ex = await Assert.ThrowsAnyAsync<Exception>(() =>
                engine.DeployLiveAsync("powerbi://api.powerbi.com/v1.0/myorg/T163", "T163Obs", "interactive", null, null,
                    commit: true, origin: "agent", overrideReason: null, confirmToken: token));
            AssertRefusedWithoutPrompting(ex.Message);
        }

        /// <summary>
        /// Block 8 step 7 records refresh_partition as "covered by the same one place and NOT separately
        /// run", because refreshing data is not part of the sitting. It does share AcquireLiveTokenAsync
        /// (LocalEngine.cs:2886) with deploy_live, so the automation can turn that note into a real pass.
        ///
        /// It must be driven with commit:true to prove anything. LocalEngine.cs:3209 returns the dry-run
        /// report BEFORE any credential is built ("never connects, never executes"), so a commit:false
        /// call cannot reach the chokepoint and asserting a refusal on it would be a check that proves
        /// nothing about sign-in.
        ///
        /// Nothing is written, and the reason is ORDER, not the endpoint: token acquisition at :3225
        /// refuses before LiveRefresh.RefreshPartition at :3226 is reached, so no request is ever sent.
        /// The endpoint names a workspace nobody is expected to own, but that is a convention, not a
        /// guarantee the code enforces; the refusal is what makes this safe.
        /// </summary>
        [Fact]
        public async Task Agent_browser_lane_refresh_partition_refuses_and_raises_nothing()
        {
            var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, new Fake());
            await BuildGatePassingModelAsync(engine, "T163Refresh");
            var parts = await engine.ListPartitionsAsync("table:Sales");
            Assert.NotEmpty(parts);

            var ex = await Assert.ThrowsAnyAsync<Exception>(() =>
                engine.RefreshPartitionAsync(parts[0].Ref, "Full", "powerbi://api.powerbi.com/v1.0/myorg/T163",
                    "T163Refresh", "interactive", null, null, commit: true, origin: "agent"));
            AssertRefusedWithoutPrompting(ex.Message);
        }

        /// <summary>
        /// export_vpax does NOT refuse: by design it still writes a metadata-only file when it cannot
        /// read live statistics. Block 8 step 10 says so explicitly and judges it differently. What
        /// must never happen is a window, and the note must be honest that the stats are missing.
        /// </summary>
        [Fact]
        public async Task Agent_browser_lane_export_vpax_degrades_honestly_and_raises_nothing()
        {
            var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, new Fake());
            await engine.CreateModelAsync("T163Vpax", 1604);
            var t = await engine.CreateTableAsync("Sales", "human");
            await engine.CreateColumnAsync(t, "Amount", "Decimal", "Amount", "human");
            sessions.Current.LiveOrigin = new LiveOrigin("powerbi://api.powerbi.com/v1.0/myorg/T163", "T163Vpax", null, "interactive");

            var r = await engine.ExportVpaxAsync(Path.Combine(_root, "t163.vpax"), "agent");

            Assert.True(r.Exported);
            Assert.Equal(0, Volatile.Read(ref _prompts));
            Assert.Empty(_browser);
            Assert.Contains(AuthRequired, r.Note);
        }

        /// <summary>The MCP door really declares itself an agent — the defect prepare_working_copy had.</summary>
        [Fact]
        public async Task Agent_browser_lane_over_the_mcp_door_refuses_and_raises_nothing()
        {
            using var engine = new LocalEngine(new SessionManager(), new Fake());
            var r = await McpTools.FabricGitStatus(engine, "ws-t163", "interactive", CancellationToken.None);
            AssertRefusedWithoutPrompting(r.Error);
        }

        // ---- the positive control: the human door really reaches the browser, and a stand-in takes its place ----

        /// <summary>
        /// The control that makes the negatives meaningful: the SAME browser-lane call on the HUMAN door really does
        /// reach the point where a browser would open, so "the agent's call never reached it" is a check that can fail.
        /// It used to raise the real default browser for an external watcher (opt-in, SEMANTICUS_T163_POSITIVE=1), and
        /// one run did so on a person's screen on 27 Sep. Now the stand-in is reached instead, on every run, and the
        /// sign-in fails: nothing is opened, and no record is written.
        /// </summary>
        [Fact]
        public async Task Human_browser_lane_reaches_the_browser_and_a_stand_in_takes_its_place_positive_control()
        {
            using var press = SignInPress.Press();   // a person's press: only a press may reach the browser step
            Environment.SetEnvironmentVariable("SEMANTICUS_ENTRA_CLIENT_ID", UnregisteredClientId);
            using var engine = new LocalEngine(new SessionManager(), new Fake());
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            var r = await engine.FabricGitStatusAsync("ws-t163", "interactive", null, "human", cts.Token);

            Assert.NotEmpty(_browser);                                            // the human door reached the browser step
            Assert.Contains("A test stood in for the browser", r.Error ?? "");    // and the stand-in, not a browser, answered
            Assert.Equal(0, Volatile.Read(ref _prompts));

            // The safety assertion: nothing was persisted. This is what makes the "no token, consent
            // or identity was obtained" claim a measurement instead of an assumption about a GUID.
            var records = Directory.Exists(_authDir) ? Directory.GetFiles(_authDir, "record-*.json") : Array.Empty<string>();
            Assert.Empty(records);
        }
    }
}
