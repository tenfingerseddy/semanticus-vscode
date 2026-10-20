// Shared scaffolding for the across-tables batching tests and the measurement run (m16/jev-batch).
//
// ManyTablesModel writes a monitoring-shaped .bim (lookup tables, three fact tables, a measure table), the same shape the hero
// runs used (32 tables, 138 measures), at any table count, so a test can ask "what does ONE Plan read of a 30-table model cost".
// CountingRelay stands in for the relay and the vendor: it answers every question of every point (so the Plan has lines to run),
// records every call (point, questions, body bytes) and can charge a latency per call, so wall time through the real engine
// paths (six asks in flight, runs one after another) is measured and not computed.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Decisions;
using Semanticus.Engine.Decisions;

namespace Semanticus.Tests
{
    internal static class ManyTablesModel
    {
        private static readonly string[] Facts = { "Activity Event", "Capacity Operation", "Refresh History" };
        private static readonly string[] Look =
        {
            "Workspace", "Item", "Capacity", "Date", "Hour", "Principal", "Domain", "Operation Type", "Region", "Status", "Item Type", "SKU",
            "Owner", "Tenant", "Environment", "Tag", "Service", "Severity", "Gateway", "Dataset Tier", "Country", "Department", "Cost Centre",
            "Licence", "Client App", "Device", "Time Zone", "Retention Class",
        };
        private static readonly string[] Shared = { "Date", "Workspace", "Item", "Capacity" };
        private static readonly string[] Num = { "Duration Ms", "Row Count", "CU Seconds", "Bytes", "Error Count", "Throttle Ms", "Queue Ms", "Cost" };
        private static readonly string[] Txt = { "Name", "Code", "Category", "Group", "Label", "Owner", "Notes", "Tier", "Kind", "Description" };

        private static Dictionary<string, object> Col(string name, string type, bool hidden = false)
        {
            var c = new Dictionary<string, object> { ["name"] = name, ["dataType"] = type, ["sourceColumn"] = name };
            if (hidden) c["isHidden"] = true;
            return c;
        }

        private static List<object> Partition(string table, IEnumerable<Dictionary<string, object>> cols)
        {
            var list = cols.ToList();
            var row = string.Join(", ", list.Select(c => (string)c["dataType"] == "string" ? "\"x\"" : "1"));
            var typ = string.Join(", ", list.Select(c => "#\"" + c["name"] + "\" = " + ((string)c["dataType"] == "string" ? "text" : "number")));
            return new List<object>
            {
                new Dictionary<string, object>
                {
                    ["name"] = table, ["mode"] = "import",
                    ["source"] = new Dictionary<string, object> { ["type"] = "m", ["expression"] = "#table(type table [" + typ + "], {{" + row + "}})" },
                },
            };
        }

