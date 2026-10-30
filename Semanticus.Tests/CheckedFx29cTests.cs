using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Decisions;
using Semanticus.Engine;
using Semanticus.Engine.Decisions;
using Xunit;

namespace Semanticus.Tests;

// fx29c: rc28 gap 1 at its cause. The rc29 capture of Kane's Contoso (no licence) read "Not scored yet. This score turns on when
// Jev scoring is released." on Totals and Alignment for every measure, at open and after a run. The seeds (EmptyMarks) and the
// fallbacks (an unscored Description, a Description score still on its way, the plan's Description match, the Totals catch)
// all wrote the calm line, whatever the real state was. Now one place reads the state (licence, permission for this model,
// saved or not, a Description to score) and every seed and fallback asks it. Only a real service fault reads the calm line.
// Each case reads every door: the assistant (get_measure_marks), the card (RPC getMeasureMarks, getMeasureDescription), the
// check cache and the Checks page, at open (before any run) and after a run.
[Collection("restore-root")]
public sealed class CheckedFx29cTests : IDisposable
{
    private const string Ref = "measure:Sales/Total Sales";
    private const string Calm = "This score turns on when Jev scoring is released.";
    private const string NeedsPro = "Scoring needs a Pro plan.";
    private const string AdviceOff = "Pro advice is off for this model.";
    private const string NotSaved = "Save the model or open it live to score it.";
    private const string NotChecked = "It is scored when this measure is checked.";
    private const string NoDescription = "There is no Description to score.";
    // A check the card's read started and that has not reached Totals yet: not a score, and nothing stops it.
    private const string Waiting = "This check is waiting to run.";
    private readonly string safeRoot;
    private readonly string root = Path.Combine(Path.GetTempPath(), "fx29c-" + Guid.NewGuid().ToString("N")[..8]);
    public CheckedFx29cTests(RestoreRootFixture fixture) { safeRoot = fixture.Root; ConnectionRegistry.RootOverride = root; }
    public void Dispose()
    {
        ConnectionRegistry.RootOverride = safeRoot;
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }

    // A relay that answers like Jev: a sum, a total that fits, a Description that matches. Down: every ask fails on the wire.
    internal sealed class Relay : IDecisionOracle
    {
        internal bool Down;
        public bool Available => true;
        public Task<DecisionAnswers> AskAsync(DecisionRequest request, CancellationToken ct)
        {
            if (Down) throw new HttpRequestException("The relay did not answer.");
            using var json = JsonDocument.Parse(request.PointName switch {
                TotalsQuestions.TypePoint => "{\"rule\":{\"type\":\"choice\",\"choice\":\"sum_of_rows\",\"confidence\":0.9}}",
                TotalsQuestions.FitPoint => "{\"fits\":{\"type\":\"noul\",\"noul\":0.95}}",
                _ => "{\"match\":{\"type\":\"noul\",\"noul\":0.9},\"detail\":{\"type\":\"noul\",\"noul\":0.8}}"
            });
            return Task.FromResult(DecisionAnswerReader.Read(request, json.RootElement, "Contoso fake"));
        }
    }

    private sealed record Lines(string[] Totals, string[] Alignment);

    private static string Not(string reason) => "Not scored yet. " + reason;
    // The card and the Checks page carry the Description score itself; this reads it as the Alignment mark words.
    private static string Line(DescriptionMatch match) => match.Score == null ? Not(match.Reason) : "Description score " + match.Score;

    // At open: the Checks page and the check cache first (nothing has started a check), then the marks doors while the
    // connected model holds every query, so the check they start cannot score anything before the doors are read.
    private static async Task<Lines> AtOpen(LocalEngine engine, Action hold, Action release)
    {
        var rpc = new EngineRpcTarget(engine);
        var alignment = new List<string>();
        var totals = new List<string>();
        alignment.Add(Line((await rpc.getChecksPage()).Measures.Single(m => m.Ref == Ref).DescriptionMatch));
        hold();
        try
        {
            foreach (var marks in new[] { (await McpTools.GetMeasureMarks(engine, Ref)).Marks, (await rpc.getMeasureMarks(Ref)).Marks })
            {
                totals.Add(marks.Single(m => m.Name == "Totals").Sentence);
                alignment.Add(marks.Single(m => m.Name == "Alignment").Sentence);
            }
            alignment.Add(Line((await rpc.getMeasureDescription(Ref)).Match));
        }
        finally { release(); }
        return new(totals.ToArray(), alignment.ToArray());
    }

