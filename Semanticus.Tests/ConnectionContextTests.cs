using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    [Collection("restore-root")]
    public sealed class ConnectionContextTests : IDisposable
    {
        private readonly string _root;
        private readonly string _safeRoot;

        public ConnectionContextTests(RestoreRootFixture fixture)
        {
            _safeRoot = fixture.Root;
            _root = Path.Combine(Path.GetTempPath(), "sem-connection-context-" + Guid.NewGuid().ToString("N"));
            ConnectionRegistry.RootOverride = _root;
        }

        public void Dispose()
        {
            ConnectionRegistry.RootOverride = _safeRoot;
            try { Directory.Delete(_root, true); } catch { }
        }

        [Fact]
        public async Task Empty_context_names_neither_model()
        {
            using var engine = new LocalEngine(new SessionManager());
            var context = await engine.ConnectionContextAsync();
            Assert.Equal("none", context.Relationship);
            Assert.False(context.TwoModelsInPlay);
            Assert.False(context.Editing.Available);
            Assert.False(context.Querying.Available);
        }

        [Fact]
        public async Task File_model_and_attached_query_model_are_explicitly_two_models()
        {
            var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions);
            await engine.OpenAsync(TestModels.FindBim());
            var record = ConnectionRegistry.Remember("xmla", "powerbi://example/workspace", "Published", "Published", null, "interactive");
            engine.SetLiveConnectionForTest(LiveConnection.ForTest("xmla", record.Endpoint, record.Database));

            var context = await engine.ConnectionContextAsync();
            Assert.Equal("twoModels", context.Relationship);
            Assert.True(context.TwoModelsInPlay);
            Assert.Equal(record.Id, context.Querying.ConnectionId);
            Assert.Equal("Published", context.Querying.ModelName);
            Assert.Contains("Two models are in play for different purposes", context.Summary);
        }

        [Fact]
        public async Task Live_open_identity_is_one_model_for_editing_and_queries()
        {
            var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions);
            await engine.OpenAsync(TestModels.FindBim());
            var record = ConnectionRegistry.Remember("xmla", "powerbi://example/workspace", "Published", "Published", null, "interactive");
            sessions.Current.LiveOrigin = new LiveOrigin(record.Endpoint, record.Database, null, record.AuthMode);
            engine.SetLiveConnectionForTest(LiveConnection.ForTest("xmla", record.Endpoint, record.Database));

            var context = await engine.ConnectionContextAsync();
            Assert.Equal("sameInstance", context.Relationship);
            Assert.False(context.TwoModelsInPlay);
            Assert.Equal(record.Id, context.Editing.ConnectionId);
            Assert.Equal(record.Id, context.Querying.ConnectionId);
        }

        [Fact]
        public async Task Linked_working_folder_is_two_copies_of_one_model()
        {
            var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions);
            await engine.OpenAsync(TestModels.FindBim());
            var source = sessions.Current.SourcePath;
            var record = ConnectionRegistry.Remember("xmla", "powerbi://example/workspace", "Published", "Published", null, "interactive");
            ConnectionRegistry.SetWorkingFolder(record.Id, Path.GetDirectoryName(source));
            engine.SetLiveConnectionForTest(LiveConnection.ForTest("xmla", record.Endpoint, record.Database));

            var context = await engine.ConnectionContextAsync();
            Assert.Equal("workingCopyAndPublished", context.Relationship);
            Assert.True(context.TwoModelsInPlay);
            Assert.Contains("Two copies are in play for different purposes", context.Summary);
            Assert.Equal(record.Id, context.Publishing.ConnectionId);
            Assert.False(context.PublishDestinationSeparateFromQuerying);
        }

        [Fact]
        public async Task Local_query_runtime_does_not_hide_the_linked_xmla_publish_destination()
        {
            var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions);
            await engine.OpenAsync(TestModels.FindBim());
            var published = ConnectionRegistry.Remember("xmla", "powerbi://example/workspace", "Published", "Published", null, "interactive");
            ConnectionRegistry.SetWorkingFolder(published.Id, Path.GetDirectoryName(sessions.Current.SourcePath));
            var local = ConnectionRegistry.Remember("localDesktop", "localhost:51234", "LocalTest", "Local test model");
            engine.SetLiveConnectionForTest(LiveConnection.ForTest("local", local.Endpoint, local.Database));

            var context = await engine.ConnectionContextAsync();

            Assert.Equal("twoModels", context.Relationship);
            Assert.Equal(local.Id, context.Querying.ConnectionId);
            Assert.Equal(published.Id, context.Publishing.ConnectionId);
            Assert.True(context.PublishDestinationSeparateFromQuerying);
            Assert.Contains("explicitly push local changes to Published", context.Summary);
        }

        [Fact]
        public async Task Desktop_source_local_copy_keeps_its_separate_xmla_publish_destination()
        {
            var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions);
            await engine.OpenAsync(TestModels.FindBim());
            var localSource = ConnectionRegistry.Remember("localDesktop", "localhost:51234", "DesktopModel", "Desktop source");
            var published = ConnectionRegistry.Remember("xmla", "powerbi://example/workspace", "Published", "Published", null, "interactive");
            ConnectionRegistry.SetWorkingCopy(localSource.Id, Path.GetDirectoryName(sessions.Current.SourcePath), published.Id);
            engine.SetLiveConnectionForTest(LiveConnection.ForTest("local", localSource.Endpoint, localSource.Database));

            var context = await engine.ConnectionContextAsync();

            Assert.Equal("workingCopyAndLocalRuntime", context.Relationship);
            Assert.Equal("file", context.Editing.Kind);
            Assert.Null(context.Editing.ConnectionId);
            Assert.Equal(localSource.Id, context.Editing.SourceConnectionId);
            Assert.Equal(localSource.Id, context.Querying.ConnectionId);
            Assert.Equal(published.Id, context.Publishing.ConnectionId);
            Assert.True(context.PublishDestinationSeparateFromQuerying);
            Assert.Contains("linked local running model", context.Summary);
            Assert.Contains("explicitly push local changes to Published", context.Summary);
        }

        [Fact]
        public async Task Existing_local_file_in_a_repository_is_a_source_without_an_ownership_marker()
        {
            var repo = Path.Combine(_root, "user-repo");
            Directory.CreateDirectory(Path.Combine(repo, ".git"));
            var model = Path.Combine(repo, "model.bim");
            File.Copy(TestModels.FindBim(), model);
            using var engine = new LocalEngine(new SessionManager());

            await engine.OpenAsync(model);
            var context = await engine.ConnectionContextAsync();

            Assert.True(context.Editing.Available);
            Assert.Equal("file", context.Editing.Kind);
            Assert.Equal(model, context.Editing.Source);
            Assert.True(context.Editing.SourceControlled);
            Assert.Equal(repo, context.Editing.RepositoryRoot);
            Assert.False(File.Exists(Path.Combine(repo, WorkingCopyPlanner.MarkerFile)));
            Assert.Contains("No XMLA publish destination is linked", context.Summary);
        }

        [Fact]
        public async Task Existing_repository_model_can_link_publish_destination_without_touching_repository_files()
        {
            var repo = Path.Combine(_root, "linked-repo");
            Directory.CreateDirectory(Path.Combine(repo, ".git"));
            var model = Path.Combine(repo, "model.bim");
            File.Copy(TestModels.FindBim(), model);
            using var engine = new LocalEngine(new SessionManager());
            await engine.OpenAsync(model);
            var published = ConnectionRegistry.Remember("xmla", "powerbi://example/workspace", "Published", "Published", null, "interactive");

            var context = await engine.SetPublishDestinationAsync(published.Id, "human");

            Assert.Equal(published.Id, context.Publishing.ConnectionId);
            Assert.Equal("Published", context.Publishing.ModelName);
            Assert.True(context.Editing.SourceControlled);
            Assert.False(File.Exists(Path.Combine(repo, WorkingCopyPlanner.MarkerFile)));
            Assert.Single(Directory.GetFiles(repo));
            Assert.DoesNotContain(await engine.ListConnectionsAsync(), r => string.Equals(r.Kind, "file", StringComparison.OrdinalIgnoreCase));

            var local = ConnectionRegistry.Remember("localDesktop", "localhost:51234", "LocalTest", "Local test model");
            var secondPublish = ConnectionRegistry.Remember("xmla", "powerbi://example/other", "OtherPublished", "Other published", null, "interactive");
            engine.SetLiveConnectionForTest(LiveConnection.ForTest("local", local.Endpoint, local.Database));
            context = await engine.SetPublishDestinationAsync(secondPublish.Id, "human");
            Assert.Equal(local.Id, context.Querying.ConnectionId);
            Assert.Equal(secondPublish.Id, context.Publishing.ConnectionId);
            Assert.True(context.PublishDestinationSeparateFromQuerying);

            var second = Path.Combine(repo, "second-model.bim");
            File.Copy(TestModels.FindBim(), second);
            await engine.OpenAsync(second);
            Assert.False((await engine.ConnectionContextAsync()).Publishing.Available);
        }
        [Fact]
        public async Task Matching_endpoint_with_a_different_dataset_stays_two_models()
        {
            var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions);
            await engine.OpenAsync(TestModels.FindBim());
            var record = ConnectionRegistry.Remember("xmla", "powerbi://example/workspace", "Published", "Published", null, "interactive");
            sessions.Current.LiveOrigin = new LiveOrigin(record.Endpoint, record.Database, null, record.AuthMode);
            engine.SetLiveConnectionForTest(LiveConnection.ForTest("xmla", record.Endpoint, "OtherDataset"));

            var context = await engine.ConnectionContextAsync();
            // Same endpoint is NOT the same model: sameInstance needs endpoint AND dataset to match,
            // or a query against a sibling dataset silently reads as the model being edited.
            Assert.NotEqual("sameInstance", context.Relationship);
            Assert.True(context.TwoModelsInPlay);
        }

        [Fact]
        public void Live_origin_write_authority_is_compiled_only_into_the_editing_open_paths()
        {
            var setter = typeof(Session).GetProperty(nameof(Session.LiveOrigin))!.SetMethod!;
            var expected = new[] { nameof(LocalEngine.OpenLiveAsync), nameof(LocalEngine.OpenLocalAsync) }
                .Select(name => typeof(LocalEngine).GetMethod(name)!.GetCustomAttribute<AsyncStateMachineAttribute>()!.StateMachineType)
                .Select(type => type.GetMethod("MoveNext", BindingFlags.Instance | BindingFlags.NonPublic)!)
                .OrderBy(MethodKey)
                .ToArray();
            var actual = CompiledCallGraph.FindCallers(typeof(LocalEngine).Assembly, setter)
                .OrderBy(MethodKey)
                .ToArray();

            // LiveOrigin is WRITE authority: it is the deploy-back target, not a query hint. ONLY the editing-open paths
            // (open_live / open_local) may set it. connect_xmla is a QUERY connection and must NEVER rebind it (HIGH 1),
            // so BindLiveOriginIfCurrent was removed — this pin now proves no assignment site can creep back in via source
            // layout, comments, generated files, or ambient untracked .cs files.
            Assert.Equal(expected.Select(MethodKey), actual.Select(MethodKey));
        }

        private static string MethodKey(MethodBase method) => method.DeclaringType!.FullName + "." + method.Name;
    }
}