        /// <summary>Writes the model and returns the number of tables it has (the lookups asked for, three facts, one measure table).</summary>
        public static int Write(string path, int tableCount, Func<string, string> rename = null)
        {
            var T = rename ?? (x => x);   // jev114: an optional translation of the fact tables, their columns and measures (multilingual check)
            var lookups = Math.Max(1, tableCount - Facts.Length - 1);
            var look = Enumerable.Range(0, lookups).Select(i => i < Look.Length ? Look[i] : "Lookup " + i).ToList();
            var shared = Shared.Where(look.Contains).ToList();
            var own = look.Where(l => !shared.Contains(l)).ToList();
            var tables = new List<Dictionary<string, object>>();
            var rels = new List<object>();
            for (var ti = 0; ti < look.Count; ti++)
            {
                var l = look[ti];
                var cols = new List<Dictionary<string, object>> { Col(l + " Key", "int64", hidden: true) };
                for (var i = 0; i < 7 + ti % 6; i++)
                {
                    var n = T(Txt[i % Txt.Length]) + (i >= Txt.Length ? " " + i : "");
                    cols.Add(Col(n == "Name" || n == "Code" ? l + " " + n : n, "string"));
                }
                tables.Add(new Dictionary<string, object> { ["name"] = l, ["columns"] = cols, ["partitions"] = Partition(l, cols) });
            }
            for (var fi = 0; fi < Facts.Length; fi++)
            {
                var f = T(Facts[fi]);
                var picks = shared.Concat(own.Skip(own.Count == 0 ? 0 : (fi * 3) % own.Count).Take(1 + fi % 4)).ToList();
                var cols = new List<Dictionary<string, object>> { Col(f + " Key", "int64", hidden: true) };
                var num = Num.Select(T).ToArray();
                cols.AddRange(num.Select(c => Col(c, "double")));
                cols.AddRange(new[] { "Timestamp", "Event Id", "Correlation Id", "Status Code" }.Select(c => Col(T(c), "string")));
                cols.AddRange(picks.Select(p => Col(p + " Key", "int64", hidden: true)));
                var ms = new List<object>();
                foreach (var c in num)
                {
                    ms.Add(new Dictionary<string, object> { ["name"] = f + " " + c, ["expression"] = "SUM('" + f + "'[" + c + "])" });
                    ms.Add(new Dictionary<string, object> { ["name"] = f + T(" Avg ") + c, ["expression"] = "AVERAGE('" + f + "'[" + c + "])" });
                }
                ms.Add(new Dictionary<string, object> { ["name"] = f + T(" Count"), ["expression"] = "COUNTROWS('" + f + "')" });
                foreach (var c in num)
                    ms.Add(new Dictionary<string, object> { ["name"] = f + " " + c + T(" per Row"), ["expression"] = "DIVIDE([" + f + " " + c + "], [" + f + T(" Count") + "])" });
                tables.Add(new Dictionary<string, object> { ["name"] = f, ["columns"] = cols, ["measures"] = ms, ["partitions"] = Partition(f, cols) });
                foreach (var p in picks)
                    rels.Add(new Dictionary<string, object>
                    {
                        ["name"] = "r" + rels.Count, ["fromTable"] = f, ["fromColumn"] = p + " Key", ["toTable"] = p, ["toColumn"] = p + " Key",
                    });
            }
            var kpis = Enumerable.Range(0, 12).Select(i => (object)new Dictionary<string, object>
            {
                ["name"] = "KPI " + (i + 1).ToString("00", CultureInfo.InvariantCulture),
                ["expression"] = "[" + T(Facts[i % 3]) + " " + T("Cost") + "] + [" + T(Facts[(i + 1) % 3]) + " " + T("Cost") + "] * " + (i + 1).ToString(CultureInfo.InvariantCulture),
            }).ToList();
            var valueCols = new List<Dictionary<string, object>> { Col("Value", "int64", hidden: true) };
            tables.Add(new Dictionary<string, object>
            {
                ["name"] = "Measure Table", ["columns"] = valueCols, ["measures"] = kpis, ["partitions"] = Partition("Measure Table", valueCols),
            });
            var model = new Dictionary<string, object>
            {
                ["name"] = "Monitoring Ops", ["compatibilityLevel"] = 1600,
                ["model"] = new Dictionary<string, object>
                {
                    ["culture"] = "en-US", ["tables"] = tables, ["relationships"] = rels,
                    ["annotations"] = new List<object> { new Dictionary<string, object> { ["name"] = "PBI_QueryOrder", ["value"] = "[]" } },
                },
            };
            File.WriteAllText(path, JsonSerializer.Serialize(model, new JsonSerializerOptions { WriteIndented = true }));
            return tables.Count;
        }
    }

    /// <summary>One call the fake relay saw.</summary>
    internal sealed class RelayCall
    {
        public string Point;
        public int Questions;
        public int BodyBytes;
        public int SnapshotBytes;
        public double StartedAtSeconds;
        public double LatencyMs;
    }

    /// <summary>A stand-in for the relay and the vendor. It answers every question (the first option, sure; a score on its third rung; a
    /// noul yes), records every call, and charges <see cref="BaseLatencyMs"/> plus <see cref="PerQuestionMs"/> a question per call
    /// (real time, scaled by <see cref="Scale"/>), so six asks in flight and runs one after another cost what they would cost.</summary>
    internal sealed class CountingRelay : IDecisionOracle
    {
        private readonly List<RelayCall> _calls = new List<RelayCall>();
        private readonly object _gate = new object();
        private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
        private int _inFlight, _peak;

        public double BaseLatencyMs { get; set; }
        public double PerQuestionMs { get; set; } = 1;
        public double Scale { get; set; } = 1;

        /// <summary>Which option a choice question is answered with (the first by default).</summary>
        public Func<DecisionQuestion, string> Choose { get; set; }

