using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.Entitlement;
using Xunit;
using TOM = Microsoft.AnalysisServices.Tabular;

namespace Semanticus.Tests
{
    /// <summary>
    /// The end-to-end proof that the deploy_live governance hole is CLOSED: an agent push to a live target is gated by
    /// the policy, resolving the target's label from the connection registry, and an 'ask' is only satisfiable by a
    /// human-granted approval the agent cannot mint. Driven through apply_model_diff → workspace (the seamed push path),
    /// which shares the exact chokepoint deploy_live uses.
    /// </summary>
    [Collection("restore-root")]   // committed pushes write a restore point — keep it off the real home
    public sealed class AgentPolicyGateTests : IDisposable
    {
        private const string Endpoint = "powerbi://api.powerbi.com/v1.0/myorg/WS";
        private readonly string _root;
        private readonly string _safeRoot;

        public AgentPolicyGateTests(RestoreRootFixture fixture)
        {
            _safeRoot = fixture.Root;
            _root = Path.Combine(Path.GetTempPath(), "sem-gate-" + Guid.NewGuid().ToString("N").Substring(0, 8));
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

        private static TOM.Database Db(string totalExpr)
        {
            var db = new TOM.Database("t") { CompatibilityLevel = 1600, Model = new TOM.Model() };
            var t = new TOM.Table { Name = "Sales", LineageTag = "tag-sales" };
            t.Partitions.Add(new TOM.Partition { Name = "Sales", Source = new TOM.MPartitionSource { Expression = "let x=1 in x" } });
            t.Measures.Add(new TOM.Measure { Name = "Total", Expression = totalExpr, LineageTag = "m-total" });
            db.Model.Tables.Add(t);
            return db;
        }
        private static string Bim(TOM.Database db)
        {
            var p = Path.Combine(Path.GetTempPath(), $"sem-gate-{Guid.NewGuid():N}.bim");
            File.WriteAllText(p, TOM.JsonSerializer.SerializeDatabase(db));
            return p;
        }
        private static ModelRef Ws() => new() { Kind = "workspace", Endpoint = Endpoint, Database = "DS" };
        private static DeployReport Ok() => new() { Committed = true, TotalChanges = 1, SyncedRefs = new[] { "measure:Sales/Total" } };

        // A one-object push (source Total=2 vs live Total=1) as the given origin; returns (result, wasPushed).
        private async Task<(ApplyDiffResult r, bool pushed)> Push(LocalEngine engine, string origin)
        {
            var src = Bim(Db("2"));
            var pushed = false;
            engine.WorkspaceSnapshotHook = () => Task.FromResult(Db("1"));
            engine.WorkspacePushHook = (_, __) => { pushed = true; return Ok(); };
            try { return (await engine.ApplyDiffAsync(new ModelRef { Kind = "file", Path = src }, Ws(), null, commit: true, origin), pushed); }
            finally { File.Delete(src); }
        }

        // ---- an UNLABELLED target is prod; the default policy asks; an agent is refused and NOTHING is pushed ----
        [Fact]
        public async Task Agent_push_to_an_unlabelled_target_is_refused_and_pushes_nothing()
        {
            using var engine = new LocalEngine(new SessionManager(), new Fake(pro: true));
            var (r, pushed) = await Push(engine, "agent");

            Assert.False(r.Applied);
            Assert.False(pushed);                                  // the endpoint was never touched
            Assert.Contains("approval", r.Error, StringComparison.OrdinalIgnoreCase);
            Assert.Single(ApprovalLedger.List());                  // ...and it queued a request for the human
        }

        // ---- the SAME push by a human proceeds: the matrix restrains the agent, not the authority who set it ----
        [Fact]
        public async Task Human_push_to_the_same_target_proceeds()
        {
            using var engine = new LocalEngine(new SessionManager(), new Fake(pro: true));
            var (r, pushed) = await Push(engine, "human");
            Assert.True(r.Applied);
            Assert.True(pushed);
        }

        // ---- the whole ask→approve→retry loop: after a human approves, the agent's retry pushes ONCE ----
        [Fact]
        public async Task After_a_human_approves_the_agents_retry_pushes()
        {
            using var engine = new LocalEngine(new SessionManager(), new Fake(pro: true));

            var (r1, pushed1) = await Push(engine, "agent");
            Assert.False(pushed1);
            var pending = ApprovalLedger.List().Single();
            ApprovalLedger.Approve(pending.Id, "human");           // the human approves in the UI

            var (r2, pushed2) = await Push(engine, "agent");        // agent retries the SAME action
            Assert.True(r2.Applied);
            Assert.True(pushed2);

            var (_, pushed3) = await Push(engine, "agent");         // one-shot: a THIRD attempt needs a fresh approval
            Assert.False(pushed3);
        }

        // ---- a dev-labelled target: the agent is allowed with no approval needed ----
        [Fact]
        public async Task Agent_push_to_a_dev_labelled_target_is_allowed()
        {
            var rec = ConnectionRegistry.Remember("xmla", Endpoint, "DS");
            ConnectionRegistry.SetLabel(rec.Id, "dev", "human");

            using var engine = new LocalEngine(new SessionManager(), new Fake(pro: true));
            var (r, pushed) = await Push(engine, "agent");
            Assert.True(r.Applied);
            Assert.True(pushed);
        }

        // ---- under the 'client' preset, prod is a WALL: deny, no approval path, nothing queued that helps ----
        [Fact]
        public async Task Under_the_client_preset_an_agent_push_to_prod_is_denied_outright()
        {
            AgentPolicyStore.SetPreset("client", "human", isPro: true);
            var rec = ConnectionRegistry.Remember("xmla", Endpoint, "DS");
            ConnectionRegistry.SetLabel(rec.Id, "prod", "human");

            using var engine = new LocalEngine(new SessionManager(), new Fake(pro: true));
            var (r, pushed) = await Push(engine, "agent");

            Assert.False(r.Applied);
            Assert.False(pushed);
            Assert.Contains("does not permit", r.Error);
            // even if something tried to approve, no grant can satisfy a Deny — the guard never consulted the ledger.
        }

        // ---- the global kill-switch OFF: the guardrail steps aside honestly, and the agent push proceeds ----
        [Fact]
        public async Task With_governance_switched_off_the_agent_push_proceeds()
        {
            AgentPolicyStore.SetEnabled(false, "human");
            using var engine = new LocalEngine(new SessionManager(), new Fake(pro: true));
            var (r, pushed) = await Push(engine, "agent");
            Assert.True(r.Applied);
            Assert.True(pushed);
        }
    }
}
