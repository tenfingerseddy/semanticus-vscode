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
using Xunit;
using Xunit.Abstractions;

namespace Semanticus.Tests;

// Verifier v-rc23, step 3: what leaves the machine for the Description score and for Totals, and under which permission.
// Evidence tests. They use the real DecisionService gates and a recording oracle. No network.
[Collection("restore-root")]
public sealed class VerifierRc23ConsentTests(ITestOutputHelper output)
{
    private const string Reference = "measure:Sales/Total Sales";

    private sealed class Recorder : IDecisionOracle
    {
        public bool Available => true;
        internal readonly List<DecisionRequest> Requests = new();
        internal DecisionRequest[] Seen { get { lock (Requests) return Requests.ToArray(); } }
        public Task<DecisionAnswers> AskAsync(DecisionRequest request, CancellationToken ct)
        {
            lock (Requests) Requests.Add(request);
            var text = request.PointName == TotalsQuestions.TypePoint ? "{\"rule\":{\"type\":\"choice\",\"choice\":\"sum_of_rows\",\"confidence\":0.9}}"
                : request.PointName == TotalsQuestions.FitPoint ? "{\"fits\":{\"type\":\"noul\",\"noul\":0.9}}"
                : request.PointName == DescriptionMatchQuestions.Point ? "{\"match\":{\"type\":\"noul\",\"noul\":0.8},\"detail\":{\"type\":\"noul\",\"noul\":0.8}}"
                : null;
            if (text == null) return Task.FromResult(DecisionAnswers.NotDecided(request, "the verifier answers Totals and the Description score only"));
            using var json = JsonDocument.Parse(text);
            return Task.FromResult(DecisionAnswerReader.Read(request, json.RootElement, "Contoso verifier"));
        }
    }

    private static async Task<(DecisionFixture Home, MeasureMarksDoorTests.Fixture F, Recorder Oracle)> OpenAsync(Func<string, DecisionConsent> consent)
    {
        var home = new DecisionFixture();
        var f = new MeasureMarksDoorTests.Fixture(home.Workspace);
        await f.Open();
        await f.Engine.SaveAsync(Path.Combine(home.Workspace, "Contoso.bim"), "bim", false, "human", true);
        var oracle = new Recorder();
        f.Engine.UseDecisionsForTest(new DecisionService(oracle, readConsent: consent));
        return (home, f, oracle);
    }

    private static async Task<(MeasureMarksResult Marks, MeasureDescription View)> ScoreAndMarkAsync(LocalEngine engine)
    {
        await engine.SetDescriptionAsync(Reference, "Adds the Contoso sales amount.", "human");
        var marks = await MeasureMarksTests.Finish(engine, Reference);
        var view = await engine.GetMeasureDescriptionAsync(Reference);
        return (marks, view);
    }

    private void Log(Recorder oracle)
    {
        foreach (var request in oracle.Seen)
            output.WriteLine(request.PointName + " " + JevDecisionOracle.BuildRelayBody(request, JsonSerializer.Serialize(request.Snapshot), "verifier"));
    }

    [Fact]
    public async Task A_version_6_permission_with_every_field_sends_nothing_for_Totals_or_the_Description_score()
    {
        var legacy = DecisionConsent.Grant(DecisionRegistry.DeclaredFields, 6, DateTimeOffset.UtcNow);
        var (home, f, oracle) = await OpenAsync(_ => legacy);
        using (home) using (f)
        {
            var (marks, view) = await ScoreAndMarkAsync(f.Engine);
            Log(oracle);
            Assert.Empty(oracle.Seen);
            var totals = marks.Marks.Single(m => m.Name == "Totals");
            Assert.NotEqual("suspect", totals.State);
            Assert.NotEqual("passed", totals.State);
            Assert.Null(totals.Total);
            Assert.Null(view.Match.Score);
            Assert.Equal("not scored yet", view.Match.Label);
        }
    }

    [Fact]
    public async Task Version_7_sends_exactly_the_declared_fields_and_probe_rows_only_to_the_fit_question()
    {
        var current = DecisionConsent.Grant(DecisionRegistry.DeclaredFields, DecisionRegistry.ConsentSchemaVersion, DateTimeOffset.UtcNow);
        var (home, f, oracle) = await OpenAsync(_ => current);
        using (home) using (f)
        {
            var (marks, view) = await ScoreAndMarkAsync(f.Engine);
            Log(oracle);
            var seen = oracle.Seen;
            Assert.Contains(seen, r => r.PointName == TotalsQuestions.TypePoint);
            Assert.Contains(seen, r => r.PointName == TotalsQuestions.FitPoint);
            Assert.Contains(seen, r => r.PointName == DescriptionMatchQuestions.Point);
            Assert.All(seen, r => Assert.Contains(r.PointName, new[] { TotalsQuestions.TypePoint, TotalsQuestions.FitPoint, DescriptionMatchQuestions.Point }));
            foreach (var request in seen)
            {
                var keys = request.Snapshot.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray();
                if (request.PointName == TotalsQuestions.FitPoint)
                {
                    Assert.Equal(new[] { "description", "expected_total_rule", "formula", "name", "probe" }, keys);
                    using var wire = JsonDocument.Parse(JevDecisionOracle.BuildRelayBody(request, JsonSerializer.Serialize(request.Snapshot), "verifier"));
                    var probe = wire.RootElement.GetProperty("snapshot").GetProperty("probe");
                    Assert.Equal(new[] { "facts", "grouped_by", "rows", "total" }, probe.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal));
                    foreach (var row in probe.GetProperty("rows").EnumerateArray())
                        Assert.Equal(new[] { "member", "value" }, row.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal));
                }
                else Assert.Equal(new[] { "description", "formula", "name" }, keys);
                Assert.Equal("Adds the Contoso sales amount.", request.Snapshot["description"]);
                Assert.Equal("SUM('Sales'[Amount])", request.Snapshot["formula"]);
                Assert.Equal("Total Sales", request.Snapshot["name"]);
            }
            Assert.Equal("passed", marks.Marks.Single(m => m.Name == "Totals").State);
            Assert.Equal(80, view.Match.Score);
        }
    }

    [Fact]
    public async Task Version_7_without_the_probe_field_reads_the_type_and_sends_no_row()
    {
        var narrowed = DecisionConsent.Grant(DecisionRegistry.DeclaredFields.Where(x => x != TotalsQuestions.FitPoint + ":probe"),
            DecisionRegistry.ConsentSchemaVersion, DateTimeOffset.UtcNow);
        var (home, f, oracle) = await OpenAsync(_ => narrowed);
        using (home) using (f)
        {
            var (marks, _) = await ScoreAndMarkAsync(f.Engine);
            Log(oracle);
            Assert.Contains(oracle.Seen, r => r.PointName == TotalsQuestions.TypePoint);
            Assert.DoesNotContain(oracle.Seen, r => r.PointName == TotalsQuestions.FitPoint);
            Assert.DoesNotContain(oracle.Seen, r => r.Snapshot.ContainsKey("probe"));
            var totals = marks.Marks.Single(m => m.Name == "Totals");
            Assert.Equal("info", totals.State);
            Assert.Equal("sum", totals.Total.Type);
        }
    }
}
