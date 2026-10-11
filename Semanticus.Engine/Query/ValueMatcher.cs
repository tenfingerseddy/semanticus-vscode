// Filter values are matched HERE, on the person's computer, and nowhere else (Kane, 26 Sep; the ratified consent
// promise: "rows or values from any table" never go).
//
// The engine reads the distinct values of the columns a question could narrow by, from the connected model, and
// looks for them in the question's words with the same typo-tolerant rule the word match uses (FuzzyWords). What Jev
// is told is only "column X has a value matching the words 'coast'", and the words are the question's own. The
// value itself, and every value that did not match, stays in this process.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Semanticus.Engine.Composer;

namespace Semanticus.Engine.Query
{
    /// <summary>One place a question's words met a column's values.</summary>
    internal sealed class ValueCandidate
    {
        /// <summary>f1, f2...: the id Jev's question is named by.</summary>
        public string Id;
        public QueryColumn Column;
        /// <summary>The question's own words that matched, as typed. The only part of a match that leaves.</summary>
        public string Words;
        /// <summary>The model's values those words matched. Never leaves.</summary>
        public List<object> Values = new List<object>();
        /// <summary>"value" when a value matched; "year" when the column holds years and the words are a year it does
        /// not hold, so only a comparison (after, from, before, up to) can use it.</summary>
        public string Kind = "value";
        public bool Exact;
        /// <summary>The words matched the value only through a slip of the fingers ("coats" for Coast), not exactly and not
        /// as a plural. Said in the note (G2, the ruling of 29 Sep).</summary>
        public bool Slip;
        /// <summary>Where the words sit in the question, in words, so overlaps can be settled.</summary>
        public int Start, Length;
    }

    internal static class ValueMatcher
    {
        /// <summary>A column with more distinct values than this is not read for matching.</summary>
        public const int MaxValuesPerColumn = 5000;

        /// <summary>The most candidates put to Jev in one question.</summary>
        public const int MaxCandidates = 12;

        /// <summary>Single words a value may not match on its own: they are the words of the question, not of the data.</summary>
        private static readonly HashSet<string> Common = new HashSet<string>(StringComparer.Ordinal)
        {
            "a", "an", "the", "and", "or", "of", "by", "for", "in", "on", "to", "per", "each", "all", "top", "bottom",
            "total", "sum", "count", "show", "me", "what", "which", "how", "many", "much", "is", "are", "was", "were",
            "with", "from", "after", "before", "since", "until", "not", "no", "yes", "none", "other", "unknown", "blank",
        };

        /// <summary>Words that make the number after them a count of rows, not a value: "top 10", "best 5".</summary>
        private static readonly HashSet<string> RankWords = new HashSet<string>(StringComparer.Ordinal)
        {
            "top", "bottom", "first", "last", "best", "worst", "highest", "lowest", "biggest", "smallest", "largest",
        };

        /// <summary>The columns worth reading values from: ones a person can see, that no relationship uses as a key,
        /// of a kind a filter can use.</summary>
        public static IEnumerable<QueryColumn> Readable(QueryCatalog cat) =>
            cat.Visible.Where(c => !c.Key && (c.Kind == "text" || c.Kind == "whole"));

        /// <summary>Find the question's words among each column's values. <paramref name="values"/> holds the distinct
        /// values read per column (by Id); a column missing from it was not read.</summary>
        public static List<ValueCandidate> Match(string question, QueryCatalog cat, IReadOnlyDictionary<string, object[]> values)
        {
            var words = FuzzyWords.Words(question).ToList();
            var found = new List<ValueCandidate>();
            if (words.Count == 0) return found;
            var rankedNumbers = new HashSet<int>();
            for (var i = 0; i + 1 < words.Count; i++)
                if (RankWords.Contains(words[i].Word) && IsNumber(words[i + 1].Word)) rankedNumbers.Add(i + 1);

            foreach (var col in Readable(cat))
            {
                if (!values.TryGetValue(col.Id, out var vals) || vals == null) continue;
                var byColumn = new List<ValueCandidate>();
                foreach (var v in vals)
                {
                    if (v == null || v is DBNull) continue;
                    var text = Convert.ToString(v, CultureInfo.InvariantCulture);
                    var full = FuzzyWords.Words(text).Select(w => w.Word).ToList();
                    if (full.Count == 0) continue;
                    // A year column's label ("CY 2008", "Year 2008") is the year the question names by its number alone (K21).
                    var yearOnly = YearOfLabel(col, full);
                    foreach (var vw in yearOnly == null ? new[] { full } : new[] { full, yearOnly })
                    {
                    if (vw.Count == 1 && Common.Contains(vw[0])) continue;
                    for (var start = 0; start + vw.Count <= words.Count; start++)
                    {
                        if (vw.Count == 1 && rankedNumbers.Contains(start)) continue;
                        var exact = true;
                        var slip = false;
                        var ok = true;
                        for (var k = 0; k < vw.Count && ok; k++)
                        {
                            var q = words[start + k].Word;
                            if (q == vw[k]) continue;
                            exact = false;
                            // Numbers match exactly or not at all: 2024 is not a slip for 2025. A plural is not a slip.
                            var plural = Plural(vw[k], q);
                            ok = !IsNumber(vw[k]) && (plural || FuzzyWords.Near(vw[k], q));
                            if (ok && !plural) slip = true;
                        }
                        if (!ok) continue;
                        var from = words[start].At;
                        var last = words[start + vw.Count - 1];
                        var typed = question.Substring(from, last.At + last.Word.Length - from);
                        var hit = byColumn.FirstOrDefault(c => c.Start == start && c.Length == vw.Count);
                        if (hit == null)
                            byColumn.Add(hit = new ValueCandidate { Column = col, Words = typed, Start = start, Length = vw.Count, Exact = exact, Slip = slip });
                        if (exact && !hit.Exact) { hit.Values.Clear(); hit.Exact = true; hit.Slip = false; }
                        if (exact == hit.Exact && !hit.Values.Contains(v)) hit.Values.Add(v);
                        // A slip stays said while any value taken was reached by one.
                        if (!hit.Exact && slip) hit.Slip = true;
                    }
                    }
                }
                // A match inside a longer match in the same column is the longer one's words: "Pine" inside "Pine Crest".
                byColumn.RemoveAll(c => byColumn.Any(o => o != c && o.Length > c.Length && o.Start <= c.Start && o.Start + o.Length >= c.Start + c.Length));
                found.AddRange(byColumn);

                // A year the column does not hold can still bound it: "since 2023" on a column of 2024 and 2025.
                if (col.Kind == "whole" && vals.Length > 0 && vals.All(IsYear))
                    for (var i = 0; i < words.Count; i++)
                    {
                        if (rankedNumbers.Contains(i) || !IsYear(words[i].Word)) continue;
                        if (byColumn.Any(c => c.Start == i)) continue;
                        found.Add(new ValueCandidate
                        {
                            Column = col, Words = question.Substring(words[i].At, words[i].Word.Length), Start = i, Length = 1,
                            Kind = "year", Exact = true, Values = { long.Parse(words[i].Word, CultureInfo.InvariantCulture) },
                        });
                    }
            }

            var kept = found
                .OrderByDescending(c => c.Length).ThenByDescending(c => c.Exact).ThenBy(c => c.Kind == "year")
                .ThenBy(c => c.Start).ThenBy(c => c.Column.Table, StringComparer.Ordinal).ThenBy(c => c.Column.Name, StringComparer.Ordinal)
                .Take(MaxCandidates)
                .OrderBy(c => c.Start).ThenBy(c => c.Column.Table, StringComparer.Ordinal).ThenBy(c => c.Column.Name, StringComparer.Ordinal)
                .ToList();
            for (var i = 0; i < kept.Count; i++) kept[i].Id = "f" + (i + 1).ToString(CultureInfo.InvariantCulture);
            return kept;
        }

