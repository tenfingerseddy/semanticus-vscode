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
                await engine.SaveAsync(dir, "TMDL", false, "human");                              // anchors SourcePath = dir; sidecar home = dir/.semanticus

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

        // UAT-S05a-2-003: the Diagram saves positions only (x/y, no size), and the engine wrote that missing size
        // as width 0 / height 0 over the stored one, so a drag erased every table's saved box and get_layout returned
        // the wrong dimensions. An omitted size must keep the size already stored, from the sidecar or, on a PBIP,
        // from Power BI Desktop's own diagramLayout.json.
        [Fact]
        public async Task A_position_only_save_keeps_the_stored_width_and_height()
        {
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions);
            var root = Path.Combine(Path.GetTempPath(), "semanticus_layout_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            var def = Path.Combine(root, "M.SemanticModel", "definition");
            try
            {
                await engine.OpenAsync(TestModels.FindBim());
                var tables = (await engine.GetModelGraphAsync()).Tables.Select(t => t.Name).ToArray();
                var sized = tables[0];      // sized through save_layout, as the agent did
                var native = tables[1];     // sized only by Power BI Desktop's native layout
                await engine.SaveAsync(def, "TMDL", false, "human");
                File.WriteAllText(Path.Combine(root, "M.SemanticModel", "diagramLayout.json"),
                    "{\"version\":\"1.1.0\",\"diagrams\":[{\"ordinal\":0,\"name\":\"All tables\",\"nodes\":[" +
                    "{\"nodeIndex\":\"" + native + "\",\"location\":{\"x\":10,\"y\":20},\"size\":{\"width\":234,\"height\":312}}]}]}");

                await engine.SaveLayoutAsync(new[] { new LayoutNode { Name = sized, X = 360, Y = 180, Width = 240, Height = 420 } }, "agent");

                // The Diagram's drag: every table, position only (diagram.tsx positionsToLayoutNodes sends no size).
                await engine.SaveLayoutAsync(new[]
                {
                    new LayoutNode { Name = sized, X = 500, Y = 180 },
                    new LayoutNode { Name = native, X = 40, Y = 60 },
                }, "human");

                var got = await engine.GetLayoutAsync();
                var s = got.Tables.Single(t => t.Name == sized);
                Assert.Equal(500, s.X);
                Assert.Equal(240, s.Width); Assert.Equal(420, s.Height);
                var n = got.Tables.Single(t => t.Name == native);
                Assert.Equal(40, n.X);
                Assert.Equal(234, n.Width); Assert.Equal(312, n.Height);

                // A size that IS sent still wins.
                await engine.SaveLayoutAsync(new[] { new LayoutNode { Name = sized, X = 500, Y = 180, Width = 300, Height = 200 } }, "agent");
                var resized = (await engine.GetLayoutAsync()).Tables.Single(t => t.Name == sized);
                Assert.Equal(300, resized.Width); Assert.Equal(200, resized.Height);
            }
            finally
            {
                engine.Dispose();
                try { Directory.Delete(root, true); } catch { /* best-effort temp cleanup */ }
            }
        }

        // Kane, 28 Sep: when a saved layout leaves no room for the lines, the Diagram moves tables apart and saves the
        // new spots, and one Undo puts them back. The put-back must stick for both doors: it is saved as `keep`, which
        // get_layout returns (so the assistant sees it and the Diagram leaves those tables alone after a reload), which
        // the assistant can set through save_layout too, and which any later save of that table without it clears.
        [Fact]
        public async Task A_kept_position_round_trips_to_both_doors_and_a_later_save_without_keep_clears_it()
        {
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions);
            var dir = Path.Combine(Path.GetTempPath(), "semanticus_layout_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                await engine.OpenAsync(TestModels.FindBim());
                var tables = (await engine.GetModelGraphAsync()).Tables.Select(t => t.Name).ToArray();
                await engine.SaveAsync(dir, "TMDL", false, "human");
                LayoutChange heard = null;
                sessions.Bus.LayoutChanged += c => heard = c;

                // The person's put-back (the webview saves as "human"), and a plain save of another table.
                await engine.SaveLayoutAsync(new[]
                {
                    new LayoutNode { Name = tables[0], X = 10, Y = 20, Keep = true },
                    new LayoutNode { Name = tables[1], X = 30, Y = 40 },
                }, "human");
                var got = await engine.GetLayoutAsync();
                Assert.True(got.Tables.Single(t => t.Name == tables[0]).Keep);
                Assert.Null(got.Tables.Single(t => t.Name == tables[1]).Keep);
                Assert.True(heard.Tables.Single(t => t.Name == tables[0]).Keep);          // the live broadcast carries it too
                Assert.Contains("\"keep\": true", File.ReadAllText(Path.Combine(dir, ".semanticus", "layout.json")));

                // A save that omits the kept table leaves its mark; the assistant can ask for a kept spot itself.
                await engine.SaveLayoutAsync(new[] { new LayoutNode { Name = tables[1], X = 31, Y = 41, Keep = true } }, "agent");
                got = await engine.GetLayoutAsync();
                Assert.True(got.Tables.Single(t => t.Name == tables[0]).Keep);
                Assert.True(got.Tables.Single(t => t.Name == tables[1]).Keep);

                // Moving a kept table without keep (a drag, or the Diagram making room) clears its mark.
                await engine.SaveLayoutAsync(new[] { new LayoutNode { Name = tables[0], X = 90, Y = 20 } }, "human");
                got = await engine.GetLayoutAsync();
                Assert.Null(got.Tables.Single(t => t.Name == tables[0]).Keep);
                Assert.True(got.Tables.Single(t => t.Name == tables[1]).Keep);
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
