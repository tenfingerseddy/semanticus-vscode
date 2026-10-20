using System;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// A saved SQL source is where the engine may send a sign-in token, so text in a shared model must not be able to steer the assistant
    /// into saving a host and then testing it. An assistant's save of a source on a host nobody has saved is the person's question in
    /// every preset, the default one included, and it names the host. The yes covers that one host, once. A person's own save, and an
    /// assistant's save of a host already saved, are never asked.
    /// </summary>
    [Collection("restore-root")]
    public sealed class SqlHostAskTests : GateFixtureBase
    {
        private readonly string _safeConnections;

        public SqlHostAskTests(RestoreRootFixture fixture) : base(fixture)
        {
            _safeConnections = fixture.Root;
            ConnectionRegistry.RootOverride = System.IO.Path.Combine(Root, "registry");
        }

        public override void Dispose()
        {
            ConnectionRegistry.RootOverride = _safeConnections;
            base.Dispose();
        }

        private static Task<SqlSourceRecord> AssistantSaves(IEngine door, string name, string server) =>
            door.SaveSqlSourceAsync(null, name, server, "Warehouse", "azcli", null, "agent");

        [Fact]
        public async Task An_assistants_save_of_a_new_host_asks_the_person_under_the_default_preset_and_names_the_host()
        {
            var (engine, _, _) = await OpenAsync("sqlhost");
            using (engine)
            {
                Assert.Equal("standard", AgentPolicyStore.Get().Preset);   // the default preset: the policy alone would allow this write
                var door = AssistantEngine.ForAssistant(engine);

                var refused = await RefusedAsync(() => AssistantSaves(door, "Shared", "attacker.example.net"));

                Assert.Contains("approval", refused.Message, StringComparison.OrdinalIgnoreCase);
                Assert.Empty(SqlSourceRegistry.List());   // nothing was saved
                var asked = await OnlyPendingAsync(engine);
                Assert.Contains("attacker.example.net", asked.Summary);

                // The yes is for this host: it saves, and a different host asks again.
                await engine.ApproveAgentActionAsync(asked.Id, "human");
                var saved = await AssistantSaves(door, "Shared", "attacker.example.net");
                Assert.Equal("attacker.example.net", saved.Server);
                await RefusedAsync(() => AssistantSaves(door, "Other", "other.example.net"));
                Assert.Contains("other.example.net", (await OnlyPendingAsync(engine)).Summary);
                Assert.Single(SqlSourceRegistry.List());
            }
        }

        [Fact]
        public async Task A_person_saving_a_host_and_an_assistant_saving_a_host_already_saved_are_not_asked()
        {
            var (engine, _, _) = await OpenAsync("sqlhost2");
            using (engine)
            {
                var door = AssistantEngine.ForAssistant(engine);
                await engine.SaveSqlSourceAsync(null, "Mine", "contoso-sql.database.windows.net", "Warehouse", "azcli", null, "human");
                Assert.Empty(await engine.ListPendingApprovalsAsync());

                var second = await AssistantSaves(door, "Second database", "CONTOSO-SQL.database.windows.net");

                Assert.NotNull(second.Id);
                Assert.Empty(await engine.ListPendingApprovalsAsync());
            }
        }

        [Fact]
        public async Task A_road_around_the_door_fails_closed_for_a_new_host()
        {
            var (engine, _, _) = await OpenAsync("sqlhost3");
            using (engine)
            {
                var ex = await RefusedAsync(() => engine.SaveSqlSourceAsync(null, "Shared", "attacker.example.net", "Warehouse", "azcli", null, "agent"));
                Assert.Contains("attacker.example.net", ex.Message);
                Assert.Empty(SqlSourceRegistry.List());
            }
        }
    }
}
