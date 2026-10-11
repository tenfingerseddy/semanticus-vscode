// G4 by shape (review-fix round 7, item 2). Round 6 stopped a fiscal period the reading did not use, by three words (fiscal,
// financial, FY). A tax year, an academic year, a school year, a trading year, or "fisacl" walked straight past it and ran as the
// calendar year, under a statement that said nothing of the difference. The list could only grow, so the guard now reads the shape:
//
//   a WORD BEFORE A PERIOD NOUN (year, quarter, month, period, week) that the reading does not use, that the model has no column
//   for, and that is not one of the plain words a person puts in front of a period for the calendar ("this", "last", "each",
//   "calendar", a number, a rank, a month's name), names a period this reading does not have.
//
// The plain words are a short list of structure, not of the qualifiers that matter: a new kind of year (tax, school, trading,
// reporting, a word nobody has thought of, a typo of any of them) is unexplained until a column of the reading or of the model
// says what it is. A slip of a plain word ("thsi year", "calender year") is read as the plain word. A qualifier the question
// negates ("calendar year 2025, not the fiscal year") is what the person did not ask for, so it stops nothing.
//
// One more shape, for the period with no noun: a year number beside a word that is a slip of a kind of year ("fisacl 2025",
// "FY25"). A word before a bare year is otherwise never read ("in december 2025", "revenue 2025").
//
// Deterministic and local. Nothing here asks Jev.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Semanticus.Engine.Composer;

namespace Semanticus.Engine.Query
{
    internal static class PeriodQualifiers
    {
        /// <summary>A qualified period the question names: the words as typed, and the word that qualifies it.</summary>
        internal sealed class Named
        {
            public string Text;
            public string Qualifier;
            /// <summary>The qualifier is a fiscal, financial or FY word (or a slip of one): the model's fiscal columns answer it.</summary>
            public bool Fiscal;
        }

        private static readonly HashSet<string> Nouns = new HashSet<string>(StringComparer.Ordinal)
        {
            "year", "years", "quarter", "quarters", "month", "months", "period", "periods", "week", "weeks",
        };

        /// <summary>The kinds of year that a bare year number is read with ("fisacl 2025"): only used beside a year number.</summary>
        private static readonly string[] Kinds = { "fiscal", "financial", "academic", "school", "tax", "trading", "accounting" };

        private static readonly string[] FiscalGroup = { "fiscal", "financial", "fy" };

        /// <summary>Plain words that stand before a period for the calendar or for the sentence: not qualifiers of a kind of year.</summary>
        private static readonly HashSet<string> Plain = new HashSet<string>(StringComparer.Ordinal)
        {
            // the sentence
            "a", "an", "the", "this", "that", "these", "those", "each", "every", "any", "all", "per", "by", "in", "on", "at", "of", "for", "to", "from",
            "into", "over", "under", "across", "during", "within", "between", "among", "through", "throughout", "since", "until", "till", "before", "after",
            "around", "about", "than", "then", "vs", "versus", "and", "or", "nor", "but", "not", "no", "non", "with", "without", "as", "is", "are", "was",
            "were", "be", "been", "do", "does", "did", "has", "have", "had", "will", "would", "could", "should", "can", "may", "it", "its", "we", "our", "us",
            "my", "your", "their", "his", "her", "they", "you", "i", "me", "what", "which", "who", "when", "where", "how", "why", "if", "while", "so", "such",
            "other", "another", "one", "both", "either", "some", "many", "much", "more", "most", "less", "least", "few", "fewer", "several", "only", "just",
            "also", "even", "still", "yet", "now", "here", "there", "up", "down", "out", "again", "roughly", "approximately", "nearly", "almost", "exactly",
            // relative and whole
            "next", "last", "previous", "prior", "current", "present", "past", "coming", "recent", "latest", "earliest", "first", "second", "third", "fourth",
            "fifth", "sixth", "final", "following", "preceding", "same", "whole", "entire", "full", "complete", "single", "total", "overall", "combined",
            "half", "quarterly", "monthly", "weekly", "yearly", "annual", "calendar", "calender", "common", "regular", "typical", "usual", "normal",
            // a rank
            "top", "bottom", "best", "worst", "better", "worse", "highest", "lowest", "biggest", "smallest", "largest", "fastest", "slowest", "busiest",
            "quietest", "strongest", "weakest", "good", "bad", "great", "strong", "weak", "slow", "busy", "quiet", "peak", "record", "new", "old", "early", "late",
            // a count
            "zero", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten", "eleven", "twelve", "thirteen", "fourteen", "twenty", "thirty",
            "sixty", "ninety", "hundred", "couple", "dozen",
            // the period words themselves
            "year", "quarter", "month", "week", "period", "day", "days", "date", "time", "s",
        };

