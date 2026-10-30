using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests;

// fx31 (rc30 tester, gap 1): delete a measure, then Undo. Every measure read "Unknown: Filters could not run" with "Reopen the
// deployed model to check this structure edit.", a Broken measure turned Unknown, and Check now and Run ended at once Unknown
// until a reopen. The Undo of a delete replays with no property events, so the session records one "structure" delta; the mark
// observer then set a session-wide reason that only an open cleared, and every check of every measure read it. The reason must
// follow the model as it is now (Kane: Undo is the safety net; finish line C4: Broken stays Broken): back to the connected
// model's structure, no reason; while a structure change is live, only the measures it touches are held, and the other measures
// keep checking. fx33 (rc32 tester gap 1): a held measure reads Unknown with the reason, not its last verdict (spec R8).
[Collection("restore-root")]
public sealed class CheckedFx31Tests : IDisposable
{
    private const string Broken = "measure:Sales/Total Sales";
    private const string Constant = "measure:Sales/Contoso constant";
    private const string Hold = "Publish this change to check it.";
    private readonly string safeRoot;
    private readonly string root = Path.Combine(Path.GetTempPath(), "fx31-" + Guid.NewGuid().ToString("N")[..8]);
    public CheckedFx31Tests(RestoreRootFixture fixture) { safeRoot = fixture.Root; ConnectionRegistry.RootOverride = root; }
    public void Dispose()
    {
        ConnectionRegistry.RootOverride = safeRoot;
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }

    private static bool Busy(MeasureCheckCache s) => s.Running || s.Marks.Any(m => m.Pending || m.Progress != null);

    private static async Task<MeasureCheckCache[]> WaitSettled(LocalEngine engine)
    {
        var clock = Stopwatch.StartNew();
        var states = await engine.GetMeasureCheckCacheAsync();
        while (states.Any(Busy) && clock.Elapsed < TimeSpan.FromSeconds(20)) { await Task.Delay(25); states = await engine.GetMeasureCheckCacheAsync(); }
        return states;
    }

    private static async Task SaveBrokenTrustedNumber(MeasureMarksDoorTests.Fixture f) =>
        await f.Engine.SaveTestDefinitionAsync(new TestDefinition
        {
            Kind = TestKinds.MeasureValue, Title = "Contoso trusted total", TargetRef = Broken,
            ParamsJson = TestSuiteStore.Serialize(new MeasureValueRequest { MeasureRef = Broken, ExpectedValue = "999" })
        }, "human");

    private static async Task<MeasureCheckCache[]> CheckNow(MeasureMarksDoorTests.Fixture f, IEnumerable<string> refs)
    {
        var run = await f.Engine.RunMeasurePlanAsync((await f.Engine.PlanMeasureRunAsync(new MeasureRunRequest { Selection = new() { Measures = refs.ToArray() }, Cap = 25 })).Id);
        Assert.False(run.Stopped || run.StoppedOnEdit);
        return await WaitSettled(f.Engine);
    }

    private static string Marks(MeasureCheckCache s) => s.MeasureRef + " " + s.Verdict + ": " + string.Join(", ", s.Marks.Select(m => $"{m.Name}:{m.State}:{m.Verdict}:{m.Sentence}"));

    // Both doors: the tree and Checks rows (RPC) and the assistant (MCP). No mark names engine words.
    private static async Task<MeasureCheckCache[]> BothDoors(LocalEngine engine)
    {
        var rows = await engine.GetMeasureCheckCacheAsync();
        var agent = await McpTools.GetMeasureCheckCache(engine);
        var words = rows.Concat(agent).SelectMany(s => s.Marks.Where(m => (m.Sentence ?? "").Contains("deployed") || (m.Sentence ?? "").Contains("structure"))
            .Select(m => $"{s.MeasureRef} {m.Name}: {m.Sentence}")).ToArray();
        Assert.True(words.Length == 0, "engine words in a mark: " + string.Join(" | ", words));
        foreach (var row in rows)
            Assert.True(agent.Single(a => a.MeasureRef == row.MeasureRef).Verdict == row.Verdict, $"the doors differ on {row.MeasureRef}");
        return rows;
    }

    private static string[] Held(IEnumerable<MeasureCheckCache> states) =>
        states.Where(s => s.Marks.Any(m => (m.Sentence ?? "").Contains(Hold))).Select(s => s.MeasureRef).ToArray();

    private static void AssertHeldUnknown(IEnumerable<string> touched, IEnumerable<MeasureCheckCache> states, string why)
    {
        foreach (var state in states.Where(s => touched.Contains(s.MeasureRef)))
            Assert.True(state.Verdict == "unknown", $"{why}: a held measure must read Unknown: {Marks(state)}");
    }

    private static void AssertSameVerdicts(IReadOnlyDictionary<string, string> before, IEnumerable<MeasureCheckCache> states, string why)
    {
        foreach (var state in states.Where(s => before.ContainsKey(s.MeasureRef)))
            Assert.True(state.Verdict == before[state.MeasureRef], $"{why}: {state.MeasureRef} was {before[state.MeasureRef]}: {Marks(state)}");
    }

