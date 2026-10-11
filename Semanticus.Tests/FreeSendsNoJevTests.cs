// H1 (completeness audit, 27 Sep): Free never calls Jev, for EVERY decision point, not only the two that were tested.
//
// The central refusal (DecisionService.AskAsync, "part of Semanticus Pro") holds a Free session out only because each
// point is flagged ProOnly. Before this file only measure.folder_and_format and column.summarize were tested for that
// flag, so a new point registered without it would have reached Jev for Free with nothing failing. These run over the
// registry itself, so a point added later is covered the day it is registered.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Decisions;
using Semanticus.Engine.Decisions;
using Xunit;

namespace Semanticus.Tests
{
    public sealed class FreeSendsNoJevTests
    {
        public static IEnumerable<object[]> EveryPoint() => DecisionRegistry.Points.Select(p => new object[] { p.Name });

        [Fact]
        public void The_registry_has_every_point_this_release_ships()
        {
            var names = DecisionRegistry.Points.Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
            foreach (var n in new[] { DecisionRegistry.MeasureFolderAndFormat, DecisionRegistry.PlanRank, DecisionRegistry.AskRoute,
                                      DecisionRegistry.MeasureStarter, DecisionRegistry.ComposeDraft, DecisionRegistry.ColumnFormat, DecisionRegistry.AiSchema })
                Assert.Contains(n, names);
        }

        [Theory]
        [MemberData(nameof(EveryPoint))]
        public void Every_decision_point_is_Pro_only(string name)
        {
            Assert.True(DecisionRegistry.Require(name).ProOnly, name + " is not ProOnly, so the central refusal would let a Free session ask Jev");
        }

        [Theory]
        [MemberData(nameof(EveryPoint))]
        public async Task A_free_ask_on_any_point_sends_nothing(string name)
        {
            var oracle = new CountingOracle();
            var service = new DecisionService(oracle, new DecisionBudget(1_000_000));
            var outcome = await service.AskAsync(name, new Dictionary<string, object>(), Array.Empty<DecisionQuestion>(),
                isPro: false, enabled: true, fence: null, CancellationToken.None);
            Assert.False(outcome.Asked);
            Assert.Contains("Semanticus Pro", outcome.SkippedReason);
            Assert.Equal(0, oracle.Calls);
        }

        private sealed class CountingOracle : IDecisionOracle
        {
            public int Calls;
            public bool Available => true;
            public Task<DecisionAnswers> AskAsync(DecisionRequest request, CancellationToken ct)
            {
                Interlocked.Increment(ref Calls);
                throw new InvalidOperationException("A Free ask reached the oracle.");
            }
        }
    }
}
