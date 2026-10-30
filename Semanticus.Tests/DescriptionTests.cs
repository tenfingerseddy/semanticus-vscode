using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.Jev;
using Semanticus.Engine.Decisions;
using Semanticus.Engine.Lineage;
using Semanticus.Decisions;
using Xunit;

namespace Semanticus.Tests;

[Collection("restore-root")]
// Keep the new Description cases in the existing full Checked gate filter.
public sealed class CheckedDescriptionTests
{
    private const string Ref = "measure:Sales/Sales Amount";
    private const string Draft = "Adds the Contoso sales amounts.";

    [Fact]
    public async Task Reading_Descriptions_and_running_marks_preserve_the_model_projection_and_fingerprint()
    {
        using var f = await Fixture.Open();
        var session = f.Sessions.Current;
        Task<string> Projection() => session.ReadAsync(_ => Semanticus.Engine.StartPlan.StartPlanProjection.Of(session));
        var before = await Projection();
        var fingerprint = JsonSerializer.Serialize(await f.Engine.GetModelFingerprintAsync());
        var revision = session.Revision;
        var steps = await f.Steps();

        await f.Engine.GetMeasureDescriptionAsync(Ref);
        await f.Engine.GetMeasureMarksAsync(Ref);
        var plan = await f.Engine.PlanMeasureRunAsync(new() { Selection = new() { Measures = new[] { Ref } } });
        await f.Engine.RunMeasurePlanAsync(plan.Id);
        await f.Engine.GetMeasureCheckCacheAsync();
        await f.Engine.GetChecksPageAsync();

        Assert.Equal(before, await Projection());
        Assert.Equal(fingerprint, JsonSerializer.Serialize(await f.Engine.GetModelFingerprintAsync()));
        Assert.Equal(revision, session.Revision);
        Assert.Equal(steps, await f.Steps());
        Assert.Null(await f.Source());
    }

    [Fact]
    public async Task Person_edit_records_provenance_in_the_same_undo_step()
    {
        using var f = await Fixture.Open();
        var steps = await f.Steps();
        await f.Engine.SetDescriptionAsync(Ref, Draft, "human");
        Assert.Equal("person", await f.Source());
        Assert.Equal(steps + 1, await f.Steps());
        await f.Engine.UndoAsync("human");
        Assert.Null(await f.Source());
    }

