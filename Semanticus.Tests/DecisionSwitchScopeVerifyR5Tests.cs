// verify: m11/switch-scope round 5. The Verify_ facts here are defects the verifier found, left failing on purpose.
//
// Ruling for round 5 (reversible, the coordinator's): ons never clear. An off may be shared with every model the clicker
// cannot be told apart from; an on is kept only under the clicker's own identity and changes no other identity; the row
// says exactly what happened, and no note claims more; a Store-app or Report Server Desktop is Desktop, and a local
// instance that cannot be placed with certainty is a Desktop model known by nothing, never Analysis Services.
//
// 1. A real Analysis Services database opened while any Power BI Desktop runs is stamped a Desktop model known by nothing
//    (the builder's decision 1: a server is proved only when no msmdsrv on the machine has a Desktop parent, whichever
//    port it listens on). Its on is then kept under the known-by-nothing identity, so it switches the point back on for
//    every Desktop model known by nothing, including one that reads Stock.pbix's off and may be Stock.pbix (1a). The
//    other way, an on for a Desktop model known by nothing switches the Analysis Services model back on over the off the
//    person set for every model (1b). Both are an on reaching a model Semanticus can tell apart: the server's port belongs
//    to no Desktop, which the process list and the Desktop's own port file already show.
// 2. An on made from Desktop's recent list says "On for Power BI Desktop files with this name", while Sales.pbix opened by
//    double-click, a file with this name, stays off. The note claims more than happened (ruling item 4, M1's class).
// 3. A double-clicked file switched off and then back on says nothing, while the same file opened from Desktop's recent
//    list, and every Desktop model known by nothing, stay off because of the off it shared. The ruling's own sentence
//    ("Other Power BI Desktop files that were switched off together with it stay off until you switch them on there.")
//    describes exactly this, and the row is silent (the builder's decision 3).
// 4. Outside the round-5 matrix, older than this round: a Desktop started by double-clicking Sales.pbix keeps Sales.pbix on
//    its command line when the person opens Stock.pbix in the same window (reported for Windows 11 in pbi-cli issue 25,
//    27 Sep 2026; not seen on a real Desktop here). The identity ladder takes the path from the command line and the name
//    from the window title without comparing them, so Stock is kept under Sales.pbix's own file: an on for Stock switches
//    Sales.pbix back on (4a), and an off for Stock never reaches Stock.pbix opened by double-click (4b). The process lists
//    below go through LocalDesktop.Resolve, the ladder open_local uses, so the stamp is the product's own.
//
// The facts do not choose the fix. 1 passes when neither on reaches the other model, whichever way the server is placed.
// 2 passes when the words change (they are then read against the matrix by eye) or a double-clicked Sales.pbix is not
// left off. 3 passes when the row says something whenever the views that read this file's off stay off. 4 passes when
// Stock is not kept under Sales.pbix's file, for example when a window title that names another file drops the path.

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
    public sealed class DecisionSwitchScopeVerifyR5Tests
    {
        private const string Point = DecisionRegistry.MeasureFolderAndFormat;
        /// <summary>The words fact 2 is about, as the row says them at f788ee9b.</summary>
        private const string OnForFilesWithThisName = "On for Power BI Desktop files with this name. Other Power BI Desktop files that were switched off together with it stay off until you switch them on there.";

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

        /// <summary>What the process list shows while that Desktop runs beside a real Analysis Services service on 2383:
        /// Desktop and its msmdsrv (whose command line names Desktop's workspace), and the service's msmdsrv.</summary>
        private static List<LocalDesktop.ProcInfo> DesktopBesideAService(Machine m) => new List<LocalDesktop.ProcInfo>
        {
            new LocalDesktop.ProcInfo { Pid = 10, ParentPid = 1, Name = "PBIDesktop.exe", Title = "Sales - Power BI Desktop" },
            new LocalDesktop.ProcInfo { Pid = 11, ParentPid = 10, Name = "msmdsrv.exe", CommandLine = "\"msmdsrv.exe\" -c -s \"" + m.DesktopWorkspace + "\\Data\"" },
            new LocalDesktop.ProcInfo { Pid = 20, ParentPid = 4, Name = "msmdsrv.exe",
                CommandLine = "\"C:\\Program Files\\Microsoft SQL Server\\MSAS16.MSSQLSERVER\\OLAP\\bin\\msmdsrv.exe\" -s \"C:\\Program Files\\Microsoft SQL Server\\MSAS16.MSSQLSERVER\\OLAP\\Config\"" },
        };

        /// <summary>What open_local stamps (LocalEngine.cs:3247) on this machine. This is the one line that names the
        /// placement seam as it is at f788ee9b; a fix that reads more (for example which process owns the port) changes only
        /// this line, so that the same machine is read.</summary>
        private static LiveOrigin Stamp(Machine m, string dataSource, string database, LocalDesktop.DesktopIdentity id)
            => LocalEngine.LocalOriginFor(dataSource, database, id, null, LocalDesktop.WorkspaceRoots(m.LocalAppData, m.Profile), () => DesktopBesideAService(m));

        /// <summary>A Power BI Desktop model, opened the way Desktop names it (a path, a title, or nothing), in a new run.</summary>
        private static async Task OpenDesktopAsync(DecisionFixture f, Machine m, string title = null, string path = null)
        {
            var port = m.Port++;
            DesktopStarts(m.InstallerRoot, port);
            await f.Engine.CreateModelAsync(title ?? "Model", 1604, discardUnsaved: true);
            // The join (item 5, 30 Sep): a Desktop identity carries no path; the path argument is kept for the callers' reading.
            _ = path;
            var id = title == null ? null : new LocalDesktop.DesktopIdentity { Stem = title };
            f.Sessions.Current.LiveOrigin = Stamp(m, "localhost:" + port, Guid.NewGuid().ToString(), id);
            Assert.True(f.Sessions.Current.LiveOrigin.LocalDesktop);
        }

        /// <summary>The Analysis Services database "Finance" on the service at localhost:2383, opened with open_local.</summary>
        private static async Task OpenServerAsync(DecisionFixture f, Machine m)
        {
            await f.Engine.CreateModelAsync("Finance", 1604, discardUnsaved: true);
            f.Sessions.Current.LiveOrigin = Stamp(m, "localhost:2383", "Finance", null);
        }

        // ---- 1: a real Analysis Services model opened while Power BI Desktop runs ------------------------------------------

        [Theory]
        [InlineData("studio")]
        [InlineData("agent")]
        public async Task Verify_an_on_for_an_Analysis_Services_model_leaves_a_Desktop_model_known_by_nothing_off(string door)
        {
            using var f = new DecisionFixture();
            var m = NewMachine(f);
            await OpenDesktopAsync(f, m, "Stock", Path.Combine(f.Home, "north", "Stock.pbix"));   // Stock.pbix double-clicked, switched off
            await McpTools.SetDecisionPointEnabled(f.Engine, Point, false);
            await OpenDesktopAsync(f, m);                                   // a Desktop model known by nothing: it may be Stock.pbix
            Assert.False((await RowAsync(f, door)).Enabled);

            await OpenServerAsync(f, m);                                    // the Finance database on the Analysis Services service
            var note = (await SetAsync(f, door, true)).Single(p => p.Name == Point).Note;   // switched on for Finance

            await OpenDesktopAsync(f, m);                                   // the Desktop model known by nothing, the next day
            var row = await RowAsync(f, door);
            var asked = await AskedAfterTheYesAsync(f);
            Assert.False(row.Enabled, "An on for the Analysis Services database Finance switched the point back on for a Power BI Desktop "
                + "model known by nothing, which reads Stock.pbix's off and may be Stock.pbix; after the yes its measure was "
                + (asked ? "sent" : "not sent") + ". The row for Finance said: " + (note ?? "nothing"));
            Assert.False(asked);
        }

        [Theory]
        [InlineData("studio")]
        [InlineData("agent")]
        public async Task Verify_an_on_for_a_Desktop_model_known_by_nothing_leaves_an_Analysis_Services_model_off(string door)
        {
            using var f = new DecisionFixture();
            var m = NewMachine(f);
            // Anyone who switched a point off before each model kept its own switches has this in the workspace file, and it
            // holds for every model here.
            var shared = Path.Combine(f.Workspace, LayoutStore.DirName, LayoutStore.DecisionSettingsFile);
            Directory.CreateDirectory(Path.GetDirectoryName(shared));
            File.WriteAllText(shared, "{\"points\":{\"" + Point + "\":{\"enabled\":false}}}");
            await OpenServerAsync(f, m);
            Assert.False((await RowAsync(f, door)).Enabled);

            await OpenDesktopAsync(f, m);                                   // a Desktop model known by nothing, switched on
            var note = (await SetAsync(f, door, true)).Single(p => p.Name == Point).Note;

            await OpenServerAsync(f, m);                                    // Finance again
            var row = await RowAsync(f, door);
            var asked = await AskedAfterTheYesAsync(f);
            Assert.False(row.Enabled, "An on for a Power BI Desktop model known by nothing switched the point back on for the Analysis "
                + "Services database Finance, whose off was set for every model; after the yes its measure was " + (asked ? "sent" : "not sent")
                + ". The Desktop row said: " + (note ?? "nothing") + ". Finance now says: " + (row.Note ?? "nothing"));
            Assert.False(asked);
        }

        // ---- 2: what the row of an on made from Desktop's recent list says ---------------------------------------------

        [Theory]
        [InlineData("studio")]
        [InlineData("agent")]
        public async Task Verify_an_on_from_the_recent_list_does_not_say_files_with_this_name_are_on_while_a_double_clicked_one_stays_off(string door)
        {
            using var f = new DecisionFixture();
            var m = NewMachine(f);
            var east = Path.Combine(f.Home, "east", "Sales.pbix");
            await OpenDesktopAsync(f, m, "Sales");                          // day 1: Sales from Desktop's recent list, switched off
            await McpTools.SetDecisionPointEnabled(f.Engine, Point, false);
            await OpenDesktopAsync(f, m, "Sales");                          // day 2: Sales from the recent list again, switched on
            Assert.False((await RowAsync(f, door)).Enabled);
            var note = (await SetAsync(f, door, true)).Single(p => p.Name == Point).Note;

            await OpenDesktopAsync(f, m, "Sales", east);                    // day 3: east\Sales.pbix double-clicked
            var eastRow = await RowAsync(f, door);
            Assert.False(note == OnForFilesWithThisName && !eastRow.Enabled,
                "The row said \"" + note + "\", and east\\Sales.pbix, a Power BI Desktop file with this name, reads off: " + eastRow.Note);
        }

        // ---- 3: the row of an on after the file's own off ----------------------------------------------------------------

        [Theory]
        [InlineData("studio")]
        [InlineData("agent")]
        public async Task Verify_an_on_after_this_file_s_own_off_says_that_the_views_it_was_shared_with_stay_off(string door)
        {
            using var f = new DecisionFixture();
            var m = NewMachine(f);
            var east = Path.Combine(f.Home, "east", "Sales.pbix");
            await OpenDesktopAsync(f, m, "Sales", east);                    // east\Sales.pbix double-clicked
            await SetAsync(f, door, false);
            var on = (await SetAsync(f, door, true)).Single(p => p.Name == Point);   // and switched straight back on
            Assert.True(on.Enabled);

            await OpenDesktopAsync(f, m, "Sales");                          // Sales from Desktop's recent list (it may be east)
            var recent = await RowAsync(f, door);
            await OpenDesktopAsync(f, m);                                   // a Desktop model known by nothing
            var nameless = await RowAsync(f, door);
            Assert.False(on.Note == null && (!recent.Enabled || !nameless.Enabled),
                "east\\Sales.pbix was switched off and back on and its row said nothing, while Sales from Desktop's recent list reads "
                + (recent.Enabled ? "on" : "off (" + recent.Note + ")") + " and a Desktop model known by nothing reads "
                + (nameless.Enabled ? "on" : "off (" + nameless.Note + ")") + ".");
        }

        // ---- 4: a Desktop started by double-clicking Sales.pbix, then Stock.pbix opened in the same window ---------------

        private const string SalesPbix = "C:\\Reports\\Sales.pbix";
        private const string StockPbix = "C:\\Reports\\Stock.pbix";

        /// <summary>A Desktop run whose command line names <paramref name="launched"/> and whose window shows
        /// <paramref name="title"/>, stamped the way open_local stamps it: the identity comes from LocalDesktop.Resolve over
        /// the process list, the placement from Stamp.</summary>
        private static async Task OpenDesktopWindowAsync(DecisionFixture f, Machine m, string launched, string title)
        {
            var port = m.Port++;
            var ws = DesktopStarts(m.InstallerRoot, port);
            var id = LocalDesktop.Resolve(ws, new List<LocalDesktop.ProcInfo>
            {
                new LocalDesktop.ProcInfo { Pid = 30, ParentPid = 1, Name = "PBIDesktop.exe", Title = title + " - Power BI Desktop",
                    CommandLine = "\"C:\\Program Files\\Microsoft Power BI Desktop\\bin\\PBIDesktop.exe\" \"" + launched + "\"" },
                new LocalDesktop.ProcInfo { Pid = 31, ParentPid = 30, Name = "msmdsrv.exe", CommandLine = "\"msmdsrv.exe\" -c -s \"" + ws + "\\Data\"" },
            });
            Assert.NotNull(id);
            await f.Engine.CreateModelAsync(title, 1604, discardUnsaved: true);
            f.Sessions.Current.LiveOrigin = Stamp(m, "localhost:" + port, Guid.NewGuid().ToString(), id);
            Assert.True(f.Sessions.Current.LiveOrigin.LocalDesktop);
        }

        [Theory]
        [InlineData("studio")]
        [InlineData("agent")]
        public async Task Verify_an_on_for_Stock_opened_in_a_window_Sales_pbix_started_leaves_Sales_pbix_off(string door)
        {
            using var f = new DecisionFixture();
            var m = NewMachine(f);
            await OpenDesktopWindowAsync(f, m, SalesPbix, "Sales");         // day 1: Sales.pbix double-clicked, switched off
            await McpTools.SetDecisionPointEnabled(f.Engine, Point, false);
            await OpenDesktopWindowAsync(f, m, SalesPbix, "Stock");         // day 2: Sales.pbix double-clicked, then Stock opened in that window
            var stockBefore = await RowAsync(f, door);
            var note = (await SetAsync(f, door, true)).Single(p => p.Name == Point).Note;   // switched on for Stock

            await OpenDesktopWindowAsync(f, m, SalesPbix, "Sales");         // day 3: Sales.pbix double-clicked
            var sales = await RowAsync(f, door);
            var asked = await AskedAfterTheYesAsync(f);
            Assert.False(sales.Enabled, "An on for Stock, opened in a Desktop window that Sales.pbix started, switched the point back on for "
                + "Sales.pbix, whose own off it was; after the yes Sales.pbix's measure was " + (asked ? "sent" : "not sent")
                + ". Before the on, Stock's row read " + (stockBefore.Enabled ? "on" : "off (" + stockBefore.Note + ")")
                + "; the on's row said: " + (note ?? "nothing"));
            Assert.False(asked);
        }

        [Theory]
        [InlineData("studio")]
        [InlineData("agent")]
        public async Task Verify_an_off_for_Stock_opened_in_a_window_Sales_pbix_started_reaches_Stock_pbix(string door)
        {
            using var f = new DecisionFixture();
            var m = NewMachine(f);
            await OpenDesktopWindowAsync(f, m, SalesPbix, "Stock");         // day 1: Sales.pbix double-clicked, then Stock opened in that window
            await SetAsync(f, door, false);                                  // switched off for Stock

            await OpenDesktopWindowAsync(f, m, StockPbix, "Stock");         // day 2: Stock.pbix double-clicked
            var stock = await RowAsync(f, door);
            var asked = await AskedAfterTheYesAsync(f);
            Assert.False(stock.Enabled, "The person switched the point off for Stock in a Desktop window that Sales.pbix started. Stock.pbix, "
                + "double-clicked the next day, reads on" + (stock.Note == null ? " with no reason shown" : ": " + stock.Note)
                + ", and after the yes its measure was " + (asked ? "sent" : "not sent") + ".");
            Assert.False(asked);
        }
    }
}
