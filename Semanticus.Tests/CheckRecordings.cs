// The recorded live runs of the reading check (q:check:gap, and q:check:real for a second opinion), so CI never calls Jev (the ruling of
// 29 Sep, item 7). One file, tools/jev-replay/query/check/recorded-checks.json, in the RecordedDecisionOracle shape,
// keyed on the COMPLETE request digest: the question words, the stated reading and the fields sent. A test whose
// request changed (a new wording of the stated reading, another measure) finds no entry and is answered by its own
// script, or by the plain "nothing missing, yes"; it is never answered from a neighbour's recording.
//
// JEV_CHECK_LIVE=1 makes every check call go to the real service instead and appends the answers to
// $JEV_CHECK_OUT/recorded-checks.json (the record is then copied into the repository by hand). The key is read from
// TYPESAFE_API_KEY in this process's environment first, else ~/.config/typesafe/env, and never printed or written.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Decisions;
using Semanticus.Engine.Decisions;
using Semanticus.Engine.Query;

namespace Semanticus.Tests
{
    internal static class CheckRecordings
    {
        private static readonly object Gate = new object();
        private static RecordedDecisionOracle _oracle;
        private static HashSet<string> _digests;
        private static readonly List<Dictionary<string, object>> Captured = new List<Dictionary<string, object>>();
        private static JevDecisionOracle _live;

        internal static string Repo()
        {
            var dir = AppContext.BaseDirectory;
            while (dir != null && !File.Exists(Path.Combine(dir, "Semanticus.sln"))) dir = Path.GetDirectoryName(dir);
            return dir ?? throw new InvalidOperationException("Could not find the repository root from " + AppContext.BaseDirectory);
        }

        internal static string RecordingPath => Path.Combine(Repo(), "tools", "jev-replay", "query", "check", "recorded-checks.json");

        private static bool Live => Environment.GetEnvironmentVariable("JEV_CHECK_LIVE") == "1";

        private static void Load()
        {
            lock (Gate)
            {
                if (_oracle != null) return;
                var json = File.Exists(RecordingPath) ? File.ReadAllText(RecordingPath) : "{\"entries\":[]}";
                _oracle = new RecordedDecisionOracle(json, "recorded-check");
                using var doc = JsonDocument.Parse(json);
                _digests = new HashSet<string>(doc.RootElement.GetProperty("entries").EnumerateArray().Select(e => e.GetProperty("digest").GetString()), StringComparer.Ordinal);
            }
        }

        /// <summary>True for a test that is a labelled check item: the verifier's round 2 and 3 cases, and the tests marked
        /// [CheckItem]. Only these are answered from the recording, or recorded live; every other test's check is the plain
        /// "nothing missing, yes", so a plumbing test never depends on what Jev once said.</summary>
        internal static bool IsItem(string test)
        {
            if (string.IsNullOrEmpty(test)) return false;
            if (test.StartsWith("Verify2_", StringComparison.Ordinal) || test.StartsWith("Verify3_", StringComparison.Ordinal)) return true;
            var method = typeof(AskDataTests).GetMethod(test, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
            return method != null && method.GetCustomAttributes(typeof(CheckItemAttribute), false).Length > 0;
        }

        /// <summary>True when the recording holds this exact request.</summary>
        internal static bool Holds(string digest) { Load(); return _digests.Contains(digest); }

        /// <summary>The recorded (or, with JEV_CHECK_LIVE=1, the live) answer to a check request; null when there is none.</summary>
        internal static async Task<DecisionAnswers> AnswerAsync(DecisionRequest request, string test, CancellationToken ct)
        {
            if (Live) return await LiveAsync(request, test, ct).ConfigureAwait(false);
            Load();
            return _digests.Contains(request.Digest) ? await _oracle.AskAsync(request, ct).ConfigureAwait(false) : null;
        }

        private static string Key()
        {
            var fromEnv = Environment.GetEnvironmentVariable("TYPESAFE_API_KEY");
            if (!string.IsNullOrWhiteSpace(fromEnv)) return fromEnv.Trim();
            var file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "typesafe", "env");
            var m = Regex.Match(File.ReadAllText(file), @"^\s*(?:export\s+)?TYPESAFE_API_KEY\s*=\s*[""']?([^""'\s]+)", RegexOptions.Multiline);
            if (!m.Success) throw new InvalidOperationException("No TYPESAFE_API_KEY in the key file.");
            return m.Groups[1].Value;
        }

        private static async Task<DecisionAnswers> LiveAsync(DecisionRequest request, string test, CancellationToken ct)
        {
            JevDecisionOracle oracle;
            lock (Gate) oracle = _live ??= new JevDecisionOracle(Key(), new DecisionBudget(2_000_000));
            var started = DateTime.UtcNow;
            var answers = await oracle.AskAsync(request, ct).ConfigureAwait(false);
            var ms = (DateTime.UtcNow - started).TotalMilliseconds;
            if (!answers.Decided) return answers;
            var entry = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["digest"] = request.Digest, ["point"] = request.PointName, ["id"] = test,
                ["question"] = request.Snapshot["question_words"], ["stated_reading"] = request.Snapshot.TryGetValue("stated_reading", out var sr) ? sr : null,
                ["ms"] = Math.Round(ms), ["usage"] = new Dictionary<string, object> { ["input_tokens"] = answers.InputTokens, ["output_tokens"] = answers.OutputTokens },
                ["answers"] = request.Questions.ToDictionary(q => q.Name, q => (object)Shape(q, answers.For(q.Name))),
            };
            lock (Gate)
            {
                Captured.RemoveAll(e => (string)e["digest"] == request.Digest);
                Captured.Add(entry);
                var dir = Environment.GetEnvironmentVariable("JEV_CHECK_OUT") ?? throw new InvalidOperationException("Set JEV_CHECK_OUT to keep the live answers.");
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "recorded-checks.json"), JsonSerializer.Serialize(new { servedModelVersion = answers.ServedModelVersion, entries = Captured }, new JsonSerializerOptions { WriteIndented = true }));
            }
            return answers;
        }

        private static Dictionary<string, object> Shape(DecisionQuestion q, DecisionAnswer a)
        {
            if (a == null) return new Dictionary<string, object> { ["type"] = "choice", ["choice"] = q.Options[0].Id, ["confidence"] = 0.0 };
            if (q.Kind == DecisionQuestionKind.Noul)
                return new Dictionary<string, object> { ["type"] = "noul", ["noul"] = a.Probabilities.TryGetValue("yes", out var y) ? y : (a.Noul == true ? a.Confidence : 1 - a.Confidence) };
            return new Dictionary<string, object>
            {
                ["type"] = "choice", ["choice"] = a.Choice, ["confidence"] = a.Confidence,
                ["probabilities"] = a.Probabilities.ToDictionary(kv => kv.Key, kv => kv.Value),
            };
        }
    }
}

namespace Semanticus.Tests
{
    /// <summary>Marks a test as a labelled item of the check replay set: its reading was put to the real check once, live, and
    /// the answer is recorded (tools/jev-replay/query/check/recorded-checks.json) and replayed offline. Label is "stop"
    /// (the reading is wrong for the question: the gate must stop it) or "run" (the reading is right: the gate must let it run).</summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
    internal sealed class CheckItemAttribute : Attribute
    {
        public CheckItemAttribute(string label, string grade = null) { Label = label; Grade = grade; }
        public string Label { get; }
        public string Grade { get; }
    }
}
