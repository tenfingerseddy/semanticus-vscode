using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.Decisions;
using Semanticus.Engine.MeasureContexts;
using Xunit;

namespace Semanticus.Tests;

// fx29: the rc28 release tester's gaps 1, 7 and 8, at their cause in the engine.
// Gap 1: a licensed engine with Pro advice off for the model read "This score turns on when Jev scoring is released." on Totals
// and the Description score: ScoringCopy.Reason sent every permission reason to the calm line, which says the feature is not out.
// Gap 7: a measure added on a connected model read "Unknown: Equivalence could not run" before any check ran.
// Gap 8: a date measure with its own format (yyyy-mm-dd) read "12 Sep 2016" in its mark cells and sentence.
[Collection("restore-root")]
public sealed class CheckedFx29Tests : IDisposable
{
    private const string AdviceOff = "Not scored yet. Pro advice is off for this model.";
    private readonly string safeRoot;
    private readonly string root = Path.Combine(Path.GetTempPath(), "fx29-" + Guid.NewGuid().ToString("N")[..8]);
    public CheckedFx29Tests(RestoreRootFixture fixture) { safeRoot = fixture.Root; ConnectionRegistry.RootOverride = root; }
    public void Dispose()
    {
        ConnectionRegistry.RootOverride = safeRoot;
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }

    // Every way DecisionConsent.Covers says permission is missing: Turn on is the fix, so the line says advice is off.
    [Theory]
    [InlineData("you have not given permission to send this model's details for advice")]
    [InlineData("you withdrew permission to send this model's details for advice")]
    [InlineData("the list of details that would be sent has changed since you agreed, so permission is needed again")]
    [InlineData("permission does not cover every detail this would send (measure.totals_type:formula)")]
    public async Task Pro_advice_off_reads_its_own_line_on_Totals_and_the_Description_score(string reason)
    {
        Assert.Equal(AdviceOff, ScoringCopy.Sentence(ScoringCopy.Reason(DecisionOutcome.Skipped(reason))));
        var totals = new JevTotalsScorer((_, _, _, _) => Task.FromResult(DecisionOutcome.Skipped(reason)));
        var mark = await totals.ScoreAsync(new("Total Quantity", "", "SUM ( Sales[Quantity] )"), Array.Empty<TotalProbe>(), new(), false, CancellationToken.None);
        Assert.Equal(AdviceOff, mark.Sentence);
        // Both doors read the mark through CopyMarks, which keeps an actionable line and calms the rest.
        Assert.Equal(AdviceOff, ScoringCopy.MarkSentence(mark));
        var scorer = new JevDescriptionMatchScorer((_, _) => Task.FromResult(DecisionOutcome.Skipped(reason)));
        var match = await scorer.ScoreAsync(new("Total units sold.", "SUM ( Sales[Quantity] )", "Total Quantity"), CancellationToken.None);
        Assert.Null(match.Score);
        Assert.Equal("Pro advice is off for this model.", match.Reason);
    }

    // The calm line stays only for a real service fault. A never-saved model has its own line (fx29b E, CheckedVFx29Tests).
    // fx29c: "no suggestion service is set up" is not a fault. The relay client is built only from a licence, so it means the
    // session has no licence to present (the rc29 capture: tier pro, no licence), and the true line is "needs a Pro plan".
    [Theory]
    [InlineData("the template or the reply was not usable")]
    public void A_service_side_cause_keeps_the_calm_line(string reason)
        => Assert.Equal(ScoringCopy.Missing, ScoringCopy.Reason(DecisionOutcome.Skipped(reason)));

    [Fact]
    public void No_relay_client_means_no_licence_to_present()
        => Assert.Equal(ScoringCopy.NeedsPro, ScoringCopy.Reason(DecisionOutcome.Skipped("no suggestion service is set up, so nothing was sent and nothing changed")));

    // Gap 1 end to end: a Pro engine on a connected model with no permission given. The Totals mark of a run reads the line.
    [Fact]
    public async Task A_licensed_run_with_advice_off_says_so_on_the_Totals_mark()
    {
        using var f = new MeasureMarksDoorTests.Fixture(root);
        await f.Open();
        // fx30e K5: a real state (a relay, permission off), so the stored line is what the state says, not a stub's word.
        f.Engine.UseDecisionsForTest(new DecisionService(new CheckedFx29cTests.Relay()));
        f.Engine.TotalsScorer = new JevTotalsScorer((_, _, _, _) => Task.FromResult(
            DecisionOutcome.Skipped("you have not given permission to send this model's details for advice")));
        var result = await MeasureMarksTests.Finish(f.Engine, "measure:Sales/Total Sales");
        var totals = result.Marks.Single(m => m.Name == "Totals");
        Assert.Equal(AdviceOff, totals.Sentence);
        Assert.False(totals.Applies);
        var agent = (await McpTools.GetMeasureCheckCache(f.Engine)).Single(s => s.MeasureRef == "measure:Sales/Total Sales");
        Assert.Equal(AdviceOff, agent.Marks.Single(m => m.Name == "Totals").Sentence);
    }

