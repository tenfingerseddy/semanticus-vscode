using System;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests;

// v-rc23c: Kane's Contoso Sales YTD is IF ( [ShowValueForDates], CALCULATE ( [Sales Amount], DATESYTD ( 'Date'[Date] ) ) ).
// Before fx8 its Filters mark passed. After fx8 it reads "The filter check could not compare the source rows."
[Collection("restore-root")]
public sealed class CheckedVRc23cTests
{
    [Theory]
    [InlineData("IF ( [ShowValueForDates], CALCULATE ( [Sales Amount], DATESYTD ( 'Date'[Date] ) ) )")]
    [InlineData("IF ( [ShowValueForDates], CALCULATE ( [Sales Amount], DATEADD ( 'Date'[Date], -1, YEAR ) ) )")]
    public async Task A_shown_dates_guard_keeps_the_filter_row_witness(string formula)
    {
        using var f = new DecisionFixture();
        await f.Engine.CreateModelAsync("Contoso rows", 1604);
        await f.Sessions.Current.MutateAsync("human", "Contoso YTD fixture", m => {
            var sales = m.AddTable("Sales");
            sales.AddDataColumn("Quantity", "Quantity", dataType: TabularEditor.TOMWrapper.DataType.Int64);
            sales.AddDataColumn("Order Date", "Order Date", dataType: TabularEditor.TOMWrapper.DataType.DateTime);
            m.AddTable("Date").AddDataColumn("Date", "Date", dataType: TabularEditor.TOMWrapper.DataType.DateTime);
            sales.AddMeasure("Sales Amount", "SUM ( Sales[Quantity] )");
            sales.AddMeasure("ShowValueForDates", "VAR LastDateWithData = CALCULATE ( MAX ( Sales[Order Date] ), REMOVEFILTERS () )\n"
                + "VAR FirstDateVisible = MIN ( 'Date'[Date] )\nRETURN FirstDateVisible <= LastDateWithData");
            sales.AddMeasure("Sales YTD", formula);
        });
        var snapshot = await f.Sessions.Current.ReadAsync(MeasureMarkSnapshot.Capture);
        // The pre-fx8 witness (FactExpression) exists for this measure; the fx8 row witness must exist too, or Filters cannot run.
        Assert.NotNull(snapshot.FactExpression("measure:Sales/Sales YTD"));
        Assert.NotNull(snapshot.FilterRowsExpression("measure:Sales/Sales YTD", preserveFilterChanges: false));
        Assert.NotNull(snapshot.FilterRowsExpression("measure:Sales/Sales YTD"));
    }

    [Fact]
    public async Task A_guarded_measure_that_clears_a_slicer_reads_information_until_the_person_says_on_purpose()
    {
        using var root = new DecisionFixture();
        using var f = new MeasureMarksDoorTests.Fixture(root.Workspace);
        await f.Open();
        // FX13 said the Description states that the clear is intended. 1.7 item 2 (L2): words never pass it.
        await f.Sessions.Current.MutateAsync("human", "Guard Contoso measure", m => {
            var measure = m.Tables["Sales"].Measures["Total Sales"];
            measure.Expression = "IF ( 1 = 1, CALCULATE ( SUM ( 'Sales'[Amount] ), ALL('Product') ) )";
            measure.Description = "Ignores Product filters on purpose.";
        });
        var result = await MeasureMarksTests.Finish(f.Engine, "measure:Sales/Total Sales");
        var filters = result.Marks.Single(m => m.Name == "Filters");
        Assert.True(filters.State == "info", $"Filters is {filters.State}: {filters.Sentence}");
        Assert.Contains("ignores Product", filters.Sentence);
    }
}
