using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests;

[Collection("restore-root")]
public sealed class CheckedFx8Tests
{
    private static ProbeResult Cities(bool counts = false, bool truncated = false) => new() {
        Status = "ok", Scenarios = new[] {
            new ScenarioEvidence { Name = "grand total", Status = "ok", Rows = new[] { new ProbeRow { V = counts ? 740 : 3 } } },
            new ScenarioEvidence { Name = "multi-select 'Customer'[City]", Status = "ok", Truncated = truncated,
                Rows = new[] { ("Canning Creek", 15), ("Mallanganee", 185), ("Yelta", 540) }.Select(c =>
                    new ProbeRow { V = counts ? c.Item2 : 1, Members = new() { ["'Customer'[City]"] = c.Item1 } }).ToArray() }
        }
    };

    [Fact]
    public void Distinct_customers_in_three_cities_pass_when_the_fact_rows_change()
    {
        var facts = Cities(counts: true);
        Array.Reverse(facts.Scenarios[1].Rows);
        var mark = MeasureMarkJudge.Filters(Cities(), facts, null);
        Assert.Equal("passed", mark.State);
        Assert.DoesNotContain("may be ignored", mark.Sentence);
    }

    [Fact]
    public void A_selected_distinct_count_can_equal_the_total_while_its_source_rows_change()
    {
        var probe = Cities();
        probe.Scenarios[0].Rows[0].V = 1;
        probe.Scenarios[1].Name = "single-select 'Customer'[City]=Canning Creek";
        probe.Scenarios[1].Rows = probe.Scenarios[1].Rows.Take(1).ToArray();
        var facts = Cities(true);
        facts.Scenarios[1].Name = probe.Scenarios[1].Name;
        facts.Scenarios[1].Rows = facts.Scenarios[1].Rows.Take(1).ToArray();
        Assert.Equal("passed", MeasureMarkJudge.Filters(probe, facts, null).State);
    }

    [Theory]
    [InlineData("DISTINCTCOUNT(Sales[CustomerKey])", "COALESCE(COUNTROWS('Sales'), 0)")]
    [InlineData("AVERAGE(Sales[Quantity])", "COALESCE(COUNTROWS('Sales'), 0)")]
    [InlineData("CALCULATE(SUM(Sales[Quantity]), ALL('Customer'))", "CALCULATE(COALESCE(COUNTROWS('Sales'), 0), ALL('Customer'))")]
    [InlineData("SUMX(FILTER(Sales, Sales[Quantity] > 0), Sales[Quantity])", "COALESCE(COUNTROWS(FILTER(Sales, Sales[Quantity] > 0)), 0)")]
    public async Task Filter_witness_counts_rows_and_preserves_the_formulas_filters(string formula, string expected)
    {
        using var f = new DecisionFixture();
        await f.Engine.CreateModelAsync("Contoso rows", 1604);
        await f.Sessions.Current.MutateAsync("human", "Contoso row fixture", m => {
            var sales = m.AddTable("Sales");
            sales.AddDataColumn("Quantity", "Quantity", dataType: TabularEditor.TOMWrapper.DataType.Int64);
            sales.AddDataColumn("CustomerKey", "CustomerKey", dataType: TabularEditor.TOMWrapper.DataType.Int64);
            m.AddTable("Customer").AddDataColumn("City", "City");
            sales.AddMeasure("Contoso rows", formula);
        });
        var snapshot = await f.Sessions.Current.ReadAsync(MeasureMarkSnapshot.Capture);
        Assert.Equal(expected, snapshot.FilterRowsExpression("measure:Sales/Contoso rows"));
        Assert.Equal(formula.Contains("ALL('Customer')") ? "COALESCE(COUNTROWS('Sales'), 0)" : expected,
            snapshot.FilterRowsExpression("measure:Sales/Contoso rows", preserveFilterChanges: false));
    }

