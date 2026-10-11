// The engine's guards on its OWN reading (the ruling "Jev data questions v2", 29 Sep). They replace the word coverage
// check, which tried to judge what a word MEANS by which word it was (it let 13 of the verifier's 20 wrong numbers
// through, and stopped 22 right ones). What stays is what the engine can prove about the query it built, never about a
// word's meaning:
//
//   G1  a part the others on the same column imply or cancel: an "is not" beside an "is", an "is" inside or against a
//       bound, a bound another bound makes moot. One of them was misread, so nothing runs and both are named. A real
//       range ("from 2024", "up to 2025") is never refused.
//   G2  a filter value matched only through a slip of the fingers ("coats" for Coast) is said in the note (Notes).
//   G3  "last year to date" is read as the same span of the earlier period, or goes to unsupported (QueryPeriods).
//   G4  a qualifier before a period noun (tax year, school year, fisacl 2025) that the reading does not use and the model has no
//       column for: the reading filters or splits by the calendar, and nothing on the screen would say the other kind of year was
//       dropped (round 6, D2; by shape since round 7, PeriodQualifiers). A fact of the model, not a word.
//   G5  a share or a percent of a count (orders, customers) answered by a ratio of amounts: Return Rate % is returned value over
//       sales value, and "the share of orders that had a return" is another number (round 6, D1; by grain since round 7).
//
// And two reasons kept from the old check, both about what the engine did with the data rather than about words:
//   a month or a quarter with no year, on data holding more than one year, would add the same month of every year;
//   a value in a column two tables hold, with both filtered and no table named, could be either.
//
// Deterministic and local. Nothing here asks Jev.
//
// SAID, NOT STOPPED (K16, Kane 8 Oct: "We should always return something"). Every guard here now returns the plain line the
// answer says beside the number (DataAnswer.Assumption); none of them stops a run. A bound word read as one value is fixed
// to the range it names (FixBounds). What still stops is in LocalEngine: filters that leave nothing, a query that does not
// check out, a query that failed.

using System;
using System.Collections.Generic;
using System.Linq;
using Semanticus.Engine.Composer;

namespace Semanticus.Engine.Query
{
    internal static class QueryGuards
    {
        /// <summary>The lines a reading is stopped with by what the engine did with the calendar and with twin columns, one
        /// plain sentence each; empty when nothing is wrong. G1 is asked earlier (before any time is read).
        /// <paramref name="years"/> is how many years the data holds, when read.</summary>
        public static List<string> Check(string question, JevQueryReader.Reading reading, QueryCatalog cat, int? years)
        {
            var lines = new List<string>();
            if (reading == null) return lines;
            lines.AddRange(BoundPeriod(question, reading));
            lines.AddRange(Calendar(question, reading, cat, years));
            return lines.Distinct().ToList();
        }

        // ---- G1 ---------------------------------------------------------------------------------------------------

        private static bool Lower(string op) => op == QueryOps.After || op == QueryOps.From;
        private static bool Upper(string op) => op == QueryOps.Before || op == QueryOps.UpTo;

        /// <summary>Parts on one column that cannot all be what was meant. Works on the candidates Jev settled, by their
        /// words, so the line names what the question said.</summary>
        internal static IEnumerable<string> G1(JevQueryReader.Reading reading, QueryCatalog cat, string question = null)
        {
            foreach (var g in reading.Filters.GroupBy(f => f.Candidate.Column))
            {
                var parts = g.Select(x => (x.Candidate, x.Op)).ToList();
                var kept = parts.Where(p => p.Op == QueryOps.Is).ToList();
                var left = parts.Where(p => p.Op == QueryOps.IsNot).ToList();
                var lows = parts.Where(p => Lower(p.Op)).ToList();
                var highs = parts.Where(p => Upper(p.Op)).ToList();
                (ValueCandidate, string)? a = null, b = null;
                if (kept.Count > 0 && left.Count > 0) { a = left[0]; b = kept[0]; }
                else if (kept.Count > 0 && (lows.Count > 0 || highs.Count > 0)) { a = kept[0]; b = (lows.Count > 0 ? lows : highs)[0]; }
                else if (lows.Count > 1) { a = lows[0]; b = lows[1]; }
                else if (highs.Count > 1) { a = highs[0]; b = highs[1]; }
                if (a != null) yield return $"Read as {Part(a.Value, cat)} and {Part(b.Value, cat)}.";
            }
        }

