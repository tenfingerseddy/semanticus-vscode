using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Decisions;
using Semanticus.Engine;
using Semanticus.Engine.Decisions;
using Semanticus.Engine.MeasureContexts;
using Xunit;

namespace Semanticus.Tests;

[Collection("restore-root")]
public sealed class CheckedTotalsTests
{
    private static readonly string[] Rules = { "sum_of_rows", "recomputed_at_total", "last_value_over_time",
        "first_value_over_time", "distinct_count", "average", "blank_at_total" };
    private static readonly TotalsInput Input = new("Total Quantity", "Adds the Contoso quantities.", "SUM(Sales[Quantity])");
    private static readonly TotalProbe[] Probes = { new("Product[Category]", new[] {
        new TotalProbeRow("Bikes", 10), new TotalProbeRow("Accessories", 20), new TotalProbeRow("Clothing", 30) }, 100) };

    private static JevTotalsScorer Fake(string rule, double confidence, double fit,
        Action<string, IReadOnlyDictionary<string, object>, IReadOnlyList<DecisionQuestion>> inspect = null)
        => new((point, state, questions, ct) => {
            ct.ThrowIfCancellationRequested();
            inspect?.Invoke(point, state, questions);
            var request = new DecisionRequest(point, 1, state, questions);
            var probabilities = Rules.ToDictionary(r => r, r => r == rule ? confidence : (1 - confidence) / 6);
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(point.EndsWith("type", StringComparison.Ordinal)
                ? (object)new { rule = new { type = "choice", choice = rule, confidence, probabilities } }
                : new { fits = new { type = "noul", noul = fit } }));
            return Task.FromResult(DecisionOutcome.Answered(DecisionAnswerReader.Read(request, json.RootElement, "Contoso fake")));
        });

    public static IEnumerable<object[]> Cuts()
    {
        // L2: warn below the warn cut, Verified only at the pass cut (both measured on a held-out set).
        foreach (var (rule, warn, pass) in new[] { ("sum_of_rows", .25, .5), ("recomputed_at_total", .3, .7),
            ("last_value_over_time", .5, .5), ("first_value_over_time", .5, .5), ("average", .65, .65), ("blank_at_total", .5, .5) })
        {
            yield return new object[] { rule, warn - .001, "suspect" };
            yield return new object[] { rule, pass, "passed" };
            yield return new object[] { rule, pass + .001, "passed" };
        }
    }

    [Theory, MemberData(nameof(Cuts))]
    public async Task Each_measured_cut_is_strict_and_keeps_the_type(string rule, double fit, string state)
    {
        var mark = await Fake(rule, .9, fit).ScoreAsync(Input, Probes, new(), false, CancellationToken.None);
        Assert.Equal(state, mark.State);
        Assert.Equal(rule, mark.Total.Rule);
        Assert.Equal(.9, mark.Total.Confidence);
        Assert.Contains("Jev reading the name, the Description and the formula", mark.Total.Source);
        Assert.InRange(mark.Cells.Length, 1, 6);
        Assert.NotEmpty(mark.Sentence);
        if (state == "suspect") {
            Assert.StartsWith("The total does not fit a ", mark.Sentence);
            Assert.Contains("rows 10, 20, 30; total 100", mark.Sentence);
            Assert.Equal("This is expected", mark.Action);
        }
    }

    [Theory]
    [InlineData("sum_of_rows", .799, "info")]
    [InlineData("sum_of_rows", .8, "suspect")]
    [InlineData("sum_of_rows", .801, "suspect")]
    [InlineData("recomputed_at_total", .799, "info")]
    [InlineData("recomputed_at_total", .8, "suspect")]
    [InlineData("recomputed_at_total", .801, "suspect")]
    public async Task Sum_and_recalc_need_measured_type_confidence(string rule, double confidence, string state)
        => Assert.Equal(state, (await Fake(rule, confidence, .01).ScoreAsync(Input, Probes, new(), false, CancellationToken.None)).State);

    [Theory]
    [InlineData(0)] [InlineData(.199)] [InlineData(.2)] [InlineData(.299)] [InlineData(.3)]
    [InlineData(.499)] [InlineData(.5)] [InlineData(.501)] [InlineData(1)]
    public async Task Distinct_is_always_information(double fit)
    {
        var mark = await Fake("distinct_count", .99, fit).ScoreAsync(Input, Probes, new(), false, CancellationToken.None);
        Assert.Equal("info", mark.State);
        Assert.Equal("Checked as a distinct count. Totals for this type are shown, not judged.", mark.Sentence);
        Assert.Null(mark.Action);
    }

    [Theory]
    [InlineData(.499, "info")] [InlineData(.5, "suspect")] [InlineData(.501, "suspect")]
    public async Task Low_type_confidence_is_unclear(double confidence, string state)
    {
        var mark = await Fake("average", confidence, .01).ScoreAsync(Input, Probes, new(), false, CancellationToken.None);
        Assert.Equal(state, mark.State);
        if (confidence < .5) Assert.Equal("Type unclear.", mark.Sentence);
    }

    [Fact]
    public async Task Waive_stops_the_warning_and_settings_change_the_cut()
    {
        var scorer = Fake("sum_of_rows", .9, .2);
        Assert.Equal("suspect", (await scorer.ScoreAsync(Input, Probes, new(), false, CancellationToken.None)).State);
        Assert.Equal("info", (await scorer.ScoreAsync(Input, Probes, new(), true, CancellationToken.None)).State);
        Assert.Equal("passed", (await scorer.ScoreAsync(Input, Probes, new() { SumFitBelow = .15, SumPassAtLeast = .15 }, false, CancellationToken.None)).State);
        Assert.Equal("info", (await scorer.ScoreAsync(Input, Probes, new() { SumTypeAtLeast = .95 }, false, CancellationToken.None)).State);
    }

    [Fact]
    public async Task Fit_chains_the_type_and_supplies_the_measured_code_facts_and_wire_wording()
    {
        var calls = 0;
        var scorer = Fake("recomputed_at_total", .9, .7, (point, state, questions) => {
            calls++;
            Assert.Equal(Input.Name, state["name"]);
            Assert.Equal(Input.Description, state["description"]);
            Assert.Equal(Input.Formula, state["formula"]);
            var request = new DecisionRequest(point, 1, state, questions);
            using var wire = JsonDocument.Parse(JevDecisionOracle.BuildRelayBody(request, JsonSerializer.Serialize(state), "Contoso"));
            if (point == TotalsQuestions.TypePoint) {
                Assert.Equal(new[] { "description", "formula", "name" }, state.Keys.OrderBy(x => x));
                Assert.Equal("When `name` is shown in a table with a total row, which rule should the total follow, given `description` and `formula`?", questions.Single().Instructions);
                Assert.Equal(7, questions.Single().Options.Count);
            } else {
                Assert.Equal(TotalsQuestions.Rules.Single(r => r.Id == "recomputed_at_total").Criterion, state["expected_total_rule"]);
                var probe = wire.RootElement.GetProperty("snapshot").GetProperty("probe");
                Assert.Equal("Product[Category]", probe.GetProperty("grouped_by").GetString());
                Assert.Equal(3, probe.GetProperty("rows").GetArrayLength());
                var facts = probe.GetProperty("facts");
                Assert.Equal(60, facts.GetProperty("sum_of_rows").GetDouble());
                Assert.Equal(20, facts.GetProperty("mean_of_rows").GetDouble());
                Assert.False(facts.GetProperty("total_equals_mean_of_rows").GetBoolean());
                Assert.False(facts.GetProperty("total_equals_sum_of_rows").GetBoolean());
                var question = wire.RootElement.GetProperty("questions").GetProperty("fits");
                Assert.Equal("Does `probe.total` follow `expected_total_rule`, given the rows and facts in `probe`? A recalculated total is not the plain mean of the row values.", question.GetProperty("instructions").GetString());
                Assert.Equal("The total follows the expected rule", question.GetProperty("criteria").GetProperty("true").GetString());
                Assert.False(question.GetProperty("criteria").TryGetProperty("yes", out _));
            }
        });
        var mark = await scorer.ScoreAsync(Input, Probes, new(), false, CancellationToken.None);
        Assert.Equal("passed", mark.State);
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData("last_value_over_time")] [InlineData("first_value_over_time")]
    [InlineData("average")] [InlineData("blank_at_total")]
    public async Task Each_half_cut_and_the_unclear_cut_can_be_adjusted(string rule)
    {
        var settings = new TotalsSettings { SemiAdditiveFitBelow = .2, AverageFitBelow = .2, BlankFitBelow = .2,
            SemiAdditivePassAtLeast = .2, AveragePassAtLeast = .2, BlankPassAtLeast = .2 };
        Assert.Equal("passed", (await Fake(rule, .6, .3).ScoreAsync(Input, Probes, settings, false, CancellationToken.None)).State);
        var unclear = await Fake(rule, .6, .01).ScoreAsync(Input, Probes, settings with { TypeUnclearBelow = .7 }, false, CancellationToken.None);
        Assert.Equal("Type unclear.", unclear.Sentence);
        Assert.Equal("info", unclear.State);
        Assert.Equal(rule, unclear.Total.Rule);
    }

    [Fact]
    public async Task Recalc_cuts_can_be_adjusted_and_missing_fit_keeps_the_type()
    {
        var scorer = Fake("recomputed_at_total", .85, .15);
        Assert.Equal("passed", (await scorer.ScoreAsync(Input, Probes, new() { RecalcFitBelow = .1, RecalcPassAtLeast = .1 }, false, CancellationToken.None)).State);
        Assert.Equal("info", (await scorer.ScoreAsync(Input, Probes, new() { RecalcTypeAtLeast = .9 }, false, CancellationToken.None)).State);
        var missing = new JevTotalsScorer((point, state, questions, ct) => point == TotalsQuestions.TypePoint
            ? FakeOutcome(point, state, questions) : Task.FromResult(DecisionOutcome.Skipped("Contoso unavailable")));
        var mark = await missing.ScoreAsync(Input, Probes, new(), false, CancellationToken.None);
        Assert.Equal("info", mark.State);
        Assert.Equal("sum", mark.Total.Type);
        Assert.Null(mark.Total.FitScore);
        Assert.StartsWith("Not scored yet. ", mark.Sentence);
        Assert.DoesNotContain("judge", mark.Sentence, StringComparison.OrdinalIgnoreCase);
    }

    private static Task<DecisionOutcome> FakeOutcome(string point, IReadOnlyDictionary<string, object> state, IReadOnlyList<DecisionQuestion> questions)
    {
        var request = new DecisionRequest(point, 1, state, questions);
        using var json = JsonDocument.Parse("{\"rule\":{\"type\":\"choice\",\"choice\":\"sum_of_rows\",\"confidence\":0.9}}");
        return Task.FromResult(DecisionOutcome.Answered(DecisionAnswerReader.Read(request, json.RootElement, "Contoso fake")));
    }

    [Fact]
    public void Probe_facts_keep_the_measured_mean_tolerance_and_blank_first_and_last_rows()
    {
        var probe = new TotalProbe("Product[Category]", new[] {
            new TotalProbeRow("Bikes", .1), new TotalProbeRow("Clothing", .2) }, .1502);
        var facts = TotalsQuestions.Facts(probe);
        Assert.Equal(.3, facts["sum_of_rows"]);
        Assert.Equal(.15, facts["mean_of_rows"]);
        Assert.Equal(true, facts["total_equals_mean_of_rows"]);
        Assert.Equal(false, facts["total_equals_sum_of_rows"]);
        facts = TotalsQuestions.Facts(new("Date[Month]", new[] {
            new TotalProbeRow("January", null), new TotalProbeRow("February", 3) }, 3));
        Assert.Equal(false, facts["total_equals_first_row"]);
        Assert.Equal(true, facts["total_equals_last_row"]);
        facts = TotalsQuestions.Facts(new("Date[Month]", Array.Empty<TotalProbeRow>(), null));
        Assert.Null(facts["sum_of_rows"]);
        Assert.Null(facts["mean_of_rows"]);
        Assert.Equal(true, facts["total_is_blank"]);
        Assert.Equal(false, facts["total_equals_mean_of_rows"]);
    }

    private sealed class RecordingScorer : ITotalsScorer
    {
        internal readonly List<TotalsInput> Inputs = new();
        internal TotalProbe[] Probes;
        public Task<MeasureMark> ScoreAsync(TotalsInput input, TotalProbe[] probes, TotalsSettings settings, bool expected, CancellationToken ct)
        {
            Inputs.Add(input); Probes = probes;
            return Fake("sum_of_rows", .9, .1).ScoreAsync(input, probes, settings, expected, ct);
        }
    }

    [Fact]
    public async Task Marks_door_shows_type_confidence_and_probe_cells_and_waive_is_persistent_and_undoable()
    {
        using var home = new DecisionFixture();
        using var f = new MeasureMarksDoorTests.Fixture(home.Workspace);
        await f.Open(2);
        const string reference = "measure:Sales/Total Sales";
        await f.Engine.SetDescriptionAsync(reference, "Adds Contoso sales.", "human");
        var scorer = new RecordingScorer(); f.Engine.TotalsScorer = scorer;
        var result = await MeasureMarksTests.Finish(f.Engine, reference);
        var mark = result.Marks.Single(m => m.Name == "Totals");
        Assert.Equal("suspect", mark.State);
        Assert.Equal("sum", mark.Total.Type);
        Assert.Equal(.9, mark.Total.Confidence);
        Assert.Equal("Adds Contoso sales.", scorer.Inputs.Last().Description);
        Assert.NotEmpty(scorer.Probes);
        Assert.Equal(300, scorer.Probes[0].Total);
        Assert.Equal(2, scorer.Probes[0].Rows.Length);
        Assert.Equal("Grand total", mark.Cells.Last().Context);
        Assert.NotEmpty(mark.ChosenContexts);
        await f.Engine.SetMeasureTotalsAsync(measureRef: reference, expected: true);
        Assert.Equal("true", await f.Sessions.Current.ReadAsync(m => m.Tables["Sales"].Measures["Total Sales"].GetAnnotation("Semanticus.Totals.Expected")));
        await f.Engine.GetMeasureMarksAsync(reference, refresh: true);
        mark = (await MeasureMarksTests.Finish(f.Engine, reference)).Marks.Single(m => m.Name == "Totals");
        Assert.Equal("info", mark.State);
        Assert.True(mark.Total.Expected);
        Assert.Null(mark.Action);
        var snapshot = await f.Sessions.Current.ReadAsync(MeasureMarkSnapshot.Capture);
        Assert.Contains(reference, snapshot.ExpectedTotals);
        Assert.DoesNotContain("measure:Sales/Sales Copy 1", snapshot.ExpectedTotals);
        await f.Engine.UndoAsync("human");
        Assert.Null(await f.Sessions.Current.ReadAsync(m => m.Tables["Sales"].Measures["Total Sales"].GetAnnotation("Semanticus.Totals.Expected")));
    }

    [Fact]
    public async Task Run_plan_buys_totals_only_for_the_selection_and_settings_invalidate_cached_inputs()
    {
        using var home = new DecisionFixture();
        using var f = new MeasureMarksDoorTests.Fixture(home.Workspace);
        await f.Open(3);
        var scorer = new RecordingScorer(); f.Engine.TotalsScorer = scorer;
        var request = new MeasureRunRequest { Selection = new() { Measures = new[] { "measure:Sales/Total Sales" } } };
        var plan = await f.Engine.PlanMeasureRunAsync(request);
        Assert.Empty(scorer.Inputs);
        var result = await f.Engine.RunMeasurePlanAsync(plan.Id);
        Assert.Single(scorer.Inputs);
        Assert.Equal("Total Sales", scorer.Inputs[0].Name);
        Assert.Equal("suspect", result.Results.Single(r => r.MeasureRef == "measure:Sales/Total Sales").Marks.Single(m => m.Name == "Totals").State);
        var before = plan.Picks.Single().Inputs;
        await f.Engine.SetMeasureTotalsAsync(measureRef: "measure:Sales/Total Sales", expected: true);
        var waivedCache = (await f.Engine.GetMeasureCheckCacheAsync()).Single(c => c.MeasureRef == "measure:Sales/Total Sales");
        Assert.Equal("info", waivedCache.Marks.Single(m => m.Name == "Totals").State);
        Assert.True(waivedCache.Marks.Single(m => m.Name == "Totals").Total.Expected);
        await f.Engine.SetMeasureTotalsAsync(measureRef: "measure:Sales/Total Sales", expected: false);
        await f.Engine.SetMeasureTotalsAsync(new() { SumFitBelow = .05, SumPassAtLeast = .05 });
        var after = await f.Engine.PlanMeasureRunAsync(request);
        Assert.NotEqual(before, after.Picks.Single().Inputs);
        result = await f.Engine.RunMeasurePlanAsync(after.Id);
        Assert.Equal("passed", result.Results.Single(r => r.MeasureRef == "measure:Sales/Total Sales").Marks.Single(m => m.Name == "Totals").State);
    }

    [Fact]
    public void Probe_builder_reuses_grand_total_and_one_subtotal_and_refuses_incomplete_groups()
    {
        var category = new ContextColumn("Product", "Category");
        var product = new ContextColumn("Product", "Product Name");
        var plan = new MeasureContextPlan(new[] {
            new ContextCandidate("total", null, ContextShape.GrandTotal, ContextRole.Total, "", "", 1, Array.Empty<string>(), Array.Empty<string>()),
            new ContextCandidate("subtotal", category, ContextShape.Subtotal, ContextRole.Total, "", "", 1, Array.Empty<string>(), Array.Empty<string>()) { DetailColumn = product }
        }, Array.Empty<string>());
        ProbeRow Row(double? value, params (string Column, string Member)[] members) => new() {
            V = value, Blank = value == null, Members = members.ToDictionary(m => m.Column, m => (object)m.Member)
        };
        var grouped = new ScenarioEvidence { Name = "by " + category.Dax + " (unfiltered)", Status = "ok",
            Rows = new[] { Row(30, (category.Dax, "Bikes")), Row(50, (category.Dax, "Clothing")) } };
        var probe = new ProbeResult { Status = "ok", Scenarios = new[] {
            new ScenarioEvidence { Name = "grand total", Status = "ok", Rows = new[] { Row(80) } }, grouped,
            new ScenarioEvidence { Name = "detail under " + category.Dax, Status = "ok", Rows = new[] {
                Row(10, (category.Dax, "Bikes"), (product.Dax, "Contoso Road")),
                Row(20, (category.Dax, "Bikes"), (product.Dax, "Contoso City")),
                Row(50, (category.Dax, "Clothing"), (product.Dax, "Contoso Jersey")) } }
        } };
        var totals = TotalProbeBuilder.From(plan, probe);
        Assert.Equal(2, totals.Length);
        Assert.Equal(80, totals[0].Total);
        Assert.Equal(30, totals[1].Total);
        Assert.Equal(new double?[] { 10, 20 }, totals[1].Rows.Select(r => r.Value));
        grouped.Truncated = true;
        Assert.Empty(TotalProbeBuilder.From(plan, probe));
    }

    private sealed class TotalsOracle : IDecisionOracle
    {
        public bool Available => true;
        internal readonly List<DecisionRequest> Requests = new();
        public Task<DecisionAnswers> AskAsync(DecisionRequest request, CancellationToken ct)
        {
            Requests.Add(request);
            using var json = JsonDocument.Parse(request.PointName == TotalsQuestions.TypePoint
                ? "{\"rule\":{\"type\":\"choice\",\"choice\":\"sum_of_rows\",\"confidence\":0.9}}"
                : "{\"fits\":{\"type\":\"noul\",\"noul\":0.1}}");
            return Task.FromResult(DecisionAnswerReader.Read(request, json.RootElement, "Contoso fake"));
        }
    }

    [Fact]
    public async Task Production_scorer_uses_registered_decision_service_and_both_doors_store_the_same_waive()
    {
        using var home = new DecisionFixture();
        using var f = new MeasureMarksDoorTests.Fixture(home.Workspace);
        await f.Open();
        var path = Path.Combine(home.Workspace, "Contoso.bim");
        await f.Engine.SaveAsync(path, "bim", false, "human", true);
        var oracle = new TotalsOracle();
        f.Engine.UseDecisionsForTest(new DecisionService(oracle));
        await f.Engine.SetConsentAsync(DecisionRegistry.DeclaredFields.ToArray(), DecisionRegistry.ConsentSchemaVersion, "human");
        const string reference = "measure:Sales/Total Sales";
        var marks = await MeasureMarksTests.Finish(f.Engine, reference);
        Assert.Equal("suspect", marks.Marks.Single(m => m.Name == "Totals").State);
        Assert.Equal(new[] { TotalsQuestions.TypePoint, TotalsQuestions.FitPoint }, oracle.Requests.Select(r => r.PointName));
        await McpTools.SetMeasureTotals(f.Engine, measureRef: reference, expected: true);
        await f.Engine.SaveAsync(path, "bim", true, "human");
        using var sessions = new SessionManager();
        using var reopened = new LocalEngine(sessions, TestEntitlements.Pro, home.Workspace);
        await reopened.OpenAsync(path);
        var snapshot = await sessions.Current.ReadAsync(MeasureMarkSnapshot.Capture);
        Assert.Contains(reference, snapshot.ExpectedTotals);
        var rpc = new EngineRpcTarget(reopened);
        var settings = await rpc.setMeasureTotals(new() { RecalcFitBelow = .15 }, reference, false);
        Assert.Equal(.15, settings.RecalcFitBelow);
        snapshot = await sessions.Current.ReadAsync(MeasureMarkSnapshot.Capture);
        Assert.DoesNotContain(reference, snapshot.ExpectedTotals);
        Assert.Equal(.15, snapshot.TotalsSettings.RecalcFitBelow);
    }
}
