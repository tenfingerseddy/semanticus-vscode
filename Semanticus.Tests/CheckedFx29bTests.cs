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

// fx29b: the items fx29 left, at their cause.
// B: get_measure_marks read the calm "turns on when Jev scoring is released" line on its Alignment mark after a real
// Description score, while the card (get_measure_description) showed the score. The Alignment slot was filled once with
// ScoringCopy.NotScored and nothing ever wrote the score into it; the card read the Description score cache. Golden rule 2:
// one engine, two doors, one answer.
// C: MeasureMarksDoorTests.Removing_a_dependency_cannot_pass_against_the_unchanged_live_definition (moved here, inside the
// Checked gate). A real fault: since fx28 a measure delete is not a structure edit, so a measure that used the deleted one
// was compared, and its after query ran "[Total Sales]" on the connected model, where Total Sales still exists. Equivalence
// passed, and Filters and Contexts verified, for a formula that no longer works in the model being edited.
// G: the race reason "permission was taken back and given again" read "Pro advice is off for this model." while it was on.
// I: a locale-prefixed date format ([$-en-US]dd/mm/yyyy) fell back to "12 Sep 2016".
[Collection("restore-root")]
public sealed class CheckedFx29bTests : IDisposable
{
    private const string Ref = "measure:Sales/Total Sales";
    private readonly string safeRoot;
    private readonly string root = Path.Combine(Path.GetTempPath(), "fx29b-" + Guid.NewGuid().ToString("N")[..8]);
    public CheckedFx29bTests(RestoreRootFixture fixture) { safeRoot = fixture.Root; ConnectionRegistry.RootOverride = root; }
    public void Dispose()
    {
        ConnectionRegistry.RootOverride = safeRoot;
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }

    private sealed class Scorer : IDescriptionMatchScorer
    {
        private readonly Func<DescriptionMatchInput, Task<DescriptionMatch>> score;
        internal Scorer(Func<DescriptionMatchInput, Task<DescriptionMatch>> score) => this.score = score;
        public Task<DescriptionMatch> ScoreAsync(DescriptionMatchInput input, CancellationToken cancellationToken) => score(input);
    }

    // scored: Jev answered. unscored: the measure has no Description, so nothing is asked. advice-off: a Pro engine with
    // permission not given for this model; the line names the cause Turn on fixes.
    [Theory]
    [InlineData("scored", "Total units sold.", "Description score 83, Matches. The Description statements match the formula.", "passed", true)]
    // fx29c: no Description is not a service fault; the line says there is none to score.
    [InlineData("unscored", "", "Not scored yet. There is no Description to score.", "not run", false)]
    [InlineData("advice-off", "Total units sold.", "Not scored yet. Pro advice is off for this model.", "not run", false)]
    public async Task The_assistant_door_reads_the_same_Description_score_as_the_card(string kind, string description,
        string sentence, string state, bool applies)
    {
        using var f = new MeasureMarksDoorTests.Fixture(root);
        await f.Open();
        f.Engine.DescriptionMatchEnabled = true;
        f.Engine.DescriptionMatchScorer = kind == "advice-off"
            ? new JevDescriptionMatchScorer((_, _) => Task.FromResult(
                DecisionOutcome.Skipped("you have not given permission to send this model's details for advice")))
            : new Scorer(_ => Task.FromResult(new DescriptionMatch(83, "matches", "The Description statements match the formula.", "Matches")));
        await f.Sessions.Current.MutateAsync("human", "Contoso description", m => m.Tables["Sales"].Measures["Total Sales"].Description = description);

        // The card: the score arrives in the background and the text never waits for it.
        var clock = Stopwatch.StartNew();
        var card = await f.Engine.GetMeasureDescriptionAsync(Ref);
        while (kind == "scored" && card.Match.Score == null && clock.Elapsed < TimeSpan.FromSeconds(10))
        { await Task.Delay(20); card = await f.Engine.GetMeasureDescriptionAsync(Ref); }
        Assert.Equal(kind == "scored" ? 83 : null, card.Match.Score);

        // The assistant door, while its checks run and after they finish, and the check cache door.
        var first = (await McpTools.GetMeasureMarks(f.Engine, Ref)).Marks.Single(m => m.Name == "Alignment");
        var done = (await MeasureMarksTests.Finish(f.Engine, Ref)).Marks.Single(m => m.Name == "Alignment");
        var cache = (await McpTools.GetMeasureCheckCache(f.Engine)).Single(s => s.MeasureRef == Ref).Marks.Single(m => m.Name == "Alignment");
        var run = (await f.Engine.RunMeasurePlanAsync((await f.Engine.PlanMeasureRunAsync(new MeasureRunRequest { Cap = 25 })).Id))
            .Results.Single(s => s.MeasureRef == Ref).Marks.Single(m => m.Name == "Alignment");
        foreach (var mark in new[] { first, done, cache, run })
        {
            Assert.Equal(sentence, mark.Sentence);
            Assert.Equal(state, mark.State);
            Assert.Equal(applies, mark.Applies);
            Assert.Equal(card.Match.Score == null ? "Not scored yet. " + card.Match.Reason
                : $"Description score {card.Match.Score}, {card.Match.Label}. {card.Match.Reason}", mark.Sentence);
        }
        // The Description score is never one of the seven marks: it does not set the measure's verdict.
        var state0 = (await f.Engine.GetMeasureCheckCacheAsync()).Single(s => s.MeasureRef == Ref);
        Assert.Equal(MeasureCheckVerdict.Worst(state0.Marks.Where(m => m.Name != "Alignment")), state0.Verdict);
    }

