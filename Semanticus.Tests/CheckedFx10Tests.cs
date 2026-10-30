using System;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Dax.Syntax;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests;

[Collection("restore-root")]
public sealed class CheckedFx10Tests
{
    private const string Rows = "COALESCE(COUNTROWS('Sales'), 0)";
    private const string AllRows = "CALCULATE(" + Rows + ", ALL('Product'))";

    private static async Task<MeasureMarkSnapshot> Snapshot(string formula)
    {
        using var f = new DecisionFixture();
        await f.Engine.CreateModelAsync("Contoso rows", 1604);
        await f.Sessions.Current.MutateAsync("human", "Contoso witness fixture", m => {
            var sales = m.AddTable("Sales");
            sales.AddDataColumn("Amount", "Amount", dataType: TabularEditor.TOMWrapper.DataType.Decimal);
            m.AddTable("Budget").AddDataColumn("Amount", "Amount", dataType: TabularEditor.TOMWrapper.DataType.Decimal);
            m.AddTable("Product").AddDataColumn("Category", "Category");
            m.AddTable("Date").AddDataColumn("Date", "Date", dataType: TabularEditor.TOMWrapper.DataType.DateTime);
            sales.AddMeasure("Sales Amount", "SUM(Sales[Amount])");
            sales.AddMeasure("ShowValueForDates", "CALCULATE(MAX('Date'[Date]), REMOVEFILTERS()) > MIN('Date'[Date])");
            sales.AddMeasure("Contoso value", formula);
        });
        return await f.Sessions.Current.ReadAsync(MeasureMarkSnapshot.Capture);
    }

    [Theory]
    [InlineData("IF([ShowValueForDates], CALCULATE([Sales Amount], ALL('Product')))")]
    [InlineData("IF([ShowValueForDates], CALCULATE([Sales Amount], ALL('Product')), BLANK())")]
    [InlineData("IF([ShowValueForDates], BLANK(), CALCULATE([Sales Amount], ALL('Product')))")]
    [InlineData("IF([ShowValueForDates], , CALCULATE([Sales Amount], ALL('Product')))")]
    [InlineData("SWITCH(TRUE(), [ShowValueForDates], CALCULATE([Sales Amount], ALL('Product')), BLANK())")]
    [InlineData("SWITCH(TRUE(), [ShowValueForDates], BLANK(), CALCULATE([Sales Amount], ALL('Product')))")]
    [InlineData("SWITCH(1, 1, CALCULATE([Sales Amount], ALL('Product')))")]
    public async Task Guard_results_keep_the_value_branch_filters(string formula)
    {
        var snapshot = await Snapshot(formula);
        Assert.Equal(AllRows, snapshot.FilterRowsExpression("measure:Sales/Contoso value"));
        Assert.Equal(Rows, snapshot.FilterRowsExpression("measure:Sales/Contoso value", false));
    }

    [Fact]
    public async Task Var_return_keeps_filter_variables_in_scope()
    {
        var snapshot = await Snapshot("VAR Categories = ALL('Product') RETURN CALCULATE([Sales Amount], Categories)");
        var filtered = snapshot.FilterRowsExpression("measure:Sales/Contoso value");
        Assert.NotNull(filtered);
        Assert.Contains("VAR Categories = ALL('Product')", filtered);
        Assert.Contains("RETURN CALCULATE(" + Rows + ", Categories)", filtered);
        Assert.Empty(DaxSyntaxTree.ParseExpression(filtered).GetDiagnostics());
        // A filter variable must also be inspected when building the ordinary source-row witness.
        var source = snapshot.FilterRowsExpression("measure:Sales/Contoso value", false);
        Assert.NotNull(source);
        Assert.Contains("RETURN " + Rows, source);
        Assert.DoesNotContain("RETURN CALCULATE", source);
    }

    [Fact]
    public async Task Var_result_keeps_its_evaluation_before_an_outer_calculate()
    {
        var snapshot = await Snapshot("VAR ContosoAmount = [Sales Amount] RETURN CALCULATE(ContosoAmount, ALL('Product'))");
        var filtered = snapshot.FilterRowsExpression("measure:Sales/Contoso value");
        Assert.NotNull(filtered);
        Assert.Contains("VAR __smx_filter_rows_0 = " + Rows, filtered);
        Assert.Contains("RETURN CALCULATE(__smx_filter_rows_0, ALL('Product'))", filtered);
        Assert.Empty(DaxSyntaxTree.ParseExpression(filtered).GetDiagnostics());
    }

    [Theory]
    [InlineData("DIVIDE(CALCULATE([Sales Amount], ALL('Product')), 100)")]
    [InlineData("DIVIDE(CALCULATE([Sales Amount], ALL('Product')), 100, 0)")]
    [InlineData("DIVIDE(100, CALCULATE([Sales Amount], ALL('Product')))")]
    public async Task Divide_keeps_the_numerator_filter_witness(string formula)
    {
        var snapshot = await Snapshot(formula);
        Assert.Equal(AllRows, snapshot.FilterRowsExpression("measure:Sales/Contoso value"));
        Assert.Equal(Rows, snapshot.FilterRowsExpression("measure:Sales/Contoso value", false));
    }

