#nullable enable
using System.Linq;
using System.Threading.Tasks;
using TabularEditor.TOMWrapper;

namespace Semanticus.Engine;

public sealed record MeaningAnnotationCleanup(int Measures, int Annotations, string Note);

public sealed partial class LocalEngine
{
    private static readonly string[] LegacyMeaningAnnotations = {
        "Semanticus.Checked.Meaning", "Semanticus.Checked.MeaningWords"
    };

    public Task<MeaningAnnotationCleanup> CleanupMeaningAnnotationsAsync(string origin) =>
        CleanupMeaningAnnotationsAsync(_sessions.Require(), origin, beforeSave: false);

    private static async Task<MeaningAnnotationCleanup> CleanupMeaningAnnotationsAsync(Session session, string origin, bool beforeSave)
    {
        var found = await session.ReadAsync(model => model.AllMeasures.Any(m =>
            LegacyMeaningAnnotations.Any(m.HasAnnotation))).ConfigureAwait(false);
        if (!found) return new(0, 0, "There are no old meaning annotations to remove. Description stays the same.");
        var measures = 0;
        var annotations = 0;
        void Remove(Model model)
        {
            foreach (var measure in model.AllMeasures)
            {
                var keys = LegacyMeaningAnnotations.Where(measure.HasAnnotation).ToArray();
                if (keys.Length == 0) continue;
                measures++;
                foreach (var key in keys) { measure.SetAnnotation(key, null); annotations++; }
            }
        }
        const string label = "Remove old meaning annotations. Undo restores them. Description stays the same.";
        // The authorized save includes this migration. It must not request a separate model-edit approval.
        if (beforeSave) await session.MutateBeforeSaveAsync("system", label, Remove).ConfigureAwait(false);
        else await session.MutateAsync(origin, label, Remove).ConfigureAwait(false);
        return new(measures, annotations, $"Removed {annotations} old meaning annotations from {measures} measures. Undo restores them. Description stays the same.");
    }
}
