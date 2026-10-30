using System;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.MeasureContexts;
using Xunit;

namespace Semanticus.Tests;

[Collection("restore-root")]
public sealed class CheckedFx12Tests
{
    private const string DateNewCustomer = "CALCULATE ( MIN ( Sales[Order Date] ), ALLEXCEPT ( Sales, Sales[CustomerKey], Customer ) )";

    private static ContextModel Model(string formula)
    {
        var order = new ContextColumn("Sales", "Order Date", Time: true) { DateTyped = true };
        var date = new ContextColumn("Date", "Date", Time: true) { DateTyped = true };
        var fk = new ContextColumn("Sales", "CustomerKey", Numeric: true, Key: true);
        var pk = new ContextColumn("Customer", "CustomerKey", Numeric: true, Key: true);
        return new(new[] { order, date, fk, pk, new ContextColumn("Date", "Year", Time: true),
                new ContextColumn("Customer", "City"), new ContextColumn("Sales", "Amount", Numeric: true) },
            new[] { new ContextRelationship(order, date), new ContextRelationship(fk, pk) },
            new[] { new ContextMeasure("Sales", "Date New Customer", formula) }, new[] { "Date" });
    }

    private static ProbeResult Values(object total, object first, object second, string column = "'Date'[Date]") => new() {
        Status = "ok", Scenarios = new[] {
            new ScenarioEvidence { Name = "grand total", Status = "ok", Rows = new[] { new ProbeRow { V = total } } },
            new ScenarioEvidence { Name = "single-select " + column, Status = "ok", Rows = new[] {
                new ProbeRow { V = first, Members = new() { [column] = "Contoso first" } },
                new ProbeRow { V = second, Members = new() { [column] = "Contoso second" } }
            } }
        }
    };

    private sealed class Dates : IMeasureContextPlanner
    {
        public MeasureContextPlan Plan(ContextPlanningRequest request)
        {
            var plan = new MeasureContextPlanner().Plan(request);
            return plan with { Contexts = plan.Contexts.Where(c => c.Shape == ContextShape.GrandTotal
                || c.Column?.Table == "Date" && c.Column.Name == "Date" && c.Shape == ContextShape.Single).ToArray() };
        }
    }

