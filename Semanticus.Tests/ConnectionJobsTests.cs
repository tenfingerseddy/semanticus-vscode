using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// Slice C (lane C2, contract 19.1): the Connections page strikes out a model that cannot do a job WITH THE
    /// ENGINE'S REASON, and does not re-decide the rule. So list_connections carries, on each saved model, the reason
    /// it cannot answer tests and queries and the reason it cannot be a publish destination: the same sentence the
    /// engine throws when that job is attempted. Also here, the drop 14 bug: a plain local model was reported as "in
    /// source control" with the home folder as its repository, because an empty ".git" folder counted as a repository.
    /// </summary>
    [Collection("restore-root")]
    public sealed class ConnectionJobsTests : IDisposable
    {
        private readonly string _root;
        private readonly string _safeRoot;

        public ConnectionJobsTests(RestoreRootFixture fixture)
        {
            _safeRoot = fixture.Root;
            _root = Path.Combine(Path.GetTempPath(), "sem-conn-jobs-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(_root);
            ConnectionRegistry.RootOverride = Path.Combine(_root, "registry");
        }

        public void Dispose()
        {
            ConnectionRegistry.RootOverride = _safeRoot;
            try { Directory.Delete(_root, true); } catch { }
        }

        private static void RememberFour()
        {
            ConnectionRegistry.Remember("file", @"C:\Models\Contoso Retail", null, "Contoso Retail");
            ConnectionRegistry.Remember("localDesktop", "localhost:51000", "Contoso", "Contoso local test");
            ConnectionRegistry.Remember("xmla", "powerbi://api.powerbi.com/v1.0/myorg/Contoso Sales", "Contoso Sales", "Contoso Sales", null, "interactive");
            ConnectionRegistry.Remember("xmla", "localhost:52100", "Loopback", "Loopback model", null, "interactive");
        }

        private static void AssertJobs(ModelConnectionRecord[] list)
        {
            ModelConnectionRecord Named(string n) => list.Single(r => r.ModelName == n);
            Assert.Equal(ConnectionJobs.TestsFile, Named("Contoso Retail").TestsRefusal);
            Assert.Equal(ConnectionJobs.PublishNotWorkspace, Named("Contoso Retail").PublishRefusal);
            Assert.Null(Named("Contoso local test").TestsRefusal);
            Assert.Equal(ConnectionJobs.PublishNotWorkspace, Named("Contoso local test").PublishRefusal);
            Assert.Null(Named("Contoso Sales").TestsRefusal);
            Assert.Null(Named("Contoso Sales").PublishRefusal);
            Assert.Equal(ConnectionJobs.PublishLoopback, Named("Loopback model").PublishRefusal);
        }

        [Fact]
        public async Task Both_doors_list_each_saved_model_with_the_engines_job_refusals()
        {
            RememberFour();
            using var engine = new LocalEngine(new SessionManager());
            AssertJobs(await McpTools.ListConnections(engine));                  // the agent door
            AssertJobs(await new EngineRpcTarget(engine).listConnections());     // the UI door
        }

        [Fact]
        public async Task The_refusals_are_worked_out_on_read_and_never_written_to_connections_json()
        {
            RememberFour();
            using var engine = new LocalEngine(new SessionManager());
            await engine.ListConnectionsAsync();
            ConnectionRegistry.Remember("xmla", "powerbi://api.powerbi.com/v1.0/myorg/Contoso Finance", "Contoso Finance", "Contoso Finance");
            var json = File.ReadAllText(Path.Combine(ConnectionRegistry.Root(), "connections.json"));
            Assert.DoesNotContain("Refusal", json, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task The_publish_refusal_listed_is_the_sentence_the_engine_throws()
        {
            var model = Path.Combine(_root, "model.bim");
            File.Copy(TestModels.FindBim(), model);
            using var engine = new LocalEngine(new SessionManager());
            await engine.OpenAsync(model);
            var desktop = ConnectionRegistry.Remember("localDesktop", "localhost:51000", "Contoso", "Contoso local test");
            var loop = ConnectionRegistry.Remember("xmla", "localhost:52100", "Loopback", "Loopback model");

            var e1 = await Assert.ThrowsAsync<InvalidOperationException>(() => McpTools.SetPublishDestination(engine, desktop.Id));
            Assert.Equal(ConnectionJobs.PublishNotWorkspace, e1.Message);
            var e2 = await Assert.ThrowsAsync<InvalidOperationException>(() => McpTools.SetPublishDestination(engine, loop.Id));
            Assert.Equal(ConnectionJobs.PublishLoopback, e2.Message);
        }

        [Fact]
        public async Task An_empty_git_folder_above_a_plain_local_model_is_not_source_control()
        {
            // The drop 14 shape: a home folder holding an EMPTY ".git" directory, and a model somewhere below it that is
            // in no repository (git rev-parse fails there).
            var home = Path.Combine(_root, "home");
            Directory.CreateDirectory(Path.Combine(home, ".git"));
            var dir = Path.Combine(home, ".cache", "lane");
            Directory.CreateDirectory(dir);
            var model = Path.Combine(dir, "model.bim");
            File.Copy(TestModels.FindBim(), model);
            using var engine = new LocalEngine(new SessionManager());
            await engine.OpenAsync(model);

            var editing = (await McpTools.GetConnectionContext(engine)).Editing;
            Assert.False(editing.SourceControlled);
            Assert.Null(editing.RepositoryRoot);
        }

        [Fact]
        public async Task A_real_repository_above_the_model_is_still_source_control()
        {
            var repo = Path.Combine(_root, "repo");
            Directory.CreateDirectory(Path.Combine(repo, ".git"));
            File.WriteAllText(Path.Combine(repo, ".git", "HEAD"), "ref: refs/heads/main\n");
            var model = Path.Combine(repo, "model.bim");
            File.Copy(TestModels.FindBim(), model);
            using var engine = new LocalEngine(new SessionManager());
            await engine.OpenAsync(model);

            var editing = (await McpTools.GetConnectionContext(engine)).Editing;
            Assert.True(editing.SourceControlled);
            Assert.Equal(Path.GetFullPath(repo), Path.GetFullPath(editing.RepositoryRoot));

            // A worktree or submodule keeps a ".git" FILE that points at the real one; that is a repository too.
            var wt = Path.Combine(_root, "worktree");
            Directory.CreateDirectory(wt);
            File.WriteAllText(Path.Combine(wt, ".git"), "gitdir: " + Path.Combine(repo, ".git") + "\n");
            var model2 = Path.Combine(wt, "model.bim");
            File.Copy(TestModels.FindBim(), model2);
            await engine.OpenAsync(model2);
            Assert.True((await McpTools.GetConnectionContext(engine)).Editing.SourceControlled);
        }
    }
}