    [Fact]
    public async Task Removing_a_dependency_cannot_pass_against_the_unchanged_live_definition()
    {
        using var f = new MeasureMarksDoorTests.Fixture(root); await f.Open(2);
        await f.Sessions.Current.MutateAsync("human", "Contoso constant", m => m.Tables["Sales"].AddMeasure("Contoso constant", "300"));
        await f.Sessions.Current.MutateAsync("human", "Remove Contoso dependency", m => m.Tables["Sales"].Measures["Total Sales"].Delete());
        const string reason = "The formula uses [Total Sales], which is not in this model. Fix the formula, then check it again.";
        // The comparison the delete started, then the checks a read starts: none of them passes, each says why, on both doors.
        var edit = await MeasureMarksTests.Finish(f.Engine, "measure:Sales/Sales Copy 1");
        var equivalence = edit.Marks.Single(m => m.Name == "Equivalence");
        Assert.Equal("not run", equivalence.State);
        Assert.Equal(reason, equivalence.Sentence);
        Assert.Equal("unknown", equivalence.Verdict);
        var read = await MeasureMarksTests.Finish(f.Engine, "measure:Sales/Sales Copy 1");
        foreach (var name in new[] { "Filters", "Contexts" })
        {
            var mark = read.Marks.Single(m => m.Name == name);
            Assert.True(mark.State == "not run" && mark.Sentence == reason && mark.Verdict == "unknown", $"{name}: {mark.State} {mark.Verdict} {mark.Sentence}");
        }
        Assert.DoesNotContain(read.Marks, m => m.Verdict == "verified");
        var state = (await McpTools.GetMeasureCheckCache(f.Engine)).Single(s => s.MeasureRef == "measure:Sales/Sales Copy 1");
        Assert.Equal("unknown", state.Verdict);
        // A measure the delete does not touch is still checked as before (the fx28 point: a delete is not a structure edit).
        var constant = await MeasureMarksTests.Finish(f.Engine, "measure:Sales/Contoso constant");
        Assert.Equal("verified", constant.Marks.Single(m => m.Name == "Contexts").Verdict);
        // Putting the measure back makes the formula whole again, and its checks run.
        await f.Sessions.Current.MutateAsync("human", "Restore Contoso dependency", m => m.Tables["Sales"].AddMeasure("Total Sales", "SUM('Sales'[Amount])"));
        var whole = await MeasureMarksTests.Finish(f.Engine, "measure:Sales/Sales Copy 1");
        Assert.Equal("verified", whole.Marks.Single(m => m.Name == "Contexts").Verdict);
    }

