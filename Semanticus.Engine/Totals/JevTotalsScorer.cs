using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Decisions;
using Semanticus.Engine.Decisions;

namespace Semanticus.Engine;

internal sealed class JevTotalsScorer : ITotalsScorer
{
    private readonly Func<string, IReadOnlyDictionary<string, object>, IReadOnlyList<DecisionQuestion>, CancellationToken, Task<DecisionOutcome>> ask;
    internal JevTotalsScorer(Func<string, IReadOnlyDictionary<string, object>, IReadOnlyList<DecisionQuestion>, CancellationToken, Task<DecisionOutcome>> ask)
        => this.ask = ask;

    public async Task<MeasureMark> ScoreAsync(TotalsInput input, TotalProbe[] probes, TotalsSettings settings,
        bool expected, CancellationToken cancellationToken)
    {
        settings.Validate();
        DecisionOutcome outcome;
        try {
            outcome = await ask(TotalsQuestions.TypePoint, TotalsQuestions.TypeSnapshot(input),
                TotalsQuestions.TypeQuestions, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (System.Net.Http.HttpRequestException) { return ScoringCopy.NotScored("Totals", ScoringCopy.Unreachable); }
        catch { return ScoringCopy.NotScored("Totals", ScoringCopy.Unavailable); }
        if (outcome.Answers?.Decided != true || !outcome.Answers.Answers.TryGetValue("rule", out var answer)
            || !TotalsQuestions.Rules.Any(r => r.Id == answer.Choice) || !Probability(answer.Confidence))
            return ScoringCopy.NotScored("Totals", ScoringCopy.Reason(outcome));
        var rule = answer.Choice;
        var confidence = answer.Confidence;
        var type = TypeName(rule);
        var first = probes.FirstOrDefault();
        MeasureMark Read(string state, string sentence, TotalProbe probe, double? fit = null, string reason = null) => new() {
            Name = "Totals", State = state, Sentence = sentence,
            Total = new(rule, type, confidence, fit, expected),
            Cells = Cells(probe), Action = state == "suspect" ? "This is expected" : null,
            ReasonCode = reason, ActionCodes = state == "suspect" ? new[] { "expected" } : Array.Empty<string>(),
            LastRan = DateTimeOffset.UtcNow.ToString("O")
        };
        if (confidence < settings.TypeUnclearBelow) return Read("info", "Type unclear.", first, reason: "totals_type_unclear");
        var info = "Checked as " + Article(rule) + ". Totals for this type are shown, not judged.";
        if (expected) return Read("info", ExpectedSentence(rule), first, reason: "totals_expected");
        if (rule == "distinct_count") return Read("info", info, first, reason: "totals_distinct");
        // A sum whose every probed total is exactly the sum of its rows is right, whatever a fit score says: the arithmetic is
        // the proof (fx30e F5: SUM ( Sales[Quantity] ) * 2 read Unknown at fit 0.38 with a total equal to its rows).
        if (rule == "sum_of_rows" && probes.Length > 0
            && probes.All(p => TotalsQuestions.Facts(p).GetValueOrDefault("total_equals_sum_of_rows") is true))
            return Read("passed", Passed(rule), first);
        var (warn, pass, typeAtLeast) = settings.Cuts(rule);
        if (confidence < typeAtLeast || probes.Length == 0) return Read("info", info, first, reason: "totals_not_judged");

        // Two cuts measured on a held-out set (l2 totals-report.md): warn below the warn cut, Verified only when
        // every probe reaches the pass cut. A pass cut set below the warn cut never widens the pass.
        pass = Math.Max(pass, warn);
        double? lowest = null;
        TotalProbe weakest = first;
        var complete = true;
        string missingReason = null;
        foreach (var probe in probes) {
            cancellationToken.ThrowIfCancellationRequested();
            DecisionOutcome fitOutcome;
            try {
                fitOutcome = await ask(TotalsQuestions.FitPoint, TotalsQuestions.FitSnapshot(input, probe, rule),
                    TotalsQuestions.FitQuestions, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (System.Net.Http.HttpRequestException) { complete = false; missingReason = ScoringCopy.Unreachable; continue; }
            catch { complete = false; missingReason = ScoringCopy.Unavailable; continue; }
            if (fitOutcome.Answers?.Decided != true || !fitOutcome.Answers.Answers.TryGetValue("fits", out var fitAnswer)
                || !fitAnswer.Probabilities.TryGetValue("yes", out var fit) || !Probability(fit)) {
                complete = false; missingReason = ScoringCopy.Reason(fitOutcome); continue;
            }
            if (fit < warn)
                return Read("suspect", "The total does not fit " + Article(rule) + ": rows "
                    + string.Join(", ", probe.Rows.Take(5).Select(r => Number(r.Value))) + "; total " + Number(probe.Total) + ".", probe, fit);
            if (lowest == null || fit < lowest) { lowest = fit; weakest = probe; }
        }
        if (!complete) return Read("info", ScoringCopy.Sentence(missingReason), weakest, lowest);
        return lowest >= pass ? Read("passed", Passed(rule), weakest, lowest)
            : Read("unknown", "The total is not clearly right for " + Article(rule) + ".", weakest, lowest, "totals_unclear");
    }

    private static bool Probability(double value) => double.IsFinite(value) && value >= 0 && value <= 1;
    internal static string ExpectedSentence(string rule) => "Checked as " + Article(rule) + ". This is expected.";
    internal static string TypeName(string rule) => rule switch {
        "sum_of_rows" => "sum", "recomputed_at_total" => "recalc (recomputed at the total)",
        "last_value_over_time" => "semi-additive (last value over time)",
        "first_value_over_time" => "semi-additive (first value over time)",
        "distinct_count" => "distinct count", "average" => "average", "blank_at_total" => "blank at total",
        _ => "Type unclear"
    };
    private static string Article(string rule) => rule switch {
        "average" => "a row average", "blank_at_total" => "a blank total", _ => "a " + TypeName(rule)
    };
    private static string Passed(string rule) => rule switch {
        "sum_of_rows" => "Total adds up as a sum, as expected.",
        "recomputed_at_total" => "Total is recomputed at the total, as expected.",
        "last_value_over_time" => "Total shows the last value over time, as expected.",
        "first_value_over_time" => "Total shows the first value over time, as expected.",
        "average" => "Total is an average, as expected.",
        _ => "Total is blank, as expected."
    };
    private static string Number(double? value) => value?.ToString("G", CultureInfo.InvariantCulture) ?? "BLANK";
    private static MeasureMarkCell[] Cells(TotalProbe probe) => probe == null ? Array.Empty<MeasureMarkCell>()
        : probe.Rows.Take(5).Select(r => new MeasureMarkCell { Context = r.Member, After = Number(r.Value) })
            .Append(new MeasureMarkCell { Context = probe.Context, After = Number(probe.Total) }).ToArray();
}
