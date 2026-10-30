using System;
using System.Threading.Tasks;

namespace Semanticus.Engine;

// This source uses the existing benchmark and profile primitives. Tests supply measured samples.
internal interface IMeasureTimingSource
{
    Task<BenchmarkResult> WarmAsync(string query, int runs);
    async Task<BenchmarkResult> WarmAsync(string query, int runs, Action<int> completed)
    {
        var result = await WarmAsync(query, runs).ConfigureAwait(false);
        completed(result.RunsMs.Length);
        return result;
    }
    Task<ColdWarmBenchmark> ColdAsync(string query);
    Task<ServerTimings> ProfileAsync(string query);
}

public sealed class MeasurePerformanceSettings
{
    public double SlowThresholdMs { get; set; } = 3000;
    public double RegressionPercent { get; set; } = 30;
    public double RegressionMs { get; set; } = 200;
    public bool AllowBackground { get; set; }
    public bool Shared { get; set; }
}

public sealed class MeasureTimingDetail
{
    public string Context { get; set; }
    public double MedianMs { get; set; }
    public long MinMs { get; set; }
    public long MaxMs { get; set; }
    public long SpreadMs => MaxMs - MinMs;
    public long[] WarmRunsMs { get; set; } = Array.Empty<long>();
    public long? ColdMs { get; set; }
    public string ColdNote { get; set; }
    public double? BeforeMedianMs { get; set; }
    public long[] BeforeRunsMs { get; set; }
    public double NoiseBandMs { get; set; }
    public bool Regression { get; set; }
    public long? FormulaEngineMs { get; set; }
    public long? StorageEngineMs { get; set; }
    public string ProfileNote { get; set; }
}

public sealed partial class LocalEngine
{
    internal IMeasureTimingSource MeasureTimingSourceForTests { get; set; }
    internal MeasureContexts.IMeasureContextPlanner MeasureContextPlannerForTests { get; set; }
}