    [Theory]
    [InlineData("", "info")]
    // 1.7 item 2 (L2): Description words no longer pass the clear; the mark reads information until the person says on purpose.
    [InlineData("This measure ignores Date filters on purpose.", "info")]
    [InlineData("Absolute scope: each customer's first order date ever, ignoring Date and Product filters.", "info")]
    public async Task Production_Date_New_Customer_keeps_its_first_date_without_a_warning(string description, string state)
    {
        using var f = new DecisionFixture();
        await f.Engine.CreateModelAsync("Contoso dates", 1604);
        await f.Sessions.Current.MutateAsync("human", "Contoso first purchase", m => {
            var sales = m.AddTable("Sales");
            sales.AddDataColumn("Order Date", "Order Date", dataType: TabularEditor.TOMWrapper.DataType.DateTime);
            sales.AddDataColumn("CustomerKey", "CustomerKey", dataType: TabularEditor.TOMWrapper.DataType.Int64);
            sales.AddDataColumn("ProductKey", "ProductKey", dataType: TabularEditor.TOMWrapper.DataType.Int64);
            var customer = m.AddTable("Customer");
            customer.AddDataColumn("CustomerKey", "CustomerKey", dataType: TabularEditor.TOMWrapper.DataType.Int64);
            customer.AddDataColumn("City", "City");
            var product = m.AddTable("Product");
            product.AddDataColumn("ProductKey", "ProductKey", dataType: TabularEditor.TOMWrapper.DataType.Int64);
            product.AddDataColumn("Category", "Category");
            var date = m.AddTable("Date");
            date.DataCategory = "Time";
            date.AddDataColumn("Date", "Date", dataType: TabularEditor.TOMWrapper.DataType.DateTime).IsKey = true;
        });
        await f.Engine.CreateRelationshipAsync("column:Sales/Order Date", "column:Date/Date", null, true, "human");
        await f.Engine.CreateRelationshipAsync("column:Sales/CustomerKey", "column:Customer/CustomerKey", null, true, "human");
        await f.Engine.CreateRelationshipAsync("column:Sales/ProductKey", "column:Product/ProductKey", null, true, "human");
        await f.Sessions.Current.MutateAsync("human", "Contoso date measure", m =>
            m.Tables["Sales"].AddMeasure("Date New Customer", DateNewCustomer).Description = description);
        f.Sessions.Current.LiveOrigin = new LiveOrigin("contoso", "Contoso dates", null);
        f.Engine.MeasureContextPlannerForTests = new Dates();
        f.Engine.SetLiveConnectionForTest(LiveConnection.ForTest("xmla", "contoso", "Contoso dates", q => {
            var first = new DateTime(2016, 9, 28);
            if (q.Contains("OrphanRows")) return MeasureMarksDoorTests.Fixture.Rs(RelationshipProbes.CombinedColumns,
                new object[] { 0L, 0L, 0L, 0L, 3L, 2L });
            var outer = q[q.LastIndexOf("EVALUATE", StringComparison.Ordinal)..];
            if (outer.StartsWith("EVALUATE TOPN(1,")) return MeasureMarksDoorTests.Fixture.Rs(new[] { "Date" }, new object[] { first });
            if (outer.StartsWith("EVALUATE TOPN(50,")) return MeasureMarksDoorTests.Fixture.Rs(new[] { "Date" },
                new object[] { first }, new object[] { new DateTime(2016, 10, 28) });
            if (outer.Contains("FALSE ()")) return MeasureMarksDoorTests.Fixture.Rs(new[] { "v", "__present" });
            var anonymous = q.IndexOf("MEASURE 'Sales'[__smx_", StringComparison.Ordinal);
            var body = anonymous < 0 ? q : q[anonymous..];
            var counts = body.Contains("COUNTROWS('Sales')");
            var clears = body.Contains("ALLEXCEPT");
            var firstFilter = outer.IndexOf("TREATAS", StringComparison.Ordinal);
            var secondFilter = firstFilter < 0 ? -1 : outer.IndexOf("TREATAS", firstFilter + 1, StringComparison.Ordinal);
            var firstMember = secondFilter < 0 || outer[firstFilter..secondFilter].Contains("2016, 9, 28");
            var count = secondFilter < 0 || clears ? 3 : firstMember ? 1 : 2;
            object value = counts ? count : first;
            return outer.Contains("SUMMARIZECOLUMNS(") && outer.Contains("'Date'[Date],")
                ? MeasureMarksDoorTests.Fixture.Rs(new[] { "Date", "v", "__present" },
                    new object[] { firstMember ? first : new DateTime(2016, 10, 28), value, 1 })
                : MeasureMarksDoorTests.Fixture.Rs(new[] { "v", "__present" }, new object[] { value, 1 });
        }));
        var filters = (await MeasureMarksTests.Finish(f.Engine, "measure:Sales/Date New Customer")).Marks.Single(m => m.Name == "Filters");
        Assert.Equal(state, filters.State);
        Assert.Contains("Date", filters.Sentence);
        // FX16: this planner probes Date only, so it cannot claim that Product repeats.
        Assert.DoesNotContain("Product", filters.Sentence);
        Assert.DoesNotContain("No ignored filter", filters.Sentence);
        if (state == "passed") Assert.Contains("on purpose", filters.Sentence);
        Assert.Contains(filters.Cells, c => c.After == "28 Sep 2016");
    }

    [Fact]
    public void ALLEXCEPT_keeps_every_column_in_a_named_table()
    {
        var model = Model(DateNewCustomer);
        var plan = new MeasureContextPlanner().Plan(new(model, model.Measures[0]));
        var customer = plan.Contexts.Where(c => c.Column?.Table == "Customer").ToArray();
        Assert.NotEmpty(customer);
        Assert.All(customer, c => {
            Assert.Contains("keep this column's slicer", c.ExpectedBehaviour);
            Assert.False(plan.ExpectsConstant(c));
            Assert.False(plan.ClearsFilter(c));
        });
    }

    [Fact]
    public void Date_New_Customer_clears_dates_but_keeps_the_Customer_table()
    {
        var model = Model(DateNewCustomer);
        var plan = new MeasureContextPlanner().Plan(new(model, model.Measures[0]));
        var date = plan.Contexts.First(c => c.Column?.Table == "Date" && c.Column.Name == "Date");
        Assert.Contains("clear this column's slicer", date.ExpectedBehaviour);
        var value = new DateTime(2016, 9, 28);
        var mark = MeasureMarkJudge.Filters(Values(value, value, value), Values(3, 1, 2), null,
            plan.ExpectsConstant(date) || plan.ClearsFilter(date), Values(3, 3, 3));
        Assert.Equal("passed", mark.State);
        Assert.All(plan.Contexts.Where(c => c.Column?.Table == "Customer"), c => {
            Assert.Contains("keep this column's slicer", c.ExpectedBehaviour);
            Assert.False(plan.ExpectsConstant(c));
            Assert.False(plan.ClearsFilter(c));
            Assert.Equal("suspect", MeasureMarkJudge.Filters(Values(3, 3, 3, c.Column.Dax),
                Values(3, 1, 2, c.Column.Dax), null, plan.ExpectsConstant(c) || plan.ClearsFilter(c),
                Values(3, 3, 3, c.Column.Dax)).State);
        });
    }

