using System;
using System.Threading.Tasks;

namespace Semanticus.Engine;

public sealed partial class LocalEngine
{
    // The person's "It is on purpose" for a formula that ignores a filter (1.7 item 2). Stored like the Totals
    // "This is expected" waiver: an annotation on the measure, saved with the model, on the undo timeline.
    public async Task<bool> SetMeasureFiltersOnPurposeAsync(string measureRef, bool onPurpose, string origin = "human")
    {
        if (string.IsNullOrWhiteSpace(measureRef)) throw new ArgumentException("Choose a measure to mark its filters as on purpose.");
        var session = _sessions.Require();
        await session.MutateAsync(origin, onPurpose ? "Mark ignored filters as on purpose" : "Remove on purpose from ignored filters",
            model => DescriptionMeasure(model, measureRef).SetAnnotation(FiltersOnPurposeAnnotation, onPurpose ? "true" : null)).ConfigureAwait(false);
        return await session.ReadAsync(model => DescriptionMeasure(model, measureRef).GetAnnotation(FiltersOnPurposeAnnotation) == "true").ConfigureAwait(false);
    }

    internal const string FiltersOnPurposeAnnotation = "Semanticus.Filters.OnPurpose";
}
