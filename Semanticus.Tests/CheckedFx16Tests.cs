using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.MeasureContexts;
using Xunit;
using Xunit.Abstractions;

namespace Semanticus.Tests;

[Collection("restore-root")]
public sealed class CheckedFx16Tests(ITestOutputHelper output)
{
    private static readonly ContextColumn Category = new("Product", "Category");
    private static ProbeResult Values(object first = null, object second = null) => new() {
        Status = "ok", Scenarios = new[] {
            new ScenarioEvidence { Name = "grand total", Status = "ok", Rows = new[] { new ProbeRow { V = 300 } } },
            new ScenarioEvidence { Name = "by " + Category.Dax, Status = "ok", Rows = new[] {
                new ProbeRow { V = first ?? 300, Members = new() { [Category.Dax] = "Contoso first" } },
                new ProbeRow { V = second ?? 300, Members = new() { [Category.Dax] = "Contoso second" } }
            } }
        }
    };

    [Theory]
    [InlineData("Product quantity. Ignores returns.", "info")]
    [InlineData("Ignores returns. Product quantity.", "info")]
    [InlineData("Never ignores Product filters.", "info")]
    [InlineData("No Product filters are ignored.", "info")]
    [InlineData("Doesn't ignore Product filters.", "info")]
    [InlineData("Does not ignore Product filters.", "info")]
    [InlineData("Respects not filtered PRODUCT filters.", "info")]
    [InlineData("Product is not filtered.", "info")]
    [InlineData("Ignores Product filters, not Customer filters.", "info")]
    [InlineData("Regardless of Production filters.", "info")]
    [InlineData("Units of each product sold, ignoring returns.", "info")]
    [InlineData("Never ignores Customer filters. Ignores Product filters.", "info")]
    [InlineData("Product filters are ignored.", "info")]
    [InlineData("Ignores Product filters.", "info")]
    [InlineData("Regardless of Category.", "info")]
    public void Intent_names_its_target_in_one_sentence_without_negation(string description, string state)
    {
        var mark = MeasureMarkJudge.FilterIntent(new MeasureMark { Name = "Filters", State = "passed" },
            Values(), new[] { new ContextFilterClear(Category, "ALL") }, false);
        Assert.Equal(state, mark.State);
    }

