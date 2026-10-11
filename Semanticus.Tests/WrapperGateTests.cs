using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Engine;
using TabularEditor.TOMWrapper;
using TabularEditor.TOMWrapper.Utils;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// The wrapper gate (spec-first start design v2, S6): the vendored wrapper binds every object it creates to a
    /// process-wide static handler, so a second model on its own dispatcher (a hidden scratch build, or a session
    /// built before the old one retires) used to be able to steal the binding. These tests keep two models alive
    /// at once and check each object lands on its own model, and on its own undo stack.
    /// </summary>
    public sealed class WrapperGateTests
    {
        private static readonly TimeSpan Patience = TimeSpan.FromSeconds(60);

        private static async Task<T> Within<T>(Task<T> t) => await t.WaitAsync(Patience);
        private static async Task Within(Task t) => await t.WaitAsync(Patience);

        // (a) While scratch B builds 100 objects on its own dispatcher, A creates measures. Each of A's measures
        // must carry A's handler, and A's undo stack must hold only A's actions.
        [Fact]
        public async Task A_model_s_objects_keep_its_own_handler_and_undo_stack_while_another_model_builds()
        {
            var a = await new SessionManager().CreateAsync("A", 1604);
            await a.MutateAsync("agent", "add table", m => { m.AddTable("TA"); });
            var b = await new SessionManager().CreateAsync("B", 1604);   // built last: ungated, the static now points at B
            using (a)
            using (b)
            {
                await b.MutateAsync("agent", "add table", m => { m.AddTable("TB"); });
                var aBefore = a.UndoStateNow().Steps;
                var bBefore = b.UndoStateNow().Steps;

                var bMeasures = new List<Measure>();
                var aMeasures = new List<Measure>();
                var build = Task.Run(async () =>
                {
                    for (var i = 0; i < 100; i++)
                        await b.MutateAsync("agent", "scratch " + i, m => bMeasures.Add(m.Tables["TB"].AddMeasure("B" + i, "1")));
                });
                for (var i = 0; i < 20; i++)
                    await Within(a.MutateAsync("agent", "A measure " + i, m => aMeasures.Add(m.Tables["TA"].AddMeasure("MA" + i, "1"))));
                await Within(build);

                var aHandler = WrapperGate.HandlerOf(a.Model);
                var bHandler = WrapperGate.HandlerOf(b.Model);
                Assert.NotSame(aHandler, bHandler);
                Assert.All(aMeasures, m => Assert.Same(aHandler, WrapperGate.HandlerOf(m)));
                Assert.All(bMeasures, m => Assert.Same(bHandler, WrapperGate.HandlerOf(m)));
                Assert.Equal(aBefore + 20, a.UndoStateNow().Steps);
                Assert.Equal(bBefore + 100, b.UndoStateNow().Steps);
            }
        }

        // (b) Barrier: pause INSIDE one of B's work items, bring a third handler to life from another thread, then
        // resume. B's new objects must still bind to B: the construction waits for the item to finish.
        [Fact]
        public async Task A_third_handler_built_mid_work_item_cannot_move_the_model_the_item_is_working_on()
        {
            using var b = await new SessionManager().CreateAsync("B", 1604);
            await b.MutateAsync("agent", "add table", m => { m.AddTable("TB"); });

            using var paused = new ManualResetEventSlim();
            using var resume = new ManualResetEventSlim();
            Measure before = null, after = null;
            var item = b.RunAsync(() =>
            {
                var t = b.Model.Tables["TB"];
                before = t.AddMeasure("before", "1");
                paused.Set();
                resume.Wait(Patience);
                after = t.AddMeasure("after", "1");
                return true;
            });
            Assert.True(paused.Wait(Patience), "B's work item never started");

            // A third model comes to life on its own dispatcher thread while B's item is paused mid-way.
            var constructing = Task.Run(() => new SessionManager().CreateAsync("C", 1604));
            Assert.True(SpinWait.SpinUntil(() => constructing.IsCompleted || WrapperGate.WaitingCount > 0, Patience),
                "the third construction neither finished nor reached the gate");
            resume.Set();
            await Within(item);
            using var c = await Within(constructing);

            var bHandler = WrapperGate.HandlerOf(b.Model);
            Assert.NotSame(bHandler, WrapperGate.HandlerOf(c.Model));
            Assert.Same(bHandler, WrapperGate.HandlerOf(before));
            Assert.Same(bHandler, WrapperGate.HandlerOf(after));
        }

        // A model built INSIDE another model's work item (a temporary handler) hands the pin back afterwards.
        [Fact]
        public async Task A_handler_built_inside_a_work_item_hands_the_pin_back_to_the_item()
        {
            using var a = await new SessionManager().CreateAsync("A", 1604);
            await a.MutateAsync("agent", "add table", m => { m.AddTable("TA"); });
            var made = await a.RunAsync(() =>
            {
                var temp = Te2ModelSession.Create("temp", 1604);   // its constructor moves the static, inside the gate
                Assert.NotSame(WrapperGate.HandlerOf(a.Model), WrapperGate.HandlerOf(temp.Model));
                return a.Model.Tables["TA"].AddMeasure("after the temp", "1");
            });
            Assert.Same(WrapperGate.HandlerOf(a.Model), WrapperGate.HandlerOf(made));
        }

        // Undo and redo on both models stay on their own timelines when the two interleave.
        [Fact]
        public async Task Undo_and_redo_on_two_models_stay_on_their_own_timelines()
        {
            var a = await new SessionManager().CreateAsync("A", 1604);
            await a.MutateAsync("agent", "add table", m => { m.AddTable("TA"); });
            var b = await new SessionManager().CreateAsync("B", 1604);
            using (a)
            using (b)
            {
                await b.MutateAsync("agent", "add table", m => { m.AddTable("TB"); });
                await a.MutateAsync("agent", "a1", m => { m.Tables["TA"].AddMeasure("A1", "1"); });
                await b.MutateAsync("agent", "b1", m => { m.Tables["TB"].AddMeasure("B1", "1"); });
                await b.MutateAsync("agent", "b2", m => { m.Tables["TB"].AddMeasure("B2", "1"); });

                await a.UndoAsync("human");
                await b.UndoAsync("human");
                Assert.Empty(await a.ReadAsync(m => m.Tables["TA"].Measures.ToArray()));
                Assert.Equal(new[] { "B1" }, await b.ReadAsync(m => m.Tables["TB"].Measures.Select(x => x.Name).ToArray()));

                await b.RedoAsync("human");
                await a.RedoAsync("human");
                Assert.Equal(new[] { "A1" }, await a.ReadAsync(m => m.Tables["TA"].Measures.Select(x => x.Name).ToArray()));
                Assert.Equal(2, await b.ReadAsync(m => m.Tables["TB"].Measures.Count));
                Assert.Equal(1, a.UndoStateNow().Steps - 1);   // the table add, then A1
                Assert.Equal(3, b.UndoStateNow().Steps);       // the table add, then B1 and B2
            }
        }
    }
}
