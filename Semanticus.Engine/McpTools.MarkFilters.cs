using System.ComponentModel;
using System.Threading.Tasks;
using ModelContextProtocol.Server;

namespace Semanticus.Engine;

public static partial class McpTools
{
    [McpServerTool(Name = "set_measure_filters_on_purpose"), Description("Say that a measure ignores a filter on purpose, as the person told you. The Filters mark then reads Overridden instead of asking for review. onPurpose=false removes it. Use it only when the person says the lost filter is meant; when it is a bug, fix the formula instead. Stored on the measure, saved with the model and supports undo.")]
    public static Task<bool> SetMeasureFiltersOnPurpose(IEngine engine,
        [Description("The measure reference, such as measure:Sales/Total Sales.")] string measureRef,
        [Description("true when the person says the ignored filter is meant; false to remove it.")] bool onPurpose)
        => engine.SetMeasureFiltersOnPurposeAsync(measureRef, onPurpose, "agent");
}
