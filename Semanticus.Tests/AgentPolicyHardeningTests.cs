using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.Entitlement;
using Xunit;
using TOM = Microsoft.AnalysisServices.Tabular;

namespace Semanticus.Tests
{
    /// <summary>
    /// Pins from the SECOND adversarial review (gpt-5.6-sol) of the policy engine. Each test names the bypass it
    /// kills: grant-timestamp forgery, grant-consumption on read paths, the literal-null policy downgrade, the
    /// "localhost"-substring prod bypass, cross-intent approval substitution, the fail-open legacy IsAgent, the
    /// registry's proceed-unlocked fallback, and the new call-site gates (refresh / data-agent / git-connect).
    /// </summary>
    [Collection("restore-root")]
    public sealed class AgentPolicyHardeningTests : IDisposable
    {
        private const string CloudEp = "powerbi://api.powerbi.com/v1.0/myorg/WS";
        private readonly string _root;
        private readonly string _safeRoot;

        public AgentPolicyHardeningTests(RestoreRootFixture fixture)
        {
            _safeRoot = fixture.Root;
            _root = Path.Combine(Path.GetTempPath(), "sem-hard-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            AgentPolicyStore.RootOverride = _root;
            ApprovalLedger.RootOverride = _root;
            ConnectionRegistry.RootOverride = _root;
        }
        public void Dispose()
        {
            AgentPolicyStore.RootOverride = _safeRoot; ApprovalLedger.RootOverride = _safeRoot; ConnectionRegistry.RootOverride = _safeRoot;
            try { Directory.Delete(_root, true); } catch { }
        }

        private sealed class Fake : IEntitlement
        {
            public bool IsPro { get; }
            public EntitlementInfo Info { get; }
            public Fake(bool pro) { IsPro = pro; Info = new EntitlementInfo { Tier = pro ? "pro" : "free" }; }
        }

        private string LedgerPath => Path.Combine(_root, "agent-approvals.json");
        private string PolicyPath => Path.Combine(_root, "agent-policy.json");

        // Register + approve one intent; returns the intent hash used.
        private static string GrantOne(AgentCapability cap, string label, string basis)
        {
            var intent = ApprovalLedger.IntentHash(cap.ToString(), CloudEp, "DS", basis);
            var rec = ApprovalLedger.Request(cap, label, intent, "s", "t");
            ApprovalLedger.Approve(rec.Id, "human");
            return intent;
        }

        private void MutateGrantJson(Action<JsonObject> mutate)
        {
            var root = JsonNode.Parse(File.ReadAllText(LedgerPath)).AsObject();
            var item = root["items"].AsArray()[0].AsObject();
            mutate(item);
            File.WriteAllText(LedgerPath, root.ToJsonString());
        }

        // ---- grant timestamp bounds: a forged/corrupt record must be dead, not immortal --------------------------

        [Fact]
        public void Garbage_GrantedUtc_with_a_plausible_expiry_is_not_consumable()
        {
            var intent = GrantOne(AgentCapability.DeployLive, "prod", "b1");
            MutateGrantJson(o => o["grantedUtc"] = "not-a-timestamp");
            Assert.False(ApprovalLedger.TryConsume(AgentCapability.DeployLive, "prod", intent));
        }

        [Fact]
        public void An_expiry_stretched_beyond_the_TTL_window_is_not_consumable()
        {
            var intent = GrantOne(AgentCapability.DeployLive, "prod", "b2");
            // Hand-edit the file to a 10-day expiry: granted+10d > granted+Ttl(+skew) ⇒ dead even though it parses.
            MutateGrantJson(o => o["expiresUtc"] = DateTimeOffset.UtcNow.AddDays(10).ToString("O"));
            Assert.False(ApprovalLedger.TryConsume(AgentCapability.DeployLive, "prod", intent));
        }

        [Fact]
        public void A_normally_granted_approval_is_consumable_exactly_once()
        {
            var intent = GrantOne(AgentCapability.DeployLive, "prod", "b3");
            Assert.True(ApprovalLedger.TryConsume(AgentCapability.DeployLive, "prod", intent));
            Assert.False(ApprovalLedger.TryConsume(AgentCapability.DeployLive, "prod", intent));   // one-shot
        }

        // ---- HasLiveGrant: the QueryData session-grant read must never spend the grant ---------------------------

        [Fact]
        public void HasLiveGrant_reads_without_consuming()
        {
            var intent = GrantOne(AgentCapability.QueryData, "prod", "querydata");
            Assert.True(ApprovalLedger.HasLiveGrant(AgentCapability.QueryData, "prod", intent));
            Assert.True(ApprovalLedger.HasLiveGrant(AgentCapability.QueryData, "prod", intent));   // still there
            Assert.True(ApprovalLedger.TryConsume(AgentCapability.QueryData, "prod", intent));     // a consumer CAN spend it
            Assert.False(ApprovalLedger.HasLiveGrant(AgentCapability.QueryData, "prod", intent));  // and then it is gone
        }

        // ---- intent binding: approving one plan never authorises a sibling plan on the same target ---------------

        [Fact]
        public void A_grant_for_one_intent_basis_cannot_be_spent_on_another()
        {
            // The sol scenario: human approves a selective push; the agent tries to spend it on a full deploy_live.
            var pushIntent = GrantOne(AgentCapability.DeployLive, "prod", "apply:measure:Sales/Total\n#del:");
            var deployIntent = ApprovalLedger.IntentHash(nameof(AgentCapability.DeployLive), CloudEp, "DS", "deploy_live");
            Assert.False(ApprovalLedger.TryConsume(AgentCapability.DeployLive, "prod", deployIntent));  // substitution refused
            Assert.True(ApprovalLedger.TryConsume(AgentCapability.DeployLive, "prod", pushIntent));     // the approved plan works
        }

        // ---- literal-null policy: saved-but-null is an ERROR state, never the weaker default ---------------------

        [Fact]
        public void A_policy_file_containing_literal_null_fails_closed()
        {
            Directory.CreateDirectory(_root);
            File.WriteAllText(PolicyPath, "null");   // valid JSON, deserializes to null without throwing
            var p = AgentPolicyStore.Get();
            Assert.Equal("unreadable", p.Preset);
            Assert.Equal(PolicyAction.Deny, p.Resolve(AgentCapability.DeployLive, "dev"));   // all live denied
            Assert.Equal(PolicyAction.Allow, p.Resolve(AgentCapability.EditLocal, "dev"));   // local work still permitted
        }

        [Fact]
        public void A_missing_policy_file_is_the_normal_default()
        {
            Assert.Equal("standard", AgentPolicyStore.Get().Preset);
        }

        // ---- loopback is HOST-exact: the "localhost"-substring prod bypass is dead -------------------------------

        [Theory]
        [InlineData("powerbi://api.powerbi.com/v1.0/myorg/prod-localhost-mirror", "prod")]   // the sol bypass: scheme ⇒ remote
        [InlineData("localhost:52640", "local")]
        [InlineData("Data Source=localhost:52640", "local")]
        [InlineData("127.0.0.1:2383", "local")]
        [InlineData("[::1]:2383", "local")]
        [InlineData("myserver.corp:1234", "prod")]                                            // unlabelled ⇒ prod
        public void Target_labels_resolve_by_host_not_substring(string endpoint, string expected)
        {
            Assert.Equal(expected, LocalEngine.ResolveTargetLabel(endpoint, "DS"));
        }

        [Fact]
        public void An_explicit_label_beats_the_loopback_inference()
        {
            // The label is a human declaration — if the user says their localhost tunnel is prod, it is prod.
            var rec = ConnectionRegistry.Remember("xmla", "localhost:52640", "DS", "M", null, "interactive");
            ConnectionRegistry.SetLabel(rec.Id, "prod", "human");
            Assert.Equal("prod", LocalEngine.ResolveTargetLabel("localhost:52640", "DS"));
        }

        // ---- the legacy DeployGuard.IsAgent is fail-closed: only an exact human is human --------------------------

        [Theory]
        [InlineData(null, true)]
        [InlineData("", true)]
        [InlineData("system", true)]
        [InlineData("robot", true)]
        [InlineData("human ", true)]    // trailing space is NOT an exact declaration
        [InlineData("human", false)]
        [InlineData("HUMAN", false)]    // case-insensitive, same as AgentPolicyGuard.IsHuman
        public void Legacy_IsAgent_treats_everything_but_exact_human_as_an_agent(string origin, bool isAgent)
        {
            Assert.Equal(isAgent, DeployGuard.IsAgent(origin));
        }

        // ---- registry lock: timeout THROWS; a corrupt file is preserved aside, never silently rewritten -----------

        [Fact]
        public void Registry_mutation_throws_when_the_lock_cannot_be_taken()
        {
            Directory.CreateDirectory(_root);
            var lockPath = Path.Combine(_root, "connections.json") + ".lock";
            // Hold the lock the way another engine process would (FileShare.None on the same path)...
            using var held = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1);
            // ...and the mutation must THROW after its 3s budget, never proceed unlocked (the old fallback let a
            // stale write resurrect a weaker label than the one the user just declared).
            Assert.Throws<IOException>(() => ConnectionRegistry.Remember("xmla", CloudEp, "DS", "M", null, "interactive"));
        }