    [Fact]
    public void Intent_does_not_cover_another_ignored_table()
    {
        var probe = Values();
        var city = new ContextColumn("Customer", "City");
        probe.Scenarios = probe.Scenarios.Append(new ScenarioEvidence { Name = "by " + city.Dax, Status = "ok",
            Rows = new[] {
                new ProbeRow { V = 300, Members = new() { [city.Dax] = "Contoso first" } },
                new ProbeRow { V = 300, Members = new() { [city.Dax] = "Contoso second" } }
            } }).ToArray();
        var mark = MeasureMarkJudge.FilterIntent(new MeasureMark { State = "passed" }, probe,
            new[] { new ContextFilterClear(Category, "ALL"), new ContextFilterClear(city, "REMOVEFILTERS") },
            false);
        Assert.Equal("info", mark.State);
        Assert.Contains("Customer", mark.Sentence);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Ignores Product filters.")]
    public void A_clear_with_varying_values_keeps_the_passed_finding(string description)
    {
        var finding = new MeasureMark { State = "passed", Sentence = "No ignored filter in 3 places checked." };
        Assert.Same(finding, MeasureMarkJudge.FilterIntent(finding, Values(100, 200),
            new[] { new ContextFilterClear(Category, "ALL") }, false));
    }

    [Theory]
    [InlineData("suspect")]
    [InlineData("not run")]
    [InlineData("info")]
    public void Intent_preserves_all_earlier_nonpassed_findings(string state)
    {
        var finding = new MeasureMark { Name = "Filters", State = state, Sentence = "Contoso earlier finding." };
        Assert.Same(finding, MeasureMarkJudge.FilterIntent(finding, Values(),
            new[] { new ContextFilterClear(Category, "ALL") }, false));
    }

    [Fact]
    public async Task Sales_percent_of_Category_passes_without_information()
    {
        using var root = new DecisionFixture();
        using var f = new MeasureMarksDoorTests.Fixture(root.Workspace);
        await f.Open();
        await f.Sessions.Current.MutateAsync("human", "Contoso category share", m => {
            var measure = m.Tables["Sales"].Measures["Total Sales"];
            measure.Name = "Sales % of Category";
            measure.Expression = "DIVIDE(SUM(Sales[Amount]), CALCULATE(SUM(Sales[Amount]), ALL('Product'[Category])))";
        });
        f.Engine.SetLiveConnectionForTest(LiveConnection.ForTest("xmla", "contoso", "Contoso Sales", q => {
            var rs = f.Query(q);
            var anonymous = q.IndexOf("MEASURE 'Sales'[__smx_", StringComparison.Ordinal);
            if (anonymous >= 0 && q[anonymous..].Contains("[Sales % of Category]"))
            {
                var index = Array.FindIndex(rs.Columns, c => c.Name == "v");
                if (index >= 0) foreach (var row in rs.Rows) row[index] = Convert.ToDecimal(row[index]) / 300m;
            }
            return rs;
        }));
        var mark = (await MeasureMarksTests.Finish(f.Engine, "measure:Sales/Sales % of Category"))
            .Marks.Single(m => m.Name == "Filters");
        Assert.Equal("passed", mark.State);
        Assert.DoesNotContain("ignores", mark.Sentence);
        Assert.Contains(mark.Cells, c => c.After != "1");
    }

    [Fact]
    public async Task Orphan_rows_on_Customer_stay_suspect_beside_a_Product_clear()
    {
        using var root = new DecisionFixture();
        using var f = new MeasureMarksDoorTests.Fixture(root.Workspace);
        await f.Open(configure: m => {
            var fk = m.Tables["Sales"].AddDataColumn("CustomerKey", "CustomerKey", dataType: TabularEditor.TOMWrapper.DataType.Int64);
            var customer = m.AddTable("Customer");
            var pk = customer.AddDataColumn("CustomerKey", "CustomerKey", dataType: TabularEditor.TOMWrapper.DataType.Int64);
            customer.AddDataColumn("City", "City");
            var relationship = m.AddRelationship();
            relationship.FromColumn = fk;
            relationship.ToColumn = pk;
        });
        await f.Sessions.Current.MutateAsync("human", "Contoso clear with lost rows", m => {
            var measure = m.Tables["Sales"].Measures["Total Sales"];
            measure.Expression = "CALCULATE(SUM(Sales[Amount]), ALL('Product'))";
            measure.Description = "Ignores Product filters.";
        });
        f.Orphans = 4;
        var mark = (await MeasureMarksTests.Finish(f.Engine, "measure:Sales/Total Sales")).Marks.Single(m => m.Name == "Filters");
        Assert.Equal("suspect", mark.State);
        Assert.Contains("no related key", mark.Sentence);
    }

    [Theory]
    [InlineData("IF([Clear helper], SUM(Sales[Quantity]))", false)]
    [InlineData("VAR guard = [Clear helper] RETURN IF(guard, SUM(Sales[Quantity]))", false)]
    [InlineData("SWITCH(TRUE(), [Clear helper] > 0, SUM(Sales[Quantity]), 0)", false)]
    [InlineData("VAR unused = [Clear helper] RETURN SUM(Sales[Quantity])", false)]
    [InlineData("VAR result = [Clear helper] RETURN result", true)]
    [InlineData("DIVIDE([Clear helper], 100)", true)]
    public void Only_helpers_that_feed_a_result_contribute_clears(string formula, bool clears)
    {
        var model = CheckedMeasureContextTests.Contoso();
        var helper = new ContextMeasure("Sales", "Clear helper", "CALCULATE(SUM(Sales[Quantity]), ALL(Product))");
        var measure = new ContextMeasure("Sales", "Contoso result", formula);
        model = model with { Measures = model.Measures.Append(helper).Append(measure).ToArray() };
        var plan = new MeasureContextPlanner().Plan(new(model, measure));
        Assert.Equal(clears, plan.FilterClears.Any(c => c.Column?.Table == "Product"));
        if (!clears) Assert.All(plan.Contexts.Where(c => c.Column?.Table == "Product"),
            c => Assert.DoesNotContain("removes filters", c.Reason));
    }

    [Fact]
    public async Task Count_information_on_the_bundled_Contoso_Shop_fixture()
    {
        using var f = new DecisionFixture();
        f.UseNull();
        var source = Path.Combine(AssistantGateKit.RepoRoot(), "Semanticus.VSCode", "assets", "learn", "contoso-shop");
        var copy = Path.Combine(f.Workspace, "contoso-shop");
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(copy, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
        await f.Engine.OpenAsync(copy);
        var model = (await f.Sessions.Current.ReadAsync(MeasureMarkSnapshot.Capture)).ContextModel;
        var count = model.Measures.Count(measure => {
            var plan = new MeasureContextPlanner().Plan(new(model, measure));
            return MeasureMarkJudge.FilterIntent(new MeasureMark { State = "passed" }, Values(),
                plan.FilterClears, false).State == "info";
        });
        output.WriteLine($"Bundled Contoso Shop: {model.Measures.Length} measures; {count} information marks; headless canned probes.");
        Assert.NotEmpty(model.Measures);
    }
}
