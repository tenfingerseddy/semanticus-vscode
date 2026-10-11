// The one interface the engine asks a typed decision through.
//
// There is exactly one of these in the product and three implementations: the null one (no service, the
// tested path), the recorded one (a JSON fixture, what every unit test uses), and the Jev client (the only
// thing that touches the network, and it lives in Semanticus.Engine so this assembly stays offline).
//
// Two rules that a reader should not have to infer:
//   Available is a fact about configuration, not a promise about the next call. An available oracle may
//   still answer "not decided", and a caller must handle that on every call.
//   AskAsync never throws for an ordinary failure. A timeout, a refusal, a malformed reply and an exhausted
//   budget all come back as DecisionAnswers.NotDecided with a plain reason, because a decision service going
//   quiet must never turn into a failed model operation.

using System.Threading;
using System.Threading.Tasks;

namespace Semanticus.Decisions
{
    public interface IDecisionOracle
    {
        /// <summary>True when this oracle is configured to answer at all. False means the product behaves
        /// exactly as it did before any decision service existed.</summary>
        bool Available { get; }

        /// <summary>Ask one registered point's questions. Never throws for an ordinary failure; returns
        /// <see cref="DecisionAnswers.NotDecided(DecisionRequest, string)"/> instead.</summary>
        Task<DecisionAnswers> AskAsync(DecisionRequest request, CancellationToken ct);
    }
}