    [Theory]
    [InlineData("Filters", "No ignored filter in 4 places.")]
    [InlineData("Contexts", "Values in all 4 places.")]
    public void A_limited_check_reports_its_finding_as_a_pass(string name, string finding)
    {
        var probe = Cities(truncated: true);
        var mark = name == "Filters" ? MeasureMarkJudge.Filters(probe, Cities(true), null) : MeasureMarkJudge.Contexts(probe);
        Assert.Equal("passed", mark.State);
        // fx30e F6: "More places are available." is cut (spec section 5, cut 12).
        Assert.Equal(finding, mark.Sentence);
    }

    [Fact]
    public void Mark_places_use_plain_column_names()
    {
        var mark = MeasureMarkJudge.Contexts(Cities());
        Assert.Equal("Grand total", mark.Cells[0].Context);
        Assert.Equal("City: Canning Creek", mark.Cells[1].Context);
        var edit = MeasureMarkJudge.Equivalence(new BaselineDiff { Verdict = "moved", Mismatches = new[] {
            new EquivalenceMismatch { Context = "[__mark_context]=Grand total", ValueA = "1", ValueB = "2" }
        } });
        Assert.Equal("Grand total", Assert.Single(edit.Cells).Context);
    }

    [Fact]
    public async Task Undo_restores_clean_equivalence_and_redo_restores_the_changed_formula()
    {
        using var root = new DecisionFixture();
        using var f = new MeasureMarksDoorTests.Fixture(root.Workspace);
        await f.Open();
        const string reference = "measure:Sales/Total Sales";
        await MeasureMarksTests.Finish(f.Engine, reference);
        await f.Sessions.Current.MutateAsync("human", "Contoso change", m => m.Tables["Sales"].Measures["Total Sales"].Expression += " + 50");
        Assert.Equal("suspect", (await MeasureMarksTests.Finish(f.Engine, reference)).Marks.Single(m => m.Name == "Equivalence").State);
        var plan = await f.Engine.PlanMeasureRunAsync();
        await f.Engine.RunMeasurePlanAsync(plan.Id);
        Assert.Equal("suspect", Assert.Single(await f.Engine.GetMeasureCheckCacheAsync()).Marks.Single(m => m.Name == "Equivalence").State);
        await f.Sessions.Current.UndoAsync("human");
        var restored = (await MeasureMarksTests.Finish(f.Engine, reference)).Marks.Single(m => m.Name == "Equivalence");
        Assert.NotEqual("suspect", restored.State);
        Assert.Empty(restored.Cells);
        Assert.DoesNotContain("moved", restored.Sentence, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual("suspect", Assert.Single(await f.Engine.GetMeasureCheckCacheAsync()).Marks.Single(m => m.Name == "Equivalence").State);
        await f.Sessions.Current.RedoAsync("human");
        Assert.Equal("suspect", (await MeasureMarksTests.Finish(f.Engine, reference)).Marks.Single(m => m.Name == "Equivalence").State);
    }

    [Fact]
    public async Task Undo_while_comparisons_are_queued_keeps_the_restored_formulas_own_comparison()
    {
        using var root = new DecisionFixture();
        using var f = new MeasureMarksDoorTests.Fixture(root.Workspace);
        await f.Open();
        const string reference = "measure:Sales/Total Sales";
        await MeasureMarksTests.Finish(f.Engine, reference);
        f.Release.Reset();
        await f.Sessions.Current.MutateAsync("human", "Contoso first", m => m.Tables["Sales"].Measures["Total Sales"].Expression += " + 50");
        await f.Sessions.Current.MutateAsync("human", "Contoso second", m => m.Tables["Sales"].Measures["Total Sales"].Expression += " + 50");
        await f.Sessions.Current.UndoAsync("human");
        f.Release.Set();
        var restored = (await MeasureMarksTests.Finish(f.Engine, reference)).Marks.Single(m => m.Name == "Equivalence");
        Assert.Equal("suspect", restored.State);
        Assert.Contains(restored.Cells, c => c.Before == "300" && c.After == "350");
        await f.Sessions.Current.UndoAsync("human");
        Assert.NotEqual("suspect", (await MeasureMarksTests.Finish(f.Engine, reference)).Marks.Single(m => m.Name == "Equivalence").State);
    }
}
