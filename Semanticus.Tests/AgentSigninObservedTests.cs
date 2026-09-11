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
    /// So these tests are driven by an EXTERNAL observer: tools/release/signin-sentinel.ps1 watches
    /// the desktop for new top-level windows and browser processes while the test runs, and
    /// tools/release/run-block8.ps1 pairs two runs:
    ///
    ///   NEGATIVE  agent door, authMode "interactive", empty store -> refuses; nothing observed
    ///   POSITIVE  human door, same call, same store               -> raises a browser; observed
    ///
    /// The positive control is the whole point. A sentinel that could detect nothing at all would
    /// report a clean negative run, and the gate would be a check that cannot fail. run-block8.ps1
    /// requires the control to produce a SIGNIN_TITLE_MATCH specifically, and reports VOID when it
    /// does not.
    ///
    /// WHAT THE WATCHING CANNOT DO, because these tests must not be read as proving more than they
    /// do: if a browser was already running, a sign-in opened as a BACKGROUND TAB creates no new
    /// top-level window and no new process family, so it is invisible to that method. The runner
    /// therefore refuses to certify (VOID) on any machine with a browser open. Nothing in this file
    /// closes that hole; the in-process assertions below are what actually prove the refusals.
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
    /// registered application, so the flow cannot complete. That is ASSERTED by checking no token
    /// and no record were persisted, not assumed from the id.
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

        // The env var the runner sets to opt in to the browser-raising control.
        private const string PositiveOptIn = "SEMANTICUS_T163_POSITIVE";

        // A client id that is not expected to be a registered application, so the browser can be
        // raised (which is what is being observed) while the flow does not complete. This is an
        // ASSUMPTION about a value, not a guarantee from code: the control asserts the outcome
        // instead, by checking no token and no auth record were persisted afterwards. If this id
        // ever became a real registration, that assertion is what would catch it.
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

        // ---- the positive control: the sentinel must be able to SEE a sign-in surface ------------

        /// <summary>
        /// The control that makes the whole gate meaningful: the SAME browser-lane call on the HUMAN
        /// door really does raise the default browser, so the external watcher is shown to be capable
        /// of detecting a sign-in surface at all. Opt in with SEMANTICUS_T163_POSITIVE=1;
        /// run-block8.ps1 sets it for exactly one run, requires a SIGNIN_TITLE_MATCH from it, and
        /// reports VOID when it does not get one.
        ///
        /// The detection itself happens OUTSIDE this process, so this test deliberately asserts
        /// nothing about whether a window appeared — it could not observe that. What it DOES assert
        /// is the safety property: that the flow obtained nothing. No auth record is written and no
        /// token is returned, which is what makes raising a real browser here acceptable.
        /// </summary>
        [Fact]
        public async Task Human_browser_lane_really_raises_a_signin_surface_positive_control()
        {
            if (Environment.GetEnvironmentVariable(PositiveOptIn) != "1")
            {
                Console.WriteLine("T163-POSITIVE-SKIPPED: " + PositiveOptIn + " not set");
                return;
            }

            Environment.SetEnvironmentVariable("SEMANTICUS_ENTRA_CLIENT_ID", UnregisteredClientId);
            using var engine = new LocalEngine(new SessionManager(), new Fake());
            // Long enough for the browser to be raised, short enough that nobody waits on a sign-in.
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            string outcome;
            try
            {
                var r = await engine.FabricGitStatusAsync("ws-t163", "interactive", null, "human", cts.Token);
                outcome = "returned error=" + (r.Error ?? "<none>");
                // If it somehow SUCCEEDED, the "not a registered application" assumption is wrong and
                // the control just performed a real sign-in. Fail loudly rather than pass quietly.
                Assert.False(string.IsNullOrWhiteSpace(r.Error),
                    "the positive control COMPLETED a sign-in. The client id is evidently a real registration now; " +
                    "stop using it before running this again.");
            }
            catch (Exception ex) { outcome = ex.GetType().Name + ": " + ex.Message; }
            Console.WriteLine("T163-POSITIVE-OUTCOME: " + outcome);

            // The safety assertion: nothing was persisted. This is what makes the "no token, consent
            // or identity was obtained" claim a measurement instead of an assumption about a GUID.
            var records = Directory.Exists(_authDir) ? Directory.GetFiles(_authDir, "record-*.json") : Array.Empty<string>();
            Assert.Empty(records);
        }
    }
}
