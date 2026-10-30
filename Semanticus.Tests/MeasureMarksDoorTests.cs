using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipelines;
using System.Linq;
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
using Xunit.Abstractions;

namespace Semanticus.Tests;

[Collection("restore-root")]
public sealed class MeasureMarksDoorTests : IDisposable
{
    private readonly ITestOutputHelper output;
    private readonly string safeRoot;
    private readonly string root = Path.Combine(Path.GetTempPath(), "contoso-marks-" + Guid.NewGuid().ToString("N")[..8]);
    public MeasureMarksDoorTests(ITestOutputHelper output, RestoreRootFixture fixture)
    {
        this.output = output; safeRoot = fixture.Root; ConnectionRegistry.RootOverride = root;
    }
    public void Dispose()
    {
        ConnectionRegistry.RootOverride = safeRoot;
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }

    internal sealed class Fixture : IDisposable
    {
        internal readonly SessionManager Sessions = new();
        internal readonly LocalEngine Engine;
        internal readonly ConcurrentQueue<string> Queries = new();
        internal readonly ManualResetEventSlim Release = new(true);
        internal long Orphans;
        internal int SqlCalls;
        internal Fixture(string root, Semanticus.Engine.Entitlement.IEntitlement? entitlement = null) => Engine = new LocalEngine(Sessions, entitlement ?? TestEntitlements.Pro, root);
        internal async Task Open(int count = 1, Action<TabularEditor.TOMWrapper.Model>? configure = null)
        {
            await Engine.CreateModelAsync("Contoso Sales", 1604);
            var sales = await Engine.CreateTableAsync("Sales", "human");
            var product = await Engine.CreateTableAsync("Product", "human");
            var fk = await Engine.CreateColumnAsync(sales, "ProductKey", "Int64", "ProductKey", "human");
            var pk = await Engine.CreateColumnAsync(product, "ProductKey", "Int64", "ProductKey", "human");
            await Engine.CreateColumnAsync(product, "Category", "String", "Category", "human");
            await Engine.CreateColumnAsync(sales, "Amount", "Decimal", "Amount", "human");
            await Engine.CreateRelationshipAsync(fk, pk, null, true, "human");
            await Sessions.Current.MutateAsync("human", "Contoso measures", m =>
            {
                configure?.Invoke(m);
                m.Tables["Sales"].AddMeasure("Total Sales", "SUM('Sales'[Amount])").LineageTag = "contoso-total-sales";
                for (var i = 1; i < count; i++) m.Tables["Sales"].AddMeasure("Sales Copy " + i, "[Total Sales]");
            });
            Sessions.Current.LiveOrigin = new LiveOrigin("contoso", "Contoso Sales", null);
            Engine.SetLiveConnectionForTest(LiveConnection.ForTest("xmla", "contoso", "Contoso Sales", Query));
            Engine.ReconcileSqlForTests = (_, _, sql) =>
            {
                Interlocked.Increment(ref SqlCalls);
                return sql.Contains("GROUP BY", StringComparison.OrdinalIgnoreCase)
                    ? Rs(new[] { "Category", "Amount" }, new object[] { "Bikes", 100m }, new object[] { "Accessories", 200m })
                    : Rs(new[] { "Amount" }, new object[] { 300m });
            };
        }
        internal ResultSet Query(string query)
        {
            Release.Wait();
            Queries.Enqueue(query);
            if (query.Contains("OrphanRows")) return Rs(RelationshipProbes.CombinedColumns, new object[] { Orphans, 0L, 0L, 0L, 3L, 2L });
            if (query.Contains("EVALUATE TOPN(50, VALUES("))
                return Rs(new[] { "Category" }, new object[] { "Bikes" }, new object[] { "Accessories" });
            var moved = query.Contains("+ 50", StringComparison.Ordinal);
            var ignored = query.Contains("ALL('Product')", StringComparison.Ordinal)
                || query.Contains("'Product'[Category] = \"Bikes\"", StringComparison.Ordinal);
            var anonymous = query.IndexOf("MEASURE 'Sales'[__smx_", StringComparison.Ordinal);
            if (anonymous >= 0)
            {
                var body = query[anonymous..];
                moved = false;
                ignored = body.Contains("ALL('Product')", StringComparison.Ordinal)
                    || body.Contains("'Product'[Category] = \"Bikes\"", StringComparison.Ordinal);
            }
            decimal Total() => moved ? 350m : 300m;
            if (query.Contains("__mark_context"))
                return Rs(new[] { "__mark_context", "v" }, new object[] { "Grand total", Total() },
                    new object[] { "Bikes", ignored ? Total() : moved ? 150m : 100m },
                    new object[] { "Accessories", ignored ? Total() : moved ? 250m : 200m });
            var empty = query.Contains("FALSE()", StringComparison.OrdinalIgnoreCase) || query.Contains("FALSE ()", StringComparison.OrdinalIgnoreCase);
            if (empty) return Rs(new[] { "Category", "v", "__present" });
            if (query.Contains("ROW(\"__recon_value\"") || query.Contains("ROW(\"v\","))
                return Rs(new[] { "v" }, new object[] { query.Contains("ROW(\"v\", (1))") ? 1m : Total() });
            var grouped = query.Contains("SUMMARIZECOLUMNS(") && query.Contains("'Product'[Category],");
            var filtered = query.Contains("TREATAS", StringComparison.OrdinalIgnoreCase);
            var single = filtered && query.Contains("\"Bikes\"") && !query.Contains("\"Accessories\"");
            var edge = filtered && !query.Contains("\"Bikes\"") && query.Contains("\"Accessories\"");
            if (query.Contains("Contoso error") && edge) return new ResultSet { Error = "Contoso error in the edge context." };
            if (grouped)
            {
                var rows = new List<object[]>();
                if (!edge) rows.Add(new object[] { "Bikes", ignored ? Total() : moved ? 150m : 100m, 1 });
                if (!single) rows.Add(new object[] { "Accessories", ignored ? Total() : moved ? 250m : 200m, 1 });
                return Rs(new[] { "Category", "v", "__present" }, rows.ToArray());
            }
            return Rs(new[] { "v", "__present" }, new object[] { ignored ? Total() : single ? 100m : edge ? 200m : Total(), 1 });
        }
        internal static ResultSet Rs(string[] names, params object[][] rows) => new()
        { Columns = names.Select(n => new ColumnDef { Name = n }).ToArray(), Rows = rows, RowCount = rows.Length };
        internal async Task SaveChecks(string reference, string sourceId)
        {
            await Engine.SaveTestDefinitionAsync(new TestDefinition
            {
                Kind = TestKinds.MeasureReconcile, Title = "Contoso SQL " + reference, TargetRef = reference,
                ParamsJson = TestSuiteStore.Serialize(new ReconcileRequest
                {
                    SqlSourceId = sourceId, MeasureRef = reference, BlankPolicy = "distinct",
                    Sql = "SELECT Category, SUM(Amount) FROM ContosoSales GROUP BY Category",
                    SqlGrandTotal = "SELECT SUM(Amount) FROM ContosoSales", GroupBy = new[] { "'Product'[Category]" }
                })
            }, "human");
            await Engine.SaveTestDefinitionAsync(new TestDefinition
            {
                Kind = TestKinds.MeasureValue, Title = "Contoso trusted " + reference, TargetRef = reference,
                ParamsJson = TestSuiteStore.Serialize(new MeasureValueRequest { MeasureRef = reference, ExpectedValue = "300" })
            }, "human");
        }
        public void Dispose() { Release.Set(); Engine.Dispose(); Sessions.Dispose(); Release.Dispose(); }
    }

