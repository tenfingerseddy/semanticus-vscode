using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Semanticus.Engine
{
    /// <summary>
    /// docs/harness-engineering.md §5 — "mine the telemetry for ergonomics before mining it for
    /// learning." A PURE, DETERMINISTIC analyzer over the L0 experience log (`.semanticus/experience.jsonl`,
    /// written by <see cref="ExperienceTee"/>). It answers the harness-ergonomics question — which ops flail,
    /// which error messages recur — so §1/§2's audits get priorities from DATA, not taste. Engine-side so
    /// both doors AND the offline script share ONE definition; the fallback `tools/harness-report/report.py`
    /// mirrors these exact definitions.
    ///
    /// Envelope schema it reads (verified against ExperienceTee, do not guess):
    ///   common:   schemaVersion, when(ISO-8601), kind("change"|"activity"), sessionId, origin, modelFingerprint
    ///   change:   revision, label, deltas[]            (an applied delta — always successful, no ok/error)
    ///   activity: seq, op, label, target, ok(bool), error(string|null), rowCount, elapsedMs, result
    /// Only activity records carry ok/error, so error-rate/retry/flail analysis is over activity records;
    /// change records are counted separately. The envelope carries NO token counts → tokens-per-outcome is
    /// NOT derivable from L0 v1 (reported honestly, never fabricated).
    /// </summary>
    public static class HarnessReport
    {
        // ── Deterministic definitions (IDENTICAL to tools/harness-report/report.py) ──────────────────
        //
        // RETRY CLUSTER: a maximal run of >=2 activity records that share the same `op` and are ALL
        //   failures (ok==false), ADJACENT in the activity stream taken in file order (an append-only
        //   JSONL is already chronological, so file order == time order — no re-sort needed). "Adjacent"
        //   means no other activity record sits between them; change records do not break a cluster.
        //
        // FLAIL SITE: per op, `failuresBeforeSuccess` = the count of failure records that PRECEDED a
        //   success of that same op. Walk each op's activity records in file order, running a failure
        //   counter; on a success, add the counter to the total and reset; on a failure, increment.
        //   Trailing failures that never reach a success are NOT counted (they are hard errors, not flail —
        //   they still surface in the error-rate table). Ops ranked by failuresBeforeSuccess descending.
        //
        // INTERVENTION: a human stepping into an agent sequence — counted as the number of
        //   agent-origin -> human-origin transitions in the full record stream (change + activity) in
        //   file order. Each transition is one intervention regardless of how many human records follow.
        //
        // FLAIL RATE: failure activity records per 100 activity records.
        // TASK SUCCESS RATE: ok activity records / total activity records (records that carry `ok`).
        // TOKENS PER OUTCOME: not derivable from L0 v1 (no token field in the envelope).

        public static HarnessReportResult FromFile(string path, int topN)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return new HarnessReportResult
                {
                    LogPath = path,
                    Note = string.IsNullOrEmpty(path)
                        ? "No experience log path could be resolved (no session / no workspace). Empty report."
                        : "No experience log at the resolved path yet — nothing captured. Empty report.",
                };

            string[] lines;
            try { lines = File.ReadAllLines(path); }
            catch (Exception ex)
            {
                return new HarnessReportResult { LogPath = path, Note = "Experience log present but unreadable (" + ex.GetType().Name + ") — skipped." };
            }

            var result = Analyze(lines, topN);
            result.LogPath = path;
            return result;
        }

        /// <summary>Pure analysis over raw JSONL lines. Fail-soft: an unparseable/corrupt line is counted
        /// in <see cref="HarnessReportResult.SkippedLines"/> and skipped, never thrown.</summary>
        public static HarnessReportResult Analyze(IEnumerable<string> lines, int topN)
        {
            if (topN <= 0) topN = 10;
            var records = new List<Rec>();
            int skipped = 0;

            foreach (var line in lines ?? Enumerable.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var r = doc.RootElement;
                    var rec = new Rec
                    {
                        Kind = Str(r, "kind"),
                        Origin = Str(r, "origin"),
                        When = Str(r, "when"),
                        Op = Str(r, "op"),
                        Label = Str(r, "label"),
                        Error = Str(r, "error"),
                    };
                    if (r.TryGetProperty("ok", out var okEl) && (okEl.ValueKind == JsonValueKind.True || okEl.ValueKind == JsonValueKind.False))
                        rec.Ok = okEl.GetBoolean();
                    records.Add(rec);
                }
                catch { skipped++; }   // one corrupt line must not sink the report (fail-soft, §5)
            }

            var result = new HarnessReportResult
            {
                SkippedLines = skipped,
                TotalRecords = records.Count,
                ActivityRecords = records.Count(x => x.Kind == "activity"),
                ChangeRecords = records.Count(x => x.Kind == "change"),
            };

            var activity = records.Where(x => x.Kind == "activity" && !string.IsNullOrEmpty(x.Op)).ToList();

            // Per-op counts + error rate (over records that carry `ok`).
            result.OpStats = activity
                .GroupBy(x => x.Op)
                .Select(g =>
                {
                    var withOk = g.Where(x => x.Ok.HasValue).ToList();
                    int failures = withOk.Count(x => x.Ok == false);
                    return new OpStat
                    {
                        Op = g.Key,
                        Count = g.Count(),
                        Failures = failures,
                        ErrorRate = withOk.Count == 0 ? 0.0 : Math.Round((double)failures / withOk.Count, 4),
                    };
                })
                .OrderByDescending(o => o.Failures).ThenByDescending(o => o.Count).ThenBy(o => o.Op)
                .ToArray();

            // Retry clusters: maximal runs of >=2 adjacent same-op failures in the activity stream.
            var clusters = new List<RetryCluster>();
            int i = 0;
            while (i < activity.Count)
            {
                var a = activity[i];
                if (a.Ok == false)
                {
                    int j = i + 1;
                    while (j < activity.Count && activity[j].Ok == false && activity[j].Op == a.Op) j++;
                    int runLen = j - i;
                    if (runLen >= 2)
                    {
                        clusters.Add(new RetryCluster
                        {
                            Op = a.Op,
                            Count = runLen,
                            FirstWhen = a.When,
                            LastWhen = activity[j - 1].When,
                            Errors = activity.Skip(i).Take(runLen)
                                .Select(x => x.Error).Where(e => !string.IsNullOrEmpty(e)).Distinct().ToArray(),
                        });
                        i = j;
                        continue;
                    }
                }
                i++;
            }
            result.RetryClusters = clusters.OrderByDescending(c => c.Count).ThenBy(c => c.Op).ToArray();

            // Error-message frequency (top N distinct error texts on failed activity records).
            result.TopErrors = activity
                .Where(x => x.Ok == false && !string.IsNullOrEmpty(x.Error))
                .GroupBy(x => x.Error)
                .Select(g => new ErrorFreq { Error = g.Key, Count = g.Count() })
                .OrderByDescending(e => e.Count).ThenBy(e => e.Error)
                .Take(topN)
                .ToArray();

            // Flail sites: failures-before-a-success, per op (see definition block above).
            var flail = new List<FlailSite>();
            foreach (var g in activity.GroupBy(x => x.Op))
            {
                int running = 0, total = 0;
                foreach (var x in g)
                {
                    if (x.Ok == false) running++;
                    else if (x.Ok == true) { total += running; running = 0; }
                }
                if (total > 0) flail.Add(new FlailSite { Op = g.Key, FailuresBeforeSuccess = total });
            }
            result.FlailSites = flail.OrderByDescending(f => f.FailuresBeforeSuccess).ThenBy(f => f.Op).Take(topN).ToArray();

            // Interventions: agent -> human origin transitions in the full record stream (file order).
            int interventions = 0;
            for (int k = 1; k < records.Count; k++)
                if (records[k].Origin == "human" && records[k - 1].Origin == "agent") interventions++;

            // KPI summary line.
            int okBearing = activity.Count(x => x.Ok.HasValue);
            int failCount = activity.Count(x => x.Ok == false);
            double? successRate = okBearing == 0 ? (double?)null : Math.Round((double)activity.Count(x => x.Ok == true) / okBearing, 4);
            double flailRate = activity.Count == 0 ? 0.0 : Math.Round((double)failCount / activity.Count * 100.0, 2);

            result.Kpis = new HarnessKpis
            {
                Events = activity.Count,
                SuccessRate = successRate,
                FlailRatePer100 = flailRate,
                Interventions = interventions,
                Summary = string.Format(
                    "success {0} · tokens/outcome {1} · flail {2}/100 · interventions {3} (over {4} activity events)",
                    successRate.HasValue ? (successRate.Value * 100).ToString("0.#") + "%" : "n/a",
                    HarnessKpis.TokensPerOutcomeNote,
                    flailRate.ToString("0.##"),
                    interventions,
                    activity.Count),
            };

            if (result.TotalRecords == 0)
                result.Note = skipped > 0
                    ? "No parseable records — " + skipped + " line(s) were corrupt and skipped."
                    : "Log is empty — nothing captured yet.";
            else if (skipped > 0)
                result.Note = skipped + " corrupt line(s) were skipped (fail-soft).";

            return result;
        }

        private static string Str(JsonElement r, string name)
            => r.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;

        private sealed class Rec
        {
            public string Kind, Origin, When, Op, Label, Error;
            public bool? Ok;
        }
    }

    /// <summary>The §5 telemetry report. DTO lives here (not Protocol.cs) to keep it conflict-free.</summary>
    public sealed class HarnessReportResult
    {
        public string LogPath { get; set; }
        public string Note { get; set; }
        public int TotalRecords { get; set; }
        public int ActivityRecords { get; set; }
        public int ChangeRecords { get; set; }
        public int SkippedLines { get; set; }
        public HarnessKpis Kpis { get; set; }
        public OpStat[] OpStats { get; set; } = Array.Empty<OpStat>();
        public RetryCluster[] RetryClusters { get; set; } = Array.Empty<RetryCluster>();
        public ErrorFreq[] TopErrors { get; set; } = Array.Empty<ErrorFreq>();
        public FlailSite[] FlailSites { get; set; } = Array.Empty<FlailSite>();
    }

    public sealed class HarnessKpis
    {
        public const string TokensPerOutcomeNote = "not derivable from L0 v1 (envelope carries no token counts)";
        public int Events { get; set; }
        public double? SuccessRate { get; set; }             // null when no ok-bearing events
        public string TokensPerOutcome { get; set; } = TokensPerOutcomeNote;
        public double FlailRatePer100 { get; set; }
        public int Interventions { get; set; }
        public string Summary { get; set; }
    }

    public sealed class OpStat
    {
        public string Op { get; set; }
        public int Count { get; set; }
        public int Failures { get; set; }
        public double ErrorRate { get; set; }
    }

    public sealed class RetryCluster
    {
        public string Op { get; set; }
        public int Count { get; set; }
        public string FirstWhen { get; set; }
        public string LastWhen { get; set; }
        public string[] Errors { get; set; } = Array.Empty<string>();
    }

    public sealed class ErrorFreq
    {
        public string Error { get; set; }
        public int Count { get; set; }
    }

    public sealed class FlailSite
    {
        public string Op { get; set; }
        public int FailuresBeforeSuccess { get; set; }
    }
}
