using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;
using Xunit.Abstractions;

namespace Semanticus.Tests;

public sealed class MeasureMarksTests
{
    private readonly ITestOutputHelper output;
    public MeasureMarksTests(ITestOutputHelper output) => this.output = output;

    private static ProbeResult Contoso(bool ignored = false, bool error = false, bool blank = false, bool allBlank = false)
    {
        ProbeRow R(object? value, string? member = null) => new()
        {
            V = allBlank ? null : value, Blank = allBlank || value == null,
            Members = member == null ? new() : new() { ["'Product'[Category]"] = member }
        };
        ScenarioEvidence S(string name, params ProbeRow[] rows) => new() { Name = name, Status = "ok", Rows = rows };
        return new ProbeResult
        {
            Status = "ok", Scenarios = new[]
            {
                S("grand total", R(300m)),
                S("by 'Product'[Category] (unfiltered)", R(ignored ? 300m : 100m, "Bikes"), R(ignored ? 300m : 200m, "Accessories")),
                S("single-select 'Product'[Category]=Bikes", R(ignored ? 300m : 100m, "Bikes")),
                S("multi-select 'Product'[Category]", R(ignored ? 300m : 100m, "Bikes"), R(ignored ? 300m : 200m, "Accessories")),
                S("empty-select 'Product'[Category]"),
                error ? new ScenarioEvidence { Name = "boundary 'Product'[Category]=Accessories", Status = "error", Error = "Contoso division error" }
                    : S("boundary 'Product'[Category]=Accessories", R(blank ? null : ignored ? 300m : 200m, "Accessories"))
            }
        };
    }

    [Fact]
    public void Filters_pass_when_the_related_slicer_changes_the_number()
    {
        var mark = MeasureMarkJudge.Filters(Contoso(), Contoso(), null);
        Assert.Equal("passed", mark.State);
        Assert.NotEmpty(mark.Cells);
        Assert.NotNull(mark.LastRan);
    }

