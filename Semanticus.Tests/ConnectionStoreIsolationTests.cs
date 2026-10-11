// F-232: a test run must never write into the real user's connection list.
//
// The test assembly points SEMANTICUS_HOME at a scratch directory for the whole run (TestEnvironment's module
// initializer), and the engine resolves every per-user store from it when no store-level override is set. This
// proves the chain end to end with the override CLEARED, which is exactly the state that used to leak: a
// fixture that sets nothing, opens a model, and has the open recorded.

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    [Collection("restore-root")]   // clears the static ConnectionRegistry root for the length of one test
    public sealed class ConnectionStoreIsolationTests
    {
        private static string RealHome() =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".semanticus");

        /// <summary>Run <paramref name="body"/> with a scratch home under the temp folder. The module initializer keeps
        /// a SEMANTICUS_HOME that was already set, so the inherited value can be any folder (a lane's, a verifier's under
        /// ~/.cache), and these tests prove the chain, not that choice: when the inherited home is not a scratch folder
        /// under temp, the test sets one of its own and puts the inherited value back after. The inherited scratch
        /// home is used as it is, because every test running beside this one resolves the same variable. For the
        /// same reason a folder of our own is left in place afterwards: a store that resolved it mid-test may still write.</summary>
        private static async Task WithOwnHome(Func<string, Task> body)
        {
            var before = Environment.GetEnvironmentVariable(SemanticusHome.Variable);
            var temp = Path.GetFullPath(Path.GetTempPath());
            if (!string.IsNullOrWhiteSpace(before) && Path.GetFullPath(before).StartsWith(temp, StringComparison.Ordinal))
            {
                await body(Path.GetFullPath(before));
                return;
            }
            var home = Path.Combine(temp, "sem-f232-home-" + Guid.NewGuid().ToString("N").Substring(0, 12));
            Directory.CreateDirectory(home);
            Environment.SetEnvironmentVariable(SemanticusHome.Variable, home);
            try { await body(Path.GetFullPath(home)); }
            finally { Environment.SetEnvironmentVariable(SemanticusHome.Variable, before); }
        }

        private sealed class Snapshot
        {
            public bool Exists; public DateTime Written; public byte[] Bytes;
            public static Snapshot Of(string path) => File.Exists(path)
                ? new Snapshot { Exists = true, Written = File.GetLastWriteTimeUtc(path), Bytes = File.ReadAllBytes(path) }
                : new Snapshot { Exists = false };
        }

        [Fact]
        public Task Opening_a_model_in_a_test_writes_the_scratch_home_and_leaves_the_real_one_alone() => WithOwnHome(async ownHome =>
        {
            var real = Path.GetFullPath(RealHome());
            var scratch = Path.GetFullPath(SemanticusHome.Root());
            Assert.Equal(ownHome, scratch);
            Assert.NotEqual(real, scratch);
            Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), scratch, StringComparison.Ordinal);

            var realConnections = Path.Combine(real, "connections.json");
            var realHistory = Path.Combine(real, "connection-history.json");
            var connectionsBefore = Snapshot.Of(realConnections);
            var historyBefore = Snapshot.Of(realHistory);

            var workspace = Path.Combine(Path.GetTempPath(), "sem-f232-" + Guid.NewGuid().ToString("N").Substring(0, 12));
            Directory.CreateDirectory(workspace);
            var file = Path.Combine(workspace, "Contoso F232.bim");
            var previous = ConnectionRegistry.RootOverride;
            ConnectionRegistry.RootOverride = null;
            try
            {
                using (var author = new LocalEngine(new SessionManager(), TestEntitlements.Pro, workspace))
                {
                    await author.CreateModelAsync("Contoso", 1604);
                    await author.SaveAsync(file, "bim", false, "human");
                }
                using (var reader = new LocalEngine(new SessionManager(), TestEntitlements.Pro, workspace))
                    await reader.OpenAsync(file);

                Assert.Equal(scratch, Path.GetFullPath(ConnectionRegistry.Root()));
                // A model under the temp folder is not SAVED to the connections list (StateHomeTests), but the open
                // still gets its timeline row, and that row must land in the scratch home.
                Assert.Contains(ConnectionHistory.List(), e =>
                    e.Kind == "open" && string.Equals(Path.GetFullPath(e.Endpoint ?? ""), Path.GetFullPath(file), StringComparison.Ordinal));
                Assert.True(File.Exists(Path.Combine(scratch, "connection-history.json")));
            }
            finally
            {
                ConnectionRegistry.RootOverride = previous;
                try { Directory.Delete(workspace, true); } catch { }
            }

            // The real files: same bytes, same write time, and never mention this test's model.
            var connectionsAfter = Snapshot.Of(realConnections);
            var historyAfter = Snapshot.Of(realHistory);
            Assert.Equal(connectionsBefore.Exists, connectionsAfter.Exists);
            Assert.Equal(historyBefore.Exists, historyAfter.Exists);
            if (connectionsAfter.Exists)
                Assert.DoesNotContain("Contoso F232", System.Text.Encoding.UTF8.GetString(connectionsAfter.Bytes), StringComparison.Ordinal);
            if (historyAfter.Exists)
                Assert.DoesNotContain("Contoso F232", System.Text.Encoding.UTF8.GetString(historyAfter.Bytes), StringComparison.Ordinal);
            Assert.Equal(connectionsBefore.Written, connectionsAfter.Written);
            Assert.Equal(connectionsBefore.Bytes ?? Array.Empty<byte>(), connectionsAfter.Bytes ?? Array.Empty<byte>());
            Assert.Equal(historyBefore.Written, historyAfter.Written);
            Assert.Equal(historyBefore.Bytes ?? Array.Empty<byte>(), historyAfter.Bytes ?? Array.Empty<byte>());
        });

        [Fact]
        public Task The_experience_knowledge_and_interview_homes_are_the_scratch_home_and_the_real_ones_stay_untouched() => WithOwnHome(async ownHome =>
        {
            var real = Path.GetFullPath(RealHome());
            var scratch = Path.GetFullPath(SemanticusHome.Root());
            Assert.Equal(ownHome, scratch);
            Assert.NotEqual(real, scratch);

            // Where each global store resolves, checked BEFORE anything is written, so a wrong answer fails here
            // and never writes into the real home.
            Assert.Equal(scratch, Path.GetFullPath(ExperienceStore.GlobalHomeDir()));
            Assert.Equal(Path.Combine(scratch, KnowledgeStore.DirName), Path.GetFullPath(LocalEngine.GlobalKnowledgeDir()));
            Assert.Equal(Path.Combine(scratch, InterviewStore.DirName), Path.GetFullPath(LocalEngine.GlobalInterviewDir()));

            var realInsights = Path.Combine(real, KnowledgeStore.DirName, KnowledgeStore.FileName);
            var realQuestions = Path.Combine(real, InterviewStore.DirName, InterviewStore.FileName);
            var realExperience = Path.Combine(real, "experience.jsonl");
            var insightsBefore = Snapshot.Of(realInsights);
            var questionsBefore = Snapshot.Of(realQuestions);
            var experienceBefore = Snapshot.Of(realExperience);

            var workspace = Path.Combine(Path.GetTempPath(), "sem-f232-" + Guid.NewGuid().ToString("N").Substring(0, 12));
            Directory.CreateDirectory(workspace);
            try
            {
                using var engine = new LocalEngine(new SessionManager(), TestEntitlements.Pro, workspace);
                await engine.CreateModelAsync("Contoso", 1604);
                await engine.SaveAsync(Path.Combine(workspace, "Contoso Homes.bim"), "bim", false, "human");
                await engine.AddInsightAsync("Contoso home check lesson", new[] { "ContosoHomeCheck" }, "insight", "global", false, "human");
                await engine.AddInterviewQuestionAsync("Contoso home check question?", "refusal", null, null, null, null, null,
                    null, null, true, null, "user", "global", "human");

                Assert.Contains("Contoso home check lesson", File.ReadAllText(Path.Combine(scratch, KnowledgeStore.DirName, KnowledgeStore.FileName)));
                Assert.Contains("Contoso home check question?", File.ReadAllText(Path.Combine(scratch, InterviewStore.DirName, InterviewStore.FileName)));
            }
            finally { try { Directory.Delete(workspace, true); } catch { } }

            foreach (var (before, path) in new[] { (insightsBefore, realInsights), (questionsBefore, realQuestions), (experienceBefore, realExperience) })
            {
                var after = Snapshot.Of(path);
                Assert.Equal(before.Exists, after.Exists);
                Assert.Equal(before.Written, after.Written);
                Assert.Equal(before.Bytes ?? Array.Empty<byte>(), after.Bytes ?? Array.Empty<byte>());
            }
        });
    }
}
