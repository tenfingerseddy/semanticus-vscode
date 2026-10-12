// verify: m11/switch-scope round 1. Left failing on purpose: each fact is a defect the verifier found, not a fix.
//
// A Power BI Desktop model is keyed by the .pbix path when Desktop's command line names it, and by the window title
// when it does not, which is every file opened from Desktop's own Open dialog or its recent list
// (LocalDesktop.cs:16-18). So two files that share a title share one switch, and a point one owner switched off is
// switched back on by the other; and one file reached once by title and once by path keeps two switches, so a point
// switched off one way reads on the other way.
//
// The primer keys on the same ladder, finds a path key's title twin (LocalEngine.Primer.cs:287-295), and lists two
// title-only files as a disclosed limit (:24-27). For a primer that limit shows one model's notes for another; for a
// switch it sends what a person switched off, and a switch can fail closed simply, because an off may always win.
//
// Why it matters although a re-opened Desktop model asks for permission again: a switch is the promise that "off
// means it is never asked, whatever the permission says" (set_decision_point_enabled). The next yes to the
// permission card sends the point the person switched off.

using System;
using System.IO;
using System.Threading.Tasks;
using Semanticus.Decisions;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    public sealed class DecisionSwitchScopeVerifyTests
    {
        private const string Point = DecisionRegistry.MeasureFolderAndFormat;

        private static async Task OpenDesktopAsync(DecisionFixture f, string port, string database, string title, string path = null)
        {
            await f.Engine.CreateModelAsync(title, 1604, discardUnsaved: true);
            f.Sessions.Current.LiveOrigin = new LiveOrigin("localhost:" + port, database, null, localName: title, localPath: path);
        }

        /// <summary>A Desktop model with one measure that has no folder or format, so the folder-and-format point has
        /// something to ask.</summary>
        private static async Task<string> OpenDesktopWithMeasureAsync(DecisionFixture f, string port, string database, string title)
        {
            await OpenDesktopAsync(f, port, database, title);
            await f.Engine.CreateTableAsync("Sales", "human");
            await f.Engine.CreateColumnAsync("table:Sales", "Amount", "Double", null, "human");
            await f.Engine.CreateMeasureAsync("table:Sales", "Order Count", "COUNTROWS ( Sales )", "human", "Volume");
            return await f.Engine.CreateMeasureAsync("table:Sales", "Total Sales", "SUM ( Sales[Amount] )", "human");
        }

        [Fact]
        public async Task Verify_two_Desktop_files_with_the_same_title_never_switch_each_other_back_on()
        {
            // Two different files, both called Sales.pbix, both opened from Desktop's Open dialog.
            using var f = new DecisionFixture();
            var a = Guid.NewGuid().ToString();
            await OpenDesktopAsync(f, "52001", a, "Sales");
            await McpTools.SetDecisionPointEnabled(f.Engine, Point, false);

            // B's owner switches the point on for B, from Studio.
            await OpenDesktopAsync(f, "52002", Guid.NewGuid().ToString(), "Sales");
            await new EngineRpcTarget(f.Engine).setDecisionPointEnabled(Point, true);

            await OpenDesktopAsync(f, "52001", a, "Sales");
            Assert.False(f.Engine.DecisionPointEnabled(Point),
                "Switching the point on for one Sales.pbix switched it back on for the other, whose owner switched it off.");
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Verify_two_Desktop_files_with_the_same_title_the_next_yes_sends_what_one_switched_off()
        {
            using var f = new DecisionFixture();
            var a = Guid.NewGuid().ToString();
            var aMeasure = await OpenDesktopWithMeasureAsync(f, "52001", a, "Sales");
            await f.ConsentAsync();
            var oracle = f.UseEmptyRecorded();
            await McpTools.SetDecisionPointEnabled(f.Engine, Point, false);
            Assert.False((await f.Engine.ProposeMeasureDefaultsAsync(aMeasure)).Asked);

            await OpenDesktopWithMeasureAsync(f, "52002", Guid.NewGuid().ToString(), "Sales");
            await f.ConsentAsync();
            await new EngineRpcTarget(f.Engine).setDecisionPointEnabled(Point, true);

            // Back to A. K19 (8 Oct): permission is kept on this machine under A's own address, so A still has the yes it was given.
            // B's yes did not reach A, and A's off stays A's.
            aMeasure = await OpenDesktopWithMeasureAsync(f, "52001", a, "Sales");
            Assert.True((await f.Engine.GetConsentAsync()).Current);
            var asked = oracle.AskedDigests.Count;
            var proposal = await McpTools.ProposeMeasureDefaults(f.Engine, aMeasure);
            Assert.False(proposal.Asked, "A's measure was sent for a suggestion although A's owner switched the point off for A.");
            Assert.Equal(asked, oracle.AskedDigests.Count);
        }

        [Theory]
        [InlineData(false)]   // opened from Desktop's Open dialog first, double-clicked later
        [InlineData(true)]    // double-clicked first, opened from Desktop's Open dialog later
        public async Task Verify_one_Desktop_file_keeps_its_switch_whether_Desktop_names_its_path_or_only_its_title(bool pathFirst)
        {
            using var f = new DecisionFixture();
            var sales = Path.Combine(f.Home, "reports", "Sales.pbix");
            await OpenDesktopAsync(f, "52001", Guid.NewGuid().ToString(), "Sales", pathFirst ? sales : null);
            await new EngineRpcTarget(f.Engine).setDecisionPointEnabled(Point, false);

            // The same file on another day: Desktop has a new port and a new database name.
            await OpenDesktopAsync(f, "61234", Guid.NewGuid().ToString(), "Sales", pathFirst ? null : sales);
            Assert.False(f.Engine.DecisionPointEnabled(Point),
                "A point switched off for Sales.pbix reads on again because Desktop was opened the other way.");
        }
    }
}