    // After a run: a plan run of the measure, then every door once the card's own check has finished.
    private static async Task<Lines> AfterRun(LocalEngine engine)
    {
        var run = await engine.RunMeasurePlanAsync((await engine.PlanMeasureRunAsync(new MeasureRunRequest { Cap = 25 })).Id);
        Assert.False(run.Stopped);
        var rpc = new EngineRpcTarget(engine);
        var card = await MeasureMarksTests.Finish(engine, Ref);
        var states = new[] {
            run.Results.Single(s => s.MeasureRef == Ref).Marks,
            card.Marks,
            (await McpTools.GetMeasureMarks(engine, Ref)).Marks,
            (await rpc.getMeasureMarks(Ref)).Marks,
            (await McpTools.GetMeasureCheckCache(engine)).Single(s => s.MeasureRef == Ref).Marks,
            (await rpc.getMeasureCheckCache()).Single(s => s.MeasureRef == Ref).Marks
        };
        var description = (await rpc.getMeasureDescription(Ref)).Match;
        var page = (await rpc.getChecksPage()).Measures.Single(m => m.Ref == Ref).DescriptionMatch;
        return new(states.Select(m => m.Single(x => x.Name == "Totals").Sentence).ToArray(),
            states.Select(m => m.Single(x => x.Name == "Alignment").Sentence)
                .Concat(new[] { description, page }.Select(Line)).ToArray());
    }

    private static void AllRead(string[] lines, string expected, string where)
    {
        foreach (var line in lines) Assert.True(line == expected, $"{where}: expected \"{expected}\", read \"{line}\" (all: {string.Join(" | ", lines)})");
    }

    private static async Task Describe(MeasureMarksDoorTests.Fixture f, string text)
        => await f.Sessions.Current.MutateAsync("human", "Contoso description", m => m.Tables["Sales"].Measures["Total Sales"].Description = text);

    // A Description score that was asked for has come back (the card asks for it; nothing else waits on it).
    private static async Task Scored(LocalEngine engine)
    {
        for (var i = 0; i < 200; i++)
        {
            var match = (await engine.GetMeasureDescriptionAsync(Ref)).Match;
            if (match.Score != null || match.Reason != null && match.Reason != NotChecked && match.Reason != Calm) return;
            await Task.Delay(20);
        }
    }

    // (a) No licence: "Scoring needs a Pro plan." on Totals and the Description score, at open and after a run. "pro tier":
    // the rc29 capture's engine read tier pro with no licence in its home, so it had no licence to present to the relay and
    // built the null client; its "no suggestion service is set up" skip read the calm line after a run.
    [Theory]
    [InlineData("free")]
    [InlineData("pro tier")]
    public async Task No_licence_reads_needs_a_Pro_plan_at_open_and_after_a_run(string tier)
    {
        using var f = new MeasureMarksDoorTests.Fixture(root, tier == "free" ? TestEntitlements.Free : TestEntitlements.Pro);
        await f.Open();
        await Describe(f, "Total sales amount.");
        var open = await AtOpen(f.Engine, f.Release.Reset, f.Release.Set);
        AllRead(open.Totals, Not(NeedsPro), "Totals at open");
        AllRead(open.Alignment, Not(NeedsPro), "Description at open");
        var after = await AfterRun(f.Engine);
        AllRead(after.Totals, Not(NeedsPro), "Totals after a run");
        AllRead(after.Alignment, Not(NeedsPro), "Description after a run");
    }

