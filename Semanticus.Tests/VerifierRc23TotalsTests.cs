using System;
using System.Collections.Generic;
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

// Verifier v-rc23, defect D1: a context that repeats the total by design is used as a Totals probe.
// The Filters mark already expects these contexts to repeat (LocalEngine.MarkContexts.cs, expectsConstant). Totals does not,
// so a correct SUM such as Contoso's Total Cost, probed by the disconnected 'Bridge Steps'[Step], shows rows equal to the
// total. J1 measured that Jev scores "total equals one row" below 0.5 in 18 of 18 cases, so the sum cut warns.
public sealed class VerifierRc23TotalsTests
{
    private static readonly ContextColumn City = new("Customer", "City");
    private static readonly ContextColumn Step = new("Bridge Steps", "Step");
    private static readonly ContextColumn Region = new("Store", "Region");

    private static ProbeRow Row(double value, ContextColumn column = null, string member = null) => new()
    {
        V = value, Blank = false,
        Members = column == null ? new Dictionary<string, object>() : new Dictionary<string, object> { [column.Dax] = member }
    };

    private static (MeasureContextPlan Plan, ProbeResult Probe) ContosoTotalCost()
    {
        var plan = new MeasureContextPlan(new[] {
            new ContextCandidate("total", null, ContextShape.GrandTotal, ContextRole.Total, "", "", 90, Array.Empty<string>(), Array.Empty<string>()),
            new ContextCandidate("group:Customer/City", City, ContextShape.Group, ContextRole.Related,
                "Check the relationship path and its blank member for unmatched keys.",
                "This dimension should split the result through the relationship path.", 20, Array.Empty<string>(), Array.Empty<string>()),
            new ContextCandidate("group:Store/Region", Region, ContextShape.Group, ContextRole.Related,
                "Check the relationship path and its blank member for unmatched keys.",
                "The relationship direction should leave the result unchanged.", 20, Array.Empty<string>(), Array.Empty<string>()),
            new ContextCandidate("group:Bridge%20Steps/Step", Step, ContextShape.Group, ContextRole.Control,
                "This dimension has no relationship path to the formula's data.",
                "The result should repeat because this dimension cannot filter the data.", 40, Array.Empty<string>(), Array.Empty<string>())
        }, Array.Empty<string>());
        var probe = new ProbeResult { Status = "ok", Scenarios = new[] {
            new ScenarioEvidence { Name = "grand total", Status = "ok", Rows = new[] { Row(300) } },
            new ScenarioEvidence { Name = "by " + City.Dax + " (unfiltered)", Status = "ok",
                Rows = new[] { Row(100, City, "Seattle"), Row(200, City, "Toronto") } },
            new ScenarioEvidence { Name = "by " + Region.Dax + " (unfiltered)", Status = "ok",
                Rows = new[] { Row(300, Region, "North"), Row(300, Region, "South") } },
            new ScenarioEvidence { Name = "by " + Step.Dax + " (unfiltered)", Status = "ok",
                Rows = new[] { Row(300, Step, "Gross"), Row(300, Step, "Discount"), Row(300, Step, "Net") } }
        } };
        return (plan, probe);
    }

    // Answers as Jev measured in J1: a confident sum, and a total that equals one row but not the sum does not fit.
    private static JevTotalsScorer MeasuredJev() => new((point, state, questions, ct) =>
    {
        var request = new DecisionRequest(point, 1, state, questions);
        string text;
        if (point == TotalsQuestions.TypePoint) text = "{\"rule\":{\"type\":\"choice\",\"choice\":\"sum_of_rows\",\"confidence\":0.95}}";
        else
        {
            var facts = (Dictionary<string, object>)((Dictionary<string, object>)state["probe"])["facts"];
            var fits = facts.TryGetValue("total_equals_sum_of_rows", out var sum) && sum is true;
            text = "{\"fits\":{\"type\":\"noul\",\"noul\":" + (fits ? "0.95" : "0.05") + "}}";
        }
        using var json = JsonDocument.Parse(text);
        return Task.FromResult(DecisionOutcome.Answered(DecisionAnswerReader.Read(request, json.RootElement, "Contoso verifier")));
    });

    [Fact]
    public void Contexts_that_repeat_the_total_by_design_are_not_totals_probes()
    {
        var (plan, probe) = ContosoTotalCost();
        var probes = TotalProbeBuilder.From(plan, probe);
        Assert.Contains(probes, p => p.GroupedBy == City.Dax);
        Assert.DoesNotContain(probes, p => p.GroupedBy == Step.Dax);
        Assert.DoesNotContain(probes, p => p.GroupedBy == Region.Dax);
    }

    [Fact]
    public async Task A_correct_sum_does_not_warn_because_a_disconnected_dimension_repeats_it()
    {
        var (plan, probe) = ContosoTotalCost();
        var mark = await MeasuredJev().ScoreAsync(new TotalsInput("Total Cost", "Adds the cost of every sales line.", "SUM ( Sales[Total Cost] )"),
            TotalProbeBuilder.From(plan, probe), new(), false, CancellationToken.None);
        Assert.True(mark.State != "suspect", mark.Sentence);
    }
}
