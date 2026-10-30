using System.ComponentModel;
using System.Threading.Tasks;
using ModelContextProtocol.Server;

namespace Semanticus.Engine;

public static partial class McpTools
{
    [McpServerTool(Name = "plan_measure_run"), Description("Show which measures will run, with a reason per measure and an estimated time. This runs no checks. Choose measures, a folder, a table, or changed measures in request.selection. With no selection, use the rule order and a cap of 30 (adjustable from 25 to 40). All changed measures stay in capped batches. Performance is off unless requested.")]
    public static Task<MeasureRunPlan> PlanMeasureRun(IEngine engine,
        [Description("Optional selection, cap, includePerformance, and performanceMeasures for this run.")] MeasureRunRequest request = null)
        => engine.PlanMeasureRunAsync(request);

    [McpServerTool(Name = "run_measure_plan"), Description("Run a plan already returned by plan_measure_run. Cheap checks cover all measures. Contexts, SQL and trusted numbers cover the selection. Returns cached marks, last checked times and their input fingerprints. An edit cancels this run.")]
    public static Task<MeasureRunResult> RunMeasurePlan(IEngine engine, [Description("The id from the displayed run plan.")] string planId)
        => engine.RunMeasurePlanAsync(planId);

    [McpServerTool(Name = "sweep_measure_checks"), Description("Check the next small batch outside the last run, never checked or oldest first. Stop on an edit. Performance runs only when the model's sweep allows it.")]
    public static Task<MeasureRunResult> SweepMeasureChecks(IEngine engine,
        [Description("True to run now. False to wait until the model has been idle for its configured delay.")] bool onRequest = true)
        => engine.SweepMeasureChecksAsync(onRequest);

    [McpServerTool(Name = "configure_measure_sweep"), Description("Set this model session's idle sweep. Defaults: enabled, five idle minutes, three measures per batch, Performance off. Both doors share these settings.")]
    public static Task<MeasureSweepOptions> ConfigureMeasureSweep(IEngine engine,
        [Description("enabled, idleMinutes, batchSize (1 to 10), and allowPerformance.")] MeasureSweepOptions options)
        => engine.ConfigureMeasureSweepAsync(options);

    [McpServerTool(Name = "stop_measure_run"), Description("Stop the check run in progress (a run_measure_plan or a sweep). Measures already checked keep their new marks; the rest keep their last marks. Returns false when nothing is running.")]
    public static Task<bool> StopMeasureRun(IEngine engine) => engine.StopMeasureRunAsync();

    [McpServerTool(Name = "get_measure_check_cache"), Description("Read each measure's check state: its marks with applies, verdict and pending for each, whether it is running, its Keep, last checked time, measured duration, input fingerprint and stale state. Every surface and this tool read the same state. Dependency edits make earlier results stale. Reading this runs no checks.")]
    public static Task<MeasureCheckCache[]> GetMeasureCheckCache(IEngine engine) => engine.GetMeasureCheckCacheAsync();
}
