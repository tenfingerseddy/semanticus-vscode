using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.MeasureContexts;
using Xunit;
using Xunit.Abstractions;

namespace Semanticus.Tests;

[Collection("restore-root")]
public sealed class CheckedTimeContextTests(ITestOutputHelper output)
{
    private const string City = "'Customer'[City]";
    private const string Date = "'Date'[Date]";
    private const string Guarded = "IF([ShowValueForDates], CALCULATE([Sales Amount], DATESYTD('Date'[Date])))";
    private const string Ytd = "TOTALYTD([Sales Amount], 'Date'[Date])";
    private const string Prior = "CALCULATE([Sales Amount], SAMEPERIODLASTYEAR('Date'[Date]))";
    private const string Shifted = "CALCULATE([Sales Amount], DATEADD('Date'[Date], -1, YEAR))";

    private sealed class Cities : IMeasureContextPlanner
    {
        public MeasureContextPlan Plan(ContextPlanningRequest request) => new MeasureContextPlanner().Plan(request) with
        {
            Contexts = new ContextCandidate[] {
                new("cities", new("Customer", "City"), ContextShape.Subtotal, ContextRole.Related,
                    "Compare Contoso cities.", "Split sales by city.", 1, Array.Empty<string>(), Array.Empty<string>()),
                new("city-selection", new("Customer", "City"), ContextShape.Single, ContextRole.Related,
                    "Select Contoso cities.", "Split sales by city.", 2, Array.Empty<string>(), Array.Empty<string>()),
                new("total", null, ContextShape.GrandTotal, ContextRole.Total,
                    "Check the grand total.", "Add sales.", 3, Array.Empty<string>(), Array.Empty<string>())
            }
        };
    }

    private sealed class Contoso : IDisposable
    {
        internal readonly DecisionFixture F = new();
        internal readonly List<string> Queries = new();
        internal string DateRead = "ok";
        internal bool DropSeattle;
        internal bool WrongYtd;
        internal bool MeasureError;
        internal bool Plain;
        internal bool SparsePrior;
        internal bool NoPrior;
        internal bool Prior;
        // The calendar ends in 2027. Both 2024 and 2025 have sales, including the same day a year apart.
        private readonly DateTime[] calendar = { new(2024, 6, 30), new(2025, 6, 30), new(2027, 12, 31) };
        private readonly (DateTime Date, string City, decimal Amount)[] sales = {
            (new(2024, 6, 30), "Seattle", 10m), (new(2024, 6, 30), "Boston", 20m),
            (new(2025, 6, 30), "Seattle", 30m), (new(2025, 6, 30), "Boston", 50m)
        };

        internal async Task Open(string formula)
        {
            Prior = formula.Contains("SAMEPERIODLASTYEAR") || formula.Contains("DATEADD");
            await F.Engine.CreateModelAsync("Contoso", 1604);
            await F.Sessions.Current.MutateAsync("human", "Contoso time fixture", m => {
                var fact = m.AddTable("Sales");
                fact.AddDataColumn("Amount", "Amount", dataType: TabularEditor.TOMWrapper.DataType.Decimal);
                fact.AddDataColumn("Order Date", "Order Date", dataType: TabularEditor.TOMWrapper.DataType.DateTime);
                fact.AddDataColumn("CustomerKey", "CustomerKey", dataType: TabularEditor.TOMWrapper.DataType.Int64);
                var date = m.AddTable("Date");
                date.DataCategory = "Time";
                date.AddDataColumn("Date", "Date", dataType: TabularEditor.TOMWrapper.DataType.DateTime).IsKey = true;
                // This column runs past the sales. Using it for YTD gives a blank inside the real data date.
                date.AddDataColumn("Ship Date", "Ship Date", dataType: TabularEditor.TOMWrapper.DataType.DateTime);
                date.AddDataColumn("Year", "Year", dataType: TabularEditor.TOMWrapper.DataType.Int64);
                var customer = m.AddTable("Customer");
                customer.AddDataColumn("CustomerKey", "CustomerKey", dataType: TabularEditor.TOMWrapper.DataType.Int64);
                customer.AddDataColumn("City", "City");
            });
            await F.Engine.CreateRelationshipAsync("column:Sales/Order Date", "column:Date/Date", null, true, "human");
            await F.Engine.CreateRelationshipAsync("column:Sales/CustomerKey", "column:Customer/CustomerKey", null, true, "human");
            await F.Engine.CreateMeasureAsync("table:Sales", "Sales Amount", "SUM('Sales'[Amount])", "human");
            await F.Engine.CreateMeasureAsync("table:Sales", "ShowValueForDates",
                "VAR LastDateWithData = CALCULATE(MAX(Sales[Order Date]), REMOVEFILTERS()) " +
                "VAR FirstDateVisible = MIN('Date'[Date]) RETURN FirstDateVisible <= LastDateWithData", "human");
            await F.Engine.CreateMeasureAsync("table:Sales", "Sales YTD", formula, "human");
            F.Sessions.Current.LiveOrigin = new LiveOrigin("contoso", "Contoso", null);
            F.Engine.MeasureContextPlannerForTests = new Cities();
            F.Engine.SetLiveConnectionForTest(LiveConnection.ForTest("xmla", "contoso", "Contoso", Query));
        }

