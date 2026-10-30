using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.MeasureContexts;
using Xunit;
using Xunit.Abstractions;

namespace Semanticus.Tests;

// The private capture stays outside the checkout. CI runs the hand-built Contoso fixtures below.
public sealed class ContosoContextEvidenceAttribute : FactAttribute
{
    public ContosoContextEvidenceAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SEMANTICUS_CONTEXT_BIM")))
            Skip = "Set SEMANTICUS_CONTEXT_BIM to run the offline context finder against an external model structure.";
    }
}

[Collection("restore-root")]
public sealed class CheckedContextQueryTests
{
    private const string Brand = "'Product'[Brand]";
    private const string Code = "'Product'[Brand Code]";
    private const string CompositeError = "Column [Columns] is part of composite key, but not all columns of the composite key are included in the expression.";
    private readonly ITestOutputHelper output;
    public CheckedContextQueryTests(ITestOutputHelper output) => this.output = output;

    private static async Task Build(DecisionFixture f, bool composite = false)
    {
        await f.Engine.CreateModelAsync("Contoso", 1604);
        foreach (var table in new[] { "Sales", "Product" }) await f.Engine.CreateTableAsync(table, "human");
        await f.Engine.CreateColumnAsync("table:Sales", "Quantity", "Int64", "Quantity", "human");
        await f.Engine.CreateColumnAsync("table:Sales", "Unit Cost", "Decimal", "Unit Cost", "human");
        var fk = await f.Engine.CreateColumnAsync("table:Sales", "ProductKey", "Int64", "ProductKey", "human");
        var pk = await f.Engine.CreateColumnAsync("table:Product", "ProductKey", "Int64", "ProductKey", "human");
        await f.Engine.CreateColumnAsync("table:Product", "Brand", "String", "Brand", "human");
        await f.Engine.CreateColumnAsync("table:Product", "Brand Code", "String", "Brand Code", "human");
        await f.Engine.CreateRelationshipAsync(fk, pk, null, true, "human");
        await f.Engine.CreateMeasureAsync("table:Sales", "Total Quantity", "SUM('Sales'[Quantity])", "human");
        await f.Engine.CreateMeasureAsync("table:Sales", "Total Cost", "SUMX('Sales', 'Sales'[Quantity] * 'Sales'[Unit Cost])", "human");
        f.Sessions.Current.LiveOrigin = new LiveOrigin("contoso", "Contoso", null);
        if (composite)
            await f.Sessions.Current.MutateAsync("human", "Contoso composite label", m =>
            {
                var product = m.Tables["Product"];
                product.Columns["Brand"].GroupByColumns.Add(product.Columns["Brand Code"]);
                product.Columns["Brand Code"].IsHidden = true;
            });
    }

