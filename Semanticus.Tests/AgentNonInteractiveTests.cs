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
    /// [T163] An agent can never trigger an interactive sign-in, anywhere on the tool surface. #263 closed the
    /// open / connect / compare-read / compare-push paths; these pin the rest — Fabric Git + the ALM reads, the
    /// deploy_live lazy live credential, the cloud-report lane, SQL introspection, the data-agent tools, and
    /// export_vpax's live-stats re-auth. The rule is DeployGuard.IsAgent — fail-closed, so anything that is not
    /// exactly "human" acquires with DisableAutomaticAuthentication.
    ///
    /// These drive the REAL Azure.Identity credential build: authMode "devicecode" against an ISOLATED, EMPTY auth
    /// store, so silent acquisition is impossible for every identity. A non-interactive credential therefore throws
    /// AuthenticationRequiredException and NEVER reaches the device-code prompt callback — which Azure.Identity
    /// invokes only when it is genuinely about to ask a human for something. An earlier draft of these tests used
    /// authMode "token", which returns BEFORE any credential is built; those tests observed a computed flag rather
    /// than the production build, and stripping disableInteractive from the real builds left them all green.
    ///
    /// What this lane does and does not add: it adds NO blanket auth-mode refusal. An agent call still succeeds
    /// whenever silent authentication is available, and an agent still reaches the same op with the same arguments.
    /// What changes is that a call which would have required human interaction now FAILS instead of prompting.
    /// </summary>
    [Collection("restore-root")]
    public sealed class AgentNonInteractiveTests : IDisposable
    {
        private readonly string _root;
        private readonly string _safeRoot;
        private readonly string _prevPersistDir;
        private int _prompts;

        public AgentNonInteractiveTests(RestoreRootFixture fixture)
        {
            _safeRoot = fixture.Root;
            _root = Path.Combine(Path.GetTempPath(), "sem-t163-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(_root);
            ConnectionRegistry.RootOverride = Path.Combine(_root, "registry");
            AgentPolicyStore.RootOverride = _root;
            ApprovalLedger.RootOverride = _root;
            var authDir = Path.Combine(_root, "auth");
            Directory.CreateDirectory(authDir);
            _prevPersistDir = EntraToken.PersistDirOverride;
            EntraToken.PersistDirOverride = authDir;
            EntraToken.FabricTokenForTests = null;   // no canned bearer: the Fabric acquisition must really build a credential
            EntraToken.DeviceCodePromptForTests = (info, ct) =>
            {
                Interlocked.Increment(ref _prompts);
                throw new InvalidOperationException("T163-PROMPT-REACHED");   // reaching here at all is the failure
            };
        }

        public void Dispose()
        {
            EntraToken.PersistDirOverride = _prevPersistDir;
            EntraToken.DeviceCodePromptForTests = null;
            EntraToken.FabricTokenForTests = null;
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

        // What Azure.Identity says when a credential that MAY NOT prompt is asked for a token it cannot get
        // silently. Pinning the message is what proves the production build carried DisableAutomaticAuthentication:
        // without it the same call reaches the prompt instead.
        private const string AuthRequired = "Interactive authentication is needed";

        private void AssertRefusedWithoutPrompting(string message)
        {
            Assert.Equal(0, Volatile.Read(ref _prompts));   // no prompt was ever possible...
            Assert.Contains(AuthRequired, message ?? "");    // ...and the acquisition refused for exactly that reason
        }

        // ---- audience 1 of 3: Fabric (Fabric Git + the ALM reads + the data-agent tools) ----
        [Fact]
        public async Task Fabric_audience_agent_acquisition_refuses_instead_of_prompting()
        {
            using var engine = new LocalEngine(new SessionManager(), new Fake());
            var r = await engine.FabricGitStatusAsync("ws-1", "devicecode", null, "agent", CancellationToken.None);
            AssertRefusedWithoutPrompting(r.Error);
        }

        // ---- audience 2 of 3: Power BI / XMLA (the cloud-report lane) ----
        [Fact]
        public async Task Xmla_audience_agent_acquisition_refuses_instead_of_prompting()
        {
            using var engine = new LocalEngine(new SessionManager(), new Fake());
            var ex = await Assert.ThrowsAnyAsync<Exception>(() =>
                engine.ListReportsAsync("ws-1", "devicecode", null, "agent", CancellationToken.None));
            AssertRefusedWithoutPrompting(ex.Message);
        }

        // ---- audience 2 of 3, SECOND acquisition: analyze_cloud_reports mints a Fabric token AFTER discovery ----
        // The Power BI leg throws first for an agent, so without the discovery seam this acquisition is unreachable
        // and a regression there would survive the whole suite (it did: a hardcoded "human" here passed every test).
        [Fact]
        public async Task Analyze_cloud_reports_second_fabric_acquisition_also_refuses_instead_of_prompting()
        {
            var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, new Fake());
            await engine.CreateModelAsync("Reports", 1604);   // a model must be open: report usage reconciles to it
            engine.CloudReportDiscoveryForTests = _ => Task.FromResult(new[]
            {
                new CloudReport { Id = "r1", Name = "Sales", ReportType = "PowerBIReport" },
            });

            var ex = await Assert.ThrowsAnyAsync<Exception>(() =>
                engine.AnalyzeCloudReportsAsync("ws-1", null, consent: true, "devicecode", null, null, "agent", CancellationToken.None));
            AssertRefusedWithoutPrompting(ex.Message);
        }

        // ---- audience 3 of 3: SQL (schema probe / reconcile / test suite / spec-from-Fabric) ----
        [Fact]
        public async Task Sql_audience_agent_acquisition_refuses_instead_of_prompting()
        {
            using var engine = new LocalEngine(new SessionManager(), new Fake());
            var ex = await Assert.ThrowsAnyAsync<Exception>(() =>
                engine.AutogenerateSpecFromFabricAsync("s.datawarehouse.fabric.microsoft.com", "wh", "devicecode", "import", "agent"));
            AssertRefusedWithoutPrompting(ex.Message);
        }

        // ---- the data-agent lane rides the Fabric chokepoint: prove it really flows through it ----
        [Fact]
        public async Task Data_agent_tools_agent_acquisition_refuses_instead_of_prompting()
        {
            using var engine = new LocalEngine(new SessionManager(), new Fake());
            bool? viaChokepoint = null;
            engine.DataAgentDisableInteractiveForTests = d => viaChokepoint = d;
            var r = await engine.ListDataAgentsAsync("ws-1", "devicecode", null, "agent", CancellationToken.None);
            AssertRefusedWithoutPrompting(r.Error);
            Assert.True(viaChokepoint);   // and it went through the guarded chokepoint, not a private acquisition
        }

        // ---- export_vpax's live-stats re-authentication ----
        [Fact]
        public async Task Export_vpax_live_stats_agent_acquisition_refuses_instead_of_prompting()
        {
            var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, new Fake());
            await engine.CreateModelAsync("Vpax", 1604);
            var t = await engine.CreateTableAsync("Sales", "human");
            await engine.CreateColumnAsync(t, "Amount", "Decimal", "Amount", "human");
            sessions.Current.LiveOrigin = new LiveOrigin("powerbi://api.powerbi.com/v1.0/myorg/T163", "Vpax", null, "devicecode");
            var target = Path.Combine(_root, "t163.vpax");

            var r = await engine.ExportVpaxAsync(target, "agent");

            // The export still succeeds, metadata-only — the stats leg degrading is the pre-existing contract, and
            // this lane adds no refusal to the OP itself. What must never happen is the prompt.
            Assert.True(r.Exported);
            Assert.Equal(0, Volatile.Read(ref _prompts));
            Assert.Contains(AuthRequired, r.Note);
        }

        // ---- the deploy_live / refresh_partition lazy live credential ----
        [Fact]
        public async Task Deploy_live_agent_acquisition_refuses_instead_of_prompting()
        {
            var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, new Fake());
            await engine.CreateModelAsync("Passing", 1604);
            var t = await engine.CreateTableAsync("Sales", "human");
            var amt = await engine.CreateColumnAsync(t, "Sales Amount", "Decimal", "Sales Amount", "human");
            await engine.CreateColumnAsync(t, "Customer Name", "String", "Customer Name", "human");
            await engine.SetColumnMetadataAsync(amt, true, null, null, null, "human");
            var mref = await engine.CreateMeasureAsync(t, "Total Sales", "SUM ( Sales[Sales Amount] )", "human");
            await engine.SetMeasureFormatAsync(mref, "#,0", "human");
            await engine.SetDescriptionAsync(mref, "The sum of all sales amounts across the model.", "human");
            // A real date table, else MODEL_SHOULD_HAVE_A_DATE_TABLE (sev 2, no auto-fix) reddens the deploy gate and
            // deploy_live refuses on the gate before reaching the credential acquisition this test pins.
            var dt = await engine.CreateTableAsync("Date", "human");
            var dkey = await engine.CreateColumnAsync(dt, "Date", "DateTime", "Date", "human");
            await engine.SetObjectPropertyAsync(dkey, "IsKey", "true", "human");
            await engine.SetObjectPropertyAsync(dkey, "FormatString", "yyyy-mm-dd", "human");
            await engine.MarkDateTableAsync(dt, "Date", "human");
            await engine.SetAgentPolicyEnabledAsync(false, "human");   // the permission gate is a separate protection

            var token = await ReviewFenceTest.TokenAsync(engine, "powerbi://api.powerbi.com/v1.0/myorg/T163", "Passing", "agent");
            var ex = await Assert.ThrowsAnyAsync<Exception>(() =>
                engine.DeployLiveAsync("powerbi://api.powerbi.com/v1.0/myorg/T163", "Passing", "devicecode", null, null,
                    commit: true, origin: "agent", overrideReason: null, confirmToken: token));
            AssertRefusedWithoutPrompting(ex.Message);
        }

        // ---- the MCP door really passes "agent" ----
        // Every other test calls LocalEngine directly, so none of them would catch a door that forgot to declare
        // itself — which is exactly the defect prepare_working_copy had.
        [Fact]
        public async Task The_mcp_door_passes_agent_so_its_tools_acquire_non_interactively()
        {
            using var engine = new LocalEngine(new SessionManager(), new Fake());
            var r = await McpTools.FabricGitStatus(engine, "ws-1", "devicecode", CancellationToken.None);
            AssertRefusedWithoutPrompting(r.Error);
        }

        // ---- a human is still allowed to prompt ----
        // Asserted on the real option objects rather than by running an interactive acquisition, which would open a
        // browser (or block on a device-code flow) on whatever machine runs the suite. This is the exact option
        // Azure.Identity consults, produced by the production builders.
        [Fact]
        public void A_human_credential_is_still_built_prompt_capable()
        {
            Assert.False(EntraToken.InteractiveOptionsForTests(null, disableInteractive: false).DisableAutomaticAuthentication);
            Assert.False(EntraToken.DeviceCodeOptionsForTests(null, disableInteractive: false).DisableAutomaticAuthentication);
            Assert.True(EntraToken.InteractiveOptionsForTests(null, disableInteractive: true).DisableAutomaticAuthentication);
            Assert.True(EntraToken.DeviceCodeOptionsForTests(null, disableInteractive: true).DisableAutomaticAuthentication);
        }
    }
}
