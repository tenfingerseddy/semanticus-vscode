using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.Decisions;
using Semanticus.Engine.Jev;
using Xunit;

namespace Semanticus.Tests;

[Collection("restore-root")]
public sealed class CheckedFx11Tests
{
    private const string Ref = "measure:Sales/Total Sales";
    private const string Calm = "Not scored yet. This score turns on when Jev scoring is released.";

    // A cause the person can act on keeps its own plain line; only service-side causes read the calm release line.
    [Theory]
    [InlineData("suggestions like this one are part of Semanticus Pro", "Not scored yet. Scoring needs a Pro plan.")]
    [InlineData("this suggestion is switched off for this model", "Not scored yet. Scoring is off for this model.")]
    [InlineData(DecisionService.BudgetSpentReason, "Not scored yet. The scoring allowance is used up for now.")]
    [InlineData("No suggestion service is configured.", Calm)]
    public void A_cause_the_person_can_act_on_keeps_its_own_line(string reason, string expected)
    {
        var line = ScoringCopy.Sentence(ScoringCopy.Reason(DecisionOutcome.Skipped(reason)));
        Assert.Equal(expected, line);
        Assert.Equal(expected, ScoringCopy.MarkSentence(new MeasureMark { Name = "Totals", State = "not run", Sentence = line }));
    }

    [Theory]
    [InlineData("No suggestion service is configured.")]
    [InlineData("Unknown question template: contoso")]
    public async Task Unscored_totals_and_match_use_one_calm_line(string reason)
    {
        var totals = new JevTotalsScorer((_, _, _, _) => Task.FromResult(DecisionOutcome.Skipped(reason)));
        var mark = await totals.ScoreAsync(new("Contoso", "Contoso total.", "1"), Array.Empty<TotalProbe>(), new(), false, CancellationToken.None);
        Assert.Equal(Calm, mark.Sentence);
        var scorer = new JevDescriptionMatchScorer((_, _) => Task.FromResult(DecisionOutcome.Skipped(reason)));
        var match = await scorer.ScoreAsync(new("Contoso total.", "1", "Contoso"), CancellationToken.None);
        Assert.Equal(Calm, "Not scored yet. " + match.Reason);
    }

