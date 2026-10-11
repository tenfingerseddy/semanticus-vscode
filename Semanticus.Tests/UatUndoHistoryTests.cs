using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// Lane m11/uat-undo, item U3 (Sol UAT-F-S07a-1-002, UAT-F-S09-1-005): History must keep an edit's undone state
    /// when the engine restarts or a saved version is restored. The model has the edit taken back; a row that reads
    /// active says the opposite. Class: a ledger flag that is saved with the entry but ignored when the entry belongs
    /// to an earlier session (the same family as UAT S12-3-003, which fixed it for the current session).
    /// </summary>
    public sealed class UatUndoHistoryTests
    {
        private static string Git(string dir, params string[] args)
        {
            var psi = new ProcessStartInfo("git") { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var arg in args) psi.ArgumentList.Add(arg);
            using var p = Process.Start(psi)!;
            var stdout = p.StandardOutput.ReadToEnd();
            var stderr = p.StandardError.ReadToEnd();
            p.WaitForExit();
            if (p.ExitCode != 0) throw new InvalidOperationException(stderr);
            return stdout.Trim();
        }

        [Fact]
        public async Task An_edit_undone_before_an_engine_restart_still_reads_undone_after_it()
        {
            var dir = Path.Combine(Path.GetTempPath(), "uu-h-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
            try
            {
                var model = LedgerFixture.Write(dir, "model.bim");
                var sessions = new SessionManager();
                string kept;
                using (var engine = new LocalEngine(sessions, TestEntitlements.Pro))
                {
                    await engine.OpenAsync(model);
                    await engine.SetDescriptionAsync("measure:Sales/M1", "first words", "human");
                    await engine.SetDescriptionAsync("measure:Sales/M2", "second words", "human");
                    await engine.SetDescriptionAsync("measure:Sales/M3", "third words", "human");
                    await engine.UndoAsync("human");   // M3 taken back
                    await engine.UndoAsync("human");   // M2 taken back
                    kept = (await engine.GetPlanHistoryAsync()).Rows.Single(r => r.Deltas.Any(d => d.Ref == "measure:Sales/M1")).Id;
                    await sessions.Ledger.FlushAsync();
                }

                var again = new SessionManager();
                using var next = new LocalEngine(again, TestEntitlements.Pro);
                await next.OpenAsync(model);
                var rows = (await next.GetPlanHistoryAsync()).Rows.Where(r => r.Kind == "edit").ToList();
                Assert.Equal(3, rows.Count);
                Assert.All(rows, r => Assert.True(r.BeforeThisSession));
                Assert.False(rows.Single(r => r.Id == kept).Undone, "the edit that stayed in the model is not struck");
                var undone = rows.Where(r => r.Id != kept).ToList();
                Assert.Equal(2, undone.Count);
                Assert.All(undone, r => Assert.True(r.Undone, "an edit taken back before the restart reads undone after it"));
                Assert.True(string.IsNullOrEmpty(await again.Current.ReadAsync(m => m.Tables["Sales"].Measures["M2"].Description)));
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        [Fact]
        public async Task An_edit_undone_before_a_saved_version_is_restored_still_reads_undone_after_the_restore()
        {
            var dir = Path.Combine(Path.GetTempPath(), "uu-r-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
            try
            {
                var model = LedgerFixture.Write(dir, "model.bim");
                Git(dir, "init", "-q");
                Git(dir, "config", "user.email", "history@test.local");
                Git(dir, "config", "user.name", "History Test");
                Git(dir, "config", "commit.gpgsign", "false");
                Git(dir, "add", "--", "model.bim");
                Git(dir, "commit", "-q", "-m", "initial");

                var sessions = new SessionManager();
                using var engine = new LocalEngine(sessions, TestEntitlements.Pro);
                await engine.OpenAsync(model);
                await engine.SetDescriptionAsync("measure:Sales/M1", "to be undone", "human");
                await engine.UndoAsync("human");
                var good = await engine.CreateHistoryCheckpointAsync("Known good", commit: true, "human");
                Assert.True(good.Committed, good.Error);
                var restored = await engine.RestoreHistoryCheckpointAsync(good.Checkpoint.Hash, restore: true, "human");
                Assert.True(restored.Restored, restored.Error);

                var edit = Assert.Single((await engine.GetPlanHistoryAsync()).Rows, r => r.Kind == "edit");
                Assert.True(edit.BeforeThisSession);
                Assert.True(edit.Undone, "the restore did not bring the edit back, so the row must not read active");
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }
    }
}
