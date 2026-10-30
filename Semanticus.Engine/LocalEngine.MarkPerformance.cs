using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;

namespace Semanticus.Engine;

public sealed partial class LocalEngine
{
    private const int QuickTimingContexts = 3;
    private const long QuickTimingBudgetMs = 2500;

    private sealed record MarkTimingBaseline(LiveConnection Live, MeasureMarkSnapshot Snapshot, string Reference,
        MarkContextQuery[] Queries, MeasureTimingDetail[] Timings);

    private sealed class LiveMeasureTimingSource : IMeasureTimingSource
    {
        private readonly LiveConnection live;
        internal LiveMeasureTimingSource(LiveConnection live) => this.live = live;
        public Task<BenchmarkResult> WarmAsync(string query, int runs) => DaxBench.BenchmarkAsync(live, query, runs);
        public Task<BenchmarkResult> WarmAsync(string query, int runs, Action<int> completed) => DaxBench.BenchmarkAsync(live, query, runs, completed);
        public async Task<ColdWarmBenchmark> ColdAsync(string query)
        {
            var clear = await DaxCache.ClearAsync(live).ConfigureAwait(false);
            if (!clear.Cleared) return new ColdWarmBenchmark { CacheClearAvailable = false, Note = clear.Note ?? clear.Error };
            var result = await live.ExecuteAsync(query, 1, 120).ConfigureAwait(false);
            return new ColdWarmBenchmark { CacheClearAvailable = string.IsNullOrEmpty(result.Error), Error = result.Error,
                ColdTotal = new ColdWarmStats { N = 1, RunsMs = new[] { result.ElapsedMs } },
                Detail = new[] { new ColdWarmRun { Index = 1, Cold = true, TotalMs = result.ElapsedMs } } };
        }
        public Task<ServerTimings> ProfileAsync(string query) => DaxTrace.ProfileAsync(live, query);
    }

    private static MeasureTimingDetail CopyTiming(MeasureTimingDetail t) => new()
    {
        Context = MeasureMarkJudge.Place(t.Context), MedianMs = t.MedianMs, MinMs = t.MinMs, MaxMs = t.MaxMs,
        WarmRunsMs = t.WarmRunsMs.ToArray(), ColdMs = t.ColdMs, ColdNote = t.ColdNote,
        BeforeMedianMs = t.BeforeMedianMs, BeforeRunsMs = t.BeforeRunsMs?.ToArray(),
        NoiseBandMs = t.NoiseBandMs, Regression = t.Regression,
        FormulaEngineMs = t.FormulaEngineMs, StorageEngineMs = t.StorageEngineMs, ProfileNote = t.ProfileNote
    };

    private static MeasureTimingDetail WarmDetail(string context, BenchmarkResult result)
    {
        if (result == null || !string.IsNullOrEmpty(result.Error))
            throw new InvalidOperationException(result?.Error ?? "The benchmark returned no timings.");
        var warm = result.RunsMs.Skip(1).ToArray();
        if (warm.Length < 3 || warm.Any(t => t < 0))
            throw new InvalidOperationException("The benchmark did not return three warm samples.");
        var sorted = warm.OrderBy(t => t).ToArray();
        return new MeasureTimingDetail
        {
            Context = context, WarmRunsMs = warm, MinMs = sorted[0], MaxMs = sorted[^1],
            MedianMs = sorted.Length % 2 == 1 ? sorted[sorted.Length / 2]
                : sorted[sorted.Length / 2 - 1] / 2.0 + sorted[sorted.Length / 2] / 2.0
        };
    }

