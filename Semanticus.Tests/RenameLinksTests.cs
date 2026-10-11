using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Analysis;
using Semanticus.Engine;
using TabularEditor.TOMWrapper;
using TabularEditor.TOMWrapper.Utils;
using Xunit;
using Xunit.Abstractions;

namespace Semanticus.Tests
{
    /// <summary>
    /// Plan statement check 4, fix round 2, R2 and R4. A rename rewrites only the formulas the wrapper's dependency links list, so every path that reloads the model's objects
    /// (apply_tmdl, its undo and redo, a calendar change) must leave the links to be rebuilt before the next rename. Fix round 3: every rename rebuilds the links first (a rename changes
    /// which names resolve, and no comparison of formula texts sees that), so the tests prove every formula stays whole and log what a batch of 200 renames costs.
    /// </summary>
    public sealed class RenameLinksTests
    {
        private readonly ITestOutputHelper _out;
        public RenameLinksTests(ITestOutputHelper output) => _out = output;

        private static async Task<(LocalEngine Engine, Session S)> ModelAsync(Action<Model> build)
        {
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, TestEntitlements.Free);
            await engine.CreateModelAsync("RenameLinks", 1604);
            var s = sessions.Require();
            if (build != null) await s.MutateAsync("human", "build", build);
            return (engine, s);
        }

        private static void Sales(Model m)
        {
            var t = m.AddTable("Sales");
            t.AddDataColumn("Amount", "Amount", null, DataType.Decimal);
            t.AddDataColumn("Region", "Region", null, DataType.String);
            t.AddMeasure("Total Amount", "SUM ( 'Sales'[Amount] )");
        }

        private static string[] Broken(Model m) => FormulaReferences.Scan(m).Select(b => b.Object.Name + ": " + FormulaReferences.Describe(b)).ToArray();

        [Fact]
        public async Task A_measure_that_apply_tmdl_adds_to_a_table_follows_a_later_rename()
        {
            var (engine, s) = await ModelAsync(Sales);
            using (engine)
            {
                var tmdl = await engine.ScriptObjectsAsync(new[] { "table:Sales" }, "tmdl");
                var added = tmdl.TrimEnd() + "\n\n\tmeasure Added = SUM ( 'Sales'[Region] ) + [Total Amount]\n";
                var applied = await engine.ApplyTmdlScriptAsync(added, "agent");
                Assert.True(applied.Applied.Length > 0, string.Join("; ", applied.Notes ?? Array.Empty<string>()) + " " + string.Join("; ", applied.Skipped ?? Array.Empty<string>()) + "\n" + added);
                Assert.Equal("SUM ( 'Sales'[Region] ) + [Total Amount]", await s.ReadAsync(m => m.Tables["Sales"].Measures["Added"].Expression.Trim()));
                await engine.RenameObjectAsync("column:Sales/Region", "Area", "agent");
                await engine.RenameObjectAsync("measure:Sales/Total Amount", "Amount Total", "agent");
                Assert.Equal("SUM ( 'Sales'[Area] ) + [Amount Total]", await s.ReadAsync(m => m.Tables["Sales"].Measures["Added"].Expression.Trim()));
                Assert.Empty(await s.ReadAsync(Broken));
            }
        }

        [Fact]
        public async Task A_formula_that_apply_tmdl_wrote_follows_a_rename_after_its_undo_and_redo()
        {
            var (engine, s) = await ModelAsync(m => { Sales(m); m.Tables["Sales"].AddMeasure("Scripted", "1"); });
            using (engine)
            {
                var tmdl = await engine.ScriptObjectsAsync(new[] { "measure:Sales/Scripted" }, "tmdl");
                await engine.ApplyTmdlScriptAsync(tmdl.Replace("measure Scripted = 1", "measure Scripted = SUM ( 'Sales'[Region] ) + [Total Amount]"), "agent");
                await engine.UndoAsync("human");
                Assert.Equal("1", await s.ReadAsync(m => m.Tables["Sales"].Measures["Scripted"].Expression.Trim()));
                await engine.RedoAsync("human");
                await engine.RenameObjectAsync("column:Sales/Region", "Area", "agent");
                await engine.RenameObjectAsync("measure:Sales/Total Amount", "Amount Total", "agent");
                Assert.Equal("SUM ( 'Sales'[Area] ) + [Amount Total]", await s.ReadAsync(m => m.Tables["Sales"].Measures["Scripted"].Expression.Trim()));
                Assert.Empty(await s.ReadAsync(Broken));
            }
        }