    [Theory]
    [InlineData("ALL('Date')")]
    [InlineData("REMOVEFILTERS('Date')")]
    [InlineData("ALLSELECTED('Date')")]
    [InlineData("ALL('Date'[Date])")]
    [InlineData("REMOVEFILTERS('Date'[Date])")]
    [InlineData("ALLSELECTED('Date'[Date])")]
    [InlineData("ALL()")]
    [InlineData("REMOVEFILTERS()")]
    [InlineData("ALLSELECTED()")]
    public void A_clear_exempts_only_its_column_from_an_ignored_filter_finding(string modifier)
    {
        var model = Model("CALCULATE(MIN(Sales[Order Date]), " + modifier + ")");
        var plan = new MeasureContextPlanner().Plan(new(model, model.Measures[0]));
        var candidate = plan.Contexts.First(c => c.Column?.Table == "Date" && c.Column.Name == "Date");
        Assert.True(plan.ClearsFilter(candidate));
        Assert.Equal("passed", MeasureMarkJudge.Filters(Values(3, 3, 3), Values(3, 1, 2), null,
            plan.ExpectsConstant(candidate) || plan.ClearsFilter(candidate), Values(3, 3, 3)).State);
        if (modifier.Contains("'Date'"))
            Assert.All(plan.Contexts.Where(c => c.Column?.Table == "Customer"), c => Assert.False(plan.ExpectsConstant(c)));
    }

    [Theory]
    [InlineData("CALCULATE(SUM('Sales'[Amount]), ALL('Product'))")]
    [InlineData("CALCULATE(SUM('Sales'[Amount]), REMOVEFILTERS('Product'))")]
    [InlineData("CALCULATE(SUM('Sales'[Amount]), ALLSELECTED('Product'))")]
    [InlineData("DIVIDE(SUM('Sales'[Amount]), CALCULATE(SUM('Sales'[Amount]), ALL('Product')))")]
    [InlineData("DIVIDE(SUM('Sales'[Amount]), CALCULATE(SUM('Sales'[Amount]), REMOVEFILTERS('Product')))")]
    [InlineData("DIVIDE(SUM('Sales'[Amount]), CALCULATE(SUM('Sales'[Amount]), ALLSELECTED('Product')))")]
    public async Task Production_marks_do_not_accuse_explicit_clears(string expression)
    {
        using var root = new DecisionFixture();
        using var f = new MeasureMarksDoorTests.Fixture(root.Workspace);
        await f.Open();
        await f.Sessions.Current.MutateAsync("human", "Contoso clear", m =>
            m.Tables["Sales"].Measures["Total Sales"].Expression = expression);
        f.Engine.SetLiveConnectionForTest(LiveConnection.ForTest("xmla", "contoso", "Contoso Sales", q =>
            f.Query(q.Replace("REMOVEFILTERS('Product')", "ALL('Product')")
                .Replace("ALLSELECTED('Product')", "ALL('Product')"))));
        var filters = (await MeasureMarksTests.Finish(f.Engine, "measure:Sales/Total Sales")).Marks.Single(m => m.Name == "Filters");
        // FX16: the canned ratio repeats, but its unavailable comparison must remain not run.
        Assert.Equal(expression.StartsWith("DIVIDE", StringComparison.Ordinal) ? "not run" : "info", filters.State);
        if (filters.State == "info") Assert.Contains("Product", filters.Sentence);
        Assert.DoesNotContain("No ignored filter", filters.Sentence);
    }

