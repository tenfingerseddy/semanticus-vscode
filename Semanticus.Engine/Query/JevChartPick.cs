// queries.chart_kind: Jev's visual pick for a Query result (docs/queries-chart-pick-bar.md). OFF by default.
//
// The visual rules (Semanticus.VSCode/webview/src/query-model.ts queryVisualFits, lifted from Graphicus at 84812e04) always pick first
// and say which visuals can draw the shape at all (`fits`). This point may only choose among those, or say the rules' pick is fine, so Jev can be wrong
// but never broken. What leaves the machine is the question's words and the result's SHAPE: each split's display
// name, whether it is a date and how many values it has, the display name of each number, how many rows, whether the numbers share a
// unit and whether any is negative. Never a row, never a value. 1.6 (schema 2): the visual set replaced the old chart kinds.
//
// Jev writes no text (golden rule 1). The reason shown beside a pick is one fixed sentence per chart kind, written here.
//
// THE BAR. JevPicks is false until the replay in tools/jev-replay/chart clears docs/queries-chart-pick-bar.md (beats
// the rules, McNemar exact p < 0.05, no more harmful picks), and only Kane flips it, after the numbers are in the
// change log. While it is false nothing is ever asked, whatever the point's runtime switch says.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Semanticus.Decisions;

namespace Semanticus.Engine.Query
{
    /// <summary>pick_chart_kind / pickChartKind: what the Studio (or an assistant) sends.</summary>
    public sealed class ChartPickAsk
    {
        /// <summary>The question in plain words, when there were any. A result built from parts has none.</summary>
        public string Words { get; set; }
        public ChartPickShape Shape { get; set; }
        /// <summary>The kinds the hard rules leave, in the order the rules offer them.</summary>
        public string[] Fits { get; set; }
        /// <summary>The rules' own pick: the fallback for every case where Jev does not pick.</summary>
        public string RulesPick { get; set; }
    }

    public sealed class ChartPickShape
    {
        public ChartPickDim[] Dims { get; set; }
        public int Measures { get; set; }
        public int Rows { get; set; }
        /// <summary>The display name of each number (a model object name, like a split's), so the question names what it asks about.</summary>
        public string[] MeasureNames { get; set; }
        /// <summary>Every number is in one unit (same format), so two of them can be set against each other.</summary>
        public bool SameUnit { get; set; }
        /// <summary>A negative number is in the result (a bullet draws from zero).</summary>
        public bool Negatives { get; set; }
    }

    public sealed class ChartPickDim
    {
        public string Name { get; set; }
        public bool IsDate { get; set; }
        public int Distinct { get; set; }
    }

    /// <summary>The answer. <see cref="By"/> is "jev" only when Jev picked a kind the rules left, above the bar.</summary>
    public sealed class ChartPick
    {
        public const string ByJev = "jev";
        public const string ByRules = "rules";

        public string Kind { get; set; }
        public string By { get; set; }
        /// <summary>Engine-authored: the fixed sentence for <see cref="Kind"/>. Never Jev's words.</summary>
        public string Reason { get; set; }
        public double? Confidence { get; set; }
        /// <summary>Why Jev did not pick, in plain words, when it did not.</summary>
        public string Note { get; set; }
    }

    public static class JevChartPick
    {
        /// <summary>Has the queries.chart_kind replay cleared its bar? No: no run has been made. Only Kane flips this.</summary>
        internal const bool JevPicks = false;

        public const string Question = "chart_kind";

        /// <summary>The neutral option: "the rules' pick is fine". It maps to the rules' pick, by the rules.</summary>
        public const string RulesOption = "rules";

        /// <summary>Every visual the Query page can draw, as query-model.ts names them (the shared visual set).</summary>
        public static readonly IReadOnlyList<string> Kinds = new[] { "total", "bars", "groups", "line", "variance", "dumbbell", "bullet", "slope", "multiples", "sparktable", "table" };

