using System.Threading.Tasks;

namespace Semanticus.Engine;

public partial interface IEngine
{
    Task<MeasureRunPlan> PlanMeasureRunAsync(MeasureRunRequest request = null);
    Task<MeasureRunResult> RunMeasurePlanAsync(string planId);
    Task<MeasureRunResult> SweepMeasureChecksAsync(bool onRequest = true);
    Task<MeasureSweepOptions> ConfigureMeasureSweepAsync(MeasureSweepOptions options);
    Task<MeasureCheckCache[]> GetMeasureCheckCacheAsync();
    Task<bool> StopMeasureRunAsync();
}