    [Fact]
    public async Task Fx14_Properties_and_Checks_share_a_single_measure_run_and_its_refresh()
    {
        using var f = new Fixture(root); await f.Open();
        const string reference = "measure:Sales/Total Sales";
        TaskCompletionSource? announcement = null;
        // The engine pushes the finished check state (checks/didChange): no longer running, with its marks.
        f.Sessions.Bus.ChecksChanged += change => {
            if (change.States.Any(s => s.MeasureRef == reference && !s.Running && s.LastChecked != null)) announcement?.TrySetResult();
        };
        for (var i = 0; i < 2; i++)
        {
            announcement = new(TaskCreationOptions.RunContinuationsAsynchronously);
            if (i > 0) await f.Engine.GetMeasureMarksAsync(reference, refresh: true);
            var properties = await MeasureMarksTests.Finish(f.Engine, reference);
            await announcement.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var row = Assert.Single((await f.Engine.GetMeasureCheckCacheAsync()).Where(c => c.MeasureRef == reference));
            Assert.Equal(JsonSerializer.Serialize(properties.Marks), JsonSerializer.Serialize(row.Marks));
            Assert.Equal(properties.Marks.Where(m => m.LastRan != null).Max(m => DateTimeOffset.Parse(m.LastRan)), row.LastChecked);
            Assert.Equal(properties.LastChecked, row.LastChecked);
            Assert.False(row.Stale);
        }
    }