    // Gap 7: a new measure has no before, so Equivalence does not apply to it. It never reads Unknown before its checks run,
    // and after they run it has its normal marks.
    [Fact]
    public async Task A_new_measure_has_no_Unknown_before_or_after_its_checks_run()
    {
        using var f = new MeasureMarksDoorTests.Fixture(root);
        await f.Open(3);
        var first = await f.Engine.RunMeasurePlanAsync((await f.Engine.PlanMeasureRunAsync(new MeasureRunRequest { Cap = 25 })).Id);
        Assert.False(first.Stopped);
        var added = await f.Engine.CreateMeasureAsync("table:Sales", "New Measure", "0", "human");
        var clock = Stopwatch.StartNew();
        string seen = null;
        while (clock.Elapsed < TimeSpan.FromSeconds(10))
        {
            var state = (await f.Engine.GetMeasureCheckCacheAsync()).FirstOrDefault(s => s.MeasureRef == added);
            var agent = (await McpTools.GetMeasureCheckCache(f.Engine)).FirstOrDefault(s => s.MeasureRef == added);
            foreach (var s in new[] { state, agent }.Where(s => s != null))
                if (s.Verdict == "unknown" || s.Marks.Any(m => m.Applies && m.Verdict == "unknown"))
                    seen ??= $"{clock.ElapsedMilliseconds} ms: " + string.Join(", ", s.Marks.Select(m => $"{m.Name}:{m.State}:{m.ReasonCode}:{m.Verdict}:{m.Sentence}"));
            if (state != null && !state.Running && state.Marks.All(m => !m.Pending) && clock.Elapsed > TimeSpan.FromSeconds(2)) break;
            await Task.Delay(50);
        }
        Assert.True(seen == null, "the new measure read Unknown at " + seen);
        var card = await MeasureMarksTests.Finish(f.Engine, added);
        var equivalence = card.Marks.Single(m => m.Name == "Equivalence");
        Assert.False(equivalence.Applies, $"Equivalence applies to a new measure: {equivalence.State} {equivalence.ReasonCode} {equivalence.Sentence}");
        Assert.DoesNotContain(card.Marks, m => m.Applies && m.Verdict == "unknown");
        Assert.Contains(card.Marks, m => m.Name == "Contexts" && m.Verdict == "verified");
    }

    // Gap 8: a date measure's mark cells and its Filters sentence read in its own format.
    [Theory]
    [InlineData("yyyy-mm-dd", "2016-09-12")]
    [InlineData("dd/mm/yyyy", "12/09/2016")]
    [InlineData("mmm yyyy", "Sep 2016")]
    [InlineData(null, "12 Sep 2016")]
    public void A_date_measure_reads_in_its_own_format(string format, string shown)
    {
        var column = new ContextColumn("Date", "Year");
        var date = new DateTime(2016, 9, 12);
        var probe = new ProbeResult { Status = "ok", Scenarios = new[] {
            new ScenarioEvidence { Name = "grand total", Status = "ok", Rows = new[] { new ProbeRow { V = date } } },
            new ScenarioEvidence { Name = "by 'Date'[Year]", Status = "ok", Rows = new[] {
                new ProbeRow { V = date, Members = new() { [column.Dax] = 2016L } },
                new ProbeRow { V = date, Members = new() { [column.Dax] = 2017L } }
            } },
            new ScenarioEvidence { Name = "single-select 'Sales'[Quantity]", Status = "ok", Rows = new[] { new ProbeRow { V = new DateTime(2016, 9, 28) } } }
        } };
        var mark = MeasureMarkJudge.FilterIntent(new MeasureMark { Name = "Filters", State = "passed" }, probe,
            new[] { new ContextFilterClear(column, "ALLEXCEPT") }, false, format);
        Assert.Contains("The grand total is " + shown + ".", mark.Sentence);
        MarkNumberFormat.FillCells(mark, format);
        Assert.Equal(shown, mark.Cells.Single(c => c.Context == "Grand total").AfterText);
        Assert.All(mark.Cells, c => Assert.DoesNotContain(shown.Contains(" Sep ") ? "2016-09" : " Sep 2016", c.AfterText ?? ""));
    }

    // A number format is not a date format: a date under "#,0" keeps the date rule.
    [Fact]
    public void A_number_format_is_not_read_as_a_date()
        => A_date_measure_reads_in_its_own_format("#,0", "12 Sep 2016");
}