    // G: permission moved while a score was on its way. Advice is on, so the line says what happened and what to do.
    [Fact]
    public async Task Permission_given_again_mid_score_does_not_read_advice_is_off()
    {
        const string race = "permission was taken back and given again while this suggestion was being prepared, so it was dropped";
        var reason = ScoringCopy.Reason(DecisionOutcome.Skipped(race));
        Assert.NotEqual(ScoringCopy.AdviceOff, reason);
        Assert.NotEqual(ScoringCopy.Missing, reason);
        Assert.Equal("Not scored yet. Pro advice changed while this was being scored. Check again to score it.", ScoringCopy.Sentence(reason));
        var totals = new JevTotalsScorer((_, _, _, _) => Task.FromResult(DecisionOutcome.Skipped(race)));
        var mark = await totals.ScoreAsync(new("Total Quantity", "", "SUM ( Sales[Quantity] )"), Array.Empty<TotalProbe>(), new(), false, CancellationToken.None);
        Assert.Equal(ScoringCopy.Sentence(reason), ScoringCopy.MarkSentence(mark));
        var match = await new JevDescriptionMatchScorer((_, _) => Task.FromResult(DecisionOutcome.Skipped(race)))
            .ScoreAsync(new("Total units sold.", "SUM ( Sales[Quantity] )", "Total Quantity"), CancellationToken.None);
        Assert.Equal(reason, match.Reason);
    }

    // I: Power BI writes a locale in front of a date format. The locale tag is not part of the date.
    [Theory]
    [InlineData("[$-en-US]dd/mm/yyyy", "12/09/2016")]
    [InlineData("[$-409]mmmm d, yyyy", "September 12, 2016")]
    [InlineData("[$-en-GB]yyyy-mm-dd", "2016-09-12")]
    public void A_locale_prefixed_date_format_reads_in_its_own_format(string format, string shown)
    {
        Assert.Equal(shown, MarkNumberFormat.DisplayText(new DateTime(2016, 9, 12), format));
        Assert.Equal(shown, MarkNumberFormat.DisplayText("2016-09-12T00:00:00", format));
    }

    // A currency tag in brackets is a number format, not a date: it keeps the date rule.
    [Fact]
    public void A_bracketed_currency_format_is_not_read_as_a_date()
        => Assert.Equal("12 Sep 2016", MarkNumberFormat.DisplayText(new DateTime(2016, 9, 12), "[$€-407]#,0.00"));

    // A score that does not match is a reason to look (d1 s2: Needs review); one that could be improved is a score only.
    [Theory]
    [InlineData(40, "does not match", "Does not match", "suspect", "review")]
    [InlineData(55, "could be improved", "Could be improved", "info", null)]
    public async Task A_low_score_reads_on_the_assistant_door_as_the_card_reads_it(int score, string level, string label, string state, string verdict)
    {
        using var f = new MeasureMarksDoorTests.Fixture(root);
        await f.Open();
        f.Engine.DescriptionMatchEnabled = true;
        f.Engine.DescriptionMatchScorer = new Scorer(_ => Task.FromResult(new DescriptionMatch(score, level, "Some Description statements may differ.", label)));
        await f.Sessions.Current.MutateAsync("human", "Contoso description", m => m.Tables["Sales"].Measures["Total Sales"].Description = "Total units sold.");
        var clock = Stopwatch.StartNew();
        while ((await f.Engine.GetMeasureDescriptionAsync(Ref)).Match.Score == null && clock.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(20);
        var mark = (await MeasureMarksTests.Finish(f.Engine, Ref)).Marks.Single(m => m.Name == "Alignment");
        Assert.Equal($"Description score {score}, {label}. Some Description statements may differ.", mark.Sentence);
        Assert.Equal(state, mark.State);
        Assert.Equal(verdict, mark.Verdict);
    }
}