    [Fact]
    public void First_run_discloses_that_its_estimate_is_not_measured()
    {
        var plan = MeasureRunPlanner.Build(Enumerable.Range(0, 12).Select(i => new MeasureRunCandidate {
            MeasureRef = "measure:Contoso/Measure " + i, Inputs = "Contoso", Formula = "SUM(Contoso[Amount])"
        }).ToArray());
        Assert.True(plan.FirstRun);
        Assert.Equal(168, plan.EstimatedSeconds);
        Assert.Contains("About 3 minutes", plan.Sentence);
        // fx30e F6: ", first run. The estimate improves after this run" is cut (spec section 5, cut 42); FirstRun still says it.
        Assert.DoesNotContain("first run", plan.Sentence, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Selected_run_returns_only_the_two_completed_measures()
    {
        using var f = new Fixture(); await f.Open(12);
        var refs = (await f.Engine.GetChecksPageAsync()).Measures.Take(2).Select(m => m.Ref).ToArray();
        var plan = await f.Engine.PlanMeasureRunAsync(new() { Selection = new() { Measures = refs } });
        var result = await f.Engine.RunMeasurePlanAsync(plan.Id);
        Assert.Equal(refs.OrderBy(r => r), result.Results.Select(r => r.MeasureRef));
        Assert.All(result.Results, r => Assert.NotNull(r.LastChecked));
    }

    [Theory]
    [InlineData("DISTINCTCOUNT(Sales[ProductKey])", "distinct count")]
    [InlineData("SUMX(Sales, Sales[Amount])", "row calculations")]
    [InlineData("AVERAGE(Sales[Amount])", "average")]
    public async Task Run_reasons_name_the_calculation_in_plain_words(string formula, string words)
    {
        using var f = new Fixture(); await f.Open();
        await f.Sessions.Current.MutateAsync("human", "Contoso formula", m => m.Tables["Sales"].Measures["Total Sales"].Expression = formula);
        // A fresh engine has no changed-measure priority hiding the calculation reason.
        using var reader = new LocalEngine(f.Sessions, TestEntitlements.Pro, f.Root);
        var plan = await reader.PlanMeasureRunAsync();
        Assert.Contains(words, Assert.Single(plan.Picks).Reason);
        Assert.DoesNotContain("semi-additive", Assert.Single(plan.Picks).Reason);
        Assert.DoesNotContain("iterator", Assert.Single(plan.Picks).Reason);
    }

    [Fact]
    public async Task Timing_settings_are_check_state_and_leave_real_edit_undo_intact()
    {
        using var f = new Fixture(); await f.Open();
        await f.Engine.SetDescriptionAsync(Ref, "Contoso amounts.", "human");
        var revision = f.Sessions.Current.Revision;
        var steps = await f.Sessions.Current.ReadAsync(_ => f.Sessions.Current.UndoStateNow().Steps);
        await f.Engine.SetMeasurePerformanceAsync(new() { SlowThresholdMs = 1750 }, Ref, true);
        Assert.Equal(revision, f.Sessions.Current.Revision);
        Assert.Equal(steps, await f.Sessions.Current.ReadAsync(_ => f.Sessions.Current.UndoStateNow().Steps));
        Assert.Null(await f.Sessions.Current.ReadAsync(m => m.GetAnnotation("Semanticus.Performance")));
        Assert.Null(await f.Sessions.Current.ReadAsync(m => m.Tables["Sales"].Measures["Total Sales"].GetAnnotation("Semanticus.Performance.Include")));
        await f.Engine.UndoAsync("human");
        Assert.Empty((await f.Engine.GetMeasureDescriptionAsync(Ref)).Description);
        var marks = await f.Engine.GetMeasureMarksAsync(Ref);
        Assert.True(marks.IncludePerformanceForMeasure);
        Assert.Equal(1750, marks.PerformanceSettings.SlowThresholdMs);
    }

    private sealed class SlowScore : IDescriptionMatchScorer
    {
        internal readonly TaskCompletionSource<DescriptionMatch> Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Block;
        public Task<DescriptionMatch> ScoreAsync(DescriptionMatchInput input, CancellationToken token) => Block ? Done.Task.WaitAsync(token)
            : Task.FromResult(new DescriptionMatch(90, "matches", "Contoso matches.", "Matches"));
    }

    [Fact]
    public async Task Undo_and_redo_publish_description_before_a_blocked_score_finishes()
    {
        using var f = new Fixture(); await f.Open();
        var slow = new SlowScore(); f.Engine.DescriptionMatchScorer = slow;
        try
        {
            await f.Engine.SetDescriptionAsync(Ref, "Contoso original.", "human");
            using var attached = f.Engine.AttachAssistantDoor();
            var proposal = await f.Engine.ProposeMeasureDescriptionAsync(Ref).WaitAsync(TimeSpan.FromSeconds(2));
            var request = (await f.Engine.AssistantRequests.ListAsync(f.Sessions.Current.Id)).Single();
            var claim = await f.Engine.AssistantRequests.ClaimAsync(request.RequestId, "Contoso assistant");
            await f.Engine.AssistantRequests.CompleteAsync(request.RequestId, claim.Request.ClaimToken, new AssistantAnswer { Text = "Contoso kept." });
            slow.Block = true;
            await f.Engine.KeepMeasureDescriptionAsync(proposal.Id, "human");
            await f.Engine.UndoAsync("human").WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal("Contoso original.", (await f.Engine.GetMeasureDescriptionAsync(Ref).WaitAsync(TimeSpan.FromSeconds(2))).Description);
            Assert.Equal("Contoso original.", (await f.Engine.GetChecksPageAsync().WaitAsync(TimeSpan.FromSeconds(2))).Measures.Single().OneLine);
            await f.Engine.RedoAsync("human").WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal("Contoso kept.", (await f.Engine.GetMeasureDescriptionAsync(Ref).WaitAsync(TimeSpan.FromSeconds(2))).Description);
        }
        finally { slow.Done.TrySetResult(new(null, "not scored yet", "Contoso", "not scored yet")); }
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly string Root = Path.Combine(Path.GetTempPath(), "contoso-fx11-" + Guid.NewGuid().ToString("N")[..8]);
        internal readonly SessionManager Sessions = new();
        internal readonly LocalEngine Engine;
        internal Fixture() => Engine = new(Sessions, TestEntitlements.Pro, Root);
        internal async Task Open(int count = 1)
        {
            await Engine.CreateModelAsync("Contoso", 1604);
            await Sessions.Current.MutateAsync("human", "Contoso fixture", m => {
                var table = m.AddTable("Sales");
                table.AddDataColumn("Amount", "Amount", dataType: TabularEditor.TOMWrapper.DataType.Decimal);
                table.AddDataColumn("ProductKey", "ProductKey", dataType: TabularEditor.TOMWrapper.DataType.Int64);
                table.AddMeasure("Total Sales", "SUM(Sales[Amount])");
                for (var i = 1; i < count; i++) table.AddMeasure("Contoso " + i, "[Total Sales] + " + i);
            });
        }
        public void Dispose() { Engine.Dispose(); Sessions.Dispose(); if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }
}
