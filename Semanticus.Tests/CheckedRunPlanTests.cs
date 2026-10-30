using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests;

public sealed class CheckedRunPlanTests
{
    internal static MeasureRunCandidate[] Contoso500() => Enumerable.Range(0, 500).Select(i => new MeasureRunCandidate
    {
        MeasureRef = "measure:Contoso Sales/Measure " + i.ToString("D3"), Table = "Contoso Sales",
        Folder = i < 50 ? "Core\\Sales" : "Other", Formula = "SUM('Contoso Sales'[Amount])", Inputs = "input-" + i,
        Changed = i < 6, BaseMeasure = i is >= 6 and < 16, Dependents = i is >= 6 and < 16 ? 500 - i : 0,
        Risk = i is >= 16 and < 50 ? "iterator" : null, LastSeconds = 3.75
    }).ToArray();

    [Fact]
    public void Rule_order_on_500_prioritises_changed_core_reports_risk_match_then_age()
    {
        var c = Contoso500();
        c[16] = c[16] with { Risk = null, ReportUses = 12 };
        c[17] = c[17] with { Risk = null, ReportUses = 2 };
        c[50] = c[50] with { MatchScore = .2 };
        var picks = new RuleMeasureRunOrder().Order(c.AsEnumerable().Reverse().ToArray(), 40);
        Assert.Equal(40, picks.Count);
        Assert.Equal(c.Take(40).Select(x => x.MeasureRef), picks.Select(x => x.MeasureRef));
        Assert.All(picks, p => Assert.False(string.IsNullOrWhiteSpace(p.Reason)));
        var tail = new RuleMeasureRunOrder().Order(new[] { c[60] with { LastChecked = DateTimeOffset.UtcNow }, c[61], c[62] with { Suspect = true }, c[63] with { MatchScore = .1 } }, 25);
        Assert.Equal(new[] { c[63].MeasureRef, c[62].MeasureRef, c[61].MeasureRef, c[60].MeasureRef }, tail.Select(p => p.MeasureRef));
    }

    [Theory]
    [InlineData(1, 25)] [InlineData(30, 30)] [InlineData(100, 40)]
    public void Automatic_cap_is_adjustable_from_25_to_40(int requested, int expected)
        => Assert.Equal(expected, MeasureRunPlanner.Build(Contoso500(), new() { Cap = requested }).Picks.Length);

    [Fact]
    public void Changed_helpers_are_always_in_and_unchanged_helpers_are_skipped()
    {
        var c = Contoso500(); c[0] = c[0] with { Helper = true }; c[6] = c[6] with { Helper = true };
        var plan = MeasureRunPlanner.Build(c);
        Assert.Contains(plan.Picks, p => p.MeasureRef == c[0].MeasureRef);
        Assert.DoesNotContain(plan.Picks, p => p.MeasureRef == c[6].MeasureRef);
        Assert.Equal(30, plan.Picks.Length);
    }

    [Fact]
    public void More_changed_than_the_cap_stays_in_capped_batches()
    {
        var c = Contoso500().Select(x => x with { Changed = true }).ToArray();
        var plan = MeasureRunPlanner.Build(c);
        Assert.Equal(500, plan.Picks.Length);
        Assert.Equal(30, plan.BatchSize);
        Assert.All(plan.Picks.Chunk(plan.BatchSize), batch => Assert.InRange(batch.Length, 1, 30));
    }

    [Fact]
    public void Plan_sentence_uses_measured_times_and_default_times()
    {
        var plan = MeasureRunPlanner.Build(Contoso500(), new() { Cap = 32 });
        Assert.Equal("Checking 32 of 500: 6 changed, 10 core, 16 risky. About 2 minutes.", plan.Sentence);
        Assert.Equal(120, plan.EstimatedSeconds);
        Assert.Equal(420, MeasureRunPlanner.Build(Contoso500().Select(c => c with { LastSeconds = null }).ToArray()).EstimatedSeconds);
    }

    [Theory]
    [InlineData("folder", 50)] [InlineData("table", 500)] [InlineData("changed", 6)] [InlineData("measures", 2)]
    public void Explicit_selection_uses_the_requested_scope(string kind, int expected)
    {
        var c = Contoso500();
        var plan = MeasureRunPlanner.Build(c, new() { Selection = new() { Kind = kind, Table = "Contoso Sales", Folder = "Core", Measures = new[] { c[1].MeasureRef, c[2].MeasureRef } } });
        Assert.Equal(expected, plan.Picks.Length);
    }

    [Fact]
    public void Both_doors_expose_plan_run_sweep_and_cache()
    {
        Assert.NotNull(typeof(IEngine).GetMethod("PlanMeasureRunAsync"));
        Assert.NotNull(typeof(EngineRpcTarget).GetMethod("planMeasureRun"));
        Assert.NotNull(typeof(McpTools).GetMethod("PlanMeasureRun"));
        Assert.NotNull(typeof(IEngine).GetMethod("RunMeasurePlanAsync"));
        Assert.NotNull(typeof(IEngine).GetMethod("SweepMeasureChecksAsync"));
        Assert.NotNull(typeof(IEngine).GetMethod("GetMeasureCheckCacheAsync"));
    }

    [Fact]
    public void Fx14_first_plan_shows_a_rough_time_and_measured_runs_replace_it()
    {
        var first = MeasureRunPlanner.Build(Contoso500().Take(12).Select(c => c with { LastSeconds = null }).ToArray());
        Assert.True(first.FirstRun);
        Assert.Matches("About [1-9][0-9]* (seconds|minutes)", first.Sentence);
        Assert.InRange(first.EstimatedSeconds, 120, 360);
        var measured = MeasureRunPlanner.Build(Contoso500().Take(12).ToArray());
        Assert.False(measured.FirstRun);
        Assert.Equal(45, measured.EstimatedSeconds);
        var light = Contoso500().Take(12).Select(c => c with { LastSeconds = null, ContextChecks = 6 }).ToArray();
        var heavy = light.Select(c => c with { ContextChecks = 36 }).ToArray();
        Assert.Equal(60, MeasureRunPlanner.Build(light).EstimatedSeconds);
        Assert.Equal(240, MeasureRunPlanner.Build(heavy).EstimatedSeconds);
        Assert.Equal(45, MeasureRunPlanner.Build(heavy.Select(c => c with { LastSeconds = 3.75 }).ToArray()).EstimatedSeconds);
    }
}
