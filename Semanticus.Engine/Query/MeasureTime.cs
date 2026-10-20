// A measure whose meaning already holds a period ("Net Sales PY", "Sales YTD", "Sales last 12 months") read from its DAX on this
// computer. When the engine has already read the words' period ("last year" as 2025), such a measure must not move time a second
// time: [Net Sales PY] under Year 2025 is 2024's figure said as 2025's (verifier round 2, Query UAT). The measure it wraps is
// shown instead; with no clear base the answer is flagged, never a figure for the wrong period.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Semanticus.Engine.Query
{
    internal static class MeasureTime
    {
        private const string Shifts = "DATEADD|SAMEPERIODLASTYEAR|PREVIOUS(?:YEAR|QUARTER|MONTH|DAY)|NEXT(?:YEAR|QUARTER|MONTH|DAY)|PARALLELPERIOD"
            + "|DATES(?:YTD|QTD|MTD)|TOTAL(?:YTD|QTD|MTD)|DATESINPERIOD";
        private static readonly Regex Shift = new Regex(@"\b(" + Shifts + @")\s*\(", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex ShiftArg = new Regex(@"^\s*(?:" + Shifts + @")\s*\(", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex Wrapper = new Regex(@"^\s*(?:CALCULATE|TOTALYTD|TOTALQTD|TOTALMTD)\s*\((.*)\)\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);
        private static readonly Regex BareRef = new Regex(@"^\s*\[([^\]]+)\]\s*$", RegexOptions.Compiled);
        private static readonly Regex Strings = new Regex("\"(?:[^\"]|\"\")*\"", RegexOptions.Compiled);
        private static readonly Regex Comments = new Regex(@"/\*.*?\*/|//[^\r\n]*|--[^\r\n]*", RegexOptions.Compiled | RegexOptions.Singleline);
        // Only read when the measure has no DAX to read: a name that says the period in a word of its own.
        private static readonly Regex NameSays = new Regex(@"\b(PY|LY|SPLY|YTD|QTD|MTD|prior|previous|rolling|trailing|last year)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>True when the measure's own meaning already contains a period.</summary>
        public static bool Moves(QueryMeasure m, QueryCatalog cat) => Moves(m, cat, 0);

        private static bool Moves(QueryMeasure m, QueryCatalog cat, int depth)
        {
            if (string.IsNullOrWhiteSpace(m.Expression)) return NameSays.IsMatch(m.Name ?? "");
            var text = Clean(m.Expression);
            if (Shift.IsMatch(text)) return true;
            // A measure that only points at another one that moves is that measure.
            var bare = BareRef.Match(text);
            if (bare.Success && depth < 4)
            {
                var next = Find(cat, bare.Groups[1].Value);
                return next != null && !ReferenceEquals(next, m) && Moves(next, cat, depth + 1);
            }
            return false;
        }

        /// <summary>The measure a moving measure wraps and nothing else (CALCULATE ( [Net Sales], DATEADD ( ... ) ), TOTALYTD ( [Net Sales], ... )),
        /// followed down to one that does not move. Null when the measure is anything else (a growth ratio, a filter beside the shift, variables).</summary>
        public static QueryMeasure Base(QueryMeasure m, QueryCatalog cat)
        {
            var at = m;
            for (var depth = 0; depth < 5; depth++)
            {
                var text = Clean(at.Expression ?? "");
                QueryMeasure next;
                var bare = BareRef.Match(text);
                if (bare.Success) next = Find(cat, bare.Groups[1].Value);
                else
                {
                    var w = Wrapper.Match(text);
                    if (!w.Success) return null;
                    var args = Split(w.Groups[1].Value);
                    if (args == null || args.Count < 2) return null;
                    var first = BareRef.Match(args[0]);
                    if (!first.Success) return null;
                    var tail = args.Skip(1).ToList();
                    var total = Regex.IsMatch(text, @"^\s*TOTAL", RegexOptions.IgnoreCase);
                    // Every other argument must be a shift of time; a filter beside it (a region) would be lost by the swap.
                    if (total ? tail.Count > 1 : !tail.All(a => ShiftArg.IsMatch(a))) return null;
                    next = Find(cat, first.Groups[1].Value);
                }
                if (next == null || ReferenceEquals(next, at)) return null;
                if (!Moves(next, cat, 0)) return next;
                at = next;
            }
            return null;
        }

        /// <summary>The plain line when the measure shown is swapped for the one it wraps.</summary>
        public static string Swapped(QueryMeasure from, QueryMeasure to, IEnumerable<string> words) =>
            $"{from.Name} already moves the date, and {Quoted(words)} was read as the period, so {to.Name} is shown for that period.";

        /// <summary>The line when a moving measure meets a period the words name and no base measure can be found.</summary>
        public static string Cannot(QueryMeasure m, IEnumerable<string> words) =>
            $"{m.Name} already moves the date, and {Quoted(words)} does too, so the figure would be for the wrong period. Nothing was run. "
            + "Ask for the period on its own, or for the number without a period in its name.";

        private static string Quoted(IEnumerable<string> words) => string.Join(" and ", words.Select(w => "'" + w + "'"));

        private static QueryMeasure Find(QueryCatalog cat, string name) =>
            cat.Measures.FirstOrDefault(x => string.Equals(x.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));

        private static string Clean(string dax) => Comments.Replace(Strings.Replace(dax, "\"\""), " ").Trim();

        /// <summary>The top-level, comma-separated arguments of a call's inside; null when the brackets do not close.</summary>
        private static List<string> Split(string inside)
        {
            var parts = new List<string>();
            int depth = 0, start = 0;
            var quote = false;
            for (var i = 0; i < inside.Length; i++)
            {
                var c = inside[i];
                if (c == '\'') quote = !quote;
                if (quote) continue;
                if (c == '(' || c == '[') depth++;
                else if (c == ')' || c == ']') { depth--; if (depth < 0) return null; }
                else if (c == ',' && depth == 0) { parts.Add(inside.Substring(start, i - start)); start = i + 1; }
            }
            if (depth != 0) return null;
            parts.Add(inside.Substring(start));
            return parts;
        }
    }
}
