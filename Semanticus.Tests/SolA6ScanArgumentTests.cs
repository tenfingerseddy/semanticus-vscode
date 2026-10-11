using System.Threading.Tasks;
using Semanticus.Engine;
using TabularEditor.TOMWrapper;
using Xunit;

namespace Semanticus.Tests
{
    public sealed class SolA6ScanArgumentTests
    {
        // The delimiter and percentile are scalar arguments, not the expression evaluated for each row.
        // https://learn.microsoft.com/en-us/dax/concatenatex-function-dax
        // https://learn.microsoft.com/en-us/dax/percentilex-inc-function-dax
        [Theory]
        [InlineData("CONCATENATEX ( Sales, Sales[Label], [Parameter] )")]
        [InlineData("PERCENTILEX.INC ( Sales, Sales[Amount], [Parameter] )")]
        [InlineData("PERCENTILEX.EXC ( Sales, Sales[Amount], [Parameter] )")]
        public async Task Renaming_a_column_does_not_rewrite_an_iterator_scalar_parameter(string formula)
        {
            var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, TestEntitlements.Pro);
            await engine.CreateModelAsync("ScalarArguments", 1601);
            await sessions.Require().MutateAsync("human", "seed", m =>
            {
                var sales = m.AddTable("Sales");
                sales.AddDataColumn("Label", "Label", null, DataType.String);
                sales.AddDataColumn("Amount", "Amount", null, DataType.Decimal);
                sales.AddDataColumn("Parameter", "Parameter", null, DataType.Decimal);
                var metrics = m.AddTable("Metrics");
                metrics.AddMeasure("Parameter", "0.5");
                metrics.AddMeasure("New Parameter", "0.9");
                metrics.AddMeasure("Host", formula);
            });

            await engine.RenameObjectAsync("column:Sales/Parameter", "New Parameter", "human");
            var actual = await sessions.Require().ReadAsync(m => m.Tables["Metrics"].Measures["Host"].Expression);
            Assert.Equal(formula, actual);
        }
    }
}
