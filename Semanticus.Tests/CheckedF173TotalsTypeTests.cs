using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Decisions;
using Semanticus.Engine;
using Semanticus.Engine.Decisions;
using Xunit;

namespace Semanticus.Tests;

// f173 (Kane, 1 Nov, CORR-0765): "The requested behaviour for totals was to have Jev choose then show the type used". In rc2 a
// Totals Jev typed but did not judge (a distinct count, a type Jev is not sure of, a type under its cut, no place to probe) did
// not apply, so most count and ratio measures showed no Totals at all. Now it applies and names the type, with no verdict, so
// the measure's verdict and the Checks counts stay as they were. Totals stays hidden only when it has no result.
[Collection("restore-root")]
public sealed class CheckedF173TotalsTypeTests : IDisposable
{
    private const string Reference = "measure:Sales/Total Sales";
    private static readonly TotalsInput Input = new("Contoso customers", "Counts the Contoso customers.", "DISTINCTCOUNT ( Sales[CustomerKey] )");
    private static readonly TotalProbe[] Probes = { new("Product[Category]", new[] {
        new TotalProbeRow("Bikes", 10), new TotalProbeRow("Accessories", 20), new TotalProbeRow("Clothing", 30) }, 45) };
    private readonly string safeRoot;
    private readonly string root = Path.Combine(Path.GetTempPath(), "f173-" + Guid.NewGuid().ToString("N")[..8]);
    public CheckedF173TotalsTypeTests(RestoreRootFixture fixture) { safeRoot = fixture.Root; ConnectionRegistry.RootOverride = root; }
    public void Dispose()
    {
        ConnectionRegistry.RootOverride = safeRoot;
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }

