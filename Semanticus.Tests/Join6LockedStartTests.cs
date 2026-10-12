using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipelines;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Semanticus.Engine;
using Semanticus.Engine.StartPlan;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// Join part 6, J1 (security). Under the Locked preset every assistant write is held for the person, and the spec-first start is assistant writing
    /// too: the plan it makes, the edits and words it writes into the plan, the permission it gives for Jev, the build and the discard. These go through
    /// the real MCP door (the SDK serving the real tools, the gated engine registered as Program.cs registers it) and must each come back as the person's
    /// question with nothing changed; the person's yes lets the same call through once; and below Locked nothing is asked.
    /// </summary>
    [Collection("restore-root")]
    public sealed class Join6LockedStartTests : GateFixtureBase
    {
        public Join6LockedStartTests(RestoreRootFixture fixture) : base(fixture) { }

        private const string Server = "wh-j8.datawarehouse.fabric.microsoft.com", Db = "ContosoConsulting";

        private static Func<string, string, string, int, Task<ResultSet>> J8Rows(FabricSchema schema)
        {
            var t = typeof(StartPlanEndToEndTests).GetNestedType("J8Source", BindingFlags.NonPublic);
            var src = Activator.CreateInstance(t, schema);
            return (Func<string, string, string, int, Task<ResultSet>>)Delegate.CreateDelegate(typeof(Func<string, string, string, int, Task<ResultSet>>), src, t.GetMethod("Execute"));
        }

        private LocalEngine NewEngine()
        {
            var engine = new LocalEngine(new SessionManager(), TestEntitlements.Free, Path.Combine(Root, "ws"));
            engine.StartPlanSeedForTest = "00112233445566778899aabbccddeeff";
            engine.StartPlanSchemaReaderForTest = (req, origin) => Task.FromResult(SqlDraftBuildTests.J8Schema());
            engine.SourceSampleExecutorForTest = J8Rows(SqlDraftBuildTests.J8Schema());
            return engine;
        }

        private static async Task<(IHost host, McpClient client)> StartDoorAsync(LocalEngine engine)
        {
            var toServer = new Pipe();
            var toClient = new Pipe();
            var builder = Host.CreateApplicationBuilder(Array.Empty<string>());
            builder.Logging.ClearProviders();
            builder.Services.AddSingleton<IEngine>(AssistantEngine.ForAssistant(engine));   // as Program.cs registers it
            builder.Services.AddMcpServer()
                .WithStreamServerTransport(toServer.Reader.AsStream(), toClient.Writer.AsStream())
                .WithToolsFromAssembly(typeof(McpTools).Assembly)
                .WithRequestFilters(f => McpDoorFilters.Add(f, engine));
            var host = builder.Build();
            await host.StartAsync();
            var client = await McpClient.CreateAsync(new StreamClientTransport(toServer.Writer.AsStream(), toClient.Reader.AsStream()));
            return (host, client);
        }

        private static string TextOf(CallToolResult r) => string.Join("\n", r.Content.OfType<TextContentBlock>().Select(b => b.Text));

        private Dictionary<string, object> StartArgs(string folderName) => new Dictionary<string, object>
        {
            ["server"] = Server, ["database"] = Db, ["folder"] = Path.Combine(Root, folderName), ["name"] = "Uplift",
        };

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task J1_start_model_from_sql_is_held_under_Locked_and_makes_nothing_until_the_person_says_yes()
        {
            using var engine = NewEngine();
            AgentPolicyStore.SetPreset(AssistantGateKit.Locked, "human");
            var (host, client) = await StartDoorAsync(engine);
            try
            {
                var folder = Path.Combine(Root, "m1");
                var first = await client.CallToolAsync("start_model_from_sql", StartArgs("m1"));
                Assert.True(first.IsError, TextOf(first));
                Assert.Contains("approval", TextOf(first), StringComparison.OrdinalIgnoreCase);
                Assert.False(Directory.Exists(folder), "the folder was made before the person said yes");
                Assert.False((await engine.GetStartPlanAsync()).Exists);
                var asked = await OnlyPendingAsync(engine);
                Assert.Contains("start the new model", asked.Summary ?? "", StringComparison.OrdinalIgnoreCase);

                await ApproveAsync(engine);
                var second = await client.CallToolAsync("start_model_from_sql", StartArgs("m1"));
                Assert.False(second.IsError == true, TextOf(second));
                Assert.True(Directory.Exists(folder), "the person said yes and the model was still not made");
                Assert.True((await engine.GetStartPlanAsync()).Exists);
            }
            finally { await client.DisposeAsync(); await host.StopAsync(); host.Dispose(); }
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task J1_every_other_start_verb_is_held_under_Locked_and_leaves_the_plan_as_it_was()
        {
            using var engine = NewEngine();
            AgentPolicyStore.SetPreset("open", "human");
            // the person starts the plan (their own door), then turns the policy to Locked
            var view = await engine.StartModelFromSqlAsync(new StartModelRequest { Server = Server, Database = Db, Folder = Path.Combine(Root, "m2"), Name = "Uplift" }, "human");
            await engine.StartSampleSettledForTest();
            await engine.StartDecideSettledForTest();
            await engine.StartPreviewSettledForTest();
            var digestBefore = (await engine.GetStartPlanAsync()).Digest;   // after the plan's own background work, which moves the digest on its own
            AgentPolicyStore.SetPreset(AssistantGateKit.Locked, "human");
            var (host, client) = await StartDoorAsync(engine);
            try
            {
                var calls = new (string Tool, Dictionary<string, object> Args)[]
                {
                    ("edit_start_plan", new Dictionary<string, object> { ["op"] = "answer", ["target"] = "fiscal_year", ["value"] = "7/1" }),
                    ("write_start_words", new Dictionary<string, object> { ["words"] = new[] { new Dictionary<string, object> { ["ref"] = "table:Nope", ["kind"] = "description", ["text"] = "Words." } } }),
                    ("build_start_plan", new Dictionary<string, object> { ["digest"] = digestBefore }),
                    ("discard_start_plan", new Dictionary<string, object>()),
                };
                foreach (var (tool, args) in calls)
                {
                    var r = await client.CallToolAsync(tool, args);
                    Assert.True(r.IsError, tool + " was not held: " + TextOf(r));
                    Assert.Contains("approval", TextOf(r), StringComparison.OrdinalIgnoreCase);
                    var asked = await OnlyPendingAsync(engine);
                    await engine.DenyAgentActionAsync(asked.Id, "human");
                    var now = await engine.GetStartPlanAsync();
                    Assert.True(now.Exists && now.Digest == digestBefore, tool + " changed the plan before the person said yes");
                }

                // the person's yes lets the same call through once: the discard is the plainest to see
                var held = await client.CallToolAsync("discard_start_plan", new Dictionary<string, object>());
                Assert.True(held.IsError, TextOf(held));
                await ApproveAsync(engine);
                var done = await client.CallToolAsync("discard_start_plan", new Dictionary<string, object>());
                Assert.False(done.IsError == true, TextOf(done));
                Assert.False((await engine.GetStartPlanAsync()).Exists);
            }
            finally { await client.DisposeAsync(); await host.StopAsync(); host.Dispose(); }
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task J1_below_Locked_the_start_verbs_ask_nothing_and_the_reads_never_ask()
        {
            using var engine = NewEngine();
            AgentPolicyStore.SetPreset("open", "human");
            var (host, client) = await StartDoorAsync(engine);
            try
            {
                var started = await client.CallToolAsync("start_model_from_sql", StartArgs("m3"));
                Assert.False(started.IsError == true, TextOf(started));
                Assert.Empty(await engine.ListPendingApprovalsAsync());
                AgentPolicyStore.SetPreset(AssistantGateKit.Locked, "human");
                var read = await client.CallToolAsync("get_start_plan", new Dictionary<string, object>());
                Assert.False(read.IsError == true, TextOf(read));
                var words = await client.CallToolAsync("get_start_words", new Dictionary<string, object>());
                Assert.False(words.IsError == true, TextOf(words));
                Assert.Empty(await engine.ListPendingApprovalsAsync());
            }
            finally { await client.DisposeAsync(); await host.StopAsync(); host.Dispose(); }
        }

        [Fact]
        public void J1_approving_the_start_plan_is_an_edit_row_and_every_start_method_has_a_row()
        {
            Assert.Equal(GateKind.Hold, GateRegistry.Find("ApproveStartPlanAsync").Kind);
            Assert.Equal(AgentCapability.EditLocal, GateRegistry.Find("ApproveStartPlanAsync").Capability);   // Kane, 10 Oct 2026: the person's word in chat is enough
            // the plan lives outside the model (Write model files); only the build commits to the model (Edit the open model); the start does both
            Assert.Equal(GateKind.Consent, GateRegistry.Find("GrantStartDisclosureAsync").Kind);   // a consent row in every preset, like set_consent; the assistant's call grants after the person's yes in chat (chatyes)
            foreach (var (m, cap) in new[] { ("StartModelFromSqlAsync", AgentCapability.EditLocal), ("EditStartPlanAsync", AgentCapability.DeployFile),
                                              ("WriteStartWordsAsync", AgentCapability.DeployFile), ("BuildStartPlanAsync", AgentCapability.EditLocal), ("DiscardStartPlanAsync", AgentCapability.DeployFile) })
            {
                var row = GateRegistry.Find(m);
                Assert.True(row != null && row.Kind == GateKind.Hold && row.Capabilities.Contains(cap), m + " is not a held row for " + cap);
            }
            foreach (var m in new[] { "GetStartPlanAsync", "GetStartWordsAsync" }) Assert.Equal(GateKind.Pass, GateRegistry.Find(m).Kind);
        }
    }
}