    [Fact]
    public async Task Production_door_probes_once_for_both_marks_and_uses_orphan_results()
    {
        using var f = new Fixture(root); await f.Open();
        var sound = await MeasureMarksTests.Finish(f.Engine, "measure:Sales/Total Sales");
        Assert.Equal("passed", sound.Marks.Single(m => m.Name == "Filters").State);
        Assert.Equal("passed", sound.Marks.Single(m => m.Name == "Contexts").State);
        var memberQueries = f.Queries.Where(q => q.Contains("EVALUATE TOPN(50, VALUES(")).ToArray();
        Assert.NotEmpty(memberQueries);
        Assert.Equal(memberQueries.Length, memberQueries.Distinct().Count());
        f.Orphans = 4;
        await f.Engine.GetMeasureMarksAsync("measure:Sales/Total Sales", refresh: true);
        var broken = await MeasureMarksTests.Finish(f.Engine, "measure:Sales/Total Sales");
        Assert.Contains(broken.Marks.Single(m => m.Name == "Filters").Cells, c => c.After == "4");
    }

    [Fact]
    public async Task Saved_SQL_and_trusted_tests_run_through_the_existing_runners()
    {
        using var f = new Fixture(root); await f.Open();
        var source = SqlSourceRegistry.Save(null, "Contoso warehouse", "contoso-sql.database.windows.net", "Contoso", "azcli", null);
        await f.Engine.SaveTestDefinitionAsync(new TestDefinition
        {
            Kind = TestKinds.MeasureReconcile, Title = "Contoso SQL", TargetRef = "measure:Sales/Total Sales",
            ParamsJson = TestSuiteStore.Serialize(new ReconcileRequest
            {
                SqlSourceId = source.Id, MeasureRef = "measure:Sales/Total Sales", BlankPolicy = "distinct",
                Sql = "SELECT Category, SUM(Amount) FROM ContosoSales GROUP BY Category", SqlGrandTotal = "SELECT SUM(Amount) FROM ContosoSales",
                GroupBy = new[] { "'Product'[Category]" }
            })
        }, "human");
        await f.Engine.SaveTestDefinitionAsync(new TestDefinition
        {
            Kind = TestKinds.MeasureValue, Title = "Contoso trusted total", TargetRef = "measure:Sales/Total Sales",
            ParamsJson = TestSuiteStore.Serialize(new MeasureValueRequest { MeasureRef = "measure:Sales/Total Sales", ExpectedValue = "300" })
        }, "human");
        var result = await MeasureMarksTests.Finish(f.Engine, "measure:Sales/Total Sales");
        Assert.True(result.Marks.Single(m => m.Name == "SQL").State == "passed",
            JsonSerializer.Serialize(result) + "\n" + string.Join("\n", f.Queries));
        Assert.Equal("passed", result.Marks.Single(m => m.Name == "Trusted number").State);
        Assert.Equal(2, f.SqlCalls);
        await f.Sessions.Current.MutateAsync("human", "Contoso saved checks after edit", m => m.Tables["Sales"].Measures["Total Sales"].Expression += " + 50");
        var edited = await MeasureMarksTests.Finish(f.Engine, "measure:Sales/Total Sales");
        foreach (var name in new[] { "SQL", "Trusted number" })
        {
            var mark = edited.Marks.Single(m => m.Name == name);
            Assert.Equal("suspect", mark.State);
            Assert.Contains(mark.Cells, c => c.Before == "300" && c.After == "350");
        }
    }

