// "Fix 3 names that Copilot reads as the same word" opened Find and replace EMPTY (plan statement check 2, defect 6): the person
// had to work out which names to type. The row now carries the clashing names in its jump (ModelPlanJump.Find), both doors read them
// from get_model_plan, and the Studio opens Find and replace with them filled in (webview test: plan-collision-find.test.mjs).

using System;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    public sealed class PlanCollisionFindTests
    {
        /// <summary>Each group is one column and one measure whose names read as the same word to Copilot ("Group1Key", "Group1 Key").</summary>
        private static async Task<DecisionFixture> CollidingModelAsync(int groups)
        {
            var f = new DecisionFixture(TestEntitlements.Pro);
            await f.Engine.CreateModelAsync("Contoso", 1604);
            await f.Engine.CreateTableAsync("Sales", "human");
            await f.Engine.CreateColumnAsync("table:Sales", "Amount", "Double", null, "human");
            await f.Engine.CreateLinguisticSchemaAsync(null, "agent");
            for (var i = 1; i <= groups; i++)
            {
                await f.Engine.CreateColumnAsync("table:Sales", "Group" + i + "Key", "String", null, "human");
                await f.Engine.CreateMeasureAsync("table:Sales", "Group" + i + " Key", "SUM ( Sales[Amount] )", "human");
            }
            await f.SaveAsAsync("CollisionFind.bim");
            f.UseNull();
            return f;
        }

        [Fact]
        public async Task The_collisions_row_hands_find_and_replace_every_clashing_name()
        {
            using var f = await CollidingModelAsync(3);
            var row = (await f.Engine.GetModelPlanAsync()).Items.Single(i => i.Id == "proposal/collisions");

            Assert.Equal("search", row.Jump.Tool);
            Assert.NotNull(row.Jump.Find);
            foreach (var n in new[] { 1, 2, 3 })
            {
                Assert.Contains("Group" + n + "Key", row.Jump.Find);
                Assert.Contains("Group" + n + " Key", row.Jump.Find);
            }
            Assert.Equal(row.Jump.Find.Length, row.Jump.Find.Distinct().Count());
        }

        [Fact]
        public async Task Only_the_collisions_row_carries_names_to_find()
        {
            using var f = await CollidingModelAsync(2);
            var plan = await f.Engine.GetModelPlanAsync();
            Assert.All(plan.Items.Where(i => i.Id != "proposal/collisions"), i => Assert.Null(i.Jump?.Find));
        }

        [Fact]
        public async Task A_long_list_of_clashes_is_capped_for_the_search_box()
        {
            using var f = await CollidingModelAsync(30);   // 60 clashing names
            var row = (await f.Engine.GetModelPlanAsync()).Items.Single(i => i.Id == "proposal/collisions");
            Assert.Equal(LocalEngine.FindNameCap, row.Jump.Find.Length);
        }

        [Fact]
        public async Task The_agent_door_reads_the_same_names()
        {
            using var f = await CollidingModelAsync(2);
            var viaEngine = (await f.Engine.GetModelPlanAsync()).Items.Single(i => i.Id == "proposal/collisions").Jump.Find;
            var json = System.Text.Json.JsonSerializer.Serialize(await f.Engine.GetModelPlanAsync(), ModelContextProtocol.McpJsonUtilities.DefaultOptions);
            foreach (var n in viaEngine) Assert.Contains(n, json);
            Assert.Contains("\"find\"", json);
        }
    }
}
