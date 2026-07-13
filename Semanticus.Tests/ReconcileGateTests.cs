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
    /// The QueryData permission gate is the ENTIRE security story for reconcile_measure now (the SQL guard + bespoke
    /// approval were removed by Kane's ruling). These tests are the coverage that deletion lost: they pin that an
    /// AGENT-origin reconcile is gated on the SQL target BEFORE any connection — Deny/Ask refuse, Allow proceeds,
    /// human is never gated — driven OFFLINE via the same policy-root seam AgentPolicyHardeningTests uses. They MUST
    /// FAIL if the sqlGate block in ReconcileMeasureAsync is removed (an ungated agent would fall through to
    /// "not connected" instead of being refused).
    /// </summary>
    [Collection("restore-root")]
    public sealed class ReconcileGateTests : IDisposable
    {
        private const string CloudEp = "powerbi://api.powerbi.com/v1.0/myorg/WS";   // never in the registry ⇒ prod
        private readonly string _root;
        private readonly string _safeRoot;

        public ReconcileGateTests(RestoreRootFixture fixture)
        {
            _safeRoot = fixture.Root;
            _root = Path.Combine(Path.GetTempPath(), "sem-recgate-" + Guid.NewGuid().ToString("N").Substring(0, 8));
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

        // A model with a measure to resolve (grand-total-only mode needs no grouping column).
        private static string Bim()
        {
            var db = new TOM.Database("t") { CompatibilityLevel = 1600, Model = new TOM.Model() };
            var t = new TOM.Table { Name = "Sales", LineageTag = "tag-sales" };
            t.Partitions.Add(new TOM.Partition { Name = "Sales", Source = new TOM.MPartitionSource { Expression = "let x=1 in x" } });
            t.Measures.Add(new TOM.Measure { Name = "Total", Expression = "1" });
            db.Model.Tables.Add(t);
            var p = Path.Combine(Path.GetTempPath(), $"sem-recgate-{Guid.NewGuid():N}.bim");
            File.WriteAllText(p, TOM.JsonSerializer.SerializeDatabase(db));
            return p;
        }

        private static ReconcileRequest Req() => new ReconcileRequest
        {
            MeasureRef = "measure:Sales/Total",
            GroupBy = Array.Empty<string>(),        // grand-total-only: resolution needs only the measure
            Sql = "SELECT 1",
            BlankPolicy = "zero",
            ToleranceAbsolute = 1e-9,
            ToleranceRelative = 1e-7,
            Server = CloudEp,
            Database = "DS",
        };

        private async Task<ReconcileRunResult> Run(string preset, string origin)
        {
            AgentPolicyStore.SetPreset(preset, "human", isPro: true);
            using var engine = new LocalEngine(new SessionManager(), new Fake(pro: true));
            var bim = Bim();
            try
            {
                await engine.OpenAsync(bim);      // NOT connected live — _live stays null
                return await engine.ReconcileMeasureAsync(Req(), origin);
            }
            finally { File.Delete(bim); }
        }

        [Fact]
        public async Task Agent_reconcile_is_refused_by_the_SQL_target_gate_before_any_connection_when_denied()
        {
            // client preset: QueryData@prod = deny. The refusal must come from the GATE (PolicyRefused), offline,
            // NOT from "not connected" — proving the SQL gate runs before the live check. (Deleting the sqlGate block
            // makes this fall through to InsufficientCoverage/"not connected" and FAIL.)
            var r = await Run("client", "agent");
            Assert.Equal(ReconcileStatus.InputError, r.Status);
            Assert.True(r.PolicyRefused);
            Assert.NotNull(r.Error);
            Assert.DoesNotContain("connect", r.Error, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task Agent_reconcile_denied_target_names_the_policy_and_queues_no_ask()
        {
            var r = await Run("client", "agent");
            Assert.True(r.PolicyRefused);
            Assert.Contains("does not permit", r.Error, StringComparison.OrdinalIgnoreCase);   // the Deny row, not an ask
            Assert.Empty(ApprovalLedger.List());
        }

        [Fact]
        public async Task Agent_reconcile_on_an_ask_target_is_refused_and_queues_a_human_approval()
        {
            // standard preset: QueryData@prod = ask. Refused (offline) with the ask queued for the human.
            var r = await Run("standard", "agent");
            Assert.Equal(ReconcileStatus.InputError, r.Status);
            Assert.True(r.PolicyRefused);
            Assert.Contains("approval", r.Error, StringComparison.OrdinalIgnoreCase);
            var pending = Assert.Single(ApprovalLedger.List());
            Assert.Equal(pending.Id, r.ApprovalId);   // activity can deep-link without matching text/targets
        }

        [Fact]
        public async Task Agent_reconcile_on_an_allowed_target_passes_the_gate_then_degrades_offline()
        {
            // open preset: QueryData allowed on every label. The gate does NOT refuse; the run proceeds to the live
            // check and honestly reports "not connected" — proving Allow is not the blocker.
            var r = await Run("open", "agent");
            Assert.False(r.PolicyRefused);
            Assert.Equal(ReconcileStatus.InsufficientCoverage, r.Status);
            Assert.Contains("connect", r.Error, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task Human_reconcile_is_never_gated_and_reaches_the_connection_check()
        {
            // Even with QueryData@prod = deny (client preset), a HUMAN origin is ungated and reaches the connection.
            var r = await Run("client", "human");
            Assert.False(r.PolicyRefused);
            Assert.Contains("connect", r.Error, StringComparison.OrdinalIgnoreCase);
        }
    }
}
