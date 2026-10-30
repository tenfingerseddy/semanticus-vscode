using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.Decisions;
using Semanticus.Engine.MeasureContexts;
using Xunit;

namespace Semanticus.Tests;

// v-fx29 (verifier of fx29, diff 5c7389643..c5d17b704).
// Gap 1 at its cause: every reason a Totals or Description score is skipped reads its own true line; only a real service fault
// keeps the calm "turns on when Jev scoring is released" line (brief v-fx29 step 2). A model that has never been saved is not a
// service fault: scoring is released and licensed, and saving the model is what the person can do. fx29 sends it to the calm line.
[Collection("restore-root")]
public sealed class CheckedVFx29Tests : IDisposable
{
    private readonly string safeRoot;
    private readonly string root = Path.Combine(Path.GetTempPath(), "vfx29-" + Guid.NewGuid().ToString("N")[..8]);
    public CheckedVFx29Tests(RestoreRootFixture fixture) { safeRoot = fixture.Root; ConnectionRegistry.RootOverride = root; }
    public void Dispose()
    {
        ConnectionRegistry.RootOverride = safeRoot;
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }

    // Gap 1, Turn on: once the person gives permission, the Totals mark no longer says "Pro advice is off for this model."
    // Live on pilot2 (v-fx29 logs/runs.txt E): set_consent, then get_measure_marks with no refresh still read the line at 0 s and
    // 8 s. The Properties card re-reads on a change without a refresh, so after Turn on it keeps "Pro advice is off. Turn on".
    [Fact]
    public async Task After_Turn_on_the_Totals_mark_no_longer_says_advice_is_off()
    {
        using var f = new MeasureMarksDoorTests.Fixture(root);
        await f.Open();
        // fx30e K5: a real state (a relay, permission off), so the stored line is what the state says, not a stub's word.
        f.Engine.UseDecisionsForTest(new DecisionService(new CheckedFx29cTests.Relay()));
        var given = false;
        f.Engine.TotalsScorer = new JevTotalsScorer((_, _, _, _) => Task.FromResult(given
            ? DecisionOutcome.Skipped("no suggestion service is set up, so nothing was sent and nothing changed")
            : DecisionOutcome.Skipped("you have not given permission to send this model's details for advice")));
        var first = await MeasureMarksTests.Finish(f.Engine, "measure:Sales/Total Sales");
        Assert.EndsWith("Pro advice is off for this model.", first.Marks.Single(m => m.Name == "Totals").Sentence);
        var consent = await f.Engine.GetConsentAsync();
        given = true;
        var after = await f.Engine.SetConsentAsync(consent.FieldsInForce.ToArray(), consent.SchemaVersionInForce, "human");
        Assert.True(after.Current);
        string sentence = null;
        for (var i = 0; i < 100; i++)
        {
            var marks = await f.Engine.GetMeasureMarksAsync("measure:Sales/Total Sales", null, false, "human");
            sentence = marks.Marks.Single(m => m.Name == "Totals").Sentence;
            if (!marks.Running && sentence?.EndsWith("Pro advice is off for this model.") != true) break;
            await Task.Delay(100);
        }
        Assert.False(sentence?.EndsWith("Pro advice is off for this model.") == true, "10 s after Turn on the Totals mark still reads: " + sentence);
    }

    private const string NoScope = "this model has not been saved or opened live, so there is no permission that belongs to it";

    [Fact]
    public void A_never_saved_model_does_not_read_the_not_released_line()
    {
        var reason = ScoringCopy.Reason(DecisionOutcome.Skipped(NoScope));
        Assert.NotEqual(ScoringCopy.Missing, reason);
        // It is a cause the person can act on, so the doors keep it (MarkSentence), and the card and Checks page can show it.
        var mark = ScoringCopy.NotScored("Totals", reason);
        Assert.NotEqual(ScoringCopy.Sentence(ScoringCopy.Missing), ScoringCopy.MarkSentence(mark));
    }

    // Gap 8 attacks: a date measure reads in its own format in a mark cell, for the formats the brief names.
    [Theory]
    [InlineData("yyyy-mm-dd", "2016-09-12")]
    [InlineData("dd/MM/yyyy", "12/09/2016")]
    [InlineData("\"Day \"d\" of \"mmmm yyyy", "Day 12 of September 2016")]
    [InlineData("dddd, mmmm d, yyyy", "Monday, September 12, 2016")]
    [InlineData("\"No date here\"", "12 Sep 2016")]
    public void A_date_cell_reads_in_its_measure_format(string format, string shown)
        => Assert.Equal(shown, MarkNumberFormat.DisplayText(new DateTime(2016, 9, 12), format));

    // A date the engine wrote as text earlier (a saved cell) reads the same way.
    [Theory]
    [InlineData("yyyy-mm-dd", "12 Sep 2016", "2016-09-12")]
    [InlineData("dd/MM/yyyy", "2016-09-12T00:00:00", "12/09/2016")]
    public void A_saved_date_text_reads_in_its_measure_format(string format, string saved, string shown)
        => Assert.Equal(shown, MarkNumberFormat.DisplayText(saved, format));
}
