using System.Threading.Tasks;

namespace Semanticus.Engine;

public partial interface IEngine
{
    Task<TotalsSettings> SetMeasureTotalsAsync(TotalsSettings settings = null,
        string measureRef = null, bool? expected = null, string origin = "human");
    Task<MeasurePerformanceSettings> SetMeasurePerformanceAsync(MeasurePerformanceSettings settings = null,
        string measureRef = null, bool? includePerformance = null, string origin = "human");
    Task<MeasureCheckCache> KeepMeasureEditAsync(string measureRef, string origin = "human");
    Task<MeasureMarksResult> GetMeasureMarksAsync(string measureRef, string primaryAxis = null, bool refresh = false, string origin = "human", bool includePerformance = false, bool fullPerformanceSweep = false);
}
