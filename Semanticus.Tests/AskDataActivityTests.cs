// Golden rule 2 for ask_data: the UI door is pushed changes live. A question the person's assistant asks over MCP
// is published as a read-only activity ("ask_data", origin agent), so an open Ask your data page refreshes its list
// the moment it lands instead of on the next Refresh. It is not a model change: the model and undo are untouched.

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    public class AskDataActivityTests
    {
        [Fact]
        public async Task An_assistant_ask_is_published_as_a_read_only_activity()
        {
            using var f = new DecisionFixture();
            await f.Engine.CreateModelAsync("Contoso Retail", 1604);
            await f.Engine.CreateTableAsync("Sales", "human");
            await f.Engine.CreateColumnAsync("table:Sales", "Amount", "Double", "Amount", "human");
            await f.Engine.CreateMeasureAsync("table:Sales", "Net Sales", "SUM ( Sales[Amount] )", "human");
            var seen = new List<ActivityEvent>();
            f.Sessions.Bus.Activity += seen.Add;

            var a = await McpTools.AskData(f.Engine, measures: new[] { "Net Sales" });
            await Task.Delay(50);   // Emit is fire-and-forget

            var e = Assert.Single(seen.Where(x => x.Kind == "ask_data"));
            Assert.Equal("agent", e.Origin);
            Assert.Equal(a.Reading, e.Target);
            Assert.Null(e.Result);   // no rows ride on the activity: the page reads the history, which keeps none
        }
    }
}
