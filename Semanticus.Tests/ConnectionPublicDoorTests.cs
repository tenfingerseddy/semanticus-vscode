using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.Entitlement;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>T111 anchors connection-registry mutations through both public doors. These writes affect a
    /// machine-local sidecar, not the model, so they broadcast activity without advancing model/didChange.</summary>
    [Collection("restore-root")]
    public sealed class ConnectionPublicDoorTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "sem-public-connections-" + Guid.NewGuid().ToString("N"));
        private readonly string _safeRoot;

        public ConnectionPublicDoorTests(RestoreRootFixture fixture)
        {
            _safeRoot = fixture.Root;
            ConnectionRegistry.RootOverride = _root;
        }

        public void Dispose()
        {
            ConnectionRegistry.RootOverride = _safeRoot;
            try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { }
        }

        private sealed class Free : IEntitlement
        {
            public bool IsPro => false;
            public EntitlementInfo Info => new EntitlementInfo { Tier = "free" };
        }

        [Fact]
        public async Task Label_public_doors_enforce_the_human_boundary_and_broadcast_exact_outcomes()
        {
            using var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, new Free());
            var rpc = new EngineRpcTarget(engine);
            var connection = ConnectionRegistry.Remember("xmla", "powerbi://example/public-label", "Sales", "Sales model");
            var activities = new List<ActivityEvent>();
            var modelChanges = 0;
            sessions.Bus.Activity += activities.Add;
            sessions.Bus.Changed += _ => modelChanges++;

            var refused = Json(await McpTools.LabelConnection(engine, connection.Id, "dev"));

            Assert.Contains("Only a human", refused.GetProperty("error").GetString(), StringComparison.OrdinalIgnoreCase);
            Assert.Null(ConnectionRegistry.Find(connection.Id).Label);
            var agentActivity = Assert.Single(activities);
            Assert.Equal("label_connection", agentActivity.Kind);
            Assert.Equal("agent", agentActivity.Origin);
            Assert.False(agentActivity.Ok);
            Assert.Equal(refused.GetProperty("error").GetString(), agentActivity.Error);

            activities.Clear();
            var labelled = await rpc.labelConnection(connection.Id, "DEV");

            Assert.Equal("dev", labelled.Label);
            Assert.Equal("dev", ConnectionRegistry.Find(connection.Id).Label);
            var humanActivity = Assert.Single(activities);
            Assert.Equal("label_connection", humanActivity.Kind);
            Assert.Equal("human", humanActivity.Origin);
            Assert.True(humanActivity.Ok);
            Assert.Equal(connection.Id, humanActivity.Target);

            activities.Clear();
            var invalid = await Assert.ThrowsAsync<ArgumentException>(
                () => rpc.labelConnection(connection.Id.ToUpperInvariant(), "production"));
            Assert.Contains("not a target label", invalid.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("dev", ConnectionRegistry.Find(connection.Id).Label);
            var invalidActivity = Assert.Single(activities);
            Assert.Equal("human", invalidActivity.Origin);
            Assert.False(invalidActivity.Ok);
            Assert.Equal(invalid.Message, invalidActivity.Error);
            Assert.Equal(connection.Id, invalidActivity.Target);

            activities.Clear();
            var cleared = await rpc.labelConnection(connection.Id, "");
            Assert.Null(cleared.Label);
            var clearActivity = Assert.Single(activities);
            Assert.True(clearActivity.Ok);
            Assert.Contains("Cleared the label", clearActivity.Label, StringComparison.OrdinalIgnoreCase);

            activities.Clear();
            var missing = await Assert.ThrowsAsync<InvalidOperationException>(() => rpc.labelConnection("missing-id", "dev"));
            Assert.Contains("Open Connections", missing.Message, StringComparison.OrdinalIgnoreCase);
            var missingActivity = Assert.Single(activities);
            Assert.Equal("human", missingActivity.Origin);
            Assert.False(missingActivity.Ok);
            Assert.Equal(missing.Message, missingActivity.Error);
            Assert.Equal(0, modelChanges);
        }

        [Fact]
        public async Task Engine_label_default_fails_closed_as_agent_origin()
        {
            using var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, new Free());
            var connection = ConnectionRegistry.Remember("xmla", "powerbi://example/default-origin", "Sales", "Sales model");
            var activities = new List<ActivityEvent>();
            sessions.Bus.Activity += activities.Add;

            var refused = await Assert.ThrowsAsync<InvalidOperationException>(
                () => engine.LabelConnectionAsync(connection.Id, "dev"));

            Assert.Contains("Only a human", refused.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Null(ConnectionRegistry.Find(connection.Id).Label);
            var activity = Assert.Single(activities);
            Assert.Equal("agent", activity.Origin);
            Assert.False(activity.Ok);
        }

        [Fact]
        public async Task Forget_public_doors_report_success_missing_and_governance_refusal_without_model_changes()
        {
            using var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, new Free());
            var rpc = new EngineRpcTarget(engine);
            var scratch = ConnectionRegistry.Remember("xmla", "powerbi://example/scratch", "Scratch", "Scratch model");
            var governed = ConnectionRegistry.Remember("xmla", "powerbi://example/governed", "Finance", "Finance model");
            ConnectionRegistry.SetLabel(governed.Id, "prod", "human");
            var activities = new List<ActivityEvent>();
            var modelChanges = 0;
            sessions.Bus.Activity += activities.Add;
            sessions.Bus.Changed += _ => modelChanges++;

            var forgotten = Json(await McpTools.ForgetConnection(engine, scratch.Id));
            Assert.True(forgotten.GetProperty("forgotten").GetBoolean());
            Assert.Null(ConnectionRegistry.Find(scratch.Id));
            AssertActivity(activities[0], "agent", ok: true, scratch.Id);

            var missing = Json(await McpTools.ForgetConnection(engine, scratch.Id));
            Assert.False(missing.GetProperty("forgotten").GetBoolean());
            Assert.Contains("No remembered connection", missing.GetProperty("error").GetString(), StringComparison.OrdinalIgnoreCase);
            AssertActivity(activities[1], "agent", ok: false, scratch.Id);
            Assert.Contains("Open Connections", activities[1].Error, StringComparison.OrdinalIgnoreCase);
            var internalToolName = string.Concat("list", "_connections");
            Assert.DoesNotContain(internalToolName, activities[1].Error, StringComparison.OrdinalIgnoreCase);

            var refused = Json(await McpTools.ForgetConnection(engine, governed.Id.ToUpperInvariant()));
            Assert.False(refused.GetProperty("forgotten").GetBoolean());
            Assert.Contains("Only a human", refused.GetProperty("error").GetString(), StringComparison.OrdinalIgnoreCase);
            Assert.NotNull(ConnectionRegistry.Find(governed.Id));
            AssertActivity(activities[2], "agent", ok: false, governed.Id);

            Assert.True(await rpc.forgetConnection(governed.Id));
            Assert.Null(ConnectionRegistry.Find(governed.Id));
            AssertActivity(activities[3], "human", ok: true, governed.Id);
            Assert.Equal(4, activities.Count);
            Assert.Equal(0, modelChanges);
        }

        private static JsonElement Json(object value)
        {
            using var document = JsonDocument.Parse(JsonSerializer.Serialize(value));
            return document.RootElement.Clone();
        }

        private static void AssertActivity(ActivityEvent activity, string origin, bool ok, string target)
        {
            Assert.Equal("forget_connection", activity.Kind);
            Assert.Equal(origin, activity.Origin);
            Assert.Equal(ok, activity.Ok);
            Assert.Equal(target, activity.Target);
        }
    }
}
