using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Decisions;
using Semanticus.Engine;
using Semanticus.Engine.Decisions;
using Semanticus.Engine.MeasureContexts;
using Xunit;

namespace Semanticus.Tests;

// L2 (1.7 items 1, 2 and 3): no mark passes on weak evidence. Totals reads Verified only at a pass cut measured on a
// held-out set; between the warn and pass cuts it is Unknown. Filters never passes from Description words, only from
// the person's "It is on purpose". A lost filter offers "Fix the formula" as well as "It is on purpose".
[Collection("restore-root")]
public sealed class CheckedL2Tests
{
    private static readonly string[] Rules = { "sum_of_rows", "recomputed_at_total", "last_value_over_time",
        "first_value_over_time", "distinct_count", "average", "blank_at_total" };
    private static readonly TotalsInput Input = new("Total Quantity", "Adds the Contoso quantities.", "SUM(Sales[Quantity])");
    private static TotalProbe Probe(double total) => new("Product[Category]", new[] {
        new TotalProbeRow("Bikes", 10), new TotalProbeRow("Accessories", 20), new TotalProbeRow("Clothing", 30) }, total);

    // Answers the type question with rule and confidence, then each fit question with the next fit in order.
    private static JevTotalsScorer Fake(string rule, double confidence, params double[] fits)
    {
        var next = 0;
        return new((point, state, questions, ct) => {
            var request = new DecisionRequest(point, 1, state, questions);
            var probabilities = Rules.ToDictionary(r => r, r => r == rule ? confidence : (1 - confidence) / 6);
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(point == TotalsQuestions.TypePoint
                ? (object)new { rule = new { type = "choice", choice = rule, confidence, probabilities } }
                : new { fits = new { type = "noul", noul = fits[Math.Min(next++, fits.Length - 1)] } }));
            return Task.FromResult(DecisionOutcome.Answered(DecisionAnswerReader.Read(request, json.RootElement, "Contoso fake")));
        });
    }

    private static Task<MeasureMark> Score(JevTotalsScorer scorer, params TotalProbe[] probes)
        // 61, not the rows' 60: a sum whose rows add up passes on the arithmetic alone (fx30e F5); these tests read the cuts.
        => scorer.ScoreAsync(Input, probes.Length == 0 ? new[] { Probe(61) } : probes, new(), false, CancellationToken.None);

    // The cuts measured in l2/t2 (warn, pass) per type; see totals-report.md.
    public static IEnumerable<object[]> MeasuredCuts() => new[] {
        new object[] { "sum_of_rows", .25, .5 }, new object[] { "recomputed_at_total", .3, .7 },
        new object[] { "last_value_over_time", .5, .5 }, new object[] { "first_value_over_time", .5, .5 },
        new object[] { "average", .65, .65 }, new object[] { "blank_at_total", .5, .5 } };

    [Theory, MemberData(nameof(MeasuredCuts))]
    public async Task Totals_reads_verified_only_at_the_measured_pass_cut(string rule, double warn, double pass)
    {
        Assert.Equal("passed", (await Score(Fake(rule, .9, pass))).State);
        Assert.Equal("suspect", (await Score(Fake(rule, .9, warn - .001))).State);
        var between = await Score(Fake(rule, .9, pass - .001));
        Assert.Equal(pass > warn ? "unknown" : "suspect", between.State);
    }

    [Fact]
    public async Task Totals_between_the_cuts_is_unknown_with_a_reason_code()
    {
        var mark = await Score(Fake("sum_of_rows", .9, .4));
        Assert.Equal("unknown", mark.State);
        Assert.Equal("totals_unclear", mark.ReasonCode);
        Assert.Equal("The total is not clearly right for a sum.", mark.Sentence);
        Assert.Equal("sum", mark.Total.Type);
        Assert.Equal(.4, mark.Total.FitScore);
        Assert.Null(mark.Action);
        Assert.Empty(mark.ActionCodes);
    }

    [Fact]
    public async Task Totals_pass_needs_every_probe_at_the_pass_cut()
    {
        var mark = await Score(Fake("recomputed_at_total", .95, .9, .6), Probe(55), Probe(50));
        Assert.Equal("unknown", mark.State);
        Assert.Equal("totals_unclear", mark.ReasonCode);
    }

    [Fact]
    public async Task Totals_suspect_and_distinct_carry_codes()
    {
        var suspect = await Score(Fake("sum_of_rows", .9, .1));
        Assert.Equal("suspect", suspect.State);
        Assert.Equal(new[] { "expected" }, suspect.ActionCodes);
        var distinct = await Score(Fake("distinct_count", .99, .99));
        Assert.Equal("info", distinct.State);
        Assert.Equal("totals_distinct", distinct.ReasonCode);
        var passed = await Score(Fake("sum_of_rows", .9, .9));
        Assert.Equal("passed", passed.State);
        Assert.Null(passed.ReasonCode);
    }

    [Fact]
    public async Task A_sum_read_with_low_type_confidence_never_reads_verified()
    {
        // t2 found recalc measures read as a sum at confidence .48 to .6 whose summed (broken) total fit .78 to .83.
        Assert.NotEqual("passed", (await Score(Fake("sum_of_rows", .6, .95))).State);
        Assert.NotEqual("passed", (await Score(Fake("recomputed_at_total", .7, .95))).State);
    }

    [Fact]
    public void Default_settings_are_the_held_out_measured_cuts()
    {
        var s = new TotalsSettings();
        Assert.Equal((.25, .5, .8), (s.SumFitBelow, s.SumPassAtLeast, s.SumTypeAtLeast));
        Assert.Equal((.3, .7, .8), (s.RecalcFitBelow, s.RecalcPassAtLeast, s.RecalcTypeAtLeast));
        Assert.Equal((.5, .5), (s.SemiAdditiveFitBelow, s.SemiAdditivePassAtLeast));
        Assert.Equal((.65, .65), (s.AverageFitBelow, s.AveragePassAtLeast));
        Assert.Equal((.5, .5), (s.BlankFitBelow, s.BlankPassAtLeast));
        Assert.Throws<ArgumentException>(() => new TotalsSettings { SumPassAtLeast = 1.2 }.Validate());
    }

    [Fact]
    public async Task A_pass_cut_below_the_warn_cut_never_widens_the_pass()
    {
        var scorer = Fake("sum_of_rows", .9, .4);
        var mark = await scorer.ScoreAsync(Input, new[] { Probe(61) }, new() { SumFitBelow = .45, SumPassAtLeast = .1 },
            false, CancellationToken.None);
        Assert.Equal("suspect", mark.State);
    }

    // ---------------------------------------------------------------- Filters
    private static readonly ContextColumn Category = new("Product", "Category");
    private static ProbeResult Repeated() => new() { Status = "ok", Scenarios = new[] { new ScenarioEvidence {
        Name = "by " + Category.Dax, Status = "ok", Rows = new[] {
            new ProbeRow { V = 300, Members = new() { [Category.Dax] = "Bikes" } },
            new ProbeRow { V = 300, Members = new() { [Category.Dax] = "Clothing" } } } } } };
    private static MeasureMark Judge(bool onPurpose = false) => MeasureMarkJudge.FilterIntent(
        new() { Name = "Filters", State = "passed" }, Repeated(), new[] { new ContextFilterClear(Category, "ALL") }, onPurpose);

    [Fact]
    public void Filters_never_passes_from_Description_words()
    {
        // The rule that read Descriptions is gone: the judge has no Description input at all. Its one text input is the
        // measure's format string (L3, joined in rc28), which only shapes how the number in the sentence reads.
        var method = typeof(MeasureMarkJudge).GetMethod("FilterIntent", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.Equal(new[] { "format" }, method.GetParameters().Where(p => p.ParameterType == typeof(string)).Select(p => p.Name));
        Assert.Null(typeof(MeasureMarkJudge).GetMethod("DescriptionStatesClear", BindingFlags.Static | BindingFlags.NonPublic));
        Assert.Null(typeof(MeasureMarkJudge).GetField("ClearDescriptionRule", BindingFlags.Static | BindingFlags.NonPublic));
        var mark = Judge();
        Assert.Equal("info", mark.State);
        Assert.Equal("filters_lost", mark.ReasonCode);
        Assert.StartsWith("This formula ignores Product Category filters", mark.Sentence);
    }

    [Fact]
    public void A_lost_filter_offers_fix_the_formula_and_on_purpose()
        => Assert.Equal(new[] { "fix_formula", "on_purpose" }, Judge().ActionCodes);

    [Fact]
    public void On_purpose_is_the_persons_word_and_reads_overridden()
    {
        var mark = Judge(onPurpose: true);
        Assert.Equal("info", mark.State);
        Assert.Equal("filters_on_purpose", mark.ReasonCode);
        Assert.Equal(new[] { "not_on_purpose" }, mark.ActionCodes);
        Assert.Equal("Ignores Product Category filters on purpose. You marked this.", mark.Sentence);
        // A cached lost-filter mark turns Overridden at once; a passed or suspect mark is not touched.
        var cached = Judge();
        Assert.Equal("filters_on_purpose", MeasureMarkJudge.ApplyFiltersOnPurpose(cached, true).ReasonCode);
        var passed = new MeasureMark { Name = "Filters", State = "passed", Sentence = "No ignored filter in 2 places checked." };
        Assert.Same(passed, MeasureMarkJudge.ApplyFiltersOnPurpose(passed, true));
        Assert.Equal("passed", passed.State);
        var lost = Judge();
        Assert.Same(lost, MeasureMarkJudge.ApplyFiltersOnPurpose(lost, false));
        Assert.Equal("filters_lost", lost.ReasonCode);
    }

    [Fact]
    public async Task Both_doors_store_on_purpose_as_an_annotation_with_undo()
    {
        using var home = new DecisionFixture();
        using var f = new MeasureMarksDoorTests.Fixture(home.Workspace);
        await f.Open();
        const string reference = "measure:Sales/Total Sales";
        string Annotation(TabularEditor.TOMWrapper.Model m) => m.Tables["Sales"].Measures["Total Sales"].GetAnnotation("Semanticus.Filters.OnPurpose");
        Assert.True(await McpTools.SetMeasureFiltersOnPurpose(f.Engine, reference, true));
        Assert.Equal("true", await f.Sessions.Current.ReadAsync(Annotation));
        Assert.Contains(reference, (await f.Sessions.Current.ReadAsync(MeasureMarkSnapshot.Capture)).FiltersOnPurpose);
        Assert.False(await new EngineRpcTarget(f.Engine).setMeasureFiltersOnPurpose(reference, false));
        Assert.Null(await f.Sessions.Current.ReadAsync(Annotation));
        await f.Engine.UndoAsync("human");
        Assert.Equal("true", await f.Sessions.Current.ReadAsync(Annotation));
        await Assert.ThrowsAsync<ArgumentException>(() => f.Engine.SetMeasureFiltersOnPurposeAsync(" ", true));
    }
}
