using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.Entitlement;

namespace Semanticus.PerfSmoke
{
    internal static class Program
    {
        private const int SchemaVersion = 1;
        private static readonly JsonSerializerOptions Json = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            WriteIndented = true,
        };

        private sealed class FreeEntitlement : IEntitlement
        {
            public bool IsPro => false;
            public EntitlementInfo Info { get; } = new EntitlementInfo { Tier = "free", Reason = "Performance fixture." };
        }

        private sealed class Config
        {
            public int Samples { get; set; } = 50;
            public int Warmups { get; set; } = 5;
            public int Cycles { get; set; } = 50;
            public string Fixture { get; set; }
            public string Output { get; set; }
            public string Baseline { get; set; }
        }

        private sealed class EnvironmentInfo
        {
            public string Os { get; set; }
            public string Architecture { get; set; }
            public string Framework { get; set; }
            public int ProcessorCount { get; set; }
            public string MachineId { get; set; }
        }

        private sealed class Metric
        {
            public string Name { get; set; }
            public int Samples { get; set; }
            public double P50Ms { get; set; }
            public double P95Ms { get; set; }
            public double MaxMs { get; set; }
            public int TimeoutMs { get; set; }
            public bool UnderTimeout { get; set; }
            public double[] RunsMs { get; set; } = Array.Empty<double>();
        }

        private sealed class ResourcePoint
        {
            public int Cycle { get; set; }
            public long ManagedBytes { get; set; }
            public long WorkingSetBytes { get; set; }
            public int Threads { get; set; }
            public int? Handles { get; set; }
        }

        private sealed class LifecycleResult
        {
            public int Cycles { get; set; }
            public ResourcePoint[] Points { get; set; } = Array.Empty<ResourcePoint>();
            public long ManagedGrowthBytes { get; set; }
            public long WorkingSetGrowthBytes { get; set; }
            public int ThreadGrowth { get; set; }
            public int? HandleGrowth { get; set; }
            public bool Bounded { get; set; }
            public string[] Failures { get; set; } = Array.Empty<string>();
        }

        private sealed class Report
        {
            public int SchemaVersion { get; set; }
            public string CapturedUtc { get; set; }
            public EnvironmentInfo Environment { get; set; }
            public string FixtureName { get; set; }
            public string FixtureSha256 { get; set; }
            public int Samples { get; set; }
            public int Warmups { get; set; }
            public Metric[] Metrics { get; set; } = Array.Empty<Metric>();
            public LifecycleResult Lifecycle { get; set; }
            public string ComparedTo { get; set; }
            public bool ComparableBaseline { get; set; }
            public bool Passed { get; set; }
            public string[] Failures { get; set; } = Array.Empty<string>();
        }

