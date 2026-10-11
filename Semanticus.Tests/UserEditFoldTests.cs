using System;
using System.Collections.Generic;
using System.IO.Pipes;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol.Protocol;
using Semanticus.Engine;
using Semanticus.Engine.Entitlement;
using StreamJsonRpc;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>F-246 (the prompt audit, 24 Sep): a person's edits in Studio never reached the assistant. Golden rule 2
    /// and the shipped skill promised the next tool result would carry them; nothing did, so the assistant kept acting
    /// on a stale picture. Now every MCP tool result gets ONE terse line when commits from anyone other than this
    /// assistant landed since its previous call, on both the owner path (the MCP process owns the model) and the
    /// attached path (it reaches a running engine over the pipe). Nothing changed means no line.</summary>
    public sealed class UserEditFoldTests
    {
        private const string Challenge = "user-edit-fold-test-challenge-0123456789abcdef";

        private sealed class Free : IEntitlement
        {
            public bool IsPro => false;
            public EntitlementInfo Info { get; } = new EntitlementInfo { Tier = "free" };
        }

        private static async Task<(LocalEngine engine, SessionManager sm)> FreshAsync()
        {
            var sm = new SessionManager();
            var engine = new LocalEngine(sm, new Free());
            await engine.CreateModelAsync("FoldTest", 1604);
            var t = await engine.CreateTableAsync("Sales", "human");
            await engine.CreateColumnAsync(t, "Amount", "Decimal", "Amount", "human");
            await engine.CreateMeasureAsync(t, "Total Amount", "SUM(Sales[Amount])", "human");
            return (engine, sm);
        }

        /// <summary>One MCP tool call exactly as Program.Mcp runs it, with the body the test supplies.</summary>
        private static async Task<string[]> CallAsync(IEngine engine, Func<Task> body = null)
        {
            var result = await McpHealthAppender.InvokeAsync(engine, async () =>
            {
                if (body != null) await body();
                return new CallToolResult { Content = new List<ContentBlock> { new TextContentBlock { Text = "ok" } } };
            });
            return result.Content.OfType<TextContentBlock>().Select(b => b.Text).ToArray();
        }

        private static string FoldLine(string[] texts) =>
            texts.SingleOrDefault(t => t.StartsWith(McpHealthAppender.FoldPrefix, StringComparison.Ordinal));

        [Fact]
        public async Task Owner_path_a_studio_edit_between_two_calls_shows_up_in_the_second_call()
        {
            var (engine, sm) = await FreshAsync();
            using (sm)
            {
                await CallAsync(engine);                                   // the assistant's first look
                await engine.SetDescriptionAsync("measure:Sales/Total Amount", "Summed sales.", "human");
                var line = FoldLine(await CallAsync(engine));
                Assert.NotNull(line);
                Assert.Equal($"changed since your last call (in Studio): measure:Sales/Total Amount; rev {sm.Current.Revision}", line);
                Assert.Null(FoldLine(await CallAsync(engine)));           // take-once: the third call is quiet
            }
        }

        // F-247 (Sol 6, slice 6 review): the journal kept commits from the model that was open before, so the first
        // tool result on a newly opened model could name edits made to the old one. It must name only this model's.
        [Fact]
        public async Task After_a_model_swap_the_first_call_names_no_edits_from_the_previous_model()
        {
            var (engine, sm) = await FreshAsync();
            using (sm)
            {
                await CallAsync(engine);                                   // the assistant's look at model A
                await engine.SetDescriptionAsync("measure:Sales/Total Amount", "Edited on model A.", "human");
                await engine.CreateMeasureAsync("table:Sales", "Only On A", "1", "human");

                await engine.CreateModelAsync("ModelB", 1604);             // the open model changes to B
                var first = FoldLine(await CallAsync(engine));
                Assert.Null(first);

                // B's own edits still reach the assistant as usual.
                await engine.CreateTableAsync("Orders", "human");
                var line = FoldLine(await CallAsync(engine));
                Assert.NotNull(line);
                Assert.StartsWith("changed since your last call (in Studio): ", line);
                Assert.Contains("Orders", line);
                Assert.DoesNotContain("Total Amount", line);
                Assert.DoesNotContain("Only On A", line);
            }
        }

        [Fact]
        public async Task No_line_when_nothing_changed()
        {
            var (engine, sm) = await FreshAsync();
            using (sm)
            {
                await CallAsync(engine);
                Assert.Null(FoldLine(await CallAsync(engine)));
                Assert.Null(FoldLine(await CallAsync(engine)));
            }
        }

        [Fact]
        public async Task The_assistants_own_edits_are_not_echoed_back()
        {
            var (engine, sm) = await FreshAsync();
            using (sm)
            {
                await CallAsync(engine);
                var own = await CallAsync(engine, () => engine.CreateMeasureAsync("table:Sales", "Mine", "1", "agent"));
                Assert.Null(FoldLine(own));
                Assert.Null(FoldLine(await CallAsync(engine)));
            }
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task The_line_names_at_most_twelve_objects_then_says_how_many_more()
        {
            var (engine, sm) = await FreshAsync();
            using (sm)
            {
                await CallAsync(engine);
                for (var i = 1; i <= 15; i++)
                    await engine.CreateMeasureAsync("table:Sales", "M" + i.ToString("00"), "1", "human");
                var line = FoldLine(await CallAsync(engine));
                Assert.NotNull(line);
                Assert.Contains("measure:Sales/M01", line);
                Assert.Contains("measure:Sales/M12", line);
                Assert.DoesNotContain("measure:Sales/M13", line);
                Assert.Contains(", and 3 more; rev ", line);
            }
        }

        [Fact]
        public async Task A_person_undoing_the_assistants_edit_counts_as_a_change()
        {
            var (engine, sm) = await FreshAsync();
            using (sm)
            {
                await CallAsync(engine);
                await CallAsync(engine, () => engine.SetDescriptionAsync("measure:Sales/Total Amount", "By the assistant.", "agent"));
                await engine.UndoAsync("human");
                var line = FoldLine(await CallAsync(engine));
                Assert.NotNull(line);
                Assert.StartsWith("changed since your last call (in Studio): undo", line);
                Assert.Contains("measure:Sales/Total Amount", line);

                await engine.RedoAsync("human");
                var redo = FoldLine(await CallAsync(engine));
                Assert.NotNull(redo);
                Assert.StartsWith("changed since your last call (in Studio): redo", redo);
            }
        }

        [Fact]
        public async Task Attached_path_studio_edits_reach_the_assistant_and_its_own_edits_do_not_echo()
        {
            var (engine, sm) = await FreshAsync();
            var pipeName = TestPipe.Mint("semanticus-fold-");
            using var server = new RpcServer(sm, engine, pipeName, Challenge);
            using var stop = new CancellationTokenSource();
            var serving = server.RunAsync(stop.Token);
            RpcClient studio = null;
            RemoteEngine assistant = null, other = null;
            try
            {
                studio = await RpcClient.ConnectAsync(pipeName, "human", Challenge);
                assistant = await RemoteEngine.ConnectAsync(pipeName);
                other = await RemoteEngine.ConnectAsync(pipeName);

                await CallAsync(assistant);
                Assert.Null(FoldLine(await CallAsync(assistant)));                  // nothing changed: no line

                // The assistant's own edit crosses the pipe on its own connection: never echoed.
                Assert.Null(FoldLine(await CallAsync(assistant, () => assistant.CreateMeasureAsync("table:Sales", "Mine", "1", "agent"))));
                Assert.Null(FoldLine(await CallAsync(assistant)));

                // A person's edit in Studio, between two calls.
                await studio.InvokeAsync<SetResult>("setDescription", "measure:Sales/Mine", "Named by a person.");
                var line = FoldLine(await CallAsync(assistant));
                Assert.Equal($"changed since your last call (in Studio): measure:Sales/Mine; rev {sm.Current.Revision}", line);

                // A person's undo, too.
                await studio.InvokeAsync<UndoState>("undo");
                var undo = FoldLine(await CallAsync(assistant));
                Assert.NotNull(undo);
                Assert.StartsWith("changed since your last call (in Studio): undo", undo);

                // A second assistant on the same engine is named as such.
                await other.CreateMeasureAsync("table:Sales", "Theirs", "2", "agent");
                var theirs = FoldLine(await CallAsync(assistant));
                Assert.Equal($"changed since your last call (by another assistant): measure:Sales/Theirs; rev {sm.Current.Revision}", theirs);
            }
            finally
            {
                (assistant as IDisposable)?.Dispose();
                (other as IDisposable)?.Dispose();
                studio?.Dispose();
                stop.Cancel();
                try { await serving; } catch (OperationCanceledException) { }
                sm.Dispose();
            }
        }

        private sealed class RpcClient : IDisposable
        {
            private readonly NamedPipeClientStream _pipe;
            private readonly JsonRpc _rpc;

            private RpcClient(NamedPipeClientStream pipe, JsonRpc rpc) { _pipe = pipe; _rpc = rpc; }

            internal static async Task<RpcClient> ConnectAsync(string pipeName, string role, string challenge = null)
            {
                var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                await pipe.ConnectAsync(5000);
                await RpcHandshake.WriteAsync(pipe, role == "human" ? RpcConnectionRole.Human : RpcConnectionRole.Agent, challenge);
                await RpcHandshake.ReadAcceptedAsync(pipe);
                var rpc = new JsonRpc(RpcServer.CreateHandler(pipe));
                rpc.StartListening();
                return new RpcClient(pipe, rpc);
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
