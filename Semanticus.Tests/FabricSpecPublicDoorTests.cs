using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.Entitlement;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>T111 anchors the conditional Fabric spec generator at both public doors without requiring a live
    /// tenant. Invalid input must fail before authentication and preserve the last good session-held spec.</summary>
    public sealed class FabricSpecPublicDoorTests
    {
        private sealed class Free : IEntitlement
        {
            public bool IsPro => false;
            public EntitlementInfo Info => new EntitlementInfo { Tier = "free" };
        }

        [Fact]
        public async Task Autogenerate_spec_from_fabric_public_doors_fail_before_auth_and_preserve_the_spec()
        {
            using var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, new Free());
            var rpc = new EngineRpcTarget(engine);
            var baseline = await engine.SetSpecAsync(
                "{\"name\":\"Last good\",\"storageMode\":\"import\",\"tables\":[]}", "human");
            var specChanges = 0;
            var modelChanges = 0;
            var activities = new List<ActivityEvent>();
            sessions.Bus.SpecChanged += _ => specChanges++;
            sessions.Bus.Changed += _ => modelChanges++;
            sessions.Bus.Activity += activities.Add;

            var missingEndpoint = await Assert.ThrowsAsync<ArgumentException>(() =>
                McpTools.AutogenerateSpecFromFabric(engine, " ", "Warehouse", authMode: "token"));
            Assert.Contains("Fabric SQL endpoint", missingEndpoint.Message, StringComparison.OrdinalIgnoreCase);

            var missingDatabase = await Assert.ThrowsAsync<ArgumentException>(() =>
                rpc.autogenerateSpecFromFabric("workspace.datawarehouse.fabric.microsoft.com", " ", "token"));
            Assert.Contains("warehouse or lakehouse database", missingDatabase.Message, StringComparison.OrdinalIgnoreCase);

            var invalidStorage = await Assert.ThrowsAsync<ArgumentException>(() =>
                McpTools.AutogenerateSpecFromFabric(
                    engine, "workspace.datawarehouse.fabric.microsoft.com", "Warehouse", "hybrid", "token"));
            Assert.Contains("Import or Direct Lake", invalidStorage.Message, StringComparison.OrdinalIgnoreCase);

            var authFailure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                McpTools.AutogenerateSpecFromFabric(
                    engine, "workspace.datawarehouse.fabric.microsoft.com", "Warehouse", "import", "token"));
            Assert.Contains("could not get a sign-in token", authFailure.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("SQL/database resource", authFailure.Message, StringComparison.OrdinalIgnoreCase);

            var after = await engine.GetSpecAsync();
            Assert.Equal(baseline.Version, after.Version);
            Assert.Equal("Last good", after.Spec.Name);
            Assert.Equal(0, specChanges);
            Assert.Equal(0, modelChanges);
            Assert.Empty(activities);
        }
    }
}