    private async Task TimeMeasureMarksAsync(MarkRun run)
    {
        if (run.Live == null || !MarkRunCurrent(run))
        { SetMark(run, Coded(MeasureMarkJudge.NotRun("Performance", "Connect a model to time this measure."), "not_connected")); return; }
        if (run.CannotCompare != null || run.After.HasCalcGroups)
        { SetMark(run, MeasureMarkJudge.NotRun("Performance", run.CannotCompare ?? "Timing is unavailable for calculation groups.")); return; }
        if (run.After.MissingReason(run.Reference) is { } missing)
        { SetMark(run, MeasureMarkJudge.NotRun("Performance", missing)); return; }
        var source = MeasureTimingSourceForTests ?? new LiveMeasureTimingSource(run.Live);
        var settings = run.After.PerformanceSettings;
        var queries = (run.BeforeTiming?.Queries ?? run.ContextQueries)
            .OrderBy(q => q.Id == "total" ? 0 : 1).ToArray();
        var selected = run.FullPerformanceSweep ? queries : queries.Take(QuickTimingContexts).ToArray();
        var details = new List<MeasureTimingDetail>(); var completed = new List<MarkContextQuery>();
        const int warmRuns = 6; // One warm-up, then five measured warm runs.
        var expectedRuns = selected.Length * (warmRuns * (run.BeforeTiming == null ? 1 : 2) + (settings.Shared ? 0 : 1));
        var runsDone = 0;
        var elapsed = Stopwatch.StartNew();
        void Progress(string phase)
        {
            if (!MarkRunCurrent(run)) return;
            var remaining = runsDone == 0 ? selected.Length * 10
                : Math.Max(1, (int)Math.Ceiling(elapsed.Elapsed.TotalSeconds / runsDone * (expectedRuns - runsDone)));
            var amount = remaining < 60 ? remaining : (int)Math.Ceiling(remaining / 60.0);
            var expected = amount + (remaining < 60 ? " second" : " minute") + (amount == 1 ? "" : "s");
            // No run counts and no Cold or Warm words reach a door (spec D4, fx30e F3): the time left is the whole line.
            SetMark(run, Timing("Timing. About " + expected + "."));
        }
        async Task<BenchmarkResult> Warm(string query, string phase)
        {
            var start = runsDone;
            return await source.WarmAsync(query, warmRuns, done => {
                runsDone = start + done;
                Progress(phase);
            }).ConfigureAwait(false);
        }
        long spent = 0;
        try
        {
            Progress("Preparing the warm runs");
            foreach (var query in selected)
            {
                if (!MarkRunCurrent(run)) return;
                MeasureTimingDetail before = null;
                var afterQuery = query.Query;
                if (run.BeforeTiming != null)
                {
                    var baseline = run.BeforeTiming;
                    before = WarmDetail(query.Context, await Warm(baseline.Snapshot.Apply(query.Query, baseline.Reference), "Before: " + MeasureMarkJudge.Place(query.Context)).ConfigureAwait(false));
                    var oldName = baseline.Snapshot.Formulas[baseline.Reference].Name;
                    var newName = run.After.Formulas[run.Reference].Name;
                    if (oldName != newName) afterQuery = afterQuery.Replace(Baseline.MeasureRefExpr(oldName), Baseline.MeasureRefExpr(newName), StringComparison.Ordinal);
                    spent += before.WarmRunsMs.Sum();
                }
                var measured = await Warm(run.After.Apply(afterQuery, run.Reference), "Warm: " + MeasureMarkJudge.Place(query.Context)).ConfigureAwait(false);
                var detail = WarmDetail(query.Context, measured);
                spent += measured.RunsMs.Sum();
                if (before != null)
                {
                    detail.BeforeMedianMs = before.MedianMs;
                    detail.BeforeRunsMs = before.WarmRunsMs;
                    // The sum of the observed ranges is a conservative empirical noise band.
                    detail.NoiseBandMs = (before.MaxMs - before.MinMs) + (detail.MaxMs - detail.MinMs);
                    var increase = detail.MedianMs - before.MedianMs;
                    detail.Regression = increase > before.MedianMs * settings.RegressionPercent / 100
                        && increase > settings.RegressionMs && increase > detail.NoiseBandMs;
                }
                detail.ProfileNote = "The engine split was not collected for this quick sample.";
                if (settings.Shared) detail.ColdNote = "Cold run skipped because the model is marked shared; its cache was not cleared.";
                else
                {
                    Progress("Cold: " + MeasureMarkJudge.Place(query.Context));
                    var cold = await source.ColdAsync(run.After.Apply(afterQuery, run.Reference)).ConfigureAwait(false);
                    if (cold?.ColdTotal?.RunsMs.Length == 1) runsDone++;
                    else expectedRuns--;
                    Progress("Cold run finished");
                    detail.ColdMs = cold?.CacheClearAvailable == true && string.IsNullOrEmpty(cold.Error)
                        && cold.ColdTotal.RunsMs.Length == 1 ? cold.ColdTotal.RunsMs[0] : null;
                    detail.ColdNote = detail.ColdMs.HasValue ? "One cold run cleared this model's cache." : cold?.Error ?? cold?.Note ?? "Cold timing was unavailable.";
                    spent += cold?.Detail.Sum(d => d.TotalMs) ?? 0;
                }
                details.Add(detail); completed.Add(query with { Query = afterQuery });
                if (!run.FullPerformanceSweep && spent >= QuickTimingBudgetMs) break;
            }
            if (details.Count == 0)
            { SetMark(run, MeasureMarkJudge.NotRun("Performance", "The chosen contexts could not be timed.")); return; }
            var slowest = details.OrderByDescending(d => d.MedianMs).First();
            // Trace only the slowest slow context. A trace session itself can cost seconds.
            if (slowest.MedianMs > settings.SlowThresholdMs || details.Any(d => d.Regression) || run.FullPerformanceSweep)
            {
                Progress("Reading the server timings");
                var i = details.IndexOf(slowest);
                try
                {
                    var profile = await source.ProfileAsync(run.After.Apply(completed[i].Query, run.Reference)).ConfigureAwait(false);
                    if (profile?.TraceAvailable == true && string.IsNullOrEmpty(profile.Error))
                    { slowest.FormulaEngineMs = profile.FeMs; slowest.StorageEngineMs = profile.SeMs; }
                    slowest.ProfileNote = profile?.Error ?? profile?.Note ?? (profile?.TraceAvailable == true ? null : "Server engine timings were unavailable.");
                }
                catch (Exception ex) { slowest.ProfileNote = ex.Message; }
            }
            var slow = slowest.MedianMs > settings.SlowThresholdMs;
            var regression = details.Any(d => d.Regression);
            string Ms(double n) => n.ToString("0.##", CultureInfo.InvariantCulture) + " ms";
            // Spec 4.4: "Slowest: <place>, <ms>. Limit <s>." or "Over the <s> limit." (cut 30: the threshold, the count, the sweep).
            var regressed = details.FirstOrDefault(d => d.Regression);
            var sentence = regressed != null
                ? "Slower after your edit: " + Ms(regressed.BeforeMedianMs ?? 0) + " to " + Ms(regressed.MedianMs) + "."
                : "Slowest: " + slowest.Context + ", " + Ms(slowest.MedianMs) + ". "
                    + (slow ? "Over the " + Ms(settings.SlowThresholdMs) + " limit." : "Limit " + Ms(settings.SlowThresholdMs) + ".");
            SetMark(run, new MeasureMark
            {
                Name = "Performance", State = slow || regression ? "suspect" : "passed", Sentence = sentence,
                Action = "Include performance", LastRan = DateTime.UtcNow.ToString("o"), Timings = details.ToArray(),
                ChosenContexts = completed.Select(q => run.ContextPlan?.Contexts.FirstOrDefault(c => c.Id == q.Id))
                    .Where(c => c != null).ToArray(),
                Cells = details.OrderByDescending(d => d.MedianMs).Select(d => new MeasureMarkCell
                { Context = d.Context, Before = d.BeforeMedianMs.HasValue ? Ms(d.BeforeMedianMs.Value) : null, After = Ms(d.MedianMs) }).ToArray()
            });
            lock (marksLock)
            {
                // A timing Stop or an edit ended is no baseline: a later edit would be compared against it (j29, the fx29a rule).
                if (!MarkRunCurrent(run)) return;
                run.TimingBaseline = new(run.Live, run.After, run.Reference, completed.ToArray(), details.Select(CopyTiming).ToArray());
                if (run.Compare && measureMarks.TryGetValue(run.Reference, out var latest) && latest.Revision == run.Revision)
                    latest.TimingBaseline = run.TimingBaseline;
            }
        }
        catch (Exception ex)
        {
            SetMark(run, new MeasureMark { Name = "Performance", State = "not run", Sentence = "Timing could not finish: " + ex.Message,
                Action = "Include performance", Timings = details.ToArray() });
        }
    }
}
