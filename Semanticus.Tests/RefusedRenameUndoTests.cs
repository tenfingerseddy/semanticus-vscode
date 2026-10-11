using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// UAT-S07b-1-007 and 008 (Sol UAT, 25 Sep): four Sales key columns became hidden with no History edit, and
    /// set_column_hidden(false) then said changed while reads still showed them hidden.
    ///
    /// The cause was one refused rename. TE2 opens a batch of its own when a table is renamed (Table.OnPropertyChanging,
    /// for FormulaFixup) and closes it in OnPropertyChanged. TOM refuses "Measures" (a reserved name) between the two,
    /// so that inner batch stayed open and the one EndUpdate on the refusal path closed only it. The mutation's own
    /// batch stayed open, so the next Undo drained the whole stack, threw "Stack empty." halfway and left TE2's undo
    /// stuck "in progress". From then on TE2 recorded nothing and a rollback did nothing, so every plan rehearsal of
    /// the safe fixes (hide the key columns) landed for real, with no revision and no History row.
    /// </summary>
    public sealed class RefusedRenameUndoTests : IClassFixture<ModelTemplates>
    {
        private readonly ModelTemplates _templates;
        public RefusedRenameUndoTests(ModelTemplates templates) => _templates = templates;

        // The UAT session had just restarted and every earlier edit was undone, so the refused rename's batch was all
        // that stood on the undo stack. Reopening the saved model gives the same short stack.
        // Lean suite: the plan model (what ChangeCauseTests.PlanModelAsync builds) is built once for the class and each test opens a copy of the saved file.
        private async Task<DecisionFixture> FreshAsync(string file)
        {
            var f = new DecisionFixture(TestEntitlements.Pro);
            await _templates.OpenCopyAsync(f, "plan", async b =>
            {
                var e = b.Engine;
                await e.CreateModelAsync("Contoso", 1604);
                await e.CreateTableAsync("Sales", "human");
                await e.CreateColumnAsync("table:Sales", "Amount", "Double", null, "human");
                await e.CreateColumnAsync("table:Sales", "CustomerKey", "Int64", null, "human");
                await e.CreateTableAsync("Customer", "human");
                await e.CreateColumnAsync("table:Customer", "CustomerKey", "Int64", null, "human");
                await e.CreateColumnAsync("table:Customer", "City", "String", null, "human");
                await e.CreateColumnAsync("table:Sales", "ProductKey", "Int64", null, "human");
                await e.CreateTableAsync("Product", "human");
                await e.CreateColumnAsync("table:Product", "ProductKey", "Int64", null, "human");
                await e.CreateRelationshipAsync("column:Sales/CustomerKey", "column:Customer/CustomerKey", null, true, "human");
                await e.CreateRelationshipAsync("column:Sales/ProductKey", "column:Product/ProductKey", null, true, "human");
                await e.SetColumnMetadataAsync("column:Sales/ProductKey", null, "Sum", null, null, "human");
                await e.CreateMeasureAsync("table:Sales", "Total Sales", "SUM ( Sales[Amount] )", "human");
            }, file);
            f.UseNull();
            return f;
        }

        private static async Task<bool> HiddenAsync(LocalEngine engine, string col) =>
            (bool)(await engine.GetObjectAsync(col)).Properties["isHidden"];

        [Fact]
        public async Task A_refused_rename_leaves_the_undo_timeline_whole_so_Undo_to_here_takes_back_the_edit_before_it()
        {
            using var f = await FreshAsync("RefusedRename.bim");
            var scratch = await f.Engine.CreateTableAsync("Scratch", "human");
            var created = f.Sessions.Ledger.Entries(f.Sessions.Current.Id).Last().EntryId;

            await Assert.ThrowsAnyAsync<Exception>(() => f.Engine.RenameObjectAsync(scratch, "Measures", "agent"));

            // Undo to here threw "Stack empty." and took the table back with no revision and no broadcast.
            var rev = f.Sessions.Current.Revision;
            await f.Engine.UndoToEntryAsync(created, "human");
            Assert.Equal(rev + 1, f.Sessions.Current.Revision);
            await Assert.ThrowsAnyAsync<Exception>(() => f.Engine.GetObjectAsync(scratch));

            // An edit after it is on the undo stack as its own step again.
            await f.Engine.SetDescriptionAsync("measure:Sales/Total Sales", "after the refusal", "human");
            var later = f.Sessions.Ledger.Entries(f.Sessions.Current.Id).Last().EntryId;
            Assert.True((await f.Engine.GetPlanHistoryAsync()).Rows.First(r => r.Id == later).Undoable,
                "the edit after a refused rename made no undo step");
        }

        [Fact]
        public async Task After_a_refused_rename_and_an_undo_the_plan_never_hides_a_key_on_its_own_and_unhiding_sticks()
        {
            using var f = await FreshAsync("RefusedRenameKeys.bim");
            Assert.Contains((await f.Engine.GetModelPlanAsync()).Items, i => i.Id == "safefix/Visibility");
            var scratch = await f.Engine.CreateTableAsync("Scratch", "human");
            var created = f.Sessions.Ledger.Entries(f.Sessions.Current.Id).Last().EntryId;
            await Assert.ThrowsAnyAsync<Exception>(() => f.Engine.RenameObjectAsync(scratch, "Measures", "agent"));
            // The person carries on past whatever Undo to here says, as in the UAT; the test above pins the undo itself.
            await Record.ExceptionAsync(() => f.Engine.UndoToEntryAsync(created, "human"));

            // The plan rehearses its safe fixes to draw itself; a rehearsal must leave the model as it was.
            await f.Engine.GetModelPlanAsync();
            Assert.False(await HiddenAsync(f.Engine, "column:Sales/CustomerKey"), "the plan hid a key column nobody asked to hide");

            // 008: a real unhide is still in force after the plan draws itself again.
            await f.Engine.SetColumnMetadataAsync("column:Sales/CustomerKey", true, null, null, null, "human");
            var r = await f.Engine.SetColumnMetadataAsync("column:Sales/CustomerKey", false, null, null, null, "agent");
            Assert.True(r.Changed);
            await f.Engine.GetModelPlanAsync();
            Assert.False(await HiddenAsync(f.Engine, "column:Sales/CustomerKey"), "set_column_hidden(false) said changed but the column is hidden");
        }

        [Fact]
        public async Task A_rehearsed_rename_that_TOM_refuses_also_leaves_the_undo_timeline_whole()
        {
            using var f = await FreshAsync("RefusedRenameDry.bim");
            var scratch = await f.Engine.CreateTableAsync("Scratch", "human");
            var created = f.Sessions.Ledger.Entries(f.Sessions.Current.Id).Last().EntryId;

            var rpt = await f.Engine.DryRunOpAsync("rename_object", "{\"objRef\":\"" + scratch + "\",\"newName\":\"Measures\"}");
            Assert.False(rpt.WouldSucceed);

            await f.Engine.UndoToEntryAsync(created, "human");
            await Assert.ThrowsAnyAsync<Exception>(() => f.Engine.GetObjectAsync(scratch));
            await f.Engine.GetModelPlanAsync();
            Assert.False(await HiddenAsync(f.Engine, "column:Sales/CustomerKey"));
        }
    }
}