        [Fact]
        public async Task An_undone_apply_tmdl_does_not_leave_a_rename_pointing_at_the_formula_it_took_back()
        {
            var (engine, s) = await ModelAsync(m => { Sales(m); m.Tables["Sales"].AddMeasure("Scripted", "SUM ( 'Sales'[Region] )"); });
            using (engine)
            {
                var tmdl = await engine.ScriptObjectsAsync(new[] { "measure:Sales/Scripted" }, "tmdl");
                await engine.ApplyTmdlScriptAsync(tmdl.Replace("measure Scripted = SUM ( 'Sales'[Region] )", "measure Scripted = 1"), "agent");
                await engine.UndoAsync("human");
                await engine.RenameObjectAsync("column:Sales/Region", "Area", "agent");
                Assert.Equal("SUM ( 'Sales'[Area] )", await s.ReadAsync(m => m.Tables["Sales"].Measures["Scripted"].Expression.Trim()));
                Assert.Empty(await s.ReadAsync(Broken));
            }
        }

        // ---------------------------------------------------------------- R4: the cost of a batch of renames

        // Lean suite (8 Oct): a rename rebuilds the links first, so the cost is renames x formulas. The default run proves "every formula
        // stays whole" on 5 tables (100 formulas, 40 renames); the 25 table form (500 formulas, 200 renames) is the sweep.
        private const int PerTable = 20, ColumnsRenamedPerTable = 8;
        private const int SampleTables = 5, FullTables = 25;

        private static Action<Model> Formulas(int tables) => m =>
        {
            for (var t = 0; t < tables; t++)
            {
                var table = m.AddTable("T" + t);
                for (var c = 0; c < PerTable; c++) table.AddDataColumn("C" + c, "C" + c, null, DataType.Decimal);
                for (var k = 0; k < PerTable; k++)
                    table.AddMeasure("M" + t + "_" + k, "SUM ( 'T" + t + "'[C" + k + "] )" + (k > 0 ? " + [M" + t + "_" + (k - 1) + "]" : ""));
            }
        };

        private async Task BatchOfRenamesKeepsEveryFormulaWholeAsync(int tables)
        {
            var (engine, s) = await ModelAsync(Formulas(tables));
            using (engine)
            {
                Assert.Equal(tables * PerTable, await s.ReadAsync(m => FormulaReferences.CountFormulas(m)));
                var sw = Stopwatch.StartNew();
                await s.MutateAsync("human", tables * ColumnsRenamedPerTable + " renames", m =>
                {
                    for (var t = 0; t < tables; t++)
                        for (var c = 0; c < ColumnsRenamedPerTable; c++)
                            s.Rename(m.Tables["T" + t].Columns["C" + c], "Col " + c);
                });
                sw.Stop();
                _out.WriteLine($"R4 batch of {tables * ColumnsRenamedPerTable} renames, {tables * PerTable} formulas: {sw.ElapsedMilliseconds} ms");
                Assert.Empty(await s.ReadAsync(Broken));
                var last = tables - 1;
                Assert.Equal("SUM ( 'T" + last + "'[C19] ) + [M" + last + "_18]", await s.ReadAsync(m => m.Tables["T" + last].Measures["M" + last + "_19"].Expression.Trim()));
                Assert.Equal("SUM ( 'T3'[Col 5] ) + [M3_4]", await s.ReadAsync(m => m.Tables["T3"].Measures["M3_5"].Expression.Trim()));
            }
        }

        private async Task RenamesOneCallAtATimeAreEachWholeAsync(int tables)
        {
            var (engine, s) = await ModelAsync(Formulas(tables));
            using (engine)
            {
                var sw = Stopwatch.StartNew();
                for (var t = 0; t < tables; t++)
                    for (var c = 0; c < ColumnsRenamedPerTable; c++)
                        await engine.RenameObjectAsync("column:T" + t + "/C" + c, "Col " + c, "agent");
                sw.Stop();
                _out.WriteLine($"R4 {tables * ColumnsRenamedPerTable} renames one call each, {tables * PerTable} formulas: {sw.ElapsedMilliseconds} ms");
                Assert.Empty(await s.ReadAsync(Broken));
                Assert.Equal("SUM ( 'T3'[Col 5] ) + [M3_4]", await s.ReadAsync(m => m.Tables["T3"].Measures["M3_5"].Expression.Trim()));
            }
        }

        [Fact]
        public Task A_batch_of_40_renames_on_100_formulas_keeps_every_formula_whole_sample() => BatchOfRenamesKeepsEveryFormulaWholeAsync(SampleTables);

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public Task The_same_40_renames_made_one_call_at_a_time_are_each_whole_sample() => RenamesOneCallAtATimeAreEachWholeAsync(SampleTables);

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public Task A_batch_of_200_renames_on_500_formulas_keeps_every_formula_whole() => BatchOfRenamesKeepsEveryFormulaWholeAsync(FullTables);

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public Task The_same_200_renames_made_one_call_at_a_time_are_each_whole() => RenamesOneCallAtATimeAreEachWholeAsync(FullTables);
    }
}
