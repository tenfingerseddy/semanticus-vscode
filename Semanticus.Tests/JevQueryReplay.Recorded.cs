// Recorded parts, live check (the ruling "Jev data questions v2", 29 Sep, item 8 and the acceptance test's set C).
//
// A live run's results.json keeps every request's picks, in order, per question. Set JEV_REPLAY_PICKS to one and the parts
// calls (the first, the split, the table) are answered from those picks, exactly as the run made them, while the check
// that comes last is put to the REAL service. That tells what the check says about every reading the run built, without
// paying for the parts again and without the parts' own doubt moving the readings. A request with no recorded answer is
// listed under "mismatches" and answered "not sure", never invented.
//
//   JEV_REPLAY_LIVE=1 JEV_REPLAY_PICKS=<a live results.json> JEV_REPLAY_SET=<its set> JEV_REPLAY_BARE=1 JEV_REPLAY_FIXTURES=<fixtures>
//   JEV_REPLAY_OUT=<out dir> dotnet test --filter FullyQualifiedName~Replay_the_labelled_questions_through_the_product

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Decisions;
using Semanticus.Engine.Query;

namespace Semanticus.Tests
{
    public sealed partial class JevQueryReplay
    {
        /// <summary>The recorded picks of a live results.json, by item id.</summary>
        private static Dictionary<string, List<JsonElement>> RecordedPicks(string path)
        {
            using var rec = JsonDocument.Parse(File.ReadAllText(path));
            return rec.RootElement.GetProperty("rows").EnumerateArray()
                .ToDictionary(r => r.GetProperty("id").GetString(), r => r.GetProperty("picks").EnumerateArray().Select(p => p.Clone()).ToList());
        }

        /// <summary>The parts calls come from the recorded picks; the check goes to <paramref name="live"/>.</summary>
        private sealed class RecordedPartsLiveCheck : IDecisionOracle
        {
            private readonly IDecisionOracle _live;
            private List<JsonElement> _picks = new List<JsonElement>();
            private int _next;
            public readonly List<string> Mismatches = new List<string>();
            public RecordedPartsLiveCheck(IDecisionOracle live) { _live = live; }
            public bool Available => _live.Available;
            public void Load(List<JsonElement> picks) { _picks = picks; _next = 0; Mismatches.Clear(); }

            public Task<DecisionAnswers> AskAsync(DecisionRequest request, CancellationToken ct)
            {
                if (request.Questions.Any(q => q.Name == JevQueryReader.CheckGapQ || q.Name == JevQueryReader.CheckRealQ)) return _live.AskAsync(request, ct);
                if (_next >= _picks.Count)
                {
                    Mismatches.Add("no recorded request " + _next);
                    return Task.FromResult(DecisionAnswers.NotDecided(request, "no recorded answer"));
                }
                var rec = _picks[_next++];
                if (rec.TryGetProperty("notDecided", out var nd)) return Task.FromResult(DecisionAnswers.NotDecided(request, nd.GetString()));
                var answers = new Dictionary<string, object>(StringComparer.Ordinal);
                foreach (var q in request.Questions)
                {
                    if (!rec.TryGetProperty(q.Name, out var x))
                    {
                        Mismatches.Add("no recorded answer for " + q.Name);
                        answers[q.Name] = q.Kind == DecisionQuestionKind.Noul
                            ? new Dictionary<string, object> { ["type"] = "noul", ["noul"] = 0.5 }
                            : new Dictionary<string, object> { ["type"] = "choice", ["choice"] = q.HasOption("none") ? "none" : q.Options[0].Id, ["confidence"] = 0.0 };
                        continue;
                    }
                    var pick = x.GetProperty("pick").GetString();
                    var confidence = x.GetProperty("confidence").GetDouble();
                    var probs = x.GetProperty("probs").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetDouble());
                    if (q.Kind == DecisionQuestionKind.Noul)
                    {
                        var yes = probs.TryGetValue("yes", out var y) ? y : pick == "yes" ? confidence : 1 - confidence;
                        answers[q.Name] = new Dictionary<string, object> { ["type"] = "noul", ["noul"] = yes };
                    }
                    else
                    {
                        if (!q.Options.Any(o => o.Id == pick)) Mismatches.Add("recorded pick not offered for " + q.Name + ": " + pick);
                        answers[q.Name] = new Dictionary<string, object>
                        {
                            ["type"] = "choice", ["choice"] = pick, ["confidence"] = confidence,
                            ["probabilities"] = probs.Where(p => q.Options.Any(o => o.Id == p.Key)).ToDictionary(p => p.Key, p => p.Value),
                        };
                    }
                }
                var doc = new Dictionary<string, object>
                {
                    ["entries"] = new[]
                    {
                        new Dictionary<string, object>
                        {
                            ["digest"] = request.Digest, ["point"] = request.PointName,
                            ["usage"] = new Dictionary<string, object> { ["input_tokens"] = 0, ["output_tokens"] = 0 },
                            ["answers"] = answers,
                        },
                    },
                };
                return new RecordedDecisionOracle(JsonSerializer.Serialize(doc), "recorded-parts").AskAsync(request, ct);
            }
        }
    }
}