    // (b) Pro, advice off for this model: "Pro advice is off for this model." (the card puts Turn on beside it).
    [Fact]
    public async Task Pro_with_advice_off_reads_advice_is_off_at_open_and_after_a_run()
    {
        using var f = new MeasureMarksDoorTests.Fixture(root);
        await f.Open();
        f.Engine.UseDecisionsForTest(new DecisionService(new Relay()));
        await Describe(f, "Total sales amount.");
        var open = await AtOpen(f.Engine, f.Release.Reset, f.Release.Set);
        AllRead(open.Totals, Not(AdviceOff), "Totals at open");
        AllRead(open.Alignment, Not(AdviceOff), "Description at open");
        var after = await AfterRun(f.Engine);
        AllRead(after.Totals, Not(AdviceOff), "Totals after a run");
        AllRead(after.Alignment, Not(AdviceOff), "Description after a run");
    }

    // (c) A never-saved model: Pro and a working relay, but no file and no live model, so no permission can belong to it.
    [Fact]
    public async Task A_never_saved_model_reads_save_it_at_open_and_after_a_run()
    {
        using var sessions = new SessionManager();
        using var engine = new LocalEngine(sessions, TestEntitlements.Pro, root);
        engine.UseDecisionsForTest(new DecisionService(new Relay()));
        await engine.CreateModelAsync("Never Saved", 1604);
        var sales = await engine.CreateTableAsync("Sales", "human");
        await engine.CreateColumnAsync(sales, "Amount", "Decimal", "Amount", "human");
        await sessions.Current.MutateAsync("human", "measure", m => m.Tables["Sales"].AddMeasure("Total Sales", "SUM('Sales'[Amount])").Description = "Total sales amount.");
        var open = await AtOpen(engine, () => { }, () => { });
        AllRead(open.Totals, Not(NotSaved), "Totals at open");
        AllRead(open.Alignment, Not(NotSaved), "Description at open");
        var after = await AfterRun(engine);
        AllRead(after.Totals, Not(NotSaved), "Totals after a run");
        AllRead(after.Alignment, Not(NotSaved), "Description after a run");
    }

    // (d) Pro, advice on, before the measure's scoring has run: not checked yet, in words that are true. After a run: the scores.
    [Fact]
    public async Task Pro_with_advice_on_reads_not_checked_yet_before_scoring_and_the_scores_after()
    {
        using var f = new MeasureMarksDoorTests.Fixture(root);
        await f.Open();
        f.Engine.UseDecisionsForTest(new DecisionService(new Relay()));
        var consent = await f.Engine.GetConsentAsync();
        Assert.True((await f.Engine.SetConsentAsync(consent.FieldsInForce.ToArray(), consent.SchemaVersionInForce, "human")).Current);
        await Describe(f, "Total sales amount.");
        var open = await AtOpen(f.Engine, f.Release.Reset, f.Release.Set);
        foreach (var line in open.Totals)
            Assert.True(line == Not(NotChecked) || line == Waiting, "Totals at open: " + line);
        foreach (var line in open.Alignment)
            Assert.True(line == Not(NotChecked) || line.StartsWith("Description score 90", StringComparison.Ordinal), "Description at open: " + line);
        var after = await AfterRun(f.Engine);
        // The relay scored the total: a type read, not a reason it was not scored.
        foreach (var line in after.Totals) Assert.True(!line.StartsWith("Not scored yet", StringComparison.Ordinal) && !line.Contains("released"), "Totals after a run: " + line);
        foreach (var line in after.Alignment) Assert.StartsWith("Description score 90", line);
    }

    // (e) No Description: the Description score says there is none to score, whatever the licence, never "released".
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_measure_with_no_Description_says_there_is_none_to_score(bool licensed)
    {
        using var f = new MeasureMarksDoorTests.Fixture(root, licensed ? TestEntitlements.Pro : TestEntitlements.Free);
        await f.Open();
        if (licensed)
        {
            f.Engine.UseDecisionsForTest(new DecisionService(new Relay()));
            var consent = await f.Engine.GetConsentAsync();
            await f.Engine.SetConsentAsync(consent.FieldsInForce.ToArray(), consent.SchemaVersionInForce, "human");
        }
        var open = await AtOpen(f.Engine, f.Release.Reset, f.Release.Set);
        AllRead(open.Alignment, Not(NoDescription), "Description at open");
        var after = await AfterRun(f.Engine);
        AllRead(after.Alignment, Not(NoDescription), "Description after a run");
        // The cheap pass of a plan run (Parse, Lint, Orphan keys, Description match) says the same.
        var cheap = (await f.Engine.GetMeasureCheckCacheAsync()).Single(s => s.MeasureRef == Ref).Cheap.Single(m => m.Name == "Description match");
        Assert.Equal(Not(NoDescription), cheap.Sentence);
    }

