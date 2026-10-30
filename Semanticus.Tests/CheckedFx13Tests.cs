using System;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.MeasureContexts;
using Xunit;

namespace Semanticus.Tests;

[Collection("restore-root")]
public sealed class CheckedFx13Tests
{
    private static async Task<MeasureMark> Clear(string formula, string description = "", long orphans = 0, bool onPurpose = false)
    {
        using var root = new DecisionFixture();
        using var f = new MeasureMarksDoorTests.Fixture(root.Workspace);
        await f.Open(configure: m => m.Tables["Sales"].AddDataColumn("Quantity", "Quantity",
            dataType: TabularEditor.TOMWrapper.DataType.Int64));
        await f.Sessions.Current.MutateAsync("human", "Contoso filter intent", m => {
            var measure = m.Tables["Sales"].Measures["Total Sales"];
            measure.Name = "Total Quantity";
            measure.Expression = formula;
            measure.Description = description;
            if (onPurpose) measure.SetAnnotation("Semanticus.Filters.OnPurpose", "true");
        });
        f.Orphans = orphans;
        f.Engine.SetLiveConnectionForTest(LiveConnection.ForTest("xmla", "contoso", "Contoso Sales", q => {
            // The fixture supplies the same repeated result for each explicit modifier.
            var normalized = q.Replace("ALL ( Sales )", "ALL('Product')")
                .Replace("REMOVEFILTERS('Product')", "ALL('Product')")
                .Replace("REMOVEFILTERS()", "ALL('Product')")
                .Replace("ALLSELECTED('Product')", "ALL('Product')")
                .Replace("ALL('Product'[Category])", "ALL('Product')")
                .Replace("CROSSFILTER(Sales[ProductKey], Product[ProductKey], NONE)", "ALL('Product')");
            // Resolve the anonymous probe's measure reference, as the model would. Row witnesses keep their own bodies.
            if (normalized.Contains("ALL('Product')"))
                normalized = normalized.Replace(DaxBench.InlineScalar("[Total Quantity]"),
                    DaxBench.InlineScalar("CALCULATE(SUM(Sales[Quantity]), ALL('Product'))"));
            return f.Query(normalized);
        }));
        return (await MeasureMarksTests.Finish(f.Engine, "measure:Sales/Total Quantity")).Marks.Single(m => m.Name == "Filters");
    }

    [Theory]
    [InlineData("CALCULATE ( SUM ( Sales[Quantity] ), ALL ( Sales ) )")]
    [InlineData("SUMX ( ALL ( Sales ), Sales[Quantity] )")]
    [InlineData("CALCULATE(SUM(Sales[Quantity]), ALL('Product'))")]
    [InlineData("CALCULATE(SUM(Sales[Quantity]), ALL('Product'[Category]))")]
    [InlineData("CALCULATE(SUM(Sales[Quantity]), REMOVEFILTERS('Product'))")]
    [InlineData("CALCULATE(SUM(Sales[Quantity]), REMOVEFILTERS())")]
    [InlineData("CALCULATE(SUM(Sales[Quantity]), ALLSELECTED('Product'))")]
    [InlineData("CALCULATE(SUM(Sales[Quantity]), CROSSFILTER(Sales[ProductKey], Product[ProductKey], NONE))")]
    public async Task An_unstated_clear_is_information_with_its_target_and_value(string formula)
    {
        var mark = await Clear(formula);
        Assert.Equal("info", mark.State);
        Assert.Contains("Product", mark.Sentence);
        Assert.Contains("300", mark.Sentence);
        Assert.DoesNotContain("No ignored filter", mark.Sentence);
        Assert.DoesNotContain("on purpose", mark.Sentence);
    }

