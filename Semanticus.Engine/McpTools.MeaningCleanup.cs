using System.ComponentModel;
using System.Threading.Tasks;
using ModelContextProtocol.Server;

namespace Semanticus.Engine;

public static partial class McpTools
{
    [McpServerTool(Name = "cleanup_meaning_annotations"), Description("Remove the two old meaning annotations from every measure now. This is one undoable edit. Undo restores the annotations. Description stays the same. The next save also removes these annotations automatically.")]
    public static Task<MeaningAnnotationCleanup> CleanupMeaningAnnotations(IEngine engine) =>
        engine.CleanupMeaningAnnotationsAsync("agent");
}
