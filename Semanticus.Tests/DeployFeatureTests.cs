using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.Entitlement;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// The DEPLOY features end-to-end through the LocalEngine ops — driven against a FAKE Fabric REST (a scripted
    /// HttpMessageHandler behind FabricRest.TestClientFactory) + a canned bearer (EntraToken.FabricTokenForTests), so
    /// NO live tenant is touched. Complements the FabricRest-primitive coverage in Semanticus.CicdSmoke (pagination /
    /// LRO / parse) and the pure LiveDeploy.SyncModels diff/apply in LiveDeploySyncTests: here we pin the OP behaviour
    /// the human/agent doors actually reach — the deploy gate verdicts, the New/Update preview, the dry-run-by-default
    /// + prod-confirm-token + agent-can't-promote gate matrix, the accountable forceOverride, cicd publish/generate
    /// refusals, deployment history + pipeline reads (incl. pagination), and Fabric-Git round-trips. Every refusal is
    /// asserted for the RESULT-CONTRACT tone (the error names the recovery). One class ⇒ serial (the seams are static;
    /// the model singleton also forbids cross-class parallelism, see TestModels.cs).
    /// </summary>
    [Collection("deploy-serial")]
    public sealed class DeployFeatureTests : IDisposable
    {
        public void Dispose() { FabricRest.TestClientFactory = null; EntraToken.FabricTokenForTests = null; }

        private sealed class Fake : IEntitlement
        {
            public bool IsPro { get; }
            public EntitlementInfo Info { get; }
            public Fake(bool pro) { IsPro = pro; Info = new EntitlementInfo { Tier = pro ? "pro" : "free" }; }
        }

        // ---- the fake Fabric REST: a URL+method router over a shared handler that records every call ----
        private sealed class FakeFabric : HttpMessageHandler
        {
            private readonly Func<string, string, string, (HttpStatusCode status, string body, (string, string)[]? headers)> _route;
            public readonly List<(string method, string path, string query, string? body)> Calls = new();
            public FakeFabric(Func<string, string, string, (HttpStatusCode, string, (string, string)[]?)> route) => _route = route;
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
            {
                var method = req.Method.Method;
                var path = req.RequestUri!.AbsolutePath;   // e.g. /v1/deploymentPipelines/p/stages
                var query = req.RequestUri.Query;
                var body = req.Content != null ? await req.Content.ReadAsStringAsync(ct) : null;
                Calls.Add((method, path, query, body));
                var (status, respBody, headers) = _route(method, path, query);
                var resp = new HttpResponseMessage(status) { Content = new StringContent(respBody ?? "", Encoding.UTF8, "application/json") };
                if (headers != null) foreach (var (k, v) in headers) resp.Headers.TryAddWithoutValidation(k, v);
                return resp;
            }
        }

        // Wire BOTH test seams: a canned bearer (so the op's token acquire doesn't hit Azure.Identity) + a factory that
        // hands FabricRest a fresh HttpClient over the shared scripted handler (disposeHandler:false — the op disposes
        // its client each call, but the handler must survive across the several calls one op makes).
        private FakeFabric InstallFabric(Func<string, string, string, (HttpStatusCode, string, (string, string)[]?)> route)
        {
            var handler = new FakeFabric(route);
            EntraToken.FabricTokenForTests = () => "test-bearer";
            FabricRest.TestClientFactory = () => new HttpClient(handler, disposeHandler: false) { BaseAddress = new Uri(FabricRest.BaseUrl), Timeout = TimeSpan.FromSeconds(100) };
            return handler;
        }

        private static (HttpStatusCode, string, (string, string)[]?) Ok(string body) => (HttpStatusCode.OK, body, null);
        private static (HttpStatusCode, string, (string, string)[]?) Accepted(params (string, string)[] hdrs) => (HttpStatusCode.Accepted, "", hdrs);
        private static (HttpStatusCode, string, (string, string)[]?) Err(int status, string body) => ((HttpStatusCode)status, body, null);

        private const string Stages = "{\"value\":[" +
            "{\"id\":\"s0\",\"order\":0,\"displayName\":\"Development\",\"workspaceId\":\"w0\",\"workspaceName\":\"Contoso [Dev]\",\"isPublic\":false}," +
            "{\"id\":\"s1\",\"order\":1,\"displayName\":\"Test\",\"workspaceId\":\"w1\",\"workspaceName\":\"Contoso [Test]\",\"isPublic\":false}," +
            "{\"id\":\"s2\",\"order\":2,\"displayName\":\"Production\",\"workspaceId\":\"w2\",\"workspaceName\":\"Contoso [Prod]\",\"isPublic\":true}]}";
        // Two items: one paired to a target (⇒ Update), one unpaired (⇒ New).
        private const string Items = "{\"value\":[" +
            "{\"itemId\":\"i1\",\"itemDisplayName\":\"Contoso\",\"itemType\":\"SemanticModel\",\"targetItemId\":\"t1\"}," +
            "{\"itemId\":\"i2\",\"itemDisplayName\":\"Returns\",\"itemType\":\"SemanticModel\"}]}";

        // The standard read+deploy router used by most tests. Pagination/error variants pass their own.
        private static (HttpStatusCode, string, (string, string)[]?) StdRoute(string method, string path, string query)
        {
            if (method == "GET" && path.EndsWith("/stages")) return Ok(Stages);
            if (method == "GET" && path.Contains("/stages/") && path.EndsWith("/items")) return Ok(Items);
            if (method == "POST" && path.EndsWith("/deploy")) return Accepted(("x-ms-operation-id", "op-dep"), ("deployment-id", "dep-1"));
            if (method == "GET" && path.Contains("/operations/")) return Ok("{\"status\":\"Succeeded\",\"percentComplete\":100}");   // LRO poll
            if (method == "GET" && path.EndsWith("/operations")) return Ok(
                "{\"value\":[{\"id\":\"h1\",\"type\":\"Deploy\",\"status\":\"Succeeded\",\"executionEndTime\":\"2026-06-24T09:12:00Z\"," +
                "\"sourceStageId\":\"s1\",\"targetStageId\":\"s2\",\"note\":{\"content\":\"June release\",\"isTruncated\":false}," +
                "\"preDeploymentDiffInformation\":{\"newItemsCount\":1,\"differentItemsCount\":2,\"noDifferenceItemsCount\":3}," +
                "\"performedBy\":{\"id\":\"u1\",\"type\":\"User\",\"displayName\":\"kane@contoso.com\"}}]}");
            if (method == "POST" && path.EndsWith("/commitToGit")) return Accepted(("x-ms-operation-id", "op-c"));
            if (method == "GET" && path.EndsWith("/git/status")) return Ok(
                "{\"workspaceHead\":\"aaa\",\"remoteCommitHash\":\"bbb\",\"changes\":[{\"itemMetadata\":{\"itemIdentifier\":{\"objectId\":\"o1\",\"logicalId\":\"l1\"}," +
                "\"itemType\":\"SemanticModel\",\"displayName\":\"Sales\"},\"workspaceChange\":\"Modified\",\"conflictType\":\"None\"}]}");
            if (method == "GET" && path.EndsWith("/git/connection")) return Ok(
                "{\"gitConnectionState\":\"ConnectedAndInitialized\",\"gitProviderDetails\":{\"gitProviderType\":\"AzureDevOps\"," +
                "\"organizationName\":\"Contoso\",\"projectName\":\"BI\",\"repositoryName\":\"models\",\"branchName\":\"main\",\"directoryName\":\"/\"}," +
                "\"gitSyncDetails\":{\"head\":\"abc123\",\"lastSyncTime\":\"2026-06-20T10:00:00Z\"}}");
            if (method == "GET" && path.EndsWith("/deploymentPipelines")) return Ok("{\"value\":[{\"id\":\"p1\",\"displayName\":\"Contoso Sales\",\"description\":\"Sales model + report\"}]}");
            return Err(404, "{\"errorCode\":\"NotFound\",\"message\":\"no route\"}");
        }

        private bool AnyDeployPost(FakeFabric h) => h.Calls.Any(c => c.method == "POST" && c.path.EndsWith("/deploy"));

        // A clean, gate-PASSING model: a described measure with a format string, the numeric fact column hidden — so
        // neither the >50%-undescribed readiness gate nor a blocking BPA format-string error trips.
        private static async Task<LocalEngine> PassingModelAsync()
        {
            var engine = new LocalEngine(new SessionManager(), new Fake(true));
            await engine.CreateModelAsync("Passing", 1604);
            var t = await engine.CreateTableAsync("Sales", "human");
            var amt = await engine.CreateColumnAsync(t, "Sales Amount", "Decimal", "Sales Amount", "human");
            await engine.CreateColumnAsync(t, "Customer Name", "String", "Customer Name", "human");
            await engine.SetColumnMetadataAsync(amt, true, null, null, null, "human");   // hide the numeric column (no format-string BPA error)
            var mref = await engine.CreateMeasureAsync(t, "Total Sales", "SUM ( Sales[Sales Amount] )", "human");
            await engine.SetMeasureFormatAsync(mref, "#,0", "human");
            await engine.SetDescriptionAsync(mref, "The sum of all sales amounts across the model.", "human");
            return engine;
        }

        // A gate-BLOCKING model: a visible measure with no description ⇒ >50% of visible measures undescribed ⇒ the
        // readiness hard-gate caps the grade and populates GatedBy.
        private static async Task<LocalEngine> BlockedModelAsync()
        {
            var engine = new LocalEngine(new SessionManager(), new Fake(true));
            await engine.CreateModelAsync("Blocked", 1604);
            var t = await engine.CreateTableAsync("Sales", "human");
            await engine.CreateColumnAsync(t, "Customer Name", "String", "Customer Name", "human");
            await engine.CreateMeasureAsync(t, "Total Sales", "1", "human");   // visible, undescribed
            return engine;
        }

        // ============================================================================================
        // 1) deploy_gate — pass and block verdicts, blockers named.
        // ============================================================================================
        [Fact]
        public async Task DeployGate_passes_on_a_clean_model_with_no_blockers()
        {
            using var engine = await PassingModelAsync();
            var gate = await engine.DeployGateAsync(null);
            Assert.True(gate.Pass, "expected a passing gate, blockers were: " + string.Join(" | ", gate.Blockers));
            Assert.Empty(gate.Blockers);
            Assert.Equal("Gate passed.", gate.Note);
        }

        [Fact]
        public async Task DeployGate_blocks_and_names_the_blockers_on_a_red_model()
        {
            using var engine = await BlockedModelAsync();
            var gate = await engine.DeployGateAsync(null);
            Assert.False(gate.Pass);
            Assert.NotEmpty(gate.Blockers);
            Assert.Contains("Gate blocked", gate.Note);           // the note names the recovery-relevant blockers
            Assert.Contains(gate.Blockers, b => b.Contains("undescribed") || b.Contains("BPA"));
        }

        // ============================================================================================
        // 2) preview_deploy — New-vs-Update pairing; strictly read-only (no deploy POST).
        // ============================================================================================
        [Fact]
        public async Task PreviewDeploy_pairs_new_vs_update_and_writes_nothing()
        {
            var h = InstallFabric(StdRoute);
            using var engine = new LocalEngine(new SessionManager(), new Fake(true));   // no model open ⇒ gate advisory
            var prev = await engine.PreviewDeployAsync("p1", "s1", "s2", "azcli", null);

            Assert.Null(prev.Error);
            Assert.Equal("Test", prev.SourceStageName);
            Assert.Equal("Production", prev.TargetStageName);
            Assert.True(prev.TargetIsProd);
            Assert.Equal(1, prev.NewCount);
            Assert.Equal(1, prev.UpdateCount);
            Assert.Equal("New", prev.Items.Single(i => i.ItemId == "i2").State);
            Assert.Equal("Update", prev.Items.Single(i => i.ItemId == "i1").State);
            Assert.False(AnyDeployPost(h));   // read-only: the source/target reads happened, but nothing was deployed
        }

        // ============================================================================================
        // 3) deploy_stage — dry-run default, non-prod commit, prod human/agent, forceOverride.
        // ============================================================================================
        [Fact]
        public async Task DeployStage_dry_run_is_the_default_and_posts_nothing()
        {
            var h = InstallFabric(StdRoute);
            using var engine = new LocalEngine(new SessionManager(), new Fake(true));
            var r = await engine.DeployStageAsync("p1", "s0", "s1", null, null, commit: false, confirmToken: null,
                forceOverride: false, authMode: "azcli", tenantId: null, origin: "human");

            Assert.False(r.Committed);
            Assert.Contains("DRY RUN", r.Plan);
            Assert.Equal(1, r.NewCount);
            Assert.Equal(1, r.UpdateCount);
            Assert.Null(r.ConfirmToken);          // non-prod target ⇒ no token
            Assert.False(AnyDeployPost(h));
        }

        [Fact]
        public async Task DeployStage_agent_commit_to_a_nonprod_stage_posts_with_the_right_body()
        {
            var h = InstallFabric(StdRoute);
            using var engine = new LocalEngine(new SessionManager(), new Fake(true));   // no model ⇒ gate passes
            var r = await engine.DeployStageAsync("p1", "s0", "s1", null, null, commit: true, confirmToken: null,
                forceOverride: false, authMode: "azcli", tenantId: null, origin: "agent");

            Assert.True(r.Committed, r.Error ?? r.Plan);
            Assert.Equal("Succeeded", r.Status);
            var post = h.Calls.Single(c => c.method == "POST" && c.path.EndsWith("/deploy"));
            Assert.Contains("\"sourceStageId\":\"s0\"", post.body);
            Assert.Contains("\"targetStageId\":\"s1\"", post.body);
            Assert.DoesNotContain("\"items\"", post.body);   // items=null ⇒ deploy ALL
        }

        [Fact]
        public async Task DeployStage_agent_cannot_commit_to_production()
        {
            var h = InstallFabric(StdRoute);
            using var engine = new LocalEngine(new SessionManager(), new Fake(true));
            var r = await engine.DeployStageAsync("p1", "s1", "s2", null, null, commit: true, confirmToken: null,
                forceOverride: false, authMode: "azcli", tenantId: null, origin: "agent");

            Assert.False(r.Committed);
            Assert.Contains("agent cannot promote", r.Error);
            Assert.Contains("human must confirm", r.Error);   // the refusal names who can recover it
            Assert.False(AnyDeployPost(h));
        }

        [Fact]
        public async Task DeployStage_prod_confirm_token_is_a_human_only_round_trip()
        {
            var h = InstallFabric(StdRoute);
            using var engine = new LocalEngine(new SessionManager(), new Fake(true));

            // Agent's prod dry-run must NOT surface the token.
            var agentPrev = await engine.DeployStageAsync("p1", "s1", "s2", null, null, commit: false, confirmToken: null,
                forceOverride: false, authMode: "azcli", tenantId: null, origin: "agent");
            Assert.Null(agentPrev.ConfirmToken);
            Assert.Contains("agent cannot complete", agentPrev.Plan);

            // Human's prod dry-run surfaces the token…
            var humanPrev = await engine.DeployStageAsync("p1", "s1", "s2", null, null, commit: false, confirmToken: null,
                forceOverride: false, authMode: "azcli", tenantId: null, origin: "human");
            Assert.False(string.IsNullOrEmpty(humanPrev.ConfirmToken));
            Assert.StartsWith("PROD-", humanPrev.ConfirmToken);
            Assert.Contains("confirmToken", humanPrev.Plan);

            // …a WRONG token is refused with the recovery instruction…
            var wrong = await engine.DeployStageAsync("p1", "s1", "s2", null, null, commit: true, confirmToken: "PROD-000000",
                forceOverride: false, authMode: "azcli", tenantId: null, origin: "human");
            Assert.False(wrong.Committed);
            Assert.Contains("confirmToken shown by the dry-run preview", wrong.Error);
            Assert.False(AnyDeployPost(h));

            // …the RIGHT token (from the human preview, same item-set) proceeds to the live POST.
            var ok = await engine.DeployStageAsync("p1", "s1", "s2", null, null, commit: true, confirmToken: humanPrev.ConfirmToken,
                forceOverride: false, authMode: "azcli", tenantId: null, origin: "human");
            Assert.True(ok.Committed, ok.Error);
            Assert.True(AnyDeployPost(h));
        }

        [Fact]
        public async Task DeployStage_forceOverride_needs_a_reason_and_is_recorded()
        {
            var h = InstallFabric(StdRoute);
            using var engine = await BlockedModelAsync();   // RED gate open ⇒ commit is blocked

            // A failing gate blocks a commit with no override.
            var blocked = await engine.DeployStageAsync("p1", "s0", "s1", null, null, commit: true, confirmToken: null,
                forceOverride: false, authMode: "azcli", tenantId: null, origin: "human");
            Assert.False(blocked.Committed);
            Assert.Contains("readiness gate", blocked.Error);
            Assert.Contains("forceOverride", blocked.Error);

            // forceOverride WITHOUT a reason is refused (accountable override).
            var noReason = await engine.DeployStageAsync("p1", "s0", "s1", null, null, commit: true, confirmToken: null,
                forceOverride: true, authMode: "azcli", tenantId: null, origin: "human");
            Assert.False(noReason.Committed);
            Assert.Contains("overrideReason", noReason.Error);
            Assert.False(AnyDeployPost(h));

            // A reasoned override ships past the RED gate (and records the accountable checkpoint before the POST).
            var overridden = await engine.DeployStageAsync("p1", "s0", "s1", null, null, commit: true, confirmToken: null,
                forceOverride: true, authMode: "azcli", tenantId: null, origin: "human", overrideReason: "hotfix approved by data owner");
            Assert.True(overridden.Committed, overridden.Error);
            Assert.True(AnyDeployPost(h));
        }

        // ============================================================================================
        // 4) deploy_live — the validation/refusal legs that run BEFORE any XMLA connection. The live push
        //    (LiveDeploy.SyncSessionToLive → AMO SaveChanges) needs a real Analysis Services endpoint and is
        //    covered by the pure diff/apply core in LiveDeploySyncTests + the live smoke — see the return report.
        // ============================================================================================
        [Fact]
        public async Task DeployLive_requires_an_endpoint_or_a_live_bound_session()
        {
            using var engine = await PassingModelAsync();   // file-created ⇒ not live-bound
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                engine.DeployLiveAsync(null, null, "serviceprincipal", null, null, commit: false));
            Assert.Contains("open_live", ex.Message);       // the message names the recovery
        }

        [Fact]
        public async Task DeployLive_explicit_endpoint_requires_a_database()
        {
            using var engine = await PassingModelAsync();
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                engine.DeployLiveAsync("powerbi://api.powerbi.com/v1.0/myorg/ws", null, "serviceprincipal", null, null, commit: false));
            Assert.Contains("database", ex.Message);
        }

        [Fact]
        public async Task DeployLive_commit_is_blocked_by_a_red_gate_before_connecting()
        {
            using var engine = await BlockedModelAsync();
            // commit=true runs the deploy gate BEFORE any auth/network — a RED gate throws with the blockers + recovery.
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                engine.DeployLiveAsync("powerbi://api.powerbi.com/v1.0/myorg/ws", "Sales", "serviceprincipal", null, null, commit: true));
            Assert.Contains("blocked by the deploy gate", ex.Message);
            Assert.Contains("overrideReason", ex.Message);
        }

        // ============================================================================================
        // 5) cicd_publish — agent commit refused with teaching text; the dry-run part enumeration (pure).
        // ============================================================================================
        [Fact]
        public async Task CicdPublish_agent_commit_is_refused_with_teaching_text()
        {
            using var engine = new LocalEngine(new SessionManager(), new Fake(true));
            var r = await engine.CicdPublishAsync("w1", "m1", commit: true, authMode: "azcli", tenantId: null, origin: "agent");
            Assert.False(r.Committed);
            Assert.Contains("agent cannot publish", r.Error);
            Assert.Contains("human must confirm", r.Error);
        }

        [Fact]
        public async Task CicdPublish_dry_run_needs_an_open_model_on_disk()
        {
            using var engine = new LocalEngine(new SessionManager(), new Fake(true));
            var r = await engine.CicdPublishAsync("w1", "m1", commit: false, authMode: "azcli", tenantId: null, origin: "human");
            Assert.False(r.Committed);
            Assert.Contains("Open a model first", r.Error);   // no session ⇒ the recovery is named
        }

        [Fact]
        public void CicdPublish_part_enumeration_skips_non_definition_files()
        {
            // Craft a minimal Fabric .SemanticModel folder and prove EnumeratePartPaths keeps definition parts and
            // drops the editor-local (.pbi), sidecar (.semanticus), envelope (.platform) and stale model.bim files.
            var root = Path.Combine(Path.GetTempPath(), "sem-cicd-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(Path.Combine(root, "definition", "tables"));
            Directory.CreateDirectory(Path.Combine(root, ".pbi"));
            Directory.CreateDirectory(Path.Combine(root, ".semanticus"));
            try
            {
                File.WriteAllText(Path.Combine(root, "definition.pbism"), "{}");
                File.WriteAllText(Path.Combine(root, "definition", "model.tmdl"), "model Model");
                File.WriteAllText(Path.Combine(root, "definition", "tables", "Sales.tmdl"), "table Sales");
                File.WriteAllText(Path.Combine(root, "model.bim"), "{}");            // stale TMSL beside a TMDL definition ⇒ dropped
                File.WriteAllText(Path.Combine(root, ".platform"), "{}");            // envelope metadata ⇒ dropped
                File.WriteAllText(Path.Combine(root, ".pbi", "localSettings.json"), "{}");   // editor cache ⇒ dropped
                File.WriteAllText(Path.Combine(root, ".semanticus", "layout.json"), "{}");   // diagram sidecar ⇒ dropped

                var parts = LocalEngine.EnumeratePartPaths(root);
                Assert.Contains("definition.pbism", parts);
                Assert.Contains("definition/model.tmdl", parts);
                Assert.Contains("definition/tables/Sales.tmdl", parts);
                Assert.DoesNotContain(parts, p => p == "model.bim" || p == ".platform" || p.Contains(".pbi/") || p.Contains(".semanticus/"));
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }

        // ============================================================================================
        // 6) cicd_generate — pure file authoring; write=false returns content only, invalid env refused.
        // ============================================================================================
        [Fact]
        public async Task CicdGenerate_github_returns_the_scaffold_contents_without_writing()
        {
            using var engine = new LocalEngine(new SessionManager(), new Fake(true));
            var sc = await engine.CicdGenerateAsync("github", "ws-guid", "PROD", write: false);
            Assert.Null(sc.Error);
            Assert.False(sc.Written);
            Assert.Empty(sc.WrittenPaths);
            Assert.Contains(sc.Files, f => f.Path == "parameter.yml" && !string.IsNullOrWhiteSpace(f.Content));
            Assert.Contains(sc.Files, f => f.Path == ".github/workflows/fabric-cicd.yml");
            Assert.Contains(sc.Files, f => f.Path == ".deploy/deploy.py");
        }

        [Fact]
        public async Task CicdGenerate_ado_target_emits_an_azure_pipeline()
        {
            using var engine = new LocalEngine(new SessionManager(), new Fake(true));
            var sc = await engine.CicdGenerateAsync("ado", null, "TEST", write: false);
            Assert.Null(sc.Error);
            Assert.Contains(sc.Files, f => f.Path == "azure-pipelines.yml");
        }

        [Fact]
        public async Task CicdGenerate_rejects_an_unsafe_environment_name()
        {
            using var engine = new LocalEngine(new SessionManager(), new Fake(true));
            var sc = await engine.CicdGenerateAsync("github", null, "bad env!", write: false);
            Assert.NotNull(sc.Error);
            Assert.Contains("environment name", sc.Error);   // names what to fix
        }

        // ============================================================================================
        // 7) deployment_history / list_deployment_pipelines / get_pipeline_stages / get_stage_items (+pagination).
        // ============================================================================================
        [Fact]
        public async Task DeploymentHistory_parses_status_diff_counts_note_and_principal()
        {
            InstallFabric(StdRoute);
            using var engine = new LocalEngine(new SessionManager(), new Fake(true));
            var hist = await engine.DeploymentHistoryAsync("p1", "azcli", null);
            var e = Assert.Single(hist);
            Assert.Equal("Succeeded", e.Status);
            Assert.Equal(1, e.PreDeploymentDiffInformation.NewItemsCount);
            Assert.Equal(2, e.PreDeploymentDiffInformation.DifferentItemsCount);
            Assert.Equal("June release", e.Note.Content);
            Assert.Equal("kane@contoso.com", e.PerformedBy.DisplayName);
        }

        [Fact]
        public async Task ListPipelines_and_stages_and_items_parse_and_order()
        {
            InstallFabric(StdRoute);
            using var engine = new LocalEngine(new SessionManager(), new Fake(true));

            var pipes = await engine.ListDeploymentPipelinesAsync("azcli", null);
            Assert.Equal("Contoso Sales", Assert.Single(pipes).DisplayName);

            var stages = await engine.GetPipelineStagesAsync("p1", "azcli", null);
            Assert.Equal(new[] { 0, 1, 2 }, stages.Select(s => s.Order).ToArray());   // ordered by Order
            Assert.True(stages[2].IsPublic);
            Assert.Equal("Production", stages[2].DisplayName);

            var items = await engine.GetStageItemsAsync("p1", "s1", "azcli", null);
            Assert.Equal(2, items.Length);
            Assert.Equal("t1", items.Single(i => i.ItemId == "i1").TargetItemId);
        }

        [Fact]
        public async Task ListPipelines_follows_the_continuation_token_across_pages()
        {
            (HttpStatusCode, string, (string, string)[]?) Paged(string method, string path, string query)
            {
                if (method == "GET" && path.EndsWith("/deploymentPipelines"))
                    return query.Contains("continuationToken")
                        ? Ok("{\"value\":[{\"id\":\"p2\",\"displayName\":\"Finance\"}]}")                       // page 2 (token consumed ⇒ end)
                        : Ok("{\"value\":[{\"id\":\"p1\",\"displayName\":\"Contoso Sales\"}],\"continuationToken\":\"tok\"}");   // page 1
                return Err(404, "{}");
            }
            InstallFabric(Paged);
            using var engine = new LocalEngine(new SessionManager(), new Fake(true));
            var pipes = await engine.ListDeploymentPipelinesAsync("azcli", null);
            Assert.Equal(new[] { "Contoso Sales", "Finance" }, pipes.Select(p => p.DisplayName).ToArray());
        }

        [Fact]
        public async Task PipelineReads_surface_a_permission_error_scrubbed_on_the_dto()
        {
            // A 403 (no pipeline role) — preview_deploy must report it on .Error, never throw across the door.
            InstallFabric((m, p, q) => Err(403, "{\"errorCode\":\"InsufficientPrivileges\",\"message\":\"need Admin\"}"));
            using var engine = new LocalEngine(new SessionManager(), new Fake(true));
            var prev = await engine.PreviewDeployAsync("p1", "s1", "s2", "azcli", null);
            Assert.NotNull(prev.Error);
            Assert.Contains("403", prev.Error);
        }

        // ============================================================================================
        // 8) fabric_git_status / connection round-trip + the commit dry-run + the agent-disconnect refusal.
        // ============================================================================================
        [Fact]
        public async Task FabricGitStatus_and_connection_round_trip_canned_payloads()
        {
            InstallFabric(StdRoute);
            using var engine = new LocalEngine(new SessionManager(), new Fake(true));

            var conn = await engine.FabricGitConnectionAsync("w1", "azcli", null);
            Assert.Null(conn.Error);
            Assert.Equal("ConnectedAndInitialized", conn.State);
            Assert.Equal("AzureDevOps", conn.ProviderType);
            Assert.Equal("models", conn.Repository);

            var st = await engine.FabricGitStatusAsync("w1", "azcli", null);
            Assert.Null(st.Error);
            var ch = Assert.Single(st.Changes);
            Assert.Equal("SemanticModel", ch.ItemType);
            Assert.Equal("Modified", ch.WorkspaceChange);
            Assert.False(st.Conflicts);
        }

        [Fact]
        public async Task FabricGitCommit_dry_run_previews_the_change_count_and_posts_nothing()
        {
            var h = InstallFabric(StdRoute);
            using var engine = new LocalEngine(new SessionManager(), new Fake(true));
            var r = await engine.FabricGitCommitAsync("w1", "nightly", null, commit: false, authMode: "azcli", tenantId: null, origin: "human");
            Assert.False(r.Committed);
            Assert.Equal(1, r.ChangeCount);
            Assert.Contains("DRY RUN", r.Plan);
            Assert.DoesNotContain(h.Calls, c => c.method == "POST");   // dry-run only READ the status
        }

        [Fact]
        public async Task FabricGitDisconnect_agent_commit_is_refused()
        {
            using var engine = new LocalEngine(new SessionManager(), new Fake(true));
            var r = await engine.FabricGitDisconnectAsync("w1", commit: true, authMode: "azcli", tenantId: null, origin: "agent");
            Assert.False(r.Committed);
            Assert.Contains("agent cannot disconnect", r.Error);
            Assert.Contains("human must confirm", r.Error);
        }
    }
}