    /// <summary>Jev as the relay answers: the type with a confidence, then the fit of each probe.</summary>
    internal static JevTotalsScorer Jev(string rule, double confidence, double fit = .9) => new((point, state, questions, ct) =>
    {
        var request = new DecisionRequest(point, 1, state, questions);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(point == TotalsQuestions.TypePoint
            ? (object)new { rule = new { type = "choice", choice = rule, confidence } }
            : new { fits = new { type = "noul", noul = fit } }));
        return Task.FromResult(DecisionOutcome.Answered(DecisionAnswerReader.Read(request, json.RootElement, "Contoso fake")));
    });

    private static MeasureMark Filters() =>
        MeasureCheckVerdict.Finish(new MeasureMark { Name = "Filters", State = "passed", Sentence = "No ignored filter in 4 places." }, null, false);

    [Theory]
    [InlineData("distinct_count", .99, "Checked as a distinct count. Distinct counts do not add up, so the total is shown, not judged.")]
    [InlineData("recomputed_at_total", .4, "Checked as a recalculation. Jev is not sure of the type, so the total is shown, not judged.")]
    [InlineData("average", .3, "Checked as an average. Jev is not sure of the type, so the total is shown, not judged.")]
    [InlineData("sum_of_rows", .7, "Checked as a sum. Jev is not sure enough of the type to judge it, so the total is shown, not judged.")]
    public async Task A_Totals_Jev_typed_but_did_not_judge_applies_names_the_type_and_has_no_verdict(string rule, double confidence, string sentence)
    {
        var totals = MeasureCheckVerdict.Finish(await Jev(rule, confidence).ScoreAsync(Input, Probes, new(), false, CancellationToken.None), null, false);
        Assert.True(totals.Applies, $"{rule} at {confidence} is hidden: {totals.ReasonCode}");
        Assert.Null(totals.Verdict);
        Assert.Equal("info", totals.State);
        Assert.Equal(sentence, totals.Sentence);
        Assert.Equal(rule, totals.Total.Rule);
        Assert.Equal(confidence, totals.Total.Confidence);
        Assert.DoesNotContain("—", totals.Sentence);
        // The measure's verdict is the same with it as without it.
        Assert.Equal("verified", MeasureCheckVerdict.Worst(new[] { totals, Filters() }));
        Assert.Null(MeasureCheckVerdict.Worst(new[] { totals }));
    }

    [Fact]
    public async Task A_Totals_with_no_place_to_probe_names_the_type()
    {
        var totals = MeasureCheckVerdict.Finish(await Jev("sum_of_rows", .95).ScoreAsync(Input, Array.Empty<TotalProbe>(), new(), false, CancellationToken.None), null, false);
        Assert.True(totals.Applies);
        Assert.Null(totals.Verdict);
        Assert.Equal("Checked as a sum. No place was found to check the total, so it is shown, not judged.", totals.Sentence);
    }

    // Totals stays hidden only when it truly has no result.
    [Theory]
    [InlineData("totals_off")] [InlineData("not_scored")] [InlineData("not_connected")]
    public void A_Totals_with_no_result_stays_hidden(string code)
    {
        var totals = MeasureCheckVerdict.Finish(new MeasureMark { Name = "Totals", State = "not run", Sentence = "Not scored yet.", ReasonCode = code }, null, false);
        Assert.False(totals.Applies);
        Assert.Null(totals.Verdict);
    }

    [Fact]
    public async Task Jev_unreachable_keeps_Totals_hidden()
    {
        var scorer = new JevTotalsScorer((_, _, _, _) => throw new System.Net.Http.HttpRequestException("Contoso relay down"));
        var totals = MeasureCheckVerdict.Finish(await scorer.ScoreAsync(Input, Probes, new(), false, CancellationToken.None), null, false);
        Assert.False(totals.Applies);
    }

    private static string Marks(MeasureMark[] marks) => string.Join(", ", marks.Select(m => $"{m.Name}:{m.State}:{m.ReasonCode}:{m.Applies}:{m.Verdict}:{m.Sentence}"));

    private static async Task<MeasureCheckCache[]> CheckNow(LocalEngine engine)
    {
        var run = await engine.RunMeasurePlanAsync((await engine.PlanMeasureRunAsync(new MeasureRunRequest { Selection = new() { Measures = new[] { Reference } }, Cap = 25 })).Id);
        Assert.False(run.Stopped || run.StoppedOnEdit);
        var clock = Stopwatch.StartNew();
        var states = await engine.GetMeasureCheckCacheAsync();
        while (states.Any(s => s.Running || s.Marks.Any(m => m.Pending || m.Progress != null)) && clock.Elapsed < TimeSpan.FromSeconds(20))
        { await Task.Delay(25); states = await engine.GetMeasureCheckCacheAsync(); }
        return states;
    }

    /// <summary>The measure checked with this scorer: its verdict and every copy of its Totals the screens and the assistant get.</summary>
    private async Task<(string Verdict, (string Door, MeasureMark Totals)[] Doors)> Checked(string name, ITotalsScorer scorer)
    {
        using var f = new MeasureMarksDoorTests.Fixture(Path.Combine(root, name));
        await f.Open();
        f.Engine.TotalsScorer = scorer;
        var state = (await CheckNow(f.Engine)).Single(s => s.MeasureRef == Reference);
        var doors = new (string, MeasureMark[])[]
        {
            ("rpc states", state.Marks),
            ("mcp states", (await McpTools.GetMeasureCheckCache(f.Engine)).Single(s => s.MeasureRef == Reference).Marks),
            ("rpc marks", (await f.Engine.GetMeasureMarksAsync(Reference)).Marks),
            ("mcp marks", (await McpTools.GetMeasureMarks(f.Engine, Reference)).Marks)
        };
        return (state.Verdict, doors.Select(d => (d.Item1, d.Item2.Single(m => m.Name == "Totals"))).ToArray());
    }

    [Theory]
    [InlineData("distinct_count", .99, "distinct count")]
    [InlineData("recomputed_at_total", .4, "recalculation")]
    public async Task Every_door_shows_the_type_Jev_chose_and_the_measure_verdict_does_not_move(string rule, double confidence, string type)
    {
        var (verdict, doors) = await Checked("typed", Jev(rule, confidence));
        foreach (var (door, totals) in doors)
        {
            Assert.True(totals.Applies && totals.Verdict == null && totals.State == "info", $"{door}: {Marks(new[] { totals })}");
            Assert.StartsWith("Checked as a " + type + ".", totals.Sentence);
            Assert.Equal(rule, totals.Total?.Rule);
        }
        // The same measure with no Totals result reads the same verdict, so the Checks counts do not move either.
        var (without, _) = await Checked("unscored", new JevTotalsScorer((_, _, _, _) => throw new System.Net.Http.HttpRequestException("Contoso relay down")));
        Assert.Equal(without, verdict);
    }
}
