using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.Entitlement;
using TabularEditor.TOMWrapper;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// Verifier defect found while checking m11/fixup, and FAILING ON PURPOSE. A measure whose formula apply_tmdl wrote is not
    /// followed by the next rename: renaming the column or the table it reads leaves the formula on the old name, and nothing says
    /// so. It fails on m11/join 908aaff7 as well, with a formula that holds no keyword at all, so the patched fixup did not cause it.
    /// Measured: right after the apply the measure's DependsOn is empty. Likely cause, not proven: TmdlApplier.Apply rebuilds the
    /// wrapper with Model.Reinit() (Semanticus.Core/TmdlApplier.cs:72) and nothing rebuilds the DAX dependency tree for the new
    /// wrapper objects, so the fixup has no reference to rewrite.
    /// </summary>
    public sealed class VerifyFixupTmdlDefectTests
    {
        private sealed class Pro : IEntitlement
        {
            public bool IsPro => true;
            public EntitlementInfo Info { get; } = new EntitlementInfo { Tier = "pro" };
        }

        [Theory]
        [InlineData("SUM('Sales'[Qty]) + [Total]", "column:Sales/Qty", "Quantity", "SUM('Sales'[Quantity]) + [Total]")]
        [InlineData("SUM('Sales'[Qty]) + [Total]", "table:Sales", "Sales Facts", "SUM('Sales Facts'[Qty]) + [Total]")]
        [InlineData("SUM('Sales'[Qty]) + [Total]", "measure:Sales/Total", "Total Sales", "SUM('Sales'[Qty]) + [Total Sales]")]
        [InlineData("VAR a = 1 RETURN [Total] + SUM('Sales'[Qty])", "column:Sales/Qty", "Quantity", "VAR a = 1 RETURN [Total] + SUM('Sales'[Quantity])")]
        public async Task A_rename_right_after_apply_tmdl_rewrites_the_formula_apply_tmdl_wrote(string formula, string objRef, string to, string expected)
        {
            var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, new Pro());
            await engine.CreateModelAsync("VerifyFixupTmdl", 1702);
            var s = sessions.Require();
            await s.MutateAsync("human", "seed", m =>
            {
                var sales = m.AddTable("Sales");
                sales.AddDataColumn("Qty", "Qty", null, DataType.Int64);
                sales.AddMeasure("Total", "SUM('Sales'[Qty])");
                sales.AddMeasure("Host", "1");
            });
            var applied = await engine.ApplyTmdlScriptAsync("// @object measure:Sales/Host\nref table 'Sales'\n\n\tmeasure Host = " + formula + "\n", "human");
            Assert.True(applied.Applied.Length > 0, "apply_tmdl wrote nothing: " + string.Join("; ", applied.Skipped));
            Assert.Equal(formula, await s.ReadAsync(m => m.Tables["Sales"].Measures["Host"].Expression));

            Assert.True((await engine.RenameObjectAsync(objRef, to, "human")).Changed);

            Assert.Equal(expected, await s.ReadAsync(m => m.AllMeasures.Single(x => x.Name == "Host").Expression));
        }
    }
}
