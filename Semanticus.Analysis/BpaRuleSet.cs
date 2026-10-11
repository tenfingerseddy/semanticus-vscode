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

        /// <summary>The rules in force on a model: the bundled standard set (the curated defaults only if the embedded
        /// resource failed to load, so a scan is never left with zero rules), plus the rules embedded in the model (the
        /// BestPracticeAnalyzer annotation, where load_rules persists user and org rules), merged by id with the model's
        /// rule winning. A model rule is tagged with its provenance. Shared by the engine and the analyzers, so the scan, the
        /// fixes and the declined-change set all read the SAME effective set.</summary>
        public static List<BpaRule> ForModel(TabularEditor.TOMWrapper.Model m)
        {
            var byId = new Dictionary<string, BpaRule>(StringComparer.OrdinalIgnoreCase);
            var standard = Standard();
            foreach (var r in (standard.Count > 0 ? standard : BpaDefaultRules.Rules)) byId[r.ID] = r;
            try
            {
                var json = m.GetAnnotation("BestPracticeAnalyzer");
                if (!string.IsNullOrWhiteSpace(json))
                    foreach (var r in Parse(json))
                    {
                        // A Tabular Editor user who embeds the standard set has put the BUNDLED rule in the annotation, not written
                        // one: same id and the same behaviour in EVERY field (SameRule) is the bundled rule, so it is advice where the bundled rule is
                        // advice and the gate skips it (M11 join part 4, E3). A rule that differs in any field, its severity above all, is one the
                        // model wrote, and keeps its own severity 3 gate.
                        r.FromModelAnnotation = !(byId.TryGetValue(r.ID, out var bundled) && !bundled.FromModelAnnotation && SameRule(bundled, r));
                        byId[r.ID] = r;
                    }
            }
            catch { /* malformed embedded rules: ignore, keep the base set */ }
            // Built in: never from the bundled file, so a model that carries only its own rules, or whose bundled set failed to load, is still checked for it.
            foreach (var r in Native()) byId[r.ID] = r;
            return byId.Values.ToList();
        }

        /// <summary>The rules Semanticus adds to the Tabular Editor set. Each one needs the engine's own checks, which a Dynamic-LINQ predicate cannot call, so it is a token
        /// check (<see cref="BpaRule.TokenCheck"/>).</summary>
        public static IReadOnlyList<BpaRule> Native() => _native ??= new[]
        {
            new BpaRule
            {
                ID = BpaGrading.BrokenFormulaRuleId, Name = "A formula refers to an object the model does not have",
                Category = "Error Prevention", Severity = 3, Scope = "Measure, CalculatedColumn, CalculationItem, ModelRole",
                TokenCheck = "missing-objects",
                Description = "%object% has a formula that refers to a table, column or measure the model does not have, so it cannot give a number. Point the formula at objects that exist.",
            },
        };
        private static IReadOnlyList<BpaRule> _native;

        /// <summary>Everything about a rule that changes WHAT IT DOES: which objects it looks at, what it flags, how bad a hit is, what its fix
        /// writes, which models it applies to. Every public property of <see cref="BpaRule"/> is one of these unless it is named here as words a person
        /// reads (the name and the description), the id that matched it, or provenance. So a property added to the rule later is compared on its own
        /// and a copy that differs in it is the model's own rule, instead of passing for the bundled one until somebody remembers to list it
        /// (Sol 6.1 on the M11 delta, finding B1, the second strike of 4.3: the first version compared the expression alone, so a copy with its
        /// severity raised to an error was taken for the bundled rule and the gate skipped it).</summary>
        internal static readonly System.Reflection.PropertyInfo[] BehaviourFields = typeof(BpaRule)
            .GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Where(p => p.Name != nameof(BpaRule.ID) && p.Name != nameof(BpaRule.Name) && p.Name != nameof(BpaRule.Description) && p.Name != nameof(BpaRule.FromModelAnnotation))
            .ToArray();

        /// <summary>Whether <paramref name="b"/> is the bundled rule <paramref name="a"/> and nothing else: the same id, and every behaviour field the
        /// same (text compared with its whitespace squashed, so a reflowed expression still matches).</summary>
        internal static bool SameRule(BpaRule a, BpaRule b)
        {
            if (!string.Equals(a.ID, b.ID, StringComparison.OrdinalIgnoreCase)) return false;
            foreach (var field in BehaviourFields)
            {
                var x = field.GetValue(a);
                var y = field.GetValue(b);
                if (x is string || y is string) { if (!string.Equals(Squash(x as string), Squash(y as string), StringComparison.Ordinal)) return false; }
                else if (!Equals(x, y)) return false;
            }
            return true;
        }

        private static string Squash(string expression) =>
            System.Text.RegularExpressions.Regex.Replace(expression ?? "", @"\s+", " ").Trim();

        /// <summary>Parse standard-format rules JSON — a rule array, OR a single rule object (treated as a
        /// one-element array). Tolerant of comments / trailing commas / string-encoded numbers and of extra or
        /// missing fields; throws <see cref="JsonException"/> on genuinely malformed JSON so a bad file is
        /// reported, not silently ignored. Drops entries with no ID (a rule needs a stable id) and de-dups by
        /// ID — last wins, so a re-listed rule overrides rather than double-fires. Callers that must REFUSE a
        /// duplicate id (the load op) use <see cref="ParseDetailed"/>.</summary>
        public static List<BpaRule> Parse(string json)
        {
            var (rules, _) = ParseDetailed(json);
            return rules;
        }

        /// <summary>Like <see cref="Parse"/>, plus the ids that appeared more than once. The returned list still
        /// last-wins (stored annotations stay loadable); the duplicate names are what lets a load refuse out loud.</summary>
        public static (List<BpaRule> rules, string[] duplicateIds) ParseDetailed(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return (new List<BpaRule>(), Array.Empty<string>());
            var trimmed = json.TrimStart();
            List<BpaRule> rules;
            if (trimmed.StartsWith("{"))   // a single inline rule object → one-element array
            {
                var one = JsonSerializer.Deserialize<BpaRule>(trimmed, JsonOpts);
                rules = one == null ? new List<BpaRule>() : new List<BpaRule> { one };
            }
            else rules = JsonSerializer.Deserialize<List<BpaRule>>(trimmed, JsonOpts) ?? new List<BpaRule>();
            var byId = new Dictionary<string, BpaRule>(StringComparer.OrdinalIgnoreCase);
            var dupes = new List<string>();
            foreach (var r in rules)
            {
                if (r == null || string.IsNullOrWhiteSpace(r.ID)) continue;
                if (byId.ContainsKey(r.ID) && !dupes.Exists(d => string.Equals(d, r.ID, StringComparison.OrdinalIgnoreCase)))
                    dupes.Add(r.ID);
                byId[r.ID] = r;
            }
            return (byId.Values.ToList(), dupes.ToArray());
        }
    }
}
