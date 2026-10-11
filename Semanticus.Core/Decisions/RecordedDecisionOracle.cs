// The oracle every test uses.
//
// It answers from a JSON fixture keyed on the COMPLETE request digest: point name, schema version, the
// canonical snapshot, the questions, and the option order. Keying on anything smaller (a question hash, a
// point name, an object ref) is the mistake the plan's replay gate calls out by name, because it lets a test
// pass while the evidence that was actually sent has quietly changed underneath it.
//
// A digest it does not hold is "not decided", never an invented answer and never an exception. That is what
// makes it safe to point a whole suite at one small fixture.
//
// Fixture shape:
//   {
//     "servedModelVersion": "jev-1.13.0",
//     "entries": [
//       {
//         "digest": "<64 hex chars>",
//         "point": "measure.folder_and_format",
//         "usage": { "input_tokens": 999, "output_tokens": 48 },
//         "answers": { "<question>": { "type": "choice", "choice": "Sales", "confidence": 0.93,
//                                      "probabilities": { "Sales": 0.93, "none": 0.07 } } }
//       }
//     ]
//   }

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Semanticus.Decisions
{
    public sealed class RecordedDecisionOracle : IDecisionOracle
    {
        private sealed class Entry
        {
            public string Point;
            public JsonElement Answers;
            public int InputTokens;
            public int OutputTokens;
        }

        private readonly Dictionary<string, Entry> _byDigest = new Dictionary<string, Entry>(StringComparer.Ordinal);
        private readonly List<string> _asked = new List<string>();
        private readonly object _gate = new object();
        private readonly JsonDocument _document;

        public RecordedDecisionOracle(string fixtureJson, string servedModelVersion = null)
        {
            if (string.IsNullOrWhiteSpace(fixtureJson)) throw new ArgumentException("The fixture is empty.", nameof(fixtureJson));
            _document = JsonDocument.Parse(fixtureJson);
            var root = _document.RootElement;

            JsonElement version;
            ServedModelVersion = servedModelVersion
                ?? (root.TryGetProperty("servedModelVersion", out version) && version.ValueKind == JsonValueKind.String
                    ? version.GetString() : "recorded");

            JsonElement entries;
            if (!root.TryGetProperty("entries", out entries) || entries.ValueKind != JsonValueKind.Array)
                throw new ArgumentException("The fixture has no 'entries' array.", nameof(fixtureJson));

            foreach (var e in entries.EnumerateArray())
            {
                JsonElement digest, answers;
                if (!e.TryGetProperty("digest", out digest) || digest.ValueKind != JsonValueKind.String)
                    throw new ArgumentException("A fixture entry has no 'digest'.", nameof(fixtureJson));
                if (!e.TryGetProperty("answers", out answers))
                    throw new ArgumentException($"Fixture entry '{digest.GetString()}' has no 'answers'.", nameof(fixtureJson));

                JsonElement point, usage, tokens;
                var entry = new Entry
                {
                    Point = e.TryGetProperty("point", out point) && point.ValueKind == JsonValueKind.String ? point.GetString() : null,
                    Answers = answers,
                };
                if (e.TryGetProperty("usage", out usage) && usage.ValueKind == JsonValueKind.Object)
                {
                    if (usage.TryGetProperty("input_tokens", out tokens) && tokens.ValueKind == JsonValueKind.Number)
                        entry.InputTokens = tokens.GetInt32();
                    if (usage.TryGetProperty("output_tokens", out tokens) && tokens.ValueKind == JsonValueKind.Number)
                        entry.OutputTokens = tokens.GetInt32();
                }

                var key = digest.GetString();
                if (_byDigest.ContainsKey(key))
                    throw new ArgumentException($"The fixture records the digest '{key}' twice.", nameof(fixtureJson));
                _byDigest[key] = entry;
            }
        }

        public static RecordedDecisionOracle FromFile(string path, string servedModelVersion = null) =>
            new RecordedDecisionOracle(File.ReadAllText(path), servedModelVersion);

        /// <summary>An oracle that is configured but holds nothing, so every ask is "not decided". Used to prove
        /// that a caller handles a shrug from an AVAILABLE service, which is a different path from no service.</summary>
        public static RecordedDecisionOracle Empty(string servedModelVersion = "recorded") =>
            new RecordedDecisionOracle("{\"entries\":[]}", servedModelVersion);

        public string ServedModelVersion { get; }

        public bool Available { get { return true; } }

        /// <summary>Every digest this oracle was asked, in order. The frozen case matrix asserts this is EMPTY on
        /// every refused path, which is how "zero requests are recorded" is measured rather than asserted.</summary>
        public IReadOnlyList<string> AskedDigests
        {
            get { lock (_gate) { return _asked.ToList(); } }
        }

        public int AskCount { get { lock (_gate) { return _asked.Count; } } }

        public Task<DecisionAnswers> AskAsync(DecisionRequest request, CancellationToken ct)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));

            lock (_gate) _asked.Add(request.Digest);

            if (ct.IsCancellationRequested)
                return Task.FromResult(DecisionAnswers.NotDecided(request, "the request was cancelled before it was answered"));

            Entry entry;
            if (!_byDigest.TryGetValue(request.Digest, out entry))
                return Task.FromResult(DecisionAnswers.NotDecided(request,
                    $"no recorded answer for this evidence (digest {Short(request.Digest)})"));

            if (entry.Point != null && !string.Equals(entry.Point, request.PointName, StringComparison.Ordinal))
                return Task.FromResult(DecisionAnswers.NotDecided(request,
                    $"the recorded answer for this digest belongs to '{entry.Point}'"));

            return Task.FromResult(DecisionAnswerReader.Read(request, entry.Answers, ServedModelVersion,
                entry.InputTokens, entry.OutputTokens));
        }

        private static string Short(string digest) =>
            string.IsNullOrEmpty(digest) ? "none" : digest.Substring(0, Math.Min(12, digest.Length));
    }
}
