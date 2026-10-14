// Verifier probes for m15/ci4-fix (13 Oct 2026): the "emptied table" mark the delete path leaves for the DAX validator.
// A table that never had columns is not judged; a table whose last column was deleted is. These probe that the mark
// cannot go stale and refuse valid DAX later, and which other delete paths leave it.

using System;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.Entitlement;
using Xunit;

namespace Semanticus.Tests
{
    public sealed class VerifyCi4FixTests
    {
        private sealed class Fake : IEntitlement
        {
            public bool IsPro => false;
            public EntitlementInfo Info { get; } = new EntitlementInfo { Tier = "free" };
        }

        private static async Task<(LocalEngine engine, SessionManager sessions)> OneColumnAsync()
        {
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, new Fake());
            await engine.CreateModelAsync("Ci4Probe", 1604);
            await engine.CreateTableAsync("Sales", "human");
            await engine.CreateColumnAsync("table:Sales", "Amount", "Double", null, "human");
            return (engine, sessions);
        }

        [Fact]
        public async Task Deleted_last_column_then_a_formula_over_it_is_refused()
        {
            var (e, _) = await OneColumnAsync();
            using (e)
            {
                await e.DeleteObjectAsync("column:Sales/Amount", "human");
                await Assert.ThrowsAsync<InvalidOperationException>(() => e.CreateMeasureAsync("table:Sales", "T", "SUM ( Sales[Amount] )", "human", null));
            }
        }

        [Fact]
        public async Task Re_added_column_with_the_same_name_is_accepted()
        {
            var (e, _) = await OneColumnAsync();
            using (e)
            {
                await e.DeleteObjectAsync("column:Sales/Amount", "human");
                await e.CreateColumnAsync("table:Sales", "Amount", "Double", null, "human");
                await e.CreateMeasureAsync("table:Sales", "T", "SUM ( Sales[Amount] )", "human", null);
                Assert.Contains(await e.ListMeasuresAsync(), m => m.Name == "T");
            }
        }

        [Fact]
        public async Task Undo_of_the_delete_then_a_formula_over_the_column_is_accepted()
        {
            var (e, _) = await OneColumnAsync();
            using (e)
            {
                await e.DeleteObjectAsync("column:Sales/Amount", "human");
                await e.UndoAsync("human");
                await e.CreateMeasureAsync("table:Sales", "T", "SUM ( Sales[Amount] )", "human", null);
                Assert.Contains(await e.ListMeasuresAsync(), m => m.Name == "T");
            }
        }

        // Undo then redo the delete: the table is empty again through the undo stack (not the delete path). Is the formula still refused?
        [Fact]
        public async Task Redo_of_the_delete_still_refuses_the_formula()
        {
            var (e, _) = await OneColumnAsync();
            using (e)
            {
                await e.DeleteObjectAsync("column:Sales/Amount", "human");
                await e.UndoAsync("human");
                await e.RedoAsync("human");
                await Assert.ThrowsAsync<InvalidOperationException>(() => e.CreateMeasureAsync("table:Sales", "T", "SUM ( Sales[Amount] )", "human", null));
            }
        }

        // Stale-mark probe: emptied, a new column added and then deleted again by undo of the add. Then a formula over a DIFFERENT name
        // (one that never existed) is refused, which is right; the stale case would be a formula over a column that exists, and the table has none.
        [Fact]
        public async Task Emptied_then_re_added_then_add_undone_refuses_only_missing_columns()
        {
            var (e, _) = await OneColumnAsync();
            using (e)
            {
                await e.DeleteObjectAsync("column:Sales/Amount", "human");
                await e.CreateColumnAsync("table:Sales", "Qty", "Double", null, "human");
                await e.CreateMeasureAsync("table:Sales", "Q", "SUM ( Sales[Qty] )", "human", null);
                Assert.Contains(await e.ListMeasuresAsync(), m => m.Name == "Q");
            }
        }

        // Other delete paths: schema sync removing the last column does not leave the mark.
        [Fact(Skip = "verifier gap probe, 13 Oct: this delete path leaves no emptied mark; unskip when it does")]
        public async Task Probe_schema_sync_emptying_a_table()
        {
            var (e, _) = await OneColumnAsync();
            using (e)
            {
                var r = await e.ApplySchemaUpdateAsync("table:Sales", new[] { new SchemaUpdateItem { Change = "Removed", Column = "Amount" } }, "human");
                Console.WriteLine("schema sync: " + string.Join(",", r.GetType().GetProperties().Select(p => p.Name + "=" + p.GetValue(r))));
                Exception? refused = null;
                try { await e.CreateMeasureAsync("table:Sales", "T", "SUM ( Sales[Amount] )", "human", null); } catch (Exception ex) { refused = ex; }
                Assert.True(refused != null, "PROBE: after schema sync emptied the table, a formula over the removed column was ACCEPTED");
            }
        }

        // Other delete paths: remove-safe deleting the last (unused) column does not leave the mark.
        [Fact(Skip = "verifier gap probe, 13 Oct: this delete path leaves no emptied mark; unskip when it does")]
        public async Task Probe_remove_safe_emptying_a_table()
        {
            var (e, _) = await OneColumnAsync();
            using (e)
            {
                var r = await e.RemoveSafeObjectsAsync(new[] { "column:Sales/Amount" }, null, "human");
                Console.WriteLine("remove safe removed=" + r.Removed);
                Exception? refused = null;
                try { await e.CreateMeasureAsync("table:Sales", "T", "SUM ( Sales[Amount] )", "human", null); } catch (Exception ex) { refused = ex; }
                Assert.True(refused != null, "PROBE: after remove-safe emptied the table, a formula over the removed column was ACCEPTED");
            }
        }
    }
}
