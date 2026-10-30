using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests;

// fx29a: cancellation is a state, never a result. A run's Stop or an edit cancels only that run's own work. The relationship
// scan that several checks read belongs to the engine, under its own token: each check waits on it with its own token, the
// last one to leave early cancels it, and a cancelled scan is never kept. Live on pilot2 (fx29a live/out-before.txt): a card's
// Check now during a run, then Stop, then Check now turned 9 Verified measures Unknown ("the related orphan-key check could
// not run"), and a second Check now kept them Unknown. Marks are compared one by one, never by verdict alone: the offline
// Equivalence Unknown of the fixture's added measures hid this drift behind the verdict (fx29 gap 7).
[Collection("restore-root")]
public sealed class CheckedFx29aTests : IDisposable
{
    private const string Broken = "measure:Sales/Total Sales";
    private readonly string safeRoot;
    private readonly string root = Path.Combine(Path.GetTempPath(), "fx29a-" + Guid.NewGuid().ToString("N")[..8]);
    public CheckedFx29aTests(RestoreRootFixture fixture) { safeRoot = fixture.Root; ConnectionRegistry.RootOverride = root; }
    public void Dispose()
    {
        ConnectionRegistry.RootOverride = safeRoot;
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }

    private static async Task SaveBrokenTrustedNumber(MeasureMarksDoorTests.Fixture f) =>
        await f.Engine.SaveTestDefinitionAsync(new TestDefinition
        {
            Kind = TestKinds.MeasureValue, Title = "Contoso trusted total", TargetRef = Broken,
            ParamsJson = TestSuiteStore.Serialize(new MeasureValueRequest { MeasureRef = Broken, ExpectedValue = "999" })
        }, "human");

    private static bool Busy(MeasureCheckCache s) => s.Running || s.Marks.Any(m => m.Pending || m.Progress != null);

    // Every mark of every measure as the rows read it, and the cheap orphan-key line the run writes for each measure.
    private static async Task<Dictionary<string, string[]>> Marks(LocalEngine engine) =>
        (await engine.GetMeasureCheckCacheAsync()).ToDictionary(s => s.MeasureRef, s => s.Marks
            .Select(m => $"{m.Name}: {m.State} / {m.ReasonCode} / {m.Verdict}")
            .Append("Orphan keys (cheap): " + s.Cheap.FirstOrDefault(m => m.Name == "Orphan keys")?.Sentence).ToArray());

    private static void SameMarks(Dictionary<string, string[]> expected, Dictionary<string, string[]> actual, string why)
    {
        var diffs = expected.SelectMany(e =>
        {
            var now = actual.GetValueOrDefault(e.Key) ?? Array.Empty<string>();
            return e.Value.Except(now).Select(m => $"{e.Key}: expected [{m}], got [{now.FirstOrDefault(n => n.Split(':')[0] == m.Split(':')[0])}]");
        }).ToArray();
        Assert.True(diffs.Length == 0, $"{why}: {diffs.Length} marks differ from an uninterrupted run: {string.Join(" | ", diffs)}");
    }