        [Fact]
        public void A_corrupt_registry_is_moved_aside_before_the_rewrite()
        {
            Directory.CreateDirectory(_root);
            var file = Path.Combine(_root, "connections.json");
            File.WriteAllText(file, "{ this is not json");
            ConnectionRegistry.Remember("xmla", CloudEp, "DS", "M", null, "interactive");
            var aside = Directory.GetFiles(_root, "connections.json.corrupt-*");
            Assert.Single(aside);
            Assert.Equal("{ this is not json", File.ReadAllText(aside[0]));   // the bytes survived for recovery
        }

        // ---- the NEW call-site gates fire before any effect (offline: a refusal proves gate-before-network) -------

        private static string Bim()
        {
            var db = new TOM.Database("t") { CompatibilityLevel = 1600, Model = new TOM.Model() };
            var t = new TOM.Table { Name = "Sales", LineageTag = "tag-sales" };
            t.Partitions.Add(new TOM.Partition { Name = "Sales", Source = new TOM.MPartitionSource { Expression = "let x=1 in x" } });
            db.Model.Tables.Add(t);
            var p = Path.Combine(Path.GetTempPath(), $"sem-hard-{Guid.NewGuid():N}.bim");
            File.WriteAllText(p, TOM.JsonSerializer.SerializeDatabase(db));
            return p;
        }