        private static readonly string[] MonthNames = { "jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec" };
        private static readonly string[] Weekdays = { "mon", "tue", "wed", "thu", "fri", "sat", "sun" };

        private static readonly Regex Negation = new Regex(
            @"\b(not|non|no|instead\s+of|rather\s+than|excluding|without|ignoring|except|never)\s+(?:the\s+|a\s+|any\s+)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex Fy = new Regex(@"\bfy\s?\d{2,4}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static bool IsYearNumber(string w) => Regex.IsMatch(w, @"^(19|20)\d{2}$");

        private static bool IsNoun(string w) => Nouns.Contains(w) || (w.Length >= 5 && Nouns.Any(n => n.Length >= 5 && FuzzyWords.Near(n, w)));

        /// <summary>Slips of "year" and "week" one letter off ("yeer", "wek"), taken as the noun only beside a kind of year.</summary>
        private static bool SlipOfShortNoun(string w) =>
            w.Length >= 3 && new[] { "year", "week" }.Any(n => w[0] == n[0] && FuzzyWords.Distance(n, w) == 1);

        private static bool IsKind(string w) =>
            Kinds.Contains(w) || (w.Length >= 5 && Kinds.Any(k => k.Length >= 5 && FuzzyWords.Near(k, w))) || FiscalGroup.Contains(w)
            || (w.Length >= 5 && FiscalGroup.Any(k => k.Length >= 5 && FuzzyWords.Near(k, w)));

        private static bool IsFiscalWord(string w) => FiscalGroup.Contains(w) || (w.Length >= 5 && FiscalGroup.Any(k => k.Length >= 5 && FuzzyWords.Near(k, w)));

        private static bool PlainWord(string w)
        {
            if (w.Length <= 1 || Plain.Contains(w)) return true;
            if (w.Any(char.IsDigit)) return true;
            if (w.Length >= 5 && w.EndsWith("est", StringComparison.Ordinal)) return true;
            if (MonthNames.Any(m => w.StartsWith(m, StringComparison.Ordinal)) && w.Length <= 9) return true;
            if (Weekdays.Any(d => w.StartsWith(d, StringComparison.Ordinal)) && w.Length <= 9) return true;
            // A slip of a plain word: "thsi year", "calender year".
            return w.Length >= 4 && Plain.Any(p => p.Length >= 4 && FuzzyWords.Near(p, w) && !IsKind(w));
        }

        /// <summary>A word of a column's own name that may stand before its period noun and leave it the calendar's: one of the plain
        /// words, exactly ("Calendar Year", "Current Year"). A name is not typed in a hurry, so no slip is read as a plain word here.</summary>
        internal static bool PlainInName(string w) => Plain.Contains(w) && !IsKind(w);

        /// <summary>A kind of year (fiscal, financial, FY, tax, academic, school, trading, accounting, or a slip of one).</summary>
        internal static bool KindInName(string w) => IsKind(w);

        private static bool Same(string known, string typed) =>
            known == typed || typed == known + "s" || typed == known + "es" || known == typed + "s" || FuzzyWords.Near(known, typed);

        private static bool NamesPeriod(QueryColumn c) =>
            c.Kind == "date" || QuerySplitShortlist.NameWords(c.Name).Any(w => Nouns.Contains(w) || w == "day" || w == "date" || w == "fy");

        /// <summary>The first qualified period the question names that nothing in the reading or the model explains, or null.</summary>
        public static Named Unexplained(string question, QuerySpec spec, JevQueryReader.Reading reading, QueryCatalog cat)
        {
            var words = FuzzyWords.Words(question ?? "").ToList();
            var hits = new List<(Named Named, int At)>();
            for (var i = 1; i < words.Count; i++)
            {
                var (w, at) = words[i];
                var (q, qAt) = words[i - 1];
                if (q.Length <= 1 || IsNoun(q) || q.Any(char.IsDigit)) continue;
                // A qualifier and its period noun: "tax year". A noun repeated over itself ("month over month") is a comparison.
                var isNoun = IsNoun(w) || (SlipOfShortNoun(w) && IsKind(q));
                if (isNoun)
                {
                    if (i + 2 < words.Count && new[] { "over", "on", "to" }.Contains(words[i + 1].Word) && IsNoun(words[i + 2].Word)) continue;
                    hits.Add((new Named { Text = Slice(question, qAt, at + w.Length), Qualifier = q, Fiscal = IsFiscalWord(q) }, qAt));
                }
                // A kind of year beside a bare year number: "fisacl 2025", "financial 2025".
                else if (IsYearNumber(w) && IsKind(q))
                    hits.Add((new Named { Text = Slice(question, qAt, at + w.Length), Qualifier = q, Fiscal = IsFiscalWord(q) }, qAt));
            }
            foreach (Match m in Fy.Matches(question ?? ""))
                hits.Add((new Named { Text = m.Value, Qualifier = "fy", Fiscal = true }, m.Index));
            if (hits.Count == 0) return null;

            var explainedBy = ExplainingWords(spec, reading, cat);
            foreach (var (named, at) in hits.OrderBy(h => h.At))
            {
                var before = (question ?? "").Substring(0, at);
                if (Negation.IsMatch(before)) continue;
                if (Explained(named, explainedBy)) continue;
                return named;
            }
            return null;
        }

        private static string Slice(string text, int from, int to) => text.Substring(from, Math.Min(to, text.Length) - from);

        private static bool Explained(Named n, (HashSet<string> Used, HashSet<string> Model) words)
        {
            var q = n.Qualifier;
            // A kind of year is explained only by a column the reading uses; the rest by any word of the model or the reading.
            var group = n.Fiscal ? FiscalGroup : new[] { q };
            if (words.Used.Any(u => group.Any(g => Same(g, u)))) return true;
            if (IsKind(q)) return false;
            if (PlainWord(q)) return true;
            return words.Model.Any(m => group.Any(g => Same(m, g)));
        }

        /// <summary>The words of the columns the reading uses (and of the words the question's values matched), and the words of every
        /// name in the model other than a column of periods the reading leaves out (that one is "the model has this, and it is unused").</summary>
        private static (HashSet<string> Used, HashSet<string> Model) ExplainingWords(QuerySpec spec, JevQueryReader.Reading reading, QueryCatalog cat)
        {
            var used = new HashSet<string>(StringComparer.Ordinal);
            var usedColumns = spec.GroupBy.Concat(spec.Filters.Select(f => f.Column)).Concat(new[] { spec.SortColumn }.Where(c => c != null)).Distinct().ToList();
            foreach (var c in usedColumns)
                foreach (var w in QuerySplitShortlist.NameWords(c.Name).Concat(c.Synonyms.SelectMany(QuerySplitShortlist.NameWords))) used.Add(w);
            foreach (var m in spec.Measures.Concat(new[] { spec.TopBy, spec.SortMeasure }.Where(m => m != null)))
                foreach (var w in QuerySplitShortlist.NameWords(m.Name)) used.Add(w);
            foreach (var (candidate, _) in reading?.Filters ?? new List<(ValueCandidate Candidate, string Op)>())
                foreach (var w in FuzzyWords.Words(candidate.Words)) used.Add(w.Word);
            var model = new HashSet<string>(StringComparer.Ordinal);
            var calendarTables = new HashSet<string>(cat.Columns.Where(NamesPeriod).Select(c => c.Table), StringComparer.Ordinal);
            foreach (var c in cat.Visible.Where(c => !NamesPeriod(c) || usedColumns.Contains(c)))
                foreach (var w in QuerySplitShortlist.NameWords(c.Name).Concat(c.Synonyms.SelectMany(QuerySplitShortlist.NameWords))) model.Add(w);
            foreach (var m in cat.VisibleMeasures)
                foreach (var w in QuerySplitShortlist.NameWords(m.Name).Concat(m.Synonyms.SelectMany(QuerySplitShortlist.NameWords))) model.Add(w);
            foreach (var t in cat.Columns.Select(c => c.Table).Concat(cat.Measures.Select(m => m.Table)).Distinct().Where(t => !calendarTables.Contains(t)))
                foreach (var w in QuerySplitShortlist.NameWords(t).Concat((cat.TableSynonyms.TryGetValue(t, out var syn) ? syn : Array.Empty<string>()).SelectMany(QuerySplitShortlist.NameWords))) model.Add(w);
            return (used, model);
        }

        /// <summary>The columns of periods the model has for this qualifier (its words are in the name) that the reading does not use.</summary>
        public static List<QueryColumn> ColumnsFor(Named n, QuerySpec spec, QueryCatalog cat)
        {
            var used = spec.GroupBy.Concat(spec.Filters.Select(f => f.Column)).ToList();
            var group = n.Fiscal ? FiscalGroup : new[] { n.Qualifier };
            return cat.Visible.Where(c => !c.Key && !used.Contains(c) && NamesPeriod(c)
                && QuerySplitShortlist.NameWords(c.Name).Any(w => group.Any(g => Same(g, w)))).ToList();
        }
    }
}
