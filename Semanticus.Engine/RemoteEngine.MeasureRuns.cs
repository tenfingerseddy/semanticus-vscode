using System.Threading.Tasks;

namespace Semanticus.Engine;

public sealed partial class RemoteEngine
{
    public Task<MeasureRunPlan> PlanMeasureRunAsync(MeasureRunRequest request = null)
        => _rpc.InvokeAsync<MeasureRunPlan>("planMeasureRun", request ?? new());
    public Task<MeasureRunResult> RunMeasurePlanAsync(string planId) => _rpc.InvokeAsync<MeasureRunResult>("runMeasurePlan", planId);
    public Task<MeasureRunResult> SweepMeasureChecksAsync(bool onRequest = true) => _rpc.InvokeAsync<MeasureRunResult>("sweepMeasureChecks", onRequest);
    public Task<MeasureSweepOptions> ConfigureMeasureSweepAsync(MeasureSweepOptions options) => _rpc.InvokeAsync<MeasureSweepOptions>("configureMeasureSweep", options);
    public Task<MeasureCheckCache[]> GetMeasureCheckCacheAsync() => _rpc.InvokeAsync<MeasureCheckCache[]>("getMeasureCheckCache");
    public Task<bool> StopMeasureRunAsync() => _rpc.InvokeAsync<bool>("stopMeasureRun");
}