    [Theory]
    [InlineData("human")]
    [InlineData("agent")]
    public async Task Delete_then_Undo_keeps_every_mark_and_Check_now_and_Run_give_real_verdicts(string door)
    {
        using var f = new MeasureMarksDoorTests.Fixture(root); await f.Open(6);
        await SaveBrokenTrustedNumber(f);
        Assert.False((await f.Engine.RunMeasurePlanAsync((await f.Engine.PlanMeasureRunAsync(new MeasureRunRequest { Cap = 25 })).Id)).Stopped);
        const string gone = "measure:Sales/Sales Copy 5";
        var before = (await f.Engine.GetMeasureCheckCacheAsync()).Where(s => s.MeasureRef != gone).ToDictionary(s => s.MeasureRef, s => s.Verdict);
        Assert.Equal("broken", before[Broken]);
        Assert.Equal("verified", before["measure:Sales/Sales Copy 1"]);

        if (door == "human") await f.Engine.DeleteObjectAsync(gone, "human"); else await McpTools.DeleteObject(f.Engine, gone);
        var deleted = await WaitSettled(f.Engine);
        AssertSameVerdicts(before, deleted, "after the delete");
        if (door == "human") await f.Engine.UndoAsync("human"); else await McpTools.UndoChange(f.Engine);
        var undone = await WaitSettled(f.Engine);
        Assert.True(!undone.Any(Busy), "20 s after Undo: " + string.Join(" | ", undone.Where(Busy).Select(Marks)));
        AssertSameVerdicts(before, await BothDoors(f.Engine), "after Delete then Undo");

        // Check now on one measure (the card), then Run on all: real verdicts, a Broken measure stays Broken.
        var card = await MeasureMarksTests.Finish(f.Engine, "measure:Sales/Sales Copy 1");
        Assert.True(card.Marks.Single(m => m.Name == "Contexts").Verdict == "verified",
            "Check now after Undo: " + string.Join(", ", card.Marks.Select(m => $"{m.Name}:{m.Verdict}:{m.Sentence}")));
        await CheckNow(f, before.Keys.Append(gone));
        var states = await BothDoors(f.Engine);
        Assert.True(Held(states).Length == 0, "after Delete, Undo and Run, marks read a held change: " + string.Join(", ", Held(states)));
        AssertSameVerdicts(before, states, "after Delete, Undo and Run");
        Assert.DoesNotContain(states, s => s.Verdict == "unknown");
    }

    [Fact]
    public async Task A_live_structure_change_holds_only_the_measures_it_touches_and_Undo_or_Redo_back_clears_it()
    {
        using var f = new MeasureMarksDoorTests.Fixture(root); await f.Open(3);
        await f.Sessions.Current.MutateAsync("human", "Contoso constant", m => m.Tables["Sales"].AddMeasure("Contoso constant", "300"));
        await SaveBrokenTrustedNumber(f);
        Assert.False((await f.Engine.RunMeasurePlanAsync((await f.Engine.PlanMeasureRunAsync(new MeasureRunRequest { Cap = 25 })).Id)).Stopped);
        var first = await WaitSettled(f.Engine);
        var before = first.ToDictionary(s => s.MeasureRef, s => s.Verdict);
        Assert.Equal("broken", before[Broken]);
        Assert.Equal("verified", before[Constant]);
        var touched = before.Keys.Where(r => r != Constant).ToArray();

        // A new source column for Sales[Amount]: the connected model still reads the old one, so the measures over it cannot be
        // checked there. They read Unknown with the reason (fx33); the constant does not use it and keeps its verdict.
        async Task ChangeSource() => await f.Sessions.Current.MutateAsync("human", "Contoso source",
            m => ((TabularEditor.TOMWrapper.DataColumn)m.Tables["Sales"].Columns["Amount"]).SourceColumn = "ContosoAmount");
        await ChangeSource();
        await WaitSettled(f.Engine);
        var states = await BothDoors(f.Engine);
        AssertHeldUnknown(touched, states, "right after the structure change");
        AssertSameVerdicts(before.Where(p => p.Key == Constant).ToDictionary(p => p.Key, p => p.Value), states, "right after the structure change");
        await CheckNow(f, before.Keys);
        states = await BothDoors(f.Engine);
        Assert.Equal(touched.OrderBy(r => r), Held(states).OrderBy(r => r));
        AssertHeldUnknown(touched, states, "Run while the structure change is live");
        AssertSameVerdicts(before.Where(p => p.Key == Constant).ToDictionary(p => p.Key, p => p.Value), states, "Run while the structure change is live");
        var constant = states.Single(s => s.MeasureRef == Constant);
        Assert.True(constant.Marks.Single(m => m.Name == "Contexts").Verdict == "verified" && constant.CheckedOn?.Contains("|" + f.Sessions.Current.Revision + "|") == true,
            "a measure the change does not touch stopped checking: " + Marks(constant));
        var card = await MeasureMarksTests.Finish(f.Engine, Broken);
        Assert.Contains(card.Marks, m => (m.Sentence ?? "").Contains(Hold));

        await f.Engine.UndoAsync("human");
        await WaitSettled(f.Engine);
        states = await CheckNow(f, before.Keys);
        Assert.True(Held(await BothDoors(f.Engine)).Length == 0, "after Undo: " + string.Join(" | ", states.Select(Marks)));
        AssertSameVerdicts(before, states, "after Undo and Run");

        await f.Engine.RedoAsync("human");
        states = await CheckNow(f, before.Keys);
        Assert.Equal(touched.OrderBy(r => r), Held(await BothDoors(f.Engine)).OrderBy(r => r));
        AssertHeldUnknown(touched, states, "after Redo and Run");
        await f.Engine.UndoAsync("human");
        states = await CheckNow(f, before.Keys);
        Assert.True(Held(await BothDoors(f.Engine)).Length == 0, "after Redo then Undo: " + string.Join(" | ", states.Select(Marks)));
        AssertSameVerdicts(before, states, "after Redo, Undo and Run");
        Assert.DoesNotContain(states, s => s.Verdict == "unknown");
    }
}
