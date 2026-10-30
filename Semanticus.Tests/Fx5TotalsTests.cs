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

public sealed class Fx5TotalsTests
{
    private static readonly ContextColumn City = new("Customer", "City");
    private static readonly ContextColumn Step = new("Bridge Steps", "Step");
    private static readonly ContextColumn Detail = new("Bridge Steps", "Detail");
    private static readonly TotalsInput Input = new("Total Cost", "Adds the sales cost.", "SUM(Sales[Cost])");

    private static ContextCandidate Candidate(ContextColumn column, ContextShape shape, ContextRole role, string expectation)
        => new(column.Dax + shape, column, shape, role, "", expectation, 20, Array.Empty<string>(), Array.Empty<string>());

    private static ProbeRow Row(double value, params (ContextColumn Column, string Member)[] members) => new()
    {
        V = value, Blank = false, Members = members.ToDictionary(m => m.Column.Dax, m => (object)m.Member)
    };

    private static (MeasureContextPlan Plan, ProbeResult Probe) Fixture(string reason, ContextShape shape)
    {
        var contexts = new List<ContextCandidate> {
            Candidate(City, ContextShape.Group, ContextRole.Related, "This dimension should split the result."),
            Candidate(Step, shape, reason == "control" ? ContextRole.Control : ContextRole.Related,
                reason == "blocked" ? "The relationship direction should leave the result unchanged."
                : reason == "repeat" ? "The result should repeat for this context." : "Compare the detail rows.")
                with { DetailColumn = shape == ContextShape.Subtotal ? Detail : null }
        };
        if (reason is "overwrite" or "detail overwrite")
            contexts.Add(Candidate(reason == "overwrite" ? Step : Detail, ContextShape.Single, ContextRole.Filter,
                "The formula should overwrite the slicer on this column.") with { Families = new[] { "CALCULATE" } });
        if (reason == "detail blocked")
            contexts.Add(Candidate(Detail, ContextShape.Group, ContextRole.Related,
                "This dimension should leave the result unchanged because the formula blocks its filter path."));
        return (new(contexts, Array.Empty<string>()), new ProbeResult { Status = "ok", Scenarios = new[] {
            new ScenarioEvidence { Name = "grand total", Status = "ok", Rows = new[] { Row(300) } },
            // The City rows do not add up to the total, so the fx30e rule (a sum whose total equals its rows passes
            // without a fit score) does not answer first, and the City probe still reaches the fit question.
            new ScenarioEvidence { Name = "by " + City.Dax + " (unfiltered)", Status = "ok",
                Rows = new[] { Row(100, (City, "Seattle")), Row(150, (City, "Toronto")) } },
            new ScenarioEvidence { Name = "by " + Step.Dax + " (unfiltered)", Status = "ok",
                Rows = new[] { Row(300, (Step, "Gross")), Row(300, (Step, "Net")) } },
            new ScenarioEvidence { Name = "detail under " + Step.Dax, Status = "ok",
                Rows = new[] { Row(300, (Step, "Gross"), (Detail, "First")), Row(300, (Step, "Gross"), (Detail, "Last")) } }
        } });
    }

    private static JevTotalsScorer Scorer(List<JsonElement> fits) => new((point, state, questions, ct) =>
    {
        var request = new DecisionRequest(point, 1, state, questions);
        if (point == TotalsQuestions.FitPoint)
        {
            using var wire = JsonDocument.Parse(JevDecisionOracle.BuildRelayBody(request, JsonSerializer.Serialize(state), "fx5"));
            fits.Add(wire.RootElement.GetProperty("snapshot").GetProperty("probe").Clone());
        }
        using var answer = JsonDocument.Parse(point == TotalsQuestions.TypePoint
            ? "{\"rule\":{\"type\":\"choice\",\"choice\":\"sum_of_rows\",\"confidence\":0.95}}"
            : "{\"fits\":{\"type\":\"noul\",\"noul\":0.05}}");
        return Task.FromResult(DecisionOutcome.Answered(DecisionAnswerReader.Read(request, answer.RootElement, "fx5")));
    });

    [Theory]
    [InlineData("control", ContextShape.Group)]
    [InlineData("blocked", ContextShape.Group)]
    [InlineData("repeat", ContextShape.Group)]
    [InlineData("overwrite", ContextShape.Group)]
    [InlineData("control", ContextShape.Subtotal)]
    [InlineData("blocked", ContextShape.Subtotal)]
    [InlineData("repeat", ContextShape.Subtotal)]
    [InlineData("overwrite", ContextShape.Subtotal)]
    [InlineData("detail overwrite", ContextShape.Subtotal)]
    [InlineData("detail blocked", ContextShape.Subtotal)]
    public async Task Repeating_contexts_never_reach_the_fit_question(string reason, ContextShape shape)
    {
        var (plan, probe) = Fixture(reason, shape);
        var selected = TotalProbeBuilder.From(plan, probe);
        if (reason.StartsWith("detail ", StringComparison.Ordinal))
            Assert.DoesNotContain(selected, p => p.GroupedBy == Detail.Dax);
        else Assert.All(selected, p => Assert.Equal(City.Dax, p.GroupedBy));
        // Keep all eligible probes visible even when the fake reports a conflict.
        foreach (var candidate in selected)
        {
            var fits = new List<JsonElement>();
            await Scorer(fits).ScoreAsync(Input, new[] { candidate }, new(), false, CancellationToken.None);
            Assert.Equal(candidate.GroupedBy, Assert.Single(fits).GetProperty("grouped_by").GetString());
            Assert.NotEqual(Detail.Dax, fits[0].GetProperty("grouped_by").GetString());
            if (!reason.StartsWith("detail ", StringComparison.Ordinal))
                Assert.Equal(City.Dax, fits[0].GetProperty("grouped_by").GetString());
        }
        Assert.Contains(selected, p => p.GroupedBy == City.Dax);
    }

