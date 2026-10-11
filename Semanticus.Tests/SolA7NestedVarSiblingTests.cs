using System.Threading.Tasks;
using Semanticus.Engine;
using TabularEditor.TOMWrapper;
using Xunit;

namespace Semanticus.Tests
{
    public sealed class SolA7NestedVarSiblingTests
    {
        [Fact]
        public async Task A_sibling_declaration_reads_the_outer_table_after_an_inner_var_block_ends()
        {
            const string before = "VAR t = Sales VAR CountOther = VAR t = Other RETURN COUNTROWS ( t ) VAR SalesTotal = SUMX ( t, [P] ) RETURN CountOther + SalesTotal";
            await AssertUnrelatedRenamePreservesFormula(before);
        }

        [Fact]
        public async Task An_outer_return_ends_all_completed_inner_return_blocks()
        {
            const string before = "VAR t = Sales VAR CountOther = VAR t = Other RETURN VAR Extra = 1 RETURN COUNTROWS ( t ) + Extra RETURN CountOther + SUMX ( t, [P] )";
            await AssertUnrelatedRenamePreservesFormula(before);
        }

        private static async Task AssertUnrelatedRenamePreservesFormula(string before)
        {
            var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, TestEntitlements.Pro);
            await engine.CreateModelAsync("NestedVarSibling", 1601);
            await sessions.Require().MutateAsync("human", "seed", m =>
            {
                m.AddTable("Sales").AddDataColumn("P", "P", null, DataType.Decimal);
                var other = m.AddTable("Other");
                other.AddDataColumn("P", "P", null, DataType.Decimal);
                var metrics = m.AddTable("Metrics");
                metrics.AddMeasure("New P", "900");
                metrics.AddMeasure("Host", before);
            });

            await engine.RenameObjectAsync("column:Other/P", "New P", "human");

            var actual = await sessions.Require().ReadAsync(m => m.Tables["Metrics"].Measures["Host"].Expression);
            Assert.Equal(before, actual);
        }
    }
}
