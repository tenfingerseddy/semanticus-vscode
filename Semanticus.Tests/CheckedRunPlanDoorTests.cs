using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipelines;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests;

public sealed class CheckedRunPlanDoorTests
{
    private sealed class Fixture : IDisposable
    {
        internal readonly string Root = Path.Combine(Path.GetTempPath(), "contoso-run-" + Guid.NewGuid().ToString("N")[..8]);
        internal readonly SessionManager Sessions = new();
        internal readonly LocalEngine Engine;
        internal Fixture() => Engine = new(Sessions, TestEntitlements.Pro, Root);
        internal async Task Open(int count = 8, int compatibility = 1604)
        {
            await Engine.CreateModelAsync("Contoso", compatibility);
            await Sessions.Current.MutateAsync("human", "Contoso fixture", m =>
            {
                var t = m.AddTable("Contoso Sales");
                t.AddMeasure("Base", "100");
                for (var i = 1; i < count; i++) t.AddMeasure("Measure " + i, "[Base] + " + i);
            });
        }
        public void Dispose() { Engine.Dispose(); Sessions.Dispose(); if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }

    private static Task<T> Call<T>(object target, string method, params object?[] args)
    {
        var member = target.GetType().GetMethod(method);
        Assert.NotNull(member);
        return (Task<T>)member.Invoke(target, args)!;
    }
    private static Task<MeasureRunPlan> Plan(object engine, params string[] refs)
        => Call<MeasureRunPlan>(engine, "PlanMeasureRunAsync", new MeasureRunRequest
        { Selection = refs.Length == 0 ? null : new() { Measures = refs } });

    private sealed class Extras : IMeasureRunExtras
    {
        public string Inputs => "contoso-contexts-v1";
        internal int CheapCalls, ContextCalls, PerformanceCalls;
        internal readonly ConcurrentQueue<string> ContextRefs = new();
        internal bool Block;
        internal int BlockAt = 1;
        internal readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<double?> FastMatchAsync(MeasureRunCandidate candidate, CancellationToken cancellationToken)
        { Interlocked.Increment(ref CheapCalls); return Task.FromResult<double?>(.9); }
        public async Task<MeasureMark[]> ContextsAsync(string measureRef, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref ContextCalls); ContextRefs.Enqueue(measureRef); Started.TrySetResult();
            if (Block && ContextCalls >= BlockAt)
                try { await Task.Delay(Timeout.Infinite, cancellationToken); }
                catch (OperationCanceledException) { Cancelled.TrySetResult(); throw; }
            return new[] { new MeasureMark { Name = "Contexts", State = "passed", Sentence = "Contoso contexts passed." } };
        }
        public Task<MeasureMark> PerformanceAsync(string measureRef, CancellationToken cancellationToken)
        { Interlocked.Increment(ref PerformanceCalls); return Task.FromResult(new MeasureMark { Name = "Performance", State = "passed" }); }
    }