        /// <summary>A question the reply leaves out (a service that answered part of a request).</summary>
        public Func<DecisionQuestion, bool> Omit { get; set; }

        /// <summary>An older relay: it refuses a request with more questions than this, free, in its own words (413 too_many_questions).</summary>
        public int RefuseOver { get; set; }

        /// <summary>How many requests the relay refused as too big.</summary>
        public int Refused;

        /// <summary>Extra latency for one point (milliseconds, before <see cref="Scale"/>), so a test can keep ONE point in flight.</summary>
        public Func<string, double> ExtraLatencyMs { get; set; }

        /// <summary>The calls of each point whose ask was cancelled after they left (the vendor still charges them).</summary>
        private readonly Dictionary<string, int> _cancelled = new Dictionary<string, int>(StringComparer.Ordinal);
        public int CancelledOf(string point) { lock (_gate) return _cancelled.TryGetValue(point, out var n) ? n : 0; }

        public bool Available => true;
        public int Peak => _peak;
        public IReadOnlyList<RelayCall> Calls { get { lock (_gate) return _calls.ToList(); } }

        public void Reset() { lock (_gate) _calls.Clear(); _peak = 0; _clock.Restart(); }

        public async Task<DecisionAnswers> AskAsync(DecisionRequest request, CancellationToken ct)
        {
            var snapshotJson = DecisionDigest.CanonicalSnapshotJson(request.Snapshot);
            var call = new RelayCall
            {
                Point = request.PointName, Questions = request.Questions.Count, SnapshotBytes = Encoding.UTF8.GetByteCount(snapshotJson),
                BodyBytes = Encoding.UTF8.GetByteCount(JevDecisionOracle.BuildBody(request, snapshotJson)),
                StartedAtSeconds = _clock.Elapsed.TotalSeconds,
                LatencyMs = BaseLatencyMs + PerQuestionMs * request.Questions.Count + (ExtraLatencyMs?.Invoke(request.PointName) ?? 0),
            };
            if (RefuseOver > 0 && request.Questions.Count > RefuseOver)
            {
                Interlocked.Increment(ref Refused);
                return DecisionAnswers.NotDecided(request, JevDecisionOracle.TooBigSentences[0]);
            }
            lock (_gate) _calls.Add(call);
            var now = Interlocked.Increment(ref _inFlight);
            int seen;
            while ((seen = _peak) < now && Interlocked.CompareExchange(ref _peak, now, seen) != seen) { }
            try
            {
                if (call.LatencyMs > 0) await Task.Delay(TimeSpan.FromMilliseconds(call.LatencyMs * Scale), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                lock (_gate) _cancelled[request.PointName] = (_cancelled.TryGetValue(request.PointName, out var n) ? n : 0) + 1;
                throw;
            }
            finally { Interlocked.Decrement(ref _inFlight); }

            var body = new StringBuilder("{");
            var first = true;
            foreach (var q in request.Questions)
            {
                if (Omit != null && Omit(q)) continue;
                if (!first) body.Append(',');
                first = false;
                body.Append(JsonSerializer.Serialize(q.Name)).Append(':');
                switch (q.Kind)
                {
                    case DecisionQuestionKind.Choice:
                        body.Append("{\"type\":\"choice\",\"choice\":").Append(JsonSerializer.Serialize(Choose?.Invoke(q) ?? q.Options[0].Id)).Append(",\"confidence\":0.95}");
                        break;
                    case DecisionQuestionKind.Noul:
                        body.Append("{\"type\":\"noul\",\"noul\":0.9}");
                        break;
                    default:
                        var legend = string.Join(",", q.Options.Select((o, i) => "\"" + i + "\":" + JsonSerializer.Serialize(o.Criterion)));
                        var probs = string.Join(",", q.Options.Select((o, i) => "\"" + i + "\":" + (i == 2 ? "0.9" : "0.025")));
                        body.Append("{\"type\":\"score\",\"score\":2.0,\"confidence\":0.9,\"legend\":{").Append(legend).Append("},\"probabilities\":{").Append(probs).Append("}}");
                        break;
                }
            }
            body.Append('}');
            using var doc = JsonDocument.Parse(body.ToString());
            // About four characters a token is the rule of thumb the product's own estimate divides by three to be pessimistic about.
            return DecisionAnswerReader.Read(request, doc.RootElement.Clone(), "jev-test-1", call.BodyBytes / 4, 20);
        }
    }
}
