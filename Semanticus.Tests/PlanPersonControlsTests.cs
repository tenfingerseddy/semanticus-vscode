// The person's Plan controls (Plan at the heart, lane E1; contract sections 3.3, 4 and 12).
//
// What these tests hold, one line each:
//   each control changes only what it names, on a task and on a derived row
//   the doer switch keeps the order and the done-when honest, and never makes Semanticus do words
//   Up moves one task one place among the assistant's tasks and nothing else
//   the switch pauses the offer's wording and nothing else
//   every control is absent from an agent connection: refused by method-not-found over the pipe, through
//   RemoteEngine (the attached assistant's engine), and absent from the MCP surface

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol.Server;
using Semanticus.Engine;
using StreamJsonRpc;
using Xunit;

namespace Semanticus.Tests
{
    public sealed class PlanPersonControlsTests
    {
        private const string Challenge = "plan-controls-test-challenge-0123456789abcdef";

        private static readonly string[] Controls =
            { "addPlanTask", "holdPlanItem", "setPlanItemDoer", "movePlanTaskUp", "setAssistantQueue", "setPlanTaskNote" };

        private static async Task<DecisionFixture> GappyAsync(string file)
        {
            var f = new DecisionFixture(TestEntitlements.Free);
            await f.Engine.CreateModelAsync("Contoso", 1604);
            await f.Engine.CreateTableAsync("Sales", "human");
            await f.Engine.CreateColumnAsync("table:Sales", "Amount", "Double", null, "human");
            await f.Engine.CreateColumnAsync("table:Sales", "CustomerKey", "Int64", null, "human");
            await f.Engine.CreateTableAsync("Customer", "human");
            await f.Engine.CreateColumnAsync("table:Customer", "CustomerKey", "Int64", null, "human");
            await f.Engine.CreateRelationshipAsync("column:Sales/CustomerKey", "column:Customer/CustomerKey", null, true, "human");
            await f.Engine.CreateMeasureAsync("table:Sales", "Total Sales", "SUM ( Sales[Amount] )", "human");
            await f.SaveAsAsync(file);
            f.UseNull();
            return f;
        }

        private static async Task<string> AskAsync(DecisionFixture f, string words, string doer = null) =>
            (await f.Engine.AddPlanTaskAsync(new PlanTaskRequest { Words = words, Doer = doer })).ItemId;

        private static ModelPlanItem Row(ModelPlanView plan, string id) => Assert.Single(plan.Items.Where(i => i.Id == id));

        /// <summary>Every field a person could see, except the ones the named control is allowed to move.</summary>
        private static string Face(ModelPlanItem i, params string[] except)
        {
            var parts = new List<string>();
            foreach (var p in typeof(ModelPlanItem).GetMembers(BindingFlags.Public | BindingFlags.Instance).OfType<PropertyInfo>())
            {
                // The offer number is the fold's numbering of the assistant's Ready rows (contract 6.1): pausing,
                // moving or giving one row renumbers the offer by design. PlanGapsTests holds it.
                if (except.Contains(p.Name) || p.Name == nameof(ModelPlanItem.OfferNumber)) continue;
                var v = p.GetValue(i);
                parts.Add(p.Name + "=" + (v is Array a ? string.Join("|", a.Cast<object>().Select(x => x?.ToString())) : v?.ToString()));
            }
            return string.Join(";", parts);
        }

        private static void OthersUnchanged(ModelPlanView before, ModelPlanView after, string id)
        {
            var b = before.Items.Where(i => i.Id != id).ToDictionary(i => i.Id, i => Face(i));
            var a = after.Items.Where(i => i.Id != id).ToDictionary(i => i.Id, i => Face(i));
            Assert.Equal(b, a);
        }

        /// <summary>A doer switch changes the switched row and, through it, who waits for whom: a person's row waits for
        /// a Semanticus row only while Semanticus does it (5.2, coordinator 24 Sep), and the cycle pass can then settle
        /// a loop through the switched row by plan order. So the other rows' wait fields may move; nothing else of
        /// theirs may.</summary>
        private static void OthersUnchangedButWaits(ModelPlanView before, ModelPlanView after, string id)
        {
            var waitFields = new[] { nameof(ModelPlanItem.WaitsFor), nameof(ModelPlanItem.StateWord), nameof(ModelPlanItem.Sub), nameof(ModelPlanItem.StateNote) };
            var b = before.Items.Where(i => i.Id != id).ToDictionary(i => i.Id, i => Face(i, waitFields));
            var a = after.Items.Where(i => i.Id != id).ToDictionary(i => i.Id, i => Face(i, waitFields));
            Assert.Equal(b, a);
        }