        /// <summary>A bound word the words carry right before a value ("from 2024", "since 2023", "before 2025") that Jev read as
        /// "is": the words name a range, so the filter becomes that range (K16: the most likely reading runs; it was a stop).
        /// Only for a column with an order (a year, a date, a number): "from Australia" says where, not a bound. "from 2024 and
        /// 2025" lists values, so it stays as it is.</summary>
        internal static void FixBounds(JevQueryReader.Reading reading, string question)
        {
            var kept = reading.Filters.Where(p => p.Op == QueryOps.Is).ToList();
            for (var i = 0; i < reading.Filters.Count; i++)
            {
                var (candidate, op) = reading.Filters[i];
                if (op != QueryOps.Is || !Ordered(candidate.Column)) continue;
                var bound = BoundBefore(question, candidate.Words);
                if (bound == null) continue;
                if (Listed(question, candidate.Words, kept.SelectMany(k => new[] { k.Candidate.Words }.Concat((k.Candidate.Values ?? new List<object>()).Select(v => v?.ToString()))))) continue;
                reading.Filters[i] = (candidate, bound switch
                {
                    "after" => QueryOps.After,
                    "before" => QueryOps.Before,
                    "until" or "till" => QueryOps.UpTo,
                    _ => QueryOps.From,
                });
            }
        }

        /// <summary>"in 2007" names one year: the year is the value, so a year the column does not hold (offered to Jev only as a
        /// bound) is read as "is" unless a bound word sits right before it ("since 2007") (B2, hero run 2).</summary>
        internal static void FixYearIn(JevQueryReader.Reading reading, string question)
        {
            for (var i = 0; i < reading.Filters.Count; i++)
            {
                var (candidate, op) = reading.Filters[i];
                if (candidate.Kind != "year" || !QueryOps.Compares(op) || string.IsNullOrWhiteSpace(question) || string.IsNullOrWhiteSpace(candidate.Words)) continue;
                if (BoundBefore(question, candidate.Words) != null) continue;
                if (!System.Text.RegularExpressions.Regex.IsMatch(question, @"\bin\s+(?:the\s+)?(?:year\s+)?" + System.Text.RegularExpressions.Regex.Escape(candidate.Words.Trim()) + @"\b",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase)) continue;
                reading.Filters[i] = (candidate, QueryOps.Is);
            }
        }

        /// <summary>One plain line when a year the question names is not in the data, or null. <paramref name="held"/> are the
        /// years the column holds.</summary>
        internal static string NoDataForYear(JevQueryReader.Reading reading, IReadOnlyDictionary<string, object[]> held)
        {
            foreach (var (candidate, op) in reading.Filters)
            {
                if (candidate.Kind != "year" || op != QueryOps.Is || !held.TryGetValue(candidate.Column.Id, out var vals) || vals == null || vals.Length == 0) continue;
                var years = vals.Where(v => v != null && !(v is DBNull)).Select(v => Convert.ToInt64(v, System.Globalization.CultureInfo.InvariantCulture)).ToList();
                if (years.Count == 0) continue;
                var asked = candidate.Values.Select(v => Convert.ToInt64(v, System.Globalization.CultureInfo.InvariantCulture)).FirstOrDefault();
                if (years.Contains(asked)) continue;
                var edge = asked < years.Min() ? "The data starts in " + years.Min() : asked > years.Max() ? "The data ends in " + years.Max() : "The data has no rows for that year";
                return $"There is no data for {asked}. {edge}.";
            }
            return null;
        }

        /// <summary>The words right after <paramref name="words"/> join it to another kept value by "and", "or", "&amp;" or a comma
        /// ("2024 and 2025", "2024, 2025"): a list of values, not the start of a range.</summary>
        private static bool Listed(string question, string words, IEnumerable<string> keptValues)
        {
            if (string.IsNullOrWhiteSpace(question) || string.IsNullOrWhiteSpace(words)) return false;
            var others = new HashSet<string>(keptValues.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim()), StringComparer.OrdinalIgnoreCase);
            others.Remove(words.Trim());
            if (others.Count == 0) return false;
            var m = System.Text.RegularExpressions.Regex.Match(question,
                System.Text.RegularExpressions.Regex.Escape(words.Trim()) + @"\s*(?:,|&|\band\b|\bor\b)\s*(?:(?:and|or)\s+)?(\S+)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            return m.Success && others.Contains(m.Groups[1].Value.TrimEnd('.', ',', '?', '!', ';'));
        }

