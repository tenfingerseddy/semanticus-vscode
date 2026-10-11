// verify: m11/switch-scope round 3. Left failing on purpose: each fact is a defect the verifier found, not a fix.
//
// An on for a Power BI Desktop model clears the offs next door that might be this model's, and the row it answers with
// says: "This also turns it back on for other Power BI Desktop models Semanticus cannot tell apart from this one." Two
// of those offs belong to no model in particular, and every Desktop model reads them: the workspace's old off from
// before each model kept its own switches (lifted for every Desktop model through desktop-earlier-lifted.json, which is
// keyed by the point alone), and an off a Desktop model known by nothing left in desktop-unnamed.json (every Desktop
// model reads it, and an on for any Desktop model empties it). So an on for Sales, a file Desktop named by its path or
// its title, also switches the point back on for Stock, a different file Semanticus tells apart from Sales, and after
// the next yes to Stock's permission card its measure is sent, although nobody switched it on for Stock. The row said
// only look-alikes, and the fix report says an on never spreads to a file known by path; its test gives both files an
// off of their own first, which hides this.
//
// These facts do not choose the fix. They pass when Stock keeps its off (the clear reaches only the models Semanticus
// cannot tell apart from Sales), or when the row says what really happened (every Desktop file that read that off is
// back on), which changes what this lane promises and so is Kane's call. An on made for a Desktop model known by
// nothing is left out on purpose: that model might be Stock, so the sentence covers it.

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Decisions;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    public sealed class DecisionSwitchScopeVerifyR3Tests
    {
        private const string Point = DecisionRegistry.MeasureFolderAndFormat;
        private const string LookAlikesOnly = "This also turns it back on for other Power BI Desktop models Semanticus cannot tell apart from this one.";

        /// <summary>A Desktop model the way open_local stamps it (LocalEngine.cs:3247): localDesktop is always true there,
        /// and the title and path are whatever LocalDesktop.TryGetIdentity could read.</summary>
        private static async Task OpenDesktopAsync(DecisionFixture f, string port, string title = null, string path = null)
        {
            await f.Engine.CreateModelAsync(title ?? "Model", 1604, discardUnsaved: true);
            f.Sessions.Current.LiveOrigin = new LiveOrigin("localhost:" + port, Guid.NewGuid().ToString(), null,
                localName: title, localPath: path, localDesktop: true);
        }

        private static async Task<string> AddMeasureAsync(DecisionFixture f)
        {
            await f.Engine.CreateTableAsync("Sales", "human");
            await f.Engine.CreateColumnAsync("table:Sales", "Amount", "Double", null, "human");
            await f.Engine.CreateMeasureAsync("table:Sales", "Order Count", "COUNTROWS ( Sales )", "human", "Volume");
            return await f.Engine.CreateMeasureAsync("table:Sales", "Total Sales", "SUM ( Sales[Amount] )", "human");
        }

        // off: "old" is the workspace's off from before each model kept its own switches; "nothing" is an off a Desktop model
        // known by nothing left behind. salesBy and stockBy: "path" is a double-click (Desktop names the path and the
        // title), "title" is Desktop's recent list (the title only). Either way Sales and Stock are two different files.
        [Theory]
        [InlineData("old", "path", "path", "studio")]
        [InlineData("old", "title", "path", "agent")]
        [InlineData("nothing", "path", "path", "studio")]
        [InlineData("nothing", "title", "title", "agent")]
        public async Task Verify_an_on_for_Sales_that_switches_Stock_back_on_says_so(string off, string salesBy, string stockBy, string door)
        {
            using var f = new DecisionFixture();
            var sales = Path.Combine(f.Home, "east", "Sales.pbix");
            var stock = Path.Combine(f.Home, "west", "Stock.pbix");
            if (off == "old")
            {
                // Anyone who switched a point off before each model kept its own switches has this in the workspace file.
                var shared = Path.Combine(f.Workspace, LayoutStore.DirName, "decision-settings.json");
                Directory.CreateDirectory(Path.GetDirectoryName(shared));
                File.WriteAllText(shared, "{\"points\":{\"" + Point + "\":{\"enabled\":false}}}");
            }
            else
            {
                await OpenDesktopAsync(f, "52000");   // some Desktop file, opened when Desktop named nothing
                await McpTools.SetDecisionPointEnabled(f.Engine, Point, false);
            }

            Task OpenStock(string port) => OpenDesktopAsync(f, port, "Stock", stockBy == "path" ? stock : null);
            await OpenStock("52002");
            Assert.False(f.Engine.DecisionPointEnabled(Point));   // Stock reads the off before anything else happens

            // Sales. The person switches the point on for it.
            await OpenDesktopAsync(f, "52001", "Sales", salesBy == "path" ? sales : null);
            Assert.False(f.Engine.DecisionPointEnabled(Point));
            var rows = door == "studio" ? await new EngineRpcTarget(f.Engine).setDecisionPointEnabled(Point, true)
                                        : await McpTools.SetDecisionPointEnabled(f.Engine, Point, true);
            var note = rows.Single(p => p.Name == Point).Note;

            // Stock, opened again another day. Nobody switched the point on for it.
            await OpenStock("52003");
            var stockOn = (await McpTools.GetDecisionPoints(f.Engine)).Single(p => p.Name == Point).Enabled;
            var measure = await AddMeasureAsync(f);
            await f.ConsentAsync();
            var oracle = f.UseEmptyRecorded();
            var asked = (await McpTools.ProposeMeasureDefaults(f.Engine, measure)).Asked;

            Assert.False(stockOn && note == LookAlikesOnly,
                "The on for Sales switched the point back on for Stock, a file Semanticus tells apart from it, "
                + "and after the yes Stock's measure was " + (asked ? "sent" : "not sent")
                + ", while the row said only: " + note);
            Assert.False(asked && note == LookAlikesOnly, "Stock was asked, and the row that switched it on said only look-alikes.");
        }
    }
}
