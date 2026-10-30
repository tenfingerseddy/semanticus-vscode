using System;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.MeasureContexts;
using Xunit;

namespace Semanticus.Tests;

// v-rc23g: a recheck of the intent-word fix (d62c3253). A true statement of a clear passes; a Description where the
// intent word governs something else does not pass a mistaken ALL ( 'Product' ).
[Collection("restore-root")]
public sealed class CheckedVRc23gTests
{
    // The CheckedVRc23eTests fixture: every clear gives the repeated value 300 in each Product place.
    private static async Task<MeasureMark> Clear(string formula, string description = "", long orphans = 0, bool customer = false,
        string helper = null)
    {
        using var root = new DecisionFixture();
        using var f = new MeasureMarksDoorTests.Fixture(root.Workspace);
        await f.Open(configure: m => {
            m.Tables["Sales"].AddDataColumn("Quantity", "Quantity", dataType: TabularEditor.TOMWrapper.DataType.Int64);
            if (helper != null) m.Tables["Sales"].AddMeasure("Qty Helper", helper);
            if (!customer) return;
            var fk = m.Tables["Sales"].AddDataColumn("CustomerKey", "CustomerKey", dataType: TabularEditor.TOMWrapper.DataType.Int64);
            var c = m.AddTable("Customer");
            var pk = c.AddDataColumn("CustomerKey", "CustomerKey", dataType: TabularEditor.TOMWrapper.DataType.Int64);
            c.AddDataColumn("City", "City");
            var rel = m.AddRelationship();
            rel.FromColumn = fk;
            rel.ToColumn = pk;
        });
        await f.Sessions.Current.MutateAsync("human", "Contoso filter intent", m => {
            var measure = m.Tables["Sales"].Measures["Total Sales"];
            measure.Name = "Total Quantity";
            measure.Expression = formula;
            measure.Description = description;
        });
        f.Orphans = orphans;
        f.Engine.SetLiveConnectionForTest(LiveConnection.ForTest("xmla", "contoso", "Contoso Sales", q => {
            var normalized = q.Replace("ALL ( Sales )", "ALL('Product')")
                .Replace("ALL(Sales)", "ALL('Product')")
                .Replace("REMOVEFILTERS()", "ALL('Product')")
                .Replace("REMOVEFILTERS('Product')", "ALL('Product')")
                .Replace("ALLSELECTED('Product')", "ALL('Product')")
                .Replace("ALL('Product'[Category])", "ALL('Product')")
                .Replace("CROSSFILTER(Sales[ProductKey], 'Product'[ProductKey], NONE)", "ALL('Product')");
            if (helper != null && normalized.Contains("[Qty Helper]"))
                normalized = normalized.Replace("[Qty Helper]", "CALCULATE(SUM(Sales[Quantity]), ALL('Product'))");
            if (normalized.Contains("ALL('Product')"))
                normalized = normalized.Replace(DaxBench.InlineScalar("[Total Quantity]"),
                    DaxBench.InlineScalar("CALCULATE(SUM(Sales[Quantity]), ALL('Product'))"));
            return f.Query(normalized);
        }));
        return (await MeasureMarksTests.Finish(f.Engine, "measure:Sales/Total Quantity")).Marks.Single(m => m.Name == "Filters");
    }

    // 1.7 item 2 (L2): true statements of a clear no longer pass on words; the person says "It is on purpose".
    [Theory]
    [InlineData("Sales for all dates, regardless of Product category.")]
    [InlineData("Quantity ignoring Date and Product filters.")]
    [InlineData("Product filters are ignored.")]
    [InlineData("Product and Date filters are ignored.")]
    [InlineData("Removes the Product filter.")]
    [InlineData("Share of sales across all products (ignores product filters)")]
    [InlineData("Absolute scope: total quantity, ignoring the Product filter")]
    public async Task A_true_statement_of_the_clear_does_not_pass_on_words(string description)
    {
        var mark = await Clear("CALCULATE(SUM(Sales[Quantity]), ALL('Product'))", description);
        Assert.True(mark.State == "info", $"Filters is {mark.State}: {mark.Sentence}");
        Assert.DoesNotContain("on purpose", mark.Sentence);
    }

    // Loose Descriptions that the fix already stops: the name is past a comma or past six words.
    [Theory]
    [InlineData("Regardless of returns, by product.")]
    [InlineData("Product quantity regardless of discount.")]
    [InlineData("Whatever happens, units by product.")]
    [InlineData("Total quantity, regardless of the returns made for each Product.")]
    public async Task A_loose_Description_the_fix_stops_does_not_pass(string description)
    {
        var mark = await Clear("CALCULATE(SUM(Sales[Quantity]), ALL('Product'))", description);
        Assert.True(mark.State != "passed", $"Filters is {mark.State}: {mark.Sentence}");
        Assert.DoesNotContain("on purpose", mark.Sentence);
    }

    // Defect: the intent word governs "returns", "blanks" or "duplicates", not Product, yet the name is in the same
    // clause within six words. A mistaken ALL ( 'Product' ) reads "Passed ... on purpose". The first is the
    // v-rc23f defect again with the comma left out. The last says the Product filter is kept.
    [Theory]
    [InlineData("Ignores returns for each product.")]
    [InlineData("Ignoring returns and quantity by product.")]
    [InlineData("Ignores blanks in each Product row.")]
    [InlineData("Removes duplicate orders per product.")]
    [InlineData("Respects the Product filter and ignores returns.")]
    [InlineData("Share of all units, whatever the Product.")]
    public async Task An_intent_word_that_governs_another_word_does_not_pass_a_clear(string description)
    {
        var mark = await Clear("CALCULATE(SUM(Sales[Quantity]), ALL('Product'))", description);
        Assert.True(mark.State != "passed", $"Filters is {mark.State}: {mark.Sentence}");
        Assert.DoesNotContain("on purpose", mark.Sentence);
    }

    // FX19 passed a strict comma list. 1.7 item 2 (L2): a list of names is words too, so it reads information.
    [Fact]
    public async Task A_comma_list_of_cleared_tables_reads_information()
    {
        var mark = await Clear("CALCULATE(SUM(Sales[Quantity]), ALL('Product'))", "Ignores Date, Product and Customer filters.");
        Assert.True(mark.State == "info", $"Filters is {mark.State}: {mark.Sentence}");
    }
}
