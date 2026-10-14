// The assistant sign (1.4, Kane 12 Oct: "from the mockup A for both options"): the MCP initialize clientInfo name is read once by
// the engine, mapped to a plain name and a drawn tile, and reaches the Studio on the entitlement read and on assistant/call.

using System;
using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Newtonsoft.Json.Linq;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    public class AssistantSignTests
    {
        [Theory]
        [InlineData("claude-code", "claude", "Claude Code", "CC")]
        [InlineData("Claude Code", "claude", "Claude Code", "CC")]
        [InlineData("claude-ai", "claude", "Claude", "CL")]
        [InlineData("Claude Desktop", "claude", "Claude", "CL")]
        [InlineData("claude", "claude", "Claude", "CL")]
        [InlineData("codex-mcp-client", "codex", "Codex", "CX")]
        [InlineData("Codex", "codex", "Codex", "CX")]
        [InlineData("github-copilot-developer", "copilot", "GitHub Copilot", "GC")]
        [InlineData("GitHub Copilot", "copilot", "GitHub Copilot", "GC")]
        [InlineData("cursor-vscode", "cursor", "Cursor", "CU")]
        [InlineData("Cursor", "cursor", "Cursor", "CU")]
        public void A_known_client_maps_to_its_plain_name_and_initials(string client, string key, string name, string initials)
        {
            var who = AssistantIdentity.Map(client, "1.0");
            Assert.Equal(key, who.Key);
            Assert.Equal(name, who.Name);
            Assert.Equal(initials, who.Initials);
            Assert.Equal("1.0", who.Version);
        }

        [Fact]
        public void A_plain_unknown_client_keeps_its_own_name_with_initials_from_it()
        {
            var who = AssistantIdentity.Map("Zed Agent", null);
            Assert.Equal("other", who.Key);
            Assert.Equal("Zed Agent", who.Name);
            Assert.Equal("ZA", who.Initials);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("https://evil.example/x?y=1")]
        [InlineData("a;b<script>")]
        [InlineData("this-name-is-far-too-long-to-sit-in-a-small-bar-beside-a-tile")]
        // A program id, not a name: Kane's driver bar read "local-agent-mode-semanticus drives" (13 Oct 2026).
        [InlineData("local-agent-mode-semanticus")]
        [InlineData("mcp_client")]
        public void A_missing_or_odd_client_name_is_Your_assistant_with_no_initials(string client)
        {
            var who = AssistantIdentity.Map(client, "9");
            Assert.Equal("unknown", who.Key);
            Assert.Equal("Your assistant", who.Name);
            Assert.Null(who.Initials);
        }

        [Fact]
        public async Task The_name_is_read_from_the_first_call_and_the_entitlement_read_carries_it()
        {
            using var f = new DecisionFixture(TestEntitlements.Free);
            await f.Engine.CreateModelAsync("Contoso", 1604);
            Assert.Null((await f.Engine.GetEntitlementAsync()).Assistant);   // nobody has called yet: no sign
            await f.Engine.NoteAgentCallAsync("get_model_summary", null, "codex-mcp-client", "0.5.1");
            var who = (await f.Engine.GetEntitlementAsync()).Assistant;
            Assert.NotNull(who);
            Assert.Equal("codex", who.Key);
            Assert.Equal("Codex", who.Name);
            Assert.Equal("0.5.1", who.Version);
            Assert.Equal("free", (await f.Engine.GetEntitlementAsync()).Tier);   // the licence's own answer is untouched
        }

        [Fact]
        public async Task A_call_with_no_client_name_keeps_what_the_engine_already_read()
        {
            using var f = new DecisionFixture(TestEntitlements.Free);
            await f.Engine.CreateModelAsync("Contoso", 1604);
            await f.Engine.NoteAgentCallAsync("get_model_summary", null, "cursor", "1");
            await f.Engine.NoteAgentCallAsync("list_measures");
            Assert.Equal("cursor", (await f.Engine.GetEntitlementAsync()).Assistant.Key);
        }

        [Fact]
        public async Task Every_assistant_call_event_carries_the_assistant_for_the_live_Studio()
        {
            using var f = new DecisionFixture(TestEntitlements.Free);
            await f.Engine.CreateModelAsync("Contoso", 1604);
            var heard = new ConcurrentQueue<AgentCallEvent>();
            f.Sessions.Bus.AgentCall += heard.Enqueue;
            await McpHealthAppender.InvokeAsync(f.Engine, () => new System.Threading.Tasks.ValueTask<CallToolResult>(new CallToolResult()),
                "get_model_summary", null, "claude-code", "2.0.1");
            var call = Assert.Single(heard);
            Assert.Equal("claude", call.Assistant.Key);
            Assert.Equal("Claude Code", call.Assistant.Name);
        }

        [Fact]
        public async Task The_studio_rpc_reads_the_assistant_the_attached_door_told_the_owner()
        {
            using var f = new DecisionFixture(TestEntitlements.Free);
            await f.Engine.CreateModelAsync("Contoso", 1604);
            var agent = new EngineRpcTarget(f.Engine, "agent");
            await agent.noteAgentCall("get_model_summary", null, null, "github-copilot-developer", "1.2");
            var human = new EngineRpcTarget(f.Engine, "human");
            Assert.Equal("copilot", (await human.getEntitlement()).Assistant.Key);
            Assert.Equal("copilot", (await agent.getEntitlement()).Assistant.Key);   // and the assistant's own door reads the same
        }

        /// <summary>The real SDK in process, with the door's own filter chain and a client that names itself in initialize, as Claude Code does.</summary>
        private static async Task<(IHost host, McpClient client)> DoorAsync(IEngine engine, string clientName, string clientVersion)
        {
            var toServer = new Pipe();
            var toClient = new Pipe();
            var builder = Host.CreateApplicationBuilder(Array.Empty<string>());
            builder.Logging.ClearProviders();
            builder.Services.AddSingleton<IEngine>(engine);
            builder.Services.AddMcpServer()
                .WithStreamServerTransport(toServer.Reader.AsStream(), toClient.Writer.AsStream())
                .WithToolsFromAssembly(typeof(McpTools).Assembly)
                .WithRequestFilters(f => McpDoorFilters.Add(f, engine));
            var host = builder.Build();
            await host.StartAsync();
            var client = await McpClient.CreateAsync(new StreamClientTransport(toServer.Writer.AsStream(), toClient.Reader.AsStream()),
                new McpClientOptions { ClientInfo = new Implementation { Name = clientName, Version = clientVersion } });
            return (host, client);
        }

        [Theory]
        [InlineData("claude-code", "claude", "Claude Code")]
        [InlineData("codex-mcp-client", "codex", "Codex")]
        [InlineData("Zed Agent", "other", "Zed Agent")]
        public async Task The_door_reads_the_clientInfo_the_assistant_sent_in_initialize_and_get_entitlement_returns_it(string sent, string key, string name)
        {
            using var f = new DecisionFixture(TestEntitlements.Free);
            await f.Engine.CreateModelAsync("Contoso", 1604);
            var (host, client) = await DoorAsync(f.Engine, sent, "3.4.5");
            try
            {
                var result = await client.CallToolAsync("get_entitlement");   // the first call already carries the name: it is noted before the tool runs
                var json = JObject.Parse(string.Join("", result.Content.OfType<TextContentBlock>().Select(b => b.Text)));
                Assert.Equal("free", (string)json["tier"]);
                Assert.Equal(key, (string)json["assistant"]["key"]);
                Assert.Equal(name, (string)json["assistant"]["name"]);
                Assert.Equal("3.4.5", (string)json["assistant"]["version"]);
            }
            finally { await client.DisposeAsync(); await host.StopAsync(); host.Dispose(); }
        }
    }
}
