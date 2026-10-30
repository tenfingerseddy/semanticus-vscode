using System;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.MeasureContexts;
using Xunit;

namespace Semanticus.Tests;

// v-rc23f: a recheck of the fx16 Filters rule. Intent only confirms a would-be pass; "ignores" names only clears that
// reach the result where the value repeats; a Description states a clear in one sentence with no negation.
[Collection("restore-root")]
public sealed class CheckedVRc23fTests
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

    // Step 1: the ways a Power BI developer loses a filter, with a vague, empty or unrelated Description.
    [Theory]
    [InlineData("CALCULATE ( SUM ( Sales[Quantity] ), ALL ( Sales ) )", "Total quantity")]
    [InlineData("SUMX ( ALL ( Sales ), Sales[Quantity] )", "")]
    [InlineData("CALCULATE(SUM(Sales[Quantity]), REMOVEFILTERS())", "Units sold.")]
    [InlineData("CALCULATE(SUM(Sales[Quantity]), REMOVEFILTERS('Product'))", "Units sold.")]
    [InlineData("CALCULATE(SUM(Sales[Quantity]), ALL('Product'[Category]))", "Total quantity")]
    [InlineData("CALCULATE(SUM(Sales[Quantity]), ALLSELECTED('Product'))", "Total quantity")]
    [InlineData("CALCULATE(SUM(Sales[Quantity]), FILTER(ALL(Sales), Sales[Quantity] > 0))", "Positive quantities only.")]
    [InlineData("CALCULATE(SUM(Sales[Quantity]), CROSSFILTER(Sales[ProductKey], 'Product'[ProductKey], NONE))", "Total quantity")]
    [InlineData("IF(HASONEVALUE('Product'[Category]), CALCULATE(SUM(Sales[Quantity]), ALL('Product')))", "Total quantity")]
    [InlineData("VAR q = CALCULATE(SUM(Sales[Quantity]), ALL('Product')) RETURN DIVIDE(q, 1)", "Quantity.")]
    [InlineData("CALCULATE ( SUM ( Sales[Quantity] ), ALL ( Sales ) )", "Total quantity, ignoring returns.")]
    [InlineData("CALCULATE ( SUM ( Sales[Quantity] ), ALL ( Sales ) )", "Quantity regardless of discount.")]
    [InlineData("CALCULATE(SUM(Sales[Quantity]), ALL('Product'))", "Units of each product sold, ignoring returns.")]
    [InlineData("CALCULATE(SUM(Sales[Quantity]), ALL('Product'))", "Does not ignore Product filters.")]
    [InlineData("CALCULATE(SUM(Sales[Quantity]), ALL('Product'))", "Ignores Sales filters.")]
    public async Task A_lost_filter_never_reads_passed(string formula, string description)
    {
        var mark = await Clear(formula, description);
        Assert.True(mark.State != "passed", $"Filters is {mark.State}: {mark.Sentence}");
        Assert.DoesNotContain("No ignored filter", mark.Sentence);
        Assert.Contains("Product", mark.Sentence);
    }

    // Step 1: the clear sits in a helper measure that feeds the result.
    [Theory]
    [InlineData("[Qty Helper]")]
    [InlineData("VAR h = [Qty Helper] RETURN h")]
    public async Task A_lost_filter_in_a_helper_never_reads_passed(string formula)
    {
        var mark = await Clear(formula, "Total quantity", helper: "CALCULATE(SUM(Sales[Quantity]), ALL('Product'))");
        Assert.True(mark.State != "passed", $"Filters is {mark.State}: {mark.Sentence}");
        Assert.DoesNotContain("No ignored filter", mark.Sentence);
    }

    // Step 2: a Description that names a different table than the one cleared does not pass.
    [Fact]
    public async Task A_Description_about_another_table_does_not_pass_the_clear()
    {
        var mark = await Clear("CALCULATE(SUM(Sales[Quantity]), ALL('Product'))", "Ignores Customer filters.", customer: true);
        Assert.True(mark.State == "info", $"Filters is {mark.State}: {mark.Sentence}");
        Assert.Contains("Product", mark.Sentence);
        Assert.Contains("300", mark.Sentence);
    }

    // 1.7 item 2 (L2): a stated clear no longer passes on words; the person says "It is on purpose".
    [Theory]
    [InlineData("Ignores Product filters.")]
    [InlineData("Total quantity. Product filters are ignored on purpose.")]
    [InlineData("Quantity regardless of Product.")]
    public async Task A_stated_clear_does_not_pass_on_words(string description)
    {
        var mark = await Clear("CALCULATE(SUM(Sales[Quantity]), ALL('Product'))", description);
        Assert.True(mark.State == "info", $"Filters is {mark.State}: {mark.Sentence}");
        Assert.DoesNotContain("on purpose", mark.Sentence);
        Assert.DoesNotContain("No ignored filter", mark.Sentence);
    }

    // Step 3: a right plain SUM still passes; a lost filter with no clear in the formula still reads Suspect.
    [Fact]
    public async Task A_right_plain_SUM_passes()
    {
        var mark = await Clear("SUM(Sales[Quantity])", "Total quantity");
        Assert.True(mark.State == "passed", $"Filters is {mark.State}: {mark.Sentence}");
        Assert.Contains("No ignored filter", mark.Sentence);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Ignores Customer filters.")]
    public async Task A_SUM_that_loses_rows_with_no_clear_reads_suspect(string description)
    {
        var mark = await Clear("SUM(Sales[Quantity])", description, orphans: 4, customer: true);
        Assert.True(mark.State == "suspect", $"Filters is {mark.State}: {mark.Sentence}");
    }

    // Step 2 judgement: the planner on the Contoso shape. ALL ( Sales ) clears every related dimension, so the
    // Description must state each repeated table. Naming only Product leaves Customer unstated.
    [Fact]
    public void ALL_Sales_needs_every_repeated_table_stated()
    {
        var model = CheckedMeasureContextTests.Contoso();
        var measure = new ContextMeasure("Sales", "Contoso quantity", "CALCULATE(SUM(Sales[Quantity]), ALL(Sales))");
        model = model with { Measures = model.Measures.Append(measure).ToArray() };
        var plan = new MeasureContextPlanner().Plan(new(model, measure));
        var brand = plan.FilterClears.First(c => c.Column?.Table == "Product").Column;
        var continent = plan.FilterClears.First(c => c.Column?.Table == "Customer").Column;
        ScenarioEvidence By(ContextColumn column) => new() { Name = "by " + column.Dax, Status = "ok", Rows = new[] {
            new ProbeRow { V = 628370, Members = new() { [column.Dax] = "Contoso first" } },
            new ProbeRow { V = 628370, Members = new() { [column.Dax] = "Contoso second" } } } };
        var probe = new ProbeResult { Status = "ok", Scenarios = new[] {
            new ScenarioEvidence { Name = "grand total", Status = "ok", Rows = new[] { new ProbeRow { V = 628370 } } },
            By(brand), By(continent) } };
        var mark = MeasureMarkJudge.FilterIntent(new MeasureMark { Name = "Filters", State = "passed" }, probe,
            plan.FilterClears, false);
        Assert.True(mark.State == "info", $"Filters is {mark.State}: {mark.Sentence}");
        Assert.Contains("Customer", mark.Sentence);
        Assert.Contains("628370", mark.Sentence.Replace(",", ""));
    }

    // Defect: the name may sit anywhere after the intent word, so the word need not govern the name. These Descriptions
    // say "for each product" or ignore something else, yet a mistaken ALL ( 'Product' ) reads "Passed ... on purpose".
    [Theory]
    [InlineData("Ignoring returns, quantity by product.")]
    [InlineData("Units sold for every product.")]
    [InlineData("Absolute change in Product quantity.")]
    public async Task An_intent_word_that_does_not_govern_the_name_does_not_pass_a_clear(string description)
    {
        var mark = await Clear("CALCULATE(SUM(Sales[Quantity]), ALL('Product'))", description);
        Assert.True(mark.State != "passed", $"Filters is {mark.State}: {mark.Sentence}");
        Assert.DoesNotContain("on purpose", mark.Sentence);
    }
}
