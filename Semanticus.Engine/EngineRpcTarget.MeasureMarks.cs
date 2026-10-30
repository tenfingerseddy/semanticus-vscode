using System.Threading.Tasks;

namespace Semanticus.Engine;

public sealed partial class EngineRpcTarget
{
    public Task<TotalsSettings> setMeasureTotals(TotalsSettings settings = null,
        string measureRef = null, bool? expected = null, string origin = "human")
        => _engine.SetMeasureTotalsAsync(settings, measureRef, expected, _origin);
    public Task<MeasurePerformanceSettings> setMeasurePerformance(MeasurePerformanceSettings settings = null,
        string measureRef = null, bool? includePerformance = null, string origin = "human")
        => _engine.SetMeasurePerformanceAsync(settings, measureRef, includePerformance, _origin);
    public Task<MeasureCheckCache> keepMeasureEdit(string measureRef, string origin = "human")
        => _engine.KeepMeasureEditAsync(measureRef, _origin);
    public Task<MeasureMarksResult> getMeasureMarks(string measureRef, string primaryAxis = null, bool refresh = false, string origin = "human", bool includePerformance = false, bool fullPerformanceSweep = false)
        => _engine.GetMeasureMarksAsync(measureRef, primaryAxis, refresh, _origin, includePerformance, fullPerformanceSweep);
}
