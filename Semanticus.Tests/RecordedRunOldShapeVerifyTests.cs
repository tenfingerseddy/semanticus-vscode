// Verifier re-check of m16/validation-view 14adb650 (RecordedCheck.Mismatches, 1.6): a run recorded before the field existed
// has no "mismatches" key. It must still load, and the field must read as absent (null), so the page claims no share for it.
using System.Linq;
using System.Text.Json.Nodes;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    public class RecordedRunOldShapeVerifyTests
    {
        [Fact]
        public void A_run_recorded_before_mismatches_were_kept_still_loads_with_no_count()
        {
            var record = new TestRunRecord
            {
                RunId = "old1", When = "2026-09-01T09:00:00Z", Live = true,
                Outcomes = new TestRunOutcomes
                {
                    Checks = new[]
                    {
                        new RecordedCheck
                        {
                            Kind = "savedCheck", DefId = "d2", Name = "Net Revenue", ContextsTotal = 500, Mismatches = 30,
                            Contexts = Enumerable.Range(0, 21).Select(i => new RecordedContext { Context = "Place " + i, Expected = 100, Actual = 90, Difference = -10 }).ToArray(),
                        },
                    },
                },
            };
            var node = JsonNode.Parse(TestSuiteStore.Serialize(record))!;
            var check = node["outcomes"]!["checks"]![0]!.AsObject();
            Assert.True(check.ContainsKey("mismatches"), "the new shape writes the count");
            check.Remove("mismatches");   // the shape every run recorded before 1.6 has on disk

            var loaded = TestSuiteStore.Deserialize<TestRunRecord>(node.ToJsonString());

            Assert.NotNull(loaded);
            var only = Assert.Single(loaded!.Outcomes.Checks);
            Assert.Null(only.Mismatches);
            Assert.Equal(500, only.ContextsTotal);
            Assert.Equal(21, only.Contexts.Length);
        }
    }
}
