// verify: m11/switch-scope round 6. The Verify_ facts here are defects the verifier found, left failing on purpose.
//
// Round 6's F2 fix (LocalDesktop.IdentityOf, TitleNames) keeps the path on Desktop's command line only when the window
// title names that file. When the title cannot be read as a name (the builder's own example: a preview build's
// "Sales - Power BI Desktop (Vorschau)"), it keeps the path whenever the raw title starts with the file's name plus " - ".
// A file whose name starts with another file's name plus " - " (Windows' own "Sales - Copy.pbix", or "Sales - EU.pbix")
// then passes that test for the other file. So on a build whose titles Semanticus cannot read, a Desktop started by
// double-clicking Sales.pbix, in which the person then opens Sales - EU.pbix, is still kept under Sales.pbix's own file:
// the round 5 F2 harm, through the branch round 6 added.
//
// 1a. An on for Sales - EU in that window switches Sales.pbix, double-clicked the next day, back on over its own off.
// 1b. An off for Sales - EU in that window never reaches Sales - EU.pbix double-clicked the next day.
//
// 2. The larger hole (found by the Opus advisor's second read): the title holds a file's name, not its folder. A Desktop
//    started by double-clicking east\Sales.pbix, in which the person opens west\Sales.pbix, shows the ordinary title
//    "Sales - Power BI Desktop", which names the command line's file too. No title test can tell them apart, so the window
//    is kept under east\Sales.pbix: an on for west switches east back on (2a), and an off for west never reaches
//    west\Sales.pbix double-clicked (2b). Round 5's premise alone, with a readable title and a same-named copy in another
//    folder, which is what the path rung exists for.
// The premise is the round 5 one (a third party's report that Desktop keeps the first file on its command line), plus a
// title Semanticus cannot read, which the product itself allows for. The process lists go through LocalDesktop.Resolve,
// the ladder open_local uses, so the stamp is the product's own. The facts do not choose the fix: they pass when a
// window whose title names another file (with or without a suffix Semanticus cannot read) does not keep the path of the
// file Desktop started with. 1a says nothing when a fix drops the path under every unreadable title, since Sales.pbix is
// then known by nothing and the ruling shares that identity's ons; 2a likewise says nothing when a fix stops knowing
// east\Sales.pbix by its path. 2 passes when the window showing west is not kept under east's file.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Semanticus.Decisions;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    public sealed class DecisionSwitchScopeVerifyR6Tests
    {
        private const string Point = DecisionRegistry.MeasureFolderAndFormat;

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

        // ---- a scratch Windows machine: one profile, Desktop's workspace folders, and the process list -------------------

        private sealed class Machine
        {
            public string Profile, LocalAppData, DesktopWorkspace;
            public int Port = 53000;
            public string InstallerRoot => Path.Combine(LocalAppData, "Microsoft", "Power BI Desktop", "AnalysisServicesWorkspaces");
        }

        private static Machine NewMachine(DecisionFixture f)
        {
            var m = new Machine { Profile = Path.Combine(f.Home, "profile") };
            m.LocalAppData = Path.Combine(m.Profile, "AppData", "Local");
            m.DesktopWorkspace = DesktopStarts(m.InstallerRoot, 52999);   // a Power BI Desktop is running, on port 52999
            return m;
        }

        private static string DesktopStarts(string root, int port)
        {
            var ws = Path.Combine(root, "AnalysisServicesWorkspace_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(ws, "Data"));
            File.WriteAllText(Path.Combine(ws, "Data", "msmdsrv.port.txt"), port.ToString(), Encoding.Unicode);
            return ws;
        }

        /// <summary>What open_local stamps on this machine: no server runs, only Desktops.</summary>
        private static LiveOrigin Stamp(Machine m, string dataSource, string database, LocalDesktop.DesktopIdentity id)
            => LocalEngine.LocalOriginFor(dataSource, database, id, null, LocalDesktop.WorkspaceRoots(m.LocalAppData, m.Profile), () => new List<LocalDesktop.ProcInfo>());

        private const string SalesPbix = "C:\\Reports\\Sales.pbix";
        private const string EastSales = "C:\\east\\Sales.pbix";
        private const string WestSales = "C:\\west\\Sales.pbix";
        private const string SalesEuPbix = "C:\\Reports\\Sales - EU.pbix";
        /// <summary>The suffix a build whose titles Semanticus cannot read puts after the file name (the builder's own example).</summary>
        private const string UnreadableSuffix = " - Power BI Desktop (Vorschau)";

        /// <summary>A Desktop run whose command line names <paramref name="launched"/> and whose window title is
        /// <paramref name="title"/>, stamped the way open_local stamps it (LocalDesktop.Resolve over the process list).</summary>
        private static async Task OpenDesktopWindowAsync(DecisionFixture f, Machine m, string launched, string title, string modelName)
        {
            var port = m.Port++;
            var ws = DesktopStarts(m.InstallerRoot, port);
            var id = LocalDesktop.Resolve(ws, new List<LocalDesktop.ProcInfo>
            {
                new LocalDesktop.ProcInfo { Pid = 30, ParentPid = 1, Name = "PBIDesktop.exe", Title = title,
                    CommandLine = "\"C:\\Program Files\\Microsoft Power BI Desktop\\bin\\PBIDesktop.exe\" \"" + launched + "\"" },
                new LocalDesktop.ProcInfo { Pid = 31, ParentPid = 30, Name = "msmdsrv.exe", CommandLine = "\"msmdsrv.exe\" -c -s \"" + ws + "\\Data\"" },
            });
            await f.Engine.CreateModelAsync(modelName, 1604, discardUnsaved: true);
            f.Sessions.Current.LiveOrigin = Stamp(m, "localhost:" + port, Guid.NewGuid().ToString(), id);
            Assert.True(f.Sessions.Current.LiveOrigin.LocalDesktop);
        }

        [Theory]
        [InlineData("studio")]
        [InlineData("agent")]
        public async Task Verify_an_on_for_Sales_EU_opened_in_a_window_Sales_pbix_started_leaves_Sales_pbix_off(string door)
        {
            using var f = new DecisionFixture();
            var m = NewMachine(f);
            await OpenDesktopWindowAsync(f, m, SalesPbix, "Sales" + UnreadableSuffix, "Sales");         // day 1: Sales.pbix double-clicked
            await McpTools.SetDecisionPointEnabled(f.Engine, Point, false);                              // switched off
            await OpenDesktopWindowAsync(f, m, SalesPbix, "Sales - EU" + UnreadableSuffix, "Sales - EU"); // day 2: then Sales - EU opened in that window
            var euBefore = await RowAsync(f, door);
            var note = (await SetAsync(f, door, true)).Single(p => p.Name == Point).Note;               // switched on for Sales - EU

            await OpenDesktopWindowAsync(f, m, SalesPbix, "Sales" + UnreadableSuffix, "Sales");         // day 3: Sales.pbix double-clicked
            // A fix that stops trusting the path under every title it cannot read makes Sales.pbix itself a Desktop model known
            // by nothing, whose ons the ruling shares (item 5): the fact then has nothing to say. It speaks while Sales.pbix
            // is still known by its path, as it is at 6460440b.
            if (f.Sessions.Current.LiveOrigin.LocalPath == null) return;
            var sales = await RowAsync(f, door);
            var asked = await AskedAfterTheYesAsync(f);
            Assert.False(sales.Enabled, "An on for Sales - EU, opened in a Desktop window that Sales.pbix started, switched the point back on "
                + "for Sales.pbix, whose own off it was; after the yes Sales.pbix's measure was " + (asked ? "sent" : "not sent")
                + ". Before the on, the Sales - EU row read " + (euBefore.Enabled ? "on" : "off (" + euBefore.Note + ")")
                + "; the on's row said: " + (note ?? "nothing"));
            Assert.False(asked);
        }

        [Theory]
        [InlineData("studio")]
        [InlineData("agent")]
        public async Task Verify_an_off_for_Sales_EU_opened_in_a_window_Sales_pbix_started_reaches_Sales_EU_pbix(string door)
        {
            using var f = new DecisionFixture();
            var m = NewMachine(f);
            await OpenDesktopWindowAsync(f, m, SalesPbix, "Sales - EU" + UnreadableSuffix, "Sales - EU"); // day 1: Sales.pbix, then Sales - EU in that window
            await SetAsync(f, door, false);                                                              // switched off for Sales - EU

            await OpenDesktopWindowAsync(f, m, SalesEuPbix, "Sales - EU" + UnreadableSuffix, "Sales - EU"); // day 2: Sales - EU.pbix double-clicked
            var eu = await RowAsync(f, door);
            var asked = await AskedAfterTheYesAsync(f);
            Assert.False(eu.Enabled, "The person switched the point off for Sales - EU in a Desktop window that Sales.pbix started. "
                + "Sales - EU.pbix, double-clicked the next day, reads on" + (eu.Note == null ? " with no reason shown" : ": " + eu.Note)
                + ", and after the yes its measure was " + (asked ? "sent" : "not sent") + ".");
            Assert.False(asked);
        }
        [Theory]
        [InlineData("studio")]
        [InlineData("agent")]
        public async Task Verify_an_on_for_west_Sales_pbix_opened_in_a_window_east_Sales_pbix_started_leaves_east_off(string door)
        {
            using var f = new DecisionFixture();
            var m = NewMachine(f);
            await OpenDesktopWindowAsync(f, m, EastSales, "Sales - Power BI Desktop", "Sales");    // day 1: east\Sales.pbix double-clicked
            await McpTools.SetDecisionPointEnabled(f.Engine, Point, false);                         // switched off
            await OpenDesktopWindowAsync(f, m, EastSales, "Sales - Power BI Desktop", "Sales");    // day 2: east double-clicked, then west\Sales.pbix opened in that window
            var westBefore = await RowAsync(f, door);
            var note = (await SetAsync(f, door, true)).Single(p => p.Name == Point).Note;          // switched on for west

            await OpenDesktopWindowAsync(f, m, EastSales, "Sales - Power BI Desktop", "Sales");    // day 3: east\Sales.pbix double-clicked
            if (f.Sessions.Current.LiveOrigin.LocalPath == null) return;                           // a fix that no longer knows east by its path
            var east = await RowAsync(f, door);
            var asked = await AskedAfterTheYesAsync(f);
            Assert.False(east.Enabled, "An on for west\\Sales.pbix, opened in a Desktop window that east\\Sales.pbix started, switched the point "
                + "back on for east\\Sales.pbix, whose own off it was; after the yes its measure was " + (asked ? "sent" : "not sent")
                + ". Before the on, the west row read " + (westBefore.Enabled ? "on" : "off (" + westBefore.Note + ")")
                + "; the on's row said: " + (note ?? "nothing"));
            Assert.False(asked);
        }

        [Theory]
        [InlineData("studio")]
        [InlineData("agent")]
        public async Task Verify_an_off_for_west_Sales_pbix_opened_in_a_window_east_Sales_pbix_started_reaches_west(string door)
        {
            using var f = new DecisionFixture();
            var m = NewMachine(f);
            await OpenDesktopWindowAsync(f, m, EastSales, "Sales - Power BI Desktop", "Sales");    // day 1: east double-clicked, then west\Sales.pbix opened in that window
            await SetAsync(f, door, false);                                                         // switched off for west

            await OpenDesktopWindowAsync(f, m, WestSales, "Sales - Power BI Desktop", "Sales");    // day 2: west\Sales.pbix double-clicked
            var west = await RowAsync(f, door);
            var asked = await AskedAfterTheYesAsync(f);
            Assert.False(west.Enabled, "The person switched the point off for west\\Sales.pbix in a Desktop window that east\\Sales.pbix started. "
                + "west\\Sales.pbix, double-clicked the next day, reads on" + (west.Note == null ? " with no reason shown" : ": " + west.Note)
                + ", and after the yes its measure was " + (asked ? "sent" : "not sent") + ".");
            Assert.False(asked);
        }
    }
}
