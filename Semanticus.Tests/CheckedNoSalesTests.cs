using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Decisions;
using Semanticus.Engine;
using Semanticus.Engine.Decisions;
using Semanticus.Engine.MeasureContexts;
using Xunit;

namespace Semanticus.Tests;

[Collection("restore-root")]
public sealed class CheckedNoSalesTests
{
    private const string City = "'Customer'[City]";
    private sealed class Cities : IMeasureContextPlanner
    {
        public MeasureContextPlan Plan(ContextPlanningRequest request) => new(new ContextCandidate[] {
            new("cities", new("Customer", "City"), ContextShape.Subtotal, ContextRole.Related,
                "Compare Contoso cities.", "Split sales by city.", 1, Array.Empty<string>(), Array.Empty<string>())
                { DetailColumn = new("Customer", "CustomerKey", Numeric: true) },
            new("city-selection", new("Customer", "City"), ContextShape.Single, ContextRole.Related,
                "Select Contoso cities.", "Split sales by city.", 2, Array.Empty<string>(), Array.Empty<string>()),
            new("total", null, ContextShape.GrandTotal, ContextRole.Total,
                "Check the grand total.", "Add sales.", 3, Array.Empty<string>(), Array.Empty<string>())
        }, Array.Empty<string>());
    }

    [Theory]
    [InlineData("Total Quantity", "SUM('Sales'[Quantity])", false)]
    [InlineData("Sales Count", "COUNTROWS('Sales')", false)]
    [InlineData("Average Quantity", "AVERAGE('Sales'[Quantity])", false)]
    [InlineData("Dropped Quantity", "CALCULATE(SUM('Sales'[Quantity]), 'Sales'[Quantity] > 100)", true)]
    [InlineData("Dropped City", "CALCULATE(SUM('Sales'[Quantity]), KEEPFILTERS('Customer'[City] <> \"Seattle\"))", true)]
    public async Task Blank_cities_are_suspect_only_when_sales_rows_exist(string name, string formula, bool dropped)
    {
        using var f = new DecisionFixture();
        await f.Engine.CreateModelAsync("Contoso", 1604);
        await f.Sessions.Current.MutateAsync("human", "Contoso city fixture", m => {
            var sales = m.AddTable("Sales");
            sales.AddDataColumn("Quantity", "Quantity", dataType: TabularEditor.TOMWrapper.DataType.Int64);
            sales.AddDataColumn("CustomerKey", "CustomerKey", dataType: TabularEditor.TOMWrapper.DataType.Int64);
            var customer = m.AddTable("Customer");
            customer.AddDataColumn("City", "City", dataType: TabularEditor.TOMWrapper.DataType.String);
            customer.AddDataColumn("CustomerKey", "CustomerKey", dataType: TabularEditor.TOMWrapper.DataType.Int64);
        });
        await f.Engine.CreateRelationshipAsync("column:Sales/CustomerKey", "column:Customer/CustomerKey", null, true, "human");
        await f.Engine.CreateMeasureAsync("table:Sales", name, formula, "human");
        f.Sessions.Current.LiveOrigin = new LiveOrigin("contoso", "Contoso", null);
        f.Engine.MeasureContextPlannerForTests = new Cities();
        var counts = new List<string>();
        var cities = new[] { "Arlington", "Brooklyn", "Seattle", "Boston" };
        // Seattle has two sales, Boston has one. Arlington and Brooklyn have no sales.
        var salesRows = new[] { (City: "Seattle", Quantity: 10m), (City: "Seattle", Quantity: 20m), (City: "Boston", Quantity: 200m) };
        f.Engine.SetLiveConnectionForTest(LiveConnection.ForTest("xmla", "contoso", "Contoso", query => {
            if (query.Contains("OrphanRows")) return Rs(RelationshipProbes.CombinedColumns, new object[] { 0L, 0L, 0L, 0L, 3L, 4L });
            if (query.Contains("EVALUATE TOPN(50")) return Rs(new[] { "City" },
                cities.Select(c => new object[] { c }).ToArray());
            var count = query.Contains("COALESCE(COUNTROWS('Sales'), 0)");
            if (count) counts.Add(query);
            var evaluateAt = query.LastIndexOf("EVALUATE", StringComparison.Ordinal);
            var outer = evaluateAt < 0 ? query : query[evaluateAt..];
            var detail = outer.Contains(City + ", 'Customer'[CustomerKey]")
                || outer.Contains(City + ",\n    'Customer'[CustomerKey]");
            var grouped = outer.Contains(City + ",");
            var empty = outer.Contains("FALSE ()");
            var selected = cities.Where(c => !outer.Contains("TREATAS") || outer.Contains("\"" + c + "\"")).ToArray();
            object Value(string[] members)
            {
                var visibleSales = salesRows.Where(r => members.Contains(r.City)).ToArray();
                if (count) return visibleSales.Length;
                var measuredSales = dropped ? visibleSales.Where(r => name == "Dropped City" ? r.City != "Seattle" : r.Quantity > 100).ToArray() : visibleSales;
                if (measuredSales.Length == 0) return null;
                return name == "Sales Count" ? measuredSales.Length : name == "Average Quantity"
                    ? measuredSales.Average(r => r.Quantity) : measuredSales.Sum(r => r.Quantity);
            }
            var names = detail ? new[] { "City", "CustomerKey", "v", "__present" }
                : grouped ? new[] { "City", "v", "__present" } : new[] { "v", "__present" };
            if (empty) return Rs(names);
            if (!grouped) return Rs(names, new object[] { Value(selected), 1 });
            object[] Row(string city, int key, object value) => detail ? new object[] { city, key, value, 1 } : new object[] { city, value, 1 };
            var rows = selected.Select(c => Row(c, Array.IndexOf(cities, c) + 1, Value(new[] { c }))).ToList();
            // Counts return a different order. The comparison must use members, not row indexes.
            if (count) rows.Reverse();
            return Rs(names, rows.ToArray());
        }));
        var result = await MeasureMarksTests.Finish(f.Engine, "measure:Sales/" + name);
        var contexts = Assert.Single(result.Marks, m => m.Name == "Contexts");
        Assert.True(contexts.State == (dropped ? "suspect" : "passed"), contexts.State + ": " + contexts.Sentence);
        if (dropped) Assert.Contains(contexts.Cells, c => c.Context.Contains("Seattle") && c.After == "BLANK");
        else Assert.Contains(contexts.Cells, c => c.Context.Contains("Arlington") && c.After == "BLANK");
        Assert.Equal("passed", result.Marks.Single(m => m.Name == "Filters").State);
        Assert.NotEmpty(counts);
        Assert.Equal(counts.Count, counts.Distinct().Count());
        Assert.Equal(5, counts.Count); // grand, cities, detail, single, boundary; no query per cell or additivity query
        Assert.All(counts, q => Assert.DoesNotContain("COUNTROWS('Customer')", q));
    }