        /// <summary>G1 on the periods the engine read itself. A bound word right before the period words ("since last year", "from
        /// last quarter") makes a range from or to that period, and the engine read the one period alone: the periods after it (or
        /// before it) are left out and nothing on the screen says so (verifier round 5, R15, D7's own bound words). Only when one
        /// period was read; two read side by side ("from last year to this year") are both shown.</summary>
        internal static IEnumerable<string> BoundPeriod(string question, JevQueryReader.Reading reading)
        {
            var read = reading.Periods.Where(p => p.Span != "split" && !string.IsNullOrWhiteSpace(p.Words)).ToList();
            if (read.Count != 1) yield break;
            var bound = BoundBefore(question, read[0].Words);
            if (bound == null) yield break;
            yield return $"Read '{bound} {read[0].Words}' as {read[0].Words} only, not a range.";
        }

        /// <summary>A column whose values come in an order, so a bound means something: a date, a year, a month, a number.</summary>
        private static bool Ordered(QueryColumn c) => c.Kind == "date" || c.Kind == "whole" || c.Kind == "decimal" || IsCalendar(c);

        /// <summary>A period column of the calendar (a date, a year, a year and month, a quarter, a month of the year).</summary>
        internal static bool IsCalendar(QueryColumn c) => c.Kind == "date" || QueryPeriods.IsYear(c) || QueryPeriods.IsYearMonth(c)
            || QueryPeriods.IsYearQuarter(c) || QueryPeriods.IsQuarter(c) || QueryPeriods.IsMonthOfYear(c);

