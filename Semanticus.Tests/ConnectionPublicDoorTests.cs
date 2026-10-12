using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using ModelContextProtocol.Protocol;
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
            AgentPolicyStore.RootOverride = _root; ApprovalLedger.RootOverride = _root;   // the test sets the locked preset: its own policy and approval files, so another class's preset or approval cannot reach it
        }

        public void Dispose()
        {
            ConnectionRegistry.RootOverride = _safeRoot;
            AgentPolicyStore.RootOverride = _safeRoot; ApprovalLedger.RootOverride = _safeRoot;
            try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { }
        }

        [Fact]
        public async Task Label_connection_updates_the_saved_connection_through_the_MCP_door()
        {
            using var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, new Free());
            var connection = ConnectionRegistry.Remember("xmla", "powerbi://example/mcp-label", "Sales", "Sales model");
            var activities = new List<ActivityEvent>();
            sessions.Bus.Activity += activities.Add;
            var (host, client) = await ToolMergeTests.StartDoorAsync(engine, gated: true);
            using (host)
            await using (client)
            {
                Assert.Contains(await client.ListToolsAsync(), t => t.Name == "label_connection");
                var result = await client.CallToolAsync("label_connection", new Dictionary<string, object>
                { ["id"] = connection.Id, ["label"] = "DEV" });
                Assert.False(result.IsError == true);
                Assert.Equal("dev", ConnectionRegistry.Find(connection.Id).Label);
                Assert.Contains("dev", result.Content.OfType<TextContentBlock>().First().Text);
                Assert.Contains(activities, a => a.Kind == "label_connection" && a.Origin == "agent" && a.Ok);
                ConnectionRegistry.SetLabel(connection.Id, "prod", "human");
                foreach (var label in new[] { "dev", "" })
                {
                    var refused = await client.CallToolAsync("label_connection", new Dictionary<string, object>
                    { ["id"] = connection.Id, ["label"] = label });
                    Assert.True(refused.IsError);
                    Assert.Equal("label_connection failed: Changing a connection's label changes what your assistant may do there, so only you can change it, in Connections.",
                        refused.Content.OfType<TextContentBlock>().First().Text);
                    Assert.Equal("prod", ConnectionRegistry.Find(connection.Id).Label);
                }
                AgentPolicyStore.SetPreset("locked", "human");
                try
                {
                    foreach (var label in new[] { "dev", "" })
                    {
                        var refused = await client.CallToolAsync("label_connection", new Dictionary<string, object>
                        { ["id"] = connection.Id, ["label"] = label });
                        Assert.True(refused.IsError);
                        Assert.Equal("label_connection failed: Changing a connection's label changes what your assistant may do there, so only you can change it, in Connections.",
                            refused.Content.OfType<TextContentBlock>().First().Text);
                        Assert.Equal("prod", ConnectionRegistry.Find(connection.Id).Label);
                    }
                    var unlabelled = ConnectionRegistry.Remember("xmla", "powerbi://example/locked-label", "Sales", "Sales model");
                    var held = await client.CallToolAsync("label_connection", new Dictionary<string, object>
                    { ["id"] = unlabelled.Id, ["label"] = "dev" });
                    Assert.True(held.IsError, "the locked call said: " + string.Join(" ", held.Content.OfType<TextContentBlock>().Select(x => x.Text)));
                    Assert.Null(ConnectionRegistry.Find(unlabelled.Id).Label);
                }
                finally { AgentPolicyStore.SetPreset("standard", "human"); }
                await host.StopAsync();
            }
        }

        private sealed class Free : IEntitlement
        {
            public bool IsPro => false;
            public EntitlementInfo Info => new EntitlementInfo { Tier = "free" };
        }

        [Fact]
        public async Task Agent_cannot_change_a_connections_existing_label()
        {
            using var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, new Free());
            var connection = ConnectionRegistry.Remember("xmla", "powerbi://example/existing-label", "Sales", "Sales model");
            ConnectionRegistry.SetLabel(connection.Id, "prod", "human");

            var refusal = await Assert.ThrowsAsync<InvalidOperationException>(
                () => McpTools.LabelConnection(engine, connection.Id, "dev"));

            Assert.Equal("Changing a connection's label changes what your assistant may do there, so only you can change it, in Connections.", refusal.Message);
            Assert.Equal("prod", ConnectionRegistry.Find(connection.Id).Label);
        }

        [Theory]
        [InlineData("")]
        [InlineData(null)]
        [InlineData("prod")]
        public async Task Agent_cannot_clear_or_reset_a_connections_existing_label(string label)
        {
            using var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, new Free());
            var rpc = new EngineRpcTarget(engine, "agent");
            var connection = ConnectionRegistry.Remember("xmla", "powerbi://example/clear-label", "Sales", "Sales model");
            ConnectionRegistry.SetLabel(connection.Id, "prod", "human");
            var activities = new List<ActivityEvent>();
            sessions.Bus.Activity += activities.Add;

            var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() => rpc.labelConnection(connection.Id, label));

            Assert.Equal("Changing a connection's label changes what your assistant may do there, so only you can change it, in Connections.", refusal.Message);
            Assert.Equal("prod", ConnectionRegistry.Find(connection.Id).Label);
            Assert.Empty(activities); // The RPC gate refuses before the engine method runs.

            var directRefusal = await Assert.ThrowsAsync<InvalidOperationException>(
                () => engine.LabelConnectionAsync(connection.Id, label, "agent"));
            Assert.Equal(refusal.Message, directRefusal.Message);
            Assert.Equal("prod", ConnectionRegistry.Find(connection.Id).Label);
            var activity = Assert.Single(activities);
            Assert.Equal("agent", activity.Origin);
            Assert.False(activity.Ok);
            Assert.Equal(refusal.Message, activity.Error);
        }

        [Fact]
        public async Task Label_public_doors_broadcast_exact_outcomes()
        {
            using var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, new Free());
            var rpc = new EngineRpcTarget(engine);
            var connection = ConnectionRegistry.Remember("xmla", "powerbi://example/public-label", "Sales", "Sales model");
            var activities = new List<ActivityEvent>();
            var modelChanges = 0;
            sessions.Bus.Activity += activities.Add;
            sessions.Bus.Changed += _ => modelChanges++;

            foreach (var attempt in new Func<Task>[]
            {
                () => new EngineRpcTarget(engine, "agent").labelConnection(connection.Id, "dev"),
                () => engine.LabelConnectionAsync(connection.Id, "dev", "agent"),
            })
            {
                await rpc.labelConnection(connection.Id, "");
                activities.Clear();
                await attempt();
                Assert.Equal("dev", ConnectionRegistry.Find(connection.Id).Label);
                var agentActivity = Assert.Single(activities);
                Assert.Equal("label_connection", agentActivity.Kind);
                Assert.Equal("agent", agentActivity.Origin);
                Assert.True(agentActivity.Ok);
            }

            await rpc.labelConnection(connection.Id, "prod");
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
        public async Task Engine_label_default_records_agent_origin()
        {
            using var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, new Free());
            var connection = ConnectionRegistry.Remember("xmla", "powerbi://example/default-origin", "Sales", "Sales model");
            var activities = new List<ActivityEvent>();
            sessions.Bus.Activity += activities.Add;

            var result = await engine.LabelConnectionAsync(connection.Id, "dev");
            Assert.Equal("dev", result.Label);
            Assert.Equal("dev", ConnectionRegistry.Find(connection.Id).Label);
            var activity = Assert.Single(activities);
            Assert.Equal("agent", activity.Origin);
            Assert.True(activity.Ok);
        }

        [Fact]
        public async Task Connection_sidecar_shared_defaults_fail_closed_as_agent_while_the_ui_boundary_stays_human()
        {
            using var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, new Free());
            var activities = new List<ActivityEvent>();
            sessions.Bus.Activity += activities.Add;

            var remembered = await engine.RememberXmlaConnectionAsync(
                "powerbi://example/shared-default", "Sales", "Sales model", "azcli");
            var rememberActivity = Assert.Single(activities);
            Assert.Equal("remember_xmla_connection", rememberActivity.Kind);
            Assert.Equal("agent", rememberActivity.Origin);

            activities.Clear();
            await engine.OpenAsync(TestModels.FindBim());
            await engine.SetPublishDestinationAsync(remembered.Id, origin: " ");
            var publishActivity = Assert.Single(activities);
            Assert.Equal("set_publish_destination", publishActivity.Kind);
            Assert.Equal("agent", publishActivity.Origin);

            foreach (var method in new[]
            {
                nameof(IEngine.RememberXmlaConnectionAsync),
                nameof(IEngine.PrepareWorkingCopyAsync),
                nameof(IEngine.SetPublishDestinationAsync),
            })
            {
                AssertOriginDefault(typeof(IEngine), method, "agent");
                AssertOriginDefault(typeof(LocalEngine), method, "agent");
                AssertOriginDefault(typeof(RemoteEngine), method, "agent");
            }

            AssertOriginDefault(typeof(EngineRpcTarget), "rememberXmlaConnection", "human");
            AssertOriginDefault(typeof(EngineRpcTarget), "prepareWorkingCopy", "human");
            AssertOriginDefault(typeof(EngineRpcTarget), "setPublishDestination", "human");
        }

        [Fact]
        public async Task Public_publish_routes_refuse_a_loopback_xmla_destination()
        {
            using var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, new Free());
            var rpc = new EngineRpcTarget(engine);
            await engine.OpenAsync(TestModels.FindBim());
            var loopback = ConnectionRegistry.Remember("xmla", "localhost:51234", "LocalLoop", "Local loop");
            var remote = ConnectionRegistry.Remember("xmla", "powerbi://example/workspace", "Published", "Published");

            var mcpSet = await Assert.ThrowsAsync<InvalidOperationException>(
                () => McpTools.SetPublishDestination(engine, loopback.Id));
            var rpcSet = await Assert.ThrowsAsync<InvalidOperationException>(
                () => rpc.setPublishDestination(loopback.Id));
            Assert.Equal(ConnectionJobs.PublishLoopback, mcpSet.Message);   // the sentence the Connections page shows beside it
            Assert.Equal(ConnectionJobs.PublishLoopback, rpcSet.Message);   // the sentence the Connections page shows beside it
            Assert.False((await engine.ConnectionContextAsync()).Publishing.Available);

            var parent = Path.Combine(_root, "models");
            var mcpPrep = await Assert.ThrowsAsync<InvalidOperationException>(
                () => McpTools.PrepareWorkingCopy(engine, remote.Id, parent, false, null, loopback.Id));
            var rpcPrep = await Assert.ThrowsAsync<InvalidOperationException>(
                () => rpc.prepareWorkingCopy(remote.Id, parent, false, null, loopback.Id));
            Assert.Equal(ConnectionJobs.PublishLoopback, mcpPrep.Message);   // the sentence the Connections page shows beside it
            Assert.Equal(ConnectionJobs.PublishLoopback, rpcPrep.Message);   // the sentence the Connections page shows beside it
            Assert.False(Directory.Exists(parent));
        }

        [Fact]
        public async Task Opening_a_local_file_is_remembered_on_both_doors()
        {
            using var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, new Free());
            var rpc = new EngineRpcTarget(engine);
            var bim = TestModels.FindBim();
            var full = Path.GetFullPath(bim);

            await McpTools.OpenModel(engine, bim);
            var mcpList = await McpTools.ListConnections(engine);
            Assert.Contains(mcpList, r => r.Kind == "file"
                && string.Equals(r.Endpoint, full, StringComparison.OrdinalIgnoreCase));

            await rpc.open(bim);
            var rpcList = await rpc.listConnections();
            Assert.Contains(rpcList, r => r.Kind == "file"
                && string.Equals(r.Endpoint, full, StringComparison.OrdinalIgnoreCase));
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

        private static void AssertOriginDefault(Type type, string method, string expected)
        {
            var origin = type.GetMethod(method, BindingFlags.Instance | BindingFlags.Public)!
                .GetParameters().Single(parameter => parameter.Name == "origin");
            Assert.True(origin.HasDefaultValue);
            Assert.Equal(expected, origin.DefaultValue);
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