    private static ResultSet Rs(string[] names, params object[][] rows) => MeasureMarksDoorTests.Fixture.Rs(names, rows);

    [Theory]
    [InlineData("SUM('Sales'[Quantity])", "Sales")]
    [InlineData("TOTALYTD([Total Quantity], 'Date'[Date])", "Sales")]
    [InlineData("IF(SELECTEDVALUE('Customer'[City]) = \"Arlington\", BLANK(), [Total Quantity])", "Sales")]
    [InlineData("AVERAGE('Product'[Unit Price])", "Product")]
    public async Task Row_counts_use_the_data_table_without_adding_its_related_dimensions(string formula, string source)
    {
        using var f = new DecisionFixture();
        await f.Engine.CreateModelAsync("Contoso source rows", 1604);
        await f.Sessions.Current.MutateAsync("human", "Contoso source tables", m => {
            var sales = m.AddTable("Sales");
            sales.AddDataColumn("Quantity", "Quantity", dataType: TabularEditor.TOMWrapper.DataType.Int64);
            foreach (var dimension in new[] { "Date", "Customer", "Product" }) {
                sales.AddDataColumn(dimension + "Key", dimension + "Key", dataType: TabularEditor.TOMWrapper.DataType.Int64);
                m.AddTable(dimension).AddDataColumn(dimension + "Key", dimension + "Key", dataType: TabularEditor.TOMWrapper.DataType.Int64);
            }
            m.Tables["Date"].AddDataColumn("Date", "Date", dataType: TabularEditor.TOMWrapper.DataType.DateTime);
            m.Tables["Customer"].AddDataColumn("City", "City");
            m.Tables["Product"].AddDataColumn("Unit Price", "Unit Price", dataType: TabularEditor.TOMWrapper.DataType.Decimal);
        });
        foreach (var dimension in new[] { "Date", "Customer", "Product" })
            await f.Engine.CreateRelationshipAsync("column:Sales/" + dimension + "Key", "column:" + dimension + "/" + dimension + "Key", null, true, "human");
        await f.Engine.CreateMeasureAsync("table:Sales", "Total Quantity", "SUM('Sales'[Quantity])", "human");
        await f.Engine.CreateMeasureAsync("table:Sales", "Contoso source", formula, "human");
        var snapshot = await f.Sessions.Current.ReadAsync(MeasureMarkSnapshot.Capture);
        Assert.Equal("COALESCE(COUNTROWS('" + source + "'), 0)", snapshot.FactRowsExpression("measure:Sales/Contoso source"));
    }

