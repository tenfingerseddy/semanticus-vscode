using System;
using System.IO;
using System.Linq;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// docs/harness-engineering.md §5 — the harness-ergonomics report. Pins the DETERMINISTIC definitions
    /// (retry cluster = >=2 adjacent same-op failures; flail = failures-before-a-success per op; intervention
    /// = agent->human origin transition) and the fail-soft contract (corrupt lines skipped; empty/missing log
    /// yields an empty report with an honest note, never a throw). The analyzer is pure over raw JSONL lines,
    /// so the synthetic fixtures are built inline — no engine/session needed.
    /// </summary>
    public sealed class HarnessReportTests
    {
        private static string Activity(string op, bool ok, string origin = "agent", string error = null, string when = "2026-07-04T00:00:00Z")
            => $"{{\"schemaVersion\":1,\"when\":\"{when}\",\"kind\":\"activity\",\"origin\":\"{origin}\",\"op\":\"{op}\",\"label\":\"{op}\",\"ok\":{(ok ? "true" : "false")},\"error\":{(error == null ? "null" : "\"" + error + "\"")}}}";

        private static string Change(string origin = "agent", string when = "2026-07-04T00:00:00Z")
            => $"{{\"schemaVersion\":1,\"when\":\"{when}\",\"kind\":\"change\",\"origin\":\"{origin}\",\"label\":\"set DAX\",\"revision\":1}}";

        [Fact]
        public void ErrorRatesAndCountsPerOp()
        {
            var lines = new[]
            {
                Activity("run_dax", ok: true),
                Activity("run_dax", ok: false, error: "bad syntax"),
                Activity("run_dax", ok: true),
                Activity("save_model", ok: true),
            };
            var r = HarnessReport.Analyze(lines, topN: 10);

            Assert.Equal(4, r.ActivityRecords);
            var runDax = r.OpStats.Single(o => o.Op == "run_dax");
            Assert.Equal(3, runDax.Count);
            Assert.Equal(1, runDax.Failures);
            Assert.Equal(0.3333, runDax.ErrorRate, 3);   // 1/3
            var save = r.OpStats.Single(o => o.Op == "save_model");
            Assert.Equal(0, save.Failures);
            Assert.Equal(0.0, save.ErrorRate);
        }

        [Fact]
        public void RetryClusterIsTwoOrMoreAdjacentSameOpFailures()
        {
            var lines = new[]
            {
                Activity("validate_dax", ok: false, error: "err A"),
                Activity("validate_dax", ok: false, error: "err B"),
                Activity("validate_dax", ok: false, error: "err A"),   // 3-failure run -> one cluster
                Activity("validate_dax", ok: true),                    // success ends it
                Activity("run_dax", ok: false, error: "x"),            // lone failure -> NOT a cluster (<2)
                Activity("run_dax", ok: true),
            };
            var r = HarnessReport.Analyze(lines, topN: 10);

            var cluster = Assert.Single(r.RetryClusters);
            Assert.Equal("validate_dax", cluster.Op);
            Assert.Equal(3, cluster.Count);
            Assert.Equal(2, cluster.Errors.Length);   // distinct: "err A", "err B"
        }

        [Fact]
        public void FlailRankingCountsFailuresBeforeASuccess()
        {
            var lines = new[]
            {
                // create_measure: 2 fails then success -> 2 failuresBeforeSuccess
                Activity("create_measure", ok: false, error: "e1"),
                Activity("create_measure", ok: false, error: "e2"),
                Activity("create_measure", ok: true),
                // set_dax: 1 fail then success -> 1
                Activity("set_dax", ok: false, error: "e3"),
                Activity("set_dax", ok: true),
                // deploy_live: fails only, never succeeds -> NOT flail (hard error, not flail)
                Activity("deploy_live", ok: false, error: "e4"),
                Activity("deploy_live", ok: false, error: "e4"),
            };
            var r = HarnessReport.Analyze(lines, topN: 10);

            Assert.Equal("create_measure", r.FlailSites.First().Op);
            Assert.Equal(2, r.FlailSites.First().FailuresBeforeSuccess);
            Assert.Contains(r.FlailSites, f => f.Op == "set_dax" && f.FailuresBeforeSuccess == 1);
            Assert.DoesNotContain(r.FlailSites, f => f.Op == "deploy_live");   // trailing fails don't count

            // Error-frequency table: "e4" recurs twice, tops the list.
            Assert.Equal("e4", r.TopErrors.First().Error);
            Assert.Equal(2, r.TopErrors.First().Count);
        }

        [Fact]
        public void KpiLineAndInterventions()
        {
            var lines = new[]
            {
                Activity("run_dax", ok: true, origin: "agent"),
                Activity("run_dax", ok: false, origin: "agent", error: "boom"),
                Change(origin: "human"),                        // human steps in after agent -> 1 intervention
                Activity("save_model", ok: true, origin: "agent"),
            };
            var r = HarnessReport.Analyze(lines, topN: 10);

            Assert.Equal(3, r.Kpis.Events);                     // 3 activity records
            Assert.Equal(0.6667, r.Kpis.SuccessRate.Value, 3);  // 2 ok / 3
            Assert.Equal(33.33, r.Kpis.FlailRatePer100, 2);     // 1 fail / 3 * 100
            Assert.Equal(1, r.Kpis.Interventions);
            Assert.Contains("not derivable", r.Kpis.TokensPerOutcome);   // tokens honestly not derivable from L0 v1
            Assert.Contains("interventions 1", r.Kpis.Summary);
        }

        [Fact]
        public void CorruptLinesSkippedFailSoft()
        {
            var lines = new[]
            {
                Activity("run_dax", ok: true),
                "{ this is not valid json",
                "",                                  // blank lines ignored (not counted as corrupt)
                Activity("run_dax", ok: false, error: "e"),
                "}{ also broken",
            };
            var r = HarnessReport.Analyze(lines, topN: 10);

            Assert.Equal(2, r.SkippedLines);
            Assert.Equal(2, r.ActivityRecords);
            Assert.Contains("skipped", r.Note, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void EmptyAndMissingLogGiveEmptyReportWithNoteNeverThrows()
        {
            var empty = HarnessReport.Analyze(Array.Empty<string>(), topN: 10);
            Assert.Equal(0, empty.TotalRecords);
            Assert.False(string.IsNullOrEmpty(empty.Note));
            Assert.Empty(empty.OpStats);

            var missing = HarnessReport.FromFile(Path.Combine(Path.GetTempPath(), "smx-no-such-" + Guid.NewGuid().ToString("N") + ".jsonl"), topN: 10);
            Assert.Equal(0, missing.TotalRecords);
            Assert.False(string.IsNullOrEmpty(missing.Note));

            var nullPath = HarnessReport.FromFile(null, topN: 10);
            Assert.Equal(0, nullPath.TotalRecords);
            Assert.False(string.IsNullOrEmpty(nullPath.Note));
        }
    }
}