        private ResultSet Query(string query)
        {
            Queries.Add(query);
            if (query.Contains("OrphanRows")) return Rs(RelationshipProbes.CombinedColumns,
                new object[] { 0L, 0L, 0L, 0L, 4L, 3L });
            var outer = query[query.LastIndexOf("EVALUATE", StringComparison.Ordinal)..];
            if (outer.StartsWith("EVALUATE TOPN(1,", StringComparison.Ordinal))
            {
                Assert.Contains("CALCULATE(COALESCE(COUNTROWS('Sales'), 0))", outer);
                Assert.Contains("ALL(" + Date + ")", outer);
                Assert.DoesNotContain("[Sales YTD]", query);
                Assert.DoesNotContain("[ShowValueForDates]", query);
                Assert.DoesNotContain("'Date'[Ship Date]", outer);
                if (DateRead == "error") return new ResultSet { Error = "Contoso date read failed" };
                var dates = calendar.Where(d => sales.Any(s => s.Date == d)).OrderByDescending(d => d).Take(1);
                return Rs(new[] { "Date" }, DateRead == "empty" ? Array.Empty<object[]>() : dates.Select(d => new object[] { d }).ToArray());
            }
            if (outer.StartsWith("EVALUATE TOPN(50", StringComparison.Ordinal))
                return Rs(new[] { "City" }, new object[] { "Seattle" }, new object[] { "Arlington" }, new object[] { "Boston" });
            if (MeasureError && query.Contains("[Sales YTD]")) return new ResultSet { Error = "Contoso measure failed" };
            var count = query.Contains("COALESCE(COUNTROWS('Sales'), 0)");
            var scoped = outer.Contains("TREATAS") && outer.Contains(Date);
            var grouped = outer.Contains(City + ",");
            var names = grouped ? new[] { "City", "v", "__present" } : new[] { "v", "__present" };
            if (outer.Contains("FALSE ()")) return Rs(names);
            var cities = new[] { "Seattle", "Arlington", "Boston" }.Where(c =>
                !outer.Contains("}, " + City + " )") || outer.Contains("\"" + c + "\"")).ToArray();
            object Value(string[] members)
            {
                var wholeYear = outer.Contains("DATE ( 2025, 1, 1 )") && outer.Contains("DATE ( 2025, 12, 31 )");
                var previousYear = outer.Contains("DATE ( 2024, 1, 1 )");
                var year = previousYear || !count && Prior && scoped ? 2024 : 2025;
                var rows = sales.Where(s => members.Contains(s.City) && (!scoped || s.Date.Year == year))
                    .Where(s => !NoPrior || s.Date.Year != 2024)
                    .Where(s => !SparsePrior || !scoped || wholeYear || previousYear || s.Date.Year != 2024).ToArray();
                if (count) return rows.Length;
                if (rows.Length == 0 || WrongYtd || !Plain && !scoped
                    || DropSeattle && members.Length == 1 && members[0] == "Seattle") return null;
                return rows.Sum(s => s.Amount);
            }
            return grouped ? Rs(names, cities.Select(c => new object[] { c, Value(new[] { c }), 1 }).Reverse().ToArray())
                : Rs(names, new object[] { Value(cities), 1 });
        }

        internal async Task<MeasureMark> Mark() => Assert.Single(
            (await MeasureMarksTests.Finish(F.Engine, "measure:Sales/Sales YTD")).Marks, m => m.Name == "Contexts");
        internal void CheckedDate(MeasureMark mark)
        {
            // fx30e F2: the spec form "Checked inside <year>.", never a date in another format than the measure's.
            Assert.StartsWith("Checked inside 2025. ", mark.Sentence);
            Assert.DoesNotContain("Jan", mark.Sentence);
            var probes = Queries.Where(q => q.Contains("__present") && q.Contains("[Sales YTD]")).ToArray();
            Assert.NotEmpty(probes);
            Assert.All(probes, q => {
                var outer = q[q.LastIndexOf("EVALUATE", StringComparison.Ordinal)..];
                Assert.Contains("TREATAS", outer);
                Assert.Contains(Date, outer);
                Assert.Contains("DATE ( 2025, 1, 1 )", outer);
                Assert.Contains("DATE ( 2025, 12, 31 )", outer);
                Assert.DoesNotContain("2027", outer);
            });
            Assert.Single(Queries.Where(q => q.Contains("EVALUATE TOPN(1,")));
        }
        public void Dispose() => F.Dispose();
    }

    [Theory]
    [InlineData(Prior, false)]
    [InlineData(Shifted, false)]
    [InlineData(Prior, true)]
    [InlineData(Shifted, true)]
    public async Task Previous_year_checks_use_the_whole_year_and_its_source_rows(string formula, bool noPrior)
    {
        using var f = new Contoso { SparsePrior = true, NoPrior = noPrior };
        await f.Open(formula);
        var mark = await f.Mark();
        Assert.Equal("passed", mark.State);
        f.CheckedDate(mark);
        if (noPrior) {
            Assert.DoesNotContain("blank even though", mark.Sentence);
            Assert.Contains(f.Queries, q => q.Contains("COALESCE(COUNTROWS('Sales'), 0)") && q.Contains("DATE ( 2024, 1, 1 )"));
        }
    }

