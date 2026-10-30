using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests;

// v-rc28 (verifier of the joined 1.7 state layer). Attacks on the one engine check state: every door reads the same finished
// copy during and after a run, after Keep, after an edit of a used measure, after Undo, and after Stop; a run of 14 measures
// with slow live queries finishes while the doors keep answering.
// Stop_keeps_... fails on 77a1ebb2e: the measure being checked when Stop lands stays Running with every mark pending, on every
// door, for good (30 s seen, 3 of 3 runs). Cause: the mark run's finally calls FinishRunCheckState(run, latest: ... &&
// MarkRunCurrent(run)), and MarkRunCurrent is false once the plan token is cancelled, so the record is never closed.
[Collection("restore-root")]
public sealed class CheckedVRc28Tests : IDisposable
{
    private readonly string safeRoot;
    private readonly string root = Path.Combine(Path.GetTempPath(), "vrc28-" + Guid.NewGuid().ToString("N")[..8]);
    public CheckedVRc28Tests(RestoreRootFixture fixture) { safeRoot = fixture.Root; ConnectionRegistry.RootOverride = root; }
    public void Dispose()
    {
        ConnectionRegistry.RootOverride = safeRoot;
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }

    private static MeasureMark Mark(MeasureCheckCache state, string name) => state?.Marks.SingleOrDefault(m => m.Name == name);

    // The three readers of one measure: the tree and Checks rows (RPC cache), the assistant (MCP cache) and the card (MCP marks).
    private static async Task<(MeasureCheckCache Row, MeasureCheckCache Agent, MeasureMarksResult Card)> Doors(LocalEngine engine, string reference)
    {
        var row = (await engine.GetMeasureCheckCacheAsync().WaitAsync(TimeSpan.FromSeconds(10))).Single(c => c.MeasureRef == reference);
        var agent = (await McpTools.GetMeasureCheckCache(engine).WaitAsync(TimeSpan.FromSeconds(10))).Single(c => c.MeasureRef == reference);
        var card = await McpTools.GetMeasureMarks(engine, reference).WaitAsync(TimeSpan.FromSeconds(10));
        return (row, agent, card);
    }

    private static void Agree((MeasureCheckCache Row, MeasureCheckCache Agent, MeasureMarksResult Card) d, string why)
    {
        // A door read later can be newer; the same version must say the same thing.
        if (d.Row.Version == d.Agent.Version) Assert.True(d.Row.Verdict == d.Agent.Verdict, $"{why}: row {d.Row.Verdict} vs assistant {d.Agent.Verdict} at v{d.Row.Version}");
        if (d.Card.Version == d.Row.Version)
            foreach (var mark in d.Row.Marks.Where(m => MeasureCheckVerdict.Seven.Contains(m.Name)))
            {
                var other = d.Card.Marks.SingleOrDefault(m => m.Name == mark.Name);
                Assert.True(other != null && (mark.Applies, mark.Verdict) == (other.Applies, other.Verdict),
                    $"{why}: {mark.Name} row ({mark.Applies}, {mark.Verdict}) vs card ({other?.Applies}, {other?.Verdict}) at v{d.Row.Version}");
            }
    }

