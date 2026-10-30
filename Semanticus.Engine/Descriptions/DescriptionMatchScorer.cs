using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Decisions;
using Semanticus.Engine.Decisions;

namespace Semanticus.Engine;

// J1 questions.py align(v2), measured in j1.md section 6. Keep the wording exact.
public static class DescriptionMatchQuestions
{
    public const string Point = DecisionRegistry.DescriptionMatch;
    public static readonly IReadOnlyList<DecisionQuestion> Questions = new[] {
        new DecisionQuestion("match", DecisionQuestionKind.Noul,
            "Is everything that `description` says about the measure `name` true of `formula`? The description may leave things out; only what it states counts.",
            new[] {
                new DecisionOption("yes", "Every statement in the description is true of the formula"),
                new DecisionOption("no", "At least one statement in the description is false for the formula, for example the wrong price, the wrong date column, a filter the formula does not have, or a different calculation") }),
        new DecisionQuestion("detail", DecisionQuestionKind.Noul,
            "Does `description` say enough about how `formula` works to tell this measure apart from a similar one?",
            new[] {
                new DecisionOption("yes", "It names the filters, the dates or the total and blank handling that the formula uses"),
                new DecisionOption("no", "It gives only the general topic of the measure") })
    };
    public static IReadOnlyDictionary<string, object> Snapshot(DescriptionMatchInput input) =>
        new Dictionary<string, object> { ["description"] = input.Description, ["formula"] = input.Formula, ["name"] = input.Name };
}

public sealed class DescriptionMatchBands
{
    public int MatchesAt { get; init; } = 70;
    public int ImproveAt { get; init; } = 50;
    public DescriptionMatch Read(int score, string reason)
    {
        if (ImproveAt < 0 || MatchesAt <= ImproveAt || MatchesAt > 100)
            throw new InvalidOperationException("The Description match bands must be between 0 and 100, in increasing order.");
        score = Math.Clamp(score, 0, 100);
        return score >= MatchesAt ? new(score, "matches", reason, "Matches")
            : score >= ImproveAt ? new(score, "could be improved", reason, "Could be improved")
            : new(score, "does not match", reason, "Does not match");
    }
}

// Uses the product's registered, consented and metered Jev route. No second HTTP client or key path.
internal sealed class JevDescriptionMatchScorer : IDescriptionMatchScorer
{
    private readonly Func<DescriptionMatchInput, CancellationToken, Task<DecisionOutcome>> _ask;
    private readonly DescriptionMatchBands _bands;
    internal JevDescriptionMatchScorer(Func<DescriptionMatchInput, CancellationToken, Task<DecisionOutcome>> ask,
        DescriptionMatchBands bands = null) { _ask = ask; _bands = bands ?? new(); }
    public async Task<DescriptionMatch> ScoreAsync(DescriptionMatchInput input, CancellationToken cancellationToken)
    {
        DescriptionMatch Unscored(string reason) => new(null, "not scored yet", reason, "not scored yet");
        DecisionOutcome outcome;
        try { outcome = await _ask(input, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (System.Net.Http.HttpRequestException) { return Unscored(ScoringCopy.Unreachable); }
        catch { return Unscored(ScoringCopy.Unavailable); }
        if (outcome.Answers?.Decided != true || !outcome.Answers.Answers.TryGetValue("match", out var match)
            || !match.Probabilities.TryGetValue("yes", out var probability) || !double.IsFinite(probability)
            || probability < 0 || probability > 1)
            return Unscored(ScoringCopy.Reason(outcome));
        var score = (int)Math.Round(probability * 100, MidpointRounding.AwayFromZero);
        var reason = score >= _bands.MatchesAt ? "The Description statements match the formula."
            : "Some Description statements may differ from the formula. Compare the calculation, filters and dates.";
        return _bands.Read(score, reason);
    }
}
