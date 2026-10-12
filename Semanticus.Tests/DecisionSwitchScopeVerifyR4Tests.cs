// verify: m11/switch-scope round 4. The three Verify_ facts that fail are defects the verifier found, left failing on
// purpose; the two Restated_ facts pass and replace round 3's scratch attacks T2 and T3, which the rulings overturned.
//
// Rulings for round 4 (reversible, the coordinator's): an on never reaches a model Semanticus can tell apart; a clearer
// known by nothing clears for everyone and the row says so; a title-only clearer clears for its title and for models known
// by nothing; a path-known clearer clears for title-only models with its title and for models known by nothing (ruling 1).
// A file Desktop names by its path keeps its own off, the row says why, and an on's row says only what it really did
// (ruling 2). A local Analysis Services model is not a Power BI Desktop model (LOW B).
//
// 1. An on for east\Sales.pbix switches the point back on for west\Sales.pbix, another file with the same name that
//    Semanticus tells apart by its path. The off both read was left by Sales opened from Desktop's recent list (the
//    title only). The path-known clearer takes that writer out of the title file for every reader, so west reads on and
//    is asked after the next yes, while the row says "models Semanticus cannot tell apart from this one".
// 2. An on made when Desktop names nothing clears for everyone (ruling 1), and its row says so, then adds: "A file
//    Desktop opens by double-click keeps its own switch and stays off until you switch it on there." Stock.pbix,
//    double-clicked, whose off was the shared one, reads on and is asked after the next yes. The row tells the person
//    the opposite of what happened to it.
// 3. The Microsoft Store Power BI Desktop keeps its workspaces under %USERPROFILE%\Microsoft\Power BI Desktop Store App,
//    which the new discriminator does not read, so a Store-app Desktop model is stamped as Analysis Services: its off is
//    kept only in the file for that run of Desktop, and Desktop gives a new port and database every start. After Desktop
//    restarts the point reads on, with no reason shown, and the next yes sends. At fd444611 every local open was stamped a
//    Desktop model and the off held. The installer version is the control and holds.
//
// The facts do not choose the fix. 1 passes when west keeps its off. 2 passes when the row stops saying that a
// double-clicked file stays off where one did not (or Stock keeps its off). 3 passes when a Store-app Desktop model keeps
// its off across a restart, whichever way the discriminator learns it is Desktop.

