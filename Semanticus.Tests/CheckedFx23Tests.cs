using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests;

// d1 (1.7 Checks minimal design), section 3: a mark that does not apply is hidden. ra1 item 7: the engine sends a reason code
// and applies on each mark (MeasureCheckVerdict), so a reworded engine sentence can no longer show a hidden mark again or hide
// a mark that failed. These tests hold the codes the producers set to the rule that reads them. Gap 6: an edit carries its two formulas.
[Collection("restore-root")]
public sealed class CheckedFx23Tests
{
    [Fact]
    public async Task Marks_that_do_not_apply_carry_a_reason_code_and_applies_false()
    {
        using var root = new DecisionFixture();
        using var f = new MeasureMarksDoorTests.Fixture(root.Workspace);
        await f.Open();
        var result = await MeasureMarksTests.Finish(f.Engine, "measure:Sales/Total Sales");
        foreach (var (name, code) in new[] { ("SQL", "no_sql"), ("Trusted number", "no_trusted"), ("Performance", "not_timed"), ("Totals", "totals_off") })
        {
            var mark = Assert.Single(result.Marks, m => m.Name == name);
            Assert.Equal("not run", mark.State);
            Assert.Equal(code, mark.ReasonCode);
            Assert.False(mark.Applies, name);
            Assert.Null(mark.Verdict);
        }
    }

    [Fact]
    public void A_row_count_that_cannot_check_the_filters_is_a_does_not_apply_code()
    {
        Assert.False(MeasureCheckVerdict.Applies("filters_no_rows"));
        // The producer names the code on the mark it made from that reason, not from the words.
        var contexts = File.ReadAllText(Path.Combine(AssistantGateKit.RepoRoot(), "Semanticus.Engine", "LocalEngine.MarkContexts.cs"));
        Assert.Contains("filterMark.Sentence == comparisonReason) filterMark.ReasonCode = \"filters_no_rows\"", contexts);
    }

    [Theory]
    [InlineData("Filters", "The filters could not be probed.")]
    [InlineData("Filters", "Some filter contexts could not finish; check the Contexts mark.")]
    [InlineData("Filters", "Could not check this place.")]
    [InlineData("SQL", "The SQL query could not run.")]
    [InlineData("Performance", "Timing failed: the query was cancelled.")]
    [InlineData("Equivalence", "This edit could not be compared because no model is connected.")]
    [InlineData("Filters", "No SQL check is saved for this measure.")]
    public void A_mark_that_applies_but_could_not_run_is_not_hidden(string name, string sentence)
    {
        // R8: a failure is Unknown, never hidden, whatever its words say. Only a reason code hides a mark.
        var mark = MeasureCheckVerdict.Finish(MeasureMarkJudge.NotRun(name, sentence), null, false);
        Assert.True(mark.Applies);
        Assert.Equal("could_not_run", mark.ReasonCode);
        Assert.Equal("unknown", mark.Verdict);
    }

    [Fact]
    public async Task Equivalence_carries_the_formula_before_and_after_the_edit()
    {
        using var root = new DecisionFixture();
        using var f = new MeasureMarksDoorTests.Fixture(root.Workspace);
        await f.Open();
        var before = f.Sessions.Current.Model.Tables["Sales"].Measures["Total Sales"].Expression;
        await MeasureMarksTests.Finish(f.Engine, "measure:Sales/Total Sales");
        await f.Sessions.Current.MutateAsync("human", "Double Contoso sales", m =>
            m.Tables["Sales"].Measures["Total Sales"].Expression = "SUM('Sales'[Amount]) * 2");
        var result = await MeasureMarksTests.Finish(f.Engine, "measure:Sales/Total Sales");
        var mark = Assert.Single(result.Marks, m => m.Name == "Equivalence");
        Assert.Equal(before, mark.BeforeFormula);
        Assert.Equal("SUM('Sales'[Amount]) * 2", mark.AfterFormula);
    }
}