    // (f) Only a real service fault reads the calm line: the relay fails on the wire.
    [Fact]
    public async Task Only_a_service_fault_reads_the_calm_line()
    {
        using var f = new MeasureMarksDoorTests.Fixture(root);
        await f.Open();
        f.Engine.UseDecisionsForTest(new DecisionService(new Relay { Down = true }));
        var consent = await f.Engine.GetConsentAsync();
        await f.Engine.SetConsentAsync(consent.FieldsInForce.ToArray(), consent.SchemaVersionInForce, "human");
        await Describe(f, "Total sales amount.");
        await Scored(f.Engine);
        var after = await AfterRun(f.Engine);
        AllRead(after.Totals, Not(Calm), "Totals after a run");
        AllRead(after.Alignment, Not(Calm), "Description after a run");
    }

    // The calm line before a fault is not a fault: a Pro session whose relay is down has not tried yet at open.
    [Fact]
    public async Task Before_a_fault_happens_the_line_is_not_checked_yet()
    {
        using var f = new MeasureMarksDoorTests.Fixture(root);
        await f.Open();
        f.Engine.UseDecisionsForTest(new DecisionService(new Relay { Down = true }));
        var consent = await f.Engine.GetConsentAsync();
        await f.Engine.SetConsentAsync(consent.FieldsInForce.ToArray(), consent.SchemaVersionInForce, "human");
        var open = await AtOpen(f.Engine, f.Release.Reset, f.Release.Set);
        foreach (var line in open.Totals)
            Assert.True(line == Not(NotChecked) || line == Waiting, "Totals at open: " + line);
    }

    // The Totals catch: a scorer that fails for a reason that is not the service (here, a bug) still says the true cause
    // when the state already blocks scoring: no licence reads "Scoring needs a Pro plan.", not the calm line.
    [Fact]
    public async Task A_Totals_scorer_failure_asks_the_state_first()
    {
        using var f = new MeasureMarksDoorTests.Fixture(root, TestEntitlements.Free);
        await f.Open();
        f.Engine.TotalsScorer = new Throwing();
        var totals = (await MeasureMarksTests.Finish(f.Engine, Ref)).Marks.Single(m => m.Name == "Totals");
        Assert.Equal(Not(NeedsPro), totals.Sentence);
    }

    private sealed class Throwing : ITotalsScorer
    {
        public Task<MeasureMark> ScoreAsync(TotalsInput input, TotalProbe[] probes, TotalsSettings settings, bool expected, CancellationToken cancellationToken)
            => throw new InvalidOperationException("A bug in the scorer.");
    }

    // Guard: an unscored mark always names its reason (NotScored has no default), and the calm line is written only where a
    // service call failed: a catch, ScoringCopy's own service fallback, or the one place's fault answer.
    [Fact]
    public void Only_the_service_fault_path_produces_the_calm_line()
    {
        var notScored = typeof(ScoringCopy).GetMethod("NotScored", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        Assert.False(notScored.GetParameters().Single(p => p.Name == "reason").HasDefaultValue, "NotScored(name) must name its reason.");
        var engine = Path.Combine(AssistantGateKit.RepoRoot(), "Semanticus.Engine");
        var calm = new Regex(@"ScoringCopy\.(Missing|MissingCheck|Unavailable|Unreachable)\b");
        var found = new List<string>();
        foreach (var file in Directory.GetFiles(engine, "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) && !p.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)
                && Path.GetFileName(p) != "ScoringCopy.cs"))
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
                if (calm.IsMatch(lines[i]) && !lines[i].Contains("catch", StringComparison.Ordinal) && !lines[i].Contains("// service fault", StringComparison.Ordinal))
                    found.Add($"{Path.GetFileName(file)}:{i + 1}: {lines[i].Trim()}");
        }
        Assert.True(found.Count == 0, "The calm line outside a service fault:\n" + string.Join("\n", found));
    }

}
