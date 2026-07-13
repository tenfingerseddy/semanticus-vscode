using System;
using System.IO;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    [Collection("restore-root")]
    public sealed class WorkingCopyTests : IDisposable
    {
        private readonly string _root;
        private readonly string _safeRoot;

        public WorkingCopyTests(RestoreRootFixture fixture)
        {
            _safeRoot = fixture.Root;
            _root = Path.Combine(Path.GetTempPath(), "sem-working-copy-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            ConnectionRegistry.RootOverride = Path.Combine(_root, "registry");
        }

        public void Dispose()
        {
            ConnectionRegistry.RootOverride = _safeRoot;
            try { Directory.Delete(_root, true); } catch { }
        }

        [Fact]
        public async Task Preview_explains_two_copies_and_writes_nothing()
        {
            var record = ConnectionRegistry.Remember("xmla", "powerbi://example/workspace", "Sales", "Sales / Gold", null, "interactive");
            var parent = Path.Combine(_root, "models");
            using var engine = new LocalEngine(new SessionManager());

            var result = await engine.PrepareWorkingCopyAsync(record.Id, parent, commit: false, origin: "human");

            Assert.True(result.CanCommit);
            Assert.Equal("create", result.Action);
            Assert.True(result.TwoCopiesInPlay);
            Assert.Contains("Two copies", result.Summary);
            Assert.Contains("Sales _ Gold.SemanticModel", result.TargetFolder);
            Assert.False(Directory.Exists(parent));
            Assert.False(result.CommitRequested);
        }

        [Fact]
        public async Task Preview_can_keep_xmla_publish_target_while_queries_use_a_local_runtime()
        {
            var published = ConnectionRegistry.Remember("xmla", "powerbi://example/workspace", "Published", "Published", null, "interactive");
            var local = ConnectionRegistry.Remember("localDesktop", "localhost:51234", "LocalTest", "Local test model");
            using var engine = new LocalEngine(new SessionManager());

            var result = await engine.PrepareWorkingCopyAsync(published.Id, Path.Combine(_root, "models"), commit: false, queryConnectionId: local.Id, origin: "human");

            Assert.Equal(published.Id, result.PublishConnectionId);
            Assert.Equal(local.Id, result.QueryConnectionId);
            Assert.Equal("localDesktop", result.QueryKind);
            Assert.Contains("local running model Local test model", result.Summary);
            Assert.Contains("push the local changes to Published through its XMLA connection", result.Summary);
            Assert.False(Directory.Exists(Path.Combine(_root, "models")));
        }

        [Fact]
        public async Task Local_running_model_can_be_the_source_with_a_separate_xmla_destination()
        {
            var localSource = ConnectionRegistry.Remember("localDesktop", "localhost:51234", "DesktopModel", "Desktop source");
            var published = ConnectionRegistry.Remember("xmla", "powerbi://example/workspace", "Published", "Published", null, "interactive");
            using var engine = new LocalEngine(new SessionManager());

            var result = await engine.PrepareWorkingCopyAsync(localSource.Id, Path.Combine(_root, "models"), commit: false,
                publishConnectionId: published.Id, origin: "human");

            Assert.Equal(localSource.Id, result.SourceConnectionId);
            Assert.Equal(localSource.Id, result.QueryConnectionId);
            Assert.Equal(published.Id, result.PublishConnectionId);
            Assert.Contains("local running model Desktop source", result.Summary);
            Assert.Contains("push the local changes to Published through its XMLA connection", result.Summary);
        }

        [Fact]
        public void Nonempty_unowned_folder_is_refused_without_changes()
        {
            var record = ConnectionRegistry.Remember("xmla", "powerbi://example/workspace", "Sales", "Sales", null, "interactive");
            var parent = Path.Combine(_root, "models");
            var target = Path.Combine(parent, "Sales.SemanticModel");
            Directory.CreateDirectory(target);
            var userFile = Path.Combine(target, "keep.txt");
            File.WriteAllText(userFile, "mine");

            var result = WorkingCopyPlanner.Plan(record, parent);

            Assert.False(result.CanCommit);
            Assert.Single(result.Conflicts);
            Assert.Equal("mine", File.ReadAllText(userFile));
            Assert.False(File.Exists(Path.Combine(target, WorkingCopyPlanner.MarkerFile)));
        }

        [Fact]
        public void Owned_working_copy_reopens_without_refreshing_files()
        {
            var record = ConnectionRegistry.Remember("xmla", "powerbi://example/workspace", "Sales", "Sales", null, "interactive");
            var parent = Path.Combine(_root, "models");
            var target = Path.Combine(parent, "Sales.SemanticModel");
            var definition = Path.Combine(target, "definition");
            Directory.CreateDirectory(definition);
            File.WriteAllText(Path.Combine(definition, "model.tmdl"), "model Model");
            var localEdit = Path.Combine(definition, "local-note.txt");
            File.WriteAllText(localEdit, "do not replace");
            WorkingCopyPlanner.WriteMarker(target, record);

            var result = WorkingCopyPlanner.Plan(record, parent);

            Assert.True(result.CanCommit);
            Assert.Equal("open", result.Action);
            Assert.Contains("will not be refreshed or overwritten", result.Summary);
            Assert.Equal("do not replace", File.ReadAllText(localEdit));
        }

        [Fact]
        public void Marker_proves_ownership_without_persisting_endpoint_or_tenant()
        {
            var record = ConnectionRegistry.Remember("xmla", "powerbi://private/workspace", "Finance", "Finance", "private-tenant", "interactive");
            var target = Path.Combine(_root, "Finance.SemanticModel");
            Directory.CreateDirectory(target);

            WorkingCopyPlanner.WriteMarker(target, record);

            var json = File.ReadAllText(Path.Combine(target, WorkingCopyPlanner.MarkerFile));
            var marker = WorkingCopyPlanner.ReadMarker(target);
            Assert.Equal(record.Id, marker.ConnectionId);
            Assert.DoesNotContain(record.Endpoint, json);
            Assert.DoesNotContain(record.TenantId, json);
            Assert.DoesNotContain("interactive", json);
        }

        [Fact]
        public void Folder_owned_by_another_connection_is_refused()
        {
            var first = ConnectionRegistry.Remember("xmla", "powerbi://example/one", "Sales", "Sales", null, "interactive");
            var second = ConnectionRegistry.Remember("xmla", "powerbi://example/two", "Sales", "Sales", null, "interactive");
            var parent = Path.Combine(_root, "models");
            var target = Path.Combine(parent, "Sales.SemanticModel");
            var definition = Path.Combine(target, "definition");
            Directory.CreateDirectory(definition);
            File.WriteAllText(Path.Combine(definition, "model.tmdl"), "model Model");
            WorkingCopyPlanner.WriteMarker(target, first);

            var result = WorkingCopyPlanner.Plan(second, parent);

            Assert.False(result.CanCommit);
            Assert.Contains("different connection", result.Conflicts[0]);
        }

        [Fact]
        public async Task Confirm_creates_owned_copy_opens_local_and_keeps_published_query_separate()
        {
            var record = ConnectionRegistry.Remember("xmla", "powerbi://example/workspace", "Published", "Published", null, "interactive");
            var snapshotDir = Path.Combine(_root, "snapshot");
            Directory.CreateDirectory(snapshotDir);
            var snapshot = Path.Combine(snapshotDir, "Published.bim");
            File.Copy(TestModels.FindBim(), snapshot);
            var parent = Path.Combine(_root, "models");
            var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions);
            engine.WorkspaceTokenExportForTests = _ => Task.FromResult(new LiveModelExport.Snapshot
            {
                BimPath = snapshot,
                DatabaseName = "Published",
                DatabaseCount = 1,
                DatabaseNames = new[] { "Published" }
            });
            engine.WorkingCopyConnectForTests = r =>
            {
                engine.SetLiveConnectionForTest(LiveConnection.ForTest("xmla", r.Endpoint, r.Database));
                return Task.CompletedTask;
            };

            var result = await engine.PrepareWorkingCopyAsync(record.Id, parent, commit: true, origin: "human");

            Assert.True(result.Opened);
            Assert.True(result.QueryConnected);
            Assert.Null(result.Error);
            Assert.Equal("workingCopyAndPublished", result.Context.Relationship);
            Assert.True(result.Context.TwoModelsInPlay);
            Assert.Null(sessions.Current.LiveOrigin);
            Assert.StartsWith(result.DefinitionFolder, sessions.Current.SourcePath, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(Path.Combine(result.TargetFolder, "definition.pbism")));
            Assert.True(File.Exists(Path.Combine(result.TargetFolder, WorkingCopyPlanner.MarkerFile)));
            var linked = ConnectionRegistry.Find(record.Id);
            Assert.Equal(result.TargetFolder, linked.WorkingFolder);
            Assert.Equal(record.Id, linked.PublishConnectionId);
            Assert.True(File.Exists(snapshot)); // cleanup is restricted to the engine-owned snapshot root
        }
    }
}
