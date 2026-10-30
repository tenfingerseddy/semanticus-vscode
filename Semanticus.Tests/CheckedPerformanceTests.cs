using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.MeasureContexts;
using Xunit;
using Xunit.Abstractions;

namespace Semanticus.Tests;

[Collection("restore-root")]
public sealed class CheckedPerformanceTests : IDisposable
{
    private const string Reference = "measure:Sales/Total Sales";
    private readonly string root = Path.Combine(Path.GetTempPath(), "contoso-performance-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string safeRoot;
    private readonly ITestOutputHelper output;
    public CheckedPerformanceTests(RestoreRootFixture fixture, ITestOutputHelper output)
    { safeRoot = fixture.Root; this.output = output; ConnectionRegistry.RootOverride = root; }
    public void Dispose()
    { ConnectionRegistry.RootOverride = safeRoot; if (Directory.Exists(root)) Directory.Delete(root, true); }

    private sealed class Timer : IMeasureTimingSource
    {
        internal long[] Before = { 1000, 1010, 990 };
        internal long[] After = { 1500, 1510, 1490 };
        internal readonly List<string> WarmQueries = new();
        internal int ColdCalls, ProfileCalls;
        public Task<BenchmarkResult> WarmAsync(string query, int runs)
        {
            Assert.InRange(runs, 4, 6);
            WarmQueries.Add(query);
            var values = query.Contains("+ 50", StringComparison.Ordinal) ? After : Before;
            return Task.FromResult(new BenchmarkResult { Runs = 4, RunsMs = new[] { values[0] * 9 }.Concat(values).ToArray() });
        }
        public Task<ColdWarmBenchmark> ColdAsync(string query)
        {
            ColdCalls++;
            return Task.FromResult(new ColdWarmBenchmark
            { CacheClearAvailable = true, ColdTotal = new ColdWarmStats { N = 1, RunsMs = new[] { 7000L } } });
        }
        public Task<ServerTimings> ProfileAsync(string query)
        {
            ProfileCalls++;
            return Task.FromResult(new ServerTimings { TraceAvailable = true, TotalMs = 4000, SeMs = 500, FeMs = 3500 });
        }
    }

    private sealed class Planner : IMeasureContextPlanner
    {
        internal int Calls;
        internal ContextCandidate[] Candidates =
        {
            new("contoso-category", new("Product", "Category"), ContextShape.Group, ContextRole.Related,
                "Compare Contoso categories.", "The category should split the result.", 1, Array.Empty<string>(), Array.Empty<string>()),
            new("total", null, ContextShape.GrandTotal, ContextRole.Total, "Check the grand total.", "Recalculate the total.",
                90, Array.Empty<string>(), Array.Empty<string>())
        };
        public MeasureContextPlan Plan(ContextPlanningRequest request) { Calls++; return new(Candidates, Array.Empty<string>()); }
    }

    private async Task<MeasureMarksDoorTests.Fixture> Open(Timer timer, Planner? planner = null)
    {
        var f = new MeasureMarksDoorTests.Fixture(root); await f.Open();
        f.Engine.MeasureTimingSourceForTests = timer;
        f.Engine.MeasureContextPlannerForTests = planner ?? new Planner();
        return f;
    }
    private static MeasureMark Performance(MeasureMarksResult result) => Assert.Single(result.Marks, m => m.Name == "Performance");
    private static Task Settings(MeasureMarksDoorTests.Fixture f, MeasurePerformanceSettings settings) =>
        f.Engine.SetMeasurePerformanceAsync(settings);
    private static async Task<MeasureMarksResult> Timed(MeasureMarksDoorTests.Fixture f, bool full = false)
    {
        await f.Engine.GetMeasureMarksAsync(Reference, includePerformance: true, fullPerformanceSweep: full);
        return await MeasureMarksTests.Finish(f.Engine, Reference);
    }
    private static Task Edit(MeasureMarksDoorTests.Fixture f) => f.Sessions.Current.MutateAsync("human", "Edit Contoso sales", m =>
        m.Tables["Sales"].Measures["Total Sales"].Expression += " + 50");

    private async Task<MeasureMarksDoorTests.Fixture> OpenTailspin(Timer timer)
    {
        var f = await Open(timer);
        f.Engine.MeasureContextPlannerForTests = null;
        await f.Engine.CreateColumnAsync("table:Sales", "Quantity", "Int64", "Quantity", "human");
        await f.Engine.CreateColumnAsync("table:Sales", "Net Price", "Decimal", "Net Price", "human");
        foreach (var name in new[] { "Manufacturer", "Brand" })
            await f.Engine.CreateColumnAsync("table:Product", name, "String", name, "human");
        foreach (var (table, column) in new[] { ("Customer", "Continent"), ("Store", "Country"), ("Date", "Year") })
        {
            await f.Engine.CreateTableAsync(table, "human");
            var key = table + "Key";
            var fk = await f.Engine.CreateColumnAsync("table:Sales", key, "Int64", key, "human");
            var pk = await f.Engine.CreateColumnAsync("table:" + table, key, "Int64", key, "human");
            await f.Engine.CreateColumnAsync("table:" + table, column, column == "Year" ? "Int64" : "String", column, "human");
            await f.Engine.CreateRelationshipAsync(fk, pk, null, true, "human");
        }
        await f.Sessions.Current.MutateAsync("human", "Contoso Tailspin measure", m =>
        {
            m.Tables["Date"].DataCategory = "Time";
            m.Tables["Sales"].AddMeasure("Sales Tailspin Toys",
                "CALCULATE(SUMX(Sales, Sales[Quantity] * Sales[Net Price]), Product[Manufacturer] = \"Tailspin Toys\")");
        });
        // Reopen the completed fixture so structure edits do not stand in for a deployed model.
        var path = Path.Combine(root, "Contoso.bim");
        await f.Engine.SaveAsync(path, "bim", true, "human");
        await f.Engine.OpenAsync(path, true);
        f.Sessions.Current.LiveOrigin = new LiveOrigin("contoso", "Contoso Sales", null);
        f.Engine.SetLiveConnectionForTest(LiveConnection.ForTest("xmla", "contoso", "Contoso Sales", f.Query));
        return f;
    }

    [Theory]
    [InlineData("off")]
    [InlineData("run")]
    [InlineData("selection")]
    [InlineData("measure")]
    public async Task Planned_Tailspin_run_uses_chosen_contexts_and_times_only_when_requested(string timing)
    {
        const string reference = "measure:Sales/Sales Tailspin Toys";
        var timer = new Timer(); using var f = await OpenTailspin(timer);
        if (timing == "measure") await f.Engine.SetMeasurePerformanceAsync(measureRef: reference, includePerformance: true);
        var plan = await f.Engine.PlanMeasureRunAsync(new()
        {
            Selection = new() { Measures = new[] { reference } }, IncludePerformance = timing == "run",
            PerformanceMeasures = timing == "selection" ? new[] { reference } : Array.Empty<string>()
        });
        Assert.Empty(timer.WarmQueries);
        Assert.NotNull(f.Engine.MeasureRunExtras);
        var result = await f.Engine.RunMeasurePlanAsync(plan.Id);
        Assert.False(result.StoppedOnEdit);
        var cached = Assert.Single(result.Results, r => r.MeasureRef == reference);
        var expected = new[] { "slice:Product/Manufacturer:single",
            "group:Product/Brand", "group:Product/Category", "group:Customer/Continent", "group:Store/Country",
            "total" };
        foreach (var name in new[] { "Filters", "Contexts", "Totals" })
            Assert.Equal(expected, Assert.Single(cached.Marks, m => m.Name == name).ChosenContexts.Select(c => c.Id));
        Assert.Contains(f.Queries, q => q.Contains("'Product'[Manufacturer]"));
        Assert.DoesNotContain(f.Queries, q => q.Contains("'Date'[Year]"));
        var performance = Assert.Single(cached.Marks, m => m.Name == "Performance");
        Assert.Equal(timing != "off", cached.IncludedPerformance);
        if (timing == "off")
        {
            Assert.Equal("not run", performance.State); Assert.Empty(performance.Timings);
            Assert.Empty(timer.WarmQueries); Assert.Equal(0, timer.ColdCalls); Assert.Equal(0, timer.ProfileCalls);
        }
        else
        {
            Assert.Equal("passed", performance.State); Assert.NotEmpty(performance.Timings);
            Assert.NotEmpty(timer.WarmQueries); Assert.Equal("Grand total", performance.Timings[0].Context);
            Assert.All(timer.WarmQueries, q => Assert.Contains("Tailspin Toys", q));
        }
    }

    [Fact]
    public async Task Planned_run_cache_becomes_stale_when_Performance_settings_change()
    {
        using var f = await Open(new Timer());
        var plan = await f.Engine.PlanMeasureRunAsync(new() { Selection = new() { Measures = new[] { Reference } } });
        await f.Engine.RunMeasurePlanAsync(plan.Id);
        Assert.False(Assert.Single(await f.Engine.GetMeasureCheckCacheAsync()).Stale);
        await Settings(f, new() { SlowThresholdMs = 100 });
        Assert.True(Assert.Single(await f.Engine.GetMeasureCheckCacheAsync()).Stale);
    }

    [Fact]
    public async Task Refresh_after_a_timed_edit_collects_new_samples()
    {
        var timer = new Timer(); using var f = await Open(timer);
        await Timed(f); await Edit(f); await MeasureMarksTests.Finish(f.Engine, Reference);
        timer.WarmQueries.Clear();
        await f.Engine.GetMeasureMarksAsync(Reference, refresh: true, includePerformance: true);
        var mark = Performance(await MeasureMarksTests.Finish(f.Engine, Reference));
        Assert.Equal("passed", mark.State); Assert.NotEmpty(timer.WarmQueries);
        Assert.All(mark.Timings, t => Assert.Null(t.BeforeMedianMs));
    }

    [Fact]
    public async Task Planner_drives_Filters_Contexts_and_Totals_with_one_chosen_list()
    {
        var planner = new Planner(); using var f = await Open(new Timer(), planner);
        var result = await MeasureMarksTests.Finish(f.Engine, Reference);
        Assert.Equal(1, planner.Calls);
        foreach (var name in new[] { "Filters", "Contexts", "Totals" })
            Assert.Equal(planner.Candidates.Select(c => c.Id), result.Marks.Single(m => m.Name == name).ChosenContexts.Select(c => c.Id));
        Assert.Single(f.Queries, q => q.Contains("EVALUATE TOPN(50, VALUES("));
        Assert.Contains(f.Queries, q => q.Contains("'Product'[Category],"));
    }

    [Fact]
    public async Task Fixed_CALCULATE_filter_does_not_make_its_own_column_suspect()
    {
        using var f = await Open(new Timer()); f.Engine.MeasureContextPlannerForTests = null;
        await f.Sessions.Current.MutateAsync("human", "Set Contoso fixed filter", m =>
            m.Tables["Sales"].Measures["Total Sales"].Expression = "CALCULATE(SUM('Sales'[Amount]), 'Product'[Category] = \"Bikes\")");
        var result = await MeasureMarksTests.Finish(f.Engine, Reference);
        var filters = result.Marks.Single(m => m.Name == "Filters");
        Assert.Equal("passed", filters.State);
        Assert.Contains(filters.ChosenContexts, c => c.ExpectedBehaviour.Contains("overwrite", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Default_selected_run_is_not_timed_even_with_background_enabled()
    {
        var timer = new Timer(); using var f = await Open(timer);
        await Settings(f, new() { AllowBackground = true });
        var mark = Performance(await MeasureMarksTests.Finish(f.Engine, Reference));
        Assert.Equal("not run", mark.State); Assert.Equal("not timed", mark.Sentence);
        Assert.Empty(timer.WarmQueries); Assert.Equal(0, timer.ColdCalls);
    }

    [Fact]
    public async Task Slow_nested_iterator_flags_and_shows_median_spread_cold_and_engine_split()
    {
        var timer = new Timer { Before = new[] { 4000L, 4100L, 3900L } }; using var f = await Open(timer);
        await f.Sessions.Current.MutateAsync("human", "Contoso nested iterator", m =>
            m.Tables["Sales"].Measures["Total Sales"].Expression = "SUMX('Product', SUMX('Sales', 'Sales'[Amount]))");
        await MeasureMarksTests.Finish(f.Engine, Reference);
        var watch = Stopwatch.StartNew(); var mark = Performance(await Timed(f));
        Assert.Equal("suspect", mark.State); Assert.Contains("Grand total", mark.Sentence);
        var detail = Assert.Single(mark.Timings);
        Assert.Equal(4000, detail.MedianMs); Assert.Equal(3900, detail.MinMs); Assert.Equal(4100, detail.MaxMs);
        Assert.Equal(7000, detail.ColdMs); Assert.Equal(3500, detail.FormulaEngineMs); Assert.Equal(500, detail.StorageEngineMs);
        Assert.Equal(1, timer.ColdCalls); Assert.Equal(1, timer.ProfileCalls);
        output.WriteLine("Time to Performance mark (fake source): " + watch.ElapsedMilliseconds + " ms");
    }

    [Theory]
    [InlineData(3000, "suspect")]
    [InlineData(5000, "passed")]
    [InlineData(4000, "passed")]
    public async Task Model_threshold_setting_is_respected(double threshold, string state)
    {
        var timer = new Timer { Before = new[] { 4000L, 4001L, 3999L } }; using var f = await Open(timer);
        await Settings(f, new() { SlowThresholdMs = threshold });
        Assert.Equal(state, Performance(await Timed(f)).State);
    }

    [Theory]
    [InlineData(1000, 1400, 2000, 1000, 1900, 2100, "passed")]
    [InlineData(990, 1000, 1010, 1490, 1500, 1510, "suspect")]
    [InlineData(990, 1000, 1010, 1190, 1200, 1210, "passed")]
    [InlineData(1990, 2000, 2010, 2490, 2500, 2510, "passed")]
    public async Task Edit_requires_percent_absolute_and_noise_limits(long b1, long b2, long b3, long a1, long a2, long a3, string state)
    {
        var timer = new Timer { Before = new[] { b1, b2, b3 }, After = new[] { a1, a2, a3 } };
        using var f = await Open(timer); await Timed(f); await Edit(f);
        var mark = Performance(await MeasureMarksTests.Finish(f.Engine, Reference));
        Assert.Equal(state, mark.State);
        Assert.Contains(mark.Timings, t => t.BeforeMedianMs != null && t.BeforeRunsMs!.Length == 3);
        Assert.Contains(timer.WarmQueries, q => q.Contains("+ 50"));
    }

    [Fact]
    public async Task Edit_without_timing_baseline_does_not_time()
    {
        var timer = new Timer(); using var f = await Open(timer); await Edit(f);
        Assert.Equal("not timed", Performance(await MeasureMarksTests.Finish(f.Engine, Reference)).Sentence);
        Assert.Empty(timer.WarmQueries);
    }

    [Fact]
    public async Task Measure_opt_in_times_and_shared_model_skips_cache_clear()
    {
        var timer = new Timer(); using var f = await Open(timer); await Settings(f, new() { Shared = true });
        await f.Sessions.Current.MutateAsync("human", "Time Contoso measure", m =>
            m.Tables["Sales"].Measures["Total Sales"].SetAnnotation("Semanticus.Performance.Include", "true"));
        var mark = Performance(await MeasureMarksTests.Finish(f.Engine, Reference));
        Assert.Equal("passed", mark.State); Assert.NotEmpty(timer.WarmQueries); Assert.Equal(0, timer.ColdCalls);
        Assert.All(mark.Timings, t => { Assert.Null(t.ColdMs); Assert.Contains("shared", t.ColdNote); });
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 2)]
    public async Task Background_run_respects_model_opt_in(bool enabled, int expectedContexts)
    {
        var timer = new Timer { Before = new[] { 10L, 11L, 9L } }; using var f = await Open(timer);
        await Settings(f, new() { AllowBackground = enabled });
        await f.Engine.GetMeasureMarksAsync(Reference, origin: "background");
        var mark = Performance(await MeasureMarksTests.Finish(f.Engine, Reference));
        Assert.Equal(expectedContexts, mark.Timings.Length);
    }

    [Theory]
    [InlineData(60, 100, "passed")]
    [InlineData(10, 600, "passed")]
    [InlineData(10, 100, "suspect")]
    public async Task Model_regression_limits_are_adjustable(double percent, double milliseconds, string state)
    {
        using var f = await Open(new Timer()); await Settings(f, new() { RegressionPercent = percent, RegressionMs = milliseconds });
        await Timed(f); await Edit(f);
        Assert.Equal(state, Performance(await MeasureMarksTests.Finish(f.Engine, Reference)).State);
    }

    [Theory]
    [InlineData(false, 3)]
    [InlineData(true, 9)]
    public async Task Quick_run_is_capped_and_only_an_explicit_full_sweep_times_all_contexts(bool full, int count)
    {
        var planner = new Planner();
        planner.Candidates = Enumerable.Range(0, 8).Select(i => planner.Candidates[0] with { Id = "contoso-category-" + i })
            .Append(planner.Candidates[1]).ToArray();
        var timer = new Timer { Before = new[] { 10L, 11L, 9L } };
        using var f = await Open(timer, planner);
        var mark = Performance(await Timed(f, full));
        Assert.Equal(count, mark.Timings.Length); Assert.Equal(count, timer.ColdCalls);
        Assert.Equal("Grand total", mark.Timings[0].Context);
    }

    [Fact]
    public async Task Configuration_survives_reopen_in_the_check_store_without_an_edit_or_unsaved_model()
    {
        using var f = await Open(new Timer());
        var file = Path.Combine(root, "Contoso.bim");
        await f.Engine.SaveAsync(file, "bim", true, "human");
        var before = await File.ReadAllTextAsync(file);
        var revision = f.Sessions.Current.Revision;
        Assert.False((await f.Engine.SessionInfoAsync()).HasUnsavedChanges);
        await f.Engine.SetMeasurePerformanceAsync(new() { SlowThresholdMs = 1750, Shared = true }, Reference, true);
        var mark = await MeasureMarksTests.Finish(f.Engine, Reference);
        Assert.Equal(1750, mark.PerformanceSettings.SlowThresholdMs); Assert.True(mark.IncludePerformanceForMeasure);
        Assert.Null(await f.Sessions.Current.ReadAsync(m => m.GetAnnotation("Semanticus.Performance")));
        Assert.Equal(revision, f.Sessions.Current.Revision);
        Assert.False((await f.Engine.SessionInfoAsync()).HasUnsavedChanges);
        Assert.Equal(before, await File.ReadAllTextAsync(file));
        Assert.DoesNotContain((await f.Engine.GetPlanHistoryAsync()).Rows, r => r.Title == "Set performance settings");
        await f.Engine.OpenAsync(file);
        mark = await MeasureMarksTests.Finish(f.Engine, Reference);
        Assert.Equal(1750, mark.PerformanceSettings.SlowThresholdMs); Assert.True(mark.IncludePerformanceForMeasure);
        await f.Engine.SetMeasurePerformanceAsync(measureRef: Reference, includePerformance: false);
        Assert.False((await f.Engine.GetMeasureMarksAsync(Reference)).IncludePerformanceForMeasure);
        Assert.False((await f.Engine.SessionInfoAsync()).HasUnsavedChanges);
    }
}
