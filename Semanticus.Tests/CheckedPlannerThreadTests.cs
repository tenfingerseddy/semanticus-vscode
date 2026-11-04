using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests;

/// <summary>cx4 (the 1.7.4-rc2 tester, 3 Nov): after a live open the window showed the old model's figures for 5 to 8 s. One cause was the
/// measure checks' planner on the engine's one model thread. Every open starts it two or three times (the tree marks, the Properties card,
/// the Checks page), and each call held the thread 2 to 4 s on the test model (12 measures). Every read the window makes after an open
/// (the session, the tree, Properties) waited behind it.
///
/// The cause, proved with a trace on that model: its structure text (tables, columns, partitions, roles) is 8 MB, and the planner wrote
/// all of it into JSON again and hashed it again once per measure, inside the model read: about 160 ms a measure.</summary>
public sealed class CheckedPlannerThreadTests
{
    private sealed class Fixture : IDisposable
    {
        internal readonly string Root = Path.Combine(Path.GetTempPath(), "contoso-planner-" + Guid.NewGuid().ToString("N")[..8]);
        internal readonly SessionManager Sessions = new();
        internal readonly LocalEngine Engine;
        internal Fixture() => Engine = new(Sessions, TestEntitlements.Pro, Root);

        /// <summary>A model with a large partition query (an "Enter data" table holds its rows in its query), and `count` measures.</summary>
        internal async Task Open(int count, int queryChars)
        {
            await Engine.CreateModelAsync("Contoso", 1604);
            await Sessions.Current.MutateAsync("human", "Contoso fixture", m =>
            {
                var t = m.AddTable("Contoso Sales");
                t.AddMPartition("Contoso Rows", "let Source = \"" + new string('x', queryChars) + "\" in Source // Caf\u00e9 <b> & \\ \u2014 \U0001F600");
                t.AddMeasure("Base", "100");
                for (var i = 1; i < count; i++) t.AddMeasure("Measure " + i, "[Base] + " + i);
            });
        }

        /// <summary>How long a small read waits on the model thread while the planner runs: what the window's session read waits after an open.</summary>
        internal async Task<(long probeWaitMs, long plannerMs)> ProbeBehindPlanner(Func<Task> planner)
        {
            var hold = new ManualResetEventSlim(false);
            var holding = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            // Hold the thread, so the planner's read is queued before the probe's (the dispatcher runs reads in order).
            var blocker = Sessions.Current.ReadAsync(_ => { holding.TrySetResult(); hold.Wait(); return 0; });
            await holding.Task;
            var total = Stopwatch.StartNew();
            var run = planner();
            await Task.Delay(1000);   // the planner's own work before its model read is a few ms; on a loaded machine this is still ample
            var probe = Stopwatch.StartNew();
            var probeRead = Sessions.Current.ReadAsync(_ => probe.ElapsedMilliseconds);
            hold.Set();
            await blocker;
            var waited = await probeRead;
            await run;
            return (waited, total.ElapsedMilliseconds - 1000);
        }

        public void Dispose() { Engine.Dispose(); Sessions.Dispose(); if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }

    [Fact]
    public async Task Cx4_the_planner_does_not_hold_the_model_thread_for_seconds_on_a_model_with_a_large_structure()
    {
        using var f = new Fixture();
        await f.Open(count: 24, queryChars: 6_000_000);
        await f.Engine.GetMeasureCheckCacheAsync();   // warm: the first call loads code and the check records
        var (waited, plannerMs) = await f.ProbeBehindPlanner(() => f.Engine.GetMeasureCheckCacheAsync());
        // Before the fix: the probe waited for every measure's JSON and hash (several seconds here). After: one pass over the structure.
        Assert.True(waited < 1500, $"a small read waited {waited} ms behind the planner (planner {plannerMs} ms); the window's reads wait as long after an open");
    }

    [Fact]
    public async Task Cx4_a_plan_does_not_hold_the_model_thread_for_seconds_either()
    {
        using var f = new Fixture();
        await f.Open(count: 24, queryChars: 6_000_000);
        await f.Engine.PlanMeasureRunAsync();
        var (waited, plannerMs) = await f.ProbeBehindPlanner(() => f.Engine.PlanMeasureRunAsync());
        Assert.True(waited < 1500, $"a small read waited {waited} ms behind the plan (plan {plannerMs} ms)");
    }

    /// <summary>The fix computes the same fingerprints. A changed fingerprint would mark every saved check result stale once after the update:
    /// a person's checks would read "not checked since the change" with no change made. Pinned on a fixed model, computed by the code before cx4.</summary>
    [Fact]
    public async Task Cx4_the_check_fingerprints_are_the_same_as_before()
    {
        using var f = new Fixture();
        await f.Open(count: 3, queryChars: 64);
        var method = typeof(LocalEngine).GetMethod("MeasureRunCandidatesAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(method);
        var candidates = await (Task<MeasureRunCandidate[]>)method!.Invoke(f.Engine, new object?[] { f.Sessions.Current, null, false })!;
        var seen = candidates.Select(c => c.MeasureRef + "=" + c.Inputs + "|" + MeasureRunPlanner.Digest(c.InputSnapshot!.Value.GetRawText())).ToArray();
        Assert.Equal(PinnedInputs, seen);
        // The plan's picks carry the fingerprints the planner computes with the engine's own extra checks (both doors run the plan by them).
        var extras = typeof(LocalEngine).GetProperty("MeasureRunExtras", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)!.GetValue(f.Engine);
        var own = await (Task<MeasureRunCandidate[]>)method.Invoke(f.Engine, new object?[] { f.Sessions.Current, extras, false })!;
        var plan = await f.Engine.PlanMeasureRunAsync();
        Assert.Equal(own.Select(c => c.Inputs).OrderBy(x => x, StringComparer.Ordinal), plan.Picks.Select(p => p.Inputs).OrderBy(x => x, StringComparer.Ordinal));
    }

    // Written by the code before cx4 (fix/cx3 9fc69e6ac) on the fixture above, count 3, queryChars 64 (the same twice). Measure=inputs|digest of the input snapshot.
    private static readonly string[] PinnedInputs =
    {
        "measure:Contoso Sales/Base=9E9468FBE18CCEA4BF2756B7CF25670D12B6507E8787C5771A6E1F082BD60A24|4D351F1FFDBC33E0F97FE803310CCC6C3613C60B4867DE6718A5C2C2D099446C",
        "measure:Contoso Sales/Measure 1=4A54F1B296AD2BBABA4694043CAF3055D0AF83990C60A87AD5878A2E15E75F63|4544DC07F50D17744EEF2E4BE4F4578A866817B8859023C0092FCF6D32E48358",
        "measure:Contoso Sales/Measure 2=584F9ACCC36FC1B6E0F803E30467E74A6759DD692FB9CDB3CF3E368FB7BEF807|BDB23CF7A8F131579F3FDB075BD8A9D5E97805C083EE9E81E297345CD23B8C9E",
    };
}
