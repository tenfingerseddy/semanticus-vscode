// Verifier probe (v-refresh, 11 Oct): Refresh plan on the real Contoso Shop demo, through the gated MCP door.
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;
using Xunit.Abstractions;

namespace Semanticus.Tests
{
    public sealed class VRefreshContosoTests
    {
        private readonly ITestOutputHelper _out;
        public VRefreshContosoTests(ITestOutputHelper o) => _out = o;

        private static void CopyFolder(string from, string to)
        {
            Directory.CreateDirectory(to);
            foreach (var file in Directory.GetFiles(from)) File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
            foreach (var dir in Directory.GetDirectories(from)) CopyFolder(dir, Path.Combine(to, Path.GetFileName(dir)));
        }

        [Fact]
        public async Task Refresh_plan_on_Contoso_Shop_clears_done_rows_keeps_history_and_undo_redo_hold()
        {
            using var f = new DecisionFixture(TestEntitlements.Pro);
            f.UseNull();
            var copy = Path.Combine(f.Workspace, "contoso-shop");
            CopyFolder(Path.Combine(AssistantGateKit.RepoRoot(), "Semanticus.VSCode", "assets", "learn", "contoso-shop"), copy);
            await f.Engine.OpenAsync(copy);

            var start = await f.Engine.GetModelPlanAsync();
            _out.WriteLine("start: " + string.Join(" | ", start.Items.Select(i => $"{i.Id}={i.Status}/{i.StateWord}")));
            var gated = AssistantEngine.ForAssistant(f.Engine);
            var first = await McpTools.RefreshPlan(gated);
            _out.WriteLine("first refresh: " + first.Sentence);

            for (var press = 0; press < 4; press++)
            {
                var plan = await f.Engine.GetModelPlanAsync();
                if (!plan.Items.Any(i => i.StateWord == PlanStateWords.Ready && i.Doer == PlanDoers.Semanticus)) break;
                var ran = await f.Engine.ApplyPlanItemsAsync(null, "human", plan.ReadyDigest);
                _out.WriteLine("run: " + ran.Sentence);
            }
            var after = await f.Engine.GetModelPlanAsync();
            var done = after.Items.Where(i => i.Status == ModelPlanStatuses.Done).ToList();
            _out.WriteLine("after run: " + string.Join(" | ", after.Items.Select(i => $"{i.Id}={i.Status}")));
            Assert.NotEmpty(done);

            var r = await McpTools.RefreshPlan(gated);
            _out.WriteLine("refresh: " + r.Sentence);
            Assert.Equal(done.Count, r.Cleared);
            var listed = await f.Engine.GetModelPlanAsync();
            Assert.DoesNotContain(listed.Items, i => i.Status == ModelPlanStatuses.Done);
            var entry = listed.History.Last(h => h.Sentence != null && h.Sentence.StartsWith("Refreshed the plan"));
            foreach (var d in done) Assert.Contains(d.Title, entry.Detail);
            Assert.Equal("Nothing new.", (await McpTools.RefreshPlan(gated)).Sentence);

            await f.Engine.UndoAsync("human");
            var undone = await f.Engine.GetModelPlanAsync();
            _out.WriteLine("undone: " + string.Join(" | ", undone.Items.Select(i => $"{i.Id}={i.Status}")));
            Assert.Contains(undone.Items, i => i.Status != ModelPlanStatuses.Done && done.Any(d => d.Id == i.Id));
            await f.Engine.RedoAsync("human");
            var redone = await f.Engine.GetModelPlanAsync();
            _out.WriteLine("redone: " + string.Join(" | ", redone.Items.Select(i => $"{i.Id}={i.Status}")));
            Assert.Contains(redone.Items, i => i.Status == ModelPlanStatuses.Done && done.Any(d => d.Id == i.Id));
        }
    }
}
