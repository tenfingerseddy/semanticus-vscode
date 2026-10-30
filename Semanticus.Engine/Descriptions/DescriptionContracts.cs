using System;
using System.Threading;
using System.Threading.Tasks;

namespace Semanticus.Engine;

public sealed record DescriptionMatchInput(string Description, string Formula, string Name);
public sealed record DescriptionMatch(int? Score, string Level, string Reason, string Label);
public interface IDescriptionMatchScorer
{
    Task<DescriptionMatch> ScoreAsync(DescriptionMatchInput input, CancellationToken cancellationToken);
}
public sealed record DescriptionProposal(string Id, string State, string Message, string Text,
    DateTimeOffset? Deadline, string RequestId)
{
    // Full context stays readable even when it exceeds the Plan task brief's size limit.
    public object Context { get; init; }
}
public sealed record MeasureDescription(string Ref, string Name, string Description, string OneLine,
    string Provenance, DescriptionMatch Match, DescriptionProposal Proposal);
