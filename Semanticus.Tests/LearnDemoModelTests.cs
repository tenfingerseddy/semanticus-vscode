// The Learn tour's demo model (1.4, contract section 9): Semanticus.VSCode/assets/learn/contoso-shop/, a TMDL folder.
//
// What this holds, in one line each:
//   the folder opens through the engine's own open, with the five tables and six measures the tour talks about
//   the Plan lists rows for the gaps left on purpose, so the tour's first chapter has something to point at
//   the gaps are the four the tour names: missing descriptions, an unhidden key, no date table mark, an implicit measure
// The tour works on a scratch copy, so this test copies the folder too and never opens the one in the repo.

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    public sealed class LearnDemoModelTests
    {
        private static string DemoFolder() =>
            Path.Combine(AssistantGateKit.RepoRoot(), "Semanticus.VSCode", "assets", "learn", "contoso-shop");

        private static void CopyFolder(string from, string to)
        {
            Directory.CreateDirectory(to);
            foreach (var file in Directory.GetFiles(from)) File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
            foreach (var dir in Directory.GetDirectories(from)) CopyFolder(dir, Path.Combine(to, Path.GetFileName(dir)));
        }

        [Fact]
        public async Task The_demo_model_opens_with_five_tables_and_six_measures_and_the_plan_has_rows()
        {
            using var f = new DecisionFixture(TestEntitlements.Pro);
            f.UseNull();
            var copy = Path.Combine(f.Workspace, "contoso-shop");
            CopyFolder(DemoFolder(), copy);

            var opened = await f.Engine.OpenAsync(copy);

            Assert.Equal("Contoso Shop", opened.ModelName);
            Assert.Equal(5, opened.Tables);
            Assert.Equal(6, opened.Measures);

            var plan = await f.Engine.GetModelPlanAsync();
            var open = plan.Items.Where(i => i.Status is ModelPlanStatuses.Ready or ModelPlanStatuses.Proposed).ToArray();
            var titles = string.Join(" | ", plan.Items.Select(i => $"{i.Id}: {i.Title}"));
            Assert.True(open.Length >= 3, "The Plan needs rows for the tour to point at. It listed: " + titles);
            Assert.All(plan.Items, i => Assert.DoesNotContain("—", i.Title, StringComparison.Ordinal));
        }

        [Fact]
        public async Task The_demo_model_keeps_its_four_gaps_on_purpose()
        {
            using var f = new DecisionFixture(TestEntitlements.Pro);
            f.UseNull();
            var copy = Path.Combine(f.Workspace, "contoso-gaps");
            CopyFolder(DemoFolder(), copy);
            await f.Engine.OpenAsync(copy);

            var plan = await f.Engine.GetModelPlanAsync();
            var all = string.Join(" | ", plan.Items.Select(i => $"{i.Id}: {i.Title}"));

            // The three gaps the Plan names in a row of their own.
            Assert.True(plan.Items.Any(i => i.Title.StartsWith("Write words for Copilot", StringComparison.Ordinal)),   // 6343ed4b: one words row for the descriptions and the rest
                "No row that writes descriptions. The Plan listed: " + all);
            Assert.True(plan.Items.Any(i => i.Title.Contains("Mark Date as the date table", StringComparison.OrdinalIgnoreCase)),
                "No row that marks the date table. The Plan listed: " + all);
            Assert.True(plan.Items.Any(i => i.Id.Contains("implicit", StringComparison.OrdinalIgnoreCase)),
                "No row about the implicit measure. The Plan listed: " + all);

            // The unhidden key sits inside the best-practice row, so the scan is asked for it by name.
            var scan = await f.Engine.AiReadinessScanAsync();
            var keys = scan.Findings.Where(x => x.RuleId == "VIS-KEY").Select(x => x.ObjectRef).ToArray();
            Assert.Contains(keys, k => k.Contains("Customer Key", StringComparison.Ordinal));
            Assert.Contains(keys, k => k.Contains("Product Key", StringComparison.Ordinal));
        }
    }
}
