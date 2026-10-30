using System.Linq;
using Semanticus.Engine;
using Semanticus.Engine.MeasureContexts;
using Xunit;

namespace Semanticus.Tests;

// 1.7 item 2 (L2): the strict Description forms no longer pass a cleared filter. Every wording below, once a pass,
// now reads information with "Fix the formula" and "It is on purpose"; only the person's word passes it.
public sealed class CheckedFx19Tests
{
    private static MeasureMark Judge(string description, string table = "Product", string name = "Category")
    {
        var column = new ContextColumn(table, name);
        var probe = new ProbeResult { Status = "ok", Scenarios = new[] {
            new ScenarioEvidence { Name = "by " + column.Dax, Status = "ok", Rows = new[] {
                new ProbeRow { V = 300, Members = new() { [column.Dax] = "First" } },
                new ProbeRow { V = 300, Members = new() { [column.Dax] = "Second" } }
            } }
        } };
        return MeasureMarkJudge.FilterIntent(new() { Name = "Filters", State = "passed" }, probe,
            new[] { new ContextFilterClear(column, "ALL") }, false);
    }

    [Theory]
    [InlineData("Ignore Product filters.")]
    [InlineData("Ignores the Product filter.")]
    [InlineData("Ignoring any Product filters.")]
    [InlineData("Removes all Product filters.")]
    [InlineData("Removing Product filters.")]
    [InlineData("Product filter is ignored.")]
    [InlineData("Product filters are removed.")]
    [InlineData("Regardless of Product.")]
    [InlineData("Regardless of the Product, totals repeat.")]
    [InlineData("Regardless of Product; totals repeat.")]
    [InlineData("IgNoReS PRODUCT filters.")]
    [InlineData("Ignores Product Category filters.")]
    [InlineData("Ignores Category filters.")]
    [InlineData("Ignores Date, Product and Customer filters.")]
    [InlineData("Date, Product, and Customer filters are ignored.")]
    [InlineData("Regardless of Date, Product and Customer.")]
    [InlineData("Absolute scope: each customer's first order date ever, ignoring Date and Product filters.")]
    [InlineData("No returns. Ignores Product filters.")]
    public void Strict_forms_no_longer_pass_the_clear(string description) => Assert.Equal("info", Judge(description).State);

    [Theory]
    [InlineData("Removes Product.")]
    [InlineData("Remove Product filters.")]
    [InlineData("Ignores filters on Product.")]
    [InlineData("Removes all of the Product filters.")]
    [InlineData("Ignores the any Product filters.")]
    [InlineData("Ignores Product with zero sales.")]
    [InlineData("Ignores Product in this calculation.")]
    [InlineData("Ignores Product for every row.")]
    [InlineData("Share of all units, whatever the Product.")]
    [InlineData("All dates for Product.")]
    [InlineData("Regardless Product.")]
    [InlineData("Regardless of any Product.")]
    [InlineData("Regardless of Product with zero sales.")]
    [InlineData("Regardless of Products.")]
    [InlineData("Ignores Products filters.")]
    [InlineData("Ignores Other Product filters.")]
    [InlineData("Ignores Product Category Extra filters.")]
    [InlineData("Other Product filters are ignored.")]
    [InlineData("Ignores Product filters, so every row shows the grand total.")]
    [InlineData("Product filters are ignored, so every row shows the grand total.")]
    [InlineData("Ignores Date, returns for Product filters.")]
    [InlineData("Ignores Product or Date filters.")]
    public void Other_forms_read_information(string description) => Assert.Equal("info", Judge(description).State);

    // The fourth form: the cleared name ends the clause after the intent verb (Contoso's "(Absolute scope, ignores Product)").
    [Theory]
    [InlineData("Ignores Product.")]
    [InlineData("Ignores Product, filters.")]
    [InlineData("Customers whose first order falls in the period (Absolute scope, ignores Product).")]
    public void The_name_ending_the_clause_after_ignores_does_not_pass(string description) => Assert.Equal("info", Judge(description).State);

    [Theory]
    [InlineData("not")]
    [InlineData("no")]
    [InlineData("never")]
    [InlineData("cannot")]
    [InlineData("without")]
    [InlineData("can't")]
    [InlineData("won't")]
    [InlineData("isn't")]
    [InlineData("shouldn't")]
    [InlineData("Don't")]
    [InlineData("doesn’t")]
    [InlineData("couldn’t")]
    [InlineData("unlistedn't")]
    public void Negation_anywhere_in_the_sentence_blocks_every_form(string negation)
    {
        foreach (var form in new[] { "ignores Product filters", "Product filters are ignored", "regardless of Product" })
            Assert.Equal("info", Judge(negation + "; " + form + ".").State);
        Assert.Equal("info", Judge("Ignores Product filters; " + negation + ".").State);
    }

    [Fact]
    public void Whole_names_include_spaces_and_regex_characters()
    {
        Assert.Equal("info", Judge("Ignores Order Details filters.", "Order Details").State);
        Assert.Equal("info", Judge("Ignores Details filters.", "Order Details").State);
        Assert.Equal("info", Judge("Ignores Product (old) filters.", "Product (old)").State);
        Assert.Equal("info", Judge("Ignores Product old filters.", "Product (old)").State);
    }
}