    [Theory]
    [InlineData("ignore", "info")]
    [InlineData("ignoring", "info")]
    [InlineData("regardless", "info")]
    [InlineData("all dates", "info")]
    [InlineData("removes", "info")]
    [InlineData("not filtered", "info")]
    [InlineData("whatever", "info")]
    public async Task A_named_clear_needs_a_strict_form(string intent, string state)
    {
        var mark = await Clear("CALCULATE(SUM(Sales[Quantity]), ALL('Product'))", intent + " PRODUCT filters.");
        Assert.Equal(state, mark.State);
        Assert.DoesNotContain("on purpose", mark.Sentence);
        Assert.Contains("Product", mark.Sentence);
        Assert.DoesNotContain("No ignored filter", mark.Sentence);
    }

    [Theory]
    [InlineData("Ignores Customer filters.")]
    [InlineData("Product quantity.")]
    [InlineData("")]
    public async Task Both_the_target_and_an_intent_word_are_required(string description)
    {
        var mark = await Clear("CALCULATE(SUM(Sales[Quantity]), ALL('Product'))", description);
        Assert.Equal("info", mark.State);
        Assert.DoesNotContain("No ignored filter", mark.Sentence);
    }

    [Fact]
    // 1.7 item 2 (L2): Description words never pass a cleared filter. Only the person's "It is on purpose" does.
    public async Task Naming_a_cleared_column_is_not_stated_intent()
    {
        var mark = await Clear("CALCULATE(SUM(Sales[Quantity]), ALL('Product'[Category]))", "Regardless of CATEGORY.");
        Assert.Equal("info", mark.State);
        Assert.Contains("Category", mark.Sentence);
        Assert.DoesNotContain("on purpose", mark.Sentence);
    }

    // 1.7 item 2 (L2), end to end: the person's "It is on purpose" on the measure reads Overridden after a run.
    [Fact]
    public async Task The_persons_on_purpose_reads_overridden_after_a_run()
    {
        var mark = await Clear("CALCULATE(SUM(Sales[Quantity]), ALL('Product'))", "Product quantity.", onPurpose: true);
        // ReasonCode and ActionCodes are proven on the judge (CheckedL2Tests); the run copy carries them after the L1 join.
        Assert.Equal("info", mark.State);
        Assert.Equal("Ignores Product Category filters on purpose. You marked this.", mark.Sentence);
    }

    [Fact]
    public async Task A_plain_SUM_with_orphan_rows_still_reads_suspect()
    {
        var mark = await Clear("SUM(Sales[Quantity])", orphans: 4);
        Assert.Equal("suspect", mark.State);
        Assert.Contains("no related key", mark.Sentence);
    }

    [Fact]
    public async Task A_formula_clear_does_not_accuse_the_clear_when_orphans_exist()
    {
        var mark = await Clear("CALCULATE(SUM(Sales[Quantity]), ALL('Product'))", orphans: 4);
        Assert.Equal("suspect", mark.State);
        Assert.Contains("no related key", mark.Sentence);
    }

    [Theory]
    [InlineData("Bridge Steps", "Step")]
    [InlineData("Customer", "City")]
    public async Task Context_lines_and_place_rows_are_unique_in_the_engine_result(string table, string column)
    {
        using var root = new DecisionFixture();
        using var f = new MeasureMarksDoorTests.Fixture(root.Workspace);
        await f.Open(configure: m => m.AddTable(table).AddDataColumn(column, column));
        await f.Sessions.Current.MutateAsync("human", "Contoso repeated context", m =>
            m.Tables["Sales"].Measures["Total Sales"].Expression = "CALCULATE(SUM(Sales[Amount]), ALL('" + table + "'))");
        var result = await MeasureMarksTests.Finish(f.Engine, "measure:Sales/Total Sales");
        foreach (var name in new[] { "Filters", "Contexts", "Totals" })
        {
            var mark = result.Marks.Single(m => m.Name == name);
            Assert.Single(mark.ChosenContexts, c => c.Column?.Table == table && c.Column.Name == column);
            Assert.Equal(mark.Cells.Length, mark.Cells.Select(c => c.Context).Distinct().Count());
        }
        var filters = result.Marks.Single(m => m.Name == "Filters");
        Assert.Single(filters.Cells, c => c.Context == "Grand total");
    }

