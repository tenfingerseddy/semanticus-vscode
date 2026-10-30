using System.Threading.Tasks;

namespace Semanticus.Engine;

public sealed partial class RemoteEngine
{
    public Task<TotalsSettings> SetMeasureTotalsAsync(TotalsSettings settings = null,
        string measureRef = null, bool? expected = null, string origin = "human")
        => _rpc.InvokeAsync<TotalsSettings>("setMeasureTotals", settings, measureRef, expected, origin);
    public Task<MeasurePerformanceSettings> SetMeasurePerformanceAsync(MeasurePerformanceSettings settings = null,
        string measureRef = null, bool? includePerformance = null, string origin = "human")
        => _rpc.InvokeAsync<MeasurePerformanceSettings>("setMeasurePerformance", settings, measureRef, includePerformance, origin);
    public Task<MeasureCheckCache> KeepMeasureEditAsync(string measureRef, string origin = "human")
        => _rpc.InvokeAsync<MeasureCheckCache>("keepMeasureEdit", measureRef, origin);
    public Task<MeasureMarksResult> GetMeasureMarksAsync(string measureRef, string primaryAxis = null, bool refresh = false, string origin = "human", bool includePerformance = false, bool fullPerformanceSweep = false)
        => _rpc.InvokeAsync<MeasureMarksResult>("getMeasureMarks", measureRef, primaryAxis, refresh, origin, includePerformance, fullPerformanceSweep);
}