    [Fact]
    public void Filters_find_ignored_slicer_and_repeated_axis_values_with_fact_evidence()
    {
        var mark = MeasureMarkJudge.Filters(Contoso(ignored: true), Contoso(), null, filterRows: Contoso(ignored: true));
        Assert.Equal("suspect", mark.State);
        Assert.Contains("filter", mark.Sentence, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(mark.Cells, c => c.After == "300");
        Assert.InRange(mark.Cells.Length, 2, 6);
    }

    [Fact]
    public void Equal_fact_members_do_not_accuse_a_legitimate_constant()
    {
        var mark = MeasureMarkJudge.Filters(Contoso(ignored: true), Contoso(ignored: true), null);
        Assert.Equal("passed", mark.State);
    }

    [Fact]
    public void A_failed_fact_witness_cannot_clear_a_constant_measure()
    {
        var facts = Contoso();
        foreach (var scenario in facts.Scenarios)
        {
            scenario.Status = "error"; scenario.Error = "Contoso facts unavailable";
            scenario.Rows = Array.Empty<ProbeRow>();
        }
        Assert.Equal("not run", MeasureMarkJudge.Filters(Contoso(ignored: true), facts, null).State);
    }

    [Fact]
    public void Orphan_keys_from_run_tests_count_under_Filters()
    {
        var rel = new RelationshipIntegrityReport
        {
            Relationships = new[] { new RelationshipResult
            {
                Name = "Contoso Sales Product", ReferentialIntegrity = new CheckResult
                { Verdict = Verdict.Fail, Count = 4, Message = "4 sales rows have no product key." }
            } }
        };
        var mark = MeasureMarkJudge.Filters(Contoso(), Contoso(), rel);
        Assert.Equal("suspect", mark.State);
        Assert.Contains(mark.Cells, c => c.After == "4");
    }

    [Fact]
    public void Contexts_pass_for_sound_contexts_and_do_not_judge_additivity()
    {
        var probe = Contoso();
        probe.NonAdditiveScenarios = new[] { 1 };
        Assert.Equal("passed", MeasureMarkJudge.Contexts(probe).State);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Contexts_find_an_error_or_a_blank_in_only_some_contexts(bool error, bool blank)
    {
        var mark = MeasureMarkJudge.Contexts(Contoso(error: error, blank: blank), Contoso());
        Assert.Equal("suspect", mark.State);
        Assert.Contains(mark.Cells, c => c.Context.Contains("Accessories"));
    }

    [Fact]
    public void A_blank_shared_by_every_context_is_never_suspect()
        => Assert.Equal("passed", MeasureMarkJudge.Contexts(Contoso(allBlank: true)).State);

    [Theory]
    [InlineData("SQL", "Set up a SQL check")]
    [InlineData("Trusted number", "Add a number you trust")]
    public void Missing_saved_checks_say_not_run_and_offer_the_setup_action(string name, string action)
    {
        var mark = MeasureMarkJudge.Saved(name, Array.Empty<ReconcileOutcome>());
        Assert.Equal("not run", mark.State);
        Assert.Equal(action, mark.Action);
        Assert.Null(mark.LastRan);
    }

    [Theory]
    [InlineData("SQL")]
    [InlineData("Trusted number")]
    public void Saved_checks_keep_the_trusted_and_actual_numbers(string name)
    {
        var outcome = new ReconcileOutcome { Verdict = Verdict.Pass, Expected = "300", Actual = "300", Filter = "Grand total" };
        Assert.Equal("passed", MeasureMarkJudge.Saved(name, new[] { outcome }).State);
        outcome.Verdict = Verdict.Fail;
        outcome.Actual = "350";
        var mark = MeasureMarkJudge.Saved(name, new[] { outcome });
        Assert.Equal("suspect", mark.State);
        Assert.Equal("300", Assert.Single(mark.Cells).Before);
        Assert.Equal("350", mark.Cells[0].After);
    }

    [Fact]
    public void A_check_that_cannot_run_never_passes()
    {
        var mark = MeasureMarkJudge.Saved("SQL", new[] { new ReconcileOutcome { Verdict = Verdict.NotVerifiable, Message = "Contoso source is offline." } });
        Assert.Equal("not run", mark.State);
        Assert.Contains("offline", mark.Sentence);
    }

    [Fact]
    public void Equivalence_says_moved_with_before_and_after_numbers_and_never_wrong()
    {
        var before = new BaselineEntryState { Ref = "measure:Sales/Total Sales", Rows = new() { ["Grand total"] = 300m } };
        var diff = Baseline.Diff(before, new() { ["Grand total"] = 350m }, false);
        var mark = MeasureMarkJudge.Equivalence(diff);
        Assert.Equal("suspect", mark.State);
        Assert.Contains("moved", mark.Sentence);
        Assert.DoesNotContain("wrong", mark.Sentence, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("300", Assert.Single(mark.Cells).Before);
        Assert.Equal("350", mark.Cells[0].After);
        Assert.Equal("passed", MeasureMarkJudge.Equivalence(Baseline.Diff(before, before.Rows, false)).State);
    }

    [Fact]
    public async Task Offline_door_returns_all_marks_and_keeps_unmeasured_judges_not_run()
    {
        using var sessions = new SessionManager();
        using var engine = new LocalEngine(sessions);
        await engine.CreateModelAsync("Contoso", 1604);
        await sessions.Current.MutateAsync("human", "Contoso fixture", m => m.AddTable("Sales").AddMeasure("Total Sales", "300"));
        var result = await Finish(engine, "measure:Sales/Total Sales");
        Assert.Equal(8, result.Marks.Length);
        Assert.All(result.Marks, m => Assert.Equal("not run", m.State));
        var totals = result.Marks.Single(m => m.Name == "Totals");
        Assert.StartsWith("Not scored yet. ", totals.Sentence);
        Assert.DoesNotContain("judge", totals.Sentence + totals.Action, StringComparison.OrdinalIgnoreCase);
        Assert.Null(totals.Total);
        // fx29c: a measure with no Description is not a service fault; the line says there is none to score.
        Assert.Equal("Not scored yet. There is no Description to score.", result.Marks.Single(m => m.Name == "Alignment").Sentence);
    }

    internal static async Task<MeasureMarksResult> Finish(IEngine engine, string measureRef, string? axis = null)
    {
        var clock = Stopwatch.StartNew();
        while (true)
        {
            var result = await engine.GetMeasureMarksAsync(measureRef, axis);
            if (!result.Running) return result;
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(15), "The marks did not finish.");
            await Task.Delay(10);
        }
    }
}