        public static async Task<int> Main(string[] args)
        {
            try
            {
                var cfg = Parse(args);
                var source = cfg.Fixture ?? Path.Combine(AppContext.BaseDirectory, "TestData", "AdventureWorks.bim");
                if (!File.Exists(source)) throw new FileNotFoundException("Performance fixture was not found.", source);

                var scratch = Path.Combine(Path.GetTempPath(), "semanticus-perf-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(scratch);
                var fixture = Path.Combine(scratch, Path.GetFileName(source));
                File.Copy(source, fixture);
                try
                {
                    var report = await RunAsync(cfg, fixture, source);
                    var payload = JsonSerializer.Serialize(report, Json);
                    if (!string.IsNullOrWhiteSpace(cfg.Output))
                    {
                        var output = Path.GetFullPath(cfg.Output);
                        Directory.CreateDirectory(Path.GetDirectoryName(output) ?? ".");
                        File.WriteAllText(output, payload + Environment.NewLine);
                        await Console.Out.WriteLineAsync("[i] report: " + output);
                    }
                    await Console.Out.WriteLineAsync(payload);
                    return report.Passed ? 0 : 1;
                }
                finally { try { Directory.Delete(scratch, true); } catch { } }
            }
            catch (Exception ex)
            {
                await Console.Error.WriteLineAsync("[FAIL] " + ex.Message);
                return 2;
            }
        }

        private static async Task<Report> RunAsync(Config cfg, string fixture, string source)
        {
            var failures = new List<string>();
            var metrics = new List<Metric>();
            using (var sessions = new SessionManager())
            using (var engine = new LocalEngine(sessions, new FreeEntitlement(), Path.GetDirectoryName(fixture)))
            {
                await engine.OpenAsync(fixture);
                metrics.Add(await MeasureAsync("model_open", cfg, 30_000, 1, async () =>
                    JsonSerializer.Serialize(await engine.OpenAsync(fixture))));
                metrics.Add(await MeasureAsync("orientation_payload", cfg, 10_000, 5, async () =>
                    JsonSerializer.Serialize(await engine.GetOrientationAsync())));
                metrics.Add(await MeasureAsync("tests_payload", cfg, 600_000, 10, async () =>
                    JsonSerializer.Serialize(await engine.RunTestSuiteAsync(false, "benchmark"))));
                metrics.Add(await MeasureAsync("dax_verification_payload", cfg, 30_000, 1, () =>
                {
                    string payload = null;
                    for (var i = 0; i < 100; i++) payload = JsonSerializer.Serialize(RepresentativeEquivalencePayload());
                    return Task.FromResult(payload);
                }));
            }

            foreach (var metric in metrics.Where(x => !x.UnderTimeout))
                failures.Add($"{metric.Name} exceeded its declared {metric.TimeoutMs} ms timeout (max {metric.MaxMs:F2} ms).");

            var lifecycle = await ExerciseLifecycleAsync(cfg.Cycles, fixture);
            failures.AddRange(lifecycle.Failures);
            var report = new Report
            {
                SchemaVersion = SchemaVersion,
                CapturedUtc = DateTimeOffset.UtcNow.ToString("O"),
                Environment = CurrentEnvironment(),
                FixtureName = Path.GetFileName(source),
                FixtureSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source))),
                Samples = cfg.Samples,
                Warmups = cfg.Warmups,
                Metrics = metrics.ToArray(),
                Lifecycle = lifecycle,
                ComparedTo = string.IsNullOrWhiteSpace(cfg.Baseline) ? null : Path.GetFullPath(cfg.Baseline),
            };

            if (!string.IsNullOrWhiteSpace(cfg.Baseline)) CompareBaseline(report, cfg.Baseline, failures);
            report.Passed = failures.Count == 0;
            report.Failures = failures.ToArray();
            return report;
        }

        private static async Task<Metric> MeasureAsync(string name, Config cfg, int timeoutMs, int observationsPerSample,
            Func<Task<string>> action)
        {
            for (var i = 0; i < cfg.Warmups; i++) await action();
            var runs = new List<double>();
            for (var i = 0; i < cfg.Samples; i++)
            {
                for (var j = 0; j < observationsPerSample; j++)
                {
                    var sw = Stopwatch.StartNew();
                    var payload = await action();
                    sw.Stop();
                    GC.KeepAlive(payload);
                    runs.Add(sw.Elapsed.TotalMilliseconds);
                }
            }
            runs.Sort();
            return new Metric
            {
                Name = name,
                Samples = runs.Count,
                P50Ms = Percentile(runs, 0.50),
                P95Ms = Percentile(runs, 0.95),
                MaxMs = runs[^1],
                TimeoutMs = timeoutMs,
                UnderTimeout = runs[^1] <= timeoutMs,
                RunsMs = runs.ToArray(),
            };
        }

        private static async Task<LifecycleResult> ExerciseLifecycleAsync(int cycles, string fixture)
        {
            // Unrecorded cycles absorb JIT/static caches. Comparing half-window medians keeps a GC or OS spike
            // from masquerading as an unbounded trend while still catching rooted sessions and workers.
            var pipe = "semanticus-perf-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            using var ownerSessions = new SessionManager();
            using var owner = new LocalEngine(ownerSessions, new FreeEntitlement(), Path.GetDirectoryName(fixture));
            await owner.OpenAsync(fixture);
            using var server = new RpcServer(ownerSessions, owner, pipe);
            using var stop = new CancellationTokenSource();
            var serverTask = server.RunAsync(stop.Token);
            try
            {
                for (var i = 0; i < 5; i++) await LifecycleCycleAsync(fixture, pipe);
                var points = new List<ResourcePoint>();
                for (var i = 0; i < cycles; i++)
                {
                    await LifecycleCycleAsync(fixture, pipe);
                    ForceGc();
                    points.Add(Capture(i + 1));
                }

                var split = Math.Max(1, points.Count / 2);
                var first = points.Take(split).ToArray();
                var last = points.Skip(points.Count - split).ToArray();
                var managed = Median(last.Select(x => x.ManagedBytes)) - Median(first.Select(x => x.ManagedBytes));
                var working = Median(last.Select(x => x.WorkingSetBytes)) - Median(first.Select(x => x.WorkingSetBytes));
                var threads = (int)(Median(last.Select(x => (long)x.Threads)) - Median(first.Select(x => (long)x.Threads)));
                int? handles = points.All(x => x.Handles.HasValue)
                    ? (int)(Median(last.Select(x => (long)x.Handles.Value)) - Median(first.Select(x => (long)x.Handles.Value)))
                    : null;

                var failures = new List<string>();
                if (managed > 32L * 1024 * 1024) failures.Add($"Managed memory grew {managed / 1024d / 1024d:F1} MiB across repeated lifecycle cycles.");
                if (working > 128L * 1024 * 1024) failures.Add($"Working set grew {working / 1024d / 1024d:F1} MiB across repeated lifecycle cycles.");
                if (threads > 8) failures.Add($"Thread count grew by {threads} across repeated lifecycle cycles.");
                if (handles > 32) failures.Add($"Handle count grew by {handles} across repeated lifecycle cycles.");
                return new LifecycleResult
                {
                    Cycles = cycles,
                    Points = points.ToArray(),
                    ManagedGrowthBytes = managed,
                    WorkingSetGrowthBytes = working,
                    ThreadGrowth = threads,
                    HandleGrowth = handles,
                    Bounded = failures.Count == 0,
                    Failures = failures.ToArray(),
                };
            }
            finally
            {
                await stop.CancelAsync();
                try { await serverTask; } catch (OperationCanceledException) { }
            }
        }

        private static async Task LifecycleCycleAsync(string fixture, string pipe)
        {
            using (var sessions = new SessionManager())
            using (var engine = new LocalEngine(sessions, new FreeEntitlement(), Path.GetDirectoryName(fixture)))
            {
                await engine.OpenAsync(fixture);
                GC.KeepAlive(await engine.GetOrientationAsync());
                GC.KeepAlive(await engine.RunTestSuiteAsync(false, "benchmark"));
                await engine.DisconnectAsync();
            }
            using var remote = await RemoteEngine.ConnectAsync(pipe, timeoutMs: 5000);
            GC.KeepAlive(await remote.GetOrientationAsync());
        }

        private static EquivalenceResult RepresentativeEquivalencePayload() => new EquivalenceResult
        {
            AllMatch = false,
            RowsCompared = 100_000,
            MismatchCount = 50,
            Truncated = true,
            Query = string.Join("\n", Enumerable.Repeat("EVALUATE SUMMARIZECOLUMNS('Date'[Year], 'Product'[Category], \"__A\", [A], \"__B\", [B])", 64)),
            Mismatches = Enumerable.Range(1, 50).Select(i => new EquivalenceMismatch
            {
                Context = $"'Date'[Year]=202{i % 10}, 'Product'[Category]=Category {i}",
                ValueA = (i * 1000.25).ToString(System.Globalization.CultureInfo.InvariantCulture),
                ValueB = (i * 1000.25 + 1).ToString(System.Globalization.CultureInfo.InvariantCulture),
            }).ToArray(),
        };

        private static void CompareBaseline(Report current, string path, List<string> failures)
        {
            var baseline = JsonSerializer.Deserialize<Report>(File.ReadAllText(path), Json)
                ?? throw new InvalidOperationException("The baseline report is empty.");
            var comparable = baseline.SchemaVersion == SchemaVersion
                && string.Equals(baseline.Environment?.Os, current.Environment.Os, StringComparison.Ordinal)
                && string.Equals(baseline.Environment?.Architecture, current.Environment.Architecture, StringComparison.Ordinal)
                && string.Equals(baseline.Environment?.Framework, current.Environment.Framework, StringComparison.Ordinal)
                && baseline.Environment?.ProcessorCount == current.Environment.ProcessorCount
                && string.Equals(baseline.Environment?.MachineId, current.Environment.MachineId, StringComparison.Ordinal)
                && string.Equals(baseline.FixtureSha256, current.FixtureSha256, StringComparison.OrdinalIgnoreCase);
            current.ComparableBaseline = comparable;
            if (!comparable)
            {
                failures.Add("The supplied baseline is not comparable: schema, machine, OS, architecture, processor count, runtime and fixture hash must match.");
                return;
            }
            foreach (var metric in current.Metrics)
            {
                var before = baseline.Metrics.FirstOrDefault(x => string.Equals(x.Name, metric.Name, StringComparison.Ordinal));
                if (before == null) { failures.Add("The baseline is missing metric " + metric.Name + "."); continue; }
                var allowed = before.P95Ms * 1.20;
                if (metric.P95Ms > allowed)
                    failures.Add($"{metric.Name} p95 regressed from {before.P95Ms:F2} ms to {metric.P95Ms:F2} ms (limit {allowed:F2} ms).");
            }
        }

        private static EnvironmentInfo CurrentEnvironment() => new EnvironmentInfo
        {
            Os = RuntimeInformation.OSDescription,
            Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            Framework = RuntimeInformation.FrameworkDescription,
            ProcessorCount = Environment.ProcessorCount,
            MachineId = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Environment.MachineName))).Substring(0, 12),
        };

        private static ResourcePoint Capture(int cycle)
        {
            using var process = Process.GetCurrentProcess();
            process.Refresh();
            int? handles = null;
            try { handles = process.HandleCount; } catch { }
            return new ResourcePoint
            {
                Cycle = cycle,
                ManagedBytes = GC.GetTotalMemory(false),
                WorkingSetBytes = process.WorkingSet64,
                Threads = process.Threads.Count,
                Handles = handles,
            };
        }

        private static void ForceGc()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        private static double Percentile(IReadOnlyList<double> sorted, double percentile)
        {
            var index = (int)Math.Ceiling(percentile * sorted.Count) - 1;
            return sorted[Math.Clamp(index, 0, sorted.Count - 1)];
        }

        private static long Median(IEnumerable<long> values)
        {
            var ordered = values.OrderBy(x => x).ToArray();
            return ordered[ordered.Length / 2];
        }

        private static Config Parse(string[] args)
        {
            var cfg = new Config();
            for (var i = 0; i < args.Length; i++)
            {
                string NeedValue() => i + 1 < args.Length ? args[++i] : throw new ArgumentException("Missing value for " + args[i] + ".");
                switch (args[i])
                {
                    case "--samples": cfg.Samples = int.Parse(NeedValue()); break;
                    case "--warmups": cfg.Warmups = int.Parse(NeedValue()); break;
                    case "--cycles": cfg.Cycles = int.Parse(NeedValue()); break;
                    case "--fixture": cfg.Fixture = Path.GetFullPath(NeedValue()); break;
                    case "--output": cfg.Output = NeedValue(); break;
                    case "--baseline": cfg.Baseline = NeedValue(); break;
                    default: throw new ArgumentException("Unknown argument: " + args[i]);
                }
            }
            if (cfg.Samples < 20) throw new ArgumentOutOfRangeException(nameof(cfg.Samples), "At least 20 samples are required for a p95 gate.");
            if (cfg.Warmups < 1) throw new ArgumentOutOfRangeException(nameof(cfg.Warmups), "At least one warmup is required.");
            if (cfg.Cycles < 30) throw new ArgumentOutOfRangeException(nameof(cfg.Cycles), "At least 30 lifecycle cycles are required.");
            return cfg;
        }
    }
}