    [Theory]
    [InlineData("Contexts")]
    [InlineData("Filters")]
    public void Row_limits_describe_the_checked_contexts_without_a_failure(string name)
    {
        var probe = new ProbeResult { Status = "ok", Scenarios = new[] {
            new ScenarioEvidence { Name = "Contoso cities", Status = "ok", Truncated = true,
                Rows = new[] { new ProbeRow { V = 10, Members = new() { [City] = "Seattle" } },
                    new ProbeRow { V = 20, Members = new() { [City] = "Austin" } } } }
        } };
        var mark = name == "Contexts" ? MeasureMarkJudge.Contexts(probe) : MeasureMarkJudge.Filters(probe, null, null);
        Assert.Equal("passed", mark.State);
        Assert.Equal(name == "Contexts" ? "Values in all 2 places." : "No ignored filter in 2 places.", mark.Sentence);
        Assert.Equal(2, mark.Cells.Length);
        Assert.NotNull(mark.LastRan);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Missing_questions_and_unreachable_scoring_use_plain_not_scored_words(bool unreachable)
    {
        Task<DecisionOutcome> Ask(string point, IReadOnlyDictionary<string, object> state, IReadOnlyList<DecisionQuestion> questions) =>
            unreachable ? throw new System.Net.Http.HttpRequestException("Contoso fake connection failed")
                : Task.FromResult(DecisionOutcome.Answered(DecisionAnswers.NotDecided(
                    new DecisionRequest(point, 1, state, questions), "Unknown question id: " + point)));
        var totals = await new JevTotalsScorer((p, s, q, _) => Ask(p, s, q))
            .ScoreAsync(new("Total Quantity", "Adds quantities.", "SUM('Sales'[Quantity])"), Array.Empty<TotalProbe>(), new(), false, CancellationToken.None);
        Assert.Equal("not run", totals.State);
        Assert.StartsWith("Not scored yet. ", totals.Sentence);
        Plain(totals.Sentence + totals.Action);
        var input = new DescriptionMatchInput("Adds quantities.", "SUM('Sales'[Quantity])", "Total Quantity");
        var match = await new JevDescriptionMatchScorer((i, _) => Ask(DescriptionMatchQuestions.Point,
            DescriptionMatchQuestions.Snapshot(i), DescriptionMatchQuestions.Questions)).ScoreAsync(input, CancellationToken.None);
        Assert.Null(match.Score);
        Assert.Equal("not scored yet", match.Level);
        Assert.False(string.IsNullOrWhiteSpace(match.Reason));
        Plain(match.Label + " " + match.Reason);
        Assert.Equal("This score turns on when Jev scoring is released.", match.Reason);
    }

    private static void Plain(string text)
    {
        foreach (var word in new[] { "judge", "Check this measure again", "Unknown question", "relay", "decision", "engine" })
            Assert.DoesNotContain(word, text, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Description_scoring_never_exposes_a_missing_question_or_a_connection_error(bool unreachable)
    {
        var input = new DescriptionMatchInput("Adds quantities.", "SUM('Sales'[Quantity])", "Total Quantity");
        var scorer = new JevDescriptionMatchScorer((i, _) => unreachable
            ? throw new System.Net.Http.HttpRequestException("Contoso fake connection failed")
            : Task.FromResult(DecisionOutcome.Answered(DecisionAnswers.NotDecided(
                new DecisionRequest(DescriptionMatchQuestions.Point, 1, DescriptionMatchQuestions.Snapshot(i), DescriptionMatchQuestions.Questions),
                "Unknown question id: description.match"))));
        var match = await scorer.ScoreAsync(input, CancellationToken.None);
        Assert.Null(match.Score);
        Plain(match.Label + " " + match.Reason);
        Assert.Equal("This score turns on when Jev scoring is released.", match.Reason);
    }
}
