using System;
using System.Collections.Generic;
using System.Linq;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>The I3 cross-measure cascade (LocalEngine.DemoteDependentFailures, pure over outcomes):
    /// a Fail downstream of another Fail is a cascade named after its root, never a second root — and a
    /// Fail with only PASSING dependencies keeps its verdict (direct evidence is never overridden).</summary>
    public sealed class TestSuiteCascadeTests
    {
        private static ReconcileOutcome O(string id, Verdict v, string msg = "mismatch in 1 of 3 cells")
            => new ReconcileOutcome { DefId = id, Title = id, Verdict = v, Message = msg };

        private static Dictionary<string, (string Measure, IReadOnlyList<string> DependsOnBound)> Plans(
            params (string id, string measure, string[] deps)[] plans)
            => plans.ToDictionary(p => p.id, p => (p.measure, (IReadOnlyList<string>)p.deps));

        [Fact]
        public void Dependent_fail_is_demoted_to_suspect_naming_the_root()
        {
            var outcomes = new List<ReconcileOutcome> { O("a", Verdict.Fail), O("b", Verdict.Fail) };
            LocalEngine.DemoteDependentFailures(outcomes, Plans(
                ("a", "Net Revenue", Array.Empty<string>()),
                ("b", "Gross Margin %", new[] { "Net Revenue" })));
            Assert.Equal(Verdict.Fail, outcomes[0].Verdict);            // the true root keeps its Fail
            Assert.Equal(Verdict.Suspect, outcomes[1].Verdict);         // the cascade is demoted…
            Assert.Contains("[Net Revenue]", outcomes[1].Message);      // …naming its root cause (I3)
            Assert.Contains("Original result:", outcomes[1].Message);   // …without losing its own evidence
        }

        [Fact]
        public void Chain_of_fails_leaves_exactly_one_root()
        {
            // A → B → C all fail: demotion judges against the ORIGINAL fail set, so B and C both demote
            // and only A survives as the root, regardless of outcome order.
            var outcomes = new List<ReconcileOutcome> { O("c", Verdict.Fail), O("a", Verdict.Fail), O("b", Verdict.Fail) };
            LocalEngine.DemoteDependentFailures(outcomes, Plans(
                ("a", "A", Array.Empty<string>()),
                ("b", "B", new[] { "A" }),
                ("c", "C", new[] { "B", "A" })));
            Assert.Equal(Verdict.Fail, outcomes.Single(o => o.DefId == "a").Verdict);
            Assert.Equal(Verdict.Suspect, outcomes.Single(o => o.DefId == "b").Verdict);
            Assert.Equal(Verdict.Suspect, outcomes.Single(o => o.DefId == "c").Verdict);
        }

        [Fact]
        public void Fail_over_passing_dependency_is_not_demoted()
        {
            // B's dependency passed its own reconciliation: B's failure is independent evidence and must
            // stand as a root — demoting it would hide a real defect behind a healthy neighbour.
            var outcomes = new List<ReconcileOutcome> { O("a", Verdict.Pass), O("b", Verdict.Fail) };
            LocalEngine.DemoteDependentFailures(outcomes, Plans(
                ("a", "A", Array.Empty<string>()),
                ("b", "B", new[] { "A" })));
            Assert.Equal(Verdict.Fail, outcomes.Single(o => o.DefId == "b").Verdict);
        }

        [Fact]
        public void Unrelated_fails_both_stay_roots()
        {
            var outcomes = new List<ReconcileOutcome> { O("a", Verdict.Fail), O("b", Verdict.Fail) };
            LocalEngine.DemoteDependentFailures(outcomes, Plans(
                ("a", "A", Array.Empty<string>()),
                ("b", "B", Array.Empty<string>())));
            Assert.All(outcomes, o => Assert.Equal(Verdict.Fail, o.Verdict));
        }

        [Fact]
        public void Analyzer_counts_only_the_surviving_root()
        {
            // End-to-end with the real analyzer: one root + one demoted cascade = ONE root failure.
            var outcomes = new List<ReconcileOutcome> { O("a", Verdict.Fail), O("b", Verdict.Fail) };
            LocalEngine.DemoteDependentFailures(outcomes, Plans(
                ("a", "A", Array.Empty<string>()),
                ("b", "B", new[] { "A" })));
            var h = TestHealthAnalyzer.Analyze(
                RelationshipIntegrity.Evaluate(Array.Empty<RelationshipCheckInput>()),
                SecurityStaticChecks.Evaluate(Array.Empty<RoleFilterInput>()),
                outcomes);
            Assert.Equal(1, h.RootFailures);
            Assert.Equal(1, h.Failed);
            Assert.Equal(1, h.Suspect);
        }
    }
}