    private static async Task<MeasureCheckCache> Settled(LocalEngine engine, string reference, Func<MeasureCheckCache, bool> done)
    {
        var clock = Stopwatch.StartNew();
        while (true)
        {
            var state = (await engine.GetMeasureCheckCacheAsync()).Single(c => c.MeasureRef == reference);
            if (!state.Running && done(state)) return state;
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(20), "The check state did not settle: "
                + string.Join(", ", state.Marks.Select(m => $"{m.Name}:{m.State}:{m.ReasonCode}:{m.Verdict}")));
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task A_Keep_on_a_dependent_never_hides_a_number_a_later_edit_of_a_used_measure_moved()
    {
        using var f = new MeasureMarksDoorTests.Fixture(root); await f.Open();
        await f.Sessions.Current.MutateAsync("human", "Contoso chain", m =>
        {
            m.Tables["Sales"].AddMeasure("Level One", "[Total Sales]");
            m.Tables["Sales"].AddMeasure("Level Two", "[Level One]");
        });
        const string two = "measure:Sales/Level Two";
        await MeasureMarksTests.Finish(f.Engine, two);
        // An edit one level down that moves nothing: Equivalence passes. Kane keeps it.
        await f.Sessions.Current.MutateAsync("human", "Contoso level one", m => m.Tables["Sales"].Measures["Level One"].Expression = "[Total Sales] * 1");
        var same = await Settled(f.Engine, two, s => Mark(s, "Equivalence") is { State: not "not run" } e && e.Inputs != null);
        var kept = await f.Engine.KeepMeasureEditAsync(two);
        Assert.Equal("kept", Mark(kept, "Equivalence").ReasonCode);
        Agree(await Doors(f.Engine, two), "after Keep");
        // Two levels down, an edit moves the numbers. The Keep was on other inputs, so every door shows Needs review.
        await f.Sessions.Current.MutateAsync("human", "Contoso base", m => m.Tables["Sales"].Measures["Total Sales"].Expression += " + 50");
        var moved = await Settled(f.Engine, two, s => Mark(s, "Equivalence") is { State: "suspect" });
        Assert.NotEqual(kept.Keep.Inputs, Mark(moved, "Equivalence").Inputs);
        var d = await Doors(f.Engine, two);
        Agree(d, "after a used-measure edit");
        foreach (var verdict in new[] { d.Row.Verdict, d.Agent.Verdict })
            Assert.True(verdict == "review", $"a moved number after a Keep reads {verdict}");
        Assert.Equal("review", d.Card.Marks.Single(m => m.Name == "Equivalence").Verdict);
        // Undo the base edit: back to the kept inputs, the Keep holds again and nothing reads review.
        await f.Sessions.Current.UndoAsync("human");
        var back = await Settled(f.Engine, two, s => Mark(s, "Equivalence")?.State != "suspect");
        var b = await Doors(f.Engine, two);
        Agree(b, "after Undo");
        Assert.NotEqual("review", b.Row.Verdict);
        _ = back;
    }

    [Fact]
    public async Task A_run_of_14_measures_with_slow_live_queries_finishes_and_every_door_answers_during_it()
    {
        using var f = new MeasureMarksDoorTests.Fixture(root); await f.Open(14);
        var queries = 0;
        f.Engine.SetLiveConnectionForTest(LiveConnection.ForTest("xmla", "contoso", "Contoso Sales", q =>
        {
            Interlocked.Increment(ref queries);
            Thread.Sleep(40);
            return f.Query(q);
        }));
        var pushes = new ConcurrentQueue<CheckStateChange>();
        f.Sessions.Bus.ChecksChanged += pushes.Enqueue;
        var plan = await f.Engine.PlanMeasureRunAsync(new MeasureRunRequest { Cap = 25 });
        Assert.True(plan.Picks.Length >= 14, $"the plan picks {plan.Picks.Length}");
        var clock = Stopwatch.StartNew();
        var run = f.Engine.RunMeasurePlanAsync(plan.Id);
        var reads = 0; var slowest = TimeSpan.Zero;
        while (!run.IsCompleted)
        {
            // The rc27 hang: a plain read timed out while a run held the engine. Each door must answer within 5 s.
            var read = Stopwatch.StartNew();
            var d = await Doors(f.Engine, "measure:Sales/Sales Copy 7");
            var summary = await f.Sessions.Current.ReadAsync(m => m.AllMeasures.Count()).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(14, summary);
            Agree(d, "during the run");
            if (read.Elapsed > slowest) slowest = read.Elapsed;
            reads++;
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(180), $"the run did not finish in 180 s ({queries} queries, {reads} reads)");
            await Task.Delay(25);
        }
        var result = await run;
        Assert.False(result.Stopped, "the run stopped by itself");
        var states = await f.Engine.GetMeasureCheckCacheAsync();
        Assert.Equal(14, states.Length);
        Assert.All(states, s => { Assert.False(s.Running, s.MeasureRef + " still running"); Assert.NotNull(s.LastChecked); Assert.NotNull(s.Verdict); });
        Assert.All(states, s => Assert.DoesNotContain(s.Marks, m => m.Pending));
        Console.WriteLine($"v-rc28 run: {clock.Elapsed.TotalSeconds:0.0} s, {queries} queries, {reads} door reads, slowest {slowest.TotalMilliseconds:0} ms, {pushes.Count} pushes");
        // Versions only grow, per measure, in push order.
        foreach (var g in pushes.SelectMany(p => p.States).GroupBy(s => s.MeasureRef))
            Assert.Equal(g.Select(s => s.Version).OrderBy(v => v), g.Select(s => s.Version));
    }