    [Theory]
    [InlineData("SUM('Sales'[Quantity])")]
    [InlineData("CALCULATE([Total Quantity], REMOVEFILTERS())")]
    [InlineData("CALCULATE([Total Quantity], 'Columns'[Columns] = \"Brand\")")]
    public async Task Field_parameter_columns_are_never_contexts_or_subtotal_details(string expression)
    {
        using var f = new DecisionFixture(); await Build(f);
        await f.Engine.CreateFieldParameterAsync("Columns", new[] {
            new FieldParameterItem { ObjectRef = "column:Product/Brand" },
            new FieldParameterItem { ObjectRef = "measure:Sales/Total Quantity" }
        }, "human");
        // Include a visible extra column. The metadata applies to the whole parameter table.
        await f.Sessions.Current.MutateAsync("human", "Contoso parameter category", m =>
            m.Tables["Columns"].Columns["Columns Order"].IsHidden = false);
        var captured = await f.Sessions.Current.ReadAsync(ContextModelCapture.Capture);
        var plan = new MeasureContextPlanner().Plan(new(captured, new("Sales", "Contoso sample", expression)));
        Assert.DoesNotContain(plan.Contexts, c => c.Column?.Table == "Columns" || c.DetailColumn?.Table == "Columns");
        Assert.Contains(plan.Contexts, c => c.Column?.Table == "Product");
        Assert.Contains(plan.Contexts, c => c.Shape == ContextShape.GrandTotal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Chosen_composite_labels_query_the_hidden_key_in_grouping_members_and_empty_filters(bool defaults)
    {
        using var f = new DecisionFixture(); await Build(f, composite: true);
        var queries = new List<string>();
        f.Engine.SetLiveConnectionForTest(LiveConnection.ForTest("xmla", "contoso", "Contoso", query =>
        {
            queries.Add(query);
            if (query.Contains("TOPN(50"))
                return MeasureMarksDoorTests.Fixture.Rs(new[] { "Brand", "Brand Code" },
                    new object[] { "Contoso", "C01" }, new object[] { "Fabrikam", "F01" });
            if (query.Contains("FALSE ()"))
                return MeasureMarksDoorTests.Fixture.Rs(new[] { "Brand", "Brand Code", "v", "__present" });
            return query.Contains(Brand + ",")
                ? MeasureMarksDoorTests.Fixture.Rs(new[] { "Brand", "Brand Code", "v", "__present" }, new object[] { "Contoso", "C01", 30m, 1 })
                : MeasureMarksDoorTests.Fixture.Rs(new[] { "v", "__present" }, new object[] { 30m, 1 });
        }));
        var scenario = new ProbeScenario { Name = "Contoso empty brand", GroupBy = new[] { Brand },
            Filters = new[] { new ProbeFilter { Column = Brand, Empty = true } } };
        var probe = await f.Engine.ProbeMeasureAsync("[Total Quantity]", defaults ? Brand : null,
            defaults ? null : new[] { scenario, new ProbeScenario { Name = "Contoso brands", GroupBy = new[] { Brand } } }, defaults, 64);
        Assert.All(probe.Scenarios, s => Assert.Equal("ok", s.Status));
        var row = probe.Scenarios.SelectMany(s => s.Rows).First(r => r.Members.ContainsKey(Brand));
        Assert.Equal(30m, row.V);
        Assert.Equal("C01", row.Members[Code]);
        var grouped = queries.Where(q => q.Contains("SUMMARIZECOLUMNS(\n    " + Brand + ",")
            || q.Contains("SUMMARIZECOLUMNS(" + Brand + ",")).ToArray();
        Assert.NotEmpty(grouped);
        Assert.All(grouped, q => Assert.Contains(Code, q));
        var empty = Assert.Single(queries, q => q.Contains("FALSE ()"));
        Assert.Contains("ALL ( " + Brand + ", " + Code + " )", empty);
        if (defaults)
        {
            Assert.DoesNotContain(queries, q => q.Contains("VALUES(" + Brand + ")"));
            Assert.Contains(queries, q => q.Contains("TOPN(50") && q.Contains(Brand) && q.Contains(Code));
        }
    }

    [Theory]
    [InlineData(CompositeError, false)]
    [InlineData("The column 'Brand' specified in the SUMMARIZECOLUMNS function was not found in the input table.", false)]
    [InlineData(CompositeError, true)]
    public async Task A_context_that_fails_without_a_measure_is_unchecked_for_every_measure(string error, bool throws)
    {
        using var f = new DecisionFixture(); await Build(f);
        var queries = new List<string>();
        f.Engine.SetLiveConnectionForTest(LiveConnection.ForTest("xmla", "contoso", "Contoso", query =>
        {
            queries.Add(query);
            if (query.Contains(Brand))
            {
                if (throws) throw new InvalidOperationException(error);
                return new ResultSet { Error = error };
            }
            return MeasureMarksDoorTests.Fixture.Rs(new[] { "v", "__present" }, new object[] { 300m, 1 });
        }));
        foreach (var measure in new[] { "Total Quantity", "Total Cost" })
        {
            var probe = await f.Engine.ProbeMeasureAsync("[" + measure + "]", null, new[] {
                new ProbeScenario { Name = "by " + Brand, GroupBy = new[] { Brand } },
                new ProbeScenario { Name = "grand total" }
            }, false, 64);
            foreach (var mark in new[] { MeasureMarkJudge.Contexts(probe), MeasureMarkJudge.Filters(probe, null, null) })
            {
                Assert.Equal("not run", mark.State);
                Assert.Contains(mark.Cells, c => c.Context == "Brand"
                    && c.After.Contains("could not check this place", StringComparison.OrdinalIgnoreCase));
                Assert.DoesNotContain("The measure gives an error", mark.Sentence);
            }
            var unavailableOnly = new ProbeResult { Status = "ok", Scenarios = probe.Scenarios.Take(1).ToArray() };
            Assert.Equal("not run", MeasureMarkJudge.Contexts(unavailableOnly).State);
            Assert.Equal("not run", MeasureMarkJudge.Filters(unavailableOnly, null, null).State);
        }
        Assert.Contains(queries, q => q.Contains(Brand) && !q.Contains("[Total Quantity]") && !q.Contains("[Total Cost]"));
    }

    [Theory]
    [InlineData("Contoso measure error")]
    [InlineData(CompositeError)]
    public async Task An_error_in_the_measure_stays_suspect_even_when_its_words_describe_a_composite_key(string error)
    {
        using var f = new DecisionFixture(); await Build(f);
        f.Engine.SetLiveConnectionForTest(LiveConnection.ForTest("xmla", "contoso", "Contoso", query =>
            query.Contains("[Total Quantity]") ? new ResultSet { Error = error }
                : MeasureMarksDoorTests.Fixture.Rs(new[] { "Brand", "v", "__present" }, new object[] { "Contoso", 1, 1 })));
        var probe = await f.Engine.ProbeMeasureAsync("[Total Quantity]", null, new[] {
            new ProbeScenario { Name = "by " + Brand, GroupBy = new[] { Brand } }
        }, false, 64);
        var mark = MeasureMarkJudge.Contexts(probe);
        Assert.Equal("suspect", mark.State);
        Assert.Contains(mark.Cells, c => c.After == "ERROR: " + error);
    }

    [Fact]
    public async Task A_real_measure_error_stays_visible_beside_an_uncheckable_context()
    {
        using var f = new DecisionFixture(); await Build(f);
        f.Engine.SetLiveConnectionForTest(LiveConnection.ForTest("xmla", "contoso", "Contoso", query =>
            query.Contains(Brand) ? new ResultSet { Error = CompositeError }
                : query.Contains("[Total Quantity]") ? new ResultSet { Error = "Contoso measure error" }
                : MeasureMarksDoorTests.Fixture.Rs(new[] { "v", "__present" }, new object[] { 1, 1 })));
        var probe = await f.Engine.ProbeMeasureAsync("[Total Quantity]", null, new[] {
            new ProbeScenario { Name = "by " + Brand, GroupBy = new[] { Brand } },
            new ProbeScenario { Name = "grand total" }
        }, false, 64);
        var mark = MeasureMarkJudge.Contexts(probe);
        Assert.Equal("suspect", mark.State);
        Assert.Contains(mark.Cells, c => c.After == "ERROR: Contoso measure error");
        Assert.Contains(mark.Cells, c => c.After.Contains("could not check this place", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(mark.Cells, c => c.After == "ERROR: " + CompositeError);
    }

    [Fact]
    public async Task Production_marks_keep_plain_Contoso_measures_clear_with_a_field_parameter()
    {
        using var home = new DecisionFixture();
        using var f = new MeasureMarksDoorTests.Fixture(home.Workspace); await f.Open();
        await f.Engine.CreateFieldParameterAsync("Columns", new[] {
            new FieldParameterItem { ObjectRef = "column:Product/Category" },
            new FieldParameterItem { ObjectRef = "measure:Sales/Total Sales" }
        }, "human");
        var path = Path.Combine(home.Workspace, "Contoso.bim");
        await f.Engine.SaveAsync(path, "bim", true, "human");
        await f.Engine.OpenAsync(path, true);
        f.Sessions.Current.LiveOrigin = new LiveOrigin("contoso", "Contoso Sales", null);
        f.Engine.SetLiveConnectionForTest(LiveConnection.ForTest("xmla", "contoso", "Contoso Sales", query =>
            query.Contains("'Columns'[Columns]") ? new ResultSet { Error = CompositeError } : f.Query(query)));
        var result = await MeasureMarksTests.Finish(f.Engine, "measure:Sales/Total Sales");
        foreach (var name in new[] { "Contexts", "Filters" })
        {
            var mark = Assert.Single(result.Marks, m => m.Name == name);
            Assert.Equal("passed", mark.State);
            Assert.DoesNotContain(mark.ChosenContexts, c => c.Column?.Table == "Columns");
        }
    }

    [Fact]
    public async Task Production_marks_report_an_uncheckable_context_without_accusing_the_measure()
    {
        using var home = new DecisionFixture();
        using var f = new MeasureMarksDoorTests.Fixture(home.Workspace); await f.Open();
        f.Engine.SetLiveConnectionForTest(LiveConnection.ForTest("xmla", "contoso", "Contoso Sales", query =>
            query.Contains("'Product'[Category]") && !query.Contains("OrphanRows")
                ? new ResultSet { Error = CompositeError } : f.Query(query)));
        var result = await MeasureMarksTests.Finish(f.Engine, "measure:Sales/Total Sales");
        foreach (var name in new[] { "Contexts", "Filters" })
        {
            var mark = Assert.Single(result.Marks, m => m.Name == name);
            Assert.Equal("not run", mark.State);
            Assert.Contains(mark.Cells, c => c.After.Contains("could not check this place", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain("The measure gives an error", mark.Sentence);
        }
    }

    private sealed class LegacyContexts(LocalEngine engine, string version) : IMeasureRunExtras
    {
        public string Inputs => "formula-contexts-" + version + "|totals-v1|performance-v1|cached-description-v1|" + engine.DescriptionMatchActive;
        public Task<double?> FastMatchAsync(MeasureRunCandidate candidate, CancellationToken cancellationToken) => Task.FromResult<double?>(null);
        public Task<MeasureMark> PerformanceAsync(string measureRef, CancellationToken cancellationToken) =>
            Task.FromResult(new MeasureMark { Name = "Performance", State = "not run" });
        public Task<MeasureMark[]> ContextsAsync(string measureRef, CancellationToken cancellationToken) => Task.FromResult(new[] {
            new MeasureMark { Name = "Contexts", State = "suspect", Sentence = "The measure gives an error in some contexts." }
        });
    }

    [Theory]
    [InlineData("v1")]
    [InlineData("v2")]
    [InlineData("v4")]
    public async Task Old_context_marks_become_stale_after_the_context_checker_changes(string version)
    {
        using var home = new DecisionFixture();
        using var f = new MeasureMarksDoorTests.Fixture(home.Workspace); await f.Open();
        var current = f.Engine.MeasureRunExtras;
        f.Engine.MeasureRunExtras = new LegacyContexts(f.Engine, version);
        var plan = await f.Engine.PlanMeasureRunAsync(new() { Selection = new() { Measures = new[] { "measure:Sales/Total Sales" } } });
        await f.Engine.RunMeasurePlanAsync(plan.Id);
        var old = Assert.Single(await f.Engine.GetMeasureCheckCacheAsync());
        Assert.False(old.Stale);
        Assert.Contains(old.Marks, m => m.Name == "Contexts" && m.State == "suspect");
        f.Engine.MeasureRunExtras = current;
        Assert.True(Assert.Single(await f.Engine.GetMeasureCheckCacheAsync()).Stale);
        var next = await f.Engine.PlanMeasureRunAsync();
        Assert.Contains(next.Picks, p => p.MeasureRef == old.MeasureRef && p.Group == "changed");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Context_metadata_changes_make_cached_marks_stale(bool parameter)
    {
        using var home = new DecisionFixture();
        using var f = new MeasureMarksDoorTests.Fixture(home.Workspace); await f.Open();
        var plan = await f.Engine.PlanMeasureRunAsync(new() { Selection = new() { Measures = new[] { "measure:Sales/Total Sales" } } });
        await f.Engine.RunMeasurePlanAsync(plan.Id);
        Assert.False(Assert.Single(await f.Engine.GetMeasureCheckCacheAsync()).Stale);
        await f.Sessions.Current.MutateAsync("human", "Contoso context metadata", m =>
        {
            var label = m.Tables["Product"].Columns["Category"];
            if (parameter) label.SetExtendedProperty("ParameterMetadata", "{\"version\":3,\"kind\":2}", TabularEditor.TOMWrapper.ExtendedPropertyType.Json);
            else label.GroupByColumns.Add(m.Tables["Product"].Columns["ProductKey"]);
        });
        Assert.True(Assert.Single(await f.Engine.GetMeasureCheckCacheAsync()).Stale);
    }

    [ContosoContextEvidence]
    public async Task Offline_Contoso_finder_chooses_only_structurally_queryable_contexts()
    {
        var path = Environment.GetEnvironmentVariable("SEMANTICUS_CONTEXT_BIM")!;
        Assert.True(File.Exists(path), "SEMANTICUS_CONTEXT_BIM must point to the external model structure.");
        using var f = new DecisionFixture();
        await f.Engine.OpenAsync(path);
        var model = await f.Sessions.Current.ReadAsync(ContextModelCapture.Capture);
        var parameters = await f.Sessions.Current.ReadAsync(m => m.Tables
            .Where(t => t.Columns.Any(c => c.GetExtendedProperty("ParameterMetadata") != null)).Select(t => t.Name).ToArray());
        Assert.Equal(new[] { "Columns", "Metrics", "Rows" }, parameters.OrderBy(x => x, StringComparer.Ordinal));
        var composites = model.Columns.Where(c => c.GroupByColumns.Length > 0).ToArray();
        Assert.Equal(3, composites.Length);
        Assert.All(composites, c => Assert.True(c.FieldParameter));
        var planner = new MeasureContextPlanner();
        var picked = new HashSet<string>();
        var bad = new List<string>();
        foreach (var measure in model.Measures)
        {
            var plan = planner.Plan(new(model, measure));
            Assert.NotEmpty(plan.Contexts);
            foreach (var column in plan.Contexts.SelectMany(c => new[] { c.Column, c.DetailColumn }).Where(c => c != null))
            {
                if (parameters.Contains(column.Table) || column.Hidden) bad.Add(measure.Name + ": " + column.Dax);
                picked.Add(column.Dax);
            }
            output.WriteLine(measure.Name + ": " + string.Join("; ", plan.Contexts.Select(c => c.Id)));
        }
        output.WriteLine("Offline Contoso: " + model.Measures.Length + " measures; " + picked.Count + " distinct context columns.");
        output.WriteLine("Chosen columns: " + string.Join("; ", picked.OrderBy(x => x, StringComparer.Ordinal)));
        Assert.Empty(bad);
    }
}
