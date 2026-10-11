using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// Raising a model's compatibility level past 1400 inside a session, then editing and undoing (found by lane C1,
    /// 24 Sep). The vendored wrapper builds its level-gated members (the OLS indexers on roles, tables and columns)
    /// only when each wrapper object is first initialised. Objects that existed before the raise kept nulls there, so undoing a table add on the raised AdventureWorks fixture (it has four
    /// roles) threw a NullReferenceException in TableOLSIndexer.SetValue. Undo is the safety net, so every edit
    /// below must undo cleanly after the raise.
    /// </summary>
    public sealed class CompatibilityRaiseUndoTests
    {
        private static async Task<LocalEngine> RaisedFixtureAsync()
        {
            var engine = new LocalEngine(new SessionManager(), TestEntitlements.Pro);
            await engine.OpenAsync(TestModels.FindBim());   // real saved CL-1200 model with roles and perspectives
            Assert.NotEmpty(await engine.ListRolesAsync());
            await engine.SetCompatibilityLevelAsync(1701, "human");
            return engine;
        }

        private static async Task<bool> HasTableAsync(LocalEngine engine, string name) =>
            (await engine.GetModelGraphAsync()).Tables.Any(t => t.Name == name);

        [Fact]
        public async Task Undoing_a_table_add_after_a_level_raise_removes_the_table()
        {
            using var engine = await RaisedFixtureAsync();
            await engine.CreateTableAsync("Raised Table", "agent");
            Assert.True(await HasTableAsync(engine, "Raised Table"));

            await engine.UndoAsync("human");
            Assert.False(await HasTableAsync(engine, "Raised Table"));

            await engine.RedoAsync("human");
            Assert.True(await HasTableAsync(engine, "Raised Table"));
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Undoing_a_column_add_on_an_existing_table_after_a_level_raise_removes_the_column()
        {
            using var engine = await RaisedFixtureAsync();
            var col = await engine.CreateColumnAsync("table:Currency", "Raised Column", "String", "RaisedColumn", "agent");
            Assert.NotNull(col);

            await engine.UndoAsync("human");
            Assert.Null(ObjectRefsProbe(engine, "column:Currency/Raised Column"));
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Undoing_measure_role_and_perspective_adds_after_a_level_raise_removes_each()
        {
            using var engine = await RaisedFixtureAsync();

            await engine.CreateMeasureAsync("table:Currency", "Raised Measure", "1", "agent");
            await engine.UndoAsync("human");
            Assert.Null(ObjectRefsProbe(engine, "measure:Currency/Raised Measure"));

            await engine.CreateRoleAsync("Raised Role", "Read", "agent");
            await engine.UndoAsync("human");
            Assert.DoesNotContain(await engine.ListRolesAsync(), r => r.Name == "Raised Role");

            await engine.CreatePerspectiveAsync("Raised Perspective", "agent");
            await engine.UndoAsync("human");
            Assert.DoesNotContain(await engine.GetPerspectivesAsync(), p => p.Name == "Raised Perspective");
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Deleting_a_table_and_a_column_that_predate_the_raise_works_and_undoes()
        {
            using var engine = await RaisedFixtureAsync();

            await engine.DeleteObjectAsync("column:Currency/Currency Code", "agent");
            Assert.Null(ObjectRefsProbe(engine, "column:Currency/Currency Code"));
            await engine.UndoAsync("human");
            Assert.NotNull(ObjectRefsProbe(engine, "column:Currency/Currency Code"));

            await engine.DeleteObjectAsync("table:Currency", "agent");
            Assert.False(await HasTableAsync(engine, "Currency"));
            await engine.UndoAsync("human");
            Assert.True(await HasTableAsync(engine, "Currency"));
        }

        [Fact]
        public async Task Object_level_security_on_roles_and_columns_that_predate_the_raise_can_be_set_and_undone()
        {
            using var engine = await RaisedFixtureAsync();

            var t = await engine.SetTableObjectPermissionAsync("Users", "table:Currency", "None", "agent");
            Assert.True(t.Changed);
            var c = await engine.SetColumnObjectPermissionAsync("Users", "column:Customer/Customer Id", "None", "agent");
            Assert.True(c.Changed);
            Assert.Contains((await engine.ListRolesAsync()).Single(r => r.Name == "Users").ObjectPermissions, p => p.Table == "Currency");

            await engine.UndoAsync("human");
            await engine.UndoAsync("human");
            Assert.Empty((await engine.ListRolesAsync()).Single(r => r.Name == "Users").ObjectPermissions);
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_table_add_undoes_after_the_raise_is_undone_and_redone()
        {
            using var engine = await RaisedFixtureAsync();
            await engine.UndoAsync("human");   // back to 1200
            await engine.RedoAsync("human");   // up to 1701 again

            await engine.CreateTableAsync("Redone Raise Table", "agent");
            await engine.UndoAsync("human");
            Assert.False(await HasTableAsync(engine, "Redone Raise Table"));
        }

        [Fact]
        public void A_batch_that_raises_the_level_adds_a_table_and_rolls_back_leaves_the_model_as_it_was()
        {
            // The rollback removes the new table while the level is still raised, so it runs the same gated delete
            // path as an undo. Driven on the handler directly because no engine op fails on purpose after a raise.
            var h = new TabularEditor.TOMWrapper.TabularModelHandler(TestModels.FindBim());
            TabularEditor.TOMWrapper.Utils.CompatibilityGatedMembers.Track(h);
            var tables = h.Model.Tables.Count;

            h.BeginUpdate("raise then fail");
            h.Model.Database.CompatibilityLevel = 1701;
            h.Model.AddCalculatedTable("Rolled Back Table", "{1}");
            h.EndUpdate(undoable: true, rollback: true);

            Assert.Equal(1200, h.CompatibilityLevel);
            Assert.Equal(tables, h.Model.Tables.Count);
        }

        private static object ObjectRefsProbe(LocalEngine engine, string objRef)
        {
            try { return engine.GetObjectAsync(objRef).GetAwaiter().GetResult(); }
            catch { return null; }
        }
    }
}
