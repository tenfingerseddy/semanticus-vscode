// review-fix round 6 for m11/switch-scope: written first, red first. Narrow: an instance is placed by ITS OWN port, a Desktop
// command-line path is trusted only when its file name is the window's title, and each on note says what that view did.

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
    public sealed class DecisionSwitchScopeR6Tests
    {
        private const string Point = DecisionRegistry.MeasureFolderAndFormat;

        private static string Workspace(string root, int? port)
        {
            var ws = Path.Combine(root, "AnalysisServicesWorkspace_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(ws, "Data"));
            if (port != null) File.WriteAllText(Path.Combine(ws, "Data", "msmdsrv.port.txt"), port.Value.ToString(), Encoding.Unicode);
            return ws;
        }

        private static List<LocalDesktop.ProcInfo> Machine(string desktopWorkspace, bool service)
        {
            var list = new List<LocalDesktop.ProcInfo>();
            if (desktopWorkspace != null)
            {
                list.Add(new LocalDesktop.ProcInfo { Pid = 10, ParentPid = 1, Name = "PBIDesktop.exe", Title = "Sales - Power BI Desktop" });
                list.Add(new LocalDesktop.ProcInfo { Pid = 11, ParentPid = 10, Name = "msmdsrv.exe", CommandLine = "\"msmdsrv.exe\" -c -s \"" + desktopWorkspace + "\\Data\"" });
            }
            if (service)
                list.Add(new LocalDesktop.ProcInfo { Pid = 20, ParentPid = 4, Name = "msmdsrv.exe", CommandLine = "\"C:\\Program Files\\Microsoft SQL Server\\MSAS16.MSSQLSERVER\\OLAP\\bin\\msmdsrv.exe\" -s \"C:\\Program Files\\Microsoft SQL Server\\MSAS16.MSSQLSERVER\\OLAP\\Config\"" });
            return list;
        }

        [Fact]
        public void An_instance_is_placed_by_its_own_port_and_a_Desktop_on_another_port_changes_nothing()
        {
            var tmp = Path.Combine(Path.GetTempPath(), "sem-r6-" + Guid.NewGuid().ToString("N"));
            try
            {
                var elsewhere = Path.Combine(tmp, "elsewhere");
                var known = Path.Combine(tmp, "known");
                var desktop = Workspace(elsewhere, 52999);
                var roots = new[] { known };
                // Analysis Services on 2383 beside a Desktop that listens on 52999: a server.
                Assert.Equal(LocalDesktop.Placement.Server, LocalDesktop.PlaceOf("localhost:2383", roots, () => Machine(desktop, service: true)));
                // The same Desktop on the SAME port, in a folder this build does not know: it is that Desktop.
                Assert.Equal(LocalDesktop.Placement.Unplaced, LocalDesktop.PlaceOf("localhost:52999", roots, () => Machine(desktop, service: true)));
                // A Desktop whose port cannot be read might be this instance.
                var starting = Workspace(elsewhere, null);
                Assert.Equal(LocalDesktop.Placement.Unplaced, LocalDesktop.PlaceOf("localhost:2383", roots, () => Machine(starting, service: true)));
                // Its own process cannot be found: only a Desktop's msmdsrv runs, on another port.
                Assert.Equal(LocalDesktop.Placement.Unplaced, LocalDesktop.PlaceOf("localhost:2383", roots, () => Machine(desktop, service: false)));
                // No Desktop runs and a server does.
                Assert.Equal(LocalDesktop.Placement.Server, LocalDesktop.PlaceOf("localhost:2383", roots, () => Machine(null, service: true)));
                // A command line with no quotes still names the workspace.
                var bare = Machine(desktop, service: true);
                bare[1].CommandLine = "msmdsrv.exe -c -s " + desktop + "\\Data";
                Assert.Equal(LocalDesktop.Placement.Server, LocalDesktop.PlaceOf("localhost:2383", roots, () => bare));
            }
            finally { try { Directory.Delete(tmp, true); } catch { } }
        }

        private static LocalDesktop.DesktopIdentity Identity(string launched, string title)
        {
            var ws = "C:\\Users\\k\\AppData\\Local\\Microsoft\\Power BI Desktop\\AnalysisServicesWorkspaces\\AnalysisServicesWorkspace_7";
            return LocalDesktop.Resolve(ws, new List<LocalDesktop.ProcInfo>
            {
                new LocalDesktop.ProcInfo { Pid = 1, ParentPid = 0, Name = "PBIDesktop.exe", Title = title,
                    CommandLine = launched == null ? "\"C:\\Program Files\\Microsoft Power BI Desktop\\bin\\PBIDesktop.exe\"" : "\"C:\\Program Files\\Microsoft Power BI Desktop\\bin\\PBIDesktop.exe\" \"" + launched + "\"" },
                new LocalDesktop.ProcInfo { Pid = 2, ParentPid = 1, Name = "msmdsrv.exe", CommandLine = "msmdsrv.exe -c -s \"" + ws + "\\Data\"" },
            });
        }

        // Rewritten at the join (item 5, the coordinator's brief, 30 Sep). This fact was "a command-line path is kept only when
        // its file name is the window title"; the ruling keeps no command-line path at all, so the title alone decides.
        [Fact]
        public void The_window_title_alone_decides_and_a_command_line_path_is_never_kept()
        {
            Assert.Equal("Sales", Identity("C:\\Reports\\Sales.pbix", "Sales - Power BI Desktop").Stem);
            Assert.Equal("SALES", Identity("C:\\Reports\\sales.pbix", "SALES - Power BI Desktop").Stem);
            Assert.Equal("Stock", Identity("C:\\Reports\\Sales.pbix", "Stock - Power BI Desktop").Stem);
            Assert.Equal("Sales - EU", Identity("C:\\Reports\\Sales - EU.pbix", "Sales - EU - Power BI Desktop").Stem);
            Assert.Null(Identity("C:\\Reports\\Sales.pbix", null));                                    // no title: known by nothing
            Assert.Null(Identity("C:\\Reports\\Sales.pbix", "Sales - Power BI Desktop (Vorschau)"));   // a title Semanticus cannot read
            Assert.Null(Identity("C:\\Reports\\Sales.pbix", "Stock - Vorschau"));
        }

        private static async Task<DecisionPointInfo> OnAsync(DecisionFixture f, string title, string path)
        {
            await f.Engine.CreateModelAsync(title ?? "Model", 1604, discardUnsaved: true);
            f.Sessions.Current.LiveOrigin = new LiveOrigin("localhost:" + (54000 + Math.Abs(Guid.NewGuid().GetHashCode() % 900)), Guid.NewGuid().ToString(), null,
                localName: title, localPath: path, localDesktop: true);
            return null;
        }

        [Theory]
        [InlineData("Sales", true, LocalEngine.OnForThisNameAfterItsOwnOffNote)]    // the join (item 5): a path is ignored, the title decides
        [InlineData("Sales", false, LocalEngine.OnForThisNameAfterItsOwnOffNote)]
        [InlineData(null, false, LocalEngine.OnForNoNameAfterItsOwnOffNote)]
        public async Task An_on_after_the_models_own_off_names_the_views_that_read_that_off(string title, bool withPath, string expected)
        {
            using var f = new DecisionFixture();
            var path = withPath ? Path.Combine(f.Home, "east", "Sales.pbix") : null;
            await OnAsync(f, title, path);
            await McpTools.SetDecisionPointEnabled(f.Engine, Point, false);
            var on = (await McpTools.SetDecisionPointEnabled(f.Engine, Point, true)).Single(p => p.Name == Point);
            Assert.True(on.Enabled);
            Assert.Equal(expected, on.Note);
            Assert.DoesNotContain("\u2014", on.Note);
        }

        [Fact]
        public async Task An_on_for_a_model_that_was_never_off_says_nothing()
        {
            using var f = new DecisionFixture();
            await OnAsync(f, "Sales", null);
            var on = (await McpTools.SetDecisionPointEnabled(f.Engine, Point, true)).Single(p => p.Name == Point);
            Assert.Null(on.Note);
        }
    }
}
