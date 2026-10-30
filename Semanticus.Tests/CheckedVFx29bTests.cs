using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.Decisions;
using Xunit;

namespace Semanticus.Tests;

// v-fx29b (verifier of fx29a, fx29b and j29, diff c5d17b704..9bfc63beb). Attacks on values that outlive the moment they were
// true: a consent line written when permission was given, read after permission was taken back, and the name the fix line gives.
[Collection("restore-root")]
public sealed class CheckedVFx29bTests : IDisposable
{
    private const string Reference = "measure:Sales/Total Sales";
    private readonly string safeRoot;
    private readonly string root = Path.Combine(Path.GetTempPath(), "vfx29b-" + Guid.NewGuid().ToString("N")[..8]);
    public CheckedVFx29bTests(RestoreRootFixture fixture) { safeRoot = fixture.Root; ConnectionRegistry.RootOverride = root; }
    public void Dispose()
    {
        ConnectionRegistry.RootOverride = safeRoot;
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }

    // Turn on rewrites each stored advice-off Totals line to "Pro advice is on now. Check again to score it." (fx29b F). If the
    // person takes permission back before the measure is read again, that line is false: advice is off, and the Checks page
    // offers no Turn on beside it. Every door must read a line that is true now.
    [Fact]
    public async Task Withdraw_after_Turn_on_leaves_no_line_that_says_advice_is_on()
    {
        using var f = new MeasureMarksDoorTests.Fixture(root);
        await f.Open(2);
        // fx30e: a real state (a relay, permission off), so the stored advice-off line is what the state says.
        f.Engine.UseDecisionsForTest(new DecisionService(new CheckedFx29cTests.Relay()));
        var given = false;
        f.Engine.TotalsScorer = new JevTotalsScorer((_, _, _, _) => Task.FromResult(given
            ? DecisionOutcome.Skipped("no suggestion service is set up, so nothing was sent and nothing changed")
            : DecisionOutcome.Skipped("you have not given permission to send this model's details for advice")));
        Assert.False((await f.Engine.RunMeasurePlanAsync((await f.Engine.PlanMeasureRunAsync(new MeasureRunRequest { Cap = 25 })).Id)).Stopped);
        var before = (await f.Engine.GetMeasureCheckCacheAsync()).Single(s => s.MeasureRef == Reference);
        Assert.EndsWith(ScoringCopy.AdviceOff, before.Marks.Single(m => m.Name == "Totals").Sentence);

        var consent = await f.Engine.GetConsentAsync();
        given = true;
        Assert.True((await f.Engine.SetConsentAsync(consent.FieldsInForce.ToArray(), consent.SchemaVersionInForce, "human")).Current);
        given = false;
        Assert.False((await f.Engine.WithdrawConsentAsync("human")).Current);

        foreach (var state in await f.Engine.GetMeasureCheckCacheAsync())
        {
            var totals = state.Marks.Single(m => m.Name == "Totals").Sentence;
            Assert.False(totals?.Contains("Pro advice is on now") == true, $"Checks page, {state.MeasureRef}, advice is off but it reads: {totals}");
        }
        var mcp = (await McpTools.GetMeasureCheckCache(f.Engine)).Single(s => s.MeasureRef == "measure:Sales/Sales Copy 1");
        Assert.False(mcp.Marks.Single(m => m.Name == "Totals").Sentence?.Contains("Pro advice is on now") == true,
            "get_measure_check_cache, advice is off but it reads: " + mcp.Marks.Single(m => m.Name == "Totals").Sentence);
    }

    // The fix line names what the formula uses that the model lacks. A measure name with an apostrophe must read whole.
    [Fact]
    public async Task The_missing_name_line_reads_a_name_with_an_apostrophe_whole()
    {
        using var f = new MeasureMarksDoorTests.Fixture(root);
        await f.Open();
        await f.Sessions.Current.MutateAsync("human", "Owner's measures", m =>
        {
            m.Tables["Sales"].AddMeasure("Owner's Total", "SUM('Sales'[Amount])");
            m.Tables["Sales"].AddMeasure("Owner Share", "[Owner's Total] / 2");
        });
        await MeasureMarksTests.Finish(f.Engine, "measure:Sales/Owner Share");
        await f.Sessions.Current.MutateAsync("human", "Remove Owner's Total", m => m.Tables["Sales"].Measures["Owner's Total"].Delete());
        var read = await MeasureMarksTests.Finish(f.Engine, "measure:Sales/Owner Share");
        var filters = read.Marks.Single(m => m.Name == "Filters");
        Assert.Equal("unknown", filters.Verdict);
        Assert.Equal("The formula uses [Owner's Total], which is not in this model. Fix the formula, then check it again.", filters.Sentence);
    }
}
