using TabularEditor.TOMWrapper;
using TOM = Microsoft.AnalysisServices.Tabular;

namespace TabularEditor.TOMWrapper.Utils
{
    /// <summary>
    /// Per-object TMDL scripting. Lives in Semanticus.Core (compiled alongside the TOMWrapper sources), so —
    /// like <see cref="Scripter"/> — it can reach the wrapper's <c>protected internal</c> MetadataObject, which
    /// the engine/app assemblies cannot. TMDL is the model's native on-disk format and is far more readable than
    /// TMSL JSON, so it's the preferred "Script ▸" output. Powers script_objects' 'tmdl' format.
    /// </summary>
    public static class TmdlScripter
    {
        public static string ScriptTmdl(TabularNamedObject obj)
            => TOM.TmdlSerializer.SerializeObject(obj.MetadataObject);
    }
}