        // ---- each control changes only what it names ------------------------------------------------------

        [Fact]
        public async Task Pause_on_a_task_changes_only_its_held_flag_and_state_word()
        {
            using var f = await GappyAsync("CtlHoldTask.bim");
            var id = await AskAsync(f, "Describe the Sales measures");
            var before = await f.Engine.GetModelPlanAsync();

            var after = await f.Engine.HoldPlanItemAsync(id, true);

            var row = Row(after, id);
            Assert.True(row.Held);
            Assert.Equal(PlanStateWords.Paused, row.StateWord);
            Assert.Equal(Face(Row(before, id), "Held", "StateWord", "StateNote"), Face(row, "Held", "StateWord", "StateNote"));
            OthersUnchanged(before, after, id);

            var resumed = await f.Engine.HoldPlanItemAsync(id, false);
            Assert.Equal(PlanStateWords.Ready, Row(resumed, id).StateWord);
        }

        [Fact]
        public async Task Pause_on_a_derived_row_is_kept_by_its_id_and_survives_a_reopen()
        {
            using var f = await GappyAsync("CtlHoldRow.bim");
            var before = await f.Engine.GetModelPlanAsync();
            var derived = before.Items.First(i => !PlanTasks.IsTaskId(i.Id) && PlanStateWords.IsOpen(i.StateWord));

            var after = await f.Engine.HoldPlanItemAsync(derived.Id, true);
            Assert.Equal(PlanStateWords.Paused, Row(after, derived.Id).StateWord);
            Assert.Equal(Face(derived, "Held", "StateWord", "StateNote"), Face(Row(after, derived.Id), "Held", "StateWord", "StateNote"));
            OthersUnchanged(before, after, derived.Id);

            await f.Engine.CreateModelAsync("Somewhere Else", 1604);
            await f.Engine.OpenAsync(Path.Combine(f.Workspace, "CtlHoldRow.bim"));
            f.UseNull();
            Assert.True(Row(await f.Engine.GetModelPlanAsync(), derived.Id).Held);
        }

        [Fact]
        public async Task A_note_changes_only_the_note_and_empty_clears_it()
        {
            using var f = await GappyAsync("CtlNote.bim");
            var id = await AskAsync(f, "Check the Customer table");
            var before = await f.Engine.GetModelPlanAsync();

            var after = await f.Engine.SetPlanTaskNoteAsync(id, "Only the visible columns.");
            Assert.Equal("Only the visible columns.", Row(after, id).Note);
            Assert.Equal(Face(Row(before, id), "Note"), Face(Row(after, id), "Note"));
            OthersUnchanged(before, after, id);

            Assert.Null(Row(await f.Engine.SetPlanTaskNoteAsync(id, "  "), id).Note);
            await Assert.ThrowsAsync<ArgumentException>(() =>
                f.Engine.SetPlanTaskNoteAsync(id, new string('x', PlanTasks.MaxNoteChars + 1)));
        }

        [Fact]
        public async Task The_doer_switch_moves_the_task_between_you_and_your_assistant()
        {
            using var f = await GappyAsync("CtlDoer.bim");
            var first = await AskAsync(f, "First for the assistant");
            var id = await AskAsync(f, "Second for the assistant");
            var before = await f.Engine.GetModelPlanAsync();
            Assert.Equal(2, Row(before, id).Order);

            var mine = await f.Engine.SetPlanItemDoerAsync(id, "you");
            var row = Row(mine, id);
            Assert.Equal(PlanDoers.You, row.Doer);
            Assert.Null(row.Order);
            Assert.Equal(PlanDoneWhen.YouMark, row.DoneWhen);
            Assert.Equal(PlanStateWords.NeedsYou, row.StateWord);
            Assert.Equal(1, Row(mine, first).Order);

            var back = Row(await f.Engine.SetPlanItemDoerAsync(id, "assistant"), id);
            Assert.Equal(PlanDoers.Assistant, back.Doer);
            Assert.Equal(2, back.Order);                       // back at the end of the order
            Assert.Equal(PlanDoneWhen.AssistantSays, back.DoneWhen);

            // Semanticus does not do words.
            await Assert.ThrowsAsync<ArgumentException>(() => f.Engine.SetPlanItemDoerAsync(id, "semanticus"));
            await Assert.ThrowsAsync<ArgumentException>(() => f.Engine.SetPlanItemDoerAsync(id, "jev"));
        }

