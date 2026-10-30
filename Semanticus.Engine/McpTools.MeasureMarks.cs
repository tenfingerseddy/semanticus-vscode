using System.ComponentModel;
using System.Threading.Tasks;
using ModelContextProtocol.Server;

namespace Semanticus.Engine;

public static partial class McpTools
{
    [McpServerTool(Name = "set_measure_totals"), Description("Save the adjustable per-type Totals limits, or mark one measure's totals as expected so it stops warning. Distinct count always shows its type without a warning. expected=false removes the per-measure waiver. Settings and waivers persist with the model and support undo.")]
    public static Task<TotalsSettings> SetMeasureTotals(IEngine engine,
        TotalsSettings settings = null, string measureRef = null, bool? expected = null)
        => engine.SetMeasureTotalsAsync(settings, measureRef, expected, "agent");
    [McpServerTool(Name = "set_measure_performance"), Description("Save Performance check limits and shared status, or turn Include performance on or off for one measure. Default limits: 3000 ms slow, regression over 30 percent and 200 ms beyond measured noise. Background timing defaults off. Shared models never clear their cache for this mark. Settings live in the check store and do not edit the model or its Undo history.")]
    public static Task<MeasurePerformanceSettings> SetMeasurePerformance(IEngine engine,
        MeasurePerformanceSettings settings = null, string measureRef = null, bool? includePerformance = null)
        => engine.SetMeasurePerformanceAsync(settings, measureRef, includePerformance, "agent");

    [McpServerTool(Name = "keep_measure_edit"), Description("Keep the person's edit of one measure: they meant the numbers it moved. The Equivalence mark stops applying until the measure or a measure it uses changes again; an Undo back to the kept formulas hides it again. Saved with the check cache, so every window reads it. This does not edit the model.")]
    public static Task<MeasureCheckCache> KeepMeasureEdit(IEngine engine,
        [Description("The measure reference, such as measure:Sales/Total Sales.")] string measureRef)
        => engine.KeepMeasureEditAsync(measureRef, "agent");

    [McpServerTool(Name = "get_measure_marks"), Description("Read the Totals, Filters, Contexts, SQL, Trusted number, Equivalence and Performance marks for one measure. Each is passed, suspect with its numbers, information, or not run. Totals always names the type Jev read and its confidence when available; warnings use measured per-type limits. Totals uses chosen contexts that can split the result. Under version 7 permission, Totals can ask Jev. Totals sends the name, Description and formula to read the total type. For the Fit question, Totals also sends the expected total rule, grouping column, calculated facts, probe rows and total. Each probe row contains a member name and a measure value. Starts missing checks in the background and returns immediately; call again while running is true. Performance defaults to not timed; includePerformance opts in for this run. A timing baseline enables timing comparisons after edits.")]
    public static Task<MeasureMarksResult> GetMeasureMarks(IEngine engine,
        [Description("The measure reference, such as measure:Sales/Total Sales.")] string measureRef,
        [Description("Optional related column, such as 'Product'[Category]. Otherwise the engine chooses a related column.")] string primaryAxis = null,
        [Description("Run the checks again, including saved SQL and trusted numbers.")] bool refresh = false,
        [Description("Include performance for this run. Defaults off. The quick sample times up to three chosen contexts, with the grand total first.")] bool includePerformance = false,
        [Description("Explicitly time all chosen contexts. This can take longer than the quick sample.")] bool fullPerformanceSweep = false)
        => engine.GetMeasureMarksAsync(measureRef, primaryAxis, refresh, "agent", includePerformance, fullPerformanceSweep);
}