        /// <summary>Find the words a person or an assistant gave for a filter among one column's values: an exact
        /// (case-blind) value first, then the typo-tolerant match. Returns the matched values; empty when none.</summary>
        public static List<object> Find(QueryColumn col, string words, object[] values)
        {
            var result = new List<object>();
            if (string.IsNullOrWhiteSpace(words) || values == null) return result;
            var w = words.Trim();
            foreach (var v in values)
            {
                if (v == null || v is DBNull) continue;
                if (string.Equals(Convert.ToString(v, CultureInfo.InvariantCulture), w, StringComparison.OrdinalIgnoreCase)) result.Add(v);
            }
            if (result.Count > 0 || col.Kind != "text") return result;
            var want = FuzzyWords.Words(w).Select(x => x.Word).ToList();
            foreach (var v in values)
            {
                if (v == null || v is DBNull) continue;
                var vw = FuzzyWords.Words(Convert.ToString(v, CultureInfo.InvariantCulture)).Select(x => x.Word).ToList();
                if (vw.Count == want.Count && vw.Count > 0 && vw.Zip(want, (a, b) => a == b || FuzzyWords.Near(a, b)).All(x => x)) result.Add(v);
            }
            return result;
        }

        /// <summary>Parse words as a value of a number column, for a comparison that need not name a value it holds.</summary>
        public static bool TryNumber(QueryColumn col, string words, out object value)
        {
            value = null;
            var w = (words ?? "").Trim();
            if (col.Kind == "whole" && long.TryParse(w, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l)) { value = l; return true; }
            if (col.Kind == "decimal" && double.TryParse(w, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) { value = d; return true; }
            return false;
        }

        private static bool IsNumber(string w) => w.Length > 0 && w.All(char.IsDigit);

        /// <summary>The year alone, when a label of a year column is words around one year ("CY 2008"): the label's other words
        /// hold no number. Null for any other value, and for a label that is the year alone.</summary>
        private static List<string> YearOfLabel(QueryColumn col, List<string> label)
        {
            if (col.Kind != "text" || label.Count < 2 || !QueryPeriods.IsYear(col)) return null;
            var years = label.Where(IsYear).ToList();
            return years.Count == 1 && label.Count(IsNumber) == 1 ? years : null;
        }

        /// <summary>One word the plural of the other: "bike" is the value "Bikes", "accessory" is "Accessories". The typo
        /// rule takes a four-letter word as meant, so "bike sales" once lost its filter.</summary>
        private static bool Plural(string a, string b)
        {
            static bool Of(string plural, string one) => one.Length >= 3 && (plural == one + "s" || plural == one + "es"
                || (one.EndsWith("y", StringComparison.Ordinal) && plural == one.Substring(0, one.Length - 1) + "ies"));
            return Of(a, b) || Of(b, a);
        }

        private static bool IsYear(string w) => w.Length == 4 && IsNumber(w) && int.Parse(w, CultureInfo.InvariantCulture) is >= 1900 and <= 2100;

        private static bool IsYear(object v)
        {
            try
            {
                if (v == null || v is DBNull) return true;
                var l = Convert.ToInt64(v, CultureInfo.InvariantCulture);
                return l >= 1900 && l <= 2100;
            }
            catch { return false; }
        }
    }
}