    [Fact]
    public async Task An_edit_automatically_compares_before_and_after_without_waiting_for_queries()
    {
        using var f = new Fixture(root); await f.Open();
        await MeasureMarksTests.Finish(f.Engine, "measure:Sales/Total Sales");
        f.Release.Reset();
        var edit = f.Sessions.Current.MutateAsync("human", "Change Contoso sales", m => m.Tables["Sales"].Measures["Total Sales"].Expression = "SUM('Sales'[Amount]) + 50");
        await edit.WaitAsync(TimeSpan.FromSeconds(2));
        f.Release.Set();
        var result = await MeasureMarksTests.Finish(f.Engine, "measure:Sales/Total Sales");
        var mark = result.Marks.Single(m => m.Name == "Equivalence");
        Assert.Equal("suspect", mark.State);
        Assert.Contains("moved", mark.Sentence);
        Assert.Contains(mark.Cells, c => c.Before == "300" && c.After == "350");
        Assert.DoesNotContain("wrong", mark.Sentence, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task One_edit_check_has_one_baseline_history_row_including_its_dependents()
    {
        using var f = new Fixture(root); await f.Open(2);
        await f.Sessions.Current.MutateAsync("human", "Contoso edit", m => m.Tables["Sales"].Measures["Total Sales"].Expression += " + 50");
        await MeasureMarksTests.Finish(f.Engine, "measure:Sales/Total Sales");
        await MeasureMarksTests.Finish(f.Engine, "measure:Sales/Sales Copy 1");
        var history = await f.Engine.GetPlanHistoryAsync(limit: 100);
        var row = Assert.Single(history.Rows.Where(r => r.SessionId == f.Sessions.Current.Id && r.Record?.Op == "compare_baseline"));
        using var evidence = JsonDocument.Parse(row.Record.Evidence);
        Assert.Equal(2, evidence.RootElement.GetProperty("measures").GetInt32());
    }

    [Fact]
    public async Task A_dependency_edit_and_undo_restore_its_downstream_measure()
    {
        using var f = new Fixture(root); await f.Open(2);
        await MeasureMarksTests.Finish(f.Engine, "measure:Sales/Sales Copy 1");
        await f.Sessions.Current.MutateAsync("agent", "Change Contoso dependency", m => m.Tables["Sales"].Measures["Total Sales"].Expression += " + 50");
        var changed = await MeasureMarksTests.Finish(f.Engine, "measure:Sales/Sales Copy 1");
        Assert.Equal("suspect", changed.Marks.Single(m => m.Name == "Equivalence").State);
        await f.Sessions.Current.UndoAsync("human");
        var undone = await MeasureMarksTests.Finish(f.Engine, "measure:Sales/Sales Copy 1");
        var restored = undone.Marks.Single(m => m.Name == "Equivalence");
        Assert.NotEqual("suspect", restored.State);
        Assert.Empty(restored.Cells);
    }

    [Fact]
    public async Task First_edit_without_a_prior_read_still_gets_its_before_numbers()
    {
        using var f = new Fixture(root); await f.Open();
        await f.Sessions.Current.MutateAsync("human", "First Contoso edit", m => m.Tables["Sales"].Measures["Total Sales"].Expression += " + 50");
        var result = await MeasureMarksTests.Finish(f.Engine, "measure:Sales/Total Sales");
        Assert.Contains(result.Marks.Single(m => m.Name == "Equivalence").Cells, c => c.Before == "300" && c.After == "350");
    }

    [Fact]
    public async Task Rewrite_that_keeps_numbers_says_nothing_moved()
    {
        using var f = new Fixture(root); await f.Open();
        await f.Sessions.Current.MutateAsync("human", "Rewrite Contoso sales", m => m.Tables["Sales"].Measures["Total Sales"].Expression += " + 0");
        var result = await MeasureMarksTests.Finish(f.Engine, "measure:Sales/Total Sales");
        var mark = result.Marks.Single(m => m.Name == "Equivalence");
        Assert.Equal("passed", mark.State);
        Assert.Contains("nothing moved", mark.Sentence, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Automatic_captures_do_not_evict_a_persons_eight_manual_captures()
    {
        using var f = new Fixture(root); await f.Open();
        string? first = null;
        for (var i = 0; i < 8; i++)
        {
            var capture = await f.Engine.CaptureBaselineAsync("measure:Sales/Total Sales", null, null, false, 1, 8, null, "human");
            first ??= capture.CaptureId;
        }
        await f.Sessions.Current.MutateAsync("human", "Contoso automatic comparison", m => m.Tables["Sales"].Measures["Total Sales"].Expression += " + 0");
        await MeasureMarksTests.Finish(f.Engine, "measure:Sales/Total Sales");
        var result = await f.Engine.CompareBaselineAsync(first, null, "human");
        Assert.Equal("ok", result.Status);
    }

    [Fact]
    public async Task A_refresh_during_an_edit_keeps_the_automatic_comparison()
    {
        using var f = new Fixture(root); await f.Open();
        f.Release.Reset();
        await f.Sessions.Current.MutateAsync("human", "Contoso edit then refresh", m => m.Tables["Sales"].Measures["Total Sales"].Expression += " + 50");
        await f.Engine.GetMeasureMarksAsync("measure:Sales/Total Sales", refresh: true);
        f.Release.Set();
        var result = await MeasureMarksTests.Finish(f.Engine, "measure:Sales/Total Sales");
        Assert.Equal("suspect", result.Marks.Single(m => m.Name == "Equivalence").State);
    }

    [Fact]
    public async Task A_connection_to_another_model_does_not_measure_this_edit()
    {
        using var f = new Fixture(root); await f.Open();
        f.Sessions.Current.LiveOrigin = new LiveOrigin("another-contoso-model", "Contoso Sales", null);
        var result = await MeasureMarksTests.Finish(f.Engine, "measure:Sales/Total Sales");
        Assert.Equal("not run", result.Marks.Single(m => m.Name == "Filters").State);
        Assert.Empty(f.Queries);
    }

    [Fact]
    public async Task A_model_swap_never_returns_the_previous_models_marks()
    {
        using var f = new Fixture(root); await f.Open();
        await MeasureMarksTests.Finish(f.Engine, "measure:Sales/Total Sales");
        await f.Engine.CreateModelAsync("Contoso replacement", 1604, discardUnsaved: true);
        await f.Sessions.Current.MutateAsync("human", "Contoso replacement measure", m => m.AddTable("Sales").AddMeasure("Total Sales", "12"));
        var result = await MeasureMarksTests.Finish(f.Engine, "measure:Sales/Total Sales");
        Assert.All(result.Marks, m => Assert.Equal("not run", m.State));
    }

    [Fact]
    public async Task Ordinary_queries_do_not_inherit_the_formula_snapshot()
    {
        using var f = new Fixture(root); await f.Open();
        await MeasureMarksTests.Finish(f.Engine, "measure:Sales/Total Sales");
        const string query = "EVALUATE ROW(\"Contoso ordinary read\", 1)";
        await f.Engine.RunDaxAsync(query, 2, "human");
        Assert.Equal(query, f.Queries.Last());
    }

    [Theory]
    [InlineData("SUM('Sales'[Amount])", "Filters")]
    [InlineData("SUM('Sales'[Amount]) /* Contoso error */", "Contexts")]
    public async Task Production_marks_find_the_broken_Contoso_measure(string expression, string markName)
    {
        using var f = new Fixture(root); await f.Open();
        if (markName == "Filters") f.Orphans = 4;
        await f.Sessions.Current.MutateAsync("human", "Break Contoso measure", m => m.Tables["Sales"].Measures["Total Sales"].Expression = expression);
        var result = await MeasureMarksTests.Finish(f.Engine, "measure:Sales/Total Sales");
        Assert.Equal("suspect", result.Marks.Single(m => m.Name == markName).State);
        var memberQueries = f.Queries.Where(q => q.Contains("EVALUATE TOPN(50, VALUES(")).ToArray();
        Assert.NotEmpty(memberQueries);
        Assert.Equal(memberQueries.Length, memberQueries.Distinct().Count());
    }

    [Fact]
    public async Task A_measure_rename_compares_the_same_measure_before_and_after()
    {
        using var f = new Fixture(root); await f.Open(2);
        await f.Sessions.Current.MutateAsync("human", "Rename Contoso sales", m => m.Tables["Sales"].Measures["Total Sales"].Name = "Contoso Total Sales");
        var result = await MeasureMarksTests.Finish(f.Engine, "measure:Sales/Contoso Total Sales");
        Assert.Equal("passed", result.Marks.Single(m => m.Name == "Equivalence").State);
    }

    [Fact]
    public async Task A_constant_measure_still_gets_contexts_from_its_home_table()
    {
        using var f = new Fixture(root); await f.Open();
        await f.Sessions.Current.MutateAsync("human", "Contoso constant", m => m.Tables["Sales"].AddMeasure("Contoso constant", "300"));
        var result = await MeasureMarksTests.Finish(f.Engine, "measure:Sales/Contoso constant");
        Assert.Equal("passed", result.Marks.Single(m => m.Name == "Contexts").State);
    }

    [Fact]
    public async Task A_saved_candidate_formula_cannot_pass_as_the_measures_trusted_number()
    {
        using var f = new Fixture(root); await f.Open();
        await f.Engine.SaveTestDefinitionAsync(new TestDefinition
        {
            Kind = TestKinds.MeasureValue, TargetRef = "measure:Sales/Total Sales", Title = "Contoso separate formula",
            ParamsJson = TestSuiteStore.Serialize(new MeasureValueRequest { ExpectedValue = "1", ExpressionDax = "1" })
        }, "human");
        var result = await MeasureMarksTests.Finish(f.Engine, "measure:Sales/Total Sales");
        var mark = result.Marks.Single(m => m.Name == "Trusted number");
        Assert.Equal("not run", mark.State);
        Assert.Contains("separate formula", mark.Sentence);
    }

    [Fact]
    public async Task A_tagless_saved_check_follows_its_identity_after_a_rename()
    {
        using var f = new Fixture(root); await f.Open();
        await f.Sessions.Current.MutateAsync("human", "Contoso legacy identity", m => m.Tables["Sales"].Measures["Total Sales"].LineageTag = "");
        var source = SqlSourceRegistry.Save(null, "Contoso warehouse", "contoso-sql.database.windows.net", "Contoso", "azcli", null);
        await f.SaveChecks("measure:Sales/Total Sales", source.Id);
        await f.Sessions.Current.MutateAsync("human", "Rename Contoso legacy measure", m => m.Tables["Sales"].Measures["Total Sales"].Name = "Contoso renamed sales");
        var result = await MeasureMarksTests.Finish(f.Engine, "measure:Sales/Contoso renamed sales");
        Assert.Equal("passed", result.Marks.Single(m => m.Name == "Trusted number").State);
    }

    [Fact]
    public async Task A_saved_formula_that_still_matches_the_measure_checks_its_trusted_number()
    {
        using var f = new Fixture(root); await f.Open();
        await f.Engine.SaveTestDefinitionAsync(new TestDefinition
        {
            Kind = TestKinds.MeasureValue, TargetRef = "measure:Sales/Total Sales", Title = "Contoso current formula",
            ParamsJson = TestSuiteStore.Serialize(new MeasureValueRequest
            { ExpectedValue = "300", ExpressionDax = "SUM('Sales'[Amount])" })
        }, "human");
        var result = await MeasureMarksTests.Finish(f.Engine, "measure:Sales/Total Sales");
        Assert.Equal("passed", result.Marks.Single(m => m.Name == "Trusted number").State);
    }

    [Fact]
    public async Task Each_committed_edit_publishes_its_own_numbers_in_the_background()
    {
        using var f = new Fixture(root); await f.Open();
        var reports = new ConcurrentQueue<MeasureMarksResult>();
        f.Sessions.Bus.Activity += e => { if (e.Kind == "measure_marks" && e.Result is MeasureMarksResult r) reports.Enqueue(r); };
        var first = await f.Sessions.Current.MutateAsync("human", "Contoso first edit", m => m.Tables["Sales"].Measures["Total Sales"].Expression += " + 50");
        var second = await f.Sessions.Current.MutateAsync("human", "Contoso second edit", m => m.Tables["Sales"].Measures["Total Sales"].Expression = "SUM('Sales'[Amount])");
        var watch = Stopwatch.StartNew();
        while (reports.Count < 2 && watch.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(10);
        Assert.Contains(reports, r => r.Revision == first && r.Marks.Single(m => m.Name == "Equivalence").Cells.Any(c => c.Before == "300" && c.After == "350"));
        Assert.Contains(reports, r => r.Revision == second && r.Marks.Single(m => m.Name == "Equivalence").Cells.Any(c => c.Before == "350" && c.After == "300"));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(50)]
    public async Task First_result_is_under_two_seconds_on_the_fixture_and_a_fifty_measure_model(int count)
    {
        using var f = new Fixture(root); await f.Open(count);
        var refs = (await f.Engine.ListMeasuresAsync()).Select(m => m.Ref).ToArray();
        var source = SqlSourceRegistry.Save(null, "Contoso warehouse", "contoso-sql.database.windows.net", "Contoso", "azcli", null);
        foreach (var reference in refs) await f.SaveChecks(reference, source.Id);
        var watch = Stopwatch.StartNew();
        await f.Sessions.Current.MutateAsync("human", "Time Contoso marks", m => m.Tables["Sales"].Measures["Total Sales"].Expression += " + 0");
        var first = await f.Engine.GetMeasureMarksAsync("measure:Sales/Total Sales");
        var snapshotMs = watch.ElapsedMilliseconds;
        while (!first.Marks.Any(m => m.LastRan != null) && first.Running && watch.ElapsedMilliseconds < 2000)
        {
            await Task.Delay(1);
            first = await f.Engine.GetMeasureMarksAsync("measure:Sales/Total Sales");
        }
        var firstMs = watch.ElapsedMilliseconds;
        Assert.True(firstMs < 2000 && first.Marks.Any(m => m.LastRan != null), "First measured result took " + firstMs + "ms.");
        foreach (var reference in refs) await f.Engine.GetMeasureMarksAsync(reference);
        foreach (var reference in refs)
        {
            var result = await MeasureMarksTests.Finish(f.Engine, reference);
            Assert.All(result.Marks.Take(5), m => Assert.True(m.State == "passed", JsonSerializer.Serialize(result)));
        }
        output.WriteLine($"Contoso measures={count}; first snapshot={snapshotMs}ms; first result={firstMs}ms; all five={watch.ElapsedMilliseconds}ms; queries={f.Queries.Count}");
        Assert.Equal(8, first.Marks.Length);
    }

    [Fact]
    public async Task RPC_and_MCP_return_the_same_marks_on_one_session()
    {
        using var f = new Fixture(root); await f.Open();
        await MeasureMarksTests.Finish(f.Engine, "measure:Sales/Total Sales");
        var pipe = TestPipe.Mint("marks-");
        const string challenge = "contoso-marks-ui-challenge-0123456789abcdef";
        using var server = new RpcServer(f.Sessions, f.Engine, pipe, challenge);
        using var stop = new CancellationTokenSource();
        var serving = server.RunAsync(stop.Token);
        using var remote = await RemoteEngine.ConnectAsync(pipe);
        using var human = await PlanQueueKit.Client.ConnectAsync(pipe, "human", challenge);
        var toServer = new Pipe(); var toClient = new Pipe();
        var builder = Host.CreateApplicationBuilder(Array.Empty<string>());
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<IEngine>(remote);
        builder.Services.AddMcpServer().WithStreamServerTransport(toServer.Reader.AsStream(), toClient.Writer.AsStream())
            .WithToolsFromAssembly(typeof(McpTools).Assembly)
            .WithRequestFilters(filters => McpDoorFilters.Add(filters, remote));
        using var host = builder.Build(); await host.StartAsync();
        try
        {
            await using var client = await McpClient.CreateAsync(new StreamClientTransport(toServer.Writer.AsStream(), toClient.Reader.AsStream()));
            var rpc = await human.InvokeAsync<MeasureMarksResult>("getMeasureMarks", "measure:Sales/Total Sales", null, false, "agent");
            var mcp = await client.CallToolAsync("get_measure_marks", new Dictionary<string, object?> { ["measureRef"] = "measure:Sales/Total Sales" });
            Assert.NotEqual(true, mcp.IsError);
            var text = string.Join("", mcp.Content.OfType<TextContentBlock>().Select(c => c.Text));
            var result = JsonSerializer.Deserialize<MeasureMarksResult>(text, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            Assert.Equal(JsonSerializer.Serialize(rpc), JsonSerializer.Serialize(result));
        }
        finally
        {
            await host.StopAsync(); stop.Cancel(); try { await serving; } catch (OperationCanceledException) { }
        }
    }
}