    private static async Task<MeasureCheckCache[]> WaitSettled(LocalEngine engine, int quietMs = 300)
    {
        var clock = Stopwatch.StartNew();
        var quiet = Stopwatch.StartNew();
        while (true)
        {
            var states = await engine.GetMeasureCheckCacheAsync();
            if (states.Any(Busy)) quiet.Restart();
            else if (quiet.ElapsedMilliseconds >= quietMs) return states;
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(30), "the checks did not settle: " + string.Join(", ", states.Where(Busy).Select(s => s.MeasureRef)));
            await Task.Delay(20);
        }
    }

    private static async Task<MeasureRunPlan> PlanAll(LocalEngine engine) =>
        await engine.PlanMeasureRunAsync(new MeasureRunRequest
        { Selection = new() { Measures = (await engine.GetMeasureCheckCacheAsync()).Select(s => s.MeasureRef).ToArray() }, Cap = 25 });

    // The Check now on the run line, to its end.
    private static async Task CheckNow(LocalEngine engine, int quietMs = 300)
    {
        Assert.False((await engine.RunMeasurePlanAsync((await PlanAll(engine)).Id)).Stopped);
        await WaitSettled(engine, quietMs);
    }

    private static async Task WaitRunning(LocalEngine engine)
    {
        var clock = Stopwatch.StartNew();
        while (!(await engine.GetMeasureCheckCacheAsync()).Any(s => s.Running) && clock.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(5);
    }

    private static Task Edit(MeasureMarksDoorTests.Fixture f, string words) =>
        f.Sessions.Current.MutateAsync("human", "Contoso note " + words, m => m.Tables["Product"].Description = "Contoso products " + words);

    private static Task<bool> Stop(LocalEngine engine, string door) => door == "mcp"
        ? McpTools.StopMeasureRun(AssistantEngine.ForAssistant(engine))
        : new EngineRpcTarget(engine).stopMeasureRun();

    // An outside checker of the future: it returns its own Filters and Contexts, which the card's built-in probes cannot make.
    private sealed class OwnExtras : IMeasureRunExtras
    {
        internal volatile bool Hold;
        internal readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Inputs => "contoso-own-checker-v1";
        public async Task<MeasureMark[]> ContextsAsync(string measureRef, CancellationToken cancellationToken)
        {
            if (Hold) { Started.TrySetResult(); await Task.Delay(Timeout.Infinite, cancellationToken); }
            return new[]
            {
                new MeasureMark { Name = "Filters", State = "passed", Sentence = "Contoso outside filters checked." },
                new MeasureMark { Name = "Contexts", State = "passed", Sentence = "Contoso outside contexts checked." }
            };
        }
        public Task<MeasureMark> PerformanceAsync(string measureRef, CancellationToken cancellationToken) =>
            Task.FromResult(new MeasureMark { Name = "Performance", State = "passed", Sentence = "Contoso outside timing." });
        public Task<double?> FastMatchAsync(MeasureRunCandidate candidate, CancellationToken cancellationToken) => Task.FromResult<double?>(null);
    }

    // (a) First trigger: the person presses Check now on a card during a run, then Stop on the run line, then Check now.
    [Theory]
    [InlineData("rpc")]
    [InlineData("mcp")]
    public async Task Stop_after_a_card_Check_now_during_a_run_then_Check_now_gives_the_marks_of_an_uninterrupted_run(string door)
    {
        using var f = new MeasureMarksDoorTests.Fixture(root); await f.Open(4);
        await SaveBrokenTrustedNumber(f);
        f.Engine.SetLiveConnectionForTest(LiveConnection.ForTest("xmla", "contoso", "Contoso Sales", q => { Thread.Sleep(15); return f.Query(q); }));
        await CheckNow(f.Engine);
        var expected = await Marks(f.Engine);
        Assert.Contains("Filters: passed / ", expected["measure:Sales/Sales Copy 2"][0]);

        var run = f.Engine.RunMeasurePlanAsync((await PlanAll(f.Engine)).Id);
        await WaitRunning(f.Engine);
        Assert.True((await f.Engine.GetMeasureMarksAsync(Broken, refresh: true)).Running, "the card's Check now did not start");
        Assert.True(await Stop(f.Engine, door));
        Assert.True((await run.WaitAsync(TimeSpan.FromSeconds(30))).Stopped);
        // The checks Stop cancelled end on their own a little later; nothing they leave may change the next check.
        await Task.Delay(1500);
        await WaitSettled(f.Engine);
        await CheckNow(f.Engine);
        SameMarks(expected, await Marks(f.Engine), "after Stop and Check now");
    }

    // (a) Second trigger: a card opened on a new revision starts a check whose relationship scan the run line's Check now then
    // shares; Stop ends both while the scan is in flight.
    [Theory]
    [InlineData("rpc")]
    [InlineData("mcp")]
    public async Task Stop_while_a_card_check_and_a_run_share_a_scan_in_flight_then_Check_now_gives_the_marks_of_an_uninterrupted_run(string door)
    {
        using var f = new MeasureMarksDoorTests.Fixture(root); await f.Open(6);
        await SaveBrokenTrustedNumber(f);
        var gate = new ManualResetEventSlim(true);
        var orphanSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Engine.SetLiveConnectionForTest(LiveConnection.ForTest("xmla", "contoso", "Contoso Sales", q =>
        {
            if (q.Contains("OrphanRows") && !gate.IsSet) { orphanSeen.TrySetResult(); gate.Wait(TimeSpan.FromSeconds(20)); }
            return f.Query(q);
        }));
        await CheckNow(f.Engine);
        await Edit(f, "1");
        await CheckNow(f.Engine);
        var expected = await Marks(f.Engine);

        await Edit(f, "2");
        gate.Reset();
        Assert.True((await f.Engine.GetMeasureMarksAsync("measure:Sales/Sales Copy 3")).Running, "opening the card did not start its check");
        await orphanSeen.Task.WaitAsync(TimeSpan.FromSeconds(20));
        var run = f.Engine.RunMeasurePlanAsync((await PlanAll(f.Engine)).Id);
        var clock = Stopwatch.StartNew();
        var stopped = false;
        while (!(stopped = await Stop(f.Engine, door)) && clock.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(10);
        Assert.True(stopped, "Stop found no run");
        gate.Set();
        Assert.True((await run.WaitAsync(TimeSpan.FromSeconds(30))).Stopped);
        await Task.Delay(1000);
        await WaitSettled(f.Engine);
        await CheckNow(f.Engine);
        SameMarks(expected, await Marks(f.Engine), "after Stop and Check now");
    }

    // (b) The class: Stop lands on each live query k of a run that has a card check in it, through each Stop door, with the
    // built-in checker and with an outside one. The next Check now must give every mark of an uninterrupted run, whichever
    // query Stop cut and whatever input that query was making for other checks.
    // About 200 stops per row, so it runs in the sweep tier; the Checked gate names it, and the two triggers above stay in every run.
    [Theory]
    [Trait(TestCost.Key, TestCost.Sweep)]
    [InlineData("rpc", false)]
    [InlineData("mcp", false)]
    [InlineData("rpc", true)]
    [InlineData("mcp", true)]
    public async Task Stop_at_any_live_query_of_a_run_with_a_card_check_then_Check_now_gives_the_marks_of_an_uninterrupted_run(string door, bool outside)
    {
        using var f = new MeasureMarksDoorTests.Fixture(root); await f.Open(2);
        await SaveBrokenTrustedNumber(f);
        if (outside) f.Engine.MeasureRunExtras = new OwnExtras();
        var count = 0; var stopAt = 0;
        var hit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new ManualResetEventSlim(true);
        f.Engine.SetLiveConnectionForTest(LiveConnection.ForTest("xmla", "contoso", "Contoso Sales", q =>
        {
            if (Interlocked.Increment(ref count) == Volatile.Read(ref stopAt)) { resume.Reset(); hit.TrySetResult(); resume.Wait(TimeSpan.FromSeconds(10)); }
            return f.Query(q);
        }));
        await CheckNow(f.Engine);

        // One uninterrupted run with a card check in it: its live query count is N, and the Check now after it is the expected marks.
        await Edit(f, "0");
        Volatile.Write(ref count, 0);
        var first = f.Engine.RunMeasurePlanAsync((await PlanAll(f.Engine)).Id);
        await WaitRunning(f.Engine);
        await f.Engine.GetMeasureMarksAsync(Broken, refresh: true);
        Assert.False((await first.WaitAsync(TimeSpan.FromSeconds(30))).Stopped);
        await WaitSettled(f.Engine);
        var n = Volatile.Read(ref count);
        Assert.InRange(n, 1, 400);
        await CheckNow(f.Engine);
        var expected = await Marks(f.Engine);

        for (var k = 1; k <= n; k++)
        {
            await Edit(f, k.ToString());
            hit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Volatile.Write(ref count, 0); Volatile.Write(ref stopAt, k);
            var run = f.Engine.RunMeasurePlanAsync((await PlanAll(f.Engine)).Id);
            var clock = Stopwatch.StartNew();
            while (!hit.Task.IsCompleted && !run.IsCompleted && !(await f.Engine.GetMeasureCheckCacheAsync()).Any(s => s.Running)
                && clock.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(2);
            await f.Engine.GetMeasureMarksAsync(Broken, refresh: true);
            await Task.WhenAny(hit.Task, run).WaitAsync(TimeSpan.FromSeconds(30));
            if (hit.Task.IsCompleted) await Stop(f.Engine, door);
            Volatile.Write(ref stopAt, 0); resume.Set();
            await run.WaitAsync(TimeSpan.FromSeconds(30));
            await Task.Delay(50);
            await WaitSettled(f.Engine, 50);
            await CheckNow(f.Engine, 50);
            SameMarks(expected, await Marks(f.Engine), $"Stop through {door} at live query {k} of {n}{(outside ? " (outside checker)" : "")}, then Check now");
        }
    }

    // (c) One checker per check: an outside checker's marks read the same on the card door and the plan door. Opening a card
    // starts no check in the outside checker's name, and the card's Check now gives what the plan gives.
    [Fact]
    public async Task An_outside_checker_gives_the_same_marks_on_the_card_door_and_the_plan_door()
    {
        using var f = new MeasureMarksDoorTests.Fixture(root); await f.Open(3);
        const string reference = "measure:Sales/Sales Copy 1";
        f.Engine.MeasureRunExtras = new OwnExtras();
        await CheckNow(f.Engine);
        string[] Words(IEnumerable<MeasureMark> marks) => marks.Where(m => m.Name is "Filters" or "Contexts")
            .Select(m => $"{m.Name}: {m.State} / {m.ReasonCode} / {m.Verdict} / {m.Sentence}").ToArray();
        var plan = Words((await f.Engine.GetMeasureCheckCacheAsync()).Single(s => s.MeasureRef == reference).Marks);
        Assert.Contains("Filters: passed /  / verified / Contoso outside filters checked.", plan);

        await Edit(f, "1");
        var opened = await f.Engine.GetMeasureMarksAsync(reference);
        Assert.False(opened.Running, "opening the card started a check in the outside checker's name");
        Assert.Equal(plan, Words(opened.Marks));

        var revision = f.Sessions.Current.Revision;
        await f.Engine.GetMeasureMarksAsync(reference, refresh: true);
        var clock = Stopwatch.StartNew();
        MeasureCheckCache state;
        while (!((state = (await f.Engine.GetMeasureCheckCacheAsync()).Single(s => s.MeasureRef == reference)).CheckedOn?.Contains("|" + revision + "|") == true
            && !Busy(state)))
        {
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(20), "the card's Check now did not check the measure on this revision");
            await Task.Delay(20);
        }
        Assert.Equal(plan, Words((await f.Engine.GetMeasureMarksAsync(reference)).Marks));
        Assert.Equal(plan, Words((await McpTools.GetMeasureMarks(f.Engine, reference)).Marks));
    }

    // (c) Stop during an outside checker, then each card is read: no read starts a check in that checker's name.
    [Fact]
    public async Task After_Stop_during_an_outside_checker_no_card_read_starts_a_check_in_its_name()
    {
        using var f = new MeasureMarksDoorTests.Fixture(root); await f.Open(3);
        var extras = new OwnExtras();
        f.Engine.MeasureRunExtras = extras;
        await CheckNow(f.Engine);
        var before = await Marks(f.Engine);
        await Edit(f, "1");
        extras.Hold = true;
        var run = f.Engine.RunMeasurePlanAsync((await PlanAll(f.Engine)).Id);
        await extras.Started.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.True(await f.Engine.StopMeasureRunAsync());
        Assert.True((await run.WaitAsync(TimeSpan.FromSeconds(30))).Stopped);
        foreach (var reference in before.Keys)
        {
            var card = await f.Engine.GetMeasureMarksAsync(reference);
            Assert.False(card.Running, $"reading the card of {reference} started a check in the outside checker's name");
        }
        await Task.Delay(300);
        Assert.False((await f.Engine.GetMeasureCheckCacheAsync()).Any(Busy), "a card read started a check");
        extras.Hold = false;
        await CheckNow(f.Engine);
        SameMarks(before, await Marks(f.Engine), "after Check now with the outside checker");
    }

    // (d) The scan does not hold the query lane for nobody: when Stop or an edit leaves no check waiting on the relationship
    // scan, its DAX command is cancelled on the server. The scan is shared, so it has its own token; this is what that token
    // must still do. Both the run line's own scan and one a card started and the run then shared.
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Stop_or_an_edit_with_no_check_left_waiting_cancels_the_scans_DAX_command(bool edit, bool cardStarted)
    {
        using var f = new MeasureMarksDoorTests.Fixture(root); await f.Open(3);
        await CheckNow(f.Engine);
        await Edit(f, "1");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var armed = 1;
        f.Engine.SetLiveConnectionForTest(LiveConnection.ForTest("xmla", "contoso", "Contoso Sales", (q, ct) =>
        {
            if (q.Contains("OrphanRows") && Interlocked.Exchange(ref armed, 0) == 1)
            {
                started.TrySetResult();
                ct.WaitHandle.WaitOne(TimeSpan.FromSeconds(10));
                if (ct.IsCancellationRequested) cancelled.TrySetResult();
            }
            return f.Query(q);
        }));
        // Without a card, the scan is the run line's own: this revision has no scan yet.
        if (cardStarted) Assert.True((await f.Engine.GetMeasureMarksAsync("measure:Sales/Sales Copy 1")).Running);
        var run = f.Engine.RunMeasurePlanAsync((await PlanAll(f.Engine)).Id);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        if (cardStarted) await WaitRunning(f.Engine);
        if (edit) await Edit(f, "2");
        else
        {
            var clock = Stopwatch.StartNew();
            while (!await f.Engine.StopMeasureRunAsync() && clock.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(10);
        }
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var result = await run.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(edit ? result.StoppedOnEdit : result.Stopped);
    }

    // (e) The assistant's Stop is the person's Stop: stop_measure_run reaches the same StopMeasureRunAsync and SettleOpenRuns,
    // so the run ends, every door is settled, and no verdict or CheckedOn moves.
    [Fact]
    public async Task The_assistants_stop_measure_run_stops_a_run_like_the_Stop_button()
    {
        using var f = new MeasureMarksDoorTests.Fixture(root); await f.Open(4);
        await SaveBrokenTrustedNumber(f);
        var hold = new ManualResetEventSlim(true);
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Engine.SetLiveConnectionForTest(LiveConnection.ForTest("xmla", "contoso", "Contoso Sales", q =>
        {
            // The first measure query of the run (the shared relationship scan is not one): no measure has finished yet.
            if (!q.Contains("OrphanRows") && !hold.IsSet) { held.TrySetResult(); hold.Wait(TimeSpan.FromSeconds(20)); }
            return f.Query(q);
        }));
        await CheckNow(f.Engine);
        await Edit(f, "1");
        var before = await f.Engine.GetMeasureCheckCacheAsync();
        hold.Reset();
        var run = f.Engine.RunMeasurePlanAsync((await PlanAll(f.Engine)).Id);
        await held.Task.WaitAsync(TimeSpan.FromSeconds(20));
        // The person also opened a card and pressed Check now on it: Stop ends that check too.
        Assert.True((await f.Engine.GetMeasureMarksAsync(Broken, refresh: true)).Running);
        Assert.True(await McpTools.StopMeasureRun(AssistantEngine.ForAssistant(f.Engine)), "the assistant's Stop found no run");
        hold.Set();
        Assert.True((await run.WaitAsync(TimeSpan.FromSeconds(30))).Stopped);
        var after = await WaitSettled(f.Engine);
        var agent = await McpTools.GetMeasureCheckCache(AssistantEngine.ForAssistant(f.Engine));
        Assert.False(agent.Any(Busy), "the assistant still reads a check running");
        foreach (var b in before)
        {
            var a = after.Single(s => s.MeasureRef == b.MeasureRef);
            Assert.True(a.Verdict == b.Verdict, $"{b.MeasureRef}: {b.Verdict} -> {a.Verdict}");
            Assert.True(a.CheckedOn == b.CheckedOn, $"{b.MeasureRef}: CheckedOn {b.CheckedOn} -> {a.CheckedOn}");
        }
        // Opening a card checks a measure that has no check on this revision (by design): that check ends by itself.
        foreach (var b in before)
        {
            var card = await McpTools.GetMeasureMarks(f.Engine, b.MeasureRef);
            var clock = Stopwatch.StartNew();
            while ((card.Running || card.Marks.Any(m => m.Pending)) && clock.Elapsed < TimeSpan.FromSeconds(20))
            { await Task.Delay(25); card = await McpTools.GetMeasureMarks(f.Engine, b.MeasureRef); }
            Assert.False(card.Running || card.Marks.Any(m => m.Pending), $"the card of {b.MeasureRef} never ends its check");
        }
    }

    // (f) The assistant's Keep is the person's Keep, and the shared Undo timeline covers it: Undo takes the kept edit back.
    [Theory]
    [InlineData("rpc")]
    [InlineData("mcp")]
    public async Task Keep_through_either_door_keeps_the_edit_and_Undo_takes_the_edit_back(string door)
    {
        using var f = new MeasureMarksDoorTests.Fixture(root); await f.Open();
        const string reference = "measure:Sales/Total Sales";
        MeasureMark Mark(MeasureCheckCache s, string name) => s.Marks.SingleOrDefault(m => m.Name == name);
        async Task<MeasureCheckCache> Settled(Func<MeasureCheckCache, bool> done)
        {
            var clock = Stopwatch.StartNew();
            while (true)
            {
                var state = (await f.Engine.GetMeasureCheckCacheAsync()).Single(c => c.MeasureRef == reference);
                if (!state.Running && done(state)) return state;
                Assert.True(clock.Elapsed < TimeSpan.FromSeconds(15), "the check state did not settle");
                await Task.Delay(10);
            }
        }
        await MeasureMarksTests.Finish(f.Engine, reference);
        var original = await f.Sessions.Current.ReadAsync(m => m.Tables["Sales"].Measures["Total Sales"].Expression);
        await f.Sessions.Current.MutateAsync("human", "Contoso edit", m => m.Tables["Sales"].Measures["Total Sales"].Expression += " + 50");
        var moved = await Settled(s => Mark(s, "Equivalence")?.State == "suspect");
        Assert.Equal("review", moved.Verdict);

        var kept = door == "mcp"
            ? await McpTools.KeepMeasureEdit(AssistantEngine.ForAssistant(f.Engine), reference)
            : await new EngineRpcTarget(f.Engine).keepMeasureEdit(reference);
        Assert.Equal("kept", Mark(kept, "Equivalence").ReasonCode);
        Assert.False(Mark(kept, "Equivalence").Applies);
        Assert.Equal(Mark(moved, "Equivalence").Inputs, kept.Keep.Inputs);
        var row = (await f.Engine.GetMeasureCheckCacheAsync()).Single(s => s.MeasureRef == reference);
        var agent = (await McpTools.GetMeasureCheckCache(AssistantEngine.ForAssistant(f.Engine))).Single(s => s.MeasureRef == reference);
        var card = await f.Engine.GetMeasureMarksAsync(reference);
        Assert.NotEqual("review", row.Verdict);
        Assert.NotEqual("review", agent.Verdict);
        Assert.False(card.Marks.Single(m => m.Name == "Equivalence").Applies);

        await f.Sessions.Current.UndoAsync("human");
        Assert.Equal(original, await f.Sessions.Current.ReadAsync(m => m.Tables["Sales"].Measures["Total Sales"].Expression));
        var back = await Settled(s => Mark(s, "Equivalence")?.State != "suspect");
        Assert.NotEqual("review", back.Verdict);
        Assert.True(Mark(back, "Equivalence") is null || Mark(back, "Equivalence").ReasonCode != "kept",
            "after Undo the measure still reads a kept edit: " + Mark(back, "Equivalence")?.Sentence);
    }

    // fx29 gap 7, offline half: a measure added on a model with no matching connection is new, not an edit, so it has no
    // Equivalence. An edit of an existing measure offline still says why it could not be compared.
    [Fact]
    public async Task A_measure_added_offline_has_no_Equivalence_and_an_offline_edit_still_says_why()
    {
        using var f = new MeasureMarksDoorTests.Fixture(root);
        await f.Engine.CreateModelAsync("Contoso Sales", 1604);
        var sales = await f.Engine.CreateTableAsync("Sales", "human");
        await f.Engine.CreateColumnAsync(sales, "Amount", "Decimal", "Amount", "human");
        await f.Sessions.Current.MutateAsync("human", "Contoso add", m => m.Tables["Sales"].AddMeasure("Total Sales", "SUM('Sales'[Amount])"));
        var added = (await MeasureMarksTests.Finish(f.Engine, "measure:Sales/Total Sales")).Marks.Single(m => m.Name == "Equivalence");
        Assert.True(added.ReasonCode == "no_edit" && added.Applies == false,
            $"an added measure reads Equivalence {added.State} / {added.ReasonCode}: {added.Sentence}");

        await f.Sessions.Current.MutateAsync("human", "Contoso edit", m => m.Tables["Sales"].Measures["Total Sales"].Expression += " + 1");
        var edited = (await MeasureMarksTests.Finish(f.Engine, "measure:Sales/Total Sales")).Marks.Single(m => m.Name == "Equivalence");
        Assert.Equal("This edit could not be compared because no matching model was connected.", edited.Sentence);
    }
}
