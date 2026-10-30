using System;
using System.Threading;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace Semanticus.Engine;

public sealed partial class LocalEngine
{
    // Automatic captures use the same capture/compare implementation, with their own short-lived store.
    // They cannot consume or evict the person's eight manual captures.
    private readonly AsyncLocal<MarkBaselineScope> markBaselineScope = new();

    private sealed class MarkComparisonBatch
    {
        internal Session Session;
        internal string Root;
        internal int Pending;
        internal readonly List<VerifiedEditRecord> Records = new();
    }

    private async Task FinishMarkComparisonAsync(MarkRun run)
    {
        var batch = run.ComparisonBatch;
        if (batch == null) return;
        VerifiedEditRecord[] records;
        lock (marksLock)
        {
            if (--batch.Pending != 0) return;
            records = batch.Records.ToArray();
        }
        if (records.Length == 0) return;
        var evidence = records.Select(r => JsonSerializer.Deserialize<JsonElement>(r.Evidence)).ToArray();
        int Sum(string name) => evidence.Sum(e => e.GetProperty(name).GetInt32());
        var count = Sum("measures"); var moved = Sum("moved"); var missing = Sum("missing");
        var safe = records.All(r => r.Verdict == "safe");
        await RecordAnalysisAsync(batch.Session, new VerifiedEditRecord {
            SessionId = batch.Session.Id, Origin = records[0].Origin, Op = "compare_baseline", ObjectRef = batch.Root,
            Verdict = safe ? "safe" : "impact",
            Summary = records.Length == 1 ? records[0].Summary : safe
                ? $"SAFE: {count} downstream measure(s) unchanged across the captured grid."
                : $"{count} measures compared: {moved} moved, {missing} missing. Review the comparison details.",
            Evidence = JsonSerializer.Serialize(new { measures = count, moved, missing, unclean = Sum("unclean"),
                truncated = evidence.Any(e => e.GetProperty("truncated").GetBoolean()),
                comparisons = evidence, diffs = evidence.SelectMany(e => e.GetProperty("diffs").EnumerateArray()).ToArray() })
        }).ConfigureAwait(false);
    }

    private BaselineStore BaselinesFor(SessionContext context)
        => markBaselineScope.Value?.Context == context ? markBaselineScope.Value.Store : context.Baselines;

    private sealed class MarkBaselineScope : IDisposable
    {
        private readonly LocalEngine engine;
        private readonly MarkBaselineScope parent;
        internal readonly SessionContext Context;
        internal readonly MarkComparisonBatch Batch;
        internal readonly BaselineStore Store = new();
        internal MarkBaselineScope(LocalEngine engine, SessionContext context, MarkComparisonBatch batch = null)
        {
            this.engine = engine; Context = context; Batch = batch;
            parent = engine.markBaselineScope.Value;
            engine.markBaselineScope.Value = this;
        }
        public void Dispose() => engine.markBaselineScope.Value = parent;
    }
}
