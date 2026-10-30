using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.MeasureContexts;
using Xunit;

namespace Semanticus.Tests;

// v-rc24: a recheck of the release polish (fx17 and fx18 on rc23). A true statement of a clear passes; a negation
// or a Description where the name is not the cleared filter does not pass a mistaken clear; Equivalence shows the
// numbers in the measure's own format.
[Collection("restore-root")]
public sealed class CheckedVRc24Tests
{
    private static MeasureMark Judge(string description, params ContextColumn[] columns)
    {
        if (columns.Length == 0) columns = new[] { new ContextColumn("Product", "Category") };
        var probe = new ProbeResult { Status = "ok", Scenarios = columns.Select(c => new ScenarioEvidence {
            Name = "by " + c.Dax, Status = "ok", Rows = new[] {
                new ProbeRow { V = 300, Members = new() { [c.Dax] = "Contoso first" } },
                new ProbeRow { V = 300, Members = new() { [c.Dax] = "Contoso second" } }
            } }).ToArray() };
        var clears = columns.Select(c => new ContextFilterClear(c, "ALL")).ToArray();
        return MeasureMarkJudge.FilterIntent(new() { Name = "Filters", State = "passed" }, probe, clears, false);
    }

    // 1.7 item 2 (L2): true statements, Kane's four Contoso Descriptions that state a clear, and comma lists no longer
    // pass on words. Each reads information with "Fix the formula" and "It is on purpose".
    [Theory]
    [InlineData("Quantity ignoring Date and Product filters.")]
    [InlineData("Product filters are ignored.")]
    [InlineData("Removes the Product filter.")]
    [InlineData("Share of sales across all products (ignores product filters).")]
    [InlineData("Customers whose first order ever falls in the visible period (Absolute scope, ignores Product). daxpatterns.com.")]
    public void A_true_statement_of_the_clear_does_not_pass(string description)
    {
        var mark = Judge(description);
        Assert.True(mark.State == "info" && mark.ReasonCode == "filters_lost", $"Filters is {mark.State}: {mark.Sentence}");
    }

    [Theory]
    [InlineData("Average all-time sales per customer (ignores the date filter).", false)]
    [InlineData("Absolute scope: each customer's first order date ever, ignoring Date and Product filters. Basis of New/Returning.", true)]
    public void Kane_Contoso_date_clears_need_the_persons_word(string description, bool product)
    {
        var columns = product ? new[] { new ContextColumn("Date", "Year"), new ContextColumn("Product", "Category") }
            : new[] { new ContextColumn("Date", "Year") };
        var mark = Judge(description, columns);
        Assert.True(mark.State == "info" && mark.ReasonCode == "filters_lost", $"Filters is {mark.State}: {mark.Sentence}");
    }

    [Theory]
    [InlineData("Ignores Date, Product and Customer filters.")]
    [InlineData("Ignores the Date, Product, and Customer filters on purpose.")]
    [InlineData("Date, Product and Customer filters are removed.")]
    public void A_comma_list_of_cleared_names_does_not_pass(string description)
    {
        var mark = Judge(description, new ContextColumn("Date", "Year"), new ContextColumn("Product", "Category"),
            new ContextColumn("Customer", "City"));
        Assert.True(mark.State == "info" && mark.ReasonCode == "filters_lost", $"Filters is {mark.State}: {mark.Sentence}");
    }

    // Works: the negations the rule knows, and the v-rc23g loose wordings.
    [Theory]
    [InlineData("It does not ignore Product filters.")]
    [InlineData("It doesn't ignore Product filters.")]
    [InlineData("Product filters are never ignored.")]
    [InlineData("not filtered PRODUCT filters")]
    [InlineData("Ignores returns for each product.")]
    [InlineData("Respects the Product filter and ignores returns.")]
    public void A_known_negation_or_loose_wording_does_not_pass(string description)
    {
        var mark = Judge(description);
        Assert.True(mark.State != "passed", $"Filters is {mark.State}: {mark.Sentence}");
    }

    // Defect: other plain negations are not known, so a Description that says the filter is KEPT passes a mistaken
    // ALL ( 'Product' ) as "Passed ... on purpose". The same list was in place at fd0d0474 (not new in this diff).
    [Theory]
    [InlineData("It can't ignore Product filters.")]
    [InlineData("It cannot ignore Product filters.")]
    [InlineData("It won't ignore Product filters.")]
    [InlineData("It isn't ignoring Product filters.")]
    [InlineData("It shouldn't ignore Product filters.")]
    [InlineData("Don't ignore Product filters.")]
    [InlineData("Total sales without ignoring Product filters.")]
    public void A_negation_never_passes_a_clear(string description)
    {
        var mark = Judge(description);
        Assert.True(mark.State != "passed", $"Filters is {mark.State}: {mark.Sentence}");
        Assert.DoesNotContain("on purpose", mark.Sentence);
    }

    // Defect: "with", "in" and "for" after the name count as the end of the object, so "ignores the rows of Products
    // that have zero sales" reads as a stated clear of the Products table. Table names are often plural.
    [Theory]
    [InlineData("Average sales per product. Ignores Products with zero sales.")]
    [InlineData("Ignores Product with zero sales.")]
    public void A_row_condition_on_the_name_does_not_pass_a_clear(string description)
    {
        var mark = Judge(description, new ContextColumn(description.Contains("Products") ? "Products" : "Product", "Category"));
        Assert.True(mark.State != "passed", $"Filters is {mark.State}: {mark.Sentence}");
        Assert.DoesNotContain("on purpose", mark.Sentence);
    }

    // Defect: a measure with no format string that divides (here by a tax rate) is shown as a percent in Equivalence,
    // so 909.09 reads "90,909.0909%". The measure has no percent format; Power BI shows it as a plain number.
    [Fact]
    public async Task Equivalence_shows_an_unformatted_division_as_a_plain_number()
    {
        using var root = new DecisionFixture();
        using var f = new MeasureMarksDoorTests.Fixture(root.Workspace);
        await f.Open();
        await f.Sessions.Current.MutateAsync("human", "Contoso sales ex tax", m => {
            var measure = m.Tables["Sales"].Measures["Total Sales"];
            measure.Expression = "SUM('Sales'[Amount]) / 1.1";
            measure.FormatString = "";
        });
        await MeasureMarksTests.Finish(f.Engine, "measure:Sales/Total Sales");
        await f.Sessions.Current.MutateAsync("human", "Contoso sales ex tax, doubled", m =>
            m.Tables["Sales"].Measures["Total Sales"].Expression = "SUM('Sales'[Amount]) * 2 / 1.1");
        var result = await MeasureMarksTests.Finish(f.Engine, "measure:Sales/Total Sales");
        var mark = Assert.Single(result.Marks, m => m.Name == "Equivalence");
        Assert.DoesNotContain("%", mark.BeforeFormatString ?? "");
        Assert.DoesNotContain("%", mark.AfterFormatString ?? "");
    }
}