        [Fact]
        public async Task Agent_refresh_of_an_unlabelled_target_is_refused_before_any_connection()
        {
            using var engine = new LocalEngine(new SessionManager(), new Fake(pro: true));
            var bim = Bim();
            try
            {
                await engine.OpenAsync(bim);
                // Explicit CLOUD endpoint (never in the registry ⇒ prod) + commit as the agent. The default policy
                // asks on prod; with no grant the refusal must come from the POLICY (proving the gate runs before
                // token acquisition — a token attempt against this fake endpoint would fail with an auth error).
                var rep = await engine.RefreshPartitionAsync("partition:Sales/Sales", "Full", CloudEp, "DS",
                    "serviceprincipal", null, null, commit: true, origin: "agent");
                Assert.False(rep.Committed);
                Assert.Contains("approval", rep.Error, StringComparison.OrdinalIgnoreCase);
                Assert.Single(ApprovalLedger.List());   // the ask was queued for the human
            }
            finally { File.Delete(bim); }
        }

        [Fact]
        public async Task Agent_refresh_dry_run_is_never_gated()
        {
            using var engine = new LocalEngine(new SessionManager(), new Fake(pro: true));
            var bim = Bim();
            try
            {
                await engine.OpenAsync(bim);
                var rep = await engine.RefreshPartitionAsync("partition:Sales/Sales", "ClearValues", CloudEp, "DS",
                    "serviceprincipal", null, null, commit: false, origin: "agent");
                Assert.Null(rep.Error);                    // previewing is what makes the policy livable
                Assert.Empty(ApprovalLedger.List());       // and it queues nothing
            }
            finally { File.Delete(bim); }
        }