        [Fact]
        public async Task The_doer_switch_on_a_derived_row_is_residue_and_let_it_run_only_where_the_engine_can()
        {
            using var f = await GappyAsync("CtlDoerRow.bim");
            var before = await f.Engine.GetModelPlanAsync();
            // A row that waits for nothing: a waiting row reads Waiting whoever its doer is (contract 1.4, lane E2).
            var engineRow = before.Items.FirstOrDefault(i => i.Doer == PlanDoers.Semanticus && PlanStateWords.IsOpen(i.StateWord)
                                                             && (i.WaitsFor?.Length ?? 0) == 0);
            var personRow = before.Items.FirstOrDefault(i => i.Doer == PlanDoers.You && !PlanTasks.IsTaskId(i.Id)
                                                             && PlanStateWords.IsOpen(i.StateWord));
            Assert.True(engineRow != null || personRow != null, "the gappy model should carry a derived open row");

            if (engineRow != null)
            {
                var mine = await f.Engine.SetPlanItemDoerAsync(engineRow.Id, "you");
                Assert.Equal(PlanDoers.You, Row(mine, engineRow.Id).Doer);
                // Now a person's row, it waits for the person's cleanup again: Semanticus rows go first only while Semanticus does them (5.2, coordinator 24 Sep).
                Assert.Equal(PlanStateWords.Waiting, Row(mine, engineRow.Id).StateWord);
                Assert.Equal(engineRow.Who, Row(mine, engineRow.Id).Who);   // the old field keeps its meaning
                OthersUnchangedButWaits(before, mine, engineRow.Id);
                var back = await f.Engine.SetPlanItemDoerAsync(engineRow.Id, "semanticus");
                Assert.Equal(Face(engineRow), Face(Row(back, engineRow.Id)));
            }
            if (personRow != null)
                await Assert.ThrowsAsync<ArgumentException>(() => f.Engine.SetPlanItemDoerAsync(personRow.Id, "semanticus"));
        }

        [Fact]
        public async Task Up_moves_one_task_one_place_and_nothing_else()
        {
            using var f = await GappyAsync("CtlUp.bim");
            var a = await AskAsync(f, "Task A");
            var b = await AskAsync(f, "Task B");
            var c = await AskAsync(f, "Task C");
            var mine = await AskAsync(f, "Mine", doer: "you");
            var before = await f.Engine.GetModelPlanAsync();

            var after = await f.Engine.MovePlanTaskUpAsync(c);
            Assert.Equal(1, Row(after, a).Order);
            Assert.Equal(2, Row(after, c).Order);
            Assert.Equal(3, Row(after, b).Order);
            Assert.Equal(Face(Row(before, a)), Face(Row(after, a)));
            Assert.Equal(Face(Row(before, mine)), Face(Row(after, mine)));

            // The first stays first, and only an assistant's task has a place.
            var top = await f.Engine.MovePlanTaskUpAsync(a);
            Assert.Equal(1, Row(top, a).Order);
            await Assert.ThrowsAsync<ArgumentException>(() => f.Engine.MovePlanTaskUpAsync(mine));
            await Assert.ThrowsAsync<ArgumentException>(() =>
                f.Engine.MovePlanTaskUpAsync(before.Items.First(i => !PlanTasks.IsTaskId(i.Id)).Id));
        }

        [Fact]
        public async Task The_switch_changes_only_the_offer_wording()
        {
            using var f = await GappyAsync("CtlSwitch.bim");
            var id = await AskAsync(f, "Describe the Sales measures");
            var before = await f.Engine.GetModelPlanAsync();

            var paused = await f.Engine.SetAssistantQueueAsync("paused");
            Assert.Equal(PlanAssistantQueue.Paused, paused.AssistantQueue);
            Assert.Equal(PlanStateWords.Ready, Row(paused, id).StateWord);
            Assert.Equal(PlanTasks.PausedByYouNote, Row(paused, id).StateNote);
            Assert.Equal(Face(Row(before, id), "StateNote"), Face(Row(paused, id), "StateNote"));
            OthersUnchanged(before, paused, id);

            var on = await f.Engine.SetAssistantQueueAsync("On");
            Assert.Equal(PlanAssistantQueue.On, on.AssistantQueue);
            Assert.Equal(PlanTasks.OnNextCallNote, Row(on, id).StateNote);
            await Assert.ThrowsAsync<ArgumentException>(() => f.Engine.SetAssistantQueueAsync("off"));
        }

        [Fact]
        public async Task A_control_on_an_id_nobody_can_see_is_refused()
        {
            using var f = await GappyAsync("CtlUnknown.bim");
            await Assert.ThrowsAsync<ArgumentException>(() => f.Engine.HoldPlanItemAsync("safefix/Nothing", true));
            await Assert.ThrowsAsync<ArgumentException>(() => f.Engine.SetPlanTaskNoteAsync("pt-00000000", "x"));
        }

        // ---- person-only: absent from every agent route -----------------------------------------------------