    [Fact]
    public async Task No_assistant_answers_in_less_than_two_seconds_without_a_request()
    {
        using var f = await Fixture.Open();
        var watch = Stopwatch.StartNew();
        var proposal = await f.Engine.DraftMeasureDescriptionAsync(Ref).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2));
        Assert.Equal("unavailable", proposal.State);
        Assert.Contains("No assistant is connected", proposal.Message);
        Assert.Empty(await f.Engine.AssistantRequests.ListAsync(f.Sessions.Current.Id));
        Assert.Equal(0, await f.Steps());
    }

    [Fact]
    public async Task Draft_contains_context_and_keep_is_one_undoable_edit_with_AI_provenance()
    {
        using var f = await Fixture.Open();
        using var attached = f.Engine.AttachAssistantDoor();
        var proposal = await f.Engine.DraftMeasureDescriptionAsync(Ref);
        Assert.Equal("waiting", proposal.State);
        Assert.Contains("Waiting", proposal.Message);
        var request = (await f.Engine.AssistantRequests.ListAsync(f.Sessions.Current.Id)).Single();
        Assert.Contains("SUM(Sales[Amount])", request.Brief);
        Assert.Contains("Sales Amount", request.Brief);
        Assert.Contains("Contoso Sales", request.Brief);
        Assert.Contains("Sales Twice", request.Brief);
        Assert.Contains("Visuals", request.Brief);
        var steps = await f.Steps();
        await f.Answer(request);
        var view = await f.Engine.GetMeasureDescriptionAsync(Ref);
        Assert.Equal("ready", view.Proposal.State);
        Assert.Equal(Draft, view.Proposal.Text);
        Assert.True(string.IsNullOrEmpty(view.Description));
        Assert.Null(await f.Source());
        Assert.Equal(steps, await f.Steps());
        await f.Engine.KeepMeasureDescriptionAsync(proposal.Id, "human");
        Assert.Equal("ai", await f.Source());
        Assert.Equal(steps + 1, await f.Steps());
        await f.Engine.UndoAsync("human");
        Assert.True(string.IsNullOrEmpty((await f.Engine.GetMeasureDescriptionAsync(Ref)).Description));
        Assert.Null(await f.Source());
        await f.Engine.RedoAsync("human");
        Assert.Equal("ai", await f.Source());
        await f.Engine.SetDescriptionAsync(Ref, "Contoso sales total.", "human");
        Assert.Equal("person", await f.Source());
    }

    [Fact]
    public async Task Default_deadline_is_sixty_seconds_and_late_answer_cannot_be_kept()
    {
        using var f = await Fixture.Open();
        using var attached = f.Engine.AttachAssistantDoor();
        var now = DateTimeOffset.UtcNow;
        f.Engine.DescriptionNow = () => now;
        var proposal = await f.Engine.DraftMeasureDescriptionAsync(Ref);
        Assert.Equal(now.AddSeconds(60), proposal.Deadline);
        now = now.AddSeconds(60);
        var view = await f.Engine.GetMeasureDescriptionAsync(Ref);
        Assert.Equal("timedOut", view.Proposal.State);
        Assert.Contains("did not answer", view.Proposal.Message);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Engine.KeepMeasureDescriptionAsync(proposal.Id, "human"));
        Assert.Equal(0, await f.Steps());
    }

    [Fact]
    public async Task Propose_includes_current_description_and_match_reason()
    {
        using var f = await Fixture.Open();
        using var attached = f.Engine.AttachAssistantDoor();
        f.Engine.DescriptionMatchEnabled = true;
        f.Engine.DescriptionMatchScorer = new FakeScorer();
        await f.Engine.SetDescriptionAsync(Ref, "Contoso sales.", "human");
        await f.Engine.ProposeMeasureDescriptionAsync(Ref);
        var request = (await f.Engine.AssistantRequests.ListAsync(f.Sessions.Current.Id)).Single();
        Assert.Contains("Contoso sales.", request.Brief);
        Assert.Contains("The Description needs more detail.", request.Brief);
        Assert.Equal("Contoso sales.", (await f.Engine.GetMeasureDescriptionAsync(Ref)).Description);
    }

    [Fact]
    public async Task Match_can_be_turned_off_and_returns_one_line_without_changing_the_description()
    {
        using var f = await Fixture.Open();
        var scorer = new FakeScorer();
        f.Engine.DescriptionMatchScorer = scorer;
        f.Engine.DescriptionMatchEnabled = false;
        await f.Engine.SetDescriptionAsync(Ref, "Contoso sales.\nAcross all rows.", "human");
        var view = await f.Engine.GetMeasureDescriptionAsync(Ref);
        Assert.False(f.Engine.DescriptionMatchEnabled);
        Assert.Equal("not scored yet", view.Match.Label);
        Assert.Null(view.Match.Score);
        Assert.Equal(0, scorer.Calls);
        Assert.Equal("Contoso sales. Across all rows.", view.OneLine);
        Assert.Contains('\n', view.Description);
        Assert.Equal("person", view.Provenance);
    }

    [Fact]
    public async Task Enabled_match_uses_fresh_description_formula_and_name_after_edits()
    {
        using var f = await Fixture.Open();
        var scorer = new FakeScorer();
        f.Engine.DescriptionMatchScorer = scorer;
        f.Engine.DescriptionMatchEnabled = true;
        await f.Engine.SetDescriptionAsync(Ref, Draft, "human");
        await f.Engine.GetMeasureDescriptionAsync(Ref);
        Assert.Equal(new DescriptionMatchInput(Draft, "SUM(Sales[Amount])", "Sales Amount"), scorer.Last);
        var calls = scorer.Calls;
        await f.Engine.SetDaxAsync(Ref, "SUM(Sales[Amount]) * 2", "human");
        await f.Engine.GetMeasureDescriptionAsync(Ref);
        Assert.True(scorer.Calls > calls);
        Assert.Equal("SUM(Sales[Amount]) * 2", scorer.Last.Formula);
        calls = scorer.Calls;
        await f.Engine.SetDescriptionAsync(Ref, "Double the Contoso sales.", "human");
        await f.Engine.GetMeasureDescriptionAsync(Ref);
        Assert.True(scorer.Calls > calls);
        Assert.Equal("Double the Contoso sales.", scorer.Last.Description);
    }

    [Theory]
    [InlineData(0.0, 0, "Does not match")]
    [InlineData(0.49, 49, "Does not match")]
    [InlineData(0.50, 50, "Could be improved")]
    [InlineData(0.69, 69, "Could be improved")]
    [InlineData(0.70, 70, "Matches")]
    [InlineData(1.0, 100, "Matches")]
    public async Task Jev_adapter_uses_match_probability_and_the_measured_bands(double probability, int score, string label)
    {
        var input = new DescriptionMatchInput(Draft, "SUM(Sales[Amount])", "Sales Amount");
        var scorer = new JevDescriptionMatchScorer((snapshot, ct) => {
            Assert.Equal(input, snapshot);
            var request = new DecisionRequest(DescriptionMatchQuestions.Point, 1,
                DescriptionMatchQuestions.Snapshot(snapshot), DescriptionMatchQuestions.Questions);
            Assert.Equal(new[] { "description", "formula", "name" }, request.Snapshot.Keys.OrderBy(x => x));
            Assert.Equal("Is everything that `description` says about the measure `name` true of `formula`? The description may leave things out; only what it states counts.", request.Questions[0].Instructions);
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(new {
                match = new { type = "noul", noul = probability }, detail = new { type = "noul", noul = 0.9 }
            }));
            return Task.FromResult(DecisionOutcome.Answered(DecisionAnswerReader.Read(request, json.RootElement, "Contoso recorded")));
        });
        var match = await scorer.ScoreAsync(input, CancellationToken.None);
        Assert.Equal(score, match.Score);
        Assert.Equal(label, match.Label);
        Assert.False(string.IsNullOrWhiteSpace(match.Reason));
        Assert.Equal("Matches", new DescriptionMatchBands { MatchesAt = 60, ImproveAt = 40 }.Read(60, "Contoso").Label);
    }

    [Fact]
    public async Task Jev_failure_is_unscored_and_default_build_is_live()
    {
        var scorer = new JevDescriptionMatchScorer((input, ct) => Task.FromResult(DecisionOutcome.Skipped("The service is unavailable.")));
        var match = await scorer.ScoreAsync(new(Draft, "1", "Contoso"), CancellationToken.None);
        Assert.Null(match.Score);
        Assert.Equal("not scored yet", match.Label);
        using var f = await Fixture.Open();
        var point = (await f.Engine.GetDecisionPointsAsync()).Single(p => p.Name == DescriptionMatchQuestions.Point);
        Assert.True(point.Live);
    }

    [Fact]
    public void Measured_v2_criteria_reach_both_Jev_routes_with_the_exact_keys_and_words()
    {
        var request = new DecisionRequest(DescriptionMatchQuestions.Point, 1,
            DescriptionMatchQuestions.Snapshot(new(Draft, "SUM(Sales[Amount])", "Sales Amount")), DescriptionMatchQuestions.Questions);
        foreach (var body in new[] { JevDecisionOracle.BuildBody(request, JsonSerializer.Serialize(request.Snapshot)),
            JevDecisionOracle.BuildRelayBody(request, JsonSerializer.Serialize(request.Snapshot), "Contoso recorded") })
        {
            using var json = JsonDocument.Parse(body);
            var criteria = json.RootElement.GetProperty("questions").GetProperty("match").GetProperty("criteria");
            Assert.Equal("Every statement in the description is true of the formula", criteria.GetProperty("true").GetString());
            Assert.Equal("At least one statement in the description is false for the formula, for example the wrong price, the wrong date column, a filter the formula does not have, or a different calculation", criteria.GetProperty("false").GetString());
            Assert.False(criteria.TryGetProperty("yes", out _));
        }
    }

    [Fact]
    public async Task Automatic_rescore_runs_without_a_description_read_and_caches_only_identical_inputs()
    {
        using var f = await Fixture.Open();
        var scorer = new FakeScorer();
        f.Engine.DescriptionMatchScorer = scorer;
        f.Engine.DescriptionMatchEnabled = true;
        await f.Engine.SetDescriptionAsync(Ref, Draft, "human");
        await WaitFor(() => scorer.Calls >= 1);
        var calls = scorer.Calls;
        await f.Engine.SetDaxAsync(Ref, "2", "human");
        await WaitFor(() => scorer.Calls > calls);
        Assert.Equal("2", scorer.Last.Formula);
        calls = scorer.Calls;
        await f.Engine.GetMeasureDescriptionAsync(Ref);
        await f.Engine.GetMeasureDescriptionAsync(Ref);
        Assert.Equal(calls, scorer.Calls);
    }

    [Fact]
    public async Task Actual_adjustable_timeout_cancels_the_claim_and_rejects_a_late_reply()
    {
        using var f = await Fixture.Open();
        using var attached = f.Engine.AttachAssistantDoor();
        var proposal = await f.Engine.DraftMeasureDescriptionAsync(Ref, 1);
        var claim = await f.Engine.AssistantRequests.ClaimAsync(proposal.RequestId, "Contoso assistant");
        Assert.True(claim.Ok, claim.Message);
        await Task.Delay(1400);
        Assert.Equal("timedOut", (await f.Engine.GetMeasureDescriptionAsync(Ref)).Proposal.State);
        var late = await f.Engine.AssistantRequests.CompleteAsync(proposal.RequestId, claim.Request.ClaimToken, new AssistantAnswer { Text = Draft });
        Assert.False(late.Ok);
        Assert.True(string.IsNullOrEmpty((await f.Engine.GetMeasureDescriptionAsync(Ref)).Description));
    }

    [Fact]
    public async Task A_persons_edit_cannot_be_overwritten_by_a_stale_proposal()
    {
        using var f = await Fixture.Open();
        using var attached = f.Engine.AttachAssistantDoor();
        var proposal = await f.Engine.DraftMeasureDescriptionAsync(Ref);
        await f.Answer((await f.Engine.AssistantRequests.ListAsync(f.Sessions.Current.Id)).Single());
        await f.Engine.SetDescriptionAsync(Ref, "Contoso person edit.", "human");
        var steps = await f.Steps();
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Engine.KeepMeasureDescriptionAsync(proposal.Id, "human"));
        Assert.Equal(steps, await f.Steps());
        Assert.Equal("Contoso person edit.", (await f.Engine.GetMeasureDescriptionAsync(Ref)).Description);
    }

    [Fact]
    public async Task Known_visuals_and_both_dependency_directions_reach_the_assistant()
    {
        using var f = await Fixture.Open();
        using var attached = f.Engine.AttachAssistantDoor();
        var root = Path.Combine(f.Root, "Contoso.Report", "definition");
        var visual = Path.Combine(root, "pages", "Contoso overview", "visuals", "Contoso card");
        Directory.CreateDirectory(visual);
        await File.WriteAllTextAsync(Path.Combine(root, "report.json"), "{}");
        await File.WriteAllTextAsync(Path.Combine(visual, "visual.json"), """
            {"visual":{"visualType":"card","query":{"queryState":{"Values":{"projections":[{"field":{"Measure":{"Expression":{"SourceRef":{"Entity":"Sales"}},"Property":"Sales Amount"}}}]}}}}}
            """);
        await f.Engine.SetReportScopeAsync(new[] { new ReportScopeChoice { Kind = "local", Name = "Contoso report", Path = Path.GetDirectoryName(root) } }, "human");
        await f.Engine.CheckReportsAsync(null, true, null, null, null, "human");
        var proposal = await f.Engine.DraftMeasureDescriptionAsync(Ref);
        var context = JsonSerializer.Serialize(proposal.Context);
        Assert.Contains("Contoso card", context);
        Assert.Contains("Contoso overview", context);
        Assert.Contains("Sales Twice", context);
        var dependent = await f.Engine.DraftMeasureDescriptionAsync("measure:Sales/Sales Twice");
        Assert.Contains("Sales Amount", JsonSerializer.Serialize(dependent.Context));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task All_new_actions_work_through_RPC_and_MCP_with_one_keep(bool rpc)
    {
        using var f = await Fixture.Open();
        using var attached = f.Engine.AttachAssistantDoor();
        var target = new EngineRpcTarget(f.Engine);
        var proposal = rpc ? await target.draftMeasureDescription(Ref) : await McpTools.DraftMeasureDescription(f.Engine, Ref);
        await f.Answer((await f.Engine.AssistantRequests.ListAsync(f.Sessions.Current.Id)).Single());
        var before = rpc ? await target.getMeasureDescription(Ref) : await McpTools.GetMeasureDescription(f.Engine, Ref);
        Assert.True(string.IsNullOrEmpty(before.Description));
        if (rpc) await target.keepMeasureDescription(proposal.Id); else await McpTools.KeepMeasureDescription(f.Engine, proposal.Id);
        var after = rpc ? await target.getMeasureDescription(Ref) : await McpTools.GetMeasureDescription(f.Engine, Ref);
        Assert.Equal(Draft, after.Description);
        Assert.Equal("ai", after.Provenance);
        var better = rpc ? await target.proposeMeasureDescription(Ref) : await McpTools.ProposeMeasureDescription(f.Engine, Ref);
        Assert.Equal("waiting", better.State);
        Assert.Equal(1, await f.Steps());
    }

    [Fact]
    public async Task Named_pipe_carries_the_proposal_context_keep_and_undo()
    {
        using var f = await Fixture.Open();
        using var attached = f.Engine.AttachAssistantDoor();
        var pipe = TestPipe.Mint("desc-");
        using var server = new RpcServer(f.Sessions, f.Engine, pipe);
        using var cancellation = new CancellationTokenSource();
        var running = server.RunAsync(cancellation.Token);
        try
        {
            using var remote = await RemoteEngine.ConnectAsync(pipe, timeoutMs: 5000);
            var proposal = await remote.DraftMeasureDescriptionAsync(Ref);
            await f.Answer((await f.Engine.AssistantRequests.ListAsync(f.Sessions.Current.Id)).Single());
            Assert.Equal("ready", (await remote.GetMeasureDescriptionAsync(Ref)).Proposal.State);
            await remote.KeepMeasureDescriptionAsync(proposal.Id, "human");
            Assert.Equal("ai", (await remote.GetMeasureDescriptionAsync(Ref)).Provenance);
            await remote.UndoAsync("human");
            Assert.True(string.IsNullOrEmpty((await remote.GetMeasureDescriptionAsync(Ref)).Description));
        }
        finally { cancellation.Cancel(); try { await running; } catch (OperationCanceledException) { } }
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        for (var n = 0; n < 100 && !condition(); n++) await Task.Delay(20);
        Assert.True(condition());
    }

    [Fact]
    public async Task Rehearsal_does_not_consume_a_ready_proposal()
    {
        using var f = await Fixture.Open();
        using var attached = f.Engine.AttachAssistantDoor();
        var proposal = await f.Engine.DraftMeasureDescriptionAsync(Ref);
        await f.Answer((await f.Engine.AssistantRequests.ListAsync(f.Sessions.Current.Id)).Single());
        DryRunScope.Current = new DryRunCollector();
        try { await f.Engine.KeepMeasureDescriptionAsync(proposal.Id, "human"); }
        finally { DryRunScope.Current = null; }
        var view = await f.Engine.GetMeasureDescriptionAsync(Ref);
        Assert.True(string.IsNullOrEmpty(view.Description));
        Assert.Equal("ready", view.Proposal.State);
        Assert.Equal(0, await f.Steps());
        await f.Engine.KeepMeasureDescriptionAsync(proposal.Id, "human");
        Assert.Equal(Draft, (await f.Engine.GetMeasureDescriptionAsync(Ref)).Description);
    }

    [Fact]
    public async Task Property_grid_bulk_edits_and_undo_preserve_provenance()
    {
        using var f = await Fixture.Open();
        await f.Engine.SetObjectPropertyAsync(Ref, "Description", Draft, "agent");
        Assert.Equal("ai", await f.Source());
        await f.Engine.SetObjectPropertiesAsync(new[] { Ref, "measure:Sales/Sales Twice" }, "Description", "Contoso person edit.", "human");
        Assert.Equal("person", await f.Source());
        await f.Engine.UndoAsync("human");
        Assert.Equal("ai", await f.Source());
        await f.Engine.RedoAsync("human");
        Assert.Equal("person", await f.Source());
    }

    [Fact]
    public async Task Large_context_is_available_in_full_without_brief_truncation()
    {
        using var f = await Fixture.Open();
        using var attached = f.Engine.AttachAssistantDoor();
        var formula = "SUM(Sales[Amount]) /* " + new string('a', 5000) + " Contoso end */";
        await f.Engine.SetDaxAsync(Ref, formula, "human");
        var proposal = await f.Engine.DraftMeasureDescriptionAsync(Ref);
        Assert.Equal("waiting", proposal.State);
        var request = (await f.Engine.AssistantRequests.ListAsync(f.Sessions.Current.Id)).Single();
        Assert.Contains("get_measure_description", request.Brief);
        var read = await f.Engine.GetMeasureDescriptionAsync(Ref);
        Assert.Contains("Contoso end", JsonSerializer.Serialize(read.Proposal.Context));
    }

    [Fact]
    public async Task A_failed_edit_rolls_back_description_and_provenance_together()
    {
        using var f = await Fixture.Open();
        await f.Engine.SetDescriptionAsync(Ref, Draft, "agent");
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Sessions.Current.MutateAsync("human", "Contoso failed edit", m => {
            m.Tables["Sales"].Measures["Sales Amount"].Description = "Contoso rejected edit.";
            throw new InvalidOperationException("Contoso rollback");
        }));
        Assert.Equal(Draft, (await f.Engine.GetMeasureDescriptionAsync(Ref)).Description);
        Assert.Equal("ai", await f.Source());
    }

    [Fact]
    public async Task Provenance_survives_save_and_reopen_and_an_unrecorded_source_is_not_guessed()
    {
        using var f = await Fixture.Open();
        await f.Sessions.Current.MutateAsync("human", "Contoso old source", m => {
            var measure = m.Tables["Sales"].Measures["Sales Amount"];
            measure.Description = Draft;
            measure.SetAnnotation(LocalEngine.DescriptionProvenanceAnnotation, null);
        });
        Assert.Equal("not recorded", (await f.Engine.GetMeasureDescriptionAsync(Ref)).Provenance);
        await f.Engine.SetDescriptionAsync(Ref, "Contoso person edit.", "human");
        var saved = await f.Engine.SaveAsync(null, "bim", false, "human");
        await f.Engine.OpenAsync(saved.Path);
        Assert.Equal("person", (await f.Engine.GetMeasureDescriptionAsync(Ref)).Provenance);
    }

    [Theory]
    [InlineData(70, "Matches")]
    [InlineData(69, "Could be improved")]
    [InlineData(50, "Could be improved")]
    [InlineData(49, "Does not match")]
    public async Task New_model_scores_by_default_at_Kanes_boundaries(int score, string label)
    {
        using var f = await Fixture.Open();
        var oracle = await f.UseMatchOracle(score);
        Assert.Empty(Directory.GetFiles(f.Root, "*.json", SearchOption.AllDirectories)
            .Where(p => Path.GetFileName(Path.GetDirectoryName(p)) == LayoutStore.DecisionSettingsFolder));
        await f.Engine.SetDescriptionAsync(Ref, Draft, "human");
        var revision = f.Sessions.Current.Revision;
        var steps = await f.Steps();
        var rpc = await new EngineRpcTarget(f.Engine).getMeasureDescription(Ref);
        var mcp = await McpTools.GetMeasureDescription(f.Engine, Ref);
        Assert.True(f.Engine.DescriptionMatchEnabled);
        var point = (await f.Engine.GetDecisionPointsAsync()).Single(p => p.Name == DescriptionMatchQuestions.Point);
        Assert.True(point.Live);
        Assert.True(point.Enabled);
        Assert.Equal(score, rpc.Match.Score);
        Assert.Equal(label, rpc.Match.Label);
        Assert.Equal(rpc, mcp);
        Assert.Equal(1, oracle.Calls);
        Assert.Equal(revision, f.Sessions.Current.Revision);
        Assert.Equal(steps, await f.Steps());
        Assert.Equal(Draft, rpc.Description);
        Assert.Equal("person", rpc.Provenance);
        Assert.Null(rpc.Proposal);
    }

    [Fact]
    public async Task Unreachable_relay_leaves_the_description_and_seven_marks_unchanged()
    {
        using var f = await Fixture.Open();
        var oracle = await f.UseMatchOracle(70, unreachable: true);
        await f.Engine.SetDescriptionAsync(Ref, Draft, "human");
        var revision = f.Sessions.Current.Revision;
        var steps = await f.Steps();
        var marks = await MeasureMarksTests.Finish(f.Engine, Ref);
        var view = await f.Engine.GetMeasureDescriptionAsync(Ref);
        Assert.Equal(1, oracle.Calls);
        Assert.Null(view.Match.Score);
        Assert.Equal("not scored yet", view.Match.Label);
        Assert.Equal("not scored yet", view.Match.Level);
        Assert.Equal(Draft, view.Description);
        Assert.Equal("Sales Amount", view.Name);
        Assert.Equal("person", view.Provenance);
        Assert.Null(view.Proposal);
        Assert.Equal(revision, f.Sessions.Current.Revision);
        Assert.Equal(steps, await f.Steps());
        Assert.Equal(JsonSerializer.Serialize(marks), JsonSerializer.Serialize(await f.Engine.GetMeasureMarksAsync(Ref)));
        Assert.Equal(7, marks.Marks.Count(m => m.Name != "Alignment"));
        Assert.Equal("SUM(Sales[Amount])", await f.Sessions.Current.ReadAsync(m => m.Tables["Sales"].Measures["Sales Amount"].Expression));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Model_setting_turns_off_a_cached_score_and_survives_reopen(bool rpc)
    {
        using var f = await Fixture.Open();
        f.Engine.DescriptionMatchEnabled = true;
        var oracle = await f.UseMatchOracle(70);
        await f.Engine.SetDescriptionAsync(Ref, Draft, "human");
        Assert.Equal(70, (await f.Engine.GetMeasureDescriptionAsync(Ref)).Match.Score);
        var calls = oracle.Calls;
        if (rpc) await new EngineRpcTarget(f.Engine).setDecisionPointEnabled(DescriptionMatchQuestions.Point, false);
        else await McpTools.SetDecisionPointEnabled(f.Engine, DescriptionMatchQuestions.Point, false);
        Assert.Null((await f.Engine.GetMeasureDescriptionAsync(Ref)).Match.Score);
        await f.Engine.SetDescriptionAsync(Ref, "Adds all Contoso sales.", "human");
        Assert.Equal("not scored yet", (await f.Engine.GetMeasureDescriptionAsync(Ref)).Match.Label);
        Assert.Equal(calls, oracle.Calls);
        await f.Engine.OpenAsync(Path.Combine(f.Root, "Contoso.bim"), discardUnsaved: true);
        Assert.False(f.Engine.DecisionPointEnabled(DescriptionMatchQuestions.Point));
        await f.Engine.SetDescriptionAsync(Ref, Draft, "human");
        Assert.Null((await f.Engine.GetMeasureDescriptionAsync(Ref)).Match.Score);
        Assert.Equal(calls, oracle.Calls);
        Assert.Null((await f.Engine.GetChecksPageAsync()).Measures.Single(m => m.Ref == Ref).DescriptionMatch.Score);
    }

    private sealed class MatchOracle(int score, bool unreachable) : IDecisionOracle
    {
        public bool Available => true;
        public int Calls;
        public Task<DecisionAnswers> AskAsync(DecisionRequest request, CancellationToken cancellationToken)
        {
            if (request.PointName != DescriptionMatchQuestions.Point)
                return Task.FromResult(DecisionAnswers.NotDecided(request, "Contoso unrelated decision."));
            Interlocked.Increment(ref Calls);
            Assert.Equal(DescriptionMatchQuestions.Point, request.PointName);
            if (unreachable) throw new System.Net.Http.HttpRequestException("Contoso fake relay is unreachable.");
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(new {
                match = new { type = "noul", noul = score / 100.0 }, detail = new { type = "noul", noul = .9 }
            }));
            return Task.FromResult(DecisionAnswerReader.Read(request, json.RootElement, "Contoso fake relay"));
        }
    }

    private sealed class FakeScorer : IDescriptionMatchScorer
    {
        public int Calls;
        public DescriptionMatchInput Last;
        public Task<DescriptionMatch> ScoreAsync(DescriptionMatchInput input, CancellationToken cancellationToken)
        {
            Calls++;
            Last = input;
            return Task.FromResult(new DescriptionMatch(55, "could be improved", "The Description needs more detail.", "Could be improved"));
        }
    }

    private sealed class Fixture : IDisposable
    {
        public string Root = Path.Combine(Path.GetTempPath(), "e6-" + Guid.NewGuid().ToString("N"));
        public SessionManager Sessions = new();
        public LocalEngine Engine;
        private string PreviousRoot;
        public static async Task<Fixture> Open()
        {
            var f = new Fixture();
            Directory.CreateDirectory(f.Root);
            f.PreviousRoot = ConnectionRegistry.RootOverride;
            ConnectionRegistry.RootOverride = Path.Combine(f.Root, "user");
            f.Engine = new LocalEngine(f.Sessions, TestEntitlements.Pro, f.Root);
            var path = Path.Combine(f.Root, "Contoso.bim");
            await File.WriteAllTextAsync(path, """
                {"name":"Contoso","compatibilityLevel":1600,"model":{"culture":"en-US","tables":[{"name":"Sales","columns":[{"name":"Amount","dataType":"decimal","sourceColumn":"Amount"}],"measures":[
                {"name":"Sales Amount","expression":"SUM(Sales[Amount])","displayFolder":"Contoso Sales"},
                {"name":"Sales Twice","expression":"[Sales Amount] * 2"}]}]}}
                """);
            await f.Engine.OpenAsync(path);
            return f;
        }
        public Task<string> Source() => Sessions.Current.ReadAsync(m => m.Tables["Sales"].Measures["Sales Amount"].GetAnnotation(LocalEngine.DescriptionProvenanceAnnotation));
        public Task<int> Steps() => Sessions.Current.ReadAsync(_ => Sessions.Current.UndoStateNow().Steps);
        public async Task<MatchOracle> UseMatchOracle(int score, bool unreachable = false)
        {
            var oracle = new MatchOracle(score, unreachable);
            Engine.UseDecisionsForTest(new DecisionService(oracle));
            await Engine.SetConsentAsync(DecisionRegistry.DeclaredFields.ToArray(), DecisionRegistry.ConsentSchemaVersion, "human");
            return oracle;
        }
        public async Task Answer(AssistantRequest request)
        {
            var claim = await Engine.AssistantRequests.ClaimAsync(request.RequestId, "Contoso assistant");
            Assert.True(claim.Ok, claim.Message);
            var reply = await Engine.AssistantRequests.CompleteAsync(request.RequestId, claim.Request.ClaimToken, new AssistantAnswer { Text = Draft });
            Assert.True(reply.Ok, reply.Message);
        }
        public void Dispose()
        {
            Engine.Dispose(); Sessions.Dispose();
            ConnectionRegistry.RootOverride = PreviousRoot;
            try { Directory.Delete(Root, true); } catch { }
        }
    }
}