        /// <summary>The bound word ("from", "since", "after", "before", "until", "till") right before <paramref name="words"/> in the
        /// question, or null. "the" and "year" between them do not count.</summary>
        private static string BoundBefore(string question, string words)
        {
            if (string.IsNullOrWhiteSpace(question) || string.IsNullOrWhiteSpace(words)) return null;
            var m = System.Text.RegularExpressions.Regex.Match(question,
                @"\b(from|since|after|before|until|till)\s+(?:(?:the|in|of|year)\s+)*" + System.Text.RegularExpressions.Regex.Escape(words.Trim()) + @"\b",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value.ToLowerInvariant() : null;
        }

        // ---- G4 and G5, on the reading about to run (LocalEngine, before the check) ---------------------------------------

        /// <summary>G4. The question names a period by a qualifier the reading does not use (a fiscal year, a tax year, a school year,
        /// "fisacl 2025": PeriodQualifiers, a shape and not a list of words), and the model has no column for it: then "where Year
        /// is 2025" reads as that period and is not. The line says the model has no such period (or which column it would use), and
        /// the calendar reading stays one click away. Null when nothing is wrong. A fact of the model: the columns the reading uses.</summary>
        public static string Fiscal(string question, QuerySpec spec, QueryCatalog cat, JevQueryReader.Reading reading = null)
        {
            var named = PeriodQualifiers.Unexplained(question, spec, reading, cat);
            if (named == null) return null;
            var calendar = spec.GroupBy.Concat(spec.Filters.Select(f => f.Column)).Distinct().Where(IsCalendar).ToList();
            var have = PeriodQualifiers.ColumnsFor(named, spec, cat);
            var shown = string.Join(" and ", calendar.Select(cat.Shown));
            var said = "The question names '" + named.Text + "'";
            if (calendar.Count == 0)
                return said + (have.Count == 0 ? ", and this model has no such period" : ", and the model has " + cat.Shown(have[0]) + " for it") + ". This reading does not filter or split by that period.";
            if (have.Count > 0)
                return said + ", but this reading uses the calendar (" + shown + "). The model has " + cat.Shown(have[0]) + " for it.";
            return said + (named.Fiscal ? ", a fiscal period, and this model has no fiscal year, so " : ", a period this model has no column for, so ")
                + "this reading uses the calendar (" + shown + ").";
        }

        private static readonly System.Text.RegularExpressions.Regex ShareWords = new System.Text.RegularExpressions.Regex(
            @"\b(share|percent|percentage|proportion|fraction)\b|%", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        /// <summary>What the words say the share is OF ("orders" in "what percent of sales orders had a return"): the words after
        /// the share word and "of", up to the first verb or joining word, or null when the words name none.</summary>
        internal static string ShareOf(string question)
        {
            var q = question ?? "";
            var m = System.Text.RegularExpressions.Regex.Match(q, @"\b(?:share|percent|percentage|proportion|fraction)\s+(?:of|from)\s+(.+)|%\s+(?:of|from)\s+(.+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (!m.Success)
                m = System.Text.RegularExpressions.Regex.Match(q, @"\b(?:out\s+of|of)\s+((?:(?:the|our|all|my|their|its|total|gross|net)\s+)*[a-z][a-z ]*?)\s+(?:that|which|who|had|have|has)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (!m.Success) return null;
            var text = m.Groups.Cast<System.Text.RegularExpressions.Group>().Skip(1).First(g => g.Success).Value;
            var kept = new List<string>();
            foreach (var w in FuzzyWords.Words(text).Select(x => x.Word))
            {
                if (ShareStop.Contains(w) || (w.Length >= 5 && w.EndsWith("ed", StringComparison.Ordinal))) break;
                if (Determiner.Contains(w) && kept.Count == 0) continue;
                kept.Add(w);
            }
            return kept.Count == 0 ? null : string.Join(" ", kept);
        }

        private static readonly HashSet<string> Determiner = new HashSet<string>(StringComparer.Ordinal) { "the", "our", "all", "my", "their", "its", "these", "those", "every", "each" };
        private static readonly HashSet<string> ShareStop = new HashSet<string>(StringComparer.Ordinal)
        {
            "that", "which", "who", "whose", "with", "had", "have", "has", "were", "was", "are", "is", "did", "do", "does", "being", "in", "by", "for", "from", "per",
            "across", "on", "at", "last", "this", "during", "where", "came", "come", "coming", "made", "make", "and", "or", "but", "than", "as", "to", "compared", "vs", "versus",
        };
        private static readonly HashSet<string> Everything = new HashSet<string>(StringComparer.Ordinal) { "total", "totals", "whole", "everything", "it", "them", "all", "overall", "something", "anything" };

        /// <summary>Words that make a measure's part an AMOUNT (money or a total): the grain of a ratio of value.</summary>
        private static readonly HashSet<string> AmountWords = new HashSet<string>(StringComparer.Ordinal)
        {
            "amount", "amounts", "sales", "sale", "revenue", "revenues", "income", "profit", "profits", "cost", "costs", "price", "prices", "value", "values", "money",
            "spend", "spending", "turnover", "margin", "margins", "takings", "earnings", "expense", "expenses", "budget", "cash", "dollars", "total",
        };

        /// <summary>Words that make a part a COUNT of things: the grain of a ratio of counts.</summary>
        private static readonly HashSet<string> CountWords = new HashSet<string>(StringComparer.Ordinal)
        {
            "count", "counts", "number", "numbers", "orders", "order", "customers", "customer", "units", "unit", "quantity", "qty", "items", "item", "rows", "row", "lines",
            "transactions", "transaction", "distinct", "stores", "products", "visits", "tickets",
        };

        /// <summary>A ratio measure whose two parts are both amounts (returned money over sales money): provable from what its parts
        /// are named. A part the names do not classify is not an amount, so an unknown ratio is never called one.</summary>
        internal static bool RatioOfAmounts(QueryMeasure m)
        {
            if (m.RatioParts is not { } p) return false;
            bool Amount(string part)
            {
                var words = QuerySplitShortlist.NameWords(part);
                return words.Any(AmountWords.Contains) && !words.Any(CountWords.Contains);
            }
            return Amount(p.Numerator) && Amount(p.Denominator);
        }

        /// <summary>The question's own grain: a count of things (orders, customers) unless its head word names an amount (sales, revenue).</summary>
        private static bool AskedOfACount(string of)
        {
            var words = FuzzyWords.Words(of).Select(w => w.Word).ToList();
            if (words.Count == 0 || Everything.Contains(words[words.Count - 1])) return false;
            return !AmountWords.Contains(words[words.Count - 1]);
        }

        /// <summary>G5. A share or a percent OF A COUNT (orders, customers) is asked, and the reading shows a ratio of amounts: the
        /// number shown is money over money, not the share of things asked for. It compares the grain of the question's denominator
        /// with the grain of the ratio's parts (round 7). A percent with no base named ("return % by brand") is the model's own ratio
        /// and is left to the check. Null when nothing is wrong.</summary>
        public static string RatioShare(string question, StatedReading stated)
        {
            if (!ShareWords.IsMatch(question ?? "")) return null;
            var of = ShareOf(question);
            if (of == null || !AskedOfACount(of)) return null;
            var amounts = stated.Ratios.Where(RatioOfAmounts).ToList();
            if (amounts.Count == 0) return null;
            return "The question asks for a share of " + of + ", but " + string.Join(" and ", amounts.Select(m => m.Name))
                + " divides amounts of money, not " + of + ". Nothing in this model gives that share yet.";
        }

        private static string Part((ValueCandidate Candidate, string Op) p, QueryCatalog cat) =>
            $"{cat.Shown(p.Candidate.Column)} {JevQueryReader.OpWords(p.Op)} '{p.Candidate.Words}'";

        // ---- G2 ---------------------------------------------------------------------------------------------------

        /// <summary>The notes for filter values matched only through a slip: "Semanticus read 'coats' as the Region value
        /// Coast." Said the way a name's slip is, never taken quietly. A plural is not a slip.</summary>
        public static IEnumerable<string> SlipNotes(JevQueryReader.Reading reading, QueryCatalog cat)
        {
            foreach (var (candidate, op) in reading.Filters)
            {
                if (!candidate.Slip || !reading.Spec.Filters.Any(f => f.Column == candidate.Column && f.Op == op)) continue;
                var values = candidate.Values.Select(v => Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture)).Distinct().ToList();
                yield return $"Semanticus read '{candidate.Words}' as the {cat.Shown(candidate.Column)} value{(values.Count == 1 ? "" : "s")} {string.Join(" or ", values)}.";
            }
        }

        // ---- what the engine did with the calendar and with twin columns ------------------------------------------------

        private static IEnumerable<string> Calendar(string question, JevQueryReader.Reading reading, QueryCatalog cat, int? years)
        {
            var spec = reading.Spec;
            var ofYearAlone = spec.GroupBy.Concat(spec.Filters.Select(f => f.Column)).Where(c => QueryPeriods.OfYear(c) && !QueryPeriods.YearNamed(spec, c, cat) && years != 1).Distinct().ToList();
            foreach (var c in ofYearAlone.Where(spec.GroupBy.Contains))
                yield return $"Each {(QueryPeriods.IsQuarter(c) ? "quarter" : "month")} adds every year together.";
            var words = FuzzyWords.Words(question ?? "").ToList();
            // A value in a column two tables hold (Region in Store and Customer), when both tables are filtered.
            var twinFiltered = spec.Filters.Select(f => f.Column).Distinct()
                .Where(c => spec.Filters.Any(o => o.Column.Table != c.Table && string.Equals(o.Column.Name, c.Name, StringComparison.OrdinalIgnoreCase))).ToList();
            foreach (var (cand, op) in reading.Filters)
            {
                if (!spec.Filters.Any(f => f.Column == cand.Column && f.Op == op)) continue;
                if (ofYearAlone.Contains(cand.Column))
                {
                    yield return $"'{cand.Words}' adds every year's {cand.Words} together.";
                    continue;
                }
                if (twinFiltered.Contains(cand.Column) && !TableTerms(cand.Column.Table, cat).Any(t => words.Any(w => Same(t, w.Word))))
                {
                    var other = twinFiltered.First(c => c != cand.Column && string.Equals(c.Name, cand.Column.Name, StringComparison.OrdinalIgnoreCase));
                    yield return $"'{cand.Words}' narrows both {cat.Shown(cand.Column)} and {cat.Shown(other)}.";
                }
            }
        }

        private static IEnumerable<string> TableTerms(string table, QueryCatalog cat) =>
            QuerySplitShortlist.NameWords(table).Concat((cat.TableSynonyms.TryGetValue(table, out var t) ? t : Array.Empty<string>()).SelectMany(QuerySplitShortlist.NameWords));

        /// <summary>The typed word is the known one: the same, a plural, or its -ly form.</summary>
        private static bool Same(string known, string typed) =>
            known == typed || typed == known + "s" || typed == known + "es" || known == typed + "s" || typed == known + "ly"
            || (known.EndsWith("y", StringComparison.Ordinal) && (typed == known.Substring(0, known.Length - 1) + "ies" || typed == known.Substring(0, known.Length - 1) + "ily"));
    }
}