        [Fact]
        public void The_controls_live_on_the_human_target_and_on_no_agent_surface()
        {
            foreach (var name in Controls)
            {
                Assert.NotNull(typeof(HumanGovernanceRpcTarget).GetMethod(name));
                Assert.Null(typeof(EngineRpcTarget).GetMethod(name));
            }

            // No MCP tool reaches any of them: no tool method calls the engine methods behind them.
            var toolNames = typeof(McpTools).Assembly.GetTypes()
                .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static))
                .Select(m => m.GetCustomAttribute<McpServerToolAttribute>()?.Name)
                .Where(n => n != null).ToArray();
            Assert.NotEmpty(toolNames);
            foreach (var forbidden in new[] { "add_plan_task", "hold_plan_item", "set_plan_item_doer", "move_plan_task_up",
                                              "set_assistant_queue", "set_plan_task_note" })
                Assert.DoesNotContain(forbidden, toolNames);
        }

        [Fact]
        public async Task An_agent_connection_is_refused_every_control_and_the_person_is_not()
        {
            var f = await GappyAsync("CtlPipe.bim");
            var pipeName = TestPipe.Mint("semanticus-ctl-");
            using var server = new RpcServer(f.Sessions, f.Engine, pipeName, Challenge);
            using var stop = new CancellationTokenSource();
            var serving = server.RunAsync(stop.Token);
            RpcClient human = null, agent = null;
            RemoteEngine remote = null;
            try
            {
                human = await RpcClient.ConnectAsync(pipeName, "human", Challenge);
                agent = await RpcClient.ConnectAsync(pipeName, "agent");
                remote = await RemoteEngine.ConnectAsync(pipeName);

                var added = await human.InvokeAsync<ModelPlanActionResult>("addPlanTask", new PlanTaskRequest { Words = "From the page" });
                Assert.Matches("^pt-[0-9a-f]{8}$", added.ItemId);
                var held = await human.InvokeAsync<ModelPlanView>("holdPlanItem", added.ItemId, true);
                Assert.Equal(PlanStateWords.Paused, Row(held, added.ItemId).StateWord);

                async Task Refused(Func<Task> call)
                {
                    var ex = await Assert.ThrowsAnyAsync<Exception>(call);
                    Assert.Contains("method", ex.Message, StringComparison.OrdinalIgnoreCase);
                }
                var revision = f.Sessions.Current.Revision;
                var planBefore = Row(await f.Engine.GetModelPlanAsync(), added.ItemId);

                await Refused(() => agent.InvokeAsync<ModelPlanActionResult>("addPlanTask", new PlanTaskRequest { Words = "Invented" }));
                await Refused(() => agent.InvokeAsync<ModelPlanView>("holdPlanItem", added.ItemId, false));
                await Refused(() => agent.InvokeAsync<ModelPlanView>("setPlanItemDoer", added.ItemId, "you"));
                await Refused(() => agent.InvokeAsync<ModelPlanView>("movePlanTaskUp", added.ItemId));
                await Refused(() => agent.InvokeAsync<ModelPlanView>("setAssistantQueue", "paused"));
                await Refused(() => agent.InvokeAsync<ModelPlanView>("setPlanTaskNote", added.ItemId, "x"));

                // The attached assistant's own engine: the same refusal, from the owner's surface.
                await Refused(() => remote.AddPlanTaskAsync(new PlanTaskRequest { Words = "Invented" }));
                await Refused(() => remote.HoldPlanItemAsync(added.ItemId, false));
                await Refused(() => remote.SetPlanItemDoerAsync(added.ItemId, "you"));
                await Refused(() => remote.MovePlanTaskUpAsync(added.ItemId));
                await Refused(() => remote.SetAssistantQueueAsync("paused"));
                await Refused(() => remote.SetPlanTaskNoteAsync(added.ItemId, "x"));

                // Nothing any of them tried landed, and the assistant can still READ the setting.
                var after = await remote.GetModelPlanAsync();
                Assert.Equal(revision, f.Sessions.Current.Revision);
                Assert.DoesNotContain(after.Items, i => i.Title == "Invented");
                Assert.Equal(Face(planBefore), Face(Row(after, added.ItemId)));
                Assert.True(Row(after, added.ItemId).Held);
                Assert.Equal(PlanAssistantQueue.On, after.AssistantQueue);
            }
            finally
            {
                remote?.Dispose();
                agent?.Dispose();
                human?.Dispose();
                stop.Cancel();
                try { await serving; } catch (OperationCanceledException) { }
                f.Dispose();
            }
        }
            /// <summary>A bare pipe client that speaks the handshake as a person or an agent (as RpcRoleBindingTests does).</summary>
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