    [Theory]
    [InlineData(12)]
    [InlineData(115)]
    public async Task Completed_rows_are_announced_before_a_later_measure_finishes(int count)
    {
        using var f = new Fixture(); await f.Open(count);
        var extra = new Extras { Block = true, BlockAt = 2 }; SetExtras(f.Engine, extra);
        var announced = new TaskCompletionSource<MeasureCheckCache[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        // The engine pushes each completed row (checks/didChange) before the run returns.
        f.Sessions.Bus.ChecksChanged += change => {
            if (change.States.Any(s => s.LastChecked != null && !s.Running && s.Marks.Length > 0))
                _ = Read();
            async Task Read() { announced.TrySetResult(await f.Engine.GetMeasureCheckCacheAsync()); }
        };
        var plan = await Plan(f.Engine);
        var run = f.Engine.RunMeasurePlanAsync(plan.Id);
        try {
            var rows = await announced.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Contains(rows, r => r.LastChecked != null && !r.Stale);
            Assert.False(run.IsCompleted);
        }
        finally {
            await f.Sessions.Current.MutateAsync("human", "Contoso stop", m => m.Tables["Contoso Sales"].Measures["Base"].Description = "Stop");
            await run.WaitAsync(TimeSpan.FromSeconds(3));
        }
    }

    private static void SetExtras(LocalEngine engine, Extras extras)
    {
        var property = typeof(LocalEngine).GetProperty("MeasureRunExtras");
        Assert.NotNull(property); property.SetValue(engine, extras);
    }

    [Fact]
    public async Task Fx14_Properties_reads_the_completed_plan_result()
    {
        using var f = new Fixture(); await f.Open(); SetExtras(f.Engine, new Extras());
        const string reference = "measure:Contoso Sales/Measure 1";
        Directory.CreateDirectory(f.Root);
        var path = Path.Combine(f.Root, "Contoso.bim");
        await f.Engine.SaveAsync(path, "bim", true, "human");
        await f.Engine.SetMeasurePerformanceAsync(measureRef: reference, includePerformance: true);
        var plan = await Plan(f.Engine, reference);
        var run = await f.Engine.RunMeasurePlanAsync(plan.Id);
        var properties = await MeasureMarksTests.Finish(f.Engine, reference);
        var row = Assert.Single((await f.Engine.GetMeasureCheckCacheAsync()).Where(c => c.MeasureRef == reference));
        Assert.Equal(JsonSerializer.Serialize(Assert.Single(run.Results).Marks), JsonSerializer.Serialize(properties.Marks));
        Assert.Equal(JsonSerializer.Serialize(properties.Marks), JsonSerializer.Serialize(row.Marks));
        Assert.Equal(Assert.Single(run.Results).LastChecked, properties.LastChecked);
        Assert.Equal(properties.LastChecked, row.LastChecked);
        Assert.Equal(new[] { "Filters", "Contexts", "SQL", "Trusted number", "Equivalence", "Totals", "Alignment", "Performance" }, properties.Marks.Select(m => m.Name));
        await f.Engine.OpenAsync(path, true);
        var reopened = await f.Engine.GetMeasureMarksAsync(reference);
        var reopenedRow = Assert.Single((await f.Engine.GetMeasureCheckCacheAsync()).Where(c => c.MeasureRef == reference));
        Assert.False(reopened.Running);
        Assert.Equal(JsonSerializer.Serialize(properties.Marks), JsonSerializer.Serialize(reopened.Marks));
        Assert.Equal(reopened.LastChecked, reopenedRow.LastChecked);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Properties_rechecks_old_checker_marks_in_active_and_restored_workers(bool reopen)
    {
        using var f = new Fixture(); await f.Open(); SetExtras(f.Engine, new Extras());
        const string reference = "measure:Contoso Sales/Measure 1";
        Directory.CreateDirectory(f.Root);
        var path = Path.Combine(f.Root, "Contoso.bim");
        await f.Engine.SaveAsync(path, "bim", true, "human");
        var plan = await Plan(f.Engine, reference);
        await f.Engine.RunMeasurePlanAsync(plan.Id);
        if (reopen) await f.Engine.OpenAsync(path, true);
        var previous = await f.Engine.GetMeasureMarksAsync(reference);
        Assert.False(previous.Running);
        Assert.Contains(previous.Marks, m => m.Sentence == "Contoso contexts passed.");
        SetExtras(f.Engine, new Extras());
        Assert.False((await f.Engine.GetMeasureMarksAsync(reference)).Running);

        f.Engine.MeasureRunExtras = null;
        var recheck = await f.Engine.GetMeasureMarksAsync(reference);
        Assert.True(recheck.Running);
        Assert.DoesNotContain(recheck.Marks, m => m.Sentence == "Contoso contexts passed.");
        var properties = await MeasureMarksTests.Finish(f.Engine, reference);
        var row = Assert.Single((await f.Engine.GetMeasureCheckCacheAsync()).Where(c => c.MeasureRef == reference));
        Assert.Equal(JsonSerializer.Serialize(properties.Marks), JsonSerializer.Serialize(row.Marks));
        Assert.False(row.Stale);
    }

    [Fact]
    public async Task Fx14_first_estimate_counts_the_rule_contexts_for_each_mark_without_queries()
    {
        using var f = new MeasureMarksDoorTests.Fixture(Path.Combine(Path.GetTempPath(), "Contoso-fx14-estimate"));
        await f.Open();
        const string reference = "measure:Sales/Total Sales";
        var expected = await f.Sessions.Current.ReadAsync(model => {
            var snapshot = MeasureMarkSnapshot.Capture(model);
            var measure = snapshot.ContextModel.Measures.Single(m => m.Name == "Total Sales");
            var contexts = new Semanticus.Engine.MeasureContexts.MeasureContextPlanner().Plan(new(snapshot.ContextModel, measure));
            return 2 + .5 * new[] { "Filters", "Contexts", "Totals" }.Sum(name => contexts.ForMark(name).Count());
        });
        var plan = await f.Engine.PlanMeasureRunAsync(new() { Selection = new() { Measures = new[] { reference } } });
        Assert.True(plan.FirstRun);
        Assert.Equal(expected, plan.EstimatedSeconds);
        Assert.Empty(f.Queries);
    }

    [Theory]
    [InlineData(12)]
    [InlineData(115)]
    public async Task An_entire_selected_plan_fills_every_row(int count)
    {
        using var f = new Fixture(); await f.Open(count);
        var extra = new Extras(); SetExtras(f.Engine, extra);
        var refs = Enumerable.Range(0, count).Select(i => "measure:Contoso Sales/" + (i == 0 ? "Base" : "Measure " + i)).ToArray();
        var plan = await Plan(f.Engine, refs);
        var run = await f.Engine.RunMeasurePlanAsync(plan.Id);
        Assert.False(run.StoppedOnEdit);
        Assert.Equal(count, run.Results.Length);
        Assert.All(run.Results, row => { Assert.NotNull(row.LastChecked); Assert.NotEmpty(row.Marks); Assert.False(row.Stale); });
        Assert.Equal(count, extra.ContextCalls);
    }

    [Fact]
    public async Task Planning_runs_no_checks_then_execution_checks_all_cheap_and_only_selected_expensive()
    {
        using var f = new Fixture(); await f.Open(500); var extra = new Extras(); SetExtras(f.Engine, extra);
        var plan = await Plan(f.Engine, "measure:Contoso Sales/Measure 1", "measure:Contoso Sales/Measure 2");
        Assert.Equal(0, extra.CheapCalls + extra.ContextCalls + extra.PerformanceCalls);
        var run = await Call<MeasureRunResult>(f.Engine, "RunMeasurePlanAsync", plan.Id);
        Assert.False(run.StoppedOnEdit); Assert.Equal(2, run.Results.Length);
        Assert.Equal(500, extra.CheapCalls); Assert.Equal(2, extra.ContextCalls); Assert.Equal(0, extra.PerformanceCalls);
        Assert.Equal(500, (await f.Engine.GetMeasureCheckCacheAsync()).Count(c => c.Cheap.Length > 0));
        Assert.All(run.Results, c => Assert.Contains(c.Cheap, m => m.Name == "Parse"));
        Assert.All(run.Results.Where(c => c.Marks.Length > 0), c => Assert.NotNull(c.LastChecked));
    }

    [Fact]
    public async Task Dependency_edit_makes_cached_results_stale_but_an_unrelated_edit_does_not()
    {
        using var f = new Fixture(); await f.Open();
        var plan = await Plan(f.Engine, "measure:Contoso Sales/Measure 1");
        await Call<MeasureRunResult>(f.Engine, "RunMeasurePlanAsync", plan.Id);
        await f.Sessions.Current.MutateAsync("human", "Contoso unrelated edit", m => m.Tables["Contoso Sales"].Measures["Measure 7"].Description = "Contoso unrelated");
        var cache = await Call<MeasureCheckCache[]>(f.Engine, "GetMeasureCheckCacheAsync");
        var before = Assert.Single(cache.Where(c => c.MeasureRef.EndsWith("/Measure 1"))); Assert.False(before.Stale);
        Assert.NotNull(before.InputSnapshot);
        Assert.Contains("100", before.InputSnapshot.Value.GetRawText());
        await f.Sessions.Current.MutateAsync("human", "Contoso dependency edit", m => m.Tables["Contoso Sales"].Measures["Base"].Expression = "200");
        cache = await Call<MeasureCheckCache[]>(f.Engine, "GetMeasureCheckCacheAsync");
        var after = Assert.Single(cache.Where(c => c.MeasureRef == before.MeasureRef));
        Assert.True(after.Stale); Assert.Equal(before.LastChecked, after.LastChecked); Assert.Equal(before.Inputs, after.Inputs);
        Assert.Equal(before.InputSnapshot.Value.GetRawText(), after.InputSnapshot!.Value.GetRawText());
        var next = await Plan(f.Engine); Assert.Contains(next.Picks, p => p.MeasureRef == after.MeasureRef && p.Group == "changed");
    }

    [Fact]
    public async Task Sweep_cancels_its_active_check_on_edit_and_never_starts_the_next_measure()
    {
        using var f = new Fixture(); await f.Open(); var extra = new Extras { Block = true }; SetExtras(f.Engine, extra);
        var sweep = Call<MeasureRunResult>(f.Engine, "SweepMeasureChecksAsync", true);
        await extra.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await f.Sessions.Current.MutateAsync("human", "Contoso stop sweep", m => m.Tables["Contoso Sales"].Measures["Base"].Description = "Changed");
        await extra.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var result = await sweep.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(result.StoppedOnEdit); Assert.Equal(1, extra.ContextCalls);
        Assert.Empty(result.Results.Where(c => c.LastChecked != null));
    }

    [Fact]
    public async Task Sweep_obeys_idle_delay_and_excludes_last_run()
    {
        using var f = new Fixture(); await f.Open(); var extra = new Extras(); SetExtras(f.Engine, extra);
        var plan = await Plan(f.Engine, "measure:Contoso Sales/Base");
        await Call<MeasureRunResult>(f.Engine, "RunMeasurePlanAsync", plan.Id);
        var idle = await Call<MeasureRunResult>(f.Engine, "SweepMeasureChecksAsync", false);
        Assert.Empty(idle.Results);
        var sweep = await Call<MeasureRunResult>(f.Engine, "SweepMeasureChecksAsync", true);
        Assert.Equal(3, sweep.Results.Length);
        Assert.DoesNotContain(sweep.Results, c => c.MeasureRef == "measure:Contoso Sales/Base");
        await Call<MeasureSweepOptions>(f.Engine, "ConfigureMeasureSweepAsync", new MeasureSweepOptions { IdleMinutes = 0, BatchSize = 1 });
        var eligible = await Call<MeasureRunResult>(f.Engine, "SweepMeasureChecksAsync", false);
        Assert.Single(eligible.Results);
    }

    [Fact]
    public async Task Sweep_runs_never_checked_measures_before_the_oldest_cached_measure()
    {
        using var f = new Fixture(); await f.Open(); var extra = new Extras(); SetExtras(f.Engine, extra);
        var first = await Plan(f.Engine, "measure:Contoso Sales/Base", "measure:Contoso Sales/Measure 1");
        await Call<MeasureRunResult>(f.Engine, "RunMeasurePlanAsync", first.Id);
        var second = await Plan(f.Engine, "measure:Contoso Sales/Base");
        await Call<MeasureRunResult>(f.Engine, "RunMeasurePlanAsync", second.Id);
        await Call<MeasureSweepOptions>(f.Engine, "ConfigureMeasureSweepAsync", new MeasureSweepOptions { BatchSize = 10 });
        var swept = await Call<MeasureRunResult>(f.Engine, "SweepMeasureChecksAsync", true);
        Assert.Equal(7, swept.Results.Length);
        Assert.Equal(Enumerable.Range(2, 6).Select(i => "measure:Contoso Sales/Measure " + i).Append("measure:Contoso Sales/Measure 1"), extra.ContextRefs.Skip(3));
    }

    [Fact]
    public async Task A_bad_unselected_formula_still_gets_a_cheap_parse_finding()
    {
        using var f = new Fixture(); await f.Open(); var extra = new Extras(); SetExtras(f.Engine, extra);
        await f.Sessions.Current.MutateAsync("human", "Contoso bad formula", m => m.Tables["Contoso Sales"].AddMeasure("Contoso broken", "SUM("));
        var plan = await Plan(f.Engine, "measure:Contoso Sales/Base");
        var run = await Call<MeasureRunResult>(f.Engine, "RunMeasurePlanAsync", plan.Id);
        Assert.Single(run.Results);
        var broken = Assert.Single((await f.Engine.GetMeasureCheckCacheAsync()).Where(c => c.MeasureRef.EndsWith("/Contoso broken")));
        Assert.Equal("suspect", Assert.Single(broken.Cheap.Where(m => m.Name == "Parse")).State);
        Assert.Empty(broken.Marks); Assert.Equal(1, extra.ContextCalls);
    }

    [Fact]
    public async Task Performance_is_opt_in_and_the_sweep_needs_model_permission()
    {
        using var f = new Fixture(); await f.Open(); var extra = new Extras(); SetExtras(f.Engine, extra);
        var request = new MeasureRunRequest { Selection = new() { Measures = new[] { "measure:Contoso Sales/Base" } }, IncludePerformance = true };
        var plan = await Call<MeasureRunPlan>(f.Engine, "PlanMeasureRunAsync", request);
        await Call<MeasureRunResult>(f.Engine, "RunMeasurePlanAsync", plan.Id);
        Assert.Equal(1, extra.PerformanceCalls);
        await Call<MeasureRunResult>(f.Engine, "SweepMeasureChecksAsync", true);
        Assert.Equal(1, extra.PerformanceCalls);
        var settings = await Call<MeasureSweepOptions>(f.Engine, "ConfigureMeasureSweepAsync", new MeasureSweepOptions { AllowPerformance = true, BatchSize = 2 });
        Assert.Equal(2, settings.BatchSize);
        await Call<MeasureRunResult>(f.Engine, "SweepMeasureChecksAsync", true);
        Assert.Equal(3, extra.PerformanceCalls);
    }

    [Fact]
    public async Task Cached_marks_and_times_survive_reopening_the_model()
    {
        using var f = new Fixture(); await f.Open();
        Directory.CreateDirectory(f.Root);
        var path = Path.Combine(f.Root, "Contoso.bim");
        await f.Engine.SaveAsync(path, "bim", true, "human");
        var plan = await Plan(f.Engine, "measure:Contoso Sales/Measure 1");
        await Call<MeasureRunResult>(f.Engine, "RunMeasurePlanAsync", plan.Id);
        var before = (await Call<MeasureCheckCache[]>(f.Engine, "GetMeasureCheckCacheAsync")).Single(c => c.MeasureRef.EndsWith("/Measure 1"));
        await f.Engine.OpenAsync(path, true);
        var after = (await Call<MeasureCheckCache[]>(f.Engine, "GetMeasureCheckCacheAsync")).Single(c => c.MeasureRef == before.MeasureRef);
        Assert.NotNull(before.LastChecked); Assert.Equal(before.LastChecked, after.LastChecked);
        Assert.Equal(before.Inputs, after.Inputs); Assert.False(after.Stale);
    }

    [Fact]
    public async Task A_first_save_keeps_checks_made_before_it()
    {
        using var f = new Fixture(); await f.Open();
        var plan = await Plan(f.Engine, "measure:Contoso Sales/Measure 1");
        await Call<MeasureRunResult>(f.Engine, "RunMeasurePlanAsync", plan.Id);
        var before = (await Call<MeasureCheckCache[]>(f.Engine, "GetMeasureCheckCacheAsync")).Single(c => c.MeasureRef.EndsWith("/Measure 1"));
        var path = Path.Combine(f.Root, "Contoso.bim");
        await f.Engine.SaveAsync(path, "bim", true, "human");
        await f.Engine.OpenAsync(path, true);
        var after = (await Call<MeasureCheckCache[]>(f.Engine, "GetMeasureCheckCacheAsync")).Single(c => c.MeasureRef == before.MeasureRef);
        Assert.NotNull(after.LastChecked); Assert.Equal(before.LastChecked, after.LastChecked);
    }

    [Fact]
    public async Task Formula_risk_ignores_function_names_inside_strings_and_comments()
    {
        using var f = new Fixture(); await f.Open();
        await f.Sessions.Current.MutateAsync("human", "Contoso risk formulas", m =>
        {
            var t = m.Tables["Contoso Sales"];
            t.AddMeasure("Contoso ratio", "DIVIDE([Base], 100)");
            t.AddMeasure("Contoso text", "\"CALCULATE SUMX DATEADD DIVIDE\" & [Base] // FILTER([Base])");
            t.AddMeasure("Contoso format", "FORMAT([Base], \"0.00\")");
        });
        // A completed selection establishes which formulas changed since the prior run.
        var initial = await Plan(f.Engine, "measure:Contoso Sales/Contoso ratio", "measure:Contoso Sales/Contoso text", "measure:Contoso Sales/Contoso format");
        await Call<MeasureRunResult>(f.Engine, "RunMeasurePlanAsync", initial.Id);
        var plan = await Plan(f.Engine);
        Assert.Contains(plan.Picks, p => p.MeasureRef.EndsWith("/Contoso ratio") && p.Group == "risky" && p.Reason.Contains("ratios"));
        Assert.Contains(plan.Picks, p => p.MeasureRef.EndsWith("/Contoso text") && p.Group != "risky");
        Assert.DoesNotContain(plan.Picks, p => p.MeasureRef.EndsWith("/Contoso format"));
    }

    [Fact]
    public async Task A_function_dependency_edit_makes_the_result_stale()
    {
        using var f = new Fixture(); await f.Open(compatibility: 1702);
        await f.Sessions.Current.MutateAsync("human", "Contoso function", m =>
        {
            m.AddFunction("ContosoRate").Expression = "() => 100";
            m.Tables["Contoso Sales"].Measures["Base"].Expression = "ContosoRate()";
        });
        var plan = await Plan(f.Engine, "measure:Contoso Sales/Measure 1");
        await Call<MeasureRunResult>(f.Engine, "RunMeasurePlanAsync", plan.Id);
        await f.Sessions.Current.MutateAsync("human", "Contoso function edit", m => m.Functions["ContosoRate"].Expression = "() => 200");
        var result = (await Call<MeasureCheckCache[]>(f.Engine, "GetMeasureCheckCacheAsync")).Single(c => c.MeasureRef.EndsWith("/Measure 1"));
        Assert.True(result.Stale);
    }

    [Fact]
    public async Task A_check_notification_does_not_clear_the_displayed_plan()
    {
        using var f = new Fixture(); await f.Open();
        var plan = await Plan(f.Engine, "measure:Contoso Sales/Base");
        f.Sessions.Bus.Publish(new ChangeNotification
        { SessionId = f.Sessions.Current.Id, Revision = f.Sessions.Current.Revision, Deltas = Array.Empty<ChangeDelta>(), Label = "Contoso check finished" });
        var run = await Call<MeasureRunResult>(f.Engine, "RunMeasurePlanAsync", plan.Id);
        Assert.False(run.StoppedOnEdit);
    }

    [Fact]
    public async Task An_edit_cancels_the_sweeps_actual_DAX_command()
    {
        using var f = new Fixture();
        await f.Engine.CreateModelAsync("Contoso", 1604);
        var sales = await f.Engine.CreateTableAsync("Contoso Sales", "human");
        var product = await f.Engine.CreateTableAsync("Contoso Product", "human");
        var fk = await f.Engine.CreateColumnAsync(sales, "ProductKey", "Int64", "ProductKey", "human");
        var pk = await f.Engine.CreateColumnAsync(product, "ProductKey", "Int64", "ProductKey", "human");
        await f.Engine.CreateColumnAsync(product, "Category", "String", "Category", "human");
        await f.Engine.CreateRelationshipAsync(fk, pk, null, true, "human");
        await f.Sessions.Current.MutateAsync("human", "Contoso live fixture", m => m.Tables["Contoso Sales"].AddMeasure("Base", "100"));
        f.Sessions.Current.LiveOrigin = new LiveOrigin("contoso", "Contoso", null);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        f.Engine.SetLiveConnectionForTest(LiveConnection.ForTest("xmla", "contoso", "Contoso", (_, ct) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                started.TrySetResult();
                ct.WaitHandle.WaitOne(TimeSpan.FromSeconds(10));
                if (ct.IsCancellationRequested) cancelled.TrySetResult();
            }
            return new ResultSet { Columns = new[] { new ColumnDef { Name = "v" } }, Rows = new[] { new object[] { 100m } }, RowCount = 1 };
        }));
        var sweep = Call<MeasureRunResult>(f.Engine, "SweepMeasureChecksAsync", true);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await f.Sessions.Current.MutateAsync("human", "Contoso edit while checking", m => m.Tables["Contoso Sales"].Measures["Base"].Description = "Changed");
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True((await sweep.WaitAsync(TimeSpan.FromSeconds(2))).StoppedOnEdit);
    }

