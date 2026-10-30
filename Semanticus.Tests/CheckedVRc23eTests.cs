using System;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.MeasureContexts;
using Xunit;

namespace Semanticus.Tests;

// v-rc23e: attacks on the fx13 Filters rule. A formula that clears filters reads information unless its
// Description says the clear is meant; a lost filter never reads passed; a helper's reason is true.
[Collection("restore-root")]
public sealed class CheckedVRc23eTests
{
    // The same fixture as CheckedFx13Tests.Clear: every clear gives the repeated value 300 in each place.
    private static async Task<MeasureMark> Clear(string formula, string description = "", long orphans = 0, bool customer = false)
    {
        using var root = new DecisionFixture();
        using var f = new MeasureMarksDoorTests.Fixture(root.Workspace);
        await f.Open(configure: m => {
            m.Tables["Sales"].AddDataColumn("Quantity", "Quantity", dataType: TabularEditor.TOMWrapper.DataType.Int64);
            if (!customer) return;
            // A second dimension the formula does not clear. Its orphan rows are lost from every Customer slicer.
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
                .Replace("REMOVEFILTERS('Product')", "ALL('Product')")
                .Replace("ALL('Product'[Category])", "ALL('Product')");
            if (normalized.Contains("ALL('Product')"))
                normalized = normalized.Replace(DaxBench.InlineScalar("[Total Quantity]"),
                    DaxBench.InlineScalar("CALCULATE(SUM(Sales[Quantity]), ALL('Product'))"));
            return f.Query(normalized);
        }));
        return (await MeasureMarksTests.Finish(f.Engine, "measure:Sales/Total Quantity")).Marks.Single(m => m.Name == "Filters");
    }

    // Step 1: the ways a Power BI developer breaks a measure, with a vague, empty or unrelated Description.
    [Theory]
    [InlineData("CALCULATE ( SUM ( Sales[Quantity] ), ALL ( Sales ) )", "Total quantity")]
    [InlineData("CALCULATE ( SUM ( Sales[Quantity] ), ALL ( Sales ) )", "")]
    [InlineData("SUMX ( ALL ( Sales ), Sales[Quantity] )", "Total quantity")]
    [InlineData("CALCULATE(SUM(Sales[Quantity]), REMOVEFILTERS('Product'))", "Units sold.")]
    [InlineData("CALCULATE(SUM(Sales[Quantity]), ALL('Product'[Category]))", "Total quantity")]
    [InlineData("IF(1 = 1, CALCULATE(SUM(Sales[Quantity]), ALL('Product')))", "Total quantity")]
    [InlineData("VAR q = CALCULATE(SUM(Sales[Quantity]), ALL('Product')) RETURN q", "Total quantity")]
    [InlineData("DIVIDE(CALCULATE(SUM(Sales[Quantity]), ALL('Product')), 1)", "Total quantity")]
    public async Task A_clear_with_a_vague_Description_never_reads_passed(string formula, string description)
    {
        var mark = await Clear(formula, description);
        Assert.NotEqual("passed", mark.State);
        Assert.DoesNotContain("No ignored filter", mark.Sentence);
    }

    // Step 1 and 2: a Description that says "ignoring" or "every" about something else is not a stated clear.
    [Theory]
    [InlineData("CALCULATE ( SUM ( Sales[Quantity] ), ALL ( Sales ) )", "Total quantity, ignoring returns.")]
    [InlineData("CALCULATE ( SUM ( Sales[Quantity] ), ALL ( Sales ) )", "Quantity sold to every customer.")]
    [InlineData("CALCULATE(SUM(Sales[Quantity]), ALL('Product'))", "Units of each product sold, ignoring returns.")]
    [InlineData("CALCULATE(SUM(Sales[Quantity]), ALL('Product'))", "Does not ignore Product filters.")]
    public async Task An_intent_word_about_something_else_does_not_pass_a_clear(string formula, string description)
    {
        var mark = await Clear(formula, description);
        Assert.True(mark.State != "passed", $"Filters is {mark.State}: {mark.Sentence}");
    }

    // New harm: orphan rows on Sales to Customer lose rows from every Customer slicer. Clearing Product does not make that
    // loss meant. Before fx13 this read Suspect; now a stated Product clear reads passed.
    [Theory]
    [InlineData("Ignores Product filters.")]
    [InlineData("")]
    public async Task Orphan_rows_on_an_uncleared_dimension_stay_suspect_when_the_formula_also_clears(string description)
    {
        var mark = await Clear("CALCULATE(SUM(Sales[Quantity]), ALL('Product'))", description, orphans: 4, customer: true);
        Assert.True(mark.State == "suspect", $"Filters is {mark.State}: {mark.Sentence}");
    }

    // New harm: FilterIntent replaces every earlier finding. A check that found a loss, or could not run, never reads passed.
    [Theory]
    [InlineData("suspect")]
    [InlineData("not run")]
    public void A_stated_clear_never_turns_a_suspect_or_not_run_finding_into_passed(string state)
    {
        var column = new ContextColumn("Product", "Category");
        var finding = new MeasureMark { Name = "Filters", State = state, Sentence = "Earlier finding." };
        var probe = state == "not run"
            ? new ProbeResult { Status = "error", Message = "The query timed out.", Scenarios = Array.Empty<ScenarioEvidence>() }
            : new ProbeResult { Status = "ok", Scenarios = new[] {
                new ScenarioEvidence { Name = "grand total", Status = "ok", Rows = new[] { new ProbeRow { V = 300 } } } } };
        var mark = MeasureMarkJudge.FilterIntent(finding, probe, new[] { new ContextFilterClear(column, "ALL") },
            false);
        Assert.True(mark.State == state, $"Filters is {mark.State}: {mark.Sentence}");
    }

    // Step 3: Kane's Contoso helper. REMOVEFILTERS () inside ShowValueForDates only sets a VAR in the guard.
    // Sales YTD still splits by Customer, so the plan must not say the formula removes Customer filters.
    [Fact]
    public void A_helper_guard_clear_does_not_say_the_measure_ignores_a_related_dimension()
    {
        var columns = new[] { new ContextColumn("Sales", "Quantity", Numeric: true),
            new ContextColumn("Sales", "Order Date", Time: true) { DateTyped = true },
            new ContextColumn("Sales", "CustomerKey"),
            new ContextColumn("Date", "Date", Time: true) { DateTyped = true },
            new ContextColumn("Customer", "CustomerKey"), new ContextColumn("Customer", "City") };
        var helper = new ContextMeasure("Sales", "ShowValueForDates",
            "VAR LastDateWithData = CALCULATE(MAX(Sales[Order Date]), REMOVEFILTERS()) "
            + "VAR FirstDateVisible = MIN('Date'[Date]) RETURN FirstDateVisible <= LastDateWithData");
        var measure = new ContextMeasure("Sales", "Sales YTD",
            "IF([ShowValueForDates], CALCULATE(SUM(Sales[Quantity]), DATESYTD('Date'[Date])))");
        var model = new ContextModel(columns, new[] { new ContextRelationship(columns[1], columns[3]),
            new ContextRelationship(columns[2], columns[4]) }, new[] { helper, measure }, new[] { "Date" });
        var plan = new MeasureContextPlanner().Plan(new(model, measure));

        // The mark Kane reads: Sales YTD by City is not a repeated value, so "ignores Customer filters" is false.
        var city = columns[5];
        var probe = new ProbeResult { Status = "ok", Scenarios = new[] {
            new ScenarioEvidence { Name = "grand total", Status = "ok", Rows = new[] { new ProbeRow { V = 215550 } } },
            new ScenarioEvidence { Name = "by 'Customer'[City]", Status = "ok", Rows = new[] {
                new ProbeRow { V = 53388, Members = new() { [city.Dax] = "Rockhampton" } },
                new ProbeRow { V = 162, Members = new() { [city.Dax] = "Springwood" } } } } } };
        var mark = MeasureMarkJudge.FilterIntent(new MeasureMark { Name = "Filters", State = "passed" }, probe,
            plan.FilterClears, false);
        Assert.False(mark.Sentence.Contains("Customer"), $"Filters is {mark.State}: {mark.Sentence}");
        Assert.DoesNotContain(plan.FilterClears, c => c.Column?.Table == "Customer");
        Assert.All(plan.Contexts.Where(c => c.Column?.Table == "Customer"), c => Assert.DoesNotContain("removes filters", c.Reason));
    }

    // Step 3: a right plain SUM still passes and still says "No ignored filter".
    [Fact]
    public async Task A_right_plain_SUM_still_passes()
    {
        var mark = await Clear("SUM(Sales[Quantity])", "Total quantity");
        Assert.True(mark.State == "passed", $"Filters is {mark.State}: {mark.Sentence}");
        Assert.Contains("No ignored filter", mark.Sentence);
    }
}
