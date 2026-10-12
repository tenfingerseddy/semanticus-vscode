using System;
using TabularEditor.TOMWrapper;
using TOM = Microsoft.AnalysisServices.Tabular;

namespace TabularEditor.TOMWrapper.Utils
{
    /// <summary>Restore a full file model on the existing undo timeline. Recovery never writes its source file.</summary>
    public static class ModelRecovery
    {
        public static void Apply(Model model, string json)
        {
            var target = model.Handler.Database;
            var before = target.Clone();
            var after = TOM.JsonSerializer.DeserializeDatabase(json, mode: target.CompatibilityMode);
            // Register before the copy, so the caller's batch can also roll back a failed copy.
            SidecarUndo.Register(model, "Restore unsaved changes", () => Put(before), () => Put(after));
            Put(after);

            void Put(TOM.Database source)
            {
                TabularModelHandler.Singleton = model.Handler;
                var graph = target.Model;
                source.CopyTo(target);
                // Keep the wrapper's root object if TOM replaced it while copying database metadata.
                if (!ReferenceEquals(graph, target.Model))
                {
                    target.Model = graph;
                    source.Model.CopyTo(graph);
                }
                DependencyMaintenance.Reload(model);
            }
        }
    }
}
