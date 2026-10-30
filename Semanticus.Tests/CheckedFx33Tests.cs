using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests;

// fx33 (rc32 tester, gap 1): on pilot2, Sales[Net Price] source column NetPrice -> UnitPrice. The engine held Sales Amount and
// Gross Profit, but it sent each held mark as a pass with "Publish this change to check it." after the pass sentence, so the tree
// row, hover, Properties card, Filters detail and Checks page card all read "Verified by 2 checks", and no screen showed the
// line. Totals went from passed to hidden. Spec R8 (section 3): Unknown is never a pass, and a mark that applies but cannot run
// is Unknown with its reason. Finish line E2: both doors read the same marks and lines. Expected: while the change is live, each
// mark of a held measure that applied reads Unknown with the line (Totals too, not hidden), at once and after Check now, in the
// RPC rows, the card, the push and the assistant; Undo brings every last mark back at once.
[Collection("restore-root")]
public sealed class CheckedFx33Tests : IDisposable
{
    private const string Broken = "measure:Sales/Total Sales";
    private const string Constant = "measure:Sales/Contoso constant";
    private const string Hold = "Publish this change to check it.";
    private readonly string safeRoot;
    private readonly string root = Path.Combine(Path.GetTempPath(), "fx33-" + Guid.NewGuid().ToString("N")[..8]);
    public CheckedFx33Tests(RestoreRootFixture fixture) { safeRoot = fixture.Root; ConnectionRegistry.RootOverride = root; }
    public void Dispose()
    {
        ConnectionRegistry.RootOverride = safeRoot;
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }

    /// <summary>A sum checked on its rows passes; with no probes (nothing ran on the connected model) it is shown, not judged.</summary>
    private sealed class SumScorer : ITotalsScorer
    {
        public Task<MeasureMark> ScoreAsync(TotalsInput input, TotalProbe[] probes, TotalsSettings settings, bool expected, CancellationToken ct) =>
            Task.FromResult(probes.Length > 0
                ? new MeasureMark { Name = "Totals", State = "passed", Sentence = "Total adds up as a sum, as expected.", LastRan = DateTime.UtcNow.ToString("o") }
                : new MeasureMark { Name = "Totals", State = "info", Sentence = "Totals for this type are shown, not judged.", ReasonCode = "totals_not_judged" });
    }

    private static bool Busy(MeasureCheckCache s) => s.Running || s.Marks.Any(m => m.Pending || m.Progress != null);

    private static async Task<MeasureCheckCache[]> WaitSettled(LocalEngine engine)
    {
        var clock = Stopwatch.StartNew();
        var states = await engine.GetMeasureCheckCacheAsync();
        while (states.Any(Busy) && clock.Elapsed < TimeSpan.FromSeconds(20)) { await Task.Delay(25); states = await engine.GetMeasureCheckCacheAsync(); }
        return states;
    }

    private static async Task<MeasureCheckCache[]> CheckNow(MeasureMarksDoorTests.Fixture f, IEnumerable<string> refs)
    {
        var run = await f.Engine.RunMeasurePlanAsync((await f.Engine.PlanMeasureRunAsync(new MeasureRunRequest { Selection = new() { Measures = refs.ToArray() }, Cap = 25 })).Id);
        Assert.False(run.Stopped || run.StoppedOnEdit);
        return await WaitSettled(f.Engine);
    }

    private static string Marks(MeasureCheckCache s) => s.MeasureRef + " " + s.Verdict + ": " + string.Join(", ", s.Marks.Select(m => $"{m.Name}:{m.State}:{m.Verdict}:{m.Sentence}"));

    private static readonly string[] Judged = { "Totals", "Filters", "SQL", "Trusted number", "Contexts", "Performance" };

    /// <summary>The marks that apply and have a verdict, by name.</summary>
    private static Dictionary<string, string> Applying(MeasureCheckCache s) =>
        s.Marks.Where(m => Judged.Contains(m.Name) && m.Applies && m.Verdict != null).ToDictionary(m => m.Name, m => m.Verdict);

    // Both doors: the tree and Checks rows (RPC) and the assistant (MCP) give the same verdict and the same lines.
    private static async Task<MeasureCheckCache[]> BothDoors(LocalEngine engine)
    {
        var rows = await engine.GetMeasureCheckCacheAsync();
        var agent = await McpTools.GetMeasureCheckCache(engine);
        foreach (var row in rows)
        {
            var other = agent.Single(a => a.MeasureRef == row.MeasureRef);
            Assert.True(other.Verdict == row.Verdict, $"the doors differ on {row.MeasureRef}: {Marks(row)} | {Marks(other)}");
            Assert.Equal(string.Join("|", row.Marks.Select(m => m.Name + m.Verdict + m.Sentence)), string.Join("|", other.Marks.Select(m => m.Name + m.Verdict + m.Sentence)));
        }
        return rows;
    }

    private static void AssertHeld(MeasureCheckCache state, Dictionary<string, string> before, string why)
    {
        Assert.True(state.Verdict == "unknown", $"{why}: a held measure must read Unknown, not {state.Verdict}: {Marks(state)}");
        foreach (var name in before.Keys)
        {
            var mark = state.Marks.Single(m => m.Name == name);
            Assert.True(mark.Applies && mark.Verdict == "unknown" && mark.Sentence == Hold,
                $"{why}: {state.MeasureRef} {name} was {before[name]}; held it must read Unknown with \"{Hold}\": {Marks(state)}");
        }
    }

