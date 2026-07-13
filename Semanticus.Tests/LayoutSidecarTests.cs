using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// Hole #1 (docs/strategy/04 §D1): diagram positions used to live ONLY in the webview's per-machine state, so a
    /// layout never round-tripped through the engine and the diagram was "demo-only, not a claimed moat". This pins the
    /// engine-owned sidecar (.semanticus/layout.json): it round-trips, SURVIVES A TABLE RENAME via the LineageTag key
    /// (the whole reason the key isn't the name), and NEVER pollutes the model diff (the saved TMDL is byte-identical
    /// across a layout write). Drives the public <see cref="IEngine"/> both doors share.
    /// </summary>
    public sealed class LayoutSidecarTests
    {
        [Fact]
        public async Task Layout_round_trips_survives_rename_and_does_not_pollute_the_model_diff()
        {
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions);
            var dir = Path.Combine(Path.GetTempPath(), "semanticus_layout_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                await engine.OpenAsync(TestModels.FindBim());
                await engine.SetCompatibilityLevelAsync(1604, "agent");          // CL >= 1540 so a NEW table gets a LineageTag
                var existing = (await engine.ListMeasuresAsync()).First().Table; // an EXISTING (CL1200, tagless) table → Name key
                var probeRef = await engine.CreateTableAsync("LayoutProbe", "agent");        // created at 1604 → has a LineageTag
                await engine.CreateMeasureAsync("table:LayoutProbe", "Probe M", "42", "agent"); // non-empty table saves cleanly
                await engine.SaveAsync(dir, "TMDL");                              // anchors SourcePath = dir; sidecar home = dir/.semanticus

                // Save positions: the probe by ref, an existing table by NAME (exercises both resolution paths).
                var save = await engine.SaveLayoutAsync(new[]
                {
                    new LayoutNode { Ref = probeRef, X = 100, Y = 200, Width = 190, Height = 92 },
                    new LayoutNode { Name = existing, X = 300, Y = 400 },
                }, "agent");
                Assert.True(save.Saved);
                Assert.Equal(2, save.Count);
                Assert.Contains(".semanticus", save.Path);

                // (1) DIFF POLLUTION: the layout write must not touch the model — clean dirty-flag + byte-identical TMDL.
                Assert.False((await engine.SessionInfoAsync()).HasUnsavedChanges);
                Assert.True(File.Exists(Path.Combine(dir, ".semanticus", "layout.json")));
                var tmdlBefore = SnapshotTmdl(dir);
                await engine.SaveLayoutAsync(new[] { new LayoutNode { Ref = probeRef, X = 101, Y = 201 } }, "agent");
                Assert.Equal(tmdlBefore, SnapshotTmdl(dir));   // a second layout save still changed zero model files

                // (2) ROUND TRIP: get_layout returns the persisted positions, reconciled to live tables.
                var got = await engine.GetLayoutAsync();
                var probe = got.Tables.Single(t => t.Name == "LayoutProbe");
                Assert.Equal(101, probe.X); Assert.Equal(201, probe.Y);          // the latest (merged) write won
                var ex = got.Tables.Single(t => t.Name == existing);
                Assert.Equal(300, ex.X); Assert.Equal(400, ex.Y);                // the omitted table kept its prior position

                // (3) RENAME SURVIVAL: rename the tagged table; its box stays attached by LineageTag, not name.
                await engine.RenameObjectAsync(probeRef, "LayoutProbeRenamed", "agent");
                var after = await engine.GetLayoutAsync();
                Assert.DoesNotContain(after.Tables, t => t.Name == "LayoutProbe");
                var renamed = after.Tables.Single(t => t.Name == "LayoutProbeRenamed");
                Assert.Equal(101, renamed.X); Assert.Equal(201, renamed.Y);
                Assert.Equal(existing, after.Tables.Single(t => t.X == 300).Name);   // the tagless one is untouched
            }
            finally
            {
                engine.Dispose();
                try { Directory.Delete(dir, true); } catch { /* best-effort temp cleanup */ }
            }
        }

        // A stable string snapshot of every TMDL file's name+content, to assert a layout write changes ZERO model files.
        private static string SnapshotTmdl(string dir) => string.Join("\n",
            Directory.EnumerateFiles(dir, "*.tmdl", SearchOption.AllDirectories)
                     .OrderBy(p => p, StringComparer.Ordinal)
                     .Select(p => Path.GetFileName(p) + ":" + File.ReadAllText(p)));
    }
}
