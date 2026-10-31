using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.Decisions;
using Xunit;

namespace Semanticus.Tests;

// n171 (rc34 and rc35 tester Next lists, for 1.7.1).
// rc34 Next 3: the Checks page row of a trusted number ("Total Quantity: All rows") read "Not run yet" while the measure's card
// showed Trusted number passed after several runs. The run checks the saved number as a mark; the page read only the results
// of a Checks-page run. The mark's result is now the page's result too, so the row and the card read one answer.
// rc34 Next 5: the Model page in Properties showed the plan with an active Run while a run was going on the Checks page. Every
// check-state push now says whether a run is going on, with its count, and the run's end is pushed, so every run plan shows it.
// rc35 Next 2: the plan counted a measure as "changed" with no edit, after its card check and Pro advice Turn on, and still
// after the run checked it.
[Collection("restore-root")]
public sealed class CheckedN171Tests : IDisposable
{
    private const string Reference = "measure:Sales/Total Sales";
    private readonly string safeRoot;
    private readonly string root = Path.Combine(Path.GetTempPath(), "n171-" + Guid.NewGuid().ToString("N")[..8]);
    public CheckedN171Tests(RestoreRootFixture fixture) { safeRoot = fixture.Root; ConnectionRegistry.RootOverride = root; }
    public void Dispose()
    {
        ConnectionRegistry.RootOverride = safeRoot;
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }

    private static async Task<MeasureRunResult> RunAll(LocalEngine engine)
        => await engine.RunMeasurePlanAsync((await engine.PlanMeasureRunAsync(new MeasureRunRequest { Cap = 25 })).Id);

    private static async Task<MeasureCheckCache[]> Settled(LocalEngine engine)
    {
        var clock = Stopwatch.StartNew();
        while (true)
        {
            var states = await engine.GetMeasureCheckCacheAsync();
            if (!states.Any(s => s.Running || s.Marks.Any(m => m.Pending)) || clock.Elapsed > TimeSpan.FromSeconds(20)) return states;
            await Task.Delay(20);
        }
    }

    private static async Task<string[]> Changed(LocalEngine engine) =>
        (await engine.PlanMeasureRunAsync(new MeasureRunRequest { Cap = 25 })).Picks.Where(p => p.Group == "changed").Select(p => p.MeasureRef).ToArray();

    [Fact]
    public async Task A_trusted_number_the_run_checked_reads_the_same_on_the_Checks_page_row()
    {
        using var f = new MeasureMarksDoorTests.Fixture(root); await f.Open(2);
        var saved = await f.Engine.SaveTestDefinitionAsync(new TestDefinition
        {
            Kind = TestKinds.MeasureValue, Title = "Total Sales: All rows", TargetRef = Reference,
            ParamsJson = TestSuiteStore.Serialize(new MeasureValueRequest { MeasureRef = Reference, ExpectedValue = "300" })
        }, "human");
        var activity = new List<string>();
        f.Sessions.Bus.Activity += e => { lock (activity) activity.Add(e.Kind); };
        Assert.False((await RunAll(f.Engine)).Stopped);
        await Settled(f.Engine);
        var mark = (await f.Engine.GetMeasureCheckCacheAsync()).Single(s => s.MeasureRef == Reference).Marks.Single(m => m.Name == "Trusted number");
        Assert.Equal("verified", mark.Verdict);
        var page = await f.Engine.GetChecksPageAsync();
        var id = page.Numbers.Definitions.Single(d => d.TargetRef == Reference).Id;
        Assert.True(page.Results.TryGetValue(id, out var result), "the Checks page row reads Not run yet while the card reads Trusted number passed");
        Assert.Equal(Verdict.Pass, result.Verdict);
        Assert.True(page.ResultTimes.ContainsKey(id), "the row has no time for its result");
        lock (activity) Assert.Contains("checks_page_changed", activity);
    }

    [Fact]
    public async Task Every_push_says_whether_a_run_is_going_on_and_the_end_of_the_run_is_pushed()
    {
        using var f = new MeasureMarksDoorTests.Fixture(root); await f.Open(3);
        var pushes = new List<CheckStateChange>();
        f.Sessions.Bus.ChecksChanged += c => { lock (pushes) pushes.Add(c); };
        var plan = await f.Engine.PlanMeasureRunAsync(new MeasureRunRequest { Cap = 25 });
        Assert.False((await f.Engine.RunMeasurePlanAsync(plan.Id)).Stopped);
        CheckStateChange[] seen;
        lock (pushes) seen = pushes.ToArray();
        Assert.NotEmpty(seen);
        Assert.All(seen, c => Assert.NotNull(c.Run));
        var during = seen.Where(c => c.Run.Active).ToArray();
        Assert.True(during.Length > 0, "no push said a run is going on");
        Assert.All(during, c => Assert.Equal(plan.Picks.Length, c.Run.Total));
        Assert.True(during.Select(c => c.Run.Done).SequenceEqual(during.Select(c => c.Run.Done).OrderBy(d => d)), "the count went back");
        Assert.Equal(plan.Picks.Length, during.Max(c => c.Run.Done));
        Assert.False(seen.Last().Run.Active, "the end of the run was not pushed");
        // A push outside a run says no run is going on.
        lock (pushes) pushes.Clear();
        await f.Engine.GetMeasureMarksAsync(Reference, null, true);
        await Settled(f.Engine);
        lock (pushes) Assert.All(pushes, c => Assert.False(c.Run.Active));
    }

    [Fact]
    public async Task A_card_check_and_Turn_on_never_make_a_measure_changed_and_the_run_clears_it()
    {
        using var f = new MeasureMarksDoorTests.Fixture(root); await f.Open(3);
        f.Engine.UseDecisionsForTest(new DecisionService(new CheckedFx29cTests.Relay()));
        Assert.False((await RunAll(f.Engine)).Stopped);
        await Settled(f.Engine);
        Assert.Empty(await Changed(f.Engine));
        // The card's Check now.
        await f.Engine.GetMeasureMarksAsync(Reference, null, true);
        await Settled(f.Engine);
        Assert.Empty(await Changed(f.Engine));
        // Pro advice Turn on, then the card reads again (its recheck).
        var consent = await f.Engine.GetConsentAsync();
        Assert.True((await f.Engine.SetConsentAsync(consent.FieldsInForce.ToArray(), consent.SchemaVersionInForce, "human")).Current);
        await f.Engine.GetMeasureMarksAsync(Reference);
        await Settled(f.Engine);
        Assert.Empty(await Changed(f.Engine));
        Assert.False((await RunAll(f.Engine)).Stopped);
        await Settled(f.Engine);
        Assert.Empty(await Changed(f.Engine));
    }
}
