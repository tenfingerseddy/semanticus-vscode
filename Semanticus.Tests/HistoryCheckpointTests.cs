using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.Entitlement;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>R7 pins git as the durable Edit History store: model-only commits, explicit preview, rescue-first
    /// restore on the current branch, automatic reopen, and refusal when unrelated staged work could be swept in.</summary>
    public sealed class HistoryCheckpointTests
    {
        private sealed class Pro : IEntitlement
        {
            public bool IsPro => true;
            public EntitlementInfo Info => new EntitlementInfo { Tier = "pro" };
        }

        private static (string Dir, string Model) Repo()
        {
            var dir = Path.Combine(Path.GetTempPath(), "history-checkpoint-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var model = Path.Combine(dir, "model.bim");
            File.Copy(TestModels.FindBim(), model);
            File.WriteAllText(Path.Combine(dir, "notes.txt"), "leave me alone");
            Git(dir, "init", "-q");
            Git(dir, "config", "user.email", "history@test.local");
            Git(dir, "config", "user.name", "History Test");
            Git(dir, "config", "commit.gpgsign", "false");
            Git(dir, "add", "--", "model.bim", "notes.txt");
            Git(dir, "commit", "-q", "-m", "initial");
            return (dir, model);
        }

        private static string Git(string dir, params string[] args)
        {
            var psi = new ProcessStartInfo("git") { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var arg in args) psi.ArgumentList.Add(arg);
            using var p = Process.Start(psi);
            var stdout = p.StandardOutput.ReadToEnd();
            var stderr = p.StandardError.ReadToEnd();
            p.WaitForExit();
            if (p.ExitCode != 0) throw new InvalidOperationException(stderr);
            return stdout.Trim();
        }

        private static async Task<(LocalEngine Engine, string Ref)> Open(string model)
        {
            var engine = new LocalEngine(new SessionManager(), new Pro());
            await engine.OpenAsync(model);
            return (engine, (await engine.ListMeasuresAsync()).First().Ref);
        }

        [Fact]
        public async Task Preview_and_commit_are_model_scoped_and_listed_from_git()
        {
            var (dir, model) = Repo();
            try
            {
                var (engine, measure) = await Open(model);
                using (engine)
                {
                    await engine.SetDaxAsync(measure, "1", "human");
                    var preview = await engine.CreateHistoryCheckpointAsync("Accepted total", commit: false, "human");
                    Assert.True(preview.Preview);
                    Assert.True(preview.SavedModelFirst);
                    Assert.False(preview.Committed);

                    var made = await engine.CreateHistoryCheckpointAsync("Accepted total", commit: true, "human");
                    Assert.Null(made.Error);
                    Assert.True(made.Committed);
                    Assert.Equal(40, made.Checkpoint.Hash.Length);
                    Assert.Contains(made.Files, x => x == "model.bim");
                    Assert.DoesNotContain(made.Files, x => x == "notes.txt");

                    var list = await engine.ListHistoryCheckpointsAsync();
                    Assert.True(list.Supported);
                    var checkpoint = Assert.Single(list.Checkpoints);
                    Assert.Equal("Accepted total", checkpoint.Label);
                    Assert.Equal(made.Checkpoint.Hash, checkpoint.Hash);
                    Assert.Equal("model.bim", checkpoint.ModelPath);
                    Assert.Contains("Semanticus-Checkpoint: 1", Git(dir, "show", "-s", "--format=%b", checkpoint.Hash));
                }
            }
            finally { Delete(dir); }
        }

        [Fact]
        public async Task Unrelated_staged_work_refuses_without_committing_it()
        {
            var (dir, model) = Repo();
            try
            {
                var (engine, measure) = await Open(model);
                using (engine)
                {
                    await engine.SetDaxAsync(measure, "2", "human");
                    File.WriteAllText(Path.Combine(dir, "notes.txt"), "my unfinished notes");
                    Git(dir, "add", "--", "notes.txt");
                    var before = Git(dir, "rev-parse", "HEAD");

                    var made = await engine.CreateHistoryCheckpointAsync("Must stay scoped", commit: true, "human");
                    Assert.Contains("Unrelated files are already staged", made.Error);
                    Assert.Contains("notes.txt", made.Error);
                    Assert.Equal(before, Git(dir, "rev-parse", "HEAD"));
                    Assert.Contains("notes.txt", Git(dir, "diff", "--cached", "--name-only"));
                }
            }
            finally { Delete(dir); }
        }

        [Fact]
        public async Task Restore_creates_rescue_and_restored_commits_reopens_model_and_keeps_branch()
        {
            var (dir, model) = Repo();
            try
            {
                var (engine, measure) = await Open(model);
                using (engine)
                {
                    var branch = Git(dir, "branch", "--show-current");
                    await engine.SetDaxAsync(measure, "1", "human");
                    var first = await engine.CreateHistoryCheckpointAsync("Known good", commit: true, "human");
                    await engine.SetDaxAsync(measure, "2", "human");
                    await engine.CreateHistoryCheckpointAsync("Experiment", commit: true, "human");

                    var preview = await engine.RestoreHistoryCheckpointAsync(first.Checkpoint.ShortHash, restore: false, "human");
                    Assert.True(preview.Preview);
                    Assert.Equal(first.Checkpoint.Hash, preview.Target.Hash);

                    var restored = await engine.RestoreHistoryCheckpointAsync(first.Checkpoint.Hash, restore: true, "human");
                    Assert.Null(restored.Error);
                    Assert.True(restored.Restored);
                    Assert.NotNull(restored.RescueCheckpoint);
                    Assert.NotNull(restored.RestoredCheckpoint);
                    Assert.NotEqual(restored.RescueCheckpoint.Hash, restored.RestoredCheckpoint.Hash);
                    Assert.Equal(branch, Git(dir, "branch", "--show-current"));
                    Assert.Equal("leave me alone", File.ReadAllText(Path.Combine(dir, "notes.txt")));
                    Assert.Equal("1", (await engine.ListMeasuresAsync()).Single(x => x.Ref == measure).Expression);
                    Assert.Equal("", Git(dir, "status", "--porcelain", "--", "model.bim"));
                }
            }
            finally { Delete(dir); }
        }

        private static void Delete(string dir)
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                    try { File.SetAttributes(file, FileAttributes.Normal); } catch { }
                Directory.Delete(dir, true);
            }
            catch { }
        }
    }
}
