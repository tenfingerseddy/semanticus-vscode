using System.Threading.Tasks;

namespace Semanticus.Engine;

public sealed partial class EngineRpcTarget
{
    public Task<MeasureRunPlan> planMeasureRun(MeasureRunRequest request = null) => _engine.PlanMeasureRunAsync(request);
    public Task<MeasureRunResult> runMeasurePlan(string planId) => _engine.RunMeasurePlanAsync(planId);
    public Task<MeasureRunResult> sweepMeasureChecks(bool onRequest = true) => _engine.SweepMeasureChecksAsync(onRequest);
    public Task<MeasureSweepOptions> configureMeasureSweep(MeasureSweepOptions options) => _engine.ConfigureMeasureSweepAsync(options);
    public Task<MeasureCheckCache[]> getMeasureCheckCache() => _engine.GetMeasureCheckCacheAsync();
    public Task<bool> stopMeasureRun() => _engine.StopMeasureRunAsync();
}