    private static void AssertSame(IReadOnlyDictionary<string, MeasureCheckCache> before, IEnumerable<MeasureCheckCache> states, string why)
    {
        foreach (var state in states.Where(s => before.ContainsKey(s.MeasureRef)))
        {
            var was = before[state.MeasureRef];
            Assert.True(state.Verdict == was.Verdict && Applying(state).OrderBy(p => p.Key).SequenceEqual(Applying(was).OrderBy(p => p.Key))
                && !state.Marks.Any(m => (m.Sentence ?? "").Contains(Hold)), $"{why}: was {Marks(was)}; now {Marks(state)}");
        }
    }

    [Fact]
    public async Task A_held_measure_reads_Unknown_with_the_line_in_both_doors_and_the_push_and_Undo_brings_its_marks_back()
    {
        using var f = new MeasureMarksDoorTests.Fixture(root); await f.Open(3);
        f.Engine.TotalsScorer = new SumScorer();
        await f.Sessions.Current.MutateAsync("human", "Contoso constant", m => m.Tables["Sales"].AddMeasure("Contoso constant", "300"));
        await f.Engine.SaveTestDefinitionAsync(new TestDefinition
        {
            Kind = TestKinds.MeasureValue, Title = "Contoso trusted total", TargetRef = Broken,
            ParamsJson = TestSuiteStore.Serialize(new MeasureValueRequest { MeasureRef = Broken, ExpectedValue = "999" })
        }, "human");
        Assert.False((await f.Engine.RunMeasurePlanAsync((await f.Engine.PlanMeasureRunAsync(new MeasureRunRequest { Cap = 25 })).Id)).Stopped);
        var first = await WaitSettled(f.Engine);
        var before = first.ToDictionary(s => s.MeasureRef);
        Assert.Equal("broken", before[Broken].Verdict);
        Assert.Equal("verified", before["measure:Sales/Sales Copy 1"].Verdict);
        Assert.Equal("verified", before[Constant].Verdict);
        Assert.True(Applying(before["measure:Sales/Sales Copy 1"]).GetValueOrDefault("Totals") == "verified", "Totals applies before: " + Marks(before["measure:Sales/Sales Copy 1"]));
        var touched = before.Keys.Where(r => r != Constant).ToArray();

        // The push the tree and the pages keep: the newest version per measure.
        var pushed = new ConcurrentDictionary<string, MeasureCheckCache>(StringComparer.Ordinal);
        f.Sessions.Bus.ChecksChanged += change =>
        {
            foreach (var s in change.States) pushed.AddOrUpdate(s.MeasureRef, s, (_, old) => old.Version > s.Version ? old : s);
        };

        await f.Sessions.Current.MutateAsync("human", "Contoso source",
            m => ((TabularEditor.TOMWrapper.DataColumn)m.Tables["Sales"].Columns["Amount"]).SourceColumn = "ContosoAmount");
        await WaitSettled(f.Engine);

        // At once, before any run: every door reads the held measures as Unknown with the line.
        var states = await BothDoors(f.Engine);
        foreach (var reference in touched) AssertHeld(states.Single(s => s.MeasureRef == reference), Applying(before[reference]), "right after the change");
        AssertSame(new Dictionary<string, MeasureCheckCache> { [Constant] = before[Constant] }, states, "a measure the change does not touch");
        foreach (var reference in touched)
            Assert.True(pushed.TryGetValue(reference, out var push) && push.Verdict == "unknown",
                $"the push for {reference} must carry the held state: {(push == null ? "none" : Marks(push))}");
        var card = await MeasureMarksTests.Finish(f.Engine, Broken);
        Assert.True(card.Marks.Where(m => Applying(before[Broken]).ContainsKey(m.Name)).All(m => m.Verdict == "unknown" && m.Sentence == Hold),
            "the card: " + string.Join(", ", card.Marks.Select(m => $"{m.Name}:{m.Verdict}:{m.Sentence}")));

        // Check now while the change is live: still held, Totals still shown as Unknown (not hidden).
        states = await CheckNow(f, before.Keys);
        states = await BothDoors(f.Engine);
        foreach (var reference in touched) AssertHeld(states.Single(s => s.MeasureRef == reference), Applying(before[reference]), "Check now while the change is live");
        var constant = states.Single(s => s.MeasureRef == Constant);
        Assert.True(constant.Verdict == "verified" && constant.CheckedOn?.Contains("|" + f.Sessions.Current.Revision + "|") == true,
            "a measure the change does not touch stopped checking: " + Marks(constant));

        // Undo: every last mark is back at once, in both doors and the push, and Check now agrees.
        pushed.Clear();
        await f.Engine.UndoAsync("human");
        await WaitSettled(f.Engine);
        AssertSame(before, await BothDoors(f.Engine), "right after Undo");
        foreach (var reference in touched)
            Assert.True(pushed.TryGetValue(reference, out var push) && push.Verdict == before[reference].Verdict,
                $"the push after Undo for {reference}: {(push == null ? "none" : Marks(push))}");
        states = await CheckNow(f, before.Keys);
        AssertSame(before, await BothDoors(f.Engine), "Check now after Undo");
    }
}
