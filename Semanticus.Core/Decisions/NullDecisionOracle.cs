// The tested path.
//
// With no decision service configured the product behaves exactly as it did before one existed. That is not
// a fallback bolted on at the end: it is the default, it is what every free session gets, and it is what the
// frozen case matrix measures against. This class is deliberately the dullest file in the feature.

using System.Threading;
using System.Threading.Tasks;

namespace Semanticus.Decisions
{
    public sealed class NullDecisionOracle : IDecisionOracle
    {
        public static readonly NullDecisionOracle Instance = new NullDecisionOracle();

        public bool Available { get { return false; } }

        public Task<DecisionAnswers> AskAsync(DecisionRequest request, CancellationToken ct) =>
            Task.FromResult(DecisionAnswers.NotDecided(request, "no decision service is configured for this session"));
    }
}
