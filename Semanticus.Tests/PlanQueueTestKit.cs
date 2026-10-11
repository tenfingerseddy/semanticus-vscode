// Shared fixtures for the queue tests (Plan at the heart, lane E3): a small gappy model saved so it has a durable
// identity, a task added the way the person adds one, one MCP tool call exactly as the door runs it, and a live
// pipe with a person, a bare agent connection and an attached assistant (RemoteEngine) on one engine.

using System;
using System.Collections.Generic;
using System.IO.Pipes;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol.Protocol;
using Semanticus.Engine;
using StreamJsonRpc;
using Xunit;

namespace Semanticus.Tests
{
    internal static class PlanQueueKit
    {
        internal const string Challenge = "plan-queue-test-challenge-0123456789abcdef";

        /// <summary>The gappy model the E1 tests use, saved so tasks are kept in the Plan's own file.</summary>
        internal static async Task<DecisionFixture> GappyAsync(string file)
        {
            var f = new DecisionFixture(TestEntitlements.Free);
            await f.Engine.CreateModelAsync("Contoso", 1604);
            await f.Engine.CreateTableAsync("Sales", "human");
            await f.Engine.CreateColumnAsync("table:Sales", "Amount", "Double", null, "human");
            await f.Engine.CreateColumnAsync("table:Sales", "CustomerKey", "Int64", null, "human");
            await f.Engine.CreateTableAsync("Customer", "human");
            await f.Engine.CreateColumnAsync("table:Customer", "CustomerKey", "Int64", null, "human");
            await f.Engine.CreateColumnAsync("table:Customer", "City", "String", null, "human");
            await f.Engine.CreateRelationshipAsync("column:Sales/CustomerKey", "column:Customer/CustomerKey", null, true, "human");
            await f.Engine.CreateMeasureAsync("table:Sales", "Total Sales", "SUM ( Sales[Amount] )", "human");
            await f.SaveAsAsync(file);
            f.UseNull();
            return f;
        }

        /// <summary>A task the person adds for their assistant. Objects that name nothing else on the plan make a
        /// task with a known footprint that waits for nothing.</summary>
        internal static async Task<string> AskAsync(IEngine engine, string words, string[] objects = null,
                                                    string doer = null, string doneWhen = null) =>
            (await engine.AddPlanTaskAsync(new PlanTaskRequest { Words = words, Objects = objects, Doer = doer, DoneWhen = doneWhen })).ItemId;

        /// <summary>A ref nothing on the gappy plan reads or writes, so a task naming it waits for nothing. It is still
        /// never called independent: a free-form task's footprint is unknown (Sol 6, slice 7, finding 3).</summary>
        internal static string[] Free(string name) => new[] { "measure:Sales/" + name };

        internal static ModelPlanItem Row(ModelPlanView plan, string id) => Assert.Single(plan.Items.Where(i => i.Id == id));

        /// <summary>An open row Semanticus does, whose known footprint writes an object, and that object.</summary>
        internal static (ModelPlanItem Row, string Object) SemanticusRowWithObject(ModelPlanView plan)
        {
            // Not the best-practice row: it is the plan's last pass and nothing waits for it (PlanFootprint.Waits), so a
            // task on an object it writes (the key its key fix sets) is never held by it.
            foreach (var r in plan.Items.Where(i => i.Doer == PlanDoers.Semanticus && i.FootprintKnown
                                                    && PlanStateWords.IsOpen(i.StateWord) && !PlanTasks.IsTaskId(i.Id)
                                                    && !LocalEngine.IsBpaFixItem(i.Id)))
            {
                var obj = (r.Writes ?? Array.Empty<string>()).FirstOrDefault(w => !w.StartsWith("model:", StringComparison.Ordinal) && w.Contains(':'));
                if (obj != null) return (r, obj);
            }
            throw new Xunit.Sdk.XunitException("the gappy model should carry an open Semanticus row that writes an object");
        }

        /// <summary>One MCP tool call exactly as Program.Mcp runs it, with the body the test supplies. Returns the
        /// result's text blocks.</summary>
        internal static async Task<string[]> CallAsync(IEngine engine, Func<Task> body = null)
        {
            var result = await McpHealthAppender.InvokeAsync(engine, async () =>
            {
                if (body != null) await body();
                return new CallToolResult { Content = new List<ContentBlock> { new TextContentBlock { Text = "ok" } } };
            });
            return result.Content.OfType<TextContentBlock>().Select(b => b.Text).ToArray();
        }

        internal static string PlanLine(string[] texts) =>
            texts.SingleOrDefault(t => t.StartsWith("Plan:", StringComparison.Ordinal));

        /// <summary>A pipe on one engine with a person, a bare agent connection and an attached assistant.</summary>
        internal sealed class Pipe : IAsyncDisposable
        {
            private readonly CancellationTokenSource _stop = new CancellationTokenSource();
            private RpcServer _server;
            private Task _serving;
            internal Client Human { get; private set; }
            internal Client Agent { get; private set; }
            internal RemoteEngine Assistant { get; private set; }

            internal static async Task<Pipe> OpenAsync(DecisionFixture f)
            {
                var name = TestPipe.Mint("semanticus-queue-");
                var p = new Pipe { _server = new RpcServer(f.Sessions, f.Engine, name, Challenge) };
                p._serving = p._server.RunAsync(p._stop.Token);
                p.Human = await Client.ConnectAsync(name, "human", Challenge);
                p.Agent = await Client.ConnectAsync(name, "agent");
                p.Assistant = await RemoteEngine.ConnectAsync(name);
                return p;
            }

            public async ValueTask DisposeAsync()
            {
                Assistant?.Dispose();
                Agent?.Dispose();
                Human?.Dispose();
                _stop.Cancel();
                try { await _serving; } catch (OperationCanceledException) { }
                _server?.Dispose();
            }
        }

        /// <summary>A bare pipe client that speaks the handshake as a person or an agent.</summary>
        internal sealed class Client : IDisposable
        {
            private readonly NamedPipeClientStream _pipe;
            private readonly JsonRpc _rpc;

            private Client(NamedPipeClientStream pipe, JsonRpc rpc) { _pipe = pipe; _rpc = rpc; }

            internal static async Task<Client> ConnectAsync(string pipeName, string role, string challenge = null)
            {
                var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                await pipe.ConnectAsync(5000);
                await RpcHandshake.WriteAsync(pipe, role == "human" ? RpcConnectionRole.Human : RpcConnectionRole.Agent, challenge);
                await RpcHandshake.ReadAcceptedAsync(pipe);
                var rpc = new JsonRpc(RpcServer.CreateHandler(pipe));
                rpc.StartListening();
                return new Client(pipe, rpc);
            }

            internal Task<T> InvokeAsync<T>(string method, params object[] args) => _rpc.InvokeAsync<T>(method, args);

            public void Dispose()
            {
                try { _rpc.Dispose(); } catch { }
                try { _pipe.Dispose(); } catch { }
            }
        }
    }
}
