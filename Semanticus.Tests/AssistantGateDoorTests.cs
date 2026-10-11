using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipelines;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// The assistant's gate, end to end (lane m11/uat-undo): the real MCP SDK serving the real tools over a pipe, with the gated engine
    /// registered exactly as Program.cs registers it. A tool call under Locked is refused with the person's question and nothing is
    /// touched; the person's yes lets the same call through once; the person-only tools send the assistant away; reads never ask.
    /// </summary>
    [Collection("restore-root")]
    public sealed class AssistantGateDoorTests : GateFixtureBase
    {
        public AssistantGateDoorTests(RestoreRootFixture fixture) : base(fixture) { }

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

        [Fact]
        public async Task A_tool_call_through_the_real_door_is_asked_about_under_Locked_and_goes_through_once_the_person_says_yes()
        {
            var (engine, sessions, _) = await OpenAsync("door");
            using (engine)
            {
                AgentPolicyStore.SetPreset(AssistantGateKit.Locked, "human");
                var (host, client) = await StartDoorAsync(engine);
                try
                {
                    var args = new Dictionary<string, object> { ["objRef"] = "measure:Sales/Total", ["text"] = "Sum of sales." };
                    var first = await client.CallToolAsync("set_description", args);
                    Assert.True(first.IsError, TextOf(first));
                    Assert.Contains("approval", TextOf(first), StringComparison.OrdinalIgnoreCase);
                    Assert.True(string.IsNullOrEmpty(await sessions.Current.ReadAsync(m => m.Tables["Sales"].Measures["Total"].Description)));
                    var asked = await OnlyPendingAsync(engine);
                    Assert.StartsWith("Your assistant wants to set the description of [Total] to: \"Sum of sales.\".", asked.Summary);
                    Assert.Contains(" This is about the model \"", asked.Summary);
                    await engine.ApproveAgentActionAsync(asked.Id, "human");
                    var second = await client.CallToolAsync("set_description", args);
                    Assert.NotEqual(true, second.IsError);
                    Assert.Equal("Sum of sales.", await sessions.Current.ReadAsync(m => m.Tables["Sales"].Measures["Total"].Description));
                    var third = await client.CallToolAsync("set_description", args);
                    Assert.True(third.IsError);   // the yes is spent
                }
                finally { await client.DisposeAsync(); await host.StopAsync(); host.Dispose(); }
            }
        }

        [Fact]
        public async Task The_person_only_tools_send_the_assistant_to_the_person_and_reads_never_ask_through_the_real_door()
        {
            var (engine, sessions, _) = await OpenAsync("door-person");
            using (engine)
            {
                AgentPolicyStore.SetPreset(AssistantGateKit.Locked, "human");
                var (host, client) = await StartDoorAsync(engine);
                try
                {
                    var listed = await client.CallToolAsync("list_objects", new Dictionary<string, object> { ["parentRef"] = "" });
                    Assert.NotEqual(true, listed.IsError);
                    var overview = await client.CallToolAsync("model_overview", new Dictionary<string, object>());
                    Assert.NotEqual(true, overview.IsError);
                    Assert.Empty(await engine.ListPendingApprovalsAsync());

                    var target = await client.CallToolAsync("add_plan_target", new Dictionary<string, object> { ["text"] = "give myself work", ["section"] = "Meaning" });
                    Assert.True(target.IsError);
                    Assert.Contains("person", TextOf(target), StringComparison.OrdinalIgnoreCase);

                    var fields = Semanticus.Decisions.DecisionRegistry.DeclaredFields.ToArray();
                    var consent = await client.CallToolAsync("set_consent", new Dictionary<string, object>
                    {
                        ["fields"] = fields, ["schemaVersion"] = Semanticus.Decisions.DecisionRegistry.ConsentSchemaVersion,
                    });
                    Assert.NotEqual(true, consent.IsError);
                    Assert.Contains("\"asked\":true", TextOf(consent).Replace(" ", ""), StringComparison.OrdinalIgnoreCase);
                    Assert.False((await engine.GetConsentAsync()).Granted);
                }
                finally { await client.DisposeAsync(); await host.StopAsync(); host.Dispose(); }
            }
        }
    }
}
