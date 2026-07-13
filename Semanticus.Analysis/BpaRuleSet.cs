using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Semanticus.Analysis
{
    /// <summary>
    /// Sources of Best Practice rules. The bundled canonical <b>Power BI standard ruleset</b>
    /// (TabularEditor/BestPracticeRules, MIT) ships as an embedded resource and is the default base;
    /// <see cref="Parse"/> reads user-supplied rule JSON (a file, a URL, the model annotation) in the
    /// same standard BPARules.json schema (see <see cref="BpaRule"/>). The engine composes
    /// <see cref="Standard"/> with the model's embedded rules and any session-loaded rules.
    /// </summary>
    public static class BpaRuleSet
    {
        private const string StandardResourceSuffix = "BPARules-PowerBI.json";
        // Match the leniency of the Newtonsoft reader TabularEditor itself uses, so hand-edited community/org
        // BPARules.json (comments, trailing commas, string-encoded numbers like "Severity":"2") load the same here.
        private static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            NumberHandling = JsonNumberHandling.AllowReadingFromString,
        };
        private static IReadOnlyList<BpaRule> _standard;

        /// <summary>The bundled Power BI best-practice ruleset, parsed once and cached. Returns empty if the
        /// embedded resource is somehow missing/unreadable — the engine then falls back to the curated
        /// <see cref="BpaDefaultRules"/> so a scan is never left with zero rules.</summary>
        public static IReadOnlyList<BpaRule> Standard()
        {
            if (_standard != null) return _standard;
            try
            {
                var asm = typeof(BpaRuleSet).Assembly;
                var name = Array.Find(asm.GetManifestResourceNames(),
                    n => n.EndsWith(StandardResourceSuffix, StringComparison.OrdinalIgnoreCase));
                if (name == null) return _standard = Array.Empty<BpaRule>();
                using var stream = asm.GetManifestResourceStream(name);
                using var reader = new StreamReader(stream);
                return _standard = Parse(reader.ReadToEnd());
            }
            catch { return _standard = Array.Empty<BpaRule>(); }
        }

        /// <summary>Parse standard-format rules JSON — a rule array, OR a single rule object (treated as a
        /// one-element array). Tolerant of comments / trailing commas / string-encoded numbers and of extra or
        /// missing fields; throws <see cref="JsonException"/> on genuinely malformed JSON so a bad file is
        /// reported, not silently ignored. Drops entries with no ID (a rule needs a stable id) and de-dups by
        /// ID — last wins, so a re-listed rule overrides rather than double-fires.</summary>
        public static List<BpaRule> Parse(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return new List<BpaRule>();
            var trimmed = json.TrimStart();
            List<BpaRule> rules;
            if (trimmed.StartsWith("{"))   // a single inline rule object → one-element array
            {
                var one = JsonSerializer.Deserialize<BpaRule>(trimmed, JsonOpts);
                rules = one == null ? new List<BpaRule>() : new List<BpaRule> { one };
            }
            else rules = JsonSerializer.Deserialize<List<BpaRule>>(trimmed, JsonOpts) ?? new List<BpaRule>();
            var byId = new Dictionary<string, BpaRule>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in rules)
                if (r != null && !string.IsNullOrWhiteSpace(r.ID)) byId[r.ID] = r;
            return byId.Values.ToList();
        }
    }
}
