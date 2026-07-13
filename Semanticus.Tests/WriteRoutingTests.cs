using System;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.Entitlement;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// Pins the write-routing boundary (D2) and the conflict policy (D3) documented in docs/op-routing-map.md — the
    /// resolution of hole #2 (TMDL apply can't create top-level objects; the typed path must). These are contracts,
    /// not incidental behavior, so they get tests. Each builds its OWN fresh CL-1604 model (the real authoring target)
    /// and drives the public <see cref="IEngine"/> both doors share.
    /// </summary>
    public sealed class WriteRoutingTests
    {
        private sealed class Pro : IEntitlement
        {
            public bool IsPro => true;
            public EntitlementInfo Info => new EntitlementInfo { Tier = "pro" };
        }

        [Fact]
        public async Task Tmdl_apply_rejects_a_new_top_level_object_typed_op_creates_it()
        {
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions);
            try
            {
                await engine.CreateModelAsync("RoutingTest", 1604);   // fresh modern model (the real target CL)
                const string name = "RoutingProbe";

                // (1) TMDL APPLY cannot create a brand-new top-level object — it is SKIPPED with a message that routes
                //     the caller to the typed path (so the undo timeline stays reversible — see op-routing-map.md "Why").
                var rejected = await engine.ApplyTmdlScriptAsync("table " + name + "\n", "agent");
                Assert.Empty(rejected.Applied);
                Assert.Single(rejected.Skipped);
                Assert.Contains("does not exist", rejected.Skipped[0]);
                Assert.Contains("typed actions", rejected.Skipped[0]);
                Assert.DoesNotContain((await engine.GetModelGraphAsync()).Tables, t => t.Name == name);

                // (2) The TYPED op creates it (the only create route).
                var tableRef = await engine.CreateTableAsync(name, "agent");
                await engine.CreateMeasureAsync("table:" + name, "Probe M", "1", "agent");
                Assert.Contains((await engine.GetModelGraphAsync()).Tables, t => t.Name == name);

                // (3) Now that it EXISTS, TMDL apply edits it (script → re-apply round-trips the existing-object route).
                var tmdl = await engine.ScriptObjectsAsync(new[] { tableRef }, "TMDL");
                var edited = await engine.ApplyTmdlScriptAsync(tmdl, "agent");
                Assert.Empty(edited.Skipped);
                Assert.Contains(edited.Applied, p => p.Contains(name));

                // (4) The TYPED delete removes a table (the only delete route).
                var ref2 = await engine.CreateTableAsync(name + "2", "agent");
                await engine.DeleteObjectAsync(ref2, "agent");
                Assert.DoesNotContain((await engine.GetModelGraphAsync()).Tables, t => t.Name == name + "2");
            }
            finally { engine.Dispose(); }
        }

        [Fact]
        public async Task Interleaved_writes_to_the_same_object_are_last_writer_wins_and_undoable()
        {
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions);
            try
            {
                await engine.CreateModelAsync("ConflictTest", 1604);
                await engine.CreateTableAsync("T", "agent");

                // Two drivers edit the SAME object's SAME property in two dispatcher turns. There is no lock and no
                // merge: the LATER write wins, the model stays coherent, undo reverts to the prior writer's value (D3).
                var mref = await engine.CreateMeasureAsync("table:T", "ConflictM", "1", "agent");
                await engine.SetDaxAsync(mref, "2", "human");   // writer A
                await engine.SetDaxAsync(mref, "3", "agent");   // writer B (later) — wins
                Assert.Equal("3", Expr(await engine.ListMeasuresAsync(), "ConflictM"));

                await engine.UndoAsync("human");                // one shared timeline → reverts B to A's value
                Assert.Equal("2", Expr(await engine.ListMeasuresAsync(), "ConflictM"));

                await engine.UndoAsync("human");                // and back to the original
                Assert.Equal("1", Expr(await engine.ListMeasuresAsync(), "ConflictM"));
            }
            finally { engine.Dispose(); }
        }

        [Fact]
        public async Task Measure_tmdl_round_trip_updates_only_the_selected_measure_and_is_undoable()
        {
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, new Pro());
            try
            {
                await engine.CreateModelAsync("MeasureTmdl", 1604);
                await engine.CreateTableAsync("T", "agent");
                var selected = await engine.CreateMeasureAsync("table:T", "Selected", "1", "agent");
                var untouched = await engine.CreateMeasureAsync("table:T", "Untouched", "2", "agent");

                var script = await engine.ScriptObjectsAsync(new[] { selected }, "tmdl");
                var untouchedBytes = await engine.ScriptObjectsAsync(new[] { untouched }, "tmdl");
                Assert.StartsWith("// @object measure:T/Selected", script);
                Assert.Contains("ref table T", script);
                Assert.DoesNotContain("Untouched", script);

                var editedScript = script.Replace("= 1", "= 10");
                Assert.NotEqual(script, editedScript);
                var edited = await engine.ApplyTmdlScriptAsync(editedScript, "human");
                Assert.Empty(edited.Skipped);
                Assert.Equal(new[] { selected }, edited.Applied);
                Assert.Equal("10", Expr(await engine.ListMeasuresAsync(), "Selected"));
                Assert.Equal("2", Expr(await engine.ListMeasuresAsync(), "Untouched"));
                Assert.Equal(untouchedBytes, await engine.ScriptObjectsAsync(new[] { untouched }, "tmdl"));

                await engine.UndoAsync("human");
                Assert.Equal("1", Expr(await engine.ListMeasuresAsync(), "Selected"));
                Assert.Equal("2", Expr(await engine.ListMeasuresAsync(), "Untouched"));

                var unsafeRename = await engine.ApplyTmdlScriptAsync(script.Replace("measure Selected", "measure Renamed"), "human");
                Assert.Empty(unsafeRename.Applied);
                Assert.Contains(unsafeRename.Skipped, x => x.Contains("rename_object"));
                Assert.DoesNotContain((await engine.ListMeasuresAsync()), x => x.Name == "Renamed");

                var vanishedParent = await engine.ApplyTmdlScriptAsync(script.Replace("ref table T", "ref table Vanished"), "human");
                Assert.Empty(vanishedParent.Applied);
                Assert.Contains(vanishedParent.Skipped, x => x.Contains("parent-table reference changed"));
                Assert.Equal("1", Expr(await engine.ListMeasuresAsync(), "Selected"));
                Assert.Equal(untouchedBytes, await engine.ScriptObjectsAsync(new[] { untouched }, "tmdl"));
            }
            finally { engine.Dispose(); }
        }

        [Fact]
        public async Task Multi_measure_tmdl_round_trip_is_one_undoable_selection()
        {
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, new Pro());
            try
            {
                await engine.CreateModelAsync("MultiMeasureTmdl", 1604);
                await engine.CreateTableAsync("T", "agent");
                await engine.CreateTableAsync("U", "agent");
                var first = await engine.CreateMeasureAsync("table:T", "First", "1", "agent");
                var second = await engine.CreateMeasureAsync("table:U", "Second", "2", "agent");

                var script = await engine.ScriptObjectsAsync(new[] { first, second }, "tmdl");
                Assert.Equal(2, script.Split("// @object").Length - 1);
                var edited = await engine.ApplyTmdlScriptAsync(script.Replace("= 1", "= 10").Replace("= 2", "= 20"), "human");
                Assert.Empty(edited.Skipped);
                Assert.Equal(new[] { first, second }, edited.Applied);
                Assert.Equal("10", Expr(await engine.ListMeasuresAsync(), "First"));
                Assert.Equal("20", Expr(await engine.ListMeasuresAsync(), "Second"));

                await engine.UndoAsync("human");
                Assert.Equal("1", Expr(await engine.ListMeasuresAsync(), "First"));
                Assert.Equal("2", Expr(await engine.ListMeasuresAsync(), "Second"));
            }
            finally { engine.Dispose(); }
        }

        [Fact]
        public async Task Deleting_a_table_after_a_CL_bump_with_roles_gives_a_clear_error_not_an_NRE()
        {
            // A known vendored TOMWrapper limitation (docs/op-routing-map.md): a ModelRole created at CL<1400 keeps a
            // NULL MetadataPermission (its OLS indexer is built in ModelRole.Init only at CL>=1400), and a CL bump does
            // NOT rebuild it (the vendored Reinit reuses existing wrappers). Deleting a table then clears OLS across
            // every role and the vendored code throws a bare NullReferenceException. We can't fix the vendored wrapper
            // from our layer (without modifying the pinned submodule), so DeleteObjectAsync GUARDS it: a clear,
            // actionable InvalidOperationException instead of an opaque NRE. (A natively-modern model is unaffected; its
            // roles have OLS indexers — see Tmdl_apply_rejects_..., which deletes on a fresh CL-1604 model and succeeds.)
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions);
            try
            {
                await engine.OpenAsync(TestModels.FindBim());                   // AdventureWorks @ CL 1200
                await engine.CreateRoleAsync("OlsDeleteProbe", "Read", "agent");// role at CL<1400 → null OLS indexer
                await engine.SetCompatibilityLevelAsync(1604, "agent");         // crosses the 1400 OLS boundary (no wrapper rebuild)
                var tref = await engine.CreateTableAsync("OlsDelProbeTbl", "agent");

                var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => engine.DeleteObjectAsync(tref, "agent"));
                Assert.Contains("compatibility level 1400", ex.Message);
                Assert.Contains("Reopen", ex.Message);

                // The guard rolled the delete back cleanly — the model is still coherent and the table remains.
                Assert.Contains((await engine.GetModelGraphAsync()).Tables, t => t.Name == "OlsDelProbeTbl");
            }
            finally { engine.Dispose(); }
        }

        private static string Expr(MeasureRow[] rows, string name) => rows.First(m => m.Name == name).Expression;
    }
}
