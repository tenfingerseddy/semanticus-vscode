using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// The engine could set a publish destination but never clear one (contract 19.2). set_publish_destination
    /// gains an explicit clear on both doors, recorded in the connection history like a set, and no new tool. The
    /// Change panel's "Not set" is this call.
    /// </summary>
    [Collection("restore-root")]
    public sealed class PublishDestinationClearTests : IDisposable
    {
        private readonly string _root;
        private readonly string _safeRoot;

        public PublishDestinationClearTests(RestoreRootFixture fixture)
        {
            _safeRoot = fixture.Root;
            _root = Path.Combine(Path.GetTempPath(), "sem-publish-clear-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(_root);
            ConnectionRegistry.RootOverride = Path.Combine(_root, "registry");
        }

        public void Dispose()
        {
            ConnectionRegistry.RootOverride = _safeRoot;
            try { Directory.Delete(_root, true); } catch { }
        }

        private async Task<LocalEngine> OpenFileModelAsync()
        {
            var model = Path.Combine(_root, "model.bim");
            File.Copy(TestModels.FindBim(), model);
            var engine = new LocalEngine(new SessionManager());
            await engine.OpenAsync(model);
            return engine;
        }

        [Fact]
        public async Task Set_then_clear_from_the_agent_door_and_connection_context_reports_none()
        {
            using var engine = await OpenFileModelAsync();
            var published = ConnectionRegistry.Remember("xmla", "powerbi://example/workspace", "Contoso Sales", "Contoso Sales", null, "interactive");

            var set = await McpTools.SetPublishDestination(engine, published.Id);
            Assert.Equal(published.Id, set.Publishing.ConnectionId);

            var cleared = await McpTools.SetPublishDestination(engine, connectionId: null, clear: true);

            Assert.False(cleared.Publishing.Available);
            Assert.False((await McpTools.GetConnectionContext(engine)).Publishing.Available);
            Assert.Contains("No XMLA publish destination is linked", cleared.Summary);

            // Recorded in the connection history like a set, against the destination that was let go.
            var history = await engine.ListConnectionHistoryAsync(published.Id);
            Assert.Contains(history, h => h.Kind == "role" && h.Detail == "set as the publish destination");
            Assert.Contains(history, h => h.Kind == "role" && h.Detail == "cleared as the publish destination");
        }

        // H1 (UAT-J9-S10b-003, d-001, e-005). Studio sends no target on Publish: the engine resolves the destination
        // itself. These three tests pin what that means when the destination moves under an open page.
        private static (Func<string, string, string, bool, System.Collections.Generic.IReadOnlyCollection<string>, DeployReport> hook, System.Collections.Generic.List<string> seen) Recorder()
        {
            var seen = new System.Collections.Generic.List<string>();
            return ((bim, endpoint, database, write, del) =>
            {
                seen.Add(database + "@" + endpoint + (write ? " write" : " preview"));
                return new DeployReport { Endpoint = endpoint, Database = database, TotalChanges = 0 };
            }, seen);
        }

        [Fact]
        public async Task A_publish_naming_no_destination_uses_the_destination_in_force_now_not_the_one_a_page_drew()
        {
            using var engine = await OpenFileModelAsync();
            var shown = ConnectionRegistry.Remember("xmla", "powerbi://example/workspace", "Shown Model", "Shown Model", null, "interactive");
            var moved = ConnectionRegistry.Remember("xmla", "powerbi://example/workspace", "Moved Model", "Moved Model", null, "interactive");
            await engine.SetPublishDestinationAsync(shown.Id, "human");
            var (hook, seen) = Recorder();
            engine.DeployLiveSyncHook = hook;
            await engine.DeployLiveAsync(null, null, null, null, null, false, "human");
            await engine.SetPublishDestinationAsync(moved.Id, "agent");   // the assistant moves it; the page is not told

            await engine.DeployLiveAsync(null, null, null, null, null, false, "human");

            Assert.Equal(new[] { "Shown Model@powerbi://example/workspace preview", "Moved Model@powerbi://example/workspace preview" }, seen);
        }

        [Fact]
        public async Task A_publish_that_names_the_destination_it_showed_is_refused_when_the_engine_has_another()
        {
            using var engine = await OpenFileModelAsync();
            var shown = ConnectionRegistry.Remember("xmla", "powerbi://example/workspace", "Shown Model", "Shown Model", null, "interactive");
            var moved = ConnectionRegistry.Remember("xmla", "powerbi://example/workspace", "Moved Model", "Moved Model", null, "interactive");
            await engine.SetPublishDestinationAsync(moved.Id, "agent");
            var (hook, seen) = Recorder();
            engine.DeployLiveSyncHook = hook;

            foreach (var commit in new[] { false, true })
            {
                var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => engine.DeployLiveAsync(
                    null, null, null, null, null, commit, "human", "reason", "REVIEW-x", null,
                    expectedEndpoint: shown.Endpoint, expectedDatabase: shown.Database));
                Assert.Contains("Moved Model", ex.Message);
                Assert.Contains("Shown Model", ex.Message);
                Assert.Contains("Nothing was published", ex.Message);
                Assert.DoesNotContain("\u2014", ex.Message);
            }
            Assert.Empty(seen);   // refused before any preview or write reached the destination
        }

        [Fact]
        public async Task A_publish_that_names_the_destination_in_force_goes_through()
        {
            using var engine = await OpenFileModelAsync();
            var dest = ConnectionRegistry.Remember("xmla", "powerbi://example/workspace/", "Shown Model", "Shown Model", null, "interactive");
            await engine.SetPublishDestinationAsync(dest.Id, "human");
            var (hook, seen) = Recorder();
            engine.DeployLiveSyncHook = hook;

            var rep = await engine.DeployLiveAsync(null, null, null, null, null, false, "human", null, null, null,
                expectedEndpoint: "POWERBI://example/workspace", expectedDatabase: "shown model");

            Assert.Null(rep.Error);
            Assert.Single(seen);
        }

        // verify (v-s10fix, H1): the whole Studio journey. The person previews X, the assistant moves the destination
        // to Y through its own door, then the person presses Confirm. Whether the page still shows X or has already
        // followed to Y, nothing is written to either model.
        [Fact]
        public async Task Verify_confirm_after_the_assistant_moves_the_destination_writes_nothing_whether_the_page_followed_or_not()
        {
            using var engine = await OpenFileModelAsync();
            var shown = ConnectionRegistry.Remember("xmla", "powerbi://example/workspace", "Shown Model", "Shown Model", null, "interactive");
            var moved = ConnectionRegistry.Remember("xmla", "powerbi://example/other", "Moved Model", "Moved Model", null, "interactive");
            await engine.SetPublishDestinationAsync(shown.Id, "human");
            var (hook, seen) = Recorder();
            engine.DeployLiveSyncHook = hook;

            var preview = await engine.DeployLiveAsync(null, null, null, null, null, false, "human", null, null, null,
                expectedEndpoint: shown.Endpoint, expectedDatabase: shown.Database);
            Assert.False(string.IsNullOrEmpty(preview.ConfirmToken));
            await McpTools.SetPublishDestination(engine, moved.Id);   // the assistant's door

            // The page did not follow: it sends X, the engine has Y.
            var stale = await Record.ExceptionAsync(() => engine.DeployLiveAsync(null, null, null, null, null, true, "human",
                "verify reason", preview.ConfirmToken, null, expectedEndpoint: shown.Endpoint, expectedDatabase: shown.Database));
            // The page followed: it sends Y, but the review token was minted for X.
            var followed = await Record.ExceptionAsync(() => engine.DeployLiveAsync(null, null, null, null, null, true, "human",
                "verify reason", preview.ConfirmToken, null, expectedEndpoint: moved.Endpoint, expectedDatabase: moved.Database));

            Assert.NotNull(stale);
            Assert.Contains("Nothing was published", stale.Message);
            Assert.NotNull(followed);
            Assert.DoesNotContain(seen, x => x.EndsWith(" write"));
        }

        // verify (v-s10fix, H1): the page sends back exactly what connection_context gave it. For every shape a remembered
        // destination can take, that must match what the engine resolves, or the person can never publish at all.
        [Theory]
        [InlineData("powerbi://api.powerbi.com/v1.0/myorg/Contoso%20UAT", "Sales Model")]
        [InlineData("powerbi://api.powerbi.com/v1.0/myorg/Contoso UAT", "Sales Model")]
        [InlineData("  powerbi://api.powerbi.com/v1.0/myorg/WS/  ", "Sales Model")]
        [InlineData("Data Source=powerbi://api.powerbi.com/v1.0/myorg/WS;Initial Catalog=Sales Model", "Sales Model")]
        public async Task Verify_the_destination_the_page_is_given_is_never_refused_as_a_mismatch(string endpoint, string database)
        {
            using var engine = await OpenFileModelAsync();
            var dest = ConnectionRegistry.Remember("xmla", endpoint, database, database, null, "interactive");
            await engine.SetPublishDestinationAsync(dest.Id, "human");
            var shown = (await engine.ConnectionContextAsync()).Publishing;
            var (hook, seen) = Recorder();
            engine.DeployLiveSyncHook = hook;

            var rep = await engine.DeployLiveAsync(null, null, null, null, null, false, "human", null, null, null,
                expectedEndpoint: shown.Endpoint, expectedDatabase: shown.Database);

            Assert.Null(rep.Error);
            Assert.Single(seen);
        }

        // verify (v-s10fix, H1): a model opened LIVE from X whose remembered record links a publish destination Y.
        // connection_context names Y (the page shows Y), but a publish with no target resolves the live origin X.
        // The person's publish must be refused, never sent to X under Y's name.
        [Fact]
        public async Task Verify_a_live_model_whose_record_links_another_destination_never_publishes_to_the_unshown_one()
        {
            var model = Path.Combine(_root, "model.bim");
            File.Copy(TestModels.FindBim(), model);
            var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions);
            await engine.OpenAsync(model);
            var live = ConnectionRegistry.Remember("xmla", "powerbi://example/live", "Live Model", "Live Model", null, "interactive");
            var linked = ConnectionRegistry.Remember("xmla", "powerbi://example/linked", "Linked Model", "Linked Model", null, "interactive");
            ConnectionRegistry.SetWorkingCopy(live.Id, _root, linked.Id);
            sessions.Current.LiveOrigin = new LiveOrigin(live.Endpoint, live.Database, null, "interactive");
            var shown = (await engine.ConnectionContextAsync()).Publishing;
            var (hook, seen) = Recorder();
            engine.DeployLiveSyncHook = hook;

            var ex = await Record.ExceptionAsync(() => engine.DeployLiveAsync(null, null, null, null, null, false, "human", null, null, null,
                expectedEndpoint: shown.Endpoint, expectedDatabase: shown.Database));

            // Either the page is shown the live origin (and the preview goes there), or it is refused.
            if (ex == null) Assert.All(seen, x => Assert.StartsWith(shown.Database + "@" + shown.Endpoint, x));
            else Assert.Empty(seen);
        }

        // join 10, J4: ONE resolver. For a model opened live from X whose record links a publish destination Y,
        // connection_context names Y, so a publish with no target goes to Y too (the assistant door), and the person's
        // page, which sends the Y it was shown, previews there instead of being refused.
        [Fact]
        public async Task Join10_a_live_model_with_a_linked_destination_publishes_where_connection_context_says()
        {
            var model = Path.Combine(_root, "model.bim");
            File.Copy(TestModels.FindBim(), model);
            var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions);
            await engine.OpenAsync(model);
            var live = ConnectionRegistry.Remember("xmla", "powerbi://example/live", "Live Model", "Live Model", null, "interactive");
            var linked = ConnectionRegistry.Remember("xmla", "powerbi://example/linked", "Linked Model", "Linked Model", null, "interactive");
            ConnectionRegistry.SetWorkingCopy(live.Id, _root, linked.Id);
            sessions.Current.LiveOrigin = new LiveOrigin(live.Endpoint, live.Database, null, "interactive");
            var shown = (await engine.ConnectionContextAsync()).Publishing;
            Assert.Equal(linked.Endpoint, shown.Endpoint);
            var (hook, seen) = Recorder();
            engine.DeployLiveSyncHook = hook;

            var person = await engine.DeployLiveAsync(null, null, null, null, null, false, "human", null, null, null,
                expectedEndpoint: shown.Endpoint, expectedDatabase: shown.Database);
            var assistant = await engine.DeployLiveAsync(null, null, null, null, null, false, "agent");

            Assert.Null(person.Error);
            Assert.Null(assistant.Error);
            Assert.Equal(new[] { "Linked Model@powerbi://example/linked preview", "Linked Model@powerbi://example/linked preview" }, seen);
        }

        // verify (v-s10fix, H1): the assistant's door keeps its old meaning. No destination named = the one in force,
        // with no new refusal, after the person's page drew another.
        [Fact]
        public async Task Verify_the_assistant_door_still_previews_the_destination_in_force()
        {
            using var engine = await OpenFileModelAsync();
            var shown = ConnectionRegistry.Remember("xmla", "powerbi://example/workspace", "Shown Model", "Shown Model", null, "interactive");
            var moved = ConnectionRegistry.Remember("xmla", "powerbi://example/other", "Moved Model", "Moved Model", null, "interactive");
            await engine.SetPublishDestinationAsync(shown.Id, "human");
            await McpTools.SetPublishDestination(engine, moved.Id);
            var (hook, seen) = Recorder();
            engine.DeployLiveSyncHook = hook;

            var rep = await engine.DeployLiveAsync(null, null, null, null, null, false, "agent");

            Assert.Null(rep.Error);
            Assert.Equal(new[] { "Moved Model@powerbi://example/other preview" }, seen);
        }

        [Fact]
        public async Task Clear_from_the_ui_door_is_the_same_call()
        {
            using var engine = await OpenFileModelAsync();
            var rpc = new EngineRpcTarget(engine);
            var published = ConnectionRegistry.Remember("xmla", "powerbi://example/workspace", "Contoso Sales", "Contoso Sales", null, "interactive");
            await rpc.setPublishDestination(published.Id);

            var cleared = await rpc.setPublishDestination(null, "human", clear: true);

            Assert.False(cleared.Publishing.Available);
            Assert.False((await rpc.connectionContext()).Publishing.Available);
        }

        [Fact]
        public async Task Clearing_with_none_set_is_a_no_op_that_says_so()
        {
            using var engine = await OpenFileModelAsync();

            var result = await McpTools.SetPublishDestination(engine, connectionId: null, clear: true);

            Assert.False(result.Publishing.Available);
            Assert.Contains("No publish destination was set", result.Note);
            // Opening the model is on the history; a clear that changed nothing is not.
            Assert.DoesNotContain(await engine.ListConnectionHistoryAsync(), h => h.Kind == "role");
        }

        [Fact]
        public async Task A_working_copy_of_a_published_model_can_be_cleared_even_though_its_source_is_xmla()
        {
            // A working copy of an XMLA model publishes back to that model by default (the source record is
            // itself XMLA). Clearing must beat that default too, not only the explicit link.
            var source = ConnectionRegistry.Remember("xmla", "powerbi://example/workspace", "Published", "Published", null, "interactive");
            var copy = Path.Combine(_root, "copy");
            Directory.CreateDirectory(copy);
            File.Copy(TestModels.FindBim(), Path.Combine(copy, "model.bim"));
            ConnectionRegistry.SetWorkingCopy(source.Id, copy, source.Id);
            using var engine = new LocalEngine(new SessionManager());
            await engine.OpenAsync(Path.Combine(copy, "model.bim"));
            Assert.Equal(source.Id, (await engine.ConnectionContextAsync()).Publishing.ConnectionId);

            var cleared = await engine.SetPublishDestinationAsync(null, "human", clear: true);

            Assert.False(cleared.Publishing.Available);
            Assert.Equal(copy, ConnectionRegistry.Find(source.Id).WorkingFolder);   // the copy stays linked; only publishing is let go

            // Choosing a destination again brings it back.
            var again = await engine.SetPublishDestinationAsync(source.Id, "human");
            Assert.Equal(source.Id, again.Publishing.ConnectionId);
        }

        [Fact]
        public async Task A_cleared_destination_stays_cleared_when_the_working_copy_is_reopened()
        {
            // Sol 6: reopening defaulted the destination back to the XMLA source and wiped the clear. Only a destination
            // chosen on purpose brings one back.
            var source = ConnectionRegistry.Remember("xmla", "powerbi://example/workspace", "Published", "Published", null, "interactive");
            var snapshotDir = Path.Combine(_root, "snapshot");
            Directory.CreateDirectory(snapshotDir);
            var snapshot = Path.Combine(snapshotDir, "Published.bim");
            File.Copy(TestModels.FindBim(), snapshot);
            using var engine = new LocalEngine(new SessionManager());
            engine.WorkspaceTokenExportForTests = _ => Task.FromResult(new LiveModelExport.Snapshot
            {
                BimPath = snapshot, DatabaseName = "Published", DatabaseCount = 1, DatabaseNames = new[] { "Published" },
            });
            engine.WorkingCopyConnectForTests = r =>
            {
                engine.SetLiveConnectionForTest(LiveConnection.ForTest("xmla", r.Endpoint, r.Database));
                return Task.CompletedTask;
            };
            var parent = Path.Combine(_root, "models");
            var created = await engine.PrepareWorkingCopyAsync(source.Id, parent, commit: true, origin: "human");
            Assert.Equal(source.Id, created.Context.Publishing.ConnectionId);   // the default for a copy of an XMLA model
            await engine.SetPublishDestinationAsync(null, "human", clear: true);

            var reopened = await engine.PrepareWorkingCopyAsync(source.Id, parent, commit: true, origin: "human");

            Assert.Equal("open", reopened.Action);
            Assert.False(reopened.Context.Publishing.Available);
            Assert.False((await engine.ConnectionContextAsync()).Publishing.Available);
            Assert.True(ConnectionRegistry.Find(source.Id).PublishCleared);
            Assert.Null(reopened.PublishConnectionId);

            // Naming a destination on reopen is a choice, and it brings one back.
            var chosen = await engine.PrepareWorkingCopyAsync(source.Id, parent, commit: true, publishConnectionId: source.Id, origin: "human");
            Assert.Equal(source.Id, chosen.Context.Publishing.ConnectionId);
            Assert.Null(ConnectionRegistry.Find(source.Id).PublishCleared);
        }

        [Fact]
        public async Task A_destination_chosen_on_purpose_survives_a_reopen_that_names_none()
        {
            // The same lost work as a clear coming back: a reopen with no destination named defaulted to the XMLA
            // source and overwrote the destination the person had chosen. Only the first creation takes the default.
            var source = ConnectionRegistry.Remember("xmla", "powerbi://example/workspace", "Published", "Published", null, "interactive");
            var chosen = ConnectionRegistry.Remember("xmla", "powerbi://example/other", "Contoso Sales", "Contoso Sales", null, "interactive");
            var snapshotDir = Path.Combine(_root, "snapshot");
            Directory.CreateDirectory(snapshotDir);
            var snapshot = Path.Combine(snapshotDir, "Published.bim");
            File.Copy(TestModels.FindBim(), snapshot);
            using var engine = new LocalEngine(new SessionManager());
            engine.WorkspaceTokenExportForTests = _ => Task.FromResult(new LiveModelExport.Snapshot
            {
                BimPath = snapshot, DatabaseName = "Published", DatabaseCount = 1, DatabaseNames = new[] { "Published" },
            });
            engine.WorkingCopyConnectForTests = r =>
            {
                engine.SetLiveConnectionForTest(LiveConnection.ForTest("xmla", r.Endpoint, r.Database));
                return Task.CompletedTask;
            };
            var parent = Path.Combine(_root, "models");
            await engine.PrepareWorkingCopyAsync(source.Id, parent, commit: true, origin: "human");
            await engine.SetPublishDestinationAsync(chosen.Id, "human");

            var reopened = await engine.PrepareWorkingCopyAsync(source.Id, parent, commit: true, origin: "human");

            Assert.Equal("open", reopened.Action);
            Assert.Equal(chosen.Id, reopened.PublishConnectionId);
            Assert.Equal(chosen.Id, reopened.Context.Publishing.ConnectionId);
            Assert.Equal(chosen.Id, ConnectionRegistry.Find(source.Id).PublishConnectionId);
        }

        [Fact]
        public async Task A_live_workspace_model_cannot_be_cleared_because_saving_goes_straight_back_to_it()
        {
            var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions);
            await engine.OpenAsync(TestModels.FindBim());
            var live = ConnectionRegistry.Remember("xmla", "powerbi://example/workspace", "Contoso Sales", "Contoso Sales", null, "interactive");
            sessions.Current.LiveOrigin = new LiveOrigin(live.Endpoint, live.Database, null);
            Assert.True((await engine.ConnectionContextAsync()).Publishing.Available);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => McpTools.SetPublishDestination(engine, connectionId: null, clear: true));

            Assert.Contains("open live", ex.Message);
            Assert.True((await engine.ConnectionContextAsync()).Publishing.Available);
        }

        [Fact]
        public async Task Naming_a_destination_and_asking_to_clear_at_once_is_refused()
        {
            using var engine = await OpenFileModelAsync();
            var published = ConnectionRegistry.Remember("xmla", "powerbi://example/workspace", "Contoso Sales", "Contoso Sales", null, "interactive");

            await Assert.ThrowsAsync<ArgumentException>(() => McpTools.SetPublishDestination(engine, published.Id, clear: true));
            await Assert.ThrowsAsync<ArgumentException>(() => McpTools.SetPublishDestination(engine, connectionId: null));
        }
    }
}
