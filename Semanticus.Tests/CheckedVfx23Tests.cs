using System;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests;

// v-fx23 (verifier of the fx23 build, d1 Checks design). d1 section 2: "Checking: a run is in progress. The tree row keeps
// its last glyph." The Model tree and the Checks rows read GetMeasureCheckCacheAsync. While a measure is being checked
// again, that cache carries the run's starting view, where SQL and Trusted number read "No ... is saved for this measure."
// until SavedMarksAsync reaches them. The UI reads that sentence as "does not apply" (marksModel.ts NOT_APPLYING), so a
// Broken measure loses its ✗ mid-run, and reads ✓ Verified once Filters, Contexts and Totals finish. This test fails on 44027669a.
[Collection("restore-root")]
public sealed class CheckedVfx23Tests
{
    [Fact]
    public async Task A_measure_being_checked_again_keeps_its_last_verdict_in_the_check_cache()
    {
        using var root = new DecisionFixture();
        using var f = new MeasureMarksDoorTests.Fixture(root.Workspace);
        await f.Open();
        const string reference = "measure:Sales/Total Sales";
        await f.Engine.SaveTestDefinitionAsync(new TestDefinition
        {
            Kind = TestKinds.MeasureValue, Title = "Contoso trusted total", TargetRef = reference,
            ParamsJson = TestSuiteStore.Serialize(new MeasureValueRequest { MeasureRef = reference, ExpectedValue = "999" })
        }, "human");
        var broken = await MeasureMarksTests.Finish(f.Engine, reference);
        Assert.Equal("suspect", broken.Marks.Single(m => m.Name == "Trusted number").State);

        // Hold the queries, so the second run stays in progress while the Checks page and the tree read the cache.
        f.Release.Reset();
        try
        {
            var running = await f.Engine.GetMeasureMarksAsync(reference, refresh: true).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(running.Running);
            var row = Assert.Single((await f.Engine.GetMeasureCheckCacheAsync().WaitAsync(TimeSpan.FromSeconds(10))).Where(c => c.MeasureRef == reference));
            var trusted = row.Marks.Single(m => m.Name == "Trusted number");
            Assert.True(trusted.State == "suspect",
                $"mid-run the check cache shows Trusted number as '{trusted.State}: {trusted.Sentence}', so the row drops its Broken mark");
        }
        finally { f.Release.Set(); }
        await MeasureMarksTests.Finish(f.Engine, reference);
    }
}