        [Fact]
        public async Task Approving_a_Full_refresh_does_not_authorise_a_ClearValues()
        {
            using var engine = new LocalEngine(new SessionManager(), new Fake(pro: true));
            var bim = Bim();
            try
            {
                await engine.OpenAsync(bim);
                // Ask as the agent with Full → approve THAT request as the human.
                var first = await engine.RefreshPartitionAsync("partition:Sales/Sales", "Full", CloudEp, "DS",
                    "serviceprincipal", null, null, commit: true, origin: "agent");
                Assert.False(first.Committed);
                var pending = ApprovalLedger.List().Single();
                ApprovalLedger.Approve(pending.Id, "human");

                // The sol scenario: spend the Full grant on the DESTRUCTIVE type. Must be refused (fresh ask queued).
                var clear = await engine.RefreshPartitionAsync("partition:Sales/Sales", "ClearValues", CloudEp, "DS",
                    "serviceprincipal", null, null, commit: true, origin: "agent");
                Assert.False(clear.Committed);
                Assert.Contains("approval", clear.Error, StringComparison.OrdinalIgnoreCase);
            }
            finally { File.Delete(bim); }
        }

        [Fact]
        public async Task Agent_data_agent_writes_are_gated_and_delete_escalates()
        {
            using var engine = new LocalEngine(new SessionManager(), new Fake(pro: true));
            // Deny the delete capability on prod outright; leave DeployLive on the default ask.
            AgentPolicyStore.SetCell("DeployDelete", "prod", "deny", "human", isPro: true);

            var create = await engine.CreateDataAgentAsync("ws-guid", "Agent1", "", commit: true, "serviceprincipal", null, "agent");
            Assert.Equal("error", create.Status);
            Assert.Contains("approval", create.Message, StringComparison.OrdinalIgnoreCase);   // ask (queued), not an auth error

            var del = await engine.DeleteDataAgentAsync("ws-guid", "agent-id", commit: true, "serviceprincipal", null, "agent");
            Assert.Equal("error", del.Status);
            Assert.Contains("does not permit", del.Message);                                    // the DENY row, i.e. DeployDelete
        }

        [Fact]
        public async Task Agent_cannot_connect_a_workspace_to_git()
        {
            using var engine = new LocalEngine(new SessionManager(), new Fake(pro: true));
            var r = await engine.FabricGitConnectAsync("ws-guid", "AzureDevOps", "org", "proj", "repo", "main", "/", null,
                commit: true, "serviceprincipal", null, "agent");
            Assert.Contains("cannot connect", r.Error);
            // Symmetry with disconnect: the human path is exercised in the existing Fabric tests; the agent path must
            // refuse BEFORE any token/network work (this test runs fully offline).
        }

        // ---- GuardAgent's QueryData path: session grant, checked without being spent ------------------------------

        [Fact]
        public void QueryData_grant_is_a_session_not_a_single_shot()
        {
            using var engine = new LocalEngine(new SessionManager(), new Fake(pro: true));
            // client preset: QueryData@prod = deny — refusal names the policy, no ask is queued.
            AgentPolicyStore.SetPreset("client", "human", isPro: true);
            var denied = engine.GuardAgent(AgentCapability.QueryData, CloudEp, "DS", "agent", isCommit: true,
                summary: "s", intentBasis: "querydata", consumeGrant: false);
            Assert.NotNull(denied);
            Assert.Empty(ApprovalLedger.List());

            // standard preset: QueryData@prod = ask — grant once, then MANY reads ride the same grant untouched.
            AgentPolicyStore.SetPreset("standard", "human", isPro: true);
            var ask = engine.GuardAgent(AgentCapability.QueryData, CloudEp, "DS", "agent", true, "s", "querydata", consumeGrant: false);
            Assert.NotNull(ask);
            ApprovalLedger.Approve(ApprovalLedger.List().Single().Id, "human");
            for (var i = 0; i < 3; i++)
                Assert.Null(engine.GuardAgent(AgentCapability.QueryData, CloudEp, "DS", "agent", true, "s", "querydata", consumeGrant: false));
        }
    }
}