    [Fact]
    public async Task RPC_equals_MCP_run_plan_on_one_session()
    {
        using var f = new Fixture(); await f.Open();
        var pipe = TestPipe.Mint("runplan-"); const string challenge = "contoso-run-plan-ui-challenge-0123456789";
        using var server = new RpcServer(f.Sessions, f.Engine, pipe, challenge);
        using var stop = new CancellationTokenSource(); var serving = server.RunAsync(stop.Token);
        using var remote = await RemoteEngine.ConnectAsync(pipe);
        using var human = await PlanQueueKit.Client.ConnectAsync(pipe, "human", challenge);
        var toServer = new Pipe(); var toClient = new Pipe();
        var builder = Host.CreateApplicationBuilder(Array.Empty<string>()); builder.Logging.ClearProviders();
        builder.Services.AddSingleton<IEngine>(remote);
        builder.Services.AddMcpServer().WithStreamServerTransport(toServer.Reader.AsStream(), toClient.Writer.AsStream())
            .WithToolsFromAssembly(typeof(McpTools).Assembly).WithRequestFilters(filters => McpDoorFilters.Add(filters, remote));
        using var host = builder.Build(); await host.StartAsync();
        try
        {
            await using var client = await McpClient.CreateAsync(new StreamClientTransport(toServer.Writer.AsStream(), toClient.Reader.AsStream()));
            Assert.NotNull(typeof(IEngine).GetMethod("PlanMeasureRunAsync"));
            var rpc = await human.InvokeAsync<MeasureRunPlan>("planMeasureRun", new MeasureRunRequest());
            var mcp = await client.CallToolAsync("plan_measure_run", new Dictionary<string, object?>());
            Assert.NotEqual(true, mcp.IsError);
            var result = JsonSerializer.Deserialize<MeasureRunPlan>(string.Join("", mcp.Content.OfType<TextContentBlock>().Select(c => c.Text)), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            Assert.Equal(JsonSerializer.Serialize(rpc), JsonSerializer.Serialize(result));
        }
        finally { await host.StopAsync(); stop.Cancel(); try { await serving; } catch (OperationCanceledException) { } }
    }
}
