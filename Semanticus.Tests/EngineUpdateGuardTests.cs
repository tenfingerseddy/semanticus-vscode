using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ModelContextProtocol.Protocol;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>Kane, 25 September, on the Yoga. A new build was installed over the same version (1.2.1) while two
    /// older engines kept running: the one VS Code had started, and a pair from the assistant's connection that
    /// morning. The newer Studio page then showed the older engine's raw "No method by the name
    /// 'describeWorkflowSteps' is found."
    ///
    /// The assistant's door holds two rules here. When THIS process is older than the files now installed beside it,
    /// every tool result says so plainly and says to restart the assistant. When the engine this process joined is
    /// older and has no such method, the result is one plain sentence, never the RPC text. The running build is the
    /// one read when the process started, so a reinstall under it can never make it report the new files as itself.</summary>
    public sealed class EngineUpdateGuardTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "semanticus-update-guard-" + Guid.NewGuid().ToString("N"));

        public EngineUpdateGuardTests() => Directory.CreateDirectory(_dir);

        public void Dispose()
        {
            try { Directory.Delete(_dir, true); } catch { /* temp */ }
        }

        private void Put(string name, string content, DateTime? stamp = null)
        {
            var path = Path.Combine(_dir, name);
            File.WriteAllText(path, content);
            if (stamp != null) File.SetLastWriteTimeUtc(path, stamp.Value);
        }

        private static string TextOf(CallToolResult r) => string.Join("\n", r.Content.OfType<TextContentBlock>().Select(b => b.Text));

        // ---- (1) is this process older than the files installed beside it? ---------------------------------------
        [Fact]
        public void The_same_files_are_not_an_update()
        {
            Put("Semanticus.Engine.dll", "engine-v1");
            var watch = new InstalledBuildWatch(_dir, BuildIdentity.ForDirectory(_dir));
            Assert.False(watch.Updated());
            Assert.False(watch.Updated(), "asking again changes nothing");
        }

        [Fact]
        public void An_in_place_reinstall_of_the_same_version_is_an_update()
        {
            Put("Semanticus.Engine.dll", "engine-v1", new DateTime(2026, 9, 25, 8, 0, 0, DateTimeKind.Utc));
            var watch = new InstalledBuildWatch(_dir, BuildIdentity.ForDirectory(_dir));
            Assert.False(watch.Updated());
            // Same folder, same file name, same version: only the bytes move, exactly Kane's reinstall.
            Put("Semanticus.Engine.dll", "engine-v2", new DateTime(2026, 9, 25, 9, 0, 0, DateTimeKind.Utc));
            Assert.True(watch.Updated());
        }

        [Fact]
        public void A_removed_install_folder_is_an_update()
        {
            // A version update installs into a new folder and VS Code removes the old one: the files this process came
            // from are gone, so it is certainly not the build a fresh start would run.
            Put("Semanticus.Engine.dll", "engine-v1");
            var watch = new InstalledBuildWatch(_dir, BuildIdentity.ForDirectory(_dir));
            Directory.Delete(_dir, true);
            Assert.True(watch.Updated());
        }

        [Fact]
        public void A_process_that_cannot_read_its_own_build_never_claims_an_update()
        {
            Put("Semanticus.Engine.dll", "engine-v1");
            Assert.False(new InstalledBuildWatch(_dir, null).Updated(), "a development run with no identity says nothing");
        }

        [Fact]
        public void Unchanged_files_are_not_hashed_again()
        {
            // Every tool call asks, so the answer has to be cheap: the hash is recomputed only when a file's size or
            // write time moved.
            Put("Semanticus.Engine.dll", "engine-v1");
            var watch = new InstalledBuildWatch(_dir, BuildIdentity.ForDirectory(_dir));
            for (var i = 0; i < 5; i++) watch.Updated();
            Assert.Equal(1, watch.Hashes);
        }

        // ---- (2) the assistant's tool results say so -------------------------------------------------------------
        private static ValueTask<CallToolResult> Ok() => new ValueTask<CallToolResult>(new CallToolResult
        {
            Content = new List<ContentBlock> { new TextContentBlock { Text = "{\"ok\":true}" } },
        });

        [Fact]
        public async Task An_outdated_process_says_so_on_every_result()
        {
            var ok = await EngineUpdateGuard.InvokeAsync(() => true, Ok);
            Assert.Contains("{\"ok\":true}", TextOf(ok));
            Assert.Contains(EngineUpdateGuard.AssistantOutdatedText, TextOf(ok));

            var failed = await EngineUpdateGuard.InvokeAsync(() => true, () => new ValueTask<CallToolResult>(new CallToolResult
            {
                IsError = true,
                Content = new List<ContentBlock> { new TextContentBlock { Text = "run_dax failed: bad query" } },
            }));
            Assert.Contains(EngineUpdateGuard.AssistantOutdatedText, TextOf(failed));
        }

        [Fact]
        public async Task A_current_process_adds_nothing()
        {
            var ok = await EngineUpdateGuard.InvokeAsync(() => false, Ok);
            Assert.Equal("{\"ok\":true}", TextOf(ok));
        }

        [Fact]
        public void The_notice_is_plain_and_says_to_restart()
        {
            foreach (var line in new[] { EngineUpdateGuard.AssistantOutdatedText, EngineUpdateGuard.PersonText })
            {
                Assert.StartsWith("Semanticus was updated", line);
                Assert.DoesNotContain('\u2014', line);
                Assert.DoesNotContain('\u2013', line);
                Assert.DoesNotContain("method", line, StringComparison.OrdinalIgnoreCase);
            }
            Assert.Contains("restart", EngineUpdateGuard.AssistantOutdatedText, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("Semanticus was updated. Close VS Code and your assistant, then open them again to finish.", EngineUpdateGuard.PersonText);
        }

        // ---- (3) an older engine behind this door: never the raw RPC text ------------------------------------------
        [Fact]
        public async Task A_missing_method_on_the_joined_engine_is_the_plain_sentence()
        {
            var res = await McpErrorBoundary.InvokeAsync("describe_workflow", () => throw new InvalidOperationException(
                "No method by the name 'describeWorkflowSteps' is found."));
            Assert.True(res.IsError);
            Assert.Contains(EngineUpdateGuard.PersonText, TextOf(res));
            Assert.DoesNotContain("No method by the name", TextOf(res));
        }

        [Fact]
        public async Task The_rpc_exception_type_itself_is_recognised()
        {
            // The type StreamJsonRpc throws for a missing remote method. Its constructor is not public, so it is built the
            // way the library builds it; the message is deliberately NOT the usual sentence, so only the type can match.
            var ctor = typeof(StreamJsonRpc.RemoteMethodNotFoundException)
                .GetConstructors(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)
                .Single(c => c.GetParameters().Length == 5);
            var ex = (Exception)ctor.Invoke(new object[] { "remote said no", "describeWorkflowSteps",
                StreamJsonRpc.Protocol.JsonRpcErrorCode.MethodNotFound, null, null });
            var res = await McpErrorBoundary.InvokeAsync("describe_workflow", () => throw new AggregateException(ex));
            Assert.Contains(EngineUpdateGuard.PersonText, TextOf(res));
        }

        [Fact]
        public async Task Other_failures_keep_their_own_message()
        {
            var res = await McpErrorBoundary.InvokeAsync("get_object", () => throw new InvalidOperationException("Object not found: measure:Sales/Nope"));
            Assert.Contains("Object not found", TextOf(res));
            Assert.DoesNotContain(EngineUpdateGuard.PersonText, TextOf(res));
        }

        // ---- (4) wiring ------------------------------------------------------------------------------------------
        [Fact]
        public void The_running_build_is_read_before_anything_else_starts()
        {
            // BuildIdentity.Current is read lazily from disk. Read late, after an in-place reinstall, it would report
            // the NEW files as this process's build and every comparison above would pass on old code. Main reads it
            // first thing, so the value is the files this process actually started from.
            var program = File.ReadAllText(Path.Combine(RepoRoot(), "Semanticus.Engine", "Program.cs"));
            var main = program.IndexOf("private static async Task<int> Main(", StringComparison.Ordinal);
            Assert.True(main > 0);
            var body = program.Substring(main, program.IndexOf("switch (mode)", main, StringComparison.Ordinal) - main);
            Assert.Contains("BuildIdentity.Current", body);

            var filters = File.ReadAllText(Path.Combine(RepoRoot(), "Semanticus.Engine", "McpDoorFilters.cs"));
            Assert.Contains("EngineUpdateGuard.Wrap", filters);
        }

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Semanticus.sln"))) dir = dir.Parent;
            return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
        }
    }
}