        public const int MaxDims = 8;
        public const int MaxNameChars = 200;
        public const int MaxWordsChars = 2000;

        internal const string OffNote = "Jev's visual pick is off until it clears its bar.";
        internal const string SwitchedOffNote = "Jev's visual pick is switched off for this model.";
        internal const string NoFitsNote = "The visual rules left no visual to choose from, so the rules' pick stands.";
        internal const string OneFitNote = "Only one visual fits this result, so there was nothing for Jev to choose.";
        internal const string NoModelNote = "Jev was not asked: no model is open.";
        internal const string NeutralNote = "Jev said the rules' pick is fine.";

        /// <summary>The one sentence shown beside each kind. Plain, fixed, and the engine's own. It does not name the kind:
        /// the kind travels in its own field, and Queries already writes "Jev picked Table:" before it.</summary>
        public static string ReasonFor(string kind) => kind switch
        {
            "total" => "One number reads best on its own, large, with what it is set against.",
            "bars" => "Ranked bars make it easy to compare the members side by side, biggest first.",
            "groups" => "A cluster of bars for each member, one bar in it for each value of the second field, shows both splits at once.",
            "line" => "Numbers over time read best as a line from left to right.",
            "variance" => "Bars from a zero line show how far each member is from the other number.",
            "dumbbell" => "A dot for each value and the gap between them show where the two numbers differ.",
            "bullet" => "Each value against its mark shows who is short and who is over.",
            "slope" => "A line for each member from the first value to the second shows who moved most.",
            "multiples" => "A small trend for each member keeps every trend readable on one scale.",
            "sparktable" => "A trend beside each member's figures shows many members at once.",
            "table" => "The rows themselves are the clearest way to read this.",
            _ => "The visual rules picked this visual.",
        };

        /// <summary>The kinds as given, known ones only, each once, in the rules' order.</summary>
        public static List<string> CleanFits(IEnumerable<string> fits) =>
            (fits ?? Array.Empty<string>()).Where(k => k != null && Kinds.Contains(k)).Distinct(StringComparer.Ordinal).ToList();