    [Fact]
    public void A_clear_names_the_value_when_other_contexts_give_different_values()
    {
        var column = new ContextColumn("Product", "Category");
        var probe = new ProbeResult { Status = "ok", Scenarios = new[] {
            new ScenarioEvidence { Name = "grand total", Status = "ok", Rows = new[] { new ProbeRow { V = 300 } } },
            new ScenarioEvidence { Name = "by 'Product'[Category]", Status = "ok", Rows = new[] {
                new ProbeRow { V = 300, Members = new() { [column.Dax] = "Bikes" } },
                new ProbeRow { V = 300, Members = new() { [column.Dax] = "Accessories" } }
            } },
            new ScenarioEvidence { Name = "single-select 'Sales'[Quantity]", Status = "ok", Rows = new[] { new ProbeRow { V = 100 } } }
        } };
        var mark = MeasureMarkJudge.FilterIntent(new MeasureMark { State = "passed" }, probe,
            new[] { new ContextFilterClear(column, "ALL") }, false);
        Assert.Equal("info", mark.State);
        Assert.Contains("300", mark.Sentence);
        Assert.DoesNotContain("every place", mark.Sentence);
    }

    [Fact]
    public void Combining_filter_evidence_removes_duplicate_places_before_the_limit()
    {
        var cells = Enumerable.Range(0, 7).Select(i => new MeasureMarkCell { Context = "Place " + i, After = "300" }).ToArray();
        var mark = MeasureMarkJudge.Combine("Filters", new[] {
            new MeasureMark { State = "passed", Cells = new[] { cells[0], cells[0], cells[1] } },
            new MeasureMark { State = "passed", Cells = cells }
        });
        Assert.Equal(6, mark.Cells.Length);
        Assert.Equal(6, mark.Cells.Select(c => c.Context).Distinct().Count());
        Assert.Contains(mark.Cells, c => c.Context == "Place 5");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_helper_global_clear_does_not_claim_to_remove_a_filter_without_a_relationship_path(bool usesSelector)
    {
        var columns = new[] { new ContextColumn("Sales", "Quantity", Numeric: true),
            new ContextColumn("Sales", "Order Date", Time: true) { DateTyped = true },
            new ContextColumn("Date", "Date", Time: true) { DateTyped = true },
            new ContextColumn("Bridge Steps", "Step"), new ContextColumn("Customer", "City") };
        var helper = new ContextMeasure("Sales", "ShowValueForDates",
            "VAR LastDateWithData = CALCULATE(MAX(Sales[Order Date]), REMOVEFILTERS()) "
            + "VAR FirstDateVisible = MIN('Date'[Date]) RETURN FirstDateVisible <= LastDateWithData");
        var body = "IF([ShowValueForDates], CALCULATE(SUM(Sales[Quantity]), DATESYTD('Date'[Date])))";
        if (usesSelector) body = "IF(SELECTEDVALUE('Bridge Steps'[Step]) = \"YTD\", " + body + ")";
        var measure = new ContextMeasure("Sales", "Sales YTD", body);
        var model = new ContextModel(columns, new[] { new ContextRelationship(columns[1], columns[2]) },
            new[] { helper, measure }, new[] { "Date" });
        var plan = new MeasureContextPlanner().Plan(new(model, measure));
        Assert.DoesNotContain(plan.FilterClears, c => c.Column?.Table is "Bridge Steps" or "Customer");
        var unrelated = plan.Contexts.Where(c => c.Column?.Table is "Bridge Steps" or "Customer").ToArray();
        Assert.All(unrelated, c => {
            Assert.False(plan.ClearsFilter(c));
            Assert.DoesNotContain("removes filters", c.Reason);
            if (c.Shape != ContextShape.Subtotal)
            {
                Assert.Contains("no relationship path", c.Reason);
                Assert.True(plan.ExpectsConstant(c));
            }
        });
    }
}
