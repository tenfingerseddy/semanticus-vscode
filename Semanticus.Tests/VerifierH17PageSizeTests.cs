using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.StartPlan;
using Xunit;

namespace Semanticus.Tests
{
    // Verifier v-h17-engine: a max-100 start plan page must stay under the 50,000 characters Claude Code passes uncut (PlanReadShape.WholePlanChars note).
    public class VerifierH17PageSizeTests
    {
        [Fact]
        public async Task A_max_100_page_stays_inside_the_tool_result_limit()
        {
            var engine = DispatchProxy.Create<IEngine, LeanEngineFake>();
            var fake = (LeanEngineFake)(object)engine;
            fake.View.Sections[0].Groups[0].Lines = Enumerable.Range(0, 400).Select(i => new StartLine
            {
                Item = new ChangeItem { Id = "line-" + i, Kind = "set_description", ObjectRef = "column:Sales/Column" + i, After = new string('x', 900) },
                Why = "Describe the column.", Kept = true, DecidedBy = "rule",
            }).ToList();
            var view = await McpTools.GetStartPlanOp(engine, item: "tables", offset: 0, max: 100);
            Assert.True(McpLeanResults.Chars(view) <= PlanReadShape.WholePlanChars, "page was " + McpLeanResults.Chars(view) + " characters");
        }
    }
}
