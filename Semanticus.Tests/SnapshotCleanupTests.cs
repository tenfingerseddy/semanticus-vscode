using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.Entitlement;
using Xunit;
using TOM = Microsoft.AnalysisServices.Tabular;

namespace Semanticus.Tests
{
    /// <summary>
    /// #122: open_live / open_local write the target's FULL model metadata — measures, M queries, role filters,
    /// datasource connection strings — to a temp dir and adopt it as the editable copy. That dir was only ever deleted
    /// when the open FAILED, so every successful open leaked one, forever (3,490 files / 2.4 GB of client production
    /// metadata on one real machine). The engine now owns the dir for the life of the session it backs, and prunes
    /// what earlier runs abandoned.
    /// </summary>
    public sealed class SnapshotCleanupTests : IDisposable
    {
        private readonly string _root;

        public SnapshotCleanupTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "sem-snap-tests-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(_root);
            LiveModelExport.TempRootOverride = _root;
        }

        public void Dispose()
        {
            LiveModelExport.TempRootOverride = null;
            try { Directory.Delete(_root, true); } catch { }
        }

        private sealed class Fake : IEntitlement
        {
            public bool IsPro => true;
            public EntitlementInfo Info => new EntitlementInfo { Tier = "pro" };
        }

        // A snapshot dir holding a real, openable .bim — the shape open_live produces.
        private string MakeSnapshotDir(string name = "Model")
        {
            var dir = Path.Combine(_root, Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
            var db = new TOM.Database("d") { CompatibilityLevel = 1600, Model = new TOM.Model() };
            db.Model.Tables.Add(new TOM.Table { Name = "Sales" });
            File.WriteAllText(Path.Combine(dir, name + ".bim"), TOM.JsonSerializer.SerializeDatabase(db));
            return dir;
        }

        private static string BimIn(string dir) => Directory.EnumerateFiles(dir, "*.bim").Single();

        private void Age(string dir, TimeSpan by)
        {
            var t = DateTime.UtcNow - by;
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)) File.SetLastWriteTimeUtc(f, t);
            Directory.SetLastWriteTimeUtc(dir, t);
        }

        // ---- The leak itself: opening a second model releases the first snapshot's dir. ----
        [Fact]
        public async Task Opening_another_model_deletes_the_previous_snapshot_dir()
        {
            using var engine = new LocalEngine(new SessionManager(), new Fake());
            var live = MakeSnapshotDir("Live");
            var other = MakeSnapshotDir("Other");

            engine.TrackSnapshotDir(BimIn(live));          // as open_live would
            await engine.OpenAsync(BimIn(other));           // ...then the user opens something else

            Assert.False(Directory.Exists(live));           // the leaked copy of the live model's metadata is gone
            Assert.True(Directory.Exists(other));           // the one we just opened is untouched
        }

        // ---- Re-opening the SAME snapshot by its own path must not delete the file mid-open. ----
        [Fact]
        public async Task Reopening_the_tracked_snapshot_does_not_delete_it()
        {
            using var engine = new LocalEngine(new SessionManager(), new Fake());
            var live = MakeSnapshotDir("Live");

            engine.TrackSnapshotDir(BimIn(live));
            await engine.OpenAsync(BimIn(live));

            Assert.True(File.Exists(BimIn(live)));
        }

        // ---- A FAILED open must not destroy the working copy the user still has. Release happens after success. ----
        [Fact]
        public async Task A_failed_open_leaves_the_tracked_snapshot_alone()
        {
            using var engine = new LocalEngine(new SessionManager(), new Fake());
            var live = MakeSnapshotDir("Live");
            engine.TrackSnapshotDir(BimIn(live));

            await Assert.ThrowsAnyAsync<Exception>(() => engine.OpenAsync(Path.Combine(_root, "does-not-exist.bim")));

            Assert.True(Directory.Exists(live));
        }

        // ---- Disposing the engine releases its snapshot: closing VS Code leaves no model metadata behind. ----
        [Fact]
        public void Disposing_the_engine_deletes_its_snapshot_dir()
        {
            var live = MakeSnapshotDir("Live");
            using (var engine = new LocalEngine(new SessionManager(), new Fake()))
                engine.TrackSnapshotDir(BimIn(live));

            Assert.False(Directory.Exists(live));
        }

        // ---- The sweep reclaims what earlier runs abandoned, and only that. ----
        [Fact]
        public void Sweep_removes_stale_dirs_keeps_fresh_ones_and_never_touches_the_kept_dir()
        {
            var stale = MakeSnapshotDir("Stale");
            var fresh = MakeSnapshotDir("Fresh");
            var mine = MakeSnapshotDir("Mine");
            Age(stale, TimeSpan.FromDays(3));
            Age(mine, TimeSpan.FromDays(3));   // old, but it is the caller's own — never swept

            var swept = LiveModelExport.SweepStale(TimeSpan.FromDays(1), keep: mine);

            Assert.Equal(1, swept);
            Assert.False(Directory.Exists(stale));
            Assert.True(Directory.Exists(fresh));
            Assert.True(Directory.Exists(mine));
        }

        // ---- Staleness is the newest write INSIDE the dir: a snapshot saved back to is live, however old its dir node.
        [Fact]
        public void A_dir_whose_contents_were_recently_written_is_not_stale()
        {
            var dir = MakeSnapshotDir("Saved");
            Age(dir, TimeSpan.FromDays(5));
            File.WriteAllText(BimIn(dir), "{}");   // the user saved back to it just now

            Assert.Equal(0, LiveModelExport.SweepStale(TimeSpan.FromDays(1)));
            Assert.True(Directory.Exists(dir));
        }

        [Fact]
        public void Sweep_on_a_missing_root_is_a_no_op()
        {
            LiveModelExport.TempRootOverride = Path.Combine(_root, "nope");
            Assert.Equal(0, LiveModelExport.SweepStale(TimeSpan.FromDays(1)));
        }

        // ---- A snapshot the user SAVED INTO must survive. `save_model` with no argument writes back to this .bim, so
        // discarding it on close would destroy work the user was told had been saved. ----
        [Fact]
        public void An_edited_snapshot_is_never_deleted_on_release()
        {
            var live = MakeSnapshotDir("Edited");
            using (var engine = new LocalEngine(new SessionManager(), new Fake()))
            {
                engine.TrackSnapshotDir(BimIn(live));
                File.WriteAllText(BimIn(live), "{ \"edited\": true }");   // the user saved
            }
            Assert.True(Directory.Exists(live));
            Assert.False(File.Exists(Path.Combine(live, ".inuse")));   // ...but the claim is released
        }

        [Fact]
        public void An_untouched_snapshot_is_reclaimed_on_release()
        {
            var live = MakeSnapshotDir("Untouched");
            using (var engine = new LocalEngine(new SessionManager(), new Fake()))
                engine.TrackSnapshotDir(BimIn(live));

            Assert.False(Directory.Exists(live));
        }

        // ---- A dir claimed by a LIVE process is never swept, however old. An open-but-idle session rewrites nothing,
        // so age alone would let one engine delete another engine's working model. ----
        [Fact]
        public void Sweep_never_touches_a_dir_claimed_by_a_running_process()
        {
            var held = MakeSnapshotDir("Held");
            LiveModelExport.MarkInUse(held);       // this very process
            Age(held, TimeSpan.FromDays(30));

            Assert.Equal(0, LiveModelExport.SweepStale(TimeSpan.FromDays(1)));
            Assert.True(Directory.Exists(held));
        }

        // ---- ...but a marker from a DEAD process must not wedge the dir forever, or a crash would leak it again. ----
        [Fact]
        public void Sweep_reclaims_a_dir_whose_claiming_process_is_gone()
        {
            var orphan = MakeSnapshotDir("Orphan");
            File.WriteAllText(Path.Combine(orphan, ".inuse"), "999999999");   // a pid that cannot be running
            Age(orphan, TimeSpan.FromDays(3));

            Assert.Equal(1, LiveModelExport.SweepStale(TimeSpan.FromDays(1)));
            Assert.False(Directory.Exists(orphan));
        }

        // ---- The tracked dir authorises a recursive delete, so it must never be able to name a user's own folder. ----
        [Fact]
        public void Tracking_a_path_outside_the_snapshot_root_is_refused()
        {
            using var engine = new LocalEngine(new SessionManager(), new Fake());
            var userDir = Path.Combine(Path.GetTempPath(), "sem-user-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(userDir);
            var bim = Path.Combine(userDir, "MyModel.bim");
            File.WriteAllText(bim, "{}");
            try
            {
                Assert.Throws<InvalidOperationException>(() => engine.TrackSnapshotDir(bim));
                engine.Dispose();
                Assert.True(Directory.Exists(userDir));   // the user's folder is untouched
            }
            finally { try { Directory.Delete(userDir, true); } catch { } }
        }
    }
}
