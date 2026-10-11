// A recorded oracle for the BATCHED families (m11/jev-batch): the fixtures the per-object tests wrote, read for a request
// that asks a whole table at once.
//
// The per-object tests record one entry per object, keyed by that object's own request digest. A batched request has ONE
// digest for the table, so those entries cannot be found by digest any more; what identifies an object now is its ref, which
// the batched question names carry ("summarize_by:column:Sales/Amount"). This oracle keeps the fixture's shape (an entry's
// "digest" may be an object ref instead of a 64 character digest) and answers a batched request by composing one reply from
// the entries of the objects it asks about, question by question. A question whose object has no entry is simply left out of
// the reply, which is what a service that answered part of a request looks like (DecisionAnswerReader, per question).
// An entry keyed by a real digest, and any request that is not batched, is answered exactly as RecordedDecisionOracle does.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Decisions;

namespace Semanticus.Tests
{
    public sealed class ByObjectRecordedOracle : IDecisionOracle
    {
        private readonly RecordedDecisionOracle _byDigest;
        private readonly Dictionary<string, (string Point, JsonElement Answers, int In, int Out)> _byRef =
            new Dictionary<string, (string, JsonElement, int, int)>(StringComparer.Ordinal);
        private readonly List<DecisionRequest> _seen = new List<DecisionRequest>();
        private readonly object _gate = new object();
        private readonly JsonDocument _document;

        public ByObjectRecordedOracle(string fixtureJson, string servedModelVersion = "jev-test-1")
        {
            ServedModelVersion = servedModelVersion;
            _byDigest = new RecordedDecisionOracle(fixtureJson, servedModelVersion);
            _document = JsonDocument.Parse(fixtureJson);
            foreach (var e in _document.RootElement.GetProperty("entries").EnumerateArray())
            {
                var key = e.GetProperty("digest").GetString();
                if (key.Length == 64 && key.All(Uri.IsHexDigit)) continue;   // a real digest: RecordedDecisionOracle answers it
                var usage = e.TryGetProperty("usage", out var u) ? u : default;
                _byRef[key] = (e.TryGetProperty("point", out var p) ? p.GetString() : null, e.GetProperty("answers"),
                    usage.ValueKind == JsonValueKind.Object && usage.TryGetProperty("input_tokens", out var i) ? i.GetInt32() : 0,
                    usage.ValueKind == JsonValueKind.Object && usage.TryGetProperty("output_tokens", out var o) ? o.GetInt32() : 0);
            }
        }

        /// <summary>The ref of the one object a per-object request of the four batched points is about, read off its snapshot, or null.</summary>
        private static string RefOf(DecisionRequest r)
        {
            string Text(string key) => r.Snapshot.TryGetValue(key, out var v) ? v as string : null;
            switch (r.PointName)
            {
                case "column.format":
                case "column.summarize":
                    return Text("table_name") != null && Text("column_name") != null ? "column:" + Text("table_name") + "/" + Text("column_name") : null;
                case "measure.folder_and_format":
                    return Text("table_name") != null && Text("measure_name") != null ? "measure:" + Text("table_name") + "/" + Text("measure_name") : null;
                case "ai.schema":
                    var kind = Text("object_kind"); var name = Text("object_name"); var table = Text("table_name");
                    if (kind == null || name == null || table == null) return null;
                    return kind == "table" ? "table:" + name : kind + ":" + table + "/" + name;
                default: return null;
            }
        }

        public string ServedModelVersion { get; }
        public bool Available => true;

        /// <summary>Every request this oracle was asked, in order.</summary>
        public IReadOnlyList<DecisionRequest> Seen { get { lock (_gate) return _seen.ToList(); } }
        public IReadOnlyList<string> AskedDigests { get { lock (_gate) return _seen.Select(r => r.Digest).ToList(); } }
        public int AskCount { get { lock (_gate) return _seen.Count; } }

        public Task<DecisionAnswers> AskAsync(DecisionRequest request, CancellationToken ct)
        {
            lock (_gate) _seen.Add(request);
            if (!request.PerQuestion)
            {
                // A request for ONE object (the families' per-object mode): found by the object's ref, read off the request's own
                // snapshot, and answered with just the questions it asks. Anything else is answered by digest as before.
                var own = RefOf(request);
                if (own != null && _byRef.TryGetValue(own, out var one) && (one.Point == null || one.Point == request.PointName))
                {
                    var names = request.Questions.Select(q => q.Name).ToList();
                    if (names.All(n => one.Answers.TryGetProperty(n, out _)))
                    {
                        var body1 = "{" + string.Join(",", names.Select(n => JsonSerializer.Serialize(n) + ":" + one.Answers.GetProperty(n).GetRawText())) + "}";
                        return Task.FromResult(DecisionAnswerReader.Read(request, JsonDocument.Parse(body1).RootElement, ServedModelVersion, one.In, one.Out));
                    }
                }
                return _byDigest.AskAsync(request, ct);
            }
            if (ct.IsCancellationRequested)
                return Task.FromResult(DecisionAnswers.NotDecided(request, "the request was cancelled before it was answered"));

            var body = new StringBuilder("{");
            int tokensIn = 0, tokensOut = 0;
            var first = true;
            var counted = new HashSet<string>(StringComparer.Ordinal);
            foreach (var question in request.Questions)
            {
                var at = question.Name.IndexOf(':');
                if (at < 0) continue;
                var plain = question.Name.Substring(0, at);
                var objectRef = question.Name.Substring(at + 1);
                if (!_byRef.TryGetValue(objectRef, out var entry)) continue;
                if (entry.Point != null && !string.Equals(entry.Point, request.PointName, StringComparison.Ordinal)) continue;
                if (!entry.Answers.TryGetProperty(plain, out var answer)) continue;
                if (!first) body.Append(',');
                first = false;
                body.Append(JsonSerializer.Serialize(question.Name)).Append(':').Append(answer.GetRawText());
                if (counted.Add(objectRef)) { tokensIn += entry.In; tokensOut += entry.Out; }
            }
            body.Append('}');
            if (first)
                return Task.FromResult(DecisionAnswers.NotDecided(request, "no recorded answer for any object in this request"));
            return Task.FromResult(DecisionAnswerReader.Read(request, JsonDocument.Parse(body.ToString()).RootElement, ServedModelVersion, tokensIn, tokensOut));
        }
    }
}