    [Fact]
    public async Task Stop_keeps_every_measures_last_verdict_and_a_Broken_measure_stays_Broken()
    {
        using var f = new MeasureMarksDoorTests.Fixture(root); await f.Open(6);
        const string broken = "measure:Sales/Total Sales";
        await f.Engine.SaveTestDefinitionAsync(new TestDefinition
        {
            Kind = TestKinds.MeasureValue, Title = "Contoso trusted total", TargetRef = broken,
            ParamsJson = TestSuiteStore.Serialize(new MeasureValueRequest { MeasureRef = broken, ExpectedValue = "999" })
        }, "human");
        var first = await f.Engine.RunMeasurePlanAsync((await f.Engine.PlanMeasureRunAsync(new MeasureRunRequest { Cap = 25 })).Id);
        Assert.False(first.Stopped);
        var before = (await f.Engine.GetMeasureCheckCacheAsync()).ToDictionary(s => s.MeasureRef, s => s.Verdict);
        Assert.Equal("broken", before[broken]);

        f.Engine.SetLiveConnectionForTest(LiveConnection.ForTest("xmla", "contoso", "Contoso Sales", q => { Thread.Sleep(30); return f.Query(q); }));
        var plan = await f.Engine.PlanMeasureRunAsync(new MeasureRunRequest { Selection = new() { Measures = before.Keys.ToArray() }, Cap = 25 });
        var run = f.Engine.RunMeasurePlanAsync(plan.Id);
        var clock = Stopwatch.StartNew();
        // Stop part way: after the first measure finished, while others wait.
        while ((await f.Engine.GetMeasureCheckCacheAsync()).Count(s => s.Running) == 0 && clock.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(5);
        var mid = await Doors(f.Engine, broken);
        Agree(mid, "during the second run");
        Assert.True(mid.Row.Verdict == "broken", $"mid-run the Broken measure reads {mid.Row.Verdict}");
        Assert.True(await f.Engine.StopMeasureRunAsync());
        var result = await run.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(result.Stopped);
        var after = await f.Engine.GetMeasureCheckCacheAsync();
        var stuck = after.Where(s => s.Running).Select(s => s.MeasureRef + " [" + string.Join(", ", s.Marks.Select(m => $"{m.Name}:{m.State}:{m.ReasonCode}:{(m.Pending ? "pending" : "")}")) + "]").ToArray();
        Console.WriteLine($"v-rc28 stop: {stuck.Length} running right after Stop: {string.Join(" | ", stuck)}");
        var settle = Stopwatch.StartNew();
        while (after.Any(s => s.Running) && settle.Elapsed < TimeSpan.FromSeconds(30)) { await Task.Delay(50); after = await f.Engine.GetMeasureCheckCacheAsync(); }
        Console.WriteLine($"v-rc28 stop: settled={!after.Any(s => s.Running)} after {settle.ElapsedMilliseconds} ms; still running: {string.Join(", ", after.Where(s => s.Running).Select(s => s.MeasureRef))}");
        Assert.True(stuck.Length == 0, $"right after Stop returned, {stuck.Length} rows still read Checking (settled {!after.Any(s => s.Running)} after {settle.ElapsedMilliseconds} ms): {string.Join(" | ", stuck)}");
        foreach (var s in after)
        {
            Assert.False(s.Running, s.MeasureRef + " still running after Stop");
            Assert.DoesNotContain(s.Marks, m => m.Pending);
            Assert.True(s.Verdict == before[s.MeasureRef], $"{s.MeasureRef}: {before[s.MeasureRef]} before, {s.Verdict} after Stop");
            Agree(await Doors(f.Engine, s.MeasureRef), "after Stop " + s.MeasureRef);
        }
    }

    // fx27, the same case for a run an edit cancels: an edit of another measure ends the plan run, and the measure being checked
    // then must settle as Stop's does, keeping its last verdict.
    [Fact]
    public async Task An_edit_that_cancels_a_run_keeps_every_measures_last_verdict_and_a_Broken_measure_stays_Broken()
    {
        using var f = new MeasureMarksDoorTests.Fixture(root); await f.Open(6);
        const string broken = "measure:Sales/Total Sales";
        await f.Engine.SaveTestDefinitionAsync(new TestDefinition
        {
            Kind = TestKinds.MeasureValue, Title = "Contoso trusted total", TargetRef = broken,
            ParamsJson = TestSuiteStore.Serialize(new MeasureValueRequest { MeasureRef = broken, ExpectedValue = "999" })
        }, "human");
        var first = await f.Engine.RunMeasurePlanAsync((await f.Engine.PlanMeasureRunAsync(new MeasureRunRequest { Cap = 25 })).Id);
        Assert.False(first.Stopped);
        var before = (await f.Engine.GetMeasureCheckCacheAsync()).ToDictionary(s => s.MeasureRef, s => s.Verdict);
        Assert.Equal("broken", before[broken]);

        f.Engine.SetLiveConnectionForTest(LiveConnection.ForTest("xmla", "contoso", "Contoso Sales", q => { Thread.Sleep(30); return f.Query(q); }));
        var plan = await f.Engine.PlanMeasureRunAsync(new MeasureRunRequest { Selection = new() { Measures = before.Keys.ToArray() }, Cap = 25 });
        var run = f.Engine.RunMeasurePlanAsync(plan.Id);
        var clock = Stopwatch.StartNew();
        while ((await f.Engine.GetMeasureCheckCacheAsync()).Count(s => s.Running) == 0 && clock.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(5);
        Assert.True((await f.Engine.GetMeasureCheckCacheAsync()).Any(s => s.Running), "no measure was being checked when the edit landed");
        // A new measure uses none of the six, so no measure the run checks gets a new run of its own from this edit.
        await f.Sessions.Current.MutateAsync("human", "Contoso note", m => m.Tables["Sales"].AddMeasure("Contoso Note", "1"));
        var result = await run.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(result.StoppedOnEdit, "the edit did not end the run");
        var after = (await f.Engine.GetMeasureCheckCacheAsync()).Where(s => before.ContainsKey(s.MeasureRef)).ToArray();
        var stuck = after.Where(s => s.Running).Select(s => s.MeasureRef).ToArray();
        Assert.True(stuck.Length == 0, $"right after the edit ended the run, {stuck.Length} rows still read Checking: {string.Join(", ", stuck)}");
        foreach (var s in after)
        {
            Assert.DoesNotContain(s.Marks, m => m.Pending);
            Assert.True(s.Verdict == before[s.MeasureRef], $"{s.MeasureRef}: {before[s.MeasureRef]} before, {s.Verdict} after the edit");
            Agree(await Doors(f.Engine, s.MeasureRef), "after the edit " + s.MeasureRef);
        }
    }

    // fx27: the Keep rule the v-fx23 to v-fx26 attack files held against a TS copy (isKept, now gone), held here against the
    // engine's own rule, MeasureCheckVerdict.Finish. Every surface and the assistant read the mark it returns.
    [Theory]
    [InlineData("I-quantity", "I-quantity", true)]              // v-fx23 2: an edit of another measure leaves the inputs, the Keep holds
    [InlineData("I-profit-1", "I-profit-2", false)]             // v-fx24 1: the next edit of a used measure ends the Keep
    [InlineData(null, "b2c4e1", false)]                         // v-fx25 1 and v-fx26 N1 c: an old Keep with no inputs never hides a mark
    [InlineData("", "b2c4e1", false)]                           // v-fx26 N1 c: a Keep saved with empty inputs
    [InlineData("I-kept", "I-profit-edited", false)]            // v-fx26 N1 a: one level down
    [InlineData("I-kept", "I-cost-under-profit-edited", false)] // v-fx26 N1 a: two levels down
    [InlineData("I-kept", "I-kept", true)]                      // v-fx26 N1 a: Undo back to the kept inputs brings the Keep back
    [InlineData("I-own", "I-own2", false)]                      // v-fx26 N1 b: an own edit ends the Keep
    [InlineData("I-kept", "", false)]                           // v-fx26 N1 d: a mark with empty inputs
    [InlineData("I-kept", null, false)]
    public void The_engine_Keep_rule_hides_an_Equivalence_mark_only_on_the_inputs_it_kept(string keptInputs, string markInputs, bool kept)
    {
        var moved = new MeasureMark { Name = "Equivalence", State = "suspect", Sentence = "This change moved these numbers.", Inputs = markInputs };
        var mark = MeasureCheckVerdict.Finish(moved, new MeasureKeep { Inputs = keptInputs, At = DateTimeOffset.UtcNow }, false);
        Assert.Equal(kept, mark.ReasonCode == "kept");
        Assert.Equal(!kept, mark.Applies);
        Assert.Equal(kept ? null : "review", mark.Verdict);
        Assert.Equal(kept ? null : "review", MeasureCheckVerdict.Worst(new[] { mark }));
    }
}