    [Fact]
    public async Task Splitting_contexts_still_reach_Jev_and_warn_even_when_their_values_repeat()
    {
        var (plan, probe) = Fixture("splits", ContextShape.Group);
        var selected = TotalProbeBuilder.From(plan, probe).Single(p => p.GroupedBy == Step.Dax);
        var fits = new List<JsonElement>();
        var mark = await Scorer(fits).ScoreAsync(Input, new[] { selected }, new(), false, CancellationToken.None);
        Assert.Equal("suspect", mark.State);
        Assert.Equal(Step.Dax, Assert.Single(fits).GetProperty("grouped_by").GetString());
    }

    private static string RepoFile(string path)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "Semanticus.sln"))) root = root.Parent;
        Assert.NotNull(root);
        return File.ReadAllText(Path.Combine(root.FullName, path));
    }

    private static string Fields(IEnumerable<string> keys, params (string Key, string Words)[] fields)
    {
        Assert.Equal(fields.Select(f => f.Key).OrderBy(k => k), keys.OrderBy(k => k));
        return string.Join(", ", fields.Take(fields.Length - 1).Select(f => f.Words)) + " and " + fields.Last().Words;
    }

    [Fact]
    public void Help_names_the_fields_actually_sent_for_the_Description_score_and_Totals()
    {
        var description = DescriptionMatchQuestions.Snapshot(new(Input.Description, Input.Formula, Input.Name));
        var type = TotalsQuestions.TypeSnapshot(Input);
        var fit = TotalsQuestions.FitSnapshot(Input, new(City.Dax, new[] { new TotalProbeRow("Seattle", 100) }, 100), "sum_of_rows");
        using var wire = JsonDocument.Parse(JevDecisionOracle.BuildRelayBody(
            new DecisionRequest(TotalsQuestions.FitPoint, 1, fit, TotalsQuestions.FitQuestions), JsonSerializer.Serialize(fit), "fx5"));
        var probe = wire.RootElement.GetProperty("snapshot").GetProperty("probe");
        var common = Fields(description.Keys, ("name", "name"), ("description", "Description"), ("formula", "formula"));
        Assert.Equal(common, Fields(type.Keys, ("name", "name"), ("description", "Description"), ("formula", "formula")));
        Assert.Equal(new[] { "description", "expected_total_rule", "formula", "name", "probe" }, fit.Keys.OrderBy(k => k));
        var probeFields = Fields(probe.EnumerateObject().Select(p => p.Name), ("grouped_by", "grouping column"),
            ("facts", "calculated facts"), ("rows", "probe rows"), ("total", "total"));
        var rowFields = Fields(probe.GetProperty("rows")[0].EnumerateObject().Select(p => p.Name),
            ("member", "a member name"), ("value", "a measure value"));
        var descriptionSentence = "The Description score sends the " + common + " to Jev.";
        var totalsSentence = "Totals sends the " + common + " to read the total type.";
        var fitSentence = "For the Fit question, Totals also sends the expected total rule, " + probeFields + ".";
        var rowSentence = "Each probe row contains " + rowFields + ".";
        foreach (var path in new[] { "Semanticus.VSCode/webview/src/help.tsx", "README.md", "Semanticus.VSCode/README.md" })
        {
            var text = RepoFile(path);
            Assert.Contains(descriptionSentence, text);
            Assert.Contains(totalsSentence, text);
            Assert.Contains(fitSentence, text);
            Assert.Contains(rowSentence, text);
            Assert.DoesNotContain("(never the formula", text);
            Assert.DoesNotContain("A row or a value from your tables never goes", text);
        }
        var consent = RepoFile("Semanticus.VSCode/webview/src/consent.tsx");
        Assert.Contains("the " + common + " for the Description score and Totals", consent);
        Assert.Contains("the expected total rule, " + probeFields, consent);
        Assert.Contains(rowSentence, consent);
        Assert.Contains(descriptionSentence, RepoFile("Semanticus.Engine/McpTools.Descriptions.cs"));
        var marks = RepoFile("Semanticus.Engine/McpTools.MeasureMarks.cs");
        Assert.Contains(totalsSentence, marks);
        Assert.Contains(fitSentence, marks);
        Assert.Contains(rowSentence, marks);
        Assert.DoesNotContain("The formula itself is never sent", RepoFile("Semanticus.Engine/McpTools.Decisions.cs"));
        Assert.Equal(7, DecisionRegistry.ConsentSchemaVersion);
    }
}