    [Theory]
    [InlineData("{0}")]
    [InlineData("IF(1 = 1, {0})")]
    [InlineData("SWITCH(TRUE(), 1 = 1, {0}, BLANK())")]
    [InlineData("VAR Scale = 100 RETURN {0}")]
    [InlineData("DIVIDE({0}, 100)")]
    public async Task A_SUM_with_lost_relationship_rows_still_reads_suspect_without_a_clear(string wrapper)
    {
        using var root = new DecisionFixture();
        using var f = new MeasureMarksDoorTests.Fixture(root.Workspace);
        await f.Open();
        var expression = string.Format(wrapper, "SUM('Sales'[Amount])");
        await f.Sessions.Current.MutateAsync("human", "Contoso lost rows", m =>
            m.Tables["Sales"].Measures["Total Sales"].Expression = expression);
        f.Orphans = 4;
        var snapshot = await f.Sessions.Current.ReadAsync(MeasureMarkSnapshot.Capture);
        Assert.Equal(expression, snapshot.Formulas["measure:Sales/Total Sales"].Body);
        var filters = (await MeasureMarksTests.Finish(f.Engine, "measure:Sales/Total Sales")).Marks.Single(m => m.Name == "Filters");
        Assert.Equal("suspect", filters.State);
        Assert.Contains("no related key", filters.Sentence);
    }

    [Theory]
    [InlineData("Contexts")]
    [InlineData("Filters")]
    [InlineData("SQL")]
    [InlineData("Trusted number")]
    [InlineData("Equivalence")]
    public void Every_mark_formats_date_evidence_in_plain_words(string name)
    {
        const string stamp = "2016-09-28T00:00:00.0000000";
        var date = new DateTime(2016, 9, 28);
        var probe = Values(date, date, date);
        foreach (var row in probe.Scenarios[1].Rows) row.Members["'Date'[Date]"] = date;
        var mark = name switch {
            "Contexts" => MeasureMarkJudge.Contexts(probe),
            "Filters" => MeasureMarkJudge.Filters(probe, Values(3, 1, 2), null, true),
            "Equivalence" => MeasureMarkJudge.Equivalence(new BaselineDiff { Verdict = "moved", Mismatches = new[] {
                new EquivalenceMismatch { Context = "Date: " + stamp, ValueA = stamp, ValueB = "2017-09-28T00:00:00.0000000" }
            } }),
            _ => MeasureMarkJudge.Saved(name, new[] { new ReconcileOutcome { Verdict = Verdict.Pass,
                Filter = "Date: " + stamp, Expected = stamp, Actual = stamp } })
        };
        Assert.Contains(mark.Cells, c => c.After == "28 Sep 2016" || c.After == "28 Sep 2017");
        Assert.All(mark.Cells, c => {
            Assert.DoesNotContain("T00:00:00", c.After ?? "");
            Assert.DoesNotContain("T00:00:00", c.Before ?? "");
            Assert.DoesNotContain("T00:00:00", c.Context ?? "");
        });
    }

    [Theory]
    [InlineData("IF(1 = 1, CALCULATE(SUM('Sales'[Amount]), ALL('Product')), SUM('Sales'[Amount]))", "different sets of rows")]
    [InlineData("VAR LastDateWithData = CALCULATE(MAX('Sales'[Order Date]), REMOVEFILTERS()) VAR FirstDateVisible = MIN('Date'[Date]) RETURN FirstDateVisible <= LastDateWithData", "which dates to show")]
    public async Task An_unavailable_row_comparison_explains_the_reason_and_next_step(string formula, string reason)
    {
        using var root = new DecisionFixture();
        using var f = new MeasureMarksDoorTests.Fixture(root.Workspace);
        await f.Open(configure: m => {
            m.Tables["Sales"].AddDataColumn("Order Date", "Order Date", dataType: TabularEditor.TOMWrapper.DataType.DateTime);
            m.AddTable("Date").AddDataColumn("Date", "Date", dataType: TabularEditor.TOMWrapper.DataType.DateTime);
        });
        await f.Sessions.Current.MutateAsync("human", "Contoso unsupported comparison", m =>
            m.Tables["Sales"].Measures["Total Sales"].Expression = formula);
        f.Engine.SetLiveConnectionForTest(LiveConnection.ForTest("xmla", "contoso", "Contoso Sales", q =>
            f.Query(q.Replace("REMOVEFILTERS()", "ALL('Product')"))));
        var filters = (await MeasureMarksTests.Finish(f.Engine, "measure:Sales/Total Sales")).Marks.Single(m => m.Name == "Filters");
        var snapshot = await f.Sessions.Current.ReadAsync(MeasureMarkSnapshot.Capture);
        var unavailable = snapshot.FilterRowsUnavailableReason("measure:Sales/Total Sales", formula.Contains("'Date'"));
        Assert.Contains(reason, unavailable);
        Assert.Contains("Add a number you trust", unavailable);
        Assert.Equal("not run", filters.State);
        Assert.Contains(reason, filters.Sentence);
        Assert.Contains("Add a number you trust", filters.Sentence);
    }
}