        /// <summary>The declared snapshot: the words, when there are any, and the result's shape. No rows, no values.</summary>
        public static IReadOnlyDictionary<string, object> Snapshot(string words, ChartPickShape shape)
        {
            var point = DecisionRegistry.Require(DecisionRegistry.QueriesChartKind);
            var dims = (shape?.Dims ?? Array.Empty<ChartPickDim>()).Where(d => d != null).ToList();
            if (dims.Count > MaxDims)
                throw new ArgumentException($"The result has {dims.Count} splits; the chart pick reads at most {MaxDims}.");
            var fields = new List<KeyValuePair<string, object>>();
            var w = (words ?? "").Trim();
            if (w.Length > MaxWordsChars)
                throw new ArgumentException($"The question is longer than {MaxWordsChars} characters.");
            if (w.Length > 0) fields.Add(new KeyValuePair<string, object>("question_words", w));
            fields.Add(new KeyValuePair<string, object>("result_shape", new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["dims"] = dims.Select(d =>
                {
                    var name = (d.Name ?? "").Trim();
                    if (name.Length > MaxNameChars) throw new ArgumentException($"A split's name is longer than {MaxNameChars} characters.");
                    return (object)new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        ["name"] = name,
                        ["is_date"] = d.IsDate,
                        ["distinct"] = Math.Max(0, d.Distinct),
                    };
                }).ToList(),
                ["measures"] = Math.Max(0, shape?.Measures ?? 0),
                ["measure_names"] = (shape?.MeasureNames ?? Array.Empty<string>()).Where(n => !string.IsNullOrWhiteSpace(n)).Take(MaxDims).Select(n =>
                {
                    var name = n.Trim();
                    if (name.Length > MaxNameChars) throw new ArgumentException($"A number's name is longer than {MaxNameChars} characters.");
                    return (object)name;
                }).ToList(),
                ["rows"] = Math.Max(0, shape?.Rows ?? 0),
                ["same_unit"] = shape?.SameUnit ?? false,
                ["has_negatives"] = shape?.Negatives ?? false,
            }));
            var snapshot = DecisionSnapshot.Build(point, fields);
            DecisionSnapshot.RequireWithin(snapshot, 16 * 1024);
            return snapshot;
        }

        /// <summary>The offer: the fitting kinds in an order seeded from the snapshot (so the rules' order is not a
        /// hint, and the same ask is the same digest), then the neutral option last.</summary>
        public static IReadOnlyList<DecisionQuestion> Questions(IReadOnlyDictionary<string, object> snapshot, IReadOnlyList<string> fits)
        {
            var seed = DecisionDigest.Sha256Hex(DecisionDigest.CanonicalSnapshotJson(snapshot));
            var ordered = fits.OrderBy(k => DecisionDigest.Sha256Hex(seed + ":" + k), StringComparer.Ordinal).ToList();
            var options = ordered.Select(k => new DecisionOption(k, Criterion(k))).ToList();
            options.Add(new DecisionOption(RulesOption, "none of these is clearly better than the visual the rules would pick"));
            return new[]
            {
                new DecisionQuestion(Question, DecisionQuestionKind.Choice,
                    "Which visual lets a model developer see the answer to the question in question_words in this result? The result is shaped as result_shape describes, and it names each split and each number.",
                    options),
            };
        }

        private static string Criterion(string kind) => kind switch
        {
            "total" => "one number shown large on its own, with what it is set against beside it",
            "bars" => "bars side by side, one per member, biggest first",
            "groups" => "a cluster of bars for each member, one bar in it for each value of a second field",
            "line" => "a line across dates from left to right",
            "variance" => "bars from a zero line, one per member, showing how far one number is from the other",
            "dumbbell" => "two dots per member joined by a line, one dot per value, so the gap shows",
            "bullet" => "a bar per member against a mark, to see who is under or over it",
            "slope" => "one line per member from its first value to its second",
            "multiples" => "one small trend per member, all on one scale",
            "sparktable" => "a table of members with a small trend beside each one's figures",
            "table" => "the rows as they are, one line each",
            _ => kind,
        };

        /// <summary>The seeded order a request offers, for the tests and the replay.</summary>
        internal static IReadOnlyList<string> OfferedOrder(IReadOnlyList<DecisionQuestion> questions) =>
            questions[0].Options.Select(o => o.Id).ToList();

        internal static ChartPick Rules(string rulesPick, string note) => new ChartPick
        {
            Kind = rulesPick,
            By = ChartPick.ByRules,
            Reason = ReasonFor(rulesPick),
            Note = note,
        };

        /// <summary>Read Jev's answer. Only a kind in <paramref name="fits"/>, above the bar, is Jev's pick; anything
        /// else is the rules' pick with a note saying why.</summary>
        public static ChartPick Read(DecisionAnswers answers, double bar, IReadOnlyList<string> fits, string rulesPick)
        {
            var a = answers?.For(Question);
            if (a == null || a.Choice == null) return Rules(rulesPick, "Jev did not answer, so the rules' pick stands.");
            if (a.Confidence < bar)
                return WithConfidence(Rules(rulesPick, "Jev was not sure enough, so the rules' pick stands."), a.Confidence);
            if (string.Equals(a.Choice, RulesOption, StringComparison.Ordinal))
                return WithConfidence(Rules(rulesPick, NeutralNote), a.Confidence);
            if (!fits.Contains(a.Choice, StringComparer.Ordinal))
                return WithConfidence(Rules(rulesPick, "Jev named a visual the rules had ruled out, so it was not used and the rules' pick stands."), a.Confidence);
            return new ChartPick { Kind = a.Choice, By = ChartPick.ByJev, Reason = ReasonFor(a.Choice), Confidence = a.Confidence };
        }

        private static ChartPick WithConfidence(ChartPick p, double c) { p.Confidence = c; return p; }
    }
}
