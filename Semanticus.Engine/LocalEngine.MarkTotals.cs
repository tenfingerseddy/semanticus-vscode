using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Semanticus.Engine.Decisions;

namespace Semanticus.Engine;

public sealed partial class LocalEngine
{
    internal ITotalsScorer TotalsScorer { get; set; }

    public async Task<TotalsSettings> SetMeasureTotalsAsync(TotalsSettings settings = null,
        string measureRef = null, bool? expected = null, string origin = "human")
    {
        settings?.Validate();
        if (expected.HasValue && string.IsNullOrWhiteSpace(measureRef))
            throw new ArgumentException("Choose a measure to mark its totals as expected.");
        var session = _sessions.Require();
        await session.MutateAsync(origin, "Set totals settings", model => {
            var measure = measureRef == null ? null : DescriptionMeasure(model, measureRef);
            if (settings != null) model.SetAnnotation("Semanticus.Totals", JsonSerializer.Serialize(settings));
            if (expected.HasValue) measure.SetAnnotation("Semanticus.Totals.Expected", expected.Value ? "true" : null);
        }).ConfigureAwait(false);
        return await session.ReadAsync(m => TotalsSettings.Read(m.GetAnnotation("Semanticus.Totals"))).ConfigureAwait(false);
    }

    private async Task ScoreMarkTotalsAsync(MarkRun run, TotalProbe[] probes)
    {
        var scorer = TotalsScorer ?? new JevTotalsScorer((point, state, questions, ct) => {
            var fence = new DecisionSessionFence(run.Session.Id, run.Revision, () => {
                var current = _sessions.Current;
                return (current?.Id, current?.Revision ?? -1);
            });
            return Decisions.AskAsync(point, state, questions, Entitlement.FeatureGrants.IsProTier(_entitlement),
                DecisionPointEnabled(point), fence, ct);
        });
        MeasureMark mark;
        try {
            var formula = run.After.Formulas[run.Reference];
            var measure = run.After.ContextModel.Measures.Single(m => m.Table == formula.Table && m.Name == formula.Name);
            var input = new TotalsInput(measure.Name, measure.Description ?? "", measure.Formula ?? "");
            mark = await scorer.ScoreAsync(input, probes, run.After.TotalsSettings,
                run.After.ExpectedTotals.Contains(run.Reference), run.Cancellation).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return; }
        // A failure says what stops scoring, when something does; otherwise it is a fault (fx29c).
        catch { mark = ScoringCopy.NotScored("Totals", (await UnscoredReasonsAsync().ConfigureAwait(false)).TotalsFault); }
        if (MarkRunCurrent(run) && run.Session.Revision == run.Revision) SetMark(run, mark);
    }
}
