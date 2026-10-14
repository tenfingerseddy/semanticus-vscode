// Verifier (v-plan-status): the session-held Done moves into the file once (no double count), and never follows the person to another model.
using System;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;
using Xunit.Abstractions;

namespace Semanticus.Tests
{
    public sealed class VPlanStatusTests
    {
        private readonly ITestOutputHelper _out;
        public VPlanStatusTests(ITestOutputHelper o) => _out = o;
        private const string Compat = "Raise compatibility level to 1701";

        [Fact]
        public async Task Done_held_in_the_session_moves_into_the_file_exactly_once()
        {
            using var f = new DecisionFixture(TestEntitlements.Pro);
            var e = f.Engine;
            await e.CreateModelAsync("Once", 1604);
            await e.CreateTableAsync("Sales", "human");
            await e.CreateColumnAsync("table:Sales", "Amount", "Decimal", null, "human");
            f.UseNull();
            var inv = new PlanInvariant(e, _out);
            await inv.CheckAsync("start", findings: false);
            await McpTools.SetCompatibilityLevel(e, 1701);
            var mid = await inv.CheckAsync("after the direct tool", findings: false);
            var before = mid.Strip.TasksDone;
            _out.WriteLine($"unsaved: {mid.Strip.TasksDone} of {mid.Strip.TasksAll}");
            Assert.True(before >= 1);
            await f.SaveAsAsync("Once.bim");
            var a = await inv.CheckAsync("first save", findings: false);
            Assert.Equal(before, a.Strip.TasksDone);
            Assert.True(a.Strip.TasksDone <= a.Strip.TasksAll);
            Assert.Equal(1, a.Items.Count(i => i.Title == Compat && i.StateWord == PlanStateWords.Done));
            await f.SaveAsAsync("Once.bim");
            var b = await inv.CheckAsync("second save", findings: false);
            Assert.Equal(before, b.Strip.TasksDone);
            var c = await inv.CheckAsync("re-read", findings: false);
            Assert.Equal(before, c.Strip.TasksDone);
            _out.WriteLine($"saved: {c.Strip.TasksDone} of {c.Strip.TasksAll}");
        }

        private static async Task<ModelPlanView> FreshB(DecisionFixture f, PlanInvariant inv, bool discard)
        {
            await f.Engine.CreateModelAsync("Other", 1604, discard);
            await f.Engine.CreateTableAsync("Sales", "human");
            await f.Engine.CreateColumnAsync("table:Sales", "Amount", "Decimal", null, "human");
            return await inv.CheckAsync("model B", findings: false);
        }

        [Fact]
        public async Task Done_on_one_unsaved_model_does_not_carry_to_another_model_saved_or_not()
        {
            using var g = new DecisionFixture(TestEntitlements.Pro);
            g.UseNull();
            var baseline = await FreshB(g, new PlanInvariant(g.Engine, _out), false);

            using var f = new DecisionFixture(TestEntitlements.Pro);
            var e = f.Engine;
            await e.CreateModelAsync("A", 1604);
            await e.CreateTableAsync("Sales", "human");
            await e.CreateColumnAsync("table:Sales", "Amount", "Decimal", null, "human");
            f.UseNull();
            var inv = new PlanInvariant(e, _out);
            await inv.CheckAsync("A start", findings: false);
            await McpTools.SetCompatibilityLevel(e, 1701);
            var a = await inv.CheckAsync("A done", findings: false);
            Assert.True(a.Strip.TasksDone >= 1);
            var b = await FreshB(f, inv, true);
            _out.WriteLine($"baseline B {baseline.Strip.TasksDone}/{baseline.Strip.TasksAll}; B after A {b.Strip.TasksDone}/{b.Strip.TasksAll}");
            Assert.Equal(baseline.Strip.TasksDone, b.Strip.TasksDone);
            Assert.DoesNotContain(b.Items, i => i.Title == Compat && i.StateWord == PlanStateWords.Done);
            await f.SaveAsAsync("Other.bim");
            var saved = await inv.CheckAsync("B saved", findings: false);
            Assert.Equal(baseline.Strip.TasksDone, saved.Strip.TasksDone);
            Assert.DoesNotContain(saved.Items, i => i.Title == Compat && i.StateWord == PlanStateWords.Done);
        }
    }
}