using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Semanticus.Decisions;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    public sealed class DecisionSwitchScopeVerifyR4Tests
    {
        private const string Point = DecisionRegistry.MeasureFolderAndFormat;
        private const string FileOff = "Off because it was switched off for this file.";
        /// <summary>The clause fact 2 is about, as the row says it at 481b6fdf.</summary>
        private const string DoubleClickStaysOff = "A file Desktop opens by double-click keeps its own switch and stays off until you switch it on there.";

        /// <summary>A Desktop model the way open_local stamps one Desktop can name: the title and path are whatever
        /// LocalDesktop.TryGetIdentity read, and Desktop picks a new port and database every start.</summary>
        private static async Task OpenDesktopAsync(DecisionFixture f, string port, string title = null, string path = null)
        {
            await f.Engine.CreateModelAsync(title ?? "Model", 1604, discardUnsaved: true);
            f.Sessions.Current.LiveOrigin = new LiveOrigin("localhost:" + port, Guid.NewGuid().ToString(), null,
                localName: title, localPath: path, localDesktop: true);
        }

        private static Task<DecisionPointInfo[]> SetAsync(DecisionFixture f, string door, bool enabled)
            => door == "studio" ? new EngineRpcTarget(f.Engine).setDecisionPointEnabled(Point, enabled)
             : McpTools.SetDecisionPointEnabled(f.Engine, Point, enabled);

        private static async Task<DecisionPointInfo> RowAsync(DecisionFixture f, string door)
        {
            var points = door == "studio" ? await new EngineRpcTarget(f.Engine).getDecisionPoints() : await McpTools.GetDecisionPoints(f.Engine);
            var row = points.Single(p => p.Name == Point);
            Assert.Equal(row.Enabled, f.Engine.DecisionPointEnabled(Point));
            return row;
        }

        /// <summary>The person's yes on the permission card, then the one ask this point makes. True when it was asked.</summary>
        private static async Task<bool> AskedAfterTheYesAsync(DecisionFixture f)
        {
            await f.Engine.CreateTableAsync("Sales", "human");
            await f.Engine.CreateColumnAsync("table:Sales", "Amount", "Double", null, "human");
            await f.Engine.CreateMeasureAsync("table:Sales", "Order Count", "COUNTROWS ( Sales )", "human", "Volume");
            var measure = await f.Engine.CreateMeasureAsync("table:Sales", "Total Sales", "SUM ( Sales[Amount] )", "human");
            await f.ConsentAsync();
            f.UseEmptyRecorded();
            return (await McpTools.ProposeMeasureDefaults(f.Engine, measure)).Asked;
        }

        // ---- 1: the title file ----------------------------------------------------------------------------------------

        // ---- 2: what the row of an on made when Desktop names nothing says about double-clicked files -----------------

        [Theory]
        [InlineData("old", "studio")]
        [InlineData("unnamed", "agent")]
        public async Task Verify_an_on_that_turns_Stock_pbix_back_on_does_not_say_a_double_clicked_file_stays_off(string off, string door)
        {
            using var f = new DecisionFixture();
            var stock = Path.Combine(f.Home, "west", "Stock.pbix");
            if (off == "old")
            {
                // Anyone who switched a point off before each model kept its own switches has this in the workspace file.
                var shared = Path.Combine(f.Workspace, LayoutStore.DirName, LayoutStore.DecisionSettingsFile);
                Directory.CreateDirectory(Path.GetDirectoryName(shared));
                File.WriteAllText(shared, "{\"points\":{\"" + Point + "\":{\"enabled\":false}}}");
            }
            else
            {
                await OpenDesktopAsync(f, "52000");                        // some Desktop file, opened when Desktop named nothing
                await McpTools.SetDecisionPointEnabled(f.Engine, Point, false);
            }
            await OpenDesktopAsync(f, "52001", "Stock", stock);            // Stock.pbix double-clicked reads that off
            Assert.False((await RowAsync(f, door)).Enabled);

            await OpenDesktopAsync(f, "52002");                            // a Desktop model known by nothing, switched on
            Assert.False((await RowAsync(f, door)).Enabled);
            var note = (await SetAsync(f, door, true)).Single(p => p.Name == Point).Note;
            Assert.NotNull(note);                                          // ruling 1: the row says so

            await OpenDesktopAsync(f, "52003", "Stock", stock);            // Stock.pbix double-clicked again
            var stockOn = (await RowAsync(f, door)).Enabled;
            var asked = await AskedAfterTheYesAsync(f);
            Assert.False(stockOn && note.Contains(DoubleClickStaysOff),
                "Stock.pbix, opened by double-click, reads on and its measure was " + (asked ? "sent" : "not sent")
                + " after the yes, while the row told the person: " + note);
        }

        // ---- 3: the Microsoft Store Power BI Desktop ------------------------------------------------------------------

        /// <summary>A scratch Windows profile. The installer Desktop keeps its workspaces under %LOCALAPPDATA%\Microsoft\Power
        /// BI Desktop\AnalysisServicesWorkspaces, the Store app under %USERPROFILE%\Microsoft\Power BI Desktop Store
        /// App\AnalysisServicesWorkspaces. Each workspace holds Data\msmdsrv.port.txt with the port it listens on.</summary>
        private static string InstallerRoot(string profile) => Path.Combine(profile, "AppData", "Local", "Microsoft", "Power BI Desktop", "AnalysisServicesWorkspaces");
        private static string StoreRoot(string profile) => Path.Combine(profile, "Microsoft", "Power BI Desktop Store App", "AnalysisServicesWorkspaces");

        private static string DesktopStarts(string root, int port)
        {
            var ws = Path.Combine(root, "AnalysisServicesWorkspace_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(ws, "Data"));
            File.WriteAllText(Path.Combine(ws, "Data", "msmdsrv.port.txt"), port.ToString(), Encoding.Unicode);
            return ws;
        }

        /// <summary>What open_local stamps (LocalEngine.cs:3247) on the machine whose profile folder is <paramref name="profile"/>.
        /// This is the one line that names the discriminator's test seam as it is at 481b6fdf (the folder it reads with a null
        /// root). A fix that widens the seam, for example to the profile folder or a list of Desktop folders, changes only
        /// this line, so that the same scratch profile is read.</summary>
        private static LiveOrigin StampLocalOpen(string profile, int port)
            => LocalEngine.LocalOriginFor("localhost:" + port, Guid.NewGuid().ToString(), null, InstallerRoot(profile));

        [Theory]
        [InlineData("store", "studio")]
        [InlineData("store", "agent")]
        [InlineData("installer", "studio")]   // the control: the same journey on the installer version holds
        public async Task Verify_an_off_set_in_Power_BI_Desktop_holds_after_Desktop_restarts(string app, string door)
        {
            using var f = new DecisionFixture();
            var profile = Path.Combine(f.Home, "profile");
            var root = app == "store" ? StoreRoot(profile) : InstallerRoot(profile);

            var first = DesktopStarts(root, 52555);                        // day 1: Desktop has Sales.pbix open
            await f.Engine.CreateModelAsync("Sales", 1604, discardUnsaved: true);
            f.Sessions.Current.LiveOrigin = StampLocalOpen(profile, 52555);
            Assert.True((await RowAsync(f, door)).Enabled);
            Assert.False((await SetAsync(f, door, false)).Single(p => p.Name == Point).Enabled);
            Directory.Delete(first, true);                                 // Desktop closes, and its workspace goes with it

            DesktopStarts(root, 52556);                                    // day 2: Desktop opens Sales.pbix again
            await f.Engine.CreateModelAsync("Sales", 1604, discardUnsaved: true);
            f.Sessions.Current.LiveOrigin = StampLocalOpen(profile, 52556);
            var day2 = await RowAsync(f, door);
            var asked = await AskedAfterTheYesAsync(f);
            Assert.False(day2.Enabled, "The person switched the point off in the " + app + " version of Power BI Desktop. After Desktop "
                + "restarted it read on" + (day2.Note == null ? " with no reason shown" : ": " + day2.Note) + ", and after the yes the measure was "
                + (asked ? "sent" : "not sent") + ".");
            Assert.False(asked);
        }

        // ---- round 3's T2 and T3, restated to ruling 2 (these pass) ----------------------------------------------------

    }
}