    private static ResultSet Rs(string[] names, params object[][] rows) => MeasureMarksDoorTests.Fixture.Rs(names, rows);

    [Theory]
    [InlineData(Guarded)]
    [InlineData(Ytd)]
    [InlineData(Prior)]
    [InlineData(Shifted)]
    public async Task Time_blanks_without_a_date_are_checked_inside_a_date_with_sales(string formula)
    {
        using var f = new Contoso(); await f.Open(formula);
        var unselected = await f.F.Engine.ProbeMeasureAsync("[Sales YTD]", null,
            new[] { new ProbeScenario { Name = "no date selected" } }, false, 64);
        Assert.True(Assert.Single(Assert.Single(unselected.Scenarios).Rows).Blank);
        f.Queries.Clear();
        var mark = await f.Mark();
        Assert.Equal("passed", mark.State);
        Assert.Contains(mark.Cells, c => c.Context.Contains("Arlington") && c.After == "BLANK");
        f.CheckedDate(mark);
    }

    [Theory]
    [InlineData(Guarded)]
    [InlineData(Ytd)]
    [InlineData(Prior)]
    [InlineData(Shifted)]
    public async Task Blank_with_rows_inside_the_data_date_stays_suspect(string formula)
    {
        using var f = new Contoso { DropSeattle = true };
        await f.Open("IF(SELECTEDVALUE('Customer'[City]) = \"Seattle\", BLANK(), " + formula + ")");
        var mark = await f.Mark();
        Assert.Equal("suspect", mark.State);
        Assert.Contains("rows to calculate", mark.Sentence);
        Assert.Contains(mark.Cells, c => c.Context.Contains("Seattle") && c.After == "BLANK");
        f.CheckedDate(mark);
        Assert.All(f.Queries.Where(q => q.Contains("__present") && q.Contains("COALESCE(COUNTROWS('Sales'), 0)")),
            q => Assert.Contains(formula.Contains("SAMEPERIODLASTYEAR") || formula.Contains("DATEADD")
                ? "DATE ( 2024, 1, 1 )" : "DATE ( 2025, 1, 1 )", q));
    }

    [Fact]
    public async Task Wrong_ytd_on_another_date_column_stays_suspect_inside_the_data_date()
    {
        using var f = new Contoso { WrongYtd = true };
        await f.Open("CALCULATE([Sales Amount], DATESYTD('Date'[Ship Date]))");
        var mark = await f.Mark();
        Assert.Equal("suspect", mark.State);
        Assert.Contains("rows to calculate", mark.Sentence);
        f.CheckedDate(mark);
    }

    [Theory]
    [InlineData("empty", false)]
    [InlineData("error", false)]
    [InlineData("error", true)]
    public async Task Missing_data_date_does_not_judge_unselected_blanks_but_keeps_measure_errors(string read, bool error)
    {
        using var f = new Contoso { DateRead = read, MeasureError = error };
        await f.Open(Guarded);
        var mark = await f.Mark();
        Assert.Equal(error ? "suspect" : "not run", mark.State);
        Assert.DoesNotContain("blank even though", mark.Sentence);
        if (error) Assert.Contains(mark.Cells, c => c.After == "ERROR: Contoso measure failed");
        else Assert.Contains("date with source rows", mark.Sentence);
    }

    [Fact]
    public async Task Plain_sales_keeps_the_no_sales_rule_without_a_date_lookup()
    {
        using var f = new Contoso { Plain = true }; await f.Open("[Sales Amount]");
        var mark = await f.Mark();
        Assert.Equal("passed", mark.State);
        Assert.DoesNotContain("Checked inside", mark.Sentence);
        Assert.DoesNotContain(f.Queries, q => q.Contains("EVALUATE TOPN(1,"));
    }

    [ContosoContextEvidence]
    public async Task Offline_Contoso_lists_measures_that_receive_a_data_date()
    {
        var path = Environment.GetEnvironmentVariable("SEMANTICUS_CONTEXT_BIM")!;
        Assert.True(File.Exists(path));
        using var f = new DecisionFixture(); await f.Engine.OpenAsync(path);
        var model = await f.Sessions.Current.ReadAsync(ContextModelCapture.Capture);
        var touched = new List<string>();
        foreach (var measure in model.Measures)
        {
            var plan = new MeasureContextPlanner().Plan(new(model, measure));
            if (!plan.UsesTimeLogic) continue;
            var date = plan.DataDateColumn;
            Assert.NotNull(date);
            touched.Add(measure.Name);
            output.WriteLine(measure.Table + "[" + measure.Name + "]: " + date!.Dax);
        }
        Assert.Contains("Sales YTD", touched);
        Assert.DoesNotContain("Total Quantity", touched);
        Assert.DoesNotContain("Total Cost", touched);
        output.WriteLine("Date-scoped measures: " + touched.Count + " of " + model.Measures.Length);
    }
}
