// fix-uat-claude-work (17 Oct UAT): a real "Save a copy next to the model" on a live model put the copy in the temporary
// folder Semanticus keeps for its own working copy, and the assistant then said it could be deleted. A save of a live
// model must land in a lasting place (the connected folder, else ~/Semanticus/<model>), and the result must carry the
// fact whether that folder is under git, so the assistant never has to guess it.
using System;
using System.IO;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    [Collection("restore-root")]
    public sealed class LiveSaveLastingTests : IAsyncLifetime
    {
        private const string Endpoint = "powerbi://api.powerbi.com/v1.0/myorg/Finance";
        private readonly string _savedRegistryRoot;
        private readonly string _root = Path.Combine(Path.GetTempPath(), "sem-lsl-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        private string _workspace = null!;

        public LiveSaveLastingTests(RestoreRootFixture fixture) => _savedRegistryRoot = fixture.Root;

        public Task InitializeAsync()
        {
            Directory.CreateDirectory(_root);
            _workspace = Path.Combine(_root, "clients", "northstar");
            Directory.CreateDirectory(_workspace);
            ConnectionRegistry.RootOverride = Path.Combine(_root, "registry");
            LiveModelExport.TempRootOverride = Path.Combine(_root, "snapshots");
            Directory.CreateDirectory(LiveModelExport.TempRoot);
            LocalEngine.LastingHomeForTests = Path.Combine(_root, "home");
            return Task.CompletedTask;
        }

        public Task DisposeAsync()
        {
            LiveModelExport.TempRootOverride = null;
            LocalEngine.LastingHomeForTests = null;
            ConnectionRegistry.RootOverride = _savedRegistryRoot;
            try { Directory.Delete(_root, true); } catch { }
            return Task.CompletedTask;
        }

        private LocalEngine NewLiveEngine(string workspace, bool workspaceIsFolder = true, SessionManager sessions = null)
        {
            var engine = new LocalEngine(sessions ?? new SessionManager(), TestEntitlements.Pro, workspace, workspaceIsFolder);
            engine.LiveSnapshotExportForTests = (ds, db) =>
            {
                var dir = Path.Combine(LiveModelExport.TempRoot, Guid.NewGuid().ToString("N").Substring(0, 8));
                Directory.CreateDirectory(dir);
                var bim = Path.Combine(dir, "Northstar Sales Working.bim");
                File.Copy(TestModels.FindBim(), bim);
                return Task.FromResult(new LiveModelExport.Snapshot { BimPath = bim, DatabaseName = "Northstar Sales Working", DatabaseCount = 1, DatabaseNames = new[] { "Northstar Sales Working" } });
            };
            engine.LiveBindForTests = (kind, ds) => LiveConnection.ForTest(kind, ds, "Northstar Sales Working");
            return engine;
        }

        private static bool UnderSnapshotRoot(string p) =>
            LiveModelExport.Norm(p).StartsWith(LiveModelExport.Norm(LiveModelExport.TempRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_copy_of_a_live_model_aimed_at_the_temporary_folder_lands_in_the_connected_folder()
        {
            using var engine = NewLiveEngine(_workspace);
            await engine.OpenLiveAsync(Endpoint, "Northstar Sales Working", "token", "not-a-jwt", null, origin: "agent", discardUnsaved: true);
            var src = (await engine.SessionInfoAsync()).Source;
            var wanted = Path.Combine(Path.GetDirectoryName(src)!, "Northstar Sales Working copy.bim");

            var r = await engine.SaveAsync(wanted, "bim", false, "agent");

            Assert.False(UnderSnapshotRoot(r.Path), "the copy must not sit in the temporary folder: " + r.Path);
            Assert.Equal(Path.GetFullPath(_workspace), Path.GetDirectoryName(Path.GetFullPath(r.Path)));
            Assert.True(File.Exists(r.Path));
            Assert.False(File.Exists(wanted));
            Assert.Contains(r.Path, r.Note);   // the result names the path
            Assert.Contains(Path.GetFullPath(_workspace), r.Note);   // and says where the model's own next save goes
        }

        // UAT 18 Oct N23: on a live model Claude saved a copy and said "The changes are saved", while the footer said unsaved and the Plan still asked to save.
        // A kept copy is not a save of the service: the result says which one happened, and the session says the same thing to the footer and the Plan.
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_kept_copy_of_a_live_model_says_the_service_is_not_changed_until_publish_and_the_session_agrees()
        {
            var sessions = new SessionManager();
            var heard = new System.Collections.Generic.List<ChangeNotification>();
            sessions.Bus.Changed += heard.Add;
            using var engine = NewLiveEngine(_workspace, sessions: sessions);
            await engine.OpenLiveAsync(Endpoint, "Northstar Sales Working", "token", "not-a-jwt", null, origin: "agent", discardUnsaved: true);
            await engine.CreateMeasureAsync("table:Product", "Kept Copy Probe", "1", "agent");
            Assert.True((await engine.SessionInfoAsync()).HasUnsavedChanges);
            Assert.Null((await engine.SessionInfoAsync()).KeptCopyPath);
            Assert.Contains((await engine.GetModelPlanAsync()).Items, i => i.Id == LocalEngine.UnsavedItemId);   // before a copy the row asks to save
            var src = (await engine.SessionInfoAsync()).Source;

            var r = await engine.SaveAsync(Path.Combine(Path.GetDirectoryName(src)!, "Northstar Sales Working copy.bim"), "bim", false, "agent");

            Assert.True(r.IsBackupCopy);
            // A copy changes what the footer and the Plan say, and no edit follows it, so the window is told or it keeps the line it had (found on the real window, 18 Oct).
            Assert.Contains(heard, n => n.Label == "keep copy" && n.Revision == r.Revision);
            Assert.Equal("Kept a copy at " + r.Path + ". The service is not changed until you publish.", r.Note);
            var info = await engine.SessionInfoAsync();
            Assert.True(info.HasUnsavedChanges, "a copy does not make the live model saved");
            Assert.Equal(r.Path, info.KeptCopyPath);   // the footer reads this: it says a copy is kept, not that nothing is saved
            // The Plan's "Save the changes you have made" is what the copy did; what is left is publishing, which its own suggestion says.
            Assert.DoesNotContain((await engine.GetModelPlanAsync()).Items, i => i.Id == LocalEngine.UnsavedItemId);
            await engine.CreateMeasureAsync("table:Product", "After The Copy", "2", "agent");
            Assert.Null((await engine.SessionInfoAsync()).KeptCopyPath);   // a newer change is in no copy
        }

        // UAT 18 Oct round 2 (N23 half): the Plan header still read "Save your changes, Do it" 35 s after the copy was kept. The save it asks for has happened.
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task The_next_step_after_a_kept_copy_of_a_live_model_is_not_to_save()
        {
            using var engine = NewLiveEngine(_workspace);
            await engine.OpenLiveAsync(Endpoint, "Northstar Sales Working", "token", "not-a-jwt", null, origin: "agent", discardUnsaved: true);
            await engine.CreateMeasureAsync("table:Product", "Next Step Probe", "1", "agent");
            Assert.Equal(NextStepRules.Save, (await engine.GetNextStepAsync()).Step?.Kind);   // before a copy it asks to save
            var src = (await engine.SessionInfoAsync()).Source;

            await engine.SaveAsync(Path.Combine(Path.GetDirectoryName(src)!, "Northstar Sales Working copy.bim"), "bim", false, "agent");

            var step = (await engine.GetNextStepAsync()).Step;
            Assert.True(step == null || step.Kind != NextStepRules.Save, "the header still asks: " + step?.Lead);
        }

        // UAT 18 Oct P4: the person's Ctrl+S wrote "Northstar Sales Working.bim"; a later open of the same published model saved by the assistant
        // wrote "Northstar Sales Working 2.bim" beside it. A model that already has a file of its own saves to that file.
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_published_model_that_already_has_a_saved_file_saves_to_that_file_not_a_numbered_second_one()
        {
            string first;
            using (var engine = NewLiveEngine(_workspace))
            {
                await engine.OpenLiveAsync(Endpoint, "Northstar Sales Working", "token", "not-a-jwt", null, origin: "human", discardUnsaved: true);
                first = (await engine.SaveAsync(null, "bim", false, "human")).Path;
            }
            Assert.Equal("Northstar Sales Working.bim", Path.GetFileName(first));

            using var again = NewLiveEngine(_workspace);
            await again.OpenLiveAsync(Endpoint, "Northstar Sales Working", "token", "not-a-jwt", null, origin: "agent", discardUnsaved: true);
            await again.CreateMeasureAsync("table:Product", "Second Open Probe", "1", "agent");
            var r = await again.SaveAsync(null, "bim", false, "agent");

            Assert.Equal(first, r.Path);
            Assert.Single(Directory.GetFiles(Path.GetDirectoryName(first)!, "*.bim"));
            Assert.Contains("Second Open Probe", File.ReadAllText(first));
            Assert.False((await again.SessionInfoAsync()).HasUnsavedChanges);
        }

        // Verifier v-fix-publish-role (18 Oct, live lane) P4: the person's Ctrl+S wrote "Northstar Sales Working.bim", Claude kept "... copy.bim", then saved again after more
        // edits and the engine refused to write over its own copy and sent it to "copy 2.bim": three model files. A copy this session already wrote is replaced.
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_second_save_to_the_copy_the_session_already_kept_replaces_it_and_makes_no_third_file()
        {
            using var engine = NewLiveEngine(_workspace);
            await engine.OpenLiveAsync(Endpoint, "Northstar Sales Working", "token", "not-a-jwt", null, origin: "human", discardUnsaved: true);
            var own = (await engine.SaveAsync(null, "bim", false, "human")).Path;
            var copy = Path.Combine(Path.GetDirectoryName(own)!, "Northstar Sales Working copy.bim");
            await engine.CreateMeasureAsync("table:Product", "First Copy Probe", "1", "agent");
            await engine.SaveAsync(copy, "bim", false, "agent");
            await engine.CreateMeasureAsync("table:Product", "Second Copy Probe", "2", "agent");

            var r = await engine.SaveAsync(copy, "bim", false, "agent");

            Assert.Equal(copy, r.Path);
            Assert.Contains("Second Copy Probe", File.ReadAllText(copy));
            Assert.Equal(2, Directory.GetFiles(Path.GetDirectoryName(own)!, "*.bim").Length);   // the person's file and the one copy
        }

        // A copy somebody else made, or one from an earlier session, is still never written over: only a file this session wrote is replaced.
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_file_the_session_did_not_write_is_still_not_replaced_by_a_copy()
        {
            using var engine = NewLiveEngine(_workspace);
            await engine.OpenLiveAsync(Endpoint, "Northstar Sales Working", "token", "not-a-jwt", null, origin: "human", discardUnsaved: true);
            var own = (await engine.SaveAsync(null, "bim", false, "human")).Path;
            var other = Path.Combine(Path.GetDirectoryName(own)!, "Northstar Sales Working copy.bim");
            File.WriteAllText(other, "not this model");

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => engine.SaveAsync(other, "bim", false, "agent"));

            Assert.Contains("already exists", ex.Message);
            Assert.Equal("not this model", File.ReadAllText(other));
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task With_no_connected_folder_the_copy_goes_to_the_Semanticus_folder_in_the_home_folder()
        {
            using var engine = NewLiveEngine(null);
            await engine.OpenLiveAsync(Endpoint, "Northstar Sales Working", "token", "not-a-jwt", null, origin: "agent", discardUnsaved: true);
            var src = (await engine.SessionInfoAsync()).Source;

            var r = await engine.SaveAsync(Path.Combine(Path.GetDirectoryName(src)!, "Northstar Sales Working copy.bim"), "bim", false, "agent");

            Assert.False(UnderSnapshotRoot(r.Path));
            Assert.Equal(Path.Combine(_root, "home", "Semanticus", "Northstar Sales Working"), Path.GetDirectoryName(Path.GetFullPath(r.Path)));
            Assert.True(File.Exists(r.Path));
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_persons_save_with_no_folder_open_never_writes_into_the_processs_working_folder()
        {
            // With no folder open the extension still gives the engine a workspace (the VS Code process's working folder, for engine.json).
            // That is not a folder the person chose, so a plain save of a live model goes to ~/Semanticus/<model> and the result says where.
            using var engine = NewLiveEngine(_workspace, workspaceIsFolder: false);
            await engine.OpenLiveAsync(Endpoint, "Northstar Sales Working", "token", "not-a-jwt", null, origin: "human", discardUnsaved: true);

            var r = await engine.SaveAsync(null, null, false, "human");

            Assert.Equal(Path.Combine(_root, "home", "Semanticus", "Northstar Sales Working"), Path.GetDirectoryName(Path.GetFullPath(r.Path)));
            Assert.Empty(Directory.GetFiles(_workspace));
            Assert.Contains(r.Path, r.Note);
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_plain_save_of_a_live_model_moves_the_model_to_a_lasting_file()
        {
            using var engine = NewLiveEngine(_workspace);
            await engine.OpenLiveAsync(Endpoint, "Northstar Sales Working", "token", "not-a-jwt", null, origin: "agent", discardUnsaved: true);

            var r = await engine.SaveAsync(null, null, false, "human");

            Assert.False(UnderSnapshotRoot(r.Path));
            Assert.True(File.Exists(r.Path));
            Assert.Equal(Path.GetFullPath(r.Path), Path.GetFullPath((await engine.SessionInfoAsync()).Source));   // the next plain save writes here too
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Git_status_of_a_live_model_says_it_is_a_temporary_working_copy_not_a_fact_about_the_persons_folder()
        {
            using var engine = NewLiveEngine(_workspace);
            await engine.OpenLiveAsync(Endpoint, "Northstar Sales Working", "token", "not-a-jwt", null, origin: "agent", discardUnsaved: true);
            var st = await engine.GitStatusAsync();
            Assert.False(st.IsRepo);
            Assert.Contains("working copy", st.Note);
            Assert.Contains("save_model", st.Note);
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_save_result_says_whether_the_folder_is_in_git()
        {
            var plain = Path.Combine(_root, "plain"); Directory.CreateDirectory(plain);
            var repo = Path.Combine(_root, "repo"); Directory.CreateDirectory(repo);
            var init = await GitCli.RunAsync(repo, "init");   // a real repository: git answers, not a folder named .git
            Assert.True(init.ExitCode == 0, "git init failed: " + init.Stderr);
            using var engine = new LocalEngine(new SessionManager(), TestEntitlements.Pro);
            await engine.OpenAsync(TestModels.FindBim());

            var inRepo = await engine.SaveAsync(Path.Combine(repo, "m.bim"), "bim", false, "human");
            var outside = await engine.SaveAsync(Path.Combine(plain, "m.bim"), "bim", false, "human");

            Assert.True(inRepo.InGit);
            Assert.False(outside.InGit);
        }
    }
}