    [Theory]
    [InlineData("VAR ContosoAmount = CALCULATE([Sales Amount], DATESYTD('Date'[Date])) RETURN ContosoAmount")]
    [InlineData("VAR Scale = 100 RETURN DIVIDE(CALCULATE([Sales Amount], DATESYTD('Date'[Date])), Scale)")]
    [InlineData("IF([ShowValueForDates], DIVIDE(CALCULATE([Sales Amount], DATEADD('Date'[Date], -1, YEAR)), 100))")]
    public async Task Nested_time_wrappers_keep_the_time_filter(string formula)
    {
        var snapshot = await Snapshot(formula);
        foreach (var preserve in new[] { false, true })
        {
            var expression = snapshot.FilterRowsExpression("measure:Sales/Contoso value", preserve);
            Assert.NotNull(expression);
            Assert.Contains(formula.Contains("DATESYTD") ? "DATESYTD('Date'[Date])" : "DATEADD('Date'[Date], -1, YEAR)", expression);
            Assert.Contains(Rows, expression);
            Assert.Empty(DaxSyntaxTree.ParseExpression(expression).GetDiagnostics());
        }
    }

    [Theory]
    [InlineData("IF([ShowValueForDates], CALCULATE([Sales Amount], ALL('Product')), [Sales Amount])")]
    [InlineData("SWITCH(TRUE(), [ShowValueForDates], CALCULATE([Sales Amount], ALL('Product')), [Sales Amount])")]
    [InlineData("IF(1 = 1, CALCULATE(SUM(Budget[Amount]), ALL('Product')), CALCULATE(SUM(Sales[Amount]), ALL('Product')))")]
    [InlineData("SWITCH(TRUE(), 1 = 1, CALCULATE(SUM(Budget[Amount]), ALL('Product')), CALCULATE(SUM(Sales[Amount]), ALL('Product')))")]
    [InlineData("DIVIDE(CALCULATE([Sales Amount], ALL('Product')), [Sales Amount])")]
    [InlineData("DIVIDE(CALCULATE([Sales Amount], ALL('Product')), 0, 0)")]
    [InlineData("DIVIDE(0, CALCULATE([Sales Amount], ALL('Product')), 0)")]
    [InlineData("DIVIDE(CALCULATE([Sales Amount], ALL('Product')), BLANK(), 0)")]
    [InlineData("VAR ContosoRows = ALL(Sales) RETURN SUMX(ContosoRows, Sales[Amount])")]
    [InlineData("CALCULATE([Sales Amount], ALL('Product')) + [Sales Amount]")]
    public async Task Different_filtered_sources_keep_the_honest_unknown(string formula)
    {
        var snapshot = await Snapshot(formula);
        Assert.Null(snapshot.FilterRowsExpression("measure:Sales/Contoso value"));
    }

    [Theory]
    [InlineData("IF(1 = 1, {0}, BLANK())")]
    [InlineData("IF(1 = 0, BLANK(), {0})")]
    [InlineData("SWITCH(TRUE(), 1 = 1, {0}, BLANK())")]
    [InlineData("VAR Scale = 100 RETURN {0}")]
    [InlineData("DIVIDE({0}, 100)")]
    public async Task Production_marks_pass_wrapped_measures_with_and_without_an_explicit_clear(string wrapper)
    {
        using var root = new DecisionFixture();
        using var f = new MeasureMarksDoorTests.Fixture(root.Workspace);
        await f.Open();
        foreach (var clears in new[] { false, true })
        {
            var value = clears ? "CALCULATE(SUM('Sales'[Amount]), ALL('Product'))" : "CALCULATE(SUM('Sales'[Amount]))";
            await f.Sessions.Current.MutateAsync("human", "Contoso wrapped formula", m =>
                m.Tables["Sales"].Measures["Total Sales"].Expression = string.Format(wrapper, value));
            var result = await MeasureMarksTests.Finish(f.Engine, "measure:Sales/Total Sales");
            // FX13: a formula clear needs stated intent to pass. The guarded row witnesses above must still exist.
            Assert.Equal(clears ? "info" : "passed", result.Marks.Single(m => m.Name == "Filters").State);
        }
    }

    [Fact]
    public async Task Production_marks_cannot_compare_different_filtered_branches()
    {
        using var root = new DecisionFixture();
        using var f = new MeasureMarksDoorTests.Fixture(root.Workspace);
        await f.Open();
        await f.Sessions.Current.MutateAsync("human", "Contoso mixed sources", m =>
            m.Tables["Sales"].Measures["Total Sales"].Expression =
                "IF(1 = 1, CALCULATE(SUM('Sales'[Amount]), ALL('Product')), SUM('Sales'[Amount]))");
        var result = await MeasureMarksTests.Finish(f.Engine, "measure:Sales/Total Sales");
        var filters = result.Marks.Single(m => m.Name == "Filters");
        var snapshot = await f.Sessions.Current.ReadAsync(MeasureMarkSnapshot.Capture);
        Assert.Null(snapshot.FilterRowsExpression("measure:Sales/Total Sales"));
        // FX16: intent cannot replace an unavailable row comparison.
        Assert.Equal("not run", filters.State);
        Assert.Contains("different sets of rows", filters.Sentence);
    }
}
