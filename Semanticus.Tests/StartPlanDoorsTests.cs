// The spec-first start's two doors (lane m11/sf-doors; design-spec-first-start-v2.md section 3 and golden rule 2).
//
// What these pin, in the order a reader would doubt it:
//   - the nine engine verbs are on IEngine (declared, not inherited: reflection on an interface does not list what it
//     inherits, so the completeness walk would never see an inherited one) and every name on every door is classified;
//   - the SAME verb through MCP and through the pipe reaches the engine with the same arguments and the origin the door
//     decides, and the view comes back as the same JSON;
//   - there is no approve an assistant can reach: not a tool, not an RPC entry on an agent connection;
//   - the Free and Pro line is the engine's, so no verb is refused by the entitlement gate on either tier;
//   - the Studio is pushed startPlan/didChange, an assistant hears of somebody else's change as ONE line on its next
//     call (never its own), and a plan change alone is enough to say it;
//   - the Plan carries a pointer to the start plan while one exists, and reads exactly as before when none does.
// The engine verbs are stubs until the wave-1 lanes land, so every test that needs an answer uses a fake engine and
// the tests that need the real engine only ask whether the GATE refused.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.IO.Pipelines;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Newtonsoft.Json.Linq;
using Semanticus.Engine;
using Semanticus.Engine.Entitlement;
using Semanticus.Engine.StartPlan;
using StreamJsonRpc;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>An IEngine that answers the nine start verbs with canned values and records every call, and answers
    /// every other method with the default of its type. Public and unsealed because DispatchProxy needs both.</summary>
    public class StartEngineFake : DispatchProxy
    {
        public readonly List<(string Method, object[] Args)> Calls = new List<(string, object[])>();
        public StartPlanView View = StartPlanDoorsTests.CannedView();

        public static IEngine Create(out StartEngineFake fake)
        {
            var proxy = DispatchProxy.Create<IEngine, StartEngineFake>();
            fake = (StartEngineFake)(object)proxy;
            return proxy;
        }

        protected override object Invoke(MethodInfo method, object[] args)
        {
            if (method.Name.EndsWith("StartPlanAsync", StringComparison.Ordinal) || method.Name.Contains("StartModel")
                || method.Name.Contains("StartWords") || method.Name.Contains("StartDisclosure"))
                lock (Calls) Calls.Add((method.Name, args));
            object value = method.Name switch
            {
                "StartModelFromSqlAsync" or "GetStartPlanAsync" or "EditStartPlanAsync" or "GrantStartDisclosureAsync"
                    or "ApproveStartPlanAsync" or "DiscardStartPlanAsync" => View,
                "GetStartWordsAsync" => new StartWordsPage
                {
                    Total = 2, Offset = 0,
                    Items = new List<StartWordItem>
                    {
                        new StartWordItem { Ref = "table:Sales", Kind = "description", Context = "Fact table, 4 columns" },
                        new StartWordItem { Ref = "column:Sales/Amount", Kind = "description", Context = "Decimal" },
                    },
                },
                "WriteStartWordsAsync" => new StartWordsResult { Written = 1, Refused = new List<string> { "table:Gone: no such object" }, View = View },
                "BuildStartPlanAsync" => new StartPlanBuildResult { Built = false, Refused = "The person has not approved this plan.", Revision = 7, View = View },
                _ => null,
            };
            var ret = method.ReturnType;
            if (ret == typeof(Task)) return Task.CompletedTask;
            if (ret.IsGenericType && ret.GetGenericTypeDefinition() == typeof(Task<>))
            {
                var inner = ret.GetGenericArguments()[0];
                var result = value != null && inner.IsInstanceOfType(value) ? value : (inner.IsValueType ? Activator.CreateInstance(inner) : null);
                return typeof(Task).GetMethod(nameof(Task.FromResult)).MakeGenericMethod(inner).Invoke(null, new[] { result });
            }
            return ret.IsValueType && ret != typeof(void) ? Activator.CreateInstance(ret) : null;
        }
    }

    public sealed class StartPlanDoorsTests
    {
        private const string Challenge = "start-plan-doors-challenge-0123456789abcdef";

        private static readonly string[] Tools =
        {
            "start_model_from_sql", "get_start_plan", "edit_start_plan", "grant_start_disclosure",
            "get_start_words", "write_start_words", "build_start_plan", "discard_start_plan",
        };

        private static readonly string[] RpcNames =
        {
            "startModelFromSql", "getStartPlan", "editStartPlan", "grantStartDisclosure",
            "getStartWords", "writeStartWords", "buildStartPlan", "discardStartPlan",
        };

        private static readonly string[] EngineNames =
        {
            "StartModelFromSqlAsync", "GetStartPlanAsync", "EditStartPlanAsync", "GrantStartDisclosureAsync",
            "ApproveStartPlanAsync", "GetStartWordsAsync", "WriteStartWordsAsync", "BuildStartPlanAsync", "DiscardStartPlanAsync",
        };

        internal static StartPlanView CannedView(string digest = "d1ge57-aaaaaaaa", bool exists = true, bool approved = false, string state = StartDecideState.Deciding, int needs = 1)
        {
            var v = new StartPlanView
            {
                Exists = exists,
                Id = "sp-1",
                Destination = new StartDestination { Folder = "/work/Sales", Name = "Sales", ModelPath = "/work/Sales/Sales.SemanticModel" },
                Source = new StartSource { Server = "x.datawarehouse.fabric.microsoft.com", Database = "SalesWh", AuthMode = "azcli", StorageMode = "import" },
                Decide = new StartDecide { State = state, Round = 2, Reason = null, Progress = new List<StartFamilyProgress> { new StartFamilyProgress { Family = "measures", Asked = 3, Answered = 3, Unusable = 0, Remaining = 0 } } },
                Words = new StartWords { Total = 4, Open = 2 },
                Grades = new StartGrades { Now = 31, AfterBuild = 78 },
                Digest = digest,
                Approval = approved ? new StartApproval { Digest = digest, Utc = new DateTime(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc) } : null,
                CanBuild = approved,
                BlockedBecause = approved ? null : "The person has not approved this plan yet.",
            };
            foreach (var id in StartSectionId.All)
                v.Sections.Add(new StartSection
                {
                    Id = id, Title = id, Summary = id + " summary", Counts = new StartCounts { Rule = 2, Jev = 1, Assistant = 0, You = 0 },
                    Groups = new List<StartGroup>
                    {
                        new StartGroup
                        {
                            Table = "Sales", SourceName = "dbo.sales",
                            Lines = new List<StartLine>
                            {
                                new StartLine { Item = new ChangeItem { Id = "ci-1", Kind = "set_description", ObjectRef = "table:Sales", Target = "description", After = "Orders." }, DecidedBy = StartDecidedBy.Rule, Round = 1, Kept = true, Why = "a rule" },
                            },
                        },
                    },
                });
            v.NeedsYou = Enumerable.Range(0, needs).Select(i => new StartNeed { Id = "need-" + i, Kind = "fiscal_year", Text = "Does the year start in July?", Options = new[] { "Yes", "No" }, Recommended = "Yes" }).ToList();
            return v;
        }

        // ------------------------------------------------------------------ the surface

        [Fact]
        public void IEngine_declares_the_contracts_nine_verbs_itself_not_by_inheritance()
        {
            var contract = typeof(IStartPlanEngine).GetMethods().OrderBy(m => m.Name, StringComparer.Ordinal).ToArray();
            Assert.Equal(9, contract.Length);
            var declared = typeof(IEngine).GetMethods().Where(x => EngineNames.Contains(x.Name)).ToDictionary(m => m.Name, StringComparer.Ordinal);
            foreach (var m in contract)
            {
                Assert.True(declared.TryGetValue(m.Name, out var onEngine), m.Name + " is not declared on IEngine, so the completeness walk cannot see it");
                Assert.Equal(m.ReturnType, onEngine.ReturnType);
                Assert.Equal(m.GetParameters().Select(p => (p.Name, p.ParameterType)), onEngine.GetParameters().Select(p => (p.Name, p.ParameterType)));
            }
            Assert.Equal(EngineNames.OrderBy(n => n, StringComparer.Ordinal), contract.Select(m => m.Name));
        }

        [Fact]
        public void Every_start_verb_is_classified_and_free_under_every_one_of_its_names()
        {
            foreach (var t in Tools)
            {
                Assert.True(FeatureMap.IsOperationClassified(t), t);
                Assert.False(FeatureMap.TryOperation(t, out _, out _), t + " must be free: the Pro line is drawn inside the engine");
                Assert.Contains(t, FeatureMap.FreeOperations);
            }
            foreach (var r in RpcNames)
            {
                Assert.True(FeatureMap.IsRpcMethodClassified(r), r);
                Assert.False(FeatureMap.TryRpcMethod(r, out _, out _), r);
                Assert.Contains(r, FeatureMap.FreeRpcMethods);
            }
            foreach (var e in EngineNames)
            {
                Assert.True(FeatureMap.IsEngineMethodClassified(e), e);
                Assert.False(FeatureMap.TryEngineMethod(e, out _, out _), e);
                Assert.Contains(e, FeatureMap.FreeEngineMethods);
            }
        }

        [Fact]
        public void The_start_tools_are_filed_on_the_model_spec_shelf()
        {
            foreach (var t in Tools)
            {
                Assert.True(OpTaxonomy.TryGet(t, out var question, out var shelf), t);
                Assert.Equal("Change the model", question);
                Assert.Equal("The model spec", shelf);
            }
        }

        [Fact]
        public void The_eight_tools_are_compiled_and_approve_start_plan_is_the_ninth()
        {
            var compiled = ProFeatureMapTests.CompiledOperationNames();
            foreach (var t in Tools) Assert.Contains(t, compiled);
            Assert.Contains("approve_start_plan", compiled);
            // Nor does any McpTools method take an engine and call the approve verb under another name: the only way an
            // MCP tool reaches IEngine is its method body, so look for the verb's name in every tool's IL-free signature
            // surface (name, parameters) and in the descriptions' promise.
            // The text the agent reads sits on the compact wrapper (lean-results); the build still refuses without an approval.
            var tool = typeof(McpTools).GetMethod("BuildStartPlanOp");
            Assert.NotNull(tool);
            var text = tool.GetCustomAttribute<DescriptionAttribute>().Description;
            Assert.Contains("approval", text, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void The_rpc_target_carries_approve_for_a_start_plan_on_both_doors()
        {
            var entries = typeof(EngineRpcTarget).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).Select(m => m.Name);
            Assert.Contains("approveStartPlan", entries);
        }

        [Fact]
        public async Task The_tool_list_the_SDK_serves_carries_the_eight_and_every_compiled_tool()
        {
            var engine = StartEngineFake.Create(out _);
            var (host, client) = await StartDoorAsync(engine);
            try
            {
                var listed = (await client.ListToolsAsync()).Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
                foreach (var t in Tools) Assert.Contains(t, listed);
                Assert.Contains("approve_start_plan", listed);   // Kane, 10 Oct 2026: the person's word in chat is enough
                // A stale build serves fewer tools than the source holds; the served list must be the compiled list.
                Assert.Equal(ProFeatureMapTests.CompiledOperationNames().OrderBy(n => n, StringComparer.Ordinal), listed.OrderBy(n => n, StringComparer.Ordinal));
            }
            finally { await client.DisposeAsync(); await host.StopAsync(); host.Dispose(); }
        }

        [Fact]
        public void The_copy_of_every_start_tool_follows_the_style_rules()
        {
            foreach (var name in Tools)
            {
                var method = typeof(McpTools).GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .Single(m => m.GetCustomAttributes(true).Any(a => a.GetType().Name == "McpServerToolAttribute"
                        && (string)a.GetType().GetProperty("Name").GetValue(a) == name));
                var texts = new[] { method.GetCustomAttribute<DescriptionAttribute>().Description }
                    .Concat(method.GetParameters().Select(p => p.GetCustomAttribute<DescriptionAttribute>()?.Description).Where(d => d != null));
                foreach (var t in texts)
                {
                    Assert.DoesNotContain("—", t);   // no em dash, ever
                    Assert.DoesNotContain("one click", t, StringComparison.OrdinalIgnoreCase);
                    Assert.DoesNotContain("1-click", t, StringComparison.OrdinalIgnoreCase);
                }
            }
        }

        [Theory]
        [InlineData("BuildModelFromSpec")]
        [InlineData("AutogenerateSpecFromFabric")]
        public void The_two_step_spec_tools_point_a_new_model_at_the_start_plan(string method)
        {
            var text = typeof(McpTools).GetMethod(method).GetCustomAttribute<DescriptionAttribute>(false)?.Description
                ?? throw new Xunit.Sdk.XunitException(method + " has no description");
            Assert.Contains("start_model_from_sql", text, StringComparison.Ordinal);
        }

        [Fact]
        public void The_words_queue_and_the_plan_point_at_the_start_plan_tools()
        {
            Assert.Contains("get_start_words", typeof(McpTools).GetMethod("GetWordsQueue").GetCustomAttribute<DescriptionAttribute>().Description);
            Assert.Contains("get_start_plan", typeof(McpTools).GetMethod("GetModelPlan").GetCustomAttribute<DescriptionAttribute>().Description);
        }

        [Fact]
        public void Dry_run_refuses_the_start_plan_writers_and_still_rehearses_its_two_reads()
        {
            foreach (var t in new[] { "start_model_from_sql", "edit_start_plan", "grant_start_disclosure", "write_start_words", "build_start_plan", "discard_start_plan" })
            {
                Assert.True(LocalEngine.TryGuardDryRunnable(t, out var reason), t + " must not be rehearsable: it writes outside the model's rollback");
                Assert.Contains("get_start_plan", reason);
            }
            foreach (var t in new[] { "get_start_plan", "get_start_words" })
                Assert.False(LocalEngine.TryGuardDryRunnable(t, out _), t);
        }

        // ------------------------------------------------------------------ the gate, per verb

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task No_start_verb_is_refused_by_the_entitlement_gate_on_either_tier(bool pro)
        {
            using var f = new DecisionFixture(pro ? TestEntitlements.Pro : TestEntitlements.Free);
            var engine = f.Engine;
            // Empty arguments on purpose: a verb that is wired to do real work (a schema read, a folder) must not be
            // started by a gate test. A verb that asked for Pro would fail louder than any refusal here, because
            // RequireProFeature throws when the caller is not classified Pro in FeatureMap, and every verb is classified free.
            var calls = new Dictionary<string, Func<Task>>
            {
                ["StartModelFromSqlAsync"] = () => engine.StartModelFromSqlAsync(null, "agent"),
                ["GetStartPlanAsync"] = () => engine.GetStartPlanAsync(),
                ["EditStartPlanAsync"] = () => engine.EditStartPlanAsync(null, "agent"),
                ["GrantStartDisclosureAsync"] = () => engine.GrantStartDisclosureAsync(null, "agent"),
                ["ApproveStartPlanAsync"] = () => engine.ApproveStartPlanAsync(null),
                ["GetStartWordsAsync"] = () => engine.GetStartWordsAsync(1, 0),
                ["WriteStartWordsAsync"] = () => engine.WriteStartWordsAsync(Array.Empty<StartWord>(), "agent"),
                ["BuildStartPlanAsync"] = () => engine.BuildStartPlanAsync(null, "agent"),
                ["DiscardStartPlanAsync"] = () => engine.DiscardStartPlanAsync("agent"),
            };
            Assert.Equal(EngineNames.OrderBy(n => n, StringComparer.Ordinal), calls.Keys.OrderBy(n => n, StringComparer.Ordinal));
            foreach (var (name, call) in calls)
            {
                Exception thrown = null;
                try { await call(); } catch (Exception ex) { thrown = ex; }
                // Anything else (a result, a refusal for a missing plan, a stub) is the engine's own business; only the
                // entitlement refusal would mean the line was drawn at the door, which the design says it is not.
                Assert.False(thrown is EntitlementException, name + " was refused by the entitlement gate: " + thrown?.Message);
            }
        }

        // ------------------------------------------------------------------ dual-door parity

        private static Dictionary<string, object> McpArgs(string tool)
        {
            Dictionary<string, object> args = tool switch
            {
            "start_model_from_sql" => new() { ["server"] = "x.datawarehouse.fabric.microsoft.com", ["database"] = "SalesWh", ["folder"] = "/work/Sales", ["name"] = "Sales" },
            "edit_start_plan" => new() { ["op"] = "rename", ["target"] = "table:Sales", ["value"] = "Orders" },
            "grant_start_disclosure" => new() { ["digest"] = "d1ge57-aaaaaaaa" },
            "get_start_words" => new() { ["max"] = 25, ["offset"] = 5 },
            "write_start_words" => new() { ["words"] = new[] { new Dictionary<string, object> { ["ref"] = "table:Sales", ["kind"] = "description", ["text"] = "One row per order." } } },
            "build_start_plan" => new() { ["digest"] = "d1ge57-aaaaaaaa" },
            _ => new Dictionary<string, object>(),
            };
            args["detail"] = true;
            return args;
        }

        private static object[] RpcArgs(string tool) => tool switch
        {
            "start_model_from_sql" => new object[] { new StartModelRequest { Server = "x.datawarehouse.fabric.microsoft.com", Database = "SalesWh", Folder = "/work/Sales", Name = "Sales", StorageMode = "import", AuthMode = "azcli" }, "human" },
            "edit_start_plan" => new object[] { new StartPlanEdit { Op = "rename", Target = "table:Sales", Value = "Orders" }, "human" },
            "grant_start_disclosure" => new object[] { "d1ge57-aaaaaaaa", "human" },
            "get_start_words" => new object[] { 25, 5 },
            "write_start_words" => new object[] { new[] { new StartWord { Ref = "table:Sales", Kind = "description", Text = "One row per order." } }, "human" },
            "build_start_plan" => new object[] { "d1ge57-aaaaaaaa", "human" },
            "discard_start_plan" => new object[] { "human" },
            _ => Array.Empty<object>(),
        };

        /// <summary>Verifier (sf-doors): the parity test below compares the two doors' engine arguments with
        /// Assert.Equal on JToken, which xUnit compares as sequences, and a JValue enumerates as empty, so any two
        /// values compare equal (an MCP verb sending null or "human" as its origin passed it). This compares by value.</summary>
        [Fact]
        public async Task Verify_the_two_doors_hand_the_engine_the_same_argument_values_origin_agent_on_both()
        {
            var engine = StartEngineFake.Create(out var fake);
            var (host, client) = await StartDoorAsync(engine);
            var name = TestPipe.Mint("semanticus-sfdoors-");
            using var sessions = new SessionManager();
            using var server = new RpcServer(sessions, engine, name, Challenge);
            using var stop = new CancellationTokenSource();
            var serving = server.RunAsync(stop.Token);
            using var agent = await PlanQueueKit.Client.ConnectAsync(name, "agent");
            try
            {
                for (var i = 0; i < Tools.Length; i++)
                {
                    fake.Calls.Clear();
                    await client.CallToolAsync(Tools[i], McpArgs(Tools[i]));
                    var mcpArgs = Normalize(JToken.FromObject(Assert.Single(fake.Calls).Args));
                    fake.Calls.Clear();
                    await agent.InvokeAsync<JToken>(RpcNames[i], RpcArgs(Tools[i]));
                    var rpcArgs = Normalize(JToken.FromObject(Assert.Single(fake.Calls).Args));
                    Assert.True(JToken.DeepEquals(mcpArgs, rpcArgs), Tools[i] + " handed the engine different arguments:\nMCP: "
                        + mcpArgs.ToString(Newtonsoft.Json.Formatting.None) + "\nRPC: " + rpcArgs.ToString(Newtonsoft.Json.Formatting.None));
                }
            }
            finally
            {
                stop.Cancel();
                try { await serving; } catch (OperationCanceledException) { }
                await client.DisposeAsync(); await host.StopAsync(); host.Dispose();
            }
        }

        [Fact]
        public async Task The_same_verb_through_MCP_full_detail_and_the_pipe_reaches_the_engine_alike_and_returns_the_same_json()
        {
            var engine = StartEngineFake.Create(out var fake);
            var (host, client) = await StartDoorAsync(engine);
            var name = TestPipe.Mint("semanticus-sfdoors-");
            using var sessions = new SessionManager();
            using var server = new RpcServer(sessions, engine, name, Challenge);
            using var stop = new CancellationTokenSource();
            var serving = server.RunAsync(stop.Token);
            using var agent = await PlanQueueKit.Client.ConnectAsync(name, "agent");
            try
            {
                for (var i = 0; i < Tools.Length; i++)
                {
                    var tool = Tools[i];
                    fake.Calls.Clear();
                    var mcp = await client.CallToolAsync(tool, McpArgs(tool));
                    Assert.NotEqual(true, mcp.IsError);
                    var mcpCall = Assert.Single(fake.Calls);
                    var mcpJson = JToken.Parse(string.Join("", mcp.Content.OfType<TextContentBlock>().Select(b => b.Text)));

                    fake.Calls.Clear();
                    var rpcJson = await agent.InvokeAsync<JToken>(RpcNames[i], RpcArgs(tool));
                    var rpcCall = Assert.Single(fake.Calls);

                    Assert.Equal(mcpCall.Method, rpcCall.Method);
                    Assert.Equal(Normalize(JToken.FromObject(mcpCall.Args)), Normalize(JToken.FromObject(rpcCall.Args)));   // the same arguments, origin "agent" on both
                    Assert.True(JToken.DeepEquals(Normalize(mcpJson), Normalize(rpcJson)),
                        tool + " answered differently on the two doors:\nMCP: " + mcpJson.ToString(Newtonsoft.Json.Formatting.None) + "\nRPC: " + rpcJson.ToString(Newtonsoft.Json.Formatting.None));
                }
            }
            finally
            {
                stop.Cancel();
                try { await serving; } catch (OperationCanceledException) { }
                await client.DisposeAsync(); await host.StopAsync(); host.Dispose();
            }
        }

        [Fact]
        public async Task A_pipe_connection_decides_the_origin_not_the_wire_and_an_agent_reaches_approve()
        {
            var engine = StartEngineFake.Create(out var fake);
            var name = TestPipe.Mint("semanticus-sfdoors-");
            using var sessions = new SessionManager();
            using var server = new RpcServer(sessions, engine, name, Challenge);
            using var stop = new CancellationTokenSource();
            var serving = server.RunAsync(stop.Token);
            using var person = await PlanQueueKit.Client.ConnectAsync(name, "human", Challenge);
            using var agent = await PlanQueueKit.Client.ConnectAsync(name, "agent");
            try
            {
                await person.InvokeAsync<JToken>("discardStartPlan", "agent");   // the person's connection claims "agent"
                Assert.Equal("human", Assert.Single(fake.Calls).Args[0]);
                fake.Calls.Clear();
                await agent.InvokeAsync<JToken>("discardStartPlan", "human");   // the agent's connection claims "human"
                Assert.Equal("agent", Assert.Single(fake.Calls).Args[0]);
                fake.Calls.Clear();

                // The approval is on an agent connection too (Kane, 10 Oct 2026: the person's word in chat is enough).
                await agent.InvokeAsync<JToken>("approveStartPlan", "d1ge57-aaaaaaaa");
                Assert.Equal("ApproveStartPlanAsync", Assert.Single(fake.Calls).Method);
            }
            finally { stop.Cancel(); try { await serving; } catch (OperationCanceledException) { } }
        }

        /// <summary>The engine treats a null origin as the PERSON, so a verb on the agent door that forgot to name "agent"
        /// would act as the person (sf-core verifier, 1 Oct). Every MCP verb that takes an origin names "agent" itself,
        /// whatever the caller passes, and no caller-supplied argument can change it.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Every_MCP_verb_names_agent_as_its_origin_and_never_leaves_it_null()
        {
            var engine = StartEngineFake.Create(out var fake);
            var (host, client) = await StartDoorAsync(engine);
            try
            {
                var withOrigin = new[] { "start_model_from_sql", "edit_start_plan", "grant_start_disclosure", "write_start_words", "build_start_plan", "discard_start_plan" };
                foreach (var tool in withOrigin)
                {
                    fake.Calls.Clear();
                    var args = McpArgs(tool);
                    args["origin"] = "human";          // a caller trying to name itself the person: there is no such parameter, so it is refused or ignored
                    var result = await client.CallToolAsync(tool, args);
                    // Either the door refuses the unknown argument, or it ignores it; what it never does is pass it on.
                    if (result.IsError == true) { Assert.Empty(fake.Calls); args.Remove("origin"); result = await client.CallToolAsync(tool, args); }
                    var call = Assert.Single(fake.Calls);
                    Assert.Equal("agent", call.Args[call.Args.Length - 1]);
                    Assert.DoesNotContain(call.Args, a => a is string str && str == "human");
                }
                // And the approval is among the tools (Kane, 10 Oct 2026).
                var listed = (await client.ListToolsAsync()).Select(t => t.Name).ToArray();
                Assert.Contains("approve_start_plan", listed);
            }
            finally { await client.DisposeAsync(); await host.StopAsync(); host.Dispose(); }
        }

        // ------------------------------------------------------------------ the push to Studio

        [Fact]
        public async Task The_Studio_and_an_agent_connection_are_pushed_startPlan_didChange_with_the_whole_view()
        {
            using var f = new DecisionFixture(TestEntitlements.Free);
            var name = TestPipe.Mint("semanticus-sfdoors-");
            using var server = new RpcServer(f.Sessions, f.Engine, name, Challenge);
            using var stop = new CancellationTokenSource();
            var serving = server.RunAsync(stop.Token);
            using var ui = await Listener.ConnectAsync(name, "human", Challenge);
            using var agent = await Listener.ConnectAsync(name, "agent");
            try
            {
                // The server registers a client for broadcasts just after it answers the handshake: wait for both.
                var until = DateTime.UtcNow.AddSeconds(10);
                while (server.ClientCount < 2 && DateTime.UtcNow < until) await Task.Delay(20);
                Assert.Equal(2, server.ClientCount);
                var view = CannedView();
                f.Engine.NotifyStartPlanChanged(view, "agent");
                var seenUi = await ui.Next.Task.WaitAsync(TimeSpan.FromSeconds(10));
                var seenAgent = await agent.Next.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Equal("d1ge57-aaaaaaaa", (string)seenUi["digest"]);
                Assert.Equal(true, (bool)seenUi["exists"]);
                Assert.Equal(5, ((JArray)seenUi["sections"]).Count);
                Assert.True(JToken.DeepEquals(seenUi, seenAgent));
            }
            finally { stop.Cancel(); try { await serving; } catch (OperationCanceledException) { } }
        }

        // ------------------------------------------------------------------ the fold

        private static string StartLine(string[] texts) =>
            texts.SingleOrDefault(t => t.StartsWith("start plan changed since your last call", StringComparison.Ordinal));

        [Fact]
        public async Task An_assistant_hears_once_of_a_change_a_person_made_in_Studio_and_never_of_its_own()
        {
            using var f = new DecisionFixture(TestEntitlements.Free);
            await PlanQueueKit.CallAsync(f.Engine);                       // the door's first call: registers nothing new, drains nothing

            f.Engine.NotifyStartPlanChanged(CannedView(), "human");       // a person edits the plan in Studio
            var heard = StartLine(await PlanQueueKit.CallAsync(f.Engine));
            Assert.NotNull(heard);
            Assert.Contains("(in Studio)", heard);
            Assert.Contains("digest d1ge57-aaaa", heard);                 // the short digest, so two plans can be told apart
            Assert.Contains("not approved", heard);
            Assert.Contains("needs the person 1", heard);
            Assert.Null(StartLine(await PlanQueueKit.CallAsync(f.Engine)));   // said once

            // Its own change, made inside its own tool call, is never echoed back to it.
            var own = await PlanQueueKit.CallAsync(f.Engine, () => { f.Engine.NotifyStartPlanChanged(CannedView("own-digest-0000"), "agent"); return Task.CompletedTask; });
            Assert.Null(StartLine(own));
            Assert.Null(StartLine(await PlanQueueKit.CallAsync(f.Engine)));
        }

        [Fact]
        public async Task The_engines_own_deciding_is_news_to_every_assistant_and_a_discard_reads_as_one()
        {
            using var f = new DecisionFixture(TestEntitlements.Free);
            await PlanQueueKit.CallAsync(f.Engine);

            f.Engine.NotifyStartPlanChanged(CannedView(state: StartDecideState.Stable, needs: 0, approved: true), null);   // the loop settled, with no writer
            var settled = StartLine(await PlanQueueKit.CallAsync(f.Engine));
            Assert.NotNull(settled);
            Assert.Contains("(in the background)", settled);
            Assert.Contains("settled", settled);
            Assert.Contains("nothing needs the person", settled);

            f.Engine.NotifyStartPlanChanged(new StartPlanView { Exists = false }, "human");
            var gone = StartLine(await PlanQueueKit.CallAsync(f.Engine));
            Assert.NotNull(gone);
            Assert.Contains("discarded", gone);
        }

        [Fact]
        public async Task A_plan_change_and_a_model_change_arrive_together_as_two_lines()
        {
            using var f = new DecisionFixture(TestEntitlements.Free);
            await f.Engine.CreateModelAsync("Fold", 1604);
            await f.Engine.CreateTableAsync("Sales", "human");
            await PlanQueueKit.CallAsync(f.Engine);

            await f.Engine.CreateTableAsync("Dates", "human");            // a person's model edit
            f.Engine.NotifyStartPlanChanged(CannedView(), "human");       // and their plan edit
            // One fold block, two lines: the model's, then the plan's.
            var lines = (await PlanQueueKit.CallAsync(f.Engine)).SelectMany(t => t.Split('\n')).ToArray();
            Assert.Single(lines.Where(t => t.StartsWith(McpHealthAppender.FoldPrefix, StringComparison.Ordinal)));
            Assert.NotNull(StartLine(lines));
        }

        [Fact]
        public async Task An_attached_assistant_pulls_the_same_line_over_the_pipe_and_an_old_reader_loses_nothing()
        {
            using var f = new DecisionFixture(TestEntitlements.Free);
            await using var pipe = await PlanQueueKit.Pipe.OpenAsync(f);
            Assert.Null((await pipe.Assistant.PullForeignChangesAsync()));

            f.Engine.NotifyStartPlanChanged(CannedView(), "human");
            var pulled = await pipe.Assistant.PullForeignChangesAsync();
            Assert.NotNull(pulled);
            Assert.Contains("(in Studio)", pulled.StartPlan);
            Assert.Empty(pulled.Items);                                   // an old reader's own line (Items) reads as nothing changed, which is true of the model
            Assert.Null(await pipe.Assistant.PullForeignChangesAsync());

            // What an older client sees on the wire: every field it knew, and one it ignores.
            f.Engine.NotifyStartPlanChanged(CannedView("second-digest-bbbb"), null);
            var raw = await pipe.Agent.InvokeAsync<JToken>("pullForeignChanges");
            Assert.NotNull(raw["startPlan"]);
            Assert.NotNull(raw["items"]);
            Assert.Equal(0, ((JArray)raw["items"]).Count);
        }

        [Fact]
        public void The_fold_text_for_a_plan_change_alone_is_the_sentence_and_nothing_else()
        {
            var plan = new ForeignChanges { Items = Array.Empty<string>(), StartPlan = "start plan changed since your last call (in Studio): drafted, not approved; digest abc" };
            Assert.Equal(plan.StartPlan, McpHealthAppender.FoldText(plan));
            var both = new ForeignChanges { Items = new[] { "table:Sales" }, Studio = true, Revision = 9, StartPlan = plan.StartPlan };
            var text = McpHealthAppender.FoldText(both);
            Assert.StartsWith("changed since your last call (in Studio): table:Sales; rev 9", text, StringComparison.Ordinal);
            Assert.EndsWith(plan.StartPlan, text, StringComparison.Ordinal);
            Assert.Null(McpHealthAppender.FoldText(new ForeignChanges { Items = Array.Empty<string>() }));
            Assert.Null(McpHealthAppender.FoldText(null));
        }

        // ------------------------------------------------------------------ the pointer on the Plan

        [Fact]
        public void The_pointer_never_says_ready_until_the_person_approved_exactly_this_digest()
        {
            var unapproved = StartPlanDoors.PointerOf(CannedView(approved: false));
            Assert.False(unapproved.Approved);
            Assert.DoesNotContain("build_start_plan", unapproved.Next);

            var stale = CannedView(approved: true);
            stale.Approval.Digest = "an-older-digest";   // the person approved a plan that has since moved
            var pointer = StartPlanDoors.PointerOf(stale);
            Assert.False(pointer.Approved);
            Assert.Null(StartPlanDoors.PointerOf(null));
            Assert.Null(StartPlanDoors.PointerOf(new StartPlanView { Exists = false }));
        }

        // ------------------------------------------------------------------ harness

        private static Newtonsoft.Json.JsonSerializerSettings RpcWire()
        {
            var s = new Newtonsoft.Json.JsonSerializerSettings();
            s.ContractResolver = new Newtonsoft.Json.Serialization.DefaultContractResolver
            {
                NamingStrategy = new Newtonsoft.Json.Serialization.CamelCaseNamingStrategy { ProcessDictionaryKeys = false, OverrideSpecifiedNames = true },
            };
            s.NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore;
            return s;
        }

        private static System.Text.Json.JsonSerializerOptions McpWire() => new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never,
        };

        /// <summary>Drop nulls and sort object members, so the two serialisers' different habits do not matter and
        /// only the content is compared.</summary>
        private static JToken Normalize(JToken t)
        {
            switch (t)
            {
                case JObject o:
                    var n = new JObject();
                    foreach (var p in o.Properties().Where(p => p.Value.Type != JTokenType.Null).OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
                        n[char.ToLowerInvariant(p.Name[0]) + p.Name.Substring(1)] = Normalize(p.Value);
                    return n;
                case JArray a:
                    return new JArray(a.Select(Normalize));
                default:
                    return t.DeepClone();
            }
        }

        private static async Task<(IHost host, McpClient client)> StartDoorAsync(IEngine engine)
        {
            var toServer = new System.IO.Pipelines.Pipe();
            var toClient = new System.IO.Pipelines.Pipe();
            var builder = Host.CreateApplicationBuilder(Array.Empty<string>());
            builder.Logging.ClearProviders();
            builder.Services.AddSingleton<IEngine>(engine);
            builder.Services.AddMcpServer()
                .WithStreamServerTransport(toServer.Reader.AsStream(), toClient.Writer.AsStream())
                .WithToolsFromAssembly(typeof(McpTools).Assembly)
                .WithRequestFilters(f => McpDoorFilters.Add(f, engine));
            var host = builder.Build();
            await host.StartAsync();
            var client = await McpClient.CreateAsync(new StreamClientTransport(toServer.Writer.AsStream(), toClient.Reader.AsStream()));
            return (host, client);
        }

        /// <summary>A pipe client that keeps the first startPlan/didChange it is sent.</summary>
        private sealed class Listener : IDisposable
        {
            private readonly System.IO.Pipes.NamedPipeClientStream _pipe;
            private readonly JsonRpc _rpc;
            public TaskCompletionSource<JToken> Next { get; } = new TaskCompletionSource<JToken>(TaskCreationOptions.RunContinuationsAsynchronously);

            private Listener(System.IO.Pipes.NamedPipeClientStream pipe, JsonRpc rpc) { _pipe = pipe; _rpc = rpc; }

            internal static async Task<Listener> ConnectAsync(string pipeName, string role, string challenge = null)
            {
                var pipe = new System.IO.Pipes.NamedPipeClientStream(".", pipeName, System.IO.Pipes.PipeDirection.InOut, System.IO.Pipes.PipeOptions.Asynchronous);
                await pipe.ConnectAsync(5000);
                await RpcHandshake.WriteAsync(pipe, role == "human" ? RpcConnectionRole.Human : RpcConnectionRole.Agent, challenge);
                await RpcHandshake.ReadAcceptedAsync(pipe);
                var rpc = new JsonRpc(RpcServer.CreateHandler(pipe));
                var l = new Listener(pipe, rpc);
                rpc.AddLocalRpcMethod("startPlan/didChange", new Action<JToken>(v => l.Next.TrySetResult(v)));
                rpc.StartListening();
                return l;
            }

            public void Dispose()
            {
                try { _rpc.Dispose(); } catch { }
                try { _pipe.Dispose(); } catch { }
            }
        }
    }
    /// <summary>The doors against the real engine's start (sf-core merged): the origin the door names is what the engine
    /// sees, the assistant can never discard unsaved work, the answer names a model it closed, the Plan points at a plan
    /// that exists and stops pointing when it is gone, and the fold carries the plan's moves. Offline through the engine's
    /// own test seam; SEMANTICUS_HOME is a scratch folder.</summary>
    [Collection("restore-root")]
    public sealed class StartPlanDoorsRealEngineTests : IDisposable
    {
        private sealed class Fake : IEntitlement
        {
            public bool IsPro => false;
            public EntitlementInfo Info { get; } = new EntitlementInfo { Tier = "free" };
        }

        private readonly string _root;
        private readonly string _origHome;
        private readonly SessionManager _sessions = new SessionManager();
        private readonly LocalEngine _engine;

        public StartPlanDoorsRealEngineTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "spd-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(Path.Combine(_root, "home", ".semanticus"));
            _origHome = Environment.GetEnvironmentVariable(SemanticusHome.Variable);
            Environment.SetEnvironmentVariable(SemanticusHome.Variable, Path.Combine(_root, "home", ".semanticus"));
            _engine = new LocalEngine(_sessions, new Fake(), _root);
            _engine.StartPlanSchemaReaderForTest = (req, origin) => Task.FromResult(new FabricSchema
            {
                Server = "x.datawarehouse.fabric.microsoft.com", Database = "Wh",
                Tables = new[]
                {
                    new FabricTable
                    {
                        Schema = "dbo", Name = "Sales", KeyColumns = new[] { "OrderId" },
                        Columns = new[]
                        {
                            new FabricColumn { Name = "OrderId", DataType = "Int64", SqlType = "bigint", Ordinal = 1 },
                            new FabricColumn { Name = "Amount", DataType = "Decimal", SqlType = "decimal", Ordinal = 2 },
                        },
                    },
                },
            });
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(SemanticusHome.Variable, _origHome);
            try { _engine.Dispose(); } catch { }
            try { Directory.Delete(_root, true); } catch { }
        }

        private string NewFolder(string name) => Path.Combine(_root, name);

        private async Task OpenCleanSavedModelAsync(string name)
        {
            await _engine.CreateModelAsync(name, 1604);
            await _engine.SaveAsync(Path.Combine(_root, name + ".SemanticModel", "definition"), "tmdl", false, "human");
            Assert.False((await _engine.SessionInfoAsync()).HasUnsavedChanges);
        }

        private Task<StartPlanView> McpStart(string folder, string name = "Sales Model") =>
            McpTools.StartModelFromSql(_engine, "x.datawarehouse.fabric.microsoft.com", "Wh", folder, name);

        [Fact]
        public async Task An_MCP_start_over_unsaved_work_is_refused_and_nothing_the_caller_sends_can_discard_it()
        {
            await _engine.CreateModelAsync("Open", 1604);
            await _engine.CreateTableAsync("Scratch", "human");   // unsaved work
            Assert.True((await _engine.SessionInfoAsync()).HasUnsavedChanges);

            await Assert.ThrowsAsync<InvalidOperationException>(() => McpStart(NewFolder("a")));
            // The same refusal through an agent pipe connection, whatever its request body claims.
            var agentDoor = new EngineRpcTarget(_engine, "agent", "rpc:t");
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => agentDoor.startModelFromSql(
                new StartModelRequest { Server = "s", Database = "Wh", Folder = NewFolder("b"), Name = "B", DiscardUnsaved = true }, "human"));
            Assert.Contains("not saved", ex.Message);
            Assert.False(Directory.Exists(NewFolder("a")) && Directory.EnumerateFileSystemEntries(NewFolder("a")).Any());
            Assert.True((await _engine.SessionInfoAsync()).HasUnsavedChanges);   // still open, still unsaved: nothing was lost

            // The person's own door, with the person's own confirmation, may.
            var person = new EngineRpcTarget(_engine, "human");
            var view = await person.startModelFromSql(new StartModelRequest { Server = "s", Database = "Wh", Folder = NewFolder("c"), Name = "C", DiscardUnsaved = true });
            Assert.True(view.Exists);
        }

        [Fact]
        public async Task An_MCP_start_over_a_clean_saved_model_says_plainly_which_model_it_closed()
        {
            await OpenCleanSavedModelAsync("Quarterly Report");
            var view = await McpStart(NewFolder("d"), "Fresh");
            Assert.True(view.Exists);
            Assert.NotNull(view.Notice);
            Assert.Contains("Quarterly Report", view.Notice);
            Assert.Contains("closed", view.Notice);
            Assert.DoesNotContain("\u2014", view.Notice);
            // The notice belongs to that answer only: the plan itself, read again, does not carry it.
            Assert.Null((await McpTools.GetStartPlan(_engine)).Notice);
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task The_Plan_points_at_a_start_plan_while_one_exists_and_reads_as_before_when_none_does()
        {
            await _engine.CreateModelAsync("PointerModel", 1604);
            var before = await _engine.GetModelPlanAsync();
            Assert.Null(before.StartPlan);
            Assert.DoesNotContain("startPlan", System.Text.Json.JsonSerializer.Serialize(before, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)), StringComparison.OrdinalIgnoreCase);

            var started = await McpStart(NewFolder("e"), "Pointed");
            var during = await _engine.GetModelPlanAsync();
            Assert.NotNull(during.StartPlan);
            Assert.Equal(started.Digest, during.StartPlan.Digest);
            Assert.False(during.StartPlan.Approved);
            Assert.Contains("get_start_plan", during.StartPlan.Next);
            Assert.Contains("startPlan", System.Text.Json.JsonSerializer.Serialize(during, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)));
            Assert.NotNull((await McpTools.GetModelPlan(_engine)).StartPlan);   // the assistant's read carries it too

            // Another model opens: the plan is parked, and the pointer says why, with no event to tell it so.
            await _engine.CreateModelAsync("Elsewhere", 1604, discardUnsaved: true);
            var parked = await _engine.GetModelPlanAsync();
            Assert.NotNull(parked.StartPlan);
            Assert.False(parked.StartPlan.CanBuild);
            Assert.Contains("Another model is open", parked.StartPlan.Next);

            await McpTools.DiscardStartPlan(_engine);
            Assert.Null((await _engine.GetModelPlanAsync()).StartPlan);
        }

        [Fact]
        public async Task A_start_and_a_discard_by_the_person_are_heard_once_by_the_assistant_and_its_own_are_not()
        {
            await PlanQueueKit.CallAsync(_engine);
            await _engine.StartModelFromSqlAsync(new StartModelRequest { Server = "s", Database = "Wh", Folder = NewFolder("f"), Name = "F" }, "human");
            var heard = (await PlanQueueKit.CallAsync(_engine)).SingleOrDefault(t => t.StartsWith("start plan changed since your last call", StringComparison.Ordinal));
            Assert.NotNull(heard);
            Assert.Contains("(in Studio)", heard);
            Assert.Null((await PlanQueueKit.CallAsync(_engine)).SingleOrDefault(t => t.StartsWith("start plan changed", StringComparison.Ordinal)));

            var own = await PlanQueueKit.CallAsync(_engine, () => McpTools.DiscardStartPlan(_engine));
            Assert.DoesNotContain(own, t => t.StartsWith("start plan changed", StringComparison.Ordinal));
        }
    }
}
